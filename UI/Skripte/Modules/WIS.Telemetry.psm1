<#
.MODULE
    WIS.Telemetry.psm1
.DESCRIPTION
    Klijentska telemetrija za komunikaciju sa centralnim servisnim hubom.
    Podrzava rad preko razlicitih podmreza (LAN i WLAN subnetwork) uz usmereni
    broadcast, multi-IP failover i dinamicko prevezivanje.
.COMPATIBILITY
    Windows PowerShell 5.1+
#>

Import-Module (Join-Path $PSScriptRoot 'WIS.StatusContract.psm1')

function Get-WisInstallSessionId {
    <#
    .SYNOPSIS
        Cita ili kreira jedinstveni GUID tekuce instalacione sesije (C:\setup\install-session-id.txt)
        i obezbedjuje marker u servis_deploy.log (IMP-66 / IMP-69).
    #>
    [CmdletBinding()]
    param(
        [string]$LocalSetupDir = "C:\setup",
        [switch]$EnsureLogMarker
    )
    $sessionFile = Join-Path $LocalSetupDir "install-session-id.txt"
    $sessionId = ""
    try {
        if (Test-Path -LiteralPath $sessionFile -PathType Leaf -ErrorAction SilentlyContinue) {
            $raw = (Get-Content -LiteralPath $sessionFile -Raw -Encoding UTF8 -ErrorAction SilentlyContinue)
            if ($raw) { $sessionId = $raw.Trim() }
        }
    } catch {}

    if ([string]::IsNullOrWhiteSpace($sessionId)) {
        $sessionId = [guid]::NewGuid().ToString("N")
        try {
            if (-not (Test-Path -LiteralPath $LocalSetupDir)) {
                New-Item -Path $LocalSetupDir -ItemType Directory -Force -ErrorAction SilentlyContinue | Out-Null
            }
            Set-Content -LiteralPath $sessionFile -Value $sessionId -Encoding UTF8 -Force -ErrorAction SilentlyContinue
        } catch {}
    }

    if ($EnsureLogMarker) {
        $null = Ensure-WisSessionLogMarker -SessionId $sessionId -LocalSetupDir $LocalSetupDir
    }
    return $sessionId
}

function Ensure-WisSessionLogMarker {
    param(
        [string]$SessionId = "",
        [string]$LocalSetupDir = "C:\setup"
    )
    if ([string]::IsNullOrWhiteSpace($SessionId)) {
        $SessionId = Get-WisInstallSessionId -LocalSetupDir $LocalSetupDir
    }
    $marker = "=== WIS_SESSION_START:$SessionId ==="
    foreach ($logCand in @((Join-Path $LocalSetupDir "servis_deploy.log"), "C:\Windows\Logs\WIS\servis_deploy.log")) {
        try {
            $parentDir = Split-Path -Path $logCand -Parent
            if (-not (Test-Path -LiteralPath $parentDir)) { continue }
            $alreadyPresent = $false
            if (Test-Path -LiteralPath $logCand -PathType Leaf -ErrorAction SilentlyContinue) {
                $tail = Get-Content -LiteralPath $logCand -Tail 200 -ErrorAction SilentlyContinue
                if ($tail -and ($tail -contains $marker)) {
                    $alreadyPresent = $true
                }
            }
            if (-not $alreadyPresent) {
                Add-Content -LiteralPath $logCand -Value $marker -Encoding UTF8 -ErrorAction SilentlyContinue
            }
        } catch {}
    }
    return $marker
}

