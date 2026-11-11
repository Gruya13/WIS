<#
.MODULE
    WIS.UI.psm1
.DESCRIPTION
    Orkestracioni modul za povezivanje i kontrolu korisnickog interfejsa (WPF GUI / Console fallback)
    i dvosmernu telemetrijsku sinhronizaciju sa centralnim ServisHub-om preko SSE protokola.
.COMPATIBILITY
    Windows PowerShell 5.1+
#>

# ------------------------------------------------------------------------------
# GLOBALNE PROMENLJIVE UI PODSISTEMA
# ------------------------------------------------------------------------------
$Global:WIS_UI_Backend = "Console"
$Global:WIS_UI_Process = $null
$Global:WIS_UI_Stdin = $null
$Global:WIS_UI_EventQueue = $null
$Global:WIS_UI_State = $null
$Global:WIS_UI_IsPaused = $false
$Global:WIS_UI_SkipRequested = $false
$Global:WIS_UI_DelayRestartRequested = $false
$Global:WIS_UI_InUpdate = $false
$Global:WIS_SessionId = $null
$Global:WIS_HubUrl = $null

# ------------------------------------------------------------------------------
# INICIJALIZACIJA INTERFEJSA
# ------------------------------------------------------------------------------

function Initialize-ServisUI {
    [CmdletBinding()]
    param(
        [ValidateSet("WPF", "Console")]
        [string]$Backend = "WPF",
        [string]$HubUrl = "",
        [string]$SessionId = "",
        $ModelInfo = $null
    )

    # 1. Odredjivanje i perzistencija SessionId
    if (-not $SessionId) {
        if ($Global:SessionId) {
            $SessionId = $Global:SessionId
        } elseif ($Global:State -and $Global:State.SessionId) {
            $SessionId = $Global:State.SessionId
        } else {
            $SessionId = "$($env:COMPUTERNAME)-$((Get-Date).ToString('yyyyMMdd-HHmmss'))-$([guid]::NewGuid().ToString().Substring(0, 8))"
        }
    }
    $Global:SessionId = $SessionId
    $Global:WIS_SessionId = $SessionId

    if ($Global:State) {
        if ($Global:State -is [hashtable]) {
            $Global:State["SessionId"] = $SessionId
        } else {
            $Global:State | Add-Member -MemberType NoteProperty -Name "SessionId" -Value $SessionId -Force -ErrorAction SilentlyContinue
        }
    }

    # 2. Odredjivanje HubUrl
    if ($HubUrl) {
        $Global:WIS_HubUrl = $HubUrl
        $Global:ServisHubUrl = $HubUrl
    } elseif ($Global:ServisHubUrl) {
        $Global:WIS_HubUrl = $Global:ServisHubUrl
    }

    # Ako je pokrenuto u Pipeline/Worker modu, UI je već pokrenut kao Master Shell roditelj
    if ($Global:Worker -or $Backend -eq "Pipeline") {
        $Global:WIS_UI_Backend = "Pipeline"
        $initPhase = if ($Global:State -and $Global:State.Phase) { [int]$Global:State.Phase } else { 1 }
        $initModel = if ($ModelInfo) { $ModelInfo } elseif ($Global:HwInfo) { "$($Global:HwInfo.Manufacturer) $($Global:HwInfo.Model)".Trim() } else { "PC" }
        Update-ServisUI -Phase $initPhase -SubPhase "Inicijalizacija" -ProgressPercent 0 -StatusMessage "Sistem pokrenut (Pipeline)" -SessionId $SessionId -HubUrl $Global:WIS_HubUrl
        return
    }

    # 3. Provera i pokretanje WPF procesa ukoliko je Backend == 'WPF'
    $uiStarted = $false
    if ($Backend -eq "WPF") {
        $candidatePaths = [System.Collections.Generic.List[string]]::new()
        $candidatePaths.Add("C:\setup\Skripte\WIS.UI\WIS.UI.exe")
        $candidatePaths.Add((Join-Path $PSScriptRoot "..\WIS.UI\WIS.UI.exe"))
        $candidatePaths.Add((Join-Path $PSScriptRoot "..\..\Programi\WIS.UI\WIS.UI.exe"))
        $candidatePaths.Add("C:\setup\Programi\WIS.UI\WIS.UI.exe")
        $candidatePaths.Add("C:\setup\WIS.UI\WIS.UI.exe")
        if ($Global:SourceRoot) {
            $candidatePaths.Add((Join-Path $Global:SourceRoot "Skripte\WIS.UI\WIS.UI.exe"))
            $candidatePaths.Add((Join-Path $Global:SourceRoot "Programi\WIS.UI\WIS.UI.exe"))
        }

        $targetExe = $null
        foreach ($p in $candidatePaths) {
            try {
                if ($p -and (Test-Path $p)) {
                    $targetExe = (Resolve-Path $p).Path
                    break
                }
            } catch {}
        }

        if (-not $targetExe) {
            # Pokusaj automatskog tihog build-a ako postoji Build.bat u WIS.UI folderu
            $buildBatCandidates = [System.Collections.Generic.List[string]]::new()
            $buildBatCandidates.Add("C:\setup\Skripte\WIS.UI\Build.bat")
            $buildBatCandidates.Add((Join-Path $PSScriptRoot "..\WIS.UI\Build.bat"))
            $buildBatCandidates.Add((Join-Path $PSScriptRoot "..\..\Programi\WIS.UI\Build.bat"))
            $buildBatCandidates.Add("C:\setup\Programi\WIS.UI\Build.bat")
            $buildBatCandidates.Add("C:\setup\WIS.UI\Build.bat")
            if ($Global:SourceRoot) {
                $buildBatCandidates.Add((Join-Path $Global:SourceRoot "Skripte\WIS.UI\Build.bat"))
                $buildBatCandidates.Add((Join-Path $Global:SourceRoot "Programi\WIS.UI\Build.bat"))
            }

            $targetBat = $null
            foreach ($b in $buildBatCandidates) {
                try {
                    if ($b -and (Test-Path $b)) {
                        $targetBat = (Resolve-Path $b).Path
                        break
                    }
                } catch {}
            }

            if ($targetBat) {
                if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                    Write-ServisLog "WIS.UI.exe nije pronadjen, ali je pronadjen $targetBat. Pokrecem automatsko tiho kompajliranje..." -Level "INFO" -Component "UI"
                }
                try {
                    $psiBuild = [System.Diagnostics.ProcessStartInfo]::new()
                    $psiBuild.FileName = "cmd.exe"
                    $psiBuild.Arguments = "/c `"$targetBat`" /silent"
                    $psiBuild.UseShellExecute = $false
                    $psiBuild.CreateNoWindow = $true
                    $psiBuild.WindowStyle = [System.Diagnostics.ProcessWindowStyle]::Hidden
                    $buildProc = [System.Diagnostics.Process]::Start($psiBuild)
                    if ($buildProc) {
                        $buildProc.WaitForExit(60000)
                    }
                } catch {}

                # Ponovna provera kandidata za WIS (Windows Installation Assistant).UI.exe nakon zavrsenog build-a
                foreach ($p in $candidatePaths) {
                    try {
                        if ($p -and (Test-Path $p)) {
                            $targetExe = (Resolve-Path $p).Path
                            break
                        }
                    } catch {}
                }
            }
        }

        # 1. Proveri da li je WIS.UI već pokrenut (Master Shell režim) ili radnik radi u fazi
        $existingUi = Get-Process -Name "WIS.UI" -ErrorAction SilentlyContinue
        if ($existingUi) {
            if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                Write-ServisLog "Detektovan postojeci WIS.UI Master Shell (PID: $(($existingUi | Select-Object -First 1).Id)). Radnik radi pod postojecim interfejsom." -Level "INFO" -Component "UI"
            }
            $Global:WIS_UI_Backend = "MasterWPF"
            $Global:WIS_UI_Process = $existingUi | Select-Object -First 1
            $uiStarted = $true
            return $true
        }

        if ($targetExe) {
            try {
                $psi = [System.Diagnostics.ProcessStartInfo]::new()
                $psi.FileName = $targetExe
                $psi.Arguments = "--session `"$SessionId`""
                $psi.UseShellExecute = $false
                $psi.RedirectStandardInput = $true
                $psi.RedirectStandardOutput = $true
                $psi.RedirectStandardError = $true
                $psi.CreateNoWindow = $false
                $psi.StandardOutputEncoding = [System.Text.Encoding]::UTF8

                $proc = [System.Diagnostics.Process]::new()
                $proc.StartInfo = $psi

                $eventQueue = [System.Collections.Concurrent.ConcurrentQueue[string]]::new()
                $Global:WIS_UI_EventQueue = $eventQueue

                # Asinhrono citanje sa Stdout-a GUI procesa
                $proc.add_OutputDataReceived({
                    param($sender, $e)
                    if ($e.Data) {
                        $Global:WIS_UI_EventQueue.Enqueue($e.Data)
                    }
                })

                $started = $proc.Start()
                if ($started) {
                    $proc.BeginOutputReadLine()
                    $Global:WIS_UI_Process = $proc
                    $Global:WIS_UI_Stdin = $proc.StandardInput
                    $Global:WIS_UI_Backend = "WPF"
                    $uiStarted = $true
                    if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                        Write-ServisLog "Uspesno pokrenut WIS.UI WPF interfejs (PID: $($proc.Id), Putanja: $targetExe)" -Level "SUCCESS" -Component "UI"
                    }
                }
            } catch {
                if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                    Write-ServisLog "Greska pri pokretanju WIS.UI.exe ($($_.Exception.Message)). Prelazim na konzolni rezim." -Level "WARN" -Component "UI"
                }
            }
        }
    }

    # 4. Graceful fallback na konzolu ako EXE ne postoji ili nije pokrenut
    if (-not $uiStarted) {
        $Global:WIS_UI_Backend = "Console"
        $Global:WIS_UI_Process = $null
        $Global:WIS_UI_Stdin = $null
        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
            Write-ServisLog "WIS.UI.exe nije pronadjen ili je deaktiviran. Koristi se 'Console' backend." -Level "INFO" -Component "UI"
        }
    }

    # 5. Slanje inicijalnog stanja
    $initPhase = if ($Global:State -and $Global:State.Phase) { [int]$Global:State.Phase } else { 1 }
    $initModel = if ($ModelInfo) { $ModelInfo } elseif ($Global:HwInfo) { "$($Global:HwInfo.Manufacturer) $($Global:HwInfo.Model)".Trim() } else { "PC" }
    
    Update-ServisUI -Phase $initPhase -SubPhase "Inicijalizacija" -ProgressPercent 0 -StatusMessage "Sistem pokrenut" -SessionId $SessionId -HubUrl $Global:WIS_HubUrl
}

