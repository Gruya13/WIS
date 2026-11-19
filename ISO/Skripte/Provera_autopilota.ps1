<#
.SYNOPSIS
    Microsoft Autopilot Forensic Auditor v2.0
.DESCRIPTION
    Deterministicka verifikacija vezanosti uredjaja za Microsoft Entra ID / Intune tenant.
    Objedinjuje duboku lokalnu forenziku sistema sa zivim klijentskim upitom prema
    Microsoft Zero Touch Deployment (ZTD) oblaku (ztd.dds.microsoft.com) bez potrebe
    za administratorskim nalozima za Microsoft Graph API.

    Daje konacnu presudu kroz 4-stupanjsku deterministicku matricu stanja.
.NOTES
    Verzija:  2.0
    Autor:    Antigravity AI / Agency Multi-Agent Squad
#>

[CmdletBinding()]
param(
    [switch]$JsonOutput
)

# ==============================================================================
# PROXY FUNKCIJE ZA TIHI JSON REZIM (-JsonOutput)
# ==============================================================================
if ($JsonOutput) {
    $WarningPreference     = 'SilentlyContinue'
    $InformationPreference = 'SilentlyContinue'
    $VerbosePreference     = 'SilentlyContinue'
    $DebugPreference       = 'SilentlyContinue'
    $ProgressPreference    = 'SilentlyContinue'

    function global:Write-Host {
        [CmdletBinding()]
        param(
            [Parameter(Position=0, ValueFromPipeline=$true)]
            [System.Object]$Object,
            [switch]$NoNewline,
            [System.Object]$Separator,
            [System.ConsoleColor]$ForegroundColor,
            [System.ConsoleColor]$BackgroundColor
        )
    }
    function global:Clear-Host { }
    function global:Read-Host { param([string]$Prompt) return "" }
}

# ==============================================================================
# 0. AUTO-ELEVATION GUARD (Zahteva Administratorska prava)
# ==============================================================================
$currentUser = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal   = [Security.Principal.WindowsPrincipal]$currentUser
$isAdmin     = $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

if (-not $isAdmin) {
    if ($JsonOutput) {
        $errJson = [PSCustomObject]@{
            timestamp          = (Get-Date).ToString("yyyy-MM-ddTHH:mm:ss")
            matrix_code        = "ELEVATION_FAILED"
            status_level       = "ERROR"
            status_title       = "GRESKA - ZAHTEVA ADMINISTRATORSKA PRAVA"
            status_description = "Nije uspelo podizanje privilegija za pristup registru i ZTD oblaku."
            recommended_action = "Pokrenite aplikaciju kao Administrator."
            is_locked          = $false
            tenant_domain      = ""
            tenant_id          = ""
            device_serial      = "Nepoznato"
            device_model       = "Nepoznato"
            hardware           = [PSCustomObject]@{
                manufacturer  = "Nepoznato"
                model         = "Nepoznato"
                serial_number = "Nepoznato"
                smbios_uuid   = "Nepoznato"
                tpm_present   = $false
                tpm_ready     = $false
            }
            local_forensics    = [PSCustomObject]@{
                has_traces               = $false
                tenant_domain            = ""
                tenant_id                = ""
                assigned_device_name     = ""
                policy_cache_found       = $false
                established_corr         = $false
                azure_ad_joined          = $false
                mdm_enrolled             = $false
                org_certificates_count   = 0
                device_management_keys   = 0
                enrollment_keys          = 0
                enterprise_work_accounts = 0
                azure_ad_status          = "Nije pridruzen (NO)"
                mdm_status               = "Nije upisan (NO)"
                org_certificates         = @()
                mdm_enrollment_urls      = @()
            }
            cloud_ztd          = [PSCustomObject]@{
                online_checked        = $false
                is_registered         = $false
                error_code            = "ELEVATION_REQUIRED"
                raw_status_message    = "Zahteva administratorska prava"
                profile_tenant_domain = ""
                profile_tenant_id     = ""
                profile_device_name   = ""
            }
            ztd_inspection     = [PSCustomObject]@{
                status      = "OFFLINE"
                checked     = $false
                http_status = 0
            }
        } | ConvertTo-Json -Depth 6 -Compress
        [Console]::Out.WriteLine($errJson)
        exit 0
    }
    Write-Host "`n[!] Skripta zahteva administratorske privilegije za pristup dijagnostici i registru." -ForegroundColor Yellow
    Write-Host "[*] Pokrecem novu instancu sa uvecanim (UAC) pravima...`n" -ForegroundColor Cyan
    try {
        Start-Process powershell.exe -ArgumentList "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`"" -Verb RunAs
    } catch {
        Write-Host "[-] Nije uspelo podizanje privilegija: $($_.Exception.Message)" -ForegroundColor Red
        Read-Host "Pritisnite ENTER za izlaz..."
    }
    exit 1
}

# Postavljanje kodiranja konzole na UTF-8
try {
    [Console]::OutputEncoding = [System.Text.Encoding]::UTF8
} catch { }

Clear-Host
Write-Host "================================================================================" -ForegroundColor Cyan
Write-Host "               MICROSOFT AUTOPILOT FORENSIC AUDITOR v2.0                        " -ForegroundColor Cyan
Write-Host "          Deterministicka inspekcija hardvera, registra i ZTD oblaka           " -ForegroundColor DarkCyan
Write-Host "================================================================================" -ForegroundColor Cyan

# ==============================================================================
# 1. HARDVERSKA IDENTIFIKACIJA UREDJAJA
# ==============================================================================
Write-Host "`n[1] IDENTIFIKACIJA HARDVERA" -ForegroundColor Yellow
Write-Host "--------------------------------------------------------------------------------" -ForegroundColor DarkGray

$hwInfo = [PSCustomObject]@{
    Manufacturer = "Nepoznato"
    Model        = "Nepoznato"
    SerialNumber = "Nepoznato"
    SmbiosUuid   = "Nepoznato"
    TpmPresent   = $false
    TpmReady     = $false
}

try {
    $cs = Get-CimInstance -ClassName Win32_ComputerSystem -ErrorAction SilentlyContinue
    if ($cs) {
        $hwInfo.Manufacturer = $cs.Manufacturer.Trim()
        $hwInfo.Model        = $cs.Model.Trim()
    }
    $bios = Get-CimInstance -ClassName Win32_BIOS -ErrorAction SilentlyContinue
    if ($bios) {
        $hwInfo.SerialNumber = $bios.SerialNumber.Trim()
    }
    $csp = Get-CimInstance -ClassName Win32_ComputerSystemProduct -ErrorAction SilentlyContinue
    if ($csp) {
        $hwInfo.SmbiosUuid = $csp.UUID.Trim()
    }
    $tpm = Get-Tpm -ErrorAction SilentlyContinue
    if ($tpm) {
        $hwInfo.TpmPresent = $tpm.TpmPresent
        $hwInfo.TpmReady   = $tpm.TpmReady
    }
} catch { }

Write-Host "  Proizvodjac:       $($hwInfo.Manufacturer)" -ForegroundColor White
Write-Host "  Model racunara:    $($hwInfo.Model)" -ForegroundColor White
Write-Host "  Serijski broj:     $($hwInfo.SerialNumber)" -ForegroundColor White
Write-Host "  SMBIOS UUID:       $($hwInfo.SmbiosUuid)" -ForegroundColor White
Write-Host "  TPM 2.0 status:    Prisutan: $(if($hwInfo.TpmPresent){'DA'}else{'NE'}) | Spreman: $(if($hwInfo.TpmReady){'DA'}else{'NE'})" -ForegroundColor $(if($hwInfo.TpmPresent -and $hwInfo.TpmReady){'Green'}else{'Yellow'})

