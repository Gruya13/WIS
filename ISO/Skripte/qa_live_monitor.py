#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
GWADT Live QA Debugger Monitor (qa_live_monitor.py)
===================================================
Prati tok instalacije na novom uređaju u realnom vremenu:
- Čeka da se mašina pojavi na ServisHub-u nakon WinPE faze (Specialize / FirstLogon).
- Prati i parsira logove uživo (preko ServisHub API ili Logs/live/*.log).
- Trenutno uočava greške [ERROR], upozorenja [WARN] i izlazne kodove.
- Prikazuje napredak po fazama (Faza 1 - Faza 5) i status svake aplikacije.
"""

import sys
import time
import os
import json
import urllib.request
from datetime import datetime

DEFAULT_HUB_URL = "http://127.0.0.1:8080"
BASE_DIR = os.path.abspath(os.path.join(os.path.dirname(__file__), ".."))
LIVE_LOGS_DIR = os.path.join(BASE_DIR, "Logs", "live")

C_RESET = "\033[0m"
C_BOLD = "\033[1m"
C_GREEN = "\033[32m"
C_YELLOW = "\033[33m"
C_RED = "\033[31m"
C_CYAN = "\033[36m"
C_GRAY = "\033[90m"

def log_event(msg, level="INFO"):
    ts = datetime.now().strftime("%H:%M:%S")
    color = C_GREEN if level == "OK" else (C_RED if level == "ERR" else (C_YELLOW if level == "WARN" else C_CYAN))
    print(f"{C_GRAY}[{ts}]{C_RESET} {color}[{level}]{C_RESET} {msg}", flush=True)

def get_devices(hub_url=DEFAULT_HUB_URL):
    try:
        req = urllib.request.Request(f"{hub_url}/api/devices", headers={"User-Agent": "GWADT-QA-Monitor"})
        with urllib.request.urlopen(req, timeout=1.5) as resp:
            return json.loads(resp.read().decode("utf-8"))
    except Exception:
        return {}

def find_active_device(monitor_start_ts):
    """Pronalazi novog ili aktivnog klijenta koji salje telemetriju."""
    devs = get_devices()
    now_ts = time.time()
    for dev_id, info in devs.items():
        last_seen = info.get("last_seen", 0)
        # Ako je pingovan u poslednjih 20 sekundi (aktivan rad)
        if (now_ts - last_seen) < 20:
            return dev_id, info
        # Ili ako je started_at posle pokretanja ovog monitora
        st_epoch = info.get("last_seen", 0) - info.get("duration_seconds", 0)
        if st_epoch > monitor_start_ts and (now_ts - last_seen) < 60:
            return dev_id, info
    return None, None

def monitor_live():
    print(f"{C_BOLD}{C_CYAN}=== GWADT OPS / QA MONITOR: LIVE INSTALLATION STREAM ==={C_RESET}", flush=True)
    log_event("Cekam klijenta nakon zavrsetka WinPE faze...", "INFO")
    
    monitor_start_ts = time.time()
    active_device = None
    target_log_path = None
    last_file_pos = 0
    poll_count = 0

    while True:
        try:
            if not active_device:
                dev_id, dev_info = find_active_device(monitor_start_ts)
                if dev_id:
                    active_device = dev_id
                    hw = dev_info.get("hw", {})
                    hw_str = f"{hw.get('manufacturer', '')} {hw.get('model', '')} (S/N: {hw.get('serial', 'N/A')})".strip()
                    log_event(f"DETEKTOVAN KLIJENT: {C_BOLD}{dev_id}{C_RESET} | Hardver: {hw_str} | IP: {dev_info.get('client_ip')}", "OK")
                    log_event(f"Pocetni status: Faza {dev_info.get('phase')} ({dev_info.get('phase_name')}) - {dev_info.get('live_status')}", "INFO")
                else:
                    # Alternativno proveri da li je kreiran novi log fajl u Logs/live
                    if os.path.exists(LIVE_LOGS_DIR):
                        recent_logs = []
                        for f in os.listdir(LIVE_LOGS_DIR):
                            if f.endswith(".log"):
                                p = os.path.join(LIVE_LOGS_DIR, f)
                                mtime = os.path.getmtime(p)
                                if (time.time() - mtime) < 30: # Modifikovan u zadnjih 30 sekundi
                                    recent_logs.append((mtime, p, f))
                        if recent_logs:
                            recent_logs.sort(reverse=True)
                            target_log_path = recent_logs[0][1]
                            # Izvadi ime masine
                            # servis_deploy_<Machine>_<Date>.log
                            base_f = recent_logs[0][2]
                            parts = base_f.replace("servis_deploy_", "").split("_")
                            active_device = parts[0] if parts else "DEVICE"
                            log_event(f"Pronadjen aktivan log fajl: {base_f} za uredjaj: {active_device}", "OK")

                if not active_device:
                    poll_count += 1
                    if poll_count % 10 == 0:
                        log_event("I dalje na PE/OOBE restartu... Slusam na portu 8080 i pratim Logs/live...", "INFO")
                    time.sleep(2.0)
                    continue

            # Ako imamo aktivan uredjaj, pronadji njegov log fajl
            if not target_log_path:
                date_str = datetime.now().strftime("%Y%m%d")
                possible_path = os.path.join(LIVE_LOGS_DIR, f"servis_deploy_{active_device}_{date_str}.log")
                if os.path.exists(possible_path):
                    target_log_path = possible_path
                    last_file_pos = 0
                    log_event(f"Povezan na disk log: {os.path.basename(target_log_path)}", "OK")

            # Citaj nove linije iz fajla
            if target_log_path and os.path.exists(target_log_path):
                with open(target_log_path, "r", encoding="utf-8", errors="ignore") as f:
                    f.seek(last_file_pos)
                    new_lines = f.readlines()
                    last_file_pos = f.tell()

                for raw_line in new_lines:
                    line = raw_line.strip()
                    if not line:
                        continue
                    if "[ERROR]" in line or "[CRITICAL]" in line or "ExitCode: [^0]" in line:
                        print(f"{C_RED}{C_BOLD}>>> [GRESKA] {line}{C_RESET}", flush=True)
                    elif "[WARN]" in line:
                        print(f"{C_YELLOW}>>> [UPOZORENJE] {line}{C_RESET}", flush=True)
                    elif "[SUCCESS]" in line:
                        print(f"{C_GREEN}>>> [USPESNO] {line}{C_RESET}", flush=True)
                    elif "[STEP]" in line:
                        print(f"{C_CYAN}>>> [KORAK] {line}{C_RESET}", flush=True)
                    else:
                        print(f"    {line}", flush=True)

            # Proveri status preko API-ja povremeno
            devs = get_devices()
            if active_device in devs:
                info = devs[active_device]
                prog = info.get("worker_progress", 0)
                phase_name = info.get("phase_name", "")
                lbl = info.get("status_label", "")
                if lbl == "Zavrseno" or prog >= 100 or info.get("phase") == 5:
                    log_event(f"INSTALACIJA ZAVRSENA: {active_device} | Progres: 100% | Status: {lbl}", "OK")
                    break

            time.sleep(2.0)

        except KeyboardInterrupt:
            log_event("Monitor zaustavljen od strane korisnika.", "WARN")
            break
        except Exception as e:
            time.sleep(2.0)

if __name__ == "__main__":
    monitor_live()
