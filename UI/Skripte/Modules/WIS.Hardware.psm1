<#
.MODULE
    WIS.Hardware.psm1
.DESCRIPTION
    Hardverska telemetrija, profilisanje masine, detekcija komponenti i baterije za WIS (Windows Installation Assistant).
.COMPATIBILITY
    Windows PowerShell 5.1+
#>
Import-Module (Join-Path $PSScriptRoot 'WIS.StatusContract.psm1')


function Get-HardwareDiagnosticsInfo {
    $info = @{}

    # 1. Informacije o sistemu, ploci i OS-u
    $sys = Get-CimInstance Win32_ComputerSystem -ErrorAction SilentlyContinue
    $bios = Get-CimInstance Win32_Bios -ErrorAction SilentlyContinue
    $board = Get-CimInstance Win32_BaseBoard -ErrorAction SilentlyContinue
    $os = Get-CimInstance Win32_OperatingSystem -ErrorAction SilentlyContinue

    $mfg = if ($sys.Manufacturer) { ($sys.Manufacturer -replace '\s+', ' ').Trim() } else { "Nepoznato" }
    $model = if ($sys.Model) { ($sys.Model -replace '\s+', ' ').Trim() } else { "Nepoznato" }

    # Normalizacija modela (Lenovo / Dell / Custom)
    $csp = Get-CimInstance Win32_ComputerSystemProduct -ErrorAction SilentlyContinue
    $cspVer = if ($csp -and $csp.Version) { ($csp.Version -replace '\s+', ' ').Trim() } else { "" }
    $cspName = if ($csp -and $csp.Name) { ($csp.Name -replace '\s+', ' ').Trim() } else { "" }
    $boardProd = if ($board -and $board.Product) { ($board.Product -replace '\s+', ' ').Trim() } else { "" }
    $invalidModelStrings = @("None", "Default string", "To be filled by O.E.M.", "System Product Name", "System manufacturer", "Not Applicable", "N/A", "Unknown", "")

    if ($mfg -match '(?i)Lenovo') {
        if ($cspVer -and ($invalidModelStrings -notcontains $cspVer)) {
            $model = $cspVer
        } elseif ($cspName -and ($invalidModelStrings -notcontains $cspName)) {
            $model = $cspName
        }
    } elseif ($mfg -match '(?i)Dell') {
        if ($model -and ($invalidModelStrings -notcontains $model)) {
            # Zadrzi Win32_ComputerSystem.Model (npr. 'Latitude 5490')
        } elseif ($cspName -and ($invalidModelStrings -notcontains $cspName)) {
            $model = $cspName
        }
    } else {
        if (-not $model -or ($invalidModelStrings -contains $model) -or ($model -match '(?i)System Product Name|To be filled')) {
            if ($boardProd -and ($invalidModelStrings -notcontains $boardProd)) {
                $model = $boardProd
            } elseif ($cspName -and ($invalidModelStrings -notcontains $cspName)) {
                $model = $cspName
            }
        }
    }
    $serial = if ($bios.SerialNumber) { $bios.SerialNumber.Trim() } else { "N/A" }
    $biosVer = if ($bios.SMBIOSBIOSVersion) { $bios.SMBIOSBIOSVersion.Trim() } else { "Nepoznato" }
    $biosDate = ""
    if ($bios.ReleaseDate) {
        try {
            $biosDate = ([datetime]$bios.ReleaseDate).ToString("dd.MM.yyyy")
        } catch {
            $biosDate = "$($bios.ReleaseDate)"
        }
    }
    $boardName = if ($board.Product) { "$($board.Manufacturer) $($board.Product)".Trim() } else { "Standardna ploca" }
    $osCaption = if ($os.Caption) { ($os.Caption -replace 'Microsoft ', '').Trim() } else { "Windows 11" }
    $osBuild = if ($os.BuildNumber) { "Build $($os.BuildNumber)" } else { "" }
    $osArch = if ($os.OSArchitecture) { $os.OSArchitecture } else { "64-bit" }

    # 2. Sigurnosni indikatori (Autopilot, SecureBoot, BitLocker)
    $apStatus = "Clean"
    $apTenant = ""
    $apPresent = $false
    $serverHandled = $false

    # Određivanje serijskog broja uređaja za upit (BIOS ili matična ploča)
    $targetSerial = ""
    if ($bios -and $bios.SerialNumber -and $bios.SerialNumber.Trim() -ne "" -and $bios.SerialNumber.Trim() -ne "N/A" -and $bios.SerialNumber.Trim() -notmatch 'To Be Filled|System Serial') {
        $targetSerial = $bios.SerialNumber.Trim()
    } elseif ($board -and $board.SerialNumber -and $board.SerialNumber.Trim() -ne "" -and $board.SerialNumber.Trim() -ne "N/A" -and $board.SerialNumber.Trim() -notmatch 'To Be Filled|System Serial') {
        $targetSerial = $board.SerialNumber.Trim()
    } elseif ($serial -ne "N/A" -and $serial -ne "") {
        $targetSerial = $serial
    }

    # 2a. Brzi mrežni upit ka GDS Central Server API-ju (Autopilot status)
    try {
        if ($targetSerial -and $targetSerial -ne "N/A" -and $targetSerial -ne "Nepoznato") {
            $gdsCandidateHosts = [System.Collections.Generic.List[string]]::new()
            $gdsPort = 8000
            $gdsEnabled = $true

            # Ako je radni GDS Server URL već keširan u sesiji, postavi ga na vrh liste
            if ($Global:GdsServerUrl) {
                $gdsCandidateHosts.Add($Global:GdsServerUrl)
            }

            # 1. Provera environment varijabli ($env:GDS_SERVER_URL ili $env:GDS_SERVER_IP)
            if ($env:GDS_SERVER_URL -and $env:GDS_SERVER_URL.Trim() -ne "") {
                $gdsCandidateHosts.Add($env:GDS_SERVER_URL.Trim())
            } elseif ($env:GDS_SERVER_IP -and $env:GDS_SERVER_IP.Trim() -ne "") {
                $gdsCandidateHosts.Add($env:GDS_SERVER_IP.Trim())
            }

            # 2. Provera Wifi-Servis.ini ([GdsServer] ili [ServisHub])
            $iniCandidates = @(
                "C:\setup\Wifi-Servis.ini",
                (Join-Path $PSScriptRoot "Wifi-Servis.ini"),
                (Join-Path $PSScriptRoot "Skripte\Wifi-Servis.ini"),
                (Join-Path (Split-Path $PSScriptRoot -Parent) "Wifi-Servis.ini"),
                (Join-Path (Split-Path $PSScriptRoot -Parent) "Skripte\Wifi-Servis.ini")
            )
            if ($Global:LocalSetupDir) { $iniCandidates += (Join-Path $Global:LocalSetupDir "Wifi-Servis.ini") }
            if ($Global:SourceRoot) {
                $iniCandidates += (Join-Path $Global:SourceRoot "Wifi-Servis.ini")
                $iniCandidates += (Join-Path $Global:SourceRoot "Skripte\Wifi-Servis.ini")
            }
            foreach ($d in (Get-PSDrive -PSProvider FileSystem -ErrorAction SilentlyContinue)) {
                $iniCandidates += (Join-Path "$($d.Root)" "Wifi-Servis.ini")
                $iniCandidates += (Join-Path "$($d.Root)" "Skripte\Wifi-Servis.ini")
            }

            $iniPath = $iniCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
            if ($iniPath) {
                try {
                    $iniContent = Get-Content $iniPath -Raw -ErrorAction SilentlyContinue
                    $configuredHosts = ""
                    if ($iniContent -match '(?mi)^\s*\[GdsServer\]([\s\S]*?)(?:^\s*\[|\Z)') {
                        $gdsSec = $matches[1]
                        if ($gdsSec -match '(?mi)^\s*Enabled\s*=\s*0') { $gdsEnabled = $false }
                        if ($gdsSec -match '(?mi)^\s*ServerHost\s*=\s*([^\r\n#;]+)') { $configuredHosts = $matches[1].Trim() }
                        if ($gdsSec -match '(?mi)^\s*ServerPort\s*=\s*([0-9]+)') { $gdsPort = [int]$matches[1].Trim() }
                    }
                    if (-not $configuredHosts -and $iniContent -match '(?mi)^\s*\[ServisHub\]([\s\S]*?)(?:^\s*\[|\Z)') {
                        $hubSec = $matches[1]
                        if ($hubSec -match '(?mi)^\s*ServerHost\s*=\s*([^\r\n#;]+)') { $configuredHosts = $matches[1].Trim() }
                    }
                    if ($configuredHosts) {
                        $tokens = ($configuredHosts -split ',') | ForEach-Object { $_.Trim() } | Where-Object { $_ -and $_ -ne "auto" }
                        foreach ($t in $tokens) {
                            if (-not $gdsCandidateHosts.Contains($t)) { $gdsCandidateHosts.Add($t) }
                        }
                    }
                } catch {}
            }

            # Ako je ServisHub host već prepoznat u sesiji, dodaj i njega
            if ($Global:ServisHubUrl) {
                try {
                    $shUri = [System.Uri]$Global:ServisHubUrl
                    if ($shUri.Host -and -not $gdsCandidateHosts.Contains($shUri.Host)) {
                        $gdsCandidateHosts.Insert(0, $shUri.Host)
                    }
                } catch {}
            }

            # 3. Poznate lokalne adrese i Default Gateway
            $knownIps = @("192.168.170.254", "192.168.88.50", "192.168.180.50", "192.168.170.188", "192.168.1.100")
            foreach ($kip in $knownIps) {
                if (-not $gdsCandidateHosts.Contains($kip)) { $gdsCandidateHosts.Add($kip) }
            }
            try {
                $gwList = Get-NetRoute -DestinationPrefix "0.0.0.0/0" -ErrorAction SilentlyContinue | Where-Object { $_.NextHop -and $_.NextHop -ne "0.0.0.0" } | Select-Object -ExpandProperty NextHop
                foreach ($gw in $gwList) {
                    if ($gw -and -not $gdsCandidateHosts.Contains($gw)) { $gdsCandidateHosts.Add($gw) }
                }
            } catch {}

            # Provera mrežnog upita ako je GDS omogućen
            if ($gdsEnabled) {
                $hasActiveNet = $false
                try {
                    $activeIps = Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue | Where-Object {
                        $_.InterfaceAlias -notmatch 'Loopback' -and $_.IPAddress -notmatch '^169\.254\.'
                    }
                    if ($activeIps) { $hasActiveNet = $true }
                } catch {}

                if ($hasActiveNet) {
                    foreach ($cand in $gdsCandidateHosts) {
                        $baseUrl = ""
                        if ($cand -match '^https?://') {
                            $baseUrl = $cand.TrimEnd('/')
                        } elseif ($cand -match ':[0-9]+$') {
                            $baseUrl = "http://$cand"
                        } else {
                            $baseUrl = "http://${cand}:${gdsPort}"
                        }

                        $apiUrl = "$baseUrl/api/v1/devices/$([System.Uri]::EscapeDataString($targetSerial))/autopilot-status"
                        $respJson = $null

                        try {
                            $req = [System.Net.WebRequest]::Create($apiUrl)
                            $req.Method = "GET"
                            $req.Timeout = 1500
                            if ($req -is [System.Net.HttpWebRequest]) {
                                $req.ReadWriteTimeout = 1500
                            }
                            $resp = $req.GetResponse()
                            $s = $resp.GetResponseStream()
                            $r = New-Object System.IO.StreamReader($s, [System.Text.Encoding]::UTF8)
                            $respJson = $r.ReadToEnd()
                            $r.Close()
                            $resp.Close()
                        } catch {
                            # 404 znači da server postoji ali uređaj nije pronađen u GDS bazi (prekini dalju potragu)
                            if ($_.Exception -and $_.Exception.Response) {
                                try {
                                    $httpResp = [System.Net.HttpWebResponse]$_.Exception.Response
                                    if ([int]$httpResp.StatusCode -eq 404) {
                                        $Global:GdsServerUrl = $baseUrl
                                        $httpResp.Close()
                                        break
                                    }
                                    $httpResp.Close()
                                } catch {}
                            }
                        }

                        if ($respJson) {
                            try {
                                $respObj = $respJson | ConvertFrom-Json
                                if ($respObj -and $respObj.status) {
                                    $st = "$($respObj.status)".ToUpper().Trim()
                                    $tDomain = if ($respObj.tenant_domain) { "$($respObj.tenant_domain)".Trim() } elseif ($respObj.tenant_id) { "$($respObj.tenant_id)".Trim() } else { "" }
                                    switch ($st) {
                                        "LOCKED" {
                                            $apStatus = "Autopilot"
                                            $apPresent = $true
                                            $apTenant = $tDomain
                                            $serverHandled = $true
                                        }
                                        "STALE_MARKER" {
                                            $apStatus = "StaleMarker"
                                            $apPresent = $false
                                            $apTenant = $tDomain
                                            $serverHandled = $true
                                        }
                                        "CLEANED_IN_SESSION" {
                                            $apStatus = "Clean"
                                            $apPresent = $false
                                            $serverHandled = $true
                                        }
                                        "CLEAN" {
                                            $apStatus = "Clean"
                                            $apPresent = $false
                                            $serverHandled = $true
                                        }
                                        default {
                                            $serverHandled = $false
                                        }
                                    }
                                    $Global:GdsServerUrl = $baseUrl
                                    if ($serverHandled) {
                                        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                                            Write-ServisLog "GDS Server Autopilot status za [$targetSerial]: $apStatus ($st, Tenant: $apTenant)" -Level "INFO" -Component "Hardware"
                                        }
                                        break
                                    }
                                }
                            } catch {}
                        }
                    }
                }
            }
        }
    } catch {}

    # 2b. Provera trajno sacuvanog Pre-OOBE Autopilot izvestaja, pa lokalna registarska i dsregcmd provera
    if (-not $serverHandled) {
        try {
            foreach ($apReportPath in @("C:\Windows\Logs\WIS\autopilot_report.json", "C:\setup\autopilot_report.json")) {
                if (Test-Path $apReportPath) {
                    $cachedApRaw = Get-Content -Path $apReportPath -Raw -ErrorAction SilentlyContinue
                    if ($cachedApRaw) {
                        $cachedAp = $cachedApRaw | ConvertFrom-Json -ErrorAction SilentlyContinue
                        if ($cachedAp) {
                            $cachedSer = if ($cachedAp.device_serial) { "$($cachedAp.device_serial)".Trim() } else { "" }
                            $serMatch = (-not $cachedSer) -or ($cachedSer -eq "Nepoznato") -or (-not $targetSerial) -or ($targetSerial -eq "Nepoznato") -or ($cachedSer -ieq $targetSerial)
                            $cachedMc = if ($cachedAp.matrix_code) { "$($cachedAp.matrix_code)".ToUpper().Trim() } else { "" }
                            if ($serMatch -and (($cachedAp.is_locked -eq $true) -or ($cachedMc -in @("LOCKED_TENANT", "HIDDEN_LOCK")))) {
                                $apStatus = "Autopilot"
                                $apPresent = $true
                                $apTenant = if ($cachedAp.tenant_domain) { "$($cachedAp.tenant_domain)".Trim() } elseif ($cachedAp.tenant_id) { "$($cachedAp.tenant_id)".Trim() } else { "" }
                                $serverHandled = $true
                                break
                            }
                        }
                    }
                }
            }
        } catch {}
    }

    if (-not $serverHandled) {
        try {
            $apDiagReg = "HKLM:\SOFTWARE\Microsoft\Provisioning\Diagnostics\AutoPilot"
            $apDiag = Get-ItemProperty -Path $apDiagReg -ErrorAction SilentlyContinue
            $td = if ($apDiag -and $apDiag.CloudAssignedTenantDomain) { $apDiag.CloudAssignedTenantDomain.ToString().Trim() } else { "" }
            $tid = if ($apDiag -and $apDiag.CloudAssignedTenantId) { $apDiag.CloudAssignedTenantId.ToString().Trim() } else { "" }
            $estVal = if ($apDiag) { $apDiag.EstablishedCorrelations } else { $null }
            $hasEst = ($null -ne $estVal) -and ($estVal.ToString().Trim() -ne "")

            $apSetReg = "HKLM:\SOFTWARE\Microsoft\Provisioning\AutopilotSettings"
            $apSet = Get-ItemProperty -Path $apSetReg -ErrorAction SilentlyContinue
            $setTid = if ($apSet -and $apSet.TenantId) { $apSet.TenantId.ToString().Trim() } else { "" }

            $hasZtd = (Test-Path "HKLM:\SOFTWARE\Microsoft\Enrollments\ZTD") -or [bool](Get-ChildItem -Path "HKLM:\SOFTWARE\Microsoft\Enrollments" -ErrorAction SilentlyContinue | Where-Object { $_.PSChildName -match "ZTD" })

            if ($td -ne "" -or $tid -ne "" -or $setTid -ne "" -or $hasEst -or $hasZtd) {
                $apStatus = "Autopilot"
                $apPresent = $true
                if ($td -ne "") {
                    $apTenant = $td
                } elseif ($tid -ne "") {
                    $apTenant = $tid
                } elseif ($setTid -ne "") {
                    $apTenant = $setTid
                }
            } else {
                $dsCmd = if (Test-Path "$env:windir\System32\dsregcmd.exe") { "$env:windir\System32\dsregcmd.exe" } else { "dsregcmd" }
                $dsOut = & $dsCmd /status 2>$null
                $azJoined = [bool]($dsOut -match "AzureAdJoined\s*:\s*YES")
                $mdmJoined = [bool]($dsOut -match "MdmEnrolled\s*:\s*YES")
                $workplaceJoined = [bool]($dsOut -match "WorkplaceJoined\s*:\s*YES")
                if ($azJoined -or $mdmJoined -or $workplaceJoined) {
                    $apStatus = "AadMdm"
                    $apPresent = $false
                }
            }
        } catch {}
    }

    $sbActive = $false
    try {
        $sbActive = Confirm-SecureBootUEFI -ErrorAction SilentlyContinue
    } catch {}

    $blActive = $false
    try {
        $bl = Get-BitLockerVolume -MountPoint "C:" -ErrorAction SilentlyContinue
        if ($bl -and $bl.ProtectionStatus -eq 'On') {
            $blActive = $true
        }
    } catch {}

    # 3. Procesor (CPU)
    $cpu = Get-CimInstance Win32_Processor -ErrorAction SilentlyContinue | Select-Object -First 1
    $cpuName = if ($cpu.Name) {
        ($cpu.Name -replace '\(R\)|\(TM\)|Processor|CPU', '' -replace 'with\s+.*Graphics', '' -replace '\s+', ' ').Trim()
    } else {
        "Standard CPU"
    }
    $cores = if ($cpu.NumberOfCores) { $cpu.NumberOfCores } else { 1 }
    $threads = if ($cpu.NumberOfLogicalProcessors) { $cpu.NumberOfLogicalProcessors } else { $cores }
    $ghz = if ($cpu.MaxClockSpeed) { [math]::Round(($cpu.MaxClockSpeed / 1000.0), 2) } else { 0.0 }
    $cpuStr = if ($ghz -gt 0) { "$cpuName ($cores C / $threads T @ $ghz GHz)" } else { "$cpuName ($cores C / $threads T)" }

    # 4. RAM memorija
    $memModules = Get-CimInstance Win32_PhysicalMemory -ErrorAction SilentlyContinue
    $ramTotalGB = 0.0
    $ramType = "DDR4"
    $ramSpeed = 0
    $ramChannel = "Single-Channel"
    $modCount = 0

    if ($memModules) {
        $modList = @($memModules)
        $modCount = $modList.Count
        $sumBytes = 0.0
        foreach ($m in $modList) { $sumBytes += [double]$m.Capacity }
        $ramTotalGB = [math]::Round(($sumBytes / 1GB), 1)
        if ($modList[0].Speed) { $ramSpeed = [int]$modList[0].Speed }
        elseif ($modList[0].ConfiguredClockSpeed) { $ramSpeed = [int]$modList[0].ConfiguredClockSpeed }
        
        $smType = $modList[0].SMBIOSMemoryType
        $ramType = switch ($smType) {
            20 { "DDR" }
            21 { "DDR2" }
            24 { "DDR3" }
            26 { "DDR4" }
            30 { "DDR5" }
            34 { "LPDDR5" }
            default {
                if ($ramSpeed -ge 4800) { "DDR5" }
                elseif ($ramSpeed -ge 2133) { "DDR4" }
                elseif ($ramSpeed -ge 1333) { "DDR3" }
                else { "RAM" }
            }
        }
        $ramChannel = if ($modCount -ge 2) { "Dual-Channel ($modCount modula)" } else { "Single-Channel (1 modul)" }
    } else {
        if ($sys.TotalPhysicalMemory) {
            $ramTotalGB = [math]::Round(([double]$sys.TotalPhysicalMemory / 1GB), 1)
        }
    }
    $ramSpeedStr = if ($ramSpeed -gt 0) { " $ramSpeed MHz" } else { "" }
    $ramStr = "$ramTotalGB GB $ramType$ramSpeedStr ($ramChannel)"

    # 5. Disk / Skladiste (Primarni NVMe / SATA SSD)
    $diskStr = "Nije detektovan"
    try {
        $pDisks = Get-PhysicalDisk -ErrorAction SilentlyContinue | Where-Object { $_.BusType -ne 'USB' -and $_.MediaType -ne 'Removable' }
        if (-not $pDisks) {
            $pDisks = Get-PhysicalDisk -ErrorAction SilentlyContinue
        }
        if ($pDisks) {
            $primary = $pDisks | Sort-Object -Property @{Expression={ if ($_.BusType -eq 'NVMe') { 0 } else { 1 } }}, Size -Descending | Select-Object -First 1
            $dModel = ($primary.FriendlyName -replace '\s+', ' ').Trim()
            $dBus = if ($primary.BusType -and $primary.BusType -ne 'Unknown') { "$($primary.BusType) " } else { "" }
            $dMedia = if ($primary.MediaType -match 'SSD|NVMe') { "SSD" } elseif ($primary.MediaType -match 'HDD') { "HDD" } else { "" }
            $dSizeGB = [math]::Round(([double]$primary.Size / 1GB), 0)
            $dCap = if ($dSizeGB -ge 900 -and $dSizeGB -le 1100) { "1TB" }
                    elseif ($dSizeGB -ge 1800 -and $dSizeGB -le 2200) { "2TB" }
                    elseif ($dSizeGB -ge 450 -and $dSizeGB -le 550) { "512GB" }
                    elseif ($dSizeGB -ge 220 -and $dSizeGB -le 275) { "256GB" }
                    elseif ($dSizeGB -ge 110 -and $dSizeGB -le 140) { "128GB" }
                    else { "$dSizeGB GB" }
            $dHealth = if ($primary.HealthStatus) { " [$($primary.HealthStatus)]" } else { "" }
            $diskStr = "$dModel $dCap $dBus$dMedia$dHealth".Trim()
        } else {
            $dDrive = Get-CimInstance Win32_DiskDrive -ErrorAction SilentlyContinue | Where-Object { $_.InterfaceType -ne 'USB' } | Select-Object -First 1
            if ($dDrive) {
                $sz = [math]::Round(([double]$dDrive.Size / 1GB), 0)
                $diskStr = "$($dDrive.Model) ${sz}GB ($($dDrive.InterfaceType))"
            }
        }
    } catch {}

    # 6. Grafika (GPU 1 & GPU 2)
    $gpu1Str = "Integrisana grafika"
    $gpu2Str = "Nema diskretne grafike"
    try {
        $videoControllers = Get-CimInstance Win32_VideoController -ErrorAction SilentlyContinue | Where-Object { $_.Name -notmatch 'Remote|Virtual|Basic Display' }
        if (-not $videoControllers) {
            $videoControllers = Get-CimInstance Win32_VideoController -ErrorAction SilentlyContinue
        }
        $vList = @()
        foreach ($vc in $videoControllers) {
            $vName = ($vc.Name -replace '\s+', ' ').Trim()
            $vramStr = ""
            if ($vc.AdapterRAM -gt 1048576) {
                $vramVal = [math]::Round(($vc.AdapterRAM / 1GB), 0)
                if ($vramVal -gt 0) { $vramStr = " (${vramVal} GB)" }
            }
            $vList += "$vName$vramStr"
        }
        if ($vList.Count -ge 1) { $gpu1Str = $vList[0] }
        if ($vList.Count -ge 2) { $gpu2Str = $vList[1] }
    } catch {}

    # 7. Ekran / Panel (EDID očitavanje, matrica, nits, sRGB/NTSC gamut)
    $dispStr = "Nije detektovan"
    $dispModel = "Standardni panel"
    $dispNits = 250
    $dispSrgb = 65
    $dispNtsc = 45
    $dispGamut = "65% sRGB (45% NTSC)"
    $panelType = "Standard"

    try {
        $vc1 = Get-CimInstance Win32_VideoController -ErrorAction SilentlyContinue | Select-Object -First 1
        $w = $vc1.CurrentHorizontalResolution
        $h = $vc1.CurrentVerticalResolution
        $hz = $vc1.CurrentRefreshRate

        # Čitanje sirovog EDID bloka iz Registra
        $edidBytes = $null
        try {
            $regDisplay = Get-ItemProperty -Path "HKLM:\SYSTEM\CurrentControlSet\Enum\DISPLAY\*\*\Device Parameters" -Name "EDID" -ErrorAction SilentlyContinue
            if ($regDisplay) {
                foreach ($rd in $regDisplay) {
                    if ($rd.EDID -and $rd.EDID.Length -ge 128) {
                        if ($rd.EDID[0] -eq 0 -and $rd.EDID[1] -eq 255 -and $rd.EDID[2] -eq 255 -and $rd.EDID[7] -eq 0) {
                            $edidBytes = $rd.EDID
                            break
                        }
                    }
                }
            }
        } catch {}

        # Ekstrakcija naziva matrice iz EDID deskriptor blokova (ofseti 54, 72, 90, 108)
        if ($edidBytes -and $edidBytes.Length -ge 128) {
            $descOffsets = @(54, 72, 90, 108)
            foreach ($off in $descOffsets) {
                if ($off + 18 -le $edidBytes.Length) {
                    # Tag 0xFC = Display Product Name
                    if ($edidBytes[$off] -eq 0 -and $edidBytes[$off + 1] -eq 0 -and $edidBytes[$off + 2] -eq 0 -and $edidBytes[$off + 3] -eq 0xFC) {
                        $charBytes = @()
                        for ($idx = $off + 5; $idx -lt $off + 18; $idx++) {
                            $b = $edidBytes[$idx]
                            if ($b -eq 0x0A -or $b -eq 0x00) { break }
                            if ($b -ge 32 -and $b -le 126) { $charBytes += [char]$b }
                        }
                        $extractedName = (-join $charBytes).Trim()
                        if ($extractedName) {
                            $dispModel = $extractedName
                            break
                        }
                    } elseif ($dispModel -eq "Standardni panel" -and $edidBytes[$off] -eq 0 -and $edidBytes[$off + 1] -eq 0 -and $edidBytes[$off + 2] -eq 0 -and $edidBytes[$off + 3] -eq 0xFE) {
                        $charBytes = @()
                        for ($idx = $off + 5; $idx -lt $off + 18; $idx++) {
                            $b = $edidBytes[$idx]
                            if ($b -eq 0x0A -or $b -eq 0x00) { break }
                            if ($b -ge 32 -and $b -le 126) { $charBytes += [char]$b }
                        }
                        $extractedName = (-join $charBytes).Trim()
                        if ($extractedName) { $dispModel = $extractedName }
                    }
                }
            }

            # Proračun CIE xy hromatskih koordinata iz bajtova 25-34
            try {
                $b25 = [int]$edidBytes[25]
                $b26 = [int]$edidBytes[26]
                $rxRaw = ([int]$edidBytes[27] -shl 2) -bor (($b25 -shr 6) -band 0x03)
                $ryRaw = ([int]$edidBytes[28] -shl 2) -bor (($b25 -shr 4) -band 0x03)
                $gxRaw = ([int]$edidBytes[29] -shl 2) -bor (($b25 -shr 2) -band 0x03)
                $gyRaw = ([int]$edidBytes[30] -shl 2) -bor ($b25 -band 0x03)
                $bxRaw = ([int]$edidBytes[31] -shl 2) -bor (($b26 -shr 6) -band 0x03)
                $byRaw = ([int]$edidBytes[32] -shl 2) -bor (($b26 -shr 4) -band 0x03)

                $rX = $rxRaw / 1024.0; $rY = $ryRaw / 1024.0
                $gX = $gxRaw / 1024.0; $gY = $gyRaw / 1024.0
                $bX = $bxRaw / 1024.0; $bY = $byRaw / 1024.0

                # Shoelace formula: 0.5 * |rx*(gy-by) + gx*(by-ry) + bx*(ry-gy)|
                $area = 0.5 * [math]::Abs($rX * ($gY - $bY) + $gX * ($bY - $rY) + $bX * ($rY - $gY))
                if ($area -gt 0.02 -and $area -lt 0.25) {
                    $dispNtsc = [int][math]::Round(($area / 0.1582) * 100.0)
                    $dispSrgb = [int][math]::Round(($area / 0.1118) * 100.0)
                }
            } catch {}
        }

        # Fallback za naziv preko WmiMonitorID ako nije pronađen u EDID
        if ($dispModel -eq "Standardni panel") {
            try {
                $wmiMon = Get-CimInstance -Namespace root\wmi -ClassName WmiMonitorID -ErrorAction SilentlyContinue | Select-Object -First 1
                if ($wmiMon) {
                    $fn = ($wmiMon.UserFriendlyName | Where-Object { $_ -gt 0 } | ForEach-Object { [char]$_ }) -join ''
                    $pc = ($wmiMon.ProductCodeID | Where-Object { $_ -gt 0 } | ForEach-Object { [char]$_ }) -join ''
                    if ($fn -and $fn.Trim()) { $dispModel = $fn.Trim() }
                    elseif ($pc -and $pc.Trim()) { $dispModel = $pc.Trim() }
                }
            } catch {}
        }

        # Nits osvetljenje i heuristika
        if ($dispModel -match '(?i)OLED|AMOLED') {
            $panelType = "OLED"
            $dispNits = 400
            if ($dispSrgb -le 65) { $dispSrgb = 100; $dispNtsc = 100 }
        } elseif ($dispModel -match '(?i)HAN|HCE|NV1|LP1|B140|B156|N140|N156|IPS') {
            $panelType = "IPS"
            if ($dispSrgb -ge 90) { $dispNits = 350 } else { $dispNits = 300 }
        } elseif ($dispSrgb -ge 95) {
            $panelType = "IPS"
            $dispNits = 350
        } elseif ($dispSrgb -ge 70) {
            $panelType = "IPS"
            $dispNits = 300
        } else {
            $panelType = if ($w -ge 1920) { "IPS" } else { "TN / Standard" }
            $dispNits = 250
        }

        $dispGamut = "$dispSrgb% sRGB ($dispNtsc% NTSC)"

        $diagStr = ""
        $mon = Get-CimInstance -Namespace root\wmi -ClassName WmiMonitorBasicDisplayParams -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($mon -and $mon.MaxHorizontalImageSize -gt 0 -and $mon.MaxVerticalImageSize -gt 0) {
            $wCm = [double]$mon.MaxHorizontalImageSize
            $hCm = [double]$mon.MaxVerticalImageSize
            $dInch = [math]::Round(([math]::Sqrt([math]::Pow($wCm, 2) + [math]::Pow($hCm, 2)) / 2.54), 1)
            if ($dInch -ge 15.3 -and $dInch -le 15.8) { $diagStr = '15.6" ' }
            elseif ($dInch -ge 17.0 -and $dInch -le 17.5) { $diagStr = '17.3" ' }
            elseif ($dInch -ge 15.9 -and $dInch -le 16.3) { $diagStr = '16.0" ' }
            elseif ($dInch -ge 13.8 -and $dInch -le 14.3) { $diagStr = '14.0" ' }
            elseif ($dInch -ge 13.0 -and $dInch -le 13.6) { $diagStr = '13.3" ' }
            elseif ($dInch -gt 0) { $diagStr = "$dInch`" " }
        }

        $tag = switch ("$w`x$h") {
            '1920x1080' { 'FHD' }
            '1920x1200' { 'FHD+' }
            '2560x1440' { 'QHD' }
            '2560x1600' { 'QHD+' }
            '3840x2160' { '4K UHD' }
            '1366x768'  { 'HD' }
            default     { if ($w -and $h) { "$w`x$h" } else { "Panel" } }
        }
        $hzStr = if ($hz -and $hz -gt 0) { " @ ${hz}Hz" } else { "" }
        $resPart = if ($w -and $h) { " ($w`x$h$hzStr)" } else { "" }
        $dispStr = "$dispModel • $diagStr$tag$resPart • $dispNits nits • $dispGamut • $panelType".Trim()
    } catch {}

    # 8. Baterija & Napajanje
    $batStr = "Nema baterije (Desktop / AC Only)"
    $pwrStr = "AC status nepoznat"
    try {
        $bat = Get-CimInstance Win32_Battery -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($bat) {
            $pct = $bat.EstimatedChargeRemaining
            $bStatus = switch ($bat.BatteryStatus) {
                1 { "Prazni se" }
                2 { "Na punjacu" }
                3 { "Puna" }
                default { "Aktivna" }
            }
            $healthStr = ""
            try {
                $bFull = (Get-CimInstance -Namespace root\wmi -ClassName BatteryFullChargedCapacity -ErrorAction SilentlyContinue | Select-Object -First 1).FullChargedCapacity
                $bStatic = (Get-CimInstance -Namespace root\wmi -ClassName BatteryStaticData -ErrorAction SilentlyContinue | Select-Object -First 1).DesignedCapacity
                if ($bFull -gt 0 -and $bStatic -gt 0) {
                    $hPct = [math]::Round(($bFull / $bStatic) * 100.0, 0)
                    $healthStr = " | Zdravlje: $hPct%"
                }
            } catch {}
            $batStr = "$pct% ($bStatus$healthStr)"
            $pwrStr = if ($bat.BatteryStatus -eq 2 -or $bat.BatteryStatus -eq 3) { "AC Adapter Online (Mrezno)" } else { "Baterijsko napajanje" }
        } else {
            $pwrStr = "AC Adapter Online (Fiksno napajanje)"
        }
    } catch {}

    # 9. Periferije (Wi-Fi, Bluetooth, Audio, Mikrofon, Kamera, IR, Otisak prsta, Svetleća tastatura)
    $wifiName = "Nije detektovan"
    $wifiOk = $false
    try {
        $wAdapters = Get-NetAdapter -ErrorAction SilentlyContinue | Where-Object { $_.InterfaceDescription -match 'Wi-Fi|Wireless|802.11' -or $_.Name -match 'Wi-Fi' }
        if ($wAdapters) {
            $wa = $wAdapters | Select-Object -First 1
            $wifiName = ($wa.InterfaceDescription -replace 'Wi-Fi Adapter|Wireless-AC|Network Adapter', '' -replace '\s+', ' ').Trim()
            $wifiOk = $wa.Status -eq 'Up' -or $wa.Status -eq 'Connected' -or $wa.Status -eq 'Disconnected'
        }
    } catch {}

    $btName = "Nije detektovan"
    $btOk = $false
    try {
        $bt = Get-PnpDevice -Class Bluetooth -Status OK -ErrorAction SilentlyContinue | Where-Object { $_.FriendlyName -notmatch 'Enumerator|Device \(RFCOMM' } | Select-Object -First 1
        if ($bt) {
            $btName = ($bt.FriendlyName -replace '\s+', ' ').Trim()
            $btOk = $true
        }
    } catch {}

    # Audio zvučnici i mikrofon (HW-07 - AudioEndpoint / SoundDevice)
    $sndName = "Standardni Audio"
    $sndOk = $false
    $micName = "Nije detektovan"
    $hasMic = $false
    try {
        $endpoints = Get-CimInstance Win32_PnPEntity -ErrorAction SilentlyContinue | Where-Object { $_.PNPClass -eq 'AudioEndpoint' }
        if ($endpoints) {
            foreach ($ep in $endpoints) {
                $epName = if ($ep.Name) { ($ep.Name -replace '\s+', ' ').Trim() } else { "" }
                $epDevId = if ($ep.DeviceID) { $ep.DeviceID } else { "" }
                if ($epDevId -match '(?i)Render' -or $epName -match '(?i)Speakers|Zvučnici') {
                    if ($sndName -eq "Standardni Audio" -and $epName) {
                        $sndName = $epName
                        $sndOk = $true
                    }
                } elseif ($epDevId -match '(?i)Capture' -or $epName -match '(?i)Microphone|Mikrofon') {
                    if ($micName -eq "Nije detektovan" -and $epName) {
                        $micName = $epName
                        $hasMic = $true
                    }
                }
            }
        }
        if (-not $sndOk) {
            $snd = Get-CimInstance Win32_SoundDevice -ErrorAction SilentlyContinue | Where-Object { $_.Status -eq 'OK' } | Select-Object -First 1
            if ($snd) {
                $sndName = ($snd.Name -replace '\s+', ' ').Trim()
                $sndOk = $true
            }
        }
    } catch {}

    # Web kamera
    $camName = "Nije ugradjena"
    $camOk = $false
    try {
        $cam = Get-PnpDevice -Class Camera, Image -Status OK -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($cam) {
            $camName = ($cam.FriendlyName -replace '\s+', ' ').Trim()
            $camOk = $true
        }
    } catch {}

    # IR Kamera (HW-04 - eliminacija FaceLogon i virtuelnih softverskih uredjaja)
    $hasIrCamera = $false
    $irCameraName = "Nije detektovana"
    try {
        $irPnpList = Get-CimInstance Win32_PnPEntity -ErrorAction SilentlyContinue | Where-Object {
            ($_.Name -match '(?i)\b(IR Camera|Infrared|Windows Hello Face|IR Sensor)\b' -or $_.HardwareID -match '(?i)IRCamera') -and
            $_.DeviceID -notmatch '(?i)^SWC\\|MS_FACE_AUTH|^ROOT\\' -and
            $_.Name -notmatch '(?i)Software|Virtual' -and
            ([int]$_.ConfigManagerErrorCode -eq 0)
        }
        if ($irPnpList) {
            $irDev = $irPnpList | Select-Object -First 1
            $hasIrCamera = $true
            $irCameraName = ($irDev.Name -replace '\s+', ' ').Trim()
        }
    } catch {}

    # Čitač otiska prsta (HW-05 - eliminacija Hello Face softverskih uredjaja)
    $hasFp = $false
    $fpName = "Nije detektovan"
    try {
        $bioList = Get-CimInstance Win32_PnPEntity -ErrorAction SilentlyContinue | Where-Object {
            $_.PNPClass -eq 'Biometric' -and
            $_.Name -notmatch '(?i)Face|Software|Virtual' -and
            $_.DeviceID -notmatch '(?i)MS_FACE|^ROOT\\' -and
            ([int]$_.ConfigManagerErrorCode -eq 0)
        }
        if ($bioList) {
            $bioDev = $bioList | Select-Object -First 1
            $hasFp = $true
            $fpName = ($bioDev.Name -replace '\s+', ' ').Trim()
        }
    } catch {}

    # Svetleća tastatura (HW-06 - OEM WMI upiti i trostepeni status za laptopove)
    $hasBacklit = $false
    $backlitStatus = "Standardna (Bez osvetljenja)"
    try {
        # 1. PnP pretraga
        $kbPnp = Get-CimInstance Win32_PnPEntity -ErrorAction SilentlyContinue | Where-Object {
            ($_.Name -match '(?i)Backlight|Illuminated|RGB Keyboard' -or
             $_.Service -match '(?i)KbdBacklight|Backlight' -or
             $_.DeviceID -match '(?i)DELLABCE|LEN0071')
        }
        if ($kbPnp) {
            $hasBacklit = $true
            $backlitStatus = "Da (Pozadinsko osvetljenje)"
        }

        # 2. Lenovo WMI
        if (-not $hasBacklit) {
            try {
                $lenovoKb = Get-CimInstance -Namespace root\WMI -ClassName Lenovo_SetKeyboardBacklightStatus -ErrorAction SilentlyContinue
                if ($lenovoKb) {
                    $hasBacklit = $true
                    $backlitStatus = "Da (Pozadinsko osvetljenje)"
                }
            } catch {}
        }

        # 3. Dell WMI
        if (-not $hasBacklit) {
            try {
                $dellKb = Get-CimInstance -Namespace root\dcim\sysman -ClassName DCIM_BiosEnumeration -Filter "AttributeName LIKE '%Keyboard%Backlight%'" -ErrorAction SilentlyContinue
                if ($dellKb) {
                    $hasBacklit = $true
                    $backlitStatus = "Da (Pozadinsko osvetljenje)"
                }
            } catch {}
        }

        # 4. HP WMI
        if (-not $hasBacklit) {
            try {
                $hpKb = Get-CimInstance -Namespace root\hp\instrumentedBIOS -ClassName HP_BIOSSetting -Filter "Name LIKE '%Backlit%'" -ErrorAction SilentlyContinue
                if ($hpKb) {
                    $hasBacklit = $true
                    $backlitStatus = "Da (Pozadinsko osvetljenje)"
                }
            } catch {}
        }

        # Trostepeni fallback za laptopove gde WMI nije izložen
        $isLap = (Test-BatteryPresent)
        if (-not $hasBacklit -and $isLap) {
            $backlitStatus = "Standardna / Proveri ručno (Fn+Space)"
        }
    } catch {}

    # 10. Zdravstveni status hardvera (SSD i Baterija)
    $hwHealth = $null
    try {
        $hwHealth = Get-HardwareHealthStatus
    } catch {}

    # 11. TPM 2.0 status
    $tpmPresent = $false
    $tpmReady = $false
    try {
        $tpm = Get-Tpm -ErrorAction SilentlyContinue
        if ($tpm) {
            $tpmPresent = [bool]$tpm.TpmPresent
            $tpmReady = [bool]$tpm.TpmReady
        }
    } catch {}

    # 12. PnP Problematic Devices (drajveri koji fale ili imaju zuti uzvicnik)
    $pnpProblems = @()
    try {
        $problemList = Get-CimInstance Win32_PnPEntity -ErrorAction SilentlyContinue | Where-Object { $_.ConfigManagerErrorCode -and $_.ConfigManagerErrorCode -ne 0 }
        if ($problemList) {
            foreach ($pd in $problemList) {
                $errCode = [int]$pd.ConfigManagerErrorCode
                $errDesc = switch ($errCode) {
                    1 { "Uredjaj nije pravilno konfigurisan (Kod 1)" }
                    10 { "Uredjaj ne moze da se pokrene (Kod 10)" }
                    18 { "Potrebna reinstalacija drajvera (Kod 18)" }
                    28 { "Drajver nije instaliran (Kod 28)" }
                    39 { "Drajver je ostecen ili nedostaje (Kod 39)" }
                    43 { "Uredjaj zaustavljen zbog greske (Kod 43)" }
                    default { "Greska drajvera (Kod $errCode)" }
                }
                $pnpProblems += [PSCustomObject]@{
                    Name        = if ($pd.Name) { $pd.Name.Trim() } else { "Nepoznat uredjaj" }
                    DeviceID    = if ($pd.DeviceID) { $pd.DeviceID.Trim() } else { "" }
                    ErrorCode   = $errCode
                    ErrorStatus = $errDesc
                }
            }
        }
    } catch {}

    $hwDiagResult = [PSCustomObject]@{
        Manufacturer     = $mfg
        Model            = $model
        SerialNumber     = $serial
        BiosVersion      = $biosVer
        BiosDate         = $biosDate
        BoardName        = $boardName
        OSName           = $osCaption
        OSBuild          = $osBuild
        OSArch           = $osArch
        AutopilotStatus  = $apStatus
        Autopilot        = $apPresent
        AutopilotTenant  = $apTenant
        SecureBoot       = $sbActive
        BitLocker        = $blActive
        CpuStr           = $cpuStr
        RamStr           = $ramStr
        DiskStr          = $diskStr
        Gpu1Str          = $gpu1Str
        Gpu2Str          = $gpu2Str
        DisplayStr       = $dispStr
        DisplayModel     = $dispModel
        DisplayNits      = $dispNits
        DisplaySrgb      = $dispSrgb
        DisplayNtsc      = $dispNtsc
        DisplayGamut     = $dispGamut
        PanelType        = $panelType
        SpeakersName     = $sndName
        MicName          = $micName
        HasMicrophone    = $hasMic
        HasIrCamera      = $hasIrCamera
        IrCameraName     = $irCameraName
        HasFingerprint   = $hasFp
        FingerprintName  = $fpName
        HasBacklitKb     = $hasBacklit
        BacklitKbStatus  = $backlitStatus
        BatteryStr       = $batStr
        PowerStr         = $pwrStr
        WifiName         = $wifiName
        WifiOk           = $wifiOk
        BtName           = $btName
        BtOk             = $btOk
        SoundName        = $sndName
        SoundOk          = $sndOk
        CamName          = $camName
        CamOk            = $camOk
        HealthStatus     = if ($hwHealth) { $hwHealth.OverallStatus } else { "OK" }
        DiskHealth       = if ($hwHealth) { $hwHealth.DiskHealth } else { $null }
        BatteryHealth    = if ($hwHealth) { $hwHealth.BatteryHealth } else { $null }
        HardwareHealth   = $hwHealth
        PowerOnHours     = if ($hwHealth -and ($null -ne $hwHealth.PowerOnHours)) { $hwHealth.PowerOnHours } else { $null }
        IsSmartEstimated = if ($hwHealth -and $hwHealth.IsSmartEstimated) { $true } else { $false }
        TpmPresent       = $tpmPresent
        TpmReady         = $tpmReady
        PnpProblems      = $pnpProblems
        PnpProblemCount  = [int]$pnpProblems.Count
    }

    $Global:HwInfo = $hwDiagResult
    return $hwDiagResult
}

