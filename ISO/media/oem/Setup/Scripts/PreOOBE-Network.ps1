# PreOOBE-Network.ps1 - Ukljucivanje mreznih adaptera i servisnog Wi-Fi-ja pre ulaska u OOBE
# Omogucava Windows 11 OOBE-u da odmah kontaktira Microsoft i detektuje Autopilot
# Paket A1 (Nalaz 17 & U1): Bezbedan XML escaping, UTF-8 bez BOM-a, provera $LASTEXITCODE i trenutno brisanje u finally bloku

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
    $ts = (Get-Date).ToString("yyyy-MM-dd HH:mm:ss.fff")
    $line = "[$ts] [$Level] [PreOOBE-Network] $Message"
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

try {
    Get-NetAdapter -ErrorAction SilentlyContinue | Enable-NetAdapter -Confirm:$false -ErrorAction SilentlyContinue
} catch {}

try {
    & sc.exe config wlansvc start= auto >$null 2>&1
    & net.exe start wlansvc >$null 2>&1
} catch {}

$candidates = @("C:\setup\Wifi-Servis.ini")
foreach ($d in (Get-PSDrive -PSProvider FileSystem -ErrorAction SilentlyContinue)) {
    $candidates += (Join-Path $d.Root "Wifi-Servis.ini")
    $candidates += (Join-Path $d.Root "Skripte\Wifi-Servis.ini")
}

$wifiIni = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if ($wifiIni) {
    try {
        $content = Get-Content -Path $wifiIni -Raw -ErrorAction SilentlyContinue
        $ssidRaw = if ($content -match '(?m)^\s*SSID\s*=\s*(.+)$') { $matches[1].Trim() } else { '' }
        $pass    = if ($content -match '(?m)^\s*Password\s*=\s*(.+)$') { $matches[1].Trim() } else { '' }
        $enabled = if ($content -match '(?m)^\s*Enabled\s*=\s*1') { $true } else { $false }

        if ($enabled -and $ssidRaw -and $pass) {
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
                    Start-Sleep -Seconds 2
                } else {
                    Write-PreOobeLog "Greska pri uvozu WLAN profila za '$s' (ExitCode=$addExit): $addMsg" "WARN"
                }
            }
        }
    } catch {
        Write-PreOobeLog "Izuzetak u PreOOBE-Network.ps1: $($_.Exception.Message)" "WARN"
    }
}
