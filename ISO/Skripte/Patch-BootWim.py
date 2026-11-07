#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
WIS Patch-BootWim.py
=============================================================================
Uloga: wis_windows_admin
Zadatak: WIS-TASK-131 (RST v20 Podrazumevani za Gen 12-14 / Core 5 120U / Core Ultra, RST v19 za Tiger Lake DEV_9A0B)
Namena: Automatizovano ubacivanje Intel VMD RST drajvera (20.0 + 19.5 + 18.0 Legacy),
        kuriranih mrežnih paketa, kreiranje X:\\VentoyAutoRun.bat, ažuriranje
        startnet.cmd (Tag-based bypass za Ventoy + High Performance power plan)
        i uklanjanje restriktivnog winpeshl.ini iz OBA INDEKSA
        u sources/boot.wim (Index 1 - WinPE, Index 2 - Windows Setup) koristeći
        wimlib-imagex update bez potrebe za mountovanjem slike.
=============================================================================
"""

import sys
import os
import argparse
import subprocess
import shutil
import tempfile
from pathlib import Path


VENTOY_AUTORUN_BAT_CONTENT = """@echo off
title WIS VentoyAutoRun Pre-Boot
echo =======================================================
echo  WIS Ventoy Pre-Boot: Inicijalizacija drajvera...
echo =======================================================
wpeinit
powercfg.exe /s 8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c >nul 2>&1

:: 1. Ucitavanje mreznih drajvera (LAN / Wi-Fi)
echo [*] Ucitavam mrezne drajvere...
if exist "X:\\Drivers\\Net" (
    for /R "X:\\Drivers\\Net" %%f in (*.inf) do (
        drvload.exe "%%f" >nul 2>&1
        pnputil.exe /add-driver "%%f" /install >nul 2>&1
    )
)
echo [*] Inicijalizujem mrezni podsistem...
wpeutil InitializeNetwork >nul 2>&1

:: 2. Ucitavanje Intel VMD skladisnog drajvera
echo [*] WIS: Detekcija Intel VMD skladisnog kontrolera...
set "IS_GEN11_TIGERLAKE="
reg.exe query "HKLM\\SYSTEM\\CurrentControlSet\\Enum\\PCI" /s 2>nul | find.exe /I "DEV_9A0B" >nul 2>&1
if not errorlevel 1 set "IS_GEN11_TIGERLAKE=1"
if not defined IS_GEN11_TIGERLAKE (
    reg.exe query "HKLM\\SYSTEM\\ControlSet001\\Enum\\PCI" /s 2>nul | find.exe /I "DEV_9A0B" >nul 2>&1
    if not errorlevel 1 set "IS_GEN11_TIGERLAKE=1"
)

    if defined IS_GEN11_TIGERLAKE (
        echo [+] WIS: Detektovan Intel VMD Gen 11 (Tiger Lake DEV_9A0B). Ucitavam RST v19...
        drvload.exe "X:\\Drivers\\VMD\\02_RST_v19_Gen11-13\\iaStorVD.inf"
        if errorlevel 1 drvload.exe "X:\\Drivers\\VMD\\03_RST_v18_Legacy_Optane\\iaStorVD.inf" >nul 2>&1
        pnputil.exe /add-driver "X:\\Drivers\\VMD\\02_RST_v19_Gen11-13\\iaStorVD.inf" /install >nul 2>&1
    ) else (
        echo [+] WIS: Intel VMD Gen 12-14 / Core 5 120U / Core Ultra. Ucitavam RST v20...
        drvload.exe "X:\\Drivers\\VMD\\01_RST_v20_Gen12-14_CoreUltra\\iaStorVD.inf"
        if errorlevel 1 (
            drvload.exe "X:\\Drivers\\VMD\\02_RST_v19_Gen11-13\\iaStorVD.inf" >nul 2>&1
            drvload.exe "X:\\Drivers\\VMD\\03_RST_v18_Legacy_Optane\\iaStorVD.inf" >nul 2>&1
        )
        pnputil.exe /add-driver "X:\\Drivers\\VMD\\01_RST_v20_Gen12-14_CoreUltra\\iaStorVD.inf" /install >nul 2>&1
    )

    echo [*] WIS: Osvezavanje skladisnih uredjaja...
    pnputil.exe /scan-devices >nul 2>&1
    (
    echo san policy=OnlineAll
    echo rescan
    ) | diskpart >nul 2>&1

    echo 1 > "X:\\WIS_PreBoot_Done.tag"
    echo 1 > "X:\\GWADT_PreBoot_Done.tag"
    echo [*] Gotovo. Nastavljam Ventoy proces...
    exit /b 0
    """

STARTNET_CMD_CONTENT = r"""@echo off
title WIS Universal WinPE Boot & Auto-Deploy
wpeinit

:: Provera da li je VentoyAutoRun vec obavio pre-boot ucitavanje
if exist "X:\WIS_PreBoot_Done.tag" goto :StartnetPreBootDone

powercfg.exe /s 8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c >nul 2>&1

:: 1. Ucitavanje mreznih drajvera (LAN i Wi-Fi) za povezivanje na server
echo [*] WIS: Ucitavam mrezne drajvere (LAN i Wi-Fi)...
if exist "X:\Drivers\Net" (
    for /R "X:\Drivers\Net" %%f in (*.inf) do (
        drvload.exe "%%f" >nul 2>&1
        pnputil.exe /add-driver "%%f" /install >nul 2>&1
    )
)
echo [*] WIS: Inicijalizujem mrezni podsistem...
wpeutil InitializeNetwork >nul 2>&1

:: 2. Ucitavanje Intel VMD skladisnog drajvera
echo [*] WIS: Detekcija Intel VMD skladisnog kontrolera...
set "IS_GEN11_TIGERLAKE="
reg.exe query "HKLM\SYSTEM\CurrentControlSet\Enum\PCI" /s 2>nul | find.exe /I "DEV_9A0B" >nul 2>&1
if not errorlevel 1 set "IS_GEN11_TIGERLAKE=1"
if not defined IS_GEN11_TIGERLAKE (
    reg.exe query "HKLM\SYSTEM\ControlSet001\Enum\PCI" /s 2>nul | find.exe /I "DEV_9A0B" >nul 2>&1
    if not errorlevel 1 set "IS_GEN11_TIGERLAKE=1"
)

