@echo off
title WIS Universal VMD Storage Driver Loader
echo =======================================================
echo  WIS: Automatsko ucitavanje Intel VMD skladisnih drajvera
echo =======================================================

:: 1. Detekcija i ucitavanje odgovarajuceg Intel VMD drajvera sa X:\
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
    if exist "X:\Drivers\VMD\02_RST_v19_Gen11-13\iaStorVD.inf" (
        drvload.exe "X:\Drivers\VMD\02_RST_v19_Gen11-13\iaStorVD.inf"
        pnputil.exe /add-driver "X:\Drivers\VMD\02_RST_v19_Gen11-13\iaStorVD.inf" /install >nul 2>&1
    )
) else (
    echo [+] WIS: Intel VMD Gen 12-14 / Core 5 120U / Core Ultra. Ucitavam RST v20...
    if exist "X:\Drivers\VMD\01_RST_v20_Gen12-14_CoreUltra\iaStorVD.inf" (
        drvload.exe "X:\Drivers\VMD\01_RST_v20_Gen12-14_CoreUltra\iaStorVD.inf"
        pnputil.exe /add-driver "X:\Drivers\VMD\01_RST_v20_Gen12-14_CoreUltra\iaStorVD.inf" /install >nul 2>&1
    )
)

:: 2. Osvezavanje i online aktivacija svih skladisnih diskova
echo [*] WIS: Osvezavanje skladisnih uredjaja...
pnputil.exe /scan-devices >nul 2>&1
(
echo san policy=OnlineAll
echo rescan
) | diskpart >nul 2>&1

echo =======================================================
echo [+] WIS VMD drajveri uspesno procesirani.
echo =======================================================
exit /b 0
