"""
servishub.predictive_cache
==========================
Predictive Cache & Pre-Arrival Pre-Warming podsistem za ServisHub.
Omogućava da se pre dolaska računara u servis (na osnovu serijskog broja, SKU-a ili modela)
unapred povuku i keširaju:
- OEM BOM i hardverske specifikacije (hardware_spec.py)
- Fabrička garancija, OEM SCCM paket drajvera i BIOS info (drivers/oem/)
- Pojedinačni .cab drajveri na osnovu naučenog Model DNA HWID otiska (model_profiles.py + DriverCacheManager)
- Provera spremnosti Windows 11 .msu / .cab zakrpi za ciljni OS build (api/updates.py)
"""

import json
import os
import threading
import uuid
from datetime import datetime
from typing import Any, Dict, List, Optional

from servishub.api.updates import extract_os_build, get_model_pack_status, scan_updates_directory
from servishub.config import active_config
from servishub.drivers.oem import get_oem_provider
from servishub.hardware_spec import resolve_hardware_spec
from servishub.model_profiles import list_model_profiles, load_model_profile, update_model_dna


def _infer_vendor_from_inputs(manufacturer: str, model: str) -> str:
    combined = f"{manufacturer} {model}".upper()
    if any(k in combined for k in ("LENOVO", "THINKPAD", "IDEAPAD", "THINKCENTRE", "YOGA", "LEGION")):
        return "Lenovo"
    if any(k in combined for k in ("DELL", "LATITUDE", "OPTIPLEX", "PRECISION", "XPS", "VOSTRO", "INSPIRON")):
        return "Dell"
    if any(k in combined for k in ("HP", "HEWLETT", "ELITEBOOK", "PROBOOK", "ZBOOK", "ELITEDESK", "PRODESK")):
        return "HP"
    return manufacturer.strip()


