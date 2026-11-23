<#
.SCRIPT
    Phase6-Finalize.ps1
.DESCRIPTION
    WIS v2.15.0 (Windows Installation Assistant) - Faza 6: Zavrsetak, Flotno Izvestavanje, Anti-Leak Ciscenje i UAC (Vitki Orkestrator).
.COMPATIBILITY
    Windows PowerShell 5.1+
#>

[CmdletBinding()]
param(
    [string]$LocalSetupDir = "C:\setup",
    [string]$SystemLogDir = "C:\Windows\Logs\WIS",
    [string]$HubUrl = "",
    [switch]$NoReboot
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot '..\Modules\WIS.StatusContract.psm1') -Force

trap {
    $err = $_
    if (Get-Command Send-IpcLog -ErrorAction SilentlyContinue) {
        Send-IpcLog -Message "FATALNA GREŠKA FAZE: $($err.Exception.Message)" -Level "FATAL" -Tag "[FATAL]" -Phase 6
    }
    if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
        Write-ServisLog "Fatalna greška faze: $($err.Exception.Message)" -Level "ERROR" -Component "Phase6"
    }
    exit 1
}

# 1. Uvoz deljenog modula i namenskih modula za ciscenje, telemetriju i azuriranje (iskljucivo WIS.*)
$commonMod = @(
    (Join-Path $PSScriptRoot "..\Modules\WIS.Common.psm1"),
    (Join-Path $LocalSetupDir "Skripte\Modules\WIS.Common.psm1"),
    "C:\setup\Skripte\Modules\WIS.Common.psm1"
) | Where-Object { Test-Path $_ } | Select-Object -First 1

if ($commonMod) { Import-Module $commonMod -Force }

$cleanMod = @(
    (Join-Path $PSScriptRoot "..\Modules\WIS.Cleanup.psm1"),
    (Join-Path $LocalSetupDir "Skripte\Modules\WIS.Cleanup.psm1"),
    "C:\setup\Skripte\Modules\WIS.Cleanup.psm1"
) | Where-Object { Test-Path $_ } | Select-Object -First 1

if ($cleanMod) { Import-Module $cleanMod -Force }

$telMod = @(
    (Join-Path $PSScriptRoot "..\Modules\WIS.Telemetry.psm1"),
    (Join-Path $LocalSetupDir "Skripte\Modules\WIS.Telemetry.psm1"),
    "C:\setup\Skripte\Modules\WIS.Telemetry.psm1"
) | Where-Object { Test-Path $_ } | Select-Object -First 1

if ($telMod) { Import-Module $telMod -Force -ErrorAction SilentlyContinue }

$updateMod = @(
    (Join-Path $PSScriptRoot "..\Modules\WIS.Update.psm1"),
    (Join-Path $LocalSetupDir "Skripte\Modules\WIS.Update.psm1"),
    "C:\setup\Skripte\Modules\WIS.Update.psm1"
) | Where-Object { Test-Path $_ } | Select-Object -First 1

if ($updateMod) { Import-Module $updateMod -Force -ErrorAction SilentlyContinue }

$hwMod = @(
    (Join-Path $PSScriptRoot "..\Modules\WIS.Hardware.psm1"),
    (Join-Path $LocalSetupDir "Skripte\Modules\WIS.Hardware.psm1"),
    "C:\setup\Skripte\Modules\WIS.Hardware.psm1"
) | Where-Object { Test-Path $_ } | Select-Object -First 1

if ($hwMod) { Import-Module $hwMod -Force -ErrorAction SilentlyContinue }


# 2. Verifikacija administratorskih privilegija
try {
    Assert-ServisAdministrator
} catch {
    $adminErrMsg = "KRITICNA GRESKA: Faza 6 zahteva administratorske privilegije (Run as Administrator)!"
    if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) { Write-ServisLog $adminErrMsg -Level "ERROR" -Component "Security" }
    if (Get-Command Send-IpcLog -ErrorAction SilentlyContinue) { Send-IpcLog -Message $adminErrMsg -Level "ERROR" -Tag "[ERROR]" -Phase 6 }
    exit 1
}

