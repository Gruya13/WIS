"""
servishub.reports
=================
Generisanje profesionalnih Markdown servisnih kartona i perzistencija JSON izveštaja.
"""

import errno
import json
import os
import re
import sys
import threading
import time
from datetime import datetime
from servishub.drivers.cache import sanitize_id

_ENOSPC_LOG_LOCK = threading.Lock()
_LAST_ENOSPC_LOG_TS = 0.0


def is_enospc_error(err: Exception) -> bool:
    """Proverava da li je izuzetak izazvan nedostatkom prostora na disku (Errno 28 / ENOSPC)."""
    if err is None:
        return False
    err_no = getattr(err, "errno", None)
    if err_no in (errno.ENOSPC, 28):
        return True
    msg = str(err).lower()
    return "[errno 28]" in msg or "no space left on device" in msg or "enospc" in msg


def log_enospc_rate_limited(prefix: str, err: Exception, interval_sec: float = 60.0, now: float = None) -> bool:
    """
    Ispisuje disk grešku na stderr uz rate-limit od max 1x na interval_sec (60s) za ENOSPC,
    kako pun disk ne bi zagušio Docker logove (IMP-36).
    Vraća True ako je poruka ispisana, False ako je potisnuta rate-limitom.
    """
    global _LAST_ENOSPC_LOG_TS
    cur_ts = float(now) if now is not None else time.time()
    if is_enospc_error(err):
        with _ENOSPC_LOG_LOCK:
            if _LAST_ENOSPC_LOG_TS > 0.0 and (cur_ts - _LAST_ENOSPC_LOG_TS) < float(interval_sec):
                return False
            _LAST_ENOSPC_LOG_TS = cur_ts
        print(f"[ERROR] {prefix}: {err} (ENOSPC rate-limited 1x/{int(interval_sec)}s)", file=sys.stderr)
        return True
    print(f"[ERROR] {prefix}: {err}", file=sys.stderr)
    return True


def cleanup_stale_tmp_files(directory: str, max_age_sec: float = 3600.0, now: float = None) -> int:
    """
    Briše bajate .tmp fajlove starije od max_age_sec (podrazumevano 1h) u zadatom direktorijumu,
    strogo bez praćenja symlinkova i bez path traversal-a (IMP-36).
    Vraća broj obrisanih .tmp fajlova.
    """
    if not directory or not os.path.isdir(directory) or os.path.islink(directory):
        return 0
    cur_ts = float(now) if now is not None else time.time()
    real_base = os.path.realpath(directory)
    removed = 0
    try:
        with os.scandir(directory) as it:
            entries = list(it)
    except OSError:
        return 0

    for entry in entries:
        try:
            name = entry.name
            if not name.endswith(".tmp") or ".." in name or "/" in name or "\\" in name:
                continue
            if entry.is_symlink() or not entry.is_file(follow_symlinks=False):
                continue
            full_path = entry.path
            if os.path.islink(full_path):
                continue
            real_target = os.path.realpath(full_path)
            if os.path.commonpath([real_base, real_target]) != real_base:
                continue
            st = entry.stat(follow_symlinks=False)
            if (cur_ts - float(st.st_mtime)) >= float(max_age_sec):
                os.remove(full_path)
                removed += 1
        except (OSError, ValueError):
            continue
    return removed


def _atomic_write_text(filepath: str, content: str) -> None:
    """Atomično zapisuje tekstualni sadržaj na disk preko .tmp + os.replace."""
    parent = os.path.dirname(filepath)
    if parent:
        os.makedirs(parent, exist_ok=True)
    tmp_path = f"{filepath}.tmp"
    try:
        if os.path.islink(tmp_path) or os.path.islink(filepath):
            raise OSError(errno.ELOOP, "Odbijen upis preko simboličkog linka", filepath)
        with open(tmp_path, "w", encoding="utf-8") as f:
            f.write(content)
            f.flush()
            os.fsync(f.fileno())
        os.replace(tmp_path, filepath)
    except Exception as ex:
        if os.path.exists(tmp_path) and not os.path.islink(tmp_path):
            try:
                os.remove(tmp_path)
            except OSError:
                pass
        if parent and is_enospc_error(ex):
            cleanup_stale_tmp_files(parent, max_age_sec=3600.0)
            log_enospc_rate_limited("Atomični upis fajla nije uspeo (disk pun)", ex)
        raise


def _atomic_write_json(filepath: str, data: dict) -> None:
    """Atomično zapisuje JSON rečnik na disk preko .tmp + os.replace."""
    _atomic_write_text(filepath, json.dumps(data, indent=2, ensure_ascii=False))


def _format_hw_ram(hw: dict) -> str:
    """Formatira podatak o RAM memoriji uz podršku za numerički ram_gb i tekstualni hw['ram']."""
    if not isinstance(hw, dict):
        return "N/A"
    ram_gb = hw.get("ram_gb")
    if ram_gb is not None and str(ram_gb).strip() not in ("", "0", "None", "N/A"):
        return f"{ram_gb} GB"
    ram_txt = str(hw.get("ram") or "").strip()
    if ram_txt and ram_txt != "N/A":
        return ram_txt
    return "N/A"


def _format_hw_disk(hw: dict) -> str:
    """Formatira podatak o disku uz podršku za disk_model/disk_size_gb i tekstualni hw['disk']."""
    if not isinstance(hw, dict):
        return "N/A"
    model = str(hw.get("disk_model") or "").strip()
    size_gb = hw.get("disk_size_gb")
    size_str = str(size_gb).strip() if size_gb is not None else ""
    if (model and model != "N/A") or (size_str and size_str not in ("0", "None", "N/A")):
        return f"{model or 'N/A'} ({size_str or 'N/A'} GB)"
    disk_txt = str(hw.get("disk") or "").strip()
    if disk_txt and disk_txt != "N/A":
        return disk_txt
    return "N/A"


def _format_hw_display(hw: dict) -> str:
    """Formatira podatak o ekranu (`hw.display`) za servisni karton (V350-04)."""
    if not isinstance(hw, dict):
        return "N/A"
    disp = hw.get("display")
    if isinstance(disp, dict):
        parts = [
            str(disp.get("label") or disp.get("classification") or "").strip(),
            str(disp.get("resolution") or "").strip(),
            str(disp.get("panel") or "").strip(),
        ]
        txt = " ".join(p for p in parts if p and p != "N/A").strip()
        return txt or "N/A"
    disp_txt = str(disp or "").strip()
    if disp_txt and disp_txt.upper() not in ("N/A", "NONE", "NULL", "UNKNOWN"):
        return disp_txt
    return "N/A"


def _parse_semver_tuple(raw_ver) -> tuple[int, int, int] | None:
    """Parsira semantičku verziju ('3.4.3', 'v3.4.2') u (major, minor, patch) ili vraća None."""
    if raw_ver is None or isinstance(raw_ver, bool):
        return None
    s = str(raw_ver).strip()
    if not s:
        return None
    m = re.search(r"(\d+)\.(\d+)(?:\.(\d+))?", s)
    if not m:
        return None
    try:
        major = int(m.group(1))
        minor = int(m.group(2))
        patch = int(m.group(3)) if m.group(3) is not None else 0
        return (major, minor, patch)
    except (ValueError, TypeError):
        return None


def _extract_report_version_tuple(hwtest: dict, report_data: dict = None) -> tuple[int, int, int] | None:
    """Izdvaja verziju klijenta/HwTest-a iz hwtest ili report_data rečnika (IMP-41)."""
    candidates = []
    if isinstance(hwtest, dict):
        candidates.extend([
            hwtest.get("app_version"),
            hwtest.get("version"),
            hwtest.get("wis_version"),
        ])
    if isinstance(report_data, dict):
        candidates.extend([
            report_data.get("wis_version"),
            report_data.get("app_version"),
            report_data.get("version"),
        ])
        nested = report_data.get("latest_report")
        if isinstance(nested, dict):
            candidates.extend([
                nested.get("wis_version"),
                nested.get("app_version"),
                nested.get("version"),
            ])
            nested_hw = nested.get("hwtest")
            if isinstance(nested_hw, dict):
                candidates.extend([
                    nested_hw.get("app_version"),
                    nested_hw.get("version"),
                    nested_hw.get("wis_version"),
                ])
    for cand in candidates:
        vt = _parse_semver_tuple(cand)
        if vt is not None:
            return vt
    return None


def _has_explicit_manual_skip_reason(hwtest: dict, report_data: dict = None) -> bool:
    """Proverava da li postoji eksplicitan manual_skip_reason na nivou izveštaja, hwtest bloka ili pojedinačnog testa."""
    if isinstance(hwtest, dict):
        if str(hwtest.get("manual_skip_reason") or "").strip():
            return True
        summary = hwtest.get("summary")
        if isinstance(summary, dict) and str(summary.get("manual_skip_reason") or "").strip():
            return True
        tests = hwtest.get("tests")
        if isinstance(tests, list):
            for t in tests:
                if isinstance(t, dict) and str(t.get("manual_skip_reason") or "").strip():
                    return True
    if isinstance(report_data, dict):
        if str(report_data.get("manual_skip_reason") or "").strip():
            return True
        nested = report_data.get("latest_report")
        if isinstance(nested, dict) and str(nested.get("manual_skip_reason") or "").strip():
            return True
    return False


