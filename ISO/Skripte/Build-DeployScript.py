#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
Build-DeployScript.py - GWADT Build & Assembly Pipeline
Spaja modularne PowerShell komponente iz Skripte/Modules/ u jedinstvenu
monolitnu produkcionu skriptu Install-ServisApps.ps1 i sinhronizuje je
preko celog repozitorijuma (root, Skripte, sources/$OEM$/$1/setup).
"""

import os
import re
import hashlib
import sys
import json
from datetime import datetime

SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
PROJECT_ROOT = os.path.dirname(SCRIPT_DIR)
MODULES_DIR = os.path.join(SCRIPT_DIR, "Modules")

TARGET_FILES = [
    os.path.join(PROJECT_ROOT, "Install-ServisApps.ps1"),
    os.path.join(PROJECT_ROOT, "Skripte", "Install-ServisApps.ps1"),
    os.path.join(PROJECT_ROOT, "sources", "$OEM$", "$1", "setup", "Install-ServisApps.ps1")
]

STORAGE_DIR = "/home/gru/Storage"
if os.path.exists(STORAGE_DIR):
    TARGET_FILES.append(os.path.join(STORAGE_DIR, "Install-ServisApps.ps1"))
    if os.path.exists(os.path.join(STORAGE_DIR, "Skripte")):
        TARGET_FILES.append(os.path.join(STORAGE_DIR, "Skripte", "Install-ServisApps.ps1"))

MODULE_ORDER = [
    "GWADT.Core.psm1",
    "GWADT.Hardware.psm1",
    "GWADT.Network.psm1",
    "GWADT.Telemetry.psm1",
    "GWADT.UI.psm1",
    "GWADT.Update.psm1",
    "GWADT.Apps.psm1",
    "GWADT.Branding.psm1",
    "GWADT.Cleanup.psm1"
]

PREAMBLE = '''param(
    [int]$Phase = 0,
    [string]$Worker = ""
)

# Zabelezi vreme pocetka skripte na samom vrhu
if (-not $Global:ScriptStartTime) {
    $Global:ScriptStartTime = Get-Date
}

if ($Phase -gt 0) {
    $Global:CurrentPhase = $Phase
}

if ($Worker) {
    $Global:Worker = $Worker
}


# ==============================================================================
# TEHNO KUTAK AUTO-DEPLOY & UPDATE TOOLKIT | Windows 11 Automatizacija
# ==============================================================================

# 1. Provera i automatsko podizanje na Administratorska prava (sa -NoExit za interaktivni mod)
if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    $argList = "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`""
    if ($Phase -gt 0) {
        $argList += " -Phase $Phase"
    }
    if ($Worker) {
        $argList += " -Worker `"$Worker`""
        Start-Process powershell.exe -ArgumentList $argList -Verb RunAs -WindowStyle Hidden
    } else {
        $argList = "-NoProfile -NoExit -ExecutionPolicy Bypass -File `"$PSCommandPath`""
        if ($Phase -gt 0) {
            $argList += " -Phase $Phase"
        }
        Start-Process powershell.exe -ArgumentList $argList -Verb RunAs -WindowStyle Maximized
    }
    return
}

# Process Takeover mehanizam: preuzimanje kontrole nad zaostalim instancama skripte
if (-not $Worker -and $Phase -eq 0) {
    try {
        $otherProcs = Get-CimInstance Win32_Process -ErrorAction SilentlyContinue | Where-Object {
            $_.Name -match 'powershell' -and
            $_.CommandLine -match 'Install-ServisApps\\.ps1' -and
            $_.CommandLine -notmatch '-Worker' -and
            $_.CommandLine -notmatch '-Phase' -and
            $_.ProcessId -ne $PID
        }
        if ($otherProcs) {
            foreach ($op in $otherProcs) {
                Write-Host "Pronadjena prethodna/pozadinska instanca Install-ServisApps.ps1 (PID: $($op.ProcessId)). Terminiram je radi preuzimanja interaktivne kontrole..." -ForegroundColor Yellow
                Stop-Process -Id $op.ProcessId -Force -ErrorAction SilentlyContinue
            }
            Start-Sleep -Milliseconds 500
        }
    } catch {}
}

$Host.UI.RawUI.WindowTitle = "TEHNO KUTAK AUTO-DEPLOY I UPDATE TOOLKIT"

try {
    [Console]::OutputEncoding = [System.Text.Encoding]::UTF8
    $OutputEncoding = [System.Text.Encoding]::UTF8
} catch {}

# 2. Rano sprecavanje gasenja ekrana (ekran ostaje upaljen tokom celog procesa)
try {
    powercfg -change -monitor-timeout-ac 0
    powercfg -change -monitor-timeout-dc 0
} catch {}

# 3. Oporavak mrežnih adaptera, drajver polisa i Windows Update servisa nakon OOBE faze
try {
    Get-NetAdapter -ErrorAction SilentlyContinue | Enable-NetAdapter -Confirm:$false -ErrorAction SilentlyContinue
} catch {}

try {
    $wuPolPaths = @(
        "HKLM:\\SOFTWARE\\Policies\\Microsoft\\Windows\\WindowsUpdate",
        "HKLM:\\SOFTWARE\\Policies\\Microsoft\\Windows\\WindowsUpdate\\AU"
    )
    foreach ($pol in $wuPolPaths) {
        if (Test-Path $pol) {
            @("DoNotConnectToWindowsUpdateInternetLocations", "NoAutoUpdate", "DisableOSUpgrade", "UseWUServer", "WUServer", "WUStatusServer") | ForEach-Object {
                Remove-ItemProperty -Path $pol -Name $_ -ErrorAction SilentlyContinue
            }
        }
    }
    Remove-ItemProperty -Path "HKLM:\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\OOBE" -Name "DisableOOBEConnection" -ErrorAction SilentlyContinue

    # Eksplicitno dozvoli preuzimanje drajvera sa Windows Update servisa
    $drvSearchPath = "HKLM:\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\DriverSearching"
    if (-not (Test-Path $drvSearchPath)) { New-Item -Path $drvSearchPath -Force -ErrorAction SilentlyContinue | Out-Null }
    Set-ItemProperty -Path $drvSearchPath -Name "SearchOrderConfig" -Value 1 -Type DWord -Force -ErrorAction SilentlyContinue

    # Ukloni ogranicenja brzine i omoguci LAN Peering za Delivery Optimization
    $doPath = "HKLM:\\SOFTWARE\\Policies\\Microsoft\\Windows\\DeliveryOptimization"
    if (-not (Test-Path $doPath)) { New-Item -Path $doPath -Force -ErrorAction SilentlyContinue | Out-Null }
    Set-ItemProperty -Path $doPath -Name "DODownloadMode" -Value 1 -Type DWord -Force -ErrorAction SilentlyContinue
    Set-ItemProperty -Path $doPath -Name "DOMaxForegroundDownloadBandwidth" -Value 0 -Type DWord -Force -ErrorAction SilentlyContinue
    Set-ItemProperty -Path $doPath -Name "DOMaxBackgroundDownloadBandwidth" -Value 0 -Type DWord -Force -ErrorAction SilentlyContinue
    Set-ItemProperty -Path $doPath -Name "DOAbsoluteMaxDownloadBandwidth" -Value 0 -Type DWord -Force -ErrorAction SilentlyContinue
    Set-ItemProperty -Path $doPath -Name "DOMinDiskSizeAllowedToPeer" -Value 1 -Type DWord -Force -ErrorAction SilentlyContinue
    Set-ItemProperty -Path $doPath -Name "DOMinRAMAllowedToPeer" -Value 1 -Type DWord -Force -ErrorAction SilentlyContinue

    # Pokreni i postavi sve potrebne servise na Automatic
    @("wuauserv", "UsoSvc", "bits", "cryptsvc", "DoSvc") | ForEach-Object {
        Set-Service -Name $_ -StartupType Automatic -ErrorAction SilentlyContinue
        Start-Service -Name $_ -ErrorAction SilentlyContinue
    }
} catch {}

# ------------------------------------------------------------------------------
# SISTEM DETALJNOG SERVISNOG LOGOVANJA (Multi-Target Logging Engine)
# ------------------------------------------------------------------------------
$LocalSetupDir = "C:\\setup"
if (-not (Test-Path $LocalSetupDir)) {
    New-Item -Path $LocalSetupDir -ItemType Directory -Force | Out-Null
}

$SystemLogDir = "C:\\Windows\\Logs\\GWADT"
if (-not (Test-Path $SystemLogDir)) {
    New-Item -Path $SystemLogDir -ItemType Directory -Force | Out-Null
}

$PublicDesktopLog = "C:\\Users\\Public\\Desktop\\GWADT_Izvestaj.log"

$Global:LogPaths = [System.Collections.Generic.List[string]]::new()
$Global:LogPaths.Add((Join-Path $LocalSetupDir "servis_deploy.log"))
$Global:LogPaths.Add((Join-Path $SystemLogDir "servis_deploy.log"))
$Global:LogPaths.Add($PublicDesktopLog)
'''

POST_MODULES = '''
# ------------------------------------------------------------------------------
# INICIJALIZACIJA IZVORA, DISKOVA I LOKALNOG KEŠA
# ------------------------------------------------------------------------------
# Maksimizacija prozora konzole i prilagodjavanje bafera na pun ekran
if (-not $Worker) {
    try {
        Set-ConsoleMaximized
    } catch {}
}

Write-ServisLog "================================================================================" -Level "INFO" -Component "Init"
Write-ServisLog "POKRENUT TEHNO KUTAK AUTO-DEPLOY I UPDATE TOOLKIT" -Level "STEP" -Component "Init"
Write-ServisLog "Racunar: $env:COMPUTERNAME | Korisnik: $env:USERNAME | ScriptPath: $PSCommandPath" -Level "INFO" -Component "Init"
Write-ServisLog "PowerShell verzija: $($PSVersionTable.PSVersion) | CLR: $($PSVersionTable.CLRVersion)" -Level "INFO" -Component "Init"

# Odmah dodaj D:\\ particiju i C:\\setup u Windows Defender izuzetke da Defender ne bi pojeo instalacione fajlove
try {
    Add-MpPreference -ExclusionPath "D:\\" -ErrorAction SilentlyContinue
    Add-MpPreference -ExclusionPath $LocalSetupDir -ErrorAction SilentlyContinue
    Get-PSDrive -PSProvider FileSystem | Where-Object { $_.Root -ne "C:\\" -and ((Test-Path (Join-Path $_.Root "Programi")) -or (Test-Path (Join-Path $_.Root "Skripte"))) } | ForEach-Object {
        Add-MpPreference -ExclusionPath $_.Root -ErrorAction SilentlyContinue
    }
    Write-ServisLog "Podeseni Windows Defender izuzeci za C:\\setup i eksterne drajvove." -Level "DEBUG" -Component "Init"
} catch {}

$LocalScriptPath = Join-Path $LocalSetupDir "Install-ServisApps.ps1"
$StateFile = Join-Path $LocalSetupDir "deploy-state.json"
$Global:StateFile = $StateFile
$LocalProgramiDir = Join-Path $LocalSetupDir "Programi"

# Inicijalizuj servisno okruzenje i osiguraj NTFS permisije
Initialize-ServisEnvironment -SetupDir $LocalSetupDir -LogDir $SystemLogDir

# Preventivno poravnaj strukturu ako vec postoji ugnjezdeni C:\\setup\\Programi\\Programi
Repair-ProgramiNesting $LocalProgramiDir
Grant-SetupAclPermissions -Path $LocalSetupDir

# Ucitaj ili inicijalizuj stanje
if (Test-Path $StateFile) {
    try {
        $State = Get-Content $StateFile -Raw | ConvertFrom-Json
    } catch {
        $State = @{ Phase = 1; UpdateCycle = 1; MaxCycles = 3; WifiSSID = ""; Brand = ""; ProgramiDir = $LocalProgramiDir }
    }
} else {
    $State = @{ Phase = 1; UpdateCycle = 1; MaxCycles = 3; WifiSSID = ""; Brand = ""; ProgramiDir = $LocalProgramiDir }
}
$Global:State = $State

# Osiguraj da kljucni parametri uvek postoje u State objektu
if (-not $State.MaxCycles) { $State.MaxCycles = 3 }
if (-not $State.UpdateCycle) { $State.UpdateCycle = 1 }
if (-not $State.Phase) { $State.Phase = 1 }

# SessionId ucitavanje ili generisanje
$SessionId = $null
if ($State.PSObject -and $State.PSObject.Properties['SessionId'] -and $State.SessionId) {
    $SessionId = [string]$State.SessionId
} elseif ($State -is [hashtable] -and $State.ContainsKey('SessionId') -and $State['SessionId']) {
    $SessionId = [string]$State['SessionId']
}
if (-not $SessionId) {
    $SessionId = [guid]::NewGuid().ToString()
    if ($State -is [hashtable]) {
        $State['SessionId'] = $SessionId
    } else {
        $State | Add-Member -MemberType NoteProperty -Name "SessionId" -Value $SessionId -Force -ErrorAction SilentlyContinue
    }
}
$Global:SessionId = $SessionId
if (Get-Command Save-State -ErrorAction SilentlyContinue) { Save-State }

# Inicijalno prikupljanje hardverske telemetrije i identifikatora flote
$Global:HwInfo = Get-HwInfo
if (Get-Command Get-FleetDeviceId -ErrorAction SilentlyContinue) {
    $Global:FleetDeviceId = Get-FleetDeviceId
}
$DeviceModel = "$($Global:HwInfo.Manufacturer) $($Global:HwInfo.Model)".Trim()
$CpuName = $Global:HwInfo.CpuStr
$AutopilotPresent = $Global:HwInfo.Autopilot
$AutopilotTenant = $Global:HwInfo.AutopilotTenant

if (-not $State.Brand) {
    $State.Brand = Get-HardwareBrand $Global:HwInfo
}
$Brand = $State.Brand
$Global:Brand = $Brand

# Inicijalizacija korisnickog interfejsa (WPF ili Fallback na Konzolu)
if (-not $Worker) {
    if (Get-Command Initialize-ServisUI -ErrorAction SilentlyContinue) {
        Initialize-ServisUI -Backend "WPF" -HubUrl $Global:ServisHubUrl -SessionId $SessionId -ModelInfo $DeviceModel
    }
}

# Ako Programi jos nisu prekopirani na C:\\setup, kopiraj ih sa USB-a
$SourceRoot = Find-SourceDrive
$Global:SourceRoot = $SourceRoot
$Global:UsbCanBeRemoved = $false

if ($SourceRoot) {
    Register-UsbLog -SourcePath $SourceRoot
    Write-ServisLog "Pronadjen USB izvor na drajvu: $SourceRoot" -Level "INFO" -Component "Init"

    # Kopiraj skripte i ini ako nedostaju, uvek osvezi Install-ServisApps.ps1 sa USB-a
    if (Test-Path (Join-Path $SourceRoot "Install-ServisApps.ps1")) {
        Copy-Item -Path (Join-Path $SourceRoot "Install-ServisApps.ps1") -Destination $LocalScriptPath -Force -ErrorAction SilentlyContinue
    }
    $srcSkripte = Join-Path $SourceRoot "Skripte"
    if (Test-Path $srcSkripte) {
        Copy-Item -Path "$srcSkripte\\*" -Destination $LocalSetupDir -Recurse -Force -ErrorAction SilentlyContinue
    }
    $srcIni = Join-Path $SourceRoot "Wifi-Servis.ini"
    if (Test-Path $srcIni) {
        Copy-Item -Path $srcIni -Destination (Join-Path $LocalSetupDir "Wifi-Servis.ini") -Force -ErrorAction SilentlyContinue
    }

    $srcProgrami = Join-Path $SourceRoot "Programi"
    if (-not (Test-Path $srcProgrami)) {
        $srcProgrami = Join-Path $SourceRoot 'sources\\$OEM$\\$1\\setup\\Programi'
    }
    if (Test-Path $srcProgrami) {
        Repair-ProgramiNesting $srcProgrami
    }

    $needsCopy = $false
    if (Test-Path $srcProgrami) {
        if (-not (Test-Path $LocalProgramiDir)) {
            $needsCopy = $true
        } else {
            Repair-ProgramiNesting $LocalProgramiDir
            $officeCheck = (Test-Path (Join-Path $LocalProgramiDir "Office 2024\\files\\setup.exe")) -or (Test-Path (Join-Path $LocalProgramiDir "Office 2024\\setup.exe"))
            $adobeCheck = (Test-Path (Join-Path $LocalProgramiDir "Adobe.Acrobat.Pro.2024.x64.Multilingual\\Adobe Acrobat Pro\\AcroPro.msi")) -or (Test-Path (Join-Path $LocalProgramiDir "Acrobat Pro 2024\\Adobe Acrobat Pro\\AcroPro.msi"))
            $chromeCheck = (Test-Path (Join-Path $LocalProgramiDir "ChromeSetup.exe")) -or (Test-Path (Join-Path $LocalProgramiDir "GoogleChromeStandaloneEnterprise64.msi"))
            if ((-not $officeCheck) -or (-not $adobeCheck) -or (-not $chromeCheck)) {
                $needsCopy = $true
            }
        }
    }

    if ($needsCopy) {
        Write-Host "======================================================================================" -ForegroundColor Cyan
        Write-Host "   TEHNO KUTAK AUTO-DEPLOY I UPDATE TOOLKIT | Inicijalizacija" -ForegroundColor Cyan
        Write-Host "======================================================================================" -ForegroundColor Cyan
        Write-Host "   [KOPIRANJE] Prenosim instalacione pakete na C:\\setup..." -ForegroundColor Yellow
        Write-Host "   Molimo sacekajte ~30-60 sekundi da se podaci prebace sa USB-a na SSD." -ForegroundColor White
        Write-Host "   [!] NEMOJTE VADITI USB FLES DOK SE PRENOS I VERIFIKACIJA NE ZAVRSE!" -ForegroundColor Red
        Write-Host "--------------------------------------------------------------------------------------" -ForegroundColor DarkGray
        
        Write-ServisLog "Zapocinjem prenos paketa sa USB ($srcProgrami) na SSD ($LocalProgramiDir)..." -Level "STEP" -Component "Init"

        if (-not (Test-Path $LocalProgramiDir)) {
            New-Item -Path $LocalProgramiDir -ItemType Directory -Force | Out-Null
        }

        $sw = [System.Diagnostics.Stopwatch]::StartNew()
        $rc = Start-Process "robocopy.exe" -ArgumentList @($srcProgrami, $LocalProgramiDir, "/E", "/R:1", "/W:1", "/NDL", "/NFL", "/NJH", "/NJS") -Wait -PassThru -WindowStyle Hidden
        $sw.Stop()
        $dur = $sw.Elapsed.TotalSeconds.ToString("F1")
        Write-ServisLog "Robocopy prenos zavrsen za ${dur}s. ExitCode: $($rc.ExitCode)" -Level "INFO" -Component "Init"

        Repair-ProgramiNesting $LocalProgramiDir
        Grant-SetupAclPermissions -Path $LocalProgramiDir

        $vOffice = (Test-Path (Join-Path $LocalProgramiDir "Office 2024\\files\\setup.exe")) -or (Test-Path (Join-Path $LocalProgramiDir "Office 2024\\setup.exe"))
        $vAdobe = (Test-Path (Join-Path $LocalProgramiDir "Adobe.Acrobat.Pro.2024.x64.Multilingual\\Adobe Acrobat Pro\\AcroPro.msi")) -or (Test-Path (Join-Path $LocalProgramiDir "Acrobat Pro 2024\\Adobe Acrobat Pro\\AcroPro.msi"))
        $vChrome = (Test-Path (Join-Path $LocalProgramiDir "ChromeSetup.exe")) -or (Test-Path (Join-Path $LocalProgramiDir "GoogleChromeStandaloneEnterprise64.msi"))

        if ((-not $vOffice) -or (-not $vAdobe) -or (-not $vChrome)) {
            Write-ServisLog "Paketi nisu kompletni nakon robocopy-ja (Office=$vOffice, Adobe=$vAdobe, Chrome=$vChrome). Pokrecem Copy-Item dopunu..." -Level "WARN" -Component "Init"
            Copy-Item -Path "$srcProgrami\\*" -Destination "$LocalProgramiDir\\" -Recurse -Force -ErrorAction SilentlyContinue
            Repair-ProgramiNesting $LocalProgramiDir
            Grant-SetupAclPermissions -Path $LocalProgramiDir

            $vOffice = (Test-Path (Join-Path $LocalProgramiDir "Office 2024\\files\\setup.exe")) -or (Test-Path (Join-Path $LocalProgramiDir "Office 2024\\setup.exe"))
            $vAdobe = (Test-Path (Join-Path $LocalProgramiDir "Adobe.Acrobat.Pro.2024.x64.Multilingual\\Adobe Acrobat Pro\\AcroPro.msi")) -or (Test-Path (Join-Path $LocalProgramiDir "Acrobat Pro 2024\\Adobe Acrobat Pro\\AcroPro.msi"))
            $vChrome = (Test-Path (Join-Path $LocalProgramiDir "ChromeSetup.exe")) -or (Test-Path (Join-Path $LocalProgramiDir "GoogleChromeStandaloneEnterprise64.msi"))
        }

        if ($vOffice -and $vAdobe) {
            $Global:UsbCanBeRemoved = $true
            Write-ServisLog "Verifikacija uspesna! Kljucni instalacioni paketi su kompletirani na C:\\setup\\Programi." -Level "SUCCESS" -Component "Init"
            Write-Host "   [OK] Svi instalacioni paketi su prekopirani i verifikovani na C:\\setup\\Programi!" -ForegroundColor Green
            Write-Host "   -> USB fles mozete slobodno izvaditi!" -ForegroundColor Green
            if (Get-Command Update-ServisUI -ErrorAction SilentlyContinue) {
                Update-ServisUI -UsbFree:$true
            }
        } else {
            $Global:UsbCanBeRemoved = $false
            Write-ServisLog "UPOZORENJE: Paketi na SSD-u nisu kompletni (Office=$vOffice, Adobe=$vAdobe, Chrome=$vChrome). USB fles je i dalje potreban!" -Level "WARN" -Component "Init"
            Write-Host "   [!] UPOZORENJE: Neki paketi nisu kompletirani na SSD-u. Molimo OSTAVITE USB fles uboden!" -ForegroundColor Yellow
        }
        Start-Sleep -Seconds 2
    } else {
        Repair-ProgramiNesting $LocalProgramiDir
        $vOffice = (Test-Path (Join-Path $LocalProgramiDir "Office 2024\\files\\setup.exe")) -or (Test-Path (Join-Path $LocalProgramiDir "Office 2024\\setup.exe"))
        $vAdobe = (Test-Path (Join-Path $LocalProgramiDir "Adobe.Acrobat.Pro.2024.x64.Multilingual\\Adobe Acrobat Pro\\AcroPro.msi")) -or (Test-Path (Join-Path $LocalProgramiDir "Acrobat Pro 2024\\Adobe Acrobat Pro\\AcroPro.msi"))
        $Global:UsbCanBeRemoved = ($vOffice -and $vAdobe)
        if ($Global:UsbCanBeRemoved -and (Get-Command Update-ServisUI -ErrorAction SilentlyContinue)) {
            Update-ServisUI -UsbFree:$true
        }
    }
} else {
    Repair-ProgramiNesting $LocalProgramiDir
    $vOffice = (Test-Path (Join-Path $LocalProgramiDir "Office 2024\\files\\setup.exe")) -or (Test-Path (Join-Path $LocalProgramiDir "Office 2024\\setup.exe"))
    $vAdobe = (Test-Path (Join-Path $LocalProgramiDir "Adobe.Acrobat.Pro.2024.x64.Multilingual\\Adobe Acrobat Pro\\AcroPro.msi")) -or (Test-Path (Join-Path $LocalProgramiDir "Acrobat Pro 2024\\Adobe Acrobat Pro\\AcroPro.msi"))
    
    # PXE Boot Fallback: Ako paketi nedostaju na SSD-u, preuzmi ih sa servisnog NAS-a
    if ((-not $vOffice) -or (-not $vAdobe)) {
        Write-ServisLog "USB izvor nije detektovan i SSD paketi su nepotpuni. Pokusavam automatsko preuzimanje sa servisnog NAS-a..." -Level "INFO" -Component "Init"
        $nasSynced = Sync-ProgramiFromNas -DestinationDir $LocalProgramiDir
        Repair-ProgramiNesting $LocalProgramiDir
        Grant-SetupAclPermissions -Path $LocalProgramiDir
        $vOffice = (Test-Path (Join-Path $LocalProgramiDir "Office 2024\\files\\setup.exe")) -or (Test-Path (Join-Path $LocalProgramiDir "Office 2024\\setup.exe"))
        $vAdobe = (Test-Path (Join-Path $LocalProgramiDir "Adobe.Acrobat.Pro.2024.x64.Multilingual\\Adobe Acrobat Pro\\AcroPro.msi")) -or (Test-Path (Join-Path $LocalProgramiDir "Acrobat Pro 2024\\Adobe Acrobat Pro\\AcroPro.msi"))
    }

    $Global:UsbCanBeRemoved = ($vOffice -and $vAdobe)
    if ($Global:UsbCanBeRemoved -and (Get-Command Update-ServisUI -ErrorAction SilentlyContinue)) {
        Update-ServisUI -UsbFree:$true
    }
    Write-ServisLog "USB izvor nije detektovan. Koristim lokalni SSD keš (Spreman: $Global:UsbCanBeRemoved)." -Level $(if ($Global:UsbCanBeRemoved) { "INFO" } else { "WARN" }) -Component "Init"
}

# Ako je skripta pokrenuta sa USB-a, prebaci se na C:\\setup i pokreni odatle (samo za interaktivni mod)
if (-not $Worker -and $Phase -eq 0 -and $PSCommandPath -ne $LocalScriptPath -and (Test-Path $LocalScriptPath)) {
    $argList = "-NoProfile -NoExit -ExecutionPolicy Bypass -File `"$LocalScriptPath`""
    Start-Process powershell.exe -ArgumentList $argList
    return
}

$ProgramiDir = Get-ProgramiDir
$Global:ProgramiDir = $ProgramiDir
Write-ServisLog "Definisan direktorijum programa: $ProgramiDir" -Level "INFO" -Component "Init"

$rebootMarker = Join-Path $LocalSetupDir ".stage_reboot_done"
if (Test-Path $rebootMarker) {
    Write-ServisLog "Sistem stabilizovan nakon restarta. Nastavljam sa Fazom $($State.Phase)..." -Level "INFO" -Component "Lifecycle"
}

# Zastita integriteta stanja: Ako je faza 5, a programi nedostaju, vrati na fazu 4 (samo za autonomni pipeline)
if ($Phase -eq 0 -and $State.Phase -ge 5) {
    try {
        $isWordInstalled = (Test-Path "C:\\Program Files\\Microsoft Office\\root\\Office16\\WINWORD.EXE") -or (Test-Path "C:\\Program Files (x86)\\Microsoft Office\\root\\Office16\\WINWORD.EXE")
        $isChromeInstalled = (Test-Path "C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe") -or (Test-Path "C:\\Program Files (x86)\\Google\\Chrome\\Application\\chrome.exe")
        $isAcroInstalled = (Test-Path "C:\\Program Files\\Adobe\\Acrobat DC\\Acrobat\\Acrobat.exe") -or (Test-Path "C:\\Program Files (x86)\\Adobe\\Acrobat DC\\Acrobat\\Acrobat.exe") -or (Test-Path "C:\\Program Files\\Adobe\\Acrobat 2024\\Acrobat\\Acrobat.exe")
        if ((-not $isWordInstalled) -or (-not $isChromeInstalled) -or (-not $isAcroInstalled)) {
            Write-ServisLog "Detektovano stanje Phase=$($State.Phase), ali pojedini kljucni programi nisu instalirani! Vracam stanje na Fazu 4." -Level "WARN" -Component "Init"
            $State.Phase = 4
        }
    } catch {}
}

# ------------------------------------------------------------------------------
# PREUSMERAVANJE RADNIKA (WORKER ENTRY POINT)
# ------------------------------------------------------------------------------
if ($Worker -eq "Programs") {
    Write-ServisLog "=== RADNIK: POKRENUT PROCES ZA PARALELNU INSTALACIJU PROGRAMA ===" -Level "STEP" -Component "Worker"
    try {
        Install-AllServisPrograms -IsWorker
        Update-AppWorkerStatus -Status "Completed" -CurrentApp "Non-MSI programi instalirani (MSI odlozen za Fazu 4)" -Progress 60
        Write-ServisLog "=== RADNIK: PARALELNA INSTALACIJA NON-MSI PROGRAMA ZAVRSENA (MSI prepusten Fazi 4) ===" -Level "SUCCESS" -Component "Worker"
    } catch {
        Update-AppWorkerStatus -Status "Failed" -CurrentApp "Greska: $($_.Exception.Message)" -Progress 100
        Write-ServisLog "Greska u radniku za programe: $($_.Exception.Message)" -Level "ERROR" -Component "Worker"
    }
    exit 0
}

# ------------------------------------------------------------------------------
# FAZNO IZVRŠAVANJE ZA MODULARNE RADNIKE (-Phase 1..5)
# ------------------------------------------------------------------------------
if ($Phase -gt 0) {
    Write-ServisLog "=== POKRETANJE MODULARNOG RADNIKA: FAZA $Phase (Worker: '$Worker') ===" -Level "STEP" -Component "PhaseWorker"

    if ($Worker -eq "Pipeline" -or $Global:GWADT_UI_Backend -eq "Pipeline") {
        $Global:GWADT_UI_Backend = "Pipeline"
    }

    try {
        switch ($Phase) {
            1 {
                # FAZA 1: Mreža & Wi-Fi i osvežavanje mrežnih drajvera
                if (Get-Command Wait-ServisUICooperativePause -ErrorAction SilentlyContinue) {
                    Wait-ServisUICooperativePause -CheckpointName "Faza 1 - Mreza i Servisni Wi-Fi"
                }
                if (Get-Command Update-ServisUI -ErrorAction SilentlyContinue) {
                    Update-ServisUI -Phase 1 -SubPhase "Mreža i Wi-Fi" -StatusMessage "Povezivanje na servisnu mrežu..." -ProgressPercent 10
                }

                if (Get-Command Install-FallbackNetworkDrivers -ErrorAction SilentlyContinue) {
                    Install-FallbackNetworkDrivers
                }

                Invoke-NetworkPhase -LocalSetupDir $LocalSetupDir

                if (Get-Command Export-ServisNetworkDrivers -ErrorAction SilentlyContinue) {
                    Export-ServisNetworkDrivers
                }

                # Start Event telemetrija
                $Global:HwInfo = Get-HwInfo
                $startData = @{
                    device_id     = if ($Global:FleetDeviceId) { $Global:FleetDeviceId } else { $Global:HwInfo.SerialNumber }
                    serial_number = $Global:HwInfo.SerialNumber
                    hw            = @{
                        manufacturer = $Global:HwInfo.Manufacturer
                        model        = $Global:HwInfo.Model
                        cpu          = $Global:HwInfo.CpuStr
                        ram          = $Global:HwInfo.RamStr
                        disk         = $Global:HwInfo.DiskStr
                        battery      = $Global:HwInfo.BatteryStr
                    }
                    autopilot     = @{
                        status        = $Global:HwInfo.AutopilotStatus
                        is_registered = $Global:HwInfo.Autopilot
                        tenant        = $Global:HwInfo.AutopilotTenant
                    }
                }
                $null = Send-ServisLifecycleEvent -EventType "STARTED" -Data $startData -LocalSetupDir $LocalSetupDir
                Write-ServisLog "Poslat START telemetrijski izveštaj ka ServisHub-u (Autopilot: $($Global:HwInfo.AutopilotStatus), DeviceId: $($Global:FleetDeviceId))" -Level "INFO" -Component "Telemetry"
                Write-ServisLog "=== FAZA 1 USPESNO ZAVRSENA ===" -Level "SUCCESS" -Component "PhaseWorker"
                $global:LASTEXITCODE = 0
                exit 0
            }
            2 {
                # FAZA 2: Windows Update i NAS drajveri
                if (Get-Command Wait-ServisUICooperativePause -ErrorAction SilentlyContinue) {
                    Wait-ServisUICooperativePause -CheckpointName "Faza 2 - Windows Update i Drajveri"
                }
                if (Get-Command Update-ServisUI -ErrorAction SilentlyContinue) {
                    Update-ServisUI -Phase 2 -SubPhase "Windows Update" -StatusMessage "Priprema Windows Update iteracija i drajvera..." -ProgressPercent 25
                }

                Invoke-WindowsUpdatePhase -LocalScriptPath $LocalScriptPath
                if (Get-Command Export-ServisHardwareDrivers -ErrorAction SilentlyContinue) {
                    Export-ServisHardwareDrivers
                } elseif (Get-Command Export-ServisNetworkDrivers -ErrorAction SilentlyContinue) {
                    Export-ServisNetworkDrivers
                }
                Write-ServisLog "=== FAZA 2 USPESNO ZAVRSENA ===" -Level "SUCCESS" -Component "PhaseWorker"
                $global:LASTEXITCODE = 0
                exit 0
            }
            3 {
                # FAZA 3: Sistemske optimizacije, QuickEdit, DPI skaliranje, Defender izuzeci, OEM brendiranje
                if (Get-Command Wait-ServisUICooperativePause -ErrorAction SilentlyContinue) {
                    Wait-ServisUICooperativePause -CheckpointName "Faza 3 - Optimizacija i Brendiranje"
                }
                if (Get-Command Update-ServisUI -ErrorAction SilentlyContinue) {
                    Update-ServisUI -Phase 3 -SubPhase "Optimizacija i Brendiranje" -StatusMessage "Primenjujem sistemske optimizacije i OEM brendiranje..." -ProgressPercent 50
                }

                Invoke-SystemOptimizationAndBranding -Brand $Brand -LocalSetupDir $LocalSetupDir -ProgramiDir $ProgramiDir
                Write-ServisLog "=== FAZA 3 USPESNO ZAVRSENA ===" -Level "SUCCESS" -Component "PhaseWorker"
                $global:LASTEXITCODE = 0
                exit 0
            }
            4 {
                # FAZA 4: Aplikacije: .NET, Chrome, Office, Acrobat uz MSI Mutex čekanje
                if (Get-Command Wait-ServisUICooperativePause -ErrorAction SilentlyContinue) {
                    Wait-ServisUICooperativePause -CheckpointName "Faza 4 - Programi i OEM Licenca"
                }
                if (Get-Command Update-ServisUI -ErrorAction SilentlyContinue) {
                    Update-ServisUI -Phase 4 -SubPhase "Instalacija programa" -StatusMessage "Instalacija i verifikacija servisnih programa..." -ProgressPercent 70
                }

                if (Get-Command Wait-MsiMutexAvailable -ErrorAction SilentlyContinue) {
                    Wait-MsiMutexAvailable -TimeoutSec 180
                }

                Invoke-AppsPhase
                Write-ServisLog "=== FAZA 4 USPESNO ZAVRSENA ===" -Level "SUCCESS" -Component "PhaseWorker"
                $global:LASTEXITCODE = 0
                exit 0
            }
            5 {
                # FAZA 5: Čišćenje C:\\setup, uklanjanje tajni i higijena
                if (Get-Command Wait-ServisUICooperativePause -ErrorAction SilentlyContinue) {
                    Wait-ServisUICooperativePause -CheckpointName "Faza 5 - Zavrsetak i Verifikacija"
                }
                if (Get-Command Update-ServisUI -ErrorAction SilentlyContinue) {
                    Update-ServisUI -Phase 5 -SubPhase "Završetak i Verifikacija" -StatusMessage "Prikupljanje dijagnostike i servisnog izveštaja..." -ProgressPercent 90
                }

                # Telemetrijski izvestaj o zavrsetku
                $totalSec = if ($Global:ScriptStartTime) { [int]((Get-Date) - $Global:ScriptStartTime).TotalSeconds } else { 0 }
                $durationFmt = "{0}m {1:D2}s" -f [math]::Floor($totalSec / 60), ($totalSec % 60)

                $realApps = if (Get-Command Get-RealAppStatuses -ErrorAction SilentlyContinue) { Get-RealAppStatuses } else { $null }
                $appsMap = @{}
                if ($realApps -and $realApps.Items) {
                    foreach ($it in $realApps.Items) {
                        $appsMap[$it.Id] = $it.Status
                    }
                }
                $appsData = @{
                    Chrome  = if ($appsMap.ContainsKey("Chrome")) { $appsMap["Chrome"] } else { "Nepoznato" }
                    WinRAR  = if ($appsMap.ContainsKey("WinRAR")) { $appsMap["WinRAR"] } else { "Nepoznato" }
                    Office  = if ($appsMap.ContainsKey("Office")) { $appsMap["Office"] } else { "Nepoznato" }
                    Acrobat = if ($appsMap.ContainsKey("Acrobat")) { $appsMap["Acrobat"] } else { "Nepoznato" }
                }

                $freeSpaceGB = 0
                try {
                    $cDrive = Get-PSDrive -Name "C" -PSProvider FileSystem -ErrorAction SilentlyContinue
                    if ($cDrive -and $cDrive.Free) {
                        $freeSpaceGB = [math]::Round(($cDrive.Free / 1GB), 1)
                    } else {
                        $cVol = Get-CimInstance Win32_LogicalDisk -Filter "DeviceID='C:'" -ErrorAction SilentlyContinue
                        if ($cVol -and $cVol.FreeSpace) {
                            $freeSpaceGB = [math]::Round(($cVol.FreeSpace / 1GB), 1)
                        }
                    }
                } catch {}

                $completeData = @{
                    device_id          = if ($Global:FleetDeviceId) { $Global:FleetDeviceId } else { if ($Global:HwInfo) { $Global:HwInfo.SerialNumber } else { "N/A" } }
                    serial_number      = if ($Global:HwInfo -and $Global:HwInfo.SerialNumber) { $Global:HwInfo.SerialNumber } else { "N/A" }
                    duration_seconds   = $totalSec
                    duration_formatted = $durationFmt
                    apps               = $appsData
                    system_summary     = @{
                        manufacturer = if ($Global:HwInfo) { $Global:HwInfo.Manufacturer } else { "Nepoznato" }
                        model        = if ($Global:HwInfo) { $Global:HwInfo.Model } else { "Nepoznato" }
                        cpu          = if ($Global:HwInfo) { $Global:HwInfo.CpuStr } else { "Nepoznato" }
                        ram          = if ($Global:HwInfo) { $Global:HwInfo.RamStr } else { "Nepoznato" }
                        disk         = if ($Global:HwInfo) { $Global:HwInfo.DiskStr } else { "Nepoznato" }
                        battery      = if ($Global:HwInfo) { $Global:HwInfo.BatteryStr } else { "Nepoznato" }
                        free_c_disk  = "$freeSpaceGB GB"
                    }
                    autopilot_status   = if ($Global:HwInfo) { $Global:HwInfo.AutopilotStatus } else { "Clean" }
                }

                $null = Send-ServisLifecycleEvent -EventType "COMPLETED" -Data $completeData -LocalSetupDir $LocalSetupDir
                Write-ServisLog "Poslat COMPLETION telemetrijski izveštaj ka ServisHub-u (Trajanje: $durationFmt)" -Level "SUCCESS" -Component "Telemetry"

                if (Get-Command Send-FleetFinalReport -ErrorAction SilentlyContinue) {
                    Send-FleetFinalReport -Status "COMPLETED"
                }

                if (Get-Command Update-ServisUI -ErrorAction SilentlyContinue) {
                    Update-ServisUI -Phase 5 -SubPhase "Završeno" -StatusMessage "Sve operacije uspešno završene!" -ProgressPercent 100
                }
                if (Get-Command Close-ServisUI -ErrorAction SilentlyContinue) {
                    Close-ServisUI
                }

                Invoke-ServisCleanupAndFinalize -LocalSetupDir $LocalSetupDir -SystemLogDir $SystemLogDir -PublicDesktopLog $PublicDesktopLog -SourceRoot $SourceRoot -Worker:$true
                Write-ServisLog "=== FAZA 5 USPESNO ZAVRSENA ===" -Level "SUCCESS" -Component "PhaseWorker"
                $global:LASTEXITCODE = 0
                exit 0
            }
            default {
                Write-ServisLog "Nepoznata faza: $Phase. Izlazim sa greskom." -Level "ERROR" -Component "PhaseWorker"
                exit 1
            }
        }
    } catch {
        Write-ServisLog "KRITICNA GRESKA U FAZI $Phase RADNIKA: $($_.Exception.Message)" -Level "ERROR" -Component "PhaseWorker"
        if (Get-Command Send-FleetFinalReport -ErrorAction SilentlyContinue) {
            Send-FleetFinalReport -Status "FAILED" -Errors @($_.Exception.Message)
        }
        exit 1
    }
}

# ------------------------------------------------------------------------------
# GLAVNI SERVISNI PAJPLAJN (FAZE 1 DO 5) SA CRASH GUARD ZASTITOM
# ------------------------------------------------------------------------------
$pipelineRunning = $true
while ($pipelineRunning) {
    try {
        # FAZA 1: MREŽA I SERVISNI WI-FI
        if ($State.Phase -eq 1) {
            if (Get-Command Wait-ServisUICooperativePause -ErrorAction SilentlyContinue) {
                Wait-ServisUICooperativePause -CheckpointName "Faza 1 - Mreza i Servisni Wi-Fi"
            }
            if (Get-Command Update-ServisUI -ErrorAction SilentlyContinue) {
                Update-ServisUI -Phase 1 -SubPhase "Mreža i Wi-Fi" -StatusMessage "Povezivanje na servisnu mrežu..." -ProgressPercent 10
            }

            Invoke-NetworkPhase -LocalSetupDir $LocalSetupDir

            # Tačka 1 (Start Event & Autopilot)
            $Global:HwInfo = Get-HwInfo
            $startData = @{
                device_id     = if ($Global:FleetDeviceId) { $Global:FleetDeviceId } else { $Global:HwInfo.SerialNumber }
                serial_number = $Global:HwInfo.SerialNumber
                hw            = @{
                    manufacturer = $Global:HwInfo.Manufacturer
                    model        = $Global:HwInfo.Model
                    cpu          = $Global:HwInfo.CpuStr
                    ram          = $Global:HwInfo.RamStr
                    disk         = $Global:HwInfo.DiskStr
                    battery      = $Global:HwInfo.BatteryStr
                }
                autopilot     = @{
                    status        = $Global:HwInfo.AutopilotStatus
                    is_registered = $Global:HwInfo.Autopilot
                    tenant        = $Global:HwInfo.AutopilotTenant
                }
            }
            $null = Send-ServisLifecycleEvent -EventType "STARTED" -Data $startData -LocalSetupDir $LocalSetupDir
            Write-ServisLog "Poslat START telemetrijski izveštaj ka ServisHub-u (Autopilot: $($Global:HwInfo.AutopilotStatus), DeviceId: $($Global:FleetDeviceId))" -Level "INFO" -Component "Telemetry"
        }

        # FAZA 2: WINDOWS UPDATE ITERATIVNI KRUGOVI I DRAJVERI
        if ($State.Phase -eq 2) {
            if (Get-Command Wait-ServisUICooperativePause -ErrorAction SilentlyContinue) {
                Wait-ServisUICooperativePause -CheckpointName "Faza 2 - Windows Update i Drajveri"
            }
            if (Get-Command Update-ServisUI -ErrorAction SilentlyContinue) {
                Update-ServisUI -Phase 2 -SubPhase "Windows Update" -StatusMessage "Priprema Windows Update iteracija i drajvera..." -ProgressPercent 25
            }

            Invoke-WindowsUpdatePhase -LocalScriptPath $LocalScriptPath
            if (Get-Command Export-ServisHardwareDrivers -ErrorAction SilentlyContinue) {
                Export-ServisHardwareDrivers
            } elseif (Get-Command Export-ServisNetworkDrivers -ErrorAction SilentlyContinue) {
                Export-ServisNetworkDrivers
            }
        }

        # FAZA 3: SISTEMSKA OPTIMIZACIJA I BRENDIRANJE
        if ($State.Phase -eq 3) {
            if (Get-Command Wait-ServisUICooperativePause -ErrorAction SilentlyContinue) {
                Wait-ServisUICooperativePause -CheckpointName "Faza 3 - Optimizacija i Brendiranje"
            }
            if (Get-Command Update-ServisUI -ErrorAction SilentlyContinue) {
                Update-ServisUI -Phase 3 -SubPhase "Optimizacija i Brendiranje" -StatusMessage "Primenjujem sistemske optimizacije i OEM brendiranje..." -ProgressPercent 50
            }

            Invoke-SystemOptimizationAndBranding -Brand $Brand -LocalSetupDir $LocalSetupDir -ProgramiDir $ProgramiDir
            Invoke-ServisInterimReboot -Brand $Brand -LocalSetupDir $LocalSetupDir -LocalScriptPath $LocalScriptPath
        }

        # FAZA 4: PROGRAMI, PREČICE I OEM LICENCA
        if ($State.Phase -eq 4) {
            if (Get-Command Wait-ServisUICooperativePause -ErrorAction SilentlyContinue) {
                Wait-ServisUICooperativePause -CheckpointName "Faza 4 - Programi i OEM Licenca"
            }
            if (Get-Command Update-ServisUI -ErrorAction SilentlyContinue) {
                Update-ServisUI -Phase 4 -SubPhase "Instalacija programa" -StatusMessage "Instalacija i verifikacija servisnih programa..." -ProgressPercent 70
            }

            Invoke-AppsPhase
        }

        # FAZA 5: ZAVRŠETAK, VERIFIKACIJA, DIJAGNOSTIKA I ČIŠĆENJE
        if ($State.Phase -ge 5) {
            if (Get-Command Wait-ServisUICooperativePause -ErrorAction SilentlyContinue) {
                Wait-ServisUICooperativePause -CheckpointName "Faza 5 - Zavrsetak i Verifikacija"
            }
            if (Get-Command Update-ServisUI -ErrorAction SilentlyContinue) {
                Update-ServisUI -Phase 5 -SubPhase "Završetak i Verifikacija" -StatusMessage "Prikupljanje dijagnostike i servisnog izveštaja..." -ProgressPercent 90
            }

            # Tačka 2 (Completion Event & Servisni Karton)
            $totalSec = if ($Global:ScriptStartTime) { [int]((Get-Date) - $Global:ScriptStartTime).TotalSeconds } else { 0 }
            $durationFmt = "{0}m {1:D2}s" -f [math]::Floor($totalSec / 60), ($totalSec % 60)

            $realApps = Get-RealAppStatuses
            $appsMap = @{}
            if ($realApps -and $realApps.Items) {
                foreach ($it in $realApps.Items) {
                    $appsMap[$it.Id] = $it.Status
                }
            }
            $appsData = @{
                Chrome  = if ($appsMap.ContainsKey("Chrome")) { $appsMap["Chrome"] } else { "Nepoznato" }
                WinRAR  = if ($appsMap.ContainsKey("WinRAR")) { $appsMap["WinRAR"] } else { "Nepoznato" }
                Office  = if ($appsMap.ContainsKey("Office")) { $appsMap["Office"] } else { "Nepoznato" }
                Acrobat = if ($appsMap.ContainsKey("Acrobat")) { $appsMap["Acrobat"] } else { "Nepoznato" }
            }

            $freeSpaceGB = 0
            try {
                $cDrive = Get-PSDrive -Name "C" -PSProvider FileSystem -ErrorAction SilentlyContinue
                if ($cDrive -and $cDrive.Free) {
                    $freeSpaceGB = [math]::Round(($cDrive.Free / 1GB), 1)
                } else {
                    $cVol = Get-CimInstance Win32_LogicalDisk -Filter "DeviceID='C:'" -ErrorAction SilentlyContinue
                    if ($cVol -and $cVol.FreeSpace) {
                        $freeSpaceGB = [math]::Round(($cVol.FreeSpace / 1GB), 1)
                    }
                }
            } catch {}

            $completeData = @{
                device_id          = if ($Global:FleetDeviceId) { $Global:FleetDeviceId } else { if ($Global:HwInfo) { $Global:HwInfo.SerialNumber } else { "N/A" } }
                serial_number      = if ($Global:HwInfo -and $Global:HwInfo.SerialNumber) { $Global:HwInfo.SerialNumber } else { "N/A" }
                duration_seconds   = $totalSec
                duration_formatted = $durationFmt
                apps               = $appsData
                system_summary     = @{
                    manufacturer = if ($Global:HwInfo) { $Global:HwInfo.Manufacturer } else { "Nepoznato" }
                    model        = if ($Global:HwInfo) { $Global:HwInfo.Model } else { "Nepoznato" }
                    cpu          = if ($Global:HwInfo) { $Global:HwInfo.CpuStr } else { "Nepoznato" }
                    ram          = if ($Global:HwInfo) { $Global:HwInfo.RamStr } else { "Nepoznato" }
                    disk         = if ($Global:HwInfo) { $Global:HwInfo.DiskStr } else { "Nepoznato" }
                    battery      = if ($Global:HwInfo) { $Global:HwInfo.BatteryStr } else { "Nepoznato" }
                    free_c_disk  = "$freeSpaceGB GB"
                }
                autopilot_status   = if ($Global:HwInfo) { $Global:HwInfo.AutopilotStatus } else { "Clean" }
            }

            $null = Send-ServisLifecycleEvent -EventType "COMPLETED" -Data $completeData -LocalSetupDir $LocalSetupDir
            Write-ServisLog "Poslat COMPLETION telemetrijski izveštaj ka ServisHub-u (Trajanje: $durationFmt)" -Level "SUCCESS" -Component "Telemetry"

            if (Get-Command Send-FleetFinalReport -ErrorAction SilentlyContinue) {
                Send-FleetFinalReport -Status "COMPLETED"
            }

            if (Get-Command Update-ServisUI -ErrorAction SilentlyContinue) {
                Update-ServisUI -Phase 5 -SubPhase "Završeno" -StatusMessage "Sve operacije uspešno završene!" -ProgressPercent 100
            }
            if (Get-Command Close-ServisUI -ErrorAction SilentlyContinue) {
                Close-ServisUI
            }

            Invoke-ServisCleanupAndFinalize -LocalSetupDir $LocalSetupDir -SystemLogDir $SystemLogDir -PublicDesktopLog $PublicDesktopLog -SourceRoot $SourceRoot
        }

        $pipelineRunning = $false
    } catch {
        $fatalErr = $_
        Write-ServisLog "KRITICAN PREKID U FAZI $($State.Phase): $($fatalErr.Exception.Message)" -Level "ERROR" -Component "CrashGuard"
        if (Get-Command Send-FleetFinalReport -ErrorAction SilentlyContinue) {
            Send-FleetFinalReport -Status "FAILED" -Errors @($fatalErr.Exception.Message)
        }
        $action = Show-ServisFatalErrorScreen -Phase $State.Phase -ErrorRecord $fatalErr
        if ($action -eq "Retry") {
            continue
        } elseif ($action -eq "Skip") {
            $State.Phase = [int]$State.Phase + 1
            Save-State
            continue
        } else {
            break
        }
    }
}

# Zavrsna fail-safe poruka i pauza ukoliko je aktivan konzolni mod
if (-not $Worker) {
    Write-Host ""
    Write-Host "================================================================================" -ForegroundColor Green
    Write-Host "   TEHNO KUTAK AUTO-DEPLOY I UPDATE TOOLKIT - INSTALACIJA JE ZAVRSENA" -ForegroundColor Green
    Write-Host "================================================================================" -ForegroundColor Green
    Write-Host "   Pritisnite bilo koji taster za zatvaranje ove konzole..." -ForegroundColor Yellow
    try {
        $null = $Host.UI.RawUI.ReadKey("NoEcho,IncludeKeyDown")
    } catch {
        pause
    }
}

$global:LASTEXITCODE = 0
exit 0
'''

def clean_module_code(code: str) -> str:
    """Uklanja <# .MODULE ... #> zaglavlje i Export-ModuleMember poziv."""
    # Ukloni gornji blok komentara
    code = re.sub(r'^\s*<#[\s\S]*?#>\s*', '', code)
    # Ukloni Export-ModuleMember liniju/blok
    code = re.sub(r'Export-ModuleMember[\s\S]*$', '', code)
    return code.strip()

