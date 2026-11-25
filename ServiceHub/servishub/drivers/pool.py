"""
servishub.drivers.pool
======================
Jedinstveni Content-Addressable (CAS) Driver Pool sa deduplikacijom na nivou .inf paketa,
Model Manifest datotekama i automatskim spajanjem sva 3 izvora drajvera:
1. Zvanični OEM Enterprise Driver Pack paketi (HP/Dell/Lenovo)
2. Microsoft Update Catalog .cab paketi
3. Berba drajvera sa klijenata (<Model>.tar / Windows DriverStore)
"""

from __future__ import annotations

import copy
import gc
import hashlib
import json
import os
import re
import shutil
import subprocess
import tarfile
import tempfile
import threading
import time
import uuid
import zipfile
from datetime import datetime
from pathlib import Path
from typing import Any, Dict, Iterable, List, Optional, Sequence, Tuple

from servishub.drivers.hwid import generate_hwid_candidates
from servishub.drivers.oem.catalogs import OemCatalogManager, TIER1_OEM_VENDORS, detect_oem_vendor
from servishub.drivers.oem.microsoft_surface import extract_msi_drivers
from servishub.drivers.version import is_catalog_driver_newer, parse_driver_date

try:
    from servishub.api.updates import (
        _FORBIDDEN_MODEL_PACK_EXTENSIONS,
        is_generic_model_pack_name,
        sanitize_model_pack_name,
    )
except Exception:
    _FORBIDDEN_MODEL_PACK_EXTENSIONS = (
        ".ps1", ".psm1", ".bat", ".cmd", ".vbs", ".vbe", ".js", ".jse",
        ".wsf", ".wsh", ".hta", ".scr", ".msi", ".msp", ".com", ".pif",
    )

    def sanitize_model_pack_name(model_name: Optional[str]) -> str:
        raw = str(model_name or "").strip()
        base = os.path.basename(raw.replace("\\", "/"))
        if base.lower().endswith(".tar"):
            base = base[:-4]
        clean = re.sub(r"[^a-zA-Z0-9._-]+", "_", base).strip("._")
        if not clean:
            raise ValueError("Nedostaje validno ime modela.")
        return clean[:80]

    def is_generic_model_pack_name(clean_model: Optional[str]) -> bool:
        val = str(clean_model or "").strip().lower()
        return val in (
            "", "unknown", "standardni_pc", "pc", "system_product_name",
            "to_be_filled_by_o.e.m", "to_be_filled_by_o_e_m", "default_string",
            "none", "oem",
        )


FORBIDDEN_DRIVER_EXTENSIONS = tuple(_FORBIDDEN_MODEL_PACK_EXTENSIONS)
_IGNORED_INBOX_INFS = frozenset({"prnms009.inf", "autorun.inf"})

MAX_ARCHIVE_FILE_BYTES = int(os.environ.get("SERVIS_POOL_MAX_ARCHIVE_BYTES", str(16 * 1024 ** 3)))
MAX_ARCHIVE_UNPACKED_BYTES = int(os.environ.get("SERVIS_POOL_MAX_UNPACKED_BYTES", str(24 * 1024 ** 3)))
MAX_ARCHIVE_MEMBERS = int(os.environ.get("SERVIS_POOL_MAX_ARCHIVE_MEMBERS", "50000"))

_SOURCE_PRIORITY = {
    "oem_pack": 3,
    "ms_catalog": 2,
    "harvest": 1,
}

# Strogi 4-delni SUBSYS match (VEN + DEV + 8-hex SUBSYS) za IMP-89
_STRICT_SUBSYS_RE = re.compile(r"VEN_[0-9A-F]{4}&DEV_[0-9A-F]{4}&SUBSYS_[0-9A-F]{8}", re.IGNORECASE)

# Prepoznavanje HWID stringova unutar .inf datoteka (PCI, USB, Audio, ACPI, HID, NVMe, SCSI, SD, I2C/SPI)
_POOL_INF_HWID_RE = re.compile(
    r"(?i)(?:^|[^A-Za-z0-9_])("
    r"(?:PCI\\VEN_[0-9A-F]{4}&DEV_[0-9A-F]{4}(?:&SUBSYS_[0-9A-F]{8})?(?:&REV_[0-9A-F]{2,4})?)"
    r"|(?:USB\\VID_[0-9A-F]{4}&PID_[0-9A-F]{4}(?:&REV_[0-9A-F]{2,4})?(?:&MI_[0-9A-F]{2})?)"
    r"|(?:(?:HDAUDIO|INTELAUDIO)\\FUNC_\d+&VEN_[0-9A-F]{4}&DEV_[0-9A-F]{4}(?:&SUBSYS_[0-9A-F]{8})?(?:&REV_[0-9A-F]{2,4})?)"
    r"|(?:ACPI\\VEN_[A-Z0-9_]{3,6}&DEV_[A-Z0-9_]{3,6})"
    r"|(?:ACPI\\[A-Z]{3,4}[0-9A-Z]{4})"
    r"|(?:\*[A-Z]{3,4}[0-9A-Z]{4})"
    r"|(?:HID\\(?:VID_[0-9A-F]{4}&PID_[0-9A-F]{4}|VEN_[A-Z0-9_]{3,6}&DEV_[A-Z0-9_]{3,6})(?:&REV_[0-9A-F]{2,4})?(?:&MI_[0-9A-F]{2})?(?:&COL[0-9A-F]{2})?)"
    r"|(?:(?:BTHENUM|DISPLAY|SCSI|NVME|SD|I2C|SPI)\\[A-Z0-9_&.-]{4,64})"
    r")"
)


def _decode_inf_text(raw: bytes) -> str:
    if not raw:
        return ""
    if raw.startswith((b"\xff\xfe", b"\xfe\xff")):
        try:
            return raw.decode("utf-16", errors="ignore")
        except Exception:
            pass
    if raw.startswith(b"\xef\xbb\xbf"):
        try:
            return raw.decode("utf-8-sig", errors="ignore")
        except Exception:
            pass
    if raw[:400].count(b"\x00") > 10:
        try:
            return raw.decode("utf-16-le", errors="ignore")
        except Exception:
            pass
    try:
        return raw.decode("utf-8", errors="ignore")
    except Exception:
        return raw.decode("latin-1", errors="ignore")


def _strip_inf_comment(line: str) -> str:
    in_quotes = False
    for idx, ch in enumerate(line):
        if ch == '"':
            in_quotes = not in_quotes
        elif ch == ";" and not in_quotes:
            return line[:idx]
    return line


def _resolve_inf_tokens(value: str, strings_map: Dict[str, str]) -> str:
    if "%" not in value or not strings_map:
        return value
    return re.sub(
        r"%([^%\r\n]+)%",
        lambda m: strings_map.get(m.group(1).strip().lower(), m.group(0)),
        value,
    )


def parse_inf_metadata(inf_path: str) -> Optional[Dict[str, Any]]:
    """
    Parsira Windows .inf datoteku u strukturisane metapodatke:
    driver_ver, driver_date, driver_class, provider, catalog_files, hwids.
    Vraća None ako fajl pripada ignorisanim inbox fajlovima ili nije validan drajver INF.
    """
    inf_name = os.path.basename(inf_path)
    if inf_name.lower() in _IGNORED_INBOX_INFS:
        return None

    try:
        with open(inf_path, "rb") as f:
            raw = f.read(4 * 1024 * 1024)
    except OSError:
        return None

    text = _decode_inf_text(raw)
    if not text.strip():
        return None

    section = ""
    version_kv: Dict[str, str] = {}
    strings_kv: Dict[str, str] = {}
    body_lines: List[str] = []

    for raw_line in text.splitlines():
        cleaned = _strip_inf_comment(raw_line).strip()
        if not cleaned:
            continue
        sec_m = re.match(r"^\[([^\]]+)\]$", cleaned)
        if sec_m:
            section = sec_m.group(1).strip().lower()
            continue
        if section == "version" and "=" in cleaned:
            k, v = cleaned.split("=", 1)
            version_kv[k.strip().lower()] = v.strip().strip('"')
        elif (section == "strings" or section.startswith("strings.")) and "=" in cleaned:
            k, v = cleaned.split("=", 1)
            strings_kv[k.strip().lower()] = v.strip().strip('"')
        elif section not in ("version", "strings") and not section.startswith("strings."):
            body_lines.append(cleaned)

    signature = _resolve_inf_tokens(version_kv.get("signature", ""), strings_kv).lower()
    if signature and "windows" not in signature and "chicago" not in signature:
        return None

    driver_class = _resolve_inf_tokens(version_kv.get("class", ""), strings_kv).strip()
    provider = _resolve_inf_tokens(version_kv.get("provider", ""), strings_kv).strip()
    raw_driver_ver = _resolve_inf_tokens(version_kv.get("driverver", ""), strings_kv).strip()

    driver_date = ""
    driver_ver = ""
    if raw_driver_ver:
        parts = [p.strip() for p in raw_driver_ver.split(",") if p.strip()]
        if len(parts) >= 2:
            parsed_d = parse_driver_date(parts[0])
            driver_date = parsed_d.strftime("%Y-%m-%d") if parsed_d else parts[0]
            driver_ver = parts[1]
        elif len(parts) == 1:
            if parse_driver_date(parts[0]) is not None:
                parsed_d = parse_driver_date(parts[0])
                driver_date = parsed_d.strftime("%Y-%m-%d") if parsed_d else parts[0]
            else:
                driver_ver = parts[0]

    catalog_files: List[str] = []
    for k, v in version_kv.items():
        if k == "catalogfile" or k.startswith("catalogfile."):
            cat_name = _resolve_inf_tokens(v, strings_kv).strip()
            if cat_name and cat_name not in catalog_files:
                catalog_files.append(cat_name)

    resolved_body = "\n".join(_resolve_inf_tokens(line, strings_kv) for line in body_lines)
    hwids: List[str] = []
    seen_hw = set()
    for match in _POOL_INF_HWID_RE.findall(resolved_body):
        norm = re.sub(r"\\+", r"\\", str(match).strip().upper())
        if not norm or norm.startswith(("ROOT\\", "SWD\\")):
            continue
        if norm not in seen_hw:
            seen_hw.add(norm)
            hwids.append(norm)

    if not hwids and not driver_ver and not driver_class:
        return None

    return {
        "inf_name": inf_name,
        "inf_stem": os.path.splitext(inf_name)[0],
        "driver_class": driver_class,
        "provider": provider,
        "driver_date": driver_date,
        "driver_ver": driver_ver,
        "catalog_files": catalog_files,
        "hwids": hwids,
    }


def _file_sha256(path: str) -> str:
    h = hashlib.sha256()
    with open(path, "rb") as f:
        while True:
            chunk = f.read(65536)
            if not chunk:
                break
            h.update(chunk)
    return h.hexdigest()


def _is_safe_archive_relpath(raw_name: str) -> bool:
    norm = (raw_name or "").replace("\\", "/")
    if norm.startswith("/") or re.match(r"^[A-Za-z]:", norm) or "\x00" in norm:
        return False
    parts = [p for p in norm.split("/") if p not in ("", ".")]
    return ".." not in parts


def _assert_extracted_tree_safe(root_dir: str) -> None:
    real_root = os.path.realpath(root_dir)
    for dirpath, dirnames, filenames in os.walk(real_root, followlinks=False):
        for dn in dirnames:
            full_d = os.path.join(dirpath, dn)
            if os.path.islink(full_d):
                raise ValueError(f"Arhiva sadrži nedozvoljen symlink direktorijum: {dn}")
        for fn in filenames:
            full_f = os.path.join(dirpath, fn)
            if os.path.islink(full_f):
                raise ValueError(f"Arhiva sadrži nedozvoljen symlink fajl: {fn}")
            if os.path.commonpath([real_root, os.path.realpath(full_f)]) != real_root:
                raise ValueError(f"Detektovan path traversal posle ekstrakcije: {fn}")


