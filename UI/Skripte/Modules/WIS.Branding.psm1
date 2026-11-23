<#
.MODULE
    WIS.Branding.psm1
.DESCRIPTION
    Sistemske optimizacije, OEM brendiranje korisnickog naloga, regionalna podesavanja i gasenje telemetrije za WIS (Windows Installation Assistant).
.COMPATIBILITY
    Windows PowerShell 5.1+
#>

$Global:WisBloatPatterns = @(
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

$Global:WisTelemetryConfig = @{
    RegValues = @(
        @{ Path = "HKLM:\SOFTWARE\Policies\Microsoft\Windows\DataCollection"; Name = "AllowTelemetry"; Value = 0; Type = "DWord" },
        @{ Path = "HKLM:\SOFTWARE\Policies\Microsoft\Windows\DataCollection"; Name = "MaxTelemetryAllowed"; Value = 0; Type = "DWord" },
        @{ Path = "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\location"; Name = "ShowGlobalPrompts"; Value = 0; Type = "DWord" },
        @{ Path = "HKCU:\Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\location"; Name = "ShowGlobalPrompts"; Value = 0; Type = "DWord" },
        @{ Path = "HKCU:\Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo"; Name = "Enabled"; Value = 0; Type = "DWord" },
        @{ Path = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Search"; Name = "BingSearchEnabled"; Value = 0; Type = "DWord" },
        @{ Path = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Search"; Name = "CortanaConsent"; Value = 0; Type = "DWord" }
    )
    Services = @("DiagTrack", "dmwappushservice")
}

function Disable-LocationPrivacyPrompts {
    <#
    .SYNOPSIS
        Omogućava pristup lokaciji za Win32/NonPackaged aplikacije (ConsentStore\location Value="Allow")
        i potiskuje Windows 11 24H2+ modalni prozor (ShowGlobalPrompts=0) u svim profilima,
        uz uklanjanje zaostale polise DisableLocation=1 i osiguranje da servis lfsvc nije Disabled.
    #>
    [CmdletBinding()]
    param()

    $subKey = "Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\location"
    foreach ($rootPath in @("HKLM:\$subKey", "HKCU:\$subKey")) {
        try {
            if (-not (Test-Path $rootPath)) {
                New-Item -Path $rootPath -Force -ErrorAction SilentlyContinue | Out-Null
            }
            Set-ItemProperty -Path $rootPath -Name "ShowGlobalPrompts" -Type DWord -Value 0 -Force -ErrorAction SilentlyContinue
            Set-ItemProperty -Path $rootPath -Name "Value" -Type String -Value "Allow" -Force -ErrorAction SilentlyContinue

            $nonPkgPath = Join-Path $rootPath "NonPackaged"
            if (-not (Test-Path $nonPkgPath)) {
                New-Item -Path $nonPkgPath -Force -ErrorAction SilentlyContinue | Out-Null
            }
            Set-ItemProperty -Path $nonPkgPath -Name "Value" -Type String -Value "Allow" -Force -ErrorAction SilentlyContinue
        } catch {}
    }

    # Primena na sve vec ucitane korisnicke profile u HKEY_USERS
    try {
        $sidRoots = Get-ChildItem -Path "Registry::HKEY_USERS" -ErrorAction SilentlyContinue |
            Where-Object { $_.PSChildName -match '^S-1-5-21-' -and $_.PSChildName -notlike '*_Classes' }
        foreach ($sid in $sidRoots) {
            try {
                $userLocPath = Join-Path $sid.PSPath $subKey
                if (-not (Test-Path $userLocPath)) {
                    New-Item -Path $userLocPath -Force -ErrorAction SilentlyContinue | Out-Null
                }
                Set-ItemProperty -Path $userLocPath -Name "ShowGlobalPrompts" -Type DWord -Value 0 -Force -ErrorAction SilentlyContinue
                Set-ItemProperty -Path $userLocPath -Name "Value" -Type String -Value "Allow" -Force -ErrorAction SilentlyContinue

                $userNonPkgPath = Join-Path $userLocPath "NonPackaged"
                if (-not (Test-Path $userNonPkgPath)) {
                    New-Item -Path $userNonPkgPath -Force -ErrorAction SilentlyContinue | Out-Null
                }
                Set-ItemProperty -Path $userNonPkgPath -Name "Value" -Type String -Value "Allow" -Force -ErrorAction SilentlyContinue
            } catch {}
        }
    } catch {}

    # Primena na C:\Users\Default\NTUSER.DAT da se dozvola i potiskivanje upita prenesu i na buduce korisnike
    $defaultHive = "C:\Users\Default\NTUSER.DAT"
    if (Test-Path $defaultHive) {
        $mountKey = "HKU\WIS_DefaultLoc"
        $loaded = $false
        try {
            & reg.exe load $mountKey $defaultHive >$null 2>&1
            if ($LASTEXITCODE -eq 0) {
                $loaded = $true
                & reg.exe add "$mountKey\$subKey" /v "ShowGlobalPrompts" /t REG_DWORD /d 0 /f >$null 2>&1
                & reg.exe add "$mountKey\$subKey" /v "Value" /t REG_SZ /d "Allow" /f >$null 2>&1
                & reg.exe add "$mountKey\$subKey\NonPackaged" /v "Value" /t REG_SZ /d "Allow" /f >$null 2>&1
            }
        } catch {}
        finally {
            if ($loaded) {
                try { [gc]::Collect(); [gc]::WaitForPendingFinalizers() } catch {}
                & reg.exe unload $mountKey >$null 2>&1
            }
        }
    }

    # Uklanjanje eventualne zaostale polise DisableLocation=1
    try {
        $locPolicyPath = "HKLM:\SOFTWARE\Policies\Microsoft\Windows\LocationAndSensors"
        if (Test-Path $locPolicyPath) {
            $dlProp = Get-ItemProperty -Path $locPolicyPath -Name "DisableLocation" -ErrorAction SilentlyContinue
            if ($null -ne $dlProp -and $null -ne $dlProp.DisableLocation -and [int]$dlProp.DisableLocation -eq 1) {
                Remove-ItemProperty -Path $locPolicyPath -Name "DisableLocation" -Force -ErrorAction SilentlyContinue
                if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                    Write-ServisLog "Uklonjena zaostala LocationAndSensors\DisableLocation=1 polisa." -Level "INFO" -Component "Branding"
                }
            }
        }
    } catch {}

    # Osiguranje da Windows Location Service (lfsvc) nije Disabled i da radi ako je zaustavljen
    try {
        $lfsvcRegPath = "HKLM:\SYSTEM\CurrentControlSet\Services\lfsvc"
        if (Test-Path $lfsvcRegPath) {
            $lfStart = (Get-ItemProperty -Path $lfsvcRegPath -Name "Start" -ErrorAction SilentlyContinue).Start
            if ($null -ne $lfStart -and [int]$lfStart -eq 4) {
                Set-Service -Name "lfsvc" -StartupType Manual -ErrorAction SilentlyContinue
                if (Get-Command sc.exe -ErrorAction SilentlyContinue) {
                    & sc.exe config lfsvc start= demand >$null 2>&1
                }
            }
        }
        $lfsvcSvc = Get-Service -Name "lfsvc" -ErrorAction SilentlyContinue
        if ($lfsvcSvc) {
            if ($lfsvcSvc.StartType -eq "Disabled") {
                Set-Service -Name "lfsvc" -StartupType Manual -ErrorAction SilentlyContinue
            }
            if ($lfsvcSvc.Status -ne "Running") {
                Start-Service -Name "lfsvc" -ErrorAction SilentlyContinue
            }
        }
    } catch {}
}

function Test-WisLocationAccessEnabled {
    <#
    .SYNOPSIS
        Proverava da li je Location ConsentStore postavljen na "Allow" (HKLM/HKCU + NonPackaged),
        da ne postoji DisableLocation=1 i da lfsvc nije Disabled.
    #>
    [CmdletBinding()]
    param()

    $subKey = "Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\location"
    foreach ($rootPath in @("HKLM:\$subKey", "HKCU:\$subKey")) {
        $val = (Get-ItemProperty -Path $rootPath -Name "Value" -ErrorAction SilentlyContinue).Value
        if ("$val" -ine "Allow") { return $false }

        $nonPkgVal = (Get-ItemProperty -Path (Join-Path $rootPath "NonPackaged") -Name "Value" -ErrorAction SilentlyContinue).Value
        if ("$nonPkgVal" -ine "Allow") { return $false }
    }

    $locPolicyPath = "HKLM:\SOFTWARE\Policies\Microsoft\Windows\LocationAndSensors"
    if (Test-Path $locPolicyPath) {
        $dlProp = Get-ItemProperty -Path $locPolicyPath -Name "DisableLocation" -ErrorAction SilentlyContinue
        if ($null -ne $dlProp -and $null -ne $dlProp.DisableLocation -and [int]$dlProp.DisableLocation -eq 1) {
            return $false
        }
    }

    $lfsvcRegPath = "HKLM:\SYSTEM\CurrentControlSet\Services\lfsvc"
    if (Test-Path $lfsvcRegPath) {
        $lfStart = (Get-ItemProperty -Path $lfsvcRegPath -Name "Start" -ErrorAction SilentlyContinue).Start
        if ($null -ne $lfStart -and [int]$lfStart -eq 4) {
            return $false
        }
    }

    return $true
}

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
        try {
            if (-not (Test-Path "HKCU:\Control Panel\Desktop")) {
                New-Item -Path "HKCU:\Control Panel\Desktop" -Force | Out-Null
            }
            Set-ItemProperty -Path "HKCU:\Control Panel\Desktop" -Name "LogPixels" -Value 96 -Type DWord -ErrorAction SilentlyContinue
            if (-not (Test-Path "C:\setup")) {
                New-Item -Path "C:\setup" -ItemType Directory -Force | Out-Null
            }
            Set-Content -Path "C:\setup\dpi-scaling-done.flag" -Value "100% (${screenWidth}x${screenHeight})" -Encoding UTF8 -Force -ErrorAction SilentlyContinue
        } catch {}
        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
            Write-ServisLog "Rezolucija ekrana je ${screenWidth}x${screenHeight} ($scaleCategory). Zadrzava se fabricki default 100% scale (LogPixels=96, flag kreiran)." -Level "INFO" -Component "Branding"
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

    # B. Gasenje telemetrije
    try {
        New-Item -Path "HKLM:\SOFTWARE\Policies\Microsoft\Windows\DataCollection" -Force -ErrorAction SilentlyContinue | Out-Null
        Set-ItemProperty -Path "HKLM:\SOFTWARE\Policies\Microsoft\Windows\DataCollection" -Name "AllowTelemetry" -Type DWord -Value 0 -Force -ErrorAction SilentlyContinue
        Stop-Service -Name "DiagTrack" -WarningAction SilentlyContinue -ErrorAction SilentlyContinue
        Set-Service -Name "DiagTrack" -StartupType Disabled -ErrorAction SilentlyContinue

        Stop-Service -Name "dmwappushservice" -WarningAction SilentlyContinue -ErrorAction SilentlyContinue
        Set-Service -Name "dmwappushservice" -StartupType Disabled -ErrorAction SilentlyContinue

        Disable-LocationPrivacyPrompts

        New-Item -Path "HKCU:\Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo" -Force -ErrorAction SilentlyContinue | Out-Null
        Set-ItemProperty -Path "HKCU:\Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo" -Name "Enabled" -Type DWord -Value 0 -Force -ErrorAction SilentlyContinue

        New-Item -Path "HKCU:\Software\Microsoft\Windows\CurrentVersion\Search" -Force -ErrorAction SilentlyContinue | Out-Null
        Set-ItemProperty -Path "HKCU:\Software\Microsoft\Windows\CurrentVersion\Search" -Name "BingSearchEnabled" -Type DWord -Value 0 -Force -ErrorAction SilentlyContinue

        Set-ItemProperty -Path "HKCU:\Software\Microsoft\Windows\CurrentVersion\Search" -Name "CortanaConsent" -Type DWord -Value 0 -Force -ErrorAction SilentlyContinue
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
        if (Get-Command Send-IpcStatus -ErrorAction SilentlyContinue) {
            Send-IpcStatus -Phase 3 -SubPhase "Restart u toku" -StatusMessage "Drajveri i optimizacije primenjeni. Sistem se restartuje radi stabilizacije..."
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

    # Uvoz StepGuard modula ukoliko funkcija Invoke-WisStep nije registrovana u sesiji
    if (-not (Get-Command Invoke-WisStep -ErrorAction SilentlyContinue)) {
        $stepGuardMod = @(
            (Join-Path $PSScriptRoot "WIS.StepGuard.psm1"),
            (Join-Path $PSScriptRoot "..\Modules\WIS.StepGuard.psm1"),
            (Join-Path $LocalSetupDir "Skripte\Modules\WIS.StepGuard.psm1"),
            "C:\setup\Skripte\Modules\WIS.StepGuard.psm1"
        ) | Where-Object { Test-Path $_ } | Select-Object -First 1
        if ($stepGuardMod) { Import-Module $stepGuardMod -Force -ErrorAction SilentlyContinue }
    }

    # Emitovanje liste svih koraka u stanju WAIT da bi UI odmah iscrtao listu koraka
    $phase3StepCatalog = @(
        @{ Id = "P3.Debloat";   Title = "Debloat neželjenih aplikacija" },
        @{ Id = "P3.Telemetry"; Title = "Gašenje telemetrije i praćenja" },
        @{ Id = "P3.Oem";       Title = "OEM brending i regionalna podešavanja" },
        @{ Id = "P3.Scaling";   Title = "Optimalno DPI skaliranje ekrana" },
        @{ Id = "P3.VcRedist";  Title = "Visual C++ Runtime biblioteke" },
        @{ Id = "P3.UsbFree";   Title = "Signal za oslobađanje USB medijuma" }
    )
    foreach ($sc in $phase3StepCatalog) {
        if (Get-Command Send-IpcStep -ErrorAction SilentlyContinue) {
            Send-IpcStep -Phase 3 -StepId $sc.Id -Title $sc.Title -State "WAIT" -Reason ""
        }
    }

    # Odredjivanje OEM Brenda
    if (-not $Brand) {
        try {
            $cs = Get-CimInstance Win32_ComputerSystem -ErrorAction SilentlyContinue
            if ($cs -and $cs.Manufacturer) {
                $m = $cs.Manufacturer.ToUpper()
                if ($m -match "HP|HEWLETT") { $Brand = "HP" }
                elseif ($m -match "DELL") { $Brand = "DELL" }
                elseif ($m -match "LENOVO") { $Brand = "LENOVO" }
                elseif ($m -match "ASUS") { $Brand = "ASUS" }
                elseif ($m -match "ACER") { $Brand = "ACER" }
                elseif ($m -match "TOSHIBA|DYNABOOK") { $Brand = "TOSHIBA" }
                elseif ($m -match "FUJITSU") { $Brand = "FUJITSU" }
                else { $Brand = "OEM" }
            }
        } catch {}
        if (-not $Brand) { $Brand = "OEM" }
    }
    if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
        Write-ServisLog "Identifikovan ciljni OEM brend za nalog: $Brand" -Level "INFO" -Component "Branding"
    }

    # =========================================================================
    # 1. DEBLOAT WINDOWS APLIKACIJA - P3.Debloat
    # =========================================================================
    $null = Invoke-WisStep -Phase 3 -StepId "P3.Debloat" -Title "Debloat neželjenih aplikacija" -Test {
        if (Test-Path "C:\setup\debloat-done.flag") {
            return @{ Done = $true; Reason = "Windows debloat je izvrsen (debloat-done.flag prisutan)" }
        }
        $foundBloat = @()
        $allAppx = @(Get-AppxPackage -AllUsers -ErrorAction SilentlyContinue)
        $allProv = @(Get-AppxProvisionedPackage -Online -ErrorAction SilentlyContinue)
        foreach ($pat in $Global:WisBloatPatterns) {
            if ($allAppx.Count -gt 0) {
                $pkgs = @($allAppx | Where-Object { $_.Name -like $pat })
                foreach ($p in $pkgs) { $foundBloat += $p.Name }
            }
            if ($allProv.Count -gt 0) {
                $provPkgs = @($allProv | Where-Object { $_.DisplayName -like $pat })
                foreach ($pp in $provPkgs) { $foundBloat += $pp.DisplayName }
            }
        }
        if ($foundBloat.Count -eq 0) {
            return @{ Done = $true; Reason = "Sve neželjene sponzorisane aplikacije su već uklonjene" }
        } else {
            return @{ Done = $false; Reason = "Pronađeno $($foundBloat.Count) preinstaliranih aplikacija" }
        }
    } -Action {
        if (Get-Command Send-IpcProgress -ErrorAction SilentlyContinue) {
            Send-IpcProgress -Progress 15 -Phase 3 -PhaseName "Optimizacija" -SubPhase "Debloat aplikacija" -StatusMessage "Uklanjanje neželjenih preinstaliranih Windows aplikacija..."
        }
        if (Get-Command Wait-ServisUICooperativePause -ErrorAction SilentlyContinue) {
            Wait-ServisUICooperativePause -CheckpointName "Faza 3 - Debloat"
        }

        if ((Get-Command Test-ServisSkipRequested -ErrorAction SilentlyContinue) -and (Test-ServisSkipRequested)) {
            if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                Write-ServisLog "Debloat korak preskočen na zahtev servisera." -Level "WARN" -Component "Debloat"
            }
            return
        }

        $removedCount = 0
        $allAppx = @(Get-AppxPackage -AllUsers -ErrorAction SilentlyContinue)
        $allProv = @(Get-AppxProvisionedPackage -Online -ErrorAction SilentlyContinue)
        $removedPkgMap = @{}
        $removedProvMap = @{}
        foreach ($pat in $Global:WisBloatPatterns) {
            try {
                if ($allAppx.Count -gt 0) {
                    $pkgs = @($allAppx | Where-Object { $_.Name -like $pat })
                    foreach ($p in $pkgs) {
                        if ($p.PackageFullName -and -not $removedPkgMap.ContainsKey($p.PackageFullName)) {
                            $removedPkgMap[$p.PackageFullName] = $true
                            Remove-AppxPackage -Package $p.PackageFullName -AllUsers -ErrorAction SilentlyContinue | Out-Null
                            $removedCount++
                        }
                    }
                }
                if ($allProv.Count -gt 0) {
                    $provPkgs = @($allProv | Where-Object { $_.DisplayName -like $pat })
                    foreach ($pp in $provPkgs) {
                        if ($pp.PackageName -and -not $removedProvMap.ContainsKey($pp.PackageName)) {
                            $removedProvMap[$pp.PackageName] = $true
                            Remove-AppxProvisionedPackage -Online -PackageName $pp.PackageName -ErrorAction SilentlyContinue | Out-Null
                        }
                    }
                }
            } catch {}
        }
        try {
            if (-not (Test-Path "C:\setup")) { New-Item -Path "C:\setup" -ItemType Directory -Force -ErrorAction SilentlyContinue | Out-Null }
            Set-Content -Path "C:\setup\debloat-done.flag" -Value (Get-Date).ToString("o") -Encoding UTF8 -Force -ErrorAction SilentlyContinue
        } catch {}
        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
            Write-ServisLog "Windows debloat zavrsen (uklonjeno bloat paketa: $removedCount)." -Level "SUCCESS" -Component "Debloat"
        }
    }

    # =========================================================================
    # 2. GAŠENJE TELEMETRIJE I PRAĆENJA - P3.Telemetry
    # =========================================================================
    $null = Invoke-WisStep -Phase 3 -StepId "P3.Telemetry" -Title "Gašenje telemetrije i praćenja" -Test {
        $allOk = $true
        $unmatched = @()

        foreach ($entry in $Global:WisTelemetryConfig.RegValues) {
            $curr = (Get-ItemProperty -Path $entry.Path -Name $entry.Name -ErrorAction SilentlyContinue).$($entry.Name)
            if ($null -eq $curr -or [int]$curr -ne [int]$entry.Value) {
                $allOk = $false
                $unmatched += "$($entry.Name) != $($entry.Value)"
            }
        }

        foreach ($sName in $Global:WisTelemetryConfig.Services) {
            $svcRegPath = "HKLM:\SYSTEM\CurrentControlSet\Services\$sName"
            if (Test-Path $svcRegPath) {
                $startVal = (Get-ItemProperty -Path $svcRegPath -Name "Start" -ErrorAction SilentlyContinue).Start
                if ($null -ne $startVal -and [int]$startVal -ne 4) {
                    $allOk = $false
                    $unmatched += "Servis $sName (Start=$startVal)"
                }
            }
        }

        if (-not (Test-WisLocationAccessEnabled)) {
            $allOk = $false
            $unmatched += "LocationAllow != Allow"
        }

        if ($allOk) {
            return @{ Done = $true; Reason = "Telemetrija i servisi za praćenje su već uspešno deaktivirani" }
        } else {
            return @{ Done = $false; Reason = "Telemetrija aktivna: $($unmatched -join ', ')" }
        }
    } -Action {
        if (Get-Command Send-IpcProgress -ErrorAction SilentlyContinue) {
            Send-IpcProgress -Progress 35 -Phase 3 -PhaseName "Optimizacija" -SubPhase "Gašenje telemetrije" -StatusMessage "Gašenje telemetrije, dijagnostičkih servisa i praćenja..."
        }
        if (Get-Command Wait-ServisUICooperativePause -ErrorAction SilentlyContinue) {
            Wait-ServisUICooperativePause -CheckpointName "Faza 3 - Telemetrija"
        }

        foreach ($entry in $Global:WisTelemetryConfig.RegValues) {
            try {
                if (-not (Test-Path $entry.Path)) {
                    New-Item -Path $entry.Path -Force -ErrorAction SilentlyContinue | Out-Null
                }
                Set-ItemProperty -Path $entry.Path -Name $entry.Name -Type $entry.Type -Value $entry.Value -Force -ErrorAction SilentlyContinue
            } catch {}
        }

        foreach ($sName in $Global:WisTelemetryConfig.Services) {
            try {
                Stop-Service -Name $sName -WarningAction SilentlyContinue -ErrorAction SilentlyContinue
                Set-Service -Name $sName -StartupType Disabled -ErrorAction SilentlyContinue
            } catch {}
        }

        Disable-LocationPrivacyPrompts

        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
            Write-ServisLog "Telemetrija i pracenje uspesno deaktivirani." -Level "SUCCESS" -Component "Telemetry"
        }
    }

    # =========================================================================
    # 3. SERVISNI OEM BRENDING I REGIONALNA PODEŠAVANJA - P3.Oem
    # =========================================================================
    $null = Invoke-WisStep -Phase 3 -StepId "P3.Oem" -Title "OEM brending i regionalna podešavanja" -Test {
        $tz = (Get-TimeZone -ErrorAction SilentlyContinue).Id
        $regIntl = "HKCU:\Control Panel\International"
        $sTime = (Get-ItemProperty -Path $regIntl -Name "sTimeFormat" -ErrorAction SilentlyContinue).sTimeFormat
        $isTzOk = ($tz -match "Central Europe")
        $isTimeOk = ($sTime -eq "HH:mm")

        if ($isTzOk -and $isTimeOk) {
            return @{ Done = $true; Reason = "OEM brending i regionalna podešavanja (vremenska zona $tz, 24h sat) već primenjeni" }
        } else {
            return @{ Done = $false; Reason = "Regionalna podešavanja nisu kompletna (TZ: $tz, Vreme: $sTime)" }
        }
    } -Action {
        if (Get-Command Send-IpcProgress -ErrorAction SilentlyContinue) {
            Send-IpcProgress -Progress 55 -Phase 3 -PhaseName "Optimizacija" -SubPhase "OEM Brending" -StatusMessage "Primenjujem OEM brending ($Brand) i regionalna podešavanja..."
        }
        if (Get-Command Wait-ServisUICooperativePause -ErrorAction SilentlyContinue) {
            Wait-ServisUICooperativePause -CheckpointName "Faza 3 - Brending"
        }

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
    }

    # =========================================================================
    # 4. OPTIMALNO DPI SKALIRANJE EKRANA - P3.Scaling
    # =========================================================================
    $null = Invoke-WisStep -Phase 3 -StepId "P3.Scaling" -Title "Optimalno DPI skaliranje ekrana" -Test {
        $dpiFlagPath = "C:\setup\dpi-scaling-done.flag"
        $logPixels = (Get-ItemProperty -Path "HKCU:\Control Panel\Desktop" -Name "LogPixels" -ErrorAction SilentlyContinue).LogPixels
        if ((Test-Path $dpiFlagPath) -or ($null -ne $logPixels -and [int]$logPixels -ge 96)) {
            $reasonMsg = if ($null -ne $logPixels) { "DPI skaliranje je podešeno (LogPixels = $logPixels)" } else { "DPI skaliranje je potvrđeno flagom ($dpiFlagPath)" }
            return @{ Done = $true; Reason = $reasonMsg }
        } else {
            return @{ Done = $false; Reason = "DPI skaliranje još nije konfigurisano" }
        }
    } -Action {
        try {
            Set-OptimalDisplayScaling
        } catch {
            if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                Write-ServisLog "Upozorenje pri podešavanju DPI skaliranja: $($_.Exception.Message)" -Level "DEBUG" -Component "Branding"
            }
        }
    }

    # =========================================================================
    # 5. INSTALACIJA VISUAL C++ REDISTRIBUTABLES - P3.VcRedist
    # =========================================================================
    $null = Invoke-WisStep -Phase 3 -StepId "P3.VcRedist" -Title "Visual C++ Runtime biblioteke" -Test {
        $uninstallKeys = @(
            "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
            "HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
        )
        $hasVc64 = $false
        $hasVcx86 = $false

        foreach ($uk in $uninstallKeys) {
            if (Test-Path $uk) {
                $subKeys = Get-ChildItem -Path $uk -ErrorAction SilentlyContinue
                foreach ($sk in $subKeys) {
                    $dn = (Get-ItemProperty -Path $sk.PSPath -Name "DisplayName" -ErrorAction SilentlyContinue).DisplayName
                    if ($dn -and $dn -match "Visual C\+\+.*(2015-2022|2015-2019|2022)") {
                        if ($dn -match "x64") { $hasVc64 = $true }
                        if ($dn -match "x86") { $hasVcx86 = $true }
                    }
                }
            }
        }

        if ($hasVc64 -and $hasVcx86) {
            return @{ Done = $true; Reason = "Visual C++ 2015-2022 (x86 i x64) biblioteke su već instalirane" }
        } else {
            return @{ Done = $false; Reason = "Nedostaju VC++ biblioteke (x64: $hasVc64, x86: $hasVcx86)" }
        }
    } -Action {
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

        if ($vcInstallers.Count -eq 0 -and -not (Get-Command Connect-ServisNasUpdatesShare -ErrorAction SilentlyContinue)) {
            $updModCand = @(
                (Join-Path $PSScriptRoot "WIS.Update.psm1"),
                (Join-Path $LocalSetupDir "Skripte\Modules\WIS.Update.psm1"),
                "C:\setup\Skripte\Modules\WIS.Update.psm1"
            ) | Where-Object { Test-Path $_ } | Select-Object -First 1
            if ($updModCand) {
                try { Import-Module $updModCand -Force -ErrorAction SilentlyContinue } catch {}
            }
        }

        if ($vcInstallers.Count -eq 0 -and (Get-Command Connect-ServisNasUpdatesShare -ErrorAction SilentlyContinue)) {
            $nasVcCtx = $null
            try {
                $nasVcCtx = Connect-ServisNasUpdatesShare -Subfolder "Programi\VCRedist"
                $nasVcCandidates = @()
                if ($nasVcCtx -and $nasVcCtx.Connected) {
                    if ($nasVcCtx.UpdatesDir -and (Test-Path $nasVcCtx.UpdatesDir -ErrorAction SilentlyContinue)) {
                        $nasVcCandidates += $nasVcCtx.UpdatesDir
                    }
                    if ($nasVcCtx.NasHost) {
                        $nasVcCandidates += "\\$($nasVcCtx.NasHost)\Programi\VCRedist"
                    }
                }
                $localVcCache = Join-Path $LocalSetupDir "Programi\VCRedist"
                foreach ($nasVcDir in ($nasVcCandidates | Select-Object -Unique)) {
                    if (-not (Test-Path $nasVcDir -ErrorAction SilentlyContinue)) { continue }
                    $nasExes = Get-ChildItem -Path $nasVcDir -Filter "*.exe" -Recurse -File -ErrorAction SilentlyContinue | Where-Object {
                        $_.Name -match '(?i)vcredist|vc_redist|visual_c'
                    }
                    if ($nasExes -and @($nasExes).Count -gt 0) {
                        if (-not (Test-Path $localVcCache)) {
                            New-Item -Path $localVcCache -ItemType Directory -Force -ErrorAction SilentlyContinue | Out-Null
                        }
                        foreach ($ne in $nasExes) {
                            $destVc = Join-Path $localVcCache $ne.Name
                            Copy-Item -Path $ne.FullName -Destination $destVc -Force -ErrorAction SilentlyContinue
                            if (Test-Path $destVc) {
                                $vcInstallers += (Get-Item -Path $destVc)
                            }
                        }
                        if ($vcInstallers.Count -gt 0) {
                            if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                                Write-ServisLog "P3.VcRedist: Pronađeno $($vcInstallers.Count) VC++ instalera na servisnom NAS-u ($nasVcDir) - preskačem winget." -Level "INFO" -Component "Branding"
                            }
                            break
                        }
                    }
                }
            } catch {} finally {
                if ($nasVcCtx -and $nasVcCtx.UncShare -and (Get-Command Disconnect-ServisNasUpdatesShare -ErrorAction SilentlyContinue)) {
                    try { Disconnect-ServisNasUpdatesShare -UncShare $nasVcCtx.UncShare } catch {}
                }
            }
        }

        if ($vcInstallers.Count -gt 0) {
            foreach ($inst in $vcInstallers) {
                try {
                    $p = Start-Process -FilePath $inst.FullName -ArgumentList "/install /quiet /norestart /q" -PassThru -WindowStyle Hidden
                    if ($p) {
                        $done = $p.WaitForExit(180000)
                        if (-not $done -or (-not $p.HasExited)) {
                            & taskkill.exe /PID $p.Id /T /F >$null 2>&1
                            if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                                Write-ServisLog "P3.VcRedist: Instaler $($inst.Name) prekoracio timeout (180s) i zaustavljen." -Level "WARN" -Component "Branding"
                            }
                        }
                    }
                } catch {}
            }
        } else {
            foreach ($wgId in @("Microsoft.VCRedist.2015+.x64", "Microsoft.VCRedist.2015+.x86")) {
                try {
                    $wp = Start-Process -FilePath "winget.exe" -ArgumentList "install --id $wgId --silent --accept-package-agreements --accept-source-agreements" -PassThru -WindowStyle Hidden -ErrorAction SilentlyContinue
                    if ($wp) {
                        $wgDone = $wp.WaitForExit(120000)
                        if (-not $wgDone -or (-not $wp.HasExited)) {
                            & taskkill.exe /PID $wp.Id /T /F >$null 2>&1
                            if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                                Write-ServisLog "P3.VcRedist: Winget za $wgId prekoracio timeout (120s) i prekinut (/T /F)." -Level "WARN" -Component "Branding"
                            }
                        }
                    }
                } catch {}
            }
        }
    }

    # =========================================================================
    # 6. OSLOBAĐANJE USB MEDIJUMA - P3.UsbFree (-NoSkip)
    # =========================================================================
    $null = Invoke-WisStep -Phase 3 -StepId "P3.UsbFree" -Title "Signal za oslobađanje USB medijuma" -NoSkip -Test {
        return @{ Done = $true; Reason = "Signal USB slobodan" }
    } -Action {
        if (Get-Command Send-IpcProgress -ErrorAction SilentlyContinue) {
            Send-IpcProgress -Progress 90 -Phase 3 -PhaseName "Optimizacija" -SubPhase "USB Free" -StatusMessage "Optimizacije završene. USB fleš disk je slobodan za uklanjanje."
        }
        if (Get-Command Send-IpcStatus -ErrorAction SilentlyContinue) {
            Send-IpcStatus -Phase 3 -Progress 95 -SubPhase "Spremno za programe" -StatusMessage "Sistem optimizovan i brendiran." -UsbFree
        }
    }

    # =========================================================================
    # 7. SNIMANJE STANJA ZA MEĐURESTART (ODLUKA 6)
    # =========================================================================
    # Na kraju Faze 3 snimamo Get-WisPendingRebootState u žurnal pre nego što Faza 4
    # eventualno pozove Remove-PendingRebootLocks (koja briše iste ključeve).
    try {
        $pendingState = Get-WisPendingRebootState
        $summaryP2 = Get-WisPhaseStepSummary -Phase 2
        $summaryP3 = Get-WisPhaseStepSummary -Phase 3

        $rebootNeeded = ($summaryP2.executed_any -or $summaryP3.executed_any -or $pendingState.Pending)
        $null = Write-WisStepJournal -InterimRebootRequired $rebootNeeded

        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
            Write-ServisLog "Faza 3 rezime za međurestart: P2.executed_any=$($summaryP2.executed_any), P3.executed_any=$($summaryP3.executed_any), pending_reboot=$($pendingState.Pending) -> InterimRebootRequired=$rebootNeeded" -Level "INFO" -Component "Lifecycle"
        }
    } catch {
        # U slucaju greske pri citanju zurnala, postavi interim_reboot_required na $true radi opreza
        $null = Write-WisStepJournal -InterimRebootRequired $true
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
    Invoke-Phase3Optimization, `
    Disable-LocationPrivacyPrompts, `
    Test-WisLocationAccessEnabled
