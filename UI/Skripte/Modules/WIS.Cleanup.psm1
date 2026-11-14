<#
.MODULE
    WIS.Cleanup.psm1
.DESCRIPTION
    Zavrsna verifikacija, generisanje dijagnostickih izvestaja, ciscenje C:\setup i kontrolisani restart za WIS (Windows Installation Assistant).
.COMPATIBILITY
    Windows PowerShell 5.1+
#>

function Export-ServisSummaryReport {
    param(
        $VerificationResults,
        [string]$LocalSetupDir = "C:\setup",
        [string]$SystemLogDir = "C:\Windows\Logs\WIS",
        [string]$SourceRoot = ""
    )
    try {
        $compName = if ($env:COMPUTERNAME) { $env:COMPUTERNAME } else { "KLIJENT" }
        $user = if ($env:USERNAME) { $env:USERNAME } else { "Korisnik" }
        $ts = (Get-Date).ToString("yyyy-MM-dd HH:mm:ss")
        $hw = if (Get-Command Get-HardwareDiagnosticsInfo -ErrorAction SilentlyContinue) {
            Get-HardwareDiagnosticsInfo
        } else { $Global:HwInfo }

        $sb = [System.Text.StringBuilder]::new()
        $sb.AppendLine("================================================================================") | Out-Null
        $sb.AppendLine("              WIS SERVICE AUTO-DEPLOY TOOLKIT | ZAVRSNI IZVESTAJ                ") | Out-Null
        $sb.AppendLine("================================================================================") | Out-Null
        $sb.AppendLine("Datum i vreme provere : $ts") | Out-Null
        $sb.AppendLine("Racunar / Hostname    : $compName") | Out-Null
        $sb.AppendLine("Korisnicki nalog      : $user") | Out-Null
        if ($hw) {
            $sb.AppendLine("Proizvodjac i Model   : $($hw.Manufacturer) $($hw.Model)") | Out-Null
            $sb.AppendLine("Serijski broj         : $($hw.SerialNumber)") | Out-Null
            $sb.AppendLine("Procesor (CPU)        : $($hw.CpuName) ($($hw.CpuCores) jezgra)") | Out-Null
            $sb.AppendLine("RAM Memorija          : $($hw.RamGB) GB ($($hw.RamType) $($hw.RamSpeed))") | Out-Null
            $sb.AppendLine("Glavni Disk           : $($hw.DiskModel) ($($hw.DiskSizeGB) GB, $($hw.DiskType))") | Out-Null
            if ($hw.DisplayStr) {
                $sb.AppendLine("Ekran / Panel         : $($hw.DisplayStr)") | Out-Null
            }
            if ($hw.SpeakersName) {
                $sb.AppendLine("Zvucnici              : $($hw.SpeakersName)") | Out-Null
            }
            if ($hw.MicName) {
                $sb.AppendLine("Mikrofon              : $($hw.MicName)") | Out-Null
            }
            if ($hw.HasFingerprint) {
                $sb.AppendLine("Otisak prsta          : $($hw.FingerprintName)") | Out-Null
            }
            if ($hw.HasIrCamera) {
                $sb.AppendLine("IR Kamera             : $($hw.IrCameraName)") | Out-Null
            }
            if ($hw.BacklitKbStatus) {
                $sb.AppendLine("Svetleca tastatura    : $($hw.BacklitKbStatus)") | Out-Null
            }
        }
        $isLaptop = $VerificationResults -and $VerificationResults.BatteryBar -and $VerificationResults.BatteryBar.IsLaptop
        $sb.AppendLine("Baterija              : $(if ($isLaptop) { 'Detektovana (Laptop)' } else { 'Nema baterije (Desktop / VM)' })") | Out-Null
        $sb.AppendLine("--------------------------------------------------------------------------------") | Out-Null
        $sb.AppendLine("                     STATUS INSTALIRANIH APLIKACIJA                             ") | Out-Null
        $sb.AppendLine("--------------------------------------------------------------------------------") | Out-Null
        if ($VerificationResults) {
            $sb.AppendLine("Google Chrome         : $(if ($VerificationResults.Chrome.Installed) { '[OK] ' } else { '[NEDOSTAJE] ' })$($VerificationResults.Chrome.Message)") | Out-Null
            $sb.AppendLine("WinRAR x64            : $(if ($VerificationResults.WinRAR.Installed) { '[OK] ' } else { '[NEDOSTAJE] ' })$($VerificationResults.WinRAR.Message)") | Out-Null
            $sb.AppendLine("Adobe Acrobat Pro     : $(if ($VerificationResults.Acrobat.Installed) { '[OK] ' } else { '[NEDOSTAJE] ' })$($VerificationResults.Acrobat.Message)") | Out-Null
            $sb.AppendLine("Microsoft Office      : $(if ($VerificationResults.Office.Installed) { '[OK] ' } else { '[NEDOSTAJE] ' })$($VerificationResults.Office.Message)") | Out-Null
            $sb.AppendLine("Microsoft .NET        : $(if ($VerificationResults.DotNet.Installed) { '[OK] ' } else { '[NEDOSTAJE] ' })$($VerificationResults.DotNet.Message)") | Out-Null
            $sb.AppendLine("BatteryBar            : $(if ($VerificationResults.BatteryBar.Installed) { '[OK] ' } else { '[NEDOSTAJE] ' })$($VerificationResults.BatteryBar.Message)") | Out-Null
            $sb.AppendLine("--------------------------------------------------------------------------------") | Out-Null
            $sb.AppendLine("                     STATUS SISTEMSKIH LICENCI                                  ") | Out-Null
            $sb.AppendLine("--------------------------------------------------------------------------------") | Out-Null
            $sb.AppendLine("Windows Licenca       : $(if ($VerificationResults.WindowsActivation.Activated) { '[OK] Trajno aktiviran' } else { '[!] NIJE AKTIVIRAN' })") | Out-Null
            $sb.AppendLine("Office Licenca        : $(if ($VerificationResults.OfficeActivation.Activated) { '[OK] Trajno aktiviran' } else { '[!] NIJE AKTIVIRAN' })") | Out-Null
            $sb.AppendLine("--------------------------------------------------------------------------------") | Out-Null
            $sb.AppendLine("UKUPNI STATUS         : $(if ($VerificationResults.AllRequiredInstalled) { 'USPESNO - SVI PROGRAMI SU INSTALIRANI' } else { 'PAZNJA - NEDOSTAJU PROGRAMI: ' + ($VerificationResults.MissingList -join ', ') })") | Out-Null
        }
        $sb.AppendLine("--------------------------------------------------------------------------------") | Out-Null
        $sb.AppendLine("Detaljni logovi su sacuvani na:") | Out-Null
        $sb.AppendLine("1. C:\Windows\Logs\WIS\servis_deploy.log (trajni sistemski log)") | Out-Null
        $sb.AppendLine("2. C:\Windows\Logs\WIS\servis_deploy.jsonl (strukturirana telemetrija)") | Out-Null
        $sb.AppendLine("3. C:\Users\Public\Desktop\WIS_Izvestaj.log") | Out-Null
        if ($SourceRoot) {
            $sb.AppendLine("4. $SourceRoot\Logs (USB fleš arhiva)") | Out-Null
        }
        $sb.AppendLine("================================================================================") | Out-Null

        $reportContent = $sb.ToString()

        # Generisanje Markdown Servisnog Kartona (servisni_karton.md)
        $mdSb = [System.Text.StringBuilder]::new()
        $mdSb.AppendLine("# [KARTON] WIS SERVICE | SERVISNI KARTON RAČUNARA") | Out-Null
        $mdSb.AppendLine("") | Out-Null
        $mdSb.AppendLine("- **Datum i vreme provere:** $ts") | Out-Null
        $mdSb.AppendLine("- **Hostname (Računar):** $compName") | Out-Null
        $mdSb.AppendLine("- **Korisnički nalog:** $user") | Out-Null
        if ($hw) {
            $mdSb.AppendLine("") | Out-Null
            $mdSb.AppendLine("### [HARDVER] Hardverski Profil") | Out-Null
            $mdSb.AppendLine("- **Proizvođač i Model:** $($hw.Manufacturer) $($hw.Model)") | Out-Null
            $mdSb.AppendLine("- **Serijski broj:** $($hw.SerialNumber)") | Out-Null
            $mdSb.AppendLine("- **Procesor (CPU):** $($hw.CpuName) ($($hw.CpuCores) jezgra)") | Out-Null
            $mdSb.AppendLine("- **RAM Memorija:** $($hw.RamGB) GB ($($hw.RamType) $($hw.RamSpeed))") | Out-Null
            $mdSb.AppendLine("- **Glavni Disk:** $($hw.DiskModel) ($($hw.DiskSizeGB) GB, $($hw.DiskType))") | Out-Null
            if ($hw.DisplayStr) {
                $mdSb.AppendLine("- **Ekran / Panel:** $($hw.DisplayStr)") | Out-Null
            }
            if ($hw.SpeakersName) {
                $mdSb.AppendLine("- **Zvučnici:** $($hw.SpeakersName)") | Out-Null
            }
            if ($hw.MicName) {
                $mdSb.AppendLine("- **Mikrofon:** $($hw.MicName)") | Out-Null
            }
            if ($hw.HasFingerprint) {
                $mdSb.AppendLine("- **Čitač otiska prsta:** $($hw.FingerprintName)") | Out-Null
            }
            if ($hw.HasIrCamera) {
                $mdSb.AppendLine("- **IR Kamera:** $($hw.IrCameraName)") | Out-Null
            }
            if ($hw.BacklitKbStatus) {
                $mdSb.AppendLine("- **Svetleća tastatura:** $($hw.BacklitKbStatus)") | Out-Null
            }
            $mdSb.AppendLine("- **Baterija:** $(if ($isLaptop) { 'Detektovana (Laptop)' } else { 'Nema baterije (Desktop / VM)' })") | Out-Null
        }
        $mdSb.AppendLine("") | Out-Null
        $mdSb.AppendLine("### [PROGRAMI] Status Instaliranih Aplikacija") | Out-Null
        if ($VerificationResults) {
            $mdSb.AppendLine("- **Google Chrome:** $(if ($VerificationResults.Chrome.Installed) { '[OK] Instaliran' } else { '[NEDOSTAJE] Nedostaje' }) ($($VerificationResults.Chrome.Message))") | Out-Null
            $mdSb.AppendLine("- **WinRAR x64:** $(if ($VerificationResults.WinRAR.Installed) { '[OK] Instaliran' } else { '[NEDOSTAJE] Nedostaje' }) ($($VerificationResults.WinRAR.Message))") | Out-Null
            $mdSb.AppendLine("- **Adobe Acrobat:** $(if ($VerificationResults.Acrobat.Installed) { '[OK] Instaliran' } else { '[NEDOSTAJE] Nedostaje' }) ($($VerificationResults.Acrobat.Message))") | Out-Null
            $mdSb.AppendLine("- **Microsoft Office:** $(if ($VerificationResults.Office.Installed) { '[OK] Instaliran' } else { '[NEDOSTAJE] Nedostaje' }) ($($VerificationResults.Office.Message))") | Out-Null
            $mdSb.AppendLine("- **Microsoft .NET:** $(if ($VerificationResults.DotNet.Installed) { '[OK] Instaliran' } else { '[NEDOSTAJE] Nedostaje' }) ($($VerificationResults.DotNet.Message))") | Out-Null
            $mdSb.AppendLine("- **BatteryBar:** $(if ($VerificationResults.BatteryBar.Installed) { '[OK] Instaliran' } else { '[NEDOSTAJE] Nedostaje' }) ($($VerificationResults.BatteryBar.Message))") | Out-Null
            $mdSb.AppendLine("") | Out-Null
            $mdSb.AppendLine("### [LICENCE] Status Licenci") | Out-Null
            $mdSb.AppendLine("- **Windows Licenca:** $(if ($VerificationResults.WindowsActivation.Activated) { '[OK] Trajno aktiviran' } else { '[UPOZORENJE] Nije aktiviran' })") | Out-Null
            $mdSb.AppendLine("- **Office Licenca:** $(if ($VerificationResults.OfficeActivation.Activated) { '[OK] Trajno aktiviran' } else { '[UPOZORENJE] Nije aktiviran' })") | Out-Null
            $mdSb.AppendLine("") | Out-Null
            $mdSb.AppendLine("### [REZULTAT] Rezultat Servisa") | Out-Null
            $overallTxt = if ($VerificationResults.AllRequiredInstalled) { 'USPEŠNO - SVI PROGRAMI SU INSTALIRANI' } else { 'PAŽNJA - NEDOSTAJU: ' + ($VerificationResults.MissingList -join ', ') }
            $mdSb.AppendLine("- **Status:** $overallTxt") | Out-Null
        } else {
            $mdSb.AppendLine("- **Status:** SERVIS ZAVRŠEN! Sistem je verifikovan i spreman za pakovanje.") | Out-Null
        }
        $mdKartonContent = $mdSb.ToString()

        # Generisanje JSON Servisnog Kartona (servisni_karton.json)
        $kartonMap = [ordered]@{
            device_id     = if ($Global:FleetDeviceId) { $Global:FleetDeviceId } else { "TK-$compName" }
            hostname      = $compName
            timestamp     = (Get-Date).ToString("o")
            user          = $user
            status        = if ($VerificationResults -and (-not $VerificationResults.AllRequiredInstalled)) { "WARN" } else { "COMPLETED" }
            hardware      = [ordered]@{
                manufacturer     = if ($hw) { $hw.Manufacturer } else { "UNKNOWN" }
                model            = if ($hw) { $hw.Model } else { "UNKNOWN" }
                serial           = if ($hw) { $hw.SerialNumber } else { "UNKNOWN" }
                cpu              = if ($hw) { $hw.CpuName } else { "" }
                ram_gb           = if ($hw) { $hw.RamGB } else { 0 }
                disk             = if ($hw) { $hw.DiskModel } else { "" }
                display_model    = if ($hw -and $hw.DisplayModel) { $hw.DisplayModel } else { "" }
                display_nits     = if ($hw -and $hw.DisplayNits) { $hw.DisplayNits } else { 0 }
                display_gamut    = if ($hw -and $hw.DisplayGamut) { $hw.DisplayGamut } else { "" }
                speakers         = if ($hw -and $hw.SpeakersName) { $hw.SpeakersName } else { "" }
                microphone       = if ($hw -and $hw.MicName) { $hw.MicName } else { "" }
                fingerprint      = if ($hw -and $hw.HasFingerprint) { $true } else { $false }
                ir_camera        = if ($hw -and $hw.HasIrCamera) { $true } else { $false }
                backlit_keyboard = if ($hw -and $hw.BacklitKbStatus) { $hw.BacklitKbStatus } else { "" }
                battery          = if ($isLaptop) { "Laptop" } else { "Desktop" }
            }
            apps          = [ordered]@{
                chrome     = if ($VerificationResults -and $VerificationResults.Chrome) { $VerificationResults.Chrome.Installed } else { $false }
                winrar     = if ($VerificationResults -and $VerificationResults.WinRAR) { $VerificationResults.WinRAR.Installed } else { $false }
                acrobat    = if ($VerificationResults -and $VerificationResults.Acrobat) { $VerificationResults.Acrobat.Installed } else { $false }
                office     = if ($VerificationResults -and $VerificationResults.Office) { $VerificationResults.Office.Installed } else { $false }
                dotnet     = if ($VerificationResults -and $VerificationResults.DotNet) { $VerificationResults.DotNet.Installed } else { $false }
                batterybar = if ($VerificationResults -and $VerificationResults.BatteryBar) { $VerificationResults.BatteryBar.Installed } else { $false }
            }
            licenses      = [ordered]@{
                windows = if ($VerificationResults -and $VerificationResults.WindowsActivation) { $VerificationResults.WindowsActivation.Activated } else { $false }
                office  = if ($VerificationResults -and $VerificationResults.OfficeActivation) { $VerificationResults.OfficeActivation.Activated } else { $false }
            }
            status_message = "SERVIS ZAVRŠEN! Sistem je verifikovan i spreman za pakovanje."
        }
        $jsonKartonContent = ConvertTo-Json -InputObject $kartonMap -Depth 5

        $publicDesktop = "C:\Users\Public\Desktop"
        if (Test-Path $publicDesktop) {
            [System.IO.File]::WriteAllText((Join-Path $publicDesktop "WIS_Status_Instalacije.txt"), $reportContent, [System.Text.Encoding]::UTF8)
        }

        # Upis u C:\setup
        if (Test-Path $LocalSetupDir) {
            [System.IO.File]::WriteAllText((Join-Path $LocalSetupDir "WIS_Status_Instalacije.txt"), $reportContent, [System.Text.Encoding]::UTF8)
            [System.IO.File]::WriteAllText((Join-Path $LocalSetupDir "servisni_karton.md"), $mdKartonContent, [System.Text.Encoding]::UTF8)
            [System.IO.File]::WriteAllText((Join-Path $LocalSetupDir "servisni_karton.json"), $jsonKartonContent, [System.Text.Encoding]::UTF8)
        }

        # Upis u sistemski log direktorijum C:\Windows\Logs\WIS
        if (Test-Path $SystemLogDir) {
            [System.IO.File]::WriteAllText((Join-Path $SystemLogDir "WIS_Status_Instalacije.txt"), $reportContent, [System.Text.Encoding]::UTF8)
            [System.IO.File]::WriteAllText((Join-Path $SystemLogDir "servisni_karton.md"), $mdKartonContent, [System.Text.Encoding]::UTF8)
            [System.IO.File]::WriteAllText((Join-Path $SystemLogDir "servisni_karton.json"), $jsonKartonContent, [System.Text.Encoding]::UTF8)
        }

        if ($SourceRoot -and (Test-Path $SourceRoot)) {
            $usbLogs = Join-Path $SourceRoot "Logs"
            if (-not (Test-Path $usbLogs)) { New-Item -Path $usbLogs -ItemType Directory -Force | Out-Null }
            $dateTag = (Get-Date).ToString("yyyyMMdd")
            [System.IO.File]::WriteAllText((Join-Path $usbLogs "WIS_Status_${compName}_${dateTag}.txt"), $reportContent, [System.Text.Encoding]::UTF8)
            [System.IO.File]::WriteAllText((Join-Path $usbLogs "servisni_karton_${compName}_${dateTag}.md"), $mdKartonContent, [System.Text.Encoding]::UTF8)
            [System.IO.File]::WriteAllText((Join-Path $usbLogs "servisni_karton_${compName}_${dateTag}.json"), $jsonKartonContent, [System.Text.Encoding]::UTF8)
        }
        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
            Write-ServisLog "Uspesno kreiran i eksportovan servisni statusni izvestaj i servisni karton (JSON/MD)." -Level "SUCCESS" -Component "Report"
        }
    } catch {
        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
            Write-ServisLog "Greska pri kreiranju statusnog izvestaja: $($_.Exception.Message)" -Level "WARN" -Component "Report"
        }
    }
}

