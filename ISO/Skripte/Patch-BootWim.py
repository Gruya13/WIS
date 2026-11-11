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
import zipfile
from pathlib import Path

# Automatska podrška za wimlib-imagex u .workspace/artifacts/tools/wimlib i /mnt/nvme
_SCRIPT_DIR = Path(__file__).resolve().parent
_REPO_ROOT = _SCRIPT_DIR.parent.parent
_EXTRA_BINS = [
    _REPO_ROOT / ".workspace/artifacts/tools/wimlib",
    Path("/mnt/nvme/usr/bin"),
]
_VALID_BINS = [str(p) for p in _EXTRA_BINS if p.exists()]
if _VALID_BINS:
    os.environ["PATH"] = ":".join(_VALID_BINS) + ":" + os.environ.get("PATH", "")
_EXTRA_LIBS = [
    str(_REPO_ROOT / ".workspace/artifacts/tools/wimlib"),
    "/mnt/nvme/usr/lib/x86_64-linux-gnu",
    "/mnt/nvme/lib/x86_64-linux-gnu",
]
_VALID_LIBS = [p for p in _EXTRA_LIBS if Path(p).exists()]
if _VALID_LIBS:
    _CUR_LD = os.environ.get("LD_LIBRARY_PATH", "")
    os.environ["LD_LIBRARY_PATH"] = ":".join(_VALID_LIBS) + (f":{_CUR_LD}" if _CUR_LD else "")


VENTOY_AUTORUN_BAT_CONTENT = r"""@echo off
title WIS VentoyAutoRun Pre-Boot (Zero-Touch PXE & USB)
set "WIS_LOG=X:\wis_preboot.log"
echo [%TIME%] === WIS VentoyAutoRun.bat start === >> "%WIS_LOG%" 2>nul
powercfg.exe /s 8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c >nul 2>&1

:: 0. Brza grana za iVentoy PXE mod (ProcessInjection faza pre VTOYPE.BAT)
if exist "X:\Windows\System32\ventoy\iventoy_param" (
    echo [%TIME%] Detektovan iVentoy PXE mod [iventoy_param prisutan]. >> "%WIS_LOG%" 2>nul
    
    :: Ucitaj mrezne drajvere AKO adapter nije aktivan
    set "IV_NET="
    ipconfig.exe 2>nul | find.exe /I "IPv4" >nul 2>&1
    if not errorlevel 1 set "IV_NET=1"
    if not defined IV_NET (
        echo [*] WIS: Inicijalizacija LAN drajvera za PXE...
        echo [%TIME%] Ucitavam LAN drajvere iz X:\Drivers\Net za PXE mod... >> "%WIS_LOG%" 2>nul
        for %%f in (
            "X:\Drivers\Net\Intel_LAN\e1d.inf"
            "X:\Drivers\Net\Intel_LAN\e2fn.inf"
            "X:\Drivers\Net\Realtek_LAN\rt68cx21x64.inf"
            "X:\Drivers\Net\Realtek_USB_LAN\rtu53cx22x64.inf"
        ) do (
            if exist "%%~f" (
                drvload.exe "%%~f" >> "%WIS_LOG%" 2>&1
                pnputil.exe /add-driver "%%~f" /install >> "%WIS_LOG%" 2>&1
            )
        )
        pnputil.exe /scan-devices >nul 2>&1
        wpeutil InitializeNetwork >> "%WIS_LOG%" 2>&1

        :: Cekanje na IPv4 adresu (do 10 sekundi uz pravu 1s pauzu)
        set /a IV_DHCP_WAIT=0
        :IvDhcpLoop
        ipconfig.exe 2>nul | find.exe /I "IPv4" >nul 2>&1
        if not errorlevel 1 goto :IvDhcpReady
        set /a IV_DHCP_WAIT+=1
        if %IV_DHCP_WAIT% leq 10 (
            ping.exe -n 2 127.0.0.1 >nul 2>&1
            goto :IvDhcpLoop
        )
        :IvDhcpReady
        echo [%TIME%] iVentoy LAN DHCP zavrsen [IV_DHCP_WAIT=%IV_DHCP_WAIT%]. >> "%WIS_LOG%" 2>nul
    )

    if exist "X:\Windows\System32\load_vmd.cmd" (
        call "X:\Windows\System32\load_vmd.cmd"
    )
    if exist "X:\Windows\System32\WIS-AcceleratePE.cmd" (
        call "X:\Windows\System32\WIS-AcceleratePE.cmd"
    )
    if exist "X:\wis_pci_enum.txt" (
        copy /y "X:\wis_pci_enum.txt" "X:\Windows\System32\ventoy\wis_pci_enum.txt" >nul 2>&1
    )
    echo 1 > "X:\WIS_PreBoot_Done.tag"
    echo 1 > "X:\GWADT_PreBoot_Done.tag"
    echo [%TIME%] === WIS VentoyAutoRun.bat [iVentoy PXE] uspesno zavrsen === >> "%WIS_LOG%" 2>nul
    exit /b 0
)

wpeinit

:: 1. Brza provera mreze (0.1s): NE resetuj aktivnu mreznu karticu ako vec ima IPv4 adresu!
set "NET_ACTIVE="
ipconfig.exe 2>nul | find.exe /I "IPv4" >nul 2>&1
if not errorlevel 1 set "NET_ACTIVE=1"

if defined NET_ACTIVE (
    echo [+] WIS: Mrezni adapter je vec aktivan [IPv4 prisutan] - cuvam postojecu PXE konekciju.
    echo [%TIME%] Mreza je vec aktivna [IPv4 prisutan] - preskacem drvload Net. >> "%WIS_LOG%" 2>nul
) else (
    echo [*] WIS: Mrezni adapter nema inbox drajver - ucitavam LAN drajvere...
    echo [%TIME%] Ucitavam LAN drajvere iz X:\Drivers\Net... >> "%WIS_LOG%" 2>nul
    for %%f in (
        "X:\Drivers\Net\Intel_LAN\e1d.inf"
        "X:\Drivers\Net\Intel_LAN\e2fn.inf"
        "X:\Drivers\Net\Realtek_LAN\rt68cx21x64.inf"
        "X:\Drivers\Net\Realtek_USB_LAN\rtu53cx22x64.inf"
    ) do (
        if exist "%%~f" (
            drvload.exe "%%~f" >> "%WIS_LOG%" 2>&1
            pnputil.exe /add-driver "%%~f" /install >> "%WIS_LOG%" 2>&1
        )
    )
    pnputil.exe /scan-devices >nul 2>&1
    wpeutil InitializeNetwork >> "%WIS_LOG%" 2>&1
    set /a NET_WAIT=0
    :AutorunPingLoop
    ipconfig.exe 2>nul | find.exe /I "IPv4" >nul 2>&1
    if not errorlevel 1 goto :AutorunNetReady
    set /a NET_WAIT+=1
    if %NET_WAIT% leq 20 (
        ping.exe -n 2 127.0.0.1 >nul 2>&1
        goto :AutorunPingLoop
    )
    :AutorunNetReady
    echo [%TIME%] Autorun LAN inicijalizacija zavrsena [NET_WAIT=%NET_WAIT%]. >> "%WIS_LOG%" 2>nul
)

:: 2. Brzo i deterministicko ucitavanje odgovarajuceg Intel VMD skladisnog drajvera (0.2s)
if exist "X:\Windows\System32\load_vmd.cmd" (
    call "X:\Windows\System32\load_vmd.cmd"
) else (
    set "PCI_DUMP=X:\wis_pci_enum.txt"
    reg.exe query "HKLM\SYSTEM\CurrentControlSet\Enum\PCI" > "%PCI_DUMP%" 2>nul
    reg.exe query "HKLM\SYSTEM\ControlSet001\Enum\PCI" >> "%PCI_DUMP%" 2>nul
    find.exe /I "DEV_9A0B" "%PCI_DUMP%" >nul 2>&1
    if not errorlevel 1 (
        drvload.exe "X:\Drivers\VMD\02_RST_v19_Gen11-13\iaStorVD.inf" >nul 2>&1
        pnputil.exe /scan-devices >nul 2>&1
        (echo san policy=OnlineAll & echo rescan) | diskpart >nul 2>&1
    ) else (
        set "HAS_VMD_V20="
        for %%p in (DEV_7D0B DEV_AD0B DEV_B07F DEV_A77F DEV_467F) do (
            find.exe /I "%%p" "%PCI_DUMP%" >nul 2>&1
            if not errorlevel 1 set "HAS_VMD_V20=1"
        )
        if defined HAS_VMD_V20 (
            drvload.exe "X:\Drivers\VMD\01_RST_v20_Gen12-14_CoreUltra\iaStorVD.inf" >nul 2>&1
            pnputil.exe /scan-devices >nul 2>&1
            (echo san policy=OnlineAll & echo rescan) | diskpart >nul 2>&1
        )
    )
    echo 1 > "X:\WIS_VMD_Loaded.tag"
)

echo 1 > "X:\WIS_PreBoot_Done.tag"
echo 1 > "X:\GWADT_PreBoot_Done.tag"
echo [%TIME%] === WIS VentoyAutoRun.bat uspesno zavrsen === >> "%WIS_LOG%" 2>nul
ver >nul
exit /b 0
"""