def build():
    print("=" * 70)
    print("   GWADT BUILD ENGINE | Sastavljanje Install-ServisApps.ps1")
    print("=" * 70)

    modules_code = []
    for mod_name in MODULE_ORDER:
        mod_path = os.path.join(MODULES_DIR, mod_name)
        if not os.path.exists(mod_path):
            print(f"[GRESKA] Nedostaje modul: {mod_path}")
            sys.exit(1)
        
        with open(mod_path, "r", encoding="utf-8") as f:
            raw_content = f.read()
        
        cleaned = clean_module_code(raw_content)
        separator = f"\n# ==============================================================================\n# MODUL: {mod_name}\n# ==============================================================================\n"
        modules_code.append(separator + cleaned)
        print(f" [OK] Ucitan modul: {mod_name:<25} ({len(cleaned.splitlines()):>5} linija)")

    # Sastavi skriptu
    full_script = PREAMBLE + "\n" + "\n".join(modules_code) + "\n" + POST_MODULES + "\n"
    total_lines = len(full_script.splitlines())
    total_bytes = len(full_script.encode("utf-8"))
    script_hash = hashlib.md5(full_script.encode("utf-8")).hexdigest()

    print("-" * 70)
    print(f" Ukupno linija koda : {total_lines}")
    print(f" Velicina datoteke  : {total_bytes / 1024:.2f} KB ({total_bytes} bajtova)")
    print(f" MD5 kontrolni zbir : {script_hash}")
    print("-" * 70)

    # Generisanje i osvezavanje version.json manifesta (TASK-023)
    version_manifest_path = os.path.join(PROJECT_ROOT, "version.json")
    pipeline_version = "2.5.0"
    file_hashes = {}
    hasher = hashlib.sha256()

    # Heširanje modula
    for mod in MODULE_ORDER:
        mp = os.path.join(MODULES_DIR, mod)
        if os.path.exists(mp):
            with open(mp, "rb") as f:
                h = hashlib.sha256(f.read()).hexdigest()
                file_hashes[mod] = h
                hasher.update(h.encode("utf-8"))

    # Heširanje ključnih konfiguracionih datoteka
    key_configs = ["autounattend.xml", "FirstLogon.cmd", "FirstLogon.ps1", "Pokreni-Instalaciju.bat", "Wifi-Servis.ini"]
    for kc in key_configs:
        kcp = os.path.join(PROJECT_ROOT, kc)
        if os.path.exists(kcp):
            with open(kcp, "rb") as f:
                h = hashlib.sha256(f.read()).hexdigest()
                file_hashes[kc] = h
                hasher.update(h.encode("utf-8"))

    pipeline_hash = hasher.hexdigest()
    now_str = datetime.now().strftime("%Y-%m-%d %H:%M:%S")

    version_data = {
        "pipeline_version": pipeline_version,
        "pipeline_hash": pipeline_hash,
        "build_timestamp": now_str,
        "os_build_target": "10.0.26200.8037",
        "modules_count": len(MODULE_ORDER),
        "files": file_hashes
    }

    import json
    with open(version_manifest_path, "w", encoding="utf-8") as vf:
        json.dump(version_data, vf, indent=2, ensure_ascii=False)
    print(f" [VERSION MANIFEST] -> {version_manifest_path} (Hash: {pipeline_hash[:16]}...)")

    # Distribucija na sve ciljne lokacije
    for target in TARGET_FILES:
        target_dir = os.path.dirname(target)
        if not os.path.exists(target_dir):
            os.makedirs(target_dir, exist_ok=True)
        with open(target, "w", encoding="utf-8", newline="\r\n") as f:
            f.write(full_script)
        print(f" [SINHRONIZOVANO] -> {target}")

    # Provera montiranog USB flesa (/run/media/*/*, /mnt/* ili preko --usb)
    detected_usb = []
    if len(sys.argv) > 1 and sys.argv[1].startswith("--usb="):
        detected_usb.append(sys.argv[1].split("=", 1)[1])
    elif len(sys.argv) > 2 and sys.argv[1] == "--usb":
        detected_usb.append(sys.argv[2])
    else:
        # Automatska detekcija
        candidate_roots = ["/mnt", f"/run/media/{os.environ.get('USER', 'gruya')}", f"/run/media/{os.environ.get('USER', 'gru')}"]
        for cr in candidate_roots:
            if os.path.exists(cr):
                if os.path.exists(os.path.join(cr, "sources")) and os.path.exists(os.path.join(cr, "Install-ServisApps.ps1")):
                    detected_usb.append(cr)
                else:
                    for item in os.listdir(cr):
                        p = os.path.join(cr, item)
                        if os.path.isdir(p) and (os.path.exists(os.path.join(p, "sources")) or os.path.exists(os.path.join(p, "Install-ServisApps.ps1"))):
                            detected_usb.append(p)

    for usb_mount in detected_usb:
        usb_targets = [
            os.path.join(usb_mount, "Install-ServisApps.ps1"),
            os.path.join(usb_mount, "Skripte", "Install-ServisApps.ps1"),
            os.path.join(usb_mount, "sources", "$OEM$", "$1", "setup", "Install-ServisApps.ps1")
        ]
        print(f" Detektovan montiran USB medijum na {usb_mount}. Sinhronizujem...")
        for ut in usb_targets:
            ut_dir = os.path.dirname(ut)
            if os.path.exists(ut_dir):
                with open(ut, "w", encoding="utf-8", newline="\r\n") as f:
                    f.write(full_script)
                print(f" [USB SINHRONIZOVANO] -> {ut}")
        # Sinhronizuj i version.json na USB
        usb_version_file = os.path.join(usb_mount, "version.json")
        try:
            with open(usb_version_file, "w", encoding="utf-8") as uvf:
                json.dump(version_data, uvf, indent=2, ensure_ascii=False)
            print(f" [USB VERSION] -> {usb_version_file}")
        except Exception as e:
            print(f" [UPOZORENJE] Neuspeh upisa version.json na USB: {e}")

        os.system("sync")
        print(" [SYNC] Baferi USB medijuma ispraznjeni.")

    print("=" * 70)
    print(f" [USPEH] Svi targeti i version.json (v{pipeline_version}) su 100% azurirani!")
    print("=" * 70)

if __name__ == "__main__":
    build()
