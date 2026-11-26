<#
.SYNOPSIS
    Microsoft Autopilot Forensic Auditor v3.5.5 (PowerShell 5.1)
.DESCRIPTION
    Deterministicka verifikacija vezanosti uredjaja za Microsoft Entra ID / Intune tenant.
    Objedinjuje duboku lokalnu forenziku sistema (wmansvc, Provisioning\Autopilot,
    EstablishedCorrelations podkljuc, AutopilotPolicyCache\PolicyJsonCache vrednost,
    AutopilotSettings, EnterpriseAssignedAccess, EnrollmentStatusTracking, W-AAD-*)
    sa inspekcijom Windows Autopilot Event Log-a i ZTD oblaka (ztd.dds.microsoft.com).

    Binarni model presude bez laznog zelenog:
      - IMA_AUTOPILOT (CRITICAL): LOCKED_TENANT / HIDDEN_LOCK
      - NEMA_AUTOPILOT (OK / WARNING sa starim tragovima): iskljucivo uz eksplicitnu Event 807 potvrdu
      - NIJE_PROVERENO (WARNING): sva UNVERIFIED_* i OFFLINE_* stanja
.NOTES
    Verzija:  3.5.5
    Kompatibilnost: Windows PowerShell 5.1
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
            timestamp            = (Get-Date).ToString("yyyy-MM-ddTHH:mm:ss")
            matrix_code          = "ELEVATION_FAILED"
            status_level         = "ERROR"
            binary_verdict       = "NIJE_PROVERENO"
            status_title         = "GRESKA - ZAHTEVA ADMINISTRATORSKA PRAVA"
            status_description   = "Nije uspelo podizanje privilegija za pristup registru i ZTD oblaku."
            recommended_action   = "Pokrenite aplikaciju kao Administrator."
            is_locked            = $false
            confirmed_free       = $false
            has_local_traces     = $false
            tenant_domain        = ""
            tenant_id            = ""
            assigned_device_name = ""
            query_mode           = "ELEVATION_REQUIRED"
            source               = "provera_autopilota"
            device_serial        = "Nepoznato"
            device_model         = "Nepoznato"
            hardware             = [PSCustomObject]@{
                manufacturer  = "Nepoznato"
                model         = "Nepoznato"
                serial_number = "Nepoznato"
                smbios_uuid   = "Nepoznato"
                tpm_present   = $false
                tpm_ready     = $false
            }
            local_forensics      = [PSCustomObject]@{
                has_traces                        = $false
                tenant_domain                     = ""
                tenant_id                         = ""
                assigned_device_name              = ""
                policy_cache_found                = $false
                established_corr                  = $false
                autopilot_settings_found          = $false
                enterprise_assigned_access_found  = $false
                enrollment_status_tracking_found  = $false
                waad_device_name_matched          = $false
                matched_waad_name                 = ""
                azure_ad_joined                   = $false
                mdm_enrolled                      = $false
                org_certificates_count            = 0
                device_management_keys            = 0
                enrollment_keys                   = 0
                enterprise_work_accounts          = 0
                azure_ad_status                   = "Nije pridruzen (NO)"
                mdm_status                        = "Nije upisan (NO)"
                org_certificates                  = @()
                mdm_enrollment_urls               = @()
            }
            cloud_ztd            = [PSCustomObject]@{
                online_checked        = $false
                network_reachable     = $false
                is_registered         = $false
                confirmed_free        = $false
                query_mode            = "ELEVATION_REQUIRED"
                error_code            = "ELEVATION_REQUIRED"
                raw_status_message    = "Zahteva administratorska prava"
                profile_tenant_domain = ""
                profile_tenant_id     = ""
                profile_device_name   = ""
                ztd_registration_state = ""
            }
            ztd_inspection       = [PSCustomObject]@{
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

$script:ZeroGuid = "00000000-0000-0000-0000-000000000000"
$script:WaadPattern = '\bW-AAD-\d{9}\b'
$script:GuidPattern = '[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}'

function Test-ValidNonZeroGuid {
    param([string]$Value)
    if ([string]::IsNullOrWhiteSpace($Value)) { return $false }
    $clean = $Value.Trim().Trim('{', '}')
    if ($clean -ieq $script:ZeroGuid) { return $false }
    return ($clean -match "^$script:GuidPattern$")
}

function Get-PrimaryZtdFileCandidates {
    $list = New-Object System.Collections.ArrayList
    $winRoot = if ($env:SystemRoot) { $env:SystemRoot } else { "C:\Windows" }

    # 1. PRIMARNA Windows 11 putanja: C:\Windows\ServiceState\wmansvc\AutopilotDDSZTDFile.json
    [void]$list.Add(@{
        Path = (Join-Path $winRoot "ServiceState\wmansvc\AutopilotDDSZTDFile.json")
        Mode = "WMANSVC_ZTD_FILE"
    })

    # 2. PRIMARNI Windows 11 Provisioning direktorijum: C:\Windows\Provisioning\Autopilot\
    $provDir = Join-Path $winRoot "Provisioning\Autopilot"
    [void]$list.Add(@{
        Path = (Join-Path $provDir "AutopilotConfigurationFile.json")
        Mode = "PROVISIONING_AUTOPILOT"
    })
    [void]$list.Add(@{
        Path = (Join-Path $provDir "AutopilotDDSZTDFile.json")
        Mode = "PROVISIONING_AUTOPILOT"
    })
    if (Test-Path $provDir) {
        try {
            $extraProv = Get-ChildItem -Path $provDir -Filter "*.json" -File -ErrorAction SilentlyContinue
            foreach ($pf in $extraProv) {
                $existsInList = $false
                foreach ($item in $list) {
                    if ($item.Path -ieq $pf.FullName) { $existsInList = $true; break }
                }
                if (-not $existsInList) {
                    [void]$list.Add(@{
                        Path = $pf.FullName
                        Mode = "PROVISIONING_AUTOPILOT"
                    })
                }
            }
        } catch { }
    }

    # 3. SEKUNDARNI fallback: Pre-OOBE rezervna kopija i starija ServiceState\Autopilot putanja
    [void]$list.Add(@{
        Path = "C:\Windows\Logs\WIS\AutopilotDDSZTDFile.preoobe.json"
        Mode = "PREOOBE_ZTD_FILE"
    })
    [void]$list.Add(@{
        Path = (Join-Path $winRoot "ServiceState\Autopilot\AutopilotDDSZTDFile.json")
        Mode = "SERVICE_STATE_CACHE"
    })

    return ,$list.ToArray()
}

Clear-Host
Write-Host "================================================================================" -ForegroundColor Cyan
Write-Host "               MICROSOFT AUTOPILOT FORENSIC AUDITOR v3.5.5                      " -ForegroundColor Cyan
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
        if ($cs.Manufacturer) { $hwInfo.Manufacturer = $cs.Manufacturer.Trim() }
        if ($cs.Model)        { $hwInfo.Model        = $cs.Model.Trim() }
    }
    $bios = Get-CimInstance -ClassName Win32_BIOS -ErrorAction SilentlyContinue
    if ($bios -and $bios.SerialNumber) {
        $hwInfo.SerialNumber = $bios.SerialNumber.Trim()
    }
    $csp = Get-CimInstance -ClassName Win32_ComputerSystemProduct -ErrorAction SilentlyContinue
    if ($csp -and $csp.UUID) {
        $hwInfo.SmbiosUuid = $csp.UUID.Trim()
    }
    $tpm = Get-Tpm -ErrorAction SilentlyContinue
    if ($tpm) {
        $hwInfo.TpmPresent = [bool]$tpm.TpmPresent
        $hwInfo.TpmReady   = [bool]$tpm.TpmReady
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
    HasTraces                      = $false
    TenantId                       = $null
    TenantDomain                   = $null
    AssignedDeviceName             = $null
    PolicyCacheFound               = $false
    EstablishedCorr                = $false
    AutopilotSettingsFound         = $false
    EnterpriseAssignedAccessFound  = $false
    EnrollmentStatusTrackingFound  = $false
    WaadDeviceNameMatched          = $false
    MatchedWaadName                = $null
    MdmEnrollmentUrls              = @()
    CloudDomainJoinTenant          = $null
    CloudDomainJoinUser            = $null
    OrgCertificates                = @()
    AzureAdJoined                  = $false
    MdmEnrolled                    = $false
}

