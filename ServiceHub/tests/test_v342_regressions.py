"""
tests.test_v342_regressions
===========================
Regresioni jedinični i integracioni testovi za WIS v3.4.2 / ServiceHub v3.6.2:
- IMP-19: DriverPoolManager (stat size+mtime_ns fast-skip bez SHA-256, smanjen opseg locka,
  čišćenje wis-pool-extract-* bez praćenja simlinkova, keširan get_stats() < 50 ms)
- IMP-20 & IMP-23: FleetStore (reset završnih polja stare sesije pri re-run-u uz očuvanje fajlova na disku,
  brisanje privremenih DESKTOP-* siročića po SN/IP u Fazi 1/2, record_logs preko _resolve_device_key_locked,
  45s deduplikacija u record_lifecycle_complete bez duplih report_*.json sa kraćim trajanjem)
- IMP-25: Inkrementalni thread-safe keš za compute_fleet_benchmark_summary (/api/benchmark/summary)
- IMP-26: get_device_history i /api/devices/by-serial/<sn>/history čitaju kanonske WIS JSON kartone
  (sortirano najnovije prvo, bez path traversala i bez curenja ključeva)
- IMP-27: Timezone konfiguracija TZ=Europe/Belgrade u Dockerfile i docker-compose.yml
"""

import json
import os
import shutil
import tarfile
import tempfile
import threading
import time
import unittest
import urllib.request
from http.server import ThreadingHTTPServer
from unittest.mock import patch

import servishub.benchmark as bm_mod
import servishub.drivers.pool as pool_mod
from servishub.benchmark import compute_fleet_benchmark_summary
from servishub.config import HubConfig
from servishub.drivers.pool import DriverPoolManager
from servishub.handlers import ServisHubHandler
from servishub.reports import get_device_history, save_device_report
from servishub.telemetry import FleetStore
from tests.test_driver_pool import _build_tar_from_dir, _write_driver_folder