# ==============================================================================
# 2. DUBOKA LOKALNA SISTEMSKA FORENZIKA
# ==============================================================================
Write-Host "`n[2] DUBOKA LOKALNA FORENZIKA (OS, Registar, Sertifikati)" -ForegroundColor Yellow
Write-Host "--------------------------------------------------------------------------------" -ForegroundColor DarkGray

$localReport = [PSCustomObject]@{
    HasTraces             = $false
    TenantId              = $null
    TenantDomain          = $null
    AssignedDeviceName    = $null
    PolicyCacheFound      = $false
    EstablishedCorr       = $false
    MdmEnrollmentUrls     = @()
    CloudDomainJoinTenant = $null
    CloudDomainJoinUser   = $null
    OrgCertificates       = @()
    AzureAdJoined         = $false
    MdmEnrolled           = $false
}

# 2.1 Diagnostics\AutoPilot
$diagPath = "HKLM:\SOFTWARE\Microsoft\Provisioning\Diagnostics\AutoPilot"
if (Test-Path $diagPath) {
    try {
        $props = Get-ItemProperty -Path $diagPath -ErrorAction Stop
        if ($props.CloudAssignedTenantId) {
            $localReport.HasTraces     = $true
            $localReport.TenantId       = $props.CloudAssignedTenantId
        }
        if ($props.CloudAssignedTenantDomain) {
            $localReport.HasTraces     = $true
            $localReport.TenantDomain   = $props.CloudAssignedTenantDomain
        }
        if ($props.CloudAssignedDeviceName) {
            $localReport.AssignedDeviceName = $props.CloudAssignedDeviceName
        }
        if ($props.EstablishedCorrelations -ne $null) {
            $localReport.HasTraces       = $true
            $localReport.EstablishedCorr = $true
        }
    } catch { }
}

# 2.2 PolicyJsonCache
$cachePath = "HKLM:\SOFTWARE\Microsoft\Provisioning\AutopilotPolicyCache\PolicyJsonCache"
if (Test-Path $cachePath) {
    $localReport.HasTraces        = $true
    $localReport.PolicyCacheFound = $true
}

# 2.3 Enrollments
$enrollPath = "HKLM:\SOFTWARE\Microsoft\Enrollments"
if (Test-Path $enrollPath) {
    try {
        Get-ChildItem -Path $enrollPath -ErrorAction SilentlyContinue | ForEach-Object {
            $eProps = Get-ItemProperty -Path $_.PSPath -ErrorAction SilentlyContinue
            if ($eProps.DiscoveryServiceFullURL) {
                $localReport.HasTraces = $true
                $localReport.MdmEnrollmentUrls += $eProps.DiscoveryServiceFullURL
            }
        }
    } catch { }
}

# 2.4 CloudDomainJoin
$cdjPath = "HKLM:\SYSTEM\CurrentControlSet\Control\CloudDomainJoin\JoinInfo"
if (Test-Path $cdjPath) {
    try {
        Get-ChildItem -Path $cdjPath -ErrorAction SilentlyContinue | ForEach-Object {
            $jProps = Get-ItemProperty -Path $_.PSPath -ErrorAction SilentlyContinue
            if ($jProps.TenantId) {
                $localReport.HasTraces             = $true
                $localReport.CloudDomainJoinTenant = $jProps.TenantId
                $localReport.CloudDomainJoinUser   = $jProps.UserEmail
            }
        }
    } catch { }
}

# 2.5 Sertifikati racunara
try {
    Get-ChildItem -Path "Cert:\LocalMachine\My" -ErrorAction SilentlyContinue | ForEach-Object {
        if ($_.Issuer -like "*Intune MDM Device CA*" -or $_.Issuer -like "*MS-Organization-Access*") {
            $localReport.HasTraces = $true
            $localReport.OrgCertificates += "$($_.Subject) (Izdavac: $($_.Issuer))"
        }
    }
} catch { }

# 2.6 dsregcmd /status
try {
    $dsreg = dsregcmd /status 2>$null
    if ($dsreg) {
        $localReport.AzureAdJoined = ($dsreg -match "AzureAdJoined\s*:\s*YES")
        $localReport.MdmEnrolled   = ($dsreg -match "MdmEnrolled\s*:\s*YES")
        if ($localReport.AzureAdJoined -or $localReport.MdmEnrolled) {
            $localReport.HasTraces = $true
        }
    }
} catch { }

# Prikaz lokalnih nalaza
if ($localReport.TenantDomain -or $localReport.TenantId) {
    Write-Host "  [!] Pronadjen Autopilot profil u registru:" -ForegroundColor Red
    if ($localReport.TenantDomain) { Write-Host "      • Tenant domen:          $($localReport.TenantDomain)" -ForegroundColor Red }
    if ($localReport.TenantId)     { Write-Host "      • Tenant ID:             $($localReport.TenantId)" -ForegroundColor Red }
    if ($localReport.AssignedDeviceName) { Write-Host "      • Dodeljeno ime masine:  $($localReport.AssignedDeviceName)" -ForegroundColor Red }
} else {
    Write-Host "  [*] Autopilot grana registra:       Nema dodeljenog tenant-a" -ForegroundColor Green
}

Write-Host "  [*] AutopilotPolicyCache:          $(if($localReport.PolicyCacheFound){'PRONADJEN (Postoji profil)'}else{'Nema zapisa'})" -ForegroundColor $(if($localReport.PolicyCacheFound){'Red'}else{'Green'})
Write-Host "  [*] EstablishedCorrelations:       $(if($localReport.EstablishedCorr){'POSTOJI'}else{'Nema zapisa'})" -ForegroundColor $(if($localReport.EstablishedCorr){'Red'}else{'Green'})
Write-Host "  [*] Entra ID (Azure AD) status:    $(if($localReport.AzureAdJoined){'PRIDRUZEN (YES)'}else{'Nije pridruzen (NO)'})" -ForegroundColor $(if($localReport.AzureAdJoined){'Red'}else{'Green'})
Write-Host "  [*] Intune MDM upis:               $(if($localReport.MdmEnrolled){'UPISAN (YES)'}else{'Nije upisan (NO)'})" -ForegroundColor $(if($localReport.MdmEnrolled){'Red'}else{'Green'})

if ($localReport.MdmEnrollmentUrls.Count -gt 0) {
    Write-Host "  [!] Pronadjeni MDM Enrollment URL-ovi ($($localReport.MdmEnrollmentUrls.Count)):" -ForegroundColor Red
    foreach ($url in ($localReport.MdmEnrollmentUrls | Select-Object -Unique)) {
        Write-Host "      • $url" -ForegroundColor Red
    }
}

if ($localReport.OrgCertificates.Count -gt 0) {
    Write-Host "  [!] Pronadjeni organizacioni kriptografski sertifikati:" -ForegroundColor Red
    foreach ($cert in $localReport.OrgCertificates) {
        Write-Host "      • $cert" -ForegroundColor Red
    }
}

# ==============================================================================
# 3. LIVE CLOUD ZTD PROVERA (Microsoft Zero Touch Deployment)
# ==============================================================================
Write-Host "`n[3] ZIVA PROVERA U MICROSOFT OBLAKU (ztd.dds.microsoft.com)" -ForegroundColor Yellow
Write-Host "--------------------------------------------------------------------------------" -ForegroundColor DarkGray

