"""
tests.test_api_integration
===========================
Integracioni testovi celokupnog ServisHub HTTP REST API interfejsa.
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
from servishub.payload import PayloadManager
from tests.release_fixture import make_release
from servishub.drivers.cache import DriverCacheManager
from servishub.drivers.scraper import MsUpdateCatalogScraper
from servishub.handlers import ServisHubHandler
from servishub.version import VERSION


class TestApiIntegration(unittest.TestCase):

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
        cls.driver_cache_mgr = DriverCacheManager(cache_dir=cls.config.drivers_cache_dir)
        cls.ms_catalog_scraper = MsUpdateCatalogScraper()
        cache, key, public, bundle, meta = make_release(cls.test_dir)
        cls.payload_mgr = PayloadManager(base_dir=cls.test_dir, cache_dir=cache, public_key=public)

        ServisHubHandler.fleet_store = cls.fleet_store
        ServisHubHandler.config = cls.config
        ServisHubHandler.payload_mgr = cls.payload_mgr
        ServisHubHandler.driver_cache_mgr = cls.driver_cache_mgr
        ServisHubHandler.ms_catalog_scraper = cls.ms_catalog_scraper

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
        body = json.dumps(data).encode("utf-8") if data else None
        req = urllib.request.Request(url, data=body, method=method)
        req.add_header("Content-Type", "application/json")
        with urllib.request.urlopen(req, timeout=5) as resp:
            content_type = resp.headers.get("Content-Type", "")
            raw = resp.read()
            if "application/json" in content_type:
                return resp.status, json.loads(raw.decode("utf-8"))
            return resp.status, raw

    def test_01_health_and_dashboard(self):
        status, data = self._request("GET", "/api/health")
        self.assertEqual(status, 200)
        self.assertEqual(data.get("status"), "ok")
        self.assertEqual(data.get("version"), VERSION)

        req = urllib.request.Request(f"{self.base_url}/")
        with urllib.request.urlopen(req, timeout=5) as resp:
            self.assertEqual(resp.status, 200)
            self.assertEqual(resp.headers.get("Content-Type"), "text/html; charset=utf-8")
            self.assertEqual(resp.headers.get("Cache-Control"), "no-cache, no-store, must-revalidate")
            self.assertEqual(resp.headers.get("Pragma"), "no-cache")
            self.assertEqual(resp.headers.get("Expires"), "0")
            body = resp.read()
            html_text = body.decode("utf-8")
            self.assertIn("<!DOCTYPE html>", html_text)
            self.assertIn("<title>Tehno-Kutak | ServisHub Centralni Sistem</title>", html_text)
            self.assertIn("ServisHub v" + VERSION, html_text)
            # Navigacija i tabovi
            self.assertIn('id="tab-btn-fleet"', html_text)
            self.assertIn('id="tab-btn-wis"', html_text)
            self.assertIn('id="tab-btn-updates"', html_text)
            self.assertIn('id="tab-fleet"', html_text)
            self.assertIn('id="tab-wis"', html_text)
            self.assertIn('id="tab-updates"', html_text)
            # Fleet statistika i grid
            self.assertIn('id="fleet-grid"', html_text)
            self.assertIn('id="stat-total"', html_text)
            self.assertIn('id="stat-active"', html_text)
            self.assertIn('id="stat-done"', html_text)
            # WIS Centar
            self.assertIn('id="wis-iso-filename"', html_text)
            self.assertIn('id="wis-usb-select"', html_text)
            self.assertIn('id="wis-confirm-wipe"', html_text)
            self.assertIn('id="btn-burn-usb"', html_text)
            self.assertIn('id="wis-burn-progress-box"', html_text)
            # Windows 11 Zakrpe
            self.assertIn('id="updates-status-text"', html_text)
            self.assertIn('id="updates-table-body"', html_text)
            self.assertIn('id="updates-search"', html_text)
            # Modali
            self.assertIn('id="log-modal"', html_text)
            self.assertIn('id="report-modal"', html_text)
            self.assertIn('id="history-modal"', html_text)

    def test_02_heartbeat_and_devices(self):
        hb = {
            "computer_name": "TK-LAPTOP-TEST01",
            "phase": 1,
            "worker_progress": 25,
            "cpu_percent": 34,
            "cpu_temp": 58.5,
            "ram_used": "5.4 GB",
            "ram_total": "16.0 GB",
            "ram_percent": 33,
            "hw": {"manufacturer": "Dell", "model": "Latitude 5420"}
        }
        status, data = self._request("POST", "/api/heartbeat", hb)
        self.assertEqual(status, 200)
        self.assertEqual(data.get("status"), "ok")

        status, devs = self._request("GET", "/api/devices")
        self.assertEqual(status, 200)
        self.assertIn("TK-LAPTOP-TEST01", devs)
        dev = devs["TK-LAPTOP-TEST01"]
        self.assertEqual(dev["computer_name"], "TK-LAPTOP-TEST01")
        self.assertEqual(dev["cpu_percent"], 34)
        self.assertEqual(dev["cpu_temp"], 58.5)
        self.assertEqual(dev["ram_used"], "5.4 GB")
        self.assertEqual(dev["ram_total"], "16.0 GB")
        self.assertEqual(dev["ram_percent"], 33)

    def test_03_logging_and_retrieval(self):
        log_payload = {
            "computer_name": "TK-LAPTOP-TEST01",
            "lines": [
                "[STEP] Faza 1: Provera mrežne veze...",
                "[SUCCESS] LAN mrežna veza uspostavljena (1 Gbps)"
            ]
        }
        status, res = self._request("POST", "/api/log", log_payload)
        self.assertEqual(status, 200)
        self.assertEqual(res.get("appended"), 2)

        status, logs = self._request("GET", "/api/devices/TK-LAPTOP-TEST01/logs")
        self.assertEqual(status, 200)
        self.assertGreaterEqual(len(logs), 2)
        self.assertTrue(any("LAN mrežna veza" in l for l in logs))

    def test_04_session_state(self):
        sess_data = {
            "computer_name": "TK-LAPTOP-TEST01",
            "phase": 2,
            "sub_phase": "Windows Update",
            "progress_percent": 50,
            "status_message": "Instalacija ažuriranja..."
        }
        status, res = self._request("POST", "/api/session/sess_123/state", sess_data)
        self.assertEqual(status, 200)
        self.assertTrue(res.get("ok"))

        status, sessions = self._request("GET", "/api/sessions")
        self.assertEqual(status, 200)
        self.assertGreater(sessions.get("total", 0), 0)

    def test_05_lifecycle_events(self):
        start_payload = {
            "event": "STARTED",
            "computer_name": "TK-LAPTOP-TEST01",
            "autopilot": {"status": "Clean"}
        }
        status, res = self._request("POST", "/api/lifecycle", start_payload)
        self.assertEqual(status, 200)
        self.assertEqual(res.get("event"), "STARTED")

        comp_payload = {
            "event": "COMPLETED",
            "computer_name": "TK-LAPTOP-TEST01",
            "duration_formatted": "12m 45s",
            "duration_seconds": 765,
            "apps": {"Chrome": "Completed", "Office": "Completed"}
        }
        status, res = self._request("POST", "/api/lifecycle", comp_payload)
        self.assertEqual(status, 200)
        self.assertEqual(res.get("event"), "COMPLETED")
        self.assertTrue(res.get("report_saved"))

    def test_06_payload_version(self):
        status, meta = self._request("GET", "/api/payload/version")
        self.assertEqual(status, 200)
        self.assertIn("version", meta)
        self.assertIn("sha256", meta)

    def test_07_updates_status(self):
        status, data = self._request("GET", "/api/updates/status")
        self.assertEqual(status, 200)
        self.assertTrue(data.get("enabled"))
        self.assertIn("available", data)
        self.assertIn("total_packages", data)
        self.assertIn("packages", data)
        self.assertIn("kb_list", data)


if __name__ == "__main__":
    unittest.main()
