<#
.MODULE
    WIS.Update.psm1
.DESCRIPTION
    Windows Update COM API orkestracija, iterativni krugovi preuzimanja i instalacije drajvera i zakrpa za WIS,
    sa bezbednim pokretanjem pozadinskog radnika za aplikacije.
.COMPATIBILITY
    Windows PowerShell 5.1+
#>

function Get-WUQueueDisplay {
    param($Queue)
    $lines = @("   LISTA PREOSTALIH AZURIRANJA:")
    if (-not $Queue) {
        $lines += "      [OK] Svi paketi iz ovog kruga su uspesno instalirani!"
        return $lines
    }
    $rem = $Queue.Count
    if ($rem -eq 0) {
        $lines += "      [OK] Svi paketi iz ovog kruga su uspesno instalirani!"
        return $lines
    }
    $showMax = [math]::Min(8, $rem)
    for ($k = 0; $k -lt $showMax; $k++) {
        $lines += "      $($k + 1). $($Queue[$k].Title)"
    }
    return $lines
}

function Format-WUProgress {
    param([int]$Done, [int]$Total)
    if ($Total -le 0) { return "0 od 0 (0%)" }
    $Done = [math]::Min($Total, [math]::Max(0, $Done))
    $pct = [math]::Round(($Done / $Total) * 100)
    $slots = 18
    $filled = [math]::Min($slots, [math]::Max(0, [math]::Round(($Done / $Total) * $slots)))
    $empty = $slots - $filled
    $bar = ("=" * $filled) + ("-" * $empty)
    return "Paket $Done od $Total ($($pct)%) [$bar]"
}

