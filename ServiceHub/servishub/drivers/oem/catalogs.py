"""
servishub.drivers.oem.catalogs
==============================
Sinhronizacija, lokalno keširanje i pretraga zvaničnih OEM Enterprise kataloga
(Dell, HP, Lenovo, Microsoft Surface, Fujitsu DeskUpdate) i Tier 2 Hybrid Drop-in
paketa (ASUS, Acer, MSI, Dynabook) sa strogom HTTPS host validacijom, XXE zaštitom
i verifikacijom kontrolnih suma.
"""

from __future__ import annotations

import hashlib
import json
import os
import re
import shutil
import subprocess
import tempfile
import threading
import time
import urllib.error
import urllib.parse
import urllib.request
import xml.etree.ElementTree as ET
from datetime import datetime
from typing import Any, Dict, List, Optional, Sequence, Tuple


TIER1_OEM_VENDORS: Tuple[str, ...] = ("dell", "hp", "lenovo", "microsoft", "fujitsu")
TIER2_DROPIN_VENDORS: Tuple[str, ...] = ("asus", "acer", "msi", "dynabook")

CATALOG_URLS: Dict[str, str] = {
    "dell": "https://downloads.dell.com/catalog/DriverPackCatalog.cab",
    "hp": "https://hpia.hpcloud.hp.com/downloads/driverpackcatalog/HPClientDriverPackCatalog.cab",
    "lenovo": "https://download.lenovo.com/cdrt/td/catalogv2.xml",
    "fujitsu": "https://support.ts.fujitsu.com/DeskUpdate/Catalog/DeskUpdateCatalog.xml",
}

ALLOWED_OEM_HOST_SUFFIXES: Tuple[str, ...] = (
    "downloads.dell.com",
    "dl.dell.com",
    "ftp.hp.com",
    "hpia.hpcloud.hp.com",
    "support.hp.com",
    "download.lenovo.com",
    "download.lenovo.com.edgesuite.net",
    "download.microsoft.com",
    ".microsoft.com",
    "support.ts.fujitsu.com",
    ".dell.com",
    ".hp.com",
    ".hpcloud.hp.com",
    ".lenovo.com",
)

DEFAULT_CATALOG_TTL_SEC = 86400  # 24 sata
MAX_CATALOG_BYTES = 128 * 1024 * 1024  # 128 MiB limit za XML/CAB katalog
MAX_OEM_PACKAGE_BYTES = 6 * 1024 * 1024 * 1024  # 6 GiB limit za jedan OEM paket

_FEATURE_ORDER = {
    "26H2": 262,
    "25H2": 252,
    "24H2": 242,
    "23H2": 232,
    "22H2": 222,
    "21H2": 212,
    "20H2": 202,
    "*": 100,
    "": 50,
}

_WIN11_BUILD_RANKS = {
    "26200": ("win11", 252),
    "26100": ("win11", 242),
    "22631": ("win11", 232),
    "22621": ("win11", 222),
    "22000": ("win11", 212),
}

_WIN10_BUILD_RANKS = {
    "19045": ("win10", 222),
    "19044": ("win10", 212),
    "19043": ("win10", 211),
    "19042": ("win10", 202),
    "19041": ("win10", 201),
}


def detect_oem_vendor(manufacturer: str = "", model: str = "") -> str:
    """
    Normalizuje naziv OEM proizvođača u Tier 1 ('dell', 'hp', 'lenovo', 'microsoft', 'fujitsu'),
    Tier 2 ('asus', 'acer', 'msi', 'dynabook') ili ''.
    """
    combined = f"{manufacturer or ''} {model or ''}".strip().upper()
    if not combined:
        return ""

    # 1. Eksplicitni nazivi proizvođača / brendova (sprečava kolizije serija poput "ASUS ... Creator")
    if "LENOVO" in combined:
        return "lenovo"
    if "DELL" in combined:
        return "dell"
    if "HEWLETT" in combined or combined == "HP" or re.search(r"(?<![A-Z0-9])HP(?![A-Z0-9])", combined):
        return "hp"
    # MSI mora biti proveren odvojeno od MICROSOFT ('MICRO-STAR' != 'MICROSOFT', word-boundary MSI)
    if "MICRO-STAR" in combined or "MICRO STAR" in combined or re.search(r"(?<![A-Z0-9])MSI(?![A-Z0-9])", combined):
        return "msi"
    # Microsoft Surface zahteva SURFACE (kako Hyper-V Virtual Machine sa Microsoft Corporation ne bi bio Surface)
    if re.search(r"(?<![A-Z0-9])SURFACE(?![A-Z0-9])", combined):
        return "microsoft"
    if re.search(r"(?<![A-Z0-9])FUJITSU(?![A-Z0-9])", combined):
        return "fujitsu"
    if re.search(r"(?<![A-Z0-9])(ASUS|ASUSTEK)(?![A-Z0-9])", combined):
        return "asus"
    if re.search(r"(?<![A-Z0-9])ACER(?![A-Z0-9])", combined):
        return "acer"
    if re.search(r"(?<![A-Z0-9])(DYNABOOK|TOSHIBA)(?![A-Z0-9])", combined):
        return "dynabook"

    # 2. Prepoznavanje po serijama modela kada Manufacturer nije popunjen (IMP-108)
    if any(k in combined for k in ("THINKPAD", "THINKCENTRE", "IDEAPAD", "THINKBOOK", "YOGA", "LEGION")):
        return "lenovo"
    if any(k in combined for k in ("LATITUDE", "OPTIPLEX", "PRECISION", "XPS", "VOSTRO", "INSPIRON")):
        return "dell"
    if re.search(
        r"(?<![A-Z0-9])(ELITEBOOK|PROBOOK|ZBOOK|ELITEDESK|PRODESK|PAVILION|ENVY|OMEN|VICTUS|OMNIBOOK)(?![A-Z0-9])",
        combined,
    ):
        return "hp"
    if re.search(r"(?<![A-Z0-9])(LIFEBOOK|ESPRIMO|CELSIUS)(?![A-Z0-9])", combined):
        return "fujitsu"
    if re.search(
        r"(?<![A-Z0-9])(ZENBOOK|VIVOBOOK|PROART|EXPERTBOOK|TUF|ROG)(?![A-Z0-9])",
        combined,
    ):
        return "asus"
    if re.search(
        r"(?<![A-Z0-9])(TRAVELMATE|ASPIRE|NITRO|SWIFT|EXTENSA|VERITON|PREDATOR)(?![A-Z0-9])",
        combined,
    ):
        return "acer"
    if re.search(
        r"(?<![A-Z0-9])(PRESTIGE|MODERN|KATANA|STEALTH|CREATOR)(?![A-Z0-9])",
        combined,
    ):
        return "msi"
    if re.search(r"(?<![A-Z0-9])(TECRA|PORTEGE|PORTÉGÉ)(?![A-Z0-9])", combined):
        return "dynabook"
    return ""


