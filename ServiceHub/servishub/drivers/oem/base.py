"""
servishub.drivers.oem.base
==========================
Definicija apstraktne bazne klase za OEM provajdere (BaseOEMProvider).
Svaki podržani proizvođač (Lenovo, Dell, HP itd.) implementira ovaj interfejs
za proveru statusa fabričke garancije, rezoluciju SCCM/enterprise drajver paketa
i proveru dostupnosti novijih BIOS ažuriranja.
"""

from abc import ABC, abstractmethod
from typing import Any, Dict


class BaseOEMProvider(ABC):
    """
    Apstraktna bazna klasa za OEM servisne i drajverske provajdere.
    """

    @abstractmethod
    def get_warranty_info(self, serial: str, model: str = "") -> Dict[str, Any]:
        """
        Dohvata podatke o fabričkoj garanciji uređaja na osnovu serijskog broja i modela.

        :param serial: Serijski broj uređaja (npr. 'PF123456')
        :param model: Model ili MTM kod računara (opciono)
        :return: Rečnik sa strukturom:
            {
                "status": "ok" | "error" | "fallback",
                "in_warranty": bool,
                "status_label": str,
                "service_level": str,
                "start_date": str,
                "end_date": str,
                "days_remaining": int,
                "raw_data": dict
            }
        """
        pass

    @abstractmethod
    def resolve_driver_pack(self, model: str, os_ver: str = "win11-x64") -> Dict[str, Any]:
        """
        Pronalazi zvanični OEM / SCCM paket drajvera za dati model i verziju OS-a.
        Proverava lokalni keš, a u slučaju odsustva pronalazi/simulira metapodatke
        sa zvaničnog portala.

        :param model: Naziv modela ili Machine Type Model (MTM) kod
        :param os_ver: Ciljana verzija operativnog sistema (npr. 'win11-x64', 'win10-x64')
        :return: Rečnik sa strukturom:
            {
                "status": "ok" | "not_found" | "error",
                "source": str,
                "package_name": str,
                "download_url": str,
                "local_path": str,
                "size_bytes": int
            }
        """
        pass

    @abstractmethod
    def check_bios_update(self, model: str, current_bios: str = "") -> Dict[str, Any]:
        """
        Proverava dostupnost novije verzije BIOS/UEFI firmware-a za dati model.

        :param model: Naziv modela ili Machine Type Model (MTM) kod
        :param current_bios: Trenutno instalirana verzija BIOS-a
        :return: Rečnik sa strukturom:
            {
                "update_available": bool,
                "latest_version": str,
                "current_version": str,
                "download_url": str,
                "release_date": str
            }
        """
        pass