# ------------------------------------------------------------------------------
# AZURIRANJE STANJA I SINHRONIZACIJA (GUI + ZERO-BLOCK HUB SSE MIRROR)
# ------------------------------------------------------------------------------

function Update-ServisUI {
    [CmdletBinding()]
    param(
        $Phase = $null,
        [string]$SubPhase = "",
        $ProgressPercent = $null,
        [string]$StatusMessage = "",
        [string]$LogLine = "",
        $TabData = $null,
        [switch]$UsbFree,
        [string]$HubUrl = "",
        [string]$SessionId = "",
        [switch]$IsPaused,
        $ModelInfo = $null,
        [string]$AppId = "",
        [string]$AppStatus = "",
        [string]$AppDetail = "",
        $Hw = $null,
        $Diag = $null,
        $Network = $null,
        $Apps = $null,
        [Parameter(ValueFromRemainingArguments=$true)]
        $ExtraParams
    )

    # Zastita od beskonacne rekurzije logera
    if ($Global:WIS_UI_InUpdate) { return }
    $Global:WIS_UI_InUpdate = $true

    try {
        # 1. Odrzavanje akumuliranog stanja
        if (-not $Global:WIS_UI_State) {
            $Global:WIS_UI_State = [ordered]@{
                session_id       = if ($SessionId) { $SessionId } elseif ($Global:WIS_SessionId) { $Global:WIS_SessionId } else { "INIT" }
                computer_name    = if ($env:COMPUTERNAME) { $env:COMPUTERNAME } else { "KLIJENT" }
                phase            = 1
                sub_phase        = ""
                progress_percent = 0
                status_message   = ""
                last_log         = ""
                tab_data         = @{}
                usb_free         = $false
                is_paused        = $false
                model_info       = ""
                hw               = @{}
                diag             = @{}
                network          = @{}
                apps             = @{}
                updated_at       = (Get-Date).ToString("o")
            }
        }

        if ($SessionId) { $Global:WIS_UI_State["session_id"] = $SessionId }
        if ($null -ne $Phase) { $Global:WIS_UI_State["phase"] = [int]$Phase }
        if ($SubPhase) { $Global:WIS_UI_State["sub_phase"] = $SubPhase }
        if ($null -ne $ProgressPercent) { $Global:WIS_UI_State["progress_percent"] = [int]$ProgressPercent }
        if ($StatusMessage) { $Global:WIS_UI_State["status_message"] = $StatusMessage }
        if ($LogLine) { $Global:WIS_UI_State["last_log"] = $LogLine }
        if ($TabData) {
            if ($TabData -is [hashtable] -or $TabData -is [System.Collections.IDictionary]) {
                foreach ($k in $TabData.Keys) {
                    $Global:WIS_UI_State["tab_data"][$k] = $TabData[$k]
                }
            } else {
                $Global:WIS_UI_State["tab_data"] = $TabData
            }
        }
        if ($PSBoundParameters.ContainsKey("UsbFree")) { $Global:WIS_UI_State["usb_free"] = [bool]$UsbFree }
        if ($PSBoundParameters.ContainsKey("IsPaused")) {
            $Global:WIS_UI_State["is_paused"] = [bool]$IsPaused
            $Global:WIS_UI_IsPaused = [bool]$IsPaused
        }
        if ($ModelInfo) { $Global:WIS_UI_State["model_info"] = "$ModelInfo" }

        # Obrada direktno prosledjenih parametara hw, diag, network, apps
        if ($Hw) {
            if ($Hw -is [hashtable] -or $Hw -is [System.Collections.IDictionary]) {
                foreach ($hk in $Hw.Keys) { $Global:WIS_UI_State["hw"][$hk] = $Hw[$hk] }
            } else { $Global:WIS_UI_State["hw"] = $Hw }
        }
        if ($Diag) {
            if ($Diag -is [hashtable] -or $Diag -is [System.Collections.IDictionary]) {
                foreach ($dk in $Diag.Keys) { $Global:WIS_UI_State["diag"][$dk] = $Diag[$dk] }
            } else { $Global:WIS_UI_State["diag"] = $Diag }
        }
        if ($Network) {
            if ($Network -is [hashtable] -or $Network -is [System.Collections.IDictionary]) {
                foreach ($nk in $Network.Keys) { $Global:WIS_UI_State["network"][$nk] = $Network[$nk] }
            } else { $Global:WIS_UI_State["network"] = $Network }
        }
        if ($Apps) {
            if ($Apps -is [hashtable] -or $Apps -is [System.Collections.IDictionary]) {
                foreach ($ak in $Apps.Keys) { $Global:WIS_UI_State["apps"][$ak] = $Apps[$ak] }
            } else { $Global:WIS_UI_State["apps"] = $Apps }
        }

        # Obrada aplikacijskog ažuriranja (-AppId, -AppStatus, -AppDetail)
        $isAppEvent = $false
        if ($AppId -and $AppStatus) {
            if (-not ($Global:WIS_UI_State["apps"] -is [hashtable] -or $Global:WIS_UI_State["apps"] -is [System.Collections.IDictionary])) {
                $Global:WIS_UI_State["apps"] = @{}
            }
            $Global:WIS_UI_State["apps"][$AppId] = $AppStatus
            $isAppEvent = $true
        }

        # 1a. Automatsko obogaćivanje iz $Global:HwInfo ako hw nije prosleđen ili je prazan
        $hwObj = $Global:HwInfo
        if (-not $Global:WIS_UI_State["hw"] -or ($Global:WIS_UI_State["hw"] -is [System.Collections.ICollection] -and $Global:WIS_UI_State["hw"].Count -eq 0)) {
            $mfg = if ($hwObj -and $hwObj.Manufacturer -and $hwObj.Manufacturer -ne "Nepoznato") { $hwObj.Manufacturer.Trim() } else { "" }
            $mod = if ($hwObj -and $hwObj.Model -and $hwObj.Model -ne "Nepoznato") { $hwObj.Model.Trim() } else { "" }
            $fullModel = if ($mfg -and $mod) {
                if ($mod.StartsWith($mfg, [System.StringComparison]::OrdinalIgnoreCase)) { $mod } else { "$mfg $mod" }
            } elseif ($mod) { $mod } elseif ($mfg) { $mfg } elseif ($Global:WIS_UI_State["model_info"]) { "$($Global:WIS_UI_State['model_info'])" } else { "Standard PC" }

            $Global:WIS_UI_State["hw"] = [ordered]@{
                model   = $fullModel
                serial  = if ($hwObj -and $hwObj.SerialNumber) { "$($hwObj.SerialNumber)".Trim() } else { "N/A" }
                cpu     = if ($hwObj -and $hwObj.CpuStr) { "$($hwObj.CpuStr)".Trim() } else { "Standard CPU" }
                ram     = if ($hwObj -and $hwObj.RamStr) { "$($hwObj.RamStr)".Trim() } else { "Standard RAM" }
                disk    = if ($hwObj -and $hwObj.DiskStr) { "$($hwObj.DiskStr)".Trim() } else { "Standard Disk" }
                battery = if ($hwObj -and $hwObj.BatteryStr) { "$($hwObj.BatteryStr)".Trim() } else { "AC Napajanje" }
            }
        }

        # 1b. Automatsko obogaćivanje dijagnostike (diag)
        if (-not $Global:WIS_UI_State["diag"] -or ($Global:WIS_UI_State["diag"] -is [System.Collections.ICollection] -and $Global:WIS_UI_State["diag"].Count -eq 0)) {
            $wear = 100
            $smartSt = "Healthy"
            $tbw = 0.0
            $realloc = 0
            $critWarn = 0
            $batHealth = 100
            $batStatus = "AC Napajanje / Desktop"
            $batCycles = "N/A"
            $isLaptop = $false

            if ($hwObj) {
                if ($hwObj.DiskHealth) { $wear = [int]$hwObj.DiskHealth }
                elseif ($hwObj.HardwareHealth -and $hwObj.HardwareHealth.DiskHealth) { $wear = [int]$hwObj.HardwareHealth.DiskHealth }

                if ($hwObj.HealthStatus) { $smartSt = "$($hwObj.HealthStatus)" }
                elseif ($hwObj.HardwareHealth -and $hwObj.HardwareHealth.OverallStatus) { $smartSt = "$($hwObj.HardwareHealth.OverallStatus)" }

                if ($hwObj.HardwareHealth) {
                    if ($null -ne $hwObj.HardwareHealth.TbwTB) { $tbw = $hwObj.HardwareHealth.TbwTB }
                    if ($null -ne $hwObj.HardwareHealth.ReallocatedSectors) { $realloc = [int]$hwObj.HardwareHealth.ReallocatedSectors }
                    if ($null -ne $hwObj.HardwareHealth.CriticalWarning) { $critWarn = [int]$hwObj.HardwareHealth.CriticalWarning }
                    if ($null -ne $hwObj.HardwareHealth.BatteryHealth) { $batHealth = [int]$hwObj.HardwareHealth.BatteryHealth }
                    if ($null -ne $hwObj.HardwareHealth.IsLaptop) { $isLaptop = [bool]$hwObj.HardwareHealth.IsLaptop }
                }

                if ($hwObj.BatteryStr) {
                    $batStatus = "$($hwObj.BatteryStr)"
                    if ($hwObj.BatteryStr -notmatch "N/A|Nema|AC Napajanje") { $isLaptop = $true }
                }
                if ($hwObj.BatteryCycles) { $batCycles = "$($hwObj.BatteryCycles)" }
            }

            $cpuUse = if ($Global:WIS_UI_State["cpu_usage"]) { $Global:WIS_UI_State["cpu_usage"] } else { "Očitavanje..." }
            $ramUse = if ($Global:WIS_UI_State["ram_usage"]) { $Global:WIS_UI_State["ram_usage"] } else { "Očitavanje..." }

            $Global:WIS_UI_State["diag"] = [ordered]@{
                wear                = $wear
                smart_status        = $smartSt
                tbw                 = $tbw
                reallocated_sectors = $realloc
                critical_warning    = $critWarn
                battery_health      = $batHealth
                battery_status      = $batStatus
                battery_cycles      = $batCycles
                is_laptop           = $isLaptop
                cpu_usage           = $cpuUse
                ram_usage           = $ramUse
                power_on_hours      = if ($hwObj -and $hwObj.HardwareHealth -and $null -ne $hwObj.HardwareHealth.PowerOnHours) { $hwObj.HardwareHealth.PowerOnHours } else { $null }
                is_smart_estimated  = if ($hwObj -and $hwObj.HardwareHealth -and $hwObj.HardwareHealth.IsSmartEstimated) { $true } else { $false }
            }
        } elseif ($Global:WIS_UI_State["diag"] -is [System.Collections.IDictionary]) {
            if (-not $Global:WIS_UI_State["diag"].Contains("power_on_hours")) {
                $Global:WIS_UI_State["diag"]["power_on_hours"] = if ($hwObj -and $hwObj.HardwareHealth -and $null -ne $hwObj.HardwareHealth.PowerOnHours) { $hwObj.HardwareHealth.PowerOnHours } else { $null }
            }
            if (-not $Global:WIS_UI_State["diag"].Contains("is_smart_estimated")) {
                $Global:WIS_UI_State["diag"]["is_smart_estimated"] = if ($hwObj -and $hwObj.HardwareHealth -and $hwObj.HardwareHealth.IsSmartEstimated) { $true } else { $false }
            }
        }

        # 1c. Automatsko obogaćivanje mreže (network)
        if (-not $Global:WIS_UI_State["network"] -or ($Global:WIS_UI_State["network"] -is [System.Collections.ICollection] -and $Global:WIS_UI_State["network"].Count -eq 0)) {
            $wifiSsid = if ($Global:State -and $Global:State.WifiSSID) { $Global:State.WifiSSID } else { "Nije povezano" }
            $ipAddr = "127.0.0.1"
            $connType = "Detekcija mrežnih kartica..."
            $nasLat = if ($Global:State -and $Global:State.NasLatency) { "$($Global:State.NasLatency) ms" } else { "N/A" }
            $nasStat = if ($Global:ServisHubUrl) { "Povezan" } else { "Lokalno" }
            $dlMb = if ($Global:State -and $Global:State.DownloadedMB) { "$($Global:State.DownloadedMB)" } else { "0" }

            if (Get-Command Get-NetworkDetailStatus -ErrorAction SilentlyContinue) {
                try {
                    $ns = Get-NetworkDetailStatus
                    if ($ns) {
                        if ($ns.WifiSSID) { $wifiSsid = $ns.WifiSSID }
                        if ($ns.LanConnected -and $ns.WifiConnected) { $connType = "Ethernet + Wi-Fi" }
                        elseif ($ns.LanConnected) { $connType = if ($ns.LanSpeed) { "Ethernet ($($ns.LanSpeed))" } else { "Ethernet" } }
                        elseif ($ns.WifiConnected) { $connType = if ($ns.WifiSignal) { "Wi-Fi ($($ns.WifiSignal))" } else { "Wi-Fi" } }
                    }
                } catch {}
            }

            try {
                $ipObj = Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue | Where-Object {
                    $_.InterfaceAlias -notmatch 'Loopback' -and $_.IPAddress -notmatch '^169\.254\.'
                } | Select-Object -First 1
                if ($ipObj -and $ipObj.IPAddress) { $ipAddr = $ipObj.IPAddress }
            } catch {}

            $Global:WIS_UI_State["network"] = [ordered]@{
                wifi_ssid       = $wifiSsid
                ip              = $ipAddr
                connection_type = $connType
                nas_latency     = $nasLat
                nas_status      = $nasStat
                downloaded_mb   = $dlMb
            }
        }

        # 1d. Sinhronizacija aplikacija (apps) ako nije postavljeno
        if (-not $Global:WIS_UI_State["apps"] -or ($Global:WIS_UI_State["apps"] -is [System.Collections.ICollection] -and $Global:WIS_UI_State["apps"].Count -eq 0)) {
            $initApps = [ordered]@{
                DotNet     = "WAIT"
                Chrome     = "WAIT"
                WinRAR     = "WAIT"
                Acrobat    = "WAIT"
                Office     = "WAIT"
                BatteryBar = "WAIT"
                License    = "WAIT"
                Shortcuts  = "WAIT"
            }
            if (Get-Command Get-RealAppStatuses -ErrorAction SilentlyContinue) {
                try {
                    $ras = Get-RealAppStatuses
                    if ($ras -and $ras.Items) {
                        foreach ($it in $ras.Items) {
                            $initApps[$it.Id] = $it.Status
                        }
                    }
                } catch {}
            }
            $Global:WIS_UI_State["apps"] = $initApps
        }

        # 1e. Obavezno obezbeđivanje svih unificiranih ključeva u root JSON-u:
        # progress, progress_percent, worker_progress, live_status, status_message, log, last_log, hw, diag, network, apps
        $progVal = [int]$Global:WIS_UI_State["progress_percent"]
        $stMsg = "$($Global:WIS_UI_State['status_message'])"
        $lLog = "$($Global:WIS_UI_State['last_log'])"

        $Global:WIS_UI_State["progress"] = $progVal
        $Global:WIS_UI_State["progress_percent"] = $progVal
        $Global:WIS_UI_State["worker_progress"] = if ($Global:WIS_UI_State.Contains("worker_progress") -and $null -ne $Global:WIS_UI_State["worker_progress"]) {
            [int]$Global:WIS_UI_State["worker_progress"]
        } else {
            $progVal
        }
        $Global:WIS_UI_State["live_status"] = $stMsg
        $Global:WIS_UI_State["status_message"] = $stMsg
        $Global:WIS_UI_State["last_log"] = $lLog

        if (-not $Global:WIS_UI_State.Contains("log") -or $null -eq $Global:WIS_UI_State["log"]) {
            $Global:WIS_UI_State["log"] = [ordered]@{
                time    = (Get-Date).ToString("HH:mm:ss")
                level   = "INFO"
                tag     = "[WIS]"
                message = $lLog
            }
        }

        if ($isAppEvent) {
            $Global:WIS_UI_State["event"] = "APP_UPDATE"
            $Global:WIS_UI_State["id"] = $AppId
            $Global:WIS_UI_State["status"] = $AppStatus
            if ($AppDetail) { $Global:WIS_UI_State["detail"] = $AppDetail }
        } else {
            $null = $Global:WIS_UI_State.Remove("event")
            $null = $Global:WIS_UI_State.Remove("id")
            $null = $Global:WIS_UI_State.Remove("status")
            $null = $Global:WIS_UI_State.Remove("detail")
        }

        $Global:WIS_UI_State["updated_at"] = (Get-Date).ToString("o")

        # 2. Slanje stanja na Stdin WPF GUI procesa ili Stdout u Pipeline/Master Runner modu
        $jsonState = ConvertTo-Json $Global:WIS_UI_State -Compress -Depth 6

        $isWorkerPipeline = ($Global:Worker -and $Global:Worker -ne "") -or ($Global:WIS_UI_Backend -eq "Pipeline") -or ($Global:WIS_UI_Backend -eq "MasterWPF") -or ($env:WIS_WORKER -eq "1") -or ($Global:Phase -and $Global:Phase -gt 0)
        if ($isWorkerPipeline) {
            try {
                if ($null -ne [Console]::Out) {
                    [Console]::Out.WriteLine($jsonState)
                    [Console]::Out.Flush()
                }
            } catch {
                # Nikada ne bacati izuzetak i ne blokirati ako STDOUT baci IOException, ObjectDisposedException ili ako pipe nije otvoren
            }
        } elseif ($Global:WIS_UI_Backend -eq "WPF") {
            if ($Global:WIS_UI_Process -and (-not $Global:WIS_UI_Process.HasExited) -and $Global:WIS_UI_Stdin) {
                try {
                    $Global:WIS_UI_Stdin.WriteLine($jsonState)
                    $Global:WIS_UI_Stdin.Flush()
                } catch {
                    $Global:WIS_UI_Backend = "Console"
                    if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                        Write-ServisLog "Greska pri upisu na WIS.UI Stdin. Prelazim na Console fallback." -Level "WARN" -Component "UI"
                    }
                }
            } else {
                $Global:WIS_UI_Backend = "Console"
            }
        }

        # 3. Konzolni ispis ukoliko je aktivan Console mod
        if ($Global:WIS_UI_Backend -eq "Console" -and ($StatusMessage -or $SubPhase)) {
            $pNum = $Global:WIS_UI_State["phase"]
            $pSub = $Global:WIS_UI_State["sub_phase"]
            $pMsg = $Global:WIS_UI_State["status_message"]
            $pPct = $Global:WIS_UI_State["progress_percent"]
            if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                Write-ServisLog "[Faza $pNum] $($pSub): $pMsg ($pPct%)" -Level "INFO" -Component "UI"
            }
        }

        # 4. Zero-Block asinhrono slanje stanja na ServisHub (/api/session/<session_id>/state)
        $targetHub = if ($HubUrl) { $HubUrl } elseif ($Global:WIS_HubUrl) { $Global:WIS_HubUrl } else { $Global:ServisHubUrl }
        $targetSession = if ($SessionId) { $SessionId } elseif ($Global:WIS_SessionId) { $Global:WIS_SessionId } else { $Global:SessionId }

        if ($targetHub -and $targetSession) {
            $statePostUrl = "$targetHub/api/session/$targetSession/state"
            [System.Threading.ThreadPool]::QueueUserWorkItem({
                param($tuple)
                try {
                    $postUrl = $tuple[0]
                    $jsonPayload = $tuple[1]
                    $req = [System.Net.HttpWebRequest]::Create($postUrl)
                    $req.Method = "POST"
                    $req.ContentType = "application/json; charset=utf-8"
                    $req.Timeout = 2000
                    $req.ReadWriteTimeout = 2000
                    $bytes = [System.Text.Encoding]::UTF8.GetBytes($jsonPayload)
                    $req.ContentLength = $bytes.Length
                    $stream = $req.GetRequestStream()
                    $stream.Write($bytes, 0, $bytes.Length)
                    $stream.Close()
                    $resp = $req.GetResponse()
                    $resp.Close()
                } catch {}
            }, @($statePostUrl, $jsonState)) | Out-Null
        }
    } catch {
        # Obezbediti da nikakva runtime greska u obradi UI stanja ili IPC slanju ne obori Master Runner ili radnika
    } finally {
        $Global:WIS_UI_InUpdate = $false
    }
}