function Get-WUConfigMode {
    $candidates = @(
        "C:\setup\Wifi-Servis.ini",
        (Join-Path $PSScriptRoot "Wifi-Servis.ini"),
        (Join-Path (Split-Path $PSScriptRoot -Parent) "Wifi-Servis.ini"),
        (Join-Path (Split-Path $PSScriptRoot -Parent) "Skripte\Wifi-Servis.ini")
    )
    if (Get-Command Get-CimInstance -ErrorAction SilentlyContinue) {
        $drives = Get-CimInstance Win32_LogicalDisk -Filter "DriveType=2" -ErrorAction SilentlyContinue
        foreach ($d in $drives) {
            $candidates += (Join-Path "$($d.DeviceID)\" "Wifi-Servis.ini")
            $candidates += (Join-Path "$($d.DeviceID)\" "Skripte\Wifi-Servis.ini")
        }
    }
    foreach ($path in $candidates) {
        if ($path -and (Test-Path $path)) {
            $inWU = $false
            foreach ($line in (Get-Content $path -ErrorAction SilentlyContinue)) {
                $trimmed = $line.Trim()
                if ($trimmed -match '^\[WindowsUpdate\]') { $inWU = $true; continue }
                if ($trimmed -match '^\[' -and $inWU) { $inWU = $false }
                if ($inWU -and $trimmed -match '^Mode\s*=\s*(.+)$') {
                    return $matches[1].Trim()
                }
            }
        }
    }
    return "All"
}

function Test-IsDriverUpdate {
    param($UpdateItem)
    if (-not $UpdateItem) { return $false }
    
    # 1. WUA Tip: 2 = Driver
    if ($UpdateItem.Type -eq 2) { return $true }
    
    # 2. Provera kategorija
    if ($UpdateItem.Categories) {
        foreach ($cat in $UpdateItem.Categories) {
            if ($cat.Type -match "Driver" -or $cat.Name -match "Driver") {
                return $true
            }
        }
    }
    
    # 3. Provera naslova
    $title = $UpdateItem.Title
    if ($title -match "(?i)\b(Driver|Intel|Realtek|Synaptics|AMD|NVIDIA|Qualcomm|MediaTek|Broadcom|Conexant|Elan|HPIC|Touchpad|Chipset|Graphics|Display|Sound|Audio|Camera|Bluetooth|Wireless|Wi-Fi|Net|Ethernet|LAN|PCI|ACPI|Firmware)\b") {
        if ($title -notmatch "(?i)(Cumulative Update|Kumulativno|Security Update for Windows|Windows 11 Version|Malicious Software|\.NET Framework)") {
            return $true
        }
    }
    return $false
}

function Test-IsCumulativeUpdate {
    param($UpdateItem)
    if (-not $UpdateItem) { return $false }
    $title = $UpdateItem.Title
    if ($title -match "(?i)(Cumulative Update|Kumulativno azuriranje|Security Update for Windows|Windows 11 Version.*Update)") {
        return $true
    }
    return $false
}

function Start-AppWorker {
    param(
        [string]$ScriptPath = "C:\setup\Install-ServisApps.ps1"
    )
    if ($Global:AppWorkerProc -and (-not $Global:AppWorkerProc.HasExited)) {
        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
            Write-ServisLog "Pozadinski radnik za aplikacije je vec aktivan (PID: $($Global:AppWorkerProc.Id))." -Level "DEBUG" -Component "Worker"
        }
        return $Global:AppWorkerProc
    }

    # Proveri da li su programi vec kompletirani
    $workerStatus = if (Get-Command Get-AppWorkerStatus -ErrorAction SilentlyContinue) { Get-AppWorkerStatus } else { $null }
    if ($workerStatus -and $workerStatus.Status -eq "Completed") {
        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
            Write-ServisLog "Programi su vec kompletirani prema app_worker_status.json. Pozadinski radnik nije potreban." -Level "INFO" -Component "Worker"
        }
        return $null
    }

    $targetScript = $ScriptPath
    if (-not (Test-Path $targetScript)) {
        if ($PSCommandPath -and (Test-Path $PSCommandPath)) {
            $targetScript = $PSCommandPath
        } elseif (Test-Path "C:\setup\Install-ServisApps.ps1") {
            $targetScript = "C:\setup\Install-ServisApps.ps1"
        }
    }

    if (Test-Path $targetScript) {
        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
            Write-ServisLog "Pokrecem pozadinskog radnika za instalaciju programa: $targetScript -Worker `"Programs`"" -Level "STEP" -Component "Worker"
        }
        if (Get-Command Update-AppWorkerStatus -ErrorAction SilentlyContinue) {
            Update-AppWorkerStatus -Status "Running" -CurrentApp "Inicijalizacija paralelnog radnika..." -Progress 1
        }
        try {
            $Global:AppWorkerProc = Start-Process powershell.exe `
                -ArgumentList "-NoProfile -ExecutionPolicy Bypass -File `"$targetScript`" -Worker `"Programs`"" `
                -WindowStyle Hidden `
                -PassThru
            if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                Write-ServisLog "Pozadinski radnik uspesno pokrenut sa PID: $($Global:AppWorkerProc.Id)" -Level "SUCCESS" -Component "Worker"
            }
            return $Global:AppWorkerProc
        } catch {
            if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                Write-ServisLog "Greska pri pokretanju pozadinskog radnika: $($_.Exception.Message)" -Level "ERROR" -Component "Worker"
            }
        }
    } else {
        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
            Write-ServisLog "Skripta za radnika nije pronadjena na $targetScript. Programi ce biti instalirani u Fazi 4." -Level "WARN" -Component "Worker"
        }
    }
    return $null
}

function Optimize-WUPerformance {
    <#
    .SYNOPSIS
        Uklanja ogranicenja brzine za Windows Update i Delivery Optimization (WUDO),
        omogucava lokalni LAN Peering (1 Gbps razmena zakrpa medju servisnim racunarima),
        postavlja profil napajanja na High Performance i stiti Servicing Stack od Defender inspekcije.
    #>
    if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
        Write-ServisLog "Optimizujem Windows Update i Delivery Optimization propusni opseg (LAN Peering & No Limit)..." -Level "STEP" -Component "WU-Opt"
    }

    try {
        # 1. Delivery Optimization (WUDO) polise - maksimalni protok i lokalno deljenje (LAN Peering)
        $doPath = "HKLM:\SOFTWARE\Policies\Microsoft\Windows\DeliveryOptimization"
        if (-not (Test-Path $doPath)) { New-Item -Path $doPath -Force -ErrorAction SilentlyContinue | Out-Null }
        Set-ItemProperty -Path $doPath -Name "DODownloadMode" -Value 1 -Type DWord -Force -ErrorAction SilentlyContinue
        Set-ItemProperty -Path $doPath -Name "DOMaxForegroundDownloadBandwidth" -Value 0 -Type DWord -Force -ErrorAction SilentlyContinue
        Set-ItemProperty -Path $doPath -Name "DOMaxBackgroundDownloadBandwidth" -Value 0 -Type DWord -Force -ErrorAction SilentlyContinue
        Set-ItemProperty -Path $doPath -Name "DOAbsoluteMaxDownloadBandwidth" -Value 0 -Type DWord -Force -ErrorAction SilentlyContinue
        Set-ItemProperty -Path $doPath -Name "DOMinDiskSizeAllowedToPeer" -Value 1 -Type DWord -Force -ErrorAction SilentlyContinue
        Set-ItemProperty -Path $doPath -Name "DOMinRAMAllowedToPeer" -Value 1 -Type DWord -Force -ErrorAction SilentlyContinue

        # 2. Windows Update polise - omoguci MU servis i spreci automatski reboot van nase kontrole
        $wuPol = "HKLM:\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate"
        if (-not (Test-Path $wuPol)) { New-Item -Path $wuPol -Force -ErrorAction SilentlyContinue | Out-Null }
        $wuAU = "HKLM:\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU"
        if (-not (Test-Path $wuAU)) { New-Item -Path $wuAU -Force -ErrorAction SilentlyContinue | Out-Null }
        Set-ItemProperty -Path $wuPol -Name "AllowMUUpdateService" -Value 1 -Type DWord -Force -ErrorAction SilentlyContinue
        Set-ItemProperty -Path $wuAU -Name "NoAutoRebootWithLoggedOnUsers" -Value 1 -Type DWord -Force -ErrorAction SilentlyContinue

        # 3. Servisi za preuzimanje postavljeni na Automatic i pokrenuti
        @("wuauserv", "UsoSvc", "bits", "DoSvc") | ForEach-Object {
            Set-Service -Name $_ -StartupType Automatic -ErrorAction SilentlyContinue
            Start-Service -Name $_ -ErrorAction SilentlyContinue
        }

        # 4. Postavljanje energetskog profila na High Performance radi maksimalnog takta CPU-a pri dekompresiji zakrpa
        try {
            powercfg -setactive 8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c
        } catch {}

        # 5. Privremeni izuzeci Defender-a za Servicing Stack foldere i procese
        try {
            Add-MpPreference -ExclusionPath "C:\Windows\SoftwareDistribution" -ErrorAction SilentlyContinue
            Add-MpPreference -ExclusionProcess "TiWorker.exe" -ErrorAction SilentlyContinue
            Add-MpPreference -ExclusionProcess "TrustedInstaller.exe" -ErrorAction SilentlyContinue
        } catch {}

        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
            Write-ServisLog "Windows Update i Delivery Optimization mrezne i procesorske optimizacije uspesno primenjene." -Level "SUCCESS" -Component "WU-Opt"
        }
    } catch {
        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
            Write-ServisLog "Upozorenje pri podesavanju WU optimizacija: $($_.Exception.Message)" -Level "WARN" -Component "WU-Opt"
        }
    }
}

if (-not (Get-Command Get-ServisNasSettings -ErrorAction SilentlyContinue)) {
function Get-ServisNasSettings {
    <#
    .SYNOPSIS
        Ucitava parametre [ServisNas] sekcije iz Wifi-Servis.ini konfiguracije.
    #>
    $iniCandidates = @(
        "C:\setup\Wifi-Servis.ini",
        (Join-Path $PSScriptRoot "Wifi-Servis.ini"),
        (Join-Path (Split-Path $PSScriptRoot -Parent) "Wifi-Servis.ini"),
        (Join-Path (Split-Path $PSScriptRoot -Parent) "Skripte\Wifi-Servis.ini")
    )
    if ($Global:SourceRoot) {
        $iniCandidates += (Join-Path $Global:SourceRoot "Wifi-Servis.ini")
        $iniCandidates += (Join-Path $Global:SourceRoot "Skripte\Wifi-Servis.ini")
    }
    foreach ($d in (Get-PSDrive -PSProvider FileSystem -ErrorAction SilentlyContinue)) {
        $iniCandidates += (Join-Path "$($d.Root)" "Wifi-Servis.ini")
        $iniCandidates += (Join-Path "$($d.Root)" "Skripte\Wifi-Servis.ini")
    }

    $iniPath = $iniCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $iniPath) {
        return [PSCustomObject]@{
            IniPath           = $null
            Enabled           = "1"
            NasHost           = "192.168.170.254"
            ShareName         = "Servis"
            ProgramiShare     = "Programi"
            UpdatesSubfolder  = "Updates\Win11"
            Username          = "admin"
            Password          = ""
            PreInstallDrivers = "1"
            DriversSubDir     = "Drivers\Harvested\Models"
            HarvestMode       = "All"
        }
    }

    $iniContent = Get-Content $iniPath -Raw -ErrorAction SilentlyContinue
    if (-not $iniContent -or $iniContent -notmatch '(?mi)^\s*\[ServisNas\]([\s\S]*?)(?:^\s*\[|\Z)') {
        return [PSCustomObject]@{
            IniPath           = $iniPath
            Enabled           = "1"
            NasHost           = "192.168.170.254"
            ShareName         = "Servis"
            ProgramiShare     = "Programi"
            UpdatesSubfolder  = "Updates\Win11"
            Username          = "admin"
            Password          = ""
            PreInstallDrivers = "1"
            DriversSubDir     = "Drivers\Harvested\Models"
            HarvestMode       = "All"
        }
    }

    $nasSec = $matches[1]
    $enabled = if ($nasSec -match '(?mi)^[ \t]*Enabled[ \t]*=[ \t]*([^\r\n#;]+)') { $matches[1].Trim() } else { "1" }
    $nasHost = if ($nasSec -match '(?mi)^[ \t]*NasHost[ \t]*=[ \t]*([^\r\n#;]+)') { $matches[1].Trim() } else { "192.168.170.254" }
    $shareName = if ($nasSec -match '(?mi)^[ \t]*ShareName[ \t]*=[ \t]*([^\r\n#;]+)') { $matches[1].Trim() } else { "Servis" }
    $programiShare = if ($nasSec -match '(?mi)^[ \t]*ProgramiShare[ \t]*=[ \t]*([^\r\n#;]+)') { $matches[1].Trim() } else { "Programi" }
    $updatesSub = if ($nasSec -match '(?mi)^[ \t]*UpdatesSubfolder[ \t]*=[ \t]*([^\r\n#;]+)') { $matches[1].Trim() } else { "Updates\Win11" }
    $user = if ($nasSec -match '(?mi)^[ \t]*Username[ \t]*=[ \t]*([^\r\n#;]+)') { $matches[1].Trim() } else { "admin" }
    if ([string]::IsNullOrWhiteSpace($user)) { $user = "admin" }
    $pass = if ($nasSec -match '(?mi)^[ \t]*Password[ \t]*=[ \t]*([^\r\n#;]+)') { $matches[1].Trim() } else { "" }
    if ([string]::IsNullOrWhiteSpace($pass) -or $pass -eq "promeni_lozinku" -or $pass -eq "CHANGE_ME") { $pass = "" }
    $preInstall = if ($nasSec -match '(?mi)^[ \t]*PreInstallDrivers[ \t]*=[ \t]*([^\r\n#;]+)') { $matches[1].Trim() } else { "1" }
    $driversSub = if ($nasSec -match '(?mi)^[ \t]*DriversSubDir[ \t]*=[ \t]*([^\r\n#;]+)') { $matches[1].Trim() } else { "Drivers\Harvested\Models" }
    $harvestMode = if ($nasSec -match '(?mi)^[ \t]*HarvestMode[ \t]*=[ \t]*([^\r\n#;]+)') { $matches[1].Trim() } else { "All" }

    return [PSCustomObject]@{
        IniPath           = $iniPath
        Enabled           = $enabled
        NasHost           = $nasHost
        ShareName         = $shareName
        ProgramiShare     = $programiShare
        UpdatesSubfolder  = $updatesSub
        Username          = $user
        Password          = $pass
        PreInstallDrivers = $preInstall
        DriversSubDir     = $driversSub
        HarvestMode       = $harvestMode
    }
}
}

if (-not (Get-Command Test-NasHostOnline -ErrorAction SilentlyContinue)) {
function Test-NasHostOnline {
    param([string]$NasHost)
    if ([string]::IsNullOrWhiteSpace($NasHost)) { return $false }
    $nasOnline = $false
    try {
        $tcp = New-Object System.Net.Sockets.TcpClient
        $ar = $tcp.BeginConnect($NasHost, 445, $null, $null)
        $ok = $ar.AsyncWaitHandle.WaitOne(1500, $false)
        if ($ok -and $tcp.Connected) {
            $nasOnline = $true
            $tcp.EndConnect($ar)
        }
        $tcp.Close()
    } catch { $nasOnline = $false }

    if (-not $nasOnline) {
        try {
            $nasOnline = Test-Connection -ComputerName $NasHost -Count 1 -Quiet -ErrorAction SilentlyContinue
        } catch { $nasOnline = $false }
    }
    return $nasOnline
}
}

function Get-CleanComputerModel {
    $rawModel = "GenericPC"
    try {
        $cs = Get-CimInstance Win32_ComputerSystem -ErrorAction SilentlyContinue
        if ($cs -and $cs.Model) { $rawModel = $cs.Model }
    } catch {}

    $cleanModel = ($rawModel -replace '[^\w\.\-\s]', '' -replace '\s+', '_').Trim()
    if ([string]::IsNullOrWhiteSpace($cleanModel) -or $cleanModel -match '^To_Be_Filled') {
        $cleanModel = "Unknown_Model"
    }
    return $cleanModel
}

function Install-DriverTarArchive {
    <#
    .SYNOPSIS
        Raspakuje single-stream .tar arhivu drajvera i instalira .inf pakete preko pnputil.exe.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$TarPath,

        [Parameter(Mandatory = $true)]
        [string]$CleanModel,

        [Parameter(Mandatory = $false)]
        [string]$ExtractRoot = "",

        [Parameter(Mandatory = $false)]
        [string]$SourceLabel = "NAS"
    )

    if (-not (Test-Path $TarPath -ErrorAction SilentlyContinue)) {
        throw "Arhiva drajvera ne postoji na putanji: $TarPath"
    }

    $ownsWorkDir = [string]::IsNullOrWhiteSpace($ExtractRoot)
    $workDir = if ($ownsWorkDir) {
        Join-Path $env:TEMP ("WIS_Drivers_" + [Guid]::NewGuid().ToString().Substring(0,8))
    } else {
        $ExtractRoot
    }

    try {
        if (-not (Test-Path $workDir)) {
            New-Item -Path $workDir -ItemType Directory -Force | Out-Null
        }

        & tar.exe -xf "$TarPath" -C "$workDir" 2>$null
        $tarExit = $LASTEXITCODE
        $extractedInfs = @(Get-ChildItem -Path $workDir -Filter "*.inf" -Recurse -File -ErrorAction SilentlyContinue)
        if ($tarExit -ne 0 -or $extractedInfs.Count -eq 0) {
            throw "Raspakivanje $CleanModel.tar nije proizvelo validne .inf fajlove (ExitCode=$tarExit, INF=$($extractedInfs.Count))."
        }

        & pnputil.exe /add-driver "$workDir\*.inf" /subdirs /install >$null 2>&1
        Start-Sleep -Seconds 3

        $unresolvedDevs = @()
        try {
            if (Get-Command Get-PnpDevice -ErrorAction SilentlyContinue) {
                $unresolvedDevs = @(Get-PnpDevice -PresentOnly -ErrorAction SilentlyContinue | Where-Object {
                    $_.ConfigManagerErrorCode -ne 0 -and
                    $_.ConfigManagerErrorCode -notin @(22, 45) -and
                    $_.Class -notin @('Printer', 'PrintQueue', 'SoftwareDevice') -and
                    $_.InstanceId -notmatch '^(ROOT|SWD)\\'
                })
            }
        } catch {}

        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
            Write-ServisLog "Pre-instalacija drajvera za model $CleanModel ($SourceLabel) iz single-stream arhive uspesno izvrsena ($($extractedInfs.Count) .inf). Preostalo neprepoznatih uredjaja: $($unresolvedDevs.Count)." -Level "SUCCESS" -Component "NAS"
        }
        if (Get-Command Show-ServisDashboard -ErrorAction SilentlyContinue) {
            Show-ServisDashboard -LiveStatus "[OK] Drajveri za $CleanModel uspesno instalirani ($SourceLabel / $CleanModel.tar)"
        }
        return $true
    } finally {
        if ($ownsWorkDir -and $workDir -and (Test-Path $workDir)) {
            Remove-Item -Path $workDir -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}

function Invoke-ModelDriversPreInstall {
    <#
    .SYNOPSIS
        Pre-instalacija kompletnih hardverskih drajvera za model racunara sa lokalnog NAS servera ili ServiceHub HTTP kesa.
    .DESCRIPTION
        Cita konfiguraciju [ServisNas] iz Wifi-Servis.ini. Ako je PreInstallDrivers=1 i NAS dostupan,
        proverava postojanje namenskog paketa drajvera na \\<NasHost>\<Share>\Drivers\Harvested\Archives\<Model>.tar
        ili u Drivers\Harvested\Models\<Model>\.
        Ukoliko SMB nije dostupan ili paket nije pronadjen preko SMB-a, koristi ServiceHub HTTP fallback
        (/api/drivers/model-pack/status i /api/drivers/model-pack/download).
    #>
    [CmdletBinding()]
    param(
        [string]$ExistingUncShare = ""
    )

    try {
        $nas = Get-ServisNasSettings
        if (-not $nas -or $nas.Enabled -ne "1" -or $nas.PreInstallDrivers -ne "1") {
            return
        }

        $cleanModel = Get-CleanComputerModel
        $installedAny = $false
        $uncShare = $null
        $ownsSmbSession = $false

        if (-not [string]::IsNullOrWhiteSpace($ExistingUncShare) -and (Test-Path $ExistingUncShare -ErrorAction SilentlyContinue)) {
            $uncShare = $ExistingUncShare
            $ownsSmbSession = $false
        } elseif (-not [string]::IsNullOrWhiteSpace($nas.NasHost) -and (Test-NasHostOnline -NasHost $nas.NasHost)) {
            $smbCtx = Connect-ServisNasUpdatesShare -Subfolder "Drivers"
            if ($smbCtx -and $smbCtx.Connected -and -not [string]::IsNullOrWhiteSpace($smbCtx.UncShare)) {
                $uncShare = $smbCtx.UncShare
                $ownsSmbSession = $true
            } elseif (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                Write-ServisLog "Invoke-ModelDriversPreInstall: Nije moguce mapirati SMB share na NAS serveru $($nas.NasHost). Provericu ServiceHub HTTP fallback..." -Level "WARN" -Component "NAS"
            }
        } else {
            if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                Write-ServisLog "Invoke-ModelDriversPreInstall: NAS SMB ($($nas.NasHost)) nije dostupan. Provericu ServiceHub HTTP fallback..." -Level "INFO" -Component "NAS"
            }
        }

        if ($uncShare) {
            try {
                try {
                    $tarArchive = Join-Path $uncShare "Drivers\Harvested\Archives\$cleanModel.tar"

                    if (Test-Path $tarArchive -ErrorAction SilentlyContinue) {
                        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                            Write-ServisLog "Pronadjena optimizovana arhiva drajvera za model $cleanModel na NAS-u ($cleanModel.tar). Preuzimam single-stream paket..." -Level "STEP" -Component "NAS"
                        }
                        if (Get-Command Show-ServisDashboard -ErrorAction SilentlyContinue) {
                            Show-ServisDashboard -LiveStatus "Preuzimam optimizovani single-stream paket drajvera ($cleanModel.tar)..." -SubLogs @(
                                "Model: $cleanModel",
                                "Izvor: $tarArchive",
                                "Metoda: Windows tar.exe ekstrakcija (NVMe ubrzanje)"
                            )
                        }

                        $localTemp = Join-Path $env:TEMP ("WIS_Drivers_" + [Guid]::NewGuid().ToString().Substring(0,8))
                        try {
                            if (-not (Test-Path $localTemp)) {
                                New-Item -Path $localTemp -ItemType Directory -Force | Out-Null
                            }
                            $localTar = Join-Path $localTemp "$cleanModel.tar"
                            Copy-Item -Path $tarArchive -Destination $localTar -Force -ErrorAction Stop
                            if (Install-DriverTarArchive -TarPath $localTar -CleanModel $cleanModel -ExtractRoot $localTemp -SourceLabel "NAS SMB") {
                                $installedAny = $true
                            }
                        } catch {
                            if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                                Write-ServisLog "UPOZORENJE: Preuzimanje ili raspakivanje drajvera iz arhive $cleanModel.tar nije uspelo ($($_.Exception.Message)). Prelazim na stablo foldera..." -Level "WARN" -Component "NAS"
                            }
                        } finally {
                            if ($localTemp -and (Test-Path $localTemp)) {
                                Remove-Item -Path $localTemp -Recurse -Force -ErrorAction SilentlyContinue
                            }
                        }
                    }

                    if (-not $installedAny) {
                        $modelRelCandidates = @("Drivers\Harvested\Models\$cleanModel")
                        if (-not [string]::IsNullOrWhiteSpace($nas.DriversSubDir) -and $nas.DriversSubDir.Trim() -ne "Drivers") {
                            $modelRelCandidates += (Join-Path $nas.DriversSubDir.Trim() $cleanModel)
                        } else {
                            $modelRelCandidates += "Drivers\$cleanModel"
                        }

                        $modelDir = $null
                        $infFiles = @()
                        foreach ($relCand in ($modelRelCandidates | Select-Object -Unique)) {
                            $candDir = Join-Path $uncShare $relCand
                            if (Test-Path $candDir -ErrorAction SilentlyContinue) {
                                $candInfs = @(Get-ChildItem -Path $candDir -Filter "*.inf" -Recurse -File -ErrorAction SilentlyContinue)
                                if ($candInfs.Count -gt 0) {
                                    $modelDir = $candDir
                                    $infFiles = $candInfs
                                    break
                                }
                                if (-not $modelDir) { $modelDir = $candDir }
                            }
                        }
                        if (-not $modelDir) {
                            $modelDir = Join-Path $uncShare "Drivers\Harvested\Models\$cleanModel"
                        }

                        if (Test-Path $modelDir -ErrorAction SilentlyContinue) {
                            if ($infFiles.Count -gt 0) {
                                if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                                    Write-ServisLog "Pronadjen paket drajvera za model $cleanModel na NAS-u ($($infFiles.Count) .inf fajlova). Pokrecem pnputil..." -Level "STEP" -Component "NAS"
                                }
                                if (Get-Command Show-ServisDashboard -ErrorAction SilentlyContinue) {
                                    Show-ServisDashboard -LiveStatus "Pre-instaliram $($infFiles.Count) drajvera sa NAS-a za model $cleanModel..." -SubLogs @(
                                        "Model: $cleanModel",
                                        "Izvor: $modelDir",
                                        "Ukupno paketa: $($infFiles.Count) .inf"
                                    )
                                }

                                & pnputil.exe /add-driver "$modelDir\*.inf" /subdirs /install >$null 2>&1

                                Start-Sleep -Seconds 3

                                $unresolvedDevs = @()
                                try {
                                    if (Get-Command Get-PnpDevice -ErrorAction SilentlyContinue) {
                                        $unresolvedDevs = @(Get-PnpDevice -PresentOnly -ErrorAction SilentlyContinue | Where-Object {
                                            $_.ConfigManagerErrorCode -ne 0 -and
                                            $_.ConfigManagerErrorCode -notin @(22, 45) -and
                                            $_.Class -notin @('Printer', 'PrintQueue', 'SoftwareDevice') -and
                                            $_.InstanceId -notmatch '^(ROOT|SWD)\\'
                                        })
                                    }
                                } catch {}

                                if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                                    Write-ServisLog "Pre-instalacija drajvera za model $cleanModel sa NAS-a uspesno izvrsena. Preostalo neprepoznatih uredjaja: $($unresolvedDevs.Count)." -Level "SUCCESS" -Component "NAS"
                                }
                                if (Get-Command Show-ServisDashboard -ErrorAction SilentlyContinue) {
                                    Show-ServisDashboard -LiveStatus "[OK] Drajveri za $cleanModel uspesno instalirani sa NAS-a"
                                }
                                $installedAny = $true
                            } else {
                                if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                                    Write-ServisLog "Direktorijum za model $cleanModel na NAS-u ($modelDir) ne sadrzi .inf fajlove." -Level "DEBUG" -Component "NAS"
                                }
                            }
                        } else {
                            if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                                Write-ServisLog "Lokalni paket drajvera za model $cleanModel ne postoji na NAS-u ($modelDir)." -Level "INFO" -Component "NAS"
                            }
                        }
                    }
                } catch {
                    if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                        Write-ServisLog "UPOZORENJE: Pre-instalacija drajvera sa NAS servera nije uspela ($($_.Exception.Message)). Nastavljam sa standardnim tokom..." -Level "WARN" -Component "NAS"
                    }
                    if (Get-Command Show-ServisDashboard -ErrorAction SilentlyContinue) {
                        Show-ServisDashboard -LiveStatus "Upozorenje: Pre-instalacija drajvera sa NAS-a nije uspela, nastavljam..."
                    }
                }
            } finally {
                if ($ownsSmbSession -and $uncShare) {
                    Disconnect-ServisNasUpdatesShare -UncShare $uncShare
                }
            }
        }

        # ServiceHub HTTP fallback za <Model>.tar kada SMB nije dostupan ili nema paketa na SMB-u
        if (-not $installedAny) {
            $hubUrl = Get-WisUpdateHubEndpoint
            if ($hubUrl) {
                $escapedModel = [System.Uri]::EscapeDataString($cleanModel)
                $statusUri = "$hubUrl/api/drivers/model-pack/status?model=$escapedModel"
                try {
                    $packStatus = Invoke-RestMethod -Uri $statusUri -Method Get -TimeoutSec 10 -ErrorAction Stop
                    if ($packStatus -and $packStatus.available) {
                        $dlUri = "$hubUrl/api/drivers/model-pack/download?model=$escapedModel"
                        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                            Write-ServisLog "ServiceHub poseduje <Model>.tar paket drajvera za $cleanModel ($($packStatus.size_mb) MB). Preuzimam preko HTTP-a..." -Level "STEP" -Component "NAS"
                        }
                        if (Get-Command Show-ServisDashboard -ErrorAction SilentlyContinue) {
                            Show-ServisDashboard -LiveStatus "Preuzimam paket drajvera ($cleanModel.tar) sa ServiceHub-a..."
                        }

                        $localHttpTemp = Join-Path $env:TEMP ("WIS_HubDrivers_" + [Guid]::NewGuid().ToString().Substring(0,8))
                        try {
                            New-Item -Path $localHttpTemp -ItemType Directory -Force -ErrorAction SilentlyContinue | Out-Null
                            $localHttpTar = Join-Path $localHttpTemp "$cleanModel.tar"
                            if (Receive-UpdateFileFromUrl -Url $dlUri -DestinationPath $localHttpTar -TimeoutMs 600000 -MinBytes 512) {
                                if (Install-DriverTarArchive -TarPath $localHttpTar -CleanModel $cleanModel -ExtractRoot $localHttpTemp -SourceLabel "ServiceHub HTTP") {
                                    $installedAny = $true
                                }
                            } else {
                                if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                                    Write-ServisLog "Neuspelo preuzimanje paketa $cleanModel.tar sa ServiceHub-a." -Level "WARN" -Component "NAS"
                                }
                            }
                        } finally {
                            if ($localHttpTemp -and (Test-Path $localHttpTemp)) {
                                Remove-Item -Path $localHttpTemp -Recurse -Force -ErrorAction SilentlyContinue
                            }
                        }
                    }
                } catch {
                    if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                        Write-ServisLog "ServiceHub HTTP model-pack provera za $cleanModel nije uspela ($($_.Exception.Message))." -Level "INFO" -Component "NAS"
                    }
                }
            }
        }
    } catch {
        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
            Write-ServisLog "Upozorenje tokom NAS pre-instalacije drajvera: $($_.Exception.Message)" -Level "WARN" -Component "NAS"
        }
    }
}

function Get-WisUpdateHubEndpoint {
    <#
    .SYNOPSIS
        Pronalazi aktivnu HTTP adresu Centralnog ServisHub servera.
    #>
    [CmdletBinding()]
    param()

    if ($Global:ServisHubUrl -and -not [string]::IsNullOrWhiteSpace($Global:ServisHubUrl)) {
        return $Global:ServisHubUrl.TrimEnd('/')
    }

    if (Get-Command Get-ServisHubEndpoint -ErrorAction SilentlyContinue) {
        try {
            $ep = Get-ServisHubEndpoint
            if ($ep -and -not [string]::IsNullOrWhiteSpace($ep)) {
                return $ep.TrimEnd('/')
            }
        } catch {}
    }

    $iniCandidates = @(
        "C:\setup\Wifi-Servis.ini",
        "C:\Windows\Setup\Scripts\Wifi-Servis.ini",
        (Join-Path $PSScriptRoot "Wifi-Servis.ini"),
        (Join-Path (Split-Path $PSScriptRoot -Parent) "Wifi-Servis.ini"),
        (Join-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) "Wifi-Servis.ini")
    )
    foreach ($iniPath in $iniCandidates) {
        if ($iniPath -and (Test-Path $iniPath -ErrorAction SilentlyContinue)) {
            try {
                $content = Get-Content -Path $iniPath -Raw -ErrorAction SilentlyContinue
                if ($content -match '(?mi)^\s*\[ServisHub\]([\s\S]*?)(?:^\s*\[|\Z)') {
                    $sec = $matches[1]
                    if ($sec -match '(?mi)^\s*(?:HubUrl|Url|Endpoint)\s*=\s*([^\r\n#;]+)') {
                        $rawUrl = $matches[1].Trim().TrimEnd('/')
                        if ($rawUrl -match '^https?://') {
                            return $rawUrl
                        }
                    }
                    $hostName = if ($sec -match '(?mi)^\s*ServerHost\s*=\s*([^\r\n#;]+)') { $matches[1].Trim() } else { "" }
                    $port = if ($sec -match '(?mi)^\s*ServerPort\s*=\s*([0-9]+)') { [int]$matches[1].Trim() } else { 8080 }
                    $firstHost = ($hostName -split ',')[0].Trim()
                    if ($firstHost -and $firstHost -ne "auto") {
                        return "http://${firstHost}:${port}"
                    }
                }
            } catch {}
        }
    }

    try {
        $nas = Get-ServisNasSettings
        if ($nas -and -not [string]::IsNullOrWhiteSpace($nas.NasHost)) {
            return "http://$($nas.NasHost):8080"
        }
    } catch {}

    return $null
}

function Get-UpdateKbArticleId {
    <#
    .SYNOPSIS
        Izvlaci normalizovani KB identifikator (npr. KB5053598) iz WUA update objekta.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $false)]
        $UpdateItem
    )

    if (-not $UpdateItem) { return $null }

    try {
        if ($UpdateItem.KBArticleIDs -and $UpdateItem.KBArticleIDs.Count -gt 0) {
            foreach ($kbRaw in $UpdateItem.KBArticleIDs) {
                $clean = ([string]$kbRaw).Trim().ToUpper()
                if ($clean -match '^KB\d{5,8}$') {
                    return $clean
                }
                if ($clean -match '^\d{5,8}$') {
                    return "KB$clean"
                }
            }
        }
    } catch {}

    try {
        $titleStr = [string]$UpdateItem.Title
        $m = [regex]::Match($titleStr, '(?i)\b(KB\d{5,8})\b')
        if ($m.Success) {
            return $m.Groups[1].Value.ToUpper()
        }
    } catch {}

    return $null
}

function Install-LocalUpdatePackageFile {
    <#
    .SYNOPSIS
        Tiho instalira lokalni .msu (wusa.exe) ili .cab (dism.exe) paket zakrpe.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$PackagePath,

        [Parameter(Mandatory = $false)]
        [string]$DisplayName = ""
    )

    $label = if ([string]::IsNullOrWhiteSpace($DisplayName)) { Split-Path $PackagePath -Leaf } else { $DisplayName }
    if (-not (Test-Path $PackagePath -ErrorAction SilentlyContinue)) {
        return @{ Success = $false; ExitCode = -1; RebootRequired = $false }
    }

    $ext = ([System.IO.Path]::GetExtension($PackagePath)).ToLower()
    $proc = $null
    try {
        if ($ext -eq ".msu") {
            $proc = Start-Process -FilePath "wusa.exe" -ArgumentList "`"$PackagePath`" /quiet /norestart" -Wait -PassThru -ErrorAction Stop
        } elseif ($ext -eq ".cab") {
            $proc = Start-Process -FilePath "dism.exe" -ArgumentList "/Online /Add-Package /PackagePath:`"$PackagePath`" /Quiet /NoRestart" -Wait -PassThru -ErrorAction Stop
        } else {
            return @{ Success = $false; ExitCode = -2; RebootRequired = $false }
        }
    } catch {
        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
            Write-ServisLog "Install-LocalUpdatePackageFile: Greska pri pokretanju instalacije paketa $label ($($_.Exception.Message))." -Level "WARN" -Component "NAS"
        }
        return @{ Success = $false; ExitCode = -3; RebootRequired = $false }
    }

    $exitCode = if ($proc) { [int]$proc.ExitCode } else { -1 }
    # 0 = Uspeh, 3010 = Uspeh (potreban restart), 2359302 = Vec instaliran (0x00240006)
    if ($exitCode -eq 0 -or $exitCode -eq 3010 -or $exitCode -eq 2359302) {
        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
            Write-ServisLog "Paket $label uspesno primenjen (Exit kod: $exitCode)." -Level "SUCCESS" -Component "NAS"
        }
        return @{
            Success        = $true
            ExitCode       = $exitCode
            RebootRequired = ($exitCode -eq 3010)
        }
    }

    if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
        Write-ServisLog "Paket $label zavrsio sa exit kodom: $exitCode." -Level "WARN" -Component "NAS"
    }
    return @{ Success = $false; ExitCode = $exitCode; RebootRequired = $false }
}

function Receive-UpdateFileFromUrl {
    <#
    .SYNOPSIS
        Preuzima fajl preko HTTP/HTTPS pomocu stream bafera uz proveru minimalne velicine.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$Url,

        [Parameter(Mandatory = $true)]
        [string]$DestinationPath,

        [int]$TimeoutMs = 180000,
        [long]$MinBytes = 65536
    )

    try {
        [System.Net.ServicePointManager]::SecurityProtocol = [System.Net.ServicePointManager]::SecurityProtocol -bor [System.Net.SecurityProtocolType]::Tls12
    } catch {}

    $tmpPath = "$DestinationPath.tmp"
    $resp = $null
    $inStream = $null
    $outStream = $null
    try {
        $parentDir = Split-Path $DestinationPath -Parent
        if ($parentDir -and -not (Test-Path $parentDir)) {
            New-Item -Path $parentDir -ItemType Directory -Force -ErrorAction SilentlyContinue | Out-Null
        }

        $req = [System.Net.HttpWebRequest]::Create($Url)
        $req.Method = "GET"
        $req.Timeout = $TimeoutMs
        $req.ReadWriteTimeout = $TimeoutMs
        $req.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36"

        $resp = $req.GetResponse()
        $inStream = $resp.GetResponseStream()
        $outStream = [System.IO.File]::Create($tmpPath)
        $buffer = New-Object byte[] 65536
        while (($read = $inStream.Read($buffer, 0, $buffer.Length)) -gt 0) {
            $outStream.Write($buffer, 0, $read)
        }
        $outStream.Close()
        $outStream = $null
        $inStream.Close()
        $inStream = $null
        $resp.Close()
        $resp = $null

        if (-not (Test-Path $tmpPath)) { return $false }
        $fi = Get-Item -Path $tmpPath -ErrorAction SilentlyContinue
        if (-not $fi -or $fi.Length -lt $MinBytes) {
            Remove-Item -Path $tmpPath -Force -ErrorAction SilentlyContinue
            return $false
        }

        if (Test-Path $DestinationPath) {
            Remove-Item -Path $DestinationPath -Force -ErrorAction SilentlyContinue
        }
        Move-Item -Path $tmpPath -Destination $DestinationPath -Force -ErrorAction Stop
        return $true
    } catch {
        return $false
    } finally {
        if ($outStream) { try { $outStream.Dispose() } catch {} }
        if ($inStream) { try { $inStream.Dispose() } catch {} }
        if ($resp) { try { $resp.Close() } catch {} }
        if (Test-Path $tmpPath) { Remove-Item -Path $tmpPath -Force -ErrorAction SilentlyContinue }
    }
}

function Get-CurrentWindowsBuildNumber {
    <#
    .SYNOPSIS
        Vraca trenutni Windows OS Build broj (npr. '26100', '22631', '22621', '22000').
    #>
    [CmdletBinding()]
    param()

    try {
        $reg = Get-ItemProperty -Path "HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion" -ErrorAction SilentlyContinue
        if ($reg -and $reg.CurrentBuildNumber) {
            return ([string]$reg.CurrentBuildNumber).Trim()
        }
        if ($reg -and $reg.CurrentBuild) {
            return ([string]$reg.CurrentBuild).Trim()
        }
    } catch {}

    try {
        $os = Get-CimInstance -ClassName Win32_OperatingSystem -ErrorAction SilentlyContinue
        if ($os -and $os.BuildNumber) {
            return ([string]$os.BuildNumber).Trim()
        }
    } catch {}

    return ""
}

function Test-UpdatePackageBuildCompatible {
    <#
    .SYNOPSIS
        Proverava da li je .msu/.cab paket zakrpe kompatibilan sa trenutnim Windows 11 OSBuild brojem.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $false)]
        [string]$CurrentBuild = "",

        [Parameter(Mandatory = $false)]
        [string]$PackageName = "",

        [Parameter(Mandatory = $false)]
        [string]$PackageOsBuild = ""
    )

    if ([string]::IsNullOrWhiteSpace($CurrentBuild)) { return $true }
    $normCur = $CurrentBuild.Trim()
    $versionMap = @{
        "21H2" = "22000"
        "22H2" = "22621"
        "23H2" = "22631"
        "24H2" = "26100"
        "25H2" = "26200"
    }

    if (-not [string]::IsNullOrWhiteSpace($PackageOsBuild)) {
        $normPkg = $PackageOsBuild.Trim().ToUpper()
        if ($versionMap.ContainsKey($normPkg)) { $normPkg = $versionMap[$normPkg] }
        return ($normPkg -eq $normCur)
    }

    if ([string]::IsNullOrWhiteSpace($PackageName)) { return $true }
    $upper = $PackageName.ToUpper()
    if ($upper -match '(?<![A-Z0-9])(21H2|22H2|23H2|24H2|25H2)(?![A-Z0-9])') {
        return ($versionMap[$matches[1]] -eq $normCur)
    }
    if ($upper -match '(?<!KB)(?<!\d)(26200|26100|22631|22621|22000)(?!\d)') {
        return ($matches[1] -eq $normCur)
    }

    return $true
}

function Invoke-ServisHubBinaryUpload {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$UploadUrl,

        [Parameter(Mandatory = $true)]
        [string]$FilePath,

        [Parameter(Mandatory = $false)]
        [hashtable]$Headers = @{},

        [Parameter(Mandatory = $false)]
        [int]$TimeoutMs = 300000,

        [Parameter(Mandatory = $false)]
        [string]$LogLabel = ""
    )

    if (-not (Test-Path $FilePath -ErrorAction SilentlyContinue)) { return $false }
    $fi = Get-Item -Path $FilePath -ErrorAction SilentlyContinue
    if (-not $fi -or $fi.Length -le 0) { return $false }

    $inStream = $null
    $reqStream = $null
    $resp = $null
    try {
        $req = [System.Net.HttpWebRequest]::Create($UploadUrl)
        $req.Method = "POST"
        $req.ContentType = "application/octet-stream"
        $req.ContentLength = $fi.Length
        $req.AllowWriteStreamBuffering = $false
        $req.Timeout = $TimeoutMs
        $req.ReadWriteTimeout = $TimeoutMs
        if ($Headers) {
            foreach ($hk in $Headers.Keys) {
                if (-not [string]::IsNullOrWhiteSpace([string]$Headers[$hk])) {
                    $req.Headers.Add([string]$hk, [string]$Headers[$hk])
                }
            }
        }

        $inStream = [System.IO.File]::OpenRead($FilePath)
        $reqStream = $req.GetRequestStream()
        $buffer = New-Object byte[] 65536
        while (($read = $inStream.Read($buffer, 0, $buffer.Length)) -gt 0) {
            $reqStream.Write($buffer, 0, $read)
        }
        $reqStream.Close()
        $reqStream = $null
        $inStream.Close()
        $inStream = $null

        $resp = $req.GetResponse()
        $statusCode = [int]$resp.StatusCode
        $resp.Close()
        $resp = $null
        return ($statusCode -ge 200 -and $statusCode -lt 300)
    } catch {
        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
            $lbl = if ([string]::IsNullOrWhiteSpace($LogLabel)) { $fi.Name } else { $LogLabel }
            Write-ServisLog "Invoke-ServisHubBinaryUpload: Greska pri slanju $lbl na ServisHub ($($_.Exception.Message))." -Level "WARN" -Component "UpdateCache"
        }
        return $false
    } finally {
        if ($reqStream) { try { $reqStream.Dispose() } catch {} }
        if ($inStream) { try { $inStream.Dispose() } catch {} }
        if ($resp) { try { $resp.Close() } catch {} }
    }
}

function Send-UpdatePackageToServisHub {
    <#
    .SYNOPSIS
        Salje lokalni .msu ili .cab paket zakrpe na ServisHub HTTP API (/api/updates/upload).
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$HubUrl,

        [Parameter(Mandatory = $true)]
        [string]$FilePath,

        [Parameter(Mandatory = $false)]
        [string]$Filename = "",

        [Parameter(Mandatory = $false)]
        [string]$KbId = "",

        [Parameter(Mandatory = $false)]
        [string]$OsBuild = ""
    )

    if (-not (Test-Path $FilePath -ErrorAction SilentlyContinue)) { return $false }
    $fi = Get-Item -Path $FilePath -ErrorAction SilentlyContinue
    if (-not $fi -or $fi.Length -le 0) { return $false }

    $targetName = if ([string]::IsNullOrWhiteSpace($Filename)) { $fi.Name } else { $Filename }
    $effectiveBuild = if ([string]::IsNullOrWhiteSpace($OsBuild)) { Get-CurrentWindowsBuildNumber } else { $OsBuild.Trim() }

    $uploadUrl = "$($HubUrl.TrimEnd('/'))/api/updates/upload?filename=$([System.Uri]::EscapeDataString($targetName))"
    if (-not [string]::IsNullOrWhiteSpace($KbId)) {
        $uploadUrl = "${uploadUrl}&kb=$([System.Uri]::EscapeDataString($KbId))"
    }
    if (-not [string]::IsNullOrWhiteSpace($effectiveBuild)) {
        $uploadUrl = "${uploadUrl}&os_build=$([System.Uri]::EscapeDataString($effectiveBuild))"
    }

    $hdrs = @{ "X-Update-Filename" = $targetName }
    if (-not [string]::IsNullOrWhiteSpace($KbId)) { $hdrs["X-Update-KB"] = $KbId }
    if (-not [string]::IsNullOrWhiteSpace($effectiveBuild)) { $hdrs["X-OS-Build"] = $effectiveBuild }

    return (Invoke-ServisHubBinaryUpload -UploadUrl $uploadUrl -FilePath $FilePath -Headers $hdrs -TimeoutMs 300000 -LogLabel $targetName)
}

function Send-ModelDriverPackToServisHub {
    <#
    .SYNOPSIS
        Salje model-specificni <Model>.tar paket drajvera na ServisHub HTTP API (/api/drivers/model-pack/upload).
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$HubUrl,

        [Parameter(Mandatory = $true)]
        [string]$FilePath,

        [Parameter(Mandatory = $true)]
        [string]$CleanModel
    )

    if (-not (Test-Path $FilePath -ErrorAction SilentlyContinue)) { return $false }
    $fi = Get-Item -Path $FilePath -ErrorAction SilentlyContinue
    if (-not $fi -or $fi.Length -lt 512) { return $false }

    $targetName = "$CleanModel.tar"
    $uploadUrl = "$($HubUrl.TrimEnd('/'))/api/drivers/model-pack/upload?model=$([System.Uri]::EscapeDataString($CleanModel))&filename=$([System.Uri]::EscapeDataString($targetName))"
    $hdrs = @{
        "X-Model-Name"     = $CleanModel
        "X-Model-Filename" = $targetName
    }

    return (Invoke-ServisHubBinaryUpload -UploadUrl $uploadUrl -FilePath $FilePath -Headers $hdrs -TimeoutMs 600000 -LogLabel $targetName)
}

function Connect-ServisNasUpdatesShare {
    <#
    .SYNOPSIS
        Povezuje se na SMB NAS deljenje pokusavajuci redom ShareName, WIS i Servis.
    #>
    [CmdletBinding()]
    param(
        [switch]$CreateUpdatesDir,
        [string]$Subfolder = ""
    )

    $result = @{
        Connected  = $false
        UncShare   = $null
        UpdatesDir = $null
        NasHost    = $null
        Username   = "admin"
        Password   = ""
    }

    try {
        $nas = Get-ServisNasSettings
        if (-not $nas -or $nas.Enabled -ne "1" -or [string]::IsNullOrWhiteSpace($nas.NasHost)) {
            return $result
        }

        if (-not (Test-NasHostOnline -NasHost $nas.NasHost)) {
            return $result
        }

        # Osiguraj SMB Guest pristup i potpisivanje u registru (Windows 11 hardening bypass)
        try {
            $lanmanPath = "HKLM:\SYSTEM\CurrentControlSet\Services\LanmanWorkstation\Parameters"
            if (-not (Test-Path $lanmanPath)) {
                New-Item -Path $lanmanPath -Force -ErrorAction SilentlyContinue | Out-Null
            }
            Set-ItemProperty -Path $lanmanPath -Name "AllowInsecureGuestAuth" -Value 1 -Type DWord -Force -ErrorAction SilentlyContinue
            Set-ItemProperty -Path $lanmanPath -Name "RequireSecuritySignature" -Value 0 -Type DWord -Force -ErrorAction SilentlyContinue
        } catch {}

        $uName = if ($nas.Username) { $nas.Username } else { "admin" }
        $uPass = if ($nas.Password -and $nas.Password -ne "promeni_lozinku" -and $nas.Password -ne "CHANGE_ME") { $nas.Password } else { "" }
        $userArg = if ($uName -match "\\") { $uName } else { "$($nas.NasHost)\$uName" }

        $result.NasHost = $nas.NasHost
        $result.Username = $uName
        $result.Password = $uPass

        $shareCandidates = @($nas.ShareName, "WIS", "Servis") | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -Unique
        $subFolder = if (-not [string]::IsNullOrWhiteSpace($Subfolder)) {
            $Subfolder.Trim()
        } elseif ($nas.UpdatesSubfolder) {
            $nas.UpdatesSubfolder
        } else {
            "Updates\Win11"
        }

        $fallbackShare = $null
        $fallbackUpdatesDir = $null

        foreach ($shareName in $shareCandidates) {
            $candUnc = "\\$($nas.NasHost)\$shareName"
            $authOk = $false
            if (-not [string]::IsNullOrWhiteSpace($uPass)) {
                & net.exe use $candUnc $uPass "/user:$userArg" /persistent:no >$null 2>&1
                if ($LASTEXITCODE -eq 0) {
                    $authOk = $true
                } else {
                    & net.exe use $candUnc $uPass "/user:$uName" /persistent:no >$null 2>&1
                    if ($LASTEXITCODE -eq 0) { $authOk = $true }
                }
            }
            if (-not $authOk) {
                & net.exe use $candUnc '""' /user:guest /persistent:no >$null 2>&1
            }

            if (Test-Path $candUnc -ErrorAction SilentlyContinue) {
                $candUpdatesDir = Join-Path $candUnc $subFolder
                if (Test-Path $candUpdatesDir -ErrorAction SilentlyContinue) {
                    if ($fallbackShare -and $fallbackShare -ne $candUnc) {
                        $null = & cmd.exe /c "net.exe use `"$fallbackShare`" /delete /y >nul 2>&1"
                    }
                    $result.Connected = $true
                    $result.UncShare = $candUnc
                    $result.UpdatesDir = $candUpdatesDir
                    return $result
                }

                if (-not $fallbackShare) {
                    $fallbackShare = $candUnc
                    $fallbackUpdatesDir = $candUpdatesDir
                } else {
                    $null = & cmd.exe /c "net.exe use `"$candUnc`" /delete /y >nul 2>&1"
                }
            }
        }

        if ($fallbackShare) {
            if ($CreateUpdatesDir -and $fallbackUpdatesDir) {
                try {
                    New-Item -Path $fallbackUpdatesDir -ItemType Directory -Force -ErrorAction SilentlyContinue | Out-Null
                } catch {}
            }
            $result.Connected = $true
            $result.UncShare = $fallbackShare
            $result.UpdatesDir = $fallbackUpdatesDir
        }
    } catch {}

    return $result
}

function Disconnect-ServisNasUpdatesShare {
    <#
    .SYNOPSIS
        Bezbedno zatvara otvorenu SMB sesiju prema NAS serveru.
    #>
    [CmdletBinding()]
    param(
        [string]$UncShare = ""
    )

    try {
        if (-not [string]::IsNullOrWhiteSpace($UncShare)) {
            $null = & cmd.exe /c "net.exe use `"$UncShare`" /delete /y >nul 2>&1"
            if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                Write-ServisLog "Mrezna SMB sesija sa NAS serverom ($UncShare) je bezbedno zatvorena." -Level "DEBUG" -Component "NAS"
            }
        } else {
            $null = & cmd.exe /c "net.exe use * /delete /y >nul 2>&1"
        }
    } catch {}
}