def _has_skipped_interactive_hwtests(hwtest, report_data: dict = None) -> bool:
    """
    Proverava da li u `hwtest.tests` postoje preskočeni ili nezavršeni interaktivni testovi.
    Prema IMP-41 (3A): strogo spuštanje na NEEDS_SERVICE primenjuje se na izveštaje sa verzijom >= 3.4.3
    (ili kada postoji eksplicitan `manual_skip_reason`), dok istorijski v3.4.0–v3.4.2 izveštaji
    zadržavaju svoj status (READY_WITH_NOTE za PASS_WITH_SKIP).
    """
    if not isinstance(hwtest, dict):
        return False
    ver_tuple = _extract_report_version_tuple(hwtest, report_data)
    has_manual_reason = _has_explicit_manual_skip_reason(hwtest, report_data)
    if ver_tuple is not None and ver_tuple < (3, 4, 3) and not has_manual_reason:
        return False
    tests = hwtest.get("tests")
    if not isinstance(tests, list):
        return False
    for test in tests:
        if not isinstance(test, dict):
            continue
        if test.get("automated") is False:
            st = str(test.get("status") or "").strip().upper()
            if st in ("SKIP", "PENDING"):
                return True
    return False


_LOG_ISO_DATE_RE = re.compile(r"\b(20\d{2}-\d{2}-\d{2})\b")
_LOG_DOT_DATE_RE = re.compile(r"\b(\d{2}\.\d{2}\.20\d{2})\b")


def _extract_report_date(rec: dict, nested: dict):
    """Izdvaja datum izveštaja (`date`) iz timestamp/completed_at polja ili današnjeg datuma."""
    for src in (rec, nested):
        if not isinstance(src, dict):
            continue
        for k in ("timestamp", "completed_at", "started_at"):
            raw = str(src.get(k) or "").strip()
            if not raw:
                continue
            m = _LOG_ISO_DATE_RE.search(raw)
            if m:
                try:
                    return datetime.strptime(m.group(1), "%Y-%m-%d").date()
                except ValueError:
                    pass
    return datetime.now().date()


def _extract_line_date(err_text: str):
    """Izdvaja datum iz log linije ako postoji (YYYY-MM-DD ili DD.MM.YYYY)."""
    m_iso = _LOG_ISO_DATE_RE.search(err_text)
    if m_iso:
        try:
            return datetime.strptime(m_iso.group(1), "%Y-%m-%d").date()
        except ValueError:
            pass
    m_dot = _LOG_DOT_DATE_RE.search(err_text)
    if m_dot:
        try:
            return datetime.strptime(m_dot.group(1), "%d.%m.%Y").date()
        except ValueError:
            pass
    return None


def _apps_have_real_failures(apps) -> bool:
    """Proverava da li u `apps` (dict ili list) ima neuspelih aplikacija, izuzimajući 'License' (V350-07)."""
    if isinstance(apps, dict):
        for app_name, app_st in apps.items():
            if str(app_name or "").strip().lower() == "license":
                continue
            st_norm = str(app_st or "").strip().upper()
            if st_norm in ("ERROR", "FAIL", "FAILED", "CRITICAL", "ERR") or st_norm.startswith(("ERROR", "FAIL")):
                return True
    elif isinstance(apps, list):
        for item in apps:
            if isinstance(item, dict):
                item_id = str(item.get("id") or item.get("name") or "").strip().lower()
                if item_id == "license":
                    continue
                st_norm = str(item.get("status") or "").strip().upper()
                if st_norm in ("ERROR", "FAIL", "FAILED", "CRITICAL", "ERR") or st_norm.startswith(("ERROR", "FAIL")):
                    return True
    return False


def _filter_stale_session_errors(rec: dict, nested: dict, errors_list: list) -> list:
    """
    IMP-66: Za status == 'COMPLETED' gde su aplikacije čiste (ne računajući 'License')
    i hwtest je bez FAIL/preskočenih interaktivnih testova, filtrira zastarele log greške
    od prethodnih dana / starih sesija da ne izazivaju lažni NEEDS_SERVICE.
    """
    if not errors_list:
        return []
    nested_d = nested if isinstance(nested, dict) else {}
    status_raw = str(rec.get("status") if rec.get("status") is not None else nested_d.get("status") or "").strip().upper()
    if status_raw != "COMPLETED":
        return list(errors_list)

    apps = rec.get("apps")
    if apps is None:
        apps = nested_d.get("apps")
    if _apps_have_real_failures(apps):
        return list(errors_list)

    hwtest = rec.get("hwtest")
    if hwtest is None:
        hwtest = nested_d.get("hwtest")
    if isinstance(hwtest, dict):
        summary = hwtest.get("summary")
        overall = str(summary.get("overall") or "").strip().upper() if isinstance(summary, dict) else ""
        if overall == "FAIL" or hwtest.get("passed") is False:
            return list(errors_list)
        if _has_skipped_interactive_hwtests(hwtest, report_data=rec):
            return list(errors_list)

    report_date = _extract_report_date(rec, nested_d)
    cur_sid = str(rec.get("install_session_id") or nested_d.get("install_session_id") or "").strip()

    filtered = []
    for item in errors_list:
        txt = str(item or "").strip()
        if not txt:
            continue
        if cur_sid:
            m_sid = re.search(r"\[SESSION:([^\]]+)\]", txt, re.IGNORECASE)
            if m_sid and m_sid.group(1).strip() != cur_sid:
                continue
        line_date = _extract_line_date(txt)
        if line_date is not None and report_date is not None and line_date < report_date:
            continue
        filtered.append(item)
    return filtered


def _has_failed_apps_or_errors(rec: dict, nested: dict) -> bool:
    """
    Proverava da li `apps` sadrži aplikaciju sa ERROR/FAIL statusom (izuzev 'License' — V350-07)
    ili `errors` sadrži aktivne greške (uz IMP-66 filtriranje zastarelih log linija).
    """
    apps = rec.get("apps")
    if apps is None and isinstance(nested, dict):
        apps = nested.get("apps")
    if _apps_have_real_failures(apps):
        return True

    errors = rec.get("errors")
    if errors is None and isinstance(nested, dict):
        errors = nested.get("errors")
    if isinstance(errors, list):
        active_errors = _filter_stale_session_errors(rec, nested, errors)
        if any(str(e or "").strip() for e in active_errors):
            return True
    elif isinstance(errors, str) and bool(errors.strip()):
        active_errors = _filter_stale_session_errors(rec, nested, [errors])
        if any(str(e or "").strip() for e in active_errors):
            return True
    if rec.get("has_errors") is True or (isinstance(nested, dict) and nested.get("has_errors") is True):
        return True
    return False


def compute_handover_status(rec: dict) -> str:
    """
    Računa jedinstveni kanonski status spremnosti uređaja za predaju kupcu:
    - FAILED: kritični neuspeh, KVAR_POTVRDJEN (*KVAR*) ili propala anti-leak / security provera
    - NEEDS_SERVICE: termalno pregrevanje (THERMAL_CRITICAL), HwTest FAIL, preskočeni interaktivni testovi (v3.4.3+),
      aplikacija u ERROR/FAIL stanju, zabeležene greške, nedostajući latest_report u Fazi 6 ili NEEDS_ATTENTION
    - READY_WITH_NOTE: završeno uz napomenu (WARN / SENSOR_UNAVAILABLE / PASS_WITH_SKIP automatskih ili pre-v3.4.3 testova / servisne beleške)
    - READY: potpuno spreman bez primedbi
    """
    if not isinstance(rec, dict):
        return "NEEDS_SERVICE"

    nested = rec.get("latest_report") if isinstance(rec.get("latest_report"), dict) else {}

    def _pick(key: str):
        val = rec.get(key)
        if val is None and isinstance(nested, dict):
            val = nested.get(key)
        return val

    status_raw = str(_pick("status") or "").strip().upper()
    dep_raw = str(_pick("deployment_status") or "").strip().upper()
    agy_raw = str(_pick("agy_status") or "").strip().upper()
    lbl_raw = str(_pick("status_label") or "").strip()
    lbl_upper = lbl_raw.upper()
    diag = diagnostics_contract(rec if "diagnostics_status" in rec or "max_cpu_temp_c" in rec else nested)
    diag_st = diag.get("diagnostics_status", "NOT_VERIFIED")

    hwtest = _pick("hwtest")
    hwtest_overall = ""
    if isinstance(hwtest, dict):
        summary = hwtest.get("summary")
        if isinstance(summary, dict):
            hwtest_overall = str(summary.get("overall") or "").strip().upper()
        elif hwtest.get("passed") is False:
            hwtest_overall = "FAIL"

    has_skipped_interactive = _has_skipped_interactive_hwtests(hwtest, report_data=rec)
    has_app_or_rec_errors = _has_failed_apps_or_errors(rec, nested)

    phase_val = _pick("phase")
    has_saved_report = (
        (isinstance(rec.get("latest_report"), dict) and bool(rec.get("latest_report")))
        or bool(rec.get("latest_report_file"))
        or bool(rec.get("_has_in_memory_report"))
    )
    is_missing_terminal_report = (
        lbl_raw in ("Zavrseno", "Završeno")
        and not has_saved_report
        and (
            phase_val in (6, "6")
            or "latest_report" in rec
            or (not status_raw and not dep_raw)
        )
    )

    anti_leak = _pick("anti_leak_verified")
    sec_hardening = _pick("security_hardening_verified")
    handover_ready = _pick("handover_ready")

    if (
        status_raw in ("ERROR", "FAILED", "CRITICAL", "CRITICAL_ISSUE", "KVAR_POTVRDJEN")
        or "KVAR" in status_raw
        or "KVAR" in dep_raw
        or "KVAR" in agy_raw
        or lbl_raw == "Kvar (AGY)"
        or "KVAR" in lbl_upper
        or anti_leak is False
        or sec_hardening is False
    ):
        return "FAILED"

    if (
        diag_st == "THERMAL_CRITICAL"
        or hwtest_overall == "FAIL"
        or has_skipped_interactive
        or dep_raw == "NEEDS_ATTENTION"
        or status_raw == "NEEDS_ATTENTION"
        or handover_ready is False
        or has_app_or_rec_errors
        or is_missing_terminal_report
    ):
        return "NEEDS_SERVICE"

    notes = _pick("notes")
    thermal_note = str(_pick("thermal_note") or "").strip()
    has_notes = bool(thermal_note) or (isinstance(notes, list) and any(str(x).strip() for x in notes)) or (isinstance(notes, str) and bool(notes.strip()))

    if (
        diag_st in ("WARN", "SENSOR_UNAVAILABLE", "ERROR", "CANCELLED")
        or hwtest_overall == "PASS_WITH_SKIP"
        or has_notes
    ):
        return "READY_WITH_NOTE"

    if status_raw in ("COMPLETED", "SUCCESS", "OK", "DONE") or dep_raw in ("DONE", "READY", "COMPLETED") or lbl_raw in ("Zavrseno", "Završeno", "Servisiran (AGY)"):
        return "READY"

    return "READY_WITH_NOTE" if has_notes else "NEEDS_SERVICE"