def validate_oem_download_url(
    url: str,
    allowed_hosts: Optional[Sequence[str]] = None,
) -> str:
    """
    Validira URL za preuzimanje OEM kataloga ili driver paketa.
    U podrazumevanom režimu zahteva HTTPS i zvanični HP/Dell/Lenovo/Microsoft/Fujitsu domen.
    Opcioni `allowed_hosts` omogućava eksplicitnu listu hostova za testove.
    """
    raw = str(url or "").strip()
    if not raw:
        raise ValueError("OEM URL je prazan.")
    parsed = urllib.parse.urlparse(raw)
    scheme = (parsed.scheme or "").lower()
    host = (parsed.hostname or "").lower()
    if not host:
        raise ValueError(f"OEM URL nema validan hostname: {raw}")

    if allowed_hosts is not None:
        normalized = [str(h).strip().lower() for h in allowed_hosts if str(h).strip()]
        if scheme not in ("https", "http"):
            raise ValueError(f"Nedozvoljena URL šema za test allowlist: {scheme}")
        if not any(host == h or (h.startswith(".") and host.endswith(h)) for h in normalized):
            raise ValueError(f"OEM host '{host}' nije na dozvoljenoj listi: {normalized}")
        return raw

    if scheme != "https":
        raise ValueError(f"OEM URL mora koristiti HTTPS protokol: {raw}")
    for allowed in ALLOWED_OEM_HOST_SUFFIXES:
        if allowed.startswith("."):
            if host.endswith(allowed):
                return raw
        elif host == allowed:
            return raw
    raise ValueError(f"OEM hostname '{host}' nije na zvaničnoj OEM allowlist-i.")


MAX_OEM_REDIRECTS = 5


class _SafeOemRedirectHandler(urllib.request.HTTPRedirectHandler):
    """
    HTTPRedirectHandler koji validira svaki 3xx Location cilj kroz `validate_oem_download_url`
    PRE slanja novog zahteva (štiti od SSRF / 3xx Open Redirect bypass-a HTTPS allowliste)
    i ograničava broj uzastopnih redirekcija na `max_redirects`.
    """

    def __init__(
        self,
        allowed_hosts: Optional[Sequence[str]] = None,
        max_redirects: int = MAX_OEM_REDIRECTS,
    ):
        super().__init__()
        self.allowed_hosts = allowed_hosts
        self.max_redirects = max(0, int(max_redirects))

    def redirect_request(self, req, fp, code, msg, headers, newurl):
        hops = int(getattr(req, "_wis_redirect_hops", 0)) + 1
        if hops > self.max_redirects:
            if fp is not None:
                try:
                    fp.close()
                except Exception:
                    pass
            raise ValueError(f"OEM redirekcija prelazi maksimalni limit ({self.max_redirects} skokova): {newurl}")

        resolved_url = urllib.parse.urljoin(req.full_url, str(newurl or "").strip())
        try:
            valid_target = validate_oem_download_url(resolved_url, allowed_hosts=self.allowed_hosts)
        except ValueError:
            if fp is not None:
                try:
                    fp.close()
                except Exception:
                    pass
            raise

        new_req = super().redirect_request(req, fp, code, msg, headers, valid_target)
        if new_req is not None:
            new_req._wis_redirect_hops = hops
            if req.get_method().upper() == "HEAD" and code in (301, 302, 307, 308):
                new_req.method = "HEAD"
        return new_req


def open_safe_oem_url(
    req: urllib.request.Request,
    timeout: float = 30.0,
    allowed_hosts: Optional[Sequence[str]] = None,
    max_redirects: int = MAX_OEM_REDIRECTS,
):
    """
    Otvara OEM zahtev kroz `urllib.request.build_opener(_SafeOemRedirectHandler(...))`
    uz pred-proveru inicijalnog URL-a, validaciju svakog 3xx skoka pre slanja i post-response
    validaciju finalnog `resp.geturl()`.
    """
    validate_oem_download_url(req.full_url, allowed_hosts=allowed_hosts)
    opener = urllib.request.build_opener(
        _SafeOemRedirectHandler(allowed_hosts=allowed_hosts, max_redirects=max_redirects)
    )
    resp = opener.open(req, timeout=timeout)
    try:
        final_url = resp.geturl() if hasattr(resp, "geturl") else req.full_url
        validate_oem_download_url(final_url, allowed_hosts=allowed_hosts)
    except Exception:
        try:
            resp.close()
        except Exception:
            pass
        raise
    return resp


def _check_xml_safe(xml_bytes: bytes) -> bytes:
    """XXE / Entity-expansion zaštita pre XML parsiranja."""
    if not xml_bytes or not isinstance(xml_bytes, (bytes, bytearray)):
        raise ValueError("Prazan XML sadržaj kataloga.")
    head_upper = bytes(xml_bytes[:65536]).upper()
    if b"<!ENTITY" in head_upper or b"<!DOCTYPE" in head_upper:
        raise ValueError("XML katalog sadrži zabranjenu <!DOCTYPE / <!ENTITY deklaraciju.")
    # Skini UTF-8 BOM ako postoji
    if xml_bytes.startswith(b"\xef\xbb\xbf"):
        return bytes(xml_bytes[3:])
    return bytes(xml_bytes)


def _strip_ns(tag: str) -> str:
    if "}" in tag:
        return tag.split("}", 1)[1]
    return tag


def _norm_model_tokens(text: str) -> List[str]:
    if not text:
        return []
    s = str(text).lower()
    for noise in (
        "hewlett packard",
        "hewlett-packard",
        "hp inc.",
        "hp inc",
        "dell inc.",
        "dell inc",
        "lenovo group",
        "microsoft corporation",
        "fujitsu client computing limited",
        "fujitsu client computing",
        "asustek computer inc.",
        "asustek computer",
        "micro-star international",
        "dynabook inc.",
        "notebook pc",
        "desktop mini pc",
        "desktop mini",
        "desktop pc",
        "workstation",
        "mobile workstation",
        "notebook",
        "all-in-one",
        "aio",
        "pc",
    ):
        s = re.sub(rf"\b{re.escape(noise)}\b", " ", s)
    return [t for t in re.split(r"[^a-z0-9]+", s) if t]


