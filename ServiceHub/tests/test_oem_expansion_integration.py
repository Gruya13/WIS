"""
tests.test_oem_expansion_integration
=====================================
Sveobuhvatni integracioni i QA end-to-end testovi za proširenje OEM podsistema
(Talas 4 / OEM Ekspanzija za Dell, HP i Lenovo).

Pokriva:
1. Fabrici OEM provajdera (`get_oem_provider`) sa svim varijacijama imena brendova,
   aliasima, case-insensitivity i nepoznatim proizvođačima.
2. Striktnu verifikaciju ugovora (`BaseOEMProvider` specifikacija):
   - `get_warranty_info`: tipovi, ključevi, statusi ("ok", "error", "fallback").
   - `resolve_driver_pack`: ključevi, izvori, paketi, veličina, perzistencija na disku.
   - `check_bios_update`: semantičko poređenje verzija i ažuriranja.
3. Radionički offline režim i mrežnu otpornost:
   - Striktno ograničenje mrežnog timeout-a na najviše 5.0s.
   - Graceful fallback u slučaju odsustva interneta i nepoznatih serijskih brojeva.
   - Dell radionički offline katalog (aktivna i istekla fabrička garancija).
   - HP i Lenovo perzistentni keš na disku i rekalkulaciju preostalih dana.
4. Thread-safety i konkurentno izvršavanje za sva tri brenda (Dell, HP, Lenovo)
   istovremeno preko više radnih niti bez kolizija i korupcije JSON datoteka.
5. End-to-end integraciju sa REST API rutama preko stvarnog ThreadingHTTPServer-a:
   - `POST /api/drivers/resolve` sa Dell, HP, Lenovo i nepodržanim hardverom.
   - `POST /api/drivers/check_update` sa stvarnim keširanim drajverima za Dell i HP.

STRIKTNO NO-MOCK PRAVILO: Svi testovi koriste stvarni HTTP server, stvarni disk I/O
i stvarne procese bez ijednog mock/patch objekta.
"""

import os
import json
import shutil
import tempfile
import threading
import urllib.request
import urllib.error
from datetime import date, timedelta
from http.server import ThreadingHTTPServer
import unittest

from servishub.config import HubConfig
from servishub.telemetry import FleetStore
from servishub.drivers.cache import DriverCacheManager
from servishub.drivers.scraper import MsUpdateCatalogScraper
from servishub.handlers import ServisHubHandler
from servishub.drivers.oem.base import BaseOEMProvider
from servishub.drivers.oem.dell import DellOEMProvider
from servishub.drivers.oem.hp import HpOEMProvider
from servishub.drivers.oem.lenovo import LenovoOEMProvider
from servishub.drivers.oem import get_oem_provider


class TestOEMProviderFactoryVariations(unittest.TestCase):
    """
    Test 1: Verifikacija get_oem_provider fabrike sa svim varijacijama imena
    proizvođača, belim prostorima, velika/mala slova i nepodržanim unosima.
    """

    def test_factory_dell_variations(self):
        """Proverava prepoznavanje svih Dell varijacija."""
        variations = [
            "DELL",
            "dell",
            "Dell",
            "Dell Inc.",
            "Dell Inc",
            "Dell Technologies",
            "Dell Computer Corporation",
            "  dell  ",
            "\tDELL INC.\n",
        ]
        for name in variations:
            provider = get_oem_provider(name)
            self.assertIsNotNone(provider, f"Neuspelo prepoznavanje za Dell: '{name}'")
            self.assertIsInstance(provider, DellOEMProvider)
            self.assertIsInstance(provider, BaseOEMProvider)

    def test_factory_hp_variations(self):
        """Proverava prepoznavanje svih HP / Hewlett-Packard varijacija."""
        variations = [
            "HP",
            "hp",
            "Hp",
            "HP Inc.",
            "HP Inc",
            "Hewlett-Packard",
            "Hewlett Packard",
            "HEWLETT-PACKARD COMPANY",
            "HP Hewlett-Packard",
            "  hp  ",
            "\thp inc.\n",
        ]
        for name in variations:
            provider = get_oem_provider(name)
            self.assertIsNotNone(provider, f"Neuspelo prepoznavanje za HP: '{name}'")
            self.assertIsInstance(provider, HpOEMProvider)
            self.assertIsInstance(provider, BaseOEMProvider)

    def test_factory_lenovo_variations(self):
        """Proverava prepoznavanje svih Lenovo varijacija."""
        variations = [
            "LENOVO",
            "lenovo",
            "Lenovo",
            "Lenovo Group Limited",
            "LENOVO GROUP",
            "Lenovo (Singapore) Pte. Ltd.",
            "  lenovo  ",
            "\tLENOVO\n",
        ]
        for name in variations:
            provider = get_oem_provider(name)
            self.assertIsNotNone(provider, f"Neuspelo prepoznavanje za Lenovo: '{name}'")
            self.assertIsInstance(provider, LenovoOEMProvider)
            self.assertIsInstance(provider, BaseOEMProvider)

    def test_factory_unsupported_or_invalid_inputs(self):
        """Proverava da nepodržani i nevalidni unosi bezbedno vraćaju None."""
        invalid_inputs = [
            "Acer",
            "ASUS",
            "Toshiba",
            "Apple",
            "MSI",
            "Gigabyte",
            "Samsung",
            "Sony",
            "",
            "   ",
            "\n",
            None,
            12345,
            [],
            {},
        ]
        for val in invalid_inputs:
            provider = get_oem_provider(val)
            self.assertIsNone(provider, f"Očekivano None za unos: {val!r}")

    def test_factory_passes_custom_kwargs(self):
        """Proverava da se cache_dir i timeout prosleđuju svim provajderima."""
        with tempfile.TemporaryDirectory() as tmp_dir:
            p_dell = get_oem_provider("Dell", cache_dir=tmp_dir, timeout=2.5)
            self.assertEqual(p_dell.base_cache_dir, os.path.abspath(tmp_dir))
            self.assertEqual(p_dell.timeout, 2.5)

            p_hp = get_oem_provider("HP", cache_dir=tmp_dir, timeout=3.5)
            self.assertEqual(p_hp.base_cache_dir, os.path.abspath(tmp_dir))
            self.assertEqual(p_hp.timeout, 3.5)

            p_lenovo = get_oem_provider("Lenovo", cache_dir=tmp_dir, timeout=4.0)
            self.assertEqual(p_lenovo.base_cache_dir, os.path.abspath(tmp_dir))
            self.assertEqual(p_lenovo.timeout, 4.0)