function Add-WaadNameCheck {
    param([string]$Candidate)
    if ([string]::IsNullOrWhiteSpace($Candidate)) { return }
    if ($Candidate -match $script:WaadPattern) {
        $matchedName = $matches[0].ToUpper()
        $localReport.HasTraces = $true
        $localReport.WaadDeviceNameMatched = $true
        if (-not $localReport.MatchedWaadName) {
            $localReport.MatchedWaadName = $matchedName
        }
        if (-not $localReport.AssignedDeviceName) {
            $localReport.AssignedDeviceName = $matchedName
        }
    }
}

function Scan-ProfileHintsInText {
    param([string]$Text)
    if ([string]::IsNullOrWhiteSpace($Text)) { return }
    if ($Text -match '"CloudAssignedTenantId"\s*:\s*"([^"]+)"') {
        $tid = $matches[1].Trim().Trim('{', '}')
        if (Test-ValidNonZeroGuid -Value $tid) {
            $localReport.HasTraces = $true
            if (-not $localReport.TenantId) { $localReport.TenantId = $tid }
        }
    }
    if ($Text -match '"CloudAssignedTenantDomain"\s*:\s*"([^"]+)"') {
        $dom = $matches[1].Trim()
        if ($dom) {
            $localReport.HasTraces = $true
            if (-not $localReport.TenantDomain) { $localReport.TenantDomain = $dom }
        }
    }
    if ($Text -match '"CloudAssignedDeviceName"\s*:\s*"([^"]+)"') {
        $dev = $matches[1].Trim()
        if ($dev) {
            $localReport.HasTraces = $true
            if (-not $localReport.AssignedDeviceName) { $localReport.AssignedDeviceName = $dev }
            Add-WaadNameCheck -Candidate $dev
        }
    }
    Add-WaadNameCheck -Candidate $Text
}

function Get-RegistryNonSystemEntriesCount {
    param([string]$RegPath)
    $count = 0
    if (-not (Test-Path $RegPath)) { return 0 }
    try {
        $item = Get-Item -Path $RegPath -ErrorAction SilentlyContinue
        if ($item) {
            foreach ($vn in $item.GetValueNames()) {
                $val = $item.GetValue($vn)
                if ($null -ne $val) {
                    $sVal = "$val".Trim()
                    if ($sVal -ne "") {
                        $count++
                        if ($vn -ieq "CloudAssignedTenantId" -or $vn -ieq "TenantId") {
                            if (Test-ValidNonZeroGuid -Value $sVal) {
                                $localReport.HasTraces = $true
                                if (-not $localReport.TenantId) { $localReport.TenantId = $sVal.Trim('{', '}') }
                            }
                        } elseif ($vn -ieq "CloudAssignedTenantDomain" -or $vn -ieq "TenantDomain") {
                            $localReport.HasTraces = $true
                            if (-not $localReport.TenantDomain) { $localReport.TenantDomain = $sVal }
                        } elseif ($vn -ieq "CloudAssignedDeviceName") {
                            $localReport.HasTraces = $true
                            if (-not $localReport.AssignedDeviceName) { $localReport.AssignedDeviceName = $sVal }
                            Add-WaadNameCheck -Candidate $sVal
                        } else {
                            Scan-ProfileHintsInText -Text $sVal
                        }
                    }
                }
            }
        }
        $subKeys = @(Get-ChildItem -Path $RegPath -ErrorAction SilentlyContinue)
        foreach ($sk in $subKeys) {
            $count++
            Add-WaadNameCheck -Candidate $sk.PSChildName
            $subItem = Get-Item -Path $sk.PSPath -ErrorAction SilentlyContinue
            if ($subItem) {
                foreach ($svn in $subItem.GetValueNames()) {
                    $sval = $subItem.GetValue($svn)
                    if ($null -ne $sval -and "$sval".Trim() -ne "") {
                        $count++
                        Scan-ProfileHintsInText -Text ("$sval".Trim())
                    }
                }
            }
        }
    } catch { }
    return $count
}

# 2.0 Provera imena racunara i inicijalnog hostnama na W-AAD-<9 cifara> obrazac
Add-WaadNameCheck -Candidate $env:COMPUTERNAME
foreach ($hostFile in @("C:\setup\initial-hostname.txt", "C:\Windows\Logs\WIS\initial-hostname.txt")) {
    if (Test-Path $hostFile) {
        try {
            $initHost = (Get-Content -Path $hostFile -Raw -ErrorAction SilentlyContinue)
            if ($initHost) { Add-WaadNameCheck -Candidate $initHost.Trim() }
        } catch { }
    }
}

# 2.1 Diagnostics\AutoPilot (ukljucujuci podkljuc EstablishedCorrelations!)
$diagPath = "HKLM:\SOFTWARE\Microsoft\Provisioning\Diagnostics\AutoPilot"
if (Test-Path $diagPath) {
    try {
        $props = Get-ItemProperty -Path $diagPath -ErrorAction Stop
        if ($props.CloudAssignedTenantId -and (Test-ValidNonZeroGuid -Value "$($props.CloudAssignedTenantId)")) {
            $localReport.HasTraces = $true
            $localReport.TenantId  = "$($props.CloudAssignedTenantId)".Trim().Trim('{', '}')
        }
        if ($props.CloudAssignedTenantDomain -and "$($props.CloudAssignedTenantDomain)".Trim() -ne "") {
            $localReport.HasTraces    = $true
            $localReport.TenantDomain = "$($props.CloudAssignedTenantDomain)".Trim()
        }
        if ($props.CloudAssignedDeviceName -and "$($props.CloudAssignedDeviceName)".Trim() -ne "") {
            $devNameStr = "$($props.CloudAssignedDeviceName)".Trim()
            $localReport.HasTraces          = $true
            $localReport.AssignedDeviceName = $devNameStr
            Add-WaadNameCheck -Candidate $devNameStr
        }
        if ($null -ne $props.EstablishedCorrelations -and "$($props.EstablishedCorrelations)".Trim() -ne "") {
            $localReport.HasTraces       = $true
            $localReport.EstablishedCorr = $true
        }
    } catch { }
}

# EstablishedCorrelations je PODKLJUC pod Diagnostics\AutoPilot
$estCorrSubKey = "HKLM:\SOFTWARE\Microsoft\Provisioning\Diagnostics\AutoPilot\EstablishedCorrelations"
if (Test-Path $estCorrSubKey) {
    $estCount = Get-RegistryNonSystemEntriesCount -RegPath $estCorrSubKey
    if ($estCount -gt 0) {
        $localReport.HasTraces       = $true
        $localReport.EstablishedCorr = $true
    }
}