def _extract_os_family_and_feature(os_text: str) -> Tuple[str, int]:
    """Vraća ('win11'|'win10'|'other', feature_rank)."""
    up = str(os_text or "").upper()
    if "WIN11" in up or "WINDOWS 11" in up or "WINDOWS11" in up:
        os_fam = "win11"
    elif "WIN10" in up or "WINDOWS 10" in up or "WINDOWS10" in up:
        os_fam = "win10"
    else:
        os_fam = "other"

    feat_rank = _FEATURE_ORDER.get("", 50)
    m = re.search(r"\b(2[0-9]H[12])\b", up)
    if m:
        feat_rank = _FEATURE_ORDER.get(m.group(1), 150)
    else:
        for bld, (bfam, brank) in _WIN11_BUILD_RANKS.items():
            if bld in up:
                if os_fam == "other":
                    os_fam = bfam
                feat_rank = brank
                break
        if os_fam == "other" or feat_rank == _FEATURE_ORDER.get("", 50):
            for bld, (bfam, brank) in _WIN10_BUILD_RANKS.items():
                if bld in up:
                    if os_fam == "other":
                        os_fam = bfam
                    if feat_rank == _FEATURE_ORDER.get("", 50):
                        feat_rank = brank
                    break
        if feat_rank == _FEATURE_ORDER.get("", 50) and "*" in up:
            feat_rank = _FEATURE_ORDER["*"]
    return os_fam, feat_rank


def _parse_iso_or_us_date(date_str: str) -> str:
    s = str(date_str or "").strip()
    if not s:
        return ""
    head = s.split("T")[0].strip()
    for fmt in ("%Y-%m-%d", "%m/%d/%Y %I:%M:%S %p", "%m/%d/%Y", "%Y/%m/%d"):
        try:
            return datetime.strptime(s if "%" in fmt and " " in fmt else head, fmt).strftime("%Y-%m-%d")
        except ValueError:
            pass
    m = re.match(r"^(\d{1,2})/(\d{1,2})/(\d{4})", s)
    if m:
        try:
            return datetime(int(m.group(3)), int(m.group(1)), int(m.group(2))).strftime("%Y-%m-%d")
        except ValueError:
            pass
    return head[:10]


def extract_xml_from_cab(cab_bytes: bytes) -> bytes:
    """Ekstrahuje prvi .xml fajl iz .cab arhive koristeći bsdtar ili cabextract."""
    if not cab_bytes:
        raise ValueError("Prazan .cab bajt niz.")
    bsdtar_bin = shutil.which("bsdtar")
    cabextract_bin = shutil.which("cabextract")
    if not bsdtar_bin and not cabextract_bin:
        raise RuntimeError("Nedostaje alat za raspakivanje .cab XML kataloga (bsdtar ili cabextract).")

    with tempfile.TemporaryDirectory(prefix="wis-oem-cab-") as tmp_dir:
        cab_path = os.path.join(tmp_dir, "catalog.cab")
        with open(cab_path, "wb") as f:
            f.write(cab_bytes)

        if bsdtar_bin:
            list_proc = subprocess.run([bsdtar_bin, "-tf", cab_path], capture_output=True, text=True, timeout=30, check=False)
            if list_proc.returncode == 0:
                entries = [line.strip() for line in (list_proc.stdout or "").splitlines() if line.strip()]
                xml_entries = [e for e in entries if e.lower().endswith(".xml") and ".." not in e and not e.startswith("/")]
                if xml_entries:
                    ext_proc = subprocess.run([bsdtar_bin, "-xOf", cab_path, xml_entries[0]], capture_output=True, timeout=60, check=False)
                    if ext_proc.returncode == 0 and ext_proc.stdout:
                        return ext_proc.stdout

        if cabextract_bin:
            out_dir = os.path.join(tmp_dir, "out")
            os.makedirs(out_dir, exist_ok=True)
            ext_proc = subprocess.run([cabextract_bin, "-q", "-d", out_dir, cab_path], capture_output=True, timeout=60, check=False)
            if ext_proc.returncode == 0:
                for root_dir, _, files in os.walk(out_dir):
                    for fn in sorted(files):
                        if fn.lower().endswith(".xml"):
                            with open(os.path.join(root_dir, fn), "rb") as xf:
                                return xf.read()

    raise RuntimeError("Ekstrakcija XML datoteke iz .cab kataloga nije uspela.")


def parse_dell_catalog_xml(xml_bytes: bytes) -> List[Dict[str, Any]]:
    """Parsira Dell Command | Deploy DriverPackCatalog.xml u kanonsku listu zapisa."""
    clean_bytes = _check_xml_safe(xml_bytes)
    root = ET.fromstring(clean_bytes)
    base_loc = (root.attrib.get("baseLocation") or "downloads.dell.com").strip().strip("/")
    entries: List[Dict[str, Any]] = []

    for elem in root.iter():
        if _strip_ns(elem.tag) != "DriverPackage":
            continue
        rel_path = (elem.attrib.get("path") or "").strip().lstrip("/")
        if not rel_path:
            continue

        fmt = (elem.attrib.get("format") or "cab").strip().lower()
        dell_ver = (elem.attrib.get("dellVersion") or elem.attrib.get("vendorVersion") or "").strip()
        release_id = (elem.attrib.get("releaseID") or "").strip()
        date_str = _parse_iso_or_us_date(elem.attrib.get("dateTime") or "")
        md5_val = (elem.attrib.get("hashMD5") or "").strip().lower()
        sha256_val = (elem.attrib.get("hashSHA256") or elem.attrib.get("sha256") or "").strip().lower()
        try:
            size_bytes = int(elem.attrib.get("size") or 0)
        except (TypeError, ValueError):
            size_bytes = 0

        pkg_name = os.path.basename(rel_path) or release_id or "dell-driverpack.cab"
        os_list: List[str] = []
        os_rank = 0
        best_os = "other"
        best_feat = 0
        is_x64 = False

        models: List[str] = []
        system_ids: List[str] = []
        seen_models = set()
        seen_sids = set()

        for child in elem:
            ctag = _strip_ns(child.tag)
            if ctag == "Name":
                for d in child:
                    if _strip_ns(d.tag) == "Display" and (d.text or "").strip():
                        pkg_name = (d.text or "").strip()
                        break
            elif ctag == "SupportedOperatingSystems":
                for os_el in child:
                    if _strip_ns(os_el.tag) != "OperatingSystem":
                        continue
                    os_code = (os_el.attrib.get("osCode") or "").strip()
                    os_arch = (os_el.attrib.get("osArch") or "x64").strip().lower()
                    disp = ""
                    for d in os_el:
                        if _strip_ns(d.tag) == "Display" and (d.text or "").strip():
                            disp = (d.text or "").strip()
                            break
                    if os_arch in ("x64", "amd64", ""):
                        is_x64 = True
                    os_fam, feat_rank = _extract_os_family_and_feature(f"{os_code} {disp}")
                    if os_fam != "other":
                        os_list.append(os_fam)
                    score = (200 if os_fam == "win11" else (100 if os_fam == "win10" else 0)) + feat_rank
                    if os_arch in ("x64", "amd64", "") and score > os_rank:
                        os_rank = score
                        best_os = os_fam
                        best_feat = feat_rank
            elif ctag == "SupportedSystems":
                for brand in child:
                    brand_disp = ""
                    for sub in brand:
                        stag = _strip_ns(sub.tag)
                        if stag == "Display" and (sub.text or "").strip():
                            brand_disp = (sub.text or "").strip()
                        elif stag == "Model":
                            m_name = (sub.attrib.get("name") or "").strip()
                            sid = (sub.attrib.get("systemID") or "").strip().lower()
                            m_disp = ""
                            for md in sub:
                                if _strip_ns(md.tag) == "Display" and (md.text or "").strip():
                                    m_disp = (md.text or "").strip()
                                    break
                            for cand_m in (m_name, f"{brand_disp} {m_disp}".strip(), m_disp):
                                if cand_m and cand_m.lower() not in seen_models:
                                    seen_models.add(cand_m.lower())
                                    models.append(cand_m)
                            if sid and sid not in seen_sids:
                                seen_sids.add(sid)
                                system_ids.append(sid)

        if not models:
            continue
        if best_os == "other" or not is_x64:
            continue

        download_url = f"https://{base_loc}/{rel_path}"
        entries.append({
            "vendor": "dell",
            "package_id": release_id or pkg_name,
            "package_name": pkg_name,
            "version": dell_ver,
            "release_date": date_str,
            "os": best_os,
            "os_feature_rank": best_feat,
            "os_supported": sorted(set(os_list)),
            "arch": "x64",
            "format": fmt,
            "download_url": download_url,
            "size_bytes": size_bytes,
            "sha256": sha256_val,
            "md5": md5_val,
            "models": models,
            "system_ids": system_ids,
            "mtms": [],
        })
    return entries


