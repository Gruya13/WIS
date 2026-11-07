"""
servishub.tui_cli
=================
Terminal TUI Fleet Watcher za ServisHub.
Konzolni monitor za praćenje flote i prenos logova uživo direktno u terminalu.
Prilagođen za AGY servisirane uređaje i prikaz OEM fabričke garancije.
"""

import sys
import time
import json
import urllib.request
import os
from typing import Any, Dict, Optional

from servishub.config import DEFAULT_HTTP_PORT

DEFAULT_HUB_URL = f"http://127.0.0.1:{DEFAULT_HTTP_PORT}"

# ANSI boje i stilovi
C_RESET = "\033[0m"
C_BOLD = "\033[1m"
C_DIM = "\033[2m"
C_CYAN = "\033[36m"
C_BLUE = "\033[34m"
C_GREEN = "\033[32m"
C_YELLOW = "\033[33m"
C_RED = "\033[31m"
C_MAGENTA = "\033[35m"
C_GRAY = "\033[90m"
C_WHITE = "\033[97m"


def fetch_json(url: str) -> Optional[Any]:
    """Preuzima JSON odgovor sa zadatog URL-a."""
    try:
        req = urllib.request.Request(url, headers={"User-Agent": "ServisHub-CLI"})
        with urllib.request.urlopen(req, timeout=2.5) as resp:
            return json.loads(resp.read().decode("utf-8"))
    except Exception:
        return None


def clear_screen() -> None:
    """Čisti terminal i vraća kursor na početak."""
    sys.stdout.write("\033[2J\033[H")
    sys.stdout.flush()


def extract_warranty(d: dict) -> Optional[dict]:
    """
    Ekstrahuje rečnik fabričke garancije iz telemetrije uređaja.
    Pretražuje top-level, latest_report, latest_agy_session, hw i oem polja.
    """
    if not isinstance(d, dict):
        return None

    # 1. Direktno polje na uređaju
    if isinstance(d.get("warranty"), dict) and d["warranty"]:
        return d["warranty"]

    # 2. latest_report
    lr = d.get("latest_report")
    if isinstance(lr, dict):
        if isinstance(lr.get("warranty"), dict) and lr["warranty"]:
            return lr["warranty"]
        if isinstance(lr.get("oem"), dict) and isinstance(lr["oem"].get("warranty"), dict):
            return lr["oem"]["warranty"]

    # 3. latest_agy_session
    las = d.get("latest_agy_session")
    if isinstance(las, dict):
        if isinstance(las.get("warranty"), dict) and las["warranty"]:
            return las["warranty"]
        if isinstance(las.get("oem"), dict) and isinstance(las["oem"].get("warranty"), dict):
            return las["oem"]["warranty"]

    # 4. hw
    hw = d.get("hw")
    if isinstance(hw, dict) and isinstance(hw.get("warranty"), dict) and hw["warranty"]:
        return hw["warranty"]

    # 5. oem
    oem = d.get("oem")
    if isinstance(oem, dict):
        if isinstance(oem.get("warranty"), dict) and oem["warranty"]:
            return oem["warranty"]
        if "in_warranty" in oem or "days_remaining" in oem:
            return oem

    # 6. tab_data
    td = d.get("tab_data")
    if isinstance(td, dict) and isinstance(td.get("warranty"), dict):
        return td["warranty"]

    return None