function Get-WisImageProfile {
    <#
    .SYNOPSIS
        IMP-112: Cita profil Windows WIM slike iz C:\Windows\Logs\WIS\image-profile.json
        sa bezbednim fallbackom (image_type="lean", wim_version="unknown").
    #>
    [CmdletBinding()]
    param(
        [string]$ProfilePath = "",
        [string]$SystemLogDir = "C:\Windows\Logs\WIS",
        [string]$LocalSetupDir = "C:\setup"
    )

    $candidates = [System.Collections.Generic.List[string]]::new()
    if (-not [string]::IsNullOrWhiteSpace($ProfilePath)) {
        [void]$candidates.Add($ProfilePath)
    }
    foreach ($c in @(
        (Join-Path $SystemLogDir "image-profile.json"),
        "C:\Windows\Logs\WIS\image-profile.json",
        (Join-Path $LocalSetupDir "image-profile.json")
    )) {
        if (-not [string]::IsNullOrWhiteSpace($c) -and -not $candidates.Contains($c)) {
            [void]$candidates.Add($c)
        }
    }

    foreach ($candPath in $candidates) {
        if (Test-Path -LiteralPath $candPath -PathType Leaf -ErrorAction SilentlyContinue) {
            try {
                $rawJson = Get-Content -LiteralPath $candPath -Raw -Encoding UTF8 -ErrorAction SilentlyContinue
                if (-not [string]::IsNullOrWhiteSpace($rawJson)) {
                    $parsed = ConvertFrom-Json -InputObject $rawJson -ErrorAction SilentlyContinue
                    if ($parsed) {
                        $imgType = "lean"
                        if ($parsed.PSObject.Properties['image_type'] -and -not [string]::IsNullOrWhiteSpace([string]$parsed.image_type)) {
                            $normType = ([string]$parsed.image_type).Trim().ToLowerInvariant()
                            if ($normType -in @("preinstalled", "lean")) {
                                $imgType = $normType
                            }
                        }
                        $wimVer = "unknown"
                        if ($parsed.PSObject.Properties['wim_version'] -and -not [string]::IsNullOrWhiteSpace([string]$parsed.wim_version)) {
                            $wimVer = ([string]$parsed.wim_version).Trim()
                        }
                        $preApps = @()
                        if ($parsed.PSObject.Properties['preinstalled_apps'] -and $parsed.preinstalled_apps) {
                            $preApps = @($parsed.preinstalled_apps | ForEach-Object { ([string]$_).Trim() } | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
                        }
                        $builtAt = ""
                        if ($parsed.PSObject.Properties['built_at'] -and -not [string]::IsNullOrWhiteSpace([string]$parsed.built_at)) {
                            $builtAt = ([string]$parsed.built_at).Trim()
                        }
                        return [PSCustomObject]@{
                            image_type        = $imgType
                            wim_version       = $wimVer
                            preinstalled_apps = $preApps
                            built_at          = $builtAt
                            found             = $true
                            source_path       = $candPath
                        }
                    }
                }
            } catch {}
        }
    }

    return [PSCustomObject]@{
        image_type        = "lean"
        wim_version       = "unknown"
        preinstalled_apps = @()
        built_at          = ""
        found             = $false
        source_path       = ""
    }
}

# Isti statusni ugovor za heartbeat i zavrsni izvestaj; bez HTTP-a.
function Get-WisTelemetryDiagnostics {
    [CmdletBinding()]
    param([string]$LocalSetupDir = 'C:\setup', [string]$SystemLogDir = 'C:\Windows\Logs\WIS')
    $result = $null
    if (Get-Command Get-ServisStressTestResult -ErrorAction SilentlyContinue) {
        $result = Get-ServisStressTestResult -LocalSetupDir $LocalSetupDir
        if (-not $result) { $result = Get-ServisStressTestResult -LocalSetupDir $SystemLogDir }
    }
    $status = if ($result -and $result.status) { [string]$result.status } else { 'UNKNOWN' }
    $sensor = if ($result) { [bool]$result.sensor_available } else { $false }
    $temperature = if ($result -and $null -ne $result.max_cpu_temp_c) { [double]$result.max_cpu_temp_c }
        elseif ($result -and $null -ne $result.max_cpu_temp) { [double]$result.max_cpu_temp } else { 0.0 }
    $abort = if ($result) { [bool]$result.emergency_abort } else { $false }
    $contract = Get-WisDiagnosticsContract -Status $status -SensorAvailable $sensor -MaxCpuTemp $temperature -EmergencyAbort $abort
    @{
        diagnostics_status = $contract.diagnostics_status
        diagnostics_warning = $contract.diagnostics_warning
        cooling_verified = [bool]$contract.cooling_verified
        manual_check_required = [bool]$contract.manual_check_required
        stress_status = $status
        sensor_available = $sensor
    }
}

function New-WisTelemetryJson {
    [CmdletBinding()]
    param([Parameter(Mandatory=$true)][System.Collections.IDictionary]$Payload,
          [hashtable]$CustomData = @{}, [string]$LocalSetupDir = 'C:\setup',
          [string]$SystemLogDir = 'C:\Windows\Logs\WIS', [int]$Depth = 5)
    $merged = [ordered]@{}
    foreach ($key in $Payload.Keys) { $merged[$key] = $Payload[$key] }
    $diagnostics = Get-WisTelemetryDiagnostics -LocalSetupDir $LocalSetupDir -SystemLogDir $SystemLogDir
    foreach ($key in $diagnostics.Keys) { $merged[$key] = $diagnostics[$key] }
    # Phase6 prosledjuje vec uhvacen autoritativni ugovor: radi i nakon cleanup-a.
    foreach ($key in $CustomData.Keys) { $merged[$key] = $CustomData[$key] }
    ConvertTo-Json -InputObject $merged -Compress -Depth $Depth
}


# ------------------------------------------------------------------------------
# MULTI-SUBNET DISCOVERY & CONFIGURATION
# ------------------------------------------------------------------------------

function Find-ServisHub {
    param(
        [string]$LocalSetupDir = "C:\setup",
        [switch]$ForceRefresh
    )

    try {
        if ($Global:ServisHubUrl -and -not $ForceRefresh) {
            return $Global:ServisHubUrl
        }

        # 1. Provera konfiguracionog fajla Wifi-Servis.ini
        $wifiIniCandidates = @(
            (Join-Path $LocalSetupDir "Wifi-Servis.ini"),
            (Join-Path $PSScriptRoot "Wifi-Servis.ini"),
            (Join-Path (Split-Path $PSScriptRoot -Parent) "Skripte\Wifi-Servis.ini"),
            "C:\setup\Wifi-Servis.ini"
        )
        foreach ($d in (Get-PSDrive -PSProvider FileSystem)) {
            $p = Join-Path "$($d.Root)" "Skripte\Wifi-Servis.ini"
            if ($wifiIniCandidates -notcontains $p) { $wifiIniCandidates += $p }
            $p2 = Join-Path "$($d.Root)" "Wifi-Servis.ini"
            if ($wifiIniCandidates -notcontains $p2) { $wifiIniCandidates += $p2 }
        }

        $wifiIni = $wifiIniCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
        $enabled = $true
        $configuredHostRaw = "192.168.170.254,auto"
        $configuredPort = 8080

        if ($wifiIni) {
            try {
                $content = Get-Content $wifiIni -Raw -ErrorAction SilentlyContinue
                if ($content -match '(?mi)^\s*\[ServisHub\]([\s\S]*?)(?:^\s*\[|\Z)') {
                    $hubSection = $matches[1]
                    if ($hubSection -match '(?mi)^\s*Enabled\s*=\s*0') {
                        $enabled = $false
                    }
                    if ($hubSection -match '(?mi)^\s*ServerHost\s*=\s*([^\r\n#;]+)') {
                        $configuredHostRaw = $matches[1].Trim()
                    }
                    if ($hubSection -match '(?mi)^\s*ServerPort\s*=\s*([0-9]+)') {
                        $configuredPort = [int]$matches[1].Trim()
                    }
                } elseif ($content -match '(?mi)^\s*\[GdsServer\]([\s\S]*?)(?:^\s*\[|\Z)') {
                    $gdsSection = $matches[1]
                    if ($gdsSection -match '(?mi)^\s*Enabled\s*=\s*0') {
                        $enabled = $false
                    }
                    if ($gdsSection -match '(?mi)^\s*ServerHost\s*=\s*([^\r\n#;]+)') {
                        $configuredHostRaw = $matches[1].Trim()
                    }
                    if ($gdsSection -match '(?mi)^\s*ServerPort\s*=\s*([0-9]+)') {
                        $configuredPort = [int]$matches[1].Trim()
                    }
                }
            } catch {}
        }

        if (-not $enabled) {
            $Global:ServisHubUrl = $null
            return $null
        }

        # Formiranje liste kandidata za povezivanje
        $candidateIps = [System.Collections.Generic.List[string]]::new()
        $hostTokens = ($configuredHostRaw -split ',') | ForEach-Object { $_.Trim() } | Where-Object { $_ }

        foreach ($t in $hostTokens) {
            if ($t -ne "auto" -and -not $candidateIps.Contains($t)) {
                $candidateIps.Add($t)
            }
        }

        # Predefinisane servisne adrese po podmrezama (GDS Central: 192.168.170.254, LAN: 192.168.88.50, WLAN: 192.168.180.50, Eth: 192.168.170.188)
        $knownServiceIps = @("192.168.170.254", "192.168.88.50", "192.168.180.50", "192.168.170.188")
        foreach ($kip in $knownServiceIps) {
            if (-not $candidateIps.Contains($kip)) {
                $candidateIps.Add($kip)
            }
        }

        # Ocitavanje Default Gateway adresa za svaki aktivni interfejs (i LAN i Wi-Fi)
        try {
            $gwList = Get-NetRoute -DestinationPrefix "0.0.0.0/0" -ErrorAction SilentlyContinue | Where-Object { $_.NextHop -and $_.NextHop -ne "0.0.0.0" } | Select-Object -ExpandProperty NextHop
            foreach ($gw in $gwList) {
                if ($gw -and -not $candidateIps.Contains($gw)) {
                    $candidateIps.Add($gw)
                }
            }
        } catch {}

        # Provera da li je ukljucen auto-discovery
        $allowAuto = ($hostTokens -contains "auto" -or $hostTokens.Count -eq 0)

        if ($allowAuto) {
            # Slanje usmerenih UDP broadcast paketa na sve aktivne podmreze (LAN i WLAN)
            try {
                $udp = New-Object System.Net.Sockets.UdpClient
                $udp.Client.ReceiveTimeout = 300
                $udp.Client.SendTimeout = 300
                $udp.EnableBroadcast = $true

                $reqBytes = [System.Text.Encoding]::UTF8.GetBytes("WIS_DISCOVER client=$env:COMPUTERNAME")

                # Lista broadcast ciljeva
                $broadcastEps = [System.Collections.Generic.List[System.Net.IPEndPoint]]::new()
                $broadcastEps.Add((New-Object System.Net.IPEndPoint([System.Net.IPAddress]::Broadcast, 8888)))

                # Izracunaj broadcast adresu za svaki aktivni mrezni adapter
                try {
                    $netIps = Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue | Where-Object { $_.InterfaceAlias -notmatch 'Loopback' -and $_.IPAddress -notmatch '^169\.254\.' }
                    foreach ($nip in $netIps) {
                        $ipParts = $nip.IPAddress.Split('.')
                        if ($ipParts.Length -eq 4) {
                            # Pretpostavka standardnog /24 opsega za servisne LAN i Wi-Fi podmreze
                            $subnetBcast = "$($ipParts[0]).$($ipParts[1]).$($ipParts[2]).255"
                            try {
                                $parsedIp = [System.Net.IPAddress]::Parse($subnetBcast)
                                $broadcastEps.Add((New-Object System.Net.IPEndPoint($parsedIp, 8888)))
                            } catch {}
                        }
                    }
                } catch {}

                foreach ($bep in $broadcastEps) {
                    try { $udp.Send($reqBytes, $reqBytes.Length, $bep) | Out-Null } catch {}
                }

                $remoteEp = New-Object System.Net.IPEndPoint([System.Net.IPAddress]::Any, 0)
                $respBytes = $udp.Receive([ref]$remoteEp)
                $respStr = [System.Text.Encoding]::UTF8.GetString($respBytes)

                if ($respStr -match 'WIS_HUB_OFFER') {
                    $discoveredHost = $remoteEp.Address.ToString()
                    if ($respStr -match 'port=([0-9]+)') {
                        $configuredPort = [int]$matches[1]
                    }
                    if (-not $candidateIps.Contains($discoveredHost)) {
                        $candidateIps.Insert(0, $discoveredHost)
                    } else {
                        $candidateIps.Remove($discoveredHost) | Out-Null
                        $candidateIps.Insert(0, $discoveredHost)
                    }
                }
                $udp.Close()
            } catch {
                if ($udp) { try { $udp.Close() } catch {} }
            }
        }

        # 3. Brzo sondiranje kandidata (Fast Multi-Subnet HTTP Probe - maks. 400ms timeout)
        $candidatesToProbe = $candidateIps | Select-Object -First 5
        $portsToProbe = @($configuredPort, 8080, 8000) | Select-Object -Unique
        foreach ($candHost in $candidatesToProbe) {
            foreach ($candPort in $portsToProbe) {
                $testUrl = "http://${candHost}:${candPort}"
                try {
                    # Provera /api/status (ili /api/health) sprecava koliziju sa drugim servisima na portu 8000
                    $req = [System.Net.WebRequest]::Create("$testUrl/api/status")
                    $req.Timeout = 400
                    $req.ReadWriteTimeout = 400
                    $req.Method = "GET"
                    $resp = $req.GetResponse()
                    $resp.Close()

                    $Global:ServisHubUrl = $testUrl
                    $Global:HubFailCount = 0
                    if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                        Write-ServisLog "Uspesno povezan na Centralni Servisni Hub: $testUrl (Podmreza prepoznata)" -Level "SUCCESS" -Component "Telemetry"
                    }
                    return $testUrl
                } catch {
                    # Prelazak na sledeci port/adresu
                }
            }
        }

        $Global:ServisHubUrl = $null
        return $null
    } catch {
        $Global:ServisHubUrl = $null
        return $null
    }
}

# ------------------------------------------------------------------------------
# HARDWARE SENSORS & TELEMETRY METRICS
# ------------------------------------------------------------------------------