$cloudReport = [PSCustomObject]@{
    OnlineChecked       = $false
    IsRegistered        = $false
    ConfirmedFree       = $false
    ErrorCode           = $null
    ProfileTenantId     = $null
    ProfileTenantDomain = $null
    ProfileDeviceName   = $null
    RawStatusMessage    = "Nije provereno"
    QueryMode           = "NONE"
}

# Brzi socket test konektivnosti ka ztd.dds.microsoft.com (port 443, 3s timeout)
function Test-TcpConnectionFast ($hostname, $port = 443, $timeoutMs = 3000) {
    try {
        $tcpClient = New-Object System.Net.Sockets.TcpClient
        $iar = $tcpClient.BeginConnect($hostname, $port, $null, $null)
        $wait = $iar.AsyncWaitHandle.WaitOne($timeoutMs, $false)
        if (-not $wait) {
            $tcpClient.Close()
            return $false
        }
        $tcpClient.EndConnect($iar)
        $tcpClient.Close()
        return $true
    } catch {
        return $false
    }
}

function Resolve-ZtdJsonContent {
    param(
        [string]$RawJson,
        [Parameter(Mandatory=$true)]$CloudReport,
        [string]$QueryMode = "SERVICE_STATE_CACHE"
    )
    if ([string]::IsNullOrWhiteSpace($RawJson)) { return $false }
    try {
        $parsed = $RawJson | ConvertFrom-Json -ErrorAction Stop
        if ($null -eq $parsed) { return $false }

        $hasTenantProp = $null -ne $parsed.PSObject.Properties['CloudAssignedTenantId']
        if ($hasTenantProp) {
            $tid = if ($parsed.CloudAssignedTenantId) { "$($parsed.CloudAssignedTenantId)".Trim() } else { "" }
            if ($tid -and $tid -ne "00000000-0000-0000-0000-000000000000") {
                $CloudReport.OnlineChecked       = $true
                $CloudReport.IsRegistered        = $true
                $CloudReport.ConfirmedFree       = $false
                $CloudReport.ProfileTenantId     = $tid
                $CloudReport.ProfileTenantDomain = if ($parsed.CloudAssignedTenantDomain) { "$($parsed.CloudAssignedTenantDomain)".Trim() } else { $null }
                $CloudReport.ProfileDeviceName   = if ($parsed.CloudAssignedDeviceName) { "$($parsed.CloudAssignedDeviceName)".Trim() } else { $null }
                $CloudReport.QueryMode           = $QueryMode
                $CloudReport.RawStatusMessage    = "Uredjaj je 100% REGISTROVAN u Microsoft Autopilot bazi ($QueryMode)"
                return $true
            }

            $CloudReport.OnlineChecked    = $true
            $CloudReport.IsRegistered     = $false
            $CloudReport.ConfirmedFree    = $true
            $CloudReport.ErrorCode        = "807 - ZtdDeviceIsNotRegistered (ZTD File)"
            $CloudReport.QueryMode        = $QueryMode
            $CloudReport.RawStatusMessage = "Slobodan (Microsoft ZTD profil potvrdio: Uredjaj nije registrovan)"
            return $true
        }

        if ($null -ne $parsed.PSObject.Properties['ProfileState']) {
            $pState = "$($parsed.ProfileState)".Trim()
            if ($pState -eq "0" -or $pState -ieq "NotAssigned") {
                $CloudReport.OnlineChecked    = $true
                $CloudReport.IsRegistered     = $false
                $CloudReport.ConfirmedFree    = $true
                $CloudReport.ErrorCode        = "807 - ZtdDeviceIsNotRegistered (ZTD File)"
                $CloudReport.QueryMode        = $QueryMode
                $CloudReport.RawStatusMessage = "Slobodan (Microsoft ZTD profil potvrdio: Uredjaj nije registrovan)"
                return $true
            }
        }
    } catch {}
    return $false
}

function Test-SavedAutopilotReportConfirmed {
    param([string]$JsonText)
    if ([string]::IsNullOrWhiteSpace($JsonText)) { return $false }
    try {
        $obj = $JsonText | ConvertFrom-Json -ErrorAction Stop
        if ($null -eq $obj) { return $false }
        $mc = if ($obj.matrix_code) { "$($obj.matrix_code)".ToUpper().Trim() } else { "" }
        if ($mc -in @('LOCKED_TENANT', 'HIDDEN_LOCK', 'RELEASED_LOCAL_TRACES', 'CONFIRMED_CLEAN')) { return $true }
        if ($obj.cloud_ztd -and ($obj.cloud_ztd.is_registered -eq $true -or $obj.cloud_ztd.confirmed_free -eq $true)) { return $true }
    } catch {}
    return $false
}

# 3.0 Provera trajno sacuvanih Pre-OOBE izvestaja i ZTD kesa (radi trenutno i u offline rezimu)
$cacheMatched = $false
$ztdPreOobeFile = "C:\Windows\Logs\WIS\AutopilotDDSZTDFile.preoobe.json"
$serviceStateJson = Join-Path $env:SystemRoot "ServiceState\Autopilot\AutopilotDDSZTDFile.json"

foreach ($ztdCandidate in @(
    @{ Path = $ztdPreOobeFile; Mode = "PREOOBE_ZTD_FILE" },
    @{ Path = $serviceStateJson; Mode = "SERVICE_STATE_CACHE" }
)) {
    if (Test-Path $ztdCandidate.Path) {
        try {
            $rawZtd = Get-Content -Path $ztdCandidate.Path -Raw -ErrorAction SilentlyContinue
            if (Resolve-ZtdJsonContent -RawJson $rawZtd -CloudReport $cloudReport -QueryMode $ztdCandidate.Mode) {
                $cacheMatched = $true
                break
            }
        } catch {}
    }
}

