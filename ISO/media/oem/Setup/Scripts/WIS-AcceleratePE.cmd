@echo off
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

:: 5. Bezuslovno mapiranje TrueNAS WIS deljenog resursa na disk Z:
set "MAPPED_NAS="
echo [*] Povezujem TrueNAS WIS SMB 3.1.1 na disk Z:...
if exist "%~dp0WIS-ConnectNAS.cmd" (
    call "%~dp0WIS-ConnectNAS.cmd" Z:
) else (
    if defined WIS_NAS_PASS (
        net use Z: \\truenas.servis\WIS "%WIS_NAS_PASS%" /user:admin /persistent:no >nul 2>&1
        if not exist "Z:\" net use Z: \\192.168.170.254\WIS "%WIS_NAS_PASS%" /user:admin /persistent:no >nul 2>&1
        if not exist "Z:\" net use Z: \\TRUENAS\WIS "%WIS_NAS_PASS%" /user:admin /persistent:no >nul 2>&1
    )
    if not exist "Z:\" net use Z: \\truenas.servis\WIS /user:guest "" >nul 2>&1
    if not exist "Z:\" net use Z: \\192.168.170.254\WIS /user:guest "" >nul 2>&1
    if not exist "Z:\" net use Z: \\192.168.170.254\Servis /user:guest "" >nul 2>&1
)

if exist "Z:\" (
    set "MAPPED_NAS=1"
    echo [+] TrueNAS WIS deljenje montirano na Z:\
) else (
    echo [-] NAS server nije dostupan na lokalnoj mrezi - rad se nastavlja sa lokalnog medijuma
)

:: 6. Provera da li instalacija koristi spori iVentoy virtuelni CD-ROM (Y:)
set "IS_IVENTOY="
set "WIS_SOURCE="
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

    :: Ako je startnet.cmd vec aktivirao Z:\win11 i postavio WIS_PreBoot_Done.tag,
    :: cuvaj Y: netaknutim kako bi setup.exe preneo svezi /m:"Y:\sources\$OEM$" sa butovanog ISO-a!
    if exist "X:\WIS_PreBoot_Done.tag" if exist "Z:\win11\sources\install.esd" (
        echo [*] WIS Accelerator: startnet.cmd je vec aktivirao Z:\win11 SMB 3.1.1 - cuvam Y: za $OEM$ paket.
        goto :SkipDetachY
    )
    if exist "X:\WIS_PreBoot_Done.tag" if exist "Z:\win11\sources\install.wim" (
        echo [*] WIS Accelerator: startnet.cmd je vec aktivirao Z:\win11 SMB 3.1.1 - cuvam Y: za $OEM$ paket.
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
    if exist "%~dp0WIS-ConnectNAS.cmd" (
        call "%~dp0WIS-ConnectNAS.cmd" Y:
    ) else (
        if defined WIS_NAS_PASS (
            net use Y: \\truenas.servis\WIS\win11 "%WIS_NAS_PASS%" /user:admin /persistent:no >nul 2>&1
            set "HAS_IMAGE=" & if exist "Y:\sources\install.esd" set "HAS_IMAGE=1" & if exist "Y:\sources\install.wim" set "HAS_IMAGE=1"
            if not defined HAS_IMAGE (
                net use Y: \\192.168.170.254\WIS\win11 "%WIS_NAS_PASS%" /user:admin /persistent:no >nul 2>&1
                set "HAS_IMAGE=" & if exist "Y:\sources\install.esd" set "HAS_IMAGE=1" & if exist "Y:\sources\install.wim" set "HAS_IMAGE=1"
            )
        )
        if not defined HAS_IMAGE (
            net use Y: \\truenas.servis\WIS\win11 /user:guest "" >nul 2>&1
            set "HAS_IMAGE=" & if exist "Y:\sources\install.esd" set "HAS_IMAGE=1" & if exist "Y:\sources\install.wim" set "HAS_IMAGE=1"
        )
        if not defined HAS_IMAGE (
            net use Y: \\192.168.170.254\WIS\win11 /user:guest "" >nul 2>&1
            set "HAS_IMAGE=" & if exist "Y:\sources\install.esd" set "HAS_IMAGE=1" & if exist "Y:\sources\install.wim" set "HAS_IMAGE=1"
        )
    )

:SkipDetachY
    set "HAS_IMAGE=" & if exist "Y:\sources\install.esd" set "HAS_IMAGE=1" & if exist "Y:\sources\install.wim" set "HAS_IMAGE=1"
    if defined HAS_IMAGE (
        set "WIS_SOURCE=Y:"
        echo =======================================================
        echo  [+] WIS Accelerator: TrueNAS SMB 3.1.1 USPESNO AKTIVIRAN na Y:\!
        echo  [+] Protok: ~115 MB/s ^| Ocekivano trajanje: ~58 sekundi!
        echo =======================================================
    ) else (
        set "WIS_SOURCE=Z:\win11"
        echo [*] WIS Accelerator: Slovo Y: zauzeto, ali Z:\win11 je spreman za Setup.
    )
) else (
    echo [*] WIS Accelerator: Rad se nastavlja sa lokalnog medijuma (USB/NVMe) uz aktivan TrueNAS disk Z:\.
)

if exist "X:\" echo 1 > "X:\WIS_PreBoot_Done.tag"

:Done
exit /b 0