function Invoke-NasUpdatePrePatch {
    <#
    .SYNOPSIS
        Pre-patch faza kumulativnih zakrpi i lokalnih drajvera sa lokalnog servisnog NAS servera.
    .DESCRIPTION
        Ukoliko je u Wifi-Servis.ini pod [ServisNas] omogucen NAS kes, povezuje se
        na SMB share (kandidati ShareName, WIS, Servis), skenira .msu i .cab pakete
        i tiho ih instalira pre COM API pretrage. Kada SMB nije dostupan ili nema
        paketa, koristi ServiceHub HTTP API fallback (/api/updates/status i /download).
        Takodje poziva pre-instalaciju drajvera modela i proveru mreznih drajvera sa NAS-a.
    #>
    try {
        $nas = Get-ServisNasSettings
        $wuMode = Get-WUConfigMode
        $currBuild = Get-CurrentWindowsBuildNumber
        $handledKbs = @{}
        $smbPackagesFound = 0

        if ($nas -and $nas.Enabled -eq "1" -and -not [string]::IsNullOrWhiteSpace($nas.NasHost) -and (Test-NasHostOnline -NasHost $nas.NasHost)) {
            if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                Write-ServisLog "Proveravam dostupnost servisnog NAS servera na $($nas.NasHost)..." -Level "INFO" -Component "NAS"
            }

            $smbCtx = Connect-ServisNasUpdatesShare
            if ($smbCtx.Connected) {
                $uncShare = $smbCtx.UncShare
                $targetUpdatesDir = $smbCtx.UpdatesDir
                $uName = $smbCtx.Username
                $uPass = $smbCtx.Password

                if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                    Write-ServisLog "NAS host $($nas.NasHost) je dostupan. Povezujem se na mrezni resurs $uncShare (Korisnik: $uName)..." -Level "INFO" -Component "NAS"
                }

                try {
                    # 1. Lokalni .msu i .cab paketi preko SMB-a
                    if ($wuMode -eq "DriversOnly") {
                        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                            Write-ServisLog "Rezim rada je DriversOnly. Preskacem instalaciju kumulativnih .msu/.cab zakrpi sa NAS servera jer su vec integrisane u sistem." -Level "INFO" -Component "NAS"
                        }
                    } else {
                        if ($targetUpdatesDir -and (Test-Path $targetUpdatesDir -ErrorAction SilentlyContinue)) {
                            $pkgList = @(Get-ChildItem -Path $targetUpdatesDir -File -ErrorAction SilentlyContinue | Where-Object {
                                ($_.Extension -eq ".msu" -or $_.Extension -eq ".cab") -and ($_.Name -notmatch '(?i)^wsusscan\.cab$')
                            } | Sort-Object Name)

                            if ($pkgList.Count -gt 0) {
                                $smbPackagesFound = $pkgList.Count
                                if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                                    Write-ServisLog "Pronadjeno $($pkgList.Count) .msu/.cab paketa na NAS serveru ($targetUpdatesDir). Pokrecem brzi pre-patch..." -Level "INFO" -Component "NAS"
                                }
                                if (Get-Command Show-ServisDashboard -ErrorAction SilentlyContinue) {
                                    Show-ServisDashboard -LiveStatus "Instaliram lokalne zakrpe sa NAS servera ($($pkgList.Count) paketa)..."
                                }

                                $idx = 0
                                foreach ($pkg in $pkgList) {
                                    $idx++
                                    if (-not (Test-UpdatePackageBuildCompatible -CurrentBuild $currBuild -PackageName $pkg.Name)) {
                                        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                                            Write-ServisLog "[$idx/$($pkgList.Count)] Paket $($pkg.Name) pripada drugom Windows OSBuild-u (sistem: $currBuild). Preskacem." -Level "INFO" -Component "NAS"
                                        }
                                        continue
                                    }
                                    $kbCheck = $null
                                    if ($pkg.Name -match '(?i)(KB\d{5,8})') {
                                        $kbCheck = $matches[1].ToUpper()
                                        $alreadyInstalled = Get-HotFix -Id $kbCheck -ErrorAction SilentlyContinue
                                        if ($alreadyInstalled) {
                                            $handledKbs[$kbCheck] = $true
                                            if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                                                Write-ServisLog "[$idx/$($pkgList.Count)] Zakrpa $kbCheck ($($pkg.Name)) je vec instalirana na sistemu. Preskacem." -Level "INFO" -Component "NAS"
                                            }
                                            continue
                                        }
                                    }

                                    if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                                        Write-ServisLog "[$idx/$($pkgList.Count)] Preuzimam i instaliram lokalni NAS pre-patch: $($pkg.Name)..." -Level "STEP" -Component "NAS"
                                    }
                                    if (Get-Command Show-ServisDashboard -ErrorAction SilentlyContinue) {
                                        Show-ServisDashboard -LiveStatus "[$idx/$($pkgList.Count)] NAS pre-patch: $($pkg.Name)..."
                                    }

                                    # Sekvencijalno kopiranje na lokalni NVMe SSD pre poziva wusa.exe/dism.exe
                                    $localPkgPath = Join-Path $env:TEMP $pkg.Name
                                    $targetExecPkg = $pkg.FullName
                                    try {
                                        Copy-Item -Path $pkg.FullName -Destination $localPkgPath -Force -ErrorAction Stop
                                        if (Test-Path $localPkgPath) {
                                            $targetExecPkg = $localPkgPath
                                        }
                                    } catch {}

                                    try {
                                        $instRes = Install-LocalUpdatePackageFile -PackagePath $targetExecPkg -DisplayName $pkg.Name
                                        if ($instRes.Success -and $kbCheck) {
                                            $handledKbs[$kbCheck] = $true
                                        }
                                    } finally {
                                        if ($localPkgPath -and (Test-Path $localPkgPath)) {
                                            Remove-Item -Path $localPkgPath -Force -ErrorAction SilentlyContinue
                                        }
                                    }
                                }
                            } else {
                                if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                                    Write-ServisLog "Nisu pronadjeni .msu/.cab paketi u $targetUpdatesDir na NAS serveru." -Level "INFO" -Component "NAS"
                                }
                            }
                        } else {
                            if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                                Write-ServisLog "Direktorijum sa azuriranjima $targetUpdatesDir nije dostupan na NAS deljenju." -Level "WARN" -Component "NAS"
                            }
                        }
                    }

                    # 2. Pre-instalacija kompletnih drajvera za model racunara sa NAS-a (uz deljenje SMB sesije)
                    Invoke-ModelDriversPreInstall -ExistingUncShare $uncShare

                    # 3. Provera i instalacija mreznih drajvera sa NAS skladista ako racunaru nedostaju mrezni adapteri
                    try {
                        $netAdapters = Get-NetAdapter -ErrorAction SilentlyContinue
                        if (-not $netAdapters -or $netAdapters.Count -eq 0) {
                            # Osiguraj SMB sesiju ako je u medjuvremenu zatvorena
                            if (-not (Test-Path $uncShare)) {
                                $userArg = if ($uName -match "\\") { $uName } else { "$($nas.NasHost)\$uName" }
                                & net.exe use $uncShare $uPass "/user:$userArg" /persistent:no >$null 2>&1
                                if ($LASTEXITCODE -ne 0) {
                                    & net.exe use $uncShare $uPass "/user:$uName" /persistent:no >$null 2>&1
                                }
                            }

                            $harvestedNetDir = Join-Path $uncShare "Drivers\Harvested\Net"
                            if (Test-Path $harvestedNetDir) {
                                if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                                    Write-ServisLog "Detektovan nedostatak mreznih adaptera. Skeniram bazu drajvera na NAS-u ($harvestedNetDir)..." -Level "STEP" -Component "NAS"
                                }
                                if (Get-Command Show-ServisDashboard -ErrorAction SilentlyContinue) {
                                    Show-ServisDashboard -LiveStatus "Instaliram mrezne drajvere sa NAS harvested baze..."
                                }

                                $cleanModel = Get-CleanComputerModel
                                $installedHarvested = $false

                                if ($cleanModel -and $cleanModel -ne "Unknown_Model") {
                                    $modelHarvestDir = Join-Path $harvestedNetDir $cleanModel
                                    if (Test-Path $modelHarvestDir) {
                                        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                                            Write-ServisLog "Pronadjen namenski paket mreznih drajvera za model $cleanModel na NAS-u. Pokrecem pnputil..." -Level "INFO" -Component "NAS"
                                        }
                                        & pnputil.exe /add-driver "$modelHarvestDir\*.inf" /subdirs /install >$null 2>&1
                                        $installedHarvested = $true
                                    }
                                }

                                if (-not $installedHarvested) {
                                    $infFiles = Get-ChildItem -Path $harvestedNetDir -Filter "*.inf" -Recurse -File -ErrorAction SilentlyContinue
                                    if ($infFiles -and $infFiles.Count -gt 0) {
                                        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                                            Write-ServisLog "Skeniram i instaliram sve dostupne mrezne drajvere sa NAS-a ($($infFiles.Count) .inf fajlova)..." -Level "INFO" -Component "NAS"
                                        }
                                        & pnputil.exe /add-driver "$harvestedNetDir\*.inf" /subdirs /install >$null 2>&1
                                        $installedHarvested = $true
                                    }
                                }

                                if ($installedHarvested) {
                                    Start-Sleep -Seconds 4
                                    $postNet = Get-NetAdapter -ErrorAction SilentlyContinue
                                    if ($postNet -and $postNet.Count -gt 0) {
                                        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                                            Write-ServisLog "Mrezni drajveri uspesno instalirani sa NAS-a. Prepoznato $($postNet.Count) mrezna adaptera." -Level "SUCCESS" -Component "NAS"
                                        }
                                    }
                                }
                            }
                        }
                    } catch {
                        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                            Write-ServisLog "Upozorenje pri proveri harvested mreznih drajvera sa NAS-a: $($_.Exception.Message)" -Level "WARN" -Component "NAS"
                        }
                    }
                } finally {
                    Disconnect-ServisNasUpdatesShare -UncShare $uncShare
                }
            }
        } else {
            if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                Write-ServisLog "NAS SMB pre-patch je preskocen (iskljucen ili host nije dostupan preko SMB). Proveravam ServiceHub HTTP kes..." -Level "INFO" -Component "NAS"
            }
        }

        # 4. ServiceHub HTTP fallback kada SMB nije uspeo ili kada zelimo dopunu sa Hub-a
        if ($wuMode -ne "DriversOnly" -and $smbPackagesFound -eq 0) {
            $hubUrl = Get-WisUpdateHubEndpoint
            if ($hubUrl) {
                try {
                    $statusUri = if (-not [string]::IsNullOrWhiteSpace($currBuild)) {
                        "$hubUrl/api/updates/status?os_build=$([System.Uri]::EscapeDataString($currBuild))"
                    } else {
                        "$hubUrl/api/updates/status"
                    }
                    $statusResp = Invoke-RestMethod -Uri $statusUri -Method Get -TimeoutSec 10 -ErrorAction Stop
                    $hubPackages = if ($statusResp -and $statusResp.packages) { @($statusResp.packages) } else { @() }
                    $hubPackages = @($hubPackages | Where-Object {
                        $_.filename -and $_.filename -notmatch '(?i)^wsusscan\.cab$' -and $_.filename -match '(?i)\.(msu|cab)$'
                    } | Sort-Object filename)

                    if ($hubPackages.Count -gt 0) {
                        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                            Write-ServisLog "ServiceHub ($hubUrl) prijavio $($hubPackages.Count) kesiranih paketa zakrpi. Pokrecem HTTP pre-patch fallback..." -Level "INFO" -Component "NAS"
                        }
                        if (Get-Command Show-ServisDashboard -ErrorAction SilentlyContinue) {
                            Show-ServisDashboard -LiveStatus "Instaliram zakrpe sa ServiceHub HTTP kesa ($($hubPackages.Count) paketa)..."
                        }

                        $hubIdx = 0
                        foreach ($hp in $hubPackages) {
                            $hubIdx++
                            $fname = [string]$hp.filename
                            $hpBuild = if ($hp.os_build) { [string]$hp.os_build } else { "" }
                            if (-not (Test-UpdatePackageBuildCompatible -CurrentBuild $currBuild -PackageName $fname -PackageOsBuild $hpBuild)) {
                                if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                                    Write-ServisLog "[$hubIdx/$($hubPackages.Count)] ServiceHub zakrpa $fname ($hpBuild) nije za OSBuild $currBuild. Preskacem." -Level "INFO" -Component "NAS"
                                }
                                continue
                            }
                            $kbHub = if ($hp.kb) { ([string]$hp.kb).ToUpper() } elseif ($fname -match '(?i)(KB\d{5,8})') { $matches[1].ToUpper() } else { $null }

                            if ($kbHub -and $handledKbs.ContainsKey($kbHub)) { continue }
                            if ($kbHub) {
                                $hotfixCheck = Get-HotFix -Id $kbHub -ErrorAction SilentlyContinue
                                if ($hotfixCheck) {
                                    $handledKbs[$kbHub] = $true
                                    if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                                        Write-ServisLog "[$hubIdx/$($hubPackages.Count)] ServiceHub zakrpa $kbHub ($fname) je vec instalirana. Preskacem." -Level "INFO" -Component "NAS"
                                    }
                                    continue
                                }
                            }

                            $dlUrl = "$hubUrl/api/updates/download?filename=$([System.Uri]::EscapeDataString($fname))"
                            $localHubPkg = Join-Path $env:TEMP $fname
                            try {
                                if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                                    Write-ServisLog "[$hubIdx/$($hubPackages.Count)] Preuzimam zakrpu $fname sa ServiceHub-a..." -Level "STEP" -Component "NAS"
                                }
                                if (Receive-UpdateFileFromUrl -Url $dlUrl -DestinationPath $localHubPkg -TimeoutMs 300000) {
                                    $instRes = Install-LocalUpdatePackageFile -PackagePath $localHubPkg -DisplayName $fname
                                    if ($instRes.Success -and $kbHub) {
                                        $handledKbs[$kbHub] = $true
                                    }
                                } else {
                                    if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                                        Write-ServisLog "[$hubIdx/$($hubPackages.Count)] Neuspelo preuzimanje paketa $fname sa ServiceHub-a." -Level "WARN" -Component "NAS"
                                    }
                                }
                            } finally {
                                if (Test-Path $localHubPkg) {
                                    Remove-Item -Path $localHubPkg -Force -ErrorAction SilentlyContinue
                                }
                            }
                        }
                    }
                } catch {
                    if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                        Write-ServisLog "ServiceHub HTTP kes zakrpi nije dostupan ($($_.Exception.Message)). Nastavljam sa standardnim Windows Update-om." -Level "INFO" -Component "NAS"
                    }
                }
            }
        }
    } catch {
        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
            Write-ServisLog "Greska tokom NAS pre-patch ciklusa: $($_.Exception.Message)" -Level "WARN" -Component "NAS"
        }
    }
}

function Save-UpdatePackageFromMsCatalog {
    <#
    .SYNOPSIS
        Preuzima samostalni Windows 11/10 x64 .msu/.cab paket zakrpe iz Microsoft Update Catalog-a po KB broju.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$KbId,

        [Parameter(Mandatory = $true)]
        [string]$DestinationDir,

        [Parameter(Mandatory = $false)]
        [string]$PreferredTitle = ""
    )

    $normKb = $KbId.Trim().ToUpper()
    if ($normKb -notmatch '^KB\d{5,8}$') {
        if ($normKb -match '^\d{5,8}$') {
            $normKb = "KB$normKb"
        } else {
            return $null
        }
    }

    try {
        [System.Net.ServicePointManager]::SecurityProtocol = [System.Net.ServicePointManager]::SecurityProtocol -bor [System.Net.SecurityProtocolType]::Tls12
    } catch {}

    $userAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36"
    $searchUrl = "https://www.catalog.update.microsoft.com/Search.aspx?q=$([System.Uri]::EscapeDataString($normKb))"
    $searchHtml = ""
    try {
        $req = [System.Net.HttpWebRequest]::Create($searchUrl)
        $req.Method = "GET"
        $req.Timeout = 25000
        $req.UserAgent = $userAgent
        $resp = $req.GetResponse()
        $stream = $resp.GetResponseStream()
        $reader = New-Object System.IO.StreamReader($stream, [System.Text.Encoding]::UTF8)
        $searchHtml = $reader.ReadToEnd()
        $reader.Close()
        $stream.Close()
        $resp.Close()
    } catch {
        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
            Write-ServisLog "Save-UpdatePackageFromMsCatalog: Greska pretrage kataloga za ${normKb}: $($_.Exception.Message)" -Level "WARN" -Component "UpdateCache"
        }
        return $null
    }

    $regexOpts = [System.Text.RegularExpressions.RegexOptions]::Singleline -bor [System.Text.RegularExpressions.RegexOptions]::IgnoreCase
    $rowMatches = [regex]::Matches($searchHtml, '<tr\s+id="([a-f0-9\-]+)_R\d+"[^>]*>(.*?)</tr>', $regexOpts)
    if ($rowMatches.Count -eq 0) {
        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
            Write-ServisLog "Save-UpdatePackageFromMsCatalog: Nema rezultata u Microsoft Katalogu za $normKb." -Level "INFO" -Component "UpdateCache"
        }
        return $null
    }

    $preferredRelease = ""
    if ($PreferredTitle -match '(?i)\b(2[1-9]H[12])\b') {
        $preferredRelease = $matches[1].ToLower()
    }

    $candidates = New-Object System.Collections.Generic.List[PSCustomObject]
    foreach ($m in $rowMatches) {
        $guid = $m.Groups[1].Value
        $rowHtml = $m.Groups[2].Value
        $tdMatches = [regex]::Matches($rowHtml, '<td[^>]*>(.*?)</td>', $regexOpts)
        if ($tdMatches.Count -lt 5) { continue }

        $rawTitle = $tdMatches[1].Groups[1].Value
        $cleanTitle = ([regex]::Replace($rawTitle, '<[^>]+>', ' ')).Trim()
        $rawProducts = $tdMatches[2].Groups[1].Value
        $cleanProducts = ([regex]::Replace($rawProducts, '<[^>]+>', ' ')).Trim()
        $combText = ($cleanTitle + " " + $cleanProducts).ToLower()

        if ($combText -match '(arm64|\barm\b|itanium|ia64|\bx86\b|32-bit|32bit)') { continue }
        if ($combText -match 'windows server' -and $combText -notmatch 'windows 11' -and $combText -notmatch 'windows 10') { continue }

        $isWin11 = ($combText -match 'windows 11')
        $isWin10 = ($combText -match 'windows 10')
        if (-not $isWin11 -and -not $isWin10) { continue }

        $score = 0
        if ($isWin11) { $score += 100 }
        if ($combText -match '\bx64\b|x64-based') { $score += 20 }
        if ($preferredRelease -and $combText.Contains($preferredRelease)) { $score += 50 }

        $candidates.Add([PSCustomObject]@{
            UpdateId = $guid
            Title    = $cleanTitle
            Score    = $score
        })
    }

    if ($candidates.Count -eq 0) {
        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
            Write-ServisLog "Save-UpdatePackageFromMsCatalog: Nijedan kandidat za $normKb ne odgovara Windows 11/10 x64 platformi." -Level "INFO" -Component "UpdateCache"
        }
        return $null
    }

    $bestMatch = $candidates | Sort-Object -Property Score -Descending | Select-Object -First 1
    $updateId = $bestMatch.UpdateId

    $dialogUrl = "https://www.catalog.update.microsoft.com/DownloadDialog.aspx"
    $dialogHtml = ""
    try {
        $dlgReq = [System.Net.HttpWebRequest]::Create($dialogUrl)
        $dlgReq.Method = "POST"
        $dlgReq.ContentType = "application/x-www-form-urlencoded"
        $dlgReq.UserAgent = $userAgent
        $dlgReq.Timeout = 25000

        $payloadJson = '[{"size":0,"languages":"","uidInfo":"' + $updateId + '","updateID":"' + $updateId + '"}]'
        $postParam = "updateIDs=" + [System.Uri]::EscapeDataString($payloadJson)
        $byteData = [System.Text.Encoding]::UTF8.GetBytes($postParam)
        $dlgReq.ContentLength = $byteData.Length
        $reqStream = $dlgReq.GetRequestStream()
        $reqStream.Write($byteData, 0, $byteData.Length)
        $reqStream.Close()

        $dlgResp = $dlgReq.GetResponse()
        $dStream = $dlgResp.GetResponseStream()
        $dReader = New-Object System.IO.StreamReader($dStream, [System.Text.Encoding]::UTF8)
        $dialogHtml = $dReader.ReadToEnd()
        $dReader.Close()
        $dStream.Close()
        $dlgResp.Close()
    } catch {
        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
            Write-ServisLog "Save-UpdatePackageFromMsCatalog: Neuspeh dohvatanja download linka za ${normKb} ($($_.Exception.Message))." -Level "WARN" -Component "UpdateCache"
        }
        return $null
    }

    $allUrls = New-Object System.Collections.Generic.List[string]
    $urlMatches = [regex]::Matches($dialogHtml, 'downloadInformation\[\d+\]\.files\[\d+\]\.url\s*=\s*[''"]([^''"]+)[''"]')
    foreach ($um in $urlMatches) {
        $uStr = $um.Groups[1].Value.Trim()
        if ($uStr -match '(?i)^https?://.*\.(msu|cab)(\?|$)' -and -not $allUrls.Contains($uStr)) {
            $allUrls.Add($uStr)
        }
    }
    if ($allUrls.Count -eq 0) {
        $rawMatches = [regex]::Matches($dialogHtml, 'https?://[^\s''"<>]+\.(?:msu|cab)', [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
        foreach ($rm in $rawMatches) {
            $uStr = $rm.Groups[0].Value.Trim()
            if (-not $allUrls.Contains($uStr)) {
                $allUrls.Add($uStr)
            }
        }
    }

    if ($allUrls.Count -eq 0) {
        return $null
    }

    # Prioritet imaju .msu paketi (narocito oni sa KB brojem u URL-u), pa zatim .cab
    $kbLower = $normKb.ToLower()
    $orderedUrls = @($allUrls | Sort-Object -Property @(
        @{ Expression = { if ($_ -match '(?i)\.msu(\?|$)') { 0 } else { 1 } } },
        @{ Expression = { if ($_.ToLower().Contains($kbLower)) { 0 } else { 1 } } },
        @{ Expression = { if ($_.ToLower().Contains("x64")) { 0 } else { 1 } } }
    ))

    foreach ($dlUrl in $orderedUrls) {
        $ext = if ($dlUrl -match '(?i)\.cab(\?|$)') { ".cab" } else { ".msu" }
        $destFileName = "Windows11.0-$normKb-x64$ext"
        $tempDlPath = Join-Path $env:TEMP "$destFileName.tmp"
        $destFilePath = Join-Path $DestinationDir $destFileName

        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
            Write-ServisLog "Save-UpdatePackageFromMsCatalog: Preuzimam $normKb ($ext) za NAS kes..." -Level "INFO" -Component "UpdateCache"
        }

        if (Receive-UpdateFileFromUrl -Url $dlUrl -DestinationPath $tempDlPath -TimeoutMs 600000 -MinBytes 65536) {
            try {
                if (-not (Test-Path $DestinationDir -ErrorAction SilentlyContinue)) {
                    New-Item -Path $DestinationDir -ItemType Directory -Force -ErrorAction SilentlyContinue | Out-Null
                }
                if (Test-Path $destFilePath) {
                    Remove-Item -Path $destFilePath -Force -ErrorAction SilentlyContinue
                }
                Copy-Item -Path $tempDlPath -Destination $destFilePath -Force -ErrorAction Stop
                if (Test-Path $destFilePath) {
                    if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                        Write-ServisLog "Save-UpdatePackageFromMsCatalog: Paket $destFileName uspesno sacuvan u $DestinationDir." -Level "SUCCESS" -Component "UpdateCache"
                    }
                    return $destFilePath
                }
            } catch {
                if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                    Write-ServisLog "Save-UpdatePackageFromMsCatalog: Greska pri cuvanju $destFileName ($($_.Exception.Message))." -Level "WARN" -Component "UpdateCache"
                }
            } finally {
                if (Test-Path $tempDlPath) {
                    Remove-Item -Path $tempDlPath -Force -ErrorAction SilentlyContinue
                }
            }
        }
    }

    return $null
}

