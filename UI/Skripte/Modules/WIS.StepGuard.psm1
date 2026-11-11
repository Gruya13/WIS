<#
.MODULE
    WIS.StepGuard.psm1
.DESCRIPTION
    WIS v2.19.0 (Windows Installation Assistant) - StepGuard Mehanizam.
    Obezbeđuje pre-proveru, post-verifikaciju i inteligentno preskakanje već urađenih koraka
    u Fazama 2, 3 i 4 uz perzistenciju u C:\setup\step-journal.json i IPC notifikacije.
.COMPATIBILITY
    Windows PowerShell 5.1+ (Strogo kompatibilno, bez ?? i ?:)
#>

$Global:WisStepJournalPath = "C:\setup\step-journal.json"

function Invoke-WisScriptBlockWithTimeout {
    <#
    .SYNOPSIS
        Izvrsava scriptblock sa vremenskim ogranicenjem (timeout) u zasticenom okruzenju.
    #>
    param(
        [Parameter(Mandatory = $true)]
        [scriptblock]$ScriptBlock,
        [int]$TimeoutSec = 20
    )

    if ($TimeoutSec -le 0) {
        try {
            $directRes = & $ScriptBlock
            return @{ Success = $true; TimedOut = $false; Output = $directRes; Error = $null }
        } catch {
            return @{ Success = $false; TimedOut = $false; Output = $null; Error = $_ }
        }
    }

    $ps = [System.Management.Automation.PowerShell]::Create()
    try {
        $loadedModules = Get-Module -Name 'WIS.*' -ErrorAction SilentlyContinue
        if ($loadedModules) {
            foreach ($mod in $loadedModules) {
                if ($mod.Path -and (Test-Path -LiteralPath $mod.Path)) {
                    [void]$ps.AddCommand('Import-Module').AddParameter('Name', $mod.Path).AddParameter('Global', $true)
                }
            }
        }
        [void]$ps.AddScript($ScriptBlock.ToString())

        $asyncResult = $ps.BeginInvoke()
        $waitMs = [int]($TimeoutSec * 1000)
        $signaled = $asyncResult.AsyncWaitHandle.WaitOne($waitMs, $false)

        if ($signaled) {
            try {
                $out = $ps.EndInvoke($asyncResult)
                $effectiveOut = if ($out -and $out.Count -eq 1) { $out[0] } else { $out }
                return @{
                    Success  = $true
                    TimedOut = $false
                    Output   = $effectiveOut
                    Error    = $null
                }
            } catch {
                return @{
                    Success  = $false
                    TimedOut = $false
                    Output   = $null
                    Error    = $_
                }
            }
        } else {
            try { $ps.Stop() } catch {}
            return @{
                Success  = $false
                TimedOut = $true
                Output   = $null
                Error    = "Istekao timeout od ${TimeoutSec}s pri izvrsenju skriptbloka."
            }
        }
    } catch {
        # Fallback na direktno izvrsenje u slucaju nemogucnosti kreiranja PowerShell instance
        try {
            $fallbackRes = & $ScriptBlock
            return @{ Success = $true; TimedOut = $false; Output = $fallbackRes; Error = $null }
        } catch {
            return @{ Success = $false; TimedOut = $false; Output = $null; Error = $_ }
        }
    } finally {
        try { $ps.Dispose() } catch {}
    }
}

