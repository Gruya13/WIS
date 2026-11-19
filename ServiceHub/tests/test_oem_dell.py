"""
tests.test_oem_dell
===================
Jedinični i integracioni testovi za Dell OEM provajder (servishub.drivers.oem.dell)
sa fokusom na DellOEMProvider, BaseOEMProvider, fabričku garanciju,
Dell Command | Deploy CAB pakete i BIOS provere.
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
from servishub.drivers.oem.dell import DellOEMProvider
from servishub.drivers.oem import get_oem_provider


class TestDellOEMProviderFactory(unittest.TestCase):
    """Testovi za get_oem_provider fabriku sa Dell hardverom."""

    def test_factory_returns_dell_provider(self):
        for mfg in ["DELL", "dell", "Dell", "Dell Inc.", "Dell Inc", "  dell  "]:
            provider = get_oem_provider(mfg)
            self.assertIsNotNone(provider)
            self.assertIsInstance(provider, DellOEMProvider)
            self.assertIsInstance(provider, BaseOEMProvider)

    def test_factory_passes_kwargs_to_dell(self):
        with tempfile.TemporaryDirectory() as tmp_dir:
            provider = get_oem_provider("DELL", cache_dir=tmp_dir, timeout=3.5)
            self.assertEqual(provider.base_cache_dir, os.path.abspath(tmp_dir))
            self.assertEqual(provider.timeout, 3.5)


class TestDellOEMProvider(unittest.TestCase):
    """Detaljni testovi za sve metode klase DellOEMProvider."""

    def setUp(self):
        self.test_dir = tempfile.mkdtemp()
        self.provider = DellOEMProvider(cache_dir=self.test_dir, timeout=2.0)

    def tearDown(self):
        shutil.rmtree(self.test_dir, ignore_errors=True)

    # -------------------------------------------------------------------------
    # 1. Normalizacija i pomoćne funkcije
    # -------------------------------------------------------------------------

    def test_normalize_serial(self):
        self.assertEqual(self.provider._normalize_serial("8ABC123"), "8ABC123")
        self.assertEqual(self.provider._normalize_serial(" 8abc123 "), "8ABC123")
        self.assertEqual(self.provider._normalize_serial("8-ABC-123"), "8ABC123")
        self.assertEqual(self.provider._normalize_serial(""), "")
        self.assertEqual(self.provider._normalize_serial(None), "")

    def test_express_service_code_conversion(self):
        # 8ABC123 u bazi 36 -> konvertovan Express Service Code
        # Testiramo konverziju i idempotenciju
        tag = "8ABC123"
        num = int(tag, 36)
        express_code = str(num)
        self.assertEqual(self.provider._express_code_to_service_tag(express_code), tag)
        self.assertEqual(self.provider._normalize_serial(express_code), tag)

    def test_normalize_model(self):
        self.assertEqual(self.provider._normalize_model("Latitude 5420"), "Latitude_5420")
        self.assertEqual(self.provider._normalize_model("Dell Latitude 5420"), "Latitude_5420")
        self.assertEqual(self.provider._normalize_model("Latitude 5420 vPro"), "Latitude_5420")
        self.assertEqual(self.provider._normalize_model("Dell OptiPlex 7080 Micro"), "OptiPlex_7080")
        self.assertEqual(self.provider._normalize_model("OptiPlex 7090 SFF"), "OptiPlex_7090")
        self.assertEqual(self.provider._normalize_model("Precision 3560"), "Precision_3560")
        self.assertEqual(self.provider._normalize_model("Dell Precision 3560 Workstation"), "Precision_3560")
        self.assertEqual(self.provider._normalize_model("Latitude 7420"), "Latitude_7420")
        self.assertEqual(self.provider._normalize_model("5420"), "Latitude_5420")
        self.assertEqual(self.provider._normalize_model("7080"), "OptiPlex_7080")
        self.assertEqual(self.provider._normalize_model(""), "")

    def test_bios_version_comparison(self):
        # Starija instalirana vs novija dostupna
        self.assertTrue(DellOEMProvider._is_newer_bios("1.25.0", "1.34.0"))
        self.assertTrue(DellOEMProvider._is_newer_bios("1.10.0", "1.19.1"))
        self.assertTrue(DellOEMProvider._is_newer_bios("", "1.34.0"))
        self.assertTrue(DellOEMProvider._is_newer_bios("UNKNOWN", "1.34.0"))
        self.assertTrue(DellOEMProvider._is_newer_bios("A05", "A08"))
        self.assertTrue(DellOEMProvider._is_newer_bios("Latitude 5420 (1.20.0)", "1.34.0"))

        # Ista ili novija instalirana verzija
        self.assertFalse(DellOEMProvider._is_newer_bios("1.34.0", "1.34.0"))
        self.assertFalse(DellOEMProvider._is_newer_bios("1.35.0", "1.34.0"))
        self.assertFalse(DellOEMProvider._is_newer_bios("A10", "A08"))
        self.assertFalse(DellOEMProvider._is_newer_bios("1.34.0", ""))
        self.assertFalse(DellOEMProvider._is_newer_bios("1.34", "1.34.0"))

    # -------------------------------------------------------------------------
    # 2. Testovi garancije (get_warranty_info)
    # -------------------------------------------------------------------------

    def test_warranty_empty_or_invalid_serial(self):
        res_empty = self.provider.get_warranty_info("")
        self.assertEqual(res_empty["status"], "error")
        self.assertFalse(res_empty["in_warranty"])
        self.assertEqual(res_empty["status_label"], "Neispravan serijski broj")

        res_short = self.provider.get_warranty_info("12345")
        self.assertEqual(res_short["status"], "error")
        self.assertFalse(res_short["in_warranty"])
        self.assertEqual(res_short["status_label"], "Neispravan serijski broj")

    def test_warranty_offline_catalog_hit(self):
        # 8ABC123 je u OFFLINE_WARRANTY_CATALOG (Latitude 5420, ProSupport Plus)
        res = self.provider.get_warranty_info("8ABC123")
        self.assertEqual(res["status"], "ok")
        self.assertIn("service_level", res)
        self.assertIn("ProSupport", res["service_level"])
        self.assertIn("start_date", res)
        self.assertIn("end_date", res)
        self.assertIn("days_remaining", res)
        self.assertIn("raw_data", res)
        self.assertEqual(res["raw_data"]["source"], "offline_catalog")

        # Provera da je rezultat keširan u warranty folderu
        cache_file = os.path.join(self.provider.warranty_cache_dir, "8ABC123.json")
        self.assertTrue(os.path.isfile(cache_file))

    def test_warranty_fallback_on_network_error(self):
        # Nepoznat serijski broj van offline kataloga uz network failure
        with patch("urllib.request.urlopen", side_effect=Exception("Connection timed out")):
            res = self.provider.get_warranty_info("9ZZZ888", model="Latitude 5420")
            self.assertEqual(res["status"], "fallback")
            self.assertFalse(res["in_warranty"])
            self.assertEqual(res["status_label"], "Nepoznato (Timeout/Greška)")
            self.assertEqual(res["service_level"], "N/A")
            self.assertEqual(res["days_remaining"], 0)
            self.assertIn("serial", res["raw_data"])

    def test_warranty_success_and_caching(self):
        future_date = (date.today() + timedelta(days=150)).isoformat()
        start_date = (date.today() - timedelta(days=200)).isoformat()

        mock_payload = {
            "warranties": [
                {
                    "serviceLevelDescription": "ProSupport Plus with 4-Hour Onsite",
                    "startDate": start_date,
                    "endDate": future_date,
                    "status": "Active",
                    "inWarranty": True,
                }
            ]
        }

        mock_resp = MagicMock()
        mock_resp.status = 200
        mock_resp.read.return_value = json.dumps(mock_payload).encode("utf-8")
        mock_resp.__enter__.return_value = mock_resp

        with patch("urllib.request.urlopen", return_value=mock_resp):
            res = self.provider.get_warranty_info("7ABC777")
            self.assertEqual(res["status"], "ok")
            self.assertTrue(res["in_warranty"])
            self.assertEqual(res["status_label"], "U garanciji")
            self.assertEqual(res["service_level"], "ProSupport Plus with 4-Hour Onsite")
            self.assertGreaterEqual(res["days_remaining"], 149)

        # Provera keša na disku
        cache_file = os.path.join(self.provider.warranty_cache_dir, "7ABC777.json")
        self.assertTrue(os.path.isfile(cache_file))

        # Drugi poziv mora čitati iz lokalnog keša bez mrežnog poziva
        with patch("urllib.request.urlopen") as mock_url:
            cached_res = self.provider.get_warranty_info("7ABC777")
            mock_url.assert_not_called()
            self.assertEqual(cached_res["status"], "ok")
            self.assertTrue(cached_res["in_warranty"])
            self.assertEqual(cached_res["service_level"], "ProSupport Plus with 4-Hour Onsite")

    # -------------------------------------------------------------------------
    # 3. Testovi Dell Command | Deploy CAB paketa (resolve_driver_pack)
    # -------------------------------------------------------------------------

    def test_resolve_driver_pack_empty_model(self):
        res = self.provider.resolve_driver_pack("")
        self.assertEqual(res["status"], "not_found")
        self.assertEqual(res["package_name"], "")
        self.assertEqual(res["size_bytes"], 0)

    def test_resolve_driver_pack_known_models(self):
        for model in ["Latitude 5420", "OptiPlex 7080", "OptiPlex 7090", "Precision 3560"]:
            res = self.provider.resolve_driver_pack(model, os_ver="win11-x64")
            self.assertEqual(res["status"], "ok")
            self.assertEqual(res["source"], "dell_command_deploy_catalog")
            self.assertTrue(res["package_name"].endswith(".cab"))
            self.assertTrue(res["download_url"].startswith("https://downloads.dell.com"))
            self.assertEqual(res["local_path"], "")
            self.assertGreater(res["size_bytes"], 1000000000)

            # Provera da je metapodatak upisan u package_meta.json
            canon = self.provider._normalize_model(model)
            meta_path = os.path.join(self.test_dir, canon, "package_meta.json")
            self.assertTrue(os.path.isfile(meta_path))

    def test_resolve_driver_pack_cached_file(self):
        model_dir = os.path.join(self.test_dir, "Latitude_5420")
        os.makedirs(model_dir, exist_ok=True)
        fake_cab = os.path.join(model_dir, "Latitude-5420-win11-A07-6N8DF.cab")
        with open(fake_cab, "wb") as f:
            f.write(b"MOCK_DELL_CAB_DATA" * 512)

        res = self.provider.resolve_driver_pack("Latitude 5420", os_ver="win11-x64")
        self.assertEqual(res["status"], "ok")
        self.assertEqual(res["source"], "cache")
        self.assertEqual(res["package_name"], "Latitude-5420-win11-A07-6N8DF.cab")
        self.assertEqual(res["local_path"], fake_cab)
        self.assertGreater(res["size_bytes"], 0)

    def test_resolve_driver_pack_from_existing_package_meta(self):
        model_dir = os.path.join(self.test_dir, "Latitude_5430")
        os.makedirs(model_dir, exist_ok=True)
        meta_file = os.path.join(model_dir, "package_meta.json")
        saved_meta = {
            "status": "ok",
            "source": "dell_sccm_cached_meta",
            "package_name": "Latitude-5430-win11-A06-7M2K1.cab",
            "download_url": "https://downloads.dell.com/FOLDER10884211M/1/Latitude-5430-win11-A06-7M2K1.cab",
            "size_bytes": 1945120000,
            "local_path": "",
        }
        with open(meta_file, "w", encoding="utf-8") as f:
            json.dump(saved_meta, f)

        res = self.provider.resolve_driver_pack("Latitude 5430")
        self.assertEqual(res["status"], "ok")
        self.assertEqual(res["package_name"], "Latitude-5430-win11-A06-7M2K1.cab")
        self.assertEqual(res["size_bytes"], 1945120000)

    # -------------------------------------------------------------------------
    # 4. Testovi BIOS provere (check_bios_update)
    # -------------------------------------------------------------------------

    def test_check_bios_update_known_model(self):
        # Latitude 5420 u bazi ima BIOS 1.34.0
        res_older = self.provider.check_bios_update("Latitude 5420", current_bios="1.25.0")
        self.assertTrue(res_older["update_available"])
        self.assertEqual(res_older["latest_version"], "1.34.0")
        self.assertEqual(res_older["current_version"], "1.25.0")
        self.assertTrue(res_older["download_url"].endswith(".exe"))

        res_same = self.provider.check_bios_update("Latitude 5420", current_bios="1.34.0")
        self.assertFalse(res_same["update_available"])

        res_newer = self.provider.check_bios_update("Latitude 5420", current_bios="1.35.0")
        self.assertFalse(res_newer["update_available"])

        # Provera da je bios_meta.json sačuvan
        bios_meta = os.path.join(self.test_dir, "Latitude_5420", "bios_meta.json")
        self.assertTrue(os.path.isfile(bios_meta))

    def test_check_bios_update_empty_model(self):
        res = self.provider.check_bios_update("")
        self.assertFalse(res["update_available"])
        self.assertEqual(res["latest_version"], "")

    def test_thread_safety_lock(self):
        self.assertIsInstance(self.provider.lock, type(threading.Lock()))


if __name__ == "__main__":
    unittest.main()