function Export-ServisWindowsUpdates {
    <#
    .SYNOPSIS
        Dvosmerno arhivira preuzete/instalirane Windows Update zakrpe na NAS/ServiceHub kes.
    .DESCRIPTION
        Za ne-drajverske zakrpe (uz iskljucenje dnevnih Defender/MSRT paketa) proverava da li
        vec postoje na NAS skladistu (SMB ili ServiceHub HTTP). Nedostajuce KB pakete
        prvo bere iz lokalnog WUA kesa (C:\Windows\SoftwareDistribution\Download), zatim
        sa direktnih BundledUpdates DownloadUrl adresa, a za UUP kumulativne zakrpe bez
        standalone fajla koristi Microsoft Update Catalog (lokalno ili preko /api/updates/cache-kb).
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $false)]
        $Updates = @(),

        [switch]$Force
    )

    $flatUpdates = New-Object System.Collections.Generic.List[object]
    if ($Updates -is [System.Collections.IEnumerable] -and -not ($Updates -is [string])) {
        foreach ($entry in $Updates) {
            if ($null -eq $entry) { continue }
            $hasTitle = $false
            try { if ($entry.Title) { $hasTitle = $true } } catch {}
            if (-not $hasTitle -and ($entry -is [System.Collections.IEnumerable]) -and -not ($entry -is [string])) {
                foreach ($sub in $entry) {
                    if ($null -ne $sub) { $flatUpdates.Add($sub) }
                }
            } else {
                $flatUpdates.Add($entry)
            }
        }
    } elseif ($null -ne $Updates) {
        $flatUpdates.Add($Updates)
    }

    if ($flatUpdates.Count -eq 0) {
        return
    }

    $skipKbs = @("KB2267602", "KB890830", "KB4052623")
    $candidateList = New-Object System.Collections.Generic.List[PSCustomObject]
    $seenReqKbs = @{}

    foreach ($u in $flatUpdates) {
        if (-not $u) { continue }
        if (Test-IsDriverUpdate -UpdateItem $u) { continue }

        $kbId = Get-UpdateKbArticleId -UpdateItem $u
        if (-not $kbId) { continue }
        if ($kbId -in $skipKbs) { continue }
        if ($seenReqKbs.ContainsKey($kbId)) { continue }

        $seenReqKbs[$kbId] = $true
        $candidateList.Add([PSCustomObject]@{
            KbId   = $kbId
            Update = $u
            Title  = [string]$u.Title
        })
    }

    if ($candidateList.Count -eq 0) {
        return
    }

    $currBuild = Get-CurrentWindowsBuildNumber
    $smbCtx = @{ Connected = $false; UncShare = $null; UpdatesDir = $null }
    try {
        $smbCtx = Connect-ServisNasUpdatesShare -CreateUpdatesDir
        $hubUrl = Get-WisUpdateHubEndpoint

        if (-not $smbCtx.Connected -and -not $hubUrl) {
            if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                Write-ServisLog "Export-ServisWindowsUpdates: Ni NAS SMB ni ServiceHub nisu dostupni. Preskacem kesiranje zakrpi." -Level "INFO" -Component "UpdateCache"
            }
            return
        }

        $targetUpdatesDir = $smbCtx.UpdatesDir
        $cachedKbs = @{}

        if (-not $Force) {
            if ($smbCtx.Connected -and $targetUpdatesDir -and (Test-Path $targetUpdatesDir -ErrorAction SilentlyContinue)) {
                $existingFiles = Get-ChildItem -Path $targetUpdatesDir -File -ErrorAction SilentlyContinue | Where-Object {
                    $_.Extension -in @('.msu', '.cab')
                }
                foreach ($ef in $existingFiles) {
                    if ($ef.Name -match '(?i)(KB\d{5,8})') {
                        $cachedKbs[$matches[1].ToUpper()] = $true
                    }
                }
            }

            if ($hubUrl) {
                try {
                    $statusUri = if (-not [string]::IsNullOrWhiteSpace($currBuild)) {
                        "$hubUrl/api/updates/status?os_build=$([System.Uri]::EscapeDataString($currBuild))"
                    } else {
                        "$hubUrl/api/updates/status"
                    }
                    $hubStatus = Invoke-RestMethod -Uri $statusUri -Method Get -TimeoutSec 10 -ErrorAction Stop
                    if ($hubStatus -and $hubStatus.kb_list) {
                        foreach ($hk in $hubStatus.kb_list) {
                            $hNorm = ([string]$hk).Trim().ToUpper()
                            if ($hNorm) { $cachedKbs[$hNorm] = $true }
                        }
                    }
                    if ($hubStatus -and $hubStatus.packages) {
                        foreach ($hp in $hubStatus.packages) {
                            if ($hp.kb) {
                                $cachedKbs[([string]$hp.kb).Trim().ToUpper()] = $true
                            } elseif ($hp.filename -match '(?i)(KB\d{5,8})') {
                                $cachedKbs[$matches[1].ToUpper()] = $true
                            }
                        }
                    }
                } catch {}
            }
        }

        $missingItems = @($candidateList | Where-Object { $Force -or -not $cachedKbs.ContainsKey($_.KbId) })
        if ($missingItems.Count -eq 0) {
            if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                Write-ServisLog "Export-ServisWindowsUpdates: Sve obradjene zakrpe ($($candidateList.Count)) vec postoje u NAS kesu. Nema potrebe za ponovnim prenosom." -Level "INFO" -Component "UpdateCache"
            }
            return
        }

        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
            Write-ServisLog "Export-ServisWindowsUpdates: Pokrecem arhiviranje $($missingItems.Count) novih Windows Update zakrpi na NAS..." -Level "STEP" -Component "UpdateCache"
        }

        $sdDownloadRoot = "C:\Windows\SoftwareDistribution\Download"
        $sdFiles = @()
        if (Test-Path $sdDownloadRoot -ErrorAction SilentlyContinue) {
            $sdFiles = @(Get-ChildItem -Path $sdDownloadRoot -Recurse -File -ErrorAction SilentlyContinue | Where-Object {
                ($_.Extension -eq ".msu" -or $_.Extension -eq ".cab") -and $_.Length -gt 65536 -and ($_.Name -notmatch '(?i)^wsusscan\.cab$')
            })
        }

        foreach ($item in $missingItems) {
            $kbId = $item.KbId
            $kbNum = $kbId -replace '(?i)^KB', ''
            $uObj = $item.Update
            $archivedThisKb = $false

            # Prikupi URL-ove i imena iz BundledUpdates -> DownloadContents
            $directUrls = New-Object System.Collections.Generic.List[string]
            $expectedNames = New-Object System.Collections.Generic.List[string]
            try {
                if ($uObj.DownloadContents) {
                    foreach ($dc in $uObj.DownloadContents) {
                        if ($dc.DownloadUrl) {
                            $dUrl = [string]$dc.DownloadUrl
                            if ($dUrl -match '(?i)\.(msu|cab)(\?|$)' -and $dUrl -notmatch '(?i)wsusscan\.cab') {
                                if (-not $directUrls.Contains($dUrl)) { $directUrls.Add($dUrl) }
                                $leaf = ([string]($dUrl -split '\?')[0] -split '/')[-1].ToLower()
                                if ($leaf -and -not $expectedNames.Contains($leaf)) { $expectedNames.Add($leaf) }
                            }
                        }
                    }
                }
                if ($uObj.BundledUpdates) {
                    foreach ($bu in $uObj.BundledUpdates) {
                        if ($bu.DownloadContents) {
                            foreach ($dc in $bu.DownloadContents) {
                                if ($dc.DownloadUrl) {
                                    $dUrl = [string]$dc.DownloadUrl
                                    if ($dUrl -match '(?i)\.(msu|cab)(\?|$)' -and $dUrl -notmatch '(?i)wsusscan\.cab') {
                                        if (-not $directUrls.Contains($dUrl)) { $directUrls.Add($dUrl) }
                                        $leaf = ([string]($dUrl -split '\?')[0] -split '/')[-1].ToLower()
                                        if ($leaf -and -not $expectedNames.Contains($leaf)) { $expectedNames.Add($leaf) }
                                    }
                                }
                            }
                        }
                    }
                }
            } catch {}

            # 1) Lokalni WUA cache u C:\Windows\SoftwareDistribution\Download
            $matchedLocalFiles = @($sdFiles | Where-Object {
                $fnLow = $_.Name.ToLower()
                ($fnLow -match "(?i)$kbId") -or
                ($fnLow -match "(?i)windows[^/\\]*$kbNum") -or
                ($expectedNames.Contains($fnLow)) -or
                ($_.Directory.FullName -match "(?i)$kbId")
            } | Sort-Object -Property @(
                @{ Expression = { if ($_.Extension -eq ".msu") { 0 } else { 1 } } },
                @{ Expression = { -$_.Length } }
            ))

            if ($matchedLocalFiles.Count -gt 0) {
                $localSrc = $matchedLocalFiles[0]
                $cleanExt = $localSrc.Extension.ToLower()
                $destName = if ($localSrc.Name -match "(?i)$kbId") {
                    ($localSrc.Name -replace '[^a-zA-Z0-9._-]', '_')
                } else {
                    "Windows11.0-$kbId-x64$cleanExt"
                }

                if ($smbCtx.Connected -and $targetUpdatesDir) {
                    try {
                        if (-not (Test-Path $targetUpdatesDir -ErrorAction SilentlyContinue)) {
                            New-Item -Path $targetUpdatesDir -ItemType Directory -Force -ErrorAction SilentlyContinue | Out-Null
                        }
                        $smbDest = Join-Path $targetUpdatesDir $destName
                        Copy-Item -Path $localSrc.FullName -Destination $smbDest -Force -ErrorAction Stop
                        $archivedThisKb = $true
                        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                            Write-ServisLog "Export-ServisWindowsUpdates: Zakrpa $kbId ($destName) sacuvana na NAS iz lokalnog WUA kesa." -Level "SUCCESS" -Component "UpdateCache"
                        }
                    } catch {
                        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                            Write-ServisLog "Export-ServisWindowsUpdates: Neuspelo SMB kopiranje $destName ($($_.Exception.Message))." -Level "WARN" -Component "UpdateCache"
                        }
                    }
                }

                if (-not $archivedThisKb -and $hubUrl) {
                    if (Send-UpdatePackageToServisHub -HubUrl $hubUrl -FilePath $localSrc.FullName -Filename $destName -KbId $kbId -OsBuild $currBuild) {
                        $archivedThisKb = $true
                        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                            Write-ServisLog "Export-ServisWindowsUpdates: Zakrpa $kbId ($destName) uploadovana na ServiceHub iz lokalnog WUA kesa." -Level "SUCCESS" -Component "UpdateCache"
                        }
                    }
                }
            }

            # 2) Direktni DownloadContents.DownloadUrl (.msu / .cab)
            if (-not $archivedThisKb -and $directUrls.Count -gt 0) {
                $orderedDirect = @($directUrls | Sort-Object -Property @(
                    @{ Expression = { if ($_ -match '(?i)\.msu(\?|$)') { 0 } else { 1 } } }
                ))

                foreach ($dUrl in $orderedDirect) {
                    $ext = if ($dUrl -match '(?i)\.cab(\?|$)') { ".cab" } else { ".msu" }
                    $destName = "Windows11.0-$kbId-x64$ext"
                    $tempPkgPath = Join-Path $env:TEMP $destName

                    if (Receive-UpdateFileFromUrl -Url $dUrl -DestinationPath $tempPkgPath -TimeoutMs 600000 -MinBytes 65536) {
                        try {
                            if ($smbCtx.Connected -and $targetUpdatesDir) {
                                if (-not (Test-Path $targetUpdatesDir -ErrorAction SilentlyContinue)) {
                                    New-Item -Path $targetUpdatesDir -ItemType Directory -Force -ErrorAction SilentlyContinue | Out-Null
                                }
                                $smbDest = Join-Path $targetUpdatesDir $destName
                                Copy-Item -Path $tempPkgPath -Destination $smbDest -Force -ErrorAction Stop
                                $archivedThisKb = $true
                                if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                                    Write-ServisLog "Export-ServisWindowsUpdates: Zakrpa $kbId ($destName) preuzeta sa direktnog CDN linka i sacuvana na NAS." -Level "SUCCESS" -Component "UpdateCache"
                                }
                            } elseif ($hubUrl) {
                                if (Send-UpdatePackageToServisHub -HubUrl $hubUrl -FilePath $tempPkgPath -Filename $destName -KbId $kbId -OsBuild $currBuild) {
                                    $archivedThisKb = $true
                                    if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                                        Write-ServisLog "Export-ServisWindowsUpdates: Zakrpa $kbId ($destName) preuzeta sa CDN linka i uploadovana na ServiceHub." -Level "SUCCESS" -Component "UpdateCache"
                                    }
                                }
                            }
                        } catch {} finally {
                            if (Test-Path $tempPkgPath) {
                                Remove-Item -Path $tempPkgPath -Force -ErrorAction SilentlyContinue
                            }
                        }
                    }
                    if ($archivedThisKb) { break }
                }
            }

            # 3) Fallback za UUP kumulativne zakrpe bez standalone paketa -> Microsoft Update Catalog
            if (-not $archivedThisKb) {
                if ($smbCtx.Connected -and $targetUpdatesDir) {
                    $savedCatPath = Save-UpdatePackageFromMsCatalog -KbId $kbId -DestinationDir $targetUpdatesDir -PreferredTitle $item.Title
                    if ($savedCatPath) {
                        $archivedThisKb = $true
                    }
                }

                if (-not $archivedThisKb -and $hubUrl) {
                    try {
                        $bodyJson = (@{ kb = $kbId; title = $item.Title; os_build = $currBuild } | ConvertTo-Json -Compress)
                        $cacheResp = Invoke-RestMethod -Uri "$hubUrl/api/updates/cache-kb" -Method Post -Body $bodyJson -ContentType "application/json; charset=utf-8" -TimeoutSec 240 -ErrorAction Stop
                        if ($cacheResp -and ($cacheResp.status -eq "cached" -or $cacheResp.success)) {
                            $archivedThisKb = $true
                            if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                                Write-ServisLog "Export-ServisWindowsUpdates: ServiceHub uspesno kesirao $kbId iz Microsoft Catalog-a." -Level "SUCCESS" -Component "UpdateCache"
                            }
                        }
                    } catch {
                        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                            Write-ServisLog "Export-ServisWindowsUpdates: ServiceHub cache-kb za $kbId nije uspeo ($($_.Exception.Message)). Pokusavam direktno iz Kataloga..." -Level "INFO" -Component "UpdateCache"
                        }
                    }

                    if (-not $archivedThisKb) {
                        $localCatDir = Join-Path $env:TEMP "WisUpdateExport"
                        $localCatPath = Save-UpdatePackageFromMsCatalog -KbId $kbId -DestinationDir $localCatDir -PreferredTitle $item.Title
                        if ($localCatPath) {
                            try {
                                $catLeaf = Split-Path $localCatPath -Leaf
                                if (Send-UpdatePackageToServisHub -HubUrl $hubUrl -FilePath $localCatPath -Filename $catLeaf -KbId $kbId -OsBuild $currBuild) {
                                    $archivedThisKb = $true
                                    if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                                        Write-ServisLog "Export-ServisWindowsUpdates: Zakrpa $kbId ($catLeaf) preuzeta iz Microsoft Catalog-a i uploadovana na ServiceHub." -Level "SUCCESS" -Component "UpdateCache"
                                    }
                                }
                            } finally {
                                if (Test-Path $localCatPath) {
                                    Remove-Item -Path $localCatPath -Force -ErrorAction SilentlyContinue
                                }
                            }
                        }
                    }
                }
            }

            if (-not $archivedThisKb -and (Get-Command Write-ServisLog -ErrorAction SilentlyContinue)) {
                Write-ServisLog "Export-ServisWindowsUpdates: Za zakrpu $kbId ($($item.Title)) trenutno nije pronadjen samostalni .msu/.cab paket." -Level "INFO" -Component "UpdateCache"
            }
        }
    } catch {
        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
            Write-ServisLog "Export-ServisWindowsUpdates: Upozorenje tokom arhiviranja zakrpi na NAS: $($_.Exception.Message)" -Level "WARN" -Component "UpdateCache"
        }
    } finally {
        if ($smbCtx.Connected) {
            Disconnect-ServisNasUpdatesShare -UncShare $smbCtx.UncShare
        }
    }
}

