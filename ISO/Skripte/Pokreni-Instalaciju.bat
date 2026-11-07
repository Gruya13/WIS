@echo off
setlocal EnableDelayedExpansion
title TEHNO KUTAK Auto-Deploy Launcher

:: 1. Provera administratorskih prava
net session >nul 2>&1
if %errorlevel% neq 0 (
    echo [INFO] Trazim administratorska prava za pokretanje instalacije...
    powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
    exit /b
)

:: 2. Radni direktorijum C:\setup
if not exist "C:\setup" mkdir "C:\setup" >nul 2>&1
cd /d "C:\setup"

:: 3. Ako skripta, launcher ili UI postoje u folderu gde se nalazi ovaj .bat fajl, osvezi C:\setup
if exist "%~dp0GWADT.Launcher.exe" (
    copy /y "%~dp0GWADT.Launcher.exe" "C:\setup\GWADT.Launcher.exe" >nul 2>&1
)
if exist "%~dp0Skripte\GWADT.Launcher\GWADT.Launcher.exe" (
    copy /y "%~dp0Skripte\GWADT.Launcher\GWADT.Launcher.exe" "C:\setup\GWADT.Launcher.exe" >nul 2>&1
)
if exist "%~dp0sources\$OEM$\$1\setup\GWADT.Launcher.exe" (
    copy /y "%~dp0sources\$OEM$\$1\setup\GWADT.Launcher.exe" "C:\setup\GWADT.Launcher.exe" >nul 2>&1
)
if exist "%~dp0Wifi-Servis.ini" (
    copy /y "%~dp0Wifi-Servis.ini" "C:\setup\Wifi-Servis.ini" >nul 2>&1
)
if exist "%~dp0Skripte\GWADT.UI\GWADT.UI.exe" (
    if not exist "C:\setup\Skripte\GWADT.UI" mkdir "C:\setup\Skripte\GWADT.UI" >nul 2>&1
    copy /y "%~dp0Skripte\GWADT.UI\GWADT.UI.exe" "C:\setup\Skripte\GWADT.UI\GWADT.UI.exe" >nul 2>&1
)
if exist "%~dp0Skripte\Modules" (
    if not exist "C:\setup\Skripte\Modules" (
        mkdir "C:\setup\Skripte" >nul 2>&1
        robocopy "%~dp0Skripte" "C:\setup\Skripte" /E /R:1 /W:1 /NDL /NFL /NJH /NJS >nul 2>&1
    )
)

:: 4. Prvenstveno pokreni GWADT.Launcher.exe (.NET 4.0 Autoupdater sa NAS-a)
if exist "C:\setup\GWADT.Launcher.exe" (
    echo [INFO] Pokrecem GWADT Launcher (.NET 4.0 autoupdater)...
    start "" "C:\setup\GWADT.Launcher.exe"
    exit /b 0
)

:: 4.1. Sekundarno pokreni GWADT.UI.exe ako vec postoji (Master Shell)
if exist "C:\setup\Skripte\GWADT.UI\GWADT.UI.exe" (
    echo [INFO] Pokrecem GWADT.UI korisnicki interfejs...
    start "" /MAX "C:\setup\Skripte\GWADT.UI\GWADT.UI.exe" --master
    exit /b 0
)

:: 5. Ako nema UI-ja, pokreni modularne faze (Phase1 do Phase5) u konzolnom modu
if exist "C:\setup\Skripte\Phases\Phase1-Network.ps1" (
    echo [INFO] Pokrecem TEHNO KUTAK modularne faze instalacije (konzolni mod)...
    powershell.exe -NoProfile -NoExit -ExecutionPolicy Bypass -Command "& 'C:\setup\Skripte\Phases\Phase1-Network.ps1'; & 'C:\setup\Skripte\Phases\Phase2-Drivers.ps1'; & 'C:\setup\Skripte\Phases\Phase3-Optimization.ps1'; & 'C:\setup\Skripte\Phases\Phase4-Apps.ps1'; & 'C:\setup\Skripte\Phases\Phase5-Finalize.ps1'"
    exit /b %errorlevel%
)

:: 6. Fallback pretraga na svim diskovima za GWADT.UI ili modularnim skriptama
echo [UPOZORENJE] GWADT komponente nisu pronadjene u C:\setup. Trazim na diskovima...
powershell.exe -NoProfile -ExecutionPolicy Bypass -Command ^
    "$d = Get-PSDrive FileSystem -ErrorAction SilentlyContinue | Where-Object { $_.Root -ne 'C:\' -and ((Test-Path (Join-Path $_.Root 'Skripte\GWADT.UI\GWADT.UI.exe')) -or (Test-Path (Join-Path $_.Root 'Skripte\Phases\Phase1-Network.ps1'))) } | Select-Object -First 1; " ^
    "if ($d) { " ^
    "    $src = Join-Path $d.Root 'Skripte'; " ^
    "    if (-not (Test-Path 'C:\setup\Skripte')) { New-Item -Path 'C:\setup\Skripte' -ItemType Directory -Force | Out-Null }; " ^
    "    & robocopy.exe $src 'C:\setup\Skripte' /E /R:1 /W:1 /NDL /NFL /NJH /NJS >nul 2>&1; " ^
    "    $uiSrc = 'C:\setup\Skripte\GWADT.UI\GWADT.UI.exe'; " ^
    "    if (Test-Path $uiSrc) { " ^
    "        Start-Process -FilePath $uiSrc -ArgumentList '--master' -WindowStyle Maximized; " ^
    "    } else { " ^
    "        Start-Process powershell.exe -ArgumentList '-NoProfile -NoExit -ExecutionPolicy Bypass -Command \"& C:\setup\Skripte\Phases\Phase1-Network.ps1; & C:\setup\Skripte\Phases\Phase2-Drivers.ps1; & C:\setup\Skripte\Phases\Phase3-Optimization.ps1; & C:\setup\Skripte\Phases\Phase4-Apps.ps1; & C:\setup\Skripte\Phases\Phase5-Finalize.ps1\"' -WindowStyle Maximized; " ^
    "    } " ^
    "} else { " ^
    "    Write-Host '[GRESKA] GWADT komponente nisu pronadjene ni na jednom disku!' -ForegroundColor Red; " ^
    "    Write-Host 'Molimo proverite da li je USB prikljucen.' -ForegroundColor Yellow; " ^
    "    Write-Host 'Pritisnite bilo koji taster za izlaz...'; " ^
    "    $null = $Host.UI.RawUI.ReadKey('NoEcho,IncludeKeyDown'); " ^
    "}"

exit /b 0
