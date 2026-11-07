<#
.MODULE
    WIS.Network.psm1
.DESCRIPTION
    Mrezna automatizacija, servisni Wi-Fi, Dual-Homing (LAN + Wi-Fi) i fallback mehanizmi za WIS (Tehno kutak Windows Installation Assistant).
.COMPATIBILITY
    Windows PowerShell 5.1+
#>

function Test-Internet {
    [CmdletBinding()]
    param(
        [int]$TimeoutMs = 1500
    )
    # 1. Brza TCP soket provera ka javnim DNS serverima (port 53) - radi cak i kada je ICMP ping blokiran na ruteru
    foreach ($dnsIp in @("1.1.1.1", "8.8.8.8", "9.9.9.9")) {
        try {
            $tcp = New-Object System.Net.Sockets.TcpClient
            $iar = $tcp.BeginConnect($dnsIp, 53, $null, $null)
            $success = $iar.AsyncWaitHandle.WaitOne($TimeoutMs, $false)
            if ($success -and $tcp.Connected) {
                $tcp.Close()
                return $true
            }
            $tcp.Close()
        } catch {}
    }

    # 2. HTTP provera ka msftconnecttest.com (kratak timeout)
    try {
        $req = [System.Net.HttpWebRequest]::Create("http://www.msftconnecttest.com/connecttest.txt")
        $req.Timeout = $TimeoutMs
        $req.Method = "GET"
        $resp = $req.GetResponse()
        $resp.Close()
        return $true
    } catch {}

    # 3. ICMP Ping fallback ka javnom DNS-u
    try {
        if (Test-Connection -ComputerName 8.8.8.8 -Count 1 -Quiet -ErrorAction SilentlyContinue) {
            return $true
        }
    } catch {}

    return $false
}

function Get-NetworkDetailStatus {
    $lanPresent = $false
    $lanConnected = $false
    $lanName = "Nije pronadjen"
    $lanSpeed = ""

    $wifiPresent = $false
    $wifiConnected = $false
    $wifiSsid = ""
    $wifiSignal = ""
    $wifiAdapterName = "Nije pronadjen"

    # 1. Provera zicnog (LAN / Ethernet) adaptera
    try {
        $netAdapters = Get-NetAdapter -ErrorAction SilentlyContinue
        if ($netAdapters) {
            $lanList = $netAdapters | Where-Object {
                $_.InterfaceDescription -notmatch 'Wi-Fi|Wireless|802.11|Bluetooth|Virtual|Hyper-V|vEthernet|Loopback|TAP|VPN' -and
                $_.Name -notmatch 'Wi-Fi|Wireless|Bluetooth|vEthernet' -and
                $_.HardwareInterface -eq $true
            }
            if ($lanList) {
                $lanPresent = $true
                $activeLan = $lanList | Where-Object { $_.Status -eq 'Up' } | Select-Object -First 1
                if ($activeLan) {
                    $lanConnected = $true
                    $lanName = ($activeLan.InterfaceDescription -replace '\s+', ' ').Trim()
                    $lanSpeed = if ($activeLan.LinkSpeed) { $activeLan.LinkSpeed.Trim() } else { "" }
                } else {
                    $firstLan = $lanList | Select-Object -First 1
                    $lanName = ($firstLan.InterfaceDescription -replace '\s+', ' ').Trim()
                }
            }

            # 2. Provera bezicnog (Wi-Fi) adaptera
            $wList = $netAdapters | Where-Object {
                $_.InterfaceDescription -match 'Wi-Fi|Wireless|802.11' -or $_.Name -match 'Wi-Fi'
            }
            if ($wList) {
                $wifiPresent = $true
                $wa = $wList | Select-Object -First 1
                $wifiAdapterName = ($wa.InterfaceDescription -replace '\s+', ' ').Trim()
                if ($wa.Status -eq 'Up') {
                    $wifiConnected = $true
                }
            }
        }
    } catch {}

    # 3. Precizno ocitavanje trenutne Wi-Fi veze preko netsh (samo ako postoji Wi-Fi adapter)
    if ($wifiPresent) {
        try {
            $wlanOut = netsh wlan show interfaces 2>$null
            if ($wlanOut) {
                foreach ($line in $wlanOut) {
                    if ($line -match '^\s*SSID\s*:\s*(.+)$') {
                        $val = $matches[1].Trim()
                        if ($val -and $val -ne "none" -and $val -ne "<none>") {
                            $wifiSsid = $val
                        }
                    } elseif ($line -match '^\s*State\s*:\s*(.+)$') {
                        $st = $matches[1].Trim().ToLower()
                        if ($st -match 'connected|povezano') {
                            $wifiConnected = $true
                        }
                    } elseif ($line -match '^\s*Signal\s*:\s*(.+)$') {
                        $wifiSignal = $matches[1].Trim()
                    }
                }
            }
        } catch {}
        finally {
            $global:LASTEXITCODE = 0
        }
    } else {
        $global:LASTEXITCODE = 0
    }

    # Ako je u Global:State zabelezen SSID, a netsh ga nije uhvatio
    if (-not $wifiSsid -and $Global:State -and $Global:State.WifiSSID) {
        $wifiSsid = $Global:State.WifiSSID
    }

    $netActive = Test-Internet
    $both = ($lanConnected -and $wifiConnected)
    $global:LASTEXITCODE = 0

    return [PSCustomObject]@{
        LanPresent      = $lanPresent
        LanConnected    = $lanConnected
        LanName         = $lanName
        LanSpeed        = $lanSpeed
        WifiPresent     = $wifiPresent
        WifiConnected   = $wifiConnected
        WifiSSID        = $wifiSsid
        WifiSignal      = $wifiSignal
        WifiAdapterName = $wifiAdapterName
        InternetActive  = $netActive
        BothConnected   = $both
    }
}