function Export-ServisHardwareDrivers {
    <#
    .SYNOPSIS
        Izvozi sve instalirane hardverske drajvere racunara i arhivira ih na centralni NAS po modelu.
    .DESCRIPTION
        Cita konfiguraciju iz [ServisNas] u Wifi-Servis.ini. Proverava postojanje i deduplikaciju
        drajvera na NAS-u (ako folder za model vec sadrzi >= 3 .inf fajla, preskace arhiviranje radi ustede vremena i protoka).
        Ukoliko folder ne postoji ili je prazan, ekstrahuje drajvere preko dism.exe ili pnputil.exe,
        kopira kompletne drajverske podfoldere u Drivers\Harvested\Models\<Model>, a mrezne drajvere (Class=Net)
        paralelno sinhronizuje i u Drivers\Harvested\Net\<Model> radi maksimalne kompatibilnosti sa ADR-0027.
    #>
    [CmdletBinding()]
    param(
        [string]$HarvestMode = "",
        [switch]$Force
    )
    $tempExportDir = $null
    $tarPath = $null
    $uncShare = $null
    $smbMapped = $false
    $destTarTmp = $null

    try {
        # 1. Citanje konfiguracije iz Wifi-Servis.ini i ServiceHub fallback opcije
        $nas = Get-ServisNasSettings
        if (-not $nas -or $nas.Enabled -ne "1") {
            return
        }

        $cleanModel = Get-CleanComputerModel
        $effectiveHarvestMode = if ($HarvestMode) { $HarvestMode } elseif ($nas.HarvestMode) { $nas.HarvestMode } else { "All" }
        $hubUrl = Get-WisUpdateHubEndpoint
        $smbReady = $false

        # 2. Provera mrezne dostupnosti NAS hosta i povezivanje na share (ShareName / Servis / WIS)
        if (-not [string]::IsNullOrWhiteSpace($nas.NasHost) -and (Test-NasHostOnline -NasHost $nas.NasHost)) {
            $smbCtx = Connect-ServisNasUpdatesShare -Subfolder "Drivers"
            if ($smbCtx -and $smbCtx.Connected -and -not [string]::IsNullOrWhiteSpace($smbCtx.UncShare)) {
                $uncShare = $smbCtx.UncShare
                $smbMapped = $true
                $smbReady = $true
            } elseif (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                Write-ServisLog "Export-ServisHardwareDrivers: Nije moguce mapirati NAS share na $($nas.NasHost). Proveravam ServiceHub HTTP upload..." -Level "WARN" -Component "DriverHarvest"
            }
        } elseif (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
            Write-ServisLog "Export-ServisHardwareDrivers: NAS server $($nas.NasHost) nije dostupan preko SMB. Proveravam ServiceHub HTTP upload..." -Level "INFO" -Component "DriverHarvest"
        }

        if (-not $smbReady -and (-not $hubUrl -or $effectiveHarvestMode -eq "NetOnly")) {
            if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                Write-ServisLog "Export-ServisHardwareDrivers: Ni NAS SMB ($($nas.NasHost)) ni ServiceHub HTTP model-pack upload nisu dostupni. Preskacem arhiviranje." -Level "INFO" -Component "DriverHarvest"
            }
            return
        }

        $nasModelDest = $null
        $nasArchiveTar = $null
        if ($smbReady) {
            $driversSub = if (-not [string]::IsNullOrWhiteSpace($nas.DriversSubDir) -and $nas.DriversSubDir.Trim() -ne "Drivers") {
                $nas.DriversSubDir.Trim()
            } else {
                "Drivers\Harvested\Models"
            }
            $nasModelDest = Join-Path $uncShare (Join-Path $driversSub $cleanModel)
            $nasArchiveTar = Join-Path $uncShare "Drivers\Harvested\Archives\$cleanModel.tar"
        }

        # Provera postojanja i deduplikacije
        if (-not $Force) {
            if ($smbReady) {
                try {
                    if (Test-Path $nasModelDest -ErrorAction SilentlyContinue) {
                        $existingInfs = @(Get-ChildItem -Path $nasModelDest -Filter "*.inf" -Recurse -File -ErrorAction SilentlyContinue)
                        $archiveExists = ($effectiveHarvestMode -eq "NetOnly") -or (Test-Path $nasArchiveTar -ErrorAction SilentlyContinue)
                        if ($existingInfs.Count -ge 3 -and $archiveExists) {
                            if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                                Write-ServisLog "Drajveri za model $cleanModel vec postoje na NAS serveru ($($existingInfs.Count) .inf paketa u $nasModelDest). Preskacem ponovni upload (deduplikacija)." -Level "INFO" -Component "DriverHarvest"
                            }
                            return
                        }
                    }
                } catch {}
            } elseif ($hubUrl -and $effectiveHarvestMode -ne "NetOnly") {
                try {
                    $packStatusUri = "$hubUrl/api/drivers/model-pack/status?model=$([System.Uri]::EscapeDataString($cleanModel))"
                    $packSt = Invoke-RestMethod -Uri $packStatusUri -Method Get -TimeoutSec 10 -ErrorAction Stop
                    if ($packSt -and $packSt.available) {
                        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                            Write-ServisLog "Arhiva drajvera za model $cleanModel vec postoji na ServiceHub-u ($($packSt.size_mb) MB). Preskacem ponovni izvoz (deduplikacija)." -Level "INFO" -Component "DriverHarvest"
                        }
                        return
                    }
                } catch {}
            }
        }

        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
            Write-ServisLog "Zapocinjem automatsku berbu hardverskih drajvera (Rezim: $effectiveHarvestMode) za model: $cleanModel..." -Level "STEP" -Component "DriverHarvest"
        }
        if (Get-Command Show-ServisDashboard -ErrorAction SilentlyContinue) {
            Show-ServisDashboard -LiveStatus "Izvozim hardverske drajvere za model $cleanModel ka NAS-u..."
        }

        # 5. Privremeni direktorijum za lokalnu ekstrakciju
        $tempExportDir = Join-Path $env:TEMP "WIS_DriverExport_$([guid]::NewGuid().ToString('N'))"
        New-Item -Path $tempExportDir -ItemType Directory -Force -ErrorAction SilentlyContinue | Out-Null

        # Ekstrakcija drajvera: primarno dism.exe, fallback pnputil.exe
        $dismSuccess = $false
        try {
            $proc = Start-Process "dism.exe" -ArgumentList "/online /export-driver /destination:`"$tempExportDir`"" -Wait -PassThru -WindowStyle Hidden -ErrorAction SilentlyContinue
            if ($proc -and $proc.ExitCode -eq 0) {
                $dismSuccess = $true
            }
        } catch {}

        if (-not $dismSuccess) {
            try {
                & pnputil.exe /export-driver * "$tempExportDir" >$null 2>&1
            } catch {}
        }

        # 6. Skeniranje izvezenih INF datoteka
        $allInfFiles = Get-ChildItem -Path $tempExportDir -Filter "*.inf" -Recurse -File -ErrorAction SilentlyContinue
        if (-not $allInfFiles -or $allInfFiles.Count -eq 0) {
            if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                Write-ServisLog "Nisu pronadjeni izvezeni drajveri u privremenom direktorijumu za model $cleanModel." -Level "WARN" -Component "DriverHarvest"
            }
            return
        }

        # Odredjivanje skupa paketa prema rezimu berbe
        $targetInfFiles = @()
        $netInfFiles = @()

        foreach ($inf in $allInfFiles) {
            $isNet = $false
            try {
                $txt = Get-Content $inf.FullName -Raw -ErrorAction SilentlyContinue
                if ($txt -match '(?mi)^\s*Class\s*=\s*Net\b') {
                    $isNet = $true
                    $netInfFiles += $inf
                }
            } catch {}

            if ($effectiveHarvestMode -eq "NetOnly") {
                if ($isNet) { $targetInfFiles += $inf }
            } else {
                $targetInfFiles += $inf
            }
        }

        if ($targetInfFiles.Count -eq 0) {
            if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                Write-ServisLog "Nisu pronadjeni drajveri koji odgovaraju filteru ($effectiveHarvestMode) za model $cleanModel." -Level "DEBUG" -Component "DriverHarvest"
            }
            return
        }

        # 7. Kopiranje drajvera u Drivers\Harvested\Models\<cleanModel> (kada je SMB dostupan)
        # 8. Mrezni drajveri (Class=Net) se kopiraju i u Drivers\Harvested\Net\<cleanModel> radi kompatibilnosti sa ADR-0027
        # OKLOPLJENO u striktan try/catch blok tako da nijedna greska u kreiranju foldera ili Copy-Item ne prekida servisni proces
        $copiedFolders = 0
        $copiedNetFolders = 0
        try {
            if ($smbReady) {
                if (-not (Test-Path $nasModelDest -ErrorAction SilentlyContinue)) {
                    New-Item -Path $nasModelDest -ItemType Directory -Force -ErrorAction Stop | Out-Null
                }

                $copiedDirs = @{}
                foreach ($inf in $targetInfFiles) {
                    $srcFolder = $inf.DirectoryName
                    if (-not $copiedDirs.ContainsKey($srcFolder)) {
                        $copiedDirs[$srcFolder] = $true
                        $folderName = $inf.Directory.Name
                        $destFolder = Join-Path $nasModelDest $folderName
                        if (-not (Test-Path $destFolder -ErrorAction SilentlyContinue)) {
                            New-Item -Path $destFolder -ItemType Directory -Force -ErrorAction Stop | Out-Null
                        }
                        Copy-Item -Path "$srcFolder\*" -Destination $destFolder -Recurse -Force -ErrorAction Stop
                        $copiedFolders++
                    }
                }

                if ($netInfFiles -and $netInfFiles.Count -gt 0) {
                    $nasNetDest = Join-Path $uncShare "Drivers\Harvested\Net\$cleanModel"
                    if (-not (Test-Path $nasNetDest -ErrorAction SilentlyContinue)) {
                        New-Item -Path $nasNetDest -ItemType Directory -Force -ErrorAction Stop | Out-Null
                    }
                    $copiedNetDirs = @{}
                    foreach ($netInf in $netInfFiles) {
                        $srcNetFolder = $netInf.DirectoryName
                        if (-not $copiedNetDirs.ContainsKey($srcNetFolder)) {
                            $copiedNetDirs[$srcNetFolder] = $true
                            $folderName = $netInf.Directory.Name
                            $destFolder = Join-Path $nasNetDest $folderName
                            if (-not (Test-Path $destFolder -ErrorAction SilentlyContinue)) {
                                New-Item -Path $destFolder -ItemType Directory -Force -ErrorAction Stop | Out-Null
                            }
                            Copy-Item -Path "$srcNetFolder\*" -Destination $destFolder -Recurse -Force -ErrorAction Stop
                            $copiedNetFolders++
                        }
                    }
                }

                if ($copiedFolders -gt 0) {
                    if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                        Write-ServisLog "[SUCCESS] Hardverski drajveri uspesno izvezeni na NAS za model: $cleanModel ($copiedFolders paketa u $nasModelDest; mrezni: $copiedNetFolders paketa)." -Level "SUCCESS" -Component "DriverHarvest"
                    }
                    if (Get-Command Show-ServisDashboard -ErrorAction SilentlyContinue) {
                        Show-ServisDashboard -LiveStatus "[OK] Drajveri za $cleanModel uspesno izvezeni na NAS ($copiedFolders paketa)"
                    }
                }
            }

            # 9. Single-Stream tar.exe arhiviranje za hiper-brzu distribuciju floti racunara (SMB primarno, ServiceHub HTTP fallback)
            if ($effectiveHarvestMode -ne "NetOnly") {
                $tarStoredOnSmb = $false
                try {
                    $tarPath = Join-Path $env:TEMP ("$cleanModel.tar")
                    if (Test-Path $tarPath) { Remove-Item -Path $tarPath -Force -ErrorAction SilentlyContinue }
                    & tar.exe -cf "$tarPath" -C "$tempExportDir" . 2>$null
                    $tarExit = $LASTEXITCODE
                    if ($tarExit -eq 0 -and (Test-Path $tarPath) -and ((Get-Item $tarPath).Length -gt 0)) {
                        if ($smbReady) {
                            try {
                                $nasArchivesDir = Join-Path $uncShare "Drivers\Harvested\Archives"
                                if (-not (Test-Path $nasArchivesDir -ErrorAction SilentlyContinue)) {
                                    New-Item -Path $nasArchivesDir -ItemType Directory -Force -ErrorAction Stop | Out-Null
                                }
                                $destTarPath = Join-Path $nasArchivesDir "$cleanModel.tar"
                                $destTarTmp = Join-Path $nasArchivesDir "$cleanModel.tar.tmp.$PID"
                                Copy-Item -Path $tarPath -Destination $destTarTmp -Force -ErrorAction Stop
                                Move-Item -Path $destTarTmp -Destination $destTarPath -Force -ErrorAction Stop
                                $destTarTmp = $null
                                $tarStoredOnSmb = $true
                                if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                                    Write-ServisLog "Drajveri modela $cleanModel uspesno arhivirani u $cleanModel.tar na NAS-u." -Level "SUCCESS" -Component "DriverHarvest"
                                }
                            } catch {
                                if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                                    Write-ServisLog "UPOZORENJE: SMB snimanje tar arhive ($cleanModel.tar) nije uspelo: $($_.Exception.Message)" -Level "WARN" -Component "DriverHarvest"
                                }
                            }
                        }

                        if (-not $tarStoredOnSmb -and $hubUrl) {
                            if (Send-ModelDriverPackToServisHub -HubUrl $hubUrl -FilePath $tarPath -CleanModel $cleanModel) {
                                if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                                    Write-ServisLog "Drajveri modela $cleanModel uspesno uploadovani kao $cleanModel.tar na ServiceHub HTTP skladiste." -Level "SUCCESS" -Component "DriverHarvest"
                                }
                                if (Get-Command Show-ServisDashboard -ErrorAction SilentlyContinue) {
                                    Show-ServisDashboard -LiveStatus "[OK] Paket drajvera ($cleanModel.tar) sacuvan na ServiceHub"
                                }
                            } elseif (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                                Write-ServisLog "UPOZORENJE: HTTP upload arhive drajvera $cleanModel.tar na ServiceHub nije uspeo." -Level "WARN" -Component "DriverHarvest"
                            }
                        }
                    } else {
                        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                            Write-ServisLog "UPOZORENJE: tar.exe pakovanje za $cleanModel nije proizvelo validnu arhivu (ExitCode=$tarExit)." -Level "WARN" -Component "DriverHarvest"
                        }
                    }
                } catch {
                    if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                        Write-ServisLog "UPOZORENJE: Arhiviranje drajvera u tar paket ($cleanModel.tar) nije uspelo: $($_.Exception.Message)" -Level "WARN" -Component "DriverHarvest"
                    }
                } finally {
                    if ($destTarTmp -and (Test-Path $destTarTmp -ErrorAction SilentlyContinue)) {
                        Remove-Item -Path $destTarTmp -Force -ErrorAction SilentlyContinue
                    }
                    if ($tarPath -and (Test-Path $tarPath)) {
                        Remove-Item -Path $tarPath -Force -ErrorAction SilentlyContinue
                    }
                }
            }
        } catch {
            if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                Write-ServisLog "UPOZORENJE: Arhiviranje drajvera na NAS server nije uspelo ($($_.Exception.Message)). Nastavljam sa servisnim procesom..." -Level "WARN" -Component "DriverHarvest"
            }
            if (Get-Command Show-ServisDashboard -ErrorAction SilentlyContinue) {
                Show-ServisDashboard -LiveStatus "Upozorenje: Arhiviranje drajvera na NAS nije uspelo, nastavljam..."
            }
        }

    } catch {
        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
            Write-ServisLog "UPOZORENJE: Neocekivana greska tokom izvoza drajvera: $($_.Exception.Message)" -Level "WARN" -Component "DriverHarvest"
        }
    } finally {
        # Brisanje privremenog foldera i tar arhive u $env:TEMP
        if ($tempExportDir -and (Test-Path $tempExportDir)) {
            Remove-Item -Path $tempExportDir -Recurse -Force -ErrorAction SilentlyContinue
        }
        if ($tarPath -and (Test-Path $tarPath)) {
            Remove-Item -Path $tarPath -Force -ErrorAction SilentlyContinue
        }
        # Garantovano raskidanje iskljucivo ove SMB sesije radi bezbednosti
        if ($smbMapped -and $uncShare) {
            Disconnect-ServisNasUpdatesShare -UncShare $uncShare
        }
    }
}

function Export-ServisNetworkDrivers {
    <#
    .SYNOPSIS
        Kompatibilni wrapper za Export-ServisHardwareDrivers.
    #>
    [CmdletBinding()]
    param(
        [switch]$Force
    )
    Export-ServisHardwareDrivers -Force:$Force
}

function Invoke-WindowsUpdateCycle {
    <#
    .SYNOPSIS
        Kompletan ciklus Windows Update procesa ukljucujuci NAS pre-patch, lokalne drajvere i COM API pretragu.
    #>
    param(
        [string]$LocalScriptPath = "C:\setup\Install-ServisApps.ps1"
    )
    Invoke-NasUpdatePrePatch
    Invoke-ModelDriversPreInstall
    Invoke-WindowsUpdatePhase -LocalScriptPath $LocalScriptPath
}

function Invoke-WindowsUpdatePhase {
    param(
        [string]$LocalScriptPath = "C:\setup\Install-ServisApps.ps1"
    )
    # 0a. Lokalni NAS pre-patch kumulativnih zakrpi ako je omogucen u konfiguraciji
    Invoke-NasUpdatePrePatch

    # 0b. Pre-instalacija kompletnih drajvera modela sa NAS-a pre COM API skeniranja
    Invoke-ModelDriversPreInstall

    # 0c. Primena mrežnih i hardverskih optimizacija za maksimalnu brzinu Windows Update-a
    Optimize-WUPerformance


    # Pokretanje bezbednog paralelnog radnika za instalaciju programa
    # Non-MSI programi (WinRAR, BatteryBar, Office Click-to-Run) se instaliraju odmah u pozadini.
    # MSI programi (.NET, Chrome, Acrobat) koriste Wait-MsiMutexAvailable kako bi sacekali oslobadjanje Windows Installera.
    if (-not $Global:AppWorkerProc -or $Global:AppWorkerProc.HasExited) {
        $Global:AppWorkerProc = Start-AppWorker -ScriptPath $LocalScriptPath
    }

    $cycle = if ($Global:State -and $Global:State.UpdateCycle) { $Global:State.UpdateCycle } else { 1 }
    $maxC = if ($Global:State -and $Global:State.MaxCycles) { $Global:State.MaxCycles } else { 3 }

    # Detaljna telemetrija nedostajucih PNP drajvera pre skeniranja
    $missingPnpPre = if (Get-Command Get-PnpMissingDriversDetail -ErrorAction SilentlyContinue) { Get-PnpMissingDriversDetail } else { @() }
    if ($missingPnpPre -and $missingPnpPre.Count -gt 0) {
        if (Get-Command Write-DiagnosticEvent -ErrorAction SilentlyContinue) {
            Write-DiagnosticEvent -Event "PNP_MISSING_PRE" -Data $missingPnpPre -Message "Pre pocetka WU (Krug $cycle) detektovano $($missingPnpPre.Count) uredjaja bez drajvera." -Component "Hardware"
        }
        foreach ($d in $missingPnpPre) {
            Write-ServisLog "  [PNP-NEDOSTAJE] $($d.FriendlyName) | Klasa: $($d.Class) | HWID: $($d.HardwareIDs)" -Level "WARN" -Component "Hardware"
        }
    } else {
        if (Get-Command Write-DiagnosticEvent -ErrorAction SilentlyContinue) {
            Write-DiagnosticEvent -Event "PNP_MISSING_PRE" -Data @() -Message "Svi hardverski drajveri su prepoznati." -Component "Hardware"
        }
    }

    if (Get-Command Wait-ServisUICooperativePause -ErrorAction SilentlyContinue) {
        Wait-ServisUICooperativePause -CheckpointName "Windows Update (Krug $cycle/$maxC)"
    }
    if (Get-Command Update-ServisUI -ErrorAction SilentlyContinue) {
        Update-ServisUI -Phase 2 -SubPhase "Windows Update (Krug $cycle/$maxC)" -StatusMessage "Pokrecem Windows Update skeniranje (Krug $cycle/$maxC)..." -ProgressPercent 25
    }

    if (Get-Command Show-ServisDashboard -ErrorAction SilentlyContinue) {
        Show-ServisDashboard -LiveStatus "Pokrecem Windows Update skeniranje (Krug $cycle/$maxC)..."
    }

    # Ako je u medjuvremenu prekinut net, sacekaj kabl
    while (-not (Test-Internet)) {
        if (Get-Command Show-ServisDashboard -ErrorAction SilentlyContinue) {
            Show-ServisDashboard -LiveStatus "[PAZNJA] NEMA INTERNETA! Prikljucite mrezni kabl za Windows Update..." -SubLogs @(
                "Cekam mreznu konekciju radi preuzimanja zakrpa i drajvera..."
            )
        }
        Start-Sleep -Seconds 5
    }

    # 1. Registracija Microsoft Update servisa (obuhvata drajvere proizvodjaca hardvera)
    $muServiceId = "7971f918-a847-4430-9279-4a52d1efe18d"
    $isMuRegistered = $false
    try {
        $ServiceManager = New-Object -ComObject Microsoft.Update.ServiceManager
        $ServiceManager.ClientApplicationID = "WIS Servis Toolkit"
        foreach ($svc in $ServiceManager.Services) {
            if ($svc.ServiceID -eq $muServiceId) { $isMuRegistered = $true; break }
        }
        if (-not $isMuRegistered) {
            $ServiceManager.AddService2($muServiceId, 7, "") | Out-Null
            $isMuRegistered = $true
        }
    } catch { $isMuRegistered = $false }

    try {
        $maxAttempts = 3
        $SearchResult = $null
        $UpdateCount = 0

        for ($attempt = 1; $attempt -le $maxAttempts; $attempt++) {
            $missingHW = 0
            if (Get-Command Get-MissingHardwareCount -ErrorAction SilentlyContinue) {
                $missingHW = Get-MissingHardwareCount
            }
            $statusText = "Pretrazujem Microsoft servere za zakrpe i drajvere (Pokusaj $attempt/$maxAttempts)"
            if ($missingHW -gt 0) { $statusText += " [Otkriveno $missingHW uredjaja bez drajvera]" }
            if (Get-Command Show-ServisDashboard -ErrorAction SilentlyContinue) {
                Show-ServisDashboard -LiveStatus "$statusText..."
            }

            if ($attempt -gt 1 -or $cycle -eq 1) {
                try {
                    Start-Process "usoclient.exe" -ArgumentList "StartInteractiveScan" -WindowStyle Hidden -ErrorAction SilentlyContinue
                } catch {}
                Start-Sleep -Seconds 3
            }

            try {
                $UpdateSession = New-Object -ComObject Microsoft.Update.Session
                $UpdateSearcher = $UpdateSession.CreateUpdateSearcher()

                if ($isMuRegistered) {
                    $UpdateSearcher.ServerSelection = 3
                    $UpdateSearcher.ServiceID = $muServiceId
                } else {
                    $UpdateSearcher.ServerSelection = 2
                }

                $SearchResult = $UpdateSearcher.Search("IsInstalled=0 and IsHidden=0")
                $UpdateCount = if ($SearchResult -and $SearchResult.Updates) { $SearchResult.Updates.Count } else { 0 }
            } catch {
                $UpdateCount = 0
                try {
                    $UpdateSearcher.ServerSelection = 2
                    $SearchResult = $UpdateSearcher.Search("IsInstalled=0 and IsHidden=0")
                    $UpdateCount = if ($SearchResult -and $SearchResult.Updates) { $SearchResult.Updates.Count } else { 0 }
                } catch {
                    try {
                        $UpdateSearcher.ServerSelection = 0
                        $SearchResult = $UpdateSearcher.Search("IsInstalled=0")
                        $UpdateCount = if ($SearchResult -and $SearchResult.Updates) { $SearchResult.Updates.Count } else { 0 }
                    } catch { $UpdateCount = 0 }
                }
            }

            if ($UpdateCount -gt 0) {
                break
            }

            if ($attempt -lt $maxAttempts) {
                $waitMsg = if ($missingHW -gt 0) { "Hardver ceka $missingHW drajvera sa servera" } else { "Inicijalizacija baze zakrpa novog sistema" }
                if (Get-Command Show-ServisDashboard -ErrorAction SilentlyContinue) {
                    Show-ServisDashboard -LiveStatus "Katalog se sinhronizuje ($waitMsg). Cekam server..." -SubLogs @(
                        "Windows Update Agent uspostavlja kontakt sa Microsoft katalogom...",
                        "Sledeci pokusaj pretrage za 12 sekundi (Pokusaj $($attempt + 1) od $maxAttempts)..."
                    )
                }
                try { Restart-Service -Name wuauserv -Force -ErrorAction SilentlyContinue } catch {}
                Start-Sleep -Seconds 12
            }
        }

        if ($UpdateCount -eq 0) {
            $finalMissingHW = 0
            if (Get-Command Get-MissingHardwareCount -ErrorAction SilentlyContinue) {
                $finalMissingHW = Get-MissingHardwareCount
            }
            if ($finalMissingHW -gt 0 -and $cycle -eq 1) {
                if (Get-Command Show-ServisDashboard -ErrorAction SilentlyContinue) {
                    Show-ServisDashboard -LiveStatus "[PAZNJA] Server trenutno nije vratio drajvere ($finalMissingHW uredjaja neprepoznato)!" -SubLogs @(
                        "Moguci uzrok: Serveru je potrebno par minuta da profilise cipset ili je privremeno preopterecen.",
                        "Pritisnite [P] za ponovnu pretragu ili [ENTER] za prelazak na programe...",
                        "(Automatski prelazak na instalaciju programa za 15 sekundi)"
                    )
                }
                $userChoice = ""
                $isWorker = ($Global:Worker -or $Global:WIS_UI_Backend -eq "Pipeline")
                if (-not $isWorker) {
                    $sw = [System.Diagnostics.Stopwatch]::StartNew()
                    while ($sw.Elapsed.TotalSeconds -lt 15) {
                        try {
                            if ([Console]::KeyAvailable) {
                                $k = [Console]::ReadKey($true)
                                $userChoice = $k.KeyChar.ToString().ToUpper()
                                break
                            }
                        } catch {
                            # Preusmeren STDIN baca InvalidOperationException - ignorisi i nastavi
                            break
                        }
                        Start-Sleep -Milliseconds 250
                    }
                }
                if ($userChoice -eq "P") {
                    if (Get-Command Show-ServisDashboard -ErrorAction SilentlyContinue) {
                        Show-ServisDashboard -LiveStatus "Ponovo pokrecem Windows Update proceduru na zahtev servisera..."
                    }
                    Start-Sleep -Seconds 2
                    return
                }
            }

            if (Get-Command Show-ServisDashboard -ErrorAction SilentlyContinue) {
                Show-ServisDashboard -LiveStatus "[OK] SISTEM JE AZURIRAN! Nema novih zakrpa ni drajvera." -SubLogs @("Svi drajveri i bezbednosne zakrpe su uspesno instalirani.")
            }
            # Automatska berba i arhiviranje hardverskih drajvera na centralni NAS
            Export-ServisHardwareDrivers
            Start-Sleep -Seconds 3
            if ($Global:State) {
                $Global:State.Phase = 3
                if (Get-Command Save-State -ErrorAction SilentlyContinue) { Save-State }
            }
        } else {
            # Filtriranje paketa prema servisnoj konfiguraciji (DriversOnly, SkipCumulative, Full)
            $wuMode = Get-WUConfigMode
            $rawTotal = $SearchResult.Updates.Count
            if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                Write-ServisLog "Katalog vratio $rawTotal stavki. Primenjujem servisni filter: $wuMode..." -Level "INFO" -Component "Phase2"
            }

            $PendingUpdates = [System.Collections.Generic.List[object]]::new()
            for ($k = 0; $k -lt $rawTotal; $k++) {
                $item = $SearchResult.Updates.Item($k)
                $isDrv = Test-IsDriverUpdate -UpdateItem $item
                $isCum = Test-IsCumulativeUpdate -UpdateItem $item

                $include = $false
                if ($wuMode -eq "DriversOnly") {
                    if ($isDrv -and (-not $isCum)) { $include = $true }
                } elseif ($wuMode -eq "SkipCumulative") {
                    if (-not $isCum) { $include = $true }
                } else {
                    $include = $true
                }

                if ($include) {
                    if (-not $item.EulaAccepted) {
                        try { $item.AcceptEula() } catch {}
                    }
                    $PendingUpdates.Add($item)
                } else {
                    if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                        Write-ServisLog "Preskacem stavku (Rezim: $wuMode - integrisano u ISO): $($item.Title)" -Level "DEBUG" -Component "Phase2"
                    }
                }
            }

            $UpdateCount = $PendingUpdates.Count

            if ($UpdateCount -eq 0) {
                if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                    Write-ServisLog "Nakon primene filtera ($wuMode) nema preostalih drajvera za instalaciju. Sistem je hardverski kompletiran." -Level "SUCCESS" -Component "Phase2"
                }
                if (Get-Command Show-ServisDashboard -ErrorAction SilentlyContinue) {
                    Show-ServisDashboard -LiveStatus "[OK] HARDVERSKI DRAJVERI KOMPLETIRANI! Prelazim na sledecu fazu..." -SubLogs @(
                        "Svi pronadjeni hardverski drajveri su vec ugradjeni.",
                        "Kumulativne nadogradnje su integrisane u instalacioni ISO."
                    )
                }
                # Automatska berba i arhiviranje hardverskih drajvera na centralni NAS
                Export-ServisHardwareDrivers
                Start-Sleep -Seconds 3
                if ($Global:State) {
                    $Global:State.Phase = 3
                    if (Get-Command Save-State -ErrorAction SilentlyContinue) { Save-State }
                }
                return
            }

            if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                Write-ServisLog "Zapocinjem sekvencijalnu instalaciju $UpdateCount drajvera (jedan po jedan sa zivim TUI progresom)..." -Level "STEP" -Component "Phase2"
            }

            # ------------------------------------------------------------------
            # SEKVENCIJALNI RAD: Drajver po drajver (Preuzmi -> Instaliraj -> TUI)
            # ------------------------------------------------------------------
            $InstalledCount = 0
            $requiresReboot = $false

            for ($i = 0; $i -lt $UpdateCount; $i++) {
                $u = $PendingUpdates[$i]
                $curr = $i + 1

                # 1. POJEDINACNO PREUZIMANJE (Brzo, 2-5s po paketu)
                if (-not $u.IsDownloaded) {
                    $dlTxt = Format-WUProgress -Done $i -Total $UpdateCount
                    $inTxt = Format-WUProgress -Done $InstalledCount -Total $UpdateCount
                    $queueDisplay = Get-WUQueueDisplay -Queue $PendingUpdates

                    if (Get-Command Show-ServisDashboard -ErrorAction SilentlyContinue) {
                        Show-ServisDashboard `
                            -LiveStatus "Preuzimam drajver $curr od ${UpdateCount}: $($u.Title)" `
                            -DownloadProgress $dlTxt `
                            -InstallProgress $inTxt `
                            -SubLogs $queueDisplay
                    }
                    if (Get-Command Send-ServisHeartbeat -ErrorAction SilentlyContinue) {
                        $pctProg = [math]::Round(($i / $UpdateCount) * 100)
                        Send-ServisHeartbeat -LiveStatus "Preuzimam drajver ($curr/$UpdateCount): $($u.Title)" -WorkerProgress $pctProg
                    }
                    if (Get-Command Update-ServisUI -ErrorAction SilentlyContinue) {
                        $pctProg = [math]::Round(($i / $UpdateCount) * 100)
                        Update-ServisUI -Phase 2 -SubPhase "WU Preuzimanje ($curr/$UpdateCount)" -StatusMessage "Preuzimam: $($u.Title)" -ProgressPercent $pctProg
                    }

                    $singleDl = New-Object -ComObject Microsoft.Update.UpdateColl
                    $singleDl.Add($u) | Out-Null
                    $Downloader = $UpdateSession.CreateUpdateDownloader()
                    $Downloader.Updates = $singleDl
                    $swDl = [System.Diagnostics.Stopwatch]::StartNew()
                    try {
                        $Downloader.Download() | Out-Null
                        $swDl.Stop()
                        $durDl = [math]::Round($swDl.Elapsed.TotalSeconds, 1)
                        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                            Write-ServisLog "Preuzet drajver ($curr/$UpdateCount): $($u.Title) za ${durDl}s" -Level "SUCCESS" -Component "Phase2"
                        }
                    } catch {
                        $swDl.Stop()
                        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                            Write-ServisLog "Greska pri preuzimanju drajvera $($u.Title): $($_.Exception.Message)" -Level "WARN" -Component "Phase2"
                        }
                    }
                }

                # 2. POJEDINACNA INSTALACIJA (Brzo, 3-10s po paketu)
                if ($u.IsDownloaded) {
                    $dlTxt = Format-WUProgress -Done $curr -Total $UpdateCount
                    $inTxt = Format-WUProgress -Done $InstalledCount -Total $UpdateCount
                    $queueDisplay = Get-WUQueueDisplay -Queue $PendingUpdates

                    if (Get-Command Show-ServisDashboard -ErrorAction SilentlyContinue) {
                        Show-ServisDashboard `
                            -LiveStatus "Instaliram drajver $curr od ${UpdateCount}: $($u.Title)" `
                            -DownloadProgress $dlTxt `
                            -InstallProgress $inTxt `
                            -SubLogs $queueDisplay
                    }
                    if (Get-Command Send-ServisHeartbeat -ErrorAction SilentlyContinue) {
                        $pctProg = [math]::Round(($curr / $UpdateCount) * 100)
                        Send-ServisHeartbeat -LiveStatus "Instaliram drajver ($curr/$UpdateCount): $($u.Title)" -WorkerProgress $pctProg
                    }
                    if (Get-Command Update-ServisUI -ErrorAction SilentlyContinue) {
                        $pctProg = [math]::Round(($curr / $UpdateCount) * 100)
                        Update-ServisUI -Phase 2 -SubPhase "WU Instalacija ($curr/$UpdateCount)" -StatusMessage "Instaliram: $($u.Title)" -ProgressPercent $pctProg
                    }

                    $singleInst = New-Object -ComObject Microsoft.Update.UpdateColl
                    $singleInst.Add($u) | Out-Null
                    $Installer = $UpdateSession.CreateUpdateInstaller()
                    $Installer.Updates = $singleInst

                    # Postavi OS-wide flag da je WU instalacija aktivna (sprecava konflikt sa msiexec)
                    Set-ItemProperty -Path "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Installer" -Name "ServisWuActive" -Value 1 -Force -ErrorAction SilentlyContinue
                    try {
                        $swWu = [System.Diagnostics.Stopwatch]::StartNew()
                        $instRes = $Installer.Install()
                        $swWu.Stop()
                        $durWu = [math]::Round($swWu.Elapsed.TotalSeconds, 1)
                        $resCode = if ($instRes) { $instRes.ResultCode } else { "Unknown" }
                        $resText = switch ($resCode) {
                            2 { "Uspesno" }
                            3 { "Uspesno sa greskama" }
                            4 { "Neuspesno" }
                            5 { "Prekinuto" }
                            default { "Kod: $resCode" }
                        }
                        $logLevel = if ($resCode -eq 2) { "SUCCESS" } else { "WARN" }
                        $diagExitCode = $resCode
                        if ($resCode -eq 4 -and $instRes) {
                            $subRes = try { $instRes.GetUpdateResult(0) } catch { $null }
                            if ($subRes) {
                                $hresInt = $subRes.HResult
                                $hexHres = "0x{0:x8}" -f ($hresInt -band 0xFFFFFFFF)
                                if ($hexHres -eq "0x8024200b" -or $hexHres -eq "0x80070103") {
                                    $resText = "Noviji drajver vec prisutan na sistemu ($hexHres)"
                                    $logLevel = "INFO"
                                    $diagExitCode = 0
                                }
                            }
                        }
                        if ($instRes.RebootRequired) { $requiresReboot = $true }
                        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                            Write-ServisLog "Instaliran drajver ($curr/$UpdateCount): $($u.Title) -> $resText za ${durWu}s (RebootRequired: $($instRes.RebootRequired))" -Level $logLevel -Component "Phase2"
                        }
                        if (Get-Command Write-DiagnosticEvent -ErrorAction SilentlyContinue) {
                            Write-DiagnosticEvent -Event "WU_PACKAGE_RESULT" -App $u.Title -ExitCode $diagExitCode -DurationSec $durWu -Status $resText -Level $logLevel -Component "Phase2"
                        }
                        $InstalledCount++
                    } catch {
                        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                            Write-ServisLog "Greska pri instalaciji drajvera $($u.Title): $($_.Exception.Message)" -Level "ERROR" -Component "Phase2"
                        }
                        if (Get-Command Write-DiagnosticEvent -ErrorAction SilentlyContinue) {
                            Write-DiagnosticEvent -Event "WU_PACKAGE_ERROR" -App $u.Title -ExitCode -1 -Status "ERROR" -Message $_.Exception.Message -Level "ERROR" -Component "Phase2"
                        }
                        $InstalledCount++
                    } finally {
                        Set-ItemProperty -Path "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Installer" -Name "ServisWuActive" -Value 0 -Force -ErrorAction SilentlyContinue
                    }
                }
            }

            $dlTxt = Format-WUProgress -Done $UpdateCount -Total $UpdateCount
            $inTxt = Format-WUProgress -Done $InstalledCount -Total $UpdateCount
            $finalQueue = Get-WUQueueDisplay -Queue $PendingUpdates
            if (Get-Command Show-ServisDashboard -ErrorAction SilentlyContinue) {
                Show-ServisDashboard `
                    -LiveStatus "Krug $cycle obradjen! Zavrseno $InstalledCount od $UpdateCount azuriranja." `
                    -DownloadProgress $dlTxt `
                    -InstallProgress $inTxt `
                    -SubLogs $finalQueue
            }
            Start-Sleep -Seconds 2

            # Dvosmerna berba i arhiviranje preuzetih/instaliranih Windows zakrpi na centralni NAS / ServiceHub
            try {
                Export-ServisWindowsUpdates -Updates $PendingUpdates
            } catch {}

            if (Test-Path "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired") {
                $requiresReboot = $true
            }

            # Cekanje paralelnog radnika ako je jos u toku pre restarta
            if ($Global:AppWorkerProc -and (-not $Global:AppWorkerProc.HasExited)) {
                if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                    Write-ServisLog "Windows Update krug je zavrsen, ali pozadinska instalacija programa jos traje. Cekam radnika pre restarta..." -Level "INFO" -Component "Parallel"
                }
                $waitSec = 0
                while ((-not $Global:AppWorkerProc.HasExited) -and ($waitSec -lt 900)) {
                    $wStatus = if (Get-Command Get-AppWorkerStatus -ErrorAction SilentlyContinue) { Get-AppWorkerStatus } else { $null }
                    $appInfo = if ($wStatus) { "$($wStatus.CurrentApp) ($($wStatus.Progress)%)" } else { "Instalacija programa u toku..." }
                    if (Get-Command Show-ServisDashboard -ErrorAction SilentlyContinue) {
                        Show-ServisDashboard `
                            -LiveStatus "Windows Update krug zavrsen. Cekam zavrsetak instalacije programa pre restarta..." `
                            -SubLogs @(
                                "Programi se instaliraju u pozadini: $appInfo",
                                "Sistem ce se automatski restartovati za sledeci krug Windows Update-a cim programi zavrse."
                            )
                    }
                    Start-Sleep -Seconds 3
                    $waitSec += 3
                }
                if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                    Write-ServisLog "Paralelna instalacija programa je zavrsena. Nastavljam sa restartom." -Level "SUCCESS" -Component "Parallel"
                }
            }

            # Detaljna telemetrija preostalih nedostajucih drajvera na kraju ovog kruga
            $missingPnpPost = if (Get-Command Get-PnpMissingDriversDetail -ErrorAction SilentlyContinue) { Get-PnpMissingDriversDetail } else { @() }
            if (Get-Command Write-DiagnosticEvent -ErrorAction SilentlyContinue) {
                Write-DiagnosticEvent -Event "PNP_MISSING_POST" -Data $missingPnpPost -Message "Na kraju WU kruga $cycle preostalo $($missingPnpPost.Count) uredjaja bez drajvera." -Component "Hardware"
            }

            # Provera broja krugova i restart
            if ($cycle -lt $maxC) {
                if ($Global:State) {
                    $Global:State.UpdateCycle = $cycle + 1
                    if (Get-Command Save-State -ErrorAction SilentlyContinue) { Save-State }
                }

                Set-ItemProperty -Path "HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon" -Name "AutoAdminLogon" -Value "1" -ErrorAction SilentlyContinue
                Set-ItemProperty -Path "HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon" -Name "DefaultUserName" -Value "Korisnik" -ErrorAction SilentlyContinue

                $runCmd = "powershell.exe -NoProfile -NoExit -ExecutionPolicy Bypass -File `"$LocalScriptPath`""
                Set-ItemProperty -Path "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce" -Name "ServisDeployResume" -Value $runCmd -Force

                for ($sec = 5; $sec -gt 0; $sec--) {
                    if (Get-Command Show-ServisDashboard -ErrorAction SilentlyContinue) {
                        Show-ServisDashboard -LiveStatus "Krug $cycle instaliran! RESTARTUJEM RACUNAR ZA $sec SEKUNDI..." -SubLogs @(
                            "Skripta automatski nastavlja sledeci krug provere azuriranja i drajvera."
                        )
                    }
                    Start-Sleep -Seconds 1
                }
                Restart-Computer -Force
                exit
            } else {
                if (Get-Command Show-ServisDashboard -ErrorAction SilentlyContinue) {
                    Show-ServisDashboard -LiveStatus "Dostignut maksimalan broj restarta ($maxC). Nastavljam na optimizaciju i programe..."
                }
                # Automatska berba i arhiviranje hardverskih drajvera na centralni NAS
                Export-ServisHardwareDrivers
                Start-Sleep -Seconds 3
                if ($Global:State) {
                    $Global:State.Phase = 3
                    if (Get-Command Save-State -ErrorAction SilentlyContinue) { Save-State }
                }
            }
        }
    } catch {
        if (Get-Command Show-ServisDashboard -ErrorAction SilentlyContinue) {
            Show-ServisDashboard -LiveStatus "UPOZORENJE pri radu Windows Update-a: $($_.Exception.Message). Nastavljam dalje..."
        }
        Start-Sleep -Seconds 4
        if ($Global:State) {
            $Global:State.Phase = 3
            if (Get-Command Save-State -ErrorAction SilentlyContinue) { Save-State }
        }
    } finally {
        Set-ItemProperty -Path "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Installer" -Name "ServisWuActive" -Value 0 -Force -ErrorAction SilentlyContinue
    }
}

