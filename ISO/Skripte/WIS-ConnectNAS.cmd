@echo off
setlocal EnableDelayedExpansion
rem WIS NAS connection: reads credentials from Wifi-Servis.ini (or WIS_NAS_PASS env) with guest fallback.

if /I "%~1"=="Y:" goto :InitAndMapSetup
if /I "%~1"=="P:" goto :InitAndMapProgrami

:: Podrazumevano: mapiranje Z: na \\truenas.servis\WIS
if exist "Z:\win11\sources\install.esd" exit /b 0
if exist "Z:\win11\sources\install.wim" exit /b 0

call :LoadIniCredentials

find.exe /I "truenas.servis" "%SystemRoot%\System32\drivers\etc\hosts" >nul 2>&1
if errorlevel 1 echo !NAS_HOST! truenas.servis>>"%SystemRoot%\System32\drivers\etc\hosts"

:: 1. Primarno povezivanje na WIS deljenje sa konfigurisanim akreditivima uz brzu retry petlju (do 2 pokusaja)
if defined NAS_PASS (
    for /L %%i in (1,1,2) do (
        net.exe use Z: "\\truenas.servis\!NAS_SHARE!" "!NAS_PASS!" /user:!NAS_USER! /persistent:no >nul 2>&1
        if not errorlevel 1 exit /b 0
        net.exe use Z: "\\!NAS_HOST!\!NAS_SHARE!" "!NAS_PASS!" /user:!NAS_USER! /persistent:no >nul 2>&1
        if not errorlevel 1 exit /b 0
        ping.exe -n 2 127.0.0.1 >nul 2>&1
    )
)

:: 2. Fallback na guest ako server dozvoli anonimni pristup
net.exe use Z: "\\truenas.servis\!NAS_SHARE!" /persistent:no >nul 2>&1
if not errorlevel 1 exit /b 0

net.exe use Z: "\\!NAS_HOST!\!NAS_SHARE!" /persistent:no >nul 2>&1
if not errorlevel 1 exit /b 0

exit /b 1

:InitAndMapSetup
call :LoadIniCredentials

find.exe /I "truenas.servis" "%SystemRoot%\System32\drivers\etc\hosts" >nul 2>&1
if errorlevel 1 echo !NAS_HOST! truenas.servis>>"%SystemRoot%\System32\drivers\etc\hosts"

:: Provera da li je Y: vec montiran na nase TrueNAS deljenje sa postojecim install.esd
net.exe use | find.exe /I "Y:" | find.exe /I "!NAS_SHARE!\win11" >nul 2>&1
if not errorlevel 1 if exist "Y:\sources\install.esd" exit /b 0
if not errorlevel 1 if exist "Y:\sources\install.wim" exit /b 0

:: ZASTITA: Ako Y: vec ima postojeci install.esd ili install.wim (npr. iVentoy virtuelni CD-ROM),
:: i TrueNAS Z:\win11 nije dostupan, NE gaziti Y: bezuslovno!
if exist "Y:\sources\install.esd" (
    fsutil.exe fsinfo drivetype Y: 2>nul | find.exe /I "CD-ROM" >nul 2>&1
    if not errorlevel 1 (
        echo [*] WIS: Y: je aktivan iVentoy virtuelni CD-ROM sa vazecim install.esd medijumom.
        exit /b 0
    )
)
if exist "Y:\sources\install.wim" (
    fsutil.exe fsinfo drivetype Y: 2>nul | find.exe /I "CD-ROM" >nul 2>&1
    if not errorlevel 1 (
        echo [*] WIS: Y: je aktivan iVentoy virtuelni CD-ROM sa vazecim install.wim medijumom.
        exit /b 0
    )
)

:: Oslobodi slovo Y: od eventualnog neispravnog ili tudjeg mapiranja
mountvol.exe Y: /D >nul 2>&1
mountvol.exe Y: /P >nul 2>&1
net.exe use Y: /delete /y >nul 2>&1

if defined NAS_PASS (
    for /L %%i in (1,1,2) do (
        net.exe use Y: "\\truenas.servis\!NAS_SHARE!\win11" "!NAS_PASS!" /user:!NAS_USER! /persistent:no >nul 2>&1
        if not errorlevel 1 exit /b 0
        net.exe use Y: "\\!NAS_HOST!\!NAS_SHARE!\win11" "!NAS_PASS!" /user:!NAS_USER! /persistent:no >nul 2>&1
        if not errorlevel 1 exit /b 0
        ping.exe -n 2 127.0.0.1 >nul 2>&1
    )
)
net.exe use Y: "\\truenas.servis\!NAS_SHARE!\win11" /persistent:no >nul 2>&1
if not errorlevel 1 exit /b 0
net.exe use Y: "\\!NAS_HOST!\!NAS_SHARE!\win11" /persistent:no >nul 2>&1
exit /b %errorlevel%

:InitAndMapProgrami
call :LoadIniCredentials
if exist "P:\Office 2024" exit /b 0
if defined NAS_PASS (
    for /L %%i in (1,1,2) do (
        net.exe use P: "\\truenas.servis\!PROG_SHARE!" "!NAS_PASS!" /user:!NAS_USER! /persistent:no >nul 2>&1
        if not errorlevel 1 exit /b 0
        net.exe use P: "\\!NAS_HOST!\!PROG_SHARE!" "!NAS_PASS!" /user:!NAS_USER! /persistent:no >nul 2>&1
        if not errorlevel 1 exit /b 0
        ping.exe -n 2 127.0.0.1 >nul 2>&1
    )
)
net.exe use P: "\\truenas.servis\!PROG_SHARE!" /persistent:no >nul 2>&1
if not errorlevel 1 exit /b 0
net.exe use P: "\\!NAS_HOST!\!PROG_SHARE!" /persistent:no >nul 2>&1
exit /b %errorlevel%

:LoadIniCredentials
set "NAS_HOST=192.168.170.254"
set "NAS_SHARE=WIS"
set "PROG_SHARE=Programi"
set "NAS_USER=admin"
set "NAS_PASS=%WIS_NAS_PASS%"
set "IN_NAS_SEC=0"
for %%f in ("%~dp0Wifi-Servis.ini" "X:\Windows\System32\Wifi-Servis.ini" "Y:\Wifi-Servis.ini" "C:\setup\Wifi-Servis.ini") do (
    if exist "%%~f" (
        for /f "usebackq tokens=1,* delims==" %%a in ("%%~f") do (
            set "LINE_KEY=%%a"
            if /I "!LINE_KEY:~0,1!"=="[" (
                if /I "!LINE_KEY!"=="[ServisNas]" (set "IN_NAS_SEC=1") else (set "IN_NAS_SEC=0")
            ) else if "!IN_NAS_SEC!"=="1" (
                if /I "%%a"=="NasHost" set "NAS_HOST=%%b"
                if /I "%%a"=="ShareName" set "NAS_SHARE=%%b"
                if /I "%%a"=="ProgramiShare" set "PROG_SHARE=%%b"
                if /I "%%a"=="Username" set "NAS_USER=%%b"
                if /I "%%a"=="Password" if /I not "%%b"=="promeni_lozinku" if /I not "%%b"=="CHANGE_ME" set "NAS_PASS=%%b"
            )
        )
        goto :eof
    )
)
goto :eof
