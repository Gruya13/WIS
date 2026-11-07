<#
.SYNOPSIS
    Analyze-WISLog.ps1 - WIS Deployment & Telemetry Log Analyzer (PowerShell 5.1+)
.DESCRIPTION
    PowerShell alat za analizu, dijagnostiku i kreiranje izvestaja iz servis_deploy.log
    i servis_deploy.jsonl bez zavisnosti od spoljnih biblioteka ili Pythona.
.PARAMETER LogDir
    Putanja do foldera sa logovima (podrazumevano: C:\Windows\Logs\WIS, C:\setup ili tekuci folder).
.PARAMETER LogFile
    Direktna putanja do specificnog log fajla.
.PARAMETER OutputReport
    Putanja za cuvanje izvestaja (.md ili .json).
.PARAMETER NoColor
    Iskljucuje boje u konzolnom ispisu.
#>

[CmdletBinding()]
param(
    [Parameter(Position=0)]
    [string]$LogDir = "",

    [Parameter()]
    [string]$LogFile = "",

    [Parameter()]
    [string]$OutputReport = "",

    [switch]$NoColor,

    [switch]$VerboseOutput
)

$ErrorActionPreference = "SilentlyContinue"

# ------------------------------------------------------------------------------
# PREDLOG LOKACIJA I DETEKCIJA FAJLOVA
# ------------------------------------------------------------------------------
$TargetFile = ""
if ($LogFile -and (Test-Path $LogFile)) {
    $TargetFile = (Resolve-Path $LogFile).Path
} else {
    $Candidates = [System.Collections.Generic.List[string]]::new()
    if ($LogDir) { $Candidates.Add($LogDir) }
    $Candidates.Add((Get-Location).Path)
    $Candidates.Add("C:\Windows\Logs\WIS")
    $Candidates.Add("C:\setup")

    Get-PSDrive -PSProvider FileSystem | ForEach-Object {
        $Candidates.Add("$($_.Root)Logs")
        $Candidates.Add($_.Root)
    }

    foreach ($dir in $Candidates) {
        if ($dir -and (Test-Path $dir)) {
            $testLog = Join-Path $dir "servis_deploy.log"
            if (Test-Path $testLog) {
                $TargetFile = $testLog
                break
            }
            $matchLog = Get-ChildItem -Path $dir -Filter "servis_deploy*.log" | Select-Object -First 1
            if ($matchLog) {
                $TargetFile = $matchLog.FullName
                break
            }
        }
    }
}

if (-not $TargetFile -or (-not (Test-Path $TargetFile))) {
    Write-Host " [GRESKA] Nije pronadjen WIS log fajl za analizu!" -ForegroundColor Red
    exit 1
}

# ------------------------------------------------------------------------------
# INICIJALIZACIJA STATISTIKE
# ------------------------------------------------------------------------------
$Meta = [ordered]@{
    ComputerName = "Nepoznato"
    UserName     = "Nepoznato"
    PSVersion    = "Nepoznato"
    Brand        = "USER"
    StartTime    = $null
    EndTime      = $null
}

$Phases = [ordered]@{
    "Phase 1 (Init & Mreza)"               = @{ Start = $null; End = $null; Status = "PENDING"; Duration = "N/A" }
    "Phase 2 (Windows Update)"             = @{ Start = $null; End = $null; Status = "PENDING"; Duration = "N/A"; Reboots = 0 }
    "Phase 3 (Brendiranje & Optimizacije)" = @{ Start = $null; End = $null; Status = "PENDING"; Duration = "N/A" }
    "Phase 4 (Programi & Aktivacija)"      = @{ Start = $null; End = $null; Status = "PENDING"; Duration = "N/A" }
    "Phase 5 (Termalni Stres Test)"        = @{ Start = $null; End = $null; Status = "PENDING"; Duration = "N/A" }
    "Phase 6 (Ciscenje & Zavrsetak)"       = @{ Start = $null; End = $null; Status = "PENDING"; Duration = "N/A" }
}

