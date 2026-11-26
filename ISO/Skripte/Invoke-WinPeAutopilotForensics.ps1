<#
.SYNOPSIS
    WIS WinPE Pre-Format Autopilot Forensics (IMP-114 Sloj B)
.DESCRIPTION
    Izvrsava se u WinPE fazi PRE formatiranja diska i pokretanja Windows Setup-a.
    Proverava zatecenu Windows particiju (\Windows\System32\config\SOFTWARE i SYSTEM
    preko bezbednog reg load + obaveznog reg unload u finally bloku, plus
    \Windows\ServiceState\wmansvc\AutopilotDDSZTDFile.json i \Windows\Provisioning\Autopilot\),
    cuva nalaz u privremenu evidenciju (X:\wis_winpe_autopilot.json) i salje ga po
    serijskom broju (SN) na ServiceHub (POST /api/devices/<sn>/autopilot) sa
    X-ServisHub-Client-Token zaglavljem iz [ServisHub] sekcije Wifi-Servis.ini bez
    logovanja lozinki i tokena.
.COMPATIBILITY
    Windows PowerShell 5.1 (WinPE & Windows)
#>

[CmdletBinding()]
param(
    [string]$OutputPath = "X:\wis_winpe_autopilot.json",
    [string]$LogPath = "X:\wis_preboot.log",
    [string[]]$CandidateRoots = @(),
    [string]$HubUrl = "",
    [string]$WifiIniPath = "",
    [switch]$SkipUpload
)

$ErrorActionPreference = 'Continue'
$script:Utf8NoBom = New-Object System.Text.UTF8Encoding($false)
$script:ZeroGuid = '00000000-0000-0000-0000-000000000000'
$script:WaadRegex = '\bW-AAD-\d{9}\b'
$script:GuidRegex = '[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}'

function Write-WinPeForensicsLog {
    param(
        [string]$Message,
        [string]$Level = 'INFO'
    )
    $sanitized = if ($null -eq $Message) { '' } else { "$Message" }
    $sanitized = $sanitized -replace '(?i)(ClientToken\s*[:=]\s*)([^\s;,\r\n"''&]+)', '$1***MASKIRANO***'
    $sanitized = $sanitized -replace '(?i)(Password\s*[:=]\s*)([^\s;,\r\n"''&]+)', '$1***MASKIRANO***'
    $sanitized = $sanitized -replace '(?i)(X-ServisHub-Client-Token\s*[:=]\s*)([^\s;,\r\n"'']+)', '$1***MASKIRANO***'
    $sanitized = $sanitized -replace '(?i)(Authorization\s*[:=]\s*Bearer\s+)([^\s;,\r\n"'']+)', '$1***MASKIRANO***'

    $ts = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss')
    $line = "[$ts] [$Level] [WinPE-Forensics] $sanitized"
    try {
        $dir = Split-Path $LogPath -Parent
        if ($dir -and -not (Test-Path $dir)) {
            New-Item -Path $dir -ItemType Directory -Force -ErrorAction SilentlyContinue | Out-Null
        }
        [System.IO.File]::AppendAllText($LogPath, $line + [Environment]::NewLine, $script:Utf8NoBom)
    } catch { }
}

function Test-NonZeroGuid {
    param([string]$Candidate)
    if ([string]::IsNullOrWhiteSpace($Candidate)) { return $false }
    $clean = $Candidate.Trim().Trim('{', '}')
    if ($clean -ieq $script:ZeroGuid) { return $false }
    return ($clean -match "^$script:GuidRegex$")
}

