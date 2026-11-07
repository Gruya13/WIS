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
    return "DriversOnly"
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
            ShareName         = "WIS"
            ProgramiShare     = "Programi"
            UpdatesSubfolder  = "Updates\Win11"
            Username          = "admin"
            Password          = ""
            PreInstallDrivers = "1"
            DriversSubDir     = "Drivers"
            HarvestMode       = "All"
        }
    }

    $iniContent = Get-Content $iniPath -Raw -ErrorAction SilentlyContinue
    if (-not $iniContent -or $iniContent -notmatch '(?mi)^\s*\[ServisNas\]([\s\S]*?)(?:^\s*\[|\Z)') {
        return [PSCustomObject]@{
            IniPath           = $iniPath
            Enabled           = "1"
            NasHost           = "192.168.170.254"
            ShareName         = "WIS"
            ProgramiShare     = "Programi"
            UpdatesSubfolder  = "Updates\Win11"
            Username          = "admin"
            Password          = ""
            PreInstallDrivers = "1"
            DriversSubDir     = "Drivers"
            HarvestMode       = "All"
        }
    }

    $nasSec = $matches[1]
    $enabled = if ($nasSec -match '(?mi)^[ \t]*Enabled[ \t]*=[ \t]*([^\r\n#;]+)') { $matches[1].Trim() } else { "1" }
    $nasHost = if ($nasSec -match '(?mi)^[ \t]*NasHost[ \t]*=[ \t]*([^\r\n#;]+)') { $matches[1].Trim() } else { "192.168.170.254" }
    $shareName = if ($nasSec -match '(?mi)^[ \t]*ShareName[ \t]*=[ \t]*([^\r\n#;]+)') { $matches[1].Trim() } else { "WIS" }
    $programiShare = if ($nasSec -match '(?mi)^[ \t]*ProgramiShare[ \t]*=[ \t]*([^\r\n#;]+)') { $matches[1].Trim() } else { "Programi" }
    $updatesSub = if ($nasSec -match '(?mi)^[ \t]*UpdatesSubfolder[ \t]*=[ \t]*([^\r\n#;]+)') { $matches[1].Trim() } else { "Updates\Win11" }
    $user = if ($nasSec -match '(?mi)^[ \t]*Username[ \t]*=[ \t]*([^\r\n#;]+)') { $matches[1].Trim() } else { "admin" }
    if ([string]::IsNullOrWhiteSpace($user)) { $user = "admin" }
    $pass = if ($nasSec -match '(?mi)^[ \t]*Password[ \t]*=[ \t]*([^\r\n#;]+)') { $matches[1].Trim() } else { "" }
    if ([string]::IsNullOrWhiteSpace($pass) -or $pass -eq "promeni_lozinku" -or $pass -eq "CHANGE_ME") { $pass = "" }
    $preInstall = if ($nasSec -match '(?mi)^[ \t]*PreInstallDrivers[ \t]*=[ \t]*([^\r\n#;]+)') { $matches[1].Trim() } else { "1" }
    $driversSub = if ($nasSec -match '(?mi)^[ \t]*DriversSubDir[ \t]*=[ \t]*([^\r\n#;]+)') { $matches[1].Trim() } else { "Drivers" }
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