$Apps = [ordered]@{
    "Google Chrome"        = [ordered]@{ Status = "NIJE_PRONADJENO"; ExitCode = "-"; Duration = "-" }
    "WinRAR"               = [ordered]@{ Status = "NIJE_PRONADJENO"; ExitCode = "-"; Duration = "-" }
    "Adobe Acrobat Pro"    = [ordered]@{ Status = "NIJE_PRONADJENO"; ExitCode = "-"; Duration = "-" }
    "Microsoft Office"     = [ordered]@{ Status = "NIJE_PRONADJENO"; ExitCode = "-"; Duration = "-" }
    ".NET Desktop Runtime" = [ordered]@{ Status = "NIJE_PRONADJENO"; ExitCode = "-"; Duration = "-" }
    "BatteryBar"           = [ordered]@{ Status = "PRESKOCENO";      ExitCode = "-"; Duration = "-" }
    "OEM Licenca"          = [ordered]@{ Status = "NEAKTIVIRANO";    ExitCode = "-"; Duration = "-" }
}

$Errors = [System.Collections.Generic.List[PSCustomObject]]::new()
$Warnings = [System.Collections.Generic.List[PSCustomObject]]::new()
$WuFailedPackages = [System.Collections.Generic.List[string]]::new()

$PrePnpCount = "N/A"
$PostPnpCount = "N/A"
$TotalWuDiscovered = 0

function Format-Sec([double]$seconds) {
    if ($seconds -lt 0) { return "N/A" }
    $sec = [int][math]::Round($seconds)
    $hrs = [math]::Floor($sec / 3600)
    $mins = [math]::Floor(($sec % 3600) / 60)
    $s = $sec % 60
    if ($hrs -gt 0) { return ("{0}h {1:D2}m {2:D2}s" -f $hrs, $mins, $s) }
    elseif ($mins -gt 0) { return ("{0}m {1:D2}s" -f $mins, $s) }
    else { return ("{0:F1}s" -f $seconds) }
}

# ------------------------------------------------------------------------------
# PARSIRANJE LOGA
# ------------------------------------------------------------------------------
$lines = [System.IO.File]::ReadAllLines($TargetFile, [System.Text.Encoding]::UTF8)
$lineRegex = '(?s)^\[(?<Timestamp>\d{4}-\d{2}-\d{2}\s+\d{2}:\d{2}:\d{2}(?:\.\d{3})?)\]\s+\[(?<Level>[A-Z]+)\]\s+\[(?<Component>[^\]]+)\]\s*(?<Message>.*)$'