def diagnostics_contract(data):
    """Normalizuj primljeni rezultat bez proglašavanja odsutnog testa uspešnim."""
    status = str(data.get('diagnostics_status') or 'NOT_VERIFIED').upper()
    allowed = {'OK', 'WARN', 'SENSOR_UNAVAILABLE', 'ERROR', 'CANCELLED', 'NOT_VERIFIED', 'THERMAL_CRITICAL'}
    if status not in allowed:
        status = 'NOT_VERIFIED'
    try:
        raw_temp = data.get('max_cpu_temp_c', data.get('max_cpu_temp', 0))
        temperature = float(raw_temp) if not isinstance(raw_temp, bool) else 0
    except (ValueError, TypeError):
        temperature = 0
    if data.get('emergency_abort') is True or temperature >= 95:
        status = 'THERMAL_CRITICAL'
    verified = status == 'OK' and data.get('cooling_verified') is True and data.get('manual_check_required') is not True
    manual = not verified
    if status == 'OK' and not verified:
        status = 'NOT_VERIFIED'
    messages = {
        'OK': 'Hlađenje automatski provereno prema rezultatu klijenta.',
        'SENSOR_UNAVAILABLE': 'Temperatura nije izmerena; potrebna ručna provera hlađenja.',
        'THERMAL_CRITICAL': 'NAPOMENA: Kritično pregrevanje procesora (>= 95°C) — sačuvano u izveštaju za servis hlađenja.',
        'WARN': 'Dijagnostičko upozorenje; potrebna servisna provera.',
        'ERROR': 'Dijagnostika je završena greškom; potrebna ručna provera.',
        'CANCELLED': 'Dijagnostika je prekinuta; potrebna ručna provera.',
        'NOT_VERIFIED': 'Hlađenje nije potvrđeno; potrebna ručna provera.',
    }
    return {'diagnostics_status': status, 'cooling_verified': verified,
            'manual_check_required': manual, 'diagnostics_summary': messages[status]}


_PRODUCT_KEY_PATTERN = re.compile(
    r"(?<![A-Za-z0-9-])(?:[A-Za-z0-9]{5}-){4}([A-Za-z0-9]{5})(?![A-Za-z0-9-])"
)


def _mask_license_keys(value):
    """Maskira 25-karakterne product ključeve (XXXXX-XXXXX-XXXXX-XXXXX-XXXXX) u tekstu i strukturama."""
    if isinstance(value, str):
        return _PRODUCT_KEY_PATTERN.sub(r"*****-*****-*****-*****-" + r"\1", value)
    if isinstance(value, dict):
        return {k: _mask_license_keys(v) for k, v in value.items()}
    if isinstance(value, list):
        return [_mask_license_keys(item) for item in value]
    return value


def _parse_optional_bool(val):
    """Bezbedno konvertuje opcioni boolean iz payload-a u True/False/None."""
    if val is None:
        return None
    if isinstance(val, bool):
        return val
    if isinstance(val, (int, float)):
        return bool(val)
    if isinstance(val, str):
        norm = val.strip().lower()
        if norm in ("true", "1", "yes", "da", "ok", "licensed", "activated"):
            return True
        if norm in ("false", "0", "no", "ne", "unlicensed", "not_activated"):
            return False
    return None


def _extract_license_summary(report_data: dict):
    """
    Izdvaja status licenciranja (Windows & Office) iz payload-a izveštaja,
    uz fallback na `licenses` rečnik i obavezno maskiranje product ključeva.
    Vraća None kada izveštaj nema nijedno polje o licenciranju (povratna kompatibilnost).
    """
    if not isinstance(report_data, dict):
        return None

    licenses = report_data.get("licenses") if isinstance(report_data.get("licenses"), dict) else {}

    def _pick_bool(top_key: str, *license_keys):
        if top_key in report_data and report_data[top_key] is not None:
            parsed = _parse_optional_bool(report_data[top_key])
            if parsed is not None:
                return parsed
        for lk in license_keys:
            if lk in licenses and licenses[lk] is not None:
                parsed = _parse_optional_bool(licenses[lk])
                if parsed is not None:
                    return parsed
        return None

    def _pick_text(top_key: str, *license_keys) -> str:
        if top_key in report_data and report_data[top_key] is not None:
            txt = str(report_data[top_key]).strip()
            if txt:
                return _mask_license_keys(txt)
        for lk in license_keys:
            if lk in licenses and licenses[lk] is not None:
                txt = str(licenses[lk]).strip()
                if txt:
                    return _mask_license_keys(txt)
        return ""

    win_act = _pick_bool("windows_activated", "windows_activated", "windows")
    win_oa3 = _pick_bool("windows_oa3_present", "windows_oa3_present", "oa3_present")
    win_edition = _pick_text("windows_edition", "windows_edition")
    win_method = _pick_text("windows_license_method", "windows_license_method")
    win_detail = _pick_text("windows_license_detail", "windows_license_detail")
    off_inst = _pick_bool("office_installed", "office_installed")
    off_act = _pick_bool("office_activated", "office_activated", "office")
    method = _pick_text("office_license_method", "office_license_method", "method")
    detail = _pick_text("office_license_detail", "office_license_detail", "detail")
    mode = _pick_text("office_activation_mode", "office_activation_mode", "mode")

    has_any = any([
        win_act is not None,
        win_oa3 is not None,
        bool(win_edition),
        bool(win_method),
        bool(win_detail),
        off_inst is not None,
        off_act is not None,
        bool(method),
        bool(detail),
        bool(mode),
    ])
    if not has_any:
        return None

    if win_act is True:
        win_label = "✅ Licenciran"
    elif win_act is False:
        win_label = "⚠️ Nije aktiviran"
    else:
        win_label = "Nepoznato"

    mode_norm = mode.strip().lower()
    method_norm = method.strip().lower()
    if off_inst is False:
        off_label = "Nije instaliran"
    elif off_act is True:
        off_label = "✅ Licenciran"
    elif mode_norm == "skip" or method_norm == "skip":
        off_label = "Preskočeno (Skip)"
    elif off_act is False:
        off_label = "⚠️ Nije aktiviran"
    else:
        off_label = "Nepoznato"

    return {
        "windows_activated": win_act,
        "windows_oa3_present": win_oa3,
        "windows_edition": win_edition,
        "windows_license_method": win_method,
        "windows_license_detail": win_detail,
        "office_installed": off_inst,
        "office_activated": off_act,
        "office_license_method": method,
        "office_license_detail": detail,
        "office_activation_mode": mode,
        "windows_status_label": win_label,
        "office_status_label": off_label,
    }


def _sanitize_report_license_fields(payload: dict) -> dict:
    """Vraća kopiju izveštaja sa maskiranim eventualnim product ključevima u poljima licenciranja."""
    if not isinstance(payload, dict):
        return payload
    sanitized = dict(payload)
    for key in (
        "windows_license_detail",
        "windows_license_method",
        "windows_edition",
        "windows_product_key",
        "WindowsProductKey",
        "office_license_detail",
        "office_license_method",
        "office_activation_mode",
        "office_product_key",
        "OfficeProductKey",
        "product_key",
        "licenses",
    ):
        if key in sanitized and sanitized[key] is not None:
            sanitized[key] = _mask_license_keys(sanitized[key])
    if isinstance(sanitized.get("latest_report"), dict):
        sanitized["latest_report"] = _sanitize_report_license_fields(sanitized["latest_report"])
    return sanitized


