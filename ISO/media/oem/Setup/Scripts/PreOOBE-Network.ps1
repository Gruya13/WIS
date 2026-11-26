# PreOOBE-Network.ps1 - Ukljucivanje mreznih adaptera, servisnog Wi-Fi-ja i OOBE ZTD watchera (IMP-114 Sloj C)
# Omogucava Windows 11 OOBE-u da odmah kontaktira ztd.dds.microsoft.com i detektuje Autopilot bez preranog BypassNRO/SkipNetworkSetup
# Paket A1 + IMP-114: Bezbedan XML escaping, UTF-8 bez BOM-a, maskiranje tajni, brisanje XML-a u finally bloku, PS 5.1 kompatibilno

$script:Utf8NoBom = New-Object System.Text.UTF8Encoding($false)
$script:PreOobeLogPaths = @(
    "C:\Windows\Logs\WIS\preoobe_network.log",
    "C:\setup\preoobe_network.log"
)

function Write-PreOobeLog {
    param(
        [string]$Message,
        [string]$Level = "INFO"
    )
    $sanitized = if ($null -eq $Message) { '' } else { "$Message" }
    $sanitized = $sanitized -replace '(?i)(Password\s*[:=]\s*)([^\s;,\r\n"''&]+)', '$1***MASKIRANO***'
    $sanitized = $sanitized -replace '(?i)(ClientToken\s*[:=]\s*)([^\s;,\r\n"''&]+)', '$1***MASKIRANO***'
    $sanitized = $sanitized -replace '(?i)(<keyMaterial>)(.*?)(</keyMaterial>)', '$1***MASKIRANO***$3'
    $ts = (Get-Date).ToString("yyyy-MM-dd HH:mm:ss.fff")
    $line = "[$ts] [$Level] [PreOOBE-Network] $sanitized"
    foreach ($lp in $script:PreOobeLogPaths) {
        try {
            $dir = Split-Path $lp -Parent
            if ($dir -and -not (Test-Path $dir)) {
                New-Item -Path $dir -ItemType Directory -Force -ErrorAction SilentlyContinue | Out-Null
            }
            [System.IO.File]::AppendAllText($lp, $line + [Environment]::NewLine, $script:Utf8NoBom)
        } catch {}
    }
}