$lineNum = 0
foreach ($line in $lines) {
    $lineNum++
    if (-not ($line -match $lineRegex)) { continue }

    $tsStr = $Matches["Timestamp"]
    $level = $Matches["Level"]
    $comp  = $Matches["Component"]
    $msg   = $Matches["Message"]

    $ts = [datetime]$tsStr
    if (-not $Meta.StartTime) { $Meta.StartTime = $ts }
    $Meta.EndTime = $ts

    if ($level -eq "ERROR") {
        $Errors.Add([PSCustomObject]@{ Line = $lineNum; Time = $tsStr; Component = $comp; Message = $msg })
    } elseif ($level -eq "WARN") {
        $Warnings.Add([PSCustomObject]@{ Line = $lineNum; Time = $tsStr; Component = $comp; Message = $msg })
    }

    if ($comp -eq "Init") {
        if ($msg -match 'Racunar:\s*([^\|]+)\|\s*Korisnik:\s*([^\|]+)') {
            $Meta.ComputerName = $Matches[1].Trim()
            $Meta.UserName = $Matches[2].Trim()
        }
        if ($msg -match 'PowerShell verzija:\s*([^\|]+)') {
            $Meta.PSVersion = $Matches[1].Trim()
        }
    }

    if ($msg -match 'POKRENUT TEHNO KUTAK') {
        if (-not $Phases["Phase 1 (Init & Mreza)"].Start) {
            $Phases["Phase 1 (Init & Mreza)"].Start = $ts
        }
    }
    if ($msg -match 'Pokrecem Windows Update skeniranje') {
        if (-not $Phases["Phase 1 (Init & Mreza)"].End) {
            $Phases["Phase 1 (Init & Mreza)"].End = $ts
            $Phases["Phase 1 (Init & Mreza)"].Status = "ZAVRSENO"
        }
        if (-not $Phases["Phase 2 (Windows Update)"].Start) {
            $Phases["Phase 2 (Windows Update)"].Start = $ts
            $Phases["Phase 2 (Windows Update)"].Status = "U TOKU"
        }
    }
    if ($msg -match 'RESTARTUJEM RACUNAR') {
        $Phases["Phase 2 (Windows Update)"].Reboots++
    }
    if ($msg -match 'Otkriveno\s+(\d+)\s+uredjaja bez drajvera') {
        if ($PrePnpCount -eq "N/A") { $PrePnpCount = $Matches[1] }
        $PostPnpCount = $Matches[1]
    }
    if ($msg -match 'Greska pri instalaciji WU paketa\s+([^:]+)') {
        $WuFailedPackages.Add($Matches[1].Trim())
    }
    if ($msg -match 'Primenjujem sistemske optimizacije') {
        if (-not $Phases["Phase 2 (Windows Update)"].End) {
            $Phases["Phase 2 (Windows Update)"].End = $ts
            $Phases["Phase 2 (Windows Update)"].Status = "ZAVRSENO"
        }
        $Phases["Phase 3 (Brendiranje & Optimizacije)"].Start = $ts
        $Phases["Phase 3 (Brendiranje & Optimizacije)"].Status = "U TOKU"
        if ($msg -match 'podesavam brend\s+([A-Za-z0-9_\-]+)') {
            $Meta.Brand = $Matches[1].Trim()
        }
    }
    if ($msg -match 'Optimizacija sistema uspesna') {
        $Phases["Phase 3 (Brendiranje & Optimizacije)"].End = $ts
        $Phases["Phase 3 (Brendiranje & Optimizacije)"].Status = "ZAVRSENO"
    }
    if ($msg -match 'POCETAK FAZE 4') {
        $Phases["Phase 4 (Programi & Aktivacija)"].Start = $ts
        $Phases["Phase 4 (Programi & Aktivacija)"].Status = "U TOKU"
    }

    if ($comp -eq "Chrome") {
        if ($msg -match 'uspesno instaliran') { $Apps["Google Chrome"].Status = "INSTALIRANO" }
        elseif ($msg -match 'nije pronadjen') { $Apps["Google Chrome"].Status = "NIJE_PRONADJENO" }
    }
    if ($comp -eq "WinRAR") {
        if ($msg -match 'uspesno instaliran') { $Apps["WinRAR"].Status = "INSTALIRANO" }
        elseif ($msg -match 'nije pronadjen') { $Apps["WinRAR"].Status = "NIJE_PRONADJENO" }
    }
    if ($comp -eq "Acrobat") {
        if ($msg -match 'uspesno instaliran') { $Apps["Adobe Acrobat Pro"].Status = "INSTALIRANO" }
        elseif ($msg -match 'nije pronadjen') { $Apps["Adobe Acrobat Pro"].Status = "NIJE_PRONADJENO" }
    }
    if ($comp -eq "Office") {
        if ($msg -match 'uspesno instaliran') { $Apps["Microsoft Office"].Status = "INSTALIRANO" }
        elseif ($msg -match 'nije pronadjen') { $Apps["Microsoft Office"].Status = "NIJE_PRONADJENO" }
    }
    if ($comp -eq "DotNet") {
        if ($msg -match 'uspesno instaliran') { $Apps[".NET Desktop Runtime"].Status = "INSTALIRANO" }
        elseif ($msg -match 'nije pronadjen') { $Apps[".NET Desktop Runtime"].Status = "NIJE_PRONADJENO" }
    }
    if ($comp -eq "BatteryBar") {
        if ($msg -match 'Preskacem BatteryBar') { $Apps["BatteryBar"].Status = "PRESKOCENO" }
        elseif ($msg -match 'uspesno instaliran') { $Apps["BatteryBar"].Status = "INSTALIRANO" }
    }
    if ($comp -in @("License", "Activation", "Dashboard")) {
        if ($msg -match 'Zavrsni Windows status:\s*\[OK\]' -or ($msg -match 'Licenciran|Aktiviran' -and $msg -notmatch 'Nije')) {
            $Apps["OEM Licenca"].Status = "AKTIVIRANO"
        }
    }

    if ($msg -match 'Zavrsen proces "([^"]+)" za ([\d\.]+)s\.\s*ExitCode:\s*(-?\d+)') {
        $p = $Matches[1].ToLower()
        $d = "$($Matches[2])s"
        $c = $Matches[3]
        if ($p -match "chrome") { $Apps["Google Chrome"].Duration = $d; $Apps["Google Chrome"].ExitCode = $c }
        elseif ($p -match "winrar") { $Apps["WinRAR"].Duration = $d; $Apps["WinRAR"].ExitCode = $c }
        elseif ($p -match "acrobat") { $Apps["Adobe Acrobat Pro"].Duration = $d; $Apps["Adobe Acrobat Pro"].ExitCode = $c }
        elseif ($p -match "office|setup\.exe") { $Apps["Microsoft Office"].Duration = $d; $Apps["Microsoft Office"].ExitCode = $c }
        elseif ($p -match "runtime|dotnet") { $Apps[".NET Desktop Runtime"].Duration = $d; $Apps[".NET Desktop Runtime"].ExitCode = $c }
    }

    if ($msg -match 'Faza 4 uspesno zavrsena') {
        $Phases["Phase 4 (Programi & Aktivacija)"].End = $ts
        $Phases["Phase 4 (Programi & Aktivacija)"].Status = "ZAVRSENO"
    }
    if ($msg -match 'FAZA 5: TERMALNI STRES TEST' -or $msg -match 'Zapocinjem 30-sekundni termalni stres test') {
        if (-not $Phases["Phase 4 (Programi & Aktivacija)"].End) {
            $Phases["Phase 4 (Programi & Aktivacija)"].End = $ts
            $Phases["Phase 4 (Programi & Aktivacija)"].Status = "ZAVRSENO"
        }
        $Phases["Phase 5 (Termalni Stres Test)"].Start = $ts
        $Phases["Phase 5 (Termalni Stres Test)"].Status = "U TOKU"
    }
    if ($msg -match 'Faza 5 uspesno zavrsena' -or $msg -match 'Termalni stres test zavrsen') {
        $Phases["Phase 5 (Termalni Stres Test)"].End = $ts
        $Phases["Phase 5 (Termalni Stres Test)"].Status = "ZAVRSENO"
    }
    if ($msg -match 'FAZA 6: ZAVRSETAK' -or $msg -match 'SVE FAZE ZAVRSENE' -or $msg -match 'Pokrecem zavrsno ciscenje') {
        if ($Phases["Phase 5 (Termalni Stres Test)"].Start -and (-not $Phases["Phase 5 (Termalni Stres Test)"].End)) {
            $Phases["Phase 5 (Termalni Stres Test)"].End = $ts
            $Phases["Phase 5 (Termalni Stres Test)"].Status = "ZAVRSENO"
        }
        if (-not $Phases["Phase 6 (Ciscenje & Zavrsetak)"].Start) {
            $Phases["Phase 6 (Ciscenje & Zavrsetak)"].Start = $ts
        }
        $Phases["Phase 6 (Ciscenje & Zavrsetak)"].End = $ts
        $Phases["Phase 6 (Ciscenje & Zavrsetak)"].Status = "ZAVRSENO"
    }
}