function Get-WinPeHardwareIdentity {
    $info = [PSCustomObject]@{
        SerialNumber = ''
        Model        = ''
        Manufacturer = ''
    }

    try {
        $bios = Get-CimInstance -ClassName Win32_BIOS -ErrorAction SilentlyContinue
        if (-not $bios) {
            $bios = Get-WmiObject -Class Win32_BIOS -ErrorAction SilentlyContinue
        }
        if ($bios -and $bios.SerialNumber) {
            $info.SerialNumber = "$($bios.SerialNumber)".Trim()
        }
    } catch { }

    if ([string]::IsNullOrWhiteSpace($info.SerialNumber)) {
        try {
            $wmicOut = & wmic.exe bios get serialnumber /value 2>$null
            foreach ($wl in $wmicOut) {
                if ($wl -match '^\s*SerialNumber\s*=\s*(.+)$') {
                    $info.SerialNumber = $matches[1].Trim()
                    break
                }
            }
        } catch { }
    }

    try {
        $cs = Get-CimInstance -ClassName Win32_ComputerSystem -ErrorAction SilentlyContinue
        if (-not $cs) {
            $cs = Get-WmiObject -Class Win32_ComputerSystem -ErrorAction SilentlyContinue
        }
        if ($cs) {
            if ($cs.Model)        { $info.Model        = "$($cs.Model)".Trim() }
            if ($cs.Manufacturer) { $info.Manufacturer = "$($cs.Manufacturer)".Trim() }
        }
    } catch { }

    # Odbaci genericke OEM placeholder serijske brojeve
    $invalidSerials = @('', '0', 'NONE', 'DEFAULT STRING', 'TO BE FILLED BY O.E.M.', 'SYSTEM SERIAL NUMBER', 'NOT SPECIFIED', 'N/A')
    if ($invalidSerials -contains $info.SerialNumber.ToUpper()) {
        $info.SerialNumber = ''
    }

    return $info
}

function Read-ServisHubIniConfig {
    param(
        [string]$ExplicitIniPath = '',
        [string]$ExplicitHubUrl = ''
    )
    $cfg = [PSCustomObject]@{
        Url         = 'http://192.168.170.254:8080'
        ClientToken = ''
        SourcePath  = ''
    }

    if (-not [string]::IsNullOrWhiteSpace($ExplicitHubUrl)) {
        $cfg.Url = $ExplicitHubUrl.Trim().TrimEnd('/')
    }

    $iniCandidates = New-Object System.Collections.ArrayList
    if (-not [string]::IsNullOrWhiteSpace($ExplicitIniPath)) {
        [void]$iniCandidates.Add($ExplicitIniPath)
    }
    foreach ($p in @(
        'X:\Windows\System32\Wifi-Servis.ini',
        'X:\Wifi-Servis.ini',
        'Y:\Wifi-Servis.ini',
        'Z:\win11\Wifi-Servis.ini',
        'Z:\Wifi-Servis.ini'
    )) {
        [void]$iniCandidates.Add($p)
    }
    try {
        $fsDrives = Get-PSDrive -PSProvider FileSystem -ErrorAction SilentlyContinue
        foreach ($d in $fsDrives) {
            if ($d.Root -and $d.Root -ine 'X:\') {
                [void]$iniCandidates.Add((Join-Path $d.Root 'Wifi-Servis.ini'))
                [void]$iniCandidates.Add((Join-Path $d.Root 'Skripte\Wifi-Servis.ini'))
            }
        }
    } catch { }

    $foundIni = $null
    foreach ($cand in $iniCandidates) {
        if ($cand -and (Test-Path -LiteralPath $cand -PathType Leaf -ErrorAction SilentlyContinue)) {
            $foundIni = $cand
            break
        }
    }

    if ($foundIni) {
        $cfg.SourcePath = $foundIni
        try {
            $lines = Get-Content -LiteralPath $foundIni -ErrorAction Stop
            $inHubSection = $false
            foreach ($rawLine in $lines) {
                $line = $rawLine.Trim()
                if (-not $line -or $line.StartsWith('#') -or $line.StartsWith(';')) { continue }
                if ($line -match '^\[([^\]]+)\]$') {
                    $secName = $matches[1].Trim()
                    $inHubSection = ($secName -ieq 'ServisHub')
                    continue
                }
                if (-not $inHubSection) { continue }
                if ($line -match '^\s*Url\s*=\s*(.+)$' -and [string]::IsNullOrWhiteSpace($ExplicitHubUrl)) {
                    $u = $matches[1].Trim().TrimEnd('/')
                    if ($u -match '^https?://[A-Za-z0-9.\-:]+(/.*)?$') {
                        $cfg.Url = $u
                    }
                } elseif ($line -match '^\s*ClientToken\s*=\s*(.+)$') {
                    $cfg.ClientToken = $matches[1].Trim()
                }
            }
        } catch {
            Write-WinPeForensicsLog "Napomena pri citanju [ServisHub] sekcije iz INI fajla: $($_.Exception.Message)" 'WARN'
        }
    }

    return $cfg
}

