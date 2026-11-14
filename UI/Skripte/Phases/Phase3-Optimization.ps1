<#
.SCRIPT
    Phase3-Optimization.ps1
.DESCRIPTION
    WIS v2.15.0 (Windows Installation Assistant) - Faza 3: Sistemska Optimizacija, Debloat, Brending i VC++ (Vitki Orkestrator).
.COMPATIBILITY
    Windows PowerShell 5.1+
#>

[CmdletBinding()]
param(
    [string]$LocalSetupDir = "C:\setup",
    [string]$Brand = ""
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

# 1. Uvoz deljenog modula i namenskog modula za brending i optimizaciju
$commonMod = @(
    (Join-Path $PSScriptRoot "..\Modules\WIS.Common.psm1"),
    (Join-Path $LocalSetupDir "Skripte\Modules\WIS.Common.psm1"),
    "C:\setup\Skripte\Modules\WIS.Common.psm1"
) | Where-Object { Test-Path $_ } | Select-Object -First 1

if ($commonMod) { Import-Module $commonMod -Force }

$stepGuardMod = @(
    (Join-Path $PSScriptRoot "..\Modules\WIS.StepGuard.psm1"),
    (Join-Path $LocalSetupDir "Skripte\Modules\WIS.StepGuard.psm1"),
    "C:\setup\Skripte\Modules\WIS.StepGuard.psm1"
) | Where-Object { Test-Path $_ } | Select-Object -First 1

if ($stepGuardMod) { Import-Module $stepGuardMod -Force }

$brandMod = @(
    (Join-Path $PSScriptRoot "..\Modules\WIS.Branding.psm1"),
    (Join-Path $LocalSetupDir "Skripte\Modules\WIS.Branding.psm1"),
    "C:\setup\Skripte\Modules\WIS.Branding.psm1"
) | Where-Object { Test-Path $_ } | Select-Object -First 1

if ($brandMod) { Import-Module $brandMod -Force }

# 2. Verifikacija administratorskih privilegija
try {
    Assert-ServisAdministrator
} catch {
    $adminErrMsg = "KRITICNA GRESKA: Faza 3 zahteva administratorske privilegije (Run as Administrator)!"
    if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) { Write-ServisLog $adminErrMsg -Level "ERROR" -Component "Security" }
    if (Get-Command Send-IpcLog -ErrorAction SilentlyContinue) { Send-IpcLog -Message $adminErrMsg -Level "ERROR" -Tag "[ERROR]" -Phase 3 }
    exit 1
}

# 3. Glavno orkestrirano izvrsenje Faze 3
try {
    $Global:CurrentPhase = 3
    if (-not $Global:State) { $Global:State = @{} }
    $Global:State.Phase = 3
    Send-IpcProgress -Progress 5 -Phase 3 -PhaseName "Optimizacija" -SubPhase "Inicijalizacija" -StatusMessage "Zapocinjem sistemske optimizacije..."
    Invoke-Phase3Optimization -LocalSetupDir $LocalSetupDir -Brand $Brand
    exit 0
} catch {
    $err = $_
    $errMsg = "Neocekivana sistemska greska u Fazi 3 (Optimizacija): $($err.Exception.Message)"
    if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) { Write-ServisLog $errMsg -Level "ERROR" -Component "Phase3" }
    if (Get-Command Send-IpcLog -ErrorAction SilentlyContinue) { Send-IpcLog -Message $errMsg -Level "ERROR" -Tag "[ERROR]" -Phase 3 }
    exit 1
}
