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
from servishub.drivers.oem.catalogs import OemCatalogManager, detect_oem_vendor
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

_SOURCE_PRIORITY = {
    "oem_pack": 3,
    "ms_catalog": 2,
    "harvest": 1,
}

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


def _extract_archive_safely(archive_path: str, dest_dir: str) -> None:
    """
    Bezbedno raspakuje .tar/.tar.gz/.zip/.cab/.7z/.exe(SFX) arhive u `dest_dir`
    sa strogim path-traversal i symlink proverama.
    """
    ap = os.path.abspath(archive_path)
    lower = ap.lower()
    os.makedirs(dest_dir, exist_ok=True)

    # 1. TAR / TAR.GZ / TGZ
    if lower.endswith((".tar", ".tar.gz", ".tgz", ".tar.bz2", ".tar.xz")):
        with tarfile.open(ap, "r:*") as tf:
            members = tf.getmembers()
            for member in members:
                if not _is_safe_archive_relpath(member.name):
                    raise ValueError(f"Nedozvoljena putanja unutar .tar arhive: '{member.name}'.")
                if member.issym() or member.islnk() or member.ischr() or member.isblk() or member.isfifo():
                    raise ValueError(f"Nedozvoljen link/device član unutar .tar arhive: '{member.name}'.")
            for member in members:
                if member.isdir():
                    target = os.path.realpath(os.path.join(dest_dir, member.name))
                    if os.path.commonpath([os.path.realpath(dest_dir), target]) != os.path.realpath(dest_dir):
                        raise ValueError(f"Path traversal direktorijuma u .tar: '{member.name}'.")
                    os.makedirs(target, exist_ok=True)
                elif member.isreg():
                    rel = member.name.replace("\\", "/").lstrip("/")
                    target = os.path.realpath(os.path.join(dest_dir, rel))
                    if os.path.commonpath([os.path.realpath(dest_dir), target]) != os.path.realpath(dest_dir):
                        raise ValueError(f"Path traversal fajla u .tar: '{member.name}'.")
                    os.makedirs(os.path.dirname(target), exist_ok=True)
                    src_f = tf.extractfile(member)
                    if src_f is not None:
                        with src_f, open(target, "wb") as out_f:
                            shutil.copyfileobj(src_f, out_f)
        _assert_extracted_tree_safe(dest_dir)
        return

    # 2. ZIP
    if lower.endswith(".zip"):
        with zipfile.ZipFile(ap, "r") as zf:
            for info in zf.infolist():
                if not _is_safe_archive_relpath(info.filename):
                    raise ValueError(f"Nedozvoljena putanja unutar .zip arhive: '{info.filename}'.")
                # Unix symlink bit u external_attr
                if (info.external_attr >> 16) & 0o170000 == 0o120000:
                    raise ValueError(f"Nedozvoljen symlink unutar .zip arhive: '{info.filename}'.")
            for info in zf.infolist():
                if info.is_dir():
                    target = os.path.realpath(os.path.join(dest_dir, info.filename))
                    if os.path.commonpath([os.path.realpath(dest_dir), target]) == os.path.realpath(dest_dir):
                        os.makedirs(target, exist_ok=True)
                    continue
                target = os.path.realpath(os.path.join(dest_dir, info.filename))
                if os.path.commonpath([os.path.realpath(dest_dir), target]) != os.path.realpath(dest_dir):
                    raise ValueError(f"Path traversal u .zip: '{info.filename}'.")
                os.makedirs(os.path.dirname(target), exist_ok=True)
                with zf.open(info, "r") as src_f, open(target, "wb") as out_f:
                    shutil.copyfileobj(src_f, out_f)
        _assert_extracted_tree_safe(dest_dir)
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