function Test-WisForceRerun {
    <#
    .SYNOPSIS
        Proverava da li je ukljucen zahtev za prinudno ponovno izvrsenje koraka (ForceRerun).
    .DESCRIPTION
        Samo cita konfiguraciju:
        1. C:\setup\force-rerun-phase<N>.flag ili C:\setup\force-rerun.flag (ili iz -FlagsDir)
        2. Kljuc ForceRerun / ForceRerunPhase<N> u sekciji [Servis] u Wifi-Servis.ini
    #>
    [CmdletBinding()]
    param(
        [int]$Phase = 0,
        [string]$FlagsDir = "C:\setup"
    )

    # 1. Provera flag datoteka
    $flagDirs = @($FlagsDir, "C:\setup") | Select-Object -Unique
    foreach ($fd in $flagDirs) {
        if ($fd -and (Test-Path $fd)) {
            if (Test-Path (Join-Path $fd "force-rerun.flag")) {
                return $true
            }
            if ($Phase -gt 0) {
                if (Test-Path (Join-Path $fd "force-rerun-phase$Phase.flag")) {
                    return $true
                }
            }
        }
    }

    # 2. Provera Wifi-Servis.ini (samo citanje, bez ikakvog upisa ili modifikacije)
    $iniCandidates = @(
        "C:\setup\Wifi-Servis.ini",
        "C:\setup\Skripte\Wifi-Servis.ini",
        (Join-Path $PSScriptRoot "Wifi-Servis.ini"),
        (Join-Path (Split-Path $PSScriptRoot -Parent) "Wifi-Servis.ini")
    )
    if ($Global:LocalSetupDir) {
        $iniCandidates += (Join-Path $Global:LocalSetupDir "Wifi-Servis.ini")
    }

    $iniPath = $iniCandidates | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
    if ($iniPath) {
        try {
            $lines = Get-Content -Path $iniPath -ErrorAction SilentlyContinue
            $inServisSection = $false
            foreach ($rawLine in $lines) {
                $line = $rawLine.Trim()
                if ($line.StartsWith("[") -and $line.EndsWith("]")) {
                    $secName = $line.Substring(1, $line.Length - 2).Trim()
                    $inServisSection = ($secName -ieq "Servis")
                    continue
                }
                if ($inServisSection -and $line -match '^\s*([^=]+)=(.*)$') {
                    $key = $matches[1].Trim()
                    $val = $matches[2].Trim()

                    $isForceKey = ($key -ieq "ForceRerun")
                    if (-not $isForceKey -and $Phase -gt 0) {
                        $isForceKey = ($key -ieq "ForceRerunPhase$Phase")
                    }

                    if ($isForceKey) {
                        if ($val -in @("1", "true", "True", "TRUE", "yes", "Yes", "da", "Da")) {
                            return $true
                        }
                    }
                }
            }
        } catch {}
    }

    return $false
}

function Get-WisPendingRebootState {
    <#
    .SYNOPSIS
        Proverava stanje operativnog sistema po pitanju restarta na cekanju (Pending Reboot).
    .DESCRIPTION
        Samo cita registar (nikada ne brise kljuceve!):
        - CBS RebootPending
        - Windows Update RebootRequired
        - PendingFileRenameOperations
        - Preimenovanje racunara na cekanju
        - UpdateExeVolatile
    #>
    [CmdletBinding()]
    param()

    $reasons = [System.Collections.Generic.List[string]]::new()
    $cbsPending = $false
    $wuPending = $false
    $fileRenamePending = $false
    $compRenamePending = $false
    $volatilePending = $false

    # 1. Component Based Servicing (CBS)
    try {
        $cbsKey = "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending"
        if (Test-Path $cbsKey) {
            $cbsPending = $true
            $reasons.Add("CBS RebootPending kljuc prisutan")
        }
    } catch {}

    # 2. Windows Update Auto Update
    try {
        $wuKey = "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired"
        if (Test-Path $wuKey) {
            $wuPending = $true
            $reasons.Add("Windows Update RebootRequired kljuc prisutan")
        }
    } catch {}

    # 3. Session Manager - PendingFileRenameOperations
    try {
        $smKey = "HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager"
        $val1 = (Get-ItemProperty -Path $smKey -Name "PendingFileRenameOperations" -ErrorAction SilentlyContinue).PendingFileRenameOperations
        if ($val1 -and $val1.Count -gt 0) {
            $fileRenamePending = $true
            $reasons.Add("Session Manager ima PendingFileRenameOperations")
        }
        $val2 = (Get-ItemProperty -Path $smKey -Name "PendingFileRenameOperations2" -ErrorAction SilentlyContinue).PendingFileRenameOperations2
        if ($val2 -and $val2.Count -gt 0) {
            $fileRenamePending = $true
            $reasons.Add("Session Manager ima PendingFileRenameOperations2")
        }
    } catch {}

    # 4. Computer Name Rename Pending
    try {
        $activeName = (Get-ItemProperty -Path "HKLM:\SYSTEM\CurrentControlSet\Control\ComputerName\ActiveComputerName" -Name "ComputerName" -ErrorAction SilentlyContinue).ComputerName
        $targetName = (Get-ItemProperty -Path "HKLM:\SYSTEM\CurrentControlSet\Control\ComputerName\ComputerName" -Name "ComputerName" -ErrorAction SilentlyContinue).ComputerName
        if ($activeName -and $targetName -and ($activeName -ine $targetName)) {
            $compRenamePending = $true
            $reasons.Add("Preimenovanje racunara na cekanju ($activeName -> $targetName)")
        }
    } catch {}

    # 5. UpdateExeVolatile
    try {
        $volVal = (Get-ItemProperty -Path "HKLM:\SOFTWARE\Microsoft\Updates" -Name "UpdateExeVolatile" -ErrorAction SilentlyContinue).UpdateExeVolatile
        if ($null -ne $volVal -and [int]$volVal -ne 0) {
            $volatilePending = $true
            $reasons.Add("UpdateExeVolatile vrednost aktivna ($volVal)")
        }
    } catch {}

    $isPending = ($reasons.Count -gt 0)

    return @{
        Pending = $isPending
        Reasons = @($reasons)
        Details = @{
            CbsRebootPending      = $cbsPending
            WuRebootRequired      = $wuPending
            PendingFileRename     = $fileRenamePending
            ComputerRenamePending = $compRenamePending
            UpdateExeVolatile     = $volatilePending
        }
    }
}

