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
import shutil
import threading
from collections import deque
from datetime import datetime
from servishub.drivers.cache import sanitize_id
from servishub.reports import (
    diagnostics_contract,
    compute_handover_status,
    _atomic_write_json,
    cleanup_stale_tmp_files,
    is_enospc_error,
    is_partial_hwtest_payload,
    log_enospc_rate_limited,
    merge_app_statuses,
    merge_report_payloads,
)


_HW_PLACEHOLDER_VALUES = {
    "",
    "n/a",
    "na",
    "unknown",
    "unknown model",
    "unknown serial",
    "unknown-pc",
    "none",
    "null",
    "pc",
    "standardni pc",
    "genericpc",
    "nepoznat model",
    "nepoznato",
    "nepoznat serijski",
    "nepoznat serijski broj",
    "system product name",
    "system serial number",
    "chassis serial number",
    "default string",
    "to be filled by o.e.m.",
    "not specified",
    "not available",
    "ucitavanje...",
    "učitavanje...",
    "ucitavanje",
    "učitavanje",
    "ucitavanje konfiguracije...",
    "učitavanje konfiguracije...",
    "ucitavanje konfiguracije",
    "učitavanje konfiguracije",
    "detekcija...",
    "detekcija",
    "loading...",
    "loading",
    "loading configuration...",
    "loading configuration",
    "detecting...",
    "detecting",
    "reading...",
    "reading",
    "加载中...",
    "加载中",
    "读取中...",
    "读取中",
    "正在加载...",
    "正在加载",
    "正在加载配置...",
    "正在加载配置",
    "检测中...",
    "检测中",
    "正在检测...",
    "正在检测",
    "未知",
    "未知型号",
    "未知序列号",
    "无",
    "未连接",
}

_VALID_SERIAL_RE = re.compile(r"^[A-Za-z0-9][A-Za-z0-9._-]{1,63}$")


def is_placeholder_hw_value(value) -> bool:
    """Proverava da li je hardverska vrednost prazan string ili lokalizovani UI placeholder (sr/en/zh-CN)."""
    if value is None or isinstance(value, bool):
        return True
    s = str(value).strip()
    if not s:
        return True
    low = s.lower()
    if low in _HW_PLACEHOLDER_VALUES:
        return True
    if low.startswith((
        "učitavanje",
        "ucitavanje",
        "detekcija",
        "loading",
        "detecting",
        "reading",
        "加载中",
        "读取中",
        "正在加载",
        "检测中",
        "正在检测",
        "未知",
    )):
        return True
    if low.endswith(("...", "…")):
        return True
    return False


