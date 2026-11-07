"""
servishub.drivers
=================
Podsistem za pametnu rezoluciju, normalizaciju HWID-ja, pretragu Microsoft
Update Catalog-a i lokalno keširanje drajvera.
"""

from servishub.drivers.hwid import generate_hwid_candidates
from servishub.drivers.version import (
    parse_driver_date,
    parse_driver_version_components,
    is_catalog_driver_newer,
)
from servishub.drivers.scraper import MsUpdateCatalogScraper
from servishub.drivers.cache import DriverCacheManager, sanitize_id
from servishub.drivers.oem import (
    BaseOEMProvider,
    LenovoOEMProvider,
    get_oem_provider,
)

__all__ = [
    "generate_hwid_candidates",
    "parse_driver_date",
    "parse_driver_version_components",
    "is_catalog_driver_newer",
    "MsUpdateCatalogScraper",
    "DriverCacheManager",
    "sanitize_id",
    "BaseOEMProvider",
    "LenovoOEMProvider",
    "get_oem_provider",
]

