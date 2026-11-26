"""
servishub.drivers.oem.microsoft_surface
=======================================
OEM provajder i bezbedna MSI ekstrakcija za Microsoft Surface uređaje
(Surface Pro, Surface Laptop, Surface Book, Surface Go, Surface Studio).

Podržava:
- Javnu funkciju `extract_msi_drivers(msi_path, dest_dir)` koja koristi `msiextract -C <tmp> <msi_path>`
  iz paketa `msitools`, kopira isključivo dozvoljene drajverske ekstenzije, nikada ne izvršava
  `.exe`/`.msi` kod i uvek briše privremeni direktorijum u `finally`.
- Mapiranje Surface modela po `SystemSKU` i OS buildu (radi tačnog razlikovanja Intel x64 vs
  Snapdragon ARM64 i for-Business revizija) sa zvaničnim `download.microsoft.com` HTTPS URL-ovima.
- Parsiranje prilagođenih JSON/XML Surface kataloga uz `_check_xml_safe` (XXE zaštita) i
  `validate_oem_download_url`.
"""

from __future__ import annotations

import json
import os
import re
import shutil
import subprocess
import tempfile
import threading
import urllib.error
import urllib.parse
import urllib.request
import xml.etree.ElementTree as ET
from datetime import date, datetime
from typing import Any, Callable, Dict, List, Optional, Sequence, Tuple

from servishub.drivers.oem.base import (
    BaseOEMProvider,
    build_keyboard_spec,
    make_keyboard_layout_name,
)
from servishub.drivers.oem.catalogs import (
    MAX_CATALOG_BYTES,
    _check_xml_safe,
    _extract_os_family_and_feature,
    _norm_model_tokens,
    _parse_iso_or_us_date,
    _strip_ns,
    validate_oem_download_url,
)

SURFACE_SUPPORT_INDEX_URL = (
    "https://support.microsoft.com/en-us/surface/"
    "download-drivers-and-firmware-for-surface-09bb2e09-2a4b-cb69-0951-078a7739e120"
)
_DOWNLOAD_CENTER_DETAILS_TMPL = "https://www.microsoft.com/en-us/download/details.aspx?id={download_id}"
_MSI_URL_RE = re.compile(r"https?://[^\s\"'<>\\]+\.msi(?:\?[^\s\"'<>\\]*)?", re.IGNORECASE)
_MSI_VER_RE = re.compile(r"(\d+\.\d+\.\d+(?:\.\d+)?)\.msi$", re.IGNORECASE)

ALLOWED_MSI_DRIVER_EXTENSIONS: Tuple[str, ...] = (
    ".inf",
    ".sys",
    ".cat",
    ".dll",
    ".bin",
    ".dat",
    ".fw",
    ".icm",
    ".efi",
    ".cap",
    ".rom",
    ".man",
    ".ini",
)


def extract_msi_drivers(msi_path: str, dest_dir: str) -> List[str]:
    """
    Bezbedno ekstrahuje drajverske fajlove iz Microsoft Surface `.msi` paketa
    koristeći `msiextract -C <tmp> <msi_path>`.

    Kopira isključivo dozvoljene drajverske ekstenzije (`ALLOWED_MSI_DRIVER_EXTENSIONS`),
    nikada ne izvršava `.exe`/`.msi` kod, štiti od path traversala i symlinkova,
    i obavezno briše privremeni direktorijum u `finally` bloku.

    :param msi_path: Putanja do `.msi` paketa
    :param dest_dir: Odredišni direktorijum za izdvojene drajvere
    :return: Sortirana lista apsolutnih putanja kopiranih drajverskih fajlova
    """
    if not msi_path or not isinstance(msi_path, str):
        raise ValueError("Putanja do MSI paketa je prazna.")
    if os.path.islink(msi_path):
        raise ValueError(f"Odbijena ekstrakcija simboličkog linka: {msi_path}")
    if not os.path.isfile(msi_path):
        raise FileNotFoundError(f"MSI paket ne postoji: {msi_path}")

    if not dest_dir or not isinstance(dest_dir, str):
        raise ValueError("Odredišni direktorijum je prazan.")
    if os.path.islink(dest_dir):
        raise ValueError(f"Odredišni direktorijum ne sme biti simbolički link: {dest_dir}")

    msiextract_bin = shutil.which("msiextract")
    if not msiextract_bin:
        raise RuntimeError(
            "Nedostaje alat 'msiextract' (paket msitools) za ekstrakciju .msi paketa."
        )

    abs_msi = os.path.abspath(msi_path)
    tmp_dir = tempfile.mkdtemp(prefix="wis-msi-extract-")
    extracted_paths: List[str] = []

    try:
        proc = subprocess.run(
            [msiextract_bin, "-C", tmp_dir, abs_msi],
            capture_output=True,
            text=True,
            timeout=600,
            check=False,
        )
        if proc.returncode != 0:
            err_msg = (proc.stderr or proc.stdout or f"exit code {proc.returncode}").strip()[:400]
            raise RuntimeError(
                f"Ekstrakcija MSI paketa '{os.path.basename(abs_msi)}' nije uspela: {err_msg}"
            )

        os.makedirs(dest_dir, exist_ok=True)
        real_tmp = os.path.realpath(tmp_dir)
        real_dest = os.path.realpath(dest_dir)

        for dirpath, dirnames, filenames in os.walk(tmp_dir, followlinks=False):
            # Izbegavamo ulazak u eventualne symlink poddirektorijume
            dirnames[:] = [
                d for d in sorted(dirnames)
                if not os.path.islink(os.path.join(dirpath, d))
            ]
            for fn in sorted(filenames):
                src_fp = os.path.join(dirpath, fn)
                if os.path.islink(src_fp):
                    continue
                real_src = os.path.realpath(src_fp)
                if os.path.commonpath([real_tmp, real_src]) != real_tmp:
                    raise ValueError(f"Detektovan path traversal u ekstrahovanom MSI stablu: {fn}")

                _, ext = os.path.splitext(fn)
                if ext.lower() not in ALLOWED_MSI_DRIVER_EXTENSIONS:
                    continue

                rel_p = os.path.relpath(src_fp, tmp_dir)
                dst_fp = os.path.realpath(os.path.join(real_dest, rel_p))
                if os.path.commonpath([real_dest, dst_fp]) != real_dest:
                    raise ValueError(f"Detektovan path traversal pri kopiranju MSI drajvera: {rel_p}")

                os.makedirs(os.path.dirname(dst_fp), exist_ok=True)
                shutil.copy2(src_fp, dst_fp)
                extracted_paths.append(dst_fp)

        return sorted(extracted_paths)
    finally:
        shutil.rmtree(tmp_dir, ignore_errors=True)


