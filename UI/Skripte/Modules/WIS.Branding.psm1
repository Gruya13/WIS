<#
.MODULE
    WIS.Branding.psm1
.DESCRIPTION
    Sistemske optimizacije, OEM brendiranje korisnickog naloga, regionalna podesavanja i gasenje telemetrije za WIS (Windows Installation Assistant).
.COMPATIBILITY
    Windows PowerShell 5.1+
#>

function Set-OptimalDisplayScaling {
    <#
    .SYNOPSIS
        Automatsko podesavanje DPI skaliranja ekrana na osnovu rezolucije (WIS-TASK-042).
    .DESCRIPTION
        Detektuje primarnu rezoluciju ekrana i primenjuje optimalno skaliranje:
        - FHD i FHD+ (1920px - 2559px) -> 125% scale (LogPixels = 120)
        - 2K / QHD (2560px - 3839px)   -> 150% scale (LogPixels = 144)
        - 4K / UHD (>= 3840px)          -> 200% scale (LogPixels = 192)
        - Sub-FHD (< 1920px)           -> 100% scale (LogPixels = 96, default)
        - Fallback (< 1024px)          -> Preskace se (nevazeca/privremena rezolucija)
        Kombinuje P/Invoke Win32 API (user32.dll) za instant primenu i Registry upis (HKCU).
    #>
    param()

    if (Get-Command Show-ServisDashboard -ErrorAction SilentlyContinue) {
        Show-ServisDashboard -LiveStatus "Proveravam rezoluciju ekrana za optimalno DPI skaliranje..."
    }
    if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
        Write-ServisLog "Zapocinjem proveru rezolucije ekrana za automatsko DPI skaliranje..." -Level "STEP" -Component "Branding"
    }

    $screenWidth = 0
    $screenHeight = 0

    # 1. Detekcija preko Win32_VideoController (CIM / WMI)
    try {
        $video = Get-CimInstance Win32_VideoController -ErrorAction SilentlyContinue |
            Where-Object { $_.CurrentHorizontalResolution -gt 0 } |
            Sort-Object CurrentHorizontalResolution -Descending |
            Select-Object -First 1
        if (-not $video) {
            $video = Get-WmiObject Win32_VideoController -ErrorAction SilentlyContinue |
                Where-Object { $_.CurrentHorizontalResolution -gt 0 } |
                Sort-Object CurrentHorizontalResolution -Descending |
                Select-Object -First 1
        }
        if ($video -and $video.CurrentHorizontalResolution -gt 0) {
            $screenWidth = [int]$video.CurrentHorizontalResolution
            $screenHeight = [int]$video.CurrentVerticalResolution
        }
    } catch {}

    # 2. Fallback na System.Windows.Forms.Screen
    if ($screenWidth -le 0) {
        try {
            Add-Type -AssemblyName System.Windows.Forms -ErrorAction SilentlyContinue
            $primaryScreen = [System.Windows.Forms.Screen]::PrimaryScreen
            if ($primaryScreen -and $primaryScreen.Bounds.Width -gt 0) {
                $screenWidth = [int]$primaryScreen.Bounds.Width
                $screenHeight = [int]$primaryScreen.Bounds.Height
            }
        } catch {}
    }

    # 3. Sigurnosni osigurac (Fallback): nevazeca ili privremena rezolucija (< 1024px)
    if ($screenWidth -lt 1024) {
        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
            Write-ServisLog "Detektovana nevazeca ili osnovna privremena rezolucija (${screenWidth}x${screenHeight} < 1024px). Preskacem automatsko DPI skaliranje." -Level "WARN" -Component "Branding"
        }
        return
    }

    # 4. Odredjivanje ciljanog DPI faktora skaliranja
    $targetScale = 100
    $logPixels = 96
    $scaleCategory = ""

    if ($screenWidth -ge 3840) {
        $targetScale = 200
        $logPixels = 192
        $scaleCategory = "4K / UHD"
    } elseif ($screenWidth -ge 2560) {
        $targetScale = 150
        $logPixels = 144
        $scaleCategory = "2K / QHD"
    } elseif ($screenWidth -ge 1920) {
        $targetScale = 125
        $logPixels = 120
        $scaleCategory = "FHD / FHD+"
    } else {
        $targetScale = 100
        $logPixels = 96
        $scaleCategory = "Sub-FHD"
    }

    if ($targetScale -eq 100) {
        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
            Write-ServisLog "Rezolucija ekrana je ${screenWidth}x${screenHeight} ($scaleCategory). Zadrzava se fabricki default 100% scale (LogPixels=96)." -Level "INFO" -Component "Branding"
        }
        return
    }

    if (Get-Command Show-ServisDashboard -ErrorAction SilentlyContinue) {
        Show-ServisDashboard -LiveStatus "Primenjujem DPI skaliranje na $targetScale% ($scaleCategory - ${screenWidth}x${screenHeight})..."
    }
    if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
        Write-ServisLog "Detektovana rezolucija: ${screenWidth}x${screenHeight} ($scaleCategory). Postavljam DPI scale na $targetScale% (LogPixels = $logPixels)." -Level "STEP" -Component "Branding"
    }

    # 5. Registry upis za trajnu perzistenciju (HKCU:\Control Panel\Desktop)
    try {
        $regDesktop = "HKCU:\Control Panel\Desktop"
        if (-not (Test-Path $regDesktop)) {
            New-Item -Path $regDesktop -Force -ErrorAction SilentlyContinue | Out-Null
        }
        Set-ItemProperty -Path $regDesktop -Name "LogPixels" -Value $logPixels -Type DWord -Force -ErrorAction SilentlyContinue
        Set-ItemProperty -Path $regDesktop -Name "Win8DpiScaling" -Value 1 -Type DWord -Force -ErrorAction SilentlyContinue
    } catch {
        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
            Write-ServisLog "Greska pri upisu DPI vrednosti u registar: $($_.Exception.Message)" -Level "WARN" -Component "Branding"
        }
    }

    # 6. P/Invoke Win32 API poziv (user32.dll) za instant primenu bez odjavljivanja gde je moguce
    try {
        if (-not ([System.Management.Automation.PSTypeName]'WIS.DpiHelper').Type) {
            Add-Type -MemberDefinition @'
            [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
            public static extern IntPtr SendMessageTimeout(
                IntPtr hWnd,
                uint Msg,
                UIntPtr wParam,
                string lParam,
                uint fuFlags,
                uint uTimeout,
                out UIntPtr lpdwResult);

            [DllImport("user32.dll", EntryPoint = "UpdatePerUserSystemParameters")]
            public static extern int UpdatePerUserSystemParameters(int nReserved, int bForceUpdate);
'@ -Name 'DpiHelper' -Namespace 'WIS' -ErrorAction SilentlyContinue
        }

        if ([System.Management.Automation.PSTypeName]'WIS.DpiHelper'.Type) {
            [WIS.DpiHelper]::UpdatePerUserSystemParameters(1, 1) | Out-Null
            $res = [UIntPtr]::Zero
            [WIS.DpiHelper]::SendMessageTimeout([IntPtr]0xffff, 0x001A, [UIntPtr]::Zero, "Control Panel\Desktop", 2, 2000, [ref]$res) | Out-Null
        }
    } catch {
        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
            Write-ServisLog "Upozorenje pri Win32 API osvezavanju DPI postavki: $($_.Exception.Message)" -Level "DEBUG" -Component "Branding"
        }
    }

    if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
        Write-ServisLog "Uspesno primenjeno optimalno DPI skaliranje ($targetScale% - LogPixels=$logPixels) za ${screenWidth}x${screenHeight}." -Level "SUCCESS" -Component "Branding"
    }
}