# Izracunaj trajanja
foreach ($k in $Phases.Keys) {
    $p = $Phases[$k]
    if ($p.Start -and $p.End) {
        $sec = ($p.End - $p.Start).TotalSeconds
        $p.Duration = Format-Sec $sec
    }
}
$TotalSec = if ($Meta.StartTime -and $Meta.EndTime) { ($Meta.EndTime - $Meta.StartTime).TotalSeconds } else { 0 }
$TotalDurationStr = Format-Sec $TotalSec

# ------------------------------------------------------------------------------
# ISPIS U KONZOLI
# ------------------------------------------------------------------------------
Write-Host "================================================================================" -ForegroundColor Cyan
Write-Host "   WIS DEPLOYMENT & TELEMETRY LOG ANALYZER | PowerShell Dijagnostika" -ForegroundColor Cyan
Write-Host "================================================================================" -ForegroundColor Cyan

Write-Host "`n[OSNOVNE INFORMACIJE O SISTEMU]" -ForegroundColor White
Write-Host (" Racunar:          {0}" -f $Meta.ComputerName) -ForegroundColor Yellow
Write-Host (" Korisnik:         {0}" -f $Meta.UserName)
Write-Host (" Brend masine:     {0}" -f $Meta.Brand) -ForegroundColor Cyan
Write-Host (" PowerShell:       {0}" -f $Meta.PSVersion)
Write-Host (" Analizirani log:  {0}" -f $TargetFile)
Write-Host (" Ukupno trajanje:  {0}" -f $TotalDurationStr) -ForegroundColor Green

