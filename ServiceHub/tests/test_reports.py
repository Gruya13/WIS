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

    def test_save_device_report(self):
        sample = {
            "device_id": "TK-TEST-5678",
            "computer_name": "TK-DESKTOP-5678",
            "status": "COMPLETED"
        }
        j_fp, m_fp, j_fn, m_fn = save_device_report("TK-TEST-5678", sample, self.test_dir)
        self.assertTrue(os.path.isfile(j_fp))
        self.assertTrue(os.path.isfile(m_fp))


if __name__ == "__main__":
    unittest.main()