def _extract_archive_safely(
    archive_path: str,
    dest_dir: str,
    max_archive_bytes: int = MAX_ARCHIVE_FILE_BYTES,
    max_unpacked_bytes: int = MAX_ARCHIVE_UNPACKED_BYTES,
    max_members: int = MAX_ARCHIVE_MEMBERS,
) -> None:
    """
    Bezbedno raspakuje .tar/.tar.gz/.tgz/.zip/.cab/.7z/.exe(SFX) arhive u `dest_dir`
    sa strogim path-traversal, symlink/hardlink i size-limit proverama.
    """
    if os.path.islink(archive_path):
        raise ValueError(f"Odbijeno raspakivanje simboličkog linka: {archive_path}")
    ap = os.path.abspath(archive_path)
    if max_archive_bytes > 0 and os.path.getsize(ap) > max_archive_bytes:
        raise ValueError(f"Arhiva '{os.path.basename(ap)}' premašuje maksimalnu dozvoljenu veličinu ({max_archive_bytes} B).")
    lower = ap.lower()
    os.makedirs(dest_dir, exist_ok=True)
    real_dest = os.path.realpath(dest_dir)

    # 1. TAR / TAR.GZ / TGZ
    if lower.endswith((".tar", ".tar.gz", ".tgz", ".tar.bz2", ".tar.xz")):
        total_unpacked = 0
        with tarfile.open(ap, "r:*") as tf:
            members = tf.getmembers()
            if max_members > 0 and len(members) > max_members:
                raise ValueError(f"Arhiva sadrži previše članova ({len(members)} > {max_members}).")
            for member in members:
                if not _is_safe_archive_relpath(member.name):
                    raise ValueError(f"Nedozvoljena putanja unutar .tar arhive: '{member.name}'.")
                if member.issym() or member.islnk() or member.ischr() or member.isblk() or member.isfifo():
                    raise ValueError(f"Nedozvoljen link/device član unutar .tar arhive: '{member.name}'.")
                if member.isreg():
                    if member.size < 0 or (max_unpacked_bytes > 0 and (total_unpacked + member.size) > max_unpacked_bytes):
                        raise ValueError(f"Arhiva premašuje maksimalnu raspakovanu veličinu ({max_unpacked_bytes} B).")
                    total_unpacked += int(member.size)
            actual_written = 0
            for member in members:
                if member.isdir():
                    target = os.path.realpath(os.path.join(real_dest, member.name))
                    if os.path.commonpath([real_dest, target]) != real_dest:
                        raise ValueError(f"Path traversal direktorijuma u .tar: '{member.name}'.")
                    os.makedirs(target, exist_ok=True)
                elif member.isreg():
                    rel = member.name.replace("\\", "/").lstrip("/")
                    target = os.path.realpath(os.path.join(real_dest, rel))
                    if os.path.commonpath([real_dest, target]) != real_dest:
                        raise ValueError(f"Path traversal fajla u .tar: '{member.name}'.")
                    os.makedirs(os.path.dirname(target), exist_ok=True)
                    src_f = tf.extractfile(member)
                    if src_f is not None:
                        with src_f, open(target, "wb") as out_f:
                            while True:
                                chunk = src_f.read(65536)
                                if not chunk:
                                    break
                                actual_written += len(chunk)
                                if max_unpacked_bytes > 0 and actual_written > max_unpacked_bytes:
                                    raise ValueError(f"Arhiva premašuje maksimalnu raspakovanu veličinu ({max_unpacked_bytes} B).")
                                out_f.write(chunk)
        _assert_extracted_tree_safe(real_dest)
        return

    # 2. ZIP
    if lower.endswith(".zip"):
        with zipfile.ZipFile(ap, "r") as zf:
            infos = zf.infolist()
            if max_members > 0 and len(infos) > max_members:
                raise ValueError(f".zip arhiva sadrži previše članova ({len(infos)} > {max_members}).")
            total_unpacked = 0
            for info in infos:
                if not _is_safe_archive_relpath(info.filename):
                    raise ValueError(f"Nedozvoljena putanja unutar .zip arhive: '{info.filename}'.")
                # Unix symlink bit u external_attr
                if (info.external_attr >> 16) & 0o170000 == 0o120000:
                    raise ValueError(f"Nedozvoljen symlink unutar .zip arhive: '{info.filename}'.")
                if not info.is_dir():
                    total_unpacked += int(info.file_size or 0)
                    if max_unpacked_bytes > 0 and total_unpacked > max_unpacked_bytes:
                        raise ValueError(f".zip arhiva premašuje maksimalnu raspakovanu veličinu ({max_unpacked_bytes} B).")
            actual_written = 0
            for info in infos:
                if info.is_dir():
                    target = os.path.realpath(os.path.join(real_dest, info.filename))
                    if os.path.commonpath([real_dest, target]) == real_dest:
                        os.makedirs(target, exist_ok=True)
                    continue
                target = os.path.realpath(os.path.join(real_dest, info.filename))
                if os.path.commonpath([real_dest, target]) != real_dest:
                    raise ValueError(f"Path traversal u .zip: '{info.filename}'.")
                os.makedirs(os.path.dirname(target), exist_ok=True)
                with zf.open(info, "r") as src_f, open(target, "wb") as out_f:
                    while True:
                        chunk = src_f.read(65536)
                        if not chunk:
                            break
                        actual_written += len(chunk)
                        if max_unpacked_bytes > 0 and actual_written > max_unpacked_bytes:
                            raise ValueError(f".zip arhiva premašuje maksimalnu raspakovanu veličinu ({max_unpacked_bytes} B).")
                        out_f.write(chunk)
        _assert_extracted_tree_safe(real_dest)
        return

    # 3. CAB (bsdtar / cabextract)
    if lower.endswith(".cab"):
        bsdtar_bin = shutil.which("bsdtar")
        cabextract_bin = shutil.which("cabextract")
        if bsdtar_bin:
            p = subprocess.run([bsdtar_bin, "-xf", ap, "-C", dest_dir], capture_output=True, text=True, timeout=300, check=False)
            if p.returncode == 0:
                _assert_extracted_tree_safe(dest_dir)
                return
        if cabextract_bin:
            p = subprocess.run([cabextract_bin, "-q", "-d", dest_dir, ap], capture_output=True, text=True, timeout=300, check=False)
            if p.returncode == 0:
                _assert_extracted_tree_safe(dest_dir)
                return
        raise RuntimeError(f"Raspakivanje .cab arhive '{os.path.basename(ap)}' nije uspelo (proverite bsdtar/cabextract).")

    # 3b. MSI (Microsoft Surface Drivers & Firmware preko msiextract)
    if lower.endswith(".msi"):
        extract_msi_drivers(ap, real_dest)
        _assert_extracted_tree_safe(real_dest)
        return

    # 4. 7Z / SFX EXE (HP SoftPaq, Lenovo SCCM)
    extract_cmds: List[List[str]] = []
    for seven_bin in ("7z", "7zz", "7za"):
        found = shutil.which(seven_bin)
        if found:
            extract_cmds.append([found, "x", "-y", f"-o{dest_dir}", ap])
            break
    bsdtar_bin = shutil.which("bsdtar")
    if bsdtar_bin:
        extract_cmds.append([bsdtar_bin, "-xf", ap, "-C", dest_dir])
    inno_bin = shutil.which("innoextract")
    if inno_bin:
        extract_cmds.append([inno_bin, "-s", "-d", dest_dir, ap])

    last_err = ""
    for cmd in extract_cmds:
        # Očisti parcijalne fajlove iz neuspelog pokušaja
        for entry in os.listdir(dest_dir):
            ep = os.path.join(dest_dir, entry)
            if os.path.isdir(ep):
                shutil.rmtree(ep, ignore_errors=True)
            else:
                os.remove(ep)
        proc = subprocess.run(cmd, capture_output=True, text=True, timeout=600, check=False)
        has_files = any(os.scandir(dest_dir))
        if proc.returncode == 0 or (has_files and any(
            fn.lower().endswith(".inf")
            for _, _, files in os.walk(dest_dir)
            for fn in files
        )):
            _assert_extracted_tree_safe(dest_dir)
            return
        last_err = (proc.stderr or proc.stdout or "").strip()[:300]

    raise RuntimeError(f"Neuspešno raspakivanje arhive '{os.path.basename(ap)}': {last_err or 'nedostaje 7z/bsdtar/innoextract'}")


class _HashingFileWrapper:
    """Omotač oko binarnog fajla koji u jednom prolazu računa SHA-256 tokom pisanja na disk."""

    def __init__(self, raw_file):
        self._f = raw_file
        self._hasher = hashlib.sha256()

    def write(self, data):
        if data:
            self._hasher.update(data)
        return self._f.write(data)

    def flush(self):
        return self._f.flush()

    def tell(self):
        return self._f.tell()

    def seekable(self):
        return False

    def hexdigest(self) -> str:
        return self._hasher.hexdigest()