function Read-WisStepJournal {
    <#
    .SYNOPSIS
        Ucitava zurnal koraka sa diska (C:\setup\step-journal.json).
    #>
    [CmdletBinding()]
    param(
        [Alias("Path")]
        [string]$JournalPath = $Global:WisStepJournalPath
    )
    $Path = $JournalPath

    $defaultJournal = @{
        schema                  = 1
        steps                   = @{}
        interim_reboot_required = $null
    }

    if (-not (Test-Path -LiteralPath $Path)) {
        return $defaultJournal
    }

    try {
        $content = [System.IO.File]::ReadAllText($Path, [System.Text.Encoding]::UTF8)
        if ([string]::IsNullOrWhiteSpace($content)) {
            return $defaultJournal
        }

        $obj = ConvertFrom-Json -InputObject $content -ErrorAction Stop
        if (-not $obj) {
            return $defaultJournal
        }

        $journal = @{
            schema                  = if ($obj.schema) { [int]$obj.schema } else { 1 }
            steps                   = @{}
            interim_reboot_required = if ($obj.PSObject.Properties['interim_reboot_required']) { $obj.interim_reboot_required } else { $null }
        }

        if ($obj.steps) {
            foreach ($prop in $obj.steps.PSObject.Properties) {
                $stepVal = $prop.Value
                $journal.steps[$prop.Name] = @{
                    state       = [string]$stepVal.state
                    reason      = [string]$stepVal.reason
                    executed    = [bool]$stepVal.executed
                    ts          = [string]$stepVal.ts
                    duration_ms = if ($stepVal.duration_ms) { [int]$stepVal.duration_ms } else { 0 }
                }
            }
        }

        return $journal
    } catch {
        return $defaultJournal
    }
}

function Write-WisStepJournal {
    <#
    .SYNOPSIS
        Upisuje azurirani korak ili globalni status u zurnal (C:\setup\step-journal.json).
    #>
    [CmdletBinding()]
    param(
        [string]$StepId = "",
        [string]$State = "",
        [string]$Reason = "",
        [bool]$Executed = $false,
        [int]$DurationMs = 0,
        [object]$InterimRebootRequired = $null,
        [Alias("Path")]
        [string]$JournalPath = $Global:WisStepJournalPath
    )
    $Path = $JournalPath

    try {
        $dir = Split-Path -Path $Path -Parent
        if ($dir -and -not (Test-Path -LiteralPath $dir)) {
            New-Item -Path $dir -ItemType Directory -Force -ErrorAction SilentlyContinue | Out-Null
        }

        $journal = Read-WisStepJournal -Path $Path

        if ($StepId) {
            $journal.steps[$StepId] = @{
                state       = $State
                reason      = $Reason
                executed    = $Executed
                ts          = [DateTime]::UtcNow.ToString("o")
                duration_ms = $DurationMs
            }
        }

        if ($null -ne $InterimRebootRequired) {
            $journal.interim_reboot_required = [bool]$InterimRebootRequired
        }

        $json = ConvertTo-Json -InputObject $journal -Depth 6
        $utf8WithBom = New-Object System.Text.UTF8Encoding($true)
        [System.IO.File]::WriteAllText($Path, $json, $utf8WithBom)
        return $true
    } catch {
        return $false
    }
}