function Test-PreOobeZtdTcp {
    param(
        [string]$TargetHost = 'ztd.dds.microsoft.com',
        [int]$Port = 443,
        [int]$TimeoutMs = 2500
    )
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

function Read-PreOobeWifiSection {
    param([string]$IniPath)
    $res = [PSCustomObject]@{
        Enabled  = $true
        SsidRaw  = ''
        Password = ''
    }
    try {
        $lines = Get-Content -LiteralPath $IniPath -ErrorAction Stop
        $hasSections = $false
        $inWifi = $false
        foreach ($rawLine in $lines) {
            $line = $rawLine.Trim()
            if (-not $line -or $line.StartsWith('#') -or $line.StartsWith(';')) { continue }
            if ($line -match '^\[([^\]]+)\]$') {
                $hasSections = $true
                $inWifi = ($matches[1].Trim() -ieq 'WiFi')
                continue
            }
            if ($hasSections -and (-not $inWifi)) { continue }
            if ($line -match '^\s*Enabled\s*=\s*(.+)$') {
                $res.Enabled = ($matches[1].Trim() -eq '1')
            } elseif ($line -match '^\s*SSID\s*=\s*(.+)$') {
                $res.SsidRaw = $matches[1].Trim()
            } elseif ($line -match '^\s*Password\s*=\s*(.+)$') {
                $res.Password = $matches[1].Trim()
            }
        }
    } catch {}
    return $res
}

# 0. IMP-114 Sloj C: Ukloni prerane OOBE mrezne blokade pre ZTD razgovora sa ztd.dds.microsoft.com
try {
    $oobeKeyPath = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\OOBE'
    if (Test-Path $oobeKeyPath) {
        foreach ($blockerVal in @('BypassNRO', 'SkipNetworkSetup', 'DisableOOBEConnection')) {
            try {
                $existingProp = Get-ItemProperty -Path $oobeKeyPath -Name $blockerVal -ErrorAction SilentlyContinue
                if ($null -ne $existingProp -and $null -ne $existingProp.$blockerVal) {
                    Remove-ItemProperty -Path $oobeKeyPath -Name $blockerVal -Force -ErrorAction SilentlyContinue
                    Write-PreOobeLog "Uklonjena prerana OOBE blokada '$blockerVal' radi realne ZTD provere." "INFO"
                }
            } catch {}
        }
    }
} catch {}

# 1. Aktivacija svih mreznih adaptera (LAN + Wi-Fi) i wlansvc servisa
try {
    Get-NetAdapter -ErrorAction SilentlyContinue | Enable-NetAdapter -Confirm:$false -ErrorAction SilentlyContinue
} catch {}

try {
    & sc.exe config wlansvc start= auto >$null 2>&1
    & net.exe start wlansvc >$null 2>&1
} catch {}

# 2. Uvoz servisnog Wi-Fi profila (ako postoji Wifi-Servis.ini)
$candidates = @("C:\setup\Wifi-Servis.ini")
foreach ($d in (Get-PSDrive -PSProvider FileSystem -ErrorAction SilentlyContinue)) {
    $candidates += (Join-Path $d.Root "Wifi-Servis.ini")
    $candidates += (Join-Path $d.Root "Skripte\Wifi-Servis.ini")
}

$wifiIni = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if ($wifiIni) {
    try {
        $wifiCfg = Read-PreOobeWifiSection -IniPath $wifiIni
        $ssidRaw = $wifiCfg.SsidRaw
        $pass    = $wifiCfg.Password
        $enabled = $wifiCfg.Enabled

        if ($enabled -and $ssidRaw -and $pass -and $ssidRaw -ne 'YOUR_SERVICE_WIFI_SSID' -and $pass -ne 'CHANGE_ME') {
            $ssids = @($ssidRaw -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ })
            $tempDir = if ($env:TEMP -and (Test-Path $env:TEMP)) { $env:TEMP } else { "C:\Windows\Temp" }
            $xmlPath = Join-Path $tempDir "wlan_preoobe.xml"

            foreach ($s in $ssids) {
                $escapedSsid = [System.Security.SecurityElement]::Escape([string]$s)
                $escapedPass = [System.Security.SecurityElement]::Escape([string]$pass)
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
                $addMsg = ""
                try {
                    [System.IO.File]::WriteAllText($xmlPath, $xml, $script:Utf8NoBom)
                    $addOut = & netsh.exe wlan add profile filename="$xmlPath" user=all 2>&1
                    $addExit = $LASTEXITCODE
                    $addMsg = ($addOut -join " ").Trim()
                } finally {
                    Remove-Item -Path $xmlPath -Force -ErrorAction SilentlyContinue
                }

                if ($addExit -eq 0) {
                    Write-PreOobeLog "WLAN profil za '$s' uspesno dodat (ExitCode=0): $addMsg" "SUCCESS"
                    $connOut = & netsh.exe wlan connect name="$s" 2>&1
                    $connExit = $LASTEXITCODE
                    $connMsg = ($connOut -join " ").Trim()
                    $connLvl = if ($connExit -eq 0) { "SUCCESS" } else { "WARN" }
                    Write-PreOobeLog "Rezultat netsh wlan connect za '$s' (ExitCode=$connExit): $connMsg" $connLvl

                    $gotValidIp = $false
                    for ($waitSec = 1; $waitSec -le 12; $waitSec++) {
                        Start-Sleep -Seconds 1
                        try {
                            $validIp = Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue | Where-Object {
                                $_.InterfaceAlias -notmatch 'Loopback|vEthernet' -and
                                $_.IPAddress -notmatch '^(127\.|169\.254\.)'
                            } | Select-Object -First 1
                            if ($validIp -and $validIp.IPAddress) {
                                $gotValidIp = $true
                                Write-PreOobeLog "Wi-Fi '$s' dobio validnu IPv4 adresu ($($validIp.IPAddress)) za ${waitSec}s." "SUCCESS"
                                break
                            }
                        } catch {}
                    }
                    if ($gotValidIp) { break }
                } else {
                    Write-PreOobeLog "Greska pri uvozu WLAN profila za '$s' (ExitCode=$addExit): $addMsg" "WARN"
                }
            }
        }
    } catch {
        Write-PreOobeLog "Izuzetak u PreOOBE-Network.ps1: $($_.Exception.Message)" "WARN"
    }
}

