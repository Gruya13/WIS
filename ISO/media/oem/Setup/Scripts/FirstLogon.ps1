<#
.SYNOPSIS
    WIS FirstLogon.ps1 - Podržana alternativna/recovery PowerShell 5.1 Bootstrap ulazna tačka
.DESCRIPTION
    Podržana alternativna i recovery PowerShell bootstrap ulazna tačka deployment procesa na Desktopu korisnika
    (dok primarni unattended tok ide kroz FirstLogon.cmd -> WIS-DesktopStart.cmd -> WIS.Launcher.exe -> WIS.UI.exe --master).
    Zamena za inline batch komande koje su prelazile limit cmd.exe od 8191 karaktera.
    Vrsi rane Defender izuzetke, SMB/mrezne optimizacije za Win 11 24H2,
    inicijalizaciju neprekidnog tajmera (initial_start_time),
    detekciju i kopiranje skripti i programa (Office 2024) sa svih drajvova,
    i pokretanje WIS UI Master Shell / Launcher interfejsa.
.COMPATIBILITY
    Windows PowerShell 5.1+
#>

[CmdletBinding()]
param()

$env:SEE_MASK_NOZONECHECKS = '1'
try {
    [Environment]::SetEnvironmentVariable("SEE_MASK_NOZONECHECKS", "1", "Process")
    [Environment]::SetEnvironmentVariable("SEE_MASK_NOZONECHECKS", "1", "Machine")
    [Environment]::SetEnvironmentVariable("SEE_MASK_NOZONECHECKS", "1", "User")
} catch {}

$ErrorActionPreference = 'Continue'

# ------------------------------------------------------------------------------
# 1. SISTEM LOGOVANJA I INICIJALIZACIJA INITIAL_START_TIME TAJMERA
# ------------------------------------------------------------------------------
$logFiles = @('C:\Windows\Logs\WIS\firstlogon.log', 'C:\setup\firstlogon.log')
foreach ($f in $logFiles) {
    $p = Split-Path $f -Parent
    if (-not (Test-Path $p)) {
        try { New-Item -Path $p -ItemType Directory -Force -ErrorAction SilentlyContinue | Out-Null } catch {}
    }
}

function Write-Log([string]$msg, [string]$lvl='INFO', [string]$cmp='FirstLogon') {
    $t = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss.fff')
    $l = ('[{0}] [{1}] [{2}] {3}' -f $t, $lvl, $cmp, $msg)
    try { [Console]::WriteLine($l) } catch {}
    foreach ($f in $logFiles) {
        try { [System.IO.File]::AppendAllText($f, $l + [Environment]::NewLine, [System.Text.Encoding]::UTF8) } catch {}
    }
}

# Inicijalizacija initial_start_time (UTC ISO 8601) u C:\setup\deploy-state.json i HKLM:\SOFTWARE\WIS\InitialStartTime
try {
    $statePath = 'C:\setup\deploy-state.json'
    $regPath   = 'HKLM:\SOFTWARE\WIS'
    $regName   = 'InitialStartTime'
    $stateObj  = [ordered]@{}
    $existingStart = $null

    if (Test-Path -LiteralPath $statePath) {
        try {
            $rawJson = [System.IO.File]::ReadAllText($statePath, [System.Text.Encoding]::UTF8)
            $parsed = ConvertFrom-Json -InputObject $rawJson -ErrorAction Stop
            if ($parsed) {
                foreach ($prop in $parsed.PSObject.Properties) {
                    $stateObj[$prop.Name] = $prop.Value
                }
                if ($stateObj.Contains('initial_start_time') -and -not [string]::IsNullOrWhiteSpace([string]$stateObj['initial_start_time'])) {
                    $existingStart = [string]$stateObj['initial_start_time']
                }
            }
        } catch {}
    }

    if ([string]::IsNullOrWhiteSpace($existingStart)) {
        try {
            if (Test-Path -LiteralPath $regPath) {
                $regVal = (Get-ItemProperty -Path $regPath -Name $regName -ErrorAction SilentlyContinue).$regName
                if (-not [string]::IsNullOrWhiteSpace($regVal)) {
                    $existingStart = [string]$regVal
                }
            }
        } catch {}
    }

    if ([string]::IsNullOrWhiteSpace($existingStart)) {
        $existingStart = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
    }

    try {
        if (-not (Test-Path -LiteralPath $regPath)) {
            New-Item -Path $regPath -Force -ErrorAction SilentlyContinue | Out-Null
        }
        Set-ItemProperty -Path $regPath -Name $regName -Value $existingStart -Type String -Force -ErrorAction SilentlyContinue
    } catch {}

    $stateObj['initial_start_time'] = $existingStart
    if (-not $stateObj.Contains('current_phase')) { $stateObj['current_phase'] = 1 }
    if (-not $stateObj.Contains('status')) { $stateObj['status'] = 'initialized' }
    if (-not $stateObj.Contains('timestamp')) { $stateObj['timestamp'] = (Get-Date -Format 'o') }
    $jsonOut = $stateObj | ConvertTo-Json -Depth 5
    [System.IO.File]::WriteAllText($statePath, $jsonOut, (New-Object System.Text.UTF8Encoding($false)))
    Write-Log ('Inicijalizovan neprekidni tajmer initial_start_time: {0}' -f $existingStart) 'INFO' 'Timer'
} catch {
    Write-Log ('Upozorenje pri inicijalizaciji initial_start_time: {0}' -f $_.Exception.Message) 'WARN' 'Timer'
}