class TestOEMContractsAndSpecifications(unittest.TestCase):
    """
    Test 2: Verifikacija BaseOEMProvider interfejsa i ugovora za sve provajdere:
    - get_warranty_info
    - resolve_driver_pack
    - check_bios_update
    """

    def setUp(self):
        self.test_dir = tempfile.mkdtemp()
        self.dell_dir = os.path.join(self.test_dir, "dell")
        self.hp_dir = os.path.join(self.test_dir, "hp")
        self.lenovo_dir = os.path.join(self.test_dir, "lenovo")

        self.dell_p = DellOEMProvider(cache_dir=self.dell_dir, timeout=1.0)
        self.hp_p = HpOEMProvider(cache_dir=self.hp_dir, timeout=1.0)
        self.lenovo_p = LenovoOEMProvider(cache_dir=self.lenovo_dir, timeout=1.0)

    def tearDown(self):
        shutil.rmtree(self.test_dir, ignore_errors=True)

    # -------------------------------------------------------------------------
    # 2.1 Warranty Info Ugovor
    # -------------------------------------------------------------------------

    def _verify_warranty_contract_structure(self, res: dict):
        required_keys = {
            "status",
            "in_warranty",
            "status_label",
            "service_level",
            "start_date",
            "end_date",
            "days_remaining",
            "raw_data",
        }
        self.assertTrue(required_keys.issubset(res.keys()), f"Nedostaju ključevi: {required_keys - set(res.keys())}")
        self.assertIn(res["status"], ["ok", "error", "fallback"])
        self.assertIsInstance(res["in_warranty"], bool)
        self.assertIsInstance(res["status_label"], str)
        self.assertIsInstance(res["service_level"], str)
        self.assertIsInstance(res["start_date"], str)
        self.assertIsInstance(res["end_date"], str)
        self.assertIsInstance(res["days_remaining"], int)
        self.assertIsInstance(res["raw_data"], (dict, list))

    def test_warranty_contract_empty_serial(self):
        """Svi provajderi moraju vratiti status='error' za prazan serijski broj."""
        for p in [self.dell_p, self.hp_p, self.lenovo_p]:
            res = p.get_warranty_info("")
            self._verify_warranty_contract_structure(res)
            self.assertEqual(res["status"], "error")
            self.assertFalse(res["in_warranty"])
            self.assertEqual(res["days_remaining"], 0)

    def test_warranty_contract_dell_offline_active(self):
        """Dell aktivna garancija iz offline kataloga (DL54201)."""
        res = self.dell_p.get_warranty_info("DL54201", model="Latitude 5420")
        self._verify_warranty_contract_structure(res)
        self.assertEqual(res["status"], "ok")
        self.assertIn("ProSupport", res["service_level"])
        self.assertIn("2026", res["end_date"])

    def test_warranty_contract_dell_offline_expired(self):
        """Dell istekla garancija iz offline kataloga (J3K9L22)."""
        res = self.dell_p.get_warranty_info("J3K9L22", model="OptiPlex 7080")
        self._verify_warranty_contract_structure(res)
        self.assertEqual(res["status"], "ok")
        self.assertFalse(res["in_warranty"])
        self.assertEqual(res["days_remaining"], 0)
        self.assertEqual(res["status_label"], "Istekla garancija")

    def test_warranty_contract_cached_hp(self):
        """HP garancija pročitana iz stvarnog keš fajla na disku."""
        serial = "5CG9991122"
        future_end = (date.today() + timedelta(days=120)).isoformat()
        start_dt = (date.today() - timedelta(days=200)).isoformat()

        cache_data = {
            "status": "ok",
            "in_warranty": True,
            "status_label": "U garanciji",
            "service_level": "HP Care Pack 3y Next Business Day",
            "start_date": start_dt,
            "end_date": future_end,
            "days_remaining": 120,
            "raw_data": {"cached": True},
        }
        cache_file = os.path.join(self.hp_p.warranty_cache_dir, f"{serial}.json")
        with open(cache_file, "w", encoding="utf-8") as f:
            json.dump(cache_data, f)

        res = self.hp_p.get_warranty_info(serial)
        self._verify_warranty_contract_structure(res)
        self.assertEqual(res["status"], "ok")
        self.assertTrue(res["in_warranty"])
        self.assertGreaterEqual(res["days_remaining"], 119)

    def test_warranty_contract_cached_lenovo(self):
        """Lenovo garancija pročitana iz stvarnog keš fajla na disku."""
        serial = "PF123456"
        future_end = (date.today() + timedelta(days=60)).isoformat()
        start_dt = (date.today() - timedelta(days=300)).isoformat()

        cache_data = {
            "status": "ok",
            "in_warranty": True,
            "status_label": "U garanciji",
            "service_level": "Premier Support",
            "start_date": start_dt,
            "end_date": future_end,
            "days_remaining": 60,
            "raw_data": {"cached": True},
        }
        cache_file = os.path.join(self.lenovo_p.warranty_cache_dir, f"{serial}.json")
        with open(cache_file, "w", encoding="utf-8") as f:
            json.dump(cache_data, f)

        res = self.lenovo_p.get_warranty_info(serial)
        self._verify_warranty_contract_structure(res)
        self.assertEqual(res["status"], "ok")
        self.assertTrue(res["in_warranty"])
        self.assertGreaterEqual(res["days_remaining"], 59)

    # -------------------------------------------------------------------------
    # 2.2 Driver Pack Ugovor
    # -------------------------------------------------------------------------

    def _verify_driver_pack_contract_structure(self, res: dict):
        required_keys = {
            "status",
            "source",
            "package_name",
            "download_url",
            "local_path",
            "size_bytes",
        }
        self.assertTrue(required_keys.issubset(res.keys()), f"Nedostaju ključevi: {required_keys - set(res.keys())}")
        self.assertIn(res["status"], ["ok", "not_found", "error"])
        self.assertIsInstance(res["source"], str)
        self.assertIsInstance(res["package_name"], str)
        self.assertIsInstance(res["download_url"], str)
        self.assertIsInstance(res["local_path"], str)
        self.assertIsInstance(res["size_bytes"], int)

    def test_driver_pack_contract_empty_model(self):
        """Svi provajderi vraćaju status='not_found' i size=0 za prazan model."""
        for p in [self.dell_p, self.hp_p, self.lenovo_p]:
            res = p.resolve_driver_pack("")
            self._verify_driver_pack_contract_structure(res)
            self.assertEqual(res["status"], "not_found")
            self.assertEqual(res["package_name"], "")
            self.assertEqual(res["size_bytes"], 0)

    def test_driver_pack_contract_dell_known_and_cached(self):
        """Dell rezolucija za poznati model i perzistiran CAB na disku."""
        res = self.dell_p.resolve_driver_pack("Latitude 5420", os_ver="win11-x64")
        self._verify_driver_pack_contract_structure(res)
        self.assertEqual(res["status"], "ok")
        self.assertEqual(res["package_name"], "Latitude-5420-win11-A07-6N8DF.cab")
        self.assertTrue(res["download_url"].startswith("https://downloads.dell.com"))
        self.assertGreater(res["size_bytes"], 0)

        # Proveravamo zapisani package_meta.json na disku
        meta_file = os.path.join(self.dell_dir, "Latitude_5420", "package_meta.json")
        self.assertTrue(os.path.isfile(meta_file))

        # Sada kreiramo stvarni .cab fajl na disku i proveravamo cache hit
        cab_path = os.path.join(self.dell_dir, "Latitude_5420", "Latitude-5420-win11-A07-6N8DF.cab")
        with open(cab_path, "wb") as f:
            f.write(b"REAL_DELL_CAB_DATA" * 1024)

        res_cached = self.dell_p.resolve_driver_pack("Latitude 5420")
        self.assertEqual(res_cached["source"], "cache")
        self.assertEqual(res_cached["local_path"], cab_path)
        self.assertEqual(res_cached["size_bytes"], os.path.getsize(cab_path))

    def test_driver_pack_contract_hp_known_and_cached(self):
        """HP rezolucija za poznati model i perzistiran SoftPaq na disku."""
        res = self.hp_p.resolve_driver_pack("HP EliteBook 840 G7 Notebook PC", os_ver="win11-x64")
        self._verify_driver_pack_contract_structure(res)
        self.assertEqual(res["status"], "ok")
        self.assertEqual(res["package_name"], "sp141520.exe")
        self.assertTrue(res["download_url"].startswith("https://ftp.hp.com"))
        self.assertGreater(res["size_bytes"], 0)

        # Proveravamo metapodatke na disku
        meta_file = os.path.join(self.hp_dir, "elitebook_840_g7", "package_meta.json")
        self.assertTrue(os.path.isfile(meta_file))

        # Simuliramo preuzet exe na disku
        exe_path = os.path.join(self.hp_dir, "elitebook_840_g7", "sp141520.exe")
        with open(exe_path, "wb") as f:
            f.write(b"REAL_HP_SOFTPACK_DATA" * 512)

        res_cached = self.hp_p.resolve_driver_pack("HP EliteBook 840 G7")
        self.assertEqual(res_cached["source"], "cache")
        self.assertEqual(res_cached["local_path"], exe_path)

    def test_driver_pack_contract_lenovo_known_and_cached(self):
        """Lenovo rezolucija za poznati model (20TD) i perzistiran SCCM na disku."""
        res = self.lenovo_p.resolve_driver_pack("20TD", os_ver="win11-x64")
        self._verify_driver_pack_contract_structure(res)
        self.assertEqual(res["status"], "ok")
        self.assertEqual(res["package_name"], "tp_e14_gen2_win11_20td.exe")
        self.assertTrue(res["download_url"].startswith("https://download.lenovo.com"))
        self.assertGreater(res["size_bytes"], 0)

        # Kreiramo stvarni exe fajl na disku
        exe_path = os.path.join(self.lenovo_dir, "20TD", "tp_e14_gen2_win11_20td.exe")
        with open(exe_path, "wb") as f:
            f.write(b"REAL_LENOVO_SCCM_DATA" * 512)

        res_cached = self.lenovo_p.resolve_driver_pack("20TD")
        self.assertEqual(res_cached["source"], "cache")
        self.assertEqual(res_cached["local_path"], exe_path)

    # -------------------------------------------------------------------------
    # 2.3 BIOS Update Check Ugovor
    # -------------------------------------------------------------------------

    def _verify_bios_contract_structure(self, res: dict):
        required_keys = {
            "update_available",
            "latest_version",
            "current_version",
            "download_url",
            "release_date",
        }
        self.assertTrue(required_keys.issubset(res.keys()), f"Nedostaju ključevi: {required_keys - set(res.keys())}")
        self.assertIsInstance(res["update_available"], bool)
        self.assertIsInstance(res["latest_version"], str)
        self.assertIsInstance(res["current_version"], str)
        self.assertIsInstance(res["download_url"], str)
        self.assertIsInstance(res["release_date"], str)

    def test_bios_contract_empty_model(self):
        """Svi provajderi vraćaju update_available=False za prazan model."""
        for p in [self.dell_p, self.hp_p, self.lenovo_p]:
            res = p.check_bios_update("", current_bios="1.0.0")
            self._verify_bios_contract_structure(res)
            self.assertFalse(res["update_available"])
            self.assertEqual(res["latest_version"], "")

    def test_bios_contract_dell_comparison(self):
        """Dell semantičko poređenje verzija (Latitude 5420: najnovija 1.34.0)."""
        res_older = self.dell_p.check_bios_update("Latitude 5420", current_bios="1.20.0")
        self._verify_bios_contract_structure(res_older)
        self.assertTrue(res_older["update_available"])
        self.assertEqual(res_older["latest_version"], "1.34.0")

        res_same = self.dell_p.check_bios_update("Latitude 5420", current_bios="1.34.0")
        self.assertFalse(res_same["update_available"])

        res_newer = self.dell_p.check_bios_update("Latitude 5420", current_bios="1.35.0")
        self.assertFalse(res_newer["update_available"])

    def test_bios_contract_hp_comparison(self):
        """HP semantičko poređenje verzija (EliteBook 840 G7: najnovija 01.15.00)."""
        res_older = self.hp_p.check_bios_update("HP EliteBook 840 G7", current_bios="01.10.00")
        self._verify_bios_contract_structure(res_older)
        self.assertTrue(res_older["update_available"])
        self.assertEqual(res_older["latest_version"], "01.15.00")

        res_same = self.hp_p.check_bios_update("HP EliteBook 840 G7", current_bios="01.15.00")
        self.assertFalse(res_same["update_available"])

    def test_bios_contract_lenovo_comparison(self):
        """Lenovo semantičko poređenje verzija (20TD: najnovija 1.62)."""
        res_older = self.lenovo_p.check_bios_update("20TD", current_bios="1.50")
        self._verify_bios_contract_structure(res_older)
        self.assertTrue(res_older["update_available"])
        self.assertEqual(res_older["latest_version"], "1.62")

        res_same = self.lenovo_p.check_bios_update("20TD", current_bios="1.62")
        self.assertFalse(res_same["update_available"])