function Invoke-Phase2Drivers {
    [CmdletBinding()]
    param(
        [string]$LocalSetupDir = "C:\setup",
        [switch]$SkipWindowsUpdate
    )

    $Global:CurrentPhase = 2
    if (-not $Global:State) { $Global:State = @{} }
    $Global:State.Phase = 2

    if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
        Write-ServisLog "=== POKRETANJE FAZE 2: Drajveri i PnP Integracija ===" -Level "STEP" -Component "Phase2"
    }
    if (Get-Command Send-IpcProgress -ErrorAction SilentlyContinue) {
        Send-IpcProgress -Progress 5 -Phase 2 -PhaseName "Drajveri" -SubPhase "Inicijalizacija" -StatusMessage "Skeniranje hardverskih komponenti i drajvera..."
    }

    if (Get-Command Wait-ServisUICooperativePause -ErrorAction SilentlyContinue) {
        Wait-ServisUICooperativePause -CheckpointName "Faza 2 - Pocetak"
    }

    # Uvoz StepGuard modula ukoliko funkcija Invoke-WisStep nije registrovana u sesiji
    if (-not (Get-Command Invoke-WisStep -ErrorAction SilentlyContinue)) {
        $stepGuardMod = @(
            (Join-Path $PSScriptRoot "WIS.StepGuard.psm1"),
            (Join-Path $PSScriptRoot "..\Modules\WIS.StepGuard.psm1"),
            (Join-Path $LocalSetupDir "Skripte\Modules\WIS.StepGuard.psm1"),
            "C:\setup\Skripte\Modules\WIS.StepGuard.psm1"
        ) | Where-Object { Test-Path $_ } | Select-Object -First 1
        if ($stepGuardMod) { Import-Module $stepGuardMod -Force -ErrorAction SilentlyContinue }
    }

    # Uvoz Hardware modula ukoliko funkcija Test-WisDeviceManagerClean nije registrovana
    if (-not (Get-Command Test-WisDeviceManagerClean -ErrorAction SilentlyContinue)) {
        $hwMod = @(
            (Join-Path $PSScriptRoot "WIS.Hardware.psm1"),
            (Join-Path $PSScriptRoot "..\Modules\WIS.Hardware.psm1"),
            (Join-Path $LocalSetupDir "Skripte\Modules\WIS.Hardware.psm1"),
            "C:\setup\Skripte\Modules\WIS.Hardware.psm1"
        ) | Where-Object { Test-Path $_ } | Select-Object -First 1
        if ($hwMod) { Import-Module $hwMod -Force -ErrorAction SilentlyContinue }
    }

    # Emitovanje liste svih koraka u stanju WAIT da bi UI odmah iscrtao listu koraka
    $phase2StepCatalog = @(
        @{ Id = "P2.Identify";  Title = "Identifikacija računara i hardvera" },
        @{ Id = "P2.NasCache";  Title = "TrueNAS model drajveri i pre-patch zakrpe" },
        @{ Id = "P2.Vmd";       Title = "Intel VMD / RST skladišni drajveri" },
        @{ Id = "P2.Pnp";       Title = "PnP instalacija lokalnih drajvera" },
        @{ Id = "P2.Diag";      Title = "Dijagnostika preostalih uređaja" },
        @{ Id = "P2.WuDrivers"; Title = "Hardverski drajveri preko Windows Update-a" }
    )
    foreach ($sc in $phase2StepCatalog) {
        if (Get-Command Send-IpcStep -ErrorAction SilentlyContinue) {
            Send-IpcStep -Phase 2 -StepId $sc.Id -Title $sc.Title -State "WAIT" -Reason ""
        }
    }

    $phaseCtx = @{
        ModelName = "Standardni PC"
        SerialNum = "N/A"
        SkipWU    = [bool]$SkipWindowsUpdate
    }

    # =========================================================================
    # 1. IDENTIFIKACIJA MODELA I HARDVERSKIH PARAMETARA - P2.Identify (-NoSkip)
    # =========================================================================
    Invoke-WisStep -Phase 2 -StepId "P2.Identify" -Title "Identifikacija računara i hardvera" -NoSkip -Test {
        return @{ Done = $true; Reason = "Identifikacija računara obavezna" }
    } -Action {
        try {
            $cs = Get-CimInstance Win32_ComputerSystem -ErrorAction SilentlyContinue
            $bios = Get-CimInstance Win32_Bios -ErrorAction SilentlyContinue
            if ($cs -and $cs.Model) { $phaseCtx.ModelName = ($cs.Model -replace '\s+', ' ').Trim() }
            if ($bios -and $bios.SerialNumber) { $phaseCtx.SerialNum = $bios.SerialNumber.Trim() }
        } catch {}

        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
            Write-ServisLog "Detektovan racunar: $($phaseCtx.ModelName) (Serijski broj: $($phaseCtx.SerialNum))" -Level "INFO" -Component "Hardware"
        }
        if (Get-Command Send-IpcProgress -ErrorAction SilentlyContinue) {
            Send-IpcProgress -Progress 15 -Phase 2 -PhaseName "Drajveri" -SubPhase "Identifikacija" -StatusMessage "Identifikovan model: $($phaseCtx.ModelName)"
        }

        try { Optimize-WUPerformance } catch {}
    }

    # =========================================================================
    # 2. TRUENAS MODEL DRAJVERI I PRE-PATCH - P2.NasCache
    # =========================================================================
    Invoke-WisStep -Phase 2 -StepId "P2.NasCache" -Title "TrueNAS model drajveri i pre-patch zakrpe" -JournalTtlHours 6 -Test {
        if (-not (Test-Path "C:\setup\nas-prepatch-done.flag")) {
            return @{ Done = $false; Reason = "NAS pre-patch i provera zakrpa jos nisu izvrseni na ovom sistemu" }
        }
        Test-WisDeviceManagerClean
    } -Action {
        if (Get-Command Send-IpcProgress -ErrorAction SilentlyContinue) {
            Send-IpcProgress -Progress 20 -Phase 2 -PhaseName "Drajveri" -SubPhase "TrueNAS Keš" -StatusMessage "Provera single-stream drajvera (<Model>.tar) i zakrpa na TrueNAS-u..."
        }
        try {
            Invoke-NasUpdatePrePatch
        } catch {}
        try {
            $setupFlagDir = "C:\setup"
            if (-not (Test-Path $setupFlagDir)) {
                New-Item -Path $setupFlagDir -ItemType Directory -Force -ErrorAction SilentlyContinue | Out-Null
            }
            Set-Content -Path "C:\setup\nas-prepatch-done.flag" -Value (Get-Date).ToString("o") -Encoding UTF8 -Force -ErrorAction SilentlyContinue
        } catch {}
    }

    # Pronalazenje lokalnih i staged paketa drajvera
    $driverFolders = @(
        (Join-Path $LocalSetupDir "Drivers"),
        (Join-Path $LocalSetupDir "Programi\Drivers"),
        "C:\setup\Drivers",
        "C:\setup\Programi\Drivers"
    )
    foreach ($d in (Get-PSDrive -PSProvider FileSystem -ErrorAction SilentlyContinue)) {
        $cand1 = Join-Path "$($d.Root)" "Drivers"
        $cand2 = Join-Path "$($d.Root)" "sources\`$OEM`$\`$1\setup\Drivers"
        if ($driverFolders -notcontains $cand1) { $driverFolders += $cand1 }
        if ($driverFolders -notcontains $cand2) { $driverFolders += $cand2 }
    }

    # =========================================================================
    # 3. INTEL VMD / RST STORAGE DRAJVERI - P2.Vmd
    # =========================================================================
    Invoke-WisStep -Phase 2 -StepId "P2.Vmd" -Title "Intel VMD / RST skladišni drajveri" -Test {
        try {
            $storageDevs = Get-CimInstance Win32_PnPEntity -Filter "PNPClass = 'SCSIAdapter' or PNPClass = 'HDC'" -ErrorAction SilentlyContinue
            $hasVmd = $false
            $vmdOk = $false
            foreach ($sd in $storageDevs) {
                if ($sd.Name -match '(?i)vmd|rst|optane|raid' -or $sd.DeviceID -match 'VEN_8086&(DEV_9A0B|DEV_A77F|DEV_7D0B)') {
                    $hasVmd = $true
                    if ($sd.ConfigManagerErrorCode -eq 0) {
                        $vmdOk = $true
                    }
                }
            }
            if (-not $hasVmd) {
                return @{ Done = $true; Reason = "Nema Intel VMD/RST kontrolera na sistemu (standardni NVMe/AHCI)" }
            } elseif ($vmdOk) {
                return @{ Done = $true; Reason = "Intel VMD/RST kontroler radi ispravno (ConfigManagerErrorCode = 0)" }
            } else {
                return @{ Done = $false; Reason = "Intel VMD kontroler zahteva instalaciju drajvera" }
            }
        } catch {
            return @{ Done = $false; Reason = "Greška pri proveri VMD kontrolera: $($_.Exception.Message)" }
        }
    } -Action {
        if (Get-Command Send-IpcProgress -ErrorAction SilentlyContinue) {
            Send-IpcProgress -Progress 25 -Phase 2 -PhaseName "Drajveri" -SubPhase "Intel VMD / RST" -StatusMessage "Provera i instalacija Intel VMD/RST drajvera..."
        }
        if (Get-Command Wait-ServisUICooperativePause -ErrorAction SilentlyContinue) {
            Wait-ServisUICooperativePause -CheckpointName "Faza 2 - Intel VMD"
        }

        $vmdCandidates = @()
        foreach ($df in $driverFolders) {
            if (Test-Path $df) {
                try {
                    $vmdDirs = Get-ChildItem -Path $df -Directory -Recurse -ErrorAction SilentlyContinue | Where-Object {
                        $_.Name -match '(?i)vmd|rst|storage|f6flpy'
                    }
                    foreach ($vd in $vmdDirs) { $vmdCandidates += $vd.FullName }
                } catch {}
            }
        }

        if ($vmdCandidates.Count -gt 0) {
            if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                Write-ServisLog "Pronadjeni namenski Intel VMD/RST storage drajveri ($($vmdCandidates.Count) lokacija). Pokrecem pnputil..." -Level "INFO" -Component "Drivers"
            }
            foreach ($vPath in $vmdCandidates) {
                if ((Get-Command Test-ServisSkipRequested -ErrorAction SilentlyContinue) -and (Test-ServisSkipRequested)) {
                    if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                        Write-ServisLog "Preskocena instalacija VMD drajvera na zahtev servisera." -Level "WARN" -Component "Drivers"
                    }
                    break
                }
                try {
                    $vInfs = Get-ChildItem -Path $vPath -Filter "*.inf" -Recurse -File -ErrorAction SilentlyContinue
                    if ($vInfs -and $vInfs.Count -gt 0) {
                        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                            Write-ServisLog "Instaliram VMD drajvere iz: $vPath ($($vInfs.Count) .inf fajlova)" -Level "STEP" -Component "Drivers"
                        }
                        $null = & pnputil.exe /add-driver "$vPath\*.inf" /subdirs /install 2>&1
                    }
                } catch {}
            }
        }
    }

    # =========================================================================
    # 4. OPŠTA PNP INSTALACIJA PREOSTALIH DRAJVERA - P2.Pnp
    # =========================================================================
    Invoke-WisStep -Phase 2 -StepId "P2.Pnp" -Title "PnP instalacija lokalnih drajvera" -Test {
        Test-WisDeviceManagerClean
    } -Action {
        if (Get-Command Send-IpcProgress -ErrorAction SilentlyContinue) {
            Send-IpcProgress -Progress 40 -Phase 2 -PhaseName "Drajveri" -SubPhase "PnP Instalacija" -StatusMessage "Instalacija PnP drajvera preko pnputil.exe..."
        }
        if (Get-Command Wait-ServisUICooperativePause -ErrorAction SilentlyContinue) {
            Wait-ServisUICooperativePause -CheckpointName "Faza 2 - PnP Drajveri"
        }

        $pnpInstalledCount = 0
        foreach ($df in $driverFolders) {
            if ((Get-Command Test-ServisSkipRequested -ErrorAction SilentlyContinue) -and (Test-ServisSkipRequested)) {
                if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                    Write-ServisLog "Preskocena PnP instalacija na zahtev servisera." -Level "WARN" -Component "Drivers"
                }
                break
            }
            if (Test-Path $df) {
                try {
                    $allInfs = Get-ChildItem -Path $df -Filter "*.inf" -Recurse -File -ErrorAction SilentlyContinue
                    if ($allInfs -and $allInfs.Count -gt 0) {
                        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                            Write-ServisLog "Pronadjen paket drajvera u: $df ($($allInfs.Count) .inf datoteka). Pokrecem pnputil..." -Level "INFO" -Component "Drivers"
                        }
                        if (Get-Command Send-IpcProgress -ErrorAction SilentlyContinue) {
                            Send-IpcProgress -Progress 50 -Phase 2 -PhaseName "Drajveri" -SubPhase "PnP Instalacija" -StatusMessage "Instaliram drajvere iz $($df)..."
                        }
                        $null = & pnputil.exe /add-driver "$df\*.inf" /subdirs /install 2>&1
                        $pnpInstalledCount += $allInfs.Count
                    }
                } catch {}
            }
        }
        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
            Write-ServisLog "Lokalna PnP instalacija zavrsena (obradjeno ukupno $pnpInstalledCount drajver profila)." -Level "SUCCESS" -Component "Drivers"
        }
    }

    # =========================================================================
    # 5. DIJAGNOSTIKA I SERVISHUB DRAJVERI - P2.Diag (-NoSkip)
    # =========================================================================
    Invoke-WisStep -Phase 2 -StepId "P2.Diag" -Title "Dijagnostika preostalih uređaja" -NoSkip -Test {
        return @{ Done = $true; Reason = "Dijagnostičko skeniranje uređaja" }
    } -Action {
        if (Get-Command Send-IpcProgress -ErrorAction SilentlyContinue) {
            Send-IpcProgress -Progress 65 -Phase 2 -PhaseName "Drajveri" -SubPhase "Dijagnostika uredjaja" -StatusMessage "Skeniranje neprepoznatih PnP uređaja..."
        }
        $dmCheck = $null
        if (Get-Command Test-WisDeviceManagerClean -ErrorAction SilentlyContinue) {
            $dmCheck = Test-WisDeviceManagerClean
        }
        $problemDevices = if ($dmCheck -and $dmCheck.ProblemDevices) { @($dmCheck.ProblemDevices) } else { @() }

        if ($dmCheck -and -not $dmCheck.Clean) {
            if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                Write-ServisLog "Detektovano $($problemDevices.Count) problema sa drajverima ($($dmCheck.Reason))." -Level "WARN" -Component "Drivers"
            }
            if (-not $phaseCtx.SkipWU -and (Get-Command Resolve-MissingDriversFromHub -ErrorAction SilentlyContinue)) {
                $hubRes = Resolve-MissingDriversFromHub
                if ($hubRes -and $hubRes.AllResolved -and $hubRes.TotalProblematic -gt 0) {
                    $postDm = Test-WisDeviceManagerClean
                    if ($postDm -and $postDm.Clean) {
                        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                            Write-ServisLog "Svi problematični uređaji su uspešno rešeni preko ServisHub-a. Preskačem Windows Update pretragu." -Level "SUCCESS" -Component "Drivers"
                        }
                        $phaseCtx.SkipWU = $true
                    }
                }
            }
        } else {
            if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                Write-ServisLog "Svi PnP hardverski uredjaji su prepoznati i funkcionalni." -Level "SUCCESS" -Component "Drivers"
            }
            if (-not $phaseCtx.SkipWU) {
                $phaseCtx.SkipWU = $true
            }
        }
    }

    # =========================================================================
    # 6. DRAJVERI PREKO WINDOWS UPDATE-A - P2.WuDrivers
    # =========================================================================
    Invoke-WisStep -Phase 2 -StepId "P2.WuDrivers" -Title "Hardverski drajveri preko Windows Update-a" -Test {
        $dmRes = Test-WisDeviceManagerClean
        if (-not $dmRes.Clean) {
            return @{ Done = $false; Reason = "Device Manager nije čist: $($dmRes.Reason)" }
        }
        return @{ Done = $true; Reason = $dmRes.Reason }
    } -Action {
        if ($phaseCtx.SkipWU) {
            if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                Write-ServisLog "Windows Update drajveri preskočeni jer su svi uređaji već prepoznati ili rešeni preko ServisHub-a." -Level "INFO" -Component "WinUpdate"
            }
            return
        }

        if (Get-Command Send-IpcProgress -ErrorAction SilentlyContinue) {
            Send-IpcProgress -Progress 75 -Phase 2 -PhaseName "Drajveri" -SubPhase "Windows Update" -StatusMessage "Provera drajvera kroz Windows Update servis..."
        }
        if (Get-Command Wait-ServisUICooperativePause -ErrorAction SilentlyContinue) {
            Wait-ServisUICooperativePause -CheckpointName "Faza 2 - Windows Update Drajveri"
        }

        try {
            if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                Write-ServisLog "Pokrecem Microsoft.Update.Session pretragu za hardverske drajvere..." -Level "INFO" -Component "WinUpdate"
            }
            $uSession = New-Object -ComObject Microsoft.Update.Session
            $uSearcher = $uSession.CreateUpdateSearcher()
            $uSearcher.ServerSelection = 2
            $searchResult = $uSearcher.Search("IsInstalled=0 and Type='Driver'")
            $driverUpdates = $searchResult.Updates

            if ($driverUpdates -and $driverUpdates.Count -gt 0) {
                $totalDrv = $driverUpdates.Count
                if (Get-Command Send-IpcProgress -ErrorAction SilentlyContinue) {
                    Send-IpcProgress -Progress 80 -Phase 2 -PhaseName "Drajveri" -SubPhase "Windows Update" -StatusMessage "Preuzimanje i instalacija $totalDrv drajvera..."
                }

                $batchDlColl = New-Object -ComObject Microsoft.Update.UpdateColl
                for ($bIdx = 0; $bIdx -lt $totalDrv; $bIdx++) {
                    $bDrv = $driverUpdates.Item($bIdx)
                    if (-not $bDrv.EulaAccepted) {
                        try { $bDrv.AcceptEula() } catch {}
                    }
                    if (-not $bDrv.IsDownloaded) {
                        $batchDlColl.Add($bDrv) | Out-Null
                    }
                }
                if ($batchDlColl.Count -gt 0) {
                    try {
                        $batchDownloader = $uSession.CreateUpdateDownloader()
                        $batchDownloader.Updates = $batchDlColl
                        $batchDownloader.Download() | Out-Null
                    } catch {}
                }

                try {
                    if (-not (Test-Path "HKLM:\SOFTWARE\WIS")) { New-Item -Path "HKLM:\SOFTWARE\WIS" -Force -ErrorAction SilentlyContinue | Out-Null }
                    Set-ItemProperty -Path "HKLM:\SOFTWARE\WIS" -Name "ServisWuActive" -Value 1 -Type DWord -Force -ErrorAction SilentlyContinue
                } catch {}

                try {
                    for ($dIdx = 0; $dIdx -lt $totalDrv; $dIdx++) {
                        $drv = $driverUpdates.Item($dIdx)
                        $drvNum = $dIdx + 1

                        $drvPct = [math]::Min(94, 80 + [math]::Round(($dIdx / $totalDrv) * 14))
                        if (Get-Command Send-IpcProgress -ErrorAction SilentlyContinue) {
                            Send-IpcProgress -Progress $drvPct -Phase 2 -PhaseName "Drajveri" -SubPhase "Windows Update ($drvNum/$totalDrv)" -StatusMessage "Instaliram: $($drv.Title)"
                        }

                        try {
                            $singleColl = New-Object -ComObject Microsoft.Update.UpdateColl
                            $singleColl.Add($drv) | Out-Null

                            if (-not $drv.IsDownloaded) {
                                $downloader = $uSession.CreateUpdateDownloader()
                                $downloader.Updates = $singleColl
                                $downloader.Download() | Out-Null
                            }

                            $installer = $uSession.CreateUpdateInstaller()
                            $installer.Updates = $singleColl
                            $null = $installer.Install()
                        } catch {}
                    }
                } finally {
                    try {
                        Set-ItemProperty -Path "HKLM:\SOFTWARE\WIS" -Name "ServisWuActive" -Value 0 -Type DWord -Force -ErrorAction SilentlyContinue
                    } catch {}
                }
            }
        } catch {
            if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                Write-ServisLog "Windows Update pretraga drajvera nije uspela: $($_.Exception.Message)" -Level "WARN" -Component "WinUpdate"
            }
        }
    }

    # Samouceca berba hardverskih drajvera u <Model>.tar na TrueNAS serveru
    try {
        Export-ServisHardwareDrivers
    } catch {}

    # Slanje azuriranog hardverskog statusa ka UI-ju
    $hwData = [ordered]@{
        model  = $phaseCtx.ModelName
        serial = $phaseCtx.SerialNum
    }
    if (Get-Command Send-IpcStatus -ErrorAction SilentlyContinue) {
        Send-IpcStatus -Phase 2 -Progress 95 -SubPhase "Verifikacija" -StatusMessage "Hardverski drajveri usklađeni." -Hw $hwData
    }

    # Perzistencija stanja u deploy-state.json
    try {
        if (Get-Command Save-DeployStatePhase -ErrorAction SilentlyContinue) {
            Save-DeployStatePhase -Phase 3
        }
    } catch {}

    # Pokretanje pozadinskog Windows Update radnika odmah nakon Koraka 2
    try {
        Start-BackgroundWindowsUpdateWorker
    } catch {
        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
            Write-ServisLog "Upozorenje pri pokretanju pozadinskog Windows Update radnika nakon Faze 2: $($_.Exception.Message)" -Level "WARN" -Component "WinUpdate"
        }
    }

    if (Get-Command Send-IpcProgress -ErrorAction SilentlyContinue) {
        Send-IpcProgress -Progress 100 -Phase 2 -PhaseName "Drajveri" -SubPhase "Završeno" -StatusMessage "Faza 2 uspešno završena. Pozadinski Windows Update nastavlja rad u pozadini."
    }
    if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
        Write-ServisLog "=== FAZA 2 USPESNO ZAVRSENA: Drajveri su integrisani, pozadinski Windows Update pokrenut ===" -Level "SUCCESS" -Component "Phase2"
    }

    $global:LASTEXITCODE = 0
}