STARTNET_CMD_CONTENT = r"""@echo off
title WIS Universal WinPE Boot & Auto-Deploy
set "WIS_LOG=X:\wis_preboot.log"
echo [%TIME%] === WIS startnet.cmd start === >> "%WIS_LOG%" 2>nul
wpeinit

:: Provera da li je VentoyAutoRun vec obavio pre-boot ucitavanje
if exist "X:\WIS_PreBoot_Done.tag" goto :StartnetPreBootDone

powercfg.exe /s 8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c >nul 2>&1

:: 1. Pametna provera i ucitavanje LAN drajvera bez obaranja aktivne PXE veze
set "NET_ACTIVE="
ipconfig.exe 2>nul | find.exe /I "IPv4" >nul 2>&1
if not errorlevel 1 set "NET_ACTIVE=1"
if not defined NET_ACTIVE (
    echo [*] WIS: Ucitavam LAN drajvere...
    for %%f in (
        "X:\Drivers\Net\Intel_LAN\e1d.inf"
        "X:\Drivers\Net\Intel_LAN\e2fn.inf"
        "X:\Drivers\Net\Realtek_LAN\rt68cx21x64.inf"
        "X:\Drivers\Net\Realtek_USB_LAN\rtu53cx22x64.inf"
    ) do (
        if exist "%%~f" (
            drvload.exe "%%~f" >nul 2>&1
            pnputil.exe /add-driver "%%~f" /install >nul 2>&1
        )
    )
    pnputil.exe /scan-devices >nul 2>&1
    echo [*] WIS: Inicijalizujem mrezni podsistem...
    wpeutil InitializeNetwork >nul 2>&1
    
    set /a STARTNET_DHCP_WAIT=0
    :StartnetDhcpLoop
    ipconfig.exe 2>nul | find.exe /I "IPv4" >nul 2>&1
    if not errorlevel 1 goto :StartnetDhcpReady
    set /a STARTNET_DHCP_WAIT+=1
    if %STARTNET_DHCP_WAIT% leq 10 (
        ping.exe -n 2 127.0.0.1 >nul 2>&1
        goto :StartnetDhcpLoop
    )
    :StartnetDhcpReady
)

:: 2. Ucitavanje Intel VMD skladisnog drajvera (preko load_vmd.cmd sa deterministickim PCI izborom)
if exist "X:\Windows\System32\load_vmd.cmd" (
    call "X:\Windows\System32\load_vmd.cmd"
) else (
    echo [*] WIS: Detekcija Intel VMD skladisnog kontrolera...
    set "IS_GEN11_TIGERLAKE="
    reg.exe query "HKLM\SYSTEM\CurrentControlSet\Enum\PCI" 2>nul | find.exe /I "DEV_9A0B" >nul 2>&1
    if not errorlevel 1 set "IS_GEN11_TIGERLAKE=1"
    if not defined IS_GEN11_TIGERLAKE (
        reg.exe query "HKLM\SYSTEM\ControlSet001\Enum\PCI" 2>nul | find.exe /I "DEV_9A0B" >nul 2>&1
        if not errorlevel 1 set "IS_GEN11_TIGERLAKE=1"
    )
    if defined IS_GEN11_TIGERLAKE (
        echo [+] WIS: Detektovan Intel VMD Gen 11 [Tiger Lake DEV_9A0B]. Ucitavam RST v19...
        drvload.exe "X:\Drivers\VMD\02_RST_v19_Gen11-13\iaStorVD.inf"
    ) else (
        echo [+] WIS: Intel VMD Gen 12-14 / Core 5 120U / Core Ultra. Ucitavam RST v20...
        drvload.exe "X:\Drivers\VMD\01_RST_v20_Gen12-14_CoreUltra\iaStorVD.inf"
    )
    echo [*] WIS: Osvezavanje skladisnih uredjaja...
    pnputil.exe /scan-devices >nul 2>&1
    (
    echo san policy=OnlineAll
    echo rescan
    ) | diskpart >nul 2>&1
)

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

:: Cekanje na DHCP IP adresu i mrezni link ka 192.168.170.254 (do 5 sekundi ako ima IPv4)
set "SN_IPV4="
ipconfig.exe 2>nul | find.exe /I "IPv4" >nul 2>&1
if not errorlevel 1 set "SN_IPV4=1"
if not defined SN_IPV4 goto :StartnetMediaSearch

set /a NET_WAIT=0
:PingLoop
ping.exe -n 1 -w 800 192.168.170.254 >nul 2>&1
if %errorlevel% equ 0 goto :NetworkReady
set /a NET_WAIT+=1
if %NET_WAIT% leq 5 (
    echo [*] Cekam DHCP mrezni link ka 192.168.170.254 [pokusaj %NET_WAIT%/5]...
    ping.exe -n 2 127.0.0.1 >nul 2>&1
    goto :PingLoop
)
:NetworkReady

:: Dodavanje hostname aliasa u HOSTS fajl radi sprecavanja kolizije sa iVentoy portom 10445
set "HOSTS_FILE=%SystemRoot%\System32\drivers\etc\hosts"
find.exe /I "truenas.servis" "%HOSTS_FILE%" >nul 2>&1
if errorlevel 1 (
    echo. >> "%HOSTS_FILE%" 2>nul
    echo 192.168.170.254 truenas.servis >> "%HOSTS_FILE%" 2>nul
    echo 192.168.170.254 TRUENAS >> "%HOSTS_FILE%" 2>nul
)

:: Bezuslovno mapiranje TrueNAS WIS deljenog resursa na disk Z:
if not exist "Z:\win11\sources\install.esd" if not exist "Z:\win11\sources\install.wim" (
    echo [*] Mapiram TrueNAS SMB 3.1.1 [\\truenas.servis\WIS] na disk Z:...
    if exist "X:\Windows\System32\WIS-ConnectNAS.cmd" (
        call "X:\Windows\System32\WIS-ConnectNAS.cmd" Z: >nul 2>&1
    )
)

if exist "Z:\win11\sources\install.esd" (
    echo [+] TrueNAS deljenje montirano na Z:\ [Z:\win11\sources\install.esd aktivan]
) else if exist "Z:\win11\sources\install.wim" (
    echo [+] TrueNAS deljenje montirano na Z:\ [Z:\win11\sources\install.wim aktivan]
) else (
    echo [-] NAS server nije dostupan na lokalnoj mrezi - rad se nastavlja sa lokalnog/iVentoy medijuma
)

:: Detekcija iVentoy / virtuelnog CD-ROM ISO drajva (za cuvanje $OEM$ i autounattend.xml sa butovanog ISO-a)
:StartnetMediaSearch
set "WIS_BOOT_OEM="
set "WIS_BOOT_UNATTEND="
for %%d in (Y C D E F G H I J K L M N O P Q R S T U V W) do (
    if exist "%%d:\sources\$OEM$\$1\setup\WIS.Launcher.exe" (
        if not defined WIS_BOOT_OEM set "WIS_BOOT_OEM=%%d:\sources\$OEM$"
        if exist "%%d:\autounattend.xml" if not defined WIS_BOOT_UNATTEND set "WIS_BOOT_UNATTEND=%%d:\autounattend.xml"
    )
)

:: Glavna petlja pronalazenja instalacionog medijuma sa automatskim oporavkom
set /a RETRY_COUNT=0
:SearchMediaLoop

:: 5. PRIORITET 1: Pretraga pravog fizickog USB medijuma (iskljucujuci CD-ROM / iVentoy virtualni drajv)
set "WIS_SOURCE="
echo [*] WIS: Provera prisustva fizickog USB instalacionog medijuma...
for %%d in (C D E F G H I J K L M N O P Q R S T U V W) do (
    if exist "%%d:\sources\install.wim" (
        fsutil fsinfo drivetype %%d: 2>nul | find.exe /I "CD-ROM" >nul 2>&1
        if errorlevel 1 (
            set "WIS_SOURCE=%%d:"
            echo [+] WIS: Pronadjen fizicki USB instalacioni medijum na %%d:!
            goto :LaunchSetup
        )
    )
    if exist "%%d:\sources\install.esd" (
        fsutil fsinfo drivetype %%d: 2>nul | find.exe /I "CD-ROM" >nul 2>&1
        if errorlevel 1 (
            set "WIS_SOURCE=%%d:"
            echo [+] WIS: Pronadjen fizicki USB instalacioni medijum na %%d:!
            goto :LaunchSetup
        )
    )
)

:: 6. PRIORITET 2: Ako fizicki USB nije pronadjen -> Koristi TrueNAS SMB 3.1.1 sa montiranog Z:\win11
if exist "Z:\win11\setup.exe" if exist "Z:\win11\sources\install.wim" (
    set "WIS_SOURCE=Z:\win11"
    echo [+] WIS: Aktiviran brzi TrueNAS SMB 3.1.1 prenos sa Z:\win11 [115 MB/s]!
    goto :LaunchSetup
)
if exist "Z:\win11\setup.exe" if exist "Z:\win11\sources\install.esd" (
    set "WIS_SOURCE=Z:\win11"
    echo [+] WIS: Aktiviran brzi TrueNAS SMB 3.1.1 prenos sa Z:\win11 [115 MB/s]!
    goto :LaunchSetup
)

:: 7. PRIORITET 3: Fallback na sve preostale drajvove (ukljucujuci iVentoy CD-ROM Y: / lokalni disk)
echo [*] WIS Fallback: Pretrazujem sve preostale drajvove [iVentoy CD-ROM Y: / lokalni disk]...
for %%d in (Y C D E F G H I J K L M N O P Q R S T U V W Z) do (
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

:: Ako nijedan medijum nije pronadjen, automatski oporavak u petlji umesto gasenja konzole!
set /a RETRY_COUNT+=1
echo =======================================================
echo [!] UPOZORENJE: Instalacioni medijum jos uvek nije dostupan [pokusaj %RETRY_COUNT%/30]!
echo [*] Proveravam mrezu i ponavljam povezivanje za 3 sekunde...
echo =======================================================
if exist "X:\Windows\System32\WIS-ConnectNAS.cmd" call "X:\Windows\System32\WIS-ConnectNAS.cmd" Z: >nul 2>&1
ping.exe -n 4 127.0.0.1 >nul 2>&1
if %RETRY_COUNT% leq 30 goto :SearchMediaLoop

echo =======================================================
echo [!] KRITICNA GRESKA: Isteklo vreme za pronalazenje instalacionog medijuma!
echo Otvaram dijagnosticku konzolu. Ukucajte 'ipconfig' ili 'net use' za proveru.
echo =======================================================
cmd.exe
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

:: Odredjivanje izvrsne putanje setup.exe (koren izvora ili sources\setup.exe)
set "WIS_SETUP_EXE=%WIS_SOURCE%\setup.exe"
if not exist "%WIS_SETUP_EXE%" if exist "%WIS_SOURCE%\sources\setup.exe" set "WIS_SETUP_EXE=%WIS_SOURCE%\sources\setup.exe"

:: Pokretanje Windows Setup-a sa autounattend.xml odgovorima i ocuvanjem $OEM$ sa butovanog ISO-a
if defined WIS_BOOT_OEM if exist "%WIS_BOOT_OEM%" (
    if defined WIS_BOOT_UNATTEND if exist "%WIS_BOOT_UNATTEND%" (
        "%WIS_SETUP_EXE%" /unattend:"%WIS_BOOT_UNATTEND%" /m:"%WIS_BOOT_OEM%"
        goto :eof
    )
)
if exist "%WIS_SOURCE%\autounattend.xml" (
    "%WIS_SETUP_EXE%" /unattend:"%WIS_SOURCE%\autounattend.xml"
) else if exist "X:\autounattend.xml" (
    "%WIS_SETUP_EXE%" /unattend:"X:\autounattend.xml"
) else (
    "%WIS_SETUP_EXE%" /unattend:"%WIS_SOURCE%\autounattend.xml"
)
"""

