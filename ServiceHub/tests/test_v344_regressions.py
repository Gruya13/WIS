"""
tests.test_v344_regressions
===========================
Regresioni jedinični i integracioni testovi za WIS v3.4.4 / ServiceHub v3.6.4 (WIS-TASK-269 + WIS-TASK-270):
- IMP-36: /api/status disk_usage (free_bytes, total_bytes, used_bytes, used_pct, free_gb,
  low_disk_warning < 10 GB, critical_disk_warning < 5 GB), LOW_DISK_WARNING banner na dashboard-u,
  automatska retencija starih logova bez diranja aktivnih uređaja iz 24h i bez praćenja symlinkova,
  čišćenje bajatih .tmp fajlova starijih od 1h, i rate-limitovanje [Errno 28] ENOSPC poruka (1x/60s).
- IMP-39: record_logs i POST /api/log prihvataju opcioni serial_number i osvežavaju last_seen
  za neterminalne uređaje.
- IMP-41 (3A): compute_handover_status svrstava KVAR_POTVRDJEN (*KVAR*) u FAILED (nikad READY),
  a _has_skipped_interactive_hwtests obara na NEEDS_SERVICE samo za izveštaje >= 3.4.3 (ili uz
  eksplicitan manual_skip_reason), čuvajući istorijske v3.4.0–v3.4.2 izveštaje na READY_WITH_NOTE.
- IMP-43: compute_handover_status nikad ne vraća READY kada rec['apps'] ima ERROR/FAIL, kada
  rec['errors'] nije prazan, ili kada terminalni zapis (status_label == 'Zavrseno', phase == 6)
  nema snimljen latest_report (None).
- IMP-45: is_placeholder_hw_value prepoznaje zh-CN/en/sr UI placeholdere ('加载中...', '读取中...',
  '正在加载配置...', 'loading...', 'detecting...', '...'), _resolve_device_key_locked nikada ne
  upisuje placeholder u _serial_index i čisti zatrovani _alias_index kada stigne validan SN.
- IMP-47: record_heartbeat resetuje terminalno stanje za svaki aktivan heartbeat sa
  incoming_phase < 6 i incoming_lbl != 'Zavrseno' (npr. naknadni ulazak u P4 ili P5 posle Faze 6).
- IMP-50: get_devices_summary prikazuje 'Restart u toku' za UEFI BIOS restart u Phase 5 (do 720s).
- IMP-52: record_heartbeat u phase >= 5 sprečava da dolazni apps sa isključivo WAIT/SKIPPED
  vrednostima pregazi postojeće OK/WARN/ERROR statuse iz Faze 4.
"""

from collections import namedtuple
import errno
import io
import json
import os
import shutil
import tempfile
import threading
import time
import unittest
import urllib.request
from http.server import ThreadingHTTPServer
from unittest.mock import patch

from servishub.benchmark import compute_fleet_benchmark_summary
from servishub.config import HubConfig, active_config
from servishub.handlers import ServisHubHandler
from servishub.reports import (
    _has_skipped_interactive_hwtests,
    cleanup_stale_tmp_files,
    compute_handover_status,
    generate_markdown_report,
    log_enospc_rate_limited,
)
from servishub.telemetry import FleetStore, is_placeholder_hw_value
from servishub.web.dashboard import _load_dashboard_html

_DiskUsageTuple = namedtuple("usage", ["total", "used", "free"])