# 2.2 AutopilotPolicyCache (PolicyJsonCache je VREDNOST unutar AutopilotPolicyCache + sve vrednosti/podkljucevi)
$cacheParentPath = "HKLM:\SOFTWARE\Microsoft\Provisioning\AutopilotPolicyCache"
if (Test-Path $cacheParentPath) {
    try {
        $cacheProps = Get-ItemProperty -Path $cacheParentPath -ErrorAction SilentlyContinue
        if ($cacheProps -and $null -ne $cacheProps.PolicyJsonCache -and "$($cacheProps.PolicyJsonCache)".Trim() -ne "") {
            $localReport.HasTraces        = $true
            $localReport.PolicyCacheFound = $true
            Scan-ProfileHintsInText -Text "$($cacheProps.PolicyJsonCache)"
        }
        $cacheEntries = Get-RegistryNonSystemEntriesCount -RegPath $cacheParentPath
        if ($cacheEntries -gt 0) {
            $localReport.HasTraces        = $true
            $localReport.PolicyCacheFound = $true
        }
    } catch { }
}

# 2.2b AutopilotSettings
$apSettingsPath = "HKLM:\SOFTWARE\Microsoft\Provisioning\AutopilotSettings"
if (Test-Path $apSettingsPath) {
    $setEntries = Get-RegistryNonSystemEntriesCount -RegPath $apSettingsPath
    if ($setEntries -gt 0) {
        $localReport.HasTraces              = $true
        $localReport.AutopilotSettingsFound = $true
    }
}

# 2.2c EnterpriseAssignedAccess
$eaaPath = "HKLM:\SOFTWARE\Microsoft\Provisioning\EnterpriseAssignedAccess"
if (Test-Path $eaaPath) {
    $eaaEntries = Get-RegistryNonSystemEntriesCount -RegPath $eaaPath
    if ($eaaEntries -gt 0) {
        $localReport.HasTraces                     = $true
        $localReport.EnterpriseAssignedAccessFound = $true
    }
}

# 2.2d EnrollmentStatusTracking
$espPaths = @(
    "HKLM:\SOFTWARE\Microsoft\Windows\Autopilot\EnrollmentStatusTracking",
    "HKLM:\SOFTWARE\Microsoft\Provisioning\EnrollmentStatusTracking",
    "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\DeviceManagement\EnrollmentStatusTracking",
    "HKLM:\SOFTWARE\Microsoft\Enrollments\Status"
)
foreach ($espPath in $espPaths) {
    if (Test-Path $espPath) {
        $espEntries = Get-RegistryNonSystemEntriesCount -RegPath $espPath
        if ($espEntries -gt 0) {
            $localReport.HasTraces                     = $true
            $localReport.EnrollmentStatusTrackingFound = $true
        }
    }
}

