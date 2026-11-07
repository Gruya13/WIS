@echo off
set SEE_MASK_NOZONECHECKS=1
title TEHNO KUTAK Auto-Deploy Launcher
:: WIS FirstLogon.cmd - Robustan Zero-Touch Launcher i Desktop Startup Hook
:: WIS-TASK-089: Sprecavanje Start menija i pouzdano lansiranje Launchera na radnoj povrsini
:: WIS-TASK-116: Trajno eliminisanje 'Open File - Security Warning' dijaloga (ZoneIdentifier / Attachment Manager)

if not exist "C:\Windows\Logs\WIS" mkdir "C:\Windows\Logs\WIS" >nul 2>&1
if not exist "C:\setup" mkdir "C:\setup" >nul 2>&1
icacls "C:\setup" /grant "*S-1-5-18:(OI)(CI)F" /grant "*S-1-5-32-544:(OI)(CI)F" /grant "*S-1-1-0:(OI)(CI)F" /T /Q >nul 2>&1
icacls "C:\Windows\Logs\WIS" /grant "*S-1-5-18:(OI)(CI)F" /grant "*S-1-5-32-544:(OI)(CI)F" /grant "*S-1-1-0:(OI)(CI)F" /T /Q >nul 2>&1
reg.exe add "HKCU\Console" /v "QuickEdit" /t REG_DWORD /d 0 /f >nul 2>&1
reg.exe add "HKCU\Console\%%SystemRoot%%_System32_WindowsPowerShell_v1.0_powershell.exe" /v "QuickEdit" /t REG_DWORD /d 0 /f >nul 2>&1

:: Osiguranje Zero-Touch UAC polisa tokom instalacije
reg.exe add "HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System" /v "ConsentPromptBehaviorAdmin" /t REG_DWORD /d 0 /f >nul 2>&1
reg.exe add "HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System" /v "PromptOnSecureDesktop" /t REG_DWORD /d 0 /f >nul 2>&1
reg.exe add "HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System" /v "EnableFirstLogonAnimation" /t REG_DWORD /d 0 /f >nul 2>&1

:: WIS-TASK-116: Sistemske polise za eliminisanje 'Run / Cancel' (Open File - Security Warning) dijaloga
reg.exe add "HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Environment" /v "SEE_MASK_NOZONECHECKS" /t REG_SZ /d "1" /f >nul 2>&1
reg.exe add "HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Associations" /v "LowRiskFileTypes" /t REG_SZ /d ".exe;.bat;.cmd;.msi;.ps1;.reg;.vbs;.cab;.zip;" /f >nul 2>&1
reg.exe add "HKCU\Software\Microsoft\Windows\CurrentVersion\Policies\Associations" /v "LowRiskFileTypes" /t REG_SZ /d ".exe;.bat;.cmd;.msi;.ps1;.reg;.vbs;.cab;.zip;" /f >nul 2>&1
reg.exe add "HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Attachments" /v "SaveZoneInformation" /t REG_DWORD /d 1 /f >nul 2>&1
reg.exe add "HKCU\Software\Microsoft\Windows\CurrentVersion\Policies\Attachments" /v "SaveZoneInformation" /t REG_DWORD /d 1 /f >nul 2>&1
reg.exe add "HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Attachments" /v "HideZoneInfoOnProperties" /t REG_DWORD /d 1 /f >nul 2>&1
reg.exe add "HKCU\Software\Microsoft\Windows\CurrentVersion\Policies\Attachments" /v "HideZoneInfoOnProperties" /t REG_DWORD /d 1 /f >nul 2>&1
reg.exe add "HKCU\Software\Microsoft\Windows\CurrentVersion\Internet Settings\ZoneMap\Ranges\Range1" /v ":Range" /t REG_SZ /d "192.168.*.*" /f >nul 2>&1
reg.exe add "HKCU\Software\Microsoft\Windows\CurrentVersion\Internet Settings\ZoneMap\Ranges\Range1" /v "*" /t REG_DWORD /d 1 /f >nul 2>&1
reg.exe add "HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Internet Settings\ZoneMap\Ranges\Range1" /v ":Range" /t REG_SZ /d "192.168.*.*" /f >nul 2>&1
reg.exe add "HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Internet Settings\ZoneMap\Ranges\Range1" /v "*" /t REG_DWORD /d 1 /f >nul 2>&1
reg.exe add "HKCU\Software\Microsoft\Windows\CurrentVersion\Internet Settings\Zones\1" /v "1806" /t REG_DWORD /d 0 /f >nul 2>&1
reg.exe add "HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Internet Settings\Zones\1" /v "1806" /t REG_DWORD /d 0 /f >nul 2>&1

