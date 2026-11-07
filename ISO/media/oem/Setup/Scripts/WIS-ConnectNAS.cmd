@echo off
setlocal EnableDelayedExpansion
rem WIS NAS connection: reads credentials from Wifi-Servis.ini (or WIS_NAS_PASS env) with guest fallback.
if /I "%~1"=="Y:" goto :InitAndMapSetup
if /I "%~1"=="P:" goto :InitAndMapProgrami
if exist "Z:\win11\sources\install.esd" exit /b 0
if exist "Z:\win11\sources\install.wim" exit /b 0

call :LoadIniCredentials

findstr /I /C:"truenas.servis" "%SystemRoot%\System32\drivers\etc\hosts" >nul 2>&1
if errorlevel 1 echo !NAS_HOST! truenas.servis>>"%SystemRoot%\System32\drivers\etc\hosts"

:: 1. Primarno povezivanje na WIS deljenje sa konfigurisanim akreditivima (ako je lozinka definisana u Wifi-Servis.ini)
if defined NAS_PASS (
    net use Z: "\\truenas.servis\!NAS_SHARE!" "!NAS_PASS!" /user:!NAS_USER! /persistent:no >nul 2>&1
    if not errorlevel 1 exit /b 0
    net use Z: "\\!NAS_HOST!\!NAS_SHARE!" "!NAS_PASS!" /user:!NAS_USER! /persistent:no >nul 2>&1
    if not errorlevel 1 exit /b 0
)

:: 2. Fallback na guest ako server dozvoli anonimni pristup
net use Z: "\\truenas.servis\!NAS_SHARE!" /persistent:no >nul 2>&1
if not errorlevel 1 exit /b 0

net use Z: "\\!NAS_HOST!\!NAS_SHARE!" /persistent:no >nul 2>&1
if not errorlevel 1 exit /b 0

exit /b 1

:InitAndMapSetup
call :LoadIniCredentials
if exist "Y:\sources\install.esd" exit /b 0
if exist "Y:\sources\install.wim" exit /b 0
if defined NAS_PASS (
    net use Y: "\\truenas.servis\!NAS_SHARE!\win11" "!NAS_PASS!" /user:!NAS_USER! /persistent:no >nul 2>&1
    if not errorlevel 1 exit /b 0
    net use Y: "\\!NAS_HOST!\!NAS_SHARE!\win11" "!NAS_PASS!" /user:!NAS_USER! /persistent:no >nul 2>&1
    if not errorlevel 1 exit /b 0
)
net use Y: "\\truenas.servis\!NAS_SHARE!\win11" /persistent:no >nul 2>&1
if not errorlevel 1 exit /b 0
net use Y: "\\!NAS_HOST!\!NAS_SHARE!\win11" /persistent:no >nul 2>&1
exit /b %errorlevel%

:InitAndMapProgrami
call :LoadIniCredentials
if exist "P:\Office 2024" exit /b 0
if defined NAS_PASS (
    net use P: "\\truenas.servis\!PROG_SHARE!" "!NAS_PASS!" /user:!NAS_USER! /persistent:no >nul 2>&1
    if not errorlevel 1 exit /b 0
    net use P: "\\!NAS_HOST!\!PROG_SHARE!" "!NAS_PASS!" /user:!NAS_USER! /persistent:no >nul 2>&1
    if not errorlevel 1 exit /b 0
)
net use P: "\\truenas.servis\!PROG_SHARE!" /persistent:no >nul 2>&1
if not errorlevel 1 exit /b 0
net use P: "\\!NAS_HOST!\!PROG_SHARE!" /persistent:no >nul 2>&1
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