def extract_surface_os_build(os_ver: str = "") -> str:
    """Normalizuje OS build broj iz stringa poput 'win11-24h2', '26100', '19045'."""
    up = str(os_ver or "").upper()
    for bld in ("26200", "26100", "22631", "22621", "22000", "19045", "19044", "19043"):
        if bld in up:
            return bld
    if "24H2" in up:
        return "26100"
    if "23H2" in up:
        return "22631"
    if "22H2" in up:
        return "19045" if ("WIN10" in up or "WINDOWS 10" in up) else "22621"
    if "WIN10" in up or "WINDOWS 10" in up:
        return "19045"
    if "WIN11" in up or "WINDOWS 11" in up:
        return "22621"
    return ""


# Kanonski indeks Microsoft Surface modela po SystemSKU i OS buildu sa zvaničnim
# Microsoft Download Center stranicama (bez sintetičkih/placeholder .msi CDN linkova — IMP-98 & IMP-113).
# Stvarni .msi paketi se preuzimaju dinamičkim fetchom + HTTP HEAD verifikacijom ili preko DropInOEMProvider-a.
SURFACE_CATALOG_SPECS: Tuple[Dict[str, Any], ...] = (
    {
        "package_id": "SurfacePro7_Win11_22621",
        "package_name": "SurfacePro7_Win11_22621.msi",
        "version": "",
        "release_date": "2024-06-15",
        "os": "win11",
        "os_build": "22621",
        "os_feature_rank": 222,
        "arch": "x64",
        "download_center_id": "100419",
        "download_page_url": _DOWNLOAD_CENTER_DETAILS_TMPL.format(download_id="100419"),
        "download_url": "",
        "dropin_hint": "<dropin_dir>/microsoft/Surface_Pro_7/*.msi",
        "size_bytes": 0,
        "models": ["Surface Pro 7"],
        "system_skus": ["Surface_Pro_7_1866"],
        "bios_version": "15.11.140",
    },
    {
        "package_id": "SurfacePro7Plus_Win11_22621",
        "package_name": "SurfacePro7Plus_Win11_22621.msi",
        "version": "",
        "release_date": "2024-08-20",
        "os": "win11",
        "os_build": "22621",
        "os_feature_rank": 222,
        "arch": "x64",
        "download_center_id": "102633",
        "download_page_url": _DOWNLOAD_CENTER_DETAILS_TMPL.format(download_id="102633"),
        "download_url": "",
        "dropin_hint": "<dropin_dir>/microsoft/Surface_Pro_7_Plus/*.msi",
        "size_bytes": 0,
        "models": ["Surface Pro 7+", "Surface Pro 7 Plus"],
        "system_skus": ["Surface_Pro_7+_1960", "Surface_Pro_7+_1961"],
        "bios_version": "21.101.143",
    },
    {
        "package_id": "SurfacePro8_Win11_22621",
        "package_name": "SurfacePro8_Win11_22621.msi",
        "version": "",
        "release_date": "2024-09-12",
        "os": "win11",
        "os_build": "22621",
        "os_feature_rank": 222,
        "arch": "x64",
        "download_center_id": "103503",
        "download_page_url": _DOWNLOAD_CENTER_DETAILS_TMPL.format(download_id="103503"),
        "download_url": "",
        "dropin_hint": "<dropin_dir>/microsoft/Surface_Pro_8/*.msi",
        "size_bytes": 0,
        "models": ["Surface Pro 8", "Surface Pro 8 for Business"],
        "system_skus": ["Surface_Pro_8_1983", "Surface_Pro_8_for_Business_1983", "Surface_Pro_8_LTE_1982"],
        "bios_version": "25.102.141",
    },
    {
        "package_id": "SurfacePro9_Win11_26100",
        "package_name": "SurfacePro9_Win11_26100.msi",
        "version": "",
        "release_date": "2025-01-20",
        "os": "win11",
        "os_build": "26100",
        "os_feature_rank": 242,
        "arch": "x64",
        "download_center_id": "104680",
        "download_page_url": _DOWNLOAD_CENTER_DETAILS_TMPL.format(download_id="104680"),
        "download_url": "",
        "dropin_hint": "<dropin_dir>/microsoft/Surface_Pro_9/*.msi",
        "size_bytes": 0,
        "models": ["Surface Pro 9", "Surface Pro 9 for Business"],
        "system_skus": ["Surface_Pro_9_2038", "Surface_Pro_9_for_Business_2038"],
        "bios_version": "18.101.143",
    },
    {
        "package_id": "SurfacePro9_Win11_22621",
        "package_name": "SurfacePro9_Win11_22621.msi",
        "version": "",
        "release_date": "2024-10-15",
        "os": "win11",
        "os_build": "22621",
        "os_feature_rank": 222,
        "arch": "x64",
        "download_center_id": "104680",
        "download_page_url": _DOWNLOAD_CENTER_DETAILS_TMPL.format(download_id="104680"),
        "download_url": "",
        "dropin_hint": "<dropin_dir>/microsoft/Surface_Pro_9/*.msi",
        "size_bytes": 0,
        "models": ["Surface Pro 9", "Surface Pro 9 for Business"],
        "system_skus": ["Surface_Pro_9_2038", "Surface_Pro_9_for_Business_2038"],
        "bios_version": "18.100.143",
    },
    {
        "package_id": "SurfacePro9With5G_Win11_22621",
        "package_name": "SurfacePro9With5G_Win11_22621.msi",
        "version": "",
        "release_date": "2024-08-25",
        "os": "win11",
        "os_build": "22621",
        "os_feature_rank": 222,
        "arch": "arm64",
        "download_center_id": "105941",
        "download_page_url": _DOWNLOAD_CENTER_DETAILS_TMPL.format(download_id="105941"),
        "download_url": "",
        "dropin_hint": "<dropin_dir>/microsoft/Surface_Pro_9_with_5G/*.msi",
        "size_bytes": 0,
        "models": ["Surface Pro 9 with 5G", "Surface Pro 9 5G"],
        "system_skus": ["Surface_Pro_9_With_5G_1997", "Surface_Pro_9_5G_2046"],
        "bios_version": "14.101.143",
    },
    {
        "package_id": "SurfacePro10_Win11_26100",
        "package_name": "SurfacePro10_Win11_26100.msi",
        "version": "",
        "release_date": "2025-02-18",
        "os": "win11",
        "os_build": "26100",
        "os_feature_rank": 242,
        "arch": "x64",
        "download_center_id": "105947",
        "download_page_url": _DOWNLOAD_CENTER_DETAILS_TMPL.format(download_id="105947"),
        "download_url": "",
        "dropin_hint": "<dropin_dir>/microsoft/Surface_Pro_10_for_Business/*.msi",
        "size_bytes": 0,
        "models": ["Surface Pro 10", "Surface Pro 10 for Business"],
        "system_skus": ["Surface_Pro_10_for_Business_2079", "Surface_Pro_10_2079"],
        "bios_version": "10.104.143",
    },
    {
        "package_id": "SurfacePro11_ARM64_Win11_26100",
        "package_name": "SurfacePro11thEdition_ARM64_Win11_26100.msi",
        "version": "",
        "release_date": "2025-03-10",
        "os": "win11",
        "os_build": "26100",
        "os_feature_rank": 242,
        "arch": "arm64",
        "download_center_id": "106119",
        "download_page_url": _DOWNLOAD_CENTER_DETAILS_TMPL.format(download_id="106119"),
        "download_url": "",
        "dropin_hint": "<dropin_dir>/microsoft/Surface_Pro_11th_Edition/*.msi",
        "size_bytes": 0,
        "models": ["Surface Pro 11th Edition", "Surface Pro Copilot+ PC"],
        "system_skus": ["Surface_Pro_11th_Edition_2076"],
        "bios_version": "11.102.143",
    },
    {
        "package_id": "SurfacePro11_Intel_Win11_26100",
        "package_name": "SurfacePro11thEditionforBusiness_Win11_26100.msi",
        "version": "",
        "release_date": "2025-03-14",
        "os": "win11",
        "os_build": "26100",
        "os_feature_rank": 242,
        "arch": "x64",
        "download_center_id": "108013",
        "download_page_url": _DOWNLOAD_CENTER_DETAILS_TMPL.format(download_id="108013"),
        "download_url": "",
        "dropin_hint": "<dropin_dir>/microsoft/Surface_Pro_11th_Edition_for_Business/*.msi",
        "size_bytes": 0,
        "models": ["Surface Pro 11th Edition for Business", "Surface Pro for Business 11th Edition with Intel"],
        "system_skus": ["Surface_Pro_11th_Edition_for_Business_2110"],
        "bios_version": "12.101.143",
    },
    {
        "package_id": "SurfaceLaptop3_Intel_Win11_22621",
        "package_name": "SurfaceLaptop3_Win11_22621.msi",
        "version": "",
        "release_date": "2024-05-10",
        "os": "win11",
        "os_build": "22621",
        "os_feature_rank": 222,
        "arch": "x64",
        "download_center_id": "100429",
        "download_page_url": _DOWNLOAD_CENTER_DETAILS_TMPL.format(download_id="100429"),
        "download_url": "",
        "dropin_hint": "<dropin_dir>/microsoft/Surface_Laptop_3/*.msi",
        "size_bytes": 0,
        "models": ["Surface Laptop 3"],
        "system_skus": ["Surface_Laptop_3_1867:1868", "Surface_Laptop_3_1867", "Surface_Laptop_3_1872"],
        "bios_version": "17.101.140",
    },
    {
        "package_id": "SurfaceLaptop4_Intel_Win11_22621",
        "package_name": "SurfaceLaptop4_Intel_Win11_22621.msi",
        "version": "",
        "release_date": "2024-07-18",
        "os": "win11",
        "os_build": "22621",
        "os_feature_rank": 222,
        "arch": "x64",
        "download_center_id": "102924",
        "download_page_url": _DOWNLOAD_CENTER_DETAILS_TMPL.format(download_id="102924"),
        "download_url": "",
        "dropin_hint": "<dropin_dir>/microsoft/Surface_Laptop_4/*.msi",
        "size_bytes": 0,
        "models": ["Surface Laptop 4"],
        "system_skus": ["Surface_Laptop_4_1950:1951", "Surface_Laptop_4_1950", "Surface_Laptop_4_1951", "Surface_Laptop_4_1978:1979"],
        "bios_version": "21.102.143",
    },
    {
        "package_id": "SurfaceLaptop5_Win11_26100",
        "package_name": "SurfaceLaptop5_Win11_26100.msi",
        "version": "",
        "release_date": "2025-02-05",
        "os": "win11",
        "os_build": "26100",
        "os_feature_rank": 242,
        "arch": "x64",
        "download_center_id": "104679",
        "download_page_url": _DOWNLOAD_CENTER_DETAILS_TMPL.format(download_id="104679"),
        "download_url": "",
        "dropin_hint": "<dropin_dir>/microsoft/Surface_Laptop_5/*.msi",
        "size_bytes": 0,
        "models": ["Surface Laptop 5", "Surface Laptop 5 for Business"],
        "system_skus": ["Surface_Laptop_5_1950:1951", "Surface_Laptop_5_1950", "Surface_Laptop_5_1951", "Surface_Laptop_5_1979", "Surface_Laptop_5_for_Business_1950"],
        "bios_version": "16.101.143",
    },
    {
        "package_id": "SurfaceLaptop6_Win11_26100",
        "package_name": "SurfaceLaptop6forBusiness_Win11_26100.msi",
        "version": "",
        "release_date": "2025-03-01",
        "os": "win11",
        "os_build": "26100",
        "os_feature_rank": 242,
        "arch": "x64",
        "download_center_id": "105946",
        "download_page_url": _DOWNLOAD_CENTER_DETAILS_TMPL.format(download_id="105946"),
        "download_url": "",
        "dropin_hint": "<dropin_dir>/microsoft/Surface_Laptop_6_for_Business/*.msi",
        "size_bytes": 0,
        "models": ["Surface Laptop 6 for Business", "Surface Laptop 6"],
        "system_skus": ["Surface_Laptop_6_for_Business_2081", "Surface_Laptop_6_2080"],
        "bios_version": "12.104.143",
    },
    {
        "package_id": "SurfaceLaptop7_ARM64_Win11_26100",
        "package_name": "SurfaceLaptop7thEdition_ARM64_Win11_26100.msi",
        "version": "",
        "release_date": "2025-03-08",
        "os": "win11",
        "os_build": "26100",
        "os_feature_rank": 242,
        "arch": "arm64",
        "download_center_id": "106120",
        "download_page_url": _DOWNLOAD_CENTER_DETAILS_TMPL.format(download_id="106120"),
        "download_url": "",
        "dropin_hint": "<dropin_dir>/microsoft/Surface_Laptop_7th_Edition/*.msi",
        "size_bytes": 0,
        "models": ["Surface Laptop 7th Edition"],
        "system_skus": ["Surface_Laptop_7th_Edition_2036", "Surface_Laptop_7th_Edition_2037"],
        "bios_version": "11.103.143",
    },
    {
        "package_id": "SurfaceLaptop7_Intel_Win11_26100",
        "package_name": "SurfaceLaptop7thEditionforBusiness_Win11_26100.msi",
        "version": "",
        "release_date": "2025-04-02",
        "os": "win11",
        "os_build": "26100",
        "os_feature_rank": 242,
        "arch": "x64",
        "download_center_id": "108014",
        "download_page_url": _DOWNLOAD_CENTER_DETAILS_TMPL.format(download_id="108014"),
        "download_url": "",
        "dropin_hint": "<dropin_dir>/microsoft/Surface_Laptop_7th_Edition_for_Business/*.msi",
        "size_bytes": 0,
        "models": [
            "Surface Laptop 7th Edition for Business",
            "Surface Laptop for Business 7th Edition with Intel",
        ],
        "system_skus": [
            "Surface_Laptop_7th_Edition_for_Business_2095",
            "Surface_Laptop_for_Business_7th_Edition_with_Intel_2095",
            "Surface_Laptop_7th_Edition_With_Intel_For_Business_2107",
            "Surface_Laptop_for_Business_7th_Edition_with_Intel_2107",
        ],
        "bios_version": "13.102.143",
    },
    {
        "package_id": "SurfaceLaptopStudio2_Win11_26100",
        "package_name": "SurfaceLaptopStudio2_Win11_26100.msi",
        "version": "",
        "release_date": "2025-01-14",
        "os": "win11",
        "os_build": "26100",
        "os_feature_rank": 242,
        "arch": "x64",
        "download_center_id": "105610",
        "download_page_url": _DOWNLOAD_CENTER_DETAILS_TMPL.format(download_id="105610"),
        "download_url": "",
        "dropin_hint": "<dropin_dir>/microsoft/Surface_Laptop_Studio_2/*.msi",
        "size_bytes": 0,
        "models": ["Surface Laptop Studio 2", "Surface Laptop Studio"],
        "system_skus": ["Surface_Laptop_Studio_2_2029", "Surface_Laptop_Studio_1964"],
        "bios_version": "15.101.143",
    },
    {
        "package_id": "SurfaceGo4_Win11_22621",
        "package_name": "SurfaceGo4_Win11_22621.msi",
        "version": "",
        "release_date": "2024-09-18",
        "os": "win11",
        "os_build": "22621",
        "os_feature_rank": 222,
        "arch": "x64",
        "download_center_id": "105609",
        "download_page_url": _DOWNLOAD_CENTER_DETAILS_TMPL.format(download_id="105609"),
        "download_url": "",
        "dropin_hint": "<dropin_dir>/microsoft/Surface_Go_4/*.msi",
        "size_bytes": 0,
        "models": ["Surface Go 4", "Surface Go 3"],
        "system_skus": ["Surface_Go_4_2067", "Surface_Go_3_1926"],
        "bios_version": "10.102.143",
    },
)


