"""
servishub.drivers.oem
=====================
Modularni OEM podsistem za Servis Hub.
Podržava specifične OEM provajdere (Lenovo, Dell, HP itd.) za proveru fabričke
garancije, rezoluciju zvaničnih SCCM/enterprise drajver paketa i proveru
dostupnosti novijih BIOS/UEFI firmware verzija.
"""

from typing import Optional

from servishub.drivers.oem.base import BaseOEMProvider
from servishub.drivers.oem.lenovo import LenovoOEMProvider
from servishub.drivers.oem.dell import DellOEMProvider
from servishub.drivers.oem.hp import HpOEMProvider

__all__ = [
    "BaseOEMProvider",
    "LenovoOEMProvider",
    "DellOEMProvider",
    "HpOEMProvider",
    "get_oem_provider",
]


def get_oem_provider(manufacturer: str, **kwargs) -> Optional[BaseOEMProvider]:
    """
    Instancira odgovarajućeg OEM provajdera na osnovu imena proizvođača hardvera.

    Podržava case-insensitive pretragu (npr. 'LENOVO', 'lenovo', 'DELL', 'Dell Inc.').
    Vraća None ukoliko provajder za zadatog proizvođača nije registrovan.

    :param manufacturer: Naziv proizvođača (npr. 'Lenovo', 'Dell', 'HP')
    :param kwargs: Dodatni argumenti prosleđeni konstruktoru provajdera (npr. cache_dir, timeout)
    :return: Instanca BaseOEMProvider podklase ili None
    """
    if not manufacturer or not isinstance(manufacturer, str):
        return None

    mfg_clean = manufacturer.strip().upper()

    if "LENOVO" in mfg_clean:
        return LenovoOEMProvider(**kwargs)

    if "DELL" in mfg_clean:
        return DellOEMProvider(**kwargs)

    if "HP" in mfg_clean or "HEWLETT" in mfg_clean:
        return HpOEMProvider(**kwargs)

    return None