function Test-BatteryPresent {
    try {
        $battery = Get-CimInstance Win32_Battery -ErrorAction SilentlyContinue
        if ($battery) { return $true }
        $enclosure = Get-CimInstance Win32_SystemEnclosure -ErrorAction SilentlyContinue
        $types = if ($enclosure -and $enclosure.ChassisTypes) { @($enclosure.ChassisTypes) } else { @() }
        $laptopChassisCodes = @(8, 9, 10, 14, 30, 31, 32)
        foreach ($t in $types) {
            if ($laptopChassisCodes -contains [int]$t) {
                return $true
            }
        }
    } catch {}
    return $false
}

function Get-MissingHardwareCount {
    try {
        $errDevs = Get-PnpDevice -PresentOnly -ErrorAction SilentlyContinue | Where-Object {
            $_.ConfigManagerErrorCode -ne 0 -and
            $_.ConfigManagerErrorCode -notin @(22, 45) -and
            $_.Class -notin @('Printer', 'PrintQueue', 'SoftwareDevice') -and
            $_.InstanceId -notmatch '^(ROOT|SWD)\\' -and
            ($_.FriendlyName -notmatch 'Volume Snapshot')
        }
        return @($errDevs).Count
    } catch { return 0 }
}

function Get-PnpMissingDriversDetail {
    $missingList = [System.Collections.Generic.List[PSCustomObject]]::new()
    try {
        $pnpDevs = Get-PnpDevice -PresentOnly -ErrorAction SilentlyContinue | Where-Object {
            $_.ConfigManagerErrorCode -ne 0 -and
            $_.ConfigManagerErrorCode -notin @(22, 45) -and
            $_.Class -notin @('Printer', 'PrintQueue', 'SoftwareDevice') -and
            $_.InstanceId -notmatch '^(ROOT|SWD)\\' -and
            ($_.FriendlyName -notmatch 'Volume Snapshot')
        }
        if ($pnpDevs) {
            foreach ($dev in $pnpDevs) {
                $hwIds = @()
                try {
                    $prop = Get-PnpDeviceProperty -InstanceId $dev.InstanceId -KeyName 'DEVPKEY_Device_HardwareIds' -ErrorAction SilentlyContinue
                    if ($prop -and $prop.Data) { $hwIds = @($prop.Data) }
                } catch {}
                
                $missingList.Add([PSCustomObject]@{
                    InstanceId             = $dev.InstanceId
                    FriendlyName           = if ($dev.FriendlyName) { $dev.FriendlyName } else { "Nepoznat uredjaj" }
                    Class                  = if ($dev.Class) { $dev.Class } else { "Unknown" }
                    Status                 = $dev.Status
                    ConfigManagerErrorCode = $dev.ConfigManagerErrorCode
                    HardwareIDs            = ($hwIds -join '; ')
                })
            }
        }
    } catch {
        try {
            $cimDevs = Get-CimInstance Win32_PnPEntity -ErrorAction SilentlyContinue | Where-Object {
                $_.ConfigManagerErrorCode -and
                $_.ConfigManagerErrorCode -notin @(0, 22, 45) -and
                $_.PNPClass -notin @('Printer', 'PrintQueue', 'SoftwareDevice') -and
                $_.DeviceID -notmatch '^(ROOT|SWD)\\'
            }
            if ($cimDevs) {
                foreach ($cdev in $cimDevs) {
                    $missingList.Add([PSCustomObject]@{
                        InstanceId             = $cdev.DeviceID
                        FriendlyName           = if ($cdev.Name) { $cdev.Name } else { if ($cdev.Description) { $cdev.Description } else { "Nepoznat uredjaj" } }
                        Class                  = if ($cdev.PNPClass) { $cdev.PNPClass } else { "Unknown" }
                        Status                 = $cdev.Status
                        ConfigManagerErrorCode = $cdev.ConfigManagerErrorCode
                        HardwareIDs            = if ($cdev.HardwareID) { ($cdev.HardwareID -join '; ') } else { "" }
                    })
                }
            }
        } catch {}
    }
    return $missingList
}