def format_warranty(warranty_data: Any) -> str:
    """
    Formatira OEM garanciju uređaja za prikaz u TUI konzoli.
    Primeri:
      - 'Garancija: Premier Onsite (448d)'
      - 'Garancija: Istekla / Van garancije'
    """
    if not warranty_data:
        return ""

    if isinstance(warranty_data, str):
        w_str = warranty_data.strip()
        if "istekla" in w_str.lower() or "van garancije" in w_str.lower():
            return "Garancija: Istekla / Van garancije"
        if w_str.startswith("Garancija:"):
            return w_str
        return f"Garancija: {w_str}"

    if not isinstance(warranty_data, dict):
        return ""

    in_warr = warranty_data.get("in_warranty")
    days = warranty_data.get("days_remaining")
    status_lbl = str(warranty_data.get("status_label", "")).lower()

    # Ako je dan isteka naveden ili end_date
    if days is None and warranty_data.get("end_date"):
        try:
            from datetime import date, datetime
            end_str = str(warranty_data["end_date"])[:10]
            end_dt = datetime.strptime(end_str, "%Y-%m-%d").date()
            diff = (end_dt - date.today()).days
            days = max(0, diff)
            if days <= 0:
                in_warr = False
        except Exception:
            pass

    # 1. Istekla garancija
    if in_warr is False or "istekla" in status_lbl or "van garancije" in status_lbl or (days is not None and days <= 0):
        return "Garancija: Istekla / Van garancije"

    # Ako nema podataka ili je fallback/greška
    if in_warr is None and days is None and not status_lbl:
        if warranty_data.get("status") in ("fallback", "error"):
            return "Garancija: Istekla / Van garancije"
        return ""

    # 2. U garanciji
    service_level = warranty_data.get("service_level") or warranty_data.get("name") or warranty_data.get("type") or "Premier Onsite"
    clean_level = str(service_level).strip()
    if clean_level.endswith(" Support"):
        clean_level = clean_level[:-8].strip()

    days_str = f"{days}d" if days is not None else "?d"
    return f"Garancija: {clean_level} ({days_str})"


def extract_intervention_text(d: dict) -> str:
    """
    Ekstrahuje tekst intervencije ili sažetak rada za uređaje koji nemaju GWADT faze 1-5
    ili su prošli AGY intervenciju.
    """
    if not isinstance(d, dict):
        return ""

    # 1. Direktna polja
    for key in ("summary", "intervention", "action", "status_message", "live_status"):
        val = d.get(key)
        if val and isinstance(val, str) and val.strip():
            return val.strip()

    # 2. latest_report / latest_agy_session
    for sub in ("latest_report", "latest_agy_session"):
        container = d.get(sub)
        if isinstance(container, dict):
            for key in ("summary", "intervention", "action", "status_message"):
                val = container.get(key)
                if val and isinstance(val, str) and val.strip():
                    return val.strip()

    # 3. dnevnik_entry
    de = d.get("dnevnik_entry")
    if de and isinstance(de, str):
        lines = [line.strip().lstrip("#- *") for line in de.splitlines() if line.strip()]
        if lines:
            return lines[0]

    # 4. Status oznaka za AGY
    lbl = str(d.get("status_label", ""))
    if "AGY" in lbl:
        return "AGY Servis"

    return ""


def get_status_color(status_label: str) -> str:
    """
    Vraća ANSI boju na osnovu statusne oznake.
    - Cijan/Plavo za 'Servisiran (AGY)'
    - Crveno/Žuto za 'Kvar (AGY)'
    """
    lbl = str(status_label or "")
    if "Servisiran (AGY)" in lbl or lbl == "Servisiran (AGY)":
        return f"{C_BOLD}{C_CYAN}"
    elif "Kvar (AGY)" in lbl or lbl == "Kvar (AGY)":
        return f"{C_BOLD}{C_RED}"
    elif lbl == "Aktivno":
        return C_GREEN
    elif lbl in ("Zavrseno", "Završeno"):
        return C_CYAN
    elif "Restart" in lbl:
        return C_YELLOW
    elif lbl in ("Pauzirano", "Upozorenje"):
        return C_YELLOW
    elif lbl == "Offline":
        return C_GRAY
    else:
        return C_RED


