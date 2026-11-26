<#
.MODULE
    WIS.Core.psm1
.DESCRIPTION
    Osnovni sistemski modul za WIS (Windows Installation Assistant).
    Sadrzi multi-target logovanje, upravljanje stanjem, detekciju medijuma, TUI dashboard i status radnika.
.COMPATIBILITY
    Windows PowerShell 5.1+
#>

# ------------------------------------------------------------------------------
# LOGGING ENGINE & EXIT CODES
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

function Write-ServisLog {
    param(
        [Parameter(Mandatory=$true)][string]$Message,
        [ValidateSet("INFO", "STEP", "SUCCESS", "WARN", "ERROR", "DEBUG")][string]$Level = "INFO",
        [string]$Component = "General",
        [string]$Phase = "",
        $Data = $null
    )
    # Sanitizacija lozinki i tajni iz logova
    $sanitizedMsg = $Message
    if ($sanitizedMsg -match '(?i)(Password\s*=\s*)([^\s;,\r\n]+)') {
        $sanitizedMsg = $sanitizedMsg -replace '(?i)(Password\s*=\s*)([^\s;,\r\n]+)', '$1***MASKIRANO***'
    }
    if ($sanitizedMsg -match '(?i)(key\s*=\s*)([^\s;,\r\n]+)') {
        $sanitizedMsg = $sanitizedMsg -replace '(?i)(key\s*=\s*)([^\s;,\r\n]+)', '$1***MASKIRANO***'
    }
    if ($sanitizedMsg -match '(?i)(<keyMaterial>)(.*?)(</keyMaterial>)') {
        $sanitizedMsg = $sanitizedMsg -replace '(?i)(<keyMaterial>)(.*?)(</keyMaterial>)', '$1***MASKIRANO***$3'
    }
    if ($sanitizedMsg -match '(?i)(net(?:\.exe)?\s+use\s+\S+\s+)("[^"]+"|\S+)') {
        $sanitizedMsg = $sanitizedMsg -replace '(?i)(net(?:\.exe)?\s+use\s+\S+\s+)("[^"]+"|\S+)', '$1***MASKIRANO***'
    }

    $now = Get-Date
    $ts = $now.ToString("yyyy-MM-dd HH:mm:ss.fff")
    $isoTs = $now.ToString("o")
    $logLine = "[$ts] [$Level] [$Component] $sanitizedMsg"
    
    if (-not $Global:LogPaths) {
        $Global:LogPaths = [System.Collections.Generic.List[string]]::new()
        $Global:LogPaths.Add("C:\setup\servis_deploy.log")
        $Global:LogPaths.Add("C:\Windows\Logs\WIS\servis_deploy.log")
        $Global:LogPaths.Add("C:\Users\Public\Desktop\WIS_Izvestaj.log")
    }

    if (-not $Global:JsonlLogPaths) {
        $Global:JsonlLogPaths = [System.Collections.Generic.List[string]]::new()
        $Global:JsonlLogPaths.Add("C:\setup\servis_deploy.jsonl")
        $Global:JsonlLogPaths.Add("C:\Windows\Logs\WIS\servis_deploy.jsonl")
    }

    # Upis u tekstualne logove
    foreach ($p in $Global:LogPaths) {
        if ($p) {
            try {
                $dir = Split-Path $p -Parent
                if (-not (Test-Path $dir)) { New-Item -Path $dir -ItemType Directory -Force | Out-Null }
                [System.IO.File]::AppendAllText($p, $logLine + [Environment]::NewLine, [System.Text.Encoding]::UTF8)
            } catch {}
        }
    }

    # Upis u strukturirani JSON Lines (JSONL) log
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
                    [System.IO.File]::AppendAllText($jp, $jsonLine + [Environment]::NewLine, [System.Text.Encoding]::UTF8)
                } catch {}
            }
        }
    } catch {}

    # Slanje u centralni Hub bafer za live telemetriju
    if (Get-Command Push-ServisLogToHub -ErrorAction SilentlyContinue) {
        Push-ServisLogToHub -LogLine $logLine
    }

    # Prosledjivanje log linije ka WIS UI interfejsu
    if (Get-Command Update-ServisUI -ErrorAction SilentlyContinue) {
        Update-ServisUI -LogLine $logLine
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
        [ValidateSet("INFO", "STEP", "SUCCESS", "WARN", "ERROR", "DEBUG")][string]$Level = "INFO",
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
            $Global:JsonlLogPaths = [System.Collections.Generic.List[string]]::new()
            $Global:JsonlLogPaths.Add("C:\setup\servis_deploy.jsonl")
            $Global:JsonlLogPaths.Add("C:\Windows\Logs\WIS\servis_deploy.jsonl")
        }
        foreach ($jp in $Global:JsonlLogPaths) {
            if ($jp) {
                try {
                    $jDir = Split-Path $jp -Parent
                    if (-not (Test-Path $jDir)) { New-Item -Path $jDir -ItemType Directory -Force | Out-Null }
                    [System.IO.File]::AppendAllText($jp, $jsonLine + [Environment]::NewLine, [System.Text.Encoding]::UTF8)
                } catch {}
            }
        }
    } catch {}

    if ($Message) {
        Write-ServisLog -Message $Message -Level $Level -Component $Component
    }
}

function Register-UsbLog {
    param([string]$SourcePath)
    if ($SourcePath -and (Test-Path $SourcePath)) {
        try {
            $usbLogsDir = Join-Path $SourcePath "Logs"
            if (-not (Test-Path $usbLogsDir)) { New-Item -Path $usbLogsDir -ItemType Directory -Force | Out-Null }
            $compName = if ($env:COMPUTERNAME) { $env:COMPUTERNAME } else { "KLIJENT" }
            $dateTag = (Get-Date).ToString("yyyyMMdd")
            $usbLogFile = Join-Path $usbLogsDir "servis_deploy_${compName}_${dateTag}.log"
            $usbJsonlFile = Join-Path $usbLogsDir "servis_deploy_${compName}_${dateTag}.jsonl"
            
            if (-not $Global:LogPaths) {
                $Global:LogPaths = [System.Collections.Generic.List[string]]::new()
            }
            if (-not $Global:LogPaths.Contains($usbLogFile)) {
                $Global:LogPaths.Add($usbLogFile)
                Write-ServisLog "Registrovan USB log fajl: $usbLogFile" -Level "SUCCESS" -Component "Logger"
            }
            if (-not $Global:JsonlLogPaths) {
                $Global:JsonlLogPaths = [System.Collections.Generic.List[string]]::new()
            }
            if (-not $Global:JsonlLogPaths.Contains($usbJsonlFile)) {
                $Global:JsonlLogPaths.Add($usbJsonlFile)
            }
        } catch {}
    }
}

function Invoke-LoggedProcess {
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
    Write-ServisLog $startMsg -Level "DEBUG" -Component $Component
    
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    try {
        $p = Start-Process -FilePath $FilePath -ArgumentList $ArgumentList -WorkingDirectory $WorkingDirectory -WindowStyle $(if ($Hidden) { 'Hidden' } else { 'Normal' }) -PassThru
        if ($Wait -and $p) {
            $p.WaitForExit()
            $sw.Stop()
            $durSec = [math]::Round($sw.Elapsed.TotalSeconds, 2)
            $code = $p.ExitCode
            $codeDesc = Format-ExitCodeDescription $code
            $lvl = if ($code -eq 0 -or $code -eq 3010) { "SUCCESS" } else { "WARN" }
            $finMsg = "Zavrsen proces `"$FilePath`" za ${durSec}s. ExitCode: $codeDesc"
            Write-ServisLog $finMsg -Level $lvl -Component $Component
            
            if ($AppName) {
                $statusStr = if ($code -eq 0) { "INSTALIRANO" } elseif ($code -eq 3010) { "INSTALIRANO (RESTART)" } else { "GRESKA" }
                Write-DiagnosticEvent -Event "PROCESS_FINISHED" -App $AppName -ExitCode $code -DurationSec $durSec -Status $statusStr -Component $Component
            }
        }
        return $p
    } catch {
        $sw.Stop()
        Write-ServisLog "Greska pri izvrsenju `"$FilePath`": $($_.Exception.Message)" -Level "ERROR" -Component $Component
        if ($AppName) {
            Write-DiagnosticEvent -Event "PROCESS_FAILED" -App $AppName -ExitCode -1 -Status "GRESKA" -Message $_.Exception.Message -Level "ERROR" -Component $Component
        }
        return $null
    }
}

# ------------------------------------------------------------------------------
# ENVIRONMENT & NTFS ACL MANAGEMENT
# ------------------------------------------------------------------------------

function Grant-SetupAclPermissions {
    <#
    .SYNOPSIS
        Dodeljuje pune NTFS permisije (Full Control) za SYSTEM, Administrators i Everyone na zadatoj putanji.
    .DESCRIPTION
        Sprecava gresku 1603 (Access Denied) tokom tihih instalacija u SYSTEM kontekstu i osigurava
        nesmetan pristup Windows Installer servisu (msiserver) instalacionim paketima.
    .PARAMETER Path
        Ciljni direktorijum ili fajl (podrazumevano C:\setup).
    #>
    [CmdletBinding()]
    param(
        [string]$Path = "C:\setup"
    )

    if (-not (Test-Path $Path)) {
        try {
            New-Item -Path $Path -ItemType Directory -Force | Out-Null
        } catch {}
    }

    if (Test-Path $Path) {
        Write-ServisLog "Dodela NTFS permisija (SYSTEM/Admins=Full, Users=RX) za: $Path" -Level "DEBUG" -Component "Acl"
        try {
            & icacls.exe "$Path" /grant "*S-1-5-18:(OI)(CI)F" /grant "*S-1-5-32-544:(OI)(CI)F" /grant "*S-1-5-32-545:(OI)(CI)RX" /T /Q >$null 2>&1
            Write-ServisLog "Uspesno primenjene NTFS permisije na: $Path" -Level "SUCCESS" -Component "Acl"
        } catch {
            Write-ServisLog "Upozorenje pri dodeli NTFS permisija za ${Path}: $($_.Exception.Message)" -Level "WARN" -Component "Acl"
        }
    }
}

function Test-IsSingleInstance {
    <#
    .SYNOPSIS
        Proverava da li je druga instanca Install-ServisApps.ps1 vec aktivna (Process Takeover mehanizam).
    .DESCRIPTION
        Kada $Worker nije postavljen, proverava da li postoji drugi powershell proces
        koji izvrsava Install-ServisApps.ps1 (a nije worker proces sa -Worker i nije trenutni $PID).
        Umesto autocidnog gasenja, preuzima kontrolu terminiranjem starih instanci.
        # exit 0 uklonjen u korist Takeover mehanizma (Stop-Process -Force) radi sprecavanja autocidnog gasenja
    #>
    [CmdletBinding()]
    param()

    if ($Worker -or ($Phase -and $Phase -gt 0) -or ($Global:Phase -and $Global:Phase -gt 0) -or $env:WIS_PHASE_WORKER) {
        return $true
    }

    try {
        $otherProcs = Get-CimInstance Win32_Process -ErrorAction SilentlyContinue | Where-Object {
            $_.Name -match 'powershell' -and
            $_.CommandLine -match 'Install-ServisApps\.ps1' -and
            $_.CommandLine -notmatch '-Worker' -and
            $_.CommandLine -notmatch '-Phase' -and
            $_.ProcessId -ne $PID
        }
        if ($otherProcs) {
            foreach ($op in $otherProcs) {
                try {
                    Write-Host "Pronadjena prethodna/pozadinska instanca Install-ServisApps.ps1 (PID: $($op.ProcessId)). Terminiram je radi preuzimanja interaktivne kontrole..." -ForegroundColor Yellow
                    Stop-Process -Id $op.ProcessId -Force -ErrorAction SilentlyContinue
                } catch {}
            }
            Start-Sleep -Milliseconds 500
            if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                Write-ServisLog "Preuzeta kontrola nad deployment procesom (prethodne instance terminirane). Nastavljam interaktivni rad." -Level "INFO" -Component "Init"
            }
        }
    } catch {}

    return $true
}

function Initialize-ServisEnvironment {
    <#
    .SYNOPSIS
        Inicijalizuje osnovno radno okruzenje za servisnu skriptu (direktorijumi i NTFS dozvole).
    #>
    [CmdletBinding()]
    param(
        [string]$SetupDir = "C:\setup",
        [string]$LogDir = "C:\Windows\Logs\WIS"
    )

    $null = Test-IsSingleInstance

    if (-not (Test-Path $SetupDir)) {
        try { New-Item -Path $SetupDir -ItemType Directory -Force | Out-Null } catch {}
    }
    if (-not (Test-Path $LogDir)) {
        try { New-Item -Path $LogDir -ItemType Directory -Force | Out-Null } catch {}
    }

    Grant-SetupAclPermissions -Path $SetupDir
    Grant-SetupAclPermissions -Path $LogDir
    Register-WisSecurityRecoverySentinel | Out-Null
}

function Register-WisSecurityRecoverySentinel {
    <#
    .SYNOPSIS
        Registruje jednokratni OnStartup task oporavka koji vraca Windows bezbednosne polise
        ako se servisna instalacija prekine nasilno pre Faze 6.
    #>
    [CmdletBinding()]
    param()

    try {
        $psCmd = "reg.exe add 'HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System' /v ConsentPromptBehaviorAdmin /t REG_DWORD /d 5 /f >nul & reg.exe add 'HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System' /v PromptOnSecureDesktop /t REG_DWORD /d 1 /f >nul & reg.exe add 'HKLM\SYSTEM\CurrentControlSet\Services\LanmanWorkstation\Parameters' /v AllowInsecureGuestAuth /t REG_DWORD /d 0 /f >nul & reg.exe add 'HKLM\SYSTEM\CurrentControlSet\Services\LanmanWorkstation\Parameters' /v RequireSecuritySignature /t REG_DWORD /d 1 /f >nul & reg.exe add 'HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon' /v AutoAdminLogon /t REG_SZ /d 0 /f >nul & powershell.exe -NoProfile -Command `"try { Remove-MpPreference -ExclusionPath 'C:\setup' -ErrorAction SilentlyContinue } catch {}`" & schtasks.exe /Delete /TN WIS_SecurityRecovery_Sentinel /F >nul 2>&1"
        $taskRun = "cmd.exe /c `"$psCmd`""
        & schtasks.exe /Create /TN "WIS_SecurityRecovery_Sentinel" /SC ONSTART /RU SYSTEM /RL HIGHEST /TR $taskRun /F >$null 2>&1
        return ($LASTEXITCODE -eq 0)
    } catch {
        return $false
    }
}

# ------------------------------------------------------------------------------
# PATH & REPOSITORY DISCOVERY
# ------------------------------------------------------------------------------

function Repair-ProgramiNesting {
    param([string]$TargetDir)
    if (-not $TargetDir -or -not (Test-Path $TargetDir)) { return }

    # Sprecavamo unistavanje C:\setup\Programi: ravnanje se radi SAMO ako unutar TargetDir postoji ugnjezdeni 'Programi'
    # a sam TargetDir je vec folder 'Programi' (npr. C:\setup\Programi\Programi ili D:\Programi\Programi)
    $dirName = (Get-Item $TargetDir).Name
    if ($dirName -ne "Programi") {
        # Ako TargetDir nije 'Programi' (npr. C:\setup), proveravamo da li unutar njega postoji dvostruko ugnjezdenje: Programi\Programi
        $doubleNested = Join-Path $TargetDir "Programi\Programi"
        if (Test-Path $doubleNested) {
            $realProgrami = Join-Path $TargetDir "Programi"
            Repair-ProgramiNesting $realProgrami
        }
        return
    }

    # Ako smo ovde, TargetDir JE 'Programi' (npr. C:\setup\Programi).
    # Proveravamo da li unutar njega postoji podfolder 'Programi' (ugnjezdenje C:\setup\Programi\Programi)
    $nested = Join-Path $TargetDir "Programi"
    if (Test-Path $nested) {
        Write-ServisLog "Detektovan ugnjezdeni folder: $nested. Pokrecem automatsko ravnanje..." -Level "WARN" -Component "Init"
        try {
            Get-ChildItem -Path $nested -Force | ForEach-Object {
                $dest = Join-Path $TargetDir $_.Name
                if (-not (Test-Path $dest)) {
                    Move-Item -Path $_.FullName -Destination $TargetDir -Force -ErrorAction SilentlyContinue
                } else {
                    Copy-Item -Path "$($_.FullName)\*" -Destination $dest -Recurse -Force -ErrorAction SilentlyContinue
                    Remove-Item -Path $_.FullName -Recurse -Force -ErrorAction SilentlyContinue
                }
            }
            Remove-Item -Path $nested -Recurse -Force -ErrorAction SilentlyContinue
            Write-ServisLog "Uspesno poravnata struktura u $TargetDir" -Level "SUCCESS" -Component "Init"
        } catch {
            Write-ServisLog "Upozorenje pri ravnanju strukture: $($_.Exception.Message)" -Level "WARN" -Component "Init"
        }
    }
}

