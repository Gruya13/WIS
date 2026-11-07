"""
tests.test_oem_lenovo
=====================
Jedinični testovi za modularni OEM podsistem (servishub.drivers.oem)
sa fokusom na LenovoOEMProvider, BaseOEMProvider i get_oem_provider helper.
"""

import os
import json
import shutil
import tempfile
import unittest
from unittest.mock import patch, MagicMock
from datetime import date, timedelta
import threading

from servishub.drivers.oem.base import BaseOEMProvider
from servishub.drivers.oem.lenovo import LenovoOEMProvider
from servishub.drivers.oem import get_oem_provider


class TestOEMProviderFactory(unittest.TestCase):
    """Testovi za get_oem_provider factory funkciju."""

    def test_factory_returns_lenovo_provider(self):
        # Case-insensitive prepoznavanje
        for mfg in ["LENOVO", "lenovo", "Lenovo", "Lenovo Group Limited", "  lenovo  "]:
            provider = get_oem_provider(mfg)
            self.assertIsNotNone(provider)
            self.assertIsInstance(provider, LenovoOEMProvider)
            self.assertIsInstance(provider, BaseOEMProvider)

    def test_factory_returns_none_for_unsupported_or_empty(self):
        self.assertIsNone(get_oem_provider("ACER"))
        self.assertIsNone(get_oem_provider("TOSHIBA"))
        self.assertIsNone(get_oem_provider("ASUS"))
        self.assertIsNone(get_oem_provider(""))
        self.assertIsNone(get_oem_provider(None))

    def test_factory_passes_kwargs(self):
        with tempfile.TemporaryDirectory() as tmp_dir:
            provider = get_oem_provider("LENOVO", cache_dir=tmp_dir, timeout=3.5)
            self.assertEqual(provider.base_cache_dir, os.path.abspath(tmp_dir))
            self.assertEqual(provider.timeout, 3.5)


class TestBaseOEMProviderContract(unittest.TestCase):
    """Verifikacija apstraktne klase i ugovora."""

    def test_cannot_instantiate_base_provider(self):
        with self.assertRaises(TypeError):
            BaseOEMProvider()

    def test_subclass_must_implement_all_methods(self):
        class IncompleteProvider(BaseOEMProvider):
            pass

        with self.assertRaises(TypeError):
            IncompleteProvider()


