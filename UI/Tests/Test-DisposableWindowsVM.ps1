<#
    Samo za svezu Windows VM sa snapshotom. Pravi stvarni restart, koristi
    C:\setup i izvrsava stvarni Clear-ServisWifiSecrets; nikad na klijentu.
    Ne dokazuje pun instalacioni tok, upravljanje drajverima ili fizicke senzore.
#>
[CmdletBinding()]
param([switch]$DisposableVM, [switch]$AfterReboot, [string]$SourceCommit = '')
$ErrorActionPreference = 'Stop'
if (-not $DisposableVM) { throw 'Zahteva eksplicitni -DisposableVM i svezu VM sa snapshotom.' }
$machine = Get-CimInstance Win32_ComputerSystem
if (-not $machine.HypervisorPresent -or $machine.Model -notmatch 'Virtual|VMware|KVM|QEMU|HVM') { throw 'Namenska virtuelna masina nije detektovana.' }
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
if (-not (New-Object Security.Principal.WindowsPrincipal($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Zahteva administratora unutar VM.' }
$uiRoot = Split-Path $PSScriptRoot -Parent
$scriptRoot = Join-Path $uiRoot 'Skripte'
$taskName = 'WIS-VM-ResumeValidation'
$evidenceDir = 'C:\WIS-VM-Validation'
$evidencePath = Join-Path $evidenceDir 'result.json'
$markerPath = Join-Path $evidenceDir 'before-reboot.json'
Import-Module (Join-Path $scriptRoot 'Modules\WIS.Common.psm1') -Force
Import-Module (Join-Path $scriptRoot 'Modules\WIS.Cleanup.psm1') -Force
try {
    if (-not $AfterReboot) {
        if (Test-Path 'C:\setup') { throw 'C:\setup mora biti odsutan: koristite svezu VM.' }
        if (Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue) { throw 'Prethodni test jos postoji.' }
        New-Item $evidenceDir -ItemType Directory -Force | Out-Null
        & (Join-Path $PSScriptRoot 'Test-PowerShell51Parse.ps1')
        & (Join-Path $PSScriptRoot 'Test-StatusContract.ps1')
        if (-not $SourceCommit) {
            $SourceCommit = (& git -C (Split-Path $uiRoot -Parent) rev-parse HEAD).Trim()
            if ($LASTEXITCODE -ne 0) { throw 'Nije procitan source commit.' }
        }
        if ($SourceCommit -notmatch '^[0-9a-f]{40}$') { throw 'Potreban je pun source commit.' }
        New-Item 'C:\setup' -ItemType Directory | Out-Null
        $result = [PSCustomObject]@{ run_id = 'vm-unknown'; status = 'UNKNOWN'; sensor_available = $false; max_cpu_temp_c = 0; emergency_abort = $false; reason = 'SENSOR_UNAVAILABLE'; timestamp = (Get-Date -Format o) }
        Save-ServisStressTestResult -StressResult $result -Phase 6 -DeploymentStatus 'ACTIVE' -HandoverReady $true
        Save-DeployStatePhase -Phase 6
        $boot = (Get-CimInstance Win32_OperatingSystem).LastBootUpTime.ToString('o')
        @{ boot = $boot; started = (Get-Date -Format o); source_commit = $SourceCommit } | ConvertTo-Json | Set-Content $markerPath -Encoding UTF8
        # Namerno lazna tajna, nikad produkciona lozinka.
        Set-Content 'C:\setup\Wifi-Servis.ini' "SSID=Wifi-Servis-VM-Test`nPassword=VM-TEST-ONLY" -Encoding UTF8
        Set-Content 'C:\setup\wifi_profile.xml' '<vm-test-secret />' -Encoding UTF8
        $command = '-NoProfile -ExecutionPolicy Bypass -File "' + $PSCommandPath + '" -DisposableVM -AfterReboot'
        $action = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument $command
        $trigger = New-ScheduledTaskTrigger -AtStartup
        Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger -User 'SYSTEM' -RunLevel Highest -Force | Out-Null
        Restart-Computer -Force
        return
    }
    $before = Get-Content $markerPath -Raw | ConvertFrom-Json
    $boot = (Get-CimInstance Win32_OperatingSystem).LastBootUpTime.ToString('o')
    if ($boot -eq $before.boot) { throw 'Stvarni restart nije potvrdjen.' }
    if ((Get-DeployStatePhase) -ne 6) { throw 'Resume: izgubljena faza 6.' }
    $saved = Get-ServisStressTestResult
    if ($saved.run_id -ne 'vm-unknown' -or $saved.status -ne 'UNKNOWN') { throw 'Resume: izgubljen autoritativni rezultat.' }
    $ok = [bool](Clear-ServisWifiSecrets -LocalSetupDir 'C:\setup')
    if (-not $ok) { throw 'Anti-Leak nije verifikovan.' }
    foreach ($file in @('Wifi-Servis.ini', 'wifi_profile.xml')) {
        if (Test-Path (Join-Path 'C:\setup' $file)) { throw "Preostala tajna: $file" }
    }
    $state = Get-Content 'C:\setup\deploy-state.json' -Raw | ConvertFrom-Json
    if (-not $state.anti_leak_verified -or $state.cooling_verified) { throw 'Netacan zavrsni status.' }
    @{ status = 'PASS'; source_commit = $before.source_commit; restart_verified = $true; resume_phase = 6; anti_leak_verified = $true; cooling_verified = $false; timestamp = (Get-Date -Format o) } |
        ConvertTo-Json | Set-Content $evidencePath -Encoding UTF8
} catch {
    if (-not (Test-Path $evidenceDir)) { New-Item $evidenceDir -ItemType Directory | Out-Null }
    @{ status = 'FAIL'; error = $_.Exception.Message; timestamp = (Get-Date -Format o) } | ConvertTo-Json | Set-Content $evidencePath -Encoding UTF8
    throw
} finally {
    if ($AfterReboot) { Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue }
}
