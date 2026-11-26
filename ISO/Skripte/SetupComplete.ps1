<#
.SCRIPT
    SetupComplete.ps1
.DESCRIPTION
    WIS SetupComplete.ps1 - Zavrsna faza Windows Setup-a & OOBE ZTD Presretac (NT AUTHORITY\SYSTEM).
    Paket A1 + IMP-114 (Sloj A & Sloj C):
    - Primarna Windows 11 ZTD putanja C:\Windows\ServiceState\wmansvc\AutopilotDDSZTDFile.json i C:\Windows\Provisioning\Autopilot\.
    - Pravi OOBE ZTD presretac (-OobeWatcherMode): bez preranog BypassNRO/SkipNetworkSetup, u realnom vremenu prati
      wmansvc\AutopilotDDSZTDFile.json, HKLM\...\Diagnostics\AutoPilot i ModernDeployment-Diagnostics-Provider/Autopilot Event Log
      (Event 806 -> IMA_AUTOPILOT + alarm na ServiceHub; Event 807 -> NEMA_AUTOPILOT + prelaz u Korisnik -> WIS.Launcher.exe;
      timeout/offline -> NIJE_PROVERENO uz bezbedan fallback da instalacija nikad ne ostane mrtva).
    - Bezbedno generisanje WLAN XML profila sa [System.Security.SecurityElement]::Escape() i trenutnim brisanjem u finally bloku.
    - Jedinstvena i idempotentna ekstrakcija Programi.tar uz proveru $LASTEXITCODE i validaciju sadrzaja.
.COMPATIBILITY
    Windows PowerShell 5.1+
#>

[CmdletBinding()]
param(
    [string]$SetupDir = "C:\setup",
    [string]$LogDir = "C:\Windows\Logs\WIS",
    [switch]$ForceFail,
    [switch]$OobeWatcherMode,
    [int]$OobeWatcherTimeoutSeconds = 90
)

$ErrorActionPreference = 'Continue'
$script:Utf8NoBom = New-Object System.Text.UTF8Encoding($false)
$script:CriticalFailure = $false
$script:CriticalErrors = New-Object System.Collections.Generic.List[string]
$script:ZeroGuid = '00000000-0000-0000-0000-000000000000'
$script:WaadRegex = '\bW-AAD-\d{9}\b'
$script:GuidRegex = '[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}'

$logFiles = @(
    (Join-Path $LogDir "setupcomplete.log"),
    (Join-Path $SetupDir "setupcomplete.log")
)

foreach ($f in $logFiles) {
    try {
        $p = Split-Path $f -Parent
        if ($p -and -not (Test-Path $p)) {
            New-Item -Path $p -ItemType Directory -Force -ErrorAction Stop | Out-Null
        }
    } catch {}
}

function Write-Log {
    param(
        [string]$Msg,
        [string]$Lvl = 'INFO',
        [string]$Cmp = 'SetupComplete'
    )
    $sanitized = if ($null -eq $Msg) { '' } else { "$Msg" }
    if ($sanitized -match '(?i)(Password\s*[:=]\s*)([^\s;,\r\n"''&]+)') {
        $sanitized = $sanitized -replace '(?i)(Password\s*[:=]\s*)([^\s;,\r\n"''&]+)', '$1***MASKIRANO***'
    }
    if ($sanitized -match '(?i)(ClientToken\s*[:=]\s*)([^\s;,\r\n"''&]+)') {
        $sanitized = $sanitized -replace '(?i)(ClientToken\s*[:=]\s*)([^\s;,\r\n"''&]+)', '$1***MASKIRANO***'
    }
    if ($sanitized -match '(?i)(X-ServisHub-Client-Token\s*[:=]\s*)([^\s;,\r\n"'']+)') {
        $sanitized = $sanitized -replace '(?i)(X-ServisHub-Client-Token\s*[:=]\s*)([^\s;,\r\n"'']+)', '$1***MASKIRANO***'
    }
    if ($sanitized -match '(?i)(Authorization\s*[:=]\s*Bearer\s+)([^\s;,\r\n"'']+)') {
        $sanitized = $sanitized -replace '(?i)(Authorization\s*[:=]\s*Bearer\s+)([^\s;,\r\n"'']+)', '$1***MASKIRANO***'
    }
    if ($sanitized -match '(?i)(<keyMaterial>)(.*?)(</keyMaterial>)') {
        $sanitized = $sanitized -replace '(?i)(<keyMaterial>)(.*?)(</keyMaterial>)', '$1***MASKIRANO***$3'
    }
    $t = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss.fff')
    $l = ('[{0}] [{1}] [{2}] {3}' -f $t, $Lvl, $Cmp, $sanitized)
    foreach ($f in $logFiles) {
        try {
            [System.IO.File]::AppendAllText($f, $l + [Environment]::NewLine, $script:Utf8NoBom)
        } catch {}
    }
}

function Register-CriticalFailure {
    param(
        [string]$Reason,
        [string]$Cmp = 'SetupComplete'
    )
    $script:CriticalFailure = $true
    if (-not $script:CriticalErrors.Contains($Reason)) {
        $script:CriticalErrors.Add($Reason)
    }
    Write-Log $Reason 'ERROR' $Cmp
}

function Test-WisNonZeroGuid {
    param([string]$Candidate)
    if ([string]::IsNullOrWhiteSpace($Candidate)) { return $false }
    $clean = $Candidate.Trim().Trim('{', '}')
    if ($clean -ieq $script:ZeroGuid) { return $false }
    return ($clean -match "^$script:GuidRegex$")
}

function Get-PrimaryZtdJsonFiles {
    $list = New-Object System.Collections.ArrayList
    $winRoot = if ($env:SystemRoot) { $env:SystemRoot } else { 'C:\Windows' }

    # 1. PRIMARNA Windows 11 putanja: C:\Windows\ServiceState\wmansvc\AutopilotDDSZTDFile.json
    [void]$list.Add((Join-Path $winRoot 'ServiceState\wmansvc\AutopilotDDSZTDFile.json'))

    # 2. PRIMARNI Windows 11 Provisioning direktorijum: C:\Windows\Provisioning\Autopilot\
    $provDir = Join-Path $winRoot 'Provisioning\Autopilot'
    [void]$list.Add((Join-Path $provDir 'AutopilotConfigurationFile.json'))
    [void]$list.Add((Join-Path $provDir 'AutopilotDDSZTDFile.json'))
    if (Test-Path $provDir) {
        try {
            foreach ($pf in (Get-ChildItem -Path $provDir -Filter '*.json' -File -ErrorAction SilentlyContinue)) {
                if (-not $list.Contains($pf.FullName)) {
                    [void]$list.Add($pf.FullName)
                }
            }
        } catch {}
    }

    # 3. Sekundarni fallback: starija ServiceState\Autopilot putanja
    [void]$list.Add((Join-Path $winRoot 'ServiceState\Autopilot\AutopilotDDSZTDFile.json'))

    return ,$list.ToArray()
}

function Copy-PrimaryZtdBackup {
    $preOobeZtdCopy = Join-Path $LogDir 'AutopilotDDSZTDFile.preoobe.json'
    foreach ($ztdFile in (Get-PrimaryZtdJsonFiles)) {
        if (Test-Path -LiteralPath $ztdFile -PathType Leaf -ErrorAction SilentlyContinue) {
            try {
                if (-not (Test-Path $LogDir)) {
                    New-Item -Path $LogDir -ItemType Directory -Force -ErrorAction SilentlyContinue | Out-Null
                }
                Copy-Item -LiteralPath $ztdFile -Destination $preOobeZtdCopy -Force -ErrorAction Stop
                Write-Log ('Rezervna kopija ZTD profila ({0}) sacuvana u: {1}' -f $ztdFile, $preOobeZtdCopy) 'SUCCESS' 'AutopilotPreOOBE'
                return $ztdFile
            } catch {
                Write-Log ('Neuspesno kopiranje ZTD profila {0}: {1}' -f $ztdFile, $_.Exception.Message) 'WARN' 'AutopilotPreOOBE'
            }
        }
    }
    return $null
}

function Test-PreOobeTcpEndpoint {
    param([string]$TargetHost, [int]$Port = 443, [int]$TimeoutMs = 2000)
    try {
        $tcp = New-Object System.Net.Sockets.TcpClient
        $iar = $tcp.BeginConnect($TargetHost, $Port, $null, $null)
        $ok = $iar.AsyncWaitHandle.WaitOne($TimeoutMs, $false)
        if (-not $ok) {
            $tcp.Close()
            return $false
        }
        $tcp.EndConnect($iar)
        $tcp.Close()
        return $true
    } catch {
        return $false
    }
}

function Get-SetupDeviceSerial {
    try {
        $bios = Get-CimInstance -ClassName Win32_BIOS -ErrorAction SilentlyContinue
        if ($bios -and $bios.SerialNumber) {
            $sn = "$($bios.SerialNumber)".Trim()
            $invalid = @('', '0', 'NONE', 'DEFAULT STRING', 'TO BE FILLED BY O.E.M.', 'SYSTEM SERIAL NUMBER', 'NOT SPECIFIED', 'N/A')
            if ($invalid -notcontains $sn.ToUpper()) {
                return $sn
            }
        }
    } catch {}
    return ''
}

