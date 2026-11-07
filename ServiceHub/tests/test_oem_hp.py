"""
tests.test_oem_hp
=================
Jedinični testovi za HP OEM podsistem (servishub.drivers.oem.hp)
sa fokusom na HpOEMProvider, integraciju u get_oem_provider fabriku,
keširanje na disku, semantičko poređenje BIOS verzija i thread-safety.
Sprovedeno u skladu sa Strict No-Mock pravilom (pravi disk I/O, pravi procesi).
"""

import os
import json
import shutil
import tempfile
import threading
import unittest
from datetime import date, timedelta
from http.server import HTTPServer, BaseHTTPRequestHandler

from servishub.drivers.oem.base import BaseOEMProvider
from servishub.drivers.oem.hp import HpOEMProvider
from servishub.drivers.oem import get_oem_provider


class TestHpOEMProviderFactory(unittest.TestCase):
    """Testovi za prepoznavanje HP proizvođača u get_oem_provider fabrici."""

    def test_factory_returns_hp_provider(self):
        # Case-insensitive i alias prepoznavanje
        for mfg in [
            "HP",
            "hp",
            "Hp",
            "HP Inc.",
            "Hewlett-Packard",
            "Hewlett Packard",
            "HEWLETT-PACKARD COMPANY",
            "  hp  ",
        ]:
            provider = get_oem_provider(mfg)
            self.assertIsNotNone(provider, f"Neuspelo prepoznavanje za: {mfg}")
            self.assertIsInstance(provider, HpOEMProvider)
            self.assertIsInstance(provider, BaseOEMProvider)

    def test_factory_passes_kwargs(self):
        with tempfile.TemporaryDirectory() as tmp_dir:
            provider = get_oem_provider("HP", cache_dir=tmp_dir, timeout=3.5)
            self.assertIsNotNone(provider)
            self.assertEqual(provider.base_cache_dir, os.path.abspath(tmp_dir))
            self.assertEqual(provider.timeout, 3.5)


class TestHpOEMProviderContract(unittest.TestCase):
    """Verifikacija BaseOEMProvider interfejsa za HpOEMProvider."""

    def test_hp_provider_is_subclass(self):
        self.assertTrue(issubclass(HpOEMProvider, BaseOEMProvider))

    def test_instantiation_without_arguments(self):
        with tempfile.TemporaryDirectory() as tmp_dir:
            provider = HpOEMProvider(cache_dir=tmp_dir)
            self.assertTrue(os.path.isdir(provider.base_cache_dir))
            self.assertTrue(os.path.isdir(provider.warranty_cache_dir))