function Connect-ServisWifi {
    param(
        [string]$LocalSetupDir = "C:\setup"
    )

    # Pronalazenje konfiguracionog fajla Wifi-Servis.ini
    $wifiIniCandidates = @(
        (Join-Path $LocalSetupDir "Wifi-Servis.ini"),
        (Join-Path $PSScriptRoot "Wifi-Servis.ini"),
        (Join-Path (Split-Path $PSScriptRoot -Parent) "Skripte\Wifi-Servis.ini"),
        "C:\setup\Wifi-Servis.ini",
        "C:\Users\HP\Desktop\windows 11\Skripte\Wifi-Servis.ini"
    )
    foreach ($d in (Get-PSDrive -PSProvider FileSystem)) {
        $p = Join-Path "$($d.Root)" "Skripte\Wifi-Servis.ini"
        if ($wifiIniCandidates -notcontains $p) { $wifiIniCandidates += $p }
        $p2 = Join-Path "$($d.Root)" "Wifi-Servis.ini"
        if ($wifiIniCandidates -notcontains $p2) { $wifiIniCandidates += $p2 }
    }

    $wifiIni = $wifiIniCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $wifiIni) {
        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
            Write-ServisLog "Wifi-Servis.ini nije pronadjen medju kandidatima." -Level "DEBUG" -Component "Network"
        }
        $global:LASTEXITCODE = 0
        return (Test-Internet)
    }

    $iniContent = Get-Content $wifiIni -Raw
    $ssid = if ($iniContent -match 'SSID=(.+)') { $matches[1].Trim() } else { '' }
    $pass = if ($iniContent -match 'Password=(.+)') { $matches[1].Trim() } else { '' }
    $enabled = if ($iniContent -match 'Enabled=1') { $true } else { $false }

    if (-not ($enabled -and $ssid -and $pass)) {
        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
            Write-ServisLog "Wi-Fi profil je onemogucen ili nepotpun u $wifiIni" -Level "DEBUG" -Component "Network"
        }
        $global:LASTEXITCODE = 0
        return (Test-Internet)
    }

    # Provera postojanja Wi-Fi adaptera
    $hasWifiAdapter = $false
    try {
        $wList = Get-NetAdapter -ErrorAction SilentlyContinue | Where-Object {
            $_.InterfaceDescription -match 'Wi-Fi|Wireless|802.11' -or $_.Name -match 'Wi-Fi'
        }
        if ($wList) { $hasWifiAdapter = $true }
    } catch {}

    if (-not $hasWifiAdapter) {
        Install-FallbackNetworkDrivers
        try {
            $wList = Get-NetAdapter -ErrorAction SilentlyContinue | Where-Object {
                $_.InterfaceDescription -match 'Wi-Fi|Wireless|802.11' -or $_.Name -match 'Wi-Fi'
            }
            if ($wList) { $hasWifiAdapter = $true }
        } catch {}
    }

    if (-not $hasWifiAdapter) {
        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
            Write-ServisLog "Uredjaj nema detektovan Wi-Fi adapter ili nedostaje drajver." -Level "WARN" -Component "Network"
        }
        $global:LASTEXITCODE = 0
        return (Test-Internet)
    }

    if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
        Write-ServisLog "Pronadjena Wi-Fi konfiguracija: $wifiIni" -Level "INFO" -Component "Network"
    }

    $ssids = ($ssid -split ',') | ForEach-Object { $_.Trim() } | Where-Object { $_ }
    $connectedAnyWifi = $false

    foreach ($currentSsid in $ssids) {
        if (Get-Command Show-ServisDashboard -ErrorAction SilentlyContinue) {
            Show-ServisDashboard -LiveStatus "Konfigurisem servisni Wi-Fi profil za: $currentSsid ..."
        }

        # Generisanje WLAN profila sa trajnim unosom za sve korisnike (user=all) i XML escaping-om (Nalaz 17)
        $escapedSsid = [System.Security.SecurityElement]::Escape([string]$currentSsid)
        $escapedPass = [System.Security.SecurityElement]::Escape([string]$pass)
        $xml = @"
<?xml version="1.0"?>
<WLANProfile xmlns="http://www.microsoft.com/networking/WLAN/profile/v1">
    <name>$escapedSsid</name>
    <SSIDConfig>
        <SSID><name>$escapedSsid</name></SSID>
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
        if (-not (Test-Path $LocalSetupDir)) {
            New-Item -Path $LocalSetupDir -ItemType Directory -Force | Out-Null
        }
        $xmlPath = Join-Path $LocalSetupDir "wifi_profile.xml"
        $utf8NoBom = New-Object System.Text.UTF8Encoding($false)

        # Dodavanje profila u sistem trajno za sve korisnike (user=all) uz trenutno brisanje u finally bloku (U1)
        $addExitCode = -1
        $addOutStr = ""
        try {
            [System.IO.File]::WriteAllText($xmlPath, $xml, $utf8NoBom)
            $addOut = & netsh.exe wlan add profile filename="$xmlPath" user=all 2>&1
            $addExitCode = $LASTEXITCODE
            $addOutStr = ($addOut -join " ").Trim()
        } catch {
            $addOutStr = $_.Exception.Message
        } finally {
            Remove-Item -Path $xmlPath -Force -ErrorAction SilentlyContinue
            $global:LASTEXITCODE = 0
        }

        if ($addExitCode -eq 0) {
            if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                Write-ServisLog "WLAN profil za '$currentSsid' uspesno dodat (ExitCode=0): $addOutStr" -Level "INFO" -Component "Network"
            }
        } else {
            if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                Write-ServisLog "Greska pri uvozu WLAN profila za '$currentSsid' (ExitCode=$addExitCode): $addOutStr" -Level "WARN" -Component "Network"
            }
            continue
        }

        # Pokusaj povezivanja na mrezu cak i ako je LAN aktivan (omogucava Dual-Homing)
        if (Get-Command Show-ServisDashboard -ErrorAction SilentlyContinue) {
            Show-ServisDashboard -LiveStatus "Povezujem se na servisni Wi-Fi: $currentSsid ..."
        }
        $connExitCode = -1
        $connOutStr = ""
        try {
            $connOut = & netsh.exe wlan connect name="$currentSsid" 2>&1
            $connExitCode = $LASTEXITCODE
            $connOutStr = ($connOut -join " ").Trim()
        } catch {
            $connOutStr = $_.Exception.Message
        } finally {
            $global:LASTEXITCODE = 0
        }
        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
            $connLogLvl = if ($connExitCode -eq 0) { "INFO" } else { "WARN" }
            Write-ServisLog "Rezultat netsh wlan connect za '$currentSsid' (ExitCode=$connExitCode): $connOutStr" -Level $connLogLvl -Component "Network"
        }
        Start-Sleep -Seconds 3

        # Provera da li je adapter uspostavio vezu
        $curConnected = $false
        try {
            $wlanCheck = netsh wlan show interfaces 2>$null
            if ($wlanCheck) {
                $wlanStr = $wlanCheck -join " "
                if ($wlanStr -match [regex]::Escape($currentSsid) -and ($wlanStr -match 'State\s*:\s*connected|State\s*:\s*povezano')) {
                    $curConnected = $true
                }
            }
        } catch {}
        finally {
            $global:LASTEXITCODE = 0
        }

        if ($curConnected) {
            $connectedAnyWifi = $true
            if ($Global:State) {
                $Global:State.WifiSSID = $currentSsid
            }
            if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                Write-ServisLog "Uspesno povezan na servisni Wi-Fi: $currentSsid (Sistemski profil registrovan za sve korisnike)" -Level "SUCCESS" -Component "Network"
            }
            break
        }
    }

    # Ako je Wi-Fi povezan ili LAN ima internet, imamo konekciju
    $global:LASTEXITCODE = 0
    return (Test-Internet)
}