function Refresh-ExplorerShell {
    try {
        if (-not ([System.Management.Automation.PSTypeName]'ShellNotifier').Type) {
            $code = @'
using System;
using System.Runtime.InteropServices;
public class ShellNotifier {
    [DllImport("shell32.dll")]
    public static extern void SHChangeNotify(int wEventId, int uFlags, IntPtr dwItem1, IntPtr dwItem2);
    public static void Refresh() {
        SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero); // SHCNE_ASSOCCHANGED
        SHChangeNotify(0x00001000, 0, IntPtr.Zero, IntPtr.Zero); // SHCNE_UPDATEDIR
    }
}
'@
            Add-Type -TypeDefinition $code -ErrorAction SilentlyContinue
        }
        [ShellNotifier]::Refresh()
        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
            Write-ServisLog "Explorer Shell osvezen (SHChangeNotify uspesno pozvan)." -Level "DEBUG" -Component "Cleanup"
        }
    } catch {}
}

function Clear-ServisDesktopShortcuts {
    <#
    .SYNOPSIS
        Agresivno uklanja sve privremene servisne prečice i skripte sa svih radnih površina.
    .DESCRIPTION
        Skenira C:\Users\Public\Desktop i sve korisničke profile (C:\Users\*\Desktop) i briše:
        *Launcher*.lnk, *WIS*.lnk, *Pokreni*.bat, *Pokreni*.lnk, setup.lnk,
        kao i servisne dnevnike instalacije. Nakon toga osvežava Explorer shell.
    #>
    [CmdletBinding()]
    param()

    $desktopLocations = [System.Collections.Generic.List[string]]::new()
    
    $publicDesk = "C:\Users\Public\Desktop"
    if (Test-Path $publicDesk) { $desktopLocations.Add($publicDesk) }
    
    $curDesk = [Environment]::GetFolderPath('Desktop')
    if ($curDesk -and (Test-Path $curDesk) -and (-not $desktopLocations.Contains($curDesk))) {
        $desktopLocations.Add($curDesk)
    }

    if (Test-Path "C:\Users") {
        $userDirs = Get-ChildItem -Path "C:\Users" -Directory -ErrorAction SilentlyContinue
        foreach ($ud in $userDirs) {
            $uDesk = Join-Path $ud.FullName "Desktop"
            if ((Test-Path $uDesk) -and (-not $desktopLocations.Contains($uDesk))) {
                $desktopLocations.Add($uDesk)
            }
        }
    }

    $patternsToRemove = @(
        "*Launcher*.lnk",
        "*WIS*.lnk",
        "*Pokreni*.bat",
        "*Pokreni*.lnk",
        "setup.lnk",
        "WIS_Izvestaj.log",
        "WIS_Status_Instalacije.txt",
        "servis_deploy.log",
        "WIS_*.log",
        "WIS_*.txt"
    )

    $deletedCount = 0
    foreach ($dl in $desktopLocations) {
        if ($dl -and (Test-Path $dl)) {
            foreach ($pat in $patternsToRemove) {
                $items = Get-ChildItem -Path $dl -Filter $pat -Force -ErrorAction SilentlyContinue
                if ($items) {
                    foreach ($it in $items) {
                        try {
                            Remove-Item -Path $it.FullName -Force -ErrorAction SilentlyContinue
                            $deletedCount++
                        } catch {}
                    }
                }
            }
        }
    }

    if (Get-Command Refresh-ExplorerShell -ErrorAction SilentlyContinue) {
        Refresh-ExplorerShell
    }

    if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
        Write-ServisLog "Uklonjeno $deletedCount privremenih stavki sa svih radnih povrsina. Explorer shell osvezen." -Level "SUCCESS" -Component "Cleanup"
    }

    return $deletedCount
}

