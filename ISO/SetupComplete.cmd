@echo off
setlocal EnableExtensions
:: ==============================================================================
:: WIS SetupComplete.cmd - Kratki wrapper za SetupComplete.ps1 (SYSTEM)
:: Paket A1: Izvrsava SetupComplete.ps1 preko -File i cuva stvarni izlazni kod
:: ==============================================================================

if not exist "C:\Windows\Logs\WIS" mkdir "C:\Windows\Logs\WIS" >nul 2>&1
if not exist "C:\setup" mkdir "C:\setup" >nul 2>&1

set "PS_SCRIPT="
if exist "%~dp0SetupComplete.ps1" set "PS_SCRIPT=%~dp0SetupComplete.ps1"
if not defined PS_SCRIPT if exist "C:\Windows\Setup\Scripts\SetupComplete.ps1" set "PS_SCRIPT=C:\Windows\Setup\Scripts\SetupComplete.ps1"
if not defined PS_SCRIPT if exist "C:\setup\SetupComplete.ps1" set "PS_SCRIPT=C:\setup\SetupComplete.ps1"
if not defined PS_SCRIPT if exist "C:\setup\Skripte\SetupComplete.ps1" set "PS_SCRIPT=C:\setup\Skripte\SetupComplete.ps1"

set "PS_EXIT=1"
if not defined PS_SCRIPT goto :AFTER_PS
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%PS_SCRIPT%"
set "PS_EXIT=%ERRORLEVEL%"

:AFTER_PS
:: Native CMD fallback osiguranje za sprecavanje Start Menija, animacija i RunOnce
reg.exe add "HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System" /v "EnableFirstLogonAnimation" /t REG_DWORD /d 0 /f >nul 2>&1
reg.exe load HKU\DefaultUser "C:\Users\Default\NTUSER.DAT" >nul 2>&1
reg.exe add "HKU\DefaultUser\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced" /v "StartShownOnUpgrade" /t REG_DWORD /d 1 /f >nul 2>&1
reg.exe unload HKU\DefaultUser >nul 2>&1

if exist "C:\setup\WIS.Launcher.exe" (
    reg.exe add "HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce" /v "WIS_Launcher" /t REG_SZ /d "C:\setup\WIS.Launcher.exe" /f >nul 2>&1
)

exit /b %PS_EXIT%