function Get-HardwareTelemetryMetrics {
    <#
    .SYNOPSIS
        Prikuplja dinamicke hardverske senzorske metrike (CPU opterecenje, termika, RAM zauzece).
    .DESCRIPTION
        - CPU opterecenje: LoadPercentage prosek preko Win32_Processor.
        - CPU temperatura (termika): ACPI termalne zone (MSAcpi_ThermalZoneTemperature)
          konvertovane iz desetinki Kelvina u Celzijuse:
          Formula: [math]::Round(($rawTemp - 2732) / 10, 1).
          Fallback na Win32_PerfFormattedData_Counters_ThermalZoneInformation ako MSAcpi nije dostupan.
          Ako senzori nisu podrzani u hardveru ili WinPE drajveru, polje ostaje $null.
        - RAM: Win32_OperatingSystem TotalVisibleMemorySize i FreePhysicalMemory u GB (1 decimala) i procenat zauzeca:
          ram_percent: [math]::Round((($total - $free) / $total) * 100, 0).
    .COMPATIBILITY
        Windows PowerShell 5.1+
    #>
    [CmdletBinding()]
    param()

    $metrics = [ordered]@{
        cpu_percent = $null
        cpu_temp    = $null
        ram_used    = $null
        ram_total   = $null
        ram_percent = $null
    }

    # 1. CPU Opterecenje
    try {
        $cpuMeasure = Get-CimInstance Win32_Processor -ErrorAction SilentlyContinue | Measure-Object -Property LoadPercentage -Average
        if ($cpuMeasure -and $cpuMeasure.Average -ne $null) {
            $metrics.cpu_percent = [int][math]::Round([double]$cpuMeasure.Average, 0)
        }
    } catch {}

    # 2. CPU Termika (ACPI termalna zona)
    try {
        $foundTemp = $null
        # Pokusaj 1: ACPI WMI termalna zona
        $tzList = Get-CimInstance -Namespace "root/wmi" -ClassName "MSAcpi_ThermalZoneTemperature" -ErrorAction SilentlyContinue
        if ($tzList) {
            $maxT = $null
            foreach ($tz in $tzList) {
                $rawTemp = $tz.CurrentTemperature
                if ($rawTemp -and $rawTemp -gt 2732) {
                    $celsius = [math]::Round(($rawTemp - 2732) / 10.0, 1)
                    if ($celsius -ge 0 -and $celsius -le 130) {
                        if ($maxT -eq $null -or $celsius -gt $maxT) {
                            $maxT = $celsius
                        }
                    }
                }
            }
            $foundTemp = $maxT
        }

        # Pokusaj 2: Fallback na ThermalZoneInformation ako MSAcpi nije vratio vrednost
        if ($foundTemp -eq $null) {
            $perfTz = Get-CimInstance -ClassName Win32_PerfFormattedData_Counters_ThermalZoneInformation -ErrorAction SilentlyContinue
            if ($perfTz) {
                $maxT = $null
                foreach ($pt in $perfTz) {
                    if ($pt.Temperature -gt 273) {
                        $celsius = [math]::Round([double]$pt.Temperature - 273.15, 1)
                        if ($celsius -ge 0 -and $celsius -le 130) {
                            if ($maxT -eq $null -or $celsius -gt $maxT) {
                                $maxT = $celsius
                            }
                        }
                    }
                }
                $foundTemp = $maxT
            }
        }

        $metrics.cpu_temp = $foundTemp
    } catch {
        $metrics.cpu_temp = $null
    }

    # 3. RAM memorija
    try {
        $osInfo = Get-CimInstance Win32_OperatingSystem -ErrorAction SilentlyContinue
        if ($osInfo -and $osInfo.TotalVisibleMemorySize -gt 0) {
            $totalKb = [double]$osInfo.TotalVisibleMemorySize
            $freeKb  = if ($osInfo.FreePhysicalMemory) { [double]$osInfo.FreePhysicalMemory } else { 0.0 }
            $usedKb  = [math]::Max(0.0, ($totalKb - $freeKb))

            $totalGbNum = [math]::Round($totalKb / 1048576.0, 1)
            $usedGbNum  = [math]::Round($usedKb / 1048576.0, 1)

            # Invariant culture format za pouzdan string 'X.Y GB'
            $invCult = [System.Globalization.CultureInfo]::InvariantCulture
            $metrics.ram_total   = "$($totalGbNum.ToString('0.0', $invCult)) GB"
            $metrics.ram_used    = "$($usedGbNum.ToString('0.0', $invCult)) GB"
            $metrics.ram_percent = [int][math]::Round((($totalKb - $freeKb) / $totalKb) * 100.0, 0)
        }
    } catch {}

    return [PSCustomObject]$metrics
}

# ------------------------------------------------------------------------------
# REMOTE COMMAND EXECUTION (BIOS / UEFI REBOOT, PXE, CANCEL)
# ------------------------------------------------------------------------------

function Invoke-ServisRemoteCommand {
    <#
    .SYNOPSIS
        Izvrsava daljinsku komandu primljenu sa Centralnog Servisnog Huba.
    .DESCRIPTION
        Obradjuje daljinske komande poput reboot_uefi (restart u UEFI firmware meni),
        reboot_pxe (restart radi ponovnog mreznog butovanja) i cancel/abort operacija.
        Poseduje fallback logiku za starije Legacy BIOS masine.
    .COMPATIBILITY
        Windows PowerShell 5.1+
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory=$false)]
        [psobject]$Command
    )

    try {
        if (-not $Command) { return }

        $action = $null
        if ($Command -is [string]) {
            $action = $Command
        } elseif ($Command.action) {
            $action = $Command.action
        } elseif ($Command.command) {
            $action = $Command.command
        } elseif ($Command.type) {
            $action = $Command.type
        } elseif ($Command.name) {
            $action = $Command.name
        } else {
            $action = [string]$Command
        }

        if (-not $action) { return }
        $action = $action.ToString().Trim().ToLowerInvariant()

        switch ($action) {
            "reboot_uefi" {
                if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                    Write-ServisLog "Daljinska komanda: Pokretanje restarta u UEFI/BIOS firmware..." -Level "WARN" -Component "RemoteCommand"
                }
                try {
                    $res = & shutdown.exe /r /fw /t 3 2>&1
                    if ($LASTEXITCODE -ne 0) {
                        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                            Write-ServisLog "UEFI firmware interfejs nije podrzan (Legacy BIOS ili greska: $res). Fallback na standardni restart..." -Level "WARN" -Component "RemoteCommand"
                        }
                        & shutdown.exe /r /t 3 2>&1 | Out-Null
                    }
                } catch {
                    if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                        Write-ServisLog "Greska pri pokretanju restarta u UEFI firmware: $($_.Exception.Message). Fallback na standardni restart..." -Level "WARN" -Component "RemoteCommand"
                    }
                    try { & shutdown.exe /r /t 3 2>&1 | Out-Null } catch {}
                }
            }
            "reboot_pxe" {
                if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                    Write-ServisLog "Daljinska komanda: Ponovno pokretanje PXE mrežnog buta..." -Level "WARN" -Component "RemoteCommand"
                }
                try {
                    & shutdown.exe /r /t 3 2>&1 | Out-Null
                } catch {}
            }
            "cancel" {
                if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                    Write-ServisLog "Daljinska komanda: Prekid operacije evidentiran ($action)." -Level "WARN" -Component "RemoteCommand"
                }
            }
            "abort" {
                if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                    Write-ServisLog "Daljinska komanda: Prekid operacije evidentiran ($action)." -Level "WARN" -Component "RemoteCommand"
                }
            }
            "skip_step" {
                try {
                    if (-not (Test-Path "C:\setup")) { New-Item -Path "C:\setup" -ItemType Directory -Force -ErrorAction SilentlyContinue | Out-Null }
                    Set-Content -Path "C:\setup\.skip" -Value "hub_remote_skip $(Get-Date -Format 'o')" -Encoding UTF8 -Force -ErrorAction SilentlyContinue
                } catch {}
                if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                    Write-ServisLog "Daljinska komanda: Preskakanje trenutnog koraka (C:\setup\.skip kreiran)." -Level "WARN" -Component "RemoteCommand"
                }
            }
            default {
                if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                    Write-ServisLog "Nepoznata daljinska komanda primljena: $action" -Level "DEBUG" -Component "RemoteCommand"
                }
            }
        }
    } catch {
        # Zastita radnika: nijedna nepoznata komanda ili greska ne sme oboriti klijenta
    }
}

# ------------------------------------------------------------------------------
# HEARTBEAT & STATUS REPORTING
# ------------------------------------------------------------------------------

