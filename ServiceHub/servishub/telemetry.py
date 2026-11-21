"""
servishub.telemetry
===================
Thread-safe skladište za telemetriju flote računara, aktivne sesije,
live logove i SSE pretplatnike.
"""

import os
import re
import sys
import time
import json
import uuid
import threading
from datetime import datetime
from servishub.drivers.cache import sanitize_id
from servishub.reports import diagnostics_contract, compute_handover_status, _atomic_write_json


_HW_PLACEHOLDER_VALUES = {
    "",
    "n/a",
    "na",
    "unknown",
    "none",
    "null",
    "pc",
    "standardni pc",
    "genericpc",
    "nepoznat model",
    "nepoznato",
    "nepoznat serijski",
    "system product name",
    "default string",
    "to be filled by o.e.m.",
    "ucitavanje...",
    "učitavanje...",
    "ucitavanje konfiguracije...",
    "učitavanje konfiguracije...",
    "detekcija...",
}


def is_placeholder_hw_value(value) -> bool:
    """Proverava da li je hardverska vrednost prazan string ili UI placeholder."""
    if value is None or isinstance(value, bool):
        return True
    s = str(value).strip()
    if not s:
        return True
    low = s.lower()
    if low in _HW_PLACEHOLDER_VALUES:
        return True
    if low.startswith(("učitavanje", "ucitavanje", "detekcija")):
        return True
    return False


def extract_hw_from_payload(payload: dict) -> dict:
    """Izdvaja i normalizuje hardverski blok iz različitih formata telemetrije."""
    if not isinstance(payload, dict):
        return {}
    hw = {}
    for src_key in ("hardware", "hw"):
        src = payload.get(src_key)
        if isinstance(src, dict):
            for k, v in src.items():
                if v is not None and str(v).strip() != "":
                    hw[k] = str(v).strip() if isinstance(v, str) else v

    tab_data = payload.get("tab_data")
    if isinstance(tab_data, dict) and isinstance(tab_data.get("hw"), dict):
        for k, v in tab_data["hw"].items():
            if v is not None and str(v).strip() != "" and is_placeholder_hw_value(hw.get(k)):
                hw[k] = str(v).strip() if isinstance(v, str) else v

    alias_map = (
        ("manufacturer", ("manufacturer", "oem")),
        ("model", ("model", "device_model", "model_info")),
        ("serial", ("serial", "serial_number", "device_serial")),
    )
    for target_key, candidates in alias_map:
        if is_placeholder_hw_value(hw.get(target_key)):
            for cand in candidates:
                val = payload.get(cand)
                if not is_placeholder_hw_value(val):
                    hw[target_key] = str(val).strip()
                    break

    return hw


def merge_hw_dicts(existing_hw: dict, incoming_hw: dict) -> dict:
    """
    Spaja postojeći i dolazni `hw` rečnik tako da placeholderi ili prazne
    vrednosti nikada ne pregaze već poznate validne hardverske podatke.
    """
    merged = {}
    if isinstance(existing_hw, dict):
        for k, v in existing_hw.items():
            if v is not None and str(v).strip() != "":
                merged[k] = v
    if isinstance(incoming_hw, dict):
        for k, v in incoming_hw.items():
            if v is None or str(v).strip() == "":
                continue
            cur = merged.get(k)
            if is_placeholder_hw_value(cur) or not is_placeholder_hw_value(v):
                merged[k] = v
    return merged


def _normalize_apps_summary(value):
    """Normalizuje apps_summary ako sadrzi ne-latinski/kineski tekst u standardni srpski zapis."""
    if not isinstance(value, str):
        return value
    s = value.strip()
    m = re.match(r"已安装\s*(\d+)\s*/\s*(\d+)\s*[•·]\s*进行中\s*(\d+)\s*[•·]\s*已跳过\s*(\d+)\s*[•·]\s*错误\s*(\d+)", s)
    if m:
        inst, tot, in_prog, skip, err = m.groups()
        return f"{inst} od {tot} instalirano • {in_prog} u toku • {skip} preskočeno • {err} grešaka"
    return value


