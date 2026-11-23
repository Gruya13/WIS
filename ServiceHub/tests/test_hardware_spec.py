#!/usr/bin/env python3
"""Unit testovi za servishub.hardware_spec OEM BOM parser i heuristiku."""

import json
import os
import shutil
import sys
import tempfile
import unittest
from unittest.mock import patch

SERVICEHUB_DIR = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
if SERVICEHUB_DIR not in sys.path:
    sys.path.insert(0, SERVICEHUB_DIR)

from servishub.config import active_config
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

    def test_v343_04_hp_keyboard_backlight_and_schema_version_3(self):
        # 1. HP primary non-backlit (čak i kad spare_part ima BL opciju!)
        res_440 = _evaluate_bom_entries(
            "hp_partsurfer_bom",
            primary_entries=[("M21388-B71", "ASSY KBD CP SR NORDIC")],
            secondary_entries=[("M21389-B71", "SPS-KBD CP BL SR NORDIC")],
            is_desktop=False,
        )
        self.assertEqual(res_440["schema_version"], 3)
        self.assertFalse(res_440["has_keyboard_backlight"])
        self.assertIn("ASSY KBD CP SR NORDIC", res_440["keyboard_backlight_evidence"])

        res_460 = _evaluate_bom_entries(
            "hp_partsurfer_bom",
            primary_entries=[("N11111-B71", "ASSY KBD fTBT CP num kypd SR NORDIC")],
            secondary_entries=[],
            is_desktop=False,
        )
        self.assertFalse(res_460["has_keyboard_backlight"])

        res_745 = _evaluate_bom_entries(
            "hp_partsurfer_bom",
            primary_entries=[("L11111-B71", "Assy KBD CP+PS SR Cllb SE/FI")],
            secondary_entries=[],
            is_desktop=False,
        )
        self.assertFalse(res_745["has_keyboard_backlight"])

        # 2. HP pozitivne oznake: \bBL\b i BL-RGB
        res_hp_bl = _evaluate_bom_entries(
            "hp_partsurfer_bom",
            primary_entries=[("M05024-041", "ASSY KBDPM CP BL SR GR")],
            secondary_entries=[],
            is_desktop=False,
        )
        self.assertTrue(res_hp_bl["has_keyboard_backlight"])

        res_omen = _evaluate_bom_entries(
            "hp_partsurfer_bom",
            primary_entries=[("M44444-001", "ASSY, KBD WTL ISK PT CP BL-RGB VK SL")],
            secondary_entries=[],
            is_desktop=False,
        )
        self.assertTrue(res_omen["has_keyboard_backlight"])

    def test_v343_04_lenovo_dell_bl_ambiguity_and_explicit_tokens(self):
        # Za Lenovo/Dell golo \bBL\b (Black) NE sme dati has_keyboard_backlight=True
        res_lenovo_black = _evaluate_bom_entries(
            "lenovo_parts_api",
            primary_entries=[],
            secondary_entries=[("5N21B001", "KEYBOARD US English BL")],
            is_desktop=False,
        )
        self.assertIsNone(res_lenovo_black["has_keyboard_backlight"])

        res_dell_black = _evaluate_bom_entries(
            "dell_asset_parts_api",
            primary_entries=[("K1234", "KYBD, 82, US, BL")],
            secondary_entries=[],
            is_desktop=False,
        )
        self.assertIsNone(res_dell_black["has_keyboard_backlight"])

        # Eksplicitne negacije na različitim formatima
        for desc in (
            "Keyboard US NON-BACKLIT",
            "KBD UK NON BACKLIT",
            "Internal KBD NO BL",
            "Keyboard US W/O BL",
            "Keyboard NBL English",
            "Keyboard WITHOUT BACKLIGHT",
        ):
            res_neg = _evaluate_bom_entries(
                "lenovo_parts_api",
                primary_entries=[],
                secondary_entries=[("PN-NEG", desc)],
                is_desktop=False,
            )
            self.assertFalse(res_neg["has_keyboard_backlight"], f"Očekivano False za: {desc}")

        # Eksplicitne pozitivne oznake
        for desc in (
            "Internal Keyboard BACKLIT US",
            "Keyboard with BACKLIGHT",
            "ILLUMINATED Keyboard",
            "KYBD, 82, US, BLGT",
            "Keyboard US W/BL",
            "Keyboard Per-Key RGB",
            "Keyboard BL-RGB",
        ):
            res_pos = _evaluate_bom_entries(
                "dell_asset_parts_api",
                primary_entries=[("PN-POS", desc)],
                secondary_entries=[],
                is_desktop=False,
            )
            self.assertTrue(res_pos["has_keyboard_backlight"], f"Očekivano True za: {desc}")

    def test_v343_04_cache_invalidation_for_schema_v2(self):
        tmp_dir = tempfile.mkdtemp(prefix="wis_hwspec_cache_")
        try:
            cache_dir = os.path.join(tmp_dir, "storage", "hw_spec_cache")
            os.makedirs(cache_dir, exist_ok=True)
            cache_file = os.path.join(cache_dir, "HP_5CD2111R5T.json")
            with open(cache_file, "w", encoding="utf-8") as f:
                json.dump({"schema_version": 2, "bom_loaded": True, "source": "hp_partsurfer_bom"}, f)

            hp_bom_payload = {
                "Body": {
                    "SerialNumberBOM": {
                        "unit_configuration": [
                            {"part_number": "M21388-B71", "part_description": "ASSY KBD CP SR NORDIC"},
                            {"part_number": "L51624-332", "part_description": "PNL,14.0 FHD AG UWVA"},
                        ],
                        "spare_part": [],
                    }
                }
            }
            with patch.object(active_config, "base_dir", tmp_dir), \
                 patch("servishub.hardware_spec._http_get_json", return_value=hp_bom_payload) as mock_get:
                spec = resolve_hardware_spec(
                    manufacturer="HP",
                    model="HP ProBook 440 G8",
                    serial="5CD2111R5T",
                    is_desktop=False,
                )
            self.assertTrue(mock_get.called)
            self.assertEqual(spec["schema_version"], 3)
            self.assertFalse(spec["has_keyboard_backlight"])
            self.assertTrue(spec["keyboard_backlight_evidence"])
        finally:
            shutil.rmtree(tmp_dir, ignore_errors=True)


if __name__ == "__main__":
    unittest.main()