function Invoke-ModelDriversPreInstall {
    <#
    .SYNOPSIS
        Pre-instalacija kompletnih hardverskih drajvera za model racunara sa lokalnog NAS servera.
    .DESCRIPTION
        Cita konfiguraciju [ServisNas] iz Wifi-Servis.ini. Ako je PreInstallDrivers=1 i NAS dostupan,
        proverava postojanje namenskog paketa drajvera na \\<NasHost>\<ShareName>\Drivers\Harvested\Models\<Model>\.
        Ukoliko paket postoji i sadrzi .inf datoteke, vrsi brzu pre-instalaciju preko pnputil.exe
        pre izlaska na Microsoft Update servere.
    #>
    try {
        $nas = Get-ServisNasSettings
        if (-not $nas -or $nas.Enabled -ne "1" -or $nas.PreInstallDrivers -ne "1") {
            return
        }

        if ([string]::IsNullOrWhiteSpace($nas.NasHost) -or [string]::IsNullOrWhiteSpace($nas.ShareName)) {
            return
        }

        if (-not (Test-NasHostOnline -NasHost $nas.NasHost)) {
            if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                Write-ServisLog "Invoke-ModelDriversPreInstall: NAS server $($nas.NasHost) nije dostupan na mrezi. Preskacem lokalnu pre-instalaciju drajvera." -Level "INFO" -Component "NAS"
            }
            return
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

        $cleanModel = Get-CleanComputerModel
        $uncShare = "\\$($nas.NasHost)\$($nas.ShareName)"
        $smbMapped = $false

        try {
            $uName = if ($nas.Username) { $nas.Username } else { "admin" }
            $uPass = if ($nas.Password -and $nas.Password -ne "promeni_lozinku" -and $nas.Password -ne "CHANGE_ME") { $nas.Password } else { "" }
            $userArg = if ($uName -match "\\") { $uName } else { "$($nas.NasHost)\$uName" }
            if (-not [string]::IsNullOrWhiteSpace($uPass)) {
                & net.exe use $uncShare $uPass "/user:$userArg" /persistent:no >$null 2>&1
                if ($LASTEXITCODE -ne 0) {
                    & net.exe use $uncShare $uPass "/user:$uName" /persistent:no >$null 2>&1
                }
            } else {
                $LASTEXITCODE = 1
            }
            if ($LASTEXITCODE -ne 0) {
                & net.exe use $uncShare '""' /user:guest /persistent:no >$null 2>&1
            }
            $smbMapped = (Test-Path $uncShare -ErrorAction SilentlyContinue)

            try {
                $installedTar = $false
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

                        if (Test-Path $localTar) {
                            & tar.exe -xf "$localTar" -C "$localTemp"

                            & pnputil.exe /add-driver "$localTemp\*.inf" /subdirs /install >$null 2>&1

                            Start-Sleep -Seconds 3

                            $unresolvedDevs = @()
                            try {
                                if (Get-Command Get-PnpDevice -ErrorAction SilentlyContinue) {
                                    $unresolvedDevs = Get-PnpDevice -Status "Error", "Degraded", "Unknown" -ErrorAction SilentlyContinue |
                                        Where-Object { $_.Problem -and $_.Problem -ne 0 -and $_.Class -ne "Printer" -and $_.Class -ne "PrintQueue" }
                                }
                            } catch {}

                            if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                                Write-ServisLog "Pre-instalacija drajvera za model $cleanModel iz single-stream arhive uspesno izvrsena. Preostalo neprepoznatih uredjaja: $($unresolvedDevs.Count)." -Level "SUCCESS" -Component "NAS"
                            }
                            if (Get-Command Show-ServisDashboard -ErrorAction SilentlyContinue) {
                                Show-ServisDashboard -LiveStatus "[OK] Drajveri za $cleanModel uspesno instalirani iz arhive ($cleanModel.tar)"
                            }

                            $installedTar = $true
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

                if (-not $installedTar) {
                    $driversSub = if ($nas.DriversSubDir) { $nas.DriversSubDir } else { "Drivers\Harvested\Models" }
                    $modelDir = Join-Path $uncShare (Join-Path $driversSub $cleanModel)

                    if (Test-Path $modelDir -ErrorAction SilentlyContinue) {
                        $infFiles = Get-ChildItem -Path $modelDir -Filter "*.inf" -Recurse -File -ErrorAction SilentlyContinue
                        if ($infFiles -and $infFiles.Count -gt 0) {
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
                                    $unresolvedDevs = Get-PnpDevice -Status "Error", "Degraded", "Unknown" -ErrorAction SilentlyContinue |
                                        Where-Object { $_.Problem -and $_.Problem -ne 0 -and $_.Class -ne "Printer" -and $_.Class -ne "PrintQueue" }
                                }
                            } catch {}

                            if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                                Write-ServisLog "Pre-instalacija drajvera za model $cleanModel sa NAS-a uspesno izvrsena. Preostalo neprepoznatih uredjaja: $($unresolvedDevs.Count)." -Level "SUCCESS" -Component "NAS"
                            }
                            if (Get-Command Show-ServisDashboard -ErrorAction SilentlyContinue) {
                                Show-ServisDashboard -LiveStatus "[OK] Drajveri za $cleanModel uspesno instalirani sa NAS-a"
                            }
                        } else {
                            if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                                Write-ServisLog "Direktorijum za model $cleanModel na NAS-u ($modelDir) ne sadrzi .inf fajlove." -Level "DEBUG" -Component "NAS"
                            }
                        }
                    } else {
                        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                            Write-ServisLog "Lokalni paket drajvera za model $cleanModel ne postoji na NAS-u ($modelDir). Drajveri ce biti preuzeti tokom WU ciklusa." -Level "INFO" -Component "NAS"
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
            if ($smbMapped) {
                & net.exe use "$uncShare" /delete /y >$null 2>&1
                & net.exe use * /delete /y >$null 2>&1
                if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                    Write-ServisLog "Mrezna SMB sesija sa NAS serverom ($uncShare) je bezbedno zatvorena." -Level "DEBUG" -Component "NAS"
                }
            }
        }
    } catch {
        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
            Write-ServisLog "Upozorenje tokom NAS pre-instalacije drajvera: $($_.Exception.Message)" -Level "WARN" -Component "NAS"
        }
    }
}