class PredictiveCacheManager:
    """Thread-safe orkestrator za Pre-Arrival Pre-Warming i praćenje spremnosti keša."""

    def __init__(self, queue_file: Optional[str] = None):
        self.lock = threading.Lock()
        self.jobs: List[Dict[str, Any]] = []
        self._queue_file = queue_file
        self._load_persisted_jobs()

    def _resolve_queue_path(self, config: Any = None) -> str:
        if self._queue_file:
            return self._queue_file
        storage_dir = getattr(config, "storage_dir", None) if config else None
        if not storage_dir:
            storage_dir = os.path.join(active_config.base_dir, "storage")
        os.makedirs(storage_dir, exist_ok=True)
        return os.path.join(storage_dir, "predictive_queue.json")

    def _load_persisted_jobs(self) -> None:
        if not self._queue_file or not os.path.isfile(self._queue_file):
            return
        try:
            with open(self._queue_file, "r", encoding="utf-8") as f:
                data = json.load(f)
            if isinstance(data, list):
                self.jobs = data[-100:]
        except Exception:
            pass

    def _save_persisted_jobs(self, config: Any = None) -> None:
        try:
            path = self._resolve_queue_path(config=config)
            tmp_path = f"{path}.tmp"
            with open(tmp_path, "w", encoding="utf-8") as f:
                json.dump(self.jobs[-100:], f, indent=2, ensure_ascii=False)
            os.replace(tmp_path, path)
        except Exception:
            pass

    def prewarm_device(
        self,
        manufacturer: str = "",
        model: str = "",
        serial_number: str = "",
        sku: str = "",
        os_build: str = "24H2",
        hwids: Optional[List[str]] = None,
        driver_cache_mgr: Any = None,
        ms_catalog_scraper: Any = None,
        config: Any = None,
    ) -> Dict[str, Any]:
        """
        Izvršava prediktivno zagrevanje keša za najavljeni uređaj.
        """
        clean_mfg = (manufacturer or "").strip()
        clean_model = (model or "").strip()
        clean_sn = (serial_number or "").strip()
        clean_sku = (sku or "").strip()
        clean_hwids_input = [str(h).strip() for h in (hwids or []) if str(h).strip()]

        if not (clean_model or clean_sn or clean_sku or clean_hwids_input):
            raise ValueError("Potrebno je zadati bar jedan identifikator uređaja (model, serial_number, sku ili hwids).")

        norm_build = extract_os_build(os_build) or (str(os_build).strip() if os_build else "26100")

        # Korak B1: OEM Garancija (može otkriti model ako nije prosleđen)
        inferred_vendor = _infer_vendor_from_inputs(clean_mfg, clean_model)
        provider = get_oem_provider(inferred_vendor) if inferred_vendor else None
        warranty_info = None
        warranty_cached = False
        oem_driver_pack = None
        bios_info = None

        if provider and clean_sn:
            try:
                warranty_info = provider.get_warranty_info(clean_sn, clean_model)
                if isinstance(warranty_info, dict) and warranty_info.get("status") not in ("error",):
                    warranty_cached = True
                    if not clean_model and warranty_info.get("model"):
                        cand_model = str(warranty_info.get("model")).strip()
                        if cand_model.lower() not in ("unknown", "n/a", ""):
                            clean_model = cand_model
            except Exception:
                warranty_info = None

        # Korak A: OEM BOM & Hardware Spec
        hw_spec = {}
        bom_cached = False
        if clean_sn or clean_sku or clean_model:
            try:
                hw_spec = resolve_hardware_spec(
                    manufacturer=inferred_vendor or clean_mfg,
                    model=clean_model,
                    serial=clean_sn,
                    sku=clean_sku,
                )
                bom_cached = bool(hw_spec.get("bom_loaded") or hw_spec.get("source"))
            except Exception:
                hw_spec = {}

        # Korak B2: OEM Driver Pack & BIOS
        if provider and clean_model:
            try:
                oem_driver_pack = provider.resolve_driver_pack(clean_model)
            except Exception:
                oem_driver_pack = None
            try:
                bios_info = provider.check_bios_update(clean_model)
            except Exception:
                bios_info = None

        # Korak C: Model DNA & HWID Driver Pre-Warming
        profile = None
        merged_hwids: List[str] = []
        seen_hw = set()

        if clean_model:
            if clean_hwids_input:
                profile = update_model_dna(
                    clean_model,
                    clean_hwids_input,
                    manufacturer=inferred_vendor or clean_mfg,
                    os_build=norm_build,
                    by="predictive_prewarm",
                )
            else:
                profile = load_model_profile(clean_model)
            for h in profile.get("known_hwids", []) or []:
                hl = h.strip().lower()
                if hl and hl not in seen_hw:
                    seen_hw.add(hl)
                    merged_hwids.append(h.strip())
        else:
            for h in clean_hwids_input:
                hl = h.strip().lower()
                if hl and hl not in seen_hw:
                    seen_hw.add(hl)
                    merged_hwids.append(h.strip())

        drivers_cached_hits = 0
        drivers_downloaded = 0
        drivers_missing = 0
        driver_details: List[Dict[str, Any]] = []

        for hwid in merged_hwids:
            if driver_cache_mgr is not None:
                cached = driver_cache_mgr.get_cached(hwid)
                if cached:
                    drivers_cached_hits += 1
                    driver_details.append({
                        "hwid": hwid,
                        "status": "cached",
                        "driver_id": cached.get("driver_id", ""),
                        "title": cached.get("title", ""),
                    })
                    continue

                if ms_catalog_scraper is not None:
                    try:
                        resolved = ms_catalog_scraper.resolve_driver(hwid)
                        if resolved and resolved.get("download_url"):
                            cab_data = ms_catalog_scraper.download_cab(resolved["download_url"])
                            aliases = [resolved["matched_query"]] if resolved.get("matched_query") else None
                            saved = driver_cache_mgr.save_cache(hwid, cab_data, resolved, alias_hwids=aliases)
                            drivers_downloaded += 1
                            driver_details.append({
                                "hwid": hwid,
                                "status": "downloaded",
                                "driver_id": saved.get("driver_id", ""),
                                "title": saved.get("title", ""),
                            })
                            continue
                    except Exception:
                        pass

            drivers_missing += 1
            driver_details.append({"hwid": hwid, "status": "missing"})

        model_pack_available = False
        model_pack_info = None
        if clean_model:
            try:
                model_pack_info = get_model_pack_status(clean_model, active_config=config)
                model_pack_available = bool(model_pack_info.get("available"))
            except Exception:
                model_pack_info = None

        # Korak D: Windows Update Readiness
        updates_dir = getattr(config, "updates_dir", None) if config else None
        wu_status = scan_updates_directory(directory=updates_dir, os_build=norm_build)
        updates_count = wu_status.total_packages
        kb_list = list(wu_status.kb_list)
        updates_ready = updates_count > 0

        # Readiness Score (0 - 100%)
        score = 0.0
        total_hw = len(merged_hwids)
        has_oem_pack = bool(isinstance(oem_driver_pack, dict) and oem_driver_pack.get("available"))
        if total_hw > 0:
            covered = drivers_cached_hits + drivers_downloaded
            score += (covered / float(total_hw)) * 40.0
            if model_pack_available or has_oem_pack:
                score += 10.0
        elif model_pack_available or has_oem_pack:
            score += 45.0

        if updates_ready:
            score += 30.0
        if bom_cached:
            score += 10.0
        if warranty_cached or (isinstance(bios_info, dict) and bios_info.get("latest_version")):
            score += 10.0

        readiness_score_pct = min(100, max(0, int(round(score))))

        job_record = {
            "status": "ok",
            "job_id": f"pw_{uuid.uuid4().hex[:12]}",
            "created_at": datetime.now().isoformat(),
            "manufacturer": inferred_vendor or clean_mfg,
            "model": clean_model,
            "serial_number": clean_sn,
            "sku": clean_sku,
            "os_build": norm_build,
            "readiness_score_pct": readiness_score_pct,
            "bom_cached": bom_cached,
            "warranty_cached": warranty_cached,
            "warranty": warranty_info,
            "hw_spec": hw_spec,
            "oem_driver_pack": oem_driver_pack,
            "bios_info": bios_info,
            "model_pack_available": model_pack_available,
            "model_pack": model_pack_info,
            "drivers_total_hwids": total_hw,
            "drivers_cached_hits": drivers_cached_hits,
            "drivers_downloaded": drivers_downloaded,
            "drivers_missing": drivers_missing,
            "driver_details": driver_details,
            "updates_ready": updates_ready,
            "updates_available_count": updates_count,
            "kb_list": kb_list,
        }

        with self.lock:
            self.jobs.append(job_record)
            if len(self.jobs) > 100:
                self.jobs = self.jobs[-100:]
            self._save_persisted_jobs(config=config)

        return dict(job_record)

    def get_status(self, config: Any = None) -> Dict[str, Any]:
        """Vraća status svih pre-warm zadataka, poznate Model DNA profile i stanje Win11 keša."""
        with self.lock:
            jobs_snapshot = list(reversed(self.jobs[-50:]))
            total_jobs = len(self.jobs)

        updates_dir = getattr(config, "updates_dir", None) if config else None
        wu_status = scan_updates_directory(directory=updates_dir)
        wu_dict = wu_status.model_dump() if hasattr(wu_status, "model_dump") else wu_status.to_dict()

        return {
            "status": "ok",
            "total_jobs": total_jobs,
            "jobs": jobs_snapshot,
            "known_models": list_model_profiles(),
            "updates_cache": {
                "available": wu_dict.get("available", False),
                "total_packages": wu_dict.get("total_packages", 0),
                "total_size_mb": wu_dict.get("total_size_mb", 0.0),
                "kb_list": wu_dict.get("kb_list", []),
            },
        }


PREDICTIVE_CACHE_MGR = PredictiveCacheManager()