function Invoke-SafeRegUnload {
    param([Parameter(Mandatory=$true)][string]$HiveMountKey)
    [GC]::Collect()
    [GC]::WaitForPendingFinalizers()
    for ($attempt = 1; $attempt -le 4; $attempt++) {
        & reg.exe unload $HiveMountKey >$null 2>&1
        if ($LASTEXITCODE -eq 0) {
            return $true
        }
        Start-Sleep -Milliseconds 300
        [GC]::Collect()
        [GC]::WaitForPendingFinalizers()
    }
    Write-WinPeForensicsLog "Upozorenje: reg unload za $HiveMountKey nije vratio ExitCode=0 posle 4 pokusaja." 'WARN'
    return $false
}

function Add-ForensicsEvidence {
    param($Finding, [string]$Note)
    if ($null -eq $Finding -or [string]::IsNullOrWhiteSpace($Note)) { return }
    if (-not $Finding.Evidence.Contains($Note)) {
        [void]$Finding.Evidence.Add($Note)
    }
}

function Inspect-TextForAutopilotMarkers {
    param(
        [string]$RawText,
        $Finding,
        [string]$SourceTag
    )
    if ([string]::IsNullOrWhiteSpace($RawText)) { return }

    if ($RawText -match '"CloudAssignedTenantId"\s*:\s*"([^"]+)"') {
        $tid = $matches[1].Trim().Trim('{', '}')
        if (Test-NonZeroGuid -Candidate $tid) {
            $Finding.HasStrongAutopilot = $true
            $Finding.HasLocalTraces = $true
            if (-not $Finding.TenantId) { $Finding.TenantId = $tid }
            Add-ForensicsEvidence -Finding $Finding -Note "${SourceTag}:CloudAssignedTenantId"
        }
    }
    if ($RawText -match '"CloudAssignedTenantDomain"\s*:\s*"([^"]+)"') {
        $dom = $matches[1].Trim()
        if ($dom) {
            $Finding.HasStrongAutopilot = $true
            $Finding.HasLocalTraces = $true
            if (-not $Finding.TenantDomain) { $Finding.TenantDomain = $dom }
            Add-ForensicsEvidence -Finding $Finding -Note "${SourceTag}:CloudAssignedTenantDomain"
        }
    }
    if ($RawText -match '"CloudAssignedDeviceName"\s*:\s*"([^"]+)"') {
        $dev = $matches[1].Trim()
        if ($dev) {
            $Finding.HasLocalTraces = $true
            if (-not $Finding.AssignedDeviceName) { $Finding.AssignedDeviceName = $dev }
            if ($dev -match $script:WaadRegex) {
                $Finding.HasStrongAutopilot = $true
            }
            Add-ForensicsEvidence -Finding $Finding -Note "${SourceTag}:CloudAssignedDeviceName=$dev"
        }
    }
    if ($RawText -match $script:WaadRegex) {
        $waad = $matches[0].ToUpper()
        $Finding.HasStrongAutopilot = $true
        $Finding.HasLocalTraces = $true
        if (-not $Finding.AssignedDeviceName) { $Finding.AssignedDeviceName = $waad }
        Add-ForensicsEvidence -Finding $Finding -Note "${SourceTag}:W-AAD=$waad"
    }
}

