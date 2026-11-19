"""
tests.test_security_and_v34
===========================
Regresioni i sigurnosni testovi za WIS v3.1.0 / ServiceHub v3.4.0:
- Fail-closed admin autentifikacija (503 bez tokena) i klijentski upload token
- CORS allowlist bez podrazumevanog wildcard '*'
- Validacija .tar model paketa (zabrana traversal/symlink/skripti, obavezan .inf)
- SSE limit pretplatnika (429), Screen Mirror TTL & RAM eviction, retencija logova
- Kanonski identitet uređaja po serijskom broju (bez duplikata DESKTOP-* <-> TK-*)
- Jedinstveni handover_status, HwTest FAIL -> success=False, kohorte i None za 0 zahteva
- INF-driven cross-model HWID keširanje sa prioritetom tačnog SUBSYS meča
"""

import io
import json
import os
import shutil
import socket
import tarfile
import tempfile
import threading
import time
import unittest
import urllib.error
import urllib.request
from http.server import ThreadingHTTPServer
from unittest.mock import patch

from servishub.benchmark import compute_fleet_benchmark_summary, extract_device_benchmark
from servishub.config import HubConfig
from servishub.drivers.cache import DriverCacheManager
from servishub.handlers import ServisHubHandler
from servishub.reports import compute_handover_status
from servishub.telemetry import FleetStore
from servishub.web.dashboard import HTML_DASHBOARD


def _build_tar_bytes(entries: dict[str, bytes], symlink: tuple[str, str] = None) -> bytes:
    buf = io.BytesIO()
    with tarfile.open(fileobj=buf, mode="w") as tf:
        for name, data in entries.items():
            ti = tarfile.TarInfo(name=name)
            ti.size = len(data)
            tf.addfile(ti, io.BytesIO(data))
        if symlink:
            link_name, link_target = symlink
            ti = tarfile.TarInfo(name=link_name)
            ti.type = tarfile.SYMTYPE
            ti.linkname = link_target
            tf.addfile(ti)
    return buf.getvalue()