class TestOEMOfflineWorkshopAndTimeoutResilience(unittest.TestCase):
    """
    Test 3: Ponašanje pri mrežnim greškama, timeout ograničenjima i radioničkom offline režimu.
    """

    def setUp(self):
        self.test_dir = tempfile.mkdtemp()

    def tearDown(self):
        shutil.rmtree(self.test_dir, ignore_errors=True)

    def test_timeout_bounds_clamping(self):
        """Verifikuje da je timeout striktno ograničen na opseg [0.5, 5.0] sekundi."""
        for ProviderCls in [DellOEMProvider, HpOEMProvider, LenovoOEMProvider]:
            p_high = ProviderCls(cache_dir=self.test_dir, timeout=12.0)
            self.assertEqual(p_high.timeout, 5.0, "Timeout > 5s mora biti srezan na 5.0s")

            p_low = ProviderCls(cache_dir=self.test_dir, timeout=0.1)
            self.assertEqual(p_low.timeout, 0.5, "Timeout < 0.5s mora biti podignut na 0.5s")

    def test_dell_offline_fallback_unknown_serial(self):
        """
        Kada je uređaj van baze i mreža nedostupna/timeout, Dell vraća graceful fallback:
        status='fallback', in_warranty=False, days_remaining=0.
        """
        dell_p = DellOEMProvider(cache_dir=self.test_dir, timeout=0.5)
        # Serijski broj koji NIJE u offline katalogu (npr. '9ZZZ888')
        res = dell_p.get_warranty_info("9ZZZ888", model="Latitude 5420")
        self.assertEqual(res["status"], "fallback")
        self.assertFalse(res["in_warranty"])
        self.assertEqual(res["days_remaining"], 0)
        self.assertEqual(res["service_level"], "N/A")
        self.assertIn("9ZZZ888", res["raw_data"]["serial"])

    def test_hp_offline_fallback_unknown_serial(self):
        """HP vraća graceful fallback za nepoznat serijski broj u offline okruženju."""
        hp_p = HpOEMProvider(cache_dir=self.test_dir, timeout=0.5)
        res = hp_p.get_warranty_info("5CG0001111", model="840 G7")
        self.assertEqual(res["status"], "fallback")
        self.assertFalse(res["in_warranty"])
        self.assertEqual(res["days_remaining"], 0)
        self.assertEqual(res["service_level"], "N/A")

    def test_lenovo_offline_fallback_unknown_serial(self):
        """Lenovo vraća graceful fallback za nepoznat serijski broj u offline okruženju."""
        lenovo_p = LenovoOEMProvider(cache_dir=self.test_dir, timeout=0.5)
        res = lenovo_p.get_warranty_info("PF000000", model="20TD")
        self.assertEqual(res["status"], "fallback")
        self.assertFalse(res["in_warranty"])
        self.assertEqual(res["days_remaining"], 0)
        self.assertEqual(res["service_level"], "N/A")


