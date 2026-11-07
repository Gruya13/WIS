#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
Test skripta za ServisHub API za Poređenje Verzija i Proveru Ažuriranja Drajvera
(GWADT-TASK-117 Faza 1).
"""

import sys
import os
import json
import time
import threading
import urllib.request
from http.server import ThreadingHTTPServer

# Uvoz iz hub_server.py
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from hub_server import (
    is_catalog_driver_newer,
    parse_driver_date,
    parse_driver_version_components,
    ServisHubHandler,
    DRIVER_CACHE_MGR
)

def test_version_and_date_comparison():
    print("[TEST 1] Testiranje funkcije is_catalog_driver_newer()...")
    
    # 1. Datum: katalog noviji (2023 vs 2018)
    assert is_catalog_driver_newer("6.0.1.8500", "05/15/2018", "6.0.9500.1", "10/20/2023") is True, "Greška: 2023 mora biti noviji od 2018"
    
    # 2. Datum: katalog stariji (2018 vs 2023)
    assert is_catalog_driver_newer("6.0.9500.1", "10/20/2023", "6.0.1.8500", "05/15/2018") is False, "Greška: 2018 ne može biti noviji od 2023"
    
    # 3. Isti datumi, veća kataloška verzija
    assert is_catalog_driver_newer("10.0.19041.1", "2023-01-15", "10.0.19041.5", "2023-01-15") is True, "Greška: veća verzija mora biti novija pri istom datumu"
    
    # 4. Isti datumi, manja kataloška verzija
    assert is_catalog_driver_newer("10.0.19041.5", "2023-01-15", "10.0.19041.1", "2023-01-15") is False, "Greška: manja verzija ne sme biti novija"
    
    # 5. Potpuno jednaki datum i verzija
    assert is_catalog_driver_newer("10.0.19041.1", "2023-01-15", "10.0.19041.1", "2023-01-15") is False, "Greška: identičan drajver ne sme biti noviji"
    
    # 6. Nepoznati datumi, poređenje verzija
    assert is_catalog_driver_newer("1.0.0", "", "1.0.1", "") is True, "Greška: semantička verzija 1.0.1 > 1.0.0 bez datuma"
    assert is_catalog_driver_newer("2.5.0", "unknown", "2.4.9", "unknown") is False, "Greška: 2.4.9 < 2.5.0 bez datuma"
    
    # 7. WMI datum format (20180515...) vs ISO datum (2023-10-20)
    assert is_catalog_driver_newer("6.0.1.8500", "20180515000000.000000-000", "6.0.9500.1", "2023-10-20") is True, "Greška: WMI format nije ispravno prepoznat"
    
    # 8. Evropski tačkasti format
    assert is_catalog_driver_newer("1.0", "15.05.2018.", "2.0", "20.10.2023") is True, "Greška: tačkasti datum format"

    print("  [✓] Svi testovi poređenja verzija i datuma su uspešno prošli!\n")


def test_api_check_update_http():
    print("[TEST 2] Testiranje REST API rute POST /api/drivers/check_update na živom HTTP serveru...")
    
    # Pokretanje test HTTP servera na slobodnom portu
    test_port = 18889
    server = ThreadingHTTPServer(("127.0.0.1", test_port), ServisHubHandler)
    server_thread = threading.Thread(target=server.serve_forever, daemon=True)
    server_thread.start()
    time.sleep(0.5)

    base_url = f"http://127.0.0.1:{test_port}/api/drivers/check_update"

    try:
        # A) Poziv sa starom verzijom Realtek drajvera iz 2018. i provera
        payload_old = {
            "hwids": ["PCI\\VEN_10EC&DEV_8168&SUBSYS_85701043&REV_2C", "PCI\\VEN_10EC&DEV_8168"],
            "current_version": "9.1.402.2015",
            "current_date": "05/15/2018",
            "device_name": "Realtek PCIe GbE Family Controller"
        }
        
        req = urllib.request.Request(
            base_url,
            data=json.dumps(payload_old).encode("utf-8"),
            headers={"Content-Type": "application/json"}
        )
        print(f"  [*] Šaljem POST /api/drivers/check_update sa starim Realtek drajverom (v9.1.402.2015, datum: 05/15/2018)...")
        with urllib.request.urlopen(req, timeout=40) as resp:
            data = json.loads(resp.read().decode("utf-8"))
            print(f"  [+] Odgovor servera: status={data.get('status')}, update_available={data.get('update_available')}")
            print(f"      Pronađena novija verzija: {data.get('latest_version')} ({data.get('latest_date')})")
            print(f"      Naslov: {data.get('title')}")
            print(f"      Download URL: {data.get('download_url')}")
            
            assert data.get("status") == "ok", f"Status mora biti 'ok', dobijeno: {data.get('status')}"
            assert data.get("update_available") is True, f"Mora vratiti update_available=True za staru verziju iz 2018!"
            assert "download_url" in data, "Mora postojati download_url za preuzimanje novog drajvera!"
            assert "driver_id" in data, "Mora postojati driver_id keširanog drajvera!"

        # B) Poziv kada je klijentski drajver već najnovija verzija (fiktivna verzija iz 2099.)
        payload_newest = {
            "hwids": ["PCI\\VEN_10EC&DEV_8168"],
            "current_version": "99.99.99.99",
            "current_date": "2099-01-01",
            "device_name": "Realtek PCIe GbE Family Controller"
        }
        req2 = urllib.request.Request(
            base_url,
            data=json.dumps(payload_newest).encode("utf-8"),
            headers={"Content-Type": "application/json"}
        )
        print(f"  [*] Šaljem POST /api/drivers/check_update sa ultra-novom verzijom (v99.99.99.99, datum: 2099-01-01)...")
        with urllib.request.urlopen(req2, timeout=40) as resp2:
            data2 = json.loads(resp2.read().decode("utf-8"))
            print(f"  [+] Odgovor servera: update_available={data2.get('update_available')}, poruka='{data2.get('message')}'")
            assert data2.get("status") == "ok"
            assert data2.get("update_available") is False, "Mora vratiti update_available=False jer je lokalna verzija novija od kataloga!"
            assert "već najnovija verzija" in data2.get("message", "")

        # C) Poziv za nepostojeći hardver
        payload_nonexistent = {
            "hwids": ["PCI\\VEN_9999&DEV_9999&SUBSYS_99999999"],
            "current_version": "1.0.0.0",
            "current_date": "2020-01-01",
            "device_name": "Nepostojeći Uređaj"
        }
        req3 = urllib.request.Request(
            base_url,
            data=json.dumps(payload_nonexistent).encode("utf-8"),
            headers={"Content-Type": "application/json"}
        )
        print(f"  [*] Šaljem POST /api/drivers/check_update za nepostojeći HWID...")
        with urllib.request.urlopen(req3, timeout=40) as resp3:
            data3 = json.loads(resp3.read().decode("utf-8"))
            print(f"  [+] Odgovor servera: update_available={data3.get('update_available')}, not_found={data3.get('not_found')}")
            assert data3.get("status") == "ok"
            assert data3.get("update_available") is False
            assert data3.get("not_found") is True

        print("  [✓] Svi testovi REST API rute su uspešno prošli!\n")

    finally:
        server.shutdown()
        server.server_close()

if __name__ == "__main__":
    print("=== Pokretanje verifikacije GWADT-TASK-117 Faza 1 ===\n")
    test_version_and_date_comparison()
    test_api_check_update_http()
    print("=== SVI TESTOVI SU USPEŠNO PROŠLI! ===")
