"""
servishub.drivers.oem.dropin
============================
Hybrid Drop-in OEM provajder za Tier 2 proizvođače (ASUS, Acer, MSI, Dynabook)
i lokalni fallback za Tier 1 proizvođače.

Bezbedno (bez path traversala i bez praćenja simboličkih linkova) skenira
`<dropin_dir>/<vendor>/<model>/` za `.zip`, `.cab` i `.msi` pakete.
"""

from __future__ import annotations

import os
import re
import threading
from datetime import datetime
from typing import Any, Dict, List, Optional, Tuple

from servishub.drivers.oem.base import BaseOEMProvider
from servishub.drivers.oem.catalogs import (
    MAX_OEM_PACKAGE_BYTES,
    _extract_os_family_and_feature,
    _norm_model_tokens,
    detect_oem_vendor,
)

SUPPORTED_DROPIN_EXTENSIONS: Tuple[str, ...] = (".zip", ".cab", ".msi")
DEFAULT_DROPIN_DIR = os.environ.get("SERVIS_OEM_DROPIN_DIR", "/app/oem_dropin")


def _has_traversal_pattern(raw: str) -> bool:
    """Proverava da li ulazni string sadrži obrasce za path traversal."""
    val = str(raw or "")
    if not val:
        return False
    if "\x00" in val:
        return True
    norm = val.replace("\\", "/")
    if norm.startswith("/") or re.match(r"^[A-Za-z]:", norm):
        return True
    parts = [p for p in norm.split("/") if p not in ("", ".")]
    return ".." in parts


def _score_dropin_model(query_model: str, query_tokens: List[str], cand_dir_name: str) -> int:
    """Ocenjuje poklapanje naziva modela sa imenom foldera u drop-in stablu."""
    if not query_model or not cand_dir_name:
        return 0
    q_slug = re.sub(r"[^a-z0-9]+", "_", query_model.lower()).strip("_")
    c_slug = re.sub(r"[^a-z0-9]+", "_", cand_dir_name.lower()).strip("_")
    if q_slug and c_slug and q_slug == c_slug:
        return 100

    c_tokens = _norm_model_tokens(cand_dir_name)
    if not query_tokens or not c_tokens:
        return 0
    if " ".join(query_tokens) == " ".join(c_tokens):
        return 98

    q_nums = {t for t in query_tokens if any(ch.isdigit() for ch in t)}
    c_nums = {t for t in c_tokens if any(ch.isdigit() for ch in t)}
    if q_nums and c_nums and q_nums != c_nums:
        return 0

    q_set = set(query_tokens)
    c_set = set(c_tokens)
    if q_set == c_set:
        return 95
    if q_set <= c_set:
        return max(60, 88 - 5 * len(c_set - q_set))
    if c_set <= q_set:
        return max(55, 84 - 5 * len(q_set - c_set))
    return 0


