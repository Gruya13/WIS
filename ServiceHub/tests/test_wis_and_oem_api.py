"""
tests.test_wis_and_oem_api
==========================
Integracioni testovi za Talas 2 REST API rute:
- WIS ISO rute: GET /api/wis/iso/info, GET /api/wis/iso/download
- WIS USB rute: GET /api/wis/usb/list, POST /api/wis/usb/burn, GET /api/wis/usb/progress (SSE)
- Proširena ruta: POST /api/drivers/resolve sa OEM provajderom (Lenovo garancija i SCCM)
- Integracija garancije u servishub.reports (Markdown, DEVICE_INFO.json i get_device_history)
"""

import os
import shutil
import tempfile
import threading
import json
import urllib.request
import urllib.error
import unittest
from unittest.mock import patch, MagicMock
from http.server import ThreadingHTTPServer

from servishub.config import HubConfig
from servishub.telemetry import FleetStore
from servishub.reports import generate_markdown_report, save_agy_session, get_device_history
from servishub.handlers import ServisHubHandler, reset_wis_burn_state, _update_wis_burn_progress


class TestWisAndOemApi(unittest.TestCase):

    @classmethod
    def setUpClass(cls):
        cls.test_dir = tempfile.mkdtemp()
        cls.config = HubConfig(
            host="127.0.0.1",
            port=0,
            base_dir=cls.test_dir,
            storage_dir=os.path.join(cls.test_dir, "storage"),
            logs_dir=os.path.join(cls.test_dir, "logs"),
        )
        cls.fleet_store = FleetStore(live_logs_dir=cls.config.live_logs_dir, reports_dir=cls.config.reports_dir)

        ServisHubHandler.fleet_store = cls.fleet_store
        ServisHubHandler.config = cls.config
        ServisHubHandler.payload_mgr = None
        ServisHubHandler.driver_cache_mgr = None
        ServisHubHandler.ms_catalog_scraper = None

        cls.httpd = ThreadingHTTPServer(("127.0.0.1", 0), ServisHubHandler)
        cls.port = cls.httpd.server_address[1]
        cls.base_url = f"http://127.0.0.1:{cls.port}"

        cls.server_thread = threading.Thread(target=cls.httpd.serve_forever, daemon=True)
        cls.server_thread.start()

    @classmethod
    def tearDownClass(cls):
        cls.httpd.shutdown()
        cls.httpd.server_close()
        shutil.rmtree(cls.test_dir, ignore_errors=True)

    def setUp(self):
        reset_wis_burn_state()

    def _request(self, method: str, path: str, data: dict = None):
        url = f"{self.base_url}{path}"
        body = json.dumps(data).encode("utf-8") if data is not None else None
        req = urllib.request.Request(url, data=body, method=method)
        req.add_header("Content-Type", "application/json")
        try:
            with urllib.request.urlopen(req, timeout=5) as resp:
                headers = dict(resp.headers)
                content_type = headers.get("Content-Type", "")
                raw = resp.read()
                if "application/json" in content_type:
                    return resp.status, json.loads(raw.decode("utf-8")), headers
                return resp.status, raw, headers
        except urllib.error.HTTPError as e:
            raw = e.read()
            content_type = e.headers.get("Content-Type", "")
            if "application/json" in content_type:
                return e.code, json.loads(raw.decode("utf-8")), dict(e.headers)
            return e.code, raw, dict(e.headers)

    # =========================================================================
    # 1. WIS ISO API Rute
    # =========================================================================

    def test_01_wis_iso_info_not_available(self):
        status, data, headers = self._request("GET", "/api/wis/iso/info")
        self.assertEqual(status, 200)
        self.assertFalse(data.get("available"))
        self.assertEqual(data.get("size_bytes"), 0)

    def test_02_wis_iso_download_404(self):
        status, data, headers = self._request("GET", "/api/wis/iso/download")
        self.assertEqual(status, 404)
        self.assertIn("error", data)

    def test_03_wis_iso_info_and_download_when_present(self):
        iso_file = os.path.join(self.config.wis_dir, "wis-v2.5.0.iso")
        dummy_content = b"CD001" + b"\x00" * 1024 + b"TEST_WIS_IMAGE"
        with open(iso_file, "wb") as f:
            f.write(dummy_content)

        # GET /api/wis/iso/info
        status, data, headers = self._request("GET", "/api/wis/iso/info")
        self.assertEqual(status, 200)
        self.assertTrue(data.get("available"))
        self.assertEqual(data.get("filename"), "wis-v2.5.0.iso")
        self.assertEqual(data.get("version"), "2.5.0")
        self.assertEqual(data.get("size_bytes"), len(dummy_content))
        self.assertIsNotNone(data.get("sha256"))

        # GET /api/wis/iso/download
        status, content, resp_headers = self._request("GET", "/api/wis/iso/download")
        self.assertEqual(status, 200)
        self.assertEqual(content, dummy_content)
        self.assertEqual(resp_headers.get("Content-Type"), "application/octet-stream")
        self.assertIn('attachment; filename="wis-v2.5.0.iso"', resp_headers.get("Content-Disposition", ""))
        self.assertEqual(resp_headers.get("X-WIS-Version"), "2.5.0")
        self.assertEqual(resp_headers.get("X-WIS-SHA256"), data.get("sha256"))

    # =========================================================================
    # 2. WIS USB API Rute
    # =========================================================================

    def test_04_wis_usb_list(self):
        status, data, headers = self._request("GET", "/api/wis/usb/list")
        self.assertEqual(status, 200)
        self.assertEqual(data.get("status"), "ok")
        self.assertIsInstance(data.get("devices"), list)

    def test_05_wis_usb_burn_validation(self):
        # 1. Missing confirm_wipe
        status, data, headers = self._request("POST", "/api/wis/usb/burn", {"device_path": "/dev/sdb"})
        self.assertEqual(status, 400)
        self.assertIn("error", data)

        # 2. confirm_wipe is False
        status, data, headers = self._request("POST", "/api/wis/usb/burn", {"device_path": "/dev/sdb", "confirm_wipe": False})
        self.assertEqual(status, 400)
        self.assertIn("error", data)

        # 3. Missing device_path
        status, data, headers = self._request("POST", "/api/wis/usb/burn", {"confirm_wipe": True})
        self.assertEqual(status, 400)
        self.assertIn("error", data)

    def test_06_wis_usb_burn_and_progress_stream(self):
        # Pokrećemo burn sa mockovanim pozadinskim procesom
        with patch("servishub.handlers._run_wis_burn_process") as mock_run:
            status, data, headers = self._request(
                "POST",
                "/api/wis/usb/burn",
                {"device_path": "/dev/sdb", "confirm_wipe": True},
            )
            self.assertEqual(status, 200)
            self.assertEqual(data.get("status"), "started")
            self.assertEqual(data.get("device_path"), "/dev/sdb")
            mock_run.assert_called_once()

        # Emitujemo događaje za SSE test
        def feed_events():
            import time
            time.sleep(0.05)
            _update_wis_burn_progress("unmounting", 10, "Demontiranje particija...")
            time.sleep(0.05)
            _update_wis_burn_progress("partitioning", 50, "Kreiranje GPT tabele...")
            time.sleep(0.05)
            _update_wis_burn_progress("completed", 100, "Narezivanje završeno.")

        feeder = threading.Thread(target=feed_events, daemon=True)
        feeder.start()

        # Čitamo SSE strim
        url = f"{self.base_url}/api/wis/usb/progress"
        req = urllib.request.Request(url)
        with urllib.request.urlopen(req, timeout=5) as resp:
            self.assertEqual(resp.status, 200)
            self.assertEqual(resp.headers.get("Content-Type"), "text/event-stream")

            received_lines = []
            while True:
                line = resp.readline().decode("utf-8")
                if not line:
                    break
                if line.startswith("data: "):
                    payload = json.loads(line[6:].strip())
                    received_lines.append(payload)
                    if payload.get("phase") in ("completed", "error"):
                        break

        phases = [item.get("phase") for item in received_lines]
        self.assertIn("completed", phases)
        self.assertEqual(received_lines[-1]["percent"], 100)

    # =========================================================================
    # 3. Proširena ruta POST /api/drivers/resolve sa OEM informacijama
    # =========================================================================

    def test_07_driver_resolve_lenovo_oem(self):
        req_data = {
            "manufacturer": "LENOVO",
            "model": "20UD",
            "serial_number": "PF123456",
            "hwids": ["PCI\\VEN_1022&DEV_1638"],
        }
        status, data, headers = self._request("POST", "/api/drivers/resolve", req_data)
        self.assertEqual(status, 200)
        self.assertEqual(data.get("status"), "ok")
        self.assertIn("warranty", data)
        self.assertIn("oem", data)

        warranty = data["warranty"]
        self.assertIn("in_warranty", warranty)
        self.assertIn("status_label", warranty)
        self.assertIn("service_level", warranty)

        oem = data["oem"]
        self.assertEqual(oem.get("status"), "ok")
        self.assertIn("package_name", oem)
        self.assertIn("tp_t14_gen1_amd_win11_20ud.exe", oem.get("package_name"))

    def test_08_driver_resolve_oem_without_hwids(self):
        req_data = {
            "manufacturer": "Lenovo",
            "model": "21AH",
            "serial_number": "PF999999",
        }
        status, data, headers = self._request("POST", "/api/drivers/resolve", req_data)
        self.assertEqual(status, 200)
        self.assertIn("warranty", data)
        self.assertIn("oem", data)
        self.assertEqual(data.get("count"), 0)
        self.assertEqual(data.get("results"), [])

    def test_09_driver_resolve_unsupported_oem_falls_back(self):
        req_data = {
            "manufacturer": "Asus",
            "model": "ZenBook",
            "hwids": ["PCI\\VEN_8086&DEV_9999"],
        }
        status, data, headers = self._request("POST", "/api/drivers/resolve", req_data)
        self.assertEqual(status, 200)
        self.assertNotIn("warranty", data)
        self.assertNotIn("oem", data)
        self.assertEqual(data.get("count"), 1)

    # =========================================================================
    # 4. Integracija Garancije u servishub.reports
    # =========================================================================

    def test_10_generate_markdown_report_includes_warranty_section(self):
        report_data = {
            "device_id": "TK-DEV-100",
            "computer_name": "THINKPAD-T14",
            "serial_number": "PF123456",
            "status": "COMPLETED",
            "warranty": {
                "in_warranty": True,
                "status_label": "U garanciji",
                "service_level": "Premier Onsite Support",
                "start_date": "2023-01-01",
                "end_date": "2026-12-31",
                "days_remaining": 448,
            },
            "hw": {
                "manufacturer": "Lenovo",
                "model": "ThinkPad T14 Gen 2",
            },
        }

        md = generate_markdown_report(report_data)
        self.assertIn("### 🛡️ Fabrička Garancija (OEM)", md)
        self.assertIn("- **Status:** U garanciji", md)
        self.assertIn("- **Nivo podrške:** Premier Onsite Support", md)
        self.assertIn("- **Period:** 2023-01-01 do 2026-12-31 (Preostalo: 448 dana)", md)

    def test_11_save_agy_session_and_device_history_persists_warranty(self):
        reports_dir = os.path.join(self.test_dir, "test_rep_history")
        tag = "Lenovo_ThinkPad_T14_PF123456"

        payload = {
            "device_tag": tag,
            "serial_number": "PF123456",
            "session_id": "sess-test-warranty-01",
            "hardware": {
                "manufacturer": "Lenovo",
                "model": "ThinkPad T14",
            },
            "warranty": {
                "in_warranty": True,
                "status_label": "U garanciji",
                "service_level": "Depot Warranty",
                "start_date": "2024-01-01",
                "end_date": "2027-01-01",
                "days_remaining": 815,
            },
            "oem": {
                "status": "ok",
                "package_name": "tp_t14_win11.exe",
            },
            "summary": "Dijagnostika sa garancijom.",
            "status": "COMPLETED",
        }

        saved = save_agy_session(tag, payload, reports_dir)
        self.assertTrue(os.path.isfile(saved["device_info_file"]))

        # Čitanje DEVICE_INFO.json
        with open(saved["device_info_file"], "r", encoding="utf-8") as f:
            dev_info = json.load(f)

        self.assertEqual(dev_info["warranty"]["status_label"], "U garanciji")
        self.assertEqual(dev_info["oem"]["package_name"], "tp_t14_win11.exe")

        # Provera preko get_device_history
        history = get_device_history(tag, reports_dir)
        self.assertTrue(history["is_registered"])
        self.assertIsNotNone(history.get("warranty"))
        self.assertEqual(history["warranty"]["days_remaining"], 815)
        self.assertIsNotNone(history.get("oem"))


if __name__ == "__main__":
    unittest.main()
