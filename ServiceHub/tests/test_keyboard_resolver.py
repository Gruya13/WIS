import json
import os
import shutil
import tempfile
import threading
import unittest
import urllib.request
from http.server import ThreadingHTTPServer
from io import BytesIO
from unittest.mock import patch

from servishub.config import HubConfig, active_config
from servishub.drivers.oem.dell import DellOEMProvider
from servishub.drivers.oem.hp import HpOEMProvider
from servishub.drivers.oem.lenovo import LenovoOEMProvider
from servishub.handlers import ServisHubHandler
from servishub.keyboard_resolver import resolve_keyboard_layout
from servishub.model_profiles import load_model_profile, update_keyboard_override
from servishub.telemetry import FleetStore


class _FakeHttpResponse:
    def __init__(self, body_str: str, status: int = 200):
        self._bytes = body_str.encode("utf-8")
        self.status = status

    def read(self):
        return self._bytes

    def __enter__(self):
        return self

    def __exit__(self, exc_type, exc_val, exc_tb):
        return False


class TestKeyboardResolverAndOEM(unittest.TestCase):
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

    def test_lenovo_fru_descriptions(self):
        p1 = LenovoOEMProvider._parse_lenovo_keyboard_description("CS20-84US,KYB,US English,BL")
        self.assertFalse(p1["has_numpad"])
        self.assertFalse(p1["is_iso"])

        p2 = LenovoOEMProvider._parse_lenovo_keyboard_description("CS20-85GR,KYB,German,Backlit")
        self.assertFalse(p2["has_numpad"])
        self.assertTrue(p2["is_iso"])

        p3 = LenovoOEMProvider._parse_lenovo_keyboard_description("CS21-101US FullSize Copilot")
        self.assertTrue(p3["has_numpad"])
        self.assertFalse(p3["is_iso"])
        self.assertTrue(p3["has_copilot"])

        p4 = LenovoOEMProvider._parse_lenovo_keyboard_description("CS21-102SRB Serbian")
        self.assertTrue(p4["has_numpad"])
        self.assertTrue(p4["is_iso"])

    def test_lenovo_mock_api_and_disk_cache(self):
        cache_dir = tempfile.mkdtemp(dir=self.test_dir)
        provider = LenovoOEMProvider(cache_dir=cache_dir, timeout=2.0)

        payload = json.dumps({
            "data": {
                "parts": [
                    {"category": "LCD", "description": "14 FHD IPS"},
                    {"category": "KEYBOARDS INTERNAL", "description": "CS20-85SRB BL Backlit"},
                ]
            }
        })

        with patch("urllib.request.urlopen", return_value=_FakeHttpResponse(payload)) as m_open:
            spec = provider.lookup_keyboard_spec(serial="PF2ABC12", model="ThinkPad T14 Gen 2", product_number="20W00012AD")
            self.assertEqual(spec["status"], "ok")
            self.assertEqual(spec["source"], "lenovo_parts_api")
            self.assertEqual(spec["base_layout"], "thinkpad-tkl-iso")
            self.assertTrue(spec["is_iso"])
            self.assertFalse(spec["has_numpad"])
            self.assertIn("Oem102", spec["added_keys"])
            self.assertEqual(m_open.call_count, 1)

        # Drugi poziv čita iz keša bez mreže
        with patch("urllib.request.urlopen", side_effect=RuntimeError("Offline")) as m_open2:
            cached = provider.lookup_keyboard_spec(serial="PF2ABC12", model="ThinkPad T14 Gen 2", product_number="20W00012AD")
            self.assertEqual(cached["base_layout"], "thinkpad-tkl-iso")
            self.assertEqual(m_open2.call_count, 0)

    def test_dell_kybd_sku_and_series_rules(self):
        d1 = DellOEMProvider._parse_dell_kybd_description("KYBD,82,US-INTL")
        self.assertFalse(d1["has_numpad"])
        self.assertFalse(d1["is_iso"])

        d2 = DellOEMProvider._parse_dell_kybd_description("KYBD,83,GER,BL")
        self.assertFalse(d2["has_numpad"])
        self.assertTrue(d2["is_iso"])

        d3 = DellOEMProvider._parse_dell_kybd_description("KYBD,101,US")
        self.assertTrue(d3["has_numpad"])
        self.assertFalse(d3["is_iso"])

        d4 = DellOEMProvider._parse_dell_kybd_description("KYBD,102,UK")
        self.assertTrue(d4["has_numpad"])
        self.assertTrue(d4["is_iso"])

        # Numeracija modela: 5420 -> 14" TKL, 5530 -> 15.6" Numpad
        self.assertFalse(DellOEMProvider._infer_dell_series_layout("Latitude 5420")["has_numpad"])
        self.assertTrue(DellOEMProvider._infer_dell_series_layout("Latitude 5530")["has_numpad"])
        self.assertTrue(DellOEMProvider._infer_dell_series_layout("Precision 3571")["has_numpad"])
        self.assertFalse(DellOEMProvider._infer_dell_series_layout("XPS 13 9310")["has_numpad"])

        cache_dir = tempfile.mkdtemp(dir=self.test_dir)
        provider = DellOEMProvider(cache_dir=cache_dir, timeout=2.0)
        dell_json = json.dumps({"parts": [{"partDescription": "KYBD,103,ADRIATIC,BL"}]})
        with patch("urllib.request.urlopen", return_value=_FakeHttpResponse(dell_json)):
            spec = provider.lookup_keyboard_spec(serial="7XYZ123", model="Latitude 5530")
            self.assertEqual(spec["base_layout"], "laptop-numpad-iso")
            self.assertTrue(spec["has_numpad"])
            self.assertTrue(spec["is_iso"])

    def test_hp_partsurfer_and_sku_suffix(self):
        self.assertFalse(HpOEMProvider._parse_hp_sku_suffix("3G0E1AV#ABA"))
        self.assertTrue(HpOEMProvider._parse_hp_sku_suffix("3G0E1AV#ABD"))
        self.assertTrue(HpOEMProvider._parse_hp_sku_suffix("5Y2B9EA#BED"))
        self.assertTrue(HpOEMProvider._parse_hp_sku_suffix("5Y2B9EA#AKN"))
        self.assertIsNone(HpOEMProvider._parse_hp_sku_suffix("5Y2B9EA"))

        cache_dir = tempfile.mkdtemp(dir=self.test_dir)
        provider = HpOEMProvider(cache_dir=cache_dir, timeout=2.0)
        html_bom = """
        <table>
          <tr><td>M07145-001</td><td>Battery 3-cell</td></tr>
          <tr><td>M36312-B11</td><td>Top cover with keyboard, backlit, Adriatic, without numeric keypad</td></tr>
        </table>
        """
        with patch("urllib.request.urlopen", return_value=_FakeHttpResponse(html_bom)):
            spec = provider.lookup_keyboard_spec(serial="5CD12345XY", model="HP EliteBook 840 G8", product_number="3G0E1AV#BED")
            self.assertEqual(spec["source"], "hp_partsurfer")
            self.assertEqual(spec["base_layout"], "laptop-tkl-iso")
            self.assertFalse(spec["has_numpad"])
            self.assertTrue(spec["is_iso"])
            self.assertIn("Home", spec["added_keys"])
            self.assertIn("Oem102", spec["added_keys"])

        # Offline WAF fallback sa SKU sufiksom i modelom
        with patch("urllib.request.urlopen", side_effect=TimeoutError("WAF timeout")):
            offline = provider.lookup_keyboard_spec(serial="5CD99999AA", model="HP ProBook 450 G9", product_number="6F123EA#ABA")
            self.assertEqual(offline["source"], "hp_sku_series_catalog")
            self.assertEqual(offline["base_layout"], "laptop-numpad-ansi")
            self.assertTrue(offline["has_numpad"])
            self.assertFalse(offline["is_iso"])

    def test_resolver_priority_and_http_endpoints(self):
        # 1. Offline heuristika po dijagonali i os_oem102 kad OEM nije poznat
        heur = resolve_keyboard_layout(
            manufacturer="UnknownVendor",
            model="CustomBook 15",
            screen_inches=15.6,
            os_oem102=True,
        )
        self.assertEqual(heur["base_layout"], "laptop-numpad-iso")
        self.assertTrue(heur["has_numpad"])
        self.assertTrue(heur["is_iso"])

        # 2. HTTP GET /api/keyboard-layout (HP offline SKU fallback, bez izlaza na internet)
        url = (
            f"{self.base_url}/api/keyboard-layout?"
            "manufacturer=HP&model=HP+ProBook+450+G9&sku=6F123EA%23BED&screen_inches=15.6"
        )
        with urllib.request.urlopen(url) as resp:
            data = json.loads(resp.read().decode("utf-8"))
            self.assertEqual(data["base_layout"], "laptop-numpad-iso")
            self.assertTrue(data["is_iso"])
            self.assertTrue(data["has_numpad"])

        # 3. POST /api/model-profile/keyboard ima prednost nad svime nakon potvrde
        req = urllib.request.Request(
            f"{self.base_url}/api/model-profile/keyboard",
            data=json.dumps({
                "model": "HP ProBook 450 G9",
                "base_layout": "laptop-numpad-ansi",
                "added_keys": ["Copilot"],
                "removed_keys": ["Oem102"],
                "is_iso": False,
                "has_numpad": True,
                "confirmed": True,
            }).encode("utf-8"),
            headers={"Content-Type": "application/json"},
        )
        with urllib.request.urlopen(req) as resp:
            post_res = json.loads(resp.read().decode("utf-8"))
            self.assertEqual(post_res["status"], "ok")

        # Sada GET /api/keyboard-layout mora vratiti sačuvani profil umesto SKU heuristike
        with urllib.request.urlopen(url) as resp:
            confirmed = json.loads(resp.read().decode("utf-8"))
            self.assertEqual(confirmed["source"], "model_profile")
            self.assertEqual(confirmed["base_layout"], "laptop-numpad-ansi")
            self.assertFalse(confirmed["is_iso"])
            self.assertIn("Copilot", confirmed["added_keys"])
            self.assertIn("Oem102", confirmed["removed_keys"])

        # 4. Provera rešavanja konflikta added_keys vs removed_keys u model_profiles
        update_keyboard_override("ConflictModel", removed_keys=["Oem102"], base_layout="laptop-tkl-ansi")
        updated = update_keyboard_override("ConflictModel", added_keys=["Oem102"], base_layout="laptop-tkl-iso")
        self.assertIn("Oem102", updated["keyboard"]["added_keys"])
        self.assertNotIn("Oem102", updated["keyboard"]["removed_keys"])
        reloaded = load_model_profile("ConflictModel")
        self.assertEqual(reloaded["keyboard"]["base_layout"], "laptop-tkl-iso")

        # 5. HP EliteBook 840 G8 (G8 ima nav kolonu -> laptop-tkl-nav-iso) vs 630 G9 (nema nav kolonu -> laptop-tkl-iso)
        with patch("urllib.request.urlopen", side_effect=TimeoutError("Offline")):
            res_840 = resolve_keyboard_layout(
                manufacturer="HP",
                model="HP EliteBook 840 G8 Notebook PC",
                serial="5CG2238Y18",
                sku="6L7S4EC#UUG",
                screen_inches=13.9,
                is_desktop=False,
                os_oem102=True,
            )
            self.assertEqual(res_840["base_layout"], "laptop-tkl-nav-iso")
            self.assertTrue(res_840["has_nav_column"])
            self.assertTrue(res_840["is_iso"])
            self.assertNotIn("Home", res_840["added_keys"])

            res_630 = resolve_keyboard_layout(
                manufacturer="HP",
                model="HP EliteBook 630 13 inch G9 Notebook PC",
                serial="5CG0000001",
                sku="6F123EA#BED",
                screen_inches=13.3,
                is_desktop=False,
                os_oem102=True,
            )
            self.assertEqual(res_630["base_layout"], "laptop-tkl-iso")
            self.assertFalse(res_630["has_nav_column"])
            self.assertNotIn("Home", res_630["added_keys"])

    def test_thinkpad_x380_t16_and_legacy_profile_migration(self):
        # ThinkPad X380 Yoga (TKL 13.3") i ThinkPad T16 (Numpad 16") moraju dobiti thinkpad-* šablon
        with patch("urllib.request.urlopen", side_effect=TimeoutError("Offline")):
            res_x380 = resolve_keyboard_layout(
                manufacturer="LENOVO",
                model="ThinkPad X380 Yoga",
                serial="MP1ABC99",
                sku="LENOVO_MT_20LH",
                screen_inches=13.3,
                is_desktop=False,
                os_oem102=False,
            )
            self.assertEqual(res_x380["base_layout"], "thinkpad-tkl-ansi")
            self.assertFalse(res_x380["has_numpad"])
            self.assertFalse(res_x380["has_nav_column"])

            res_t16 = resolve_keyboard_layout(
                manufacturer="LENOVO",
                model="ThinkPad T16 Gen 2",
                serial="PF9T1699",
                sku="LENOVO_MT_21HH",
                screen_inches=16.0,
                is_desktop=False,
                os_oem102=True,
            )
            self.assertEqual(res_t16["base_layout"], "thinkpad-numpad-iso")
            self.assertTrue(res_t16["has_numpad"])

        # Zastareli potvrđeni profil ThinkPad modela sa laptop-tkl-iso i dodatim Home/End/PgUp/PgDn/PrtSc
        # mora biti automatski migriran na thinkpad-tkl-iso uz čišćenje tih 5 dupliranih tastera.
        update_keyboard_override(
            "ThinkPad X380 Yoga Legacy",
            added_keys=["Home", "End", "PageUp", "PageDown", "PrintScreen", "Apps"],
            removed_keys=[],
            base_layout="laptop-tkl-iso",
            is_iso=True,
            has_numpad=False,
            confirmed=True,
        )
        migrated = resolve_keyboard_layout(
            manufacturer="LENOVO",
            model="ThinkPad X380 Yoga Legacy",
            serial="MP1OLD01",
            sku="LENOVO_MT_20LH",
            screen_inches=13.3,
            is_desktop=False,
            os_oem102=True,
        )
        self.assertEqual(migrated["source"], "model_profile")
        self.assertEqual(migrated["base_layout"], "thinkpad-tkl-iso")
        for dup_key in ("Home", "End", "PageUp", "PageDown", "PrintScreen"):
            self.assertNotIn(dup_key, migrated["added_keys"])
        self.assertIn("Apps", migrated["added_keys"])


if __name__ == "__main__":
    unittest.main()