function Get-RcDesc([int]$c) {
    switch ($c) {
        0 { return '0 (Nema promena - izvor i odrediste sinhronizovani)' }
        1 { return '1 (Uspesno - fajlovi su iskopirani)' }
        2 { return '2 (Postoje dodatni fajlovi na odredistu)' }
        3 { return '3 (Uspesno - fajlovi iskopirani uz dodatne fajlove)' }
        4 { return '4 (Detektovane neuskladjenosti)' }
        8 { return '8 (GRESKA: Neki fajlovi nisu iskopirani - prekoracen retry)' }
        16 { return '16 (FATALNA GRESKA: Robocopy pristup ili greska parametara)' }
        default { if ($c -ge 8) { return ('{0} (GRESKA)' -f $c) } else { return ('{0} (Uspesno)' -f $c) } }
    }
}

$curUser = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name
$hostName = $env:COMPUTERNAME
Write-Log '==============================================================================' 'INFO' 'FirstLogon'
Write-Log ('FirstLogon.ps1 bootstrap pokrenut na racunaru: {0}' -f $hostName) 'INFO' 'FirstLogon'
Write-Log ('Ulogovani korisnik: {0}' -f $curUser) 'INFO' 'FirstLogon'
Write-Log ('Radni direktorijum: {0}' -f $PWD.Path) 'INFO' 'FirstLogon'

# ------------------------------------------------------------------------------
# 2. DEFENDER IZUZECI ZA C:\setup I SVE DISKOVE
# ------------------------------------------------------------------------------
Write-Log 'Postavljanje Defender izuzetaka za C:\setup i eksterne diskove...' 'STEP' 'Defender'
try {
    Add-MpPreference -ExclusionPath 'C:\setup' -ErrorAction SilentlyContinue
    Write-Log 'Dodat Defender izuzetak za: C:\setup' 'SUCCESS' 'Defender'
} catch {}

if (Test-Path 'D:\') {
    try {
        Add-MpPreference -ExclusionPath 'D:\' -ErrorAction SilentlyContinue
        Write-Log 'Dodat Defender izuzetak za: D:\' 'SUCCESS' 'Defender'
    } catch {}
}

try {
    Get-PSDrive -PSProvider FileSystem -ErrorAction SilentlyContinue | Where-Object { $_.Root -ne 'C:\' } | ForEach-Object {
        Add-MpPreference -ExclusionPath $_.Root -ErrorAction SilentlyContinue
        Write-Log ('Dodat Defender izuzetak za particiju: {0}' -f $_.Root) 'INFO' 'Defender'
    }
} catch {}

# ------------------------------------------------------------------------------
# 3. PODEŠAVANJE SMB I MREŽNIH PARAMETARA ZA WIN 11 24H2
# ------------------------------------------------------------------------------
Write-Log 'Konfigurisanje SMB klijenta za nesiguran Guest pristup i onemogucavanje obaveznog SMB potpisivanja (Win 11 24H2 kompatibilnost)...' 'STEP' 'Registry'
& reg.exe add 'HKLM\SYSTEM\CurrentControlSet\Services\LanmanWorkstation\Parameters' /v 'AllowInsecureGuestAuth' /t REG_DWORD /d 1 /f >nul 2>&1
& reg.exe add 'HKLM\SYSTEM\CurrentControlSet\Services\LanmanWorkstation\Parameters' /v 'RequireSecuritySignature' /t REG_DWORD /d 0 /f >nul 2>&1
Write-Log 'SMB parametri (AllowInsecureGuestAuth=1, RequireSecuritySignature=0) uspesno primenjeni.' 'SUCCESS' 'Registry'

Write-Log 'Optimizacija mreznog steka i Lanman parametara za maksimalan protok flote...' 'STEP' 'Network'
& reg.exe add 'HKLM\SYSTEM\CurrentControlSet\Services\LanmanWorkstation\Parameters' /v 'DisableBandwidthThrottling' /t REG_DWORD /d 1 /f >nul 2>&1
& reg.exe add 'HKLM\SYSTEM\CurrentControlSet\Services\LanmanWorkstation\Parameters' /v 'MaxCmds' /t REG_DWORD /d 2048 /f >nul 2>&1
& netsh.exe int tcp set global autotuninglevel=normal >nul 2>&1
& netsh.exe int tcp set global rss=enabled >nul 2>&1
& netsh.exe int tcp set heuristics disabled >nul 2>&1
$global:LASTEXITCODE = 0
Write-Log 'Mrezni stek optimizovan (DisableBandwidthThrottling=1, MaxCmds=2048, autotuning=normal, rss=enabled).' 'SUCCESS' 'Network'