function Wait-ForNetworkConnection {
    Install-FallbackNetworkDrivers
    $waitLoopCount = 0
    while (-not (Test-Internet)) {
        $waitLoopCount++
        if ($waitLoopCount % 3 -eq 1) {
            if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                Write-ServisLog "Cekam mreznu/internet konekciju (LAN kabl ili Wi-Fi)..." -Level "WARN" -Component "Network"
            }
        }
        $netStat = if (Get-Command Get-NetworkDetailStatus -ErrorAction SilentlyContinue) {
            Get-NetworkDetailStatus
        } else { $null }

        $subLogs = @(
            "1. Wi-Fi mreza nije pronadjena ili lozinka nije prosla.",
            "2. Prikljucite mrezni LAN kabl (Ethernet) u laptop.",
            "3. Skripta automatski proverava konekciju svakih 5 sekundi...",
            "4. Rad se nastavlja cim se uspostavi internet."
        )
        if ($netStat -and -not $netStat.WifiPresent) {
            $subLogs[0] = "1. [UPOZORENJE] Nema Wi-Fi drajvera - mrezni LAN kabl je OBAVEZAN!"
        }
        # Ako je proslo 40 sekundi (8 iteracija), a LAN je povezan ili racunar ima lokalnu IP adresu (nije 169.254):
        if ($waitLoopCount -ge 8) {
            $hasLocalIp = $false
            try {
                $ips = Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue | Where-Object { $_.IPAddress -notmatch '^(127\.|169\.254\.)' }
                if ($ips) { $hasLocalIp = $true }
            } catch {}

            if (($netStat -and $netStat.LanConnected) -or $hasLocalIp) {
                if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                    Write-ServisLog "Detektovana aktivna lokalna mreza i dodeljena IP adresa. Nastavljam rad deployment procesa." -Level "SUCCESS" -Component "Network"
                }
                break
            }
        }
        Start-Sleep -Seconds 5
    }

    $netStat = if (Get-Command Get-NetworkDetailStatus -ErrorAction SilentlyContinue) {
        Get-NetworkDetailStatus
    } else { $null }

    $connType = if ($netStat -and $netStat.BothConnected) {
        "DUAL-HOMING (LAN kabl + Wi-Fi: '$($netStat.WifiSSID)')"
    } elseif ($netStat -and $netStat.LanConnected) {
        "MREZNI KABL (LAN: $($netStat.LanSpeed))"
    } elseif ($netStat -and $netStat.WifiConnected) {
        "WI-FI (SSID: '$($netStat.WifiSSID)')"
    } else {
        "Aktivna mreza"
    }

    if (Get-Command Show-ServisDashboard -ErrorAction SilentlyContinue) {
        Show-ServisDashboard -LiveStatus "[OK] Internet je uspostavljen preko: $connType!"
    }
    if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
        Write-ServisLog "Internet konekcija je aktivna ($connType)." -Level "SUCCESS" -Component "Network"
    }
    Start-Sleep -Seconds 2
    $global:LASTEXITCODE = 0
    return $true
}

function Install-FallbackNetworkDrivers {
    <#
    .SYNOPSIS
        Proverava prisustvo mreznih adaptera i instalira fallback mrezne drajvere ako nedostaju.
    .DESCRIPTION
        Ukoliko na sistemu nema nijednog mreznog adaptera ili nema Wi-Fi adaptera, funkcija
        pretrazuje lokalne i USB repozitorijume sa drajverima za mrezu i pokrece pnputil.exe.
    #>
    try {
        $adapters = Get-NetAdapter -ErrorAction SilentlyContinue
        $hasAny = ($adapters -and $adapters.Count -gt 0)
        $hasWifi = $false
        if ($hasAny) {
            $wifiAdapters = $adapters | Where-Object {
                $_.InterfaceDescription -match 'Wi-Fi|Wireless|802.11' -or $_.Name -match 'Wi-Fi'
            }
            if ($wifiAdapters) { $hasWifi = $true }
        }

        if (-not $hasAny -or -not $hasWifi) {
            if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                Write-ServisLog "Nedostaje mrezni adapter (Ukupno adaptera: $(if ($adapters) { $adapters.Count } else { 0 }), Wi-Fi prisutan: $hasWifi). Trazim fallback mrezne drajvere..." -Level "WARN" -Component "Network"
            }

            $candidateDirs = @(
                "C:\setup\Drivers\Net",
                (Join-Path $PSScriptRoot "..\..\sources\`$OEM`$\`$1\setup\Drivers\Net"),
                (Join-Path $PSScriptRoot "..\..\Programi\Drivers\Net"),
                "C:\setup\Programi\Drivers\Net"
            )
            if ($Global:SourceRoot) {
                $candidateDirs += (Join-Path $Global:SourceRoot "sources\`$OEM`$\`$1\setup\Drivers\Net")
                $candidateDirs += (Join-Path $Global:SourceRoot "Programi\Drivers\Net")
                $candidateDirs += (Join-Path $Global:SourceRoot "Drivers\Net")
            }
            foreach ($d in (Get-PSDrive -PSProvider FileSystem -ErrorAction SilentlyContinue)) {
                $candidateDirs += (Join-Path "$($d.Root)" "Programi\Drivers\Net")
                $candidateDirs += (Join-Path "$($d.Root)" "Drivers\Net")
                $candidateDirs += (Join-Path "$($d.Root)" "sources\`$OEM`$\`$1\setup\Drivers\Net")
            }

            $installedAny = $false
            foreach ($driverDir in $candidateDirs) {
                if ($driverDir -and (Test-Path $driverDir)) {
                    $infFiles = Get-ChildItem -Path $driverDir -Filter "*.inf" -Recurse -File -ErrorAction SilentlyContinue
                    if ($infFiles -and $infFiles.Count -gt 0) {
                        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                            Write-ServisLog "Pronadjen fallback paket mreznih drajvera u: $driverDir ($($infFiles.Count) .inf fajlova). Pokrecem pnputil..." -Level "INFO" -Component "Network"
                        }
                        if (Get-Command Show-ServisDashboard -ErrorAction SilentlyContinue) {
                            Show-ServisDashboard -LiveStatus "Instaliram fallback mrezne drajvere iz $driverDir..."
                        }
                        try {
                            & pnputil.exe /add-driver "$driverDir\*.inf" /subdirs /install | Out-Null
                        } catch {}
                        finally {
                            $global:LASTEXITCODE = 0
                        }
                        $installedAny = $true
                    }
                }
            }

            if ($installedAny) {
                if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                    Write-ServisLog "Fallback drajveri instalirani. Cekam 4 sekunde za PnP registraciju..." -Level "INFO" -Component "Network"
                }
                Start-Sleep -Seconds 4
                $adapters = Get-NetAdapter -ErrorAction SilentlyContinue
                if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                    Write-ServisLog "Nakon PnP registracije detektovano $($adapters.Count) mreznih adaptera." -Level "SUCCESS" -Component "Network"
                }
            } else {
                if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                    Write-ServisLog "Nisu pronadjeni fallback mrezni drajveri na zadatim putanjama." -Level "DEBUG" -Component "Network"
                }
            }
        }
    } catch {
        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
            Write-ServisLog "Greska tokom provere fallback drajvera za mrezu: $($_.Exception.Message)" -Level "WARN" -Component "Network"
        }
    } finally {
        $global:LASTEXITCODE = 0
    }
}