function Invoke-SystemOptimizationAndBranding {
    param(
        [string]$Brand = "",
        [string]$LocalSetupDir = "C:\setup",
        [string]$ProgramiDir = "C:\setup\Programi"
    )

    if (-not $Brand) {
        $Brand = if ($Global:Brand) { $Global:Brand } elseif ($Global:State -and $Global:State.Brand) { $Global:State.Brand } else { "OEM" }
    }

    if (Get-Command Show-ServisDashboard -ErrorAction SilentlyContinue) {
        Show-ServisDashboard -LiveStatus "Primenjujem sistemske optimizacije i podesavam brend $Brand..."
    }
    if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
        Write-ServisLog "Zapocinjem sistemsku optimizaciju i brendiranje ($Brand)..." -Level "STEP" -Component "Branding"
    }

    # A. Defender izuzeci za setup i programe
    $exclusionPaths = @(
        "D:\",
        $LocalSetupDir,
        $ProgramiDir,
        "C:\Program Files\Adobe",
        "C:\Program Files (x86)\Adobe",
        "C:\Program Files\Common Files\Adobe",
        "C:\Program Files\Microsoft Office",
        "C:\Program Files (x86)\Microsoft Office"
    )
    foreach ($path in $exclusionPaths) {
        if ($path) {
            Add-MpPreference -ExclusionPath $path -ErrorAction SilentlyContinue
        }
    }

    # B. Gasenje telemetrije i lokacije
    try {
        New-Item -Path "HKLM:\SOFTWARE\Policies\Microsoft\Windows\DataCollection" -Force -ErrorAction SilentlyContinue | Out-Null
        Set-ItemProperty -Path "HKLM:\SOFTWARE\Policies\Microsoft\Windows\DataCollection" -Name "AllowTelemetry" -Type DWord -Value 0 -Force -ErrorAction SilentlyContinue
        Stop-Service -Name "DiagTrack" -WarningAction SilentlyContinue -ErrorAction SilentlyContinue
        Set-Service -Name "DiagTrack" -StartupType Disabled -ErrorAction SilentlyContinue

        Stop-Service -Name "dmwappushservice" -WarningAction SilentlyContinue -ErrorAction SilentlyContinue
        Set-Service -Name "dmwappushservice" -StartupType Disabled -ErrorAction SilentlyContinue

        New-Item -Path "HKLM:\SOFTWARE\Policies\Microsoft\Windows\LocationAndSensors" -Force -ErrorAction SilentlyContinue | Out-Null
        Set-ItemProperty -Path "HKLM:\SOFTWARE\Policies\Microsoft\Windows\LocationAndSensors" -Name "DisableLocation" -Type DWord -Value 1 -Force -ErrorAction SilentlyContinue
        Stop-Service -Name "lfsvc" -WarningAction SilentlyContinue -ErrorAction SilentlyContinue
        Set-Service -Name "lfsvc" -StartupType Disabled -ErrorAction SilentlyContinue

        New-Item -Path "HKCU:\Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo" -Force -ErrorAction SilentlyContinue | Out-Null
        Set-ItemProperty -Path "HKCU:\Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo" -Name "Enabled" -Type DWord -Value 0 -Force -ErrorAction SilentlyContinue

        New-Item -Path "HKCU:\Software\Microsoft\Windows\CurrentVersion\Search" -Force -ErrorAction SilentlyContinue | Out-Null
        Set-ItemProperty -Path "HKCU:\Software\Microsoft\Windows\CurrentVersion\Search" -Name "BingSearchEnabled" -Type DWord -Value 0 -Force -ErrorAction SilentlyContinue
    } catch {}

    # C. Preimenovanje korisnika u marku (DELL, HP, LENOVO...)
    try {
        $target = "Korisnik"
        if (Get-LocalUser -Name $target -ErrorAction SilentlyContinue) {
            Rename-LocalUser -Name $target -NewName $Brand -ErrorAction SilentlyContinue
            Set-LocalUser -Name $Brand -FullName $Brand -ErrorAction SilentlyContinue
            Set-ItemProperty -Path "HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon" -Name "DefaultUserName" -Value $Brand -ErrorAction SilentlyContinue
            if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                Write-ServisLog "Lokalni nalog preimenovan u: $Brand" -Level "SUCCESS" -Component "Branding"
            }
        }
    } catch {}

    # D. Iskljucivanje sleep-a, gasenja monitora i hibernacije kada je racunar na punjacu (AC napajanje)
    try {
        powercfg -change -standby-timeout-ac 0
        powercfg -change -monitor-timeout-ac 0
        powercfg -change -hibernate-timeout-ac 0
        # Na bateriji (DC) postaviti razuman standby timeout
        powercfg -change -standby-timeout-dc 30
        powercfg -change -monitor-timeout-dc 15
        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
            Write-ServisLog "Optimizacija napajanja: Standby, Monitor i Hibernate na AC punjaču postavljeni na NEVER (0)." -Level "SUCCESS" -Component "Power"
        }
    } catch {}

    # E. Ikonice na Desktopu (This PC, Control Panel, User)
    try {
        $regPath = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\HideDesktopIcons\NewStartPanel"
        $icons = @{
            "{20D04FE0-3AEA-1069-A2D8-08002B30309D}" = 0
            "{5399E694-6CE5-4D6C-8FCE-1D8870FDCBA0}" = 0
            "{59031a47-3f72-44a7-89c5-5595fe6b30ee}" = 0
        }
        if (-not (Test-Path $regPath)) { New-Item -Path $regPath -Force | Out-Null }
        foreach ($k in $icons.Keys) {
            Set-ItemProperty -Path $regPath -Name $k -Value 0 -Type DWord -ErrorAction SilentlyContinue
        }
    } catch {}

    # F. Srpska latinica i cirilica
    try {
        $langList = Get-WinUserLanguageList
        $srLatn = "sr-Latn-RS"
        $srCyrl = "sr-Cyrl-RS"
        $tags = $langList.LanguageTag
        if ($tags -notmatch $srLatn) { $langList.Add($srLatn) }
        if ($tags -notmatch $srCyrl) { $langList.Add($srCyrl) }
        Set-WinUserLanguageList -LanguageList $langList -Force
    } catch {}

    # G. Vremenska zona Beograd i 24h sat
    try { Set-TimeZone -Id "Central Europe Standard Time" -ErrorAction SilentlyContinue } catch {}
    try {
        $regIntl = "HKCU:\Control Panel\International"
        Set-ItemProperty -Path $regIntl -Name "sTimeFormat" -Value "HH:mm" -Type String -ErrorAction SilentlyContinue
        Set-ItemProperty -Path $regIntl -Name "sShortTime" -Value "HH:mm" -Type String -ErrorAction SilentlyContinue
        Set-ItemProperty -Path $regIntl -Name "sLongTime"  -Value "HH:mm:ss" -Type String -ErrorAction SilentlyContinue
    } catch {}

    # H. Automatsko optimalno DPI skaliranje ekrana na osnovu rezolucije (WIS-TASK-042)
    try {
        Set-OptimalDisplayScaling
    } catch {
        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
            Write-ServisLog "Greska pri pozivu Set-OptimalDisplayScaling: $($_.Exception.Message)" -Level "WARN" -Component "Branding"
        }
    }

    if (Get-Command Show-ServisDashboard -ErrorAction SilentlyContinue) {
        Show-ServisDashboard -LiveStatus "Optimizacija sistema uspesna! Prelazim na programe..."
    }
    Start-Sleep -Seconds 2

    if ($Global:State) {
        $Global:State.Phase = 4
        if (Get-Command Save-State -ErrorAction SilentlyContinue) { Save-State }
    }
}