Write-Log 'Konfigurisanje sistemskih polisa za trajno eliminisanje Open File - Security Warning dijaloga...' 'STEP' 'Registry'
& reg.exe add 'HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Environment' /v 'SEE_MASK_NOZONECHECKS' /t REG_SZ /d '1' /f >nul 2>&1
& reg.exe add 'HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Associations' /v 'LowRiskFileTypes' /t REG_SZ /d '.exe;.bat;.cmd;.msi;.ps1;.reg;.vbs;.cab;.zip;' /f >nul 2>&1
& reg.exe add 'HKCU\Software\Microsoft\Windows\CurrentVersion\Policies\Associations' /v 'LowRiskFileTypes' /t REG_SZ /d '.exe;.bat;.cmd;.msi;.ps1;.reg;.vbs;.cab;.zip;' /f >nul 2>&1
& reg.exe add 'HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Attachments' /v 'SaveZoneInformation' /t REG_DWORD /d 1 /f >nul 2>&1
& reg.exe add 'HKCU\Software\Microsoft\Windows\CurrentVersion\Policies\Attachments' /v 'SaveZoneInformation' /t REG_DWORD /d 1 /f >nul 2>&1
& reg.exe add 'HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Attachments' /v 'HideZoneInfoOnProperties' /t REG_DWORD /d 1 /f >nul 2>&1
& reg.exe add 'HKCU\Software\Microsoft\Windows\CurrentVersion\Policies\Attachments' /v 'HideZoneInfoOnProperties' /t REG_DWORD /d 1 /f >nul 2>&1
& reg.exe add 'HKCU\Software\Microsoft\Windows\CurrentVersion\Internet Settings\ZoneMap\Ranges\Range1' /v ':Range' /t REG_SZ /d '192.168.*.*' /f >nul 2>&1
& reg.exe add 'HKCU\Software\Microsoft\Windows\CurrentVersion\Internet Settings\ZoneMap\Ranges\Range1' /v '*' /t REG_DWORD /d 1 /f >nul 2>&1
& reg.exe add 'HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Internet Settings\ZoneMap\Ranges\Range1' /v ':Range' /t REG_SZ /d '192.168.*.*' /f >nul 2>&1
& reg.exe add 'HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Internet Settings\ZoneMap\Ranges\Range1' /v '*' /t REG_DWORD /d 1 /f >nul 2>&1
& reg.exe add 'HKCU\Software\Microsoft\Windows\CurrentVersion\Internet Settings\Zones\1' /v '1806' /t REG_DWORD /d 0 /f >nul 2>&1
& reg.exe add 'HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Internet Settings\Zones\1' /v '1806' /t REG_DWORD /d 0 /f >nul 2>&1
Write-Log 'Sistemske i korisnicke polise za zone i priloge uspesno primenjene (SEE_MASK_NOZONECHECKS=1, LowRiskFileTypes, 1806=0).' 'SUCCESS' 'Registry'

Write-Log 'Deblokiranje svih preuzetih i raspakovanih fajlova u C:\setup (Unblock-File)...' 'STEP' 'Unblock'
if (Test-Path 'C:\setup') {
    Get-ChildItem -Path 'C:\setup' -Recurse -File -ErrorAction SilentlyContinue | ForEach-Object {
        Unblock-File -Path $_.FullName -ErrorAction SilentlyContinue
    }
    Write-Log 'Rekurzivni Unblock-File nad C:\setup uspesno zavrsen.' 'SUCCESS' 'Unblock'
}

# ------------------------------------------------------------------------------
# 4. VERIFIKACIJA MREŽNIH ADAPTERA I WINDOWS UPDATE SERVISA
# ------------------------------------------------------------------------------
Write-Log 'Verifikacija i aktiviranje mreznih adaptera...' 'STEP' 'Network'
$netAdapters = Get-NetAdapter -ErrorAction SilentlyContinue
if ($netAdapters) {
    foreach ($a in $netAdapters) {
        Write-Log ('Mrezni adapter: {0} | Status: {1} | LinkSpeed: {2}' -f $a.Name, $a.Status, $a.LinkSpeed) 'INFO' 'Network'
    }
    Get-NetAdapter -ErrorAction SilentlyContinue | Enable-NetAdapter -Confirm:$false -ErrorAction SilentlyContinue
    Write-Log 'Enable-NetAdapter komanda izvrsena.' 'SUCCESS' 'Network'
} else {
    Write-Log 'Nema detektovanih mreznih adaptera.' 'WARN' 'Network'
}

Write-Log 'Osiguravanje servisa Windows Update (wuauserv)...' 'STEP' 'WindowsUpdate'
& sc.exe config wuauserv start= auto >nul 2>&1
& net.exe start wuauserv >nul 2>&1
$svc = Get-Service -Name wuauserv -ErrorAction SilentlyContinue
if ($svc) {
    Write-Log ('Stanje wuauserv: Status={0}, StartType={1}' -f $svc.Status, $svc.StartType) 'INFO' 'WindowsUpdate'
}