class TestV344Regressions(unittest.TestCase):
    def setUp(self):
        self.tmp_dir = tempfile.mkdtemp(prefix="wis_v344_test_")
        self.orig_base = active_config.base_dir
        active_config.base_dir = self.tmp_dir
        self.config = HubConfig(
            host="127.0.0.1",
            port=0,
            base_dir=self.tmp_dir,
            storage_dir=os.path.join(self.tmp_dir, "storage"),
            logs_dir=os.path.join(self.tmp_dir, "logs"),
        )
        os.makedirs(self.config.reports_dir, exist_ok=True)
        os.makedirs(self.config.live_logs_dir, exist_ok=True)
        self.fleet_store = FleetStore(
            live_logs_dir=self.config.live_logs_dir,
            reports_dir=self.config.reports_dir,
        )

        self._prev_cfg = getattr(ServisHubHandler, "config", None)
        self._prev_store = getattr(ServisHubHandler, "fleet_store", None)
        ServisHubHandler.config = self.config
        ServisHubHandler.fleet_store = self.fleet_store
        ServisHubHandler.start_time = time.time() - 120

        self.httpd = ThreadingHTTPServer(("127.0.0.1", 0), ServisHubHandler)
        self.port = self.httpd.server_address[1]
        self.base_url = f"http://127.0.0.1:{self.port}"
        self.server_thread = threading.Thread(target=self.httpd.serve_forever, daemon=True)
        self.server_thread.start()

    def tearDown(self):
        self.httpd.shutdown()
        self.httpd.server_close()
        ServisHubHandler.config = self._prev_cfg
        ServisHubHandler.fleet_store = self._prev_store
        active_config.base_dir = self.orig_base
        shutil.rmtree(self.tmp_dir, ignore_errors=True)

    def _post_json(self, path: str, payload: dict):
        req = urllib.request.Request(
            f"{self.base_url}{path}",
            data=json.dumps(payload).encode("utf-8"),
            headers={"Content-Type": "application/json"},
            method="POST",
        )
        with urllib.request.urlopen(req, timeout=5) as resp:
            return resp.status, json.loads(resp.read().decode("utf-8"))

    def _get_json(self, path: str):
        with urllib.request.urlopen(f"{self.base_url}{path}", timeout=5) as resp:
            return resp.status, json.loads(resp.read().decode("utf-8"))

    def test_01_imp36_status_disk_usage_dashboard_banner_and_enospc_protection(self):
        """IMP-36: /api/status disk_usage (low < 10 GB, critical < 5 GB), HTML banner, retencija, .tmp i ENOSPC rate-limit."""
        gb = 1024 ** 3

        # 1. Zdrav disk (50 GB slobodno od 100 GB)
        with patch("servishub.handlers.shutil.disk_usage", return_value=_DiskUsageTuple(100 * gb, 50 * gb, 50 * gb)):
            code, data = self._get_json("/api/status")
        self.assertEqual(code, 200)
        du = data["disk_usage"]
        self.assertEqual(du["total_bytes"], 100 * gb)
        self.assertEqual(du["used_bytes"], 50 * gb)
        self.assertEqual(du["free_bytes"], 50 * gb)
        self.assertEqual(du["used_pct"], 50.0)
        self.assertEqual(du["free_gb"], 50.0)
        self.assertFalse(du["low_disk_warning"])
        self.assertFalse(du["critical_disk_warning"])

        # 2. Low-disk upozorenje (8 GB slobodno < 10 GB)
        with patch("servishub.handlers.shutil.disk_usage", return_value=_DiskUsageTuple(100 * gb, 92 * gb, 8 * gb)):
            _, data_low = self._get_json("/api/status")
        self.assertTrue(data_low["disk_usage"]["low_disk_warning"])
        self.assertFalse(data_low["disk_usage"]["critical_disk_warning"])
        self.assertIn("LOW_DISK_WARNING", data_low["disk_usage"]["status_message"])

        # 3. Kritično upozorenje (2.5 GB slobodno < 5 GB)
        with patch("servishub.handlers.shutil.disk_usage", return_value=_DiskUsageTuple(100 * gb, int(97.5 * gb), int(2.5 * gb))):
            _, data_crit = self._get_json("/api/status")
        self.assertTrue(data_crit["disk_usage"]["low_disk_warning"])
        self.assertTrue(data_crit["disk_usage"]["critical_disk_warning"])
        self.assertIn("KRITIČNO", data_crit["disk_usage"]["status_message"])

        # 4. Dashboard HTML sadrži LOW_DISK_WARNING alert banner i JS funkciju
        dashboard_html = _load_dashboard_html()
        self.assertIn('id="low-disk-warning-banner"', dashboard_html)
        self.assertIn("LOW_DISK_WARNING", dashboard_html)
        self.assertIn("checkLowDiskStatusAlert", dashboard_html)

        # 5. Čišćenje bajatih .tmp fajlova (> 1h) i zaštita od brisanja preko symlinkova
        now = time.time()
        stale_tmp = os.path.join(self.config.reports_dir, "report_OLD.json.tmp")
        fresh_tmp = os.path.join(self.config.reports_dir, "report_FRESH.json.tmp")
        outside_file = os.path.join(self.tmp_dir, "outside_secret.txt.tmp")
        symlink_tmp = os.path.join(self.config.reports_dir, "evil_link.json.tmp")

        for fp in (stale_tmp, fresh_tmp, outside_file):
            with open(fp, "w", encoding="utf-8") as f:
                f.write("temp")
        os.utime(stale_tmp, (now - 4000, now - 4000))
        os.utime(outside_file, (now - 4000, now - 4000))
        os.symlink(outside_file, symlink_tmp)

        removed_cnt = cleanup_stale_tmp_files(self.config.reports_dir, max_age_sec=3600.0, now=now)
        self.assertEqual(removed_cnt, 1)
        self.assertFalse(os.path.exists(stale_tmp))
        self.assertTrue(os.path.exists(fresh_tmp))
        self.assertTrue(os.path.exists(outside_file), "Symlink meta van direktorijuma ne sme biti obrisana!")

        # 6. Automatska retencija logova kad je slobodno < 15 GB čuva aktivne uređaje iz zadnja 24h
        self.fleet_store.record_heartbeat({"computer_name": "ACTIVE-PC-01", "phase": 4}, "192.168.170.50")
        active_log = os.path.join(self.config.live_logs_dir, "servis_deploy_ACTIVE-PC-01_20261120.log")
        old_inactive_log = os.path.join(self.config.live_logs_dir, "servis_deploy_INACTIVE-OLD_20261115.log")
        recent_log = os.path.join(self.config.live_logs_dir, "servis_deploy_RECENT-24H_20261123.log")
        for fp in (active_log, old_inactive_log, recent_log):
            with open(fp, "w", encoding="utf-8") as f:
                f.write("log line\n")
        # Čak i ako active_log ima stariji mtime (npr. 3 dana), uređaj ACTIVE-PC-01 je aktivan u zadnja 24h!
        os.utime(active_log, (now - 3 * 86400, now - 3 * 86400))
        os.utime(old_inactive_log, (now - 3 * 86400, now - 3 * 86400))
        os.utime(recent_log, (now - 3600, now - 3600))

        with patch("servishub.telemetry.shutil.disk_usage", return_value=_DiskUsageTuple(100 * gb, 90 * gb, 10 * gb)):
            prune_stats = self.fleet_store._prune_old_disk_logs(now=now, force=True)
        self.assertGreaterEqual(prune_stats["removed_logs"], 1)
        self.assertFalse(os.path.exists(old_inactive_log))
        self.assertTrue(os.path.exists(active_log), "Log aktivnog uređaja iz zadnja 24h mora biti sačuvan!")
        self.assertTrue(os.path.exists(recent_log), "Log mlađi od 24h mora biti sačuvan!")

        # 7. Rate-limitovanje [Errno 28] ENOSPC poruka na max 1x u 60s
        enospc_err = OSError(errno.ENOSPC, "No space left on device")
        buf = io.StringIO()
        with patch("sys.stderr", buf):
            first_logged = log_enospc_rate_limited("Test ENOSPC", enospc_err, interval_sec=60.0, now=1000.0)
            second_logged = log_enospc_rate_limited("Test ENOSPC", enospc_err, interval_sec=60.0, now=1015.0)
            third_logged = log_enospc_rate_limited("Test ENOSPC", enospc_err, interval_sec=60.0, now=1065.0)
        self.assertTrue(first_logged)
        self.assertFalse(second_logged)
        self.assertTrue(third_logged)

    def test_02_imp39_record_logs_refreshes_last_seen_and_resolves_serial_number(self):
        """IMP-39: POST /api/log prihvata serial_number za razrešavanje uređaja i osvežava last_seen neterminalnog uređaja."""
        self.fleet_store.record_heartbeat(
            {
                "computer_name": "TK-LAPTOP-PF2BF29H",
                "serial_number": "PF2BF29H",
                "phase": 2,
                "status_label": "Aktivno",
            },
            "192.168.180.161",
        )
        # Simuliraj da je heartbeat kasnio 120s (uređaj bi bez logova bio Offline > 20s)
        stale_ts = time.time() - 120.0
        self.fleet_store.fleet_devices["TK-LAPTOP-PF2BF29H"]["last_seen"] = stale_ts

        # Stiže /api/log pod privremenim Windows imenom W-AAD-007326285 ali sa serial_number PF2BF29H
        code, resp = self._post_json(
            "/api/log",
            {
                "computer_name": "W-AAD-007326285",
                "serial_number": "PF2BF29H",
                "lines": ["[STEP] Faza 2: Instalacija KB5134133 u toku..."],
            },
        )
        self.assertEqual(code, 200)
        self.assertEqual(resp["appended"], 1)

        rec = self.fleet_store.fleet_devices["TK-LAPTOP-PF2BF29H"]
        self.assertGreater(rec["last_seen"], stale_ts + 60.0)
        summary = self.fleet_store.get_devices_summary()["TK-LAPTOP-PF2BF29H"]
        self.assertTrue(summary["is_online"])
        self.assertEqual(summary["status_label"], "Aktivno")
        logs = self.fleet_store.get_device_logs("TK-LAPTOP-PF2BF29H")
        self.assertEqual(len(logs), 1)

        # Za terminalni uređaj (Faza 6 / Zavrseno), record_logs NE menja last_seen
        self.fleet_store.record_lifecycle_complete(
            {
                "computer_name": "TK-LAPTOP-PF2BF29H",
                "serial_number": "PF2BF29H",
                "status": "COMPLETED",
                "duration_seconds": 1500,
            },
            "192.168.180.161",
        )
        term_ts = time.time() - 300.0
        self.fleet_store.fleet_devices["TK-LAPTOP-PF2BF29H"]["last_seen"] = term_ts
        self.fleet_store.record_logs("TK-LAPTOP-PF2BF29H", ["[INFO] Zakasneli log"], serial_number="PF2BF29H")
        self.assertEqual(self.fleet_store.fleet_devices["TK-LAPTOP-PF2BF29H"]["last_seen"], term_ts)

    def test_03_imp41_3a_kvar_potvrdjen_failed_and_version_aware_skipped_interactive_hwtests(self):
        """IMP-41 (3A): KVAR_POTVRDJEN -> FAILED; v3.4.0-v3.4.2 bez manual_skip_reason -> READY_WITH_NOTE; v3.4.3+ -> NEEDS_SERVICE."""
        # 1. LENOVO_82JF_PF2RQ70G sa status="KVAR_POTVRDJEN" nikada ne sme biti READY
        rec_kvar = {
            "device_id": "LENOVO_82JF_PF2RQ70G",
            "computer_name": "LENOVO_82JF_PF2RQ70G",
            "status": "KVAR_POTVRDJEN",
            "status_label": "Zavrseno",
        }
        self.assertEqual(compute_handover_status(rec_kvar), "FAILED")

        # 2. Istorijski v3.4.2 (i v3.2.2 / v3.4.1) izveštaj sa preskočenim interaktivnim testovima -> READY_WITH_NOTE (ok=True u benchmarku)
        rec_v342 = {
            "device_id": "TK-LAPTOP-5CG8353TLF",
            "computer_name": "DESKTOP-OQVRSHT",
            "status": "COMPLETED",
            "diagnostics_status": "OK",
            "cooling_verified": True,
            "manual_check_required": False,
            "duration_seconds": 2100,
            "benchmark": {"phase_durations_sec": {"Phase1": 10, "Phase2": 600, "Phase4": 900}, "total_phase_duration_sec": 2100},
            "hwtest": {
                "app_version": "3.4.2",
                "summary": {"overall": "PASS_WITH_SKIP", "pass": 14, "fail": 0, "skip": 2, "na": 0},
                "tests": [
                    {"id": "auto_cpu", "automated": True, "status": "PASS"},
                    {"id": "display", "automated": False, "status": "SKIP", "reason": "Preskočeno"},
                ],
            },
        }
        self.assertFalse(_has_skipped_interactive_hwtests(rec_v342["hwtest"], report_data=rec_v342))
        self.assertEqual(compute_handover_status(rec_v342), "READY_WITH_NOTE")
        md_v342 = generate_markdown_report(rec_v342)
        self.assertIn("- **Ukupan Ishod:** ✅ PASS", md_v342)

        # 3. Istorijski v3.4.2 izveštaj ALI sa eksplicitnim manual_skip_reason -> NEEDS_SERVICE
        rec_v342_manual = json.loads(json.dumps(rec_v342))
        rec_v342_manual["hwtest"]["manual_skip_reason"] = "Tehničar ručno prekinuo zbog neispravnog ekrana"
        self.assertTrue(_has_skipped_interactive_hwtests(rec_v342_manual["hwtest"], report_data=rec_v342_manual))
        self.assertEqual(compute_handover_status(rec_v342_manual), "NEEDS_SERVICE")

        # 4. Novi v3.4.3 / v3.4.4 izveštaj sa preskočenim interaktivnim testom -> NEEDS_SERVICE
        rec_v343 = json.loads(json.dumps(rec_v342))
        rec_v343["device_id"] = "TK-LAPTOP-V343-SKIP"
        rec_v343["hwtest"]["app_version"] = "3.4.3"
        self.assertTrue(_has_skipped_interactive_hwtests(rec_v343["hwtest"], report_data=rec_v343))
        self.assertEqual(compute_handover_status(rec_v343), "NEEDS_SERVICE")

        # 5. Provera u compute_fleet_benchmark_summary: v3.4.2 ulazi u v3_modern.successful, dok KVAR_POTVRDJEN i v3.4.3 SKIP ne ulaze
        rep_dir = os.path.join(self.tmp_dir, "bm_imp41")
        os.makedirs(rep_dir, exist_ok=True)
        for fn, obj in (
            ("r_kvar.json", rec_kvar),
            ("r_v342.json", rec_v342),
            ("r_v343.json", rec_v343),
        ):
            with open(os.path.join(rep_dir, fn), "w", encoding="utf-8") as f:
                json.dump(obj, f)

        bm = compute_fleet_benchmark_summary(fleet_store=None, reports_dir=rep_dir)
        self.assertEqual(bm["total_installations"], 3)
        self.assertEqual(bm["successful_installations"], 1)
        self.assertEqual(bm["cohorts"]["v3_modern"]["total"], 2)
        self.assertEqual(bm["cohorts"]["v3_modern"]["successful"], 1)

    def test_04_imp43_handover_blocks_ready_on_app_error_or_missing_latest_report(self):
        """IMP-43: compute_handover_status vraća NEEDS_SERVICE kada apps ima ERROR/FAIL, errors nije prazan ili fali latest_report u Fazi 6."""
        # 1. Slučaj DESKTOP-H9L1RNR: Acrobat=ERROR, Office=ERROR
        rec_app_err = {
            "device_id": "DESKTOP-H9L1RNR",
            "computer_name": "DESKTOP-H9L1RNR",
            "status": "COMPLETED",
            "status_label": "Zavrseno",
            "phase": 6,
            "diagnostics_status": "OK",
            "cooling_verified": True,
            "manual_check_required": False,
            "apps": {
                "DotNet": "OK",
                "Chrome": "OK",
                "WinRAR": "OK",
                "Acrobat": "ERROR",
                "Office": "ERROR",
            },
            "latest_report": {"status": "COMPLETED"},
        }
        self.assertEqual(compute_handover_status(rec_app_err), "NEEDS_SERVICE")

        # 2. Slučaj kada su sve aplikacije OK, ali rec['errors'] nije prazan
        rec_with_errors = {
            "device_id": "TK-LAPTOP-ERR01",
            "status": "COMPLETED",
            "diagnostics_status": "OK",
            "cooling_verified": True,
            "manual_check_required": False,
            "apps": {"Chrome": "OK", "Office": "OK"},
            "errors": ["[ERROR] Reality check Faze 4 nije prošao"],
        }
        self.assertEqual(compute_handover_status(rec_with_errors), "NEEDS_SERVICE")

        # 3. Terminalni uređaj (status_label == 'Zavrseno', phase == 6) kome je POST /report pao sa HTTP 500 (latest_report=None)
        rec_missing_report = {
            "device_id": "TK-LAPTOP-BK348VV24313GT",
            "computer_name": "DESKTOP-H9L1RNR",
            "status_label": "Zavrseno",
            "phase": 6,
            "diagnostics_status": "OK",
            "cooling_verified": True,
            "manual_check_required": False,
            "apps": {"DotNet": "OK", "Chrome": "OK", "WinRAR": "OK", "Acrobat": "OK", "Office": "OK"},
            "latest_report": None,
        }
        self.assertEqual(compute_handover_status(rec_missing_report), "NEEDS_SERVICE")

        # 4. Kada latest_report stigne i sve aplikacije su OK -> READY
        rec_valid_ready = dict(rec_missing_report, latest_report={"status": "COMPLETED", "duration_seconds": 1714})
        self.assertEqual(compute_handover_status(rec_valid_ready), "READY")

    def test_05_imp45_zh_cn_placeholder_poisoning_prevention_and_alias_cleanup(self):
        """IMP-45: Kineski placeholderi ('加载中...', '正在加载配置...') ne truju _serial_index / _alias_index i ne spajaju različite uređaje."""
        for ph in (
            "加载中...",
            "加载中",
            "读取中...",
            "正在加载配置...",
            "正在加载...",
            "检测中...",
            "loading...",
            "detecting...",
            "unknown",
            "default string",
            " Wait... ",
            "Preuzimanje…",
        ):
            self.assertTrue(is_placeholder_hw_value(ph), f"Placeholder nije prepoznat: {ph}")

        self.assertFalse(is_placeholder_hw_value("PF2BF29H"))
        self.assertFalse(is_placeholder_hw_value("PF1LVNV3"))
        self.assertFalse(is_placeholder_hw_value("Lenovo ThinkPad T14 Gen 1"))

        # 1. Prvi zh-CN uređaj (DESKTOP-1OUVS6M) šalje rani heartbeat sa '加载中...'
        k1 = self.fleet_store.record_heartbeat(
            {
                "computer_name": "DESKTOP-1OUVS6M",
                "serial_number": "加载中...",
                "hw": {"model": "正在加载配置...", "serial": "加载中..."},
                "phase": 1,
            },
            "192.168.180.153",
        )
        self.assertEqual(k1, "DESKTOP-1OUVS6M")
        self.assertEqual(len(self.fleet_store._serial_index), 0)

        # 2. Drugi zh-CN uređaj (W-AAD-007326285) takođe šalje rani heartbeat sa '加载中...'
        k2 = self.fleet_store.record_heartbeat(
            {
                "computer_name": "W-AAD-007326285",
                "serial_number": "加载中...",
                "hw": {"model": "正在加载配置...", "serial": "加载中..."},
                "phase": 1,
            },
            "192.168.180.161",
        )
        self.assertEqual(k2, "W-AAD-007326285")
        self.assertIn("DESKTOP-1OUVS6M", self.fleet_store.fleet_devices)
        self.assertIn("W-AAD-007326285", self.fleet_store.fleet_devices)

        # 3. Čak i ako je _alias_index['W-AAD-007326285'] ranije bio ručno/istorijski zatrovan na DESKTOP-1OUVS6M,
        # dolazak pravog serijskog broja PF2BF29H mora očistiti _alias_index i preusmeriti logove na pravi uređaj!
        self.fleet_store.fleet_devices["DESKTOP-1OUVS6M"]["serial_number"] = "PF1LVNV3"
        self.fleet_store._serial_index["PF1LVNV3"] = "DESKTOP-1OUVS6M"
        self.fleet_store._alias_index["W-AAD-007326285"] = "DESKTOP-1OUVS6M"

        k2_fixed = self.fleet_store.record_heartbeat(
            {
                "computer_name": "W-AAD-007326285",
                "serial_number": "PF2BF29H",
                "hw": {"model": "Lenovo ThinkPad T14 Gen 1", "serial": "PF2BF29H"},
                "phase": 2,
            },
            "192.168.180.161",
        )
        self.assertEqual(k2_fixed, "W-AAD-007326285")
        self.assertEqual(self.fleet_store._alias_index["W-AAD-007326285"], "W-AAD-007326285")

        # 4. Logovi za W-AAD-007326285 (čak i bez serial_number u payloadu) idu isključivo u W-AAD-007326285
        self.fleet_store.record_logs("W-AAD-007326285", ["[P4] Instalacija Chrome OK"])
        self.assertEqual(len(self.fleet_store.get_device_logs("W-AAD-007326285")), 1)
        self.assertEqual(len(self.fleet_store.get_device_logs("DESKTOP-1OUVS6M")), 0)

    def test_06_imp47_and_imp52_heartbeat_terminal_reset_in_p4_p5_and_apps_protection(self):
        """IMP-47 & IMP-52: Ulazak u P4/P5 posle Faze 6 resetuje Zavrseno/READY na Aktivno/IN_PROGRESS, a WAIT apps u P5 ne gazi OK/WARN iz P4."""
        # 1. Uređaj završava Fazu 6 kao READY
        self.fleet_store.record_lifecycle_complete(
            {
                "computer_name": "DESKTOP-H9L1RNR",
                "serial_number": "BK348VV24313GT",
                "status": "COMPLETED",
                "diagnostics_status": "OK",
                "cooling_verified": True,
                "manual_check_required": False,
                "duration_seconds": 1700,
                "apps": {
                    "DotNet": "OK",
                    "Chrome": "OK",
                    "WinRAR": "OK",
                    "Acrobat": "OK",
                    "Office": "OK",
                    "BatteryBar": "WARN",
                    "License": "WARN",
                    "Shortcuts": "OK",
                },
            },
            "192.168.170.122",
        )
        sum_done = self.fleet_store.get_devices_summary()["DESKTOP-H9L1RNR"]
        self.assertEqual(sum_done["status_label"], "Zavrseno")
        self.assertEqual(sum_done["handover_status"], "READY")

        # 2. Operater naknadno pokreće Fazu 5 (phase=5, worker_progress=85), a klijent posle UEFI restarta šalje apps=WAIT/SKIPPED
        self.fleet_store.record_heartbeat(
            {
                "computer_name": "DESKTOP-H9L1RNR",
                "serial_number": "BK348VV24313GT",
                "phase": 5,
                "worker_progress": 85,
                "live_status": "Hardverska dijagnostika u toku...",
                "apps": {
                    "DotNet": "WAIT",
                    "Chrome": "WAIT",
                    "WinRAR": "WAIT",
                    "Acrobat": "WAIT",
                    "Office": "WAIT",
                    "BatteryBar": "SKIPPED",
                    "License": "WAIT",
                    "Shortcuts": "WAIT",
                },
            },
            "192.168.170.122",
        )
        sum_p5 = self.fleet_store.get_devices_summary()["DESKTOP-H9L1RNR"]
        # IMP-47: Terminalni status_label="Zavrseno" i handover="READY" su resetovani!
        self.assertEqual(sum_p5["status_label"], "Aktivno")
        self.assertEqual(sum_p5["handover_status"], "IN_PROGRESS")
        self.assertNotIn("latest_report", self.fleet_store.fleet_devices["DESKTOP-H9L1RNR"])
        # IMP-52: Verifikovani P4 statusi aplikacija nisu pregaženi sa WAIT/SKIPPED!
        self.assertEqual(sum_p5["apps"]["Acrobat"], "OK")
        self.assertEqual(sum_p5["apps"]["Office"], "OK")
        self.assertEqual(sum_p5["apps"]["BatteryBar"], "WARN")

    def test_07_imp50_phase5_uefi_reboot_shows_restart_u_toku(self):
        """IMP-50: UEFI_REBOOT u Fazi 5 do 720s prikazuje 'Restart u toku' umesto 'Offline'."""
        self.fleet_store.record_heartbeat(
            {
                "computer_name": "DESKTOP-M4PSF88",
                "serial_number": "0F37XVY23503FB",
                "phase": 5,
                "worker_progress": 85,
                "live_status": "Restartujem u UEFI BIOS radi dijagnostike touchscreen-a...",
            },
            "192.168.170.184",
        )
        # Simuliraj 230s bez heartbeat-a tokom UEFI BIOS restarta
        self.fleet_store.fleet_devices["DESKTOP-M4PSF88"]["last_seen"] = time.time() - 230.0
        sum_reboot = self.fleet_store.get_devices_summary()["DESKTOP-M4PSF88"]
        self.assertFalse(sum_reboot["is_online"])
        self.assertEqual(sum_reboot["status_label"], "Restart u toku")

        # Posle 800s (> 720s) prelazi u Offline
        self.fleet_store.fleet_devices["DESKTOP-M4PSF88"]["last_seen"] = time.time() - 800.0
        sum_offline = self.fleet_store.get_devices_summary()["DESKTOP-M4PSF88"]
        self.assertEqual(sum_offline["status_label"], "Offline")


if __name__ == "__main__":
    unittest.main()