function Write-BackgroundWuStatus {
    [CmdletBinding()]
    param(
        [string]$Status = "IDLE",
        [string]$PhaseText = "",
        [string]$PhaseTextZh = "",
        [int]$FoundCount = 0,
        [int]$DownloadedCount = 0,
        [int]$InstalledCount = 0,
        [bool]$RebootRequired = $false,
        [int]$WorkerPid = 0
    )

    try {
        $setupDir = "C:\setup"
        if (-not (Test-Path $setupDir)) {
            New-Item -Path $setupDir -ItemType Directory -Force -ErrorAction SilentlyContinue | Out-Null
        }
        $statusFile = Join-Path $setupDir "wu-bg-status.json"
        $tempFile = Join-Path $setupDir "wu-bg-status.tmp"

        $effectivePid = if ($WorkerPid -gt 0) { $WorkerPid } else { $PID }
        $payload = [ordered]@{
            status           = $Status
            phase_text       = $PhaseText
            phase_text_zh    = $(if ($PhaseTextZh) { $PhaseTextZh } else { $PhaseText })
            found_count      = $FoundCount
            downloaded_count = $DownloadedCount
            installed_count  = $InstalledCount
            reboot_required  = [bool]$RebootRequired
            pid              = $effectivePid
            updated_at       = [datetime]::UtcNow.ToString("o")
        }

        $json = $payload | ConvertTo-Json -Depth 4 -Compress
        [System.IO.File]::WriteAllText($tempFile, $json, [System.Text.Encoding]::UTF8)
        Move-Item -Path $tempFile -Destination $statusFile -Force -ErrorAction SilentlyContinue
    } catch {}
}

function Start-BackgroundWindowsUpdateWorker {
    <#
    .SYNOPSIS
        Pokrece asinhroni pozadinski Windows Update proces odmah nakon Faze 2.
    .DESCRIPTION
        Proces radi u pozadini dok se izvrsavaju Faze 3 (Optimizacija), 4 (Programi) i 5 (Stres test).
        Vrsi Search i Download zakrpa/drajvera bez drzanja MSI mutexa, a samu instalaciju koordinisano
        izvodi kada MSI instalator nije zauzet, cime se eliminise cekanje na kraju u Fazi 6.
    #>
    [CmdletBinding()]
    param()

    $statusFile = "C:\setup\wu-bg-status.json"
    if (Test-Path $statusFile) {
        try {
            $raw = Get-Content -Path $statusFile -Raw -Encoding UTF8 -ErrorAction SilentlyContinue
            if ($raw) {
                $existing = ConvertFrom-Json -InputObject $raw -ErrorAction SilentlyContinue
                if ($existing) {
                    $exStatus = "$($existing.status)".ToUpperInvariant()
                    $exPid = if ($null -ne $existing.pid) { [int]$existing.pid } else { 0 }
                    if ($exStatus -in @("COMPLETED", "SKIPPED")) {
                        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                            Write-ServisLog "Start-BackgroundWindowsUpdateWorker: Pozadinski Windows Update je vec zavrsen ($exStatus)." -Level "INFO" -Component "BgWinUpdate"
                        }
                        return $true
                    }
                    if ($exPid -gt 0 -and $exStatus -in @("STARTING", "SEARCHING", "DOWNLOADING", "WAITING_MSI", "INSTALLING", "HARVESTING")) {
                        $alive = Get-Process -Id $exPid -ErrorAction SilentlyContinue
                        if ($alive) {
                            if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                                Write-ServisLog "Start-BackgroundWindowsUpdateWorker: Pozadinski Windows Update radnik je vec aktivan (PID: $exPid, Status: $exStatus)." -Level "INFO" -Component "BgWinUpdate"
                            }
                            return $true
                        }
                        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                            Write-ServisLog "Start-BackgroundWindowsUpdateWorker: Prethodni pozadinski radnik (PID: $exPid, Status: $exStatus) je prekinut (verovatno tokom restarta). Pokrecem novog radnika..." -Level "WARN" -Component "BgWinUpdate"
                        }
                    }
                }
            }
        } catch {}
    }

    try {
        if (Test-Path "C:\setup\wu-secondpass-done.flag") {
            Remove-Item -Path "C:\setup\wu-secondpass-done.flag" -Force -ErrorAction SilentlyContinue
        }
    } catch {}

    $modDir = $PSScriptRoot
    if (-not $modDir -or -not (Test-Path (Join-Path $modDir "WIS.Update.psm1"))) {
        $modDirCandidates = @(
            "C:\setup\Skripte\Modules",
            "C:\setup\Modules"
        )
        $modDir = $modDirCandidates | Where-Object { Test-Path (Join-Path $_ "WIS.Update.psm1") } | Select-Object -First 1
    }

    if (-not $modDir) {
        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
            Write-ServisLog "Start-BackgroundWindowsUpdateWorker: Direktorijum modula nije pronadjen." -Level "WARN" -Component "BgWinUpdate"
        }
        return $false
    }

    Write-BackgroundWuStatus -Status "STARTING" -PhaseText "Pokretanje pozadinskog Windows Update-a..."

    $workerCmd = @"
`$ErrorActionPreference = 'Continue'
try {
    Import-Module '$modDir\WIS.Core.psm1' -Force -DisableNameChecking -ErrorAction SilentlyContinue
    Import-Module '$modDir\WIS.Apps.psm1' -Force -DisableNameChecking -ErrorAction SilentlyContinue
    Import-Module '$modDir\WIS.Hardware.psm1' -Force -DisableNameChecking -ErrorAction SilentlyContinue
    Import-Module '$modDir\WIS.Update.psm1' -Force -DisableNameChecking -ErrorAction SilentlyContinue
    Invoke-BackgroundWindowsUpdateWorker
} catch {
    Write-BackgroundWuStatus -Status 'WARN' -PhaseText ('Upozorenje u pozadinskom WU: ' + `$_.Exception.Message)
}
"@

    $encodedCmd = [Convert]::ToBase64String([System.Text.Encoding]::Unicode.GetBytes($workerCmd))
    try {
        $bgProc = Start-Process -FilePath "powershell.exe" -ArgumentList @("-NoProfile", "-ExecutionPolicy", "Bypass", "-WindowStyle", "Hidden", "-EncodedCommand", $encodedCmd) -WindowStyle Hidden -PassThru -ErrorAction Stop
        if ($bgProc) {
            Write-BackgroundWuStatus -Status "STARTING" -PhaseText "Pozadinski Windows Update pokrenut (pretraga kataloga)..." -WorkerPid $bgProc.Id
            if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                Write-ServisLog "Pokrenut pozadinski Windows Update radnik nakon Faze 2 (PID: $($bgProc.Id))." -Level "SUCCESS" -Component "BgWinUpdate"
            }
            return $true
        }
    } catch {
        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
            Write-ServisLog "Greska pri pokretanju pozadinskog Windows Update procesa: $($_.Exception.Message)" -Level "WARN" -Component "BgWinUpdate"
        }
    }
    return $false
}

function Invoke-BackgroundWindowsUpdateWorker {
    <#
    .SYNOPSIS
        Izvrsava pozadinski Windows Update (Search -> Download -> MSI-safe Install) paralelno sa Fazama 3, 4 i 5.
    #>
    [CmdletBinding()]
    param()

    Write-BackgroundWuStatus -Status "SEARCHING" -PhaseText "Pozadinski WU: Pretraga kataloga..." -WorkerPid $PID
    if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
        Write-ServisLog "Invoke-BackgroundWindowsUpdateWorker: Zapocinjem pozadinsku pretragu Windows Update kataloga (PID: $PID)..." -Level "STEP" -Component "BgWinUpdate"
    }

    $hasNet = $false
    try {
        if (Get-Command Test-Internet -ErrorAction SilentlyContinue) {
            $hasNet = Test-Internet
        } else {
            $hasNet = [bool](Test-Connection -ComputerName "8.8.8.8" -Count 1 -Quiet -ErrorAction SilentlyContinue)
        }
    } catch {
        $hasNet = $false
    }

    if (-not $hasNet) {
        Write-BackgroundWuStatus -Status "SKIPPED" -PhaseText "Pozadinski WU: Preskoceno (nema internet veze)" -WorkerPid $PID
        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
            Write-ServisLog "Invoke-BackgroundWindowsUpdateWorker: Nema internet konekcije, preskacem pozadinski Windows Update." -Level "WARN" -Component "BgWinUpdate"
        }
        return
    }

    try {
        Start-Process "usoclient.exe" -ArgumentList "StartInteractiveScan" -WindowStyle Hidden -ErrorAction SilentlyContinue
    } catch {}
    Start-Sleep -Seconds 2

    # Registracija Microsoft Update servisa (obuhvata Office, .NET, drajvere i sistemske zakrpe)
    $muServiceId = "7971f918-a847-4430-9279-4a52d1efe18d"
    $isMuRegistered = $false
    try {
        $svcMgr = New-Object -ComObject Microsoft.Update.ServiceManager
        $svcMgr.ClientApplicationID = "WIS Servis Toolkit"
        foreach ($svc in $svcMgr.Services) {
            if ($svc.ServiceID -eq $muServiceId) { $isMuRegistered = $true; break }
        }
        if (-not $isMuRegistered) {
            $svcMgr.AddService2($muServiceId, 7, "") | Out-Null
            $isMuRegistered = $true
        }
    } catch { $isMuRegistered = $false }

    $wuMode = Get-WUConfigMode
    $totalFoundAll = 0
    $totalDownloadedAll = 0
    $totalInstalledAll = 0
    $rebootReq = $false
    $harvestUpdates = @()

    for ($bgCycle = 1; $bgCycle -le 2; $bgCycle++) {
        if ($bgCycle -gt 1) {
            Write-BackgroundWuStatus -Status "SEARCHING" -PhaseText "Pozadinski WU: Dodatna provera kataloga (krug $bgCycle/2)..." -FoundCount $totalFoundAll -DownloadedCount $totalDownloadedAll -InstalledCount $totalInstalledAll -RebootRequired $rebootReq -WorkerPid $PID
            try { Start-Process "usoclient.exe" -ArgumentList "StartInteractiveScan" -WindowStyle Hidden -ErrorAction SilentlyContinue } catch {}
            Start-Sleep -Seconds 3
        }

        $uSession = $null
        $searchResult = $null
        try {
            $uSession = New-Object -ComObject Microsoft.Update.Session
            $uSearcher = $uSession.CreateUpdateSearcher()
            $uSearcher.Online = $true
            if ($isMuRegistered) {
                $uSearcher.ServerSelection = 3
                $uSearcher.ServiceID = $muServiceId
            } else {
                $uSearcher.ServerSelection = 2
            }
            $searchResult = $uSearcher.Search("IsInstalled=0 and IsHidden=0")
        } catch {
            try {
                $uSearcher.ServerSelection = 2
                $searchResult = $uSearcher.Search("IsInstalled=0 and IsHidden=0")
            } catch {
                try {
                    $uSearcher.ServerSelection = 0
                    $searchResult = $uSearcher.Search("IsInstalled=0 and IsHidden=0")
                } catch {
                    $errStr = $_.Exception.Message
                    if ($bgCycle -eq 1 -and $totalInstalledAll -eq 0) {
                        Write-BackgroundWuStatus -Status "WARN" -PhaseText "Pozadinski WU: Katalog trenutno nedostupan" -WorkerPid $PID
                        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                            Write-ServisLog "Invoke-BackgroundWindowsUpdateWorker: Pretraga kataloga nije uspela ($errStr)." -Level "WARN" -Component "BgWinUpdate"
                        }
                        return
                    }
                    break
                }
            }
        }

        $rawCount = if ($searchResult -and $searchResult.Updates) { $searchResult.Updates.Count } else { 0 }
        if ($rawCount -eq 0) {
            if ($bgCycle -eq 1 -and $totalInstalledAll -eq 0) {
                Write-BackgroundWuStatus -Status "COMPLETED" -PhaseText "Pozadinski WU: Sistem je azuran (0 zakrpa)" -FoundCount 0 -DownloadedCount 0 -InstalledCount 0 -RebootRequired $rebootReq -WorkerPid $PID
                if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                    Write-ServisLog "Invoke-BackgroundWindowsUpdateWorker: Sistem je u potpunosti azuran (0 zaostalih zakrpa)." -Level "SUCCESS" -Component "BgWinUpdate"
                }
                return
            }
            break
        }

        $filteredUpdates = @()
        for ($i = 0; $i -lt $rawCount; $i++) {
            $u = $searchResult.Updates.Item($i)
            $isDrv = Test-IsDriverUpdate -UpdateItem $u
            $isCum = Test-IsCumulativeUpdate -UpdateItem $u

            $includeItem = $false
            if ($wuMode -eq "DriversOnly") {
                if ($isDrv -and (-not $isCum)) { $includeItem = $true }
            } elseif ($wuMode -eq "SkipCumulative") {
                if (-not $isCum) { $includeItem = $true }
            } else {
                $includeItem = $true
            }

            if ($includeItem) {
                if (-not $u.EulaAccepted) {
                    try { $u.AcceptEula() } catch {}
                }
                $filteredUpdates += $u
            }
        }

        $cycleCount = $filteredUpdates.Count
        if ($cycleCount -eq 0) {
            if ($bgCycle -eq 1 -and $totalInstalledAll -eq 0) {
                Write-BackgroundWuStatus -Status "COMPLETED" -PhaseText "Pozadinski WU: Azurno za rezim $wuMode (0 zakrpa)" -FoundCount 0 -DownloadedCount 0 -InstalledCount 0 -RebootRequired $rebootReq -WorkerPid $PID
                if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                    Write-ServisLog "Invoke-BackgroundWindowsUpdateWorker: Nakon filtera ($wuMode) nema dodatnih zakrpa (0)." -Level "SUCCESS" -Component "BgWinUpdate"
                }
                return
            }
            break
        }

        $totalFoundAll += $cycleCount
        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
            Write-ServisLog "Invoke-BackgroundWindowsUpdateWorker (krug $bgCycle): Pronadjeno $cycleCount stavki (rezim: $wuMode). Zapocinjem pozadinsko preuzimanje..." -Level "INFO" -Component "BgWinUpdate"
        }

        # FAZA A: Pozadinsko preuzimanje (Download ne drzi MSI mutex - radi 100% paralelno sa Fazama 3 i 4)
        for ($idx = 0; $idx -lt $cycleCount; $idx++) {
            $item = $filteredUpdates[$idx]
            $num = $idx + 1
            Write-BackgroundWuStatus -Status "DOWNLOADING" -PhaseText "Pozadinski WU: Preuzimanje ($($totalDownloadedAll + 1)/$totalFoundAll)..." -FoundCount $totalFoundAll -DownloadedCount $totalDownloadedAll -InstalledCount $totalInstalledAll -RebootRequired $rebootReq -WorkerPid $PID

            if (-not $item.IsDownloaded) {
                try {
                    $dlColl = New-Object -ComObject Microsoft.Update.UpdateColl
                    $dlColl.Add($item) | Out-Null
                    $downloader = $uSession.CreateUpdateDownloader()
                    $downloader.Updates = $dlColl
                    $downloader.Download() | Out-Null
                } catch {
                    if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                        Write-ServisLog "Invoke-BackgroundWindowsUpdateWorker: Upozorenje pri preuzimanju '$($item.Title)': $($_.Exception.Message)" -Level "WARN" -Component "BgWinUpdate"
                    }
                }
            }
            $totalDownloadedAll++
            Write-BackgroundWuStatus -Status "DOWNLOADING" -PhaseText "Pozadinski WU: Preuzeto ($totalDownloadedAll/$totalFoundAll)" -FoundCount $totalFoundAll -DownloadedCount $totalDownloadedAll -InstalledCount $totalInstalledAll -RebootRequired $rebootReq -WorkerPid $PID
        }

        # FAZA B: Koordinisana instalacija (ceka slobodan MSI instalator da ne izazove Error 1618 u Fazi 4)
        $cycleInstalledOk = 0
        for ($idx = 0; $idx -lt $cycleCount; $idx++) {
            $item = $filteredUpdates[$idx]

            $waitCycles = 0
            while ($waitCycles -lt 60) {
                $msiBusy = $false
                try {
                    $activeAppInstallers = Get-Process -Name "msiexec", "AcroPro*", "ChromeSetup*", "windowsdesktop-runtime*", "aspnetcore-runtime*", "VC_redist*" -ErrorAction SilentlyContinue
                    if ($activeAppInstallers -and @($activeAppInstallers).Count -gt 1) {
                        $msiBusy = $true
                    }
                } catch {}

                if (-not $msiBusy) {
                    try {
                        $mutex = $null
                        if ([System.Threading.Mutex]::TryOpenExisting("Global\_MSIExecute", [ref]$mutex)) {
                            $msiBusy = $true
                            $mutex.Close()
                        }
                    } catch {}
                }

                if (-not $msiBusy) { break }
                Write-BackgroundWuStatus -Status "WAITING_MSI" -PhaseText "Pozadinski WU: Preuzeto ($totalDownloadedAll/$totalFoundAll) - ceka MSI instalator..." -FoundCount $totalFoundAll -DownloadedCount $totalDownloadedAll -InstalledCount $totalInstalledAll -RebootRequired $rebootReq -WorkerPid $PID
                Start-Sleep -Seconds 3
                $waitCycles++
            }

            Write-BackgroundWuStatus -Status "INSTALLING" -PhaseText "Pozadinski WU: Instalacija ($($totalInstalledAll + 1)/$totalFoundAll)..." -FoundCount $totalFoundAll -DownloadedCount $totalDownloadedAll -InstalledCount $totalInstalledAll -RebootRequired $rebootReq -WorkerPid $PID

            try {
                if (-not (Test-Path "HKLM:\SOFTWARE\WIS")) {
                    New-Item -Path "HKLM:\SOFTWARE\WIS" -Force -ErrorAction SilentlyContinue | Out-Null
                }
                Set-ItemProperty -Path "HKLM:\SOFTWARE\WIS" -Name "ServisWuActive" -Value 1 -Type DWord -Force -ErrorAction SilentlyContinue

                $instColl = New-Object -ComObject Microsoft.Update.UpdateColl
                $instColl.Add($item) | Out-Null
                $installer = $uSession.CreateUpdateInstaller()
                $installer.Updates = $instColl
                $installer.ForceQuiet = $true
                $instRes = $installer.Install()

                if ($instRes) {
                    if ($instRes.RebootRequired) { $rebootReq = $true }
                    if ($instRes.ResultCode -in @(2, 3)) {
                        $cycleInstalledOk++
                        $totalInstalledAll++
                    }
                }
            } catch {
                if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                    Write-ServisLog "Invoke-BackgroundWindowsUpdateWorker: Upozorenje pri instalaciji '$($item.Title)': $($_.Exception.Message)" -Level "WARN" -Component "BgWinUpdate"
                }
            } finally {
                Set-ItemProperty -Path "HKLM:\SOFTWARE\WIS" -Name "ServisWuActive" -Value 0 -Type DWord -Force -ErrorAction SilentlyContinue
            }

            Write-BackgroundWuStatus -Status "INSTALLING" -PhaseText "Pozadinski WU: Instalirano ($totalInstalledAll/$totalFoundAll)" -FoundCount $totalFoundAll -DownloadedCount $totalDownloadedAll -InstalledCount $totalInstalledAll -RebootRequired $rebootReq -WorkerPid $PID
        }

        $harvestUpdates += $filteredUpdates
        if ($cycleInstalledOk -eq 0) {
            break
        }
    }

    if ($harvestUpdates -and $harvestUpdates.Count -gt 0) {
        Write-BackgroundWuStatus -Status "HARVESTING" -PhaseText "Pozadinski WU: Arhiviranje novih zakrpa na NAS..." -FoundCount $totalFoundAll -DownloadedCount $totalDownloadedAll -InstalledCount $totalInstalledAll -RebootRequired $rebootReq -WorkerPid $PID
        try {
            Export-ServisWindowsUpdates -Updates $harvestUpdates
        } catch {}
    }

    Write-BackgroundWuStatus -Status "COMPLETED" -PhaseText "Pozadinski WU: Zavrseno ($totalInstalledAll/$totalFoundAll instalirano)" -FoundCount $totalFoundAll -DownloadedCount $totalDownloadedAll -InstalledCount $totalInstalledAll -RebootRequired $rebootReq -WorkerPid $PID
    if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
        Write-ServisLog "Invoke-BackgroundWindowsUpdateWorker: Pozadinski Windows Update uspesno zavrsen (Instalirano: $totalInstalledAll/$totalFoundAll, RebootRequired: $rebootReq)." -Level "SUCCESS" -Component "BgWinUpdate"
    }
}


