<#
.SCRIPT
    Phase4-Apps.ps1
.DESCRIPTION
    WIS v2.15.0 (Windows Installation Assistant) - Faza 4: Korisnicki Programi, Precice i OEM Licenca (Vitki Orkestrator).
.COMPATIBILITY
    Windows PowerShell 5.1+
#>

[CmdletBinding()]
param(
    [string]$LocalSetupDir = "C:\setup",
    [string]$ProgramiDir = "C:\setup\Programi",
    [string]$OnlyApp = "",
    [switch]$PreserveCurrentPhase
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

# 0. Globalno onemogucavanje sigurnosnih dijaloga za pokretanje izvrsnih datoteka (Open File - Security Warning)
$env:SEE_MASK_NOZONECHECKS = '1'
try { [Environment]::SetEnvironmentVariable("SEE_MASK_NOZONECHECKS", "1", "Process") } catch {}

# 1. Uvoz deljenog modula i namenskog modula za aplikacije
$commonMod = @(
    (Join-Path $PSScriptRoot "..\Modules\WIS.Common.psm1"),
    (Join-Path $LocalSetupDir "Skripte\Modules\WIS.Common.psm1"),
    "C:\setup\Skripte\Modules\WIS.Common.psm1"
) | Where-Object { Test-Path $_ } | Select-Object -First 1

if ($commonMod) { Import-Module $commonMod -Force -DisableNameChecking }

$stepGuardMod = @(
    (Join-Path $PSScriptRoot "..\Modules\WIS.StepGuard.psm1"),
    (Join-Path $LocalSetupDir "Skripte\Modules\WIS.StepGuard.psm1"),
    "C:\setup\Skripte\Modules\WIS.StepGuard.psm1"
) | Where-Object { Test-Path $_ } | Select-Object -First 1

if ($stepGuardMod) { Import-Module $stepGuardMod -Force -DisableNameChecking }

$coreMod = @(
    (Join-Path $PSScriptRoot "..\Modules\WIS.Core.psm1"),
    (Join-Path $LocalSetupDir "Skripte\Modules\WIS.Core.psm1"),
    "C:\setup\Skripte\Modules\WIS.Core.psm1"
) | Where-Object { Test-Path $_ } | Select-Object -First 1

if ($coreMod) { Import-Module $coreMod -Force -DisableNameChecking -ErrorAction SilentlyContinue }

$appsMod = @(
    (Join-Path $PSScriptRoot "..\Modules\WIS.Apps.psm1"),
    (Join-Path $LocalSetupDir "Skripte\Modules\WIS.Apps.psm1"),
    "C:\setup\Skripte\Modules\WIS.Apps.psm1"
) | Where-Object { Test-Path $_ } | Select-Object -First 1

if ($appsMod) { Import-Module $appsMod -Force -DisableNameChecking }

# 2. Verifikacija administratorskih privilegija
try {
    Assert-ServisAdministrator
} catch {
    $adminErrMsg = "KRITICNA GRESKA: Faza 4 zahteva administratorske privilegije (Run as Administrator)!"
    if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) { Write-ServisLog $adminErrMsg -Level "ERROR" -Component "Security" }
    if (Get-Command Send-IpcLog -ErrorAction SilentlyContinue) {
        if ($PreserveCurrentPhase -or (-not [string]::IsNullOrWhiteSpace($OnlyApp))) {
            Send-IpcLog -Message $adminErrMsg -Level "ERROR" -Tag "[ERROR]" -SkipFileLog
        } else {
            Send-IpcLog -Message $adminErrMsg -Level "ERROR" -Tag "[ERROR]" -Phase 4
        }
    }
    exit 1
}

# 3. Glavno orkestrirano izvrsenje Faze 4 (uz Phase4Active flag za koordinaciju sa pozadinskim WU)
$phase4ExitCode = 0
$isSingleAppRetry = -not [string]::IsNullOrWhiteSpace($OnlyApp)
try {
    try {
        if (-not (Test-Path "HKLM:\SOFTWARE\WIS")) {
            New-Item -Path "HKLM:\SOFTWARE\WIS" -Force -ErrorAction SilentlyContinue | Out-Null
        }
        Set-ItemProperty -Path "HKLM:\SOFTWARE\WIS" -Name "Phase4Active" -Value 1 -Type DWord -Force -ErrorAction SilentlyContinue
    } catch {}
    $resolvedProgramiRoot = if (Get-Command Get-ServisProgramiRoot -ErrorAction SilentlyContinue) {
        Get-ServisProgramiRoot -LocalSetupDir $LocalSetupDir -PreferredDir $ProgramiDir
    } else {
        $ProgramiDir
    }
    if ($isSingleAppRetry) {
        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
            Write-ServisLog "Pokrećem pojedinačni Retry za program: $OnlyApp" -Level "STEP" -Component "Phase4"
        }
        Invoke-Phase4Apps -LocalSetupDir $LocalSetupDir -ProgramiDir $resolvedProgramiRoot -OnlyApp $OnlyApp -PreserveCurrentPhase:$PreserveCurrentPhase
    } else {
        if (Get-Command Start-WisPhaseBenchmark -ErrorAction SilentlyContinue) {
            Start-WisPhaseBenchmark -Phase 4 -PhaseName "Programi" -LocalSetupDir $LocalSetupDir | Out-Null
        }
        if (-not $PreserveCurrentPhase) {
            Send-IpcProgress -Progress 5 -Phase 4 -PhaseName "Programi" -SubPhase "Inicijalizacija" -StatusMessage "Priprema skladišta programa..."
        }
        Invoke-Phase4Apps -LocalSetupDir $LocalSetupDir -ProgramiDir $resolvedProgramiRoot -PreserveCurrentPhase:$PreserveCurrentPhase
        if (Get-Command Complete-WisPhaseBenchmark -ErrorAction SilentlyContinue) {
            Complete-WisPhaseBenchmark -Phase 4 -Status "COMPLETED" -LocalSetupDir $LocalSetupDir | Out-Null
        }
    }
} catch {
    $err = $_
    $phase4ExitCode = 1
    $errMsg = "Neocekivana sistemska greska u Fazi 4 (Programi): $($err.Exception.Message)"
    if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) { Write-ServisLog $errMsg -Level "ERROR" -Component "Phase4" }
    if (Get-Command Send-IpcLog -ErrorAction SilentlyContinue) {
        if ($isSingleAppRetry -or $PreserveCurrentPhase) {
            Send-IpcLog -Message $errMsg -Level "ERROR" -Tag "[ERROR]" -SkipFileLog
        } else {
            Send-IpcLog -Message $errMsg -Level "ERROR" -Tag "[ERROR]" -Phase 4
        }
    }
} finally {
    try {
        Set-ItemProperty -Path "HKLM:\SOFTWARE\WIS" -Name "Phase4Active" -Value 0 -Type DWord -Force -ErrorAction SilentlyContinue
    } catch {}
}
exit $phase4ExitCode
