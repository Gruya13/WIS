<#
.MODULE
    WIS.Common.psm1
.DESCRIPTION
    Deljeni zajednicki modul za WIS (Windows Installation Assistant) v2.0.
    Standardizuje IPC JSON streaming komunikaciju sa UI Master procesom, kooperativnu pauzu,
    multi-target logovanje sa automatskom rotacijom, proveru administratorskih privilegija
    i perzistenciju globalnih promenljivih i stanja.
.COMPATIBILITY
    Windows PowerShell 5.1+
#>

Import-Module (Join-Path $PSScriptRoot 'WIS.StatusContract.psm1')

# ------------------------------------------------------------------------------
# 1. OSNOVNE GLOBALNE PUTANJE I PROMENLJIVE
# ------------------------------------------------------------------------------

$Global:LocalSetupDir      = "C:\setup"
$Global:LocalProgramiDir   = "C:\setup\Programi"
$Global:SystemLogDir       = "C:\Windows\Logs\WIS"
$Global:PublicDesktopLog   = "C:\Users\Public\Desktop\WIS_Izvestaj.log"
$Global:WIS_StateFile      = "C:\setup\deploy-state.json"
$Global:WIS_StressResultFile = "C:\setup\stress-test-result.json"
$Global:WIS_LogFile        = "C:\setup\servis_deploy.log"
$Global:WIS_JsonlLogFile   = "C:\setup\servis_deploy.jsonl"

$Global:WIS_UI_IsPaused             = $false
$Global:WIS_UI_SkipRequested        = $false
$Global:WIS_UI_DelayRestartRequested = $false

function Initialize-ServisPaths {
    [CmdletBinding()]
    param(
        [string]$SetupDir = "C:\setup",
        [string]$LogsDir = "C:\Windows\Logs\WIS"
    )
    try {
        if (-not (Test-Path $SetupDir)) {
            New-Item -Path $SetupDir -ItemType Directory -Force | Out-Null
        }
        if (-not (Test-Path $LogsDir)) {
            New-Item -Path $LogsDir -ItemType Directory -Force | Out-Null
        }
        $setupProgrami = Join-Path $SetupDir "Programi"
        if (-not (Test-Path $setupProgrami)) {
            New-Item -Path $setupProgrami -ItemType Directory -Force | Out-Null
        }
    } catch {
        # Tiho ignorisanje eventualnih gresaka pri kreiranju ako su folderi zakljucani
    }
}

# ------------------------------------------------------------------------------
# 2. PROVERA ADMINISTRATORSKIH PRIVILEGIJA
# ------------------------------------------------------------------------------

function Test-ServisAdministrator {
    <#
    .SYNOPSIS
        Proverava da li je trenutni PowerShell proces pokrenut sa punim administratorskim pravima.
    #>
    [CmdletBinding()]
    param()
    try {
        $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
        $principal = [Security.Principal.WindowsPrincipal]$identity
        return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    } catch {
        return $false
    }
}

function Assert-ServisAdministrator {
    <#
    .SYNOPSIS
        Zahteva administratorska prava za rad modula i prekida izvrsenje ako proces nije eleviran.
    #>
    [CmdletBinding()]
    param()
    if (-not (Test-ServisAdministrator)) {
        $errMsg = "KRITICNA GRESKA: Skripta zahteva administratorske privilegije (Run as Administrator)!"
        Write-ServisLog -Message $errMsg -Level "ERROR" -Component "Security"
        throw $errMsg
    }
}

# ------------------------------------------------------------------------------
# 3. IPC JSON STREAMING FUNKCIJE (STDOUT KOMUNIKACIJA SA GUI-JEM)
# ------------------------------------------------------------------------------

function Send-IpcMessage {
    <#
    .SYNOPSIS
        Emituje JSON formatiranu poruku na standardni izlaz (stdout) za WIS (Windows Installation Assistant).UI proces.
    .DESCRIPTION
        Sve poruke se serijalizuju kao jednocifreni kompresovani JSON string.
        Metoda je zasticena od izuzetaka ukoliko je stdout pipe zatvoren ili nedostupan.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory=$true)]
        [object]$Data
    )
    try {
        $json = ConvertTo-Json -InputObject $Data -Compress -Depth 6
        if ($null -ne [Console]::Out) {
            [Console]::Out.WriteLine($json)
            [Console]::Out.Flush()
        } else {
            Write-Output $json
        }
    } catch {
        # Nikada ne prekidati proces ako je IO tok zatvoren
    }
}

function Send-IpcLog {
    <#
    .SYNOPSIS
        Emituje log poruku preko JSON streaminga ka UI-ju i opciono upisuje u servis_deploy.log.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory=$true)]
        [string]$Message,
        [ValidateSet("INFO", "STEP", "SUCCESS", "WARN", "ERROR", "FATAL", "DEBUG")]
        [string]$Level = "INFO",
        [string]$Tag = "[WIS]",
        [string]$Component = "General",
        [int]$Phase = 0,
        [switch]$SkipFileLog
    )
    $timeStr = (Get-Date).ToString("HH:mm:ss")
    $payload = [ordered]@{
        log = [ordered]@{
            time    = $timeStr
            level   = $Level
            tag     = $Tag
            message = $Message
        }
        live_status = $Message
    }
    if ($Phase -gt 0) {
        $payload["phase"] = $Phase
    }

    Send-IpcMessage -Data $payload

    if (-not $SkipFileLog) {
        Write-ServisLog -Message $Message -Level $Level -Component $Component -Phase $(if ($Phase -gt 0) { "Phase$Phase" } else { "" }) -NoIpc
    }
}