def show_fleet_table(hub_url: str = DEFAULT_HUB_URL, loop: bool = True, max_cycles: Optional[int] = None) -> None:
    """
    Prikazuje formatiranu tabelu flote računara u terminalu uz periodično osvežavanje.
    Prikazuje GWADT faze ili AGY intervencije, te OEM garanciju.
    """
    spinner = ['|', '/', '-', '\\']
    frame = 0

    while True:
        data = fetch_json(f"{hub_url}/api/devices")
        clear_screen()
        pulse = spinner[frame % len(spinner)]
        frame += 1

        width = 100
        print(f"{C_CYAN}+{('=' * (width - 2))}+{C_RESET}")
        print(f"{C_CYAN}| {C_BOLD}TEHNO-KUTAK SERVIS-HUB FLEET MONITOR{C_RESET}{C_CYAN} [ Aktivno: {C_GREEN}{pulse}{C_CYAN} ]" + (" " * (width - 49)) + f"|{C_RESET}")
        print(f"{C_CYAN}+{('=' * (width - 2))}+{C_RESET}")

        if not data:
            print(f"\n {C_YELLOW}[!] Hub server na {hub_url} nije dostupan ili nema uređaja.{C_RESET}")
            print(f" {C_GRAY}Pokrenite server: python3 hub_server.py{C_RESET}\n")
            if not loop or (max_cycles is not None and frame >= max_cycles):
                break
            time.sleep(2.0)
            continue

        devs = list(data.values())
        print(f" {C_WHITE}{'UREĐAJ':<22} {'S/N / IP':<18} {'FAZA / AKCIJA':<22} {'PROG':<7} {'STATUS':<18} {'ZADNJI PING'}{C_RESET}")
        print(f" {C_GRAY}{'-' * (width - 2)}{C_RESET}")

        for d in devs:
            name = d.get("computer_name") or d.get("device_tag") or "UNKNOWN"
            hw = d.get("hw") or d.get("hardware") or {}
            if hw and (hw.get("manufacturer") or hw.get("model")):
                disp_name = f"{hw.get('manufacturer', '')} {hw.get('model', '')}".strip()
            else:
                disp_name = name
            if len(disp_name) > 20:
                disp_name = disp_name[:18] + ".."

            serial = hw.get("serial") or d.get("serial_number") or d.get("client_ip", "N/A")
            if len(serial) > 16:
                serial = serial[:14] + ".."

            lbl = d.get("status_label", "Aktivno")
            col = get_status_color(lbl)

            # Provera da li mašina ima GWADT Faza 1-5 podatke
            phase_name = d.get("phase_name")
            has_gwadt_phase = bool(phase_name) and ("AGY" not in lbl) and not d.get("agy_status")

            if has_gwadt_phase:
                phase_num = d.get("phase", 1)
                phase_str = f"F{phase_num}. {phase_name}"
            else:
                intervention = extract_intervention_text(d)
                if intervention:
                    phase_str = intervention
                else:
                    phase_num = d.get("phase")
                    phase_str = f"Faza {phase_num}" if phase_num else "AGY Servis"

            col_phase_str = (phase_str[:19] + "..") if len(phase_str) > 21 else phase_str

            prog_val = d.get("worker_progress", 0)
            if ("Servisiran (AGY)" in lbl or lbl in ("Zavrseno", "Završeno")) and not prog_val:
                prog_val = 100
            prog = f"{prog_val}%"

            sec = d.get("last_seen_sec_ago", 0)
            ping_str = f"{sec}s pre"

            print(f" {C_BOLD}{disp_name:<22}{C_RESET} {C_GRAY}{serial:<18}{C_RESET} {col_phase_str:<22} {prog:<7} {col}{lbl:<18}{C_RESET} {C_GRAY}{ping_str}{C_RESET}")

            # Prikaz OEM garancije i sažetka intervencije ispod uređaja
            w_dict = extract_warranty(d)
            w_str = format_warranty(w_dict) if w_dict else ""
            full_intervention = extract_intervention_text(d)

            details_parts = []
            if w_str:
                w_col = C_CYAN if (w_dict.get("in_warranty") and "Istekla" not in w_str) else C_RED
                details_parts.append(f"{w_col}{w_str}{C_RESET}")

            if full_intervention and (not has_gwadt_phase or "AGY" in lbl):
                if full_intervention != col_phase_str:
                    details_parts.append(f"{C_WHITE}Intervencija: {full_intervention}{C_RESET}")
                elif not w_str and len(full_intervention) > 20:
                    details_parts.append(f"{C_WHITE}Intervencija: {full_intervention}{C_RESET}")

            if details_parts:
                print(f"   {C_GRAY}└─{C_RESET} " + f" {C_GRAY}|{C_RESET} ".join(details_parts))

        print(f"\n{C_GRAY} Komande: Ctrl+C za izlaz | --watch <UREĐAJ> za log | --device <UREĐAJ> za detalje{C_RESET}")

        if not loop or (max_cycles is not None and frame >= max_cycles):
            break
        time.sleep(2.0)