def _parse_duration_seconds(val) -> int:
    """Parsira trajanje iz sekundi ili 'HH:MM:SS' / 'Xm Ys' formata u cele sekunde."""
    if val is None or isinstance(val, bool):
        return 0
    if isinstance(val, (int, float)):
        return max(0, int(val))
    raw = str(val).strip()
    if not raw:
        return 0
    if raw.isdigit():
        return int(raw)
    m_hms = re.fullmatch(r"(\d{1,3}):(\d{1,2}):(\d{1,2})", raw)
    if m_hms:
        return int(m_hms.group(1)) * 3600 + int(m_hms.group(2)) * 60 + int(m_hms.group(3))
    m_ms = re.fullmatch(r"(\d{1,3}):(\d{1,2})", raw)
    if m_ms:
        return int(m_ms.group(1)) * 60 + int(m_ms.group(2))
    total = 0
    matched = False
    for pat, mult in ((r"(\d+)\s*h", 3600), (r"(\d+)\s*m", 60), (r"(\d+)\s*s", 1)):
        m_u = re.search(pat, raw, re.IGNORECASE)
        if m_u:
            total += int(m_u.group(1)) * mult
            matched = True
    return total if matched else 0


class FleetStore:
    """
    Centralno in-memory skladište za telemetriju, sesije i logove radioničke flote.
    """

    def __init__(
        self,
        live_logs_dir: str = None,
        reports_dir: str = None,
        max_screen_frames_bytes: int = 256 * 1024 * 1024,
        screen_frame_ttl_sec: float = 60.0,
        live_log_retention_days: int = 30,
        max_sse_subscribers_total: int = 64,
        max_sse_subscribers_per_device: int = 8,
    ):
        self.live_logs_dir = live_logs_dir
        self.reports_dir = reports_dir

        self.max_screen_frames_bytes = int(max_screen_frames_bytes)
        self.screen_frame_ttl_sec = float(screen_frame_ttl_sec)
        self.live_log_retention_days = int(live_log_retention_days)
        self.max_sse_subscribers_total = int(max_sse_subscribers_total)
        self.max_sse_subscribers_per_device = int(max_sse_subscribers_per_device)

        self.fleet_lock = threading.Lock()
        self.fleet_devices = {}      # device_id -> dict
        self.fleet_logs = {}         # device_id -> list of log lines
        self.fleet_subscribers = {}  # device_id -> list of (sub_queue, sub_lock)
        self._serial_index = {}      # UPPER_SERIAL -> canonical_device_key
        self._alias_index = {}       # UPPER_SANITIZED_NAME -> canonical_device_key
        self._last_log_prune_ts = 0.0

        self.session_lock = threading.Lock()
        self.fleet_sessions = {}      # session_id -> dict
        self.session_subscribers = {} # session_id -> list of (sub_queue, sub_lock)

        # In-memory thread-safe bafer za frejmove ekrana (1 Hz screen mirror)
        self.screen_lock = threading.Lock()
        self.screen_frames = {}       # device_id -> {"frame": bytes, "size": int, "timestamp": float}

        # Thread-safe komandni red za daljinsko upravljanje (BIOS reboot, PXE, abort itd.)
        self.command_lock = threading.Lock()
        self.fleet_commands = {}      # device_id -> list of command dicts

        # Brojač aktivnih SSE pretplatnika za zaštitu memorije i niti
        self._sse_lock = threading.Lock()
        self._sse_total = 0
        self._sse_per_device = {}

        if self.reports_dir and os.path.isdir(self.reports_dir):
            self.load_historical_devices()

    def acquire_sse_slot(self, channel_key: str) -> bool:
        """Rezerviše SSE slot uz globalni i per-device limit."""
        key = str(channel_key or "global")
        with self._sse_lock:
            if self._sse_total >= self.max_sse_subscribers_total:
                return False
            per_cnt = self._sse_per_device.get(key, 0)
            if per_cnt >= self.max_sse_subscribers_per_device:
                return False
            self._sse_total += 1
            self._sse_per_device[key] = per_cnt + 1
            return True

    def release_sse_slot(self, channel_key: str) -> None:
        """Oslobađa prethodno rezervisani SSE slot."""
        key = str(channel_key or "global")
        with self._sse_lock:
            if self._sse_total > 0:
                self._sse_total -= 1
            cur = self._sse_per_device.get(key, 0)
            if cur <= 1:
                self._sse_per_device.pop(key, None)
            else:
                self._sse_per_device[key] = cur - 1

    def _extract_canonical_serial(self, payload: dict, hw_dict: dict = None) -> str:
        """Izdvaja kanonski serijski broj (ili SMBIOS UUID fallback) ako nije placeholder."""
        candidates = []
        if isinstance(payload, dict):
            candidates.extend([
                payload.get("serial_number"),
                payload.get("serial"),
                payload.get("device_serial"),
                payload.get("smbios_uuid"),
            ])
        if isinstance(hw_dict, dict):
            candidates.extend([
                hw_dict.get("serial"),
                hw_dict.get("serial_number"),
                hw_dict.get("smbios_uuid"),
            ])
        for cand in candidates:
            if not is_placeholder_hw_value(cand):
                return str(cand).strip().upper()
        return ""

    def _merge_device_records_into_locked(self, target_key: str, source_key: str) -> None:
        """Spaja podatke starog zapisa u kanonski zapis i uklanja stari ključ (pozvati pod fleet_lock)."""
        if target_key == source_key or source_key not in self.fleet_devices:
            return
        src = self.fleet_devices.pop(source_key, None)
        if not isinstance(src, dict):
            return
        dst = self.fleet_devices.setdefault(target_key, {})
        if not dst.get("started_at") and src.get("started_at"):
            dst["started_at"] = src["started_at"]
        if not dst.get("autopilot") and isinstance(src.get("autopilot"), dict) and src["autopilot"]:
            dst["autopilot"] = dict(src["autopilot"])
        src_hw = src.get("hw") if isinstance(src.get("hw"), dict) else {}
        dst_hw = dst.get("hw") if isinstance(dst.get("hw"), dict) else {}
        merged_hw = merge_hw_dicts(src_hw, dst_hw)
        if merged_hw:
            dst["hw"] = merged_hw
        if is_placeholder_hw_value(dst.get("serial_number")) and not is_placeholder_hw_value(src.get("serial_number")):
            dst["serial_number"] = src["serial_number"]
        if not dst.get("hwtest") and isinstance(src.get("hwtest"), dict) and src["hwtest"]:
            dst["hwtest"] = src["hwtest"]
        if not dst.get("benchmark") and isinstance(src.get("benchmark"), dict) and src["benchmark"]:
            dst["benchmark"] = src["benchmark"]
        self._alias_index[source_key.upper()] = target_key
        if src.get("computer_name"):
            self._alias_index[sanitize_id(str(src["computer_name"])).upper()] = target_key

    def _resolve_device_key_locked(self, comp_name: str, canonical_sn: str = "") -> str:
        """
        Određuje jedinstveni kanonski ključ u fleet_devices tako da promena hostname-a
        (npr. DESKTOP-* -> TK-LAPTOP-<SN>) nikada ne stvara duplikate za isti serijski broj.
        Pozvati pod fleet_lock.
        """
        raw_key = sanitize_id(comp_name)
        raw_upper = raw_key.upper()
        is_tk_incoming = raw_upper.startswith("TK-")

        existing_key = None
        if canonical_sn:
            idx_key = self._serial_index.get(canonical_sn)
            if idx_key and idx_key in self.fleet_devices:
                existing_key = idx_key
            else:
                for k, rec in self.fleet_devices.items():
                    if not isinstance(rec, dict):
                        continue
                    rec_sn = self._extract_canonical_serial(rec, rec.get("hw"))
                    if rec_sn and rec_sn == canonical_sn:
                        existing_key = k
                        break

        if existing_key:
            if existing_key != raw_key:
                if is_tk_incoming and not str(existing_key).upper().startswith("TK-"):
                    self._merge_device_records_into_locked(raw_key, existing_key)
                    self._serial_index[canonical_sn] = raw_key
                    self._alias_index[raw_upper] = raw_key
                    return raw_key
                self._alias_index[raw_upper] = existing_key
                self._serial_index[canonical_sn] = existing_key
                return existing_key
            self._serial_index[canonical_sn] = existing_key
            return existing_key

        alias_key = self._alias_index.get(raw_upper)
        if alias_key and alias_key in self.fleet_devices:
            if canonical_sn:
                self._serial_index[canonical_sn] = alias_key
            return alias_key

        if canonical_sn:
            self._serial_index[canonical_sn] = raw_key
        self._alias_index[raw_upper] = raw_key
        return raw_key

    def _consolidate_historical_duplicates_locked(self) -> None:
        """Jednokratno spaja istorijske duplikate sa istim serijskim brojem u korist TK-* zapisa."""
        by_serial = {}
        for k, rec in list(self.fleet_devices.items()):
            if not isinstance(rec, dict):
                continue
            sn = self._extract_canonical_serial(rec, rec.get("hw"))
            if not sn:
                continue
            by_serial.setdefault(sn, []).append(k)

        for sn, keys in by_serial.items():
            if len(keys) <= 1:
                self._serial_index[sn] = keys[0]
                continue
            keys_sorted = sorted(
                keys,
                key=lambda k: (
                    1 if str(k).upper().startswith("TK-") else 0,
                    float(self.fleet_devices.get(k, {}).get("last_seen", 0.0) or 0.0),
                ),
                reverse=True,
            )
            winner = keys_sorted[0]
            for loser in keys_sorted[1:]:
                self._merge_device_records_into_locked(winner, loser)
            self._serial_index[sn] = winner

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

            hw_data = extract_hw_from_payload(data)
            serial_val = data.get("serial_number") or data.get("serial", "")
            if is_placeholder_hw_value(serial_val) and not is_placeholder_hw_value(hw_data.get("serial")):
                serial_val = hw_data.get("serial", "")

            dur_sec = data.get("duration_seconds", 0)
            dur_fmt = data.get("duration_formatted", "N/A")
            if (not dur_sec or dur_sec == 0) and data.get("duration"):
                parsed_dur = _parse_duration_seconds(data.get("duration"))
                if parsed_dur > 0:
                    dur_sec = parsed_dur
                    if not dur_fmt or dur_fmt == "N/A":
                        dur_fmt = f"{dur_sec // 60}m {dur_sec % 60:02d}s"

            record = {
                "computer_name": comp_name,
                "device_id": data.get("device_id") or dev_id,
                "serial_number": serial_val,
                "chassis": data.get("chassis", ""),
                "status": status_raw,
                "status_label": status_label,
                "phase": 5,
                "worker_progress": 100,
                "duration_formatted": dur_fmt,
                "duration_seconds": dur_sec,
                "apps": data.get("apps", {}),
                "system_summary": data.get("system_summary", {}),
                "hw": hw_data,
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

        with self.fleet_lock:
            self._consolidate_historical_duplicates_locked()

    def _prune_old_disk_logs(self, now: float) -> None:
        """Briše diskovne live logove starije od live_log_retention_days (najviše jednom na sat)."""
        if self.live_log_retention_days <= 0 or not self.live_logs_dir or not os.path.isdir(self.live_logs_dir):
            return
        if (now - self._last_log_prune_ts) < 3600.0:
            return
        self._last_log_prune_ts = now
        cutoff = now - (self.live_log_retention_days * 86400.0)
        try:
            for fname in os.listdir(self.live_logs_dir):
                if not (fname.startswith("servis_deploy_") and fname.endswith(".log")):
                    continue
                fpath = os.path.join(self.live_logs_dir, fname)
                if os.path.isfile(fpath):
                    try:
                        if os.path.getmtime(fpath) < cutoff:
                            os.remove(fpath)
                    except OSError:
                        pass
        except OSError:
            pass

    def append_to_disk_log(self, computer_name: str, lines: list):
        """Zapisuje nove linije loga u trajni lokalni fajl na disku uz retenciju starih logova."""
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
            self._prune_old_disk_logs(time.time())
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

        incoming_hw = extract_hw_from_payload(payload)
        canonical_sn = self._extract_canonical_serial(payload, incoming_hw)
        if "hw" in clean_update:
            del clean_update["hw"]

        with self.fleet_lock:
            dev_id = self._resolve_device_key_locked(comp_name, canonical_sn=canonical_sn)
            if dev_id not in self.fleet_devices:
                self.fleet_devices[dev_id] = {
                    "computer_name": comp_name,
                    "device_id": payload.get("device_id") or dev_id,
                }
                print(f"[*] [NOVI RAČUNAR] Detektovan u servisu: {comp_name} ({client_ip})")
            existing_rec = self.fleet_devices[dev_id]
            if not str(dev_id).upper().startswith("TK-") and str(existing_rec.get("computer_name", "")).upper().startswith("TK-"):
                clean_update["computer_name"] = existing_rec["computer_name"]
            elif str(dev_id).upper().startswith("TK-") and not str(comp_name).upper().startswith("TK-"):
                # Ne prepisuj kanonsko TK-* ime zaostalim DESKTOP-* heartbeat-om
                clean_update["computer_name"] = existing_rec.get("computer_name") or dev_id
            existing_hw = existing_rec.get("hw") if isinstance(existing_rec.get("hw"), dict) else {}
            merged_hw = merge_hw_dicts(existing_hw, incoming_hw)
            existing_rec.update(clean_update)
            if merged_hw:
                existing_rec["hw"] = merged_hw
                if is_placeholder_hw_value(existing_rec.get("serial_number")) and not is_placeholder_hw_value(merged_hw.get("serial")):
                    existing_rec["serial_number"] = merged_hw["serial"]

        return dev_id

    def _collect_related_records_locked(self, dev_id: str, comp_name: str = "") -> list:
        """
        Vraća povezane zapise iz fleet_devices (po dev_id, sanitize_id(comp_name)
        ili podudarnom computer_name) za spajanje memorisane telemetrije.
        Poziva se pod self.fleet_lock.
        """
        related = []
        seen_keys = set()
        candidates = [dev_id]
        if comp_name:
            candidates.append(sanitize_id(comp_name))
        for cid in candidates:
            if cid and cid not in seen_keys and cid in self.fleet_devices:
                seen_keys.add(cid)
                related.append(self.fleet_devices[cid])
        if comp_name:
            target_cn = str(comp_name).strip().upper()
            for k, rec in self.fleet_devices.items():
                if k in seen_keys or not isinstance(rec, dict):
                    continue
                if str(rec.get("computer_name") or "").strip().upper() == target_cn:
                    seen_keys.add(k)
                    related.append(rec)
        return related

    def enrich_report_payload(self, dev_id: str, payload: dict, comp_name: str = "") -> dict:
        """
        Spaja završni report payload sa postojećim podacima u memoriji
        (hw, serial_number, chassis, apps, autopilot, hwtest, benchmark) kako se
        podaci iz heartbeat-a/sesije i Faze 5 ne bi izgubili.
        """
        enriched = dict(payload) if isinstance(payload, dict) else {}
        if "apps_summary" in enriched:
            enriched["apps_summary"] = _normalize_apps_summary(enriched.get("apps_summary"))
        effective_comp = comp_name or enriched.get("computer_name") or ""
        incoming_hw = extract_hw_from_payload(enriched)

        with self.fleet_lock:
            related = self._collect_related_records_locked(dev_id, effective_comp)
            merged_hw = {}
            mem_serial = ""
            mem_chassis = ""
            mem_apps = {}
            mem_autopilot = {}
            mem_hwtest = None
            mem_benchmark = None

            for rec in related:
                if not isinstance(rec, dict):
                    continue
                rec_hw = extract_hw_from_payload(rec)
                merged_hw = merge_hw_dicts(merged_hw, rec_hw)
                rep = rec.get("latest_report")
                if isinstance(rep, dict):
                    merged_hw = merge_hw_dicts(merged_hw, extract_hw_from_payload(rep))
                    if mem_hwtest is None and isinstance(rep.get("hwtest"), dict) and rep.get("hwtest"):
                        mem_hwtest = rep.get("hwtest")
                    if mem_benchmark is None and isinstance(rep.get("benchmark"), dict) and rep.get("benchmark"):
                        mem_benchmark = rep.get("benchmark")
                if is_placeholder_hw_value(mem_serial):
                    for s_cand in (rec.get("serial_number"), rec.get("serial"), rec_hw.get("serial")):
                        if not is_placeholder_hw_value(s_cand):
                            mem_serial = str(s_cand).strip()
                            break
                if is_placeholder_hw_value(mem_chassis) and not is_placeholder_hw_value(rec.get("chassis")):
                    mem_chassis = str(rec["chassis"]).strip()
                if not mem_apps and isinstance(rec.get("apps"), dict) and rec["apps"]:
                    mem_apps = dict(rec["apps"])
                if not mem_autopilot and isinstance(rec.get("autopilot"), dict) and rec["autopilot"]:
                    mem_autopilot = dict(rec["autopilot"])
                if mem_hwtest is None and isinstance(rec.get("hwtest"), dict) and rec.get("hwtest"):
                    mem_hwtest = rec.get("hwtest")
                if mem_benchmark is None and isinstance(rec.get("benchmark"), dict) and rec.get("benchmark"):
                    mem_benchmark = rec.get("benchmark")

        merged_hw = merge_hw_dicts(merged_hw, incoming_hw)
        if merged_hw:
            enriched["hw"] = merged_hw

        cur_serial = enriched.get("serial_number") or enriched.get("serial")
        if is_placeholder_hw_value(cur_serial):
            if not is_placeholder_hw_value(merged_hw.get("serial")):
                enriched["serial_number"] = str(merged_hw["serial"]).strip()
            elif not is_placeholder_hw_value(mem_serial):
                enriched["serial_number"] = mem_serial

        if is_placeholder_hw_value(enriched.get("chassis")) and not is_placeholder_hw_value(mem_chassis):
            enriched["chassis"] = mem_chassis

        if (not enriched.get("apps")) and mem_apps:
            enriched["apps"] = mem_apps

        if (not enriched.get("autopilot")) and mem_autopilot:
            enriched["autopilot"] = mem_autopilot

        if (not enriched.get("hwtest")) and mem_hwtest:
            enriched["hwtest"] = mem_hwtest

        if (not enriched.get("benchmark")) and mem_benchmark:
            enriched["benchmark"] = mem_benchmark

        return enriched

    def cleanup_duplicate_devices_locked(self, canonical_id: str, comp_name: str = "", serial_num: str = "") -> None:
        """
        Uklanja privremeni pred-OEM zapis (npr. DESKTOP-* ili APCL-*) kada stigne
        završni zapis za isti računar/serijski broj pod TK-* ID-jem (pozvati pod fleet_lock).
        """
        if not canonical_id or not str(canonical_id).upper().startswith("TK-"):
            return
        canon_rec = self.fleet_devices.get(canonical_id)
        if not isinstance(canon_rec, dict):
            return
        target_cn = str(comp_name or canon_rec.get("computer_name") or "").strip().upper()
        target_sn = str(serial_num or canon_rec.get("serial_number") or "").strip().upper()
        if is_placeholder_hw_value(target_sn):
            target_sn = ""

        to_remove = []
        for k, rec in list(self.fleet_devices.items()):
            if k == canonical_id or str(k).upper().startswith("TK-") or not isinstance(rec, dict):
                continue
            rcn = str(rec.get("computer_name") or "").strip().upper()
            rsn = str(rec.get("serial_number") or "").strip().upper()
            if (target_cn and rcn == target_cn) or (target_sn and rsn == target_sn):
                if not canon_rec.get("started_at") and rec.get("started_at"):
                    canon_rec["started_at"] = rec["started_at"]
                if not canon_rec.get("autopilot") and isinstance(rec.get("autopilot"), dict) and rec["autopilot"]:
                    canon_rec["autopilot"] = dict(rec["autopilot"])
                to_remove.append(k)
        for k in to_remove:
            self.fleet_devices.pop(k, None)

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
        if not isinstance(payload, dict):
            payload = {}
        comp_name = payload.get("computer_name") or "UNKNOWN-PC"
        now = time.time()
        incoming_hw = extract_hw_from_payload(payload)
        canonical_sn = self._extract_canonical_serial(payload, incoming_hw)

        with self.fleet_lock:
            dev_id = self._resolve_device_key_locked(comp_name, canonical_sn=canonical_sn)
            if dev_id not in self.fleet_devices:
                self.fleet_devices[dev_id] = {}
            existing_hw = self.fleet_devices[dev_id].get("hw") if isinstance(self.fleet_devices[dev_id].get("hw"), dict) else {}
            merged_hw = merge_hw_dicts(existing_hw, incoming_hw)
            self.fleet_devices[dev_id].update({
                "computer_name": comp_name,
                "status_label": "Aktivno",
                "phase": 1,
                "autopilot": payload.get("autopilot", {}),
                "hw": merged_hw,
                "started_at": payload.get("timestamp", datetime.now().isoformat()),
                "last_seen": now,
                "client_ip": client_ip,
            })
            if is_placeholder_hw_value(self.fleet_devices[dev_id].get("serial_number")) and not is_placeholder_hw_value(merged_hw.get("serial")):
                self.fleet_devices[dev_id]["serial_number"] = merged_hw["serial"]
            sensors = self._extract_telemetry_sensors(payload)
            if sensors:
                self.fleet_devices[dev_id].update(sensors)

        return dev_id

    def record_lifecycle_complete(self, payload: dict, client_ip: str) -> str:
        """Obrada COMPLETED događaja životnog ciklusa i atomično arhiviranje kartona."""
        if not isinstance(payload, dict):
            payload = {}
        comp_name = payload.get("computer_name") or "UNKNOWN-PC"
        incoming_hw = extract_hw_from_payload(payload)
        canonical_sn = self._extract_canonical_serial(payload, incoming_hw)

        with self.fleet_lock:
            dev_id = self._resolve_device_key_locked(comp_name, canonical_sn=canonical_sn)

        enriched_payload = self.enrich_report_payload(dev_id, payload, comp_name=comp_name)

        duration_formatted = enriched_payload.get("duration_formatted", "N/A")
        duration_seconds = enriched_payload.get("duration_seconds", 0)
        if (not duration_seconds or duration_seconds == 0) and enriched_payload.get("duration"):
            parsed_dur = _parse_duration_seconds(enriched_payload.get("duration"))
            if parsed_dur > 0:
                duration_seconds = parsed_dur
                enriched_payload["duration_seconds"] = duration_seconds
                if not duration_formatted or duration_formatted == "N/A":
                    duration_formatted = f"{duration_seconds // 60}m {duration_seconds % 60:02d}s"
                    enriched_payload["duration_formatted"] = duration_formatted

        apps = enriched_payload.get("apps", {})
        system_summary = enriched_payload.get("system_summary", {})
        merged_hw = enriched_payload.get("hw", {}) if isinstance(enriched_payload.get("hw"), dict) else {}

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
            self.fleet_devices[dev_id].update(diagnostics_contract(enriched_payload))
            if self.fleet_devices[dev_id]["diagnostics_status"] == "THERMAL_CRITICAL":
                self.fleet_devices[dev_id]["status_label"] = "NEEDS_ATTENTION"
            if "autopilot" in enriched_payload:
                self.fleet_devices[dev_id]["autopilot"] = enriched_payload["autopilot"]
            if merged_hw:
                self.fleet_devices[dev_id]["hw"] = merged_hw
            if not is_placeholder_hw_value(enriched_payload.get("serial_number")):
                self.fleet_devices[dev_id]["serial_number"] = enriched_payload["serial_number"]
            if not is_placeholder_hw_value(enriched_payload.get("chassis")):
                self.fleet_devices[dev_id]["chassis"] = enriched_payload["chassis"]
            sensors = self._extract_telemetry_sensors(enriched_payload)
            if sensors:
                self.fleet_devices[dev_id].update(sensors)
            self.cleanup_duplicate_devices_locked(dev_id, comp_name=comp_name, serial_num=enriched_payload.get("serial_number", ""))

        if self.reports_dir:
            target_id = enriched_payload.get("serial_number") or comp_name
            date_str = datetime.now().strftime("%Y%m%d_%H%M%S")
            report_filename = f"report_{sanitize_id(target_id)}_{date_str}.json"
            report_filepath = os.path.join(self.reports_dir, report_filename)
            try:
                os.makedirs(self.reports_dir, exist_ok=True)
                _atomic_write_json(report_filepath, enriched_payload)
                with self.fleet_lock:
                    self.fleet_devices[dev_id]["latest_report_file"] = report_filepath
                    self.fleet_devices[dev_id]["latest_report"] = enriched_payload
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
            stale_cutoff = now - 86400.0
            for old_sid in [
                sid for sid, srec in self.fleet_sessions.items()
                if sid != session_id and (now - float(srec.get("last_seen", now) or now)) > 86400.0
            ]:
                self.fleet_sessions.pop(old_sid, None)
            self.fleet_sessions[session_id] = session_record
            if session_id in self.session_subscribers:
                for sub_q, sub_lock in self.session_subscribers[session_id]:
                    with sub_lock:
                        sub_q.append(dict(session_record))
                        sub_lock.notify_all()

        incoming_hw = extract_hw_from_payload(payload)
        canonical_sn = self._extract_canonical_serial(payload, incoming_hw)
        with self.fleet_lock:
            dev_id = self._resolve_device_key_locked(comp_name, canonical_sn=canonical_sn)
            if dev_id not in self.fleet_devices:
                self.fleet_devices[dev_id] = {"computer_name": comp_name}
            effective_comp_name = (
                self.fleet_devices[dev_id].get("computer_name")
                if str(dev_id).upper().startswith("TK-") and not str(comp_name).upper().startswith("TK-")
                else comp_name
            )
            self.fleet_devices[dev_id].update({
                "computer_name": effective_comp_name,
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
                    existing_hw = self.fleet_devices[dev_id].get("hw") if isinstance(self.fleet_devices[dev_id].get("hw"), dict) else {}
                    self.fleet_devices[dev_id]["hw"] = merge_hw_dicts(existing_hw, tab_data["hw"])
                if "warranty" in tab_data and isinstance(tab_data["warranty"], dict):
                    self.fleet_devices[dev_id]["warranty"] = tab_data["warranty"]
            if "warranty" in payload and isinstance(payload["warranty"], dict):
                self.fleet_devices[dev_id]["warranty"] = payload["warranty"]
            if model_info and not is_placeholder_hw_value(model_info):
                existing_hw = self.fleet_devices[dev_id].get("hw") if isinstance(self.fleet_devices[dev_id].get("hw"), dict) else {}
                self.fleet_devices[dev_id]["hw"] = merge_hw_dicts(existing_hw, {"model": model_info})
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
            # 1. TTL eviction za istekle frejmove (zaštita memorije za dugotrajne sesije)
            if self.screen_frame_ttl_sec > 0:
                for old_k, old_entry in list(self.screen_frames.items()):
                    if old_k != dev_id and (now - float(old_entry.get("timestamp", 0.0) or 0.0)) > self.screen_frame_ttl_sec:
                        self.screen_frames.pop(old_k, None)

            self.screen_frames[dev_id] = frame_data

            # 2. Globalni memorijski limit nad svim keširanim frejmovima
            if self.max_screen_frames_bytes > 0:
                total_bytes = sum(int(v.get("size", 0) or 0) for v in self.screen_frames.values())
                if total_bytes > self.max_screen_frames_bytes:
                    by_age = sorted(
                        [(k, v) for k, v in self.screen_frames.items() if k != dev_id],
                        key=lambda kv: float(kv[1].get("timestamp", 0.0) or 0.0),
                    )
                    for evict_k, evict_v in by_age:
                        if total_bytes <= self.max_screen_frames_bytes:
                            break
                        self.screen_frames.pop(evict_k, None)
                        total_bytes -= int(evict_v.get("size", 0) or 0)

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
                    reboot_text = " ".join(
                        str(d.get(fld) or "")
                        for fld in (
                            "current_step", "current_step_title", "step", "step_title",
                            "status_label", "message", "live_status", "sub_phase",
                            "status_message", "phase_name",
                        )
                    ).lower()
                    is_interim_reboot = (phase == 2) or (
                        phase == 3
                        and (
                            bool(d.get("interim_reboot"))
                            or bool(d.get("reboot_pending"))
                            or "restart" in reboot_text
                            or "reboot" in reboot_text
                        )
                    )
                    if is_interim_reboot and sec_ago <= 720:
                        status_lbl = "Restart u toku"
                    else:
                        status_lbl = "Offline"
                elif d.get("has_errors"):
                    status_lbl = "Upozorenje"

                d_copy = dict(d)
                d_copy["is_online"] = is_online
                d_copy["last_seen_sec_ago"] = sec_ago
                d_copy["status_label"] = status_lbl
                if status_lbl in ("Zavrseno", "NEEDS_ATTENTION", "Servisiran (AGY)", "Kvar (AGY)"):
                    d_copy["handover_status"] = compute_handover_status(d)
                else:
                    d_copy["handover_status"] = "IN_PROGRESS"
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