class DriverPoolManager:
    """
    Centralni Content-Addressable Driver Pool sa deduplikacijom na nivou .inf paketa,
    Model Manifestima i integrisanim OEM Enterprise katalozima.
    """

    def __init__(
        self,
        pool_dir: str,
        catalog_mgr: Optional[OemCatalogManager] = None,
        max_bundles_gb: Optional[float] = None,
        bundle_ttl_hours: Optional[float] = None,
        delete_ingested_archives: Optional[bool] = None,
        oem_catalog_stale_hours: Optional[float] = None,
        bundle_cache_min_hits: Optional[int] = None,
        bundle_cache_max_gb: Optional[float] = None,
        bundles_dir: Optional[str] = None,
    ):
        self.pool_dir = os.path.abspath(pool_dir)
        self.packages_dir = os.path.join(self.pool_dir, "packages")
        self.manifests_dir = os.path.join(self.pool_dir, "manifests")
        env_bundles_dir = os.environ.get("SERVIS_POOL_BUNDLES_DIR", "").strip()
        if bundles_dir:
            self.bundles_dir = os.path.abspath(bundles_dir)
        elif env_bundles_dir:
            self.bundles_dir = os.path.abspath(env_bundles_dir)
        else:
            self.bundles_dir = os.path.join(self.pool_dir, "bundles")
        self.catalogs_dir = os.path.join(self.pool_dir, "oem_catalogs")
        self.index_path = os.path.join(self.pool_dir, "pool_index.json")

        try:
            from servishub.config import (
                DEFAULT_BUNDLE_CACHE_MAX_GB,
                DEFAULT_BUNDLE_CACHE_MIN_HITS,
                DEFAULT_BUNDLE_TTL_HOURS,
                DEFAULT_MAX_BUNDLES_GB,
                DEFAULT_OEM_CATALOG_STALE_HOURS,
            )
        except Exception:
            DEFAULT_MAX_BUNDLES_GB = 16.0
            DEFAULT_BUNDLE_TTL_HOURS = 24.0
            DEFAULT_OEM_CATALOG_STALE_HOURS = 72.0
            DEFAULT_BUNDLE_CACHE_MIN_HITS = 2
            DEFAULT_BUNDLE_CACHE_MAX_GB = 4.0

        self.max_bundles_gb = float(max_bundles_gb) if max_bundles_gb is not None else float(DEFAULT_MAX_BUNDLES_GB)
        self.bundle_ttl_hours = float(bundle_ttl_hours) if bundle_ttl_hours is not None else float(DEFAULT_BUNDLE_TTL_HOURS)
        self.bundle_cache_min_hits = max(
            1, int(bundle_cache_min_hits) if bundle_cache_min_hits is not None else int(DEFAULT_BUNDLE_CACHE_MIN_HITS)
        )
        max_single_gb = float(bundle_cache_max_gb) if bundle_cache_max_gb is not None else float(DEFAULT_BUNDLE_CACHE_MAX_GB)
        self.bundle_cache_max_bytes = int(max(0.0, max_single_gb) * (1024 ** 3))
        self.delete_ingested_archives = bool(delete_ingested_archives) if delete_ingested_archives is not None else False
        self.oem_catalog_stale_hours = (
            float(oem_catalog_stale_hours)
            if oem_catalog_stale_hours is not None
            else float(DEFAULT_OEM_CATALOG_STALE_HOURS)
        )

        self.lock = threading.RLock()
        self._stats_cache_lock = threading.Lock()
        self._stats_cache: Optional[Dict[str, Any]] = None
        self._stats_version = 0
        self._index_cache: Optional[Dict[str, Any]] = None
        self._index_cache_sig: Optional[Tuple[int, int]] = None
        self.catalog_mgr = catalog_mgr or OemCatalogManager(catalogs_dir=self.catalogs_dir)
        self.jobs: Dict[str, Dict[str, Any]] = {}
        self._active_dedup_jobs: Dict[str, Dict[str, Any]] = {}

        os.makedirs(self.packages_dir, exist_ok=True)
        os.makedirs(self.manifests_dir, exist_ok=True)
        os.makedirs(self.bundles_dir, exist_ok=True)
        os.makedirs(self.catalogs_dir, exist_ok=True)

        self.cleanup_stale_temp_dirs()
        self._ensure_index_loaded()

    @staticmethod
    def cleanup_stale_temp_dirs(temp_root: Optional[str] = None) -> int:
        """
        Briše zaostale `wis-pool-extract-*` privremene direktorijume iz temp foldera
        strogo bez praćenja simboličkih linkova.
        """
        root = temp_root or tempfile.gettempdir()
        if not root or not os.path.isdir(root):
            return 0
        cleaned = 0
        try:
            with os.scandir(root) as it:
                for entry in it:
                    if not entry.name.startswith("wis-pool-extract-"):
                        continue
                    try:
                        if entry.is_symlink():
                            continue
                        if entry.is_dir(follow_symlinks=False):
                            shutil.rmtree(entry.path, ignore_errors=True)
                            cleaned += 1
                    except OSError:
                        continue
        except OSError:
            pass
        return cleaned

    def _invalidate_stats_cache(self) -> None:
        with self._stats_cache_lock:
            self._stats_version += 1
            self._stats_cache = None

    def invalidate_stats_cache(self) -> None:
        """Javna invalidacija keširane statistike Driver Pool-a."""
        self._invalidate_stats_cache()

    @staticmethod
    def _empty_index() -> Dict[str, Any]:
        return {
            "schema_version": 1,
            "updated_at": datetime.now().isoformat(),
            "packages": {},
            "hwid_winners": {},
            "rejected_packages": [],
            "ingested_archives": {},
            "model_hits": {},
            "stats": {
                "dedup_hits": 0,
                "dedup_saved_bytes": 0,
                "older_skipped_count": 0,
                "ingests_count": 0,
            },
        }

    def _ensure_index_loaded(self) -> Dict[str, Any]:
        with self.lock:
            if os.path.isfile(self.index_path):
                try:
                    st = os.stat(self.index_path)
                    sig = (int(st.st_mtime_ns), int(st.st_size))
                    if self._index_cache is not None and self._index_cache_sig == sig:
                        return self._index_cache
                    with open(self.index_path, "r", encoding="utf-8") as f:
                        data = json.load(f)
                    if isinstance(data, dict) and isinstance(data.get("packages"), dict):
                        data.setdefault("hwid_winners", {})
                        data.setdefault("rejected_packages", [])
                        data.setdefault("ingested_archives", {})
                        data.setdefault("model_hits", {})
                        st_map = data.setdefault("stats", {})
                        st_map.setdefault("dedup_hits", 0)
                        st_map.setdefault("dedup_saved_bytes", 0)
                        st_map.setdefault("older_skipped_count", 0)
                        st_map.setdefault("ingests_count", 0)
                        self._index_cache = data
                        self._index_cache_sig = sig
                        return data
                except Exception:
                    self._index_cache = None
                    self._index_cache_sig = None
            fresh = self._empty_index()
            self._save_index_locked(fresh)
            return fresh

    def _save_index_locked(self, index_data: Dict[str, Any]) -> None:
        index_data["updated_at"] = datetime.now().isoformat()
        tmp_path = f"{self.index_path}.{uuid.uuid4().hex[:6]}.tmp"
        with open(tmp_path, "w", encoding="utf-8") as f:
            json.dump(index_data, f, indent=2, ensure_ascii=False)
        os.replace(tmp_path, self.index_path)
        try:
            st = os.stat(self.index_path)
            self._index_cache = index_data
            self._index_cache_sig = (int(st.st_mtime_ns), int(st.st_size))
        except OSError:
            self._index_cache = None
            self._index_cache_sig = None
        self._invalidate_stats_cache()

    @staticmethod
    def _manifest_key(model: str) -> str:
        clean = sanitize_model_pack_name(model)
        return clean

    def _manifest_path(self, clean_model: str) -> str:
        safe = re.sub(r"[^a-zA-Z0-9._-]+", "_", clean_model).strip("._") or "unknown"
        return os.path.join(self.manifests_dir, f"{safe}.json")

    def _load_manifest_locked(self, clean_model: str) -> Optional[Dict[str, Any]]:
        path = self._manifest_path(clean_model)
        if not os.path.isfile(path):
            return None
        try:
            with open(path, "r", encoding="utf-8") as f:
                data = json.load(f)
            if isinstance(data, dict):
                return data
        except Exception:
            return None
        return None

    def _save_manifest_locked(self, clean_model: str, manifest: Dict[str, Any]) -> None:
        path = self._manifest_path(clean_model)
        tmp_path = f"{path}.{uuid.uuid4().hex[:6]}.tmp"
        with open(tmp_path, "w", encoding="utf-8") as f:
            json.dump(manifest, f, indent=2, ensure_ascii=False)
        os.replace(tmp_path, path)
        self._invalidate_stats_cache()

    @staticmethod
    def _is_package_better(cand: Dict[str, Any], existing: Dict[str, Any]) -> bool:
        """
        Određuje da li je `cand` paket strogo bolji od `existing` paketa za isti HWID:
        1. Potpisan paket (has_cat=True) uvek pobeđuje nepotpisani (has_cat=False).
        2. Potpisan (`has_cat=True`) `oem_pack` UVEK pobeđuje `ms_catalog` i `harvest` bez obzira na DriverDate;
           novija verzija/datum pobeđuje `oem_pack` samo ako je i kandidat takođe `oem_pack`.
        3. Strogo novija DriverDate/DriverVer verzija pobeđuje (is_catalog_driver_newer).
        4. Ako su verzije/datumi identični: viši prioritet izvora (oem_pack > ms_catalog > harvest) pobeđuje.
        """
        c_cat = bool(cand.get("has_cat"))
        e_cat = bool(existing.get("has_cat"))
        if c_cat != e_cat:
            return c_cat

        c_sources = set(cand.get("sources") or ([cand.get("source")] if cand.get("source") else []))
        e_sources = set(existing.get("sources") or ([existing.get("source")] if existing.get("source") else []))
        c_is_oem = "oem_pack" in c_sources
        e_is_oem = "oem_pack" in e_sources
        if c_cat and e_cat and (c_is_oem != e_is_oem):
            return c_is_oem

        c_ver = str(cand.get("driver_ver") or "")
        c_date = str(cand.get("driver_date") or "")
        e_ver = str(existing.get("driver_ver") or "")
        e_date = str(existing.get("driver_date") or "")

        if is_catalog_driver_newer(e_ver, e_date, c_ver, c_date):
            return True
        if is_catalog_driver_newer(c_ver, c_date, e_ver, e_date):
            return False

        # Identična verzija i datum: proveri prioritet izvora
        c_prio = max((_SOURCE_PRIORITY.get(s, 0) for s in c_sources), default=0)
        e_prio = max((_SOURCE_PRIORITY.get(s, 0) for s in e_sources), default=0)
        return c_prio > e_prio

    def _discover_package_units(self, extracted_dir: str) -> List[Dict[str, Any]]:
        """
        Pronalazi pojedinačne .inf drajver jedinice unutar raspakovanog stabla
        tako da relativne putanje unutar svakog paketa budu kanonske (nezavisne od wrapper foldera).
        """
        root_abs = os.path.abspath(extracted_dir)
        infs_by_dir: Dict[str, List[str]] = {}
        for dirpath, _, filenames in os.walk(root_abs, followlinks=False):
            inf_files = sorted(fn for fn in filenames if fn.lower().endswith(".inf"))
            if inf_files:
                infs_by_dir[dirpath] = inf_files

        units: List[Dict[str, Any]] = []
        inf_dirs_set = set(infs_by_dir.keys())

        for inf_dir in sorted(infs_by_dir.keys()):
            inf_names = infs_by_dir[inf_dir]
            parsed_infs: List[Tuple[str, Dict[str, Any]]] = []
            for fn in inf_names:
                meta = parse_inf_metadata(os.path.join(inf_dir, fn))
                if meta is not None:
                    parsed_infs.append((fn, meta))
            if not parsed_infs:
                continue

            # Pokupi sve fajlove u ovom inf_dir (uključujući podfoldere bez sopstvenog .inf-a)
            dir_all_files: List[Tuple[str, str]] = []
            for cur_dir, dirnames, filenames in os.walk(inf_dir, followlinks=False):
                # Ne ulazimo u poddirektorijume koji imaju sopstvene .inf fajlove
                dirnames[:] = [d for d in sorted(dirnames) if os.path.join(cur_dir, d) not in inf_dirs_set]
                for fn in sorted(filenames):
                    full_p = os.path.join(cur_dir, fn)
                    if os.path.islink(full_p):
                        continue
                    rel_p = os.path.relpath(full_p, inf_dir).replace("\\", "/")
                    dir_all_files.append((rel_p, full_p))

            # Odredi da li u folderu postoje zasebni mali .inf paketi bez deljenih binarca
            non_inf_cat_files = [
                rel for rel, _ in dir_all_files
                if not rel.lower().endswith((".inf", ".cat"))
                and not rel.lower().endswith(FORBIDDEN_DRIVER_EXTENSIONS)
            ]

            if len(parsed_infs) == 1 or len(non_inf_cat_files) > 0:
                # 1 paket za ceo list-folder (ignorišemo prnms009.inf i zabranjene skripte kasnije)
                primary_fn, primary_meta = parsed_infs[0]
                all_hwids: List[str] = []
                seen_h = set()
                best_ver = primary_meta["driver_ver"]
                best_date = primary_meta["driver_date"]
                best_inf_name = primary_fn
                best_inf_stem = primary_meta["inf_stem"]

                for fn, pm in parsed_infs:
                    if is_catalog_driver_newer(best_ver, best_date, pm["driver_ver"], pm["driver_date"]):
                        best_ver = pm["driver_ver"]
                        best_date = pm["driver_date"]
                        best_inf_name = fn
                        best_inf_stem = pm["inf_stem"]
                    for h in pm["hwids"]:
                        if h not in seen_h:
                            seen_h.add(h)
                            all_hwids.append(h)

                excluded_lower = _IGNORED_INBOX_INFS
                unit_files = [
                    (rel, fp) for rel, fp in dir_all_files
                    if os.path.basename(rel).lower() not in excluded_lower
                ]
                units.append({
                    "inf_name": best_inf_name,
                    "inf_stem": best_inf_stem,
                    "driver_class": primary_meta["driver_class"],
                    "provider": primary_meta["provider"],
                    "driver_ver": best_ver,
                    "driver_date": best_date,
                    "hwids": all_hwids,
                    "files": unit_files,
                })
            else:
                # Više samostalnih .inf fajlova u istom folderu bez deljenih binarca
                cat_lookup = {os.path.basename(rel).lower(): (rel, fp) for rel, fp in dir_all_files if rel.lower().endswith(".cat")}
                forbidden_files = [(rel, fp) for rel, fp in dir_all_files if rel.lower().endswith(FORBIDDEN_DRIVER_EXTENSIONS)]
                for fn, pm in parsed_infs:
                    inf_rel = fn
                    inf_fp = os.path.join(inf_dir, fn)
                    u_files: List[Tuple[str, str]] = [(inf_rel, inf_fp)]
                    for cat_decl in pm.get("catalog_files") or []:
                        c_pair = cat_lookup.get(os.path.basename(cat_decl).lower())
                        if c_pair and c_pair not in u_files:
                            u_files.append(c_pair)
                    stem_cat = f"{pm['inf_stem'].lower()}.cat"
                    if stem_cat in cat_lookup and cat_lookup[stem_cat] not in u_files:
                        u_files.append(cat_lookup[stem_cat])
                    u_files.extend(forbidden_files)
                    units.append({
                        "inf_name": fn,
                        "inf_stem": pm["inf_stem"],
                        "driver_class": pm["driver_class"],
                        "provider": pm["provider"],
                        "driver_ver": pm["driver_ver"],
                        "driver_date": pm["driver_date"],
                        "hwids": pm["hwids"],
                        "files": u_files,
                    })

        return units

    def ingest_directory(
        self,
        extracted_dir: str,
        source: str = "harvest",
        model: Optional[str] = None,
        manufacturer: str = "",
        oem_meta: Optional[Dict[str, Any]] = None,
    ) -> Dict[str, Any]:
        """
        Indeksira i deduplikuje sve validne .inf drajver pakete iz već raspakovanog direktorijuma.
        """
        src_norm = str(source or "harvest").strip().lower()
        if src_norm not in _SOURCE_PRIORITY:
            src_norm = "harvest"

        clean_model = None
        if model and str(model).strip():
            try:
                cand_m = self._manifest_key(str(model))
                if not is_generic_model_pack_name(cand_m):
                    clean_model = cand_m
            except ValueError:
                clean_model = None

        units = self._discover_package_units(extracted_dir)
        prepared_units: List[Tuple[Dict[str, Any], List[Tuple[str, str, str, int]]]] = []
        rejected_events: List[Dict[str, Any]] = []

        for unit in units:
            accepted_files: List[Tuple[str, str, str, int]] = []
            has_forbidden = False
            has_cat = False
            total_size = 0

            for rel_p, full_p in unit["files"]:
                lower_rel = rel_p.lower()
                if lower_rel.endswith(FORBIDDEN_DRIVER_EXTENSIONS):
                    has_forbidden = True
                    continue
                if lower_rel.endswith(".cat"):
                    has_cat = True
                try:
                    sz = os.path.getsize(full_p)
                    fsha = _file_sha256(full_p)
                except OSError:
                    continue
                accepted_files.append((rel_p, full_p, fsha, sz))
                total_size += sz

            # Nepotpisan paket sa zabranjenim skriptama se u potpunosti odbacuje
            if has_forbidden and not has_cat:
                rejected_events.append({
                    "inf_name": unit["inf_name"],
                    "reason": "unsigned_with_forbidden_script",
                    "source": src_norm,
                    "timestamp": datetime.now().isoformat(),
                })
                continue

            if not accepted_files:
                continue

            # Deterministički content hash celog paketa
            hasher = hashlib.sha256()
            for rel_p, _, fsha, sz in sorted(accepted_files, key=lambda x: x[0].lower()):
                hasher.update(f"{rel_p.lower()}|{fsha}|{sz}\n".encode("utf-8"))
            pkg_sha256 = hasher.hexdigest()

            cand_meta = {
                "pkg_id": pkg_sha256,
                "inf_name": unit["inf_name"],
                "inf_stem": unit["inf_stem"],
                "driver_class": unit["driver_class"],
                "provider": unit["provider"],
                "driver_ver": unit["driver_ver"],
                "driver_date": unit["driver_date"],
                "has_cat": has_cat,
                "hwids": list(unit["hwids"]),
                "size_bytes": total_size,
                "file_count": len(accepted_files),
                "sources": [src_norm],
            }
            prepared_units.append((cand_meta, accepted_files))

        added_count = 0
        dedup_hits = 0
        dedup_saved_bytes = 0
        older_skipped = 0
        rejected_count = len(rejected_events)
        upgraded_hwids = 0
        manifest_pkg_candidates: List[Tuple[str, Dict[str, Any]]] = []

        def _is_older_than_winners_locked(idx_map: Dict[str, Any], cand_meta_obj: Dict[str, Any]) -> bool:
            hw_list = cand_meta_obj.get("hwids") or []
            if not hw_list:
                return False
            for hw_item in hw_list:
                win_pid = idx_map["hwid_winners"].get(hw_item.lower())
                if not win_pid or win_pid not in idx_map["packages"]:
                    return False
                if self._is_package_better(cand_meta_obj, idx_map["packages"][win_pid]):
                    return False
            return True

        def _stage_package_files(pkg_id: str, files_spec: List[Tuple[str, str, str, int]]) -> str:
            tmp_dir = os.path.join(self.packages_dir, f".tmp-{pkg_id}-{uuid.uuid4().hex[:8]}")
            try:
                os.makedirs(tmp_dir, exist_ok=True)
                real_tmp = os.path.realpath(tmp_dir)
                for rel_p, src_fp, _, _ in files_spec:
                    dst_fp = os.path.realpath(os.path.join(tmp_dir, rel_p))
                    if os.path.commonpath([real_tmp, dst_fp]) != real_tmp:
                        raise ValueError(f"Nevalidna relativna putanja u paketu: {rel_p}")
                    os.makedirs(os.path.dirname(dst_fp), exist_ok=True)
                    shutil.copy2(src_fp, dst_fp)
                return tmp_dir
            except Exception:
                shutil.rmtree(tmp_dir, ignore_errors=True)
                raise

        # Faza A (kratki lock): odredi koje nove pakete uopšte treba kopirati na disk
        to_copy: List[Tuple[str, List[Tuple[str, str, str, int]]]] = []
        queued_pids = set()
        with self.lock:
            idx_ro = self._ensure_index_loaded()
            for cand_meta, accepted_files in prepared_units:
                pkg_sha256 = cand_meta["pkg_id"]
                if pkg_sha256 in queued_pids:
                    continue
                existing_pkg = idx_ro["packages"].get(pkg_sha256)
                pkg_target_dir = os.path.join(self.packages_dir, pkg_sha256)
                if existing_pkg and os.path.isdir(pkg_target_dir):
                    continue
                if _is_older_than_winners_locked(idx_ro, cand_meta):
                    continue
                queued_pids.add(pkg_sha256)
                to_copy.append((pkg_sha256, accepted_files))

        # Faza B (VAN self.lock): teško kopiranje fajlova u .tmp-* direktorijume
        staged_dirs: Dict[str, str] = {}
        try:
            for pkg_sha256, accepted_files in to_copy:
                staged_dirs[pkg_sha256] = _stage_package_files(pkg_sha256, accepted_files)

            # Faza C (kratki lock): atomični os.replace + upis metapodataka/manifesta/indeksa
            with self.lock:
                try:
                    idx = self._ensure_index_loaded()
                    if rejected_events:
                        idx["rejected_packages"].extend(rejected_events)
                        idx["rejected_packages"] = idx["rejected_packages"][-200:]

                    for cand_meta, accepted_files in prepared_units:
                        pkg_sha256 = cand_meta["pkg_id"]
                        total_size = cand_meta["size_bytes"]
                        existing_pkg = idx["packages"].get(pkg_sha256)
                        pkg_target_dir = os.path.join(self.packages_dir, pkg_sha256)

                        if existing_pkg and os.path.isdir(pkg_target_dir):
                            leftover = staged_dirs.pop(pkg_sha256, None)
                            if leftover:
                                shutil.rmtree(leftover, ignore_errors=True)
                            # Potpuni CAS pogodak -> 0 B kopiranja!
                            dedup_hits += 1
                            dedup_saved_bytes += total_size
                            if src_norm not in existing_pkg.get("sources", []):
                                existing_pkg.setdefault("sources", []).append(src_norm)
                            existing_pkg["last_seen"] = datetime.now().isoformat()
                            manifest_pkg_candidates.append((pkg_sha256, existing_pkg))
                            continue

                        # Proveri da li je paket strogo stariji od već postojećih pobednika za SVE svoje HWID-ove
                        hwids_list = cand_meta["hwids"]
                        if _is_older_than_winners_locked(idx, cand_meta):
                            leftover = staged_dirs.pop(pkg_sha256, None)
                            if leftover:
                                shutil.rmtree(leftover, ignore_errors=True)
                            older_skipped += 1
                            idx["stats"]["older_skipped_count"] = int(idx["stats"].get("older_skipped_count", 0)) + 1
                            # U manifest modela i dalje možemo uvezati pobednike iz pool-a za iste HWID-ove
                            for hw in hwids_list:
                                win_id = idx["hwid_winners"].get(hw.lower())
                                if win_id and win_id in idx["packages"]:
                                    manifest_pkg_candidates.append((win_id, idx["packages"][win_id]))
                            continue

                        tmp_pkg_dir = staged_dirs.pop(pkg_sha256, None)
                        if tmp_pkg_dir is None:
                            tmp_pkg_dir = _stage_package_files(pkg_sha256, accepted_files)

                        if os.path.isdir(pkg_target_dir):
                            shutil.rmtree(tmp_pkg_dir, ignore_errors=True)
                        else:
                            os.replace(tmp_pkg_dir, pkg_target_dir)

                        now_iso = datetime.now().isoformat()
                        stored_meta = dict(cand_meta)
                        stored_meta["first_seen"] = now_iso
                        stored_meta["last_seen"] = now_iso
                        idx["packages"][pkg_sha256] = stored_meta
                        added_count += 1

                        for hw in hwids_list:
                            hw_l = hw.lower()
                            cur_win_id = idx["hwid_winners"].get(hw_l)
                            if not cur_win_id or cur_win_id not in idx["packages"]:
                                idx["hwid_winners"][hw_l] = pkg_sha256
                                upgraded_hwids += 1
                            elif cur_win_id != pkg_sha256:
                                if self._is_package_better(stored_meta, idx["packages"][cur_win_id]):
                                    idx["hwid_winners"][hw_l] = pkg_sha256
                                    upgraded_hwids += 1

                        manifest_pkg_candidates.append((pkg_sha256, stored_meta))

                    idx["stats"]["dedup_hits"] = int(idx["stats"].get("dedup_hits", 0)) + dedup_hits
                    idx["stats"]["dedup_saved_bytes"] = int(idx["stats"].get("dedup_saved_bytes", 0)) + dedup_saved_bytes
                    idx["stats"]["ingests_count"] = int(idx["stats"].get("ingests_count", 0)) + 1

                    # Ažuriranje Model Manifesta (bez duplikata i bez degradacije verzije!)
                    if clean_model:
                        manifest = self._load_manifest_locked(clean_model) or {
                            "model": clean_model,
                            "raw_model": str(model or clean_model),
                            "manufacturer": manufacturer or detect_oem_vendor(manufacturer, str(model or "")),
                            "updated_at": datetime.now().isoformat(),
                            "sources": [],
                            "oem_pack": None,
                            "hwid_map": {},
                            "packages": [],
                            "total_bytes": 0,
                        }
                        if manufacturer and not manifest.get("manufacturer"):
                            manifest["manufacturer"] = manufacturer
                        if oem_meta:
                            manifest["oem_pack"] = {
                                "package_id": oem_meta.get("package_id"),
                                "package_name": oem_meta.get("package_name"),
                                "version": oem_meta.get("version"),
                                "release_date": oem_meta.get("release_date"),
                                "os": oem_meta.get("os"),
                                "download_url": oem_meta.get("download_url"),
                                "ingested_at": datetime.now().isoformat(),
                            }
                        m_sources = set(manifest.get("sources") or [])
                        m_sources.add(src_norm)
                        manifest["sources"] = sorted(m_sources)

                        hwid_map: Dict[str, str] = dict(manifest.get("hwid_map") or {})
                        no_hwid_pkgs: List[str] = []

                        # Uključi postojeće pakete bez HWID-ova ako i dalje postoje u pool-u
                        for old_pid in (manifest.get("packages") or []):
                            pmeta = idx["packages"].get(old_pid)
                            if pmeta and not pmeta.get("hwids"):
                                no_hwid_pkgs.append(old_pid)

                        for cand_pid, cand_pmeta in manifest_pkg_candidates:
                            p_hwids = cand_pmeta.get("hwids") or []
                            if not p_hwids:
                                if cand_pid not in no_hwid_pkgs:
                                    no_hwid_pkgs.append(cand_pid)
                                continue
                            for hw in p_hwids:
                                hw_l = hw.lower()
                                # Uvek biraj pobednika između postojećeg u manifestu, kandidata i globalnog hwid_winner-a
                                best_id = cand_pid
                                best_meta = cand_pmeta
                                cur_m_id = hwid_map.get(hw_l)
                                if cur_m_id and cur_m_id in idx["packages"]:
                                    if not self._is_package_better(best_meta, idx["packages"][cur_m_id]):
                                        best_id = cur_m_id
                                        best_meta = idx["packages"][cur_m_id]
                                glob_w_id = idx["hwid_winners"].get(hw_l)
                                if glob_w_id and glob_w_id in idx["packages"]:
                                    if self._is_package_better(idx["packages"][glob_w_id], best_meta):
                                        best_id = glob_w_id
                                        best_meta = idx["packages"][glob_w_id]
                                hwid_map[hw_l] = best_id

                        unique_pids: List[str] = []
                        seen_pids = set()
                        for pid in list(hwid_map.values()) + no_hwid_pkgs:
                            if pid in idx["packages"] and pid not in seen_pids:
                                seen_pids.add(pid)
                                unique_pids.append(pid)

                        manifest["hwid_map"] = hwid_map
                        manifest["packages"] = unique_pids
                        manifest["package_count"] = len(unique_pids)
                        manifest["total_bytes"] = sum(int(idx["packages"][pid].get("size_bytes") or 0) for pid in unique_pids)
                        manifest["updated_at"] = datetime.now().isoformat()
                        self._save_manifest_locked(clean_model, manifest)

                    self._save_index_locked(idx)
                except Exception:
                    self._index_cache = None
                    self._index_cache_sig = None
                    raise
        finally:
            for leftover_dir in list(staged_dirs.values()):
                shutil.rmtree(leftover_dir, ignore_errors=True)

        return {
            "status": "ok",
            "source": src_norm,
            "model": clean_model,
            "units_discovered": len(units),
            "packages_added": added_count,
            "dedup_hits": dedup_hits,
            "dedup_saved_bytes": dedup_saved_bytes,
            "older_skipped": older_skipped,
            "rejected_count": rejected_count,
            "upgraded_hwids": upgraded_hwids,
        }

    def ingest_archive(
        self,
        archive_path: str,
        source: str = "harvest",
        model: Optional[str] = None,
        manufacturer: str = "",
        oem_meta: Optional[Dict[str, Any]] = None,
    ) -> Dict[str, Any]:
        """Bezbedno raspakuje arhivu u temp direktorijum, ingestuje je u CAS Pool i briše temp."""
        if not os.path.isfile(archive_path):
            raise FileNotFoundError(f"Arhiva ne postoji: {archive_path}")
        tmp_dir = tempfile.mkdtemp(prefix="wis-pool-extract-")
        try:
            _extract_archive_safely(archive_path, tmp_dir)
            return self.ingest_directory(
                tmp_dir,
                source=source,
                model=model,
                manufacturer=manufacturer,
                oem_meta=oem_meta,
            )
        finally:
            shutil.rmtree(tmp_dir, ignore_errors=True)

    def refresh_stale_oem_catalogs(self, max_age_hours: Optional[float] = None) -> Dict[str, Any]:
        """
        Proverava status OEM kataloga (TIER1_OEM_VENDORS) i osvežava one koji su ne-sinhronizovani ili stariji od
        `max_age_hours` (podrazumevano 72h). Greške pojedinačnih vendora ne prekidaju ostale.
        """
        thresh_h = float(max_age_hours) if max_age_hours is not None else self.oem_catalog_stale_hours
        status_map = self.catalog_mgr.get_catalog_status() or {}
        refreshed: Dict[str, Any] = {}
        for vendor in TIER1_OEM_VENDORS:
            if status_map and vendor not in status_map:
                continue
            info = status_map.get(vendor) or {}
            synced = bool(info.get("synced"))
            age_h = info.get("age_hours")
            is_stale = (not synced) or bool(info.get("stale")) or (age_h is not None and float(age_h) >= thresh_h)
            if not is_stale:
                refreshed[vendor] = {"status": "fresh", "age_hours": age_h}
                continue
            try:
                res = self.catalog_mgr.sync_catalog(vendor, force=True)
                refreshed[vendor] = {"status": "refreshed", "result": res}
            except Exception as ex:
                refreshed[vendor] = {"status": "error", "error": str(ex)}
        self._invalidate_stats_cache()
        return {"status": "ok", "threshold_hours": thresh_h, "vendors": refreshed}

    def ingest_oem_pack(
        self,
        manufacturer: str,
        model: str,
        os_ver: str = "win11",
        system_id: str = "",
        system_sku: str = "",
        force_sync: bool = False,
    ) -> Dict[str, Any]:
        """
        Preuzima zvanični OEM Driver Pack sa kataloga (Tier 1 + Tier 2 drop-in), verifikuje hash,
        raspakuje i ingestuje u Driver Pool uz deduplikaciju i ažuriranje manifesta modela.
        """
        vendor = detect_oem_vendor(manufacturer, model)
        if not vendor:
            return {"status": "unsupported_vendor", "manufacturer": manufacturer, "model": model}

        cat_info = (self.catalog_mgr.get_catalog_status() or {}).get(vendor, {})
        age_h = cat_info.get("age_hours")
        need_sync = (
            force_sync
            or cat_info.get("stale", True)
            or (age_h is not None and float(age_h) >= self.oem_catalog_stale_hours)
        )
        if need_sync:
            try:
                self.catalog_mgr.sync_catalog(vendor, force=force_sync or (age_h is not None and float(age_h) >= self.oem_catalog_stale_hours))
            except Exception as sync_ex:
                # Nastavi ako postoji bar lokalni keš
                if not (self.catalog_mgr.get_catalog_status() or {}).get(vendor, {}).get("synced"):
                    return {"status": "catalog_error", "vendor": vendor, "error": str(sync_ex)}

        entry = self.catalog_mgr.find_driver_pack(
            manufacturer=vendor,
            model=model,
            os_ver=os_ver,
            system_id=system_id,
            system_sku=system_sku,
        )
        if not entry:
            return {"status": "not_found", "vendor": vendor, "model": model}

        tmp_dl = tempfile.mkdtemp(prefix="wis-oem-dl-")
        try:
            pkg_file = self.catalog_mgr.download_driver_pack(entry, tmp_dl)
            res = self.ingest_archive(
                pkg_file,
                source="oem_pack",
                model=model,
                manufacturer=vendor,
                oem_meta=entry,
            )
            res["oem_entry"] = {
                "package_id": entry.get("package_id"),
                "package_name": entry.get("package_name"),
                "version": entry.get("version"),
                "release_date": entry.get("release_date"),
                "os": entry.get("os"),
            }
            return res
        finally:
            shutil.rmtree(tmp_dl, ignore_errors=True)

    def _find_manifest_for_model(self, model: Optional[str]) -> Optional[Dict[str, Any]]:
        if not model or not str(model).strip():
            return None
        try:
            clean = self._manifest_key(str(model))
        except ValueError:
            return None
        if is_generic_model_pack_name(clean):
            return None
        with self.lock:
            direct = self._load_manifest_locked(clean)
            if direct:
                return direct
            # Probaj bez prefiksa proizvođača
            no_pref = re.sub(
                r"^(?:lenovo|hp|hewlett[-_ ]packard|dell(?:_inc\.?)?)[_ ]+",
                "",
                clean,
                flags=re.IGNORECASE,
            ).strip("._")
            if no_pref and no_pref != clean:
                cand = self._load_manifest_locked(no_pref)
                if cand:
                    return cand
        return None

    def has_model_manifest(self, model: Optional[str]) -> bool:
        m = self._find_manifest_for_model(model)
        return bool(m and (m.get("packages") or []))

    def has_oem_pack_in_manifest(self, model: Optional[str]) -> bool:
        with self.lock:
            m = self._find_manifest_for_model(model)
            if not m:
                return False
            if m.get("oem_pack") or ("oem_pack" in (m.get("sources") or [])):
                return True
            idx = self._ensure_index_loaded()
            for pid in (m.get("packages") or []):
                pmeta = idx.get("packages", {}).get(pid, {})
                if "oem_pack" in (pmeta.get("sources") or []):
                    return True
            return False

    def get_package_snapshot(self, pkg_id: str) -> Optional[Tuple[Dict[str, Any], str]]:
        clean_pid = os.path.basename(str(pkg_id or "").strip())
        if not clean_pid or clean_pid in (".", ".."):
            return None
        with self.lock:
            idx = self._ensure_index_loaded()
            pmeta = idx.get("packages", {}).get(clean_pid)
            if not pmeta:
                return None
            pdir = os.path.join(self.packages_dir, clean_pid)
            if not os.path.isdir(pdir) or os.path.islink(pdir):
                return None
            return (dict(pmeta), pdir)

    def resolve_device_hwids(self, hwids: Sequence[str], model: str = "") -> Dict[str, Any]:
        """Vraća lokalno pokrivene HWID-ove iz Pool-a (oem_pack / manifest winner / global hwid_winners)."""
        clean_hwids = [str(h).strip() for h in (hwids or []) if str(h).strip()]
        matches: List[Dict[str, Any]] = []
        unmatched: List[str] = []
        with self.lock:
            idx = self._ensure_index_loaded()
            manifest = self._find_manifest_for_model(model) if model else None
            manifest_hmap = (manifest or {}).get("hwid_map") or {}
            for raw_h in clean_hwids:
                cands = generate_hwid_candidates(raw_h)
                chosen = None
                matched_cand = None
                for cand in cands:
                    cand_l = cand.lower()
                    m_pid = manifest_hmap.get(cand_l)
                    g_pid = idx["hwid_winners"].get(cand_l)
                    cand_pid = None
                    if m_pid and g_pid and m_pid in idx["packages"] and g_pid in idx["packages"]:
                        cand_pid = g_pid if self._is_package_better(idx["packages"][g_pid], idx["packages"][m_pid]) else m_pid
                    elif g_pid and g_pid in idx["packages"]:
                        cand_pid = g_pid
                    elif m_pid and m_pid in idx["packages"]:
                        cand_pid = m_pid
                    if cand_pid and os.path.isdir(os.path.join(self.packages_dir, cand_pid)):
                        chosen = cand_pid
                        matched_cand = cand
                        break
                if chosen:
                    pmeta = idx["packages"].get(chosen, {})
                    matches.append({
                        "hwid": raw_h,
                        "matched_hwid": matched_cand or raw_h,
                        "pkg_id": chosen,
                        "inf_name": pmeta.get("inf_name"),
                        "driver_ver": pmeta.get("driver_ver"),
                        "driver_date": pmeta.get("driver_date"),
                        "sources": list(pmeta.get("sources") or []),
                        "has_cat": bool(pmeta.get("has_cat", True)),
                        "size_bytes": int(pmeta.get("size_bytes") or 0),
                    })
                else:
                    unmatched.append(raw_h)
        return {
            "matches": matches,
            "unmatched": unmatched,
            "has_oem_pack": bool(self.has_oem_pack_in_manifest(model)) if model else False,
        }

    def build_bundle_tar(
        self,
        model: str = "",
        missing_hwids: Optional[Sequence[str]] = None,
        all_hwids: Optional[Sequence[str]] = None,
        allow_stream_plan: bool = False,
    ) -> Dict[str, Any]:
        """
        Dinamički sastavlja (ili vraća iz bundles/ keša) minimalni, 100% deduplikovani `.tar` paket
        za traženi model i/ili konkretne HWID-ove uređaja.
        Kada je allow_stream_plan=True, retki (hits < min_hits) ili preveliki (> max_gb) modeli
        vraćaju plan za on-the-fly streaming bez upisa u bundles/.
        """
        clean_missing = [str(h).strip() for h in (missing_hwids or []) if str(h).strip()]
        clean_all = [str(h).strip() for h in (all_hwids or []) if str(h).strip()]

        selected_pids: List[str] = []
        seen_pids = set()
        matched_hwids: List[str] = []
        unmatched_hwids: List[str] = []
        pkg_snapshots: Dict[str, Tuple[Dict[str, Any], str]] = {}
        manifest_model: Optional[str] = None
        model_hits = 1

        with self.lock:
            idx = self._ensure_index_loaded()
            manifest = self._find_manifest_for_model(model)
            manifest_model = (manifest or {}).get("model")
            manifest_hmap = (manifest or {}).get("hwid_map") or {}
            manifest_has_packages = bool(manifest and (manifest.get("packages") or []))

            def _pick_for_hwid(raw_h: str, allow_global_generic: bool = True) -> Optional[str]:
                cands = generate_hwid_candidates(raw_h)
                for cand in cands:
                    cand_l = cand.lower()
                    m_pid = manifest_hmap.get(cand_l)
                    allow_g = allow_global_generic or bool(_STRICT_SUBSYS_RE.search(cand))
                    g_pid = idx["hwid_winners"].get(cand_l) if allow_g else None
                    chosen = None
                    if m_pid and g_pid and m_pid in idx["packages"] and g_pid in idx["packages"]:
                        chosen = g_pid if self._is_package_better(idx["packages"][g_pid], idx["packages"][m_pid]) else m_pid
                    elif g_pid and g_pid in idx["packages"]:
                        chosen = g_pid
                    elif m_pid and m_pid in idx["packages"]:
                        chosen = m_pid
                    if chosen and os.path.isdir(os.path.join(self.packages_dir, chosen)):
                        return chosen
                return None

            if clean_missing:
                # Ciljani mod: samo paketi koji rešavaju prijavljene problematične HWID-ove
                for raw_h in clean_missing:
                    pid = _pick_for_hwid(raw_h, allow_global_generic=True)
                    if pid:
                        matched_hwids.append(raw_h)
                        if pid not in seen_pids:
                            seen_pids.add(pid)
                            selected_pids.append(pid)
                    else:
                        unmatched_hwids.append(raw_h)
            else:
                # Puni mod modela: ceo manifest modela + dopuna iz Pool-a za poznate all_hwids uređaja.
                # IMP-89: kad već postoji manifest sa paketima za model, zabrani generički globalni fallback
                # bez SUBSYS-a u clean_all da se ne bi ubacivao tuđi driver bloat.
                if manifest:
                    for pid in (manifest.get("packages") or []):
                        if pid in idx["packages"] and pid not in seen_pids and os.path.isdir(os.path.join(self.packages_dir, pid)):
                            seen_pids.add(pid)
                            selected_pids.append(pid)
                for raw_h in clean_all:
                    pid = _pick_for_hwid(raw_h, allow_global_generic=not manifest_has_packages)
                    if pid:
                        matched_hwids.append(raw_h)
                        if pid not in seen_pids:
                            seen_pids.add(pid)
                            selected_pids.append(pid)
                    else:
                        unmatched_hwids.append(raw_h)

            for pid in selected_pids:
                pmeta = dict(idx["packages"].get(pid, {}))
                pkg_snapshots[pid] = (pmeta, os.path.join(self.packages_dir, pid))

            if selected_pids and allow_stream_plan:
                raw_hit_key = manifest_model or model
                if raw_hit_key and str(raw_hit_key).strip():
                    try:
                        hit_key = sanitize_model_pack_name(str(raw_hit_key)).lower()
                    except ValueError:
                        hit_key = f"__hash_{hashlib.sha256('|'.join(sorted(selected_pids)).encode('utf-8')).hexdigest()[:12]}"
                else:
                    hit_key = f"__hash_{hashlib.sha256('|'.join(sorted(selected_pids)).encode('utf-8')).hexdigest()[:12]}"
                hits_map = idx.setdefault("model_hits", {})
                model_hits = int(hits_map.get(hit_key, 0)) + 1
                hits_map[hit_key] = model_hits
                self._save_index_locked(idx)

        if not selected_pids:
            return {
                "available": False,
                "model": manifest_model or model,
                "package_count": 0,
                "matched_hwids": matched_hwids,
                "missing_unmatched_hwids": unmatched_hwids,
                "bundle_path": None,
                "bundle_filename": None,
                "size_bytes": 0,
                "size_mb": 0.0,
                "sha256": None,
                "sources": [],
                "packages": [],
            }

        sorted_pids = sorted(selected_pids)
        bundle_hash = hashlib.sha256("|".join(sorted_pids).encode("utf-8")).hexdigest()[:24]
        safe_model_lbl = "pool"
        if model and str(model).strip():
            try:
                safe_model_lbl = sanitize_model_pack_name(model)
            except ValueError:
                safe_model_lbl = "pool"
        bundle_filename = f"{safe_model_lbl}_{bundle_hash[:10]}.tar"
        bundle_path = os.path.join(self.bundles_dir, f"{bundle_hash}.tar")
        meta_path = os.path.join(self.bundles_dir, f"{bundle_hash}.json")

        bundle_sha = None
        cache_hit = False
        if (
            os.path.isfile(bundle_path)
            and not os.path.islink(bundle_path)
            and os.path.isfile(meta_path)
            and not os.path.islink(meta_path)
        ):
            try:
                with open(meta_path, "r", encoding="utf-8") as mf:
                    bmeta = json.load(mf)
                if bmeta.get("sha256") and os.path.getsize(bundle_path) > 0:
                    bundle_sha = bmeta["sha256"]
                    cache_hit = True
                    try:
                        os.utime(bundle_path, None)
                        os.utime(meta_path, None)
                    except OSError:
                        pass
            except Exception:
                bundle_sha = None
                cache_hit = False

        estimated_bytes = sum(int(pkg_snapshots.get(pid, ({}, ""))[0].get("size_bytes") or 0) for pid in sorted_pids)
        max_cache_bytes = int(self.bundle_cache_max_bytes)
        should_cache = (model_hits >= self.bundle_cache_min_hits) and (estimated_bytes <= max_cache_bytes)

        if not bundle_sha and allow_stream_plan and not should_cache:
            pkg_details = []
            pkg_entries = []
            sources_set = set()
            for pid in sorted_pids:
                pm, pdir = pkg_snapshots.get(pid, ({}, os.path.join(self.packages_dir, pid)))
                for s in (pm.get("sources") or []):
                    sources_set.add(s)
                pkg_details.append({
                    "pkg_id": pid,
                    "inf_name": pm.get("inf_name"),
                    "driver_ver": pm.get("driver_ver"),
                    "driver_date": pm.get("driver_date"),
                    "driver_class": pm.get("driver_class"),
                    "sources": pm.get("sources", []),
                    "size_bytes": pm.get("size_bytes", 0),
                })
                pkg_entries.append((pid, pm, pdir))
            return {
                "available": True,
                "cache_hit": False,
                "stream_only": True,
                "model_hits": model_hits,
                "estimated_bytes": estimated_bytes,
                "model": manifest_model or safe_model_lbl,
                "package_count": len(sorted_pids),
                "matched_hwids": matched_hwids,
                "missing_unmatched_hwids": unmatched_hwids,
                "bundle_path": None,
                "bundle_filename": bundle_filename,
                "size_bytes": estimated_bytes,
                "size_mb": round(estimated_bytes / (1024 * 1024), 2),
                "sha256": None,
                "sources": sorted(sources_set),
                "packages": pkg_details,
                "package_entries": pkg_entries,
            }

        if not bundle_sha:
            self.enforce_bundles_quota()
            os.makedirs(self.bundles_dir, exist_ok=True)
            tmp_tar = f"{bundle_path}.{uuid.uuid4().hex[:6]}.tmp"
            tmp_meta = f"{meta_path}.{uuid.uuid4().hex[:6]}.tmp"
            try:
                with open(tmp_tar, "wb") as raw_out:
                    hash_writer = _HashingFileWrapper(raw_out)
                    with tarfile.open(fileobj=hash_writer, mode="w") as tf:
                        for pid in sorted_pids:
                            pmeta, pdir = pkg_snapshots.get(pid, ({}, os.path.join(self.packages_dir, pid)))
                            if not os.path.isdir(pdir) or os.path.islink(pdir):
                                continue
                            stem = re.sub(r"[^a-zA-Z0-9._-]+", "_", str(pmeta.get("inf_stem") or "drv")).strip("_") or "drv"
                            folder_prefix = f"{stem}_{pid[:8]}"
                            for rdir, _, files in os.walk(pdir, followlinks=False):
                                for fn in sorted(files):
                                    if fn.lower().endswith(FORBIDDEN_DRIVER_EXTENSIONS):
                                        continue
                                    full_f = os.path.join(rdir, fn)
                                    if os.path.islink(full_f):
                                        continue
                                    rel = os.path.relpath(full_f, pdir).replace("\\", "/")
                                    tf.add(full_f, arcname=f"{folder_prefix}/{rel}", recursive=False)
                    hash_writer.flush()
                    bundle_sha = hash_writer.hexdigest()
                os.replace(tmp_tar, bundle_path)
                with open(tmp_meta, "w", encoding="utf-8") as mf:
                    json.dump({"sha256": bundle_sha, "packages": sorted_pids, "created_at": datetime.now().isoformat()}, mf)
                os.replace(tmp_meta, meta_path)
                self.enforce_bundles_quota()
            except Exception:
                for leftover in (tmp_tar, tmp_meta):
                    if os.path.exists(leftover) and not os.path.islink(leftover):
                        try:
                            os.remove(leftover)
                        except OSError:
                            pass
                raise

        sz_bytes = os.path.getsize(bundle_path)
        pkg_details = []
        sources_set = set()
        for pid in sorted_pids:
            pm, _ = pkg_snapshots.get(pid, ({}, ""))
            for s in (pm.get("sources") or []):
                sources_set.add(s)
            pkg_details.append({
                "pkg_id": pid,
                "inf_name": pm.get("inf_name"),
                "driver_ver": pm.get("driver_ver"),
                "driver_date": pm.get("driver_date"),
                "driver_class": pm.get("driver_class"),
                "sources": pm.get("sources", []),
                "size_bytes": pm.get("size_bytes", 0),
            })

        return {
            "available": True,
            "cache_hit": cache_hit,
            "model": manifest_model or safe_model_lbl,
            "package_count": len(sorted_pids),
            "matched_hwids": matched_hwids,
            "missing_unmatched_hwids": unmatched_hwids,
            "bundle_path": bundle_path,
            "bundle_filename": bundle_filename,
            "size_bytes": sz_bytes,
            "size_mb": round(sz_bytes / (1024 * 1024), 2),
            "sha256": bundle_sha,
            "sources": sorted(sources_set),
            "packages": pkg_details,
        }

    def migrate_legacy_stores(
        self,
        model_packs_dir: Optional[str] = None,
        drivers_cache_dir: Optional[str] = None,
    ) -> Dict[str, Any]:
        """
        Indeksira i deduplikuje postojeće `<Model>.tar` arhive i `drivers_cache/*.cab` fajlove
        u jedinstveni Driver Pool bez brisanja izvornih fajlova.
        """
        migrated_tars = 0
        migrated_cabs = 0
        errors: List[str] = []
        totals = {"packages_added": 0, "dedup_hits": 0, "dedup_saved_bytes": 0}

        if model_packs_dir and os.path.isdir(model_packs_dir):
            for fn in sorted(os.listdir(model_packs_dir)):
                if not fn.lower().endswith(".tar"):
                    continue
                fp = os.path.join(model_packs_dir, fn)
                if not os.path.isfile(fp) or os.path.getsize(fp) < 512:
                    continue
                model_stem = os.path.splitext(fn)[0]
                try:
                    res = self.ingest_archive(fp, source="harvest", model=model_stem)
                    migrated_tars += 1
                    totals["packages_added"] += int(res.get("packages_added") or 0)
                    totals["dedup_hits"] += int(res.get("dedup_hits") or 0)
                    totals["dedup_saved_bytes"] += int(res.get("dedup_saved_bytes") or 0)
                except Exception as ex:
                    errors.append(f"{fn}: {ex}")

        if drivers_cache_dir and os.path.isdir(drivers_cache_dir):
            seen_cab_sha = set()
            for fn in sorted(os.listdir(drivers_cache_dir)):
                if not fn.lower().endswith(".cab"):
                    continue
                fp = os.path.join(drivers_cache_dir, fn)
                if not os.path.isfile(fp) or os.path.getsize(fp) <= 0:
                    continue
                try:
                    fsha = _file_sha256(fp)
                    if fsha in seen_cab_sha:
                        continue
                    seen_cab_sha.add(fsha)
                    res = self.ingest_archive(fp, source="ms_catalog")
                    migrated_cabs += 1
                    totals["packages_added"] += int(res.get("packages_added") or 0)
                    totals["dedup_hits"] += int(res.get("dedup_hits") or 0)
                    totals["dedup_saved_bytes"] += int(res.get("dedup_saved_bytes") or 0)
                except Exception as ex:
                    errors.append(f"{fn}: {ex}")

        return {
            "status": "ok",
            "migrated_tars": migrated_tars,
            "migrated_cabs": migrated_cabs,
            **totals,
            "errors": errors[:25],
        }

    def enforce_bundles_quota(
        self,
        max_bundles_gb: Optional[float] = None,
        bundle_ttl_hours: Optional[float] = None,
    ) -> Dict[str, Any]:
        """
        Primenjuje LRU + TTL evikciju nad `bundles_dir` (IMP-63):
        - Nikada ne prati symlinkove niti izlazi van `bundles_dir` (`commonpath`).
        - Nikada ne briše aktivne `.tmp` fajlove.
        """
        max_gb = float(max_bundles_gb) if max_bundles_gb is not None else self.max_bundles_gb
        ttl_h = float(bundle_ttl_hours) if bundle_ttl_hours is not None else self.bundle_ttl_hours
        max_bytes = int(max_gb * (1024 ** 3)) if max_gb > 0 else None
        ttl_sec = float(ttl_h * 3600.0) if ttl_h > 0 else None
        try:
            from servishub.config import enforce_directory_lru_quota
            return enforce_directory_lru_quota(
                self.bundles_dir,
                max_bytes=max_bytes,
                ttl_seconds=ttl_sec,
                paired_extensions={".tar": (".json",)},
                recursive=False,
            )
        except Exception:
            return {"removed_files": 0, "freed_bytes": 0, "remaining_bytes": 0}

    def delta_check(
        self,
        model: str,
        packages: Optional[List[Dict[str, Any]]],
        os_build: str = "",
    ) -> Dict[str, Any]:
        """
        Brza provera koji su klijentski INF paketi već prisutni u Driver Pool-u (sa jednakom
        ili novijom verzijom), a koji nedostaju i zahtevaju delta harvest upload (2A).
        """
        pkg_list = packages if isinstance(packages, list) else []
        clean_model = ""
        if model and str(model).strip():
            try:
                clean_model = sanitize_model_pack_name(model)
            except ValueError:
                clean_model = str(model).strip()

        already_in_pool = 0
        missing_infs: List[str] = []

        with self.lock:
            idx = self._ensure_index_loaded()
            pkgs_map = idx.get("packages", {})
            hwid_winners = idx.get("hwid_winners", {})
            manifest = self._find_manifest_for_model(model)
            manifest_model = (manifest or {}).get("model")
            manifest_hmap = (manifest or {}).get("hwid_map") or {}

            for item in pkg_list:
                if not isinstance(item, dict):
                    continue
                inf_name = str(item.get("inf_name") or item.get("inf") or "").strip()
                if not inf_name:
                    continue
                cand_ver = str(item.get("driver_ver") or item.get("version") or "").strip()
                cand_date = str(item.get("driver_date") or "").strip()
                cand_sha = str(item.get("sha256") or "").strip().lower()
                raw_hwids = item.get("hwids") or []
                if isinstance(raw_hwids, str):
                    raw_hwids = [raw_hwids]

                in_pool = False
                # 1. Direktno poklapanje po SHA-256 (pkg_id ili inf_sha256)
                if cand_sha:
                    if cand_sha in pkgs_map and os.path.isdir(os.path.join(self.packages_dir, cand_sha)):
                        in_pool = True
                    else:
                        for pid, pmeta in pkgs_map.items():
                            if str(pmeta.get("inf_sha256") or "").lower() == cand_sha and os.path.isdir(os.path.join(self.packages_dir, pid)):
                                in_pool = True
                                break

                # 2. Provera po HWID + verziji u manifestu modela i globalnim hwid_winners
                if not in_pool and raw_hwids:
                    cand_meta = {
                        "driver_ver": cand_ver,
                        "driver_date": cand_date,
                        "source": "harvest",
                    }
                    all_hwids_covered = True
                    any_matched = False
                    for raw_h in raw_hwids:
                        h_str = str(raw_h or "").strip()
                        if not h_str:
                            continue
                        matched_pid = None
                        for cand_h in generate_hwid_candidates(h_str):
                            cl = cand_h.lower()
                            m_pid = manifest_hmap.get(cl)
                            g_pid = hwid_winners.get(cl)
                            if g_pid and g_pid in pkgs_map and os.path.isdir(os.path.join(self.packages_dir, g_pid)):
                                matched_pid = g_pid
                                break
                            if m_pid and m_pid in pkgs_map and os.path.isdir(os.path.join(self.packages_dir, m_pid)):
                                matched_pid = m_pid
                                break
                        if not matched_pid:
                            all_hwids_covered = False
                            break
                        any_matched = True
                        existing_meta = pkgs_map.get(matched_pid, {})
                        if self._is_package_better(cand_meta, existing_meta):
                            all_hwids_covered = False
                            break
                    if any_matched and all_hwids_covered:
                        in_pool = True

                if in_pool:
                    already_in_pool += 1
                else:
                    missing_infs.append(inf_name)

        return {
            "status": "ok",
            "model": manifest_model or clean_model or model,
            "os_build": str(os_build or "").strip(),
            "total_checked": len(pkg_list),
            "already_in_pool": already_in_pool,
            "missing_infs": missing_infs,
            "harvest_needed": len(missing_infs) > 0,
        }

    def prune_unused_packages(self, max_bundle_age_sec: int = 86400) -> Dict[str, Any]:
        """
        Briše nekorišćene (stare/orphan) pakete koje nijedan manifest modela niti trenutni
        `hwid_winners` indeks ne referenciraju, kao i zastarele keširane `.tar` bundle fajlove.
        """
        removed_packages = 0
        freed_bytes = 0
        removed_bundles = 0
        now = time.time()

        with self.lock:
            idx = self._ensure_index_loaded()
            referenced = set(idx.get("hwid_winners", {}).values())
            for manifest in self._iter_manifests_locked():
                for pid in (manifest.get("packages") or []):
                    referenced.add(pid)

            for pid in list(idx.get("packages", {}).keys()):
                if pid not in referenced:
                    pmeta = idx["packages"].pop(pid, {})
                    freed_bytes += int(pmeta.get("size_bytes") or 0)
                    pdir = os.path.join(self.packages_dir, pid)
                    if os.path.isdir(pdir):
                        shutil.rmtree(pdir, ignore_errors=True)
                    removed_packages += 1

            # Očisti i eventualne siročiće u packages/ folderu kojih nema u indeksu
            if os.path.isdir(self.packages_dir) and not os.path.islink(self.packages_dir):
                real_pkgs = os.path.realpath(self.packages_dir)
                for entry in os.listdir(self.packages_dir):
                    ep = os.path.join(self.packages_dir, entry)
                    if os.path.islink(ep):
                        continue
                    if os.path.commonpath([real_pkgs, os.path.realpath(ep)]) != real_pkgs:
                        continue
                    if os.path.isdir(ep) and entry not in idx["packages"]:
                        shutil.rmtree(ep, ignore_errors=True)

            # Očisti zastarele bundle-ove (strogo bez symlinkova, bez .tmp fajlova i bez izlaska van bundles_dir)
            if os.path.isdir(self.bundles_dir) and not os.path.islink(self.bundles_dir):
                ttl_s = float(max_bundle_age_sec) if max_bundle_age_sec is not None else (self.bundle_ttl_hours * 3600.0)
                if max_bundle_age_sec == 0:
                    ttl_s = 0.0001
                try:
                    from servishub.config import enforce_directory_lru_quota
                    max_b = int(self.max_bundles_gb * (1024 ** 3)) if self.max_bundles_gb > 0 else None
                    q_res = enforce_directory_lru_quota(
                        self.bundles_dir,
                        max_bytes=max_b,
                        ttl_seconds=ttl_s,
                        paired_extensions={".tar": (".json",)},
                        recursive=False,
                        now=now,
                    )
                    removed_bundles += int(q_res.get("removed_files") or 0)
                    freed_bytes += int(q_res.get("freed_bytes") or 0)
                except Exception:
                    pass

            self._save_index_locked(idx)

        return {
            "status": "ok",
            "removed_packages": removed_packages,
            "removed_bundles": removed_bundles,
            "freed_bytes": freed_bytes,
            "freed_mb": round(freed_bytes / (1024 * 1024), 2),
        }

    def _iter_manifests_locked(self) -> Iterable[Dict[str, Any]]:
        if not os.path.isdir(self.manifests_dir):
            return
        for fn in sorted(os.listdir(self.manifests_dir)):
            if not fn.endswith(".json"):
                continue
            fp = os.path.join(self.manifests_dir, fn)
            try:
                with open(fp, "r", encoding="utf-8") as f:
                    data = json.load(f)
                if isinstance(data, dict):
                    yield data
            except Exception:
                continue

    def list_manifests(self) -> List[Dict[str, Any]]:
        with self.lock:
            out = []
            for m in self._iter_manifests_locked():
                pkgs = m.get("packages") or []
                total_b = int(m.get("total_bytes") or 0)
                out.append({
                    "model": m.get("model"),
                    "manufacturer": m.get("manufacturer") or "",
                    "package_count": len(pkgs),
                    "hwids_count": len(m.get("hwid_map") or {}),
                    "sources": m.get("sources") or [],
                    "total_bytes": total_b,
                    "total_mb": round(total_b / (1024 * 1024), 2),
                    "updated_at": m.get("updated_at"),
                    "oem_pack": m.get("oem_pack"),
                })
            out.sort(key=lambda x: str(x.get("updated_at") or ""), reverse=True)
            return out

    def start_background_job(self, kind: str, target_fn, *args, **kwargs) -> Dict[str, Any]:
        job_id = f"dp_{uuid.uuid4().hex[:10]}"
        job_rec = {
            "job_id": job_id,
            "kind": kind,
            "status": "running",
            "started_at": datetime.now().isoformat(),
            "finished_at": None,
            "result": None,
            "error": None,
        }
        with self.lock:
            self.jobs[job_id] = job_rec
            # Zadrži najviše 30 poslednjih poslova
            if len(self.jobs) > 30:
                oldest = list(self.jobs.keys())[:-30]
                for k in oldest:
                    self.jobs.pop(k, None)
        self._invalidate_stats_cache()

        def _runner():
            try:
                res = target_fn(*args, **kwargs)
                with self.lock:
                    job_rec["status"] = "completed"
                    job_rec["result"] = res
                    job_rec["finished_at"] = datetime.now().isoformat()
            except Exception as ex:
                with self.lock:
                    job_rec["status"] = "error"
                    job_rec["error"] = str(ex)
                    job_rec["finished_at"] = datetime.now().isoformat()
            finally:
                self._invalidate_stats_cache()

        threading.Thread(target=_runner, daemon=True).start()
        return dict(job_rec)

    def start_dedup_background_job(self, kind: str, dedup_key: str, target_fn, *args, **kwargs) -> Dict[str, Any]:
        """Pokreće pozadinski posao uz deduplikaciju po ključu (npr. 'oem:dell:latitude_5420')."""
        norm_key = str(dedup_key or "").strip().lower()
        with self.lock:
            existing_id = self._active_dedup_jobs.get(norm_key)
            if existing_id and existing_id in self.jobs:
                existing = self.jobs[existing_id]
                if existing.get("status") == "running":
                    out = dict(existing)
                    out["deduplicated"] = True
                    return out

            job_id = f"dp_{uuid.uuid4().hex[:10]}"
            job_rec = {
                "job_id": job_id,
                "kind": kind,
                "dedup_key": norm_key,
                "status": "running",
                "started_at": datetime.now().isoformat(),
                "finished_at": None,
                "result": None,
                "error": None,
            }
            self.jobs[job_id] = job_rec
            if norm_key:
                self._active_dedup_jobs[norm_key] = job_id
            if len(self.jobs) > 30:
                oldest = list(self.jobs.keys())[:-30]
                for k in oldest:
                    self.jobs.pop(k, None)
        self._invalidate_stats_cache()

        def _runner():
            try:
                res = target_fn(*args, **kwargs)
                with self.lock:
                    job_rec["status"] = "completed"
                    job_rec["result"] = res
                    job_rec["finished_at"] = datetime.now().isoformat()
                    if norm_key and self._active_dedup_jobs.get(norm_key) == job_id:
                        self._active_dedup_jobs.pop(norm_key, None)
            except Exception as ex:
                with self.lock:
                    job_rec["status"] = "error"
                    job_rec["error"] = str(ex)
                    job_rec["finished_at"] = datetime.now().isoformat()
                    if norm_key and self._active_dedup_jobs.get(norm_key) == job_id:
                        self._active_dedup_jobs.pop(norm_key, None)
            finally:
                self._invalidate_stats_cache()

        threading.Thread(target=_runner, daemon=True).start()
        out = dict(job_rec)
        out["deduplicated"] = False
        return out

    def get_active_oem_job(self, manufacturer: str = "", model: str = "") -> Optional[Dict[str, Any]]:
        vendor = detect_oem_vendor(manufacturer, model)
        if not vendor or not model:
            return None
        try:
            clean_m = sanitize_model_pack_name(model).lower()
        except ValueError:
            clean_m = str(model).strip().lower()
        dedup_key = f"oem:{vendor}:{clean_m}"
        with self.lock:
            jid = self._active_dedup_jobs.get(dedup_key)
            if jid and jid in self.jobs and self.jobs[jid].get("status") == "running":
                return dict(self.jobs[jid])
        return None

    def get_stats(self) -> Dict[str, Any]:
        with self._stats_cache_lock:
            if self._stats_cache is not None:
                return copy.deepcopy(self._stats_cache)
            ver_before = self._stats_version

        with self.lock:
            idx = self._ensure_index_loaded()
            pkgs = idx.get("packages", {})
            total_pool_bytes = sum(int(p.get("size_bytes") or 0) for p in pkgs.values())
            stats = idx.get("stats", {})
            dedup_saved = int(stats.get("dedup_saved_bytes") or 0)
            manifests = self.list_manifests()
            jobs_list = list(reversed(list(self.jobs.values())))[:10]

            source_counts = {"oem_pack": 0, "ms_catalog": 0, "harvest": 0}
            for p in pkgs.values():
                for s in (p.get("sources") or []):
                    if s in source_counts:
                        source_counts[s] += 1

            result = {
                "status": "ok",
                "pool_dir": self.pool_dir,
                "unique_packages": len(pkgs),
                "hwid_winners_count": len(idx.get("hwid_winners", {})),
                "models_count": len(manifests),
                "total_pool_bytes": total_pool_bytes,
                "total_pool_mb": round(total_pool_bytes / (1024 * 1024), 2),
                "dedup_hits": int(stats.get("dedup_hits") or 0),
                "dedup_saved_bytes": dedup_saved,
                "dedup_saved_mb": round(dedup_saved / (1024 * 1024), 2),
                "older_skipped_count": int(stats.get("older_skipped_count") or 0),
                "ingests_count": int(stats.get("ingests_count") or 0),
                "source_counts": source_counts,
                "oem_catalogs": self.catalog_mgr.get_catalog_status(),
                "jobs": jobs_list,
            }

        with self._stats_cache_lock:
            if self._stats_version == ver_before:
                self._stats_cache = copy.deepcopy(result)

        return result

    def scan_and_ingest_archives(
        self,
        archives_dir: Optional[str],
        target_archive: Optional[str] = None,
        delete_ingested: Optional[bool] = None,
    ) -> Dict[str, Any]:
        """
        Skenira direktorijum sa Harvest arhivama (.tar/.tar.gz/.tgz/.zip) i automatski
        ingestuje nove ili izmenjene modele u CAS Driver Pool.
        Ako je `delete_ingested=True` (ili `self.delete_ingested_archives=True`),
        nakon SHA-256 verifikovanog ingesta u Pool/packages/ i uspešnog save_index(),
        automatski bezbedno briše sirovi .tar/.tar.gz/.tgz fajl iz Archives/.
        """
        if not archives_dir or os.path.islink(archives_dir) or not os.path.isdir(archives_dir):
            return {
                "status": "ok",
                "archives_dir": archives_dir,
                "scanned": 0,
                "ingested": 0,
                "skipped": 0,
                "deleted_archives": 0,
                "errors": [],
                "details": [],
            }

        eff_delete = self.delete_ingested_archives if delete_ingested is None else bool(delete_ingested)
        real_archives = os.path.realpath(archives_dir)
        target_name = os.path.basename(str(target_archive or "").strip()) if target_archive else None
        supported_exts = (".tar", ".tar.gz", ".tgz", ".tar.bz2", ".tar.xz", ".zip")
        deletable_archive_exts = (".tar", ".tar.gz", ".tgz")

        scanned = 0
        ingested = 0
        skipped = 0
        deleted_archives = 0
        errors: List[Dict[str, Any]] = []
        details: List[Dict[str, Any]] = []

        def _has_paired_harvest_lock(fp: str, base_lbl: str) -> bool:
            """Proverava da li u Archives/ postoji parni .harvest.lock fajl za ovu arhivu (bez lsof)."""
            parent = os.path.dirname(fp)
            fn_name = os.path.basename(fp)
            candidates = [
                os.path.join(parent, f"{fn_name}.harvest.lock"),
                os.path.join(parent, f"{base_lbl}.harvest.lock"),
            ]
            try:
                clean_lbl = sanitize_model_pack_name(base_lbl)
                candidates.append(os.path.join(parent, f"{clean_lbl}.harvest.lock"))
            except ValueError:
                pass
            return any(os.path.exists(c) for c in candidates)

        def _is_archive_stable(fp: str, expected_size: Optional[int] = None, expected_mtime_ns: Optional[int] = None) -> bool:
            """Proverava da li veličina i mtime_ns fajla miruju (bez oslanjanja na lsof u kontejneru)."""
            try:
                st1 = os.lstat(fp)
                if expected_size is not None and int(st1.st_size) != int(expected_size):
                    return False
                if expected_mtime_ns is not None and int(st1.st_mtime_ns) != int(expected_mtime_ns):
                    return False
                if int(st1.st_size) <= 0:
                    return False
                time.sleep(0.05)
                st2 = os.lstat(fp)
                return int(st1.st_size) == int(st2.st_size) and int(st1.st_mtime_ns) == int(st2.st_mtime_ns)
            except OSError:
                return False

        def _safe_delete_archive(
            fp: str,
            lower_name: str,
            base_lbl: str = "",
            expected_size: Optional[int] = None,
            expected_mtime_ns: Optional[int] = None,
        ) -> bool:
            if not eff_delete or not lower_name.endswith(deletable_archive_exts):
                return False
            try:
                if os.path.islink(fp) or not os.path.isfile(fp):
                    return False
                if os.path.commonpath([real_archives, os.path.realpath(fp)]) != real_archives:
                    return False
                if _has_paired_harvest_lock(fp, base_lbl):
                    return False
                if not _is_archive_stable(fp, expected_size=expected_size, expected_mtime_ns=expected_mtime_ns):
                    return False
                os.remove(fp)
                return True
            except (OSError, ValueError):
                return False

        try:
            entries = sorted(os.listdir(archives_dir))
        except OSError as ex:
            return {
                "status": "error",
                "archives_dir": archives_dir,
                "scanned": 0,
                "ingested": 0,
                "skipped": 0,
                "deleted_archives": 0,
                "errors": [{"file": archives_dir, "error": str(ex)}],
                "details": [],
            }

        for fn in entries:
            if not fn or fn.startswith(".") or ".." in fn or "/" in fn or "\\" in fn:
                continue
            if target_name and fn.lower() != target_name.lower():
                continue
            lower_fn = fn.lower()
            if ".tmp" in lower_fn or lower_fn.endswith((".part", ".lock")):
                continue
            if not lower_fn.endswith(supported_exts):
                continue

            full_path = os.path.join(archives_dir, fn)
            if os.path.islink(full_path) or not os.path.isfile(full_path):
                continue
            try:
                if os.path.commonpath([real_archives, os.path.realpath(full_path)]) != real_archives:
                    continue
            except ValueError:
                continue

            scanned += 1
            try:
                st = os.lstat(full_path)
                file_size = int(st.st_size)
                file_mtime_ns = int(st.st_mtime_ns)
            except OSError:
                skipped += 1
                continue
            if file_size <= 0:
                skipped += 1
                continue

            base_name = fn
            for ext in supported_exts:
                if lower_fn.endswith(ext):
                    base_name = fn[: -len(ext)]
                    break
            clean_model = sanitize_model_pack_name(base_name)
            is_generic = is_generic_model_pack_name(clean_model)

            fast_skip = False
            with self.lock:
                idx = self._ensure_index_loaded()
                rec = idx.get("ingested_archives", {}).get(fn)
                if (
                    isinstance(rec, dict)
                    and rec.get("size_bytes") == file_size
                    and rec.get("mtime_ns") == file_mtime_ns
                ):
                    if is_generic or self.has_model_manifest(clean_model):
                        fast_skip = True

            if fast_skip:
                if _safe_delete_archive(
                    full_path,
                    lower_fn,
                    base_lbl=base_name,
                    expected_size=file_size,
                    expected_mtime_ns=file_mtime_ns,
                ):
                    deleted_archives += 1
                skipped += 1
                continue

            try:
                file_sha = _file_sha256(full_path)
            except OSError as ex:
                errors.append({"file": fn, "error": str(ex)})
                continue

            already_done = False
            with self.lock:
                idx = self._ensure_index_loaded()
                rec = idx.get("ingested_archives", {}).get(fn)
                if isinstance(rec, dict) and rec.get("sha256") == file_sha:
                    if is_generic or self.has_model_manifest(clean_model):
                        already_done = True
                        if rec.get("mtime_ns") != file_mtime_ns or rec.get("size_bytes") != file_size:
                            rec["mtime_ns"] = file_mtime_ns
                            rec["size_bytes"] = file_size
                            self._save_index_locked(idx)

            if already_done:
                if _safe_delete_archive(
                    full_path,
                    lower_fn,
                    base_lbl=base_name,
                    expected_size=file_size,
                    expected_mtime_ns=file_mtime_ns,
                ):
                    deleted_archives += 1
                skipped += 1
                continue

            try:
                res = self.ingest_archive(
                    full_path,
                    source="harvest",
                    model=None if is_generic else clean_model,
                )
                with self.lock:
                    idx = self._ensure_index_loaded()
                    idx.setdefault("ingested_archives", {})[fn] = {
                        "sha256": file_sha,
                        "model": None if is_generic else clean_model,
                        "size_bytes": file_size,
                        "mtime_ns": file_mtime_ns,
                        "ingested_at": datetime.now().isoformat(),
                    }
                    self._save_index_locked(idx)
                was_deleted = _safe_delete_archive(
                    full_path,
                    lower_fn,
                    base_lbl=base_name,
                    expected_size=file_size,
                    expected_mtime_ns=file_mtime_ns,
                )
                if was_deleted:
                    deleted_archives += 1
                ingested += 1
                details.append({
                    "file": fn,
                    "model": None if is_generic else clean_model,
                    "ingested_count": res.get("packages_added", 0),
                    "total_units": res.get("units_discovered", 0),
                    "archive_deleted": was_deleted,
                })
            except Exception as ex:
                errors.append({"file": fn, "error": str(ex)})

        if ingested > 0:
            gc.collect()

        return {
            "status": "ok",
            "archives_dir": archives_dir,
            "scanned": scanned,
            "ingested": ingested,
            "skipped": skipped,
            "deleted_archives": deleted_archives,
            "errors": errors,
            "details": details,
        }
