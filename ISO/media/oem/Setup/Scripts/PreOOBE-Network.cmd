@echo off
:: PreOOBE-Network.cmd - Pokrece PreOOBE-Network.ps1 za rano podizanje mreze i Wi-Fi profila
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0PreOOBE-Network.ps1"