function Read-ServisHubConfigFromIni {
    $cfg = [PSCustomObject]@{
        Url         = 'http://192.168.170.254:8080'
        ClientToken = ''
    }
    $iniCands = New-Object System.Collections.ArrayList
    [void]$iniCands.Add((Join-Path $SetupDir 'Wifi-Servis.ini'))
    try {
        foreach ($dr in (Get-PSDrive -PSProvider FileSystem -ErrorAction SilentlyContinue)) {
            if ($dr.Root) {
                [void]$iniCands.Add((Join-Path $dr.Root 'Wifi-Servis.ini'))
                [void]$iniCands.Add((Join-Path $dr.Root 'Skripte\Wifi-Servis.ini'))
            }
        }
    } catch {}

    $iniFile = $iniCands | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf -ErrorAction SilentlyContinue } | Select-Object -First 1
    if (-not $iniFile) { return $cfg }

    try {
        $lines = Get-Content -LiteralPath $iniFile -ErrorAction Stop
        $inHub = $false
        foreach ($rawLine in $lines) {
            $line = $rawLine.Trim()
            if (-not $line -or $line.StartsWith('#') -or $line.StartsWith(';')) { continue }
            if ($line -match '^\[([^\]]+)\]$') {
                $inHub = ($matches[1].Trim() -ieq 'ServisHub')
                continue
            }
            if (-not $inHub) { continue }
            if ($line -match '^\s*Url\s*=\s*(.+)$') {
                $u = $matches[1].Trim().TrimEnd('/')
                if ($u -match '^https?://[A-Za-z0-9.\-:]+(/.*)?$') {
                    $cfg.Url = $u
                }
            } elseif ($line -match '^\s*ClientToken\s*=\s*(.+)$') {
                $cfg.ClientToken = $matches[1].Trim()
            }
        }
    } catch {}
    return $cfg
}

function Send-AutopilotVerdictToHub {
    param(
        [Parameter(Mandatory=$true)]$PayloadObj,
        [string]$Component = 'OobeZtdWatcher'
    )
    $sn = Get-SetupDeviceSerial
    if ([string]::IsNullOrWhiteSpace($sn)) {
        Write-Log 'Serijski broj nije dostupan - preskacem slanje Autopilot statusa na ServiceHub.' 'WARN' $Component
        return $false
    }
    $hubCfg = Read-ServisHubConfigFromIni
    $encSn = [Uri]::EscapeDataString($sn)
    $url = "{0}/api/devices/{1}/autopilot" -f $hubCfg.Url.TrimEnd('/'), $encSn
    $hasToken = -not [string]::IsNullOrWhiteSpace($hubCfg.ClientToken)

    try {
        $jsonBody = $PayloadObj | ConvertTo-Json -Depth 6
        $bodyBytes = $script:Utf8NoBom.GetBytes($jsonBody)
        $req = [System.Net.HttpWebRequest]::Create($url)
        $req.Method = 'POST'
        $req.ContentType = 'application/json; charset=utf-8'
        $req.Timeout = 5000
        $req.ReadWriteTimeout = 5000
        if ($hasToken) {
            $req.Headers.Add('X-ServisHub-Client-Token', $hubCfg.ClientToken)
        }
        $reqStream = $req.GetRequestStream()
        try {
            $reqStream.Write($bodyBytes, 0, $bodyBytes.Length)
        } finally {
            $reqStream.Close()
        }
        $resp = $req.GetResponse()
        try {
            $code = [int]$resp.StatusCode
            Write-Log ('Autopilot presuda ({0}) uspesno poslata na ServiceHub ({1}, HTTP {2}, ClientToken={3}).' -f $PayloadObj.binary_verdict, $url, $code, $(if($hasToken){'PRISUTAN'}else{'NEMA'})) 'SUCCESS' $Component
            return $true
        } finally {
            $resp.Close()
        }
    } catch {
        Write-Log ('Slanje Autopilot presude na ServiceHub nije uspelo ({0}): {1}' -f $url, $_.Exception.Message) 'WARN' $Component
        return $false
    }
}

function Write-PermanentAutopilotReport {
    param(
        [Parameter(Mandatory=$true)][string]$JsonPayload,
        [bool]$IsConfirmed = $false,
        [string]$Component = 'AutopilotPreOOBE'
    )
    $reportTargets = @(
        (Join-Path $LogDir 'autopilot_report.json'),
        (Join-Path $SetupDir 'autopilot_report.json')
    )
    foreach ($rt in $reportTargets) {
        try {
            $rtDir = Split-Path $rt -Parent
            if ($rtDir -and -not (Test-Path $rtDir)) {
                New-Item -Path $rtDir -ItemType Directory -Force -ErrorAction SilentlyContinue | Out-Null
            }
            if ((-not $IsConfirmed) -and (Test-Path $rt)) {
                $exRaw = Get-Content -Path $rt -Raw -ErrorAction SilentlyContinue
                if ($exRaw) {
                    $exObj = $exRaw | ConvertFrom-Json -ErrorAction SilentlyContinue
                    $exMc = if ($exObj -and $exObj.matrix_code) { "$($exObj.matrix_code)".ToUpper().Trim() } else { '' }
                    if ($exMc -in @('LOCKED_TENANT', 'HIDDEN_LOCK') -or ($exObj -and $exObj.is_locked -eq $true)) { continue }
                    if ($exMc -in @('RELEASED_LOCAL_TRACES', 'CONFIRMED_CLEAN') -and $exObj -and $exObj.cloud_ztd -and $exObj.cloud_ztd.confirmed_free -eq $true) { continue }
                }
            }
            [System.IO.File]::WriteAllText($rt, $JsonPayload, $script:Utf8NoBom)
        } catch {}
    }
}

function Inspect-OobeZtdRealtimeSnapshot {
    $res = [PSCustomObject]@{
        State              = 'PENDING' # LOCKED | CONFIRMED_FREE | PENDING
        TenantDomain       = ''
        TenantId           = ''
        AssignedDeviceName = ''
        ErrorCode          = ''
        HasLocalTraces     = $false
        QueryMode          = 'OOBE_ZTD_WATCHER'
        RawMessage         = ''
    }

    # 1. Primarni Windows 11 ZTD fajlovi (wmansvc\AutopilotDDSZTDFile.json i Provisioning\Autopilot\*.json)
    foreach ($zPath in (Get-PrimaryZtdJsonFiles)) {
        if (-not (Test-Path -LiteralPath $zPath -PathType Leaf -ErrorAction SilentlyContinue)) { continue }
        try {
            $raw = [System.IO.File]::ReadAllText($zPath, $script:Utf8NoBom)
            if ([string]::IsNullOrWhiteSpace($raw)) { continue }
            $parsed = $raw | ConvertFrom-Json -ErrorAction SilentlyContinue
            if ($parsed) {
                $tid = if ($parsed.PSObject.Properties['CloudAssignedTenantId'] -and $parsed.CloudAssignedTenantId) { "$($parsed.CloudAssignedTenantId)".Trim().Trim('{', '}') } else { '' }
                $dom = if ($parsed.PSObject.Properties['CloudAssignedTenantDomain'] -and $parsed.CloudAssignedTenantDomain) { "$($parsed.CloudAssignedTenantDomain)".Trim() } else { '' }
                $dev = if ($parsed.PSObject.Properties['CloudAssignedDeviceName'] -and $parsed.CloudAssignedDeviceName) { "$($parsed.CloudAssignedDeviceName)".Trim() } else { '' }
                if ((Test-WisNonZeroGuid -Candidate $tid) -or $dom -or ($dev -and ($dev -match $script:WaadRegex))) {
                    $res.State              = 'LOCKED'
                    $res.TenantId           = if (Test-WisNonZeroGuid -Candidate $tid) { $tid } else { '' }
                    $res.TenantDomain       = $dom
                    $res.AssignedDeviceName = $dev
                    $res.HasLocalTraces     = $true
                    $res.QueryMode          = 'OOBE_WMANSVC_ZTD_FILE'
                    $res.RawMessage         = "Autopilot ZTD profil preuzet u $zPath"
                    return $res
                }
            }
            if ($raw -match 'ZtdDeviceIsNotRegistered') {
                $res.State      = 'CONFIRMED_FREE'
                $res.ErrorCode  = '807 - ZtdDeviceIsNotRegistered'
                $res.QueryMode  = 'OOBE_WMANSVC_ZTD_FILE'
                $res.RawMessage = 'Microsoft ZTD fajl potvrdio: ZtdDeviceIsNotRegistered'
                return $res
            }
        } catch {}
    }

    # 2. HKLM:\SOFTWARE\Microsoft\Provisioning\Diagnostics\AutoPilot (+ podkljuc EstablishedCorrelations & AutopilotSettings)
    $diagReg = 'HKLM:\SOFTWARE\Microsoft\Provisioning\Diagnostics\AutoPilot'
    if (Test-Path $diagReg) {
        try {
            $dp = Get-ItemProperty -Path $diagReg -ErrorAction SilentlyContinue
            if ($dp) {
                $rTid = if ($dp.CloudAssignedTenantId) { "$($dp.CloudAssignedTenantId)".Trim().Trim('{', '}') } else { '' }
                $rDom = if ($dp.CloudAssignedTenantDomain) { "$($dp.CloudAssignedTenantDomain)".Trim() } else { '' }
                $rDev = if ($dp.CloudAssignedDeviceName) { "$($dp.CloudAssignedDeviceName)".Trim() } else { '' }
                if ((Test-WisNonZeroGuid -Candidate $rTid) -or $rDom -or ($rDev -and ($rDev -match $script:WaadRegex))) {
                    $res.State              = 'LOCKED'
                    $res.TenantId           = if (Test-WisNonZeroGuid -Candidate $rTid) { $rTid } else { '' }
                    $res.TenantDomain       = $rDom
                    $res.AssignedDeviceName = $rDev
                    $res.HasLocalTraces     = $true
                    $res.QueryMode          = 'OOBE_REGISTRY_AUTOPILOT'
                    $res.RawMessage         = 'Autopilot profil detektovan u HKLM\...\Diagnostics\AutoPilot'
                    return $res
                }
            }
            $estPath = "$diagReg\EstablishedCorrelations"
            if (Test-Path $estPath) {
                $estSubs = @(Get-ChildItem -Path $estPath -ErrorAction SilentlyContinue)
                $estItem = Get-Item -Path $estPath -ErrorAction SilentlyContinue
                $estVals = if ($estItem) { @($estItem.GetValueNames()) } else { @() }
                if ($estSubs.Count -gt 0 -or $estVals.Count -gt 0) {
                    $res.HasLocalTraces = $true
                }
            }
        } catch {}
    }

    # 3. Event Log: Microsoft-Windows-ModernDeployment-Diagnostics-Provider/Autopilot (806, 807, 100, 110, 170, 171)
    try {
        $evList = Get-WinEvent -LogName 'Microsoft-Windows-ModernDeployment-Diagnostics-Provider/Autopilot' -ErrorAction SilentlyContinue
        if ($evList) {
            foreach ($ev in $evList) {
                $evId = [int]$ev.Id
                $msg = if ($ev.Message) { "$($ev.Message)" } else { '' }
                $xml = ''
                try { $xml = $ev.ToXml() } catch {}
                $comb = "$msg`n$xml"

                $eTid = ''
                if ($comb -match '<Data\s+Name\s*=\s*["'']CloudAssignedTenantId["''][^>]*>([^<]+)</Data>') {
                    if (Test-WisNonZeroGuid -Candidate $matches[1]) { $eTid = $matches[1].Trim().Trim('{', '}') }
                } elseif ($comb -match "CloudAssignedTenantId[^0-9a-fA-F]*($script:GuidRegex)") {
                    if (Test-WisNonZeroGuid -Candidate $matches[1]) { $eTid = $matches[1].Trim() }
                }

                $eDom = ''
                if ($comb -match '<Data\s+Name\s*=\s*["'']CloudAssignedTenantDomain["''][^>]*>([^<]+)</Data>') {
                    $eDom = $matches[1].Trim()
                } elseif ($comb -match '"?CloudAssignedTenantDomain"?\s*[:=]\s*"?([^"\r\n,;<>]+)"?') {
                    $eDom = $matches[1].Trim()
                }

                $eDev = ''
                if ($comb -match '<Data\s+Name\s*=\s*["'']CloudAssignedDeviceName["''][^>]*>([^<]+)</Data>') {
                    $eDev = $matches[1].Trim()
                } elseif ($comb -match '"?CloudAssignedDeviceName"?\s*[:=]\s*"?([^"\r\n,;<>]+)"?') {
                    $eDev = $matches[1].Trim()
                }
                if (-not $eDev -and $comb -match $script:WaadRegex) {
                    $eDev = $matches[0].ToUpper()
                }

                $isModernEv = $evId -in @(100, 110, 170, 171)
                if ($evId -eq 806 -or $eTid -ne '' -or $eDom -ne '' -or ($eDev -and ($eDev -match $script:WaadRegex)) -or ($isModernEv -and $eDev -ne '')) {
                    $res.State              = 'LOCKED'
                    $res.TenantId           = $eTid
                    $res.TenantDomain       = $eDom
                    $res.AssignedDeviceName = $eDev
                    $res.QueryMode          = "OOBE_EVENT_$evId"
                    $res.RawMessage         = "Uredjaj je REGISTROVAN u Microsoft Autopilot bazi (Event $evId)"
                    return $res
                }

                if ($evId -eq 807 -or $comb -match 'ZtdDeviceIsNotRegistered') {
                    $res.State      = 'CONFIRMED_FREE'
                    $res.ErrorCode  = '807 - ZtdDeviceIsNotRegistered'
                    $res.QueryMode  = 'OOBE_EVENT_807'
                    $res.RawMessage = 'Microsoft potvrdio: Event 807 - ZtdDeviceIsNotRegistered'
                    return $res
                }
            }
        }
    } catch {}

    return $res
}