function Invoke-ServisInterimReboot {
    <#
    .SYNOPSIS
        Dvoetapni Deployment - Kontrolisani medjurestart izmedju Faze 3 i Faze 4 (WIS-TASK-057).
    .DESCRIPTION
        Sprovodi ciscenje CBS/MSI podsistema i osvezavanje drajvera kroz kontrolisani restart.
        Pre restarta osigurava AutoLogon i RunOnce registraciju za nesmetan nastavak u Fazi 4.
    #>
    param(
        [string]$Brand = "",
        [string]$LocalSetupDir = "C:\setup",
        [string]$LocalScriptPath = "C:\setup\Install-ServisApps.ps1"
    )
    $rebootMarker = Join-Path $LocalSetupDir ".stage_reboot_done"
    if (-not (Test-Path $rebootMarker)) {
        Write-ServisLog "=== PRE-REBOOT GATE: Priprema sistema za medjurestart radi primene drajvera i ciscenja CBS/MSI podsistema ===" -Level "STEP" -Component "Lifecycle"

        if (Get-Command Show-ServisDashboard -ErrorAction SilentlyContinue) {
            Show-ServisDashboard -LiveStatus "Drajveri i optimizacije primenjeni. Sistem se restartuje za 5 sekundi radi stabilizacije..."
        }

        # 1. Azuriraj stanje u State objektu na Fazu 4
        if ($Global:State) {
            $Global:State.Phase = 4
            if (Get-Command Save-State -ErrorAction SilentlyContinue) { Save-State }
        }

        # 2. Kreiraj marker fajl na disku
        try {
            Set-Content -Path $rebootMarker -Value (Get-Date).ToString("yyyy-MM-dd HH:mm:ss") -Force -ErrorAction SilentlyContinue
        } catch {}

        # 3. Osiguraj AutoLogon za sledece podizanje sistema
        try {
            $existingUser = Get-LocalUser -ErrorAction SilentlyContinue | Where-Object { $_.Enabled -and $_.Name -ne 'Administrator' -and $_.Name -ne 'Guest' -and $_.Name -ne 'DefaultAccount' } | Select-Object -ExpandProperty Name -First 1
            if (-not $existingUser) { $existingUser = if ($Brand) { $Brand } elseif ($Global:Brand) { $Global:Brand } else { "Korisnik" } }
            & reg.exe add "HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon" /v "AutoAdminLogon" /t REG_SZ /d "1" /f >$null 2>&1
            & reg.exe add "HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon" /v "AutoLogonCount" /t REG_DWORD /d 1 /f >$null 2>&1
            & reg.exe add "HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon" /v "DefaultUserName" /t REG_SZ /d "$existingUser" /f >$null 2>&1
            & reg.exe add "HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon" /v "DefaultPassword" /t REG_SZ /d "" /f >$null 2>&1
        } catch {}

        # 4. Registruj RunOnce komandu za automatski nastavak skripte
        try {
            $runOnceCmd = "powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$LocalScriptPath`""
            & reg.exe add "HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce" /v "WIS_Resume" /t REG_SZ /d "$runOnceCmd" /f >$null 2>&1
        } catch {}

        # 4b. Dvostruki fail-safe mehanizam: kreiranje batch fajla u All Users Startup folderu
        try {
            $startupDir = [Environment]::GetFolderPath('CommonStartup')
            if (-not $startupDir) { $startupDir = 'C:\ProgramData\Microsoft\Windows\Start Menu\Programs\Startup' }
            if (Test-Path $startupDir) {
                $resumeBat = Join-Path $startupDir 'WIS-Resume.cmd'
                $resumeContent = @'
@echo off
if exist "C:\setup\Install-ServisApps.ps1" (
    if not exist "C:\setup\.stage_completed" (
        powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "if (-not (Get-CimInstance Win32_Process -ErrorAction SilentlyContinue | Where-Object { $_.Name -match 'powershell' -and $_.CommandLine -match 'Install-ServisApps\.ps1' })) { Start-Process powershell.exe -ArgumentList '-NoProfile -NoExit -ExecutionPolicy Bypass -File \"C:\setup\Install-ServisApps.ps1\"' -WindowStyle Maximized }"
    )
)
'@
                Set-Content -Path $resumeBat -Value $resumeContent -Encoding ASCII -Force -ErrorAction SilentlyContinue
            }
        } catch {}

        # 5. Zvucni signal i kratka pauza za dashboard
        try {
            if (Get-Command Play-ServisAudioNotification -ErrorAction SilentlyContinue) {
                Play-ServisAudioNotification -Type "Warning"
            }
        } catch {}

        Start-Sleep -Seconds 5

        # 6. Prinudni restart sistema
        & shutdown.exe /r /t 0 /f
        exit 0
    } else {
        Write-ServisLog "Medjurestart vec obavljen (Marker: $rebootMarker prisutan). Nastavljam Fazu 4..." -Level "INFO" -Component "Lifecycle"
    }
}