function Optimize-ServisNetworkStack {
    <#
    .SYNOPSIS
        Otkljucavanje i optimizacija mreznog steka i SMB protokola za maksimalni protok flote.
    .DESCRIPTION
        WIS-TASK-052: Konfigurise SMB klijent (onemogucavanje throttling-a, omogucavanje Large MTU,
        visestruke RSS konekcije), podesava TCP autotuning na normal, ukljucuje RSS, iskljucuje
        heuristiku i postavlja LanmanWorkstation parametre u registru radi postizanja punih 1 Gbps / 2.5 Gbps brzina.
    #>
    [CmdletBinding()]
    param()

    try {
        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
            Write-ServisLog "Zapocinjem optimizaciju mreznog steka i SMB klijenta za maksimalan protok..." -Level "STEP" -Component "Network"
        }

        # 1. Konfiguracija SMB klijenta preko Set-SmbClientConfiguration
        try {
            if (Get-Command Set-SmbClientConfiguration -ErrorAction SilentlyContinue) {
                Set-SmbClientConfiguration -EnableBandwidthThrottling $false -EnableLargeMtu $true -ConnectionCountPerRssNetworkInterface 4 -Force -Confirm:$false -ErrorAction SilentlyContinue
                if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                    Write-ServisLog "Set-SmbClientConfiguration primenjen: Throttling=False, LargeMtu=True, RSSConnections=4." -Level "SUCCESS" -Component "Network"
                }
            }
        } catch {
            if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                Write-ServisLog "UPOZORENJE: Set-SmbClientConfiguration nije uspeo: $($_.Exception.Message)" -Level "WARN" -Component "Network"
            }
        }

        # 2. LanmanWorkstation registry parametri
        try {
            $lanmanPath = "HKLM:\SYSTEM\CurrentControlSet\Services\LanmanWorkstation\Parameters"
            if (-not (Test-Path $lanmanPath)) {
                New-Item -Path $lanmanPath -Force -ErrorAction SilentlyContinue | Out-Null
            }
            Set-ItemProperty -Path $lanmanPath -Name "DisableBandwidthThrottling" -Value 1 -Type DWord -Force -ErrorAction SilentlyContinue
            Set-ItemProperty -Path $lanmanPath -Name "MaxCmds" -Value 2048 -Type DWord -Force -ErrorAction SilentlyContinue
            Set-ItemProperty -Path $lanmanPath -Name "AllowInsecureGuestAuth" -Value 1 -Type DWord -Force -ErrorAction SilentlyContinue
            Set-ItemProperty -Path $lanmanPath -Name "RequireSecuritySignature" -Value 0 -Type DWord -Force -ErrorAction SilentlyContinue
        } catch {
            if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                Write-ServisLog "UPOZORENJE: Upis LanmanWorkstation parametara u registar nije uspeo: $($_.Exception.Message)" -Level "WARN" -Component "Network"
            }
        }

        # 3. TCP optimizacija (PowerShell native i asinhroni neblokirajuci pozivi)
        try {
            if (Get-Command Set-NetTCPSetting -ErrorAction SilentlyContinue) {
                Set-NetTCPSetting -SettingName InternetCustom -AutoTuningLevelLocal Normal -ScalingHeuristics Disabled -Confirm:$false -ErrorAction SilentlyContinue
            }
        } catch {}
        try {
            Start-Process -FilePath "netsh.exe" -ArgumentList "int tcp set global autotuninglevel=normal" -WindowStyle Hidden -ErrorAction SilentlyContinue
            Start-Process -FilePath "netsh.exe" -ArgumentList "int tcp set global rss=enabled" -WindowStyle Hidden -ErrorAction SilentlyContinue
            Start-Process -FilePath "netsh.exe" -ArgumentList "int tcp set heuristics disabled" -WindowStyle Hidden -ErrorAction SilentlyContinue
        } catch {
            if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                Write-ServisLog "UPOZORENJE: Asinhrono pokretanje netsh TCP tweakova nije uspelo: $($_.Exception.Message)" -Level "WARN" -Component "Network"
            }
        } finally {
            $global:LASTEXITCODE = 0
        }

        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
            Write-ServisLog "Mrezni stek i SMB klijent su uspesno optimizovani za mrezno ubrzanje." -Level "SUCCESS" -Component "Network"
        }
    } catch {
        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
            Write-ServisLog "Greska pri optimizaciji mreznog steka: $($_.Exception.Message)" -Level "WARN" -Component "Network"
        }
    } finally {
        $global:LASTEXITCODE = 0
    }
}

function Initialize-ServisNetwork {
    <#
    .SYNOPSIS
        Inicijalizacija mreznog podsistema sa automatskom proverom i instalacijom fallback drajvera.
    #>
    param(
        [string]$LocalSetupDir = "C:\setup"
    )
    Install-FallbackNetworkDrivers
    Optimize-ServisNetworkStack
    Invoke-NetworkPhase -LocalSetupDir $LocalSetupDir
    $global:LASTEXITCODE = 0
}