function Get-HardwareBrand {
    param($HwInfo)
    if (-not $HwInfo) { return "USER" }
    $raw = $HwInfo.Manufacturer
    if (-not $raw -or $raw -match 'To Be Filled|System manufacturer|innotek|VMware|QEMU') {
        $raw = $HwInfo.BoardName
    }
    $brand = switch -Regex ($raw) {
        'Dell'          { 'DELL' }
        'HP|Hewlett'    { 'HP' }
        'ASUS|ASUSTeK'  { 'ASUS' }
        'Lenovo'        { 'LENOVO' }
        'Acer'          { 'ACER' }
        'MSI|Micro-Star'{ 'MSI' }
        'Gigabyte'      { 'GIGABYTE' }
        'Toshiba|Dynabook' { 'TOSHIBA' }
        'Samsung'       { 'SAMSUNG' }
        'Sony'          { 'SONY' }
        'Apple'         { 'APPLE' }
        default {
            $c = ($raw -replace '[^a-zA-Z0-9]', ' ').Trim().Split(' ')[0].ToUpper()
            if ($c.Length -ge 2) { $c } else { 'USER' }
        }
    }
    return $brand
}

function Get-HardwareHealthStatus {
    <#
    .SYNOPSIS
        Ocitava zdravstveni status primarnog diska i baterije racunara za WIS (Windows Installation Assistant).
    .DESCRIPTION
        Kombinuje WMI/CIM klase (MSStorageDriver_FailurePredictStatus, Get-PhysicalDisk, Win32_Battery)
        i smartctl.exe dijagnostiku (NVMe wear percentage, TBW, realocirani sektori za ATA/SATA diskove).
        Kategorizuje celokupno stanje u OK, WARNING ili CRITICAL prema definisanim servisnim pragovima:
          - WARNING: SSD Health < 80% ili Baterija < 60%.
          - CRITICAL: SSD Health < 50%, realocirani losi sektori > 0 ili CriticalWarning != 0 (ili failure predict).
    #>
    $ssdHealth = $null
    $reallocatedSectors = 0
    $criticalWarning = 0
    $tbw = $null
    $diskModel = ""
    $failurePredict = $false
    $diskHealthStatus = "Healthy"
    $powerOnHours = $null
    $isSmartEstimated = $false

    # 1. Provera diska preko CIM / WMI
    try {
        $fps = Get-CimInstance -Namespace root\wmi -ClassName MSStorageDriver_FailurePredictStatus -ErrorAction SilentlyContinue
        if ($fps) {
            foreach ($fp in $fps) {
                if ($fp.PredictFailure -eq $true) {
                    $failurePredict = $true
                }
            }
        }
    } catch {}

    try {
        $pDisks = Get-PhysicalDisk -ErrorAction SilentlyContinue | Where-Object { $_.BusType -ne 'USB' -and $_.MediaType -ne 'Removable' }
        if (-not $pDisks) {
            $pDisks = Get-PhysicalDisk -ErrorAction SilentlyContinue
        }
        if ($pDisks) {
            $primary = $pDisks | Sort-Object -Property @{Expression={ if ($_.BusType -eq 'NVMe') { 0 } else { 1 } }}, Size -Descending | Select-Object -First 1
            $diskModel = ($primary.FriendlyName -replace '\s+', ' ').Trim()
            if ($primary.HealthStatus) {
                $diskHealthStatus = "$($primary.HealthStatus)"
            }
        } else {
            $dDrive = Get-CimInstance Win32_DiskDrive -ErrorAction SilentlyContinue | Where-Object { $_.InterfaceType -ne 'USB' } | Select-Object -First 1
            if ($dDrive) {
                $diskModel = ($dDrive.Model -replace '\s+', ' ').Trim()
                if ($dDrive.Status -and $dDrive.Status -ne "OK") {
                    $diskHealthStatus = "Warning"
                }
            }
        }
    } catch {}

    # 2. smartctl.exe proveravanje ako je dostupan na disku
    $smartCandidates = @(
        "C:\setup\Programi\Diagnostics\smartctl.exe",
        (Join-Path $PSScriptRoot "..\..\Programi\Diagnostics\smartctl.exe"),
        (Join-Path $PSScriptRoot "..\..\Programi\smartctl.exe"),
        "C:\setup\Programi\smartctl.exe"
    )
    if ($Global:SourceRoot) {
        $smartCandidates += (Join-Path $Global:SourceRoot "Programi\Diagnostics\smartctl.exe")
        $smartCandidates += (Join-Path $Global:SourceRoot "Programi\smartctl.exe")
    }

    $smartctlExe = $smartCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $smartctlExe) {
        $cmdSmart = Get-Command smartctl.exe -ErrorAction SilentlyContinue
        if ($cmdSmart) { $smartctlExe = $cmdSmart.Source }
    }

    $smartParsed = $false
    if ($smartctlExe) {
        try {
            $devicesToQuery = [System.Collections.Generic.List[hashtable]]::new()
            $scanOut = & $smartctlExe --scan -j 2>$null
            if ($scanOut) {
                try {
                    $scanJson = ($scanOut -join "`n") | ConvertFrom-Json
                    if ($scanJson -and $scanJson.devices) {
                        foreach ($d in $scanJson.devices) {
                            $devicesToQuery.Add(@{ Name = $d.name; Type = $d.type })
                        }
                    }
                } catch {}
            }
            if ($devicesToQuery.Count -eq 0) {
                $devicesToQuery.Add(@{ Name = "/dev/nvme0"; Type = "nvme" })
                $devicesToQuery.Add(@{ Name = "/dev/sda"; Type = "ata" })
                $devicesToQuery.Add(@{ Name = "/dev/csmi0,0"; Type = "csmi" })
            }

            foreach ($dev in $devicesToQuery) {
                $smartArgs = @("-j", "-a", $dev.Name)
                if ($dev.Type) { $smartArgs += @("-d", $dev.Type) }
                $devOut = & $smartctlExe $smartArgs 2>$null
                if ($devOut) {
                    try {
                        $smartObj = ($devOut -join "`n") | ConvertFrom-Json
                        if ($smartObj) {
                            # NVMe S.M.A.R.T. podaci
                            if ($smartObj.nvme_smart_health_information_log) {
                                $nvmeLog = $smartObj.nvme_smart_health_information_log
                                if ($null -ne $nvmeLog.percentage_used) {
                                    $used = [int]$nvmeLog.percentage_used
                                    $ssdHealth = [math]::Max(0, 100 - $used)
                                    $smartParsed = $true
                                }
                                if ($null -ne $nvmeLog.critical_warning) {
                                    $criticalWarning = [int]$nvmeLog.critical_warning
                                }
                                if ($null -ne $nvmeLog.data_units_written) {
                                    $units = [double]$nvmeLog.data_units_written
                                    $tbw = [math]::Round(($units * 512000.0) / 1TB, 1)
                                }
                                if ($null -ne $nvmeLog.power_on_hours) {
                                    try {
                                        $powerOnHours = [int64]$nvmeLog.power_on_hours
                                    } catch {
                                        $powerOnHours = $nvmeLog.power_on_hours
                                    }
                                }
                                break
                            }

                            # ATA / SATA S.M.A.R.T. podaci
                            if ($smartObj.ata_smart_attributes -and $smartObj.ata_smart_attributes.table) {
                                foreach ($attr in $smartObj.ata_smart_attributes.table) {
                                    if ($attr.id -eq 5 -and $attr.raw -and ($null -ne $attr.raw.value)) {
                                        $reallocatedSectors = [int]$attr.raw.value
                                        $smartParsed = $true
                                    }
                                    if ($attr.id -eq 9 -and $attr.raw -and ($null -ne $attr.raw.value)) {
                                        try {
                                            $powerOnHours = [int64]$attr.raw.value
                                        } catch {
                                            $powerOnHours = $attr.raw.value
                                        }
                                    }
                                    if ($attr.id -in @(231, 202, 177, 169) -and ($null -ne $attr.value) -and [int]$attr.value -gt 0) {
                                        $ssdHealth = [int]$attr.value
                                        $smartParsed = $true
                                    }
                                }
                                if ($smartParsed) { break }
                            }
                        }
                    } catch {}
                }
            }
        } catch {}
    }

    # Ako smartctl nije ocitao precizan procenat, koristimo WMI/CIM HealthStatus
    if ($null -eq $ssdHealth) {
        $isSmartEstimated = $true
        if ($failurePredict -or $diskHealthStatus -eq "Unhealthy") {
            $ssdHealth = 30
        } elseif ($diskHealthStatus -eq "Warning") {
            $ssdHealth = 70
        } else {
            $ssdHealth = 100
        }
        $diskHealthStatus = "$diskHealthStatus (WMI bazična provera)"
    }

    # 3. Ocitavanje stanja baterije za laptopove
    $batHealth = $null
    $isLaptop = $false
    $designCap = 0
    $fullCap = 0

    try {
        $wmiFull = Get-CimInstance -Namespace root\wmi -ClassName BatteryFullChargedCapacity -ErrorAction SilentlyContinue | Select-Object -First 1
        $wmiStatic = Get-CimInstance -Namespace root\wmi -ClassName BatteryStaticData -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($wmiFull -and $wmiStatic -and $wmiStatic.DesignedCapacity -gt 0) {
            $isLaptop = $true
            $designCap = [double]$wmiStatic.DesignedCapacity
            $fullCap = [double]$wmiFull.FullChargedCapacity
        }
    } catch {}

    if ($designCap -le 0) {
        try {
            $bat = Get-CimInstance Win32_Battery -ErrorAction SilentlyContinue | Select-Object -First 1
            if ($bat) {
                $isLaptop = $true
                if ($bat.DesignCapacity -and $bat.FullChargeCapacity -and [double]$bat.DesignCapacity -gt 0) {
                    $designCap = [double]$bat.DesignCapacity
                    $fullCap = [double]$bat.FullChargeCapacity
                }
            }
        } catch {}
    }

    if ($isLaptop -and $designCap -gt 0 -and $fullCap -gt 0) {
        $batHealth = [math]::Min(100, [math]::Max(0, [math]::Round(($fullCap / $designCap) * 100.0, 0)))
    }

    # 4. Kategorizacija zdravlja hardvera (OK, WARNING, CRITICAL)
    $overallStatus = "OK"
    $details = [System.Collections.Generic.List[string]]::new()

    if ($failurePredict) {
        $overallStatus = "CRITICAL"
        $details.Add("CRITICAL: S.M.A.R.T. Failure Predict indikator je aktivan (disk je pred otkazom)!")
    }
    if ($criticalWarning -gt 0) {
        $overallStatus = "CRITICAL"
        $details.Add("CRITICAL: NVMe Critical Warning fleg je aktivan ($criticalWarning)!")
    }
    if ($reallocatedSectors -gt 0) {
        $overallStatus = "CRITICAL"
        $details.Add("CRITICAL: Detektovano $reallocatedSectors realociranih losih sektora na disku!")
    }
    if ($ssdHealth -lt 50) {
        $overallStatus = "CRITICAL"
        $details.Add("CRITICAL: Zdravlje SSD diska je kriticno ($ssdHealth% < 50%)!")
    } elseif ($ssdHealth -lt 80) {
        if ($overallStatus -ne "CRITICAL") { $overallStatus = "WARNING" }
        $details.Add("WARNING: Ocuvanost SSD diska je niza od preporucene ($ssdHealth% < 80%).")
    }

    if ($isLaptop -and $null -ne $batHealth) {
        if ($batHealth -lt 60) {
            if ($overallStatus -ne "CRITICAL") { $overallStatus = "WARNING" }
            $details.Add("WARNING: Kapacitet baterije je degradiran ($batHealth% < 60%).")
        }
    }

    if ($overallStatus -eq "OK") {
        $details.Add("OK: Svi testirani hardverski parametri (SSD i Baterija) su u granicama normale.")
    }

    $summaryParts = @()
    if ($diskModel) {
        $tbwStr = if ($tbw) { " | TBW: ${tbw}TB" } else { "" }
        $badSecStr = if ($reallocatedSectors -gt 0) { " | Losi sektori: $reallocatedSectors" } else { "" }
        $summaryParts += "SSD: $ssdHealth%$tbwStr$badSecStr ($overallStatus)"
    }
    if ($isLaptop -and $null -ne $batHealth) {
        $summaryParts += "Baterija: $batHealth%"
    }
    $summary = $summaryParts -join " | "

    return [PSCustomObject]@{
        DiskHealth           = $ssdHealth
        DiskHealthPercent    = $ssdHealth
        DiskModel            = $diskModel
        DiskHealthStatus     = $diskHealthStatus
        PowerOnHours         = $powerOnHours
        IsSmartEstimated     = $isSmartEstimated
        ReallocatedSectors   = $reallocatedSectors
        CriticalWarning      = $criticalWarning
        TbwTB                = $tbw
        BatteryHealth        = $batHealth
        BatteryHealthPercent = $batHealth
        IsLaptop             = $isLaptop
        OverallStatus        = $overallStatus
        Details              = @($details)
        Summary              = $summary
    }
}

