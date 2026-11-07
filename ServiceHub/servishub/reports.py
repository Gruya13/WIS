"""
servishub.reports
=================
Generisanje profesionalnih Markdown servisnih kartona i perzistencija JSON izveštaja.
"""

import os
import json
from datetime import datetime
from servishub.drivers.cache import sanitize_id


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
        'THERMAL_CRITICAL': 'Kritično pregrevanje; predaja je blokirana.',
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
    if diagnostics['manual_check_required'] and status_icon == '✅':
        status_icon = '⚠️'

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
            f"- **RAM Memorija:** {hw.get('ram_gb', 'N/A')} GB",
            f"- **Disk:** {hw.get('disk_model', 'N/A')} ({hw.get('disk_size_gb', 'N/A')} GB)",
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

    with open(json_filepath, "w", encoding="utf-8") as f:
        json.dump(payload, f, indent=2, ensure_ascii=False)

    md_content = generate_markdown_report(payload)
    with open(md_filepath, "w", encoding="utf-8") as f:
        f.write(md_content)

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
    with open(session_filepath, "w", encoding="utf-8") as f:
        json.dump(payload, f, indent=2, ensure_ascii=False)

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

        with open(dev_info_path, "w", encoding="utf-8") as f:
            json.dump(dev_info, f, indent=2, ensure_ascii=False)
        dev_info_created = True

    report_md = payload.get("report_markdown")
    latest_report_path = os.path.join(dev_dir, "latest_report.md")
    latest_report_created = False
    if report_md:
        with open(latest_report_path, "w", encoding="utf-8") as f:
            f.write(str(report_md))
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

    with open(dev_info_path, "w", encoding="utf-8") as f:
        json.dump(dev_info, f, indent=2, ensure_ascii=False)

    dnevnik_path = os.path.join(dev_dir, "SERVISNI_DNEVNIK.md")
    ts_str = payload.get("timestamp") or datetime.now().strftime("%Y-%m-%d %H:%M:%S")
    prijem_entry = (
        f"### [Datum: {ts_str}] - Status: {status}\n"
        f"- **Prijem na servis:** Računar primljen na dijagnostiku (Serviser: {serviser}).\n"
        f"- **Hardver:** {manufacturer} {model} | CPU: {hw.get('cpu', 'N/A')} | RAM: {hw.get('ram_gb', 'N/A')} GB"
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
    with open(dev_info_path, "w", encoding="utf-8") as f:
        json.dump(dev_info, f, indent=2, ensure_ascii=False)

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