def generate_markdown_report(report_data: dict) -> str:
    """Generiše čitak i profesionalan Markdown servisni karton iz JSON izveštaja."""
    dev_id = report_data.get("device_id", "N/A")
    comp_name = report_data.get("computer_name", dev_id)
    chassis = report_data.get("chassis", "Desktop")
    serial = report_data.get("serial_number", "N/A")
    status = report_data.get("status", "COMPLETED")
    dur_sec = report_data.get("duration_seconds", 0)
    try:
        dur_sec = int(dur_sec)
    except (ValueError, TypeError):
        dur_sec = 0

    minutes = dur_sec // 60
    seconds = dur_sec % 60
    dur_str = report_data.get("duration_formatted") or f"{minutes}m {seconds:02d}s"
    timestamp = report_data.get("timestamp") or datetime.now().strftime("%Y-%m-%d %H:%M:%S")

    status_icon = "✅" if status.upper() in ("SUCCESS", "COMPLETED", "OK") else ("⚠️" if status.upper() == "WARNING" else "❌")
    diagnostics = diagnostics_contract(report_data)
    handover_status = compute_handover_status(report_data)
    if (diagnostics['manual_check_required'] or handover_status != "READY") and status_icon == '✅':
        status_icon = '⚠️' if handover_status != "FAILED" else '❌'

    lines = [
        f"# {status_icon} WIS Service | Završni Servisni Izveštaj",
        "",
        "## 💻 Informacije o Računaru",
        "",
        f"- **ID Uređaja:** `{dev_id}`",
        f"- **Naziv Računara:** `{comp_name}`",
        f"- **Tip Kućišta (Chassis):** {chassis}",
        f"- **Serijski Broj:** `{serial}`",
        f"- **Status Instalacije:** **{status}**",
        f"- **Status Predaje (Handover):** `{handover_status}`",
        f"- **Ukupno Trajanje:** {dur_str} ({dur_sec} s)",
        f"- **Vreme Izveštaja:** {timestamp}",
        "",
        "## Dijagnostika hlađenja",
        f"- **Rezultat:** {diagnostics['diagnostics_status']}",
        f"- **Automatski provereno:** {'Da' if diagnostics['cooling_verified'] else 'Ne'}",
        f"- **Ručna provera potrebna:** {'Da' if diagnostics['manual_check_required'] else 'Ne'}",
        diagnostics['diagnostics_summary'],
        "",
        "---",
        "",
        "## 📦 Status Instalacije Aplikacija",
        "",
        "| Aplikacija | Status |",
        "| :--- | :--- |",
    ]

    apps = report_data.get("apps", {})
    if isinstance(apps, dict) and apps:
        for app, app_st in apps.items():
            icon = "✅" if app_st in ("Completed", "OK", "Success") else ("❌" if app_st in ("Failed", "Error") else "⏳")
            lines.append(f"| {app} | {icon} {app_st} |")
    elif isinstance(apps, list) and apps:
        for app in apps:
            if isinstance(app, dict):
                app_name = app.get("name", "Unknown")
                app_st = app.get("status", "N/A")
                icon = "✅" if app_st in ("Completed", "OK", "Success") else ("❌" if app_st in ("Failed", "Error") else "⏳")
                lines.append(f"| {app_name} | {icon} {app_st} |")
            else:
                lines.append(f"| {app} | ✅ Instalirano |")
    else:
        lines.append("| Nema podataka o aplikacijama | - |")

    lic_summary = _extract_license_summary(report_data)
    if lic_summary:
        lines.extend([
            "",
            "---",
            "",
            "## 🔑 Status Licenciranja (Windows & Office)",
            "",
            f"- **Windows Status:** {lic_summary['windows_status_label']}",
        ])
        if lic_summary["windows_edition"]:
            lines.append(f"- **Windows Edicija:** {lic_summary['windows_edition']}")
        if lic_summary["windows_oa3_present"] is not None:
            oa3_label = "Prisutan" if lic_summary["windows_oa3_present"] else "Nije pronađen"
            lines.append(f"- **BIOS OA3x OEM Ključ:** {oa3_label}")
        if lic_summary["windows_license_method"]:
            lines.append(f"- **Metod Aktivacije (Windows):** {lic_summary['windows_license_method']}")
        if lic_summary["windows_license_detail"]:
            lines.append(f"- **Detalji Licenciranja (Windows):** {lic_summary['windows_license_detail']}")
        lines.append(f"- **Office Status:** {lic_summary['office_status_label']}")
        if lic_summary["office_activation_mode"]:
            lines.append(f"- **Režim Aktivacije (Office):** {lic_summary['office_activation_mode']}")
        if lic_summary["office_license_method"]:
            lines.append(f"- **Metod Aktivacije (Office):** {lic_summary['office_license_method']}")
        if lic_summary["office_license_detail"]:
            lines.append(f"- **Detalji Licenciranja:** {lic_summary['office_license_detail']}")

    lines.extend([
        "",
        "---",
        "",
        "## ⚠️ Greške i Upozorenja",
        "",
    ])

    errors = report_data.get("errors", [])
    if isinstance(errors, list) and errors:
        for err in errors:
            lines.append(f"- 🔴 {err}")
    elif isinstance(errors, str) and errors.strip():
        lines.append(f"- 🔴 {errors}")
    else:
        lines.append("✓ Nema zabeleženih grešaka tokom procesa instalacije.")

    note_items = []
    raw_thermal_note = str(report_data.get("thermal_note") or "").strip()
    if raw_thermal_note:
        note_items.append(raw_thermal_note)
    raw_notes = report_data.get("notes", [])
    if isinstance(raw_notes, list):
        for n_item in raw_notes:
            txt = str(n_item or "").strip()
            if txt and txt not in note_items:
                note_items.append(txt)
    elif isinstance(raw_notes, str) and raw_notes.strip() and raw_notes.strip() not in note_items:
        note_items.append(raw_notes.strip())
    if diagnostics.get("diagnostics_status") == "THERMAL_CRITICAL" and not note_items:
        note_items.append(diagnostics.get("diagnostics_summary", ""))
    if note_items:
        lines.extend([
            "",
            "### 📝 Napomene za Servis",
            "",
        ])
        for note in note_items:
            lines.append(f"- ⚠️ {note}")

    hw = report_data.get("hw") or {}
    if hw and isinstance(hw, dict):
        lines.extend([
            "",
            "---",
            "",
            "## ⚙️ Hardverski Profil",
            "",
            f"- **Proizvođač:** {hw.get('manufacturer', 'N/A')}",
            f"- **Model:** {hw.get('model', 'N/A')}",
            f"- **Procesor (CPU):** {hw.get('cpu', 'N/A')}",
            f"- **RAM Memorija:** {_format_hw_ram(hw)}",
            f"- **Disk:** {_format_hw_disk(hw)}",
            f"- **Ekran (Display):** {_format_hw_display(hw)}",
        ])

    # Fabrička Garancija (OEM)
    warranty_info = report_data.get("warranty")
    if not warranty_info and isinstance(report_data.get("oem"), dict):
        if "warranty" in report_data["oem"]:
            warranty_info = report_data["oem"]["warranty"]
        elif "status_label" in report_data["oem"]:
            warranty_info = report_data["oem"]

    if "warranty" in report_data or "oem" in report_data:
        w_dict = warranty_info if isinstance(warranty_info, dict) else {}
        status_label = w_dict.get("status_label") or ("U garanciji" if w_dict.get("in_warranty") else ("Nepoznato" if not w_dict else "Istekla garancija"))
        service_level = w_dict.get("service_level") or "N/A"
        start_date = w_dict.get("start_date") or "N/A"
        end_date = w_dict.get("end_date") or "N/A"
        try:
            days_remaining = int(w_dict.get("days_remaining", 0))
        except (ValueError, TypeError):
            days_remaining = 0

        lines.extend([
            "",
            "---",
            "",
            "### 🛡️ Fabrička Garancija (OEM)",
            f"- **Status:** {status_label}",
            f"- **Nivo podrške:** {service_level}",
            f"- **Period:** {start_date} do {end_date} (Preostalo: {days_remaining} dana)",
        ])

    # Hardversko testiranje (WIS.HwTest)
    hwtest = report_data.get("hwtest")
    if hwtest and isinstance(hwtest, dict):
        summary = hwtest.get("summary", {})
        overall = str(summary.get("overall", "UNKNOWN") or "UNKNOWN").strip().upper()
        passed = summary.get("pass", 0)
        failed = summary.get("fail", 0)
        skipped = summary.get("skip", 0)
        na = summary.get("na", 0)

        has_skipped_interactive = _has_skipped_interactive_hwtests(hwtest, report_data=report_data)
        if overall == "FAIL":
            badge = "❌ FAIL"
        elif has_skipped_interactive:
            badge = "⚠️ NEPOTPUNO (preskočeni interaktivni testovi)"
        elif overall in ("PASS", "PASS_WITH_SKIP"):
            badge = "✅ PASS"
        else:
            badge = f"⚪ {overall}"
        lines.extend([
            "",
            "---",
            "",
            "## 🧪 Ručno Hardversko Testiranje (WIS-HwTest)",
            "",
            f"- **Ukupan Ishod:** {badge}",
            f"- **Rezime:** {passed} prošlo, {failed} palo, {skipped} preskočeno, {na} neprimenljivo",
            "",
            "| Test | Ishod | Trajanje | Pokušaji | Detalji / Razlog |",
            "|---|---|---|---|---|"
        ])
        for t in hwtest.get("tests", []):
            tid = t.get("id", "nepoznato")
            tst = str(t.get("status", "N/A") or "N/A").strip().upper()
            ticon = (
                "✅ PASS" if tst == "PASS"
                else ("❌ FAIL" if tst == "FAIL"
                else ("⏭️ SKIP" if tst == "SKIP"
                else ("⏳ PENDING" if tst == "PENDING" else "⚪ N/A")))
            )
            tdur = f"{t.get('duration_s', 0)}s"
            tatt = str(t.get("attempts", 1))
            treason = t.get("reason", "")
            if not treason:
                det = t.get("details", {})
                if isinstance(det, dict) and det:
                    treason = ", ".join(f"{k}: {v}" for k, v in det.items() if v)
            lines.append(f"| `{tid}` | {ticon} | {tdur} | {tatt} | {treason or '—'} |")

    bm = report_data.get("benchmark")
    if isinstance(bm, dict) and bm:
        hit_ratio = bm.get("cache_hit_ratio_pct", 0)
        byte_ratio = bm.get("byte_hit_ratio_pct", 0)
        lan_mbps = bm.get("lan_throughput_mbps", 0)
        wan_mbps = bm.get("wan_throughput_mbps", 0)
        hits = bm.get("cache_hits", 0)
        misses = bm.get("cache_misses", 0)
        lan_mb = round((bm.get("lan_bytes", 0) or 0) / (1024 * 1024), 2)
        wan_mb = round((bm.get("wan_bytes", 0) or 0) / (1024 * 1024), 2)
        lines.extend([
            "",
            "---",
            "",
            "## ⏱️ WIS Benchmark & Performanse",
            "",
            f"- **Cache Hit Ratio:** {hit_ratio}% ({hits} LAN pogodaka / {misses} WAN promašaja)",
            f"- **Byte Hit Ratio:** {byte_ratio}% (LAN: {lan_mb} MB | WAN: {wan_mb} MB)",
            f"- **Protok Mreže (Throughput):** LAN {lan_mbps} Mbps | WAN {wan_mbps} Mbps",
        ])
        phase_durs = bm.get("phase_durations_sec")
        if isinstance(phase_durs, dict) and phase_durs:
            lines.extend([
                "",
                "| Faza | Trajanje |",
                "| :--- | :--- |",
            ])
            for pk in ("Phase1", "Phase2", "Phase3", "Phase4", "Phase5", "Phase6"):
                if pk in phase_durs:
                    lines.append(f"| `{pk}` | {phase_durs.get(pk, 0)}s |")

    lines.extend([
        "",
        "---",
        "*Dokument automatski generisan od strane ServisHub sistema.*",
        ""
    ])

    return "\n".join(lines)


