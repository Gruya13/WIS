#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
GWADT Terminal TUI Fleet Watcher (hub_cli.py)
==============================================
Konzolni terminal monitor za prikaz statusa instalacija u servisu WIS Service.
Povezuje se na lokalni GWADT Hub (default http://127.0.0.1:8080) i osvežava
tabelu uređaja ili prati logove uživo direktno u terminalu.

Upotreba:
    python3 hub_cli.py              # Prikaz pregledne tabele flote
    python3 hub_cli.py --watch <ID> # Uživo praćenje loga mašine (tail -f)
"""

import sys
import time
import json
import urllib.request
import os

DEFAULT_HUB_URL = "http://127.0.0.1:8080"

# ANSI kodovi za boje
C_RESET = "\033[0m"
C_BOLD = "\033[1m"
C_CYAN = "\033[36m"
C_GREEN = "\033[32m"
C_YELLOW = "\033[33m"
C_RED = "\033[31m"
C_GRAY = "\033[90m"
C_WHITE = "\033[97m"

def fetch_json(url):
    try:
        req = urllib.request.Request(url, headers={"User-Agent": "GWADT-CLI"})
        with urllib.request.urlopen(req, timeout=2.0) as resp:
            return json.loads(resp.read().decode("utf-8"))
    except Exception:
        return None

def clear_screen():
    sys.stdout.write("\033[2J\033[H")
    sys.stdout.flush()

def show_fleet_table(hub_url=DEFAULT_HUB_URL):
    spinner = ['|', '/', '-', '\\']
    frame = 0

    while True:
        data = fetch_json(f"{hub_url}/api/devices")
        clear_screen()
        pulse = spinner[frame % len(spinner)]
        frame += 1

        width = 95
        print(f"{C_CYAN}+{('=' * (width - 2))}+{C_RESET}")
        print(f"{C_CYAN}| {C_BOLD}WIS SERVICE GWADT FLEET MONITOR{C_RESET}{C_CYAN} [ Aktivno: {C_GREEN}{pulse}{C_CYAN} ]" + (" " * (width - 45)) + f"|{C_RESET}")
        print(f"{C_CYAN}+{('=' * (width - 2))}+{C_RESET}")

        if not data:
            print(f"\n {C_YELLOW}[!] Hub server na {hub_url} nije dostupan ili nema uredjaja.{C_RESET}")
            print(f" {C_GRAY}Pokrenite server: python3 Skripte/ServisHub/hub_server.py{C_RESET}\n")
            time.sleep(2.0)
            continue

        devs = list(data.values())
        print(f" {C_WHITE}{'UREĐAJ':<22} {'S/N / IP':<18} {'FAZA':<14} {'PROG':<7} {'STATUS':<15} {'ZADNJI PING'}{C_RESET}")
        print(f" {C_GRAY}{'-' * (width - 2)}{C_RESET}")

        for d in devs:
            name = d.get("computer_name", "UNKNOWN")
            hw = d.get("hw", {})
            if hw and (hw.get("manufacturer") or hw.get("model")):
                disp_name = f"{hw.get('manufacturer', '')} {hw.get('model', '')}".strip()
            else:
                disp_name = name
            if len(disp_name) > 20:
                disp_name = disp_name[:18] + ".."

            serial = hw.get("serial") or d.get("client_ip", "N/A")
            if len(serial) > 16:
                serial = serial[:14] + ".."

            phase_num = d.get("phase", 1)
            phase_name = d.get("phase_name", "Mreza")
            phase_str = f"F{phase_num}. {phase_name}"

            prog = f"{d.get('worker_progress', 0)}%"
            lbl = d.get("status_label", "Aktivno")
            sec = d.get("last_seen_sec_ago", 0)

            col = C_GREEN if lbl == "Aktivno" else (C_CYAN if lbl == "Zavrseno" else (C_YELLOW if "Restart" in lbl else C_RED))

            print(f" {C_BOLD}{disp_name:<22}{C_RESET} {C_GRAY}{serial:<18}{C_RESET} {phase_str:<14} {prog:<7} {col}{lbl:<15}{C_RESET} {C_GRAY}{sec}s pre{C_RESET}")

        print(f"\n{C_GRAY} Komande: Ctrl+C za izlaz | python3 hub_cli.py --watch <RACUNAR> za log uživo{C_RESET}")
        time.sleep(2.0)

def watch_device_log(device_name, hub_url=DEFAULT_HUB_URL):
    clean_id = "".join(c if c.isalnum() or c in "-_" else "_" for c in device_name)
    print(f"[*] Povezujem se na log za '{clean_id}'...")
    
    import urllib.request
    req = urllib.request.Request(f"{hub_url}/api/stream/{clean_id}")
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
                    else:
                        print(content)
    except KeyboardInterrupt:
        print("\n[OK] Prekid pracenja loga.")
    except Exception as e:
        print(f"[!] Greska pri preuzimanju stream-a: {e}")

if __name__ == "__main__":
    if len(sys.argv) > 2 and sys.argv[1] == "--watch":
        watch_device_log(sys.argv[2])
    else:
        try:
            show_fleet_table()
        except KeyboardInterrupt:
            print("\n[OK] Izlaz iz monitora.")