function Send-IpcProgress {
    <#
    .SYNOPSIS
        Emituje azurirani progres i status trenutne faze ka UI Master Shell-u.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory=$true)]
        [int]$Progress,
        [int]$Phase = 0,
        [string]$PhaseName = "",
        [string]$SubPhase = "",
        [string]$StatusMessage = ""
    )
    $payload = [ordered]@{
        progress         = $Progress
        progress_percent = $Progress
        worker_progress  = $Progress
    }
    if ($Phase -gt 0) {
        $payload["phase"] = $Phase
    }
    if ($PhaseName) {
        $payload["phase_name"] = $PhaseName
    }
    if ($SubPhase) {
        $payload["sub_phase"] = $SubPhase
    }
    if ($StatusMessage) {
        $payload["live_status"]    = $StatusMessage
        $payload["status_message"] = $StatusMessage
    }

    Send-IpcMessage -Data $payload
}

function Send-IpcStatus {
    <#
    .SYNOPSIS
        Sveobuhvatno azuriranje stanja procesa (hardver, mreza, aplikacije, dijagnostika, status predaje).
    #>
    [CmdletBinding()]
    param(
        [string]$StatusMessage = "",
        [string]$SubPhase = "",
        [int]$Progress = -1,
        [int]$Phase = 0,
        [string]$DeploymentStatus = "",
        $HandoverReady = $null,
        $StressResult = $null,
        $Apps = $null,
        $Network = $null,
        $Diag = $null,
        $Hw = $null,
        [switch]$UsbFree
    )
    $payload = [ordered]@{}
    if ($Phase -gt 0) {
        $payload["phase"] = $Phase
    }
    if ($Progress -ge 0) {
        $payload["progress"]         = $Progress
        $payload["progress_percent"] = $Progress
        $payload["worker_progress"]  = $Progress
    }
    if ($SubPhase) {
        $payload["sub_phase"] = $SubPhase
    }
    if ($StatusMessage) {
        $payload["live_status"]    = $StatusMessage
        $payload["status_message"] = $StatusMessage
    }
    if ($DeploymentStatus) {
        $payload["deployment_status"] = $DeploymentStatus
    }
    if ($null -ne $HandoverReady) {
        $payload["handover_ready"] = [bool]$HandoverReady
    }
    if ($null -ne $StressResult) {
        $payload["stress_result"] = $StressResult
    }
    if ($UsbFree) {
        $payload["usb_free"] = $true
    }
    if ($null -ne $Apps) {
        $payload["apps"] = $Apps
    }
    if ($null -ne $Network) {
        $payload["network"] = $Network
    }
    if ($null -ne $Diag) {
        $payload["diag"] = $Diag
    }
    if ($null -ne $Hw) {
        $payload["hw"] = $Hw
    }

    Send-IpcMessage -Data $payload
}

# ------------------------------------------------------------------------------
# 4. KOOPERATIVNA PAUZA I SIGURNE KONTROLNE TACKE
# ------------------------------------------------------------------------------

function Wait-ServisUICooperativePause {
    <#
    .SYNOPSIS
        Proverava prisustvo kontrolnih signala (.pause / .skip) i zadrzava izvrsenje
        skripte na bezbedan nacin bez naglog prekidanja tekuce operacije.
    #>
    [CmdletBinding()]
    param(
        [string]$CheckpointName = "Kontrolna tacka"
    )

    # 1. Provera kontrolnih markera na disku
    if (Test-Path "C:\setup\.pause") {
        $Global:WIS_UI_IsPaused = $true
    }
    if (Test-Path "C:\setup\.skip") {
        $Global:WIS_UI_SkipRequested = $true
        Remove-Item -Path "C:\setup\.skip" -Force -ErrorAction SilentlyContinue
    }

    # 2. Ulazak u petlju cekanja ukoliko je aktivirana pauza
    if ($Global:WIS_UI_IsPaused) {
        Write-ServisLog -Message "Kooperativna pauza aktivirana na bezbednoj tacki: $CheckpointName. Cekam na nastavak..." -Level "WARN" -Component "CoopPause"
        Send-IpcProgress -Progress -1 -StatusMessage "PAUZIRANO ($CheckpointName) - Cekam nastavak..."

        while ($Global:WIS_UI_IsPaused) {
            Start-Sleep -Milliseconds 500

            # Provera uklanjanja .pause markera
            if (-not (Test-Path "C:\setup\.pause")) {
                $Global:WIS_UI_IsPaused = $false
                Write-ServisLog -Message "Marker C:\setup\.pause uklonjen. Nastavljam rad na: $CheckpointName" -Level "SUCCESS" -Component "CoopPause"
                Send-IpcProgress -Progress -1 -StatusMessage "Nastavak rada ($CheckpointName)..."
                break
            }
        }
    }
}

function Test-ServisSkipRequested {
    <#
    .SYNOPSIS
        Proverava da li je korisnik zatražio preskakanje trenutnog koraka i resetuje flag.
    #>
    [CmdletBinding()]
    param()
    if (Test-Path "C:\setup\.skip") {
        $Global:WIS_UI_SkipRequested = $true
        Remove-Item -Path "C:\setup\.skip" -Force -ErrorAction SilentlyContinue
    }
    if ($Global:WIS_UI_SkipRequested) {
        $Global:WIS_UI_SkipRequested = $false
        return $true
    }
    return $false
}

# ------------------------------------------------------------------------------
# 5. LOGGING ENGINE, SANITIZACIJA I ROTACIJA LOGOVA
# ------------------------------------------------------------------------------

