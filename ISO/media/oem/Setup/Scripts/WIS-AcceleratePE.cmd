@echo off
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
