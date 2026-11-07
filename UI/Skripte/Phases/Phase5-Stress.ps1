<#
.SCRIPT
    Phase5-Stress.ps1
.DESCRIPTION
    WIS v2.15.0 (Tehno kutak Windows Installation Assistant) - Faza 5: Termalni Stres Test i Izvestaj (30 sekundi, Vitki Orkestrator).
.COMPATIBILITY
    Windows PowerShell 5.1+
#>

[CmdletBinding()]
param(
    [string]$LocalSetupDir = "C:\setup",
    [int]$DurationSeconds = 30
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot '..\Modules\WIS.StatusContract.psm1') -Force

trap {
    $err = $_
    if (Get-Command Send-IpcLog -ErrorAction SilentlyContinue) {
        Send-IpcLog -Message "FATALNA GREŠKA FAZE: $($err.Exception.Message)" -Level "FATAL" -Tag "[FATAL]" -Phase 5
    }
    if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
        Write-ServisLog "Fatalna greška faze: $($err.Exception.Message)" -Level "ERROR" -Component "Phase5"
    }
    exit 1
}

# 1. Uvoz deljenog modula i namenskih modula za hardver, UI i telemetriju (iskljucivo WIS.*)
$commonMod = @(
    (Join-Path $PSScriptRoot "..\Modules\WIS.Common.psm1"),
    (Join-Path $LocalSetupDir "Skripte\Modules\WIS.Common.psm1"),
    "C:\setup\Skripte\Modules\WIS.Common.psm1"
) | Where-Object { Test-Path $_ } | Select-Object -First 1

if ($commonMod) { Import-Module $commonMod -Force }

$hwMod = @(
    (Join-Path $PSScriptRoot "..\Modules\WIS.Hardware.psm1"),
    (Join-Path $LocalSetupDir "Skripte\Modules\WIS.Hardware.psm1"),
    "C:\setup\Skripte\Modules\WIS.Hardware.psm1"
) | Where-Object { Test-Path $_ } | Select-Object -First 1

if ($hwMod) { Import-Module $hwMod -Force }

$uiMod = @(
    (Join-Path $PSScriptRoot "..\Modules\WIS.UI.psm1"),
    (Join-Path $LocalSetupDir "Skripte\Modules\WIS.UI.psm1"),
    "C:\setup\Skripte\Modules\WIS.UI.psm1"
) | Where-Object { Test-Path $_ } | Select-Object -First 1

if ($uiMod) { Import-Module $uiMod -Force -ErrorAction SilentlyContinue }

$telMod = @(
    (Join-Path $PSScriptRoot "..\Modules\WIS.Telemetry.psm1"),
    (Join-Path $LocalSetupDir "Skripte\Modules\WIS.Telemetry.psm1"),
    "C:\setup\Skripte\Modules\WIS.Telemetry.psm1"
) | Where-Object { Test-Path $_ } | Select-Object -First 1

if ($telMod) { Import-Module $telMod -Force -ErrorAction SilentlyContinue }

# 2. Verifikacija administratorskih privilegija
try {
    Assert-ServisAdministrator
} catch {
    $adminErrMsg = "KRITICNA GRESKA: Faza 5 (Stres Test) zahteva administratorske privilegije (Run as Administrator)!"
    if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) { Write-ServisLog $adminErrMsg -Level "ERROR" -Component "Security" }
    if (Get-Command Send-IpcLog -ErrorAction SilentlyContinue) { Send-IpcLog -Message $adminErrMsg -Level "ERROR" -Tag "[ERROR]" -Phase 5 }
    exit 1
}