function Invoke-NasUpdatePrePatch {
    <#
    .SYNOPSIS
        Pre-patch faza kumulativnih zakrpi i lokalnih drajvera sa lokalnog servisnog NAS servera.
    .DESCRIPTION
        Ukoliko je u Wifi-Servis.ini pod [ServisNas] omogucen NAS kes, povezuje se
        na SMB share, skenira .msu pakete i tiho ih instalira pre COM API pretrage.
        Takodje poziva pre-instalaciju drajvera modela i proveru mreznih drajvera sa NAS-a.
    #>
    try {
        $nas = Get-ServisNasSettings
        if (-not $nas -or $nas.Enabled -ne "1" -or [string]::IsNullOrWhiteSpace($nas.NasHost) -or [string]::IsNullOrWhiteSpace($nas.ShareName)) {
            if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                Write-ServisLog "NAS Pre-patch je iskljucen ili parametri nisu uneti u Wifi-Servis.ini. Nastavljam." -Level "INFO" -Component "NAS"
            }
            return
        }

        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
            Write-ServisLog "Proveravam dostupnost servisnog NAS servera na $($nas.NasHost)..." -Level "INFO" -Component "NAS"
        }

        if (-not (Test-NasHostOnline -NasHost $nas.NasHost)) {
            if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                Write-ServisLog "Servisni NAS server $($nas.NasHost) nije dostupan na lokalnoj mrezi. Nastavljam sa internet Windows Update-om." -Level "INFO" -Component "NAS"
            }
            return
        }

        $uncShare = "\\$($nas.NasHost)\$($nas.ShareName)"
        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
            Write-ServisLog "NAS host $($nas.NasHost) je dostupan. Povezujem se na mrezni resurs $uncShare (Korisnik: $($nas.Username))..." -Level "INFO" -Component "NAS"
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
        if (-not [string]::IsNullOrWhiteSpace($uPass)) {
            & net.exe use $uncShare $uPass "/user:$userArg" /persistent:no >$null 2>&1
            if ($LASTEXITCODE -ne 0) {
                & net.exe use $uncShare $uPass "/user:$uName" /persistent:no >$null 2>&1
            }
        } else {
            $LASTEXITCODE = 1
        }
        if ($LASTEXITCODE -ne 0) {
            & net.exe use $uncShare '""' /user:guest /persistent:no >$null 2>&1
        }

        $targetUpdatesDir = if ($nas.UpdatesSubfolder) { Join-Path $uncShare $nas.UpdatesSubfolder } else { $uncShare }

        try {
            # 1. Lokalni .msu paketi
            $wuMode = Get-WUConfigMode
            if ($wuMode -eq "DriversOnly") {
                if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                    Write-ServisLog "Rezim rada je DriversOnly. Preskacem instalaciju kumulativnih .msu zakrpi sa NAS servera jer su vec integrisane u sistem." -Level "INFO" -Component "NAS"
                }
            } else {
                if (Test-Path $targetUpdatesDir) {
                    $msuList = Get-ChildItem -Path $targetUpdatesDir -Filter "*.msu" -File -ErrorAction SilentlyContinue | Sort-Object Name
                    if ($msuList -and $msuList.Count -gt 0) {
                        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                            Write-ServisLog "Pronadjeno $($msuList.Count) .msu paketa na NAS serveru ($targetUpdatesDir). Pokrecem brzi pre-patch..." -Level "INFO" -Component "NAS"
                        }
                        if (Get-Command Show-ServisDashboard -ErrorAction SilentlyContinue) {
                            Show-ServisDashboard -LiveStatus "Instaliram lokalne .msu zakrpe sa NAS servera ($($msuList.Count) paketa)..."
                        }

                        $idx = 0
                        foreach ($msu in $msuList) {
                            $idx++
                            # Brza provera da li je KB vec instaliran na sistemu (0.1s) pre prenosa preko mreze
                            if ($msu.Name -match '(?i)(KB\d{6,8})') {
                                $kbCheck = $matches[1].ToUpper()
                                $alreadyInstalled = Get-HotFix -Id $kbCheck -ErrorAction SilentlyContinue
                                if ($alreadyInstalled) {
                                    if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                                        Write-ServisLog "[$idx/$($msuList.Count)] Zakrpa $kbCheck ($($msu.Name)) je vec instalirana na sistemu. Preskacem." -Level "INFO" -Component "NAS"
                                    }
                                    continue
                                }
                            }

                            if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                                Write-ServisLog "[$idx/$($msuList.Count)] Preuzimam i instaliram lokalni NAS pre-patch: $($msu.Name)..." -Level "STEP" -Component "NAS"
                            }
                            if (Get-Command Show-ServisDashboard -ErrorAction SilentlyContinue) {
                                Show-ServisDashboard -LiveStatus "[$idx/$($msuList.Count)] NAS pre-patch: $($msu.Name)..."
                            }

                            # Sekvencijalno kopiranje na lokalni NVMe SSD pre poziva wusa.exe (3-4x brze od direktnog SMB random I/O raspakivanja)
                            $localMsuPath = Join-Path $env:TEMP $msu.Name
                            $targetExecMsu = $msu.FullName
                            try {
                                Copy-Item -Path $msu.FullName -Destination $localMsuPath -Force -ErrorAction Stop
                                if (Test-Path $localMsuPath) {
                                    $targetExecMsu = $localMsuPath
                                }
                            } catch {}

                            try {
                                $p = Start-Process "wusa.exe" -ArgumentList "`"$targetExecMsu`" /quiet /norestart" -Wait -PassThru
                                $exitCode = if ($p) { $p.ExitCode } else { -1 }

                                # 0 = Uspeh, 3010 = Uspeh (potreban restart), 2359302 = Vec instaliran (0x00240006)
                                if ($exitCode -eq 0 -or $exitCode -eq 3010 -or $exitCode -eq 2359302) {
                                    if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                                        Write-ServisLog "Paket $($msu.Name) uspesno primenjen (Exit kod: $exitCode)." -Level "SUCCESS" -Component "NAS"
                                    }
                                } else {
                                    if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                                        Write-ServisLog "Paket $($msu.Name) zavrsio sa exit kodom: $exitCode." -Level "WARN" -Component "NAS"
                                    }
                                }
                            } finally {
                                if ($localMsuPath -and (Test-Path $localMsuPath)) {
                                    Remove-Item -Path $localMsuPath -Force -ErrorAction SilentlyContinue
                                }
                            }
                        }
                    } else {
                        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                            Write-ServisLog "Nisu pronadjeni .msu paketi u $targetUpdatesDir na NAS serveru." -Level "INFO" -Component "NAS"
                        }
                    }
                } else {
                    if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                        Write-ServisLog "Direktorijum sa azuriranjima $targetUpdatesDir nije dostupan na NAS deljenju." -Level "WARN" -Component "NAS"
                    }
                }
            }

            # 2. Pre-instalacija kompletnih drajvera za model racunara sa NAS-a
            Invoke-ModelDriversPreInstall

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
            # Obavezno zatvaranje mrezne sesije radi bezbednosti
            & net.exe use "$uncShare" /delete /y >$null 2>&1
            & net.exe use * /delete /y >$null 2>&1
            if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                Write-ServisLog "Mrezna SMB sesija sa NAS serverom ($uncShare) je bezbedno zatvorena." -Level "DEBUG" -Component "NAS"
            }
        }
    } catch {
        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
            Write-ServisLog "Greska tokom NAS pre-patch ciklusa: $($_.Exception.Message)" -Level "WARN" -Component "NAS"
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

    try {
        # 1. Citanje konfiguracije iz Wifi-Servis.ini
        $nas = Get-ServisNasSettings
        if (-not $nas -or $nas.Enabled -ne "1") {
            return
        }

        if ([string]::IsNullOrWhiteSpace($nas.NasHost) -or [string]::IsNullOrWhiteSpace($nas.ShareName)) {
            return
        }

        # 2. Provera mrezne dostupnosti NAS hosta
        if (-not (Test-NasHostOnline -NasHost $nas.NasHost)) {
            if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                Write-ServisLog "Export-ServisHardwareDrivers: NAS server $($nas.NasHost) nije dostupan. Preskacem arhiviranje." -Level "INFO" -Component "DriverHarvest"
            }
            return
        }

        # 3. Model racunara i rezim berbe
        $cleanModel = Get-CleanComputerModel
        $effectiveHarvestMode = if ($HarvestMode) { $HarvestMode } elseif ($nas.HarvestMode) { $nas.HarvestMode } else { "All" }

        $uncShare = "\\$($nas.NasHost)\$($nas.ShareName)"

        # Pre mapiranja SMB deljenja: osigurati SMB Guest pristup i potpisivanje u registru (Windows 11 hardening bypass)
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
        if (-not [string]::IsNullOrWhiteSpace($uPass)) {
            & net.exe use $uncShare $uPass "/user:$userArg" /persistent:no >$null 2>&1
            if ($LASTEXITCODE -ne 0) {
                & net.exe use $uncShare $uPass "/user:$uName" /persistent:no >$null 2>&1
            }
        } else {
            $LASTEXITCODE = 1
        }
        if ($LASTEXITCODE -ne 0) {
            & net.exe use $uncShare '""' /user:guest /persistent:no >$null 2>&1
        }
        $smbMapped = (Test-Path $uncShare -ErrorAction SilentlyContinue)

        $driversSub = if ($nas.DriversSubDir) { $nas.DriversSubDir } else { "Drivers\Harvested\Models" }
        $nasModelDest = Join-Path $uncShare (Join-Path $driversSub $cleanModel)

        # Provera postojanja i deduplikacije (>= 3 .inf fajla na NAS-u)
        try {
            if ((Test-Path $nasModelDest -ErrorAction SilentlyContinue) -and (-not $Force)) {
                $existingInfs = Get-ChildItem -Path $nasModelDest -Filter "*.inf" -Recurse -File -ErrorAction SilentlyContinue
                if ($existingInfs -and $existingInfs.Count -ge 3) {
                    if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                        Write-ServisLog "Drajveri za model $cleanModel vec postoje na NAS serveru ($($existingInfs.Count) .inf paketa u $nasModelDest). Preskacem ponovni upload (deduplikacija)." -Level "INFO" -Component "DriverHarvest"
                    }
                    return
                }
            }
        } catch {}

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

        # 7. Kopiranje drajvera u Drivers\Harvested\Models\<cleanModel>
        # 8. Mrezni drajveri (Class=Net) se kopiraju i u Drivers\Harvested\Net\<cleanModel> radi kompatibilnosti sa ADR-0027
        # OKLOPLJENO u striktan try/catch blok tako da nijedna greska u kreiranju foldera ili Copy-Item ne prekida servisni proces
        $copiedFolders = 0
        $copiedNetFolders = 0
        try {
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

            # 9. Single-Stream tar.exe arhiviranje za hiper-brzu distribuciju floti racunara
            try {
                $tarPath = Join-Path $env:TEMP ("$cleanModel.tar")
                if (Test-Path $tarPath) { Remove-Item -Path $tarPath -Force -ErrorAction SilentlyContinue }
                & tar.exe -cf "$tarPath" -C "$tempExportDir" .
                if (Test-Path $tarPath) {
                    $nasArchivesDir = Join-Path $uncShare "Drivers\Harvested\Archives"
                    if (-not (Test-Path $nasArchivesDir -ErrorAction SilentlyContinue)) {
                        New-Item -Path $nasArchivesDir -ItemType Directory -Force -ErrorAction Stop | Out-Null
                    }
                    $destTarPath = Join-Path $nasArchivesDir "$cleanModel.tar"
                    Copy-Item -Path $tarPath -Destination $destTarPath -Force -ErrorAction Stop
                    if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                        Write-ServisLog "Drajveri modela $cleanModel uspesno arhivirani u $cleanModel.tar na NAS-u." -Level "SUCCESS" -Component "DriverHarvest"
                    }
                }
            } catch {
                if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                    Write-ServisLog "UPOZORENJE: Arhiviranje drajvera u tar paket ($cleanModel.tar) na NAS nije uspelo: $($_.Exception.Message)" -Level "WARN" -Component "DriverHarvest"
                }
            } finally {
                if ($tarPath -and (Test-Path $tarPath)) {
                    Remove-Item -Path $tarPath -Force -ErrorAction SilentlyContinue
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
        # Garantovano raskidanje SMB sesije radi bezbednosti
        if ($smbMapped -and $uncShare) {
            & net.exe use "$uncShare" /delete /y >$null 2>&1
            & net.exe use * /delete /y >$null 2>&1
            if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                Write-ServisLog "Mrezna SMB sesija sa NAS serverom ($uncShare) je bezbedno zatvorena." -Level "DEBUG" -Component "DriverHarvest"
            }
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

    if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
        Write-ServisLog "=== POKRETANJE FAZE 2: Drajveri i PnP Integracija ===" -Level "STEP" -Component "Phase2"
    }
    if (Get-Command Send-IpcProgress -ErrorAction SilentlyContinue) {
        Send-IpcProgress -Progress 5 -Phase 2 -PhaseName "Drajveri" -SubPhase "Inicijalizacija" -StatusMessage "Skeniranje hardverskih komponenti i drajvera..."
    }

    if (Get-Command Wait-ServisUICooperativePause -ErrorAction SilentlyContinue) {
        Wait-ServisUICooperativePause -CheckpointName "Faza 2 - Pocetak"
    }

    # 1. Detekcija modela i hardverskih parametara
    $modelName = "Standardni PC"
    $serialNum = "N/A"
    try {
        $cs = Get-CimInstance Win32_ComputerSystem -ErrorAction SilentlyContinue
        $bios = Get-CimInstance Win32_Bios -ErrorAction SilentlyContinue
        if ($cs -and $cs.Model) { $modelName = ($cs.Model -replace '\s+', ' ').Trim() }
        if ($bios -and $bios.SerialNumber) { $serialNum = $bios.SerialNumber.Trim() }
    } catch {}

    if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
        Write-ServisLog "Detektovan racunar: $modelName (Serijski broj: $serialNum)" -Level "INFO" -Component "Hardware"
    }
    if (Get-Command Send-IpcProgress -ErrorAction SilentlyContinue) {
        Send-IpcProgress -Progress 15 -Phase 2 -PhaseName "Drajveri" -SubPhase "Identifikacija" -StatusMessage "Identifikovan model: $modelName"
    }

    # 1A. Optimizacija Windows Update, Delivery Optimization LAN Peeringa i Defender izuzetaka za Servicing Stack
    try {
        Optimize-WUPerformance
    } catch {}

    # 1B. Brza pre-instalacija drajvera modela (<Model>.tar) i lokalnih zakrpa sa TrueNAS servera
    if (Get-Command Send-IpcProgress -ErrorAction SilentlyContinue) {
        Send-IpcProgress -Progress 20 -Phase 2 -PhaseName "Drajveri" -SubPhase "TrueNAS Keš" -StatusMessage "Provera single-stream drajvera (<Model>.tar) i zakrpa na TrueNAS-u..."
    }
    try {
        Invoke-NasUpdatePrePatch
    } catch {}

    # 2. Pronalazenje lokalnih i staged paketa drajvera
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

    # 3. Prioritetna instalacija Intel VMD / RST Storage drajvera (Best-Effort)
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
                    $pnpOut = & pnputil.exe /add-driver "$vPath\*.inf" /subdirs /install 2>&1
                }
            } catch {
                if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                    Write-ServisLog "Upozorenje pri instalaciji VMD drajvera iz $($vPath): $($_.Exception.Message)" -Level "WARN" -Component "Drivers"
                }
            }
        }
    } else {
        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
            Write-ServisLog "Nisu pronadjeni namenski offline VMD paketi (standardni NVMe/SATA kontroler)." -Level "DEBUG" -Component "Drivers"
        }
    }

    # 4. Opsta PnP instalacija svih preostalih lokalnih drajvera (Best-Effort)
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
                    $pnpOut = & pnputil.exe /add-driver "$df\*.inf" /subdirs /install 2>&1
                    $pnpInstalledCount += $allInfs.Count
                }
            } catch {
                if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                    Write-ServisLog "Upozorenje pri PnP instalaciji iz $($df): $($_.Exception.Message)" -Level "WARN" -Component "Drivers"
                }
            }
        }
    }
    if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
        Write-ServisLog "Lokalna PnP instalacija zavrsena (obradjeno ukupno $pnpInstalledCount drajver profila)." -Level "SUCCESS" -Component "Drivers"
    }

    # 5. Skeniranje nedostajucih i neprepoznatih uredjaja
    if (Get-Command Send-IpcProgress -ErrorAction SilentlyContinue) {
        Send-IpcProgress -Progress 65 -Phase 2 -PhaseName "Drajveri" -SubPhase "Dijagnostika uredjaja" -StatusMessage "Skeniranje neprepoznatih PnP uređaja..."
    }
    $problemDevices = @()
    try {
        $problemDevices = Get-PnpDevice -ErrorAction SilentlyContinue | Where-Object {
            $_.Status -in @("Error", "Degraded", "Unknown") -or $_.Problem -ne 0
        }
    } catch {}

    if ($problemDevices -and $problemDevices.Count -gt 0) {
        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
            Write-ServisLog "Detektovano $($problemDevices.Count) uredjaja sa nedostajucim ili nekompletnim drajverima." -Level "WARN" -Component "Drivers"
        }
        if (-not $SkipWindowsUpdate -and (Get-Command Resolve-MissingDriversFromHub -ErrorAction SilentlyContinue)) {
            $hubRes = Resolve-MissingDriversFromHub
            if ($hubRes -and $hubRes.AllResolved -and $hubRes.TotalProblematic -gt 0) {
                if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                    Write-ServisLog "Svi problematični uređaji su uspešno rešeni preko ServisHub-a. Preskačem Windows Update pretragu." -Level "SUCCESS" -Component "Drivers"
                }
                $SkipWindowsUpdate = $true
            }
        }
    } else {
        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
            Write-ServisLog "Svi PnP hardverski uredjaji su prepoznati i funkcionalni." -Level "SUCCESS" -Component "Drivers"
        }
        if (-not $SkipWindowsUpdate) {
            if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                Write-ServisLog "Nema preostalih neprepoznatih uređaja. Preskačem Windows Update pretragu drajvera." -Level "INFO" -Component "Drivers"
            }
            $SkipWindowsUpdate = $true
        }
    }

    # 6. Selektivno povlacenje drajvera preko Windows Update-a (Best-Effort)
    if (-not $SkipWindowsUpdate) {
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
            $uSearcher.ServerSelection = 2 # Windows Update Catalog
            
            $searchResult = $uSearcher.Search("IsInstalled=0 and Type='Driver'")
            $driverUpdates = $searchResult.Updates
            
            if ($driverUpdates -and $driverUpdates.Count -gt 0) {
                $totalDrv = $driverUpdates.Count
                if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                    Write-ServisLog "Pronadjeno $totalDrv drajverskih azuriranja na Windows Update-u. Pripremam batch preuzimanje i instalaciju..." -Level "STEP" -Component "WinUpdate"
                }
                if (Get-Command Send-IpcProgress -ErrorAction SilentlyContinue) {
                    Send-IpcProgress -Progress 80 -Phase 2 -PhaseName "Drajveri" -SubPhase "Windows Update" -StatusMessage "Preuzimanje i instalacija $totalDrv drajvera..."
                }

                # Prihvatanje EULA licenci i grupno (Batch) preuzimanje svih nepreuzetih drajvera u jednom prolazu
                $batchDlColl = New-Object -ComObject Microsoft.Update.UpdateColl
                for ($bIdx = 0; $bIdx -lt $totalDrv; $bIdx++) {
                    $bDrv = $driverUpdates[$bIdx]
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

                for ($dIdx = 0; $dIdx -lt $totalDrv; $dIdx++) {
                    $drv = $driverUpdates[$dIdx]
                    $drvNum = $dIdx + 1

                    if (Get-Command Wait-ServisUICooperativePause -ErrorAction SilentlyContinue) {
                        Wait-ServisUICooperativePause -CheckpointName "Faza 2 - Drajver ($drvNum/$totalDrv): $($drv.Title)"
                    }
                    if ((Get-Command Test-ServisSkipRequested -ErrorAction SilentlyContinue) -and (Test-ServisSkipRequested)) {
                        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                            Write-ServisLog "Preskocen drajver na zahtev servisera: $($drv.Title)" -Level "WARN" -Component "WinUpdate"
                        }
                        continue
                    }

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
                        $installResult = $installer.Install()

                        $resCode = if ($installResult) { $installResult.ResultCode } else { -1 }
                        $resText = switch ($resCode) {
                            2 { "Uspesno" }
                            3 { "Uspesno sa greskama" }
                            4 { "Neuspesno" }
                            5 { "Prekinuto" }
                            default { "Kod: $resCode" }
                        }
                        $logLvl = if ($resCode -eq 2) { "SUCCESS" } else { "WARN" }

                        if ($resCode -eq 4 -and $installResult) {
                            try {
                                $subRes = $installResult.GetUpdateResult(0)
                                if ($subRes) {
                                    $hresInt = $subRes.HResult
                                    $hexHres = "0x{0:x8}" -f ($hresInt -band 0xFFFFFFFF)
                                    if ($hexHres -eq "0x8024200b" -or $hexHres -eq "0x80070103") {
                                        $resText = "Noviji drajver vec prisutan na sistemu ($hexHres)"
                                        $logLvl = "INFO"
                                    }
                                }
                            } catch {}
                        }

                        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                            Write-ServisLog "Drajver ($drvNum/$totalDrv): $($drv.Title) -> $resText" -Level $logLvl -Component "WinUpdate"
                        }
                    } catch {
                        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                            Write-ServisLog "Upozorenje pri instalaciji drajvera $($drv.Title): $($_.Exception.Message)" -Level "WARN" -Component "WinUpdate"
                        }
                        if (Get-Command Send-IpcLog -ErrorAction SilentlyContinue) {
                            Send-IpcLog -Message "Drajver $($drv.Title) nije uspeo, nastavljam..." -Level "WARN" -Phase 2
                        }
                    }
                }
            } else {
                if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                    Write-ServisLog "Nema dodatnih drajvera na Windows Update servisu." -Level "INFO" -Component "WinUpdate"
                }
            }
        } catch {
            if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                Write-ServisLog "Windows Update pretraga drajvera nije uspela ili je servis nedostupan: $($_.Exception.Message). Nastavljam u Best-Effort režimu." -Level "WARN" -Component "WinUpdate"
            }
            if (Get-Command Send-IpcLog -ErrorAction SilentlyContinue) {
                Send-IpcLog -Message "Windows Update drajveri nisu dostupni (nastavljam sa postojećim PnP drajverima)..." -Level "WARN" -Phase 2
            }
        }
    }

    # 6B. Samouceca berba hardverskih drajvera u <Model>.tar na TrueNAS serveru
    try {
        Export-ServisHardwareDrivers
    } catch {}

    # 7. Slanje azuriranog hardverskog statusa ka UI-ju
    $hwData = [ordered]@{
        model  = $modelName
        serial = $serialNum
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

    if (Get-Command Send-IpcProgress -ErrorAction SilentlyContinue) {
        Send-IpcProgress -Progress 100 -Phase 2 -PhaseName "Drajveri" -SubPhase "Završeno" -StatusMessage "Faza 2 uspešno završena. Drajveri su obrađeni."
    }
    if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
        Write-ServisLog "=== FAZA 2 USPESNO ZAVRSENA: Drajveri i PnP komponente su integrisani (Best-Effort prolaz) ===" -Level "SUCCESS" -Component "Phase2"
    }

    $global:LASTEXITCODE = 0
}


function Invoke-WindowsUpdateSecondPass {
    <#
    .SYNOPSIS
        Dvoprolazno (Dual-Pass) Windows Update skeniranje i instalacija zaostalih kumulativnih zakrpi.
    .DESCRIPTION
        Pokrece se pre zavrsnog ciscenja sistema (u Fazi 5).
        Vrsi brzi re-scan Windows Update kataloga za zaostale kumulativne nadogradnje oslobodjene
        nakon instalacije drajvera i Servicing Stack Update-a (SSU).
        Ukoliko postoje nove zakrpe, preuzima ih i instalira automatski.
        Ako nema novih zakrpi, odmah vraca status: Sistem je u potpunosti azuriran (0 zaostalih zakrpi).
    .COMPATIBILITY
        Windows PowerShell 5.1+
    #>
    [CmdletBinding()]
    param(
        [int]$TimeoutSeconds = 600
    )

    if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
        Write-ServisLog "=== POKRECEM DRUGI PROLAZ (DUAL-PASS) WINDOWS UPDATE-A ===" -Level "STEP" -Component "Update"
    }
    if (Get-Command Send-IpcProgress -ErrorAction SilentlyContinue) {
        Send-IpcProgress -Progress 8 -Phase 5 -PhaseName "Zavrsetak" -SubPhase "Dual-Pass Update" -StatusMessage "Skeniranje zaostalih kumulativnih zakrpi i drajvera..."
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
            InstalledCount = 0
            RebootRequired = $false
            Message        = "Preskoceno - nema internet konekcije."
        }
    }

    # 1. Osvezavanje Windows Update servisa i pokretanje brzog interaktivnog skeniranja
    try {
        Start-Process "usoclient.exe" -ArgumentList "StartInteractiveScan" -WindowStyle Hidden -ErrorAction SilentlyContinue
    } catch {}
    Start-Sleep -Seconds 3

    # 2. Inicijalizacija COM objekta Microsoft.Update.Session
    $UpdateSession = $null
    $SearchResult = $null
    try {
        $UpdateSession = New-Object -ComObject Microsoft.Update.Session
        $UpdateSearcher = $UpdateSession.CreateUpdateSearcher()
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
                InstalledCount = 0
                RebootRequired = $false
                Message        = "Greska pri pretrazi: $searchErr"
            }
        }
    }

    $rawCount = if ($SearchResult -and $SearchResult.Updates) { $SearchResult.Updates.Count } else { 0 }
    if ($rawCount -eq 0) {
        $cleanMsg = "Sistem je u potpunosti azuriran (0 zaostalih zakrpi)."
        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
            Write-ServisLog "Invoke-WindowsUpdateSecondPass: $cleanMsg" -Level "SUCCESS" -Component "Update"
        }
        if (Get-Command Send-IpcLog -ErrorAction SilentlyContinue) {
            Send-IpcLog -Message $cleanMsg -Level "SUCCESS" -Tag "[UPDATE]" -Phase 5
        }
        return @{
            Success        = $true
            InstalledCount = 0
            RebootRequired = $false
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
        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
            Write-ServisLog "Invoke-WindowsUpdateSecondPass: $filteredCleanMsg" -Level "SUCCESS" -Component "Update"
        }
        if (Get-Command Send-IpcLog -ErrorAction SilentlyContinue) {
            Send-IpcLog -Message $filteredCleanMsg -Level "SUCCESS" -Tag "[UPDATE]" -Phase 5
        }
        return @{
            Success        = $true
            InstalledCount = 0
            RebootRequired = $false
            Message        = $filteredCleanMsg
        }
    }

    if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
        Write-ServisLog "Invoke-WindowsUpdateSecondPass: Detektovano $count zaostalih azuriranja (Rezim: $wuMode). Zapocinjem preuzimanje i instalaciju (Zaštitni Timeout: ${TimeoutSeconds}s)..." -Level "INFO" -Component "Update"
    }

    # 3. Kontrolisano preuzimanje i instalacija zaostalih zakrpi uz zaštitni timeout
    $rebootReq = $false
    $installedOk = 0
    $timedOut = $false
    $swPass = [System.Diagnostics.Stopwatch]::StartNew()

    $job = $null
    try {
        $job = Start-Job -ScriptBlock {
            param([string[]]$titles)
            try {
                $session = New-Object -ComObject Microsoft.Update.Session
                $searcher = $session.CreateUpdateSearcher()
                $searcher.ServerSelection = 0
                $res = $searcher.Search("IsInstalled=0 and IsHidden=0")
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
        } -ArgumentList (,$updateTitles)
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
                Send-IpcProgress -Progress 9 -Phase 5 -PhaseName "Završetak" -SubPhase "Dual-Pass Update (${elapsedSec}s/${TimeoutSeconds}s)" -StatusMessage "Obrada $count zaostalih zakrpi u toku (${elapsedSec}s)..."
            }
            Start-Sleep -Seconds 2
        }

        if (-not $timedOut -and $job.State -eq 'Completed') {
            try {
                $jobResult = Receive-Job $job -ErrorAction SilentlyContinue
                Remove-Job $job -Force -ErrorAction SilentlyContinue
                if ($jobResult -and $jobResult.Success) {
                    $installedOk = $jobResult.Installed
                    $rebootReq = [bool]$jobResult.Reboot
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
                $rebootReq = [bool]$InstallResult.RebootRequired
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

    $finalMsg = if ($timedOut) {
        "Drugi prolaz Windows Update-a prekinut (dostignut zaštitni timeout ${TimeoutSeconds}s), servis nastavlja dalje bez zastoja."
    } else {
        "Drugi prolaz Windows Update-a zavrsen. Obradjeno zakrpi: $count (Instalirano: $installedOk, RebootRequired: $rebootReq)."
    }
    if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
        Write-ServisLog "Invoke-WindowsUpdateSecondPass: $finalMsg" -Level $(if ($timedOut) { 'WARN' } else { 'SUCCESS' }) -Component "Update"
    }

    return @{
        Success        = $true
        InstalledCount = $installedOk
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
    Test-IsDriverUpdate, `
    Test-IsCumulativeUpdate, `
    Start-AppWorker, `
    Optimize-WUPerformance, `
    Invoke-NasUpdatePrePatch, `
    Invoke-ModelDriversPreInstall, `
    Export-ServisHardwareDrivers, `
    Export-ServisNetworkDrivers, `
    Invoke-WindowsUpdateCycle, `
    Invoke-WindowsUpdatePhase, `
    Invoke-Phase2Drivers, `
    Invoke-WindowsUpdateSecondPass, `
    Wait-WindowsUpdateCompletionBarrier