function Get-WisPreviousHostname {
    [CmdletBinding()]
    param(
        [string]$LocalSetupDir = "C:\setup"
    )
    $curHost = if ($env:COMPUTERNAME) { $env:COMPUTERNAME.Trim() } else { "KLIJENT" }
    $candDirs = @($LocalSetupDir, "C:\setup") | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -Unique
    foreach ($dir in $candDirs) {
        $statePath = Join-Path $dir "deploy-state.json"
        if (Test-Path -LiteralPath $statePath -PathType Leaf -ErrorAction SilentlyContinue) {
            try {
                $raw = Get-Content -LiteralPath $statePath -Raw -Encoding UTF8 -ErrorAction SilentlyContinue
                if ($raw) {
                    $obj = ConvertFrom-Json -InputObject $raw -ErrorAction SilentlyContinue
                    if ($obj) {
                        if ($obj.PSObject.Properties['previous_hostname'] -and -not [string]::IsNullOrWhiteSpace([string]$obj.previous_hostname)) {
                            $ph = ([string]$obj.previous_hostname).Trim()
                            if (-not $ph.StartsWith("TK-", [System.StringComparison]::OrdinalIgnoreCase)) {
                                return $ph
                            }
                        }
                        if ($obj.PSObject.Properties['initial_hostname'] -and -not [string]::IsNullOrWhiteSpace([string]$obj.initial_hostname)) {
                            $ih = ([string]$obj.initial_hostname).Trim()
                            if (-not $ih.StartsWith("TK-", [System.StringComparison]::OrdinalIgnoreCase)) {
                                return $ih
                            }
                        }
                        if ($obj.PSObject.Properties['computer_name'] -and -not [string]::IsNullOrWhiteSpace([string]$obj.computer_name)) {
                            $cn = ([string]$obj.computer_name).Trim()
                            if (-not $cn.StartsWith("TK-", [System.StringComparison]::OrdinalIgnoreCase)) {
                                return $cn
                            }
                        }
                    }
                }
            } catch {}
        }
        $hostFile = Join-Path $dir "initial-hostname.txt"
        if (Test-Path -LiteralPath $hostFile -PathType Leaf -ErrorAction SilentlyContinue) {
            try {
                $hfVal = (Get-Content -LiteralPath $hostFile -Raw -Encoding UTF8 -ErrorAction SilentlyContinue)
                if (-not [string]::IsNullOrWhiteSpace($hfVal)) {
                    $hfTrim = $hfVal.Trim()
                    if (-not $hfTrim.StartsWith("TK-", [System.StringComparison]::OrdinalIgnoreCase)) {
                        return $hfTrim
                    }
                }
            } catch {}
        }
    }
    # IMP-109: Nikada ne upisuj vec preimenovan TK-* hostname u initial-hostname.txt
    if ($curHost -and $curHost.StartsWith("TK-", [System.StringComparison]::OrdinalIgnoreCase)) {
        return ""
    }
    try {
        $targetDir = if (Test-Path -LiteralPath $LocalSetupDir) { $LocalSetupDir } else { "C:\setup" }
        if (-not (Test-Path -LiteralPath $targetDir)) {
            New-Item -Path $targetDir -ItemType Directory -Force -ErrorAction SilentlyContinue | Out-Null
        }
        $destHostFile = Join-Path $targetDir "initial-hostname.txt"
        $tmpHostFile = "$destHostFile.tmp.$PID"
        $utf8NoBom = New-Object System.Text.UTF8Encoding($false)
        [System.IO.File]::WriteAllText($tmpHostFile, $curHost, $utf8NoBom)
        Move-Item -LiteralPath $tmpHostFile -Destination $destHostFile -Force -ErrorAction SilentlyContinue
    } catch {}
    return $curHost
}

function Send-ServisHeartbeat {
    param(
        [string]$LiveStatus = "",
        [int]$WorkerProgress = -1,
        [string]$LocalSetupDir = "C:\setup",
        [string]$SystemLogDir = "C:\Windows\Logs\WIS"
    )

    if (-not $Global:ServisHubUrl) {
        Find-ServisHub | Out-Null
        if (-not $Global:ServisHubUrl) { return }
    }

    try {
        $curPhase = if ($Global:State -and $Global:State.Phase) { [int]$Global:State.Phase } else { if ($Global:CurrentPhase) { [int]$Global:CurrentPhase } else { 1 } }
        $phaseNames = @{ 1 = "Mreza"; 2 = "Update"; 3 = "Optimizacija"; 4 = "Programi"; 5 = "Stres Test"; 6 = "Zavrsetak" }
        $phaseName = if ($phaseNames.ContainsKey($curPhase)) { $phaseNames[$curPhase] } else { "Faza$curPhase" }

        $upCycle = if ($Global:State -and $Global:State.UpdateCycle) { [int]$Global:State.UpdateCycle } else { 1 }
        $maxCycles = if ($Global:State -and $Global:State.MaxCycles) { [int]$Global:State.MaxCycles } else { 3 }

        $hw = $Global:HwInfo
        $fHw = Get-FleetHardwareInfo
        $modelVal = if ($fHw -and $fHw.Model) { $fHw.Model } elseif ($hw -and $hw.Model) { $hw.Model } else { "PC" }
        $hwDict = if ($hw) {
            @{
                manufacturer = if ($hw.Manufacturer) { $hw.Manufacturer } else { $fHw.Manufacturer }
                model        = $modelVal
                serial       = if ($hw.SerialNumber) { $hw.SerialNumber } else { $fHw.SerialNumber }
                bios         = $hw.BiosVersion
                cpu          = $hw.CpuStr
                ram          = $hw.RamStr
                disk         = $hw.DiskStr
                battery      = $hw.BatteryStr
            }
        } else {
            @{
                manufacturer = $fHw.Manufacturer
                model        = $modelVal
                serial       = $fHw.SerialNumber
            }
        }

        $appsSummary = @{}
        if (Get-Command Get-RealAppStatuses -ErrorAction SilentlyContinue) {
            try {
                $appData = Get-RealAppStatuses
                if ($appData -and $appData.Items) {
                    foreach ($it in $appData.Items) {
                        $appsSummary[$it.Id] = $it.Status
                    }
                }
                if ($WorkerProgress -lt 0 -and $appData.WorkerProgress) {
                    $WorkerProgress = $appData.WorkerProgress
                }
            } catch {}
        }

        # Informacije o trenutnim mrezama klijenta (LAN i Wi-Fi)
        $netInfo = @{}
        if (Get-Command Get-NetworkDetailStatus -ErrorAction SilentlyContinue) {
            try {
                $ns = Get-NetworkDetailStatus
                $netInfo = @{
                    lan_connected  = $ns.LanConnected
                    lan_speed      = $ns.LanSpeed
                    wifi_connected = $ns.WifiConnected
                    wifi_ssid      = $ns.WifiSSID
                    wifi_signal    = $ns.WifiSignal
                    both_connected = $ns.BothConnected
                }
            } catch {}
        }

        $effectiveProgress = if ($WorkerProgress -ge 0) { $WorkerProgress } else { 0 }
        $effectiveLiveStatus = if ($LiveStatus) { $LiveStatus } else { if ($Global:LastLiveStatus) { $Global:LastLiveStatus } else { "Aktivan rad u Fazi $curPhase..." } }

        # Dinamicke hardverske senzorske metrike (CPU opterecenje, termika, RAM zauzece)
        $hwMetrics = $null
        try {
            $hwMetrics = Get-HardwareTelemetryMetrics
        } catch {}

        $cpuPercent = if ($hwMetrics -and $hwMetrics.cpu_percent -ne $null) { $hwMetrics.cpu_percent } else { $null }
        $cpuTemp    = if ($hwMetrics -and $hwMetrics.cpu_temp -ne $null) { $hwMetrics.cpu_temp } else { $null }
        $ramUsed    = if ($hwMetrics -and $hwMetrics.ram_used -ne $null) { $hwMetrics.ram_used } else { $null }
        $ramTotal   = if ($hwMetrics -and $hwMetrics.ram_total -ne $null) { $hwMetrics.ram_total } else { $null }
        $ramPercent = if ($hwMetrics -and $hwMetrics.ram_percent -ne $null) { $hwMetrics.ram_percent } else { $null }

        $devId = $Global:FleetDeviceId
        if ([string]::IsNullOrWhiteSpace($devId) -and (Get-Command Get-FleetDeviceId -ErrorAction SilentlyContinue)) {
            try { $devId = Get-FleetDeviceId } catch {}
        }
        if ([string]::IsNullOrWhiteSpace($devId)) {
            $devId = "TK-DESKTOP-$(if ($env:COMPUTERNAME) { $env:COMPUTERNAME } else { 'KLIJENT' })"
        }
        $serialNum = if ($Global:FleetSerialNumber) { $Global:FleetSerialNumber } elseif ($hw -and $hw.SerialNumber) { $hw.SerialNumber } elseif ($fHw -and $fHw.SerialNumber) { $fHw.SerialNumber } else { "" }
        $prevHost = Get-WisPreviousHostname -LocalSetupDir $LocalSetupDir
        $imgProfile = Get-WisImageProfile -SystemLogDir $SystemLogDir -LocalSetupDir $LocalSetupDir

        $payload = [ordered]@{
            device_id         = $devId
            serial_number     = $serialNum
            previous_hostname = $prevHost
            computer_name     = if ($env:COMPUTERNAME) { $env:COMPUTERNAME } else { "KLIJENT" }
            phase             = $curPhase
            phase_name        = $phaseName
            update_cycle      = $upCycle
            max_cycles        = $maxCycles
            live_status       = $effectiveLiveStatus
            worker_progress   = $effectiveProgress
            cpu_percent       = $cpuPercent
            cpu_temp          = $cpuTemp
            ram_used          = $ramUsed
            ram_total         = $ramTotal
            ram_percent       = $ramPercent
            image_type        = [string]$imgProfile.image_type
            wim_version       = [string]$imgProfile.wim_version
            run_meta          = [ordered]@{
                image_type  = [string]$imgProfile.image_type
                wim_version = [string]$imgProfile.wim_version
            }
            hw                = $hwDict
            network           = $netInfo
            apps              = $appsSummary
            timestamp         = (Get-Date).ToString("o")
        }

        $jsonBody = New-WisTelemetryJson -Payload $payload -LocalSetupDir $LocalSetupDir -SystemLogDir $SystemLogDir -Depth 4
        $url = "$($Global:ServisHubUrl)/api/heartbeat"

        $req = [System.Net.HttpWebRequest]::Create($url)
        $req.Method = "POST"
        $req.ContentType = "application/json; charset=utf-8"
        $req.Timeout = 500
        $req.ReadWriteTimeout = 500
        $bytes = [System.Text.Encoding]::UTF8.GetBytes($jsonBody)
        $req.ContentLength = $bytes.Length
        $reqStream = $req.GetRequestStream()
        $reqStream.Write($bytes, 0, $bytes.Length)
        $reqStream.Close()
        $resp = $req.GetResponse()
        $respText = $null
        try {
            $respStream = $resp.GetResponseStream()
            if ($respStream) {
                $reader = New-Object System.IO.StreamReader($respStream, [System.Text.Encoding]::UTF8)
                $respText = $reader.ReadToEnd()
                $reader.Close()
                $respStream.Close()
            }
        } finally {
            $resp.Close()
        }

        $Global:HubFailCount = 0

        # Obrada daljinskih komandi sa servera (pending_commands iz odgovora ili iz UI bafera)
        $queuedCmdFile = Join-Path $LocalSetupDir "pending-hub-commands.json"
        if (Test-Path -LiteralPath $queuedCmdFile) {
            try {
                $queuedRaw = Get-Content -LiteralPath $queuedCmdFile -Raw -Encoding UTF8 -ErrorAction SilentlyContinue
                Remove-Item -LiteralPath $queuedCmdFile -Force -ErrorAction SilentlyContinue
                if ($queuedRaw) {
                    $qObj = ConvertFrom-Json $queuedRaw -ErrorAction SilentlyContinue
                    if ($qObj -and $qObj.pending_commands) {
                        foreach ($qCmd in @($qObj.pending_commands)) {
                            Invoke-ServisRemoteCommand -Command $qCmd -ErrorAction SilentlyContinue
                        }
                    }
                }
            } catch {}
        }
        if ($respText) {
            try {
                $respObj = ConvertFrom-Json $respText -ErrorAction SilentlyContinue
                if ($respObj -and $respObj.pending_commands) {
                    $cmdList = @($respObj.pending_commands)
                    if ($cmdList.Count -gt 0) {
                        foreach ($cmd in $cmdList) {
                            Invoke-ServisRemoteCommand -Command $cmd -ErrorAction SilentlyContinue
                        }
                    }
                }
            } catch {}
        }
    } catch {
        $Global:HubFailCount = if ($Global:HubFailCount) { $Global:HubFailCount + 1 } else { 1 }
        # Ako je veza pukla 2 puta zaredom (npr. prelaz sa LAN-a na Wi-Fi subnet), forsiraj ponovno nalazenje Huba
        if ($Global:HubFailCount -ge 2) {
            $Global:ServisHubUrl = $null
            $Global:HubFailCount = 0
            try { Find-ServisHub -ForceRefresh | Out-Null } catch {}
        }
    }
}