function Enable-SafeLocalServiceTransition {
    param([string]$Reason = 'ZTD zavrsen')
    # Bezbedan deterministicki prelaz u lokalni servisni nalog Korisnik -> WIS.Launcher.exe
    # Poziva se ISKLJUCIVO NAKON Event 807 potvrde ili nakon timeout/offline fallbacka (nikada prerano!)
    try {
        $oobeKey = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\OOBE'
        if (-not (Test-Path $oobeKey)) {
            New-Item -Path $oobeKey -Force -ErrorAction SilentlyContinue | Out-Null
        }
        Set-ItemProperty -Path $oobeKey -Name 'BypassNRO' -Value 1 -Type DWord -Force -ErrorAction SilentlyContinue
        Set-ItemProperty -Path $oobeKey -Name 'SkipNetworkSetup' -Value 1 -Type DWord -Force -ErrorAction SilentlyContinue
        Set-ItemProperty -Path $oobeKey -Name 'HideOnlineAccountScreens' -Value 1 -Type DWord -Force -ErrorAction SilentlyContinue

        $launcherExe = Join-Path $SetupDir 'WIS.Launcher.exe'
        if (Test-Path $launcherExe) {
            & reg.exe add 'HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce' /v 'WIS_Launcher' /t REG_SZ /d $launcherExe /f >$null 2>&1
        }
        Write-Log ('Bezbedan prelaz u lokalni servisni nalog Korisnik -> WIS.Launcher.exe omogucen ({0}).' -f $Reason) 'INFO' 'OobeZtdWatcher'
    } catch {
        Write-Log ('Napomena pri omogucavanju prelaza iz OOBE-a: {0}' -f $_.Exception.Message) 'WARN' 'OobeZtdWatcher'
    }
}