function Format-ExitCodeDescription {
    param([int]$ExitCode)
    switch ($ExitCode) {
        0           { return "0 (Uspesno)" }
        3010        { return "3010 (Uspesno - zahteva restart)" }
        1602        { return "1602 (Korisnik je prekinuo instalaciju)" }
        1603        { return "1603 (Fatalna greska instalera - prava, zauzetost ili nedostajuca zavisnost)" }
        1618        { return "1618 (Druga instalacija je vec u toku - Windows Installer lock)" }
        1619        { return "1619 (Instalacioni paket se ne moze otvoriti)" }
        1638        { return "1638 (Druga verzija ovog proizvoda je vec instalirana)" }
        -1073741819 { return "0xC0000005 (Access Violation u instaleru)" }
        default     { return "$ExitCode" }
    }
}

function Rotate-ServisLog {
    <#
    .SYNOPSIS
        Vrši rotaciju log datoteke ukoliko je prešla zadatu maksimalnu veličinu.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory=$true)]
        [string]$Path,
        [int]$MaxSizeBytes = 10485760, # 10 MB
        [int]$MaxBackups = 3
    )
    try {
        if (Test-Path $Path) {
            $item = Get-Item -Path $Path -ErrorAction SilentlyContinue
            if ($item -and $item.Length -ge $MaxSizeBytes) {
                # Rotacija unazad: log.2 -> log.3, log.1 -> log.2, log -> log.1
                for ($idx = $MaxBackups - 1; $idx -ge 1; $idx--) {
                    $src = "$Path.$idx"
                    $dst = "$Path.$($idx + 1)"
                    if (Test-Path $src) {
                        Move-Item -Path $src -Destination $dst -Force -ErrorAction SilentlyContinue
                    }
                }
                Move-Item -Path $Path -Destination "$Path.1" -Force -ErrorAction SilentlyContinue
            }
        }
    } catch {
        # Rotacija ne sme nikada prekinuti rad skripte
    }
}

function Write-ServisLog {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory=$true)]
        [string]$Message,
        [ValidateSet("INFO", "STEP", "SUCCESS", "WARN", "ERROR", "FATAL", "DEBUG")]
        [string]$Level = "INFO",
        [string]$Component = "General",
        [string]$Phase = "",
        $Data = $null,
        [switch]$NoIpc
    )

    # 1. Sanitizacija lozinki i tajni iz logova (Anti-Leak)
    $sanitizedMsg = $Message
    if ($sanitizedMsg -match '(?i)(Password\s*=\s*)([^\s;,\r\n]+)') {
        $sanitizedMsg = $sanitizedMsg -replace '(?i)(Password\s*=\s*)([^\s;,\r\n]+)', '$1***MASKIRANO***'
    }
    if ($sanitizedMsg -match '(?i)(key\s*=\s*)([^\s;,\r\n]+)') {
        $sanitizedMsg = $sanitizedMsg -replace '(?i)(key\s*=\s*)([^\s;,\r\n]+)', '$1***MASKIRANO***'
    }

    $now = Get-Date
    $ts = $now.ToString("yyyy-MM-dd HH:mm:ss.fff")
    $isoTs = $now.ToString("o")
    $logLine = "[$ts] [$Level] [$Component] $sanitizedMsg"

    if (-not $Global:LogPaths) {
        $Global:LogPaths = New-Object System.Collections.Generic.List[string]
        $Global:LogPaths.Add("C:\setup\servis_deploy.log")
        $Global:LogPaths.Add("C:\Windows\Logs\WIS\servis_deploy.log")
        $Global:LogPaths.Add("C:\Users\Public\Desktop\WIS_Izvestaj.log")
    }

    if (-not $Global:JsonlLogPaths) {
        $Global:JsonlLogPaths = New-Object System.Collections.Generic.List[string]
        $Global:JsonlLogPaths.Add("C:\setup\servis_deploy.jsonl")
        $Global:JsonlLogPaths.Add("C:\Windows\Logs\WIS\servis_deploy.jsonl")
    }

    # 2. Rotacija i upis u tekstualne logove
    foreach ($p in $Global:LogPaths) {
        if ($p) {
            try {
                $dir = Split-Path $p -Parent
                if (-not (Test-Path $dir)) { New-Item -Path $dir -ItemType Directory -Force | Out-Null }
                Rotate-ServisLog -Path $p
                [System.IO.File]::AppendAllText($p, $logLine + [Environment]::NewLine, [System.Text.Encoding]::UTF8)
            } catch {}
        }
    }

    # 3. Upis u strukturirani JSON Lines (JSONL) log
    try {
        $jsonObj = [ordered]@{
            timestamp = $isoTs
            level     = $Level
            component = $Component
            phase     = if ($Phase) { $Phase } else { if ($Global:CurrentPhase) { "Phase$($Global:CurrentPhase)" } else { "Init" } }
            message   = $sanitizedMsg
        }
        if ($null -ne $Data) {
            $jsonObj["data"] = $Data
        }
        $jsonLine = ConvertTo-Json $jsonObj -Compress -Depth 5

        foreach ($jp in $Global:JsonlLogPaths) {
            if ($jp) {
                try {
                    $jDir = Split-Path $jp -Parent
                    if (-not (Test-Path $jDir)) { New-Item -Path $jDir -ItemType Directory -Force | Out-Null }
                    Rotate-ServisLog -Path $jp
                    [System.IO.File]::AppendAllText($jp, $jsonLine + [Environment]::NewLine, [System.Text.Encoding]::UTF8)
                } catch {}
            }
        }
    } catch {}

    # 4. IPC prenos loga ka UI Master Shell-u
    if (-not $NoIpc) {
        $tag = "[$Component]"
        Send-IpcLog -Message $sanitizedMsg -Level $Level -Tag $tag -Component $Component -SkipFileLog
    }
}

