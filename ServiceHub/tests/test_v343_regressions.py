"""
tests.test_v343_regressions
===========================
Regresioni jedinični i integracioni testovi za WIS v3.4.3 / ServiceHub v3.6.3:
- V343-02: Windows polja licenciranja (`windows_oa3_present`, `windows_license_method`,
  `windows_edition`, `windows_license_detail`), maskiranje ključeva i Markdown karton.
- V343-03: Preskočeni/nezavršeni interaktivni `hwtest.tests` forsiraju `NEEDS_SERVICE`
  i `⚠️ NEPOTPUNO (preskočeni interaktivni testovi)`, dok automatski `SKIP` zadržava `READY_WITH_NOTE`.
- V343-04: `hardware_spec` `schema_version == 3`, `has_keyboard_backlight` /
  `keyboard_backlight_evidence` (HP primary non-backlit, `BL-RGB`, Lenovo/Dell bez lažnog `\bBL\b`).
- V343-06: Samoučenje USB portova (`known_port_locations`, `observed_counts`, Pravila 1–3,
  anti-poisoning za `AUTO-USBC-CHARGER`, validacija i HTTP API).
"""

import json
import os
import shutil
import tempfile
import threading
import unittest
import urllib.error
import urllib.request
from http.server import ThreadingHTTPServer
from unittest.mock import patch

from servishub.config import HubConfig, active_config
from servishub.handlers import ServisHubHandler
from servishub.reports import compute_handover_status, generate_markdown_report
from servishub.telemetry import FleetStore


