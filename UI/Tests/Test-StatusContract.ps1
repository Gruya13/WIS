[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$uiRoot = Split-Path $PSScriptRoot -Parent
$scriptRoot = Join-Path $uiRoot 'Skripte'
Import-Module (Join-Path $scriptRoot 'Modules\WIS.StatusContract.psm1') -Force
Import-Module (Join-Path $scriptRoot 'Modules\WIS.Common.psm1') -Force
Import-Module (Join-Path $scriptRoot 'Modules\WIS.Telemetry.psm1') -Force
function Assert-Equal($actual, $expected, [string]$message) {
    if ($actual -ne $expected) { throw "$message : expected=$expected actual=$actual" }
}
$cases = @(
    @('PASS', $true, 70, $false, 'OK', $true),
    @('WARN', $true, 90, $false, 'WARN', $false),
    @('UNKNOWN', $false, 0, $false, 'SENSOR_UNAVAILABLE', $false),
    @('ERROR', $true, 65, $false, 'ERROR', $false),
    @('CANCELLED', $true, 65, $false, 'CANCELLED', $false),
    @('INVALID', $true, 65, $false, 'NOT_VERIFIED', $false),
    @('UNKNOWN', $false, 98, $false, 'THERMAL_CRITICAL', $false),
    @('UNKNOWN', $false, 0, $true, 'THERMAL_CRITICAL', $false),
    @('THERMAL_CRITICAL', $false, 0, $false, 'THERMAL_CRITICAL', $false),
    @('PASS', $false, 0, $false, 'SENSOR_UNAVAILABLE', $false)
)
$tempRoot = Join-Path ([IO.Path]::GetTempPath()) ('wis-contract-' + [Guid]::NewGuid().ToString('N'))
New-Item $tempRoot -ItemType Directory | Out-Null
try {
    $ordinal = 0
    foreach ($c in $cases) {
        $contract = Get-WisDiagnosticsContract -Status $c[0] -SensorAvailable $c[1] -MaxCpuTemp $c[2] -EmergencyAbort $c[3]
        Assert-Equal $contract.diagnostics_status $c[4] 'Diagnostic status'
        Assert-Equal $contract.cooling_verified $c[5] 'Cooling verification'
        Assert-Equal $contract.manual_check_required (-not $c[5]) 'Manual verification'
        $ordinal++
        $result = [PSCustomObject]@{ run_id = "run-$ordinal"; status = $c[0]; sensor_available = $c[1]; max_cpu_temp_c = $c[2]; emergency_abort = $c[3]; timestamp = (Get-Date -Format o) }
        $handover = -not $contract.thermal_critical
        Save-ServisStressTestResult -StressResult $result -LocalSetupDir $tempRoot -Phase 6 -DeploymentStatus 'ACTIVE' -HandoverReady $handover
        $state = Get-Content (Join-Path $tempRoot 'deploy-state.json') -Raw -Encoding UTF8 | ConvertFrom-Json
        Assert-Equal $state.diagnostics_status $c[4] 'Persisted diagnostics'
        Assert-Equal $state.cooling_verified $c[5] 'Persisted cooling'
        Assert-Equal $state.handover_ready $handover 'Installation continuation policy preserved'
        # Stvarni serializer koriste heartbeat i report, bez mreze/hardver side effects.
        $expectedStatus = if ($contract.thermal_critical) { 'NEEDS_ATTENTION' } else { 'COMPLETED' }
        $wire = New-WisTelemetryJson -Payload @{ status = $expectedStatus; apps = @{ Chrome = 'OK' } } -LocalSetupDir $tempRoot -SystemLogDir $tempRoot | ConvertFrom-Json
        Assert-Equal $wire.status $expectedStatus 'Diagnostic warning preserves installation status'
        Assert-Equal $wire.diagnostics_status $c[4] 'Wire diagnostics status'
        Assert-Equal $wire.cooling_verified $c[5] 'Wire cooling verification'
        Assert-Equal $wire.manual_check_required (-not $c[5]) 'Wire manual check'

    }
    $saved = Get-ServisStressTestResult -LocalSetupDir $tempRoot
    Assert-Equal @($saved.stress_history).Count ($cases.Count - 1) 'Previous stress runs retained'
    Set-Content (Join-Path $tempRoot 'stress-test-result.json') '{broken' -Encoding UTF8
    Assert-Equal (Get-ServisStressTestResult -LocalSetupDir $tempRoot) $null 'Corrupt stress result rejected'
    $archive = Join-Path $tempRoot 'archive'
    New-Item $archive -ItemType Directory | Out-Null
    @{ status = 'WARN'; sensor_available = $true; max_cpu_temp_c = 90; emergency_abort = $false } | ConvertTo-Json | Set-Content (Join-Path $archive 'stress-test-result.json') -Encoding UTF8
    $wire = New-WisTelemetryJson -Payload @{ status = 'COMPLETED' } -LocalSetupDir $tempRoot -SystemLogDir $archive | ConvertFrom-Json
    Assert-Equal $wire.diagnostics_status 'WARN' 'Archived stress result fallback'
    Remove-Item (Join-Path $archive 'stress-test-result.json')
    $captured = @{ diagnostics_status = 'WARN'; diagnostics_warning = 'Captured warning'; cooling_verified = $false; manual_check_required = $true; stress_status = 'WARN' }
    $wire = New-WisTelemetryJson -Payload @{ status = 'COMPLETED' } -CustomData $captured -LocalSetupDir $tempRoot -SystemLogDir $archive | ConvertFrom-Json
    Assert-Equal $wire.diagnostics_status 'WARN' 'Captured final contract survives cleanup'
    Assert-Equal $wire.diagnostics_warning 'Captured warning' 'Captured warning preserved'
    $wire = New-WisTelemetryJson -Payload @{ status = 'COMPLETED' } -LocalSetupDir $tempRoot -SystemLogDir $archive | ConvertFrom-Json
    Assert-Equal $wire.diagnostics_status 'SENSOR_UNAVAILABLE' 'No result never produces fake OK'
    Assert-Equal $wire.cooling_verified $false 'No result is not cooling proof'

    # Provera StepGuard IPC ugovora (događaj 'step')
    $stepStates = @('WAIT', 'RUNNING', 'OK', 'SKIPPED_DONE', 'SKIPPED_USER', 'WARN', 'ERROR')
    foreach ($st in $stepStates) {
        $sw = New-Object System.IO.StringWriter
        $prevOut = [Console]::Out
        try {
            [Console]::SetOut($sw)
            Send-IpcStep -Phase 4 -StepId "P4.Chrome" -Title "Google Chrome" -State $st -Reason "Test status" -Progress 50
        } finally {
            [Console]::SetOut($prevOut)
        }
        $stepJson = $sw.ToString().Trim()
        $stepObj = ConvertFrom-Json $stepJson
        Assert-Equal $stepObj.type 'step' 'Step IPC type'
        Assert-Equal $stepObj.step.state $st 'Step state in payload'
        Assert-Equal $stepObj.phase 4 'Step phase in payload'
        Assert-Equal $stepObj.step_id 'P4.Chrome' 'StepId in payload'
    }

    Write-Output 'PASS: 10 status/persistence/wire JSON cases, history, corrupt JSON, archive fallback, captured cleanup contract and StepGuard IPC contract'
} finally { Remove-Item $tempRoot -Recurse -Force }