def show_device_details(device_name: str, hub_url: str = DEFAULT_HUB_URL) -> bool:
    """
    Prikazuje detaljne specifikacije, status, fazu rada i OEM garanciju za pojedinačni uređaj.
    """
    data = fetch_json(f"{hub_url}/api/devices")
    target_dev = None
    if data:
        for k, v in data.items():
            comp = v.get("computer_name", "")
            tag = v.get("device_tag", "")
            sn = v.get("serial_number") or v.get("hw", {}).get("serial", "")
            ip = v.get("client_ip", "")
            needle = device_name.strip().lower()
            if needle in (k.lower(), comp.lower(), tag.lower(), sn.lower(), ip.lower()):
                target_dev = v
                break

    if not target_dev:
        clean_id = "".join(c if c.isalnum() or c in "-_" else "_" for c in device_name)
        hist = fetch_json(f"{hub_url}/api/devices/{clean_id}/history")
        if hist and hist.get("is_registered"):
            target_dev = hist

    if not target_dev:
        print(f"\n {C_YELLOW}[!] Uređaj '{device_name}' nije pronađen na {hub_url}.{C_RESET}")
        return False

    width = 78
    comp_name = target_dev.get("computer_name") or target_dev.get("device_tag") or device_name
    hw = target_dev.get("hw") or target_dev.get("hardware") or {}
    mfg = hw.get("manufacturer") or target_dev.get("manufacturer", "Nepoznato")
    model = hw.get("model") or target_dev.get("model", "Nepoznato")
    sn = hw.get("serial") or target_dev.get("serial_number", "N/A")
    ip = target_dev.get("client_ip", "N/A")
    lbl = target_dev.get("status_label") or target_dev.get("status") or "Aktivno"
    col = get_status_color(lbl)

    print(f"\n{C_CYAN}+{('=' * (width - 2))}+{C_RESET}")
    print(f"{C_CYAN}| {C_BOLD}DETALJI UREĐAJA: {comp_name:<59}{C_RESET}{C_CYAN}|{C_RESET}")
    print(f"{C_CYAN}+{('=' * (width - 2))}+{C_RESET}")

    print(f" {C_WHITE}{'Naziv računara:':<24}{C_RESET} {C_BOLD}{comp_name}{C_RESET}")
    print(f" {C_WHITE}{'Proizvođač / Model:':<24}{C_RESET} {mfg} {model}")
    print(f" {C_WHITE}{'Serijski broj / IP:':<24}{C_RESET} {sn} ({ip})")
    print(f" {C_WHITE}{'Status servisa:':<24}{C_RESET} {col}{lbl}{C_RESET}")

    # Garancija
    w_data = extract_warranty(target_dev)
    if w_data:
        w_str = format_warranty(w_data)
        w_col = C_CYAN if (w_data.get("in_warranty") and "Istekla" not in w_str) else C_RED
        print(f" {C_WHITE}{'OEM Garancija:':<24}{C_RESET} {w_col}{C_BOLD}{w_str}{C_RESET}")
        if w_data.get("start_date") or w_data.get("end_date"):
            start = w_data.get("start_date", "-")
            end = w_data.get("end_date", "-")
            print(f" {C_GRAY}{'Period garancije:':<24}{C_RESET} {C_GRAY}{start} do {end}{C_RESET}")
    else:
        print(f" {C_WHITE}{'OEM Garancija:':<24}{C_RESET} {C_GRAY}Nema podataka{C_RESET}")

    # Intervencija / Sažetak
    intervention = extract_intervention_text(target_dev)
    if intervention:
        print(f" {C_WHITE}{'Intervencija / Sažetak:':<24}{C_RESET} {C_CYAN}{intervention}{C_RESET}")

    # GWADT Faza / Napredak
    phase = target_dev.get("phase")
    phase_name = target_dev.get("phase_name")
    prog = target_dev.get("worker_progress", 0)
    if phase_name:
        print(f" {C_WHITE}{'GWADT Faza:':<24}{C_RESET} F{phase}. {phase_name} ({prog}%)")
    elif prog:
        print(f" {C_WHITE}{'Napredak:':<24}{C_RESET} {prog}%")

    # Hardver detalji
    specs = []
    if hw.get("cpu"):
        specs.append(f"CPU: {hw['cpu']}")
    if hw.get("ram_gb"):
        specs.append(f"RAM: {hw['ram_gb']}GB")
    if hw.get("disks"):
        specs.append(f"Disk: {hw['disks']}")
    if specs:
        print(f" {C_WHITE}{'Hardver specifikacija:':<24}{C_RESET} {' | '.join(specs)}")

    sec = target_dev.get("last_seen_sec_ago")
    if sec is not None:
        print(f" {C_WHITE}{'Zadnji ping:':<24}{C_RESET} {sec}s pre")

    # Ako postoje intervencije iz istorije
    interventions = target_dev.get("interventions", [])
    if interventions:
        print(f"\n {C_BOLD}Istorija servisnih intervencija ({len(interventions)}):{C_RESET}")
        for idx, it in enumerate(interventions[-3:], 1):
            dt = it.get("date", "-")
            summ = it.get("summary", "-")
            st = it.get("status", "COMPLETED")
            print(f"  {idx}. [{dt}] ({st}) {summ}")

    print(f"{C_CYAN}+{('=' * (width - 2))}+{C_RESET}\n")
    return True