function Invoke-WindowsUpdateSecondPass {
    <#
    .SYNOPSIS
        Proverava status pozadinskog Windows Update radnika (pokrenutog nakon Faze 2) ili vrsi dopunsku proveru.
    .DESCRIPTION
        U novom redizajniranom toku, pozadinski Windows Update krece odmah nakon Faze 2 i radi tokom Faza 3, 4 i 5.
        Kada proces stigne u Fazu 6, ova funkcija proverava C:\setup\wu-bg-status.json:
        - Ako je pozadinski WU vec zavrsen (COMPLETED / SKIPPED / WARN), odmah vraca rezultat bez ponovnog skeniranja.
        - Ako pozadinski WU upravo privodi kraju instalaciju, saceka njegovo kompletiranje (do 90s) bez pokretanja novog ciklusa.
    .COMPATIBILITY
        Windows PowerShell 5.1+
    #>
    [CmdletBinding()]
    param(
        [int]$TimeoutSeconds = 300
    )

    $bgStatusFile = "C:\setup\wu-bg-status.json"
    $bgPreInstalled = 0
    $bgPreFound = 0
    $bgPreReboot = $false
    $bgNeedFinalVerify = $true

    if (Test-Path $bgStatusFile) {
        $waitSw = [System.Diagnostics.Stopwatch]::StartNew()
        while ($waitSw.Elapsed.TotalSeconds -lt 180) {
            if ((Get-Command Test-ServisSkipRequested -ErrorAction SilentlyContinue) -and (Test-ServisSkipRequested)) {
                if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                    Write-ServisLog "Invoke-WindowsUpdateSecondPass: Operater zatrazio preskakanje cekanja na pozadinski WU." -Level "WARN" -Component "Update"
                }
                $bgNeedFinalVerify = $false
                break
            }

            try {
                $rawBg = Get-Content -Path $bgStatusFile -Raw -Encoding UTF8 -ErrorAction SilentlyContinue
                if ($rawBg) {
                    $bgObj = ConvertFrom-Json -InputObject $rawBg -ErrorAction SilentlyContinue
                    if ($bgObj) {
                        $st = "$($bgObj.status)".ToUpperInvariant()
                        $bgPreInstalled = if ($null -ne $bgObj.installed_count) { [int]$bgObj.installed_count } else { 0 }
                        $bgPreFound = if ($null -ne $bgObj.found_count) { [int]$bgObj.found_count } else { 0 }
                        $bgPreReboot = [bool]$bgObj.reboot_required
                        $pText = "$($bgObj.phase_text)"

                        if ($st -in @("COMPLETED", "SKIPPED", "WARN")) {
                            if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                                Write-ServisLog "Invoke-WindowsUpdateSecondPass: Pozadinski Windows Update prijavio status '$st' ($bgPreInstalled/$bgPreFound). Pokrecem zavrsnu WUA verifikaciju zaostalih ili naknadno otkljucanih azuriranja (Office/.NET/drajveri)..." -Level "INFO" -Component "Update"
                            }
                            break
                        }

                        # Proveri da li je pozadinski proces jos uvek aktivan
                        $bgPid = if ($null -ne $bgObj.pid) { [int]$bgObj.pid } else { 0 }
                        $procAlive = if ($bgPid -gt 0) { [bool](Get-Process -Id $bgPid -ErrorAction SilentlyContinue) } else { $false }
                        if (-not $procAlive) {
                            # Proces je prekinut (npr. tokom restarta u Fazi 3) pre nego sto je zavrsio - NIKAKO ne upisivati lazni COMPLETED!
                            if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                                Write-ServisLog "Invoke-WindowsUpdateSecondPass: Pozadinski Windows Update radnik (PID: $bgPid, status: $st, instalirano: $bgPreInstalled/$bgPreFound) je prekinut pre zavrsetka. Prelazim na direktno WUA skeniranje i instalaciju u Fazi 6." -Level "WARN" -Component "Update"
                            }
                            break
                        }

                        $el = [int]$waitSw.Elapsed.TotalSeconds
                        if (Get-Command Send-IpcProgress -ErrorAction SilentlyContinue) {
                            Send-IpcProgress -Progress 9 -Phase 6 -PhaseName "Zavrsetak" -SubPhase "Pozadinski WU (${el}s)" -StatusMessage "$pText"
                        }
                    }
                }
            } catch {}

            Start-Sleep -Seconds 2
        }

        if (-not $bgNeedFinalVerify) {
            return @{
                Success        = $true
                InstalledCount = $bgPreInstalled
                RebootRequired = $bgPreReboot
                Message        = "Preskoceno na zahtev operatera."
            }
        }
    }

    if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
        Write-ServisLog "=== POKRECEM ZAVRSNU PROVERU I DRUGI PROLAZ (DUAL-PASS) WINDOWS UPDATE-A ===" -Level "STEP" -Component "Update"
    }
    if (Get-Command Send-IpcProgress -ErrorAction SilentlyContinue) {
        Send-IpcProgress -Progress 8 -Phase 6 -PhaseName "Zavrsetak" -SubPhase "Dual-Pass Update" -StatusMessage "Skeniranje zaostalih kumulativnih zakrpi, Office i driver azuriranja..."
    }

    # Provera internet konekcije
    $hasNet = $false
    try {
        if (Get-Command Test-Internet -ErrorAction SilentlyContinue) {
            $hasNet = Test-Internet
        } else {
            $pingTest = Test-Connection -ComputerName "8.8.8.8" -Count 1 -Quiet -ErrorAction SilentlyContinue
            $hasNet = [bool]$pingTest
        }
    } catch {
        $hasNet = $false
    }

    if (-not $hasNet) {
        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
            Write-ServisLog "Invoke-WindowsUpdateSecondPass: Nema internet konekcije. Preskacem drugi prolaz Windows Update-a." -Level "WARN" -Component "Update"
        }
        return @{
            Success        = $true
            InstalledCount = $bgPreInstalled
            RebootRequired = $bgPreReboot
            Message        = "Preskoceno - nema internet konekcije."
        }
    }

    # 1. Osvezavanje Windows Update servisa i registracija Microsoft Update kataloga
    try {
        Start-Process "usoclient.exe" -ArgumentList "StartInteractiveScan" -WindowStyle Hidden -ErrorAction SilentlyContinue
    } catch {}
    Start-Sleep -Seconds 3

    $muServiceId = "7971f918-a847-4430-9279-4a52d1efe18d"
    $isMuRegistered = $false
    try {
        $svcMgr = New-Object -ComObject Microsoft.Update.ServiceManager
        $svcMgr.ClientApplicationID = "WIS Servis Toolkit"
        foreach ($svc in $svcMgr.Services) {
            if ($svc.ServiceID -eq $muServiceId) { $isMuRegistered = $true; break }
        }
        if (-not $isMuRegistered) {
            $svcMgr.AddService2($muServiceId, 7, "") | Out-Null
            $isMuRegistered = $true
        }
    } catch { $isMuRegistered = $false }

    # 2. Inicijalizacija COM objekta Microsoft.Update.Session
    $UpdateSession = $null
    $SearchResult = $null
    try {
        $UpdateSession = New-Object -ComObject Microsoft.Update.Session
        $UpdateSearcher = $UpdateSession.CreateUpdateSearcher()
        $UpdateSearcher.Online = $true
        if ($isMuRegistered) {
            $UpdateSearcher.ServerSelection = 3
            $UpdateSearcher.ServiceID = $muServiceId
        } else {
            $UpdateSearcher.ServerSelection = 2
        }
        $SearchResult = $UpdateSearcher.Search("IsInstalled=0 and IsHidden=0")
    } catch {
        try {
            $UpdateSearcher.ServerSelection = 2
            $SearchResult = $UpdateSearcher.Search("IsInstalled=0 and IsHidden=0")
        } catch {
            try {
                $UpdateSearcher.ServerSelection = 0
                $SearchResult = $UpdateSearcher.Search("IsInstalled=0 and IsHidden=0")
            } catch {
                $searchErr = $_.Exception.Message
                if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                    Write-ServisLog "Invoke-WindowsUpdateSecondPass: Greska pri pretrazi kataloga: $searchErr" -Level "WARN" -Component "Update"
                }
                return @{
                    Success        = $false
                    InstalledCount = $bgPreInstalled
                    RebootRequired = $bgPreReboot
                    Message        = "Greska pri pretrazi: $searchErr"
                }
            }
        }
    }

    $rawCount = if ($SearchResult -and $SearchResult.Updates) { $SearchResult.Updates.Count } else { 0 }
    if ($rawCount -eq 0) {
        $cleanMsg = "Sistem je u potpunosti azuriran (0 zaostalih zakrpi)."
        $totInst = $bgPreInstalled
        Write-BackgroundWuStatus -Status "COMPLETED" -PhaseText "Windows Update: Sistem azuran ($totInst instalirano)" -FoundCount $bgPreFound -DownloadedCount $bgPreFound -InstalledCount $totInst -RebootRequired $bgPreReboot
        try { Set-Content -Path "C:\setup\wu-secondpass-done.flag" -Value "DONE" -Encoding ASCII -Force -ErrorAction SilentlyContinue } catch {}
        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
            Write-ServisLog "Invoke-WindowsUpdateSecondPass: $cleanMsg" -Level "SUCCESS" -Component "Update"
        }
        if (Get-Command Send-IpcLog -ErrorAction SilentlyContinue) {
            Send-IpcLog -Message $cleanMsg -Level "SUCCESS" -Tag "[UPDATE]" -Phase 6
        }
        return @{
            Success        = $true
            InstalledCount = $totInst
            RebootRequired = $bgPreReboot
            Message        = $cleanMsg
        }
    }

    # Filtriranje prema konfiguraciji (DriversOnly, SkipCumulative, Full) i prihvatanje EULA licenci
    $wuMode = Get-WUConfigMode
    if ($wuMode -ne "DriversOnly") {
        try { Invoke-NasUpdatePrePatch } catch {}
    }

    $UpdatesToInstall = New-Object -ComObject Microsoft.Update.UpdateColl
    $updateTitles = @()
    for ($i = 0; $i -lt $rawCount; $i++) {
        $u = $SearchResult.Updates.Item($i)
        $isDrv = Test-IsDriverUpdate -UpdateItem $u
        $isCum = Test-IsCumulativeUpdate -UpdateItem $u

        $includeItem = $false
        if ($wuMode -eq "DriversOnly") {
            if ($isDrv -and (-not $isCum)) { $includeItem = $true }
        } elseif ($wuMode -eq "SkipCumulative") {
            if (-not $isCum) { $includeItem = $true }
        } else {
            $includeItem = $true
        }

        if (-not $includeItem) {
            if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                Write-ServisLog "  [PRESKOCENO - $wuMode] $($u.Title)" -Level "DEBUG" -Component "Update"
            }
            continue
        }

        if (-not $u.EulaAccepted) {
            try { $u.AcceptEula() } catch {}
        }
        $UpdatesToInstall.Add($u) | Out-Null
        $updateTitles += $u.Title
        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
            Write-ServisLog "  [ZAOSTALA ZAKRPA] $($u.Title)" -Level "INFO" -Component "Update"
        }
    }

    $count = $UpdatesToInstall.Count
    if ($count -eq 0) {
        $filteredCleanMsg = "Nakon primene filtera ($wuMode) nema preostalih zakrpa za drugi prolaz. Sistem je spreman za zavrsetak."
        Write-BackgroundWuStatus -Status "COMPLETED" -PhaseText "Windows Update: Zavrseno ($bgPreInstalled instalirano)" -FoundCount $bgPreFound -DownloadedCount $bgPreFound -InstalledCount $bgPreInstalled -RebootRequired $bgPreReboot
        try { Set-Content -Path "C:\setup\wu-secondpass-done.flag" -Value "DONE" -Encoding ASCII -Force -ErrorAction SilentlyContinue } catch {}
        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
            Write-ServisLog "Invoke-WindowsUpdateSecondPass: $filteredCleanMsg" -Level "SUCCESS" -Component "Update"
        }
        if (Get-Command Send-IpcLog -ErrorAction SilentlyContinue) {
            Send-IpcLog -Message $filteredCleanMsg -Level "SUCCESS" -Tag "[UPDATE]" -Phase 6
        }
        return @{
            Success        = $true
            InstalledCount = $bgPreInstalled
            RebootRequired = $bgPreReboot
            Message        = $filteredCleanMsg
        }
    }

    if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
        Write-ServisLog "Invoke-WindowsUpdateSecondPass: Detektovano $count zaostalih azuriranja (Rezim: $wuMode). Zapocinjem preuzimanje i instalaciju (Zaštitni Timeout: ${TimeoutSeconds}s)..." -Level "INFO" -Component "Update"
    }
    Write-BackgroundWuStatus -Status "INSTALLING" -PhaseText "Zavrsni WU: Instalacija $count azuriranja..." -FoundCount ($bgPreFound + $count) -DownloadedCount $bgPreFound -InstalledCount $bgPreInstalled -RebootRequired $bgPreReboot

    # 3. Kontrolisano preuzimanje i instalacija zaostalih zakrpi uz zaštitni timeout
    $rebootReq = $bgPreReboot
    $installedOk = 0
    $timedOut = $false
    $swPass = [System.Diagnostics.Stopwatch]::StartNew()

    $job = $null
    try {
        $job = Start-Job -ScriptBlock {
            param([string[]]$titles, [string]$muSvcId, [bool]$useMu)
            try {
                $session = New-Object -ComObject Microsoft.Update.Session
                $searcher = $session.CreateUpdateSearcher()
                $searcher.Online = $true
                if ($useMu -and $muSvcId) {
                    $searcher.ServerSelection = 3
                    $searcher.ServiceID = $muSvcId
                } else {
                    $searcher.ServerSelection = 2
                }
                try {
                    $res = $searcher.Search("IsInstalled=0 and IsHidden=0")
                } catch {
                    $searcher.ServerSelection = 0
                    $res = $searcher.Search("IsInstalled=0 and IsHidden=0")
                }
                if (-not $res -or -not $res.Updates -or $res.Updates.Count -eq 0) {
                    return @{ Success = $true; Installed = 0; Reboot = $false }
                }
                $coll = New-Object -ComObject Microsoft.Update.UpdateColl
                for ($j = 0; $j -lt $res.Updates.Count; $j++) {
                    $upItem = $res.Updates.Item($j)
                    if ($titles -and ($titles -contains $upItem.Title)) {
                        if (-not $upItem.EulaAccepted) { try { $upItem.AcceptEula() } catch {} }
                        $coll.Add($upItem) | Out-Null
                    }
                }
                if ($coll.Count -eq 0) {
                    return @{ Success = $true; Installed = 0; Reboot = $false }
                }
                $dl = $session.CreateUpdateDownloader()
                $dl.Updates = $coll
                $dl.Download() | Out-Null

                $inst = $session.CreateUpdateInstaller()
                $inst.Updates = $coll
                $inst.ForceQuiet = $true
                $instRes = $inst.Install()

                return @{
                    Success   = $true
                    Installed = $coll.Count
                    Reboot    = [bool]($instRes -and $instRes.RebootRequired)
                    Code      = if ($instRes) { $instRes.ResultCode } else { 0 }
                }
            } catch {
                return @{
                    Success = $false
                    Error   = $_.Exception.Message
                }
            }
        } -ArgumentList (,$updateTitles), $muServiceId, $isMuRegistered
    } catch {
        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
            Write-ServisLog "Upozorenje: Nije moguće pokrenuti Start-Job za WU drugi prolaz ($($_.Exception.Message)), prelazim na direktno izvršavanje." -Level "WARN" -Component "Update"
        }
    }

    if ($job) {
        # Kontrolisano praćenje posla sa zaštitnim timeout-om
        while ($job.State -eq 'Running') {
            $elapsedSec = [int]$swPass.Elapsed.TotalSeconds
            if ($elapsedSec -ge $TimeoutSeconds) {
                $timedOut = $true
                if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                    Write-ServisLog "Invoke-WindowsUpdateSecondPass: Zaštitni timeout (${TimeoutSeconds}s) dostignut tokom obrade zaostalih zakrpi! Prekidam posao bez blokiranja servisa." -Level "WARN" -Component "Update"
                }
                try {
                    Stop-Job $job -ErrorAction SilentlyContinue
                    Remove-Job $job -Force -ErrorAction SilentlyContinue
                } catch {}
                break
            }

            # Provera zahteva operatera za preskakanjem
            if ((Get-Command Test-ServisSkipRequested -ErrorAction SilentlyContinue) -and (Test-ServisSkipRequested)) {
                if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                    Write-ServisLog "Invoke-WindowsUpdateSecondPass: Operater je zatražio preskakanje drugog prolaza." -Level "WARN" -Component "Update"
                }
                try {
                    Stop-Job $job -ErrorAction SilentlyContinue
                    Remove-Job $job -Force -ErrorAction SilentlyContinue
                } catch {}
                break
            }

            if (Get-Command Send-IpcProgress -ErrorAction SilentlyContinue) {
                Send-IpcProgress -Progress 9 -Phase 6 -PhaseName "Završetak" -SubPhase "Dual-Pass Update (${elapsedSec}s/${TimeoutSeconds}s)" -StatusMessage "Preuzimanje i instalacija $count Windows Update zakrpi (${elapsedSec}s)..."
            }
            Start-Sleep -Seconds 2
        }

        if (-not $timedOut -and $job.State -eq 'Completed') {
            try {
                $jobResult = Receive-Job $job -ErrorAction SilentlyContinue
                Remove-Job $job -Force -ErrorAction SilentlyContinue
                if ($jobResult -and $jobResult.Success) {
                    $installedOk = $jobResult.Installed
                    if ($jobResult.Reboot) { $rebootReq = $true }
                } elseif ($jobResult -and $jobResult.Error) {
                    if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                        Write-ServisLog "Invoke-WindowsUpdateSecondPass: Upozorenje iz posla zaostalih zakrpi: $($jobResult.Error)" -Level "WARN" -Component "Update"
                    }
                }
            } catch {}
        }
    } else {
        # Fallback na direktno izvršavanje
        try {
            $Downloader = $UpdateSession.CreateUpdateDownloader()
            $Downloader.Updates = $UpdatesToInstall
            $Downloader.Download() | Out-Null

            $Installer = $UpdateSession.CreateUpdateInstaller()
            $Installer.Updates = $UpdatesToInstall
            $Installer.ForceQuiet = $true
            $InstallResult = $Installer.Install()

            if ($InstallResult) {
                if ($InstallResult.RebootRequired) { $rebootReq = $true }
                $resCode = $InstallResult.ResultCode
                if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                    Write-ServisLog "Invoke-WindowsUpdateSecondPass: Instalacija zavrsena (ResultCode: $resCode, RebootRequired: $rebootReq)." -Level "SUCCESS" -Component "Update"
                }
                $installedOk = $count
            }
        } catch {
            $instErr = $_.Exception.Message
            if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                Write-ServisLog "Invoke-WindowsUpdateSecondPass: Greska pri instalaciji zaostalih zakrpi: $instErr" -Level "WARN" -Component "Update"
            }
        }
    }

    if ($installedOk -gt 0 -or (-not $timedOut -and $count -gt 0)) {
        try {
            Export-ServisWindowsUpdates -Updates $UpdatesToInstall
        } catch {}
    }

    $totalInstalledFinal = $bgPreInstalled + $installedOk
    $totalFoundFinal = $bgPreFound + $count
    Write-BackgroundWuStatus -Status $(if ($timedOut) { "WARN" } else { "COMPLETED" }) -PhaseText "Windows Update: Zavrseno ($totalInstalledFinal/$totalFoundFinal instalirano)" -FoundCount $totalFoundFinal -DownloadedCount $totalFoundFinal -InstalledCount $totalInstalledFinal -RebootRequired $rebootReq
    try { Set-Content -Path "C:\setup\wu-secondpass-done.flag" -Value "DONE" -Encoding ASCII -Force -ErrorAction SilentlyContinue } catch {}

    $finalMsg = if ($timedOut) {
        "Drugi prolaz Windows Update-a prekinut (dostignut zaštitni timeout ${TimeoutSeconds}s), servis nastavlja dalje bez zastoja."
    } else {
        "Drugi prolaz Windows Update-a zavrsen. Obradjeno zakrpi: $count (Instalirano: $installedOk, Ukupno: $totalInstalledFinal, RebootRequired: $rebootReq)."
    }
    if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
        Write-ServisLog "Invoke-WindowsUpdateSecondPass: $finalMsg" -Level $(if ($timedOut) { 'WARN' } else { 'SUCCESS' }) -Component "Update"
    }

    return @{
        Success        = $true
        InstalledCount = $totalInstalledFinal
        RebootRequired = $rebootReq
        Message        = $finalMsg
    }
}

function Wait-WindowsUpdateCompletionBarrier {
    [CmdletBinding()]
    param(
        [int]$TimeoutSeconds = 900,
        [int]$PollIntervalSeconds = 4,
        [switch]$Force
    )

    # Ako je drugi prolaz u Fazi 6 vec kompletirao proveru i pozadinski radnik je zavrsen, preskoci dodatno cekanje
    $bgStatusFile = "C:\setup\wu-bg-status.json"
    $spDoneFile = "C:\setup\wu-secondpass-done.flag"
    if ((-not $Force) -and (Test-Path $bgStatusFile) -and (Test-Path $spDoneFile)) {
        try {
            $rawBg = Get-Content -Path $bgStatusFile -Raw -Encoding UTF8 -ErrorAction SilentlyContinue
            if ($rawBg) {
                $bgObj = ConvertFrom-Json -InputObject $rawBg -ErrorAction SilentlyContinue
                if ($bgObj -and ("$($bgObj.status)".ToUpperInvariant() -in @("COMPLETED", "SKIPPED", "WARN"))) {
                    $activeCheck = Get-Process -Name "mousocoreworker", "tiworker", "trustedinstaller" -ErrorAction SilentlyContinue
                    if (-not $activeCheck) {
                        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                            Write-ServisLog "Wait-WindowsUpdateCompletionBarrier: Zavrsni Windows Update i pozadinski procesi su kompletirani ($($bgObj.status)). Preskacem dodatno cekanje." -Level "SUCCESS" -Component "Update"
                        }
                        return @{
                            Success       = $true
                            WaitedSeconds = 0
                            TimedOut      = $false
                            Skipped       = $false
                        }
                    }
                }
            }
        } catch {}
    }

    $wuMode = Get-WUConfigMode
    if ($wuMode -eq "DriversOnly" -and (-not $Force) -and $TimeoutSeconds -gt 45) {
        $TimeoutSeconds = 45
        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
            Write-ServisLog "Wait-WindowsUpdateCompletionBarrier: Rezim rada je DriversOnly - skracujem maksimalno cekanje na pozadinske WU procese na 45s kako servis ne bi cekao preuzimanje OS LCU paketa." -Level "INFO" -Component "Update"
        }
    }

    if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
        Write-ServisLog "Wait-WindowsUpdateCompletionBarrier: Pokrenuta barijera za pracenje zavrsetka Windows Update-a (Timeout: ${TimeoutSeconds}s)..." -Level "STEP" -Component "Update"
    }
    if (Get-Command Send-IpcProgress -ErrorAction SilentlyContinue) {
        Send-IpcProgress -Progress 10 -Phase 5 -PhaseName "Završetak" -SubPhase "WU Barijera" -StatusMessage "Provera pozadinskih procesa Windows Update-a..."
    }

    $startTime = [datetime]::UtcNow
    $lastLogTime = [datetime]::MinValue
    $consecutiveIdleCount = 0
    $requiredIdleChecks = 2
    $timedOut = $false
    $skipped = $false

    # Pozadinski procesi vezani za preuzimanje i instalaciju Windows Update-a
    $wuProcesses = @("mousocoreworker", "tiworker", "trustedinstaller", "usoclient")

    while ($true) {
        # 1. Provera da li je operater u GUI-ju kliknuo Preskoci / Skip
        if ((Get-Command Test-ServisSkipRequested -ErrorAction SilentlyContinue) -and (Test-ServisSkipRequested)) {
            $skipped = $true
            if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                Write-ServisLog "Wait-WindowsUpdateCompletionBarrier: Operater je zatrazio preskakanje Windows Update barijere." -Level "WARN" -Component "Update"
            }
            break
        }

        # 2. Timeout provera
        $elapsedSec = [int]([datetime]::UtcNow - $startTime).TotalSeconds
        if ($elapsedSec -ge $TimeoutSeconds) {
            $timedOut = $true
            if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                Write-ServisLog "Wait-WindowsUpdateCompletionBarrier: Istekao timeout (${TimeoutSeconds}s). Prekidam cekanje radi zavrsetka procesa." -Level "WARN" -Component "Update"
            }
            break
        }

        # 3. Detekcija aktivnih procesa
        $activeWuProcs = @()
        foreach ($procName in $wuProcesses) {
            $procs = Get-Process -Name $procName -ErrorAction SilentlyContinue
            if ($procs) {
                foreach ($p in $procs) {
                    $activeWuProcs += "$($p.ProcessName) (PID: $($p.Id))"
                }
            }
        }

        # 4. Detekcija aktivnih BITS transfer poslova za Windows Update
        $activeBitsCount = 0
        try {
            $bitsJobs = Get-BitsTransfer -AllUsers -ErrorAction SilentlyContinue
            if ($bitsJobs) {
                $wuBits = $bitsJobs | Where-Object {
                    $_.JobState -in @('Connecting', 'Transferring', 'Transferred') -and
                    ($_.DisplayName -match '(?i)Update|WU|USO|SoftwareDistribution' -or $_.FilesTotal -gt 0)
                }
                if ($wuBits) {
                    $activeBitsCount = @($wuBits).Count
                }
            }
        } catch {}

        # 5. Provera stanja zauzetosti
        $isBusy = ($activeWuProcs.Count -gt 0) -or ($activeBitsCount -gt 0)

        if ($isBusy) {
            $consecutiveIdleCount = 0
            $now = [datetime]::UtcNow
            if (($now - $lastLogTime).TotalSeconds -ge 12) {
                $lastLogTime = $now
                $busyDetails = @()
                if ($activeWuProcs.Count -gt 0) { $busyDetails += "Procesi: " + ($activeWuProcs -join ", ") }
                if ($activeBitsCount -gt 0) { $busyDetails += "BITS poslova: $activeBitsCount" }
                $statusDetail = $busyDetails -join " | "
                $remainSec = $TimeoutSeconds - $elapsedSec

                if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                    Write-ServisLog "Wait-WindowsUpdateCompletionBarrier: Windows Update aktivan u pozadini ($statusDetail). Cekam... (Preostalo do timeout-a: ${remainSec}s)" -Level "INFO" -Component "Update"
                }
                if (Get-Command Send-IpcProgress -ErrorAction SilentlyContinue) {
                    Send-IpcProgress -Progress 12 -Phase 5 -PhaseName "Završetak" -SubPhase "WU Barijera" -StatusMessage "Windows Update skida/instalira zakrpe u pozadini (${remainSec}s)..."
                }
            }
        } else {
            $consecutiveIdleCount++
            if ($consecutiveIdleCount -ge $requiredIdleChecks) {
                if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                    Write-ServisLog "Wait-WindowsUpdateCompletionBarrier: Svi pozadinski procesi i transferi Windows Update-a su zavrseni (${elapsedSec}s)." -Level "SUCCESS" -Component "Update"
                }
                break
            }
        }

        Start-Sleep -Seconds $PollIntervalSeconds
    }

    $totalWait = [int]([datetime]::UtcNow - $startTime).TotalSeconds
    return @{
        Success       = (-not $timedOut)
        WaitedSeconds = $totalWait
        TimedOut      = $timedOut
        Skipped       = $skipped
    }
}

Export-ModuleMember -Function `
    Get-WUQueueDisplay, `
    Format-WUProgress, `
    Get-WUConfigMode, `
    Get-CurrentWindowsBuildNumber, `
    Test-UpdatePackageBuildCompatible, `
    Test-IsDriverUpdate, `
    Test-IsCumulativeUpdate, `
    Start-AppWorker, `
    Optimize-WUPerformance, `
    Invoke-NasUpdatePrePatch, `
    Invoke-ModelDriversPreInstall, `
    Export-ServisHardwareDrivers, `
    Export-ServisNetworkDrivers, `
    Get-UpdateKbArticleId, `
    Send-UpdatePackageToServisHub, `
    Send-ModelDriverPackToServisHub, `
    Save-UpdatePackageFromMsCatalog, `
    Export-ServisWindowsUpdates, `
    Invoke-WindowsUpdateCycle, `
    Invoke-WindowsUpdatePhase, `
    Invoke-Phase2Drivers, `
    Write-BackgroundWuStatus, `
    Start-BackgroundWindowsUpdateWorker, `
    Invoke-BackgroundWindowsUpdateWorker, `
    Invoke-WindowsUpdateSecondPass, `
    Wait-WindowsUpdateCompletionBarrier