class TestOEMMultiBrandConcurrencyAndThreadSafety(unittest.TestCase):
    """
    Test 4: Provera thread-safety i konkurentnih upita za sva tri brenda (Dell, HP, Lenovo)
    istovremeno bez utrkivanja (race condition), mrtvih petlji ili oštećenih datoteka na disku.
    """

    def setUp(self):
        self.test_dir = tempfile.mkdtemp()
        self.dell_p = DellOEMProvider(cache_dir=os.path.join(self.test_dir, "dell"), timeout=0.5)
        self.hp_p = HpOEMProvider(cache_dir=os.path.join(self.test_dir, "hp"), timeout=0.5)
        self.lenovo_p = LenovoOEMProvider(cache_dir=os.path.join(self.test_dir, "lenovo"), timeout=0.5)

    def tearDown(self):
        shutil.rmtree(self.test_dir, ignore_errors=True)

    def test_high_concurrency_across_three_brands(self):
        """15 paralelnih niti istovremeno izvršavaju upite za Dell, HP i Lenovo."""
        exceptions = []

        def dell_worker(idx: int):
            try:
                models = ["Latitude 5420", "OptiPlex 7080", "Precision 3560", "Latitude 5430"]
                for i in range(10):
                    m = models[(idx + i) % len(models)]
                    self.dell_p.resolve_driver_pack(m)
                    self.dell_p.check_bios_update(m, current_bios="1.00.0")
                    self.dell_p.get_warranty_info("8ABC123")
            except Exception as e:
                exceptions.append(f"DellWorker[{idx}] Error: {e}")

        def hp_worker(idx: int):
            try:
                models = ["HP EliteBook 840 G7 Notebook PC", "HP ProBook 450 G8", "HP EliteDesk 800 G6"]
                for i in range(10):
                    m = models[(idx + i) % len(models)]
                    self.hp_p.resolve_driver_pack(m)
                    self.hp_p.check_bios_update(m, current_bios="01.00.00")
            except Exception as e:
                exceptions.append(f"HpWorker[{idx}] Error: {e}")

        def lenovo_worker(idx: int):
            try:
                models = ["20TD", "21AH", "20UD", "21E3"]
                for i in range(10):
                    m = models[(idx + i) % len(models)]
                    self.lenovo_p.resolve_driver_pack(m)
                    self.lenovo_p.check_bios_update(m, current_bios="1.00")
            except Exception as e:
                exceptions.append(f"LenovoWorker[{idx}] Error: {e}")

        threads = []
        for i in range(5):
            threads.append(threading.Thread(target=dell_worker, args=(i,)))
            threads.append(threading.Thread(target=hp_worker, args=(i,)))
            threads.append(threading.Thread(target=lenovo_worker, args=(i,)))

        for t in threads:
            t.start()
        for t in threads:
            t.join()

        self.assertEqual(len(exceptions), 0, f"Prijavljene greške pri konkurentnom radu: {exceptions}")

        # Provera integriteta svih upisanih JSON fajlova na disku (garancija bezbednog zaključavanja)
        for root, _, files in os.walk(self.test_dir):
            for fn in files:
                if fn.endswith(".json"):
                    full_p = os.path.join(root, fn)
                    with open(full_p, "r", encoding="utf-8") as f:
                        data = json.load(f)
                        self.assertIsInstance(data, dict, f"Datoteka {full_p} nije validan JSON objekat!")