# 3. Glavno orkestrirano izvrsenje Faze 5 (Termalni Stres Test - 30 sekundi)
try {
    $startBannerMsg = "Faza 5: Termalni stres test ($DurationSeconds sekundi)..."
    if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
        Write-ServisLog $startBannerMsg -Level "INFO" -Component "Phase5"
    }
    if (Get-Command Send-IpcProgress -ErrorAction SilentlyContinue) {
        Send-IpcProgress -Progress 5 -Phase 5 -PhaseName "Stres Test" -SubPhase "Inicijalizacija" -StatusMessage $startBannerMsg
    }
    if (Get-Command Update-ServisUI -ErrorAction SilentlyContinue) {
        try { Update-ServisUI -Phase 5 -Progress 5 -SubPhase "Inicijalizacija" -StatusMessage $startBannerMsg } catch {}
    }

    $stressRes = $null
    if (Get-Command Start-ServisStressTest -ErrorAction SilentlyContinue) {
        $stressRes = Start-ServisStressTest -DurationSeconds $DurationSeconds -LocalSetupDir $LocalSetupDir
    } else {
        # Samostalno izvršenje: 100% Multi-Core CPU Load Generator preko RunspacePool-a uz Stopwatch i kontrolisani test ulaz
        $runId = [Guid]::NewGuid().ToString("N")
        $maxCpuTemp = 0.0
        $maxGpuTemp = 0.0
        $throttlingDetected = $false
        $emergencyAbort = $false
        $sensorAvailable = $false
        $sensorSource = "NONE"
        $manualCancel = $false
        $executionError = $false
        $errorDetail = ""
        $abortedEarly = $false
        $stopwatch = [System.Diagnostics.Stopwatch]::new()

        $controlledInput = $null
        $useControlledInput = $false
        if (-not [string]::IsNullOrWhiteSpace($env:WIS_STRESS_TEST_INPUT_JSON)) {
            $useControlledInput = $true
            $sensorSource = "CONTROLLED_TEST_INPUT"
            $rawTestInput = $env:WIS_STRESS_TEST_INPUT_JSON.Trim()
            try {
                $jsonContent = $null
                if (Test-Path -LiteralPath $rawTestInput) {
                    $jsonContent = Get-Content -LiteralPath $rawTestInput -Raw -Encoding UTF8 -ErrorAction Stop
                } elseif ($rawTestInput.StartsWith("{")) {
                    $jsonContent = $rawTestInput
                } else {
                    throw "Fajl kontrolisanog test ulaza ne postoji: $rawTestInput"
                }
                if ([string]::IsNullOrWhiteSpace($jsonContent)) {
                    throw "Kontrolisani test ulaz je prazan."
                }
                $controlledInput = ConvertFrom-Json -InputObject $jsonContent -ErrorAction Stop
                if ($null -eq $controlledInput) {
                    throw "Parsirani kontrolisani test ulaz je null."
                }
            } catch {
                $executionError = $true
                $abortedEarly = $true
                $errorDetail = $_.Exception.Message
            }
        }

        $cpuCount = [Math]::Max(1, [Environment]::ProcessorCount)
        $syncState = [hashtable]::Synchronized(@{ Stop = $false })
        $runspacePool = $null
        $stressInstances = [System.Collections.ArrayList]::new()

        try {
            if (-not $executionError) {
                if ($useControlledInput -and $controlledInput) {
                    if ($controlledInput.simulate_error -eq $true -or $controlledInput.force_error -eq $true -or "$($controlledInput.status)".ToUpper() -eq "ERROR" -or "$($controlledInput.reason)".ToUpper() -eq "EXECUTION_ERROR") {
                        $errMsgFromInput = if ($controlledInput.error_message) { "$($controlledInput.error_message)" } else { "Simulirana greška izvršavanja stres testa (CONTROLLED_TEST_INPUT)" }
                        throw $errMsgFromInput
                    }
                }

                $skipCpuWorkers = ($useControlledInput -and $controlledInput -and $controlledInput.skip_cpu_load -eq $true)
                if (-not $skipCpuWorkers) {
                    $sessionState = [System.Management.Automation.Runspaces.InitialSessionState]::CreateDefault()
                    $runspacePool = [System.Management.Automation.Runspaces.RunspaceFactory]::CreateRunspacePool(1, $cpuCount, $sessionState, $Host)
                    $runspacePool.Open()

                    $stressScript = {
                        param($sync)
                        $val = 1.0001
                        $counter = 0
                        while (-not $sync.Stop) {
                            for ($j = 0; $j -lt 5000; $j++) {
                                $val = [Math]::Sin($val) * [Math]::Cos($val) + [Math]::Sqrt($val + 1.0)
                            }
                            $counter++
                            if ($counter % 20 -eq 0) {
                                [System.Threading.Thread]::Yield() | Out-Null
                            }
                        }
                    }

                    for ($i = 0; $i -lt $cpuCount; $i++) {
                        $ps = [powershell]::Create()
                        $ps.RunspacePool = $runspacePool
                        [void]$ps.AddScript($stressScript).AddArgument($syncState)
                        [void]$stressInstances.Add(@{
                            Instance    = $ps
                            AsyncResult = $ps.BeginInvoke()
                        })
                    }
                    if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                        Write-ServisLog "Phase5: Pokrenut intenzivni 100% CPU load na svih $cpuCount jezgara..." -Level "INFO" -Component "Phase5"
                    }
                }

                $stopwatch.Start()

                $loopLimit = $DurationSeconds
                if ($useControlledInput -and $controlledInput -and $null -ne $controlledInput.test_iterations -and [int]$controlledInput.test_iterations -gt 0) {
                    $loopLimit = [int]$controlledInput.test_iterations
                }

                for ($sec = 1; $sec -le $loopLimit; $sec++) {
                    $cancelMarker = Join-Path $LocalSetupDir ".cancel_stress"
                    if ((Test-Path $cancelMarker) -or (Test-Path "C:\setup\.cancel_stress")) {
                        Remove-Item -Path $cancelMarker -Force -ErrorAction SilentlyContinue
                        Remove-Item -Path "C:\setup\.cancel_stress" -Force -ErrorAction SilentlyContinue
                        $manualCancel = $true
                        $abortedEarly = $true
                        $syncState.Stop = $true
                        break
                    }
                    if ((Get-Command Test-ServisSkipRequested -ErrorAction SilentlyContinue) -and (Test-ServisSkipRequested)) {
                        $manualCancel = $true
                        $abortedEarly = $true
                        $syncState.Stop = $true
                        break
                    }
                    if ($useControlledInput -and $controlledInput) {
                        if ($controlledInput.manual_cancel -eq $true -or $controlledInput.cancel_requested -eq $true -or "$($controlledInput.status)".ToUpper() -eq "CANCELLED" -or "$($controlledInput.reason)".ToUpper() -eq "MANUAL_CANCEL") {
                            $cancelAtSec = if ($null -ne $controlledInput.cancel_at_sec) { [int]$controlledInput.cancel_at_sec } else { 1 }
                            if ($sec -ge $cancelAtSec) {
                                $manualCancel = $true
                                $abortedEarly = $true
                                $syncState.Stop = $true
                                break
                            }
                        }
                    }

                    $pct = [int][math]::Min(95, [math]::Max(10, [math]::Round(($sec / [double]$loopLimit) * 90.0)))
                    $curTemp = 0.0
                    $curGpu = 0.0

                    if ($useControlledInput -and $controlledInput) {
                        $sensorSource = "CONTROLLED_TEST_INPUT"
                        if ($controlledInput.samples -and @($controlledInput.samples).Count -gt 0) {
                            $sampleArr = @($controlledInput.samples)
                            $sIdx = [Math]::Min($sec - 1, $sampleArr.Count - 1)
                            $curTemp = [double]$sampleArr[$sIdx]
                        } elseif ($null -ne $controlledInput.max_cpu_temp_c) {
                            $curTemp = [double]$controlledInput.max_cpu_temp_c
                        } elseif ($null -ne $controlledInput.max_cpu_temp) {
                            $curTemp = [double]$controlledInput.max_cpu_temp
                        } elseif ($null -ne $controlledInput.cpu_temp_c) {
                            $curTemp = [double]$controlledInput.cpu_temp_c
                        } elseif ($null -ne $controlledInput.cpu_temp) {
                            $curTemp = [double]$controlledInput.cpu_temp
                        } elseif ($null -ne $controlledInput.temperature) {
                            $curTemp = [double]$controlledInput.temperature
                        }

                        if ($null -ne $controlledInput.max_gpu_temp_c) {
                            $curGpu = [double]$controlledInput.max_gpu_temp_c
                        } elseif ($null -ne $controlledInput.max_gpu_temp) {
                            $curGpu = [double]$controlledInput.max_gpu_temp
                        }

                        if ($controlledInput.sensor_available -eq $false -or "$($controlledInput.status)".ToUpper() -eq "UNKNOWN" -or "$($controlledInput.reason)".ToUpper() -eq "SENSOR_UNAVAILABLE") {
                            $curTemp = 0.0
                            $curGpu = 0.0
                            $sensorAvailable = $false
                        } elseif ($curTemp -gt 0.0) {
                            $sensorAvailable = $true
                        }

                        if ($controlledInput.throttling_detected -eq $true) { $throttlingDetected = $true }
                        if ($controlledInput.emergency_abort -eq $true) { $emergencyAbort = $true }
                    } else {
                        try {
                            foreach ($lhmNs in @("root\LibreHardwareMonitor", "root\OpenHardwareMonitor")) {
                                $lhmSensors = Get-CimInstance -Namespace $lhmNs -ClassName Sensor -Filter "SensorType='Temperature'" -ErrorAction SilentlyContinue
                                if ($lhmSensors) {
                                    foreach ($s in $lhmSensors) {
                                        if ($null -ne $s.Value) {
                                            $valC = [math]::Round([double]$s.Value, 1)
                                            if ($valC -gt 0.0 -and $valC -lt 130.0) {
                                                if ($s.Name -match '(?i)GPU|Graphics|Hot Spot') {
                                                    if ($valC -gt $curGpu) { $curGpu = $valC }
                                                } else {
                                                    if ($valC -gt $curTemp) {
                                                        $curTemp = $valC
                                                        $sensorAvailable = $true
                                                        $sensorSource = "LHM_WMI"
                                                    }
                                                }
                                            }
                                        }
                                    }
                                    if ($curTemp -gt 0.0) { break }
                                }
                            }
                        } catch {}

                        if ($curTemp -le 0.0) {
                            try {
                                $tzList = Get-CimInstance -Namespace root\wmi -ClassName MSAcpi_ThermalZoneTemperature -ErrorAction SilentlyContinue
                                if ($tzList) {
                                    foreach ($tz in $tzList) {
                                        if ($tz.CurrentTemperature -gt 2732) {
                                            $c = [math]::Round(($tz.CurrentTemperature / 10.0) - 273.15, 1)
                                            if ($c -gt 0.0 -and $c -gt $curTemp -and $c -lt 130.0) {
                                                $curTemp = $c
                                                $sensorAvailable = $true
                                                if ($sensorSource -eq "NONE") { $sensorSource = "ACPI_THERMAL" }
                                            }
                                        }
                                    }
                                }
                            } catch {}
                        }

                        if ($curTemp -le 0.0) {
                            try {
                                $perfTz = Get-CimInstance -ClassName Win32_PerfFormattedData_Counters_ThermalZoneInformation -ErrorAction SilentlyContinue
                                if ($perfTz) {
                                    foreach ($pt in $perfTz) {
                                        if ($pt.Temperature -gt 273) {
                                            $c = [math]::Round([double]$pt.Temperature - 273.15, 1)
                                            if ($c -gt 0.0 -and $c -gt $curTemp -and $c -lt 130.0) {
                                                $curTemp = $c
                                                $sensorAvailable = $true
                                                if ($sensorSource -eq "NONE") { $sensorSource = "PERF_COUNTER" }
                                            }
                                        }
                                    }
                                }
                            } catch {}
                        }
                    }

                    if ($curTemp -gt $maxCpuTemp) { $maxCpuTemp = $curTemp }
                    if ($curGpu -gt $maxGpuTemp) { $maxGpuTemp = $curGpu }

                    if ($emergencyAbort -eq $true -or $curTemp -ge 98.0 -or $curGpu -ge 98.0 -or $maxCpuTemp -ge 98.0) {
                        $emergencyAbort = $true
                        $throttlingDetected = $true
                        $abortedEarly = $true
                        $syncState.Stop = $true

                        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                            Write-ServisLog "SIGURNOSNI PREKID STRES TESTA: Detektovana kritična temperatura od ${maxCpuTemp}°C (>= 98°C)!" -Level "ERROR" -Component "Phase5"
                        }
                        if (Get-Command Send-IpcLog -ErrorAction SilentlyContinue) {
                            Send-IpcLog -Message "EMERGENCY ABORT: Kritična temperatura ${maxCpuTemp}°C (>= 98°C)!" -Level "FATAL" -Tag "[ALERT]" -Phase 5
                        }

                        try {
                            if (Get-Command Send-ServisAudioSignal -ErrorAction SilentlyContinue) {
                                Send-ServisAudioSignal -Type "Alert"
                            } else {
                                [Console]::Beep(1000, 250)
                                Start-Sleep -Milliseconds 100
                                [Console]::Beep(800, 350)
                            }
                        } catch {}

                        break
                    }

                    if ($curTemp -ge 92.0 -or $maxCpuTemp -ge 92.0) {
                        $throttlingDetected = $true
                    }

                    if (Get-Command Send-IpcProgress -ErrorAction SilentlyContinue) {
                        $tempSuffix = if ($sensorAvailable -and $curTemp -gt 0) { " (Trenutno: ${curTemp}°C | Max CPU: ${maxCpuTemp}°C)" } else { " (Senzor temperature nije dostupan)" }
                        Send-IpcProgress -Progress $pct -Phase 5 -PhaseName "Stres Test" -SubPhase "Termalno opterećenje ($sec/${loopLimit}s)" -StatusMessage "Testiranje sistema hlađenja (100% Load): $sec / ${loopLimit}s$tempSuffix"
                    }

                    if ($useControlledInput -and $controlledInput -and $null -ne $controlledInput.sleep_ms) {
                        $sleepMs = [int]$controlledInput.sleep_ms
                        if ($sleepMs -gt 0) {
                            Start-Sleep -Milliseconds $sleepMs
                        }
                    } else {
                        Start-Sleep -Seconds 1
                    }
                }
            }
        } catch {
            $executionError = $true
            $abortedEarly = $true
            $errorDetail = $_.Exception.Message
            $syncState.Stop = $true
            if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                Write-ServisLog "Phase5 greška pri izvršavanju stres testa: $errorDetail" -Level "ERROR" -Component "Phase5"
            }
        } finally {
            if ($stopwatch.IsRunning) {
                $stopwatch.Stop()
            }
            $syncState.Stop = $true
            foreach ($item in $stressInstances) {
                try {
                    if ($item.Instance) {
                        $item.Instance.Stop()
                        $item.Instance.Dispose()
                    }
                } catch {}
            }
            $stressInstances.Clear()
            if ($runspacePool) {
                try {
                    $runspacePool.Close()
                    $runspacePool.Dispose()
                } catch {}
            }
        }

        $actualDurationSec = if ($abortedEarly) {
            [int][Math]::Floor($stopwatch.Elapsed.TotalSeconds)
        } else {
            [int][Math]::Round($stopwatch.Elapsed.TotalSeconds)
        }
        if (-not $abortedEarly -and $useControlledInput -and $controlledInput -and $null -ne $controlledInput.duration_sec -and $actualDurationSec -le 0) {
            $actualDurationSec = [int]$controlledInput.duration_sec
        }

        if (-not $sensorAvailable -or $maxCpuTemp -le 0.0) {
            $sensorAvailable = $false
            if (-not $useControlledInput) {
                $sensorSource = "NONE"
            }
        }

        $status = "UNKNOWN"
        $reason = "SENSOR_UNAVAILABLE"

        if ($emergencyAbort -eq $true -or $maxCpuTemp -ge 98.0) {
            $emergencyAbort = $true
            $status = "THERMAL_CRITICAL"
            $reason = "EMERGENCY_THERMAL_ABORT"
        } elseif ($maxCpuTemp -ge 95.0) {
            $status = "THERMAL_CRITICAL"
            $reason = "CRITICAL_TEMP_THRESHOLD"
        } elseif ($executionError) {
            $status = "ERROR"
            $reason = "EXECUTION_ERROR"
        } elseif ($manualCancel) {
            $status = "CANCELLED"
            $reason = "MANUAL_CANCEL"
        } elseif (-not $sensorAvailable -or $maxCpuTemp -le 0.0) {
            $status = "UNKNOWN"
            $reason = "SENSOR_UNAVAILABLE"
        } elseif ($throttlingDetected -or $maxCpuTemp -ge 85.0) {
            $status = "WARN"
            $reason = "HIGH_TEMP_WARN"
        } else {
            $status = "PASS"
            $reason = "COMPLETED_NORMAL"
        }

        $verdict = switch ($reason) {
            "EMERGENCY_THERMAL_ABORT" { "KRITIČNO (Emergency Abort na 98°C: Max CPU ${maxCpuTemp}°C, Trajanje: ${actualDurationSec}s)" }
            "CRITICAL_TEMP_THRESHOLD" { "KRITIČNO (Prekoračena bezbedna temperatura >= 95°C: Max CPU ${maxCpuTemp}°C, Trajanje: ${actualDurationSec}s)" }
            "EXECUTION_ERROR"         { "GREŠKA IZVRŠAVANJA STRES TESTA ($errorDetail)" }
            "MANUAL_CANCEL"           { "OTKAZANO (Stres test ručno prekinut nakon ${actualDurationSec}s)" }
            "SENSOR_UNAVAILABLE"      { "NEPOZNATO (Termalni senzori nisu dostupni - temperatura nije izmerena)" }
            "HIGH_TEMP_WARN"          { "UPOZORENJE (Povišena temperatura pod 100% opterećenjem: Max CPU ${maxCpuTemp}°C)" }
            default                   { "ISPRAVNO (Stabilno hlađenje pod 100% opterećenjem, Max CPU: ${maxCpuTemp}°C)" }
        }

        $stressRes = [PSCustomObject]@{
            run_id              = [string]$runId
            status              = [string]$status
            reason              = [string]$reason
            emergency_abort     = [bool]$emergencyAbort
            sensor_available    = [bool]$sensorAvailable
            sensor_source       = [string]$sensorSource
            duration_sec        = [int]$actualDurationSec
            duration_seconds    = [int]$actualDurationSec
            max_cpu_temp_c      = [double]$maxCpuTemp
            max_gpu_temp_c      = [double]$maxGpuTemp
            max_cpu_temp        = [double]$maxCpuTemp
            max_gpu_temp        = [double]$maxGpuTemp
            throttling_detected = [bool]$throttlingDetected
            diagnostics_warning = if ($status -eq "UNKNOWN" -or -not $sensorAvailable) { "Senzori temperature nisu dostupni na hardveru" } else { "" }
            timestamp           = (Get-Date -Format "o")
            stress_report       = [string]$verdict
        }
    }

    # Evaluacija kontrolnog signala za Fazu 5 prema autoritativnom statusu stres testa
    $stressStatus = if ($stressRes -and $stressRes.status) { "$($stressRes.status)".ToUpper().Trim() } else { "ERROR" }
    $stressReason = if ($stressRes -and $stressRes.reason) { "$($stressRes.reason)".Trim() } else { "EXECUTION_ERROR" }
    $stressDurSec = if ($stressRes -and $null -ne $stressRes.duration_sec) { [int]$stressRes.duration_sec } elseif ($stressRes -and $null -ne $stressRes.duration_seconds) { [int]$stressRes.duration_seconds } else { 0 }
    $maxCpuVal    = if ($stressRes -and $null -ne $stressRes.max_cpu_temp_c) { [double]$stressRes.max_cpu_temp_c } elseif ($stressRes -and $null -ne $stressRes.max_cpu_temp) { [double]$stressRes.max_cpu_temp } else { 0.0 }
    $maxGpuVal    = if ($stressRes -and $null -ne $stressRes.max_gpu_temp_c) { [double]$stressRes.max_gpu_temp_c } elseif ($stressRes -and $null -ne $stressRes.max_gpu_temp) { [double]$stressRes.max_gpu_temp } else { 0.0 }
    $sensorAvail  = if ($stressRes -and $null -ne $stressRes.sensor_available) { [bool]$stressRes.sensor_available } else { $false }
    $sensorSrc    = if ($stressRes -and $stressRes.sensor_source) { "$($stressRes.sensor_source)" } else { "NONE" }
    $emergAbort   = if ($stressRes -and $null -ne $stressRes.emergency_abort) { [bool]$stressRes.emergency_abort } else { $false }
    $throttling   = if ($stressRes -and $null -ne $stressRes.throttling_detected) { [bool]$stressRes.throttling_detected } else { $false }
    $reportTxt    = if ($stressRes -and $stressRes.stress_report) { "$($stressRes.stress_report)" } else { "$stressStatus ($stressReason)" }
    $stressTs     = if ($stressRes -and $stressRes.timestamp) { "$($stressRes.timestamp)" } else { (Get-Date -Format "o") }
    $stressRunId  = if ($stressRes -and $stressRes.run_id) { "$($stressRes.run_id)" } else { [Guid]::NewGuid().ToString("N") }

    $diagWarning  = if ($stressRes -and $stressRes.diagnostics_warning) { "$($stressRes.diagnostics_warning)" } elseif ($stressStatus -eq "UNKNOWN" -or -not $sensorAvail) { "Senzori temperature nisu dostupni na hardveru" } else { "" }

    # Određivanje ciljnih vrednosti za phase, deployment_status i handover_ready
    $targetPhase = 5
    $targetDeploymentStatus = "NEEDS_ATTENTION"
    $targetHandoverReady = $false

    if ($stressStatus -eq "PASS" -or $stressStatus -eq "WARN") {
        $targetPhase = 6
        $targetDeploymentStatus = "ACTIVE"
        $targetHandoverReady = $true
    } elseif ($stressStatus -eq "UNKNOWN") {
        # Termalni senzori nisu dostupni (stariji čipset, ploča bez WMI/ACPI senzora, virtuelna mašina):
        # Sistem NE SME da blokira ili prekida instalaciju! Pipeline uspešno nastavlja u Fazu 6 sa statusom ACTIVE i handover_ready = $true.
        $targetPhase = 6
        $targetDeploymentStatus = "ACTIVE"
        $targetHandoverReady = $true
    } else {
        # THERMAL_CRITICAL, ERROR, CANCELLED: NE upisivati phase = 6 kao dokaz uspešnog završetka faze!
        $targetPhase = 5
        $targetDeploymentStatus = "NEEDS_ATTENTION"
        $targetHandoverReady = $false
    }

    # Upis u C:\setup\stress-test-result.json i C:\setup\deploy-state.json (uz očuvanje stress_history)
    if (Get-Command Save-ServisStressTestResult -ErrorAction SilentlyContinue) {
        Save-ServisStressTestResult -StressResult $stressRes -LocalSetupDir $LocalSetupDir -Phase $targetPhase -DeploymentStatus $targetDeploymentStatus -HandoverReady $targetHandoverReady
    } else {
        $stateFile = Join-Path $LocalSetupDir "deploy-state.json"
        $resFile = Join-Path $LocalSetupDir "stress-test-result.json"
        try {
            if (-not (Test-Path $LocalSetupDir)) {
                New-Item -Path $LocalSetupDir -ItemType Directory -Force | Out-Null
            }
            $stateObj = [ordered]@{}
            if (Test-Path $stateFile) {
                try {
                    $rawState = Get-Content -Path $stateFile -Raw -Encoding UTF8 -ErrorAction SilentlyContinue
                    if ($rawState) {
                        $parsed = $rawState | ConvertFrom-Json -ErrorAction SilentlyContinue
                        if ($parsed) {
                            foreach ($p in $parsed.PSObject.Properties) {
                                $stateObj[$p.Name] = $p.Value
                            }
                        }
                    }
                } catch {}
            }

            $historyList = [System.Collections.ArrayList]::new()
            if ($stateObj.Contains("stress_history") -and $null -ne $stateObj["stress_history"]) {
                foreach ($h in @($stateObj["stress_history"])) {
                    if ($null -ne $h) { [void]$historyList.Add($h) }
                }
            }
            if ($stateObj.Contains("stress_status") -and "$($stateObj["stress_run_id"])" -ne $stressRunId) {
                $prev = [PSCustomObject]@{
                    run_id              = if ($stateObj.Contains("stress_run_id")) { "$($stateObj["stress_run_id"])" } else { "" }
                    status              = "$($stateObj["stress_status"])"
                    reason              = if ($stateObj.Contains("stress_reason")) { "$($stateObj["stress_reason"])" } else { "" }
                    emergency_abort     = if ($stateObj.Contains("emergency_abort")) { [bool]$stateObj["emergency_abort"] } else { $false }
                    sensor_available    = if ($stateObj.Contains("sensor_available")) { [bool]$stateObj["sensor_available"] } else { $false }
                    sensor_source       = if ($stateObj.Contains("sensor_source")) { "$($stateObj["sensor_source"])" } else { "NONE" }
                    duration_sec        = if ($stateObj.Contains("stress_duration_sec")) { [int]$stateObj["stress_duration_sec"] } else { 0 }
                    max_cpu_temp_c      = if ($stateObj.Contains("max_cpu_temp")) { [double]$stateObj["max_cpu_temp"] } else { 0.0 }
                    max_gpu_temp_c      = if ($stateObj.Contains("max_gpu_temp")) { [double]$stateObj["max_gpu_temp"] } else { 0.0 }
                    throttling_detected = if ($stateObj.Contains("throttling_detected")) { [bool]$stateObj["throttling_detected"] } else { $false }
                    stress_report       = if ($stateObj.Contains("stress_report")) { "$($stateObj["stress_report"])" } else { "" }
                    timestamp           = if ($stateObj.Contains("stress_timestamp")) { "$($stateObj["stress_timestamp"])" } else { "" }
                }
                [void]$historyList.Add($prev)
            }

            $utf8NoBom = New-Object System.Text.UTF8Encoding($false)

            $contractOut = [ordered]@{
                run_id              = $stressRunId
                status              = $stressStatus
                reason              = $stressReason
                emergency_abort     = $emergAbort
                sensor_available    = $sensorAvail
                sensor_source       = $sensorSrc
                duration_sec        = $stressDurSec
                duration_seconds    = $stressDurSec
                max_cpu_temp_c      = $maxCpuVal
                max_gpu_temp_c      = $maxGpuVal
                max_cpu_temp        = $maxCpuVal
                max_gpu_temp        = $maxGpuVal
                throttling_detected = $throttling
                timestamp           = $stressTs
                stress_report       = $reportTxt
                diagnostics_warning = $diagWarning
                stress_history      = @($historyList.ToArray())
            }
            [System.IO.File]::WriteAllText($resFile, ($contractOut | ConvertTo-Json -Depth 6), $utf8NoBom)

            $stateObj["stress_run_id"]       = $stressRunId
            $stateObj["stress_status"]       = $stressStatus
            $stateObj["stress_reason"]       = $stressReason
            $stateObj["emergency_abort"]     = $emergAbort
            $stateObj["sensor_available"]    = $sensorAvail
            $stateObj["sensor_source"]       = $sensorSrc
            $stateObj["stress_duration_sec"] = $stressDurSec
            $stateObj["max_cpu_temp"]        = $maxCpuVal
            $stateObj["max_gpu_temp"]        = $maxGpuVal
            $stateObj["max_cpu_temp_c"]      = $maxCpuVal
            $stateObj["max_gpu_temp_c"]      = $maxGpuVal
            $stateObj["throttling_detected"] = $throttling
            $stateObj["stress_report"]       = "$reportTxt"
            $stateObj["stress_timestamp"]    = $stressTs
            $stateObj["diagnostics_warning"] = $diagWarning
            $diagContract = Get-WisDiagnosticsContract -Status $stressStatus -SensorAvailable $sensorAvail -MaxCpuTemp $maxCpuVal -EmergencyAbort $emergencyAbort
            $stateObj["diagnostics_status"] = $diagContract.diagnostics_status
            $stateObj["cooling_verified"] = $diagContract.cooling_verified
            $stateObj["manual_check_required"] = $diagContract.manual_check_required
            $stateObj["stress_history"]      = @($historyList.ToArray())
            $stateObj["phase"]               = $targetPhase
            $stateObj["deployment_status"]   = $targetDeploymentStatus
            $stateObj["handover_ready"]      = $targetHandoverReady
            $stateObj["updated_at"]          = (Get-Date -Format "o")

            $jsonOut = $stateObj | ConvertTo-Json -Depth 6
            [System.IO.File]::WriteAllText($stateFile, $jsonOut, $utf8NoBom)
        } catch {
            if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                Write-ServisLog "Upozorenje pri upisu rezultata stres testa u deploy-state.json: $($_.Exception.Message)" -Level "WARN" -Component "Phase5"
            }
        }
    }

    # Kontrola izlaza i logovanja prema statusu stres testa
    if ($stressStatus -eq "THERMAL_CRITICAL") {
        $critMsg = "KRITIČAN TERMALNI PREKID ($stressReason): $reportTxt | Max CPU: ${maxCpuVal}°C | Max GPU: ${maxGpuVal}°C | Trajanje: ${stressDurSec}s. Računar NIJE spreman za predaju (NEEDS_ATTENTION)!"
        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
            Write-ServisLog $critMsg -Level "ERROR" -Component "Phase5"
        }
        if (Get-Command Send-IpcStatus -ErrorAction SilentlyContinue) {
            Send-IpcStatus -Phase 5 -Progress 100 -SubPhase "NEEDS_ATTENTION" -StatusMessage $critMsg -DeploymentStatus "NEEDS_ATTENTION" -HandoverReady $false -StressResult $stressRes
        }
        exit 2
    } elseif ($stressStatus -eq "ERROR" -or $stressStatus -eq "CANCELLED") {
        $errSummary = "STRES TEST NIJE USPEO ($stressStatus / $stressReason): $reportTxt | Trajanje: ${stressDurSec}s. Računar zahteva pažnju servisera (NEEDS_ATTENTION)!"
        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
            Write-ServisLog $errSummary -Level "ERROR" -Component "Phase5"
        }
        if (Get-Command Send-IpcStatus -ErrorAction SilentlyContinue) {
            Send-IpcStatus -Phase 5 -Progress 100 -SubPhase "NEEDS_ATTENTION" -StatusMessage $errSummary -DeploymentStatus "NEEDS_ATTENTION" -HandoverReady $false -StressResult $stressRes
        }
        exit 1
    } elseif ($stressStatus -eq "UNKNOWN") {
        $unknownMsg = "UPOZORENJE STRES TESTA (UNKNOWN / $stressReason): Senzori temperature nisu dostupni na hardveru (sensor_available=false). Termalni stres test ne blokira instalaciju. Prelazim na Fazu 6 sa statusom ACTIVE (handover_ready=true)."
        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
            Write-ServisLog $unknownMsg -Level "WARN" -Component "Phase5"
        }
        if (Get-Command Send-IpcProgress -ErrorAction SilentlyContinue) {
            Send-IpcProgress -Progress 100 -Phase 5 -PhaseName "Stres Test" -SubPhase "Završeno (Senzori nedostupni)" -StatusMessage $unknownMsg
        }
        if (Get-Command Send-IpcStatus -ErrorAction SilentlyContinue) {
            Send-IpcStatus -Phase 5 -Progress 100 -SubPhase "Završeno (Senzori nedostupni)" -StatusMessage $unknownMsg -DeploymentStatus "ACTIVE" -HandoverReady $true -StressResult $stressRes
        }
        exit 0
    } else {
        $summaryMsg = "Termalni stres test završen ($stressStatus / $stressReason): $reportTxt | Max CPU: ${maxCpuVal}°C | Max GPU: ${maxGpuVal}°C | Trajanje: ${stressDurSec}s | Throttling: $throttling"
        $finalLogLevel = if ($stressStatus -eq "WARN") { "WARN" } else { "SUCCESS" }
        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
            Write-ServisLog $summaryMsg -Level $finalLogLevel -Component "Phase5"
        }
        if (Get-Command Send-IpcProgress -ErrorAction SilentlyContinue) {
            Send-IpcProgress -Progress 100 -Phase 5 -PhaseName "Stres Test" -SubPhase "Završeno ($stressStatus)" -StatusMessage $summaryMsg
        }
        if (Get-Command Send-IpcStatus -ErrorAction SilentlyContinue) {
            Send-IpcStatus -Phase 5 -Progress 100 -SubPhase "Završeno ($stressStatus)" -StatusMessage $summaryMsg -DeploymentStatus "ACTIVE" -HandoverReady $true -StressResult $stressRes
        }
        exit 0
    }
} catch {
    $err = $_
    $errMsg = "Neocekivana sistemska greska u Fazi 5 (Stres Test): $($err.Exception.Message)"
    if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) { Write-ServisLog $errMsg -Level "ERROR" -Component "Phase5" }
    if (Get-Command Send-IpcLog -ErrorAction SilentlyContinue) { Send-IpcLog -Message $errMsg -Level "ERROR" -Tag "[ERROR]" -Phase 5 }
    try {
        $fallbackErrObj = [PSCustomObject]@{
            run_id              = [Guid]::NewGuid().ToString("N")
            status              = "ERROR"
            reason              = "EXECUTION_ERROR"
            emergency_abort     = $false
            sensor_available    = $false
            sensor_source       = "NONE"
            duration_sec        = 0
            duration_seconds    = 0
            max_cpu_temp_c      = 0.0
            max_gpu_temp_c      = 0.0
            max_cpu_temp        = 0.0
            max_gpu_temp        = 0.0
            throttling_detected = $false
            timestamp           = (Get-Date -Format "o")
            stress_report       = "GREŠKA IZVRŠAVANJA STRES TESTA ($($err.Exception.Message))"
        }
        if (Get-Command Save-ServisStressTestResult -ErrorAction SilentlyContinue) {
            Save-ServisStressTestResult -StressResult $fallbackErrObj -LocalSetupDir $LocalSetupDir -Phase 5 -DeploymentStatus "NEEDS_ATTENTION" -HandoverReady $false
        }
        if (Get-Command Send-IpcStatus -ErrorAction SilentlyContinue) {
            Send-IpcStatus -Phase 5 -SubPhase "NEEDS_ATTENTION" -StatusMessage $errMsg -DeploymentStatus "NEEDS_ATTENTION" -HandoverReady $false -StressResult $fallbackErrObj
        }
    } catch {}
    exit 1
}