function Invoke-Phase1Network {
    [CmdletBinding()]
    param(
        [string]$LocalSetupDir = "C:\setup",
        [string]$HubUrl = ""
    )

    if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
        Write-ServisLog "=== POKRETANJE FAZE 1: Mreza, Servisni Wi-Fi i Hub Discovery ===" -Level "STEP" -Component "Phase1"
    }
    if (Get-Command Send-IpcProgress -ErrorAction SilentlyContinue) {
        Send-IpcProgress -Progress 5 -Phase 1 -PhaseName "Mreža" -SubPhase "Inicijalizacija" -StatusMessage "Pokretanje mrežnog podsistema..."
    }

    if (Get-Command Wait-ServisUICooperativePause -ErrorAction SilentlyContinue) {
        Wait-ServisUICooperativePause -CheckpointName "Faza 1 - Pocetak"
    }

    # 1. Provera i instalacija fallback mreznih drajvera ukoliko nedostaju adapteri (Best-Effort)
    if (Get-Command Send-IpcProgress -ErrorAction SilentlyContinue) {
        Send-IpcProgress -Progress 15 -Phase 1 -PhaseName "Mreža" -SubPhase "Mrežni drajveri" -StatusMessage "Provera mrežnih adaptera..."
    }
    try {
        Install-FallbackNetworkDrivers
    } catch {
        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
            Write-ServisLog "Upozorenje pri proveri mrežnih adaptera: $($_.Exception.Message)" -Level "WARN" -Component "Network"
        }
        if (Get-Command Send-IpcLog -ErrorAction SilentlyContinue) {
            Send-IpcLog -Message "Upozorenje pri proveri mrežnih drajvera: $($_.Exception.Message)" -Level "WARN" -Phase 1
        }
    }

    # 2. Optimizacija mreznog steka i SMB protokola za gigabitni protok (Best-Effort)
    if (Get-Command Send-IpcProgress -ErrorAction SilentlyContinue) {
        Send-IpcProgress -Progress 25 -Phase 1 -PhaseName "Mreža" -SubPhase "Optimizacija steka" -StatusMessage "Optimizacija mrežnog steka i SMB parametara..."
    }
    try {
        Optimize-ServisNetworkStack
    } catch {
        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
            Write-ServisLog "Upozorenje pri optimizaciji mrežnog steka: $($_.Exception.Message)" -Level "WARN" -Component "Network"
        }
    }

    if (Get-Command Wait-ServisUICooperativePause -ErrorAction SilentlyContinue) {
        Wait-ServisUICooperativePause -CheckpointName "Faza 1 - Pre provere mreze"
    }

    # 3. Pametna petlja provere mrezne funkcionalnosti (do 20 sekundi, koraci od 2s)
    $maxWaitSec = 20
    $stepSec = 2
    $loopStopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    $wifiAttempted = $false
    $fastPathSuccess = $false
    $finalStatusMessage = ""

    $linkOk = $false
    $hubFound = $false
    $hubLatencyMs = -1
    $hubResolvedUrl = ""
    $hasInternet = $false
    $localIp = "127.0.0.1"
    $gwIp = ""
    $wifiSsid = ""
    $lanConnected = $false
    $wifiConnected = $false
    $connType = "Lokalna mreža"
    $netPayload = $null
    $progressPct = 30

    while ($loopStopwatch.Elapsed.TotalSeconds -lt $maxWaitSec) {
        $elapsedSec = [int]$loopStopwatch.Elapsed.TotalSeconds
        $progressPct = [Math]::Min(95, 30 + [int](($elapsedSec / [double]$maxWaitSec) * 60))

        # a) Provera aktivnog linka (LAN ili Wi-Fi sa validnom IPv4 adresom koja nije 127.0.0.1 niti 169.254.*)
        $netDetail = $null
        try {
            $netDetail = Get-NetworkDetailStatus
        } catch {}

        $lanConnected = if ($netDetail) { [bool]$netDetail.LanConnected } else { $false }
        $wifiConnected = if ($netDetail) { [bool]$netDetail.WifiConnected } else { $false }
        $wifiSsid = if ($netDetail -and $netDetail.WifiSSID) { $netDetail.WifiSSID } else { "" }
        $wifiPresent = if ($netDetail) { [bool]$netDetail.WifiPresent } else { $false }

        # Ocitavanje validne IPv4 adrese
        $currentIp = ""
        try {
            $validIps = Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue | Where-Object {
                $_.InterfaceAlias -notmatch 'Loopback|vEthernet' -and
                $_.IPAddress -notmatch '^(127\.|169\.254\.)'
            }
            if ($validIps -and $validIps.Count -gt 0) {
                $currentIp = $validIps[0].IPAddress
            }
        } catch {}

        if ($currentIp) {
            $localIp = $currentIp
        }

        # Ocitavanje Default Gateway-a
        try {
            $gwObj = Get-NetRoute -DestinationPrefix "0.0.0.0/0" -ErrorAction SilentlyContinue | Where-Object {
                $_.NextHop -and $_.NextHop -ne "0.0.0.0"
            } | Select-Object -First 1
            if ($gwObj) { $gwIp = $gwObj.NextHop }
        } catch {}

        $hasValidIp = ($localIp -and $localIp -notmatch '^(127\.|169\.254\.)')
        $linkOk = ($hasValidIp -and ($lanConnected -or $wifiConnected))

        # Ako nema veze, a postoji Wi-Fi adapter i Wifi-Servis.ini, pozovi Connect-ServisWifi
        if (-not $linkOk -and -not $wifiAttempted -and $wifiPresent) {
            if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                Write-ServisLog "Nema aktivnog mrežnog linka. Pokušavam povezivanje na servisni Wi-Fi iz Wifi-Servis.ini..." -Level "INFO" -Component "Network"
            }
            try {
                $wifiConnected = Connect-ServisWifi -LocalSetupDir $LocalSetupDir
                $wifiAttempted = $true
                $netDetail = Get-NetworkDetailStatus
                if ($netDetail) {
                    $lanConnected = [bool]$netDetail.LanConnected
                    $wifiConnected = [bool]$netDetail.WifiConnected
                    $wifiSsid = $netDetail.WifiSSID
                }
                $validIps = Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue | Where-Object {
                    $_.InterfaceAlias -notmatch 'Loopback|vEthernet' -and
                    $_.IPAddress -notmatch '^(127\.|169\.254\.)'
                }
                if ($validIps -and $validIps.Count -gt 0) {
                    $localIp = $validIps[0].IPAddress
                }
                $hasValidIp = ($localIp -and $localIp -notmatch '^(127\.|169\.254\.)')
                $linkOk = ($hasValidIp -and ($lanConnected -or $wifiConnected))
            } catch {
                $wifiAttempted = $true
            }
        }

        $connType = if ($netDetail -and $netDetail.BothConnected) {
            "Dual-Homing (LAN + Wi-Fi)"
        } elseif ($lanConnected) {
            "LAN Kabl"
        } elseif ($wifiConnected) {
            "Wi-Fi ($wifiSsid)"
        } else {
            "Nema konekcije"
        }

        # b) Provera ServisHub-a: Testiraj http://192.168.170.254:8080/api/status (ili gateway IP ili $Global:ServisHubUrl)
        # Izmeri vreme odziva u ms ([System.Diagnostics.Stopwatch])
        $hubCandidates = @()
        if ($HubUrl) { $hubCandidates += $HubUrl.TrimEnd('/') }
        if ($Global:ServisHubUrl) { $hubCandidates += $Global:ServisHubUrl.TrimEnd('/') }
        $hubCandidates += "http://192.168.170.254:8080"
        if ($gwIp) { $hubCandidates += "http://${gwIp}:8080" }
        $hubCandidates = $hubCandidates | Select-Object -Unique

        $hubFound = $false
        $hubLatencyMs = -1
        $hubResolvedUrl = ""

        if ($linkOk) {
            foreach ($cand in $hubCandidates) {
                $endpoints = @("$cand/api/status", "$cand/api/devices")
                foreach ($ep in $endpoints) {
                    $sw = [System.Diagnostics.Stopwatch]::StartNew()
                    try {
                        $req = [System.Net.HttpWebRequest]::Create($ep)
                        $req.Timeout = 1200
                        $req.Method = "GET"
                        $resp = $req.GetResponse()
                        $resp.Close()
                        $sw.Stop()
                        $hubLatencyMs = [int]$sw.ElapsedMilliseconds
                        $hubFound = $true
                        $hubResolvedUrl = $cand
                        break
                    } catch {
                        $sw.Stop()
                    }
                }
                if ($hubFound) { break }
            }
        }

        if ($hubFound) {
            $Global:ServisHubUrl = $hubResolvedUrl
        }

        # c) Provera Interneta: Brzi test preko Test-Internet (kratak timeout od 1s)
        $hasInternet = $false
        if ($linkOk) {
            try {
                $hasInternet = Test-Internet -TimeoutMs 1000
            } catch {
                $hasInternet = $false
            }
        }

        # d) Prikaz telemetrije: Pri svakom koraku šalji Send-IpcProgress sa statusom i Send-IpcStatus sa mrežnim podacima
        $netPayload = [ordered]@{
            ip                = $localIp
            client_ip         = $localIp
            gateway           = $gwIp
            wifi_ssid         = $wifiSsid
            wifi_connected    = $wifiConnected
            lan_connected     = $lanConnected
            nas_latency       = if ($hubFound) { "$hubLatencyMs ms" } else { "N/A" }
            nas_latency_ms    = if ($hubFound) { $hubLatencyMs } else { 0 }
            nas_status        = if ($hubFound) { "Online" } else { "Offline" }
            internet_active   = [bool]$hasInternet
            hub_url           = $hubResolvedUrl
            hub_reachable     = [bool]$hubFound
            network_fast_pass = $false
            connection_type   = $connType
        }

        $stepStatusMsg = if (-not $linkOk) {
            "Čekam mrežni link (LAN kabl ili Wi-Fi)... [$($elapsedSec)s/$($maxWaitSec)s]"
        } elseif (-not $hubFound) {
            "Link aktivan ($localIp). Tražim ServisHub... [$($elapsedSec)s/$($maxWaitSec)s]"
        } else {
            "ServisHub verifikovan ($hubLatencyMs ms). Proveravam internet..."
        }

        if (Get-Command Send-IpcProgress -ErrorAction SilentlyContinue) {
            Send-IpcProgress -Progress $progressPct -Phase 1 -PhaseName "Mreža" -SubPhase "Verifikacija" -StatusMessage $stepStatusMsg
        }
        if (Get-Command Send-IpcStatus -ErrorAction SilentlyContinue) {
            Send-IpcStatus -Phase 1 -Progress $progressPct -SubPhase "Verifikacija" -StatusMessage $stepStatusMsg -Network $netPayload
        }

        # e) Kriterijum za trenutni Instant Fast-Pass završetak:
        # Čim proveri link: ako bilo koji adapter ima link ili IP, i ima interneta ili ServisHub:
        $hasLinkOrIp = ($linkOk -or ($hasValidIp -and ($lanConnected -or $wifiConnected)))
        if ($hasLinkOrIp -and ($hubFound -or $hasInternet)) {
            $fastPathSuccess = $true
            if ($hubFound -and $hasInternet) {
                $finalStatusMessage = "Mreža, Internet i ServisHub (latencija: {0} ms) verifikovani!" -f $hubLatencyMs
            } elseif ($hubFound) {
                $finalStatusMessage = "ServisHub dostupan lokalno (latencija: {0} ms). Nastavljam sa lokalnog skladišta..." -f $hubLatencyMs
            } else {
                # ServisHub nije pronađen na lokalnoj mreži, ali računar ima internet konekciju
                $finalStatusMessage = "Mreža aktivna (Internet režim). ServisHub nije detektovan — koristi se Internet/Winget fallback."
            }
            break
        }

        Start-Sleep -Seconds $stepSec
    }

    # Ako nakon isteka petlje fastPathSuccess još nije postavljen,
    # proveravamo da li uređaj ima link ili internet:
    if (-not $fastPathSuccess) {
        if ($hasInternet) {
            $fastPathSuccess = $true
            $finalStatusMessage = "Mreža aktivna (Internet režim). ServisHub nije detektovan — koristi se Internet/Winget fallback."
        } elseif ($linkOk) {
            # Uređaj ima mrežni link (kabl ili Wi-Fi) iako ServisHub i javni internet nisu odgovorili
            $fastPathSuccess = $true
            $finalStatusMessage = "Mreža aktivna (Lokalna mreža). ServisHub i Internet nisu detektovani — koristi se lokalni keš paketa."
        }
    }

    # Završna obrada rezultata petlje
    if ($fastPathSuccess) {
        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
            Write-ServisLog "Fast-Path USPEH: $finalStatusMessage (Konekcija: $connType, IP: $localIp, Hub: $hubResolvedUrl)" -Level "SUCCESS" -Component "Phase1"
        }

        try {
            if (Get-Command Save-DeployStatePhase -ErrorAction SilentlyContinue) {
                Save-DeployStatePhase -Phase 2
            }
        } catch {}

        # Perzistencija mrežnih i ServisHub polja u deploy-state.json
        try {
            $stateFiles = @(
                (Join-Path $LocalSetupDir "deploy-state.json"),
                "C:\setup\deploy-state.json"
            ) | Select-Object -Unique
            $utf8NoBom = New-Object System.Text.UTF8Encoding($false)
            foreach ($sf in $stateFiles) {
                try {
                    $sfDir = Split-Path $sf -Parent
                    if ($sfDir -and -not (Test-Path $sfDir)) {
                        New-Item -Path $sfDir -ItemType Directory -Force -ErrorAction SilentlyContinue | Out-Null
                    }
                    $stateMap = [ordered]@{}
                    if (Test-Path $sf) {
                        $raw = Get-Content -Path $sf -Raw -Encoding UTF8 -ErrorAction SilentlyContinue
                        if ($raw) {
                            $obj = ConvertFrom-Json -InputObject $raw -ErrorAction SilentlyContinue
                            if ($obj) {
                                foreach ($p in $obj.PSObject.Properties) {
                                    $stateMap[$p.Name] = $p.Value
                                }
                            }
                        }
                    }
                    $stateMap["internet_active"]   = [bool]$hasInternet
                    $stateMap["hub_url"]           = $hubResolvedUrl
                    $stateMap["hub_reachable"]     = [bool]$hubFound
                    $stateMap["network_fast_pass"] = $false
                    $stateMap["client_ip"]         = $localIp
                    $stateMap["updated_at"]        = (Get-Date -Format "o")
                    $jsonOut = ConvertTo-Json -InputObject $stateMap -Depth 6 -Compress
                    [System.IO.File]::WriteAllText($sf, $jsonOut, $utf8NoBom)
                } catch {}
            }
        } catch {}

        if (Get-Command Send-IpcStatus -ErrorAction SilentlyContinue) {
            Send-IpcStatus -Phase 1 -Progress 100 -SubPhase "Završeno" -StatusMessage $finalStatusMessage -Network $netPayload
        }
        if (Get-Command Send-IpcProgress -ErrorAction SilentlyContinue) {
            Send-IpcProgress -Progress 100 -Phase 1 -PhaseName "Mreža" -SubPhase "Završeno" -StatusMessage $finalStatusMessage
        }

        $global:LASTEXITCODE = 0
        return $true
    }

    # f) Izuzetak ($failMsg) baciti ISKLJUČIVO ako uređaj nema NI mrežni link (nema kabla / Wi-Fi) NI internet:
    $failMsg = "Nije detektovan mrežni kabl niti internet konekcija. Priključite LAN kabl (Ethernet) ili konfigurišite Wi-Fi mrežu u Wifi-Servis.ini."

    if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
        Write-ServisLog "FAZA 1 NIJE USPELA: $failMsg" -Level "ERROR" -Component "Phase1"
    }
    if (Get-Command Send-IpcLog -ErrorAction SilentlyContinue) {
        Send-IpcLog -Message $failMsg -Level "ERROR" -Tag "[ERROR]" -Phase 1
    }
    if (Get-Command Send-IpcStatus -ErrorAction SilentlyContinue) {
        Send-IpcStatus -Phase 1 -Progress $progressPct -SubPhase "Greška" -StatusMessage $failMsg -Network $netPayload
    }

    $global:LASTEXITCODE = 1
    throw $failMsg
}