function Invoke-OobeZtdWatcher {
    param([int]$TimeoutSec = 90)
    if ($TimeoutSec -lt 15) { $TimeoutSec = 15 }
    if ($TimeoutSec -gt 180) { $TimeoutSec = 180 }

    $lockFile = Join-Path $LogDir 'oobe_ztd_watcher.lock'
    if (Test-Path $lockFile) {
        try {
            $lockAge = ((Get-Date) - (Get-Item $lockFile).LastWriteTime).TotalSeconds
            if ($lockAge -lt ($TimeoutSec + 15)) {
                Write-Log 'Drugi OobeZtdWatcher je vec aktivan - izlazim bez kolizije.' 'INFO' 'OobeZtdWatcher'
                return
            }
        } catch {}
    }

    try {
        if (-not (Test-Path $LogDir)) {
            New-Item -Path $LogDir -ItemType Directory -Force -ErrorAction SilentlyContinue | Out-Null
        }
        [System.IO.File]::WriteAllText($lockFile, "$PID", $script:Utf8NoBom)
        Write-Log ('=== IMP-114 Sloj C: Pokrenut pozadinski SYSTEM OOBE ZTD Watcher (timeout={0}s) ===' -f $TimeoutSec) 'STEP' 'OobeZtdWatcher'

        # Osiguraj da su mrezni adapteri i ZTD servisi aktivni
        try {
            Get-NetAdapter -ErrorAction SilentlyContinue | Enable-NetAdapter -Confirm:$false -ErrorAction SilentlyContinue
            foreach ($svcName in @('wlansvc', 'tbs', 'wlidsvc', 'dmwappushservice', 'wmansvc')) {
                try { Start-Service -Name $svcName -ErrorAction SilentlyContinue } catch {}
            }
            Start-Process -FilePath 'schtasks.exe' -ArgumentList '/run /tn "\Microsoft\Windows\Management\Provisioning\Autopilot"' -WindowStyle Hidden -ErrorAction SilentlyContinue
        } catch {}

        $deadline = (Get-Date).AddSeconds($TimeoutSec)
        $offlineStart = $null
        $verdict = $null
        $everOnline = $false

        while ((Get-Date) -lt $deadline) {
            $snap = Inspect-OobeZtdRealtimeSnapshot
            if ($snap.State -eq 'LOCKED' -or $snap.State -eq 'CONFIRMED_FREE') {
                $verdict = $snap
                break
            }

            $hasIp = $false
            try {
                $ipObj = Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue | Where-Object {
                    $_.InterfaceAlias -notmatch 'Loopback|vEthernet' -and
                    $_.IPAddress -notmatch '^(127\.|169\.254\.)'
                } | Select-Object -First 1
                if ($ipObj -and $ipObj.IPAddress) { $hasIp = $true }
            } catch {}

            if ($hasIp) {
                if (Test-PreOobeTcpEndpoint -TargetHost 'ztd.dds.microsoft.com' -Port 443 -TimeoutMs 2000) {
                    $everOnline = $true
                    $offlineStart = $null
                }
            }

            if (-not $everOnline) {
                if ($null -eq $offlineStart) { $offlineStart = Get-Date }
                if (((Get-Date) - $offlineStart).TotalSeconds -ge 25) {
                    Write-Log 'Mreza/ztd.dds.microsoft.com nedostupna 25s u OOBE watcheru - aktiviram bezbedan fallback (NIJE_PROVERENO).' 'WARN' 'OobeZtdWatcher'
                    break
                }
            }

            Start-Sleep -Seconds 2
        }

        $sn = Get-SetupDeviceSerial
        $modelName = 'Nepoznato'
        try {
            $cs = Get-CimInstance -ClassName Win32_ComputerSystem -ErrorAction SilentlyContinue
            if ($cs -and $cs.Model) { $modelName = "$($cs.Model)".Trim() }
        } catch {}

        if ($verdict -and $verdict.State -eq 'LOCKED') {
            Copy-PrimaryZtdBackup | Out-Null
            $lockedPayload = [PSCustomObject]@{
                timestamp            = (Get-Date).ToString('yyyy-MM-ddTHH:mm:ss')
                matrix_code          = if ($verdict.HasLocalTraces) { 'LOCKED_TENANT' } else { 'HIDDEN_LOCK' }
                status_level         = 'CRITICAL'
                binary_verdict       = 'IMA_AUTOPILOT'
                status_title         = 'IMA AUTOPILOT - ZAKLJUCAN U ORGANIZACIJI (OOBE ZTD PRESRETAC)'
                status_description   = "Windows OOBE ZTD presretac je detektovao aktivan Autopilot profil ($($verdict.TenantDomain))."
                recommended_action   = 'FORMATIRANJE DISKA NE RESAVA PROBLEM! IT administrator firme mora obrisati uredjaj iz Intune/Autopilot portala.'
                is_locked            = $true
                confirmed_free       = $false
                has_local_traces     = [bool]$verdict.HasLocalTraces
                tenant_domain        = $verdict.TenantDomain
                tenant_id            = $verdict.TenantId
                assigned_device_name = $verdict.AssignedDeviceName
                query_mode           = $verdict.QueryMode
                source               = 'oobe_ztd'
                device_serial        = if ($sn) { $sn } else { 'Nepoznato' }
                device_model         = $modelName
                cloud_ztd            = [PSCustomObject]@{
                    online_checked        = $true
                    network_reachable     = $true
                    is_registered         = $true
                    confirmed_free        = $false
                    query_mode            = $verdict.QueryMode
                    error_code            = ''
                    raw_status_message    = $verdict.RawMessage
                    profile_tenant_domain = $verdict.TenantDomain
                    profile_tenant_id     = $verdict.TenantId
                    profile_device_name   = $verdict.AssignedDeviceName
                }
            }
            $lockedJson = $lockedPayload | ConvertTo-Json -Depth 6
            Write-PermanentAutopilotReport -JsonPayload $lockedJson -IsConfirmed $true -Component 'OobeZtdWatcher'
            Write-Log ('[ALARM] OOBE ZTD PRESRETAC DETEKTOVAO IMA_AUTOPILOT! Tenant={0}, TenantId={1}, Device={2}' -f $verdict.TenantDomain, $verdict.TenantId, $verdict.AssignedDeviceName) 'ERROR' 'OobeZtdWatcher'
            Send-AutopilotVerdictToHub -PayloadObj $lockedPayload -Component 'OobeZtdWatcher' | Out-Null
        } elseif ($verdict -and $verdict.State -eq 'CONFIRMED_FREE') {
            $cleanPayload = [PSCustomObject]@{
                timestamp            = (Get-Date).ToString('yyyy-MM-ddTHH:mm:ss')
                matrix_code          = if ($verdict.HasLocalTraces) { 'RELEASED_LOCAL_TRACES' } else { 'CONFIRMED_CLEAN' }
                status_level         = if ($verdict.HasLocalTraces) { 'WARNING' } else { 'OK' }
                binary_verdict       = 'NEMA_AUTOPILOT'
                status_title         = 'NEMA AUTOPILOT - POTVRDJENO SLOBODAN (EVENT 807)'
                status_description   = 'Windows OOBE ZTD presretac je primio eksplicitnu Event 807 (ZtdDeviceIsNotRegistered) potvrdu od Microsoft oblaka.'
                recommended_action   = 'Uredjaj je slobodan za servisiranje i predaju kupcu.'
                is_locked            = $false
                confirmed_free       = $true
                has_local_traces     = [bool]$verdict.HasLocalTraces
                tenant_domain        = ''
                tenant_id            = ''
                assigned_device_name = ''
                query_mode           = $verdict.QueryMode
                source               = 'oobe_ztd'
                device_serial        = if ($sn) { $sn } else { 'Nepoznato' }
                device_model         = $modelName
                cloud_ztd            = [PSCustomObject]@{
                    online_checked        = $true
                    network_reachable     = $true
                    is_registered         = $false
                    confirmed_free        = $true
                    query_mode            = $verdict.QueryMode
                    error_code            = '807 - ZtdDeviceIsNotRegistered'
                    raw_status_message    = $verdict.RawMessage
                    profile_tenant_domain = ''
                    profile_tenant_id     = ''
                    profile_device_name   = ''
                }
            }
            $cleanJson = $cleanPayload | ConvertTo-Json -Depth 6
            Write-PermanentAutopilotReport -JsonPayload $cleanJson -IsConfirmed $true -Component 'OobeZtdWatcher'
            Write-Log '[OK] OOBE ZTD PRESRETAC: Event 807 (NEMA_AUTOPILOT) potvrdjen -> deterministicki prelaz u Korisnik -> WIS.Launcher.exe.' 'SUCCESS' 'OobeZtdWatcher'
            Send-AutopilotVerdictToHub -PayloadObj $cleanPayload -Component 'OobeZtdWatcher' | Out-Null
            Enable-SafeLocalServiceTransition -Reason 'Event 807 (NEMA_AUTOPILOT)'
        } else {
            $unverifiedMatrix = if ($everOnline) { 'UNVERIFIED_CLEAN' } else { 'OFFLINE_CLEAN_LOCAL' }
            $unverifiedMode   = if ($everOnline) { 'OOBE_ZTD_TIMEOUT' } else { 'OOBE_ZTD_OFFLINE' }
            $unverifiedPayload = [PSCustomObject]@{
                timestamp            = (Get-Date).ToString('yyyy-MM-ddTHH:mm:ss')
                matrix_code          = $unverifiedMatrix
                status_level         = 'WARNING'
                binary_verdict       = 'NIJE_PROVERENO'
                status_title         = 'NIJE PROVERENO (POTREBNA MREZA U OOBE-U)'
                status_description   = 'OOBE ZTD presretac nije dobio eksplicitnu Event 806/807 potvrdu pre isteka timeout-a ili zbog nedostatka mreze.'
                recommended_action   = 'Aktivirana je bezbedna fallback procedura za nastavak u Korisnik. Ponovite OOBE ZTD proveru sa aktivnom mrezom pre predaje.'
                is_locked            = $false
                confirmed_free       = $false
                has_local_traces     = $false
                tenant_domain        = ''
                tenant_id            = ''
                assigned_device_name = ''
                query_mode           = $unverifiedMode
                source               = 'oobe_ztd'
                device_serial        = if ($sn) { $sn } else { 'Nepoznato' }
                device_model         = $modelName
                cloud_ztd            = [PSCustomObject]@{
                    online_checked        = $false
                    network_reachable     = [bool]$everOnline
                    is_registered         = $false
                    confirmed_free        = $false
                    query_mode            = $unverifiedMode
                    error_code            = ''
                    raw_status_message    = 'Timeout / mreza nedostupna tokom OOBE ZTD provere (NIJE_PROVERENO)'
                    profile_tenant_domain = ''
                    profile_tenant_id     = ''
                    profile_device_name   = ''
                }
            }
            $unvJson = $unverifiedPayload | ConvertTo-Json -Depth 6
            Write-PermanentAutopilotReport -JsonPayload $unvJson -IsConfirmed $false -Component 'OobeZtdWatcher'
            Write-Log ('OOBE ZTD presretac zavrsio bez eksplicitnog 806/807 odgovora ({0}) - aktiviram bezbedan fallback u Korisnik.' -f $unverifiedMode) 'WARN' 'OobeZtdWatcher'
            Send-AutopilotVerdictToHub -PayloadObj $unverifiedPayload -Component 'OobeZtdWatcher' | Out-Null
            Enable-SafeLocalServiceTransition -Reason "Fallback ($unverifiedMode)"
        }
    } catch {
        Write-Log ('Izuzetak u OobeZtdWatcher: {0}' -f $_.Exception.Message) 'WARN' 'OobeZtdWatcher'
        Enable-SafeLocalServiceTransition -Reason 'Exception fallback'
    } finally {
        Remove-Item -Path $lockFile -Force -ErrorAction SilentlyContinue
        try {
            & schtasks.exe /delete /tn 'WIS_OOBE_ZTD_Watcher' /f >$null 2>&1
        } catch {}
    }
}

# Ako je skripta pokrenuta kao pozadinski SYSTEM OOBE ZTD Watcher (IMP-114 Sloj C)
if ($OobeWatcherMode) {
    Invoke-OobeZtdWatcher -TimeoutSec $OobeWatcherTimeoutSeconds
    exit 0
}