class DropInOEMProvider(BaseOEMProvider):
    """
    OEM provajder koji bezbedno skenira lokalni drop-in direktorijum
    `<dropin_dir>/<vendor>/<model>/` za `.zip`, `.cab` i `.msi` pakete.
    """

    def __init__(
        self,
        vendor: str = "",
        dropin_dir: Optional[str] = None,
        cache_dir: Optional[str] = None,
        timeout: float = 5.0,
    ):
        raw_dropin = (
            dropin_dir
            if (dropin_dir is not None and str(dropin_dir).strip())
            else os.environ.get("SERVIS_OEM_DROPIN_DIR", "/app/oem_dropin")
        )
        self.dropin_dir = os.path.abspath(str(raw_dropin).strip())
        self.vendor = self._normalize_vendor(vendor)
        self.timeout = self._clamp_timeout(timeout)
        self.lock = threading.Lock()
        self.base_cache_dir = (
            os.path.abspath(cache_dir)
            if cache_dir
            else os.path.join(self.dropin_dir, "_cache")
        )

    @staticmethod
    def _normalize_vendor(vendor: str) -> str:
        raw = str(vendor or "").strip()
        if not raw or _has_traversal_pattern(raw):
            return ""
        detected = detect_oem_vendor(raw, "")
        if detected:
            return detected
        clean = re.sub(r"[^a-z0-9_-]", "", raw.lower())
        return clean

    def resolve_safe_subdir(self, vendor: str, model: str = "") -> str:
        """
        Striktno validira i konstruiše putanju `<dropin_dir>/<vendor>[/<model>]`
        bez ikakve mogućnosti path traversala ili praćenja symlinkova.
        """
        if _has_traversal_pattern(vendor) or _has_traversal_pattern(model):
            raise ValueError("Detektovan pokušaj path traversala u vendor/model parametru.")
        if "/" in str(vendor or "") or "\\" in str(vendor or ""):
            raise ValueError("Separator putanje nije dozvoljen u imenu vendora.")
        if "/" in str(model or "") or "\\" in str(model or ""):
            raise ValueError("Separator putanje nije dozvoljen u imenu modela.")

        clean_vendor = self._normalize_vendor(vendor)
        if not clean_vendor:
            raise ValueError("Nevažeći OEM vendor naziv za drop-in putanju.")

        real_base = os.path.realpath(self.dropin_dir)
        vendor_path = os.path.join(real_base, clean_vendor)
        if os.path.islink(vendor_path):
            raise ValueError(f"Vendor direktorijum ne sme biti symlink: {vendor_path}")
        real_vendor = os.path.realpath(vendor_path)
        if os.path.commonpath([real_base, real_vendor]) != real_base:
            raise ValueError("Detektovan path traversal za vendor direktorijum.")

        if not model:
            return real_vendor

        safe_model = re.sub(r"[^A-Za-z0-9._-]+", "_", str(model).strip()).strip("._")
        if not safe_model or safe_model in (".", ".."):
            raise ValueError("Nevažeći naziv modela za drop-in putanju.")

        model_path = os.path.join(real_vendor, safe_model)
        if os.path.islink(model_path):
            raise ValueError(f"Model direktorijum ne sme biti symlink: {model_path}")
        real_model = os.path.realpath(model_path)
        if os.path.commonpath([real_base, real_model]) != real_base:
            raise ValueError("Detektovan path traversal za model direktorijum.")
        return real_model

    def scan_packages(
        self,
        vendor: str = "",
        model: str = "",
        os_ver: str = "win11",
    ) -> List[Dict[str, Any]]:
        """
        Bezbedno skenira `<dropin_dir>/<vendor>/[<model>/]` za `.zip`, `.cab` i `.msi` pakete.
        Nikada ne prati symlinkove i odbija bilo kakav path traversal.
        """
        raw_vendor = vendor or self.vendor
        if _has_traversal_pattern(raw_vendor) or _has_traversal_pattern(model):
            return []

        norm_vendor = self._normalize_vendor(raw_vendor)
        if not norm_vendor and model:
            norm_vendor = detect_oem_vendor("", model)
        if not norm_vendor:
            return []

        if not os.path.isdir(self.dropin_dir) or os.path.islink(self.dropin_dir):
            return []

        real_base = os.path.realpath(self.dropin_dir)
        vendor_dir = os.path.join(real_base, norm_vendor)
        if os.path.islink(vendor_dir) or not os.path.isdir(vendor_dir):
            return []
        real_vendor = os.path.realpath(vendor_dir)
        if os.path.commonpath([real_base, real_vendor]) != real_base:
            return []

        req_os, _ = _extract_os_family_and_feature(os_ver or "win11")
        if req_os == "other":
            req_os = "win11"

        clean_model = str(model or "").strip()
        q_tokens = _norm_model_tokens(clean_model) if clean_model else []

        results: List[Dict[str, Any]] = []
        try:
            with os.scandir(real_vendor) as it:
                model_dirs = sorted(it, key=lambda e: e.name.lower())
        except OSError:
            return []

        for m_entry in model_dirs:
            try:
                if m_entry.name.startswith(".") or ".." in m_entry.name:
                    continue
                if m_entry.is_symlink() or os.path.islink(m_entry.path):
                    continue
                if not m_entry.is_dir(follow_symlinks=False):
                    continue
                real_m_dir = os.path.realpath(m_entry.path)
                if os.path.commonpath([real_vendor, real_m_dir]) != real_vendor:
                    continue
            except OSError:
                continue

            if clean_model:
                score = _score_dropin_model(clean_model, q_tokens, m_entry.name)
                if score <= 0:
                    continue
            else:
                score = 100

            for cur_dir, dirnames, filenames in os.walk(real_m_dir, followlinks=False):
                dirnames[:] = [
                    d for d in sorted(dirnames)
                    if not d.startswith(".") and not os.path.islink(os.path.join(cur_dir, d))
                ]
                for fn in sorted(filenames):
                    low_fn = fn.lower()
                    if fn.startswith(".") or ".." in fn or low_fn.endswith((".tmp", ".part", ".lock")) or ".tmp." in low_fn:
                        continue
                    _, ext = os.path.splitext(low_fn)
                    if ext not in SUPPORTED_DROPIN_EXTENSIONS:
                        continue

                    fp = os.path.join(cur_dir, fn)
                    if os.path.islink(fp):
                        continue
                    real_fp = os.path.realpath(fp)
                    if os.path.commonpath([real_m_dir, real_fp]) != real_m_dir:
                        continue
                    if not os.path.isfile(real_fp):
                        continue
                    try:
                        st = os.lstat(real_fp)
                        sz = int(st.st_size)
                        mtime = float(st.st_mtime)
                    except OSError:
                        continue
                    if sz <= 0 or sz > MAX_OEM_PACKAGE_BYTES:
                        continue

                    pkg_os, pkg_feat = _extract_os_family_and_feature(fn)
                    if pkg_os == "other":
                        pkg_os = req_os
                    os_score = 2 if pkg_os == req_os else 1
                    rel_date = datetime.fromtimestamp(mtime).strftime("%Y-%m-%d")

                    results.append({
                        "vendor": norm_vendor,
                        "source": "dropin",
                        "package_id": f"dropin:{norm_vendor}:{m_entry.name}:{fn}",
                        "package_name": fn,
                        "version": "",
                        "release_date": rel_date,
                        "os": pkg_os,
                        "os_feature_rank": pkg_feat,
                        "os_supported": [pkg_os],
                        "arch": "x64",
                        "format": ext.lstrip("."),
                        "download_url": "",
                        "local_path": real_fp,
                        "size_bytes": sz,
                        "sha256": "",
                        "md5": "",
                        "models": [m_entry.name],
                        "system_ids": [],
                        "mtms": [],
                        "match_score": score,
                        "_sort_key": (score, os_score, pkg_feat, mtime, sz, fn.lower()),
                    })

        results.sort(key=lambda item: item["_sort_key"], reverse=True)
        for item in results:
            item.pop("_sort_key", None)
        return results

    def get_warranty_info(self, serial: str, model: str = "") -> Dict[str, Any]:
        """Tier 2 drop-in provajder nema online warranty API; vraća graceful fallback."""
        clean_sn = re.sub(r"[^A-Z0-9_-]", "", str(serial or "").strip().upper())
        if not clean_sn:
            return {
                "status": "error",
                "in_warranty": False,
                "status_label": "Neispravan serijski broj",
                "service_level": "N/A",
                "start_date": "",
                "end_date": "",
                "days_remaining": 0,
                "raw_data": {"error": "Serijski broj je prazan"},
            }
        return {
            "status": "fallback",
            "in_warranty": False,
            "status_label": "Nepoznato (Tier 2 Drop-in OEM)",
            "service_level": "N/A",
            "start_date": "",
            "end_date": "",
            "days_remaining": 0,
            "raw_data": {"serial": clean_sn, "model": model, "vendor": self.vendor},
        }

    def resolve_driver_pack(self, model: str, os_ver: str = "win11-x64") -> Dict[str, Any]:
        """Vraća najbolji lokalni drop-in paket iz `<dropin_dir>/<vendor>/<model>/`."""
        clean_model = str(model or "").strip()
        if not clean_model or _has_traversal_pattern(clean_model):
            return {
                "status": "not_found",
                "source": "dropin",
                "package_name": "",
                "download_url": "",
                "local_path": "",
                "size_bytes": 0,
            }

        matches = self.scan_packages(vendor=self.vendor, model=clean_model, os_ver=os_ver)
        if not matches:
            return {
                "status": "not_found",
                "source": "dropin",
                "package_name": "",
                "download_url": "",
                "local_path": "",
                "size_bytes": 0,
            }

        best = matches[0]
        return {
            "status": "ok",
            "source": "dropin",
            "package_name": best["package_name"],
            "download_url": "",
            "local_path": best["local_path"],
            "size_bytes": int(best["size_bytes"]),
        }

    def check_bios_update(self, model: str, current_bios: str = "") -> Dict[str, Any]:
        """Tier 2 drop-in provajder ne radi online proveru BIOS-a."""
        return {
            "update_available": False,
            "latest_version": "",
            "current_version": current_bios,
            "download_url": "",
            "release_date": "",
        }