function Write-DiagnosticEvent {
    param(
        [Parameter(Mandatory=$true)][string]$Event,
        [string]$App = "",
        $ExitCode = $null,
        $DurationSec = $null,
        [string]$Status = "",
        $Data = $null,
        [ValidateSet("INFO", "STEP", "SUCCESS", "WARN", "ERROR", "FATAL", "DEBUG")][string]$Level = "INFO",
        [string]$Component = "Telemetry",
        [string]$Message = ""
    )
    $now = Get-Date
    $isoTs = $now.ToString("o")

    $evtObj = [ordered]@{
        timestamp    = $isoTs
        event        = $Event
        level        = $Level
        component    = $Component
        phase        = if ($Global:CurrentPhase) { "Phase$($Global:CurrentPhase)" } else { "Init" }
    }
    if ($App) { $evtObj["app"] = $App }
    if ($null -ne $ExitCode) { $evtObj["exit_code"] = [int]$ExitCode }
    if ($null -ne $DurationSec) { $evtObj["duration_sec"] = [double]$DurationSec }
    if ($Status) { $evtObj["status"] = $Status }
    if ($Message) { $evtObj["message"] = $Message }
    if ($null -ne $Data) { $evtObj["data"] = $Data }

    try {
        $jsonLine = ConvertTo-Json $evtObj -Compress -Depth 5
        if (-not $Global:JsonlLogPaths) {
            $Global:JsonlLogPaths = New-Object System.Collections.Generic.List[string]
            $Global:JsonlLogPaths.Add("C:\setup\servis_deploy.jsonl")
            $Global:JsonlLogPaths.Add("C:\Windows\Logs\WIS\servis_deploy.jsonl")
        }
        foreach ($jp in $Global:JsonlLogPaths) {
            if ($jp) {
                try {
                    $jDir = Split-Path $jp -Parent
                    if (-not (Test-Path $jDir)) { New-Item -Path $jDir -ItemType Directory -Force | Out-Null }
                    Rotate-ServisLog -Path $jp
                    [System.IO.File]::AppendAllText($jp, $jsonLine + [Environment]::NewLine, [System.Text.Encoding]::UTF8)
                } catch {}
            }
        }
    } catch {}

    if ($Message) {
        Write-ServisLog -Message $Message -Level $Level -Component $Component
    }
}

function Invoke-LoggedProcess {
    <#
    .SYNOPSIS
        Izvršava proces uz merenje vremena trajanja, precizno logovanje i beleženje dijagnostičkih događaja.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory=$true)][string]$FilePath,
        [string]$ArgumentList = "",
        [string]$WorkingDirectory = "",
        [switch]$Wait,
        [switch]$Hidden,
        [string]$Component = "Process",
        [string]$AppName = ""
    )
    $startMsg = "Pokrecem: `"$FilePath`" $ArgumentList"
    if ($WorkingDirectory) { $startMsg += " (Radni dir: `"$WorkingDirectory`")" }
    Write-ServisLog -Message $startMsg -Level "DEBUG" -Component $Component
    
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    try {
        $p = Start-Process -FilePath $FilePath -ArgumentList $ArgumentList -WorkingDirectory $WorkingDirectory -WindowStyle $(if ($Hidden) { 'Hidden' } else { 'Normal' }) -PassThru
        if ($Wait -and $p) {
            $p.WaitForExit()
            $sw.Stop()
            $durSec = [math]::Round($sw.Elapsed.TotalSeconds, 2)
            $code = $p.ExitCode
            $codeDesc = Format-ExitCodeDescription -ExitCode $code
            $lvl = if ($code -eq 0 -or $code -eq 3010) { "SUCCESS" } else { "WARN" }
            $finMsg = "Zavrsen proces `"$FilePath`" za ${durSec}s. ExitCode: $codeDesc"
            Write-ServisLog -Message $finMsg -Level $lvl -Component $Component
            
            if ($AppName) {
                $statusStr = if ($code -eq 0) { "INSTALIRANO" } elseif ($code -eq 3010) { "INSTALIRANO (RESTART)" } else { "GRESKA" }
                Write-DiagnosticEvent -Event "PROCESS_FINISHED" -App $AppName -ExitCode $code -DurationSec $durSec -Status $statusStr -Component $Component
            }
        }
        return $p
    } catch {
        $sw.Stop()
        Write-ServisLog -Message "Greska pri izvrsenju `"$FilePath`": $($_.Exception.Message)" -Level "ERROR" -Component $Component
        if ($AppName) {
            Write-DiagnosticEvent -Event "PROCESS_FAILED" -App $AppName -ExitCode -1 -Status "GRESKA" -Message $_.Exception.Message -Level "ERROR" -Component $Component
        }
        return $null
    }
}

# ------------------------------------------------------------------------------
# 6. STANJE I PERZISTENCIJA
# ------------------------------------------------------------------------------