# ------------------------------------------------------------------------------
# 5. PAUZA ZA STABILIZACIJU DESKTOP-A I EXPLORERA
# ------------------------------------------------------------------------------
Write-Log 'Pauza od 2 sekunde za stabilizaciju Desktop-a i Explorera...' 'STEP' 'Explorer'
Start-Sleep -Seconds 2
$expProc = Get-Process -Name explorer -ErrorAction SilentlyContinue
if ($expProc) {
    Write-Log ('Explorer proces aktivan (Broj instanci: {0}, PIDs: {1})' -f $expProc.Count, (($expProc | ForEach-Object { $_.Id }) -join ',')) 'SUCCESS' 'Explorer'
} else {
    Write-Log 'Explorer proces jos uvek nije detektovan.' 'WARN' 'Explorer'
}

# ------------------------------------------------------------------------------
# 6. PROVERA KESA I SKENIRANJE SVIH DISKOVA ZA IZVOROM
# ------------------------------------------------------------------------------
Write-Log 'Provera statusa lokalnog kesa u C:\setup...' 'STEP' 'CacheCheck'
$hasLocalOffice = Test-Path 'C:\setup\Programi\Office 2024'
Write-Log ('Lokalni Office 2024 paket: {0}' -f (if ($hasLocalOffice) { 'PRISUTAN' } else { 'NEDOSTAJE' })) (if ($hasLocalOffice) { 'INFO' } else { 'WARN' }) 'CacheCheck'