def save_device_report(
    device_id: str,
    payload: dict,
    reports_dir: str,
    dedup_window_sec: int = 1800,
) -> tuple[str, str, str, str]:
    """
    Arhivira JSON i Markdown servisni karton na disk.
    Ako unutar `dedup_window_sec` (podrazumevano 30 min) već postoji izveštaj
    za isti `clean_id` (uz kompatibilan `serial_number` i `session_id`/`run_id`),
    ažurira taj postojeći izveštaj umesto pravljenja duplih fajlova.
    Vraća (json_filepath, md_filepath, json_filename, md_filename).
    """
    import json as _rep_json
    import time as _rep_time

    os.makedirs(reports_dir, exist_ok=True)
    cleanup_stale_tmp_files(reports_dir, max_age_sec=3600.0)
    clean_id = sanitize_id(device_id)
    now_ts = _rep_time.time()

    def _extract_sn(rec: dict) -> str:
        if not isinstance(rec, dict):
            return ""
        hw = rec.get("hw") if isinstance(rec.get("hw"), dict) else (rec.get("hardware") if isinstance(rec.get("hardware"), dict) else {})
        raw_sn = str(rec.get("serial_number") or rec.get("serial") or hw.get("serial") or "").strip()
        if raw_sn.lower() in ("", "unknown", "none", "n/a", "to be filled by o.e.m.", "default string"):
            return ""
        return raw_sn.lower()

    def _extract_sid(rec: dict) -> str:
        if not isinstance(rec, dict):
            return ""
        return str(rec.get("install_session_id") or rec.get("session_id") or rec.get("run_id") or "").strip()

    in_sn = _extract_sn(payload)
    in_sid = _extract_sid(payload)

    reuse_json_filename = None
    reuse_md_filename = None
    if dedup_window_sec and dedup_window_sec > 0:
        suffix = f"_{clean_id}.json"
        candidates = []
        try:
            for fn in os.listdir(reports_dir):
                if not fn.endswith(suffix) or fn.endswith(".tmp"):
                    continue
                fp = os.path.join(reports_dir, fn)
                if not os.path.isfile(fp):
                    continue
                try:
                    mtime = os.path.getmtime(fp)
                except OSError:
                    continue
                if (now_ts - mtime) <= dedup_window_sec:
                    candidates.append((mtime, fn, fp))
        except OSError:
            candidates = []

        candidates.sort(key=lambda x: x[0], reverse=True)
        for _, cand_fn, cand_fp in candidates:
            try:
                with open(cand_fp, "r", encoding="utf-8") as f:
                    old_data = _rep_json.load(f)
            except Exception:
                old_data = {}
            old_sn = _extract_sn(old_data)
            old_sid = _extract_sid(old_data)
            if in_sn and old_sn and in_sn != old_sn:
                continue
            if in_sid and old_sid and in_sid != old_sid:
                continue
            reuse_json_filename = cand_fn
            reuse_md_filename = cand_fn[:-5] + ".md"
            break

    if reuse_json_filename and reuse_md_filename:
        json_filename = reuse_json_filename
        md_filename = reuse_md_filename
    else:
        date_str = datetime.now().strftime("%Y%m%d_%H%M%S")
        json_filename = f"{date_str}_{clean_id}.json"
        md_filename = f"{date_str}_{clean_id}.md"
        seq = 1
        while os.path.exists(os.path.join(reports_dir, json_filename)):
            json_filename = f"{date_str}_{seq}_{clean_id}.json"
            md_filename = f"{date_str}_{seq}_{clean_id}.md"
            seq += 1

    json_filepath = os.path.join(reports_dir, json_filename)
    md_filepath = os.path.join(reports_dir, md_filename)

    sanitized_payload = _sanitize_report_license_fields(payload)
    if isinstance(sanitized_payload.get("hw"), dict):
        if not sanitized_payload["hw"].get("display") and sanitized_payload.get("display"):
            sanitized_payload["hw"]["display"] = sanitized_payload["display"]
    elif sanitized_payload.get("display"):
        sanitized_payload["hw"] = {"display": sanitized_payload["display"]}

    if isinstance(sanitized_payload.get("errors"), list) and sanitized_payload["errors"]:
        sanitized_payload["errors"] = _filter_stale_session_errors(
            sanitized_payload, {}, sanitized_payload["errors"]
        )
    elif isinstance(sanitized_payload.get("errors"), str) and sanitized_payload["errors"].strip():
        filtered_errs = _filter_stale_session_errors(
            sanitized_payload, {}, [sanitized_payload["errors"]]
        )
        sanitized_payload["errors"] = filtered_errs[0] if filtered_errs else ""

    _atomic_write_json(json_filepath, sanitized_payload)

    md_content = generate_markdown_report(sanitized_payload)
    _atomic_write_text(md_filepath, md_content)

    return json_filepath, md_filepath, json_filename, md_filename


_HISTORY_PLACEHOLDERS = frozenset({
    "", "n/a", "na", "unknown", "unknown-pc", "none", "null",
    "to be filled by o.e.m.", "default string", "system product name",
    "genericpc", "nepoznato", "nepoznat model", "nepoznat serijski",
})


def _is_valid_history_id(val) -> bool:
    if val is None or isinstance(val, bool):
        return False
    return str(val).strip().lower() not in _HISTORY_PLACEHOLDERS


def _parse_history_ts_and_iso(raw_ts, fname: str = "", fpath: str = "") -> tuple[float, str]:
    if raw_ts is not None and not isinstance(raw_ts, bool):
        if isinstance(raw_ts, (int, float)):
            ts = float(raw_ts)
            try:
                return ts, datetime.fromtimestamp(ts).isoformat()
            except Exception:
                return ts, str(raw_ts)
        if isinstance(raw_ts, str):
            txt = raw_ts.strip()
            if txt:
                try:
                    dt = datetime.fromisoformat(txt.replace("Z", "+00:00"))
                    return dt.timestamp(), txt
                except Exception:
                    for fmt in ("%Y-%m-%d %H:%M:%S", "%Y%m%d_%H%M%S", "%Y-%m-%dT%H:%M:%S"):
                        try:
                            dt = datetime.strptime(txt, fmt)
                            return dt.timestamp(), txt
                        except ValueError:
                            pass
    if fname:
        m = re.search(r"(\d{8}_\d{6})", fname)
        if m:
            try:
                dt = datetime.strptime(m.group(1), "%Y%m%d_%H%M%S")
                return dt.timestamp(), dt.isoformat()
            except ValueError:
                pass
    if fpath and os.path.isfile(fpath):
        try:
            mt = os.path.getmtime(fpath)
            return mt, datetime.fromtimestamp(mt).isoformat()
        except OSError:
            pass
    return 0.0, ""


