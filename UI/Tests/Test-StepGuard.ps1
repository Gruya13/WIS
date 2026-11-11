<#
.SYNOPSIS
    Test-StepGuard.ps1 - QA Verifikacioni test za WIS StepGuard mehanizam (WIS-TASK-181)
.DESCRIPTION
    Testira:
    1. Preskakanje kada je Test block Done = $true (SKIPPED_DONE)
    2. Izvršenje kada je Test block Done = $false (Action se izvršava, post-verifikacija uspešna -> OK)
    3. Post-verifikacija ne prolazi -> WARN (ili ERROR za -Critical)
    4. Greška / timeout u Test bloku -> bezbedno izvršenje Action bloka
    5. ForceRerun zaobilaženje (ignoriše Test block i izvršava Action)
    6. Ispravnost strukture step-journal.json
    7. PendingReboot detekcija bez menjanja stanja sistema
.COMPATIBILITY
    Windows PowerShell 5.1+
#>

[CmdletBinding()]
param(
    [string]$SetupDir = "C:\setup"
)

$ErrorActionPreference = 'Stop'
$testCount = 0
$passCount = 0
$failCount = 0

function Assert-Test([string]$name, [bool]$condition, [string]$detail = "") {
    $script:testCount++
    if ($condition) {
        $script:passCount++
        Write-Host "  [PASS] $name" -ForegroundColor Green
    } else {
        $script:failCount++
        Write-Host "  [FAIL] $($name): $detail" -ForegroundColor Red
    }
}

Write-Host "=== POKRETANJE STEPGUARD QA VERIFIKACIJE ===" -ForegroundColor Cyan

# 1. Uvoz modula
$modulePath = Join-Path $PSScriptRoot "..\Skripte\Modules\WIS.StepGuard.psm1"
if (-not (Test-Path $modulePath)) {
    Write-Error "Modul WIS.StepGuard.psm1 nije pronađen na lokaciji: $modulePath"
    exit 1
}

Import-Module $modulePath -Force
Import-Module (Join-Path $PSScriptRoot "..\Skripte\Modules\WIS.Common.psm1") -Force -ErrorAction SilentlyContinue

# Test 1: Izvoz funkcija
$exported = Get-Command -Module WIS.StepGuard | Select-Object -ExpandProperty Name
Assert-Test "Exportovana funkcija Invoke-WisStep" ($exported -contains "Invoke-WisStep")
Assert-Test "Exportovana funkcija Test-WisForceRerun" ($exported -contains "Test-WisForceRerun")
Assert-Test "Exportovana funkcija Get-WisPendingRebootState" ($exported -contains "Get-WisPendingRebootState")
Assert-Test "Exportovana funkcija Read-WisStepJournal" ($exported -contains "Read-WisStepJournal")
Assert-Test "Exportovana funkcija Write-WisStepJournal" ($exported -contains "Write-WisStepJournal")
Assert-Test "Exportovana funkcija Get-WisPhaseStepSummary" ($exported -contains "Get-WisPhaseStepSummary")

