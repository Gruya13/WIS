<#
.SCRIPT
    Phase1-Network.ps1
.DESCRIPTION
    WIS v2.15.0 (Windows Installation Assistant) - Faza 1: Mreza i Servisni Wi-Fi (Vitki Orkestrator).
.COMPATIBILITY
    Windows PowerShell 5.1+
#>

[CmdletBinding()]
param(
    [string]$LocalSetupDir = "C:\setup",
    [string]$HubUrl = ""
)

$ErrorActionPreference = 'Stop'

trap {
    $err = $_
    if (Get-Command Send-IpcLog -ErrorAction SilentlyContinue) {
        Send-IpcLog -Message "FATALNA GREŠKA FAZE: $($err.Exception.Message)" -Level "FATAL" -Tag "[FATAL]"
    }
    if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
        Write-ServisLog "Fatalna greška faze: $($err.Exception.Message)" -Level "ERROR" -Component "Phase"
    }
    exit 1
}

# 1. Uvoz deljenog modula i namenskog mreznog modula
$commonMod = @(
    (Join-Path $PSScriptRoot "..\Modules\WIS.Common.psm1"),
    (Join-Path $LocalSetupDir "Skripte\Modules\WIS.Common.psm1"),
    "C:\setup\Skripte\Modules\WIS.Common.psm1"
) | Where-Object { Test-Path $_ } | Select-Object -First 1

if ($commonMod) { Import-Module $commonMod -Force }

$netMod = @(
    (Join-Path $PSScriptRoot "..\Modules\WIS.Network.psm1"),
    (Join-Path $LocalSetupDir "Skripte\Modules\WIS.Network.psm1"),
    "C:\setup\Skripte\Modules\WIS.Network.psm1"
) | Where-Object { Test-Path $_ } | Select-Object -First 1

if ($netMod) { Import-Module $netMod -Force }

$telMod = @(
    (Join-Path $PSScriptRoot "..\Modules\WIS.Telemetry.psm1"),
    (Join-Path $LocalSetupDir "Skripte\Modules\WIS.Telemetry.psm1"),
    "C:\setup\Skripte\Modules\WIS.Telemetry.psm1"
) | Where-Object { Test-Path $_ } | Select-Object -First 1

if ($telMod) { Import-Module $telMod -Force -ErrorAction SilentlyContinue }

# 2. Verifikacija administratorskih privilegija (Tolerantni mod za Fazu 1)
$isAdmin = $false
if (Get-Command Test-ServisAdministrator -ErrorAction SilentlyContinue) {
    $isAdmin = Test-ServisAdministrator
}
if (-not $isAdmin) {
    $adminWarnMsg = "UPOZORENJE: Faza 1 pokrenuta bez punih administratorskih privilegija. Mrežne provere i discovery se nastavljaju u standardnom režimu."
    if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) { Write-ServisLog $adminWarnMsg -Level "WARN" -Component "Security" }
    if (Get-Command Send-IpcLog -ErrorAction SilentlyContinue) { Send-IpcLog -Message $adminWarnMsg -Level "WARN" -Tag "[WARN]" -Phase 1 }
}

# 2b. Instant Fast-Pass provera: ako je mrežni adapter već aktivan i konfigurisan, izvrši laganu ServisHub inicijalizaciju i vrati exit 0
try {
    $activeAdapters = Get-NetAdapter -ErrorAction SilentlyContinue | Where-Object { $_.Status -eq 'Up' }
    $validIp = Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue | Where-Object {
        $_.InterfaceAlias -notmatch 'Loopback|vEthernet' -and
        $_.IPAddress -notmatch '^(127\.|169\.254\.)'
    } | Select-Object -First 1

    if ($activeAdapters -and $validIp) {
        $hasNet = $false
        if (Get-Command Test-Internet -ErrorAction SilentlyContinue) {
            $hasNet = Test-Internet -TimeoutMs 500
        } else {
            $hasNet = [bool](Test-Connection -ComputerName 8.8.8.8 -Count 1 -Quiet -ErrorAction SilentlyContinue)
        }

        if ($hasNet) {
            $adapterDesc = $activeAdapters[0].InterfaceDescription
            $ipAddr = $validIp.IPAddress

            if (Get-Command Save-DeployStatePhase -ErrorAction SilentlyContinue) {
                Save-DeployStatePhase -Phase 2
            }

            $fastHubInfo = $null
            if (Get-Command Initialize-ServisHubFastPass -ErrorAction SilentlyContinue) {
                $fastHubInfo = Initialize-ServisHubFastPass `
                    -LocalSetupDir $LocalSetupDir `
                    -HubUrl $HubUrl `
                    -ClientIp $ipAddr `
                    -ConnectionType $adapterDesc `
                    -InternetActive ([bool]$hasNet)
            }

            $fastMsg = if ($fastHubInfo -and $fastHubInfo.StatusMessage) {
                $fastHubInfo.StatusMessage
            } else {
                "Mreža je već aktivna (Adapter: $adapterDesc, IP: $ipAddr). Instant Fast-Pass završen."
            }

            if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) { Write-ServisLog $fastMsg -Level "SUCCESS" -Component "Phase1" }
            if (Get-Command Send-IpcProgress -ErrorAction SilentlyContinue) { Send-IpcProgress -Progress 100 -Phase 1 -PhaseName "Mreža" -SubPhase "Završeno" -StatusMessage $fastMsg }
            if (-not $fastHubInfo -and (Get-Command Send-IpcStatus -ErrorAction SilentlyContinue)) {
                Send-IpcStatus -Phase 1 -Progress 100 -SubPhase "Završeno" -StatusMessage $fastMsg -Network @{
                    ip = $ipAddr; client_ip = $ipAddr; internet_active = [bool]$hasNet; hub_url = $HubUrl; hub_reachable = $false; network_fast_pass = $true; connection_type = $adapterDesc
                }
            }
            $global:LASTEXITCODE = 0
            exit 0
        }
    }
} catch {}

# 3. Glavno orkestrirano izvrsenje Faze 1
try {
    Send-IpcProgress -Progress 5 -Phase 1 -PhaseName "Mreža" -SubPhase "Inicijalizacija" -StatusMessage "Pokretanje mrežnog podsistema..."
    $success = Invoke-Phase1Network -LocalSetupDir $LocalSetupDir -HubUrl $HubUrl
    if ($success) {
        exit 0
    } else {
        exit 1
    }
} catch {
    $err = $_
    $errMsg = "Prekid Faze 1 (Mreža): $($err.Exception.Message)"
    if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) { Write-ServisLog $errMsg -Level "ERROR" -Component "Phase1" }
    if (Get-Command Send-IpcLog -ErrorAction SilentlyContinue) { Send-IpcLog -Message $errMsg -Level "ERROR" -Tag "[ERROR]" -Phase 1 }
    exit 1
}
