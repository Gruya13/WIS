@echo off
title WIS Universal VMD Storage Driver Loader
set "WIS_LOG=X:\wis_preboot.log"
echo [%TIME%] === WIS load_vmd.cmd start === >> "%WIS_LOG%" 2>nul

:: Idempotentna zastita: ako je VMD vec obradjen u ovoj WinPE sesiji, odmah izadji
if exist "X:\WIS_VMD_Loaded.tag" goto :AlreadyLoaded

:: 0. Preskoci Intel VMD na AMD procesorima
reg.exe query "HKLM\HARDWARE\DESCRIPTION\System\CentralProcessor\0" /v VendorIdentifier 2>nul | find.exe /I "AuthenticAMD" >nul 2>&1
if not errorlevel 1 goto :SkipAmd

:: 1. Trenutno listanje prvog nivoa PCI uredjaja bez sporog rekurzivnog /s pretrazivanja
set "VMD_MODE="
set "PCI_DUMP=X:\wis_pci_enum.txt"
reg.exe query "HKLM\SYSTEM\CurrentControlSet\Enum\PCI" > "%PCI_DUMP%" 2>nul
reg.exe query "HKLM\SYSTEM\ControlSet001\Enum\PCI" >> "%PCI_DUMP%" 2>nul

:: 1a. Provera za Intel VMD Gen 11 [Tiger Lake DEV_9A0B -> RST v19.5]
find.exe /I "DEV_9A0B" "%PCI_DUMP%" >nul 2>&1
if not errorlevel 1 set "VMD_MODE=V19_GEN11"
if defined VMD_MODE goto :LoadSelectedVmd

:: 1b. Provera za Intel VMD Gen 12-14 / Core Ultra [DEV_7D0B, DEV_AD0B, DEV_B07F, DEV_A77F, DEV_467F -> RST v20.0]
for %%p in (DEV_7D0B DEV_AD0B DEV_B07F DEV_A77F DEV_467F) do find.exe /I "%%p" "%PCI_DUMP%" >nul 2>&1 && set "VMD_MODE=V20_MODERN"
if defined VMD_MODE goto :LoadSelectedVmd

:: 1c. Provera za Intel VMD Gen 10 / Legacy Optane [DEV_09AB -> RST v18.0]
find.exe /I "DEV_09AB" "%PCI_DUMP%" >nul 2>&1
if not errorlevel 1 set "VMD_MODE=V18_LEGACY"
if defined VMD_MODE goto :LoadSelectedVmd

:: Nativni NVMe/AHCI kontroler [VMD nije prisutan] - trenutni izlaz sa garantovanim ERRORLEVEL=0
echo [*] WIS: Nativni NVMe/AHCI kontroler detektovan [VMD nije aktivan] - preskacem iaStorVD.
echo [%TIME%] Nativni NVMe/AHCI [bez VMD PCI ID-a] - brz izlaz. >> "%WIS_LOG%" 2>nul
echo 1 > "X:\WIS_VMD_Loaded.tag"
ver >nul
exit /b 0

:AlreadyLoaded
echo [%TIME%] VMD je vec inicijalizovan u ovoj sesiji [WIS_VMD_Loaded.tag prisutan]. >> "%WIS_LOG%" 2>nul
ver >nul
exit /b 0

:SkipAmd
echo [*] WIS: Detektovan AMD procesor - preskacem Intel VMD drajvere.
echo [%TIME%] AMD procesor detektovan - VMD preskocen. >> "%WIS_LOG%" 2>nul
echo 1 > "X:\WIS_VMD_Loaded.tag"
ver >nul
exit /b 0

:LoadSelectedVmd
if /I "%VMD_MODE%"=="V19_GEN11" echo [+] WIS: Detektovan Intel VMD Gen 11 [Tiger Lake DEV_9A0B]. Ucitavam RST v19...
if /I "%VMD_MODE%"=="V19_GEN11" if exist "X:\Drivers\VMD\02_RST_v19_Gen11-13\iaStorVD.inf" drvload.exe "X:\Drivers\VMD\02_RST_v19_Gen11-13\iaStorVD.inf" >> "%WIS_LOG%" 2>&1

if /I "%VMD_MODE%"=="V20_MODERN" echo [+] WIS: Detektovan Intel VMD Gen 12-14 / Core Ultra. Ucitavam RST v20...
if /I "%VMD_MODE%"=="V20_MODERN" if exist "X:\Drivers\VMD\01_RST_v20_Gen12-14_CoreUltra\iaStorVD.inf" drvload.exe "X:\Drivers\VMD\01_RST_v20_Gen12-14_CoreUltra\iaStorVD.inf" >> "%WIS_LOG%" 2>&1

if /I "%VMD_MODE%"=="V18_LEGACY" echo [+] WIS: Detektovan Intel VMD Legacy/Optane [DEV_09AB]. Ucitavam RST v18...
if /I "%VMD_MODE%"=="V18_LEGACY" if exist "X:\Drivers\VMD\03_RST_v18_Legacy_Optane\iaStorVD.inf" drvload.exe "X:\Drivers\VMD\03_RST_v18_Legacy_Optane\iaStorVD.inf" >> "%WIS_LOG%" 2>&1

echo [*] WIS: Osvezavanje skladisnih uredjaja...
pnputil.exe /scan-devices >nul 2>&1
echo san policy=OnlineAll > "X:\wis_diskpart.txt"
echo rescan >> "X:\wis_diskpart.txt"
diskpart.exe /s "X:\wis_diskpart.txt" >nul 2>&1

echo 1 > "X:\WIS_VMD_Loaded.tag"
echo [%TIME%] === WIS load_vmd.cmd zavrsen [VMD_MODE=%VMD_MODE%] === >> "%WIS_LOG%" 2>nul
ver >nul
exit /b 0