def parse_hp_catalog_xml(xml_bytes: bytes) -> List[Dict[str, Any]]:
    """Parsira HPClientDriverPackCatalog.xml u kanonsku listu zapisa."""
    clean_bytes = _check_xml_safe(xml_bytes)
    root = ET.fromstring(clean_bytes)

    softpaqs: Dict[str, Dict[str, Any]] = {}
    grouped_packs: Dict[str, Dict[str, Any]] = {}

    for elem in root.iter():
        if "XMLSchema" in elem.tag:
            continue
        ltag = _strip_ns(elem.tag)
        if ltag == "SoftPaq":
            sp: Dict[str, Any] = {}
            for c in elem:
                sp[_strip_ns(c.tag)] = (c.text or "").strip()
            sp_id = (sp.get("Id") or "").strip().lower()
            sp_url = (sp.get("Url") or "").strip()
            if sp_id and sp_url:
                try:
                    sz = int(sp.get("Size") or 0)
                except (TypeError, ValueError):
                    sz = 0
                softpaqs[sp_id] = {
                    "id": sp_id,
                    "name": sp.get("Name") or sp.get("CvaTitle") or sp_id,
                    "version": sp.get("Version") or "",
                    "release_date": _parse_iso_or_us_date(sp.get("DateReleased") or ""),
                    "download_url": sp_url,
                    "size_bytes": sz,
                    "md5": (sp.get("MD5") or "").strip().lower(),
                    "sha256": (sp.get("SHA256") or "").strip().lower(),
                }
        elif ltag == "ProductOSDriverPack":
            rec: Dict[str, str] = {}
            for c in elem:
                rec[_strip_ns(c.tag)] = (c.text or "").strip()
            arch = (rec.get("Architecture") or "").lower()
            if arch and "64" not in arch:
                continue
            sp_id = (rec.get("SoftPaqId") or "").strip().lower()
            sys_name = (rec.get("SystemName") or "").strip()
            os_name = (rec.get("OSName") or "").strip()
            raw_sids = (rec.get("SystemId") or "").strip()
            if not sp_id or not sys_name:
                continue
            os_fam, feat_rank = _extract_os_family_and_feature(os_name)
            if os_fam == "other":
                continue

            key = f"{sp_id}|{os_fam}|{feat_rank}"
            grp = grouped_packs.get(key)
            if grp is None:
                grp = {
                    "sp_id": sp_id,
                    "os": os_fam,
                    "os_feature_rank": feat_rank,
                    "models": [],
                    "system_ids": [],
                    "_seen_m": set(),
                    "_seen_s": set(),
                }
                grouped_packs[key] = grp
            if sys_name.lower() not in grp["_seen_m"]:
                grp["_seen_m"].add(sys_name.lower())
                grp["models"].append(sys_name)
            for sid_part in raw_sids.split(","):
                sid_clean = sid_part.strip().lower()
                if sid_clean and sid_clean not in grp["_seen_s"]:
                    grp["_seen_s"].add(sid_clean)
                    grp["system_ids"].append(sid_clean)

    entries: List[Dict[str, Any]] = []
    for grp in grouped_packs.values():
        sp_meta = softpaqs.get(grp["sp_id"])
        if not sp_meta:
            continue
        url = sp_meta["download_url"]
        pkg_name = os.path.basename(urllib.parse.urlparse(url).path) or f"{grp['sp_id']}.exe"
        entries.append({
            "vendor": "hp",
            "package_id": sp_meta["id"],
            "package_name": pkg_name,
            "version": sp_meta["version"],
            "release_date": sp_meta["release_date"],
            "os": grp["os"],
            "os_feature_rank": grp["os_feature_rank"],
            "os_supported": [grp["os"]],
            "arch": "x64",
            "format": "exe",
            "download_url": url,
            "size_bytes": sp_meta["size_bytes"],
            "sha256": sp_meta["sha256"],
            "md5": sp_meta["md5"],
            "models": grp["models"],
            "system_ids": grp["system_ids"],
            "mtms": [],
        })
    return entries