function Scan-OfflineRegistrySubtree {
    param(
        [string]$PsRegPath,
        $Finding,
        [string]$Tag,
        [switch]$StrongOnAnyEntry
    )
    if (-not (Test-Path -LiteralPath $PsRegPath -ErrorAction SilentlyContinue)) { return 0 }
    $count = 0
    $regItem = $null
    try {
        $regItem = Get-Item -LiteralPath $PsRegPath -ErrorAction SilentlyContinue
        if ($regItem) {
            foreach ($vn in $regItem.GetValueNames()) {
                $val = $regItem.GetValue($vn)
                if ($null -eq $val) { continue }
                $sVal = "$val".Trim()
                if ($sVal -eq '') { continue }
                $count++

                if ($vn -ieq 'CloudAssignedTenantId' -or $vn -ieq 'TenantId') {
                    if (Test-NonZeroGuid -Candidate $sVal) {
                        $Finding.HasStrongAutopilot = $true
                        $Finding.HasLocalTraces = $true
                        if (-not $Finding.TenantId) { $Finding.TenantId = $sVal.Trim('{', '}') }
                        Add-ForensicsEvidence -Finding $Finding -Note "$Tag\$vn"
                    }
                } elseif ($vn -ieq 'CloudAssignedTenantDomain' -or $vn -ieq 'TenantDomain') {
                    $Finding.HasStrongAutopilot = $true
                    $Finding.HasLocalTraces = $true
                    if (-not $Finding.TenantDomain) { $Finding.TenantDomain = $sVal }
                    Add-ForensicsEvidence -Finding $Finding -Note "$Tag\$vn"
                } elseif ($vn -ieq 'CloudAssignedDeviceName') {
                    $Finding.HasLocalTraces = $true
                    if (-not $Finding.AssignedDeviceName) { $Finding.AssignedDeviceName = $sVal }
                    if ($sVal -match $script:WaadRegex) { $Finding.HasStrongAutopilot = $true }
                    Add-ForensicsEvidence -Finding $Finding -Note "$Tag\$vn=$sVal"
                } else {
                    Inspect-TextForAutopilotMarkers -RawText $sVal -Finding $Finding -SourceTag "$Tag\$vn"
                }
            }
        }

        $children = @(Get-ChildItem -LiteralPath $PsRegPath -ErrorAction SilentlyContinue)
        foreach ($ch in $children) {
            $count++
            Inspect-TextForAutopilotMarkers -RawText $ch.PSChildName -Finding $Finding -SourceTag "$Tag\$($ch.PSChildName)"
            $childItem = $null
            try {
                $childItem = Get-Item -LiteralPath $ch.PSPath -ErrorAction SilentlyContinue
                if ($childItem) {
                    foreach ($cvn in $childItem.GetValueNames()) {
                        $cval = $childItem.GetValue($cvn)
                        if ($null -ne $cval -and "$cval".Trim() -ne '') {
                            $count++
                            Inspect-TextForAutopilotMarkers -RawText ("$cval".Trim()) -Finding $Finding -SourceTag "$Tag\$($ch.PSChildName)\$cvn"
                        }
                    }
                }
            } finally {
                if ($childItem -and ($childItem -is [IDisposable])) {
                    try { $childItem.Dispose() } catch { }
                }
            }
        }

        if ($count -gt 0) {
            $Finding.HasLocalTraces = $true
            if ($StrongOnAnyEntry) {
                $Finding.HasStrongAutopilot = $true
            }
            Add-ForensicsEvidence -Finding $Finding -Note "$Tag($count)"
        }
    } catch { }
    finally {
        if ($regItem -and ($regItem -is [IDisposable])) {
            try { $regItem.Dispose() } catch { }
        }
    }
    return $count
}