# ------------------------------------------------------------------------------
# PRIJEM DOGADJAJA IZ GUI PROCESA (ASINHRONI STDOUT EVENT READER)
# ------------------------------------------------------------------------------

function Get-ServisUIEvent {
    [CmdletBinding()]
    param()

    if ($Global:WIS_UI_EventQueue) {
        $rawEvent = $null
        if ($Global:WIS_UI_EventQueue.TryDequeue([ref]$rawEvent)) {
            if ($rawEvent) {
                $rawTrim = $rawEvent.Trim()
                if ($rawTrim.StartsWith("{") -and $rawTrim.EndsWith("}")) {
                    try {
                        $parsed = ConvertFrom-Json $rawTrim
                        return $parsed
                    } catch {}
                }
                return [PSCustomObject]@{
                    Event     = $rawTrim
                    Timestamp = (Get-Date).ToString("o")
                    Raw       = $rawEvent
                }
            }
        }
    }
    return $null
}

# ------------------------------------------------------------------------------
# KOOPERATIVNA PAUZA I SIGURNE KONTROLNE TACKE
# ------------------------------------------------------------------------------

function Wait-ServisUICooperativePause {
    [CmdletBinding()]
    param(
        [string]$CheckpointName = "Kontrolna tacka"
    )

    # 1. Provera kontrolnih markera na fajl sistemu (.pause i .skip)
    if (Test-Path "C:\setup\.pause") {
        $Global:WIS_UI_IsPaused = $true
    }
    if (Test-Path "C:\setup\.skip") {
        $Global:WIS_UI_SkipRequested = $true
        Remove-Item "C:\setup\.skip" -Force -ErrorAction SilentlyContinue
    }

    # 2. Obradi pristigle dogadjaje iz reda
    while ($evt = Get-ServisUIEvent) {
        $evtName = if ($evt.Event) { $evt.Event } elseif ($evt.event) { $evt.event } elseif ($evt.type) { $evt.type } else { "$evt" }
        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
            Write-ServisLog "Primljen UI dogadjaj sa Stdout-a: $evtName (Checkpoint: $CheckpointName)" -Level "INFO" -Component "UI"
        }

        if ($evtName -match "PAUSE_TOGGLE") {
            $Global:WIS_UI_IsPaused = -not $Global:WIS_UI_IsPaused
        } elseif ($evtName -match "SKIP_STEP_REQUESTED") {
            $Global:WIS_UI_SkipRequested = $true
        } elseif ($evtName -match "DELAY_RESTART_REQUESTED") {
            $Global:WIS_UI_DelayRestartRequested = $true
        }
    }

    # 3. Ako je aktivna pauza, udji u petlju cekanja
    if ($Global:WIS_UI_IsPaused) {
        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
            Write-ServisLog "Kooperativna pauza aktivirana na bezbednoj tacki: $CheckpointName. Cekam na nastavak..." -Level "WARN" -Component "UI"
        }
        Update-ServisUI -StatusMessage "PAUZIRANO ($CheckpointName) - Cekam nastavak..." -IsPaused:$true

        while ($Global:WIS_UI_IsPaused) {
            Start-Sleep -Milliseconds 500

            # Provera uklanjanja .pause markera
            if (-not (Test-Path "C:\setup\.pause")) {
                $Global:WIS_UI_IsPaused = $false
                if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                    Write-ServisLog "Marker C:\setup\.pause uklonjen. Nastavljam rad na: $CheckpointName" -Level "SUCCESS" -Component "UI"
                }
                Update-ServisUI -StatusMessage "Nastavak rada ($CheckpointName)..." -IsPaused:$false
                break
            }
            while ($evt = Get-ServisUIEvent) {
                $evtName = if ($evt.Event) { $evt.Event } elseif ($evt.event) { $evt.event } elseif ($evt.type) { $evt.type } else { "$evt" }
                if ($evtName -match "PAUSE_TOGGLE") {
                    $Global:WIS_UI_IsPaused = $false
                    if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                        Write-ServisLog "Kooperativna pauza deaktivirana. Nastavljam rad na: $CheckpointName" -Level "SUCCESS" -Component "UI"
                    }
                    Update-ServisUI -StatusMessage "Nastavak rada ($CheckpointName)..." -IsPaused:$false
                    break
                }
            }
        }
    }
}