def _safe_wis_intervention_summary(report_data: dict) -> str:
    """Generiše čitak sažetak WIS kartona bez izlaganja servisnih tajni ili ključeva."""
    handover = compute_handover_status(report_data)
    parts = [f"WIS instalacija ({handover})"]

    dur_sec = report_data.get("duration_seconds", 0)
    try:
        dur_sec_int = int(float(dur_sec or 0))
    except (ValueError, TypeError):
        dur_sec_int = 0
    dur_fmt = str(report_data.get("duration_formatted") or "").strip()
    if dur_fmt and dur_fmt != "N/A":
        parts.append(f"Trajanje: {dur_fmt}")
    elif dur_sec_int > 0:
        parts.append(f"Trajanje: {dur_sec_int // 60}m {dur_sec_int % 60:02d}s")

    apps = report_data.get("apps")
    if isinstance(apps, dict) and apps:
        ok_cnt = sum(1 for v in apps.values() if str(v).strip().upper() in ("OK", "COMPLETED", "SUCCESS", "TRUE"))
        parts.append(f"Aplikacije: {ok_cnt}/{len(apps)}")

    hwtest = report_data.get("hwtest")
    if isinstance(hwtest, dict):
        overall = str((hwtest.get("summary") or {}).get("overall") or "").strip()
        if overall:
            parts.append(f"HwTest: {overall}")

    diag = diagnostics_contract(report_data)
    if diag.get("diagnostics_status") and diag["diagnostics_status"] != "NOT_VERIFIED":
        parts.append(f"Hlađenje: {diag['diagnostics_status']}")

    lic = _extract_license_summary(report_data)
    if lic:
        parts.append(f"Win: {'OK' if lic.get('windows_activated') else 'N/A'} / Office: {lic.get('office_status_label')}")

    return _mask_license_keys(" • ".join(parts))


def get_device_history(clean_id: str, reports_dir: str = None, serial_hint: str = None) -> dict:
    """
    Očitava istoriju intervencija i servisni dnevnik za dati uređaj.
    Proverava postojanje AGY foldera storage/reports/<clean_id>/ (ili DEVICE_INFO.json)
    kao i kanonskih WIS JSON kartona u korenu reports_dir (*.json).
    Vraća sortirano najnovije prvo.
    """
    from servishub.config import active_config
    base_dir = reports_dir or active_config.reports_dir
    raw_clean = str(clean_id or "").strip()
    sanitized = sanitize_id(raw_clean)

    empty_resp = {
        "status": "ok",
        "is_registered": False,
        "device_tag": sanitized,
        "serial_number": "",
        "manufacturer": "",
        "model": "",
        "first_seen": None,
        "last_seen": None,
        "total_interventions": 0,
        "interventions": [],
        "dnevnik_markdown": "",
        "warranty": None,
        "oem": None,
    }

    if not base_dir or not os.path.isdir(base_dir) or not raw_clean or sanitized in ("", "UNKNOWN"):
        return empty_resp

    real_base = os.path.realpath(base_dir)
    hint_clean = str(serial_hint or "").strip()
    hint_san = sanitize_id(hint_clean) if hint_clean else ""

    def _is_safe_subpath(target_path: str) -> bool:
        try:
            if os.path.islink(target_path):
                return False
            return os.path.commonpath([real_base, os.path.realpath(target_path)]) == real_base
        except (ValueError, OSError):
            return False

    # 1. Pronađi AGY poddirektorijum (tačan tag, DEVICE_INFO.json SN ili substring taga)
    dev_dir = os.path.join(base_dir, sanitized)
    if not (_is_safe_subpath(dev_dir) and os.path.isdir(dev_dir)):
        dev_dir = ""
        sn_match_dir = None
        sub_match_dir = None
        lookup_norms = {sanitized.lower(), raw_clean.lower()}
        if hint_clean:
            lookup_norms.add(hint_clean.lower())
            lookup_norms.add(hint_san.lower())
        try:
            with os.scandir(base_dir) as it:
                for entry in sorted(it, key=lambda e: e.name):
                    if entry.is_symlink() or not entry.is_dir(follow_symlinks=False):
                        continue
                    candidate = entry.path
                    if not _is_safe_subpath(candidate):
                        continue
                    dinfo_file = os.path.join(candidate, "DEVICE_INFO.json")
                    if os.path.isfile(dinfo_file) and _is_safe_subpath(dinfo_file):
                        try:
                            with open(dinfo_file, "r", encoding="utf-8") as f:
                                dinfo_data = json.load(f)
                            d_sn = str(dinfo_data.get("serial_number", "")).strip()
                            if _is_valid_history_id(d_sn) and (
                                d_sn.lower() in lookup_norms or sanitize_id(d_sn).lower() in lookup_norms
                            ):
                                sn_match_dir = candidate
                                sanitized = entry.name
                                break
                        except Exception:
                            pass
                    if sub_match_dir is None and sanitized.lower() in entry.name.lower():
                        sub_match_dir = (candidate, entry.name)
        except OSError:
            pass
        if sn_match_dir:
            dev_dir = sn_match_dir
        elif sub_match_dir:
            dev_dir, sanitized = sub_match_dir

    device_info = {}
    dnevnik_markdown = ""
    interventions = []

    if dev_dir and os.path.isdir(dev_dir) and _is_safe_subpath(dev_dir):
        dinfo_path = os.path.join(dev_dir, "DEVICE_INFO.json")
        if os.path.isfile(dinfo_path) and _is_safe_subpath(dinfo_path):
            try:
                with open(dinfo_path, "r", encoding="utf-8") as f:
                    device_info = json.load(f)
            except Exception:
                device_info = {}

        dnevnik_path = os.path.join(dev_dir, "SERVISNI_DNEVNIK.md")
        if os.path.isfile(dnevnik_path) and _is_safe_subpath(dnevnik_path):
            try:
                with open(dnevnik_path, "r", encoding="utf-8") as f:
                    dnevnik_markdown = f.read()
            except Exception:
                dnevnik_markdown = ""

        sessions_dir = os.path.join(dev_dir, "sessions")
        if os.path.isdir(sessions_dir) and _is_safe_subpath(sessions_dir):
            session_files = sorted(f for f in os.listdir(sessions_dir) if f.endswith(".json"))
            for sf in session_files:
                sf_path = os.path.join(sessions_dir, sf)
                if not _is_safe_subpath(sf_path) or not os.path.isfile(sf_path):
                    continue
                try:
                    with open(sf_path, "r", encoding="utf-8") as f:
                        sess_data = json.load(f)
                    raw_dt = sess_data.get("timestamp") or sess_data.get("date", "")
                    ts_score, iso_dt = _parse_history_ts_and_iso(raw_dt, sf, sf_path)
                    interventions.append({
                        "date": raw_dt or iso_dt,
                        "session_id": sess_data.get("session_id", ""),
                        "summary": _mask_license_keys(sess_data.get("summary", "")),
                        "status": sess_data.get("status", "COMPLETED"),
                        "tools_used": sess_data.get("tools_executed") or sess_data.get("tools_used") or [],
                        "serviser_id": sess_data.get("serviser_id", ""),
                        "_sort_ts": ts_score,
                    })
                except Exception:
                    continue

        if not interventions and dnevnik_markdown:
            pattern = r"###\s+\[Datum:\s*([^\]]+)\]\s*(?:-\s*(.*))?"
            matches = list(re.finditer(pattern, dnevnik_markdown))
            for i, match in enumerate(matches):
                dt_str = match.group(1).strip()
                title_str = (match.group(2) or "").strip()
                start_pos = match.end()
                end_pos = matches[i + 1].start() if i + 1 < len(matches) else len(dnevnik_markdown)
                section_text = dnevnik_markdown[start_pos:end_pos].strip()

                summary_line = title_str
                if section_text:
                    first_line = section_text.splitlines()[0].strip().lstrip("-* ")
                    summary_line = f"{summary_line}: {first_line}" if summary_line else first_line

                ts_score, _ = _parse_history_ts_and_iso(dt_str)
                interventions.append({
                    "date": dt_str,
                    "session_id": "",
                    "summary": summary_line or "Intervencija zabeležena u dnevniku",
                    "status": "COMPLETED",
                    "tools_used": [],
                    "_sort_ts": ts_score,
                })

    # 2. Proširi pretragu na kanonske WIS JSON kartone u korenu reports_dir
    target_norms = set()
    for seed in (
        raw_clean,
        sanitized,
        hint_clean,
        hint_san,
        device_info.get("serial_number") if isinstance(device_info, dict) else "",
        device_info.get("device_tag") if isinstance(device_info, dict) else "",
        (device_info.get("hardware") or {}).get("serial_number") if isinstance(device_info, dict) and isinstance(device_info.get("hardware"), dict) else "",
    ):
        if _is_valid_history_id(seed):
            s_val = str(seed).strip()
            target_norms.add(s_val.lower())
            target_norms.add(sanitize_id(s_val).lower())

    wis_records: list[tuple[float, dict, str, dict]] = []
    seen_wis_sids: dict[str, int] = {}

    try:
        with os.scandir(base_dir) as it:
            root_entries = sorted(it, key=lambda e: e.name)
    except OSError:
        root_entries = []

    for entry in root_entries:
        fname = entry.name
        if not fname.lower().endswith(".json") or fname.lower().endswith(".tmp"):
            continue
        if entry.is_symlink() or not entry.is_file(follow_symlinks=False):
            continue
        fpath = entry.path
        if not _is_safe_subpath(fpath):
            continue
        try:
            with open(fpath, "r", encoding="utf-8") as f:
                data = json.load(f)
        except Exception:
            continue
        if not isinstance(data, dict):
            continue

        hw = data.get("hw") if isinstance(data.get("hw"), dict) else (data.get("hardware") if isinstance(data.get("hardware"), dict) else {})
        candidates = (
            data.get("serial_number"),
            data.get("serial"),
            hw.get("serial"),
            hw.get("serial_number"),
            data.get("device_id"),
            data.get("computer_name"),
            data.get("device_tag"),
        )
        matched = False
        for cand in candidates:
            if not _is_valid_history_id(cand):
                continue
            c_str = str(cand).strip()
            if c_str.lower() in target_norms or sanitize_id(c_str).lower() in target_norms:
                matched = True
                break
        if not matched:
            low_fn = fname.lower()
            for tn in target_norms:
                if tn and (low_fn.startswith(f"report_{tn}_") or low_fn.endswith(f"_{tn}.json")):
                    matched = True
                    break
        if not matched:
            continue

        safe_data = _sanitize_report_license_fields(data)
        raw_ts = safe_data.get("timestamp") or safe_data.get("completed_at") or safe_data.get("last_seen")
        ts_score, iso_dt = _parse_history_ts_and_iso(raw_ts, fname, fpath)

        dur_sec = safe_data.get("duration_seconds", 0)
        try:
            dur_sec_int = int(float(dur_sec or 0))
        except (ValueError, TypeError):
            dur_sec_int = 0
        dur_fmt = str(safe_data.get("duration_formatted") or "").strip()
        if (not dur_fmt or dur_fmt == "N/A") and dur_sec_int > 0:
            dur_fmt = f"{dur_sec_int // 60}m {dur_sec_int % 60:02d}s"

        handover_st = compute_handover_status(safe_data)
        tools_used = ["WIS Deploy"]
        if isinstance(safe_data.get("hwtest"), dict) and safe_data["hwtest"]:
            tools_used.append("WIS-HwTest")

        sid = str(safe_data.get("session_id") or safe_data.get("run_id") or "").strip()
        wis_item = {
            "date": iso_dt,
            "session_id": sid,
            "summary": _safe_wis_intervention_summary(safe_data),
            "status": str(safe_data.get("status") or "COMPLETED"),
            "handover_status": handover_st,
            "duration_seconds": dur_sec_int,
            "duration_formatted": dur_fmt or "N/A",
            "report_file": fname,
            "tools_used": tools_used,
            "serviser_id": str(safe_data.get("serviser_id") or "WIS"),
            "_sort_ts": ts_score,
        }

        if sid and sid in seen_wis_sids:
            prev_idx = seen_wis_sids[sid]
            if ts_score >= wis_records[prev_idx][0]:
                wis_records[prev_idx] = (ts_score, safe_data, fname, wis_item)
        else:
            if sid:
                seen_wis_sids[sid] = len(wis_records)
            wis_records.append((ts_score, safe_data, fname, wis_item))

    for _, _, _, w_item in wis_records:
        interventions.append(w_item)

    if not interventions and not dnevnik_markdown and not device_info and not wis_records:
        return empty_resp

    # Sortiraj sve intervencije najnovije prvo
    interventions.sort(key=lambda x: (float(x.get("_sort_ts") or 0.0), str(x.get("date") or "")), reverse=True)

    date_pairs = [
        (float(item.get("_sort_ts") or 0.0), item["date"])
        for item in interventions
        if item.get("date")
    ]
    for item in interventions:
        item.pop("_sort_ts", None)

    wis_sorted = sorted(wis_records, key=lambda x: x[0], reverse=True)
    latest_wis = wis_sorted[0][1] if wis_sorted else {}
    latest_wis_hw = (
        latest_wis.get("hw")
        if isinstance(latest_wis.get("hw"), dict)
        else (latest_wis.get("hardware") if isinstance(latest_wis.get("hardware"), dict) else {})
    )

    hw = device_info.get("hardware") if isinstance(device_info.get("hardware"), dict) else {}

    def _pick_first_valid(*vals) -> str:
        for v in vals:
            if _is_valid_history_id(v):
                return str(v).strip()
        return ""

    serial = _pick_first_valid(
        device_info.get("serial_number"),
        hw.get("serial_number"),
        hw.get("serial"),
        latest_wis.get("serial_number"),
        latest_wis.get("serial"),
        latest_wis_hw.get("serial"),
        latest_wis_hw.get("serial_number"),
        hint_clean,
    )
    manufacturer = _pick_first_valid(
        device_info.get("manufacturer"),
        hw.get("manufacturer"),
        latest_wis_hw.get("manufacturer"),
        latest_wis.get("manufacturer"),
    )
    model = _pick_first_valid(
        device_info.get("model"),
        hw.get("model"),
        latest_wis_hw.get("model"),
        latest_wis.get("device_model"),
        latest_wis.get("model"),
    )

    if dev_dir:
        resolved_tag = os.path.basename(dev_dir)
    else:
        resolved_tag = _pick_first_valid(
            latest_wis.get("device_tag"),
            latest_wis.get("device_id"),
            latest_wis.get("computer_name"),
            sanitized,
        ) or sanitized
        resolved_tag = sanitize_id(resolved_tag)

    all_date_candidates: list[tuple[float, str]] = list(date_pairs)
    for d_key in ("first_seen", "last_seen"):
        d_val = device_info.get(d_key)
        if d_val:
            d_ts, d_iso = _parse_history_ts_and_iso(d_val)
            all_date_candidates.append((d_ts, str(d_val)))

    if all_date_candidates:
        all_date_candidates.sort(key=lambda x: (x[0], x[1]))
        first_seen = device_info.get("first_seen") or all_date_candidates[0][1]
        if all_date_candidates[0][0] > 0 and device_info.get("first_seen"):
            fs_ts, _ = _parse_history_ts_and_iso(device_info["first_seen"])
            if fs_ts > 0 and all_date_candidates[0][0] < fs_ts:
                first_seen = all_date_candidates[0][1]
        last_seen = device_info.get("last_seen") or all_date_candidates[-1][1]
        if all_date_candidates[-1][0] > 0 and device_info.get("last_seen"):
            ls_ts, _ = _parse_history_ts_and_iso(device_info["last_seen"])
            if ls_ts > 0 and all_date_candidates[-1][0] > ls_ts:
                last_seen = all_date_candidates[-1][1]
    else:
        first_seen = device_info.get("first_seen")
        last_seen = device_info.get("last_seen")

    warranty = device_info.get("warranty") if isinstance(device_info.get("warranty"), dict) else latest_wis.get("warranty")
    oem = device_info.get("oem") if isinstance(device_info.get("oem"), dict) else latest_wis.get("oem")

    return {
        "status": "ok",
        "is_registered": True,
        "device_tag": resolved_tag,
        "serial_number": serial,
        "manufacturer": manufacturer,
        "model": model,
        "first_seen": first_seen,
        "last_seen": last_seen,
        "total_interventions": len(interventions),
        "interventions": interventions,
        "dnevnik_markdown": dnevnik_markdown,
        "warranty": warranty,
        "oem": oem,
    }


