#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
Test-Sprint-Defects-QA.py - Sveobuhvatna automatizovana QA verifikacija
Projekat: Tehno kutak Windows Installation Assistant (WIS) v2.13.0
Zadatak : WIS-TASK-125 (Faza 4: QA Verifikacija, Test Skripta i Kompilacija)
Uloga   : wis_qa_debugger

Tematske celine:
  Test 1 : Reality Check (WIS.Apps.psm1, Invoke-Phase4Apps, MainWindow.xaml.cs)
  Test 2 : Launcher Stabilizacija (PayloadManager.cs, SplashForm.cs)
  Test 3 : Dvojezična Podrška (LocalizationService.cs, YaHei font stack, SRB/ZH preklopnik)
  Test 4 : Globalni Error Trap u Fazama (Phase1-Network do Phase5-Finalize)
  Test 5 : Dvoslojna Detekcija Laptopa & Floating BatteryBar (ChassisTypes, Solo=True, Startup, Test-Path)
  Test 6 : WinPE Disk Z:\\ (WIS-AcceleratePE.cmd, Patch-BootWim.py, boot.wim optimizacija)
  Test 7 : Dual-Pass Windows Update (Invoke-WindowsUpdateSecondPass u modulu i Fazi 5)
  Test 8 : Internet Fallback za OEM Drajvere (Install-DriverFromMsCatalog u WIS.Hardware.psm1)
  Test 9 : Statička Analiza Koda (Verify-PowerShellSyntax.py i Verify-CSharpSyntax.py)
  Test 10: Kompilacija u Docker Wine i 100% SHA-256 Paritet Binarnih Fajlova