function Get-SystemHardwareInfo {
    <#
    .SYNOPSIS
        Prečica i alias za Get-HardwareDiagnosticsInfo sa uključenom zdravstvenom analizom.
    #>
    Get-HardwareDiagnosticsInfo
}

function Get-HwInfo {
    <#
    .SYNOPSIS
        Prečica i alias za Get-HardwareDiagnosticsInfo.
    #>
    Get-HardwareDiagnosticsInfo
}

function Get-ServisHubEndpoint {
    <#
    .SYNOPSIS
        Pronalazi i vraća aktivnu adresu Centralnog ServisHub servera.
    .DESCRIPTION
        Proverava redom keširanu promenljivu $Global:ServisHubUrl, funkciju Find-ServisHub,
        konfiguracioni fajl Wifi-Servis.ini ([ServisHub] i [GdsServer] sekcije),
        kao i brzi HTTP odziv na adresi 192.168.170.254 (port 8080 ili 8000).
    .COMPATIBILITY
        Windows PowerShell 5.1+
    #>
    [CmdletBinding()]
    param(
        [string]$DefaultFallback = "http://192.168.170.254:8080"
    )

    if ($Global:ServisHubUrl -and $Global:ServisHubUrl.Trim() -ne "") {
        return $Global:ServisHubUrl.TrimEnd('/')
    }

    if (Get-Command Find-ServisHub -ErrorAction SilentlyContinue) {
        try {
            $found = Find-ServisHub
            if ($found -and $found.Trim() -ne "") {
                return $found.TrimEnd('/')
            }
        } catch {}
    }

    # Provera Wifi-Servis.ini kandidata
    $iniCandidates = @(
        "C:\setup\Wifi-Servis.ini",
        (Join-Path $PSScriptRoot "Wifi-Servis.ini"),
        (Join-Path (Split-Path $PSScriptRoot -Parent) "Wifi-Servis.ini"),
        (Join-Path (Split-Path $PSScriptRoot -Parent) "Skripte\Wifi-Servis.ini")
    )
    if ($Global:LocalSetupDir) { $iniCandidates += (Join-Path $Global:LocalSetupDir "Wifi-Servis.ini") }
    if ($Global:SourceRoot) { $iniCandidates += (Join-Path $Global:SourceRoot "Wifi-Servis.ini") }
    foreach ($d in (Get-PSDrive -PSProvider FileSystem -ErrorAction SilentlyContinue)) {
        $iniCandidates += (Join-Path "$($d.Root)" "Wifi-Servis.ini")
        $iniCandidates += (Join-Path "$($d.Root)" "Skripte\Wifi-Servis.ini")
    }

    $iniPath = $iniCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
    if ($iniPath) {
        try {
            $content = Get-Content $iniPath -Raw -ErrorAction SilentlyContinue
            if ($content -match '(?mi)^\s*\[ServisHub\]([\s\S]*?)(?:^\s*\[|\Z)') {
                $sec = $matches[1]
                $hostName = if ($sec -match '(?mi)^\s*ServerHost\s*=\s*([^\r\n#;]+)') { $matches[1].Trim() } else { "192.168.170.254" }
                $port = if ($sec -match '(?mi)^\s*ServerPort\s*=\s*([0-9]+)') { [int]$matches[1].Trim() } else { 8080 }
                $firstHost = ($hostName -split ',')[0].Trim()
                if ($firstHost -and $firstHost -ne "auto") {
                    return "http://${firstHost}:${port}"
                }
            } elseif ($content -match '(?mi)^\s*\[GdsServer\]([\s\S]*?)(?:^\s*\[|\Z)') {
                $sec = $matches[1]
                $hostName = if ($sec -match '(?mi)^\s*ServerHost\s*=\s*([^\r\n#;]+)') { $matches[1].Trim() } else { "192.168.170.254" }
                $port = if ($sec -match '(?mi)^\s*ServerPort\s*=\s*([0-9]+)') { [int]$matches[1].Trim() } else { 8000 }
                $firstHost = ($hostName -split ',')[0].Trim()
                if ($firstHost -and $firstHost -ne "auto") {
                    return "http://${firstHost}:${port}"
                }
            }
        } catch {}
    }

    # Provera HTTP odziva
    foreach ($testHost in @("http://192.168.170.254:8080", "http://192.168.170.254:8000")) {
        try {
            $req = [System.Net.WebRequest]::Create("$testHost/api/status")
            $req.Timeout = 500
            $req.Method = "GET"
            $resp = $req.GetResponse()
            $resp.Close()
            return $testHost
        } catch {}
    }

    return $DefaultFallback
}