def _normalize_surface_sku(sku: str) -> str:
    return re.sub(r"[^a-z0-9_+:-]+", "_", str(sku or "").strip().lower()).strip("_")


def verify_surface_msi_url(
    url: str,
    timeout: float = 5.0,
    allowed_hosts: Optional[Sequence[str]] = None,
) -> Tuple[bool, int]:
    """
    Verifikuje da je Surface `.msi` URL dozvoljen HTTPS allowlistom i da stvarno postoji
    (HTTP HEAD 200/206 i ne-HTML Content-Type, uz fallback na Range GET pri 405).
    """
    try:
        valid_url = validate_oem_download_url(url, allowed_hosts=allowed_hosts)
    except ValueError:
        return False, 0

    eff_timeout = max(0.5, min(15.0, float(timeout)))
    headers = {"User-Agent": "WIS-ServiceHub/3.7"}

    for method, extra_hdrs in (("HEAD", {}), ("GET", {"Range": "bytes=0-0"})):
        req = urllib.request.Request(valid_url, method=method, headers={**headers, **extra_hdrs})
        try:
            with urllib.request.urlopen(req, timeout=eff_timeout) as resp:
                status = getattr(resp, "status", None) or resp.getcode()
                ctype = str(resp.headers.get("Content-Type") or "").lower()
                if status not in (200, 206) or ctype.startswith("text/html"):
                    return False, 0
                raw_len = resp.headers.get("Content-Length") or "0"
                content_range = str(resp.headers.get("Content-Range") or "")
                size_bytes = 0
                if "/" in content_range:
                    try:
                        size_bytes = int(content_range.rsplit("/", 1)[1].strip())
                    except ValueError:
                        size_bytes = 0
                if size_bytes <= 0:
                    try:
                        size_bytes = int(raw_len)
                    except ValueError:
                        size_bytes = 0
                return True, max(0, size_bytes)
        except urllib.error.HTTPError as exc:
            code = exc.code
            try:
                exc.close()
            except Exception:
                pass
            if method == "HEAD" and code == 405:
                continue
            return False, 0
        except (urllib.error.URLError, OSError, TimeoutError):
            return False, 0
    return False, 0


