"""
tests.test_v350_regressions
===========================
Regresioni jedinični i integracioni testovi za WIS v3.5.0 / ServiceHub v3.7.0:
- IMP-63 & IMP-68: LRU/TTL kvote za bundles_dir i DRIVERS_CACHE_DIR (bez symlinkova/traversal-a i bez
  brisanja aktivnih .tmp fajlova), automatsko bezbedno brisanje sirovog .tar/.tar.gz/.tgz iz Archives/
  nakon SHA-256 verifikovanog ingesta i save_index(), pauziranje ms_catalog preuzimanja kada je
  disk_free < 20 GB, i osvežavanje stale_oem kataloga starijih od 72h.
- IMP-53, IMP-62 & 2A: .tar.gz/.tgz podrška u scan_and_ingest_archives i notify-harvest (sa blokadom
  symlink/hardlink/path-traversal i size limita), POST /api/drivers/pool/delta-check sa client-token
  zaštitom, i optimizovani single-pass build_bundle_tar + cache hit header.
- V350-07: Izuzimanje ključa 'License' (case-insensitive) za dict i list oblike apps iz NEEDS_SERVICE.
- IMP-66, IMP-69 & IMP-56: install_session_id izolacija sesija za isti SN, filtriranje zastarelih
  log grešaka od prethodnih dana za čist COMPLETED izveštaj, i učitavanje repa današnjih live logova
  pri startu servisa.
- V350-04: Prikaz hw.display u JSON i Markdown servisnom kartonu pored CPU/RAM/Disk.
"""

from collections import namedtuple
from datetime import datetime, timedelta
import io
import json
import os
import shutil
import tarfile
import tempfile
import threading
import time
import unittest
import urllib.error
import urllib.request
from http.server import ThreadingHTTPServer
from unittest.mock import MagicMock, patch

from servishub.api.updates import cache_kb_from_ms_catalog, enforce_drivers_cache_quota
from servishub.config import HubConfig, active_config, enforce_directory_lru_quota
from servishub.drivers.oem.dell import DellOEMProvider
from servishub.drivers.oem.hp import HpOEMProvider
from servishub.drivers.oem.lenovo import LenovoOEMProvider
from servishub.drivers.pool import DriverPoolManager, _extract_archive_safely
from servishub.handlers import ServisHubHandler
from servishub.reports import (
    compute_handover_status,
    generate_markdown_report,
    save_device_report,
)
from servishub.telemetry import FleetStore

_DiskUsageTuple = namedtuple("usage", ["total", "used", "free"])


def _write_driver_folder(
    root_dir: str,
    subdir: str,
    inf_name: str = "drv.inf",
    ver: str = "1.0.0.1",
    date_str: str = "01/15/2025",
    hwid: str = r"PCI\VEN_8086&DEV_51F1",
) -> str:
    pkg_dir = os.path.join(root_dir, subdir)
    os.makedirs(pkg_dir, exist_ok=True)
    inf_content = (
        "[Version]\n"
        'Signature="$WINDOWS NT$"\n'
        "Class=Net\n"
        'Provider="TestVendor"\n'
        f"DriverVer={date_str},{ver}\n"
        f"CatalogFile={os.path.splitext(inf_name)[0]}.cat\n"
        "[Manufacturer]\n"
        "%Mfg%=Models,NTamd64\n"
        "[Models.NTamd64]\n"
        f'"Test Device" = InstallSec, {hwid}\n'
        "[Strings]\n"
        'Mfg="TestVendor"\n'
    )
    with open(os.path.join(pkg_dir, inf_name), "w", encoding="utf-8") as f:
        f.write(inf_content)
    with open(os.path.join(pkg_dir, f"{os.path.splitext(inf_name)[0]}.cat"), "wb") as f:
        f.write(b"CATDATA123456")
    with open(os.path.join(pkg_dir, f"{os.path.splitext(inf_name)[0]}.sys"), "wb") as f:
        f.write(b"SYSDATA123456" * 64)
    return pkg_dir


def _build_tar_from_dir(src_dir: str, out_path: str, mode: str = "w") -> str:
    os.makedirs(os.path.dirname(out_path), exist_ok=True)
    with tarfile.open(out_path, mode) as tf:
        for rdir, _, files in os.walk(src_dir):
            for fn in sorted(files):
                fp = os.path.join(rdir, fn)
                rel = os.path.relpath(fp, src_dir).replace("\\", "/")
                tf.add(fp, arcname=rel, recursive=False)
    return out_path