# ------------------------------------------------------------------------------
# BUFFERED LOG TRANSMISSION
# ------------------------------------------------------------------------------

function Flush-ServisLogBuffer {
    try {
        if (-not $Global:ServisHubUrl) {
            try { Find-ServisHub | Out-Null } catch {}
            if (-not $Global:ServisHubUrl) {
                if ($Global:PendingHubLogs) { $Global:PendingHubLogs.Clear() }
                return
            }
        }

        if (-not $Global:PendingHubLogs -or $Global:PendingHubLogs.Count -eq 0) {
            return
        }

        $linesToSend = $null
        try {
            $linesToSend = [string[]]$Global:PendingHubLogs.ToArray()
            $Global:PendingHubLogs.Clear()
        } catch { return }

        if (-not $linesToSend -or $linesToSend.Length -eq 0) { return }

        try {
            $logDevId = if ($Global:FleetDeviceId) { $Global:FleetDeviceId } else { "" }
            $logSerial = if ($Global:FleetSerialNumber) { $Global:FleetSerialNumber } else { "" }
            $logPrevHost = Get-WisPreviousHostname
            $payload = [ordered]@{
                device_id         = $logDevId
                serial_number     = $logSerial
                previous_hostname = $logPrevHost
                computer_name     = if ($env:COMPUTERNAME) { $env:COMPUTERNAME } else { "KLIJENT" }
                lines             = $linesToSend
            }
            $jsonBody = ConvertTo-Json $payload -Compress -Depth 4
            $url = "$($Global:ServisHubUrl)/api/log"

            $req = [System.Net.HttpWebRequest]::Create($url)
            $req.Method = "POST"
            $req.ContentType = "application/json; charset=utf-8"
            $req.Timeout = 500
            $req.ReadWriteTimeout = 500
            $bytes = [System.Text.Encoding]::UTF8.GetBytes($jsonBody)
            $req.ContentLength = $bytes.Length
            $reqStream = $req.GetRequestStream()
            $reqStream.Write($bytes, 0, $bytes.Length)
            $reqStream.Close()
            $resp = $req.GetResponse()
            $resp.Close()

            $Global:HubFailCount = 0
        } catch {
            # U slucaju pada servera ili promene subneta, sacuvaj bafer i probaj prevezivanje
            try {
                if ($Global:PendingHubLogs -and $Global:PendingHubLogs.Count -lt 100) {
                    foreach ($l in $linesToSend) { $Global:PendingHubLogs.Add($l) }
                }
            } catch {}

            $Global:HubFailCount = if ($Global:HubFailCount) { $Global:HubFailCount + 1 } else { 1 }
            if ($Global:HubFailCount -ge 2) {
                $Global:ServisHubUrl = $null
                $Global:HubFailCount = 0
                try { Find-ServisHub -ForceRefresh | Out-Null } catch {}
            }
        }
    } catch {}
}

function Push-ServisLogToHub {
    param(
        [Parameter(Mandatory=$true)][string]$LogLine
    )
    try {
        if (-not $Global:PendingHubLogs) {
            $Global:PendingHubLogs = [System.Collections.Generic.List[string]]::new()
        }

        $Global:PendingHubLogs.Add($LogLine)

        # Ako se nakupilo 5 ili vise linija, ili je proslo 2 sekunde, salji odmah
        $now = [Environment]::TickCount
        if (-not $Global:LastHubFlushTick) { $Global:LastHubFlushTick = $now }

        if ($Global:PendingHubLogs.Count -ge 5 -or ($now - $Global:LastHubFlushTick) -ge 2000) {
            $Global:LastHubFlushTick = $now
            Flush-ServisLogBuffer
        }
    } catch {
        # Zastita radnika: greska u baferisanju logova nikada ne sme prekinuti ili usporiti izvrsavanje
    }
}

# ------------------------------------------------------------------------------
# DISCRETE LIFECYCLE TELEMETRY (START & COMPLETION EVENTS)
# ------------------------------------------------------------------------------

function Send-ServisLifecycleEvent {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory=$true)]
        [ValidateSet("STARTED", "COMPLETED")]
        [string]$EventType,

        [Parameter(Mandatory=$true)]
        [hashtable]$Data,

        [string]$LocalSetupDir = "C:\setup"
    )

    if (-not $Global:ServisHubUrl) {
        if (Get-Command Find-ServisHub -ErrorAction SilentlyContinue) {
            try { Find-ServisHub -LocalSetupDir $LocalSetupDir | Out-Null } catch {}
        }
        if (-not $Global:ServisHubUrl) {
            return $false
        }
    }

    try {
        $compName = if ($env:COMPUTERNAME) { $env:COMPUTERNAME } else { "KLIJENT" }
        $lcDevId = if ($Data -and $Data.ContainsKey("device_id") -and $Data["device_id"]) {
            $Data["device_id"]
        } elseif ($Global:FleetDeviceId) {
            $Global:FleetDeviceId
        } elseif (Get-Command Get-FleetDeviceId -ErrorAction SilentlyContinue) {
            try { Get-FleetDeviceId } catch { "TK-DESKTOP-$compName" }
        } else {
            "TK-DESKTOP-$compName"
        }
        $lcSerial = if ($Data -and $Data.ContainsKey("serial_number") -and $Data["serial_number"]) {
            $Data["serial_number"]
        } elseif ($Data -and $Data.ContainsKey("serial") -and $Data["serial"]) {
            $Data["serial"]
        } elseif ($Global:FleetSerialNumber) {
            $Global:FleetSerialNumber
        } else {
            try { (Get-FleetHardwareInfo).SerialNumber } catch { "" }
        }
        $lcPrevHost = if ($Data -and $Data.ContainsKey("previous_hostname") -and $Data["previous_hostname"]) {
            $Data["previous_hostname"]
        } else {
            Get-WisPreviousHostname -LocalSetupDir $LocalSetupDir
        }

        $lcImgProfile = Get-WisImageProfile -LocalSetupDir $LocalSetupDir
        $payload = [ordered]@{
            event             = $EventType
            device_id         = $lcDevId
            serial_number     = $lcSerial
            previous_hostname = $lcPrevHost
            computer_name     = $compName
            image_type        = [string]$lcImgProfile.image_type
            wim_version       = [string]$lcImgProfile.wim_version
            run_meta          = [ordered]@{
                image_type  = [string]$lcImgProfile.image_type
                wim_version = [string]$lcImgProfile.wim_version
            }
        }

        if ($Data) {
            foreach ($k in $Data.Keys) {
                $payload[$k] = $Data[$k]
            }
        }

        $payload["timestamp"] = (Get-Date).ToString("o")

        $jsonBody = ConvertTo-Json $payload -Compress -Depth 5
        $url = "$($Global:ServisHubUrl)/api/lifecycle"

        $req = [System.Net.HttpWebRequest]::Create($url)
        $req.Method = "POST"
        $req.ContentType = "application/json; charset=utf-8"
        $req.Timeout = 500
        $req.ReadWriteTimeout = 500

        $bytes = [System.Text.Encoding]::UTF8.GetBytes($jsonBody)
        $req.ContentLength = $bytes.Length
        $reqStream = $req.GetRequestStream()
        $reqStream.Write($bytes, 0, $bytes.Length)
        $reqStream.Close()

        $resp = $req.GetResponse()
        $resp.Close()
        return $true
    } catch {
        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
            Write-ServisLog "Greska pri slanju telemetrijskog dogadjaja '$EventType' ka ServisHub-u: $($_.Exception.Message)" -Level "DEBUG" -Component "Telemetry"
        }
        return $false
    }
}