def parse_microsoft_download_center_html(
    raw_html: bytes,
    spec: Optional[Dict[str, Any]] = None,
    allowed_hosts: Optional[Sequence[str]] = None,
) -> List[Dict[str, Any]]:
    """
    Izvlači stvarne verifikovane `.msi` URL-ove iz HTML/JSON odgovora Microsoft Download Center stranice
    (`details.aspx` / `confirmation.aspx`), uz striktnu `validate_oem_download_url` proveru.
    """
    if not raw_html or not isinstance(raw_html, (bytes, bytearray)):
        return []
    head_upper = bytes(raw_html[:65536]).upper()
    if b"<!ENTITY" in head_upper:
        raise ValueError("HTML odgovor sadrži zabranjenu <!ENTITY deklaraciju.")

    text = bytes(raw_html).decode("utf-8", errors="ignore").replace("\\u002F", "/").replace("\\/", "/")
    raw_urls = _MSI_URL_RE.findall(text)
    entries: List[Dict[str, Any]] = []
    seen_urls = set()

    base_models = list((spec or {}).get("models") or [])
    base_skus = list((spec or {}).get("system_skus") or [])
    base_arch = str((spec or {}).get("arch") or "x64").lower()
    base_page = str((spec or {}).get("download_page_url") or "")
    base_id = str((spec or {}).get("download_center_id") or "")

    for cand_url in raw_urls:
        clean_u = cand_url.strip().rstrip(".,;)")
        if not clean_u or clean_u in seen_urls:
            continue
        try:
            valid_url = validate_oem_download_url(clean_u, allowed_hosts=allowed_hosts)
        except ValueError:
            continue
        seen_urls.add(valid_url)

        pkg_name = os.path.basename(urllib.parse.urlparse(valid_url).path) or "surface_drivers.msi"
        os_build = extract_surface_os_build(pkg_name) or str((spec or {}).get("os_build") or "")
        os_fam, feat_rank = _extract_os_family_and_feature(os_build or pkg_name)
        if os_fam == "other":
            os_fam = "win11"
        arch = "arm64" if ("ARM64" in pkg_name.upper() or base_arch == "arm64") else "x64"
        ver_m = _MSI_VER_RE.search(pkg_name)
        version = ver_m.group(1) if ver_m else ""

        entries.append({
            "vendor": "microsoft",
            "source": "microsoft_download_center",
            "package_id": os.path.splitext(pkg_name)[0],
            "package_name": pkg_name,
            "version": version,
            "release_date": str((spec or {}).get("release_date") or ""),
            "os": os_fam,
            "os_build": os_build,
            "os_feature_rank": feat_rank,
            "os_supported": [os_fam],
            "arch": arch,
            "format": "msi",
            "download_url": valid_url,
            "download_page_url": base_page,
            "download_center_id": base_id,
            "size_bytes": 0,
            "sha256": "",
            "md5": "",
            "models": list(base_models),
            "system_skus": list(base_skus),
            "system_ids": list(base_skus),
            "mtms": [],
        })
    return entries