class TestV350Regressions(unittest.TestCase):
    def setUp(self):
        self.tmp_dir = tempfile.mkdtemp(prefix="wis_v350_test_")
        self._orig_pool_instances = dict(ServisHubHandler._driver_pool_instances)

    def tearDown(self):
        ServisHubHandler._driver_pool_instances.clear()
        ServisHubHandler._driver_pool_instances.update(self._orig_pool_instances)
        shutil.rmtree(self.tmp_dir, ignore_errors=True)

    def _start_test_server(self, cfg: HubConfig, fleet: FleetStore):
        class _TestHandler(ServisHubHandler):
            config = cfg
            fleet_store = fleet
            driver_cache_mgr = None
            ms_catalog_scraper = None
            driver_pool_mgr = None

        server = ThreadingHTTPServer(("127.0.0.1", 0), _TestHandler)
        port = server.server_address[1]
        t = threading.Thread(target=server.serve_forever, daemon=True)
        t.start()
        return server, port

    def test_01_imp63_bundles_lru_quota_ignores_symlinks_and_active_tmp(self):
        """IMP-63: LRU/TTL evikcija u bundles_dir čuva aktivne .tmp fajlove i nikad ne prati symlinkove."""
        pool_dir = os.path.join(self.tmp_dir, "pool_lru")
        pool = DriverPoolManager(pool_dir=pool_dir, max_bundles_gb=0.00001, bundle_ttl_hours=24.0)

        outside_secret = os.path.join(self.tmp_dir, "outside_secret.txt")
        with open(outside_secret, "w", encoding="utf-8") as f:
            f.write("DO_NOT_DELETE")

        old_tar = os.path.join(pool.bundles_dir, "aaaa1111.tar")
        old_json = os.path.join(pool.bundles_dir, "aaaa1111.json")
        new_tar = os.path.join(pool.bundles_dir, "bbbb2222.tar")
        new_json = os.path.join(pool.bundles_dir, "bbbb2222.json")
        active_tmp = os.path.join(pool.bundles_dir, "cccc3333.tar.abc123.tmp")
        sym_tar = os.path.join(pool.bundles_dir, "sym_outside.tar")

        with open(old_tar, "wb") as f:
            f.write(b"X" * 8192)
        with open(old_json, "w", encoding="utf-8") as f:
            f.write('{"sha256": "aaa"}')
        with open(new_tar, "wb") as f:
            f.write(b"Y" * 8192)
        with open(new_json, "w", encoding="utf-8") as f:
            f.write('{"sha256": "bbb"}')
        with open(active_tmp, "wb") as f:
            f.write(b"T" * 16384)
        os.symlink(outside_secret, sym_tar)

        now = time.time()
        os.utime(old_tar, (now - 3600, now - 3600))
        os.utime(old_json, (now - 3600, now - 3600))
        os.utime(new_tar, (now - 10, now - 10))
        os.utime(new_json, (now - 10, now - 10))
        os.utime(active_tmp, (now - 7200, now - 7200))

        # Kvota 10 KB: briše old_tar (+ old_json), zadržava new_tar (< 10 KB) i OBAVEZNO zadržava active_tmp i outside_secret
        res = enforce_directory_lru_quota(
            pool.bundles_dir,
            max_bytes=10000,
            ttl_seconds=86400.0,
            paired_extensions={".tar": (".json",)},
            now=now,
        )
        self.assertGreaterEqual(res["removed_files"], 2)
        self.assertFalse(os.path.exists(old_tar))
        self.assertFalse(os.path.exists(old_json))
        self.assertTrue(os.path.exists(new_tar))
        self.assertTrue(os.path.exists(active_tmp))
        self.assertTrue(os.path.exists(outside_secret))
        self.assertTrue(os.path.islink(sym_tar))

    def test_02_imp68_archive_auto_delete_after_verified_ingest_and_tar_gz(self):
        """IMP-68 & 2A: .tar.gz ingest u Pool/packages/ i automatsko bezbedno brisanje sirove arhive nakon save_index()."""
        pool_dir = os.path.join(self.tmp_dir, "pool_arch")
        archives_dir = os.path.join(self.tmp_dir, "archives")
        src_dir = os.path.join(self.tmp_dir, "src_tgz")
        os.makedirs(archives_dir, exist_ok=True)

        _write_driver_folder(src_dir, "lan", inf_name="realtek_lan.inf", ver="10.68.1.1", hwid=r"PCI\VEN_10EC&DEV_8168")
        tgz_path = _build_tar_from_dir(src_dir, os.path.join(archives_dir, "Lenovo_ThinkPad_T14.tar.gz"), mode="w:gz")

        outside_tar = os.path.join(self.tmp_dir, "outside.tar")
        _build_tar_from_dir(src_dir, outside_tar, mode="w")
        sym_archive = os.path.join(archives_dir, "Symlink_Model.tar")
        os.symlink(outside_tar, sym_archive)

        pool = DriverPoolManager(pool_dir=pool_dir, delete_ingested_archives=True)
        res = pool.scan_and_ingest_archives(archives_dir)

        self.assertEqual(res["ingested"], 1)
        self.assertEqual(res["deleted_archives"], 1)
        self.assertFalse(os.path.exists(tgz_path))
        self.assertTrue(os.path.exists(outside_tar))
        self.assertTrue(pool.has_model_manifest("Lenovo_ThinkPad_T14"))
        idx = pool._ensure_index_loaded()
        self.assertIn("Lenovo_ThinkPad_T14.tar.gz", idx["ingested_archives"])

    def test_03_imp68_archive_security_blocks_traversal_symlink_and_size_limit(self):
        """2A: _extract_archive_safely blokira path traversal (..), symlink/hardlink i size limit u .tar.gz."""
        evil_tgz = os.path.join(self.tmp_dir, "evil.tar.gz")
        dest = os.path.join(self.tmp_dir, "extract_out")

        # 1. Symlink unutar .tar.gz
        with tarfile.open(evil_tgz, "w:gz") as tf:
            ti = tarfile.TarInfo("bad_link.inf")
            ti.type = tarfile.SYMTYPE
            ti.linkname = "/etc/passwd"
            tf.addfile(ti)
        with self.assertRaises(ValueError):
            _extract_archive_safely(evil_tgz, dest)

        # 2. Path traversal (..) unutar .tar.gz
        evil_trav = os.path.join(self.tmp_dir, "evil_trav.tar.gz")
        with tarfile.open(evil_trav, "w:gz") as tf:
            data = b"evil"
            ti = tarfile.TarInfo("../escape.inf")
            ti.size = len(data)
            tf.addfile(ti, io.BytesIO(data))
        with self.assertRaises(ValueError):
            _extract_archive_safely(evil_trav, dest)

        # 3. Prekoračenje maksimalne raspakovane veličine
        big_tgz = os.path.join(self.tmp_dir, "big.tar.gz")
        with tarfile.open(big_tgz, "w:gz") as tf:
            data = b"A" * 4096
            ti = tarfile.TarInfo("big.sys")
            ti.size = len(data)
            tf.addfile(ti, io.BytesIO(data))
        with self.assertRaises(ValueError):
            _extract_archive_safely(big_tgz, dest, max_unpacked_bytes=1024)

    def test_04_imp68_drivers_cache_lru_oem_providers_and_ms_catalog_low_disk_pause(self):
        """IMP-68: LRU kvota za DRIVERS_CACHE_DIR + OEM provajdere i pauza ms_catalog kada je disk_free < 20 GB."""
        dcache = os.path.join(self.tmp_dir, "drivers_cache")
        os.makedirs(dcache, exist_ok=True)
        old_cab = os.path.join(dcache, "old_drv.cab")
        new_cab = os.path.join(dcache, "new_drv.cab")
        with open(old_cab, "wb") as f:
            f.write(b"1" * 4096)
        with open(new_cab, "wb") as f:
            f.write(b"2" * 4096)
        now = time.time()
        os.utime(old_cab, (now - 5000, now - 5000))
        os.utime(new_cab, (now - 10, now - 10))

        q_res = enforce_drivers_cache_quota(dcache, max_gb=0.000005)  # ~5.3 KB
        self.assertEqual(q_res["removed_files"], 1)
        self.assertFalse(os.path.exists(old_cab))
        self.assertTrue(os.path.exists(new_cab))

        # OEM provajderi (Dell, HP, Lenovo) izlažu enforce_cache_quota
        for cls, sub in ((DellOEMProvider, "dell"), (HpOEMProvider, "hp"), (LenovoOEMProvider, "lenovo")):
            prov = cls(cache_dir=os.path.join(dcache, "oem", sub), max_cache_gb=25.0)
            self.assertEqual(prov.max_drivers_cache_gb, 25.0)
            self.assertEqual(prov.enforce_cache_quota()["removed_files"], 0)

        # Pauza ms_catalog kada je disk_free < 20 GB
        updates_dir = os.path.join(self.tmp_dir, "updates")
        os.makedirs(updates_dir, exist_ok=True)
        low_du = _DiskUsageTuple(total=100 * (1024 ** 3), used=85 * (1024 ** 3), free=15 * (1024 ** 3))
        with patch("servishub.config.shutil.disk_usage", return_value=low_du):
            res_low = cache_kb_from_ms_catalog("KB5043080", directory=updates_dir)
        self.assertEqual(res_low["status"], "paused_low_disk")
        self.assertLess(res_low["free_gb"], 20.0)

    def test_05_imp62_refresh_stale_oem_catalogs_72h(self):
        """IMP-62: refresh_stale_oem_catalogs osvežava kataloge starije od 72h."""
        pool_dir = os.path.join(self.tmp_dir, "pool_oem")
        mock_cat = MagicMock()
        mock_cat.get_catalog_status.return_value = {
            "dell": {"synced": True, "stale": False, "age_hours": 80.5},
            "hp": {"synced": True, "stale": False, "age_hours": 12.0},
            "lenovo": {"synced": False, "stale": True, "age_hours": None},
        }
        mock_cat.sync_catalog.return_value = {"status": "ok"}

        pool = DriverPoolManager(pool_dir=pool_dir, catalog_mgr=mock_cat, oem_catalog_stale_hours=72.0)
        res = pool.refresh_stale_oem_catalogs()

        self.assertEqual(res["vendors"]["dell"]["status"], "refreshed")
        self.assertEqual(res["vendors"]["hp"]["status"], "fresh")
        self.assertEqual(res["vendors"]["lenovo"]["status"], "refreshed")
        self.assertEqual(mock_cat.sync_catalog.call_count, 2)

    def test_06_2a_http_delta_check_notify_harvest_tgz_and_bundle_cache_hit(self):
        """2A & IMP-53: POST /api/drivers/pool/delta-check, notify-harvest (.tar.gz) i bundle single-pass/cache-hit."""
        cfg = HubConfig(
            base_dir=self.tmp_dir,
            storage_dir=os.path.join(self.tmp_dir, "storage"),
            logs_dir=os.path.join(self.tmp_dir, "logs"),
            client_token="tok-v350",
            delete_ingested_archives=True,
        )
        fleet = FleetStore(live_logs_dir=cfg.live_logs_dir, reports_dir=cfg.reports_dir)
        server, port = self._start_test_server(cfg, fleet)
        try:
            # 1. Pripremi .tar.gz u model_packs_dir i pozovi notify-harvest
            os.makedirs(cfg.model_packs_dir, exist_ok=True)
            src_dir = os.path.join(self.tmp_dir, "src_http")
            _write_driver_folder(src_dir, "audio", inf_name="realtek_audio.inf", ver="6.0.9500.1", hwid=r"HDAUDIO\FUNC_01&VEN_10EC&DEV_0257")
            tgz_file = _build_tar_from_dir(src_dir, os.path.join(cfg.model_packs_dir, "HP_ProBook_450_G9.tar.gz"), mode="w:gz")

            notify_body = json.dumps({"model": "HP_ProBook_450_G9", "sync": True}).encode("utf-8")
            n_req = urllib.request.Request(
                f"http://127.0.0.1:{port}/api/drivers/pool/notify-harvest",
                data=notify_body,
                method="POST",
                headers={"Content-Type": "application/json", "X-ServisHub-Client-Token": "tok-v350"},
            )
            with urllib.request.urlopen(n_req, timeout=10) as n_resp:
                self.assertEqual(n_resp.status, 200)
                n_json = json.loads(n_resp.read().decode("utf-8"))
            self.assertEqual(n_json["ingested"], 1)
            self.assertEqual(n_json["deleted_archives"], 1)
            self.assertFalse(os.path.exists(tgz_file))

            # 2. delta-check bez tokena -> 401
            delta_payload = {
                "model": "HP ProBook 450 G9",
                "os_build": "26100",
                "packages": [
                    {"inf_name": "realtek_audio.inf", "driver_ver": "6.0.9500.1", "hwids": [r"HDAUDIO\FUNC_01&VEN_10EC&DEV_0257"]},
                    {"inf_name": "missing_gpu.inf", "driver_ver": "31.0.101.5000", "hwids": [r"PCI\VEN_8086&DEV_A7A0"]},
                ],
            }
            delta_body = json.dumps(delta_payload).encode("utf-8")
            bad_req = urllib.request.Request(
                f"http://127.0.0.1:{port}/api/drivers/pool/delta-check",
                data=delta_body,
                method="POST",
                headers={"Content-Type": "application/json"},
            )
            with self.assertRaises(urllib.error.HTTPError) as ctx401:
                urllib.request.urlopen(bad_req, timeout=5)
            self.assertEqual(ctx401.exception.code, 401)
            ctx401.exception.close()

            # 3. delta-check sa tokenom -> 200
            ok_req = urllib.request.Request(
                f"http://127.0.0.1:{port}/api/drivers/pool/delta-check",
                data=delta_body,
                method="POST",
                headers={"Content-Type": "application/json", "X-ServisHub-Client-Token": "tok-v350"},
            )
            with urllib.request.urlopen(ok_req, timeout=5) as d_resp:
                self.assertEqual(d_resp.status, 200)
                d_json = json.loads(d_resp.read().decode("utf-8"))
            self.assertEqual(d_json["total_checked"], 2)
            self.assertEqual(d_json["already_in_pool"], 1)
            self.assertEqual(d_json["missing_infs"], ["missing_gpu.inf"])
            self.assertTrue(d_json["harvest_needed"])

            # 4. bundle prvi put (cache_hit=false) i drugi put (cache_hit=true)
            b_body = json.dumps({"model": "HP_ProBook_450_G9"}).encode("utf-8")
            for expected_hit in ("false", "true"):
                b_req = urllib.request.Request(
                    f"http://127.0.0.1:{port}/api/drivers/pool/bundle",
                    data=b_body,
                    method="POST",
                    headers={"Content-Type": "application/json"},
                )
                with urllib.request.urlopen(b_req, timeout=10) as b_resp:
                    self.assertEqual(b_resp.status, 200)
                    self.assertEqual(b_resp.headers.get("X-Driver-Pool-Cache-Hit"), expected_hit)
                    self.assertGreater(len(b_resp.read()), 0)
        finally:
            server.shutdown()
            server.server_close()

    def test_07_v350_07_license_excluded_from_needs_service(self):
        """V350-07: Neaktivan Windows/Office ('License' u dict ili list obliku apps) ne spušta status na NEEDS_SERVICE."""
        base_rec = {
            "status": "COMPLETED",
            "diagnostics_status": "OK",
            "cooling_verified": True,
            "manual_check_required": False,
            "hwtest": {"summary": {"overall": "PASS", "pass": 16, "fail": 0, "skip": 0}},
            "latest_report": {"status": "COMPLETED"},
        }
        rec_dict_lic = dict(base_rec, apps={"Chrome": "OK", "License": "ERROR", "license": "FAIL"})
        self.assertEqual(compute_handover_status(rec_dict_lic), "READY")

        rec_list_lic = dict(
            base_rec,
            apps=[
                {"id": "Chrome", "name": "Google Chrome", "status": "OK"},
                {"id": "License", "name": "OEM Licenca", "status": "ERROR"},
            ],
        )
        self.assertEqual(compute_handover_status(rec_list_lic), "READY")

        rec_real_fail = dict(base_rec, apps={"Chrome": "ERROR", "License": "ERROR"})
        self.assertEqual(compute_handover_status(rec_real_fail), "NEEDS_SERVICE")

    def test_08_imp66_stale_log_errors_filtered_for_clean_completed_report(self):
        """IMP-66: Zastarele [ERROR] linije od prethodnih dana ne izazivaju lažni NEEDS_SERVICE za čist COMPLETED izveštaj."""
        today_str = datetime.now().strftime("%Y-%m-%d")
        yesterday_str = (datetime.now() - timedelta(days=1)).strftime("%Y-%m-%d")

        stale_rec = {
            "status": "COMPLETED",
            "timestamp": f"{today_str}T14:00:00",
            "diagnostics_status": "OK",
            "cooling_verified": True,
            "manual_check_required": False,
            "apps": {"Chrome": "OK", "License": "WARN"},
            "hwtest": {"summary": {"overall": "PASS", "pass": 16, "fail": 0, "skip": 0}},
            "errors": [f"{yesterday_str} 16:45:00 [ERROR] Stara greska iz prethodne instalacije"],
            "latest_report": {"status": "COMPLETED"},
        }
        self.assertEqual(compute_handover_status(stale_rec), "READY")

        today_err_rec = dict(
            stale_rec,
            errors=[f"{today_str} 13:50:00 [ERROR] Stvarna greska iz tekuce instalacije"],
        )
        self.assertEqual(compute_handover_status(today_err_rec), "NEEDS_SERVICE")

    def test_09_imp69_and_imp56_install_session_isolation_and_live_log_tail(self):
        """IMP-69 & IMP-56: Promena install_session_id za isti SN čisti stare logove/aliase, a restart učitava rep live logova."""
        live_dir = os.path.join(self.tmp_dir, "live_logs")
        rep_dir = os.path.join(self.tmp_dir, "reports")
        os.makedirs(live_dir, exist_ok=True)
        os.makedirs(rep_dir, exist_ok=True)

        fleet = FleetStore(live_logs_dir=live_dir, reports_dir=rep_dir)
        fleet.record_heartbeat(
            {
                "computer_name": "DESKTOP-OLD111",
                "serial_number": "SN99887766",
                "install_session_id": "sess-1",
                "phase": 2,
            },
            "192.168.170.50",
        )
        fleet.record_logs(
            "DESKTOP-OLD111",
            ["[INFO] Stara sesija log 1", "[ERROR] Stara sesija greska"],
            serial_number="SN99887766",
            install_session_id="sess-1",
        )
        self.assertEqual(len(fleet.get_device_logs("DESKTOP-OLD111")), 2)

        # Nova instalacija za isti SN sa novim install_session_id -> čisti stare in-memory logove i stare aliase
        dev_key = fleet.record_heartbeat(
            {
                "computer_name": "DESKTOP-NEW222",
                "serial_number": "SN99887766",
                "install_session_id": "sess-2",
                "phase": 1,
            },
            "192.168.170.51",
        )
        self.assertEqual(fleet.get_device_logs(dev_key), [])
        self.assertNotIn("DESKTOP-OLD111", fleet._alias_index)

        # Novi logovi za sess-2 upisuju se na disk
        today_lines = [f"[INFO] Nova sesija linija {i}" for i in range(70)]
        fleet.record_logs("DESKTOP-NEW222", today_lines, serial_number="SN99887766", install_session_id="sess-2")

        # Simulacija restarta kontejnera: novi FleetStore učitava poslednjih 50 linija današnjeg live loga
        fleet_restarted = FleetStore(live_logs_dir=live_dir, reports_dir=rep_dir)
        restored_logs = fleet_restarted.get_device_logs(dev_key, limit=300)
        self.assertEqual(len(restored_logs), 50)
        self.assertEqual(restored_logs[-1], "[INFO] Nova sesija linija 69")

    def test_10_v350_04_hw_display_in_json_and_markdown_report(self):
        """V350-04: hw.display se čuva u JSON i prikazuje u Markdown servisnom kartonu pored CPU/RAM/Disk."""
        rep_dir = os.path.join(self.tmp_dir, "reports_disp")
        payload = {
            "device_id": "TK-LAPTOP-DISP01",
            "computer_name": "TK-LAPTOP-DISP01",
            "serial_number": "DISP01SN",
            "status": "COMPLETED",
            "install_session_id": "sess-disp-1",
            "hw": {
                "manufacturer": "Microsoft Corporation",
                "model": "Surface Laptop 5",
                "cpu": "Intel Core i5-1235U",
                "ram": "16 GB",
                "disk": "256 GB NVMe SSD",
                "display": '13.5" QHD+ IPS @ 60 Hz',
            },
            "apps": {"Chrome": "OK"},
        }
        json_fp, md_fp, _, _ = save_device_report("TK-LAPTOP-DISP01", payload, rep_dir)
        with open(json_fp, "r", encoding="utf-8") as jf:
            saved_json = json.load(jf)
        with open(md_fp, "r", encoding="utf-8") as mf:
            saved_md = mf.read()

        self.assertEqual(saved_json["hw"]["display"], '13.5" QHD+ IPS @ 60 Hz')
        self.assertIn('**Ekran (Display):** 13.5" QHD+ IPS @ 60 Hz', saved_md)
        md_direct = generate_markdown_report(payload)
        self.assertIn("Ekran (Display):", md_direct)


if __name__ == "__main__":
    unittest.main()
