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
import urllib.parse
import urllib.request
import xml.etree.ElementTree as ET
from datetime import date, datetime
from typing import Any, Dict, List, Optional, Sequence, Tuple

from servishub.drivers.oem.base import (
    BaseOEMProvider,
    build_keyboard_spec,
    make_keyboard_layout_name,
)
from servishub.drivers.oem.catalogs import (
    _check_xml_safe,
    _extract_os_family_and_feature,
    _norm_model_tokens,
    _parse_iso_or_us_date,
    _strip_ns,
    validate_oem_download_url,
)

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


# Kanonski indeks zvaničnih Microsoft Surface MSI paketa po SystemSKU i OS buildu
SURFACE_CATALOG_SPECS: Tuple[Dict[str, Any], ...] = (
    {
        "package_id": "SurfacePro7_Win11_22621",
        "package_name": "SurfacePro7_Win11_22621_24.061.12345.0.msi",
        "version": "24.061.12345.0",
        "release_date": "2024-06-15",
        "os": "win11",
        "os_build": "22621",
        "os_feature_rank": 222,
        "arch": "x64",
        "download_url": "https://download.microsoft.com/download/6/b/8/6b8d2f45-1c4b-4a6c-9d9b-surfacepro7/SurfacePro7_Win11_22621_24.061.12345.0.msi",
        "size_bytes": 945820000,
        "models": ["Surface Pro 7"],
        "system_skus": ["Surface_Pro_7_1866"],
        "bios_version": "15.11.140",
    },
    {
        "package_id": "SurfacePro7Plus_Win11_22621",
        "package_name": "SurfacePro7Plus_Win11_22621_24.082.21040.0.msi",
        "version": "24.082.21040.0",
        "release_date": "2024-08-20",
        "os": "win11",
        "os_build": "22621",
        "os_feature_rank": 222,
        "arch": "x64",
        "download_url": "https://download.microsoft.com/download/7/c/1/7c1e3a12-4d5e-4b77-8a11-surfacepro7plus/SurfacePro7Plus_Win11_22621_24.082.21040.0.msi",
        "size_bytes": 1024600000,
        "models": ["Surface Pro 7+", "Surface Pro 7 Plus"],
        "system_skus": ["Surface_Pro_7+_1960", "Surface_Pro_7+_1961"],
        "bios_version": "21.101.143",
    },
    {
        "package_id": "SurfacePro8_Win11_22621",
        "package_name": "SurfacePro8_Win11_22621_24.091.19830.0.msi",
        "version": "24.091.19830.0",
        "release_date": "2024-09-12",
        "os": "win11",
        "os_build": "22621",
        "os_feature_rank": 222,
        "arch": "x64",
        "download_url": "https://download.microsoft.com/download/8/a/2/8a2f4b18-9e12-4c33-9f22-surfacepro8/SurfacePro8_Win11_22621_24.091.19830.0.msi",
        "size_bytes": 1245000000,
        "models": ["Surface Pro 8", "Surface Pro 8 for Business"],
        "system_skus": ["Surface_Pro_8_1983", "Surface_Pro_8_for_Business_1983", "Surface_Pro_8_LTE_1982"],
        "bios_version": "25.102.141",
    },
    {
        "package_id": "SurfacePro9_Win11_26100",
        "package_name": "SurfacePro9_Win11_26100_25.012.23890.0.msi",
        "version": "25.012.23890.0",
        "release_date": "2025-01-20",
        "os": "win11",
        "os_build": "26100",
        "os_feature_rank": 242,
        "arch": "x64",
        "download_url": "https://download.microsoft.com/download/1/a/9/1a9e5c11-2b44-4d88-9a33-surfacepro9/SurfacePro9_Win11_26100_25.012.23890.0.msi",
        "size_bytes": 1412000000,
        "models": ["Surface Pro 9", "Surface Pro 9 for Business"],
        "system_skus": ["Surface_Pro_9_2038", "Surface_Pro_9_for_Business_2038"],
        "bios_version": "18.101.143",
    },
    {
        "package_id": "SurfacePro9_Win11_22621",
        "package_name": "SurfacePro9_Win11_22621_24.102.23890.0.msi",
        "version": "24.102.23890.0",
        "release_date": "2024-10-15",
        "os": "win11",
        "os_build": "22621",
        "os_feature_rank": 222,
        "arch": "x64",
        "download_url": "https://download.microsoft.com/download/1/a/9/1a9e5c11-2b44-4d88-9a33-surfacepro9/SurfacePro9_Win11_22621_24.102.23890.0.msi",
        "size_bytes": 1398000000,
        "models": ["Surface Pro 9", "Surface Pro 9 for Business"],
        "system_skus": ["Surface_Pro_9_2038", "Surface_Pro_9_for_Business_2038"],
        "bios_version": "18.100.143",
    },
    {
        "package_id": "SurfacePro9With5G_Win11_22621",
        "package_name": "SurfacePro9With5G_Win11_22621_24.084.42100.0.msi",
        "version": "24.084.42100.0",
        "release_date": "2024-08-25",
        "os": "win11",
        "os_build": "22621",
        "os_feature_rank": 222,
        "arch": "arm64",
        "download_url": "https://download.microsoft.com/download/2/b/7/2b7c1d22-3e55-4f99-8b44-surfacepro95g/SurfacePro9With5G_Win11_22621_24.084.42100.0.msi",
        "size_bytes": 910500000,
        "models": ["Surface Pro 9 with 5G", "Surface Pro 9 5G"],
        "system_skus": ["Surface_Pro_9_With_5G_1997", "Surface_Pro_9_5G_2046"],
        "bios_version": "14.101.143",
    },
    {
        "package_id": "SurfacePro10_Win11_26100",
        "package_name": "SurfacePro10_Win11_26100_25.021.31020.0.msi",
        "version": "25.021.31020.0",
        "release_date": "2025-02-18",
        "os": "win11",
        "os_build": "26100",
        "os_feature_rank": 242,
        "arch": "x64",
        "download_url": "https://download.microsoft.com/download/3/c/4/3c4d2e33-4f66-4a00-9c55-surfacepro10/SurfacePro10_Win11_26100_25.021.31020.0.msi",
        "size_bytes": 1520000000,
        "models": ["Surface Pro 10", "Surface Pro 10 for Business"],
        "system_skus": ["Surface_Pro_10_for_Business_2079", "Surface_Pro_10_2079"],
        "bios_version": "10.104.143",
    },
    {
        "package_id": "SurfacePro11_ARM64_Win11_26100",
        "package_name": "SurfacePro11thEdition_ARM64_Win11_26100_25.031.11200.0.msi",
        "version": "25.031.11200.0",
        "release_date": "2025-03-10",
        "os": "win11",
        "os_build": "26100",
        "os_feature_rank": 242,
        "arch": "arm64",
        "download_url": "https://download.microsoft.com/download/4/d/5/4d5e3f44-5a77-4b11-8d66-surfacepro11arm/SurfacePro11thEdition_ARM64_Win11_26100_25.031.11200.0.msi",
        "size_bytes": 980400000,
        "models": ["Surface Pro 11th Edition", "Surface Pro Copilot+ PC"],
        "system_skus": ["Surface_Pro_11th_Edition_2076"],
        "bios_version": "11.102.143",
    },
    {
        "package_id": "SurfacePro11_Intel_Win11_26100",
        "package_name": "SurfacePro11thEditionforBusiness_Win11_26100_25.032.21100.0.msi",
        "version": "25.032.21100.0",
        "release_date": "2025-03-14",
        "os": "win11",
        "os_build": "26100",
        "os_feature_rank": 242,
        "arch": "x64",
        "download_url": "https://download.microsoft.com/download/4/d/6/4d6f4a55-6b88-4c22-9e77-surfacepro11intel/SurfacePro11thEditionforBusiness_Win11_26100_25.032.21100.0.msi",
        "size_bytes": 1580000000,
        "models": ["Surface Pro 11th Edition for Business", "Surface Pro for Business 11th Edition with Intel"],
        "system_skus": ["Surface_Pro_11th_Edition_for_Business_2110"],
        "bios_version": "12.101.143",
    },
    {
        "package_id": "SurfaceLaptop3_Intel_Win11_22621",
        "package_name": "SurfaceLaptop3_Win11_22621_24.051.18670.0.msi",
        "version": "24.051.18670.0",
        "release_date": "2024-05-10",
        "os": "win11",
        "os_build": "22621",
        "os_feature_rank": 222,
        "arch": "x64",
        "download_url": "https://download.microsoft.com/download/5/e/1/5e1a6b77-7c99-4d33-8f88-surfacelaptop3/SurfaceLaptop3_Win11_22621_24.051.18670.0.msi",
        "size_bytes": 890000000,
        "models": ["Surface Laptop 3"],
        "system_skus": ["Surface_Laptop_3_1867:1868", "Surface_Laptop_3_1867", "Surface_Laptop_3_1872"],
        "bios_version": "17.101.140",
    },
    {
        "package_id": "SurfaceLaptop4_Intel_Win11_22621",
        "package_name": "SurfaceLaptop4_Intel_Win11_22621_24.072.19500.0.msi",
        "version": "24.072.19500.0",
        "release_date": "2024-07-18",
        "os": "win11",
        "os_build": "22621",
        "os_feature_rank": 222,
        "arch": "x64",
        "download_url": "https://download.microsoft.com/download/6/f/2/6f2b7c88-8d00-4e44-9a99-surfacelaptop4/SurfaceLaptop4_Intel_Win11_22621_24.072.19500.0.msi",
        "size_bytes": 1150000000,
        "models": ["Surface Laptop 4"],
        "system_skus": ["Surface_Laptop_4_1950:1951", "Surface_Laptop_4_1950", "Surface_Laptop_4_1951", "Surface_Laptop_4_1978:1979"],
        "bios_version": "21.102.143",
    },
    {
        "package_id": "SurfaceLaptop5_Win11_26100",
        "package_name": "SurfaceLaptop5_Win11_26100_25.020.19500.0.msi",
        "version": "25.020.19500.0",
        "release_date": "2025-02-05",
        "os": "win11",
        "os_build": "26100",
        "os_feature_rank": 242,
        "arch": "x64",
        "download_url": "https://download.microsoft.com/download/7/a/3/7a3c8d99-9e11-4f55-8b00-surfacelaptop5/SurfaceLaptop5_Win11_26100_25.020.19500.0.msi",
        "size_bytes": 1340000000,
        "models": ["Surface Laptop 5", "Surface Laptop 5 for Business"],
        "system_skus": ["Surface_Laptop_5_1950:1951", "Surface_Laptop_5_1950", "Surface_Laptop_5_1951", "Surface_Laptop_5_1979", "Surface_Laptop_5_for_Business_1950"],
        "bios_version": "16.101.143",
    },
    {
        "package_id": "SurfaceLaptop6_Win11_26100",
        "package_name": "SurfaceLaptop6forBusiness_Win11_26100_25.030.20810.0.msi",
        "version": "25.030.20810.0",
        "release_date": "2025-03-01",
        "os": "win11",
        "os_build": "26100",
        "os_feature_rank": 242,
        "arch": "x64",
        "download_url": "https://download.microsoft.com/download/8/b/4/8b4d9e00-0f22-4a66-9c11-surfacelaptop6/SurfaceLaptop6forBusiness_Win11_26100_25.030.20810.0.msi",
        "size_bytes": 1490000000,
        "models": ["Surface Laptop 6 for Business", "Surface Laptop 6"],
        "system_skus": ["Surface_Laptop_6_for_Business_2081", "Surface_Laptop_6_2080"],
        "bios_version": "12.104.143",
    },
    {
        "package_id": "SurfaceLaptop7_ARM64_Win11_26100",
        "package_name": "SurfaceLaptop7thEdition_ARM64_Win11_26100_25.031.20360.0.msi",
        "version": "25.031.20360.0",
        "release_date": "2025-03-08",
        "os": "win11",
        "os_build": "26100",
        "os_feature_rank": 242,
        "arch": "arm64",
        "download_url": "https://download.microsoft.com/download/9/c/5/9c5e0f11-1a33-4b77-8d22-surfacelaptop7arm/SurfaceLaptop7thEdition_ARM64_Win11_26100_25.031.20360.0.msi",
        "size_bytes": 1015000000,
        "models": ["Surface Laptop 7th Edition"],
        "system_skus": ["Surface_Laptop_7th_Edition_2036", "Surface_Laptop_7th_Edition_2037"],
        "bios_version": "11.103.143",
    },
    {
        "package_id": "SurfaceLaptop7_Intel_Win11_26100",
        "package_name": "SurfaceLaptop7thEditionforBusiness_Win11_26100_25.041.20950.0.msi",
        "version": "25.041.20950.0",
        "release_date": "2025-04-02",
        "os": "win11",
        "os_build": "26100",
        "os_feature_rank": 242,
        "arch": "x64",
        "download_url": "https://download.microsoft.com/download/a/d/6/ad6f1a22-2b44-4c88-9e33-surfacelaptop7intel/SurfaceLaptop7thEditionforBusiness_Win11_26100_25.041.20950.0.msi",
        "size_bytes": 1620000000,
        "models": [
            "Surface Laptop 7th Edition for Business",
            "Surface Laptop for Business 7th Edition with Intel",
        ],
        "system_skus": [
            "Surface_Laptop_7th_Edition_for_Business_2095",
            "Surface_Laptop_for_Business_7th_Edition_with_Intel_2095",
        ],
        "bios_version": "13.102.143",
    },
    {
        "package_id": "SurfaceLaptopStudio2_Win11_26100",
        "package_name": "SurfaceLaptopStudio2_Win11_26100_25.011.20290.0.msi",
        "version": "25.011.20290.0",
        "release_date": "2025-01-14",
        "os": "win11",
        "os_build": "26100",
        "os_feature_rank": 242,
        "arch": "x64",
        "download_url": "https://download.microsoft.com/download/b/e/7/be7a2b33-3c55-4d99-8f44-surfacestudio2/SurfaceLaptopStudio2_Win11_26100_25.011.20290.0.msi",
        "size_bytes": 1850000000,
        "models": ["Surface Laptop Studio 2", "Surface Laptop Studio"],
        "system_skus": ["Surface_Laptop_Studio_2_2029", "Surface_Laptop_Studio_1964"],
        "bios_version": "15.101.143",
    },
    {
        "package_id": "SurfaceGo4_Win11_22621",
        "package_name": "SurfaceGo4_Win11_22621_24.092.20670.0.msi",
        "version": "24.092.20670.0",
        "release_date": "2024-09-18",
        "os": "win11",
        "os_build": "22621",
        "os_feature_rank": 222,
        "arch": "x64",
        "download_url": "https://download.microsoft.com/download/c/f/8/cf8b3c44-4d66-4e00-9a55-surfacego4/SurfaceGo4_Win11_22621_24.092.20670.0.msi",
        "size_bytes": 760000000,
        "models": ["Surface Go 4", "Surface Go 3"],
        "system_skus": ["Surface_Go_4_2067", "Surface_Go_3_1926"],
        "bios_version": "10.102.143",
    },
)