:: Ciscenje eventualnih zaostalih nepotpisanih OEM duplikata u korenu C:\setup (PXE/OEM higijena)
if exist "C:\setup\Skripte\WIS.UI\WIS.UI.exe" (
    if exist "C:\setup\WIS.UI.exe" del /f /q "C:\setup\WIS.UI.exe" >nul 2>&1
    if exist "C:\setup\HidSharp.dll" del /f /q "C:\setup\HidSharp.dll" >nul 2>&1
    if exist "C:\setup\LibreHardwareMonitorLib.dll" del /f /q "C:\setup\LibreHardwareMonitorLib.dll" >nul 2>&1
)
if exist "C:\setup\Drivers\VMD" rmdir /s /q "C:\setup\Drivers\VMD" >nul 2>&1


:: 1. SPRECAVANJE AUTOMATSKOG OTVARANJA START MENIJA U KORISNICKOM PROFILU
reg.exe add "HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced" /v "StartShownOnUpgrade" /t REG_DWORD /d 1 /f >nul 2>&1

:: 2. KREIRANJE PRECICE (IKONICE) NA PUBLIC DESKTOP-U ZA SERVISERA
if not exist "C:\Users\Public\Desktop" mkdir "C:\Users\Public\Desktop" >nul 2>&1
powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "$wsh = New-Object -ComObject WScript.Shell; $tgt = 'C:\setup\WIS.Launcher.exe'; $sc = $wsh.CreateShortcut('C:\Users\Public\Desktop\WIS Launcher.lnk'); $sc.TargetPath = $tgt; $sc.WorkingDirectory = 'C:\setup'; $sc.Description = 'Tehno kutak WIS Auto-Deploy Launcher'; $sc.Save()" >nul 2>&1

:: 3. KREIRANJE DESKTOP STARTUP HOOK-A ZA GARANTOVANI START NA RADNOJ POVRSINI
set "STARTUP_DIR=C:\ProgramData\Microsoft\Windows\Start Menu\Programs\Startup"
if not exist "%STARTUP_DIR%" mkdir "%STARTUP_DIR%" >nul 2>&1

(
echo @echo off
echo setlocal EnableDelayedExpansion
echo timeout /t 1 /nobreak ^>nul 2^>^&1
echo taskkill.exe /F /IM StartMenuExperienceHost.exe ^>nul 2^>^&1
echo :: Trenutno povezivanje na servisni Wi-Fi na startu Desktopa
echo if exist "C:\setup\Wifi-Servis.ini" ^(
echo     net start wlansvc ^>nul 2^>^&1
echo     for /f "tokens=1,* delims==" %%%%a in ('type "C:\setup\Wifi-Servis.ini" 2^^^>nul ^^^| findstr /i "^^SSID="') do ^(
echo         set "WIFI_SSID=%%%%b"
echo     ^)
echo     if defined WIFI_SSID ^(
echo         for /f "tokens=1 delims=," %%%%s in ("!WIFI_SSID!") do netsh.exe wlan connect name="%%%%s" ^>nul 2^>^&1
echo         netsh.exe wlan connect name="!WIFI_SSID!" ^>nul 2^>^&1
echo     ^)
echo ^)
echo if exist "C:\setup\WIS.Launcher.exe" ^(
echo     tasklist.exe /FI "IMAGENAME eq WIS.Launcher.exe" 2^>nul ^| find.exe /I "WIS.Launcher.exe" ^>nul 2^>^&1 ^|^| start "" "C:\setup\WIS.Launcher.exe"
echo ^) else if exist "C:\setup\Skripte\WIS.UI\WIS.UI.exe" ^(
echo     tasklist.exe /FI "IMAGENAME eq WIS.UI.exe" 2^>nul ^| find.exe /I "WIS.UI.exe" ^>nul 2^>^&1 ^|^| start "" "C:\setup\Skripte\WIS.UI\WIS.UI.exe" --master
echo ^)
echo del "%%~f0" ^>nul 2^>^&1
) > "%STARTUP_DIR%\WIS-DesktopStart.cmd"

exit /b 0