def watch_device_log(device_name: str, hub_url: str = DEFAULT_HUB_URL) -> None:
    """
    Povezuje se na SSE live log stream servera i prenosi linije loga u realnom vremenu.
    """
    clean_id = "".join(c if c.isalnum() or c in "-_" else "_" for c in device_name)
    print(f"[*] Povezujem se na log za '{clean_id}' na {hub_url}...")

    req = urllib.request.Request(f"{hub_url}/api/stream/{clean_id}", headers={"User-Agent": "ServisHub-CLI"})
    try:
        with urllib.request.urlopen(req) as resp:
            for raw_line in resp:
                line = raw_line.decode("utf-8", errors="ignore").strip()
                if line.startswith("data: "):
                    content = line[6:]
                    if "[SUCCESS]" in content:
                        print(f"{C_GREEN}{content}{C_RESET}")
                    elif "[WARN]" in content:
                        print(f"{C_YELLOW}{content}{C_RESET}")
                    elif "[ERROR]" in content or "[CRITICAL]" in content:
                        print(f"{C_RED}{C_BOLD}{content}{C_RESET}")
                    elif "[STEP]" in content:
                        print(f"{C_CYAN}{content}{C_RESET}")
                    elif "[AGY]" in content:
                        print(f"{C_BOLD}{C_CYAN}{content}{C_RESET}")
                    elif "[WARRANTY]" in content:
                        print(f"{C_CYAN}{content}{C_RESET}")
                    else:
                        print(content)
    except KeyboardInterrupt:
        print("\n[OK] Prekid praćenja loga.")
    except Exception as e:
        print(f"[!] Greška pri preuzimanju stream-a: {e}")


def main() -> None:
    """Glavna ulazna tačka za hub_cli i tui_cli."""
    args = sys.argv[1:]

    # Izdvajanje URL-a ako je naveden
    hub_url = DEFAULT_HUB_URL
    clean_args = []
    for arg in args:
        if arg.startswith("http://") or arg.startswith("https://"):
            hub_url = arg
        else:
            clean_args.append(arg)

    if not clean_args:
        try:
            show_fleet_table(hub_url=hub_url, loop=True)
        except KeyboardInterrupt:
            print("\n[OK] Izlaz iz monitora.")
        return

    cmd = clean_args[0]
    if cmd in ("--watch", "-w"):
        if len(clean_args) > 1:
            watch_device_log(clean_args[1], hub_url=hub_url)
        else:
            print(f"[!] Greška: Morate navesti naziv računara. Primer: python3 hub_cli.py --watch TK-LAPTOP-01")
    elif cmd in ("--device", "--info", "-d", "-i"):
        if len(clean_args) > 1:
            show_device_details(clean_args[1], hub_url=hub_url)
        else:
            print(f"[!] Greška: Morate navesti naziv računara. Primer: python3 hub_cli.py --device TK-LAPTOP-01")
    elif cmd in ("--once", "-1"):
        show_fleet_table(hub_url=hub_url, loop=False)
    elif cmd in ("--help", "-h"):
        print(f"\n{C_BOLD}Tehno-Kutak Servis-Hub Terminal Monitor{C_RESET}")
        print(f"Korišćenje:")
        print(f"  python3 hub_cli.py [HUB_URL]                - Praćenje flote uživo u realnom vremenu (TUI tabela)")
        print(f"  python3 hub_cli.py --watch <UREĐAJ>         - Praćenje loga uživo za računar (SSE prenos)")
        print(f"  python3 hub_cli.py --device <UREĐAJ>        - Prikaz detalja i garancije za računar")
        print(f"  python3 hub_cli.py --once                   - Jednokratni prikaz tabele flote")
        print(f"  python3 hub_cli.py --help                   - Pomoć i spisak opcija\n")
    else:
        show_device_details(cmd, hub_url=hub_url)


if __name__ == "__main__":
    main()