def discover_surface_msi_entries(
    spec: Dict[str, Any],
    timeout: float = 5.0,
    verify_head: bool = True,
    allowed_hosts: Optional[Sequence[str]] = None,
    fetch_bytes_fn: Optional[Callable[[str, float], bytes]] = None,
    verify_head_fn: Optional[Callable[..., Tuple[bool, int]]] = None,
) -> List[Dict[str, Any]]:
    """
    Dinamički preuzima i verifikuje stvarne `.msi` linkove sa zvanične Microsoft Download Center
    stranice za zadati Surface model, poštujući HTTPS allowlistu i HTTP HEAD verifikaciju.
    Pri mrežnoj/HTTP grešci vraća praznu listu bez izmišljenih URL-ova.
    """
    page_url = str((spec or {}).get("download_page_url") or "").strip()
    if not page_url:
        return []
    try:
        valid_page = validate_oem_download_url(page_url, allowed_hosts=allowed_hosts)
    except ValueError:
        return []

    eff_timeout = max(0.5, min(15.0, float(timeout)))
    try:
        if fetch_bytes_fn is not None:
            raw_html = fetch_bytes_fn(valid_page, eff_timeout)
        else:
            req = urllib.request.Request(
                valid_page,
                headers={"User-Agent": "WIS-ServiceHub/3.7", "Accept": "text/html,application/json"},
            )
            buf = bytearray()
            with urllib.request.urlopen(req, timeout=eff_timeout) as resp:
                while True:
                    chunk = resp.read(65536)
                    if not chunk:
                        break
                    if len(buf) + len(chunk) > MAX_CATALOG_BYTES:
                        raise ValueError("Prekoračen limit veličine za Microsoft Download Center stranicu.")
                    buf.extend(chunk)
            raw_html = bytes(buf)
    except Exception:
        return []

    candidates = parse_microsoft_download_center_html(raw_html, spec=spec, allowed_hosts=allowed_hosts)
    if not verify_head:
        return candidates

    verifier = verify_head_fn or verify_surface_msi_url
    verified: List[Dict[str, Any]] = []
    for entry in candidates:
        ok, sz = verifier(entry["download_url"], timeout=eff_timeout, allowed_hosts=allowed_hosts)
        if not ok:
            continue
        item = dict(entry)
        if sz > 0:
            item["size_bytes"] = sz
        item["head_verified"] = True
        verified.append(item)
    return verified


