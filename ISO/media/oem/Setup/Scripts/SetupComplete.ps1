<#
.SCRIPT
    SetupComplete.ps1
.DESCRIPTION
    WIS SetupComplete.ps1 - Zavrsna faza Windows Setup-a (NT AUTHORITY\SYSTEM).
    Paket A1 (Nalaz 1, Nalaz 17, U1):
    - Izdvojena PowerShell 5.1 logika iz SetupComplete.cmd radi izbegavanja CMD limita od 8191 znaka.
    - Bezbedno generisanje WLAN XML profila sa [System.Security.SecurityElement]::Escape() bez sirove interpolacije.
    - Trenutno brisanje privremenog plaintext XML profila u finally bloku nakon netsh wlan add profile.
    - Jedinstvena i idempotentna ekstrakcija Programi.tar uz proveru $LASTEXITCODE i validaciju sadrzaja.
    - Strogo razlikovanje obaveznog neuspeha (exit 1 bez zavrsnog SUCCESS) i opcionog fallback-a.
.COMPATIBILITY
    Windows PowerShell 5.1+
#>

[CmdletBinding()]
param(
    [string]$SetupDir = "C:\setup",
    [string]$LogDir = "C:\Windows\Logs\WIS",
    [switch]$ForceFail
)

$ErrorActionPreference = 'Continue'
$script:Utf8NoBom = New-Object System.Text.UTF8Encoding($false)
$script:CriticalFailure = $false
$script:CriticalErrors = New-Object System.Collections.Generic.List[string]

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
    $sanitized = $Msg
    if ($sanitized -match '(?i)(Password\s*=\s*)([^\s;,\r\n]+)') {
        $sanitized = $sanitized -replace '(?i)(Password\s*=\s*)([^\s;,\r\n]+)', '$1***MASKIRANO***'
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

Write-Log 'Sprecavanje automatskog otvaranja Start Menija i animacija pri prvom logovanju...' 'STEP' 'Registry'
& reg.exe add 'HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System' /v 'EnableFirstLogonAnimation' /t REG_DWORD /d 0 /f >$null 2>&1
$animExit = $LASTEXITCODE
& reg.exe load 'HKU\DefaultUser' 'C:\Users\Default\NTUSER.DAT' >$null 2>&1
& reg.exe add 'HKU\DefaultUser\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced' /v 'StartShownOnUpgrade' /t REG_DWORD /d 1 /f >$null 2>&1
& reg.exe unload 'HKU\DefaultUser' >$null 2>&1
if ($animExit -eq 0) {
    Write-Log 'Polise StartShownOnUpgrade=1 (DefaultUser) i EnableFirstLogonAnimation=0 primenjene.' 'SUCCESS' 'Registry'
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
        $iniContent = Get-Content -Path $localWifiIni -Raw -ErrorAction SilentlyContinue
        $wifiEnabled = if ($iniContent -match '(?m)^\s*Enabled\s*=\s*1') { $true } else { $false }
        $wifiSsidRaw = if ($iniContent -match '(?m)^\s*SSID\s*=\s*(.+)$') { $matches[1].Trim() } else { '' }
        $wifiPass = if ($iniContent -match '(?m)^\s*Password\s*=\s*(.+)$') { $matches[1].Trim() } else { '' }

        if ($wifiEnabled -and $wifiSsidRaw -and $wifiPass) {
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
    Write-Log ('{0} vec postoji u lokalnom kesu.' -f $localWifiIni) 'INFO' 'WiFiAutoConnect'
}

# 6b. Pre-OOBE Autopilot ZTD Provera i Snimanje Trajnog Izvestaja (dok OOBEComplete nije zakljucan)
Write-Log 'Pokretanje Pre-OOBE Autopilot ZTD provere i cuvanje trajnog izvestaja...' 'STEP' 'AutopilotPreOOBE'
try {
    foreach ($apSvc in @('tbs', 'wlidsvc', 'dmwappushservice')) {
        try {
            Start-Service -Name $apSvc -ErrorAction SilentlyContinue
        } catch {}
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

    $preOobeOnline = $false
    for ($netSec = 1; $netSec -le 12; $netSec++) {
        $validIpObj = $null
        try {
            $validIpObj = Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue | Where-Object {
                $_.InterfaceAlias -notmatch 'Loopback|vEthernet' -and
                $_.IPAddress -notmatch '^(127\.|169\.254\.)'
            } | Select-Object -First 1
        } catch {}

        if ($validIpObj -and $validIpObj.IPAddress) {
            if ((Test-PreOobeTcpEndpoint -TargetHost 'ztd.dds.microsoft.com' -Port 443 -TimeoutMs 2000) -or
                (Test-PreOobeTcpEndpoint -TargetHost 'login.live.com' -Port 443 -TimeoutMs 2000)) {
                $preOobeOnline = $true
                Write-Log ('Mrezna veza ka Microsoft ZTD oblaku potvrdena u Pre-OOBE fazi (IP={0}, cekanje={1}s).' -f $validIpObj.IPAddress, $netSec) 'SUCCESS' 'AutopilotPreOOBE'
                break
            }
        }
        Start-Sleep -Seconds 1
    }

    if (-not $preOobeOnline) {
        Write-Log 'Microsoft ZTD oblak nije dostizan u roku od 12s u Pre-OOBE fazi (verovatno nedostaje Wi-Fi drajver ili internet veza).' 'WARN' 'AutopilotPreOOBE'
    }

    $oobeRegPath = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\OOBE'
    $hadSkipNetworkSetup = $false
    $origSkipNetworkSetup = 1
    $hadDisableOobeConn = $false
    $origDisableOobeConn = $null
    try {
        if (Test-Path $oobeRegPath) {
            $oobeProps = Get-ItemProperty -Path $oobeRegPath -ErrorAction SilentlyContinue
            if ($oobeProps -and ($null -ne $oobeProps.SkipNetworkSetup)) {
                $hadSkipNetworkSetup = $true
                $origSkipNetworkSetup = $oobeProps.SkipNetworkSetup
                Remove-ItemProperty -Path $oobeRegPath -Name 'SkipNetworkSetup' -Force -ErrorAction SilentlyContinue
            }
            if ($oobeProps -and ($null -ne $oobeProps.DisableOOBEConnection)) {
                $hadDisableOobeConn = $true
                $origDisableOobeConn = $oobeProps.DisableOOBEConnection
                Remove-ItemProperty -Path $oobeRegPath -Name 'DisableOOBEConnection' -Force -ErrorAction SilentlyContinue
            }
        }

        try {
            Start-Process -FilePath 'schtasks.exe' -ArgumentList '/run /tn "\Microsoft\Windows\Management\Provisioning\Autopilot"' -WindowStyle Hidden -ErrorAction SilentlyContinue
        } catch {}

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
                Write-Log ('Pokrecem Pre-OOBE Autopilot auditor: {0} --json' -f $apAuditorExe) 'INFO' 'AutopilotPreOOBE'
                $apProc = Start-Process -FilePath $apAuditorExe -ArgumentList '--json' -RedirectStandardOutput $tmpApOut -RedirectStandardError $tmpApErr -PassThru -WindowStyle Hidden
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
            $newConfirmed = $apMatrix -in @('LOCKED_TENANT', 'HIDDEN_LOCK', 'RELEASED_LOCAL_TRACES', 'CONFIRMED_CLEAN')
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
                    if ((-not $newConfirmed) -and (Test-Path $rt)) {
                        $exRaw = Get-Content -Path $rt -Raw -ErrorAction SilentlyContinue
                        if ($exRaw) {
                            $exObj = $exRaw | ConvertFrom-Json -ErrorAction SilentlyContinue
                            $exMc = if ($exObj -and $exObj.matrix_code) { "$($exObj.matrix_code)".ToUpper().Trim() } else { '' }
                            if ($exMc -in @('LOCKED_TENANT', 'HIDDEN_LOCK', 'RELEASED_LOCAL_TRACES', 'CONFIRMED_CLEAN')) { continue }
                        }
                    }
                    [System.IO.File]::WriteAllText($rt, $apJsonPayload, $script:Utf8NoBom)
                } catch {}
            }
            Write-Log ('Pre-OOBE Autopilot izvestaj sacuvan (matrix_code={0}, tenant={1}) u {2}\autopilot_report.json i {3}\autopilot_report.json' -f $apMatrix, $apTenantLog, $LogDir, $SetupDir) 'SUCCESS' 'AutopilotPreOOBE'
        } else {
            Write-Log 'Pre-OOBE Autopilot auditor nije vratio validan JSON izvestaj.' 'WARN' 'AutopilotPreOOBE'
        }

        $ztdStateFile = Join-Path $env:SystemRoot 'ServiceState\Autopilot\AutopilotDDSZTDFile.json'
        if (Test-Path $ztdStateFile) {
            try {
                if (-not (Test-Path $LogDir)) {
                    New-Item -Path $LogDir -ItemType Directory -Force -ErrorAction SilentlyContinue | Out-Null
                }
                $preOobeZtdCopy = Join-Path $LogDir 'AutopilotDDSZTDFile.preoobe.json'
                Copy-Item -Path $ztdStateFile -Destination $preOobeZtdCopy -Force -ErrorAction Stop
                Write-Log ('Rezervna kopija AutopilotDDSZTDFile.json sacuvana u: {0}' -f $preOobeZtdCopy) 'SUCCESS' 'AutopilotPreOOBE'
            } catch {
                Write-Log ('Neuspesno kopiranje AutopilotDDSZTDFile.json: {0}' -f $_.Exception.Message) 'WARN' 'AutopilotPreOOBE'
            }
        }
    } finally {
        try {
            if (-not (Test-Path $oobeRegPath)) {
                New-Item -Path $oobeRegPath -Force -ErrorAction SilentlyContinue | Out-Null
            }
            $restoreSkipVal = if ($hadSkipNetworkSetup) { $origSkipNetworkSetup } else { 1 }
            Set-ItemProperty -Path $oobeRegPath -Name 'SkipNetworkSetup' -Value $restoreSkipVal -Type DWord -Force -ErrorAction SilentlyContinue
            if ($hadDisableOobeConn -and ($null -ne $origDisableOobeConn)) {
                Set-ItemProperty -Path $oobeRegPath -Name 'DisableOOBEConnection' -Value $origDisableOobeConn -Type DWord -Force -ErrorAction SilentlyContinue
            }
        } catch {}
    }
} catch {
    Write-Log ('Izuzetak u Pre-OOBE Autopilot koraku: {0}' -f $_.Exception.Message) 'WARN' 'AutopilotPreOOBE'
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
    Write-Log 'Programi.tar nije pronadjen (Lean ISO profil - korak se tiho preskace za 0 sekundi).' 'INFO' 'TarDetection'
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
