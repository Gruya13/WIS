import os
import unittest
import tempfile
import shutil
import json
import threading
import urllib.request
from http.server import ThreadingHTTPServer

from servishub.config import HubConfig, active_config
from servishub.telemetry import FleetStore
from servishub.handlers import ServisHubHandler
from servishub.model_profiles import (
    normalize_model_name,
    load_model_profile,
    update_keyboard_override,
    update_usb_override
)

class TestModelProfiles(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.test_dir = tempfile.mkdtemp()
        cls.orig_base = active_config.base_dir
        active_config.base_dir = cls.test_dir

        cls.config = HubConfig(base_dir=cls.test_dir)
        cls.fleet_store = FleetStore()

        ServisHubHandler.config = cls.config
        ServisHubHandler.fleet_store = cls.fleet_store
        ServisHubHandler.start_time = 0
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
        active_config.base_dir = cls.orig_base
        cls.httpd.shutdown()
        cls.httpd.server_close()
        shutil.rmtree(cls.test_dir, ignore_errors=True)

    def test_normalize_model_name(self):
        self.assertEqual(normalize_model_name("ThinkPad T14 Gen 2"), "thinkpad_t14_gen_2")
        self.assertEqual(normalize_model_name("HP EliteBook 840 G7/G8"), "hp_elitebook_840_g7_g8")
        self.assertEqual(normalize_model_name(""), "generic_device")

    def test_keyboard_override(self):
        model = "Dell Latitude 5420"
        profile = load_model_profile(model)
        self.assertEqual(profile["keyboard"]["removed_keys"], [])

        updated = update_keyboard_override(model, removed_keys=["Insert"], by="test_user")
        self.assertIn("Insert", updated["keyboard"]["removed_keys"])
        self.assertEqual(len(updated["history"]), 1)
        self.assertEqual(updated["history"][0]["type"], "keyboard_override")

        reloaded = load_model_profile(model)
        self.assertIn("Insert", reloaded["keyboard"]["removed_keys"])

    def test_usb_override(self):
        model = "ThinkPad X1 Carbon"
        updated = update_usb_override(model, 3, by="serviser")
        self.assertEqual(updated["usb"]["port_count"], 3)
        self.assertEqual(len(updated["history"]), 1)

        reloaded = load_model_profile(model)
        self.assertEqual(reloaded["usb"]["port_count"], 3)

    def test_http_api_endpoints(self):
        # 1. GET default profile
        url = f"{self.base_url}/api/model-profile?model=ThinkPad+T14"
        with urllib.request.urlopen(url) as resp:
            data = json.loads(resp.read().decode())
            self.assertEqual(data["model"], "ThinkPad T14")
            self.assertEqual(data["keyboard"]["removed_keys"], [])

        # 2. POST keyboard override
        req = urllib.request.Request(
            f"{self.base_url}/api/model-profile/keyboard",
            data=json.dumps({"model": "ThinkPad T14", "removed_keys": ["Insert", "F12"]}).encode(),
            headers={"Content-Type": "application/json"}
        )
        with urllib.request.urlopen(req) as resp:
            data = json.loads(resp.read().decode())
            self.assertEqual(data["status"], "ok")
            self.assertIn("Insert", data["profile"]["keyboard"]["removed_keys"])

        # 3. POST usb override
        req = urllib.request.Request(
            f"{self.base_url}/api/model-profile/usb",
            data=json.dumps({"model": "ThinkPad T14", "port_count": 4}).encode(),
            headers={"Content-Type": "application/json"}
        )
        with urllib.request.urlopen(req) as resp:
            data = json.loads(resp.read().decode())
            self.assertEqual(data["status"], "ok")
            self.assertEqual(data["profile"]["usb"]["port_count"], 4)

        # 4. GET again to ensure persistence
        with urllib.request.urlopen(url) as resp:
            data = json.loads(resp.read().decode())
            self.assertIn("Insert", data["keyboard"]["removed_keys"])
            self.assertEqual(data["usb"]["port_count"], 4)

        # 5. GET /api/hardware-spec (offline convertible heuristic)
        hw_url = f"{self.base_url}/api/hardware-spec?manufacturer=HP&model=HP+EliteBook+x360+1040+G8&is_desktop=false"
        with urllib.request.urlopen(hw_url) as resp:
            hw_data = json.loads(resp.read().decode())
            self.assertTrue(hw_data["has_touchscreen"])
            self.assertTrue(hw_data["has_touchpad"])

if __name__ == "__main__":
    unittest.main()