ACCELERATE_PE_CMD_CONTENT = r"""@echo off
setlocal EnableDelayedExpansion
title WIS WinPE TrueNAS SMB 3.1.1 Multi-Client Media Mount
set "WIS_LOG=X:\wis_preboot.log"
echo =======================================================
echo  [*] WIS WinPE: Inicijalizacija instalacionog medijuma
echo =======================================================
echo [%TIME%] === WIS WIS-AcceleratePE.cmd start === >> "%WIS_LOG%" 2>nul

:: 0. Idempotentnost: Ako su i Z:\win11 i Y:\ vec spremni za multi-client SMB rad
set "HAS_Z_MEDIA="
set "HAS_Y_MEDIA="
if exist "Z:\win11\sources\install.esd" set "HAS_Z_MEDIA=1"
if exist "Z:\win11\sources\install.wim" set "HAS_Z_MEDIA=1"
if exist "Y:\sources\install.esd" set "HAS_Y_MEDIA=1"
if exist "Y:\sources\install.wim" set "HAS_Y_MEDIA=1"
if defined HAS_Z_MEDIA if defined HAS_Y_MEDIA (
    echo [+] WIS: Z:\win11 i Y:\ su vec aktivni. >> "%WIS_LOG%" 2>nul
    ver >nul
    exit /b 0
)

:: 1. Registarske polise za SMB klijenta [Guest auth + potpisivanje]
reg.exe add "HKLM\SYSTEM\CurrentControlSet\Services\LanmanWorkstation\Parameters" /v "AllowInsecureGuestAuth" /t REG_DWORD /d 1 /f >nul 2>&1
reg.exe add "HKLM\SYSTEM\CurrentControlSet\Services\LanmanWorkstation\Parameters" /v "RequireSecuritySignature" /t REG_DWORD /d 0 /f >nul 2>&1

:: 2. Provera fizickog USB instalacionog medijuma [iskljucujuci CD-ROM, iVentoy T: i SMB]
for %%d in (C D E F G H I J K L M N O P Q R S U V W) do (
    if exist "%%d:\sources\install.esd" (
        fsutil.exe fsinfo drivetype %%d: 2>nul | find.exe /I "CD-ROM" >nul 2>&1
        if errorlevel 1 (
            fsutil.exe fsinfo drivetype %%d: 2>nul | find.exe /I "Remote" >nul 2>&1
            if errorlevel 1 (
                echo [+] WIS: Detektovan fizicki USB instalacioni medijum %%d:!
                echo [%TIME%] Detektovan fizicki USB %%d: >> "%WIS_LOG%" 2>nul
                ver >nul
                exit /b 0
            )
        )
    )
    if exist "%%d:\sources\install.wim" (
        fsutil.exe fsinfo drivetype %%d: 2>nul | find.exe /I "CD-ROM" >nul 2>&1
        if errorlevel 1 (
            fsutil.exe fsinfo drivetype %%d: 2>nul | find.exe /I "Remote" >nul 2>&1
            if errorlevel 1 (
                echo [+] WIS: Detektovan fizicki USB instalacioni medijum %%d:!
                echo [%TIME%] Detektovan fizicki USB %%d: >> "%WIS_LOG%" 2>nul
                ver >nul
                exit /b 0
            )
        )
    )
)

:: 3. Brza provera IPv4 adrese [ako vec radi iz VTOYPE.BAT, preskacemo ucitavanje i DHCP cekanje]
set "HAS_IPV4="
ipconfig.exe 2>nul | find.exe /I "IPv4" >nul 2>&1
if not errorlevel 1 set "HAS_IPV4=1"

if not defined HAS_IPV4 (
    echo [*] WIS: Adapter nema IPv4 adresu - ucitavam LAN drajvere iz X:\Drivers\Net...
    echo [%TIME%] Ucitavam LAN drajvere iz X:\Drivers\Net... >> "%WIS_LOG%" 2>nul
    for %%f in (
        "X:\Drivers\Net\Intel_LAN\e1d.inf"
        "X:\Drivers\Net\Intel_LAN\e2fn.inf"
        "X:\Drivers\Net\Realtek_LAN\rt68cx21x64.inf"
        "X:\Drivers\Net\Realtek_USB_LAN\rtu53cx22x64.inf"
    ) do (
        if exist "%%~f" (
            drvload.exe "%%~f" >> "%WIS_LOG%" 2>&1
            pnputil.exe /add-driver "%%~f" /install >> "%WIS_LOG%" 2>&1
        )
    )
    pnputil.exe /scan-devices >nul 2>&1
    wpeutil.exe InitializeNetwork >> "%WIS_LOG%" 2>&1

    :: Cekanje na DHCP IPv4 adresu do 10s (bez viseminutnog zastoja)
    set /a DHCP_WAIT=0
    :WaitForDhcp
    ipconfig.exe 2>nul | find.exe /I "IPv4" >nul 2>&1
    if not errorlevel 1 (
        set "HAS_IPV4=1"
        goto :DhcpReady
    )
    set /a DHCP_WAIT+=1
    if !DHCP_WAIT! leq 10 (
        echo [*] Cekam DHCP IPv4 adresu [pokusaj !DHCP_WAIT!/10]...
        ping.exe -n 2 127.0.0.1 >nul 2>&1
        goto :WaitForDhcp
    )
    :DhcpReady
    echo [%TIME%] DHCP provera zavrsena [DHCP_WAIT=!DHCP_WAIT!, HAS_IPV4=!HAS_IPV4!]. >> "%WIS_LOG%" 2>nul
)

:: Ako i dalje nema IPv4, ne gubimo vreme na ping/SMB timeout vec ostavljamo postojeci fallback medijum
if not defined HAS_IPV4 (
    echo [-] WIS: Nema aktivne IPv4 mreze - preskacem SMB povezivanje.
    echo [%TIME%] Nema IPv4 - brz izlaz bez SMB cekanja. >> "%WIS_LOG%" 2>nul
    goto :FinishAccelerate
)

:: 3b. Brza provera dostupnosti TrueNAS servera [192.168.170.254] (max 3 pokusaja / ~3s)
set "NAS_REACHABLE="
set /a NET_WAIT=0
:WaitForNetwork
ping.exe -n 1 -w 800 192.168.170.254 >nul 2>&1
if %errorlevel% equ 0 (
    set "NAS_REACHABLE=1"
    goto :NetworkReady
)
set /a NET_WAIT+=1
if %NET_WAIT% leq 3 (
    echo [*] Cekam TrueNAS 192.168.170.254 [pokusaj %NET_WAIT%/3]...
    ping.exe -n 2 127.0.0.1 >nul 2>&1
    goto :WaitForNetwork
)
:NetworkReady
echo [%TIME%] TrueNAS ping zavrsen [NAS_REACHABLE=%NAS_REACHABLE%, pokusaji=%NET_WAIT%]. >> "%WIS_LOG%" 2>nul

if not defined NAS_REACHABLE (
    echo [-] WIS: TrueNAS 192.168.170.254 nije dostupan - ostavljam postojeci lokalni/iVentoy medijum.
    echo [%TIME%] TrueNAS nedostupan - ostavljen postojeci medijum. >> "%WIS_LOG%" 2>nul
    goto :FinishAccelerate
)

:: Dodavanje HOSTS unosa radi sprecavanja NetBIOS/iVentoy kolizije
set "HOSTS_FILE=%SystemRoot%\System32\drivers\etc\hosts"
find.exe /I "truenas.servis" "%HOSTS_FILE%" >nul 2>&1
if errorlevel 1 (
    echo. >> "%HOSTS_FILE%" 2>nul
    echo 192.168.170.254 truenas.servis >> "%HOSTS_FILE%" 2>nul
    echo 192.168.170.254 TRUENAS >> "%HOSTS_FILE%" 2>nul
)

:: 4. Primarno montiranje diska Z: za TrueNAS SMB 3.1.1 [\\truenas.servis\WIS]
if not defined HAS_Z_MEDIA (
    echo [*] Povezujem TrueNAS SMB 3.1.1 [\\truenas.servis\WIS] na disk Z:\...
    if exist "X:\Windows\System32\WIS-ConnectNAS.cmd" (
        call "X:\Windows\System32\WIS-ConnectNAS.cmd" Z:
    ) else if exist "%~dp0WIS-ConnectNAS.cmd" (
        call "%~dp0WIS-ConnectNAS.cmd" Z:
    )
)

:: 5. Ako je Z:\ uspesno montiran i sadrzi win11, oslobadjamo iVentoy port 10445 i prebacujemo Y:\ na TrueNAS SMB 3.1.1
set "Z_READY="
if exist "Z:\win11\sources\install.esd" set "Z_READY=1"
if exist "Z:\win11\sources\install.wim" set "Z_READY=1"

if defined Z_READY (
    echo [+] TrueNAS SMB 3.1.1 montiran na Z:\ [Z:\win11\sources je spreman]!
    echo [%TIME%] TrueNAS uspesno montiran na Z:\. >> "%WIS_LOG%" 2>nul

    :: Ako postoji iVentoy T:\iventoy.iso virtualni disk, sada ga bezbedno demontiramo
    if exist "T:\iventoy.iso" (
        echo select vdisk file="T:\iventoy.iso" > "X:\wis_detach.txt" 2>nul
        echo detach vdisk >> "X:\wis_detach.txt" 2>nul
        diskpart.exe /s "X:\wis_detach.txt" >nul 2>&1
        del /f /q "X:\wis_detach.txt" >nul 2>&1
        net.exe use T: /delete /y >nul 2>&1
    )

    :: Mapiraj Y: ka \\truenas.servis\WIS\win11 deljenju ako vec nije mapiran na taj SMB share
    set "Y_IS_NAS="
    net.exe use Y: 2>nul | find.exe /I "win11" >nul 2>&1 && set "Y_IS_NAS=1"
    if not defined Y_IS_NAS (
        echo [*] Povezujem TrueNAS SMB 3.1.1 [\\truenas.servis\WIS\win11] na disk Y:\...
        if exist "X:\Windows\System32\WIS-ConnectNAS.cmd" (
            call "X:\Windows\System32\WIS-ConnectNAS.cmd" Y:
        ) else if exist "%~dp0WIS-ConnectNAS.cmd" (
            call "%~dp0WIS-ConnectNAS.cmd" Y:
        )
        if not exist "Y:\sources\install.esd" if not exist "Y:\sources\install.wim" (
            ping.exe -n 2 127.0.0.1 >nul 2>&1
            if exist "X:\Windows\System32\WIS-ConnectNAS.cmd" (
                call "X:\Windows\System32\WIS-ConnectNAS.cmd" Y:
            ) else if exist "%~dp0WIS-ConnectNAS.cmd" (
                call "%~dp0WIS-ConnectNAS.cmd" Y:
            )
        )
    )
) else (
    echo [-] UPOZORENJE: TrueNAS SMB 3.1.1 nije dostupan - cuvam iVentoy virtualni disk kao fallback!
    echo [%TIME%] TrueNAS nije dostupan - cuvam iVentoy disk na Y:. >> "%WIS_LOG%" 2>nul
)

:FinishAccelerate
:: 6. Zavrsna verifikacija instalacionog medijuma
if exist "Z:\win11\sources\install.esd" (
    echo =======================================================
    echo  [+] USPEH: TrueNAS SMB 3.1.1 [Z:\win11\sources\install.esd] aktivan!
    echo  [+] Podrzano do 20 istovremenih instalacija u paraleli!
    echo =======================================================
) else if exist "Y:\sources\install.esd" (
    echo =======================================================
    echo  [+] USPEH: Instalacioni medijum [Y:\sources\install.esd] aktivan!
    echo =======================================================
) else if exist "Y:\sources\install.wim" (
    echo =======================================================
    echo  [+] USPEH: Instalacioni medijum [Y:\sources\install.wim] aktivan!
    echo =======================================================
) else (
    echo [-] UPOZORENJE: Nijedan medijum nije potvrdjen u AcceleratePE fazi.
)

echo [%TIME%] === WIS WIS-AcceleratePE.cmd zavrsen === >> "%WIS_LOG%" 2>nul
ver >nul
exit /b 0
"""