class TestHpOEMProviderLogic(unittest.TestCase):
    """Testovi logike, normalizacije i lokalnog keširanja za HpOEMProvider."""

    def setUp(self):
        self.test_dir = tempfile.mkdtemp()
        self.provider = HpOEMProvider(cache_dir=self.test_dir, timeout=1.0)

    def tearDown(self):
        shutil.rmtree(self.test_dir, ignore_errors=True)

    # -------------------------------------------------------------------------
    # Normalizacija i poređenje BIOS verzija
    # -------------------------------------------------------------------------

    def test_normalize_model(self):
        self.assertEqual(self.provider._normalize_model("HP EliteBook 840 G7 Notebook PC"), "elitebook_840_g7")
        self.assertEqual(self.provider._normalize_model("HP EliteBook 840 G8"), "elitebook_840_g8")
        self.assertEqual(self.provider._normalize_model("HP ProBook 450 G8"), "probook_450_g8")
        self.assertEqual(self.provider._normalize_model("HP EliteDesk 800 G6 Desktop Mini PC"), "elitedesk_800_g6")
        self.assertEqual(self.provider._normalize_model("840 G8"), "elitebook_840_g8")
        self.assertEqual(self.provider._normalize_model("800 G6"), "elitedesk_800_g6")
        self.assertEqual(self.provider._normalize_model(""), "")

    def test_normalize_serial(self):
        self.assertEqual(self.provider._normalize_serial("5CG1234567"), "5CG1234567")
        self.assertEqual(self.provider._normalize_serial("5cg-123-4567"), "5CG1234567")
        self.assertEqual(self.provider._normalize_serial("czc9998888"), "CZC9998888")
        self.assertEqual(self.provider._normalize_serial(""), "")

    def test_normalize_product_number(self):
        self.assertEqual(self.provider._normalize_product_number("2V8P1EA#ABB"), "2V8P1EA")
        self.assertEqual(self.provider._normalize_product_number("1A2B3CD"), "1A2B3CD")
        self.assertEqual(self.provider._normalize_product_number(""), "")

    def test_bios_version_comparison(self):
        # Starija instalirana vs novija dostupna
        self.assertTrue(HpOEMProvider._is_newer_bios("01.10.00", "01.15.00"))
        self.assertTrue(HpOEMProvider._is_newer_bios("S77 Ver. 01.10.00", "01.15.00"))
        self.assertTrue(HpOEMProvider._is_newer_bios("T76 Ver. 01.12.01", "01.16.00"))
        self.assertTrue(HpOEMProvider._is_newer_bios("", "01.15.00"))
        self.assertTrue(HpOEMProvider._is_newer_bios("UNKNOWN", "01.15.00"))
        self.assertTrue(HpOEMProvider._is_newer_bios("1.10", "1.15"))
        self.assertTrue(HpOEMProvider._is_newer_bios("01.15.00", "02.14.01"))

        # Ista ili novija instalirana verzija
        self.assertFalse(HpOEMProvider._is_newer_bios("01.15.00", "01.15.00"))
        self.assertFalse(HpOEMProvider._is_newer_bios("01.16.00", "01.15.00"))
        self.assertFalse(HpOEMProvider._is_newer_bios("S77 Ver. 01.15.00", "01.15.00"))
        self.assertFalse(HpOEMProvider._is_newer_bios("01.15.00", ""))
        self.assertFalse(HpOEMProvider._is_newer_bios("01.15.00", "UNKNOWN"))

    # -------------------------------------------------------------------------
    # Garancija: get_warranty_info
    # -------------------------------------------------------------------------

    def test_warranty_empty_serial(self):
        res = self.provider.get_warranty_info("")
        self.assertEqual(res["status"], "error")
        self.assertFalse(res["in_warranty"])
        self.assertEqual(res["status_label"], "Neispravan serijski broj")

    def test_warranty_disk_cache_read_and_recalculation(self):
        # Pripremamo keš fajl na disku sa datumom u budućnosti
        serial = "5CG9998877"
        future_date = (date.today() + timedelta(days=90)).isoformat()
        start_date = (date.today() - timedelta(days=200)).isoformat()

        cache_entry = {
            "status": "ok",
            "in_warranty": True,
            "status_label": "U garanciji",
            "service_level": "HP Care Pack 3y Next Business Day",
            "start_date": start_date,
            "end_date": future_date,
            "days_remaining": 90,
            "raw_data": {"cached": True},
        }

        cache_file = os.path.join(self.provider.warranty_cache_dir, f"{serial}.json")
        with open(cache_file, "w", encoding="utf-8") as f:
            json.dump(cache_entry, f)

        res = self.provider.get_warranty_info(serial)
        self.assertEqual(res["status"], "ok")
        self.assertTrue(res["in_warranty"])
        self.assertEqual(res["status_label"], "U garanciji")
        self.assertEqual(res["service_level"], "HP Care Pack 3y Next Business Day")
        self.assertGreaterEqual(res["days_remaining"], 89)

    def test_warranty_fallback_when_offline(self):
        # Za serijski koji nije u kešu i sa unreachable hostom (timeout <= 1.0s)
        # Očekujemo graceful fallback
        res = self.provider.get_warranty_info("5CG0001111", model="2V8P1EA")
        self.assertIn(res["status"], ["fallback", "error"])
        self.assertFalse(res["in_warranty"])
        self.assertEqual(res["service_level"], "N/A")
        self.assertEqual(res["days_remaining"], 0)

    # -------------------------------------------------------------------------
    # Rezolucija Driver Pack / SoftPaq: resolve_driver_pack
    # -------------------------------------------------------------------------

    def test_resolve_driver_pack_empty_model(self):
        res = self.provider.resolve_driver_pack("")
        self.assertEqual(res["status"], "not_found")
        self.assertEqual(res["package_name"], "")
        self.assertEqual(res["size_bytes"], 0)

    def test_resolve_driver_pack_known_model(self):
        # EliteBook 840 G7
        res = self.provider.resolve_driver_pack("HP EliteBook 840 G7 Notebook PC", os_ver="win11-x64")
        self.assertEqual(res["status"], "ok")
        self.assertEqual(res["source"], "hp_softpaq_known")
        self.assertEqual(res["package_name"], "sp141520.exe")
        self.assertTrue(res["download_url"].startswith("https://ftp.hp.com"))
        self.assertEqual(res["local_path"], "")  # Nije preuzeto na disk
        self.assertGreater(res["size_bytes"], 0)

        # Provera da je package_meta.json perzistiran na disku
        meta_file = os.path.join(self.test_dir, "elitebook_840_g7", "package_meta.json")
        self.assertTrue(os.path.isfile(meta_file))

        with open(meta_file, "r", encoding="utf-8") as f:
            saved = json.load(f)
        self.assertEqual(saved["package_name"], "sp141520.exe")

    def test_resolve_driver_pack_cached_archive_file(self):
        model_dir = os.path.join(self.test_dir, "elitebook_840_g8")
        os.makedirs(model_dir, exist_ok=True)
        archive_path = os.path.join(model_dir, "sp148910.exe")
        with open(archive_path, "wb") as f:
            f.write(b"REAL_HP_SOFTPAQ_ARCHIVE_DATA" * 512)

        res = self.provider.resolve_driver_pack("HP EliteBook 840 G8", os_ver="win11-x64")
        self.assertEqual(res["status"], "ok")
        self.assertEqual(res["source"], "cache")
        self.assertEqual(res["package_name"], "sp148910.exe")
        self.assertEqual(res["local_path"], archive_path)
        self.assertGreater(res["size_bytes"], 0)

    def test_resolve_driver_pack_simulated_unknown_model(self):
        res = self.provider.resolve_driver_pack("HP ZBook Firefly 16 G10", os_ver="win11-x64")
        self.assertEqual(res["status"], "ok")
        self.assertEqual(res["source"], "hp_softpaq_simulated")
        self.assertTrue(res["package_name"].endswith(".exe"))
        self.assertTrue(res["download_url"].startswith("https://ftp.hp.com"))
        self.assertGreater(res["size_bytes"], 0)

    # -------------------------------------------------------------------------
    # Provera BIOS ažuriranja: check_bios_update
    # -------------------------------------------------------------------------

    def test_check_bios_update_empty_model(self):
        res = self.provider.check_bios_update("")
        self.assertFalse(res["update_available"])
        self.assertEqual(res["latest_version"], "")

    def test_check_bios_update_known_model(self):
        # EliteBook 840 G7 ima u KNOWN_MODELS verziju 01.15.00
        res_older = self.provider.check_bios_update("EliteBook 840 G7", current_bios="01.10.00")
        self.assertTrue(res_older["update_available"])
        self.assertEqual(res_older["latest_version"], "01.15.00")
        self.assertEqual(res_older["current_version"], "01.10.00")
        self.assertTrue(res_older["download_url"].endswith(".exe"))

        res_same = self.provider.check_bios_update("EliteBook 840 G7", current_bios="01.15.00")
        self.assertFalse(res_same["update_available"])

        res_newer = self.provider.check_bios_update("EliteBook 840 G7", current_bios="01.16.00")
        self.assertFalse(res_newer["update_available"])

        # Provera perzistencije bios_meta.json
        meta_file = os.path.join(self.test_dir, "elitebook_840_g7", "bios_meta.json")
        self.assertTrue(os.path.isfile(meta_file))

    # -------------------------------------------------------------------------
    # Thread safety i konkurentni rad
    # -------------------------------------------------------------------------

    def test_concurrent_access_thread_safety(self):
        self.assertIsInstance(self.provider.lock, type(threading.Lock()))
        errors = []

        def worker(model_name: str):
            try:
                for _ in range(5):
                    self.provider.resolve_driver_pack(model_name)
                    self.provider.check_bios_update(model_name, current_bios="01.00.00")
            except Exception as e:
                errors.append(e)

        threads = [
            threading.Thread(target=worker, args=("EliteBook 840 G7",)),
            threading.Thread(target=worker, args=("ProBook 450 G8",)),
            threading.Thread(target=worker, args=("EliteDesk 800 G6",)),
        ]
        for t in threads:
            t.start()
        for t in threads:
            t.join()

        self.assertEqual(len(errors), 0, f"Greške pri konkurentnom radu: {errors}")


if __name__ == "__main__":
    unittest.main()