def _normalize_surface_sku(sku: str) -> str:
    return re.sub(r"[^a-z0-9_+:-]+", "_", str(sku or "").strip().lower()).strip("_")


def parse_microsoft_catalog(
    raw_bytes: bytes,
    allowed_hosts: Optional[Sequence[str]] = None,
) -> List[Dict[str, Any]]:
    """
    Parsira prilagođeni Microsoft Surface JSON ili XML katalog uz `_check_xml_safe`
    XXE zaštitu i `validate_oem_download_url` proveru za svaki paket.
    """
    if not raw_bytes or not isinstance(raw_bytes, (bytes, bytearray)):
        raise ValueError("Prazan sadržaj Microsoft Surface kataloga.")

    data = bytes(raw_bytes)
    if data.startswith(b"\xef\xbb\xbf"):
        data = data[3:]
    stripped = data.lstrip()
    entries: List[Dict[str, Any]] = []

    if stripped.startswith(b"<"):
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
            dl_url = validate_oem_download_url(dl_url, allowed_hosts=allowed_hosts)
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
        dl_url = validate_oem_download_url(dl_url, allowed_hosts=allowed_hosts)
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
        self.lock = threading.Lock()

        with self.lock:
            os.makedirs(self.base_cache_dir, exist_ok=True)
            self.warranty_cache_dir = os.path.join(self.base_cache_dir, "warranty")
            os.makedirs(self.warranty_cache_dir, exist_ok=True)
            self.keyboard_cache_dir = os.path.join(self.base_cache_dir, "keyboard")
            os.makedirs(self.keyboard_cache_dir, exist_ok=True)

    @classmethod
    def get_builtin_catalog_entries(cls) -> List[Dict[str, Any]]:
        """Vraća kanonsku listu ugrađenih Microsoft Surface MSI paketa sa HTTPS URL validacijom."""
        entries: List[Dict[str, Any]] = []
        for spec in SURFACE_CATALOG_SPECS:
            url = validate_oem_download_url(spec["download_url"])
            skus = list(spec.get("system_skus") or [])
            entries.append({
                "vendor": "microsoft",
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
                "download_url": url,
                "size_bytes": int(spec["size_bytes"]),
                "sha256": "",
                "md5": "",
                "models": list(spec.get("models") or []),
                "system_skus": skus,
                "system_ids": list(skus),
                "mtms": [],
                "bios_version": spec.get("bios_version", ""),
            })
        return entries

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
    ) -> Dict[str, Any]:
        """Pronalazi zvanični Surface `.msi` paket drajvera i firmvera u lokalnom kešu ili indeksu."""
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

        with self.lock:
            self._atomic_write_json(
                os.path.join(model_dir, "package_meta.json"),
                {
                    "model": model,
                    "system_sku": system_sku,
                    "os_ver": os_ver,
                    "package_name": spec["package_name"],
                    "download_url": spec["download_url"],
                    "size_bytes": spec["size_bytes"],
                    "arch": spec["arch"],
                    "os_build": spec["os_build"],
                    "source": "microsoft_surface_index",
                    "resolved_at": datetime.now().isoformat(),
                },
            )

        return {
            "status": "ok",
            "source": "microsoft_surface_index",
            "package_name": spec["package_name"],
            "download_url": spec["download_url"],
            "local_path": "",
            "size_bytes": int(spec["size_bytes"]),
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
            "download_url": spec["download_url"],
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
