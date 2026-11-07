"""
servishub.telemetry
===================
Thread-safe skladište za telemetriju flote računara, aktivne sesije,
live logove i SSE pretplatnike.
"""

import os
import sys
import time
import json
import uuid
import threading
from datetime import datetime
from servishub.drivers.cache import sanitize_id
from servishub.reports import diagnostics_contract


class FleetStore:
    """
    Centralno in-memory skladište za telemetriju, sesije i logove radioničke flote.
    """

    def __init__(self, live_logs_dir: str = None, reports_dir: str = None):
        self.live_logs_dir = live_logs_dir
        self.reports_dir = reports_dir

        self.fleet_lock = threading.Lock()
        self.fleet_devices = {}      # device_id -> dict
        self.fleet_logs = {}         # device_id -> list of log lines
        self.fleet_subscribers = {}  # device_id -> list of (sub_queue, sub_lock)

        self.session_lock = threading.Lock()
        self.fleet_sessions = {}      # session_id -> dict
        self.session_subscribers = {} # session_id -> list of (sub_queue, sub_lock)

        # In-memory thread-safe bafer za frejmove ekrana (1 Hz screen mirror)
        self.screen_lock = threading.Lock()
        self.screen_frames = {}       # device_id -> {"frame": bytes, "size": int, "timestamp": float}

        # Thread-safe komandni red za daljinsko upravljanje (BIOS reboot, PXE, abort itd.)
        self.command_lock = threading.Lock()
        self.fleet_commands = {}      # device_id -> list of command dicts

        if self.reports_dir and os.path.isdir(self.reports_dir):
            self.load_historical_devices()

    def _parse_report_timestamp(self, data: dict, filepath: str) -> float:
        """Parsira vremensku oznaku iz JSON podataka ili vraća mtime fajla."""
        raw_ts = data.get("timestamp") or data.get("last_seen")
        if raw_ts is not None:
            if isinstance(raw_ts, (int, float)):
                return float(raw_ts)
            if isinstance(raw_ts, str):
                try:
                    return datetime.fromisoformat(raw_ts.replace("Z", "+00:00")).timestamp()
                except Exception:
                    for fmt in ("%Y-%m-%d %H:%M:%S", "%Y%m%d_%H%M%S", "%Y-%m-%dT%H:%M:%S"):
                        try:
                            return datetime.strptime(raw_ts, fmt).timestamp()
                        except ValueError:
                            pass
        try:
            return os.path.getmtime(filepath)
        except Exception:
            return 0.0

    def _load_report_file(self, filepath: str):
        """Bezbedno učitava pojedinačni .json izveštaj u fleet_devices."""
        try:
            with open(filepath, "r", encoding="utf-8") as f:
                data = json.load(f)
        except Exception:
            return

        if not isinstance(data, dict):
            return

        comp_name = data.get("computer_name") or data.get("device_id") or data.get("serial_number")
        if not comp_name:
            return

        dev_id = sanitize_id(comp_name)
        file_ts = self._parse_report_timestamp(data, filepath)

        with self.fleet_lock:
            existing = self.fleet_devices.get(dev_id)
            if existing:
                existing_ts = existing.get("last_seen", 0.0)
                if file_ts <= existing_ts:
                    return

            status_raw = data.get("status", "COMPLETED")
            status_label = "Zavrseno"
            if data.get("status_label") in ("Servisiran (AGY)", "Kvar (AGY)"):
                status_label = data.get("status_label")
            elif existing and existing.get("status_label") in ("Servisiran (AGY)", "Kvar (AGY)"):
                status_label = existing["status_label"]
            elif status_raw in ("COMPLETED", "SUCCESS", "OK"):
                status_label = "Zavrseno"
            else:
                status_label = data.get("status_label", "Zavrseno")

            record = {
                "computer_name": comp_name,
                "device_id": data.get("device_id") or dev_id,
                "serial_number": data.get("serial_number") or data.get("serial", ""),
                "chassis": data.get("chassis", ""),
                "status": status_raw,
                "status_label": status_label,
                "phase": 5,
                "worker_progress": 100,
                "duration_formatted": data.get("duration_formatted", "N/A"),
                "duration_seconds": data.get("duration_seconds", 0),
                "apps": data.get("apps", {}),
                "system_summary": data.get("system_summary", {}),
                "hw": data.get("hw", {}) or data.get("hardware", {}),
                "autopilot": data.get("autopilot", {}),
                "last_seen": file_ts,
                "is_online": False,
                "latest_report_file": filepath,
                "latest_report": data,
            }
            if "warranty" in data:
                record["warranty"] = data["warranty"]
            if "oem" in data:
                record["oem"] = data["oem"]
            if "errors" in data:
                record["errors"] = data["errors"]
                if data["errors"]:
                    record["has_errors"] = True

            hist_sensors = self._extract_telemetry_sensors(data)
            record.update(hist_sensors)

            self.fleet_devices[dev_id] = record

    def _load_device_info_dir(self, dirpath: str, filepath: str):
        """Bezbedno učitava AGY sesiju sa DEVICE_INFO.json u fleet_devices."""
        try:
            with open(filepath, "r", encoding="utf-8") as f:
                data = json.load(f)
        except Exception:
            return

        if not isinstance(data, dict):
            return

        comp_name = (
            data.get("computer_name")
            or data.get("device_tag")
            or os.path.basename(dirpath)
            or data.get("device_id")
            or data.get("serial_number")
        )
        if not comp_name:
            return

        dev_id = sanitize_id(comp_name)
        file_ts = self._parse_report_timestamp(data, filepath)

        serial_val = (
            data.get("serial_number")
            or data.get("serial", "")
            or (data.get("hardware") if isinstance(data.get("hardware"), dict) else {}).get("serial_number", "")
        )

        status_raw = data.get("status", "COMPLETED")
        status_label = data.get("status_label") or "Servisiran (AGY)"
        if status_label not in ("Servisiran (AGY)", "Kvar (AGY)"):
            status_label = "Servisiran (AGY)"

        with self.fleet_lock:
            existing = self.fleet_devices.get(dev_id)
            if existing:
                existing_ts = existing.get("last_seen", 0.0)
                if file_ts > existing_ts:
                    record = {
                        "computer_name": comp_name,
                        "device_id": data.get("device_id") or dev_id,
                        "serial_number": serial_val,
                        "chassis": data.get("chassis", ""),
                        "status": status_raw,
                        "status_label": status_label,
                        "phase": 5,
                        "worker_progress": 100,
                        "duration_formatted": data.get("duration_formatted", "N/A"),
                        "duration_seconds": data.get("duration_seconds", 0),
                        "apps": data.get("apps", {}),
                        "system_summary": data.get("system_summary", {}),
                        "hw": data.get("hw") or data.get("hardware") or {},
                        "autopilot": data.get("autopilot", {}),
                        "last_seen": file_ts,
                        "is_online": False,
                        "latest_report_file": filepath,
                        "latest_report": data,
                    }
                    if "warranty" in data:
                        record["warranty"] = data["warranty"]
                    if "oem" in data:
                        record["oem"] = data["oem"]
                    if "errors" in data:
                        record["errors"] = data["errors"]
                        if data["errors"]:
                            record["has_errors"] = True
                    hist_sensors = self._extract_telemetry_sensors(data)
                    record.update(hist_sensors)
                    self.fleet_devices[dev_id] = record
                else:
                    if existing.get("status_label") not in ("Servisiran (AGY)", "Kvar (AGY)"):
                        existing["status_label"] = status_label
                    if "warranty" in data and "warranty" not in existing:
                        existing["warranty"] = data["warranty"]
                    if "oem" in data and "oem" not in existing:
                        existing["oem"] = data["oem"]
                    hist_sensors = self._extract_telemetry_sensors(data)
                    for sf, val in hist_sensors.items():
                        if sf not in existing:
                            existing[sf] = val
            else:
                record = {
                    "computer_name": comp_name,
                    "device_id": data.get("device_id") or dev_id,
                    "serial_number": serial_val,
                    "chassis": data.get("chassis", ""),
                    "status": status_raw,
                    "status_label": status_label,
                    "phase": 5,
                    "worker_progress": 100,
                    "duration_formatted": data.get("duration_formatted", "N/A"),
                    "duration_seconds": data.get("duration_seconds", 0),
                    "apps": data.get("apps", {}),
                    "system_summary": data.get("system_summary", {}),
                    "hw": data.get("hw") or data.get("hardware") or {},
                    "autopilot": data.get("autopilot", {}),
                    "last_seen": file_ts,
                    "is_online": False,
                    "latest_report_file": filepath,
                    "latest_report": data,
                }
                if "warranty" in data:
                    record["warranty"] = data["warranty"]
                if "oem" in data:
                    record["oem"] = data["oem"]
                if "errors" in data:
                    record["errors"] = data["errors"]
                    if data["errors"]:
                        record["has_errors"] = True
                hist_sensors = self._extract_telemetry_sensors(data)
                record.update(hist_sensors)
                self.fleet_devices[dev_id] = record

    def load_historical_devices(self):
        """
        Učitava arhivirane servisne izveštaje i AGY sesije iz reports_dir
        i popunjava in-memory skladište fleet_devices.
        """
        if not self.reports_dir or not os.path.isdir(self.reports_dir):
            return

        try:
            entries = sorted(os.listdir(self.reports_dir))
        except Exception as e:
            print(f"[WARN] Neuspeh čitanja reports_dir ({self.reports_dir}): {e}", file=sys.stderr)
            return

        # 1. Pojedinačni .json fajlovi (npr. report_*.json ili 2026*.json)
        for entry in entries:
            entry_path = os.path.join(self.reports_dir, entry)
            if os.path.isfile(entry_path) and entry.lower().endswith(".json"):
                self._load_report_file(entry_path)

        # 2. Poddirektorijumi sa DEVICE_INFO.json (npr. AGY sesije)
        for entry in entries:
            entry_path = os.path.join(self.reports_dir, entry)
            if os.path.isdir(entry_path):
                dinfo_path = os.path.join(entry_path, "DEVICE_INFO.json")
                if os.path.isfile(dinfo_path):
                    self._load_device_info_dir(entry_path, dinfo_path)

    def append_to_disk_log(self, computer_name: str, lines: list):
        """Zapisuje nove linije loga u trajni lokalni fajl na disku."""
        if not self.live_logs_dir:
            return
        try:
            os.makedirs(self.live_logs_dir, exist_ok=True)
            sanitized = sanitize_id(computer_name)
            date_str = datetime.now().strftime("%Y%m%d")
            log_file = os.path.join(self.live_logs_dir, f"servis_deploy_{sanitized}_{date_str}.log")
            with open(log_file, "a", encoding="utf-8") as f:
                for l in lines:
                    f.write(l.strip() + "\n")
        except Exception as e:
            print(f"[ERROR] Neuspeh upisa loga na disk: {e}", file=sys.stderr)

    def _extract_telemetry_sensors(self, payload: dict) -> dict:
        """
        Izdvaja i bezbedno normalizuje senzorska polja telemetrije iz payload-a:
        - cpu_percent (int/float)
        - cpu_temp (int/float)
        - ram_used (str ili float)
        - ram_total (str ili float)
        - ram_percent (int/float)
        - smart (dict sa senzorskim podacima diska: npr. Temperature, SsdHealth, Category)

        Ako polje nedostaje, nevalidno je ili je None, ignoriše se bez podizanja izuzetka.
        """
        extracted = {}
        if not isinstance(payload, dict):
            return extracted

        nested_sources = []
        if isinstance(payload.get("sensors"), dict):
            nested_sources.append(payload["sensors"])
        if isinstance(payload.get("tab_data"), dict):
            nested_sources.append(payload["tab_data"])
        if isinstance(payload.get("telemetry"), dict):
            nested_sources.append(payload["telemetry"])

        def _get_raw(keys: tuple):
            for k in keys:
                if k in payload and payload[k] is not None:
                    return payload[k]
            for src in nested_sources:
                for k in keys:
                    if k in src and src[k] is not None:
                        return src[k]
            return None

        # 1. cpu_percent (int/float)
        raw_cpu = _get_raw(("cpu_percent", "cpu_usage"))
        if raw_cpu is not None:
            if isinstance(raw_cpu, (int, float)) and not isinstance(raw_cpu, bool):
                extracted["cpu_percent"] = raw_cpu
            elif isinstance(raw_cpu, str):
                try:
                    c = raw_cpu.replace("%", "").strip()
                    val = float(c)
                    extracted["cpu_percent"] = int(val) if val.is_integer() else val
                except (ValueError, TypeError):
                    pass

        # 2. cpu_temp (int/float)
        raw_temp = _get_raw(("cpu_temp", "cpu_temperature", "temp", "temperature"))
        if raw_temp is not None:
            if isinstance(raw_temp, (int, float)) and not isinstance(raw_temp, bool):
                extracted["cpu_temp"] = raw_temp
            elif isinstance(raw_temp, str):
                try:
                    c = raw_temp.replace("°C", "").replace("°c", "").replace("°", "").replace("C", "").replace("c", "").strip()
                    val = float(c)
                    extracted["cpu_temp"] = int(val) if val.is_integer() else val
                except (ValueError, TypeError):
                    pass

        # 3. ram_used (str ili float)
        raw_ram_used = _get_raw(("ram_used", "memory_used"))
        if raw_ram_used is not None:
            if isinstance(raw_ram_used, (int, float)) and not isinstance(raw_ram_used, bool):
                extracted["ram_used"] = raw_ram_used
            elif isinstance(raw_ram_used, str) and raw_ram_used.strip():
                extracted["ram_used"] = raw_ram_used.strip()

        # 4. ram_total (str ili float)
        raw_ram_total = _get_raw(("ram_total", "memory_total"))
        if raw_ram_total is not None:
            if isinstance(raw_ram_total, (int, float)) and not isinstance(raw_ram_total, bool):
                extracted["ram_total"] = raw_ram_total
            elif isinstance(raw_ram_total, str) and raw_ram_total.strip():
                extracted["ram_total"] = raw_ram_total.strip()

        # 5. ram_percent (int/float)
        raw_ram_pct = _get_raw(("ram_percent", "memory_percent"))
        if raw_ram_pct is not None:
            if isinstance(raw_ram_pct, (int, float)) and not isinstance(raw_ram_pct, bool):
                extracted["ram_percent"] = raw_ram_pct
            elif isinstance(raw_ram_pct, str):
                try:
                    c = raw_ram_pct.replace("%", "").strip()
                    val = float(c)
                    extracted["ram_percent"] = int(val) if val.is_integer() else val
                except (ValueError, TypeError):
                    pass

        # 6. smart (dict sa senzorskim podacima diska)
        raw_smart = _get_raw(("smart", "disk_smart"))
        if raw_smart is not None and isinstance(raw_smart, dict):
            s_dict = dict(raw_smart)
            if "SsdHealth" in s_dict and "ssd_health" not in s_dict:
                s_dict["ssd_health"] = s_dict["SsdHealth"]
            elif "ssd_health" in s_dict and "SsdHealth" not in s_dict:
                s_dict["SsdHealth"] = s_dict["ssd_health"]

            if "Category" in s_dict and "category" not in s_dict:
                s_dict["category"] = s_dict["Category"]
            elif "category" in s_dict and "Category" not in s_dict:
                s_dict["Category"] = s_dict["category"]

            if "Temperature" in s_dict and "temperature" not in s_dict:
                s_dict["temperature"] = s_dict["Temperature"]
            elif "temperature" in s_dict and "Temperature" not in s_dict:
                s_dict["Temperature"] = s_dict["temperature"]

            extracted["smart"] = s_dict

        return extracted

    def update_fleet_heartbeat(self, payload: dict, client_ip: str = "") -> str:
        """Ažurira telemetriju i puls uređaja u floti (alias za record_heartbeat)."""
        return self.record_heartbeat(payload, client_ip)

    def record_heartbeat(self, payload: dict, client_ip: str = "") -> str:
        """Beleži puls uređaja sa podrškom za senzorska očitavanja."""
        if not isinstance(payload, dict):
            payload = {}

        comp_name = payload.get("computer_name") or "UNKNOWN-PC"
        dev_id = sanitize_id(comp_name)
        now = time.time()
        payload["client_ip"] = client_ip
        payload["last_seen"] = now

        # Izdvajanje novih senzorskih polja
        sensors = self._extract_telemetry_sensors(payload)

        # Čišćenje payload-a od None vrednosti da ne bi prebrisale validne podatke
        clean_update = {k: v for k, v in payload.items() if v is not None}

        # Uklanjanje nevalidnih ili sirovih senzorskih polja iz clean_update,
        # kako prazni stringovi, boolean vrednosti ili malformed podaci
        # ne bi pregazili postojeću validnu telemetriju
        sensor_alias_fields = (
            "cpu_percent", "cpu_usage",
            "cpu_temp", "cpu_temperature", "temp", "temperature",
            "ram_used", "memory_used",
            "ram_total", "memory_total",
            "ram_percent", "memory_percent",
            "smart", "disk_smart",
        )
        for sf in sensor_alias_fields:
            if sf in clean_update and sf not in sensors:
                del clean_update[sf]

        clean_update.update(sensors)

        with self.fleet_lock:
            if dev_id not in self.fleet_devices:
                self.fleet_devices[dev_id] = {
                    "computer_name": comp_name,
                    "device_id": payload.get("device_id") or dev_id,
                }
                print(f"[*] [NOVI RAČUNAR] Detektovan u servisu: {comp_name} ({client_ip})")
            self.fleet_devices[dev_id].update(clean_update)

        return dev_id

    def record_logs(self, comp_name: str, lines: list) -> int:
        """Prihvata novu grupu linija loga, baferuje u memoriji i obaveštava pretplatnike."""
        dev_id = sanitize_id(comp_name)
        if not lines:
            return 0

        with self.fleet_lock:
            if dev_id not in self.fleet_logs:
                self.fleet_logs[dev_id] = []
            self.fleet_logs[dev_id].extend(lines)
            if len(self.fleet_logs[dev_id]) > 5000:
                self.fleet_logs[dev_id] = self.fleet_logs[dev_id][-5000:]

            if dev_id in self.fleet_subscribers:
                for sub_q, sub_lock in self.fleet_subscribers[dev_id]:
                    with sub_lock:
                        sub_q.extend(lines)
                        sub_lock.notify_all()

        self.append_to_disk_log(comp_name, lines)
        return len(lines)

    def record_lifecycle_start(self, payload: dict, client_ip: str) -> str:
        """Obrada STARTED događaja životnog ciklusa."""
        comp_name = payload.get("computer_name") or "UNKNOWN-PC"
        dev_id = sanitize_id(comp_name)
        now = time.time()

        with self.fleet_lock:
            if dev_id not in self.fleet_devices:
                self.fleet_devices[dev_id] = {}
            self.fleet_devices[dev_id].update({
                "computer_name": comp_name,
                "status_label": "Aktivno",
                "phase": 1,
                "autopilot": payload.get("autopilot", {}),
                "hw": payload.get("hw", {}),
                "started_at": payload.get("timestamp", datetime.now().isoformat()),
                "last_seen": now,
                "client_ip": client_ip,
            })
            sensors = self._extract_telemetry_sensors(payload)
            if sensors:
                self.fleet_devices[dev_id].update(sensors)

        return dev_id

    def record_lifecycle_complete(self, payload: dict, client_ip: str) -> str:
        """Obrada COMPLETED događaja životnog ciklusa i arhiviranje kartona."""
        comp_name = payload.get("computer_name") or "UNKNOWN-PC"
        dev_id = sanitize_id(comp_name)
        duration_formatted = payload.get("duration_formatted", "N/A")
        duration_seconds = payload.get("duration_seconds", 0)
        apps = payload.get("apps", {})
        system_summary = payload.get("system_summary", {})

        with self.fleet_lock:
            if dev_id not in self.fleet_devices:
                self.fleet_devices[dev_id] = {"computer_name": comp_name, "client_ip": client_ip}
            self.fleet_devices[dev_id].update({
                "computer_name": comp_name,
                "status_label": "Zavrseno",
                "phase": 6,
                "worker_progress": 100,
                "duration_formatted": duration_formatted,
                "duration_seconds": duration_seconds,
                "apps": apps,
                "system_summary": system_summary,
                "last_seen": time.time(),
            })
            self.fleet_devices[dev_id].update(diagnostics_contract(payload))
            if self.fleet_devices[dev_id]["diagnostics_status"] == "THERMAL_CRITICAL":
                self.fleet_devices[dev_id]["status_label"] = "NEEDS_ATTENTION"
            if "autopilot" in payload:
                self.fleet_devices[dev_id]["autopilot"] = payload["autopilot"]
            if "hw" in payload:
                self.fleet_devices[dev_id]["hw"] = payload["hw"]
            sensors = self._extract_telemetry_sensors(payload)
            if sensors:
                self.fleet_devices[dev_id].update(sensors)

        if self.reports_dir:
            target_id = payload.get("serial_number") or comp_name
            date_str = datetime.now().strftime("%Y%m%d_%H%M%S")
            report_filename = f"report_{sanitize_id(target_id)}_{date_str}.json"
            report_filepath = os.path.join(self.reports_dir, report_filename)
            try:
                os.makedirs(self.reports_dir, exist_ok=True)
                with open(report_filepath, "w", encoding="utf-8") as f:
                    json.dump(payload, f, indent=2, ensure_ascii=False)
                with self.fleet_lock:
                    self.fleet_devices[dev_id]["latest_report_file"] = report_filepath
                    self.fleet_devices[dev_id]["latest_report"] = payload
            except Exception as e:
                print(f"[ERROR] Neuspeh čuvanja servisnog kartona na disk: {e}", file=sys.stderr)

        return dev_id

    def record_session_state(self, session_id: str, payload: dict, client_ip: str) -> dict:
        """Obrada stanja GUI sesije i slanje SSE pretplatnicima."""
        now = time.time()
        comp_name = payload.get("computer_name") or session_id
        phase = payload.get("phase", 1)
        sub_phase = payload.get("sub_phase", "")
        progress = payload.get("progress_percent", 0)
        status_msg = payload.get("status_message", "")
        is_paused = payload.get("is_paused", False)
        usb_free = bool(payload.get("usb_free", False) or payload.get("usb_can_be_removed", False))
        tab_data = payload.get("tab_data") or {}
        model_info = payload.get("model_info", "") or payload.get("device_model", "")
        smart_data = tab_data.get("smart") if isinstance(tab_data, dict) else {}
        if not isinstance(smart_data, dict):
            smart_data = {}
        ssd_pct = payload.get("ssd_health_percent") or smart_data.get("health_percent") or smart_data.get("percent") or 0
        ssd_cat = payload.get("ssd_health_category") or smart_data.get("category") or "HEALTHY"

        session_record = {
            "session_id": session_id,
            "computer_name": comp_name,
            "device_model": model_info,
            "client_ip": client_ip,
            "last_seen": now,
            "updated_at": datetime.now().isoformat(),
            "phase": phase,
            "sub_phase": sub_phase,
            "progress_percent": progress,
            "status_message": status_msg,
            "status": "PAUSED" if is_paused else str(payload.get("deployment_status") or payload.get("status") or "ACTIVE").upper(),
            "status_label": "Pauzirano" if is_paused else ("Zavrseno" if str(payload.get("deployment_status") or payload.get("status") or "").upper() in ("DONE", "READY", "COMPLETED", "SUCCESS") else "Aktivno"),
            "is_stale": False,
            "is_paused": is_paused,
            "usb_free": usb_free,
            "usb_can_be_removed": usb_free,
            "ssd_health_percent": ssd_pct,
            "ssd_health_category": ssd_cat,
            "model_info": model_info,
            "tab_data": tab_data,
            "state": payload,
        }

        with self.session_lock:
            self.fleet_sessions[session_id] = session_record
            if session_id in self.session_subscribers:
                for sub_q, sub_lock in self.session_subscribers[session_id]:
                    with sub_lock:
                        sub_q.append(dict(session_record))
                        sub_lock.notify_all()

        dev_id = sanitize_id(comp_name)
        with self.fleet_lock:
            if dev_id not in self.fleet_devices:
                self.fleet_devices[dev_id] = {"computer_name": comp_name}
            self.fleet_devices[dev_id].update({
                "computer_name": comp_name,
                "client_ip": client_ip,
                "last_seen": now,
                "phase": phase,
                "sub_phase": sub_phase,
                "worker_progress": progress,
                "live_status": status_msg or sub_phase or f"Faza {phase}",
                "session_id": session_id,
                "is_paused": is_paused,
                "usb_free": usb_free,
            })
            if tab_data and isinstance(tab_data, dict):
                self.fleet_devices[dev_id]["tab_data"] = tab_data
                if "smart" in tab_data:
                    self.fleet_devices[dev_id]["smart"] = tab_data["smart"]
                if "hw" in tab_data and isinstance(tab_data["hw"], dict):
                    self.fleet_devices[dev_id]["hw"] = tab_data["hw"]
                if "warranty" in tab_data and isinstance(tab_data["warranty"], dict):
                    self.fleet_devices[dev_id]["warranty"] = tab_data["warranty"]
            if "warranty" in payload and isinstance(payload["warranty"], dict):
                self.fleet_devices[dev_id]["warranty"] = payload["warranty"]
            if model_info and "hw" not in self.fleet_devices[dev_id]:
                self.fleet_devices[dev_id]["hw"] = {"model": model_info}
            session_sensors = self._extract_telemetry_sensors(payload)
            if session_sensors:
                self.fleet_devices[dev_id].update(session_sensors)

        return session_record

    def store_screen_frame(self, device_id: str, frame_bytes: bytes, client_ts: float = None) -> bool:
        """
        Čuva najnoviji binarni frejm ekrana za dati uređaj u memorijskom baferu.
        Thread-safe, automatski beleži veličinu i vremensku oznaku (client_ts ili server time.time()).
        """
        if not device_id or not isinstance(frame_bytes, (bytes, bytearray)):
            return False
        if len(frame_bytes) == 0:
            return False

        dev_id = sanitize_id(device_id)
        now = time.time()
        try:
            ts = float(client_ts) if client_ts is not None else now
        except (ValueError, TypeError):
            ts = now

        frame_data = {
            "frame": bytes(frame_bytes),
            "size": len(frame_bytes),
            "timestamp": ts,
        }

        with self.screen_lock:
            self.screen_frames[dev_id] = frame_data
            if dev_id != device_id:
                self.screen_frames[device_id] = frame_data

        with self.fleet_lock:
            if dev_id in self.fleet_devices:
                self.fleet_devices[dev_id]["has_screen_stream"] = True

        return True

    def get_screen_frame(self, device_id: str, max_age_seconds: float = 5.0) -> tuple[bytes, float] | None:
        """
        Dohvata najnoviji binarni frejm ekrana za dati uređaj ako nije stariji od max_age_seconds.
        Vraća (frame_bytes, timestamp) ili None ako je frejm istekao ili ne postoji.
        """
        if not device_id:
            return None

        dev_id = sanitize_id(device_id)
        now = time.time()

        with self.screen_lock:
            entry = self.screen_frames.get(dev_id)
            if entry is None and device_id in self.screen_frames:
                entry = self.screen_frames.get(device_id)

            if not entry:
                return None

            frame_ts = entry.get("timestamp", 0.0)
            age = now - frame_ts

            if max_age_seconds is not None and age > max_age_seconds:
                return None

            return (entry["frame"], frame_ts)

    def has_active_screen_stream(self, device_id: str, max_age_seconds: float = 5.0) -> bool:
        """Proverava da li uređaj ima aktivan i svež strim ekrana."""
        return self.get_screen_frame(device_id, max_age_seconds=max_age_seconds) is not None

    def get_devices_summary(self) -> dict:
        """Vraća mapu svih uređaja sa izračunatim statusima i senzorskom telemetrijom."""
        now = time.time()
        active_streams = set()
        with self.screen_lock:
            for did, f_info in self.screen_frames.items():
                if (now - f_info.get("timestamp", 0.0)) <= 5.0:
                    active_streams.add(did)

        out = {}
        with self.fleet_lock:
            for k, d in self.fleet_devices.items():
                sec_ago = int(now - d.get("last_seen", now))
                phase = d.get("phase", 1)
                is_online = (sec_ago <= 20)

                status_lbl = "Aktivno"
                if d.get("diagnostics_status") == "THERMAL_CRITICAL" or str(d.get("deployment_status", "")).upper() == "NEEDS_ATTENTION" or d.get("status_label") == "NEEDS_ATTENTION":
                    status_lbl = "NEEDS_ATTENTION"
                elif d.get("status_label") in ("Servisiran (AGY)", "Kvar (AGY)") or "AGY" in str(d.get("status_label", "")):
                    status_lbl = d.get("status_label")
                elif d.get("status_label") == "Zavrseno" or str(d.get("deployment_status") or d.get("status") or "").upper() in ("DONE", "READY", "COMPLETED", "SUCCESS"):
                    status_lbl = "Zavrseno"
                elif not is_online:
                    if phase == 2:
                        status_lbl = "Restart u toku"
                    else:
                        status_lbl = "Offline"
                elif d.get("has_errors"):
                    status_lbl = "Upozorenje"

                d_copy = dict(d)
                d_copy["is_online"] = is_online
                d_copy["last_seen_sec_ago"] = sec_ago
                d_copy["status_label"] = status_lbl
                d_copy["has_screen_stream"] = (k in active_streams) or (d.get("device_id") in active_streams)

                # Propagacija i osiguranje senzorskih polja za svaki uređaj koji ih ima
                sensor_fields = ("cpu_percent", "cpu_temp", "ram_used", "ram_total", "ram_percent", "smart")
                for field in sensor_fields:
                    val = d.get(field)
                    if val in (None, ""):
                        tab_data = d.get("tab_data")
                        if isinstance(tab_data, dict) and tab_data.get(field) not in (None, ""):
                            val = tab_data.get(field)
                        elif isinstance(d.get("latest_report"), dict) and d.get("latest_report").get(field) not in (None, ""):
                            val = d.get("latest_report").get(field)
                        elif isinstance(d.get("sensors"), dict) and d.get("sensors").get(field) not in (None, ""):
                            val = d.get("sensors").get(field)

                    if val not in (None, ""):
                        if field == "smart":
                            if isinstance(val, dict):
                                d_copy["smart"] = val
                        else:
                            d_copy[field] = val
                    elif field in d_copy:
                        del d_copy[field]

                out[k] = d_copy
        return out

    def get_sessions_summary(self) -> dict:
        """Vraća listu aktivnih sesija sa proračunatom starošću."""
        now = time.time()
        sessions_list = []
        sessions_dict = {}
        with self.session_lock:
            for sid, s in self.fleet_sessions.items():
                sec_ago = int(now - s.get("last_seen", now))
                is_stale = (sec_ago > 30)
                s_copy = dict(s)
                s_copy["last_seen_sec_ago"] = sec_ago
                s_copy["is_stale"] = is_stale
                if is_stale:
                    s_copy["status"] = "STALE"
                    s_copy["status_label"] = "STALE"
                sessions_dict[sid] = s_copy
                sessions_list.append(s_copy)
        return {"sessions": sessions_list, "items": sessions_dict, "total": len(sessions_list)}

    def get_device_logs(self, dev_id: str, limit: int = 300) -> list:
        """Dohvata logove za dati uređaj iz memorije ili sa diska."""
        with self.fleet_lock:
            logs = self.fleet_logs.get(dev_id, [])
            if not logs and self.live_logs_dir and os.path.isdir(self.live_logs_dir):
                for fn in os.listdir(self.live_logs_dir):
                    if fn.startswith(f"servis_deploy_{sanitize_id(dev_id)}_") and fn.endswith(".log"):
                        try:
                            with open(os.path.join(self.live_logs_dir, fn), "r", encoding="utf-8", errors="ignore") as f:
                                logs = [l.rstrip("\r\n") for l in f]
                                self.fleet_logs[dev_id] = list(logs)
                            break
                        except Exception:
                            pass
            return list(logs[-limit:]) if logs else []

    def _get_associated_device_keys(self, device_id: str) -> list[str]:
        """
        Vraća listu svih povezanih ključeva/identifikatora za dati uređaj
        (npr. sanitizovan ID, originalni ID, alias iz fleet_devices).
        """
        if not device_id:
            return []
        keys = [device_id, sanitize_id(device_id)]
        with self.fleet_lock:
            for k in list(keys):
                if k in self.fleet_devices:
                    d = self.fleet_devices[k]
                    if d.get("device_id"):
                        keys.append(d["device_id"])
                        keys.append(sanitize_id(d["device_id"]))
                    if d.get("computer_name"):
                        keys.append(d["computer_name"])
                        keys.append(sanitize_id(d["computer_name"]))
            for k, d in self.fleet_devices.items():
                if (
                    d.get("device_id") == device_id
                    or d.get("computer_name") == device_id
                    or sanitize_id(d.get("device_id", "")) == sanitize_id(device_id)
                ):
                    keys.append(k)
                    if d.get("device_id"):
                        keys.append(d["device_id"])
                    if d.get("computer_name"):
                        keys.append(d["computer_name"])
        seen = set()
        res = []
        for k in keys:
            if k and k not in seen:
                seen.add(k)
                res.append(k)
        return res

    def enqueue_command(self, device_id: str, action: str, params: dict = None) -> dict:
        """
        Kreira komandu sa command_id (npr. cmd_<uuid>), action, params (default {}), created_at.
        Dodaje je u red za dati device_id (FIFO, maks. 20 komandi po uređaju radi zaštite memorije).
        Vraća rečnik komande.
        """
        if not device_id:
            raise ValueError("device_id ne sme biti prazan")
        if not action or not isinstance(action, str) or not action.strip():
            raise ValueError("action mora biti neprazan string")

        clean_params = dict(params) if isinstance(params, dict) else {}
        cmd_id = f"cmd_{uuid.uuid4().hex}"
        cmd_dict = {
            "command_id": cmd_id,
            "device_id": str(device_id),
            "action": action.strip(),
            "params": clean_params,
            "created_at": time.time(),
        }

        # Primarni ključ za smeštanje u red
        associated = self._get_associated_device_keys(device_id)
        dev_key = associated[0] if associated else sanitize_id(device_id)

        with self.command_lock:
            if dev_key not in self.fleet_commands:
                self.fleet_commands[dev_key] = []
            queue = self.fleet_commands[dev_key]
            queue.append(cmd_dict)
            if len(queue) > 20:
                self.fleet_commands[dev_key] = queue[-20:]

        return dict(cmd_dict)

    def pop_pending_commands(self, device_id: str) -> list[dict]:
        """
        Vraća listu beklogovanih komandi za uređaj i prazni red za taj uređaj.
        """
        if not device_id:
            return []
        keys = self._get_associated_device_keys(device_id)
        with self.command_lock:
            cmds = []
            seen_ids = set()
            for k in keys:
                if k in self.fleet_commands:
                    for c in self.fleet_commands[k]:
                        cid = c.get("command_id")
                        if cid and cid in seen_ids:
                            continue
                        if cid:
                            seen_ids.add(cid)
                        cmds.append(dict(c))
                    self.fleet_commands[k] = []
            return cmds

    def get_pending_commands(self, device_id: str) -> list[dict]:
        """
        Samo pregled komandi bez pražnjenja.
        """
        if not device_id:
            return []
        keys = self._get_associated_device_keys(device_id)
        with self.command_lock:
            cmds = []
            seen_ids = set()
            for k in keys:
                for c in self.fleet_commands.get(k, []):
                    cid = c.get("command_id")
                    if cid and cid in seen_ids:
                        continue
                    if cid:
                        seen_ids.add(cid)
                    cmds.append(dict(c))
            return cmds

    def clear_commands(self, device_id: str) -> None:
        """
        Prazni komandni red za uređaj.
        """
        if not device_id:
            return
        keys = self._get_associated_device_keys(device_id)
        with self.command_lock:
            for k in keys:
                if k in self.fleet_commands:
                    self.fleet_commands[k] = []


# Globalni singlton
FLEET_STORE = FleetStore()