def save_agy_session(clean_id: str, payload: dict, reports_dir: str = None) -> dict:
    """
    Arhivira AGY servisnu sesiju na disk.
    Kreira/ažurira:
    - sessions/<timestamp_slug>_<session_id>.json
    - SERVISNI_DNEVNIK.md
    - DEVICE_INFO.json (ako payload sadrži 'hardware' ili 'serial_number')
    - latest_report.md (ako payload sadrži 'report_markdown')
    Vraća rečnik sa putanjama kreiranih/ažuriranih fajlova.
    """
    from servishub.config import active_config
    base_dir = reports_dir or active_config.reports_dir
    sanitized = sanitize_id(clean_id)

    dev_dir = os.path.join(base_dir, sanitized)
    sessions_dir = os.path.join(dev_dir, "sessions")
    os.makedirs(sessions_dir, exist_ok=True)

    session_id = payload.get("session_id")
    if not session_id:
        import uuid
        session_id = str(uuid.uuid4())
        payload["session_id"] = session_id

    safe_sess_id = sanitize_id(str(session_id))
    ts_raw = payload.get("timestamp")
    if ts_raw and isinstance(ts_raw, str):
        clean_ts = sanitize_id(ts_raw.replace("T", "_").replace("Z", "").replace(":", ""))
        ts_slug = clean_ts[:17]
    else:
        ts_slug = datetime.now().strftime("%Y-%m-%d_%H%M%S")

    session_filename = f"{ts_slug}_{safe_sess_id}.json"
    session_filepath = os.path.join(sessions_dir, session_filename)
    _atomic_write_json(session_filepath, payload)

    dnevnik_path = os.path.join(dev_dir, "SERVISNI_DNEVNIK.md")
    dnevnik_entry = payload.get("dnevnik_entry")
    if not dnevnik_entry:
        summary = payload.get("summary", "Dijagnostička sesija")
        status = payload.get("status", "COMPLETED")
        ts_str = payload.get("timestamp") or datetime.now().strftime("%Y-%m-%d %H:%M:%S")
        dnevnik_entry = f"### [Datum: {ts_str}] - Status: {status}\n- **Sažetak:** {summary}"
    dnevnik_entry = str(dnevnik_entry).strip()

    if not os.path.isfile(dnevnik_path):
        header = (
            f"# Servisni Dnevnik Intervencija: {sanitized}\n\n"
            "Ovaj dokument sadrzi istoriju svih servisnih intervencija i dijagnostickih nalaza za ovaj racunar.\n\n"
            "---\n\n"
        )
        with open(dnevnik_path, "w", encoding="utf-8") as f:
            f.write(header + dnevnik_entry + "\n")
    else:
        with open(dnevnik_path, "a", encoding="utf-8") as f:
            f.write(f"\n\n{dnevnik_entry}\n")

    hw = payload.get("hardware")
    dev_info_path = os.path.join(dev_dir, "DEVICE_INFO.json")
    dev_info_created = False
    if hw or payload.get("serial_number") or payload.get("warranty") or payload.get("oem"):
        dev_info = {}
        if os.path.isfile(dev_info_path):
            try:
                with open(dev_info_path, "r", encoding="utf-8") as f:
                    dev_info = json.load(f)
            except Exception:
                dev_info = {}

        dev_info["device_tag"] = sanitized
        if payload.get("serial_number"):
            dev_info["serial_number"] = payload["serial_number"]
        elif isinstance(hw, dict) and hw.get("serial_number"):
            dev_info["serial_number"] = hw["serial_number"]

        if isinstance(hw, dict):
            if hw.get("manufacturer"):
                dev_info["manufacturer"] = hw["manufacturer"]
            if hw.get("model"):
                dev_info["model"] = hw["model"]
            dev_info["hardware"] = hw

        if payload.get("warranty"):
            dev_info["warranty"] = payload["warranty"]
        elif isinstance(payload.get("oem"), dict) and "warranty" in payload.get("oem"):
            dev_info["warranty"] = payload["oem"]["warranty"]
        elif isinstance(payload.get("oem"), dict) and "status_label" in payload.get("oem"):
            dev_info["warranty"] = payload["oem"]

        if payload.get("oem"):
            dev_info["oem"] = payload["oem"]

        now_iso = payload.get("timestamp") or datetime.now().isoformat()
        if "first_seen" not in dev_info:
            dev_info["first_seen"] = now_iso
        dev_info["last_seen"] = now_iso

        _atomic_write_json(dev_info_path, dev_info)
        dev_info_created = True

    report_md = payload.get("report_markdown")
    latest_report_path = os.path.join(dev_dir, "latest_report.md")
    latest_report_created = False
    if report_md:
        _atomic_write_text(latest_report_path, str(report_md))
        latest_report_created = True

    return {
        "session_file": session_filepath,
        "session_filename": session_filename,
        "dnevnik_file": dnevnik_path,
        "device_info_file": dev_info_path if dev_info_created else None,
        "latest_report_file": latest_report_path if latest_report_created else None,
    }


