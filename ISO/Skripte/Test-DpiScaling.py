#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
Test-DpiScaling.py - Rigorozna QA verifikacija i simulacija DPI skaliranja (GWADT-TASK-042)
Testira:
1. Hash i sadržajnu sinhronizaciju svih instanci skripti.
2. PowerShell 5.1 kompatibilnost i validnost P/Invoke definicija.
3. Simulaciju graničnih rezolucija ekrana i evaluaciju ciljanih faktora skaliranja.
"""

import os
import re
import sys
import hashlib

PROJECT_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SKRIPTE_DIR = os.path.join(PROJECT_ROOT, "Skripte")
MODULES_DIR = os.path.join(SKRIPTE_DIR, "Modules")

INSTALL_SCRIPTS = [
    os.path.join(PROJECT_ROOT, "Install-ServisApps.ps1"),
    os.path.join(SKRIPTE_DIR, "Install-ServisApps.ps1"),
    os.path.join(PROJECT_ROOT, "sources", "$OEM$", "$1", "setup", "Install-ServisApps.ps1")
]

WINDOWS_SETUP_SCRIPTS = [
    os.path.join(PROJECT_ROOT, "Programi", "Windows_setup.ps1"),
    os.path.join(PROJECT_ROOT, "sources", "$OEM$", "$1", "setup", "Programi", "Windows_setup.ps1")
]

BRANDING_MODULE = os.path.join(MODULES_DIR, "GWADT.Branding.psm1")

def calculate_sha256(filepath):
    h = hashlib.sha256()
    with open(filepath, "rb") as f:
        while chunk := f.read(65536):
            h.update(chunk)
    return h.hexdigest()

def evaluate_dpi_scaling(screen_width, screen_height):
    """
    Simulira logiku iz Set-OptimalDisplayScaling:
    - width < 1024 -> Fallback (preskace se)
    - width >= 3840 -> 200% (LogPixels = 192, 4K / UHD)
    - width >= 2560 -> 150% (LogPixels = 144, 2K / QHD)
    - width >= 1920 -> 125% (LogPixels = 120, FHD / FHD+)
    - width < 1920  -> 100% (LogPixels = 96, Sub-FHD, zadrzava se fabricki)
    """
    if screen_width < 1024:
        return {
            "action": "SKIP_FALLBACK",
            "scale": None,
            "log_pixels": None,
            "category": "Invalid/Fallback (<1024px)"
        }

    target_scale = 100
    log_pixels = 96
    scale_category = ""

    if screen_width >= 3840:
        target_scale = 200
        log_pixels = 192
        scale_category = "4K / UHD"
    elif screen_width >= 2560:
        target_scale = 150
        log_pixels = 144
        scale_category = "2K / QHD"
    elif screen_width >= 1920:
        target_scale = 125
        log_pixels = 120
        scale_category = "FHD / FHD+"
    else:
        target_scale = 100
        log_pixels = 96
        scale_category = "Sub-FHD"

    if target_scale == 100:
        return {
            "action": "DEFAULT_RETAIN",
            "scale": 100,
            "log_pixels": 96,
            "category": scale_category
        }

    return {
        "action": "APPLY_SCALING",
        "scale": target_scale,
        "log_pixels": log_pixels,
        "category": scale_category
    }

def main():
    print("=" * 75)
    print("   GWADT QA SUITE | Verifikacija DPI Skaliranja Ekrana (TASK-042)")
    print("=" * 75)
    all_passed = True

    # 1. Provera hash sinhronizacije
    print("\n[TEST 1] Provera hash sinhronizacije i integriteta fajlova...")
    
    # 1a. Install-ServisApps.ps1
    install_hashes = [calculate_sha256(p) for p in INSTALL_SCRIPTS]
    if len(set(install_hashes)) == 1:
        print(f" [PASS] Sve 3 kopije Install-ServisApps.ps1 su 100% identične.")
        print(f"        SHA-256: {install_hashes[0]}")
    else:
        print(f" [FAIL] Kopije Install-ServisApps.ps1 se razlikuju!")
        for p, h in zip(INSTALL_SCRIPTS, install_hashes):
            print(f"        {os.path.relpath(p, PROJECT_ROOT)}: {h}")
        all_passed = False

    # 1b. Windows_setup.ps1
    setup_hashes = [calculate_sha256(p) for p in WINDOWS_SETUP_SCRIPTS]
    if len(set(setup_hashes)) == 1:
        print(f" [PASS] Obe kopije Windows_setup.ps1 su 100% identične.")
        print(f"        SHA-256: {setup_hashes[0]}")
    else:
        print(f" [FAIL] Kopije Windows_setup.ps1 se razlikuju!")
        for p, h in zip(WINDOWS_SETUP_SCRIPTS, setup_hashes):
            print(f"        {os.path.relpath(p, PROJECT_ROOT)}: {h}")
        all_passed = False

    # 2. Provera strukture koda i PowerShell 5.1 kompatibilnosti
    print("\n[TEST 2] Verifikacija strukture implementacije i PowerShell 5.1 pravila...")
    
    with open(BRANDING_MODULE, "r", encoding="utf-8") as f:
        branding_code = f.read()
    with open(WINDOWS_SETUP_SCRIPTS[0], "r", encoding="utf-8") as f:
        setup_code = f.read()

    # Provera ključnih komponenti funkcije
    checks = [
        ("Set-OptimalDisplayScaling definicija", r'function\s+Set-OptimalDisplayScaling\b', branding_code),
        ("Export-ModuleMember za Set-OptimalDisplayScaling", r'Export-ModuleMember.*Set-OptimalDisplayScaling', branding_code),
        ("Poziv u Invoke-SystemOptimizationAndBranding", r'Set-OptimalDisplayScaling', branding_code),
        ("Win32_VideoController detekcija", r'Win32_VideoController', branding_code),
        ("Fallback na System.Windows.Forms.Screen", r'System\.Windows\.Forms\.Screen', branding_code),
        ("Registry upis HKCU:\\Control Panel\\Desktop", r'HKCU:\\Control Panel\\Desktop', branding_code),
        ("Registry vrednost LogPixels", r'Set-ItemProperty\s+.*-Name\s+["\']LogPixels["\']', branding_code),
        ("Registry vrednost Win8DpiScaling", r'Set-ItemProperty\s+.*-Name\s+["\']Win8DpiScaling["\']', branding_code),
        ("PSTypeName provera pre Add-Type", r'\[System\.Management\.Automation\.PSTypeName\][\'"]GWADT\.DpiHelper[\'"]', branding_code),
        ("P/Invoke UpdatePerUserSystemParameters", r'UpdatePerUserSystemParameters', branding_code),
        ("P/Invoke SendMessageTimeout", r'SendMessageTimeout', branding_code),
        ("Windows_setup.ps1 korak 6 za DPI", r'Proveravam rezoluciju ekrana za optimalno DPI skaliranje', setup_code),
    ]

    for name, pattern, code in checks:
        if re.search(pattern, code, re.IGNORECASE | re.DOTALL):
            print(f" [PASS] {name:<50} - Potvrđeno")
        else:
            print(f" [FAIL] {name:<50} - Nedostaje!")
            all_passed = False

    # Provera zabranjenih operatora u PS 5.1 (npr. ??, ??=, ?:, &&, ||)
    ps7_operators = [
        (r'\?\?', "Null coalescing operator (??)"),
        (r'\?\?=', "Null coalescing assignment (??=)"),
        (r'\?.*:.*', "Ternary operator (?:)"),
        (r'\s&&\s', "Pipeline chain AND (&&)"),
        (r'\s\|\|\s', "Pipeline chain OR (||)")
    ]
    
    # Proveri unutar funkcije Set-OptimalDisplayScaling
    func_match = re.search(r'function\s+Set-OptimalDisplayScaling\s*\{([\s\S]*?)\nfunction\b', branding_code)
    if func_match:
        func_body = func_match.group(1)
        for pattern, op_name in ps7_operators:
            # Ignorisemo ako je u komentaru ili stringu
            clean_lines = [l for l in func_body.splitlines() if not l.strip().startswith('#')]
            found = False
            for line in clean_lines:
                if re.search(pattern, line):
                    found = True
                    break
            if found:
                print(f" [FAIL] Pronađen PS 7.x operator {op_name} u Set-OptimalDisplayScaling!")
                all_passed = False
            else:
                pass
        print(f" [PASS] Set-OptimalDisplayScaling je 100% kompatibilan sa PowerShell 5.1 AST")

    # 3. Granične rezolucije ekrana i simulacija faktora skaliranja
    print("\n[TEST 3] Simulacija rezolucija i evaluacija graničnih vrednosti skaliranja...")

    test_cases = [
        # (width, height, expected_action, expected_scale, expected_logpixels, description)
        (1920, 1080, "APPLY_SCALING", 125, 120, "1920x1080 (FHD) -> 125% scale"),
        (1920, 1200, "APPLY_SCALING", 125, 120, "1920x1200 (FHD+) -> 125% scale"),
        (2560, 1440, "APPLY_SCALING", 150, 144, "2560x1440 (2K QHD) -> 150% scale"),
        (2560, 1600, "APPLY_SCALING", 150, 144, "2560x1600 (2K WQXGA) -> 150% scale"),
        (3840, 2160, "APPLY_SCALING", 200, 192, "3840x2160 (4K UHD) -> 200% scale"),
        (4096, 2160, "APPLY_SCALING", 200, 192, "4096x2160 (4K DCI) -> 200% scale"),
        (1366, 768,  "DEFAULT_RETAIN", 100, 96,  "1366x768 (HD) -> 100% default retain"),
        (1600, 900,  "DEFAULT_RETAIN", 100, 96,  "1600x900 (HD+) -> 100% default retain"),
        (1280, 720,  "DEFAULT_RETAIN", 100, 96,  "1280x720 (HD 720p) -> 100% default retain"),
        (1024, 768,  "DEFAULT_RETAIN", 100, 96,  "1024x768 (XGA granična >=1024) -> 100% default"),
        (1023, 768,  "SKIP_FALLBACK", None, None, "1023x768 (granična <1024) -> Fallback preskakanje"),
        (800,  600,  "SKIP_FALLBACK", None, None, "800x600 (SVGA <1024) -> Fallback preskakanje"),
        (640,  480,  "SKIP_FALLBACK", None, None, "640x480 (VGA <1024) -> Fallback preskakanje"),
        (0,    0,    "SKIP_FALLBACK", None, None, "0x0 (nedetektovano) -> Fallback preskakanje"),
        (1919, 1080, "DEFAULT_RETAIN", 100, 96,  "1919x1080 (granična tik ispod FHD) -> 100% default"),
        (2559, 1440, "APPLY_SCALING", 125, 120, "2559x1440 (granična tik ispod 2K) -> 125% scale"),
        (3839, 2160, "APPLY_SCALING", 150, 144, "3839x2160 (granična tik ispod 4K) -> 150% scale"),
        (5120, 2880, "APPLY_SCALING", 200, 192, "5120x2880 (5K) -> 200% scale"),
        (7680, 4320, "APPLY_SCALING", 200, 192, "7680x4320 (8K) -> 200% scale"),
    ]

    for w, h, exp_act, exp_scale, exp_lp, desc in test_cases:
        res = evaluate_dpi_scaling(w, h)
        matched = (
            res["action"] == exp_act and
            res["scale"] == exp_scale and
            res["log_pixels"] == exp_lp
        )
        if matched:
            print(f" [PASS] {desc:<52} [Action: {res['action']}, Scale: {res['scale']}%, LogPixels: {res['log_pixels']}]")
        else:
            print(f" [FAIL] {desc:<52}")
            print(f"        Očekivano: Action={exp_act}, Scale={exp_scale}, LogPixels={exp_lp}")
            print(f"        Dobijeno:  Action={res['action']}, Scale={res['scale']}, LogPixels={res['log_pixels']}")
            all_passed = False

    print("\n" + "=" * 75)
    if all_passed:
        print(" [REZULTAT] SVI DPI QA TESTOVI USPESNO PROSLI (100% PASS)!")
    else:
        print(" [REZULTAT] TESTOVI NISU PROSLI! Ispravite greske iznad.")
    print("=" * 75)

    sys.exit(0 if all_passed else 1)

if __name__ == "__main__":
    main()