def parse_lenovo_catalog_xml(xml_bytes: bytes) -> List[Dict[str, Any]]:
    """Parsira Lenovo CDRT catalogv2.xml u kanonsku listu zapisa."""
    clean_bytes = _check_xml_safe(xml_bytes)
    root = ET.fromstring(clean_bytes)
    entries: List[Dict[str, Any]] = []

    for model_el in root.iter():
        if _strip_ns(model_el.tag) != "Model":
            continue
        model_name = (model_el.attrib.get("name") or "").strip()
        if not model_name:
            continue

        mtms: List[str] = []
        seen_mtm = set()
        sccm_elems = []
        for child in model_el:
            ctag = _strip_ns(child.tag)
            if ctag == "Types":
                for t_el in child:
                    if _strip_ns(t_el.tag) == "Type":
                        mtm = (t_el.text or "").strip().upper()
                        if mtm and mtm not in seen_mtm:
                            seen_mtm.add(mtm)
                            mtms.append(mtm)
            elif ctag == "SCCM":
                sccm_elems.append(child)

        for sccm in sccm_elems:
            dl_url = (sccm.text or "").strip()
            if not dl_url:
                continue
            os_attr = (sccm.attrib.get("os") or "").strip().lower()
            ver_attr = (sccm.attrib.get("version") or "*").strip()
            date_str = _parse_iso_or_us_date(sccm.attrib.get("date") or "")
            sha256_val = (sccm.attrib.get("crc") or "").strip().lower()
            md5_val = (sccm.attrib.get("md5") or "").strip().lower()

            os_fam, feat_rank = _extract_os_family_and_feature(f"{os_attr} {ver_attr}")
            if os_fam == "other":
                continue

            pkg_name = os.path.basename(urllib.parse.urlparse(dl_url).path) or "lenovo-sccm.exe"
            entries.append({
                "vendor": "lenovo",
                "package_id": pkg_name,
                "package_name": pkg_name,
                "version": ver_attr,
                "release_date": date_str,
                "os": os_fam,
                "os_feature_rank": feat_rank,
                "os_supported": [os_fam],
                "arch": "x64",
                "format": "exe",
                "download_url": dl_url,
                "size_bytes": 0,
                "sha256": sha256_val,
                "md5": md5_val,
                "models": [model_name],
                "system_ids": [],
                "mtms": mtms,
            })
    return entries


