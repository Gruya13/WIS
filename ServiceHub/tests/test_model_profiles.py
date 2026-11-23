import os
import unittest
import tempfile
import shutil
import json
import threading
import urllib.error
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

    def test_v343_06_autolearn_rule1_5_of_4_physical_locations(self):
        model = "Lenovo ThinkPad T14 Gen 5"
        update_usb_override(model, 4, by="serviser")
        locs = [
            "PCIROOT(0)#PCI(1400)#USBROOT(0)#USB(1)",
            "PCIROOT(0)#PCI(1400)#USBROOT(0)#USB(2)",
            "PCIROOT(0)#PCI(1400)#USBROOT(0)#USB(3)",
            "PCIROOT(0)#PCI(1400)#USBROOT(0)#USB(4)",
            "ACPI(_SB_)#ACPI(PC00)#ACPI(XHCI)#ACPI(RHUB)#ACPI(HS05)",
        ]
        updated = update_usb_override(
            model_name=model,
            port_count=4,
            target_ports=4,
            detected_ports=5,
            physical_port_locations=locs,
            charger_ports_count=0,
            manual_override=False,
            serial="PF111111",
        )
        self.assertEqual(updated["usb"]["port_count"], 5)
        self.assertTrue(updated["usb"]["confirmed"])
        self.assertEqual(updated["usb"]["source"], "autolearn")
        self.assertEqual(len(updated["usb"]["known_port_locations"]), 5)
        self.assertEqual(updated["history"][-1]["type"], "usb_autolearn")
        self.assertEqual(updated["history"][-1]["by"], "serviser_autolearn")

    def test_v343_06_anti_poisoning_charger_and_multi_serial_rule2(self):
        model = "ThinkPad L16 Gen 2"
        # Potvrđeni model sa 4 porta
        update_usb_override(model, 4, by="serviser")

        # Jedan uređaj sa 3 fizičke lokacije + 3 replugovana USB-C punjača (detected=6) i otrovanom AUTO-USBC-CHARGER stavkom
        locs_3 = [
            "PCIROOT(0)#PCI(1400)#USBROOT(0)#USB(13)",
            "PCIROOT(0)#PCI(1400)#USBROOT(0)#USB(9)",
            "PCIROOT(0)#PCI(1400)#USBROOT(0)#USB(14)",
            "AUTO-USBC-CHARGER#1",
            "AUTO-USBC-CHARGER#2",
        ]
        p1 = update_usb_override(
            model_name=model,
            target_ports=4,
            detected_ports=6,
            physical_port_locations=locs_3,
            charger_ports_count=3,
            max_typec_cap=2,
            manual_override=False,
            serial="PW0LPRLT",
        )
        # Ne sme podići potvrđeni 4-portni model ni na 5 ni na 6 sa samo 1 serijskog broja!
        self.assertEqual(p1["usb"]["port_count"], 4)
        self.assertEqual(len(p1["usb"]["known_port_locations"]), 3)
        self.assertTrue(all("AUTO-USBC-CHARGER" not in x for x in p1["usb"]["known_port_locations"]))

        # Drugi prolaz sa ISTIM serijskim brojem i dalje ne sme podići profil
        p1_dup = update_usb_override(
            model_name=model,
            target_ports=4,
            detected_ports=5,
            physical_port_locations=locs_3[:3],
            charger_ports_count=2,
            max_typec_cap=2,
            manual_override=False,
            serial="PW0LPRLT",
        )
        self.assertEqual(p1_dup["usb"]["port_count"], 4)

        # Drugi RAZLIČITI serijski broj potvrđuje effective_ports = 3 + 2 = 5
        p2 = update_usb_override(
            model_name=model,
            target_ports=4,
            detected_ports=5,
            physical_port_locations=locs_3[:3],
            charger_ports_count=2,
            max_typec_cap=2,
            manual_override=False,
            serial="PW0LPRLU",
        )
        self.assertEqual(p2["usb"]["port_count"], 5)
        self.assertEqual(p2["usb"]["source"], "autolearn")
        self.assertTrue(p2["usb"]["confirmed"])

        # Nepotvrđen nov model odmah prihvata effective_ports (3 + min(3, 2) = 5)
        unconfirmed_model = "HP EliteBook 840 G11"
        pu = update_usb_override(
            model_name=unconfirmed_model,
            detected_ports=6,
            physical_port_locations=locs_3[:3],
            charger_ports_count=3,
            manual_override=False,
            serial="5CD00001",
        )
        self.assertEqual(pu["usb"]["port_count"], 5)
        self.assertEqual(pu["usb"]["source"], "autolearn")
        self.assertTrue(pu["usb"]["confirmed"])

    def test_v343_06_rule3_manual_override_down_then_rule1_recovers_up(self):
        model = "HP ProBook 450 G10"
        update_usb_override(model, 4, by="serviser")
        down = update_usb_override(model, 3, manual_override=True, by="serviser")
        self.assertEqual(down["usb"]["port_count"], 3)
        self.assertEqual(down["usb"]["source"], "manual_override")
        self.assertTrue(down["usb"]["confirmed"])

        locs_4 = [
            "PCIROOT(0)#PCI(1400)#USBROOT(0)#USB(1)",
            "PCIROOT(0)#PCI(1400)#USBROOT(0)#USB(2)",
            "PCIROOT(0)#PCI(1400)#USBROOT(0)#USB(3)",
            "PCIROOT(0)#PCI(1400)#USBROOT(0)#USB(4)",
        ]
        up = update_usb_override(
            model_name=model,
            physical_port_locations=locs_4,
            charger_ports_count=0,
            manual_override=False,
            serial="5CD99901",
        )
        self.assertEqual(up["usb"]["port_count"], 4)
        self.assertEqual(up["usb"]["source"], "autolearn")

    def test_v343_06_http_usb_override_endpoint_validation_and_limits(self):
        model = "Dell Latitude 5440"
        # 1. Test /api/model-profile/usb-override endpoint sa auto-learn payload-om
        req = urllib.request.Request(
            f"{self.base_url}/api/model-profile/usb-override",
            data=json.dumps({
                "model": model,
                "target_ports": 3,
                "detected_ports": 4,
                "physical_port_locations": [
                    "PCIROOT(0)#PCI(1400)#USBROOT(0)#USB(1)",
                    "PCIROOT(0)#PCI(1400)#USBROOT(0)#USB(2)",
                    "PCIROOT(0)#PCI(1400)#USBROOT(0)#USB(3)",
                    "PCIROOT(0)#PCI(1400)#USBROOT(0)#USB(4)",
                ],
                "charger_ports_count": 0,
                "manual_override": False,
                "serial": "ABC1234",
            }).encode(),
            headers={"Content-Type": "application/json"},
        )
        with urllib.request.urlopen(req) as resp:
            data = json.loads(resp.read().decode())
            self.assertEqual(data["status"], "ok")
            self.assertEqual(data["profile"]["usb"]["port_count"], 4)
            self.assertEqual(data["profile"]["usb"]["source"], "autolearn")

        # 2. Anti-DoS: observed_counts zadržava max 20 zapisa
        for idx in range(25):
            update_usb_override(
                model_name=model,
                detected_ports=4,
                physical_port_locations=[
                    "PCIROOT(0)#PCI(1400)#USBROOT(0)#USB(1)",
                ],
                charger_ports_count=1,
                manual_override=False,
                serial=f"SN{idx:03d}",
            )
        reloaded = load_model_profile(model)
        self.assertEqual(len(reloaded["usb"]["observed_counts"]), 20)
        self.assertEqual(reloaded["usb"]["observed_counts"][-1]["serial"], "SN024")

        # 3. Validacija neispravnog payloada vraća 400
        bad_req = urllib.request.Request(
            f"{self.base_url}/api/model-profile/usb",
            data=json.dumps({"model": model, "port_count": 999}).encode(),
            headers={"Content-Type": "application/json"},
        )
        with self.assertRaises(urllib.error.HTTPError) as ctx:
            urllib.request.urlopen(bad_req)
        self.assertEqual(ctx.exception.code, 400)
        ctx.exception.close()

if __name__ == "__main__":
    unittest.main()
