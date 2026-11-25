"""
servishub.drivers.oem
=====================
Modularni OEM podsistem za Servis Hub.
Podržava specifične OEM provajdere (Lenovo, Dell, HP, Microsoft Surface, Fujitsu)
kao i Hybrid Drop-in adapter za Tier 2 proizvođače (ASUS, Acer, MSI, Dynabook).
"""

import re
from typing import Optional

from servishub.drivers.oem.base import BaseOEMProvider
from servishub.drivers.oem.lenovo import LenovoOEMProvider
from servishub.drivers.oem.dell import DellOEMProvider
from servishub.drivers.oem.hp import HpOEMProvider
from servishub.drivers.oem.microsoft_surface import MicrosoftOEMProvider, extract_msi_drivers
from servishub.drivers.oem.fujitsu import FujitsuOEMProvider, parse_fujitsu_catalog_xml
from servishub.drivers.oem.dropin import DropInOEMProvider
from servishub.drivers.oem.catalogs import (
    TIER1_OEM_VENDORS,
    TIER2_DROPIN_VENDORS,
    OemCatalogManager,
    detect_oem_vendor,
)

__all__ = [
    "BaseOEMProvider",
    "LenovoOEMProvider",
    "DellOEMProvider",
    "HpOEMProvider",
    "MicrosoftOEMProvider",
    "FujitsuOEMProvider",
    "DropInOEMProvider",
    "OemCatalogManager",
    "TIER1_OEM_VENDORS",
    "TIER2_DROPIN_VENDORS",
    "detect_oem_vendor",
    "extract_msi_drivers",
    "parse_fujitsu_catalog_xml",
    "get_oem_provider",
]


def get_oem_provider(
    manufacturer: str,
    model: str = "",
    **kwargs,
) -> Optional[BaseOEMProvider]:
    """
    Instancira odgovarajućeg Tier 1 OEM provajdera na osnovu imena proizvođača hardvera
    (i opciono modela).

    Podržava case-insensitive pretragu za Lenovo, Dell, HP, Microsoft Surface i Fujitsu.
    Za Microsoft zahteva i 'SURFACE' (npr. 'Microsoft Surface' ili model='Surface ...')
    kako se generički 'Microsoft Corporation' VM ne bi tretirao kao Surface.
    Za Tier 2 drop-in proizvođače (ASUS, Acer, MSI, Toshiba/Dynabook) vraća None
    (oni koriste `detect_oem_vendor` + `OemCatalogManager` / `DropInOEMProvider`).
    """
    if not manufacturer or not isinstance(manufacturer, str):
        return None

    mfg_clean = manufacturer.strip().upper()
    combined = f"{mfg_clean} {str(model or '').strip().upper()}".strip()

    if "LENOVO" in mfg_clean:
        return LenovoOEMProvider(**kwargs)

    if "DELL" in mfg_clean:
        return DellOEMProvider(**kwargs)

    if "HP" in mfg_clean or "HEWLETT" in mfg_clean:
        return HpOEMProvider(**kwargs)

    if re.search(r"(?<![A-Z0-9])SURFACE(?![A-Z0-9])", combined):
        return MicrosoftOEMProvider(**kwargs)

    if re.search(r"(?<![A-Z0-9])(FUJITSU|LIFEBOOK|ESPRIMO|CELSIUS)(?![A-Z0-9])", combined):
        return FujitsuOEMProvider(**kwargs)

    return None