function Find-ExistingWindowsRoots {
    param([string[]]$ExplicitRoots)
    $roots = New-Object System.Collections.ArrayList

    if ($ExplicitRoots -and $ExplicitRoots.Count -gt 0) {
        foreach ($r in $ExplicitRoots) {
            if ($r -and (Test-Path -LiteralPath $r -ErrorAction SilentlyContinue)) {
                [void]$roots.Add($r)
            }
        }
        return ,$roots.ToArray()
    }

    try {
        $drives = [System.IO.DriveInfo]::GetDrives()
        foreach ($drv in $drives) {
            try {
                if (-not $drv.IsReady) { continue }
                # Preskoci WinPE RAM X:\, CD-ROM i mrezne share-ove
                if ($drv.Name -ieq 'X:\') { continue }
                if ($drv.DriveType -eq [System.IO.DriveType]::CDRom -or $drv.DriveType -eq [System.IO.DriveType]::Network) { continue }

                $winDir = Join-Path $drv.Name 'Windows'
                $swHive = Join-Path $winDir 'System32\config\SOFTWARE'
                $wmanZtd = Join-Path $winDir 'ServiceState\wmansvc\AutopilotDDSZTDFile.json'
                $provDir = Join-Path $winDir 'Provisioning\Autopilot'

                if ((Test-Path -LiteralPath $swHive -ErrorAction SilentlyContinue) -or
                    (Test-Path -LiteralPath $wmanZtd -ErrorAction SilentlyContinue) -or
                    (Test-Path -LiteralPath $provDir -ErrorAction SilentlyContinue)) {
                    [void]$roots.Add($drv.Name)
                }
            } catch { }
        }
    } catch { }

    return ,$roots.ToArray()
}

# ==============================================================================
# GLAVNI TOK: WINPE PRE-FORMAT FORENZIKA
# ==============================================================================
Write-WinPeForensicsLog "Pokrecem WinPE pre-format Autopilot forenziku (IMP-114 Sloj B)..." 'INFO'

$hw = Get-WinPeHardwareIdentity
Write-WinPeForensicsLog ("Hardver: Manufacturer='{0}', Model='{1}', SN='{2}'" -f $hw.Manufacturer, $hw.Model, $(if($hw.SerialNumber){$hw.SerialNumber}else{'Nepoznato'})) 'INFO'

$finding = [PSCustomObject]@{
    ScannedPartitions  = New-Object System.Collections.ArrayList
    HasStrongAutopilot = $false
    HasLocalTraces     = $false
    TenantDomain       = ''
    TenantId           = ''
    AssignedDeviceName = ''
    Evidence           = New-Object System.Collections.ArrayList
}

$winRoots = Find-ExistingWindowsRoots -ExplicitRoots $CandidateRoots
if ($winRoots.Count -eq 0) {
    Write-WinPeForensicsLog "Nije pronadjena postojeca Windows particija pre formatiranja (prazan/nov disk)." 'INFO'
} else {
    foreach ($root in $winRoots) {
        [void]$finding.ScannedPartitions.Add($root)
        $winDir = Join-Path $root 'Windows'
        Write-WinPeForensicsLog ("Skeniram postojecu Windows particiju: {0}" -f $root) 'INFO'

        # 1. Primarni Windows 11 ZTD fajlovi (wmansvc & Provisioning\Autopilot) + sekundarni fallback
        $fileChecks = New-Object System.Collections.ArrayList
        [void]$fileChecks.Add((Join-Path $winDir 'ServiceState\wmansvc\AutopilotDDSZTDFile.json'))
        $provApDir = Join-Path $winDir 'Provisioning\Autopilot'
        if (Test-Path -LiteralPath $provApDir -ErrorAction SilentlyContinue) {
            try {
                $provJsons = Get-ChildItem -LiteralPath $provApDir -Filter '*.json' -File -ErrorAction SilentlyContinue
                foreach ($pj in $provJsons) { [void]$fileChecks.Add($pj.FullName) }
            } catch { }
        }
        [void]$fileChecks.Add((Join-Path $winDir 'ServiceState\Autopilot\AutopilotDDSZTDFile.json'))
        [void]$fileChecks.Add((Join-Path $winDir 'Logs\WIS\AutopilotDDSZTDFile.preoobe.json'))
        [void]$fileChecks.Add((Join-Path $winDir 'Logs\WIS\autopilot_report.json'))

        foreach ($fPath in $fileChecks) {
            if (Test-Path -LiteralPath $fPath -PathType Leaf -ErrorAction SilentlyContinue) {
                try {
                    $rawTxt = [System.IO.File]::ReadAllText($fPath, $script:Utf8NoBom)
                    Inspect-TextForAutopilotMarkers -RawText $rawTxt -Finding $finding -SourceTag (Split-Path $fPath -Leaf)
                } catch { }
            }
        }

        # 2. Offline SOFTWARE hive (reg load + OBAVEZAN reg unload u finally bloku!)
        $swHiveFile = Join-Path $winDir 'System32\config\SOFTWARE'
        if (Test-Path -LiteralPath $swHiveFile -PathType Leaf -ErrorAction SilentlyContinue) {
            $swMountKey = 'HKLM\WIS_OFFLINE_SW'
            $swPsRoot = 'Registry::HKEY_LOCAL_MACHINE\WIS_OFFLINE_SW'
            $swLoaded = $false
            try {
                # Obezbedi cist mount ako je zaostao iz prethodnog pokusaja
                if (Test-Path -LiteralPath $swPsRoot -ErrorAction SilentlyContinue) {
                    Invoke-SafeRegUnload -HiveMountKey $swMountKey | Out-Null
                }
                & reg.exe load $swMountKey $swHiveFile >$null 2>&1
                if ($LASTEXITCODE -eq 0) {
                    $swLoaded = $true
                    Write-WinPeForensicsLog ("Offline SOFTWARE hive uspesno montiran ({0})." -f $swHiveFile) 'INFO'

                    Scan-OfflineRegistrySubtree -PsRegPath "$swPsRoot\Microsoft\Provisioning\Diagnostics\AutoPilot" -Finding $finding -Tag 'SOFTWARE\Diagnostics\AutoPilot' | Out-Null
                    Scan-OfflineRegistrySubtree -PsRegPath "$swPsRoot\Microsoft\Provisioning\Diagnostics\AutoPilot\EstablishedCorrelations" -Finding $finding -Tag 'SOFTWARE\EstablishedCorrelations' -StrongOnAnyEntry | Out-Null
                    Scan-OfflineRegistrySubtree -PsRegPath "$swPsRoot\Microsoft\Provisioning\AutopilotPolicyCache" -Finding $finding -Tag 'SOFTWARE\AutopilotPolicyCache' | Out-Null
                    Scan-OfflineRegistrySubtree -PsRegPath "$swPsRoot\Microsoft\Provisioning\AutopilotSettings" -Finding $finding -Tag 'SOFTWARE\AutopilotSettings' | Out-Null
                    Scan-OfflineRegistrySubtree -PsRegPath "$swPsRoot\Microsoft\Provisioning\EnterpriseAssignedAccess" -Finding $finding -Tag 'SOFTWARE\EnterpriseAssignedAccess' | Out-Null
                    Scan-OfflineRegistrySubtree -PsRegPath "$swPsRoot\Microsoft\Windows\Autopilot\EnrollmentStatusTracking" -Finding $finding -Tag 'SOFTWARE\EnrollmentStatusTracking' | Out-Null
                    Scan-OfflineRegistrySubtree -PsRegPath "$swPsRoot\Microsoft\Provisioning\EnrollmentStatusTracking" -Finding $finding -Tag 'SOFTWARE\Provisioning\EnrollmentStatusTracking' | Out-Null

                    # Provera MDM Enrollments
                    $enrollRoot = "$swPsRoot\Microsoft\Enrollments"
                    if (Test-Path -LiteralPath $enrollRoot -ErrorAction SilentlyContinue) {
                        $eSubs = @(Get-ChildItem -LiteralPath $enrollRoot -ErrorAction SilentlyContinue)
                        foreach ($es in $eSubs) {
                            if ($es.PSChildName -match 'ZTD') {
                                $finding.HasLocalTraces = $true
                                Add-ForensicsEvidence -Finding $finding -Note "SOFTWARE\Enrollments\$($es.PSChildName)"
                            }
                            $eProps = Get-ItemProperty -LiteralPath $es.PSPath -ErrorAction SilentlyContinue
                            if ($eProps -and $eProps.DiscoveryServiceFullURL) {
                                $finding.HasLocalTraces = $true
                                Add-ForensicsEvidence -Finding $finding -Note "SOFTWARE\Enrollments\MDM_URL"
                            }
                        }
                    }
                } else {
                    Write-WinPeForensicsLog ("reg load za SOFTWARE vratio ExitCode={0}." -f $LASTEXITCODE) 'WARN'
                }
            } catch {
                Write-WinPeForensicsLog ("Izuzetak pri citanju offline SOFTWARE hive-a: {0}" -f $_.Exception.Message) 'WARN'
            } finally {
                if ($swLoaded) {
                    Invoke-SafeRegUnload -HiveMountKey $swMountKey | Out-Null
                    Write-WinPeForensicsLog "Offline SOFTWARE hive bezbedno demontiran (reg unload)." 'INFO'
                }
            }
        }

        # 3. Offline SYSTEM hive (CloudDomainJoin + ComputerName W-AAD-* + OBAVEZAN reg unload u finally!)
        $sysHiveFile = Join-Path $winDir 'System32\config\SYSTEM'
        if (Test-Path -LiteralPath $sysHiveFile -PathType Leaf -ErrorAction SilentlyContinue) {
            $sysMountKey = 'HKLM\WIS_OFFLINE_SYS'
            $sysPsRoot = 'Registry::HKEY_LOCAL_MACHINE\WIS_OFFLINE_SYS'
            $sysLoaded = $false
            try {
                if (Test-Path -LiteralPath $sysPsRoot -ErrorAction SilentlyContinue) {
                    Invoke-SafeRegUnload -HiveMountKey $sysMountKey | Out-Null
                }
                & reg.exe load $sysMountKey $sysHiveFile >$null 2>&1
                if ($LASTEXITCODE -eq 0) {
                    $sysLoaded = $true
                    Write-WinPeForensicsLog ("Offline SYSTEM hive uspesno montiran ({0})." -f $sysHiveFile) 'INFO'

                    $csName = 'ControlSet001'
                    try {
                        $selProps = Get-ItemProperty -LiteralPath "$sysPsRoot\Select" -ErrorAction SilentlyContinue
                        if ($selProps -and $selProps.Current) {
                            $csNum = [int]$selProps.Current
                            $csName = ('ControlSet{0:D3}' -f $csNum)
                        }
                    } catch { }

                    # CloudDomainJoin\JoinInfo
                    $cdjRoot = "$sysPsRoot\$csName\Control\CloudDomainJoin\JoinInfo"
                    if (Test-Path -LiteralPath $cdjRoot -ErrorAction SilentlyContinue) {
                        $cdjSubs = @(Get-ChildItem -LiteralPath $cdjRoot -ErrorAction SilentlyContinue)
                        foreach ($csItem in $cdjSubs) {
                            $jProps = Get-ItemProperty -LiteralPath $csItem.PSPath -ErrorAction SilentlyContinue
                            if ($jProps -and $jProps.TenantId -and (Test-NonZeroGuid -Candidate "$($jProps.TenantId)")) {
                                $finding.HasLocalTraces = $true
                                if (-not $finding.TenantId) { $finding.TenantId = "$($jProps.TenantId)".Trim().Trim('{', '}') }
                                Add-ForensicsEvidence -Finding $finding -Note "SYSTEM\CloudDomainJoin\TenantId"
                            }
                        }
                    }

                    # ComputerName (hvatanje zatecenog W-AAD-<9 cifara> korporativnog imena!)
                    $cnPath = "$sysPsRoot\$csName\Control\ComputerName\ComputerName"
                    if (Test-Path -LiteralPath $cnPath -ErrorAction SilentlyContinue) {
                        $cnProps = Get-ItemProperty -LiteralPath $cnPath -ErrorAction SilentlyContinue
                        if ($cnProps -and $cnProps.ComputerName) {
                            $oldHost = "$($cnProps.ComputerName)".Trim()
                            Inspect-TextForAutopilotMarkers -RawText $oldHost -Finding $finding -SourceTag 'SYSTEM\ComputerName'
                        }
                    }
                } else {
                    Write-WinPeForensicsLog ("reg load za SYSTEM vratio ExitCode={0}." -f $LASTEXITCODE) 'WARN'
                }
            } catch {
                Write-WinPeForensicsLog ("Izuzetak pri citanju offline SYSTEM hive-a: {0}" -f $_.Exception.Message) 'WARN'
            } finally {
                if ($sysLoaded) {
                    Invoke-SafeRegUnload -HiveMountKey $sysMountKey | Out-Null
                    Write-WinPeForensicsLog "Offline SYSTEM hive bezbedno demontiran (reg unload)." 'INFO'
                }
            }
        }
    }
}

# Formiranje kanonskog Autopilot nalaza za privremenu evidenciju i ServiceHub
$matrixCode    = 'OFFLINE_CLEAN_LOCAL'
$statusLevel   = 'WARNING'
$binaryVerdict = 'NIJE_PROVERENO'
$isLocked      = $false

if ($finding.HasStrongAutopilot) {
    $matrixCode    = 'LOCKED_TENANT'
    $statusLevel   = 'CRITICAL'
    $binaryVerdict = 'IMA_AUTOPILOT'
    $isLocked      = $true
} elseif ($finding.HasLocalTraces) {
    $matrixCode    = 'OFFLINE_LOCAL_TRACES'
    $statusLevel   = 'WARNING'
    $binaryVerdict = 'NIJE_PROVERENO'
    $isLocked      = $false
}

$payload = [PSCustomObject]@{
    timestamp            = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
    matrix_code          = $matrixCode
    status_level         = $statusLevel
    binary_verdict       = $binaryVerdict
    is_locked            = $isLocked
    confirmed_free       = $false
    tenant_domain        = $finding.TenantDomain
    tenant_id            = $finding.TenantId
    assigned_device_name = $finding.AssignedDeviceName
    query_mode           = 'WINPE_PREFORMAT_FORENSICS'
    has_local_traces     = [bool]$finding.HasLocalTraces
    source               = 'winpe_preformat'
    device_serial        = $hw.SerialNumber
    device_model         = $hw.Model
    manufacturer         = $hw.Manufacturer
    scanned_partitions   = @($finding.ScannedPartitions.ToArray())
    evidence             = @($finding.Evidence.ToArray())
}

$jsonBody = $payload | ConvertTo-Json -Depth 5

# Snimanje u privremenu evidenciju (X:\wis_winpe_autopilot.json)
try {
    $outDir = Split-Path $OutputPath -Parent
    if ($outDir -and -not (Test-Path $outDir)) {
        New-Item -Path $outDir -ItemType Directory -Force -ErrorAction SilentlyContinue | Out-Null
    }
    [System.IO.File]::WriteAllText($OutputPath, $jsonBody, $script:Utf8NoBom)
    Write-WinPeForensicsLog ("Nalaz sacuvan u {0} (verdict={1}, matrix={2}, tenant={3}, traces={4})." -f $OutputPath, $binaryVerdict, $matrixCode, $(if($finding.TenantDomain){$finding.TenantDomain}else{'N/A'}), $finding.HasLocalTraces) 'INFO'
} catch {
    Write-WinPeForensicsLog ("Neuspesno snimanje nalaza u {0}: {1}" -f $OutputPath, $_.Exception.Message) 'WARN'
}

# Slanje na ServiceHub po serijskom broju: POST /api/devices/<sn>/autopilot
if (-not $SkipUpload) {
    if ([string]::IsNullOrWhiteSpace($hw.SerialNumber)) {
        Write-WinPeForensicsLog "Serijski broj nije ocitan - preskacem upload na ServiceHub." 'WARN'
    } else {
        $hubCfg = Read-ServisHubIniConfig -ExplicitIniPath $WifiIniPath -ExplicitHubUrl $HubUrl
        $encodedSn = [Uri]::EscapeDataString($hw.SerialNumber)
        $endpointUrl = "{0}/api/devices/{1}/autopilot" -f $hubCfg.Url.TrimEnd('/'), $encodedSn
        $hasToken = -not [string]::IsNullOrWhiteSpace($hubCfg.ClientToken)
        Write-WinPeForensicsLog ("Saljem WinPE pre-format nalaz na {0} (ClientToken={1})..." -f $endpointUrl, $(if($hasToken){'PRISUTAN'}else{'NEMA'})) 'INFO'

        try {
            $req = [System.Net.HttpWebRequest]::Create($endpointUrl)
            $req.Method = 'POST'
            $req.ContentType = 'application/json; charset=utf-8'
            $req.Timeout = 5000
            $req.ReadWriteTimeout = 5000
            if ($hasToken) {
                $req.Headers.Add('X-ServisHub-Client-Token', $hubCfg.ClientToken)
            }
            $bodyBytes = $script:Utf8NoBom.GetBytes($jsonBody)
            $req.ContentLength = $bodyBytes.Length
            $reqStream = $req.GetRequestStream()
            try {
                $reqStream.Write($bodyBytes, 0, $bodyBytes.Length)
            } finally {
                $reqStream.Close()
            }
            $resp = $req.GetResponse()
            try {
                $statusCode = [int]$resp.StatusCode
                Write-WinPeForensicsLog ("ServiceHub primio WinPE Autopilot nalaz (HTTP {0})." -f $statusCode) 'SUCCESS'
            } finally {
                $resp.Close()
            }
        } catch {
            Write-WinPeForensicsLog ("Upload WinPE Autopilot nalaza na ServiceHub nije uspeo (nastavljam instalaciju): {0}" -f $_.Exception.Message) 'WARN'
        }
    }
}

Write-WinPeForensicsLog "WinPE pre-format Autopilot forenzika zavrsena." 'INFO'
exit 0
