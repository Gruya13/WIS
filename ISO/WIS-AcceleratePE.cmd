@echo off
setlocal EnableDelayedExpansion
title WIS WinPE TrueNAS SMB 3.1.1 Multi-Client Media Mount
set "WIS_LOG=X:\wis_preboot.log"
echo =======================================================
echo  [*] WIS WinPE: Inicijalizacija instalacionog medijuma
echo =======================================================
echo [%TIME%] === WIS WIS-AcceleratePE.cmd start === >> "%WIS_LOG%" 2>nul

:: 0. Idempotentnost: Ako su i Z:\win11 i Y:\ vec aktivni kao NAS i pre-format forenzika je uradjena
set "HAS_Z_MEDIA="
set "HAS_Y_MEDIA="
set "Y_ALREADY_NAS="
if exist "Z:\win11\sources\install.esd" set "HAS_Z_MEDIA=1"
if exist "Z:\win11\sources\install.wim" set "HAS_Z_MEDIA=1"
if exist "Y:\sources\install.esd" set "HAS_Y_MEDIA=1"
if exist "Y:\sources\install.wim" set "HAS_Y_MEDIA=1"
net.exe use Y: 2>nul | find.exe /I "win11" >nul 2>&1 && set "Y_ALREADY_NAS=1"
if defined HAS_Z_MEDIA if defined HAS_Y_MEDIA if defined Y_ALREADY_NAS (
    call :RunWinPeForensics
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
                call :RunWinPeForensics
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
                call :RunWinPeForensics
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

:: 3a. IMP-114 Sloj B: WinPE pre-format Autopilot forenzika (cim je mreza inicijalizovana, PRE formatiranja diska!)
call :RunWinPeForensics

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

:: 5. IMP-112 Provera podudarnosti WIS_IMAGE_PROFILE.tag pre prebacivanja sa Y:\ na Z:\win11
set "Z_READY="
if exist "Z:\win11\sources\install.esd" set "Z_READY=1"
if exist "Z:\win11\sources\install.wim" set "Z_READY=1"

if defined Z_READY (
    echo [+] TrueNAS SMB 3.1.1 montiran na Z:\ [Z:\win11\sources je spreman]!
    echo [%TIME%] TrueNAS uspesno montiran na Z:\. >> "%WIS_LOG%" 2>nul

    set "Y_IS_NAS="
    net.exe use Y: 2>nul | find.exe /I "win11" >nul 2>&1 && set "Y_IS_NAS=1"

    set "Y_HAS_SOURCE="
    if exist "Y:\sources\install.esd" set "Y_HAS_SOURCE=1"
    if exist "Y:\sources\install.wim" set "Y_HAS_SOURCE=1"

    set "ALLOW_Z_SWITCH=1"
    set "Y_IMAGE_TYPE="
    set "Y_WIM_VERSION="
    set "Z_IMAGE_TYPE="
    set "Z_WIM_VERSION="

    if not defined Y_IS_NAS (
        if exist "Y:\WIS_IMAGE_PROFILE.tag" (
            call :ReadProfileTag "Y:\WIS_IMAGE_PROFILE.tag" Y
            if exist "Z:\win11\WIS_IMAGE_PROFILE.tag" (
                call :ReadProfileTag "Z:\win11\WIS_IMAGE_PROFILE.tag" Z
                if "!Y_IMAGE_TYPE!"=="" (
                    set "ALLOW_Z_SWITCH=0"
                    echo [!] IMP-112: Nevalidan Y:\WIS_IMAGE_PROFILE.tag - zadrzavam izvorni medijum Y:\.
                    echo [%TIME%] IMP-112 BLOKADA: Y:\WIS_IMAGE_PROFILE.tag nema IMAGE_TYPE - zadrzan Y:\. >> "%WIS_LOG%" 2>nul
                ) else if "!Y_WIM_VERSION!"=="" (
                    set "ALLOW_Z_SWITCH=0"
                    echo [!] IMP-112: Y:\WIS_IMAGE_PROFILE.tag nema WIM_VERSION - zadrzavam izvorni medijum Y:\.
                    echo [%TIME%] IMP-112 BLOKADA: Y:\WIS_IMAGE_PROFILE.tag nema WIM_VERSION - zadrzan Y:\. >> "%WIS_LOG%" 2>nul
                ) else if /I not "!Y_IMAGE_TYPE!"=="!Z_IMAGE_TYPE!" (
                    set "ALLOW_Z_SWITCH=0"
                    echo [!] IMP-112 BLOKADA: Razlicit profil slike! Y:\=[!Y_IMAGE_TYPE!/!Y_WIM_VERSION!] vs Z:\win11=[!Z_IMAGE_TYPE!/!Z_WIM_VERSION!] - zadrzavam Y:\!
                    echo [%TIME%] IMP-112 BLOKADA: Neslaganje IMAGE_TYPE Y=[!Y_IMAGE_TYPE!] vs Z=[!Z_IMAGE_TYPE!] - zadrzan izvorni medijum Y:\. >> "%WIS_LOG%" 2>nul
                ) else if /I not "!Y_WIM_VERSION!"=="!Z_WIM_VERSION!" (
                    set "ALLOW_Z_SWITCH=0"
                    echo [!] IMP-112 BLOKADA: Razlicita verzija slike! Y:\=[!Y_IMAGE_TYPE!/!Y_WIM_VERSION!] vs Z:\win11=[!Z_IMAGE_TYPE!/!Z_WIM_VERSION!] - zadrzavam Y:\!
                    echo [%TIME%] IMP-112 BLOKADA: Neslaganje WIM_VERSION Y=[!Y_WIM_VERSION!] vs Z=[!Z_WIM_VERSION!] - zadrzan izvorni medijum Y:\. >> "%WIS_LOG%" 2>nul
                ) else (
                    echo [+] IMP-112 OK: Profil i verzija se poklapaju [!Y_IMAGE_TYPE! / !Y_WIM_VERSION!] - dozvoljeno prebacivanje na Z:\win11.
                    echo [%TIME%] IMP-112 OK: Poklapanje WIS_IMAGE_PROFILE.tag [!Y_IMAGE_TYPE! / !Y_WIM_VERSION!] - prebacujem na Z:\win11. >> "%WIS_LOG%" 2>nul
                )
            ) else (
                set "ALLOW_Z_SWITCH=0"
                echo [!] IMP-112 BLOKADA: Izvorni Y:\ ima WIS_IMAGE_PROFILE.tag [!Y_IMAGE_TYPE!/!Y_WIM_VERSION!], a Z:\win11 nema marker - zadrzavam Y:\!
                echo [%TIME%] IMP-112 BLOKADA: Z:\win11 nema WIS_IMAGE_PROFILE.tag (Y=[!Y_IMAGE_TYPE!/!Y_WIM_VERSION!]) - zadrzan izvorni medijum Y:\. >> "%WIS_LOG%" 2>nul
            )
        ) else if defined Y_HAS_SOURCE (
            if exist "Z:\win11\WIS_IMAGE_PROFILE.tag" (
                call :ReadProfileTag "Z:\win11\WIS_IMAGE_PROFILE.tag" Z
                if /I not "!Z_IMAGE_TYPE!"=="lean" (
                    set "ALLOW_Z_SWITCH=0"
                    echo [!] IMP-112 BLOKADA: Izvorni Y:\ nema marker, dok Z:\win11 ima profil [!Z_IMAGE_TYPE!/!Z_WIM_VERSION!] - zadrzavam Y:\!
                    echo [%TIME%] IMP-112 BLOKADA: Y:\ bez taga vs Z:\win11=[!Z_IMAGE_TYPE!/!Z_WIM_VERSION!] - zadrzan izvorni medijum Y:\. >> "%WIS_LOG%" 2>nul
                )
            )
        )
    )

    if "!ALLOW_Z_SWITCH!"=="0" (
        echo 1 > "X:\WIS_PROFILE_MISMATCH.tag"
        net.exe use Z: /delete /y >nul 2>&1
        echo [*] IMP-112: TrueNAS Z:\ je bezbedno otkacen radi zastite izvornog medijuma Y:\.
        echo [%TIME%] IMP-112: Z:\ otkacen zbog neslaganja profila; aktivan ostaje izvorni Y:\. >> "%WIS_LOG%" 2>nul
        goto :FinishAccelerate
    )

    :: Profil se poklapa: oslobadjamo iVentoy port 10445 i prebacujemo Y:\ na TrueNAS SMB 3.1.1
    if exist "T:\iventoy.iso" (
        echo select vdisk file="T:\iventoy.iso" > "X:\wis_detach.txt" 2>nul
        echo detach vdisk >> "X:\wis_detach.txt" 2>nul
        diskpart.exe /s "X:\wis_detach.txt" >nul 2>&1
        del /f /q "X:\wis_detach.txt" >nul 2>&1
    )
    net.exe use T: /delete /y >nul 2>&1

    if not defined Y_IS_NAS (
        mountvol.exe Y: /D >nul 2>&1
        mountvol.exe Y: /P >nul 2>&1
        net.exe use Y: /delete /y >nul 2>&1
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
) else if exist "Z:\win11\sources\install.wim" (
    echo =======================================================
    echo  [+] USPEH: TrueNAS SMB 3.1.1 [Z:\win11\sources\install.wim] aktivan!
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

:: ==============================================================================
:: POMOCNA RUTINA: Bezbedno citanje WIS_IMAGE_PROFILE.tag (IMP-112)
:: ==============================================================================
:ReadProfileTag
set "_TAG_FILE=%~1"
set "_TAG_PREFIX=%~2"
if not exist "!_TAG_FILE!" goto :eof
for /f "usebackq tokens=1,* delims==" %%k in ("!_TAG_FILE!") do (
    set "_RAW_K=%%k"
    set "_RAW_V=%%l"
    if defined _RAW_V (
        set "_RAW_V=!_RAW_V:"=!"
        for /f "tokens=* delims= " %%v in ("!_RAW_V!") do set "_CLEAN_V=%%v"
        if /I "!_RAW_K!"=="IMAGE_TYPE" set "!_TAG_PREFIX!_IMAGE_TYPE=!_CLEAN_V!"
        if /I "!_RAW_K!"=="WIM_VERSION" set "!_TAG_PREFIX!_WIM_VERSION=!_CLEAN_V!"
    )
)
goto :eof

:: ==============================================================================
:: POMOCNA RUTINA: WinPE Pre-Format Autopilot Forenzika (IMP-114 Sloj B)
:: ==============================================================================
:RunWinPeForensics
if exist "X:\WIS_WinPE_Forensics_Done.tag" goto :eof
echo [*] WIS WinPE: Pokrecem pre-format Autopilot forenziku (IMP-114 Sloj B)...
echo [%TIME%] Pokrecem WinPE pre-format Autopilot forenziku... >> "%WIS_LOG%" 2>nul

set "WIS_WINPE_PS1="
if exist "X:\Windows\System32\Invoke-WinPeAutopilotForensics.ps1" set "WIS_WINPE_PS1=X:\Windows\System32\Invoke-WinPeAutopilotForensics.ps1"
if not defined WIS_WINPE_PS1 if exist "X:\Invoke-WinPeAutopilotForensics.ps1" set "WIS_WINPE_PS1=X:\Invoke-WinPeAutopilotForensics.ps1"
if not defined WIS_WINPE_PS1 if exist "%~dp0Invoke-WinPeAutopilotForensics.ps1" set "WIS_WINPE_PS1=%~dp0Invoke-WinPeAutopilotForensics.ps1"
if not defined WIS_WINPE_PS1 if exist "Y:\Skripte\Invoke-WinPeAutopilotForensics.ps1" set "WIS_WINPE_PS1=Y:\Skripte\Invoke-WinPeAutopilotForensics.ps1"

if defined WIS_WINPE_PS1 if exist "%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" (
    "%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File "!WIS_WINPE_PS1!" >> "%WIS_LOG%" 2>&1
    echo 1 > "X:\WIS_WinPE_Forensics_Done.tag"
    goto :eof
)

:: Lagani nativni reg.exe fallback ako WinPE slika nema PowerShell komponentu (uz bezuslovni reg unload!)
for %%p in (C D E F G H I J K L M N O P Q R S U V W) do (
    if exist "%%p:\Windows\ServiceState\wmansvc\AutopilotDDSZTDFile.json" (
        echo [!] WinPE-Forensics: Pronadjen zateceni wmansvc\AutopilotDDSZTDFile.json na %%p:\ >> "%WIS_LOG%" 2>nul
        copy /y "%%p:\Windows\ServiceState\wmansvc\AutopilotDDSZTDFile.json" "X:\wis_winpe_ztd_backup.json" >nul 2>&1
    )
    if exist "%%p:\Windows\System32\config\SOFTWARE" (
        reg.exe load HKLM\WIS_OFFLINE_SW "%%p:\Windows\System32\config\SOFTWARE" >nul 2>&1
        if not errorlevel 1 (
            reg.exe query "HKLM\WIS_OFFLINE_SW\Microsoft\Provisioning\Diagnostics\AutoPilot" /s >> "X:\wis_winpe_reg_dump.txt" 2>nul
            reg.exe query "HKLM\WIS_OFFLINE_SW\Microsoft\Provisioning\AutopilotSettings" /s >> "X:\wis_winpe_reg_dump.txt" 2>nul
            reg.exe unload HKLM\WIS_OFFLINE_SW >nul 2>&1
            echo [%TIME%] WinPE-Forensics: Offline SOFTWARE hive sa %%p:\ proveren i bezbedno demontiran (reg unload). >> "%WIS_LOG%" 2>nul
        )
    )
)
echo 1 > "X:\WIS_WinPE_Forensics_Done.tag"
goto :eof
