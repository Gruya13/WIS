"""
servishub.handlers
==================
REST API ruter i HTTP handler za ServisHub.
"""

import os
import sys
import time
import json
import uuid
import threading
from urllib.parse import urlparse, parse_qs, unquote
from http.server import BaseHTTPRequestHandler

from servishub.config import active_config
from servishub.telemetry import FLEET_STORE
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
    ms_catalog_scraper = None
    start_time = time.time()
    reset_wis_burn_state = staticmethod(reset_wis_burn_state)

    def log_message(self, format, *args):
        # Isključujemo bučno logovanje svakog pojedinačnog GET/POST zahteva
        return

    def _send_json(self, data, code=200):
        body = json.dumps(data, ensure_ascii=False).encode("utf-8")
        self.send_response(code)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Access-Control-Allow-Origin", "*")
        self.send_header("Access-Control-Allow-Methods", "GET, POST, OPTIONS")
        self.send_header("Access-Control-Allow-Headers", "Content-Type, X-Payload-Version, X-WIS-Version, X-WIS-SHA256, X-Driver-ID, X-Client-Timestamp, Authorization")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def do_OPTIONS(self):
        self.send_response(200)
        self.send_header("Access-Control-Allow-Origin", "*")
        self.send_header("Access-Control-Allow-Methods", "GET, POST, OPTIONS")
        self.send_header("Access-Control-Allow-Headers", "Content-Type, X-Payload-Version, X-WIS-Version, X-WIS-SHA256, X-Driver-ID, X-Client-Timestamp, Authorization")
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
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)
            return

        if path in ("/api/health", "/api/status"):
            uptime = int(time.time() - self.start_time)
            self._send_json({"status": "ok", "uptime_sec": uptime, "version": VERSION, "release_status": RELEASE_STATUS})
            return

        if path == "/api/updates/status":
            from servishub.api.updates import scan_updates_directory
            query_params = parse_qs(parsed.query)
            custom_dir = query_params.get("directory", [None])[0]
            if not custom_dir:
                custom_dir = getattr(self.config, "updates_dir", None)
            status_data = scan_updates_directory(custom_dir)
            data = status_data.model_dump() if hasattr(status_data, "model_dump") else status_data.to_dict()
            self._send_json(data)
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
                    self.send_header("Access-Control-Allow-Origin", "*")
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
                self.send_header("Access-Control-Allow-Origin", "*")
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
            self.send_response(200)
            self.send_header("Content-Type", "text/event-stream")
            self.send_header("Cache-Control", "no-cache")
            self.send_header("Connection", "keep-alive")
            self.send_header("Access-Control-Allow-Origin", "*")
            self.end_headers()

            sub_queue = []
            sub_lock = threading.Condition()

            with WIS_BURN_LOCK:
                WIS_BURN_SUBSCRIBERS.append((sub_queue, sub_lock))
                sub_queue.append(dict(WIS_BURN_STATE))

            try:
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
                self.send_header("Access-Control-Allow-Origin", "*")
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
                session_id = parts[2]
                self.send_response(200)
                self.send_header("Content-Type", "text/event-stream")
                self.send_header("Cache-Control", "no-cache")
                self.send_header("Connection", "keep-alive")
                self.send_header("Access-Control-Allow-Origin", "*")
                self.end_headers()

                sub_queue = []
                sub_lock = threading.Condition()

                with self.fleet_store.session_lock:
                    if session_id not in self.fleet_store.session_subscribers:
                        self.fleet_store.session_subscribers[session_id] = []
                    self.fleet_store.session_subscribers[session_id].append((sub_queue, sub_lock))
                    curr = self.fleet_store.fleet_sessions.get(session_id)
                    if curr:
                        sub_queue.append(dict(curr))

                try:
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
                    self.send_header("Access-Control-Allow-Origin", "*")
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
                dev_id = parts[2]
                logs = self.fleet_store.get_device_logs(dev_id, limit=300)
                self._send_json(logs)
                return

        if path.startswith("/api/devices/") and path.endswith("/download"):
            parts = path.strip("/").split("/")
            if len(parts) >= 4:
                dev_id = parts[2]
                logs = self.fleet_store.get_device_logs(dev_id, limit=10000)
                body = "\n".join(logs).encode("utf-8")
                self.send_response(200)
                self.send_header("Content-Type", "text/plain; charset=utf-8")
                self.send_header("Content-Disposition", f'attachment; filename="servis_{dev_id}.log"')
                self.send_header("Content-Length", str(len(body)))
                self.end_headers()
                self.wfile.write(body)
                return

        if path.startswith("/api/devices/") and path.endswith("/report"):
            parts = path.strip("/").split("/")
            if len(parts) >= 4:
                dev_id = parts[2]

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
                serial = parts[3]
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
                device_tag = parts[2]
                history = get_device_history(device_tag, self.config.reports_dir)
                self._send_json(history, 200)
                return

        if path.startswith("/api/stream/"):
            dev_id = path[len("/api/stream/"):]
            self.send_response(200)
            self.send_header("Content-Type", "text/event-stream")
            self.send_header("Cache-Control", "no-cache")
            self.send_header("Connection", "keep-alive")
            self.send_header("Access-Control-Allow-Origin", "*")
            self.end_headers()

            sub_queue = []
            sub_lock = threading.Condition()

            with self.fleet_store.fleet_lock:
                if dev_id not in self.fleet_store.fleet_subscribers:
                    self.fleet_store.fleet_subscribers[dev_id] = []
                self.fleet_store.fleet_subscribers[dev_id].append((sub_queue, sub_lock))

            try:
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

        # Standardne JSON POST rute
        content_length = int(self.headers.get("Content-Length", 0))
        post_data = self.rfile.read(content_length) if content_length > 0 else b"{}"

        try:
            payload = json.loads(post_data.decode("utf-8"))
        except Exception:
            self._send_json({"error": "Nevalidan JSON"}, 400)
            return

        if path == "/api/wis/usb/burn":
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
            iso_path = payload.get("iso_path")
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

        if path == "/api/drivers/resolve":
            if not isinstance(payload, dict):
                self._send_json({"error": "Telo zahteva mora biti JSON objekat"}, 400)
                return

            raw_hwids = payload.get("hwids", [])
            if isinstance(raw_hwids, str):
                raw_hwids = [raw_hwids]
            elif not isinstance(raw_hwids, list):
                raw_hwids = []

            device_name = payload.get("device_name", "UnknownDevice")
            manufacturer = str(payload.get("manufacturer") or "").strip()
            model = str(payload.get("model") or "").strip()
            serial_number = str(payload.get("serial_number") or payload.get("serial") or "").strip()

            if not raw_hwids and not (manufacturer and (serial_number or model)):
                self._send_json({"error": "Polje 'hwids' mora sadržati listu hardverskih identifikatora (HWID) ili OEM parametre (manufacturer, serial_number/model)"}, 400)
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

            print(f"[*] [DriverResolve] Primljen upit za {len(raw_hwids)} HWID-ja (Uređaj: {device_name} / {manufacturer} {model})...")

            results = []
            for hwid in raw_hwids:
                hwid_str = str(hwid).strip()
                if not hwid_str:
                    continue

                cached = self.driver_cache_mgr.get_cached(hwid_str) if self.driver_cache_mgr else None
                if cached:
                    drv_id = cached.get("driver_id", "")
                    print(f"  [+] [CACHE HIT] {hwid_str} -> {cached.get('title', 'Driver')} (ID: {drv_id[:12]}...)")
                    results.append({
                        "hwid": hwid_str,
                        "status": "ready",
                        "driver_id": drv_id,
                        "download_url": f"/api/drivers/download?id={drv_id}",
                        "title": cached.get("title", ""),
                        "version": cached.get("version", ""),
                        "date": cached.get("date", ""),
                        "device_class": cached.get("device_class", ""),
                        "size_bytes": cached.get("size_bytes", 0),
                        "sha256": cached.get("sha256", ""),
                        "source": "cache"
                    })
                    continue

                print(f"  [*] [CACHE MISS] Pretražujem Microsoft Update Catalog za: {hwid_str}...")
                resolved = self.ms_catalog_scraper.resolve_driver(hwid_str) if self.ms_catalog_scraper else None
                if not resolved:
                    print(f"  [-] [NOT FOUND] Drajver nije pronađen na Microsoft katalogu: {hwid_str}")
                    results.append({
                        "hwid": hwid_str,
                        "status": "not_found",
                        "error": "Drajver nije pronađen na Microsoft Update Katalogu za zadati hardver."
                    })
                    continue

                cab_url = resolved.get("download_url")
                print(f"  [*] [DOWNLOAD] Preuzimam zvanični CAB drajver sa Microsoft CDN-a: {resolved.get('title')} ({cab_url[:70]}...)...")
                try:
                    cab_data = self.ms_catalog_scraper.download_cab(cab_url)
                    aliases = [hwid_str, resolved.get("matched_query")]
                    saved = self.driver_cache_mgr.save_cache(hwid_str, cab_data, resolved, alias_hwids=aliases) if self.driver_cache_mgr else resolved
                    drv_id = saved.get("driver_id", "")
                    print(f"  [+] [RESOLVED & CACHED] {hwid_str} -> {saved.get('title')} ({saved.get('size_bytes')} B)")
                    results.append({
                        "hwid": hwid_str,
                        "status": "ready",
                        "driver_id": drv_id,
                        "download_url": f"/api/drivers/download?id={drv_id}",
                        "title": saved.get("title", ""),
                        "version": saved.get("version", ""),
                        "date": saved.get("date", ""),
                        "device_class": saved.get("device_class", ""),
                        "size_bytes": saved.get("size_bytes", 0),
                        "sha256": saved.get("sha256", ""),
                        "source": "catalog_download"
                    })
                except Exception as ex:
                    print(f"  [!] [ERROR] Greška pri preuzimanju drajvera sa Microsoft CDN-a: {ex}", file=sys.stderr)
                    results.append({
                        "hwid": hwid_str,
                        "status": "error",
                        "error": f"Greška pri preuzimanju ili verifikaciji drajvera: {str(ex)}"
                    })

            resp_payload = {
                "status": "ok",
                "device_name": device_name,
                "count": len(results),
                "results": results
            }
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
                url_dev_id = parts[2]
                if not isinstance(payload, dict):
                    self._send_json({"error": "Telo zahteva mora biti JSON objekat"}, 400)
                    return

                dev_id = payload.get("device_id") or url_dev_id
                clean_id = sanitize_id(dev_id)
                comp_name = payload.get("computer_name") or dev_id
                chassis = payload.get("chassis") or "Desktop"
                serial_num = payload.get("serial_number") or (payload.get("hw") or {}).get("serial") or "UNKNOWN"
                status = payload.get("status") or "COMPLETED"

                try:
                    dur_sec = int(payload.get("duration_seconds", 0))
                except (ValueError, TypeError):
                    dur_sec = 0

                apps = payload.get("apps", {})
                if not isinstance(apps, (dict, list)):
                    apps = {}

                errors = payload.get("errors", [])
                if not isinstance(errors, list):
                    errors = [str(errors)] if errors else []

                minutes = dur_sec // 60
                seconds = dur_sec % 60
                dur_fmt = payload.get("duration_formatted") or f"{minutes}m {seconds:02d}s"

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
                    self.fleet_store.fleet_devices[clean_id].update(diagnostics_contract(payload))

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
                url_device_tag = parts[2]
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
                url_device_tag = parts[2]
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
                url_device_tag = parts[2]
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
                session_id = parts[2]
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

        self.send_response(404)
        self.end_headers()