function Initialize-ServisStartTime {
    <#
    .SYNOPSIS
        Proverava i inicijalizuje initial_start_time (ISO 8601 UTC) u C:\setup\deploy-state.json i HKLM:\SOFTWARE\WIS\InitialStartTime.
        Ukoliko initial_start_time vec postoji, nikada ga ne prebrisuje pri restartu.
    #>
    [CmdletBinding()]
    param(
        [string]$StateFile = "C:\setup\deploy-state.json",
        [string]$RegPath = "HKLM:\SOFTWARE\WIS"
    )
    $startTimeIso = $null
    $existingMap = [ordered]@{}

    # 1. Provera u deploy-state.json
    try {
        if (Test-Path $StateFile) {
            $raw = Get-Content -Path $StateFile -Raw -Encoding UTF8 -ErrorAction SilentlyContinue
            if ($raw) {
                $obj = ConvertFrom-Json -InputObject $raw -ErrorAction SilentlyContinue
                if ($obj) {
                    foreach ($p in $obj.PSObject.Properties) {
                        $existingMap[$p.Name] = $p.Value
                    }
                    if ($obj.initial_start_time -and -not [string]::IsNullOrWhiteSpace("$($obj.initial_start_time)")) {
                        $startTimeIso = "$($obj.initial_start_time)".Trim()
                    }
                }
            }
        }
    } catch {}

    # 2. Provera u Registry HKLM:\SOFTWARE\WIS\InitialStartTime
    if (-not $startTimeIso) {
        try {
            if (Test-Path $RegPath) {
                $regVal = (Get-ItemProperty -Path $RegPath -Name "InitialStartTime" -ErrorAction SilentlyContinue).InitialStartTime
                if ($regVal -and -not [string]::IsNullOrWhiteSpace("$regVal")) {
                    $startTimeIso = "$regVal".Trim()
                }
            }
        } catch {}
    }

    # 3. Ako nigde ne postoji, kreiraj novo UTC vreme
    $needsJsonWrite = $false
    if (-not $startTimeIso) {
        $startTimeIso = [DateTime]::UtcNow.ToString("o")
        $needsJsonWrite = $true
    } elseif (-not $existingMap.Contains("initial_start_time") -or "$($existingMap["initial_start_time"])" -ne $startTimeIso) {
        $needsJsonWrite = $true
    }

    # 4. Perzistencija u HKLM:\SOFTWARE\WIS\InitialStartTime (ako vec nije upisano)
    try {
        if (-not (Test-Path $RegPath)) {
            New-Item -Path $RegPath -Force -ErrorAction SilentlyContinue | Out-Null
        }
        $curReg = (Get-ItemProperty -Path $RegPath -Name "InitialStartTime" -ErrorAction SilentlyContinue).InitialStartTime
        if (-not $curReg) {
            New-ItemProperty -Path $RegPath -Name "InitialStartTime" -Value $startTimeIso -PropertyType String -Force -ErrorAction SilentlyContinue | Out-Null
        }
    } catch {}

    # 5. Perzistencija u C:\setup\deploy-state.json (bez brisanja postojecih polja)
    if ($needsJsonWrite) {
        try {
            $setupDir = Split-Path $StateFile -Parent
            if ($setupDir -and -not (Test-Path $setupDir)) {
                New-Item -Path $setupDir -ItemType Directory -Force | Out-Null
            }
            $existingMap["initial_start_time"] = $startTimeIso
            if (-not $existingMap.Contains("phase")) {
                $existingMap["phase"] = 1
            }
            if (-not $existingMap.Contains("timestamp")) {
                $existingMap["timestamp"] = (Get-Date).ToString("o")
            }
            if (-not $existingMap.Contains("computer_name")) {
                $existingMap["computer_name"] = if ($env:COMPUTERNAME) { $env:COMPUTERNAME } else { "WIS-PC" }
            }
            $json = ConvertTo-Json -InputObject $existingMap -Compress
            $utf8NoBom = New-Object System.Text.UTF8Encoding($false)
            [System.IO.File]::WriteAllText($StateFile, $json, $utf8NoBom)
        } catch {}
    }

    $Global:WIS_InitialStartTime = $startTimeIso
    return $startTimeIso
}

function Save-DeployStatePhase {
    <#
    .SYNOPSIS
        Usklađuje i perzistira broj faze u C:\setup\deploy-state.json uz očuvanje initial_start_time i ostalih polja.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory=$true)][int]$Phase
    )
    try {
        if (-not (Test-Path "C:\setup")) {
            New-Item -Path "C:\setup" -ItemType Directory -Force | Out-Null
        }
        $sf = "C:\setup\deploy-state.json"
        $stateMap = [ordered]@{}
        if (Test-Path $sf) {
            try {
                $raw = Get-Content -Path $sf -Raw -Encoding UTF8 -ErrorAction SilentlyContinue
                if ($raw) {
                    $obj = ConvertFrom-Json -InputObject $raw -ErrorAction SilentlyContinue
                    if ($obj) {
                        foreach ($p in $obj.PSObject.Properties) {
                            $stateMap[$p.Name] = $p.Value
                        }
                    }
                }
            } catch {}
        }
        if (-not $stateMap.Contains("initial_start_time") -or [string]::IsNullOrWhiteSpace("$($stateMap["initial_start_time"])")) {
            $initTime = Initialize-ServisStartTime -StateFile $sf
            if ($initTime) {
                $stateMap["initial_start_time"] = $initTime
            }
        }
        $stateMap["phase"]         = $Phase
        $stateMap["timestamp"]     = (Get-Date).ToString("o")
        $stateMap["computer_name"] = if ($env:COMPUTERNAME) { $env:COMPUTERNAME } else { "WIS-PC" }

        $json = ConvertTo-Json -InputObject $stateMap -Compress
        $utf8NoBom = New-Object System.Text.UTF8Encoding($false)
        [System.IO.File]::WriteAllText($sf, $json, $utf8NoBom)
    } catch {
        Write-ServisLog -Message "Upozorenje pri upisu deploy-state.json: $($_.Exception.Message)" -Level "WARN" -Component "State"
    }
}