# ------------------------------------------------------------------------------
# ZATVARANJE INTERFEJSA I OSLOBADJANJE RESURSA
# ------------------------------------------------------------------------------

function Close-ServisUI {
    [CmdletBinding()]
    param(
        [int]$ExitCode = 0,
        [switch]$Force
    )

    if ($Global:WIS_UI_Process -and (-not $Global:WIS_UI_Process.HasExited)) {
        try {
            if ($Global:WIS_UI_Stdin) {
                $quitMsg = ConvertTo-Json @{ command = "QUIT"; exit_code = $ExitCode } -Compress
                $Global:WIS_UI_Stdin.WriteLine($quitMsg)
                $Global:WIS_UI_Stdin.Flush()
            }
            if (-not $Global:WIS_UI_Process.WaitForExit(2000)) {
                if ($Force) {
                    $Global:WIS_UI_Process.Kill()
                }
            }
        } catch {}
    }

    try {
        if ($Global:WIS_UI_Stdin) { $Global:WIS_UI_Stdin.Close(); $Global:WIS_UI_Stdin.Dispose() }
        if ($Global:WIS_UI_Process) { $Global:WIS_UI_Process.Dispose() }
    } catch {}

    $Global:WIS_UI_Process = $null
    $Global:WIS_UI_Stdin = $null
    $Global:WIS_UI_Backend = "Console"
    if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
        Write-ServisLog "WIS UI interfejs zatvoren i resursi su oslobodjeni." -Level "INFO" -Component "UI"
    }
}

Export-ModuleMember -Function `
    Initialize-ServisUI, `
    Update-ServisUI, `
    Get-ServisUIEvent, `
    Wait-ServisUICooperativePause, `
    Close-ServisUI