# ------------------------------------------------------------------------------
# FLEET DEVICE IDENTIFICATION & FLEET REPORTING
# ------------------------------------------------------------------------------

function Get-FleetHardwareInfo {
    <#
    .SYNOPSIS
        Prikuplja i normalizuje osnovne hardverske podatke racunara (proizvodjac, model, serijski broj)
        sa naprednom detekcijom i normalizacijom modela za Lenovo, Dell i Custom racunare.
    .DESCRIPTION
        - Za Dell: primarno uzima Win32_ComputerSystem.Model (npr. 'Latitude 5490').
        - Za Lenovo: proverava Win32_ComputerSystemProduct.Version (gde se cuva 'ThinkPad T480', 'ThinkCentre M720q').
          Ako nije prazno i nije 'None'/'Default string', koristi ga. Ako jeste, uzima Win32_ComputerSystemProduct.Name ili Win32_ComputerSystem.Model.
        - Za Custom/sklapanja: ako je model 'System Product Name', 'To be filled by O.E.M.' ili slicno, fallback na Win32_BaseBoard.Product.
    #>
    [CmdletBinding()]
    param()

    try {
        $compSys = Get-CimInstance Win32_ComputerSystem -ErrorAction SilentlyContinue
        $csp     = Get-CimInstance Win32_ComputerSystemProduct -ErrorAction SilentlyContinue
        $board   = Get-CimInstance Win32_BaseBoard -ErrorAction SilentlyContinue
        $bios    = Get-CimInstance Win32_Bios -ErrorAction SilentlyContinue

        $mfg = if ($compSys -and $compSys.Manufacturer) { ($compSys.Manufacturer -replace '\s+', ' ').Trim() } else { "Nepoznato" }
        $rawModel = if ($compSys -and $compSys.Model) { ($compSys.Model -replace '\s+', ' ').Trim() } else { "" }
        $cspVer = if ($csp -and $csp.Version) { ($csp.Version -replace '\s+', ' ').Trim() } else { "" }
        $cspName = if ($csp -and $csp.Name) { ($csp.Name -replace '\s+', ' ').Trim() } else { "" }
        $boardProd = if ($board -and $board.Product) { ($board.Product -replace '\s+', ' ').Trim() } else { "" }

        $invalidStrings = @("None", "Default string", "To be filled by O.E.M.", "System Product Name", "System manufacturer", "Not Applicable", "N/A", "Unknown", "")

        $normalizedModel = $rawModel

        # A) Lenovo normalizacija:
        # Kod Lenovo masina, Win32_ComputerSystemProduct.Version cuva pravi komercijalni naziv ('ThinkPad T480', 'ThinkCentre M720q')
        if ($mfg -match '(?i)Lenovo') {
            if ($cspVer -and ($invalidStrings -notcontains $cspVer)) {
                $normalizedModel = $cspVer
            } elseif ($cspName -and ($invalidStrings -notcontains $cspName)) {
                $normalizedModel = $cspName
            } elseif ($rawModel -and ($invalidStrings -notcontains $rawModel)) {
                $normalizedModel = $rawModel
            }
        }
        # B) Dell normalizacija:
        # Kod Dell masina, Win32_ComputerSystem.Model primarno cuva tacan komercijalni naziv ('Latitude 5490', 'OptiPlex 7050')
        elseif ($mfg -match '(?i)Dell') {
            if ($rawModel -and ($invalidStrings -notcontains $rawModel)) {
                $normalizedModel = $rawModel
            } elseif ($cspName -and ($invalidStrings -notcontains $cspName)) {
                $normalizedModel = $cspName
            }
        }
        # C) Custom buildovi ili genericki OEM nazivi:
        else {
            if (-not $normalizedModel -or ($invalidStrings -contains $normalizedModel) -or ($normalizedModel -match '(?i)System Product Name|To be filled')) {
                if ($boardProd -and ($invalidStrings -notcontains $boardProd)) {
                    $normalizedModel = $boardProd
                } elseif ($cspName -and ($invalidStrings -notcontains $cspName)) {
                    $normalizedModel = $cspName
                }
            }
        }

        if (-not $normalizedModel -or ($invalidStrings -contains $normalizedModel)) {
            $normalizedModel = if ($boardProd) { $boardProd } else { "PC" }
        }

        # Ocitavanje serijskog broja
        $serial = if ($bios -and $bios.SerialNumber) { $bios.SerialNumber.Trim() } else { "" }
        if (-not $serial -or ($invalidStrings -contains $serial)) {
            if ($csp -and $csp.IdentifyingNumber -and ($invalidStrings -notcontains $csp.IdentifyingNumber.Trim())) {
                $serial = $csp.IdentifyingNumber.Trim()
            } else {
                $serial = if ($env:COMPUTERNAME) { $env:COMPUTERNAME } else { "UNKNOWN" }
            }
        }

        # Azuriranje modela u $Global:HwInfo ukoliko vec postoji
        if ($Global:HwInfo) {
            $Global:HwInfo.Model = $normalizedModel
        }

        return [PSCustomObject]@{
            Manufacturer = $mfg
            Model        = $normalizedModel
            RawModel     = $rawModel
            SerialNumber = $serial
            Board        = $boardProd
        }
    } catch {
        return [PSCustomObject]@{
            Manufacturer = "Nepoznato"
            Model        = "PC"
            RawModel     = ""
            SerialNumber = "UNKNOWN"
            Board        = ""
        }
    }
}

function Get-FleetDeviceId {
    <#
    .SYNOPSIS
        Odredjuje jedinstveni Fleet identifikator racunara u formatu TK-{Chassis}-{SerialNumber}.
    .DESCRIPTION
        - Tip sasije (Laptop, Desktop, All-in-One, Mini-PC) odredjuje preko Win32_SystemEnclosure.
        - Ocitava serijski broj iz Win32_Bios.
        - Vraca format: TK-{Chassis}-{SerialNumber} (npr. TK-LAPTOP-5CD1234 ili TK-DESKTOP-CZC9876).
    #>
    [CmdletBinding()]
    param()

    try {
        # 1. Odredjivanje tipa sasije (Chassis)
        $chassisType = "Desktop"

        $enclosure = Get-CimInstance Win32_SystemEnclosure -ErrorAction SilentlyContinue
        $hwProfile = Get-FleetHardwareInfo
        $model = if ($hwProfile -and $hwProfile.Model) { $hwProfile.Model.Trim() } else { "" }

        $types = @()
        if ($enclosure -and $enclosure.ChassisTypes) {
            $types = @($enclosure.ChassisTypes)
        }

        # Provera All-in-One (tip 13 ili model sadrzi All-in-One / AIO)
        if (($types -contains 13) -or ($model -match '(?i)all[- ]in[- ]one|\baio\b')) {
            $chassisType = "All-in-One"
        }
        # Provera Laptop / Portable (tipovi 8, 9, 10, 11, 12, 14, 30, 31, 32 ili prisustvo baterije)
        elseif (($types | Where-Object { $_ -in @(8, 9, 10, 11, 12, 14, 30, 31, 32) }) -or (Get-CimInstance Win32_Battery -ErrorAction SilentlyContinue)) {
            $chassisType = "Laptop"
        }
        # Provera Mini-PC (tipovi 15, 16, 33, 34, 35, 36 ili model sadrzi Mini/Micro/Tiny/NUC/USFF/DM)
        elseif (($types | Where-Object { $_ -in @(15, 16, 33, 34, 35, 36) }) -or ($model -match '(?i)\b(mini|micro|tiny|nuc|usff|dm)\b')) {
            $chassisType = "Mini-PC"
        }
        else {
            $chassisType = "Desktop"
        }

        # 2. Ocitavanje serijskog broja
        $serial = if ($hwProfile -and $hwProfile.SerialNumber) { $hwProfile.SerialNumber.Trim() } else { "" }
        $invalidSerials = @("To be filled by O.E.M.", "Default string", "None", "System Serial Number", "0123456789", "System manufacturer", "")
        if (-not $serial -or ($invalidSerials -contains $serial)) {
            $serial = if ($env:COMPUTERNAME) { $env:COMPUTERNAME } else { "UNKNOWN" }
        }

        $cleanSerial = ($serial -replace '[^a-zA-Z0-9_-]', '').ToUpper()
        if (-not $cleanSerial) { $cleanSerial = "UNKNOWN" }

        # 3. Format: TK-{Chassis}-{SerialNumber}
        $chassisTag = $chassisType.ToUpper()
        $deviceId = "TK-$chassisTag-$cleanSerial"

        $Global:FleetDeviceId = $deviceId
        $Global:FleetChassis = $chassisType
        $Global:FleetSerialNumber = $cleanSerial

        return $deviceId
    } catch {
        $fallbackId = "TK-DESKTOP-$($env:COMPUTERNAME)"
        $Global:FleetDeviceId = $fallbackId
        return $fallbackId
    }
}

