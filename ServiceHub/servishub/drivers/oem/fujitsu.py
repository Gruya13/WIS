"""
servishub.drivers.oem.fujitsu
=============================
Konkretna implementacija OEM provajdera i XML parsera za Fujitsu hardver
(LIFEBOOK, ESPRIMO, CELSIUS) preko Fujitsu DeskUpdate Enterprise kataloga.

Podržava:
- Parsiranje Fujitsu DeskUpdate Enterprise XML kataloga sa `_check_xml_safe` XXE zaštitom
  i strogom `validate_oem_download_url` HTTPS allowlist proverom (`support.ts.fujitsu.com`).
- Rezoluciju zvaničnih `.zip` / `.cab` drajver paketa sa lokalnim disk kešom.
- Proveru BIOS/UEFI ažuriranja i graceful fallback za status garancije.
"""

from __future__ import annotations

import os
import re
import threading
import urllib.parse
import xml.etree.ElementTree as ET
from datetime import datetime
from typing import Any, Dict, List, Optional, Sequence

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

DESKUPDATE_CATALOG_URL = "https://support.ts.fujitsu.com/DeskUpdate/Catalog/DeskUpdateCatalog.xml"


def parse_fujitsu_catalog_xml(
    xml_bytes: bytes,
    allowed_hosts: Optional[Sequence[str]] = None,
) -> List[Dict[str, Any]]:
    """
    Parsira Fujitsu DeskUpdate Enterprise XML katalog u kanonsku listu zapisa.
    Obavezno primenjuje `_check_xml_safe` (XXE zaštita) i `validate_oem_download_url`.
    """
    clean_bytes = _check_xml_safe(xml_bytes)
    root = ET.fromstring(clean_bytes)
    base_url = (
        root.attrib.get("baseUrl")
        or root.attrib.get("baseLocation")
        or root.attrib.get("BaseUrl")
        or "https://support.ts.fujitsu.com"
    ).strip().rstrip("/")
    if not base_url.startswith(("http://", "https://")):
        base_url = f"https://{base_url}"

    entries: List[Dict[str, Any]] = []
    pkg_tags = {"DriverPackage", "Package", "DriverPack", "Update", "DeskUpdatePackage"}

    for elem in root.iter():
        if _strip_ns(elem.tag) not in pkg_tags:
            continue

        fields: Dict[str, str] = {k.lower(): str(v).strip() for k, v in elem.attrib.items()}
        models: List[str] = []
        system_ids: List[str] = []
        os_list: List[str] = []
        best_os = "other"
        best_feat = 0
        best_os_rank = -1
        is_x64 = True

        seen_m = set()
        seen_s = set()

        def _add_model(val: str) -> None:
            v = val.strip()
            if v and v.lower() not in seen_m:
                seen_m.add(v.lower())
                models.append(v)

        def _add_sid(val: str) -> None:
            v = val.strip().lower()
            if v and v not in seen_s:
                seen_s.add(v)
                system_ids.append(v)

        for child in elem:
            ctag = _strip_ns(child.tag).lower()
            if ctag in ("models", "supportedsystems", "systems", "products"):
                for sub in child.iter():
                    if sub is child:
                        continue
                    stag = _strip_ns(sub.tag).lower()
                    if stag in ("model", "system", "product", "systemname"):
                        m_val = (
                            sub.attrib.get("name")
                            or sub.attrib.get("Name")
                            or sub.attrib.get("model")
                            or sub.text
                            or ""
                        ).strip()
                        _add_model(m_val)
                        s_val = (
                            sub.attrib.get("systemId")
                            or sub.attrib.get("SystemId")
                            or sub.attrib.get("id")
                            or ""
                        ).strip()
                        if s_val:
                            _add_sid(s_val)
                    elif stag in ("systemid", "smbiosid", "productid") and (sub.text or "").strip():
                        _add_sid((sub.text or "").strip())
            elif ctag in ("supportedoperatingsystems", "operatingsystems", "oslist"):
                for os_el in child:
                    os_txt = " ".join(
                        filter(
                            None,
                            [
                                os_el.attrib.get("name", ""),
                                os_el.attrib.get("osCode", ""),
                                os_el.attrib.get("version", ""),
                                os_el.attrib.get("arch", ""),
                                os_el.text or "",
                            ],
                        )
                    )
                    arch_val = (os_el.attrib.get("arch") or os_el.attrib.get("Architecture") or "").lower()
                    if arch_val and "86" in arch_val and "64" not in arch_val:
                        continue
                    os_fam, feat_rank = _extract_os_family_and_feature(os_txt)
                    if os_fam != "other":
                        os_list.append(os_fam)
                    score = (200 if os_fam == "win11" else (100 if os_fam == "win10" else 0)) + feat_rank
                    if score > best_os_rank:
                        best_os_rank = score
                        best_os = os_fam
                        best_feat = feat_rank
            elif ctag in ("model", "systemname") and (child.text or "").strip():
                _add_model((child.text or "").strip())
            elif ctag in ("systemid", "smbiosid") and (child.text or "").strip():
                _add_sid((child.text or "").strip())
            else:
                if (child.text or "").strip():
                    fields[ctag] = (child.text or "").strip()

        if "model" in fields and not models:
            _add_model(fields["model"])

        arch_attr = (fields.get("arch") or fields.get("architecture") or "").lower()
        if arch_attr and "86" in arch_attr and "64" not in arch_attr:
            is_x64 = False

        if not is_x64 or not models:
            continue

        if best_os == "other":
            os_hint = fields.get("os") or fields.get("osname") or fields.get("operatingsystem") or "win11"
            best_os, best_feat = _extract_os_family_and_feature(os_hint)
            if best_os == "other":
                best_os = "win11"
            os_list = [best_os]

        raw_url = (
            fields.get("downloadurl")
            or fields.get("url")
            or fields.get("filepath")
            or fields.get("path")
            or ""
        ).strip()
        if not raw_url:
            continue
        if not raw_url.startswith(("http://", "https://")):
            raw_url = f"{base_url}/{raw_url.lstrip('/')}"

        # Stroga validacija HTTPS allowlist-a (odbija HTTP i neovlašćene domene po paketu bez rušenja celog kataloga — IMP-106)
        try:
            dl_url = validate_oem_download_url(raw_url, allowed_hosts=allowed_hosts)
        except ValueError:
            continue

        pkg_name = (
            fields.get("packagename")
            or fields.get("filename")
            or os.path.basename(urllib.parse.urlparse(dl_url).path)
            or "fujitsu_driverpack.zip"
        )
        fmt = (fields.get("format") or fields.get("type") or os.path.splitext(pkg_name)[1].lstrip(".") or "zip").lower()
        try:
            size_bytes = int(fields.get("sizebytes") or fields.get("size") or 0)
        except (TypeError, ValueError):
            size_bytes = 0

        entries.append({
            "vendor": "fujitsu",
            "package_id": fields.get("packageid") or fields.get("ftsid") or fields.get("id") or pkg_name,
            "package_name": pkg_name,
            "version": fields.get("version") or "",
            "release_date": _parse_iso_or_us_date(
                fields.get("releasedate") or fields.get("datereleased") or fields.get("date") or ""
            ),
            "os": best_os,
            "os_feature_rank": best_feat,
            "os_supported": sorted(set(os_list)) or [best_os],
            "arch": "x64",
            "format": fmt,
            "download_url": dl_url,
            "size_bytes": size_bytes,
            "sha256": (fields.get("sha256") or "").lower(),
            "md5": (fields.get("md5") or "").lower(),
            "models": models,
            "system_ids": system_ids,
            "mtms": [],
        })

    return entries