# 2.3 Enrollments
$enrollPath = "HKLM:\SOFTWARE\Microsoft\Enrollments"
if (Test-Path $enrollPath) {
    try {
        Get-ChildItem -Path $enrollPath -ErrorAction SilentlyContinue | ForEach-Object {
            if ($_.PSChildName -match "ZTD") {
                $localReport.HasTraces = $true
            }
            $eProps = Get-ItemProperty -Path $_.PSPath -ErrorAction SilentlyContinue
            if ($eProps -and $eProps.DiscoveryServiceFullURL) {
                $localReport.HasTraces = $true
                $localReport.MdmEnrollmentUrls += "$($eProps.DiscoveryServiceFullURL)".Trim()
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
            if ($jProps -and $jProps.TenantId -and (Test-ValidNonZeroGuid -Value "$($jProps.TenantId)")) {
                $localReport.HasTraces             = $true
                $localReport.CloudDomainJoinTenant = "$($jProps.TenantId)".Trim().Trim('{', '}')
                if (-not $localReport.TenantId) { $localReport.TenantId = $localReport.CloudDomainJoinTenant }
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
        $localReport.AzureAdJoined = [bool]($dsreg -match "AzureAdJoined\s*:\s*YES")
        $localReport.MdmEnrolled   = [bool]($dsreg -match "MdmEnrolled\s*:\s*YES")
        if ($localReport.AzureAdJoined -or $localReport.MdmEnrolled) {
            $localReport.HasTraces = $true
        }
    }
} catch { }

# Prikaz lokalnih nalaza
if ($localReport.TenantDomain -or $localReport.TenantId -or $localReport.WaadDeviceNameMatched) {
    Write-Host "  [!] Pronadjen Autopilot profil / korporativni trag u registru:" -ForegroundColor Red
    if ($localReport.TenantDomain)       { Write-Host "      * Tenant domen:          $($localReport.TenantDomain)" -ForegroundColor Red }
    if ($localReport.TenantId)           { Write-Host "      * Tenant ID:             $($localReport.TenantId)" -ForegroundColor Red }
    if ($localReport.AssignedDeviceName) { Write-Host "      * Dodeljeno ime masine:  $($localReport.AssignedDeviceName)" -ForegroundColor Red }
} else {
    Write-Host "  [*] Autopilot grana registra:       Nema dodeljenog tenant-a" -ForegroundColor Green
}

Write-Host "  [*] AutopilotPolicyCache:          $(if($localReport.PolicyCacheFound){'PRONADJEN (Postoji profil)'}else{'Nema zapisa'})" -ForegroundColor $(if($localReport.PolicyCacheFound){'Red'}else{'Green'})
Write-Host "  [*] EstablishedCorrelations:       $(if($localReport.EstablishedCorr){'POSTOJI (podkljuc)'}else{'Nema zapisa'})" -ForegroundColor $(if($localReport.EstablishedCorr){'Red'}else{'Green'})
Write-Host "  [*] AutopilotSettings:             $(if($localReport.AutopilotSettingsFound){'PRONADJEN'}else{'Nema zapisa'})" -ForegroundColor $(if($localReport.AutopilotSettingsFound){'Red'}else{'Green'})
Write-Host "  [*] Entra ID (Azure AD) status:    $(if($localReport.AzureAdJoined){'PRIDRUZEN (YES)'}else{'Nije pridruzen (NO)'})" -ForegroundColor $(if($localReport.AzureAdJoined){'Red'}else{'Green'})
Write-Host "  [*] Intune MDM upis:               $(if($localReport.MdmEnrolled){'UPISAN (YES)'}else{'Nije upisan (NO)'})" -ForegroundColor $(if($localReport.MdmEnrolled){'Red'}else{'Green'})

if ($localReport.MdmEnrollmentUrls.Count -gt 0) {
    Write-Host "  [!] Pronadjeni MDM Enrollment URL-ovi ($($localReport.MdmEnrollmentUrls.Count)):" -ForegroundColor Red
    foreach ($url in ($localReport.MdmEnrollmentUrls | Select-Object -Unique)) {
        Write-Host "      * $url" -ForegroundColor Red
    }
}

if ($localReport.OrgCertificates.Count -gt 0) {
    Write-Host "  [!] Pronadjeni organizacioni kriptografski sertifikati:" -ForegroundColor Red
    foreach ($cert in $localReport.OrgCertificates) {
        Write-Host "      * $cert" -ForegroundColor Red
    }
}

# ==============================================================================
# 3. LIVE CLOUD ZTD PROVERA (Microsoft Zero Touch Deployment)
# ==============================================================================
Write-Host "`n[3] ZIVA PROVERA U MICROSOFT OBLAKU (ztd.dds.microsoft.com)" -ForegroundColor Yellow
Write-Host "--------------------------------------------------------------------------------" -ForegroundColor DarkGray

$cloudReport = [PSCustomObject]@{
    OnlineChecked        = $false
    NetworkReachable     = $false
    IsRegistered         = $false
    ConfirmedFree        = $false
    ErrorCode            = $null
    ProfileTenantId      = $null
    ProfileTenantDomain  = $null
    ProfileDeviceName    = $null
    ZtdRegistrationState = $null
    RawStatusMessage     = "Nije provereno"
    QueryMode            = "NONE"
}

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
        [string]$QueryMode = "WMANSVC_ZTD_FILE"
    )
    if ([string]::IsNullOrWhiteSpace($RawJson)) { return $false }
    try {
        $parsed = $RawJson | ConvertFrom-Json -ErrorAction Stop
        if ($null -eq $parsed) { return $false }

        $tid = if ($null -ne $parsed.PSObject.Properties['CloudAssignedTenantId'] -and $parsed.CloudAssignedTenantId) { "$($parsed.CloudAssignedTenantId)".Trim().Trim('{', '}') } else { "" }
        $dom = if ($null -ne $parsed.PSObject.Properties['CloudAssignedTenantDomain'] -and $parsed.CloudAssignedTenantDomain) { "$($parsed.CloudAssignedTenantDomain)".Trim() } else { "" }
        $dev = if ($null -ne $parsed.PSObject.Properties['CloudAssignedDeviceName'] -and $parsed.CloudAssignedDeviceName) { "$($parsed.CloudAssignedDeviceName)".Trim() } else { "" }
        if ($null -ne $parsed.PSObject.Properties['ZtdRegistrationState'] -and $parsed.ZtdRegistrationState) {
            $CloudReport.ZtdRegistrationState = "$($parsed.ZtdRegistrationState)".Trim()
        }

        $hasValidTid = Test-ValidNonZeroGuid -Value $tid
        $hasWaadDev  = ($dev -and ($dev -match $script:WaadPattern))

        if ($hasValidTid -or $dom -or $hasWaadDev) {
            $CloudReport.OnlineChecked       = $true
            $CloudReport.IsRegistered        = $true
            $CloudReport.ConfirmedFree       = $false
            $CloudReport.ProfileTenantId     = if ($hasValidTid) { $tid } else { $null }
            $CloudReport.ProfileTenantDomain = if ($dom) { $dom } else { $null }
            $CloudReport.ProfileDeviceName   = if ($dev) { $dev } else { $null }
            $CloudReport.QueryMode           = $QueryMode
            $CloudReport.RawStatusMessage    = "Uredjaj je 100% REGISTROVAN u Microsoft Autopilot bazi ($QueryMode)"
            return $true
        }

        # ZABRANJENO: prazan CloudAssignedTenantId ili ProfileState=NotAssigned BEZ eksplicitne Event 807 / ZtdDeviceIsNotRegistered potvrde
        # NIKADA ne sme postaviti ConfirmedFree = $true!
        $hasExplicit807 = ($RawJson -match 'ZtdDeviceIsNotRegistered') -or ($RawJson -match '"(?:ErrorCode|EventId|ZtdStatusCode)"\s*:\s*(?:807|"807[^"]*")')
        if ($hasExplicit807) {
            $CloudReport.OnlineChecked    = $true
            $CloudReport.IsRegistered     = $false
            $CloudReport.ConfirmedFree    = $true
            $CloudReport.ErrorCode        = "807 - ZtdDeviceIsNotRegistered"
            $CloudReport.QueryMode        = $QueryMode
            $CloudReport.RawStatusMessage = "Slobodan (Microsoft ZTD eksplicitno potvrdio: Event 807 - ZtdDeviceIsNotRegistered)"
            return $true
        }

        $CloudReport.IsRegistered  = $false
        $CloudReport.ConfirmedFree = $false
        if (-not $CloudReport.ZtdRegistrationState) {
            $CloudReport.ZtdRegistrationState = "Unverified_NotAssigned"
        }
        $CloudReport.RawStatusMessage = "ZTD fajl sadrzi prazan TenantId / ProfileState=NotAssigned bez eksplicitne Event 807 potvrde"
    } catch { }
    return $false
}

function Resolve-AutopilotEventRecord {
    param(
        [Parameter(Mandatory=$true)]$EventObj,
        [Parameter(Mandatory=$true)]$CloudReport,
        [string]$QueryMode = "LIVE_EVENT_LOG"
    )
    $evId = [int]$EventObj.Id
    $msg = if ($EventObj.Message) { "$($EventObj.Message)" } else { "" }
    $xml = ""
    try { $xml = $EventObj.ToXml() } catch { }
    $combined = $msg + "`n" + $xml

    $tid = ""
    if ($combined -match '<Data\s+Name\s*=\s*["'']CloudAssignedTenantId["''][^>]*>([^<]+)</Data>') {
        if (Test-ValidNonZeroGuid -Value $matches[1]) { $tid = $matches[1].Trim().Trim('{', '}') }
    } elseif ($combined -match "CloudAssignedTenantId[^0-9a-fA-F]*($script:GuidPattern)") {
        if (Test-ValidNonZeroGuid -Value $matches[1]) { $tid = $matches[1].Trim() }
    }

    $dom = ""
    if ($combined -match '<Data\s+Name\s*=\s*["'']CloudAssignedTenantDomain["''][^>]*>([^<]+)</Data>') {
        $dom = $matches[1].Trim()
    } elseif ($combined -match '"?CloudAssignedTenantDomain"?\s*[:=]\s*"?([^"\r\n,;<>]+)"?') {
        $dom = $matches[1].Trim()
    }

    $dev = ""
    if ($combined -match '<Data\s+Name\s*=\s*["'']CloudAssignedDeviceName["''][^>]*>([^<]+)</Data>') {
        $dev = $matches[1].Trim()
    } elseif ($combined -match '"?CloudAssignedDeviceName"?\s*[:=]\s*"?([^"\r\n,;<>]+)"?') {
        $dev = $matches[1].Trim()
    }
    if (-not $dev -and $combined -match $script:WaadPattern) {
        $dev = $matches[0].ToUpper()
    }

    $zState = ""
    if ($combined -match '<Data\s+Name\s*=\s*["'']ZtdRegistrationState["''][^>]*>([^<]+)</Data>') {
        $zState = $matches[1].Trim()
    } elseif ($combined -match '"?ZtdRegistrationState"?\s*[:=]\s*"?([^"\r\n,;<>]+)"?') {
        $zState = $matches[1].Trim()
    }
    if ($zState -and (-not $CloudReport.ZtdRegistrationState)) {
        $CloudReport.ZtdRegistrationState = $zState
    }

    if ($tid -and (-not $CloudReport.ProfileTenantId))     { $CloudReport.ProfileTenantId     = $tid }
    if ($dom -and (-not $CloudReport.ProfileTenantDomain)) { $CloudReport.ProfileTenantDomain = $dom }
    if ($dev -and (-not $CloudReport.ProfileDeviceName))   { $CloudReport.ProfileDeviceName   = $dev }

    $isModernProfileEv = $evId -in @(100, 110, 170, 171)
    $stateSaysRegistered = ($zState -and ($zState -match 'Registered') -and ($zState -notmatch 'NotRegistered|Unregistered'))
    $hasRegistered = ($evId -eq 806) -or ($tid -ne "") -or ($dom -ne "") -or ($dev -and ($dev -match $script:WaadPattern)) -or ($isModernProfileEv -and $dev -ne "") -or $stateSaysRegistered

    if ($hasRegistered) {
        $CloudReport.OnlineChecked    = $true
        $CloudReport.IsRegistered     = $true
        $CloudReport.ConfirmedFree    = $false
        $CloudReport.QueryMode        = $QueryMode
        $CloudReport.RawStatusMessage = "Uredjaj je 100% REGISTROVAN u Microsoft Autopilot bazi (Event $evId)"
        return $true
    }

    # ISKLJUCIVO eksplicitni Event 807 ili ZtdDeviceIsNotRegistered (NIKADA ProfileState=NotAssigned)
    if ($evId -eq 807 -or ($combined -match 'ZtdDeviceIsNotRegistered')) {
        $CloudReport.OnlineChecked    = $true
        $CloudReport.IsRegistered     = $false
        $CloudReport.ConfirmedFree    = $true
        $CloudReport.ErrorCode        = "807 - ZtdDeviceIsNotRegistered"
        $CloudReport.QueryMode        = $QueryMode
        $CloudReport.RawStatusMessage = "Slobodan (Microsoft potvrdio: Event 807 - ZtdDeviceIsNotRegistered)"
        return $true
    }

    return $false
}

function Invoke-ScanLiveAutopilotEventLog {
    param(
        [Parameter(Mandatory=$true)]$CloudReport,
        [string]$QueryMode = "LIVE_EVENT_LOG"
    )
    try {
        # Cita ceo Autopilot Event Log bez ogranicenja na 50 unosa i podrzava 100/110/170/171/806/807
        $liveEvents = Get-WinEvent -LogName "Microsoft-Windows-ModernDeployment-Diagnostics-Provider/Autopilot" -ErrorAction SilentlyContinue
        if ($liveEvents) {
            $decisive = $false
            $enrichLeft = 0
            foreach ($ev in $liveEvents) {
                if (-not $decisive) {
                    if (Resolve-AutopilotEventRecord -EventObj $ev -CloudReport $CloudReport -QueryMode $QueryMode) {
                        $decisive = $true
                        if ($CloudReport.IsRegistered -and ((-not $CloudReport.ProfileTenantDomain) -or (-not $CloudReport.ProfileTenantId) -or (-not $CloudReport.ProfileDeviceName))) {
                            $enrichLeft = 25
                            continue
                        }
                        return $true
                    }
                } elseif ($enrichLeft -gt 0) {
                    $enrichLeft--
                    $evXml = ""
                    try { $evXml = $ev.ToXml() } catch { }
                    $comb = "$($ev.Message)`n$evXml"
                    if (-not $CloudReport.ProfileTenantId -and $comb -match "CloudAssignedTenantId[^0-9a-fA-F]*($script:GuidPattern)") {
                        if (Test-ValidNonZeroGuid -Value $matches[1]) { $CloudReport.ProfileTenantId = $matches[1].Trim() }
                    }
                    if (-not $CloudReport.ProfileTenantDomain -and $comb -match '"?CloudAssignedTenantDomain"?\s*[:=]\s*"?([^"\r\n,;<>]+)"?') {
                        $CloudReport.ProfileTenantDomain = $matches[1].Trim()
                    }
                    if (-not $CloudReport.ProfileDeviceName -and $comb -match '"?CloudAssignedDeviceName"?\s*[:=]\s*"?([^"\r\n,;<>]+)"?') {
                        $CloudReport.ProfileDeviceName = $matches[1].Trim()
                    }
                    if ($enrichLeft -eq 0) { return $true }
                }
            }
            if ($decisive) { return $true }
        }
    } catch { }
    return $false
}

function Test-SavedAutopilotReportConfirmed {
    param([string]$JsonText)
    if ([string]::IsNullOrWhiteSpace($JsonText)) { return $false }
    try {
        $obj = $JsonText | ConvertFrom-Json -ErrorAction Stop
        if ($null -eq $obj) { return $false }
        $mc = if ($obj.matrix_code) { "$($obj.matrix_code)".ToUpper().Trim() } else { "" }
        if ($mc -in @('LOCKED_TENANT', 'HIDDEN_LOCK') -or $obj.is_locked -eq $true) { return $true }
        if ($obj.cloud_ztd -and $obj.cloud_ztd.is_registered -eq $true) { return $true }
        if (($mc -in @('RELEASED_LOCAL_TRACES', 'CONFIRMED_CLEAN')) -and $obj.cloud_ztd -and $obj.cloud_ztd.confirmed_free -eq $true) {
            $ec = if ($obj.cloud_ztd.error_code) { "$($obj.cloud_ztd.error_code)" } else { "" }
            if ($ec -notmatch '\(ZTD File\)') { return $true }
        }
    } catch { }
    return $false
}

# 3.0 Provera primarnih ZTD fajlova (wmansvc, Provisioning\Autopilot) i sacuvanih izvestaja (radi i offline!)
$cacheMatched = $false
$ztdCandidates = Get-PrimaryZtdFileCandidates

foreach ($ztdCandidate in $ztdCandidates) {
    if (Test-Path $ztdCandidate.Path) {
        try {
            $rawZtd = Get-Content -Path $ztdCandidate.Path -Raw -ErrorAction SilentlyContinue
            if (Resolve-ZtdJsonContent -RawJson $rawZtd -CloudReport $cloudReport -QueryMode $ztdCandidate.Mode) {
                $cacheMatched = $true
                break
            }
        } catch { }
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
            $czReg = ($cz -and $cz.is_registered -eq $true) -or ($repObj.is_locked -eq $true)
            $czFree = ($cz -and $cz.confirmed_free -eq $true)
            $czErr = if ($cz -and $cz.error_code) { "$($cz.error_code)".Trim() } else { "" }

            if ($mc -in @('LOCKED_TENANT', 'HIDDEN_LOCK') -or $czReg) {
                $cloudReport.OnlineChecked       = $true
                $cloudReport.IsRegistered        = $true
                $cloudReport.ConfirmedFree       = $false
                $cloudReport.ProfileTenantDomain = if ($cz -and $cz.profile_tenant_domain) { "$($cz.profile_tenant_domain)".Trim() } elseif ($repObj.tenant_domain) { "$($repObj.tenant_domain)".Trim() } else { $null }
                $cloudReport.ProfileTenantId     = if ($cz -and $cz.profile_tenant_id) { "$($cz.profile_tenant_id)".Trim() } elseif ($repObj.tenant_id) { "$($repObj.tenant_id)".Trim() } else { $null }
                $cloudReport.ProfileDeviceName   = if ($cz -and $cz.profile_device_name) { "$($cz.profile_device_name)".Trim() } elseif ($repObj.assigned_device_name) { "$($repObj.assigned_device_name)".Trim() } else { $null }
                $cloudReport.ErrorCode           = $czErr
                $cloudReport.QueryMode           = "PREOOBE_CACHED_REPORT"
                $cloudReport.RawStatusMessage    = if ($cz -and $cz.raw_status_message) { "$($cz.raw_status_message)" } else { "Uredjaj je 100% REGISTROVAN u Microsoft Autopilot bazi (Sacuvan OOBE izvestaj)" }
                $cacheMatched = $true
                break
            }

            $explicitCached807 = ($czErr -match '807|ZtdDeviceIsNotRegistered') -and ($czErr -notmatch '\(ZTD File\)')
            if ($czFree -and $explicitCached807 -and ($mc -in @('CONFIRMED_CLEAN', 'RELEASED_LOCAL_TRACES'))) {
                $cloudReport.OnlineChecked    = $true
                $cloudReport.IsRegistered     = $false
                $cloudReport.ConfirmedFree    = $true
                $cloudReport.ErrorCode        = $czErr
                $cloudReport.QueryMode        = "PREOOBE_CACHED_REPORT"
                $cloudReport.RawStatusMessage = "Slobodan (Potvrdjeno iz sacuvanog OOBE Event 807 izvestaja)"
                $cacheMatched = $true
                break
            }
        } catch { }
    }
}