class DriverPoolManager:
    """
    Centralni Content-Addressable Driver Pool sa deduplikacijom na nivou .inf paketa,
    Model Manifestima i integrisanim OEM Enterprise katalozima.
    """

    def __init__(
        self,
        pool_dir: str,
        catalog_mgr: Optional[OemCatalogManager] = None,
    ):
        self.pool_dir = os.path.abspath(pool_dir)
        self.packages_dir = os.path.join(self.pool_dir, "packages")
        self.manifests_dir = os.path.join(self.pool_dir, "manifests")
        self.bundles_dir = os.path.join(self.pool_dir, "bundles")
        self.catalogs_dir = os.path.join(self.pool_dir, "oem_catalogs")
        self.index_path = os.path.join(self.pool_dir, "pool_index.json")

        self.lock = threading.RLock()
        self.catalog_mgr = catalog_mgr or OemCatalogManager(catalogs_dir=self.catalogs_dir)
        self.jobs: Dict[str, Dict[str, Any]] = {}

        os.makedirs(self.packages_dir, exist_ok=True)
        os.makedirs(self.manifests_dir, exist_ok=True)
        os.makedirs(self.bundles_dir, exist_ok=True)
        os.makedirs(self.catalogs_dir, exist_ok=True)

        self._ensure_index_loaded()

    @staticmethod
    def _empty_index() -> Dict[str, Any]:
        return {
            "schema_version": 1,
            "updated_at": datetime.now().isoformat(),
            "packages": {},
            "hwid_winners": {},
            "rejected_packages": [],
            "ingested_archives": {},
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
                    with open(self.index_path, "r", encoding="utf-8") as f:
                        data = json.load(f)
                    if isinstance(data, dict) and isinstance(data.get("packages"), dict):
                        data.setdefault("hwid_winners", {})
                        data.setdefault("rejected_packages", [])
                        data.setdefault("ingested_archives", {})
                        st = data.setdefault("stats", {})
                        st.setdefault("dedup_hits", 0)
                        st.setdefault("dedup_saved_bytes", 0)
                        st.setdefault("older_skipped_count", 0)
                        st.setdefault("ingests_count", 0)
                        return data
                except Exception:
                    pass
            fresh = self._empty_index()
            self._save_index_locked(fresh)
            return fresh

    def _save_index_locked(self, index_data: Dict[str, Any]) -> None:
        index_data["updated_at"] = datetime.now().isoformat()
        tmp_path = f"{self.index_path}.tmp"
        with open(tmp_path, "w", encoding="utf-8") as f:
            json.dump(index_data, f, indent=2, ensure_ascii=False)
        os.replace(tmp_path, self.index_path)

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
        tmp_path = f"{path}.tmp"
        with open(tmp_path, "w", encoding="utf-8") as f:
            json.dump(manifest, f, indent=2, ensure_ascii=False)
        os.replace(tmp_path, path)

    @staticmethod
    def _is_package_better(cand: Dict[str, Any], existing: Dict[str, Any]) -> bool:
        """
        Određuje da li je `cand` paket strogo bolji od `existing` paketa za isti HWID:
        1. Potpisan paket (has_cat=True) uvek pobeđuje nepotpisani (has_cat=False).
        2. Strogo novija DriverDate/DriverVer verzija pobeđuje (is_catalog_driver_newer).
        3. Ako existing NIJE noviji od cand i verzije/datumi su identični:
           viši prioritet izvora (oem_pack > ms_catalog > harvest) pobeđuje.
        """
        c_cat = bool(cand.get("has_cat"))
        e_cat = bool(existing.get("has_cat"))
        if c_cat != e_cat:
            return c_cat

        c_ver = str(cand.get("driver_ver") or "")
        c_date = str(cand.get("driver_date") or "")
        e_ver = str(existing.get("driver_ver") or "")
        e_date = str(existing.get("driver_date") or "")

        if is_catalog_driver_newer(e_ver, e_date, c_ver, c_date):
            return True
        if is_catalog_driver_newer(c_ver, c_date, e_ver, e_date):
            return False

        # Identična verzija i datum: proveri prioritet izvora
        c_prio = max((_SOURCE_PRIORITY.get(s, 0) for s in (cand.get("sources") or [cand.get("source")])), default=0)
        e_prio = max((_SOURCE_PRIORITY.get(s, 0) for s in (existing.get("sources") or [existing.get("source")])), default=0)
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

        with self.lock:
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
                if hwids_list:
                    any_win_or_new = False
                    for hw in hwids_list:
                        hw_l = hw.lower()
                        win_id = idx["hwid_winners"].get(hw_l)
                        if not win_id or win_id not in idx["packages"]:
                            any_win_or_new = True
                            break
                        if self._is_package_better(cand_meta, idx["packages"][win_id]):
                            any_win_or_new = True
                            break
                    if not any_win_or_new:
                        older_skipped += 1
                        idx["stats"]["older_skipped_count"] = int(idx["stats"].get("older_skipped_count", 0)) + 1
                        # U manifest modela i dalje možemo uvezati pobednike iz pool-a za iste HWID-ove
                        for hw in hwids_list:
                            win_id = idx["hwid_winners"].get(hw.lower())
                            if win_id and win_id in idx["packages"]:
                                manifest_pkg_candidates.append((win_id, idx["packages"][win_id]))
                        continue

                # Novi paket za disk: atomično kopiranje u packages/<pkg_sha256>
                tmp_pkg_dir = os.path.join(self.packages_dir, f".tmp-{pkg_sha256}-{uuid.uuid4().hex[:8]}")
                try:
                    os.makedirs(tmp_pkg_dir, exist_ok=True)
                    for rel_p, src_fp, _, _ in accepted_files:
                        dst_fp = os.path.realpath(os.path.join(tmp_pkg_dir, rel_p))
                        if os.path.commonpath([os.path.realpath(tmp_pkg_dir), dst_fp]) != os.path.realpath(tmp_pkg_dir):
                            raise ValueError(f"Nevalidna relativna putanja u paketu: {rel_p}")
                        os.makedirs(os.path.dirname(dst_fp), exist_ok=True)
                        shutil.copy2(src_fp, dst_fp)
                    if os.path.isdir(pkg_target_dir):
                        shutil.rmtree(tmp_pkg_dir, ignore_errors=True)
                    else:
                        os.replace(tmp_pkg_dir, pkg_target_dir)
                except Exception:
                    shutil.rmtree(tmp_pkg_dir, ignore_errors=True)
                    raise

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
        with tempfile.TemporaryDirectory(prefix="wis-pool-extract-") as tmp_dir:
            _extract_archive_safely(archive_path, tmp_dir)
            return self.ingest_directory(
                tmp_dir,
                source=source,
                model=model,
                manufacturer=manufacturer,
                oem_meta=oem_meta,
            )

    def ingest_oem_pack(
        self,
        manufacturer: str,
        model: str,
        os_ver: str = "win11",
        system_id: str = "",
        force_sync: bool = False,
    ) -> Dict[str, Any]:
        """
        Preuzima zvanični OEM Driver Pack sa HP/Dell/Lenovo kataloga, verifikuje hash,
        raspakuje i ingestuje u Driver Pool uz deduplikaciju i ažuriranje manifesta modela.
        """
        vendor = detect_oem_vendor(manufacturer, model)
        if not vendor:
            return {"status": "unsupported_vendor", "manufacturer": manufacturer, "model": model}

        if force_sync or self.catalog_mgr.get_catalog_status().get(vendor, {}).get("stale", True):
            try:
                self.catalog_mgr.sync_catalog(vendor, force=force_sync)
            except Exception as sync_ex:
                # Nastavi ako postoji bar lokalni keš
                if not self.catalog_mgr.get_catalog_status().get(vendor, {}).get("synced"):
                    return {"status": "catalog_error", "vendor": vendor, "error": str(sync_ex)}

        entry = self.catalog_mgr.find_driver_pack(
            manufacturer=vendor,
            model=model,
            os_ver=os_ver,
            system_id=system_id,
        )
        if not entry:
            return {"status": "not_found", "vendor": vendor, "model": model}

        with tempfile.TemporaryDirectory(prefix="wis-oem-dl-") as tmp_dl:
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

    def build_bundle_tar(
        self,
        model: str = "",
        missing_hwids: Optional[Sequence[str]] = None,
        all_hwids: Optional[Sequence[str]] = None,
    ) -> Dict[str, Any]:
        """
        Dinamički sastavlja (ili vraća iz bundles/ keša) minimalni, 100% deduplikovani `.tar` paket
        za traženi model i/ili konkretne HWID-ove uređaja.
        """
        idx = self._ensure_index_loaded()
        manifest = self._find_manifest_for_model(model)
        manifest_hmap = (manifest or {}).get("hwid_map") or {}

        selected_pids: List[str] = []
        seen_pids = set()
        matched_hwids: List[str] = []
        unmatched_hwids: List[str] = []

        def _pick_for_hwid(raw_h: str) -> Optional[str]:
            cands = generate_hwid_candidates(raw_h)
            for cand in cands:
                cand_l = cand.lower()
                m_pid = manifest_hmap.get(cand_l)
                g_pid = idx["hwid_winners"].get(cand_l)
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

        clean_missing = [str(h).strip() for h in (missing_hwids or []) if str(h).strip()]
        clean_all = [str(h).strip() for h in (all_hwids or []) if str(h).strip()]

        if clean_missing:
            # Ciljani mod: samo paketi koji rešavaju prijavljene problematične HWID-ove
            for raw_h in clean_missing:
                pid = _pick_for_hwid(raw_h)
                if pid:
                    matched_hwids.append(raw_h)
                    if pid not in seen_pids:
                        seen_pids.add(pid)
                        selected_pids.append(pid)
                else:
                    unmatched_hwids.append(raw_h)
        else:
            # Puni mod modela: ceo manifest modela + dopuna iz Pool-a za poznate all_hwids uređaja
            if manifest:
                for pid in (manifest.get("packages") or []):
                    if pid in idx["packages"] and pid not in seen_pids and os.path.isdir(os.path.join(self.packages_dir, pid)):
                        seen_pids.add(pid)
                        selected_pids.append(pid)
            for raw_h in clean_all:
                pid = _pick_for_hwid(raw_h)
                if pid:
                    matched_hwids.append(raw_h)
                    if pid not in seen_pids:
                        seen_pids.add(pid)
                        selected_pids.append(pid)
                else:
                    unmatched_hwids.append(raw_h)

        if not selected_pids:
            return {
                "available": False,
                "model": (manifest or {}).get("model") or model,
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
        if os.path.isfile(bundle_path) and os.path.isfile(meta_path):
            try:
                with open(meta_path, "r", encoding="utf-8") as mf:
                    bmeta = json.load(mf)
                if bmeta.get("sha256") and os.path.getsize(bundle_path) > 0:
                    bundle_sha = bmeta["sha256"]
            except Exception:
                bundle_sha = None

        if not bundle_sha:
            os.makedirs(self.bundles_dir, exist_ok=True)
            tmp_tar = f"{bundle_path}.{uuid.uuid4().hex[:6]}.tmp"
            try:
                with tarfile.open(tmp_tar, "w") as tf:
                    for pid in sorted_pids:
                        pmeta = idx["packages"].get(pid, {})
                        pdir = os.path.join(self.packages_dir, pid)
                        if not os.path.isdir(pdir):
                            continue
                        stem = re.sub(r"[^a-zA-Z0-9._-]+", "_", str(pmeta.get("inf_stem") or "drv")).strip("_") or "drv"
                        folder_prefix = f"{stem}_{pid[:8]}"
                        for rdir, _, files in os.walk(pdir, followlinks=False):
                            for fn in sorted(files):
                                if fn.lower().endswith(FORBIDDEN_DRIVER_EXTENSIONS):
                                    continue
                                full_f = os.path.join(rdir, fn)
                                rel = os.path.relpath(full_f, pdir).replace("\\", "/")
                                tf.add(full_f, arcname=f"{folder_prefix}/{rel}", recursive=False)
                bundle_sha = _file_sha256(tmp_tar)
                os.replace(tmp_tar, bundle_path)
                tmp_meta = f"{meta_path}.tmp"
                with open(tmp_meta, "w", encoding="utf-8") as mf:
                    json.dump({"sha256": bundle_sha, "packages": sorted_pids, "created_at": datetime.now().isoformat()}, mf)
                os.replace(tmp_meta, meta_path)
            except Exception:
                if os.path.exists(tmp_tar):
                    try:
                        os.remove(tmp_tar)
                    except OSError:
                        pass
                raise

        sz_bytes = os.path.getsize(bundle_path)
        pkg_details = []
        sources_set = set()
        for pid in sorted_pids:
            pm = idx["packages"].get(pid, {})
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
            "model": (manifest or {}).get("model") or safe_model_lbl,
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
            if os.path.isdir(self.packages_dir):
                for entry in os.listdir(self.packages_dir):
                    ep = os.path.join(self.packages_dir, entry)
                    if os.path.isdir(ep) and entry not in idx["packages"]:
                        shutil.rmtree(ep, ignore_errors=True)

            # Očisti zastarele bundle-ove
            if os.path.isdir(self.bundles_dir):
                for entry in os.listdir(self.bundles_dir):
                    ep = os.path.join(self.bundles_dir, entry)
                    if not os.path.isfile(ep):
                        continue
                    try:
                        age = now - os.path.getmtime(ep)
                        if max_bundle_age_sec <= 0 or age >= max_bundle_age_sec:
                            freed_bytes += os.path.getsize(ep)
                            os.remove(ep)
                            removed_bundles += 1
                    except OSError:
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

        threading.Thread(target=_runner, daemon=True).start()
        return dict(job_rec)

    def get_stats(self) -> Dict[str, Any]:
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

        return {
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

    def scan_and_ingest_archives(
        self,
        archives_dir: Optional[str],
        target_archive: Optional[str] = None,
    ) -> Dict[str, Any]:
        """
        Skenira direktorijum sa Harvest arhivama (.tar/.tar.gz/.tgz/.zip) i automatski
        ingestuje nove ili izmenjene modele u CAS Driver Pool.
        """
        if not archives_dir or not os.path.isdir(archives_dir):
            return {
                "status": "ok",
                "archives_dir": archives_dir,
                "scanned": 0,
                "ingested": 0,
                "skipped": 0,
                "errors": [],
                "details": [],
            }

        target_name = os.path.basename(str(target_archive or "").strip()) if target_archive else None
        supported_exts = (".tar", ".tar.gz", ".tgz", ".tar.bz2", ".tar.xz", ".zip")

        scanned = 0
        ingested = 0
        skipped = 0
        errors: List[Dict[str, Any]] = []
        details: List[Dict[str, Any]] = []

        try:
            entries = sorted(os.listdir(archives_dir))
        except OSError as ex:
            return {
                "status": "error",
                "archives_dir": archives_dir,
                "scanned": 0,
                "ingested": 0,
                "skipped": 0,
                "errors": [{"file": archives_dir, "error": str(ex)}],
                "details": [],
            }

        for fn in entries:
            if not fn or fn.startswith("."):
                continue
            if target_name and fn.lower() != target_name.lower():
                continue
            lower_fn = fn.lower()
            if ".tmp" in lower_fn or lower_fn.endswith((".part", ".lock")):
                continue
            if not lower_fn.endswith(supported_exts):
                continue

            full_path = os.path.join(archives_dir, fn)
            if not os.path.isfile(full_path):
                continue

            scanned += 1
            try:
                file_size = os.path.getsize(full_path)
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

            if already_done:
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
                        "ingested_at": datetime.now().isoformat(),
                    }
                    self._save_index_locked(idx)
                ingested += 1
                details.append({
                    "file": fn,
                    "model": None if is_generic else clean_model,
                    "ingested_count": res.get("ingested_count", 0),
                    "total_units": res.get("total_units", 0),
                })
            except Exception as ex:
                errors.append({"file": fn, "error": str(ex)})

        return {
            "status": "ok",
            "archives_dir": archives_dir,
            "scanned": scanned,
            "ingested": ingested,
            "skipped": skipped,
            "errors": errors,
            "details": details,
        }