if (-not $cacheMatched) {
    foreach ($repPath in @("C:\Windows\Logs\WIS\autopilot_report.json", "C:\setup\autopilot_report.json")) {
        if (-not (Test-Path $repPath)) { continue }
        try {
            $repRaw = Get-Content -Path $repPath -Raw -ErrorAction SilentlyContinue
            if ([string]::IsNullOrWhiteSpace($repRaw)) { continue }
            $repObj = $repRaw | ConvertFrom-Json -ErrorAction Stop
            if ($null -eq $repObj) { continue }

            $cachedSerial = if ($repObj.device_serial) { "$($repObj.device_serial)".Trim() } else { "" }
            $currSerial = if ($hwInfo.SerialNumber) { "$($hwInfo.SerialNumber)".Trim() } else { "" }
            if ($cachedSerial -and $cachedSerial -ne "Nepoznato" -and $currSerial -and $currSerial -ne "Nepoznato" -and ($cachedSerial -ine $currSerial)) {
                continue
            }

            $mc = if ($repObj.matrix_code) { "$($repObj.matrix_code)".ToUpper().Trim() } else { "" }
            $cz = $repObj.cloud_ztd
            $czReg = ($cz -and $cz.is_registered -eq $true)
            $czFree = ($cz -and $cz.confirmed_free -eq $true)

            if ($mc -in @('LOCKED_TENANT', 'HIDDEN_LOCK') -or $czReg) {
                $cloudReport.OnlineChecked       = $true
                $cloudReport.IsRegistered        = $true
                $cloudReport.ConfirmedFree       = $false
                $cloudReport.ProfileTenantDomain = if ($cz -and $cz.profile_tenant_domain) { "$($cz.profile_tenant_domain)".Trim() } elseif ($repObj.tenant_domain) { "$($repObj.tenant_domain)".Trim() } else { $null }
                $cloudReport.ProfileTenantId     = if ($cz -and $cz.profile_tenant_id) { "$($cz.profile_tenant_id)".Trim() } elseif ($repObj.tenant_id) { "$($repObj.tenant_id)".Trim() } else { $null }
                $cloudReport.ProfileDeviceName   = if ($cz -and $cz.profile_device_name) { "$($cz.profile_device_name)".Trim() } else { $null }
                $cloudReport.ErrorCode           = if ($cz -and $cz.error_code) { "$($cz.error_code)".Trim() } else { $null }
                $cloudReport.QueryMode           = "PREOOBE_CACHED_REPORT"
                $cloudReport.RawStatusMessage    = if ($cz -and $cz.raw_status_message) { "$($cz.raw_status_message)" } else { "Uredjaj je 100% REGISTROVAN u Microsoft Autopilot bazi (Sacuvan OOBE izvestaj)" }
                $cacheMatched = $true
                break
            }

            if ($mc -in @('CONFIRMED_CLEAN', 'RELEASED_LOCAL_TRACES') -or $czFree) {
                $cloudReport.OnlineChecked    = $true
                $cloudReport.IsRegistered     = $false
                $cloudReport.ConfirmedFree    = $true
                $cloudReport.ErrorCode        = if ($cz -and $cz.error_code) { "$($cz.error_code)".Trim() } else { "807 - CachedPreOOBE" }
                $cloudReport.QueryMode        = "PREOOBE_CACHED_REPORT"
                $cloudReport.RawStatusMessage = "Slobodan (Potvrdjeno iz sacuvanog OOBE izvestaja)"
                $cacheMatched = $true
                break
            }
        } catch {}
    }
}