class TestOEMRestApiIntegration(unittest.TestCase):
    """
    Test 5: Integraciono testiranje REST API ruta u servishub/handlers.py
    (POST /api/drivers/resolve i POST /api/drivers/check_update) sa proizvođačima Dell, HP i Lenovo
    kroz stvarni ThreadingHTTPServer.
    """

    @classmethod
    def setUpClass(cls):
        cls.test_dir = tempfile.mkdtemp(prefix="servishub_oem_qa_")
        cls.config = HubConfig(
            host="127.0.0.1",
            port=0,
            base_dir=cls.test_dir,
            storage_dir=os.path.join(cls.test_dir, "storage"),
            logs_dir=os.path.join(cls.test_dir, "logs"),
        )
        cls.fleet_store = FleetStore(live_logs_dir=cls.config.live_logs_dir, reports_dir=cls.config.reports_dir)
        cls.driver_cache_mgr = DriverCacheManager(cache_dir=cls.config.drivers_cache_dir)
        cls.ms_catalog_scraper = MsUpdateCatalogScraper()

        ServisHubHandler.fleet_store = cls.fleet_store
        ServisHubHandler.config = cls.config
        ServisHubHandler.payload_mgr = None
        ServisHubHandler.driver_cache_mgr = cls.driver_cache_mgr
        ServisHubHandler.ms_catalog_scraper = cls.ms_catalog_scraper

        cls.httpd = ThreadingHTTPServer(("127.0.0.1", 0), ServisHubHandler)
        cls.port = cls.httpd.server_address[1]
        cls.base_url = f"http://127.0.0.1:{cls.port}"

        cls.server_thread = threading.Thread(target=cls.httpd.serve_forever, daemon=True)
        cls.server_thread.start()

    @classmethod
    def tearDownClass(cls):
        cls.httpd.shutdown()
        cls.httpd.server_close()
        shutil.rmtree(cls.test_dir, ignore_errors=True)

    def _request(self, method: str, path: str, data: dict = None):
        url = f"{self.base_url}{path}"
        body = json.dumps(data).encode("utf-8") if data is not None else None
        req = urllib.request.Request(url, data=body, method=method)
        req.add_header("Content-Type", "application/json")
        try:
            with urllib.request.urlopen(req, timeout=5) as resp:
                headers = dict(resp.headers)
                raw = resp.read()
                if "application/json" in headers.get("Content-Type", ""):
                    return resp.status, json.loads(raw.decode("utf-8"))
                return resp.status, raw
        except urllib.error.HTTPError as e:
            raw = e.read()
            if "application/json" in e.headers.get("Content-Type", ""):
                return e.code, json.loads(raw.decode("utf-8"))
            return e.code, raw

    # -------------------------------------------------------------------------
    # 5.1 POST /api/drivers/resolve Integracija
    # -------------------------------------------------------------------------

    def test_01_resolve_dell_active_warranty_and_cab(self):
        """POST /api/drivers/resolve sa Dell Inc. i aktivnom garancijom (DL54201)."""
        payload = {
            "manufacturer": "Dell Inc.",
            "model": "Latitude 5420",
            "serial_number": "DL54201",
            "hwids": ["PCI\\VEN_8086&DEV_0A30"],
        }
        status, data = self._request("POST", "/api/drivers/resolve", payload)
        self.assertEqual(status, 200)
        self.assertEqual(data.get("status"), "ok")

        self.assertIn("warranty", data)
        warranty = data["warranty"]
        self.assertEqual(warranty.get("status"), "ok")
        self.assertIn("ProSupport", warranty.get("service_level", ""))

        self.assertIn("oem", data)
        oem = data["oem"]
        self.assertEqual(oem.get("status"), "ok")
        self.assertEqual(oem.get("package_name"), "Latitude-5420-win11-A07-6N8DF.cab")
        self.assertTrue(oem.get("download_url", "").startswith("https://downloads.dell.com"))

    def test_02_resolve_dell_expired_warranty(self):
        """POST /api/drivers/resolve sa DELL i isteklom garancijom (J3K9L22)."""
        payload = {
            "manufacturer": "DELL",
            "model": "Dell OptiPlex 7080",
            "serial": "J3K9L22",
        }
        status, data = self._request("POST", "/api/drivers/resolve", payload)
        self.assertEqual(status, 200)
        self.assertEqual(data.get("status"), "ok")

        self.assertIn("warranty", data)
        warranty = data["warranty"]
        self.assertEqual(warranty.get("status"), "ok")
        self.assertFalse(warranty.get("in_warranty"))
        self.assertEqual(warranty.get("status_label"), "Istekla garancija")

        self.assertIn("oem", data)
        self.assertEqual(data["oem"].get("package_name"), "OptiPlex-7080-win11-A08-4T8K9.cab")

    def test_03_resolve_hp_softpaq_integration(self):
        """POST /api/drivers/resolve sa Hewlett-Packard i SoftPaq paketom."""
        payload = {
            "manufacturer": "Hewlett-Packard",
            "model": "HP EliteBook 840 G7 Notebook PC",
            "serial_number": "5CG1234567",
        }
        status, data = self._request("POST", "/api/drivers/resolve", payload)
        self.assertEqual(status, 200)
        self.assertEqual(data.get("status"), "ok")

        self.assertIn("warranty", data)
        self.assertIn(data["warranty"].get("status"), ["ok", "fallback"])

        self.assertIn("oem", data)
        oem = data["oem"]
        self.assertEqual(oem.get("status"), "ok")
        self.assertEqual(oem.get("package_name"), "sp141520.exe")
        self.assertEqual(oem.get("source"), "hp_softpaq_known")

    def test_04_resolve_hp_inc_probook_model(self):
        """POST /api/drivers/resolve sa HP Inc. i ProBook 450 G8 modelom."""
        payload = {
            "manufacturer": "HP Inc.",
            "model": "ProBook 450 G8",
            "serial_number": "5CG9988776",
        }
        status, data = self._request("POST", "/api/drivers/resolve", payload)
        self.assertEqual(status, 200)
        self.assertEqual(data.get("status"), "ok")

        self.assertIn("oem", data)
        self.assertEqual(data["oem"].get("package_name"), "sp147320.exe")

    def test_05_resolve_lenovo_group_model(self):
        """POST /api/drivers/resolve sa LENOVO GROUP i ThinkPad 20UD modelom."""
        payload = {
            "manufacturer": "LENOVO GROUP",
            "model": "20UD",
            "serial_number": "PF123456",
        }
        status, data = self._request("POST", "/api/drivers/resolve", payload)
        self.assertEqual(status, 200)
        self.assertEqual(data.get("status"), "ok")

        self.assertIn("oem", data)
        self.assertEqual(data["oem"].get("package_name"), "tp_t14_gen1_amd_win11_20ud.exe")

    def test_06_resolve_unsupported_manufacturer_falls_back_safely(self):
        """POST /api/drivers/resolve sa Asus ili Acer proizvođačem nema oem/warranty sekcije."""
        payload = {
            "manufacturer": "Asus",
            "model": "ZenBook UX425",
            "hwids": ["PCI\\VEN_8086&DEV_9999"],
        }
        status, data = self._request("POST", "/api/drivers/resolve", payload)
        self.assertEqual(status, 200)
        self.assertEqual(data.get("status"), "ok")
        self.assertNotIn("warranty", data)
        self.assertNotIn("oem", data)
        self.assertEqual(data.get("count"), 1)

    def test_07_resolve_bad_request_validation(self):
        """POST /api/drivers/resolve validacija neispravnih tela zahteva."""
        # Telo nije rečnik
        status, data = self._request("POST", "/api/drivers/resolve", "INVALID")
        self.assertEqual(status, 400)

        # Nedostaju i HWID i OEM parametri
        status, data = self._request("POST", "/api/drivers/resolve", {})
        self.assertEqual(status, 400)

    # -------------------------------------------------------------------------
    # 5.2 POST /api/drivers/check_update Integracija
    # -------------------------------------------------------------------------

    def test_08_check_update_dell_driver_newer_in_cache(self):
        """
        POST /api/drivers/check_update sa Dell hardverom:
        Lokalni keš sadrži noviju verziju drajvera -> update_available=True.
        """
        dell_hwid = "PCI\\VEN_1028&DEV_0A30&SUBSYS_00011028"
        cached_meta = {
            "title": "Dell Realtek High Definition Audio Driver",
            "version": "6.0.9500.1",
            "date": "05/10/2024",
            "device_class": "MEDIA",
        }
        self.driver_cache_mgr.save_cache(dell_hwid, b"FAKE_DELL_AUDIO_CAB" * 64, cached_meta)

        # 1. Klijent ima stariju verziju 6.0.9100.1 -> Update dostupan
        payload_older = {
            "device_name": "Dell Latitude 5420 - Audio",
            "hwids": [dell_hwid],
            "current_version": "6.0.9100.1",
            "current_date": "01/15/2022",
        }
        status, data = self._request("POST", "/api/drivers/check_update", payload_older)
        self.assertEqual(status, 200)
        self.assertEqual(data.get("status"), "ok")
        self.assertTrue(data.get("update_available"))
        self.assertEqual(data.get("latest_version"), "6.0.9500.1")
        self.assertEqual(data.get("title"), "Dell Realtek High Definition Audio Driver")
        self.assertEqual(data.get("source"), "cache")

        # 2. Klijent ima identičnu verziju 6.0.9500.1 -> Update NIJE dostupan
        payload_same = {
            "device_name": "Dell Latitude 5420 - Audio",
            "hwids": [dell_hwid],
            "current_version": "6.0.9500.1",
            "current_date": "05/10/2024",
        }
        status_same, data_same = self._request("POST", "/api/drivers/check_update", payload_same)
        self.assertEqual(status_same, 200)
        self.assertFalse(data_same.get("update_available"))
        self.assertEqual(data_same.get("latest_version"), "6.0.9500.1")

    def test_09_check_update_hp_driver_newer_in_cache(self):
        """
        POST /api/drivers/check_update sa HP hardverom:
        Lokalni keš sadrži noviju verziju drajvera -> update_available=True.
        """
        hp_hwid = "PCI\\VEN_103C&DEV_8723&SUBSYS_8723103C"
        cached_meta = {
            "title": "HP Synaptics Touchpad Driver",
            "version": "19.5.35.41",
            "date": "04/20/2024",
            "device_class": "HIDClass",
        }
        self.driver_cache_mgr.save_cache(hp_hwid, b"FAKE_HP_TOUCHPAD_CAB" * 64, cached_meta)

        payload_older = {
            "device_name": "HP EliteBook 840 G7 - Touchpad",
            "hwids": [hp_hwid],
            "current_version": "19.5.10.0",
            "current_date": "02/10/2022",
        }
        status, data = self._request("POST", "/api/drivers/check_update", payload_older)
        self.assertEqual(status, 200)
        self.assertEqual(data.get("status"), "ok")
        self.assertTrue(data.get("update_available"))
        self.assertEqual(data.get("latest_version"), "19.5.35.41")
        self.assertEqual(data.get("title"), "HP Synaptics Touchpad Driver")

    def test_10_check_update_validation_errors(self):
        """POST /api/drivers/check_update validacija neispravnih parametara."""
        # 1. Prazno telo ili nije rečnik
        status, data = self._request("POST", "/api/drivers/check_update", [])
        self.assertEqual(status, 400)

        # 2. Nedostaje hwids polje
        status, data = self._request("POST", "/api/drivers/check_update", {"device_name": "Test"})
        self.assertEqual(status, 400)


if __name__ == "__main__":
    unittest.main()