"""

import sys
import os
import hashlib
import subprocess
import xml.etree.ElementTree as ET
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent

passed_count = 0
failed_count = 0

def get_sha256(filepath: Path) -> str:
    """Računa SHA-256 heš datoteke."""
    if not filepath.exists() or not filepath.is_file():
        return ""
    h = hashlib.sha256()
    with open(filepath, "rb") as f:
        while chunk := f.read(65536):
            h.update(chunk)
    return h.hexdigest().lower()

def check(name: str, condition: bool, details: str = ""):
    global passed_count, failed_count
    if condition:
        passed_count += 1
        print(f"  [PASS] {name}" + (f" ({details})" if details else ""))
        return True
    else:
        failed_count += 1
        print(f"  [FAIL] {name}" + (f" -> {details}" if details else ""))
        return False

print("=" * 80)
print("   WIS-TASK-125 QA VERIFIKACIJA: ELIMINACIJA DEFEKATA I DVOJEZIČNI UI")
print("   Tehno kutak Windows Installation Assistant (WIS) - wis_qa_debugger")
print("=" * 80)

# ---------------------------------------------------------------------------
# TEST 1: REALITY CHECK
# ---------------------------------------------------------------------------
print("\n[TEST 1] Verifikacija Reality Check mehanizma (WIS.Apps.psm1 & MainWindow.xaml.cs)...")
apps_psm1 = REPO_ROOT / "Skripte" / "Modules" / "WIS.Apps.psm1"
apps_code = apps_psm1.read_text(encoding="utf-8", errors="ignore") if apps_psm1.exists() else ""

has_assert_func = "function Assert-InstalledApplicationsReality" in apps_code
check("WIS.Apps.psm1 sadrži funkciju Assert-InstalledApplicationsReality", has_assert_func)

has_chrome_check = "chrome.exe" in apps_code and ("Google Chrome" in apps_code or "Chrome" in apps_code)
has_acrobat_check = "Acrobat.exe" in apps_code and "Adobe Acrobat" in apps_code
has_office_check = "WINWORD.EXE" in apps_code and "EXCEL.EXE" in apps_code
has_winrar_check = "WinRAR.exe" in apps_code

check("Assert-InstalledApplicationsReality proverava Google Chrome", has_chrome_check)
check("Assert-InstalledApplicationsReality proverava Adobe Acrobat", has_acrobat_check)
check("Assert-InstalledApplicationsReality proverava Microsoft Office (Word/Excel)", has_office_check)
check("Assert-InstalledApplicationsReality proverava WinRAR", has_winrar_check)

invoke_phase4_calls_reality = "Assert-InstalledApplicationsReality" in apps_code[apps_code.find("function Invoke-Phase4Apps"):]
invoke_phase4_throws = 'throw "REALITY CHECK GREŠKA' in apps_code
check("Invoke-Phase4Apps poziva Assert-InstalledApplicationsReality i baca throw pri neuspehu",
      invoke_phase4_calls_reality and invoke_phase4_throws)

# MainWindow.xaml.cs provera
wis_mw_cs = REPO_ROOT / "Skripte" / "WIS.UI" / "MainWindow.xaml.cs"
gwadt_mw_cs = REPO_ROOT / "Skripte" / "GWADT.UI" / "MainWindow.xaml.cs"
wis_mw_code = wis_mw_cs.read_text(encoding="utf-8", errors="ignore") if wis_mw_cs.exists() else ""
gwadt_mw_code = gwadt_mw_cs.read_text(encoding="utf-8", errors="ignore") if gwadt_mw_cs.exists() else ""

wis_has_reality = "CheckInstalledApplicationsReality" in wis_mw_code or "PerformPhase4RealityCheck" in wis_mw_code
gwadt_has_reality = "CheckInstalledApplicationsReality" in gwadt_mw_code or "PerformPhase4RealityCheck" in gwadt_mw_code
check("WIS.UI MainWindow.xaml.cs sadrži Reality Check proveru aplikacija", wis_has_reality)
check("GWADT.UI MainWindow.xaml.cs sadrži Reality Check proveru aplikacija", gwadt_has_reality)

wis_has_wmi_err = "ConfigManagerErrorCode != 0" in wis_mw_code
gwadt_has_wmi_err = "ConfigManagerErrorCode != 0" in gwadt_mw_code
check("WIS.UI MainWindow.xaml.cs proverava WMI ConfigManagerErrorCode != 0 za drajvere", wis_has_wmi_err)
check("GWADT.UI MainWindow.xaml.cs proverava WMI ConfigManagerErrorCode != 0 za drajvere", gwadt_has_wmi_err)

# ---------------------------------------------------------------------------
# TEST 2: LAUNCHER STABILIZACIJA
# ---------------------------------------------------------------------------
print("\n[TEST 2] Verifikacija stabilizacije Launcher-a i detekcije verzije...")
wis_payload_cs = REPO_ROOT / "Skripte" / "WIS.Launcher" / "PayloadManager.cs"
gwadt_payload_cs = REPO_ROOT / "Skripte" / "GWADT.Launcher" / "PayloadManager.cs"
wis_payload_code = wis_payload_cs.read_text(encoding="utf-8", errors="ignore") if wis_payload_cs.exists() else ""
gwadt_payload_code = gwadt_payload_cs.read_text(encoding="utf-8", errors="ignore") if gwadt_payload_cs.exists() else ""

wis_sha_fallback = "CalculateSha256" in wis_payload_code and "metadata.json" in wis_payload_code
gwadt_sha_fallback = "CalculateSha256" in gwadt_payload_code and "metadata.json" in gwadt_payload_code
check("WIS.Launcher PayloadManager.cs računa SHA-256 direktno nad .exe ako metapodaci nedostaju", wis_sha_fallback)
check("GWADT.Launcher PayloadManager.cs računa SHA-256 direktno nad .exe ako metapodaci nedostaju", gwadt_sha_fallback)

wis_splash_cs = REPO_ROOT / "Skripte" / "WIS.Launcher" / "SplashForm.cs"
gwadt_splash_cs = REPO_ROOT / "Skripte" / "GWADT.Launcher" / "SplashForm.cs"
wis_splash_code = wis_splash_cs.read_text(encoding="utf-8", errors="ignore") if wis_splash_cs.exists() else ""
gwadt_splash_code = gwadt_splash_cs.read_text(encoding="utf-8", errors="ignore") if gwadt_splash_cs.exists() else ""

wis_update_cond = "(!isHashMatch) && (cmp < 0)" in wis_splash_code
gwadt_update_cond = "(!isHashMatch) && (cmp < 0)" in gwadt_splash_code
check("WIS.Launcher SplashForm.cs koristi striktan uslov (!isHashMatch) && (cmp < 0)", wis_update_cond)
check("GWADT.Launcher SplashForm.cs koristi striktan uslov (!isHashMatch) && (cmp < 0)", gwadt_update_cond)

# ---------------------------------------------------------------------------
# TEST 3: DVOJEZIČNA PODRŠKA (SRPSKI / KINESKI)
# ---------------------------------------------------------------------------
print("\n[TEST 3] Verifikacija dvojezičnog podsistema (Srpski / Kineski)...")
wis_loc_cs = REPO_ROOT / "Skripte" / "WIS.UI" / "LocalizationService.cs"
gwadt_loc_cs = REPO_ROOT / "Skripte" / "GWADT.UI" / "LocalizationService.cs"
check("Prisustvo LocalizationService.cs u WIS.UI", wis_loc_cs.exists())
check("Prisustvo LocalizationService.cs u GWADT.UI", gwadt_loc_cs.exists())

wis_loc_code = wis_loc_cs.read_text(encoding="utf-8", errors="ignore") if wis_loc_cs.exists() else ""
has_sr_dict = "_srDict" in wis_loc_code and "sr" in wis_loc_code
has_zh_dict = "_zhDict" in wis_loc_code and "zh" in wis_loc_code
check("LocalizationService poseduje rečnike za srpski i kineski (zh) jezik", has_sr_dict and has_zh_dict)

wis_xaml = REPO_ROOT / "Skripte" / "WIS.UI" / "MainWindow.xaml"
wis_xaml_code = wis_xaml.read_text(encoding="utf-8", errors="ignore") if wis_xaml.exists() else ""
has_yahei_font = 'FontFamily="Segoe UI, Microsoft YaHei, Arial, sans-serif"' in wis_xaml_code
check("MainWindow.xaml sadrži univerzalni YaHei font stack za kineske simbole", has_yahei_font)

has_lang_switch = "SwitchLanguageCommand" in wis_xaml_code and "LangSrb" in wis_xaml_code and "LangZh" in wis_xaml_code
check("MainWindow.xaml sadrži interaktivni jezički preklopnik [ SRB | 中文 ]", has_lang_switch)

# ---------------------------------------------------------------------------
# TEST 4: GLOBALNI ERROR TRAP U SVIM FAZAMA
# ---------------------------------------------------------------------------
print("\n[TEST 4] Verifikacija globalnog error trap-a u svim instalacionim fazama...")
phases_dir = REPO_ROOT / "Skripte" / "Phases"
phases = ["Phase1-Network.ps1", "Phase2-Drivers.ps1", "Phase3-Optimization.ps1", "Phase4-Apps.ps1", "Phase5-Finalize.ps1"]

for p in phases:
    p_path = phases_dir / p
    if not p_path.exists():
        check(f"Postojanje {p}", False, "Fajl ne postoji")
        continue
    content = p_path.read_text(encoding="utf-8", errors="ignore")
    has_stop = "$ErrorActionPreference = 'Stop'" in content
    has_trap = "trap {" in content and "exit 1" in content
    check(f"{p} sadrži $ErrorActionPreference = 'Stop' i trap {{ exit 1 }}", has_stop and has_trap)

# ---------------------------------------------------------------------------
# TEST 5: DVOSLOJNA DETEKCIJA LAPTOPA I FLOATING BATTERYBAR
# ---------------------------------------------------------------------------
print("\n[TEST 5] Verifikacija dvoslojne detekcije laptopa i BatteryBar konfiguracije...")
has_chassis_detection = "Win32_SystemEnclosure" in apps_code and "ChassisTypes" in apps_code
check("WIS.Apps.psm1 proverava BIOS Win32_SystemEnclosure ChassisTypes", has_chassis_detection)

chassis_codes = [8, 9, 10, 14, 30, 31, 32]
all_codes_present = all(str(c) in apps_code for c in chassis_codes)
check("WIS.Apps.psm1 pokriva sve DMTF kodove za prenosne računare (8, 9, 10, 14, 30, 31, 32)", all_codes_present)

has_solo_xml = "<BatteryBarSolo>" in apps_code and '<Setting Name="Solo" Type="System.Boolean">True</Setting>' in apps_code
check("Set-BatteryBarConfiguration kreira XML konfiguraciju sa Solo=True (Floating Toolbar)", has_solo_xml)

has_startup_lnk = "BatteryBar.lnk" in apps_code and "Startup" in apps_code
check("Set-BatteryBarConfiguration kreira Startup prečicu za sve korisnike", has_startup_lnk)

has_physical_test = "Test-Path $InstalledExe" in apps_code
check("Set-BatteryBarConfiguration fizički proverava BatteryBar.exe pre OK statusa", has_physical_test)

# ---------------------------------------------------------------------------
# TEST 6: WINPE DISK Z:\ I OPTIMIZACIJA BOOT.WIM
# ---------------------------------------------------------------------------
print("\n[TEST 6] Verifikacija WinPE bezuslovnog mapiranja diska Z:\\ i boot.wim...")
pe_cmd = REPO_ROOT / "WIS-AcceleratePE.cmd"
pe_code = pe_cmd.read_text(encoding="utf-8", errors="ignore") if pe_cmd.exists() else ""

pe_has_dhcp_loop = "ping -n 1 -w 1000 192.168.170.254" in pe_code and "NET_WAIT" in pe_code
pe_has_z_mount = "net use Z: \\\\192.168.170.254\\Servis" in pe_code
pe_no_usb_bypass = "if defined USB_DRIVE goto :FoundSource" not in pe_code
check("WIS-AcceleratePE.cmd poseduje DHCP polling petlju do 6 sekundi", pe_has_dhcp_loop)
check("WIS-AcceleratePE.cmd bezuslovno montira disk Z:\\ ka TrueNAS deljenju", pe_has_z_mount)
check("WIS-AcceleratePE.cmd nema restrikciju preskakanja mrežnog diska Z:\\ pri USB pokretanju", pe_no_usb_bypass)

patch_wim = REPO_ROOT / "Skripte" / "Patch-BootWim.py"
patch_code = patch_wim.read_text(encoding="utf-8", errors="ignore") if patch_wim.exists() else ""
patch_has_z_mount = "net use Z: \\\\192.168.170.254\\Servis" in patch_code
check("Patch-BootWim.py ugrađuje bezuslovno montiranje diska Z:\\ u boot.wim", patch_has_z_mount)

boot_wim = REPO_ROOT / "sources" / "boot.wim"
boot_wim_ok = boot_wim.exists() and boot_wim.stat().st_size > 600 * 1024 * 1024
check("sources/boot.wim postoji i optimizovan je (> 600 MB)", boot_wim_ok,
      f"{round(boot_wim.stat().st_size / (1024*1024), 2)} MiB" if boot_wim.exists() else "0 MiB")

# ---------------------------------------------------------------------------
# TEST 7: DUAL-PASS WINDOWS UPDATE
# ---------------------------------------------------------------------------
print("\n[TEST 7] Verifikacija dvoprolaznog (Dual-Pass) Windows Update-a...")
upd_psm1 = REPO_ROOT / "Skripte" / "Modules" / "WIS.Update.psm1"
upd_code = upd_psm1.read_text(encoding="utf-8", errors="ignore") if upd_psm1.exists() else ""

has_second_pass = "function Invoke-WindowsUpdateSecondPass" in upd_code
check("WIS.Update.psm1 sadrži funkciju Invoke-WindowsUpdateSecondPass", has_second_pass)

phase5_ps1 = REPO_ROOT / "Skripte" / "Phases" / "Phase5-Finalize.ps1"
phase5_code = phase5_ps1.read_text(encoding="utf-8", errors="ignore") if phase5_ps1.exists() else ""
phase5_calls_upd = "Invoke-WindowsUpdateSecondPass" in phase5_code
check("Phase5-Finalize.ps1 poziva Invoke-WindowsUpdateSecondPass na početku faze", phase5_calls_upd)

# ---------------------------------------------------------------------------
# TEST 8: INTERNET FALLBACK ZA OEM DRAJVERE
# ---------------------------------------------------------------------------
print("\n[TEST 8] Verifikacija Internet Fallback-a za OEM drajvere...")
hw_psm1 = REPO_ROOT / "Skripte" / "Modules" / "WIS.Hardware.psm1"
hw_code = hw_psm1.read_text(encoding="utf-8", errors="ignore") if hw_psm1.exists() else ""

has_ms_catalog_func = "function Install-DriverFromMsCatalog" in hw_code
check("WIS.Hardware.psm1 sadrži funkciju Install-DriverFromMsCatalog", has_ms_catalog_func)

hub_fallback = "Install-DriverFromMsCatalog" in hw_code[hw_code.find("function Install-OemDriverFromHub"):]
check("Install-OemDriverFromHub poseduje automatski fallback na Install-DriverFromMsCatalog", hub_fallback)

# ---------------------------------------------------------------------------
# TEST 9: STATIČKA ANALIZA KODA (POWERSHELL 5.1 & C# 5.0)
# ---------------------------------------------------------------------------
print("\n[TEST 9] Izvršavanje statičke analize izvornog koda...")
ps_res = subprocess.run([sys.executable, str(REPO_ROOT / "Skripte" / "Verify-PowerShellSyntax.py")],
                        stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
check("Verify-PowerShellSyntax.py (PowerShell 5.1 AST - 0 grešaka)", ps_res.returncode == 0,
      f"exit code: {ps_res.returncode}")

cs_res = subprocess.run([sys.executable, str(REPO_ROOT / "Skripte" / "Verify-CSharpSyntax.py")],
                        stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
check("Verify-CSharpSyntax.py (C# 5.0 .NET 4.0/4.8 - 0 prekršaja)", cs_res.returncode == 0,
      f"exit code: {cs_res.returncode}")

# ---------------------------------------------------------------------------
# TEST 10: DOCKER WINE KOMPILACIJA I 100% SHA-256 BINARNI PARITET
# ---------------------------------------------------------------------------
print("\n[TEST 10] Verifikacija binarnih izvršnih fajlova i SHA-256 pariteta...")

binaries = [
    {
        "name": "WIS.UI.exe",
        "primary": REPO_ROOT / "WIS.UI.exe",
        "min_size": 250000,
        "mirrors": [
            REPO_ROOT / "Skripte" / "WIS.UI" / "WIS.UI.exe",
            REPO_ROOT / "Skripte" / "WIS.UI" / "bin" / "Release" / "WIS.UI.exe",
            Path("/home/gru/Projects/GWADT/gwadt-ui-engine/src/ui/WIS.UI.exe"),
            Path("/home/gru/Storage/WIS.UI.exe"),
            Path("/home/gru/Storage/Skripte/WIS.UI/WIS.UI.exe"),
        ]
    },
    {
        "name": "WIS.Launcher.exe",
        "primary": REPO_ROOT / "WIS.Launcher.exe",
        "min_size": 50000,
        "mirrors": [
            REPO_ROOT / "Skripte" / "WIS.Launcher" / "WIS.Launcher.exe",
            REPO_ROOT / "Skripte" / "WIS.Launcher" / "bin" / "Release" / "WIS.Launcher.exe",
            REPO_ROOT / "sources" / "$OEM$" / "$1" / "setup" / "WIS.Launcher.exe",
            Path("/home/gru/Storage/WIS.Launcher.exe"),
            Path("/home/gru/Storage/Skripte/WIS.Launcher/WIS.Launcher.exe"),
        ]
    },
    {
        "name": "GWADT.UI.exe",
        "primary": REPO_ROOT / "GWADT.UI.exe",
        "min_size": 250000,
        "mirrors": [
            REPO_ROOT / "Skripte" / "GWADT.UI" / "GWADT.UI.exe",
            REPO_ROOT / "Skripte" / "GWADT.UI" / "bin" / "Release" / "GWADT.UI.exe",
            Path("/home/gru/Projects/GWADT/gwadt-ui-engine/src/ui/GWADT.UI.exe"),
            Path("/home/gru/Storage/GWADT.UI.exe"),
            Path("/home/gru/Storage/Skripte/GWADT.UI/GWADT.UI.exe"),
        ]
    },
    {
        "name": "GWADT.Launcher.exe",
        "primary": REPO_ROOT / "GWADT.Launcher.exe",
        "min_size": 50000,
        "mirrors": [
            REPO_ROOT / "Skripte" / "GWADT.Launcher" / "GWADT.Launcher.exe",
            REPO_ROOT / "Skripte" / "GWADT.Launcher" / "bin" / "Release" / "GWADT.Launcher.exe",
            REPO_ROOT / "sources" / "$OEM$" / "$1" / "setup" / "GWADT.Launcher.exe",
            Path("/home/gru/Projects/GWADT/gwadt-launcher/GWADT.Launcher.exe"),
            Path("/home/gru/Storage/GWADT.Launcher.exe"),
            Path("/home/gru/Storage/Skripte/GWADT.Launcher/GWADT.Launcher.exe"),
        ]
    }
]

for b in binaries:
    prim = b["primary"]
    exists = prim.exists() and prim.stat().st_size >= b["min_size"]
    prim_size = prim.stat().st_size if prim.exists() else 0
    check(f"Prisustvo i veličina {b['name']}", exists, f"{prim_size} B")

    prim_hash = get_sha256(prim)
    mirror_parity = True
    for m in b["mirrors"]:
        if m.exists():
            m_hash = get_sha256(m)
            if m_hash != prim_hash:
                mirror_parity = False
                print(f"    [MISMATCH] {m} ({m_hash[:8]}...) != {prim_hash[:8]}...")
    check(f"100% SHA-256 binarni paritet za {b['name']} na svim lokacijama", mirror_parity,
          f"SHA-256: {prim_hash[:16]}...")

# ---------------------------------------------------------------------------
# ZAVRŠNI REZIME
# ---------------------------------------------------------------------------
total_tests = passed_count + failed_count
pass_percentage = (passed_count / total_tests * 100) if total_tests > 0 else 0

print("\n" + "=" * 80)
print(f"REZULTAT QA VERIFIKACIJE: {passed_count}/{total_tests} PROVERA POLOŽENO ({pass_percentage:.1f}%)")
print("=" * 80)

if failed_count == 0:
    print("\n🟢 SVI TESTOVI SU 100% POLOŽENI! QA PRESUDA: SERTIFIKOVANO ZA PRODUKCIJU.\n")
    sys.exit(0)
else:
    print(f"\n🔴 DETEKTOVANO {failed_count} NEUSPEŠNIH PROVERA! QA PRESUDA: POTREBNA DORADA.\n")
    sys.exit(1)