class TestSecurityAndV34(unittest.TestCase):
    def setUp(self):
        self.test_dir = tempfile.mkdtemp(prefix="wis_v34_")

    def tearDown(self):
        shutil.rmtree(self.test_dir, ignore_errors=True)

    def _start_server(self, cfg: HubConfig, store: FleetStore = None):
        prev_cfg = getattr(ServisHubHandler, "config", None)
        prev_store = getattr(ServisHubHandler, "fleet_store", None)
        st = store or FleetStore(
            live_logs_dir=cfg.live_logs_dir,
            reports_dir=cfg.reports_dir,
            max_screen_frames_bytes=cfg.max_screen_frames_bytes,
            screen_frame_ttl_sec=cfg.screen_frame_ttl_sec,
            live_log_retention_days=cfg.live_log_retention_days,
            max_sse_subscribers_total=cfg.max_sse_subscribers_total,
            max_sse_subscribers_per_device=cfg.max_sse_subscribers_per_device,
        )
        ServisHubHandler.config = cfg
        ServisHubHandler.fleet_store = st
        httpd = ThreadingHTTPServer(("127.0.0.1", 0), ServisHubHandler)
        threading.Thread(target=httpd.serve_forever, daemon=True).start()

        def _cleanup():
            httpd.shutdown()
            httpd.server_close()
            ServisHubHandler.config = prev_cfg
            ServisHubHandler.fleet_store = prev_store

        self.addCleanup(_cleanup)
        return httpd.server_address[1], st

    def test_01_fail_closed_admin_and_upload_tokens(self):
        cfg = HubConfig(
            host="127.0.0.1",
            port=0,
            base_dir=self.test_dir,
            storage_dir=os.path.join(self.test_dir, "storage"),
            logs_dir=os.path.join(self.test_dir, "logs"),
            updates_dir=os.path.join(self.test_dir, "updates"),
            model_packs_dir=os.path.join(self.test_dir, "packs"),
            admin_token="",
            client_token="",
            allow_unauthenticated_lan=False,
        )
        os.makedirs(cfg.updates_dir, exist_ok=True)
        os.makedirs(cfg.model_packs_dir, exist_ok=True)
        port, _ = self._start_server(cfg)

        # 1. Bez tokena sve admin i upload rute moraju vratiti 503 (fail-closed)
        for path, body, ctype in (
            ("/api/devices/DEV1/command", b'{"action":"cancel"}', "application/json"),
            ("/api/updates/prune", b'{"max_packages_per_build":2}', "application/json"),
            ("/api/wis/usb/burn", b'{"device_path":"/dev/sdz","confirm_wipe":true}', "application/json"),
            ("/api/updates/upload?filename=KB5051111.msu", b"MSU_BYTES" * 100, "application/octet-stream"),
            ("/api/drivers/model-pack/upload?model=ThinkPad_T14", b"TAR_BYTES" * 100, "application/octet-stream"),
        ):
            req = urllib.request.Request(
                f"http://127.0.0.1:{port}{path}",
                data=body,
                method="POST",
                headers={"Content-Type": ctype, "Content-Length": str(len(body))},
            )
            with self.assertRaises(urllib.error.HTTPError) as ctx:
                urllib.request.urlopen(req, timeout=5)
            self.assertEqual(ctx.exception.code, 503, f"Očekivan 503 na {path}")
            ctx.exception.close()

        # 2. Sa podešenim client_token + admin_token: pogrešan -> 401, validan -> 200
        cfg.admin_token = "adm-secret-310"
        cfg.client_token = "cli-secret-310"

        good_tar = _build_tar_bytes({"net.inf": b"[Version]\r\nSignature=\"$WINDOWS NT$\"\r\n" * 20})

        bad_up = urllib.request.Request(
            f"http://127.0.0.1:{port}/api/drivers/model-pack/upload?model=ThinkPad_T14",
            data=good_tar,
            method="POST",
            headers={
                "Content-Type": "application/octet-stream",
                "Content-Length": str(len(good_tar)),
                "X-ServisHub-Client-Token": "wrong",
            },
        )
        with self.assertRaises(urllib.error.HTTPError) as ctx_bad:
            urllib.request.urlopen(bad_up, timeout=5)
        self.assertEqual(ctx_bad.exception.code, 401)
        ctx_bad.exception.close()

        # Client token ne može pozvati admin /command (401), ali može upload (200)
        cli_cmd = urllib.request.Request(
            f"http://127.0.0.1:{port}/api/devices/DEV1/command",
            data=b'{"action":"cancel"}',
            method="POST",
            headers={"Content-Type": "application/json", "X-ServisHub-Token": "cli-secret-310"},
        )
        with self.assertRaises(urllib.error.HTTPError) as ctx_cli_adm:
            urllib.request.urlopen(cli_cmd, timeout=5)
        self.assertEqual(ctx_cli_adm.exception.code, 401)
        ctx_cli_adm.exception.close()

        ok_up = urllib.request.Request(
            f"http://127.0.0.1:{port}/api/drivers/model-pack/upload?model=ThinkPad_T14",
            data=good_tar,
            method="POST",
            headers={
                "Content-Type": "application/octet-stream",
                "Content-Length": str(len(good_tar)),
                "X-ServisHub-Client-Token": "cli-secret-310",
            },
        )
        with urllib.request.urlopen(ok_up, timeout=5) as resp:
            self.assertEqual(resp.status, 200)
            payload = json.loads(resp.read().decode("utf-8"))
            self.assertFalse(payload["has_catalog_signatures"])

        ok_cmd = urllib.request.Request(
            f"http://127.0.0.1:{port}/api/devices/DEV1/command",
            data=b'{"action":"cancel"}',
            method="POST",
            headers={"Content-Type": "application/json", "X-ServisHub-Token": "adm-secret-310"},
        )
        with urllib.request.urlopen(ok_cmd, timeout=5) as resp_cmd:
            self.assertEqual(resp_cmd.status, 200)

    def test_02_tar_model_pack_security_validation(self):
        cfg = HubConfig(
            host="127.0.0.1",
            port=0,
            base_dir=self.test_dir,
            storage_dir=os.path.join(self.test_dir, "storage"),
            logs_dir=os.path.join(self.test_dir, "logs"),
            model_packs_dir=os.path.join(self.test_dir, "packs"),
            client_token="tok",
        )
        os.makedirs(cfg.model_packs_dir, exist_ok=True)
        port, _ = self._start_server(cfg)

        bad_tars = [
            _build_tar_bytes({"../../evil.inf": b"[Version]\r\n" * 40}),
            _build_tar_bytes({"driver.inf": b"[Version]\r\n" * 30, "run.ps1": b"calc.exe"}),
            _build_tar_bytes({"readme.txt": b"no inf here" * 60}),
            _build_tar_bytes({"driver.inf": b"[Version]\r\n" * 30}, symlink=("evil_link", "/etc/passwd")),
        ]
        for idx, blob in enumerate(bad_tars):
            req = urllib.request.Request(
                f"http://127.0.0.1:{port}/api/drivers/model-pack/upload?model=BadModel{idx}",
                data=blob,
                method="POST",
                headers={
                    "Content-Type": "application/octet-stream",
                    "Content-Length": str(len(blob)),
                    "X-ServisHub-Client-Token": "tok",
                },
            )
            with self.assertRaises(urllib.error.HTTPError) as ctx:
                urllib.request.urlopen(req, timeout=5)
            self.assertEqual(ctx.exception.code, 400, f"Loš tar #{idx} nije odbijen sa 400")
            ctx.exception.close()

    def test_03_cors_allowlist_and_dashboard_admin_fetch(self):
        cfg = HubConfig(
            host="127.0.0.1",
            port=0,
            base_dir=self.test_dir,
            storage_dir=os.path.join(self.test_dir, "storage"),
            logs_dir=os.path.join(self.test_dir, "logs"),
            cors_origins=["http://cockpit.local:8080"],
        )
        port, _ = self._start_server(cfg)

        with urllib.request.urlopen(f"http://127.0.0.1:{port}/api/health", timeout=5) as resp0:
            self.assertIsNone(resp0.headers.get("Access-Control-Allow-Origin"))

        req_ok = urllib.request.Request(
            f"http://127.0.0.1:{port}/api/health",
            headers={"Origin": "http://cockpit.local:8080"},
        )
        with urllib.request.urlopen(req_ok, timeout=5) as resp1:
            self.assertEqual(resp1.headers.get("Access-Control-Allow-Origin"), "http://cockpit.local:8080")

        req_evil = urllib.request.Request(
            f"http://127.0.0.1:{port}/api/health",
            headers={"Origin": "https://evil.example"},
        )
        with urllib.request.urlopen(req_evil, timeout=5) as resp2:
            self.assertIsNone(resp2.headers.get("Access-Control-Allow-Origin"))

        static_js = os.path.join(os.path.dirname(os.path.dirname(__file__)), "servishub", "web", "static", "dashboard.js")
        with open(static_js, "r", encoding="utf-8") as f:
            js_src = f.read()
        self.assertIn("adminFetch", js_src)
        self.assertIn("X-ServisHub-Token", js_src)
        self.assertIn("sessionStorage", js_src)

    def test_04_sse_limit_screen_ram_ttl_and_log_retention(self):
        cfg = HubConfig(
            host="127.0.0.1",
            port=0,
            base_dir=self.test_dir,
            storage_dir=os.path.join(self.test_dir, "storage"),
            logs_dir=os.path.join(self.test_dir, "logs"),
            max_screen_frames_bytes=200,
            screen_frame_ttl_sec=0.5,
            live_log_retention_days=7,
            max_sse_subscribers_total=3,
            max_sse_subscribers_per_device=1,
        )
        port, store = self._start_server(cfg)

        # 1. Screen Mirror TTL + globalni RAM limit
        now = time.time()
        store.store_screen_frame("DEV-OLD", b"A" * 100, client_ts=now - 2.0)
        store.store_screen_frame("DEV-A", b"B" * 120, client_ts=now - 0.1)
        store.store_screen_frame("DEV-B", b"C" * 120, client_ts=now)
        self.assertNotIn("DEV-OLD", store.screen_frames)
        total_bytes = sum(v["size"] for v in store.screen_frames.values())
        self.assertLessEqual(total_bytes, 200)
        self.assertIn("DEV-B", store.screen_frames)

        # 2. Retencija diskovnih logova
        os.makedirs(cfg.live_logs_dir, exist_ok=True)
        old_log = os.path.join(cfg.live_logs_dir, "servis_deploy_OLDPC_20250101.log")
        with open(old_log, "w", encoding="utf-8") as f:
            f.write("old line\n")
        old_ts = time.time() - (15 * 86400)
        os.utime(old_log, (old_ts, old_ts))
        store.append_to_disk_log("NEWPC", ["fresh line"])
        self.assertFalse(os.path.exists(old_log))

        # 3. SSE limit (max_sse_subscribers_per_device=1 -> drugi zahtev za isti uređaj dobija 429)
        sock = socket.create_connection(("127.0.0.1", port), timeout=5)
        try:
            sock.sendall(b"GET /api/stream/DEV-SSE-1 HTTP/1.1\r\nHost: 127.0.0.1\r\n\r\n")
            time.sleep(0.15)
            with self.assertRaises(urllib.error.HTTPError) as ctx_sse:
                urllib.request.urlopen(f"http://127.0.0.1:{port}/api/stream/DEV-SSE-1", timeout=5)
            self.assertEqual(ctx_sse.exception.code, 429)
            ctx_sse.exception.close()
        finally:
            sock.close()

    def test_05_canonical_serial_dedup_prevents_reverse_duplicates(self):
        store = FleetStore()
        # 1. Pred-OEM heartbeat pod DESKTOP-* sa serijskim brojem PF3NQ7J0
        k1 = store.record_heartbeat({"computer_name": "DESKTOP-XYZ123", "serial_number": "PF3NQ7J0", "phase": 1}, "10.0.0.10")
        self.assertEqual(k1, "DESKTOP-XYZ123")

        # 2. Uređaj se preimenuje u TK-LAPTOP-PF3NQ7J0 i šalje heartbeat sa istim serijskim brojem
        k2 = store.record_heartbeat({"computer_name": "TK-LAPTOP-PF3NQ7J0", "serial_number": "PF3NQ7J0", "phase": 3}, "10.0.0.10")
        self.assertEqual(k2, "TK-LAPTOP-PF3NQ7J0")
        self.assertEqual(len(store.fleet_devices), 1)

        # 3. Zakasneli heartbeat pod starim DESKTOP-XYZ123 (čak i bez serial_number) ne sme vratiti duplikat!
        k3 = store.record_heartbeat({"computer_name": "DESKTOP-XYZ123", "phase": 4}, "10.0.0.10")
        self.assertEqual(k3, "TK-LAPTOP-PF3NQ7J0")
        self.assertEqual(len(store.fleet_devices), 1)
        self.assertEqual(store.fleet_devices["TK-LAPTOP-PF3NQ7J0"]["computer_name"], "TK-LAPTOP-PF3NQ7J0")

    def test_06_handover_status_hwtest_fail_cohorts_and_null_cache_ratio(self):
        # 1. HwTest FAIL obara success čak i kad je status=COMPLETED
        rec_hw_fail = {
            "device_id": "TK-PROBOOK-460",
            "computer_name": "TK-PROBOOK-460",
            "status": "COMPLETED",
            "hwtest": {"summary": {"overall": "FAIL", "fail": 1, "operator_wait_sec": 42.0, "active_hwtest_sec": 110.0}},
            "benchmark": {"phase_durations_sec": {"Phase1": 10, "Phase2": 90}, "total_phase_duration_sec": 100},
        }
        self.assertEqual(compute_handover_status(rec_hw_fail), "NEEDS_SERVICE")

        # 2. Legacy zapis bez benchmark polja i moderni v3 zapis sa 0 keš zahteva
        reports_dir = os.path.join(self.test_dir, "reports")
        os.makedirs(reports_dir, exist_ok=True)
        with open(os.path.join(reports_dir, "r1.json"), "w", encoding="utf-8") as f:
            json.dump(rec_hw_fail, f)
        with open(os.path.join(reports_dir, "r2.json"), "w", encoding="utf-8") as f:
            json.dump({
                "device_id": "DESKTOP-LEGACY-01",
                "computer_name": "DESKTOP-LEGACY-01",
                "status": "COMPLETED",
                "duration_seconds": 600,
                "timestamp": "2026-10-01T10:00:00",
            }, f)

        bm_zero = extract_device_benchmark(rec_hw_fail)
        self.assertIsNone(bm_zero["cache_hit_ratio_pct"])
        self.assertIsNone(bm_zero["byte_hit_ratio_pct"])
        self.assertEqual(bm_zero["operator_wait_sec"], 42.0)

        summary = compute_fleet_benchmark_summary(reports_dir=reports_dir)
        self.assertEqual(summary["total_installations"], 2)
        self.assertEqual(summary["successful_installations"], 1)  # samo legacy prosao; ProBook FAIL nije uspeh
        self.assertIsNone(summary["cache_metrics"]["cache_hit_ratio_pct"])
        self.assertEqual(summary["cohorts"]["v3_modern"]["total"], 1)
        self.assertEqual(summary["cohorts"]["v3_modern"]["successful"], 0)
        self.assertEqual(summary["cohorts"]["legacy"]["total"], 1)
        self.assertEqual(summary["cohorts"]["legacy"]["successful"], 1)
        self.assertEqual(summary["phase5_timing"]["avg_operator_wait_seconds"], 42.0)
        self.assertEqual(summary["phase5_timing"]["avg_active_hwtest_seconds"], 110.0)

    def test_07_inf_cross_model_cache_indexing_and_exact_priority(self):
        cache_dir = os.path.join(self.test_dir, "drv_cache")
        mgr = DriverCacheManager(cache_dir)

        # Model A preuzima paket čiji .inf deklariše i generički čipset VEN_8086&DEV_7E4C
        with patch(
            "servishub.drivers.cache.extract_inf_hwids_from_cab",
            return_value=[r"PCI\VEN_8086&DEV_7E4C", r"PCI\VEN_8086&DEV_7E4C&SUBSYS_111117AA"],
        ):
            mgr.save_cache(
                r"PCI\VEN_8086&DEV_7E4C&SUBSYS_111117AA",
                b"CAB_MODEL_A" * 60,
                {"title": "Intel Chipset A", "version": "10.1.1"},
            )

        # Model B sa drugim SUBSYS dobija inf_cross_model pogodak preko deklarisanog PCI\VEN_8086&DEV_7E4C
        hit_b = mgr.get_cached(r"PCI\VEN_8086&DEV_7E4C&SUBSYS_22221028")
        self.assertIsNotNone(hit_b)
        self.assertEqual(hit_b["match_source"], "inf_cross_model")
        self.assertEqual(hit_b["title"], "Intel Chipset A")

        # Ako sačuvamo specifični drajver baš za Model B SUBSYS, primarni tačan keš ima prednost
        with patch("servishub.drivers.cache.extract_inf_hwids_from_cab", return_value=[]):
            mgr.save_cache(
                r"PCI\VEN_8086&DEV_7E4C&SUBSYS_22221028",
                b"CAB_MODEL_B" * 60,
                {"title": "Dell Specific B", "version": "10.1.2"},
            )

        hit_b_exact = mgr.get_cached(r"PCI\VEN_8086&DEV_7E4C&SUBSYS_22221028")
        self.assertIsNotNone(hit_b_exact)
        self.assertEqual(hit_b_exact["match_source"], "primary_cache")
        self.assertEqual(hit_b_exact["title"], "Dell Specific B")


if __name__ == "__main__":
    unittest.main()
