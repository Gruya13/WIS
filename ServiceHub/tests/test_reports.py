"""
tests.test_reports
==================
Verifikacija generisanja i arhiviranja servisnih kartona (servishub.reports).
"""

import os
import shutil
import tempfile
import unittest
from servishub.reports import generate_markdown_report, save_device_report


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


if __name__ == "__main__":
    unittest.main()