# 3. Glavno orkestrirano izvršenje Faze 6
# Zadaci Faze 6:
# - DMTF identifikacija šasije (TK-{Chassis}-{SerialNumber})
# - Generisanje servisnog kartona u JSON i Markdown formatu (servisni_karton.json, servisni_karton.md)
# - Slanje završnog izveštaja ServisHub-u: POST /api/devices/<id>/report i lifecycle COMPLETED
# - Anti-Leak brisanje servisnih Wi-Fi profila i lozinki: Clear-ServisWifiSecrets
# - Vraćanje sigurnosnih UAC nivoa na fabričke vrednosti: Restore-ServisUacDefaults
# - Agresivno čišćenje radne površine sa svih lokacija (Start-FinalCleanup) i Refresh-ExplorerShell
# - ZABRANA: Faza 6 NE SME automatski brisati C:\setup i NE SME pokretati shutdown.exe!
# - Postavljanje progresa na 100% i uspešan izlazak sa exit 0
try {
    $Global:CurrentPhase = 6
    if (-not $Global:State) { $Global:State = @{} }
    $Global:State.Phase = 6
    if (Get-Command Start-WisPhaseBenchmark -ErrorAction SilentlyContinue) {
        Start-WisPhaseBenchmark -Phase 6 -PhaseName "Završetak" -LocalSetupDir $LocalSetupDir | Out-Null
    }
    Send-IpcProgress -Progress 5 -Phase 6 -PhaseName "Završetak" -SubPhase "Inicijalizacija" -StatusMessage "Priprema završnih procedura..."

    if (-not (Test-Path $LocalSetupDir)) {
        New-Item -Path $LocalSetupDir -ItemType Directory -Force | Out-Null
    }
    if (-not (Test-Path $SystemLogDir)) {
        New-Item -Path $SystemLogDir -ItemType Directory -Force | Out-Null
    }

    $stateFile = Join-Path $LocalSetupDir "deploy-state.json"
    $stressResultFile = Join-Path $LocalSetupDir "stress-test-result.json"

    # 0. Čitanje postojećeg deploy-state.json i stress-test-result.json pre završnog čišćenja
    $initialStateMap = [ordered]@{}
    if (Test-Path $stateFile) {
        try {
            $rawInitState = Get-Content -Path $stateFile -Raw -Encoding UTF8 -ErrorAction SilentlyContinue
            if ($rawInitState) {
                $parsedInitState = ConvertFrom-Json -InputObject $rawInitState -ErrorAction SilentlyContinue
                if ($parsedInitState) {
                    foreach ($p in $parsedInitState.PSObject.Properties) {
                        $initialStateMap[$p.Name] = $p.Value
                    }
                }
            }
        } catch {}
    }

    $stressResultObj = $null
    $stressResultMissingOrCorrupt = $false
    if (Get-Command Get-ServisStressTestResult -ErrorAction SilentlyContinue) {
        $stressResultObj = Get-ServisStressTestResult -LocalSetupDir $LocalSetupDir
    } elseif (Test-Path $stressResultFile) {
        try {
            $rawStress = Get-Content -Path $stressResultFile -Raw -Encoding UTF8 -ErrorAction Stop
            if (-not [string]::IsNullOrWhiteSpace($rawStress)) {
                $parsedStress = ConvertFrom-Json -InputObject $rawStress -ErrorAction Stop
                if ($parsedStress -and (-not [string]::IsNullOrWhiteSpace("$($parsedStress.status)"))) {
                    $stressResultObj = $parsedStress
                }
            }
        } catch {}
    }
    if ($null -eq $stressResultObj) {
        $stressResultMissingOrCorrupt = $true
    }

    # 3a. Dvoprolazno (Dual-Pass) Windows Update skeniranje i instalacija zaostalih kumulativnih zakrpi
    if (Get-Command Invoke-WindowsUpdateSecondPass -ErrorAction SilentlyContinue) {
        Send-IpcProgress -Progress 8 -Phase 6 -PhaseName "Završetak" -SubPhase "Dual-Pass Update" -StatusMessage "Dvoprolazna provera zaostalih kumulativnih zakrpi..."
        try {
            $null = Invoke-WindowsUpdateSecondPass
        } catch {
            $updErr = $_.Exception.Message
            if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                Write-ServisLog "Upozorenje pri drugom prolazu Windows Update-a: $updErr" -Level "WARN" -Component "Phase6"
            }
        }
    }

    # 3b. Windows Update barijera - čeka da se završe aktivni instalaterski WU procesi ili BITS transferi (max 45s; odmah izlazi ako je wu-secondpass-done.flag prisutan)
    if (Get-Command Wait-WindowsUpdateCompletionBarrier -ErrorAction SilentlyContinue) {
        Send-IpcProgress -Progress 12 -Phase 6 -PhaseName "Završetak" -SubPhase "WU Barijera" -StatusMessage "Čekanje završetka pozadinskih procesa Windows Update-a..."
        try {
            $null = Wait-WindowsUpdateCompletionBarrier -TimeoutSeconds 45
            $Global:WisWuBarrierCompletedInPhase6 = $true
        } catch {
            $wuBarrierErr = $_.Exception.Message
            if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                Write-ServisLog "Upozorenje u Windows Update barijeri: $wuBarrierErr" -Level "WARN" -Component "Phase6"
            }
        }
    }

    # 3c. Završna provera i dopunska rezolucija preostalih drajvera + bezbedna odložena berba (pre gašenja mreže/SMB sesija)
    try {
        if ((Get-Command Test-WisDeviceManagerClean -ErrorAction SilentlyContinue) -and (Get-Command Resolve-MissingDriversFromHub -ErrorAction SilentlyContinue)) {
            $dmP6 = Test-WisDeviceManagerClean
            if ($dmP6 -and -not $dmP6.Clean) {
                if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                    Write-ServisLog "Faza 6: Detektovani preostali problemi sa drajverima ($($dmP6.Reason)) — pokrecem zavrsni Resolve-MissingDriversFromHub..." -Level "INFO" -Component "Phase6"
                }
                $null = Resolve-MissingDriversFromHub
            }
        }
        $harvestReadyP6 = if (Get-Command Test-WisDeviceManagerReadyForHarvest -ErrorAction SilentlyContinue) {
            Test-WisDeviceManagerReadyForHarvest
        } else {
            @{ Ready = $false; Reason = "Test-WisDeviceManagerReadyForHarvest nedostupan" }
        }
        if ($harvestReadyP6.Ready -and (Get-Command Export-ServisHardwareDrivers -ErrorAction SilentlyContinue)) {
            Export-ServisHardwareDrivers
            foreach ($dfFlag in @((Join-Path $LocalSetupDir "driver-harvest-deferred.flag"), "C:\setup\driver-harvest-deferred.flag")) {
                if (Test-Path $dfFlag) { Remove-Item -Path $dfFlag -Force -ErrorAction SilentlyContinue }
            }
        } elseif (-not $harvestReadyP6.Ready -and (Get-Command Write-ServisLog -ErrorAction SilentlyContinue)) {
            Write-ServisLog "Faza 6: Preskacem berbu <Model>.tar jer Device Manager i dalje nije cist ($($harvestReadyP6.Reason))." -Level "WARN" -Component "Phase6"
        }
    } catch {
        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
            Write-ServisLog "Faza 6: Upozorenje pri zavrsnoj proveri/berbi drajvera: $($_.Exception.Message)" -Level "WARN" -Component "Phase6"
        }
    }

    # Raskidanje SMB sesija nakon berbe drajvera; Wi-Fi profili OSTAJU aktivni dok HTTP izvestaj ka ServiceHub-u ne zavrsi (IMP-32)
    try {
        & net.exe use * /delete /y >$null 2>&1
    } catch {}

    # ------------------------------------------------------------------------------
    # UVEK ZAUSTAVI 1 Hz LIVE SCREEN MIRROR RADNIKA (ČAK I U NEEDS_ATTENTION STANJU)
    # ------------------------------------------------------------------------------
    if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
        Write-ServisLog "Zaustavljanje 1 Hz Live Screen Mirror radnika pre zavrsnog ciscenja..." -Level "STEP" -Component "Phase6"
    }

    try {
        $stoppedScreenCapture = $false

        if (Get-Command Stop-ServisScreenCapture -ErrorAction SilentlyContinue) {
            $stoppedScreenCapture = Stop-ServisScreenCapture
        } else {
            $screenWorkerCandidates = @(
                (Join-Path $PSScriptRoot "..\Modules\WIS.ScreenCapture.ps1"),
                (Join-Path $LocalSetupDir "Skripte\Modules\WIS.ScreenCapture.ps1"),
                "C:\setup\Skripte\Modules\WIS.ScreenCapture.ps1"
            )
            $screenWorkerPath = $screenWorkerCandidates | Where-Object { Test-Path $_ -ErrorAction SilentlyContinue } | Select-Object -First 1

            if ($screenWorkerPath) {
                & $screenWorkerPath -Stop | Out-Null
                $stoppedScreenCapture = $true
            }
        }

        $remainingJobs = Get-Job -Name "WIS_ScreenCapture*" -ErrorAction SilentlyContinue
        if ($remainingJobs) {
            $remainingJobs | Stop-Job -ErrorAction SilentlyContinue | Out-Null
            $remainingJobs | Remove-Job -Force -ErrorAction SilentlyContinue | Out-Null
            $stoppedScreenCapture = $true
        }

        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
            Write-ServisLog "1 Hz Live Screen Mirror radnik je uspesno i bezbedno zaustavljen." -Level "SUCCESS" -Component "Phase6"
        }
    } catch {
        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
            Write-ServisLog "Upozorenje pri zaustavljanju Screen Mirror radnika: $($_.Exception.Message)" -Level "WARN" -Component "Phase6"
        }
    }

    # ------------------------------------------------------------------------------
    # INICIJALNA PROVERA ANTI-LEAK ZASTAVICA (SAMO CISCENJE IDE NAKON SLANJA IZVESTAJA U FINALLY BLOKU - IMP-32)
    # ------------------------------------------------------------------------------
    $antiLeakVerified = $true
    $forceAntiLeakFail = ($env:WIS_FORCE_ANTILEAK_FAIL -eq "1")
    if ($initialStateMap.Contains("anti_leak_verified") -and $initialStateMap["anti_leak_verified"] -eq $false) {
        $antiLeakVerified = $false
    }
    if ($forceAntiLeakFail) {
        $antiLeakVerified = $false
    }

    # Evaluacija autoritativnog ugovora stres testa
    $stressStatus      = "UNKNOWN"
    $stressReason      = "MISSING_OR_CORRUPT_STRESS_RESULT"
    $emergencyAbort    = $false
    $sensorAvailable   = $false
    $sensorSource      = "NONE"
    $stressDurationSec = 0
    $maxCpuTemp        = 0.0
    $maxGpuTemp        = 0.0
    $throttlingDet     = $false
    $stressReport      = "NEPOZNATO (Nedostaje ili je nečitljiv stress-test-result.json)"
    $stressTimestamp   = (Get-Date -Format "o")
    $stressRunId       = ""
    $stressHistory     = @()

    if ($initialStateMap.Contains("stress_history") -and $null -ne $initialStateMap["stress_history"]) {
        $stressHistory = @($initialStateMap["stress_history"])
    }

    if (-not $stressResultMissingOrCorrupt -and $null -ne $stressResultObj) {
        $stressStatus      = "$($stressResultObj.status)".ToUpper().Trim()
        $stressReason      = if ($stressResultObj.reason) { "$($stressResultObj.reason)".Trim() } else { "UNKNOWN" }
        $emergencyAbort    = [bool]$stressResultObj.emergency_abort
        $sensorAvailable   = [bool]$stressResultObj.sensor_available
        $sensorSource      = if ($stressResultObj.sensor_source) { "$($stressResultObj.sensor_source)" } else { "NONE" }
        $stressDurationSec = if ($null -ne $stressResultObj.duration_sec) { [int]$stressResultObj.duration_sec } elseif ($null -ne $stressResultObj.duration_seconds) { [int]$stressResultObj.duration_seconds } else { 0 }
        $maxCpuTemp        = if ($null -ne $stressResultObj.max_cpu_temp_c) { [double]$stressResultObj.max_cpu_temp_c } elseif ($null -ne $stressResultObj.max_cpu_temp) { [double]$stressResultObj.max_cpu_temp } else { 0.0 }
        $maxGpuTemp        = if ($null -ne $stressResultObj.max_gpu_temp_c) { [double]$stressResultObj.max_gpu_temp_c } elseif ($null -ne $stressResultObj.max_gpu_temp) { [double]$stressResultObj.max_gpu_temp } else { 0.0 }
        $throttlingDet     = [bool]$stressResultObj.throttling_detected
        $stressReport      = if ($stressResultObj.stress_report) { "$($stressResultObj.stress_report)" } else { "$stressStatus ($stressReason)" }
        $stressTimestamp   = if ($stressResultObj.timestamp) { "$($stressResultObj.timestamp)" } else { (Get-Date -Format "o") }
        $stressRunId       = if ($stressResultObj.run_id) { "$($stressResultObj.run_id)" } else { "" }
        if ($stressHistory.Count -eq 0 -and $null -ne $stressResultObj.stress_history) {
            $stressHistory = @($stressResultObj.stress_history)
        }
    }

    # Provera uslova koji blokiraju predaju računara
    # WIS-TASK-245: Pregrevanje procesora (THERMAL_CRITICAL / emergencyAbort / maxCpuTemp >= 95°C) NE blokira i NE zaustavlja pipeline;
    # instalacija normalno nastavlja do kraja, a termalni nalaz se čuva u svim izveštajima kao napomena za servis hlađenja.
    # Predaja se blokira isključivo u slučaju Anti-Leak bezbednosnog prekršaja (-not $antiLeakVerified).
    $blockHandover = $false
    $blockReasons = [System.Collections.Generic.List[string]]::new()

    $hasThermalOverheat = ($stressStatus -eq "THERMAL_CRITICAL" -or $emergencyAbort -eq $true -or $maxCpuTemp -ge 95.0)
    $thermalNote = if ($hasThermalOverheat) {
        "NAPOMENA: Procesor se pregrevao tokom stres testa (${maxCpuTemp}°C >= 95°C) — potreban servis hlađenja i zamena termalne paste."
    } else {
        ""
    }
    $notesList = [System.Collections.Generic.List[string]]::new()
    if ($thermalNote) {
        $notesList.Add($thermalNote)
    }

    if (-not $antiLeakVerified) {
        $blockHandover = $true
        $blockReasons.Add("Anti-Leak verifikacija čišćenja tajni nije uspela (anti_leak_verified=false)")
    }

    # V343-03 Hard Gate: Provera C:\setup\hwtest-result.json pre proglašenja DONE / COMPLETED
    $hwTestIncomplete = $false
    $unfinishedInteractiveTests = [System.Collections.Generic.List[string]]::new()
    $hwTestPendingNote = ""
    foreach ($hwResCand in @(
        (Join-Path $LocalSetupDir "hwtest-result.json"),
        "C:\setup\hwtest-result.json",
        (Join-Path $SystemLogDir "hwtest-result.json")
    )) {
        if ($hwResCand -and (Test-Path -LiteralPath $hwResCand -ErrorAction SilentlyContinue)) {
            try {
                $rawHwRes = Get-Content -LiteralPath $hwResCand -Raw -Encoding UTF8 -ErrorAction SilentlyContinue
                if (-not [string]::IsNullOrWhiteSpace($rawHwRes)) {
                    $parsedHwRes = ConvertFrom-Json -InputObject $rawHwRes -ErrorAction SilentlyContinue
                    if ($parsedHwRes -and $parsedHwRes.tests) {
                        foreach ($tItem in @($parsedHwRes.tests)) {
                            if ($null -eq $tItem) { continue }
                            $isAutomated = if ($null -ne $tItem.automated) { [bool]$tItem.automated } else { $false }
                            $tStatus = if ($tItem.status) { ("$($tItem.status)").ToUpper().Trim() } else { "PENDING" }
                            if ((-not $isAutomated) -and ($tStatus -in @("SKIP", "PENDING", "RUNNING"))) {
                                $tLabel = if ($tItem.title) { "$($tItem.title)" } elseif ($tItem.id) { "$($tItem.id)" } else { "Nepoznat test" }
                                $unfinishedInteractiveTests.Add("$tLabel ($tStatus)")
                            }
                        }
                    }
                    break
                }
            } catch {}
        }
    }
    if ($unfinishedInteractiveTests.Count -gt 0) {
        $hwTestIncomplete = $true
        $blockHandover = $true
        $hwTestPendingNote = "NAPOMENA: Preskočeni ili nezavršeni interaktivni hardverski testovi ($($unfinishedInteractiveTests.Count)): $($unfinishedInteractiveTests -join ', ')"
        $blockReasons.Add($hwTestPendingNote)
        $notesList.Add($hwTestPendingNote)
        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
            Write-ServisLog "UPOZORENJE (V343-03 Hard Gate): Blokiran status DONE/COMPLETED jer postoje nezavršeni interaktivni testovi u hwtest-result.json -> $($unfinishedInteractiveTests -join ', ')" -Level "WARN" -Component "Phase6"
        }
        if (Get-Command Send-IpcLog -ErrorAction SilentlyContinue) {
            Send-IpcLog -Message $hwTestPendingNote -Level "WARN" -Tag "[HwTest]" -Phase 6
        }
    }

    $isSensorUnavailable = ($stressStatus -eq "UNKNOWN") -or (-not $sensorAvailable) -or ($maxCpuTemp -le 0.0)

    # Učitavanje strukturiranog statusa licenciranja iz C:\setup\license-status.json (ako postoji, bez ključeva)
    $licSummary = $null
    foreach ($licCand in @(
        (Join-Path $LocalSetupDir "license-status.json"),
        "C:\setup\license-status.json",
        (Join-Path $SystemLogDir "license-status.json")
    )) {
        if ($licCand -and (Test-Path -LiteralPath $licCand -ErrorAction SilentlyContinue)) {
            try {
                $rawLic = Get-Content -LiteralPath $licCand -Raw -Encoding UTF8 -ErrorAction SilentlyContinue
                if ($rawLic) {
                    $parsedLic = ConvertFrom-Json -InputObject $rawLic -ErrorAction SilentlyContinue
                    if ($parsedLic) {
                        $rawDet = if ($parsedLic.office_license_detail) { "$($parsedLic.office_license_detail)".Trim() } else { "" }
                        $safeDet = $rawDet -replace '(?i)\b([A-Z0-9]{5}-){4}([A-Z0-9]{5})\b', '*****-*****-*****-*****-$2'
                        $rawWinDet = if ($parsedLic.windows_license_detail) { "$($parsedLic.windows_license_detail)".Trim() } else { "" }
                        $safeWinDet = $rawWinDet -replace '(?i)\b([A-Z0-9]{5}-){4}([A-Z0-9]{5})\b', '*****-*****-*****-*****-$2'
                        $licWin = [bool]$parsedLic.windows_activated
                        $licWinOa3 = if ($null -ne $parsedLic.windows_oa3_present) { [bool]$parsedLic.windows_oa3_present } else { $false }
                        $licWinEd = if ($parsedLic.windows_edition) { "$($parsedLic.windows_edition)".Trim() } else { "" }
                        $licWinMethod = if ($parsedLic.windows_license_method) { "$($parsedLic.windows_license_method)".Trim() } else { "" }
                        $licOffInst = [bool]$parsedLic.office_installed
                        $licOffAct = [bool]$parsedLic.office_activated
                        $licMode = if ($parsedLic.office_activation_mode) { "$($parsedLic.office_activation_mode)".Trim() } else { "" }
                        $licMethod = if ($parsedLic.office_license_method) { "$($parsedLic.office_license_method)".Trim() } else { "" }
                        $licSummary = [ordered]@{
                            windows_activated      = $licWin
                            windows_oa3_present    = $licWinOa3
                            windows_edition        = $licWinEd
                            windows_license_method = $licWinMethod
                            windows_license_detail = $safeWinDet
                            office_installed       = $licOffInst
                            office_activated       = $licOffAct
                            office_activation_mode = $licMode
                            office_license_method  = $licMethod
                            office_license_detail  = $safeDet
                        }
                        break
                    }
                }
            } catch {}
        }
    }

    $notesArr = @($notesList.ToArray())
    $diagnosticsContract = Get-WisDiagnosticsContract -Status $stressStatus -SensorAvailable $sensorAvailable -MaxCpuTemp $maxCpuTemp -EmergencyAbort $emergencyAbort
    $finalTelemetryData = @{
        diagnostics_status = $diagnosticsContract.diagnostics_status
        diagnostics_warning = $diagnosticsContract.diagnostics_warning
        cooling_verified = [bool]$diagnosticsContract.cooling_verified
        manual_check_required = [bool]$diagnosticsContract.manual_check_required
        stress_status = $stressStatus
        sensor_available = [bool]$sensorAvailable
        anti_leak_verified = [bool]$antiLeakVerified
        handover_ready = (-not $blockHandover)
        hwtest_incomplete = [bool]$hwTestIncomplete
    }
    if ($unfinishedInteractiveTests.Count -gt 0) {
        $finalTelemetryData["unfinished_interactive_tests"] = @($unfinishedInteractiveTests.ToArray())
    }
    if ($null -ne $licSummary) {
        $finalTelemetryData["windows_activated"] = $licSummary.windows_activated
        $finalTelemetryData["windows_oa3_present"] = $licSummary.windows_oa3_present
        $finalTelemetryData["windows_edition"] = $licSummary.windows_edition
        $finalTelemetryData["windows_license_method"] = $licSummary.windows_license_method
        $finalTelemetryData["windows_license_detail"] = $licSummary.windows_license_detail
        $finalTelemetryData["office_installed"] = $licSummary.office_installed
        $finalTelemetryData["office_activated"] = $licSummary.office_activated
        $finalTelemetryData["office_activation_mode"] = $licSummary.office_activation_mode
        $finalTelemetryData["office_license_method"] = $licSummary.office_license_method
        $finalTelemetryData["office_license_detail"] = $licSummary.office_license_detail
        $finalTelemetryData["licenses"] = [ordered]@{
            windows                = $licSummary.windows_activated
            office                 = $licSummary.office_activated
            windows_activated      = $licSummary.windows_activated
            windows_oa3_present    = $licSummary.windows_oa3_present
            windows_edition        = $licSummary.windows_edition
            windows_license_method = $licSummary.windows_license_method
            windows_license_detail = $licSummary.windows_license_detail
            office_installed       = $licSummary.office_installed
            office_activated       = $licSummary.office_activated
            office_activation_mode = $licSummary.office_activation_mode
            office_license_method  = $licSummary.office_license_method
            office_license_detail  = $licSummary.office_license_detail
        }
    }
    if ($thermalNote) {
        $finalTelemetryData["thermal_note"] = $thermalNote
    }
    if ($notesArr.Count -gt 0) {
        $finalTelemetryData["notes"] = $notesArr
    }
    $utf8NoBom = New-Object System.Text.UTF8Encoding($false)

    # Helper funkcija za upis autoritativnih podataka stres testa i statusa predaje u servisni_karton.json i servisni_karton.md
    function Update-ServisKartonContract {
        param(
            [string]$FinalStatus,
            [bool]$HandoverReadyFlag,
            [bool]$AntiLeakFlag,
            [string]$StatusMsg
        )
        $diagWarningVal = $diagnosticsContract.diagnostics_warning
        $stressSubMap = [ordered]@{
            status              = $stressStatus
            reason              = $stressReason
            emergency_abort     = [bool]$emergencyAbort
            sensor_available    = [bool]$sensorAvailable
            sensor_source       = $sensorSource
            duration_sec        = [int]$stressDurationSec
            max_cpu_temp_c      = [double]$maxCpuTemp
            max_gpu_temp_c      = [double]$maxGpuTemp
            throttling_detected = [bool]$throttlingDet
            diagnostics_warning = $diagWarningVal
            timestamp           = $stressTimestamp
            stress_report       = $stressReport
        }

        foreach ($targetDir in @($LocalSetupDir, $SystemLogDir)) {
            if (-not $targetDir -or -not (Test-Path $targetDir)) { continue }

            $kJsonPath = Join-Path $targetDir "servisni_karton.json"
            $kMdPath   = Join-Path $targetDir "servisni_karton.md"

            $kMap = [ordered]@{}
            if (Test-Path $kJsonPath) {
                try {
                    $rawK = Get-Content -Path $kJsonPath -Raw -Encoding UTF8 -ErrorAction SilentlyContinue
                    if ($rawK) {
                        $parsedK = ConvertFrom-Json -InputObject $rawK -ErrorAction SilentlyContinue
                        if ($parsedK) {
                            foreach ($prop in $parsedK.PSObject.Properties) {
                                $kMap[$prop.Name] = $prop.Value
                            }
                        }
                    }
                } catch {}
            }
            if (-not $kMap.Contains("device_id")) {
                $comp = if ($env:COMPUTERNAME) { $env:COMPUTERNAME } else { "KLIJENT" }
                $kMap["device_id"] = "TK-$comp"
                $kMap["hostname"]  = $comp
                $kMap["timestamp"] = (Get-Date -Format "o")
            }
            $kMap["status"]              = $FinalStatus
            $kMap["deployment_status"]   = $FinalStatus
            $kMap["handover_ready"]      = $HandoverReadyFlag
            $kMap["anti_leak_verified"]  = $AntiLeakFlag
            $kMap["diagnostics_status"]  = $diagnosticsContract.diagnostics_status
            $kMap["diagnostics_warning"] = $diagWarningVal
            $kMap["cooling_verified"] = $diagnosticsContract.cooling_verified
            $kMap["manual_check_required"] = $diagnosticsContract.manual_check_required
            $kMap["thermal_note"]        = $thermalNote
            $kMap["notes"]               = $notesArr
            $kMap["hwtest_incomplete"]   = [bool]$hwTestIncomplete
            $kMap["stress_test"]         = $stressSubMap
            if ($null -ne $licSummary) {
                $kMap["windows_activated"] = $licSummary.windows_activated
                $kMap["windows_oa3_present"] = $licSummary.windows_oa3_present
                $kMap["windows_edition"] = $licSummary.windows_edition
                $kMap["windows_license_method"] = $licSummary.windows_license_method
                $kMap["windows_license_detail"] = $licSummary.windows_license_detail
                $kMap["office_installed"] = $licSummary.office_installed
                $kMap["office_activated"] = $licSummary.office_activated
                $kMap["office_activation_mode"] = $licSummary.office_activation_mode
                $kMap["office_license_method"] = $licSummary.office_license_method
                $kMap["office_license_detail"] = $licSummary.office_license_detail
                $kMap["licenses"] = [ordered]@{
                    windows                = $licSummary.windows_activated
                    office                 = $licSummary.office_activated
                    windows_activated      = $licSummary.windows_activated
                    windows_oa3_present    = $licSummary.windows_oa3_present
                    windows_edition        = $licSummary.windows_edition
                    windows_license_method = $licSummary.windows_license_method
                    windows_license_detail = $licSummary.windows_license_detail
                    office_installed       = $licSummary.office_installed
                    office_activated       = $licSummary.office_activated
                    office_activation_mode = $licSummary.office_activation_mode
                    office_license_method  = $licSummary.office_license_method
                    office_license_detail  = $licSummary.office_license_detail
                }
            }
            if (Get-Command Get-WisBenchmarkSummary -ErrorAction SilentlyContinue) {
                try {
                    $benchSummary = Get-WisBenchmarkSummary -LocalSetupDir $LocalSetupDir
                    if ($benchSummary) {
                        $kMap["benchmark"] = $benchSummary
                    }
                } catch {}
            }
            $kMap["status_message"]      = $StatusMsg

            try {
                [System.IO.File]::WriteAllText($kJsonPath, (ConvertTo-Json -InputObject $kMap -Depth 6), $utf8NoBom)
            } catch {}

            try {
                $mdExtra = [System.Text.StringBuilder]::new()
                $existingMd = ""
                if (Test-Path $kMdPath) {
                    $existingMd = Get-Content -Path $kMdPath -Raw -Encoding UTF8 -ErrorAction SilentlyContinue
                    if ($existingMd) {
                        # Ako je u postojećem MD kartonu pisalo SERVIS ZAVRŠEN, uskladi sa stvarnim statusom
                        if (-not $HandoverReadyFlag) {
                            $existingMd = $existingMd -replace 'SERVIS ZAVRŠEN! Sistem je verifikovan i spreman za pakovanje\.', $StatusMsg
                            $existingMd = $existingMd -replace 'USPEŠNO - SVI PROGRAMI SU INSTALIRANI', $StatusMsg
                        } elseif ($isSensorUnavailable -or $hasThermalOverheat) {
                            $existingMd = $existingMd -replace 'SERVIS ZAVRŠEN! Sistem je verifikovan i spreman za pakovanje\.', $StatusMsg
                        }
                        [void]$mdExtra.AppendLine($existingMd.TrimEnd())
                    }
                } else {
                    [void]$mdExtra.AppendLine("# [KARTON] WIS SERVICE | SERVISNI KARTON RAČUNARA")
                }
                if ($null -ne $licSummary -and ($existingMd -notmatch '### \[LICENCE\] Status Licenci')) {
                    [void]$mdExtra.AppendLine("")
                    [void]$mdExtra.AppendLine("### [LICENCE] Status Licenci")
                    [void]$mdExtra.AppendLine("- **Windows Licenca:** $(if ($licSummary.windows_activated) { '[OK] Trajno aktiviran' } else { '[UPOZORENJE] Nije aktiviran' })")
                    if ($licSummary.windows_edition) {
                        [void]$mdExtra.AppendLine("- **Windows Edicija:** $($licSummary.windows_edition) (OA3x: $($licSummary.windows_oa3_present))")
                    }
                    if ($licSummary.windows_license_method) {
                        [void]$mdExtra.AppendLine("- **Metod licenciranja Windows-a:** $($licSummary.windows_license_method)")
                    }
                    if ($licSummary.windows_license_detail) {
                        [void]$mdExtra.AppendLine("- **Detalj licenciranja Windows-a:** $($licSummary.windows_license_detail)")
                    }
                    $kOffTxt = if (-not $licSummary.office_installed) { 'Nije instaliran' } elseif ($licSummary.office_activated) { '[OK] Trajno aktiviran' } elseif ($licSummary.office_activation_mode -ieq 'Skip') { 'Preskočeno (OfficeActivationMode=Skip)' } else { '[UPOZORENJE] Nije aktiviran' }
                    [void]$mdExtra.AppendLine("- **Office Licenca:** $kOffTxt")
                    if ($licSummary.office_license_method) {
                        [void]$mdExtra.AppendLine("- **Metod licenciranja Office-a:** $($licSummary.office_license_method)")
                    }
                    if ($licSummary.office_license_detail) {
                        [void]$mdExtra.AppendLine("- **Detalj licenciranja:** $($licSummary.office_license_detail)")
                    }
                }
                [void]$mdExtra.AppendLine("")
                [void]$mdExtra.AppendLine("### [TERMIKA] Termalni Stres Test i Spremnost za Predaju")
                [void]$mdExtra.AppendLine("- **Status predaje (Deployment Status):** $FinalStatus (Handover Ready: $HandoverReadyFlag)")
                [void]$mdExtra.AppendLine("- **Status stres testa:** $stressStatus ($stressReason)")
                [void]$mdExtra.AppendLine("- **Dijagnostika hladjenja:** $($diagnosticsContract.diagnostics_status) (Cooling Verified: $($diagnosticsContract.cooling_verified); Manual Check Required: $($diagnosticsContract.manual_check_required))")
                [void]$mdExtra.AppendLine("- **Emergency Thermal Abort:** $emergencyAbort")
                [void]$mdExtra.AppendLine("- **Senzor dostupan:** $sensorAvailable ($sensorSource)")
                [void]$mdExtra.AppendLine("- **Stvarno trajanje testa:** ${stressDurationSec}s")
                [void]$mdExtra.AppendLine("- **Max CPU / GPU Temp:** ${maxCpuTemp}°C / ${maxGpuTemp}°C")
                [void]$mdExtra.AppendLine("- **Anti-Leak verifikovan:** $AntiLeakFlag")
                if ($notesArr.Count -gt 0) {
                    [void]$mdExtra.AppendLine("")
                    [void]$mdExtra.AppendLine("### [NAPOMENA] Termalne i Servisne Napomene")
                    foreach ($noteLine in $notesArr) {
                        if ($noteLine) {
                            [void]$mdExtra.AppendLine("- **Napomena:** $noteLine")
                        }
                    }
                }
                [void]$mdExtra.AppendLine("- **Zaključak:** $StatusMsg")
                [System.IO.File]::WriteAllText($kMdPath, $mdExtra.ToString(), $utf8NoBom)
            } catch {}
        }
    }

    $clientSecretFiles = @(
        (Join-Path $LocalSetupDir "Wifi-Servis.ini"),
        "C:\setup\Wifi-Servis.ini",
        (Join-Path $LocalSetupDir "wifi_profile.xml"),
        "C:\setup\wifi_profile.xml",
        (Join-Path $LocalSetupDir "Wifi-Servis.xml"),
        "C:\setup\Wifi-Servis.xml",
        "C:\Windows\Temp\Wifi-Servis.ini",
        "C:\Windows\Temp\wifi_profile.xml",
        "C:\Windows\Temp\Wifi-Servis.xml"
    )
    $hardeningOk = $false
    $reportAndCleanupError = $null
    $attnMsg = ""
    $readyMsg = if ($hasThermalOverheat) {
        "INSTALACIJA ZAVRŠENA (NAPOMENA: PREGREVANJE CPU ${maxCpuTemp}°C >= 95°C) | $thermalNote"
    } elseif ($diagnosticsContract.diagnostics_status -eq "SENSOR_UNAVAILABLE") {
        "INSTALACIJA ZAVRŠENA | Hlađenje nije automatski provereno; obavezna ručna provera."
    } else {
        "INSTALACIJA ZAVRŠENA | Dijagnostika hlađenja: $($diagnosticsContract.diagnostics_status). $($diagnosticsContract.diagnostics_warning)"
    }

    # Kritičan redosled (IMP-32): Prvo slanje mrežnog izveštaja ka Hub-u dok je Wi-Fi aktivan,
    # a Clear-ServisWifiSecrets + net use * /delete /y + fizička provera tajni UVEK u finally bloku!
    try {
        if ($blockHandover) {
            # ------------------------------------------------------------------------------
            # GRANA NEEDS_ATTENTION: ZABRANJEN STATUS DONE / READY / SUCCESS I AUTOMATSKO GAŠENJE!
            # ------------------------------------------------------------------------------
            try {
                Remove-ItemProperty -Path "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce" -Name "WIS_Resume" -ErrorAction SilentlyContinue
                Remove-ItemProperty -Path "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce" -Name "ServisDeployResume" -ErrorAction SilentlyContinue
                Remove-ItemProperty -Path "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce" -Name "WIS_AutoStart" -ErrorAction SilentlyContinue

                if (Get-Command Clear-ServisDesktopShortcuts -ErrorAction SilentlyContinue) {
                    Clear-ServisDesktopShortcuts | Out-Null
                }
                if (Get-Command Export-ServisSummaryReport -ErrorAction SilentlyContinue) {
                    $verResults = if (Get-Command Test-AllServisProgramsInstalled -ErrorAction SilentlyContinue) { Test-AllServisProgramsInstalled } else { $null }
                    Export-ServisSummaryReport -VerificationResults $verResults -LocalSetupDir $LocalSetupDir -SystemLogDir $SystemLogDir
                }
                foreach ($cleanupSubDir in @(
                    (Join-Path $LocalSetupDir "Programi"),
                    (Join-Path $LocalSetupDir "Cache\Licensing"),
                    "C:\setup\Cache\Licensing"
                )) {
                    if ($cleanupSubDir -and (Test-Path -LiteralPath $cleanupSubDir -ErrorAction SilentlyContinue)) {
                        Remove-Item -LiteralPath $cleanupSubDir -Recurse -Force -ErrorAction SilentlyContinue
                    }
                }
            } catch {}

            $reasonJoined = ($blockReasons -join " | ")
            $attnMsg = "NEEDS_ATTENTION: Računar NIJE spreman za predaju kupcu! Razlog: $reasonJoined"

            Update-ServisKartonContract -FinalStatus "NEEDS_ATTENTION" -HandoverReadyFlag $false -AntiLeakFlag $antiLeakVerified -StatusMsg $attnMsg

            # Očuvaj stress-test-result.json ukoliko je postojao
            if (-not $stressResultMissingOrCorrupt -and $null -ne $stressResultObj) {
                try {
                    $contractPreserved = [ordered]@{
                        run_id              = $stressRunId
                        status              = $stressStatus
                        reason              = $stressReason
                        emergency_abort     = [bool]$emergencyAbort
                        sensor_available    = [bool]$sensorAvailable
                        sensor_source       = $sensorSource
                        duration_sec        = [int]$stressDurationSec
                        duration_seconds    = [int]$stressDurationSec
                        max_cpu_temp_c      = [double]$maxCpuTemp
                        max_gpu_temp_c      = [double]$maxGpuTemp
                        max_cpu_temp        = [double]$maxCpuTemp
                        max_gpu_temp        = [double]$maxGpuTemp
                        throttling_detected = [bool]$throttlingDet
                        timestamp           = $stressTimestamp
                        stress_report       = $stressReport
                        stress_history      = $stressHistory
                    }
                    [System.IO.File]::WriteAllText($stressResultFile, (ConvertTo-Json -InputObject $contractPreserved -Depth 6), $utf8NoBom)
                    [System.IO.File]::WriteAllText((Join-Path $SystemLogDir "stress-test-result.json"), (ConvertTo-Json -InputObject $contractPreserved -Depth 6), $utf8NoBom)
                } catch {}
            }

            # Upis autoritativnog NEEDS_ATTENTION stanja u C:\setup\deploy-state.json
            $initialStateMap["phase"]               = 6
            $initialStateMap["deployment_status"]   = "NEEDS_ATTENTION"
            $initialStateMap["handover_ready"]      = $false
            $initialStateMap["hwtest_incomplete"]   = [bool]$hwTestIncomplete
            $initialStateMap["notes"]               = $notesArr
            $initialStateMap["anti_leak_verified"]  = [bool]$antiLeakVerified
            $initialStateMap["diagnostics_status"] = $diagnosticsContract.diagnostics_status
            $initialStateMap["diagnostics_warning"] = $diagnosticsContract.diagnostics_warning
            $initialStateMap["cooling_verified"] = $diagnosticsContract.cooling_verified
            $initialStateMap["manual_check_required"] = $diagnosticsContract.manual_check_required
            $initialStateMap["stress_status"]       = $stressStatus
            $initialStateMap["stress_reason"]       = $stressReason
            $initialStateMap["emergency_abort"]     = [bool]$emergencyAbort
            $initialStateMap["sensor_available"]    = [bool]$sensorAvailable
            $initialStateMap["sensor_source"]       = $sensorSource
            $initialStateMap["stress_duration_sec"] = [int]$stressDurationSec
            $initialStateMap["max_cpu_temp"]        = [double]$maxCpuTemp
            $initialStateMap["max_gpu_temp"]        = [double]$maxGpuTemp
            $initialStateMap["max_cpu_temp_c"]      = [double]$maxCpuTemp
            $initialStateMap["max_gpu_temp_c"]      = [double]$maxGpuTemp
            $initialStateMap["throttling_detected"] = [bool]$throttlingDet
            $initialStateMap["stress_report"]       = $stressReport
            $initialStateMap["stress_timestamp"]    = $stressTimestamp
            $initialStateMap["stress_history"]      = $stressHistory
            $initialStateMap["status_message"]      = $attnMsg
            $initialStateMap["updated_at"]          = (Get-Date -Format "o")

            $finalStateJson = ConvertTo-Json -InputObject $initialStateMap -Depth 6
            [System.IO.File]::WriteAllText($stateFile, $finalStateJson, $utf8NoBom)
            try {
                [System.IO.File]::WriteAllText((Join-Path $SystemLogDir "deploy-state.json"), $finalStateJson, $utf8NoBom)
            } catch {}

            # Slanje NEEDS_ATTENTION statusa na ServisHub (pre Clear-ServisWifiSecrets u finally!)
            try {
                if (Get-Command Send-FleetFinalReport -ErrorAction SilentlyContinue) {
                    Send-FleetFinalReport -Status "NEEDS_ATTENTION" -CustomData $finalTelemetryData -LocalSetupDir $LocalSetupDir -SystemLogDir $SystemLogDir | Out-Null
                }
            } catch {}
        } else {
            # ------------------------------------------------------------------------------
            # GRANA READY: Slanje izvestaja dok Wi-Fi radi (u Invoke-Phase6Finalize), pa ciscenje
            # ------------------------------------------------------------------------------
            Update-ServisKartonContract -FinalStatus "DONE" -HandoverReadyFlag $true -AntiLeakFlag $true -StatusMsg $readyMsg

            $global:LASTEXITCODE = 0
            if (Get-Command Invoke-Phase6Finalize -ErrorAction SilentlyContinue) {
                $phase6Res = Invoke-Phase6Finalize -LocalSetupDir $LocalSetupDir -SystemLogDir $SystemLogDir -HubUrl $HubUrl -NoReboot:$true
                if ($phase6Res -eq $false) {
                    $antiLeakVerified = $false
                }
            } elseif (Get-Command Invoke-Phase5Finalize -ErrorAction SilentlyContinue) {
                $phase5Res = Invoke-Phase5Finalize -LocalSetupDir $LocalSetupDir -SystemLogDir $SystemLogDir -HubUrl $HubUrl -NoReboot:$true
                if ($phase5Res -eq $false) {
                    $antiLeakVerified = $false
                }
            } else {
                if (Get-Command Export-ServisSummaryReport -ErrorAction SilentlyContinue) {
                    Export-ServisSummaryReport -LocalSetupDir $LocalSetupDir -SystemLogDir $SystemLogDir
                }
                if (Get-Command Send-FleetFinalReport -ErrorAction SilentlyContinue) {
                    Send-FleetFinalReport -Status "COMPLETED" -CustomData $finalTelemetryData -LocalSetupDir $LocalSetupDir -SystemLogDir $SystemLogDir | Out-Null
                }
                if (Get-Command Start-FinalCleanup -ErrorAction SilentlyContinue) {
                    $sfcRes = Start-FinalCleanup -LocalSetupDir $LocalSetupDir -SystemLogDir $SystemLogDir -InPhase5 -NoReboot
                    if ($sfcRes -eq $false) {
                        $antiLeakVerified = $false
                    }
                }
            }

            if ($global:LASTEXITCODE -ne 0) {
                $antiLeakVerified = $false
            }
            $hardeningOk = ($null -ne $Global:WisSecurityHardeningVerified -and [bool]$Global:WisSecurityHardeningVerified)
        }
    } catch {
        $reportAndCleanupError = $_
    } finally {
        # UVEK NAKON SLANJA IZVEŠTAJA: raskidanje SMB sesija, brisanje Wi-Fi tajni i profila (Clear-ServisWifiSecrets), provera fajlova i security restore
        try {
            & net.exe use * /delete /y >$null 2>&1
        } catch {}

        try {
            if (Get-Command Clear-ServisWifiSecrets -ErrorAction SilentlyContinue) {
                $cleanSecretRes = Clear-ServisWifiSecrets -LocalSetupDir $LocalSetupDir
                if ($cleanSecretRes -is [bool] -and $cleanSecretRes -eq $false) {
                    $antiLeakVerified = $false
                } elseif ($null -ne $cleanSecretRes) {
                    if (($null -ne $cleanSecretRes.Success -and $cleanSecretRes.Success -eq $false) -or
                        ($null -ne $cleanSecretRes.Verified -and $cleanSecretRes.Verified -eq $false) -or
                        ($null -ne $cleanSecretRes.anti_leak_verified -and $cleanSecretRes.anti_leak_verified -eq $false)) {
                        $antiLeakVerified = $false
                    }
                }
            } else {
                & netsh.exe wlan delete profile name="Wifi-Servis" >$null 2>&1
                foreach ($secFile in $clientSecretFiles) {
                    if (Test-Path $secFile) {
                        Remove-Item -Path $secFile -Force -ErrorAction SilentlyContinue
                    }
                }
            }
        } catch {
            $antiLeakVerified = $false
            if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                Write-ServisLog "Greška pri izvršavanju Clear-ServisWifiSecrets: $($_.Exception.Message)" -Level "ERROR" -Component "Security"
            }
        }

        # Provera da li je Clear-ServisWifiSecrets upisao anti_leak_verified = false u deploy-state.json
        if (Test-Path $stateFile) {
            try {
                $postCleanRaw = Get-Content -Path $stateFile -Raw -Encoding UTF8 -ErrorAction SilentlyContinue
                if ($postCleanRaw) {
                    $postCleanState = ConvertFrom-Json -InputObject $postCleanRaw -ErrorAction SilentlyContinue
                    if ($postCleanState -and $null -ne $postCleanState.anti_leak_verified -and $postCleanState.anti_leak_verified -eq $false) {
                        $antiLeakVerified = $false
                    }
                }
            } catch {}
        }

        # Fizička provera odsustva servisnih credential fajlova na klijentskom sistemu
        foreach ($sfPath in $clientSecretFiles) {
            if (Test-Path $sfPath) {
                try { Remove-Item -Path $sfPath -Force -ErrorAction SilentlyContinue } catch {}
                if (Test-Path $sfPath) {
                    $antiLeakVerified = $false
                }
            }
        }

        if ($forceAntiLeakFail) {
            $antiLeakVerified = $false
        }

        if (-not $hardeningOk) {
            try {
                if (Get-Command Restore-WisSecurityHardening -ErrorAction SilentlyContinue) {
                    $hardeningOk = [bool](Restore-WisSecurityHardening)
                } elseif (Get-Command Restore-ServisUacDefaults -ErrorAction SilentlyContinue) {
                    $hardeningOk = [bool](Restore-ServisUacDefaults)
                }
            } catch {
                $hardeningOk = $false
            }
        }
    }

    if ($null -ne $reportAndCleanupError) {
        throw $reportAndCleanupError
    }

    $finalTelemetryData['security_hardening_verified'] = [bool]$hardeningOk
    $finalTelemetryData['anti_leak_verified'] = [bool]$antiLeakVerified

    if ($blockHandover -or (-not $antiLeakVerified) -or (-not $hardeningOk)) {
        if (-not $blockHandover) {
            if (-not $antiLeakVerified) {
                $blockReasons.Add("Anti-Leak verifikacija čišćenja tajni nije uspela (anti_leak_verified=false)")
            }
            if (-not $hardeningOk) {
                $blockReasons.Add("Post-verifikacija vraćanja Windows bezbednosnih polisa (UAC/SMB/Defender) nije uspela")
            }
            $failSecReason = if (-not $antiLeakVerified) {
                "Zavrsna Anti-Leak provera nije uspela ($($blockReasons -join ' | '))."
            } else {
                "Post-verifikacija vracanja Windows bezbednosnih polisa (UAC/SMB/Defender) nije uspela."
            }
            $attnMsg = $failSecReason
            $finalTelemetryData['handover_ready'] = $false
            $finalTelemetryData['anti_leak_verified'] = [bool]$antiLeakVerified
            $finalTelemetryData['security_hardening_verified'] = [bool]$hardeningOk
            Update-ServisKartonContract -FinalStatus "NEEDS_ATTENTION" -HandoverReadyFlag $false -AntiLeakFlag ([bool]$antiLeakVerified) -StatusMsg $failSecReason
            try {
                $initialStateMap["phase"] = 6
                $initialStateMap["deployment_status"] = "NEEDS_ATTENTION"
                $initialStateMap["handover_ready"] = $false
                $initialStateMap["anti_leak_verified"] = [bool]$antiLeakVerified
                $initialStateMap["security_hardening_verified"] = [bool]$hardeningOk
                $initialStateMap["status_message"] = $failSecReason
                $initialStateMap["updated_at"] = (Get-Date -Format "o")
                $secErrJson = ConvertTo-Json -InputObject $initialStateMap -Depth 6
                [System.IO.File]::WriteAllText($stateFile, $secErrJson, $utf8NoBom)
                [System.IO.File]::WriteAllText((Join-Path $SystemLogDir "deploy-state.json"), $secErrJson, $utf8NoBom)
            } catch {}
            try {
                if (Get-Command Send-FleetFinalReport -ErrorAction SilentlyContinue) {
                    Send-FleetFinalReport -Status "NEEDS_ATTENTION" -CustomData $finalTelemetryData -LocalSetupDir $LocalSetupDir -SystemLogDir $SystemLogDir | Out-Null
                }
            } catch {}
        }

        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
            Write-ServisLog $attnMsg -Level "ERROR" -Component "Phase6"
        }
        if (Get-Command Send-IpcStatus -ErrorAction SilentlyContinue) {
            Send-IpcStatus -Phase 6 -Progress 100 -SubPhase "NEEDS_ATTENTION" -StatusMessage $attnMsg -DeploymentStatus "NEEDS_ATTENTION" -HandoverReady $false -StressResult $stressResultObj
        }

        # Arhiviranje dnevnika u C:\Windows\Logs\WIS pre izlaska
        try {
            foreach ($lf in @("servis_deploy.log", "servis_deploy.jsonl", "setupcomplete.log", "firstlogon.log")) {
                $srcP = Join-Path $LocalSetupDir $lf
                if (Test-Path $srcP) {
                    Copy-Item -Path $srcP -Destination (Join-Path $SystemLogDir $lf) -Force -ErrorAction SilentlyContinue
                }
            }
        } catch {}

        exit 2
    }

    Update-ServisKartonContract -FinalStatus "DONE" -HandoverReadyFlag $true -AntiLeakFlag $true -StatusMsg $readyMsg
    if (($Global:WisFinalReportSentStatus -ne "COMPLETED") -and (Get-Command Send-FleetFinalReport -ErrorAction SilentlyContinue)) {
        Send-FleetFinalReport -Status "COMPLETED" -CustomData $finalTelemetryData -LocalSetupDir $LocalSetupDir -SystemLogDir $SystemLogDir | Out-Null
    }

    # Očuvanje stress-test-result.json i deploy-state.json nakon što je Start-FinalCleanup izvršen
    try {
        $diagWarnFinal = $diagnosticsContract.diagnostics_warning
        $contractFinal = [ordered]@{
            run_id              = $stressRunId
            status              = $stressStatus
            reason              = $stressReason
            emergency_abort     = [bool]$emergencyAbort
            sensor_available    = [bool]$sensorAvailable
            sensor_source       = $sensorSource
            duration_sec        = [int]$stressDurationSec
            duration_seconds    = [int]$stressDurationSec
            max_cpu_temp_c      = [double]$maxCpuTemp
            max_gpu_temp_c      = [double]$maxGpuTemp
            max_cpu_temp        = [double]$maxCpuTemp
            max_gpu_temp        = [double]$maxGpuTemp
            throttling_detected = [bool]$throttlingDet
            timestamp           = $stressTimestamp
            stress_report       = $stressReport
            diagnostics_warning = $diagWarnFinal
            thermal_note        = $thermalNote
            notes               = $notesArr
            stress_history      = $stressHistory
        }
        $contractFinalJson = ConvertTo-Json -InputObject $contractFinal -Depth 6
        [System.IO.File]::WriteAllText($stressResultFile, $contractFinalJson, $utf8NoBom)
        [System.IO.File]::WriteAllText((Join-Path $SystemLogDir "stress-test-result.json"), $contractFinalJson, $utf8NoBom)

        $initialStateMap["phase"]               = 6
        $initialStateMap["deployment_status"]   = "DONE"
        $initialStateMap["handover_ready"]      = $true
        $initialStateMap["hwtest_incomplete"]   = $false
        $initialStateMap["anti_leak_verified"]  = $true
        $initialStateMap["security_hardening_verified"] = [bool]$hardeningOk
        $initialStateMap["diagnostics_status"]  = $diagnosticsContract.diagnostics_status
        $initialStateMap["diagnostics_warning"] = $diagWarnFinal
        $initialStateMap["thermal_note"]        = $thermalNote
        $initialStateMap["notes"]               = $notesArr
        $initialStateMap["cooling_verified"] = $diagnosticsContract.cooling_verified
        $initialStateMap["manual_check_required"] = $diagnosticsContract.manual_check_required
        $initialStateMap["stress_status"]       = $stressStatus
        $initialStateMap["stress_reason"]       = $stressReason
        $initialStateMap["emergency_abort"]     = [bool]$emergencyAbort
        $initialStateMap["sensor_available"]    = [bool]$sensorAvailable
        $initialStateMap["sensor_source"]       = $sensorSource
        $initialStateMap["stress_duration_sec"] = [int]$stressDurationSec
        $initialStateMap["max_cpu_temp"]        = [double]$maxCpuTemp
        $initialStateMap["max_gpu_temp"]        = [double]$maxGpuTemp
        $initialStateMap["max_cpu_temp_c"]      = [double]$maxCpuTemp
        $initialStateMap["max_gpu_temp_c"]      = [double]$maxGpuTemp
        $initialStateMap["throttling_detected"] = [bool]$throttlingDet
        $initialStateMap["stress_report"]       = $stressReport
        $initialStateMap["stress_timestamp"]    = $stressTimestamp
        $initialStateMap["stress_history"]      = $stressHistory
        $initialStateMap["status_message"]      = $readyMsg
        $initialStateMap["updated_at"]          = (Get-Date -Format "o")

        $readyStateJson = ConvertTo-Json -InputObject $initialStateMap -Depth 6
        [System.IO.File]::WriteAllText($stateFile, $readyStateJson, $utf8NoBom)
        [System.IO.File]::WriteAllText((Join-Path $SystemLogDir "deploy-state.json"), $readyStateJson, $utf8NoBom)
    } catch {}

    # 4. Eksplicitno postavljanje završnog napretka na 100% i exit 0
    Send-IpcProgress -Progress 100 -Phase 6 -PhaseName "Završetak" -SubPhase 'Završeno' -StatusMessage $readyMsg
    if (Get-Command Send-IpcStatus -ErrorAction SilentlyContinue) {
        Send-IpcStatus -Phase 6 -Progress 100 -SubPhase "Završeno" -StatusMessage $readyMsg -DeploymentStatus "DONE" -HandoverReady $true -StressResult $stressResultObj
    }

    if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
        Write-ServisLog "$readyMsg C:\setup folder je očuvan na disku." -Level "SUCCESS" -Component "Phase6"
    }

    if (Get-Command Complete-WisPhaseBenchmark -ErrorAction SilentlyContinue) {
        Complete-WisPhaseBenchmark -Phase 6 -Status "COMPLETED" -LocalSetupDir $LocalSetupDir | Out-Null
    }

    exit 0
} catch {
    $err = $_
    $errMsg = "Neocekivana sistemska greska u Fazi 6 (Zavrsetak): $($err.Exception.Message)"
    if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) { Write-ServisLog $errMsg -Level "ERROR" -Component "Phase6" }
    if (Get-Command Send-IpcLog -ErrorAction SilentlyContinue) { Send-IpcLog -Message $errMsg -Level "ERROR" -Tag "[ERROR]" -Phase 6 }
    try {
        $sfErr = Join-Path $LocalSetupDir "deploy-state.json"
        $errMap = [ordered]@{
            phase             = 6
            deployment_status = "NEEDS_ATTENTION"
            handover_ready    = $false
            status_message    = $errMsg
            updated_at        = (Get-Date -Format "o")
        }
        $utf8NoBomErr = New-Object System.Text.UTF8Encoding($false)
        [System.IO.File]::WriteAllText($sfErr, (ConvertTo-Json -InputObject $errMap -Depth 5), $utf8NoBomErr)
        if (Get-Command Send-IpcStatus -ErrorAction SilentlyContinue) {
            Send-IpcStatus -Phase 6 -SubPhase "NEEDS_ATTENTION" -StatusMessage $errMsg -DeploymentStatus "NEEDS_ATTENTION" -HandoverReady $false
        }
    } catch {}
    exit 1
}
