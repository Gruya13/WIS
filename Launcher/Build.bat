@echo off
setlocal enabledelayedexpansion

set "SILENT=0"
if /i "%1"=="/silent" set "SILENT=1"
if /i "%1"=="/auto" set "SILENT=1"
if /i "%1"=="-silent" set "SILENT=1"
if /i "%1"=="-auto" set "SILENT=1"

set "LOG_DIR=C:\Windows\Logs\WIS"
if not exist "%LOG_DIR%" mkdir "%LOG_DIR%" >nul 2>&1
set "BUILD_LOG=%LOG_DIR%\build_launcher.log"
set "LOCAL_LOG=%~dp0build.log"

if "%SILENT%"=="0" (
    title WIS.Launcher Build Tool
    echo ========================================================
    echo     WIS.Launcher (.NET Framework 4.0) - Kompajliranje
    echo ========================================================
)

:: 1. Trazenje MSBuild-a u Visual Studio instalacijama
set "MSBUILD_PATH="
set "IS_FRAMEWORK_MSBUILD=0"

for %%v in (Enterprise Professional Community BuildTools) do (
    if exist "%ProgramFiles%\Microsoft Visual Studio\2022\%%v\MSBuild\Current\Bin\MSBuild.exe" (
        set "MSBUILD_PATH=%ProgramFiles%\Microsoft Visual Studio\2022\%%v\MSBuild\Current\Bin\MSBuild.exe"
        goto :FoundMsBuild
    )
    if exist "%ProgramFiles(x86)%\Microsoft Visual Studio\2019\%%v\MSBuild\Current\Bin\MSBuild.exe" (
        set "MSBUILD_PATH=%ProgramFiles(x86)%\Microsoft Visual Studio\2019\%%v\MSBuild\Current\Bin\MSBuild.exe"
        goto :FoundMsBuild
    )
)

:: 2. Provera vswhere.exe
if exist "%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe" (
    for /f "usebackq tokens=*" %%i in (`"%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe" -latest -requires Microsoft.Component.MSBuild -find MSBuild\**\Bin\MSBuild.exe`) do (
        set "MSBUILD_PATH=%%i"
        goto :FoundMsBuild
    )
)

:: 3. Fallback na ugradjeni .NET 4.0 Framework MSBuild
if exist "%SystemRoot%\Microsoft.NET\Framework64\v4.0.30319\MSBuild.exe" (
    set "MSBUILD_PATH=%SystemRoot%\Microsoft.NET\Framework64\v4.0.30319\MSBuild.exe"
    set "IS_FRAMEWORK_MSBUILD=1"
    goto :FoundMsBuild
)

:FoundMsBuild
if "%MSBUILD_PATH%"=="" (
    if "%SILENT%"=="0" (
        echo [GRESKA] MSBuild.exe nije pronadjen na sistemu!
        echo Molimo instalirajte Visual Studio ili proverite .NET Framework instalaciju.
        pause
    )
    exit /b 1
)

:: Postavljanje parametara za MSBuild
if "!IS_FRAMEWORK_MSBUILD!"=="1" (
    set "BUILD_PARAMS=/p:Configuration=Release /p:Platform="AnyCPU" /p:TargetFrameworkVersion=v4.0 /p:FrameworkPathOverride="%SystemRoot%\Microsoft.NET\Framework64\v4.0.30319" /v:minimal /nologo"
) else (
    set "BUILD_PARAMS=/p:Configuration=Release /p:Platform="AnyCPU" /p:TargetFrameworkVersion=v4.0 /v:minimal /nologo"
)

if "%SILENT%"=="0" (
    echo Pronadjen MSBuild: "%MSBUILD_PATH%"
    echo.
    echo Kompajliranje Release konfiguracije...
    "%MSBUILD_PATH%" "%~dp0WIS.Launcher.csproj" !BUILD_PARAMS!
) else (
    "%MSBUILD_PATH%" "%~dp0WIS.Launcher.csproj" !BUILD_PARAMS! > "%BUILD_LOG%" 2>&1
    copy /y "%BUILD_LOG%" "%LOCAL_LOG%" >nul 2>&1
)

if %ERRORLEVEL% NEQ 0 (
    if "%SILENT%"=="0" (
        echo.
        echo [NEUSPEH] Doslo je do greske tokom kompajliranja!
        pause
    )
    exit /b %ERRORLEVEL%
)

if "%SILENT%"=="0" (
    echo.
    echo ========================================================
    echo [USPEH] WIS.Launcher.exe je uspesno izgradjen!
    echo Lokacija: %~dp0bin\Release\WIS.Launcher.exe
    echo ========================================================
)

:: Iskopirati generisani bin\Release\WIS.Launcher.exe u definisane lokacije
if exist "%~dp0bin\Release\WIS.Launcher.exe" (
    copy /y "%~dp0bin\Release\WIS.Launcher.exe" "%~dp0WIS.Launcher.exe" >nul
    if "%SILENT%"=="0" (
        echo Izvrsni fajl je azuriran: "%~dp0WIS.Launcher.exe"
    )
    if exist "C:\setup" (
        copy /y "%~dp0bin\Release\WIS.Launcher.exe" "C:\setup\WIS.Launcher.exe" >nul
        if "%SILENT%"=="0" (
            echo Izvrsni fajl je sinhronizovan u C:\setup\WIS.Launcher.exe.
        )
    )
)

exit /b 0
