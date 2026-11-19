@echo off
title GWADT USB Tool - Centar za Odrzavanje USB Fleša
cd /d "%~dp0"

echo ==================================================================
echo  ⚡ Pokrecem GWADT USB Tool (Centar za Odrzavanje USB-a)...
echo ==================================================================

python --version >nul 2>&1
if %errorlevel% equ 0 (
    python "%~dp0app.py" %*
    goto end
)

python3 --version >nul 2>&1
if %errorlevel% equ 0 (
    python3 "%~dp0app.py" %*
    goto end
)

echo [GRESKA] Python nije pronadjen na sistemu!
echo Molimo instalirajte Python 3.8+ i omogucite 'Add Python to PATH'.
pause
exit /b 1

:end
