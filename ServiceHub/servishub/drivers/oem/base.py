"""
servishub.drivers.oem.base
==========================
Definicija apstraktne bazne klase za OEM provajdere (BaseOEMProvider).
Svaki podržani proizvođač (Lenovo, Dell, HP itd.) implementira ovaj interfejs
za proveru statusa fabričke garancije, rezoluciju SCCM/enterprise drajver paketa
i proveru dostupnosti novijih BIOS ažuriranja.
"""

from abc import ABC, abstractmethod
from typing import Any, Dict, List, Optional


KEYBOARD_LAYOUT_VARIANTS = (
    "laptop-tkl-ansi",
    "laptop-tkl-iso",
    "laptop-tkl-nav-ansi",
    "laptop-tkl-nav-iso",
    "laptop-numpad-ansi",
    "laptop-numpad-iso",
    "thinkpad-tkl-ansi",
    "thinkpad-tkl-iso",
    "thinkpad-numpad-ansi",
    "thinkpad-numpad-iso",
    "desktop-104-ansi",
    "desktop-105-iso",
)


def make_keyboard_layout_name(
    has_numpad: bool,
    is_iso: bool,
    is_desktop: bool = False,
    has_nav_column: bool = False,
    is_thinkpad: bool = False,
) -> str:
    """Kreira kanonski naziv osnovnog šablona tastature iz fizičkih osobina."""
    if is_desktop:
        return "desktop-105-iso" if is_iso else "desktop-104-ansi"
    if is_thinkpad:
        if has_numpad:
            return "thinkpad-numpad-iso" if is_iso else "thinkpad-numpad-ansi"
        return "thinkpad-tkl-iso" if is_iso else "thinkpad-tkl-ansi"
    if has_numpad:
        return "laptop-numpad-iso" if is_iso else "laptop-numpad-ansi"
    if has_nav_column:
        return "laptop-tkl-nav-iso" if is_iso else "laptop-tkl-nav-ansi"
    return "laptop-tkl-iso" if is_iso else "laptop-tkl-ansi"


def build_keyboard_spec(
    status: str = "fallback",
    source: str = "oem_base_fallback",
    confidence: str = "low",
    base_layout: Optional[str] = None,
    has_numpad: Optional[bool] = None,
    is_iso: Optional[bool] = None,
    has_copilot: bool = False,
    oem_part_description: str = "",
    added_keys: Optional[List[str]] = None,
    removed_keys: Optional[List[str]] = None,
    raw_data: Optional[Dict[str, Any]] = None,
    has_nav_column: Optional[bool] = None,
) -> Dict[str, Any]:
    """Gradi standardizovani rečnik specifikacije tastature za sve OEM provajdere."""
    keys_added = list(added_keys or [])
    if is_iso is True and "Oem102" not in keys_added:
        keys_added.append("Oem102")
    if has_copilot and "Copilot" not in keys_added:
        keys_added.append("Copilot")

    nav_flag = bool(has_nav_column) if has_nav_column is not None else (
        bool(base_layout and "-nav-" in base_layout)
    )

    return {
        "status": status,
        "source": source,
        "confidence": confidence,
        "base_layout": base_layout,
        "has_numpad": has_numpad,
        "is_iso": is_iso,
        "has_nav_column": nav_flag,
        "has_copilot": bool(has_copilot),
        "oem_part_description": oem_part_description or "",
        "added_keys": keys_added,
        "removed_keys": list(removed_keys or []),
        "raw_data": raw_data if isinstance(raw_data, dict) else {},
    }


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

    def lookup_keyboard_spec(
        self,
        serial: str = "",
        model: str = "",
        product_number: str = "",
    ) -> Dict[str, Any]:
        """
        Podrazumevana (ne-apstraktna) metoda za određivanje fizičkog rasporeda tastature
        na osnovu serijskog broja, modela i SKU/MTM koda.
        """
        return build_keyboard_spec(
            status="fallback",
            source="oem_base_fallback",
            confidence="low",
            raw_data={"serial": serial, "model": model, "product_number": product_number},
        )