function Invoke-Phase3Optimization {
    [CmdletBinding()]
    param(
        [string]$LocalSetupDir = "C:\setup",
        [string]$Brand = ""
    )

    if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
        Write-ServisLog "=== POKRETANJE FAZE 3: Optimizacija, Debloat, Brending i VC++ ===" -Level "STEP" -Component "Phase3"
    }
    if (Get-Command Send-IpcProgress -ErrorAction SilentlyContinue) {
        Send-IpcProgress -Progress 5 -Phase 3 -PhaseName "Optimizacija" -SubPhase "Inicijalizacija" -StatusMessage "Započinjem sistemske optimizacije..."
    }

    if (Get-Command Wait-ServisUICooperativePause -ErrorAction SilentlyContinue) {
        Wait-ServisUICooperativePause -CheckpointName "Faza 3 - Pocetak"
    }

    # Odredjivanje OEM Brenda
    if (-not $Brand) {
        try {
            $cs = Get-CimInstance Win32_ComputerSystem -ErrorAction SilentlyContinue
            if ($cs -and $cs.Manufacturer) {
                $m = $cs.Manufacturer.ToUpper()
                if ($m -match 'HP|HEWLETT') { $Brand = "HP" }
                elseif ($m -match 'DELL') { $Brand = "DELL" }
                elseif ($m -match 'LENOVO') { $Brand = "LENOVO" }
                elseif ($m -match 'ASUS') { $Brand = "ASUS" }
                elseif ($m -match 'ACER') { $Brand = "ACER" }
                elseif ($m -match 'TOSHIBA|DYNABOOK') { $Brand = "TOSHIBA" }
                elseif ($m -match 'FUJITSU') { $Brand = "FUJITSU" }
                else { $Brand = "OEM" }
            }
        } catch {}
        if (-not $Brand) { $Brand = "OEM" }
    }
    if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
        Write-ServisLog "Identifikovan ciljni OEM brend za nalog: $Brand" -Level "INFO" -Component "Branding"
    }

    # 1. DEBLOAT WINDOWS APLIKACIJA (Uklanjanje sponzorisanih Appx paketa - Best-Effort)
    if (Get-Command Send-IpcProgress -ErrorAction SilentlyContinue) {
        Send-IpcProgress -Progress 15 -Phase 3 -PhaseName "Optimizacija" -SubPhase "Debloat aplikacija" -StatusMessage "Uklanjanje neželjenih preinstaliranih Windows aplikacija..."
    }
    if (Get-Command Wait-ServisUICooperativePause -ErrorAction SilentlyContinue) {
        Wait-ServisUICooperativePause -CheckpointName "Faza 3 - Debloat"
    }

    $skipRequested = (Get-Command Test-ServisSkipRequested -ErrorAction SilentlyContinue) -and (Test-ServisSkipRequested)
    if (-not $skipRequested) {
        $bloatPatterns = @(
            "*Clipchamp*",
            "*TikTok*",
            "*CandyCrush*",
            "*Facebook*",
            "*Instagram*",
            "*Spotify*",
            "*Disney*",
            "*PrimeVideo*",
            "*Netflix*",
            "*MicrosoftSolitaireCollection*",
            "*BingNews*",
            "*BingWeather*",
            "*GetHelp*",
            "*Getstarted*",
            "*MicrosoftTeams*",
            "*Todos*",
            "*PowerAutomateDesktop*",
            "*LinkedIn*",
            "*Xbox*",
            "*ZuneMusic*",
            "*ZuneVideo*"
        )

        $removedCount = 0
        foreach ($pat in $bloatPatterns) {
            try {
                $pkgs = Get-AppxPackage -Name $pat -AllUsers -ErrorAction SilentlyContinue
                foreach ($p in $pkgs) {
                    Remove-AppxPackage -Package $p.PackageFullName -AllUsers -ErrorAction SilentlyContinue
                    $removedCount++
                }
                $provPkgs = Get-AppxProvisionedPackage -Online -ErrorAction SilentlyContinue | Where-Object { $_.DisplayName -like $pat }
                foreach ($pp in $provPkgs) {
                    Remove-AppxProvisionedPackage -Online -PackageName $pp.PackageName -ErrorAction SilentlyContinue | Out-Null
                }
            } catch {}
        }
        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
            Write-ServisLog "Windows debloat zavrsen (uklonjeno bloat paketa: $removedCount)." -Level "SUCCESS" -Component "Debloat"
        }
    } else {
        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
            Write-ServisLog "Debloat korak preskočen na zahtev servisera." -Level "WARN" -Component "Debloat"
        }
    }

    # 2. GASENJE TELEMETRIJE I PRACENJA (Best-Effort)
    if (Get-Command Send-IpcProgress -ErrorAction SilentlyContinue) {
        Send-IpcProgress -Progress 35 -Phase 3 -PhaseName "Optimizacija" -SubPhase "Gašenje telemetrije" -StatusMessage "Gašenje telemetrije, dijagnostičkih servisa i praćenja..."
    }
    if (Get-Command Wait-ServisUICooperativePause -ErrorAction SilentlyContinue) {
        Wait-ServisUICooperativePause -CheckpointName "Faza 3 - Telemetrija"
    }

    try {
        # DataCollection & Telemetrija
        $dcReg = "HKLM:\SOFTWARE\Policies\Microsoft\Windows\DataCollection"
        if (-not (Test-Path $dcReg)) { New-Item -Path $dcReg -Force -ErrorAction SilentlyContinue | Out-Null }
        Set-ItemProperty -Path $dcReg -Name "AllowTelemetry" -Type DWord -Value 0 -Force -ErrorAction SilentlyContinue
        Set-ItemProperty -Path $dcReg -Name "MaxTelemetryAllowed" -Type DWord -Value 0 -Force -ErrorAction SilentlyContinue

        # Gasenje dijagnostickih servisa
        Stop-Service -Name "DiagTrack" -WarningAction SilentlyContinue -ErrorAction SilentlyContinue
        Set-Service -Name "DiagTrack" -StartupType Disabled -ErrorAction SilentlyContinue

        Stop-Service -Name "dmwappushservice" -WarningAction SilentlyContinue -ErrorAction SilentlyContinue
        Set-Service -Name "dmwappushservice" -StartupType Disabled -ErrorAction SilentlyContinue

        # Location & Senzori
        $locReg = "HKLM:\SOFTWARE\Policies\Microsoft\Windows\LocationAndSensors"
        if (-not (Test-Path $locReg)) { New-Item -Path $locReg -Force -ErrorAction SilentlyContinue | Out-Null }
        Set-ItemProperty -Path $locReg -Name "DisableLocation" -Type DWord -Value 1 -Force -ErrorAction SilentlyContinue
        Stop-Service -Name "lfsvc" -WarningAction SilentlyContinue -ErrorAction SilentlyContinue
        Set-Service -Name "lfsvc" -StartupType Disabled -ErrorAction SilentlyContinue

        # Advertising ID & Bing Search u Start Meniju
        $advReg = "HKCU:\Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo"
        if (-not (Test-Path $advReg)) { New-Item -Path $advReg -Force -ErrorAction SilentlyContinue | Out-Null }
        Set-ItemProperty -Path $advReg -Name "Enabled" -Type DWord -Value 0 -Force -ErrorAction SilentlyContinue

        $srchReg = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Search"
        if (-not (Test-Path $srchReg)) { New-Item -Path $srchReg -Force -ErrorAction SilentlyContinue | Out-Null }
        Set-ItemProperty -Path $srchReg -Name "BingSearchEnabled" -Type DWord -Value 0 -Force -ErrorAction SilentlyContinue
        Set-ItemProperty -Path $srchReg -Name "CortanaConsent" -Type DWord -Value 0 -Force -ErrorAction SilentlyContinue

        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
            Write-ServisLog "Telemetrija i pracenje uspesno deaktivirani." -Level "SUCCESS" -Component "Telemetry"
        }
    } catch {
        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
            Write-ServisLog "Upozorenje pri gasenju telemetrije: $($_.Exception.Message)" -Level "WARN" -Component "Telemetry"
        }
    }

    # 3. SERVISNI OEM BRENDING I REGIONALNA PODEŠAVANJA (Best-Effort)
    if (Get-Command Send-IpcProgress -ErrorAction SilentlyContinue) {
        Send-IpcProgress -Progress 55 -Phase 3 -PhaseName "Optimizacija" -SubPhase "OEM Brending" -StatusMessage "Primenjujem OEM brending ($Brand) i regionalna podešavanja..."
    }
    if (Get-Command Wait-ServisUICooperativePause -ErrorAction SilentlyContinue) {
        Wait-ServisUICooperativePause -CheckpointName "Faza 3 - Brending"
    }

    try {
        if (Get-Command Invoke-SystemOptimizationAndBranding -ErrorAction SilentlyContinue) {
            Invoke-SystemOptimizationAndBranding -Brand $Brand -LocalSetupDir $LocalSetupDir -ProgramiDir (Join-Path $LocalSetupDir "Programi")
        } else {
            try {
                $langList = Get-WinUserLanguageList
                $srLatn = "sr-Latn-RS"
                $srCyrl = "sr-Cyrl-RS"
                if ($langList.LanguageTag -notmatch $srLatn) { $langList.Add($srLatn) }
                if ($langList.LanguageTag -notmatch $srCyrl) { $langList.Add($srCyrl) }
                Set-WinUserLanguageList -LanguageList $langList -Force -ErrorAction SilentlyContinue
                Set-TimeZone -Id "Central Europe Standard Time" -ErrorAction SilentlyContinue

                $regIntl = "HKCU:\Control Panel\International"
                Set-ItemProperty -Path $regIntl -Name "sTimeFormat" -Value "HH:mm" -Type String -Force -ErrorAction SilentlyContinue
                Set-ItemProperty -Path $regIntl -Name "sShortTime" -Value "HH:mm" -Type String -Force -ErrorAction SilentlyContinue
                Set-ItemProperty -Path $regIntl -Name "sLongTime"  -Value "HH:mm:ss" -Type String -Force -ErrorAction SilentlyContinue

                $targetUser = "Korisnik"
                if (Get-LocalUser -Name $targetUser -ErrorAction SilentlyContinue) {
                    Rename-LocalUser -Name $targetUser -NewName $Brand -ErrorAction SilentlyContinue
                    Set-LocalUser -Name $Brand -FullName $Brand -ErrorAction SilentlyContinue
                    Set-ItemProperty -Path "HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon" -Name "DefaultUserName" -Value $Brand -ErrorAction SilentlyContinue
                }
            } catch {}
        }
    } catch {
        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
            Write-ServisLog "Upozorenje pri brendiranju: $($_.Exception.Message)" -Level "WARN" -Component "Branding"
        }
    }

    # DPI Skaliranje (Best-Effort)
    try {
        if (Get-Command Set-OptimalDisplayScaling -ErrorAction SilentlyContinue) {
            Set-OptimalDisplayScaling
        }
    } catch {
        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
            Write-ServisLog "Upozorenje pri podešavanju DPI skaliranja: $($_.Exception.Message)" -Level "DEBUG" -Component "Branding"
        }
    }

    # 4. INSTALACIJA VISUAL C++ REDISTRIBUTABLES (2005-2022) (Striktni Best-Effort)
    if (Get-Command Send-IpcProgress -ErrorAction SilentlyContinue) {
        Send-IpcProgress -Progress 75 -Phase 3 -PhaseName "Optimizacija" -SubPhase "Visual C++ Runtime" -StatusMessage "Instalacija Microsoft Visual C++ Redistributable paketa..."
    }
    if (Get-Command Wait-ServisUICooperativePause -ErrorAction SilentlyContinue) {
        Wait-ServisUICooperativePause -CheckpointName "Faza 3 - VC++ Runtime"
    }

    $vcDirs = @(
        (Join-Path $LocalSetupDir "Programi\VCRedist"),
        (Join-Path $LocalSetupDir "Programi\VC"),
        (Join-Path $LocalSetupDir "Programi\DotNet"),
        "C:\setup\Programi\VCRedist",
        "C:\setup\Programi\DotNet"
    )
    foreach ($d in (Get-PSDrive -PSProvider FileSystem -ErrorAction SilentlyContinue)) {
        $cand = Join-Path "$($d.Root)" "Programi\VCRedist"
        if ($vcDirs -notcontains $cand) { $vcDirs += $cand }
    }

    $vcInstallers = @()
    foreach ($vcd in $vcDirs) {
        if (Test-Path $vcd) {
            try {
                $exes = Get-ChildItem -Path $vcd -Filter "*.exe" -Recurse -File -ErrorAction SilentlyContinue | Where-Object {
                    $_.Name -match '(?i)vcredist|vc_redist|visual_c'
                }
                foreach ($e in $exes) { $vcInstallers += $e }
            } catch {}
        }
    }

    if ($vcInstallers.Count -gt 0) {
        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
            Write-ServisLog "Pronadjeno $($vcInstallers.Count) Visual C++ instalera. Pokrecem tihu instalaciju..." -Level "INFO" -Component "VCRedist"
        }
        foreach ($inst in $vcInstallers) {
            if (Get-Command Wait-ServisUICooperativePause -ErrorAction SilentlyContinue) {
                Wait-ServisUICooperativePause -CheckpointName "Faza 3 - VC++ ($($inst.Name))"
            }
            if ((Get-Command Test-ServisSkipRequested -ErrorAction SilentlyContinue) -and (Test-ServisSkipRequested)) {
                if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                    Write-ServisLog "Preskočen instaler $($inst.Name) na zahtev servisera." -Level "WARN" -Component "VCRedist"
                }
                continue
            }

            if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                Write-ServisLog "Instaliram VC++: $($inst.Name)" -Level "STEP" -Component "VCRedist"
            }
            try {
                $p = Start-Process -FilePath $inst.FullName -ArgumentList "/install /quiet /norestart /q" -Wait -PassThru -WindowStyle Hidden
                $exitCode = if ($p) { $p.ExitCode } else { -1 }
                $logLvl = if ($exitCode -eq 0 -or $exitCode -eq 3010) { "SUCCESS" } else { "WARN" }
                if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                    Write-ServisLog "Instaler $($inst.Name) zavrsen (ExitCode: $exitCode)." -Level $logLvl -Component "VCRedist"
                }
            } catch {
                if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                    Write-ServisLog "Upozorenje pri instalaciji $($inst.Name): $($_.Exception.Message)" -Level "WARN" -Component "VCRedist"
                }
                if (Get-Command Send-IpcLog -ErrorAction SilentlyContinue) {
                    Send-IpcLog -Message "Upozorenje pri instalaciji VC++ paketa $($inst.Name), nastavljam..." -Level "WARN" -Phase 3
                }
            }
        }
    } else {
        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
            Write-ServisLog "Nisu pronadjeni lokalni VC++ paketi. Proveravam Winget..." -Level "DEBUG" -Component "VCRedist"
        }
        try {
            Start-Process -FilePath "winget.exe" -ArgumentList "install --id Microsoft.VCRedist.2015+.x64 --silent --accept-package-agreements --accept-source-agreements" -Wait -WindowStyle Hidden -ErrorAction SilentlyContinue
            Start-Process -FilePath "winget.exe" -ArgumentList "install --id Microsoft.VCRedist.2015+.x86 --silent --accept-package-agreements --accept-source-agreements" -Wait -WindowStyle Hidden -ErrorAction SilentlyContinue
        } catch {
            if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                Write-ServisLog "Winget fallback za VC++ nije dostupan." -Level "DEBUG" -Component "VCRedist"
            }
        }
    }

    # 5. OSLOBAĐANJE USB MEDIJUMA (FLASH & RELEASE SIGNAL)
    if (Get-Command Send-IpcProgress -ErrorAction SilentlyContinue) {
        Send-IpcProgress -Progress 90 -Phase 3 -PhaseName "Optimizacija" -SubPhase "USB Free" -StatusMessage "Optimizacije završene. USB fleš disk je slobodan za uklanjanje."
    }
    if (Get-Command Send-IpcStatus -ErrorAction SilentlyContinue) {
        Send-IpcStatus -Phase 3 -Progress 95 -SubPhase "Spremno za programe" -StatusMessage "Sistem optimizovan i brendiran." -UsbFree
    }

    # Perzistencija stanja u deploy-state.json
    try {
        if (Get-Command Save-DeployStatePhase -ErrorAction SilentlyContinue) {
            Save-DeployStatePhase -Phase 4
        }
    } catch {}

    if (Get-Command Send-IpcProgress -ErrorAction SilentlyContinue) {
        Send-IpcProgress -Progress 100 -Phase 3 -PhaseName "Optimizacija" -SubPhase "Završeno" -StatusMessage "Faza 3 uspešno završena. Sistem je optimizovan i brendiran."
    }
    if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
        Write-ServisLog "=== FAZA 3 USPESNO ZAVRSENA: Debloat, telemetrija, brending i VC++ kompletirani ===" -Level "SUCCESS" -Component "Phase3"
    }

    $global:LASTEXITCODE = 0
}

Export-ModuleMember -Function `
    Invoke-SystemOptimizationAndBranding, `
    Set-OptimalDisplayScaling, `
    Invoke-ServisInterimReboot, `
    Invoke-Phase3Optimization