function Get-WisPhaseStepSummary {
    <#
    .SYNOPSIS
        Vraca rezime izvrsenih/preskocenih koraka za zadatu fazu iz zurnala.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [int]$Phase,
        [Alias("Path")]
        [string]$JournalPath = $Global:WisStepJournalPath
    )
    $Path = $JournalPath

    $journal = Read-WisStepJournal -Path $Path
    $prefix = "P$Phase."

    $phaseSteps = @{}
    $executedAny = $false
    $allSkipped = $true
    $countTotal = 0

    $counts = @{
        OK           = 0
        SKIPPED_DONE = 0
        SKIPPED_USER = 0
        WARN         = 0
        ERROR        = 0
        RUNNING      = 0
        WAIT         = 0
    }

    foreach ($k in $journal.steps.Keys) {
        if ($k.StartsWith($prefix)) {
            $countTotal++
            $item = $journal.steps[$k]
            $phaseSteps[$k] = $item

            if ($item.executed -eq $true) {
                $executedAny = $true
            }

            $st = $item.state
            if ($counts.ContainsKey($st)) {
                $counts[$st] = $counts[$st] + 1
            } else {
                $counts[$st] = 1
            }

            if ($st -ne "SKIPPED_DONE" -and $st -ne "SKIPPED_USER") {
                $allSkipped = $false
            }
        }
    }

    if ($countTotal -eq 0) {
        $allSkipped = $false
    }

    return @{
        Phase        = $Phase
        executed_any = $executedAny
        ExecutedAny  = $executedAny
        all_skipped  = $allSkipped
        AllSkipped   = $allSkipped
        total_steps  = $countTotal
        TotalSteps   = $countTotal
        counts       = $counts
        steps        = $phaseSteps
    }
}