class TestV342Regressions(unittest.TestCase):
    def setUp(self):
        self.tmp_dir = tempfile.mkdtemp(prefix="wis_v342_test_")

    def tearDown(self):
        shutil.rmtree(self.tmp_dir, ignore_errors=True)

    def test_01_imp19_scan_and_ingest_skips_sha256_on_unchanged_size_and_mtime_ns(self):
        """IMP-19: Drugi prolaz scan_and_ingest_archives preskače _file_sha256 kada su size_bytes i mtime_ns nepromenjeni."""
        pool_dir = os.path.join(self.tmp_dir, "pool")
        archives_dir = os.path.join(self.tmp_dir, "archives")
        src_dir = os.path.join(self.tmp_dir, "drv_src")
        os.makedirs(archives_dir, exist_ok=True)

        _write_driver_folder(
            src_dir,
            "wlan",
            inf_name="intel_wlan.inf",
            ver="23.40.0.4",
            date_str="07/10/2025",
            hwid=r"PCI\VEN_8086&DEV_51F1&SUBSYS_00948086",
        )
        tar_path = _build_tar_from_dir(src_dir, os.path.join(archives_dir, "Lenovo_ThinkPad_E16_Gen_2.tar"))

        pool = DriverPoolManager(pool_dir=pool_dir)

        # 1. Prvi prolaz ingestuje arhivu i zapisuje size_bytes + mtime_ns
        res1 = pool.scan_and_ingest_archives(archives_dir)
        self.assertEqual(res1["ingested"], 1)
        self.assertEqual(res1["skipped"], 0)

        idx = pool._ensure_index_loaded()
        rec = idx["ingested_archives"]["Lenovo_ThinkPad_E16_Gen_2.tar"]
        st = os.stat(tar_path)
        self.assertEqual(rec["size_bytes"], int(st.st_size))
        self.assertEqual(rec["mtime_ns"], int(st.st_mtime_ns))

        # 2. Drugi prolaz nad neizmenjenim fajlom NE SME pozvati _file_sha256
        with patch.object(pool_mod, "_file_sha256", wraps=pool_mod._file_sha256) as spy_sha:
            res2 = pool.scan_and_ingest_archives(archives_dir)
            self.assertEqual(res2["ingested"], 0)
            self.assertEqual(res2["skipped"], 1)
            self.assertEqual(spy_sha.call_count, 0)

        # 3. Simulacija starog zapisa bez mtime_ns: prvi naredni prolaz dopisuje mtime_ns, a sledeći preskače SHA-256
        with pool.lock:
            idx_legacy = pool._ensure_index_loaded()
            idx_legacy["ingested_archives"]["Lenovo_ThinkPad_E16_Gen_2.tar"].pop("mtime_ns", None)
            pool._save_index_locked(idx_legacy)

        with patch.object(pool_mod, "_file_sha256", wraps=pool_mod._file_sha256) as spy_sha_backfill:
            res3 = pool.scan_and_ingest_archives(archives_dir)
            self.assertEqual(res3["ingested"], 0)
            self.assertEqual(res3["skipped"], 1)
            self.assertEqual(spy_sha_backfill.call_count, 1)

        idx_after = pool._ensure_index_loaded()
        self.assertEqual(
            idx_after["ingested_archives"]["Lenovo_ThinkPad_E16_Gen_2.tar"].get("mtime_ns"),
            int(os.stat(tar_path).st_mtime_ns),
        )

        with patch.object(pool_mod, "_file_sha256", wraps=pool_mod._file_sha256) as spy_sha_after:
            res4 = pool.scan_and_ingest_archives(archives_dir)
            self.assertEqual(res4["ingested"], 0)
            self.assertEqual(res4["skipped"], 1)
            self.assertEqual(spy_sha_after.call_count, 0)

    def test_02_imp19_temp_cleanup_no_symlink_follow_and_fast_cached_get_stats(self):
        """IMP-19: Čišćenje wis-pool-extract-* bez praćenja simlinkova i keširan get_stats() < 50 ms pod zauzetim lockom."""
        fake_tmp_root = os.path.join(self.tmp_dir, "fake_tmp")
        os.makedirs(fake_tmp_root, exist_ok=True)

        stale_real_dir = os.path.join(fake_tmp_root, "wis-pool-extract-stale123")
        os.makedirs(stale_real_dir, exist_ok=True)
        with open(os.path.join(stale_real_dir, "dummy.inf"), "w", encoding="utf-8") as f:
            f.write("stale")

        protected_target = os.path.join(self.tmp_dir, "protected_target")
        os.makedirs(protected_target, exist_ok=True)
        protected_file = os.path.join(protected_target, "keep_me.txt")
        with open(protected_file, "w", encoding="utf-8") as f:
            f.write("important data")

        symlink_path = os.path.join(fake_tmp_root, "wis-pool-extract-symlink999")
        os.symlink(protected_target, symlink_path)

        cleaned = DriverPoolManager.cleanup_stale_temp_dirs(temp_root=fake_tmp_root)
        self.assertEqual(cleaned, 1)
        self.assertFalse(os.path.exists(stale_real_dir))
        # Symlink meta MORA ostati netaknuta!
        self.assertTrue(os.path.isfile(protected_file))

        # Provera keširanog get_stats() dok druga nit drži pool.lock
        pool = DriverPoolManager(pool_dir=os.path.join(self.tmp_dir, "pool_stats"))
        src_dir = os.path.join(self.tmp_dir, "drv_stats_src")
        _write_driver_folder(src_dir, "net", inf_name="net_fast.inf")
        pool.ingest_directory(src_dir, source="harvest", model="HP_EliteBook_840_G10", manufacturer="HP")

        initial_stats = pool.get_stats()
        self.assertEqual(initial_stats["unique_packages"], 1)
        self.assertEqual(initial_stats["models_count"], 1)

        lock_acquired = threading.Event()
        release_lock = threading.Event()

        def _hold_pool_lock():
            with pool.lock:
                lock_acquired.set()
                release_lock.wait(timeout=2.0)

        holder = threading.Thread(target=_hold_pool_lock, daemon=True)
        holder.start()
        self.assertTrue(lock_acquired.wait(timeout=1.0))
        try:
            t0 = time.perf_counter()
            cached_stats = pool.get_stats()
            elapsed_ms = (time.perf_counter() - t0) * 1000.0
            self.assertLess(elapsed_ms, 50.0, f"get_stats() je trajao {elapsed_ms:.2f} ms (očekivano < 50 ms)")
            self.assertEqual(cached_stats["unique_packages"], 1)
        finally:
            release_lock.set()
            holder.join(timeout=1.0)

    def test_03_imp20_rerun_resets_terminal_session_cleans_orphan_and_resolves_logs(self):
        """IMP-20: Re-run istog SN resetuje terminalna polja, briše DESKTOP-* siroče iz Faze 1/2 i usmerava logove na kanonski ključ."""
        reports_dir = os.path.join(self.tmp_dir, "reports_imp20")
        live_logs_dir = os.path.join(self.tmp_dir, "live_imp20")
        os.makedirs(reports_dir, exist_ok=True)
        os.makedirs(live_logs_dir, exist_ok=True)

        store = FleetStore(live_logs_dir=live_logs_dir, reports_dir=reports_dir)

        # 1. Završena prethodna sesija za SN 5CG3271T6P (sa starim izveštajem na disku i 98°C)
        store.record_heartbeat({
            "computer_name": "TK-LAPTOP-5CG3271T6P",
            "serial_number": "5CG3271T6P",
            "phase": 5,
            "hw": {"manufacturer": "HP", "model": "HP EliteBook 840 G10", "serial": "5CG3271T6P"},
        }, "192.168.180.50")
        store.record_lifecycle_complete({
            "computer_name": "TK-LAPTOP-5CG3271T6P",
            "serial_number": "5CG3271T6P",
            "status": "NEEDS_ATTENTION",
            "diagnostics_status": "THERMAL_CRITICAL",
            "max_cpu_temp_c": 98.0,
            "cooling_verified": False,
            "manual_check_required": True,
            "duration_seconds": 3100,
            "duration_formatted": "51m 40s",
            "apps": {"Chrome": "OK", "Office": "ERROR"},
            "hwtest": {"summary": {"overall": "FAIL", "fail": 1}},
            "errors": ["Kritično pregrevanje 98C"],
        }, "192.168.180.50")

        old_report_file = store.fleet_devices["TK-LAPTOP-5CG3271T6P"].get("latest_report_file")
        self.assertTrue(old_report_file and os.path.isfile(old_report_file))
        sum_before = store.get_devices_summary()["TK-LAPTOP-5CG3271T6P"]
        self.assertEqual(sum_before["status_label"], "NEEDS_ATTENTION")
        self.assertEqual(sum_before["handover_status"], "NEEDS_SERVICE")

        # 2. Nova instalacija istog laptopa počinje sa privremenim DESKTOP-9FLD1E2 u Fazi 1 (pre čitanja SN-a na istom IP-u)
        store.record_heartbeat({
            "computer_name": "DESKTOP-9FLD1E2",
            "phase": 1,
            "worker_progress": 5,
        }, "192.168.180.50")
        self.assertIn("DESKTOP-9FLD1E2", store.fleet_devices)

        # 3. U Fazi 1/2 stiže heartbeat sa serijskim brojem 5CG3271T6P (pod novim privremenim imenom DESKTOP-CBHEN1N)
        resolved_key = store.record_heartbeat({
            "computer_name": "DESKTOP-CBHEN1N",
            "serial_number": "5CG3271T6P",
            "phase": 1,
            "worker_progress": 15,
        }, "192.168.180.50")
        self.assertEqual(resolved_key, "TK-LAPTOP-5CG3271T6P")

        # Siroče DESKTOP-9FLD1E2 sa istog IP-a u Fazi 1 mora biti očišćeno
        self.assertNotIn("DESKTOP-9FLD1E2", store.fleet_devices)
        self.assertNotIn("DESKTOP-CBHEN1N", store.fleet_devices)

        # Stara terminalna polja u memoriji moraju biti resetovana, dok fajl na disku ostaje netaknut!
        self.assertTrue(os.path.isfile(old_report_file))
        active_rec = store.fleet_devices["TK-LAPTOP-5CG3271T6P"]
        for cleared_field in (
            "status", "handover_status", "duration_seconds", "duration_formatted",
            "latest_report", "latest_report_file", "hwtest", "apps", "errors",
            "completed_at", "diagnostics_status", "cooling_verified", "manual_check_required",
        ):
            self.assertNotIn(cleared_field, active_rec, f"Polje {cleared_field} nije resetovano pri re-run startu!")

        sum_active = store.get_devices_summary()["TK-LAPTOP-5CG3271T6P"]
        self.assertEqual(sum_active["status_label"], "Aktivno")
        self.assertEqual(sum_active["handover_status"], "IN_PROGRESS")
        self.assertEqual(sum_active["phase"], 1)

        # 4. Logovi poslati pod DESKTOP-CBHEN1N moraju otići pod kanonski TK-LAPTOP-5CG3271T6P
        store.record_logs("DESKTOP-CBHEN1N", ["[STEP] Faza 2: Windows Update u toku..."])
        logs = store.get_device_logs("TK-LAPTOP-5CG3271T6P")
        self.assertEqual(len(logs), 1)
        self.assertIn("Faza 2: Windows Update", logs[0])

    def test_04_imp23_lifecycle_complete_45s_deduplication_preserves_richer_report_and_longer_duration(self):
        """IMP-23: Dupli COMPLETED u roku od 45s ne kreira duple report_*.json fajlove i čuva duži duration_seconds + hwtest/benchmark."""
        reports_dir = os.path.join(self.tmp_dir, "reports_imp23")
        os.makedirs(reports_dir, exist_ok=True)
        store = FleetStore(reports_dir=reports_dir)

        # Prvi, puni COMPLETED izveštaj (npr. 2832s sa hwtest, benchmark i apps)
        dev_id = store.record_lifecycle_complete({
            "computer_name": "TK-LAPTOP-PF57XGY9",
            "serial_number": "PF57XGY9",
            "status": "COMPLETED",
            "duration_seconds": 2832,
            "duration_formatted": "47m 12s",
            "apps": {"Chrome": "OK", "Office": "OK"},
            "hwtest": {"summary": {"overall": "PASS", "pass": 14, "fail": 0}},
            "benchmark": {"total_phase_duration_sec": 2832, "cache_hits": 10},
        }, "192.168.170.120")
        self.assertEqual(dev_id, "TK-LAPTOP-PF57XGY9")

        files_after_first = [fn for fn in os.listdir(reports_dir) if fn.endswith(".json")]
        self.assertEqual(len(files_after_first), 1)

        # Drugi COMPLETED 2s kasnije sa kraćim duration_seconds (2457s) i bez hwtest bloka
        store.record_lifecycle_complete({
            "computer_name": "TK-LAPTOP-PF57XGY9",
            "serial_number": "PF57XGY9",
            "status": "COMPLETED",
            "duration_seconds": 2457,
            "duration_formatted": "40m 57s",
            "cooling_verified": True,
            "diagnostics_status": "OK",
        }, "192.168.170.120")

        files_after_second = [fn for fn in os.listdir(reports_dir) if fn.endswith(".json")]
        self.assertEqual(len(files_after_second), 1, "Ne sme se kreirati dupli report_*.json u okviru 45s!")

        rec = store.fleet_devices["TK-LAPTOP-PF57XGY9"]
        self.assertEqual(rec["duration_seconds"], 2832)
        self.assertEqual(rec["duration_formatted"], "47m 12s")
        self.assertIn("hwtest", rec["latest_report"])
        self.assertIn("benchmark", rec["latest_report"])
        self.assertTrue(rec["cooling_verified"])

    def test_05_imp25_incremental_benchmark_cache_avoids_reparsing_unchanged_files(self):
        """IMP-25: compute_fleet_benchmark_summary kešira parsirane JSON fajlove po (fname, size, mtime_ns) i finalni summary."""
        rep_dir = os.path.join(self.tmp_dir, "bm_cache_reports")
        os.makedirs(rep_dir, exist_ok=True)

        for i in range(1, 4):
            with open(os.path.join(rep_dir, f"20261121_12000{i}_TK-LAPTOP-BM0{i}.json"), "w", encoding="utf-8") as f:
                json.dump({
                    "device_id": f"TK-LAPTOP-BM0{i}",
                    "computer_name": f"TK-LAPTOP-BM0{i}",
                    "serial_number": f"SNBM0{i}",
                    "status": "COMPLETED",
                    "handover_ready": True,
                    "duration_seconds": 1000 + i * 100,
                    "benchmark": {
                        "phase_durations_sec": {"Phase1": 10, "Phase2": 400, "Phase3": 50, "Phase4": 400, "Phase5": 100, "Phase6": 40},
                        "total_phase_duration_sec": 1000 + i * 100,
                        "cache_hits": 5,
                        "cache_misses": 1,
                    },
                    "timestamp": f"2026-11-21T12:0{i}:00",
                }, f)

        # 1. Prvi poziv parsira sva 3 fajla
        sum1 = compute_fleet_benchmark_summary(fleet_store=None, reports_dir=rep_dir)
        self.assertEqual(sum1["total_installations"], 3)

        # 2. Drugi poziv nad nepromenjenim direktorijumom ne sme uopšte zvati json.load (0 parsiranja!)
        with patch.object(bm_mod.json, "load", wraps=bm_mod.json.load) as spy_load:
            t0 = time.perf_counter()
            sum2 = compute_fleet_benchmark_summary(fleet_store=None, reports_dir=rep_dir)
            elapsed_ms = (time.perf_counter() - t0) * 1000.0
            self.assertEqual(spy_load.call_count, 0)
            self.assertLess(elapsed_ms, 50.0)
            self.assertEqual(sum2["total_installations"], 3)

        # 3. Dodavanje 4. fajla parsira SAMO 1 novi fajl (postojeća 3 ostaju u kešu)
        f4_path = os.path.join(rep_dir, "20261121_120004_TK-LAPTOP-BM04.json")
        with open(f4_path, "w", encoding="utf-8") as f:
            json.dump({
                "device_id": "TK-LAPTOP-BM04",
                "computer_name": "TK-LAPTOP-BM04",
                "serial_number": "SNBM04",
                "status": "COMPLETED",
                "handover_ready": True,
                "duration_seconds": 1500,
                "timestamp": "2026-11-21T12:04:00",
            }, f)

        with patch.object(bm_mod.json, "load", wraps=bm_mod.json.load) as spy_load_inc:
            sum3 = compute_fleet_benchmark_summary(fleet_store=None, reports_dir=rep_dir)
            self.assertEqual(spy_load_inc.call_count, 1)
            self.assertEqual(sum3["total_installations"], 4)

        # 4. Brisanje 4. fajla izbacuje ga iz keša i vraća total_installations na 3 bez parsiranja preostala 3
        os.remove(f4_path)
        with patch.object(bm_mod.json, "load", wraps=bm_mod.json.load) as spy_load_del:
            sum4 = compute_fleet_benchmark_summary(fleet_store=None, reports_dir=rep_dir)
            self.assertEqual(spy_load_del.call_count, 0)
            self.assertEqual(sum4["total_installations"], 3)

    def test_06_imp26_device_history_reads_canonical_wis_reports_by_serial_and_tag(self):
        """IMP-26: get_device_history i /api/devices/by-serial/<sn>/history čitaju kanonske WIS JSON kartone iz korena reports_dir."""
        cfg = HubConfig(
            host="127.0.0.1",
            port=0,
            base_dir=self.tmp_dir,
            storage_dir=os.path.join(self.tmp_dir, "storage"),
            logs_dir=os.path.join(self.tmp_dir, "logs"),
        )
        rep_dir = cfg.reports_dir
        os.makedirs(rep_dir, exist_ok=True)

        # Stariji izveštaj (19.11.)
        rep_old = {
            "device_id": "TK-LAPTOP-PF57XGY9",
            "computer_name": "DESKTOP-OLD01",
            "serial_number": "PF57XGY9",
            "status": "NEEDS_ATTENTION",
            "duration_seconds": 2100,
            "duration_formatted": "35m 00s",
            "timestamp": "2026-11-19T10:15:00",
            "hw": {"manufacturer": "LENOVO", "model": "ThinkPad E16 Gen 2", "serial": "PF57XGY9"},
            "apps": {"Chrome": "OK"},
        }
        # Noviji izveštaj (21.11.) sa sirovim ključem u office_license_detail koji mora biti maskiran
        rep_new = {
            "device_id": "TK-LAPTOP-PF57XGY9",
            "computer_name": "DESKTOP-1213TCC",
            "serial_number": "PF57XGY9",
            "status": "COMPLETED",
            "handover_ready": True,
            "diagnostics_status": "OK",
            "cooling_verified": True,
            "manual_check_required": False,
            "duration_seconds": 2457,
            "duration_formatted": "40m 57s",
            "timestamp": "2026-11-21T14:55:42",
            "hw": {"manufacturer": "LENOVO", "model": "ThinkPad E16 Gen 2", "serial": "PF57XGY9"},
            "apps": {"Chrome": "OK", "Office": "OK"},
            "hwtest": {"summary": {"overall": "PASS"}},
            "windows_activated": True,
            "office_installed": True,
            "office_activated": False,
            "office_license_detail": "Key ABCDE-12345-FGHIJ-67890-ZXCVB failed with 0x4004F00C",
        }

        with open(os.path.join(rep_dir, "report_PF57XGY9_20261119_101500.json"), "w", encoding="utf-8") as f:
            json.dump(rep_old, f)
        with open(os.path.join(rep_dir, "20261121_145542_TK-LAPTOP-PF57XGY9.json"), "w", encoding="utf-8") as f:
            json.dump(rep_new, f)

        # 1. Direktan poziv po serijskom broju
        hist = get_device_history("PF57XGY9", rep_dir)
        self.assertTrue(hist["is_registered"])
        self.assertEqual(hist["serial_number"], "PF57XGY9")
        self.assertEqual(hist["manufacturer"], "LENOVO")
        self.assertEqual(hist["model"], "ThinkPad E16 Gen 2")
        self.assertEqual(hist["total_interventions"], 2)
        self.assertEqual(len(hist["interventions"]), 2)
        # Sortirano najnovije prvo (2026-11-21 pre 2026-11-19)
        self.assertIn("2026-11-21", hist["interventions"][0]["date"])
        self.assertIn("2026-11-19", hist["interventions"][1]["date"])
        self.assertEqual(hist["first_seen"], "2026-11-19T10:15:00")
        self.assertEqual(hist["last_seen"], "2026-11-21T14:55:42")
        # Provera da tajni product ključ nije iscurio u summary
        self.assertNotIn("ABCDE-12345-FGHIJ-67890-ZXCVB", json.dumps(hist))

        # 2. Provera preko HTTP rute GET /api/devices/by-serial/PF57XGY9/history
        store = FleetStore(live_logs_dir=cfg.live_logs_dir, reports_dir=rep_dir)
        prev_cfg = getattr(ServisHubHandler, "config", None)
        prev_store = getattr(ServisHubHandler, "fleet_store", None)
        ServisHubHandler.config = cfg
        ServisHubHandler.fleet_store = store
        httpd = ThreadingHTTPServer(("127.0.0.1", 0), ServisHubHandler)
        port = httpd.server_address[1]
        threading.Thread(target=httpd.serve_forever, daemon=True).start()
        try:
            with urllib.request.urlopen(f"http://127.0.0.1:{port}/api/devices/by-serial/PF57XGY9/history", timeout=5) as resp:
                self.assertEqual(resp.status, 200)
                http_hist = json.loads(resp.read().decode("utf-8"))
            self.assertTrue(http_hist["is_registered"])
            self.assertEqual(http_hist["serial_number"], "PF57XGY9")
            self.assertEqual(http_hist["total_interventions"], 2)
            self.assertIn("2026-11-21", http_hist["interventions"][0]["date"])
        finally:
            httpd.shutdown()
            httpd.server_close()
            ServisHubHandler.config = prev_cfg
            ServisHubHandler.fleet_store = prev_store

    def test_07_imp27_docker_timezone_configuration(self):
        """IMP-27: Dockerfile i docker-compose.yml imaju podešen TZ=Europe/Belgrade i tzdata."""
        servicehub_root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
        dockerfile_path = os.path.join(servicehub_root, "Dockerfile")
        compose_path = os.path.join(servicehub_root, "docker-compose.yml")

        with open(dockerfile_path, "r", encoding="utf-8") as df:
            dockerfile_txt = df.read()
        self.assertIn("tzdata", dockerfile_txt)
        self.assertIn("ENV TZ=Europe/Belgrade", dockerfile_txt)
        self.assertIn("ln -snf /usr/share/zoneinfo/$TZ /etc/localtime && echo $TZ > /etc/timezone", dockerfile_txt)

        with open(compose_path, "r", encoding="utf-8") as cf:
            compose_txt = cf.read()
        self.assertIn("- TZ=${TZ:-Europe/Belgrade}", compose_txt)


if __name__ == "__main__":
    unittest.main()