function Invoke-ServisCleanupAndFinalize {
    param(
        [string]$LocalSetupDir = "C:\setup",
        [string]$SystemLogDir = "C:\Windows\Logs\WIS",
        [string]$PublicDesktopLog = "C:\Users\Public\Desktop\WIS_Izvestaj.log",
        [string]$SourceRoot = "",
        [switch]$Worker = $false
    )

    $isWorker = ($Global:Worker -or $Worker -or $Global:WIS_UI_Backend -eq "Pipeline")

    Remove-ItemProperty -Path "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce" -Name "WIS_Resume" -ErrorAction SilentlyContinue
    Remove-ItemProperty -Path "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce" -Name "ServisDeployResume" -ErrorAction SilentlyContinue
    Remove-ItemProperty -Path "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce" -Name "WIS_AutoStart" -ErrorAction SilentlyContinue

    $startupDir = [Environment]::GetFolderPath('CommonStartup')
    if (-not $startupDir) { $startupDir = 'C:\ProgramData\Microsoft\Windows\Start Menu\Programs\Startup' }
    $resumeBat = Join-Path $startupDir 'WIS-Resume.cmd'
    if (Test-Path $resumeBat) { Remove-Item -Path $resumeBat -Force -ErrorAction SilentlyContinue }
    $autoStartCmd = Join-Path $startupDir 'WIS-AutoStart.cmd'
    if (Test-Path $autoStartCmd) { Remove-Item -Path $autoStartCmd -Force -ErrorAction SilentlyContinue }
    if (Test-Path 'C:\ProgramData\Microsoft\Windows\Start Menu\Programs\Startup\WIS-AutoStart.cmd') {
        Remove-Item -Path 'C:\ProgramData\Microsoft\Windows\Start Menu\Programs\Startup\WIS-AutoStart.cmd' -Force -ErrorAction SilentlyContinue
    }

    if (Get-Command Create-ServisShortcuts -ErrorAction SilentlyContinue) { Create-ServisShortcuts }
    if (Get-Command Refresh-ExplorerShell -ErrorAction SilentlyContinue) { Refresh-ExplorerShell }

    # PETLJA ZAVRŠNE VERIFIKACIJE I ODLUČIVANJA
    while ($true) {
        $verResults = if (Get-Command Test-AllServisProgramsInstalled -ErrorAction SilentlyContinue) {
            Test-AllServisProgramsInstalled
        } else { $null }

        Export-ServisSummaryReport -VerificationResults $verResults -LocalSetupDir $LocalSetupDir -SystemLogDir $SystemLogDir -SourceRoot $SourceRoot

        try {
            $compName = if ($env:COMPUTERNAME) { $env:COMPUTERNAME } else { "KLIJENT" }
            $dateTag = (Get-Date).ToString("yyyyMMdd")
            if (-not (Test-Path $SystemLogDir)) { New-Item -Path $SystemLogDir -ItemType Directory -Force | Out-Null }
            $usbLogsDir = if ($SourceRoot -and (Test-Path $SourceRoot)) {
                $uDir = Join-Path $SourceRoot "Logs"
                if (-not (Test-Path $uDir)) { New-Item -Path $uDir -ItemType Directory -Force | Out-Null }
                $uDir
            } else { $null }

            $logFilesToSync = @("servis_deploy.log", "servis_deploy.jsonl", "setupcomplete.log", "firstlogon.log", "acrobat_install.log")
            foreach ($lf in $logFilesToSync) {
                $srcF = Join-Path $LocalSetupDir $lf
                if (Test-Path $srcF) {
                    Copy-Item -Path $srcF -Destination (Join-Path $SystemLogDir $lf) -Force -ErrorAction SilentlyContinue
                    if ($usbLogsDir) {
                        $ext = [System.IO.Path]::GetExtension($lf)
                        $base = [System.IO.Path]::GetFileNameWithoutExtension($lf)
                        Copy-Item -Path $srcF -Destination (Join-Path $usbLogsDir "${base}_${compName}_${dateTag}${ext}") -Force -ErrorAction SilentlyContinue
                    }
                }
            }
            if (Test-Path (Join-Path $LocalSetupDir "servis_deploy.log")) {
                Copy-Item -Path (Join-Path $LocalSetupDir "servis_deploy.log") -Destination $PublicDesktopLog -Force -ErrorAction SilentlyContinue
            }
        } catch {}

        $dashLogs = @()
        if ($verResults) {
            $dashLogs = @(
                "1. Google Chrome     : $($verResults.Chrome.Message)",
                "2. WinRAR x64         : $($verResults.WinRAR.Message)",
                "3. Adobe Acrobat Pro : $($verResults.Acrobat.Message)",
                "4. Microsoft Office  : $($verResults.Office.Message)",
                "5. Microsoft .NET    : $($verResults.DotNet.Message)",
                "6. BatteryBar        : $($verResults.BatteryBar.Message)",
                "7. Windows Licenca   : $(if ($verResults.WindowsActivation.Activated) { '[OK] Trajno aktiviran' } else { '[!] Nije aktiviran' })",
                "8. Office Licenca    : $(if ($verResults.OfficeActivation.Activated) { '[OK] Trajno aktiviran' } else { '[!] Nije aktiviran' })"
            )
        }

        if ($verResults -and $verResults.AllRequiredInstalled) {
            if ($isWorker) {
                Write-Host "[WORKER] Svi programi instalirani. Automatski nastavljam sa ciscenjem (R)..."
                break
            }

            if (Get-Command Show-ServisDashboard -ErrorAction SilentlyContinue) {
                Show-ServisDashboard -LiveStatus "[100% USPESNO] SVE APLIKACIJE SU INSTALIRANE I VERIFIKOVANE!" -SubLogs $dashLogs
            }

            $cW = if (Get-Command Get-ServisConsoleWidth -ErrorAction SilentlyContinue) { Get-ServisConsoleWidth } else { 120 }
            $boxBar = "=" * [math]::Max(60, $cW - 6)
            Write-Host ""
            Write-Host "   $boxBar" -ForegroundColor Green
            Write-Host "      [OK] SVI PROGRAMI SU 100% INSTALIRANI I PROVERENI NA SISTEMU!" -ForegroundColor Green
            Write-Host "   $boxBar" -ForegroundColor Green
            Write-Host "      [R] RESTARTUJ RACUNAR  - Brise privremeni C:\setup i bezbedno restartuje racunar" -ForegroundColor White
            Write-Host "      [P] PREGLEDAJ SISTEM   - Ostanak na radnoj povrsini (fajlovi se ne brisu)" -ForegroundColor Gray
            Write-Host "      [L] OTVORI LOG         - Otvara dijagnosticki izvestaj u Notepadu" -ForegroundColor Cyan
            Write-Host "   $boxBar" -ForegroundColor Green
            Write-Host ""

            $inputKey = Read-Host "   Unesite opciju [R za Restart / P za Pregled / L za Log] (Podrazumevano: R)"
            $choice = if ([string]::IsNullOrWhiteSpace($inputKey)) { "R" } else { $inputKey.Trim().ToUpper() }

            if ($choice -eq "R") {
                break
            } elseif ($choice -eq "L") {
                $repFile = "C:\Users\Public\Desktop\WIS_Status_Instalacije.txt"
                if (Test-Path $repFile) { Start-Process "notepad.exe" -ArgumentList "`"$repFile`"" }
                continue
            } elseif ($choice -eq "P" -or $choice -eq "S") {
                Write-Host ""
                Write-Host "   [INFO] Pregled sistema aktivan. Privremeni fajlovi na C:\setup NISU obrisani." -ForegroundColor Yellow
                Write-Host "   Kada zavrsite pregled, pritisnite bilo koji taster za brisanje i restart..." -ForegroundColor Cyan
                try {
                    $null = $Host.UI.RawUI.ReadKey("NoEcho,IncludeKeyDown")
                } catch {
                    Read-Host "   Pritisnite ENTER za brisanje i restart"
                }
                break
            } else {
                Write-Host "   [!] Nepoznata opcija." -ForegroundColor Red
                continue
            }
        } else {
            if ($isWorker) {
                Write-Host "[WORKER] Nedostaju pojedini programi. Automatski nastavljam sa ciscenjem i arhiviranjem..."
                break
            }

            $missingNames = if ($verResults) { $verResults.MissingList -join ", " } else { "Programi" }
            if (Get-Command Show-ServisDashboard -ErrorAction SilentlyContinue) {
                Show-ServisDashboard -LiveStatus "[PAZNJA] NEDOSTAJU POJEDINI PROGRAMI: $missingNames!" -SubLogs $dashLogs
            }

            $cW = if (Get-Command Get-ServisConsoleWidth -ErrorAction SilentlyContinue) { Get-ServisConsoleWidth } else { 120 }
            $boxBar = "=" * [math]::Max(60, $cW - 6)
            Write-Host ""
            Write-Host "   $boxBar" -ForegroundColor Red
            Write-Host "      [!] UPOZORENJE: SLEDECI PROGRAMI NISU INSTALIRANI NA SISTEMU:" -ForegroundColor Yellow
            if ($verResults) {
                foreach ($m in $verResults.MissingList) {
                    Write-Host "          - $m" -ForegroundColor Red
                }
            }
            Write-Host "   $boxBar" -ForegroundColor Red
            Write-Host "      [P] PONOVI INSTALACIJU (Retry)  - Pokrece ponovnu instalaciju nedostajucih aplikacija" -ForegroundColor White
            Write-Host "      [U] DETEKTUJ USB I PONOVI       - Ceka ubacivanje USB flesa radi dopune paketa" -ForegroundColor Yellow
            Write-Host "      [L] OTVORI LOG ZA DIJAGNOSTIKU  - Otvara detaljan log sa greskama u Notepadu" -ForegroundColor Cyan
            Write-Host "      [R] RESTARTUJ UPRKOS GRESKAMA   - Brisanje C:\setup i restart racunara" -ForegroundColor DarkGray
            Write-Host "      [O] OSTANI NA SISTEMU (Exit)    - Ostanak u konzoli bez brisanja fajlova" -ForegroundColor Magenta
            Write-Host "   $boxBar" -ForegroundColor Red
            Write-Host ""

            $inputKey = Read-Host "   Unesite opciju [P za Ponovi / U za USB / L za Log / R za Restart / O za Ostanak] (Podrazumevano: P)"
            $choice = if ([string]::IsNullOrWhiteSpace($inputKey)) { "P" } else { $inputKey.Trim().ToUpper() }

            if ($choice -eq "P") {
                Write-Host "   [RETRY] Pokrecem ponovnu instalaciju nedostajucih programa..." -ForegroundColor Yellow
                if (Get-Command Install-AllServisPrograms -ErrorAction SilentlyContinue) {
                    Install-AllServisPrograms -IsWorker:$false
                }
                continue
            } elseif ($choice -eq "U") {
                Write-Host "   [USB] Proveravam dostupnost USB fles diska..." -ForegroundColor Yellow
                if (Get-Command Ensure-ProgramiAvailable -ErrorAction SilentlyContinue) {
                    $null = Ensure-ProgramiAvailable -Interactive:$true
                }
                if (Get-Command Install-AllServisPrograms -ErrorAction SilentlyContinue) {
                    Install-AllServisPrograms -IsWorker:$false
                }
                continue
            } elseif ($choice -eq "L") {
                $logToShow = if (Test-Path $PublicDesktopLog) { $PublicDesktopLog } else { (Join-Path $LocalSetupDir "servis_deploy.log") }
                if (Test-Path $logToShow) {
                    Start-Process "notepad.exe" -ArgumentList "`"$logToShow`""
                }
                continue
            } elseif ($choice -eq "O") {
                Write-Host ""
                Write-Host "   [OSTANAK] Prekidam automatizaciju. C:\setup i logovi ostaju sacuvani." -ForegroundColor Green
                Write-Host "   Izvestaj mozete pogledati na Radnoj Povrsini: WIS_Status_Instalacije.txt" -ForegroundColor White
                Write-Host "   Dnevnik rada: $PublicDesktopLog" -ForegroundColor White
                exit 0
            } elseif ($choice -eq "R") {
                $confirm = Read-Host "   Da li ste SIGURNI da zelite restart i brisanje C:\setup iako programi nedostaju? [DA/NE]"
                if ($confirm.Trim().ToUpper() -eq "DA") {
                    if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                        Write-ServisLog "Korisnik je svesno potvrdio restart uprkos nedostajucim programima: $missingNames" -Level "WARN" -Component "Phase5"
                    }
                    break
                } else {
                    continue
                }
            } else {
                Write-Host "   [!] Nepoznata opcija. Molimo izaberite ponudjeno slovo." -ForegroundColor Red
                continue
            }
        }
    }

    if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
        Write-ServisLog "Korisnik potvrdio zavrsni restart. Zapocinjem arhiviranje logova i ciscenje..." -Level "INFO" -Component "Phase5"
    }
    if (Get-Command Show-ServisDashboard -ErrorAction SilentlyContinue) {
        Show-ServisDashboard -LiveStatus "Zavrsno ciscenje sistema (arhiviram logove i uklanjam C:\setup)..."
    }

    try {
        if (Get-Command Write-DiagnosticEvent -ErrorAction SilentlyContinue) {
            Write-DiagnosticEvent -Event "DEPLOY_COMPLETED" -Status "SUCCESS" -Component "Phase5" -Message "Sve faze deployment pipeline-a su uspesno zavrsene."
        }

        $compName = if ($env:COMPUTERNAME) { $env:COMPUTERNAME } else { "KLIJENT" }
        $dateTag = (Get-Date).ToString("yyyyMMdd")
        if (-not (Test-Path $SystemLogDir)) { New-Item -Path $SystemLogDir -ItemType Directory -Force | Out-Null }
        $usbLogsDir = if ($SourceRoot -and (Test-Path $SourceRoot)) {
            $uDir = Join-Path $SourceRoot "Logs"
            if (-not (Test-Path $uDir)) { New-Item -Path $uDir -ItemType Directory -Force | Out-Null }
            $uDir
        } else { $null }

        $logFilesToSync = @("servis_deploy.log", "servis_deploy.jsonl", "setupcomplete.log", "firstlogon.log", "WIS_Status_Instalacije.txt", "acrobat_install.log")
        foreach ($lf in $logFilesToSync) {
            $srcF = Join-Path $LocalSetupDir $lf
            if (-not (Test-Path $srcF) -and $lf -eq "WIS_Status_Instalacije.txt") {
                $srcF = Join-Path $SystemLogDir $lf
            }
            if (Test-Path $srcF) {
                Copy-Item -Path $srcF -Destination (Join-Path $SystemLogDir $lf) -Force -ErrorAction SilentlyContinue
                if ($usbLogsDir) {
                    $ext = [System.IO.Path]::GetExtension($lf)
                    $base = [System.IO.Path]::GetFileNameWithoutExtension($lf)
                    Copy-Item -Path $srcF -Destination (Join-Path $usbLogsDir "${base}_${compName}_${dateTag}${ext}") -Force -ErrorAction SilentlyContinue
                }
            }
        }
    } catch {}

    # Slanje finalnog flotnog izvestaja (Send-FleetFinalReport) pre bilo kakvog brisanja sa diska
    try {
        if (Get-Command Send-FleetFinalReport -ErrorAction SilentlyContinue) {
            $finalRepStatus = if ($verResults -and -not $verResults.AllRequiredInstalled) { "COMPLETED_WITH_WARNINGS" } else { "COMPLETED" }
            Send-FleetFinalReport -Status $finalRepStatus
            if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                Write-ServisLog "Zavrsni flotni izvestaj (FleetFinalReport) uspesno poslat pre ciscenja sistema." -Level "SUCCESS" -Component "Phase5"
            }
        }
    } catch {
        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
            Write-ServisLog "Upozorenje pri slanju zavrsnog flotnog izvestaja: $($_.Exception.Message)" -Level "WARN" -Component "Phase5"
        }
    }

    # Iskljucujemo Desktop log iz Global:LogPaths pre brisanja kako ga Write-ServisLog ne bi ponovo kreirao
    if ($Global:LogPaths) {
        $Global:LogPaths.Remove("C:\Users\Public\Desktop\WIS_Izvestaj.log") | Out-Null
        $Global:LogPaths.Remove($PublicDesktopLog) | Out-Null
    }

    if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
        Write-ServisLog "WIS SERVICE AUTO-DEPLOY TOOLKIT ZAPOCINJE ANTI-LEAK CISCENJE I VRACANJE UAC-A." -Level "SUCCESS" -Component "Phase5"
    }

    try {
        # -1. Postavljanje markera zavrsetka faze i uklanjanje fail-safe resume i autostart skripti
        Set-Content -Path (Join-Path $LocalSetupDir ".stage_completed") -Value (Get-Date).ToString("o") -Force -ErrorAction SilentlyContinue
        Remove-ItemProperty -Path "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce" -Name "WIS_AutoStart" -ErrorAction SilentlyContinue
        $startupDir = [Environment]::GetFolderPath('CommonStartup')
        if (-not $startupDir) { $startupDir = 'C:\ProgramData\Microsoft\Windows\Start Menu\Programs\Startup' }
        $resumeBat = Join-Path $startupDir 'WIS-Resume.cmd'
        if (Test-Path $resumeBat) { Remove-Item -Path $resumeBat -Force -ErrorAction SilentlyContinue }
        $autoStartCmd = Join-Path $startupDir 'WIS-AutoStart.cmd'
        if (Test-Path $autoStartCmd) { Remove-Item -Path $autoStartCmd -Force -ErrorAction SilentlyContinue }
        if (Test-Path 'C:\ProgramData\Microsoft\Windows\Start Menu\Programs\Startup\WIS-AutoStart.cmd') {
            Remove-Item -Path 'C:\ProgramData\Microsoft\Windows\Start Menu\Programs\Startup\WIS-AutoStart.cmd' -Force -ErrorAction SilentlyContinue
        }

        # 0. Raskidanje svih SMB sesija i mrežnih veza radi zaštite servisnih NAS kredencijala
        & net.exe use * /delete /y >$null 2>&1

        # 1. ANTI-LEAK: Brisanje servisnih Wi-Fi profila (netsh wlan delete profile) i datoteke Wifi-Servis.ini
        if (Get-Command Clear-ServisWifiSecrets -ErrorAction SilentlyContinue) {
            Clear-ServisWifiSecrets -LocalSetupDir $LocalSetupDir
        } else {
            & netsh.exe wlan delete profile name="Wifi-Servis" >$null 2>&1
            $iniF = Join-Path $LocalSetupDir "Wifi-Servis.ini"
            if (Test-Path $iniF) { Remove-Item -Path $iniF -Force -ErrorAction SilentlyContinue }
            if (Test-Path "C:\setup\Wifi-Servis.ini") { Remove-Item -Path "C:\setup\Wifi-Servis.ini" -Force -ErrorAction SilentlyContinue }
        }

        # 2. BEZBEDNOST: Vraćanje UAC nivoa u registru na fabričke Windows 11 bezbednosne vrednosti
        Set-ItemProperty -Path "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System" -Name "ConsentPromptBehaviorAdmin" -Value 5 -Force -ErrorAction SilentlyContinue
        Set-ItemProperty -Path "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System" -Name "PromptOnSecureDesktop" -Value 1 -Force -ErrorAction SilentlyContinue
        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
            Write-ServisLog "UAC nivoi u registru vraceni na fabricke vrednosti (ConsentPromptBehaviorAdmin=5, PromptOnSecureDesktop=1)." -Level "SUCCESS" -Component "Phase5"
        }

        # 3. Brisanje privremenih instalacionih fajlova iz C:\setup
        if (Test-Path (Join-Path $LocalSetupDir "Programi")) {
            Remove-Item -Path (Join-Path $LocalSetupDir "Programi") -Recurse -Force -ErrorAction SilentlyContinue
        }
        $stateF = Join-Path $LocalSetupDir "deploy-state.json"
        if (Test-Path $stateF) {
            Remove-Item -Path $stateF -Force -ErrorAction SilentlyContinue
        }

        # 4. Agresivno uklanjanje privremenih prečica i dnevnika sa svih radnih površina
        Clear-ServisDesktopShortcuts | Out-Null

        # 5. Čišćenje privremenih artefakata i samouništenje C:\setup\ foldera pre predaje računara kupcu (samo za samostalni standalone režim van radnika/Faze 6)
        if (-not $isWorker) {
            $selfDestructCmd = "/c timeout /t 3 /nobreak >nul & net use * /delete /y >nul 2>&1 & del /f /q `"C:\Users\Public\Desktop\WIS_*.*`" >nul 2>&1 & timeout /t 3 /nobreak >nul & rmdir /s /q `"$LocalSetupDir`""
            if ($LocalSetupDir -ne "C:\setup" -and (Test-Path "C:\setup")) {
                $selfDestructCmd += " & rmdir /s /q `"C:\setup`""
            }
            Start-Process cmd.exe -ArgumentList $selfDestructCmd -WindowStyle Hidden
        }
    } catch {}

    if (Get-Command Show-ServisDashboard -ErrorAction SilentlyContinue) {
        Show-ServisDashboard -LiveStatus "[OK] SIGURNOSNO CISCENJE ZAVRSENO! RESTARTUJEM RACUNAR..." -SubLogs @(
            "- Folder C:\setup i instalacioni fajlovi se uklanjaju (samounistenje pre predaje kupcu).",
            "- Servisni Wi-Fi profili i lozinke (Wifi-Servis) su trajno obrisani (Anti-Leak).",
            "- UAC nivo bezbednosti je vracen na fabricke vrednosti (ConsentPromptBehaviorAdmin=5, PromptOnSecureDesktop=1).",
            "- Izvestaj i statusni fajlovi su obrisani sa Radne Povrsine (cist Desktop).",
            "- Trajni logovi su sacuvani u C:\Windows\Logs\WIS i poslat je flotni izvestaj.",
            "- Racunar se restartuje za 5 sekundi radi primene svih izmena..."
        )
    }

    Start-Sleep -Seconds 2
    if ($isWorker) {
        if (Get-Command Update-ServisUI -ErrorAction SilentlyContinue) {
            Update-ServisUI -Phase 6 -SubPhase "Završeno" -StatusMessage "Sve operacije uspešno završene! Priprema za gašenje..." -ProgressPercent 100
        }
        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
            Write-ServisLog "WIS SERVICE AUTO-DEPLOY radnik je uspesno zavrsio ciscenje. Prepustam zavrsni ekran i gasenje Master Shell-u." -Level "SUCCESS" -Component "Phase6"
        }
        Start-Sleep -Seconds 1
        exit 0
    } else {
        try {
            shutdown.exe /r /t 5 /c "WIS SERVICE | Instalacija i ciscenje zavrseni. Restartujem racunar..." /f
            Restart-Computer -Force -ErrorAction SilentlyContinue
        } catch {
            shutdown.exe /r /t 3 /f
        }
    }
}

function Clear-ServisWifiSecrets {
    <#
    .SYNOPSIS
        Trajno uklanja servisne Wi-Fi profile i konfiguracione fajlove sa klijentskog sistema (Anti-Leak zastita tajni).
    .DESCRIPTION
        Paket A1 (Uslovni problem U1):
        - Brise iskljucivo servisne Wi-Fi profile (procitane iz Wifi-Servis.ini i Wifi-Servis*) iz Windows WLAN sistema,
          cuvajuci postojece korisnicke Wi-Fi profile.
        - Brise sve privremene credential fajlove (Wifi-Servis.ini, Wifi-Servis.xml, wifi_profile.xml, wlan_preoobe.xml)
          ISKLJUCIVO sa sistemskog diska (C:\ / $env:SystemDrive), uz strogu zastitu servisnog USB medijuma.
        - Vrsi eksplicitnu verifikaciju odsustva svih fajlova i servisnih WLAN profila nakon brisanja.
        - Upisuje anti_leak_verified = $true / $false u deploy-state.json i vraca bool rezultat.
    #>
    [CmdletBinding()]
    param(
        [string]$LocalSetupDir = "C:\setup"
    )

    $sysDrive = if ($env:SystemDrive) { $env:SystemDrive.TrimEnd('\') } else { "C:" }
    $sysRoot  = "$sysDrive\"

    $serviceSsids = [System.Collections.Generic.List[string]]::new()
    $serviceSsids.Add("Wifi-Servis")
    if ($Global:State -and $Global:State.WifiSSID) {
        $stSsid = "$($Global:State.WifiSSID)".Trim()
        if ($stSsid -and -not $serviceSsids.Contains($stSsid)) {
            $serviceSsids.Add($stSsid)
        }
    }

    # Pronalazenje konfiguracionog fajla Wifi-Servis.ini na klijentskom sistemu radi ekstrakcije servisnih SSID naziva pre brisanja
    $tempEnvDir = if ($env:TEMP) { $env:TEMP } else { "C:\Windows\Temp" }
    $wifiIniReadCandidates = @(
        (Join-Path $LocalSetupDir "Wifi-Servis.ini"),
        "C:\setup\Wifi-Servis.ini",
        (Join-Path $LocalSetupDir "Skripte\Wifi-Servis.ini"),
        "C:\setup\Skripte\Wifi-Servis.ini",
        "C:\Windows\Temp\Wifi-Servis.ini",
        (Join-Path $tempEnvDir "Wifi-Servis.ini")
    )

    foreach ($cand in $wifiIniReadCandidates) {
        if ($cand -and (Test-Path $cand)) {
            try {
                $iniContent = Get-Content -Path $cand -Raw -ErrorAction SilentlyContinue
                if ($iniContent -match '(?m)^\s*SSID\s*=\s*(.+)$') {
                    $rawSsids = $matches[1].Trim()
                    foreach ($s in ($rawSsids -split ',')) {
                        $cleanS = $s.Trim()
                        if ($cleanS -and -not $serviceSsids.Contains($cleanS)) {
                            $serviceSsids.Add($cleanS)
                        }
                    }
                }
            } catch {}
        }
    }

    # a) Brisanje iskljucivo servisnih Wi-Fi profila iz Windows WLAN sistema (cuvaju se korisnicki profili)
    foreach ($prof in $serviceSsids) {
        try {
            & netsh.exe wlan delete profile name="$prof" >$null 2>&1
        } catch {}
    }

    try {
        $wlanProfiles = & netsh.exe wlan show profiles 2>$null
        if ($wlanProfiles) {
            foreach ($wpLine in $wlanProfiles) {
                if ($wpLine -match ':\s*(.+)$') {
                    $wName = $matches[1].Trim()
                    if ($wName -and ($serviceSsids.Contains($wName) -or $wName -match '^Wifi-Servis')) {
                        & netsh.exe wlan delete profile name="$wName" >$null 2>&1
                    }
                }
            }
        }
    } catch {}
    finally {
        $global:LASTEXITCODE = 0
    }

    # b) Jedinstvena lista svih poznatih privremenih credential fajlova na klijentskom sistemu (C:\)
    $rawCandidateFiles = @(
        (Join-Path $LocalSetupDir "Wifi-Servis.ini"),
        "C:\setup\Wifi-Servis.ini",
        (Join-Path $LocalSetupDir "Wifi-Servis.xml"),
        "C:\setup\Wifi-Servis.xml",
        (Join-Path $LocalSetupDir "wifi_profile.xml"),
        "C:\setup\wifi_profile.xml",
        (Join-Path $LocalSetupDir "wlan_preoobe.xml"),
        "C:\setup\wlan_preoobe.xml",
        "C:\Windows\Temp\Wifi-Servis.ini",
        "C:\Windows\Temp\Wifi-Servis.xml",
        "C:\Windows\Temp\wifi_profile.xml",
        "C:\Windows\Temp\wlan_preoobe.xml",
        (Join-Path $env:TEMP "Wifi-Servis.ini"),
        (Join-Path $env:TEMP "Wifi-Servis.xml"),
        (Join-Path $env:TEMP "wifi_profile.xml"),
        (Join-Path $env:TEMP "wlan_preoobe.xml"),
        (Join-Path $LocalSetupDir "Skripte\Wifi-Servis.ini"),
        "C:\setup\Skripte\Wifi-Servis.ini"
    )

    # STROGA ZASTITA SERVISNOG USB MEDIJUMA: dozvoljeno je brisanje iskljucivo na sistemskom disku ($env:SystemDrive / C:\)
    $wifiFilesToDelete = [System.Collections.Generic.List[string]]::new()
    foreach ($wCand in $rawCandidateFiles) {
        if ([string]::IsNullOrWhiteSpace($wCand)) { continue }
        $isClientSysDrive = (
            $wCand.StartsWith($sysRoot, [System.StringComparison]::OrdinalIgnoreCase) -or
            $wCand.StartsWith("C:\", [System.StringComparison]::OrdinalIgnoreCase)
        )
        if ($isClientSysDrive -and -not $wifiFilesToDelete.Contains($wCand)) {
            $wifiFilesToDelete.Add($wCand)
        }
    }

    foreach ($wFile in $wifiFilesToDelete) {
        if (Test-Path $wFile) {
            try {
                Remove-Item -Path $wFile -Force -ErrorAction SilentlyContinue
            } catch {}
        }
    }

    # c) Eksplicitna verifikacija odsustva svih ciljnih credential fajlova i servisnih WLAN profila
    $remainingFiles = [System.Collections.Generic.List[string]]::new()
    foreach ($wFile in $wifiFilesToDelete) {
        if (Test-Path $wFile) {
            $remainingFiles.Add($wFile)
        }
    }

    $remainingProfiles = [System.Collections.Generic.List[string]]::new()
    try {
        $wlanCheckAfter = & netsh.exe wlan show profiles 2>$null
        if ($LASTEXITCODE -eq 0 -and $wlanCheckAfter) {
            foreach ($wpLine in $wlanCheckAfter) {
                if ($wpLine -match ':\s*(.+)$') {
                    $wName = $matches[1].Trim()
                    if ($wName -and ($serviceSsids.Contains($wName) -or $wName -match '^Wifi-Servis')) {
                        if (-not $remainingProfiles.Contains($wName)) {
                            $remainingProfiles.Add($wName)
                        }
                    }
                }
            }
        }
    } catch {}
    finally {
        $global:LASTEXITCODE = 0
    }

    $antiLeakVerified = ($remainingFiles.Count -eq 0 -and $remainingProfiles.Count -eq 0)

    # d) Perzistencija anti_leak_verified statusa u deploy-state.json
    $statePaths = [System.Collections.Generic.List[string]]::new()
    $primaryState = Join-Path $LocalSetupDir "deploy-state.json"
    if ($primaryState.StartsWith($sysRoot, [System.StringComparison]::OrdinalIgnoreCase) -or $primaryState.StartsWith("C:\", [System.StringComparison]::OrdinalIgnoreCase)) {
        $statePaths.Add($primaryState)
    }
    if (-not $statePaths.Contains("C:\setup\deploy-state.json")) {
        $statePaths.Add("C:\setup\deploy-state.json")
    }

    $utf8NoBom = New-Object System.Text.UTF8Encoding($false)
    foreach ($stateFile in $statePaths) {
        try {
            $stateDir = Split-Path $stateFile -Parent
            if ($stateDir -and -not (Test-Path $stateDir)) {
                New-Item -Path $stateDir -ItemType Directory -Force -ErrorAction SilentlyContinue | Out-Null
            }
            $stateObj = [ordered]@{}
            if (Test-Path $stateFile) {
                $rawState = Get-Content -Path $stateFile -Raw -Encoding UTF8 -ErrorAction SilentlyContinue
                if ($rawState) {
                    $parsed = $rawState | ConvertFrom-Json -ErrorAction SilentlyContinue
                    if ($parsed) {
                        foreach ($p in $parsed.PSObject.Properties) {
                            $stateObj[$p.Name] = $p.Value
                        }
                    }
                }
            }
            $stateObj["anti_leak_verified"] = [bool]$antiLeakVerified
            $stateObj["anti_leak_remaining_files"] = @($remainingFiles)
            $stateObj["anti_leak_remaining_profiles"] = @($remainingProfiles)
            $stateObj["updated_at"] = (Get-Date).ToString("o")

            $jsonOut = ConvertTo-Json -InputObject $stateObj -Depth 5 -Compress
            [System.IO.File]::WriteAllText($stateFile, $jsonOut, $utf8NoBom)
        } catch {}
    }

    if ($antiLeakVerified) {
        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
            Write-ServisLog "Uspesno obrisani i verifikovani svi servisni Wi-Fi profili ($($serviceSsids -join ', ')) i credential fajlovi (anti_leak_verified=True)." -Level "SUCCESS" -Component "Security"
        }
        return $true
    } else {
        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
            Write-ServisLog "KRITICNA GRESKA (Anti-Leak): Nisu uklonjene sve servisne tajne! Preostali fajlovi: [$($remainingFiles -join ', ')]; Preostali profili: [$($remainingProfiles -join ', ')] (anti_leak_verified=False)." -Level "ERROR" -Component "Security"
        }
        return $false
    }
}

function Restore-ServisUacDefaults {
    <#
    .SYNOPSIS
        Vraca nivoe kontrole korisnickog naloga (UAC) na fabricke Windows 11 bezbednosne vrednosti.
    .DESCRIPTION
        Postavlja ConsentPromptBehaviorAdmin na 5 i PromptOnSecureDesktop na 1 pod
        HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System.
    #>
    [CmdletBinding()]
    param()

    try {
        $uacRegPath = "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System"
        Set-ItemProperty -Path $uacRegPath -Name "ConsentPromptBehaviorAdmin" -Value 5 -Force -ErrorAction SilentlyContinue
        Set-ItemProperty -Path $uacRegPath -Name "PromptOnSecureDesktop" -Value 1 -Force -ErrorAction SilentlyContinue
        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
            Write-ServisLog "UAC nivoi uspesno vraceni na fabricke Windows 11 vrednosti (ConsentPromptBehaviorAdmin=5, PromptOnSecureDesktop=1)." -Level "SUCCESS" -Component "Security"
        }
        return $true
    } catch {
        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
            Write-ServisLog "Greska pri vracanju UAC vrednosti: $($_.Exception.Message)" -Level "WARN" -Component "Security"
        }
        return $false
    }
}

function Invoke-ServisCleanup {
    <#
    .SYNOPSIS
        Alias/wrapper za Invoke-ServisCleanupAndFinalize.
    #>
    [CmdletBinding()]
    param(
        [string]$LocalSetupDir = "C:\setup",
        [string]$SystemLogDir = "C:\Windows\Logs\WIS",
        [string]$PublicDesktopLog = "C:\Users\Public\Desktop\WIS_Izvestaj.log",
        [string]$SourceRoot = "",
        [switch]$Worker = $false
    )
    Invoke-ServisCleanupAndFinalize -LocalSetupDir $LocalSetupDir -SystemLogDir $SystemLogDir -PublicDesktopLog $PublicDesktopLog -SourceRoot $SourceRoot -Worker:$Worker
}

function Start-FinalCleanup {
    <#
    .SYNOPSIS
        Priprema i izvršava završno čišćenje sistema.
    .DESCRIPTION
        Razdvojena funkcija za pripremu čišćenja:
        - Agresivno uklanja prečice i privremene skripte sa svih Desktop lokacija
          (C:\Users\Public\Desktop i C:\Users\*\Desktop): *Launcher*.lnk, *WIS*.lnk,
          *Pokreni*.bat, *Pokreni*.lnk, setup.lnk.
        - Osvežava Explorer shell preko Refresh-ExplorerShell.
        - Raskida SMB mrežne sesije radi zaštite NAS akreditiva.
        - Briše servisne Wi-Fi profile i lozinke (Clear-ServisWifiSecrets).
        - Vraća UAC nivoe u registru na fabričke vrednosti (Restore-ServisUacDefaults).
        - Uklanja RunOnce i Startup zaostale unose.
        Ukoliko se poziva unutar završne faze ($InPhase5 / $InPhase6) ili uz -NoReboot:
        NE gasi mašinu (nema shutdown.exe) i NE briše folder C:\setup!
    #>
    [CmdletBinding()]
    param(
        [string]$LocalSetupDir = "C:\setup",
        [string]$SystemLogDir = "C:\Windows\Logs\WIS",
        [switch]$InPhase5 = $false,
        [switch]$InPhase6 = $false,
        [switch]$NoReboot = $false,
        [switch]$DeleteSetupDir = $false
    )

    $isFinalPhase = ($InPhase5 -or $InPhase6)

    if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
        Write-ServisLog "Start-FinalCleanup: Zapocinjem pripremu zavrsnog ciscenja (InFinalPhase=$isFinalPhase, NoReboot=$NoReboot)..." -Level "INFO" -Component "Cleanup"
    }

    # 1. Raskidanje svih SMB sesija radi zaštite servisnih NAS akreditiva
    & net.exe use * /delete /y >$null 2>&1

    # 2. Uklanjanje RunOnce i Startup zaostalih unosa
    Remove-ItemProperty -Path "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce" -Name "WIS_Resume" -ErrorAction SilentlyContinue
    Remove-ItemProperty -Path "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce" -Name "ServisDeployResume" -ErrorAction SilentlyContinue
    Remove-ItemProperty -Path "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce" -Name "WIS_AutoStart" -ErrorAction SilentlyContinue

    $startupDir = [Environment]::GetFolderPath('CommonStartup')
    if (-not $startupDir) { $startupDir = 'C:\ProgramData\Microsoft\Windows\Start Menu\Programs\Startup' }
    $resumeBat = Join-Path $startupDir 'WIS-Resume.cmd'
    if (Test-Path $resumeBat) { Remove-Item -Path $resumeBat -Force -ErrorAction SilentlyContinue }
    $autoStartCmd = Join-Path $startupDir 'WIS-AutoStart.cmd'
    if (Test-Path $autoStartCmd) { Remove-Item -Path $autoStartCmd -Force -ErrorAction SilentlyContinue }

    # 3. Agresivno čišćenje radnih površina (*Launcher*.lnk, *WIS*.*, *Pokreni*.*, setup.lnk) i Refresh Explorer
    Clear-ServisDesktopShortcuts | Out-Null

    # 4. Uklanjanje privremenih instalacionih fajlova unutar C:\setup bez brisanja samog C:\setup direktorijuma
    if (Test-Path (Join-Path $LocalSetupDir "Programi")) {
        Remove-Item -Path (Join-Path $LocalSetupDir "Programi") -Recurse -Force -ErrorAction SilentlyContinue
    }
    if (-not $isFinalPhase -and $DeleteSetupDir) {
        $stateF = Join-Path $LocalSetupDir "deploy-state.json"
        if (Test-Path $stateF) {
            Remove-Item -Path $stateF -Force -ErrorAction SilentlyContinue
        }
    }

    # 5. Anti-Leak: Brisanje servisnih Wi-Fi profila i tajni uz verifikaciju odsustva
    $antiLeakOk = $true
    if (Get-Command Clear-ServisWifiSecrets -ErrorAction SilentlyContinue) {
        $antiLeakOk = [bool](Clear-ServisWifiSecrets -LocalSetupDir $LocalSetupDir)
    }

    # 6. Bezbednost: Vraćanje fabričkog nivoa UAC-a
    if (Get-Command Restore-ServisUacDefaults -ErrorAction SilentlyContinue) {
        Restore-ServisUacDefaults | Out-Null
    }

    # 7. Rukovanje C:\setup i gašenjem računara:
    # U završnoj fazi (Faza 6) strogo je zabranjeno automatsko brisanje C:\setup i pokretanje shutdown.exe!
    if ($isFinalPhase) {
        if ($antiLeakOk) {
            if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                Write-ServisLog "Faza 6 aktivna: Folder C:\setup je sacuvan i masina se NE gasi. Zavrsni korak prepusten UI-ju i operateru." -Level "SUCCESS" -Component "Cleanup"
            }
        } else {
            if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                Write-ServisLog "Faza 6 aktivna: Anti-Leak verifikacija NIJE uspela! Predaja racunara je blokirana." -Level "ERROR" -Component "Cleanup"
            }
        }
        return $antiLeakOk
    }

    # Ako NIJE u završnoj fazi i izričito je zatraženo brisanje C:\setup:
    if ($DeleteSetupDir) {
        $selfDestructCmd = "/c timeout /t 3 /nobreak >nul & net use * /delete /y >nul 2>&1 & del /f /q `"C:\Users\Public\Desktop\WIS_*.*`" >nul 2>&1 & timeout /t 3 /nobreak >nul & rmdir /s /q `"$LocalSetupDir`""
        if ($LocalSetupDir -ne "C:\setup" -and (Test-Path "C:\setup")) {
            $selfDestructCmd += " & rmdir /s /q `"C:\setup`""
        }
        Start-Process cmd.exe -ArgumentList $selfDestructCmd -WindowStyle Hidden
    }

    # Restart/Shutdown se vrši ISKLJUČIVO ako nije završna faza i nije NoReboot
    if (-not $NoReboot) {
        try {
            shutdown.exe /r /t 5 /c "WIS SERVICE | Instalacija i ciscenje zavrseni. Restartujem racunar..." /f
            Restart-Computer -Force -ErrorAction SilentlyContinue
        } catch {
            shutdown.exe /r /t 3 /f
        }
    }

    return $antiLeakOk
}

function Invoke-Phase6Finalize {
    [CmdletBinding()]
    param(
        [string]$LocalSetupDir = "C:\setup",
        [string]$SystemLogDir = "C:\Windows\Logs\WIS",
        [string]$HubUrl = "",
        [switch]$NoReboot
    )

    if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
        Write-ServisLog "=== POKRETANJE FAZE 6: Zavrsetak, Flotni Izvestaj, Servisni Karton i Anti-Leak ===" -Level "STEP" -Component "Phase6"
    }
    if (Get-Command Send-IpcProgress -ErrorAction SilentlyContinue) {
        Send-IpcProgress -Progress 5 -Phase 6 -PhaseName "Završetak" -SubPhase "Inicijalizacija" -StatusMessage "Priprema završnih procedura..."
    }

    if (Get-Command Wait-ServisUICooperativePause -ErrorAction SilentlyContinue) {
        Wait-ServisUICooperativePause -CheckpointName "Faza 6 - Pocetak"
    }

    # 0. Windows Update barijera - pokreće se samo kratko (max 15s) i isključivo ako Phase6 nije već završila Invoke-WindowsUpdateSecondPass
    $spDoneCandidate1 = Join-Path $LocalSetupDir "wu-secondpass-done.flag"
    $spDoneCandidate2 = "C:\setup\wu-secondpass-done.flag"
    $spAlreadyDone = (Test-Path $spDoneCandidate1) -or (Test-Path $spDoneCandidate2)
    if ((-not $spAlreadyDone) -and (Get-Command Wait-WindowsUpdateCompletionBarrier -ErrorAction SilentlyContinue)) {
        try {
            Wait-WindowsUpdateCompletionBarrier -TimeoutSeconds 15
        } catch {
            if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                Write-ServisLog "Upozorenje u WU barijeri unutar Invoke-Phase6Finalize: $($_.Exception.Message)" -Level "WARN" -Component "Phase6"
            }
        }
    } elseif ($spAlreadyDone) {
        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
            Write-ServisLog "Invoke-Phase6Finalize: Zavrsni Windows Update drugi prolaz (wu-secondpass-done.flag) je vec kompletiran; preskacem duplu WU barijeru." -Level "INFO" -Component "Phase6"
        }
    }

    # 1. DMTF IDENTIFIKACIJA ŠASIJE (TK-{Chassis}-{SerialNumber} - Best-Effort)
    if (Get-Command Send-IpcProgress -ErrorAction SilentlyContinue) {
        Send-IpcProgress -Progress 15 -Phase 6 -PhaseName "Završetak" -SubPhase "DMTF Identifikacija" -StatusMessage "Očitavanje hardverskog DMTF identifikatora šasije..."
    }
    
    $deviceId = ""
    $chassis = "Desktop"
    $serial = "UNKNOWN"

    try {
        if (Get-Command Get-FleetDeviceId -ErrorAction SilentlyContinue) {
            $deviceId = Get-FleetDeviceId
            $chassis = if ($Global:FleetChassis) { $Global:FleetChassis } else { "PC" }
            $serial = if ($Global:FleetSerialNumber) { $Global:FleetSerialNumber } else { "UNKNOWN" }
        } else {
            $enc = Get-CimInstance Win32_SystemEnclosure -ErrorAction SilentlyContinue
            $bios = Get-CimInstance Win32_Bios -ErrorAction SilentlyContinue
            $types = if ($enc -and $enc.ChassisTypes) { @($enc.ChassisTypes) } else { @() }
            
            if (($types -contains 13) -or ((Get-CimInstance Win32_ComputerSystem).Model -match '(?i)all[- ]in[- ]one|\baio\b')) {
                $chassis = "ALL-IN-ONE"
            } elseif (($types | Where-Object { $_ -in @(8, 9, 10, 11, 12, 14, 30, 31, 32) }) -or (Get-CimInstance Win32_Battery -ErrorAction SilentlyContinue)) {
                $chassis = "LAPTOP"
            } elseif (($types | Where-Object { $_ -in @(15, 16, 33, 34, 35, 36) }) -or ((Get-CimInstance Win32_ComputerSystem).Model -match '(?i)\b(mini|micro|tiny|nuc|usff|dm)\b')) {
                $chassis = "MINI-PC"
            } else {
                $chassis = "DESKTOP"
            }
            $rawSerial = if ($bios -and $bios.SerialNumber) { $bios.SerialNumber.Trim() } else { $env:COMPUTERNAME }
            $serial = ($rawSerial -replace '[^a-zA-Z0-9_-]', '').ToUpper()
            $deviceId = "TK-$chassis-$serial"
        }
    } catch {
        $deviceId = "TK-DESKTOP-$($env:COMPUTERNAME)"
    }
    if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
        Write-ServisLog "Flotni identifikator racunara: $deviceId (Sasija: $chassis, Serijski: $serial)" -Level "INFO" -Component "Telemetry"
    }

    # 2. GENERISANJE SERVISNOG KARTONA (JSON/MD) I ARHIVIRANJE U C:\Windows\Logs\WIS\
    if (Get-Command Send-IpcProgress -ErrorAction SilentlyContinue) {
        Send-IpcProgress -Progress 35 -Phase 6 -PhaseName "Završetak" -SubPhase "Servisni Karton" -StatusMessage "Generisanje servisnog kartona (JSON/MD)..."
    }
    if (Get-Command Wait-ServisUICooperativePause -ErrorAction SilentlyContinue) {
        Wait-ServisUICooperativePause -CheckpointName "Faza 6 - Servisni Karton"
    }

    try {
        if (-not (Test-Path $SystemLogDir)) {
            New-Item -Path $SystemLogDir -ItemType Directory -Force | Out-Null
        }

        if (Get-Command Export-ServisSummaryReport -ErrorAction SilentlyContinue) {
            $verResults = if (Get-Command Test-AllServisProgramsInstalled -ErrorAction SilentlyContinue) { Test-AllServisProgramsInstalled } else { $null }
            Export-ServisSummaryReport -VerificationResults $verResults -LocalSetupDir $LocalSetupDir -SystemLogDir $SystemLogDir
        }

        $logFilesToArchive = @("servis_deploy.log", "servis_deploy.jsonl", "setupcomplete.log", "firstlogon.log", "WIS_Status_Instalacije.txt", "servisni_karton.json", "servisni_karton.md", "acrobat_install.log", "payload_meta.json", "build-attestation.json", "vm-validation.json", "version.json")
        foreach ($lf in $logFilesToArchive) {
            $srcP = Join-Path $LocalSetupDir $lf
            if (Test-Path $srcP) {
                Copy-Item -Path $srcP -Destination (Join-Path $SystemLogDir $lf) -Force -ErrorAction SilentlyContinue
            }
        }
        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
            Write-ServisLog "Servisni karton (JSON/MD) i dnevnici uspesno arhivirani u $SystemLogDir." -Level "SUCCESS" -Component "Archiver"
        }
    } catch {
        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
            Write-ServisLog "Upozorenje pri arhiviranju dnevnika: $($_.Exception.Message)" -Level "WARN" -Component "Archiver"
        }
    }

    # 3. SLANJE ZAVRŠNOG IZVEŠTAJA NA SERFISHUB (POST /api/devices/<id>/report I LIFECYCLE COMPLETED)
    if (Get-Command Send-IpcProgress -ErrorAction SilentlyContinue) {
        Send-IpcProgress -Progress 55 -Phase 6 -PhaseName "Završetak" -SubPhase "Flotni Izveštaj" -StatusMessage "Slanje završnog izveštaja i statusa COMPLETED na ServisHub..."
    }
    if (Get-Command Wait-ServisUICooperativePause -ErrorAction SilentlyContinue) {
        Wait-ServisUICooperativePause -CheckpointName "Faza 6 - Flotni Karton"
    }

    try {
        # Prikupljanje dijagnostičkog ugovora hlađenja kako COMPLETED lifecycle ne bi pregazio cooling_verified
        $diagCustom = @{}
        if (Get-Command Get-WisTelemetryDiagnostics -ErrorAction SilentlyContinue) {
            try {
                $diagInfo = Get-WisTelemetryDiagnostics -LocalSetupDir $LocalSetupDir -SystemLogDir $SystemLogDir
                if ($diagInfo) {
                    foreach ($dk in $diagInfo.Keys) {
                        $diagCustom[$dk] = $diagInfo[$dk]
                    }
                }
            } catch {}
        }

        # 3a. Slanje životnog ciklusa COMPLETED
        if (Get-Command Send-ServisLifecycleEvent -ErrorAction SilentlyContinue) {
            $lcData = @{
                device_id = $deviceId
                status    = "COMPLETED"
                chassis   = $chassis
                serial    = $serial
            }
            foreach ($dk in $diagCustom.Keys) {
                $lcData[$dk] = $diagCustom[$dk]
            }
            Send-ServisLifecycleEvent -EventType "COMPLETED" -Data $lcData -LocalSetupDir $LocalSetupDir | Out-Null
        }

        # 3b. Slanje kompletnog završnog izveštaja na ServisHub (POST /api/devices/<id>/report)
        $repOk = $false
        if (Get-Command Send-FleetFinalReport -ErrorAction SilentlyContinue) {
            $repOk = Send-FleetFinalReport -Status "COMPLETED" -CustomData $diagCustom -LocalSetupDir $LocalSetupDir -SystemLogDir $SystemLogDir
        }

        if (-not $repOk) {
            $targetHub = if ($HubUrl) { $HubUrl } elseif ($Global:ServisHubUrl) { $Global:ServisHubUrl } else { "" }
            if ($targetHub) {
                try {
                    $repPayload = [ordered]@{
                        device_id     = $deviceId
                        serial_number = $serial
                        status        = "COMPLETED"
                        timestamp     = (Get-Date).ToString("o")
                        hostname      = $env:COMPUTERNAME
                    }
                    foreach ($dk in $diagCustom.Keys) {
                        $repPayload[$dk] = $diagCustom[$dk]
                    }
                    $jsonRep = ConvertTo-Json -InputObject $repPayload -Compress
                    $postUrl = "$targetHub/api/devices/$deviceId/report"
                    $req = [System.Net.WebRequest]::Create($postUrl)
                    $req.Method = "POST"
                    $req.ContentType = "application/json; charset=utf-8"
                    $req.Timeout = 2000
                    $bytes = [System.Text.Encoding]::UTF8.GetBytes($jsonRep)
                    $req.ContentLength = $bytes.Length
                    $st = $req.GetRequestStream()
                    $st.Write($bytes, 0, $bytes.Length)
                    $st.Close()
                    $resp = $req.GetResponse()
                    $resp.Close()
                    $repOk = $true
                    if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                        Write-ServisLog "Flotni izvestaj poslat preko fallback HTTP poziva ka $postUrl" -Level "SUCCESS" -Component "Telemetry"
                    }
                } catch {}

                try {
                    $lcPayload = [ordered]@{
                        event         = "COMPLETED"
                        device_id     = $deviceId
                        computer_name = $env:COMPUTERNAME
                        timestamp     = (Get-Date).ToString("o")
                    }
                    foreach ($dk in $diagCustom.Keys) {
                        $lcPayload[$dk] = $diagCustom[$dk]
                    }
                    $jsonLc = ConvertTo-Json -InputObject $lcPayload -Compress
                    $lcUrl = "$targetHub/api/lifecycle"
                    $reqLc = [System.Net.WebRequest]::Create($lcUrl)
                    $reqLc.Method = "POST"
                    $reqLc.ContentType = "application/json; charset=utf-8"
                    $reqLc.Timeout = 2000
                    $bytesLc = [System.Text.Encoding]::UTF8.GetBytes($jsonLc)
                    $reqLc.ContentLength = $bytesLc.Length
                    $stLc = $reqLc.GetRequestStream()
                    $stLc.Write($bytesLc, 0, $bytesLc.Length)
                    $stLc.Close()
                    $respLc = $reqLc.GetResponse()
                    $respLc.Close()
                } catch {}
            }
        }

        if ($repOk) {
            if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                Write-ServisLog "Zavrsni izvestaj i COMPLETED status uspesno poslati na ServisHub za $deviceId." -Level "SUCCESS" -Component "Telemetry"
            }
        }
    } catch {
        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
            Write-ServisLog "Upozorenje pri slanju zavrsnog izvestaja: $($_.Exception.Message)" -Level "WARN" -Component "Telemetry"
        }
    }

    # 4. PRIPREMA ZAVRŠNOG ČIŠĆENJA, ANTI-LEAK I VRACANJE UAC-a (BEZ BRISANJA C:\setup I BEZ SHUTDOWN)
    if (Get-Command Send-IpcProgress -ErrorAction SilentlyContinue) {
        Send-IpcProgress -Progress 80 -Phase 6 -PhaseName "Završetak" -SubPhase "Priprema Čišćenja" -StatusMessage "Čišćenje radne površine, Anti-Leak zaštita i vraćanje UAC-a..."
    }
    if (Get-Command Wait-ServisUICooperativePause -ErrorAction SilentlyContinue) {
        Wait-ServisUICooperativePause -CheckpointName "Faza 6 - Ciscenje"
    }

    # Zaštitno zaustavljanje 1 Hz Live Screen Mirror radnika pre Anti-Leak čišćenja (Servis Hub v3.2 integracija)
    try {
        $stoppedScreenCapture = $false
        if (Get-Command Stop-ServisScreenCapture -ErrorAction SilentlyContinue) {
            $stoppedScreenCapture = Stop-ServisScreenCapture
        } else {
            $screenWorkerCandidates = @(
                (Join-Path $PSScriptRoot "WIS.ScreenCapture.ps1"),
                (Join-Path $LocalSetupDir "Skripte\Modules\WIS.ScreenCapture.ps1"),
                "C:\setup\Skripte\Modules\WIS.ScreenCapture.ps1"
            )
            $screenWorkerPath = $screenWorkerCandidates | Where-Object { Test-Path $_ -ErrorAction SilentlyContinue } | Select-Object -First 1
            if ($screenWorkerPath) {
                & $screenWorkerPath -Stop | Out-Null
                $stoppedScreenCapture = $true
            }
        }
        $remainingJobs = Get-Job -Name "WIS_ScreenCapture*" -ErrorAction SilentlyContinue
        if ($remainingJobs) {
            $remainingJobs | Stop-Job -ErrorAction SilentlyContinue | Out-Null
            $remainingJobs | Remove-Job -Force -ErrorAction SilentlyContinue | Out-Null
            $stoppedScreenCapture = $true
        }
        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
            Write-ServisLog "Zaštitno zaustavljanje 1 Hz Live Screen Mirror radnika u Invoke-Phase6Finalize izvršeno." -Level "INFO" -Component "Phase6"
        }
    } catch {
        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
            Write-ServisLog "Upozorenje pri zaustavljanju Screen Mirror radnika u Invoke-Phase6Finalize: $($_.Exception.Message)" -Level "WARN" -Component "Phase6"
        }
    }

    $antiLeakOk = $false
    try {
        if (Get-Command Start-FinalCleanup -ErrorAction SilentlyContinue) {
            $antiLeakOk = [bool](Start-FinalCleanup -LocalSetupDir $LocalSetupDir -SystemLogDir $SystemLogDir -InPhase6 -NoReboot)
        } else {
            Clear-ServisDesktopShortcuts | Out-Null
            if (Get-Command Restore-ServisUacDefaults -ErrorAction SilentlyContinue) {
                Restore-ServisUacDefaults | Out-Null
            }
        }
        if (Get-Command Clear-ServisWifiSecrets -ErrorAction SilentlyContinue) {
            $antiLeakOk = Clear-ServisWifiSecrets -LocalSetupDir $LocalSetupDir
        }
    } catch {
        $antiLeakOk = $false
        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
            Write-ServisLog "Greska pri pripremi ciscenja i Anti-Leak verifikaciji: $($_.Exception.Message)" -Level "ERROR" -Component "Phase6"
        }
    }

    if (-not $antiLeakOk) {
        if (Get-Command Send-IpcLog -ErrorAction SilentlyContinue) {
            Send-IpcLog -Message "KRITIČNA GREŠKA: Anti-Leak brisanje servisnih Wi-Fi tajni nije uspelo! Sistem NIJE spreman za predaju." -Level "ERROR" -Tag "[SECURITY]" -Phase 6
        }
        if (Get-Command Send-IpcProgress -ErrorAction SilentlyContinue) {
            Send-IpcProgress -Progress 95 -Phase 6 -PhaseName "Završetak" -SubPhase "Blokirano (Anti-Leak)" -StatusMessage "GREŠKA: Zaostale servisne Wi-Fi tajne! Sistem NIJE spreman za predaju."
        }
        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
            Write-ServisLog "=== FAZA 6 BLOKIRANA: Anti-Leak verifikacija nije prosla (anti_leak_verified=False). Zabranjeno proglasavanje statusa READY/SUCCESS! ===" -Level "ERROR" -Component "Phase6"
        }
        $global:LASTEXITCODE = 1
        return $false
    }

    # 5. POSTAVLJANJE NAPRETKA NA 100% I USPEŠAN ZAVRŠETAK (C:\setup se NE brise, shutdown prepusten UI-ju)
    if (Get-Command Send-IpcProgress -ErrorAction SilentlyContinue) {
        Send-IpcProgress -Progress 100 -Phase 6 -PhaseName "Završetak" -SubPhase 'Završeno' -StatusMessage 'SERVIS ZAVRŠEN! Sistem je verifikovan i spreman za pakovanje.'
    }

    if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
        Write-ServisLog "=== FAZA 6 USPESNO ZAVRSENA: Servisni karton kreiran, ServisHub obavesten (COMPLETED), Anti-Leak primenjen, UAC vracen. Sistem spreman za pakovanje ===" -Level "SUCCESS" -Component "Phase6"
    }

    $global:LASTEXITCODE = 0
    return $true
}

function Invoke-Phase5Finalize {
    [CmdletBinding()]
    param(
        [string]$LocalSetupDir = "C:\setup",
        [string]$SystemLogDir = "C:\Windows\Logs\WIS",
        [string]$HubUrl = "",
        [switch]$NoReboot
    )
    return (Invoke-Phase6Finalize -LocalSetupDir $LocalSetupDir -SystemLogDir $SystemLogDir -HubUrl $HubUrl -NoReboot:$NoReboot)
}

Export-ModuleMember -Function `
    Export-ServisSummaryReport, `
    Clear-ServisWifiSecrets, `
    Restore-ServisUacDefaults, `
    Clear-ServisDesktopShortcuts, `
    Refresh-ExplorerShell, `
    Start-FinalCleanup, `
    Invoke-ServisCleanup, `
    Invoke-ServisCleanupAndFinalize, `
    Invoke-Phase5Finalize, `
    Invoke-Phase6Finalize
