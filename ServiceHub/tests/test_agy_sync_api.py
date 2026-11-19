"""
tests.test_agy_sync_api
=======================
Integracioni testovi za ADR-0003: Antigravity CLI Serviser Sync API
(Pre-flight istorija i Post-flight sesije).
"""

import os
import shutil
import tempfile
import threading
import json
import urllib.request
import unittest
from http.server import ThreadingHTTPServer

from servishub.config import HubConfig
from servishub.telemetry import FleetStore
from servishub.reports import get_device_history, save_agy_session
from servishub.handlers import ServisHubHandler


class TestAgySyncApi(unittest.TestCase):

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

    def _request(self, method: str, path: str, data: dict = None):
        url = f"{self.base_url}{path}"
        body = json.dumps(data).encode("utf-8") if data is not None else None
        req = urllib.request.Request(url, data=body, method=method)
        req.add_header("Content-Type", "application/json")
        with urllib.request.urlopen(req, timeout=5) as resp:
            content_type = resp.headers.get("Content-Type", "")
            raw = resp.read()
            if "application/json" in content_type:
                return resp.status, json.loads(raw.decode("utf-8"))
            return resp.status, raw

    def test_01_reports_unit_functions(self):
        reports_dir = os.path.join(self.test_dir, "unit_reports")
        tag = "Dell_Inc__Latitude_5420_8G7X123"

        # 1. Provera istorije za nepostojeći uređaj
        history = get_device_history(tag, reports_dir)
        self.assertFalse(history["is_registered"])
        self.assertEqual(len(history["interventions"]), 0)
        self.assertEqual(history["dnevnik_markdown"], "")

        # 2. Arhiviranje sesije
        payload = {
            "device_tag": tag,
            "serial_number": "8G7X123",
            "session_id": "sess-uuid-001",
            "timestamp": "2026-10-09T14:45:00Z",
            "serviser_id": "USB_TEREN_01",
            "hardware": {
                "manufacturer": "Dell Inc.",
                "model": "Latitude 5420",
                "cpu": "Intel Core i5-1145G7",
                "ram_gb": 16.0
            },
            "summary": "Zamenjena termalna pasta. Temperature normalizovane.",
            "status": "COMPLETED",
            "tools_executed": ["Get-DiskHealth.ps1", "Get-EventLogTriage.ps1"],
            "dnevnik_entry": "### [Datum: 2026-10-09 14:45:00] - Servis hladnjaka\n- Zamenjena pasta.",
            "report_markdown": "# Detaljni Izvestaj\nSve ispravno."
        }
        res = save_agy_session(tag, payload, reports_dir)
        self.assertTrue(os.path.isfile(res["session_file"]))
        self.assertTrue(os.path.isfile(res["dnevnik_file"]))
        self.assertTrue(os.path.isfile(res["device_info_file"]))
        self.assertTrue(os.path.isfile(res["latest_report_file"]))

        # 3. Provera istorije nakon arhiviranja
        updated_history = get_device_history(tag, reports_dir)
        self.assertTrue(updated_history["is_registered"])
        self.assertEqual(updated_history["serial_number"], "8G7X123")
        self.assertEqual(updated_history["manufacturer"], "Dell Inc.")
        self.assertEqual(updated_history["model"], "Latitude 5420")
        self.assertEqual(len(updated_history["interventions"]), 1)
        self.assertEqual(updated_history["interventions"][0]["summary"], "Zamenjena termalna pasta. Temperature normalizovane.")
        self.assertIn("Servis hladnjaka", updated_history["dnevnik_markdown"])

    def test_02_http_preflight_unregistered_device(self):
        status, data = self._request("GET", "/api/devices/Lenovo_ThinkPad_T14_PF9999/history")
        self.assertEqual(status, 200)
        self.assertFalse(data.get("is_registered"))
        self.assertEqual(len(data.get("interventions", [])), 0)
        self.assertEqual(data.get("dnevnik_markdown"), "")

    def test_03_http_post_agy_session_and_fleet_update(self):
        dev_tag = "Lenovo_ThinkPad_T14_PF9999"
        session_payload = {
            "device_tag": dev_tag,
            "serial_number": "PF9999",
            "session_id": "f81d4fae-7dec-11d0-a765-00a0c91e6bf6",
            "timestamp": "2026-10-09T14:45:00Z",
            "serviser_id": "USB_TEREN_01",
            "hardware": {
                "manufacturer": "Lenovo",
                "model": "ThinkPad T14",
                "cpu": "Intel Core i7-1185G7",
                "ram_gb": 16.0,
                "disks": "NVMe PC711 SK hynix 512GB (477 GB)",
                "os": "Windows 11 Pro 64-bit - Build 22631"
            },
            "summary": "Root cause: Detektovan NVMe SMART critical warning (Media Errors: 142). Preporučena hitna zamena diska.",
            "status": "CRITICAL_ISSUE",
            "tools_executed": ["Get-DiskHealth.ps1", "Get-EventLogTriage.ps1"],
            "dnevnik_entry": "### [Datum: 2026-10-09 14:45:00] - Otkaz NVMe diska\n- **Nalaz:** 142 nepopravljive greske.",
            "report_markdown": "# Detaljni Izvestaj Servisne Dijagnostike\n..."
        }

        status, resp = self._request("POST", f"/api/devices/{dev_tag}/agy-session", session_payload)
        self.assertEqual(status, 200)
        self.assertEqual(resp.get("status"), "ok")
        self.assertEqual(resp.get("device_tag"), dev_tag)
        self.assertEqual(resp.get("session_id"), "f81d4fae-7dec-11d0-a765-00a0c91e6bf6")
        self.assertTrue(resp.get("dnevnik_updated"))
        self.assertIn("f81d4fae", resp.get("json_archive", ""))

        # Provera FleetStore stanja
        devs = self.fleet_store.get_devices_summary()
        self.assertIn(dev_tag, devs)
        device_entry = devs[dev_tag]
        self.assertEqual(device_entry.get("status_label"), "Kvar (AGY)")
        self.assertEqual(device_entry.get("serial_number"), "PF9999")
        self.assertTrue(device_entry.get("has_errors"))

    def test_04_http_preflight_registered_device_by_tag_and_serial(self):
        dev_tag = "Lenovo_ThinkPad_T14_PF9999"

        # 1. Pull po DeviceTag-u
        status, data = self._request("GET", f"/api/devices/{dev_tag}/history")
        self.assertEqual(status, 200)
        self.assertTrue(data.get("is_registered"))
        self.assertEqual(data.get("serial_number"), "PF9999")
        self.assertEqual(data.get("manufacturer"), "Lenovo")
        self.assertEqual(data.get("model"), "ThinkPad T14")
        self.assertEqual(len(data.get("interventions")), 1)
        self.assertEqual(data.get("interventions")[0]["status"], "CRITICAL_ISSUE")
        self.assertIn("Otkaz NVMe diska", data.get("dnevnik_markdown"))

        # 2. Pull po serijskom broju
        status, data_serial = self._request("GET", "/api/devices/by-serial/PF9999/history")
        self.assertEqual(status, 200)
        self.assertTrue(data_serial.get("is_registered"))
        self.assertEqual(data_serial.get("device_tag"), dev_tag)
        self.assertEqual(data_serial.get("serial_number"), "PF9999")
        self.assertEqual(len(data_serial.get("interventions")), 1)

    def test_05_second_session_appends_to_dnevnik(self):
        dev_tag = "Lenovo_ThinkPad_T14_PF9999"
        second_payload = {
            "device_tag": dev_tag,
            "serial_number": "PF9999",
            "session_id": "sess-uuid-002",
            "timestamp": "2026-10-10T10:00:00Z",
            "serviser_id": "USB_TEREN_02",
            "hardware": {
                "manufacturer": "Lenovo",
                "model": "ThinkPad T14",
                "cpu": "Intel Core i7-1185G7",
                "ram_gb": 16.0
            },
            "summary": "Ugrađen nov Kingston KC3000 1TB NVMe disk. Kloniran sistem.",
            "status": "COMPLETED",
            "tools_executed": ["Get-DiskHealth.ps1"],
            "dnevnik_entry": "### [Datum: 2026-10-10 10:00:00] - Zamena SSD-a\n- Ugradjen nov disk 1TB.",
            "report_markdown": "# Drugi Izvestaj\nSve radi perfektno."
        }

        status, resp = self._request("POST", f"/api/devices/{dev_tag}/agy-session", second_payload)
        self.assertEqual(status, 200)

        # Provera ažurirane istorije
        status, data = self._request("GET", f"/api/devices/{dev_tag}/history")
        self.assertEqual(status, 200)
        self.assertEqual(len(data.get("interventions")), 2)
        self.assertEqual(data.get("total_interventions"), 2)
        self.assertIn("Otkaz NVMe diska", data.get("dnevnik_markdown"))
        self.assertIn("Zamena SSD-a", data.get("dnevnik_markdown"))

        # Provera FleetStore statusa - sada je uspešno servisiran
        devs = self.fleet_store.get_devices_summary()
        self.assertEqual(devs[dev_tag].get("status_label"), "Servisiran (AGY)")


if __name__ == "__main__":
    unittest.main()