function Invoke-WisStep {
    <#
    .SYNOPSIS
        Izvrsava korak faze sa StepGuard pre-proverom i post-verifikacijom.
    .DESCRIPTION
        Tok jednog koraka:
        1. ForceRerun provera: ako je aktivan, preskace se Test i ide se direktno na Action.
        2. Test blok: proverava da li je korak vec zavrsen (Done=true).
           - Ako jeste -> Korak dobija status SKIPPED_DONE i razlog.
           - Ako nije, ili baci izuzetak, ili istekne timeout -> Korak se izvrsava.
        3. Action blok: sprovodi radnju.
        4. Post-verifikacija: poziva se isti Test blok da potvrdi uspeh.
           - Ako prodje -> OK.
           - Ako ne prodje -> WARN ili ERROR (ako je -Critical).
        5. Upis u zurnal (step-journal.json) i slanje IPC step dogadjaja ka UI-ju.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [int]$Phase,
        [Parameter(Mandatory = $true)]
        [string]$StepId,
        [Parameter(Mandatory = $true)]
        [string]$Title,
        [Parameter(Mandatory = $true)]
        [scriptblock]$Test,
        [Parameter(Mandatory = $true)]
        [scriptblock]$Action,
        [switch]$Critical,
        [switch]$NoSkip,
        [int]$TimeoutSec = 20,
        [double]$JournalTtlHours = 0,
        [Alias("Path")]
        [string]$JournalPath = $Global:WisStepJournalPath,
        [string]$FlagsDir = "C:\setup"
    )

    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $forceRerun = Test-WisForceRerun -Phase $Phase -FlagsDir $FlagsDir
    $canSkip = (-not $forceRerun) -and (-not $NoSkip)

    # 1. Provera TTL-a u zurnalu ako je zadat (npr. Windows Update drajveri < 6h)
    if ($canSkip -and $JournalTtlHours -gt 0) {
        $currentJournal = Read-WisStepJournal -JournalPath $JournalPath
        if ($currentJournal.steps.ContainsKey($StepId)) {
            $pastEntry = $currentJournal.steps[$StepId]
            if ($pastEntry.state -eq "OK" -and $pastEntry.ts) {
                try {
                    $pastTime = [DateTime]::Parse($pastEntry.ts, [System.Globalization.CultureInfo]::InvariantCulture, [System.Globalization.DateTimeStyles]::RoundtripKind)
                    $diffHours = ([DateTime]::UtcNow - $pastTime.ToUniversalTime()).TotalHours
                    if ($diffHours -gt $JournalTtlHours) {
                        $canSkip = $false
                    }
                } catch {
                    $canSkip = $false
                }
            } else {
                $canSkip = $false
            }
        } else {
            $canSkip = $false
        }
    }

    # 2. Pre-provera (Test blok)
    if ($canSkip) {
        $testRes = Invoke-WisScriptBlockWithTimeout -ScriptBlock $Test -TimeoutSec $TimeoutSec
        $isDone = $false
        $testReason = ""

        if ($testRes.Success -and -not $testRes.TimedOut -and $null -ne $testRes.Output) {
            $outObj = $testRes.Output
            if ($outObj -is [hashtable] -or $outObj -is [System.Collections.IDictionary]) {
                if ($outObj.ContainsKey("Done")) { $isDone = [bool]$outObj["Done"] }
                elseif ($outObj.ContainsKey("Clean")) { $isDone = [bool]$outObj["Clean"] }
                elseif ($outObj.ContainsKey("Installed")) { $isDone = [bool]$outObj["Installed"] }

                if ($outObj.ContainsKey("Reason")) { $testReason = [string]$outObj["Reason"] }
                elseif ($outObj.ContainsKey("Message")) { $testReason = [string]$outObj["Message"] }
            } elseif ($outObj -is [PSCustomObject]) {
                if ($outObj.PSObject.Properties['Done']) { $isDone = [bool]$outObj.Done }
                elseif ($outObj.PSObject.Properties['Clean']) { $isDone = [bool]$outObj.Clean }
                elseif ($outObj.PSObject.Properties['Installed']) { $isDone = [bool]$outObj.Installed }

                if ($outObj.PSObject.Properties['Reason']) { $testReason = [string]$outObj.Reason }
                elseif ($outObj.PSObject.Properties['Message']) { $testReason = [string]$outObj.Message }
            } elseif ($outObj -is [bool]) {
                $isDone = $outObj
            }
        }

        if ($isDone) {
            $sw.Stop()
            $finalReason = if ($testReason) { $testReason } else { "Već urađeno" }

            Write-WisStepJournal -StepId $StepId -JournalPath $JournalPath -State "SKIPPED_DONE" -Reason $finalReason -Executed $false -DurationMs ([int]$sw.ElapsedMilliseconds)
            if (Get-Command Send-IpcStep -ErrorAction SilentlyContinue) {
                Send-IpcStep -Phase $Phase -StepId $StepId -Title $Title -State "SKIPPED_DONE" -Reason $finalReason
            }
            if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                Write-ServisLog "Korak $StepId ($Title): PRESKOČENO — već urađeno ($finalReason)." -Level "INFO" -Component "StepGuard"
            }

            return @{
                State      = "SKIPPED_DONE"
                Reason     = $finalReason
                Executed   = $false
                DurationMs = [int]$sw.ElapsedMilliseconds
            }
        }
    }

    # 3. Izvrsenje koraka (Action blok)
    if (Get-Command Send-IpcStep -ErrorAction SilentlyContinue) {
        Send-IpcStep -Phase $Phase -StepId $StepId -Title $Title -State "RUNNING" -Reason ""
    }
    if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
        Write-ServisLog "Izvršavam korak $StepId ($Title)..." -Level "STEP" -Component "StepGuard"
    }

    $actionSuccess = $true
    $actionError = ""

    try {
        & $Action
    } catch {
        $actionSuccess = $false
        $actionError = $_.Exception.Message
    }

    if (-not $actionSuccess) {
        $sw.Stop()
        $failState = if ($Critical) { "ERROR" } else { "WARN" }
        $failReason = "Greška pri izvršenju koraka: $actionError"

        Write-WisStepJournal -StepId $StepId -JournalPath $JournalPath -State $failState -Reason $failReason -Executed $true -DurationMs ([int]$sw.ElapsedMilliseconds)
        if (Get-Command Send-IpcStep -ErrorAction SilentlyContinue) {
            Send-IpcStep -Phase $Phase -StepId $StepId -Title $Title -State $failState -Reason $failReason
        }
        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
            $lvl = if ($Critical) { "ERROR" } else { "WARN" }
            Write-ServisLog "Korak $StepId ($Title) nije uspeo: $failReason" -Level $lvl -Component "StepGuard"
        }

        return @{
            State      = $failState
            Reason     = $failReason
            Executed   = $true
            DurationMs = [int]$sw.ElapsedMilliseconds
        }
    }

    # 4. Post-verifikacija (isti Test blok)
    $postRes = Invoke-WisScriptBlockWithTimeout -ScriptBlock $Test -TimeoutSec $TimeoutSec
    $postDone = $false
    $postReason = ""

    if ($postRes.Success -and -not $postRes.TimedOut -and $null -ne $postRes.Output) {
        $outObj = $postRes.Output
        if ($outObj -is [hashtable] -or $outObj -is [System.Collections.IDictionary]) {
            if ($outObj.ContainsKey("Done")) { $postDone = [bool]$outObj["Done"] }
            elseif ($outObj.ContainsKey("Clean")) { $postDone = [bool]$outObj["Clean"] }
            elseif ($outObj.ContainsKey("Installed")) { $postDone = [bool]$outObj["Installed"] }

            if ($outObj.ContainsKey("Reason")) { $postReason = [string]$outObj["Reason"] }
            elseif ($outObj.ContainsKey("Message")) { $postReason = [string]$outObj["Message"] }
        } elseif ($outObj -is [PSCustomObject]) {
            if ($outObj.PSObject.Properties['Done']) { $postDone = [bool]$outObj.Done }
            elseif ($outObj.PSObject.Properties['Clean']) { $postDone = [bool]$outObj.Clean }
            elseif ($outObj.PSObject.Properties['Installed']) { $postDone = [bool]$outObj.Installed }

            if ($outObj.PSObject.Properties['Reason']) { $postReason = [string]$outObj.Reason }
            elseif ($outObj.PSObject.Properties['Message']) { $postReason = [string]$outObj.Message }
        } elseif ($outObj -is [bool]) {
            $postDone = $outObj
        }
    }

    $sw.Stop()

    $finalState = "OK"
    $stepReasonResult = ""

    if ($postDone) {
        $finalState = "OK"
        $stepReasonResult = if ($postReason) { $postReason } else { "Uspešno izvršeno" }
    } else {
        $finalState = if ($Critical) { "ERROR" } else { "WARN" }
        $stepReasonResult = if ($postReason) { "Post-verifikacija nije uspela: $postReason" } else { "Post-verifikacija nije potvrdila završetak koraka" }
    }

    Write-WisStepJournal -StepId $StepId -JournalPath $JournalPath -State $finalState -Reason $stepReasonResult -Executed $true -DurationMs ([int]$sw.ElapsedMilliseconds)
    if (Get-Command Send-IpcStep -ErrorAction SilentlyContinue) {
        Send-IpcStep -Phase $Phase -StepId $StepId -Title $Title -State $finalState -Reason $stepReasonResult
    }
    if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
        $logLvl = if ($finalState -eq "OK") { "SUCCESS" } elseif ($finalState -eq "WARN") { "WARN" } else { "ERROR" }
        Write-ServisLog "Korak $StepId ($Title) završen sa statusom $finalState ($stepReasonResult)." -Level $logLvl -Component "StepGuard"
    }

    return @{
        State      = $finalState
        Reason     = $stepReasonResult
        Executed   = $true
        DurationMs = [int]$sw.ElapsedMilliseconds
    }
}

Export-ModuleMember -Function `
    Invoke-WisStep, `
    Test-WisForceRerun, `
    Get-WisPendingRebootState, `
    Read-WisStepJournal, `
    Write-WisStepJournal, `
    Get-WisPhaseStepSummary
