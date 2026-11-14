<#
.SCRIPT
    Phase2-Drivers.ps1
.DESCRIPTION
    WIS v2.15.0 (Windows Installation Assistant) - Faza 2: Drajveri i Hardverska Podrska (Vitki Orkestrator).
.COMPATIBILITY
    Windows PowerShell 5.1+
#>

[CmdletBinding()]
param(
    [string]$LocalSetupDir = "C:\setup",
    [switch]$SkipWindowsUpdate
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

# 1. Uvoz deljenog modula i namenskog modula za drajvere/azuriranja
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

$hwMod = @(
    (Join-Path $PSScriptRoot "..\Modules\WIS.Hardware.psm1"),
    (Join-Path $LocalSetupDir "Skripte\Modules\WIS.Hardware.psm1"),
    "C:\setup\Skripte\Modules\WIS.Hardware.psm1"
) | Where-Object { Test-Path $_ } | Select-Object -First 1

if ($hwMod) { Import-Module $hwMod -Force }

$updMod = @(
    (Join-Path $PSScriptRoot "..\Modules\WIS.Update.psm1"),
    (Join-Path $LocalSetupDir "Skripte\Modules\WIS.Update.psm1"),
    "C:\setup\Skripte\Modules\WIS.Update.psm1"
) | Where-Object { Test-Path $_ } | Select-Object -First 1

if ($updMod) { Import-Module $updMod -Force }

# 2. Verifikacija administratorskih privilegija
try {
    Assert-ServisAdministrator
} catch {
    $adminErrMsg = "KRITICNA GRESKA: Faza 2 zahteva administratorske privilegije (Run as Administrator)!"
    if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) { Write-ServisLog $adminErrMsg -Level "ERROR" -Component "Security" }
    if (Get-Command Send-IpcLog -ErrorAction SilentlyContinue) { Send-IpcLog -Message $adminErrMsg -Level "ERROR" -Tag "[ERROR]" -Phase 2 }
    exit 1
}

# 3. Glavno orkestrirano izvrsenje Faze 2 (P2.Diag unutar Invoke-Phase2Drivers pokrece Resolve-MissingDriversFromHub nakon NAS/VMD/PnP)
try {
    Send-IpcProgress -Progress 5 -Phase 2 -PhaseName "Drajveri" -SubPhase "Inicijalizacija" -StatusMessage "Skeniranje hardverskih komponenti i drajvera..."
    Invoke-Phase2Drivers -LocalSetupDir $LocalSetupDir -SkipWindowsUpdate:$SkipWindowsUpdate
    exit 0
} catch {
    $err = $_
    $errMsg = "Neocekivana sistemska greska u Fazi 2 (Drajveri): $($err.Exception.Message)"
    if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) { Write-ServisLog $errMsg -Level "ERROR" -Component "Phase2" }
    if (Get-Command Send-IpcLog -ErrorAction SilentlyContinue) { Send-IpcLog -Message $errMsg -Level "ERROR" -Tag "[ERROR]" -Phase 2 }
    exit 1
}
