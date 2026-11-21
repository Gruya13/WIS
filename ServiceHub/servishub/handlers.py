"""
servishub.handlers
==================
REST API ruter i HTTP handler za ServisHub.
"""

import os
import sys
import time
import hmac
import json
import uuid
import threading
from datetime import datetime
from concurrent.futures import ThreadPoolExecutor
from urllib.parse import urlparse, parse_qs, unquote
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
                mgr = DriverPoolManager(pool_dir=abs_pool)
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
        auth_header = (self.headers.get("Authorization") or "").strip()
        provided = ""
        if auth_header.lower().startswith("bearer "):
            provided = auth_header[7:].strip()
        if not provided:
            provided = (self.headers.get("X-ServisHub-Token") or "").strip()
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
        self.send_header("Access-Control-Allow-Headers", "Content-Type, X-Payload-Version, X-WIS-Version, X-WIS-SHA256, X-Driver-ID, X-Client-Timestamp, X-Update-Filename, X-Update-KB, X-OS-Build, X-Model-Name, X-Model-Filename, X-ServisHub-Token, X-ServisHub-Client-Token, Authorization")
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

    def _resolve_single_device_hwids(
        self,
        raw_hwids,
        device_name="UnknownDevice",
        manufacturer="",
        model="",
        async_download=False,
        inline_wait_sec=None,
    ):
        clean_hwids = [str(h).strip() for h in (raw_hwids or []) if str(h).strip()]
        results = []
        if not clean_hwids:
            return results

        wait_sec = None
        if async_download:
            wait_sec = 3.0 if inline_wait_sec is None else max(0.0, min(15.0, float(inline_wait_sec)))

        cache_scope = os.path.abspath(str(getattr(self.driver_cache_mgr, "cache_dir", "no-cache")))
        print(f"[*] [DriverResolve] Primljen upit za {len(clean_hwids)} HWID-ja (Uređaj: {device_name} / {manufacturer} {model})...")

        # 1. Cache-first provera svih HWID kandidata uz early-stop na prvom pogotku
        if self.driver_cache_mgr:
            for hwid_str in clean_hwids:
                cached = self.driver_cache_mgr.get_cached(hwid_str)
                if cached:
                    drv_id = cached.get("driver_id", "")
                    print(f"  [+] [CACHE HIT] {hwid_str} -> {cached.get('title', 'Driver')} (ID: {drv_id[:12]}...)")
                    results.append(self._build_ready_driver_entry(hwid_str, cached, source="cache"))
                    return results

        # 1b. Provera da li je za neki od HWID-jeva već u toku dedupovano pozadinsko preuzimanje
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
        self.send_header("Access-Control-Allow-Headers", "Content-Type, X-Payload-Version, X-WIS-Version, X-WIS-SHA256, X-Driver-ID, X-Client-Timestamp, X-Update-Filename, X-Update-KB, X-OS-Build, X-Model-Name, X-Model-Filename, X-ServisHub-Token, X-ServisHub-Client-Token, Authorization")
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
            self._send_json({"status": "ok", "uptime_sec": uptime, "version": VERSION, "release_status": RELEASE_STATUS})
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
            custom_dir = query_params.get("directory", [None])[0]
            if not model_name:
                self._send_json({"error": "Nedostaje obavezni parametar 'model'."}, 400)
                return
            try:
                safe_dir = validate_http_storage_directory(custom_dir, default_root=None, active_config=self.config)
                status_info = get_model_pack_status(model_name, directory=safe_dir, active_config=self.config)
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
            custom_dir = query_params.get("directory", [None])[0]
            if not model_name:
                self._send_json({"error": "Nedostaje obavezni parametar 'model'."}, 400)
                return
            try:
                safe_dir = validate_http_storage_directory(custom_dir, default_root=None, active_config=self.config)
                status_info = get_model_pack_status(model_name, directory=safe_dir, active_config=self.config)
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
                self._send_json({
                    "status": "ok",
                    "device_id": dev_id,
                    "commands": commands,
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
                        if sanitize_id(dev_serial).lower() == clean_serial:
                            found_tag = tag
                            break

                reports_dir = self.config.reports_dir
                if not found_tag and os.path.isdir(reports_dir):
                    for dname in os.listdir(reports_dir):
                        dir_path = os.path.join(reports_dir, dname)
                        if not os.path.isdir(dir_path):
                            continue
                        info_file = os.path.join(dir_path, "DEVICE_INFO.json")
                        if os.path.isfile(info_file):
                            try:
                                with open(info_file, "r", encoding="utf-8") as f:
                                    inf = json.load(f)
                                    s_val = str(inf.get("serial_number") or (inf.get("hardware") or {}).get("serial") or "")
                                    if sanitize_id(s_val).lower() == clean_serial:
                                        found_tag = dname
                                        break
                            except Exception:
                                pass
                        if clean_serial and clean_serial in sanitize_id(dname).lower():
                            found_tag = dname
                            break

                target_tag = found_tag if found_tag else serial
                history = get_device_history(target_tag, self.config.reports_dir)
                if not history.get("serial_number") and found_tag:
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

        if path == "/api/model-profile/usb":
            if not isinstance(payload, dict):
                self._send_json({"error": "Telo zahteva mora biti JSON objekat"}, 400)
                return
            model_name = str(payload.get("model", "")).strip()
            port_count = payload.get("port_count")
            if not model_name or port_count is None:
                self._send_json({"error": "Nedostaje polje 'model' ili 'port_count'"}, 400)
                return
            try:
                port_count_int = int(port_count)
            except (ValueError, TypeError):
                self._send_json({"error": "Polje 'port_count' mora biti ceo broj"}, 400)
                return
            from servishub.model_profiles import update_usb_override
            profile = update_usb_override(
                model_name=model_name,
                port_count=port_count_int,
                by=payload.get("by", "serviser")
            )
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
            try:
                bundle = pool_mgr.build_bundle_tar(
                    model=model_val,
                    missing_hwids=missing_hwids,
                    all_hwids=all_hwids,
                )
            except Exception as ex:
                self._send_json({"error": f"Greška pri sklapanju Driver Pool paketa: {ex}"}, 500)
                return

            if metadata_only:
                out_meta = dict(bundle)
                out_meta.pop("bundle_path", None)
                self._send_json(out_meta, 200)
                return

            if not bundle.get("available") or not bundle.get("bundle_path") or not os.path.isfile(bundle["bundle_path"]):
                oem_entry = pool_mgr.catalog_mgr.find_driver_pack(manufacturer=mfg_val, model=model_val)
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

        if path == "/api/drivers/pool/notify-harvest":
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
            sync_mode = bool(payload.get("sync", False))
            pool_mgr = self._get_driver_pool_mgr()
            if sync_mode:
                try:
                    res = pool_mgr.scan_and_ingest_archives(packs_dir, target_archive=target_archive or None)
                    self._send_json(res, 200)
                except Exception as ex:
                    self._send_json({"error": f"Greška pri skeniranju Harvest arhiva: {ex}"}, 500)
                return
            job = pool_mgr.start_background_job(
                "notify_harvest",
                pool_mgr.scan_and_ingest_archives,
                packs_dir,
                target_archive=target_archive or None,
            )
            self._send_json({"status": "accepted", "job": job, "target_archive": target_archive or None}, 202)
            return

        if path == "/api/drivers/resolve":
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
            serial_number = str(payload.get("serial_number") or payload.get("serial") or "").strip()
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
                    d_hwids = dev.get("hwids", [])
                    if isinstance(d_hwids, str):
                        d_hwids = [d_hwids]
                    elif not isinstance(d_hwids, list):
                        d_hwids = []
                    normalized_devices.append((d_name, d_hwids))

                def _resolve_dev_task(item):
                    d_name, d_hwids = item
                    d_res = self._resolve_single_device_hwids(
                        d_hwids,
                        device_name=d_name,
                        manufacturer=manufacturer,
                        model=model,
                        async_download=async_download,
                        inline_wait_sec=inline_wait_sec,
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
                    async_download=async_download,
                    inline_wait_sec=inline_wait_sec,
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
                if self.fleet_store and hasattr(self.fleet_store, "enrich_report_payload"):
                    payload = self.fleet_store.enrich_report_payload(clean_id, payload, comp_name=comp_name)
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

                try:
                    json_fp, md_fp, json_fn, md_fn = save_device_report(clean_id, payload, self.config.reports_dir)
                except Exception as e:
                    print(f"[ERROR] Neuspeh arhiviranja izveštaja: {e}", file=sys.stderr)
                    self._send_json({"error": f"Greška pri arhiviranju: {e}"}, 500)
                    return

                with self.fleet_store.fleet_lock:
                    if clean_id not in self.fleet_store.fleet_devices:
                        self.fleet_store.fleet_devices[clean_id] = {"computer_name": comp_name}
                    existing_hw = self.fleet_store.fleet_devices[clean_id].get("hw")
                    merged_hw = merge_hw_dicts(
                        existing_hw if isinstance(existing_hw, dict) else {},
                        payload.get("hw") if isinstance(payload.get("hw"), dict) else {},
                    )
                    self.fleet_store.fleet_devices[clean_id].update({
                        "computer_name": comp_name,
                        "status_label": "Zavrseno" if status.upper() in ("COMPLETED", "SUCCESS", "OK") else ("Upozorenje" if errors else status),
                        "phase": 6,
                        "worker_progress": 100,
                        "duration_seconds": dur_sec,
                        "duration_formatted": dur_fmt,
                        "apps": apps,
                        "errors": errors,
                        "serial_number": serial_num,
                        "chassis": chassis,
                        "latest_report": payload,
                        "latest_report_file": json_fp,
                        "latest_report_md_file": md_fp,
                        "last_seen": time.time(),
                    })
                    if merged_hw:
                        self.fleet_store.fleet_devices[clean_id]["hw"] = merged_hw
                    self.fleet_store.fleet_devices[clean_id].update(diagnostics_contract(payload))
                    if hasattr(self.fleet_store, "cleanup_duplicate_devices_locked"):
                        self.fleet_store.cleanup_duplicate_devices_locked(clean_id, comp_name=comp_name, serial_num=serial_num)

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
                is_critical = status_raw in ("CRITICAL_ISSUE", "ERROR", "CRITICAL", "FAILED")
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
            lines = payload.get("lines") or []
            if isinstance(lines, str):
                lines = [lines]
            appended = self.fleet_store.record_logs(comp_name, lines)
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
                self._send_json({"status": "ok", "event": "COMPLETED", "device_id": dev_id, "report_saved": True})
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
