#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
Analyze-WISLog.py - WIS Deployment & Telemetry Log Analyzer
Alat za analizu, verifikaciju i dijagnostiku WIS deploy procesa.
Podrzava parsiranje servis_deploy.log, servis_deploy.jsonl, setupcomplete.log i firstlogon.log.
"""

import os
import sys
import re
import json
import argparse
from datetime import datetime
from collections import defaultdict

# ------------------------------------------------------------------------------
# ANSI BOJE I FORMATIRANJE
# ------------------------------------------------------------------------------
class Colors:
    def __init__(self, enabled=True):
        self.enabled = enabled and sys.stdout.isatty()

    def _c(self, code):
        return code if self.enabled else ""

    @property
    def RESET(self): return self._c("\033[0m")
    @property
    def BOLD(self): return self._c("\033[1m")
    @property
    def RED(self): return self._c("\033[91m")
    @property
    def GREEN(self): return self._c("\033[92m")
    @property
    def YELLOW(self): return self._c("\033[93m")
    @property
    def BLUE(self): return self._c("\033[94m")
    @property
    def MAGENTA(self): return self._c("\033[95m")
    @property
    def CYAN(self): return self._c("\033[96m")
    @property
    def WHITE(self): return self._c("\033[97m")
    @property
    def GRAY(self): return self._c("\033[90m")

def parse_timestamp(ts_str):
    if not ts_str:
        return None
    ts_str = ts_str.strip().replace('T', ' ').rstrip('Z')
    for fmt in ("%Y-%m-%d %H:%M:%S.%f", "%Y-%m-%d %H:%M:%S", "%Y-%m-%d %H:%M"):
        try:
            return datetime.strptime(ts_str, fmt)
        except ValueError:
            continue
    return None

def format_duration(seconds):
    if seconds is None:
        return "N/A"
    sec = int(round(seconds))
    hrs = sec // 3600
    mins = (sec % 3600) // 60
    secs = sec % 60
    if hrs > 0:
        return f"{hrs}h {mins:02d}m {secs:02d}s"
    elif mins > 0:
        return f"{mins}m {secs:02d}s"
    else:
        return f"{seconds:.1f}s"

# ------------------------------------------------------------------------------
# MODEL PODATAKA ANALIZE
# ------------------------------------------------------------------------------
class DeployAnalysis:
    def __init__(self):
        self.source_dir = ""
        self.log_files = {}
        self.start_time = None
        self.end_time = None
        self.total_duration_sec = 0.0

        # Metapodaci sistema
        self.metadata = {
            "computer_name": "Nepoznato",
            "user_name": "Nepoznato",
            "ps_version": "Nepoznato",
            "clr_version": "Nepoznato",
            "script_path": "Nepoznato",
            "brand": "USER",
            "usb_source": "Nije detektovan"
        }

        # Faze procesa (6 faza za v2.15.0)
        self.phases = {
            "Phase 1 (Init & Mreza)": {"start": None, "end": None, "duration_sec": None, "status": "PENDING"},
            "Phase 2 (Windows Update)": {"start": None, "end": None, "duration_sec": None, "status": "PENDING", "rounds": [], "reboots": []},
            "Phase 3 (Brendiranje & Optimizacije)": {"start": None, "end": None, "duration_sec": None, "status": "PENDING"},
            "Phase 4 (Programi & Aktivacija)": {"start": None, "end": None, "duration_sec": None, "status": "PENDING"},
            "Phase 5 (Termalni Stres Test)": {"start": None, "end": None, "duration_sec": None, "status": "PENDING"},
            "Phase 6 (Ciscenje & Zavrsetak)": {"start": None, "end": None, "duration_sec": None, "status": "PENDING"}
        }

        # Aplikacije
        self.apps = {
            "Google Chrome": {"status": "NIJE_PRONADJENO", "duration_sec": None, "exit_code": None, "details": ""},
            "WinRAR": {"status": "NIJE_PRONADJENO", "duration_sec": None, "exit_code": None, "details": ""},
            "Adobe Acrobat Pro": {"status": "NIJE_PRONADJENO", "duration_sec": None, "exit_code": None, "details": ""},
            "Microsoft Office": {"status": "NIJE_PRONADJENO", "duration_sec": None, "exit_code": None, "details": ""},
            ".NET Desktop Runtime": {"status": "NIJE_PRONADJENO", "duration_sec": None, "exit_code": None, "details": ""},
            "BatteryBar": {"status": "PRESKOCENO", "duration_sec": None, "exit_code": None, "details": "Nema baterije (Desktop/VM)"},
            "OEM Licenca": {"status": "NEAKTIVIRANO", "duration_sec": None, "exit_code": None, "details": ""}
        }

        # Windows Update statistika
        self.wu_summary = {
            "total_discovered": 0,
            "total_installed": 0,
            "total_failed": 0,
            "reboot_required": False,
            "reboot_count": 0,
            "rounds": [],
            "failed_packages": [],
            "installed_packages": [],
            "pnp_missing_pre": None,
            "pnp_missing_post": None,
            "pnp_missing_detail": []
        }

        # Dijagnostika (Greske i Upozorenja)
        self.errors = []
        self.warnings = []
        self.error_counts_by_component = defaultdict(int)
        self.warn_counts_by_component = defaultdict(int)

        # Infrastrukturni logovi
        self.setupcomplete_info = {"present": False, "robocopy_status": "N/A", "details": []}
        self.firstlogon_info = {"present": False, "status": "N/A", "details": []}

# ------------------------------------------------------------------------------
# PRETRAGA I DETEKCIJA LOG FAJLOVA
# ------------------------------------------------------------------------------
def discover_log_files(target_dir=None, specific_file=None):
    discovered = {}

    if specific_file and os.path.exists(specific_file):
        base = os.path.basename(specific_file).lower()
        if "deploy" in base and base.endswith(".jsonl"):
            discovered["servis_deploy.jsonl"] = os.path.abspath(specific_file)
        else:
            discovered["servis_deploy.log"] = os.path.abspath(specific_file)
        return os.path.dirname(os.path.abspath(specific_file)), discovered

    candidate_dirs = []
    if target_dir:
        candidate_dirs.append(os.path.abspath(target_dir))
    candidate_dirs.append(os.getcwd())
    candidate_dirs.append(r"C:\Windows\Logs\WIS")
    candidate_dirs.append(r"C:\setup")

    # Dodaj i moguce USB drajvove na Windows sistemu
    if os.name == 'nt':
        for drive_letter in "DEFGHIJKLMNOPQRSTUVWXYZ":
            candidate_dirs.append(f"{drive_letter}:\\Logs")
            candidate_dirs.append(f"{drive_letter}:\\")

    resolved_dir = None
    for d in candidate_dirs:
        if os.path.exists(d):
            try:
                files = os.listdir(d)
            except Exception:
                continue
            for f in files:
                f_lower = f.lower()
                full = os.path.join(d, f)
                if f_lower == "servis_deploy.log" and "servis_deploy.log" not in discovered:
                    discovered["servis_deploy.log"] = full
                    if not resolved_dir: resolved_dir = d
                elif f_lower.startswith("servis_deploy_") and f_lower.endswith(".log") and "servis_deploy.log" not in discovered:
                    discovered["servis_deploy.log"] = full
                    if not resolved_dir: resolved_dir = d
                elif f_lower == "servis_deploy.jsonl" and "servis_deploy.jsonl" not in discovered:
                    discovered["servis_deploy.jsonl"] = full
                elif f_lower == "setupcomplete.log" and "setupcomplete.log" not in discovered:
                    discovered["setupcomplete.log"] = full
                elif f_lower == "firstlogon.log" and "firstlogon.log" not in discovered:
                    discovered["firstlogon.log"] = full

            if "servis_deploy.log" in discovered:
                resolved_dir = d
                break

    return resolved_dir or os.getcwd(), discovered

# ------------------------------------------------------------------------------
# PARSIRANJE TEKSTUALNOG LOGA (servis_deploy.log)
# ------------------------------------------------------------------------------
def parse_text_log(filepath, analysis):
    if not os.path.exists(filepath):
        return

    analysis.log_files["servis_deploy.log"] = filepath
    line_regex = re.compile(r'^\[(?P<ts>\d{4}-\d{2}-\d{2}\s+\d{2}:\d{2}:\d{2}(?:\.\d{3})?)\]\s+\[(?P<level>[A-Z]+)\]\s+\[(?P<comp>[^\]]+)\]\s*(?P<msg>.*)$')

    current_round = 0
    current_round_info = None
    last_reboot_request_time = None

    with open(filepath, 'r', encoding='utf-8', errors='replace') as f:
        lines = f.readlines()

    first_ts = None
    last_ts = None

    for idx, line in enumerate(lines, 1):
        line_str = line.strip()
        if not line_str:
            continue

        match = line_regex.match(line_str)
        if not match:
            continue

        ts_str = match.group("ts")
        level = match.group("level")
        comp = match.group("comp")
        msg = match.group("msg")
        ts = parse_timestamp(ts_str)

        if ts:
            if first_ts is None: first_ts = ts
            last_ts = ts

        if level == "ERROR":
            analysis.errors.append({
                "line": idx,
                "file": os.path.basename(filepath),
                "timestamp": ts_str,
                "component": comp,
                "message": msg
            })
            analysis.error_counts_by_component[comp] += 1
        elif level == "WARN":
            analysis.warnings.append({
                "line": idx,
                "file": os.path.basename(filepath),
                "timestamp": ts_str,
                "component": comp,
                "message": msg
            })
            analysis.warn_counts_by_component[comp] += 1

        if comp == "Init":
            if "POKRENUT WIS SERVICE" in msg:
                if analysis.phases["Phase 1 (Init & Mreza)"]["start"] is None:
                    analysis.phases["Phase 1 (Init & Mreza)"]["start"] = ts
                else:
                    if last_reboot_request_time and ts:
                        reboot_duration = (ts - last_reboot_request_time).total_seconds()
                        analysis.phases["Phase 2 (Windows Update)"]["reboots"].append({
                            "requested_at": last_reboot_request_time.strftime("%H:%M:%S"),
                            "resumed_at": ts.strftime("%H:%M:%S"),
                            "duration_sec": reboot_duration
                        })
                        analysis.wu_summary["reboot_count"] += 1
                        last_reboot_request_time = None

            if "Racunar:" in msg:
                m = re.search(r'Racunar:\s*([^\|]+)\|\s*Korisnik:\s*([^\|]+)\|\s*ScriptPath:\s*(.*)', msg)
                if m:
                    analysis.metadata["computer_name"] = m.group(1).strip()
                    analysis.metadata["user_name"] = m.group(2).strip()
                    analysis.metadata["script_path"] = m.group(3).strip()

            if "PowerShell verzija:" in msg:
                m = re.search(r'PowerShell verzija:\s*([^\|]+)\|\s*CLR:\s*(.*)', msg)
                if m:
                    analysis.metadata["ps_version"] = m.group(1).strip()
                    analysis.metadata["clr_version"] = m.group(2).strip()

            if "Pronadjen USB izvor na drajvu:" in msg:
                analysis.metadata["usb_source"] = msg.split(":")[-1].strip()

        if "Pokrecem Windows Update skeniranje" in msg:
            if analysis.phases["Phase 1 (Init & Mreza)"]["end"] is None and ts:
                analysis.phases["Phase 1 (Init & Mreza)"]["end"] = ts
                analysis.phases["Phase 1 (Init & Mreza)"]["status"] = "SUCCESS"

            if analysis.phases["Phase 2 (Windows Update)"]["start"] is None:
                analysis.phases["Phase 2 (Windows Update)"]["start"] = ts
                analysis.phases["Phase 2 (Windows Update)"]["status"] = "IN_PROGRESS"

            m = re.search(r'Krug\s+(\d+)/(\d+)', msg)
            if m:
                current_round = int(m.group(1))
                current_round_info = {
                    "round": current_round,
                    "start": ts,
                    "end": None,
                    "packages_count": 0,
                    "installed": 0,
                    "failed": 0
                }
                analysis.phases["Phase 2 (Windows Update)"]["rounds"].append(current_round_info)

        if "Otkriveno" in msg and "uredjaja bez drajvera" in msg:
            m = re.search(r'Otkriveno\s+(\d+)\s+uredjaja bez drajvera', msg)
            if m:
                count = int(m.group(1))
                if analysis.wu_summary["pnp_missing_pre"] is None:
                    analysis.wu_summary["pnp_missing_pre"] = count
                analysis.wu_summary["pnp_missing_post"] = count

        if "Instaliram paket" in msg:
            m = re.search(r'Instaliram paket\s+(\d+)\s+od\s+(\d+):\s*(.*)', msg)
            if m:
                p_tot = int(m.group(2))
                analysis.wu_summary["total_discovered"] = max(analysis.wu_summary["total_discovered"], p_tot)
                if current_round_info:
                    current_round_info["packages_count"] = max(current_round_info["packages_count"], p_tot)

        if "Greska pri instalaciji WU paketa" in msg:
            m = re.search(r'Greska pri instalaciji WU paketa\s+([^:]+):\s*(.*)', msg)
            if m:
                pkg = m.group(1).strip()
                err = m.group(2).strip()
                analysis.wu_summary["total_failed"] += 1
                analysis.wu_summary["failed_packages"].append({"round": current_round, "package": pkg, "error": err})
                if current_round_info:
                    current_round_info["failed"] += 1

        if "RESTARTUJEM RACUNAR" in msg:
            last_reboot_request_time = ts
            analysis.wu_summary["reboot_required"] = True
            if current_round_info and current_round_info["end"] is None:
                current_round_info["end"] = ts

        if "SISTEM JE AZURIRAN" in msg or ("Zavrseno" in msg and "azuriranja" in msg):
            if current_round_info and current_round_info["end"] is None:
                current_round_info["end"] = ts

        if "Primenjujem sistemske optimizacije" in msg:
            if analysis.phases["Phase 2 (Windows Update)"]["end"] is None and ts:
                analysis.phases["Phase 2 (Windows Update)"]["end"] = ts
                analysis.phases["Phase 2 (Windows Update)"]["status"] = "SUCCESS"
            analysis.phases["Phase 3 (Brendiranje & Optimizacije)"]["start"] = ts
            analysis.phases["Phase 3 (Brendiranje & Optimizacije)"]["status"] = "IN_PROGRESS"

            m = re.search(r'podesavam brend\s+([A-Za-z0-9_\-]+)', msg)
            if m:
                analysis.metadata["brand"] = m.group(1).strip()

        if "Optimizacija sistema uspesna" in msg:
            analysis.phases["Phase 3 (Brendiranje & Optimizacije)"]["end"] = ts
            analysis.phases["Phase 3 (Brendiranje & Optimizacije)"]["status"] = "SUCCESS"

        if "POCETAK FAZE 4" in msg or "Pripremam sistem za instalaciju aplikacija" in msg:
            if analysis.phases["Phase 3 (Brendiranje & Optimizacije)"]["end"] is None and ts:
                analysis.phases["Phase 3 (Brendiranje & Optimizacije)"]["end"] = ts
                analysis.phases["Phase 3 (Brendiranje & Optimizacije)"]["status"] = "SUCCESS"
            analysis.phases["Phase 4 (Programi & Aktivacija)"]["start"] = ts
            analysis.phases["Phase 4 (Programi & Aktivacija)"]["status"] = "IN_PROGRESS"

        if comp == "DotNet":
            if "nije pronadjen" in msg:
                analysis.apps[".NET Desktop Runtime"]["status"] = "NIJE_PRONADJENO"
                analysis.apps[".NET Desktop Runtime"]["details"] = "Instalacioni paket nedostaje"
            elif "uspesno instaliran" in msg:
                analysis.apps[".NET Desktop Runtime"]["status"] = "INSTALIRANO"
            elif "Greska" in msg:
                analysis.apps[".NET Desktop Runtime"]["status"] = "GRESKA"

        if comp == "BatteryBar":
            if "Preskacem BatteryBar" in msg or "Detektovan stoni racunar" in msg:
                analysis.apps["BatteryBar"]["status"] = "PRESKOCENO"
                analysis.apps["BatteryBar"]["details"] = "Desktop/VM bez baterije"
            elif "uspesno instaliran" in msg:
                analysis.apps["BatteryBar"]["status"] = "INSTALIRANO"
                analysis.apps["BatteryBar"]["details"] = "Instalirano na laptopu"

        if comp == "Chrome":
            if "nije pronadjen" in msg:
                analysis.apps["Google Chrome"]["status"] = "NIJE_PRONADJENO"
                analysis.apps["Google Chrome"]["details"] = "MSI paket nedostaje"
            elif "uspesno instaliran" in msg:
                analysis.apps["Google Chrome"]["status"] = "INSTALIRANO"
            elif "Greska" in msg:
                analysis.apps["Google Chrome"]["status"] = "GRESKA"

        if comp == "WinRAR":
            if "nije pronadjen" in msg:
                analysis.apps["WinRAR"]["status"] = "NIJE_PRONADJENO"
                analysis.apps["WinRAR"]["details"] = "Instaler nedostaje"
            elif "uspesno instaliran" in msg:
                analysis.apps["WinRAR"]["status"] = "INSTALIRANO"
            elif "Greska" in msg:
                analysis.apps["WinRAR"]["status"] = "GRESKA"

        if comp == "Acrobat":
            if "nije pronadjen" in msg:
                analysis.apps["Adobe Acrobat Pro"]["status"] = "NIJE_PRONADJENO"
                analysis.apps["Adobe Acrobat Pro"]["details"] = "Setup paket nedostaje"
            elif "uspesno instaliran" in msg:
                analysis.apps["Adobe Acrobat Pro"]["status"] = "INSTALIRANO"
            elif "Greska" in msg:
                analysis.apps["Adobe Acrobat Pro"]["status"] = "GRESKA"

        if comp == "Office":
            if "nije pronadjen" in msg:
                analysis.apps["Microsoft Office"]["status"] = "NIJE_PRONADJENO"
                analysis.apps["Microsoft Office"]["details"] = "C2R ODT paket nedostaje"
            elif "uspesno instaliran" in msg:
                analysis.apps["Microsoft Office"]["status"] = "INSTALIRANO"
            elif "Greska" in msg:
                analysis.apps["Microsoft Office"]["status"] = "GRESKA"

        if comp in ("License", "Activation", "Dashboard"):
            if "Proveravam status sistemskih licenci" in msg:
                analysis.apps["OEM Licenca"]["status"] = "POKRENUTO"
            if "Zavrsni Windows status:" in msg:
                if ("Licenciran" in msg or "Aktiviran" in msg) and "Nije" not in msg:
                    analysis.apps["OEM Licenca"]["status"] = "AKTIVIRANO"
                    analysis.apps["OEM Licenca"]["details"] = "Windows licenca potvrđena"
                else:
                    if analysis.apps["OEM Licenca"]["status"] != "AKTIVIRANO":
                        analysis.apps["OEM Licenca"]["status"] = "NEAKTIVIRANO"
                        analysis.apps["OEM Licenca"]["details"] = "Nije aktiviran (zahteva internet/server)"

        if "Zavrsen proces" in msg and "ExitCode:" in msg:
            m = re.search(r'Zavrsen proces "([^"]+)" za ([\d\.]+)s\.\s*ExitCode:\s*(-?\d+)', msg)
            if m:
                proc_path = m.group(1)
                dur = float(m.group(2))
                code = int(m.group(3))

                proc_lower = proc_path.lower()
                target_app = None
                if "chrome" in proc_lower: target_app = "Google Chrome"
                elif "winrar" in proc_lower: target_app = "WinRAR"
                elif "acrobat" in proc_lower: target_app = "Adobe Acrobat Pro"
                elif "office" in proc_lower or "setup.exe" in proc_lower: target_app = "Microsoft Office"
                elif "runtime" in proc_lower or "dotnet" in proc_lower: target_app = ".NET Desktop Runtime"
                elif "battery" in proc_lower: target_app = "BatteryBar"

                if target_app and target_app in analysis.apps:
                    analysis.apps[target_app]["duration_sec"] = dur
                    analysis.apps[target_app]["exit_code"] = code
                    if code == 0:
                        analysis.apps[target_app]["status"] = "INSTALIRANO"
                    elif code in (3010, 1641):
                        analysis.apps[target_app]["status"] = "INSTALIRANO (RESTART)"
                    else:
                        analysis.apps[target_app]["status"] = "GRESKA"

        if "Faza 4 uspesno zavrsena" in msg:
            analysis.phases["Phase 4 (Programi & Aktivacija)"]["end"] = ts
            analysis.phases["Phase 4 (Programi & Aktivacija)"]["status"] = "SUCCESS"

        if "FAZA 5: TERMALNI STRES TEST" in msg or "Zapocinjem 30-sekundni termalni stres test" in msg:
            if analysis.phases["Phase 4 (Programi & Aktivacija)"]["end"] is None and ts:
                analysis.phases["Phase 4 (Programi & Aktivacija)"]["end"] = ts
                analysis.phases["Phase 4 (Programi & Aktivacija)"]["status"] = "SUCCESS"
            analysis.phases["Phase 5 (Termalni Stres Test)"]["start"] = ts
            analysis.phases["Phase 5 (Termalni Stres Test)"]["status"] = "IN_PROGRESS"

        if "Faza 5 uspesno zavrsena" in msg or "Termalni stres test zavrsen" in msg:
            analysis.phases["Phase 5 (Termalni Stres Test)"]["end"] = ts
            analysis.phases["Phase 5 (Termalni Stres Test)"]["status"] = "SUCCESS"

        if "FAZA 6: ZAVRSETAK" in msg or "SVE FAZE ZAVRSENE" in msg or "Pokrecem zavrsno ciscenje" in msg:
            if analysis.phases["Phase 5 (Termalni Stres Test)"]["start"] and analysis.phases["Phase 5 (Termalni Stres Test)"]["end"] is None and ts:
                analysis.phases["Phase 5 (Termalni Stres Test)"]["end"] = ts
                analysis.phases["Phase 5 (Termalni Stres Test)"]["status"] = "SUCCESS"
            if analysis.phases["Phase 6 (Ciscenje & Zavrsetak)"]["start"] is None:
                analysis.phases["Phase 6 (Ciscenje & Zavrsetak)"]["start"] = ts
            analysis.phases["Phase 6 (Ciscenje & Zavrsetak)"]["status"] = "SUCCESS"
            analysis.phases["Phase 6 (Ciscenje & Zavrsetak)"]["end"] = ts

    analysis.start_time = first_ts
    analysis.end_time = last_ts
    if first_ts and last_ts:
        analysis.total_duration_sec = (last_ts - first_ts).total_seconds()

def parse_jsonl_log(filepath, analysis):
    if not os.path.exists(filepath):
        return

    analysis.log_files["servis_deploy.jsonl"] = filepath

    with open(filepath, 'r', encoding='utf-8', errors='replace') as f:
        for idx, line in enumerate(f, 1):
            line_str = line.strip()
            if not line_str:
                continue
            try:
                data = json.loads(line_str)
            except Exception:
                continue

            event = data.get("event")
            app_name = data.get("app")
            exit_code = data.get("exit_code")
            dur = data.get("duration_sec")

            if app_name and app_name in analysis.apps:
                if dur is not None: analysis.apps[app_name]["duration_sec"] = float(dur)
                if exit_code is not None: analysis.apps[app_name]["exit_code"] = int(exit_code)
                status_raw = data.get("status")
                if status_raw: analysis.apps[app_name]["status"] = status_raw

            if event == "PNP_MISSING_PRE":
                items = data.get("data", [])
                analysis.wu_summary["pnp_missing_pre"] = len(items) if isinstance(items, list) else items
                if isinstance(items, list): analysis.wu_summary["pnp_missing_detail"] = items
            elif event == "PNP_MISSING_POST":
                items = data.get("data", [])
                analysis.wu_summary["pnp_missing_post"] = len(items) if isinstance(items, list) else items

def parse_aux_logs(discovered, analysis):
    if "setupcomplete.log" in discovered:
        path = discovered["setupcomplete.log"]
        analysis.setupcomplete_info["present"] = True
        try:
            with open(path, 'r', encoding='utf-8', errors='replace') as f:
                content = f.read()
                if "ROBOCOPY" in content or "robocopy" in content:
                    if "ExitCode: 0" in content or "ExitCode: 1" in content or "100%" in content:
                        analysis.setupcomplete_info["robocopy_status"] = "USPESNO"
                    else:
                        analysis.setupcomplete_info["robocopy_status"] = "IZVRSENO"
                analysis.setupcomplete_info["details"].append(f"Pronadjen log: {os.path.basename(path)} ({len(content)} bajtova)")
        except Exception:
            pass

    if "firstlogon.log" in discovered:
        path = discovered["firstlogon.log"]
        analysis.firstlogon_info["present"] = True
        try:
            with open(path, 'r', encoding='utf-8', errors='replace') as f:
                content = f.read()
                analysis.firstlogon_info["status"] = "POKRENUTO / IZVRSENO"
                analysis.firstlogon_info["details"].append(f"Pronadjen log: {os.path.basename(path)} ({len(content)} bajtova)")
        except Exception:
            pass

def compute_durations(analysis):
    for phase_name, p in analysis.phases.items():
        if p["start"] and p["end"]:
            p["duration_sec"] = max(0.0, (p["end"] - p["start"]).total_seconds())

def render_console(analysis, c, verbose=False):
    W = 80
    print(c.CYAN + c.BOLD + "=" * W + c.RESET)
    print(c.CYAN + c.BOLD + "   WIS DEPLOYMENT & TELEMETRY LOG ANALYZER | QA Dijagnosticki Izvestaj" + c.RESET)
    print(c.CYAN + c.BOLD + "=" * W + c.RESET)

    print(c.BOLD + "\n[OSNOVNE INFORMACIJE O SISTEMU I SESIJI]" + c.RESET)
    print(f" Racunar:          {c.WHITE}{analysis.metadata['computer_name']}{c.RESET}")
    print(f" Korisnik:         {c.WHITE}{analysis.metadata['user_name']}{c.RESET}")
    print(f" Brend masine:     {c.CYAN}{analysis.metadata['brand']}{c.RESET}")
    print(f" PowerShell / CLR: {analysis.metadata['ps_version']} (CLR {analysis.metadata['clr_version']})")
    print(f" USB izvor:        {analysis.metadata['usb_source']}")

    start_s = analysis.start_time.strftime('%Y-%m-%d %H:%M:%S') if analysis.start_time else "N/A"
    end_s = analysis.end_time.strftime('%Y-%m-%d %H:%M:%S') if analysis.end_time else "N/A"
    total_dur_s = format_duration(analysis.total_duration_sec)

    print(f" Pocetak procesa:  {c.WHITE}{start_s}{c.RESET}")
    print(f" Kraj procesa:     {c.WHITE}{end_s}{c.RESET}")
    print(f" Ukupno trajanje:  {c.GREEN if analysis.total_duration_sec > 0 else c.YELLOW}{c.BOLD}{total_dur_s}{c.RESET}")

    print(c.BOLD + "\n[TRAJANJE PO FAZAMA DEPLOY PIPELINE-A]" + c.RESET)
    print(c.GRAY + f" {'Faza':<38} | {'Status':<12} | {'Trajanje':<15}" + c.RESET)
    print(c.GRAY + "-" * W + c.RESET)

    for phase_name, p in analysis.phases.items():
        dur = format_duration(p["duration_sec"])
        status = p["status"]
        if status == "SUCCESS":
            status_color = c.GREEN + "[ZAVRSENO]" + c.RESET
        elif status == "IN_PROGRESS":
            status_color = c.YELLOW + "[U TOKU]" + c.RESET
        else:
            status_color = c.GRAY + f"[{status}]" + c.RESET

        print(f" {phase_name:<38} | {status_color:<21} | {dur:<15}")

    reboots = analysis.phases["Phase 2 (Windows Update)"]["reboots"]
    if reboots:
        print(c.CYAN + f"\n -> Detektovano restarta sistema: {len(reboots)}" + c.RESET)
        for r_idx, rb in enumerate(reboots, 1):
            dur_str = format_duration(rb["duration_sec"])
            print(f"    * Restart #{r_idx}: Zatrazan u {rb['requested_at']}, nastavljen u {rb['resumed_at']} (prekid trajao: {dur_str})")

    print(c.BOLD + "\n[STATUS INSTALACIJE SERVISNIH APLIKACIJA]" + c.RESET)
    print(c.GRAY + f" {'Program':<24} | {'Status':<18} | {'Trajanje':<10} | {'Exit':<6} | {'Detalji'}" + c.RESET)
    print(c.GRAY + "-" * W + c.RESET)

    for app_name, app_info in analysis.apps.items():
        st = app_info["status"]
        dur = format_duration(app_info["duration_sec"])
        code = str(app_info["exit_code"]) if app_info["exit_code"] is not None else "-"
        det = app_info["details"]

        if st in ("INSTALIRANO", "AKTIVIRANO"):
            st_fmt = c.GREEN + f"[OK] {st}" + c.RESET
        elif st in ("PRESKOCENO",):
            st_fmt = c.YELLOW + f"[~] {st}" + c.RESET
        elif st in ("NIJE_PRONADJENO", "NEAKTIVIRANO"):
            st_fmt = c.YELLOW + f"[-] {st}" + c.RESET
        else:
            st_fmt = c.RED + f"[!] {st}" + c.RESET

        print(f" {app_name:<24} | {st_fmt:<27} | {dur:<10} | {code:<6} | {det}")

    print(c.BOLD + "\n[ANALIZA WINDOWS UPDATE I HARDVERSKIH DRAJVERA]" + c.RESET)
    wu = analysis.wu_summary
    pre = wu["pnp_missing_pre"] if wu["pnp_missing_pre"] is not None else "N/A"
    post = wu["pnp_missing_post"] if wu["pnp_missing_post"] is not None else "N/A"
    print(f" Nedostajucih drajvera na pocetku: {c.YELLOW}{pre}{c.RESET} | Na kraju: {c.GREEN if post == 0 else c.YELLOW}{post}{c.RESET}")
    print(f" Ukupno detektovano WU paketa:     {wu['total_discovered']}")
    print(f" Paketa sa greskom u logu:         {c.RED if wu['total_failed'] > 0 else c.GREEN}{wu['total_failed']}{c.RESET}")

    if wu["failed_packages"]:
        print(c.RED + " Paketi koji su prijavili gresku tokom instalacije:" + c.RESET)
        for fp in wu["failed_packages"][:5]:
            print(f"  - (Krug {fp['round']}) {fp['package']}: {fp['error'][:60]}...")
        if len(wu["failed_packages"]) > 5:
            print(f"  ... i jos {len(wu['failed_packages']) - 5} neuspesnih paketa.")

    print(c.BOLD + "\n[DIJAGNOSTIKA LOGOVA - ERROR I WARN STATISTIKA]" + c.RESET)
    err_count = len(analysis.errors)
    warn_count = len(analysis.warnings)
    print(f" Ukupno ERROR unosa: {c.RED + c.BOLD if err_count > 0 else c.GREEN}{err_count}{c.RESET}")
    print(f" Ukupno WARN unosa:  {c.YELLOW + c.BOLD if warn_count > 0 else c.GREEN}{warn_count}{c.RESET}")

    if err_count > 0:
        print(c.RED + "\n[PRIKAZ KRITICNIH GRESAKA (ERROR)]" + c.RESET)
        for e in analysis.errors[:8]:
            print(f"  {c.RED}Linija {e['line']:<4}{c.RESET} [{e['timestamp']}] [{e['component']}] {e['message']}")
        if err_count > 8:
            print(c.GRAY + f"  ... i jos {err_count - 8} gresaka (pogledajte izvestaj za kompletan spisak)." + c.RESET)

    if verbose and warn_count > 0:
        print(c.YELLOW + "\n[PRIKAZ UPOZORENJA (WARN)]" + c.RESET)
        for w in analysis.warnings[:10]:
            print(f"  Linija {w['line']:<4} [{w['timestamp']}] [{w['component']}] {w['message']}")

    print("\n" + c.CYAN + "=" * W + c.RESET)

def export_markdown(analysis, out_path):
    with open(out_path, 'w', encoding='utf-8') as f:
        f.write("# WIS Deployment & Telemetry Dijagnosticki Izvestaj\n\n")
        f.write(f"- **Datum analize:** {datetime.now().strftime('%Y-%m-%d %H:%M:%S')}\n")
        f.write(f"- **Racunar:** `{analysis.metadata['computer_name']}`\n")
        f.write(f"- **Korisnik:** `{analysis.metadata['user_name']}`\n")
        f.write(f"- **Brend:** `{analysis.metadata['brand']}`\n")
        f.write(f"- **PowerShell:** `{analysis.metadata['ps_version']}`\n")
        f.write(f"- **Pocetak:** `{analysis.start_time}`\n")
        f.write(f"- **Kraj:** `{analysis.end_time}`\n")
        f.write(f"- **Ukupno trajanje:** **{format_duration(analysis.total_duration_sec)}**\n\n")

        f.write("## 1. Trajanje po Fazama Pipeline-a\n\n")
        f.write("| Faza | Status | Pocetak | Kraj | Trajanje |\n")
        f.write("| :--- | :---: | :---: | :---: | :---: |\n")
        for phase_name, p in analysis.phases.items():
            p_s = p["start"].strftime("%H:%M:%S") if p["start"] else "-"
            p_e = p["end"].strftime("%H:%M:%S") if p["end"] else "-"
            p_d = format_duration(p["duration_sec"])
            f.write(f"| {phase_name} | `{p['status']}` | {p_s} | {p_e} | **{p_d}** |\n")

        f.write("\n## 2. Status Servisnih Aplikacija\n\n")
        f.write("| Program | Status | Trajanje | Exit Code | Detalji |\n")
        f.write("| :--- | :---: | :---: | :---: | :--- |\n")
        for app_name, app_info in analysis.apps.items():
            dur = format_duration(app_info["duration_sec"])
            code = app_info["exit_code"] if app_info["exit_code"] is not None else "-"
            f.write(f"| {app_name} | `{app_info['status']}` | {dur} | `{code}` | {app_info['details']} |\n")

        f.write("\n## 3. Windows Update i Drajveri\n\n")
        wu = analysis.wu_summary
        f.write(f"- **Nedostajuci drajveri (Pre / Posle):** `{wu['pnp_missing_pre']}` / `{wu['pnp_missing_post']}`\n")
        f.write(f"- **Ukupno paketa obradjeno:** {wu['total_discovered']}\n")
        f.write(f"- **Paketa sa greskom:** {wu['total_failed']}\n")
        f.write(f"- **Broj restarta:** {wu['reboot_count']}\n\n")

        if wu["failed_packages"]:
            f.write("### Neuspesni paketi:\n")
            for fp in wu["failed_packages"]:
                f.write(f"- **Krug {fp['round']}** - `{fp['package']}`: {fp['error']}\n")

        f.write(f"\n## 4. Dijagnostika Gresaka (ERROR: {len(analysis.errors)}, WARN: {len(analysis.warnings)})\n\n")
        if analysis.errors:
            f.write("| Linija | Timestamp | Komponenta | Poruka |\n")
            f.write("| :---: | :---: | :--- | :--- |\n")
            for e in analysis.errors:
                clean_msg = e['message'].replace('|', '\\|')
                f.write(f"| {e['line']} | {e['timestamp']} | `{e['component']}` | {clean_msg} |\n")
        else:
            f.write("> [!NOTE]\n> Nisu zabelezene kriticne ERROR poruke u analiziranim logovima.\n")

def export_json(analysis, out_path):
    serializable = {
        "metadata": analysis.metadata,
        "start_time": str(analysis.start_time),
        "end_time": str(analysis.end_time),
        "total_duration_sec": analysis.total_duration_sec,
        "phases": {
            k: {
                "status": v["status"],
                "duration_sec": v["duration_sec"],
                "start": str(v["start"]),
                "end": str(v["end"])
            } for k, v in analysis.phases.items()
        },
        "apps": analysis.apps,
        "windows_update": analysis.wu_summary,
        "errors_count": len(analysis.errors),
        "warnings_count": len(analysis.warnings),
        "errors": analysis.errors,
        "warnings": analysis.warnings
    }
    with open(out_path, 'w', encoding='utf-8') as f:
        json.dump(serializable, f, indent=2, ensure_ascii=False, default=str)

def main():
    parser = argparse.ArgumentParser(description="WIS Deployment & Telemetry Log Analyzer")
    parser.add_argument("-d", "--log-dir", help="Direktorijum sa logovima (default: C:\\Windows\\Logs\\WIS ili tekuci)")
    parser.add_argument("-f", "--file", help="Direktna putanja do log fajla (servis_deploy.log ili jsonl)")
    parser.add_argument("-o", "--output-report", help="Putanja za izvoz izvestaja (.md ili .json)")
    parser.add_argument("--no-color", action="store_true", help="Iskljucuje ANSI boje u konzoli")
    parser.add_argument("-v", "--verbose", action="store_true", help="Prikazuje detaljnija upozorenja i stack trace")

    args = parser.parse_args()
    c = Colors(enabled=not args.no_color)

    resolved_dir, discovered = discover_log_files(args.log_dir, args.file)
    if not discovered:
        print(c.RED + f" [FAIL] Nisu pronadjeni WIS log fajlovi u direktorijumu: {resolved_dir}" + c.RESET)
        sys.exit(1)

    analysis = DeployAnalysis()
    analysis.source_dir = resolved_dir

    if "servis_deploy.log" in discovered:
        parse_text_log(discovered["servis_deploy.log"], analysis)

    if "servis_deploy.jsonl" in discovered:
        parse_jsonl_log(discovered["servis_deploy.jsonl"], analysis)

    parse_aux_logs(discovered, analysis)
    compute_durations(analysis)

    render_console(analysis, c, verbose=args.verbose)

    if args.output_report:
        out = args.output_report
        if out.lower().endswith(".json"):
            export_json(analysis, out)
            print(f" [INFO] JSON izvestaj uspesno sacuvan: {out}")
        else:
            export_markdown(analysis, out)
            print(f" [INFO] Markdown izvestaj uspesno sacuvan: {out}")

    sys.exit(0)

if __name__ == "__main__":
    main()
