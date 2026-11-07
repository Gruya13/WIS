#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
GWADT USB Tool - Glavni GUI Server i Kontrolni Panel
===================================================
Samostalni Python 3 servis (bez eksternih zavisnosti) za održavanje,
dijagnostiku, verifikaciju i 1-klik nadogradnju USB fleš instalacionog medijuma.
Uključuje CSRF/Session autentifikaciju, Origin/Host validaciju, strogu server-side
proveru ciljnog USB diska, per-target kontrolu konkurentnosti i audit logovanje.
"""

import os
import re
import sys
import json
import time
import socket
import secrets
import webbrowser
import threading
import argparse
from pathlib import Path
from http.cookies import SimpleCookie
from http.server import ThreadingHTTPServer, BaseHTTPRequestHandler
from urllib.parse import urlparse, parse_qs

# Dodavanje foldera alata u sys.path radi uvoza lokalnih modula
TOOL_DIR = Path(__file__).resolve().parent
# Podrška za rad unutar Docker kontejnera gde je repozitorijum mapiran preko GWADT_PROJECT_ROOT
PROJECT_ROOT = Path(os.environ.get("GWADT_PROJECT_ROOT", str(TOOL_DIR.parent))).resolve()
sys.path.insert(0, str(TOOL_DIR))

from core.usb_detector import (
    get_all_usb_drives,
    eject_usb_drive,
    clear_dirty_bit,
    resolve_and_validate_usb_target,
    acquire_target_lock,
    attach_worker_thread_to_lock,
    release_target_lock,
    is_target_locked,
    _ACTIVE_TARGET_LOCKS,
    _has_symlink_in_path,
    _is_forbidden_path,
)
from core.system_inspector import inspect_gwadt_system
from core.patch_engine import (
    get_staging_metadata,
    get_local_patches_list,
    fetch_latest_online_build,
    compare_versions,
)
from core.usb_sync import (
    GLOBAL_UPGRADE_WORKER,
    GLOBAL_PROGRAM_REFRESH_WORKER,
    GLOBAL_ONLINE_BUILD_WORKER,
    sync_scripts_only,
)
from core.iso_finder import scan_local_iso_images, get_microsoft_official_sources
from core.iso_builder import GLOBAL_ISO_BUILD_MANAGER

DEFAULT_PORT = int(os.environ.get("GWADT_PORT", 8092))
GUI_DIR = TOOL_DIR / "gui"

# ==============================================================================
# 1. BEZBEDNOSNA KONFIGURACIJA, SESIJSKI TOKENI I AUDIT DNEVNIK
# ==============================================================================

SESSION_COOKIE_NAME = "wis_usb_session"
CSRF_HEADER_NAME = "X-USB-Tool-Token"
MAX_POST_BODY_BYTES = 64 * 1024  # 64 KB limit za JSON kontrolne zahteve

# Generisanje kriptografski jakog sesijskog/API tokena pri pokretanju servera
_SERVER_SESSION_TOKEN = (
    os.environ.get("USB_TOOL_API_TOKEN", "").strip() or secrets.token_urlsafe(32)
)

SERVER_CONFIG = {
    "host": "127.0.0.1",
    "port": DEFAULT_PORT,
    "allow_lan": False,
    "dev_mode": os.environ.get("USB_TOOL_DEV_MODE", "").strip().lower() in ("1", "true", "yes"),
}

_AUDIT_LOG_LOCK = threading.Lock()
_AUDIT_LOG_HISTORY = []


def get_active_api_token():
    """Vraća trenutno aktivni serverski API/CSRF token (nikada se ne upisuje u logove)."""
    env_tok = os.environ.get("USB_TOOL_API_TOKEN", "").strip()
    return env_tok if env_tok else _SERVER_SESSION_TOKEN


def _sanitize_for_audit(value):
    """Uklanja potencijalne tajne, tokene i kontrolne znakove pre upisa u audit log."""
    text = str(value if value is not None else "-").replace("\r", " ").replace("\n", " ").strip()
    active_tok = get_active_api_token()
    if active_tok and len(active_tok) >= 8 and active_tok in text:
        text = text.replace(active_tok, "[REDACTED]")
    if _SERVER_SESSION_TOKEN and len(_SERVER_SESSION_TOKEN) >= 8 and _SERVER_SESSION_TOKEN in text:
        text = text.replace(_SERVER_SESSION_TOKEN, "[REDACTED]")
    text = re.sub(r"(?i)(wis_usb_session|x-usb-tool-token|bearer)\s*[=:]\s*\S+", r"\1=[REDACTED]", text)
    return text[:500]


def audit_log(action, target, client_ip, outcome, detail=""):
    """
    Beleži svaku mutirajuću ili odbijenu bezbednosnu radnju u strukturirani audit log
    bez otkrivanja sesijskih tokena ili drugih tajni.
    """
    ts = time.strftime("%Y-%m-%d %H:%M:%S")
    safe_action = _sanitize_for_audit(action)
    safe_target = _sanitize_for_audit(target)
    safe_ip = _sanitize_for_audit(client_ip)
    safe_outcome = _sanitize_for_audit(outcome)
    safe_detail = _sanitize_for_audit(detail)

    entry = {
        "timestamp": ts,
        "action": safe_action,
        "target": safe_target,
        "client_ip": safe_ip,
        "outcome": safe_outcome,
        "detail": safe_detail,
    }
    line = (
        f"[AUDIT] {ts} | action={safe_action} | target={safe_target} | "
        f"client_ip={safe_ip} | outcome={safe_outcome} | detail={safe_detail}\n"
    )

    with _AUDIT_LOG_LOCK:
        _AUDIT_LOG_HISTORY.append(entry)
        if len(_AUDIT_LOG_HISTORY) > 1000:
            _AUDIT_LOG_HISTORY.pop(0)

        sys.stderr.write(line)
        log_paths = []
        env_log = os.environ.get("USB_TOOL_AUDIT_LOG", "").strip()
        if env_log:
            log_paths.append(Path(env_log))
        log_paths.append(TOOL_DIR / "usb_tool_audit.log")
        log_paths.append(Path("/tmp/usb_tool_audit.log"))

        for lp in log_paths:
            try:
                with open(lp, "a", encoding="utf-8") as f:
                    f.write(line)
                break
            except Exception:
                continue

    return entry


def get_audit_log_history():
    """Vraća kopiju audit zapisa iz memorije."""
    with _AUDIT_LOG_LOCK:
        return list(_AUDIT_LOG_HISTORY)


def _watch_async_worker_and_release_lock(lock_key, worker_obj, action_name, target_desc, client_ip):
    """
    Prati pozadinsku nit asinhronog radnika (npr. GLOBAL_UPGRADE_WORKER) i oslobađa
    per-target lock tek kada se pozadinska operacija zaista završi.
    """
    w_thread = getattr(worker_obj, "thread", None)
    if w_thread is not None:
        attach_worker_thread_to_lock(lock_key, w_thread)

    def _watcher():
        try:
            if w_thread is not None:
                w_thread.join()
            else:
                while True:
                    prog = worker_obj.get_progress() if hasattr(worker_obj, "get_progress") else {}
                    if prog.get("status") not in ("running", "building"):
                        break
                    time.sleep(0.2)
            prog_final = worker_obj.get_progress() if hasattr(worker_obj, "get_progress") else {}
            final_status = prog_final.get("status", "completed").upper()
            final_msg = prog_final.get("message") or prog_final.get("step") or "Završeno"
            audit_log(action_name, target_desc, client_ip, f"ASYNC_{final_status}", final_msg)
        finally:
            release_target_lock(lock_key)

    t = threading.Thread(target=_watcher, daemon=True)
    t.start()


# ==============================================================================
# 2. HTTP REQUEST HANDLER SA CSRF, ORIGIN I DISK VALIDACIJOM
# ==============================================================================

class UsbToolRequestHandler(BaseHTTPRequestHandler):
    """Obrada HTTP zahteva za statičke GUI datoteke i zaštićene REST API rute."""

    def log_message(self, format, *args):
        # Ignorišemo podrazumevani bučni HTTP log u konzoli, beležimo samo greške
        if args and str(args[1])[0] in ["4", "5"]:
            sys.stderr.write(f"[HTTP {args[1]}] {args[0]}\n")

    def _get_client_ip(self):
        if self.client_address and len(self.client_address) > 0:
            return str(self.client_address[0])
        return "unknown"

    def _get_allowed_hosts(self):
        """Skup dozvoljenih hostname vrednosti za Host, Origin i Referer validaciju."""
        allowed = {"127.0.0.1", "localhost", "::1", "[::1]"}
        cfg_host = (SERVER_CONFIG.get("host") or "127.0.0.1").strip().lower()
        if cfg_host and cfg_host not in ("0.0.0.0", ""):
            allowed.add(cfg_host)
        if SERVER_CONFIG.get("allow_lan") or cfg_host == "0.0.0.0":
            for ip in get_local_ip_addresses():
                allowed.add(ip.lower())
        if hasattr(self, "server") and hasattr(self.server, "server_address"):
            srv_host = str(self.server.server_address[0] or "").strip().lower()
            if srv_host and srv_host not in ("0.0.0.0", ""):
                allowed.add(srv_host)
        return allowed

    @staticmethod
    def _extract_hostname_from_host_header(host_header):
        """Izvlači čist hostname iz Host zaglavlja (npr. '127.0.0.1:8092' -> '127.0.0.1')."""
        raw = (host_header or "").strip().lower()
        if not raw:
            return ""
        if raw.startswith("["):
            end_bracket = raw.find("]")
            if end_bracket != -1:
                return raw[1:end_bracket]
        if ":" in raw:
            return raw.split(":", 1)[0].strip()
        return raw

    def _validate_host_and_origin(self):
        """
        Proverava Host, Origin i Referer zaglavlja radi sprečavanja DNS rebinding-a
        i Cross-Site Request Forgery (CSRF) napada sa drugih web lokacija.
        Vraća (True, "") ili (False, razlog).
        """
        allowed_hosts = self._get_allowed_hosts()

        host_hdr = (self.headers.get("Host") or "").strip()
        host_name = self._extract_hostname_from_host_header(host_hdr)
        if not host_name or host_name not in allowed_hosts:
            return False, f"Nedozvoljeno Host zaglavlje: '{host_hdr or 'prazno'}'."

        origin_hdr = (self.headers.get("Origin") or "").strip()
        if origin_hdr:
            if origin_hdr.lower() == "null":
                return False, "Nedozvoljen Origin ('null') - zahtev sa eksterne ili sandbox lokacije je odbijen."
            try:
                parsed_origin = urlparse(origin_hdr)
                origin_host = (parsed_origin.hostname or "").strip().lower()
            except Exception:
                origin_host = ""
            if not origin_host or origin_host not in allowed_hosts:
                return False, f"Nedozvoljen Origin zahteva: '{origin_hdr}'."

        referer_hdr = (self.headers.get("Referer") or "").strip()
        if referer_hdr:
            try:
                parsed_ref = urlparse(referer_hdr)
                ref_host = (parsed_ref.hostname or "").strip().lower()
            except Exception:
                ref_host = ""
            if not ref_host or ref_host not in allowed_hosts:
                return False, f"Nedozvoljen Referer zahteva: '{referer_hdr}'."

        return True, ""

    def _extract_session_cookie_token(self):
        """Čita vrednost wis_usb_session kolačića iz Cookie zaglavlja."""
        raw_cookie = self.headers.get("Cookie")
        if not raw_cookie:
            return ""
        try:
            cookie = SimpleCookie()
            cookie.load(raw_cookie)
            if SESSION_COOKIE_NAME in cookie:
                return (cookie[SESSION_COOKIE_NAME].value or "").strip()
        except Exception:
            pass
        # Fallback parsiranje ako SimpleCookie naiđe na nestandardni format
        for part in raw_cookie.split(";"):
            part = part.strip()
            if part.startswith(f"{SESSION_COOKIE_NAME}="):
                return part.split("=", 1)[1].strip()
        return ""

    def _validate_auth_and_csrf(self):
        """
        Proverava prisustvo i kriptografsku ispravnost X-USB-Tool-Token zaglavlja
        i sesijskog kolačića (wis_usb_session).
        Vraća (True, 200, "") ili (False, http_status_code, razlog).
        """
        expected_token = get_active_api_token()
        header_token = (self.headers.get(CSRF_HEADER_NAME) or "").strip()
        cookie_token = self._extract_session_cookie_token()

        if not header_token and not cookie_token:
            return (
                False,
                401,
                "Neautorizovan zahtev: nedostaje X-USB-Tool-Token zaglavlje i aktivna sesija.",
            )

        if not header_token:
            return (
                False,
                403,
                "Odbijeno (CSRF zaštita): nedostaje obavezno X-USB-Tool-Token zaglavlje.",
            )

        if not secrets.compare_digest(header_token, expected_token):
            return (
                False,
                403,
                "Odbijeno: Nevažeći X-USB-Tool-Token.",
            )

        if cookie_token and not secrets.compare_digest(cookie_token, expected_token):
            return (
                False,
                403,
                "Odbijeno: Nevažeći sesijski kolačić (wis_usb_session).",
            )

        return True, 200, ""

    def _send_json(self, data, status_code=200):
        body = json.dumps(data, ensure_ascii=False, indent=2).encode("utf-8")
        self.send_response(status_code)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(body)))
        self.send_header("Cache-Control", "no-cache, no-store, must-revalidate")
        self.send_header("X-Content-Type-Options", "nosniff")
        self.send_header("X-Frame-Options", "DENY")
        self.end_headers()
        self.wfile.write(body)

    def _send_index_html(self, send_body=True):
        file_path = GUI_DIR / "index.html"
        if not os.path.isfile(file_path):
            self.send_error(404, "Datoteka nije pronađena")
            return
        token = get_active_api_token()
        with open(file_path, "r", encoding="utf-8") as f:
            html_text = f.read()

        if "__USB_TOOL_CSRF_TOKEN__" in html_text:
            html_text = html_text.replace("__USB_TOOL_CSRF_TOKEN__", token)
        elif 'name="csrf-token"' in html_text:
            html_text = re.sub(
                r'(<meta\s+name="csrf-token"\s+content=")[^"]*(")',
                rf"\g<1>{token}\g<2>",
                html_text,
            )
        else:
            html_text = html_text.replace(
                "</head>",
                f'  <meta name="csrf-token" content="{token}">\n</head>',
                1,
            )

        body = html_text.encode("utf-8")
        self.send_response(200)
        self.send_header("Content-Type", "text/html; charset=utf-8")
        self.send_header("Content-Length", str(len(body)))
        self.send_header("Cache-Control", "no-cache, no-store, must-revalidate")
        self.send_header("X-Content-Type-Options", "nosniff")
        self.send_header("X-Frame-Options", "DENY")
        self.send_header(
            "Set-Cookie",
            f"{SESSION_COOKIE_NAME}={token}; HttpOnly; SameSite=Strict; Path=/",
        )
        self.end_headers()
        if send_body:
            self.wfile.write(body)

    def _send_file(self, file_path, content_type, send_body=True):
        if not os.path.isfile(file_path):
            self.send_error(404, "Datoteka nije pronađena")
            return
        size = os.path.getsize(file_path)
        self.send_response(200)
        self.send_header("Content-Type", content_type)
        self.send_header("Content-Length", str(size))
        self.send_header("Cache-Control", "no-cache")
        self.send_header("X-Content-Type-Options", "nosniff")
        self.send_header("X-Frame-Options", "DENY")
        self.end_headers()
        if send_body:
            with open(file_path, "rb") as f:
                self.wfile.write(f.read())

    def do_HEAD(self):
        parsed = urlparse(self.path)
        path = parsed.path
        if path in ["/", "/index.html"]:
            self._send_index_html(send_body=False)
        elif path == "/gui/style.css":
            self._send_file(GUI_DIR / "style.css", "text/css; charset=utf-8", send_body=False)
        elif path == "/gui/app.js":
            self._send_file(GUI_DIR / "app.js", "application/javascript; charset=utf-8", send_body=False)
        else:
            self.send_response(200)
            self.send_header("Content-Type", "application/json")
            self.end_headers()

    def do_GET(self):
        parsed = urlparse(self.path)
        path = parsed.path
        qs = parse_qs(parsed.query)

        # 0. Validacija Host i Origin zaglavlja (zaštita od DNS rebinding-a)
        ok_origin, origin_err = self._validate_host_and_origin()
        if not ok_origin:
            client_ip = self._get_client_ip()
            audit_log(path, "-", client_ip, "DENIED_ORIGIN_OR_HOST", origin_err)
            self._send_json({
                "success": False,
                "error": "FORBIDDEN_ORIGIN",
                "message": origin_err,
            }, 403)
            return

        # 1. Statički fajlovi (sa ubacivanjem sesije i CSRF tokena za index.html)
        if path in ["/", "/index.html"]:
            self._send_index_html(send_body=True)
            return
        elif path == "/gui/style.css":
            self._send_file(GUI_DIR / "style.css", "text/css; charset=utf-8")
            return
        elif path == "/gui/app.js":
            self._send_file(GUI_DIR / "app.js", "application/javascript; charset=utf-8")
            return

        # 2. REST API: GET /api/status
        if path == "/api/status":
            req_dev = "dev_mode" in qs and qs["dev_mode"][0] in ["1", "true", "yes"]
            allow_dev = bool(req_dev and SERVER_CONFIG.get("dev_mode", False))
            drives = get_all_usb_drives(include_project_repo=allow_dev)

            active_drive = None
            active_inspection = None

            if drives:
                usb_candidates = [d for d in drives if not d.get("is_dev_repo")]
                candidates_pool = usb_candidates if usb_candidates else drives

                def _rank_drive(d):
                    score = 0
                    mp = d.get("mountpoint")
                    if mp and os.path.exists(mp):
                        score += 100
                        p_mp = Path(mp)
                        if (p_mp / "sources" / "install.wim").exists() or (p_mp / "sources" / "install.esd").exists():
                            score += 500
                        if (p_mp / "autounattend.xml").exists():
                            score += 200
                    size_gb = (d.get("size_bytes") or 0) / (1024**3)
                    score += min(int(size_gb * 10), 150)
                    return score

                candidates_pool.sort(key=_rank_drive, reverse=True)
                active_drive = candidates_pool[0]

                if active_drive.get("mountpoint"):
                    active_inspection = inspect_gwadt_system(active_drive["mountpoint"])

            staging_meta = get_staging_metadata()
            online_meta = fetch_latest_online_build(force_refresh=False)

            wim_info = active_inspection.get("wim") if active_inspection else {}
            comp = compare_versions(wim_info, staging_meta, online_meta)

            self._send_json({
                "timestamp": time.strftime("%Y-%m-%d %H:%M:%S"),
                "dev_mode_enabled": bool(SERVER_CONFIG.get("dev_mode", False)),
                "drives": drives,
                "active_drive": active_drive,
                "inspection": active_inspection,
                "staging": staging_meta,
                "online": online_meta,
                "comparison": comp,
            })
            return

        # 3. REST API: GET /api/upgrade-progress
        if path == "/api/upgrade-progress":
            self._send_json(GLOBAL_UPGRADE_WORKER.get_progress())
            return

        # 4. REST API: GET /api/patches
        if path == "/api/patches":
            patches = get_local_patches_list(PROJECT_ROOT)
            self._send_json({
                "patches": patches,
                "count": len(patches),
            })
            return

        # 5. REST API: GET /api/usb-log
        if path == "/api/usb-log":
            mountpoint = qs.get("mountpoint", [""])[0]
            log_content = ""
            log_found = False

            candidates = []
            if mountpoint:
                ok_v, resolved_v = resolve_and_validate_usb_target(
                    mountpoint,
                    operation="read_log",
                    allow_dev_repo=bool(SERVER_CONFIG.get("dev_mode", False)),
                )
                if ok_v and resolved_v.get("canonical_mountpoint"):
                    candidates.append(Path(resolved_v["canonical_mountpoint"]) / "servis_deploy.log")
            candidates.append(PROJECT_ROOT / "servis_deploy.log")
            candidates.append(PROJECT_ROOT / "Logs" / "servis_deploy.log")

            for c in candidates:
                if c.exists() and c.is_file() and not c.is_symlink():
                    try:
                        with open(c, "r", encoding="utf-8", errors="ignore") as f:
                            lines = f.readlines()
                            log_content = "".join(lines[-200:])
                            log_found = True
                            break
                    except Exception as e:
                        log_content = f"Greška pri čitanju fajla {c.name}: {e}"
                        break

            self._send_json({
                "success": log_found,
                "log": log_content,
                "message": "Log je uspešno učitan." if log_found else "Servisni log nije pronađen.",
            })
            return

        # 6. REST API: GET /api/iso-sources
        if path == "/api/iso-sources":
            local_isos = scan_local_iso_images()
            official = get_microsoft_official_sources()
            self._send_json({
                "local_sources": local_isos,
                "local_images": local_isos,
                "official_sources": official,
                "count": len(local_isos),
            })
            return

        # 7. REST API: GET /api/prepare-progress
        if path == "/api/prepare-progress":
            self._send_json(GLOBAL_PROGRAM_REFRESH_WORKER.get_progress())
            return

        # 8. REST API: GET /api/online-build-progress
        if path == "/api/online-build-progress":
            self._send_json(GLOBAL_ONLINE_BUILD_WORKER.get_progress())
            return

        # 9. REST API: GET /api/build-iso/status
        if path == "/api/build-iso/status":
            self._send_json(GLOBAL_ISO_BUILD_MANAGER.get_status())
            return

        self.send_error(404, "Nepoznata ruta")

    def do_POST(self):
        parsed = urlparse(self.path)
        path = parsed.path
        client_ip = self._get_client_ip()

        # 1. Validacija Host, Origin i Referer zaglavlja (zaštita od CSRF i DNS rebinding-a)
        ok_origin, origin_err = self._validate_host_and_origin()
        if not ok_origin:
            audit_log(path, "-", client_ip, "DENIED_ORIGIN_OR_HOST", origin_err)
            self._send_json({
                "success": False,
                "error": "FORBIDDEN_ORIGIN",
                "message": origin_err,
            }, 403)
            return

        # 2. Validacija autentifikacije sesije i X-USB-Tool-Token zaglavlja
        ok_auth, auth_code, auth_err = self._validate_auth_and_csrf()
        if not ok_auth:
            audit_log(path, "-", client_ip, "DENIED_UNAUTHORIZED", auth_err)
            self._send_json({
                "success": False,
                "error": "UNAUTHORIZED",
                "message": auth_err,
            }, auth_code)
            return

        # 3. Čitanje JSON tela uz ograničenje veličine
        content_len = int(self.headers.get("Content-Length", 0))
        if content_len > MAX_POST_BODY_BYTES:
            audit_log(path, "-", client_ip, "DENIED_PAYLOAD_TOO_LARGE", f"Content-Length={content_len}")
            self._send_json({
                "success": False,
                "message": "Zahtev je prevelik.",
            }, 413)
            return

        post_data = {}
        if content_len > 0:
            try:
                body_bytes = self.rfile.read(content_len)
                post_data = json.loads(body_bytes.decode("utf-8"))
                if not isinstance(post_data, dict):
                    post_data = {}
            except Exception:
                post_data = {}

        # Pomoćna funkcija za izvlačenje cilja i očekivanog identiteta iz post_data
        def _extract_target_and_identity(data_dict):
            t_spec = (
                data_dict.get("device_id")
                or data_dict.get("mountpoint")
                or data_dict.get("device")
                or ""
            )
            exp_id = {
                k: data_dict.get(k)
                for k in ("serial", "uuid", "model", "device", "mountpoint")
                if data_dict.get(k)
            }
            return str(t_spec).strip(), exp_id

        # ----------------------------------------------------------------------
        # RUTA 1: POST /api/check-online
        # ----------------------------------------------------------------------
        if path == "/api/check-online":
            online_meta = fetch_latest_online_build(force_refresh=True)
            audit_log("check-online", "microsoft_catalog", client_ip, "SUCCESS", online_meta.get("title", "OK"))
            self._send_json({
                "success": True,
                "online": online_meta,
                "message": f"Online provera završena: {online_meta.get('title', 'Ažurirano')}",
            })
            return

        # ----------------------------------------------------------------------
        # RUTA 2: POST /api/upgrade-usb
        # ----------------------------------------------------------------------
        if path == "/api/upgrade-usb":
            target_spec, exp_id = _extract_target_and_identity(post_data)
            force = bool(post_data.get("force", False))
            if not target_spec:
                audit_log("upgrade-usb", "-", client_ip, "REJECTED_MISSING_TARGET", "Nedostaje mountpoint/device_id")
                self._send_json({"success": False, "message": "Nedostaje parametar mountpoint ili device_id!"}, 400)
                return

            # Upgrade menja/briše WIM sliku, stoga je strogo zabranjen na --dev repozitorijumu
            ok_val, resolved = resolve_and_validate_usb_target(
                target_spec,
                operation="upgrade",
                allow_dev_repo=False,
                expected_identity=exp_id,
            )
            if not ok_val:
                audit_log("upgrade-usb", target_spec, client_ip, "REJECTED_INVALID_TARGET", resolved)
                self._send_json({"success": False, "message": resolved}, 403)
                return

            lock_key = resolved["lock_key"]
            canonical_mp = resolved["canonical_mountpoint"]

            locked, lock_info = acquire_target_lock(lock_key, operation="upgrade-usb")
            if not locked:
                busy_msg = (
                    f"Uređaj ({resolved.get('device')}) je trenutno zauzet drugom operacijom "
                    f"({lock_info.get('operation', 'u toku')}). Sačekajte završetak."
                )
                audit_log("upgrade-usb", canonical_mp, client_ip, "CONFLICT_BUSY", busy_msg)
                self._send_json({"success": False, "error": "TARGET_BUSY", "message": busy_msg}, 409)
                return

            ok, msg = GLOBAL_UPGRADE_WORKER.start_upgrade(canonical_mp, force=force)
            if not ok:
                release_target_lock(lock_key)
                audit_log("upgrade-usb", canonical_mp, client_ip, "CONFLICT_BUSY", msg)
                self._send_json({"success": False, "error": "TARGET_BUSY", "message": msg}, 409)
                return

            _watch_async_worker_and_release_lock(
                lock_key, GLOBAL_UPGRADE_WORKER, "upgrade-usb", canonical_mp, client_ip
            )
            audit_log("upgrade-usb", canonical_mp, client_ip, "STARTED", msg)
            self._send_json({
                "success": True,
                "message": msg,
            })
            return

        # ----------------------------------------------------------------------
        # RUTA 3: POST /api/sync-scripts
        # ----------------------------------------------------------------------
        if path == "/api/sync-scripts":
            target_spec, exp_id = _extract_target_and_identity(post_data)
            if not target_spec:
                audit_log("sync-scripts", "-", client_ip, "REJECTED_MISSING_TARGET", "Nedostaje mountpoint/device_id")
                self._send_json({"success": False, "message": "Nedostaje parametar mountpoint ili device_id!"}, 400)
                return

            allow_dev = bool(SERVER_CONFIG.get("dev_mode", False))
            ok_val, resolved = resolve_and_validate_usb_target(
                target_spec,
                operation="sync",
                allow_dev_repo=allow_dev,
                expected_identity=exp_id,
            )
            if not ok_val:
                audit_log("sync-scripts", target_spec, client_ip, "REJECTED_INVALID_TARGET", resolved)
                self._send_json({"success": False, "message": resolved}, 403)
                return

            lock_key = resolved["lock_key"]
            canonical_mp = resolved["canonical_mountpoint"]

            locked, lock_info = acquire_target_lock(lock_key, operation="sync-scripts")
            if not locked:
                busy_msg = (
                    f"Uređaj ({resolved.get('device')}) je trenutno zauzet drugom operacijom "
                    f"({lock_info.get('operation', 'u toku')})."
                )
                audit_log("sync-scripts", canonical_mp, client_ip, "CONFLICT_BUSY", busy_msg)
                self._send_json({"success": False, "error": "TARGET_BUSY", "message": busy_msg}, 409)
                return

            try:
                ok, msg = sync_scripts_only(canonical_mp)
            finally:
                release_target_lock(lock_key)

            audit_log("sync-scripts", canonical_mp, client_ip, "SUCCESS" if ok else "ERROR", msg)
            self._send_json({
                "success": ok,
                "message": msg,
            }, 200 if ok else 500)
            return

        # ----------------------------------------------------------------------
        # RUTA 4: POST /api/eject-usb
        # ----------------------------------------------------------------------
        if path == "/api/eject-usb":
            target_spec, exp_id = _extract_target_and_identity(post_data)
            if not target_spec:
                audit_log("eject-usb", "-", client_ip, "REJECTED_MISSING_TARGET", "Nedostaje device/device_id")
                self._send_json({"success": False, "message": "Nedostaje parametar device ili device_id!"}, 400)
                return

            # Eject je STROGO ZABRANJEN nad lokalnim razvojnim repozitorijumom (allow_dev_repo=False)
            ok_val, resolved = resolve_and_validate_usb_target(
                target_spec,
                operation="eject",
                allow_dev_repo=False,
                expected_identity=exp_id,
            )
            if not ok_val:
                audit_log("eject-usb", target_spec, client_ip, "REJECTED_INVALID_TARGET", resolved)
                self._send_json({"success": False, "message": resolved}, 403)
                return

            lock_key = resolved["lock_key"]
            canonical_dev = resolved["canonical_device"]

            locked, lock_info = acquire_target_lock(lock_key, operation="eject-usb")
            if not locked:
                busy_msg = (
                    f"Uređaj ({canonical_dev}) je trenutno zauzet aktivnom operacijom "
                    f"({lock_info.get('operation', 'u toku')}) i ne može biti demontiran."
                )
                audit_log("eject-usb", canonical_dev, client_ip, "CONFLICT_BUSY", busy_msg)
                self._send_json({"success": False, "error": "TARGET_BUSY", "message": busy_msg}, 409)
                return

            try:
                ok, msg = eject_usb_drive(resolved.get("canonical_mountpoint") or canonical_dev)
            finally:
                release_target_lock(lock_key)

            audit_log("eject-usb", canonical_dev, client_ip, "SUCCESS" if ok else "ERROR", msg)
            self._send_json({
                "success": ok,
                "message": msg,
            })
            return

        # ----------------------------------------------------------------------
        # RUTA 4b: POST /api/clear-dirty
        # ----------------------------------------------------------------------
        if path == "/api/clear-dirty":
            target_spec, exp_id = _extract_target_and_identity(post_data)
            if not target_spec:
                audit_log("clear-dirty", "-", client_ip, "REJECTED_MISSING_TARGET", "Nedostaje device/device_id")
                self._send_json({
                    "success": False,
                    "message": "Nedostaje identifikator ciljnog USB uređaja (device_id ili device)!",
                }, 400)
                return

            # Clear-dirty (ntfsfix/chkdsk/UDisks2 Repair) je STROGO ZABRANJEN za --dev repozitorijum
            ok_val, resolved = resolve_and_validate_usb_target(
                target_spec,
                operation="clear_dirty",
                allow_dev_repo=False,
                expected_identity=exp_id,
            )
            if not ok_val:
                audit_log("clear-dirty", target_spec, client_ip, "REJECTED_INVALID_TARGET", resolved)
                self._send_json({"success": False, "message": resolved}, 403)
                return

            lock_key = resolved["lock_key"]
            canonical_dev = resolved["canonical_device"]

            locked, lock_info = acquire_target_lock(lock_key, operation="clear-dirty")
            if not locked:
                busy_msg = (
                    f"Uređaj ({canonical_dev}) je trenutno zauzet aktivnom operacijom "
                    f"({lock_info.get('operation', 'u toku')})."
                )
                audit_log("clear-dirty", canonical_dev, client_ip, "CONFLICT_BUSY", busy_msg)
                self._send_json({"success": False, "error": "TARGET_BUSY", "message": busy_msg}, 409)
                return

            try:
                ok, msg = clear_dirty_bit(canonical_dev)
            finally:
                release_target_lock(lock_key)

            audit_log("clear-dirty", canonical_dev, client_ip, "SUCCESS" if ok else "ERROR", msg)
            self._send_json({
                "success": ok,
                "message": msg,
            })
            return

        # ----------------------------------------------------------------------
        # RUTA 5: POST /api/prepare-program
        # ----------------------------------------------------------------------
        if path == "/api/prepare-program":
            source = str(post_data.get("source") or "").strip()
            if not source:
                audit_log("prepare-program", "-", client_ip, "REJECTED_MISSING_SOURCE", "Nedostaje source")
                self._send_json({
                    "success": False,
                    "message": "Nedostaje parametar source (putanja do ISO/ESD fajla)!",
                }, 400)
                return

            if not source.lower().startswith(("http://", "https://")):
                has_sym, sym_msg = _has_symlink_in_path(source)
                if has_sym:
                    audit_log("prepare-program", source, client_ip, "REJECTED_SYMLINK", sym_msg)
                    self._send_json({"success": False, "message": f"Odbijeno: {sym_msg}"}, 403)
                    return

            prep_lock_key = "GLOBAL_STAGE_PREPARE"
            locked, lock_info = acquire_target_lock(prep_lock_key, operation="prepare-program")
            if not locked:
                busy_msg = f"Priprema slike je već u toku ({lock_info.get('operation')})."
                audit_log("prepare-program", source, client_ip, "CONFLICT_BUSY", busy_msg)
                self._send_json({"success": False, "error": "TARGET_BUSY", "message": busy_msg}, 409)
                return

            ok, msg = GLOBAL_PROGRAM_REFRESH_WORKER.start_prepare(source)
            if not ok:
                release_target_lock(prep_lock_key)
                audit_log("prepare-program", source, client_ip, "CONFLICT_BUSY", msg)
                self._send_json({"success": False, "error": "TARGET_BUSY", "message": msg}, 409)
                return

            _watch_async_worker_and_release_lock(
                prep_lock_key, GLOBAL_PROGRAM_REFRESH_WORKER, "prepare-program", source, client_ip
            )
            audit_log("prepare-program", source, client_ip, "STARTED", msg)
            self._send_json({
                "success": True,
                "message": msg,
            })
            return

        # ----------------------------------------------------------------------
        # RUTA 6: POST /api/download-online-build
        # ----------------------------------------------------------------------
        if path == "/api/download-online-build":
            uuid = post_data.get("uuid")
            auto_sync_usb = bool(post_data.get("auto_sync_usb", False))
            edition = post_data.get("edition", "professional")
            validated_mountpoint = None
            usb_lock_key = None

            if auto_sync_usb:
                target_spec, exp_id = _extract_target_and_identity(post_data)
                if not target_spec:
                    audit_log("download-online-build", "-", client_ip, "REJECTED_MISSING_TARGET", "auto_sync_usb=True bez cilja")
                    self._send_json({
                        "success": False,
                        "message": "Uključen je auto_sync_usb, ali nije naveden validan USB uređaj!",
                    }, 400)
                    return

                ok_val, resolved = resolve_and_validate_usb_target(
                    target_spec,
                    operation="upgrade",
                    allow_dev_repo=False,
                    expected_identity=exp_id,
                )
                if not ok_val:
                    audit_log("download-online-build", target_spec, client_ip, "REJECTED_INVALID_TARGET", resolved)
                    self._send_json({"success": False, "message": resolved}, 403)
                    return

                validated_mountpoint = resolved["canonical_mountpoint"]
                usb_lock_key = resolved["lock_key"]
                if is_target_locked(usb_lock_key):
                    busy_msg = f"Ciljni USB uređaj ({resolved.get('device')}) je trenutno zauzet drugom operacijom."
                    audit_log("download-online-build", validated_mountpoint, client_ip, "CONFLICT_BUSY", busy_msg)
                    self._send_json({"success": False, "error": "TARGET_BUSY", "message": busy_msg}, 409)
                    return

            build_lock_key = "GLOBAL_ONLINE_BUILD"
            locked, lock_info = acquire_target_lock(build_lock_key, operation="download-online-build")
            if not locked:
                busy_msg = "Online preuzimanje i sklapanje slike je već u toku!"
                audit_log("download-online-build", str(uuid or "latest"), client_ip, "CONFLICT_BUSY", busy_msg)
                self._send_json({"success": False, "error": "TARGET_BUSY", "message": busy_msg}, 409)
                return

            ok, msg = GLOBAL_ONLINE_BUILD_WORKER.start_online_build(
                build_uuid=uuid,
                auto_sync_usb_mount=validated_mountpoint,
                edition=edition,
            )
            if not ok:
                release_target_lock(build_lock_key)
                audit_log("download-online-build", str(uuid or "latest"), client_ip, "CONFLICT_BUSY", msg)
                self._send_json({"success": False, "error": "TARGET_BUSY", "message": msg}, 409)
                return

            _watch_async_worker_and_release_lock(
                build_lock_key,
                GLOBAL_ONLINE_BUILD_WORKER,
                "download-online-build",
                validated_mountpoint or str(uuid or "latest"),
                client_ip,
            )
            audit_log(
                "download-online-build",
                validated_mountpoint or str(uuid or "latest"),
                client_ip,
                "STARTED",
                msg,
            )
            self._send_json({
                "success": True,
                "message": msg,
            })
            return

        # ----------------------------------------------------------------------
        # RUTA 7: POST /api/build-iso
        # ----------------------------------------------------------------------
        if path == "/api/build-iso":
            output_dir = str(post_data.get("output_dir") or "").strip() or None
            profile = str(post_data.get("profile", "lean")).strip().lower()
            no_prompt = bool(post_data.get("no_prompt", True))
            force = bool(post_data.get("force", False))
            engine = post_data.get("engine", "auto")
            dry_run = bool(post_data.get("dry_run", False))

            if output_dir:
                has_sym, sym_msg = _has_symlink_in_path(output_dir)
                if has_sym:
                    audit_log("build-iso", output_dir, client_ip, "REJECTED_SYMLINK", sym_msg)
                    self._send_json({"success": False, "message": f"Odbijeno: {sym_msg}"}, 403)
                    return
                forb, forb_msg = _is_forbidden_path(output_dir)
                if forb:
                    audit_log("build-iso", output_dir, client_ip, "REJECTED_FORBIDDEN_PATH", forb_msg)
                    self._send_json({"success": False, "message": f"Odbijeno: {forb_msg}"}, 403)
                    return

            iso_lock_key = "GLOBAL_ISO_BUILD"
            locked, _ = acquire_target_lock(iso_lock_key, operation="build-iso")
            if not locked:
                busy_msg = "Izgradnja Ventoy ISO slike je već u toku!"
                audit_log("build-iso", output_dir or "default", client_ip, "CONFLICT_BUSY", busy_msg)
                self._send_json({"success": False, "error": "TARGET_BUSY", "message": busy_msg}, 409)
                return

            try:
                ok, msg = GLOBAL_ISO_BUILD_MANAGER.start_build(
                    output_dir=output_dir,
                    profile=profile,
                    no_prompt=no_prompt,
                    force=force,
                    engine=engine,
                    dry_run=dry_run,
                )
            finally:
                release_target_lock(iso_lock_key)

            audit_log("build-iso", output_dir or "default", client_ip, "STARTED" if ok else "ERROR", msg)
            self._send_json({
                "success": ok,
                "message": msg,
                "status": GLOBAL_ISO_BUILD_MANAGER.get_status(),
            }, 200 if ok else 400)
            return

        audit_log(path, "-", client_ip, "NOT_FOUND", "Nepoznata POST ruta")
        self.send_error(404, "Nepoznata POST ruta")


# ==============================================================================
# 3. MREŽNI POMOĆNICI I POKRETANJE SERVERA
# ==============================================================================

def get_local_ip_addresses():
    """Vraća listu svih IPv4 adresa aktivnih mrežnih interfejsa."""
    ips = []
    try:
        with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as s:
            s.connect(("8.8.8.8", 80))
            primary = s.getsockname()[0]
            if primary not in ips:
                ips.append(primary)
    except Exception:
        pass
    if sys.platform != "win32":
        try:
            import subprocess
            out = subprocess.check_output(["ip", "-4", "-o", "addr", "show"], text=True, timeout=2)
            for line in out.splitlines():
                parts = line.split()
                if len(parts) >= 4:
                    ip = parts[3].split("/")[0]
                    if not ip.startswith("127.") and ip not in ips:
                        ips.append(ip)
        except Exception:
            pass
    return ips


def find_available_port(host="127.0.0.1", start_port=DEFAULT_PORT, max_attempts=10):
    """Pronalazi prvi slobodan TCP port za server."""
    for p in range(start_port, start_port + max_attempts):
        with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as s:
            s.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
            try:
                s.bind((host, p))
                return p
            except OSError:
                continue
    return start_port


def open_browser_delayed(url, delay=0.8):
    """Otvara korisnički pretraživač u posebnoj niti nakon startovanja servera."""
    def _open():
        time.sleep(delay)
        try:
            webbrowser.open(url)
        except Exception as e:
            print(f"[USB Tool] Nije moguće automatski otvoriti pregledač: {e}", file=sys.stderr)
    t = threading.Thread(target=_open, daemon=True)
    t.start()


def main():
    parser = argparse.ArgumentParser(
        description="GWADT USB Tool - Centar za Održavanje i Nadogradnju USB Fleša"
    )
    parser.add_argument(
        "--host",
        type=str,
        default="127.0.0.1",
        help="IP adresa na kojoj server sluša (podrazumevano 127.0.0.1 - samo lokalni pristup)",
    )
    parser.add_argument(
        "--allow-lan",
        action="store_true",
        help="Eksplicitno dozvoli LAN pristup sa drugih računara u mreži (bind na 0.0.0.0)",
    )
    parser.add_argument("--port", type=int, default=DEFAULT_PORT, help="TCP port za web server")
    parser.add_argument(
        "--no-browser", action="store_true", help="Ne otvaraj automatski internet pregledač"
    )
    parser.add_argument(
        "--dev",
        action="store_true",
        help="Omogući ne-destruktivni test kopiranja skripti u lokalni repozitorijum",
    )
    args = parser.parse_args()

    bind_host = args.host
    if args.allow_lan and bind_host == "127.0.0.1":
        bind_host = "0.0.0.0"

    is_lan_exposed = bind_host not in ("127.0.0.1", "localhost", "::1")

    SERVER_CONFIG["host"] = bind_host
    SERVER_CONFIG["allow_lan"] = bool(args.allow_lan or is_lan_exposed)
    SERVER_CONFIG["dev_mode"] = bool(
        args.dev or os.environ.get("USB_TOOL_DEV_MODE", "").strip().lower() in ("1", "true", "yes")
    )

    port = find_available_port(bind_host, args.port)
    SERVER_CONFIG["port"] = port
    server_address = (bind_host, port)

    try:
        httpd = ThreadingHTTPServer(server_address, UsbToolRequestHandler)
    except Exception as e:
        print(
            f"[GRESKA] Neuspešno pokretanje servera na adresi {bind_host}:{port}: {e}",
            file=sys.stderr,
        )
        return 1

    local_url = f"http://127.0.0.1:{port}"
    print("================================================================================")
    print("        GWADT USB TOOL - CENTAR ZA ODRŽAVANJE I NADOGRADNJU USB FLEŠA           ")
    print("                  Tehno-Kutak Windows 11 Automatizacija                         ")
    print("================================================================================")
    print(f" [OK] GUI Server je uspešno pokrenut na portu {port}!")
    print(f"      📍 Lokalni pristup:   {local_url}")
    if is_lan_exposed:
        print(" [UPOZORENJE / SECURITY] LAN pristup je UKLJUČEN (--allow-lan / --host)!")
        print("      Server prihvata mrežne konekcije. Svi mutirajući POST zahtevi zahtevaju")
        print("      validnu autentifikovanu sesiju i X-USB-Tool-Token zaglavlje.")
        if bind_host in ("0.0.0.0", ""):
            lan_ips = get_local_ip_addresses()
            for ip in lan_ips:
                print(f"      🌐 Mrežni pristup:   http://{ip}:{port}")
        else:
            print(f"      🌐 Pristupna adresa:  http://{bind_host}:{port}")
    else:
        print(" [SECURITY] Server je zaključan isključivo na lokalni interfejs (127.0.0.1).")
    if SERVER_CONFIG["dev_mode"]:
        print(" [DEV] Razvojni režim (--dev) aktivan samo za ne-destruktivno kopiranje skripti.")
    print(" [OK] CSRF zaštita, Origin validacija i server-side provera USB ciljeva su aktivni.")
    print(" [INFO] Pritisnite Ctrl+C za zaustavljanje servera.\n")

    if not args.no_browser:
        print("Otvaram kontrolni panel u vašem internet pregledaču...")
        open_browser_delayed(local_url)

    try:
        httpd.serve_forever()
    except KeyboardInterrupt:
        print("\nZaustavljam GWADT USB Tool server...")
    finally:
        httpd.server_close()
        print("Server je bezbedno zaustavljen.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
