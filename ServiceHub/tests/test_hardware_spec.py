#!/usr/bin/env python3
"""Unit testovi za servishub.hardware_spec OEM BOM parser i heuristiku."""

import os
import sys
import unittest

SERVICEHUB_DIR = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
if SERVICEHUB_DIR not in sys.path:
    sys.path.insert(0, SERVICEHUB_DIR)

from servishub.hardware_spec import _evaluate_bom_entries, resolve_hardware_spec


class TestHardwareSpecBom(unittest.TestCase):
    def test_hp_unit_config_non_touch_beats_spare_touch(self):
        # Kao na realnom EliteBook 630 G9 primeru:
        # unit_configuration ima non-touch AUO panel, dok spare_part ima TS rezervni panel
        primary = [
            ("L51624-332", "PNL,13.3 FHD AG UWVA 45% 250n eDP NB,AUO"),
            ("M05024-B71", "ASSY KBD CP BL SR SE/FI"),
        ]
        secondary = [
            ("M21146-001", "SPS-SPEAKER 13"),
            ("N09835-001", "SPS-WEBCAM HD"),
            ("P04524-001", "SPS-RAW PANEL TS 13.3 FHD AG UWVA 250N"),
            ("M21148-001", "SPS-TOUCHPAD 13/14"),
            ("M21398-001", "SPS-SMART CARD READER"),
        ]
        res = _evaluate_bom_entries("hp_partsurfer_bom", primary, secondary, is_desktop=False)
        self.assertTrue(res["bom_loaded"])
        self.assertFalse(res["has_touchscreen"])
        self.assertFalse(res["has_fingerprint"])
        self.assertTrue(res["has_webcam"])
        self.assertFalse(res["has_ir_camera"])
        self.assertTrue(res["has_touchpad"])
        self.assertTrue(res["has_speakers"])

    def test_touch_and_fingerprint_positive_bom(self):
        primary = [
            ("P11111-001", "PNL,14.0 FHD TOUCH ON-CELL AG 400n"),
            ("M99999-001", "SPS-FINGERPRINT READER MODULE"),
            ("M88888-001", "SPS-WEBCAM FHD IR RGB"),
            ("M77777-001", "SPS-SPEAKER KIT"),
            ("M66666-001", "SPS-CLICKPAD"),
        ]
        res = _evaluate_bom_entries("dell_asset_parts_api", primary, [], is_desktop=False)
        self.assertTrue(res["bom_loaded"])
        self.assertTrue(res["has_touchscreen"])
        self.assertTrue(res["has_fingerprint"])
        self.assertTrue(res["has_webcam"])
        self.assertTrue(res["has_ir_camera"])
        self.assertTrue(res["has_touchpad"])
        self.assertTrue(res["has_speakers"])

    def test_offline_convertible_heuristic(self):
        res = resolve_hardware_spec(
            manufacturer="HP",
            model="HP EliteBook x360 1040 G8",
            serial="",
            sku="",
            is_desktop=False,
        )
        self.assertFalse(res["bom_loaded"])
        self.assertTrue(res["has_touchscreen"])

    def test_hp_840_g8_keyboard_iso_and_usb_count(self):
        primary = [
            ("L87609-A41", "Assy KBDPM CP+PS SR BEL"),
            ("L76245-393", "PNL,14.0 FHD AG UWVA 250n eDP bent, AUO"),
        ]
        secondary = [
            ("M36310-171", "SPS-TOP COVER W/ KBD CP+PS ENG/ARAB"),
            ("M36310-001", "SPS-TOP COVER W/ KBD CP+PS US"),
        ]
        res = _evaluate_bom_entries("hp_partsurfer_bom", primary, secondary, is_desktop=False)
        self.assertTrue(res["has_keyboard_iso"])
        self.assertIn("Assy KBDPM", res["keyboard_desc"])

        offline = resolve_hardware_spec(
            manufacturer="HP",
            model="HP EliteBook 840 G8 Notebook PC",
            serial="",
            sku="6L7S4EC#UUG",
            is_desktop=False,
        )
        self.assertEqual(offline["usb_port_count"], 4)

        offline_630 = resolve_hardware_spec(
            manufacturer="HP",
            model="HP EliteBook 630 13 inch G9 Notebook PC",
            serial="",
            sku="",
            is_desktop=False,
        )
        self.assertEqual(offline_630["usb_port_count"], 3)


if __name__ == "__main__":
    unittest.main()