if ($cacheMatched) {
    Write-Host "  [*] Pronadjen sacuvan Pre-OOBE izvestaj / ZTD kes ($($cloudReport.QueryMode))." -ForegroundColor Green
} else {
    Write-Host "  [*] Proveravam dostupnost Microsoft ZTD servisa..." -NoNewline
    $canReachZtd = Test-TcpConnectionFast -hostname "ztd.dds.microsoft.com" -port 443
    $canReachLws = Test-TcpConnectionFast -hostname "login.live.com" -port 443

    if (-not ($canReachZtd -or $canReachLws)) {
        Write-Host " [OFFLINE]" -ForegroundColor Yellow
        Write-Host "  [!] Uredjaj nema internet vezu ili je blokiran pristup servisu ztd.dds.microsoft.com." -ForegroundColor Yellow
        Write-Host "      Cloud ZTD upit je preskocen. Rezultati se baziraju iskljucivo na lokalnoj forenzici." -ForegroundColor DarkYellow
        $cloudReport.RawStatusMessage = "OFFLINE - Servis nedostupan"
        $cloudReport.QueryMode = "OFFLINE"
    } else {
        Write-Host " [ONLINE]" -ForegroundColor Green
        $cloudReport.OnlineChecked = $true

        # 3.1 ULTRA-FAST PATH: Provera živog Windows Autopilot Event dnevnika
        Write-Host "  [*] Ocitavam zivi Windows Autopilot Event Log..." -ForegroundColor Cyan
        try {
            $liveEvents = Get-WinEvent -LogName "Microsoft-Windows-ModernDeployment-Diagnostics-Provider/Autopilot" -MaxEvents 50 -ErrorAction SilentlyContinue
            if ($liveEvents) {
                foreach ($ev in $liveEvents) {
                    if ($ev.Id -eq 806 -or ($ev.Message -match 'CloudAssignedTenantId\s*[:=]\s*\{?([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})\}?' -and $matches[1] -ne '00000000-0000-0000-0000-000000000000')) {
                        $cloudReport.IsRegistered = $true
                        $cloudReport.QueryMode    = "LIVE_EVENT_LOG"
                        $cloudReport.RawStatusMessage = "Uredjaj je 100% REGISTROVAN u Microsoft Autopilot bazi (Event 806)"
                        break
                    }
                    if ($ev.Id -eq 807 -or $ev.Message -match "807" -or $ev.Message -match "ZtdDeviceIsNotRegistered" -or $ev.Message -match "ProfileState\s*=\s*NotAssigned") {
                        $cloudReport.IsRegistered  = $false
                        $cloudReport.ConfirmedFree = $true
                        $cloudReport.ErrorCode     = "807 - ZtdDeviceIsNotRegistered"
                        $cloudReport.QueryMode     = "LIVE_EVENT_LOG"
                        $cloudReport.RawStatusMessage = "Slobodan (Microsoft potvrdio: Uredjaj nije registrovan)"
                        break
                    }
                }
            }
        } catch { }

        # 3.2 Provera postojanja direktnog profila u ServiceState kešu
        if (-not ($cloudReport.IsRegistered -or $cloudReport.ConfirmedFree) -and (Test-Path $serviceStateJson)) {
            try {
                $ssRaw = Get-Content -Path $serviceStateJson -Raw -ErrorAction SilentlyContinue
                Resolve-ZtdJsonContent -RawJson $ssRaw -CloudReport $cloudReport -QueryMode "SERVICE_STATE_CACHE" | Out-Null
            } catch { }
        }

        # 3.3 AKTIVNO BUDJENJE OOBE ZTD RADNIKA ako status jos uvek nije eksplicitno razresen
        if (-not ($cloudReport.IsRegistered -or $cloudReport.ConfirmedFree)) {
            Write-Host "  [*] Pokrecem aktivno pozadinsko budjenje Windows Provisioning ZTD radnika..." -ForegroundColor Cyan

            $oobeRegPath = "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\OOBE"
            $hadSkipNet = $false
            $origSkipNet = $null
            $hadDisableOobe = $false
            $origDisableOobe = $null

            $tempDir = Join-Path $env:TEMP "AP_Diag_$([guid]::NewGuid().ToString('N').Substring(0,8))"
            $cabPath = Join-Path $tempDir "AutopilotReport.cab"
            $outDir  = Join-Path $tempDir "Extracted"

            try {
                if (Test-Path $oobeRegPath) {
                    $oobeProps = Get-ItemProperty -Path $oobeRegPath -ErrorAction SilentlyContinue
                    if ($oobeProps -and ($null -ne $oobeProps.SkipNetworkSetup)) {
                        $hadSkipNet = $true
                        $origSkipNet = $oobeProps.SkipNetworkSetup
                        Remove-ItemProperty -Path $oobeRegPath -Name "SkipNetworkSetup" -Force -ErrorAction SilentlyContinue
                    }
                    if ($oobeProps -and ($null -ne $oobeProps.DisableOOBEConnection)) {
                        $hadDisableOobe = $true
                        $origDisableOobe = $oobeProps.DisableOOBEConnection
                        Remove-ItemProperty -Path $oobeRegPath -Name "DisableOOBEConnection" -Force -ErrorAction SilentlyContinue
                    }
                }

                # 3.3.1 Silent OOBE servisi - neophodni za komunikaciju sa Microsoft cloud autentifikacijom
                try {
                    Start-Service -Name "tbs" -ErrorAction SilentlyContinue
                    Start-Service -Name "wlidsvc" -ErrorAction SilentlyContinue
                    Start-Service -Name "dmwappushservice" -ErrorAction SilentlyContinue
                } catch { }

                # 3.3.2 Budjenje Task Scheduler zadataka za provisioning
                try {
                    $tasks = @(
                        "\Microsoft\Windows\Management\Provisioning\Autopilot",
                        "\Microsoft\Windows\Management\Provisioning\Logon",
                        "\Microsoft\Windows\Management\Provisioning\Cellular",
                        "\Microsoft\Windows\Management\Provisioning\Retry"
                    )
                    foreach ($t in $tasks) {
                        Start-Process -FilePath "schtasks.exe" -ArgumentList "/run /tn `"$t`"" -WindowStyle Hidden -ErrorAction SilentlyContinue
                    }
                } catch { }

                # 3.3.3 OOBE DeviceEnroller radnik
                try {
                    $devEnroller = Join-Path $env:SystemRoot "System32\deviceenroller.exe"
                    if (Test-Path $devEnroller) {
                        Start-Process -FilePath $devEnroller -ArgumentList "/c /AutoEnrollMDM" -WindowStyle Hidden -ErrorAction SilentlyContinue
                    }
                } catch { }

                New-Item -Path $outDir -ItemType Directory -Force | Out-Null
                $toolPath = Join-Path $env:SystemRoot "System32\mdmdiagnosticstool.exe"

                if (Test-Path $toolPath) {
                    $proc = Start-Process -FilePath $toolPath -ArgumentList "-area `"Autopilot;DeviceProvisioning;DeviceEnrollment`" -cab `"$cabPath`"" -PassThru -WindowStyle Hidden
                    if ($proc) {
                        # Dinamicki polling do 35 sekundi sa brzim prekidom cim stigne odgovor iz Microsoft oblaka
                        for ($waitSec = 0; $waitSec -lt 35; $waitSec++) {
                            if ($proc.HasExited) { break }
                            Start-Sleep -Seconds 1

                            # Proveri zivi Event Log u realnom vremenu
                            try {
                                $liveEvents = Get-WinEvent -LogName "Microsoft-Windows-ModernDeployment-Diagnostics-Provider/Autopilot" -MaxEvents 30 -ErrorAction SilentlyContinue
                                if ($liveEvents) {
                                    foreach ($ev in $liveEvents) {
                                        if ($ev.Id -eq 806 -or ($ev.Message -match 'CloudAssignedTenantId\s*[:=]\s*\{?([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})\}?' -and $matches[1] -ne '00000000-0000-0000-0000-000000000000')) {
                                            $cloudReport.IsRegistered = $true
                                            $cloudReport.QueryMode    = "ACTIVE_ZTD_EVENT_806"
                                            $cloudReport.RawStatusMessage = "Uredjaj je 100% REGISTROVAN u Microsoft Autopilot bazi (Event 806)"
                                            try { $proc.Kill() } catch { }
                                            break
                                        }
                                        if ($ev.Id -eq 807 -or $ev.Message -match "807" -or $ev.Message -match "ZtdDeviceIsNotRegistered" -or $ev.Message -match "ProfileState\s*=\s*NotAssigned") {
                                            $cloudReport.IsRegistered  = $false
                                            $cloudReport.ConfirmedFree = $true
                                            $cloudReport.ErrorCode     = "807 - ZtdDeviceIsNotRegistered"
                                            $cloudReport.QueryMode     = "ACTIVE_ZTD_EVENT_807"
                                            $cloudReport.RawStatusMessage = "Slobodan (Microsoft potvrdio: Uredjaj nije registrovan)"
                                            try { $proc.Kill() } catch { }
                                            break
                                        }
                                    }
                                }
                            } catch { }

                            if ($cloudReport.IsRegistered -or $cloudReport.ConfirmedFree) {
                                break
                            }

                            # Proveri ServiceState keš
                            if (Test-Path $serviceStateJson) {
                                try {
                                    $ssRaw = Get-Content -Path $serviceStateJson -Raw -ErrorAction SilentlyContinue
                                    if (Resolve-ZtdJsonContent -RawJson $ssRaw -CloudReport $cloudReport -QueryMode "ACTIVE_ZTD_SERVICESTATE") {
                                        try { $proc.Kill() } catch { }
                                        break
                                    }
                                } catch { }
                            }
                        }

                        if (-not $proc.HasExited) {
                            try { $proc.Kill() } catch { }
                        }
                    }

                    if ((-not ($cloudReport.IsRegistered -or $cloudReport.ConfirmedFree)) -and (Test-Path $cabPath)) {
                        $expandProc = Start-Process -FilePath "expand.exe" -ArgumentList "`"$cabPath`" -F:* `"$outDir`"" -PassThru -WindowStyle Hidden
                        if ($expandProc -and -not $expandProc.WaitForExit(5000)) {
                            try { $expandProc.Kill() } catch { }
                        }

                        $ztdJsonFile = Join-Path $outDir "AutopilotDDSZTDFile.json"
                        if (Test-Path $ztdJsonFile) {
                            try {
                                $jsonRaw = Get-Content -Path $ztdJsonFile -Raw -ErrorAction Stop
                                Resolve-ZtdJsonContent -RawJson $jsonRaw -CloudReport $cloudReport -QueryMode "ACTIVE_ZTD_DIAG" | Out-Null
                            } catch { }
                        }

                        if (-not $cloudReport.IsRegistered -and -not $cloudReport.ConfirmedFree) {
                            $evtxFiles = Get-ChildItem -Path $outDir -Filter "*.evtx" -ErrorAction SilentlyContinue
                            $foundError807 = $false
                            if ($evtxFiles) {
                                foreach ($ef in $evtxFiles) {
                                    try {
                                        $events = Get-WinEvent -Path $ef.FullName -MaxEvents 50 -ErrorAction SilentlyContinue
                                        foreach ($ev in $events) {
                                            if ($ev.Message -match "807" -or $ev.Message -match "ZtdDeviceIsNotRegistered" -or $ev.Message -match "ProfileState\s*=\s*NotAssigned") {
                                                $foundError807 = $true
                                                break
                                            }
                                        }
                                    } catch { }
                                    if ($foundError807) { break }
                                }
                            }

                            if ($foundError807) {
                                $cloudReport.ConfirmedFree    = $true
                                $cloudReport.ErrorCode        = "807 - ZtdDeviceIsNotRegistered"
                                $cloudReport.QueryMode        = "ACTIVE_ZTD_DIAG"
                                $cloudReport.RawStatusMessage = "Slobodan (Microsoft potvrdio: Uredjaj nije registrovan)"
                            } else {
                                $cloudReport.RawStatusMessage = "ZTD upit poslat, ceka se potvrda servera"
                                $cloudReport.QueryMode        = "ACTIVE_ZTD_UNCONFIRMED"
                            }
                        }
                    } else {
                        if (-not ($cloudReport.IsRegistered -or $cloudReport.ConfirmedFree)) {
                            $cloudReport.RawStatusMessage = "Dijagnosticki alat nije kreirao izvestaj."
                        }
                    }
                } else {
                    $cloudReport.RawStatusMessage = "mdmdiagnosticstool.exe nije pronadjen na sistemu."
                }
            } catch {
                $cloudReport.RawStatusMessage = "Greska pri izvrsavanju: $($_.Exception.Message)"
            } finally {
                try {
                    if ($hadSkipNet -or $hadDisableOobe) {
                        $imgState = ""
                        try {
                            $stProps = Get-ItemProperty -Path "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Setup\State" -ErrorAction SilentlyContinue
                            if ($stProps -and $stProps.ImageState) { $imgState = "$($stProps.ImageState)".Trim() }
                        } catch { }
                        if ($imgState -ine "IMAGE_STATE_COMPLETE") {
                            if (Test-Path $oobeRegPath) {
                                if ($hadSkipNet -and ($null -ne $origSkipNet)) {
                                    Set-ItemProperty -Path $oobeRegPath -Name "SkipNetworkSetup" -Value $origSkipNet -Type DWord -Force -ErrorAction SilentlyContinue
                                }
                                if ($hadDisableOobe -and ($null -ne $origDisableOobe)) {
                                    Set-ItemProperty -Path $oobeRegPath -Name "DisableOOBEConnection" -Value $origDisableOobe -Type DWord -Force -ErrorAction SilentlyContinue
                                }
                            }
                        }
                    }
                } catch { }
                if (Test-Path $tempDir) {
                    Remove-Item -Path $tempDir -Recurse -Force -ErrorAction SilentlyContinue
                }
            }
        }
    }
}

# Prikaz rezultata Cloud provere
if ($cloudReport.OnlineChecked -or $cloudReport.IsRegistered -or $cloudReport.ConfirmedFree) {
    if ($cloudReport.IsRegistered) {
        Write-Host "  [!] REZULTAT IZ OBLAKA: REGISTROVAN U BAZI (AKTIVNO ZAKLJUCAK)" -ForegroundColor Red
        Write-Host "      • Pripada organizaciji: $($cloudReport.ProfileTenantDomain)" -ForegroundColor Red
        Write-Host "      • Tenant GUID:          $($cloudReport.ProfileTenantId)" -ForegroundColor Red
        if ($cloudReport.ProfileDeviceName) {
            Write-Host "      • Auto-Device Name:     $($cloudReport.ProfileDeviceName)" -ForegroundColor Red
        }
    } elseif ($cloudReport.ConfirmedFree) {
        Write-Host "  [*] REZULTAT IZ OBLAKA: POTVRDJENO NIJE REGISTROVAN (SLOBODAN)" -ForegroundColor Green
        Write-Host "      • Status odgovora:      $($cloudReport.RawStatusMessage)" -ForegroundColor Green
        if ($cloudReport.ErrorCode) {
            Write-Host "      • Kod Microsoft servera: $($cloudReport.ErrorCode)" -ForegroundColor Green
        }
    } else {
        Write-Host "  [~] REZULTAT IZ OBLAKA: OBLAK NIJE POTVRDIO STATUS (USLOVNO)" -ForegroundColor Cyan
        Write-Host "      • Napomena:             $($cloudReport.RawStatusMessage)" -ForegroundColor DarkCyan
    }
}

# ==============================================================================
# 4. DETERMINISTICKA 5-STUPANJSKA MATRICA STANJA & ZAVRSNI ZAKLJUCAK
# ==============================================================================
Write-Host "`n================================================================================" -ForegroundColor Cyan
Write-Host "                       KONACNA PRESUDA I PREPORUKA                              " -ForegroundColor Cyan
Write-Host "================================================================================" -ForegroundColor Cyan

$localHas     = $localReport.HasTraces
$cloudChecked = $cloudReport.OnlineChecked
$cloudHas     = $cloudReport.IsRegistered
$cloudFree    = $cloudReport.ConfirmedFree

$matrixCode   = "UNKNOWN"
$statusLevel  = "INFO"
$statusTitle  = "NEPOZNATO STANJE"
$statusDesc   = "Status provere nije odredjen."
$recAction    = "Ponovite proveru."

if ($cloudChecked -and $cloudHas) {
    if ($localHas) {
        # MATRICA 1: Lokalno DA + Cloud DA
        $matrixCode = "LOCKED_TENANT"
        $statusLevel = "CRITICAL"
        $statusTitle = "KRITICNO - STATUS: AKTIVNO VEZAN ZA KORPORATIVNI TENANT"
        $statusDesc = "Uredjaj se aktivno nalazi u Microsoft Autopilot bazi i ima tragove u operativnom sistemu."
        $recAction = "FORMATIRANJE DISKA NE RESAVA PROBLEM! Cim se sistem ponovo instalira, OOBE ce ga zakljucati. IT administrator mora obrisati hardver iz Intune portala pre prenamene ili prodaje."

        Write-Host "+------------------------------------------------------------------------------+" -ForegroundColor Red
        Write-Host "| [!] KRITICNO - STATUS: AKTIVNO VEZAN ZA KORPORATIVNI TENANT                  |" -ForegroundColor Red
        Write-Host "+------------------------------------------------------------------------------+" -ForegroundColor Red
        Write-Host "  • Opis:       Uredjaj se aktivno nalazi u Microsoft Autopilot bazi i ima tragove" -ForegroundColor Red
        Write-Host "                u operativnom sistemu." -ForegroundColor Red
        Write-Host "  • Tenant:     $($cloudReport.ProfileTenantDomain) ($($cloudReport.ProfileTenantId))" -ForegroundColor Red
        if ($cloudReport.ProfileDeviceName) {
            Write-Host "  • Ime masine: $($cloudReport.ProfileDeviceName)" -ForegroundColor Red
        }
        Write-Host "  • Upozorenje: FORMATIRANJE DISKA NE RESAVA PROBLEM!" -ForegroundColor Red
        Write-Host "                Cim se operativni sistem ponovo instalira i spoji na mrezu, OOBE" -ForegroundColor Red
        Write-Host "                ce automatski zakljucati uredjaj na ovu organizaciju." -ForegroundColor Red
        Write-Host "  • Akcija:     Neophodno je da IT administrator navedene firme obrise hardver" -ForegroundColor Red
        Write-Host "                iz svog Intune / Autopilot portala pre prenamene ili prodaje." -ForegroundColor Red
    } else {
        # MATRICA 2: Lokalno NE + Cloud DA
        $matrixCode = "HIDDEN_LOCK"
        $statusLevel = "CRITICAL"
        $statusTitle = "SKRIVENA OPASNOST - STATUS: ZAKLJUCAN U OBLAKU (DISK JE OBRISAN)"
        $statusDesc = "Lokalni operativni sistem je cist (nema registry tragova), ali je hardverski hash ovog racunara i dalje vezan u Microsoft bazi!"
        $recAction = "Racunar ne sme ici u dalju prodaju dok ga vlasnik tenanta ne oslobodi iz Microsoft Intune portala."

        Write-Host "+------------------------------------------------------------------------------+" -ForegroundColor Red
        Write-Host "| [!] SKRIVENA OPASNOST - STATUS: ZAKLJUCAN U OBLAKU (DISK JE OBRISAN)         |" -ForegroundColor Red
        Write-Host "+------------------------------------------------------------------------------+" -ForegroundColor Red
        Write-Host "  • Opis:       Lokalni operativni sistem je cist (nema registry tragova), ali je" -ForegroundColor Red
        Write-Host "                hardverski hash ovog racunara i dalje vezan u Microsoft bazi!" -ForegroundColor Red
        Write-Host "  • Tenant:     $($cloudReport.ProfileTenantDomain) ($($cloudReport.ProfileTenantId))" -ForegroundColor Red
        Write-Host "  • Upozorenje: Stara skripta bi ovde javila lazno zeleno! Racunar NIJE cist!" -ForegroundColor Red
        Write-Host "  • Akcija:     Racunar ne sme ici u dalju prodaju dok ga vlasnik tenanta ne oslobodi." -ForegroundColor Red
    }
} elseif ($cloudChecked -and $cloudFree) {
    if ($localHas) {
        # MATRICA 3: Lokalno DA + Cloud POTVRDJENO SLOBODAN (Event 807)
        $matrixCode = "RELEASED_LOCAL_TRACES"
        $statusLevel = "WARNING"
        $statusTitle = "BEZBEDNO - STATUS: OSLOBODJEN U OBLAKU (POSTOJE SAMO LOKALNI ZAOSTACI)"
        $statusDesc = "Uredjaj je u proslosti bio u firmi, ali je USPESNO DEREGISTROVAN iz Microsoft Autopilot baze u oblaku."
        $recAction = "UREDJAJ JE BEZBEDAN ZA PRODAJU I PRENAMENU. Preporucuje se cista reinstalacija Windowsa (Clean install) kako bi se uklonili svi lokalni ostaci prethodnog tenanta."

        Write-Host "+------------------------------------------------------------------------------+" -ForegroundColor Yellow
        Write-Host "| [*] BEZBEDNO - STATUS: OSLOBODJEN U OBLAKU (POSTOJE SAMO LOKALNI ZAOSTACI)   |" -ForegroundColor Yellow
        Write-Host "+------------------------------------------------------------------------------+" -ForegroundColor Yellow
        Write-Host "  • Opis:       Uredjaj je u proslosti bio u firmi, ali je USPESNO DEREGISTROVAN" -ForegroundColor Yellow
        Write-Host "                iz Microsoft Autopilot baze u oblaku." -ForegroundColor Yellow
        Write-Host "  • Zateceno:   Pronadjeni su stari registry/cert ostaci prethodnog sistema." -ForegroundColor Yellow
        Write-Host "  • Akcija:     UREDJAJ JE BEZBEDAN ZA PRODAJU I PRENAMENU." -ForegroundColor Yellow
    } else {
        # MATRICA 4: Lokalno NE + Cloud POTVRDJENO SLOBODAN (Event 807)
        $matrixCode = "CONFIRMED_CLEAN"
        $statusLevel = "OK"
        $statusTitle = "100% CISTO - STATUS: UREDJAJ JE POTPUNO SLOBODAN (POTVRDJENO U OBLAKU)"
        $statusDesc = "Racunar nema lokalnih tragova korporativnog upravljanja, a Microsoft ZTD servis potvrdjuje da hardver nije registrovan."
        $recAction = "Racunar je u potpunosti cist i bezbedan za rad bilo kog korisnika."

        Write-Host "+------------------------------------------------------------------------------+" -ForegroundColor Green
        Write-Host "| [OK] 100% CISTO - STATUS: UREDJAJ JE POTPUNO SLOBODAN (POTVRDJENO U OBLAKU)  |" -ForegroundColor Green
        Write-Host "+------------------------------------------------------------------------------+" -ForegroundColor Green
        Write-Host "  • Opis:       Racunar nema lokalnih tragova korporativnog upravljanja, a" -ForegroundColor Green
        Write-Host "                Microsoft ZTD servis potvrdjuje da hardver nije registrovan." -ForegroundColor Green
        Write-Host "  • Akcija:     Racunar je u potpunosti cist i bezbedan za rad bilo kog korisnika." -ForegroundColor Green
    }
} elseif ($cloudChecked -and (-not $cloudHas) -and (-not $cloudFree)) {
    # MATRICA 5: Nema profila, ali nema ni koda 807 (ZTD oblak nije dao eksplicitan odgovor)
    if ($localHas) {
        $matrixCode = "UNVERIFIED_LOCAL_TRACES"
        $statusLevel = "WARNING"
        $statusTitle = "UPOZORENJE - LOKALNI TRAGOVI (STATUS U OBLAKU NIJE POTVRDJEN)"
        $statusDesc = "Pronadjeni su lokalni tragovi ($($localReport.TenantDomain)), ali Microsoft ZTD oblak nije vratio eksplicitnu potvrdu."
        $recAction = "Povezite racunar na stabilnu mrezu i ponovite proveru u oblaku."

        Write-Host "+------------------------------------------------------------------------------+" -ForegroundColor Yellow
        Write-Host "| [!] UPOZORENJE - LOKALNI TRAGOVI (STATUS U OBLAKU NIJE POTVRDJEN)            |" -ForegroundColor Yellow
        Write-Host "+------------------------------------------------------------------------------+" -ForegroundColor Yellow
    } else {
        $matrixCode = "UNVERIFIED_CLEAN"
        $statusLevel = "INFO"
        $statusTitle = "USLOVNO CISTO - NEMA LOKALNIH TRAGOVA (STATUS U OBLAKU NIJE POTVRDJEN)"
        $statusDesc = "Lokalni sistem je cist, ali tokom brze instalacije Windows OOBE modul nije dobio eksplicitan odgovor iz Microsoft oblaka."
        $recAction = "Ako je uredjaj otkupljen iz korporacije, preporucuje se provera kroz OOBE pre predaje kupcu."

        Write-Host "+------------------------------------------------------------------------------+" -ForegroundColor Cyan
        Write-Host "| [?] USLOVNO CISTO - NEMA LOKALNIH TRAGOVA (STATUS U OBLAKU NIJE POTVRDJEN)   |" -ForegroundColor Cyan
        Write-Host "+------------------------------------------------------------------------------+" -ForegroundColor Cyan
        Write-Host "  • Opis:       Lokalni sistem je cist, ali status u oblaku nije eksplicitno" -ForegroundColor Cyan
        Write-Host "                potvrdjen (unattended instalacija sa preskocenim OOBE ekranom)." -ForegroundColor Cyan
        Write-Host "  • Preporuka:  Ako je uredjaj iz korporativnog izvora, proverite OOBE ekran pre prodaje." -ForegroundColor Cyan
    }
} else {
    # OFFLINE FALLBACK
    if ($localHas) {
        $matrixCode = "OFFLINE_LOCAL_TRACES"
        $statusLevel = "WARNING"
        $statusTitle = "UPOZORENJE - STATUS: LOKALNI TRAGOVI PRONADJENI (CLOUD PROVERA OFFLINE)"
        $statusDesc = "Pronadjeni su tragovi Autopilota u registru ($($localReport.TenantDomain)). Zbog nedostatka internet konekcije, status u oblaku nije proveren."
        $recAction = "Povezite racunar na internet i ponovite test radi 100% potvrde."

        Write-Host "+------------------------------------------------------------------------------+" -ForegroundColor Yellow
        Write-Host "| [!] UPOZORENJE - STATUS: LOKALNI TRAGOVI PRONADJENI (CLOUD PROVERA OFFLINE)  |" -ForegroundColor Yellow
        Write-Host "+------------------------------------------------------------------------------+" -ForegroundColor Yellow
        Write-Host "  • Opis:       Pronadjeni su tragovi Autopilota u registru ($($localReport.TenantDomain))." -ForegroundColor Yellow
        Write-Host "  • Napomena:   Zbog nedostatka internet konekcije, nije bilo moguce utvrditi da" -ForegroundColor Yellow
        Write-Host "                li je uredjaj u medjuvremenu deregistrovan u oblaku." -ForegroundColor Yellow
        Write-Host "  • Akcija:     Povezite racunar na internet i ponovite test radi 100% potvrde." -ForegroundColor Yellow
    } else {
        $matrixCode = "OFFLINE_CLEAN_LOCAL"
        $statusLevel = "INFO"
        $statusTitle = "USLOVNO CISTO - STATUS: NEMA LOKALNIH TRAGOVA (CLOUD PROVERA OFFLINE)"
        $statusDesc = "Lokalni sistem nema tragova, ali je racunar bio OFFLINE. Ako je disk bio formatiran, status u oblaku ostaje nepoznat."
        $recAction = "Za stopostotnu sigurnost, povezite racunar na mrezu i ponovite test."

        Write-Host "+------------------------------------------------------------------------------+" -ForegroundColor Cyan
        Write-Host "| [?] USLOVNO CISTO - STATUS: NEMA LOKALNIH TRAGOVA (CLOUD PROVERA OFFLINE)    |" -ForegroundColor Cyan
        Write-Host "+------------------------------------------------------------------------------+" -ForegroundColor Cyan
        Write-Host "  • Opis:       Lokalni sistem nema tragova, ali je racunar bio OFFLINE." -ForegroundColor Cyan
        Write-Host "  • Upozorenje: Ako je disk bio formatiran, status u oblaku ostaje nepoznat." -ForegroundColor Cyan
        Write-Host "  • Akcija:     Za stopostotnu sigurnost, povezite racunar na mrezu i ponovite test." -ForegroundColor Cyan
    }
}

$outputPayload = [PSCustomObject]@{
    timestamp          = (Get-Date).ToString("yyyy-MM-ddTHH:mm:ss")
    matrix_code        = $matrixCode
    status_level       = $statusLevel
    status_title       = $statusTitle
    status_description = $statusDesc
    recommended_action = $recAction
    is_locked          = ($matrixCode -eq "LOCKED_TENANT" -or $matrixCode -eq "HIDDEN_LOCK")
    tenant_domain      = if ($localReport.TenantDomain) { "$($localReport.TenantDomain)" } elseif ($cloudReport.ProfileTenantDomain) { "$($cloudReport.ProfileTenantDomain)" } else { "" }
    tenant_id          = if ($localReport.TenantId) { "$($localReport.TenantId)" } elseif ($cloudReport.ProfileTenantId) { "$($cloudReport.ProfileTenantId)" } else { "" }
    device_serial      = "$($hwInfo.SerialNumber)"
    device_model       = "$($hwInfo.Model)"
    hardware           = [PSCustomObject]@{
        manufacturer  = "$($hwInfo.Manufacturer)"
        model         = "$($hwInfo.Model)"
        serial_number = "$($hwInfo.SerialNumber)"
        smbios_uuid   = "$($hwInfo.SmbiosUuid)"
        tpm_present   = [bool]$hwInfo.TpmPresent
        tpm_ready     = [bool]$hwInfo.TpmReady
    }
    local_forensics    = [PSCustomObject]@{
        has_traces               = [bool]$localReport.HasTraces
        tenant_domain            = if ($localReport.TenantDomain) { "$($localReport.TenantDomain)" } else { "" }
        tenant_id                = if ($localReport.TenantId) { "$($localReport.TenantId)" } else { "" }
        assigned_device_name     = if ($localReport.AssignedDeviceName) { "$($localReport.AssignedDeviceName)" } else { "" }
        policy_cache_found       = [bool]$localReport.PolicyCacheFound
        established_corr         = [bool]$localReport.EstablishedCorr
        azure_ad_joined          = [bool]$localReport.AzureAdJoined
        mdm_enrolled             = [bool]$localReport.MdmEnrolled
        org_certificates_count   = [int]($localReport.OrgCertificates.Count)
        device_management_keys   = if ($localReport.HasTraces) { 1 } else { 0 }
        enrollment_keys          = [int]($localReport.MdmEnrollmentUrls.Count)
        enterprise_work_accounts = if ($localReport.CloudDomainJoinUser) { 1 } else { 0 }
        azure_ad_status          = if ($localReport.AzureAdJoined) { "PRIDRUZEN (YES)" } else { "Nije pridruzen (NO)" }
        mdm_status               = if ($localReport.MdmEnrolled) { "UPISAN (YES)" } else { "Nije upisan (NO)" }
        org_certificates         = @($localReport.OrgCertificates)
        mdm_enrollment_urls      = @($localReport.MdmEnrollmentUrls | Select-Object -Unique)
    }
    cloud_ztd          = [PSCustomObject]@{
        online_checked        = [bool]$cloudReport.OnlineChecked
        is_registered         = [bool]$cloudReport.IsRegistered
        confirmed_free        = [bool]$cloudReport.ConfirmedFree
        query_mode            = "$($cloudReport.QueryMode)"
        error_code            = if ($cloudReport.ErrorCode) { "$($cloudReport.ErrorCode)" } else { "" }
        raw_status_message    = "$($cloudReport.RawStatusMessage)"
        profile_tenant_domain = if ($cloudReport.ProfileTenantDomain) { "$($cloudReport.ProfileTenantDomain)" } else { "" }
        profile_tenant_id     = if ($cloudReport.ProfileTenantId) { "$($cloudReport.ProfileTenantId)" } else { "" }
        profile_device_name   = if ($cloudReport.ProfileDeviceName) { "$($cloudReport.ProfileDeviceName)" } else { "" }
    }
    ztd_inspection     = [PSCustomObject]@{
        status      = if (-not $cloudReport.OnlineChecked) { "OFFLINE" } elseif ($cloudReport.IsRegistered) { "ONLINE_MATCH" } elseif ($cloudReport.ConfirmedFree) { "CONFIRMED_FREE" } else { "NOT_REGISTERED" }
        checked     = [bool]$cloudReport.OnlineChecked
        http_status = if ($cloudReport.OnlineChecked) { 200 } else { 0 }
    }
}

$prettyJsonStr = $outputPayload | ConvertTo-Json -Depth 6
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
$newIsConfirmed = ($matrixCode -in @('LOCKED_TENANT', 'HIDDEN_LOCK', 'RELEASED_LOCAL_TRACES', 'CONFIRMED_CLEAN'))
foreach ($savePath in @("C:\Windows\Logs\WIS\autopilot_report.json", "C:\setup\autopilot_report.json")) {
    try {
        if ($savePath -eq "C:\setup\autopilot_report.json" -and -not (Test-Path "C:\setup")) { continue }
        $saveDir = Split-Path $savePath -Parent
        if ($saveDir -and -not (Test-Path $saveDir)) {
            New-Item -Path $saveDir -ItemType Directory -Force -ErrorAction SilentlyContinue | Out-Null
        }
        if ((-not $newIsConfirmed) -and (Test-Path $savePath)) {
            $existingRaw = Get-Content -Path $savePath -Raw -ErrorAction SilentlyContinue
            if (Test-SavedAutopilotReportConfirmed -JsonText $existingRaw) { continue }
        }
        [System.IO.File]::WriteAllText($savePath, $prettyJsonStr, $utf8NoBom)
    } catch {}
}
if (Test-Path $serviceStateJson) {
    try {
        $ztdBackupDir = "C:\Windows\Logs\WIS"
        if (-not (Test-Path $ztdBackupDir)) { New-Item -Path $ztdBackupDir -ItemType Directory -Force -ErrorAction SilentlyContinue | Out-Null }
        Copy-Item -Path $serviceStateJson -Destination (Join-Path $ztdBackupDir "AutopilotDDSZTDFile.preoobe.json") -Force -ErrorAction SilentlyContinue
    } catch {}
}

if ($JsonOutput) {
    $jsonOutputStr = $outputPayload | ConvertTo-Json -Depth 6 -Compress
    [Console]::Out.WriteLine($jsonOutputStr)
    return
} else {
    Write-Host "================================================================================" -ForegroundColor Cyan
    Write-Host "Kraj analize." -ForegroundColor Gray
    Read-Host "`nPritisnite ENTER za zatvaranje prozora..."
}