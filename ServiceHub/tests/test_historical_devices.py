"""
tests.test_historical_devices
==============================
Unit testovi za učitavanje istorijskih uređaja i AGY sesija u FleetStore.
"""

import os
import time
import json
import tempfile
import unittest
from datetime import datetime, timezone

from servishub.telemetry import FleetStore
from servishub.drivers.cache import sanitize_id


class TestHistoricalDevices(unittest.TestCase):
    """
    Testovi za FleetStore.load_historical_devices i automatsko učitavanje pri inicijalizaciji.
    """

    def setUp(self):
        self.temp_dir = tempfile.TemporaryDirectory()
        self.reports_dir = self.temp_dir.name

    def tearDown(self):
        self.temp_dir.cleanup()

    def test_empty_or_nonexistent_reports_dir(self):
        """Proverava da prazan ili nepostojeći direktorijum ne izaziva greške."""
        # None direktorijum
        store_none = FleetStore(reports_dir=None)
        self.assertEqual(len(store_none.fleet_devices), 0)
        self.assertEqual(store_none.get_devices_summary(), {})

        # Nepostojeći direktorijum
        store_nonexistent = FleetStore(reports_dir=os.path.join(self.reports_dir, "nonexistent"))
        self.assertEqual(len(store_nonexistent.fleet_devices), 0)

    def test_load_historical_devices_single_and_multiple(self):
        """Proverava učitavanje više različitih izveštaja i tačnost popunjenih polja."""
        # 1. Računar sa standardnim uspešnim izveštajem
        pc1_data = {
            "computer_name": "DESKTOP-RADIONICA-01",
            "serial_number": "SN-RAD-001",
            "status": "COMPLETED",
            "status_label": "Zavrseno",
            "timestamp": "2026-09-15T10:00:00Z",
            "duration_formatted": "12m 30s",
            "duration_seconds": 750,
            "hw": {"model": "ThinkCentre M720q", "cpu": "i5-8400T"},
            "apps": {"chrome": True, "vlc": True},
        }
        with open(os.path.join(self.reports_dir, "report_pc01.json"), "w", encoding="utf-8") as f:
            json.dump(pc1_data, f)

        # 2. Računar sa garancijom, OEM podacima i statusom 'Kvar (AGY)'
        pc2_data = {
            "computer_name": "LAPTOP-TEREN-02",
            "serial_number": "PF2ABCDE",
            "status": "FAILED",
            "status_label": "Kvar (AGY)",
            "timestamp": "2026-09-20T14:15:00Z",
            "warranty": {"active": True, "status": "IN_WARRANTY", "end_date": "2027-01-01"},
            "oem": {"provider": "Lenovo", "model": "ThinkPad T14"},
            "errors": ["SSD SMART failure: 0x05 Reallocated Sectors"],
        }
        with open(os.path.join(self.reports_dir, "report_pc02.json"), "w", encoding="utf-8") as f:
            json.dump(pc2_data, f)

        # Inicijalizacija FleetStore sa reports_dir
        store = FleetStore(reports_dir=self.reports_dir)

        # Provera učitavanja
        self.assertEqual(len(store.fleet_devices), 2)

        # Provera PC 1
        id1 = sanitize_id("DESKTOP-RADIONICA-01")
        self.assertIn(id1, store.fleet_devices)
        dev1 = store.fleet_devices[id1]
        self.assertEqual(dev1["computer_name"], "DESKTOP-RADIONICA-01")
        self.assertEqual(dev1["serial_number"], "SN-RAD-001")
        self.assertEqual(dev1["status_label"], "Zavrseno")
        self.assertEqual(dev1["phase"], 5)
        self.assertEqual(dev1["worker_progress"], 100)
        self.assertEqual(dev1["duration_formatted"], "12m 30s")
        self.assertEqual(dev1["hw"].get("model"), "ThinkCentre M720q")
        self.assertFalse(dev1["is_online"])

        # Provera PC 2 (Kvar (AGY) + garancija + errors)
        id2 = sanitize_id("LAPTOP-TEREN-02")
        self.assertIn(id2, store.fleet_devices)
        dev2 = store.fleet_devices[id2]
        self.assertEqual(dev2["status_label"], "Kvar (AGY)")
        self.assertTrue(dev2.get("has_errors"))
        self.assertIn("SSD SMART failure", dev2.get("errors", [])[0])
        self.assertEqual(dev2["warranty"]["status"], "IN_WARRANTY")
        self.assertEqual(dev2["oem"]["provider"], "Lenovo")

        # Provera get_devices_summary
        summary = store.get_devices_summary()
        self.assertEqual(len(summary), 2)
        self.assertFalse(summary[id1]["is_online"])
        self.assertEqual(summary[id1]["status_label"], "Zavrseno")
        self.assertFalse(summary[id2]["is_online"])
        self.assertEqual(summary[id2]["status_label"], "Kvar (AGY)")

    def test_newer_report_overrides_older_report_for_same_device(self):
        """Proverava da se za isti računar zadržava noviji izveštaj."""
        comp_name = "WORKSTATION-HP-Z4"
        dev_id = sanitize_id(comp_name)

        # Stariji izveštaj (npr. avgust 2026)
        old_data = {
            "computer_name": comp_name,
            "serial_number": "CZC123456",
            "status": "COMPLETED",
            "timestamp": "2026-08-01T10:00:00Z",
            "duration_formatted": "30m",
            "duration_seconds": 1800,
            "hw": {"ram": "16GB"},
        }
        with open(os.path.join(self.reports_dir, "report_hp_old.json"), "w", encoding="utf-8") as f:
            json.dump(old_data, f)

        # Noviji izveštaj (npr. oktobar 2026)
        new_data = {
            "computer_name": comp_name,
            "serial_number": "CZC123456",
            "status": "COMPLETED",
            "timestamp": "2026-10-01T12:00:00Z",
            "duration_formatted": "18m",
            "duration_seconds": 1080,
            "hw": {"ram": "32GB"},
        }
        with open(os.path.join(self.reports_dir, "report_hp_new.json"), "w", encoding="utf-8") as f:
            json.dump(new_data, f)

        store = FleetStore(reports_dir=self.reports_dir)
        self.assertEqual(len(store.fleet_devices), 1)

        dev = store.fleet_devices[dev_id]
        # Podaci moraju biti iz novijeg izveštaja
        self.assertEqual(dev["duration_formatted"], "18m")
        self.assertEqual(dev["duration_seconds"], 1080)
        self.assertEqual(dev["hw"].get("ram"), "32GB")

    def test_subdirectory_agy_session_with_device_info(self):
        """Proverava učitavanje AGY sesije iz poddirektorijuma sa DEVICE_INFO.json."""
        agy_dir = os.path.join(self.reports_dir, "LENOVO-T490-AGY")
        os.makedirs(agy_dir, exist_ok=True)

        dinfo_data = {
            "device_tag": "LENOVO-T490-AGY",
            "serial_number": "PF1T490X",
            "manufacturer": "Lenovo",
            "model": "ThinkPad T490",
            "hardware": {"cpu": "Core i7-8565U", "ram": "16GB", "ssd": "512GB NVMe"},
            "warranty": {"active": True, "type": "Premier Support"},
            "status": "COMPLETED",
            "timestamp": "2026-09-28T09:00:00Z",
        }
        with open(os.path.join(agy_dir, "DEVICE_INFO.json"), "w", encoding="utf-8") as f:
            json.dump(dinfo_data, f)

        store = FleetStore(reports_dir=self.reports_dir)
        dev_id = sanitize_id("LENOVO-T490-AGY")

        self.assertIn(dev_id, store.fleet_devices)
        dev = store.fleet_devices[dev_id]
        self.assertEqual(dev["status_label"], "Servisiran (AGY)")
        self.assertEqual(dev["serial_number"], "PF1T490X")
        self.assertEqual(dev["hw"].get("cpu"), "Core i7-8565U")
        self.assertIn("warranty", dev)
        self.assertFalse(dev["is_online"])

        summary = store.get_devices_summary()
        self.assertEqual(summary[dev_id]["status_label"], "Servisiran (AGY)")
        self.assertFalse(summary[dev_id]["is_online"])

    def test_invalid_json_and_missing_name_handled_gracefully(self):
        """Proverava da neispravni fajlovi ili fajlovi bez identifikatora ne ruše sistem."""
        # 1. Korumpiran JSON
        with open(os.path.join(self.reports_dir, "broken.json"), "w", encoding="utf-8") as f:
            f.write("{ invalid json content ...")

        # 2. JSON bez imena računara
        with open(os.path.join(self.reports_dir, "empty_device.json"), "w", encoding="utf-8") as f:
            json.dump({"status": "COMPLETED", "note": "missing computer_name"}, f)

        # 3. Fajl koji nije JSON
        with open(os.path.join(self.reports_dir, "readme.txt"), "w", encoding="utf-8") as f:
            f.write("Ovo je tekstualni fajl.")

        # 4. Jedan validan fajl
        valid_data = {
            "computer_name": "VALID-PC-01",
            "status": "OK",
        }
        with open(os.path.join(self.reports_dir, "valid.json"), "w", encoding="utf-8") as f:
            json.dump(valid_data, f)

        store = FleetStore(reports_dir=self.reports_dir)
        self.assertEqual(len(store.fleet_devices), 1)
        self.assertIn(sanitize_id("VALID-PC-01"), store.fleet_devices)

    def test_mtime_fallback_when_no_timestamp(self):
        """Proverava fallback na os.path.getmtime kada timestamp polje nedostaje."""
        filepath = os.path.join(self.reports_dir, "report_no_ts.json")
        data = {
            "computer_name": "FALLBACK-PC-01",
            "status": "COMPLETED",
        }
        with open(filepath, "w", encoding="utf-8") as f:
            json.dump(data, f)

        mtime = os.path.getmtime(filepath)
        store = FleetStore(reports_dir=self.reports_dir)
        dev = store.fleet_devices[sanitize_id("FALLBACK-PC-01")]
        self.assertAlmostEqual(dev["last_seen"], mtime, delta=2.0)

    def test_mixed_report_file_and_agy_directory_for_same_device(self):
        """Proverava kombinaciju report fajla i naknadnog/paralelnog AGY DEVICE_INFO.json za isti PC."""
        comp_name = "DUAL-TRACK-PC"
        dev_id = sanitize_id(comp_name)

        # 1. Prvo report fajl sa statusom Zavrseno
        report_data = {
            "computer_name": comp_name,
            "status": "COMPLETED",
            "timestamp": "2026-09-01T10:00:00Z",
        }
        with open(os.path.join(self.reports_dir, "report_dual.json"), "w", encoding="utf-8") as f:
            json.dump(report_data, f)

        # 2. AGY sesija poddirektorijum sa DEVICE_INFO.json
        agy_dir = os.path.join(self.reports_dir, comp_name)
        os.makedirs(agy_dir, exist_ok=True)
        dinfo_data = {
            "device_tag": comp_name,
            "manufacturer": "Dell",
            "model": "Latitude 5420",
            "hardware": {"cpu": "i5-1135G7"},
            "timestamp": "2026-09-01T09:00:00Z",  # Ranije vreme, ali DEVICE_INFO mora postaviti Servisiran (AGY)
        }
        with open(os.path.join(agy_dir, "DEVICE_INFO.json"), "w", encoding="utf-8") as f:
            json.dump(dinfo_data, f)

        store = FleetStore(reports_dir=self.reports_dir)
        self.assertIn(dev_id, store.fleet_devices)
        dev = store.fleet_devices[dev_id]
        self.assertEqual(dev["status_label"], "Servisiran (AGY)")

    def test_explicit_load_historical_devices_call(self):
        """Proverava da dinamički poziv load_historical_devices() nakon dodavanja fajlova ažurira stanje."""
        store = FleetStore(reports_dir=self.reports_dir)
        self.assertEqual(len(store.fleet_devices), 0)

        # Dodajemo novi fajl nakon inicijalizacije
        data = {
            "computer_name": "DYNAMIC-PC-99",
            "status": "COMPLETED",
            "timestamp": "2026-10-10T12:00:00Z",
        }
        with open(os.path.join(self.reports_dir, "report_dyn.json"), "w", encoding="utf-8") as f:
            json.dump(data, f)

        store.load_historical_devices()
        self.assertEqual(len(store.fleet_devices), 1)
        self.assertIn(sanitize_id("DYNAMIC-PC-99"), store.fleet_devices)


if __name__ == "__main__":
    unittest.main()