if defined IS_GEN11_TIGERLAKE (
    echo [+] WIS: Detektovan Intel VMD Gen 11 (Tiger Lake DEV_9A0B). Ucitavam RST v19...
    drvload.exe "X:\Drivers\VMD\02_RST_v19_Gen11-13\iaStorVD.inf"
    if errorlevel 1 drvload.exe "X:\Drivers\VMD\03_RST_v18_Legacy_Optane\iaStorVD.inf" >nul 2>&1
    pnputil.exe /add-driver "X:\Drivers\VMD\02_RST_v19_Gen11-13\iaStorVD.inf" /install >nul 2>&1
) else (
    echo [+] WIS: Intel VMD Gen 12-14 / Core 5 120U / Core Ultra. Ucitavam RST v20...
    drvload.exe "X:\Drivers\VMD\01_RST_v20_Gen12-14_CoreUltra\iaStorVD.inf"
    if errorlevel 1 (
        drvload.exe "X:\Drivers\VMD\02_RST_v19_Gen11-13\iaStorVD.inf" >nul 2>&1
        drvload.exe "X:\Drivers\VMD\03_RST_v18_Legacy_Optane\iaStorVD.inf" >nul 2>&1
    )
    pnputil.exe /add-driver "X:\Drivers\VMD\01_RST_v20_Gen12-14_CoreUltra\iaStorVD.inf" /install >nul 2>&1
)

echo [*] WIS: Osvezavanje skladisnih uredjaja...
pnputil.exe /scan-devices >nul 2>&1
(
echo san policy=OnlineAll
echo rescan
) | diskpart >nul 2>&1

echo 1 > "X:\WIS_PreBoot_Done.tag"
echo 1 > "X:\GWADT_PreBoot_Done.tag"

:StartnetPreBootDone
:: 3. Provera detektovanih skladisnih uredjaja
echo =======================================================
echo [*] WIS: Provera detektovanih skladisnih uredjaja:
echo list disk | diskpart 2>nul | find.exe /I "Disk "
echo =======================================================

:: 4. Mrezna inicijalizacija i montiranje TrueNAS SMB 3.1.1 (Z:)
reg.exe add "HKLM\SYSTEM\CurrentControlSet\Services\LanmanWorkstation\Parameters" /v "AllowInsecureGuestAuth" /t REG_DWORD /d 1 /f >nul 2>&1
reg.exe add "HKLM\SYSTEM\CurrentControlSet\Services\LanmanWorkstation\Parameters" /v "RequireSecuritySignature" /t REG_DWORD /d 0 /f >nul 2>&1

:: Cekanje na DHCP IP adresu i mrezni link ka 192.168.170.254 (do 6 sekundi)
set /a NET_WAIT=0
:PingLoop
ping -n 1 -w 1000 192.168.170.254 >nul 2>&1
if %errorlevel% equ 0 goto :NetworkReady
set /a NET_WAIT+=1
if %NET_WAIT% leq 6 (
    echo [*] Cekam DHCP mrezni link ka 192.168.170.254 (pokusaj %NET_WAIT%/6)...
    timeout /t 1 /nobreak >nul 2>&1
    goto :PingLoop
)
:NetworkReady

:: Dodavanje hostname aliasa u HOSTS fajl radi sprecavanja kolizije sa iVentoy portom 10445
set "HOSTS_FILE=%SystemRoot%\System32\drivers\etc\hosts"
findstr /I /C:"truenas.servis" "%HOSTS_FILE%" >nul 2>&1
if errorlevel 1 (
    echo. >> "%HOSTS_FILE%" 2>nul
    echo 192.168.170.254 truenas.servis >> "%HOSTS_FILE%" 2>nul
    echo 192.168.170.254 TRUENAS >> "%HOSTS_FILE%" 2>nul
)

:: Bezuslovno mapiranje TrueNAS WIS / Servis deljenog resursa na disk Z:
echo [*] Mapiram TrueNAS SMB 3.1.1 (\\truenas.servis\WIS / \\192.168.170.254\Servis) na disk Z:...
if exist "X:\Windows\System32\WIS-ConnectNAS.cmd" (
    call "X:\Windows\System32\WIS-ConnectNAS.cmd" Z: >nul 2>&1
)
if not exist "Z:\" net use Z: \\truenas.servis\WIS /user:guest "" >nul 2>&1
if not exist "Z:\" net use Z: \\192.168.170.254\WIS /user:guest "" >nul 2>&1
if not exist "Z:\" net use Z: \\192.168.170.254\Servis /user:guest "" >nul 2>&1
if not exist "Z:\" net use Z: \\192.168.170.254\Servis /user:nobody "" >nul 2>&1
if not exist "Z:\" net use Z: \\192.168.170.254\Servis >nul 2>&1

if exist "Z:\" (
    echo [+] TrueNAS deljenje montirano na Z:\
) else (
    echo [-] NAS server nije dostupan na lokalnoj mrezi - rad se nastavlja sa lokalnog medijuma
)

:: Detekcija iVentoy / virtuelnog CD-ROM ISO drajva (za cuvanje $OEM$ i autounattend.xml sa butovanog ISO-a)
set "WIS_BOOT_OEM="
set "WIS_BOOT_UNATTEND="
for %%d in (Y C D E F G H I J K L M N O P Q R S T U V W) do (
    if exist "%%d:\sources\$OEM$\$1\setup\WIS.Launcher.exe" (
        if not defined WIS_BOOT_OEM set "WIS_BOOT_OEM=%%d:\sources\$OEM$"
        if exist "%%d:\autounattend.xml" if not defined WIS_BOOT_UNATTEND set "WIS_BOOT_UNATTEND=%%d:\autounattend.xml"
    )
)

:: 5. PRIORITET 1: Pretraga pravog fizickog USB medijuma (iskljucujuci CD-ROM / iVentoy virtualni drajv)
set "WIS_SOURCE="
echo [*] WIS: Provera prisustva fizickog USB instalacionog medijuma...
for %%d in (C D E F G H I J K L M N O P Q R S T U V W) do (
    if exist "%%d:\sources\install.wim" (
        fsutil fsinfo drivetype %%d: 2>nul | findstr /I "CD-ROM" >nul 2>&1
        if errorlevel 1 (
            set "WIS_SOURCE=%%d:"
            echo [+] WIS: Pronadjen fizicki USB instalacioni medijum na %%d:!
            goto :LaunchSetup
        )
    )
    if exist "%%d:\sources\install.esd" (
        fsutil fsinfo drivetype %%d: 2>nul | findstr /I "CD-ROM" >nul 2>&1
        if errorlevel 1 (
            set "WIS_SOURCE=%%d:"
            echo [+] WIS: Pronadjen fizicki USB instalacioni medijum na %%d:!
            goto :LaunchSetup
        )
    )
)

