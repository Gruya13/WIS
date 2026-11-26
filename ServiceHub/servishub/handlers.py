"""
servishub.handlers
==================
REST API ruter i HTTP handler za ServisHub.
"""

import os
import re
import sys
import time
import hmac
import json
import uuid
import shutil
import tarfile
import threading
from datetime import datetime
from concurrent.futures import ThreadPoolExecutor
from urllib.parse import urlparse, parse_qs, unquote, quote
from http.server import BaseHTTPRequestHandler

from servishub.config import active_config
from servishub.telemetry import FLEET_STORE, is_placeholder_hw_value, merge_hw_dicts
from servishub.drivers.cache import sanitize_id
from servishub.drivers.hwid import generate_hwid_candidates
from servishub.drivers.version import is_catalog_driver_newer
from servishub.reports import (
    generate_markdown_report,
    save_device_report,
    get_device_history,
    save_agy_session,
    register_new_device,
    save_device_checkpoint,
    diagnostics_contract,
    is_enospc_error,
    log_enospc_rate_limited,
    is_partial_hwtest_payload,
    merge_app_statuses,
    merge_report_payloads,
    merge_autopilot_status,
    merge_image_profile,
    extract_image_profile,
)
from servishub.version import VERSION, RELEASE_STATUS
from servishub.web.dashboard import HTML_DASHBOARD
from servishub.wis import (
    WisIsoManager,
    WisUsbEngine,
    list_removable_usb_devices,
)
from servishub.drivers.oem import get_oem_provider

MAX_JSON_BODY_BYTES = 16 * 1024 * 1024  # 16 MiB limit za JSON POST tela (SEC-WIS-004)
MAX_PACKAGE_UPLOAD_BYTES = 6 * 1024 * 1024 * 1024  # 6 GiB limit za upload zakrpi i .tar paketa

# Thread-safe globalno stanje za praćenje WIS USB kreiranja
WIS_BURN_LOCK = threading.Lock()
WIS_BURN_STATE = {
    "is_running": False,
    "phase": "idle",
    "percent": 0,
    "message": "Nema aktivnog procesa narezivanja.",
    "device_path": None,
    "error": None,
    "completed": False,
}
WIS_BURN_SUBSCRIBERS = []  # Lista tuple-ova (queue: list, condition: threading.Condition)


def _update_wis_burn_progress(phase: str, percent: int, message: str, **kwargs):
    """Ažurira globalno stanje narezivanja USB-a i obaveštava sve aktivne SSE pretplatnike."""
    with WIS_BURN_LOCK:
        WIS_BURN_STATE.update({
            "phase": phase,
            "percent": percent,
            "message": message,
            **kwargs,
        })
        if phase in ("completed", "error"):
            WIS_BURN_STATE["is_running"] = False
            WIS_BURN_STATE["completed"] = (phase == "completed")
        state_copy = dict(WIS_BURN_STATE)
        for q, cond in list(WIS_BURN_SUBSCRIBERS):
            with cond:
                q.append(state_copy)
                cond.notify_all()


def compute_disk_usage_info(cfg=None) -> dict:
    """
    Računa zauzeće diska nad LOG_DIR / DATA_DIR (logs_dir / storage_dir) pomoću shutil.disk_usage
    i vraća kanonsku strukturu za /api/status i LOW_DISK_WARNING alert (IMP-36).
    """
    target_cfg = cfg or active_config
    candidate_paths = []
    for attr in ("logs_dir", "reports_dir", "live_logs_dir", "storage_dir", "base_dir"):
        p = getattr(target_cfg, attr, None)
        if p and isinstance(p, str):
            candidate_paths.append(p)
    candidate_paths.append(os.getcwd())

    best_usage = None
    best_path = ""
    for p in candidate_paths:
        check_p = p
        while check_p and not os.path.exists(check_p):
            parent = os.path.dirname(check_p)
            if parent == check_p:
                break
            check_p = parent
        if not check_p or not os.path.exists(check_p):
            continue
        try:
            usage = shutil.disk_usage(check_p)
            if best_usage is None or usage.free < best_usage.free:
                best_usage = usage
                best_path = check_p
        except OSError:
            continue

    if best_usage is None:
        return {
            "path": "",
            "free_bytes": 0,
            "total_bytes": 0,
            "used_bytes": 0,
            "used_pct": 0.0,
            "free_gb": 0.0,
            "low_disk_warning": True,
            "critical_disk_warning": True,
            "status_message": "KRITIČNO: Nije moguće očitati slobodan prostor na disku (LOW_DISK_WARNING).",
            "message": "KRITIČNO: Nije moguće očitati slobodan prostor na disku (LOW_DISK_WARNING).",
        }

    total_b = int(best_usage.total)
    used_b = int(best_usage.used)
    free_b = int(best_usage.free)
    used_pct = round((used_b / float(total_b)) * 100.0, 1) if total_b > 0 else 0.0
    free_gb = round(free_b / (1024.0 ** 3), 2)
    low_warn = bool(free_gb < 10.0)
    crit_warn = bool(free_gb < 5.0)

    if crit_warn:
        msg = (
            f"KRITIČNO (CRITICAL_DISK_WARNING / LOW_DISK_WARNING): Slobodan prostor na disku je "
            f"samo {free_gb} GB ({used_pct}% zauzeto, < 5 GB)! Hitno oslobodite prostor na NAS-u."
        )
    elif low_warn:
        msg = (
            f"UPOZORENJE (LOW_DISK_WARNING): Slobodan prostor na disku je pao ispod 10 GB "
            f"({free_gb} GB slobodno, {used_pct}% zauzeto)."
        )
    else:
        msg = f"OK ({free_gb} GB slobodno, {used_pct}% zauzeto)"

    return {
        "path": best_path,
        "free_bytes": free_b,
        "total_bytes": total_b,
        "used_bytes": used_b,
        "used_pct": used_pct,
        "free_gb": free_gb,
        "low_disk_warning": low_warn,
        "critical_disk_warning": crit_warn,
        "status_message": msg,
        "message": msg,
    }


def reset_wis_burn_state():
    """Resetuje globalno stanje narezivanja i prazni listu pretplatnika (korisno za testove)."""
    with WIS_BURN_LOCK:
        WIS_BURN_STATE.clear()
        WIS_BURN_STATE.update({
            "is_running": False,
            "phase": "idle",
            "percent": 0,
            "message": "Nema aktivnog procesa narezivanja.",
            "device_path": None,
            "error": None,
            "completed": False,
        })
        WIS_BURN_SUBSCRIBERS.clear()


def _run_wis_burn_process(device_path: str, iso_path: str = None, wis_dir: str = None):
    """
    Pozadinski radnik koji formatira USB disk i raspakuje WIS ISO sliku.
    """
    try:
        _update_wis_burn_progress(
            phase="preparing",
            percent=0,
            message=f"Priprema za narezivanje na {device_path}...",
            device_path=device_path,
        )

        iso_mgr = WisIsoManager(wis_dir=wis_dir)
        iso_info = iso_mgr.get_iso_info(iso_path)
        if not iso_info.get("available") or not iso_info.get("path"):
            raise FileNotFoundError("WIS ISO datoteka nije pronađena na serveru.")
        target_iso = iso_info["path"]

        usb_engine = WisUsbEngine()

        def on_format_progress(p: dict):
            raw_percent = p.get("percent", 0)
            scaled = int(raw_percent * 0.4)
            _update_wis_burn_progress(
                phase=p.get("phase", "formatting"),
                percent=scaled,
                message=f"[Format] {p.get('message', '')}",
                device_path=device_path,
            )

        format_res = usb_engine.format_uefi_usb(
            device_path=device_path,
            confirm_wipe=True,
            progress_callback=on_format_progress,
        )

        target_part = format_res.get("ntfs_partition") or format_res.get("fat32_partition") or device_path

        def on_extract_progress(p: dict):
            raw_percent = p.get("percent", 0)
            scaled = 40 + int(raw_percent * 0.58)
            _update_wis_burn_progress(
                phase=p.get("phase", "extracting"),
                percent=scaled,
                message=f"[Ekstrakcija] {p.get('message', '')}",
                device_path=device_path,
            )

        iso_mgr.extract_iso_to_usb(
            iso_path=target_iso,
            target_usb_path=target_part,
            progress_callback=on_extract_progress,
        )

        _update_wis_burn_progress(
            phase="completed",
            percent=100,
            message=f"UEFI USB instalater je uspešno kreiran na {device_path}.",
            device_path=device_path,
        )
    except Exception as ex:
        _update_wis_burn_progress(
            phase="error",
            percent=WIS_BURN_STATE.get("percent", 0),
            message=f"Greška tokom narezivanja USB-a: {ex}",
            error=str(ex),
            device_path=device_path,
        )