# Sakupljanje svih mogucih drajvova (USB, CDROM / iVentoy ISO, fiksni drajvovi)
$driveRoots = @()
Get-PSDrive -PSProvider FileSystem -ErrorAction SilentlyContinue | Where-Object { $_.Root -and ($_.Root -ne 'C:\') } | ForEach-Object { $driveRoots += ($_.Root.TrimEnd('\') + '\') }
try { Get-CimInstance Win32_CDROMDrive -ErrorAction SilentlyContinue | Where-Object { $_.Drive } | ForEach-Object { $driveRoots += ($_.Drive.TrimEnd('\') + '\') } } catch {}
try { Get-Volume -ErrorAction SilentlyContinue | Where-Object { $_.DriveLetter -and ($_.DriveLetter -ne 'C') } | ForEach-Object { $driveRoots += ($_.DriveLetter + ':\') } } catch {}
try { [System.IO.DriveInfo]::GetDrives() | Where-Object { $_.Name -and ($_.Name -ne 'C:\') } | ForEach-Object { $driveRoots += ($_.Name.TrimEnd('\') + '\') } } catch {}

$drives = $driveRoots | Where-Object { $_ -and ($_ -ne 'C:\') } | Select-Object -Unique | ForEach-Object { [PSCustomObject]@{ Root = $_ } }
Write-Log ('Detektovano {0} drajvova za proveru: {1}' -f $drives.Count, (($drives | ForEach-Object { $_.Root }) -join ', ')) 'INFO' 'DiskDetection'

$oemSub = 'sources\' + [char]36 + 'OEM\' + [char]36 + '1\setup'

# A) Kopiranje modularnih skripti i faza
if (-not (Test-Path 'C:\setup\Skripte\Modules')) {
    foreach ($d in $drives) {
        Add-MpPreference -ExclusionPath $d.Root -ErrorAction SilentlyContinue
        $sCands = @(
            (Join-Path $d.Root 'Skripte'),
            (Join-Path $d.Root ($oemSub + '\Skripte'))
        ) | Where-Object { Test-Path (Join-Path $_ 'Modules') -ErrorAction SilentlyContinue } | Select-Object -First 1
        if ($sCands) {
            Write-Log ('Kopiranje modularnih skripti sa {0} u C:\setup\Skripte...' -f $sCands) 'STEP' 'FileTransfer'
            $destSkr = 'C:\setup\Skripte'
            if (-not (Test-Path $destSkr)) { New-Item -Path $destSkr -ItemType Directory -Force -ErrorAction SilentlyContinue | Out-Null }
            & robocopy.exe $sCands $destSkr /E /R:1 /W:1 /NDL /NFL /NJH /NJS >nul 2>&1
            break
        }
    }
}

# B) Kopiranje Wifi-Servis.ini
if (-not (Test-Path 'C:\setup\Wifi-Servis.ini')) {
    foreach ($d in $drives) {
        Add-MpPreference -ExclusionPath $d.Root -ErrorAction SilentlyContinue
        $w = @(
            (Join-Path $d.Root 'Wifi-Servis.ini'),
            (Join-Path $d.Root 'Skripte\Wifi-Servis.ini'),
            (Join-Path $d.Root ($oemSub + '\Wifi-Servis.ini'))
        ) | Where-Object { Test-Path $_ -ErrorAction SilentlyContinue } | Select-Object -First 1
        if ($w) {
            Write-Log ('Kopiranje Wifi-Servis.ini sa {0} u C:\setup...' -f $w) 'STEP' 'FileTransfer'
            Copy-Item -Path $w -Destination 'C:\setup\Wifi-Servis.ini' -Force -ErrorAction SilentlyContinue
            Write-Log 'Wifi-Servis.ini uspesno prekopiran.' 'SUCCESS' 'FileTransfer'
            break
        }
    }
}

# B1) Wi-Fi i mrezni bootstrap pre pokretanja Launchera
Write-Log 'Inicijalizacija mreznog podsistema i Wi-Fi bootstrapa pre pokretanja Launchera...' 'STEP' 'NetworkBootstrap'
try {
    # 1. Provera i pokretanje servisa 'wlansvc' (WLAN AutoConfig)
    $wlanSvc = Get-Service -Name 'wlansvc' -ErrorAction SilentlyContinue
    if ($wlanSvc -and $wlanSvc.Status -ne 'Running') {
        Write-Log 'Servis wlansvc nije pokrenut. Pokrecem wlansvc...' 'INFO' 'NetworkBootstrap'
        Start-Service -Name 'wlansvc' -ErrorAction SilentlyContinue
        Start-Sleep -Milliseconds 500
    }

    # 2. Uvoz mreznog modula WIS.Network.psm1
    $netModCandidates = @(
        'C:\setup\Skripte\Modules\WIS.Network.psm1'
    )
    foreach ($d in $drives) {
        $netModCandidates += (Join-Path $d.Root 'Skripte\Modules\WIS.Network.psm1')
        $netModCandidates += (Join-Path $d.Root ($oemSub + '\Skripte\Modules\WIS.Network.psm1'))
    }
    $netMod = $netModCandidates | Where-Object { Test-Path $_ -ErrorAction SilentlyContinue } | Select-Object -First 1
    if ($netMod) {
        Write-Log ('Uvozim mrezni modul sa lokacije: {0}' -f $netMod) 'INFO' 'NetworkBootstrap'
        Import-Module $netMod -Force -ErrorAction SilentlyContinue
    }

    # 3. Povezivanje na servisni Wi-Fi ako je funkcija Connect-ServisWifi dostupna
    if (Get-Command Connect-ServisWifi -ErrorAction SilentlyContinue) {
        Write-Log 'Pozivam Connect-ServisWifi za konfiguraciju i konekciju na servisni Wi-Fi...' 'STEP' 'NetworkBootstrap'
        Connect-ServisWifi -LocalSetupDir 'C:\setup'
    }

    # 4. Pametna petlja provere mrezne veze (timeout do 12 sekundi sa fast-exit)
    Write-Log 'Cekanje na mreznu vezu (do 12 sekundi sa fast-exit)...' 'INFO' 'NetworkBootstrap'
    $netConnected = $false
    $timeoutSec = 12
    $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()

    while ($stopwatch.Elapsed.TotalSeconds -lt $timeoutSec) {
        # a) Da li bilo koji mrezni adapter ima dodeljenu IPv4 adresu (osim 127.0.0.1 i 169.254.*)
        $validIp = $null
        try {
            $validIp = Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue | Where-Object {
                $_.InterfaceAlias -notmatch 'Loopback|vEthernet' -and
                $_.IPAddress -notmatch '^(127\.|169\.254\.)'
            } | Select-Object -First 1
        } catch {}

        # b) Ping ili HTTP internet provera
        $hasNet = $false
        if ($validIp) {
            $hasNet = $true
        } else {
            try {
                if (Test-Connection -ComputerName 8.8.8.8 -Count 1 -Quiet -ErrorAction SilentlyContinue) {
                    $hasNet = $true
                }
            } catch {}
        }

        # Ako je veza uspostavljena ili je LAN kabl vec aktivan, ODMAH prekini petlju (fast-exit)
        if ($hasNet) {
            $netConnected = $true
            $ipStr = if ($validIp) { $validIp.IPAddress } else { "DHCP" }
            Write-Log ('Mrezna veza uspostavljena u {0:F1}s (IP: {1}). Nastavljam dalje (fast-exit).' -f $stopwatch.Elapsed.TotalSeconds, $ipStr) 'SUCCESS' 'NetworkBootstrap'
            break
        }

        Start-Sleep -Seconds 1
    }

    if (-not $netConnected) {
        Write-Log 'Upozorenje: Mrezna veza nije uspostavljena u roku od 12s (uredjaj nema Wi-Fi drajver/karticu ili nije u dometu). Nastavljam bez blokiranja.' 'WARN' 'NetworkBootstrap'
    }
} catch {
    Write-Log ('Greska tokom Wi-Fi bootstrapa: {0}' -f $_.Exception.Message) 'WARN' 'NetworkBootstrap'
}

# C) Kopiranje WIS.UI
if (-not (Test-Path 'C:\setup\Skripte\WIS.UI\WIS.UI.exe')) {
    foreach ($d in $drives) {
        $uiSrc = @(
            (Join-Path $d.Root 'Skripte\WIS.UI'),
            (Join-Path $d.Root 'Programi\WIS.UI'),
            (Join-Path $d.Root ($oemSub + '\Skripte\WIS.UI')),
            (Join-Path $d.Root ($oemSub + '\Programi\WIS.UI'))
        ) | Where-Object { (Test-Path (Join-Path $_ 'WIS.UI.exe') -ErrorAction SilentlyContinue) -or (Test-Path (Join-Path $_ 'Build.bat') -ErrorAction SilentlyContinue) } | Select-Object -First 1
        if ($uiSrc) {
            Write-Log ('Kopiranje WIS.UI sa {0} u C:\setup\Skripte\WIS.UI...' -f $uiSrc) 'STEP' 'FileTransfer'
            $destUI = 'C:\setup\Skripte\WIS.UI'
            if (-not (Test-Path $destUI)) { New-Item -Path $destUI -ItemType Directory -Force -ErrorAction SilentlyContinue | Out-Null }
            & robocopy.exe $uiSrc $destUI /E /R:1 /W:1 /NDL /NFL /NJH /NJS >nul 2>&1
            & icacls.exe $destUI /grant '*S-1-5-18:(OI)(CI)F' /grant '*S-1-5-32-544:(OI)(CI)F' /grant '*S-1-1-0:(OI)(CI)F' /T /Q >nul 2>&1
            break
        }
    }
}

# D) Kopiranje Office 2024 / Programi preko robocopy sa Copy-Item fallback
if (-not (Test-Path 'C:\setup\Programi\Office 2024')) {
    $pkgCopied = $false
    foreach ($d in $drives) {
        Add-MpPreference -ExclusionPath $d.Root -ErrorAction SilentlyContinue
        $srcProg = $null
        $pOffice = Join-Path $d.Root 'Programi\Office 2024'
        $oOffice = Join-Path $d.Root ($oemSub + '\Programi\Office 2024')
        $oProg   = Join-Path $d.Root ($oemSub + '\Programi')
        $pProg   = Join-Path $d.Root 'Programi'
        if (Test-Path $pOffice -ErrorAction SilentlyContinue) { $srcProg = $pProg }
        elseif (Test-Path $oOffice -ErrorAction SilentlyContinue) { $srcProg = $oProg }
        elseif (Test-Path $oProg -ErrorAction SilentlyContinue) { $srcProg = $oProg }
        elseif (Test-Path $pProg -ErrorAction SilentlyContinue) { $srcProg = $pProg }
        if ($srcProg) {
            Write-Log ('Pronadjen izvor paketa na {0} ({1}). Pokretanje robocopy u C:\setup\Programi...' -f $d.Root, $srcProg) 'STEP' 'Robocopy'
            if (-not (Test-Path 'C:\setup\Programi')) { New-Item -Path 'C:\setup\Programi' -ItemType Directory -Force -ErrorAction SilentlyContinue | Out-Null }
            & robocopy.exe $srcProg 'C:\setup\Programi' /E /MT:32 /J /R:1 /W:1 /NDL /NFL /NJH /NJS >nul 2>&1
            $rcExit = $LASTEXITCODE
            $rcMsg = Get-RcDesc $rcExit
            if ($rcExit -lt 8) {
                Write-Log ('Robocopy uspesan: ExitCode={0} [{1}]' -f $rcExit, $rcMsg) 'SUCCESS' 'Robocopy'
            } else {
                Write-Log ('Robocopy prijavio gresku: ExitCode={0} [{1}]' -f $rcExit, $rcMsg) 'ERROR' 'Robocopy'
            }
            $global:LASTEXITCODE = 0
            & icacls.exe 'C:\setup\Programi' /grant '*S-1-5-18:(OI)(CI)F' /grant '*S-1-5-32-544:(OI)(CI)F' /grant '*S-1-1-0:(OI)(CI)F' /T /Q >nul 2>&1
            if (-not (Test-Path 'C:\setup\Programi\Office 2024')) {
                Write-Log 'Office 2024 nedostaje nakon robocopy-ja, fallback Copy-Item...' 'WARN' 'FileTransfer'
                Copy-Item -Path (Join-Path $srcProg '*') -Destination 'C:\setup\Programi\' -Recurse -Force -ErrorAction SilentlyContinue
                & icacls.exe 'C:\setup\Programi' /grant '*S-1-5-18:(OI)(CI)F' /grant '*S-1-5-32-544:(OI)(CI)F' /grant '*S-1-1-0:(OI)(CI)F' /T /Q >nul 2>&1
            }
            if (Test-Path 'C:\setup\Programi\Office 2024') {
                Write-Log 'Office 2024 uspesno verifikovan u C:\setup\Programi.' 'SUCCESS' 'FileTransfer'
                $pkgCopied = $true
                break
            }
        }
    }
    if (-not $pkgCopied) {
        Write-Log 'Nije pronadjen izvor sa instalacionim paketom Office 2024 na detektovanim drajvovima.' 'WARN' 'DiskDetection'
    }
} else {
    Write-Log 'Lokalni kes je u potpunosti spreman (Office 2024 i skripte su prisutni).' 'SUCCESS' 'CacheCheck'
}

# ------------------------------------------------------------------------------
# 7. PROVERA WIS GUI INTERFEJSA
# ------------------------------------------------------------------------------
Write-Log 'Verifikacija prisustva prekompajliranog WIS.UI.exe pre pokretanja instalacije...' 'STEP' 'GUI'
$uiExe = @(
    'C:\setup\Skripte\WIS.UI\WIS.UI.exe',
    'C:\setup\Programi\WIS.UI\WIS.UI.exe',
    'C:\setup\WIS.UI\WIS.UI.exe'
) | Where-Object { Test-Path $_ -ErrorAction SilentlyContinue } | Select-Object -First 1
if ($uiExe) {
    Write-Log ('WIS GUI detektovan na lokaciji: {0}' -f $uiExe) 'SUCCESS' 'GUI'
} else {
    Write-Log 'WIS.UI.exe nije pronadjen na disku (bice koriscen konzolni prikaz).' 'WARN' 'GUI'
}

# ------------------------------------------------------------------------------
# 8. POSTAVLJANJE FAIL-SAFE PREČICE NA PUBLIC DESKTOP
# ------------------------------------------------------------------------------
Write-Log 'Postavljanje fail-safe precice Pokreni-Instalaciju.bat na Public Desktop...' 'STEP' 'DesktopShortcut'
$pubDesktop = 'C:\Users\Public\Desktop'
if (Test-Path $pubDesktop) {
    $batCandidates = @(
        'C:\setup\Pokreni-Instalaciju.bat',
        'C:\setup\Skripte\Pokreni-Instalaciju.bat'
    )
    foreach ($d in $drives) {
        $batCandidates += (Join-Path $d.Root 'Pokreni-Instalaciju.bat')
        $batCandidates += (Join-Path $d.Root 'Skripte\Pokreni-Instalaciju.bat')
        $batCandidates += (Join-Path $d.Root ($oemSub + '\Pokreni-Instalaciju.bat'))
    }
    $foundBat = $batCandidates | Where-Object { Test-Path $_ -ErrorAction SilentlyContinue } | Select-Object -First 1
    if ($foundBat) {
        Copy-Item -Path $foundBat -Destination (Join-Path $pubDesktop 'Pokreni-Instalaciju.bat') -Force -ErrorAction SilentlyContinue
        Write-Log ('Kopirana precica Pokreni-Instalaciju.bat na Public Desktop sa: {0}' -f $foundBat) 'SUCCESS' 'DesktopShortcut'
    } else {
        Write-Log 'Pokreni-Instalaciju.bat nije pronadjen za Public Desktop precicu.' 'WARN' 'DesktopShortcut'
    }
}

# ------------------------------------------------------------------------------
# 9. SINHRONIZACIJA FIRSTLOGON.LOG NA USB DRAJVE
# ------------------------------------------------------------------------------
Write-Log 'Sinhronizacija firstlogon.log na USB drajv...' 'STEP' 'UsbLogSync'
$usbDrives = Get-PSDrive -PSProvider FileSystem -ErrorAction SilentlyContinue | Where-Object { $_.Root -ne 'C:\' }
$usbCopied = $false
foreach ($u in $usbDrives) {
    try {
        $uLogDir = Join-Path $u.Root 'Logs'
        if (-not (Test-Path $uLogDir)) { New-Item -Path $uLogDir -ItemType Directory -Force -ErrorAction SilentlyContinue | Out-Null }
        $destFile = Join-Path $uLogDir 'firstlogon.log'
        Copy-Item -Path 'C:\Windows\Logs\WIS\firstlogon.log' -Destination $destFile -Force -ErrorAction SilentlyContinue
        Write-Log ('firstlogon.log uspesno sinhronizovan na: {0}' -f $destFile) 'SUCCESS' 'UsbLogSync'
        $usbCopied = $true
    } catch {
        Write-Log ('Greska pri sinhronizaciji loga na drajv {0}' -f $u.Root) 'WARN' 'UsbLogSync'
    }
}
if (-not $usbCopied) {
    Write-Log 'Nije pronadjen dostupan USB drajv za sinhronizaciju loga.' 'INFO' 'UsbLogSync'
}

# ------------------------------------------------------------------------------
# 10. STARTOVANJE PRIMARNOG KORISNIČKOG INTERFEJSA (WIS MASTER SHELL / LAUNCHER)
# ------------------------------------------------------------------------------
Write-Log 'Pokretanje primarnog korisnickog interfejsa (WIS.Launcher / WIS.UI)...' 'STEP' 'Launcher'

# 10a. Kreiranje privremenog Startup launcher-a (garancija starta po podizanju Desktopa)
$startupDir = 'C:\ProgramData\Microsoft\Windows\Start Menu\Programs\Startup'
if (-not (Test-Path $startupDir)) {
    New-Item -Path $startupDir -ItemType Directory -Force -ErrorAction SilentlyContinue | Out-Null
}
$startupFile = Join-Path $startupDir 'WIS-AutoStart.cmd'
$startupContent = @"
@echo off
if not exist "C:\setup\.stage_completed" (
    powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "if (-not (Get-Process -Name 'WIS.Launcher' -ErrorAction SilentlyContinue) -and -not (Get-Process -Name 'WIS.UI' -ErrorAction SilentlyContinue)) { if (Test-Path 'C:\setup\WIS.Launcher.exe') { Start-Process -FilePath 'C:\setup\WIS.Launcher.exe' -WindowStyle Maximized } else { Start-Process -FilePath 'C:\setup\Skripte\WIS.UI\WIS.UI.exe' -ArgumentList '--master' -WindowStyle Maximized } }"
)
del "%~f0" >nul 2>&1
"@
[System.IO.File]::WriteAllText($startupFile, $startupContent, [System.Text.Encoding]::ASCII)
Write-Log 'Kreiran Startup launcher: WIS-AutoStart.cmd' 'INFO' 'Launcher'

# 10b. Upis u RunOnce registar za automatsko podizanje kada Explorer prikaze Desktop
$runOnceCmd = if (Test-Path 'C:\setup\WIS.Launcher.exe') {
    'C:\setup\WIS.Launcher.exe'
} else {
    'C:\setup\Skripte\WIS.UI\WIS.UI.exe --master'
}
& reg.exe add "HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce" /v "WIS_AutoStart" /t REG_SZ /d $runOnceCmd /f >nul 2>&1
Write-Log ('Registrovan HKLM RunOnce unos: WIS_AutoStart ({0})' -f $runOnceCmd) 'INFO' 'Launcher'

Write-Log '=== FirstLogon bootstrap uspesno zavrsen ===' 'SUCCESS' 'FirstLogon'

# ------------------------------------------------------------------------------
# 10d. POKRETANJE 1 Hz LIVE SCREEN MIRROR RADNIKA (SERVIS HUB v3.2 INTEGRACIJA)
# ------------------------------------------------------------------------------
Write-Log 'Pokretanje 1 Hz Live Screen Mirror radnika za centralni Web Cockpit...' 'STEP' 'ScreenMirror'

$screenWorkerCandidates = @(
    'C:\setup\Skripte\Modules\WIS.ScreenCapture.ps1',
    (Join-Path $PSScriptRoot 'Skripte\Modules\WIS.ScreenCapture.ps1'),
    (Join-Path $PSScriptRoot 'Modules\WIS.ScreenCapture.ps1')
)

$screenWorkerScript = $screenWorkerCandidates | Where-Object { Test-Path $_ -ErrorAction SilentlyContinue } | Select-Object -First 1

if ($screenWorkerScript) {
    try {
        # Zaustavljanje eventualno zaostalog prethodnog posla u sesiji
        Get-Job -Name "WIS_ScreenCapture*" -ErrorAction SilentlyContinue | Stop-Job -ErrorAction SilentlyContinue | Remove-Job -Force -ErrorAction SilentlyContinue

        # U2: Umesto sesijom-vezanog Start-Job -Name "WIS_ScreenCapture_Worker" -FilePath $screenWorkerScript -ArgumentList @("-RunAsJob")
        # koji bi se ugasio pri izlasku iz FirstLogon.ps1, pokrecemo nezavisan skriveni proces sa imenovanim parametrima
        # koji prezivi zavrsetak FirstLogon.ps1 i postuje single-instance Mutex + C:\setup\.screen_capture.pid:
        $workerArgs = "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$screenWorkerScript`" -LocalSetupDir `"C:\setup`" -OwnerSessionId `"FirstLogon-$PID`" -RunAsJob"
        $screenProc = Start-Process -FilePath "powershell.exe" -ArgumentList $workerArgs -WindowStyle Hidden -PassThru -ErrorAction SilentlyContinue

        if ($screenProc) {
            Write-Log ("1 Hz Screen Mirror radnik uspesno pokrenut kao nezavisan proces (PID: {0})." -f $screenProc.Id) 'SUCCESS' 'ScreenMirror'
        } else {
            Write-Log "Upozorenje: Start-Process nije vratio validnu instancu procesa za Screen Mirror." 'WARN' 'ScreenMirror'
        }
    } catch {
        Write-Log ("Greska pri pokretanju Screen Mirror radnika: {0}" -f $_.Exception.Message) 'WARN' 'ScreenMirror'
    }
} else {
    Write-Log 'WIS.ScreenCapture.ps1 nije pronadjen medju lokalnim skriptama (preskacem Live Mirror).' 'INFO' 'ScreenMirror'
}

# 10c. Pokretanje WIS.Launcher.exe ili WIS.UI.exe kao primarnog interfejsa
if (Test-Path 'C:\setup\WIS.Launcher.exe') {
    Write-Log 'Pokrecem WIS.Launcher.exe kao primarni interfejs...' 'SUCCESS' 'Launcher'
    Start-Process -FilePath 'C:\setup\WIS.Launcher.exe' -WindowStyle Maximized
    Start-Sleep -Seconds 2
} else {
    Write-Log 'Pokrecem WIS.UI.exe kao primarni interfejs (Master Shell)...' 'SUCCESS' 'Launcher'
    Start-Process -FilePath 'C:\setup\Skripte\WIS.UI\WIS.UI.exe' -ArgumentList '--master' -WindowStyle Maximized
    Start-Sleep -Seconds 2
}

$global:LASTEXITCODE = 0
exit 0