class OemCatalogManager:
    """
    Upravlja lokalnim kešom zvaničnih OEM Enterprise kataloga (Dell/HP/Lenovo/Microsoft/Fujitsu),
    Tier 2 Hybrid Drop-in paketima (ASUS/Acer/MSI/Dynabook), pretragom paketa po
    modelu/MTM/SystemId/SystemSKU i verifikovanim preuzimanjem ili lokalnim kopiranjem.
    """

    def __init__(
        self,
        catalogs_dir: str,
        ttl_seconds: int = DEFAULT_CATALOG_TTL_SEC,
        catalog_urls: Optional[Dict[str, str]] = None,
        allowed_hosts: Optional[Sequence[str]] = None,
        dropin_dir: Optional[str] = None,
    ):
        self.catalogs_dir = os.path.abspath(catalogs_dir)
        self.ttl_seconds = max(60, int(ttl_seconds))
        self._custom_catalog_urls = set(catalog_urls.keys()) if catalog_urls else set()
        self.catalog_urls = dict(CATALOG_URLS)
        if catalog_urls:
            self.catalog_urls.update(catalog_urls)
        self.allowed_hosts = list(allowed_hosts) if allowed_hosts is not None else None
        raw_dropin = (
            dropin_dir
            if (dropin_dir is not None and str(dropin_dir).strip())
            else os.environ.get("SERVIS_OEM_DROPIN_DIR", "/app/oem_dropin")
        )
        self.dropin_dir = os.path.abspath(str(raw_dropin).strip())
        self.lock = threading.RLock()
        self._memory_cache: Dict[str, Dict[str, Any]] = {}
        os.makedirs(self.catalogs_dir, exist_ok=True)

    def _catalog_file(self, vendor: str) -> str:
        safe = re.sub(r"[^a-z0-9_-]", "", vendor.lower())
        return os.path.join(self.catalogs_dir, f"{safe}_catalog.json")

    def _load_vendor_catalog(self, vendor: str) -> Optional[Dict[str, Any]]:
        v = vendor.lower()
        with self.lock:
            cached = self._memory_cache.get(v)
            if cached is not None:
                return cached
            path = self._catalog_file(v)
            if not os.path.isfile(path):
                return None
            try:
                with open(path, "r", encoding="utf-8") as f:
                    data = json.load(f)
                if isinstance(data, dict) and isinstance(data.get("entries"), list):
                    self._memory_cache[v] = data
                    return data
            except Exception:
                return None
        return None

    def _save_vendor_catalog(self, vendor: str, entries: List[Dict[str, Any]], source_url: str) -> Dict[str, Any]:
        v = vendor.lower()
        payload = {
            "vendor": v,
            "source_url": source_url,
            "synced_at": datetime.now().isoformat(),
            "synced_ts": time.time(),
            "entries_count": len(entries),
            "entries": entries,
        }
        path = self._catalog_file(v)
        tmp_path = f"{path}.tmp"
        with self.lock:
            os.makedirs(self.catalogs_dir, exist_ok=True)
            with open(tmp_path, "w", encoding="utf-8") as f:
                json.dump(payload, f, ensure_ascii=False)
            os.replace(tmp_path, path)
            self._memory_cache[v] = payload
        return payload

    def save_parsed_entries(self, vendor: str, entries: List[Dict[str, Any]], source_url: str = "local") -> Dict[str, Any]:
        """Omogućava direktan upis već parsiranih katalog zapisa (npr. u offline okruženju)."""
        return self._save_vendor_catalog(vendor, entries, source_url=source_url)

    def _http_fetch_bytes(self, url: str, timeout: float = 60.0, max_bytes: int = MAX_CATALOG_BYTES) -> bytes:
        valid_url = validate_oem_download_url(url, allowed_hosts=self.allowed_hosts)
        req = urllib.request.Request(valid_url, headers={"User-Agent": "WIS-ServiceHub/3.5"})
        buf = bytearray()
        with open_safe_oem_url(req, timeout=timeout, allowed_hosts=self.allowed_hosts) as resp:
            while True:
                chunk = resp.read(65536)
                if not chunk:
                    break
                if len(buf) + len(chunk) > max_bytes:
                    raise ValueError(f"OEM odgovor prelazi maksimalni limit ({max_bytes} B): {valid_url}")
                buf.extend(chunk)
        return bytes(buf)

    def sync_catalog(self, vendor: str, force: bool = False, timeout: float = 90.0) -> Dict[str, Any]:
        """
        Preuzima i parsira zvanični katalog za Tier 1 ('dell'|'hp'|'lenovo'|'microsoft'|'fujitsu')
        ili skenira lokalni Drop-in direktorijum za Tier 2 ('asus'|'acer'|'msi'|'dynabook').
        """
        v = str(vendor or "").strip().lower()
        if v not in TIER1_OEM_VENDORS and v not in TIER2_DROPIN_VENDORS:
            raise ValueError(f"Nepodržan OEM katalog proizvođač: '{vendor}'")

        existing = self._load_vendor_catalog(v)
        if not force and existing:
            age = time.time() - float(existing.get("synced_ts") or 0)
            if 0 <= age < self.ttl_seconds and existing.get("entries"):
                return {
                    "vendor": v,
                    "status": "cached",
                    "entries_count": len(existing["entries"]),
                    "synced_at": existing.get("synced_at"),
                }

        if v in TIER2_DROPIN_VENDORS:
            from servishub.drivers.oem.dropin import DropInOEMProvider
            dropin_entries = DropInOEMProvider(vendor=v, dropin_dir=self.dropin_dir).scan_packages(vendor=v)
            source_url = f"dropin://{v}"
            saved = self._save_vendor_catalog(v, dropin_entries, source_url=source_url)
            return {
                "vendor": v,
                "status": "synced",
                "entries_count": saved["entries_count"],
                "synced_at": saved["synced_at"],
                "source_url": source_url,
            }

        if v == "microsoft":
            from servishub.drivers.oem.dropin import DropInOEMProvider
            from servishub.drivers.oem.microsoft_surface import MicrosoftOEMProvider, parse_microsoft_catalog
            custom_env_url = os.environ.get("SERVIS_SURFACE_CATALOG_URL", "").strip()
            if v in self._custom_catalog_urls or custom_env_url:
                url = custom_env_url or self.catalog_urls[v]
                raw_bytes = self._http_fetch_bytes(url, timeout=timeout)
                entries = parse_microsoft_catalog(raw_bytes, allowed_hosts=self.allowed_hosts)
            else:
                dropin_entries = DropInOEMProvider(vendor="microsoft", dropin_dir=self.dropin_dir).scan_packages(
                    vendor="microsoft"
                )
                dynamic_entries: List[Dict[str, Any]] = []
                if os.environ.get("SERVIS_SURFACE_DYNAMIC_DISCOVERY", "").strip().lower() in ("1", "true", "yes"):
                    dynamic_entries = MicrosoftOEMProvider.discover_verified_catalog_entries(
                        timeout=min(float(timeout), 10.0),
                        verify_head=True,
                        allowed_hosts=self.allowed_hosts,
                    )
                builtin_entries = MicrosoftOEMProvider.get_builtin_catalog_entries()
                entries = list(dropin_entries) + list(dynamic_entries) + list(builtin_entries)
                if dynamic_entries:
                    url = "dynamic+microsoft-download-center"
                elif dropin_entries:
                    url = "dropin+microsoft-surface-index"
                else:
                    url = "builtin://microsoft-surface-index"
            saved = self._save_vendor_catalog(v, entries, source_url=url)
            return {
                "vendor": v,
                "status": "synced",
                "entries_count": saved["entries_count"],
                "synced_at": saved["synced_at"],
                "source_url": url,
            }

        if v == "fujitsu":
            from servishub.drivers.oem.dropin import DropInOEMProvider
            from servishub.drivers.oem.fujitsu import parse_fujitsu_catalog_xml
            url = self.catalog_urls.get(v) or CATALOG_URLS[v]
            if v in self._custom_catalog_urls:
                raw_bytes = self._http_fetch_bytes(url, timeout=timeout)
                entries = parse_fujitsu_catalog_xml(raw_bytes, allowed_hosts=self.allowed_hosts)
            else:
                try:
                    raw_bytes = self._http_fetch_bytes(url, timeout=timeout)
                    entries = parse_fujitsu_catalog_xml(raw_bytes, allowed_hosts=self.allowed_hosts)
                except (
                    urllib.error.URLError,
                    urllib.error.HTTPError,
                    OSError,
                    TimeoutError,
                    ValueError,
                    ET.ParseError,
                ):
                    entries = []
                if not entries:
                    url = "dropin://fujitsu"
                    entries = DropInOEMProvider(vendor="fujitsu", dropin_dir=self.dropin_dir).scan_packages(
                        vendor="fujitsu"
                    )
            saved = self._save_vendor_catalog(v, entries, source_url=url)
            return {
                "vendor": v,
                "status": "synced",
                "entries_count": saved["entries_count"],
                "synced_at": saved["synced_at"],
                "source_url": url,
            }

        url = self.catalog_urls.get(v) or CATALOG_URLS[v]
        raw_bytes = self._http_fetch_bytes(url, timeout=timeout)
        if url.lower().endswith(".cab") or raw_bytes[:4] == b"MSCF":
            xml_bytes = extract_xml_from_cab(raw_bytes)
        else:
            xml_bytes = raw_bytes

        if v == "dell":
            entries = parse_dell_catalog_xml(xml_bytes)
        elif v == "hp":
            entries = parse_hp_catalog_xml(xml_bytes)
        else:
            entries = parse_lenovo_catalog_xml(xml_bytes)

        saved = self._save_vendor_catalog(v, entries, source_url=url)
        return {
            "vendor": v,
            "status": "synced",
            "entries_count": saved["entries_count"],
            "synced_at": saved["synced_at"],
            "source_url": url,
        }

    def sync_all_catalogs(
        self,
        force: bool = False,
        timeout: float = 90.0,
        include_tier2: bool = False,
    ) -> Dict[str, Any]:
        results: Dict[str, Any] = {}
        ok_count = 0
        vendors = TIER1_OEM_VENDORS + (TIER2_DROPIN_VENDORS if include_tier2 else ())
        for v in vendors:
            try:
                results[v] = self.sync_catalog(v, force=force, timeout=timeout)
                ok_count += 1
            except Exception as ex:
                results[v] = {"vendor": v, "status": "error", "error": str(ex)}
        return {
            "status": "ok" if ok_count > 0 else "error",
            "synced_vendors": ok_count,
            "catalogs": results,
        }

    def get_catalog_status(self) -> Dict[str, Any]:
        out: Dict[str, Any] = {}
        now = time.time()
        for v in TIER1_OEM_VENDORS + TIER2_DROPIN_VENDORS:
            data = self._load_vendor_catalog(v)
            if not data:
                out[v] = {
                    "synced": False,
                    "synced_at": None,
                    "age_hours": None,
                    "entries_count": 0,
                    "stale": True,
                }
                continue
            ts = float(data.get("synced_ts") or 0)
            age_sec = max(0.0, now - ts) if ts > 0 else float(self.ttl_seconds + 1)
            out[v] = {
                "synced": True,
                "synced_at": data.get("synced_at"),
                "age_hours": round(age_sec / 3600.0, 2),
                "entries_count": int(data.get("entries_count") or len(data.get("entries") or [])),
                "stale": age_sec >= self.ttl_seconds,
            }
        return out

    @staticmethod
    def _extract_lenovo_mtm_tokens(text: str) -> List[str]:
        tokens: List[str] = []
        up = str(text or "").upper()
        # Hvata 4-karakterne MTM prefikse kao 20TD, 21AH, 83JE, 10SV (moraju imati i slova i cifre)
        for cand in re.findall(r"\b([A-Z0-9]{4})(?:[A-Z0-9]{2,6})?\b", up):
            if any(c.isdigit() for c in cand) and any(c.isalpha() for c in cand):
                if not re.fullmatch(r"2[0-9]H[12]|WIN1[01]|GEN[0-9]|INTEL|AMD", cand):
                    tokens.append(cand)
        return tokens

    @staticmethod
    def _score_model_match(query_model: str, query_tokens: List[str], cand_model: str) -> int:
        q_norm = re.sub(r"[^a-z0-9]+", " ", query_model.lower()).strip()
        c_norm = re.sub(r"[^a-z0-9]+", " ", cand_model.lower()).strip()
        if not q_norm or not c_norm:
            return 0
        q_clean = " ".join(query_tokens)
        c_tokens = _norm_model_tokens(cand_model)
        c_clean = " ".join(c_tokens)
        if not q_clean or not c_clean:
            return 0
        if q_clean == c_clean:
            return 100

        # Svi numerički i g<N> tokeni u upitu ili kandidatu predstavljaju generaciju / seriju — ne mešati 840 G7 i 840 G8!
        q_nums = {t for t in query_tokens if any(ch.isdigit() for ch in t)}
        c_nums = {t for t in c_tokens if any(ch.isdigit() for ch in t)}
        if q_nums and c_nums and not (q_nums <= c_nums and len(q_nums) == len(c_nums)):
            # Dozvoli jedino ako je jedan podniz drugog samo za sufiks tipa 'notebook' bez dodatnih brojeva
            if q_nums != c_nums:
                return 0

        q_set = set(query_tokens)
        c_set = set(c_tokens)
        if q_set == c_set:
            return 95
        if q_set and q_set <= c_set:
            extra = len(c_set - q_set)
            return max(60, 88 - extra * 5)
        if c_set and c_set <= q_set:
            extra = len(q_set - c_set)
            return max(55, 84 - extra * 5)
        return 0

    def find_driver_pack(
        self,
        manufacturer: str = "",
        model: str = "",
        os_ver: str = "win11",
        system_id: str = "",
        mtm: str = "",
        system_sku: str = "",
    ) -> Optional[Dict[str, Any]]:
        """
        Pronalazi najnoviji zvanični OEM Driver Pack iz lokalno keširanog kataloga
        (Dell/HP/Lenovo/Microsoft Surface/Fujitsu) ili preko DropInOEMProvider-a
        (Tier 2 ASUS/Acer/MSI/Dynabook i lokalni fallback za Tier 1).
        """
        vendor = detect_oem_vendor(manufacturer, model)
        if not vendor and system_sku:
            vendor = detect_oem_vendor(manufacturer, f"{model} {system_sku}")
        if not vendor:
            return None

        if vendor in TIER2_DROPIN_VENDORS:
            from servishub.drivers.oem.dropin import DropInOEMProvider
            dropin_hits = DropInOEMProvider(vendor=vendor, dropin_dir=self.dropin_dir).scan_packages(
                vendor=vendor,
                model=model,
                os_ver=os_ver,
            )
            if dropin_hits:
                return dropin_hits[0]
            cat_t2 = self._load_vendor_catalog(vendor)
            if cat_t2 and cat_t2.get("entries"):
                for entry in cat_t2["entries"]:
                    lp = str(entry.get("local_path") or "").strip()
                    if lp and os.path.isfile(lp) and not os.path.islink(lp):
                        q_tokens_t2 = _norm_model_tokens(model)
                        for cand_m in (entry.get("models") or []):
                            sc = self._score_model_match(model, q_tokens_t2, cand_m)
                            if sc > 0:
                                res_e = dict(entry)
                                res_e["match_score"] = sc
                                return res_e
            return None

        cat = self._load_vendor_catalog(vendor)
        entries_list = list(cat.get("entries") or []) if cat else []
        if not entries_list and vendor == "microsoft":
            from servishub.drivers.oem.microsoft_surface import MicrosoftOEMProvider
            entries_list = MicrosoftOEMProvider.get_builtin_catalog_entries()

        req_os, req_feat = _extract_os_family_and_feature(os_ver or "win11")
        if req_os == "other":
            req_os = "win11"

        q_tokens = _norm_model_tokens(model)
        q_sid = str(system_id or "").strip().lower()
        q_sku = str(system_sku or "").strip().lower()
        mtm_candidates = set()
        if mtm:
            mtm_candidates.add(mtm.strip().upper()[:4])
        for t in self._extract_lenovo_mtm_tokens(model):
            mtm_candidates.add(t)

        best_entry = None
        best_rank: Tuple[int, int, int, int, int, str] = (-1, -1, -1, -1, -1, "")

        for entry in entries_list:
            match_score = 0
            entry_skus = [s.lower() for s in (entry.get("system_skus") or [])]
            entry_sids = [s.lower() for s in (entry.get("system_ids") or [])]
            if q_sku and (q_sku in entry_skus or q_sku in entry_sids):
                match_score = max(match_score, 100)
            if q_sid and (q_sid in entry_sids or q_sid in entry_skus):
                match_score = max(match_score, 98)
            if mtm_candidates:
                entry_mtms = {m.upper()[:4] for m in (entry.get("mtms") or [])}
                if mtm_candidates & entry_mtms:
                    match_score = max(match_score, 96)
            for cand_m in (entry.get("models") or []):
                sc = self._score_model_match(model, q_tokens, cand_m)
                if sc > match_score:
                    match_score = sc

            if match_score <= 0:
                continue

            entry_os = entry.get("os") or "other"
            if req_os == "win11":
                os_match_score = 2 if entry_os == "win11" else (1 if entry_os == "win10" else 0)
            else:
                os_match_score = 2 if entry_os == req_os else 1

            feat_rank = int(entry.get("os_feature_rank") or 0)
            exact_feat_bonus = 1 if (req_feat > 100 and feat_rank == req_feat) else 0
            rel_date = str(entry.get("release_date") or "")
            is_downloadable = 1 if (str(entry.get("download_url") or "").strip() or str(entry.get("local_path") or "").strip()) else 0
            rank = (match_score, is_downloadable, os_match_score, exact_feat_bonus, feat_rank, rel_date)
            if rank > best_rank:
                best_rank = rank
                best_entry = dict(entry)
                best_entry["match_score"] = match_score

        if best_entry is not None:
            # Ako je katalog vratio samo indeksni zapis bez lokalnog fajla / verifikovanog CDN URL-a
            # (npr. Microsoft Surface Download Center referenca), proveri lokalni Drop-in prvo!
            if not str(best_entry.get("download_url") or "").strip() and not str(best_entry.get("local_path") or "").strip():
                from servishub.drivers.oem.dropin import DropInOEMProvider
                dropin_prov = DropInOEMProvider(vendor=vendor, dropin_dir=self.dropin_dir)
                dropin_hits = []
                if system_sku:
                    dropin_hits = dropin_prov.scan_packages(vendor=vendor, model=system_sku, os_ver=os_ver)
                if not dropin_hits:
                    dropin_hits = dropin_prov.scan_packages(vendor=vendor, model=model, os_ver=os_ver)
                if dropin_hits:
                    chosen_dropin = dict(dropin_hits[0])
                    if best_entry.get("download_page_url") and not chosen_dropin.get("download_page_url"):
                        chosen_dropin["download_page_url"] = best_entry["download_page_url"]
                    return chosen_dropin
            return best_entry

        # Tier 1 fallback na Drop-in ako zvanični katalog nije imao pogodak
        from servishub.drivers.oem.dropin import DropInOEMProvider
        dropin_prov = DropInOEMProvider(vendor=vendor, dropin_dir=self.dropin_dir)
        dropin_fallback = dropin_prov.scan_packages(
            vendor=vendor,
            model=model,
            os_ver=os_ver,
        )
        if not dropin_fallback and system_sku:
            dropin_fallback = dropin_prov.scan_packages(
                vendor=vendor,
                model=system_sku,
                os_ver=os_ver,
            )
        if dropin_fallback:
            return dropin_fallback[0]
        return None

    def download_driver_pack(
        self,
        entry: Dict[str, Any],
        dest_dir: str,
        timeout: float = 600.0,
        max_bytes: int = MAX_OEM_PACKAGE_BYTES,
    ) -> str:
        """
        Preuzima OEM driver pack u `dest_dir` sa validacijom URL-a,
        atomičnim `.tmp` upisom i obaveznom verifikacijom SHA-256 (ili MD5) heša.
        Ako `entry` sadrži postojeći `local_path` (Drop-in), bezbedno kopira lokalni fajl
        bez HTTP preuzimanja.
        """
        expected_sha256 = str(entry.get("sha256") or "").strip().lower()
        expected_md5 = str(entry.get("md5") or "").strip().lower()
        local_src = str(entry.get("local_path") or "").strip()

        if local_src:
            if os.path.islink(local_src):
                raise ValueError(f"Odbijeno kopiranje simboličkog linka: {local_src}")
            if not os.path.isfile(local_src):
                raise FileNotFoundError(f"Lokalni OEM drop-in fajl ne postoji: {local_src}")
            real_src = os.path.realpath(local_src)
            if str(entry.get("source") or "").lower() == "dropin":
                real_dropin = os.path.realpath(self.dropin_dir)
                if os.path.commonpath([real_dropin, real_src]) != real_dropin:
                    raise ValueError(f"Detektovan path traversal za drop-in paket: {local_src}")

            raw_name = os.path.basename((entry.get("package_name") or "").strip()) or os.path.basename(real_src) or "oem_pack.bin"
            safe_name = re.sub(r"[^a-zA-Z0-9._-]", "_", raw_name)
            if not safe_name or safe_name.startswith("."):
                safe_name = "oem_driver_pack.bin"

            os.makedirs(dest_dir, exist_ok=True)
            dest_path = os.path.join(os.path.abspath(dest_dir), safe_name)
            tmp_path = f"{dest_path}.tmp"
            sha_h = hashlib.sha256()
            md5_h = hashlib.md5()
            total_bytes = 0
            try:
                with open(real_src, "rb") as src_f, open(tmp_path, "wb") as out_f:
                    while True:
                        chunk = src_f.read(65536)
                        if not chunk:
                            break
                        total_bytes += len(chunk)
                        if total_bytes > max_bytes:
                            raise ValueError(f"OEM paket '{safe_name}' prelazi maksimalni limit ({max_bytes} B).")
                        out_f.write(chunk)
                        sha_h.update(chunk)
                        md5_h.update(chunk)
                if total_bytes <= 0:
                    raise ValueError(f"Lokalni OEM paket '{safe_name}' je prazan (0 B).")
                if expected_sha256 and sha_h.hexdigest().lower() != expected_sha256:
                    raise ValueError(f"SHA-256 neusaglašenost za '{safe_name}'.")
                if expected_md5 and md5_h.hexdigest().lower() != expected_md5:
                    raise ValueError(f"MD5 neusaglašenost za '{safe_name}'.")
                os.replace(tmp_path, dest_path)
                return dest_path
            except Exception:
                if os.path.exists(tmp_path):
                    try:
                        os.remove(tmp_path)
                    except OSError:
                        pass
                raise

        url = validate_oem_download_url(entry.get("download_url") or "", allowed_hosts=self.allowed_hosts)
        raw_name = os.path.basename((entry.get("package_name") or "").strip()) or os.path.basename(urllib.parse.urlparse(url).path) or "oem_pack.bin"
        safe_name = re.sub(r"[^a-zA-Z0-9._-]", "_", raw_name)
        if not safe_name or safe_name.startswith("."):
            safe_name = "oem_driver_pack.bin"

        os.makedirs(dest_dir, exist_ok=True)
        dest_path = os.path.join(os.path.abspath(dest_dir), safe_name)
        tmp_path = f"{dest_path}.tmp"

        sha_h = hashlib.sha256()
        md5_h = hashlib.md5()
        total_bytes = 0

        req = urllib.request.Request(url, headers={"User-Agent": "WIS-ServiceHub/3.5"})
        try:
            with open_safe_oem_url(req, timeout=timeout, allowed_hosts=self.allowed_hosts) as resp, open(tmp_path, "wb") as out_f:
                while True:
                    chunk = resp.read(65536)
                    if not chunk:
                        break
                    total_bytes += len(chunk)
                    if total_bytes > max_bytes:
                        raise ValueError(f"OEM paket '{safe_name}' prelazi maksimalni limit ({max_bytes} B).")
                    out_f.write(chunk)
                    sha_h.update(chunk)
                    md5_h.update(chunk)

            if total_bytes < 512:
                raise ValueError(f"Preuzeti OEM paket '{safe_name}' je premali ({total_bytes} B).")

            actual_sha256 = sha_h.hexdigest().lower()
            actual_md5 = md5_h.hexdigest().lower()
            if expected_sha256 and actual_sha256 != expected_sha256:
                raise ValueError(f"SHA-256 neusaglašenost za '{safe_name}': očekivano {expected_sha256}, dobijeno {actual_sha256}.")
            if expected_md5 and actual_md5 != expected_md5:
                raise ValueError(f"MD5 neusaglašenost za '{safe_name}': očekivano {expected_md5}, dobijeno {actual_md5}.")

            os.replace(tmp_path, dest_path)
            return dest_path
        except Exception:
            if os.path.exists(tmp_path):
                try:
                    os.remove(tmp_path)
                except OSError:
                    pass
            raise