function Get-DeployStatePhase {
    <#
    .SYNOPSIS
        Očitava trenutno zabeleženu fazu iz C:\setup\deploy-state.json.
    #>
    [CmdletBinding()]
    param()
    $sf = "C:\setup\deploy-state.json"
    if (Test-Path $sf) {
        try {
            $raw = Get-Content -Path $sf -Raw -ErrorAction SilentlyContinue
            if ($raw) {
                $obj = ConvertFrom-Json -InputObject $raw -ErrorAction SilentlyContinue
                if ($obj) {
                    if ($null -ne $obj.phase) { return [int]$obj.phase }
                    if ($null -ne $obj.Phase) { return [int]$obj.Phase }
                }
            }
        } catch {}
    }
    return 1
}

function Get-ServisStressTestResult {
    <#
    .SYNOPSIS
        Očitava autoritativni rezultat termalnog stres testa iz C:\setup\stress-test-result.json.
        Vraća $null ukoliko fajl ne postoji ili je nečitljiv/nevalidan.
    #>
    [CmdletBinding()]
    param(
        [string]$LocalSetupDir = "C:\setup"
    )
    $resFile = Join-Path $LocalSetupDir "stress-test-result.json"
    if (-not (Test-Path $resFile)) {
        return $null
    }
    try {
        $raw = Get-Content -Path $resFile -Raw -Encoding UTF8 -ErrorAction Stop
        if ([string]::IsNullOrWhiteSpace($raw)) {
            return $null
        }
        $parsed = ConvertFrom-Json -InputObject $raw -ErrorAction Stop
        if ($null -eq $parsed -or [string]::IsNullOrWhiteSpace("$($parsed.status)")) {
            return $null
        }
        return $parsed
    } catch {
        return $null
    }
}