# 3.1 Provera zivog Event Log-a (radi i ako je mreza trenutno offline, ukoliko je OOBE vec obavio ZTD razmenu)
if (-not $cacheMatched) {
    Write-Host "  [*] Ocitavam kompletan Windows Autopilot Event Log..." -ForegroundColor Cyan
    if (Invoke-ScanLiveAutopilotEventLog -CloudReport $cloudReport -QueryMode "LIVE_EVENT_LOG") {
        $cacheMatched = $true
    }
}

if ($cacheMatched) {
    Write-Host "  [*] Pronadjena realna ZTD evidencija ($($cloudReport.QueryMode))." -ForegroundColor Green
} else {
    Write-Host "  [*] Proveravam dostupnost Microsoft ZTD servisa (ztd.dds.microsoft.com:443)..." -NoNewline
    $canReachZtd = Test-TcpConnectionFast -hostname "ztd.dds.microsoft.com" -port 443
    $cloudReport.NetworkReachable = [bool]$canReachZtd

    if (-not $canReachZtd) {
        Write-Host " [OFFLINE]" -ForegroundColor Yellow
        Write-Host "  [!] Uredjaj nema vezu ka ztd.dds.microsoft.com:443." -ForegroundColor Yellow
        $cloudReport.OnlineChecked = $false
        $cloudReport.RawStatusMessage = "OFFLINE - Microsoft ZTD servis (ztd.dds.microsoft.com:443) nije dostupan"
        $cloudReport.QueryMode = "OFFLINE"
    } else {
        Write-Host " [DOSTUPAN - CEKA SE REALNA ZTD RAZMENA]" -ForegroundColor Yellow
        # BITNO: OnlineChecked ostaje $false dok ne stigne stvaran Event 806/807 ili ZTD JSON odgovor!

        # 3.3 AKTIVNO BUDJENJE OOBE ZTD RADNIKA ako status jos uvek nije eksplicitno razresen
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

            try {
                Start-Service -Name "tbs" -ErrorAction SilentlyContinue
                Start-Service -Name "wlidsvc" -ErrorAction SilentlyContinue
                Start-Service -Name "dmwappushservice" -ErrorAction SilentlyContinue
                Start-Service -Name "wmansvc" -ErrorAction SilentlyContinue
            } catch { }

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
                    for ($waitSec = 0; $waitSec -lt 35; $waitSec++) {
                        if ($proc.HasExited) { break }
                        Start-Sleep -Seconds 1

                        if (Invoke-ScanLiveAutopilotEventLog -CloudReport $cloudReport -QueryMode "ACTIVE_ZTD_EVENT") {
                            try { $proc.Kill() } catch { }
                            break
                        }

                        foreach ($cand in (Get-PrimaryZtdFileCandidates)) {
                            if (Test-Path $cand.Path) {
                                try {
                                    $ssRaw = Get-Content -Path $cand.Path -Raw -ErrorAction SilentlyContinue
                                    if (Resolve-ZtdJsonContent -RawJson $ssRaw -CloudReport $cloudReport -QueryMode "ACTIVE_ZTD_SERVICESTATE") {
                                        try { $proc.Kill() } catch { }
                                        break
                                    }
                                } catch { }
                            }
                        }
                        if ($cloudReport.IsRegistered -or $cloudReport.ConfirmedFree) { break }
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

                    $extractedJsons = @(Get-ChildItem -Path $outDir -Filter "*Autopilot*.json" -File -ErrorAction SilentlyContinue)
                    foreach ($jf in $extractedJsons) {
                        try {
                            $jsonRaw = Get-Content -Path $jf.FullName -Raw -ErrorAction Stop
                            if (Resolve-ZtdJsonContent -RawJson $jsonRaw -CloudReport $cloudReport -QueryMode "ACTIVE_ZTD_DIAG") {
                                break
                            }
                        } catch { }
                    }

                    if (-not $cloudReport.IsRegistered -and -not $cloudReport.ConfirmedFree) {
                        $evtxFiles = @(Get-ChildItem -Path $outDir -Filter "*.evtx" -File -ErrorAction SilentlyContinue)
                        foreach ($ef in $evtxFiles) {
                            try {
                                $events = Get-WinEvent -Path $ef.FullName -ErrorAction SilentlyContinue
                                foreach ($ev in $events) {
                                    if (Resolve-AutopilotEventRecord -EventObj $ev -CloudReport $cloudReport -QueryMode "ACTIVE_ZTD_DIAG") {
                                        break
                                    }
                                }
                            } catch { }
                            if ($cloudReport.IsRegistered -or $cloudReport.ConfirmedFree) { break }
                        }
                    }
                }
            } else {
                $cloudReport.RawStatusMessage = "mdmdiagnosticstool.exe nije pronadjen na sistemu."
            }

            if (-not ($cloudReport.IsRegistered -or $cloudReport.ConfirmedFree)) {
                $cloudReport.OnlineChecked = $false
                $cloudReport.QueryMode = "ACTIVE_ZTD_UNCONFIRMED"
                $cloudReport.RawStatusMessage = "ztd.dds.microsoft.com je dostupan, ali Windows ZTD servis nije vratio eksplicitan Event 806/807 odgovor (potrebna OOBE ZTD razmena)"
            }
        } catch {
            $cloudReport.OnlineChecked = $false
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

# Prikaz rezultata Cloud provere
if ($cloudReport.IsRegistered) {
    Write-Host "  [!] REZULTAT IZ OBLAKA: IMA AUTOPILOT (AKTIVNO ZAKLJUCAN)" -ForegroundColor Red
    Write-Host "      * Pripada organizaciji: $($cloudReport.ProfileTenantDomain)" -ForegroundColor Red
    Write-Host "      * Tenant GUID:          $($cloudReport.ProfileTenantId)" -ForegroundColor Red
    if ($cloudReport.ProfileDeviceName) {
        Write-Host "      * Auto-Device Name:     $($cloudReport.ProfileDeviceName)" -ForegroundColor Red
    }
} elseif ($cloudReport.ConfirmedFree) {
    Write-Host "  [*] REZULTAT IZ OBLAKA: NEMA AUTOPILOT (POTVRDJENO EVENT 807)" -ForegroundColor Green
    Write-Host "      * Status odgovora:      $($cloudReport.RawStatusMessage)" -ForegroundColor Green
    if ($cloudReport.ErrorCode) {
        Write-Host "      * Kod Microsoft servera: $($cloudReport.ErrorCode)" -ForegroundColor Green
    }
} else {
    Write-Host "  [!] REZULTAT IZ OBLAKA: NIJE PROVERENO (NEMA EKSPLICITNE EVENT 806/807 POTVRDE)" -ForegroundColor Yellow
    Write-Host "      * Napomena:             $($cloudReport.RawStatusMessage)" -ForegroundColor Yellow
}

# ==============================================================================
# 4. DETERMINISTICKI BINARNI MODEL PRESUDE (IMP-114)
# ==============================================================================
Write-Host "`n================================================================================" -ForegroundColor Cyan
Write-Host "                       KONACNA PRESUDA I PREPORUKA                              " -ForegroundColor Cyan
Write-Host "================================================================================" -ForegroundColor Cyan

$localHas     = [bool]$localReport.HasTraces
$cloudChecked = [bool]$cloudReport.OnlineChecked
$cloudHas     = [bool]$cloudReport.IsRegistered
$cloudFree    = ([bool]$cloudReport.ConfirmedFree) -and (-not $cloudHas)
$netReachable = [bool]$cloudReport.NetworkReachable

$matrixCode    = "UNKNOWN"
$statusLevel   = "WARNING"
$binaryVerdict = "NIJE_PROVERENO"
$statusTitle   = "NIJE PROVERENO"
$statusDesc    = "Status provere nije odredjen."
$recAction     = "Obezbedite mrezu u OOBE fazi i ponovite proveru."

if ($cloudHas) {
    $binaryVerdict = "IMA_AUTOPILOT"
    $statusLevel   = "CRITICAL"
    if ($localHas) {
        $matrixCode  = "LOCKED_TENANT"
        $statusTitle = "IMA AUTOPILOT - ZAKLJUCAN U ORGANIZACIJI (TENANT LOCK)"
        $statusDesc  = "Uredjaj se aktivno nalazi u Microsoft Autopilot bazi i ima tragove u operativnom sistemu."
        $recAction   = "FORMATIRANJE DISKA NE RESAVA PROBLEM! Cim se sistem ponovo instalira, OOBE ce ga zakljucati. IT administrator mora obrisati hardver iz Intune portala pre prenamene ili prodaje."
        Write-Host "+------------------------------------------------------------------------------+" -ForegroundColor Red
        Write-Host "| [!] IMA AUTOPILOT - AKTIVNO VEZAN ZA KORPORATIVNI TENANT                     |" -ForegroundColor Red
        Write-Host "+------------------------------------------------------------------------------+" -ForegroundColor Red
    } else {
        $matrixCode  = "HIDDEN_LOCK"
        $statusTitle = "IMA AUTOPILOT - SKRIVENA OPASNOST (ZAKLJUCAN U OBLAKU)"
        $statusDesc  = "Lokalni operativni sistem je cist (nema registry tragova), ali je hardverski hash ovog racunara i dalje vezan u Microsoft bazi!"
        $recAction   = "Racunar ne sme ici u dalju prodaju dok ga vlasnik tenanta ne oslobodi iz Microsoft Intune portala."
        Write-Host "+------------------------------------------------------------------------------+" -ForegroundColor Red
        Write-Host "| [!] IMA AUTOPILOT - ZAKLJUCAN U OBLAKU (DISK JE OBRISAN)                     |" -ForegroundColor Red
        Write-Host "+------------------------------------------------------------------------------+" -ForegroundColor Red
    }
} elseif ($cloudChecked -and $cloudFree) {
    $binaryVerdict = "NEMA_AUTOPILOT"
    if ($localHas) {
        $matrixCode  = "RELEASED_LOCAL_TRACES"
        $statusLevel = "WARNING"
        $statusTitle = "NEMA AUTOPILOT - OSLOBODJEN U OBLAKU (STARI LOKALNI TRAGOVI)"
        $statusDesc  = "Uredjaj je u proslosti bio u firmi, ali je USPESNO DEREGISTROVAN iz Microsoft Autopilot baze u oblaku (Event 807)."
        $recAction   = "UREDJAJ JE SLOBODAN ZA PRODAJU I PRENAMENU. Preporucuje se cista reinstalacija Windowsa (Clean install) radi ciscenja lokalnih tragova."
        Write-Host "+------------------------------------------------------------------------------+" -ForegroundColor Yellow
        Write-Host "| [*] NEMA AUTOPILOT - OSLOBODJEN U OBLAKU (STARI LOKALNI TRAGOVI)             |" -ForegroundColor Yellow
        Write-Host "+------------------------------------------------------------------------------+" -ForegroundColor Yellow
    } else {
        $matrixCode  = "CONFIRMED_CLEAN"
        $statusLevel = "OK"
        $statusTitle = "NEMA AUTOPILOT - UREDJAJ JE POTPUNO SLOBODAN (EVENT 807 POTVRDA)"
        $statusDesc  = "Racunar nema lokalnih tragova korporativnog upravljanja, a Microsoft ZTD servis eksplicitno potvrdjuje (Event 807) da hardver nije registrovan."
        $recAction   = "Racunar je u potpunosti cist i bezbedan za rad bilo kog novog korisnika."
        Write-Host "+------------------------------------------------------------------------------+" -ForegroundColor Green
        Write-Host "| [OK] NEMA AUTOPILOT - UREDJAJ JE POTPUNO SLOBODAN (EVENT 807)                |" -ForegroundColor Green
        Write-Host "+------------------------------------------------------------------------------+" -ForegroundColor Green
    }
} elseif ($cloudChecked -or $netReachable) {
    $binaryVerdict = "NIJE_PROVERENO"
    $statusLevel   = "WARNING"
    if ($localHas) {
        $matrixCode  = "UNVERIFIED_LOCAL_TRACES"
        $statusTitle = "NIJE PROVERENO - DETEKTOVANI LOKALNI TRAGOVI (POTREBNA MREZA U OOBE-U)"
        $statusDesc  = "Pronadjeni su lokalni tragovi ($($localReport.TenantDomain)), a Microsoft ZTD oblak nije vratio eksplicitnu Event 806/807 potvrdu."
        $recAction   = "NE SMATRATI UREDJAJ CISTIM! Obezbedite stabilnu mrezu u OOBE fazi pre predaje kupcu."
    } else {
        $matrixCode  = "UNVERIFIED_CLEAN"
        $statusTitle = "NIJE PROVERENO - STATUS U OBLAKU NIJE POTVRDJEN (POTREBNA MREZA U OOBE-U)"
        $statusDesc  = "Lokalni sistem nema vidljivih tragova, ali Microsoft ZTD oblak nije vratio eksplicitnu Event 807 potvrdu."
        $recAction   = "Status u oblaku NIJE potvrdjen - uredjaj se ne sme proglasiti slobodnim bez realne OOBE ZTD provere sa aktivnom mrezom."
    }
    Write-Host "+------------------------------------------------------------------------------+" -ForegroundColor Yellow
    Write-Host "| [!] NIJE PROVERENO (POTREBNA MREZA U OOBE-U)                                 |" -ForegroundColor Yellow
    Write-Host "+------------------------------------------------------------------------------+" -ForegroundColor Yellow
} else {
    $binaryVerdict = "NIJE_PROVERENO"
    $statusLevel   = "WARNING"
    if ($localHas) {
        $matrixCode  = "OFFLINE_LOCAL_TRACES"
        $statusTitle = "NIJE PROVERENO - OFFLINE SA LOKALNIM TRAGOVIMA (POTREBNA MREZA U OOBE-U)"
        $statusDesc  = "Pronadjeni su tragovi Autopilota u registru ($($localReport.TenantDomain)). Zbog nedostatka internet konekcije, status u oblaku nije proveren."
        $recAction   = "Povezite racunar na internet i ponovite OOBE ZTD proveru."
    } else {
        $matrixCode  = "OFFLINE_CLEAN_LOCAL"
        $statusTitle = "NIJE PROVERENO - OFFLINE REZIM (POTREBNA MREZA U OOBE-U)"
        $statusDesc  = "Lokalni sistem nema tragova, ali je racunar bio OFFLINE. Nakon brisanja diska status u Microsoft ZTD oblaku ostaje nepoznat."
        $recAction   = "Povezite racunar na mrezu u OOBE fazi i ponovite proveru."
    }
    Write-Host "+------------------------------------------------------------------------------+" -ForegroundColor Yellow
    Write-Host "| [!] NIJE PROVERENO - OFFLINE REZIM (POTREBNA MREZA U OOBE-U)                 |" -ForegroundColor Yellow
    Write-Host "+------------------------------------------------------------------------------+" -ForegroundColor Yellow
}

$resolvedTenantDomain = if ($cloudReport.ProfileTenantDomain) { "$($cloudReport.ProfileTenantDomain)" } elseif ($localReport.TenantDomain) { "$($localReport.TenantDomain)" } else { "" }
$resolvedTenantId     = if ($cloudReport.ProfileTenantId) { "$($cloudReport.ProfileTenantId)" } elseif ($localReport.TenantId) { "$($localReport.TenantId)" } else { "" }
$resolvedDeviceName   = if ($cloudReport.ProfileDeviceName) { "$($cloudReport.ProfileDeviceName)" } elseif ($localReport.AssignedDeviceName) { "$($localReport.AssignedDeviceName)" } elseif ($localReport.MatchedWaadName) { "$($localReport.MatchedWaadName)" } else { "" }
$isLockedFinal        = ($matrixCode -eq "LOCKED_TENANT" -or $matrixCode -eq "HIDDEN_LOCK")
$confirmedFreeFinal   = ($cloudFree -and (-not $isLockedFinal))

$outputPayload = [PSCustomObject]@{
    timestamp            = (Get-Date).ToString("yyyy-MM-ddTHH:mm:ss")
    matrix_code          = $matrixCode
    status_level         = $statusLevel
    binary_verdict       = $binaryVerdict
    status_title         = $statusTitle
    status_description   = $statusDesc
    recommended_action   = $recAction
    is_locked            = $isLockedFinal
    confirmed_free       = $confirmedFreeFinal
    has_local_traces     = $localHas
    tenant_domain        = $resolvedTenantDomain
    tenant_id            = $resolvedTenantId
    assigned_device_name = $resolvedDeviceName
    query_mode           = "$($cloudReport.QueryMode)"
    source               = "provera_autopilota"
    device_serial        = "$($hwInfo.SerialNumber)"
    device_model         = "$($hwInfo.Model)"
    hardware             = [PSCustomObject]@{
        manufacturer  = "$($hwInfo.Manufacturer)"
        model         = "$($hwInfo.Model)"
        serial_number = "$($hwInfo.SerialNumber)"
        smbios_uuid   = "$($hwInfo.SmbiosUuid)"
        tpm_present   = [bool]$hwInfo.TpmPresent
        tpm_ready     = [bool]$hwInfo.TpmReady
    }
    local_forensics      = [PSCustomObject]@{
        has_traces                        = $localHas
        tenant_domain                     = if ($localReport.TenantDomain) { "$($localReport.TenantDomain)" } else { "" }
        tenant_id                         = if ($localReport.TenantId) { "$($localReport.TenantId)" } else { "" }
        assigned_device_name              = if ($localReport.AssignedDeviceName) { "$($localReport.AssignedDeviceName)" } else { "" }
        policy_cache_found                = [bool]$localReport.PolicyCacheFound
        established_corr                  = [bool]$localReport.EstablishedCorr
        autopilot_settings_found          = [bool]$localReport.AutopilotSettingsFound
        enterprise_assigned_access_found  = [bool]$localReport.EnterpriseAssignedAccessFound
        enrollment_status_tracking_found  = [bool]$localReport.EnrollmentStatusTrackingFound
        waad_device_name_matched          = [bool]$localReport.WaadDeviceNameMatched
        matched_waad_name                 = if ($localReport.MatchedWaadName) { "$($localReport.MatchedWaadName)" } else { "" }
        azure_ad_joined                   = [bool]$localReport.AzureAdJoined
        mdm_enrolled                      = [bool]$localReport.MdmEnrolled
        org_certificates_count            = [int]($localReport.OrgCertificates.Count)
        device_management_keys            = if ($localHas) { 1 } else { 0 }
        enrollment_keys                   = [int]($localReport.MdmEnrollmentUrls.Count)
        enterprise_work_accounts          = if ($localReport.CloudDomainJoinUser) { 1 } else { 0 }
        azure_ad_status                   = if ($localReport.AzureAdJoined) { "PRIDRUZEN (YES)" } else { "Nije pridruzen (NO)" }
        mdm_status                        = if ($localReport.MdmEnrolled) { "UPISAN (YES)" } else { "Nije upisan (NO)" }
        org_certificates                  = @($localReport.OrgCertificates)
        mdm_enrollment_urls               = @($localReport.MdmEnrollmentUrls | Select-Object -Unique)
    }
    cloud_ztd            = [PSCustomObject]@{
        online_checked         = $cloudChecked
        network_reachable      = $netReachable
        is_registered          = $cloudHas
        confirmed_free         = $confirmedFreeFinal
        query_mode             = "$($cloudReport.QueryMode)"
        error_code             = if ($cloudReport.ErrorCode) { "$($cloudReport.ErrorCode)" } else { "" }
        raw_status_message     = "$($cloudReport.RawStatusMessage)"
        profile_tenant_domain  = if ($cloudReport.ProfileTenantDomain) { "$($cloudReport.ProfileTenantDomain)" } else { "" }
        profile_tenant_id      = if ($cloudReport.ProfileTenantId) { "$($cloudReport.ProfileTenantId)" } else { "" }
        profile_device_name    = if ($cloudReport.ProfileDeviceName) { "$($cloudReport.ProfileDeviceName)" } else { "" }
        ztd_registration_state = if ($cloudReport.ZtdRegistrationState) { "$($cloudReport.ZtdRegistrationState)" } else { "" }
    }
    ztd_inspection       = [PSCustomObject]@{
        status      = if ($cloudHas) { "ONLINE_MATCH" } elseif ($confirmedFreeFinal) { "CONFIRMED_FREE" } elseif ($netReachable) { "UNVERIFIED" } else { "OFFLINE" }
        checked     = $cloudChecked
        http_status = if ($cloudChecked) { 200 } else { 0 }
    }
}

$prettyJsonStr = $outputPayload | ConvertTo-Json -Depth 6
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
$newIsConfirmed = $isLockedFinal -or $confirmedFreeFinal
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
    } catch { }
}

# Rezervna kopija ZTD fajla sa primarnih putanja (wmansvc, Provisioning\Autopilot) pa sekundarnih
$ztdPreOobeDest = "C:\Windows\Logs\WIS\AutopilotDDSZTDFile.preoobe.json"
foreach ($cand in $ztdCandidates) {
    if ($cand.Path -ieq $ztdPreOobeDest) { continue }
    if (Test-Path $cand.Path) {
        try {
            $ztdBackupDir = "C:\Windows\Logs\WIS"
            if (-not (Test-Path $ztdBackupDir)) { New-Item -Path $ztdBackupDir -ItemType Directory -Force -ErrorAction SilentlyContinue | Out-Null }
            Copy-Item -Path $cand.Path -Destination $ztdPreOobeDest -Force -ErrorAction SilentlyContinue
            break
        } catch { }
    }
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