:: 6. PRIORITET 2: Ako fizicki USB nije pronadjen -> Koristi TrueNAS SMB 3.1.1 sa montiranog Z:
if exist "Z:\win11\sources\install.wim" (
    set "WIS_SOURCE=Z:\win11"
    echo [+] WIS: Aktiviran brzi TrueNAS SMB 3.1.1 prenos sa Z:\win11 (115 MB/s)!
    goto :LaunchSetup
)
if exist "Z:\win11\sources\install.esd" (
    set "WIS_SOURCE=Z:\win11"
    echo [+] WIS: Aktiviran brzi TrueNAS SMB 3.1.1 prenos sa Z:\win11 (115 MB/s)!
    goto :LaunchSetup
)
if exist "Z:\sources\install.wim" (
    set "WIS_SOURCE=Z:"
    echo [+] WIS: Aktiviran brzi TrueNAS SMB 3.1.1 prenos sa Z: (115 MB/s)!
    goto :LaunchSetup
)
if exist "Z:\sources\install.esd" (
    set "WIS_SOURCE=Z:"
    echo [+] WIS: Aktiviran brzi TrueNAS SMB 3.1.1 prenos sa Z: (115 MB/s)!
    goto :LaunchSetup
)

:: 7. PRIORITET 3: Fallback na sve preostale drajvove (ukljucujuci iVentoy CD-ROM / lokalni disk)
echo =======================================================
echo  [*] WIS Fallback: Pretrazujem sve preostale drajvove (iVentoy CD-ROM / lokalni disk)...
echo =======================================================
for %%d in (C D E F G H I J K L M N O P Q R S T U V W Y) do (
    if exist "%%d:\sources\install.wim" (
        set "WIS_SOURCE=%%d:"
        echo [+] WIS Fallback: Pronadjen instalacioni medijum na %%d:!
        goto :LaunchSetup
    )
    if exist "%%d:\sources\install.esd" (
        set "WIS_SOURCE=%%d:"
        echo [+] WIS Fallback: Pronadjen instalacioni medijum na %%d:!
        goto :LaunchSetup
    )
)

echo =======================================================
echo [!] GRESKA: Nijedan instalacioni medijum (install.wim / install.esd) nije pronadjen!
echo Molimo proverite status TrueNAS servera ili ubacite USB instalacioni fles.
echo =======================================================
pause
goto :eof

:LaunchSetup
echo =======================================================
echo  Pokrecem Windows 11 Automatizovanu Instalaciju...
echo  Izvor: %WIS_SOURCE%
echo =======================================================

pnputil.exe /scan-devices >nul 2>&1
(
echo san policy=OnlineAll
echo rescan
) | diskpart >nul 2>&1

:: Pokretanje Windows Setup-a sa autounattend.xml odgovorima i ocuvanjem $OEM$ sa butovanog ISO-a
if defined WIS_BOOT_OEM if exist "%WIS_BOOT_OEM%" (
    if defined WIS_BOOT_UNATTEND if exist "%WIS_BOOT_UNATTEND%" (
        "%WIS_SOURCE%\setup.exe" /unattend:"%WIS_BOOT_UNATTEND%" /m:"%WIS_BOOT_OEM%"
        goto :eof
    )
)
if exist "%WIS_SOURCE%\autounattend.xml" (
    "%WIS_SOURCE%\setup.exe" /unattend:"%WIS_SOURCE%\autounattend.xml"
) else if exist "X:\autounattend.xml" (
    "%WIS_SOURCE%\setup.exe" /unattend:"X:\autounattend.xml"
) else (
    "%WIS_SOURCE%\setup.exe" /unattend:"%WIS_SOURCE%\autounattend.xml"
)
"""

ACCELERATE_PE_CMD_CONTENT = r"""@echo off
title WIS WinPE TrueNAS SMB 3.1.1 Accelerator
echo =======================================================
echo  [*] WIS Accelerator: Mrezna Akceleracija na SMB 3.1.1
echo =======================================================

:: 1. Omogucavanje SMB Guest autentifikacije i iskljucivanje potpisa za maksimalni protok
reg add "HKLM\SYSTEM\CurrentControlSet\Services\LanmanWorkstation\Parameters" /v "AllowInsecureGuestAuth" /t REG_DWORD /d 1 /f >nul 2>&1
reg add "HKLM\SYSTEM\CurrentControlSet\Services\LanmanWorkstation\Parameters" /v "RequireSecuritySignature" /t REG_DWORD /d 0 /f >nul 2>&1

:: 2. Inicijalizacija mreznog podsistema
wpeutil InitializeNetwork >nul 2>&1

:: 3. Robusna petlja cekanja (do 6 sekundi) dok mrezni interfejs ne dobije IP adresu od DHCP servera
set /a NET_WAIT=0
:WaitForNetwork
ping -n 1 -w 1000 192.168.170.254 >nul 2>&1
if %errorlevel% equ 0 goto :NetworkReady
set /a NET_WAIT+=1
if %NET_WAIT% leq 6 (
    echo [*] Cekam DHCP mrezni link ka 192.168.170.254 (pokusaj %NET_WAIT%/6)...
    timeout /t 1 /nobreak >nul 2>&1
    goto :WaitForNetwork
)
:NetworkReady

:: 4. Dodavanje hostname aliasa u HOSTS fajl radi sprecavanja kolizije sa iVentoy portom 10445
set "HOSTS_FILE=%SystemRoot%\System32\drivers\etc\hosts"
echo. >> "%HOSTS_FILE%" 2>nul
echo 192.168.170.254 truenas.servis >> "%HOSTS_FILE%" 2>nul
echo 192.168.170.254 TRUENAS >> "%HOSTS_FILE%" 2>nul

:: 5. Bezuslovno mapiranje TrueNAS Servis deljenog resursa na disk Z:
set "MAPPED_NAS="
echo [*] Povezujem TrueNAS SMB 3.1.1 na disk Z:...
net use Z: \\192.168.170.254\Servis /user:guest "" >nul 2>&1
if not exist "Z:\" (
    net use Z: \\truenas.servis\Servis /user:guest "" >nul 2>&1
)
if not exist "Z:\" (
    net use Z: \\TRUENAS\Servis /user:guest "" >nul 2>&1
)
if not exist "Z:\" (
    net use Z: \\192.168.170.254\Servis /user:nobody "" >nul 2>&1
)
if not exist "Z:\" (
    net use Z: \\192.168.170.254\Servis >nul 2>&1
)