function Save-ServisStressTestResult {
    <#
    .SYNOPSIS
        Perzistira autoritativni ugovor rezultata stres testa u C:\setup\stress-test-result.json
        i C:\setup\deploy-state.json uz očuvanje istorije prethodnih pokušaja (stress_history).
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory=$true)]
        $StressResult,
        [string]$LocalSetupDir = "C:\setup",
        $Phase = $null,
        [string]$DeploymentStatus = "",
        $HandoverReady = $null
    )
    try {
        if (-not (Test-Path $LocalSetupDir)) {
            New-Item -Path $LocalSetupDir -ItemType Directory -Force | Out-Null
        }

        $resFile = Join-Path $LocalSetupDir "stress-test-result.json"
        $stateFile = Join-Path $LocalSetupDir "deploy-state.json"

        $stateObj = [ordered]@{}
        if (Test-Path $stateFile) {
            try {
                $rawState = Get-Content -Path $stateFile -Raw -Encoding UTF8 -ErrorAction SilentlyContinue
                if ($rawState) {
                    $parsedState = ConvertFrom-Json -InputObject $rawState -ErrorAction SilentlyContinue
                    if ($parsedState) {
                        foreach ($p in $parsedState.PSObject.Properties) {
                            $stateObj[$p.Name] = $p.Value
                        }
                    }
                }
            } catch {}
        }

        $existingResObj = $null
        if (Test-Path $resFile) {
            try {
                $rawRes = Get-Content -Path $resFile -Raw -Encoding UTF8 -ErrorAction SilentlyContinue
                if ($rawRes) {
                    $existingResObj = ConvertFrom-Json -InputObject $rawRes -ErrorAction SilentlyContinue
                }
            } catch {}
        }

        $curRunId = if ($StressResult.run_id) { "$($StressResult.run_id)" } else { [Guid]::NewGuid().ToString("N") }
        $curTimestamp = if ($StressResult.timestamp) { "$($StressResult.timestamp)" } else { (Get-Date -Format "o") }
        $curStatus = if ($StressResult.status) { "$($StressResult.status)".ToUpper().Trim() } else { "UNKNOWN" }
        $curReason = if ($StressResult.reason) { "$($StressResult.reason)".Trim() } else { "SENSOR_UNAVAILABLE" }
        $curEmergency = [bool]$StressResult.emergency_abort
        $curSensorAvail = [bool]$StressResult.sensor_available
        $curSensorSrc = if ($StressResult.sensor_source) { "$($StressResult.sensor_source)" } else { "NONE" }
        $curDurSec = if ($null -ne $StressResult.duration_sec) { [int]$StressResult.duration_sec } elseif ($null -ne $StressResult.duration_seconds) { [int]$StressResult.duration_seconds } else { 0 }
        $curMaxCpu = if ($null -ne $StressResult.max_cpu_temp_c) { [double]$StressResult.max_cpu_temp_c } elseif ($null -ne $StressResult.max_cpu_temp) { [double]$StressResult.max_cpu_temp } else { 0.0 }
        $curMaxGpu = if ($null -ne $StressResult.max_gpu_temp_c) { [double]$StressResult.max_gpu_temp_c } elseif ($null -ne $StressResult.max_gpu_temp) { [double]$StressResult.max_gpu_temp } else { 0.0 }
        $curThrottling = [bool]$StressResult.throttling_detected
        $curReport = if ($StressResult.stress_report) { "$($StressResult.stress_report)" } else { "$curStatus ($curReason)" }
        $curDiagWarn = if ($StressResult.diagnostics_warning) { "$($StressResult.diagnostics_warning)" } elseif ($curStatus -eq "UNKNOWN" -or -not $curSensorAvail) { "Senzori temperature nisu dostupni na hardveru" } else { "" }

        # Rekonstrukcija i dopuna stress_history niza kako se prethodni pokušaji (posebno kritični) ne bi prebrisali
        $historyList = [System.Collections.ArrayList]::new()
        $existingHistorySource = $null
        if ($stateObj.Contains("stress_history") -and $null -ne $stateObj["stress_history"]) {
            $existingHistorySource = @($stateObj["stress_history"])
        } elseif ($existingResObj -and $null -ne $existingResObj.stress_history) {
            $existingHistorySource = @($existingResObj.stress_history)
        }
        if ($existingHistorySource) {
            foreach ($hItem in $existingHistorySource) {
                if ($null -ne $hItem) {
                    [void]$historyList.Add($hItem)
                }
            }
        }

        # Provera da li postoji prethodni završeni pokušaj koji treba arhivirati u stress_history pre upisa novog
        $prevEntry = $null
        if ($existingResObj -and $existingResObj.status) {
            $prevRunId = if ($existingResObj.run_id) { "$($existingResObj.run_id)" } else { "" }
            $prevTs = if ($existingResObj.timestamp) { "$($existingResObj.timestamp)" } else { "" }
            $isSameRun = ($prevRunId -and $prevRunId -eq $curRunId) -or ((-not $prevRunId) -and $prevTs -and $prevTs -eq $curTimestamp)
            if (-not $isSameRun) {
                $prevEntry = [ordered]@{
                    run_id              = $prevRunId
                    status              = "$($existingResObj.status)"
                    reason              = if ($existingResObj.reason) { "$($existingResObj.reason)" } else { "" }
                    emergency_abort     = [bool]$existingResObj.emergency_abort
                    sensor_available    = [bool]$existingResObj.sensor_available
                    sensor_source       = if ($existingResObj.sensor_source) { "$($existingResObj.sensor_source)" } else { "NONE" }
                    duration_sec        = if ($null -ne $existingResObj.duration_sec) { [int]$existingResObj.duration_sec } elseif ($null -ne $existingResObj.duration_seconds) { [int]$existingResObj.duration_seconds } else { 0 }
                    max_cpu_temp_c      = if ($null -ne $existingResObj.max_cpu_temp_c) { [double]$existingResObj.max_cpu_temp_c } elseif ($null -ne $existingResObj.max_cpu_temp) { [double]$existingResObj.max_cpu_temp } else { 0.0 }
                    max_gpu_temp_c      = if ($null -ne $existingResObj.max_gpu_temp_c) { [double]$existingResObj.max_gpu_temp_c } elseif ($null -ne $existingResObj.max_gpu_temp) { [double]$existingResObj.max_gpu_temp } else { 0.0 }
                    throttling_detected = [bool]$existingResObj.throttling_detected
                    stress_report       = if ($existingResObj.stress_report) { "$($existingResObj.stress_report)" } else { "" }
                    timestamp           = $prevTs
                }
            }
        } elseif ($stateObj.Contains("stress_status") -or $stateObj.Contains("stress_report")) {
            $prevRunId = if ($stateObj.Contains("stress_run_id") -and $stateObj["stress_run_id"]) { "$($stateObj["stress_run_id"])" } else { "" }
            $prevTs = if ($stateObj.Contains("stress_timestamp") -and $stateObj["stress_timestamp"]) { "$($stateObj["stress_timestamp"])" } elseif ($stateObj.Contains("updated_at") -and $stateObj["updated_at"]) { "$($stateObj["updated_at"])" } else { "" }
            $isSameRun = ($prevRunId -and $prevRunId -eq $curRunId) -or ((-not $prevRunId) -and $prevTs -and $prevTs -eq $curTimestamp)
            if (-not $isSameRun) {
                $prevEntry = [ordered]@{
                    run_id              = $prevRunId
                    status              = if ($stateObj.Contains("stress_status") -and $stateObj["stress_status"]) { "$($stateObj["stress_status"])" } else { "UNKNOWN" }
                    reason              = if ($stateObj.Contains("stress_reason") -and $stateObj["stress_reason"]) { "$($stateObj["stress_reason"])" } else { "" }
                    emergency_abort     = if ($stateObj.Contains("emergency_abort")) { [bool]$stateObj["emergency_abort"] } else { $false }
                    sensor_available    = if ($stateObj.Contains("sensor_available")) { [bool]$stateObj["sensor_available"] } else { $false }
                    sensor_source       = if ($stateObj.Contains("sensor_source") -and $stateObj["sensor_source"]) { "$($stateObj["sensor_source"])" } else { "NONE" }
                    duration_sec        = if ($stateObj.Contains("stress_duration_sec") -and $null -ne $stateObj["stress_duration_sec"]) { [int]$stateObj["stress_duration_sec"] } else { 0 }
                    max_cpu_temp_c      = if ($stateObj.Contains("max_cpu_temp") -and $null -ne $stateObj["max_cpu_temp"]) { [double]$stateObj["max_cpu_temp"] } else { 0.0 }
                    max_gpu_temp_c      = if ($stateObj.Contains("max_gpu_temp") -and $null -ne $stateObj["max_gpu_temp"]) { [double]$stateObj["max_gpu_temp"] } else { 0.0 }
                    throttling_detected = if ($stateObj.Contains("throttling_detected")) { [bool]$stateObj["throttling_detected"] } else { $false }
                    stress_report       = if ($stateObj.Contains("stress_report") -and $stateObj["stress_report"]) { "$($stateObj["stress_report"])" } else { "" }
                    timestamp           = $prevTs
                }
            }
        }

        if ($null -ne $prevEntry) {
            $alreadyRecorded = $false
            foreach ($existingHist in $historyList) {
                if (($prevEntry.run_id -and $existingHist.run_id -eq $prevEntry.run_id) -or ((-not $prevEntry.run_id -or -not $existingHist.run_id) -and $prevEntry.timestamp -and $existingHist.timestamp -eq $prevEntry.timestamp)) {
                    $alreadyRecorded = $true
                    break
                }
            }
            if (-not $alreadyRecorded) {
                [void]$historyList.Add([PSCustomObject]$prevEntry)
            }
        }

        $utf8NoBom = New-Object System.Text.UTF8Encoding($false)

        $diagContract = Get-WisDiagnosticsContract -Status $curStatus -SensorAvailable $curSensorAvail -MaxCpuTemp $curMaxCpu -EmergencyAbort $curEmergency
        $curDiagWarn = $diagContract.diagnostics_warning

        # 1. Upis u C:\setup\stress-test-result.json
        $contractMap = [ordered]@{
            run_id              = $curRunId
            status              = $curStatus
            reason              = $curReason
            emergency_abort     = $curEmergency
            sensor_available    = $curSensorAvail
            sensor_source       = $curSensorSrc
            duration_sec        = $curDurSec
            duration_seconds    = $curDurSec
            max_cpu_temp_c      = $curMaxCpu
            max_gpu_temp_c      = $curMaxGpu
            max_cpu_temp        = $curMaxCpu
            max_gpu_temp        = $curMaxGpu
            throttling_detected = $curThrottling
            timestamp           = $curTimestamp
            stress_report       = $curReport
            diagnostics_warning = $curDiagWarn
            diagnostics_status  = $diagContract.diagnostics_status
            cooling_verified    = $diagContract.cooling_verified
            manual_check_required = $diagContract.manual_check_required
            stress_history      = @($historyList.ToArray())
        }
        $contractJson = ConvertTo-Json -InputObject $contractMap -Depth 6
        [System.IO.File]::WriteAllText($resFile, $contractJson, $utf8NoBom)

        # 2. Upis u C:\setup\deploy-state.json
        $stateObj["stress_run_id"]       = $curRunId
        $stateObj["stress_status"]       = $curStatus
        $stateObj["stress_reason"]       = $curReason
        $stateObj["emergency_abort"]     = $curEmergency
        $stateObj["sensor_available"]    = $curSensorAvail
        $stateObj["sensor_source"]       = $curSensorSrc
        $stateObj["stress_duration_sec"] = $curDurSec
        $stateObj["max_cpu_temp"]        = $curMaxCpu
        $stateObj["max_gpu_temp"]        = $curMaxGpu
        $stateObj["max_cpu_temp_c"]      = $curMaxCpu
        $stateObj["max_gpu_temp_c"]      = $curMaxGpu
        $stateObj["throttling_detected"] = $curThrottling
        $stateObj["stress_report"]       = $curReport
        $stateObj["stress_timestamp"]    = $curTimestamp
        $stateObj["diagnostics_warning"] = $curDiagWarn
        $diagContract = Get-WisDiagnosticsContract -Status $curStatus -SensorAvailable $curSensorAvail -MaxCpuTemp $curMaxCpu -EmergencyAbort $curEmergency
        $stateObj["diagnostics_status"] = $diagContract.diagnostics_status
        $stateObj["cooling_verified"] = $diagContract.cooling_verified
        $stateObj["manual_check_required"] = $diagContract.manual_check_required
        $stateObj["stress_history"]      = @($historyList.ToArray())

        if ($null -ne $Phase) {
            $stateObj["phase"] = [int]$Phase
        }
        if ($DeploymentStatus) {
            $stateObj["deployment_status"] = $DeploymentStatus
        }
        if ($null -ne $HandoverReady) {
            $stateObj["handover_ready"] = [bool]$HandoverReady
        }
        $stateObj["updated_at"] = (Get-Date -Format "o")

        $stateJson = ConvertTo-Json -InputObject $stateObj -Depth 6
        [System.IO.File]::WriteAllText($stateFile, $stateJson, $utf8NoBom)
    } catch {
        Write-ServisLog -Message "Upozorenje pri čuvanju rezultata stres testa: $($_.Exception.Message)" -Level "WARN" -Component "State"
    }
}

# Inicijalizacija osnovnih putanja i globalnog tajmera pri uvozu modula
Initialize-ServisPaths
try { Initialize-ServisStartTime | Out-Null } catch {}

# ------------------------------------------------------------------------------
# 7. IZVOZ JAVNIH FUNKCIJA
# ------------------------------------------------------------------------------

Export-ModuleMember -Function `
    Initialize-ServisPaths, `
    Initialize-ServisStartTime, `
    Test-ServisAdministrator, `
    Assert-ServisAdministrator, `
    Send-IpcMessage, `
    Send-IpcLog, `
    Send-IpcProgress, `
    Send-IpcStatus, `
    Wait-ServisUICooperativePause, `
    Test-ServisSkipRequested, `
    Format-ExitCodeDescription, `
    Rotate-ServisLog, `
    Write-ServisLog, `
    Write-DiagnosticEvent, `
    Invoke-LoggedProcess, `
    Save-DeployStatePhase, `
    Get-DeployStatePhase, `
    Get-ServisStressTestResult, `
    Save-ServisStressTestResult