def register_new_device(clean_id: str, payload: dict, reports_dir: str = None) -> dict:
    """
    Kreira novi karton računara na Servis Hub-u pre početka dijagnostike (Pre-flight registracija).
    Kreira/ažurira:
    - DEVICE_INFO.json sa statusom 'U_DIJAGNOSTICI' i hardverskim profilom
    - SERVISNI_DNEVNIK.md (inicijalni prijem)
    """
    from servishub.config import active_config
    base_dir = reports_dir or active_config.reports_dir
    sanitized = sanitize_id(clean_id)

    dev_dir = os.path.join(base_dir, sanitized)
    sessions_dir = os.path.join(dev_dir, "sessions")
    os.makedirs(sessions_dir, exist_ok=True)

    dev_info_path = os.path.join(dev_dir, "DEVICE_INFO.json")
    is_new = not os.path.isfile(dev_info_path)
    dev_info = {}
    if not is_new:
        try:
            with open(dev_info_path, "r", encoding="utf-8") as f:
                dev_info = json.load(f)
        except Exception:
            dev_info = {}

    hw = payload.get("hardware") or {}
    serial = payload.get("serial_number") or hw.get("serial_number") or dev_info.get("serial_number") or ""
    manufacturer = payload.get("manufacturer") or hw.get("manufacturer") or dev_info.get("manufacturer") or "Generic"
    model = payload.get("model") or hw.get("model") or dev_info.get("model") or "System"
    status = payload.get("status", "U_DIJAGNOSTICI")
    serviser = payload.get("serviser_id", "USB_TEREN_01")
    now_iso = payload.get("timestamp") or datetime.now().isoformat()

    dev_info["device_tag"] = sanitized
    dev_info["serial_number"] = serial
    dev_info["manufacturer"] = manufacturer
    dev_info["model"] = model
    dev_info["hardware"] = hw if hw else dev_info.get("hardware", {})
    dev_info["status"] = status
    dev_info["serviser_id"] = serviser
    if "first_seen" not in dev_info:
        dev_info["first_seen"] = now_iso
    dev_info["last_seen"] = now_iso

    _atomic_write_json(dev_info_path, dev_info)

    dnevnik_path = os.path.join(dev_dir, "SERVISNI_DNEVNIK.md")
    ts_str = payload.get("timestamp") or datetime.now().strftime("%Y-%m-%d %H:%M:%S")
    prijem_entry = (
        f"### [Datum: {ts_str}] - Status: {status}\n"
        f"- **Prijem na servis:** Računar primljen na dijagnostiku (Serviser: {serviser}).\n"
        f"- **Hardver:** {manufacturer} {model} | CPU: {hw.get('cpu', 'N/A')} | RAM: {_format_hw_ram(hw)}"
    )

    if not os.path.isfile(dnevnik_path):
        header = (
            f"# Servisni Dnevnik Intervencija: {sanitized}\n\n"
            "Ovaj dokument sadrzi istoriju svih servisnih intervencija i dijagnostickih nalaza za ovaj racunar.\n\n"
            "---\n\n"
        )
        with open(dnevnik_path, "w", encoding="utf-8") as f:
            f.write(header + prijem_entry + "\n")
    else:
        with open(dnevnik_path, "a", encoding="utf-8") as f:
            f.write(f"\n\n{prijem_entry}\n")

    return {
        "status": "ok",
        "is_registered": True,
        "action": "created" if is_new else "updated",
        "device_tag": sanitized,
        "device_info_file": dev_info_path,
        "dnevnik_file": dnevnik_path,
    }


def save_device_checkpoint(clean_id: str, payload: dict, reports_dir: str = None) -> dict:
    """
    Beleži operativni checkpoint (nalaz) tokom trajanja dijagnostike.
    Ažurira DEVICE_INFO.json i SERVISNI_DNEVNIK.md bez prekidanja sesije.
    """
    from servishub.config import active_config
    base_dir = reports_dir or active_config.reports_dir
    sanitized = sanitize_id(clean_id)

    dev_dir = os.path.join(base_dir, sanitized)
    sessions_dir = os.path.join(dev_dir, "sessions")
    os.makedirs(sessions_dir, exist_ok=True)

    checkpoint_id = payload.get("checkpoint_id")
    if not checkpoint_id:
        import uuid
        checkpoint_id = str(uuid.uuid4())
        payload["checkpoint_id"] = checkpoint_id

    summary = payload.get("summary", "Operativni servisni checkpoint.")
    status = payload.get("status", "U_DIJAGNOSTICI")
    ts_str = payload.get("timestamp") or datetime.now().strftime("%Y-%m-%d %H:%M:%S")
    now_iso = payload.get("timestamp") or datetime.now().isoformat()
    tools = payload.get("tools_executed") or []

    # Ažuriranje DEVICE_INFO.json
    dev_info_path = os.path.join(dev_dir, "DEVICE_INFO.json")
    dev_info = {}
    if os.path.isfile(dev_info_path):
        try:
            with open(dev_info_path, "r", encoding="utf-8") as f:
                dev_info = json.load(f)
        except Exception:
            dev_info = {}

    dev_info["device_tag"] = sanitized
    dev_info["status"] = status
    dev_info["last_seen"] = now_iso
    _atomic_write_json(dev_info_path, dev_info)

    # Dodavanje u SERVISNI_DNEVNIK.md
    dnevnik_path = os.path.join(dev_dir, "SERVISNI_DNEVNIK.md")
    cp_entry = f"### [Datum: {ts_str}] - Status: {status}\n- **Kontrolna tačka (Checkpoint):** {summary}"
    if tools:
        cp_entry += f"\n- **Pokrenuti alati:** {', '.join(tools)}"

    if not os.path.isfile(dnevnik_path):
        header = (
            f"# Servisni Dnevnik Intervencija: {sanitized}\n\n"
            "Ovaj dokument sadrzi istoriju svih servisnih intervencija i dijagnostickih nalaza za ovaj racunar.\n\n"
            "---\n\n"
        )
        with open(dnevnik_path, "w", encoding="utf-8") as f:
            f.write(header + cp_entry + "\n")
    else:
        with open(dnevnik_path, "a", encoding="utf-8") as f:
            f.write(f"\n\n{cp_entry}\n")

    return {
        "status": "ok",
        "checkpoint_id": checkpoint_id,
        "device_tag": sanitized,
        "status_code": status,
        "dnevnik_file": dnevnik_path,
    }

