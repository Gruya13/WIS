@echo off
title GWADT - Osvezavanje Windows 11 WIM Slike
cd /d "%~dp0"

:: 1. Provera administratorskih prava i elevacija ako je potrebno
net session >nul 2>&1
if %errorlevel% neq 0 (
    echo [INFO] Pokrecem skriptu sa administratorskim pravima...
    powershell -NoProfile -ExecutionPolicy Bypass -Command "Start-Process cmd.exe -ArgumentList '/c `\"%~f0`\"' -Verb RunAs"
    exit /b
)

:: 2. Pronalazenje PowerShell skripte
set "SCRIPT_PATH="
if exist "%~dp0Skripte\Update-WimImage.ps1" (
    set "SCRIPT_PATH=%~dp0Skripte\Update-WimImage.ps1"
) else if exist "%~dp0Update-WimImage.ps1" (
    set "SCRIPT_PATH=%~dp0Update-WimImage.ps1"
) else (
    echo [GRESKA] Datoteka Update-WimImage.ps1 nije pronadjena!
    pause
    exit /b 1
)

:: 3. Pokretanje PowerShell skripte za offline servicing
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT_PATH%"