function Send-FleetFinalReport {
    <#
    .SYNOPSIS
        Prikuplja status svih instaliranih aplikacija, trajanje i greske, te salje
        zavrsni JSON servisni karton na Centralni Servisni Hub.
    .DESCRIPTION
        - Salje JSON payload na POST $($Global:ServisHubUrl)/api/devices/$($Global:FleetDeviceId)/report.
        - Fail-safe obrada (timeout 2s, tiho nastavljanje ako Hub nije dostupan).
    #>
    [CmdletBinding()]
    param(
        [string]$Status = "COMPLETED",
        [string[]]$Errors = @(),
        [hashtable]$CustomData = @{},
        [string]$LocalSetupDir = "C:\setup",
        [string]$SystemLogDir = "C:\Windows\Logs\WIS"
    )

    try {
        # 0. Jednokratno slanje po statusu u okviru iste sesije (IMP-23)
        if (-not [string]::IsNullOrWhiteSpace($Global:WisFinalReportSentStatus) -and ($Global:WisFinalReportSentStatus -eq $Status)) {
            if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                Write-ServisLog "Send-FleetFinalReport: Zavrsni izvestaj za status '$Status' je vec poslat u ovoj sesiji; preskacem dupli POST." -Level "INFO" -Component "Telemetry"
            }
            return $true
        }

        # 1. Osiguraj Hub URL
        if (-not $Global:ServisHubUrl) {
            if (Get-Command Find-ServisHub -ErrorAction SilentlyContinue) {
                Find-ServisHub | Out-Null
            }
        }
        if (-not $Global:ServisHubUrl) {
            return $false
        }

        # 2. Osiguraj FleetDeviceId
        $devId = $Global:FleetDeviceId
        if (-not $devId) {
            if (Get-Command Get-FleetDeviceId -ErrorAction SilentlyContinue) {
                $devId = Get-FleetDeviceId
            } else {
                $devId = "TK-DESKTOP-$($env:COMPUTERNAME)"
                $Global:FleetDeviceId = $devId
            }
        }

        # 3. Benchmark sazetak i ukupno trajanje (IMP-23: prioritet initial_start_time -> benchmark -> ScriptStartTime)
        $benchSummary = $null
        if (Get-Command Get-WisBenchmarkSummary -ErrorAction SilentlyContinue) {
            try {
                $benchSummary = Get-WisBenchmarkSummary -LocalSetupDir $LocalSetupDir
            } catch {}
        }

        $totalSec = 0
        $stateFileCandidates = @((Join-Path $LocalSetupDir "deploy-state.json"), "C:\setup\deploy-state.json") | Where-Object { $_ } | Select-Object -Unique
        foreach ($stateFilePath in $stateFileCandidates) {
            if (Test-Path -LiteralPath $stateFilePath) {
                try {
                    $stRaw = Get-Content -Path $stateFilePath -Raw -Encoding UTF8 -ErrorAction SilentlyContinue
                    if ($stRaw) {
                        $stObj = ConvertFrom-Json -InputObject $stRaw -ErrorAction SilentlyContinue
                        if ($stObj -and $stObj.initial_start_time) {
                            $initDt = [DateTime]::Parse("$($stObj.initial_start_time)", [System.Globalization.CultureInfo]::InvariantCulture, [System.Globalization.DateTimeStyles]::RoundtripKind).ToUniversalTime()
                            $elapsedAll = [int][math]::Round(([DateTime]::UtcNow - $initDt).TotalSeconds)
                            if ($elapsedAll -gt 0) {
                                $totalSec = $elapsedAll
                                break
                            }
                        }
                    }
                } catch {}
            }
        }
        if ($totalSec -le 0 -and $benchSummary -and $benchSummary.total_phase_duration_sec -gt 0) {
            $totalSec = [int][math]::Round([double]$benchSummary.total_phase_duration_sec)
        }
        if ($totalSec -le 0 -and $Global:ScriptStartTime) {
            $scriptElapsed = [int][math]::Round(((Get-Date) - $Global:ScriptStartTime).TotalSeconds)
            if ($scriptElapsed -gt 0) {
                $totalSec = $scriptElapsed
            }
        }
        $durFmt = "{0}m {1:D2}s" -f [math]::Floor($totalSec / 60), ($totalSec % 60)

        # 4. Prikupljanje statusa aplikacija
        $appsData = @{}
        if (Get-Command Get-RealAppStatuses -ErrorAction SilentlyContinue) {
            try {
                $realApps = Get-RealAppStatuses
                if ($realApps -and $realApps.Items) {
                    foreach ($it in $realApps.Items) {
                        $appsData[$it.Id] = $it.Status
                    }
                }
            } catch {}
        }
        $mandatoryApps = @("Chrome", "WinRAR", "Office", "Acrobat", "DotNet", "BatteryBar", "Shortcuts", "License")
        $hasAppPresFn = [bool](Get-Command Get-WisAppPresence -ErrorAction SilentlyContinue)
        foreach ($mApp in $mandatoryApps) {
            if (-not $appsData.ContainsKey($mApp)) {
                if ($hasAppPresFn) {
                    try {
                        $pres = Get-WisAppPresence -AppId $mApp
                        if ($pres) {
                            $appsData[$mApp] = if ($pres.Installed) { "OK" } elseif ($mApp -eq "BatteryBar") { "SKIP" } else { "Failed" }
                            continue
                        }
                    } catch {}
                }
                if ($mApp -in @("Chrome", "WinRAR", "Office", "Acrobat")) {
                    $isInst = switch ($mApp) {
                        "Chrome" { (Test-Path "C:\Program Files\Google\Chrome\Application\chrome.exe") -or (Test-Path "C:\Program Files (x86)\Google\Chrome\Application\chrome.exe") }
                        "WinRAR" { (Test-Path "C:\Program Files\WinRAR\WinRAR.exe") -or (Test-Path "C:\Program Files (x86)\WinRAR\WinRAR.exe") }
                        "Office" { (Test-Path "C:\Program Files\Microsoft Office\root\Office16\WINWORD.EXE") -or (Test-Path "C:\Program Files (x86)\Microsoft Office\root\Office16\WINWORD.EXE") }
                        "Acrobat" { (Test-Path "C:\Program Files\Adobe\Acrobat DC\Acrobat\Acrobat.exe") -or (Test-Path "C:\Program Files\Adobe\Acrobat 2024\Acrobat\Acrobat.exe") }
                    }
                    $appsData[$mApp] = if ($isInst) { "OK" } else { "Failed" }
                }
            }
        }

        # 5. Prikupljanje gresaka (IMP-66: samo iz aktivne sesije, bez istorijskih linija kod cistog COMPLETED)
        $sessionId = Get-WisInstallSessionId -LocalSetupDir $LocalSetupDir -EnsureLogMarker

        $hwTestObj = $null
        $hwTestCand = @((Join-Path $LocalSetupDir "hwtest-result.json"), "C:\setup\hwtest-result.json") | Where-Object { $_ -and (Test-Path -LiteralPath $_) } | Select-Object -First 1
        if ($hwTestCand) {
            try {
                $hwTestRaw = Get-Content -LiteralPath $hwTestCand -Raw -Encoding UTF8 -ErrorAction SilentlyContinue
                if ($hwTestRaw) {
                    $hwTestObj = ConvertFrom-Json -InputObject $hwTestRaw -ErrorAction SilentlyContinue
                }
            } catch {}
        }

        $hasAppFailure = $false
        foreach ($ak in $appsData.Keys) {
            if ($ak -in @("License", "BatteryBar")) { continue }
            $av = [string]$appsData[$ak]
            if ($av -match '^(Failed|ERROR|FAIL)$') {
                $hasAppFailure = $true
                break
            }
        }
        $hasHwFailure = $false
        if ($hwTestObj -and $hwTestObj.overall_status -and ("$($hwTestObj.overall_status)" -match 'FAIL|ERROR')) {
            $hasHwFailure = $true
        }

        $errorList = [System.Collections.Generic.List[string]]::new()
        if ($Errors) {
            foreach ($e in $Errors) { if ($e) { $errorList.Add($e) } }
        }
        $allowLogScrape = ($Status -ne "COMPLETED") -or $hasAppFailure -or $hasHwFailure
        if ($errorList.Count -eq 0 -and $allowLogScrape) {
            $deployLogPath = if (Test-Path "C:\setup\servis_deploy.log") {
                "C:\setup\servis_deploy.log"
            } elseif (Test-Path "C:\Windows\Logs\WIS\servis_deploy.log") {
                "C:\Windows\Logs\WIS\servis_deploy.log"
            } else { $null }

            if ($deployLogPath) {
                try {
                    $allLines = @(Get-Content $deployLogPath -ErrorAction SilentlyContinue)
                    $startIdx = 0
                    for ($li = $allLines.Count - 1; $li -ge 0; $li--) {
                        if ($allLines[$li] -match '=== WIS_SESSION_START:[^\s=]+ ===') {
                            $startIdx = $li + 1
                            break
                        }
                    }
                    $sessionLines = if ($startIdx -lt $allLines.Count) { @($allLines[$startIdx..($allLines.Count - 1)]) } else { @() }
                    $errLines = $sessionLines | Where-Object { $_ -match '\[ERROR\]' } | Select-Object -Last 10
                    if ($errLines) {
                        foreach ($el in $errLines) { $errorList.Add($el.Trim()) }
                    }
                } catch {}
            }
        }

        # 6. Hardverski profil
        $hw = $Global:HwInfo
        if (-not $hw) {
            if (Get-Command Get-SystemHardwareInfo -ErrorAction SilentlyContinue) {
                try { $hw = Get-SystemHardwareInfo } catch {}
            } elseif (Get-Command Get-HwInfo -ErrorAction SilentlyContinue) {
                try { $hw = Get-HwInfo } catch {}
            }
        }
        $fHw = Get-FleetHardwareInfo
        $modelVal = if ($fHw -and $fHw.Model) { $fHw.Model } elseif ($hw -and $hw.Model) { $hw.Model } else { "PC" }
        $hwDict = if ($hw) {
            @{
                manufacturer = if ($hw.Manufacturer) { $hw.Manufacturer } else { $fHw.Manufacturer }
                model        = $modelVal
                serial       = if ($hw.SerialNumber) { $hw.SerialNumber } else { $fHw.SerialNumber }
                cpu          = $hw.CpuStr
                ram          = $hw.RamStr
                disk         = $hw.DiskStr
                battery      = $hw.BatteryStr
            }
        } else {
            @{
                manufacturer = $fHw.Manufacturer
                model        = $modelVal
                serial       = $fHw.SerialNumber
            }
        }

        $chassis = if ($Global:FleetChassis) { $Global:FleetChassis } else { "Desktop" }
        $serialNum = if ($Global:FleetSerialNumber) { $Global:FleetSerialNumber } else { if ($hw) { $hw.SerialNumber } else { $fHw.SerialNumber } }

        $prevHost = Get-WisPreviousHostname -LocalSetupDir $LocalSetupDir
        $repImgProfile = Get-WisImageProfile -SystemLogDir $SystemLogDir -LocalSetupDir $LocalSetupDir

        # 7. Formiranje JSON payload-a
        $payload = [ordered]@{
            device_id          = $devId
            install_session_id = $sessionId
            computer_name      = if ($env:COMPUTERNAME) { $env:COMPUTERNAME } else { $devId }
            previous_hostname  = $prevHost
            chassis            = $chassis
            serial_number      = $serialNum
            status             = $Status
            duration_seconds   = $totalSec
            duration_formatted = $durFmt
            image_type         = [string]$repImgProfile.image_type
            wim_version        = [string]$repImgProfile.wim_version
            run_meta           = [ordered]@{
                image_type  = [string]$repImgProfile.image_type
                wim_version = [string]$repImgProfile.wim_version
            }
            apps               = $appsData
            errors             = @($errorList)
            hw                 = $hwDict
            timestamp          = (Get-Date).ToString("o")
        }

        if ($benchSummary) {
            $payload["benchmark"] = $benchSummary
        }

        if ($hwTestObj) {
            $payload["hwtest"] = $hwTestObj
        }

        if ((-not $CustomData -or -not $CustomData.ContainsKey("autopilot")) -and (Get-Command Get-WisAutopilotStatusFromFile -ErrorAction SilentlyContinue)) {
            try {
                $apStatusRep = Get-WisAutopilotStatusFromFile -LocalSetupDir $LocalSetupDir -SystemLogDir $SystemLogDir
                if ($apStatusRep) {
                    $payload["autopilot"] = $apStatusRep
                }
            } catch {}
        }

        $jsonBody = New-WisTelemetryJson -Payload $payload -CustomData $CustomData -LocalSetupDir $LocalSetupDir -SystemLogDir $SystemLogDir -Depth 6
        $url = "$($Global:ServisHubUrl)/api/devices/$devId/report"

        # 8. Slanje sa timeoutom 10000 ms, X-ServisHub-Client-Token zaglavljem i 3x retry (1s, 2s)
        $clientTok = $null
        if (Get-Command Get-ServisHubClientToken -ErrorAction SilentlyContinue) {
            try { $clientTok = Get-ServisHubClientToken } catch {}
        }
        if ([string]::IsNullOrWhiteSpace($clientTok) -and -not [string]::IsNullOrWhiteSpace($env:SERVIS_HUB_CLIENT_TOKEN)) {
            $clientTok = $env:SERVIS_HUB_CLIENT_TOKEN.Trim()
        }
        if ([string]::IsNullOrWhiteSpace($clientTok)) {
            foreach ($iniCand in @((Join-Path $LocalSetupDir "Wifi-Servis.ini"), "C:\setup\Wifi-Servis.ini", "C:\Windows\Setup\Scripts\Wifi-Servis.ini")) {
                if ($iniCand -and (Test-Path -LiteralPath $iniCand -ErrorAction SilentlyContinue)) {
                    $inHubSec = $false
                    foreach ($ln in (Get-Content -LiteralPath $iniCand -ErrorAction SilentlyContinue)) {
                        $tln = ([string]$ln).Trim()
                        if ($tln -match '^\[(.+)\]$') {
                            $inHubSec = ($matches[1].Trim().ToLower() -eq 'servishub')
                        } elseif ($inHubSec -and $tln -match '^(?i)ClientToken\s*=\s*(.+)$') {
                            $foundTok = $matches[1].Trim()
                            if (-not [string]::IsNullOrWhiteSpace($foundTok)) {
                                $clientTok = $foundTok
                                break
                            }
                        }
                    }
                }
                if (-not [string]::IsNullOrWhiteSpace($clientTok)) { break }
            }
        }

        $bytes = [System.Text.Encoding]::UTF8.GetBytes($jsonBody)
        $retryDelaysSec = @(0, 1, 2)
        $maxAttempts = $retryDelaysSec.Count
        $sentOk = $false
        $lastErrMsg = ""

        for ($attemptIdx = 0; $attemptIdx -lt $maxAttempts; $attemptIdx++) {
            $delaySec = $retryDelaysSec[$attemptIdx]
            if ($delaySec -gt 0) {
                Start-Sleep -Seconds $delaySec
            }
            $reqStream = $null
            $resp = $null
            try {
                $req = [System.Net.HttpWebRequest]::Create($url)
                $req.Method = "POST"
                $req.ContentType = "application/json; charset=utf-8"
                $req.Timeout = 10000
                $req.ReadWriteTimeout = 10000
                if (-not [string]::IsNullOrWhiteSpace($clientTok)) {
                    $req.Headers.Add("X-ServisHub-Client-Token", $clientTok)
                }
                $req.ContentLength = $bytes.Length

                $reqStream = $req.GetRequestStream()
                $reqStream.Write($bytes, 0, $bytes.Length)
                $reqStream.Close()
                $reqStream = $null

                $resp = $req.GetResponse()
                $resp.Close()
                $resp = $null
                $sentOk = $true
                break
            } catch {
                $lastErrMsg = $_.Exception.Message
                if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                    $attNum = $attemptIdx + 1
                    Write-ServisLog "Send-FleetFinalReport pokusaj $attNum/$maxAttempts nije uspeo ($url): $lastErrMsg" -Level "WARN" -Component "Telemetry"
                }
            } finally {
                if ($reqStream) { try { $reqStream.Dispose() } catch {} }
                if ($resp) { try { $resp.Close() } catch {} }
            }
        }

        if ($sentOk) {
            $Global:WisFinalReportSentStatus = $Status
            if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                Write-ServisLog "Zavrsni flotni izvestaj uspesno poslat na ServisHub: $url ($devId)" -Level "SUCCESS" -Component "Telemetry"
            }
            return $true
        }

        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
            Write-ServisLog "Flotni izvestaj nije poslat na ServisHub posle $maxAttempts pokusaja: $lastErrMsg" -Level "WARN" -Component "Telemetry"
        }
        return $false
    } catch {
        # Fail-safe: tiho nastavljanje bez prekida skripte
        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
            Write-ServisLog "Flotni izvestaj nije poslat na ServisHub (timeout/nedostupan): $($_.Exception.Message)" -Level "DEBUG" -Component "Telemetry"
        }
        return $false
    }
}

Export-ModuleMember -Function `
    Find-ServisHub, `
    Send-ServisHeartbeat, `
    Invoke-ServisRemoteCommand, `
    Get-HardwareTelemetryMetrics, `
    Flush-ServisLogBuffer, `
    Push-ServisLogToHub, `
    Send-ServisLifecycleEvent, `
    Get-FleetHardwareInfo, `
    Get-FleetDeviceId, `
    Get-WisPreviousHostname, `
    Get-WisInstallSessionId, `
    Ensure-WisSessionLogMarker, `
    Get-WisImageProfile, `
    Send-FleetFinalReport, `
    Get-WisTelemetryDiagnostics, `
    New-WisTelemetryJson


