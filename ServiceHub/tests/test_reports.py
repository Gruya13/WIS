"""
tests.test_reports
==================
Verifikacija generisanja i arhiviranja servisnih kartona (servishub.reports).
"""

import json
import os
import shutil
import tempfile
import unittest
from servishub.reports import (
    _extract_license_summary,
    _mask_license_keys,
    generate_markdown_report,
    save_device_report,
)


class TestReports(unittest.TestCase):

    def setUp(self):
        self.test_dir = tempfile.mkdtemp()

    def tearDown(self):
        shutil.rmtree(self.test_dir, ignore_errors=True)

    def test_generate_markdown_report(self):
        sample = {
            "device_id": "TK-TEST-1234",
            "computer_name": "TK-LAPTOP-1234",
            "chassis": "Notebook",
            "serial_number": "PF123456",
            "status": "COMPLETED",
            "duration_seconds": 630,
            "duration_formatted": "10m 30s",
            "apps": {
                "Chrome": "Completed",
                "Office": "Completed",
                "WinRAR": "Completed"
            },
            "hw": {
                "manufacturer": "Lenovo",
                "model": "ThinkPad T14",
                "cpu": "Intel Core i7-1185G7",
                "ram_gb": 16,
                "disk_model": "Samsung 980 Pro",
                "disk_size_gb": 512
            }
        }
        md = generate_markdown_report(sample)
        self.assertIn("TK-LAPTOP-1234", md)
        self.assertIn("PF123456", md)
        self.assertIn("10m 30s", md)
        self.assertIn("ThinkPad T14", md)
        self.assertIn("Chrome", md)

    def test_generate_markdown_report_with_string_ram_and_disk(self):
        sample = {
            "device_id": "TK-LAPTOP-5CG2491CHJ",
            "computer_name": "DESKTOP-3VL6LE1",
            "chassis": "Laptop",
            "serial_number": "5CG2491CHJ",
            "status": "SUCCESS",
            "duration_seconds": 2243,
            "duration_formatted": "37m 23s",
            "hw": {
                "manufacturer": "HP",
                "model": "HP EliteBook 840 14 inch G9 Notebook PC",
                "cpu": "12th Gen Intel(R) Core(TM) i5-1235U",
                "ram": "16 GB DDR5 4800 MHz",
                "disk": "SSSTC CL4-8D256-HP (256 GB)",
            },
        }
        md = generate_markdown_report(sample)
        self.assertIn("- **RAM Memorija:** 16 GB DDR5 4800 MHz", md)
        self.assertIn("- **Disk:** SSSTC CL4-8D256-HP (256 GB)", md)
        self.assertNotIn("N/A GB", md)
        self.assertNotIn("N/A (N/A GB)", md)

    def test_save_device_report(self):
        sample = {
            "device_id": "TK-TEST-5678",
            "computer_name": "TK-DESKTOP-5678",
            "status": "COMPLETED"
        }
        j_fp, m_fp, j_fn, m_fn = save_device_report("TK-TEST-5678", sample, self.test_dir)
        self.assertTrue(os.path.isfile(j_fp))
        self.assertTrue(os.path.isfile(m_fp))


    def test_v330_imp08_save_device_report_dedup_window(self):
        """IMP-08: Ponovni submit za isti uredjaj i sesiju unutar 30 min prepisuje isti fajl; novi session_id ili mtime > 30 min pravi novi."""
        import json as _json
        import time as _time

        rep_a = {
            "device_id": "TK-LAPTOP-DEDUP01",
            "computer_name": "TK-LAPTOP-DEDUP01",
            "serial_number": "SN-DEDUP-1",
            "session_id": "sess-1",
            "status": "COMPLETED",
            "duration_seconds": 500,
        }
        j1, m1, jn1, _ = save_device_report("TK-LAPTOP-DEDUP01", rep_a, self.test_dir)
        rep_a_upd = dict(rep_a, duration_seconds=620)
        j2, m2, jn2, _ = save_device_report("TK-LAPTOP-DEDUP01", rep_a_upd, self.test_dir)

        self.assertEqual(j1, j2)
        self.assertEqual(m1, m2)
        self.assertEqual(jn1, jn2)
        with open(j2, "r", encoding="utf-8") as f:
            self.assertEqual(_json.load(f)["duration_seconds"], 620)

        # Razlicit session_id pravi odvojen izvestaj
        rep_b = dict(rep_a, session_id="sess-2", duration_seconds=700)
        j3, _, _, _ = save_device_report("TK-LAPTOP-DEDUP01", rep_b, self.test_dir)
        self.assertNotEqual(j3, j1)

        # Istekao prozor (> 1800s) za sess-2 takodje pravi novi fajl
        old_ts = _time.time() - 2000
        os.utime(j3, (old_ts, old_ts))
        j4, _, _, _ = save_device_report("TK-LAPTOP-DEDUP01", dict(rep_b, duration_seconds=710), self.test_dir)
        self.assertNotEqual(j4, j3)
        json_files = [fn for fn in os.listdir(self.test_dir) if fn.endswith(".json")]
        self.assertEqual(len(json_files), 3)

    def test_lic06_markdown_report_windows_ok_office_not_activated_0xc004f074(self):
        """LIC-06 (a): Windows licenciran, Office instaliran ali neaktiviran uz kod 0xC004F074."""
        sample = {
            "device_id": "TK-LAPTOP-LIC01",
            "computer_name": "DESKTOP-LIC01",
            "status": "COMPLETED",
            "windows_activated": True,
            "office_installed": True,
            "office_activated": False,
            "office_activation_mode": "Auto",
            "office_license_method": "KMS",
            "office_license_detail": "0xC004F074 No Key Management Service (KMS) could be contacted",
        }
        md = generate_markdown_report(sample)
        self.assertIn("## 🔑 Status Licenciranja (Windows & Office)", md)
        self.assertIn("- **Windows Status:** ✅ Licenciran", md)
        self.assertIn("- **Office Status:** ⚠️ Nije aktiviran", md)
        self.assertIn("- **Režim Aktivacije (Office):** Auto", md)
        self.assertIn("- **Metod Aktivacije (Office):** KMS", md)
        self.assertIn("0xC004F074 No Key Management Service (KMS) could be contacted", md)

    def test_lic06_markdown_report_skip_and_not_installed(self):
        """LIC-06 (b): Prikaz statusa za OfficeActivationMode=Skip i za Nije instaliran."""
        skip_report = {
            "device_id": "TK-LAPTOP-SKIP01",
            "computer_name": "DESKTOP-SKIP01",
            "status": "COMPLETED",
            "windows_activated": True,
            "office_installed": True,
            "office_activated": False,
            "office_activation_mode": "Skip",
            "office_license_method": "Skip",
            "office_license_detail": "Aktivacija preskočena po konfiguraciji",
        }
        md_skip = generate_markdown_report(skip_report)
        self.assertIn("## 🔑 Status Licenciranja (Windows & Office)", md_skip)
        self.assertIn("- **Windows Status:** ✅ Licenciran", md_skip)
        self.assertIn("- **Office Status:** Preskočeno (Skip)", md_skip)
        self.assertIn("- **Režim Aktivacije (Office):** Skip", md_skip)

        not_installed_report = {
            "device_id": "TK-DESKTOP-NOOFF01",
            "computer_name": "DESKTOP-NOOFF01",
            "status": "COMPLETED",
            "windows_activated": True,
            "office_installed": False,
            "office_activated": False,
        }
        md_no_off = generate_markdown_report(not_installed_report)
        self.assertIn("## 🔑 Status Licenciranja (Windows & Office)", md_no_off)
        self.assertIn("- **Windows Status:** ✅ Licenciran", md_no_off)
        self.assertIn("- **Office Status:** Nije instaliran", md_no_off)

    def test_lic06_backward_compatibility_and_licenses_fallback(self):
        """LIC-06 (c): Stari izveštaji bez polja o licenciranju ostaju nepromenjeni; radi fallback na `licenses`."""
        legacy = {
            "device_id": "TK-OLD-0001",
            "computer_name": "DESKTOP-OLD01",
            "status": "COMPLETED",
            "duration_seconds": 120,
            "duration_formatted": "2m 00s",
            "timestamp": "2026-11-20 10:00:00",
            "apps": {"Chrome": "Completed", "License": "WARN"},
        }
        self.assertIsNone(_extract_license_summary(legacy))
        md_legacy = generate_markdown_report(legacy)
        self.assertNotIn("Status Licenciranja", md_legacy)

        # Fallback na ugnježdeni `licenses` rečnik (npr. iz starijeg servisni_karton.json)
        fallback_report = {
            "device_id": "TK-FALLBACK-01",
            "computer_name": "DESKTOP-FB01",
            "status": "COMPLETED",
            "licenses": {
                "windows": True,
                "office": True,
                "office_license_method": "MAK",
                "office_license_detail": "Licensed",
            },
        }
        summary = _extract_license_summary(fallback_report)
        self.assertIsNotNone(summary)
        self.assertTrue(summary["windows_activated"])
        self.assertTrue(summary["office_activated"])
        md_fb = generate_markdown_report(fallback_report)
        self.assertIn("## 🔑 Status Licenciranja (Windows & Office)", md_fb)
        self.assertIn("- **Windows Status:** ✅ Licenciran", md_fb)
        self.assertIn("- **Office Status:** ✅ Licenciran", md_fb)
        self.assertIn("- **Metod Aktivacije (Office):** MAK", md_fb)

    def test_lic06_mask_product_keys_in_save_and_markdown(self):
        """LIC-06 (d): 25-karakterni product ključevi se maskiraju i u save_device_report i u generate_markdown_report."""
        raw_key_1 = "TEST1-TEST2-TEST3-TEST4-9Z8X7"
        raw_key_2 = "ABCDE-12345-FGHIJ-67890-KLMNO"

        self.assertEqual(
            _mask_license_keys(f"Greska za kljuc {raw_key_1} (0xC004F074)"),
            "Greska za kljuc *****-*****-*****-*****-9Z8X7 (0xC004F074)",
        )

        payload = {
            "device_id": "TK-MASK-001",
            "computer_name": "DESKTOP-MASK01",
            "status": "COMPLETED",
            "windows_activated": True,
            "office_installed": True,
            "office_activated": False,
            "office_activation_mode": "Mak",
            "office_license_method": "MAK",
            "office_license_detail": f"OSPP /inpkey {raw_key_1} -> 0xC004F074",
            "licenses": {
                "windows": True,
                "office": False,
                "office_license_detail": f"Pokusaj sa {raw_key_2}",
            },
        }

        # 1. Direktno generisanje Markdown-a ne sme sadržati sirove ključeve
        md_direct = generate_markdown_report(payload)
        self.assertNotIn(raw_key_1, md_direct)
        self.assertNotIn(raw_key_2, md_direct)
        self.assertIn("*****-*****-*****-*****-9Z8X7", md_direct)

        # 2. Snimanje na disk preko save_device_report mora maskirati i JSON i MD
        j_fp, m_fp, _, _ = save_device_report("TK-MASK-001", payload, self.test_dir)
        with open(j_fp, "r", encoding="utf-8") as jf:
            raw_json_text = jf.read()
            saved_json = json.loads(raw_json_text)
        with open(m_fp, "r", encoding="utf-8") as mf:
            saved_md = mf.read()

        self.assertNotIn(raw_key_1, raw_json_text)
        self.assertNotIn(raw_key_2, raw_json_text)
        self.assertNotIn(raw_key_1, saved_md)
        self.assertNotIn(raw_key_2, saved_md)
        self.assertIn("*****-*****-*****-*****-9Z8X7", saved_json["office_license_detail"])
        self.assertIn("*****-*****-*****-*****-KLMNO", saved_json["licenses"]["office_license_detail"])
        self.assertIn("*****-*****-*****-*****-9Z8X7", saved_md)


if __name__ == "__main__":
    unittest.main()