def log(msg, tag="*"):
    print(f"[{tag}] {msg}")


def log_success(msg):
    print(f"[+] {msg}")


def log_warn(msg):
    print(f"[!] UPOZORENJE: {msg}")


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
        ("/Drivers/Net", "Direktorijum mrežnih drajvera (WinPE LAN-only)"),
        ("/Drivers/Net/Intel_LAN/e1d.inf", "Intel I217/I218/I219 LAN paket u WinPE /Drivers/Net"),
        ("/Drivers/Net/Intel_LAN/e2fn.inf", "Intel I225/I226 LAN paket u WinPE /Drivers/Net"),
        ("/Drivers/Net/Realtek_LAN/rt68cx21x64.inf", "Realtek PCIe LAN paket u WinPE /Drivers/Net"),
        ("/Drivers/Net/Realtek_USB_LAN/rtu53cx22x64.inf", "Realtek USB LAN paket u WinPE /Drivers/Net"),
        ("/Windows/System32/DriverStore/FileRepository/000_wis_net1ix64.inf_guard/000_wis_net1ix64.inf", "WIS PNPHELP Guard za inbox Intel I219-LM (sprecava koliziju e1d vs net1ix64 na DEV_15FB)"),
        ("/Windows/System32/DriverStore/FileRepository/e1d.inf_amd64_dded470da430edc1/e1d.inf", "Intel I217/I218/I219-V/LM DriverStore paket (e1d.inf)"),
        ("/Windows/System32/DriverStore/FileRepository/e2fn.inf_amd64_1cea8465b51a7c39/e2fn.inf", "Intel I225/I226 2.5GbE DriverStore paket (e2fn.inf)"),
        ("/Windows/System32/DriverStore/FileRepository/rt68cx21x64.inf_amd64_b21b7271421f87d2/rt68cx21x64.inf", "Realtek PCIe GbE/2.5GbE DriverStore paket (rt68cx21x64.inf)"),
        ("/Windows/System32/DriverStore/FileRepository/rtu53cx22x64.inf_amd64_fde72e070d2da406/rtu53cx22x64.inf", "Realtek USB GbE DriverStore paket (rtu53cx22x64.inf)"),
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

    # Provera da winpeshl.ini, ventoy, WiFi drajveri, GWADT ostaci i $WinPEDriver$ NE SMEJU postojati u WinPE boot.wim
    forbidden = [
        ("/Windows/System32/winpeshl.ini", "winpeshl.ini (ne sme postojati da ne blokira iVentoy)"),
        ("/Windows/System32/ventoy", "ventoy direktorijum (NE SME postojati u boot.wim jer vtoypxe64.exe proverava IsDirExist('ventoy') na 0x14000595c)"),
        ("/Windows/System32/wis_preboot.cmd", "wis_preboot.cmd (zamenjen sa VentoyAutoRun.bat i startnet.cmd)"),
        ("/$WinPEDriver$", "$WinPEDriver$ (ne sme postojati da Windows Setup ne bi pucao sa 0x80070103 0x40031)"),
        ("/Drivers/Net/Intel_WiFi", "Intel_WiFi u WinPE (112+ MiB neupotrebljivo u WinPE; ostaje samo u $OEM$ za instalirani Windows)"),
        ("/Drivers/Net/MediaTek_WiFi", "MediaTek_WiFi u WinPE (14+ MiB neupotrebljivo u WinPE; ostaje samo u $OEM$ za instalirani Windows)"),
        ("/Drivers/Net/Realtek_WiFi", "Realtek_WiFi u WinPE (10+ MiB neupotrebljivo u WinPE; ostaje samo u $OEM$ za instalirani Windows)"),
        ("/GWADT-AcceleratePE.cmd", "zastareli GWADT-AcceleratePE.cmd"),
        ("/Windows/System32/GWADT-AcceleratePE.cmd", "zastareli System32 GWADT-AcceleratePE.cmd"),
    ]

    for f_path, f_desc in forbidden:
        if any(f == f_path or f.startswith(f_path + "/") for f in wim_files):
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
        if ("iaStorVD.inf" in l_res.stdout and "pnputil.exe" in l_res.stdout
                and "OnlineAll" in l_res.stdout and "findstr" not in l_res.stdout.lower()):
            log_success(f"[Index {image_index}] Verifikovan tačan sadržaj /Windows/System32/load_vmd.cmd (find.exe bez findstr)")
        else:
            log_error(f"[Index {image_index}] Sadržaj /Windows/System32/load_vmd.cmd ne sadrži očekivane komande ili sadrži nepostojeći findstr!")
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
                and "NAS_REACHABLE" in a_res.stdout
                and ("WIS\\win11" in a_res.stdout or "Servis\\win11" in a_res.stdout)):
            log_success(f"[Index {image_index}] Verifikovan tačan sadržaj /Windows/System32/WIS-AcceleratePE.cmd (Brzi TrueNAS SMB 3.1.1 akcelerator)")
        else:
            log_error(f"[Index {image_index}] Sadržaj /Windows/System32/WIS-AcceleratePE.cmd ne sadrži očekivane komande!")
            all_passed = False
    # Detaljna provera sadržaja /autounattend.xml
    extract_unattend_cmd = [wim_tool, "extract", str(boot_wim_path), str(image_index),
                            "/autounattend.xml", "--to-stdout"]
    u_res = subprocess.run(extract_unattend_cmd, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
    if u_res.returncode == 0:
        unattend_text = u_res.stdout
        if ("load_vmd.cmd" in unattend_text
                and "BypassTPMCheck" in unattend_text
                and "BypassDiskCheck" in unattend_text and "<Order>1</Order>" in unattend_text
                and "<Order>6</Order>" in unattend_text and "<Order>7</Order>" in unattend_text
                and "<Order>8</Order>" in unattend_text and "WIS-AcceleratePE.cmd" in unattend_text
                and ("ver >nul" in unattend_text or "ver &gt;nul" in unattend_text)):
            log_success(f"[Index {image_index}] Verifikovan /autounattend.xml (LabConfig Order 1-6 + Order 7 load_vmd.cmd + Order 8 WIS-AcceleratePE.cmd sa ver >nul zastitom)")
        else:
            log_error(f"[Index {image_index}] /autounattend.xml nema očekivane LabConfig Order 1-6 ili Order 7 load_vmd.cmd sa ver >nul zaštitom!")
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


def prepare_net_winpe_staging(net_dir, target_dir):
    """Priprema WinPE staging za /Drivers/Net sa ISKLJUČIVO LAN drajverima (izostavlja ~137.5 MiB WiFi paketa)."""
    target = Path(target_dir)
    net_root = Path(net_dir)
    target.mkdir(parents=True, exist_ok=True)

    lan_subdirs = ["Intel_LAN", "Realtek_LAN", "Realtek_USB_LAN"]
    total_bytes = 0
    copied_files = 0
    for sub_name in lan_subdirs:
        src_sub = net_root / sub_name
        if src_sub.exists() and src_sub.is_dir():
            dst_sub = target / sub_name
            shutil.copytree(src_sub, dst_sub, dirs_exist_ok=True)
            for f in dst_sub.rglob("*"):
                if f.is_file():
                    total_bytes += f.stat().st_size
                    copied_files += 1
        else:
            log_warn(f"LAN podfolder nije pronađen u Net izvoru: {src_sub}")

    readme_src = net_root / "README.md"
    if readme_src.exists() and readme_src.is_file():
        shutil.copy2(readme_src, target / "README.md")
        total_bytes += (target / "README.md").stat().st_size
        copied_files += 1

    log_success(
        f"WinPE LAN-only /Drivers/Net staging pripremljen u {target} "
        f"({copied_files} fajlova, {total_bytes / (1024 * 1024):.2f} MiB; WiFi izostavljen radi bržeg PXE boota)"
    )
    return target


def prepare_driverstore_staging(net_dir, target_dir, boot_wim_path=None, ref_index=1, wim_tool="wimlib-imagex"):
    """Priprema FileRepository pakete za VTOYPE.EXE PNPHELP + 000_wis_net1ix64.inf_guard za očuvanje inbox DEV_15FB."""
    target = Path(target_dir)
    net_root = Path(net_dir)
    target.mkdir(parents=True, exist_ok=True)

    packages = [
        ("e1d.inf_amd64_dded470da430edc1", net_root / "Intel_LAN", ["e1d.inf", "e1d.sys", "e1d.cat", "e1dmsg.dll"]),
        ("e2fn.inf_amd64_1cea8465b51a7c39", net_root / "Intel_LAN", ["e2fn.inf", "e2fn.sys", "e2fn.cat", "e2fnmsg.dll"]),
        ("rt68cx21x64.inf_amd64_b21b7271421f87d2", net_root / "Realtek_LAN", ["rt68cx21x64.inf", "rt68cx21x64.sys", "rt68cx21x64.cat"]),
        ("rtu53cx22x64.inf_amd64_fde72e070d2da406", net_root / "Realtek_USB_LAN", ["rtu53cx22x64.inf", "rtu53cx22x64.sys", "rtu53cx22x64.cat"]),
    ]

    prepared = []

    # 0. Guard paket za inbox Microsoft Intel I219-LM (net1ix64.inf) abecedno PRE e1d.inf!
    # VTOYPE.EXE PNPHELP skenira FileRepository\* abecedno i za DEV_15FB mora prvo pogoditi inbox ID-eve
    # kako drvload ne bi nasilno pregazio već aktivan kernel servis e1i68x64 sa e1dexpress.
    if boot_wim_path is None:
        raise RuntimeError("boot_wim_path je obavezan za generisanje 000_wis_net1ix64.inf_guard paketa!")

    dir_res = subprocess.run(
        [wim_tool, "dir", str(boot_wim_path), str(ref_index)],
        stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True
    )
    if dir_res.returncode != 0:
        raise RuntimeError(f"Neuspešno čitanje FileRepository iz boot.wim: {dir_res.stderr.strip()}")

    net1ix_wim_path = None
    for line in dir_res.stdout.splitlines():
        norm = line.strip().replace("\\", "/")
        if norm.lower().startswith("/windows/system32/driverstore/filerepository/net1ix64.inf_") and norm.lower().endswith("/net1ix64.inf"):
            net1ix_wim_path = norm
            break

    if not net1ix_wim_path:
        raise RuntimeError("Nije pronađen inbox net1ix64.inf u boot.wim DriverStore/FileRepository!")

    ext_res = subprocess.run(
        [wim_tool, "extract", str(boot_wim_path), str(ref_index), net1ix_wim_path, "--to-stdout"],
        stdout=subprocess.PIPE, stderr=subprocess.PIPE
    )
    if ext_res.returncode != 0 or len(ext_res.stdout) < 50000:
        raise RuntimeError(f"Neuspešna ekstrakcija {net1ix_wim_path} iz boot.wim!")

    guard_txt = ext_res.stdout.decode("utf-16-le" if ext_res.stdout.startswith(b"\xff\xfe") else "utf-8", errors="ignore").upper()
    if "PCI\\VEN_8086&DEV_15FB" not in guard_txt:
        raise RuntimeError("Ekstraktovani net1ix64.inf ne sadrži PCI\\VEN_8086&DEV_15FB!")
    if "PCI\\VEN_8086&DEV_15FC" in guard_txt or "PCI\\VEN_8086&DEV_0D4F" in guard_txt:
        raise RuntimeError("Guard net1ix64.inf neočekivano sadrži DEV_15FC/DEV_0D4F i blokirao bi e1d.inf!")

    guard_pkg_name = "000_wis_net1ix64.inf_guard"
    guard_dir = target / guard_pkg_name
    guard_dir.mkdir(parents=True, exist_ok=True)
    (guard_dir / "000_wis_net1ix64.inf").write_bytes(ext_res.stdout)
    prepared.append((guard_pkg_name, guard_dir))
    log_success(f"DriverStore GUARD paket pripremljen iz {net1ix_wim_path}: {guard_pkg_name} ({len(ext_res.stdout)} B)")

    for pkg_name, src_folder, files in packages:
        pkg_dir = target / pkg_name
        pkg_dir.mkdir(parents=True, exist_ok=True)
        copied_count = 0
        for fname in files:
            src_file = src_folder / fname
            if src_file.exists():
                shutil.copy2(src_file, pkg_dir / fname)
                copied_count += 1
        if copied_count > 0:
            prepared.append((pkg_name, pkg_dir))
            log_success(f"DriverStore paket pripremljen: {pkg_name} ({copied_count} fajlova)")
        else:
            log_error(f"Nijedan fajl nije pronađen za DriverStore paket: {pkg_name} u {src_folder}")

    return prepared


def patch_single_index(boot_wim_path, image_index, vmd_staging_dir, net_dir,
                       driverstore_packages,
                       autounattend_file, ventoy_file, startnet_file, load_vmd_file,
                       accelerate_pe_file, connect_nas_file=None, wifi_ini_file=None,
                       wim_tool="wimlib-imagex"):
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
        "delete --force --recursive /Windows/System32/ventoy",
        "delete --force /autounattend.xml",
        "delete --force /Windows/System32/winpeshl.ini",
        "delete --force /Windows/System32/wis_preboot.cmd",
        "delete --force /VentoyAutoRun.bat",
        "delete --force /Windows/System32/startnet.cmd",
        "delete --force /Windows/System32/load_vmd.cmd",
        "delete --force /load_vmd.cmd",
        "delete --force /Windows/System32/WIS-AcceleratePE.cmd",
        "delete --force /WIS-AcceleratePE.cmd",
        "delete --force /Windows/System32/GWADT-AcceleratePE.cmd",
        "delete --force /GWADT-AcceleratePE.cmd",
        "delete --force /Windows/System32/WIS-ConnectNAS.cmd",
        "delete --force /WIS-ConnectNAS.cmd",
        "delete --force /Windows/System32/Wifi-Servis.ini",
        "delete --force /Wifi-Servis.ini",
        "# 2. Ubaci drajvere (VMD + WinPE LAN-only Net)",
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
    for pkg_name, pkg_dir in driverstore_packages:
        update_commands.extend([
            f'delete --force --recursive "/Windows/System32/DriverStore/FileRepository/{pkg_name}"',
            f'add "{pkg_dir.resolve()}" "/Windows/System32/DriverStore/FileRepository/{pkg_name}"',
        ])
    if connect_nas_file and Path(connect_nas_file).exists():
        connect_nas_path = Path(connect_nas_file).resolve()
        update_commands.extend([
            f'add "{connect_nas_path}" "/Windows/System32/WIS-ConnectNAS.cmd"',
            f'add "{connect_nas_path}" "/WIS-ConnectNAS.cmd"',
        ])
    if wifi_ini_file and Path(wifi_ini_file).exists():
        wifi_ini_path = Path(wifi_ini_file).resolve()
        update_commands.extend([
            f'add "{wifi_ini_path}" "/Windows/System32/Wifi-Servis.ini"',
            f'add "{wifi_ini_path}" "/Wifi-Servis.ini"',
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

        # 1b. Priprema WinPE LAN-only /Drivers/Net staging-a (bez 137.5 MiB WiFi balasta)
        net_winpe_staging = tmp_path / "NetWinPE"
        prepare_net_winpe_staging(net_path, net_winpe_staging)

        # 1c. Priprema DriverStore FileRepository LAN paketa + Guard paketa za VTOYPE.EXE PNPHELP
        driverstore_staging = tmp_path / "DriverStore"
        ref_idx = indices[0] if indices else 1
        driverstore_packages = prepare_driverstore_staging(
            net_path, driverstore_staging,
            boot_wim_path=boot_wim, ref_index=ref_idx, wim_tool=wim_tool
        )

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

        wifi_ini_src = autounattend_file.parent / "Wifi-Servis.ini"
        wifi_ini_file = tmp_path / "Wifi-Servis.ini"
        if wifi_ini_src.exists():
            wifi_ini_file.write_bytes(wifi_ini_src.read_text(encoding="utf-8").replace("\r\n", "\n").replace("\n", "\r\n").encode("utf-8"))

        # Obrada svakog indeksa pojedinačno
        for idx in indices:
            ok = patch_single_index(
                boot_wim_path=boot_wim,
                image_index=idx,
                vmd_staging_dir=vmd_staging,
                net_dir=net_winpe_staging,
                driverstore_packages=driverstore_packages,
                autounattend_file=autounattend_file,
                ventoy_file=ventoy_file,
                startnet_file=startnet_file,
                load_vmd_file=load_vmd_file,
                accelerate_pe_file=accelerate_pe_file,
                connect_nas_file=connect_nas_file if connect_nas_file.exists() else None,
                wifi_ini_file=wifi_ini_file if wifi_ini_file.exists() else None,
                wim_tool=wim_tool
            )
            if not ok:
                log_error(f"Neuspešno krpljenje indeksa {idx}!")
                return False

    # Optimizacija boot.wim preko wimlib-imagex
    log_step("Optimizacija boot.wim slike preko wimlib-imagex...")
    try:
        opt_res = subprocess.run([wim_tool, "optimize", str(boot_wim)], capture_output=True, text=True)
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