class TestV343Regressions(unittest.TestCase):
    def setUp(self):
        self.tmp_dir = tempfile.mkdtemp(prefix="wis_v343_test_")
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
        ServisHubHandler.start_time = 0

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

    def test_01_v343_02_windows_licensing_e2e_report_masking_and_history(self):
        """V343-02: /api/devices/<id>/report čuva Windows licencna polja uz maskiranje ključeva na disku i u istoriji."""
        raw_win_key = "VK7JG-NPHTM-C97JM-9MPGT-3V66T"
        raw_off_key = "FXYTK-NJJ8C-GB6DW-3DYQT-6F7TH"
        payload = {
            "device_id": "TK-MINI-PC-YJ02G0RF",
            "computer_name": "DESKTOP-B9UQHQL",
            "serial_number": "YJ02G0RF",
            "status": "COMPLETED",
            "diagnostics_status": "OK",
            "cooling_verified": True,
            "manual_check_required": False,
            "windows_activated": False,
            "windows_oa3_present": False,
            "windows_edition": "Windows 11 Pro",
            "windows_license_method": "HWID",
            "windows_license_detail": f"Pokusaj sa {raw_win_key} neuspesan: 0xC004C003",
            "office_installed": True,
            "office_activated": False,
            "office_activation_mode": "Auto",
            "office_license_method": "KMS",
            "office_license_detail": f"OSPP /inpkey {raw_off_key} -> 0x4004F00C",
        }
        code, resp = self._post_json("/api/devices/TK-MINI-PC-YJ02G0RF/report", payload)
        self.assertEqual(code, 200)
        self.assertEqual(resp.get("status"), "ok")
        self.assertTrue(resp.get("json_file"))
        self.assertTrue(resp.get("md_file"))

        # Proveri sve sačuvane fajlove u reports_dir
        files = os.listdir(self.config.reports_dir)
        json_files = [fn for fn in files if fn.endswith(".json")]
        md_files = [fn for fn in files if fn.endswith(".md")]
        self.assertEqual(len(json_files), 1)
        self.assertEqual(len(md_files), 1)

        with open(os.path.join(self.config.reports_dir, json_files[0]), "r", encoding="utf-8") as jf:
            json_txt = jf.read()
        with open(os.path.join(self.config.reports_dir, md_files[0]), "r", encoding="utf-8") as mf:
            md_txt = mf.read()

        self.assertNotIn(raw_win_key, json_txt)
        self.assertNotIn(raw_off_key, json_txt)
        self.assertNotIn(raw_win_key, md_txt)
        self.assertNotIn(raw_off_key, md_txt)
        self.assertIn("*****-*****-*****-*****-3V66T", md_txt)
        self.assertIn("- **Windows Edicija:** Windows 11 Pro", md_txt)
        self.assertIn("- **BIOS OA3x OEM Ključ:** Nije pronađen", md_txt)
        self.assertIn("- **Metod Aktivacije (Windows):** HWID", md_txt)

        # Istorija uređaja po SN takođe ne sme sadržati sirovi ključ
        h_code, hist = self._get_json("/api/devices/by-serial/YJ02G0RF/history")
        self.assertEqual(h_code, 200)
        self.assertNotIn(raw_win_key, json.dumps(hist))
        self.assertNotIn(raw_off_key, json.dumps(hist))

    def test_02_v343_03_interactive_skip_vs_automated_skip_in_fleet_and_markdown(self):
        """V343-03: 8/8 preskočenih interaktivnih testova -> NEEDS_SERVICE; preskočen samo auto_gpu -> READY_WITH_NOTE."""
        # 1. Realni scenario sa TK-LAPTOP-PW0LPRLT (8 preskočenih interaktivnih testova)
        rec_laptop = {
            "event": "COMPLETED",
            "device_id": "TK-LAPTOP-PW0LPRLT",
            "computer_name": "TK-LAPTOP-PW0LPRLT",
            "serial_number": "PW0LPRLT",
            "status": "COMPLETED",
            "diagnostics_status": "OK",
            "cooling_verified": True,
            "manual_check_required": False,
            "hwtest": {
                "summary": {"overall": "PASS_WITH_SKIP", "pass": 7, "warnings": 1, "fail": 0, "skip": 8, "na": 3},
                "tests": [
                    {"id": "auto_cpu", "automated": True, "status": "PASS"},
                    {"id": "auto_network", "automated": True, "status": "PASS", "warning": True},
                    {"id": "display", "automated": False, "status": "SKIP", "reason": "Preskočeno u okviru Faze 5"},
                    {"id": "keyboard", "automated": False, "status": "SKIP", "reason": "Preskočeno u okviru Faze 5"},
                ],
            },
        }
        self._post_json("/api/lifecycle", rec_laptop)
        summary = self.fleet_store.get_devices_summary()["TK-LAPTOP-PW0LPRLT"]
        self.assertEqual(summary["handover_status"], "NEEDS_SERVICE")
        md_laptop = generate_markdown_report(rec_laptop)
        self.assertIn("⚠️ NEPOTPUNO (preskočeni interaktivni testovi)", md_laptop)
        self.assertNotIn("- **Ukupan Ishod:** ✅ PASS", md_laptop)

        # 2. Scenario gde je preskočen samo automatski test (npr. auto_gpu), a svi interaktivni su PASS
        rec_auto = {
            "device_id": "TK-LAPTOP-AUTOGPU",
            "computer_name": "TK-LAPTOP-AUTOGPU",
            "status": "COMPLETED",
            "diagnostics_status": "OK",
            "cooling_verified": True,
            "manual_check_required": False,
            "hwtest": {
                "summary": {"overall": "PASS_WITH_SKIP", "pass": 14, "fail": 0, "skip": 1, "na": 0},
                "tests": [
                    {"id": "auto_gpu", "automated": True, "status": "SKIP", "reason": "Nema dGPU senzora"},
                    {"id": "display", "automated": False, "status": "PASS"},
                    {"id": "usb", "automated": False, "status": "PASS"},
                ],
            },
        }
        self.assertEqual(compute_handover_status(rec_auto), "READY_WITH_NOTE")
        md_auto = generate_markdown_report(rec_auto)
        self.assertIn("- **Ukupan Ishod:** ✅ PASS", md_auto)
        self.assertNotIn("NEPOTPUNO", md_auto)

    def test_03_v343_04_http_hardware_spec_backlight_and_v2_cache_invalidation(self):
        """V343-04: GET /api/hardware-spec vraća has_keyboard_backlight i invalidira v2 keš."""
        cache_dir = os.path.join(self.tmp_dir, "storage", "hw_spec_cache")
        os.makedirs(cache_dir, exist_ok=True)
        with open(os.path.join(cache_dir, "HP_5CD211C2BK.json"), "w", encoding="utf-8") as f:
            json.dump({"schema_version": 2, "bom_loaded": True, "source": "hp_partsurfer_bom"}, f)

        omen_bom = {
            "Body": {
                "SerialNumberBOM": {
                    "unit_configuration": [
                        {"part_number": "M44444-001", "part_description": "ASSY, KBD WTL ISK PT CP BL-RGB VK SL"},
                        {"part_number": "M11111-001", "part_description": "PNL, 15.6 FHD AG 144Hz"},
                    ],
                    "spare_part": [],
                }
            }
        }
        with patch("servishub.hardware_spec._http_get_json", return_value=omen_bom):
            code, spec = self._get_json(
                "/api/hardware-spec?manufacturer=HP&model=OMEN+Laptop+15-en1xxx&serial=5CD211C2BK&is_desktop=false"
            )
        self.assertEqual(code, 200)
        self.assertEqual(spec["schema_version"], 3)
        self.assertTrue(spec["has_keyboard_backlight"])
        self.assertIn("BL-RGB", spec["keyboard_backlight_evidence"])

        # Proveri da je na disku sada sačuvan v3 keš i da drugi poziv koristi hub_cache bez HTTP upita
        with patch("servishub.hardware_spec._http_get_json", side_effect=AssertionError("Ne sme zvati OEM API na v3 keš")):
            code2, spec2 = self._get_json(
                "/api/hardware-spec?manufacturer=HP&model=OMEN+Laptop+15-en1xxx&serial=5CD211C2BK&is_desktop=false"
            )
        self.assertEqual(code2, 200)
        self.assertEqual(spec2["schema_version"], 3)
        self.assertTrue(spec2["has_keyboard_backlight"])
        self.assertIn("hub_cache", spec2["source"])

    def test_04_v343_06_usb_autolearn_union_across_units_and_input_validation(self):
        """V343-06: Unija fizičkih PCIROOT lokacija kroz više primeraka i validacija granica preko HTTP API-ja."""
        model = "Lenovo ThinkPad E16 Gen 2"
        # Početni ručni cilj: 4 porta
        self._post_json("/api/model-profile/usb", {"model": model, "port_count": 4})

        # Primerak 1 vidi lokacije L1, L2, L3 + punjač (ne diže potvrđen profil samostalno na 5)
        self._post_json("/api/model-profile/usb-override", {
            "model": model,
            "target_ports": 4,
            "detected_ports": 4,
            "physical_port_locations": [
                "PCIROOT(0)#PCI(1400)#USBROOT(0)#USB(1)",
                "PCIROOT(0)#PCI(1400)#USBROOT(0)#USB(2)",
                "PCIROOT(0)#PCI(1400)#USBROOT(0)#USB(3)",
                "AUTO-USBC-CHARGER#1",
            ],
            "charger_ports_count": 1,
            "manual_override": False,
            "serial": "PF57AAA1",
        })
        _, prof1 = self._get_json("/api/model-profile?model=Lenovo+ThinkPad+E16+Gen+2")
        self.assertEqual(prof1["usb"]["port_count"], 4)
        self.assertEqual(len(prof1["usb"]["known_port_locations"]), 3)

        # Primerak 2 testira i ostala 2 fizička porta L4, L5 (šalje L1, L2, L4, L5) -> unija je sada 5!
        self._post_json("/api/model-profile/usb", {
            "model": model,
            "target_ports": 4,
            "detected_ports": 4,
            "physical_port_locations": [
                "pciroot(0)#pci(1400)#usbroot(0)#usb(1)",  # case-insensitive duplikat L1
                "PCIROOT(0)#PCI(1400)#USBROOT(0)#USB(4)",
                "PCIROOT(0)#PCI(1400)#USBROOT(0)#USB(5)",
            ],
            "charger_ports_count": 0,
            "manual_override": False,
            "serial": "PF57AAA2",
        })
        _, prof2 = self._get_json("/api/model-profile?model=Lenovo+ThinkPad+E16+Gen+2")
        self.assertEqual(prof2["usb"]["port_count"], 5)
        self.assertEqual(prof2["usb"]["source"], "autolearn")
        self.assertEqual(len(prof2["usb"]["known_port_locations"]), 5)

        # Nevalidni tipovi i DoS vrednosti moraju biti odbijeni sa 400
        for bad_payload in (
            {"model": model, "port_count": True},
            {"model": model, "port_count": 0},
            {"model": model, "physical_port_locations": "PCIROOT(0)#USB(1)", "manual_override": False},
            {"model": model, "charger_ports_count": -1, "manual_override": False},
        ):
            req = urllib.request.Request(
                f"{self.base_url}/api/model-profile/usb-override",
                data=json.dumps(bad_payload).encode("utf-8"),
                headers={"Content-Type": "application/json"},
                method="POST",
            )
            with self.assertRaises(urllib.error.HTTPError) as ctx:
                urllib.request.urlopen(req, timeout=5)
            self.assertEqual(ctx.exception.code, 400)
            ctx.exception.close()


if __name__ == "__main__":
    unittest.main()
