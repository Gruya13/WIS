#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
Test-HubAndUIIntegration.py - End-to-End Integration Test
Verifikuje API sesije, SSE event stream i UI stanje u ServisHub-u.
"""

import sys
import os
import time
import json
import urllib.request
import urllib.error
import threading
import subprocess

SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
PROJECT_ROOT = os.path.dirname(SCRIPT_DIR)
HUB_SERVER_PATH = os.path.join(SCRIPT_DIR, "ServisHub", "hub_server.py")

TEST_PORT = 5099
BASE_URL = f"http://127.0.0.1:{TEST_PORT}"
SESSION_ID = "TEST-SESSION-E2E-1234"

def main():
    print("=" * 70)
    print("   TEST-HUB-AND-UI-INTEGRATION: ServisHub & SSE Verification")
    print("=" * 70)

    # 1. Pokreni privremeni hub server
    env = os.environ.copy()
    env["SERVIS_HUB_PORT"] = str(TEST_PORT)
    proc = subprocess.Popen(
        [sys.executable, HUB_SERVER_PATH, "--port", str(TEST_PORT)],
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        env=env
    )

    try:
        # Sacekaj da se server podigne
        server_ready = False
        for _ in range(20):
            try:
                resp = urllib.request.urlopen(f"{BASE_URL}/api/health", timeout=1)
                if resp.status == 200:
                    server_ready = True
                    break
            except Exception:
                time.sleep(0.2)

        if not server_ready:
            print("[FAIL] ServisHub server se nije podigao na portu", TEST_PORT)
            sys.exit(1)
        print("[PASS] ServisHub server uspesno pokrenut na portu", TEST_PORT)

        # 2. Test GET /api/sessions (inicijalno prazno)
        req = urllib.request.Request(f"{BASE_URL}/api/sessions")
        with urllib.request.urlopen(req) as resp:
            data = json.loads(resp.read().decode())
            assert "sessions" in data, "Nedostaje 'sessions' kljuc"
            print(f"[PASS] GET /api/sessions -> {len(data['sessions'])} sesija")

        # 3. Test POST /api/session/<session_id>/state (inicijalno stanje)
        payload = {
            "session_id": SESSION_ID,
            "device_model": "HP EliteBook 840 G8",
            "phase": 2,
            "sub_phase": "Windows Update (Krug 1/3)",
            "progress_percent": 35,
            "is_paused": False,
            "usb_can_be_removed": True,
            "status_message": "Preuzimam kumulativne zakrpe...",
            "ssd_health_percent": 99,
            "ssd_health_category": "HEALTHY",
            "status": "RUNNING"
        }
        json_bytes = json.dumps(payload).encode("utf-8")
        post_req = urllib.request.Request(
            f"{BASE_URL}/api/session/{SESSION_ID}/state",
            data=json_bytes,
            headers={"Content-Type": "application/json"}
        )
        with urllib.request.urlopen(post_req) as resp:
            resp_data = json.loads(resp.read().decode())
            assert resp_data.get("ok") is True, "POST /api/session/<id>/state nije vratio ok: true"
            print("[PASS] POST /api/session/<id>/state uspesno registrovao stanje")

        # 4. Provera da se sesija sada vidi u GET /api/sessions sa tacnim parametrima
        with urllib.request.urlopen(f"{BASE_URL}/api/sessions") as resp:
            data = json.loads(resp.read().decode())
            sessions = data.get("sessions", [])
            match = [s for s in sessions if s.get("session_id") == SESSION_ID]
            assert len(match) == 1, "Sesija nije pronadjena u listi sesija"
            s = match[0]
            assert s.get("phase") == 2, f"Pogresna faza: {s.get('phase')}"
            assert s.get("is_paused") is False, "Ocekivan is_paused: False"
            assert s.get("usb_can_be_removed") is True, "Ocekivan usb_can_be_removed: True"
            assert s.get("ssd_health_percent") == 99, "Ocekivan ssd_health_percent: 99"
            print(f"[PASS] GET /api/sessions potvrdio prisustvo sesije: Model={s.get('device_model')}, USB Free={s.get('usb_can_be_removed')}")

        # 5. Test SSE Stream: GET /api/session/<session_id>/stream
        sse_req = urllib.request.Request(f"{BASE_URL}/api/session/{SESSION_ID}/stream")
        sse_resp = urllib.request.urlopen(sse_req, timeout=3)
        assert sse_resp.headers.get("Content-Type") == "text/event-stream", "Header mora biti text/event-stream"
        assert sse_resp.headers.get("Cache-Control") == "no-cache", "Cache-Control mora biti no-cache"

        # Procitaj prvi event
        line1 = sse_resp.readline().decode().strip()
        line2 = sse_resp.readline().decode().strip()
        print(f"[PASS] SSE Stream primljen: '{line1}' | '{line2}'")
        assert line1.startswith("data:"), f"Ocekivan 'data:' prefiks, dobijeno: {line1}"
        sse_data = json.loads(line1.replace("data:", "").strip())
        assert sse_data.get("session_id") == SESSION_ID
        assert sse_data.get("phase") == 2
        sse_resp.close()

        # 6. Test promena stanja na PAUSED
        payload["is_paused"] = True
        payload["status_message"] = "Korisnik pauzirao proces"
        post_req2 = urllib.request.Request(
            f"{BASE_URL}/api/session/{SESSION_ID}/state",
            data=json.dumps(payload).encode("utf-8"),
            headers={"Content-Type": "application/json"}
        )
        with urllib.request.urlopen(post_req2) as resp:
            assert json.loads(resp.read().decode()).get("ok") is True

        with urllib.request.urlopen(f"{BASE_URL}/api/sessions") as resp:
            data = json.loads(resp.read().decode())
            match = [s for s in data.get("sessions", []) if s.get("session_id") == SESSION_ID]
            assert match[0].get("is_paused") is True, "is_paused nije azuriran na True"
            print("[PASS] Sesija uspesno azurirana u PAUSED stanje")

        print("=" * 70)
        print(" [REZULTAT] SVI E2E SERVIS HUB I SSE TESTOVI USPESNO PROSLI (100% PASS)!")
        print("=" * 70)

    finally:
        proc.terminate()
        try:
            proc.wait(timeout=2)
        except subprocess.TimeoutExpired:
            proc.kill()

if __name__ == "__main__":
    main()