function Initialize-ServisHubFastPass {
    <#
    .SYNOPSIS
        Nalaz 18: Lagana, neblokirajuća ServisHub inicijalizacija i registracija za Instant Fast-Pass granu Faze 1.
    .DESCRIPTION
        Ne vrši ponovno povezivanje na Wi-Fi niti instalaciju mrežnih drajvera.
        Razrešava kandidat HubUrl po prioritetu (eksplicitni -HubUrl -> Wifi-Servis.ini -> deploy-state.json -> $Global:ServisHubUrl -> brzi HTTP probe 192.168.170.254:8080 / gateway:8080 sa maks. 1500 ms timeout-om).
        Proverava stvarnu dostupnost ServisHub-a (GET <HubUrl>/api/status sa 1500 ms timeout-om) odvojeno od dostupnosti interneta.
        Upisuje internet_active, hub_url, hub_reachable, network_fast_pass i client_ip u deploy-state.json, emituje Send-IpcStatus i šalje inicijalnu STARTED / heartbeat registraciju ako je Hub dostupan.
    #>
    [CmdletBinding()]
    param(
        [string]$LocalSetupDir = "C:\setup",
        [string]$HubUrl = "",
        [string]$ClientIp = "",
        [string]$ConnectionType = "Aktivna mreža",
        [bool]$InternetActive = $true
    )

    $ipAddr = $ClientIp
    if (-not $ipAddr) {
        try {
            $validIpObj = Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue | Where-Object {
                $_.InterfaceAlias -notmatch 'Loopback|vEthernet' -and
                $_.IPAddress -notmatch '^(127\.|169\.254\.)'
            } | Select-Object -First 1
            if ($validIpObj) { $ipAddr = $validIpObj.IPAddress }
        } catch {}
    }
    if (-not $ipAddr) { $ipAddr = "127.0.0.1" }

    $gwIp = ""
    try {
        $gwObj = Get-NetRoute -DestinationPrefix "0.0.0.0/0" -ErrorAction SilentlyContinue | Where-Object {
            $_.NextHop -and $_.NextHop -ne "0.0.0.0"
        } | Select-Object -First 1
        if ($gwObj) { $gwIp = $gwObj.NextHop }
    } catch {}

    $candidateUrls = New-Object System.Collections.Generic.List[string]
    $configuredCandidate = ""

    # 1. Eksplicitni -HubUrl parametar
    if (-not [string]::IsNullOrWhiteSpace($HubUrl) -and $HubUrl -notmatch '^-') {
        $cleanExplicit = $HubUrl.Trim().TrimEnd('/')
        if ($cleanExplicit -notmatch '^https?://') {
            $cleanExplicit = "http://$cleanExplicit"
        }
        $candidateUrls.Add($cleanExplicit)
        $configuredCandidate = $cleanExplicit
    }

    # 2. Wifi-Servis.ini ([ServisHub] Url=..., HubUrl=..., Host=... / ServerHost=...)
    $wifiIniCandidates = @(
        (Join-Path $LocalSetupDir "Wifi-Servis.ini"),
        (Join-Path $PSScriptRoot "Wifi-Servis.ini"),
        (Join-Path (Split-Path $PSScriptRoot -Parent) "Wifi-Servis.ini"),
        "C:\setup\Wifi-Servis.ini"
    )
    $wifiIni = $wifiIniCandidates | Where-Object { $_ -and (Test-Path $_ -ErrorAction SilentlyContinue) } | Select-Object -First 1
    $hubEnabledInIni = $true
    if ($wifiIni) {
        try {
            $iniRaw = Get-Content -Path $wifiIni -Raw -ErrorAction SilentlyContinue
            if ($iniRaw -match '(?mi)^\s*\[(ServisHub|GdsServer)\]([\s\S]*?)(?:^\s*\[|\Z)') {
                $sec = $matches[2]
                if ($sec -match '(?mi)^\s*Enabled\s*=\s*0') {
                    $hubEnabledInIni = $false
                }
                $iniPort = 8080
                if ($sec -match '(?mi)^\s*(?:ServerPort|Port)\s*=\s*([0-9]+)') {
                    $iniPort = [int]$matches[1].Trim()
                }
                if ($sec -match '(?mi)^\s*(?:HubUrl|Url)\s*=\s*([^\r\n#;]+)') {
                    $rawUrl = $matches[1].Trim().TrimEnd('/')
                    if ($rawUrl -and $rawUrl -ne "auto") {
                        if ($rawUrl -notmatch '^https?://') { $rawUrl = "http://$rawUrl" }
                        if (-not $candidateUrls.Contains($rawUrl)) { $candidateUrls.Add($rawUrl) }
                        if (-not $configuredCandidate) { $configuredCandidate = $rawUrl }
                    }
                }
                if ($sec -match '(?mi)^\s*(?:ServerHost|Host)\s*=\s*([^\r\n#;]+)') {
                    $hostTokens = ($matches[1] -split ',') | ForEach-Object { $_.Trim() } | Where-Object { $_ -and $_ -ne "auto" }
                    foreach ($ht in $hostTokens) {
                        if ($ht -match '^https?://') {
                            $u = $ht.TrimEnd('/')
                            if (-not $candidateUrls.Contains($u)) { $candidateUrls.Add($u) }
                            if (-not $configuredCandidate) { $configuredCandidate = $u }
                        } else {
                            $u1 = "http://${ht}:8080"
                            if (-not $candidateUrls.Contains($u1)) { $candidateUrls.Add($u1) }
                            if ($iniPort -ne 8080) {
                                $u2 = "http://${ht}:${iniPort}"
                                if (-not $candidateUrls.Contains($u2)) { $candidateUrls.Add($u2) }
                            }
                            if (-not $configuredCandidate) { $configuredCandidate = $u1 }
                        }
                    }
                }
            }
        } catch {}
    }

    # 3. Postojeći deploy-state.json (hub_url)
    $statePaths = @(
        (Join-Path $LocalSetupDir "deploy-state.json"),
        "C:\setup\deploy-state.json"
    ) | Select-Object -Unique
    foreach ($sp in $statePaths) {
        if (Test-Path $sp -ErrorAction SilentlyContinue) {
            try {
                $rawState = Get-Content -Path $sp -Raw -Encoding UTF8 -ErrorAction SilentlyContinue
                if ($rawState) {
                    $parsedState = ConvertFrom-Json -InputObject $rawState -ErrorAction SilentlyContinue
                    if ($parsedState -and $parsedState.hub_url -and -not [string]::IsNullOrWhiteSpace("$($parsedState.hub_url)")) {
                        $stHub = "$($parsedState.hub_url)".Trim().TrimEnd('/')
                        if ($stHub -notmatch '^https?://') { $stHub = "http://$stHub" }
                        if (-not $candidateUrls.Contains($stHub)) { $candidateUrls.Add($stHub) }
                        if (-not $configuredCandidate) { $configuredCandidate = $stHub }
                    }
                }
            } catch {}
        }
    }

    # 4. $Global:ServisHubUrl
    if ($Global:ServisHubUrl -and -not [string]::IsNullOrWhiteSpace("$($Global:ServisHubUrl)")) {
        $gHub = "$($Global:ServisHubUrl)".Trim().TrimEnd('/')
        if (-not $candidateUrls.Contains($gHub)) { $candidateUrls.Add($gHub) }
        if (-not $configuredCandidate) { $configuredCandidate = $gHub }
    }

    # 5. Poznate adrese za brzi HTTP probe (192.168.170.254:8080 i gateway:8080)
    $defaultHub = "http://192.168.170.254:8080"
    if (-not $candidateUrls.Contains($defaultHub)) { $candidateUrls.Add($defaultHub) }
    if ($gwIp) {
        $gwHub = "http://${gwIp}:8080"
        if (-not $candidateUrls.Contains($gwHub)) { $candidateUrls.Add($gwHub) }
    }

    # Provera stvarne dostupnosti ServisHub-a (GET <HubUrl>/api/status sa maks. 1500 ms timeout-om)
    $hubReachable = $false
    $resolvedHubUrl = ""
    $hubLatencyMs = -1

    if ($hubEnabledInIni) {
        foreach ($cand in $candidateUrls) {
            if ([string]::IsNullOrWhiteSpace($cand)) { continue }
            $statusEp = "$($cand.TrimEnd('/'))/api/status"
            $sw = [System.Diagnostics.Stopwatch]::StartNew()
            try {
                $req = [System.Net.HttpWebRequest]::Create($statusEp)
                $req.Method = "GET"
                $req.Timeout = 1500
                $req.ReadWriteTimeout = 1500
                $resp = $req.GetResponse()
                $resp.Close()
                $sw.Stop()
                $hubReachable = $true
                $resolvedHubUrl = $cand.TrimEnd('/')
                $hubLatencyMs = [int]$sw.ElapsedMilliseconds
                break
            } catch {
                $sw.Stop()
            }
        }
    }

    if ($hubReachable) {
        $Global:ServisHubUrl = $resolvedHubUrl
    } else {
        # Ako ServisHub nije dostupan, sačuvaj konfigurisani URL (ili prazno) ali označi hub_reachable = $false
        $resolvedHubUrl = if ($configuredCandidate) { $configuredCandidate } else { "" }
        $Global:ServisHubUrl = $null
    }

    # Upis u deploy-state.json
    $utf8NoBom = New-Object System.Text.UTF8Encoding($false)
    foreach ($sp in $statePaths) {
        try {
            $spDir = Split-Path $sp -Parent
            if ($spDir -and -not (Test-Path $spDir)) {
                New-Item -Path $spDir -ItemType Directory -Force -ErrorAction SilentlyContinue | Out-Null
            }
            $stateObj = [ordered]@{}
            if (Test-Path $sp -ErrorAction SilentlyContinue) {
                try {
                    $raw = Get-Content -Path $sp -Raw -Encoding UTF8 -ErrorAction SilentlyContinue
                    if ($raw) {
                        $parsed = ConvertFrom-Json -InputObject $raw -ErrorAction SilentlyContinue
                        if ($parsed) {
                            foreach ($prop in $parsed.PSObject.Properties) {
                                $stateObj[$prop.Name] = $prop.Value
                            }
                        }
                    }
                } catch {}
            }
            $stateObj["internet_active"]   = [bool]$InternetActive
            $stateObj["hub_url"]           = $resolvedHubUrl
            $stateObj["hub_reachable"]     = [bool]$hubReachable
            $stateObj["network_fast_pass"] = $true
            $stateObj["client_ip"]         = $ipAddr
            $stateObj["updated_at"]        = (Get-Date -Format "o")

            $jsonOut = ConvertTo-Json -InputObject $stateObj -Depth 6 -Compress
            [System.IO.File]::WriteAllText($sp, $jsonOut, $utf8NoBom)
        } catch {}
    }

    # Formiranje mrežnog payload-a i slanje IPC statusa
    $netPayload = [ordered]@{
        ip                = $ipAddr
        client_ip         = $ipAddr
        gateway           = $gwIp
        internet_active   = [bool]$InternetActive
        hub_url           = $resolvedHubUrl
        hub_reachable     = [bool]$hubReachable
        network_fast_pass = $true
        nas_status        = if ($hubReachable) { "Online" } else { "Offline" }
        nas_latency       = if ($hubReachable) { "$hubLatencyMs ms" } else { "N/A" }
        nas_latency_ms    = if ($hubReachable) { $hubLatencyMs } else { 0 }
        connection_type   = $ConnectionType
    }

    $fastStatusMsg = if ($hubReachable) {
        "Mreža je već aktivna (Adapter: $ConnectionType, IP: $ipAddr, ServisHub: $resolvedHubUrl [${hubLatencyMs} ms]). Instant Fast-Pass završen."
    } else {
        "Mreža je već aktivna (Adapter: $ConnectionType, IP: $ipAddr, ServisHub: Offline). Instant Fast-Pass završen."
    }

    if (Get-Command Send-IpcStatus -ErrorAction SilentlyContinue) {
        Send-IpcStatus -Phase 1 -Progress 100 -SubPhase "Završeno" -StatusMessage $fastStatusMsg -Network $netPayload
    }

    # Ako je ServisHub dostupan, pošalji inicijalnu registraciju (STARTED događaj / heartbeat) sa kratkim timeout-om (1500 ms)
    $registered = $false
    if ($hubReachable -and $resolvedHubUrl) {
        if (Get-Command Send-ServisLifecycleEvent -ErrorAction SilentlyContinue) {
            try {
                $registered = [bool](Send-ServisLifecycleEvent -EventType "STARTED" -Data @{
                    phase             = 1
                    client_ip         = $ipAddr
                    connection_type   = $ConnectionType
                    network_fast_pass = $true
                    internet_active   = [bool]$InternetActive
                } -LocalSetupDir $LocalSetupDir)
            } catch {}
        }

        if (-not $registered) {
            try {
                $compName = if ($env:COMPUTERNAME) { $env:COMPUTERNAME } else { "KLIJENT" }
                $regPayload = [ordered]@{
                    event             = "STARTED"
                    computer_name     = $compName
                    phase             = 1
                    client_ip         = $ipAddr
                    connection_type   = $ConnectionType
                    network_fast_pass = $true
                    internet_active   = [bool]$InternetActive
                    timestamp         = (Get-Date).ToString("o")
                }
                $jsonBody = ConvertTo-Json -InputObject $regPayload -Compress -Depth 4
                $bytes = [System.Text.Encoding]::UTF8.GetBytes($jsonBody)
                $req = [System.Net.HttpWebRequest]::Create("$resolvedHubUrl/api/lifecycle")
                $req.Method = "POST"
                $req.ContentType = "application/json; charset=utf-8"
                $req.Timeout = 1500
                $req.ReadWriteTimeout = 1500
                $req.ContentLength = $bytes.Length
                $reqStream = $req.GetRequestStream()
                $reqStream.Write($bytes, 0, $bytes.Length)
                $reqStream.Close()
                $resp = $req.GetResponse()
                $resp.Close()
                $registered = $true
            } catch {}
        }
    }

    $global:LASTEXITCODE = 0
    return [PSCustomObject]@{
        InternetActive  = [bool]$InternetActive
        HubUrl          = $resolvedHubUrl
        HubReachable    = [bool]$hubReachable
        HubLatencyMs    = $hubLatencyMs
        ClientIp        = $ipAddr
        NetworkFastPass = $true
        Registered      = [bool]$registered
        StatusMessage   = $fastStatusMsg
        NetworkPayload  = $netPayload
    }
}

function Invoke-NetworkPhase {
    param(
        [string]$LocalSetupDir = "C:\setup"
    )
    Invoke-Phase1Network -LocalSetupDir $LocalSetupDir
}

Export-ModuleMember -Function `
    Test-Internet, `
    Get-NetworkDetailStatus, `
    Connect-ServisWifi, `
    Install-FallbackNetworkDrivers, `
    Optimize-ServisNetworkStack, `
    Initialize-ServisNetwork, `
    Initialize-ServisHubFastPass, `
    Wait-ForNetworkConnection, `
    Invoke-NetworkPhase, `
    Invoke-Phase1Network