function Install-DriverFromMsCatalog {
    <#
    .SYNOPSIS
        Pretrazuje Microsoft Update Catalog po HWID-ju preko interneta, preuzima .cab fajl,
        raspakuje ga preko expand.exe i instalira preko pnputil.exe.
    .DESCRIPTION
        Automatski Internet Fallback kada ServisHub na 192.168.170.254:8080 nije dostupan na lokalnoj mrezi.
        1. Pretraga: https://www.catalog.update.microsoft.com/Search.aspx?q=<HWID>
        2. Filtriranje rezultata za Windows 10/11 x64 i odabir najnovijeg drajvera.
        3. Dohvatanje direktnog .cab download linka preko DownloadDialog.aspx sa Microsoft CDN-a.
        4. Preuzimanje .cab fajla u privremeni direktorijum.
        5. Raspakivanje arhive alatom expand.exe.
        6. Instalacija svih raspakovanih .inf fajlova preko pnputil.exe /add-driver <inf> /install.
    .COMPATIBILITY
        Windows PowerShell 5.1+
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$HardwareId,

        [Parameter(Mandatory = $false)]
        [string]$DeviceName = "Nepoznat uredjaj",

        [Parameter(Mandatory = $false)]
        [string]$DestinationDir = "C:\setup\Drivers\Pending"
    )

    if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
        Write-ServisLog "Install-DriverFromMsCatalog: Pokrecem Microsoft Update Catalog pretragu za [$DeviceName] (HWID: $HardwareId)..." -Level "STEP" -Component "Hardware"
    }

    try {
        [System.Net.ServicePointManager]::SecurityProtocol = [System.Net.ServicePointManager]::SecurityProtocol -bor [System.Net.SecurityProtocolType]::Tls12
    } catch {}

    $cleanQuery = $HardwareId.Trim()
    $encodedQuery = [System.Uri]::EscapeDataString($cleanQuery)
    $searchUrl = "https://www.catalog.update.microsoft.com/Search.aspx?q=$encodedQuery"
    $userAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36"

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
        $searchErr = $_.Exception.Message
        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
            Write-ServisLog "Install-DriverFromMsCatalog: Greska pri upitu ka Microsoft Katalogu ($searchUrl): $searchErr" -Level "WARN" -Component "Hardware"
        }
        return @{
            Success        = $false
            ExitCode       = -10
            InfCount       = 0
            RebootRequired = $false
            Output         = "Neuspeh pretrage kataloga: $searchErr"
        }
    }

    $regexOpts = [System.Text.RegularExpressions.RegexOptions]::Singleline -bor [System.Text.RegularExpressions.RegexOptions]::IgnoreCase
    $matches = [regex]::Matches($searchHtml, '<tr id="([a-f0-9\-]+)_R\d+"[^>]*>(.*?)</tr>', $regexOpts)
    if ($matches.Count -eq 0) {
        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
            Write-ServisLog "Install-DriverFromMsCatalog: Nema rezultata u Microsoft Katalogu za HWID: $HardwareId" -Level "INFO" -Component "Hardware"
        }
        return @{
            Success        = $false
            ExitCode       = -11
            InfCount       = 0
            RebootRequired = $false
            Output         = "Drajver nije pronadjen u Microsoft Katalogu"
        }
    }

    $candidates = [System.Collections.Generic.List[PSCustomObject]]::new()
    foreach ($m in $matches) {
        $guid = $m.Groups[1].Value
        $rowHtml = $m.Groups[2].Value

        $tdMatches = [regex]::Matches($rowHtml, '<td[^>]*>(.*?)</td>', $regexOpts)
        if ($tdMatches.Count -lt 7) { continue }

        $rawTitle = $tdMatches[1].Groups[1].Value
        $cleanTitle = ([regex]::Replace($rawTitle, '<[^>]+>', ' ')).Trim()

        $rawProducts = $tdMatches[2].Groups[1].Value
        $cleanProducts = ([regex]::Replace($rawProducts, '<[^>]+>', ' ')).Trim()

        $rawDate = $tdMatches[4].Groups[1].Value
        $cleanDate = ([regex]::Replace($rawDate, '<[^>]+>', ' ')).Trim()

        $rawVer = $tdMatches[5].Groups[1].Value
        $cleanVer = ([regex]::Replace($rawVer, '<[^>]+>', ' ')).Trim()

        $combText = ($cleanTitle + " " + $cleanProducts).ToLower()

        if ($combText -match '(arm|arm64|itanium|ia64|x86|32-bit|32bit)') { continue }

        $isWin1011 = ($combText -match 'windows 11' -or $combText -match 'windows 10' -or $combText -match 'win 11' -or $combText -match 'win 10' -or $combText -match 'client')
        if (-not $isWin1011 -and $combText -match 'windows') {
            if ($combText -notmatch 'windows xp' -and $combText -notmatch 'windows 7' -and $combText -notmatch 'windows 8') {
                $isWin1011 = $true
            }
        }

        if ($isWin1011) {
            $parsedDate = [DateTime]::MinValue
            [DateTime]::TryParse($cleanDate, [ref]$parsedDate) | Out-Null

            $candidates.Add([PSCustomObject]@{
                UpdateId = $guid
                Title    = $cleanTitle
                Products = $cleanProducts
                Date     = $parsedDate
                Version  = $cleanVer
            })
        }
    }

    if ($candidates.Count -eq 0) {
        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
            Write-ServisLog "Install-DriverFromMsCatalog: Nijedan pronadjeni drajver ne odgovara Win10/11 x64 platformi za HWID: $HardwareId" -Level "WARN" -Component "Hardware"
        }
        return @{
            Success        = $false
            ExitCode       = -12
            InfCount       = 0
            RebootRequired = $false
            Output         = "Nema kompatibilnog x64 drajvera u katalogu"
        }
    }

    $bestMatch = $candidates | Sort-Object -Property Date -Descending | Select-Object -First 1
    $updateId = $bestMatch.UpdateId

    if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
        Write-ServisLog "Install-DriverFromMsCatalog: Odabran drajver: $($bestMatch.Title) ($($bestMatch.Version)). Preuzimam link..." -Level "INFO" -Component "Hardware"
    }

    $dialogUrl = "https://www.catalog.update.microsoft.com/DownloadDialog.aspx"
    $dialogHtml = ""
    try {
        $dlgReq = [System.Net.HttpWebRequest]::Create($dialogUrl)
        $dlgReq.Method = "POST"
        $dlgReq.ContentType = "application/x-www-form-urlencoded"
        $dlgReq.UserAgent = $userAgent
        $dlgReq.Timeout = 25000

        $postParam = "updateIDs=" + [System.Uri]::EscapeDataString('[{"size":0,"languages":"","uidInfo":"' + $updateId + '","updateID":"' + $updateId + '"}]')
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
        $dlgErr = $_.Exception.Message
        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
            Write-ServisLog "Install-DriverFromMsCatalog: Neuspeh dohvatanja download linka za $($updateId) - $dlgErr" -Level "WARN" -Component "Hardware"
        }
        return @{
            Success        = $false
            ExitCode       = -13
            InfCount       = 0
            RebootRequired = $false
            Output         = "Neuspeh dohvatanja download linka: $dlgErr"
        }
    }

    $cabUrl = $null
    $pattern1 = 'downloadInformation\[\d+\]\.files\[\d+\]\.url\s*=\s*[''"]([^''"]+)[''"]'
    $urlMatches = [regex]::Matches($dialogHtml, $pattern1)
    if ($urlMatches.Count -gt 0) {
        $cabUrl = $urlMatches[0].Groups[1].Value
    } else {
        $pattern2 = 'https?://[^\s''"\<\>]+\.cab'
        $rawCabMatches = [regex]::Matches($dialogHtml, $pattern2, [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
        if ($rawCabMatches.Count -gt 0) {
            $cabUrl = $rawCabMatches[0].Groups[0].Value
        }
    }

    if ([string]::IsNullOrWhiteSpace($cabUrl)) {
        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
            Write-ServisLog "Install-DriverFromMsCatalog: Nije pronadjen .cab URL u odgovoru Microsoft Kataloga." -Level "WARN" -Component "Hardware"
        }
        return @{
            Success        = $false
            ExitCode       = -14
            InfCount       = 0
            RebootRequired = $false
            Output         = "Nije pronadjen .cab link za preuzimanje"
        }
    }

    $cleanId = ($updateId -replace '[^\w\.\-]', '_').Trim()
    $workDir = Join-Path $DestinationDir "mscat_$cleanId"
    $extractDir = Join-Path $workDir "extracted"

    try {
        if (-not (Test-Path $workDir)) { New-Item -Path $workDir -ItemType Directory -Force | Out-Null }
        if (-not (Test-Path $extractDir)) { New-Item -Path $extractDir -ItemType Directory -Force | Out-Null }

        $cabFile = Join-Path $workDir "$cleanId.cab"
        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
            Write-ServisLog "Install-DriverFromMsCatalog: Preuzimam .cab drajver sa Microsoft CDN-a ($cabUrl)..." -Level "INFO" -Component "Hardware"
        }

        $downloadSuccess = $false
        $downloadError = ""
        try {
            $wc = New-Object System.Net.WebClient
            $wc.Headers.Add("User-Agent", $userAgent)
            $wc.DownloadFile($cabUrl, $cabFile)
            $wc.Dispose()
            $downloadSuccess = $true
        } catch {
            $downloadError = $_.Exception.Message
            try {
                Invoke-WebRequest -Uri $cabUrl -OutFile $cabFile -TimeoutSec 120 -UseBasicParsing -ErrorAction Stop
                $downloadSuccess = $true
            } catch {
                $downloadError = $_.Exception.Message
            }
        }

        if (-not $downloadSuccess -or -not (Test-Path $cabFile) -or (Get-Item $cabFile).Length -le 0) {
            $errMsg = "Preuzimanje .cab fajla sa Microsoft CDN-a nije uspelo: $downloadError"
            if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                Write-ServisLog "Install-DriverFromMsCatalog: $errMsg" -Level "ERROR" -Component "Hardware"
            }
            return @{
                Success        = $false
                ExitCode       = -15
                InfCount       = 0
                RebootRequired = $false
                Output         = $errMsg
            }
        }

        $fileSizeKB = [math]::Round(((Get-Item $cabFile).Length / 1KB), 1)
        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
            Write-ServisLog "Install-DriverFromMsCatalog: Preuzet .cab paket ($fileSizeKB KB). Raspakujem preko expand.exe..." -Level "INFO" -Component "Hardware"
        }

        try {
            & expand.exe -R "$cabFile" -F:* "$extractDir" | Out-Null
        } catch {
            $expErr = $_.Exception.Message
            if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                Write-ServisLog "Install-DriverFromMsCatalog: Upozorenje expand.exe: $expErr" -Level "WARN" -Component "Hardware"
            }
        }

        $infFiles = Get-ChildItem -Path $extractDir -Filter "*.inf" -Recurse -File -ErrorAction SilentlyContinue
        if (-not $infFiles -or $infFiles.Count -eq 0) {
            $infFiles = Get-ChildItem -Path $workDir -Filter "*.inf" -Recurse -File -ErrorAction SilentlyContinue
        }

        if (-not $infFiles -or $infFiles.Count -eq 0) {
            $errMsg = "U raspakovanom .cab paketu nije pronadjen nijedan .inf fajl za instalaciju."
            if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                Write-ServisLog "Install-DriverFromMsCatalog: $errMsg" -Level "WARN" -Component "Hardware"
            }
            return @{
                Success        = $false
                ExitCode       = -16
                InfCount       = 0
                RebootRequired = $false
                Output         = $errMsg
            }
        }

        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
            Write-ServisLog "Install-DriverFromMsCatalog: Pronadjeno $($infFiles.Count) .inf datoteka. Pokrecem instalaciju preko pnputil.exe..." -Level "INFO" -Component "Hardware"
        }

        $pnpOutputs = [System.Collections.Generic.List[string]]::new()
        $exitCodes = [System.Collections.Generic.List[int]]::new()
        $hasReboot = $false

        foreach ($inf in $infFiles) {
            $pnpOut = & pnputil.exe /add-driver "$($inf.FullName)" /install 2>&1
            $exitCode = $LASTEXITCODE
            $exitCodes.Add($exitCode)

            $outStr = ($pnpOut -join "`n").Trim()
            $pnpOutputs.Add("[$($inf.Name)] ExitCode: $exitCode | $outStr")

            if ($exitCode -eq 3010) {
                $hasReboot = $true
            }
        }

        $finalExitCode = if ($exitCodes.Count -gt 0) { $exitCodes[-1] } else { 0 }
        $overallSuccess = $false
        foreach ($ec in $exitCodes) {
            if ($ec -eq 0 -or $ec -eq 3010 -or $ec -eq 259) {
                $overallSuccess = $true
                break
            }
        }

        $outCombined = ($pnpOutputs -join "`n")
        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
            $lvl = if ($overallSuccess) { "SUCCESS" } else { "WARN" }
            Write-ServisLog "Install-DriverFromMsCatalog: [REZULTAT] Uspeh: $overallSuccess, Kod: $finalExitCode, InfCount: $($infFiles.Count), Reboot: $hasReboot" -Level $lvl -Component "Hardware"
        }

        return @{
            Success        = $overallSuccess
            ExitCode       = $finalExitCode
            InfCount       = $infFiles.Count
            RebootRequired = ($finalExitCode -eq 3010 -or $hasReboot)
            Output         = $outCombined
            Title          = $bestMatch.Title
        }
    } finally {
        if ($workDir -and (Test-Path $workDir)) {
            Remove-Item -Path $workDir -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}


function Install-OemDriverFromHub {
    <#
    .SYNOPSIS
        Preuzima zvanični OEM .cab drajver paket sa ServisHub-a, raspakuje ga pomoću expand.exe
        i instalira preko pnputil.exe.
    .DESCRIPTION
        1. Preuzima arhivu drajvera sa ServisHub /api/drivers/download endpointa.
        2. Proverava da je preuzeti .cab validan i veći od 0 bajtova.
        3. Koristi izvorni Windows alat expand.exe za raspakivanje u privremeni direktorijum.
        4. Pronalazi sve .inf fajlove i vrši njihovu instalaciju preko pnputil.exe /add-driver /install.
        5. Vraća strukturisan hashtable sa detaljima ishoda.
    .COMPATIBILITY
        Windows PowerShell 5.1+
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$DriverId,

        [Parameter(Mandatory = $false)]
        [string]$HubUrl = "",

        [Parameter(Mandatory = $false)]
        [string]$DestinationDir = "C:\setup\Drivers\Pending"
    )

    if ([string]::IsNullOrWhiteSpace($HubUrl)) {
        $HubUrl = Get-ServisHubEndpoint
    }
    $HubUrl = $HubUrl.TrimEnd('/')

    # Sanirano ime za podfolder i privremene fajlove
    $cleanId = ($DriverId -replace '[^\w\.\-]', '_').Trim()
    if ([string]::IsNullOrWhiteSpace($cleanId)) {
        $cleanId = [Guid]::NewGuid().ToString("N")
    }

    $workDir = Join-Path $DestinationDir $cleanId
    $extractDir = Join-Path $workDir "extracted"

    try {
        if (-not (Test-Path $workDir)) {
            New-Item -Path $workDir -ItemType Directory -Force | Out-Null
        }
        if (-not (Test-Path $extractDir)) {
            New-Item -Path $extractDir -ItemType Directory -Force | Out-Null
        }
    } catch {
        $createErr = $_.Exception.Message
        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
            Write-ServisLog "Install-OemDriverFromHub: Ne mogu kreirati radni direktorijum $($workDir) - $createErr" -Level "ERROR" -Component "Hardware"
        }
        return @{
            Success        = $false
            ExitCode       = -1
            InfCount       = 0
            RebootRequired = $false
            Output         = "Neuspešno kreiranje direktorijuma: $createErr"
        }
    }

    try {
        $downloadUrl = if ($DriverId -match '^https?://') {
            $DriverId
        } else {
            "$HubUrl/api/drivers/download?id=$DriverId"
        }

        $cabFile = Join-Path $workDir "$cleanId.cab"

        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
            Write-ServisLog "Install-OemDriverFromHub: Preuzimam OEM drajver [$cleanId] sa $downloadUrl..." -Level "STEP" -Component "Hardware"
        }

        # Preuzimanje .cab arhive (WebClient sa fallback-om na WebRequest/Invoke-WebRequest)
        $downloadSuccess = $false
        $downloadError = ""
        $webClient = $null

        try {
            $webClient = New-Object System.Net.WebClient
            $webClient.Headers.Add("User-Agent", "WIS-DriverEngine/2.15")
            $webClient.DownloadFile($downloadUrl, $cabFile)
            $downloadSuccess = $true
        } catch {
            $downloadError = $_.Exception.Message
            try {
                $downReq = [System.Net.HttpWebRequest]::Create($downloadUrl)
                $downReq.Method = "GET"
                $downReq.Timeout = 90000
                $downReq.UserAgent = "WIS-DriverEngine/2.15"
                $downResp = $downReq.GetResponse()
                $downStream = $downResp.GetResponseStream()
                $fs = New-Object System.IO.FileStream($cabFile, [System.IO.FileMode]::Create, [System.IO.FileAccess]::Write)
                $downStream.CopyTo($fs)
                $fs.Close()
                $downStream.Close()
                $downResp.Close()
                $downloadSuccess = $true
            } catch {
                $downloadError = $_.Exception.Message
                try {
                    Invoke-WebRequest -Uri $downloadUrl -OutFile $cabFile -TimeoutSec 90 -UseBasicParsing -ErrorAction Stop
                    $downloadSuccess = $true
                } catch {
                    $downloadError = $_.Exception.Message
                }
            }
        } finally {
            if ($webClient) { $webClient.Dispose() }
        }

        # Provera da je fajl preuzet i veci od 0 bajtova
        if (-not $downloadSuccess -or -not (Test-Path $cabFile) -or (Get-Item $cabFile).Length -le 0) {
            $errMsg = "Preuzimanje drajvera sa ServisHub-a nije uspelo ($downloadError). Pokrecem Internet Fallback..."
            if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                Write-ServisLog "Install-OemDriverFromHub: $errMsg" -Level "WARN" -Component "Hardware"
            }

            # Ako DriverId lici na HWID ili URL, pokusaj direktno preko Microsoft Kataloga
            if ($DriverId -match '^(PCI|USB|ACPI|HDAUDIO|HID|DISPLAY)' -or $DriverId -match '&DEV_' -or $DriverId -match '&PID_') {
                return Install-DriverFromMsCatalog -HardwareId $DriverId -DeviceName $cleanId -DestinationDir $DestinationDir
            }

            return @{
                Success        = $false
                ExitCode       = -2
                InfCount       = 0
                RebootRequired = $false
                Output         = $errMsg
            }
        }

        $fileSizeKB = [math]::Round(((Get-Item $cabFile).Length / 1KB), 1)
        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
            Write-ServisLog "Install-OemDriverFromHub: Paket preuzet ($fileSizeKB KB). Raspakujem preko expand.exe..." -Level "INFO" -Component "Hardware"
        }

        # Raspakivanje .cab arhive koristeći expand.exe
        try {
            & expand.exe -R "$cabFile" -F:* "$extractDir" | Out-Null
        } catch {
            $expErr = $_.Exception.Message
            if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                Write-ServisLog "Install-OemDriverFromHub: Upozorenje pri raspakivanju expand.exe: $expErr" -Level "WARN" -Component "Hardware"
            }
        }

        # Pretraga raspakovanog foldera za sve .inf fajlove
        $infFiles = Get-ChildItem -Path $extractDir -Filter "*.inf" -Recurse -File -ErrorAction SilentlyContinue
        if (-not $infFiles -or $infFiles.Count -eq 0) {
            $infFiles = Get-ChildItem -Path $workDir -Filter "*.inf" -Recurse -File -ErrorAction SilentlyContinue
        }

        if (-not $infFiles -or $infFiles.Count -eq 0) {
            $errMsg = "U raspakovanom paketu [$cleanId] nije pronađen nijedan .inf fajl za instalaciju."
            if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                Write-ServisLog "Install-OemDriverFromHub: $errMsg" -Level "WARN" -Component "Hardware"
            }
            return @{
                Success        = $false
                ExitCode       = -3
                InfCount       = 0
                RebootRequired = $false
                Output         = $errMsg
            }
        }

        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
            Write-ServisLog "Install-OemDriverFromHub: Pronađeno $($infFiles.Count) .inf datoteka. Pokrećem instalaciju preko pnputil.exe..." -Level "INFO" -Component "Hardware"
        }

        $pnpOutputs = [System.Collections.Generic.List[string]]::new()
        $exitCodes = [System.Collections.Generic.List[int]]::new()
        $hasReboot = $false

        foreach ($inf in $infFiles) {
            $pnpOut = & pnputil.exe /add-driver "$($inf.FullName)" /install 2>&1
            $exitCode = $LASTEXITCODE
            $exitCodes.Add($exitCode)

            $outStr = ($pnpOut -join "`n").Trim()
            $pnpOutputs.Add("[$($inf.Name)] ExitCode: $exitCode | $outStr")

            if ($exitCode -eq 3010) {
                $hasReboot = $true
            }
        }

        $finalExitCode = if ($exitCodes.Count -gt 0) { $exitCodes[-1] } else { 0 }
        $overallSuccess = $false
        foreach ($ec in $exitCodes) {
            if ($ec -eq 0 -or $ec -eq 3010 -or $ec -eq 259) {
                $overallSuccess = $true
                break
            }
        }

        $outCombined = ($pnpOutputs -join "`n")
        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
            $lvl = if ($overallSuccess) { "SUCCESS" } else { "WARN" }
            Write-ServisLog "Install-OemDriverFromHub: Instalacija paketa [$cleanId] završena. Uspeh: $overallSuccess, Kod: $finalExitCode, InfCount: $($infFiles.Count), Reboot: $hasReboot" -Level $lvl -Component "Hardware"
        }

        return @{
            Success        = $overallSuccess
            ExitCode       = $finalExitCode
            InfCount       = $infFiles.Count
            RebootRequired = ($finalExitCode -eq 3010 -or $hasReboot)
            Output         = $outCombined
        }
    } finally {
        if ($workDir -and (Test-Path $workDir)) {
            Remove-Item -Path $workDir -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}

function Resolve-MissingDriversFromHub {
    <#
    .SYNOPSIS
        Pronalazi sve aktivne PnP uređaje sa greškom, šalje zahteve ServisHub-u za pronalaženje OEM drajvera
        i automatski ih instalira u sistem.
    .DESCRIPTION
        Skenira Win32_PnPEntity uređaje gde je ConfigManagerErrorCode != 0 (izuzimajuci kodove 22 i 45 i virtualne ROOT/SWD uređaje),
        izvlači njihove HardwareID vrednosti i šalje POST upit ka ServisHub /api/drivers/resolve endpointu.
        Prvo traži bilo koji rezultat sa statusom "ready", a tek ako nijedan HWID uređaja nije rešen pokreće Microsoft Catalog fallback.
    .COMPATIBILITY
        Windows PowerShell 5.1+
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $false)]
        [string]$HubUrl = ""
    )

    if ([string]::IsNullOrWhiteSpace($HubUrl)) {
        $HubUrl = Get-ServisHubEndpoint
    }
    $HubUrl = $HubUrl.TrimEnd('/')

    if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
        Write-ServisLog "Resolve-MissingDriversFromHub: Započinjem proveru problematičnih uređaja preko ServisHub-a ($HubUrl)..." -Level "STEP" -Component "Hardware"
    }

    # Dohvatanje modela i serijskog broja sistema za slanje ka ServisHub OEM API-ju
    $sysModel = ""
    $sysSerial = ""
    try {
        $csInfo = Get-CimInstance Win32_ComputerSystem -ErrorAction SilentlyContinue
        if ($csInfo -and $csInfo.Model) { $sysModel = $csInfo.Model.Trim() }
        $biosInfo = Get-CimInstance Win32_BIOS -ErrorAction SilentlyContinue
        if ($biosInfo -and $biosInfo.SerialNumber) { $sysSerial = $biosInfo.SerialNumber.Trim() }
    } catch {}

    # 1. Pronalaženje svih aktivnih uređaja sa stvarnim kodom greške drajvera (isključuje kod 22 Disabled, 45 Disconnected i ROOT/SWD)
    $badDevices = @()
    try {
        $badDevices = @(Get-CimInstance Win32_PnPEntity -ErrorAction SilentlyContinue | Where-Object {
            $_.ConfigManagerErrorCode -and
            $_.ConfigManagerErrorCode -notin @(0, 22, 45) -and
            $_.HardwareID -and
            $_.PNPClass -notin @('Printer', 'PrintQueue', 'SoftwareDevice') -and
            $_.DeviceID -notmatch '^(ROOT|SWD)\\'
        })
    } catch {
        try {
            $badDevices = @(Get-WmiObject Win32_PnPEntity -ErrorAction SilentlyContinue | Where-Object {
                $_.ConfigManagerErrorCode -and
                $_.ConfigManagerErrorCode -notin @(0, 22, 45) -and
                $_.HardwareID -and
                $_.PNPClass -notin @('Printer', 'PrintQueue', 'SoftwareDevice') -and
                $_.DeviceID -notmatch '^(ROOT|SWD)\\'
            })
        } catch {}
    }

    if (-not $badDevices -or $badDevices.Count -eq 0) {
        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
            Write-ServisLog "Resolve-MissingDriversFromHub: Nema detektovanih uređaja sa greškom drajvera (ConfigManagerErrorCode=0 za sve PnP komponente)." -Level "SUCCESS" -Component "Hardware"
        }
        return [PSCustomObject]@{
            TotalProblematic = 0
            ResolvedCount    = 0
            NotFoundCount    = 0
            ErrorCount       = 0
            AllResolved      = $true
            Results          = @()
        }
    }

    $totalProblematic = $badDevices.Count
    if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
        Write-ServisLog "Resolve-MissingDriversFromHub: Detektovano $totalProblematic uređaja sa greškom drajvera. Šaljem upite ServisHub-u..." -Level "INFO" -Component "Hardware"
    }

    $resolvedCount = 0
    $notFoundCount = 0
    $errorCount = 0
    $resultsList = [System.Collections.Generic.List[PSCustomObject]]::new()

    foreach ($dev in $badDevices) {
        $devName = if ($dev.Name) { $dev.Name.Trim() } elseif ($dev.Description) { $dev.Description.Trim() } else { "Nepoznat uredjaj" }
        $mfg = if ($dev.Manufacturer) { $dev.Manufacturer.Trim() } else { "" }
        $hwids = @($dev.HardwareID | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
        if ($hwids.Count -eq 0) { continue }

        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
            Write-ServisLog "Resolve-MissingDriversFromHub: Upit za [$devName] (HWID: $($hwids[0]), Kod: $($dev.ConfigManagerErrorCode))..." -Level "DEBUG" -Component "Hardware"
        }

        # Kreiranje JSON tela sa garantovanim nizom za hwids plus model i serial_number
        $postBodyObj = [ordered]@{
            hwids         = $hwids
            device_name   = $devName
            manufacturer  = $mfg
            model         = $sysModel
            serial_number = $sysSerial
        }
        $postBody = $postBodyObj | ConvertTo-Json -Compress
        if ($hwids.Count -eq 1) {
            # PowerShell 5.1 ponekad pretvara niz sa jednim elementom u skalar u Hashtable-u
            $escapedSingle = ($hwids[0] | ConvertTo-Json -Compress)
            $postBody = $postBody -replace '"hwids":("[^"]*"|[^,\}]+)', "`"hwids`":[$escapedSingle]"
        }

        $resolveUrl = "$HubUrl/api/drivers/resolve"
        $hubResponse = $null

        try {
            $req = [System.Net.HttpWebRequest]::Create($resolveUrl)
            $req.Method = "POST"
            $req.ContentType = "application/json; charset=utf-8"
            $req.Timeout = 45000
            $req.ReadWriteTimeout = 45000
            $req.UserAgent = "WIS-DriverEngine/2.15"

            $bytes = [System.Text.Encoding]::UTF8.GetBytes($postBody)
            $req.ContentLength = $bytes.Length
            $reqStream = $req.GetRequestStream()
            $reqStream.Write($bytes, 0, $bytes.Length)
            $reqStream.Close()

            $resp = $req.GetResponse()
            $stream = $resp.GetResponseStream()
            $reader = New-Object System.IO.StreamReader($stream, [System.Text.Encoding]::UTF8)
            $respJson = $reader.ReadToEnd()
            $reader.Close()
            $stream.Close()
            $resp.Close()

            $hubResponse = $respJson | ConvertFrom-Json
        } catch {
            $reqErr = $_.Exception.Message
            if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                Write-ServisLog "Resolve-MissingDriversFromHub: ServisHub na $HubUrl nije dostupan ($reqErr). Automatski prelazim na Internet Fallback (Microsoft Update Catalog) za [$devName]..." -Level "WARN" -Component "Hardware"
            }

            # Automatski Internet Fallback preko Microsoft Update Catalog-a
            $fallbackSuccess = $false
            foreach ($hwid in $hwids) {
                if ([string]::IsNullOrWhiteSpace($hwid)) { continue }
                $msCatRes = Install-DriverFromMsCatalog -HardwareId $hwid -DeviceName $devName -DestinationDir (Join-Path "C:\setup\Drivers\Pending" "MsCat")
                if ($msCatRes.Success) {
                    $resolvedCount++
                    $fallbackSuccess = $true
                    if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                        Write-ServisLog "Resolve-MissingDriversFromHub: [USPEH FALLBACK] Drajver za [$devName] uspesno instaliran preko Microsoft Kataloga (HWID: $hwid, Kod: $($msCatRes.ExitCode))." -Level "SUCCESS" -Component "Hardware"
                    }
                    $resultsList.Add([PSCustomObject]@{
                        DeviceName     = $devName
                        Status         = "installed_via_mscatalog"
                        DriverId       = $hwid
                        ExitCode       = $msCatRes.ExitCode
                        RebootRequired = $msCatRes.RebootRequired
                        Details        = $msCatRes.Title
                    })
                    break
                }
            }

            if (-not $fallbackSuccess) {
                $errorCount++
                $resultsList.Add([PSCustomObject]@{
                    DeviceName = $devName
                    Status     = "error"
                    DriverId   = $null
                    Details    = "ServisHub nedostupan ($reqErr), Microsoft Catalog nije vratio resenje"
                })
            }
            continue
        }

        $handled = $false
        if ($hubResponse -and $hubResponse.status -eq "ok" -and $hubResponse.results) {
            $resultsArr = @($hubResponse.results)
            # Prvo proveri sve rezultate da li postoji ijedan 'ready' pre nego sto eventualno pokrenes fallback za 'not_found'
            $readyRes = $resultsArr | Where-Object { $_.status -eq "ready" -and $_.driver_id } | Select-Object -First 1

            if ($readyRes) {
                if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                    Write-ServisLog "Resolve-MissingDriversFromHub: Pronađen drajver na ServisHub-u za [$devName] (DriverId: $($readyRes.driver_id), Izvor: $($readyRes.source), Verzija: $($readyRes.version)). Pokrećem tihu instalaciju..." -Level "INFO" -Component "Hardware"
                }

                $instRes = Install-OemDriverFromHub -DriverId $readyRes.driver_id -HubUrl $HubUrl
                if ($instRes.Success) {
                    $resolvedCount++
                    $handled = $true
                    if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                        Write-ServisLog "Resolve-MissingDriversFromHub: [USPEH] Drajver za [$devName] uspešno instaliran preko ServisHub-a (ExitCode: $($instRes.ExitCode), RebootRequired: $($instRes.RebootRequired))." -Level "SUCCESS" -Component "Hardware"
                    }
                    $resultsList.Add([PSCustomObject]@{
                        DeviceName     = $devName
                        Status         = "installed"
                        DriverId       = $readyRes.driver_id
                        ExitCode       = $instRes.ExitCode
                        RebootRequired = $instRes.RebootRequired
                        Details        = $readyRes.title
                    })
                } else {
                    $errorCount++
                    $handled = $true
                    if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                        Write-ServisLog "Resolve-MissingDriversFromHub: [NEUSPEH] Instalacija drajvera za [$devName] nije uspela: $($instRes.Output)" -Level "WARN" -Component "Hardware"
                    }
                    $resultsList.Add([PSCustomObject]@{
                        DeviceName = $devName
                        Status     = "install_failed"
                        DriverId   = $readyRes.driver_id
                        ExitCode   = $instRes.ExitCode
                        Details    = $instRes.Output
                    })
                }
            } else {
                $hasDownloading = [bool]($resultsArr | Where-Object { $_.status -eq "downloading" } | Select-Object -First 1)
                $hasNotFound = [bool]($resultsArr | Where-Object { $_.status -eq "not_found" } | Select-Object -First 1)

                if ($hasNotFound -and -not $hasDownloading) {
                    if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                        Write-ServisLog "Resolve-MissingDriversFromHub: ServisHub vratio not_found za sve HWID-jeve uređaja [$devName]. Pokrećem Internet Fallback preko Microsoft Kataloga..." -Level "INFO" -Component "Hardware"
                    }

                    $fallbackSuccess = $false
                    foreach ($hwid in $hwids) {
                        if ([string]::IsNullOrWhiteSpace($hwid)) { continue }
                        $msCatRes = Install-DriverFromMsCatalog -HardwareId $hwid -DeviceName $devName -DestinationDir (Join-Path "C:\setup\Drivers\Pending" "MsCat")
                        if ($msCatRes.Success) {
                            $resolvedCount++
                            $fallbackSuccess = $true
                            $handled = $true
                            if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                                Write-ServisLog "Resolve-MissingDriversFromHub: [USPEH FALLBACK] Drajver za [$devName] uspesno instaliran preko Microsoft Kataloga (HWID: $hwid)." -Level "SUCCESS" -Component "Hardware"
                            }
                            $resultsList.Add([PSCustomObject]@{
                                DeviceName     = $devName
                                Status         = "installed_via_mscatalog"
                                DriverId       = $hwid
                                ExitCode       = $msCatRes.ExitCode
                                RebootRequired = $msCatRes.RebootRequired
                                Details        = $msCatRes.Title
                            })
                            break
                        }
                    }

                    if (-not $fallbackSuccess) {
                        $notFoundCount++
                        $handled = $true
                        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                            Write-ServisLog "Resolve-MissingDriversFromHub: Drajver za [$devName] nije pronadjen ni u ServisHub-u ni u Microsoft Katalogu." -Level "INFO" -Component "Hardware"
                        }
                        $resultsList.Add([PSCustomObject]@{
                            DeviceName = $devName
                            Status     = "not_found"
                            DriverId   = $null
                            Details    = "Nije pronadjen u Microsoft Catalog-u"
                        })
                    }
                }
            }
        }

        if (-not $handled) {
            $notFoundCount++
            $resultsList.Add([PSCustomObject]@{
                DeviceName = $devName
                Status     = "not_found"
                DriverId   = $null
                Details    = "ServisHub nije vratio odgovarajući drajver"
            })
        }
    }

    $allResolved = ($totalProblematic -gt 0 -and $resolvedCount -eq $totalProblematic)
    if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
        Write-ServisLog "Resolve-MissingDriversFromHub: Obrada završena. Ukupno problematičnih: $totalProblematic, Rešeno preko ServisHub-a: $resolvedCount, Nije pronađeno: $notFoundCount, Greške: $errorCount. Svi rešeni: $allResolved" -Level "INFO" -Component "Hardware"
    }

    return [PSCustomObject]@{
        TotalProblematic = $totalProblematic
        ResolvedCount    = $resolvedCount
        NotFoundCount    = $notFoundCount
        ErrorCount       = $errorCount
        AllResolved      = $allResolved
        Results          = @($resultsList)
    }
}

function Start-ServisStressTest {
    <#
    .SYNOPSIS
        Pokreće termalni stres test (Faza 5), meri stvarno vreme trajanja preko Stopwatch-a,
        prati maksimalne CPU/GPU temperature, dostupnost senzora i throttling, sa sigurnosnim prekidom na 98°C
        i generisanjem autoritativnog ugovora u C:\setup\stress-test-result.json i C:\setup\deploy-state.json.
    #>
    [CmdletBinding()]
    param(
        [int]$DurationSeconds = 30,
        [string]$LocalSetupDir = "C:\setup"
    )
    if ($DurationSeconds -le 0) { $DurationSeconds = 30 }

    $runId = [Guid]::NewGuid().ToString("N")
    $maxCpuTemp = 0.0
    $maxGpuTemp = 0.0
    $throttlingDetected = $false
    $emergencyAbort = $false
    $sensorAvailable = $false
    $sensorSource = "NONE"
    $manualCancel = $false
    $executionError = $false
    $errorDetail = ""
    $abortedEarly = $false
    $stopwatch = [System.Diagnostics.Stopwatch]::new()

    if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
        Write-ServisLog "Pokrećem ${DurationSeconds}-sekundni termalni stres test i praćenje senzora..." -Level "STEP" -Component "StressTest"
    }

    # Provera determinističkog kontrolisanog test ulaza senzorskog sloja ($env:WIS_STRESS_TEST_INPUT_JSON)
    $controlledInput = $null
    $useControlledInput = $false
    if (-not [string]::IsNullOrWhiteSpace($env:WIS_STRESS_TEST_INPUT_JSON)) {
        $useControlledInput = $true
        $sensorSource = "CONTROLLED_TEST_INPUT"
        $rawTestInput = $env:WIS_STRESS_TEST_INPUT_JSON.Trim()
        try {
            $jsonContent = $null
            if (Test-Path -LiteralPath $rawTestInput) {
                $jsonContent = Get-Content -LiteralPath $rawTestInput -Raw -Encoding UTF8 -ErrorAction Stop
            } elseif ($rawTestInput.StartsWith("{")) {
                $jsonContent = $rawTestInput
            } else {
                throw "Fajl kontrolisanog test ulaza ne postoji: $rawTestInput"
            }

            if ([string]::IsNullOrWhiteSpace($jsonContent)) {
                throw "Kontrolisani test ulaz je prazan."
            }
            $controlledInput = ConvertFrom-Json -InputObject $jsonContent -ErrorAction Stop
            if ($null -eq $controlledInput) {
                throw "Parsirani kontrolisani test ulaz je null."
            }
            if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                Write-ServisLog "Aktivan kontrolisani test ulaz senzorskog sloja (sensor_source=CONTROLLED_TEST_INPUT)." -Level "WARN" -Component "StressTest"
            }
        } catch {
            $executionError = $true
            $abortedEarly = $true
            $errorDetail = $_.Exception.Message
            if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                Write-ServisLog "Greška pri učitavanju WIS_STRESS_TEST_INPUT_JSON: $errorDetail" -Level "ERROR" -Component "StressTest"
            }
        }
    }

    # 1. Inicijalizacija i pokretanje 100% Multi-Core CPU Load Generatora preko RunspacePool-a
    $cpuCount = [Math]::Max(1, [Environment]::ProcessorCount)
    $syncState = [hashtable]::Synchronized(@{ Stop = $false })
    $runspacePool = $null
    $stressInstances = [System.Collections.ArrayList]::new()

    try {
        if (-not $executionError) {
            if ($useControlledInput -and $controlledInput) {
                if ($controlledInput.simulate_error -eq $true -or $controlledInput.force_error -eq $true -or "$($controlledInput.status)".ToUpper() -eq "ERROR" -or "$($controlledInput.reason)".ToUpper() -eq "EXECUTION_ERROR") {
                    $errMsgFromInput = if ($controlledInput.error_message) { "$($controlledInput.error_message)" } else { "Simulirana greška izvršavanja stres testa (CONTROLLED_TEST_INPUT)" }
                    throw $errMsgFromInput
                }
            }

            $skipCpuWorkers = ($useControlledInput -and $controlledInput -and $controlledInput.skip_cpu_load -eq $true)
            if (-not $skipCpuWorkers) {
                $sessionState = [System.Management.Automation.Runspaces.InitialSessionState]::CreateDefault()
                $runspacePool = [System.Management.Automation.Runspaces.RunspaceFactory]::CreateRunspacePool(1, $cpuCount, $sessionState, $Host)
                $runspacePool.Open()

                $stressScript = {
                    param($sync)
                    $val = 1.0001
                    $counter = 0
                    while (-not $sync.Stop) {
                        for ($j = 0; $j -lt 5000; $j++) {
                            $val = [Math]::Sin($val) * [Math]::Cos($val) + [Math]::Sqrt($val + 1.0)
                        }
                        $counter++
                        if ($counter % 20 -eq 0) {
                            [System.Threading.Thread]::Yield() | Out-Null
                        }
                    }
                }

                for ($i = 0; $i -lt $cpuCount; $i++) {
                    $ps = [powershell]::Create()
                    $ps.RunspacePool = $runspacePool
                    [void]$ps.AddScript($stressScript).AddArgument($syncState)
                    [void]$stressInstances.Add(@{
                        Instance    = $ps
                        AsyncResult = $ps.BeginInvoke()
                    })
                }
                if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                    Write-ServisLog "Pokrenut intenzivni 100% CPU load na svih $cpuCount logičkih jezgara..." -Level "INFO" -Component "StressTest"
                }
            }

            $stopwatch.Start()

            $loopLimit = $DurationSeconds
            if ($useControlledInput -and $controlledInput -and $null -ne $controlledInput.test_iterations -and [int]$controlledInput.test_iterations -gt 0) {
                $loopLimit = [int]$controlledInput.test_iterations
            }

            for ($sec = 1; $sec -le $loopLimit; $sec++) {
                # Provera ručnog otkazivanja (cancel marker ili skip signal ili kontrolisani test ulaz)
                $cancelMarker = Join-Path $LocalSetupDir ".cancel_stress"
                if ((Test-Path $cancelMarker) -or (Test-Path "C:\setup\.cancel_stress")) {
                    Remove-Item -Path $cancelMarker -Force -ErrorAction SilentlyContinue
                    Remove-Item -Path "C:\setup\.cancel_stress" -Force -ErrorAction SilentlyContinue
                    $manualCancel = $true
                    $abortedEarly = $true
                    $syncState.Stop = $true
                    if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                        Write-ServisLog "Stres test je ručno otkazan od strane operatera (.cancel_stress)." -Level "WARN" -Component "StressTest"
                    }
                    break
                }
                if ((Get-Command Test-ServisSkipRequested -ErrorAction SilentlyContinue) -and (Test-ServisSkipRequested)) {
                    $manualCancel = $true
                    $abortedEarly = $true
                    $syncState.Stop = $true
                    if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                        Write-ServisLog "Stres test je ručno prekinut preko UI signala." -Level "WARN" -Component "StressTest"
                    }
                    break
                }
                if ($useControlledInput -and $controlledInput) {
                    if ($controlledInput.manual_cancel -eq $true -or $controlledInput.cancel_requested -eq $true -or "$($controlledInput.status)".ToUpper() -eq "CANCELLED" -or "$($controlledInput.reason)".ToUpper() -eq "MANUAL_CANCEL") {
                        $cancelAtSec = if ($null -ne $controlledInput.cancel_at_sec) { [int]$controlledInput.cancel_at_sec } else { 1 }
                        if ($sec -ge $cancelAtSec) {
                            $manualCancel = $true
                            $abortedEarly = $true
                            $syncState.Stop = $true
                            break
                        }
                    }
                }

                $pct = [int][math]::Min(95, [math]::Max(10, [math]::Round(($sec / [double]$loopLimit) * 90.0)))
                $curCpu = 0.0
                $curGpu = 0.0

                if ($useControlledInput -and $controlledInput) {
                    $sensorSource = "CONTROLLED_TEST_INPUT"

                    if ($controlledInput.samples -and @($controlledInput.samples).Count -gt 0) {
                        $sampleArr = @($controlledInput.samples)
                        $sIdx = [Math]::Min($sec - 1, $sampleArr.Count - 1)
                        $curCpu = [double]$sampleArr[$sIdx]
                    } elseif ($null -ne $controlledInput.max_cpu_temp_c) {
                        $curCpu = [double]$controlledInput.max_cpu_temp_c
                    } elseif ($null -ne $controlledInput.max_cpu_temp) {
                        $curCpu = [double]$controlledInput.max_cpu_temp
                    } elseif ($null -ne $controlledInput.cpu_temp_c) {
                        $curCpu = [double]$controlledInput.cpu_temp_c
                    } elseif ($null -ne $controlledInput.cpu_temp) {
                        $curCpu = [double]$controlledInput.cpu_temp
                    } elseif ($null -ne $controlledInput.temperature) {
                        $curCpu = [double]$controlledInput.temperature
                    }

                    if ($null -ne $controlledInput.max_gpu_temp_c) {
                        $curGpu = [double]$controlledInput.max_gpu_temp_c
                    } elseif ($null -ne $controlledInput.max_gpu_temp) {
                        $curGpu = [double]$controlledInput.max_gpu_temp
                    } elseif ($null -ne $controlledInput.gpu_temp_c) {
                        $curGpu = [double]$controlledInput.gpu_temp_c
                    } elseif ($null -ne $controlledInput.gpu_temp) {
                        $curGpu = [double]$controlledInput.gpu_temp
                    }

                    if ($controlledInput.sensor_available -eq $false -or "$($controlledInput.status)".ToUpper() -eq "UNKNOWN" -or "$($controlledInput.reason)".ToUpper() -eq "SENSOR_UNAVAILABLE") {
                        $curCpu = 0.0
                        $curGpu = 0.0
                        $sensorAvailable = $false
                    } elseif ($curCpu -gt 0.0) {
                        $sensorAvailable = $true
                    }

                    if ($controlledInput.throttling_detected -eq $true) {
                        $throttlingDetected = $true
                    }
                    if ($controlledInput.emergency_abort -eq $true) {
                        $emergencyAbort = $true
                    }
                } else {
                    # 1) Pokušaj očitavanja iz LibreHardwareMonitor / OpenHardwareMonitor WMI
                    try {
                        foreach ($lhmNs in @("root\LibreHardwareMonitor", "root\OpenHardwareMonitor")) {
                            $lhmSensors = Get-CimInstance -Namespace $lhmNs -ClassName Sensor -Filter "SensorType='Temperature'" -ErrorAction SilentlyContinue
                            if ($lhmSensors) {
                                foreach ($s in $lhmSensors) {
                                    if ($null -ne $s.Value) {
                                        $valC = [math]::Round([double]$s.Value, 1)
                                        if ($valC -gt 0.0 -and $valC -lt 130.0) {
                                            if ($s.Name -match '(?i)GPU|Graphics|Hot Spot') {
                                                if ($valC -gt $curGpu) { $curGpu = $valC }
                                            } else {
                                                if ($valC -gt $curCpu) {
                                                    $curCpu = $valC
                                                    $sensorAvailable = $true
                                                    $sensorSource = "LHM_WMI"
                                                }
                                            }
                                        }
                                    }
                                }
                                if ($curCpu -gt 0.0) { break }
                            }
                        }
                    } catch {}

                    # 2) Očitavanje ACPI / WMI termalnih zona ako LHM nije vratio temperaturu
                    if ($curCpu -le 0.0) {
                        try {
                            $tzList = Get-CimInstance -Namespace root\wmi -ClassName MSAcpi_ThermalZoneTemperature -ErrorAction SilentlyContinue
                            if ($tzList) {
                                foreach ($tz in $tzList) {
                                    if ($tz.CurrentTemperature -gt 2732) {
                                        $c = [math]::Round(($tz.CurrentTemperature / 10.0) - 273.15, 1)
                                        if ($c -gt 0.0 -and $c -gt $curCpu -and $c -lt 130.0) {
                                            $curCpu = $c
                                            $sensorAvailable = $true
                                            if ($sensorSource -eq "NONE") { $sensorSource = "ACPI_THERMAL" }
                                        }
                                    }
                                }
                            }
                        } catch {}
                    }

                    # 3) Očitavanje Win32_PerfFormattedData_Counters_ThermalZoneInformation ako ACPI nije dostupan
                    if ($curCpu -le 0.0) {
                        try {
                            $perfTz = Get-CimInstance -ClassName Win32_PerfFormattedData_Counters_ThermalZoneInformation -ErrorAction SilentlyContinue
                            if ($perfTz) {
                                foreach ($pt in $perfTz) {
                                    if ($pt.Temperature -gt 273) {
                                        $c = [math]::Round([double]$pt.Temperature - 273.15, 1)
                                        if ($c -gt 0.0 -and $c -gt $curCpu -and $c -lt 130.0) {
                                            $curCpu = $c
                                            $sensorAvailable = $true
                                            if ($sensorSource -eq "NONE") { $sensorSource = "PERF_COUNTER" }
                                        }
                                    }
                                }
                            }
                        } catch {}
                    }
                }

                if ($curCpu -gt $maxCpuTemp) { $maxCpuTemp = $curCpu }
                if ($curGpu -gt $maxGpuTemp) { $maxGpuTemp = $curGpu }

                # Sigurnosni Emergency Abort prag: >= 98°C ili eksplicitni emergency_abort
                if ($emergencyAbort -eq $true -or $curCpu -ge 98.0 -or $curGpu -ge 98.0 -or $maxCpuTemp -ge 98.0) {
                    $emergencyAbort = $true
                    $throttlingDetected = $true
                    $abortedEarly = $true
                    $syncState.Stop = $true

                    if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                        Write-ServisLog "SIGURNOSNI PREKID STRES TESTA: Detektovana kritična temperatura (${maxCpuTemp}°C >= 98°C)! Odmah zaustavljam opterećenje." -Level "ERROR" -Component "StressTest"
                    }
                    if (Get-Command Send-IpcLog -ErrorAction SilentlyContinue) {
                        Send-IpcLog -Message "EMERGENCY ABORT: Temperatura dostigla ${maxCpuTemp}°C (>= 98°C)!" -Level "FATAL" -Tag "[ALERT]" -Phase 5
                    }

                    # Zvučni signal upozorenja
                    try {
                        if (Get-Command Send-ServisAudioSignal -ErrorAction SilentlyContinue) {
                            Send-ServisAudioSignal -Type "Alert"
                        } else {
                            [Console]::Beep(1000, 250)
                            Start-Sleep -Milliseconds 100
                            [Console]::Beep(800, 350)
                        }
                    } catch {}

                    break
                }

                if ($curCpu -ge 92.0 -or $maxCpuTemp -ge 92.0) {
                    $throttlingDetected = $true
                }

                if (Get-Command Send-IpcProgress -ErrorAction SilentlyContinue) {
                    $tempInfo = if ($sensorAvailable -and $maxCpuTemp -gt 0) { " (Trenutno: ${curCpu}°C | Max CPU: ${maxCpuTemp}°C)" } else { " (Senzor temperature nije dostupan)" }
                    Send-IpcProgress -Progress $pct -Phase 5 -PhaseName "Stres Test" -SubPhase "Termalno opterećenje ($sec/${loopLimit}s)" -StatusMessage "Termalni 100% stres test u toku: $sec / ${loopLimit}s$tempInfo"
                }

                if ($useControlledInput -and $controlledInput -and $null -ne $controlledInput.sleep_ms) {
                    $sleepMs = [int]$controlledInput.sleep_ms
                    if ($sleepMs -gt 0) {
                        Start-Sleep -Milliseconds $sleepMs
                    }
                } else {
                    Start-Sleep -Seconds 1
                }
            }
        }
    } catch {
        $executionError = $true
        $abortedEarly = $true
        $errorDetail = $_.Exception.Message
        $syncState.Stop = $true
        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
            Write-ServisLog "Greška tokom izvršavanja termalnog stres testa: $errorDetail" -Level "ERROR" -Component "StressTest"
        }
    } finally {
        if ($stopwatch.IsRunning) {
            $stopwatch.Stop()
        }
        # Garantovano zaustavljanje svih stres niti i oslobađanje resursa
        $syncState.Stop = $true
        foreach ($item in $stressInstances) {
            try {
                if ($item.Instance) {
                    $item.Instance.Stop()
                    $item.Instance.Dispose()
                }
            } catch {}
        }
        $stressInstances.Clear()
        if ($runspacePool) {
            try {
                $runspacePool.Close()
                $runspacePool.Dispose()
            } catch {}
        }
        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
            Write-ServisLog "Stres niti uspešno zaustavljene i resursi oslobođeni." -Level "INFO" -Component "StressTest"
        }
    }

    # Stvarno izmereno trajanje preko Stopwatch-a (nikada lažiranih fiksnih 30s ako je prekinuto ranije)
    $actualDurationSec = if ($abortedEarly) {
        [int][Math]::Floor($stopwatch.Elapsed.TotalSeconds)
    } else {
        [int][Math]::Round($stopwatch.Elapsed.TotalSeconds)
    }
    if (-not $abortedEarly -and $useControlledInput -and $controlledInput -and $null -ne $controlledInput.duration_sec -and $actualDurationSec -le 0) {
        $actualDurationSec = [int]$controlledInput.duration_sec
    }

    # Provera validnosti očitane temperature (> 0 iz stvarnog senzora ili kontrolisanog ulaza)
    if (-not $sensorAvailable -or $maxCpuTemp -le 0.0) {
        $sensorAvailable = $false
        if (-not $useControlledInput) {
            $sensorSource = "NONE"
        }
    }

    # Stroga pravila evaluacije statusa i razloga
    $status = "UNKNOWN"
    $reason = "SENSOR_UNAVAILABLE"

    if ($emergencyAbort -eq $true -or $maxCpuTemp -ge 98.0) {
        $emergencyAbort = $true
        $status = "THERMAL_CRITICAL"
        $reason = "EMERGENCY_THERMAL_ABORT"
    } elseif ($maxCpuTemp -ge 95.0) {
        $status = "THERMAL_CRITICAL"
        $reason = "CRITICAL_TEMP_THRESHOLD"
    } elseif ($executionError) {
        $status = "ERROR"
        $reason = "EXECUTION_ERROR"
    } elseif ($manualCancel) {
        $status = "CANCELLED"
        $reason = "MANUAL_CANCEL"
    } elseif (-not $sensorAvailable -or $maxCpuTemp -le 0.0) {
        $status = "UNKNOWN"
        $reason = "SENSOR_UNAVAILABLE"
    } elseif ($throttlingDetected -or $maxCpuTemp -ge 85.0) {
        $status = "WARN"
        $reason = "HIGH_TEMP_WARN"
    } else {
        $status = "PASS"
        $reason = "COMPLETED_NORMAL"
    }

    $reportStr = switch ($reason) {
        "EMERGENCY_THERMAL_ABORT" { "KRITIČNO (Emergency Abort na 98°C: Max CPU ${maxCpuTemp}°C, Trajanje: ${actualDurationSec}s)" }
        "CRITICAL_TEMP_THRESHOLD" { "KRITIČNO (Prekoračena bezbedna temperatura >= 95°C: Max CPU ${maxCpuTemp}°C, Trajanje: ${actualDurationSec}s)" }
        "EXECUTION_ERROR"         { "GREŠKA IZVRŠAVANJA STRES TESTA ($errorDetail)" }
        "MANUAL_CANCEL"           { "OTKAZANO (Stres test ručno prekinut nakon ${actualDurationSec}s)" }
        "SENSOR_UNAVAILABLE"      { "NEPOZNATO (Termalni senzori nisu dostupni - temperatura nije izmerena)" }
        "HIGH_TEMP_WARN"          { "UPOZORENJE (Povišena temperatura pod 100% opterećenjem: Max CPU ${maxCpuTemp}°C)" }
        default                   { "ISPRAVNO (Stabilno hlađenje pod 100% opterećenjem, Max CPU: ${maxCpuTemp}°C)" }
    }

    $timestampIso = (Get-Date -Format "o")
    $diagWarn = if ($status -eq "UNKNOWN" -or -not $sensorAvailable) { "Senzori temperature nisu dostupni na hardveru" } else { "" }
    $resultObj = [PSCustomObject]@{
        run_id              = [string]$runId
        status              = [string]$status
        reason              = [string]$reason
        emergency_abort     = [bool]$emergencyAbort
        sensor_available    = [bool]$sensorAvailable
        sensor_source       = [string]$sensorSource
        duration_sec        = [int]$actualDurationSec
        duration_seconds    = [int]$actualDurationSec
        max_cpu_temp_c      = [double]$maxCpuTemp
        max_gpu_temp_c      = [double]$maxGpuTemp
        max_cpu_temp        = [double]$maxCpuTemp
        max_gpu_temp        = [double]$maxGpuTemp
        throttling_detected = [bool]$throttlingDetected
        diagnostics_warning = [string]$diagWarn
        timestamp           = [string]$timestampIso
        stress_report       = [string]$reportStr
    }

    # Upis u C:\setup\stress-test-result.json i C:\setup\deploy-state.json (uz stress_history)
    if (Get-Command Save-ServisStressTestResult -ErrorAction SilentlyContinue) {
        Save-ServisStressTestResult -StressResult $resultObj -LocalSetupDir $LocalSetupDir
    } else {
        try {
            if (-not (Test-Path $LocalSetupDir)) {
                New-Item -Path $LocalSetupDir -ItemType Directory -Force | Out-Null
            }
            $resFile = Join-Path $LocalSetupDir "stress-test-result.json"
            $stateFile = Join-Path $LocalSetupDir "deploy-state.json"
            $utf8NoBom = New-Object System.Text.UTF8Encoding($false)

            $stateObj = [ordered]@{}
            if (Test-Path $stateFile) {
                $rawState = Get-Content -Path $stateFile -Raw -Encoding UTF8 -ErrorAction SilentlyContinue
                if ($rawState) {
                    $parsed = $rawState | ConvertFrom-Json -ErrorAction SilentlyContinue
                    if ($parsed) {
                        foreach ($p in $parsed.PSObject.Properties) {
                            $stateObj[$p.Name] = $p.Value
                        }
                    }
                }
            }

            $historyList = [System.Collections.ArrayList]::new()
            if ($stateObj.Contains("stress_history") -and $null -ne $stateObj["stress_history"]) {
                foreach ($h in @($stateObj["stress_history"])) {
                    if ($null -ne $h) { [void]$historyList.Add($h) }
                }
            }
            if ($stateObj.Contains("stress_status") -and "$($stateObj["stress_run_id"])" -ne $runId) {
                $prev = [PSCustomObject]@{
                    run_id              = if ($stateObj.Contains("stress_run_id")) { "$($stateObj["stress_run_id"])" } else { "" }
                    status              = "$($stateObj["stress_status"])"
                    reason              = if ($stateObj.Contains("stress_reason")) { "$($stateObj["stress_reason"])" } else { "" }
                    emergency_abort     = if ($stateObj.Contains("emergency_abort")) { [bool]$stateObj["emergency_abort"] } else { $false }
                    sensor_available    = if ($stateObj.Contains("sensor_available")) { [bool]$stateObj["sensor_available"] } else { $false }
                    sensor_source       = if ($stateObj.Contains("sensor_source")) { "$($stateObj["sensor_source"])" } else { "NONE" }
                    duration_sec        = if ($stateObj.Contains("stress_duration_sec")) { [int]$stateObj["stress_duration_sec"] } else { 0 }
                    max_cpu_temp_c      = if ($stateObj.Contains("max_cpu_temp")) { [double]$stateObj["max_cpu_temp"] } else { 0.0 }
                    max_gpu_temp_c      = if ($stateObj.Contains("max_gpu_temp")) { [double]$stateObj["max_gpu_temp"] } else { 0.0 }
                    throttling_detected = if ($stateObj.Contains("throttling_detected")) { [bool]$stateObj["throttling_detected"] } else { $false }
                    stress_report       = if ($stateObj.Contains("stress_report")) { "$($stateObj["stress_report"])" } else { "" }
                    timestamp           = if ($stateObj.Contains("stress_timestamp")) { "$($stateObj["stress_timestamp"])" } else { "" }
                }
                [void]$historyList.Add($prev)
            }

            $contractOut = [ordered]@{
                run_id              = $runId
                status              = $status
                reason              = $reason
                emergency_abort     = [bool]$emergencyAbort
                sensor_available    = [bool]$sensorAvailable
                sensor_source       = $sensorSource
                duration_sec        = [int]$actualDurationSec
                duration_seconds    = [int]$actualDurationSec
                max_cpu_temp_c      = [double]$maxCpuTemp
                max_gpu_temp_c      = [double]$maxGpuTemp
                max_cpu_temp        = [double]$maxCpuTemp
                max_gpu_temp        = [double]$maxGpuTemp
                throttling_detected = [bool]$throttlingDetected
                diagnostics_warning = $diagWarn
                timestamp           = $timestampIso
                stress_report       = $reportStr
                stress_history      = @($historyList.ToArray())
            }
            [System.IO.File]::WriteAllText($resFile, ($contractOut | ConvertTo-Json -Depth 6), $utf8NoBom)

            $stateObj["stress_run_id"]       = $runId
            $stateObj["stress_status"]       = $status
            $stateObj["stress_reason"]       = $reason
            $stateObj["emergency_abort"]     = [bool]$emergencyAbort
            $stateObj["sensor_available"]    = [bool]$sensorAvailable
            $stateObj["sensor_source"]       = $sensorSource
            $stateObj["stress_duration_sec"] = [int]$actualDurationSec
            $stateObj["max_cpu_temp"]        = [double]$maxCpuTemp
            $stateObj["max_gpu_temp"]        = [double]$maxGpuTemp
            $stateObj["max_cpu_temp_c"]      = [double]$maxCpuTemp
            $stateObj["max_gpu_temp_c"]      = [double]$maxGpuTemp
            $stateObj["throttling_detected"] = [bool]$throttlingDetected
            $stateObj["stress_report"]       = "$reportStr"
            $stateObj["stress_timestamp"]    = $timestampIso
            $stateObj["diagnostics_warning"] = $diagWarn
            $diagContract = Get-WisDiagnosticsContract -Status $status -SensorAvailable $sensorAvailable -MaxCpuTemp $maxCpuTemp -EmergencyAbort $emergencyAbort
            $stateObj["diagnostics_status"] = $diagContract.diagnostics_status
            $stateObj["cooling_verified"] = $diagContract.cooling_verified
            $stateObj["manual_check_required"] = $diagContract.manual_check_required
            $stateObj["stress_history"]      = @($historyList.ToArray())
            $stateObj["updated_at"]          = $timestampIso

            [System.IO.File]::WriteAllText($stateFile, ($stateObj | ConvertTo-Json -Depth 6), $utf8NoBom)
        } catch {
            if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                Write-ServisLog "Upozorenje pri upisu rezultata stres testa: $($_.Exception.Message)" -Level "WARN" -Component "StressTest"
            }
        }
    }

    $logLvl = switch ($status) {
        "THERMAL_CRITICAL" { "ERROR" }
        "ERROR"            { "ERROR" }
        "CANCELLED"        { "WARN" }
        "UNKNOWN"          { "WARN" }
        "WARN"             { "WARN" }
        default            { "SUCCESS" }
    }
    if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
        Write-ServisLog "Rezultat termalnog stres testa [$status / $reason]: $reportStr (Max CPU: ${maxCpuTemp}°C, Max GPU: ${maxGpuTemp}°C, Senzor: $sensorAvailable [$sensorSource], Trajanje: ${actualDurationSec}s, Abort: $emergencyAbort)" -Level $logLvl -Component "StressTest"
    }

    return $resultObj
}

function Invoke-ServisStressTest {
    <#
    .SYNOPSIS
        Alias/wrapper za Start-ServisStressTest (autoritativni termalni stres test).
    #>
    [CmdletBinding()]
    param(
        [int]$DurationSeconds = 30,
        [string]$LocalSetupDir = "C:\setup"
    )
    return (Start-ServisStressTest -DurationSeconds $DurationSeconds -LocalSetupDir $LocalSetupDir)
}

function Start-HardwareStressTest {
    <#
    .SYNOPSIS
        Alias/wrapper za Start-ServisStressTest (autoritativni termalni stres test).
    #>
    [CmdletBinding()]
    param(
        [int]$DurationSeconds = 30,
        [string]$LocalSetupDir = "C:\setup"
    )
    return (Start-ServisStressTest -DurationSeconds $DurationSeconds -LocalSetupDir $LocalSetupDir)
}

function Test-WisDeviceManagerClean {
    <#
    .SYNOPSIS
        Proverava da li su svi uredjaji u Device Manager-u ispravno instalirani
        (nema ConfigManagerErrorCode razlicitog od 0, 22 ili 45, niti generickih Basic Display/Render adaptera).
    .OUTPUTS
        Hashtable sa kljucevima Done, Clean, Reason, ProblemDevices.
    #>
    [CmdletBinding()]
    param()

    $problems = @()

    # 1. PnP Entity provera (Get-CimInstance uz fallback na Get-WmiObject)
    try {
        $pnpEntities = $null
        try {
            $pnpEntities = Get-CimInstance -ClassName Win32_PnPEntity -OperationTimeoutSec 15 -ErrorAction Stop
        } catch {
            $pnpEntities = Get-WmiObject -Class Win32_PnPEntity -ErrorAction SilentlyContinue
        }

        if ($pnpEntities) {
            foreach ($dev in $pnpEntities) {
                $code = $dev.ConfigManagerErrorCode
                if ($null -ne $code -and $code -ne 0 -and $code -ne 22 -and $code -ne 45) {
                    $devName = if ($dev.Name) { $dev.Name.Trim() } else { "Nepoznat uredjaj" }
                    $devId = if ($dev.DeviceID) { $dev.DeviceID.Trim() } else { "" }
                    $problems += [PSCustomObject]@{
                        Name      = $devName
                        DeviceID  = $devId
                        ErrorCode = [int]$code
                        Type      = "PnPError"
                    }
                }
            }
        }
    } catch {
        return @{
            Done           = $false
            Clean          = $false
            Reason         = "Greska pri ocitavanju PnP uredjaja: $($_.Exception.Message)"
            ProblemDevices = @()
        }
    }

    # 2. VideoController provera (da li je ostao Basic Display ili Render Driver)
    try {
        $gpus = $null
        try {
            $gpus = Get-CimInstance -ClassName Win32_VideoController -OperationTimeoutSec 10 -ErrorAction Stop
        } catch {
            $gpus = Get-WmiObject -Class Win32_VideoController -ErrorAction SilentlyContinue
        }

        if ($gpus) {
            foreach ($gpu in $gpus) {
                $gpuName = if ($gpu.Name) { $gpu.Name.Trim() } else { "" }
                if ($gpuName -like "*Microsoft Basic Display Adapter*" -or $gpuName -like "*Microsoft Basic Render Driver*") {
                    $problems += [PSCustomObject]@{
                        Name      = $gpuName
                        DeviceID  = if ($gpu.PNPDeviceID) { $gpu.PNPDeviceID.Trim() } else { "" }
                        ErrorCode = -1
                        Type      = "GenericDisplayDriver"
                    }
                }
            }
        }
    } catch {
        return @{
            Done           = $false
            Clean          = $false
            Reason         = "Greska pri ocitavanju grafickog adaptera: $($_.Exception.Message)"
            ProblemDevices = @()
        }
    }

    $isClean = ($problems.Count -eq 0)
    $reasonStr = ""
    if ($isClean) {
        $reasonStr = "Device Manager je cist (svi uredjaji prepoznati, nema zutih uzvicnika niti Basic Display adaptera)"
    } else {
        $sampleNames = ($problems | ForEach-Object { "$($_.Name) (Kod $($_.ErrorCode))" } | Select-Object -First 3) -join ", "
        $count = $problems.Count
        if ($count -gt 3) {
            $reasonStr = "Pronadjeno $count neispravnih/generickih uredjaja ($sampleNames, ...)"
        } else {
            $reasonStr = "Pronadjeno $count neispravnih/generickih uredjaja ($sampleNames)"
        }
    }

    return @{
        Done           = $isClean
        Clean          = $isClean
        Reason         = $reasonStr
        ProblemDevices = $problems
    }
}

Export-ModuleMember -Function `
    Get-HardwareDiagnosticsInfo, `
    Get-HardwareHealthStatus, `
    Get-SystemHardwareInfo, `
    Get-HwInfo, `
    Test-BatteryPresent, `
    Get-MissingHardwareCount, `
    Get-PnpMissingDriversDetail, `
    Get-HardwareBrand, `
    Get-ServisHubEndpoint, `
    Install-DriverFromMsCatalog, `
    Install-OemDriverFromHub, `
    Resolve-MissingDriversFromHub, `
    Start-ServisStressTest, `
    Invoke-ServisStressTest, `
    Start-HardwareStressTest, `
    Test-WisDeviceManagerClean