def is_valid_canonical_serial(value) -> bool:
    """Proverava da li je vrednost validan alfanumerički serijski broj / UUID a ne UI placeholder."""
    if is_placeholder_hw_value(value):
        return False
    s = str(value).strip()
    return bool(_VALID_SERIAL_RE.fullmatch(s))


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
        live_log_max_files: int = 200,
        low_disk_prune_threshold_gb: float = 15.0,
    ):
        self.live_logs_dir = live_logs_dir
        self.reports_dir = reports_dir

        self.max_screen_frames_bytes = int(max_screen_frames_bytes)
        self.screen_frame_ttl_sec = float(screen_frame_ttl_sec)
        self.live_log_retention_days = int(live_log_retention_days)
        self.max_sse_subscribers_total = int(max_sse_subscribers_total)
        self.max_sse_subscribers_per_device = int(max_sse_subscribers_per_device)
        self.live_log_max_files = int(live_log_max_files)
        self.low_disk_prune_threshold_gb = float(low_disk_prune_threshold_gb)

        self.fleet_lock = threading.Lock()
        self.fleet_devices = {}      # device_id -> dict
        self.fleet_logs = {}         # device_id -> list of log lines
        self.fleet_subscribers = {}  # device_id -> list of (sub_queue, sub_lock)
        self._serial_index = {}      # UPPER_SERIAL -> canonical_device_key
        self._alias_index = {}       # UPPER_SANITIZED_NAME -> canonical_device_key
        self._last_log_prune_ts = 0.0
        self._enospc_lock = threading.Lock()
        self._last_enospc_log_ts = 0.0

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
        if self.live_logs_dir and os.path.isdir(self.live_logs_dir):
            self.load_today_live_logs_tail(max_lines_per_device=50)

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
        """Izdvaja kanonski serijski broj (ili SMBIOS UUID fallback) ako nije placeholder i ako je validan SN."""
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
            if is_valid_canonical_serial(cand):
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
        if source_key in self.fleet_logs:
            old_logs = self.fleet_logs.pop(source_key, [])
            if old_logs:
                cur_logs = self.fleet_logs.setdefault(target_key, [])
                self.fleet_logs[target_key] = (old_logs + cur_logs)[-5000:]
        self._alias_index[source_key.upper()] = target_key
        if src.get("computer_name"):
            self._alias_index[sanitize_id(str(src["computer_name"])).upper()] = target_key

    _TERMINAL_SESSION_FIELDS = (
        "status",
        "deployment_status",
        "handover_status",
        "handover_ready",
        "duration_seconds",
        "duration_formatted",
        "duration",
        "latest_report",
        "latest_report_file",
        "latest_report_md_file",
        "hwtest",
        "apps",
        "apps_summary",
        "errors",
        "has_errors",
        "completed_at",
        "diagnostics_status",
        "diagnostics_summary",
        "cooling_verified",
        "manual_check_required",
        "max_cpu_temp_c",
        "max_cpu_temp",
        "emergency_abort",
        "notes",
        "thermal_note",
        "benchmark",
        "_last_report_saved_ts",
        "_has_in_memory_report",
        "worker_progress",
    )

    @staticmethod
    def _is_terminal_device_record(rec: dict) -> bool:
        if not isinstance(rec, dict):
            return False
        if rec.get("phase") in (6, "6"):
            return True
        if rec.get("status_label") in ("Zavrseno", "Završeno", "NEEDS_ATTENTION", "Servisiran (AGY)", "Kvar (AGY)"):
            return True
        if bool(rec.get("latest_report")):
            return True
        return False

    def _reset_terminal_session_fields_locked(self, rec: dict) -> None:
        """Briše završna polja prethodne sesije iz in-memory zapisa pri novom startu (ne dira Logs/reports na disku)."""
        if not isinstance(rec, dict):
            return
        for field in self._TERMINAL_SESSION_FIELDS:
            rec.pop(field, None)

    def _handle_install_session_change_locked(
        self,
        dev_id: str,
        incoming_session_id: str,
        comp_name: str = "",
    ) -> bool:
        """
        IMP-69: Kada se za isti SN/uređaj promeni `install_session_id` (nova instalacija),
        čisti in-memory logove i stare alias/foreign-host tragove prethodne sesije.
        Vraća True ako je detektovana promena sesije i izvršeno čišćenje.
        """
        inc_sid = str(incoming_session_id or "").strip()
        if not inc_sid or not dev_id:
            return False

        rec = self.fleet_devices.get(dev_id)
        if not isinstance(rec, dict):
            return False

        prev_sid = str(rec.get("install_session_id") or "").strip()
        if not prev_sid:
            rec["install_session_id"] = inc_sid
            return False
        if prev_sid == inc_sid:
            return False

        # Nova instalacija za isti SN/uređaj: očisti stare in-memory logove i alias tragove
        self.fleet_logs[dev_id] = []
        raw_upper = sanitize_id(comp_name).upper() if comp_name else ""
        stale_aliases = [
            ak for ak, target in self._alias_index.items()
            if target == dev_id and (not raw_upper or ak != raw_upper)
        ]
        for ak in stale_aliases:
            self._alias_index.pop(ak, None)
            self.fleet_logs.pop(ak, None)

        self._reset_terminal_session_fields_locked(rec)
        for stale_field in ("foreign_hosts", "errors", "notes"):
            rec.pop(stale_field, None)
        rec["install_session_id"] = inc_sid
        rec["_session_logs_cleared"] = True
        return True

    def _resolve_device_key_locked(self, comp_name: str, canonical_sn: str = "", previous_hostname: str = "") -> str:
        """
        Određuje jedinstveni kanonski ključ u fleet_devices tako da promena hostname-a
        (npr. DESKTOP-* / W-AAD-* -> TK-LAPTOP-<SN>) nikada ne stvara duplikate za isti serijski broj
        ili previous_hostname, a placeholder vrednosti nikada ne zagađuju _serial_index / _alias_index (IMP-45, IMP-72).
        Pozvati pod fleet_lock.
        """
        raw_key = sanitize_id(comp_name)
        raw_upper = raw_key.upper()
        is_tk_incoming = raw_upper.startswith("TK-")
        is_comp_placeholder = is_placeholder_hw_value(comp_name) or raw_upper in ("", "UNKNOWN", "UNKNOWN-PC")

        if canonical_sn and not is_valid_canonical_serial(canonical_sn):
            canonical_sn = ""
        elif canonical_sn:
            canonical_sn = str(canonical_sn).strip().upper()

        prev_clean = ""
        prev_upper = ""
        if previous_hostname and not is_placeholder_hw_value(previous_hostname):
            prev_clean = sanitize_id(previous_hostname)
            if prev_clean and prev_clean.upper() not in ("", "UNKNOWN", "UNKNOWN-PC"):
                prev_upper = prev_clean.upper()
            else:
                prev_clean = ""

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

        if not existing_key and prev_upper:
            prev_candidates = []
            aliased = self._alias_index.get(prev_upper)
            if aliased and aliased in self.fleet_devices:
                prev_candidates.append(aliased)
            if prev_clean in self.fleet_devices:
                prev_candidates.append(prev_clean)
            for k, rec in self.fleet_devices.items():
                if isinstance(rec, dict) and sanitize_id(str(rec.get("computer_name") or "")).upper() == prev_upper:
                    prev_candidates.append(k)
            for cand_k in prev_candidates:
                cand_rec = self.fleet_devices.get(cand_k)
                if not isinstance(cand_rec, dict):
                    continue
                cand_sn = self._extract_canonical_serial(cand_rec, cand_rec.get("hw"))
                if cand_sn and canonical_sn and cand_sn != canonical_sn:
                    continue
                existing_key = cand_k
                break

        if existing_key:
            if existing_key != raw_key:
                if is_tk_incoming and not str(existing_key).upper().startswith("TK-"):
                    self._merge_device_records_into_locked(raw_key, existing_key)
                    if canonical_sn:
                        self._serial_index[canonical_sn] = raw_key
                    if not is_comp_placeholder:
                        self._alias_index[raw_upper] = raw_key
                    if prev_upper:
                        self._alias_index[prev_upper] = raw_key
                    return raw_key
                if not is_comp_placeholder:
                    self._alias_index[raw_upper] = existing_key
                if prev_upper:
                    self._alias_index[prev_upper] = existing_key
                if canonical_sn:
                    self._serial_index[canonical_sn] = existing_key
                return existing_key
            if canonical_sn:
                self._serial_index[canonical_sn] = existing_key
            if not is_comp_placeholder:
                self._alias_index[raw_upper] = existing_key
            if prev_upper:
                self._alias_index[prev_upper] = existing_key
            return existing_key

        alias_key = self._alias_index.get(raw_upper) if not is_comp_placeholder else None
        if alias_key and alias_key in self.fleet_devices:
            if canonical_sn:
                alias_rec = self.fleet_devices.get(alias_key)
                alias_sn = (
                    self._extract_canonical_serial(alias_rec, alias_rec.get("hw"))
                    if isinstance(alias_rec, dict)
                    else ""
                )
                # IMP-45: Ako alias_key ukazuje na drugi uređaj (drugačiji SN ili tuđi zapis kad ovaj uređaj ima svoj SN),
                # očisti zatrovani alias i veži raw_upper na sopstveni raw_key!
                if (alias_sn and alias_sn != canonical_sn) or (
                    alias_key != raw_key and not str(alias_key).upper().startswith("TK-")
                ):
                    self._serial_index[canonical_sn] = raw_key
                    self._alias_index[raw_upper] = raw_key
                    if prev_upper:
                        self._alias_index[prev_upper] = raw_key
                    return raw_key
                self._serial_index[canonical_sn] = alias_key
                self._alias_index[raw_upper] = alias_key
                if prev_upper:
                    self._alias_index[prev_upper] = alias_key
            return alias_key

        if canonical_sn:
            self._serial_index[canonical_sn] = raw_key
            if not is_comp_placeholder:
                self._alias_index[raw_upper] = raw_key
            if prev_upper:
                self._alias_index[prev_upper] = raw_key
        elif not is_comp_placeholder and raw_upper not in self._alias_index:
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

    def load_today_live_logs_tail(self, max_lines_per_device: int = 50, max_devices: int = 200) -> int:
        """
        IMP-56: Pri startu servisa učitava rep postojećih današnjih `logs/live/*.log`
        fajlova (podrazumevano poslednjih 50 linija po uređaju), strogo bez praćenja symlinkova.
        """
        if not self.live_logs_dir or os.path.islink(self.live_logs_dir) or not os.path.isdir(self.live_logs_dir):
            return 0
        real_live_dir = os.path.realpath(self.live_logs_dir)
        today_str = datetime.now().strftime("%Y%m%d")
        prefix = "servis_deploy_"
        suffix = f"_{today_str}.log"
        loaded_devices = 0

        try:
            with os.scandir(real_live_dir) as it:
                entries = sorted(it, key=lambda e: e.name)
        except OSError:
            return 0

        for entry in entries:
            if max_devices > 0 and loaded_devices >= max_devices:
                break
            try:
                fname = entry.name
                if not (fname.startswith(prefix) and fname.endswith(suffix)):
                    continue
                if ".." in fname or "/" in fname or "\\" in fname:
                    continue
                if entry.is_symlink() or os.path.islink(entry.path) or not entry.is_file(follow_symlinks=False):
                    continue
                real_fpath = os.path.realpath(entry.path)
                if os.path.commonpath([real_live_dir, real_fpath]) != real_live_dir:
                    continue
                raw_dev = fname[len(prefix):-len(suffix)]
                if not raw_dev:
                    continue
                with open(real_fpath, "r", encoding="utf-8", errors="ignore") as f:
                    tail = deque((line.rstrip("\r\n") for line in f if line.strip()), maxlen=max(1, int(max_lines_per_device)))
                tail_lines = list(tail)
                if not tail_lines:
                    continue
                with self.fleet_lock:
                    target_key = self._resolve_device_key_locked(raw_dev)
                    if not self.fleet_logs.get(target_key):
                        self.fleet_logs[target_key] = tail_lines
                loaded_devices += 1
            except (OSError, ValueError):
                continue

        return loaded_devices

    def _log_disk_error_rate_limited(self, prefix: str, err: Exception, interval_sec: float = 60.0) -> bool:
        """Ispisuje disk grešku uz rate-limit od 1x na 60s za ENOSPC (IMP-36)."""
        now = time.time()
        if is_enospc_error(err):
            with self._enospc_lock:
                if self._last_enospc_log_ts > 0.0 and (now - self._last_enospc_log_ts) < float(interval_sec):
                    return False
                self._last_enospc_log_ts = now
            print(f"[ERROR] {prefix}: {err} (ENOSPC rate-limited 1x/{int(interval_sec)}s)", file=sys.stderr)
            return True
        print(f"[ERROR] {prefix}: {err}", file=sys.stderr)
        return True

    def _get_protected_active_log_prefixes(self, now: float) -> set[str]:
        """Vraća skup sanitizovanih prefiksa uređaja aktivnih u zadnja 24h (koji se ne smeju brisati pri retenciji)."""
        protected = set()
        cutoff_24h = now - 86400.0
        with self.fleet_lock:
            for dev_k, rec in self.fleet_devices.items():
                if not isinstance(rec, dict):
                    continue
                last_seen = float(rec.get("last_seen", 0.0) or 0.0)
                if last_seen >= cutoff_24h and not self._is_terminal_device_record(rec):
                    protected.add(sanitize_id(str(dev_k)))
                    if rec.get("computer_name"):
                        protected.add(sanitize_id(str(rec["computer_name"])))
                    if rec.get("device_id"):
                        protected.add(sanitize_id(str(rec["device_id"])))
        return {p for p in protected if p}

    def _prune_old_disk_logs(self, now: float = None, force: bool = False) -> dict:
        """
        Bezbedna automatska retencija live logova i bajatih .tmp fajlova (IMP-36):
        - Briše .tmp fajlove starije od 1h u live_logs_dir i reports_dir.
        - Briše servis_deploy_*.log fajlove starije od live_log_retention_days.
        - Kada je slobodan prostor < low_disk_prune_threshold_gb (15 GB) ili broj fajlova > live_log_max_files,
          briše najstarije logove starije od 24h bez diranja trenutno aktivnih uređaja iz zadnja 24h.
        - Strogo bez praćenja symlinkova i bez path traversal-a.
        """
        cur_ts = float(now) if now is not None else time.time()
        stats = {"removed_logs": 0, "removed_tmp": 0}
        if not force and (cur_ts - self._last_log_prune_ts) < 3600.0:
            return stats
        self._last_log_prune_ts = cur_ts

        if self.reports_dir and os.path.isdir(self.reports_dir) and not os.path.islink(self.reports_dir):
            stats["removed_tmp"] += cleanup_stale_tmp_files(self.reports_dir, max_age_sec=3600.0, now=cur_ts)
        if self.live_logs_dir and os.path.isdir(self.live_logs_dir) and not os.path.islink(self.live_logs_dir):
            stats["removed_tmp"] += cleanup_stale_tmp_files(self.live_logs_dir, max_age_sec=3600.0, now=cur_ts)

        if self.live_log_retention_days <= 0 or not self.live_logs_dir or not os.path.isdir(self.live_logs_dir) or os.path.islink(self.live_logs_dir):
            return stats

        real_live_dir = os.path.realpath(self.live_logs_dir)
        cutoff = cur_ts - (self.live_log_retention_days * 86400.0)
        cutoff_24h = cur_ts - 86400.0
        protected_prefixes = self._get_protected_active_log_prefixes(cur_ts)

        low_disk = False
        if self.low_disk_prune_threshold_gb > 0:
            try:
                du = shutil.disk_usage(real_live_dir)
                free_gb = du.free / (1024.0 ** 3)
                if free_gb < self.low_disk_prune_threshold_gb:
                    low_disk = True
            except OSError:
                pass

        remaining_candidates = []
        try:
            with os.scandir(self.live_logs_dir) as it:
                entries = list(it)
        except OSError:
            return stats

        for entry in entries:
            try:
                fname = entry.name
                if not (fname.startswith("servis_deploy_") and fname.endswith(".log")):
                    continue
                if ".." in fname or "/" in fname or "\\" in fname:
                    continue
                if entry.is_symlink() or not entry.is_file(follow_symlinks=False):
                    continue
                fpath = entry.path
                if os.path.islink(fpath):
                    continue
                real_fpath = os.path.realpath(fpath)
                if os.path.commonpath([real_live_dir, real_fpath]) != real_live_dir:
                    continue
                st = entry.stat(follow_symlinks=False)
                mtime = float(st.st_mtime)

                is_protected = mtime >= cutoff_24h
                if not is_protected and protected_prefixes:
                    for pref in protected_prefixes:
                        if fname.startswith(f"servis_deploy_{pref}_"):
                            is_protected = True
                            break

                if mtime < cutoff and not is_protected:
                    os.remove(fpath)
                    stats["removed_logs"] += 1
                else:
                    remaining_candidates.append((mtime, fpath, is_protected))
            except (OSError, ValueError):
                continue

        # Dodatno čišćenje pri niskom prostoru na disku (< 15 GB) ili prekoračenju maksimalnog broja log fajlova
        max_files = self.live_log_max_files if self.live_log_max_files > 0 else 200
        if low_disk:
            max_files = min(max_files, 50)

        if len(remaining_candidates) > max_files or low_disk:
            remaining_candidates.sort(key=lambda item: item[0])
            total_rem = len(remaining_candidates)
            for mtime, fpath, is_protected in remaining_candidates:
                if is_protected:
                    continue
                if not low_disk and total_rem <= max_files:
                    break
                try:
                    if not os.path.islink(fpath):
                        os.remove(fpath)
                        stats["removed_logs"] += 1
                        total_rem -= 1
                except OSError:
                    continue

        return stats

    def append_to_disk_log(self, computer_name: str, lines: list):
        """Zapisuje nove linije loga u trajni lokalni fajl na disku uz retenciju starih logova i ENOSPC zaštitu."""
        if not self.live_logs_dir:
            return
        try:
            os.makedirs(self.live_logs_dir, exist_ok=True)
            real_live_dir = os.path.realpath(self.live_logs_dir)
            sanitized = sanitize_id(computer_name)
            date_str = datetime.now().strftime("%Y%m%d")
            log_file = os.path.join(self.live_logs_dir, f"servis_deploy_{sanitized}_{date_str}.log")
            if os.path.islink(log_file) or os.path.commonpath([real_live_dir, os.path.abspath(log_file)]) != real_live_dir:
                return
            with open(log_file, "a", encoding="utf-8") as f:
                for l in lines:
                    f.write(l.strip() + "\n")
            self._prune_old_disk_logs(time.time())
        except Exception as e:
            if is_enospc_error(e):
                self._prune_old_disk_logs(time.time(), force=True)
            self._log_disk_error_rate_limited("Neuspeh upisa loga na disk", e)

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
        prev_host = str(payload.get("previous_hostname") or "").strip()
        install_sid = str(payload.get("install_session_id") or "").strip()
        if "hw" in clean_update:
            del clean_update["hw"]

        with self.fleet_lock:
            dev_id = self._resolve_device_key_locked(comp_name, canonical_sn=canonical_sn, previous_hostname=prev_host)
            if dev_id not in self.fleet_devices:
                self.fleet_devices[dev_id] = {
                    "computer_name": comp_name,
                    "device_id": payload.get("device_id") or dev_id,
                }
                print(f"[*] [NOVI RAČUNAR] Detektovan u servisu: {comp_name} ({client_ip})")
            existing_rec = self.fleet_devices[dev_id]
            if install_sid:
                self._handle_install_session_change_locked(dev_id, install_sid, comp_name=comp_name)

            incoming_phase_raw = payload.get("phase")
            try:
                incoming_phase = (
                    int(incoming_phase_raw)
                    if incoming_phase_raw is not None and not isinstance(incoming_phase_raw, bool)
                    else None
                )
            except (ValueError, TypeError):
                incoming_phase = None
            incoming_lbl = str(payload.get("status_label") or "").strip()
            if (
                self._is_terminal_device_record(existing_rec)
                and incoming_phase is not None
                and 1 <= incoming_phase < 6
                and incoming_lbl not in ("Zavrseno", "Završeno", "Servisiran (AGY)", "Kvar (AGY)")
            ):
                preserved_p4_apps = (
                    dict(existing_rec["apps"])
                    if incoming_phase >= 5 and isinstance(existing_rec.get("apps"), dict) and existing_rec.get("apps")
                    else None
                )
                self._reset_terminal_session_fields_locked(existing_rec)
                if preserved_p4_apps:
                    existing_rec["apps"] = preserved_p4_apps
                existing_rec["status_label"] = "Aktivno"
                if "status_label" not in clean_update or clean_update.get("status_label") in ("Zavrseno", "Završeno"):
                    clean_update["status_label"] = "Aktivno"

            # IMP-52: Zaštita od pregaza verifikovanih Faza 4 statusa aplikacija (OK/WARN/ERROR)
            # dolaznim WAIT/SKIPPED vrednostima nakon resume-a u Fazi 5+
            eff_phase = incoming_phase
            if eff_phase is None:
                try:
                    eff_phase = int(existing_rec.get("phase")) if existing_rec.get("phase") is not None else None
                except (ValueError, TypeError):
                    eff_phase = None
            if (
                isinstance(clean_update.get("apps"), dict)
                and isinstance(existing_rec.get("apps"), dict)
                and existing_rec["apps"]
            ):
                clean_update["apps"] = merge_app_statuses(existing_rec["apps"], clean_update["apps"])
                if eff_phase is not None and eff_phase >= 5:
                    _wait_skip_tokens = ("", "WAIT", "WAITING", "PENDING", "SKIP", "SKIPPED", "FALSE")
                    existing_has_real_status = any(
                        str(v or "").strip().upper() not in _wait_skip_tokens
                        for v in existing_rec["apps"].values()
                    )
                    incoming_only_wait_or_skip = all(
                        str(v or "").strip().upper() in _wait_skip_tokens
                        for v in (payload.get("apps") or {}).values()
                    ) if isinstance(payload.get("apps"), dict) else False
                    if existing_has_real_status and incoming_only_wait_or_skip and "apps_summary" in clean_update and existing_rec.get("apps_summary"):
                        del clean_update["apps_summary"]

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

            eff_sn = canonical_sn or self._extract_canonical_serial(existing_rec, existing_rec.get("hw"))
            if (
                str(dev_id).upper().startswith("TK-")
                or str(comp_name).upper().startswith("TK-")
                or bool(eff_sn)
                or bool(client_ip)
            ):
                self.cleanup_duplicate_devices_locked(
                    dev_id,
                    comp_name=comp_name,
                    serial_num=eff_sn,
                    client_ip=client_ip,
                    previous_hostname=prev_host,
                )

        return dev_id

    def _collect_related_records_locked(
        self,
        dev_id: str,
        comp_name: str = "",
        serial_num: str = "",
        previous_hostname: str = "",
    ) -> list:
        """
        Vraća povezane zapise iz fleet_devices (po dev_id, sanitize_id(comp_name),
        serial_number, previous_hostname ili podudarnom computer_name) za spajanje memorisane telemetrije.
        Poziva se pod self.fleet_lock.
        """
        related = []
        seen_keys = set()
        candidates = [dev_id]
        if comp_name:
            candidates.append(sanitize_id(comp_name))
        if previous_hostname and not is_placeholder_hw_value(previous_hostname):
            prev_s = sanitize_id(previous_hostname)
            candidates.append(prev_s)
            if prev_s.upper() in self._alias_index:
                candidates.append(self._alias_index[prev_s.upper()])
        if serial_num and is_valid_canonical_serial(serial_num):
            sn_u = str(serial_num).strip().upper()
            if sn_u in self._serial_index:
                candidates.append(self._serial_index[sn_u])
        for cid in candidates:
            if cid and cid not in seen_keys and cid in self.fleet_devices:
                seen_keys.add(cid)
                related.append(self.fleet_devices[cid])
        target_cn = str(comp_name).strip().upper() if comp_name else ""
        target_prev = str(previous_hostname).strip().upper() if previous_hostname and not is_placeholder_hw_value(previous_hostname) else ""
        target_sn = str(serial_num).strip().upper() if serial_num and is_valid_canonical_serial(serial_num) else ""
        if target_cn or target_prev or target_sn:
            for k, rec in self.fleet_devices.items():
                if k in seen_keys or not isinstance(rec, dict):
                    continue
                rcn = str(rec.get("computer_name") or "").strip().upper()
                rsn = self._extract_canonical_serial(rec, rec.get("hw"))
                if (
                    (target_cn and rcn == target_cn)
                    or (target_prev and (rcn == target_prev or sanitize_id(k).upper() == sanitize_id(target_prev).upper()))
                    or (target_sn and rsn and rsn == target_sn)
                ):
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
        canonical_sn = self._extract_canonical_serial(enriched, incoming_hw)
        prev_host = str(enriched.get("previous_hostname") or "").strip()

        with self.fleet_lock:
            related = self._collect_related_records_locked(
                dev_id,
                effective_comp,
                serial_num=canonical_sn,
                previous_hostname=prev_host,
            )
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

        if mem_apps:
            enriched["apps"] = merge_app_statuses(mem_apps, enriched.get("apps"))

        if (not enriched.get("autopilot")) and mem_autopilot:
            enriched["autopilot"] = mem_autopilot

        if (not enriched.get("hwtest")) and mem_hwtest:
            enriched["hwtest"] = mem_hwtest

        if (not enriched.get("benchmark")) and mem_benchmark:
            enriched["benchmark"] = mem_benchmark

        if not str(enriched.get("install_session_id") or "").strip():
            with self.fleet_lock:
                for rec in self._collect_related_records_locked(
                    dev_id,
                    effective_comp,
                    serial_num=canonical_sn,
                    previous_hostname=prev_host,
                ):
                    if isinstance(rec, dict) and str(rec.get("install_session_id") or "").strip():
                        enriched["install_session_id"] = str(rec["install_session_id"]).strip()
                        break

        return enriched

    def cleanup_duplicate_devices_locked(
        self,
        canonical_id: str,
        comp_name: str = "",
        serial_num: str = "",
        client_ip: str = "",
        previous_hostname: str = "",
    ) -> None:
        """
        Uklanja privremeni pred-OEM / siroče zapis (npr. DESKTOP-* / W-AAD-* u Fazi 1/2+)
        kada stigne zapis za isti računar / serijski broj / previous_hostname / client_ip (pozvati pod fleet_lock).
        """
        if not canonical_id:
            return
        canon_rec = self.fleet_devices.get(canonical_id)
        if not isinstance(canon_rec, dict):
            return
        target_cn = str(comp_name or canon_rec.get("computer_name") or "").strip().upper()
        target_prev = str(previous_hostname or canon_rec.get("previous_hostname") or "").strip().upper()
        if is_placeholder_hw_value(target_prev):
            target_prev = ""
        target_prev_san = sanitize_id(target_prev).upper() if target_prev else ""
        target_sn = str(
            serial_num
            or canon_rec.get("serial_number")
            or self._extract_canonical_serial(canon_rec, canon_rec.get("hw"))
            or ""
        ).strip().upper()
        if is_placeholder_hw_value(target_sn):
            target_sn = ""
        target_ip = str(client_ip or canon_rec.get("client_ip") or "").strip()
        is_canon_tk = str(canonical_id).upper().startswith("TK-") or target_cn.startswith("TK-")

        to_remove = []
        for k, rec in list(self.fleet_devices.items()):
            if k == canonical_id or str(k).upper().startswith("TK-") or not isinstance(rec, dict):
                continue
            rcn = str(rec.get("computer_name") or "").strip().upper()
            rprev = str(rec.get("previous_hostname") or "").strip().upper()
            rsn = self._extract_canonical_serial(rec, rec.get("hw"))
            rip = str(rec.get("client_ip") or "").strip()
            rec_phase = rec.get("phase")
            is_phase_1_2 = rec_phase in (1, 2, "1", "2")

            sn_match = bool(target_sn and rsn and rsn == target_sn)
            cn_match = bool(is_canon_tk and target_cn and rcn == target_cn)
            prev_host_match = bool(
                target_prev
                and not (target_sn and rsn and rsn != target_sn)
                and (
                    rcn == target_prev
                    or sanitize_id(k).upper() == target_prev_san
                    or (rprev and rprev == target_prev)
                )
            )
            ip_match = False
            if (
                target_ip
                and rip
                and rip == target_ip
                and is_phase_1_2
                and not self._is_terminal_device_record(rec)
                and not (target_sn and rsn and rsn != target_sn)
            ):
                if (
                    target_ip not in ("127.0.0.1", "::1", "localhost")
                    or is_canon_tk
                    or str(k).upper().startswith(("DESKTOP-", "WIN-", "APCL-", "W-AAD-", "UNKNOWN"))
                ):
                    ip_match = True

            if sn_match or cn_match or prev_host_match or ip_match:
                to_remove.append(k)

        for k in to_remove:
            self._merge_device_records_into_locked(canonical_id, k)

    def record_logs(
        self,
        comp_name: str,
        lines: list,
        serial_number: str = None,
        install_session_id: str = None,
        previous_hostname: str = None,
    ) -> int:
        """Prihvata novu grupu linija loga, baferuje u memoriji, osvežava last_seen i obaveštava pretplatnike."""
        if not lines:
            return 0
        raw_id = sanitize_id(comp_name)
        canonical_sn = str(serial_number).strip().upper() if is_valid_canonical_serial(serial_number) else ""
        prev_host = str(previous_hostname or "").strip()
        install_sid = str(install_session_id or "").strip()

        with self.fleet_lock:
            dev_id = self._resolve_device_key_locked(comp_name, canonical_sn=canonical_sn, previous_hostname=prev_host)
            if dev_id in self.fleet_devices:
                rec = self.fleet_devices[dev_id]
                if install_sid:
                    self._handle_install_session_change_locked(dev_id, install_sid, comp_name=comp_name)
                if isinstance(rec, dict) and not self._is_terminal_device_record(rec):
                    rec["last_seen"] = time.time()
                    if canonical_sn and is_placeholder_hw_value(rec.get("serial_number")):
                        rec["serial_number"] = canonical_sn
            if dev_id not in self.fleet_logs:
                self.fleet_logs[dev_id] = []
            self.fleet_logs[dev_id].extend(lines)
            if dev_id in self.fleet_devices and isinstance(self.fleet_devices[dev_id], dict):
                self.fleet_devices[dev_id].pop("_session_logs_cleared", None)
            if len(self.fleet_logs[dev_id]) > 5000:
                self.fleet_logs[dev_id] = self.fleet_logs[dev_id][-5000:]

            notified_subs = set()
            for sub_key in (dev_id, raw_id):
                if sub_key and sub_key in self.fleet_subscribers:
                    for sub_q, sub_lock in self.fleet_subscribers[sub_key]:
                        sub_ident = id(sub_q)
                        if sub_ident in notified_subs:
                            continue
                        notified_subs.add(sub_ident)
                        with sub_lock:
                            sub_q.extend(lines)
                            sub_lock.notify_all()

        self.append_to_disk_log(dev_id or comp_name, lines)
        return len(lines)

    def record_lifecycle_start(self, payload: dict, client_ip: str) -> str:
        """Obrada STARTED događaja životnog ciklusa."""
        if not isinstance(payload, dict):
            payload = {}
        comp_name = payload.get("computer_name") or "UNKNOWN-PC"
        now = time.time()
        incoming_hw = extract_hw_from_payload(payload)
        canonical_sn = self._extract_canonical_serial(payload, incoming_hw)
        prev_host = str(payload.get("previous_hostname") or "").strip()
        install_sid = str(payload.get("install_session_id") or "").strip()

        with self.fleet_lock:
            dev_id = self._resolve_device_key_locked(comp_name, canonical_sn=canonical_sn, previous_hostname=prev_host)
            if dev_id not in self.fleet_devices:
                self.fleet_devices[dev_id] = {}
            rec = self.fleet_devices[dev_id]
            if install_sid:
                self._handle_install_session_change_locked(dev_id, install_sid, comp_name=comp_name)
            if self._is_terminal_device_record(rec):
                self._reset_terminal_session_fields_locked(rec)

            effective_comp_name = (
                rec.get("computer_name") or dev_id
                if str(dev_id).upper().startswith("TK-")
                and not str(comp_name).upper().startswith("TK-")
                and str(rec.get("computer_name", "")).upper().startswith("TK-")
                else comp_name
            )
            existing_hw = rec.get("hw") if isinstance(rec.get("hw"), dict) else {}
            merged_hw = merge_hw_dicts(existing_hw, incoming_hw)
            rec.update({
                "computer_name": effective_comp_name,
                "status_label": "Aktivno",
                "phase": 1,
                "autopilot": payload.get("autopilot", {}),
                "hw": merged_hw,
                "started_at": payload.get("timestamp", datetime.now().isoformat()),
                "last_seen": now,
                "client_ip": client_ip,
            })
            if is_placeholder_hw_value(rec.get("serial_number")) and not is_placeholder_hw_value(merged_hw.get("serial")):
                rec["serial_number"] = merged_hw["serial"]
            sensors = self._extract_telemetry_sensors(payload)
            if sensors:
                rec.update(sensors)

            eff_sn = canonical_sn or self._extract_canonical_serial(rec, rec.get("hw"))
            if (
                str(dev_id).upper().startswith("TK-")
                or str(comp_name).upper().startswith("TK-")
                or bool(eff_sn)
                or bool(client_ip)
            ):
                self.cleanup_duplicate_devices_locked(
                    dev_id,
                    comp_name=effective_comp_name,
                    serial_num=eff_sn,
                    client_ip=client_ip,
                    previous_hostname=prev_host,
                )

        return dev_id

    @staticmethod
    def _report_completeness(rep: dict) -> tuple:
        if not isinstance(rep, dict):
            return (0, 0, 0, 0)
        has_hwtest = 1 if isinstance(rep.get("hwtest"), dict) and rep.get("hwtest") else 0
        has_bm = 1 if isinstance(rep.get("benchmark"), dict) and rep.get("benchmark") else 0
        apps_val = rep.get("apps")
        has_apps = 1 if (isinstance(apps_val, dict) and len(apps_val) > 0) or (isinstance(apps_val, list) and len(apps_val) > 0) else 0
        dur = _parse_duration_seconds(rep.get("duration_seconds") or rep.get("duration"))
        return (has_hwtest + has_bm + has_apps, has_hwtest, has_bm, dur)

    @staticmethod
    def _merge_reports_in_memory(existing_rep: dict, incoming_rep: dict) -> dict:
        return merge_report_payloads(existing_rep, incoming_rep)

    def record_lifecycle_complete(self, payload: dict, client_ip: str) -> str:
        """Obrada COMPLETED događaja životnog ciklusa i atomično arhiviranje kartona."""
        if not isinstance(payload, dict):
            payload = {}
        comp_name = payload.get("computer_name") or "UNKNOWN-PC"
        incoming_hw = extract_hw_from_payload(payload)
        canonical_sn = self._extract_canonical_serial(payload, incoming_hw)
        prev_host = str(payload.get("previous_hostname") or "").strip()

        with self.fleet_lock:
            dev_id = self._resolve_device_key_locked(comp_name, canonical_sn=canonical_sn, previous_hostname=prev_host)

        # IMP-94b: Parcijalni HwTest payload iz Faze 5 ne sme kreirati ili pregaziti završni karton!
        if is_partial_hwtest_payload(payload):
            with self.fleet_lock:
                if dev_id not in self.fleet_devices:
                    self.fleet_devices[dev_id] = {"computer_name": comp_name, "client_ip": client_ip}
                rec = self.fleet_devices[dev_id]
                if isinstance(payload.get("hwtest"), dict) and payload["hwtest"]:
                    rec["hwtest"] = payload["hwtest"]
                if payload.get("manual_driver_bypass") is True:
                    rec["manual_driver_bypass"] = True
                if payload.get("driver_gate_emergency_override_reason"):
                    rec["driver_gate_emergency_override_reason"] = str(payload["driver_gate_emergency_override_reason"]).strip()
                if incoming_hw:
                    existing_hw = rec.get("hw") if isinstance(rec.get("hw"), dict) else {}
                    rec["hw"] = merge_hw_dicts(existing_hw, incoming_hw)
                if canonical_sn and is_placeholder_hw_value(rec.get("serial_number")):
                    rec["serial_number"] = canonical_sn
                rec["last_seen"] = time.time()
                self.cleanup_duplicate_devices_locked(
                    dev_id,
                    comp_name=comp_name,
                    serial_num=canonical_sn,
                    client_ip=client_ip,
                    previous_hostname=prev_host,
                )
            return dev_id

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

        now_ts = time.time()
        skip_disk_write = False

        with self.fleet_lock:
            if dev_id not in self.fleet_devices:
                self.fleet_devices[dev_id] = {"computer_name": comp_name, "client_ip": client_ip}
            rec = self.fleet_devices[dev_id]
            install_sid = str(enriched_payload.get("install_session_id") or "").strip()
            if install_sid:
                self._handle_install_session_change_locked(dev_id, install_sid, comp_name=comp_name)

            existing_rep = rec.get("latest_report")
            last_saved_ts = float(rec.get("_last_report_saved_ts") or 0.0)
            if last_saved_ts <= 0.0 and isinstance(existing_rep, dict) and existing_rep:
                rep_file = rec.get("latest_report_file")
                if rep_file and os.path.isfile(rep_file):
                    try:
                        last_saved_ts = os.path.getmtime(rep_file)
                    except OSError:
                        last_saved_ts = 0.0

            if (
                isinstance(existing_rep, dict)
                and existing_rep
                and last_saved_ts > 0.0
                and (now_ts - last_saved_ts) <= 45.0
            ):
                ex_comp = self._report_completeness(existing_rep)
                in_comp = self._report_completeness(enriched_payload)
                if ex_comp[0] > 0 and ex_comp[1] >= in_comp[1] and ex_comp[2] >= in_comp[2] and ex_comp[0] >= in_comp[0]:
                    skip_disk_write = True
                    enriched_payload = self._merge_reports_in_memory(existing_rep, enriched_payload)

            effective_comp_name = (
                rec.get("computer_name") or dev_id
                if str(dev_id).upper().startswith("TK-")
                and not str(comp_name).upper().startswith("TK-")
                and str(rec.get("computer_name", "")).upper().startswith("TK-")
                else comp_name
            )
            apps = enriched_payload.get("apps", {})
            system_summary = enriched_payload.get("system_summary", {})
            merged_hw = enriched_payload.get("hw", {}) if isinstance(enriched_payload.get("hw"), dict) else {}
            duration_seconds = enriched_payload.get("duration_seconds", duration_seconds)
            duration_formatted = enriched_payload.get("duration_formatted", duration_formatted)

            rec.update({
                "computer_name": effective_comp_name,
                "status_label": "Zavrseno",
                "phase": 6,
                "worker_progress": 100,
                "duration_formatted": duration_formatted,
                "duration_seconds": duration_seconds,
                "apps": apps,
                "system_summary": system_summary,
                "completed_at": enriched_payload.get("timestamp") or rec.get("completed_at") or datetime.now().isoformat(),
                "last_seen": now_ts,
            })
            rec.update(diagnostics_contract(enriched_payload))
            if rec["diagnostics_status"] == "THERMAL_CRITICAL":
                rec["status_label"] = "NEEDS_ATTENTION"
            if "autopilot" in enriched_payload:
                rec["autopilot"] = enriched_payload["autopilot"]
            if "benchmark" in enriched_payload and isinstance(enriched_payload["benchmark"], dict):
                rec["benchmark"] = enriched_payload["benchmark"]
            if "hwtest" in enriched_payload and isinstance(enriched_payload["hwtest"], dict):
                rec["hwtest"] = enriched_payload["hwtest"]
            if merged_hw:
                rec["hw"] = merged_hw
            if not is_placeholder_hw_value(enriched_payload.get("serial_number")):
                rec["serial_number"] = enriched_payload["serial_number"]
            if not is_placeholder_hw_value(enriched_payload.get("chassis")):
                rec["chassis"] = enriched_payload["chassis"]
            sensors = self._extract_telemetry_sensors(enriched_payload)
            if sensors:
                rec.update(sensors)
            if skip_disk_write:
                rec["latest_report"] = enriched_payload
            self.cleanup_duplicate_devices_locked(
                dev_id,
                comp_name=effective_comp_name,
                serial_num=enriched_payload.get("serial_number", ""),
                client_ip=client_ip,
                previous_hostname=prev_host,
            )

        if self.reports_dir and not skip_disk_write:
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
                    self.fleet_devices[dev_id]["_last_report_saved_ts"] = time.time()
            except Exception as e:
                if is_enospc_error(e):
                    self._prune_old_disk_logs(time.time(), force=True)
                self._log_disk_error_rate_limited("Neuspeh čuvanja servisnog kartona na disk", e)
        elif not self.reports_dir and not skip_disk_write:
            with self.fleet_lock:
                self.fleet_devices[dev_id]["latest_report"] = enriched_payload
                self.fleet_devices[dev_id]["_has_in_memory_report"] = True
                self.fleet_devices[dev_id]["_last_report_saved_ts"] = time.time()

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

        incoming_hw = extract_hw_from_payload(payload)
        canonical_sn = self._extract_canonical_serial(payload, incoming_hw)
        prev_host = str(payload.get("previous_hostname") or "").strip()

        session_record = {
            "session_id": session_id,
            "computer_name": comp_name,
            "previous_hostname": prev_host,
            "serial_number": canonical_sn,
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

        prev_host_upper = prev_host.upper() if prev_host and not is_placeholder_hw_value(prev_host) else ""
        with self.session_lock:
            stale_cutoff = now - 86400.0
            for old_sid in [
                sid for sid, srec in self.fleet_sessions.items()
                if sid != session_id and (now - float(srec.get("last_seen", now) or now)) > 86400.0
            ]:
                self.fleet_sessions.pop(old_sid, None)

            # IMP-72: Spoji aktivnu sesiju istog uređaja po serial_number ili previous_hostname (W-AAD-* -> TK-LAPTOP-<SN>)
            if canonical_sn or prev_host_upper:
                dup_sids = []
                for old_sid, srec in list(self.fleet_sessions.items()):
                    if old_sid == session_id or not isinstance(srec, dict):
                        continue
                    old_sn = str(srec.get("serial_number") or "").strip().upper()
                    old_cn = str(srec.get("computer_name") or "").strip().upper()
                    if canonical_sn and old_sn and old_sn != canonical_sn:
                        continue
                    sn_hit = bool(canonical_sn and old_sn and old_sn == canonical_sn)
                    prev_hit = bool(prev_host_upper and (old_cn == prev_host_upper or str(old_sid).strip().upper() == prev_host_upper))
                    if sn_hit or prev_hit:
                        dup_sids.append(old_sid)
                for old_sid in dup_sids:
                    self.fleet_sessions.pop(old_sid, None)
                    old_subs = self.session_subscribers.pop(old_sid, None)
                    if old_subs:
                        self.session_subscribers.setdefault(session_id, []).extend(old_subs)

            self.fleet_sessions[session_id] = session_record
            if session_id in self.session_subscribers:
                for sub_q, sub_lock in self.session_subscribers[session_id]:
                    with sub_lock:
                        sub_q.append(dict(session_record))
                        sub_lock.notify_all()

        with self.fleet_lock:
            dev_id = self._resolve_device_key_locked(comp_name, canonical_sn=canonical_sn, previous_hostname=prev_host)
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
            if canonical_sn and is_placeholder_hw_value(self.fleet_devices[dev_id].get("serial_number")):
                self.fleet_devices[dev_id]["serial_number"] = canonical_sn
            if prev_host:
                self.fleet_devices[dev_id]["previous_hostname"] = prev_host
            if canonical_sn or prev_host:
                self.cleanup_duplicate_devices_locked(
                    dev_id,
                    comp_name=effective_comp_name,
                    serial_num=canonical_sn,
                    client_ip=client_ip,
                    previous_hostname=prev_host,
                )
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
                    is_interim_reboot = (
                        (phase in (2, "2"))
                        or (
                            phase in (3, "3")
                            and (
                                bool(d.get("interim_reboot"))
                                or bool(d.get("reboot_pending"))
                                or "restart" in reboot_text
                                or "reboot" in reboot_text
                            )
                        )
                        or (
                            phase in (5, "5")
                            and (
                                bool(d.get("interim_reboot"))
                                or bool(d.get("reboot_pending"))
                                or "uefi" in reboot_text
                                or "restartujem" in reboot_text
                                or "restart" in reboot_text
                                or "reboot" in reboot_text
                            )
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
            rec = self.fleet_devices.get(dev_id)
            logs_cleared_for_new_session = isinstance(rec, dict) and bool(rec.get("_session_logs_cleared"))
            if not logs and not logs_cleared_for_new_session and self.live_logs_dir and os.path.isdir(self.live_logs_dir):
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