Write-Host "`n[TRAJANJE PO FAZAMA]" -ForegroundColor White
foreach ($k in $Phases.Keys) {
    $p = $Phases[$k]
    $color = if ($p.Status -eq "ZAVRSENO") { "Green" } elseif ($p.Status -eq "U TOKU") { "Yellow" } else { "DarkGray" }
    Write-Host (" {0,-38} | [{1,-8}] | Trajanje: {2}" -f $k, $p.Status, $p.Duration) -ForegroundColor $color
}

Write-Host "`n[STATUS INSTALIRANIH APLIKACIJA]" -ForegroundColor White
foreach ($k in $Apps.Keys) {
    $a = $Apps[$k]
    $color = if ($a.Status -in @("INSTALIRANO", "AKTIVIRANO")) { "Green" } elseif ($a.Status -match "PRESKOCENO") { "Yellow" } else { "Red" }
    Write-Host (" {0,-24} | [{1,-15}] | ExitCode: {2,-4} | Trajanje: {3}" -f $k, $a.Status, $a.ExitCode, $a.Duration) -ForegroundColor $color
}

Write-Host "`n[WINDOWS UPDATE & DRAJVERI]" -ForegroundColor White
Write-Host (" Nedostajucih drajvera (Pre / Posle): {0} / {1}" -f $PrePnpCount, $PostPnpCount)
Write-Host (" Broj restarta sistema:              {0}" -f $Phases["Phase 2 (Windows Update)"].Reboots)
if ($WuFailedPackages.Count -gt 0) {
    Write-Host (" Paketa sa greskom: {0}" -f $WuFailedPackages.Count) -ForegroundColor Red
    foreach ($fp in $WuFailedPackages) { Write-Host ("  - {0}" -f $fp) -ForegroundColor DarkRed }
} else {
    Write-Host " Svi Windows Update paketi su prosli bez greske." -ForegroundColor Green
}

Write-Host "`n[STATISTIKA GRESAKA I UPOZORENJA]" -ForegroundColor White
Write-Host (" Ukupno ERROR zapisa: {0}" -f $Errors.Count) -ForegroundColor (if ($Errors.Count -gt 0) { "Red" } else { "Green" })
Write-Host (" Ukupno WARN zapisa:  {0}" -f $Warnings.Count) -ForegroundColor (if ($Warnings.Count -gt 0) { "Yellow" } else { "Green" })

if ($Errors.Count -gt 0) {
    Write-Host "`n[KRITICNE GRESKE]" -ForegroundColor Red
    foreach ($e in ($Errors | Select-Object -First 10)) {
        Write-Host (" Linija {0} [{1}] [{2}]: {3}" -f $e.Line, $e.Time, $e.Component, $e.Message) -ForegroundColor DarkRed
    }
}
Write-Host "`n================================================================================" -ForegroundColor Cyan