class TestLenovoOEMProvider(unittest.TestCase):
    """Detaljni testovi za LenovoOEMProvider funkcionalnosti."""

    def setUp(self):
        self.test_dir = tempfile.mkdtemp()
        self.provider = LenovoOEMProvider(cache_dir=self.test_dir, timeout=2.0)

    def tearDown(self):
        shutil.rmtree(self.test_dir, ignore_errors=True)

    # -------------------------------------------------------------------------
    # Testovi normalizacije i BIOS komparacije
    # -------------------------------------------------------------------------

    def test_normalize_mtm(self):
        self.assertEqual(self.provider._normalize_mtm("20TD"), "20TD")
        self.assertEqual(self.provider._normalize_mtm("20TD0003CD"), "20TD")
        self.assertEqual(self.provider._normalize_mtm("ThinkPad E14 Gen 2 (20TD)"), "20TD")
        self.assertEqual(self.provider._normalize_mtm("ThinkPad T14 Gen 3 Type 21AH"), "21AH")
        self.assertEqual(self.provider._normalize_mtm(""), "")

    def test_bios_version_comparison(self):
        # Starija instalirana vs novija dostupna
        self.assertTrue(LenovoOEMProvider._is_newer_bios("1.25", "1.62"))
        self.assertTrue(LenovoOEMProvider._is_newer_bios("R1EET50W (1.50)", "1.62"))
        self.assertTrue(LenovoOEMProvider._is_newer_bios("", "1.62"))
        self.assertTrue(LenovoOEMProvider._is_newer_bios("UNKNOWN", "1.62"))
        self.assertTrue(LenovoOEMProvider._is_newer_bios("R1EET50W", "R1EET62W"))
        self.assertTrue(LenovoOEMProvider._is_newer_bios("1.5.0", "1.5.1"))

        # Ista ili novija instalirana verzija
        self.assertFalse(LenovoOEMProvider._is_newer_bios("1.62", "1.62"))
        self.assertFalse(LenovoOEMProvider._is_newer_bios("1.70", "1.62"))
        self.assertFalse(LenovoOEMProvider._is_newer_bios("R1EET62W (1.62)", "1.62"))
        self.assertFalse(LenovoOEMProvider._is_newer_bios("1.62", ""))
        self.assertFalse(LenovoOEMProvider._is_newer_bios("1.5", "1.5.0"))

    # -------------------------------------------------------------------------
    # Testovi garancije (get_warranty_info)
    # -------------------------------------------------------------------------

    def test_warranty_empty_serial(self):
        res = self.provider.get_warranty_info("")
        self.assertEqual(res["status"], "error")
        self.assertFalse(res["in_warranty"])
        self.assertEqual(res["status_label"], "Neispravan serijski broj")

    def test_warranty_fallback_on_network_error(self):
        # Simuliramo grešku na mreži (URLError / timeout)
        with patch("urllib.request.urlopen", side_effect=Exception("Connection timed out")):
            res = self.provider.get_warranty_info("PF123456", model="20TD")
            self.assertEqual(res["status"], "fallback")
            self.assertFalse(res["in_warranty"])
            self.assertEqual(res["status_label"], "Nepoznato (Timeout/Greška)")
            self.assertEqual(res["service_level"], "N/A")
            self.assertEqual(res["days_remaining"], 0)
            self.assertIn("serial", res["raw_data"])

    def test_warranty_success_and_caching(self):
        future_date = (date.today() + timedelta(days=120)).isoformat()
        start_date = (date.today() - timedelta(days=200)).isoformat()

        mock_payload = {
            "data": {
                "inWarranty": True,
                "status": "In Warranty",
                "startDate": start_date,
                "endDate": future_date,
                "serviceType": "Premier Support",
            }
        }

        mock_resp = MagicMock()
        mock_resp.status = 200
        mock_resp.read.return_value = json.dumps(mock_payload).encode("utf-8")
        mock_resp.__enter__.return_value = mock_resp

        with patch("urllib.request.urlopen", return_value=mock_resp):
            res = self.provider.get_warranty_info("PF999999")
            self.assertEqual(res["status"], "ok")
            self.assertTrue(res["in_warranty"])
            self.assertEqual(res["status_label"], "U garanciji")
            self.assertEqual(res["service_level"], "Premier Support")
            self.assertGreaterEqual(res["days_remaining"], 119)

        # Provera da je rezultat keširan na disku
        cache_file = os.path.join(self.provider.warranty_cache_dir, "PF999999.json")
        self.assertTrue(os.path.isfile(cache_file))

        # Drugi poziv mora čitati iz lokalnog keša bez mrežnog poziva
        with patch("urllib.request.urlopen") as mock_url:
            cached_res = self.provider.get_warranty_info("PF999999")
            mock_url.assert_not_called()
            self.assertEqual(cached_res["status"], "ok")
            self.assertTrue(cached_res["in_warranty"])
            self.assertEqual(cached_res["service_level"], "Premier Support")

    # -------------------------------------------------------------------------
    # Testovi SCCM drajver paketa (resolve_driver_pack)
    # -------------------------------------------------------------------------

    def test_resolve_driver_pack_empty_model(self):
        res = self.provider.resolve_driver_pack("")
        self.assertEqual(res["status"], "not_found")
        self.assertEqual(res["package_name"], "")
        self.assertEqual(res["size_bytes"], 0)

    def test_resolve_driver_pack_simulated_when_uncached(self):
        res = self.provider.resolve_driver_pack("20TD", os_ver="win11-x64")
        self.assertEqual(res["status"], "ok")
        self.assertIn(res["source"], ["lenovo_sccm_simulated", "lenovo_sccm_portal"])
        self.assertTrue(res["package_name"].endswith(".exe"))
        self.assertTrue(res["download_url"].startswith("https://"))
        self.assertEqual(res["local_path"], "")  # Nije preuzeto na disk
        self.assertGreater(res["size_bytes"], 0)

        # Provera da je metapodatak zapisan u lokalni keš
        meta_file = os.path.join(self.test_dir, "20TD", "package_meta.json")
        self.assertTrue(os.path.isfile(meta_file))

    def test_resolve_driver_pack_cached_file(self):
        mtm_dir = os.path.join(self.test_dir, "21AH")
        os.makedirs(mtm_dir, exist_ok=True)
        fake_pkg = os.path.join(mtm_dir, "tp_t14_gen3_win11_21ah.exe")
        with open(fake_pkg, "wb") as f:
            f.write(b"MOCK_SCCM_PACKAGE_CONTENT" * 1024)

        res = self.provider.resolve_driver_pack("21AH", os_ver="win11-x64")
        self.assertEqual(res["status"], "ok")
        self.assertEqual(res["source"], "cache")
        self.assertEqual(res["package_name"], "tp_t14_gen3_win11_21ah.exe")
        self.assertEqual(res["local_path"], fake_pkg)
        self.assertGreater(res["size_bytes"], 0)

    # -------------------------------------------------------------------------
    # Testovi BIOS provere (check_bios_update)
    # -------------------------------------------------------------------------

    def test_check_bios_update_known_model(self):
        # 20TD u bazi ima BIOS 1.62
        res_older = self.provider.check_bios_update("20TD", current_bios="1.25")
        self.assertTrue(res_older["update_available"])
        self.assertEqual(res_older["latest_version"], "1.62")
        self.assertEqual(res_older["current_version"], "1.25")
        self.assertTrue(res_older["download_url"].endswith(".exe"))

        res_same = self.provider.check_bios_update("20TD", current_bios="1.62")
        self.assertFalse(res_same["update_available"])

        res_newer = self.provider.check_bios_update("20TD", current_bios="1.70")
        self.assertFalse(res_newer["update_available"])

        # Provera postojanja bios_meta.json u kešu
        bios_meta = os.path.join(self.test_dir, "20TD", "bios_meta.json")
        self.assertTrue(os.path.isfile(bios_meta))

    def test_check_bios_update_empty_model(self):
        res = self.provider.check_bios_update("")
        self.assertFalse(res["update_available"])
        self.assertEqual(res["latest_version"], "")

    def test_thread_safety_lock(self):
        self.assertIsInstance(self.provider.lock, type(threading.Lock()))


if __name__ == "__main__":
    unittest.main()