class ServisHubHandler(BaseHTTPRequestHandler):
    """
    Glavni HTTP Request Handler za ServisHub.
    Rukuje svim REST API rutama, SSE strimovima, preuzimanjem paketa i Web Dashboard-om.
    """
    server_version = "ServisHub/" + VERSION

    # Zavisnosti koje se mogu injektovati ili podrazumevano očitati
    fleet_store = FLEET_STORE
    config = active_config
    payload_mgr = None
    driver_cache_mgr = None
    driver_pool_mgr = None
    ms_catalog_scraper = None
    predictive_cache_mgr = None
    start_time = time.time()
    reset_wis_burn_state = staticmethod(reset_wis_burn_state)
    _driver_dl_lock = threading.Lock()
    _driver_dl_jobs = {}  # (cache_scope, token) -> aktivan ili sveže završen pozadinski download drajvera
    _driver_pool_lock = threading.Lock()
    _driver_pool_instances = {}

    def _get_predictive_mgr(self):
        if self.predictive_cache_mgr is not None:
            return self.predictive_cache_mgr
        from servishub.predictive_cache import PREDICTIVE_CACHE_MGR
        return PREDICTIVE_CACHE_MGR

    def _get_driver_pool_mgr(self):
        if self.driver_pool_mgr is not None:
            return self.driver_pool_mgr
        pool_dir = getattr(self.config, "driver_pool_dir", None) if self.config else None
        if not pool_dir:
            storage_dir = getattr(self.config, "storage_dir", None) if self.config else None
            pool_dir = os.path.join(storage_dir or active_config.storage_dir, "driver_pool")
        abs_pool = os.path.abspath(pool_dir)
        with ServisHubHandler._driver_pool_lock:
            mgr = ServisHubHandler._driver_pool_instances.get(abs_pool)
            if mgr is None:
                from servishub.drivers.pool import DriverPoolManager
                mgr = DriverPoolManager(
                    pool_dir=abs_pool,
                    max_bundles_gb=getattr(self.config, "max_bundles_gb", None) if self.config else None,
                    bundle_ttl_hours=getattr(self.config, "bundle_ttl_hours", None) if self.config else None,
                    delete_ingested_archives=getattr(self.config, "delete_ingested_archives", None) if self.config else None,
                    oem_catalog_stale_hours=getattr(self.config, "oem_catalog_stale_hours", None) if self.config else None,
                )
                ServisHubHandler._driver_pool_instances[abs_pool] = mgr
            return mgr

    def log_message(self, format, *args):
        # Isključujemo bučno logovanje svakog pojedinačnog GET/POST zahteva
        return

    def _send_security_headers(self):
        self.send_header("X-Content-Type-Options", "nosniff")
        self.send_header("X-Frame-Options", "SAMEORIGIN")
        self.send_header("Referrer-Policy", "no-referrer")

    def _is_unauthenticated_lan_allowed(self) -> bool:
        if self.config and getattr(self.config, "allow_unauthenticated_lan", False):
            return True
        return os.environ.get("SERVIS_HUB_ALLOW_UNAUTHENTICATED_LAN", "").strip().lower() in ("1", "true", "yes")

    def _get_cors_origins(self) -> list[str]:
        origins = getattr(self.config, "cors_origins", None) if self.config else None
        if origins is None:
            raw = os.environ.get("SERVIS_HUB_CORS_ORIGINS", "")
            origins = [x.strip() for x in raw.split(",") if x.strip()]
        return list(origins or [])

    def _send_cors_headers(self):
        origins = self._get_cors_origins()
        if "*" in origins:
            self.send_header("Access-Control-Allow-Origin", "*")
            return
        req_origin = (self.headers.get("Origin") or "").strip() if getattr(self, "headers", None) else ""
        if req_origin and req_origin in origins:
            self.send_header("Access-Control-Allow-Origin", req_origin)
            self.send_header("Vary", "Origin")

    def _require_admin_token_if_configured(self) -> bool:
        expected = getattr(self.config, "admin_token", None) if self.config else None
        if not expected:
            expected = os.environ.get("SERVIS_HUB_ADMIN_TOKEN", "").strip()
        if not expected:
            if self._is_unauthenticated_lan_allowed():
                return True
            self._send_json({
                "error": "Administrativna ruta je onemogućena (fail-closed): postavite SERVIS_HUB_ADMIN_TOKEN ili eksplicitno SERVIS_HUB_ALLOW_UNAUTHENTICATED_LAN=1."
            }, 503)
            return False
        provided_candidates = []
        admin_hdr = (self.headers.get("X-ServisHub-Admin-Token") or "").strip()
        if admin_hdr:
            provided_candidates.append(admin_hdr)
        hub_hdr = (self.headers.get("X-ServisHub-Token") or "").strip()
        if hub_hdr:
            provided_candidates.append(hub_hdr)
        auth_header = (self.headers.get("Authorization") or "").strip()
        if auth_header.lower().startswith("bearer "):
            provided_candidates.append(auth_header[7:].strip())
        for provided in provided_candidates:
            if provided and hmac.compare_digest(provided, expected):
                return True
        self._send_json({"error": "Neautorizovan pristup. Potreban je validan admin token."}, 401)
        return False

    _require_admin_token = _require_admin_token_if_configured

    def _require_client_upload_token(self) -> bool:
        client_expected = getattr(self.config, "client_token", None) if self.config else None
        if not client_expected:
            client_expected = os.environ.get("SERVIS_HUB_CLIENT_TOKEN", "").strip()
        admin_expected = getattr(self.config, "admin_token", None) if self.config else None
        if not admin_expected:
            admin_expected = os.environ.get("SERVIS_HUB_ADMIN_TOKEN", "").strip()

        if not client_expected and not admin_expected:
            if self._is_unauthenticated_lan_allowed():
                return True
            self._send_json({
                "error": "Upload ruta je onemogućena (fail-closed): postavite SERVIS_HUB_CLIENT_TOKEN ili SERVIS_HUB_ADMIN_TOKEN, ili SERVIS_HUB_ALLOW_UNAUTHENTICATED_LAN=1."
            }, 503)
            return False

        provided_candidates = []
        client_hdr = (self.headers.get("X-ServisHub-Client-Token") or "").strip()
        if client_hdr:
            provided_candidates.append(client_hdr)
        admin_hdr = (self.headers.get("X-ServisHub-Admin-Token") or "").strip()
        if admin_hdr:
            provided_candidates.append(admin_hdr)
        hub_hdr = (self.headers.get("X-ServisHub-Token") or "").strip()
        if hub_hdr:
            provided_candidates.append(hub_hdr)
        auth_hdr = (self.headers.get("Authorization") or "").strip()
        if auth_hdr.lower().startswith("bearer "):
            provided_candidates.append(auth_hdr[7:].strip())

        for provided in provided_candidates:
            if client_expected and hmac.compare_digest(provided, client_expected):
                return True
            if admin_expected and hmac.compare_digest(provided, admin_expected):
                return True

        self._send_json({"error": "Neautorizovan upload. Potreban je validan klijentski ili admin token."}, 401)
        return False

    def _send_json(self, data, code=200):
        body = json.dumps(data, ensure_ascii=False).encode("utf-8")
        self.send_response(code)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self._send_cors_headers()
        self.send_header("Access-Control-Allow-Methods", "GET, POST, OPTIONS")
        self.send_header("Access-Control-Allow-Headers", "Content-Type, X-Payload-Version, X-WIS-Version, X-WIS-SHA256, X-Driver-ID, X-Client-Timestamp, X-Update-Filename, X-Update-KB, X-OS-Build, X-Model-Name, X-Model-Filename, X-ServisHub-Token, X-ServisHub-Admin-Token, X-ServisHub-Client-Token, Authorization")
        self._send_security_headers()
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def _stream_file(self, file_path, filename, content_type="application/octet-stream", extra_headers=None):
        file_size = os.path.getsize(file_path)
        self.send_response(200)
        self.send_header("Content-Type", content_type)
        self.send_header("Content-Disposition", f'attachment; filename="{filename}"')
        self.send_header("Content-Length", str(file_size))
        self._send_cors_headers()
        self._send_security_headers()
        if extra_headers:
            for k, v in extra_headers.items():
                if v is not None:
                    self.send_header(k, str(v))
        self.end_headers()

        with open(file_path, "rb") as f:
            while True:
                chunk = f.read(65536)
                if not chunk:
                    break
                self.wfile.write(chunk)
        self.wfile.flush()

    @classmethod
    def _prune_driver_dl_jobs_locked(cls):
        now = time.time()
        expired_keys = [
            k for k, j in cls._driver_dl_jobs.items()
            if j.get("state") in ("ready", "error") and (now - float(j.get("finished_at") or now)) > 120.0
        ]
        for k in expired_keys:
            cls._driver_dl_jobs.pop(k, None)

    @staticmethod
    def _build_ready_driver_entry(hwid_str, drv_info, source="catalog_download"):
        drv_id = (drv_info or {}).get("driver_id", "")
        return {
            "hwid": hwid_str,
            "status": "ready",
            "driver_id": drv_id,
            "download_url": f"/api/drivers/download?id={drv_id}",
            "title": (drv_info or {}).get("title", ""),
            "version": (drv_info or {}).get("version", ""),
            "date": (drv_info or {}).get("date", ""),
            "device_class": (drv_info or {}).get("device_class", ""),
            "size_bytes": (drv_info or {}).get("size_bytes", 0),
            "sha256": (drv_info or {}).get("sha256", ""),
            "source": source,
        }

    def _start_or_join_driver_download_job(self, cache_scope, hwid_str, clean_hwids, resolved):
        cab_url = resolved.get("download_url")
        aliases = list(dict.fromkeys(clean_hwids + ([str(resolved.get("matched_query")).strip()] if resolved.get("matched_query") else [])))
        cache_mgr = self.driver_cache_mgr
        scraper = self.ms_catalog_scraper
        pool_mgr = self._get_driver_pool_mgr()

        with ServisHubHandler._driver_dl_lock:
            self._prune_driver_dl_jobs_locked()
            existing = ServisHubHandler._driver_dl_jobs.get((cache_scope, hwid_str.upper()))
            if not existing and cab_url:
                existing = ServisHubHandler._driver_dl_jobs.get((cache_scope, f"URL:{cab_url}"))
            if existing and existing.get("state") == "downloading":
                return existing

            job = {
                "job_id": uuid.uuid4().hex[:12],
                "hwid": hwid_str,
                "cab_url": cab_url,
                "title": resolved.get("title", ""),
                "event": threading.Event(),
                "state": "downloading",
                "saved": None,
                "error": None,
                "finished_at": None,
            }
            for alias in aliases:
                if alias:
                    ServisHubHandler._driver_dl_jobs[(cache_scope, alias.upper())] = job
            if cab_url:
                ServisHubHandler._driver_dl_jobs[(cache_scope, f"URL:{cab_url}")] = job

            def _worker():
                try:
                    cab_data = scraper.download_cab(cab_url)
                    saved = cache_mgr.save_cache(hwid_str, cab_data, resolved, alias_hwids=aliases) if cache_mgr else resolved
                    if pool_mgr is not None and not getattr(cache_mgr, "on_driver_cached", None):
                        cab_fp = saved.get("cab_path") if isinstance(saved, dict) else None
                        if cab_fp and os.path.isfile(cab_fp):
                            def _bg_pool_cab():
                                try:
                                    pool_mgr.ingest_archive(cab_fp, source="ms_catalog")
                                except Exception:
                                    pass
                            threading.Thread(target=_bg_pool_cab, daemon=True).start()
                    with ServisHubHandler._driver_dl_lock:
                        job["saved"] = saved
                        job["state"] = "ready"
                        job["finished_at"] = time.time()
                    print(f"  [+] [RESOLVED & CACHED] {hwid_str} -> {saved.get('title')} ({saved.get('size_bytes')} B)")
                except Exception as ex:
                    print(f"  [!] [ERROR] Greška pri preuzimanju drajvera sa Microsoft CDN-a: {ex}", file=sys.stderr)
                    with ServisHubHandler._driver_dl_lock:
                        job["error"] = str(ex)
                        job["state"] = "error"
                        job["finished_at"] = time.time()
                finally:
                    job["event"].set()

            threading.Thread(target=_worker, daemon=True).start()
            return job

    def _stream_packages_tar(self, package_entries, filename: str, extra_headers=None):
        """
        On-the-fly strimuje .tar arhivu direktno iz Pool/packages u HTTP wfile
        bez upisa u bundles/ i bez X-Model-SHA256 headera (Connection: close za HTTP/1.0).
        """
        from servishub.drivers.pool import FORBIDDEN_DRIVER_EXTENSIONS

        safe_fn = os.path.basename(str(filename or "driver_pool_bundle.tar")).replace('"', "_")
        self.send_response(200)
        self.send_header("Content-Type", "application/x-tar")
        self.send_header("Content-Disposition", f'attachment; filename="{safe_fn}"')
        self.send_header("Connection", "close")
        self.close_connection = True
        self._send_cors_headers()
        self._send_security_headers()
        if isinstance(extra_headers, dict):
            for hk, hv in extra_headers.items():
                if hv is not None and hk.lower() != "x-model-sha256":
                    self.send_header(str(hk), str(hv))
        self.end_headers()

        with tarfile.open(fileobj=self.wfile, mode="w|") as tf:
            for pid, pmeta, pdir in (package_entries or []):
                if not pdir or not os.path.isdir(pdir) or os.path.islink(pdir):
                    continue
                stem = re.sub(r"[^a-zA-Z0-9._-]+", "_", str((pmeta or {}).get("inf_stem") or "drv")).strip("_") or "drv"
                folder_prefix = f"{stem}_{str(pid)[:8]}"
                for rdir, _, files in os.walk(pdir, followlinks=False):
                    for fn in sorted(files):
                        if fn.lower().endswith(FORBIDDEN_DRIVER_EXTENSIONS):
                            continue
                        full_f = os.path.join(rdir, fn)
                        if os.path.islink(full_f):
                            continue
                        rel = os.path.relpath(full_f, pdir).replace("\\", "/")
                        tf.add(full_f, arcname=f"{folder_prefix}/{rel}", recursive=False)
        try:
            self.wfile.flush()
        except OSError:
            pass

    def _maybe_trigger_async_oem_ingest(
        self,
        pool_mgr,
        manufacturer: str = "",
        model: str = "",
        system_sku: str = "",
        require_catalog_entry: bool = True,
        force_trigger: bool = False,
    ):
        """
        IMP-98: Proverava da li model podržava zvanični OEM paket i nema oem_pack u manifestu,
        i pokreće deduplikovani pozadinski ingest_oem_pack bez blokiranja odgovora klijentu.
        """
        if pool_mgr is None or not model or not str(model).strip():
            return None
        try:
            from servishub.drivers.oem.catalogs import detect_oem_vendor
            from servishub.drivers.pool import is_generic_model_pack_name, sanitize_model_pack_name

            clean_mfg = str(manufacturer or "").strip()
            clean_model = str(model or "").strip()
            oem_vendor = detect_oem_vendor(clean_mfg, clean_model)
            if not oem_vendor or is_generic_model_pack_name(clean_model):
                return None
            if pool_mgr.has_oem_pack_in_manifest(clean_model):
                return None

            eff_mfg = clean_mfg or oem_vendor
            active_oem = pool_mgr.get_active_oem_job(eff_mfg, clean_model)
            if active_oem:
                return {
                    "job": active_oem,
                    "oem_entry": None,
                    "vendor": oem_vendor,
                    "started": False,
                }

            oem_entry = None
            if getattr(pool_mgr, "catalog_mgr", None) is not None:
                try:
                    oem_entry = pool_mgr.catalog_mgr.find_driver_pack(
                        manufacturer=eff_mfg,
                        model=clean_model,
                        system_sku=system_sku,
                    )
                except Exception:
                    oem_entry = None

            if oem_entry or (not require_catalog_entry) or force_trigger:
                try:
                    clean_m = sanitize_model_pack_name(clean_model).lower()
                except ValueError:
                    clean_m = clean_model.lower()
                active_oem = pool_mgr.start_dedup_background_job(
                    "ingest_oem",
                    f"oem:{oem_vendor}:{clean_m}",
                    pool_mgr.ingest_oem_pack,
                    eff_mfg,
                    clean_model,
                    system_sku=system_sku,
                )
                return {
                    "job": active_oem,
                    "oem_entry": oem_entry,
                    "vendor": oem_vendor,
                    "started": True,
                }
        except Exception as ex:
            print(f"[!] [DriverResolve] OEM async ingest provera greška: {ex}", file=sys.stderr)
        return None

    def _resolve_single_device_hwids(
        self,
        raw_hwids,
        device_name="UnknownDevice",
        manufacturer="",
        model="",
        system_sku="",
        async_download=False,
        inline_wait_sec=None,
        stage="",
        error_code=None,
    ):
        clean_hwids = [str(h).strip() for h in (raw_hwids or []) if str(h).strip()]
        results = []
        if not clean_hwids:
            return results

        wait_sec = None
        if async_download:
            wait_sec = 3.0 if inline_wait_sec is None else max(0.0, min(15.0, float(inline_wait_sec)))

        stage_low = str(stage or "").strip().lower()
        is_critical_stage = any(tok in stage_low for tok in ("postreboot", "finalize", "gate", "fastnas", "recovery"))
        try:
            err_code_int = int(error_code) if error_code is not None and not isinstance(error_code, bool) else None
        except (ValueError, TypeError):
            err_code_int = None
        is_critical_error = err_code_int in (10, 28, 43)
        force_oem_bg = bool(is_critical_stage or is_critical_error)

        cache_scope = os.path.abspath(str(getattr(self.driver_cache_mgr, "cache_dir", "no-cache")))
        print(f"[*] [DriverResolve] Primljen upit za {len(clean_hwids)} HWID-ja (Uređaj: {device_name} / {manufacturer} {model})...")

        # 1. Lokalni oem_pack / manifest winner iz Driver Pool-a
        try:
            pool_mgr = self._get_driver_pool_mgr()
        except Exception:
            pool_mgr = None

        if pool_mgr is not None:
            try:
                pool_res = pool_mgr.resolve_device_hwids(clean_hwids, model=model)
                pool_matches = pool_res.get("matches") or []
                if pool_matches:
                    best_m = pool_matches[0]
                    pkg_id = best_m["pkg_id"]
                    srcs = best_m.get("sources") or []
                    src_lbl = "oem_pack" if "oem_pack" in srcs else "driver_pool"
                    entry = {
                        "hwid": best_m["hwid"],
                        "status": "ready",
                        "driver_id": pkg_id,
                        "pkg_id": pkg_id,
                        "title": best_m.get("inf_name") or f"PoolDriver ({pkg_id[:8]})",
                        "driver_ver": best_m.get("driver_ver"),
                        "driver_date": best_m.get("driver_date"),
                        "download_url": f"/api/drivers/pool/package?id={quote(pkg_id)}",
                        "size_bytes": best_m.get("size_bytes", 0),
                        "source": src_lbl,
                        "sources": srcs,
                    }
                    # IMP-98: Ne vraćaj pre provere/pokretanja pozadinskog OEM ingest-a ako manifest nema oem_pack!
                    if src_lbl != "oem_pack":
                        oem_bg = self._maybe_trigger_async_oem_ingest(
                            pool_mgr,
                            manufacturer=manufacturer,
                            model=model,
                            system_sku=system_sku,
                            require_catalog_entry=not force_oem_bg,
                            force_trigger=force_oem_bg,
                        )
                        if oem_bg:
                            entry["oem_ingest_triggered"] = True
                            entry["oem_job_id"] = (oem_bg.get("job") or {}).get("job_id", "")
                            entry["oem_ingest_status"] = "downloading"
                    results.append(entry)
                    return results
            except Exception as ex:
                print(f"[!] [DriverResolve] Pool lookup greška: {ex}", file=sys.stderr)

        # 1b. Cache-first provera svih HWID kandidata uz early-stop na prvom pogotku
        if self.driver_cache_mgr:
            for hwid_str in clean_hwids:
                cached = self.driver_cache_mgr.get_cached(hwid_str)
                if cached:
                    drv_id = cached.get("driver_id", "")
                    print(f"  [+] [CACHE HIT] {hwid_str} -> {cached.get('title', 'Driver')} (ID: {drv_id[:12]}...)")
                    entry = self._build_ready_driver_entry(hwid_str, cached, source="cache")
                    # IMP-98: Pokreni pozadinski OEM ingest i na cache pogotku ako manifest nema oem_pack
                    oem_bg = self._maybe_trigger_async_oem_ingest(
                        pool_mgr,
                        manufacturer=manufacturer,
                        model=model,
                        system_sku=system_sku,
                        require_catalog_entry=not force_oem_bg,
                        force_trigger=force_oem_bg,
                    )
                    if oem_bg:
                        entry["oem_ingest_triggered"] = True
                        entry["oem_job_id"] = (oem_bg.get("job") or {}).get("job_id", "")
                        entry["oem_ingest_status"] = "downloading"
                    results.append(entry)
                    return results

        # 2. Ako postoji/u toku je OEM paket za dati model u Pool-u, pokreni asinhroni OEM ingest
        if pool_mgr is not None and model:
            oem_bg = self._maybe_trigger_async_oem_ingest(
                pool_mgr,
                manufacturer=manufacturer,
                model=model,
                system_sku=system_sku,
                require_catalog_entry=True,
                force_trigger=force_oem_bg,
            )
            if oem_bg:
                active_oem = oem_bg.get("job") or {}
                oem_entry = oem_bg.get("oem_entry") or {}
                results.append({
                    "hwid": clean_hwids[0],
                    "status": "downloading",
                    "job_id": active_oem.get("job_id", ""),
                    "retry_after_sec": 10,
                    "title": oem_entry.get("package_name") or f"OEM Driver Pack ({manufacturer or oem_bg.get('vendor', '')} {model})".strip(),
                    "source": "oem_pack_async",
                    "oem_ingest_triggered": True,
                    "oem_job_id": active_oem.get("job_id", ""),
                })
                return results

        # 1c. Provera da li je za neki od HWID-jeva već u toku dedupovano pozadinsko preuzimanje (ms_catalog)
        active_job = None
        active_hwid = clean_hwids[0]
        with ServisHubHandler._driver_dl_lock:
            self._prune_driver_dl_jobs_locked()
            for hwid_str in clean_hwids:
                candidate_job = ServisHubHandler._driver_dl_jobs.get((cache_scope, hwid_str.upper()))
                if candidate_job and candidate_job.get("state") == "downloading":
                    active_job = candidate_job
                    active_hwid = hwid_str
                    break

        if active_job is not None:
            active_job["event"].wait(timeout=wait_sec)
            if active_job.get("state") == "ready" and active_job.get("saved"):
                results.append(self._build_ready_driver_entry(active_hwid, active_job["saved"], source="catalog_download"))
                return results
            if active_job.get("state") == "downloading" and async_download:
                results.append({
                    "hwid": active_hwid,
                    "status": "downloading",
                    "job_id": active_job.get("job_id", ""),
                    "retry_after_sec": 5,
                    "title": active_job.get("title", ""),
                    "source": "catalog_download_async",
                })
                return results

        # 2. Pretraga Microsoft Update Kataloga uz dedupovani background download
        for hwid_str in clean_hwids:
            print(f"  [*] [CACHE MISS] Pretražujem Microsoft Update Catalog za: {hwid_str}...")
            resolved = self.ms_catalog_scraper.resolve_driver(hwid_str) if self.ms_catalog_scraper else None
            if not resolved:
                print(f"  [-] [NOT FOUND] Drajver nije pronađen na Microsoft katalogu: {hwid_str}")
                results.append({
                    "hwid": hwid_str,
                    "status": "not_found",
                    "error": "Drajver nije pronađen na Microsoft Update Katalogu za zadati hardver.",
                })
                continue

            cab_url = resolved.get("download_url")
            from servishub.config import is_low_disk_for_ms_catalog
            cache_probe_dir = getattr(self.driver_cache_mgr, "cache_dir", None) or (
                getattr(self.config, "drivers_cache_dir", None) if self.config else None
            )
            min_free_gb = (
                getattr(self.config, "ms_catalog_min_free_gb", None)
                if (self.config and getattr(self.config, "_explicit_ms_catalog_min_free_gb", False))
                else None
            )
            low_disk, free_gb, eff_thresh = is_low_disk_for_ms_catalog(cache_probe_dir, min_free_gb=min_free_gb)
            if low_disk:
                results.append({
                    "hwid": hwid_str,
                    "status": "paused_low_disk",
                    "free_gb": round(free_gb, 2) if free_gb is not None else None,
                    "min_free_gb": eff_thresh,
                    "error": f"Preuzimanje iz ms_catalog pauzirano: slobodan prostor ({free_gb:.2f} GB) < {eff_thresh:.1f} GB.",
                })
                continue

            print(f"  [*] [DOWNLOAD] Preuzimam zvanični CAB drajver sa Microsoft CDN-a: {resolved.get('title')} ({str(cab_url)[:70]}...)...")
            job = self._start_or_join_driver_download_job(cache_scope, hwid_str, clean_hwids, resolved)
            job["event"].wait(timeout=wait_sec)

            if job.get("state") == "ready" and job.get("saved"):
                results.append(self._build_ready_driver_entry(hwid_str, job["saved"], source="catalog_download"))
                return results
            if job.get("state") == "downloading" and async_download:
                results.append({
                    "hwid": hwid_str,
                    "status": "downloading",
                    "job_id": job.get("job_id", ""),
                    "retry_after_sec": 5,
                    "title": resolved.get("title", ""),
                    "source": "catalog_download_async",
                })
                return results

            results.append({
                "hwid": hwid_str,
                "status": "error",
                "error": f"Greška pri preuzimanju ili verifikaciji drajvera: {job.get('error') or 'Nepoznata greška'}",
            })

        return results

    def do_OPTIONS(self):
        self.send_response(200)
        self._send_cors_headers()
        self.send_header("Access-Control-Allow-Methods", "GET, POST, OPTIONS")
        self.send_header("Access-Control-Allow-Headers", "Content-Type, X-Payload-Version, X-WIS-Version, X-WIS-SHA256, X-Driver-ID, X-Client-Timestamp, X-Update-Filename, X-Update-KB, X-OS-Build, X-Model-Name, X-Model-Filename, X-ServisHub-Token, X-ServisHub-Admin-Token, X-ServisHub-Client-Token, Authorization")
        self._send_security_headers()
        self.end_headers()

    def do_GET(self):
        parsed = urlparse(self.path)
        path = parsed.path

        if path in ("/", "/index.html"):
            body = HTML_DASHBOARD.encode("utf-8")
            self.send_response(200)
            self.send_header("Content-Type", "text/html; charset=utf-8")
            self.send_header("Cache-Control", "no-cache, no-store, must-revalidate")
            self.send_header("Pragma", "no-cache")
            self.send_header("Expires", "0")
            self._send_security_headers()
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)
            return

        if path.startswith("/device/") or path.startswith("/karton/"):
            parts = path.strip("/").split("/", 1)
            raw_id = unquote(parts[1]).strip().strip("/") if len(parts) > 1 else ""
            target = f"/?device={quote(raw_id, safe='')}&view=card" if raw_id else "/"
            self.send_response(302)
            self.send_header("Location", target)
            self.send_header("Cache-Control", "no-cache, no-store, must-revalidate")
            self._send_security_headers()
            self.send_header("Content-Length", "0")
            self.end_headers()
            return

        if path in ("/static/dashboard.css", "/static/dashboard.js"):
            static_dir = os.path.join(os.path.dirname(__file__), "web", "static")
            fname = "dashboard.css" if path.endswith(".css") else "dashboard.js"
            ctype = "text/css; charset=utf-8" if path.endswith(".css") else "application/javascript; charset=utf-8"
            fpath = os.path.join(static_dir, fname)
            if os.path.isfile(fpath):
                with open(fpath, "rb") as sf:
                    body = sf.read().replace(b"__WIS_VERSION__", VERSION.encode("utf-8"))
                self.send_response(200)
                self.send_header("Content-Type", ctype)
                self.send_header("Cache-Control", "no-cache")
                self._send_security_headers()
                self.send_header("Content-Length", str(len(body)))
                self.end_headers()
                self.wfile.write(body)
                return

        if path in ("/api/health", "/api/status"):
            uptime = int(time.time() - self.start_time)
            disk_info = compute_disk_usage_info(self.config)
            status_payload = {
                "status": "ok",
                "uptime_sec": uptime,
                "version": VERSION,
                "release_status": RELEASE_STATUS,
                "disk_usage": disk_info,
                "low_disk_warning": disk_info["low_disk_warning"],
                "critical_disk_warning": disk_info["critical_disk_warning"],
            }
            if disk_info["critical_disk_warning"] or disk_info["low_disk_warning"]:
                status_payload["status_message"] = disk_info["status_message"]
            self._send_json(status_payload)
            return

        if path == "/api/model-profile":
            qs = parse_qs(parsed.query)
            model_name = qs.get("model", [""])[0].strip()
            if not model_name:
                self._send_json({"error": "Nedostaje parametar 'model'"}, 400)
                return
            from servishub.model_profiles import load_model_profile
            profile = load_model_profile(model_name)
            self._send_json(profile, 200)
            return

        if path == "/api/model-profiles":
            from servishub.model_profiles import list_model_profiles
            self._send_json({"status": "ok", "profiles": list_model_profiles()}, 200)
            return

        if path in ("/api/benchmark/summary", "/api/benchmark/stats"):
            from servishub.benchmark import compute_fleet_benchmark_summary
            reports_dir = getattr(self.config, "reports_dir", None) if self.config else None
            summary = compute_fleet_benchmark_summary(self.fleet_store, reports_dir=reports_dir)
            self._send_json(summary, 200)
            return

        if path == "/api/predictive/status":
            mgr = self._get_predictive_mgr()
            self._send_json(mgr.get_status(config=self.config), 200)
            return

        if path == "/api/keyboard-layout":
            qs = parse_qs(parsed.query)
            manufacturer = qs.get("manufacturer", [""])[0].strip()
            model_name = qs.get("model", [""])[0].strip()
            serial = qs.get("serial", [""])[0].strip()
            sku = qs.get("sku", [""])[0].strip()
            try:
                screen_inches = float(qs.get("screen_inches", ["0"])[0] or 0)
            except (ValueError, TypeError):
                screen_inches = 0.0

            def _qbool(key):
                raw = qs.get(key, [""])[0].strip().lower()
                if raw in ("1", "true", "yes", "da"):
                    return True
                if raw in ("0", "false", "no", "ne"):
                    return False
                return None

            from servishub.keyboard_resolver import resolve_keyboard_layout
            spec = resolve_keyboard_layout(
                manufacturer=manufacturer,
                model=model_name,
                serial=serial,
                sku=sku,
                screen_inches=screen_inches,
                is_desktop=_qbool("is_desktop"),
                os_oem102=_qbool("os_oem102"),
            )
            self._send_json(spec, 200)
            return

        if path == "/api/hardware-spec":
            qs = parse_qs(parsed.query)
            manufacturer = qs.get("manufacturer", [""])[0].strip()
            model_name = qs.get("model", [""])[0].strip()
            serial = qs.get("serial", [""])[0].strip()
            sku = qs.get("sku", [""])[0].strip()
            is_desktop_raw = qs.get("is_desktop", [""])[0].strip().lower()
            is_desktop = None
            if is_desktop_raw in ("1", "true", "yes", "da"):
                is_desktop = True
            elif is_desktop_raw in ("0", "false", "no", "ne"):
                is_desktop = False
            from servishub.hardware_spec import resolve_hardware_spec
            hw_spec = resolve_hardware_spec(
                manufacturer=manufacturer,
                model=model_name,
                serial=serial,
                sku=sku,
                is_desktop=is_desktop,
            )
            self._send_json(hw_spec, 200)
            return

        if path == "/api/updates/status":
            from servishub.api.updates import scan_updates_directory, validate_http_storage_directory
            query_params = parse_qs(parsed.query)
            custom_dir = query_params.get("directory", [None])[0]
            os_build = query_params.get("os_build", [None])[0]
            try:
                safe_dir = validate_http_storage_directory(
                    custom_dir,
                    default_root=getattr(self.config, "updates_dir", None),
                    active_config=self.config,
                )
            except ValueError as ve:
                self._send_json({"error": str(ve)}, 400)
                return
            status_data = scan_updates_directory(safe_dir, os_build=os_build)
            data = status_data.model_dump() if hasattr(status_data, "model_dump") else status_data.to_dict()
            self._send_json(data)
            return

        if path == "/api/updates/download":
            from servishub.api.updates import find_update_package, validate_http_storage_directory
            query_params = parse_qs(parsed.query)
            filename = query_params.get("filename", [None])[0]
            kb = query_params.get("kb", [None])[0]
            custom_dir = query_params.get("directory", [None])[0]
            try:
                safe_dir = validate_http_storage_directory(
                    custom_dir,
                    default_root=getattr(self.config, "updates_dir", None),
                    active_config=self.config,
                )
            except ValueError as ve:
                self._send_json({"error": str(ve)}, 400)
                return

            lookup = filename or kb
            if not lookup:
                self._send_json({"error": "Nedostaje parametar 'filename' ili 'kb'."}, 400)
                return

            pkg = find_update_package(lookup, directory=safe_dir)
            if not pkg or not pkg.path or not os.path.isfile(pkg.path):
                self._send_json({"error": f"Paket '{lookup}' nije pronađen u skladištu."}, 404)
                return

            try:
                content_type = "application/vnd.ms-cab-compressed" if pkg.file_type == "cab" else "application/octet-stream"
                extra = {"X-Update-KB": pkg.kb} if pkg.kb else None
                self._stream_file(pkg.path, pkg.filename, content_type=content_type, extra_headers=extra)
            except (BrokenPipeError, ConnectionResetError):
                pass
            except Exception as e:
                print(f"[ERROR] Greška pri slanju paketa {pkg.filename}: {e}", file=sys.stderr)
            return

        if path == "/api/payload/version":
            try:
                if not self.payload_mgr:
                    raise ValueError("PayloadManager nije konfigurisan")
                self._send_json(self.payload_mgr.get_metadata())
            except Exception:
                self._send_json({"error": "Nema validnog potpisanog WIS izdanja"}, 503)
            return

        if path == "/api/payload/download":
            try:
                if not self.payload_mgr:
                    raise ValueError("PayloadManager nije konfigurisan")
                meta, zip_path = self.payload_mgr.get_payload_file()
                # Immutable release path drži isti ZIP čak i pri promeni current.json.
                stream = open(zip_path, "rb")
            except Exception:
                self._send_json({"error": "Nema validnog potpisanog WIS izdanja"}, 503)
                return
            try:
                with stream:
                    self.send_response(200)
                    self.send_header("Content-Type", "application/zip")
                    self.send_header("Content-Disposition", 'attachment; filename="wis-payload.zip"')
                    self.send_header("Content-Length", str(meta["size"]))
                    self.send_header("X-Payload-Version", str(meta["version"]))
                    self.send_header("X-Payload-SHA256", str(meta["sha256"]))
                    self._send_cors_headers()
                    self._send_security_headers()
                    self.end_headers()
                    while True:
                        chunk = stream.read(65536)
                        if not chunk:
                            break
                        self.wfile.write(chunk)
                    self.wfile.flush()
            except (BrokenPipeError, ConnectionResetError):
                pass
            return

        if path == "/api/wis/iso/info":
            wis_dir = getattr(self.config, "wis_dir", None)
            iso_mgr = WisIsoManager(wis_dir=wis_dir)
            info = iso_mgr.get_iso_info()
            self._send_json(info)
            return

        if path == "/api/wis/iso/download":
            wis_dir = getattr(self.config, "wis_dir", None)
            iso_mgr = WisIsoManager(wis_dir=wis_dir)
            info = iso_mgr.get_iso_info()
            iso_path = info.get("path")
            if not info.get("available") or not iso_path or not os.path.isfile(iso_path):
                self._send_json({"error": "WIS ISO slika nije pronađena na serveru"}, 404)
                return

            try:
                file_size = info.get("size_bytes") or os.path.getsize(iso_path)
                filename = info.get("filename") or os.path.basename(iso_path)
                self.send_response(200)
                self.send_header("Content-Type", "application/octet-stream")
                self.send_header("Content-Disposition", f'attachment; filename="{filename}"')
                self.send_header("Content-Length", str(file_size))
                if info.get("version"):
                    self.send_header("X-WIS-Version", str(info.get("version")))
                if info.get("sha256"):
                    self.send_header("X-WIS-SHA256", str(info.get("sha256")))
                self._send_cors_headers()
                self._send_security_headers()
                self.end_headers()

                with open(iso_path, "rb") as f:
                    while True:
                        chunk = f.read(65536)
                        if not chunk:
                            break
                        self.wfile.write(chunk)
                self.wfile.flush()
            except (BrokenPipeError, ConnectionResetError):
                pass
            except Exception as e:
                print(f"[ERROR] Greška pri preuzimanju WIS ISO slike: {e}", file=sys.stderr)
            return

        if path == "/api/wis/usb/list":
            devices = list_removable_usb_devices()
            self._send_json({"status": "ok", "devices": devices})
            return

        if path == "/api/wis/usb/progress":
            sse_channel = "usb_burn"
            if not self.fleet_store.acquire_sse_slot(sse_channel):
                self._send_json({"error": "Prekoračen maksimalni broj istovremenih SSE konekcija."}, 429)
                return

            sub_queue = []
            sub_lock = threading.Condition()
            try:
                self.send_response(200)
                self.send_header("Content-Type", "text/event-stream")
                self.send_header("Cache-Control", "no-cache")
                self.send_header("Connection", "keep-alive")
                self._send_cors_headers()
                self._send_security_headers()
                self.end_headers()

                with WIS_BURN_LOCK:
                    WIS_BURN_SUBSCRIBERS.append((sub_queue, sub_lock))
                    sub_queue.append(dict(WIS_BURN_STATE))

                while True:
                    with sub_lock:
                        while not sub_queue:
                            sub_lock.wait(timeout=1.0)
                        items_to_send = list(sub_queue)
                        sub_queue.clear()

                    for item in items_to_send:
                        msg_str = json.dumps(item, ensure_ascii=False)
                        msg = f"data: {msg_str}\n\n"
                        self.wfile.write(msg.encode("utf-8"))
                        self.wfile.flush()
                        if item.get("phase") in ("completed", "error"):
                            return
            except (BrokenPipeError, ConnectionResetError):
                pass
            finally:
                with WIS_BURN_LOCK:
                    if (sub_queue, sub_lock) in WIS_BURN_SUBSCRIBERS:
                        try:
                            WIS_BURN_SUBSCRIBERS.remove((sub_queue, sub_lock))
                        except ValueError:
                            pass
                self.fleet_store.release_sse_slot(sse_channel)
            return

        if path == "/api/drivers/pool/package":
            query_params = parse_qs(parsed.query)
            pkg_id = (query_params.get("id") or [None])[0]
            if not pkg_id:
                self._send_json({"error": "Nedostaje parametar 'id'."}, 400)
                return
            try:
                pool_mgr = self._get_driver_pool_mgr()
                snap = pool_mgr.get_package_snapshot(pkg_id)
                if not snap:
                    self._send_json({"error": "Paket nije pronađen u Driver Pool-u."}, 404)
                    return
                pmeta, pdir = snap
                self._stream_packages_tar(
                    [(pkg_id, pmeta, pdir)],
                    f"{pkg_id[:12]}.tar",
                    extra_headers={"X-Driver-Pool-Package": pkg_id},
                )
            except (BrokenPipeError, ConnectionResetError):
                pass
            except Exception as e:
                self._send_json({"error": f"Greška pri strimovanju paketa: {e}"}, 500)
            return

        if path == "/api/drivers/pool/stats":
            try:
                pool_mgr = self._get_driver_pool_mgr()
                self._send_json(pool_mgr.get_stats())
            except Exception as e:
                self._send_json({"error": f"Greška pri čitanju statistike Driver Pool-a: {e}"}, 500)
            return

        if path == "/api/drivers/pool/manifests":
            try:
                pool_mgr = self._get_driver_pool_mgr()
                manifests = pool_mgr.list_manifests()
                self._send_json({"status": "ok", "count": len(manifests), "manifests": manifests})
            except Exception as e:
                self._send_json({"error": f"Greška pri čitanju manifesta Driver Pool-a: {e}"}, 500)
            return

        if path == "/api/drivers/model-pack/status":
            from servishub.api.updates import get_model_pack_status, validate_http_storage_directory
            query_params = parse_qs(parsed.query)
            model_name = query_params.get("model", [None])[0]
            mfg_q = query_params.get("manufacturer", [""])[0]
            sku_q = query_params.get("system_sku", [""])[0]
            custom_dir = query_params.get("directory", [None])[0]
            if not model_name:
                self._send_json({"error": "Nedostaje obavezni parametar 'model'."}, 400)
                return
            try:
                safe_dir = validate_http_storage_directory(custom_dir, default_root=None, active_config=self.config)
                status_info = get_model_pack_status(model_name, directory=safe_dir, active_config=self.config)
                if not custom_dir:
                    pool_mgr = self._get_driver_pool_mgr()
                    self._maybe_trigger_async_oem_ingest(
                        pool_mgr,
                        manufacturer=mfg_q,
                        model=model_name,
                        system_sku=sku_q,
                        require_catalog_entry=True,
                    )
                if not status_info.get("available") and not custom_dir:
                    pool_mgr = self._get_driver_pool_mgr()
                    if pool_mgr.has_model_manifest(model_name):
                        bundle = pool_mgr.build_bundle_tar(model=model_name)
                        if bundle.get("available"):
                            status_info = {
                                "available": True,
                                "model": bundle["model"],
                                "filename": bundle["bundle_filename"],
                                "directory": pool_mgr.bundles_dir,
                                "path": bundle["bundle_path"],
                                "size_bytes": bundle["size_bytes"],
                                "size_mb": bundle["size_mb"],
                                "modified_at": datetime.fromtimestamp(os.path.getmtime(bundle["bundle_path"])).isoformat(),
                                "sha256": bundle["sha256"],
                                "source": "driver_pool",
                                "package_count": bundle["package_count"],
                            }
                self._send_json(status_info)
            except ValueError as ve:
                self._send_json({"error": str(ve)}, 400)
            return

        if path == "/api/drivers/model-pack/download":
            from servishub.api.updates import get_model_pack_status, validate_http_storage_directory
            query_params = parse_qs(parsed.query)
            model_name = query_params.get("model", [None])[0]
            mfg_q = query_params.get("manufacturer", [""])[0]
            sku_q = query_params.get("system_sku", [""])[0]
            custom_dir = query_params.get("directory", [None])[0]
            if not model_name:
                self._send_json({"error": "Nedostaje obavezni parametar 'model'."}, 400)
                return
            try:
                safe_dir = validate_http_storage_directory(custom_dir, default_root=None, active_config=self.config)
                status_info = get_model_pack_status(model_name, directory=safe_dir, active_config=self.config)
                if not custom_dir:
                    pool_mgr = self._get_driver_pool_mgr()
                    self._maybe_trigger_async_oem_ingest(
                        pool_mgr,
                        manufacturer=mfg_q,
                        model=model_name,
                        system_sku=sku_q,
                        require_catalog_entry=True,
                    )
                if not status_info.get("available") and not custom_dir:
                    pool_mgr = self._get_driver_pool_mgr()
                    if pool_mgr.has_model_manifest(model_name):
                        bundle = pool_mgr.build_bundle_tar(model=model_name)
                        if bundle.get("available"):
                            status_info = {
                                "available": True,
                                "model": bundle["model"],
                                "filename": bundle["bundle_filename"],
                                "path": bundle["bundle_path"],
                                "sha256": bundle["sha256"],
                                "source": "driver_pool",
                            }
            except ValueError as ve:
                self._send_json({"error": str(ve)}, 400)
                return

            pack_path = status_info.get("path")
            if not status_info.get("available") or not pack_path or not os.path.isfile(pack_path):
                self._send_json({"error": f"Model paket '{model_name}' nije pronađen u skladištu."}, 404)
                return
            try:
                extra = {"X-Model-Name": status_info.get("model")}
                if status_info.get("sha256"):
                    extra["X-Model-SHA256"] = status_info.get("sha256")
                if status_info.get("source"):
                    extra["X-Driver-Pool-Source"] = status_info.get("source")
                self._stream_file(pack_path, status_info.get("filename"), content_type="application/x-tar", extra_headers=extra)
            except (BrokenPipeError, ConnectionResetError):
                pass
            except Exception as e:
                print(f"[ERROR] Greška pri slanju model paketa {model_name}: {e}", file=sys.stderr)
            return

        if path == "/api/drivers/cache/list":
            if self.driver_cache_mgr:
                drivers = self.driver_cache_mgr.list_cached()
                self._send_json({
                    "status": "ok",
                    "count": len(drivers),
                    "drivers": drivers
                })
            else:
                self._send_json({"status": "ok", "count": 0, "drivers": []})
            return

        if path == "/api/drivers/download" or path.startswith("/api/drivers/download/"):
            query_params = parse_qs(parsed.query)
            driver_id = None
            if "id" in query_params and query_params["id"]:
                driver_id = query_params["id"][0].strip()
            elif path.startswith("/api/drivers/download/"):
                driver_id = path.replace("/api/drivers/download/", "").strip()

            if not driver_id:
                self._send_json({"error": "Nedostaje parametar 'id' za preuzimanje drajvera"}, 400)
                return

            if not self.driver_cache_mgr:
                self._send_json({"error": "DriverCacheManager nije inicijalizovan"}, 500)
                return

            cab_path = self.driver_cache_mgr.get_cab_path(driver_id)
            if not cab_path or not os.path.isfile(cab_path):
                self._send_json({"error": f"Drajver sa ID '{driver_id}' nije pronađen u lokalnom kešu"}, 404)
                return

            try:
                file_size = os.path.getsize(cab_path)
                self.send_response(200)
                self.send_header("Content-Type", "application/vnd.ms-cab-compressed")
                self.send_header("Content-Disposition", f'attachment; filename="{driver_id}.cab"')
                self.send_header("Content-Length", str(file_size))
                self.send_header("X-Driver-ID", driver_id)
                self._send_cors_headers()
                self._send_security_headers()
                self.end_headers()

                with open(cab_path, "rb") as f:
                    while True:
                        chunk = f.read(65536)
                        if not chunk:
                            break
                        self.wfile.write(chunk)
                self.wfile.flush()
            except (BrokenPipeError, ConnectionResetError):
                pass
            except Exception as e:
                print(f"[ERROR] Greška pri serviranju drajvera {driver_id}: {e}", file=sys.stderr)
            return

        if path == "/api/devices":
            out = self.fleet_store.get_devices_summary()
            self._send_json(out)
            return

        if path == "/api/sessions":
            out = self.fleet_store.get_sessions_summary()
            self._send_json(out)
            return

        if path.startswith("/api/session/") and path.endswith("/stream"):
            parts = path.strip("/").split("/")
            if len(parts) >= 4 and parts[0] == "api" and parts[1] == "session" and parts[3] == "stream":
                session_id = unquote(parts[2]).strip()
                sse_channel = f"session:{session_id}"
                if not self.fleet_store.acquire_sse_slot(sse_channel):
                    self._send_json({"error": "Prekoračen maksimalni broj istovremenih SSE konekcija."}, 429)
                    return

                sub_queue = []
                sub_lock = threading.Condition()
                try:
                    self.send_response(200)
                    self.send_header("Content-Type", "text/event-stream")
                    self.send_header("Cache-Control", "no-cache")
                    self.send_header("Connection", "keep-alive")
                    self._send_cors_headers()
                    self._send_security_headers()
                    self.end_headers()

                    with self.fleet_store.session_lock:
                        if session_id not in self.fleet_store.session_subscribers:
                            self.fleet_store.session_subscribers[session_id] = []
                        self.fleet_store.session_subscribers[session_id].append((sub_queue, sub_lock))
                        curr = self.fleet_store.fleet_sessions.get(session_id)
                        if curr:
                            sub_queue.append(dict(curr))

                    while True:
                        with sub_lock:
                            if not sub_queue:
                                sub_lock.wait(timeout=1.0)
                                if not sub_queue:
                                    with self.fleet_store.session_lock:
                                        curr = self.fleet_store.fleet_sessions.get(session_id)
                                        if curr:
                                            curr_copy = dict(curr)
                                            now = time.time()
                                            sec_ago = int(now - curr.get("last_seen", now))
                                            curr_copy["is_stale"] = (sec_ago > 30)
                                            if curr_copy["is_stale"]:
                                                curr_copy["status"] = "STALE"
                                                curr_copy["status_label"] = "STALE"
                                            curr_copy["last_seen_sec_ago"] = sec_ago
                                            sub_queue.append(curr_copy)
                            items_to_send = list(sub_queue)
                            sub_queue.clear()

                        for item in items_to_send:
                            msg_str = json.dumps(item, ensure_ascii=False)
                            msg = f"data: {msg_str}\n\n"
                            self.wfile.write(msg.encode("utf-8"))
                        self.wfile.flush()
                except (BrokenPipeError, ConnectionResetError):
                    pass
                finally:
                    with self.fleet_store.session_lock:
                        if session_id in self.fleet_store.session_subscribers:
                            try:
                                self.fleet_store.session_subscribers[session_id].remove((sub_queue, sub_lock))
                            except ValueError:
                                pass
                    self.fleet_store.release_sse_slot(sse_channel)
                return

        norm_path = path.rstrip("/")
        if norm_path.startswith("/api/devices/") and (norm_path.endswith("/screen-frame.jpg") or norm_path.endswith("/screen-frame")):
            parts = norm_path.strip("/").split("/")
            if len(parts) >= 4 and parts[0] == "api" and parts[1] == "devices" and parts[3] in ("screen-frame.jpg", "screen-frame"):
                raw_device_id = parts[2]
                device_id = unquote(raw_device_id).strip()
                if not device_id:
                    self._send_json({"error": "Nedostaje device_id u putanji"}, 400)
                    return

                query_params = parse_qs(parsed.query)
                max_age = 5.0
                if "max_age" in query_params:
                    try:
                        max_age = float(query_params["max_age"][0])
                    except (ValueError, TypeError):
                        max_age = 5.0

                frame_data = self.fleet_store.get_screen_frame(device_id, max_age_seconds=max_age)
                if frame_data is not None:
                    frame_bytes, frame_ts = frame_data
                    self.send_response(200)
                    self.send_header("Content-Type", "image/jpeg")
                    self.send_header("Content-Length", str(len(frame_bytes)))
                    self.send_header("Cache-Control", "no-cache, no-store, must-revalidate")
                    self.send_header("Pragma", "no-cache")
                    self.send_header("Expires", "0")
                    self.send_header("X-Frame-Timestamp", str(frame_ts))
                    self._send_cors_headers()
                    self._send_security_headers()
                    self.end_headers()
                    self.wfile.write(frame_bytes)
                    self.wfile.flush()
                    return
                else:
                    self._send_json({
                        "status": "error",
                        "message": "No active screen stream or stream is stale"
                    }, 404)
                    return

        if path.startswith("/api/devices/") and (path.endswith("/commands") or path.endswith("/command")):
            parts = path.strip("/").split("/")
            if len(parts) >= 4 and parts[0] == "api" and parts[1] == "devices" and parts[3] in ("commands", "command"):
                raw_dev_id = parts[2]
                dev_id = unquote(raw_dev_id).strip()
                if not dev_id:
                    self._send_json({"error": "Nedostaje device_id u putanji"}, 400)
                    return
                commands = self.fleet_store.get_pending_commands(dev_id)
                executed = self.fleet_store.get_command_history(dev_id)
                self._send_json({
                    "status": "ok",
                    "device_id": dev_id,
                    "commands": commands,
                    "executed_commands": executed,
                }, 200)
                return

        if path.startswith("/api/devices/") and path.endswith("/autopilot"):
            parts = path.strip("/").split("/")
            if len(parts) >= 4 and parts[0] == "api" and parts[1] == "devices" and parts[3] == "autopilot":
                dev_id = unquote(parts[2]).strip()
                if not dev_id:
                    self._send_json({"error": "Nedostaje device_id u putanji"}, 400)
                    return
                target_k, ap_data = self.fleet_store.get_autopilot_status(dev_id)
                if target_k is None:
                    self._send_json({"error": "Uređaj nije pronađen."}, 404)
                    return
                self._send_json({
                    "status": "ok",
                    "device_id": target_k,
                    "autopilot": ap_data or {},
                }, 200)
                return

        if path.startswith("/api/devices/") and path.endswith("/logs"):
            parts = path.strip("/").split("/")
            if len(parts) >= 4:
                dev_id = unquote(parts[2]).strip()
                logs = self.fleet_store.get_device_logs(dev_id, limit=300)
                self._send_json(logs)
                return

        if path.startswith("/api/devices/") and path.endswith("/download"):
            parts = path.strip("/").split("/")
            if len(parts) >= 4:
                dev_id = unquote(parts[2]).strip()
                logs = self.fleet_store.get_device_logs(dev_id, limit=10000)
                body = "\n".join(logs).encode("utf-8")
                self.send_response(200)
                self.send_header("Content-Type", "text/plain; charset=utf-8")
                self.send_header("Content-Disposition", f'attachment; filename="servis_{sanitize_id(dev_id)}.log"')
                self.send_header("Content-Length", str(len(body)))
                self.end_headers()
                self.wfile.write(body)
                return

        if path.startswith("/api/devices/") and path.endswith("/report"):
            parts = path.strip("/").split("/")
            if len(parts) >= 4:
                dev_id = unquote(parts[2]).strip()

                report_data = None
                report_file = None
                serial = None
                with self.fleet_store.fleet_lock:
                    dev = self.fleet_store.fleet_devices.get(dev_id, {})
                    report_data = dev.get("latest_report")
                    report_file = dev.get("latest_report_file")
                    serial = (dev.get("hw") or {}).get("serial") or dev.get("serial_number")

                if report_data:
                    self._send_json(report_data)
                    return

                if report_file and os.path.isfile(report_file):
                    try:
                        with open(report_file, "r", encoding="utf-8") as f:
                            self._send_json(json.load(f))
                            return
                    except Exception:
                        pass

                reports_dir = self.config.reports_dir
                matching_files = []
                clean_id = sanitize_id(dev_id)
                clean_serial = sanitize_id(serial) if serial else None

                if os.path.exists(reports_dir):
                    for fn in os.listdir(reports_dir):
                        if not fn.endswith(".json"):
                            continue
                        if (fn.startswith(f"report_{clean_id}_") or fn.endswith(f"_{clean_id}.json") or
                            (clean_serial and (fn.startswith(f"report_{clean_serial}_") or fn.endswith(f"_{clean_serial}.json")))):
                            matching_files.append(os.path.join(reports_dir, fn))

                if matching_files:
                    matching_files.sort(key=lambda p: os.path.getmtime(p), reverse=True)
                    try:
                        with open(matching_files[0], "r", encoding="utf-8") as f:
                            self._send_json(json.load(f))
                            return
                    except Exception as e:
                        print(f"[ERROR] Greška čitanja izveštaja {matching_files[0]}: {e}", file=sys.stderr)

                self._send_json({"error": "Servisni karton nije pronađen ili uređaj još nije završen."}, 404)
                return

        if path.startswith("/api/devices/by-serial/") and path.endswith("/history"):
            parts = path.strip("/").split("/")
            if len(parts) >= 5:
                serial = unquote(parts[3]).strip()
                found_tag = None
                clean_serial = sanitize_id(serial).lower()

                with self.fleet_store.fleet_lock:
                    for tag, dev in self.fleet_store.fleet_devices.items():
                        dev_serial = str(dev.get("serial_number") or (dev.get("hw") or {}).get("serial") or dev.get("serial") or "")
                        if clean_serial and sanitize_id(dev_serial).lower() == clean_serial:
                            found_tag = tag
                            break

                reports_dir = self.config.reports_dir
                if not found_tag and reports_dir and os.path.isdir(reports_dir) and clean_serial not in ("", "unknown"):
                    real_reports_dir = os.path.realpath(reports_dir)
                    # 1. Provera AGY poddirektorijuma (bez praćenja simlinkova)
                    try:
                        with os.scandir(reports_dir) as it:
                            entries = sorted(it, key=lambda e: e.name)
                    except OSError:
                        entries = []

                    for entry in entries:
                        if entry.is_symlink():
                            continue
                        epath = entry.path
                        try:
                            if os.path.commonpath([real_reports_dir, os.path.realpath(epath)]) != real_reports_dir:
                                continue
                        except (ValueError, OSError):
                            continue
                        if entry.is_dir(follow_symlinks=False):
                            info_file = os.path.join(epath, "DEVICE_INFO.json")
                            if os.path.isfile(info_file) and not os.path.islink(info_file):
                                try:
                                    with open(info_file, "r", encoding="utf-8") as f:
                                        inf = json.load(f)
                                    s_val = str(inf.get("serial_number") or (inf.get("hardware") or {}).get("serial") or "")
                                    if sanitize_id(s_val).lower() == clean_serial:
                                        found_tag = entry.name
                                        break
                                except Exception:
                                    pass
                            if clean_serial and clean_serial in sanitize_id(entry.name).lower():
                                found_tag = entry.name
                                break

                    # 2. Provera kanonskih WIS JSON kartona u korenu reports_dir
                    if not found_tag:
                        for entry in entries:
                            fname = entry.name
                            if not fname.lower().endswith(".json") or fname.lower().endswith(".tmp"):
                                continue
                            if entry.is_symlink() or not entry.is_file(follow_symlinks=False):
                                continue
                            epath = entry.path
                            try:
                                if os.path.commonpath([real_reports_dir, os.path.realpath(epath)]) != real_reports_dir:
                                    continue
                                with open(epath, "r", encoding="utf-8") as f:
                                    rep_data = json.load(f)
                                if not isinstance(rep_data, dict):
                                    continue
                                hw_b = rep_data.get("hw") if isinstance(rep_data.get("hw"), dict) else (rep_data.get("hardware") if isinstance(rep_data.get("hardware"), dict) else {})
                                cands = (
                                    rep_data.get("serial_number"),
                                    rep_data.get("serial"),
                                    hw_b.get("serial"),
                                    hw_b.get("serial_number"),
                                    rep_data.get("device_id"),
                                    rep_data.get("computer_name"),
                                )
                                for cand in cands:
                                    if cand and sanitize_id(str(cand).strip()).lower() == clean_serial:
                                        found_tag = str(rep_data.get("device_id") or rep_data.get("computer_name") or serial).strip()
                                        break
                                if found_tag:
                                    break
                            except Exception:
                                continue

                target_tag = found_tag if found_tag else serial
                history = get_device_history(target_tag, self.config.reports_dir, serial_hint=serial)
                if not history.get("serial_number") and (found_tag or history.get("is_registered")):
                    history["serial_number"] = serial
                self._send_json(history, 200)
                return

        if path.startswith("/api/devices/") and path.endswith("/history"):
            parts = path.strip("/").split("/")
            if len(parts) >= 4 and parts[2] != "by-serial":
                device_tag = unquote(parts[2]).strip()
                history = get_device_history(device_tag, self.config.reports_dir)
                self._send_json(history, 200)
                return

        if path.startswith("/api/stream/"):
            dev_id = unquote(path[len("/api/stream/"):]).strip()
            sse_channel = f"logs:{dev_id}"
            if not self.fleet_store.acquire_sse_slot(sse_channel):
                self._send_json({"error": "Prekoračen maksimalni broj istovremenih SSE konekcija."}, 429)
                return

            sub_queue = []
            sub_lock = threading.Condition()
            try:
                self.send_response(200)
                self.send_header("Content-Type", "text/event-stream")
                self.send_header("Cache-Control", "no-cache")
                self.send_header("Connection", "keep-alive")
                self._send_cors_headers()
                self._send_security_headers()
                self.end_headers()

                with self.fleet_store.fleet_lock:
                    if dev_id not in self.fleet_store.fleet_subscribers:
                        self.fleet_store.fleet_subscribers[dev_id] = []
                    self.fleet_store.fleet_subscribers[dev_id].append((sub_queue, sub_lock))

                while True:
                    with sub_lock:
                        while not sub_queue:
                            sub_lock.wait(timeout=2.0)
                        lines_to_send = list(sub_queue)
                        sub_queue.clear()

                    for l in lines_to_send:
                        clean_line = l.replace("\r", "").replace("\n", " ")
                        msg = f"data: {clean_line}\n\n"
                        self.wfile.write(msg.encode("utf-8"))
                    self.wfile.flush()
            except (BrokenPipeError, ConnectionResetError):
                pass
            finally:
                with self.fleet_store.fleet_lock:
                    if dev_id in self.fleet_store.fleet_subscribers:
                        try:
                            self.fleet_store.fleet_subscribers[dev_id].remove((sub_queue, sub_lock))
                        except ValueError:
                            pass
                self.fleet_store.release_sse_slot(sse_channel)
            return

        self.send_response(404)
        self.end_headers()

    def do_POST(self):
        parsed = urlparse(self.path)
        path = parsed.path
        norm_path = path.rstrip("/")

        # 1. Specijalna binarna ruta za primanje frejmova ekrana (JPEG stream)
        if norm_path.startswith("/api/devices/") and norm_path.endswith("/screen-frame"):
            parts = norm_path.strip("/").split("/")
            if len(parts) >= 4 and parts[0] == "api" and parts[1] == "devices" and parts[3] == "screen-frame":
                raw_device_id = parts[2]
                device_id = unquote(raw_device_id).strip()
                if not device_id:
                    self._send_json({"error": "Nedostaje device_id u putanji"}, 400)
                    return

                # Provera Content-Type hedera
                content_type = self.headers.get("Content-Type", "").split(";")[0].strip().lower()
                if content_type not in ("image/jpeg", "image/jpg", "application/octet-stream"):
                    self._send_json({
                        "error": "Nevalidan Content-Type. Očekuje se 'image/jpeg' ili 'application/octet-stream'."
                    }, 400)
                    return

                # Provera dužine tela
                try:
                    content_length = int(self.headers.get("Content-Length", 0))
                except (ValueError, TypeError):
                    content_length = 0

                MAX_FRAME_SIZE = 10 * 1024 * 1024  # 10 MB limit radi zaštite memorije

                if content_length <= 0:
                    self._send_json({"error": "Telo zahteva je prazno (Content-Length mora biti > 0)"}, 400)
                    return

                if content_length > MAX_FRAME_SIZE:
                    self.close_connection = True
                    self._send_json({
                        "error": f"Veličina frejma prelazi maksimalni limit od 10 MB ({content_length} bajtova)"
                    }, 400)
                    return

                raw_bytes = self.rfile.read(content_length)
                if not raw_bytes or len(raw_bytes) == 0:
                    self._send_json({"error": "Nisu primljeni binarni bajtovi frejma"}, 400)
                    return

                if len(raw_bytes) > MAX_FRAME_SIZE:
                    self.close_connection = True
                    self._send_json({
                        "error": f"Veličina frejma prelazi maksimalni limit od 10 MB ({len(raw_bytes)} bajtova)"
                    }, 400)
                    return

                # Opciono pročitaj X-Client-Timestamp heder
                client_ts_header = self.headers.get("X-Client-Timestamp")
                client_ts = None
                if client_ts_header:
                    try:
                        client_ts = float(client_ts_header)
                    except (ValueError, TypeError):
                        client_ts = None

                ts = client_ts if client_ts is not None else time.time()
                self.fleet_store.store_screen_frame(device_id, raw_bytes, client_ts=ts)

                self._send_json({
                    "status": "ok",
                    "device_id": device_id,
                    "size_bytes": len(raw_bytes),
                    "timestamp": ts,
                })
                return

        # 2. Binarna ruta za prijem .msu / .cab zakrpi sa klijenta
        if norm_path == "/api/updates/upload":
            if not self._require_client_upload_token():
                return
            from servishub.api.updates import save_uploaded_update_package_stream, validate_http_storage_directory
            query_params = parse_qs(parsed.query)
            filename = query_params.get("filename", [None])[0] or self.headers.get("X-Update-Filename")
            kb = query_params.get("kb", [None])[0] or self.headers.get("X-Update-KB")
            os_build = query_params.get("os_build", [None])[0] or self.headers.get("X-OS-Build")
            title = query_params.get("title", [""])[0] or self.headers.get("X-Update-Title", "")
            custom_dir = query_params.get("directory", [None])[0]
            try:
                safe_dir = validate_http_storage_directory(
                    custom_dir,
                    default_root=getattr(self.config, "updates_dir", None),
                    active_config=self.config,
                )
            except ValueError as ve:
                self._send_json({"error": str(ve)}, 400)
                return

            try:
                content_length = int(self.headers.get("Content-Length", 0))
            except (ValueError, TypeError):
                content_length = 0

            if content_length <= 0:
                self._send_json({"error": "Telo zahteva je prazno (Content-Length mora biti > 0)."}, 400)
                return
            if content_length > MAX_PACKAGE_UPLOAD_BYTES:
                self.close_connection = True
                self._send_json({"error": f"Veličina paketa prelazi limit od {MAX_PACKAGE_UPLOAD_BYTES} bajtova."}, 413)
                return

            try:
                saved = save_uploaded_update_package_stream(
                    filename=filename,
                    stream=self.rfile,
                    content_length=content_length,
                    directory=safe_dir,
                    kb=kb,
                    os_build=os_build,
                    title=title,
                )
                self._send_json({
                    "status": "ok",
                    "package": saved.to_dict(),
                }, 200)
            except ValueError as ve:
                self._send_json({"error": str(ve)}, 400)
            except Exception as ex:
                print(f"[ERROR] Greška pri snimanju zakrpe na server: {ex}", file=sys.stderr)
                self._send_json({"error": f"Greška pri snimanju zakrpe: {ex}"}, 500)
            return

        # 3. Binarna ruta za prijem model-specifičnog <Model>.tar paketa drajvera sa klijenta
        if norm_path == "/api/drivers/model-pack/upload":
            if not self._require_client_upload_token():
                return
            from servishub.api.updates import save_uploaded_model_pack_stream, validate_http_storage_directory
            query_params = parse_qs(parsed.query)
            model_name = query_params.get("model", [None])[0] or self.headers.get("X-Model-Name")
            filename = query_params.get("filename", [None])[0] or self.headers.get("X-Model-Filename")
            custom_dir = query_params.get("directory", [None])[0]
            try:
                safe_dir = validate_http_storage_directory(custom_dir, default_root=None, active_config=self.config)
            except ValueError as ve:
                self._send_json({"error": str(ve)}, 400)
                return

            try:
                content_length = int(self.headers.get("Content-Length", 0))
            except (ValueError, TypeError):
                content_length = 0

            if content_length <= 0:
                self._send_json({"error": "Telo zahteva je prazno (Content-Length mora biti > 0)."}, 400)
                return
            if content_length > MAX_PACKAGE_UPLOAD_BYTES:
                self.close_connection = True
                self._send_json({"error": f"Veličina paketa prelazi limit od {MAX_PACKAGE_UPLOAD_BYTES} bajtova."}, 413)
                return

            try:
                saved = save_uploaded_model_pack_stream(
                    model=model_name,
                    stream=self.rfile,
                    content_length=content_length,
                    directory=safe_dir,
                    filename=filename,
                    active_config=self.config,
                )
                saved_path = saved.get("path")
                saved_model = saved.get("model")
                if saved_path and os.path.isfile(saved_path):
                    pool_mgr = self._get_driver_pool_mgr()
                    def _bg_ingest_tar():
                        try:
                            pool_mgr.ingest_archive(saved_path, source="harvest", model=saved_model)
                        except Exception as pool_ex:
                            print(f"[WARN] Pozadinski Driver Pool ingest za {saved_model}.tar nije uspeo: {pool_ex}", file=sys.stderr)
                    threading.Thread(target=_bg_ingest_tar, daemon=True).start()
                self._send_json(saved, 200)
            except ValueError as ve:
                self._send_json({"error": str(ve)}, 400)
            except Exception as ex:
                print(f"[ERROR] Greška pri snimanju model paketa drajvera na server: {ex}", file=sys.stderr)
                self._send_json({"error": f"Greška pri snimanju model paketa: {ex}"}, 500)
            return

        # Standardne JSON POST rute
        try:
            content_length = int(self.headers.get("Content-Length", 0))
        except (ValueError, TypeError):
            content_length = 0
        if content_length < 0:
            self._send_json({"error": "Nevalidan Content-Length"}, 400)
            return
        if content_length > MAX_JSON_BODY_BYTES:
            self.close_connection = True
            self._send_json({"error": f"Telo JSON zahteva prelazi limit od {MAX_JSON_BODY_BYTES} bajtova."}, 413)
            return
        post_data = self.rfile.read(content_length) if content_length > 0 else b"{}"

        try:
            payload = json.loads(post_data.decode("utf-8"))
        except Exception:
            self._send_json({"error": "Nevalidan JSON"}, 400)
            return

        if path == "/api/model-profile/keyboard":
            if not isinstance(payload, dict):
                self._send_json({"error": "Telo zahteva mora biti JSON objekat"}, 400)
                return
            model_name = str(payload.get("model", "")).strip()
            if not model_name:
                self._send_json({"error": "Nedostaje polje 'model'"}, 400)
                return
            from servishub.model_profiles import update_keyboard_override
            profile = update_keyboard_override(
                model_name=model_name,
                removed_keys=payload.get("removed_keys"),
                added_keys=payload.get("added_keys"),
                by=payload.get("by", "serviser"),
                base_layout=payload.get("base_layout"),
                is_iso=payload.get("is_iso"),
                has_numpad=payload.get("has_numpad"),
                confirmed=payload.get("confirmed", True),
                source=payload.get("source"),
            )
            self._send_json({"status": "ok", "profile": profile}, 200)
            return

        if path in ("/api/model-profile/usb", "/api/model-profile/usb-override"):
            if not isinstance(payload, dict):
                self._send_json({"error": "Telo zahteva mora biti JSON objekat"}, 400)
                return
            model_name = str(payload.get("model", "")).strip()
            if not model_name or len(model_name) > 200:
                self._send_json({"error": "Nedostaje ili je nevalidno polje 'model'"}, 400)
                return

            port_count = payload.get("port_count")
            target_ports = payload.get("target_ports")
            detected_ports = payload.get("detected_ports")
            physical_port_locations = payload.get("physical_port_locations")
            charger_ports_count = payload.get("charger_ports_count")
            max_typec_cap = payload.get("max_typec_cap")
            manual_override = payload.get("manual_override")
            serial = payload.get("serial") if payload.get("serial") is not None else payload.get("serial_number", "")
            by = payload.get("by", "serviser")

            has_autolearn = any(
                v is not None
                for v in (target_ports, detected_ports, physical_port_locations, charger_ports_count, max_typec_cap)
            )
            if port_count is None and not has_autolearn:
                self._send_json({"error": "Nedostaje polje 'model' ili 'port_count'"}, 400)
                return
            if manual_override is True and port_count is None:
                self._send_json({"error": "Nedostaje polje 'port_count' za manual_override"}, 400)
                return

            from servishub.model_profiles import update_usb_override
            try:
                profile = update_usb_override(
                    model_name=model_name,
                    port_count=port_count,
                    by=by,
                    target_ports=target_ports,
                    detected_ports=detected_ports,
                    physical_port_locations=physical_port_locations,
                    charger_ports_count=charger_ports_count,
                    max_typec_cap=max_typec_cap,
                    manual_override=manual_override,
                    serial=serial,
                )
            except ValueError as ve:
                self._send_json({"error": str(ve)}, 400)
                return
            self._send_json({"status": "ok", "profile": profile}, 200)
            return

        if path == "/api/model-profile/dna":
            if not isinstance(payload, dict):
                self._send_json({"error": "Telo zahteva mora biti JSON objekat"}, 400)
                return
            model_name = str(payload.get("model", "")).strip()
            hwids = payload.get("hwids")
            if isinstance(hwids, str):
                hwids = [hwids]
            if not model_name or not isinstance(hwids, list) or len([h for h in hwids if str(h).strip()]) == 0:
                self._send_json({"error": "Nedostaje obavezno polje 'model' ili lista 'hwids'"}, 400)
                return
            from servishub.model_profiles import update_model_dna
            profile = update_model_dna(
                model_name=model_name,
                hwids=hwids,
                manufacturer=str(payload.get("manufacturer") or ""),
                os_build=str(payload.get("os_build") or ""),
                by=str(payload.get("by") or "wis_client"),
                model_aliases=payload.get("model_aliases") if isinstance(payload.get("model_aliases"), list) else None,
            )
            self._send_json({"status": "ok", "profile": profile}, 200)
            return

        if path == "/api/predictive/prewarm":
            if not isinstance(payload, dict):
                self._send_json({"error": "Telo zahteva mora biti JSON objekat"}, 400)
                return
            mgr = self._get_predictive_mgr()
            try:
                res = mgr.prewarm_device(
                    manufacturer=str(payload.get("manufacturer") or ""),
                    model=str(payload.get("model") or ""),
                    serial_number=str(payload.get("serial_number") or payload.get("serial") or ""),
                    sku=str(payload.get("sku") or ""),
                    os_build=str(payload.get("os_build") or "24H2"),
                    hwids=payload.get("hwids") if isinstance(payload.get("hwids"), list) else None,
                    driver_cache_mgr=self.driver_cache_mgr,
                    ms_catalog_scraper=self.ms_catalog_scraper,
                    config=self.config,
                    driver_pool_mgr=self._get_driver_pool_mgr(),
                )
                self._send_json(res, 200)
            except ValueError as ve:
                self._send_json({"error": str(ve)}, 400)
            except Exception as ex:
                self._send_json({"error": f"Greška pri prediktivnom keširanju: {ex}"}, 500)
            return

        if path == "/api/wis/usb/burn":
            if not self._require_admin_token_if_configured():
                return
            if not isinstance(payload, dict):
                self._send_json({"error": "Telo zahteva mora biti JSON objekat"}, 400)
                return

            device_path = payload.get("device_path")
            if not device_path or not isinstance(device_path, str):
                self._send_json({"error": "Nedostaje obavezno polje 'device_path'"}, 400)
                return

            confirm_wipe = payload.get("confirm_wipe")
            if confirm_wipe is not True:
                self._send_json({"error": "Zahteva se eksplicitna potvrda brisanja uređaja (confirm_wipe=true)"}, 400)
                return

            from servishub.api.updates import validate_http_iso_path
            try:
                iso_path = validate_http_iso_path(payload.get("iso_path"), active_config=self.config)
            except ValueError as ve:
                self._send_json({"error": str(ve)}, 400)
                return

            with WIS_BURN_LOCK:
                if WIS_BURN_STATE.get("is_running"):
                    self._send_json({"error": "Proces narezivanja USB-a je već pokrenut", "status": "busy"}, 409)
                    return
                WIS_BURN_STATE["is_running"] = True
                WIS_BURN_STATE["phase"] = "starting"
                WIS_BURN_STATE["percent"] = 0
                WIS_BURN_STATE["message"] = f"Pokretanje procesa narezivanja na {device_path}..."
                WIS_BURN_STATE["device_path"] = device_path
                WIS_BURN_STATE["error"] = None
                WIS_BURN_STATE["completed"] = False

            wis_dir = getattr(self.config, "wis_dir", None)
            t = threading.Thread(
                target=_run_wis_burn_process,
                args=(device_path, iso_path, wis_dir),
                daemon=True,
            )
            t.start()

            self._send_json({
                "status": "started",
                "message": "Narezivanje USB-a je pokrenuto.",
                "device_path": device_path,
            })
            return

        if path == "/api/drivers/pool/bundle":
            if not isinstance(payload, dict):
                self._send_json({"error": "Telo zahteva mora biti JSON objekat"}, 400)
                return
            model_val = str(payload.get("model") or "").strip()
            mfg_val = str(payload.get("manufacturer") or "").strip()
            system_sku = str(payload.get("system_sku") or payload.get("system_id") or payload.get("sku") or "").strip()
            missing_hwids = payload.get("missing_hwids")
            if isinstance(missing_hwids, str):
                missing_hwids = [missing_hwids]
            elif not isinstance(missing_hwids, list):
                missing_hwids = None
            all_hwids = payload.get("hwids")
            if isinstance(all_hwids, str):
                all_hwids = [all_hwids]
            elif not isinstance(all_hwids, list):
                all_hwids = None
            metadata_only = bool(payload.get("metadata_only", False))

            pool_mgr = self._get_driver_pool_mgr()

            # IMP-98: Rani P2 OEM Prefetch (FastNas): kad model podržava OEM (čak i bez manufacturer-a)
            # i u manifestu još nema oem_pack izvora, pokreni deduplikovani background ingest.
            oem_bg_info = None
            if model_val:
                oem_bg_info = self._maybe_trigger_async_oem_ingest(
                    pool_mgr,
                    manufacturer=mfg_val,
                    model=model_val,
                    system_sku=system_sku,
                    require_catalog_entry=False,
                    force_trigger=True,
                )

            try:
                bundle = pool_mgr.build_bundle_tar(
                    model=model_val,
                    missing_hwids=missing_hwids,
                    all_hwids=all_hwids,
                    allow_stream_plan=True,
                )
            except Exception as ex:
                self._send_json({"error": f"Greška pri sklapanju Driver Pool paketa: {ex}"}, 500)
                return

            if metadata_only:
                out_meta = dict(bundle)
                out_meta.pop("bundle_path", None)
                out_meta.pop("package_entries", None)
                if oem_bg_info:
                    out_meta["oem_ingest_triggered"] = True
                    out_meta["oem_job_id"] = (oem_bg_info.get("job") or {}).get("job_id", "")
                self._send_json(out_meta, 200)
                return

            if bundle.get("available") and bundle.get("stream_only"):
                try:
                    extra = {
                        "X-Model-Name": bundle.get("model") or model_val or "pool",
                        "X-Driver-Pool-Count": str(bundle.get("package_count") or 0),
                        "X-Driver-Pool-Sources": ",".join(bundle.get("sources") or []),
                        "X-Driver-Pool-Cache-Hit": "false",
                        "X-Driver-Pool-Stream": "true",
                    }
                    if oem_bg_info:
                        extra["X-Driver-Pool-OEM-Ingest"] = "started" if oem_bg_info.get("started") else "running"
                    self._stream_packages_tar(
                        bundle.get("package_entries") or [],
                        bundle.get("bundle_filename") or "driver_pool_bundle.tar",
                        extra_headers=extra,
                    )
                except (BrokenPipeError, ConnectionResetError):
                    pass
                except Exception as e:
                    print(f"[ERROR] Greška pri on-the-fly strimovanju Driver Pool bundle-a: {e}", file=sys.stderr)
                return

            if not bundle.get("available") or not bundle.get("bundle_path") or not os.path.isfile(bundle["bundle_path"]):
                oem_entry = pool_mgr.catalog_mgr.find_driver_pack(
                    manufacturer=mfg_val,
                    model=model_val,
                    system_sku=system_sku,
                )
                self._send_json({
                    "status": "not_found",
                    "available": False,
                    "model": model_val,
                    "oem_pack_available": bool(oem_entry),
                    "oem_entry": {
                        "package_id": oem_entry.get("package_id"),
                        "package_name": oem_entry.get("package_name"),
                        "version": oem_entry.get("version"),
                        "release_date": oem_entry.get("release_date"),
                    } if oem_entry else None,
                }, 404)
                return

            try:
                extra = {
                    "X-Model-Name": bundle.get("model") or model_val or "pool",
                    "X-Model-SHA256": bundle.get("sha256") or "",
                    "X-Driver-Pool-Count": str(bundle.get("package_count") or 0),
                    "X-Driver-Pool-Sources": ",".join(bundle.get("sources") or []),
                    "X-Driver-Pool-Cache-Hit": "true" if bundle.get("cache_hit") else "false",
                }
                self._stream_file(
                    bundle["bundle_path"],
                    bundle.get("bundle_filename") or "driver_pool_bundle.tar",
                    content_type="application/x-tar",
                    extra_headers=extra,
                )
            except (BrokenPipeError, ConnectionResetError):
                pass
            except Exception as e:
                print(f"[ERROR] Greška pri strimovanju Driver Pool bundle-a: {e}", file=sys.stderr)
            return

        if path == "/api/drivers/pool/sync-catalogs":
            if not self._require_admin_token_if_configured():
                return
            if payload is None:
                payload = {}
            if not isinstance(payload, dict):
                self._send_json({"error": "Telo zahteva mora biti JSON objekat"}, 400)
                return
            vendor_val = str(payload.get("vendor") or "all").strip().lower()
            force_val = bool(payload.get("force", False))
            pool_mgr = self._get_driver_pool_mgr()
            try:
                if vendor_val in ("", "all"):
                    res = pool_mgr.catalog_mgr.sync_all_catalogs(force=force_val)
                else:
                    res = pool_mgr.catalog_mgr.sync_catalog(vendor_val, force=force_val)
                if hasattr(pool_mgr, "invalidate_stats_cache"):
                    pool_mgr.invalidate_stats_cache()
                self._send_json(res, 200)
            except ValueError as ve:
                self._send_json({"error": str(ve)}, 400)
            except Exception as ex:
                self._send_json({"error": f"Greška pri sinhronizaciji OEM kataloga: {ex}"}, 502)
            return

        if path == "/api/drivers/pool/ingest-oem":
            if not self._require_admin_token_if_configured():
                return
            if not isinstance(payload, dict):
                self._send_json({"error": "Telo zahteva mora biti JSON objekat"}, 400)
                return
            mfg_val = str(payload.get("manufacturer") or "").strip()
            model_val = str(payload.get("model") or "").strip()
            os_ver = str(payload.get("os_ver") or "win11").strip()
            sys_id = str(payload.get("system_id") or "").strip()
            force_sync = bool(payload.get("force_sync", False))
            async_mode = bool(payload.get("async_mode", True))
            if not (mfg_val or model_val) or not model_val:
                self._send_json({"error": "Polja 'manufacturer' i 'model' su obavezna."}, 400)
                return
            pool_mgr = self._get_driver_pool_mgr()
            if async_mode:
                job = pool_mgr.start_background_job(
                    "ingest_oem",
                    pool_mgr.ingest_oem_pack,
                    mfg_val,
                    model_val,
                    os_ver=os_ver,
                    system_id=sys_id,
                    force_sync=force_sync,
                )
                self._send_json({"status": "started", "job": job}, 202)
                return
            try:
                res = pool_mgr.ingest_oem_pack(
                    mfg_val,
                    model_val,
                    os_ver=os_ver,
                    system_id=sys_id,
                    force_sync=force_sync,
                )
                st = res.get("status")
                code = 400 if st == "unsupported_vendor" else (404 if st == "not_found" else (502 if st == "catalog_error" else 200))
                self._send_json(res, code)
            except ValueError as ve:
                self._send_json({"error": str(ve)}, 400)
            except Exception as ex:
                self._send_json({"error": f"Greška pri OEM ingestu: {ex}"}, 500)
            return

        if path == "/api/drivers/pool/migrate":
            if not self._require_admin_token_if_configured():
                return
            if payload is None:
                payload = {}
            if not isinstance(payload, dict):
                self._send_json({"error": "Telo zahteva mora biti JSON objekat"}, 400)
                return
            from servishub.api.updates import validate_http_storage_directory
            try:
                packs_dir = validate_http_storage_directory(
                    payload.get("model_packs_dir"),
                    default_root=getattr(self.config, "model_packs_dir", None),
                    active_config=self.config,
                )
                cabs_dir = validate_http_storage_directory(
                    payload.get("drivers_cache_dir"),
                    default_root=getattr(self.config, "drivers_cache_dir", None),
                    active_config=self.config,
                )
            except ValueError as ve:
                self._send_json({"error": str(ve)}, 400)
                return
            async_mode = bool(payload.get("async_mode", False))
            pool_mgr = self._get_driver_pool_mgr()
            if async_mode:
                job = pool_mgr.start_background_job(
                    "migrate",
                    pool_mgr.migrate_legacy_stores,
                    model_packs_dir=packs_dir,
                    drivers_cache_dir=cabs_dir,
                )
                self._send_json({"status": "started", "job": job}, 202)
                return
            try:
                res = pool_mgr.migrate_legacy_stores(model_packs_dir=packs_dir, drivers_cache_dir=cabs_dir)
                self._send_json(res, 200)
            except Exception as ex:
                self._send_json({"error": f"Greška pri migraciji Driver Pool-a: {ex}"}, 500)
            return

        if path == "/api/drivers/pool/prune":
            if not self._require_admin_token_if_configured():
                return
            if payload is None:
                payload = {}
            if not isinstance(payload, dict):
                self._send_json({"error": "Telo zahteva mora biti JSON objekat"}, 400)
                return
            try:
                max_age = int(payload.get("max_bundle_age_sec", 86400))
            except (TypeError, ValueError):
                self._send_json({"error": "Nevalidan parametar 'max_bundle_age_sec'."}, 400)
                return
            pool_mgr = self._get_driver_pool_mgr()
            res = pool_mgr.prune_unused_packages(max_bundle_age_sec=max_age)
            self._send_json(res, 200)
            return

        if path == "/api/drivers/pool/delta-check":
            if not self._require_client_upload_token():
                return
            if not isinstance(payload, dict):
                self._send_json({"error": "Telo zahteva mora biti JSON objekat"}, 400)
                return
            model_val = str(payload.get("model") or "").strip()
            os_build = str(payload.get("os_build") or "").strip()
            packages_val = payload.get("packages")
            if packages_val is not None and not isinstance(packages_val, list):
                self._send_json({"error": "Polje 'packages' mora biti lista objekata."}, 400)
                return
            pool_mgr = self._get_driver_pool_mgr()
            try:
                res = pool_mgr.delta_check(
                    model=model_val,
                    packages=packages_val or [],
                    os_build=os_build,
                )
                self._send_json(res, 200)
            except Exception as ex:
                self._send_json({"error": f"Greška pri delta-check proveri: {ex}"}, 500)
            return

        if path == "/api/drivers/pool/notify-harvest":
            if not self._require_client_upload_token():
                return
            if payload is None:
                payload = {}
            if not isinstance(payload, dict):
                self._send_json({"error": "Telo zahteva mora biti JSON objekat"}, 400)
                return
            packs_dir = getattr(self.config, "model_packs_dir", None)
            target_archive = str(payload.get("archive") or payload.get("filename") or "").strip()
            if not target_archive and payload.get("model"):
                from servishub.drivers.pool import sanitize_model_pack_name
                clean_m = sanitize_model_pack_name(str(payload.get("model")))
                if clean_m and clean_m != "Unknown_Model":
                    target_archive = f"{clean_m}.tar"
                    if packs_dir and os.path.isdir(packs_dir):
                        for cand_ext in (".tar.gz", ".tgz", ".tar"):
                            cand_name = f"{clean_m}{cand_ext}"
                            if os.path.isfile(os.path.join(packs_dir, cand_name)):
                                target_archive = cand_name
                                break
            sync_mode = bool(payload.get("sync", False))
            delete_ingested = bool(
                payload.get(
                    "delete_ingested",
                    getattr(self.config, "delete_ingested_archives", True) if self.config else True,
                )
            )
            pool_mgr = self._get_driver_pool_mgr()
            if sync_mode:
                try:
                    res = pool_mgr.scan_and_ingest_archives(
                        packs_dir,
                        target_archive=target_archive or None,
                        delete_ingested=delete_ingested,
                    )
                    self._send_json(res, 200)
                except Exception as ex:
                    self._send_json({"error": f"Greška pri skeniranju Harvest arhiva: {ex}"}, 500)
                return
            job = pool_mgr.start_background_job(
                "notify_harvest",
                pool_mgr.scan_and_ingest_archives,
                packs_dir,
                target_archive=target_archive or None,
                delete_ingested=delete_ingested,
            )
            self._send_json({"status": "accepted", "job": job, "target_archive": target_archive or None}, 202)
            return

        if path in ("/api/drivers/resolve", "/api/drivers/resolve_batch"):
            if not isinstance(payload, dict):
                self._send_json({"error": "Telo zahteva mora biti JSON objekat"}, 400)
                return

            raw_hwids = payload.get("hwids", [])
            if isinstance(raw_hwids, str):
                raw_hwids = [raw_hwids]
            elif not isinstance(raw_hwids, list):
                raw_hwids = []

            devices_payload = payload.get("devices")
            if devices_payload is not None and not isinstance(devices_payload, list):
                self._send_json({"error": "Polje 'devices' mora biti lista uređaja."}, 400)
                return

            device_name = payload.get("device_name", "UnknownDevice")
            manufacturer = str(payload.get("manufacturer") or "").strip()
            model = str(payload.get("model") or "").strip()
            system_sku = str(payload.get("system_sku") or payload.get("system_id") or payload.get("sku") or "").strip()
            serial_number = str(payload.get("serial_number") or payload.get("serial") or "").strip()
            req_stage = str(payload.get("stage") or payload.get("source_stage") or payload.get("caller") or "").strip()
            req_error_code = payload.get("error_code", payload.get("cm_error_code", payload.get("problem_code")))
            include_warranty = bool(payload.get("include_warranty", False))
            async_download = bool(payload.get("async_download", False))
            inline_wait_raw = payload.get("inline_wait_sec")
            inline_wait_sec = None
            if inline_wait_raw is not None:
                try:
                    inline_wait_sec = float(inline_wait_raw)
                except (TypeError, ValueError):
                    inline_wait_sec = None

            has_devices_batch = isinstance(devices_payload, list) and len(devices_payload) > 0
            if not raw_hwids and not has_devices_batch and not (manufacturer and (serial_number or model)):
                self._send_json({"error": "Polje 'hwids' ili 'devices' mora sadržati bar jedan HWID ili OEM parametre (manufacturer, serial_number/model)"}, 400)
                return

            warranty_info = None
            oem_info = None

            if manufacturer:
                try:
                    provider = get_oem_provider(manufacturer)
                    if provider:
                        if include_warranty:
                            try:
                                warranty_info = provider.get_warranty_info(serial_number, model)
                            except Exception as e:
                                print(f"[!] [OEM] Greška pri dohvatanju garancije: {e}", file=sys.stderr)
                                warranty_info = {
                                    "status": "fallback",
                                    "in_warranty": False,
                                    "status_label": "Nepoznato (Greška)",
                                    "error": str(e),
                                }

                        try:
                            oem_info = provider.resolve_driver_pack(model)
                        except Exception as e:
                            print(f"[!] [OEM] Greška pri rezoluciji SCCM paketa: {e}", file=sys.stderr)
                            oem_info = {
                                "status": "error",
                                "error": str(e),
                            }
                except Exception as e:
                    print(f"[!] [OEM] Greška pri radu sa OEM provajderom: {e}", file=sys.stderr)

            results = []
            device_entries_out = None

            if has_devices_batch:
                normalized_devices = []
                for idx, dev in enumerate(devices_payload):
                    if not isinstance(dev, dict):
                        continue
                    d_name = str(dev.get("device_name") or f"Device-{idx + 1}")
                    d_mfg = str(dev.get("manufacturer") or manufacturer).strip()
                    d_mod = str(dev.get("model") or model).strip()
                    d_sku = str(dev.get("system_sku") or dev.get("system_id") or dev.get("sku") or system_sku).strip()
                    d_stage = str(dev.get("stage") or dev.get("source_stage") or req_stage).strip()
                    d_err = dev.get("error_code", dev.get("cm_error_code", dev.get("problem_code", req_error_code)))
                    d_hwids = dev.get("hwids", [])
                    if isinstance(d_hwids, str):
                        d_hwids = [d_hwids]
                    elif not isinstance(d_hwids, list):
                        d_hwids = []
                    normalized_devices.append((d_name, d_hwids, d_mfg, d_mod, d_sku, d_stage, d_err))

                def _resolve_dev_task(item):
                    d_name, d_hwids, d_mfg, d_mod, d_sku, d_stage, d_err = item
                    d_res = self._resolve_single_device_hwids(
                        d_hwids,
                        device_name=d_name,
                        manufacturer=d_mfg,
                        model=d_mod,
                        system_sku=d_sku,
                        async_download=async_download,
                        inline_wait_sec=inline_wait_sec,
                        stage=d_stage,
                        error_code=d_err,
                    )
                    resolved_item = next((r for r in d_res if r.get("status") == "ready"), None)
                    return {
                        "device_name": d_name,
                        "count": len(d_res),
                        "results": d_res,
                        "resolved": resolved_item,
                    }

                if len(normalized_devices) > 1:
                    with ThreadPoolExecutor(max_workers=min(4, len(normalized_devices))) as pool:
                        device_entries_out = list(pool.map(_resolve_dev_task, normalized_devices))
                else:
                    device_entries_out = [_resolve_dev_task(item) for item in normalized_devices]

                for dev_out in device_entries_out:
                    results.extend(dev_out["results"])
            else:
                results = self._resolve_single_device_hwids(
                    raw_hwids,
                    device_name=device_name,
                    manufacturer=manufacturer,
                    model=model,
                    system_sku=system_sku,
                    async_download=async_download,
                    inline_wait_sec=inline_wait_sec,
                    stage=req_stage,
                    error_code=req_error_code,
                )

            if model and model.lower() not in ("unknown", "standardni pc", "pc", "system product name", "to be filled by o.e.m.", "none", "default string"):
                all_req_hwids = list(raw_hwids)
                if has_devices_batch:
                    for dev in devices_payload:
                        if isinstance(dev, dict):
                            dh = dev.get("hwids", [])
                            if isinstance(dh, str):
                                all_req_hwids.append(dh)
                            elif isinstance(dh, list):
                                all_req_hwids.extend(dh)
                if all_req_hwids:
                    try:
                        from servishub.model_profiles import update_model_dna
                        req_aliases = payload.get("model_aliases") if isinstance(payload.get("model_aliases"), list) else None
                        update_model_dna(model, all_req_hwids, manufacturer=manufacturer, by="driver_resolve", model_aliases=req_aliases)
                    except Exception:
                        pass

            resp_payload = {
                "status": "ok",
                "device_name": device_name,
                "count": len(results),
                "results": results,
            }
            oem_triggered_item = next(
                (r for r in results if isinstance(r, dict) and (r.get("oem_ingest_triggered") or r.get("source") == "oem_pack_async")),
                None,
            )
            if oem_triggered_item:
                resp_payload["oem_ingest_triggered"] = True
                resp_payload["oem_job_id"] = oem_triggered_item.get("oem_job_id") or oem_triggered_item.get("job_id") or ""
            if device_entries_out is not None:
                resp_payload["devices"] = device_entries_out
                resp_payload["resolved_count"] = sum(1 for d in device_entries_out if d.get("resolved"))
            if warranty_info is not None:
                resp_payload["warranty"] = warranty_info
            if oem_info is not None:
                resp_payload["oem"] = oem_info

            self._send_json(resp_payload)
            return

        if path == "/api/drivers/check_update":
            if not isinstance(payload, dict):
                self._send_json({"error": "Telo zahteva mora biti JSON objekat"}, 400)
                return

            raw_hwids = payload.get("hwids", [])
            if isinstance(raw_hwids, str):
                raw_hwids = [raw_hwids]
            elif not isinstance(raw_hwids, list):
                raw_hwids = []

            single_hwid = payload.get("hwid")
            if single_hwid and isinstance(single_hwid, str) and single_hwid not in raw_hwids:
                raw_hwids.insert(0, single_hwid)

            if not raw_hwids:
                self._send_json({"error": "Polje 'hwids' mora sadržati listu hardverskih identifikatora (HWID)"}, 400)
                return

            curr_ver = str(payload.get("current_version", "")).strip()
            curr_date = str(payload.get("current_date", "")).strip()
            device_name = str(payload.get("device_name", "UnknownDevice")).strip()

            candidates = []
            for h in raw_hwids:
                h_str = str(h).strip()
                if not h_str:
                    continue
                for cand in generate_hwid_candidates(h_str):
                    if cand not in candidates:
                        candidates.append(cand)

            if not candidates:
                self._send_json({"error": "Nije moguće generisati HWID kandidate iz prosleđenih parametara"}, 400)
                return

            print(f"[*] [CheckUpdate] Provera ažuriranja za {device_name} (HWID: {candidates[0]}, Trenutna verzija: '{curr_ver}', datum: '{curr_date}')...")

            cached_driver = None
            if self.driver_cache_mgr:
                for cand in candidates:
                    c = self.driver_cache_mgr.get_cached(cand)
                    if c:
                        cached_driver = c
                        break

            if cached_driver:
                cat_ver = cached_driver.get("version", "")
                cat_date = cached_driver.get("date", "")
                if is_catalog_driver_newer(curr_ver, curr_date, cat_ver, cat_date):
                    drv_id = cached_driver.get("driver_id", "")
                    print(f"  [+] [CACHE HIT NEWER] {candidates[0]} -> {cached_driver.get('title')} (v{cat_ver}, {cat_date})")
                    self._send_json({
                        "status": "ok",
                        "update_available": True,
                        "current_version": curr_ver,
                        "current_date": curr_date,
                        "latest_version": cat_ver,
                        "latest_date": cat_date,
                        "title": cached_driver.get("title", ""),
                        "driver_id": drv_id,
                        "download_url": f"/api/drivers/download?id={drv_id}",
                        "size_bytes": cached_driver.get("size_bytes", 0),
                        "source": "cache"
                    })
                    return

            print(f"  [*] [CheckUpdate] Pretražujem Microsoft Update Catalog za HWID kandidate...")
            matched_catalog = None
            matched_candidate = None

            if self.ms_catalog_scraper:
                for cand in candidates:
                    entries = self.ms_catalog_scraper.search(cand)
                    if not entries:
                        continue
                    filtered = self.ms_catalog_scraper.filter_for_win_x64(entries)
                    if not filtered:
                        continue
                    matched_catalog = filtered[0]
                    matched_candidate = cand
                    break

            if not matched_catalog:
                if cached_driver:
                    self._send_json({
                        "status": "ok",
                        "update_available": False,
                        "current_version": curr_ver,
                        "latest_version": cached_driver.get("version", ""),
                        "latest_date": cached_driver.get("date", ""),
                        "message": "Drajver je već najnovija verzija."
                    })
                    return
                else:
                    self._send_json({
                        "status": "ok",
                        "update_available": False,
                        "not_found": True,
                        "message": "Nije pronađen drajver na OEM katalogu."
                    })
                    return

            cat_ver = matched_catalog.get("version", "")
            cat_date_str = matched_catalog.get("date_str", "")
            if not cat_date_str and matched_catalog.get("date"):
                d = matched_catalog.get("date")
                cat_date_str = d.strftime("%m/%d/%Y")

            if is_catalog_driver_newer(curr_ver, curr_date, cat_ver, cat_date_str):
                urls = self.ms_catalog_scraper.get_download_urls(matched_catalog["update_id"])
                if not urls:
                    self._send_json({
                        "status": "error",
                        "error": f"Nije pronađen direktan link za preuzimanje drajvera sa Microsoft kataloga."
                    }, 500)
                    return

                cab_url = urls[0]
                print(f"  [*] [CheckUpdate] Preuzimam noviji drajver sa Microsoft CDN-a: {matched_catalog.get('title')}...")
                try:
                    cab_data = self.ms_catalog_scraper.download_cab(cab_url)
                    matched_catalog["download_url"] = cab_url
                    matched_catalog["matched_query"] = matched_candidate
                    aliases = list(set(raw_hwids + [matched_candidate]))
                    saved = self.driver_cache_mgr.save_cache(matched_candidate, cab_data, matched_catalog, alias_hwids=aliases) if self.driver_cache_mgr else matched_catalog
                    drv_id = saved.get("driver_id", "")
                    self._send_json({
                        "status": "ok",
                        "update_available": True,
                        "current_version": curr_ver,
                        "current_date": curr_date,
                        "latest_version": cat_ver,
                        "latest_date": cat_date_str,
                        "title": matched_catalog.get("title", ""),
                        "driver_id": drv_id,
                        "download_url": f"/api/drivers/download?id={drv_id}",
                        "size_bytes": saved.get("size_bytes", len(cab_data))
                    })
                    return
                except Exception as ex:
                    print(f"  [!] [CheckUpdate] Greška pri preuzimanju drajvera: {ex}", file=sys.stderr)
                    self._send_json({
                        "status": "error",
                        "error": f"Greška pri preuzimanju drajvera sa Microsoft CDN-a: {str(ex)}"
                    }, 500)
                    return
            else:
                self._send_json({
                    "status": "ok",
                    "update_available": False,
                    "current_version": curr_ver,
                    "latest_version": cat_ver,
                    "latest_date": cat_date_str,
                    "message": "Drajver je već najnovija verzija."
                })
                return

        if path == "/api/fleet/command":
            if not self._require_admin_token_if_configured():
                return
            if not isinstance(payload, dict):
                self._send_json({"error": "Telo zahteva mora biti JSON objekat"}, 400)
                return

            action = payload.get("action")
            params = payload.get("params")
            filter_status = payload.get("filter_status")
            filter_phase = payload.get("filter_phase")
            version_lt = payload.get("version_lt")

            try:
                res = self.fleet_store.enqueue_fleet_command(
                    action=action,
                    params=params,
                    filter_status=filter_status,
                    filter_phase=filter_phase,
                    version_lt=version_lt,
                )
            except ValueError as e:
                self._send_json({"error": f"Nevalidna broadcast komanda: {e}"}, 400)
                return
            except Exception as e:
                self._send_json({"error": f"Greška pri slanju broadcast komande: {e}"}, 500)
                return

            self._send_json(res, 200)
            return

        if path.startswith("/api/devices/") and path.endswith("/command-ack"):
            parts = path.strip("/").split("/")
            if len(parts) >= 4 and parts[0] == "api" and parts[1] == "devices" and parts[3] == "command-ack":
                if not self._require_client_upload_token():
                    return
                device_id = unquote(parts[2]).strip()
                if not device_id:
                    self._send_json({"error": "Nedostaje device_id u putanji"}, 400)
                    return
                if not isinstance(payload, dict):
                    self._send_json({"error": "Telo zahteva mora biti JSON objekat"}, 400)
                    return

                command_id = payload.get("command_id")
                action = payload.get("action", "")
                ack_status = payload.get("status", "ok")
                detail = payload.get("detail", payload.get("message", ""))
                applied_at = payload.get("applied_at") or payload.get("executed_at")

                try:
                    ack = self.fleet_store.record_command_ack(
                        device_id=device_id,
                        command_id=command_id,
                        action=action,
                        status=ack_status,
                        detail=detail,
                        applied_at=applied_at,
                    )
                except ValueError as e:
                    self._send_json({"error": str(e)}, 400)
                    return

                self._send_json({
                    "status": "ok",
                    "device_id": device_id,
                    "ack": ack,
                }, 200)
                return

        if path.startswith("/api/devices/") and path.endswith("/autopilot"):
            parts = path.strip("/").split("/")
            if len(parts) >= 4 and parts[0] == "api" and parts[1] == "devices" and parts[3] == "autopilot":
                if not self._require_client_upload_token():
                    return
                device_id = unquote(parts[2]).strip()
                if not device_id:
                    self._send_json({"error": "Nedostaje device_id u putanji"}, 400)
                    return
                if not isinstance(payload, dict):
                    self._send_json({"error": "Telo zahteva mora biti JSON objekat"}, 400)
                    return

                try:
                    res = self.fleet_store.record_autopilot_status(
                        device_id=device_id,
                        payload=payload,
                        client_ip=self.client_address[0],
                    )
                except ValueError as e:
                    self._send_json({"error": str(e)}, 400)
                    return
                except Exception as e:
                    self._send_json({"error": f"Greška pri upisu Autopilot statusa: {e}"}, 500)
                    return

                self._send_json(res, 200)
                return

        if path.startswith("/api/devices/") and (path.endswith("/command") or path.endswith("/commands")):
            parts = path.strip("/").split("/")
            if len(parts) >= 4 and parts[0] == "api" and parts[1] == "devices" and parts[3] in ("command", "commands"):
                if not self._require_admin_token_if_configured():
                    return
                raw_dev_id = parts[2]
                device_id = unquote(raw_dev_id).strip()
                if not device_id:
                    self._send_json({"error": "Nedostaje device_id u putanji"}, 400)
                    return

                if not isinstance(payload, dict):
                    self._send_json({"error": "Telo zahteva mora biti JSON objekat"}, 400)
                    return

                action = payload.get("action")
                if not action or not isinstance(action, str) or not action.strip():
                    self._send_json({"error": "Polje 'action' je obavezno i mora biti neprazan string"}, 400)
                    return
                action = action.strip()

                params = payload.get("params")
                if params is not None and not isinstance(params, dict):
                    self._send_json({"error": "Polje 'params' mora biti JSON objekat ako je navedeno"}, 400)
                    return

                try:
                    cmd_dict = self.fleet_store.enqueue_command(device_id, action, params=params)
                except Exception as e:
                    self._send_json({"error": f"Neuspeh dodavanja komande: {e}"}, 400)
                    return

                self._send_json({
                    "status": "ok",
                    "device_id": device_id,
                    "command": cmd_dict,
                }, 200)
                return

        if path.startswith("/api/devices/") and path.endswith("/report"):
            parts = path.strip("/").split("/")
            if len(parts) >= 4 and parts[0] == "api" and parts[1] == "devices" and parts[3] == "report":
                url_dev_id = unquote(parts[2]).strip()
                if not isinstance(payload, dict):
                    self._send_json({"error": "Telo zahteva mora biti JSON objekat"}, 400)
                    return

                dev_id = payload.get("device_id") or url_dev_id
                clean_id = sanitize_id(dev_id)
                comp_name = payload.get("computer_name") or dev_id
                prev_host = str(payload.get("previous_hostname") or "").strip() or None

                # IMP-94b: Parcijalni HwTest payload iz Faze 5 ne sme kreirati ili pregaziti završni karton
                if is_partial_hwtest_payload(payload):
                    serial_raw = payload.get("serial_number") or (payload.get("hw") or {}).get("serial")
                    serial_num = str(serial_raw).strip() if not is_placeholder_hw_value(serial_raw) else ""
                    with self.fleet_store.fleet_lock:
                        target_key = clean_id
                        if hasattr(self.fleet_store, "_resolve_device_key_locked"):
                            resolved_k = self.fleet_store._resolve_device_key_locked(
                                dev_id,
                                canonical_sn=serial_num.upper() if serial_num else "",
                                previous_hostname=prev_host,
                            )
                            if resolved_k:
                                target_key = resolved_k
                        rec = self.fleet_store.fleet_devices.setdefault(target_key, {"computer_name": comp_name})
                        if isinstance(payload.get("hwtest"), dict):
                            rec["hwtest"] = dict(payload["hwtest"])
                        if isinstance(payload.get("hw"), dict):
                            rec["hw"] = merge_hw_dicts(rec.get("hw") if isinstance(rec.get("hw"), dict) else {}, payload["hw"])
                        if isinstance(payload.get("autopilot"), dict) and payload["autopilot"]:
                            rec["autopilot"] = merge_autopilot_status(rec.get("autopilot"), payload["autopilot"], default_source="phase5_hwtest")
                        if "manual_driver_bypass" in payload:
                            rec["manual_driver_bypass"] = bool(payload.get("manual_driver_bypass"))
                        if payload.get("driver_gate_emergency_override_reason"):
                            rec["driver_gate_emergency_override_reason"] = str(payload["driver_gate_emergency_override_reason"])
                        rec["last_seen"] = time.time()
                    self._send_json({
                        "status": "ok",
                        "partial": True,
                        "report_saved": False,
                        "message": "Parcijalni HwTest payload zabeležen u memoriji (završni karton nije kreiran/pregažen).",
                        "device_id": dev_id,
                    }, 200)
                    return

                # IMP-107: PRVO pod fleet_lock obogati dolazni payload živim existing_rec i povezanim zapisima
                # (merge_hw_dicts + merge_app_statuses + merge_autopilot_status + merge_image_profile + merge_report_payloads),
                # PA TEK ONDA pozovi save_device_report(...) i upiši spojeni izveštaj u existing_rec["latest_report"]!
                serial_hint_raw = payload.get("serial_number") or (payload.get("hw") or {}).get("serial")
                serial_hint = str(serial_hint_raw).strip() if not is_placeholder_hw_value(serial_hint_raw) else ""
                incoming_sid = str(payload.get("install_session_id") or "").strip()

                with self.fleet_store.fleet_lock:
                    target_key = clean_id
                    if hasattr(self.fleet_store, "_resolve_device_key_locked"):
                        resolved_k = self.fleet_store._resolve_device_key_locked(
                            dev_id,
                            canonical_sn=serial_hint.upper() if serial_hint else "",
                            previous_hostname=prev_host,
                        )
                        if resolved_k:
                            target_key = resolved_k
                    if incoming_sid and hasattr(self.fleet_store, "_handle_install_session_change_locked") and target_key in self.fleet_store.fleet_devices:
                        self.fleet_store._handle_install_session_change_locked(target_key, incoming_sid, comp_name=comp_name)

                    related_recs = []
                    if target_key in self.fleet_store.fleet_devices:
                        related_recs.append(self.fleet_store.fleet_devices[target_key])
                    if hasattr(self.fleet_store, "_collect_related_records_locked"):
                        for rel in self.fleet_store._collect_related_records_locked(
                            target_key,
                            comp_name=comp_name,
                            serial_num=serial_hint,
                            previous_hostname=prev_host or "",
                        ):
                            if rel not in related_recs:
                                related_recs.append(rel)

                    mem_base = {}
                    mem_hw = {}
                    mem_apps = {}
                    mem_ap = {}
                    mem_img_ctx = {}
                    for r in related_recs:
                        if not isinstance(r, dict):
                            continue
                        r_rep = r.get("latest_report")
                        if isinstance(r_rep, dict) and r_rep:
                            r_rep_sid = str(r_rep.get("install_session_id") or "").strip()
                            if not (incoming_sid and r_rep_sid and r_rep_sid != incoming_sid):
                                mem_base = merge_report_payloads(mem_base, r_rep)
                                if isinstance(r_rep.get("hw"), dict):
                                    mem_hw = merge_hw_dicts(mem_hw, r_rep["hw"])
                                if isinstance(r_rep.get("apps"), (dict, list)) and r_rep.get("apps"):
                                    mem_apps = merge_app_statuses(mem_apps, r_rep["apps"])
                                if isinstance(r_rep.get("autopilot"), dict) and r_rep["autopilot"]:
                                    mem_ap = merge_autopilot_status(mem_ap, r_rep["autopilot"])
                                rt, rv, rexp = extract_image_profile(r_rep)
                                if rexp or rv != "unknown":
                                    mt, mv = merge_image_profile(mem_img_ctx, r_rep)
                                    mem_img_ctx = {"image_type": mt, "wim_version": mv}

                        if isinstance(r.get("hw"), dict) and r["hw"]:
                            mem_hw = merge_hw_dicts(mem_hw, r["hw"])
                        if isinstance(r.get("apps"), (dict, list)) and r.get("apps"):
                            mem_apps = merge_app_statuses(mem_apps, r["apps"])
                        if isinstance(r.get("autopilot"), dict) and r["autopilot"]:
                            mem_ap = merge_autopilot_status(mem_ap, r["autopilot"])
                        for carry_k in (
                            "hwtest",
                            "benchmark",
                            "serial_number",
                            "chassis",
                            "install_session_id",
                            "previous_hostname",
                            "manual_driver_bypass",
                            "driver_gate_emergency_override_reason",
                            "hotfix_applied",
                            "warranty",
                            "oem",
                        ):
                            val = r.get(carry_k)
                            if val not in (None, "", {}) and (carry_k not in mem_base or mem_base.get(carry_k) in (None, "", {})):
                                mem_base[carry_k] = dict(val) if isinstance(val, dict) else val
                        rt2, rv2, rexp2 = extract_image_profile(r)
                        if rexp2 or rv2 != "unknown":
                            mt, mv = merge_image_profile(mem_img_ctx, r)
                            mem_img_ctx = {"image_type": mt, "wim_version": mv}

                    if mem_hw:
                        mem_base["hw"] = merge_hw_dicts(mem_base.get("hw") if isinstance(mem_base.get("hw"), dict) else {}, mem_hw)
                    if mem_apps:
                        mem_base["apps"] = merge_app_statuses(mem_base.get("apps"), mem_apps)
                    if mem_ap:
                        mem_base["autopilot"] = merge_autopilot_status(mem_base.get("autopilot"), mem_ap)
                    if mem_img_ctx:
                        mem_base["image_type"] = mem_img_ctx["image_type"]
                        if mem_img_ctx.get("wim_version", "unknown") != "unknown":
                            mem_base["wim_version"] = mem_img_ctx["wim_version"]

                    if mem_base:
                        payload = merge_report_payloads(mem_base, payload)
                    else:
                        payload = dict(payload)

                    merged_hw_pre = merge_hw_dicts(
                        mem_hw,
                        payload.get("hw") if isinstance(payload.get("hw"), dict) else {},
                    )
                    if merged_hw_pre:
                        payload["hw"] = merged_hw_pre
                    if mem_apps:
                        payload["apps"] = merge_app_statuses(mem_apps, payload.get("apps"))
                    merged_ap_pre = merge_autopilot_status(mem_ap, payload.get("autopilot"), default_source="report")
                    if merged_ap_pre:
                        payload["autopilot"] = merged_ap_pre
                    img_t_pre, wim_v_pre = merge_image_profile(mem_img_ctx, payload)
                    payload["image_type"] = img_t_pre
                    if wim_v_pre != "unknown" or "wim_version" in payload:
                        payload["wim_version"] = wim_v_pre

                chassis_raw = payload.get("chassis")
                chassis = str(chassis_raw).strip() if not is_placeholder_hw_value(chassis_raw) else "Desktop"
                serial_raw = payload.get("serial_number") or (payload.get("hw") or {}).get("serial")
                serial_num = str(serial_raw).strip() if not is_placeholder_hw_value(serial_raw) else "UNKNOWN"
                status = payload.get("status") or "COMPLETED"

                try:
                    dur_sec = int(payload.get("duration_seconds", 0))
                except (ValueError, TypeError):
                    dur_sec = 0

                if dur_sec <= 0 and isinstance(payload.get("benchmark"), dict):
                    try:
                        bm_dur = int(float(payload["benchmark"].get("total_phase_duration_sec", 0) or 0))
                        if bm_dur > 0:
                            dur_sec = bm_dur
                    except (ValueError, TypeError):
                        pass

                apps = payload.get("apps", {})
                if not isinstance(apps, (dict, list)):
                    apps = {}

                errors = payload.get("errors", [])
                if not isinstance(errors, list):
                    errors = [str(errors)] if errors else []

                minutes = dur_sec // 60
                seconds = dur_sec % 60
                raw_dur_fmt = payload.get("duration_formatted")
                dur_fmt = raw_dur_fmt if (raw_dur_fmt and (dur_sec <= 0 or raw_dur_fmt != "0m 00s")) else f"{minutes}m {seconds:02d}s"

                payload["device_id"] = dev_id
                payload["computer_name"] = comp_name
                payload["chassis"] = chassis
                payload["serial_number"] = serial_num
                diagnostic = diagnostics_contract(payload)
                payload.update(diagnostic)
                if diagnostic["diagnostics_status"] == "THERMAL_CRITICAL":
                    status = "NEEDS_ATTENTION"
                payload["status"] = status
                payload["duration_seconds"] = dur_sec
                payload["duration_formatted"] = dur_fmt
                payload["apps"] = apps
                payload["errors"] = errors
                if "timestamp" not in payload:
                    payload["timestamp"] = datetime.now().isoformat()

                saved_merged_report = {}
                try:
                    json_fp, md_fp, json_fn, md_fn = save_device_report(
                        clean_id,
                        payload,
                        self.config.reports_dir,
                        out_merged=saved_merged_report,
                    )
                except Exception as e:
                    if is_enospc_error(e) and self.fleet_store and hasattr(self.fleet_store, "_prune_old_disk_logs"):
                        self.fleet_store._prune_old_disk_logs(time.time(), force=True)
                    log_enospc_rate_limited("Neuspeh arhiviranja izveštaja", e)
                    self._send_json({"error": f"Greška pri arhiviranju: {e}"}, 500)
                    return

                final_report = saved_merged_report if saved_merged_report else payload

                with self.fleet_store.fleet_lock:
                    target_key = clean_id
                    if hasattr(self.fleet_store, "_resolve_device_key_locked"):
                        canon_sn_val = serial_num if not is_placeholder_hw_value(serial_num) else ""
                        resolved_k = self.fleet_store._resolve_device_key_locked(
                            dev_id,
                            canonical_sn=canon_sn_val.upper() if canon_sn_val else "",
                            previous_hostname=prev_host,
                        )
                        if resolved_k:
                            target_key = resolved_k
                        if comp_name and hasattr(self.fleet_store, "_alias_index"):
                            self.fleet_store._alias_index[sanitize_id(comp_name).upper()] = target_key
                    if target_key not in self.fleet_store.fleet_devices:
                        self.fleet_store.fleet_devices[target_key] = {"computer_name": comp_name}
                    existing_rec = self.fleet_store.fleet_devices[target_key]
                    existing_hw = existing_rec.get("hw")
                    merged_hw = merge_hw_dicts(
                        existing_hw if isinstance(existing_hw, dict) else {},
                        final_report.get("hw") if isinstance(final_report.get("hw"), dict) else {},
                    )
                    merged_apps = merge_app_statuses(existing_rec.get("apps"), final_report.get("apps"))
                    merged_ap = merge_autopilot_status(existing_rec.get("autopilot"), final_report.get("autopilot"), default_source="report")
                    merged_img_t, merged_wim_v = merge_image_profile(existing_rec, final_report)

                    if merged_hw:
                        final_report["hw"] = merged_hw
                    if merged_apps:
                        final_report["apps"] = merged_apps
                    if merged_ap:
                        final_report["autopilot"] = merged_ap
                    final_report["image_type"] = merged_img_t
                    if merged_wim_v != "unknown" or "wim_version" in final_report:
                        final_report["wim_version"] = merged_wim_v

                    eff_status = str(final_report.get("status") or status)
                    eff_errors = final_report.get("errors", errors)
                    existing_rec.update({
                        "computer_name": comp_name,
                        "status_label": "Zavrseno" if eff_status.upper() in ("COMPLETED", "SUCCESS", "OK") else ("Upozorenje" if eff_errors else eff_status),
                        "phase": 6,
                        "worker_progress": 100,
                        "duration_seconds": final_report.get("duration_seconds", dur_sec),
                        "duration_formatted": final_report.get("duration_formatted", dur_fmt),
                        "apps": merged_apps,
                        "errors": eff_errors,
                        "serial_number": final_report.get("serial_number", serial_num),
                        "chassis": final_report.get("chassis", chassis),
                        "image_type": merged_img_t,
                        "wim_version": merged_wim_v,
                        "latest_report": final_report,
                        "latest_report_file": json_fp,
                        "latest_report_md_file": md_fp,
                        "completed_at": final_report.get("timestamp"),
                        "_last_report_saved_ts": time.time(),
                        "last_seen": time.time(),
                    })
                    if prev_host:
                        existing_rec["previous_hostname"] = prev_host
                    if merged_hw:
                        existing_rec["hw"] = merged_hw
                    if merged_ap:
                        existing_rec["autopilot"] = merged_ap
                    if final_report.get("install_session_id"):
                        existing_rec["install_session_id"] = str(final_report["install_session_id"]).strip()
                    existing_rec.update(diagnostics_contract(final_report))
                    if hasattr(self.fleet_store, "cleanup_duplicate_devices_locked"):
                        self.fleet_store.cleanup_duplicate_devices_locked(
                            target_key,
                            comp_name=comp_name,
                            serial_num=final_report.get("serial_number", serial_num),
                            client_ip=self.client_address[0],
                            previous_hostname=prev_host,
                        )

                print(f"[*] [REPORT] Zaprimljen završni izveštaj za {comp_name} ({clean_id}) -> {json_fn}, {md_fn}")
                self._send_json({
                    "status": "ok",
                    "message": "Završni izveštaj uspešno primljen i arhiviran",
                    "device_id": dev_id,
                    "json_file": json_fn,
                    "md_file": md_fn
                }, 200)
                return

        if path.startswith("/api/devices/") and path.endswith("/agy-session"):
            parts = path.strip("/").split("/")
            if len(parts) >= 4 and parts[0] == "api" and parts[1] == "devices" and parts[3] == "agy-session":
                url_device_tag = unquote(parts[2]).strip()
                if not isinstance(payload, dict):
                    self._send_json({"error": "Telo zahteva mora biti JSON objekat"}, 400)
                    return

                device_tag = payload.get("device_tag") or url_device_tag
                clean_tag = sanitize_id(device_tag)
                session_id = payload.get("session_id")
                if not session_id:
                    session_id = str(uuid.uuid4())
                    payload["session_id"] = session_id

                try:
                    saved_files = save_agy_session(clean_tag, payload, self.config.reports_dir)
                except Exception as e:
                    print(f"[ERROR] Neuspeh arhiviranja AGY sesije: {e}", file=sys.stderr)
                    self._send_json({"error": f"Greška pri arhiviranju AGY sesije: {e}"}, 500)
                    return

                status_raw = str(payload.get("status", "COMPLETED")).upper()
                is_critical = status_raw in ("CRITICAL_ISSUE", "ERROR", "CRITICAL", "FAILED", "KVAR_POTVRDJEN") or "KVAR" in status_raw
                status_label = "Kvar (AGY)" if is_critical else "Servisiran (AGY)"

                hw = payload.get("hardware") or {}
                serial_num = payload.get("serial_number") or hw.get("serial_number") or ""
                comp_name = payload.get("computer_name") or device_tag
                summary = payload.get("summary") or ""

                with self.fleet_store.fleet_lock:
                    if clean_tag not in self.fleet_store.fleet_devices:
                        self.fleet_store.fleet_devices[clean_tag] = {"computer_name": comp_name}
                    self.fleet_store.fleet_devices[clean_tag].update({
                        "computer_name": comp_name,
                        "device_tag": clean_tag,
                        "serial_number": serial_num,
                        "status_label": status_label,
                        "status": status_label,
                        "agy_status": status_raw,
                        "summary": summary,
                        "has_errors": is_critical,
                        "phase": 5,
                        "worker_progress": 100,
                        "hw": hw,
                        "latest_report": payload,
                        "latest_report_file": saved_files.get("session_file"),
                        "latest_report_md_file": saved_files.get("latest_report_file"),
                        "latest_agy_session": payload,
                        "last_seen": time.time(),
                    })
                    warr = payload.get("warranty")
                    if not warr and isinstance(payload.get("oem"), dict) and "warranty" in payload["oem"]:
                        warr = payload["oem"]["warranty"]
                    if warr and isinstance(warr, dict):
                        self.fleet_store.fleet_devices[clean_tag]["warranty"] = warr

                json_file_path = saved_files.get("session_file", "")
                if self.config.base_dir and json_file_path.startswith(self.config.base_dir):
                    rel_archive = os.path.relpath(json_file_path, self.config.base_dir)
                else:
                    rel_archive = json_file_path

                print(f"[*] [AGY] Arhivirana AGY sesija za {clean_tag} (Session: {str(session_id)[:8]}..., Status: {status_label})")
                self._send_json({
                    "status": "ok",
                    "message": "Servisna sesija uspešno arhivirana na ServisHub.",
                    "device_tag": clean_tag,
                    "session_id": session_id,
                    "json_archive": rel_archive,
                    "dnevnik_updated": bool(saved_files.get("dnevnik_file")),
                }, 200)
                return

        if path.startswith("/api/devices/") and path.endswith("/register"):
            parts = path.strip("/").split("/")
            if len(parts) >= 4 and parts[0] == "api" and parts[1] == "devices" and parts[3] == "register":
                url_device_tag = unquote(parts[2]).strip()
                if not isinstance(payload, dict):
                    self._send_json({"error": "Telo zahteva mora biti JSON objekat"}, 400)
                    return

                device_tag = payload.get("device_tag") or url_device_tag
                clean_tag = sanitize_id(device_tag)

                try:
                    res = register_new_device(clean_tag, payload, self.config.reports_dir)
                except Exception as e:
                    print(f"[ERROR] Neuspeh registracije novog uređaja: {e}", file=sys.stderr)
                    self._send_json({"error": f"Greška pri registraciji uređaja: {e}"}, 500)
                    return

                hw = payload.get("hardware") or {}
                serial_num = payload.get("serial_number") or hw.get("serial_number") or ""
                comp_name = payload.get("computer_name") or device_tag
                status_raw = str(payload.get("status", "U_DIJAGNOSTICI")).upper()
                status_label = "U Dijagnostici (AGY)"

                with self.fleet_store.fleet_lock:
                    if clean_tag not in self.fleet_store.fleet_devices:
                        self.fleet_store.fleet_devices[clean_tag] = {"computer_name": comp_name}
                    self.fleet_store.fleet_devices[clean_tag].update({
                        "computer_name": comp_name,
                        "device_tag": clean_tag,
                        "serial_number": serial_num,
                        "status_label": status_label,
                        "status": status_label,
                        "agy_status": status_raw,
                        "summary": "Računar primljen na servisnu dijagnostiku.",
                        "has_errors": False,
                        "phase": 1,
                        "worker_progress": 10,
                        "hw": hw,
                        "last_seen": time.time(),
                    })

                print(f"[*] [AGY] Registracija novog uređaja: {clean_tag} (SN: {serial_num}, Status: {status_label})")
                self._send_json({
                    "status": "ok",
                    "is_registered": True,
                    "action": res.get("action", "created"),
                    "device_tag": clean_tag,
                    "message": "Uređaj uspešno evidentiran na ServisHub-u.",
                }, 200)
                return

        if path.startswith("/api/devices/") and path.endswith("/checkpoint"):
            parts = path.strip("/").split("/")
            if len(parts) >= 4 and parts[0] == "api" and parts[1] == "devices" and parts[3] == "checkpoint":
                url_device_tag = unquote(parts[2]).strip()
                if not isinstance(payload, dict):
                    self._send_json({"error": "Telo zahteva mora biti JSON objekat"}, 400)
                    return

                device_tag = payload.get("device_tag") or url_device_tag
                clean_tag = sanitize_id(device_tag)

                try:
                    res = save_device_checkpoint(clean_tag, payload, self.config.reports_dir)
                except Exception as e:
                    print(f"[ERROR] Neuspeh čuvanja checkpoint-a: {e}", file=sys.stderr)
                    self._send_json({"error": f"Greška pri čuvanju checkpoint-a: {e}"}, 500)
                    return

                status_raw = str(payload.get("status", "U_DIJAGNOSTICI")).upper()
                if status_raw in ("CRITICAL_ISSUE", "ERROR", "CRITICAL", "FAILED", "KVAR_POTVRDJEN"):
                    status_label = "Kvar (AGY)"
                    has_err = True
                elif status_raw in ("COMPLETED", "ISPRAVAN", "OK", "TESTIRAN_ISPRAVAN"):
                    status_label = "Servisiran (AGY)"
                    has_err = False
                elif status_raw in ("CEKA_DELOVE", "WAITING"):
                    status_label = "Čeka delove (AGY)"
                    has_err = False
                else:
                    status_label = "U Dijagnostici (AGY)"
                    has_err = False

                summary = payload.get("summary") or "Operativni kontrolni nalaz."

                with self.fleet_store.fleet_lock:
                    if clean_tag not in self.fleet_store.fleet_devices:
                        self.fleet_store.fleet_devices[clean_tag] = {"computer_name": clean_tag}
                    self.fleet_store.fleet_devices[clean_tag].update({
                        "device_tag": clean_tag,
                        "status_label": status_label,
                        "status": status_label,
                        "agy_status": status_raw,
                        "summary": summary,
                        "has_errors": has_err,
                        "last_seen": time.time(),
                    })

                print(f"[*] [AGY] Checkpoint zabeležen za {clean_tag} (Status: {status_label}, Nalaz: {summary[:50]}...)")
                self._send_json({
                    "status": "ok",
                    "message": "Checkpoint uspešno zabeležen.",
                    "device_tag": clean_tag,
                    "checkpoint_id": res.get("checkpoint_id"),
                    "status_label": status_label,
                }, 200)
                return

        if path.startswith("/api/session/") and path.endswith("/state"):
            parts = path.strip("/").split("/")
            if len(parts) >= 4 and parts[0] == "api" and parts[1] == "session" and parts[3] == "state":
                session_id = unquote(parts[2]).strip()
                client_ip = self.client_address[0]
                self.fleet_store.record_session_state(session_id, payload, client_ip)
                self._send_json({"ok": True, "status": "ok", "session_id": session_id})
                return

        if path == "/api/heartbeat":
            if not isinstance(payload, dict):
                self._send_json({"error": "Telo zahteva mora biti JSON objekat"}, 400)
                return
            client_ip = self.client_address[0]
            dev_id = self.fleet_store.record_heartbeat(payload, client_ip)
            pending_commands = self.fleet_store.pop_pending_commands(dev_id)
            if not pending_commands and isinstance(payload, dict):
                alt_id = payload.get("device_id") or payload.get("computer_name")
                if alt_id and alt_id != dev_id:
                    pending_commands = self.fleet_store.pop_pending_commands(alt_id)
            self._send_json({
                "status": "ok",
                "device_id": dev_id,
                "timestamp": time.time(),
                "pending_commands": pending_commands,
            })
            return

        if path == "/api/log":
            comp_name = payload.get("computer_name") or "UNKNOWN-PC"
            prev_host = str(payload.get("previous_hostname") or "").strip() or None
            serial_num = (
                payload.get("serial_number")
                or payload.get("serial")
                or payload.get("device_serial")
                or ((payload.get("hw") or {}).get("serial") if isinstance(payload.get("hw"), dict) else None)
            )
            lines = payload.get("lines") or []
            if isinstance(lines, str):
                lines = [lines]
            install_sid = str(payload.get("install_session_id") or "").strip() or None
            appended = self.fleet_store.record_logs(
                comp_name,
                lines,
                serial_number=serial_num,
                install_session_id=install_sid,
                previous_hostname=prev_host,
            )
            self._send_json({"status": "ok", "appended": appended})
            return

        if path == "/api/lifecycle":
            event = payload.get("event")
            client_ip = self.client_address[0]
            if event == "STARTED":
                dev_id = self.fleet_store.record_lifecycle_start(payload, client_ip)
                self._send_json({"status": "ok", "event": "STARTED", "device_id": dev_id})
                return
            elif event == "COMPLETED":
                dev_id = self.fleet_store.record_lifecycle_complete(payload, client_ip)
                is_partial = is_partial_hwtest_payload(payload)
                self._send_json({"status": "ok", "event": "COMPLETED", "device_id": dev_id, "report_saved": not is_partial})
                return
            else:
                self._send_json({"error": f"Nepoznat event: {event}"}, 400)
                return

        if path == "/api/updates/cache-kb":
            if not isinstance(payload, dict):
                self._send_json({"error": "Telo zahteva mora biti JSON objekat"}, 400)
                return
            kb_val = str(payload.get("kb", "")).strip()
            if not kb_val:
                self._send_json({"error": "Nedostaje obavezno polje 'kb'"}, 400)
                return
            title_val = str(payload.get("title", "")).strip()
            os_build_val = str(payload.get("os_build", "")).strip() or None
            from servishub.api.updates import cache_kb_from_ms_catalog, validate_http_storage_directory
            try:
                safe_dir = validate_http_storage_directory(
                    payload.get("directory"),
                    default_root=getattr(self.config, "updates_dir", None),
                    active_config=self.config,
                )
            except ValueError as ve:
                self._send_json({"error": str(ve)}, 400)
                return
            try:
                res = cache_kb_from_ms_catalog(
                    kb=kb_val,
                    title=title_val,
                    directory=safe_dir,
                    scraper=self.ms_catalog_scraper,
                    os_build=os_build_val,
                )
                code = 404 if res.get("status") == "not_found" else (502 if res.get("status") == "error" else 200)
                self._send_json(res, code)
            except ValueError as ve:
                self._send_json({"error": str(ve)}, 400)
            except Exception as ex:
                self._send_json({"error": f"Greška pri keširanju zakrpe {kb_val}: {ex}"}, 500)
            return

        if path == "/api/updates/prune":
            if not self._require_admin_token_if_configured():
                return
            if payload is None:
                payload = {}
            if not isinstance(payload, dict):
                self._send_json({"error": "Telo zahteva mora biti JSON objekat"}, 400)
                return
            from servishub.api.updates import prune_updates_cache, validate_http_storage_directory
            try:
                safe_dir = validate_http_storage_directory(
                    payload.get("directory"),
                    default_root=getattr(self.config, "updates_dir", None),
                    active_config=self.config,
                )
            except ValueError as ve:
                self._send_json({"error": str(ve)}, 400)
                return
            try:
                max_per_build = int(payload.get("max_packages_per_build", 8))
                min_temp_age = int(payload.get("min_temp_age_seconds", 600))
            except (TypeError, ValueError):
                self._send_json({"error": "Nevalidni numerički parametri za prune."}, 400)
                return
            try:
                res = prune_updates_cache(
                    directory=safe_dir,
                    max_packages_per_build=max_per_build,
                    min_temp_age_seconds=min_temp_age,
                )
                self._send_json(res, 200)
            except Exception as ex:
                self._send_json({"error": f"Greška pri održavanju keša zakrpi: {ex}"}, 500)
            return

        self.send_response(404)
        self.end_headers()