class FujitsuOEMProvider(BaseOEMProvider):
    """
    OEM provajder za Fujitsu poslovne sisteme (LIFEBOOK, ESPRIMO, CELSIUS).
    """

    DESKUPDATE_CATALOG_URL = DESKUPDATE_CATALOG_URL
    SUPPORT_DOWNLOAD_PORTAL_URL = "https://support.ts.fujitsu.com/IndexDownload.asp"

    # Referentni metapodaci za poznate Fujitsu modele bez sintetičkih SoftwareGUID URL-ova (IMP-106).
    # Stvarni paketi stižu iz DeskUpdateCatalog.xml ili preko lokalnog DropInOEMProvider-a.
    KNOWN_MODELS: Dict[str, Dict[str, Any]] = {
        "LIFEBOOK_E5411": {
            "model_name": "FUJITSU LIFEBOOK E5411",
            "package_id": "FTS_LIFEBOOK_E5411_W11",
            "package_name": "FTS_DriverPack_LIFEBOOK_E5411_Win11_x64.zip",
            "version": "1.24.0",
            "release_date": "2024-11-10",
            "download_url": "",
            "support_page_url": SUPPORT_DOWNLOAD_PORTAL_URL,
            "dropin_hint": "<dropin_dir>/fujitsu/LIFEBOOK_E5411/*.zip",
            "size_bytes": 0,
            "bios_version": "2.18",
            "bios_url": "",
        },
        "LIFEBOOK_U7411": {
            "model_name": "FUJITSU LIFEBOOK U7411",
            "package_id": "FTS_LIFEBOOK_U7411_W11",
            "package_name": "FTS_DriverPack_LIFEBOOK_U7411_Win11_x64.zip",
            "version": "1.30.0",
            "release_date": "2024-12-05",
            "download_url": "",
            "support_page_url": SUPPORT_DOWNLOAD_PORTAL_URL,
            "dropin_hint": "<dropin_dir>/fujitsu/LIFEBOOK_U7411/*.zip",
            "size_bytes": 0,
            "bios_version": "2.22",
            "bios_url": "",
        },
        "LIFEBOOK_U7511": {
            "model_name": "FUJITSU LIFEBOOK U7511",
            "package_id": "FTS_LIFEBOOK_U7511_W11",
            "package_name": "FTS_DriverPack_LIFEBOOK_U7511_Win11_x64.zip",
            "version": "1.28.0",
            "release_date": "2024-11-18",
            "download_url": "",
            "support_page_url": SUPPORT_DOWNLOAD_PORTAL_URL,
            "dropin_hint": "<dropin_dir>/fujitsu/LIFEBOOK_U7511/*.zip",
            "size_bytes": 0,
            "bios_version": "2.20",
            "bios_url": "",
        },
        "ESPRIMO_D7011": {
            "model_name": "FUJITSU ESPRIMO D7011",
            "package_id": "FTS_ESPRIMO_D7011_W11",
            "package_name": "FTS_DriverPack_ESPRIMO_D7011_Win11_x64.zip",
            "version": "1.19.0",
            "release_date": "2024-10-22",
            "download_url": "",
            "support_page_url": SUPPORT_DOWNLOAD_PORTAL_URL,
            "dropin_hint": "<dropin_dir>/fujitsu/ESPRIMO_D7011/*.zip",
            "size_bytes": 0,
            "bios_version": "1.15",
            "bios_url": "",
        },
        "CELSIUS_H7510": {
            "model_name": "FUJITSU CELSIUS H7510",
            "package_id": "FTS_CELSIUS_H7510_W11",
            "package_name": "FTS_DriverPack_CELSIUS_H7510_Win11_x64.zip",
            "version": "1.40.0",
            "release_date": "2024-09-30",
            "download_url": "",
            "support_page_url": SUPPORT_DOWNLOAD_PORTAL_URL,
            "dropin_hint": "<dropin_dir>/fujitsu/CELSIUS_H7510/*.zip",
            "size_bytes": 0,
            "bios_version": "1.32",
            "bios_url": "",
        },
    }

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
                self.base_cache_dir = os.path.join(os.path.abspath(DRIVERS_CACHE_DIR), "oem", "fujitsu")
            except Exception:
                self.base_cache_dir = os.path.abspath(
                    os.path.join(os.path.dirname(__file__), "..", "..", "..", "storage", "drivers_cache", "oem", "fujitsu")
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

    @classmethod
    def get_builtin_catalog_entries(cls, dropin_dir: Optional[str] = None) -> List[Dict[str, Any]]:
        """
        Vraća isključivo verifikovane Fujitsu zapise (ili lokalne Drop-in pakete ako je zadat
        `dropin_dir`), bez sintetičkih `SoftwareGUID=lifebook-...` URL-ova (IMP-106).
        """
        out: List[Dict[str, Any]] = []
        if dropin_dir:
            from servishub.drivers.oem.dropin import DropInOEMProvider
            out.extend(DropInOEMProvider(vendor="fujitsu", dropin_dir=dropin_dir).scan_packages(vendor="fujitsu"))

        for km in cls.KNOWN_MODELS.values():
            raw_url = str(km.get("download_url") or "").strip()
            if not raw_url:
                continue
            try:
                url = validate_oem_download_url(raw_url)
            except ValueError:
                continue
            out.append({
                "vendor": "fujitsu",
                "package_id": km["package_id"],
                "package_name": km["package_name"],
                "version": km["version"],
                "release_date": km["release_date"],
                "os": "win11",
                "os_feature_rank": 222,
                "os_supported": ["win11"],
                "arch": "x64",
                "format": "zip",
                "download_url": url,
                "size_bytes": int(km.get("size_bytes") or 0),
                "sha256": "",
                "md5": "",
                "models": [km["model_name"], km["model_name"].replace("FUJITSU ", "")],
                "system_ids": [],
                "mtms": [],
            })
        return out

    @staticmethod
    def parse_deskupdate_xml(
        xml_bytes: bytes,
        allowed_hosts: Optional[Sequence[str]] = None,
    ) -> List[Dict[str, Any]]:
        """Parsira Fujitsu DeskUpdate XML uz XXE i URL allowlist zaštitu."""
        return parse_fujitsu_catalog_xml(xml_bytes, allowed_hosts=allowed_hosts)

    @classmethod
    def _match_known_model(cls, model: str) -> Optional[Dict[str, Any]]:
        q_tokens = _norm_model_tokens(model)
        if not q_tokens:
            return None
        q_set = set(q_tokens)
        best_km = None
        best_score = 0
        for km in cls.KNOWN_MODELS.values():
            c_tokens = _norm_model_tokens(km["model_name"])
            c_set = set(c_tokens)
            q_nums = {t for t in q_tokens if any(ch.isdigit() for ch in t)}
            c_nums = {t for t in c_tokens if any(ch.isdigit() for ch in t)}
            if q_nums and c_nums and q_nums != c_nums:
                continue
            if q_set == c_set:
                score = 100
            elif q_set <= c_set:
                score = 85
            elif c_set <= q_set:
                score = 80
            else:
                continue
            if score > best_score:
                best_score = score
                best_km = km
        return best_km

    @staticmethod
    def _normalize_serial(serial: str) -> str:
        if not serial:
            return ""
        return re.sub(r"[^A-Z0-9_-]", "", str(serial).strip().upper())

    def get_warranty_info(self, serial: str, model: str = "") -> Dict[str, Any]:
        """Vraća keširanu Fujitsu garanciju ili graceful fallback."""
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
            "status_label": "Nepoznato (Fujitsu API nije dostupan offline)",
            "service_level": "N/A",
            "start_date": "",
            "end_date": "",
            "days_remaining": 0,
            "raw_data": {
                "serial": clean_sn,
                "model": model,
                "note": "Fujitsu garancija nije dostupna bez aktivnog partner API-ja",
            },
        }

    def resolve_driver_pack(self, model: str, os_ver: str = "win11-x64") -> Dict[str, Any]:
        """Pronalazi Fujitsu DeskUpdate paket u kešu ili u referentnoj bazi."""
        clean_model = str(model or "").strip()
        if not clean_model:
            return {
                "status": "not_found",
                "source": "fujitsu_deskupdate",
                "package_name": "",
                "download_url": "",
                "local_path": "",
                "size_bytes": 0,
            }

        slug = re.sub(r"[^A-Za-z0-9_-]+", "_", clean_model).strip("_")
        model_dir = os.path.join(self.base_cache_dir, slug)

        with self.lock:
            if os.path.isdir(model_dir):
                archives = [
                    f for f in sorted(os.listdir(model_dir))
                    if f.lower().endswith((".zip", ".cab", ".exe")) and not f.endswith(".tmp")
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

        # 2. Čist DropInOEMProvider fallback pre bilo kakve referentne baze (IMP-106)
        from servishub.drivers.oem.dropin import DropInOEMProvider
        dropin_hits = DropInOEMProvider(vendor="fujitsu", dropin_dir=self.dropin_dir).scan_packages(
            vendor="fujitsu",
            model=clean_model,
            os_ver=os_ver,
        )
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

        km = self._match_known_model(clean_model)
        if not km:
            return {
                "status": "not_found",
                "source": "fujitsu_deskupdate",
                "package_name": "",
                "download_url": "",
                "local_path": "",
                "size_bytes": 0,
            }

        dl_url = str(km.get("download_url") or "").strip()
        if not dl_url:
            return {
                "status": "not_found",
                "source": "fujitsu_dropin_required",
                "package_name": km["package_name"],
                "download_url": "",
                "support_page_url": km.get("support_page_url", self.SUPPORT_DOWNLOAD_PORTAL_URL),
                "dropin_required": True,
                "dropin_hint": km.get("dropin_hint", ""),
                "local_path": "",
                "size_bytes": 0,
            }

        with self.lock:
            self._atomic_write_json(
                os.path.join(model_dir, "package_meta.json"),
                {
                    "model": clean_model,
                    "os_ver": os_ver,
                    "package_name": km["package_name"],
                    "download_url": dl_url,
                    "size_bytes": int(km.get("size_bytes") or 0),
                    "source": "fujitsu_deskupdate_known",
                    "resolved_at": datetime.now().isoformat(),
                },
            )

        return {
            "status": "ok",
            "source": "fujitsu_deskupdate_known",
            "package_name": km["package_name"],
            "download_url": dl_url,
            "local_path": "",
            "size_bytes": int(km.get("size_bytes") or 0),
        }

    def check_bios_update(self, model: str, current_bios: str = "") -> Dict[str, Any]:
        """Proverava dostupnost novije verzije BIOS-a za Fujitsu model."""
        km = self._match_known_model(str(model or ""))
        if not km:
            return {
                "update_available": False,
                "latest_version": "",
                "current_version": current_bios,
                "download_url": "",
                "release_date": "",
            }
        latest = str(km.get("bios_version") or "")
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
            "download_url": km.get("bios_url") or km.get("support_page_url") or "",
            "release_date": km.get("release_date", ""),
        }

    def lookup_keyboard_spec(
        self,
        serial: str = "",
        model: str = "",
        product_number: str = "",
    ) -> Dict[str, Any]:
        """Određuje osnovni raspored tastature za Fujitsu LIFEBOOK/ESPRIMO/CELSIUS."""
        up = f"{model} {product_number}".upper()
        is_desktop = "ESPRIMO" in up
        has_numpad = is_desktop or "U75" in up or "A35" in up or "H75" in up
        return build_keyboard_spec(
            status="ok",
            source="fujitsu_series",
            confidence="medium",
            base_layout=make_keyboard_layout_name(has_numpad=has_numpad, is_iso=True, is_desktop=is_desktop),
            has_numpad=has_numpad,
            is_iso=True,
            raw_data={"serial": serial, "model": model, "product_number": product_number},
        )