if exist "Z:\" (
    set "MAPPED_NAS=1"
    echo [+] TrueNAS Servis deljenje montirano na Z:\
) else (
    echo [-] NAS server nije dostupan na lokalnoj mrezi - rad se nastavlja sa lokalnog medijuma
)

:: 6. Provera da li instalacija koristi spori iVentoy virtuelni CD-ROM (Y:)
set "IS_IVENTOY="
if exist "T:\iventoy.iso" set "IS_IVENTOY=1"
set "HAS_Y_IMAGE="
if exist "Y:\sources\install.esd" set "HAS_Y_IMAGE=1"
if exist "Y:\sources\install.wim" set "HAS_Y_IMAGE=1"
if not defined IS_IVENTOY if defined HAS_Y_IMAGE (
    fsutil fsinfo drivetype Y: 2>nul | findstr /I "CD-ROM" >nul 2>&1
    if not errorlevel 1 set "IS_IVENTOY=1"
)

if defined IS_IVENTOY (
    echo [*] WIS Accelerator: Detektovan spori iVentoy virtuelni CD-ROM (10-12 MB/s).

    :: Zastita od samoubistva procesa: ako je skripta pokrenuta sa diska Y:
    if /I "%~d0"=="Y:" (
        echo [*] WIS Accelerator: Skripta se izvrsava sa diska Y: - preskacem demontiranje diska Y: radi stabilnosti.
        goto :SkipDetachY
    )

    echo [*] WIS Accelerator: Preusmeravam instalaciju na brzi TrueNAS SMB 3.1.1 (115 MB/s)...

    :: Demontiranje sporog iVentoy diska Y: i oslobadjanje resursa
    (
    echo select vdisk file="T:\iventoy.iso"
    echo detach vdisk
    ) | diskpart >nul 2>&1
    mountvol Y: /D >nul 2>&1
    mountvol Y: /P >nul 2>&1
    net use T: /delete /y >nul 2>&1

    :: Povezivanje brzog SMB 3.1.1 share-a na disk Y:\
    net use Y: \\truenas.servis\Servis\win11 /user:guest "" >nul 2>&1
    set "HAS_IMAGE=" & if exist "Y:\sources\install.esd" set "HAS_IMAGE=1" & if exist "Y:\sources\install.wim" set "HAS_IMAGE=1"
    if not defined HAS_IMAGE (
        net use Y: \\TRUENAS\Servis\win11 /user:guest "" >nul 2>&1
        set "HAS_IMAGE=" & if exist "Y:\sources\install.esd" set "HAS_IMAGE=1" & if exist "Y:\sources\install.wim" set "HAS_IMAGE=1"
    )
    if not defined HAS_IMAGE (
        net use Y: \\192.168.170.254\Servis\win11 /TCPPORT:445 /user:guest "" >nul 2>&1
        set "HAS_IMAGE=" & if exist "Y:\sources\install.esd" set "HAS_IMAGE=1" & if exist "Y:\sources\install.wim" set "HAS_IMAGE=1"
    )
    if not defined HAS_IMAGE (
        net use Y: \\truenas.servis\Servis\win11 /user:nobody "" >nul 2>&1
        set "HAS_IMAGE=" & if exist "Y:\sources\install.esd" set "HAS_IMAGE=1" & if exist "Y:\sources\install.wim" set "HAS_IMAGE=1"
    )
    if not defined HAS_IMAGE (
        net use Y: \\truenas.servis\Servis\win11 >nul 2>&1
        set "HAS_IMAGE=" & if exist "Y:\sources\install.esd" set "HAS_IMAGE=1" & if exist "Y:\sources\install.wim" set "HAS_IMAGE=1"
    )

:SkipDetachY
    set "HAS_IMAGE=" & if exist "Y:\sources\install.esd" set "HAS_IMAGE=1" & if exist "Y:\sources\install.wim" set "HAS_IMAGE=1"
    if defined HAS_IMAGE (
        echo =======================================================
        echo  [+] WIS Accelerator: TrueNAS SMB 3.1.1 USPESNO AKTIVIRAN na Y:\!
        echo  [+] Protok: ~115 MB/s ^| Ocekivano trajanje: ~58 sekundi!
        echo =======================================================
    ) else (
        echo [*] WIS Accelerator: Slovo Y: zauzeto, ali Z:\win11 je spreman za Setup.
    )
) else (
    echo [*] WIS Accelerator: Rad se nastavlja sa lokalnog medijuma (USB/NVMe) uz aktivan TrueNAS disk Z:\.
)