function Find-SourceDrive {
    $driveRoots = @()
    Get-PSDrive -PSProvider FileSystem -ErrorAction SilentlyContinue | Where-Object { $_.Root -and ($_.Root -ne "C:\") } | ForEach-Object { $driveRoots += ($_.Root.TrimEnd('\') + '\') }
    try {
        Get-CimInstance Win32_CDROMDrive -ErrorAction SilentlyContinue | Where-Object { $_.Drive } | ForEach-Object { $driveRoots += ($_.Drive.TrimEnd('\') + '\') }
    } catch {}
    try {
        Get-Volume -ErrorAction SilentlyContinue | Where-Object { $_.DriveLetter -and ($_.DriveLetter -ne 'C') } | ForEach-Object { $driveRoots += ($_.DriveLetter + ':\') }
    } catch {}
    try {
        [System.IO.DriveInfo]::GetDrives() | Where-Object { $_.Name -and ($_.Name -ne 'C:\') } | ForEach-Object { $driveRoots += ($_.Name.TrimEnd('\') + '\') }
    } catch {}

    $drives = $driveRoots | Where-Object { $_ -and ($_ -ne "C:\") } | Select-Object -Unique | ForEach-Object {
        [PSCustomObject]@{ Root = $_ }
    }
    
    $packageSignatures = @(
        "Programi\Office 2024",
        "Programi\GoogleChromeStandaloneEnterprise64.msi",
        "Programi\winrar-x64-701.exe",
        "Programi\Adobe.Acrobat.Pro.2024.x64.Multilingual"
    )

    # 1. Primarni prolaz: Drajv koji sadrzi sve kljucne instalacione pakete
    foreach ($d in $drives) {
        $hasAll = $true
        foreach ($sig in $packageSignatures) {
            $p1 = Join-Path $d.Root $sig
            $p2 = Join-Path $d.Root ('sources\$OEM$\$1\setup\' + $sig)
            if (-not (Test-Path $p1 -ErrorAction SilentlyContinue) -and -not (Test-Path $p2 -ErrorAction SilentlyContinue)) {
                $hasAll = $false
                break
            }
        }
        if ($hasAll) {
            Register-UsbLog -SourcePath $d.Root
            return $d.Root
        }
    }

    # 2. Sekundarni prolaz: Drajv koji sadrzi barem neki od kljucnih instalacionih paketa
    foreach ($d in $drives) {
        foreach ($sig in $packageSignatures) {
            $p1 = Join-Path $d.Root $sig
            $p2 = Join-Path $d.Root ('sources\$OEM$\$1\setup\' + $sig)
            if ((Test-Path $p1 -ErrorAction SilentlyContinue) -or (Test-Path $p2 -ErrorAction SilentlyContinue)) {
                Register-UsbLog -SourcePath $d.Root
                return $d.Root
            }
        }
    }

    # 3. Fallback prolaz: Postojanje foldera Programi ili servisne skripte
    foreach ($d in $drives) {
        $p1 = Join-Path $d.Root "Programi"
        $p2 = Join-Path $d.Root 'sources\$OEM$\$1\setup\Programi'
        $s1 = Join-Path $d.Root "Skripte\Install-ServisApps.ps1"
        $s2 = Join-Path $d.Root "Install-ServisApps.ps1"
        $s3 = Join-Path $d.Root 'sources\$OEM$\$1\setup\Install-ServisApps.ps1'
        if ((Test-Path $p1 -ErrorAction SilentlyContinue) -or 
            (Test-Path $p2 -ErrorAction SilentlyContinue) -or 
            (Test-Path $s1 -ErrorAction SilentlyContinue) -or 
            (Test-Path $s2 -ErrorAction SilentlyContinue) -or 
            (Test-Path $s3 -ErrorAction SilentlyContinue)) {
            Register-UsbLog -SourcePath $d.Root
            return $d.Root
        }
    }
    return ""
}

function Get-ProgramiDir {
    $localProg = "C:\setup\Programi"
    Repair-ProgramiNesting $localProg

    # 1. Proveri lokalni C:\setup\Programi da li sadrzi stvarne pakete
    if (Test-Path $localProg) {
        $hasOffice = (Test-Path (Join-Path $localProg "Office 2024\files\setup.exe")) -or (Test-Path (Join-Path $localProg "Office 2024\setup.exe"))
        $hasAdobe = (Test-Path (Join-Path $localProg "Adobe.Acrobat.Pro.2024.x64.Multilingual\Adobe Acrobat Pro\AcroPro.msi")) -or (Test-Path (Join-Path $localProg "Acrobat Pro 2024\Adobe Acrobat Pro\AcroPro.msi"))
        if ($hasOffice -or $hasAdobe) {
            return $localProg
        }
    }

    # 2. Ako lokalno nedostaje ili je prazno, potrazi direktno na USB fleš drajvu
    $usbRoot = Find-SourceDrive
    if ($usbRoot) {
        $usbProg = Join-Path $usbRoot "Programi"
        if (Test-Path $usbProg) {
            Repair-ProgramiNesting $usbProg
            $hasOfficeUsb = (Test-Path (Join-Path $usbProg "Office 2024\files\setup.exe")) -or (Test-Path (Join-Path $usbProg "Office 2024\setup.exe"))
            $hasAdobeUsb = (Test-Path (Join-Path $usbProg "Adobe.Acrobat.Pro.2024.x64.Multilingual\Adobe Acrobat Pro\AcroPro.msi")) -or (Test-Path (Join-Path $usbProg "Acrobat Pro 2024\Adobe Acrobat Pro\AcroPro.msi"))
            if ($hasOfficeUsb -or $hasAdobeUsb) {
                Write-ServisLog "Koristim programe direktno sa USB medijuma: $usbProg" -Level "WARN" -Component "Init"
                return $usbProg
            }
        }

        $oemProg = Join-Path $usbRoot 'sources\$OEM$\$1\setup\Programi'
        if (Test-Path $oemProg) {
            Repair-ProgramiNesting $oemProg
            return $oemProg
        }
    }

    # 3. Fallback na lokalni folder
    return $localProg
}

function Get-ServisNasSettings {
    <#
    .SYNOPSIS
        Ucitava parametre [ServisNas] sekcije iz Wifi-Servis.ini konfiguracije.
    #>
    $iniCandidates = @(
        "C:\setup\Wifi-Servis.ini",
        (Join-Path $PSScriptRoot "Wifi-Servis.ini"),
        (Join-Path (Split-Path $PSScriptRoot -Parent) "Wifi-Servis.ini"),
        (Join-Path (Split-Path $PSScriptRoot -Parent) "Skripte\Wifi-Servis.ini")
    )
    if ($Global:SourceRoot) {
        $iniCandidates += (Join-Path $Global:SourceRoot "Wifi-Servis.ini")
        $iniCandidates += (Join-Path $Global:SourceRoot "Skripte\Wifi-Servis.ini")
    }
    foreach ($d in (Get-PSDrive -PSProvider FileSystem -ErrorAction SilentlyContinue)) {
        $iniCandidates += (Join-Path "$($d.Root)" "Wifi-Servis.ini")
        $iniCandidates += (Join-Path "$($d.Root)" "Skripte\Wifi-Servis.ini")
    }

    $iniPath = $iniCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $iniPath) {
        return [PSCustomObject]@{
            IniPath           = $null
            Enabled           = "1"
            NasHost           = "192.168.170.254"
            ShareName         = "WIS"
            ProgramiShare     = "Programi"
            UpdatesSubfolder  = "Updates\Win11"
            Username          = "admin"
            Password          = ""
            PreInstallDrivers = "1"
            DriversSubDir     = "Drivers"
            HarvestMode       = "All"
        }
    }

    $iniContent = Get-Content $iniPath -Raw -ErrorAction SilentlyContinue
    if (-not $iniContent -or $iniContent -notmatch '(?mi)^\s*\[ServisNas\]([\s\S]*?)(?:^\s*\[|\Z)') {
        return [PSCustomObject]@{
            IniPath           = $iniPath
            Enabled           = "1"
            NasHost           = "192.168.170.254"
            ShareName         = "WIS"
            ProgramiShare     = "Programi"
            UpdatesSubfolder  = "Updates\Win11"
            Username          = "admin"
            Password          = ""
            PreInstallDrivers = "1"
            DriversSubDir     = "Drivers"
            HarvestMode       = "All"
        }
    }

    $nasSec = $matches[1]
    $enabled = if ($nasSec -match '(?mi)^[ \t]*Enabled[ \t]*=[ \t]*([^\r\n#;]+)') { $matches[1].Trim() } else { "1" }
    $nasHost = if ($nasSec -match '(?mi)^[ \t]*NasHost[ \t]*=[ \t]*([^\r\n#;]+)') { $matches[1].Trim() } else { "192.168.170.254" }
    $shareName = if ($nasSec -match '(?mi)^[ \t]*ShareName[ \t]*=[ \t]*([^\r\n#;]+)') { $matches[1].Trim() } else { "WIS" }
    $programiShare = if ($nasSec -match '(?mi)^[ \t]*ProgramiShare[ \t]*=[ \t]*([^\r\n#;]+)') { $matches[1].Trim() } else { "Programi" }
    $updatesSub = if ($nasSec -match '(?mi)^[ \t]*UpdatesSubfolder[ \t]*=[ \t]*([^\r\n#;]+)') { $matches[1].Trim() } else { "Updates\Win11" }
    $user = if ($nasSec -match '(?mi)^[ \t]*Username[ \t]*=[ \t]*([^\r\n#;]+)') { $matches[1].Trim() } else { "admin" }
    if ([string]::IsNullOrWhiteSpace($user) -or $user -ieq "servis") { $user = "admin" }
    $pass = if ($nasSec -match '(?mi)^[ \t]*Password[ \t]*=[ \t]*([^\r\n#;]+)') { $matches[1].Trim() } else { "" }
    if ([string]::IsNullOrWhiteSpace($pass) -or $pass -eq "promeni_lozinku" -or $pass -eq "CHANGE_ME") { $pass = "" }
    $preInstall = if ($nasSec -match '(?mi)^[ \t]*PreInstallDrivers[ \t]*=[ \t]*([^\r\n#;]+)') { $matches[1].Trim() } else { "1" }
    $driversSub = if ($nasSec -match '(?mi)^[ \t]*DriversSubDir[ \t]*=[ \t]*([^\r\n#;]+)') { $matches[1].Trim() } else { "Drivers" }
    $harvestMode = if ($nasSec -match '(?mi)^[ \t]*HarvestMode[ \t]*=[ \t]*([^\r\n#;]+)') { $matches[1].Trim() } else { "All" }

    return [PSCustomObject]@{
        IniPath           = $iniPath
        Enabled           = $enabled
        NasHost           = $nasHost
        ShareName         = $shareName
        ProgramiShare     = $programiShare
        UpdatesSubfolder  = $updatesSub
        Username          = $user
        Password          = $pass
        PreInstallDrivers = $preInstall
        DriversSubDir     = $driversSub
        HarvestMode       = $harvestMode
    }
}

function Get-ServisLicensingSettings {
    <#
    .SYNOPSIS
        Ucitava parametre [Licensing] sekcije iz Wifi-Servis.ini konfiguracije.
    #>
    [CmdletBinding()]
    param()

    $iniCandidates = @()
    if (-not [string]::IsNullOrWhiteSpace($Global:LocalSetupDir)) {
        $iniCandidates += (Join-Path $Global:LocalSetupDir "Wifi-Servis.ini")
        $iniCandidates += (Join-Path $Global:LocalSetupDir "Skripte\Wifi-Servis.ini")
    }
    $iniCandidates += "C:\setup\Wifi-Servis.ini"
    $iniCandidates += "C:\setup\Skripte\Wifi-Servis.ini"
    if (-not [string]::IsNullOrWhiteSpace($Global:SourceRoot)) {
        $iniCandidates += (Join-Path $Global:SourceRoot "Wifi-Servis.ini")
        $iniCandidates += (Join-Path $Global:SourceRoot "Skripte\Wifi-Servis.ini")
    }
    if (-not [string]::IsNullOrWhiteSpace($PSScriptRoot)) {
        $iniCandidates += (Join-Path $PSScriptRoot "Wifi-Servis.ini")
        $iniCandidates += (Join-Path (Split-Path $PSScriptRoot -Parent) "Wifi-Servis.ini")
        $iniCandidates += (Join-Path (Split-Path $PSScriptRoot -Parent) "Skripte\Wifi-Servis.ini")
    }

    $iniPath = $iniCandidates | Where-Object {
        -not [string]::IsNullOrWhiteSpace($_) -and (Test-Path -LiteralPath $_ -PathType Leaf -ErrorAction SilentlyContinue)
    } | Select-Object -First 1

    $found = $false
    $mode = "Auto"
    $kmsHost = ""
    $kmsPort = 1688
    $productKey = ""
    $winMode = "Auto"
    $winKmsHost = ""
    $winKmsPort = 1688
    $winProductKey = ""

    $validModes = @{
        "auto"         = "Auto"
        "kms"          = "Kms"
        "mak"          = "Mak"
        "externalhook" = "ExternalHook"
        "skip"         = "Skip"
    }

    if ($iniPath) {
        try {
            $lines = Get-Content -LiteralPath $iniPath -Encoding UTF8 -ErrorAction SilentlyContinue
            $inLicensing = $false
            foreach ($rawLine in $lines) {
                $line = if ($null -ne $rawLine) { ([string]$rawLine).Trim() } else { "" }
                if (-not $line -or $line.StartsWith(";") -or $line.StartsWith("#")) { continue }

                if ($line.StartsWith("[") -and $line.EndsWith("]")) {
                    $secName = $line.Substring(1, $line.Length - 2).Trim()
                    if ($secName -ieq "Licensing") {
                        $inLicensing = $true
                        $found = $true
                    } else {
                        $inLicensing = $false
                    }
                    continue
                }

                if ($inLicensing -and $line -match '^([^=]+)=(.*)$') {
                    $k = $matches[1].Trim()
                    $v = ($matches[2] -replace '[;#].*$', '').Trim()

                    if ($k -ieq "OfficeActivationMode" -and -not [string]::IsNullOrWhiteSpace($v)) {
                        $vLower = $v.ToLowerInvariant()
                        if ($validModes.ContainsKey($vLower)) {
                            $mode = $validModes[$vLower]
                        } else {
                            Write-ServisLog "Nepoznata vrednost OfficeActivationMode='$v' u [Licensing]; vracam na podrazumevano 'Auto'." -Level "WARN" -Component "License"
                            $mode = "Auto"
                        }
                    } elseif ($k -ieq "OfficeKmsHost") {
                        if (-not [string]::IsNullOrWhiteSpace($v) -and $v -ne "CHANGE_ME") {
                            $kmsHost = $v
                        }
                    } elseif ($k -ieq "OfficeKmsPort" -and -not [string]::IsNullOrWhiteSpace($v)) {
                        $parsedPort = 0
                        if ([int]::TryParse($v, [ref]$parsedPort) -and $parsedPort -ge 1 -and $parsedPort -le 65535) {
                            $kmsPort = $parsedPort
                        }
                    } elseif ($k -ieq "OfficeProductKey") {
                        # Strogo zabranjeno logovanje vrednosti OfficeProductKey
                        if (-not [string]::IsNullOrWhiteSpace($v) -and $v -ne "CHANGE_ME" -and $v -ne "promeni_kljuc") {
                            $productKey = $v
                        }
                    } elseif ($k -ieq "WindowsActivationMode" -and -not [string]::IsNullOrWhiteSpace($v)) {
                        $vLowerW = $v.ToLowerInvariant()
                        if ($validModes.ContainsKey($vLowerW)) {
                            $winMode = $validModes[$vLowerW]
                        } else {
                            Write-ServisLog "Nepoznata vrednost WindowsActivationMode='$v' u [Licensing]; vracam na podrazumevano 'Auto'." -Level "WARN" -Component "License"
                            $winMode = "Auto"
                        }
                    } elseif ($k -ieq "WindowsKmsHost") {
                        if (-not [string]::IsNullOrWhiteSpace($v) -and $v -ne "CHANGE_ME") {
                            $winKmsHost = $v
                        }
                    } elseif ($k -ieq "WindowsKmsPort" -and -not [string]::IsNullOrWhiteSpace($v)) {
                        $parsedWinPort = 0
                        if ([int]::TryParse($v, [ref]$parsedWinPort) -and $parsedWinPort -ge 1 -and $parsedWinPort -le 65535) {
                            $winKmsPort = $parsedWinPort
                        }
                    } elseif ($k -ieq "WindowsProductKey") {
                        # Strogo zabranjeno logovanje vrednosti WindowsProductKey
                        if (-not [string]::IsNullOrWhiteSpace($v) -and $v -ne "CHANGE_ME" -and $v -ne "promeni_kljuc") {
                            $winProductKey = $v
                        }
                    }
                }
            }
        } catch {
            Write-ServisLog "Upozorenje pri ucitavanju [Licensing] sekcije iz ${iniPath}: $($_.Exception.Message)" -Level "WARN" -Component "License"
        }
    }

    return [PSCustomObject]@{
        Found                 = [bool]$found
        IniPath               = $iniPath
        OfficeActivationMode  = $mode
        OfficeKmsHost         = $kmsHost
        OfficeKmsPort         = [int]$kmsPort
        OfficeProductKey      = $productKey
        WindowsActivationMode = $winMode
        WindowsKmsHost        = $winKmsHost
        WindowsKmsPort        = [int]$winKmsPort
        WindowsProductKey     = $winProductKey
    }
}

function Test-NasHostOnline {
    param([string]$NasHost)
    if ([string]::IsNullOrWhiteSpace($NasHost)) { return $false }
    $nasOnline = $false
    try {
        $tcp = New-Object System.Net.Sockets.TcpClient
        $ar = $tcp.BeginConnect($NasHost, 445, $null, $null)
        $ok = $ar.AsyncWaitHandle.WaitOne(1500, $false)
        if ($ok -and $tcp.Connected) {
            $nasOnline = $true
            $tcp.EndConnect($ar)
        }
        $tcp.Close()
    } catch { $nasOnline = $false }

    if (-not $nasOnline) {
        try {
            $nasOnline = Test-Connection -ComputerName $NasHost -Count 1 -Quiet -ErrorAction SilentlyContinue
        } catch { $nasOnline = $false }
    }
    return $nasOnline
}

function Sync-ProgramiFromNas {
    <#
    .SYNOPSIS
        Preuzima instalacione pakete programa sa servisnog NAS SMB deljenja na lokalni SSD (C:\setup\Programi).
        Glavni fallback mehanizam za PXE mrezni boot kada racunar nema fizicki prikljucen USB fles disk.
    #>
    [CmdletBinding()]
    param(
        [string]$DestinationDir = "C:\setup\Programi",
        [switch]$Force = $false,
        [string[]]$SubFolders = @(),
        [string]$OnlyApp = ""
    )

    $nas = Get-ServisNasSettings
    if ($nas -and $nas.Enabled -ne "1") {
        Write-ServisLog "NAS sinhronizacija programa iskljucena u konfiguraciji." -Level "DEBUG" -Component "NasProgrami"
        return $false
    }
    if (-not $nas) {
        $nas = [PSCustomObject]@{ NasHost = "192.168.170.254"; ShareName = "WIS"; ProgramiShare = "Programi"; Username = "admin"; Password = "" }
    }
    $programiHost = if ($nas.NasHost) { $nas.NasHost } else { "192.168.170.254" }
    if (-not (Test-NasHostOnline -NasHost $programiHost)) {
        Write-ServisLog "NAS host $programiHost nije dostupan na SMB portu." -Level "WARN" -Component "NasProgrami"
        if (Get-Command Send-IpcLog -ErrorAction SilentlyContinue) {
            Send-IpcLog -Message "NAS host $programiHost nije dostupan na SMB portu (445)." -Level "WARN" -Tag "[NasProgrami]"
        }
        return $false
    }

    # IMP-75: Kanonski prioritet je "Servis" pre default "Programi" deljenja (sprečava lažni WARN za \Programi)
    $shareCandidates = @()
    $cfgProgShare = if ($nas.ProgramiShare) { $nas.ProgramiShare.Trim() } else { "" }
    if (-not [string]::IsNullOrWhiteSpace($cfgProgShare) -and ($cfgProgShare -ine "Programi")) {
        $shareCandidates += $cfgProgShare
    }
    if (-not [string]::IsNullOrWhiteSpace($nas.ShareName) -and ($nas.ShareName.Trim() -ine "Programi") -and ($shareCandidates -notcontains $nas.ShareName.Trim())) {
        $shareCandidates += $nas.ShareName.Trim()
    }
    if ($shareCandidates -notcontains "Servis") { $shareCandidates += "Servis" }
    if ($shareCandidates -notcontains "Programi") { $shareCandidates += "Programi" }
    if ($shareCandidates -notcontains "WIS") { $shareCandidates += "WIS" }
    $shareCandidates = $shareCandidates | Select-Object -Unique

    $uncShare = $null
    $nasProgDir = $null

    try {
        # 1. Povezivanje na SMB deljenje (sa akreditivima iz Wifi-Servis.ini ili Guest fallback)
        try {
            $lanmanPath = "HKLM:\SYSTEM\CurrentControlSet\Services\LanmanWorkstation\Parameters"
            if (-not (Test-Path $lanmanPath)) {
                New-Item -Path $lanmanPath -Force -ErrorAction SilentlyContinue | Out-Null
            }
            Set-ItemProperty -Path $lanmanPath -Name "AllowInsecureGuestAuth" -Value 1 -Type DWord -Force -ErrorAction SilentlyContinue
            Set-ItemProperty -Path $lanmanPath -Name "RequireSecuritySignature" -Value 0 -Type DWord -Force -ErrorAction SilentlyContinue
        } catch {}

        $uName = if (-not [string]::IsNullOrWhiteSpace($nas.Username) -and ($nas.Username -ine "servis")) { $nas.Username } else { "admin" }
        $uPass = if (-not [string]::IsNullOrWhiteSpace($nas.Password) -and $nas.Password -ne "promeni_lozinku" -and $nas.Password -ne "CHANGE_ME") { $nas.Password } else { "" }

        foreach ($cand in $shareCandidates) {
            $candUnc = "\\$programiHost\$cand"
            Write-ServisLog "Povezujem se na servisni NAS resurs: $candUnc radi preuzimanja instalacionih paketa..." -Level "INFO" -Component "NasProgrami"
            
            $prevEap = $ErrorActionPreference
            $ErrorActionPreference = 'Continue'
            try {
                $needConnect = $true
                if (Test-Path -LiteralPath $candUnc -ErrorAction SilentlyContinue) {
                    $probeItems = @(Get-ChildItem -LiteralPath $candUnc -ErrorAction SilentlyContinue | Select-Object -First 1)
                    if ($probeItems.Count -gt 0 -or [string]::IsNullOrWhiteSpace($uPass)) {
                        $needConnect = $false
                    } else {
                        # Postoji postojeca guest/nobody sesija bez prava citanja: ocisti samo konflikt ka ovom hostu
                        Clear-WisCoreHostSmbConflicts -TargetHost $programiHost
                    }
                }

                if ($needConnect) {
                    $userArg = if ($uName -match "\\") { $uName } else { "$programiHost\$uName" }
                    if (-not [string]::IsNullOrWhiteSpace($uPass)) {
                        $rc1 = Invoke-WisCoreNetUse -Action "Connect" -ShareUnc $candUnc -Password $uPass -User $userArg
                        if ($rc1 -eq 1219) {
                            Write-ServisLog "SMB Error 1219 detektovan za $candUnc; cistim konfliktne sesije ka $programiHost..." -Level "WARN" -Component "NasProgrami"
                            Clear-WisCoreHostSmbConflicts -TargetHost $programiHost
                            $rc1 = Invoke-WisCoreNetUse -Action "Connect" -ShareUnc $candUnc -Password $uPass -User $userArg
                        }
                        if (-not (Test-Path -LiteralPath $candUnc -ErrorAction SilentlyContinue)) {
                            $rc2 = Invoke-WisCoreNetUse -Action "Connect" -ShareUnc $candUnc -Password $uPass -User $uName
                            if ($rc2 -eq 1219) {
                                Clear-WisCoreHostSmbConflicts -TargetHost $programiHost
                                $null = Invoke-WisCoreNetUse -Action "Connect" -ShareUnc $candUnc -Password $uPass -User $uName
                            }
                        }
                    }
                    if (-not (Test-Path -LiteralPath $candUnc -ErrorAction SilentlyContinue)) {
                        $rcG = Invoke-WisCoreNetUse -Action "Connect" -ShareUnc $candUnc -Password "" -User "guest"
                        if ($rcG -eq 1219) {
                            Clear-WisCoreHostSmbConflicts -TargetHost $programiHost
                            $null = Invoke-WisCoreNetUse -Action "Connect" -ShareUnc $candUnc -Password "" -User "guest"
                        }
                    }
                    if (-not (Test-Path -LiteralPath $candUnc -ErrorAction SilentlyContinue)) {
                        Write-ServisLog "SMB pristup odbijen ili nedostupan za $candUnc (korisnik: $uName)." -Level "WARN" -Component "NasProgrami"
                        if (Get-Command Send-IpcLog -ErrorAction SilentlyContinue) {
                            Send-IpcLog -Message "SMB pristup odbijen ili nedostupan za $candUnc (korisnik: $uName)." -Level "WARN" -Tag "[NasProgrami]"
                        }
                    }
                }
            } catch {} finally {
                $ErrorActionPreference = $prevEap
            }

            # Provera gde se nalaze programi:
            # Na deljenju 'Programi' paketi se nalaze direktno u korenu deljenja,
            # dok na deljenju 'WIS' ili 'Servis' mogu biti u podfolderu 'Programi'
            $testDirs = @($candUnc, (Join-Path $candUnc "Programi"))
            foreach ($td in $testDirs) {
                if (Test-Path $td -ErrorAction SilentlyContinue) {
                    $hasAnyProg = (Test-Path (Join-Path $td "Office 2024")) -or `
                                  (Test-Path (Join-Path $td "Adobe.Acrobat.Pro.2024.x64.Multilingual")) -or `
                                  (Test-Path (Join-Path $td "Acrobat Pro 2024")) -or `
                                  (Test-Path (Join-Path $td "ChromeSetup.exe")) -or `
                                  (Test-Path (Join-Path $td "GoogleChromeStandaloneEnterprise64.msi")) -or `
                                  (Test-Path (Join-Path $td "winrar-x64-701.exe"))
                    if ($hasAnyProg) {
                        $uncShare = $candUnc
                        $nasProgDir = $td
                        Write-ServisLog "Uspesno povezan NAS resurs: $uncShare (Programi pronadjeni u: $nasProgDir)" -Level "INFO" -Component "NasProgrami"
                        break
                    }
                }
            }
            if ($nasProgDir) { break }
        }

        if (-not $uncShare -or -not (Test-Path $nasProgDir)) {
            Write-ServisLog "Folder sa programima ne postoji na NAS-u ($nasProgDir)." -Level "WARN" -Component "NasProgrami"
            if (Get-Command Send-IpcLog -ErrorAction SilentlyContinue) {
                Send-IpcLog -Message "Folder sa programima nije pronađen na NAS serveru ($programiHost)." -Level "ERROR" -Tag "[NasProgrami]"
            }
            return $false
        }

        # IMP-48 / IMP-112: Priprema ciljanog (single-app / SubFolders / Golden ISO delta) prenosa
        $targetSubFolders = [System.Collections.Generic.List[string]]::new()
        $targetRootFilters = [System.Collections.Generic.List[string]]::new()
        $addTargetSpec = {
            param([string]$RawToken)
            if ([string]::IsNullOrWhiteSpace($RawToken)) { return }
            $trimmed = $RawToken.Trim()
            $norm = $trimmed.ToLowerInvariant()
            switch -Regex ($norm) {
                '^(acrobat|adobe acrobat|adobe acrobat pro)$' {
                    foreach ($af in @("Adobe.Acrobat.Pro.2024.x64.Multilingual", "Acrobat Pro 2024", "Adobe Acrobat Pro")) {
                        if (-not $targetSubFolders.Contains($af)) { $targetSubFolders.Add($af) }
                    }
                }
                '^(office|microsoft office|ms office)$' {
                    if (-not $targetSubFolders.Contains("Office 2024")) { $targetSubFolders.Add("Office 2024") }
                }
                '^(dotnet|runtimes|vcredist)$' {
                    foreach ($df in @("DotNet", "VCRedist")) {
                        if (-not $targetSubFolders.Contains($df)) { $targetSubFolders.Add($df) }
                    }
                    foreach ($rf in @("*windowsdesktop-runtime*.exe", "*aspnetcore-runtime*.exe", "*VC_redist*.exe")) {
                        if (-not $targetRootFilters.Contains($rf)) { $targetRootFilters.Add($rf) }
                    }
                }
                '^batterybar$' {
                    if (-not $targetSubFolders.Contains("BatteryBar")) { $targetSubFolders.Add("BatteryBar") }
                }
                '^(chrome|google chrome)$' {
                    if (-not $targetSubFolders.Contains("Chrome")) { $targetSubFolders.Add("Chrome") }
                    foreach ($cf in @("GoogleChromeStandaloneEnterprise64.msi", "ChromeSetup.exe", "*chrome*.msi", "*chrome*.exe")) {
                        if (-not $targetRootFilters.Contains($cf)) { $targetRootFilters.Add($cf) }
                    }
                }
                '^winrar$' {
                    if (-not $targetSubFolders.Contains("WinRAR")) { $targetSubFolders.Add("WinRAR") }
                    foreach ($wf in @("winrar-x64-701.exe", "*winrar*.exe")) {
                        if (-not $targetRootFilters.Contains($wf)) { $targetRootFilters.Add($wf) }
                    }
                }
                default {
                    if (-not $targetSubFolders.Contains($trimmed)) { $targetSubFolders.Add($trimmed) }
                }
            }
        }
        if ($SubFolders) {
            foreach ($sfItem in $SubFolders) {
                & $addTargetSpec $sfItem
            }
        }
        $normalizedOnlyApp = if ($OnlyApp) { $OnlyApp.Trim().ToLowerInvariant() } else { "" }
        if ($normalizedOnlyApp) {
            & $addTargetSpec $normalizedOnlyApp
        }
        $isTargetedSync = ($targetSubFolders.Count -gt 0) -or ($targetRootFilters.Count -gt 0)

        # 2. Provera postojanja kljucnih instalacionih paketa na NAS-u
        $nasOffice = (Test-Path (Join-Path $nasProgDir "Office 2024\files\setup.exe")) -or `
                     (Test-Path (Join-Path $nasProgDir "Office 2024\setup.exe")) -or `
                     (Test-Path (Join-Path $nasProgDir "Programi\Office 2024\files\setup.exe")) -or `
                     (Test-Path (Join-Path $nasProgDir "Programi\Office 2024\setup.exe"))
        $nasAdobe = (Test-Path (Join-Path $nasProgDir "Adobe.Acrobat.Pro.2024.x64.Multilingual\Adobe Acrobat Pro\AcroPro.msi")) -or (Test-Path (Join-Path $nasProgDir "Acrobat Pro 2024\Adobe Acrobat Pro\AcroPro.msi"))
        $nasChrome = (Test-Path (Join-Path $nasProgDir "ChromeSetup.exe")) -or (Test-Path (Join-Path $nasProgDir "GoogleChromeStandaloneEnterprise64.msi"))
        $nasBatteryBar = (Test-Path (Join-Path $nasProgDir "BatteryBar\BatteryBar.exe")) -or `
                         (Test-Path (Join-Path $nasProgDir "Programi\BatteryBar\BatteryBar.exe"))

        if (-not $isTargetedSync -and -not $nasOffice -and -not $nasAdobe) {
            Write-ServisLog "NAS folder $nasProgDir ne sadrzi kompletne pakete (Office=$nasOffice, Adobe=$nasAdobe, BatteryBar=$nasBatteryBar)." -Level "WARN" -Component "NasProgrami"
            if (Get-Command Send-IpcLog -ErrorAction SilentlyContinue) {
                Send-IpcLog -Message "NAS folder $nasProgDir ne sadrži kompletne instalacione pakete (Office=$nasOffice, Adobe=$nasAdobe)." -Level "ERROR" -Tag "[NasProgrami]"
            }
            return $false
        }

        # 3. Provera da li su paketi vec prekopirani i kompletni na lokalnom SSD-u
        if (-not $Force -and -not $isTargetedSync -and (Test-Path $DestinationDir)) {
            Repair-ProgramiNesting $DestinationDir
            $locOffice = (Test-Path (Join-Path $DestinationDir "Office 2024\files\setup.exe")) -or (Test-Path (Join-Path $DestinationDir "Office 2024\setup.exe"))
            $locAdobe = (Test-Path (Join-Path $DestinationDir "Adobe.Acrobat.Pro.2024.x64.Multilingual\Adobe Acrobat Pro\AcroPro.msi")) -or (Test-Path (Join-Path $DestinationDir "Acrobat Pro 2024\Adobe Acrobat Pro\AcroPro.msi"))
            $locChrome = (Test-Path (Join-Path $DestinationDir "ChromeSetup.exe")) -or (Test-Path (Join-Path $DestinationDir "GoogleChromeStandaloneEnterprise64.msi"))
            $locBatteryBar = (Test-Path (Join-Path $DestinationDir "BatteryBar\BatteryBar.exe")) -or `
                             (Test-Path (Join-Path $DestinationDir "Programi\BatteryBar\BatteryBar.exe"))

            # Ako mašina ima bateriju, proveriti prenosivi Floating paket BatteryBar\BatteryBar.exe
            $deviceHasBattery = $false
            try {
                if (Get-CimInstance Win32_Battery -ErrorAction SilentlyContinue) { $deviceHasBattery = $true }
            } catch {}

            $locBatteryBarOk = if ($deviceHasBattery) { [bool]$locBatteryBar } else { $true }

            if ($locOffice -and $locAdobe -and $locChrome -and $locBatteryBarOk) {
                Write-ServisLog "Lokalni paketi na $DestinationDir su vec kompletni. Preskacem prenos sa NAS-a." -Level "INFO" -Component "NasProgrami"
                return $true
            }
        }

        # 4. Prikaz TUI banera i robocopy prenos
        Write-Host ""
        Write-Host "======================================================================================" -ForegroundColor Cyan
        Write-Host "   WIS SERVICE AUTO-DEPLOY I UPDATE TOOLKIT | PXE Mrezno Preuzimanje Programa" -ForegroundColor Cyan
        Write-Host "======================================================================================" -ForegroundColor Cyan
        Write-Host "   [PXE/NAS] Pronadjen mrezni repozitorijum programa: $nasProgDir" -ForegroundColor Green
        Write-Host "   [KOPIRANJE] Prenosim instalacione pakete sa servisnog NAS-a na SSD ($DestinationDir)..." -ForegroundColor Yellow
        Write-Host "   Molimo sacekajte (kopiranje zavisi od brzine mreze i velicine paketa, obicno 1-5 min)..." -ForegroundColor White
        Write-Host "--------------------------------------------------------------------------------------" -ForegroundColor DarkGray

        if (-not (Test-Path $DestinationDir)) {
            New-Item -Path $DestinationDir -ItemType Directory -Force | Out-Null
        }

        $robocopyJobs = [System.Collections.Generic.List[object]]::new()
        if ($isTargetedSync) {
            foreach ($sf in $targetSubFolders) {
                $srcCand1 = Join-Path $nasProgDir $sf
                $srcCand2 = Join-Path (Join-Path $nasProgDir "Programi") $sf
                $resolvedSrc = if (Test-Path -LiteralPath $srcCand1 -PathType Container -ErrorAction SilentlyContinue) {
                    $srcCand1
                } elseif (Test-Path -LiteralPath $srcCand2 -PathType Container -ErrorAction SilentlyContinue) {
                    $srcCand2
                } else {
                    $null
                }
                if ($resolvedSrc) {
                    $dstSub = Join-Path $DestinationDir $sf
                    if (-not (Test-Path -LiteralPath $dstSub -ErrorAction SilentlyContinue)) {
                        New-Item -Path $dstSub -ItemType Directory -Force -ErrorAction SilentlyContinue | Out-Null
                    }
                    $robocopyJobs.Add(@{
                        Source = $resolvedSrc
                        Dest   = $dstSub
                        Args   = @($resolvedSrc, $dstSub, "/E", "/MT:32", "/J", "/R:1", "/W:1", "/NDL", "/NFL", "/NJH", "/NJS", "/XF", "BatteryBarSetup*.exe", "ShowBatteryBar.exe", "BatteryBar.dll", "BarExplorerHook.dll")
                    })
                }
            }
            if ($targetRootFilters.Count -gt 0) {
                $rootArgs = @($nasProgDir, $DestinationDir) + @($targetRootFilters) + @("/MT:16", "/J", "/R:1", "/W:1", "/NDL", "/NFL", "/NJH", "/NJS")
                $robocopyJobs.Add(@{
                    Source = $nasProgDir
                    Dest   = $DestinationDir
                    Args   = $rootArgs
                })
            }
            if ($robocopyJobs.Count -eq 0) {
                Write-ServisLog "Ciljani paket ($OnlyApp / $($targetSubFolders -join ', ')) nije pronadjen na NAS-u ($nasProgDir)." -Level "WARN" -Component "NasProgrami"
                if (Get-Command Send-IpcLog -ErrorAction SilentlyContinue) {
                    Send-IpcLog -Message "Ciljani paket ($OnlyApp) nije pronađen na NAS repozitorijumu ($nasProgDir)." -Level "WARN" -Tag "[NasProgrami]"
                }
                return $false
            }
            Write-ServisLog "Zapocinjem ciljani robocopy prenos ($($robocopyJobs.Count) zadatak(a), OnlyApp='$OnlyApp', SubFolders='$($targetSubFolders -join ',')') sa NAS-a ($nasProgDir) na SSD ($DestinationDir)..." -Level "STEP" -Component "NasProgrami"
        } else {
            Write-ServisLog "Zapocinjem robocopy prenos paketa sa NAS-a ($nasProgDir) na SSD ($DestinationDir)..." -Level "STEP" -Component "NasProgrami"
            $robocopyJobs.Add(@{
                Source = $nasProgDir
                Dest   = $DestinationDir
                Args   = @($nasProgDir, $DestinationDir, "/E", "/MT:32", "/J", "/R:1", "/W:1", "/NDL", "/NFL", "/NJH", "/NJS", "/XF", "BatteryBarSetup*.exe", "ShowBatteryBar.exe", "BatteryBar.dll", "BarExplorerHook.dll")
            })
        }

        $sw = [System.Diagnostics.Stopwatch]::StartNew()
        $rcExitCode = -1
        # IMP-100: Strogo monotoni brojac preuzetih MB (ne opada unazad pri overwrite-u/brisanju parcijalnih fajlova ili prelazu izmedju robocopy poslova)
        $maxCopiedMb = 0.0
        $completedJobsMb = 0.0
        foreach ($job in $robocopyJobs) {
            $rcArgs = $job.Args
            $measureDir = if ($job.Dest) { $job.Dest } else { $DestinationDir }
            $measureUseRoot = ($robocopyJobs.Count -eq 1) -or ($measureDir -ieq $DestinationDir)
            $proc = Start-Process -FilePath "robocopy.exe" -ArgumentList $rcArgs -PassThru -WindowStyle Hidden
            if ($proc) {
                while (-not $proc.WaitForExit(15000)) {
                    $elapsedSec = [int]$sw.Elapsed.TotalSeconds
                    $measuredMb = 0.0
                    try {
                        $scanDir = if ($measureUseRoot) { $DestinationDir } else { $measureDir }
                        if (Test-Path -LiteralPath $scanDir -ErrorAction SilentlyContinue) {
                            $sumMeasure = Get-ChildItem -LiteralPath $scanDir -Recurse -File -ErrorAction SilentlyContinue | Measure-Object -Property Length -Sum
                            if ($sumMeasure -and $null -ne $sumMeasure.Sum) {
                                $measuredMb = [Math]::Round(([double]$sumMeasure.Sum / 1MB), 1)
                            }
                        }
                    } catch {}

                    $candidateMb = if ($measureUseRoot) { $measuredMb } else { [Math]::Round(($completedJobsMb + $measuredMb), 1) }
                    if ($candidateMb -gt $maxCopiedMb) {
                        $maxCopiedMb = $candidateMb
                    }
                    $copiedMb = $maxCopiedMb

                    $syncMsg = "Preuzimam instalacione pakete sa NAS-a ($copiedMb MB preuzeto, ${elapsedSec}s)..."
                    Write-ServisLog "Robocopy prenos sa NAS-a u toku: $copiedMb MB preuzeto (${elapsedSec}s)..." -Level "INFO" -Component "NasProgrami"

                    if ($Global:WisPreserveCurrentPhase -eq $true) {
                        $preservePhaseNum = 4
                        if ($null -ne $Global:CurrentPhase) {
                            $parsedPresP = 0
                            if ([int]::TryParse([string]$Global:CurrentPhase, [ref]$parsedPresP) -and $parsedPresP -gt 0) {
                                $preservePhaseNum = $parsedPresP
                            }
                        }
                        $preserveProg = 95
                        if ($null -ne $Global:CurrentProgress) {
                            $parsedPresProg = 0
                            if ([int]::TryParse([string]$Global:CurrentProgress, [ref]$parsedPresProg) -and $parsedPresProg -ge 0 -and $parsedPresProg -le 100) {
                                $preserveProg = $parsedPresProg
                            }
                        }
                        if (Get-Command Send-IpcStatus -ErrorAction SilentlyContinue) {
                            Send-IpcStatus -Phase $preservePhaseNum -Progress $preserveProg -SubPhase "Sinhronizacija NAS paketa (${elapsedSec}s)" -StatusMessage $syncMsg
                        }
                    } elseif (Get-Command Send-IpcProgress -ErrorAction SilentlyContinue) {
                        $syncProg = 10
                        if ($null -ne $Global:CurrentProgress) {
                            $parsedSyncP = 0
                            if ([int]::TryParse([string]$Global:CurrentProgress, [ref]$parsedSyncP) -and $parsedSyncP -ge 0 -and $parsedSyncP -le 99) {
                                $syncProg = $parsedSyncP
                            }
                        }
                        $syncPhase = 4
                        if ($null -ne $Global:CurrentPhase) {
                            $parsedSyncPhase = 0
                            if ([int]::TryParse([string]$Global:CurrentPhase, [ref]$parsedSyncPhase) -and $parsedSyncPhase -gt 0) {
                                $syncPhase = $parsedSyncPhase
                            }
                        }
                        Send-IpcProgress -Progress $syncProg -Phase $syncPhase -SubPhase "Sinhronizacija NAS paketa (${elapsedSec}s)" -StatusMessage $syncMsg
                    }
                }
                $proc.WaitForExit()
                $rcExitCode = [int]$proc.ExitCode
                try {
                    if (-not $measureUseRoot -and (Test-Path -LiteralPath $measureDir -ErrorAction SilentlyContinue)) {
                        $doneMeasure = Get-ChildItem -LiteralPath $measureDir -Recurse -File -ErrorAction SilentlyContinue | Measure-Object -Property Length -Sum
                        if ($doneMeasure -and $null -ne $doneMeasure.Sum) {
                            $completedJobsMb = [Math]::Round(($completedJobsMb + ([double]$doneMeasure.Sum / 1MB)), 1)
                            if ($completedJobsMb -gt $maxCopiedMb) { $maxCopiedMb = $completedJobsMb }
                        }
                    } elseif (Test-Path -LiteralPath $DestinationDir -ErrorAction SilentlyContinue) {
                        $doneRoot = Get-ChildItem -LiteralPath $DestinationDir -Recurse -File -ErrorAction SilentlyContinue | Measure-Object -Property Length -Sum
                        if ($doneRoot -and $null -ne $doneRoot.Sum) {
                            $rootMb = [Math]::Round(([double]$doneRoot.Sum / 1MB), 1)
                            if ($rootMb -gt $maxCopiedMb) { $maxCopiedMb = $rootMb }
                        }
                    }
                } catch {}
            }
        }
        $sw.Stop()
        $dur = $sw.Elapsed.TotalSeconds.ToString("F1")
        Write-ServisLog "Robocopy prenos sa NAS-a zavrsen za ${dur}s. ExitCode: $rcExitCode" -Level "INFO" -Component "NasProgrami"

        Repair-ProgramiNesting $DestinationDir
        Grant-SetupAclPermissions -Path $DestinationDir

        $vOffice = (Test-Path (Join-Path $DestinationDir "Office 2024\files\setup.exe")) -or (Test-Path (Join-Path $DestinationDir "Office 2024\setup.exe"))
        $vAdobe = (Test-Path (Join-Path $DestinationDir "Adobe.Acrobat.Pro.2024.x64.Multilingual\Adobe Acrobat Pro\AcroPro.msi")) -or (Test-Path (Join-Path $DestinationDir "Acrobat Pro 2024\Adobe Acrobat Pro\AcroPro.msi"))
        $vChrome = (Test-Path (Join-Path $DestinationDir "ChromeSetup.exe")) -or (Test-Path (Join-Path $DestinationDir "GoogleChromeStandaloneEnterprise64.msi"))

        if ($isTargetedSync) {
            $targetVerified = $false
            if (-not [string]::IsNullOrWhiteSpace($normalizedOnlyApp)) {
                switch -Regex ($normalizedOnlyApp) {
                    '^(acrobat|adobe acrobat|adobe acrobat pro)$' { $targetVerified = [bool]$vAdobe }
                    '^(office|microsoft office|ms office)$'       { $targetVerified = [bool]$vOffice }
                    '^(chrome|google chrome)$'                    { $targetVerified = [bool]$vChrome -or (@(Get-ChildItem -LiteralPath $DestinationDir -Filter "*chrome*.msi" -Recurse -File -ErrorAction SilentlyContinue).Count -gt 0) -or (@(Get-ChildItem -LiteralPath $DestinationDir -Filter "*chrome*.exe" -Recurse -File -ErrorAction SilentlyContinue).Count -gt 0) }
                    '^winrar$'                                    { $targetVerified = @(Get-ChildItem -LiteralPath $DestinationDir -Filter "*winrar*.exe" -Recurse -File -ErrorAction SilentlyContinue).Count -gt 0 }
                    '^batterybar$'                                { $targetVerified = (Test-Path -LiteralPath (Join-Path $DestinationDir "BatteryBar\BatteryBar.exe") -ErrorAction SilentlyContinue) }
                    default                                       { $targetVerified = ($rcExitCode -ge 0 -and $rcExitCode -lt 8) }
                }
            } elseif ($SubFolders -and $SubFolders.Count -gt 0) {
                $subCheck = Test-ProgramiDirComplete -DirPath $DestinationDir -RequiredApps $SubFolders -Quiet
                $targetVerified = [bool]($subCheck.IsComplete -or ($rcExitCode -ge 0 -and $rcExitCode -lt 8))
            } else {
                $targetVerified = ($rcExitCode -ge 0 -and $rcExitCode -lt 8)
            }
            if ($targetVerified) {
                Write-ServisLog "Ciljana NAS sinhronizacija uspesna (OnlyApp='$OnlyApp', SubFolders='$($targetSubFolders -join ',')', Preuzeto=${maxCopiedMb} MB) na $DestinationDir." -Level "SUCCESS" -Component "NasProgrami"
                return $true
            }
            Write-ServisLog "UPOZORENJE: Ciljani paket ($OnlyApp / $($targetSubFolders -join ',')) nije verifikovan nakon robocopy-ja (ExitCode=$rcExitCode)." -Level "WARN" -Component "NasProgrami"
            if (Get-Command Send-IpcLog -ErrorAction SilentlyContinue) {
                Send-IpcLog -Message "Ciljani paket ($OnlyApp) nije kompletiran sa NAS-a na $DestinationDir (ExitCode=$rcExitCode)." -Level "WARN" -Tag "[NasProgrami]"
            }
            return $false
        }

        if ($vOffice -and $vAdobe) {
            Write-ServisLog "Verifikacija uspesna! Kljucni instalacioni paketi su preuzeti sa NAS-a na $DestinationDir." -Level "SUCCESS" -Component "NasProgrami"
            Write-Host "   [OK] Svi instalacioni paketi su uspesno preuzeti sa NAS-a i verifikovani na SSD-u!" -ForegroundColor Green
            return $true
        } else {
            Write-ServisLog "UPOZORENJE: Paketi nakon robocopy-ja sa NAS-a nisu kompletni (Office=$vOffice, Adobe=$vAdobe, Chrome=$vChrome). Pokrecem Copy-Item dopunu..." -Level "WARN" -Component "NasProgrami"
            if (Get-Command Send-IpcLog -ErrorAction SilentlyContinue) {
                Send-IpcLog -Message "Paketi nakon robocopy prenosa nisu kompletni (Office=$vOffice, Adobe=$vAdobe). Pokrećem dopunsko kopiranje..." -Level "WARN" -Tag "[NasProgrami]"
            }
            Copy-Item -Path "$nasProgDir\*" -Destination "$DestinationDir\" -Recurse -Force -ErrorAction SilentlyContinue
            Repair-ProgramiNesting $DestinationDir
            Grant-SetupAclPermissions -Path $DestinationDir

            $vOffice = (Test-Path (Join-Path $DestinationDir "Office 2024\files\setup.exe")) -or (Test-Path (Join-Path $DestinationDir "Office 2024\setup.exe"))
            $vAdobe = (Test-Path (Join-Path $DestinationDir "Adobe.Acrobat.Pro.2024.x64.Multilingual\Adobe Acrobat Pro\AcroPro.msi")) -or (Test-Path (Join-Path $DestinationDir "Acrobat Pro 2024\Adobe Acrobat Pro\AcroPro.msi"))

            if ($vOffice -and $vAdobe) {
                Write-ServisLog "Dopunski Copy-Item prenos uspesno kompletirao pakete sa NAS-a." -Level "SUCCESS" -Component "NasProgrami"
                Write-Host "   [OK] Instalacioni paketi su uspesno kompletirani na SSD-u!" -ForegroundColor Green
                return $true
            } else {
                Write-ServisLog "UPOZORENJE: Paketi sa NAS-a nisu mogli biti kompletirani na SSD-u." -Level "WARN" -Component "NasProgrami"
                if (Get-Command Send-IpcLog -ErrorAction SilentlyContinue) {
                    Send-IpcLog -Message "KRITIČNO: Paketi sa NAS-a nisu mogli biti kompletirani na lokalnom SSD-u ($DestinationDir, Office=$vOffice, Adobe=$vAdobe)." -Level "ERROR" -Tag "[NasProgrami]"
                }
                return $false
            }
        }
    } catch {
        Write-ServisLog "Greska pri preuzimanju programa sa NAS-a: $($_.Exception.Message)" -Level "ERROR" -Component "NasProgrami"
        if (Get-Command Send-IpcLog -ErrorAction SilentlyContinue) {
            Send-IpcLog -Message "Greška pri preuzimanju programa sa NAS-a: $($_.Exception.Message)" -Level "ERROR" -Tag "[NasProgrami]"
        }
        return $false
    }
    # Sesija se ne pamti (/persistent:no), ali ostaje dostupna drugim fazama.
    # Ne prekidati deljenja koja koriste UI, Office ili drugi servisni procesi.
}

function Format-WisCoreProcessArg {
    param([string]$Value)
    if ([string]::IsNullOrEmpty($Value)) { return '""' }
    $escaped = $Value -replace '(\\*)"', '$1$1\"'
    $escaped = $escaped -replace '(\\+)$', '$1$1'
    return ('"' + $escaped + '"')
}

function Clear-WisCoreHostSmbConflicts {
    param(
        [Parameter(Mandatory=$true)][string]$TargetHost
    )
    if ([string]::IsNullOrWhiteSpace($TargetHost)) { return }
    $cleanHost = ($TargetHost -replace '[\x00-\x1F]', '').Trim()
    if (-not $cleanHost) { return }

    try {
        if (Get-Command Get-SmbMapping -ErrorAction SilentlyContinue) {
            $maps = @(Get-SmbMapping -ErrorAction SilentlyContinue | Where-Object {
                $_.RemotePath -and ($_.RemotePath -imatch ("^\\\\" + [regex]::Escape($cleanHost) + "\\"))
            })
            foreach ($m in $maps) {
                try { Remove-SmbMapping -RemotePath $m.RemotePath -Force -UpdateProfile:$false -ErrorAction SilentlyContinue } catch {}
            }
        }
    } catch {}

    try {
        $netExe = if ($env:SystemRoot) { Join-Path $env:SystemRoot "System32\net.exe" } else { "net.exe" }
        $netOut = & $netExe use 2>$null
        $escapedHost = [regex]::Escape($cleanHost)
        foreach ($line in $netOut) {
            if ($line -match ("(\\\\$escapedHost\\[^\s]+)")) {
                $uncToDrop = $matches[1].Trim()
                $null = & $netExe use $uncToDrop /delete /y 2>$null
            }
        }
        $null = & $netExe use ("\\" + $cleanHost + "\IPC$") /delete /y 2>$null
    } catch {}
}

function Invoke-WisCoreNetUse {
    param(
        [ValidateSet("Connect", "DisconnectAll")]
        [string]$Action,
        [string]$ShareUnc = "",
        [string]$Password = "",
        [string]$User = ""
    )

    $netExe = if ($env:SystemRoot) { Join-Path $env:SystemRoot "System32\net.exe" } else { "net.exe" }
    if (-not (Test-Path -LiteralPath $netExe -PathType Leaf -ErrorAction SilentlyContinue)) {
        $netExe = "net.exe"
    }

    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $netExe
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.CreateNoWindow = $true

    if ($Action -eq "DisconnectAll") {
        $psi.Arguments = "use * /delete /y"
    } else {
        $cleanShare = ([string]$ShareUnc) -replace '[\x00-\x1F]', ''
        $cleanUser  = ([string]$User) -replace '[\x00-\x1F]', ''
        $cleanPass  = ([string]$Password) -replace '[\x00\r\n]', ''
        if ([string]::IsNullOrWhiteSpace($cleanShare) -or [string]::IsNullOrWhiteSpace($cleanUser)) {
            return -1
        }
        $qShare = Format-WisCoreProcessArg -Value $cleanShare
        $qPass  = Format-WisCoreProcessArg -Value $cleanPass
        $qUser  = Format-WisCoreProcessArg -Value ("/user:" + $cleanUser)
        $psi.Arguments = "use $qShare $qPass $qUser /persistent:no"
    }

    $proc = $null
    try {
        $proc = [System.Diagnostics.Process]::Start($psi)
        if (-not $proc) { return -1 }
        $proc.BeginOutputReadLine()
        $proc.BeginErrorReadLine()
        if (-not $proc.WaitForExit(15000)) {
            try { $proc.Kill() } catch {}
            return -2
        }
        $proc.WaitForExit()
        return [int]$proc.ExitCode
    } catch {
        return -1
    } finally {
        if ($proc) {
            try { $proc.Dispose() } catch {}
        }
    }
}

function Test-WisSafeLicensingItem {
    param(
        [Parameter(Mandatory = $true)][System.IO.FileSystemInfo]$Item
    )
    if (-not $Item) { return $false }
    $attrs = $Item.Attributes
    $forbiddenAttrs = [System.IO.FileAttributes]::ReparsePoint -bor `
                      [System.IO.FileAttributes]::Hidden -bor `
                      [System.IO.FileAttributes]::System
    if (($attrs -band $forbiddenAttrs) -ne 0) {
        return $false
    }
    $name = $Item.Name
    if ([string]::IsNullOrWhiteSpace($name)) { return $false }
    if ($name -eq "." -or $name -eq ".." -or $name.IndexOfAny([System.IO.Path]::GetInvalidFileNameChars()) -ge 0) {
        return $false
    }
    $junkNames = @("thumbs.db", "desktop.ini", ".ds_store", "ehthumbs.db", ".git", "__macosx")
    if ($junkNames -contains $name.ToLowerInvariant()) { return $false }
    if ($name.StartsWith("._")) { return $false }
    return $true
}

function Test-WisSafeLicensingDirHasFiles {
    param([string]$SourcePath)
    if ([string]::IsNullOrWhiteSpace($SourcePath) -or -not (Test-Path -LiteralPath $SourcePath -PathType Container -ErrorAction SilentlyContinue)) {
        return $false
    }
    try {
        $rootInfo = Get-Item -LiteralPath $SourcePath -Force -ErrorAction Stop
        if (-not (Test-WisSafeLicensingItem -Item $rootInfo)) { return $false }

        $queue = New-Object 'System.Collections.Generic.Queue[string]'
        $queue.Enqueue($rootInfo.FullName)
        while ($queue.Count -gt 0) {
            $curDir = $queue.Dequeue()
            $children = @(Get-ChildItem -LiteralPath $curDir -Force -ErrorAction SilentlyContinue)
            foreach ($child in $children) {
                if (-not (Test-WisSafeLicensingItem -Item $child)) { continue }
                if ($child.PSIsContainer) {
                    $queue.Enqueue($child.FullName)
                } else {
                    return $true
                }
            }
        }
    } catch {}
    return $false
}

function Copy-WisSafeLicensingTree {
    param(
        [Parameter(Mandatory = $true)][string]$SourcePath,
        [Parameter(Mandatory = $true)][string]$DestinationPath
    )

    $copiedFiles = 0
    $srcRootInfo = Get-Item -LiteralPath $SourcePath -Force -ErrorAction Stop
    if (-not $srcRootInfo.PSIsContainer -or -not (Test-WisSafeLicensingItem -Item $srcRootInfo)) {
        return 0
    }

    $destRootFull = [System.IO.Path]::GetFullPath($DestinationPath).TrimEnd('\', '/')
    $sep = [System.IO.Path]::DirectorySeparatorChar
    $destPrefix = $destRootFull + $sep

    if (-not (Test-Path -LiteralPath $destRootFull -PathType Container -ErrorAction SilentlyContinue)) {
        New-Item -Path $destRootFull -ItemType Directory -Force | Out-Null
    }

    $queue = New-Object 'System.Collections.Generic.Queue[object]'
    $queue.Enqueue([PSCustomObject]@{ Src = $srcRootInfo.FullName; Dst = $destRootFull })

    while ($queue.Count -gt 0) {
        $node = $queue.Dequeue()
        $children = @(Get-ChildItem -LiteralPath $node.Src -Force -ErrorAction Stop)
        foreach ($child in $children) {
            if (-not (Test-WisSafeLicensingItem -Item $child)) { continue }

            $candidateDst = [System.IO.Path]::GetFullPath((Join-Path $node.Dst $child.Name))
            if (-not $candidateDst.StartsWith($destPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
                throw "Detektovana nedozvoljena putanja izvan ciljnog direktorijuma: $candidateDst"
            }

            if ($child.PSIsContainer) {
                if (-not (Test-Path -LiteralPath $candidateDst -PathType Container -ErrorAction SilentlyContinue)) {
                    New-Item -Path $candidateDst -ItemType Directory -Force | Out-Null
                }
                $queue.Enqueue([PSCustomObject]@{ Src = $child.FullName; Dst = $candidateDst })
            } else {
                if (Test-Path -LiteralPath $candidateDst -PathType Leaf -ErrorAction SilentlyContinue) {
                    try { [System.IO.File]::SetAttributes($candidateDst, [System.IO.FileAttributes]::Normal) } catch {}
                }
                Copy-Item -LiteralPath $child.FullName -Destination $candidateDst -Force -ErrorAction Stop
                # Uklanjanje MOTW (Zone.Identifier) na svakom prekopiranom fajlu preko -LiteralPath
                try { Unblock-File -LiteralPath $candidateDst -ErrorAction SilentlyContinue } catch {}
                try { Remove-Item -LiteralPath "${candidateDst}:Zone.Identifier" -Force -ErrorAction SilentlyContinue } catch {}
                $copiedFiles++
            }
        }
    }

    return $copiedFiles
}

function Sync-ServisLicensingFromNas {
    <#
    .SYNOPSIS
        Brza i ciljana sinhronizacija iskljucivo Licensing i CustomScripts podfoldera sa NAS SMB deljenja u lokalni kes.
        Nikada ne preuzima gigabajtne Office/Adobe instalere niti sadrzi imena third-party alata.
    #>
    [CmdletBinding()]
    param(
        [string]$DestinationDir = "C:\setup\Cache\Licensing"
    )

    $synced = $false
    $hasLicensingDir = $false
    $hasCustomScriptsDir = $false
    $sourceDir = $null

    try {
        if ([string]::IsNullOrWhiteSpace($DestinationDir)) {
            $DestinationDir = "C:\setup\Cache\Licensing"
        }
        $destBaseFull = [System.IO.Path]::GetFullPath($DestinationDir).TrimEnd('\', '/')

        $nas = Get-ServisNasSettings
        if ($nas -and $nas.Enabled -ne "1") {
            Write-ServisLog "NAS sinhronizacija licensing foldera je iskljucena u konfiguraciji." -Level "DEBUG" -Component "NasLicensing"
            return [PSCustomObject]@{
                Synced              = $false
                DestinationDir      = $DestinationDir
                HasLicensingDir     = $false
                HasCustomScriptsDir = $false
                SourceDir           = $null
            }
        }
        if (-not $nas) {
            $nas = [PSCustomObject]@{
                NasHost       = "192.168.170.254"
                ShareName     = "Servis"
                ProgramiShare = "Programi"
                Username      = "admin"
                Password      = ""
            }
        }

        $nasHost = if (-not [string]::IsNullOrWhiteSpace($nas.NasHost)) { ($nas.NasHost -replace '[\x00-\x1F]', '').Trim() } else { "192.168.170.254" }
        if (-not (Test-NasHostOnline -NasHost $nasHost)) {
            Write-ServisLog "NAS host $nasHost nije dostupan na SMB portu za sinhronizaciju licensing foldera." -Level "WARN" -Component "NasLicensing"
            return [PSCustomObject]@{
                Synced              = $false
                DestinationDir      = $DestinationDir
                HasLicensingDir     = $false
                HasCustomScriptsDir = $false
                SourceDir           = $null
            }
        }

        # SMB konfiguracija po uzoru na Sync-ProgramiFromNas
        try {
            $lanmanPath = "HKLM:\SYSTEM\CurrentControlSet\Services\LanmanWorkstation\Parameters"
            if (-not (Test-Path $lanmanPath)) {
                New-Item -Path $lanmanPath -Force -ErrorAction SilentlyContinue | Out-Null
            }
            Set-ItemProperty -Path $lanmanPath -Name "AllowInsecureGuestAuth" -Value 1 -Type DWord -Force -ErrorAction SilentlyContinue
            Set-ItemProperty -Path $lanmanPath -Name "RequireSecuritySignature" -Value 0 -Type DWord -Force -ErrorAction SilentlyContinue
        } catch {}

        $shareCandidates = @()
        $cfgLicProgShare = if ($nas.ProgramiShare) { $nas.ProgramiShare.Trim() } else { "" }
        if (-not [string]::IsNullOrWhiteSpace($cfgLicProgShare) -and ($cfgLicProgShare -ine "Programi")) {
            $shareCandidates += $cfgLicProgShare
        }
        if (-not [string]::IsNullOrWhiteSpace($nas.ShareName) -and ($nas.ShareName.Trim() -ine "Programi") -and ($shareCandidates -notcontains $nas.ShareName.Trim())) {
            $shareCandidates += $nas.ShareName.Trim()
        }
        if ($shareCandidates -notcontains "Servis") { $shareCandidates += "Servis" }
        if ($shareCandidates -notcontains "Programi") { $shareCandidates += "Programi" }
        if ($shareCandidates -notcontains "WIS") { $shareCandidates += "WIS" }
        $shareCandidates = @($shareCandidates | ForEach-Object { ([string]$_) -replace '[\x00-\x1F]', '' } | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -Unique)

        $uName = if (-not [string]::IsNullOrWhiteSpace($nas.Username) -and ($nas.Username -ine "servis")) { ($nas.Username -replace '[\x00-\x1F]', '').Trim() } else { "admin" }
        $uPass = if (-not [string]::IsNullOrWhiteSpace($nas.Password) -and $nas.Password -ne "promeni_lozinku" -and $nas.Password -ne "CHANGE_ME") { $nas.Password } else { "" }

        foreach ($cand in $shareCandidates) {
            $candUnc = "\\$nasHost\$cand"
            $prevEap = $ErrorActionPreference
            $ErrorActionPreference = 'Continue'
            try {
                if (-not (Test-Path -LiteralPath $candUnc -ErrorAction SilentlyContinue)) {
                    $userArg = if ($uName -match "\\") { $uName } else { "$nasHost\$uName" }
                    if (-not [string]::IsNullOrWhiteSpace($uPass)) {
                        $null = Invoke-WisCoreNetUse -Action "Connect" -ShareUnc $candUnc -Password $uPass -User $userArg
                        if (-not (Test-Path -LiteralPath $candUnc -ErrorAction SilentlyContinue)) {
                            $null = Invoke-WisCoreNetUse -Action "Connect" -ShareUnc $candUnc -Password $uPass -User $uName
                        }
                        if (-not (Test-Path -LiteralPath $candUnc -ErrorAction SilentlyContinue)) {
                            # Oslobadjanje konfliktnih SMB sesija samo ka ovom serveru (Error 1219 zastita) bez rusenja tudjih sesija
                            Clear-WisCoreHostSmbConflicts -TargetHost $nasHost
                            $null = Invoke-WisCoreNetUse -Action "Connect" -ShareUnc $candUnc -Password $uPass -User $userArg
                        }
                    }
                    if (-not (Test-Path -LiteralPath $candUnc -ErrorAction SilentlyContinue)) {
                        $null = Invoke-WisCoreNetUse -Action "Connect" -ShareUnc $candUnc -Password "" -User "guest"
                    }
                    if (-not (Test-Path -LiteralPath $candUnc -ErrorAction SilentlyContinue)) {
                        Clear-WisCoreHostSmbConflicts -TargetHost $nasHost
                        $null = Invoke-WisCoreNetUse -Action "Connect" -ShareUnc $candUnc -Password "" -User "guest"
                    }
                }
            } catch {} finally {
                $ErrorActionPreference = $prevEap
            }

            if (-not (Test-Path -LiteralPath $candUnc -ErrorAction SilentlyContinue)) {
                continue
            }

            # Na 'Programi' deljenju podfolderi su u korenu; na 'Servis' i 'WIS' mogu biti u 'Programi\' ili u korenu
            $baseCandidates = @(
                $candUnc,
                (Join-Path $candUnc "Programi")
            )

            foreach ($baseDir in $baseCandidates) {
                if (-not (Test-Path -LiteralPath $baseDir -PathType Container -ErrorAction SilentlyContinue)) {
                    continue
                }

                $srcLic = Join-Path $baseDir "Licensing"
                $srcCs  = Join-Path $baseDir "CustomScripts"

                $foundLic = Test-WisSafeLicensingDirHasFiles -SourcePath $srcLic
                $foundCs  = Test-WisSafeLicensingDirHasFiles -SourcePath $srcCs

                if (-not $foundLic -and -not $foundCs) {
                    continue
                }

                if (-not (Test-Path -LiteralPath $destBaseFull -PathType Container -ErrorAction SilentlyContinue)) {
                    New-Item -Path $destBaseFull -ItemType Directory -Force | Out-Null
                }

                $subPairs = @(
                    @{ Found = $foundLic; Source = $srcLic; Name = "Licensing" },
                    @{ Found = $foundCs;  Source = $srcCs;  Name = "CustomScripts" }
                )

                foreach ($pair in $subPairs) {
                    if (-not $pair.Found) { continue }
                    $targetSub = [System.IO.Path]::GetFullPath((Join-Path $destBaseFull $pair.Name))
                    if (-not $targetSub.StartsWith(($destBaseFull + [System.IO.Path]::DirectorySeparatorChar), [System.StringComparison]::OrdinalIgnoreCase)) {
                        throw "Nedozvoljena odredisna putanja: $targetSub"
                    }

                    $copiedCnt = Copy-WisSafeLicensingTree -SourcePath $pair.Source -DestinationPath $targetSub
                    if ($copiedCnt -gt 0) {
                        if ($pair.Name -eq "Licensing") { $hasLicensingDir = $true }
                        if ($pair.Name -eq "CustomScripts") { $hasCustomScriptsDir = $true }
                    }
                }

                if ($hasLicensingDir -or $hasCustomScriptsDir) {
                    Grant-SetupAclPermissions -Path $destBaseFull
                    $synced = $true
                    $sourceDir = $baseDir
                    Write-ServisLog "Sinhronizovani licensing folderi sa NAS lokacije $baseDir u $destBaseFull (Licensing=$hasLicensingDir, CustomScripts=$hasCustomScriptsDir)." -Level "INFO" -Component "NasLicensing"
                    break
                }
            }

            if ($synced) { break }
        }

        if (-not $synced) {
            Write-ServisLog "Na NAS deljenjima nisu pronadjeni Licensing/CustomScripts podfolderi." -Level "INFO" -Component "NasLicensing"
        }
    } catch {
        Write-ServisLog "Upozorenje pri NAS sinhronizaciji licensing foldera: $($_.Exception.Message)" -Level "WARN" -Component "NasLicensing"
    }

    return [PSCustomObject]@{
        Synced              = [bool]$synced
        DestinationDir      = $DestinationDir
        HasLicensingDir     = [bool]$hasLicensingDir
        HasCustomScriptsDir = [bool]$hasCustomScriptsDir
        SourceDir           = $sourceDir
    }
}

function Get-WisImageProfile {
    <#
    .SYNOPSIS
        IMP-112: Cita profil Windows WIM slike iz C:\Windows\Logs\WIS\image-profile.json
        sa fallback vrednostima (image_type="lean", wim_version="unknown").
    #>
    [CmdletBinding()]
    param(
        [string]$ProfilePath = "",
        [string]$SystemLogDir = "C:\Windows\Logs\WIS",
        [string]$LocalSetupDir = "C:\setup"
    )

    $candidates = [System.Collections.Generic.List[string]]::new()
    if (-not [string]::IsNullOrWhiteSpace($ProfilePath)) { [void]$candidates.Add($ProfilePath) }
    foreach ($c in @(
        (Join-Path $SystemLogDir "image-profile.json"),
        "C:\Windows\Logs\WIS\image-profile.json",
        (Join-Path $LocalSetupDir "image-profile.json")
    )) {
        if (-not [string]::IsNullOrWhiteSpace($c) -and -not $candidates.Contains($c)) {
            [void]$candidates.Add($c)
        }
    }

    foreach ($candPath in $candidates) {
        if (Test-Path -LiteralPath $candPath -PathType Leaf -ErrorAction SilentlyContinue) {
            try {
                $rawJson = Get-Content -LiteralPath $candPath -Raw -Encoding UTF8 -ErrorAction SilentlyContinue
                if (-not [string]::IsNullOrWhiteSpace($rawJson)) {
                    $parsed = ConvertFrom-Json -InputObject $rawJson -ErrorAction SilentlyContinue
                    if ($parsed) {
                        $imgType = "lean"
                        if ($parsed.PSObject.Properties['image_type'] -and -not [string]::IsNullOrWhiteSpace([string]$parsed.image_type)) {
                            $normT = ([string]$parsed.image_type).Trim().ToLowerInvariant()
                            if ($normT -in @("preinstalled", "lean")) { $imgType = $normT }
                        }
                        $wimVer = "unknown"
                        if ($parsed.PSObject.Properties['wim_version'] -and -not [string]::IsNullOrWhiteSpace([string]$parsed.wim_version)) {
                            $wimVer = ([string]$parsed.wim_version).Trim()
                        }
                        $preApps = @()
                        if ($parsed.PSObject.Properties['preinstalled_apps'] -and $parsed.preinstalled_apps) {
                            $preApps = @($parsed.preinstalled_apps | ForEach-Object { ([string]$_).Trim() } | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
                        }
                        $builtAt = if ($parsed.PSObject.Properties['built_at'] -and -not [string]::IsNullOrWhiteSpace([string]$parsed.built_at)) { ([string]$parsed.built_at).Trim() } else { "" }
                        return [PSCustomObject]@{
                            image_type        = $imgType
                            wim_version       = $wimVer
                            preinstalled_apps = $preApps
                            built_at          = $builtAt
                            found             = $true
                            source_path       = $candPath
                        }
                    }
                }
            } catch {}
        }
    }

    return [PSCustomObject]@{
        image_type        = "lean"
        wim_version       = "unknown"
        preinstalled_apps = @()
        built_at          = ""
        found             = $false
        source_path       = ""
    }
}

function Test-ProgramiDirComplete {
    param(
        [string]$DirPath,
        [switch]$Quiet,
        [string[]]$RequiredApps = @()
    )
    if ([string]::IsNullOrWhiteSpace($DirPath) -or -not (Test-Path -LiteralPath $DirPath -ErrorAction SilentlyContinue)) {
        if (-not $Quiet) {
            Write-ServisLog "Test-ProgramiDirComplete: Direktorijum '$DirPath' ne postoji ili nije dostupan." -Level "WARN" -Component "NasProgrami"
            if (Get-Command Send-IpcLog -ErrorAction SilentlyContinue) {
                Send-IpcLog -Message "Direktorijum sa programima '$DirPath' ne postoji ili nije dostupan." -Level "WARN" -Tag "[NasProgrami]"
            }
        }
        return [PSCustomObject]@{ IsComplete = $false; HasOffice = $false; HasAdobe = $false; HasBatteryBarOk = $false; MissingInstallers = @($RequiredApps) }
    }
    $hasOffice = (Test-Path -LiteralPath (Join-Path $DirPath "Office 2024\files\setup.exe") -ErrorAction SilentlyContinue) -or `
                 (Test-Path -LiteralPath (Join-Path $DirPath "Office 2024\setup.exe") -ErrorAction SilentlyContinue) -or `
                 (Test-Path -LiteralPath (Join-Path $DirPath "Programi\Office 2024\files\setup.exe") -ErrorAction SilentlyContinue) -or `
                 (Test-Path -LiteralPath (Join-Path $DirPath "Programi\Office 2024\setup.exe") -ErrorAction SilentlyContinue)
    $hasAdobe = (Test-Path -LiteralPath (Join-Path $DirPath "Adobe.Acrobat.Pro.2024.x64.Multilingual\Adobe Acrobat Pro\AcroPro.msi") -ErrorAction SilentlyContinue) -or `
                (Test-Path -LiteralPath (Join-Path $DirPath "Acrobat Pro 2024\Adobe Acrobat Pro\AcroPro.msi") -ErrorAction SilentlyContinue)
    $hasBattery = $false
    try { if (Get-CimInstance Win32_Battery -ErrorAction SilentlyContinue) { $hasBattery = $true } } catch {}
    $hasBatteryBar = (Test-Path -LiteralPath (Join-Path $DirPath "BatteryBar\BatteryBar.exe") -ErrorAction SilentlyContinue) -or `
                     (Test-Path -LiteralPath (Join-Path $DirPath "Programi\BatteryBar\BatteryBar.exe") -ErrorAction SilentlyContinue)
    $hasBatteryBarOk = if ($hasBattery) { [bool]$hasBatteryBar } else { $true }

    # IMP-112: Kada je zadat ciljani skup RequiredApps (Golden Preinstalled ISO Opcija B), validiraj samo trazene instalere
    $reqClean = @($RequiredApps | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | ForEach-Object { $_.Trim() } | Select-Object -Unique)
    if ($reqClean.Count -gt 0) {
        $missingInst = [System.Collections.Generic.List[string]]::new()
        foreach ($reqApp in $reqClean) {
            $reqOk = $true
            switch -Regex ($reqApp.ToLowerInvariant()) {
                '^(office|office 2024|microsoft office)$' {
                    $reqOk = [bool]$hasOffice
                }
                '^(acrobat|adobe acrobat|adobe acrobat pro|adobe\.acrobat\.pro\.2024\.x64\.multilingual|acrobat pro 2024)$' {
                    $reqOk = [bool]$hasAdobe
                }
                '^batterybar$' {
                    $reqOk = [bool]$hasBatteryBar
                }
                '^(chrome|google chrome)$' {
                    $reqOk = (Test-Path -LiteralPath (Join-Path $DirPath "GoogleChromeStandaloneEnterprise64.msi") -ErrorAction SilentlyContinue) -or `
                             (Test-Path -LiteralPath (Join-Path $DirPath "ChromeSetup.exe") -ErrorAction SilentlyContinue) -or `
                             (@(Get-ChildItem -LiteralPath $DirPath -Filter "*chrome*.msi" -Recurse -File -ErrorAction SilentlyContinue).Count -gt 0) -or `
                             (@(Get-ChildItem -LiteralPath $DirPath -Filter "*chrome*.exe" -Recurse -File -ErrorAction SilentlyContinue).Count -gt 0)
                }
                '^winrar$' {
                    $reqOk = (@(Get-ChildItem -LiteralPath $DirPath -Filter "*winrar*.exe" -Recurse -File -ErrorAction SilentlyContinue).Count -gt 0)
                }
                '^(dotnet|runtimes|vcredist)$' {
                    $reqOk = (Test-Path -LiteralPath (Join-Path $DirPath "DotNet") -ErrorAction SilentlyContinue) -or `
                             (Test-Path -LiteralPath (Join-Path $DirPath "VCRedist") -ErrorAction SilentlyContinue) -or `
                             (@(Get-ChildItem -LiteralPath $DirPath -Filter "*windowsdesktop-runtime*.exe" -Recurse -File -ErrorAction SilentlyContinue).Count -gt 0) -or `
                             (@(Get-ChildItem -LiteralPath $DirPath -Filter "*VC_redist*.exe" -Recurse -File -ErrorAction SilentlyContinue).Count -gt 0)
                }
                default {
                    $reqOk = (Test-Path -LiteralPath (Join-Path $DirPath $reqApp) -ErrorAction SilentlyContinue)
                }
            }
            if (-not $reqOk) {
                [void]$missingInst.Add($reqApp)
            }
        }
        $isCompleteTargeted = ($missingInst.Count -eq 0)
        if (-not $isCompleteTargeted -and -not $Quiet) {
            Write-ServisLog "Test-ProgramiDirComplete: Ciljani instaleri u '$DirPath' nisu kompletni (Nedostaju: $($missingInst -join ', '))." -Level "INFO" -Component "NasProgrami"
        }
        return [PSCustomObject]@{
            IsComplete        = $isCompleteTargeted
            HasOffice         = $hasOffice
            HasAdobe          = $hasAdobe
            HasBatteryBarOk   = $hasBatteryBarOk
            MissingInstallers = @($missingInst)
        }
    }

    $isComplete = ($hasOffice -and $hasAdobe -and $hasBatteryBarOk)
    if (-not $isComplete -and -not $Quiet) {
        Write-ServisLog "Test-ProgramiDirComplete: Paketi u '$DirPath' nisu kompletni (Office=$hasOffice, Adobe=$hasAdobe, BatteryBar=$hasBatteryBarOk)." -Level "WARN" -Component "NasProgrami"
        if (Get-Command Send-IpcLog -ErrorAction SilentlyContinue) {
            Send-IpcLog -Message "Instalacioni paketi u '$DirPath' nisu kompletni (Office=$hasOffice, Adobe=$hasAdobe, BatteryBar=$hasBatteryBarOk)." -Level "WARN" -Tag "[NasProgrami]"
        }
    }
    return [PSCustomObject]@{
        IsComplete        = $isComplete
        HasOffice         = $hasOffice
        HasAdobe          = $hasAdobe
        HasBatteryBarOk   = $hasBatteryBarOk
        MissingInstallers = @()
    }
}

function Ensure-ProgramiAvailable {
    param(
        [switch]$Interactive = $true,
        [string]$OnlyApp = "",
        [string[]]$SubFolders = @(),
        [string[]]$MissingApps = $null,
        [string]$SystemLogDir = "C:\Windows\Logs\WIS"
    )
    $localProg = "C:\setup\Programi"
    Repair-ProgramiNesting $localProg
    Grant-SetupAclPermissions -Path "C:\setup"
    Grant-SetupAclPermissions -Path $localProg
    $progDir = Get-ProgramiDir

    $isTargeted = (-not [string]::IsNullOrWhiteSpace($OnlyApp)) -or ($SubFolders -and $SubFolders.Count -gt 0)
    if ($isTargeted) {
        return $progDir
    }

    # IMP-112: Proveri profil slike (image-profile.json), $isLaptop i stvarnu prisutnost instaliranih aplikacija pre punog NAS kopiranja
    $imgProf = Get-WisImageProfile -SystemLogDir $SystemLogDir
    $isLaptop = $false
    try {
        if (Get-Command Test-BatteryPresent -ErrorAction SilentlyContinue) {
            $isLaptop = [bool](Test-BatteryPresent)
        } elseif (Get-Command Test-DeviceIsPortable -ErrorAction SilentlyContinue) {
            $isLaptop = [bool](Test-DeviceIsPortable)
        } elseif (Get-CimInstance Win32_Battery -ErrorAction SilentlyContinue) {
            $isLaptop = $true
        } else {
            $encEnsure = Get-CimInstance Win32_SystemEnclosure -ErrorAction SilentlyContinue
            if ($encEnsure -and $encEnsure.ChassisTypes) {
                foreach ($eCt in @($encEnsure.ChassisTypes)) {
                    if ($eCt -in @(8, 9, 10, 11, 12, 14, 30, 31, 32)) { $isLaptop = $true; break }
                }
            }
        }
    } catch {}

    $effectiveMissing = [System.Collections.Generic.List[string]]::new()
    if ($null -ne $MissingApps) {
        foreach ($ma in $MissingApps) {
            if (-not [string]::IsNullOrWhiteSpace($ma) -and -not $effectiveMissing.Contains($ma.Trim())) {
                [void]$effectiveMissing.Add($ma.Trim())
            }
        }
    } else {
        $hasPresenceCmd = [bool](Get-Command Get-WisAppPresence -ErrorAction SilentlyContinue)
        $checkApps = @("VCRedist", "DotNet", "WinRAR", "Chrome", "Acrobat", "Office")
        if ($isLaptop) { $checkApps += "BatteryBar" }

        foreach ($cApp in $checkApps) {
            $isInst = $false
            if ($hasPresenceCmd) {
                try {
                    $pr = Get-WisAppPresence -AppId $cApp
                    if ($pr -and $pr.Installed) { $isInst = $true }
                } catch {}
            } else {
                switch ($cApp) {
                    "VCRedist"   { $isInst = (Test-Path -LiteralPath "C:\Windows\System32\vcruntime140.dll" -ErrorAction SilentlyContinue) -and (Test-Path -LiteralPath "C:\Windows\SysWOW64\vcruntime140.dll" -ErrorAction SilentlyContinue) }
                    "DotNet"     { $isInst = (Test-Path -LiteralPath "C:\Program Files\dotnet\shared\Microsoft.WindowsDesktop.App" -ErrorAction SilentlyContinue) }
                    "WinRAR"     { $isInst = (Test-Path -LiteralPath "C:\Program Files\WinRAR\WinRAR.exe" -ErrorAction SilentlyContinue) -or (Test-Path -LiteralPath "C:\Program Files (x86)\WinRAR\WinRAR.exe" -ErrorAction SilentlyContinue) }
                    "Chrome"     { $isInst = (Test-Path -LiteralPath "C:\Program Files\Google\Chrome\Application\chrome.exe" -ErrorAction SilentlyContinue) -or (Test-Path -LiteralPath "C:\Program Files (x86)\Google\Chrome\Application\chrome.exe" -ErrorAction SilentlyContinue) }
                    "Acrobat"    { $isInst = (Test-Path -LiteralPath "C:\Program Files\Adobe\Acrobat DC\Acrobat\Acrobat.exe" -ErrorAction SilentlyContinue) -or (Test-Path -LiteralPath "C:\Program Files (x86)\Adobe\Acrobat DC\Acrobat\Acrobat.exe" -ErrorAction SilentlyContinue) -or (Test-Path -LiteralPath "C:\Program Files\Adobe\Acrobat 2024\Acrobat\Acrobat.exe" -ErrorAction SilentlyContinue) }
                    "Office"     { $isInst = (Test-Path -LiteralPath "C:\Program Files\Microsoft Office\root\Office16\WINWORD.EXE" -ErrorAction SilentlyContinue) -or (Test-Path -LiteralPath "C:\Program Files (x86)\Microsoft Office\root\Office16\WINWORD.EXE" -ErrorAction SilentlyContinue) }
                    "BatteryBar" { $isInst = (Test-Path -LiteralPath "C:\Program Files\BatteryBar\BatteryBar.exe" -ErrorAction SilentlyContinue) -or (Test-Path -LiteralPath "C:\Program Files (x86)\BatteryBar\BatteryBar.exe" -ErrorAction SilentlyContinue) }
                }
            }
            if (-not $isInst) {
                [void]$effectiveMissing.Add($cApp)
            }
        }
    }

    # 0a. Ako su SVE aplikacije vec preinstalirane ($missingApps.Count -eq 0), potpuno preskoci Sync-ProgramiFromNas (0 MB prenosa)
    if ($effectiveMissing.Count -eq 0) {
        Write-ServisLog "[IMP-112] Sve obavezne aplikacije su vec instalirane (image_type=$($imgProf.image_type), wim_version=$($imgProf.wim_version), isLaptop=$isLaptop). Preskacem Sync-ProgramiFromNas (0 MB prenosa)." -Level "INFO" -Component "PackageChecker"
        return $progDir
    }

    # 0b. Ako fale SAMO lagane aplikacije bez Office/Acrobat (Golden Preinstalled ISO Opcija B), radi ciljani delta sync (~130 MB umesto 4 GB)
    $needsHeavyApps = ($effectiveMissing -contains "Office") -or ($effectiveMissing -contains "Acrobat")
    if (-not $needsHeavyApps) {
        $missingArr = @($effectiveMissing)
        Write-ServisLog "[IMP-112] Delta rezim paketa (image_type=$($imgProf.image_type), wim_version=$($imgProf.wim_version), isLaptop=$isLaptop): Office i Acrobat su vec preinstalirani. Nedostaju samo: [$($missingArr -join ', ')]." -Level "INFO" -Component "PackageChecker"
        $checkSubsetInit = Test-ProgramiDirComplete -DirPath $progDir -RequiredApps $missingArr -Quiet
        if ($checkSubsetInit.IsComplete) {
            Write-ServisLog "[IMP-112] Svi trazeni delta instaleri ($($missingArr -join ', ')) vec postoje lokalno u '$progDir'. Preskacem NAS prenos." -Level "INFO" -Component "PackageChecker"
            return $progDir
        }
        $checkSubsetLocal = Test-ProgramiDirComplete -DirPath $localProg -RequiredApps $missingArr -Quiet
        if (-not $checkSubsetLocal.IsComplete) {
            Write-ServisLog "[IMP-112] Pokrecem ciljani NAS delta sync (-SubFolders $($missingArr -join ',')) umesto punog 4 GB prenosa..." -Level "INFO" -Component "PackageChecker"
            $nasSubsetOk = Sync-ProgramiFromNas -DestinationDir $localProg -SubFolders $missingArr -Force
            if ($nasSubsetOk) {
                $progDir = Get-ProgramiDir
                return $progDir
            }
            Write-ServisLog "[IMP-112] Upozorenje: Ciljani NAS delta sync za [$($missingArr -join ', ')] nije uspeo; nastavak sa raspolozivim izvorima/fallbackom." -Level "WARN" -Component "NasProgrami"
        }
        $progDir = Get-ProgramiDir
        return $progDir
    }

    $checkInit = Test-ProgramiDirComplete -DirPath $progDir
    if ($checkInit.IsComplete) {
        return $progDir
    }

    Write-ServisLog "Instalacioni paketi nisu kompletni na SSD-u (Office=$($checkInit.HasOffice), Adobe=$($checkInit.HasAdobe), BatteryBar=$($checkInit.HasBatteryBarOk))." -Level "WARN" -Component "PackageChecker"

    # 1. Provera postojanja fizickog USB fles drajva sa kompletnim paketima
    $foundDrive = Find-SourceDrive
    if ($foundDrive) {
        $usbProgCandidates = @(
            (Join-Path $foundDrive "Programi"),
            (Join-Path $foundDrive 'sources\$OEM$\$1\setup\Programi')
        )
        $usbProg = $null
        foreach ($candUsb in $usbProgCandidates) {
            if ((Test-ProgramiDirComplete -DirPath $candUsb -Quiet).IsComplete) {
                $usbProg = $candUsb
                break
            }
        }

        if ($usbProg) {
            Write-Host "   [KOPIRANJE] Dopunjavam instalacione pakete sa USB-a na SSD..." -ForegroundColor Yellow
            Start-Process "robocopy.exe" -ArgumentList @($usbProg, $localProg, "/E", "/MT:32", "/J", "/R:1", "/W:1", "/NDL", "/NFL", "/NJH", "/NJS") -Wait -WindowStyle Hidden
            Repair-ProgramiNesting $localProg
            Grant-SetupAclPermissions -Path $localProg
            $Global:SourceRoot = $foundDrive
            Register-UsbLog -SourcePath $foundDrive
        } else {
            Write-ServisLog "Detektovan USB disk ($foundDrive), ali je u pitanju Lean medijum bez Office/Acrobat paketa." -Level "INFO" -Component "PackageChecker"
        }
    }

    # 2. Ako paketi na SSD-u i dalje nisu kompletni (npr. Lean USB ostao uboden ili PXE boot), obavezno povuci sa servisnog NAS-a
    $checkAfterUsb = Test-ProgramiDirComplete -DirPath $localProg -Quiet
    if (-not $checkAfterUsb.IsComplete) {
        Write-ServisLog "Pokrecem obavezno preuzimanje nedostajucih paketa (Office=$($checkAfterUsb.HasOffice), Adobe=$($checkAfterUsb.HasAdobe)) sa servisnog NAS-a..." -Level "INFO" -Component "PackageChecker"
        $nasOk = Sync-ProgramiFromNas -DestinationDir $localProg -Force
        if ($nasOk) {
            $progDir = Get-ProgramiDir
            return $progDir
        } else {
            Write-ServisLog "Preuzimanje instalacionih paketa sa servisnog NAS-a nije uspelo (Office=$($checkAfterUsb.HasOffice), Adobe=$($checkAfterUsb.HasAdobe))." -Level "ERROR" -Component "NasProgrami"
            if (Get-Command Send-IpcLog -ErrorAction SilentlyContinue) {
                Send-IpcLog -Message "Preuzimanje instalacionih paketa sa servisnog NAS-a nije uspelo (Office=$($checkAfterUsb.HasOffice), Adobe=$($checkAfterUsb.HasAdobe))." -Level "ERROR" -Tag "[NasProgrami]"
            }
        }
    }

    $progDir = Get-ProgramiDir
    return $progDir
}

# ------------------------------------------------------------------------------
# STATE MANAGEMENT
# ------------------------------------------------------------------------------

function Save-State {
    if (-not $Global:StateFile) {
        $Global:StateFile = "C:\setup\deploy-state.json"
    }
    if ($Global:State) {
        if ($Global:ProgramiDir) {
            $Global:State.ProgramiDir = $Global:ProgramiDir
        }
        if ($Global:SessionId) {
            if ($Global:State -is [hashtable]) {
                $Global:State["SessionId"] = $Global:SessionId
            } else {
                $Global:State | Add-Member -MemberType NoteProperty -Name "SessionId" -Value $Global:SessionId -Force -ErrorAction SilentlyContinue
            }
        }
        $dir = Split-Path $Global:StateFile -Parent
        if (-not (Test-Path $dir)) { New-Item -Path $dir -ItemType Directory -Force | Out-Null }
        $Global:State | ConvertTo-Json | Set-Content -Path $Global:StateFile -Encoding UTF8 -Force
    }
}

# ------------------------------------------------------------------------------
# APP WORKER IPC (PARALLEL PROCESS STATUS)
# ------------------------------------------------------------------------------

function Update-AppWorkerStatus {
    param(
        [string]$Status = "",
        [string]$CurrentApp = "",
        [int]$Progress = -1,
        [string]$AppId = "",
        [string]$AppState = ""
    )
    try {
        if (-not $Global:AppWorkerState) {
            $Global:AppWorkerState = @{
                Status     = "Running"
                CurrentApp = "Inicijalizacija..."
                Progress   = 0
                Apps       = @{
                    DotNet     = "Pending"
                    BatteryBar = "Pending"
                    Chrome     = "Pending"
                    WinRAR     = "Pending"
                    Acrobat    = "Pending"
                    Office     = "Pending"
                    Shortcuts  = "Pending"
                }
            }
        }
        if ($Status) { $Global:AppWorkerState.Status = $Status }
        if ($CurrentApp) { $Global:AppWorkerState.CurrentApp = $CurrentApp }
        if ($Progress -ge 0) { $Global:AppWorkerState.Progress = $Progress }
        if ($AppId -and $AppState) {
            $Global:AppWorkerState.Apps[$AppId] = $AppState
        }
        $Global:AppWorkerState.Timestamp = (Get-Date).ToString("yyyy-MM-dd HH:mm:ss")

        if (-not (Test-Path "C:\setup")) {
            New-Item -Path "C:\setup" -ItemType Directory -Force | Out-Null
        }
        $Global:AppWorkerState | ConvertTo-Json -Depth 3 | Set-Content -Path "C:\setup\app_worker_status.json" -Encoding UTF8 -Force
    } catch {}
}

function Get-AppWorkerStatus {
    $sf = "C:\setup\app_worker_status.json"
    if (Test-Path $sf) {
        try {
            return (Get-Content -Path $sf -Raw -ErrorAction SilentlyContinue | ConvertFrom-Json)
        } catch {}
    }
    if ($Global:AppWorkerState) {
        return $Global:AppWorkerState
    }
    return $null
}

function Get-AppItemState {
    param($Map, [string]$Id)
    if (-not $Map -or -not $Id) { return "Pending" }
    try {
        if ($Map -is [System.Collections.IDictionary]) {
            if ($Map.Contains($Id)) { return $Map[$Id] }
            return "Pending"
        }
        if ($Map.PSObject.Properties[$Id]) {
            return $Map.$Id
        }
    } catch {}
    return "Pending"
}

# ------------------------------------------------------------------------------
# WINDOW MANAGEMENT, RESPONSIVE LAYOUT & TUI MASTER DASHBOARD
# ------------------------------------------------------------------------------

function Set-ConsoleMaximized {
    <#
    .SYNOPSIS
        Maksimizuje prozor PowerShell konzole i prosiruje bafer na pun ekran.
    .DESCRIPTION
        Kombinuje poziv Win32 API ShowWindow (SW_MAXIMIZE = 3) za conhost i Windows Terminal
        i sinhronizuje PowerShell RawUI bafer sa maksimalnom fizickom velicinom ekrana.
    #>
    param()

    # Pozadinski radnik (Worker mode) ne menja dimenzije prozora
    if ($Global:IsWorkerMode -or $env:WIS_WORKER -eq "1") { return }

    # 1. Win32 API poziv za maksimizaciju prozora
    try {
        if (-not ([System.Management.Automation.PSTypeName]'WIS.Win32ConsoleHelper').Type) {
            Add-Type -MemberDefinition @'
            [DllImport("user32.dll")]
            public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
            [DllImport("user32.dll")]
            public static extern bool SetForegroundWindow(IntPtr hWnd);
            [DllImport("user32.dll")]
            public static extern bool BringWindowToTop(IntPtr hWnd);
            [DllImport("kernel32.dll")]
            public static extern IntPtr GetConsoleWindow();
            [DllImport("kernel32.dll", SetLastError = true)]
            public static extern IntPtr GetStdHandle(int nStdHandle);
            [DllImport("kernel32.dll", SetLastError = true)]
            public static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);
            [DllImport("kernel32.dll", SetLastError = true)]
            public static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);

            public static void DisableQuickEdit() {
                try {
                    IntPtr hStdin = GetStdHandle(-10); // STD_INPUT_HANDLE = -10
                    uint mode;
                    if (GetConsoleMode(hStdin, out mode)) {
                        mode &= ~0x0040u; // ENABLE_QUICK_EDIT_MODE = 0x0040
                        mode |= 0x0080u;  // ENABLE_EXTENDED_FLAGS = 0x0080
                        SetConsoleMode(hStdin, mode);
                    }
                } catch {}
            }
'@ -Name 'Win32ConsoleHelper' -Namespace 'WIS' -ErrorAction SilentlyContinue
        }

        # Trajno onemogucavanje QuickEdit moda radi sprecavanja pauziranja skripte
        try {
            [WIS.Win32ConsoleHelper]::DisableQuickEdit()
        } catch {}
        try {
            & reg.exe add "HKCU\Console" /v "QuickEdit" /t REG_DWORD /d 0 /f >$null 2>&1
        } catch {}

        # Standardni Console Host (conhost.exe)
        $hWnd = [WIS.Win32ConsoleHelper]::GetConsoleWindow()
        if ($hWnd -and $hWnd -ne [IntPtr]::Zero) {
            [WIS.Win32ConsoleHelper]::ShowWindow($hWnd, 3) | Out-Null # 3 = SW_MAXIMIZE
            [WIS.Win32ConsoleHelper]::SetForegroundWindow($hWnd) | Out-Null
            [WIS.Win32ConsoleHelper]::BringWindowToTop($hWnd) | Out-Null
        }

        # Glavni prozor procesa
        $mainHwnd = [System.Diagnostics.Process]::GetCurrentProcess().MainWindowHandle
        if ($mainHwnd -and $mainHwnd -ne [IntPtr]::Zero -and $mainHwnd -ne $hWnd) {
            [WIS.Win32ConsoleHelper]::ShowWindow($mainHwnd, 3) | Out-Null
            [WIS.Win32ConsoleHelper]::SetForegroundWindow($mainHwnd) | Out-Null
            [WIS.Win32ConsoleHelper]::BringWindowToTop($mainHwnd) | Out-Null
        }

        # Provera roditeljskog procesa ukoliko se izvrsava unutar Windows Terminal-a (wt.exe)
        try {
            $parentProcId = (Get-CimInstance Win32_Process -Filter "ProcessId = $PID" -ErrorAction SilentlyContinue).ParentProcessId
            if ($parentProcId) {
                $pProc = Get-Process -Id $parentProcId -ErrorAction SilentlyContinue
                if ($pProc -and $pProc.ProcessName -match '(?i)WindowsTerminal|wt') {
                    if ($pProc.MainWindowHandle -and $pProc.MainWindowHandle -ne [IntPtr]::Zero) {
                        [WIS.Win32ConsoleHelper]::ShowWindow($pProc.MainWindowHandle, 3) | Out-Null
                        [WIS.Win32ConsoleHelper]::SetForegroundWindow($pProc.MainWindowHandle) | Out-Null
                        [WIS.Win32ConsoleHelper]::BringWindowToTop($pProc.MainWindowHandle) | Out-Null
                    }
                }
            }
        } catch {}
    } catch {}

    # 2. Sinhronizacija RawUI bafera i velicine prozora na fizicki maksimum ekrana
    try {
        $raw = $Host.UI.RawUI
        if ($raw) {
            $maxPhys = $raw.MaxPhysicalWindowSize
            if ($maxPhys -and $maxPhys.Width -gt 0) {
                $buf = $raw.BufferSize
                if ($maxPhys.Width -gt $buf.Width) {
                    $buf.Width = $maxPhys.Width
                }
                if ($maxPhys.Height -gt $buf.Height) {
                    $buf.Height = [math]::Max($buf.Height, 3000)
                }
                $raw.BufferSize = $buf
                $raw.WindowSize = $maxPhys
            }
        }
    } catch {}
}

function Get-ServisConsoleWidth {
    <#
    .SYNOPSIS
        Dinamicki ocitava trenutnu sirinu konzole i ostavlja 1 karakter rezerve za prelom.
    #>
    try {
        $w = $Host.UI.RawUI.WindowSize.Width
        if ($w -and $w -ge 80) {
            return ($w - 1)
        }
    } catch {}
    return 120
}

function Write-ServisItemsGrid {
    <#
    .SYNOPSIS
        Iscrtava mrezu statusa aplikacija prilagodjavajuci se punoj sirini ekrana.
    #>
    param(
        [Parameter(Mandatory=$true)]$Items,
        [int]$Width = 120,
        [string]$Prefix = " | Programi: ",
        [string]$ContinuationPrefix = " |           "
    )
    if (-not $Items -or $Items.Count -eq 0) { return }

    $gridWidth = [math]::Max(60, $Width - $Prefix.Length)
    $count = $Items.Count

    # Odluka o broju kolona na osnovu raspolozive sirine:
    # Ako je $gridWidth >= ($count * 20) (npr. >= 140 za 7 stavki), sve staje u 1 red punom sirinom
    # Ako je $gridWidth >= 95, 4 stavke u prvom, 3 u drugom redu
    # Za uze ekrane (npr. 80 kolona), 3 stavke u prvom, 4 u drugom redu
    $rows = @()
    if ($gridWidth -ge ($count * 20)) {
        $rows += ,@($Items)
    } elseif ($gridWidth -ge 95) {
        $splitIdx = [math]::Min(4, $count)
        $rows += ,@($Items[0..($splitIdx - 1)])
        if ($count -gt $splitIdx) {
            $rows += ,@($Items[$splitIdx..($count - 1)])
        }
    } else {
        $splitIdx = [math]::Min(3, $count)
        $rows += ,@($Items[0..($splitIdx - 1)])
        if ($count -gt $splitIdx) {
            $rows += ,@($Items[$splitIdx..($count - 1)])
        }
    }

    for ($r = 0; $r -lt $rows.Count; $r++) {
        $rowItems = $rows[$r]
        $colCount = $rowItems.Count
        $colWidth = [math]::Max(14, [math]::Floor($gridWidth / $colCount))

        $linePfx = if ($r -eq 0) { $Prefix } else { $ContinuationPrefix }
        Write-Host $linePfx -ForegroundColor DarkGray -NoNewline

        for ($c = 0; $c -lt $colCount; $c++) {
            $it = $rowItems[$c]
            $dName = $it.Name
            $maxNameLen = [math]::Max(6, $colWidth - 6)
            if ($dName.Length -gt $maxNameLen) {
                $dName = $dName.Substring(0, $maxNameLen - 2) + ".."
            }

            Write-Host "$($it.Tag) " -ForegroundColor $it.Color -NoNewline
            $txtCol = if ($it.Status -eq 'Running') { 'Yellow' } elseif ($it.Status -eq 'Completed') { 'Green' } elseif ($it.Status -eq 'Failed') { 'Red' } else { 'DarkGray' }

            if ($c -lt ($colCount - 1)) {
                $remSpace = [math]::Max(1, $colWidth - 5 - $dName.Length)
                Write-Host $dName -ForegroundColor $txtCol -NoNewline
                Write-Host (" " * $remSpace) -NoNewline
            } else {
                Write-Host $dName -ForegroundColor $txtCol
            }
        }
    }
}

function Play-ServisAudioNotification {
    param(
        [Parameter(Mandatory=$false)]
        [ValidateSet("UsbFree", "Success", "Alert", "Error", "Warning")]
        [string]$Type = "Success"
    )
    try {
        switch ($Type) {
            "UsbFree" {
                # 1 kratak bip (800Hz, 300ms) kada je fles kesiran na SSD
                [Console]::Beep(800, 300)
            }
            "Success" {
                # Pobednicka melodija (1000Hz 200ms + 1500Hz 400ms) za 100% zavrsen sistem
                [Console]::Beep(1000, 200)
                Start-Sleep -Milliseconds 100
                [Console]::Beep(1500, 400)
            }
            "Alert" {
                # Upozorenje (1000Hz 250ms + 800Hz 350ms)
                [Console]::Beep(1000, 250)
                Start-Sleep -Milliseconds 100
                [Console]::Beep(800, 350)
            }
            "Warning" {
                # Upozorenje / Obavestenje o restartu (900Hz 200ms + 900Hz 200ms)
                [Console]::Beep(900, 200)
                Start-Sleep -Milliseconds 80
                [Console]::Beep(900, 200)
            }
            "Error" {
                # Greska (1000Hz 250ms + 800Hz 350ms)
                [Console]::Beep(1000, 250)
                Start-Sleep -Milliseconds 100
                [Console]::Beep(800, 350)
            }
        }
    } catch {}
}

function Get-RealAppStatuses {
    # 1. Provera hardvera - prisustvo baterije (laptop vs desktop)
    $isLaptop = $false
    if ($Global:HwInfo -and $Global:HwInfo.BatteryStr -and ($Global:HwInfo.BatteryStr -notmatch "N/A|Nema")) {
        $isLaptop = $true
    } elseif (Get-Command Test-BatteryPresent -ErrorAction SilentlyContinue) {
        $isLaptop = Test-BatteryPresent
    } else {
        try {
            $bat = Get-CimInstance -ClassName Win32_Battery -ErrorAction SilentlyContinue
            if ($bat) { $isLaptop = $true }
        } catch {}
    }

    # 2. Preuzimanje statusa radnika iz deljenog JSON fajla
    $worker = Get-AppWorkerStatus
    $workerApps = if ($worker -and $worker.Apps) { $worker.Apps } else { $null }
    $workerFinished = ($worker -and ($worker.Status -eq "Completed" -or $worker.Status -eq "Failed"))
    $workerCurrent = if ($worker -and $worker.CurrentApp) { $worker.CurrentApp } else { "" }
    $workerProgress = if ($worker -and ($null -ne $worker.Progress)) { [int]$worker.Progress } else { 0 }

    # 3. Ako je dostupna funkcija Test-AllServisProgramsInstalled, iskoristi njene podatke
    $fullTestResults = $null
    if (Get-Command Test-AllServisProgramsInstalled -ErrorAction SilentlyContinue) {
        try {
            $fullTestResults = Test-AllServisProgramsInstalled
        } catch {}
    }

    # 4. Provera stvarne instalacije na disku za svaku aplikaciju
    # DotNet
    $hasDotNet = if ($fullTestResults -and $fullTestResults.DotNet) {
        [bool]$fullTestResults.DotNet.Installed
    } else {
        (Test-Path "C:\Program Files\dotnet\shared\Microsoft.WindowsDesktop.App") -or `
        [bool]((Get-ItemProperty "HKLM:\SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedhost" -ErrorAction SilentlyContinue).Version)
    }

    # BatteryBar
    $hasBatteryBar = if ($fullTestResults -and $fullTestResults.BatteryBar) {
        [bool]$fullTestResults.BatteryBar.Installed
    } else {
        (Test-Path "C:\Program Files\BatteryBar\BatteryBar.exe") -or (Test-Path "C:\Program Files (x86)\BatteryBar\BatteryBar.exe")
    }

    # Chrome
    $hasChrome = if ($fullTestResults -and $fullTestResults.Chrome) {
        [bool]$fullTestResults.Chrome.Installed
    } else {
        (Test-Path "C:\Program Files\Google\Chrome\Application\chrome.exe") -or `
        (Test-Path "C:\Program Files (x86)\Google\Chrome\Application\chrome.exe")
    }

    # WinRAR
    $hasWinrar = if ($fullTestResults -and $fullTestResults.WinRAR) {
        [bool]$fullTestResults.WinRAR.Installed
    } else {
        (Test-Path "C:\Program Files\WinRAR\WinRAR.exe") -or `
        (Test-Path "C:\Program Files (x86)\WinRAR\WinRAR.exe")
    }

    # Acrobat
    $hasAcrobat = if ($fullTestResults -and $fullTestResults.Acrobat) {
        [bool]$fullTestResults.Acrobat.Installed
    } else {
        (Test-Path "C:\Program Files\Adobe\Acrobat DC\Acrobat\Acrobat.exe") -or `
        (Test-Path "C:\Program Files (x86)\Adobe\Acrobat DC\Acrobat\Acrobat.exe") -or `
        (Test-Path "C:\Program Files\Adobe\Acrobat 2024\Acrobat\Acrobat.exe")
    }

    # Office
    $hasOffice = if ($fullTestResults -and $fullTestResults.Office) {
        [bool]$fullTestResults.Office.Installed
    } else {
        (Test-Path "C:\Program Files\Microsoft Office\root\Office16\WINWORD.EXE") -or `
        (Test-Path "C:\Program Files (x86)\Microsoft Office\root\Office16\WINWORD.EXE") -or `
        (Test-Path "C:\Program Files\Microsoft Office\root\Office16\EXCEL.EXE")
    }

    # Shortcuts
    $hasShortcuts = (Test-Path "C:\Users\Public\Desktop\Word.lnk") -or `
                    (Test-Path "C:\Users\Public\Desktop\Excel.lnk") -or `
                    (Test-Path "$([Environment]::GetFolderPath('CommonDesktopDirectory'))\Word.lnk")

    # Definicije svih pracenih stavki
    $itemsDef = @(
        @{ Id = "DotNet";     Name = ".NET Runtimes";       Installed = $hasDotNet;     Required = $true;    IsLaptop = $false },
        @{ Id = "BatteryBar"; Name = "BatteryBar";          Installed = $hasBatteryBar; Required = $isLaptop; IsLaptop = $isLaptop },
        @{ Id = "Chrome";     Name = "Google Chrome";       Installed = $hasChrome;     Required = $true;    IsLaptop = $false },
        @{ Id = "WinRAR";     Name = "WinRAR x64";          Installed = $hasWinrar;     Required = $true;    IsLaptop = $false },
        @{ Id = "Acrobat";    Name = "Acrobat Pro";         Installed = $hasAcrobat;    Required = $true;    IsLaptop = $false },
        @{ Id = "Office";     Name = "MS Office";           Installed = $hasOffice;     Required = $true;    IsLaptop = $false },
        @{ Id = "Shortcuts";  Name = "Precice";             Installed = $hasShortcuts;  Required = $true;    IsLaptop = $false }
    )

    $curPhase = if ($Global:State -and $Global:State.Phase) { [int]$Global:State.Phase } elseif ($Global:CurrentPhase) { [int]$Global:CurrentPhase } else { 1 }
    $resultList = @()

    foreach ($it in $itemsDef) {
        $wState = Get-AppItemState -Map $workerApps -Id $it.Id
        $st = "Pending"
        $tag = "[  ]"
        $col = "DarkGray"
        $dispName = $it.Name

        if ($it.Id -eq "BatteryBar" -and -not $it.IsLaptop) {
            # Desktop racunar bez baterije - BatteryBar se preskace
            $st = "Skipped"
            $tag = "[-]"
            $col = "DarkGray"
            $dispName = "BatteryBar (Desktop)"
        } elseif ($it.Installed) {
            # Stvarno instaliran i potvrdjen na disku
            $st = "Completed"
            $tag = "[OK]"
            $col = "Green"
            if ($it.Id -eq "BatteryBar") { $dispName = "BatteryBar (Laptop)" }
        } else {
            # Nije instaliran na disku
            if ($it.Id -eq "BatteryBar") { $dispName = "BatteryBar (Laptop)" }

            $isCurrentlyRunning = ($wState -eq "Running") -or `
                ($workerCurrent -and ($workerCurrent -match $it.Name -or ($it.Id -eq "Shortcuts" -and $workerCurrent -match "precice")))

            if ($isCurrentlyRunning) {
                $st = "Running"
                $tag = "[>>]"
                $col = "Yellow"
            } elseif ($wState -eq "Deferred") {
                $st = "Deferred"
                $tag = "[..]"
                $col = "Cyan"
            } elseif ($wState -eq "Skipped") {
                $st = "Skipped"
                $tag = "[-]"
                $col = "DarkGray"
            } elseif (($workerFinished -and $wState -ne "Deferred") -or $curPhase -ge 5 -or $wState -eq "Failed" -or ($wState -eq "Completed" -and -not $it.Installed)) {
                # Radnik je zavrsio ili je faza 5, a aplikacija NE POSTOJI na disku -> NEUSPELO / NEDOSTAJE
                $st = "Failed"
                $tag = "[!]"
                $col = "Red"
            } else {
                # Ceka red za instalaciju
                $st = "Pending"
                $tag = "[  ]"
                $col = "DarkGray"
            }
        }

        $resultList += [PSCustomObject]@{
            Id        = $it.Id
            Name      = $dispName
            RawName   = $it.Name
            Status    = $st
            Tag       = $tag
            Color     = $col
            Installed = $it.Installed
            Required  = $it.Required
            IsLaptop  = $it.IsLaptop
        }
    }

    $requiredItems = $resultList | Where-Object { $_.Required }
    $allRequiredInstalled = (($requiredItems | Where-Object { $_.Status -ne "Completed" }).Count -eq 0)
    $failedItems = $resultList | Where-Object { $_.Status -eq "Failed" }
    $runningItem = $resultList | Where-Object { $_.Status -eq "Running" } | Select-Object -First 1

    return [PSCustomObject]@{
        Items                = $resultList
        WorkerStatus         = $worker
        WorkerCurrent        = $workerCurrent
        WorkerProgress       = $workerProgress
        AllInstalled         = $allRequiredInstalled
        FailedCount          = $failedItems.Count
        RunningItem          = $runningItem
        IsLaptop             = $isLaptop
        FullTestResults      = $fullTestResults
    }
}

function Show-ServisDashboard {
    param(
        [string]$LiveStatus = "",
        [string[]]$SubLogs = @(),
        [string]$DownloadProgress = "",
        [string]$InstallProgress = ""
    )
    # Optimizacija: Uklonjeni Write-ServisLog pozivi po frejmu koji su gusili log fajlove

    try {
        # Flicker-free TUI: Sakrivanje trepcuceg kursora i repozicioniranje umesto brisanja ekrana
        try { [Console]::CursorVisible = $false } catch {}
        $W = Get-ServisConsoleWidth

        # Ako se promenila sirina ekrana ili je prvo iscrtavanje, ocisti ekran, inace vrati kursor na vrh (0,0)
        if (-not $Global:LastDashboardWidth -or $Global:LastDashboardWidth -ne $W) {
            Clear-Host
            $Global:LastDashboardWidth = $W
        } else {
            try {
                [Console]::SetCursorPosition(0, 0)
            } catch {
                Clear-Host
            }
        }

        if ($LiveStatus) {
            $Global:LastLiveStatus = $LiveStatus
        }

        # Periodični telemetrijski puls ka centralnom Hubu (na svakih ~2-3 sekunde)
        $nowTick = [Environment]::TickCount
        if (-not $Global:LastHeartbeatTick -or ($nowTick - $Global:LastHeartbeatTick) -ge 2500) {
            $Global:LastHeartbeatTick = $nowTick
            if (Get-Command Send-ServisHeartbeat -ErrorAction SilentlyContinue) {
                $null = Send-ServisHeartbeat -LiveStatus $LiveStatus
            }
        }

        # 1. Spinner / rotirajuci puls indikator rada (|, /, -, \)
        $spinner = @('|', '/', '-', '\')
        if ($null -eq $Global:PulseFrame -or $Global:PulseFrame -lt 0) { $Global:PulseFrame = 0 }
        $pulseChar = $spinner[$Global:PulseFrame % $spinner.Length]
        $Global:PulseFrame = ($Global:PulseFrame + 1) % 10000

        # ASCII Zaglavlje sa indikatorom aktivnosti punom sirinom ekrana
        Write-Host ("+" + ("=" * ($W - 2)) + "+") -ForegroundColor Cyan
        Write-Host "|" -ForegroundColor Cyan -NoNewline
        Write-Host " WIS SERVICE AUTO-DEPLOY TOOLKIT" -ForegroundColor Cyan -NoNewline
        $titlePfxLen = 32
        $pulseBadgeLen = 21
        $headerPad = [math]::Max(1, $W - 2 - $titlePfxLen - $pulseBadgeLen)
        Write-Host (" " * $headerPad) -NoNewline
        Write-Host "[ AKTIVNO / RADI: " -ForegroundColor Yellow -NoNewline
        Write-Host $pulseChar -ForegroundColor Green -NoNewline
        Write-Host " ] " -ForegroundColor Yellow -NoNewline
        Write-Host "|" -ForegroundColor Cyan
        Write-Host ("+" + ("=" * ($W - 2)) + "+") -ForegroundColor Cyan

        # Hardverske informacije
        $hw = $Global:HwInfo
        if (-not $hw -and (Get-Command Get-HardwareDiagnosticsInfo -ErrorAction SilentlyContinue)) {
            $hw = Get-HardwareDiagnosticsInfo
            $Global:HwInfo = $hw
        }

        if ($hw) {
            # Dinamicka raspodela kolona u zavisnosti od sirine $W
            # Linija 1: Uredjaj, Serijski broj, BIOS
            $avail1 = [math]::Max(45, $W - 27)
            $wDev = [math]::Floor($avail1 * 0.50)
            $wSn = [math]::Floor($avail1 * 0.28)
            $wBios = [math]::Max(8, $avail1 - $wDev - $wSn)

            $devStr = "$($hw.Manufacturer) $($hw.Model)".Trim()
            if ($devStr.Length -gt $wDev) { $devStr = $devStr.Substring(0, [math]::Max(5, $wDev - 3)) + "..." }
            $snStr = if ($hw.SerialNumber) { $hw.SerialNumber.Trim() } else { "N/A" }
            if ($snStr.Length -gt $wSn) { $snStr = $snStr.Substring(0, [math]::Max(5, $wSn - 3)) + "..." }
            $biosStr = if ($hw.BiosVersion) { $hw.BiosVersion.Trim() } else { "N/A" }
            if ($biosStr.Length -gt $wBios) { $biosStr = $biosStr.Substring(0, [math]::Max(4, $wBios - 2)) + ".." }

            # Linija 2: CPU, RAM, Disk
            $avail2 = [math]::Max(45, $W - 27)
            $wCpu = [math]::Floor($avail2 * 0.50)
            $wRam = [math]::Floor($avail2 * 0.25)
            $wDisk = [math]::Max(8, $avail2 - $wCpu - $wRam)

            $cpuShort = if ($hw.CpuStr) { $hw.CpuStr.Trim() } else { "CPU" }
            if ($cpuShort.Length -gt $wCpu) { $cpuShort = $cpuShort.Substring(0, [math]::Max(5, $wCpu - 3)) + "..." }
            $ramShort = if ($hw.RamStr) { ($hw.RamStr -replace '\s*\(.*?\)', '').Trim() } else { "RAM" }
            if ($ramShort.Length -gt $wRam) { $ramShort = $ramShort.Substring(0, [math]::Max(5, $wRam - 3)) + "..." }
            $diskShort = if ($hw.DiskStr) { ($hw.DiskStr -replace '\s*\(.*?\)', '').Trim() } else { "Disk" }
            if ($diskShort.Length -gt $wDisk) { $diskShort = $diskShort.Substring(0, [math]::Max(5, $wDisk - 3)) + "..." }

            # Linija 3: OS, Baterija / Napajanje
            $avail3 = [math]::Max(45, $W - 26)
            $wOs = [math]::Floor($avail3 * 0.65)
            $wBat = [math]::Max(12, $avail3 - $wOs)

            $osShort = "$($hw.OSName) $($hw.OSArch) ($($hw.OSBuild))".Trim()
            if ($osShort.Length -gt $wOs) { $osShort = $osShort.Substring(0, [math]::Max(5, $wOs - 3)) + "..." }
            $batShort = if ($hw.BatteryStr) { $hw.BatteryStr.Trim() } else { "N/A" }
            if ($batShort.Length -gt $wBat) { $batShort = $batShort.Substring(0, [math]::Max(5, $wBat - 3)) + "..." }

            # Linija 1: Uredjaj, Serijski, BIOS
            Write-Host " [*] UREDJAJ: " -ForegroundColor Cyan -NoNewline
            Write-Host ($devStr.PadRight($wDev)) -ForegroundColor White -NoNewline
            Write-Host " S/N: " -ForegroundColor DarkGray -NoNewline
            Write-Host ($snStr.PadRight($wSn)) -ForegroundColor Yellow -NoNewline
            Write-Host " BIOS: " -ForegroundColor DarkGray -NoNewline
            Write-Host $biosStr -ForegroundColor White

            # Linija 2: CPU, RAM, Disk
            Write-Host " [*] HARDVER: " -ForegroundColor Cyan -NoNewline
            Write-Host ($cpuShort.PadRight($wCpu)) -ForegroundColor White -NoNewline
            Write-Host " RAM: " -ForegroundColor DarkGray -NoNewline
            Write-Host ($ramShort.PadRight($wRam)) -ForegroundColor White -NoNewline
            Write-Host " DISK: " -ForegroundColor DarkGray -NoNewline
            Write-Host $diskShort -ForegroundColor White

            # Linija 3: OS, Baterija/Napajanje
            Write-Host " [*] SISTEM:  " -ForegroundColor Cyan -NoNewline
            Write-Host ($osShort.PadRight($wOs)) -ForegroundColor White -NoNewline
            Write-Host " NAPAJANJE: " -ForegroundColor DarkGray -NoNewline
            Write-Host $batShort -ForegroundColor White

            # Linija 4: Sigurnost i Mreza (Zasebni Kabl i Wi-Fi indikatori)
            Write-Host " [*] STATUS:  " -ForegroundColor Cyan -NoNewline

            $netStat = if (Get-Command Get-NetworkDetailStatus -ErrorAction SilentlyContinue) {
                Get-NetworkDetailStatus
            } else { $null }

            # Indikator za mrezni kabl (LAN / Ethernet)
            Write-Host "Kabl: [" -ForegroundColor DarkGray -NoNewline
            if ($netStat -and $netStat.LanConnected) {
                $lanSpd = if ($netStat.LanSpeed) { $netStat.LanSpeed } else { "Povezan" }
                Write-Host "* $lanSpd" -ForegroundColor Green -NoNewline
            } elseif ($netStat -and $netStat.LanPresent) {
                Write-Host "o Otkacen" -ForegroundColor Yellow -NoNewline
            } elseif ($netStat) {
                Write-Host "- Nema" -ForegroundColor DarkGray -NoNewline
            } else {
                $netIsOk = if (Get-Command Test-Internet -ErrorAction SilentlyContinue) { Test-Internet } else { $false }
                if ($netIsOk) { Write-Host "* Online" -ForegroundColor Green -NoNewline } else { Write-Host "o Otkacen" -ForegroundColor Yellow -NoNewline }
            }
            Write-Host "]  " -ForegroundColor DarkGray -NoNewline

            # Indikator za bezicnu mrezu (Wi-Fi)
            Write-Host "Wi-Fi: [" -ForegroundColor DarkGray -NoNewline
            if ($netStat -and $netStat.WifiConnected) {
                $wfName = if ($netStat.WifiSSID) { $netStat.WifiSSID } else { "Povezan" }
                if ($wfName.Length -gt 15) { $wfName = $wfName.Substring(0, 13) + ".." }
                Write-Host "* $wfName" -ForegroundColor Green -NoNewline
            } elseif ($Global:State -and $Global:State.WifiSSID) {
                $wfName = $Global:State.WifiSSID
                if ($wfName.Length -gt 15) { $wfName = $wfName.Substring(0, 13) + ".." }
                Write-Host "* $wfName" -ForegroundColor Green -NoNewline
            } elseif ($netStat -and $netStat.WifiPresent) {
                Write-Host "o Otkacen" -ForegroundColor Yellow -NoNewline
            } elseif ($hw.WifiOk -and $hw.WifiName -and $hw.WifiName -ne "Nije detektovan") {
                Write-Host "o Otkacen" -ForegroundColor Yellow -NoNewline
            } else {
                Write-Host "! Nema drajvera" -ForegroundColor Red -NoNewline
            }
            Write-Host "]  AP: [" -ForegroundColor DarkGray -NoNewline
            switch ($hw.AutopilotStatus) {
                "Autopilot"   { Write-Host "! AP-REG" -ForegroundColor Red -NoNewline }
                "StaleMarker" { Write-Host "~ ZAOSTALO" -ForegroundColor Yellow -NoNewline }
                "AadMdm"      { Write-Host "~ AAD/MDM" -ForegroundColor Yellow -NoNewline }
                default     {
                    if ($hw.Autopilot) {
                        Write-Host "! AP-REG" -ForegroundColor Red -NoNewline
                    } else {
                        Write-Host "* CISTO" -ForegroundColor Green -NoNewline
                    }
                }
            }
            Write-Host "]  SB: [" -ForegroundColor DarkGray -NoNewline
            if ($hw.SecureBoot) { Write-Host "* ON" -ForegroundColor Green -NoNewline } else { Write-Host "o OFF" -ForegroundColor Yellow -NoNewline }
            Write-Host "]  BitLock: [" -ForegroundColor DarkGray -NoNewline
            if ($hw.BitLocker) { Write-Host "* ON" -ForegroundColor Yellow -NoNewline } else { Write-Host "o OFF" -ForegroundColor Green -NoNewline }
            Write-Host "]  Hardver: [" -ForegroundColor DarkGray -NoNewline
            $hStat = if ($hw.HealthStatus) { $hw.HealthStatus } else { "OK" }
            switch ($hStat) {
                "CRITICAL" {
                    $dVal = if ($hw.DiskHealth) { "$($hw.DiskHealth)%" } else { "ALARM" }
                    Write-Host "! KRITICNO ($dVal)" -ForegroundColor Red -NoNewline
                }
                "WARNING" {
                    $dVal = if ($hw.DiskHealth) { "$($hw.DiskHealth)%" } else { "PAZNJA" }
                    Write-Host "~ UPOZORENJE ($dVal)" -ForegroundColor Yellow -NoNewline
                }
                default {
                    $dVal = if ($hw.DiskHealth) { " ($($hw.DiskHealth)%)" } else { "" }
                    Write-Host "* ZDRAV$dVal" -ForegroundColor Green -NoNewline
                }
            }
            Write-Host "]" -ForegroundColor DarkGray
        }

        # Obavestenje za USB fles i audio alert (centrirano punom sirinom)
        if ($Global:UsbCanBeRemoved) {
            if (-not $Global:UsbBeepPlayed) {
                Play-ServisAudioNotification -Type "UsbFree"
                $Global:UsbBeepPlayed = $true
            }
            $usbMsg = ">>>  [ USB FLES JE SLOBODAN - MOZETE GA IZVADITI ]  <<<"
            $padTotal = [math]::Max(0, $W - 2 - $usbMsg.Length)
            $padL = [math]::Floor($padTotal / 2)
            $padR = $padTotal - $padL
            Write-Host ("+" + ("=" * ($W - 2)) + "+") -ForegroundColor Green
            Write-Host ("|" + (" " * $padL) + $usbMsg + (" " * $padR) + "|") -ForegroundColor Green
            Write-Host ("+" + ("=" * ($W - 2)) + "+") -ForegroundColor Green
        } elseif ($Global:SourceRoot) {
            $inUseMsg = " [!] USB FLES JE U UPOTREBI ($($Global:SourceRoot)) - Ne vadite USB medijum iz racunara!"
            Write-Host $inUseMsg -ForegroundColor Yellow
        }
        Write-Host ("-" * $W) -ForegroundColor DarkGray

        # 2. Status Faza (Dinamicki rasporedjen pregled punom sirinom)
        Write-Host " FAZE: " -ForegroundColor Cyan -NoNewline
        $curPhase = if ($Global:State -and $Global:State.Phase) { [int]$Global:State.Phase } elseif ($Global:CurrentPhase) { [int]$Global:CurrentPhase } else { 1 }
        $upCycle = if ($Global:State -and $Global:State.UpdateCycle) { [int]$Global:State.UpdateCycle } else { 1 }
        $maxCycles = if ($Global:State -and $Global:State.MaxCycles) { [int]$Global:State.MaxCycles } else { 3 }

        # Reset beep flegova ako je faza ponovljena
        if ($curPhase -lt 6) {
            $Global:SuccessBeepPlayed = $false
            $Global:AlertBeepPlayed = $false
        }

        $phasesDef = @(
            @{ Num = 1; ShortName = "Mreza" },
            @{ Num = 2; ShortName = "Update" },
            @{ Num = 3; ShortName = "Optimizacija" },
            @{ Num = 4; ShortName = "Programi" },
            @{ Num = 5; ShortName = "Stres Test" },
            @{ Num = 6; ShortName = "Zavrsetak" }
        )

        $phaseGap = [math]::Max(1, [math]::Min(6, [math]::Floor(($W - 78) / 6)))
        for ($pi = 0; $pi -lt $phasesDef.Count; $pi++) {
            $p = $phasesDef[$pi]
            $icon = if ($curPhase -gt $p.Num) { "[OK]" } elseif ($curPhase -eq $p.Num) { "[>>]" } else { "[  ]" }
            $col = if ($curPhase -gt $p.Num) { "Green" } elseif ($curPhase -eq $p.Num) { "Yellow" } else { "DarkGray" }
            Write-Host "$icon " -ForegroundColor $col -NoNewline
            $lbl = "$($p.Num).$($p.ShortName)"
            if ($p.Num -eq 2 -and $curPhase -eq 2) {
                $lbl += "(K$upCycle/$maxCycles)"
            }
            Write-Host $lbl -ForegroundColor $(if ($curPhase -eq $p.Num) { 'White' } elseif ($curPhase -gt $p.Num) { 'Green' } else { 'DarkGray' }) -NoNewline
            if ($pi -lt ($phasesDef.Count - 1)) {
                Write-Host (" " * $phaseGap) -NoNewline
            }
        }
        Write-Host ""
        Write-Host ("-" * $W) -ForegroundColor DarkGray

        # 3. GLAVNI MODUL: TRENUTNE AKTIVNOSTI U REALNOM VREMENU
        $appData = Get-RealAppStatuses
        $maxTextW = [math]::Max(50, $W - 18)

        if ($curPhase -eq 2 -or $DownloadProgress -or $InstallProgress) {
            # --- PANEL [A]: WINDOWS UPDATE I DRAJVERI ---
            $titleA = " /--- [A] WINDOWS UPDATE I DRAJVERI (Krug $upCycle/$maxCycles) "
            $dashA = [math]::Max(2, $W - $titleA.Length - 1)
            Write-Host $titleA -ForegroundColor Cyan -NoNewline
            Write-Host ("-" * $dashA) -ForegroundColor DarkGray -NoNewline
            Write-Host "\\" -ForegroundColor DarkGray

            $currentWuPkg = $LiveStatus
            if ($currentWuPkg -match 'Instaliram paket \d+ od \d+: (.+)') {
                $currentWuPkg = $matches[1]
            } elseif ($currentWuPkg -match 'Preuzimam paket \d+ od \d+: (.+)') {
                $currentWuPkg = $matches[1]
            }
            if ($currentWuPkg.Length -gt $maxTextW) { $currentWuPkg = $currentWuPkg.Substring(0, $maxTextW - 3) + "..." }

            $actTitle = if ($LiveStatus -match ':') { ($LiveStatus -split ':')[0] } else { $LiveStatus }
            if ($actTitle.Length -gt $maxTextW) { $actTitle = $actTitle.Substring(0, $maxTextW - 3) + "..." }

            Write-Host " | Akcija:   " -ForegroundColor DarkGray -NoNewline
            Write-Host $actTitle -ForegroundColor White
            if ($currentWuPkg -and $currentWuPkg -ne $actTitle) {
                Write-Host " | Paket:    " -ForegroundColor DarkGray -NoNewline
                Write-Host $currentWuPkg -ForegroundColor Yellow
            }
            if ($DownloadProgress) {
                $dp = if ($DownloadProgress.Length -gt $maxTextW) { $DownloadProgress.Substring(0, $maxTextW - 3) + "..." } else { $DownloadProgress }
                Write-Host " | Preuzeto: " -ForegroundColor DarkGray -NoNewline
                Write-Host $dp -ForegroundColor Yellow
            }
            if ($InstallProgress) {
                $ip = if ($InstallProgress.Length -gt $maxTextW) { $InstallProgress.Substring(0, $maxTextW - 3) + "..." } else { $InstallProgress }
                Write-Host " | Ugradnja: " -ForegroundColor DarkGray -NoNewline
                Write-Host $ip -ForegroundColor Green
            }

            # --- PANEL [B]: PARALELNA INSTALACIJA PROGRAMA ---
            $titleB = " +--- [B] INSTALACIJA PROGRAMA (Paralelni SSD proces) "
            $dashB = [math]::Max(2, $W - $titleB.Length - 1)
            Write-Host $titleB -ForegroundColor Cyan -NoNewline
            Write-Host ("-" * $dashB) -ForegroundColor DarkGray -NoNewline
            Write-Host "+" -ForegroundColor DarkGray

            $slots = [math]::Max(20, [math]::Min(70, [math]::Round(($W - 32) * 0.45)))
            $filled = [math]::Min($slots, [math]::Max(0, [math]::Round(($appData.WorkerProgress / 100) * $slots)))
            $empty = $slots - $filled
            $appBar = ("=" * $filled) + ("-" * $empty)

            Write-Host " | Status:   " -ForegroundColor DarkGray -NoNewline
            if ($appData.AllInstalled) {
                Write-Host "[OK] Svi programi su uspesno verifikovani na SSD-u!" -ForegroundColor Green
            } elseif ($appData.WorkerStatus -and $appData.WorkerStatus.Status -eq "Running") {
                $curTxt = if ($appData.WorkerCurrent) { $appData.WorkerCurrent } else { "Instalacija u toku..." }
                if ($curTxt.Length -gt $maxTextW) { $curTxt = $curTxt.Substring(0, $maxTextW - 3) + "..." }
                Write-Host "[>> $pulseChar] $curTxt" -ForegroundColor Yellow
            } elseif ($appData.FailedCount -gt 0) {
                Write-Host "[!] UPOZORENJE: Pojedini programi nisu instalirani!" -ForegroundColor Red
            } else {
                Write-Host "[  ] Cekam pokretanje instalacije programa..." -ForegroundColor DarkGray
            }

            Write-Host " | Progres:  " -ForegroundColor DarkGray -NoNewline
            Write-Host "[$appBar] $($appData.WorkerProgress)%" -ForegroundColor Magenta

            # Ako se neka stavka trenutno instalira, prikazi rotirajuci puls i elastican mini bar
            if ($appData.RunningItem) {
                $bSlots = [math]::Max(14, [math]::Min(45, [math]::Round(($W - 45) * 0.3)))
                $bFilled = [math]::Min($bSlots, [math]::Max(0, [math]::Round(($appData.WorkerProgress / 100) * $bSlots)))
                $bEmpty = $bSlots - $bFilled
                $miniBar = ("=" * $bFilled) + ("-" * $bEmpty)
                Write-Host " | U toku:   " -ForegroundColor DarkGray -NoNewline
                Write-Host "[>> $pulseChar] " -ForegroundColor Cyan -NoNewline
                Write-Host ("{0,-18}" -f $appData.RunningItem.Name) -ForegroundColor Yellow -NoNewline
                Write-Host " [$miniBar] $($appData.WorkerProgress)%" -ForegroundColor Cyan
            }

            # Tabela stavki programa (Responsivna mreza punom sirinom)
            Write-ServisItemsGrid -Items $appData.Items -Width $W -Prefix " | Stavke:   " -ContinuationPrefix " |           "
            Write-Host (" \" + ("-" * ($W - 3)) + "/") -ForegroundColor DarkGray

        } elseif ($curPhase -eq 1) {
            # --- FAZA 1: STATUS MREZE I POVEZIVANJA ---
            $titleP1 = " /--- STATUS MREZE I POVEZIVANJA (Faza 1) "
            $dashP1 = [math]::Max(2, $W - $titleP1.Length - 1)
            Write-Host $titleP1 -ForegroundColor Cyan -NoNewline
            Write-Host ("-" * $dashP1) -ForegroundColor DarkGray -NoNewline
            Write-Host "\\" -ForegroundColor DarkGray
            Write-Host " | Status:       " -ForegroundColor DarkGray -NoNewline
            $ls = if ($LiveStatus.Length -gt $maxTextW) { $LiveStatus.Substring(0, $maxTextW - 3) + "..." } else { $LiveStatus }
            Write-Host $ls -ForegroundColor Yellow

            $netStat = if (Get-Command Get-NetworkDetailStatus -ErrorAction SilentlyContinue) {
                Get-NetworkDetailStatus
            } else { $null }

            # 1. Status zicnog kabla (LAN / Ethernet)
            Write-Host " | Mrezni kabl:  " -ForegroundColor DarkGray -NoNewline
            if ($netStat -and $netStat.LanConnected) {
                $spdTxt = if ($netStat.LanSpeed) { " ($($netStat.LanSpeed))" } else { "" }
                Write-Host "[OK] POVEZAN$spdTxt" -ForegroundColor Green -NoNewline
                Write-Host " - $($netStat.LanName)" -ForegroundColor White
            } elseif ($netStat -and $netStat.LanPresent) {
                Write-Host "[o] NIJE PRIKLJUCEN" -ForegroundColor Yellow -NoNewline
                Write-Host " - Port je slobodan ($($netStat.LanName))" -ForegroundColor DarkGray
            } else {
                Write-Host "[-] NEMA ETHERNET ADAPTERA" -ForegroundColor DarkGray
            }

            # 2. Status bezicne mreze (Wi-Fi)
            Write-Host " | Wi-Fi mreza:  " -ForegroundColor DarkGray -NoNewline
            if ($netStat -and $netStat.WifiConnected) {
                $sigTxt = if ($netStat.WifiSignal) { " (Signal: $($netStat.WifiSignal))" } else { "" }
                $ssidTxt = if ($netStat.WifiSSID) { "'$($netStat.WifiSSID)'" } else { "Povezano" }
                Write-Host "[OK] POVEZAN NA: $ssidTxt$sigTxt" -ForegroundColor Green -NoNewline
                Write-Host " - $($netStat.WifiAdapterName)" -ForegroundColor DarkGray
            } elseif ($netStat -and $netStat.WifiPresent) {
                Write-Host "[o] NIJE POVEZAN" -ForegroundColor Yellow -NoNewline
                Write-Host " - Adapter aktivan ($($netStat.WifiAdapterName)), skeniram predefinisane mreze..." -ForegroundColor DarkGray
            } elseif ($hw.WifiOk -and $hw.WifiName -and $hw.WifiName -ne "Nije detektovan") {
                Write-Host "[o] NIJE POVEZAN" -ForegroundColor Yellow -NoNewline
                Write-Host " - Adapter $($hw.WifiName) spreman" -ForegroundColor DarkGray
            } else {
                Write-Host "[!] NEMA WI-FI DRAJVERA" -ForegroundColor Red -NoNewline
                Write-Host " - Drajver ce biti instaliran tokom Windows Update faze" -ForegroundColor Yellow
            }

            # 3. Zbirni status interneta sa detekcijom Dual-Homing-a
            Write-Host " | Internet:     " -ForegroundColor DarkGray -NoNewline
            $netActive = if ($netStat) { $netStat.InternetActive } else {
                if (Get-Command Test-Internet -ErrorAction SilentlyContinue) { Test-Internet } else { $false }
            }

            if ($netActive) {
                if ($netStat -and $netStat.BothConnected) {
                    Write-Host "[OK] DUAL-HOMING AKTIVAN! (LAN Kabl + Wi-Fi rade istovremeno)" -ForegroundColor Green
                } elseif ($netStat -and $netStat.LanConnected) {
                    Write-Host "[OK] Konekcija je aktivna preko LAN mreznog kabla" -ForegroundColor Green
                } elseif ($netStat -and $netStat.WifiConnected) {
                    Write-Host "[OK] Konekcija je aktivna preko Wi-Fi bezicne mreze" -ForegroundColor Green
                } else {
                    Write-Host "[OK] Konekcija ka Microsoft serverima je aktivna!" -ForegroundColor Green
                }
            } else {
                Write-Host "[!] CEKAM INTERNET KONEKCIJU (Molimo ubacite LAN kabl u racunar)..." -ForegroundColor Yellow
            }

            # Centriran okvir za Wi-Fi upozorenje punom sirinom ako nema neta i nema wifi adaptera
            $noWifi = if ($netStat) { -not $netStat.WifiPresent } else { ($hw -and (-not $hw.WifiOk -or $hw.WifiName -eq "Nije detektovan")) }
            if (-not $netActive -and $noWifi) {
                if (-not $Global:WifiAlertBeepPlayed) {
                    Play-ServisAudioNotification -Type "Alert"
                    $Global:WifiAlertBeepPlayed = $true
                }
                $warnW = [math]::Max(40, $W - 4)
                $warnText = " [UPOZORENJE] NEMA WI-FI DRAJVERA - PRIKLJUCITE LAN KABL U RACUNAR! "
                $padWarn = [math]::Max(0, $warnW - $warnText.Length)
                $padWL = [math]::Floor($padWarn / 2)
                $padWR = $padWarn - $padWL
                Write-Host (" |" + ("=" * $warnW) + "|") -ForegroundColor Yellow
                Write-Host (" |" + (" " * $padWL) + $warnText + (" " * $padWR) + "|") -ForegroundColor Yellow
                Write-Host (" |" + ("=" * $warnW) + "|") -ForegroundColor Yellow
            }
            Write-Host (" \" + ("-" * ($W - 3)) + "/") -ForegroundColor DarkGray

        } elseif ($curPhase -eq 3) {
            # --- FAZA 3: SISTEMSKA OPTIMIZACIJA I BRENDIRANJE ---
            $brandName = if ($Global:Brand) { $Global:Brand } elseif ($Global:State -and $Global:State.Brand) { $Global:State.Brand } else { "OEM" }
            $titleP3 = " /--- SISTEMSKA OPTIMIZACIJA I BRENDIRANJE (Faza 3) "
            $dashP3 = [math]::Max(2, $W - $titleP3.Length - 1)
            Write-Host $titleP3 -ForegroundColor Cyan -NoNewline
            Write-Host ("-" * $dashP3) -ForegroundColor DarkGray -NoNewline
            Write-Host "\\" -ForegroundColor DarkGray
            Write-Host " | Brend:    " -ForegroundColor DarkGray -NoNewline
            Write-Host "$brandName (OEM konfiguracija, logo i podrska)" -ForegroundColor White
            Write-Host " | Aktivnost:" -ForegroundColor DarkGray -NoNewline
            $ls = if ($LiveStatus.Length -gt $maxTextW) { $LiveStatus.Substring(0, $maxTextW - 3) + "..." } else { $LiveStatus }
            Write-Host " $ls" -ForegroundColor Yellow

            # Dinamicka provera stvarnih optimizacija
            $isTelemOff = $false
            try {
                $telemVal = (Get-ItemProperty "HKLM:\SOFTWARE\Policies\Microsoft\Windows\DataCollection" -Name "AllowTelemetry" -ErrorAction SilentlyContinue).AllowTelemetry
                if ($telemVal -eq 0) { $isTelemOff = $true }
            } catch {}

            $is24h = $false
            try {
                $timeFmt = (Get-ItemProperty "HKCU:\Control Panel\International" -Name "sTimeFormat" -ErrorAction SilentlyContinue).sTimeFormat
                if ($timeFmt -eq "HH:mm") { $is24h = $true }
            } catch {}

            Write-Host " | Stavke:   " -ForegroundColor DarkGray -NoNewline
            Write-Host $(if ($isTelemOff) { "[OK] Telemetrija " } else { "[>>] Telemetrija " }) -ForegroundColor $(if ($isTelemOff) { "Green" } else { "Yellow" }) -NoNewline
            Write-Host "[OK] Brend ($brandName) " -ForegroundColor Green -NoNewline
            Write-Host $(if ($is24h) { "[OK] 24h Sat " } else { "[>>] 24h Sat " }) -ForegroundColor $(if ($is24h) { "Green" } else { "Yellow" }) -NoNewline
            Write-Host "[OK] Sleep Off" -ForegroundColor Green
            Write-Host (" \" + ("-" * ($W - 3)) + "/") -ForegroundColor DarkGray

        } elseif ($curPhase -eq 4) {
            # --- FAZA 4: PROGRAMI, PRECICE I AKTIVACIJA (100% REALNO) ---
            $titleP4 = " /--- PROGRAMI, PRECICE I AKTIVACIJA (Faza 4) "
            $dashP4 = [math]::Max(2, $W - $titleP4.Length - 1)
            Write-Host $titleP4 -ForegroundColor Cyan -NoNewline
            Write-Host ("-" * $dashP4) -ForegroundColor DarkGray -NoNewline
            Write-Host "\\" -ForegroundColor DarkGray
            Write-Host " | Aktivnost:" -ForegroundColor DarkGray -NoNewline
            $ls = if ($LiveStatus.Length -gt $maxTextW) { $LiveStatus.Substring(0, $maxTextW - 3) + "..." } else { $LiveStatus }
            Write-Host " $ls" -ForegroundColor Yellow
            if ($InstallProgress) {
                $ip = if ($InstallProgress.Length -gt $maxTextW) { $InstallProgress.Substring(0, $maxTextW - 3) + "..." } else { $InstallProgress }
                Write-Host " | Progres:  " -ForegroundColor DarkGray -NoNewline
                Write-Host $ip -ForegroundColor Green
            }

            # Prikaz trenutnog softvera koji se obradjuje
            $curSoft = if ($appData.RunningItem) {
                "Instaliram $($appData.RunningItem.Name)..."
            } elseif ($appData.WorkerCurrent) {
                $appData.WorkerCurrent
            } elseif ($appData.AllInstalled) {
                "Svi programi su verifikovani na SSD-u."
            } else {
                "Provera instalacije programa..."
            }
            if ($curSoft.Length -gt $maxTextW) { $curSoft = $curSoft.Substring(0, $maxTextW - 3) + "..." }
            Write-Host " | Softver:  " -ForegroundColor DarkGray -NoNewline
            Write-Host $curSoft -ForegroundColor White

            # Ako je instalacija u toku, prikazi pulsirajuci elastican progress bar
            if ($appData.RunningItem) {
                $bSlots = [math]::Max(14, [math]::Min(45, [math]::Round(($W - 45) * 0.3)))
                $bFilled = [math]::Min($bSlots, [math]::Max(0, [math]::Round(($appData.WorkerProgress / 100) * $bSlots)))
                $bEmpty = $bSlots - $bFilled
                $miniBar = ("=" * $bFilled) + ("-" * $bEmpty)
                Write-Host " | U toku:   " -ForegroundColor DarkGray -NoNewline
                Write-Host "[>> $pulseChar] " -ForegroundColor Cyan -NoNewline
                Write-Host ("{0,-18}" -f $appData.RunningItem.Name) -ForegroundColor Yellow -NoNewline
                Write-Host " [$miniBar] $($appData.WorkerProgress)%" -ForegroundColor Cyan
            }

            # Tabela stavki sa realnim stanjima (Responsivna mreza punom sirinom)
            Write-ServisItemsGrid -Items $appData.Items -Width $W -Prefix " | Programi: " -ContinuationPrefix " |           "

            # Realan status programa u Fazi 4
            Write-Host " | Rezultat: " -ForegroundColor DarkGray -NoNewline
            if ($appData.AllInstalled) {
                Write-Host "[OK] Svi potrebni programi su verifikovani na sistemu!" -ForegroundColor Green
            } elseif ($appData.FailedCount -gt 0) {
                Write-Host "[!] NEDOSTAJE $($appData.FailedCount) PROGRAMA - Potrebna ponovna instalacija!" -ForegroundColor Red
            } else {
                Write-Host "[>> $pulseChar] Instalacija i konfiguracija aplikacija u toku..." -ForegroundColor Yellow
            }
            Write-Host (" \" + ("-" * ($W - 3)) + "/") -ForegroundColor DarkGray

        } elseif ($curPhase -eq 5) {
            # --- FAZA 5: TERMALNI STRES TEST I IZVESTAJ (30 SEKUNDI) ---
            $titleP5 = " /--- TERMALNI STRES TEST I IZVESTAJ (Faza 5) "
            $dashP5 = [math]::Max(2, $W - $titleP5.Length - 1)
            Write-Host $titleP5 -ForegroundColor Cyan -NoNewline
            Write-Host ("-" * $dashP5) -ForegroundColor DarkGray -NoNewline
            Write-Host "\\" -ForegroundColor DarkGray
            Write-Host " | Aktivnost:" -ForegroundColor DarkGray -NoNewline
            $ls = if ($LiveStatus.Length -gt $maxTextW) { $LiveStatus.Substring(0, $maxTextW - 3) + "..." } else { $LiveStatus }
            Write-Host " $ls" -ForegroundColor Yellow
            Write-Host (" \" + ("-" * ($W - 3)) + "/") -ForegroundColor DarkGray

        } elseif ($curPhase -ge 6) {
            # --- FAZA 6: DINAMICKI REZIME ZAVRSETKA (100% REALAN STATUS) ---
            $missingHW = if ($hw -and ($null -ne $hw.MissingDriversCount)) { [int]$hw.MissingDriversCount } else { 0 }

            # Provera licenci (aktivacija)
            $winAct = $false
            $offAct = $false
            if (Get-Command Test-WindowsActivated -ErrorAction SilentlyContinue) {
                $winAct = Test-WindowsActivated
            }
            if (Get-Command Test-OfficeActivated -ErrorAction SilentlyContinue) {
                $offAct = Test-OfficeActivated
            }

            $isFullSuccess = $appData.AllInstalled -and ($missingHW -eq 0) -and $winAct -and $offAct

            if ($isFullSuccess) {
                $titleP6 = " /--- [100% USPESNO] SERVIS ZAVRSEN - RACUNAR SPREMAN ZA KLIJENTA! (Faza 6) "
                $dashP6 = [math]::Max(2, $W - $titleP6.Length - 1)
                Write-Host $titleP6 -ForegroundColor Green -NoNewline
                Write-Host ("-" * $dashP6) -ForegroundColor DarkGray -NoNewline
                Write-Host "\\" -ForegroundColor DarkGray

                Write-Host " | [OK] Drajveri i azuriranja: Svi hardverski uredjaji prepoznati" -ForegroundColor Green
                Write-Host " | [OK] Programi (.NET, Chrome, WinRAR, Acrobat, Office): Svi instalirani" -ForegroundColor Green
                Write-Host " | [OK] Precice na Desktopu: Word, Excel i Acrobat na radnoj povrsini" -ForegroundColor Green
                Write-Host " | [OK] Trajna aktivacija: Windows i Office licence uspesno potvrdjene" -ForegroundColor Green
                Write-Host (" \" + ("-" * ($W - 3)) + "/") -ForegroundColor DarkGray

                # Pobednicka melodija
                if (-not $Global:SuccessBeepPlayed) {
                    Play-ServisAudioNotification -Type "Success"
                    $Global:SuccessBeepPlayed = $true
                }
            } else {
                $titleP6 = " /--- [UPOZORENJE] SERVIS ZAVRSEN SA NEDOSTACIMA! (Faza 6) "
                $dashP6 = [math]::Max(2, $W - $titleP6.Length - 1)
                Write-Host $titleP6 -ForegroundColor Red -NoNewline
                Write-Host ("-" * $dashP6) -ForegroundColor DarkGray -NoNewline
                Write-Host "\\" -ForegroundColor DarkGray

                # 1. Drajveri
                if ($missingHW -eq 0) {
                    Write-Host " | [OK] Drajveri: Svi hardverski drajveri su uredno instalirani" -ForegroundColor Green
                } else {
                    Write-Host " | [!] Drajveri: $missingHW uredjaja bez drajvera u Device Manageru!" -ForegroundColor Red
                }

                # 2. Programi
                if ($appData.AllInstalled) {
                    Write-Host " | [OK] Programi: Svi potrebni programi su uspesno verifikovani" -ForegroundColor Green
                } else {
                    $missingNames = ($appData.Items | Where-Object { $_.Required -and $_.Status -ne "Completed" -and $_.Status -ne "Skipped" } | ForEach-Object { $_.Name }) -join ", "
                    if (-not $missingNames) { $missingNames = "Nepotpun softver" }
                    if ($missingNames.Length -gt $maxTextW) { $missingNames = $missingNames.Substring(0, $maxTextW - 3) + "..." }
                    Write-Host " | [!] Programi: NEDOSTAJU: $missingNames" -ForegroundColor Red
                }

                # 3. Precice
                $scItem = $appData.Items | Where-Object { $_.Id -eq "Shortcuts" } | Select-Object -First 1
                if ($scItem -and $scItem.Installed) {
                    Write-Host " | [OK] Precice na Desktopu: Word, Excel i Acrobat na radnoj povrsini" -ForegroundColor Green
                } else {
                    Write-Host " | [!] Precice na Desktopu: Nedostaju standardne radne precice" -ForegroundColor Yellow
                }

                # 4. Aktivacija
                if ($winAct -and $offAct) {
                    Write-Host " | [OK] Trajna aktivacija: Windows i Office licence su potvrdjene" -ForegroundColor Green
                } else {
                    $wStr = if ($winAct) { "Win:OK" } else { "Win:NEUSPELO" }
                    $oStr = if ($offAct) { "Off:OK" } else { "Off:NEUSPELO" }
                    Write-Host " | [!] Trajna aktivacija: $wStr | $oStr (Potrebna provera licence!)" -ForegroundColor Red
                }
                Write-Host (" \" + ("-" * ($W - 3)) + "/") -ForegroundColor DarkGray

                # Audio alert za nedostatke
                if (-not $Global:AlertBeepPlayed) {
                    Play-ServisAudioNotification -Type "Alert"
                    $Global:AlertBeepPlayed = $true
                }
            }
        }

        # Prikaz SubLogs (do 10 linija prilagodjeno sirini ekrana)
        if ($SubLogs -and $SubLogs.Count -gt 0) {
            $showMaxLogs = [math]::Min(10, $SubLogs.Count)
            for ($li = 0; $li -lt $showMaxLogs; $li++) {
                $lTxt = $SubLogs[$li]
                if ($lTxt.Length -gt ($W - 6)) { $lTxt = $lTxt.Substring(0, $W - 9) + "..." }
                Write-Host "   $lTxt" -ForegroundColor DarkGray
            }
        }

        Write-Host ("=" * $W) -ForegroundColor Cyan
        Write-Host ""

        # Tail clearing: Ako je prethodni frejm imao vise linija od trenutnog, prebrisi visak
        try {
            $curY = [Console]::CursorTop
            if ($Global:LastDashboardMaxY -and $Global:LastDashboardMaxY -gt $curY) {
                $diff = $Global:LastDashboardMaxY - $curY
                $emptyLine = " " * $W
                for ($ci = 0; $ci -lt $diff; $ci++) {
                    Write-Host $emptyLine
                }
            }
            $Global:LastDashboardMaxY = [math]::Max($curY, 25)
        } catch {}

    } catch {
        $dashErr = $_
        try {
            # OTPORNI SAFE-MODE RENDER: Interfejs NIKADA ne nestaje u slucaju greske
            $W = Get-ServisConsoleWidth
            Write-Host ("+" + ("=" * ($W - 2)) + "+") -ForegroundColor Red
            Write-Host "| [!] WIS SERVICE TOOLKIT - BEZBEDNI DIJAGNOSTICKI PRIKAZ" -ForegroundColor Red
            Write-Host ("+" + ("=" * ($W - 2)) + "+") -ForegroundColor Red
            Write-Host " [*] AKTIVNI STATUS: $Global:LastLiveStatus" -ForegroundColor Yellow
            Write-Host " [!] GRESKA U PRIKAZU: $($dashErr.Exception.Message)" -ForegroundColor Red
            if ($dashErr.InvocationInfo) {
                Write-Host "     Linija: $($dashErr.InvocationInfo.ScriptLineNumber) | Poziv: $($dashErr.InvocationInfo.Line.Trim())" -ForegroundColor DarkGray
            }
            if ($SubLogs -and $SubLogs.Count -gt 0) {
                Write-Host " [*] POSLEDNJI LOGOVI:" -ForegroundColor Cyan
                foreach ($sl in ($SubLogs | Select-Object -Last 5)) {
                    Write-Host "     $sl" -ForegroundColor White
                }
            }
            Write-Host ("=" * $W) -ForegroundColor Red
        } catch {
            Write-Host " [STATUS] $LiveStatus | GRESKA: $($dashErr.Exception.Message)" -ForegroundColor Yellow
        }
        Write-ServisLog "Upozorenje pri iscrtavanju dashboarda: $($dashErr.Exception.Message)" -Level "WARN" -Component "Dashboard"
    }
}

function Show-ServisFatalErrorScreen {
    <#
    .SYNOPSIS
        Zaustavni sigurnosni ekran koji sprecava gasenje konzole i nestanak interfejsa pri kriticnoj gresci u fazama.
    #>
    param(
        [int]$Phase = 1,
        [Parameter(Mandatory=$true)]$ErrorRecord,
        [string]$ContextMessage = "Doslo je do kriticnog prekida u radu faze."
    )
    $W = Get-ServisConsoleWidth
    try { [Console]::CursorVisible = $true } catch {}

    if (Get-Command Play-ServisAudioNotification -ErrorAction SilentlyContinue) {
        Play-ServisAudioNotification -Type "Alert"
    }

    Write-Host ""
    Write-Host ("+" + ("=" * ($W - 2)) + "+") -ForegroundColor Red
    Write-Host "| [KRITICNA GRESKA] SISTEM JE ZAUSTAVIO RAD RADI ZASTITE INTERFEJSA" -ForegroundColor Red
    Write-Host ("+" + ("=" * ($W - 2)) + "+") -ForegroundColor Red
    Write-Host "  Faza rada:        Faza $Phase" -ForegroundColor Yellow
    Write-Host "  Opis greske:      $($ErrorRecord.Exception.Message)" -ForegroundColor White
    if ($ErrorRecord.InvocationInfo) {
        Write-Host "  Skripta i linija: $($ErrorRecord.InvocationInfo.ScriptName):$($ErrorRecord.InvocationInfo.ScriptLineNumber)" -ForegroundColor DarkGray
        Write-Host "  Naredba:          $($ErrorRecord.InvocationInfo.Line.Trim())" -ForegroundColor DarkGray
    }
    Write-Host "  Poslednji status: $Global:LastLiveStatus" -ForegroundColor Cyan
    Write-Host ("-" * $W) -ForegroundColor DarkGray
    Write-Host "  INTERFEJS NIJE UGASEN. Odaberite akciju za nastavak rada:" -ForegroundColor Yellow
    Write-Host "    [R] Ponovi tekucu fazu (Retry Phase $Phase)" -ForegroundColor Green
    Write-Host "    [S] Preskoci ovu fazu i nastavi dalje (Skip to next phase)" -ForegroundColor Yellow
    Write-Host "    [C] Otvori interaktivnu PowerShell konzolu (CMD prompt)" -ForegroundColor Cyan
    Write-Host ("=" * $W) -ForegroundColor Red
    Write-Host ""

    if (Get-Command Send-ServisHeartbeat -ErrorAction SilentlyContinue) {
        $null = Send-ServisHeartbeat -LiveStatus "KRITICNA GRESKA: $($ErrorRecord.Exception.Message)"
    }

    if ($Global:Worker -or $Global:WIS_UI_Backend -eq "Pipeline") {
        Write-Host "[WORKER] Fatalna greska zabelezena. Radnik vrsi automatski prelazak..."
        return "Skip"
    }

    while ($true) {
        Write-Host "Izbor [R/S/C]: " -ForegroundColor Yellow -NoNewline
        try {
            $key = [Console]::ReadKey($true)
            $char = $key.KeyChar.ToString().ToUpper()
            Write-Host $char -ForegroundColor White
        } catch {
            Write-Host "[WORKER/PREUSMERENO] STDIN preusmeren. Automatski preskacem fazu..."
            return "Skip"
        }

        if ($char -eq "R") {
            Write-Host "Ponovo pokrecem Fazu $Phase..." -ForegroundColor Green
            Start-Sleep -Seconds 2
            return "Retry"
        } elseif ($char -eq "S") {
            Write-Host "Preskacem Fazu $Phase i nastavljam..." -ForegroundColor Yellow
            Start-Sleep -Seconds 2
            return "Skip"
        } elseif ($char -eq "C") {
            Write-Host "Otvaram interaktivni PowerShell mod za dijagnostiku. Unesite 'exit' kada zavrsite." -ForegroundColor Cyan
            $host.EnterNestedPrompt()
            Write-Host "Izlaz iz interaktivnog moda. Ponovo birajte opciju." -ForegroundColor DarkGray
        }
    }
}

Export-ModuleMember -Function `
    Format-ExitCodeDescription, `
    Write-ServisLog, `
    Write-DiagnosticEvent, `
    Register-UsbLog, `
    Invoke-LoggedProcess, `
    Repair-ProgramiNesting, `
    Find-SourceDrive, `
    Get-ProgramiDir, `
    Get-ServisNasSettings, `
    Get-ServisLicensingSettings, `
    Test-NasHostOnline, `
    Sync-ProgramiFromNas, `
    Sync-ServisLicensingFromNas, `
    Get-WisImageProfile, `
    Test-ProgramiDirComplete, `
    Ensure-ProgramiAvailable, `
    Grant-SetupAclPermissions, `
    Test-IsSingleInstance, `
    Initialize-ServisEnvironment, `
    Register-WisSecurityRecoverySentinel, `
    Save-State, `
    Update-AppWorkerStatus, `
    Get-AppWorkerStatus, `
    Get-AppItemState, `
    Set-ConsoleMaximized, `
    Get-ServisConsoleWidth, `
    Write-ServisItemsGrid, `
    Play-ServisAudioNotification, `
    Get-RealAppStatuses, `
    Show-ServisDashboard, `
    Show-ServisFatalErrorScreen