def parse_microsoft_catalog(
    raw_bytes: bytes,
    allowed_hosts: Optional[Sequence[str]] = None,
) -> List[Dict[str, Any]]:
    """
    Parsira prilagođeni Microsoft Surface JSON, XML ili Download Center HTML katalog uz
    `_check_xml_safe` XXE zaštitu i `validate_oem_download_url` proveru za svaki paket.
    """
    if not raw_bytes or not isinstance(raw_bytes, (bytes, bytearray)):
        raise ValueError("Prazan sadržaj Microsoft Surface kataloga.")

    data = bytes(raw_bytes)
    if data.startswith(b"\xef\xbb\xbf"):
        data = data[3:]
    stripped = data.lstrip()
    entries: List[Dict[str, Any]] = []

    if stripped.startswith(b"<"):
        head_low = stripped[:2048].lower()
        if b"<html" in head_low or b"<!doctype html" in head_low:
            return parse_microsoft_download_center_html(data, allowed_hosts=allowed_hosts)

        clean_bytes = _check_xml_safe(data)
        root = ET.fromstring(clean_bytes)
        for elem in root.iter():
            tag = _strip_ns(elem.tag)
            if tag not in ("Package", "DriverPackage", "SurfacePackage"):
                continue
            fields: Dict[str, str] = {k.lower(): str(v).strip() for k, v in elem.attrib.items()}
            models: List[str] = []
            skus: List[str] = []
            for child in elem:
                ctag = _strip_ns(child.tag).lower()
                if ctag in ("models", "supportedsystems"):
                    for m_el in child:
                        m_val = (m_el.attrib.get("name") or m_el.text or "").strip()
                        if m_val and m_val not in models:
                            models.append(m_val)
                        s_val = (m_el.attrib.get("systemSKU") or m_el.attrib.get("sku") or "").strip()
                        if s_val and s_val not in skus:
                            skus.append(s_val)
                elif ctag in ("systemskus", "skus"):
                    for s_el in child:
                        s_val = (s_el.text or s_el.attrib.get("value") or "").strip()
                        if s_val and s_val not in skus:
                            skus.append(s_val)
                elif ctag == "model" and (child.text or "").strip():
                    models.append((child.text or "").strip())
                elif ctag in ("systemsku", "sku") and (child.text or "").strip():
                    skus.append((child.text or "").strip())
                else:
                    fields[ctag] = (child.text or "").strip()

            dl_url = fields.get("downloadurl") or fields.get("url") or fields.get("download_url") or ""
            if not dl_url:
                continue
            try:
                dl_url = validate_oem_download_url(dl_url, allowed_hosts=allowed_hosts)
            except ValueError:
                continue
            os_raw = fields.get("os") or fields.get("osbuild") or fields.get("os_build") or "win11"
            os_fam, feat_rank = _extract_os_family_and_feature(os_raw)
            if os_fam == "other":
                os_fam = "win11"
            os_build = fields.get("osbuild") or fields.get("os_build") or extract_surface_os_build(os_raw)
            pkg_name = fields.get("packagename") or fields.get("package_name") or os.path.basename(urllib.parse.urlparse(dl_url).path) or "surface_drivers.msi"
            try:
                sz = int(fields.get("sizebytes") or fields.get("size_bytes") or fields.get("size") or 0)
            except (TypeError, ValueError):
                sz = 0
            entries.append({
                "vendor": "microsoft",
                "package_id": fields.get("packageid") or fields.get("package_id") or fields.get("id") or pkg_name,
                "package_name": pkg_name,
                "version": fields.get("version") or "",
                "release_date": _parse_iso_or_us_date(fields.get("releasedate") or fields.get("release_date") or ""),
                "os": os_fam,
                "os_build": os_build,
                "os_feature_rank": feat_rank,
                "os_supported": [os_fam],
                "arch": (fields.get("arch") or "x64").lower(),
                "format": "msi",
                "download_url": dl_url,
                "size_bytes": sz,
                "sha256": (fields.get("sha256") or "").lower(),
                "md5": (fields.get("md5") or "").lower(),
                "models": models,
                "system_skus": skus,
                "system_ids": list(skus),
                "mtms": [],
            })
        return entries

    # JSON format
    parsed_json = json.loads(data.decode("utf-8"))
    raw_items = parsed_json.get("entries", []) if isinstance(parsed_json, dict) else parsed_json
    if not isinstance(raw_items, list):
        raise ValueError("Neispravna struktura Microsoft Surface JSON kataloga.")

    for item in raw_items:
        if not isinstance(item, dict):
            continue
        dl_url = str(item.get("download_url") or item.get("url") or "").strip()
        if not dl_url:
            continue
        try:
            dl_url = validate_oem_download_url(dl_url, allowed_hosts=allowed_hosts)
        except ValueError:
            continue
        os_raw = str(item.get("os_build") or item.get("os") or "win11")
        os_fam, feat_rank = _extract_os_family_and_feature(os_raw)
        if os_fam == "other":
            os_fam = "win11"
        skus = [str(s).strip() for s in (item.get("system_skus") or item.get("system_ids") or []) if str(s).strip()]
        models = [str(m).strip() for m in (item.get("models") or []) if str(m).strip()]
        pkg_name = str(item.get("package_name") or "").strip() or os.path.basename(urllib.parse.urlparse(dl_url).path) or "surface_drivers.msi"
        entries.append({
            "vendor": "microsoft",
            "package_id": str(item.get("package_id") or pkg_name),
            "package_name": pkg_name,
            "version": str(item.get("version") or ""),
            "release_date": _parse_iso_or_us_date(str(item.get("release_date") or "")),
            "os": os_fam,
            "os_build": str(item.get("os_build") or extract_surface_os_build(os_raw)),
            "os_feature_rank": int(item.get("os_feature_rank") or feat_rank),
            "os_supported": [os_fam],
            "arch": str(item.get("arch") or "x64").lower(),
            "format": str(item.get("format") or "msi").lower(),
            "download_url": dl_url,
            "size_bytes": int(item.get("size_bytes") or 0),
            "sha256": str(item.get("sha256") or "").strip().lower(),
            "md5": str(item.get("md5") or "").strip().lower(),
            "models": models,
            "system_skus": skus,
            "system_ids": list(skus),
            "mtms": [],
        })
    return entries