# 3. Provera zive IPv4 konekcije i dostupnosti ztd.dds.microsoft.com:443 (LAN ili Wi-Fi)
$detectedIp = ''
$ztdReachable = $false
for ($netTry = 1; $netTry -le 15; $netTry++) {
    try {
        $ipObj = Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue | Where-Object {
            $_.InterfaceAlias -notmatch 'Loopback|vEthernet' -and
            $_.IPAddress -notmatch '^(127\.|169\.254\.)'
        } | Select-Object -First 1
        if ($ipObj -and $ipObj.IPAddress) {
            $detectedIp = "$($ipObj.IPAddress)"
            if (Test-PreOobeZtdTcp -TargetHost 'ztd.dds.microsoft.com' -Port 443 -TimeoutMs 2500) {
                $ztdReachable = $true
                Write-PreOobeLog "Mreza aktivna (IP=$detectedIp) i ztd.dds.microsoft.com:443 dostupan za ${netTry}s." "SUCCESS"
                break
            }
        }
    } catch {}
    Start-Sleep -Seconds 1
}

try {
    $stateDir = 'C:\Windows\Logs\WIS'
    if (-not (Test-Path $stateDir)) { New-Item -Path $stateDir -ItemType Directory -Force -ErrorAction SilentlyContinue | Out-Null }
    $netStateJson = ([PSCustomObject]@{
        timestamp     = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
        ip_address    = $detectedIp
        online        = (-not [string]::IsNullOrWhiteSpace($detectedIp))
        ztd_reachable = [bool]$ztdReachable
    }) | ConvertTo-Json -Compress
    [System.IO.File]::WriteAllText((Join-Path $stateDir 'preoobe_network_state.json'), $netStateJson, $script:Utf8NoBom)
} catch {}

# 4. Pokretanje TPM/ZTD/wmansvc servisa i Autopilot taska pre ulaska u oobeSystem
try {
    foreach ($svcName in @('tbs', 'wlidsvc', 'dmwappushservice', 'wmansvc')) {
        try {
            & sc.exe config $svcName start= demand >$null 2>&1
            Start-Service -Name $svcName -ErrorAction SilentlyContinue
        } catch {}
    }
    Start-Process -FilePath "schtasks.exe" -ArgumentList '/run /tn "\Microsoft\Windows\Management\Provisioning\Autopilot"' -WindowStyle Hidden -ErrorAction SilentlyContinue
    Write-PreOobeLog "Pokrenuti servisi (tbs, wlidsvc, dmwappushservice, wmansvc) i Autopilot provisioning task u Pre-OOBE fazi." "INFO"
} catch {
    Write-PreOobeLog "Napomena pri pokretanju Pre-OOBE Autopilot servisa: $($_.Exception.Message)" "WARN"
}

# 5. IMP-114 Sloj C: Registracija pozadinskog SYSTEM OOBE ZTD Watchera (ONSTART -> SetupComplete.ps1 -OobeWatcherMode)
# Poziva iskljucivo staticku admin-only skriptu iz C:\Windows\Setup\Scripts (nikada C:\setup) sa deterministickim timeout osiguracem.
try {
    $watcherScript = 'C:\Windows\Setup\Scripts\SetupComplete.ps1'
    if (Test-Path -LiteralPath $watcherScript -PathType Leaf) {
        $taskCmd = "powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$watcherScript`" -OobeWatcherMode"
        $schOut = (& schtasks.exe /create /tn "WIS_OOBE_ZTD_Watcher" /ru SYSTEM /sc ONSTART /rl HIGHEST /tr $taskCmd /f 2>&1) -join ' '
        $schExit = $LASTEXITCODE
        $schLvl = if ($schExit -eq 0) { 'SUCCESS' } else { 'WARN' }
        Write-PreOobeLog "Registracija SYSTEM zadatka WIS_OOBE_ZTD_Watcher (ExitCode=$schExit): $schOut" $schLvl
    } else {
        Write-PreOobeLog "Admin-only $watcherScript nije pronadjen za registraciju WIS_OOBE_ZTD_Watcher zadatka." "WARN"
    }
} catch {
    Write-PreOobeLog "Napomena pri registraciji WIS_OOBE_ZTD_Watcher zadatka: $($_.Exception.Message)" "WARN"
}

exit 0
