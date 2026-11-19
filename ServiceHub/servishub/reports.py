"""
servishub.reports
=================
Generisanje profesionalnih Markdown servisnih kartona i perzistencija JSON izveštaja.
"""

import os
import json
from datetime import datetime
from servishub.drivers.cache import sanitize_id


def _atomic_write_text(filepath: str, content: str) -> None:
    """Atomično zapisuje tekstualni sadržaj na disk preko .tmp + os.replace."""
    parent = os.path.dirname(filepath)
    if parent:
        os.makedirs(parent, exist_ok=True)
    tmp_path = f"{filepath}.tmp"
    try:
        with open(tmp_path, "w", encoding="utf-8") as f:
            f.write(content)
            f.flush()
            os.fsync(f.fileno())
        os.replace(tmp_path, filepath)
    except Exception:
        if os.path.exists(tmp_path):
            try:
                os.remove(tmp_path)
            except OSError:
                pass
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


def compute_handover_status(rec: dict) -> str:
    """
    Računa jedinstveni kanonski status spremnosti uređaja za predaju kupcu:
    - FAILED: kritični neuspeh ili propala anti-leak / security provera
    - NEEDS_SERVICE: termalno pregrevanje (THERMAL_CRITICAL), HwTest FAIL ili NEEDS_ATTENTION
    - READY_WITH_NOTE: završeno uz napomenu (WARN / SENSOR_UNAVAILABLE / PASS_WITH_SKIP / servisne beleške)
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
    lbl_raw = str(_pick("status_label") or "").strip()
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

    anti_leak = _pick("anti_leak_verified")
    sec_hardening = _pick("security_hardening_verified")
    handover_ready = _pick("handover_ready")

    if (
        status_raw in ("ERROR", "FAILED", "CRITICAL", "CRITICAL_ISSUE")
        or lbl_raw == "Kvar (AGY)"
        or anti_leak is False
        or sec_hardening is False
    ):
        return "FAILED"

    if (
        diag_st == "THERMAL_CRITICAL"
        or hwtest_overall == "FAIL"
        or dep_raw == "NEEDS_ATTENTION"
        or status_raw == "NEEDS_ATTENTION"
        or handover_ready is False
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
        overall = summary.get("overall", "UNKNOWN")
        passed = summary.get("pass", 0)
        failed = summary.get("fail", 0)
        skipped = summary.get("skip", 0)
        na = summary.get("na", 0)

        badge = "✅ PASS" if overall in ("PASS", "PASS_WITH_SKIP") else ("❌ FAIL" if overall == "FAIL" else f"⚪ {overall}")
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
            tst = t.get("status", "N/A")
            ticon = "✅ PASS" if tst == "PASS" else ("❌ FAIL" if tst == "FAIL" else ("⏭️ SKIP" if tst == "SKIP" else "⚪ N/A"))
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


def save_device_report(device_id: str, payload: dict, reports_dir: str) -> tuple[str, str, str, str]:
    """
    Arhivira JSON i Markdown servisni karton na disk.
    Vraća (json_filepath, md_filepath, json_filename, md_filename).
    """
    os.makedirs(reports_dir, exist_ok=True)
    clean_id = sanitize_id(device_id)
    date_str = datetime.now().strftime("%Y%m%d_%H%M%S")
    json_filename = f"{date_str}_{clean_id}.json"
    md_filename = f"{date_str}_{clean_id}.md"
    json_filepath = os.path.join(reports_dir, json_filename)
    md_filepath = os.path.join(reports_dir, md_filename)

    _atomic_write_json(json_filepath, payload)

    md_content = generate_markdown_report(payload)
    _atomic_write_text(md_filepath, md_content)

    return json_filepath, md_filepath, json_filename, md_filename


def get_device_history(clean_id: str, reports_dir: str = None) -> dict:
    """
    Očitava istoriju intervencija i servisni dnevnik za dati uređaj.
    Proverava postojanje foldera storage/reports/<clean_id>/ (ili prosleđenog reports_dir).
    Parsira SERVISNI_DNEVNIK.md i sesije u sessions/.
    Ako uređaj ne postoji, vraća {"status": "ok", "is_registered": False, "interventions": [], "dnevnik_markdown": ""}.
    """
    from servishub.config import active_config
    base_dir = reports_dir or active_config.reports_dir
    sanitized = sanitize_id(clean_id)

    dev_dir = os.path.join(base_dir, sanitized)
    if not os.path.isdir(dev_dir):
        found_dir = None
        if os.path.isdir(base_dir):
            for d in os.listdir(base_dir):
                candidate = os.path.join(base_dir, d)
                if not os.path.isdir(candidate):
                    continue
                dinfo_file = os.path.join(candidate, "DEVICE_INFO.json")
                if os.path.isfile(dinfo_file):
                    try:
                        with open(dinfo_file, "r", encoding="utf-8") as f:
                            dinfo_data = json.load(f)
                            if str(dinfo_data.get("serial_number", "")).strip().lower() == sanitized.lower():
                                found_dir = candidate
                                sanitized = d
                                break
                    except Exception:
                        pass
                if sanitized.lower() in d.lower():
                    found_dir = candidate
                    sanitized = d
                    break
        if found_dir:
            dev_dir = found_dir

    if not os.path.isdir(dev_dir):
        return {
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

    device_info = {}
    dinfo_path = os.path.join(dev_dir, "DEVICE_INFO.json")
    if os.path.isfile(dinfo_path):
        try:
            with open(dinfo_path, "r", encoding="utf-8") as f:
                device_info = json.load(f)
        except Exception:
            device_info = {}

    dnevnik_markdown = ""
    dnevnik_path = os.path.join(dev_dir, "SERVISNI_DNEVNIK.md")
    if os.path.isfile(dnevnik_path):
        try:
            with open(dnevnik_path, "r", encoding="utf-8") as f:
                dnevnik_markdown = f.read()
        except Exception:
            dnevnik_markdown = ""

    interventions = []
    sessions_dir = os.path.join(dev_dir, "sessions")
    if os.path.isdir(sessions_dir):
        session_files = [f for f in os.listdir(sessions_dir) if f.endswith(".json")]
        session_files.sort()
        for sf in session_files:
            sf_path = os.path.join(sessions_dir, sf)
            try:
                with open(sf_path, "r", encoding="utf-8") as f:
                    sess_data = json.load(f)
                interventions.append({
                    "date": sess_data.get("timestamp") or sess_data.get("date", ""),
                    "session_id": sess_data.get("session_id", ""),
                    "summary": sess_data.get("summary", ""),
                    "status": sess_data.get("status", "COMPLETED"),
                    "tools_used": sess_data.get("tools_executed") or sess_data.get("tools_used") or [],
                    "serviser_id": sess_data.get("serviser_id", ""),
                })
            except Exception:
                continue
        interventions.sort(key=lambda x: x.get("date") or "")

    if not interventions and dnevnik_markdown:
        import re
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
                if summary_line:
                    summary_line = f"{summary_line}: {first_line}"
                else:
                    summary_line = first_line

            interventions.append({
                "date": dt_str,
                "session_id": "",
                "summary": summary_line or "Intervencija zabeležena u dnevniku",
                "status": "COMPLETED",
                "tools_used": [],
            })

    if not interventions and not dnevnik_markdown and not device_info:
        return {
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

    hw = device_info.get("hardware") if isinstance(device_info.get("hardware"), dict) else {}
    serial = device_info.get("serial_number") or hw.get("serial_number") or ""
    manufacturer = device_info.get("manufacturer") or hw.get("manufacturer") or ""
    model = device_info.get("model") or hw.get("model") or ""

    dates = [item["date"] for item in interventions if item.get("date")]
    dates.sort()
    first_seen = device_info.get("first_seen") or (dates[0] if dates else None)
    last_seen = device_info.get("last_seen") or (dates[-1] if dates else None)

    return {
        "status": "ok",
        "is_registered": True,
        "device_tag": sanitized,
        "serial_number": serial,
        "manufacturer": manufacturer,
        "model": model,
        "first_seen": first_seen,
        "last_seen": last_seen,
        "total_interventions": len(interventions),
        "interventions": interventions,
        "dnevnik_markdown": dnevnik_markdown,
        "warranty": device_info.get("warranty"),
        "oem": device_info.get("oem"),
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