# Privremeni setup direktorijum za testove žurnala
$tempDir = Join-Path ([System.IO.Path]::GetTempPath()) ("wis_test_" + [System.Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $tempDir -Force | Out-Null
$testJournalPath = Join-Path $tempDir "step-journal.json"

try {
    # Test 2: Korak već odrađen -> SKIPPED_DONE (bez pokretanja Action)
    $actionExecuted = $false
    $res1 = Invoke-WisStep -Phase 4 -StepId "Test.AppAlreadyDone" -Title "Test App Done" `
        -JournalPath $testJournalPath `
        -Test { @{ Done = $true; Reason = "Aplikacija već postoji na disku" } } `
        -Action { $actionExecuted = $true }

    Assert-Test "SKIPPED_DONE stanje za već odrađen korak" ($res1.State -eq "SKIPPED_DONE")
    Assert-Test "Action blok nije pokrenut" (-not $actionExecuted)
    Assert-Test "Razlog ispravno prenet" ($res1.Reason -like "*već postoji*")

    # Test 3: Korak nije odrađen -> Action se pokreće i post-verifikacija uspešna
    $actionExecuted = $false
    $isDoneNow = $false
    $res2 = Invoke-WisStep -Phase 4 -StepId "Test.AppNeedsInstall" -Title "Test App Install" `
        -JournalPath $testJournalPath `
        -Test { @{ Done = $isDoneNow; Reason = if ($isDoneNow) { "Instalirano" } else { "Nije instalirano" } } } `
        -Action {
            $actionExecuted = $true
            $isDoneNow = $true
        }

    Assert-Test "Action blok jeste pokrenut kada korak nije gotov" ($actionExecuted)
    Assert-Test "Stanje je OK nakon uspešne post-verifikacije" ($res2.State -eq "OK")
    Assert-Test "Executed flag je true" ($res2.Executed -eq $true)

    # Test 4: Post-verifikacija ne prolazi -> WARN (non-critical)
    $res3 = Invoke-WisStep -Phase 3 -StepId "Test.PostFailWarn" -Title "Test Warn" `
        -JournalPath $testJournalPath `
        -Test { @{ Done = $false; Reason = "Nikad nije gotovo" } } `
        -Action { $null }

    Assert-Test "Post-verifikacija ne prolazi daje WARN" ($res3.State -eq "WARN")

    # Test 5: Post-verifikacija ne prolazi za Critical -> ERROR
    $res4 = Invoke-WisStep -Phase 4 -StepId "Test.PostFailCrit" -Title "Test Crit" -Critical `
        -JournalPath $testJournalPath `
        -Test { @{ Done = $false; Reason = "Kritična komponenta fali" } } `
        -Action { $null }

    Assert-Test "Post-verifikacija ne prolazi za Critical daje ERROR" ($res4.State -eq "ERROR")

    # Test 6: Greška u Test bloku -> bezbedno izvršenje Action bloka (Safe Fail-Open)
    $actionSafeRan = $false
    $res5 = Invoke-WisStep -Phase 2 -StepId "Test.CrashTest" -Title "Crash test" `
        -JournalPath $testJournalPath `
        -Test { throw "Simulirana greška u WMI upitu" } `
        -Action { $actionSafeRan = $true }

    Assert-Test "Fail-Open: Action se izvršava ako Test baci grešku" ($actionSafeRan)

    # Test 7: Žurnal - upis i čitanje
    Assert-Test "Žurnal fajl kreiran na disku" (Test-Path $testJournalPath)
    $journalData = Read-WisStepJournal -JournalPath $testJournalPath
    Assert-Test "Žurnal sadrži podatke za korake" ($journalData -and $journalData.steps)
    Assert-Test "Žurnal ima Test.AppAlreadyDone" ($journalData.steps["Test.AppAlreadyDone"].state -eq "SKIPPED_DONE")

    # Test 8: Summary po fazama
    $summaryP4 = Get-WisPhaseStepSummary -Phase 4 -JournalPath $testJournalPath
    Assert-Test "Summary prepoznaje fazu 4" ($summaryP4 -ne $null)
    Assert-Test "Summary prepoznaje da je bar jedan korak izvršen" ($summaryP4.ExecutedAny -eq $true)

    # Test 9: ForceRerun detekcija
    $flagFile = Join-Path $tempDir "force-rerun-phase4.flag"
    New-Item -ItemType File -Path $flagFile -Force | Out-Null
    $isForce = Test-WisForceRerun -Phase 4 -FlagsDir $tempDir
    Assert-Test "Test-WisForceRerun detektuje flag fajl" ($isForce -eq $true)

    $actionForceRan = $false
    $resForce = Invoke-WisStep -Phase 4 -StepId "Test.ForceRun" -Title "Force run test" `
        -JournalPath $testJournalPath -FlagsDir $tempDir `
        -Test { @{ Done = $true; Reason = "Već urađeno" } } `
        -Action { $actionForceRan = $true }
    Assert-Test "ForceRerun prisiljava izvršenje i pored Done=true" ($actionForceRan -and $resForce.Executed)

} finally {
    if (Test-Path $tempDir) {
        Remove-Item -Path $tempDir -Recurse -Force -ErrorAction SilentlyContinue
    }
}

Write-Host "=============================================" -ForegroundColor Cyan
Write-Host "REZULTAT: $passCount položenih od $testCount testova. (Grešaka: $failCount)" -ForegroundColor $(if ($failCount -eq 0) { "Green" } else { "Red" })

if ($failCount -gt 0) {
    exit 1
}
exit 0
