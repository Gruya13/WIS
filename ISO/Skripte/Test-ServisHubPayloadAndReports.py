#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
Test-ServisHubPayloadAndReports.py
==================================
Kompletna verifikacija Dinamičkog ServisHub Auto-Packager-a (PayloadManager)
i Fleet Reports REST endpointa (/api/payload/version, /api/payload/download,
POST /api/devices/{id}/report).
"""

import os
import sys
import time
import json
import socket
import hashlib
import zipfile
import urllib.request
import urllib.error
import threading
from datetime import datetime

BASE_DIR = os.path.abspath(os.path.join(os.path.dirname(__file__), ".."))
sys.path.insert(0, os.path.join(BASE_DIR, "Skripte", "ServisHub"))

try:
    from hub_server import (
        PayloadManager,
        ServisHubHandler,
        ThreadingHTTPServer,
        generate_markdown_report,
        REPORTS_DIR,
        CACHE_DIR,
        FLEET_DEVICES,
        FLEET_LOCK
    )
except ImportError as e:
    print(f"❌ Neuspeh uvoza ServisHub modula: {e}")
    sys.exit(1)

def print_header(title):
    print("\n" + "=" * 70)
    print(f" 🧪 {title}")
    print("=" * 70)

def assert_true(cond, msg):
    if cond:
        print(f"  [PASS] {msg}")
    else:
        print(f"  [FAIL] {msg}")
        raise AssertionError(msg)

def run_tests():
    print_header("FAZA 3 TEST: ServisHub PayloadManager & Fleet Reports")

    # -------------------------------------------------------------------------
    # TEST 1: PayloadManager generisanje i keširanje
    # -------------------------------------------------------------------------
    print("\n--- TEST 1: PayloadManager generisanje ZIP-a i meta podataka ---")
    pm = PayloadManager(BASE_DIR)
    meta = pm.ensure_payload(force=True)

    assert_true(isinstance(meta, dict), "Metapodaci su uspešno vraćeni kao rečnik")
    assert_true("version" in meta and len(meta["version"]) >= 8, f"Verzija prisutna: {meta.get('version')}")
    assert_true("sha256" in meta and len(meta["sha256"]) == 64, f"SHA-256 heš prisutan: {meta.get('sha256')}")
    assert_true("size_bytes" in meta and meta["size_bytes"] > 50000, f"Veličina paketa adekvatna: {meta.get('size_bytes')} B")
    assert_true("built_at" in meta, f"Timestamp generisanja prisutan: {meta.get('built_at')}")

    zip_path = pm.payload_zip_path
    meta_path = pm.payload_meta_path
    assert_true(os.path.isfile(zip_path), f"ZIP fajl postoji na disku: {zip_path}")
    assert_true(os.path.isfile(meta_path), f"Meta JSON postoji na disku: {meta_path}")

    # Provera heša na disku
    hasher = hashlib.sha256()
    with open(zip_path, "rb") as f:
        for chunk in iter(lambda: f.read(65536), b""):
            hasher.update(chunk)
    disk_hash = hasher.hexdigest()
    assert_true(disk_hash == meta["sha256"], f"Heš na disku ({disk_hash}) 100% odgovara metapodacima!")
    assert_true(pm.is_payload_current(), "Metoda is_payload_current() vraća True za sveže generisan paket")

    # -------------------------------------------------------------------------
    # TEST 2: Validacija sadržaja gwadt-payload.zip
    # -------------------------------------------------------------------------
    print("\n--- TEST 2: Validacija unutrašnjih fajlova u ZIP arhivu ---")
    with zipfile.ZipFile(zip_path, "r") as zf:
        namelist = set(zf.namelist())

        required_entries = [
            "FirstLogon.ps1",
            "FirstLogon.cmd",
            "Skripte/FirstLogon.ps1",
            "Skripte/FirstLogon.cmd",
            # Modularne fazne skripte (Zadatak 3.1)
            "Phases/Phase1-Network.ps1",
            "Phases/Phase2-Drivers.ps1",
            "Phases/Phase3-Optimization.ps1",
            "Phases/Phase4-Apps.ps1",
            "Phases/Phase5-Finalize.ps1",
            "Skripte/Phases/Phase1-Network.ps1",
            "Skripte/Phases/Phase2-Drivers.ps1",
            "Skripte/Phases/Phase3-Optimization.ps1",
            "Skripte/Phases/Phase4-Apps.ps1",
            "Skripte/Phases/Phase5-Finalize.ps1",
            # PowerShell moduli (uključujući novi GWADT.Common.psm1)
            "Modules/GWADT.Common.psm1",
            "Skripte/Modules/GWADT.Common.psm1",
            "Skripte/Modules/GWADT.Core.psm1",
            "Skripte/Modules/GWADT.Apps.psm1",
            "Skripte/Modules/GWADT.Hardware.psm1",
            "Skripte/Modules/GWADT.Network.psm1",
            "Skripte/Modules/GWADT.Telemetry.psm1",
            "Skripte/Modules/GWADT.UI.psm1",
            "Skripte/Modules/GWADT.Update.psm1",
            "Skripte/GWADT.UI/GWADT.UI.exe",
            "Skripte/GWADT.UI/App.config",
        ]

        # Opcioni legacy monolitni skript (ukoliko postoji)
        if os.path.isfile(os.path.join(BASE_DIR, "Skripte", "Install-ServisApps.ps1")):
            required_entries.extend(["Install-ServisApps.ps1", "Skripte/Install-ServisApps.ps1"])

        for req in required_entries:
            assert_true(req in namelist, f"Obavezni fajl u ZIP-u: {req}")

        print(f"  [INFO] Ukupan broj fajlova/unosa u ZIP arhivu: {len(namelist)}")

    # -------------------------------------------------------------------------
    # TEST 3: Praćenje mtime izmena u Skripte/Phases/ (auto-regeneracija paketa)
    # -------------------------------------------------------------------------
    print("\n--- TEST 3: Praćenje timestamp-a izmena u Skripte/Phases/ (mtime auto-regeneracija) ---")
    old_version = meta["version"]
    test_dummy = os.path.join(BASE_DIR, "Skripte", "Phases", "_temp_phase_trigger.ps1")
    try:
        time.sleep(1.1)
        with open(test_dummy, "w", encoding="utf-8") as f:
            f.write("# trigger mtime for phase change\n")

        assert_true(not pm.is_payload_current(), "Metoda is_payload_current() detektuje izmenu u Skripte/Phases/ i vraća False")
        new_meta = pm.ensure_payload(force=False)
        assert_true(new_meta["version"] != old_version or new_meta["built_at"] != meta["built_at"],
                    f"Detektovana promena mtime u Skripte/Phases/ i paket automatski osvežen: nova verzija {new_meta['version']}")
        assert_true(pm.is_payload_current(), "Nakon osvežavanja, is_payload_current() ponovo vraća True")
    finally:
        if os.path.isfile(test_dummy):
            os.remove(test_dummy)
            # Regeneriši ponovo kako dummy fajl ne bi ostao u kešu
            pm.ensure_payload(force=True)

    # -------------------------------------------------------------------------
    # TEST 4: Pokretanje testnog HTTP servera i verifikacija REST ruta
    # -------------------------------------------------------------------------
    print("\n--- TEST 4: Pokretanje testnog ServisHub HTTP servera ---")
    test_port = 8189
    server = ThreadingHTTPServer(("127.0.0.1", test_port), ServisHubHandler)
    server_thread = threading.Thread(target=server.serve_forever, daemon=True)
    server_thread.start()
    time.sleep(0.3)
    base_url = f"http://127.0.0.1:{test_port}"
    print(f"  [INFO] Testni server aktivan na: {base_url}")

    try:
        # A) GET /api/payload/version
        print("\n--- TEST 4A: GET /api/payload/version ---")
        req = urllib.request.Request(f"{base_url}/api/payload/version")
        with urllib.request.urlopen(req) as resp:
            assert_true(resp.status == 200, f"Status 200 za /api/payload/version")
            body = json.loads(resp.read().decode("utf-8"))
            assert_true("version" in body and "sha256" in body and "size_bytes" in body,
                        f"JSON verzija: v{body.get('version')}, heš: {body.get('sha256')[:10]}...")

        # B) GET /api/payload/download
        print("\n--- TEST 4B: GET /api/payload/download (binarni strim) ---")
        req = urllib.request.Request(f"{base_url}/api/payload/download")
        with urllib.request.urlopen(req) as resp:
            assert_true(resp.status == 200, f"Status 200 za /api/payload/download")
            assert_true(resp.headers.get("Content-Type") == "application/zip", f"Content-Type je application/zip")
            assert_true("gwadt-payload.zip" in resp.headers.get("Content-Disposition", ""), f"Content-Disposition attachment")
            header_sha = resp.headers.get("X-Payload-SHA256")
            header_ver = resp.headers.get("X-Payload-Version")
            assert_true(header_sha == body["sha256"], f"X-Payload-SHA256 heder odgovara ({header_sha[:10]}...)")
            assert_true(header_ver == body["version"], f"X-Payload-Version heder odgovara ({header_ver})")

            dl_bytes = resp.read()
            dl_hash = hashlib.sha256(dl_bytes).hexdigest()
            assert_true(dl_hash == body["sha256"], f"Preuzeti strim ({len(dl_bytes)} B) ima identičan SHA-256 heš!")

        # C) POST /api/devices/{id}/report
        print("\n--- TEST 4C: POST /api/devices/{id}/report ---")
        test_dev_id = "SERVIS-PC-QA01"
        report_payload = {
            "device_id": test_dev_id,
            "computer_name": "TK-DESKTOP-QA01",
            "chassis": "Mini-PC",
            "serial_number": "TK-SN-2026-X89",
            "status": "SUCCESS",
            "duration_seconds": 634,
            "apps": {
                "Chrome": "Completed",
                "WinRAR": "Completed",
                "Office 2024": "Completed",
                "Acrobat": "Completed"
            },
            "errors": [],
            "hw": {
                "manufacturer": "HP",
                "model": "ProDesk 400 G6",
                "cpu": "Intel Core i5-10500T",
                "ram_gb": 16,
                "disk_model": "Samsung NVMe 512GB",
                "disk_size_gb": 512
            }
        }

        req = urllib.request.Request(
            f"{base_url}/api/devices/{test_dev_id}/report",
            data=json.dumps(report_payload).encode("utf-8"),
            headers={"Content-Type": "application/json"}
        )
        with urllib.request.urlopen(req) as resp:
            assert_true(resp.status == 200, f"Status 200 za POST /api/devices/{test_dev_id}/report")
            res_json = json.loads(resp.read().decode("utf-8"))
            assert_true(res_json.get("status") == "ok", "Status 'ok' u odgovoru")
            json_fn = res_json.get("json_file")
            md_fn = res_json.get("md_file")
            assert_true(bool(json_fn and md_fn), f"Vraćena imena fajlova: {json_fn}, {md_fn}")

        # Provera postojanja i sadržaja arhiviranih fajlova na disku
        json_path_disk = os.path.join(REPORTS_DIR, json_fn)
        md_path_disk = os.path.join(REPORTS_DIR, md_fn)
        assert_true(os.path.isfile(json_path_disk), f"JSON izveštaj sačuvan na disku: {json_path_disk}")
        assert_true(os.path.isfile(md_path_disk), f"Markdown izveštaj sačuvan na disku: {md_path_disk}")

        with open(json_path_disk, "r", encoding="utf-8") as f:
            saved_json = json.load(f)
            assert_true(saved_json["serial_number"] == "TK-SN-2026-X89", "Serijski broj tačno upisan u JSON")
            assert_true(saved_json["duration_formatted"] == "10m 34s", f"Trajanje formatirano: {saved_json['duration_formatted']}")

        with open(md_path_disk, "r", encoding="utf-8") as f:
            md_text = f.read()
            assert_true("WIS Service | Završni Servisni Izveštaj" in md_text, "Markdown zaglavlje prisutno")
            assert_true("HP" in md_text and "ProDesk 400 G6" in md_text, "Hardverski profil prisutan u Markdown-u")
            assert_true("Chrome" in md_text and "Completed" in md_text, "Tabela aplikacija prisutna u Markdown-u")
            assert_true("Nema zabeleženih grešaka" in md_text, "Status grešaka verifikovan u Markdown-u")

        # D) GET /api/devices/{id}/report
        print("\n--- TEST 4D: GET /api/devices/{id}/report ---")
        req = urllib.request.Request(f"{base_url}/api/devices/{test_dev_id}/report")
        with urllib.request.urlopen(req) as resp:
            assert_true(resp.status == 200, f"Status 200 za GET /api/devices/{test_dev_id}/report")
            fetched_report = json.loads(resp.read().decode("utf-8"))
            assert_true(fetched_report["serial_number"] == "TK-SN-2026-X89", "Povraćeni izveštaj odgovara poslatom")

        # E) Verifikacija stabilnosti postojećih ruta
        print("\n--- TEST 4E: Verifikacija stabilnosti postojećih ruta ---")
        # 1. Health
        with urllib.request.urlopen(f"{base_url}/api/health") as resp:
            assert_true(resp.status == 200, "GET /api/health operativan (200)")

        # 2. Devices
        with urllib.request.urlopen(f"{base_url}/api/devices") as resp:
            assert_true(resp.status == 200, "GET /api/devices operativan (200)")
            devs = json.loads(resp.read().decode("utf-8"))
            assert_true(test_dev_id in devs or "SERVIS_PC_QA01" in devs, "Test uređaj vidljiv u floti")

        # 3. Heartbeat
        req = urllib.request.Request(
            f"{base_url}/api/heartbeat",
            data=json.dumps({"computer_name": "HEARTBEAT-TEST", "phase": 2}).encode("utf-8"),
            headers={"Content-Type": "application/json"}
        )
        with urllib.request.urlopen(req) as resp:
            assert_true(resp.status == 200, "POST /api/heartbeat operativan (200)")

        # 4. Log
        req = urllib.request.Request(
            f"{base_url}/api/log",
            data=json.dumps({"computer_name": "LOG-TEST", "lines": ["[STEP] Test linija loga"]}).encode("utf-8"),
            headers={"Content-Type": "application/json"}
        )
        with urllib.request.urlopen(req) as resp:
            assert_true(resp.status == 200, "POST /api/log operativan (200)")

        # 5. Dashboard HTML
        with urllib.request.urlopen(f"{base_url}/") as resp:
            assert_true(resp.status == 200, "GET / (Dashboard HTML) operativan (200)")

    finally:
        server.shutdown()
        server.server_close()
        print("\n  [INFO] Testni server je ugašen.")

    print_header("SVI TESTOVI SU 100% USPEŠNO PROŠLI (ALL PASS)")

if __name__ == "__main__":
    run_tests()