:Done
exit /b 0
"""


def log(msg, tag="*"):
    print(f"[{tag}] {msg}")


def log_success(msg):
    print(f"[+] {msg}")


def log_error(msg):
    print(f"[!] GREŠKA: {msg}", file=sys.stderr)


def log_step(msg):
    print(f"\n===> {msg}")


def check_prerequisites(wim_tool="wimlib-imagex"):
    """Provera prisustva alata wimlib-imagex."""
    tool_path = shutil.which(wim_tool)
    if not tool_path:
        log_error(f"Alat '{wim_tool}' nije pronađen u PATH-u sistema.")
        log_error("Instalirajte ga komandom: sudo apt install wimtools")
        return None
    return tool_path


def find_project_root():
    """Pronalazi root direktorijum projekta na osnovu lokacije skripte."""
    current = Path(__file__).resolve().parent
    if (current.parent / "sources" / "boot.wim").exists():
        return current.parent
    if (current / "sources" / "boot.wim").exists():
        return current
    cwd = Path(os.getcwd())
    if (cwd / "sources" / "boot.wim").exists():
        return cwd
    return current.parent


def get_wim_image_indices(boot_wim_path, wim_tool="wimlib-imagex"):
    """Dohvata listu dostupnih indeksa u WIM fajlu."""
    cmd = [wim_tool, "info", str(boot_wim_path)]
    res = subprocess.run(cmd, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
    if res.returncode != 0:
        log_error(f"Neuspešno čitanje informacija iz boot.wim: {res.stderr.strip()}")
        return [1, 2]
    
    indices = []
    for line in res.stdout.splitlines():
        line = line.strip()
        if line.startswith("Index:"):
            try:
                idx = int(line.split(":")[1].strip())
                indices.append(idx)
            except ValueError:
                pass
    return indices if indices else [1, 2]


def verify_boot_wim_contents(boot_wim_path, image_index, wim_tool="wimlib-imagex"):
    """Verifikuje prisustvo i odsustvo ključnih datoteka unutar zadatog boot.wim indeksa."""
    log_step(f"Verifikacija sadržaja boot.wim (Index {image_index})...")

    # Dohvati listing fajlova
    cmd = [wim_tool, "dir", str(boot_wim_path), str(image_index)]
    res = subprocess.run(cmd, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
    if res.returncode != 0:
        log_error(f"Neuspešno čitanje direktorijuma iz boot.wim (Index {image_index}): {res.stderr.strip()}")
        return False

    wim_files = set(line.strip().replace("\\", "/") for line in res.stdout.splitlines())

    checks = [
        ("/Drivers/VMD/01_RST_v20_Gen12-14_CoreUltra/iaStorVD.inf", "Intel VMD RST v20 (Gen 12-14 / Core 5 120U / Core Ultra)"),
        ("/Drivers/VMD/02_RST_v19_Gen11-13/iaStorVD.inf", "Intel VMD RST v19 (Tiger Lake 11th Gen DEV_9A0B)"),
        ("/Drivers/VMD/03_RST_v18_Legacy_Optane/iaStorVD.inf", "Intel VMD RST v18 (Legacy Optane)"),
        ("/Drivers/Net", "Direktorijum mrežnih drajvera"),
        ("/autounattend.xml", "Embedded autounattend.xml konfiguracija"),
        ("/VentoyAutoRun.bat", "VentoyAutoRun pre-boot skripta"),
        ("/Windows/System32/startnet.cmd", "WinPE startnet.cmd skripta"),
        ("/Windows/System32/load_vmd.cmd", "WIS VMD Loader skripta za Windows Setup"),
        ("/Windows/System32/WIS-AcceleratePE.cmd", "WIS WinPE TrueNAS SMB 3.1.1 Accelerator skripta"),
    ]

    all_passed = True
    for path, desc in checks:
        found = any(f == path or f.startswith(path + "/") for f in wim_files)
        if found:
            log_success(f"[Index {image_index}] Pronađen {desc}: {path}")
        else:
            log_error(f"[Index {image_index}] Nedostaje {desc}: {path}")
            all_passed = False

    # Provera da winpeshl.ini, wis_preboot.cmd i $WinPEDriver$ NE SMEJU postojati
    forbidden = [
        ("/Windows/System32/winpeshl.ini", "winpeshl.ini (ne sme postojati da ne blokira iVentoy)"),
        ("/Windows/System32/wis_preboot.cmd", "wis_preboot.cmd (zamenjen sa VentoyAutoRun.bat i startnet.cmd)"),
        ("/$WinPEDriver$", "$WinPEDriver$ (ne sme postojati da Windows Setup ne bi pucao sa 0x80070103 0x40031)"),
    ]

    for f_path, f_desc in forbidden:
        if f_path in wim_files:
            log_error(f"[Index {image_index}] NEDOZVOLJEN FAJL PRISUTAN {f_desc}: {f_path}")
            all_passed = False
        else:
            log_success(f"[Index {image_index}] Potvrđeno odsustvo {f_desc}: {f_path}")

    # Detaljna provera sadržaja VentoyAutoRun.bat
    extract_ventoy_cmd = [wim_tool, "extract", str(boot_wim_path), str(image_index),
                          "/VentoyAutoRun.bat", "--to-stdout"]
    v_res = subprocess.run(extract_ventoy_cmd, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
    if v_res.returncode == 0:
        if ("drvload.exe" in v_res.stdout and "InitializeNetwork" in v_res.stdout
                and "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c" in v_res.stdout and "WIS_PreBoot_Done.tag" in v_res.stdout
                and "DEV_9A0B" in v_res.stdout and "01_RST_v20_Gen12-14_CoreUltra" in v_res.stdout
                and "02_RST_v19_Gen11-13" in v_res.stdout and "diskpart" in v_res.stdout
                and "pnputil.exe" in v_res.stdout and "OnlineAll" in v_res.stdout):
            log_success(f"[Index {image_index}] Verifikovan tačan sadržaj /VentoyAutoRun.bat (Ciljana VMD detekcija DEV_9A0B / RST v20 + pnputil / OnlineAll)")
        else:
            log_error(f"[Index {image_index}] Sadržaj /VentoyAutoRun.bat ne sadrži očekivane komande!")
            all_passed = False
    else:
        log_error(f"[Index {image_index}] Nije moguće pročitati /VentoyAutoRun.bat: {v_res.stderr.strip()}")
        all_passed = False

    # Detaljna provera sadržaja startnet.cmd
    extract_startnet_cmd = [wim_tool, "extract", str(boot_wim_path), str(image_index),
                            "/Windows/System32/startnet.cmd", "--to-stdout"]
    s_res = subprocess.run(extract_startnet_cmd, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
    if s_res.returncode == 0:
        if ("wpeinit" in s_res.stdout and "drvload.exe" in s_res.stdout
                and "InitializeNetwork" in s_res.stdout and "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c" in s_res.stdout
                and "WIS_PreBoot_Done.tag" in s_res.stdout and "192.168.170.254" in s_res.stdout
                and "DEV_9A0B" in s_res.stdout and "01_RST_v20_Gen12-14_CoreUltra" in s_res.stdout
                and "02_RST_v19_Gen11-13" in s_res.stdout and "setup.exe" in s_res.stdout
                and "pnputil.exe" in s_res.stdout and "OnlineAll" in s_res.stdout):
            log_success(f"[Index {image_index}] Verifikovan tačan sadržaj /Windows/System32/startnet.cmd (Ciljana VMD detekcija DEV_9A0B, pnputil, OnlineAll i Setup pokretanje)")
        else:
            log_error(f"[Index {image_index}] Sadržaj /Windows/System32/startnet.cmd ne sadrži očekivane komande!")
            all_passed = False
    else:
        log_error(f"[Index {image_index}] Nije moguće pročitati /Windows/System32/startnet.cmd: {s_res.stderr.strip()}")
        all_passed = False

    # Detaljna provera sadržaja load_vmd.cmd
    extract_vmd_cmd = [wim_tool, "extract", str(boot_wim_path), str(image_index),
                       "/Windows/System32/load_vmd.cmd", "--to-stdout"]
    l_res = subprocess.run(extract_vmd_cmd, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
    if l_res.returncode == 0:
        if ("iaStorVD.inf" in l_res.stdout and "pnputil.exe" in l_res.stdout and "OnlineAll" in l_res.stdout):
            log_success(f"[Index {image_index}] Verifikovan tačan sadržaj /Windows/System32/load_vmd.cmd")
        else:
            log_error(f"[Index {image_index}] Sadržaj /Windows/System32/load_vmd.cmd ne sadrži očekivane komande!")
            all_passed = False
    else:
        log_error(f"[Index {image_index}] Nije moguće pročitati /Windows/System32/load_vmd.cmd: {l_res.stderr.strip()}")
        all_passed = False

    # Detaljna provera sadržaja WIS-AcceleratePE.cmd
    extract_accel_cmd = [wim_tool, "extract", str(boot_wim_path), str(image_index),
                         "/Windows/System32/WIS-AcceleratePE.cmd", "--to-stdout"]
    a_res = subprocess.run(extract_accel_cmd, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
    if a_res.returncode == 0:
        if ("192.168.170.254" in a_res.stdout and "detach vdisk" in a_res.stdout
                and "AllowInsecureGuestAuth" in a_res.stdout
                and ("WIS\\win11" in a_res.stdout or "Servis\\win11" in a_res.stdout)):
            log_success(f"[Index {image_index}] Verifikovan tačan sadržaj /Windows/System32/WIS-AcceleratePE.cmd (TrueNAS SMB 3.1.1 akcelerator)")
        else:
            log_error(f"[Index {image_index}] Sadržaj /Windows/System32/WIS-AcceleratePE.cmd ne sadrži očekivane komande!")
            all_passed = False
    # Detaljna provera sadržaja /autounattend.xml
    extract_unattend_cmd = [wim_tool, "extract", str(boot_wim_path), str(image_index),
                            "/autounattend.xml", "--to-stdout"]
    u_res = subprocess.run(extract_unattend_cmd, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
    if u_res.returncode == 0:
        unattend_text = u_res.stdout
        if ("AcceleratePE.cmd" not in unattend_text and "BypassTPMCheck" in unattend_text
                and "BypassDiskCheck" in unattend_text and "<Order>1</Order>" in unattend_text
                and "<Order>6</Order>" in unattend_text):
            log_success(f"[Index {image_index}] Verifikovan čisti /autounattend.xml (bez eksternih skripti u windowsPE, LabConfig Order 1-6 prisutan)")
        else:
            log_error(f"[Index {image_index}] /autounattend.xml sadrži nedozvoljene skripte ili mu nedostaje LabConfig Order 1-6!")
            all_passed = False
    else:
        log_error(f"[Index {image_index}] Nije moguće pročitati /autounattend.xml: {u_res.stderr.strip()}")
        all_passed = False

    return all_passed


def prepare_vmd_staging(vmd_v20_dir, vmd_v19_dir, vmd_v18_dir, target_dir):
    """Priprema staging strukturu za VMD drajvere (RST 20.0 + RST 19.5 + Legacy 18.0 + Optane/AFS)."""
    target = Path(target_dir)
    v20 = Path(vmd_v20_dir)
    v19 = Path(vmd_v19_dir)
    v18 = Path(vmd_v18_dir)

    target.mkdir(parents=True, exist_ok=True)

    # 1. Podfolder 01_RST_v20_Gen12-14_CoreUltra (Meteor Lake DEV_7D0B, Raptor Lake DEV_A77F, Alder Lake DEV_467F)
    sub20 = target / "01_RST_v20_Gen12-14_CoreUltra"
    sub20.mkdir(parents=True, exist_ok=True)
    if v20.exists():
        for item in v20.iterdir():
            if item.is_file():
                shutil.copy2(item, sub20 / item.name)

    # 2. Podfolder 02_RST_v19_Gen11-13 (Tiger Lake 11th Gen DEV_9A0B)
    sub19 = target / "02_RST_v19_Gen11-13"
    sub19.mkdir(parents=True, exist_ok=True)
    if v19.exists():
        for item in v19.iterdir():
            if item.is_file():
                shutil.copy2(item, sub19 / item.name)

    # 3. Podfolder 03_RST_v18_Legacy_Optane (10th/11th Gen Optane / AFS)
    sub18 = target / "03_RST_v18_Legacy_Optane"
    sub18.mkdir(parents=True, exist_ok=True)
    if v18.exists():
        for item in v18.iterdir():
            if item.is_file():
                shutil.copy2(item, sub18 / item.name)

    log_success(f"VMD staging direktorijum pripremljen u: {target}")


def patch_single_index(boot_wim_path, image_index, vmd_staging_dir, net_dir,
                       autounattend_file, ventoy_file, startnet_file, load_vmd_file,
                       accelerate_pe_file, connect_nas_file=None, wim_tool="wimlib-imagex"):
    """Izvršava wimlib-imagex update nad jednim indeksom boot.wim slike."""
    boot_wim = Path(boot_wim_path).resolve()
    vmd_path = Path(vmd_staging_dir).resolve()
    net_path = Path(net_dir).resolve()
    autounattend_path = Path(autounattend_file).resolve()
    load_vmd_path = Path(load_vmd_file).resolve()
    accelerate_pe_path = Path(accelerate_pe_file).resolve()

    log_step(f"Ažuriranje boot.wim (Index {image_index})...")

    # Generisanje komandi za wimlib-imagex update
    update_commands = [
        "# WIS WinPE Pre-Boot & Drivers Update Script",
        "# 1. Ukloni postojece drajvere i skripte radi cistog azuriranja",
        "delete --force --recursive /Drivers/VMD",
        "delete --force --recursive /Drivers/Net",
        "delete --force --recursive /$WinPEDriver$",
        "delete --force /autounattend.xml",
        "delete --force /Windows/System32/winpeshl.ini",
        "delete --force /Windows/System32/wis_preboot.cmd",
        "delete --force /VentoyAutoRun.bat",
        "delete --force /Windows/System32/startnet.cmd",
        "delete --force /Windows/System32/load_vmd.cmd",
        "delete --force /load_vmd.cmd",
        "delete --force /Windows/System32/WIS-AcceleratePE.cmd",
        "delete --force /WIS-AcceleratePE.cmd",
        "delete --force /Windows/System32/WIS-ConnectNAS.cmd",
        "delete --force /WIS-ConnectNAS.cmd",
        "# 2. Ubaci drajvere",
        f'add "{vmd_path}" "/Drivers/VMD"',
        f'add "{net_path}" "/Drivers/Net"',
        f'add "{autounattend_path}" "/autounattend.xml"',
        "# 3. Ubaci pre-boot skripte (VentoyAutoRun.bat, startnet.cmd, load_vmd.cmd, WIS-AcceleratePE.cmd, WIS-ConnectNAS.cmd)",
        f'add "{ventoy_file}" "/VentoyAutoRun.bat"',
        f'add "{startnet_file}" "/Windows/System32/startnet.cmd"',
        f'add "{load_vmd_path}" "/Windows/System32/load_vmd.cmd"',
        f'add "{load_vmd_path}" "/load_vmd.cmd"',
        f'add "{accelerate_pe_path}" "/Windows/System32/WIS-AcceleratePE.cmd"',
        f'add "{accelerate_pe_path}" "/WIS-AcceleratePE.cmd"',
    ]
    if connect_nas_file and Path(connect_nas_file).exists():
        connect_nas_path = Path(connect_nas_file).resolve()
        update_commands.extend([
            f'add "{connect_nas_path}" "/Windows/System32/WIS-ConnectNAS.cmd"',
            f'add "{connect_nas_path}" "/WIS-ConnectNAS.cmd"',
        ])

    commands_text = "\n".join(update_commands) + "\n"

    cmd = [wim_tool, "update", str(boot_wim), str(image_index)]
    res = subprocess.run(cmd, input=commands_text, stdout=subprocess.PIPE,
                         stderr=subprocess.PIPE, text=True)

    if res.returncode != 0:
        log_error(f"wimlib-imagex update nije uspeo za Index {image_index} (kod: {res.returncode})!")
        log_error(f"Stderr: {res.stderr.strip()}")
        return False

    log_success(f"wimlib-imagex update uspešno završen za Index {image_index}.")
    if res.stdout:
        for line in res.stdout.strip().splitlines():
            if "done" in line or "scanned" in line or "Scanning" in line:
                log(line.strip(), tag="wimlib")

    return True


def patch_boot_wim(boot_wim_path, indices, vmd_v20_dir, vmd_v19_dir, vmd_v18_dir, net_dir,
                   autounattend_path, backup=False, wim_tool="wimlib-imagex"):
    """Glavna funkcija za ažuriranje svih specificiranih indeksa u boot.wim slici."""
    boot_wim = Path(boot_wim_path).resolve()
    vmd_v20 = Path(vmd_v20_dir).resolve()
    vmd_v19 = Path(vmd_v19_dir).resolve()
    vmd_v18 = Path(vmd_v18_dir).resolve()
    net_path = Path(net_dir).resolve()
    autounattend_file = Path(autounattend_path).resolve()

    log_step("Provera ulaznih putanja...")
    if not boot_wim.exists():
        log_error(f"boot.wim nije pronađen na: {boot_wim}")
        return False
    if not vmd_v20.exists() and not vmd_v19.exists():
        log_error("Nijedan VMD folder (v20 ili v19) nije pronađen!")
        return False
    if not net_path.exists():
        log_error(f"Net drajveri folder nije pronađen na: {net_path}")
        return False
    if not autounattend_file.exists():
        log_error(f"autounattend.xml nije pronađen na: {autounattend_file}")
        return False

    log(f"Ciljni WIM fajl: {boot_wim}")
    log(f"WIM Indeksi za obradu: {indices}")
    log(f"VMD RST v20 (Core Ultra / 14th Gen): {vmd_v20}")
    log(f"VMD RST v19 (11th Gen): {vmd_v19}")
    log(f"VMD RST v18 (Legacy / Optane): {vmd_v18}")
    log(f"Net drajveri: {net_path}")
    log(f"Autounattend fajl: {autounattend_file}")

    # Bekap ako je zatraženo
    if backup:
        bak_file = boot_wim.with_suffix(".wim.bak")
        if not bak_file.exists():
            log_step(f"Kreiranje rezervne kopije (backup): {bak_file}")
            shutil.copy2(boot_wim, bak_file)
            log_success("Rezervna kopija kreirana.")
        else:
            log(f"Rezervna kopija već postoji: {bak_file}")

    # Kreiranje privremenog staging direktorijuma
    with tempfile.TemporaryDirectory(prefix="wis_wim_staging_") as tmp_dir:
        tmp_path = Path(tmp_dir)

        # 1. Priprema VMD drajvera (RST 20.0 + RST 19.5 + Legacy 18.0)
        vmd_staging = tmp_path / "VMD"
        prepare_vmd_staging(vmd_v20, vmd_v19, vmd_v18, vmd_staging)

        # 2. Priprema VentoyAutoRun.bat (Windows CRLF format)
        ventoy_file = tmp_path / "VentoyAutoRun.bat"
        ventoy_file.write_bytes(VENTOY_AUTORUN_BAT_CONTENT.replace("\r\n", "\n").replace("\n", "\r\n").encode("utf-8"))

        # 3. Priprema startnet.cmd (Windows CRLF format)
        startnet_file = tmp_path / "startnet.cmd"
        startnet_file.write_bytes(STARTNET_CMD_CONTENT.replace("\r\n", "\n").replace("\n", "\r\n").encode("utf-8"))

        # 4. Priprema load_vmd.cmd (Windows CRLF format)
        load_vmd_src = autounattend_file.parent / "load_vmd.cmd"
        load_vmd_file = tmp_path / "load_vmd.cmd"
        if load_vmd_src.exists():
            load_vmd_content = load_vmd_src.read_text(encoding="utf-8")
        else:
            load_vmd_content = STARTNET_CMD_CONTENT
        load_vmd_file.write_bytes(load_vmd_content.replace("\r\n", "\n").replace("\n", "\r\n").encode("utf-8"))

        # 5. Priprema WIS-AcceleratePE.cmd i WIS-ConnectNAS.cmd (Windows CRLF format)
        wis_accel_src = autounattend_file.parent / "WIS-AcceleratePE.cmd"
        accelerate_pe_file = tmp_path / "WIS-AcceleratePE.cmd"
        if wis_accel_src.exists():
            accel_content = wis_accel_src.read_text(encoding="utf-8")
        else:
            accel_content = ACCELERATE_PE_CMD_CONTENT
        accelerate_pe_file.write_bytes(accel_content.replace("\r\n", "\n").replace("\n", "\r\n").encode("utf-8"))

        wis_connect_src = autounattend_file.parent / "WIS-ConnectNAS.cmd"
        connect_nas_file = tmp_path / "WIS-ConnectNAS.cmd"
        if wis_connect_src.exists():
            connect_nas_file.write_bytes(wis_connect_src.read_text(encoding="utf-8").replace("\r\n", "\n").replace("\n", "\r\n").encode("utf-8"))

        # Obrada svakog indeksa pojedinačno
        for idx in indices:
            ok = patch_single_index(
                boot_wim_path=boot_wim,
                image_index=idx,
                vmd_staging_dir=vmd_staging,
                net_dir=net_path,
                autounattend_file=autounattend_file,
                ventoy_file=ventoy_file,
                startnet_file=startnet_file,
                load_vmd_file=load_vmd_file,
                accelerate_pe_file=accelerate_pe_file,
                connect_nas_file=connect_nas_file if connect_nas_file.exists() else None,
                wim_tool=wim_tool
            )
            if not ok:
                log_error(f"Neuspešno krpljenje indeksa {idx}!")
                return False

    # Optimizacija boot.wim preko wimlib-imagex
    log_step("Optimizacija boot.wim slike preko wimlib-imagex...")
    try:
        opt_res = subprocess.run([wim_tool, "optimize", str(boot_wim), "--recompress", "--compress=maximum"], capture_output=True, text=True)
        if opt_res.returncode == 0:
            log_success("Optimizacija boot.wim uspešno završena.")
        else:
            log_warn(f"Optimizacija vratila kod {opt_res.returncode}: {opt_res.stderr.strip()}")
    except Exception as e:
        log_warn(f"Optimizacija nije uspela: {e}")

    # Verifikacija svih obrađenih indeksa
    all_verified = True
    for idx in indices:
        verified = verify_boot_wim_contents(boot_wim, idx, wim_tool=wim_tool)
        if not verified:
            all_verified = False

    return all_verified


def main():
    parser = argparse.ArgumentParser(
        description="WIS Patch-BootWim: Univerzalni Dual-Index WinPE Pre-Boot Mehanizam (wis_windows_admin)"
    )
    project_root = find_project_root()
    default_boot_wim = project_root / "sources" / "boot.wim"
    default_vmd_v20 = project_root / "Za disk 10+ gen" / "RST-20.0.0.1038"
    default_vmd_v19 = project_root / "Za disk 10+ gen" / "RST-F6Floppy-19.5.1.1040"
    default_vmd_legacy = project_root / "Za disk 10+ gen" / "f6vmdflpy-x64-20220309T142714Z-001" / "f6vmdflpy-x64"
    default_net = project_root / "sources" / "$OEM$" / "$1" / "setup" / "Drivers" / "Net"
    default_autounattend = project_root / "autounattend.xml"

    parser.add_argument("--boot-wim", default=str(default_boot_wim),
                        help=f"Putanja do boot.wim fajla (podrazumevano: {default_boot_wim})")
    parser.add_argument("--index", type=int, default=None,
                        help="Specificirani WIM Image Index za modifikaciju (npr. 1 ili 2). Ako se ne navede, krpe se svi indeksi.")
    parser.add_argument("--all-indices", action="store_true", default=True,
                        help="Krpljenje oba indeksa (Index 1 i Index 2) podrazumevano.")
    parser.add_argument("--vmd-v20-dir", default=str(default_vmd_v20),
                        help=f"Putanja do Intel VMD RST v20 drajvera (podrazumevano: {default_vmd_v20})")
    parser.add_argument("--vmd-dir", default=str(default_vmd_v19),
                        help=f"Putanja do Intel VMD RST v19 drajvera (podrazumevano: {default_vmd_v19})")
    parser.add_argument("--legacy-vmd-dir", default=str(default_vmd_legacy),
                        help=f"Putanja do legacy Intel VMD RST v18 drajvera (podrazumevano: {default_vmd_legacy})")
    parser.add_argument("--net-dir", default=str(default_net),
                        help=f"Putanja do mrežnih drajvera (podrazumevano: {default_net})")
    parser.add_argument("--autounattend", default=str(default_autounattend),
                        help=f"Putanja do autounattend.xml (podrazumevano: {default_autounattend})")
    parser.add_argument("--backup", action="store_true", default=False,
                        help="Kreira .bak kopiju boot.wim pre prvog patchovanja")
    parser.add_argument("--verify-only", action="store_true", default=False,
                        help="Samo verifikuje postojeći sadržaj boot.wim bez modifikacija")

    args = parser.parse_args()

    print("=================================================================")
    print("   WIS Dual-Index WinPE Pre-Boot & Drivers Patcher (wis_windows_admin)")
    print("=================================================================")

    wim_tool = check_prerequisites()
    if not wim_tool:
        sys.exit(1)

    boot_wim_path = Path(args.boot_wim).resolve()
    available_indices = get_wim_image_indices(boot_wim_path, wim_tool=wim_tool)

    # Određivanje indeksa za obradu
    if args.index is not None:
        target_indices = [args.index]
    else:
        target_indices = available_indices

    log(f"Detektovani indeksi u {boot_wim_path.name}: {available_indices}")
    log(f"Ciljani indeksi za rad: {target_indices}")

    if args.verify_only:
        all_ok = True
        for idx in target_indices:
            ok = verify_boot_wim_contents(boot_wim_path, idx, wim_tool=wim_tool)
            if not ok:
                all_ok = False
        sys.exit(0 if all_ok else 1)

    ok = patch_boot_wim(
        boot_wim_path=boot_wim_path,
        indices=target_indices,
        vmd_v20_dir=args.vmd_v20_dir,
        vmd_v19_dir=args.vmd_dir,
        vmd_v18_dir=args.legacy_vmd_dir,
        net_dir=args.net_dir,
        autounattend_path=args.autounattend,
        backup=args.backup,
        wim_tool=wim_tool
    )

    if ok:
        print("\n=================================================================")
        print("   [+] boot.wim DUAL-INDEX JE USPEŠNO ZAKRPLJEN I SPREMAN!       ")
        print("   [+] Index 1 (WinPE) i Index 2 (Setup) imaju VMD v20/v19/v18 + Net + Autorun")
        print("   [+] $WinPEDriver$ trajno eliminisan (sprečena greška 0x80070103 0x40031)")
        print("   [+] autounattend.xml ugrađen u X:\\ - automatski setup spreman! ")
        print("   [+] winpeshl.ini uklonjen - iVentoy ISO mount odblokiran!      ")
        print("=================================================================")
        sys.exit(0)
    else:
        print("\n=================================================================")
        print("   [!] DOŠLO JE DO GREŠKE PRILIKOM KRPLLJENJA boot.wim!          ")
        print("=================================================================")
        sys.exit(1)


if __name__ == "__main__":
    main()

