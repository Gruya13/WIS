"""
tests.test_driver_versions
==========================
Verifikacija funkcija za poređenje verzija i datuma drajvera
u servishub.drivers.version modulu.
"""

import unittest
from servishub.drivers.version import (
    is_catalog_driver_newer,
    parse_driver_date,
    parse_driver_version_components,
)


class TestDriverVersions(unittest.TestCase):

    def test_date_comparison(self):
        # 1. Datum: katalog noviji (2023 vs 2018)
        self.assertTrue(is_catalog_driver_newer("6.0.1.8500", "05/15/2018", "6.0.9500.1", "10/20/2023"))
        # 2. Datum: katalog stariji (2018 vs 2023)
        self.assertFalse(is_catalog_driver_newer("6.0.9500.1", "10/20/2023", "6.0.1.8500", "05/15/2018"))

    def test_version_comparison_with_same_dates(self):
        # 3. Isti datumi, veća kataloška verzija
        self.assertTrue(is_catalog_driver_newer("10.0.19041.1", "2023-01-15", "10.0.19041.5", "2023-01-15"))
        # 4. Isti datumi, manja kataloška verzija
        self.assertFalse(is_catalog_driver_newer("10.0.19041.5", "2023-01-15", "10.0.19041.1", "2023-01-15"))
        # 5. Potpuno jednaki datum i verzija
        self.assertFalse(is_catalog_driver_newer("10.0.19041.1", "2023-01-15", "10.0.19041.1", "2023-01-15"))

    def test_version_without_dates(self):
        self.assertTrue(is_catalog_driver_newer("1.0.0", "", "1.0.1", ""))
        self.assertFalse(is_catalog_driver_newer("2.5.0", "unknown", "2.4.9", "unknown"))

    def test_wmi_and_european_dates(self):
        # WMI datum format (20180515...) vs ISO datum (2023-10-20)
        self.assertTrue(is_catalog_driver_newer("6.0.1.8500", "20180515000000.000000-000", "6.0.9500.1", "2023-10-20"))
        # Evropski tačkasti format
        self.assertTrue(is_catalog_driver_newer("1.0", "15.05.2018.", "2.0", "20.10.2023"))


if __name__ == "__main__":
    unittest.main()