class MicrosoftOEMProvider(BaseOEMProvider):
    """
    OEM provajder za Microsoft Surface uređaje (Drivers & Firmware `.msi` paketi).
    """

    def __init__(
        self,
        cache_dir: Optional[str] = None,
        timeout: float = 5.0,
        max_cache_gb: Optional[float] = None,
        dropin_dir: Optional[str] = None,
    ):
        if cache_dir:
            self.base_cache_dir = os.path.abspath(cache_dir)
        else:
            try:
                from servishub.config import DRIVERS_CACHE_DIR
                self.base_cache_dir = os.path.join(os.path.abspath(DRIVERS_CACHE_DIR), "oem", "microsoft")
            except Exception:
                self.base_cache_dir = os.path.abspath(
                    os.path.join(os.path.dirname(__file__), "..", "..", "..", "storage", "drivers_cache", "oem", "microsoft")
                )

        self.max_drivers_cache_gb = float(max_cache_gb) if max_cache_gb is not None else 25.0
        self.timeout = self._clamp_timeout(timeout)
        self.dropin_dir = (
            os.path.abspath(str(dropin_dir).strip())
            if (dropin_dir is not None and str(dropin_dir).strip())
            else os.path.abspath(os.environ.get("SERVIS_OEM_DROPIN_DIR", "/app/oem_dropin"))
        )
        self.lock = threading.Lock()

        with self.lock:
            os.makedirs(self.base_cache_dir, exist_ok=True)
            self.warranty_cache_dir = os.path.join(self.base_cache_dir, "warranty")
            os.makedirs(self.warranty_cache_dir, exist_ok=True)
            self.keyboard_cache_dir = os.path.join(self.base_cache_dir, "keyboard")
            os.makedirs(self.keyboard_cache_dir, exist_ok=True)

    @classmethod
    def get_builtin_catalog_entries(cls) -> List[Dict[str, Any]]:
        """
        Vraća kanonski indeks Microsoft Surface modela sa zvaničnim Microsoft Download Center
        stranicama (HTTPS allowlist validacija) i Drop-in uputima, bez izmišljenih CDN URL-ova.
        """
        entries: List[Dict[str, Any]] = []
        for spec in SURFACE_CATALOG_SPECS:
            page_url = validate_oem_download_url(spec["download_page_url"])
            raw_dl = str(spec.get("download_url") or "").strip()
            valid_dl = validate_oem_download_url(raw_dl) if raw_dl else ""
            skus = list(spec.get("system_skus") or [])
            entries.append({
                "vendor": "microsoft",
                "source": "microsoft_surface_download_center",
                "package_id": spec["package_id"],
                "package_name": spec["package_name"],
                "version": spec["version"],
                "release_date": spec["release_date"],
                "os": spec["os"],
                "os_build": spec["os_build"],
                "os_feature_rank": spec["os_feature_rank"],
                "os_supported": [spec["os"]],
                "arch": spec["arch"],
                "format": "msi",
                "download_center_id": spec.get("download_center_id", ""),
                "download_page_url": page_url,
                "download_url": valid_dl,
                "dropin_required": not bool(valid_dl),
                "dropin_hint": spec.get("dropin_hint", ""),
                "size_bytes": int(spec.get("size_bytes") or 0),
                "sha256": "",
                "md5": "",
                "models": list(spec.get("models") or []),
                "system_skus": skus,
                "system_ids": list(skus),
                "mtms": [],
                "bios_version": spec.get("bios_version", ""),
            })
        return entries

    @classmethod
    def discover_verified_catalog_entries(
        cls,
        timeout: float = 5.0,
        verify_head: bool = True,
        allowed_hosts: Optional[Sequence[str]] = None,
        fetch_bytes_fn: Optional[Callable[[str, float], bytes]] = None,
        verify_head_fn: Optional[Callable[..., Tuple[bool, int]]] = None,
    ) -> List[Dict[str, Any]]:
        """
        Dinamički preuzima i HTTP HEAD verifikuje `.msi` pakete sa zvaničnih Microsoft Download Center
        stranica iz `SURFACE_CATALOG_SPECS` (deduplikovano po `download_center_id`).
        """
        out: List[Dict[str, Any]] = []
        seen_pages = set()
        for spec in SURFACE_CATALOG_SPECS:
            page_url = str(spec.get("download_page_url") or "").strip()
            if not page_url or page_url in seen_pages:
                continue
            seen_pages.add(page_url)
            found = discover_surface_msi_entries(
                spec,
                timeout=timeout,
                verify_head=verify_head,
                allowed_hosts=allowed_hosts,
                fetch_bytes_fn=fetch_bytes_fn,
                verify_head_fn=verify_head_fn,
            )
            out.extend(found)
        return out

    @staticmethod
    def _normalize_serial(serial: str) -> str:
        if not serial:
            return ""
        return re.sub(r"[^A-Z0-9_-]", "", str(serial).strip().upper())

    @classmethod
    def find_surface_spec(
        cls,
        model: str = "",
        os_ver: str = "win11-x64",
        system_sku: str = "",
        os_build: str = "",
    ) -> Optional[Dict[str, Any]]:
        """
        Pronalazi najbolji Surface `.msi` zapis po `system_sku`, imenu modela i OS buildu.
        """
        clean_model = str(model or "").strip()
        clean_sku = _normalize_surface_sku(system_sku)
        if not clean_model and not clean_sku:
            return None

        req_build = extract_surface_os_build(os_build or os_ver)
        req_os, req_feat = _extract_os_family_and_feature(os_ver or "win11")
        if req_os == "other":
            req_os = "win11"
        req_arm = "ARM64" in f"{os_ver} {system_sku} {model}".upper() or "5G" in f"{system_sku} {model}".upper()

        q_tokens = _norm_model_tokens(clean_model)
        q_set = set(q_tokens)
        best: Optional[Dict[str, Any]] = None
        best_rank: Tuple[int, int, int, int, str] = (-1, -1, -1, -1, "")

        for entry in cls.get_builtin_catalog_entries():
            sku_list = [_normalize_surface_sku(s) for s in (entry.get("system_skus") or [])]
            match_score = 0
            if clean_sku:
                if clean_sku in sku_list:
                    match_score = 100
                elif any(clean_sku in s or s in clean_sku for s in sku_list):
                    match_score = 92

            if match_score == 0 and q_set:
                for cand_m in (entry.get("models") or []):
                    c_tokens = _norm_model_tokens(cand_m)
                    c_set = set(c_tokens)
                    if not c_set:
                        continue
                    q_nums = {t for t in q_tokens if any(ch.isdigit() for ch in t)}
                    c_nums = {t for t in c_tokens if any(ch.isdigit() for ch in t)}
                    if q_nums and c_nums and q_nums != c_nums:
                        continue
                    if q_set == c_set:
                        match_score = max(match_score, 95)
                    elif q_set <= c_set:
                        match_score = max(match_score, max(60, 88 - 5 * len(c_set - q_set)))
                    elif c_set <= q_set:
                        match_score = max(match_score, max(55, 84 - 5 * len(q_set - c_set)))

            if match_score <= 0:
                continue

            arch_score = 1
            if req_arm and entry.get("arch") == "arm64":
                arch_score = 2
            elif not req_arm and entry.get("arch") == "x64":
                arch_score = 2
            elif req_arm and entry.get("arch") != "arm64":
                arch_score = 0

            entry_os = entry.get("os") or "win11"
            os_score = 2 if entry_os == req_os else 1
            build_score = 2 if (req_build and entry.get("os_build") == req_build) else 0
            feat_rank = int(entry.get("os_feature_rank") or 0)
            if req_feat > 100 and feat_rank == req_feat:
                build_score = max(build_score, 1)

            rank = (match_score, arch_score, build_score, os_score, str(entry.get("release_date") or ""))
            if rank > best_rank:
                best_rank = rank
                best = dict(entry)
                best["match_score"] = match_score

        return best

    def get_warranty_info(self, serial: str, model: str = "") -> Dict[str, Any]:
        """Vraća keširanu garanciju ili graceful fallback (Microsoft zahteva MSA/Partner API)."""
        clean_sn = self._normalize_serial(serial)
        if not clean_sn or len(clean_sn) < 6:
            return {
                "status": "error",
                "in_warranty": False,
                "status_label": "Neispravan serijski broj",
                "service_level": "N/A",
                "start_date": "",
                "end_date": "",
                "days_remaining": 0,
                "raw_data": {"error": "Serijski broj je prazan ili prekratak", "serial": serial},
            }

        cache_file = os.path.join(self.warranty_cache_dir, f"{clean_sn}.json")
        with self.lock:
            cached = self._read_json_file(cache_file)
            if cached:
                return cached

        return {
            "status": "fallback",
            "in_warranty": False,
            "status_label": "Nepoznato (Microsoft Surface API nije javan)",
            "service_level": "N/A",
            "start_date": "",
            "end_date": "",
            "days_remaining": 0,
            "raw_data": {
                "serial": clean_sn,
                "model": model,
                "note": "Microsoft Surface garancija zahteva autentifikovani portal",
            },
        }

    def resolve_driver_pack(
        self,
        model: str,
        os_ver: str = "win11-x64",
        system_sku: str = "",
        os_build: str = "",
        allow_dynamic_fetch: bool = False,
    ) -> Dict[str, Any]:
        """
        Pronalazi zvanični Surface `.msi` paket drajvera i firmvera:
        1. Lokalni disk keš (`base_cache_dir/<key>/*.msi`)
        2. Lokalni Drop-in paket (`DropInOEMProvider` u `<dropin_dir>/microsoft/<model>/`)
        3. Opciono dinamički fetch + HTTP HEAD verifikacija sa zvanične Microsoft Download Center stranice
        4. Čist Drop-in fallback bez izmišljenih CDN URL-ova.
        """
        clean_key = _normalize_surface_sku(system_sku or model)
        if not clean_key:
            return {
                "status": "not_found",
                "source": "microsoft_surface",
                "package_name": "",
                "download_url": "",
                "local_path": "",
                "size_bytes": 0,
            }

        model_dir = os.path.join(self.base_cache_dir, clean_key)
        with self.lock:
            if os.path.isdir(model_dir):
                archives = [
                    f for f in sorted(os.listdir(model_dir))
                    if f.lower().endswith((".msi", ".zip", ".cab")) and not f.endswith(".tmp")
                ]
                if archives:
                    chosen = archives[0]
                    local_file = os.path.join(model_dir, chosen)
                    if not os.path.islink(local_file) and os.path.isfile(local_file):
                        f_size = os.path.getsize(local_file)
                        if f_size > 0:
                            meta = self._read_json_file(os.path.join(model_dir, "package_meta.json")) or {}
                            return {
                                "status": "ok",
                                "source": "cache",
                                "package_name": chosen,
                                "download_url": meta.get("download_url", ""),
                                "local_path": local_file,
                                "size_bytes": f_size,
                            }

        # 2. Čist DropInOEMProvider fallback za lokalno postavljene zvanične Surface .msi pakete
        from servishub.drivers.oem.dropin import DropInOEMProvider
        dropin_prov = DropInOEMProvider(vendor="microsoft", dropin_dir=self.dropin_dir)
        dropin_hits = []
        if system_sku:
            dropin_hits = dropin_prov.scan_packages(vendor="microsoft", model=system_sku, os_ver=os_ver)
        if not dropin_hits and model:
            dropin_hits = dropin_prov.scan_packages(vendor="microsoft", model=model, os_ver=os_ver)
        if dropin_hits:
            best_di = dropin_hits[0]
            return {
                "status": "ok",
                "source": "dropin",
                "package_name": best_di["package_name"],
                "download_url": "",
                "local_path": best_di["local_path"],
                "size_bytes": int(best_di.get("size_bytes") or 0),
            }

        spec = self.find_surface_spec(model=model, os_ver=os_ver, system_sku=system_sku, os_build=os_build)
        if not spec:
            return {
                "status": "not_found",
                "source": "microsoft_surface",
                "package_name": "",
                "download_url": "",
                "local_path": "",
                "size_bytes": 0,
            }

        resolved_dl_url = str(spec.get("download_url") or "").strip()
        resolved_pkg_name = str(spec.get("package_name") or "")
        resolved_size = int(spec.get("size_bytes") or 0)
        resolved_source = "microsoft_surface_download_center"

        if not resolved_dl_url and allow_dynamic_fetch:
            discovered = discover_surface_msi_entries(spec, timeout=self.timeout, verify_head=True)
            if discovered:
                req_build = extract_surface_os_build(os_build or os_ver)
                discovered.sort(
                    key=lambda e: (
                        1 if (req_build and e.get("os_build") == req_build) else 0,
                        1 if e.get("arch") == spec.get("arch") else 0,
                        int(e.get("os_feature_rank") or 0),
                        str(e.get("version") or ""),
                    ),
                    reverse=True,
                )
                chosen_dyn = discovered[0]
                resolved_dl_url = chosen_dyn["download_url"]
                resolved_pkg_name = chosen_dyn["package_name"]
                resolved_size = int(chosen_dyn.get("size_bytes") or 0)

        if not resolved_dl_url:
            return {
                "status": "not_found",
                "source": "microsoft_surface_dropin_required",
                "package_name": resolved_pkg_name,
                "download_url": "",
                "download_page_url": spec.get("download_page_url", ""),
                "download_center_id": spec.get("download_center_id", ""),
                "dropin_required": True,
                "dropin_hint": spec.get("dropin_hint", ""),
                "local_path": "",
                "size_bytes": 0,
            }

        with self.lock:
            self._atomic_write_json(
                os.path.join(model_dir, "package_meta.json"),
                {
                    "model": model,
                    "system_sku": system_sku,
                    "os_ver": os_ver,
                    "package_name": resolved_pkg_name,
                    "download_url": resolved_dl_url,
                    "download_page_url": spec.get("download_page_url", ""),
                    "size_bytes": resolved_size,
                    "arch": spec["arch"],
                    "os_build": spec["os_build"],
                    "source": resolved_source,
                    "resolved_at": datetime.now().isoformat(),
                },
            )

        return {
            "status": "ok",
            "source": resolved_source,
            "package_name": resolved_pkg_name,
            "download_url": resolved_dl_url,
            "download_page_url": spec.get("download_page_url", ""),
            "local_path": "",
            "size_bytes": resolved_size,
        }

    def check_bios_update(self, model: str, current_bios: str = "") -> Dict[str, Any]:
        """Proverava verziju UEFI firmvera iz Surface indeksa."""
        spec = self.find_surface_spec(model=model)
        if not spec:
            return {
                "update_available": False,
                "latest_version": "",
                "current_version": current_bios,
                "download_url": "",
                "release_date": "",
            }
        latest = str(spec.get("bios_version") or "")
        update_avail = False
        if latest:
            if not current_bios or current_bios.strip().upper() in ("UNKNOWN", "N/A", ""):
                update_avail = True
            else:
                c_parts = [int(x) for x in re.findall(r"\d+", current_bios)]
                l_parts = [int(x) for x in re.findall(r"\d+", latest)]
                max_len = max(len(c_parts), len(l_parts), 1)
                update_avail = (l_parts + [0] * (max_len - len(l_parts))) > (c_parts + [0] * (max_len - len(c_parts)))
        return {
            "update_available": update_avail,
            "latest_version": latest,
            "current_version": current_bios,
            "download_url": spec.get("download_url") or spec.get("download_page_url") or "",
            "release_date": spec.get("release_date", ""),
        }

    def lookup_keyboard_spec(
        self,
        serial: str = "",
        model: str = "",
        product_number: str = "",
    ) -> Dict[str, Any]:
        """Surface laptopovi i Type Cover tastature su TKL (bez numeričkog bloka)."""
        return build_keyboard_spec(
            status="ok",
            source="microsoft_surface_series",
            confidence="medium",
            base_layout=make_keyboard_layout_name(has_numpad=False, is_iso=True),
            has_numpad=False,
            is_iso=True,
            has_copilot=True,
            raw_data={"serial": serial, "model": model, "product_number": product_number},
        )