function Sync-SetupCompleteLogToUsb {
    Write-Log 'Sinhronizacija logova na USB instalacioni medijum...' 'STEP' 'UsbLogSync'
    $usbDrives = Get-PSDrive -PSProvider FileSystem -ErrorAction SilentlyContinue | Where-Object { $_.Root -ne 'C:\' }
    $copiedToUsb = $false
    $srcLog = Join-Path $LogDir "setupcomplete.log"
    if (-not (Test-Path $srcLog)) {
        $srcLog = Join-Path $SetupDir "setupcomplete.log"
    }
    foreach ($ud in $usbDrives) {
        $usbLogDir = Join-Path $ud.Root 'Logs'
        if (Test-Path $usbLogDir) {
            try {
                $destPath = Join-Path $usbLogDir 'setupcomplete.log'
                if (Test-Path $srcLog) {
                    Copy-Item -Path $srcLog -Destination $destPath -Force -ErrorAction Stop
                    Write-Log ('setupcomplete.log uspesno iskopiran na USB: {0}' -f $destPath) 'SUCCESS' 'UsbLogSync'
                    $copiedToUsb = $true
                }
            } catch {
                Write-Log ('Neuspesno kopiranje loga na {0}: {1}' -f $usbLogDir, $_.Exception.Message) 'WARN' 'UsbLogSync'
            }
        }
    }
    if (-not $copiedToUsb) {
        Write-Log 'Nije pronadjen USB medijum sa postojecim folderom Logs za sinhronizaciju.' 'INFO' 'UsbLogSync'
    }
}

# 0. Inicijalna provera okruzenja
$curUser = try { [System.Security.Principal.WindowsIdentity]::GetCurrent().Name } catch { $env:USERNAME }
$hostName = if ($env:COMPUTERNAME) { $env:COMPUTERNAME } else { 'UNKNOWN-PC' }

Write-Log '==============================================================================' 'INFO' 'SetupComplete'
Write-Log ('SetupComplete.ps1 pokrenut na racunaru: {0}' -f $hostName) 'INFO' 'SetupComplete'
Write-Log ('Izvrsavanje pod korisnickim nalogom: {0}' -f $curUser) 'INFO' 'SetupComplete'
Write-Log ('Radni direktorijum: {0}' -f $PWD.Path) 'INFO' 'SetupComplete'

if (-not (Test-Path $SetupDir)) {
    try {
        New-Item -Path $SetupDir -ItemType Directory -Force -ErrorAction Stop | Out-Null
    } catch {
        Register-CriticalFailure ('Kriticni direktorijum {0} ne postoji i ne moze biti kreiran: {1}' -f $SetupDir, $_.Exception.Message) 'Environment'
    }
}

if ($ForceFail) {
    Register-CriticalFailure 'Forsirani neuspeh (ForceFail) aktiviran za testiranje kontrolnog signala.' 'SetupComplete'
}

# 1. Aktivacija mreznih adaptera (Opcioni / Best-Effort korak)
Write-Log 'Skeniranje stanja mreznih adaptera PRE aktivacije...' 'STEP' 'Network'
$adaptersBefore = Get-NetAdapter -ErrorAction SilentlyContinue
if ($adaptersBefore) {
    foreach ($a in $adaptersBefore) {
        Write-Log ('[PRE] Adapter: {0} | Status: {1} | LinkSpeed: {2} | Opis: {3}' -f $a.Name, $a.Status, $a.LinkSpeed, $a.InterfaceDescription) 'INFO' 'Network'
    }
} else {
    Write-Log 'Nema detektovanih mreznih adaptera u sistemu.' 'WARN' 'Network'
}

Write-Log 'Omogucavanje svih mreznih adaptera (Enable-NetAdapter)...' 'INFO' 'Network'
try {
    Get-NetAdapter -ErrorAction SilentlyContinue | Enable-NetAdapter -Confirm:$false -ErrorAction SilentlyContinue
} catch {}
Start-Sleep -Seconds 1

Write-Log 'Skeniranje stanja mreznih adaptera POSLE aktivacije...' 'STEP' 'Network'
$adaptersAfter = Get-NetAdapter -ErrorAction SilentlyContinue
if ($adaptersAfter) {
    foreach ($a in $adaptersAfter) {
        Write-Log ('[POSLE] Adapter: {0} | Status: {1} | LinkSpeed: {2}' -f $a.Name, $a.Status, $a.LinkSpeed) 'INFO' 'Network'
    }
} else {
    Write-Log 'Nema aktivnih mreznih adaptera.' 'WARN' 'Network'
}

# 2. Konfiguracija Windows Update servisa (wuauserv)
Write-Log 'Konfigurisanje servisa Windows Update (wuauserv) na automatsko pokretanje...' 'STEP' 'WindowsUpdate'
$scOut = (& sc.exe config wuauserv start= auto 2>&1) -join ' '
$scCode = $LASTEXITCODE
$scLvl = if ($scCode -eq 0) { 'SUCCESS' } else { 'WARN' }
Write-Log ('sc config wuauserv rezultat: ExitCode={0}, Izlaz={1}' -f $scCode, $scOut) $scLvl 'WindowsUpdate'

Write-Log 'Pokretanje servisa Windows Update (wuauserv)...' 'INFO' 'WindowsUpdate'
$netOut = (& net.exe start wuauserv 2>&1) -join ' '
$netCode = $LASTEXITCODE
$netLvl = if ($netCode -eq 0 -or $netCode -eq 2) { 'SUCCESS' } else { 'WARN' }
Write-Log ('net start wuauserv rezultat: ExitCode={0}, Izlaz={1}' -f $netCode, $netOut) $netLvl 'WindowsUpdate'

$svc = Get-Service -Name wuauserv -ErrorAction SilentlyContinue
if ($svc) {
    Write-Log ('Status servisa wuauserv: {0} (StartType: {1})' -f $svc.Status, $svc.StartType) 'INFO' 'WindowsUpdate'
}

# 3. Uklanjanje restriktivnih OOBE / WU polisa i kriticna konfiguracija registra
Write-Log 'Uklanjanje restriktivnih polisa koje su blokirale OOBE i azuriranja...' 'STEP' 'Registry'
$policies = @(
    @{ Key = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate'; Value = 'DoNotConnectToWindowsUpdateInternetLocations' },
    @{ Key = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU'; Value = 'NoAutoUpdate' },
    @{ Key = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate'; Value = 'DisableOSUpgrade' },
    @{ Key = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\OOBE'; Value = 'DisableOOBEConnection' }
)
foreach ($pol in $policies) {
    if (Test-Path $pol.Key) {
        $prop = Get-ItemProperty -Path $pol.Key -Name $pol.Value -ErrorAction SilentlyContinue
        if ($null -ne $prop -and $null -ne $prop.($pol.Value)) {
            Remove-ItemProperty -Path $pol.Key -Name $pol.Value -Force -ErrorAction SilentlyContinue
            Write-Log ('Uklonjena polisa: {0}\{1}' -f $pol.Key, $pol.Value) 'SUCCESS' 'Registry'
        } else {
            Write-Log ('Polisa ne postoji (vec cisto): {0}\{1}' -f $pol.Key, $pol.Value) 'INFO' 'Registry'
        }
    } else {
        Write-Log ('Reg kljuc ne postoji: {0}' -f $pol.Key) 'INFO' 'Registry'
    }
}

$locPolKey = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\LocationAndSensors'
if (Test-Path $locPolKey) {
    try {
        $dlProp = Get-ItemProperty -Path $locPolKey -Name 'DisableLocation' -ErrorAction SilentlyContinue
        if ($null -ne $dlProp -and $null -ne $dlProp.DisableLocation -and [int]$dlProp.DisableLocation -eq 1) {
            Remove-ItemProperty -Path $locPolKey -Name 'DisableLocation' -Force -ErrorAction SilentlyContinue
            Write-Log 'Uklonjena zaostala polisa LocationAndSensors\DisableLocation=1.' 'SUCCESS' 'Registry'
        }
    } catch {}
}

try {
    $lfsvcReg = 'HKLM:\SYSTEM\CurrentControlSet\Services\lfsvc'
    if (Test-Path $lfsvcReg) {
        $lfStart = (Get-ItemProperty -Path $lfsvcReg -Name 'Start' -ErrorAction SilentlyContinue).Start
        if ($null -ne $lfStart -and [int]$lfStart -eq 4) {
            & sc.exe config lfsvc start= demand >$null 2>&1
            Write-Log 'Servis lfsvc vracen iz Disabled u Manual (demand).' 'SUCCESS' 'Registry'
        }
    }
    $lfSvcObj = Get-Service -Name 'lfsvc' -ErrorAction SilentlyContinue
    if ($lfSvcObj -and $lfSvcObj.Status -ne 'Running') {
        Start-Service -Name 'lfsvc' -ErrorAction SilentlyContinue
    }
} catch {}

Write-Log 'Konfigurisanje SMB klijenta za nesiguran Guest pristup i onemogucavanje obaveznog SMB potpisivanja (Win 11 24H2 kompatibilnost)...' 'STEP' 'Registry'
& reg.exe add 'HKLM\SYSTEM\CurrentControlSet\Services\LanmanWorkstation\Parameters' /v 'AllowInsecureGuestAuth' /t REG_DWORD /d 1 /f >$null 2>&1
$smbExit1 = $LASTEXITCODE
& reg.exe add 'HKLM\SYSTEM\CurrentControlSet\Services\LanmanWorkstation\Parameters' /v 'RequireSecuritySignature' /t REG_DWORD /d 0 /f >$null 2>&1
$smbExit2 = $LASTEXITCODE
if ($smbExit1 -eq 0 -and $smbExit2 -eq 0) {
    Write-Log 'SMB parametri (AllowInsecureGuestAuth=1, RequireSecuritySignature=0) uspesno primenjeni.' 'SUCCESS' 'Registry'
} else {
    Register-CriticalFailure ('Neuspesno postavljanje kriticnih SMB parametara u registru (Exit1={0}, Exit2={1}).' -f $smbExit1, $smbExit2) 'Registry'
}

Write-Log 'Sprecavanje automatskog otvaranja Start Menija i animacija pri prvom logovanju + omogucavanje Location pristupa (ConsentStore=Allow)...' 'STEP' 'Registry'
& reg.exe add 'HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System' /v 'EnableFirstLogonAnimation' /t REG_DWORD /d 0 /f >$null 2>&1
$animExit = $LASTEXITCODE
$locSubKey = 'Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\location'
& reg.exe add "HKLM\$locSubKey" /v 'ShowGlobalPrompts' /t REG_DWORD /d 0 /f >$null 2>&1
& reg.exe add "HKLM\$locSubKey" /v 'Value' /t REG_SZ /d 'Allow' /f >$null 2>&1
& reg.exe add "HKLM\$locSubKey\NonPackaged" /v 'Value' /t REG_SZ /d 'Allow' /f >$null 2>&1
& reg.exe add "HKCU\$locSubKey" /v 'ShowGlobalPrompts' /t REG_DWORD /d 0 /f >$null 2>&1
& reg.exe add "HKCU\$locSubKey" /v 'Value' /t REG_SZ /d 'Allow' /f >$null 2>&1
& reg.exe add "HKCU\$locSubKey\NonPackaged" /v 'Value' /t REG_SZ /d 'Allow' /f >$null 2>&1

try {
    $loadedSids = Get-ChildItem -Path 'Registry::HKEY_USERS' -ErrorAction SilentlyContinue |
        Where-Object { $_.PSChildName -match '^S-1-5-21-' -and $_.PSChildName -notlike '*_Classes' }
    foreach ($sidNode in $loadedSids) {
        $sidRegPath = "HKU\$($sidNode.PSChildName)\$locSubKey"
        & reg.exe add $sidRegPath /v 'ShowGlobalPrompts' /t REG_DWORD /d 0 /f >$null 2>&1
        & reg.exe add $sidRegPath /v 'Value' /t REG_SZ /d 'Allow' /f >$null 2>&1
        & reg.exe add "$sidRegPath\NonPackaged" /v 'Value' /t REG_SZ /d 'Allow' /f >$null 2>&1
    }
} catch {}

& reg.exe load 'HKU\DefaultUser' 'C:\Users\Default\NTUSER.DAT' >$null 2>&1
if ($LASTEXITCODE -eq 0) {
    try {
        & reg.exe add 'HKU\DefaultUser\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced' /v 'StartShownOnUpgrade' /t REG_DWORD /d 1 /f >$null 2>&1
        & reg.exe add "HKU\DefaultUser\$locSubKey" /v 'ShowGlobalPrompts' /t REG_DWORD /d 0 /f >$null 2>&1
        & reg.exe add "HKU\DefaultUser\$locSubKey" /v 'Value' /t REG_SZ /d 'Allow' /f >$null 2>&1
        & reg.exe add "HKU\DefaultUser\$locSubKey\NonPackaged" /v 'Value' /t REG_SZ /d 'Allow' /f >$null 2>&1
    } finally {
        & reg.exe unload 'HKU\DefaultUser' >$null 2>&1
    }
}
if ($animExit -eq 0) {
    Write-Log 'Polise StartShownOnUpgrade=1 (DefaultUser), Location ConsentStore=Allow (HKLM/HKCU/HKU/DefaultUser), ShowGlobalPrompts=0 i EnableFirstLogonAnimation=0 primenjene.' 'SUCCESS' 'Registry'
} else {
    Register-CriticalFailure ('Neuspesno postavljanje kriticne polise EnableFirstLogonAnimation (ExitCode={0}).' -f $animExit) 'Registry'
}

# 4. Windows Defender izuzeci (Best-Effort)
Write-Log 'Postavljanje Windows Defender izuzetaka...' 'STEP' 'Defender'
try {
    Add-MpPreference -ExclusionPath $SetupDir -ErrorAction SilentlyContinue
    Write-Log ('Dodat Defender izuzetak za folder: {0}' -f $SetupDir) 'SUCCESS' 'Defender'
    if (Test-Path 'D:\') {
        Add-MpPreference -ExclusionPath 'D:\' -ErrorAction SilentlyContinue
        Write-Log 'Dodat Defender izuzetak za particiju: D:\' 'SUCCESS' 'Defender'
    }
} catch {}

# 5. Detekcija povezanih diskova i opciono preuzimanje Wifi-Servis.ini
Write-Log 'Detekcija povezanih diskova i particija...' 'STEP' 'DiskDetection'
$allDrives = @(Get-PSDrive -PSProvider FileSystem -ErrorAction SilentlyContinue | Where-Object { $_.Root -ne 'C:\' })
foreach ($dr in $allDrives) {
    $hasTar = Test-Path (Join-Path $dr.Root 'Programi.tar')
    $hasOff = Test-Path (Join-Path $dr.Root 'Programi\Office 2024')
    $hasOem = Test-Path (Join-Path $dr.Root ('sources\' + [char]36 + 'OEM\' + [char]36 + '1\setup\Programi\Office 2024'))
    $hasPrg = Test-Path (Join-Path $dr.Root 'Programi')
    $szGb = try { [math]::Round(($dr.Free / 1GB), 2) } catch { 0 }
    Write-Log ('Particija {0} (Slobodno: {1} GB) -> Tar: {2}, Office: {3}, OEM_Office: {4}, Programi: {5}' -f $dr.Root, $szGb, $hasTar, $hasOff, $hasOem, $hasPrg) 'INFO' 'DiskDetection'
}

$d = $allDrives | Where-Object {
    (Test-Path (Join-Path $_.Root 'Programi.tar')) -or
    (Test-Path (Join-Path $_.Root ('sources\' + [char]36 + 'OEM\' + [char]36 + '1\setup\Programi.tar'))) -or
    (Test-Path (Join-Path $_.Root 'Programi\Office 2024')) -or
    (Test-Path (Join-Path $_.Root ('sources\' + [char]36 + 'OEM\' + [char]36 + '1\setup\Programi\Office 2024'))) -or
    (Test-Path (Join-Path $_.Root 'Programi'))
} | Select-Object -First 1

$localWifiIni = Join-Path $SetupDir 'Wifi-Servis.ini'
if ($d) {
    Write-Log ('Izabran instalacioni izvor: {0}' -f $d.Root) 'SUCCESS' 'DiskDetection'
    try { Add-MpPreference -ExclusionPath $d.Root -ErrorAction SilentlyContinue } catch {}
    Write-Log ('Dodat Defender izuzetak za instalacioni izvor: {0}' -f $d.Root) 'SUCCESS' 'Defender'

    if (-not (Test-Path $localWifiIni)) {
        $w = @(
            (Join-Path $d.Root 'Wifi-Servis.ini'),
            (Join-Path $d.Root 'Skripte\Wifi-Servis.ini'),
            (Join-Path $d.Root ('sources\' + [char]36 + 'OEM\' + [char]36 + '1\setup\Wifi-Servis.ini'))
        ) | Where-Object { Test-Path $_ } | Select-Object -First 1
        if ($w) {
            Write-Log ('Kopiranje Wifi konfiguracije sa {0} u {1}...' -f $w, $localWifiIni) 'STEP' 'FileTransfer'
            Copy-Item -Path $w -Destination $localWifiIni -Force -ErrorAction SilentlyContinue
            $wfOk = Test-Path $localWifiIni
            $wfStatus = if ($wfOk) { 'USPESNO' } else { 'NEUSPELO' }
            $wfLvl = if ($wfOk) { 'SUCCESS' } else { 'WARN' }
            Write-Log ('Kopiranje Wifi-Servis.ini: {0}' -f $wfStatus) $wfLvl 'FileTransfer'
        } else {
            Write-Log 'Wifi-Servis.ini nije pronadjen na instalacionom disku (opciono).' 'INFO' 'FileTransfer'
        }
    } else {
        Write-Log ('{0} vec postoji u lokalnom kesu.' -f $localWifiIni) 'INFO' 'FileTransfer'
    }
} else {
    Write-Log 'Nije detektovan instalacioni medijum za prebacivanje fajlova.' 'WARN' 'DiskDetection'
}

# 6. Rani tihi import Wi-Fi profila sa XML escaping-om i trenutnim brisanjem XML-a (Nalaz 17 & U1)
Write-Log 'Rani tihi import Wi-Fi profila i auto-povezivanje pod SYSTEM nalogom (WIS-TASK-122 / Paket A1)...' 'STEP' 'WiFiAutoConnect'
$scWlan = (& sc.exe config wlansvc start= auto 2>&1) -join ' '
$netWlan = (& net.exe start wlansvc 2>&1) -join ' '
Write-Log ('Konfiguracija servisa wlansvc (sc: {0}, net: {1})' -f $scWlan, $netWlan) 'INFO' 'WiFiAutoConnect'

if (Test-Path $localWifiIni) {
    try {
        $iniLines = Get-Content -Path $localWifiIni -ErrorAction SilentlyContinue
        $hasSec = $false
        $inWifiSec = $false
        $wifiEnabled = $true
        $wifiSsidRaw = ''
        $wifiPass = ''
        foreach ($rl in $iniLines) {
            $ln = $rl.Trim()
            if (-not $ln -or $ln.StartsWith('#') -or $ln.StartsWith(';')) { continue }
            if ($ln -match '^\[([^\]]+)\]$') {
                $hasSec = $true
                $inWifiSec = ($matches[1].Trim() -ieq 'WiFi')
                continue
            }
            if ($hasSec -and (-not $inWifiSec)) { continue }
            if ($ln -match '^\s*Enabled\s*=\s*(.+)$') { $wifiEnabled = ($matches[1].Trim() -eq '1') }
            elseif ($ln -match '^\s*SSID\s*=\s*(.+)$') { $wifiSsidRaw = $matches[1].Trim() }
            elseif ($ln -match '^\s*Password\s*=\s*(.+)$') { $wifiPass = $matches[1].Trim() }
        }

        if ($wifiEnabled -and $wifiSsidRaw -and $wifiPass -and $wifiSsidRaw -ne 'YOUR_SERVICE_WIFI_SSID' -and $wifiPass -ne 'CHANGE_ME') {
            Write-Log ('Pronadjena konfiguracija za servisni Wi-Fi (SSID: {0}). Uvoz profila sa XML escaping-om...' -f $wifiSsidRaw) 'INFO' 'WiFiAutoConnect'
            $ssids = @($wifiSsidRaw -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ })
            $xmlPath = Join-Path $SetupDir 'Wifi-Servis.xml'
            $anyProfileAdded = $false

            foreach ($s in $ssids) {
                $escapedSsid = [System.Security.SecurityElement]::Escape([string]$s)
                $escapedPass = [System.Security.SecurityElement]::Escape([string]$wifiPass)
                $xml = @"
<?xml version="1.0"?>
<WLANProfile xmlns="http://www.microsoft.com/networking/WLAN/profile/v1">
    <name>$escapedSsid</name>
    <SSIDConfig>
        <SSID>
            <name>$escapedSsid</name>
        </SSID>
    </SSIDConfig>
    <connectionType>ESS</connectionType>
    <connectionMode>auto</connectionMode>
    <MSM>
        <security>
            <authEncryption>
                <authentication>WPA2PSK</authentication>
                <encryption>AES</encryption>
                <useOneX>false</useOneX>
            </authEncryption>
            <sharedKey>
                <keyType>passPhrase</keyType>
                <protected>false</protected>
                <keyMaterial>$escapedPass</keyMaterial>
            </sharedKey>
        </security>
    </MSM>
</WLANProfile>
"@
                $addExit = -1
                $addRes = ''
                try {
                    [System.IO.File]::WriteAllText($xmlPath, $xml, $script:Utf8NoBom)
                    $addOut = & netsh.exe wlan add profile filename="$xmlPath" user=all 2>&1
                    $addExit = $LASTEXITCODE
                    $addRes = ($addOut -join ' ').Trim()
                } finally {
                    Remove-Item -Path $xmlPath -Force -ErrorAction SilentlyContinue
                }

                if ($addExit -eq 0) {
                    $anyProfileAdded = $true
                    Write-Log ('netsh wlan add profile za "{0}" uspesan (ExitCode=0): {1}' -f $s, $addRes) 'SUCCESS' 'WiFiAutoConnect'
                } else {
                    Write-Log ('netsh wlan add profile za "{0}" nije uspeo (ExitCode={1}): {2}' -f $s, $addExit, $addRes) 'WARN' 'WiFiAutoConnect'
                }
            }

            if ($ssids.Count -gt 0 -and $anyProfileAdded) {
                $primarySsid = $ssids[0]
                $connOut = & netsh.exe wlan connect name="$primarySsid" 2>&1
                $connExit = $LASTEXITCODE
                $connRes = ($connOut -join ' ').Trim()
                $connLvl = if ($connExit -eq 0) { 'SUCCESS' } else { 'WARN' }
                Write-Log ('Rezultat netsh wlan connect za "{0}" (ExitCode={1}): {2}' -f $primarySsid, $connExit, $connRes) $connLvl 'WiFiAutoConnect'
            }
        } else {
            Write-Log 'Wifi-Servis.ini postoji ali Wi-Fi nije omogucen (Enabled!=1) ili nedostaje SSID/lozinka.' 'INFO' 'WiFiAutoConnect'
        }
    } catch {
        Write-Log ('Greska pri ranom uvozu Wi-Fi profila: {0}' -f $_.Exception.Message) 'WARN' 'WiFiAutoConnect'
    }
} else {
    Write-Log ('{0} nije pronadjen u lokalnom kesu.' -f $localWifiIni) 'INFO' 'WiFiAutoConnect'
}

# 6b. Pre-OOBE / OOBE Autopilot ZTD Provera, Primarni wmansvc Kes i Sloj C Integracija
Write-Log 'Pokretanje Pre-OOBE Autopilot ZTD provere (IMP-114 Sloj A & C: primarni wmansvc + Provisioning\Autopilot)...' 'STEP' 'AutopilotPreOOBE'
try {
    # Ako je pozadinski OOBE ZTD Watcher upravo aktivan, sacekaj kratko (do 15s) da dovrsi upis
    $watcherLock = Join-Path $LogDir 'oobe_ztd_watcher.lock'
    for ($wlWait = 0; $wlWait -lt 15; $wlWait++) {
        if (-not (Test-Path $watcherLock)) { break }
        Start-Sleep -Seconds 1
    }

    foreach ($apSvc in @('tbs', 'wlidsvc', 'dmwappushservice', 'wmansvc')) {
        try {
            Start-Service -Name $apSvc -ErrorAction SilentlyContinue
        } catch {}
    }

    $preOobeOnline = $false
    for ($netSec = 1; $netSec -le 20; $netSec++) {
        $validIpObj = $null
        try {
            $validIpObj = Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue | Where-Object {
                $_.InterfaceAlias -notmatch 'Loopback|vEthernet' -and
                $_.IPAddress -notmatch '^(127\.|169\.254\.)'
            } | Select-Object -First 1
        } catch {}

        if ($validIpObj -and $validIpObj.IPAddress) {
            if (Test-PreOobeTcpEndpoint -TargetHost 'ztd.dds.microsoft.com' -Port 443 -TimeoutMs 2500) {
                $preOobeOnline = $true
                Write-Log ('Mrezna veza ka ztd.dds.microsoft.com:443 potvrdjena u Pre-OOBE fazi (IP={0}, cekanje={1}s).' -f $validIpObj.IPAddress, $netSec) 'SUCCESS' 'AutopilotPreOOBE'
                break
            }
        }
        Start-Sleep -Seconds 1
    }

    if (-not $preOobeOnline) {
        Write-Log 'ztd.dds.microsoft.com:443 nije dostizan u roku od 20s u Pre-OOBE fazi (provericu lokalni/wmansvc ZTD kes i Event Log).' 'WARN' 'AutopilotPreOOBE'
    }

    $oobeRegPath = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\OOBE'
    try {
        # Ne blokiraj OOBE mrezu pre nego sto ZTD provera zavrsi!
        if (Test-Path $oobeRegPath) {
            foreach ($oobeBlocker in @('SkipNetworkSetup', 'BypassNRO', 'DisableOOBEConnection')) {
                try {
                    $oProps = Get-ItemProperty -Path $oobeRegPath -Name $oobeBlocker -ErrorAction SilentlyContinue
                    if ($null -ne $oProps -and $null -ne $oProps.$oobeBlocker) {
                        Remove-ItemProperty -Path $oobeRegPath -Name $oobeBlocker -Force -ErrorAction SilentlyContinue
                    }
                } catch {}
            }
        }

        try {
            Start-Process -FilePath 'schtasks.exe' -ArgumentList '/run /tn "\Microsoft\Windows\Management\Provisioning\Autopilot"' -WindowStyle Hidden -ErrorAction SilentlyContinue
        } catch {}

        # Kopiraj primarni ZTD fajl (C:\Windows\ServiceState\wmansvc\AutopilotDDSZTDFile.json / Provisioning\Autopilot\) cim postoji
        Copy-PrimaryZtdBackup | Out-Null

        # Brza provera zivog OOBE stanja (wmansvc + Diagnostics\AutoPilot + Event Log 806/807)
        $rtSnap = Inspect-OobeZtdRealtimeSnapshot
        if ($rtSnap.State -eq 'LOCKED' -or $rtSnap.State -eq 'CONFIRMED_FREE') {
            Write-Log ('OOBE ZTD evidencija vec dostupna u SetupComplete ({0}, mode={1}).' -f $rtSnap.State, $rtSnap.QueryMode) 'INFO' 'AutopilotPreOOBE'
        }

        # Pronalazenje WIS-AutopilotAuditor.exe ili Provera_autopilota.ps1
        $apAuditorExe = $null
        $apAuditorPs1 = $null
        $exeCands = @(
            (Join-Path $SetupDir 'WIS-AutopilotAuditor.exe'),
            (Join-Path $SetupDir 'Skripte\WIS-AutopilotAuditor.exe'),
            (Join-Path $SetupDir 'Tools\AutopilotAuditor\WIS-AutopilotAuditor.exe')
        )
        $ps1Cands = @(
            (Join-Path $SetupDir 'Provera_autopilota.ps1'),
            (Join-Path $SetupDir 'Skripte\Provera_autopilota.ps1')
        )
        foreach ($dr in $allDrives) {
            $exeCands += @(
                (Join-Path $dr.Root 'WIS-AutopilotAuditor.exe'),
                (Join-Path $dr.Root 'Tools\AutopilotAuditor\WIS-AutopilotAuditor.exe'),
                (Join-Path $dr.Root ('sources\' + [char]36 + 'OEM\' + [char]36 + '1\setup\WIS-AutopilotAuditor.exe')),
                (Join-Path $dr.Root ('sources\' + [char]36 + 'OEM\' + [char]36 + '1\setup\Skripte\WIS-AutopilotAuditor.exe'))
            )
            $ps1Cands += @(
                (Join-Path $dr.Root 'Provera_autopilota.ps1'),
                (Join-Path $dr.Root 'Skripte\Provera_autopilota.ps1'),
                (Join-Path $dr.Root ('sources\' + [char]36 + 'OEM\' + [char]36 + '1\setup\Provera_autopilota.ps1')),
                (Join-Path $dr.Root ('sources\' + [char]36 + 'OEM\' + [char]36 + '1\setup\Skripte\Provera_autopilota.ps1'))
            )
        }
        $apAuditorExe = $exeCands | Where-Object { Test-Path $_ } | Select-Object -First 1
        $apAuditorPs1 = $ps1Cands | Where-Object { Test-Path $_ } | Select-Object -First 1

        $apJsonPayload = $null
        $tmpApOut = Join-Path $SetupDir ('preoobe_ap_out_{0}.tmp' -f $PID)
        $tmpApErr = Join-Path $SetupDir ('preoobe_ap_err_{0}.tmp' -f $PID)
        try {
            $apProc = $null
            if ($apAuditorExe) {
                Write-Log ('Pokrecem Pre-OOBE Autopilot auditor: {0} --json --silent' -f $apAuditorExe) 'INFO' 'AutopilotPreOOBE'
                $apProc = Start-Process -FilePath $apAuditorExe -ArgumentList '--json --silent' -RedirectStandardOutput $tmpApOut -RedirectStandardError $tmpApErr -PassThru -WindowStyle Hidden
            } elseif ($apAuditorPs1) {
                Write-Log ('Pokrecem Pre-OOBE Autopilot skriptu: {0} -JsonOutput' -f $apAuditorPs1) 'INFO' 'AutopilotPreOOBE'
                $apProc = Start-Process -FilePath 'powershell.exe' -ArgumentList ('-NoProfile -ExecutionPolicy Bypass -File "{0}" -JsonOutput' -f $apAuditorPs1) -RedirectStandardOutput $tmpApOut -RedirectStandardError $tmpApErr -PassThru -WindowStyle Hidden
            } else {
                Write-Log 'Ni WIS-AutopilotAuditor.exe ni Provera_autopilota.ps1 nisu pronadjeni za Pre-OOBE proveru.' 'WARN' 'AutopilotPreOOBE'
            }

            if ($apProc) {
                if (-not $apProc.WaitForExit(55000)) {
                    try { $apProc.Kill() } catch {}
                    Write-Log 'Pre-OOBE Autopilot auditor prekoracio 55s timeout.' 'WARN' 'AutopilotPreOOBE'
                }
                if (Test-Path $tmpApOut) {
                    $rawOut = [System.IO.File]::ReadAllText($tmpApOut, $script:Utf8NoBom)
                    if ($rawOut) {
                        $firstB = $rawOut.IndexOf('{')
                        $lastB = $rawOut.LastIndexOf('}')
                        if ($firstB -ge 0 -and $lastB -gt $firstB) {
                            $candidateJson = $rawOut.Substring($firstB, $lastB - $firstB + 1).Trim()
                            $parsedCheck = $candidateJson | ConvertFrom-Json -ErrorAction SilentlyContinue
                            if ($parsedCheck -and $parsedCheck.matrix_code) {
                                $apJsonPayload = $candidateJson
                            }
                        }
                    }
                }
            }
        } finally {
            Remove-Item -Path $tmpApOut, $tmpApErr -Force -ErrorAction SilentlyContinue
        }

        if ($apJsonPayload) {
            $apParsedInfo = $apJsonPayload | ConvertFrom-Json -ErrorAction SilentlyContinue
            $apMatrix = if ($apParsedInfo -and $apParsedInfo.matrix_code) { "$($apParsedInfo.matrix_code)".ToUpper().Trim() } else { 'UNKNOWN' }
            $apTenantLog = if ($apParsedInfo -and $apParsedInfo.tenant_domain) { $apParsedInfo.tenant_domain } else { 'N/A' }
            $apLocked = ($apMatrix -in @('LOCKED_TENANT', 'HIDDEN_LOCK')) -or ($apParsedInfo -and $apParsedInfo.is_locked -eq $true)
            $apFree   = ($apParsedInfo -and $apParsedInfo.cloud_ztd -and $apParsedInfo.cloud_ztd.confirmed_free -eq $true -and (-not $apLocked))
            $newConfirmed = $apLocked -or $apFree

            Write-PermanentAutopilotReport -JsonPayload $apJsonPayload -IsConfirmed $newConfirmed -Component 'AutopilotPreOOBE'
            Write-Log ('Pre-OOBE Autopilot izvestaj sacuvan (matrix_code={0}, tenant={1}) u {2}\autopilot_report.json i {3}\autopilot_report.json' -f $apMatrix, $apTenantLog, $LogDir, $SetupDir) 'SUCCESS' 'AutopilotPreOOBE'

            $hubPayload = [PSCustomObject]@{
                matrix_code          = $apMatrix
                status_level         = if ($apParsedInfo.status_level) { "$($apParsedInfo.status_level)" } else { 'WARNING' }
                binary_verdict       = if ($apLocked) { 'IMA_AUTOPILOT' } elseif ($apFree) { 'NEMA_AUTOPILOT' } else { 'NIJE_PROVERENO' }
                is_locked            = $apLocked
                confirmed_free       = $apFree
                tenant_domain        = if ($apParsedInfo.tenant_domain) { "$($apParsedInfo.tenant_domain)" } else { '' }
                tenant_id            = if ($apParsedInfo.tenant_id) { "$($apParsedInfo.tenant_id)" } else { '' }
                assigned_device_name = if ($apParsedInfo.assigned_device_name) { "$($apParsedInfo.assigned_device_name)" } else { '' }
                query_mode           = if ($apParsedInfo.cloud_ztd -and $apParsedInfo.cloud_ztd.query_mode) { "$($apParsedInfo.cloud_ztd.query_mode)" } else { 'SETUP_COMPLETE_PREOOBE' }
                has_local_traces     = if ($apParsedInfo.local_forensics) { [bool]$apParsedInfo.local_forensics.has_traces } else { $false }
                source               = 'oobe_ztd'
            }
            Send-AutopilotVerdictToHub -PayloadObj $hubPayload -Component 'AutopilotPreOOBE' | Out-Null
        } else {
            Write-Log 'Pre-OOBE Autopilot auditor nije vratio validan JSON izvestaj.' 'WARN' 'AutopilotPreOOBE'
        }

        Copy-PrimaryZtdBackup | Out-Null
    } finally {
        # Bezbedan fallback NAKON zavrsene ZTD provere: instalacija nikad ne ostaje blokirana u OOBE ekranu
        Enable-SafeLocalServiceTransition -Reason 'SetupComplete ZTD provera zavrsena'
    }
} catch {
    Write-Log ('Izuzetak u Pre-OOBE Autopilot koraku: {0}' -f $_.Exception.Message) 'WARN' 'AutopilotPreOOBE'
} finally {
    # Bezuslovno uklanjanje privremenog SYSTEM ONSTART OOBE ZTD watcher zadatka i lock fajla (SEC-07)
    try { & schtasks.exe /Delete /TN "WIS_OOBE_ZTD_Watcher" /F >$null 2>&1 } catch {}
    try {
        $cleanupWatcherLock = Join-Path $LogDir 'oobe_ztd_watcher.lock'
        if (Test-Path -LiteralPath $cleanupWatcherLock -ErrorAction SilentlyContinue) {
            Remove-Item -LiteralPath $cleanupWatcherLock -Force -ErrorAction SilentlyContinue
        }
    } catch {}
}

# 7. Jedinstvena tiha detekcija i ekstrakcija Programi.tar sa proverom izlaza i sadrzaja (Nalaz 1)
Write-Log 'Tiha detekcija i ekstrakcija Programi.tar pomocu ugradjenog tar.exe...' 'STEP' 'TarDetection'
$tarPath = $null
$localTar = Join-Path $SetupDir 'Programi.tar'
if (Test-Path $localTar) {
    $tarPath = $localTar
} else {
    foreach ($dr in $allDrives) {
        $candidates = @(
            (Join-Path $dr.Root 'Programi.tar'),
            (Join-Path $dr.Root ('sources\' + [char]36 + 'OEM\' + [char]36 + '1\setup\Programi.tar')),
            (Join-Path $dr.Root 'setup\Programi.tar'),
            (Join-Path $dr.Root 'Programi\Programi.tar')
        )
        foreach ($cand in $candidates) {
            if (Test-Path $cand) {
                $tarPath = $cand
                break
            }
        }
        if ($tarPath) { break }
    }
}

if ($tarPath) {
    $tarMarker = Join-Path $SetupDir '.tar_extracted'
    $programiDir = Join-Path $SetupDir 'Programi'
    $alreadyExtracted = (Test-Path $tarMarker) -and (Test-Path $programiDir) -and (@(Get-ChildItem -Path $programiDir -Force -ErrorAction SilentlyContinue).Count -gt 0)

    if ($alreadyExtracted) {
        Write-Log ('Programi.tar ({0}) je vec prethodno raspakovan i verifikovan (idempotentno preskakanje).' -f $tarPath) 'INFO' 'TarExtract'
    } else {
        Write-Log ('Pronadjen Programi.tar na: {0}. Pokrecem tar.exe ekstrakciju na {1}...' -f $tarPath, $SetupDir) 'STEP' 'TarExtract'
        $tarOut = (& tar.exe -xf $tarPath -C $SetupDir 2>&1) -join ' '
        $tarExit = $LASTEXITCODE

        $extractedItems = if (Test-Path $programiDir) {
            @(Get-ChildItem -Path $programiDir -Force -ErrorAction SilentlyContinue)
        } else {
            @(Get-ChildItem -Path $SetupDir -Force -ErrorAction SilentlyContinue | Where-Object {
                $_.Name -notin @('Programi.tar', 'setupcomplete.log', 'Wifi-Servis.ini', '.tar_extracted')
            })
        }
        $contentValid = ($extractedItems.Count -gt 0)

        if ($tarExit -eq 0 -and $contentValid) {
            try {
                [System.IO.File]::WriteAllText($tarMarker, (Get-Date).ToString('o'), $script:Utf8NoBom)
            } catch {}
            Write-Log ('Programi.tar uspesno raspakovan i verifikovan na {0} (ExitCode=0, Stavki={1})' -f $SetupDir, $extractedItems.Count) 'SUCCESS' 'TarExtract'
        } else {
            Register-CriticalFailure ('Greska pri raspakivanju Programi.tar sa {0}: ExitCode={1}, ContentValid={2}, Izlaz={3}' -f $tarPath, $tarExit, $contentValid, $tarOut) 'TarExtract'
        }
    }
} else {
    Write-Log 'Programi.tar nije pronadjen (Lean/Golden ISO profil - korak se tiho preskace za 0 sekundi).' 'INFO' 'TarDetection'
}

# 8. Verifikacija kljucnih fajlova i RunOnce registracija
Write-Log ('Verifikacija prisustva kljucnih fajlova u {0}...' -f $SetupDir) 'STEP' 'Verification'
$launcherExe = Join-Path $SetupDir 'WIS.Launcher.exe'
$chkLauncher = Test-Path $launcherExe
$chkWifi     = Test-Path $localWifiIni
$leftoverXml = Join-Path $SetupDir 'Wifi-Servis.xml'
if (Test-Path $leftoverXml) {
    Remove-Item -Path $leftoverXml -Force -ErrorAction SilentlyContinue
}
$chkXml    = Test-Path $leftoverXml
$chkOffice = Test-Path (Join-Path $SetupDir 'Programi\Office 2024')

$lStatus = if ($chkLauncher) { 'PRISUTAN' } else { 'NEDOSTAJE' }
$lLvl    = if ($chkLauncher) { 'SUCCESS' } else { 'WARN' }
Write-Log ('Launcher (WIS): {0}' -f $lStatus) $lLvl 'Verification'

$wStatus = if ($chkWifi) { 'PRISUTAN' } else { 'NEDOSTAJE' }
Write-Log ('Fajl {0}: {1}' -f $localWifiIni, $wStatus) 'INFO' 'Verification'

$xStatus = if (-not $chkXml) { 'UKLONJEN (Anti-Leak)' } else { 'ZAOSTAO' }
$xLvl    = if (-not $chkXml) { 'SUCCESS' } else { 'WARN' }
Write-Log ('Privremeni fajl {0}: {1}' -f $leftoverXml, $xStatus) $xLvl 'Verification'

$oStatus = if ($chkOffice) { 'PRISUTAN' } else { 'NEDOSTAJE' }
$oLvl    = if ($chkOffice) { 'SUCCESS' } else { 'WARN' }
Write-Log ('Folder Programi\Office 2024: {0}' -f $oStatus) $oLvl 'Verification'

Write-Log 'Registracija HKLM RunOnce kljuca za garantovani auto-start Launchera...' 'STEP' 'RunOnce'
& reg.exe add 'HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce' /v 'WIS_Launcher' /t REG_SZ /d $launcherExe /f >$null 2>&1
$runOnceExit = $LASTEXITCODE
if ($runOnceExit -eq 0) {
    Write-Log 'HKLM RunOnce kljuc WIS_Launcher uspesno postavljen.' 'SUCCESS' 'RunOnce'
} else {
    Register-CriticalFailure ('Neuspesna registracija HKLM RunOnce kljuca WIS_Launcher (ExitCode={0}).' -f $runOnceExit) 'RunOnce'
}

# 9. Zavrsni status i sinhronizacija logova
if ($script:CriticalFailure) {
    $errSummary = $script:CriticalErrors -join ' | '
    Write-Log ('=== SetupComplete.ps1 ZAVRSEN SA KRITICNIM GRESKAMA: {0} ===' -f $errSummary) 'ERROR' 'SetupComplete'
    Sync-SetupCompleteLogToUsb
    exit 1
} else {
    Write-Log '=== SetupComplete.ps1 uspesno kompletiran ===' 'SUCCESS' 'SetupComplete'
    Sync-SetupCompleteLogToUsb
    exit 0
}
