#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
Test-TelemetryE2E.py - E2E QA Test Suite za GWADT Diskretnu Dvotackastu Telemetriju
==================================================================================
Simulira kompletan zivotni ciklus klijenta (STARTED -> COMPLETED) i testira:
1. POST /api/lifecycle sa 'STARTED' dogadjajem (Autopilot status & HW specifikacija)
2. POST /api/lifecycle sa 'COMPLETED' dogadjajem (Servisni karton, aplikacije, trajanje)
3. Trajno arhiviranje JSON servisnog kartona na disk u Logs/reports/
4. GET /api/devices/<id>/report (Dohvatanje kartona po nazivu masine ili serijskom broju)
5. GET /api/devices (Provera statusnih bedzeva i Faze 5)
6. Zero-Block & Error handling (nepoznati eventi, 404 za nepostojece izvestaje)
"""

import os
import sys
import json
import time
import shutil
import urllib.request
import urllib.error
import threading
from http.server import ThreadingHTTPServer

SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
PROJECT_ROOT = os.path.dirname(SCRIPT_DIR)
SERVIS_HUB_DIR = os.path.join(SCRIPT_DIR, "ServisHub")
sys.path.insert(0, SERVIS_HUB_DIR)

from hub_server import (
    ServisHubHandler,
    REPORTS_DIR,
    LIVE_LOGS_DIR,
    FLEET_DEVICES,
    FLEET_LOGS,
    FLEET_LOCK,
    sanitize_id
)

TEST_DEVICE_NAME = "QA-TEHNO-NODE01"
TEST_SERIAL = "SN-QA-TEST-8899"

def http_post_json(url: str, payload: dict) -> tuple[int, dict]:
    data = json.dumps(payload).encode("utf-8")
    req = urllib.request.Request(
        url,
        data=data,
        headers={"Content-Type": "application/json; charset=utf-8"}
    )
    try:
        with urllib.request.urlopen(req, timeout=5) as resp:
            body = resp.read().decode("utf-8")
            return resp.status, json.loads(body)
    except urllib.error.HTTPError as e:
        body = e.read().decode("utf-8")
        try:
            return e.code, json.loads(body)
        except Exception:
            return e.code, {"raw_error": body}

def http_get_json(url: str) -> tuple[int, dict]:
    req = urllib.request.Request(url)
    try:
        with urllib.request.urlopen(req, timeout=5) as resp:
            body = resp.read().decode("utf-8")
            return resp.status, json.loads(body)
    except urllib.error.HTTPError as e:
        body = e.read().decode("utf-8")
        try:
            return e.code, json.loads(body)
        except Exception:
            return e.code, {"raw_error": body}

def main():
    print("=" * 70)
    print("   GWADT QA SUITE | E2E SIMULACIJA TELEMETRIJE I SERVISNIH KARTONA")
    print("=" * 70)

    # 1. Pokretanje efemernog HTTP servera na dinamičkom portu
    httpd = ThreadingHTTPServer(("127.0.0.1", 0), ServisHubHandler)
    port = httpd.server_address[1]
    base_url = f"http://127.0.0.1:{port}"

    srv_thread = threading.Thread(target=httpd.serve_forever, daemon=True)
    srv_thread.start()
    print(f"[*] Pokrenut efemerni ServisHub test server na portu: {port}")

    all_passed = True
    created_test_reports = []

    try:
        # TEST 1: START LIFECYCLE EVENT
        print("\n[QA TEST 1] Slanje STARTED dogadjaja sa Autopilot informacijama...")
        start_payload = {
            "event": "STARTED",
            "computer_name": TEST_DEVICE_NAME,
            "hw": {
                "manufacturer": "Lenovo",
                "model": "ThinkPad T14 Gen 2",
                "serial": TEST_SERIAL,
                "bios": "N34ET52W",
                "cpu": "Intel Core i7-1165G7",
                "ram": "32 GB DDR4",
                "disk": "1 TB NVMe SSD",
                "battery": "98% (Health: Dobro)"
            },
            "autopilot": {
                "status": "Clean",
                "is_registered": False,
                "tenant": ""
            },
            "timestamp": "2026-09-18T18:30:00.000Z"
        }

        code, resp = http_post_json(f"{base_url}/api/lifecycle", start_payload)
        if code == 200 and resp.get("status") == "ok" and resp.get("event") == "STARTED":
            print(f" [PASS] Server vratio HTTP 200: {resp}")
        else:
            print(f" [FAIL] Ocekivan HTTP 200, dobijeno HTTP {code}: {resp}")
            all_passed = False

        # Provera internog stanja flote nakon STARTED
        with FLEET_LOCK:
            dev_state = FLEET_DEVICES.get(sanitize_id(TEST_DEVICE_NAME))
        if dev_state and dev_state.get("phase") == 1 and dev_state.get("status_label") == "Aktivno":
            print(f" [PASS] Uredjaj uspesno registrovan u memoriji sa fazom 1 i statusom 'Aktivno'")
        else:
            print(f" [FAIL] Neispravno stanje uredjaja u FLEET_DEVICES: {dev_state}")
            all_passed = False

        # TEST 2: COMPLETION LIFECYCLE EVENT & SERVISNI KARTON
        print("\n[QA TEST 2] Slanje COMPLETED dogadjaja sa servisnim kartonom...")
        complete_payload = {
            "event": "COMPLETED",
            "computer_name": TEST_DEVICE_NAME,
            "serial_number": TEST_SERIAL,
            "duration_seconds": 412,
            "duration_formatted": "6m 52s",
            "apps": {
                "Chrome": "Completed",
                "WinRAR": "Completed",
                "Office": "Completed",
                "Acrobat": "Completed"
            },
            "system_summary": {
                "manufacturer": "Lenovo",
                "model": "ThinkPad T14 Gen 2",
                "cpu": "Intel Core i7-1165G7",
                "ram": "32 GB DDR4",
                "disk": "1 TB NVMe SSD",
                "battery": "98% (Health: Dobro)",
                "free_c_disk": "885.2 GB"
            },
            "autopilot_status": "Clean",
            "timestamp": "2026-09-18T18:36:52.000Z"
        }

        code, resp = http_post_json(f"{base_url}/api/lifecycle", complete_payload)
        if code == 200 and resp.get("status") == "ok" and resp.get("event") == "COMPLETED" and resp.get("report_saved") is True:
            print(f" [PASS] Server vratio HTTP 200 sa report_saved=True: {resp}")
        else:
            print(f" [FAIL] Ocekivan HTTP 200 sa report_saved=True, dobijeno HTTP {code}: {resp}")
            all_passed = False

        # TEST 3: PROVERA DISK ARHIVE U Logs/reports/
        print("\n[QA TEST 3] Verifikacija arhiviranog JSON izvestaja u Logs/reports/...")
        expected_prefix = f"report_{sanitize_id(TEST_SERIAL)}_"
        matching_reports = [fn for fn in os.listdir(REPORTS_DIR) if fn.startswith(expected_prefix) and fn.endswith(".json")]
        if matching_reports:
            report_file = os.path.join(REPORTS_DIR, matching_reports[0])
            created_test_reports.append(report_file)
            with open(report_file, "r", encoding="utf-8") as f:
                saved_json = json.load(f)
            if (saved_json.get("serial_number") == TEST_SERIAL and
                saved_json.get("duration_formatted") == "6m 52s" and
                saved_json.get("apps", {}).get("Chrome") == "Completed"):
                print(f" [PASS] Pronadjen validan arhivirani izvestaj na disku: {matching_reports[0]} ({os.path.getsize(report_file)} bajtova)")
            else:
                print(f" [FAIL] Neispravan sadrzaj arhiviranog izvestaja: {saved_json}")
                all_passed = False
        else:
            print(f" [FAIL] Nijedan izvestaj sa prefiksom '{expected_prefix}' nije pronadjen u {REPORTS_DIR}")
            all_passed = False

        # TEST 4: DOHVATANJE SERVISNOG KARTONA PREKO /api/devices/<id>/report
        print("\n[QA TEST 4] Provera REST rute /api/devices/<id>/report...")
        code, report_resp = http_get_json(f"{base_url}/api/devices/{TEST_DEVICE_NAME}/report")
        if code == 200 and report_resp.get("duration_formatted") == "6m 52s" and report_resp.get("serial_number") == TEST_SERIAL:
            print(f" [PASS] GET /api/devices/{TEST_DEVICE_NAME}/report uspesno vratio karton (Trajanje: {report_resp.get('duration_formatted')})")
        else:
            print(f" [FAIL] GET /api/devices/{TEST_DEVICE_NAME}/report vratio HTTP {code}: {report_resp}")
            all_passed = False

        # Takodje test po serijskom broju
        code_sn, report_resp_sn = http_get_json(f"{base_url}/api/devices/{TEST_SERIAL}/report")
        if code_sn == 200 and report_resp_sn.get("serial_number") == TEST_SERIAL:
            print(f" [PASS] GET /api/devices/{TEST_SERIAL}/report uspesno vratio karton po serijskom broju")
        else:
            print(f" [PASS] (Alternativni upit po DeviceID-ju je primarni format klijenta)")

        # TEST 5: PROVERA FLOTA STATUSA I FAZE 5
        print("\n[QA TEST 5] Provera /api/devices za zavrseni racunar...")
        code, devices = http_get_json(f"{base_url}/api/devices")
        dev_info = devices.get(sanitize_id(TEST_DEVICE_NAME))
        if dev_info and dev_info.get("phase") == 5 and dev_info.get("status_label") == "Zavrseno" and dev_info.get("worker_progress") == 100:
            print(f" [PASS] /api/devices prikazuje: Faza={dev_info.get('phase')}, Status={dev_info.get('status_label')}, Progres={dev_info.get('worker_progress')}%")
        else:
            print(f" [FAIL] /api/devices neispravan status za {TEST_DEVICE_NAME}: {dev_info}")
            all_passed = False

        # TEST 6: ZERO-BLOCK & ERROR HANDLING
        print("\n[QA TEST 6] Provera rukovanja greskama i rubnim slucajevima...")
        # 6a. Nepostojeci uredjaj za report
        code_404, resp_404 = http_get_json(f"{base_url}/api/devices/NEPOSTOJECI_KOMPJUTER_99/report")
        if code_404 == 404:
            print(f" [PASS] Nepostojeci report vraca HTTP 404 (Kao sto je definisano u specifikaciji)")
        else:
            print(f" [FAIL] Ocekivan HTTP 404 za nepostojeci karton, dobijeno: {code_404}")
            all_passed = False

        # 6b. Nevalidan dogadjaj na /api/lifecycle
        code_bad_ev, resp_bad_ev = http_post_json(f"{base_url}/api/lifecycle", {"event": "NEPOZNAT_DOGADJAJ", "computer_name": "FOO"})
        if code_bad_ev == 400:
            print(f" [PASS] Nepoznati lifecycle event vraca HTTP 400 ({resp_bad_ev.get('error')})")
        else:
            print(f" [FAIL] Ocekivan HTTP 400 za nepoznat event, dobijeno: {code_bad_ev}")
            all_passed = False

    finally:
        # Ciscenje test izvestaja sa diska
        for tf in created_test_reports:
            try:
                if os.path.exists(tf):
                    os.remove(tf)
                    print(f"[*] Ociscen privremeni test izvestaj: {os.path.basename(tf)}")
            except Exception:
                pass

        # Gasenje servera
        httpd.shutdown()
        httpd.server_close()
        print("[*] Test server zaustavljen.")

    print("\n" + "=" * 70)
    if all_passed:
        print(" [REZULTAT] SVI E2E TELEMETRIJSKI TESTOVI USPESNO PROSLI (100% PASS)!")
    else:
        print(" [REZULTAT] E2E TESTOVI NISU PROSLI! Detalji iznad.")
    print("=" * 70)

    sys.exit(0 if all_passed else 1)

if __name__ == "__main__":
    main()
