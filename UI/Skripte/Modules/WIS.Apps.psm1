# SEE_MASK_NOZONECHECKS - Globalno onemogucavanje sigurnosnih dijaloga za pokretanje izvrsnih datoteka (Open File - Security Warning)
$env:SEE_MASK_NOZONECHECKS = '1'
try { [Environment]::SetEnvironmentVariable("SEE_MASK_NOZONECHECKS", "1", "Process") } catch {}

<#
.MODULE
    WIS.Apps.psm1
.DESCRIPTION
    Tiha instalacija, asocijacije, prečice, licenciranje i verifikacija aplikacija za WIS (Windows Installation Assistant).
    Podrzava: Chrome, WinRAR, Office 2024 (ODT), Adobe Acrobat, BatteryBar, .NET Runtimes i verifikaciju OEM licenci.
    Ukljucuje bezbedan mehanizam rukovanja Windows Installer (MSI) mutexom bez Error 1618 sukoba.
.COMPATIBILITY
    Windows PowerShell 5.1+
#>

$env:SEE_MASK_NOZONECHECKS = '1'
try { [Environment]::SetEnvironmentVariable("SEE_MASK_NOZONECHECKS", "1", "Process") } catch {}

function Remove-PendingRebootLocks {
    try {
        $sm = "HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager"
        if (Get-ItemProperty -Path $sm -Name "PendingFileRenameOperations" -ErrorAction SilentlyContinue) {
            Remove-ItemProperty -Path $sm -Name "PendingFileRenameOperations" -Force -ErrorAction SilentlyContinue
            if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                Write-ServisLog "Uklonjen privremeni kljuc PendingFileRenameOperations koji blokira instalaciju." -Level "DEBUG" -Component "LockCleaner"
            }
        }
        if (Get-ItemProperty -Path $sm -Name "PendingFileRenameOperations2" -ErrorAction SilentlyContinue) {
            Remove-ItemProperty -Path $sm -Name "PendingFileRenameOperations2" -Force -ErrorAction SilentlyContinue
        }
        Remove-Item -Path "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Installer\InProgress" -Recurse -Force -ErrorAction SilentlyContinue
        Remove-ItemProperty -Path "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update" -Name "RebootRequired" -Force -ErrorAction SilentlyContinue
        Remove-ItemProperty -Path "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing" -Name "RebootPending" -Force -ErrorAction SilentlyContinue
        Remove-ItemProperty -Path "HKLM:\SOFTWARE\Microsoft\Updates" -Name "UpdateExeVolatile" -Force -ErrorAction SilentlyContinue
    } catch {}
}

function Test-MsiBusy {
    param(
        [switch]$Detailed
    )
    # 1. OS-wide flag ServisWuActive (primarno HKLM:\SOFTWARE\WIS, sa fallback proverom Installer kljuca)
    try {
        $wuFlag = (Get-ItemProperty -Path "HKLM:\SOFTWARE\WIS" -Name "ServisWuActive" -ErrorAction SilentlyContinue).ServisWuActive
        if ($wuFlag -ne 1) {
            $wuFlag = (Get-ItemProperty -Path "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Installer" -Name "ServisWuActive" -ErrorAction SilentlyContinue).ServisWuActive
        }
        if ($wuFlag -eq 1) {
            if ($Detailed) { return @{ Busy = $true; Reason = "Windows Update instalira pakete (ServisWuActive = 1)" } }
            return $true
        }
    } catch {}

    # 2. Provera globalnog Windows Installer mutexa (_MSIExecute)
    try {
        $mutex = $null
        if ([System.Threading.Mutex]::TryOpenExisting("Global\_MSIExecute", [ref]$mutex)) {
            if ($mutex) {
                $acquired = $false
                try {
                    $acquired = $mutex.WaitOne(0, $false)
                } catch {}
                if ($acquired) {
                    try { $mutex.ReleaseMutex() } catch {}
                    $mutex.Dispose()
                } else {
                    $mutex.Dispose()
                    if ($Detailed) { return @{ Busy = $true; Reason = "Global\_MSIExecute mutex zakljucan (druga instalacija u toku)" } }
                    return $true
                }
            }
        }
    } catch {}

    # 3. Provera InProgress registry kljuca
    if (Test-Path "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Installer\InProgress") {
        if ($Detailed) { return @{ Busy = $true; Reason = "Installer\InProgress registry kljuc postoji" } }
        return $true
    }

    # 4. Provera aktivnih msiexec procesa
    try {
        $msiProcs = @(Get-CimInstance Win32_Process -Filter "Name = 'msiexec.exe'" -ErrorAction SilentlyContinue)
        if ($msiProcs -and $msiProcs.Count -gt 0) {
            # Ako postoji proces koji ima argumente za instalaciju (/i, /package, /update, /p, /x)
            $activeMsi = $msiProcs | Where-Object {
                $c = $_.CommandLine
                $c -and ($c -match '/i\b' -or $c -match '/package\b' -or $c -match '/update\b' -or $c -match '/p\b' -or $c -match '/x\b')
            }
            if ($activeMsi) {
                $pids = ($activeMsi | ForEach-Object { $_.ProcessId }) -join ', '
                if ($Detailed) { return @{ Busy = $true; Reason = "msiexec aktivan sa parametrima instalacije (PID: $pids)" } }
                return $true
            }
            # Ako ima 2 ili vise msiexec procesa, server i klijent aktivno komuniciraju
            if ($msiProcs.Count -ge 2) {
                if ($Detailed) { return @{ Busy = $true; Reason = "Aktivna visestruka msiexec sesija ($($msiProcs.Count) procesa)" } }
                return $true
            }
        }
    } catch {
        $fallbackProcs = @(Get-Process -Name "msiexec" -ErrorAction SilentlyContinue)
        if ($fallbackProcs -and $fallbackProcs.Count -ge 2) {
            if ($Detailed) { return @{ Busy = $true; Reason = "Vise msiexec procesa detektovano u memoriji" } }
            return $true
        }
    }

    if ($Detailed) { return @{ Busy = $false; Reason = "Slobodan" } }
    return $false
}

function Wait-MsiMutexAvailable {
    param(
        [int]$TimeoutSec = 600,
        [int]$CheckIntervalSec = 3,
        [int]$LogIntervalSec = 30
    )
    $elapsed = 0
    $lastLogged = -1

    $diag = Test-MsiBusy -Detailed
    if ($diag.Busy) {
        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
            Write-ServisLog "Windows Installer zauzet [$($diag.Reason)]. Cekam oslobadjanje... (0/${TimeoutSec}s)" -Level "INFO" -Component "MsiMutex"
        }
        $lastLogged = 0
    }

    while ($diag.Busy -and ($elapsed -lt $TimeoutSec)) {
        Start-Sleep -Seconds $CheckIntervalSec
        $elapsed += $CheckIntervalSec
        $diag = Test-MsiBusy -Detailed

        if ($diag.Busy -and (($elapsed - $lastLogged) -ge $LogIntervalSec)) {
            $lastLogged = $elapsed
            if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                Write-ServisLog "Windows Installer i dalje zauzet [$($diag.Reason)]. Cekam oslobadjanje... ($elapsed/${TimeoutSec}s)" -Level "DEBUG" -Component "MsiMutex"
            }
        }
    }

    Remove-PendingRebootLocks

    if ($diag.Busy) {
        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
            Write-ServisLog "Istekao timeout cekanja na MSI (${TimeoutSec}s). Razlog: $($diag.Reason)." -Level "WARN" -Component "MsiMutex"
        }
        return $false
    }

    if ($elapsed -gt 0) {
        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
            Write-ServisLog "Windows Installer je oslobodjen nakon ${elapsed}s cekanja." -Level "SUCCESS" -Component "MsiMutex"
        }
    }
    return $true
}

function Invoke-SafeMsi {
    param(
        [string]$Arguments,
        [string]$WorkingDirectory = "C:\setup",
        [int]$MaxWaitSec = 300
    )
    if (-not $WorkingDirectory -or -not (Test-Path $WorkingDirectory)) {
        $WorkingDirectory = "C:\setup"
    }
    $null = Wait-MsiMutexAvailable -TimeoutSec $MaxWaitSec
    Remove-PendingRebootLocks

    for ($retry = 1; $retry -le 5; $retry++) {
        $proc = Start-Process msiexec.exe -ArgumentList $Arguments -WorkingDirectory $WorkingDirectory -WindowStyle Hidden -PassThru
        if ($proc) {
            $proc.WaitForExit()
            $desc = if (Get-Command Format-ExitCodeDescription -ErrorAction SilentlyContinue) {
                Format-ExitCodeDescription $proc.ExitCode
            } else { "$($proc.ExitCode)" }
            if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                $mLevel = if ($proc.ExitCode -eq 0 -or $proc.ExitCode -eq 3010) { 'SUCCESS' } else { 'WARN' }
                Write-ServisLog "msiexec zavrsen sa kodom: $desc" -Level $mLevel -Component "SafeMsi"
            }
            if ($proc.ExitCode -eq 1618) {
                if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                    Write-ServisLog "Windows Installer zauzet (ExitCode 1618). Cekam oslobadjanje mutexa (Pokusaj $retry/5)..." -Level "WARN" -Component "SafeMsi"
                }
                $null = Wait-MsiMutexAvailable -TimeoutSec 120
                Remove-PendingRebootLocks
                continue
            }
            if ($proc.ExitCode -eq 1603 -and $retry -lt 3) {
                if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                    Write-ServisLog "msiexec vratio kod 1603 (Fatal Error). Pokusavam oslobadjanje brava i retry za 5s (Pokusaj $retry/5)..." -Level "WARN" -Component "SafeMsi"
                }
                Start-Sleep -Seconds 5
                Remove-PendingRebootLocks
                $null = Wait-MsiMutexAvailable -TimeoutSec 60
                continue
            }
            return $proc
        }
    }
    return $null
}

function Invoke-WingetAppInstall {
    <#
    .SYNOPSIS
        Tiha instalacija paketa preko Windows Package Manager-a (winget).
    .DESCRIPTION
        Sluzi kao pouzdan fallback kada lokalni SSD/USB paketi programa nedostaju
        ili kada primarna instalacija prijavi gresku.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory=$true)][string]$PackageId,
        [string]$CustomArgs = "",
        [int]$TimeoutSec = 300
    )

    $wingetCmd = (Get-Command winget.exe -ErrorAction SilentlyContinue).Source
    if (-not $wingetCmd) {
        $wingetCandidates = @(
            "$env:LOCALAPPDATA\Microsoft\WindowsApps\winget.exe",
            "C:\Users\$env:USERNAME\AppData\Local\Microsoft\WindowsApps\winget.exe",
            "$env:ProgramFiles\WindowsApps\Microsoft.DesktopAppInstaller_*_x64__8wekyb3d8bbwe\winget.exe",
            "$env:SystemRoot\System32\winget.exe"
        )
        foreach ($wc in $wingetCandidates) {
            $resolved = Get-ChildItem -Path $wc -ErrorAction SilentlyContinue | Select-Object -First 1
            if ($resolved -and (Test-Path $resolved.FullName)) {
                $wingetCmd = $resolved.FullName
                break
            }
        }
    }

    if (-not $wingetCmd) {
        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
            Write-ServisLog "Winget.exe nije pronadjen na sistemu. Preskacem Winget fallback za $PackageId." -Level "DEBUG" -Component "Winget"
        }
        return $false
    }

    $arguments = "install --id $PackageId --exact --silent --accept-package-agreements --accept-source-agreements --disable-interactivity"
    if ($CustomArgs) {
        $arguments += " $CustomArgs"
    }

    if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
        Write-ServisLog "Pokrecem Winget fallback: $PackageId ($arguments)" -Level "STEP" -Component "Winget"
    }

    try {
        $proc = Start-Process -FilePath $wingetCmd -ArgumentList $arguments -PassThru -WindowStyle Hidden
        if ($proc) {
            $timeoutMs = if ($TimeoutSec -gt 0) { $TimeoutSec * 1000 } else { 300000 }
            $exited = $proc.WaitForExit($timeoutMs)
            if (-not $exited -or (-not $proc.HasExited)) {
                if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                    Write-ServisLog "Winget fallback za $PackageId je prekoračio timeout od ${TimeoutSec}s. Prisilno prekidam proces (PID: $($proc.Id))..." -Level "WARN" -Component "Winget"
                }
                try {
                    Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
                } catch {}
                return $false
            }
            $code = $proc.ExitCode
            $success = ($code -eq 0 -or $code -eq 3010)
            if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                Write-ServisLog "Winget fallback za $PackageId zavrsen sa kodom: $code" -Level $(if ($success) { "SUCCESS" } else { "WARN" }) -Component "Winget"
            }
            return $success
        } else {
            return $false
        }
    } catch {
        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
            Write-ServisLog "Greska pri pokretanju Winget za $($PackageId): $($_.Exception.Message)" -Level "ERROR" -Component "Winget"
        }
        return $false
    }
}

function New-AppShortcut {
    param(
        [string]$TargetExe,
        [string]$ShortcutName
    )
    if (Test-Path $TargetExe) {
        $userDesktopPaths = @()
        try {
            Get-ChildItem -Path "C:\Users" -Directory -ErrorAction SilentlyContinue | ForEach-Object {
                $name = $_.Name
                if ($name -notin @("All Users", "Default User") -and (-not $name.StartsWith("Default"))) {
                    $uDesk = Join-Path $_.FullName "Desktop"
                    if (Test-Path $uDesk) { $userDesktopPaths += $uDesk }
                }
            }
        } catch {}
        if ($env:USERPROFILE) {
            $currDesk = Join-Path $env:USERPROFILE "Desktop"
            if (Test-Path $currDesk) { $userDesktopPaths += $currDesk }
        }

        $filterPatterns = switch ($ShortcutName) {
            "Word"          { @("*Word*.lnk") }
            "Excel"         { @("*Excel*.lnk") }
            "Adobe Acrobat" { @("*Adobe Acrobat*.lnk", "*Acrobat*.lnk", "*Adobe Reader*.lnk") }
            default         { @("$ShortcutName*.lnk") }
        }

        foreach ($ud in ($userDesktopPaths | Select-Object -Unique)) {
            if ($ud -and (Test-Path $ud)) {
                foreach ($pattern in $filterPatterns) {
                    Get-ChildItem -Path $ud -Filter $pattern -ErrorAction SilentlyContinue | ForEach-Object {
                        try {
                            Remove-Item $_.FullName -Force -ErrorAction SilentlyContinue
                            if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                                Write-ServisLog "Uklonjen duplikat precice iz korisnickog profila: $($_.FullName)" -Level "DEBUG" -Component "Shortcuts"
                            }
                        } catch {}
                    }
                }
            }
        }

        $publicDesk = "C:\Users\Public\Desktop"
        if (-not (Test-Path $publicDesk)) {
            try { $publicDesk = [Environment]::GetFolderPath('CommonDesktopDirectory') } catch {}
        }
        if (Test-Path $publicDesk) {
            foreach ($pattern in $filterPatterns) {
                Get-ChildItem -Path $publicDesk -Filter $pattern -ErrorAction SilentlyContinue | ForEach-Object {
                    if ($_.Name -ne "$ShortcutName.lnk") {
                        try {
                            Remove-Item $_.FullName -Force -ErrorAction SilentlyContinue
                            if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                                Write-ServisLog "Uklonjena alternativna precica iz Public Desktop: $($_.FullName)" -Level "DEBUG" -Component "Shortcuts"
                            }
                        } catch {}
                    }
                }
            }
        }

        if (Test-Path $publicDesk) {
            try {
                $wsh = New-Object -ComObject WScript.Shell
                $lnkPath = Join-Path $publicDesk "$ShortcutName.lnk"
                $sc = $wsh.CreateShortcut($lnkPath)
                $sc.TargetPath = $TargetExe
                $sc.WorkingDirectory = (Split-Path $TargetExe -Parent)
                $sc.Save()
                if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                    Write-ServisLog "Kreirana jedinstvena precica u Public Desktop: $lnkPath -> $TargetExe" -Level "SUCCESS" -Component "Shortcuts"
                }
            } catch {
                if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                    Write-ServisLog "Greska pri kreiranju precice u $($publicDesk): $($_.Exception.Message)" -Level "WARN" -Component "Shortcuts"
                }
            }
        }
    }
}

function Refresh-ExplorerShell {
    try {
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
        [ShellNotifier]::Refresh()
        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
            Write-ServisLog "Explorer Shell osvezen (SHChangeNotify uspesno pozvan)." -Level "DEBUG" -Component "Shortcuts"
        }
    } catch {}
}

function Remove-WinRARDesktopShortcut {
    $desktopPaths = @(
        "C:\Users\Public\Desktop",
        "C:\Users\Korisnik\Desktop",
        "$env:USERPROFILE\Desktop"
    )
    try {
        $commonDesk = [Environment]::GetFolderPath('CommonDesktopDirectory')
        if ($commonDesk) { $desktopPaths += $commonDesk }
        $userDesk = [Environment]::GetFolderPath('Desktop')
        if ($userDesk) { $desktopPaths += $userDesk }
    } catch {}
    try {
        Get-ChildItem -Path "C:\Users" -Directory -ErrorAction SilentlyContinue | ForEach-Object {
            $uDesk = Join-Path $_.FullName "Desktop"
            if (Test-Path $uDesk) { $desktopPaths += $uDesk }
        }
    } catch {}

    foreach ($dp in ($desktopPaths | Select-Object -Unique)) {
        if ($dp -and (Test-Path $dp)) {
            Get-ChildItem -Path $dp -Filter "*WinRAR*.lnk" -ErrorAction SilentlyContinue | ForEach-Object {
                Remove-Item $_.FullName -Force -ErrorAction SilentlyContinue
            }
        }
    }
}

function Create-ServisShortcuts {
    # 1. Word
    $wordCandidates = @(
        "C:\Program Files\Microsoft Office\root\Office16\WINWORD.EXE",
        "C:\Program Files (x86)\Microsoft Office\root\Office16\WINWORD.EXE",
        "C:\Program Files\Microsoft Office\Office16\WINWORD.EXE"
    )
    $wordExe = $wordCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
    if ($wordExe) { 
        New-AppShortcut -TargetExe $wordExe -ShortcutName "Word"
        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
            Write-ServisLog "Kreirana precica za Word sa $wordExe" -Level "SUCCESS" -Component "Shortcuts"
        }
    }

    # 2. Excel
    $excelCandidates = @(
        "C:\Program Files\Microsoft Office\root\Office16\EXCEL.EXE",
        "C:\Program Files (x86)\Microsoft Office\root\Office16\EXCEL.EXE",
        "C:\Program Files\Microsoft Office\Office16\EXCEL.EXE"
    )
    $excelExe = $excelCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
    if ($excelExe) { 
        New-AppShortcut -TargetExe $excelExe -ShortcutName "Excel"
        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
            Write-ServisLog "Kreirana precica za Excel sa $excelExe" -Level "SUCCESS" -Component "Shortcuts"
        }
    }

    # 3. Adobe Acrobat
    $acrobatCandidates = @(
        "C:\Program Files\Adobe\Acrobat DC\Acrobat\Acrobat.exe",
        "C:\Program Files (x86)\Adobe\Acrobat DC\Acrobat\Acrobat.exe",
        "C:\Program Files\Adobe\Acrobat 2024\Acrobat\Acrobat.exe",
        "C:\Program Files (x86)\Adobe\Acrobat 2024\Acrobat\Acrobat.exe"
    )
    $acrobatExe = $acrobatCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
    if ($acrobatExe) { 
        New-AppShortcut -TargetExe $acrobatExe -ShortcutName "Adobe Acrobat"
        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
            Write-ServisLog "Kreirana precica za Adobe Acrobat sa $acrobatExe" -Level "SUCCESS" -Component "Shortcuts"
        }
    }

    # 4. Uklanjanje WinRAR precice sa radne povrsine
    Remove-WinRARDesktopShortcut

    # 5. Osvezavanje Explorer Shell-a
    Refresh-ExplorerShell
}

function Set-AcrobatDefaultPDF {
    $acrobatCandidates = @(
        "C:\Program Files\Adobe\Acrobat DC\Acrobat\Acrobat.exe",
        "C:\Program Files (x86)\Adobe\Acrobat DC\Acrobat\Acrobat.exe",
        "C:\Program Files\Adobe\Acrobat 2024\Acrobat\Acrobat.exe",
        "C:\Program Files (x86)\Adobe\Acrobat 2024\Acrobat\Acrobat.exe",
        "C:\Program Files\Adobe\Acrobat Pro\Acrobat.exe",
        "C:\Program Files (x86)\Adobe\Acrobat Pro\Acrobat.exe",
        "C:\Program Files\Adobe\Acrobat\Acrobat.exe",
        "C:\Program Files (x86)\Adobe\Acrobat\Acrobat.exe"
    )
    $acrobatExe = $acrobatCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $acrobatExe) {
        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
            Write-ServisLog "Set-AcrobatDefaultPDF: Adobe Acrobat (Acrobat.exe) nije instaliran na disku — preskacem upis GPO/PDF asocijacija." -Level "WARN" -Component "PdfDefault"
        }
        return $false
    }

    try {
        $assocXmlPath = "C:\Windows\System32\AppAssoc_Acrobat.xml"
        $assocContent = @'
<?xml version="1.0" encoding="UTF-8"?>
<DefaultAssociations>
  <Association Identifier=".pdf" ProgId="AcroExch.Document.DC" ApplicationName="Adobe Acrobat" />
  <Association Identifier=".pdfxml" ProgId="AcroExch.Document.DC" ApplicationName="Adobe Acrobat" />
  <Association Identifier=".acrobatsecuritysettings" ProgId="AcroExch.Document.DC" ApplicationName="Adobe Acrobat" />
  <Association Identifier=".fdf" ProgId="AcroExch.Document.DC" ApplicationName="Adobe Acrobat" />
  <Association Identifier=".xfdf" ProgId="AcroExch.Document.DC" ApplicationName="Adobe Acrobat" />
</DefaultAssociations>
'@
        Set-Content -Path $assocXmlPath -Value $assocContent -Encoding UTF8 -Force

        try {
            Start-Process -FilePath "dism.exe" -ArgumentList "/Online /Import-DefaultAppAssociations:`"$assocXmlPath`"" -WindowStyle Hidden -Wait
        } catch {}

        $gpoSys = "HKLM:\SOFTWARE\Policies\Microsoft\Windows\System"
        if (-not (Test-Path $gpoSys)) { New-Item -Path $gpoSys -Force | Out-Null }
        Set-ItemProperty -Path $gpoSys -Name "DefaultAssociationsConfiguration" -Value $assocXmlPath -Force

        $adobeLock = "HKLM:\SOFTWARE\Policies\Adobe\Adobe Acrobat\DC\FeatureLockDown"
        if (-not (Test-Path $adobeLock)) { New-Item -Path $adobeLock -Force | Out-Null }
        Set-ItemProperty -Path $adobeLock -Name "bDisablePDFHandlerSwitching" -Value 1 -Type DWord -Force

        Remove-Item -Path "HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\.pdf\UserChoice" -Recurse -Force -ErrorAction SilentlyContinue

        try {
            New-Item -Path "HKLM:\SOFTWARE\Classes\.pdf" -Force -ErrorAction SilentlyContinue | Out-Null
            Set-ItemProperty -Path "HKLM:\SOFTWARE\Classes\.pdf" -Name "(Default)" -Value "AcroExch.Document.DC" -Force -ErrorAction SilentlyContinue
        } catch {}

        cmd.exe /c "assoc .pdf=AcroExch.Document.DC" | Out-Null
        if (Test-Path $acrobatExe) {
            cmd.exe /c "ftype AcroExch.Document.DC=`"$acrobatExe`" `"%1`"" | Out-Null
        }
        return $true
    } catch {
        return $false
    }
}

function Get-WindowsLicenseStatus {
    <#
    .SYNOPSIS
        Vraća detaljan status licenciranja operativnog sistema Windows (WMI/CIM SoftwareLicensingProduct + slmgr.vbs /xpr fallback).
    #>
    [CmdletBinding()]
    param()

    $statusObj = [PSCustomObject]@{
        Activated           = $false
        Method              = "NONE"
        Edition             = ""
        SkuName             = ""
        PartialProductKey   = ""
        LicenseStatus       = -1
        LicenseStatusText   = ""
        LicenseStatusReason = ""
        Oa3xPresent         = $false
        Oa3xDescription     = ""
    }

    try {
        $osInfo = Get-CimInstance -ClassName Win32_OperatingSystem -ErrorAction SilentlyContinue
        if (-not $osInfo) {
            $osInfo = Get-WmiObject -Class Win32_OperatingSystem -ErrorAction SilentlyContinue
        }
        if ($osInfo -and -not [string]::IsNullOrWhiteSpace([string]$osInfo.Caption)) {
            $statusObj.Edition = ([string]$osInfo.Caption).Trim()
        }
    } catch {}

    if ([string]::IsNullOrWhiteSpace($statusObj.Edition)) {
        try {
            $cv = Get-ItemProperty -Path "HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion" -ErrorAction SilentlyContinue
            if ($cv) {
                if (-not [string]::IsNullOrWhiteSpace([string]$cv.ProductName)) {
                    $statusObj.Edition = ([string]$cv.ProductName).Trim()
                } elseif (-not [string]::IsNullOrWhiteSpace([string]$cv.EditionID)) {
                    $statusObj.Edition = ([string]$cv.EditionID).Trim()
                }
            }
        } catch {}
    }

    try {
        $sls = Get-CimInstance -ClassName SoftwareLicensingService -ErrorAction SilentlyContinue
        if (-not $sls) {
            $sls = Get-WmiObject -Class SoftwareLicensingService -ErrorAction SilentlyContinue
        }
        if ($sls) {
            $statusObj.Oa3xPresent = (-not [string]::IsNullOrWhiteSpace([string]$sls.OA3xOriginalProductKey))
            if (-not [string]::IsNullOrWhiteSpace([string]$sls.OA3xOriginalProductKeyDescription)) {
                $statusObj.Oa3xDescription = ([string]$sls.OA3xOriginalProductKeyDescription).Trim()
            }
        }
    } catch {}

    # 1. Primarna WMI/CIM provera SoftwareLicensingProduct za Windows (ApplicationID = 55c92734-d682-4d71-98e3-d6ec3f16059f)
    $winAppId = "55c92734-d682-4d71-98e3-d6ec3f16059f"
    $wmiFallback = $null
    try {
        $winLics = @(Get-CimInstance -ClassName SoftwareLicensingProduct -Filter "ApplicationID = '$winAppId'" -ErrorAction SilentlyContinue |
            Where-Object { $null -ne $_.PartialProductKey -and -not [string]::IsNullOrWhiteSpace([string]$_.PartialProductKey) })
        if ($winLics.Count -eq 0) {
            $winLics = @(Get-WmiObject -Class SoftwareLicensingProduct -Filter "ApplicationID = '$winAppId'" -ErrorAction SilentlyContinue |
                Where-Object { $null -ne $_.PartialProductKey -and -not [string]::IsNullOrWhiteSpace([string]$_.PartialProductKey) })
        }

        $licensedSku = $winLics | Where-Object { [int]$_.LicenseStatus -eq 1 } | Select-Object -First 1
        if ($licensedSku) {
            $statusObj.Activated = $true
            $statusObj.Method = "WMI_SPP"
            $statusObj.SkuName = if ($licensedSku.Name) { [string]$licensedSku.Name } else { [string]$licensedSku.Description }
            $statusObj.PartialProductKey = [string]$licensedSku.PartialProductKey
            $statusObj.LicenseStatus = 1
            $statusObj.LicenseStatusText = "Licensed"
            if ($null -ne $licensedSku.LicenseStatusReason -and [uint32]$licensedSku.LicenseStatusReason -ne 0) {
                $statusObj.LicenseStatusReason = ('0x{0:X8}' -f [uint32]$licensedSku.LicenseStatusReason)
            }
            return $statusObj
        }

        if ($winLics.Count -gt 0) {
            $firstWin = $winLics | Select-Object -First 1
            $licCode = [int]$firstWin.LicenseStatus
            $licLabel = switch ($licCode) {
                0 { "Unlicensed" }
                1 { "Licensed" }
                2 { "OOBGrace" }
                3 { "OOTGrace" }
                4 { "NonGenuineGrace" }
                5 { "Notification" }
                6 { "ExtendedGrace" }
                default { "Status_$licCode" }
            }
            $reasonHex = ""
            if ($null -ne $firstWin.LicenseStatusReason -and [uint32]$firstWin.LicenseStatusReason -ne 0) {
                $reasonHex = ('0x{0:X8}' -f [uint32]$firstWin.LicenseStatusReason)
            }
            $wmiFallback = [PSCustomObject]@{
                SkuName             = if ($firstWin.Name) { [string]$firstWin.Name } else { [string]$firstWin.Description }
                PartialProductKey   = [string]$firstWin.PartialProductKey
                LicenseStatus       = $licCode
                LicenseStatusText   = $licLabel
                LicenseStatusReason = $reasonHex
            }
        }
    } catch {}

    # 2. Fallback: slmgr.vbs /xpr preko Invoke-CscriptWithTimeout (25s)
    $slmgrPath = "C:\Windows\System32\slmgr.vbs"
    if (Test-Path -LiteralPath $slmgrPath -PathType Leaf -ErrorAction SilentlyContinue) {
        try {
            $xprRes = Invoke-CscriptWithTimeout -ScriptPath $slmgrPath -Arguments @("/xpr") -TimeoutSec 25
            $xprOut = Protect-WisLicenseLogText -Text (([string]$xprRes.Output -replace '\r?\n', ' ').Trim())
            if ($xprRes.TimedOut) {
                if ($wmiFallback) {
                    $statusObj.Method = "WMI_SPP"
                    $statusObj.SkuName = $wmiFallback.SkuName
                    $statusObj.PartialProductKey = $wmiFallback.PartialProductKey
                    $statusObj.LicenseStatus = $wmiFallback.LicenseStatus
                    $statusObj.LicenseStatusText = $wmiFallback.LicenseStatusText
                    $statusObj.LicenseStatusReason = $wmiFallback.LicenseStatusReason
                } else {
                    $statusObj.Method = "SLMGR_TIMEOUT"
                    $statusObj.LicenseStatusText = "slmgr.vbs /xpr timeout (25s)"
                }
                return $statusObj
            }

            if ($xprOut -match '(?i)(permanently activated|volume activation will expire|is activated|trajno aktiviran|aktiviran)' -and
                $xprOut -notmatch '(?i)(not activated|nije aktiviran|notification|grace)') {
                $statusObj.Activated = $true
                $statusObj.Method = "SLMGR_XPR"
                if ($wmiFallback) {
                    $statusObj.SkuName = $wmiFallback.SkuName
                    $statusObj.PartialProductKey = $wmiFallback.PartialProductKey
                }
                $statusObj.LicenseStatus = 1
                $statusObj.LicenseStatusText = if ($xprOut) { $xprOut } else { "Activated" }
                return $statusObj
            }

            if (-not $wmiFallback -and $xprOut) {
                $statusObj.Method = "SLMGR_XPR"
                $statusObj.LicenseStatusText = $xprOut
                return $statusObj
            }
        } catch {}
    }

    if ($wmiFallback) {
        $statusObj.Activated = $false
        $statusObj.Method = "WMI_SPP"
        $statusObj.SkuName = $wmiFallback.SkuName
        $statusObj.PartialProductKey = $wmiFallback.PartialProductKey
        $statusObj.LicenseStatus = $wmiFallback.LicenseStatus
        $statusObj.LicenseStatusText = $wmiFallback.LicenseStatusText
        $statusObj.LicenseStatusReason = $wmiFallback.LicenseStatusReason
    }

    return $statusObj
}

function Test-WindowsActivated {
    try {
        $st = Get-WindowsLicenseStatus
        return [bool]($st -and $st.Activated)
    } catch {}
    return $false
}

function Test-OfficeInstalled {
    <#
    .SYNOPSIS
        Proverava da li je Microsoft Office instaliran na jednoj od 64-bit ili 32-bit C2R/MSI putanja.
    #>
    [CmdletBinding()]
    param()

    $officeExePaths = @(
        "C:\Program Files\Microsoft Office\root\Office16\WINWORD.EXE",
        "C:\Program Files (x86)\Microsoft Office\root\Office16\WINWORD.EXE",
        "C:\Program Files\Microsoft Office\Office16\WINWORD.EXE",
        "C:\Program Files (x86)\Microsoft Office\Office16\WINWORD.EXE",
        "C:\Program Files\Microsoft Office\root\Office16\EXCEL.EXE",
        "C:\Program Files (x86)\Microsoft Office\root\Office16\EXCEL.EXE",
        "C:\Program Files\Microsoft Office\Office16\EXCEL.EXE",
        "C:\Program Files (x86)\Microsoft Office\Office16\EXCEL.EXE"
    )
    foreach ($p in $officeExePaths) {
        if (Test-Path -LiteralPath $p -ErrorAction SilentlyContinue) {
            return $true
        }
    }
    return $false
}

function Protect-WisLicenseLogText {
    <#
    .SYNOPSIS
        Maskira 25-karakterne product ključeve (i opcione KMS hostove) u tekstu radi bezbednog logovanja.
    #>
    [CmdletBinding()]
    param(
        [AllowEmptyString()]
        [AllowNull()]
        [string]$Text = "",
        [string[]]$SensitiveHosts = @()
    )

    if ([string]::IsNullOrEmpty($Text)) {
        return ""
    }
    $masked = $Text -replace '(?i)\b([A-Z0-9]{5}-){4}([A-Z0-9]{5})\b', '*****-*****-*****-*****-$2'
    foreach ($sh in $SensitiveHosts) {
        if (-not [string]::IsNullOrWhiteSpace($sh)) {
            $masked = $masked -replace [regex]::Escape($sh.Trim()), '***KMS_HOST_MASKIRANO***'
        }
    }
    return $masked
}

function Format-Win32CommandLineArgument {
    <#
    .SYNOPSIS
        Ispravno citira argument za Win32 CommandLineToArgvW (udvostručuje backslash-ove ispred navodnika i na kraju stringa).
    #>
    param(
        [AllowEmptyString()]
        [AllowNull()]
        [string]$Value = "",
        [switch]$AlwaysQuote
    )

    if ($null -eq $Value) { return '""' }
    $strVal = [string]$Value
    $needsQuote = $AlwaysQuote -or ($strVal.Length -eq 0) -or ($strVal -match '[\s"\\''&|<>()^%;]')
    if (-not $needsQuote) {
        return $strVal
    }
    $escaped = $strVal -replace '(\\*)"', '$1$1\"'
    $escaped = $escaped -replace '(\\+)$', '$1$1'
    return ('"' + $escaped + '"')
}

function Copy-WisSafeDirectoryTree {
    <#
    .SYNOPSIS
        Bezbedno rekurzivno kopiranje direktorijuma bez praćenja ReparsePoint (simlink/junction) stavki i neželjenih fajlova.
    #>
    param(
        [Parameter(Mandatory = $true)]
        [string]$SourceDir,
        [Parameter(Mandatory = $true)]
        [string]$DestinationDir
    )

    $resolvedSrc = [System.IO.Path]::GetFullPath($SourceDir).TrimEnd('\')
    $resolvedDst = [System.IO.Path]::GetFullPath($DestinationDir).TrimEnd('\')
    $dstPrefix = $resolvedDst + '\'

    $srcItem = Get-Item -LiteralPath $resolvedSrc -Force -ErrorAction Stop
    if (($srcItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Izvorni direktorijum je ReparsePoint (simlink/junction) i odbijen je: $resolvedSrc"
    }
    if (-not (Test-Path -LiteralPath $resolvedDst -PathType Container -ErrorAction SilentlyContinue)) {
        New-Item -Path $resolvedDst -ItemType Directory -Force -ErrorAction Stop | Out-Null
    }

    $skipFiles = @("thumbs.db", "desktop.ini", ".ds_store")
    $children = @(Get-ChildItem -LiteralPath $resolvedSrc -Force -ErrorAction Stop)
    foreach ($child in $children) {
        if (($child.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                Write-ServisLog "Preskačem ReparsePoint (simlink/junction) stavku pri keširanju: $($child.FullName)" -Level "WARN" -Component "License"
            }
            continue
        }
        if ($child.Name -eq "." -or $child.Name -eq ".." -or $child.Name.IndexOfAny([System.IO.Path]::GetInvalidFileNameChars()) -ge 0) {
            continue
        }
        $targetChild = [System.IO.Path]::GetFullPath((Join-Path $resolvedDst $child.Name))
        if (-not $targetChild.StartsWith($dstPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
            continue
        }
        if ($child.PSIsContainer) {
            Copy-WisSafeDirectoryTree -SourceDir $child.FullName -DestinationDir $targetChild
        } else {
            if ($skipFiles -contains $child.Name.ToLowerInvariant()) { continue }
            Copy-Item -LiteralPath $child.FullName -Destination $targetChild -Force -ErrorAction Stop
        }
    }
}

function Invoke-ProcessStartInfoWithTimeout {
    <#
    .SYNOPSIS
        Interni pomoćnik za bezbedno izvršavanje ProcessStartInfo-a sa asinhronim čitanjem izlaza, timeoutom i taskkill čišćenjem.
    #>
    param(
        [Parameter(Mandatory = $true)]
        [System.Diagnostics.ProcessStartInfo]$StartInfo,
        [int]$TimeoutSec = 25,
        [string]$Label = ""
    )

    $proc = $null
    $timedOut = $false
    $exitCode = -1
    $stdoutText = ""
    $stderrText = ""

    try {
        $proc = New-Object System.Diagnostics.Process
        $proc.StartInfo = $StartInfo
        if (-not $proc.Start()) {
            return [PSCustomObject]@{
                Success  = $false
                TimedOut = $false
                ExitCode = -1
                Output   = "Neuspešno pokretanje procesa: $($StartInfo.FileName)"
            }
        }

        $stdoutTask = $proc.StandardOutput.ReadToEndAsync()
        $stderrTask = $proc.StandardError.ReadToEndAsync()
        $waitMs = [math]::Max(1000, ($TimeoutSec * 1000))
        $exited = $proc.WaitForExit($waitMs)

        if (-not $exited) {
            $timedOut = $true
            if ($Label -and (Get-Command Write-ServisLog -ErrorAction SilentlyContinue)) {
                Write-ServisLog "Proces '$Label' je prekoračio timeout (${TimeoutSec}s). Prekidam stablo procesa (PID=$($proc.Id))..." -Level "WARN" -Component "License"
            }
            try {
                Start-Process -FilePath "taskkill.exe" -ArgumentList "/PID $($proc.Id) /T /F" -WindowStyle Hidden -Wait -ErrorAction SilentlyContinue | Out-Null
            } catch {}
            try {
                if (-not $proc.HasExited) { $proc.Kill() }
            } catch {}
            try { $null = $proc.WaitForExit(2000) } catch {}
        }

        try {
            if ($stdoutTask.Wait(1500)) { $stdoutText = $stdoutTask.Result }
        } catch {}
        try {
            if ($stderrTask.Wait(1500)) { $stderrText = $stderrTask.Result }
        } catch {}

        if (-not $timedOut) {
            try { $exitCode = [int]$proc.ExitCode } catch { $exitCode = -1 }
        }
    } catch {
        $stderrText = $_.Exception.Message
    } finally {
        if ($proc) {
            try { $proc.Dispose() } catch {}
        }
    }

    $parts = @()
    if (-not [string]::IsNullOrWhiteSpace($stdoutText)) { $parts += $stdoutText.Trim() }
    if (-not [string]::IsNullOrWhiteSpace($stderrText)) { $parts += $stderrText.Trim() }
    $safeOut = Protect-WisLicenseLogText -Text ($parts -join "`n")

    return [PSCustomObject]@{
        Success  = ((-not $timedOut) -and ($exitCode -eq 0))
        TimedOut = $timedOut
        ExitCode = [int]$exitCode
        Output   = $safeOut
    }
}

function Invoke-CscriptWithTimeout {
    <#
    .SYNOPSIS
        Izvršava VBScript (slmgr.vbs / OSPP.VBS) preko cscript.exe sa strogim timeoutom i sanitizovanim izlazom.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$ScriptPath,
        [string[]]$Arguments = @(),
        [int]$TimeoutSec = 25
    )

    if ([string]::IsNullOrWhiteSpace($ScriptPath) -or ($ScriptPath -match '[\r\n\0"]') -or -not (Test-Path -LiteralPath $ScriptPath -PathType Leaf -ErrorAction SilentlyContinue)) {
        return [PSCustomObject]@{
            Success  = $false
            TimedOut = $false
            ExitCode = -1
            Output   = "Skripta nije pronađena ili je neispravna putanja: $ScriptPath"
        }
    }

    $resolvedScript = [System.IO.Path]::GetFullPath($ScriptPath)
    $cscriptExe = if (Test-Path -LiteralPath "C:\Windows\System32\cscript.exe" -PathType Leaf -ErrorAction SilentlyContinue) {
        "C:\Windows\System32\cscript.exe"
    } else {
        "cscript.exe"
    }

    $argTokens = [System.Collections.Generic.List[string]]::new()
    $argTokens.Add("//nologo")
    $argTokens.Add((Format-Win32CommandLineArgument -Value $resolvedScript -AlwaysQuote))
    foreach ($a in $Arguments) {
        if ($null -ne $a) {
            $s = [string]$a
            if (-not [string]::IsNullOrWhiteSpace($s)) {
                $argTokens.Add((Format-Win32CommandLineArgument -Value $s))
            }
        }
    }

    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $cscriptExe
    $psi.Arguments = ($argTokens -join ' ')
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.CreateNoWindow = $true
    $psi.WorkingDirectory = if (Test-Path -LiteralPath "C:\setup" -ErrorAction SilentlyContinue) { "C:\setup" } else { "C:\Windows\System32" }

    return (Invoke-ProcessStartInfoWithTimeout -StartInfo $psi -TimeoutSec $TimeoutSec)
}

function Get-OfficeLicenseStatus {
    <#
    .SYNOPSIS
        Vraća detaljan status licenciranja Microsoft Office paketa (VFS/SPP, WMI SoftwareLicensingProduct i OSPP.VBS /dstatus).
    #>
    [CmdletBinding()]
    param()

    $statusObj = [PSCustomObject]@{
        Activated         = $false
        Method            = "NONE"
        SkuName           = ""
        PartialProductKey = ""
        LicenseStatusText = ""
        LastErrorCode     = ""
    }

    # 1. Provera lokalnih VFS / SPP biblioteka licenciranja (sppc.dll / sppcs.dll > 5120 B)
    $vfsSppPaths = @(
        "C:\Program Files\Microsoft Office\root\vfs\System\sppc.dll",
        "C:\Program Files (x86)\Microsoft Office\root\vfs\SystemX86\sppc.dll",
        "C:\Program Files\Microsoft Office\root\vfs\SystemX86\sppc.dll",
        "C:\Program Files\Common Files\Microsoft Shared\OfficeSoftwareProtectionPlatform\sppcs.dll"
    )
    foreach ($dll in $vfsSppPaths) {
        if (Test-Path -LiteralPath $dll -ErrorAction SilentlyContinue) {
            try {
                $fItem = Get-Item -LiteralPath $dll -ErrorAction SilentlyContinue
                if ($fItem -and $fItem.Length -gt 5120) {
                    $statusObj.Activated = $true
                    $statusObj.Method = "VFS_SPP"
                    $statusObj.SkuName = "Microsoft Office (VFS/SPP)"
                    $statusObj.LicenseStatusText = "Licensed (VFS_SPP)"
                    return $statusObj
                }
            } catch {}
        }
    }

    # 2. Provera WMI / CIM SoftwareLicensingProduct za *Office* SKU-ove (razlikuje Licensed=1 od Grace/Notification/Unlicensed)
    $wmiFallback = $null
    try {
        $officeLics = @(Get-CimInstance -ClassName SoftwareLicensingProduct -Filter "PartialProductKey IS NOT NULL" -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -like "*Office*" -or $_.Description -like "*Office*" })

        $licensedSku = $officeLics | Where-Object { [int]$_.LicenseStatus -eq 1 } | Select-Object -First 1
        if ($licensedSku) {
            $statusObj.Activated = $true
            $statusObj.Method = "WMI_SPP"
            $statusObj.SkuName = if ($licensedSku.Name) { [string]$licensedSku.Name } else { [string]$licensedSku.Description }
            $statusObj.PartialProductKey = if ($licensedSku.PartialProductKey) { [string]$licensedSku.PartialProductKey } else { "" }
            $statusObj.LicenseStatusText = "Licensed"
            $statusObj.LastErrorCode = ""
            return $statusObj
        }

        if ($officeLics.Count -gt 0) {
            $firstUnlic = $officeLics | Select-Object -First 1
            $licCode = [int]$firstUnlic.LicenseStatus
            $licLabel = switch ($licCode) {
                0 { "Unlicensed" }
                1 { "Licensed" }
                2 { "OOBGrace" }
                3 { "OOTGrace" }
                4 { "NonGenuineGrace" }
                5 { "Notification" }
                6 { "ExtendedGrace" }
                default { "Status_$licCode" }
            }
            $wmiFallback = [PSCustomObject]@{
                SkuName           = if ($firstUnlic.Name) { [string]$firstUnlic.Name } else { [string]$firstUnlic.Description }
                PartialProductKey = if ($firstUnlic.PartialProductKey) { [string]$firstUnlic.PartialProductKey } else { "" }
                LicenseStatusText = $licLabel
            }
        }
    } catch {}

    # 3. Provera OSPP.VBS /dstatus preko Invoke-CscriptWithTimeout (20s)
    $osppPaths = @(
        "C:\Program Files\Microsoft Office\Office16\OSPP.VBS",
        "C:\Program Files\Microsoft Office\root\Office16\OSPP.VBS",
        "C:\Program Files (x86)\Microsoft Office\Office16\OSPP.VBS",
        "C:\Program Files (x86)\Microsoft Office\root\Office16\OSPP.VBS"
    )
    $ospp = $osppPaths | Where-Object { Test-Path -LiteralPath $_ -ErrorAction SilentlyContinue } | Select-Object -First 1
    if ($ospp) {
        try {
            $dstatusRes = Invoke-CscriptWithTimeout -ScriptPath $ospp -Arguments @("/dstatus") -TimeoutSec 20
            if ($dstatusRes.TimedOut) {
                $statusObj.Method = "OSPP_TIMEOUT"
                $statusObj.LicenseStatusText = "OSPP.VBS /dstatus timeout (20s)"
                if ($wmiFallback) {
                    $statusObj.SkuName = $wmiFallback.SkuName
                    $statusObj.PartialProductKey = $wmiFallback.PartialProductKey
                }
                return $statusObj
            }

            $outText = [string]$dstatusRes.Output
            if (-not [string]::IsNullOrWhiteSpace($outText)) {
                $blocks = @($outText -split '-{5,}')
                $parsedBlocks = [System.Collections.Generic.List[object]]::new()
                foreach ($blk in $blocks) {
                    if ([string]::IsNullOrWhiteSpace($blk)) { continue }
                    $sku = ""
                    if ($blk -match '(?mi)LICENSE NAME:\s*(.+)') {
                        $sku = $matches[1].Trim()
                    } elseif ($blk -match '(?mi)SKU(?:\s+ID)?:\s*(.+)') {
                        $sku = $matches[1].Trim()
                    }
                    $licStat = ""
                    if ($blk -match '(?mi)LICENSE STATUS:\s*(.+)') {
                        $licStat = $matches[1].Trim()
                    }
                    $pKey = ""
                    if ($blk -match '(?mi)Last 5 characters of installed product key:\s*([A-Z0-9]{5})') {
                        $pKey = $matches[1].Trim()
                    }
                    $errCode = ""
                    if ($blk -match '(?mi)ERROR CODE:\s*(0x[0-9A-Fa-f]+|-?\d+)') {
                        $errCode = $matches[1].Trim()
                    }
                    $errDesc = ""
                    if ($blk -match '(?mi)ERROR DESCRIPTION:\s*(.+)') {
                        $errDesc = $matches[1].Trim()
                    }
                    if ($sku -or $licStat -or $pKey -or $errCode) {
                        $isLicensedBlk = ($licStat -match '(?i)---LICENSED---')
                        $parsedBlocks.Add([PSCustomObject]@{
                            Activated = $isLicensedBlk
                            SkuName   = $sku
                            Partial   = $pKey
                            StatusTxt = $licStat
                            ErrCode   = $errCode
                            ErrDesc   = $errDesc
                        })
                    }
                }

                $winBlk = $parsedBlocks | Where-Object { $_.Activated } | Select-Object -First 1
                if ($winBlk) {
                    $statusObj.Activated = $true
                    $statusObj.Method = "OSPP"
                    $statusObj.SkuName = $winBlk.SkuName
                    $statusObj.PartialProductKey = $winBlk.Partial
                    $statusObj.LicenseStatusText = $winBlk.StatusTxt
                    $statusObj.LastErrorCode = ""
                    return $statusObj
                }

                $bestFailBlk = $parsedBlocks | Where-Object { -not [string]::IsNullOrWhiteSpace($_.ErrCode) } | Select-Object -First 1
                if (-not $bestFailBlk) {
                    $bestFailBlk = $parsedBlocks | Select-Object -First 1
                }
                if ($bestFailBlk) {
                    $statusObj.Activated = $false
                    $statusObj.Method = "OSPP"
                    $statusObj.SkuName = if ($bestFailBlk.SkuName) { $bestFailBlk.SkuName } elseif ($wmiFallback) { $wmiFallback.SkuName } else { "" }
                    $statusObj.PartialProductKey = if ($bestFailBlk.Partial) { $bestFailBlk.Partial } elseif ($wmiFallback) { $wmiFallback.PartialProductKey } else { "" }
                    $statusTxtOut = $bestFailBlk.StatusTxt
                    if ($bestFailBlk.ErrDesc) {
                        $statusTxtOut = if ($statusTxtOut) { "$statusTxtOut ($($bestFailBlk.ErrDesc))" } else { $bestFailBlk.ErrDesc }
                    }
                    $statusObj.LicenseStatusText = Protect-WisLicenseLogText -Text $statusTxtOut
                    $statusObj.LastErrorCode = $bestFailBlk.ErrCode
                    return $statusObj
                }
            }
        } catch {}
    }

    if ($wmiFallback) {
        $statusObj.Activated = $false
        $statusObj.Method = "WMI_SPP"
        $statusObj.SkuName = $wmiFallback.SkuName
        $statusObj.PartialProductKey = $wmiFallback.PartialProductKey
        $statusObj.LicenseStatusText = $wmiFallback.LicenseStatusText
        return $statusObj
    }

    return $statusObj
}

function Test-OfficeActivated {
    try {
        $st = Get-OfficeLicenseStatus
        return [bool]($st -and $st.Activated)
    } catch {
        return $false
    }
}

function Get-WisLicensingSettingsLocal {
    <#
    .SYNOPSIS
        Čita [Licensing] podešavanja iz WIS.Core ili preko bezbednog lokalnog INI fallback parsera.
    #>
    [CmdletBinding()]
    param()

    $normalizeLicMode = {
        param([string]$RawMode)
        $m = if ($RawMode) { $RawMode.Trim() } else { "" }
        switch -Regex ($m) {
            '(?i)^kms$'          { "Kms" }
            '(?i)^mak$'          { "Mak" }
            '(?i)^externalhook$' { "ExternalHook" }
            '(?i)^skip$'         { "Skip" }
            default              { "Auto" }
        }
    }

    if (Get-Command Get-ServisLicensingSettings -ErrorAction SilentlyContinue) {
        try {
            $coreLic = Get-ServisLicensingSettings
            if ($coreLic) {
                $rawCoreMode = if ($coreLic.PSObject.Properties['OfficeActivationMode']) { [string]$coreLic.OfficeActivationMode } else { "Auto" }
                $normCoreMode = & $normalizeLicMode $rawCoreMode
                $portVal = 1688
                if ($coreLic.PSObject.Properties['OfficeKmsPort'] -and $coreLic.OfficeKmsPort) {
                    try {
                        $parsedPort = [int]$coreLic.OfficeKmsPort
                        if ($parsedPort -gt 0 -and $parsedPort -le 65535) { $portVal = $parsedPort }
                    } catch {}
                }

                $rawWinMode = if ($coreLic.PSObject.Properties['WindowsActivationMode']) { [string]$coreLic.WindowsActivationMode } else { "Auto" }
                $normWinMode = & $normalizeLicMode $rawWinMode
                $winPortVal = 1688
                if ($coreLic.PSObject.Properties['WindowsKmsPort'] -and $coreLic.WindowsKmsPort) {
                    try {
                        $parsedWinPort = [int]$coreLic.WindowsKmsPort
                        if ($parsedWinPort -gt 0 -and $parsedWinPort -le 65535) { $winPortVal = $parsedWinPort }
                    } catch {}
                }

                return [PSCustomObject]@{
                    Found                 = [bool]$coreLic.Found
                    IniPath               = [string]$coreLic.IniPath
                    OfficeActivationMode  = $normCoreMode
                    OfficeKmsHost         = if ($coreLic.PSObject.Properties['OfficeKmsHost']) { ([string]$coreLic.OfficeKmsHost).Trim() } else { "" }
                    OfficeKmsPort         = $portVal
                    OfficeProductKey      = if ($coreLic.PSObject.Properties['OfficeProductKey']) { ([string]$coreLic.OfficeProductKey).Trim() } else { "" }
                    WindowsActivationMode = $normWinMode
                    WindowsKmsHost        = if ($coreLic.PSObject.Properties['WindowsKmsHost']) { ([string]$coreLic.WindowsKmsHost).Trim() } else { "" }
                    WindowsKmsPort        = $winPortVal
                    WindowsProductKey     = if ($coreLic.PSObject.Properties['WindowsProductKey']) { ([string]$coreLic.WindowsProductKey).Trim() } else { "" }
                }
            }
        } catch {}
    }

    $officeActivationMode = "Auto"
    $officeKmsHost = ""
    $officeKmsPort = 1688
    $officeProductKey = ""
    $windowsActivationMode = "Auto"
    $windowsKmsHost = ""
    $windowsKmsPort = 1688
    $windowsProductKey = ""
    $foundSection = $false

    $licIniCandidates = @(
        "C:\setup\Wifi-Servis.ini",
        "C:\setup\Skripte\Wifi-Servis.ini"
    )
    if ($Global:LocalSetupDir) {
        $licIniCandidates += (Join-Path $Global:LocalSetupDir "Wifi-Servis.ini")
    }
    if ($Global:SourceRoot) {
        $licIniCandidates += (Join-Path $Global:SourceRoot "Wifi-Servis.ini")
        $licIniCandidates += (Join-Path $Global:SourceRoot "Skripte\Wifi-Servis.ini")
    }
    if ($PSScriptRoot) {
        $licIniCandidates += (Join-Path $PSScriptRoot "Wifi-Servis.ini")
        $licIniCandidates += (Join-Path (Split-Path $PSScriptRoot -Parent) "Wifi-Servis.ini")
    }
    $licIniPath = $licIniCandidates | Where-Object { $_ -and (Test-Path -LiteralPath $_ -ErrorAction SilentlyContinue) } | Select-Object -First 1
    if ($licIniPath) {
        try {
            $iniLines = Get-Content -LiteralPath $licIniPath -Encoding UTF8 -ErrorAction SilentlyContinue
            $inLicensingSection = $false
            foreach ($rawLine in $iniLines) {
                $line = $rawLine.Trim()
                if (-not $line -or $line.StartsWith(";") -or $line.StartsWith("#")) { continue }
                if ($line.StartsWith("[") -and $line.EndsWith("]")) {
                    $secName = $line.Substring(1, $line.Length - 2).Trim()
                    $inLicensingSection = ($secName -ieq "Licensing")
                    if ($inLicensingSection) { $foundSection = $true }
                    continue
                }
                if ($inLicensingSection -and $line -match '^\s*([^=]+)=(.*)$') {
                    $k = $matches[1].Trim()
                    $v = ($matches[2] -replace '[;#].*$', '').Trim()
                    if ($k -ieq "OfficeActivationMode" -and -not [string]::IsNullOrWhiteSpace($v)) {
                        $officeActivationMode = & $normalizeLicMode $v
                    } elseif ($k -ieq "OfficeKmsHost" -and -not [string]::IsNullOrWhiteSpace($v) -and $v -ne "CHANGE_ME") {
                        $officeKmsHost = $v
                    } elseif ($k -ieq "OfficeKmsPort" -and -not [string]::IsNullOrWhiteSpace($v)) {
                        try {
                            $pNum = [int]$v
                            if ($pNum -gt 0 -and $pNum -le 65535) { $officeKmsPort = $pNum }
                        } catch {}
                    } elseif ($k -ieq "OfficeProductKey" -and -not [string]::IsNullOrWhiteSpace($v) -and $v -ne "CHANGE_ME" -and $v -ne "promeni_kljuc") {
                        # Strogo zabranjeno logovanje vrednosti OfficeProductKey
                        $officeProductKey = $v
                    } elseif ($k -ieq "WindowsActivationMode" -and -not [string]::IsNullOrWhiteSpace($v)) {
                        $windowsActivationMode = & $normalizeLicMode $v
                    } elseif ($k -ieq "WindowsKmsHost" -and -not [string]::IsNullOrWhiteSpace($v) -and $v -ne "CHANGE_ME") {
                        $windowsKmsHost = $v
                    } elseif ($k -ieq "WindowsKmsPort" -and -not [string]::IsNullOrWhiteSpace($v)) {
                        try {
                            $pwNum = [int]$v
                            if ($pwNum -gt 0 -and $pwNum -le 65535) { $windowsKmsPort = $pwNum }
                        } catch {}
                    } elseif ($k -ieq "WindowsProductKey" -and -not [string]::IsNullOrWhiteSpace($v) -and $v -ne "CHANGE_ME" -and $v -ne "promeni_kljuc") {
                        # Strogo zabranjeno logovanje vrednosti WindowsProductKey
                        $windowsProductKey = $v
                    }
                }
            }
        } catch {}
    }

    return [PSCustomObject]@{
        Found                 = $foundSection
        IniPath               = if ($licIniPath) { [string]$licIniPath } else { "" }
        OfficeActivationMode  = $officeActivationMode
        OfficeKmsHost         = $officeKmsHost
        OfficeKmsPort         = [int]$officeKmsPort
        OfficeProductKey      = $officeProductKey
        WindowsActivationMode = $windowsActivationMode
        WindowsKmsHost        = $windowsKmsHost
        WindowsKmsPort        = [int]$windowsKmsPort
        WindowsProductKey     = $windowsProductKey
    }
}

function Write-WisLicenseStatusFile {
    <#
    .SYNOPSIS
        Atomično upisuje sažetak statusa licenciranja u C:\setup\license-status.json (UTF-8 bez BOM-a, bez ključeva).
    #>
    [CmdletBinding()]
    param(
        [bool]$WindowsActivated,
        [bool]$OfficeInstalled,
        [bool]$OfficeActivated,
        [string]$OfficeActivationMode = "Auto",
        [string]$OfficeLicenseMethod = "NONE",
        [string]$OfficeLicenseDetail = "",
        [string]$LocalSetupDir = "C:\setup",
        [bool]$WindowsOa3Present = $false,
        [string]$WindowsEdition = "",
        [string]$WindowsLicenseMethod = "NONE",
        [string]$WindowsLicenseDetail = ""
    )

    $safeDetail = Protect-WisLicenseLogText -Text $OfficeLicenseDetail
    $safeWinDetail = Protect-WisLicenseLogText -Text $WindowsLicenseDetail
    $tsIso = (Get-Date).ToString("o")
    $statusMap = [ordered]@{
        windows_activated      = [bool]$WindowsActivated
        windows_oa3_present    = [bool]$WindowsOa3Present
        windows_edition        = [string]$WindowsEdition
        windows_license_method = [string]$WindowsLicenseMethod
        windows_license_detail = [string]$safeWinDetail
        office_installed       = [bool]$OfficeInstalled
        office_activated       = [bool]$OfficeActivated
        office_activation_mode = [string]$OfficeActivationMode
        office_license_method  = [string]$OfficeLicenseMethod
        office_license_detail  = [string]$safeDetail
        timestamp              = $tsIso
    }

    $targets = @("C:\setup")
    if ($LocalSetupDir -and ($targets -notcontains $LocalSetupDir)) {
        $targets += $LocalSetupDir
    }
    $utf8NoBom = New-Object System.Text.UTF8Encoding($false)
    $jsonPayload = ConvertTo-Json -InputObject $statusMap -Depth 4

    foreach ($dirPath in $targets) {
        try {
            if (-not (Test-Path -LiteralPath $dirPath -ErrorAction SilentlyContinue)) {
                New-Item -Path $dirPath -ItemType Directory -Force -ErrorAction SilentlyContinue | Out-Null
            }
            $destPath = Join-Path $dirPath "license-status.json"
            $tmpPath = "$destPath.tmp"
            [System.IO.File]::WriteAllText($tmpPath, $jsonPayload, $utf8NoBom)
            Move-Item -LiteralPath $tmpPath -Destination $destPath -Force -ErrorAction Stop
        } catch {}
    }

    return $statusMap
}

function Test-BatteryPresent {
    <#
    .SYNOPSIS
        Dvoslojna detekcija prenosnih računara (Laptop / Notebook).
    .DESCRIPTION
        Kombinuje ACPI proveru baterije (Win32_Battery) i BIOS tip šasije (Win32_SystemEnclosure).
        Ako enclosure sadrži kodove za prenosne računare: 8, 9, 10, 14, 30, 31, 32,
        uređaj se tretira kao LAPTOP čak i ako baterija trenutno nije prijavljena preko ACPI-ja
        (npr. sveže instaliran sistem pre instalacije drajvera za upravljanje napajanjem).
    #>
    try {
        $battery = Get-CimInstance Win32_Battery -ErrorAction SilentlyContinue
        if ($battery) { return $true }

        $enclosure = Get-CimInstance Win32_SystemEnclosure -ErrorAction SilentlyContinue
        $types = if ($enclosure -and $enclosure.ChassisTypes) { @($enclosure.ChassisTypes) } else { @() }
        # DMTF laptop/notebook/portable kodovi:
        # 8 (Portable), 9 (Laptop), 10 (Notebook), 14 (Sub Notebook), 30 (Tablet), 31 (Convertible), 32 (Detachable)
        $laptopChassisCodes = @(8, 9, 10, 14, 30, 31, 32)
        foreach ($t in $types) {
            if ($laptopChassisCodes -contains [int]$t) {
                return $true
            }
        }
    } catch {}
    return $false
}

function Get-WisAppPresence {
    <#
    .SYNOPSIS
        Proverava prisustvo i status instalacije zadate aplikacije (fajlovi na disku, registar i konfiguracija).
    .PARAMETER AppId
        Identifikator aplikacije (Chrome, WinRAR, Acrobat, Office, BatteryBar, DotNet/Runtimes, Shortcuts, PdfDefault, Activation).
    .OUTPUTS
        Hashtable sa kljucevima Installed, Done, Version, Path, Message, Reason, Required, IsLaptop.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$AppId
    )

    $id = $AppId.Trim()
    $isLaptop = Test-BatteryPresent

    $result = @{
        Installed = $false
        Done      = $false
        Version   = ""
        Path      = ""
        Message   = ""
        Reason    = ""
        Required  = $true
        IsLaptop  = $isLaptop
    }

    switch -Regex ($id) {
        '(?i)^(Chrome|Google Chrome)$' {
            $chromePaths = @(
                "C:\Program Files\Google\Chrome\Application\chrome.exe",
                "C:\Program Files (x86)\Google\Chrome\Application\chrome.exe"
            )
            $chPath = $chromePaths | Where-Object { Test-Path $_ } | Select-Object -First 1
            if ($chPath) {
                $ver = ""
                try { $ver = (Get-Item $chPath).VersionInfo.ProductVersion } catch { $ver = "OK" }
                $msg = "Instaliran Google Chrome $(if ($ver) { '(v' + $ver + ')' } else { '' })"
                $result.Installed = $true
                $result.Done      = $true
                $result.Path      = $chPath
                $result.Version   = $ver
                $result.Message   = $msg
                $result.Reason    = $msg
            } else {
                $result.Message   = "NEDOSTAJE (chrome.exe nije pronađen)"
                $result.Reason    = "Google Chrome nije pronađen na disku"
            }
            return $result
        }

        '(?i)^WinRAR$' {
            $winrarPaths = @(
                "C:\Program Files\WinRAR\WinRAR.exe",
                "C:\Program Files (x86)\WinRAR\WinRAR.exe"
            )
            $wrPath = $winrarPaths | Where-Object { Test-Path $_ } | Select-Object -First 1
            if ($wrPath) {
                $ver = ""
                try { $ver = (Get-Item $wrPath).VersionInfo.ProductVersion } catch { $ver = "OK" }
                $msg = "Instaliran WinRAR $(if ($ver) { '(v' + $ver + ')' } else { '' })"
                $result.Installed = $true
                $result.Done      = $true
                $result.Path      = $wrPath
                $result.Version   = $ver
                $result.Message   = $msg
                $result.Reason    = $msg
            } else {
                $result.Message   = "NEDOSTAJE (WinRAR.exe nije pronađen)"
                $result.Reason    = "WinRAR arhiver nije pronađen na disku"
            }
            return $result
        }

        '(?i)^(Acrobat|Adobe Acrobat|Adobe Acrobat Pro)$' {
            $acroPaths = @(
                "C:\Program Files\Adobe\Acrobat DC\Acrobat\Acrobat.exe",
                "C:\Program Files (x86)\Adobe\Acrobat DC\Acrobat\Acrobat.exe",
                "C:\Program Files\Adobe\Acrobat Pro\Acrobat.exe",
                "C:\Program Files (x86)\Adobe\Acrobat Pro\Acrobat.exe",
                "C:\Program Files\Adobe\Acrobat 2024\Acrobat\Acrobat.exe",
                "C:\Program Files (x86)\Adobe\Acrobat 2024\Acrobat\Acrobat.exe",
                "C:\Program Files\Adobe\Acrobat\Acrobat.exe"
            )
            $acPath = $acroPaths | Where-Object { Test-Path $_ } | Select-Object -First 1
            if ($acPath) {
                $ver = ""
                try { $ver = (Get-Item $acPath).VersionInfo.ProductVersion } catch { $ver = "OK" }
                $msg = "Instaliran Adobe Acrobat $(if ($ver) { '(v' + $ver + ')' } else { '' })"
                $result.Installed = $true
                $result.Done      = $true
                $result.Path      = $acPath
                $result.Version   = $ver
                $result.Message   = $msg
                $result.Reason    = $msg
            } else {
                $result.Message   = "NEDOSTAJE (Acrobat.exe nije pronađen)"
                $result.Reason    = "Adobe Acrobat nije pronađen na disku"
            }
            return $result
        }

        '(?i)^(Office|Microsoft Office|MS Office)$' {
            $wordPaths = @(
                "C:\Program Files\Microsoft Office\root\Office16\WINWORD.EXE",
                "C:\Program Files (x86)\Microsoft Office\root\Office16\WINWORD.EXE",
                "C:\Program Files\Microsoft Office\Office16\WINWORD.EXE",
                "C:\Program Files (x86)\Microsoft Office\Office16\WINWORD.EXE"
            )
            $excelPaths = @(
                "C:\Program Files\Microsoft Office\root\Office16\EXCEL.EXE",
                "C:\Program Files (x86)\Microsoft Office\root\Office16\EXCEL.EXE",
                "C:\Program Files\Microsoft Office\Office16\EXCEL.EXE",
                "C:\Program Files (x86)\Microsoft Office\Office16\EXCEL.EXE"
            )
            $wPath = $wordPaths | Where-Object { Test-Path $_ } | Select-Object -First 1
            $ePath = $excelPaths | Where-Object { Test-Path $_ } | Select-Object -First 1

            if ($wPath -or $ePath) {
                $wVer = ""
                $eVer = ""
                try { if ($wPath) { $wVer = (Get-Item $wPath).VersionInfo.ProductVersion } } catch {}
                try { if ($ePath) { $eVer = (Get-Item $ePath).VersionInfo.ProductVersion } } catch {}
                $verStr = if ($wVer) { "v$wVer" } elseif ($eVer) { "v$eVer" } else { "2024" }
                $msg = "Instaliran Microsoft Office ($verStr)"
                $result.Installed    = $true
                $result.Done         = $true
                $result.Path         = if ($wPath) { $wPath } else { $ePath }
                $result.Version      = $verStr
                $result.WordVersion  = $wVer
                $result.ExcelVersion = $eVer
                $result.Message      = $msg
                $result.Reason       = $msg
            } else {
                $result.Message   = "NEDOSTAJE (WINWORD.EXE i EXCEL.EXE nisu pronađeni)"
                $result.Reason    = "Microsoft Office nije pronađen na disku"
            }
            return $result
        }

        '(?i)^BatteryBar$' {
            if (-not $isLaptop) {
                $result.Required  = $false
                $result.Installed = $true
                $result.Done      = $true
                $result.Message   = "Nije potrebno (Desktop računar bez baterije)"
                $result.Reason    = "Nije primenljivo — Desktop računar bez baterije"
                return $result
            }

            $bbPaths = @(
                "C:\Program Files\BatteryBar\BatteryBar.exe",
                "C:\Program Files (x86)\BatteryBar\BatteryBar.exe"
            )
            $bbPath = $bbPaths | Where-Object { Test-Path $_ } | Select-Object -First 1
            $runReg = (Get-ItemProperty "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run" -Name "BatteryBar" -ErrorAction SilentlyContinue).BatteryBar
            if (-not $runReg) {
                $runReg = (Get-ItemProperty "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run" -Name "BatteryBar" -ErrorAction SilentlyContinue).BatteryBar
            }

            if ($bbPath) {
                $msg = "Instaliran BatteryBar ($bbPath$(if ($runReg) { ', autostart konfigurisan' } else { '' }))"
                $result.Installed = $true
                $result.Done      = $true
                $result.Path      = $bbPath
                $result.Message   = $msg
                $result.Reason    = $msg
            } else {
                $result.Message   = "NEDOSTAJE (BatteryBar.exe nije pronađen na laptopu)"
                $result.Reason    = "BatteryBar.exe nije pronađen na laptopu sa baterijom"
            }
            return $result
        }

        '(?i)^(DotNet|Runtimes)$' {
            $dotnetDesktopDir = "C:\Program Files\dotnet\shared\Microsoft.WindowsDesktop.App"
            $hasDotNetDesktop = $false
            $dnVersions = @()
            if (Test-Path $dotnetDesktopDir) {
                $dnVersions = Get-ChildItem -Path $dotnetDesktopDir -Directory -ErrorAction SilentlyContinue | ForEach-Object { $_.Name }
                if ($dnVersions.Count -gt 0) { $hasDotNetDesktop = $true }
            }
            if ($hasDotNetDesktop) {
                $verStr = $dnVersions -join ", "
                $msg = "Instaliran (.NET Desktop: $verStr)"
                $result.Installed = $true
                $result.Done      = $true
                $result.Version   = $verStr
                $result.Message   = $msg
                $result.Reason    = $msg
            } else {
                $regDn = (Get-ItemProperty "HKLM:\SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedhost" -ErrorAction SilentlyContinue).Version
                if ($regDn) {
                    $msg = "Instaliran (.NET Shared Host: $regDn)"
                    $result.Installed = $true
                    $result.Done      = $true
                    $result.Version   = $regDn
                    $result.Message   = $msg
                    $result.Reason    = $msg
                } else {
                    $result.Message   = "NEDOSTAJE (.NET Desktop Runtime nije pronađen)"
                    $result.Reason    = ".NET Desktop Runtime nije pronađen"
                }
            }
            return $result
        }

        '(?i)^Shortcuts$' {
            $publicDesktop = [Environment]::GetFolderPath("CommonDesktopDirectory")
            $userDesktop   = [Environment]::GetFolderPath("Desktop")

            $expectedShortcuts = [System.Collections.Generic.List[string]]::new()
            $acroInstalled = (Test-Path "C:\Program Files\Adobe\Acrobat DC\Acrobat\Acrobat.exe") -or `
                             (Test-Path "C:\Program Files (x86)\Adobe\Acrobat DC\Acrobat\Acrobat.exe") -or `
                             (Test-Path "C:\Program Files\Adobe\Acrobat 2024\Acrobat\Acrobat.exe") -or `
                             (Test-Path "C:\Program Files (x86)\Adobe\Acrobat 2024\Acrobat\Acrobat.exe")
            $wordInstalled = (Test-Path "C:\Program Files\Microsoft Office\root\Office16\WINWORD.EXE") -or `
                             (Test-Path "C:\Program Files (x86)\Microsoft Office\root\Office16\WINWORD.EXE") -or `
                             (Test-Path "C:\Program Files\Microsoft Office\Office16\WINWORD.EXE") -or `
                             (Test-Path "C:\Program Files (x86)\Microsoft Office\Office16\WINWORD.EXE")
            $excelInstalled = (Test-Path "C:\Program Files\Microsoft Office\root\Office16\EXCEL.EXE") -or `
                              (Test-Path "C:\Program Files (x86)\Microsoft Office\root\Office16\EXCEL.EXE") -or `
                              (Test-Path "C:\Program Files\Microsoft Office\Office16\EXCEL.EXE") -or `
                              (Test-Path "C:\Program Files (x86)\Microsoft Office\Office16\EXCEL.EXE")

            if ($acroInstalled)  { $expectedShortcuts.Add("Adobe Acrobat.lnk") }
            if ($wordInstalled)  { $expectedShortcuts.Add("Word.lnk") }
            if ($excelInstalled) { $expectedShortcuts.Add("Excel.lnk") }

            if ($expectedShortcuts.Count -eq 0) {
                $expectedShortcuts.Add("Google Chrome.lnk")
                $expectedShortcuts.Add("Adobe Acrobat.lnk")
                $expectedShortcuts.Add("Word.lnk")
                $expectedShortcuts.Add("Excel.lnk")
            }

            $foundCount = 0
            $missingLnks = [System.Collections.Generic.List[string]]::new()
            foreach ($lnk in $expectedShortcuts) {
                $p1 = Join-Path $publicDesktop $lnk
                $p2 = Join-Path $userDesktop $lnk
                if ((Test-Path $p1) -or (Test-Path $p2)) {
                    $foundCount++
                } else {
                    $missingLnks.Add($lnk)
                }
            }

            if ($foundCount -ge $expectedShortcuts.Count -and $expectedShortcuts.Count -gt 0) {
                $msg = "Radne prečice postoje na radnoj površini ($foundCount/$($expectedShortcuts.Count))"
                $result.Installed = $true
                $result.Done      = $true
                $result.Message   = $msg
                $result.Reason    = $msg
            } else {
                $missText = if ($missingLnks.Count -gt 0) { " (" + ($missingLnks -join ", ") + ")" } else { "" }
                $result.Message   = "Nedostaju prečice na radnoj površini$missText"
                $result.Reason    = "Prečice programa nisu pronađene na radnoj površini$missText"
            }
            return $result
        }

        '(?i)^PdfDefault$' {
            $acroCandidatesPdf = @(
                "C:\Program Files\Adobe\Acrobat DC\Acrobat\Acrobat.exe",
                "C:\Program Files (x86)\Adobe\Acrobat DC\Acrobat\Acrobat.exe",
                "C:\Program Files\Adobe\Acrobat 2024\Acrobat\Acrobat.exe",
                "C:\Program Files (x86)\Adobe\Acrobat 2024\Acrobat\Acrobat.exe",
                "C:\Program Files\Adobe\Acrobat Pro\Acrobat.exe",
                "C:\Program Files (x86)\Adobe\Acrobat Pro\Acrobat.exe",
                "C:\Program Files\Adobe\Acrobat\Acrobat.exe",
                "C:\Program Files (x86)\Adobe\Acrobat\Acrobat.exe"
            )
            $acroExeForPdf = $acroCandidatesPdf | Where-Object { Test-Path $_ } | Select-Object -First 1
            if (-not $acroExeForPdf) {
                $result.Installed = $false
                $result.Done      = $false
                $result.Message   = "SKIPPED_ERROR: Adobe Acrobat (Acrobat.exe) nije instaliran na disku"
                $result.Reason    = "Adobe Acrobat (Acrobat.exe) nije instaliran — nije moguće podesiti podrazumevani PDF čitač"
                return $result
            }

            $pdfProgId = ""
            try {
                $userChoiceKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\.pdf\UserChoice"
                $pdfProgId = (Get-ItemProperty -Path $userChoiceKey -Name "ProgId" -ErrorAction SilentlyContinue).ProgId
            } catch {}

            $gpoAssocPath = ""
            try {
                $gpoAssocPath = (Get-ItemProperty -Path "HKLM:\SOFTWARE\Policies\Microsoft\Windows\System" -Name "DefaultAssociationsConfiguration" -ErrorAction SilentlyContinue).DefaultAssociationsConfiguration
            } catch {}

            $classesPdf = ""
            try {
                $classesPdf = (Get-ItemProperty -Path "HKLM:\SOFTWARE\Classes\.pdf" -Name "(Default)" -ErrorAction SilentlyContinue).'(Default)'
            } catch {}

            $isAcrobatUserChoice = ($pdfProgId -like "*Acrobat*" -or $pdfProgId -like "*AcroExch*")
            $isOtherUserChoice = (-not [string]::IsNullOrWhiteSpace($pdfProgId)) -and (-not $isAcrobatUserChoice)
            $isGpoAcrobat = ($gpoAssocPath -like "*AppAssoc_Acrobat.xml*") -and (Test-Path "C:\Windows\System32\AppAssoc_Acrobat.xml")
            $isClassAcrobat = ($classesPdf -like "*AcroExch*")

            if ($isAcrobatUserChoice -or ($isGpoAcrobat -and $isClassAcrobat) -or ((-not $isOtherUserChoice) -and ($isGpoAcrobat -or $isClassAcrobat))) {
                $desc = if ($isAcrobatUserChoice) { $pdfProgId } elseif ($isGpoAcrobat) { "GPO AppAssoc_Acrobat.xml" } else { $classesPdf }
                $msg = "Adobe Acrobat je podešen kao podrazumevani PDF čitač ($desc)"
                $result.Installed = $true
                $result.Done      = $true
                $result.Path      = $acroExeForPdf
                $result.Message   = $msg
                $result.Reason    = $msg
            } else {
                $result.Message   = "Acrobat nije podrazumevani PDF čitač ($pdfProgId)"
                $result.Reason    = "Acrobat nije podešen kao podrazumevani PDF čitač"
            }
            return $result
        }

        '(?i)^(Activation|WindowsActivation|OfficeActivation)$' {
            $winStatusObj = Get-WindowsLicenseStatus
            $winAct = [bool]($winStatusObj -and $winStatusObj.Activated)
            $hasOffice = Test-OfficeInstalled
            $licCfg = Get-WisLicensingSettingsLocal
            $licMode = if ($licCfg -and $licCfg.OfficeActivationMode) { [string]$licCfg.OfficeActivationMode } else { "Auto" }
            $winLicMode = if ($licCfg -and $licCfg.WindowsActivationMode) { [string]$licCfg.WindowsActivationMode } else { "Auto" }
            $officeSkipped = ($licMode -ieq "Skip")
            $winSkipped = ($winLicMode -ieq "Skip")

            $officeStepFailed = ($Global:WisOfficeInstallFailed -eq $true)
            if (-not $hasOffice -and -not $officeStepFailed -and (Test-Path -LiteralPath "C:\setup\step-journal.json" -ErrorAction SilentlyContinue)) {
                try {
                    $sjRaw = Get-Content -LiteralPath "C:\setup\step-journal.json" -Raw -Encoding UTF8 -ErrorAction SilentlyContinue
                    if ($sjRaw) {
                        $sjObj = ConvertFrom-Json -InputObject $sjRaw -ErrorAction SilentlyContinue
                        if ($sjObj -and $sjObj.steps -and $sjObj.steps.PSObject.Properties["P4.Office"]) {
                            $p4OffState = [string]$sjObj.steps.PSObject.Properties["P4.Office"].Value.state
                            if ($p4OffState -and $p4OffState -notin @("OK", "SKIPPED_DONE")) {
                                $officeStepFailed = $true
                            }
                        }
                    }
                } catch {}
            }

            $offStatus = if ($hasOffice) { Get-OfficeLicenseStatus } else { $null }
            $offAct = if ($hasOffice) { [bool]($offStatus -and $offStatus.Activated) } elseif ($officeStepFailed -and (-not $officeSkipped)) { $false } else { $true }

            $result.WindowsActivated     = $winAct
            $result.OfficeInstalled  = $hasOffice
            $result.OfficeActivated      = $offAct
            $result.OfficeActivationMode = $licMode

            if (($winAct -or $winSkipped) -and ($offAct -or $officeSkipped)) {
                $winTxt = if ($winAct) { "Windows je trajno licenciran i aktiviran" } else { "Windows aktivacija preskočena (WindowsActivationMode=Skip)" }
                $msg = if (-not $hasOffice) {
                    "$winTxt (Office nije instaliran)"
                } elseif ($offAct) {
                    if ($winAct) { "Windows i Microsoft Office su trajno licencirani i aktivirani" } else { "$winTxt; Microsoft Office aktiviran" }
                } else {
                    "$winTxt (Office aktivacija preskočena: OfficeActivationMode=Skip)"
                }
                $result.Installed = $true
                $result.Done      = $true
                $result.Message   = $msg
                $result.Reason    = $msg
            } else {
                $parts = @()
                if (-not $winAct -and -not $winSkipped) {
                    $winDetailInfo = ""
                    if ($winStatusObj) {
                        if (-not [string]::IsNullOrWhiteSpace($winStatusObj.LicenseStatusReason)) {
                            $winDetailInfo = "$($winStatusObj.LicenseStatusText) $($winStatusObj.LicenseStatusReason)"
                        } elseif (-not [string]::IsNullOrWhiteSpace($winStatusObj.LicenseStatusText)) {
                            $winDetailInfo = $winStatusObj.LicenseStatusText
                        }
                    }
                    if ([string]::IsNullOrWhiteSpace($winDetailInfo) -and (Test-Path -LiteralPath "C:\setup\license-status.json" -ErrorAction SilentlyContinue)) {
                        try {
                            $lsRawW = Get-Content -LiteralPath "C:\setup\license-status.json" -Raw -Encoding UTF8 -ErrorAction SilentlyContinue
                            if ($lsRawW) {
                                $lsObjW = ConvertFrom-Json -InputObject $lsRawW -ErrorAction SilentlyContinue
                                if ($lsObjW -and $lsObjW.windows_license_detail) {
                                    $winDetailInfo = Protect-WisLicenseLogText -Text ([string]$lsObjW.windows_license_detail)
                                }
                            }
                        } catch {}
                    }
                    $winDetailInfo = Protect-WisLicenseLogText -Text $winDetailInfo
                    if (-not [string]::IsNullOrWhiteSpace($winDetailInfo)) {
                        $parts += "Windows nije aktiviran ($winDetailInfo)"
                    } else {
                        $parts += "Windows nije aktiviran"
                    }
                }
                if (-not $hasOffice -and $officeStepFailed -and -not $officeSkipped) {
                    $parts += "UPOZORENJE: Microsoft Office nije instaliran (korak P4.Office nije uspeo)"
                } elseif ($hasOffice -and -not $offAct -and -not $officeSkipped) {
                    $offDetailInfo = ""
                    if ($offStatus) {
                        if (-not [string]::IsNullOrWhiteSpace($offStatus.LastErrorCode)) {
                            $offDetailInfo = "$($offStatus.Method) $($offStatus.LastErrorCode)"
                        } elseif (-not [string]::IsNullOrWhiteSpace($offStatus.LicenseStatusText)) {
                            $offDetailInfo = $offStatus.LicenseStatusText
                        }
                    }
                    if ([string]::IsNullOrWhiteSpace($offDetailInfo) -and (Test-Path -LiteralPath "C:\setup\license-status.json" -ErrorAction SilentlyContinue)) {
                        try {
                            $lsRaw = Get-Content -LiteralPath "C:\setup\license-status.json" -Raw -Encoding UTF8 -ErrorAction SilentlyContinue
                            if ($lsRaw) {
                                $lsObj = ConvertFrom-Json -InputObject $lsRaw -ErrorAction SilentlyContinue
                                if ($lsObj -and $lsObj.office_license_detail) {
                                    $offDetailInfo = Protect-WisLicenseLogText -Text ([string]$lsObj.office_license_detail)
                                }
                            }
                        } catch {}
                    }
                    if (-not [string]::IsNullOrWhiteSpace($offDetailInfo)) {
                        $parts += "Office nije aktiviran ($offDetailInfo)"
                    } else {
                        $parts += "Office nije aktiviran"
                    }
                }
                $msg = $parts -join "; "
                $result.Message   = $msg
                $result.Reason    = $msg
            }
            return $result
        }

        default {
            $result.Message = "Nepoznata aplikacija: $AppId"
            $result.Reason  = "Nepoznat identifikator aplikacije: $AppId"
            return $result
        }
    }
}

function Test-AllServisProgramsInstalled {
    $chromeRes  = Get-WisAppPresence -AppId "Chrome"
    $winrarRes  = Get-WisAppPresence -AppId "WinRAR"
    $acrobatRes = Get-WisAppPresence -AppId "Acrobat"
    $officeRes  = Get-WisAppPresence -AppId "Office"
    $dotnetRes  = Get-WisAppPresence -AppId "DotNet"
    $bbRes      = Get-WisAppPresence -AppId "BatteryBar"

    $missingList = [System.Collections.Generic.List[string]]::new()
    if (-not $chromeRes.Installed)  { $missingList.Add("Google Chrome") }
    if (-not $winrarRes.Installed)  { $missingList.Add("WinRAR x64") }
    if (-not $acrobatRes.Installed) { $missingList.Add("Adobe Acrobat Pro") }
    if (-not $officeRes.Installed)  { $missingList.Add("Microsoft Office") }
    if (-not $dotnetRes.Installed)  { $missingList.Add("Microsoft .NET Runtime") }
    if ($bbRes.Required -and -not $bbRes.Installed) { $missingList.Add("BatteryBar (Laptop)") }

    $winLicStatus = Get-WindowsLicenseStatus
    $offLicStatus = if ($officeRes.Installed) { Get-OfficeLicenseStatus } else { $null }

    $results = @{
        Chrome               = $chromeRes
        WinRAR               = $winrarRes
        Acrobat              = $acrobatRes
        Office               = $officeRes
        DotNet               = $dotnetRes
        BatteryBar           = $bbRes
        WindowsActivation    = @{
            Activated           = if ($winLicStatus) { [bool]$winLicStatus.Activated } else { $false }
            Method              = if ($winLicStatus) { [string]$winLicStatus.Method } else { "NONE" }
            Edition             = if ($winLicStatus) { [string]$winLicStatus.Edition } else { "" }
            Oa3xPresent         = if ($winLicStatus) { [bool]$winLicStatus.Oa3xPresent } else { $false }
            LicenseStatusText   = if ($winLicStatus) { [string]$winLicStatus.LicenseStatusText } else { "" }
            LicenseStatusReason = if ($winLicStatus) { [string]$winLicStatus.LicenseStatusReason } else { "" }
        }
        OfficeActivation     = @{
            Activated         = if ($offLicStatus) { [bool]$offLicStatus.Activated } else { $false }
            Method            = if ($offLicStatus) { [string]$offLicStatus.Method } else { "NotInstalled" }
            SkuName           = if ($offLicStatus) { [string]$offLicStatus.SkuName } else { "" }
            PartialProductKey = if ($offLicStatus) { [string]$offLicStatus.PartialProductKey } else { "" }
            LicenseStatusText = if ($offLicStatus) { [string]$offLicStatus.LicenseStatusText } else { "" }
            LastErrorCode     = if ($offLicStatus) { [string]$offLicStatus.LastErrorCode } else { "" }
        }
        AllRequiredInstalled = ($missingList.Count -eq 0)
        MissingList          = $missingList
    }

    return $results
}

function Test-ProgramiPackageComplete {
    param([string]$DirPath)
    if ([string]::IsNullOrWhiteSpace($DirPath) -or -not (Test-Path $DirPath -ErrorAction SilentlyContinue)) {
        return $false
    }
    $hasOffice = (Test-Path (Join-Path $DirPath "Office 2024\files\setup.exe")) -or `
                 (Test-Path (Join-Path $DirPath "Office 2024\setup.exe")) -or `
                 (Test-Path (Join-Path $DirPath "Programi\Office 2024\files\setup.exe")) -or `
                 (Test-Path (Join-Path $DirPath "Programi\Office 2024\setup.exe"))
    $hasAcrobat = (Test-Path (Join-Path $DirPath "Adobe.Acrobat.Pro.2024.x64.Multilingual\Adobe Acrobat Pro\AcroPro.msi")) -or `
                  (Test-Path (Join-Path $DirPath "Acrobat Pro 2024\Adobe Acrobat Pro\AcroPro.msi")) -or `
                  (Test-Path (Join-Path $DirPath "Programi\Adobe.Acrobat.Pro.2024.x64.Multilingual\Adobe Acrobat Pro\AcroPro.msi"))
    return ($hasOffice -and $hasAcrobat)
}

function Get-ServisProgramiRoot {
    <#
    .SYNOPSIS
        Pametna detekcija izvora programa za WIS (Prioritet 1: Lokalni SSD/USB, Prioritet 2: TrueNAS SMB 3.1.1).
    .DESCRIPTION
        Omogucava Lean ISO profil (instalacioni medijum bez Programi.tar) uz direktno strimovanje
        instalera sa TrueNAS Samba deljenja (\\192.168.170.254\Programi, uz Servis\Programi fallback),
        uz automatsku autentifikaciju (guest bez lozinke) i konfiguraciju SMB klijenta.
    .PARAMETER LocalSetupDir
        Lokalni setup direktorijum (podrazumevano C:\setup).
    .PARAMETER PreferredDir
        Opcioni preferirani direktorijum sa programima.
    #>
    [CmdletBinding()]
    param(
        [string]$LocalSetupDir = "C:\setup",
        [string]$PreferredDir = ""
    )

    if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
        Write-ServisLog "Get-ServisProgramiRoot: Pokrecem detekciju izvora programa (LocalSetupDir='$LocalSetupDir', PreferredDir='$PreferredDir')..." -Level "INFO" -Component "Apps"
    }

    # =========================================================================
    # PRIORITET 1: LOKALNI FOLDER (Full ISO profil ili vec raspakovano na SSD-u)
    # =========================================================================
    $localCandidates = @()
    if ($PreferredDir -and (Test-Path $PreferredDir)) {
        $localCandidates += $PreferredDir
    }
    if ($LocalSetupDir) {
        $localCandidates += (Join-Path $LocalSetupDir "Programi")
    }
    $localCandidates += "C:\setup\Programi"
    $localCandidates += "C:\setup\Programi\Programi"

    foreach ($loc in ($localCandidates | Select-Object -Unique)) {
        if (Test-ProgramiPackageComplete -DirPath $loc) {
            if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                Write-ServisLog "Get-ServisProgramiRoot: Pronadjen kompletan lokalni izvor programa (Prioritet 1 - Office + Acrobat): $loc" -Level "SUCCESS" -Component "Apps"
            }
            return $loc
        }
    }

    # =========================================================================
    # PRIORITET 2: MREZNI DELJENI RESURS KA TrueNAS-u (Samba SMB 3.1.1)
    # =========================================================================
    if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
        Write-ServisLog "Get-ServisProgramiRoot: Lokalni programi nisu pronadjeni. Pokusavam povezivanje na TrueNAS SMB 3.1.1 deljenje..." -Level "INFO" -Component "Apps"
    }

    # 1. Osiguravanje SMB klijentskih parametara za Guest pristup i potpisivanje (Win 11 24H2 kompatibilnost)
    try {
        $lanmanPath = "HKLM:\SYSTEM\CurrentControlSet\Services\LanmanWorkstation\Parameters"
        if (-not (Test-Path $lanmanPath)) {
            New-Item -Path $lanmanPath -Force -ErrorAction SilentlyContinue | Out-Null
        }
        Set-ItemProperty -Path $lanmanPath -Name "AllowInsecureGuestAuth" -Value 1 -Type DWord -Force -ErrorAction SilentlyContinue
        Set-ItemProperty -Path $lanmanPath -Name "RequireSecuritySignature" -Value 0 -Type DWord -Force -ErrorAction SilentlyContinue
    } catch {}

    # 2. Definisanje liste mrežnih kandidata
    $nasHosts = @("192.168.170.254", "truenas.servis")
    $nasShares = @("Programi", "WIS", "Servis")
    $nasIni = $null

    # Dodavanje parametara iz Wifi-Servis.ini ako postoje
    if (Get-Command Get-ServisNasSettings -ErrorAction SilentlyContinue) {
        $nasIni = Get-ServisNasSettings
        if ($nasIni) {
            if (-not [string]::IsNullOrWhiteSpace($nasIni.NasHost) -and ($nasHosts -notcontains $nasIni.NasHost)) {
                $nasHosts += $nasIni.NasHost
            }
            if (-not [string]::IsNullOrWhiteSpace($nasIni.ProgramiShare) -and ($nasShares -notcontains $nasIni.ProgramiShare)) {
                $nasShares = @($nasIni.ProgramiShare) + $nasShares
            }
            if (-not [string]::IsNullOrWhiteSpace($nasIni.ShareName) -and ($nasShares -notcontains $nasIni.ShareName)) {
                $nasShares += $nasIni.ShareName
            }
        }
    }

    $nasUser = "admin"
    $nasPass = ""
    if ($nasIni) {
        if (-not [string]::IsNullOrWhiteSpace($nasIni.Username)) { $nasUser = $nasIni.Username }
        if ($nasUser -ieq "servis") { $nasUser = "admin" }
        if (-not [string]::IsNullOrWhiteSpace($nasIni.Password) -and $nasIni.Password -ne "promeni_lozinku" -and $nasIni.Password -ne "CHANGE_ME") { $nasPass = $nasIni.Password }
    }

    # Direktni fallback citač za Wifi-Servis.ini ako WIS.Core nije uvezen u sesiju
    if ([string]::IsNullOrWhiteSpace($nasPass)) {
        $iniCandidates = @(
            (Join-Path $LocalSetupDir "Wifi-Servis.ini"),
            "C:\setup\Wifi-Servis.ini",
            (Join-Path $PSScriptRoot "..\..\Wifi-Servis.ini"),
            (Join-Path $PSScriptRoot "..\Wifi-Servis.ini")
        )
        foreach ($ic in ($iniCandidates | Select-Object -Unique)) {
            if ($ic -and (Test-Path $ic -ErrorAction SilentlyContinue)) {
                try {
                    $rawIni = Get-Content -Path $ic -Raw -ErrorAction SilentlyContinue
                    if ($rawIni -and $rawIni -match '(?mi)^\s*\[ServisNas\]([\s\S]*?)(?:^\s*\[|\Z)') {
                        $sec = $matches[1]
                        if ($sec -match '(?mi)^[ \t]*Username[ \t]*=[ \t]*([^\r\n#;]+)') {
                            $uVal = $matches[1].Trim()
                            if ($uVal) {
                                $nasUser = if ($uVal -ieq "servis") { "admin" } else { $uVal }
                            }
                        }
                        if ($sec -match '(?mi)^[ \t]*Password[ \t]*=[ \t]*([^\r\n#;]+)') {
                            $pVal = $matches[1].Trim()
                            if ($pVal -and $pVal -ne "promeni_lozinku" -and $pVal -ne "CHANGE_ME") { $nasPass = $pVal }
                        }
                        if ($sec -match '(?mi)^[ \t]*NasHost[ \t]*=[ \t]*([^\r\n#;]+)') {
                            $hVal = $matches[1].Trim()
                            if ($hVal -and ($nasHosts -notcontains $hVal)) { $nasHosts = @($hVal) + $nasHosts }
                        }
                        break
                    }
                } catch {}
            }
        }
    }
    $nasShares = $nasShares | Select-Object -Unique

    foreach ($h in $nasHosts) {
        # Brza TCP provera porta 445 (SMB) sa kratkim timeout-om (1500 ms)
        $portOpen = $false
        try {
            $tcp = New-Object System.Net.Sockets.TcpClient
            $ar = $tcp.BeginConnect($h, 445, $null, $null)
            $waitOk = $ar.AsyncWaitHandle.WaitOne(1500, $false)
            if ($waitOk -and $tcp.Connected) {
                $tcp.EndConnect($ar)
                $portOpen = $true
            }
            $tcp.Close()
        } catch {
            $portOpen = $false
        }

        if (-not $portOpen) {
            if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                Write-ServisLog "Get-ServisProgramiRoot: Host $h nije dostupan na portu 445 (SMB)." -Level "WARN" -Component "Apps"
            }
            if (Get-Command Send-IpcLog -ErrorAction SilentlyContinue) {
                Send-IpcLog -Message "Get-ServisProgramiRoot: NAS host $h nije dostupan na portu 445 (SMB)." -Level "WARN" -Tag "[NasProgrami]"
            }
            continue
        }

        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
            Write-ServisLog "Get-ServisProgramiRoot: Host $h je dostupan (port 445 otvoren). Proveravam deljene resurse..." -Level "INFO" -Component "Apps"
        }

        foreach ($sh in $nasShares) {
            $uncShare = "\\$h\$sh"

            # 1. Osiguraj SMB autentifikovanu sesiju bez Error 1219 i bez PowerShell ErrorRecord prekida
            $prevEap = $ErrorActionPreference
            $ErrorActionPreference = 'Continue'
            try {
                if (-not (Test-Path $uncShare -ErrorAction SilentlyContinue)) {
                    if (-not [string]::IsNullOrWhiteSpace($nasPass)) {
                        $userArg = if ($nasUser -match "\\") { $nasUser } else { "$h\$nasUser" }
                        $null = & cmd.exe /c "net.exe use `"$uncShare`" `"$nasPass`" /user:`"$userArg`" /persistent:no >nul 2>&1"
                        if (-not (Test-Path $uncShare -ErrorAction SilentlyContinue)) {
                            $null = & cmd.exe /c "net.exe use `"$uncShare`" `"$nasPass`" /user:`"$nasUser`" /persistent:no >nul 2>&1"
                        }
                        # Ako postoji konfliktna SMB sesija (Error 1219, npr. prethodna guest sesija ka istom serveru), raskini i ponovi
                        if (-not (Test-Path $uncShare -ErrorAction SilentlyContinue)) {
                            $null = & cmd.exe /c "net.exe use * /delete /y >nul 2>&1"
                            $null = & cmd.exe /c "net.exe use `"$uncShare`" `"$nasPass`" /user:`"$userArg`" /persistent:no >nul 2>&1"
                        }
                    }
                    if (-not (Test-Path $uncShare -ErrorAction SilentlyContinue)) {
                        $null = & cmd.exe /c "net.exe use `"$uncShare`" `"`" /user:guest /persistent:no >nul 2>&1"
                    }
                    if (-not (Test-Path $uncShare -ErrorAction SilentlyContinue)) {
                        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                            Write-ServisLog "Get-ServisProgramiRoot: SMB povezivanje na $uncShare nije uspelo (korisnik: $nasUser)." -Level "WARN" -Component "Apps"
                        }
                        if (Get-Command Send-IpcLog -ErrorAction SilentlyContinue) {
                            Send-IpcLog -Message "Get-ServisProgramiRoot: SMB pristup odbijen ili neuspešan za $uncShare (korisnik: $nasUser)." -Level "WARN" -Tag "[NasProgrami]"
                        }
                    }
                }
            } catch {} finally {
                $ErrorActionPreference = $prevEap
            }

            # 2. Detekcija gde su paketi: na deljenju 'Programi' paketi su u korenu,
            #    dok na deljenju 'WIS' ili 'Servis' mogu biti u podfolderu 'Programi'
            $candidatePaths = if ($sh -ieq "Programi") {
                @($uncShare, (Join-Path $uncShare "Programi"))
            } else {
                @((Join-Path $uncShare "Programi"), $uncShare)
            }

            foreach ($uncProg in $candidatePaths) {
                if (Test-Path $uncProg -ErrorAction SilentlyContinue) {
                    $hasAnyProg = (Test-Path (Join-Path $uncProg "Office 2024")) -or `
                                  (Test-Path (Join-Path $uncProg "Adobe.Acrobat.Pro.2024.x64.Multilingual")) -or `
                                  (Test-Path (Join-Path $uncProg "Acrobat Pro 2024")) -or `
                                  (Test-Path (Join-Path $uncProg "ChromeSetup.exe")) -or `
                                  (Test-Path (Join-Path $uncProg "GoogleChromeStandaloneEnterprise64.msi")) -or `
                                  (Test-Path (Join-Path $uncProg "winrar-x64-701.exe"))
                    if ($hasAnyProg) {
                        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                            Write-ServisLog "Get-ServisProgramiRoot: Uspesno povezan i verifikovan SMB 3.1.1 resurs (Prioritet 2): $uncProg" -Level "SUCCESS" -Component "Apps"
                        }
                        return $uncProg
                    }
                }
            }
        }
    }

    # =========================================================================
    # PRIORITET 3: PRETRAGA PRENOSNIH USB DISKOVA
    # =========================================================================
    $allDrives = Get-PSDrive -PSProvider FileSystem -ErrorAction SilentlyContinue | Where-Object { $_.Root -ne 'C:\' -and $_.Root -ne 'c:\' }
    foreach ($dr in $allDrives) {
        $usbCandidates = @(
            (Join-Path $dr.Root "Programi"),
            (Join-Path $dr.Root 'sources\$OEM$\$1\setup\Programi'),
            (Join-Path $dr.Root 'oem\Programi')
        )
        foreach ($uc in $usbCandidates) {
            if (Test-ProgramiPackageComplete -DirPath $uc) {
                if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                    Write-ServisLog "Get-ServisProgramiRoot: Pronadjen kompletan USB izvor programa: $uc" -Level "INFO" -Component "Apps"
                }
                return $uc
            }
        }
    }

    # Krajnji fallback
    $fallback = if ($PreferredDir) { $PreferredDir } else { "C:\setup\Programi" }
    if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
        Write-ServisLog "Get-ServisProgramiRoot: Nijedan izvor nije potpuno verifikovan, koristim podrazumevanu putanju: $fallback" -Level "WARN" -Component "Apps"
    }
    if (Get-Command Send-IpcLog -ErrorAction SilentlyContinue) {
        Send-IpcLog -Message "Get-ServisProgramiRoot: Nijedan lokalni, USB ili NAS SMB izvor programa nije verifikovan (fallback: $fallback)." -Level "ERROR" -Tag "[NasProgrami]"
    }
    return $fallback
}

function Invoke-InstallerWithLocalFallback {
    <#
    .SYNOPSIS
        Tiha instalacija bezbedna od sigurnosnih upozorenja ('Open File - Security Warning').
        Za mrežne SMB/UNC pakete vrši se automatsko lokalno keširanje PRE pokretanja,
        uz obaveznu deblokadu (Unblock-File i brisanje Zone.Identifier) i pokretanje sa Hidden prozorom.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory=$true)][string]$AppName,
        [Parameter(Mandatory=$true)][string]$InstallerPath,
        [Alias("ArgumentList")]
        [string]$Arguments = "",
        [string]$WorkingDirectory = "",
        [switch]$IsDirectoryPackage = $false,
        [string]$PackageSourceDir = "",
        [int]$WaitSeconds = 60,
        [scriptblock]$CustomExecution = $null,
        [scriptblock]$VerificationCheck = $null
    )

    $env:SEE_MASK_NOZONECHECKS = '1'
    try { [Environment]::SetEnvironmentVariable("SEE_MASK_NOZONECHECKS", "1", "Process") } catch {}

    $isNetwork = ($InstallerPath -like "\\*") -or ($PackageSourceDir -like "\\*")
    $execSuccess = $false
    $execExitCode = -1
    $fallbackUsed = $false

    if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
        Write-ServisLog "[$AppName] Priprema instalera: $InstallerPath $(if ($Arguments) { $Arguments } else { '' }) $(if ($isNetwork) { '(Mrežni resurs -> Automatski lokalni keš)' } else { '(Lokalni fajl)' })" -Level "STEP" -Component $AppName
    }

    # 1. Ako je instaler mrežni (UNC / SMB) resurs:
    # UMESTO direktnog pokretanja preko sirove UNC putanje koje izaziva Windows security prompt [Run] [Cancel],
    # implementirano je automatsko privremeno lokalno keširanje PRE pokretanja.
    if ($isNetwork) {
        $cacheRoot = if (Test-Path "C:\setup") { "C:\setup\Cache" } else { Join-Path $env:TEMP "WIS\Cache" }
        $appCacheDir = Join-Path $cacheRoot $AppName

        try {
            if (Test-Path $appCacheDir) {
                Remove-Item -Path $appCacheDir -Recurse -Force -ErrorAction SilentlyContinue
            }
            New-Item -Path $appCacheDir -ItemType Directory -Force -ErrorAction SilentlyContinue | Out-Null

            $localInstaller = ""
            if ($IsDirectoryPackage -and $PackageSourceDir) {
                if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                    Write-ServisLog "[$AppName] Preuzimam mrežni paket u lokalni keš: $appCacheDir..." -Level "INFO" -Component $AppName
                }
                $null = & cmd.exe /c "robocopy.exe `"$PackageSourceDir`" `"$appCacheDir`" /E /MT:16 /R:2 /W:2 /NDL /NFL /NJH /NJS >nul 2>&1"
                $fileName = Split-Path $InstallerPath -Leaf
                $foundLocal = Get-ChildItem -Path $appCacheDir -Filter $fileName -Recurse -File -ErrorAction SilentlyContinue | Select-Object -First 1
                $localInstaller = if ($foundLocal) { $foundLocal.FullName } else { Join-Path $appCacheDir $fileName }
            } else {
                $fileName = Split-Path $InstallerPath -Leaf
                $localInstaller = Join-Path $appCacheDir $fileName
                if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                    Write-ServisLog "[$AppName] Preuzimam instaler u lokalni keš: $localInstaller..." -Level "INFO" -Component $AppName
                }
                Copy-Item -Path $InstallerPath -Destination $localInstaller -Force -ErrorAction Stop
            }

            if (Test-Path $localInstaller) {
                # Uklanjanje sigurnosnih zona sa svih keširanih fajlova
                Unblock-File -Path $localInstaller -ErrorAction SilentlyContinue
                Remove-Item -Path "$($localInstaller):Zone.Identifier" -ErrorAction SilentlyContinue
                Get-ChildItem -Path $appCacheDir -Recurse -File -ErrorAction SilentlyContinue | ForEach-Object {
                    Unblock-File -Path $_.FullName -ErrorAction SilentlyContinue
                    Remove-Item -Path "$($_.FullName):Zone.Identifier" -ErrorAction SilentlyContinue
                }

                $localWorkDir = Split-Path $localInstaller -Parent
                if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                    Write-ServisLog "[$AppName] Pokrećem lokalno keširani instaler: $localInstaller $Arguments" -Level "STEP" -Component $AppName
                }

                if ($CustomExecution) {
                    $res = & $CustomExecution $localInstaller $localWorkDir
                    if ($res -is [hashtable] -or $res -is [PSCustomObject]) {
                        $execSuccess = [bool]$res.Success
                        $execExitCode = $res.ExitCode
                    } elseif ($res -is [int]) {
                        $execExitCode = $res
                        $execSuccess = ($execExitCode -eq 0 -or $execExitCode -eq 3010)
                    } else {
                        $execSuccess = [bool]$res
                    }
                } else {
                    if ($WaitSeconds -gt 0) {
                        $proc = Start-Process -FilePath $localInstaller -ArgumentList $Arguments -WorkingDirectory $localWorkDir -PassThru -WindowStyle Hidden
                        if ($proc) {
                            $timeoutMs = $WaitSeconds * 1000
                            $exited = $proc.WaitForExit($timeoutMs)
                            if (-not $exited -or (-not $proc.HasExited)) {
                                if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                                    Write-ServisLog "[$AppName] Proces instalacije je prekoračio timeout (${WaitSeconds}s). Prisilno prekidam proces (PID: $($proc.Id))..." -Level "WARN" -Component $AppName
                                }
                                try { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue } catch {}
                                $execExitCode = -1
                                $execSuccess = $false
                            } else {
                                $execExitCode = $proc.ExitCode
                                $execSuccess = ($execExitCode -eq 0 -or $execExitCode -eq 3010)
                            }
                        } else {
                            $execExitCode = -1
                            $execSuccess = $false
                        }
                    } else {
                        $proc = Start-Process -FilePath $localInstaller -ArgumentList $Arguments -WorkingDirectory $localWorkDir -Wait -PassThru -WindowStyle Hidden
                        $execExitCode = if ($proc) { $proc.ExitCode } else { -1 }
                        $execSuccess = ($execExitCode -eq 0 -or $execExitCode -eq 3010)
                    }
                }

                if ($VerificationCheck) {
                    $isVerified = & $VerificationCheck
                    if ($isVerified) {
                        $execSuccess = $true
                    }
                }

                $fallbackUsed = $true
                if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                    Write-ServisLog "[$AppName] Keširana instalacija završena (ExitCode: $execExitCode, Success: $execSuccess)." -Level $(if ($execSuccess) { 'SUCCESS' } else { 'WARN' }) -Component $AppName
                }

                return @{ Success = $execSuccess; ExitCode = $execExitCode; FallbackUsed = $fallbackUsed }
            } else {
                if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                    Write-ServisLog "[$AppName] Neuspešno lokalno keširanje paketa ($localInstaller ne postoji)." -Level "ERROR" -Component $AppName
                }
            }
        } catch {
            if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                Write-ServisLog "[$AppName] Izuzetak tokom keširanja i pokretanja instalera: $($_.Exception.Message)" -Level "ERROR" -Component $AppName
            }
        } finally {
            try {
                if (Test-Path $appCacheDir) {
                    Remove-Item -Path $appCacheDir -Recurse -Force -ErrorAction SilentlyContinue
                    if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                        Write-ServisLog "[$AppName] Očišćen lokalni privremeni keš: $appCacheDir" -Level "DEBUG" -Component $AppName
                    }
                }
            } catch {}
        }
    } else {
        # 2. Ako je instaler lokalni fajl:
        try {
            Unblock-File -Path $InstallerPath -ErrorAction SilentlyContinue
            Remove-Item -Path "$($InstallerPath):Zone.Identifier" -ErrorAction SilentlyContinue
            if ($PackageSourceDir -and (Test-Path $PackageSourceDir)) {
                Get-ChildItem -Path $PackageSourceDir -Recurse -File -ErrorAction SilentlyContinue | ForEach-Object {
                    Unblock-File -Path $_.FullName -ErrorAction SilentlyContinue
                    Remove-Item -Path "$($_.FullName):Zone.Identifier" -ErrorAction SilentlyContinue
                }
            }

            $effectiveWorkDir = if ($WorkingDirectory -and -not ($WorkingDirectory -like "\\*") -and (Test-Path $WorkingDirectory)) {
                $WorkingDirectory
            } elseif (Test-Path (Split-Path $InstallerPath -Parent)) {
                Split-Path $InstallerPath -Parent
            } else {
                "C:\setup"
            }

            if ($CustomExecution) {
                $res = & $CustomExecution $InstallerPath $effectiveWorkDir
                if ($res -is [hashtable] -or $res -is [PSCustomObject]) {
                    $execSuccess = [bool]$res.Success
                    $execExitCode = $res.ExitCode
                } elseif ($res -is [int]) {
                    $execExitCode = $res
                    $execSuccess = ($execExitCode -eq 0 -or $execExitCode -eq 3010)
                } else {
                    $execSuccess = [bool]$res
                }
            } else {
                if ($WaitSeconds -gt 0) {
                    $proc = Start-Process -FilePath $InstallerPath -ArgumentList $Arguments -WorkingDirectory $effectiveWorkDir -PassThru -WindowStyle Hidden
                    if ($proc) {
                        $timeoutMs = $WaitSeconds * 1000
                        $exited = $proc.WaitForExit($timeoutMs)
                        if (-not $exited -or (-not $proc.HasExited)) {
                            if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                                Write-ServisLog "[$AppName] Proces instalacije je prekoračio timeout (${WaitSeconds}s). Prisilno prekidam proces (PID: $($proc.Id))..." -Level "WARN" -Component $AppName
                            }
                            try { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue } catch {}
                            $execExitCode = -1
                            $execSuccess = $false
                        } else {
                            $execExitCode = $proc.ExitCode
                            $execSuccess = ($execExitCode -eq 0 -or $execExitCode -eq 3010)
                        }
                    } else {
                        $execExitCode = -1
                        $execSuccess = $false
                    }
                } else {
                    $proc = Start-Process -FilePath $InstallerPath -ArgumentList $Arguments -WorkingDirectory $effectiveWorkDir -Wait -PassThru -WindowStyle Hidden
                    $execExitCode = if ($proc) { $proc.ExitCode } else { -1 }
                    $execSuccess = ($execExitCode -eq 0 -or $execExitCode -eq 3010)
                }
            }

            if ($VerificationCheck) {
                $isVerified = & $VerificationCheck
                if ($isVerified) {
                    $execSuccess = $true
                }
            }

            if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                Write-ServisLog "[$AppName] Lokalna instalacija završena (ExitCode: $execExitCode, Success: $execSuccess)." -Level $(if ($execSuccess) { 'SUCCESS' } else { 'WARN' }) -Component $AppName
            }

            return @{ Success = $execSuccess; ExitCode = $execExitCode; FallbackUsed = $false }
        } catch {
            $execExitCode = -1
            $execSuccess = $false
            if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                Write-ServisLog "[$AppName] Greška pri lokalnom pokretanju instalera: $($_.Exception.Message)" -Level "WARN" -Component $AppName
            }
        }
    }

    return @{ Success = $execSuccess; ExitCode = $execExitCode; FallbackUsed = $fallbackUsed }
}

function Install-ServisVCRedist {
    <#
    .SYNOPSIS
        Tiha instalacija Microsoft Visual C++ Redistributable paketa.
    #>
    [CmdletBinding()]
    param(
        [string]$ProgramiRoot = ""
    )
    if (-not $ProgramiRoot) {
        $ProgramiRoot = if (Get-Command Get-ServisProgramiRoot -ErrorAction SilentlyContinue) {
            Get-ServisProgramiRoot
        } else { "C:\setup\Programi" }
    }

    if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
        Write-ServisLog "Pokrecem proveru i instalaciju Visual C++ Redistributables (Izvor: $ProgramiRoot)..." -Level "STEP" -Component "VCRedist"
    }

    $vcDirs = @(
        (Join-Path $ProgramiRoot "VCRedist"),
        (Join-Path $ProgramiRoot "Programi\VCRedist"),
        (Join-Path $ProgramiRoot "VC"),
        "C:\setup\Programi\VCRedist",
        "C:\setup\Programi\Programi\VCRedist",
        "C:\setup\Programi\VC"
    )
    if ($Global:SourceRoot) {
        $vcDirs += (Join-Path $Global:SourceRoot "Programi\VCRedist")
        $vcDirs += (Join-Path $Global:SourceRoot "VCRedist")
    }

    $aioInstallers = @()
    $stdInstallers = @()

    foreach ($vcd in ($vcDirs | Select-Object -Unique)) {
        if ($vcd -and (Test-Path $vcd)) {
            try {
                $exes = Get-ChildItem -Path $vcd -Filter "*.exe" -Recurse -File -ErrorAction SilentlyContinue
                foreach ($e in $exes) {
                    if ($e.Name -match '(?i)aio|all_in_one|visualcppredist_aio') {
                        $aioInstallers += $e
                    } elseif ($e.Name -match '(?i)vcredist|vc_redist|visual_c|visualcpp') {
                        $stdInstallers += $e
                    }
                }
            } catch {}
        }
    }

    # Ako je detektovan All-In-One paket, ima apsolutni prioritet jer sadrzi sve verzije
    if ($aioInstallers.Count -gt 0) {
        $aio = $aioInstallers | Select-Object -First 1
        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
            Write-ServisLog "Pronadjen Visual C++ All-In-One paket: $($aio.FullName). Pokrecem tihu instalaciju..." -Level "STEP" -Component "VCRedist"
        }
        $vRes = Invoke-InstallerWithLocalFallback `
            -AppName "VCRedist-AIO" `
            -InstallerPath $aio.FullName `
            -Arguments "/ai /gm2" `
            -WorkingDirectory (Split-Path $aio.FullName -Parent) `
            -WaitSeconds 300

        # Fallback na /silent ako /ai /gm2 nije vratio uspeh (0 ili 3010)
        if (-not $vRes.Success -and $vRes.ExitCode -ne 0 -and $vRes.ExitCode -ne 3010) {
            if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                Write-ServisLog "AIO instaler sa /ai /gm2 vratio kod $($vRes.ExitCode). Pokusavam fallback sa /silent..." -Level "WARN" -Component "VCRedist"
            }
            $vRes = Invoke-InstallerWithLocalFallback `
                -AppName "VCRedist-AIO" `
                -InstallerPath $aio.FullName `
                -Arguments "/silent" `
                -WorkingDirectory (Split-Path $aio.FullName -Parent) `
                -WaitSeconds 300
        }

        $logLvl = if ($vRes.Success -or $vRes.ExitCode -eq 0 -or $vRes.ExitCode -eq 3010) { "SUCCESS" } else { "WARN" }
        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
            Write-ServisLog "Visual C++ AIO instaler $($aio.Name) zavrsen (ExitCode: $($vRes.ExitCode))." -Level $logLvl -Component "VCRedist"
        }
    } elseif ($stdInstallers.Count -gt 0) {
        foreach ($inst in ($stdInstallers | Select-Object -Unique)) {
            $vRes = Invoke-InstallerWithLocalFallback `
                -AppName "VCRedist" `
                -InstallerPath $inst.FullName `
                -Arguments "/install /quiet /norestart /q" `
                -WorkingDirectory (Split-Path $inst.FullName -Parent)
            $logLvl = if ($vRes.Success -or $vRes.ExitCode -eq 0 -or $vRes.ExitCode -eq 3010) { "SUCCESS" } else { "WARN" }
            if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                Write-ServisLog "VC++ instaler $($inst.Name) zavrsen (ExitCode: $($vRes.ExitCode))." -Level $logLvl -Component "VCRedist"
            }
        }
    } else {
        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
            Write-ServisLog "Nisu pronadjeni lokalni/SMB VC++ paketi. Pokusavam Winget fallback sa timeout zastitom..." -Level "DEBUG" -Component "VCRedist"
        }
        foreach ($wgId in @("Microsoft.VCRedist.2015+.x64", "Microsoft.VCRedist.2015+.x86")) {
            try {
                $wp = Start-Process -FilePath "winget.exe" -ArgumentList "install --id $wgId --silent --accept-package-agreements --accept-source-agreements" -PassThru -WindowStyle Hidden -ErrorAction SilentlyContinue
                if ($wp) {
                    $wgDone = $wp.WaitForExit(120000)
                    if (-not $wgDone -or (-not $wp.HasExited)) {
                        & taskkill.exe /PID $wp.Id /T /F >$null 2>&1
                        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                            Write-ServisLog "Install-ServisVCRedist: Winget za $wgId prekoracio timeout (120s) i zaustavljen." -Level "WARN" -Component "VCRedist"
                        }
                    }
                }
            } catch {}
        }
    }
}

function Install-ServisDotNet {
    <#
    .SYNOPSIS
        Tiha instalacija Microsoft .NET Desktop i ASP.NET Core Runtime paketa.
    #>
    [CmdletBinding()]
    param(
        [string]$ProgramiRoot = ""
    )
    if (-not $ProgramiRoot) {
        $ProgramiRoot = if (Get-Command Get-ServisProgramiRoot -ErrorAction SilentlyContinue) {
            Get-ServisProgramiRoot
        } else { "C:\setup\Programi" }
    }

    $dnDesktopCheck = Test-Path "C:\Program Files\dotnet\shared\Microsoft.WindowsDesktop.App"
    if ($dnDesktopCheck) {
        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
            Write-ServisLog "Microsoft .NET Desktop Runtime je vec instaliran. Preskacem." -Level "SUCCESS" -Component "DotNet"
        }
        return $true
    }

    if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
        Write-ServisLog "Pokrecem instalaciju Microsoft .NET Runtimes (Izvor: $ProgramiRoot)..." -Level "STEP" -Component "DotNet"
    }

    $dotnetDirs = @(
        (Join-Path $ProgramiRoot "DotNet"),
        (Join-Path $ProgramiRoot "Programi\DotNet"),
        "C:\setup\Programi\DotNet",
        "C:\setup\DotNet"
    )
    $dotnetDir = $dotnetDirs | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1

    $installedDotNet = $false
    if ($dotnetDir) {
        $runtimes = @(
            @{ Name = ".NET 8 Desktop Runtime"; Exe = "windowsdesktop-runtime-8.0-win-x64.exe" },
            @{ Name = "ASP.NET Core 8 Runtime"; Exe = "aspnetcore-runtime-8.0-win-x64.exe" },
            @{ Name = ".NET 9 Desktop Runtime"; Exe = "windowsdesktop-runtime-9.0-win-x64.exe" }
        )
        foreach ($rt in $runtimes) {
            $rtPath = Join-Path $dotnetDir $rt.Exe
            if (Test-Path $rtPath) {
                $rtRes = Invoke-InstallerWithLocalFallback `
                    -AppName "DotNet_$($rt.Name -replace '[^a-zA-Z0-9]','_')" `
                    -InstallerPath $rtPath `
                    -Arguments "/install /quiet /norestart" `
                    -WorkingDirectory $dotnetDir
                if ($rtRes.Success) { $installedDotNet = $true }
            }
        }
    }

    if (-not (Test-Path "C:\Program Files\dotnet\shared\Microsoft.WindowsDesktop.App")) {
        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
            Write-ServisLog ".NET paketi nisu kompletirani sa diska/mreze. Pokusavam Winget fallback..." -Level "INFO" -Component "DotNet"
        }
        $null = Invoke-WingetAppInstall -PackageId "Microsoft.DotNet.DesktopRuntime.8"
        $null = Invoke-WingetAppInstall -PackageId "Microsoft.DotNet.DesktopRuntime.9"
    }

    return (Test-Path "C:\Program Files\dotnet\shared\Microsoft.WindowsDesktop.App")
}

function Set-BatteryBarConfiguration {
    <#
    .SYNOPSIS
        Konfigurise BatteryBar u rezimu Floating Toolbar-a za Windows 11.
    .DESCRIPTION
        Kreira nativnu XML konfiguraciju (BatteryBar.Settings.xml) u Default profilu i svim
        postojecim profilima na sistemu, uklanja zastareli nepodrzani BatteryBar.ini,
        postavlja precicu u All Users Startup folderu za automatsko pokretanje,
        postavlja .exe.config sa .NET 4.8 kompatibilnoscu i pokrece proces normalno (bez -WindowStyle Hidden).
    #>
    [CmdletBinding()]
    param(
        [string]$InstalledExe = ""
    )

    if (-not $InstalledExe) {
        $InstalledExe = @(
            "C:\Program Files\BatteryBar\BatteryBar.exe",
            "C:\Program Files (x86)\BatteryBar\BatteryBar.exe"
        ) | Where-Object { Test-Path $_ } | Select-Object -First 1
    }

    if (-not $InstalledExe -or -not (Test-Path $InstalledExe)) {
        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
            Write-ServisLog "GREŠKA: BatteryBar.exe nije pronađen na disku, konfiguracija neuspešna (ERROR)." -Level "ERROR" -Component "BatteryBar"
        }
        if (Get-Command Send-IpcAppUpdate -ErrorAction SilentlyContinue) {
            Send-IpcAppUpdate -AppId "BatteryBar" -Status "ERROR" -Detail "BatteryBar.exe ne postoji na disku"
        }
        return $false
    }

    try {
        $bbInstallDir = Split-Path $InstalledExe -Parent

        # 1. Eliminacija klasične (DeskBand / taskbar) verzije i duplih autostart ključeva
        Get-Process -Name "ShowBatteryBar*" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue

        $legacyDll = Join-Path $bbInstallDir "BatteryBar.dll"
        if (Test-Path $legacyDll) {
            try {
                Start-Process -FilePath "regsvr32.exe" -ArgumentList "/u /s `"$legacyDll`"" -Wait -WindowStyle Hidden -ErrorAction SilentlyContinue
            } catch {}
        }

        foreach ($legacyFile in @("ShowBatteryBar.exe", "BatteryBar.dll", "BarExplorerHook.dll", "BatteryBarSetup-3.6.6.exe")) {
            $lfPath = Join-Path $bbInstallDir $legacyFile
            if (Test-Path $lfPath) {
                Remove-Item -Path $lfPath -Force -ErrorAction SilentlyContinue
            }
        }

        # Uklanjanje duplih Run registarskih ključeva (kako se ne bi pokretao dva puta uz All Users Startup prečicu)
        $runPaths = @(
            "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run",
            "HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run",
            "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"
        )
        foreach ($rp in $runPaths) {
            if (Test-Path $rp) {
                Remove-ItemProperty -Path $rp -Name "BatteryBar" -Force -ErrorAction SilentlyContinue
                Remove-ItemProperty -Path $rp -Name "ShowBatteryBar" -Force -ErrorAction SilentlyContinue
                Remove-ItemProperty -Path $rp -Name "BatteryBarSolo" -Force -ErrorAction SilentlyContinue
            }
        }

        # 2. .NET Runtime konfiguracija (BatteryBar.exe.config)
        $configPath = "$InstalledExe.config"
        $configContent = @"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <startup>
    <supportedRuntime version="v4.0" sku=".NETFramework,Version=v4.8" />
    <supportedRuntime version="v4.0" />
    <supportedRuntime version="v2.0.50727" />
  </startup>
</configuration>
"@
        Set-Content -Path $configPath -Value $configContent -Encoding UTF8 -Force -ErrorAction SilentlyContinue

        # 3. Nativna XML konfiguracija (BatteryBar.Solo.Settings) za Windows 11 plutajuću traku
        $bbXmlContent = @"
<?xml version="1.0" encoding="utf-8"?>
<settings>
  <BatteryBarSolo>
    <Location>1550, 980</Location>
    <AlwaysOnTop>True</AlwaysOnTop>
  </BatteryBarSolo>
  <Setting Name="Solo" Type="System.Boolean">True</Setting>
  <Debug>
    <LoggingLevel>Critical</LoggingLevel>
    <EnableDebugLogging>False</EnableDebugLogging>
  </Debug>
  <License>
  </License>
</settings>
"@

        # 4. Distribucija XML-a u C:\Users\Default (za sve buduce profile) i uklanjanje zaostalog .ini
        $bbDefaultDir = "C:\Users\Default\AppData\Roaming\BatteryBar"
        if (-not (Test-Path $bbDefaultDir)) { New-Item -Path $bbDefaultDir -ItemType Directory -Force -ErrorAction SilentlyContinue | Out-Null }
        Set-Content -Path (Join-Path $bbDefaultDir "BatteryBar.Settings.xml") -Value $bbXmlContent -Encoding UTF8 -Force -ErrorAction SilentlyContinue
        Remove-Item -Path (Join-Path $bbDefaultDir "BatteryBar.ini") -Force -ErrorAction SilentlyContinue

        # 5. Distribucija XML-a u sve postojece profile na sistemu i uklanjanje per-user duplih Startup prečica
        Get-ChildItem -Path "C:\Users" -Directory -Force -ErrorAction SilentlyContinue | Where-Object {
            $_.Name -notmatch '^(Public|Default User|All Users)$'
        } | ForEach-Object {
            $userBbDir = Join-Path $_.FullName "AppData\Roaming\BatteryBar"
            if (-not (Test-Path $userBbDir)) { New-Item -Path $userBbDir -ItemType Directory -Force -ErrorAction SilentlyContinue | Out-Null }
            Set-Content -Path (Join-Path $userBbDir "BatteryBar.Settings.xml") -Value $bbXmlContent -Encoding UTF8 -Force -ErrorAction SilentlyContinue
            Remove-Item -Path (Join-Path $userBbDir "BatteryBar.ini") -Force -ErrorAction SilentlyContinue

            $userStartup = Join-Path $_.FullName "AppData\Roaming\Microsoft\Windows\Start Menu\Programs\Startup"
            if (Test-Path $userStartup) {
                Get-ChildItem -Path $userStartup -Filter "*BatteryBar*.lnk" -File -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue
            }
        }

        if ($env:APPDATA) {
            $curAppDataBb = Join-Path $env:APPDATA "BatteryBar"
            if (-not (Test-Path $curAppDataBb)) { New-Item -Path $curAppDataBb -ItemType Directory -Force -ErrorAction SilentlyContinue | Out-Null }
            Set-Content -Path (Join-Path $curAppDataBb "BatteryBar.Settings.xml") -Value $bbXmlContent -Encoding UTF8 -Force -ErrorAction SilentlyContinue
            Remove-Item -Path (Join-Path $curAppDataBb "BatteryBar.ini") -Force -ErrorAction SilentlyContinue
        }

        # 6. Kreiranje JEDINSTVENE Startup precice za sve korisnike (All Users Startup)
        $startupDir = "C:\ProgramData\Microsoft\Windows\Start Menu\Programs\Startup"
        if (-not (Test-Path $startupDir)) { New-Item -Path $startupDir -ItemType Directory -Force -ErrorAction SilentlyContinue | Out-Null }
        $startupLnk = Join-Path $startupDir "BatteryBar.lnk"
        try {
            $wshShell = New-Object -ComObject WScript.Shell
            $shortcut = $wshShell.CreateShortcut($startupLnk)
            $shortcut.TargetPath = $InstalledExe
            $shortcut.WorkingDirectory = $bbInstallDir
            $shortcut.Description = "BatteryBar Floating Toolbar"
            $shortcut.Save()
            [System.Runtime.InteropServices.Marshal]::ReleaseComObject($wshShell) | Out-Null
            if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                Write-ServisLog "Kreirana jedinstvena BatteryBar Floating Startup precica: $startupLnk" -Level "SUCCESS" -Component "BatteryBar"
            }
        } catch {
            if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                Write-ServisLog "Neuspelo kreiranje Startup prečice za BatteryBar: $($_.Exception.Message)" -Level "WARN" -Component "BatteryBar"
            }
        }

        # 7. Pokretanje isključivo jedne Floating instance (normalno, BEZ -WindowStyle Hidden)
        $existingProcs = @(Get-Process -Name "BatteryBar*" -ErrorAction SilentlyContinue)
        if ($existingProcs.Count -gt 1) {
            if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                Write-ServisLog "Detektovano više BatteryBar procesa ($($existingProcs.Count)). Gasim duplikate radi pokretanja jedne Floating instance..." -Level "WARN" -Component "BatteryBar"
            }
            $existingProcs | Stop-Process -Force -ErrorAction SilentlyContinue
            Start-Sleep -Milliseconds 500
            $existingProcs = @()
        }

        if ($existingProcs.Count -eq 1) {
            if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                Write-ServisLog "Jedna Floating BatteryBar instanca je već aktivna, preskačem ponovno pokretanje." -Level "INFO" -Component "BatteryBar"
            }
        } else {
            if (Test-Path $InstalledExe) {
                if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                    Write-ServisLog "Pokrećem čistu Floating BatteryBar instancu: $InstalledExe" -Level "STEP" -Component "BatteryBar"
                }
                Start-Process -FilePath $InstalledExe -WorkingDirectory $bbInstallDir -ErrorAction SilentlyContinue
            }
        }

        # 8. Fizička verifikacija pre slanja konačnog statusa
        if (Test-Path $InstalledExe) {
            if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                Write-ServisLog "BatteryBar konfiguracija (XML i Startup precica) uspesno primenjena i verifikovana." -Level "SUCCESS" -Component "BatteryBar"
            }
            return $true
        } else {
            if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                Write-ServisLog "GREŠKA: Fizička provera neuspela, BatteryBar.exe ne postoji na disku!" -Level "ERROR" -Component "BatteryBar"
            }
            if (Get-Command Send-IpcAppUpdate -ErrorAction SilentlyContinue) {
                Send-IpcAppUpdate -AppId "BatteryBar" -Status "ERROR" -Detail "Fizička verifikacija neuspela"
            }
            return $false
        }
    } catch {
        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
            Write-ServisLog "Greska pri konfiguraciji BatteryBar: $($_.Exception.Message)" -Level "ERROR" -Component "BatteryBar"
        }
        if (Get-Command Send-IpcAppUpdate -ErrorAction SilentlyContinue) {
            Send-IpcAppUpdate -AppId "BatteryBar" -Status "ERROR" -Detail "Greška: $($_.Exception.Message)"
        }
        return $false
    }
}

function Install-AllServisPrograms {
    param(
        [switch]$IsWorker = $false
    )

    $progStatus = Test-AllServisProgramsInstalled
    if ($progStatus.AllRequiredInstalled) {
        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
            Write-ServisLog "Svi potrebni programi su vec instalirani i verifikovani na sistemu. Preskacem kompletnu instalaciju." -Level "SUCCESS" -Component "Programs"
        }
        if (Get-Command Update-ServisUI -ErrorAction SilentlyContinue) {
            @("DotNet", "Chrome", "WinRAR", "Acrobat", "Office", "BatteryBar") | ForEach-Object {
                Update-ServisUI -AppId $_ -AppStatus "OK" -StatusMessage "$_ je već instaliran"
            }
        }
        if (Get-Command Update-AppWorkerStatus -ErrorAction SilentlyContinue) {
            Update-AppWorkerStatus -Status "Completed" -CurrentApp "Svi programi su vec instalirani!" -Progress 100 -AppId "Shortcuts" -AppState "Completed"
        }
        return
    }

    if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
        Write-ServisLog "Zapocinjem proceduru instalacije programa (IsWorker=$IsWorker)..." -Level "STEP" -Component "Programs"
    }
    $progDir = if (Get-Command Get-ServisProgramiRoot -ErrorAction SilentlyContinue) {
        Get-ServisProgramiRoot -LocalSetupDir "C:\setup"
    } elseif (Get-Command Ensure-ProgramiAvailable -ErrorAction SilentlyContinue) {
        Ensure-ProgramiAvailable -Interactive:(-not $IsWorker)
    } else { "C:\setup\Programi" }

    if (Get-Command Update-AppWorkerStatus -ErrorAction SilentlyContinue) {
        Update-AppWorkerStatus -Status "Running" -CurrentApp "Inicijalizacija instalacije programa..." -Progress 5
    }
    if (-not $IsWorker -and (Get-Command Show-ServisDashboard -ErrorAction SilentlyContinue)) {
        Show-ServisDashboard -LiveStatus "Pripremam sistem za instalaciju aplikacija..." -InstallProgress "Inicijalizacija [====--------------]"
    }

    # ==============================================================================
    # GRUPA A: NON-MSI PROGRAMI (Bezbedni za pokretanje paralelno sa Windows Update)
    # WinRAR (NSIS/SFX), BatteryBar (kopiranje fajlova), Office (Click-to-Run)
    # ==============================================================================

    # 1. WinRAR x64 (Non-MSI, 1-2s instalacija)
    if (Get-Command Update-AppWorkerStatus -ErrorAction SilentlyContinue) {
        Update-AppWorkerStatus -Status "Running" -CurrentApp "Instaliram WinRAR x64..." -Progress 10 -AppId "WinRAR" -AppState "Running"
    }
    if (Get-Command Update-ServisUI -ErrorAction SilentlyContinue) {
        Update-ServisUI -AppId "WinRAR" -AppStatus "RUNNING" -SubPhase "Instalacija: WinRAR" -StatusMessage "Instaliram WinRAR x64..."
    }
    $isWinrarInstalled = (Test-Path "C:\Program Files\WinRAR\WinRAR.exe") -or (Test-Path "C:\Program Files (x86)\WinRAR\WinRAR.exe")
    if ($isWinrarInstalled) {
        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
            Write-ServisLog "WinRAR je vec instaliran na sistemu. Preskacem." -Level "SUCCESS" -Component "WinRAR"
        }
        if (Get-Command Write-DiagnosticEvent -ErrorAction SilentlyContinue) {
            Write-DiagnosticEvent -Event "APP_SKIPPED" -App "WinRAR" -Status "ALREADY_INSTALLED" -Component "WinRAR"
        }
        if (Get-Command Update-ServisUI -ErrorAction SilentlyContinue) {
            Update-ServisUI -AppId "WinRAR" -AppStatus "OK" -StatusMessage "WinRAR x64 već instaliran"
        }
    } else {
        $winrarCandidates = @(
            "C:\setup\Programi\winrar-x64-701.exe",
            "C:\setup\Programi\Programi\winrar-x64-701.exe",
            (Join-Path $progDir "winrar-x64-701.exe"),
            (Join-Path $progDir "Programi\winrar-x64-701.exe"),
            $(if ($Global:SourceRoot) { Join-Path $Global:SourceRoot "Programi\winrar-x64-701.exe" }),
            $(if ($Global:SourceRoot) { Join-Path $Global:SourceRoot "winrar-x64-701.exe" })
        )
        $winrarExe = $winrarCandidates | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1

        if (-not $winrarExe) {
            if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                Write-ServisLog "Lokalni WinRAR instaler nije pronadjen na SSD/USB. Pokusavam Winget/Internet fallback..." -Level "INFO" -Component "WinRAR"
            }
            $wingetOk = Invoke-WingetAppInstall -PackageId "RARLab.WinRAR"
            if (-not $wingetOk -and (Get-Command Test-Internet -ErrorAction SilentlyContinue) -and (Test-Internet)) {
                try {
                    $dlPath = "C:\setup\Programi\winrar-x64-701.exe"
                    $dlDir = Split-Path $dlPath -Parent
                    if (-not (Test-Path $dlDir)) { New-Item -Path $dlDir -ItemType Directory -Force | Out-Null }
                    [System.Net.ServicePointManager]::SecurityProtocol = [System.Net.SecurityProtocolType]::Tls12
                    (New-Object System.Net.WebClient).DownloadFile("https://www.rarlab.com/rar/winrar-x64-701.exe", $dlPath)
                    if (Test-Path $dlPath) {
                        $winrarExe = $dlPath
                    }
                } catch {}
            }
        }

        if ($winrarExe) {
            Unblock-File -Path $winrarExe -ErrorAction SilentlyContinue
            if (Get-Command Wait-ServisUICooperativePause -ErrorAction SilentlyContinue) {
                Wait-ServisUICooperativePause -CheckpointName "Instalacija: WinRAR"
            }
            if (Get-Command Update-ServisUI -ErrorAction SilentlyContinue) {
                Update-ServisUI -SubPhase "Instalacija: WinRAR" -StatusMessage "Instaliram WinRAR x64..." -ProgressPercent 65
            }
            if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                Write-ServisLog "Pokrecem tihi instaler WinRAR: $winrarExe /s" -Level "STEP" -Component "WinRAR"
            }
            if (Get-Command Write-DiagnosticEvent -ErrorAction SilentlyContinue) {
                Write-DiagnosticEvent -Event "APP_INSTALL_START" -App "WinRAR" -Status "RUNNING" -Component "WinRAR"
            }
            if (-not $IsWorker -and (Get-Command Show-ServisDashboard -ErrorAction SilentlyContinue)) {
                Show-ServisDashboard -LiveStatus "Instaliram WinRAR x64 (tihi rezim)..." -InstallProgress "WinRAR x64 [==========--------]"
            }
            $swWr = [System.Diagnostics.Stopwatch]::StartNew()
            $wCode = -1
            try {
                $proc = Start-Process -FilePath $winrarExe -ArgumentList "/s" -Wait -PassThru -WindowStyle Hidden
                $swWr.Stop()
                $wCode = if ($proc) { $proc.ExitCode } else { -1 }
                $exitDesc = if (Get-Command Format-ExitCodeDescription -ErrorAction SilentlyContinue) { Format-ExitCodeDescription $wCode } else { "$wCode" }
                if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                    Write-ServisLog "Zavrsena instalacija WinRAR-a. ExitCode: $exitDesc" -Level $(if ($wCode -eq 0) { 'SUCCESS' } else { 'WARN' }) -Component "WinRAR"
                }
            } catch {
                $swWr.Stop()
                if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                    Write-ServisLog "Greska pri instalaciji WinRAR: $($_.Exception.Message)" -Level "ERROR" -Component "WinRAR"
                }
            }
            Remove-WinRARDesktopShortcut

            $wrNow = (Test-Path "C:\Program Files\WinRAR\WinRAR.exe") -or (Test-Path "C:\Program Files (x86)\WinRAR\WinRAR.exe")
            if (-not $wrNow) {
                if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                    Write-ServisLog "WinRAR.exe nije pronadjen nakon instalera. Pokrecem Winget dopunu..." -Level "WARN" -Component "WinRAR"
                }
                $null = Invoke-WingetAppInstall -PackageId "RARLab.WinRAR"
                $wrNow = (Test-Path "C:\Program Files\WinRAR\WinRAR.exe") -or (Test-Path "C:\Program Files (x86)\WinRAR\WinRAR.exe")
            }
            if (Get-Command Write-DiagnosticEvent -ErrorAction SilentlyContinue) {
                Write-DiagnosticEvent -Event "APP_INSTALL_RESULT" -App "WinRAR" -Status $(if ($wrNow) { 'SUCCESS' } else { 'FAILED' }) -ExitCode $wCode -DurationSec ($swWr.Elapsed.TotalSeconds) -Component "WinRAR"
            }
            if ($wrNow) {
                if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                    Write-ServisLog "WinRAR x64 uspesno verifikovan na disku." -Level "SUCCESS" -Component "WinRAR"
                }
            } else {
                if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                    Write-ServisLog "UPOZORENJE: WinRAR instaler je zavrsio, ali WinRAR.exe NIJE pronadjen!" -Level "ERROR" -Component "WinRAR"
                }
            }
            if (Get-Command Update-ServisUI -ErrorAction SilentlyContinue) {
                Update-ServisUI -AppId "WinRAR" -AppStatus $(if ($wrNow) { "OK" } else { "ERROR" }) -StatusMessage $(if ($wrNow) { "WinRAR x64 uspešno instaliran" } else { "Greška pri instalaciji WinRAR-a" })
            }
        }
    }

    if (Get-Command Update-AppWorkerStatus -ErrorAction SilentlyContinue) {
        Update-AppWorkerStatus -Status "Running" -CurrentApp "Instaliran WinRAR x64" -Progress 18 -AppId "WinRAR" -AppState "Completed"
    }

    # 2. BatteryBar (Uklonjeno iz pozadinskog radnika - centralizovano u Fazi 4)
    if ($IsWorker) {
        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
            Write-ServisLog "Pozadinski radnik: BatteryBar je prepušten centralizovanoj Fazi 4 (Invoke-Phase4Apps) radi eliminacije duple instalacije." -Level "INFO" -Component "BatteryBar"
        }
    } else {
        # Direktno pokretanje van pozadinskog radnika (samostalni mod)
        if (Get-Command Update-AppWorkerStatus -ErrorAction SilentlyContinue) {
            Update-AppWorkerStatus -Status "Running" -CurrentApp "Podesavam BatteryBar (laptop)..." -Progress 20 -AppId "BatteryBar" -AppState "Running"
        }
        if (Get-Command Update-ServisUI -ErrorAction SilentlyContinue) {
            Update-ServisUI -AppId "BatteryBar" -AppStatus "RUNNING" -SubPhase "Podešavanje: BatteryBar" -StatusMessage "Podešavam BatteryBar..."
        }
        $isLaptop = Test-BatteryPresent

        if ($isLaptop) {
            $installedExe = @(
                "C:\Program Files\BatteryBar\BatteryBar.exe",
                "C:\Program Files (x86)\BatteryBar\BatteryBar.exe"
            ) | Where-Object { Test-Path $_ } | Select-Object -First 1

            if ($installedExe) {
                if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                    Write-ServisLog "BatteryBar je vec instaliran na laptopu ($installedExe). Preskacem pokretanje instalera." -Level "SUCCESS" -Component "BatteryBar"
                }
                if (Get-Command Write-DiagnosticEvent -ErrorAction SilentlyContinue) {
                    Write-DiagnosticEvent -Event "APP_SKIPPED" -App "BatteryBar" -Status "ALREADY_INSTALLED" -Component "BatteryBar"
                }
                if (Get-Command Update-ServisUI -ErrorAction SilentlyContinue) {
                    Update-ServisUI -AppId "BatteryBar" -AppStatus "OK" -StatusMessage "BatteryBar već instaliran"
                }
                Set-BatteryBarConfiguration -InstalledExe $installedExe
                if (Get-Command Update-AppWorkerStatus -ErrorAction SilentlyContinue) {
                    Update-AppWorkerStatus -Status "Running" -CurrentApp "Podesen BatteryBar" -Progress 28 -AppId "BatteryBar" -AppState "Completed"
                }
            } else {
                $targetDir = "C:\Program Files\BatteryBar"
                if (Get-Command Wait-ServisUICooperativePause -ErrorAction SilentlyContinue) {
                    Wait-ServisUICooperativePause -CheckpointName "Instalacija: BatteryBar"
                }
                if (Get-Command Update-ServisUI -ErrorAction SilentlyContinue) {
                    Update-ServisUI -SubPhase "Instalacija: BatteryBar" -StatusMessage "Instaliram BatteryBar..." -ProgressPercent 68
                }
                if (Get-Command Write-DiagnosticEvent -ErrorAction SilentlyContinue) {
                    Write-DiagnosticEvent -Event "APP_INSTALL_START" -App "BatteryBar" -Status "RUNNING" -Component "BatteryBar"
                }
                $swBb = [System.Diagnostics.Stopwatch]::StartNew()
                $bbPortableCandidates = @(
                    "C:\setup\Programi\BatteryBar",
                    "C:\setup\Programi\Programi\BatteryBar",
                    (Join-Path $progDir "BatteryBar"),
                    (Join-Path $progDir "Programi\BatteryBar"),
                    $(if ($Global:SourceRoot) { Join-Path $Global:SourceRoot "Programi\BatteryBar" })
                )
                $bbDir = $bbPortableCandidates | Where-Object { $_ -and (Test-Path (Join-Path $_ "BatteryBar.exe")) } | Select-Object -First 1

                $copiedPortable = $false
                if ($bbDir) {
                    if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                        Write-ServisLog "Pronadjen BatteryBar folder: $bbDir. Kopiram Floating paket u Program Files..." -Level "STEP" -Component "BatteryBar"
                    }
                    if (-not (Test-Path $targetDir)) { New-Item -Path $targetDir -ItemType Directory -Force | Out-Null }
                    Copy-Item -Path "$bbDir\*" -Destination $targetDir -Exclude @("ShowBatteryBar.exe", "BatteryBar.dll", "BarExplorerHook.dll", "BatteryBarSetup*.exe") -Recurse -Force -ErrorAction SilentlyContinue
                    if (Test-Path (Join-Path $targetDir "BatteryBar.exe")) {
                        $copiedPortable = $true
                        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                            Write-ServisLog "BatteryBar uspesno kopiran u $targetDir." -Level "SUCCESS" -Component "BatteryBar"
                        }
                    }
                }

                if (-not $copiedPortable) {
                    if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                        Write-ServisLog "BatteryBar prenosivi paket nije pronađen u $progDir, preskačem bez blokiranja..." -Level "WARN" -Component "BatteryBar"
                    }
                }

                # Provera putanje gde se BatteryBar instalirao (moze biti x64 ili x86)
                $installedExe = @(
                    "C:\Program Files\BatteryBar\BatteryBar.exe",
                    "C:\Program Files (x86)\BatteryBar\BatteryBar.exe"
                ) | Where-Object { Test-Path $_ } | Select-Object -First 1

                # Konfiguracija BatteryBar-a (nativni XML, Startup precica, .NET config)
                if ($installedExe) {
                    Set-BatteryBarConfiguration -InstalledExe $installedExe
                }

                if ($swBb) {
                    $swBb.Stop()
                    $bbPhysical = (Test-Path "C:\Program Files\BatteryBar\BatteryBar.exe") -or (Test-Path "C:\Program Files (x86)\BatteryBar\BatteryBar.exe")
                    $bbNow = [bool]($installedExe -and $bbPhysical)
                    if (Get-Command Write-DiagnosticEvent -ErrorAction SilentlyContinue) {
                        Write-DiagnosticEvent -Event "APP_INSTALL_RESULT" -App "BatteryBar" -Status $(if ($bbNow) { 'SUCCESS' } else { 'SKIPPED' }) -DurationSec ($swBb.Elapsed.TotalSeconds) -Component "BatteryBar"
                    }
                    if (Get-Command Update-ServisUI -ErrorAction SilentlyContinue) {
                        Update-ServisUI -AppId "BatteryBar" -AppStatus $(if ($bbNow) { "OK" } else { "WARN" }) -StatusMessage $(if ($bbNow) { "BatteryBar uspešno podešen" } else { "Preskočeno - BatteryBar prenosivi paket nije pronađen" })
                    }
                }

                if (Get-Command Update-AppWorkerStatus -ErrorAction SilentlyContinue) {
                    Update-AppWorkerStatus -Status "Running" -CurrentApp "Podesen BatteryBar" -Progress 28 -AppId "BatteryBar" -AppState "Completed"
                }
            }
        } else {
            if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                Write-ServisLog "Sistem nije prenosni racunar (desktop nema bateriju). Preskacem BatteryBar." -Level "INFO" -Component "BatteryBar"
            }
            if (Get-Command Write-DiagnosticEvent -ErrorAction SilentlyContinue) {
                Write-DiagnosticEvent -Event "APP_SKIPPED" -App "BatteryBar" -Status "NOT_APPLICABLE" -Component "BatteryBar" -Message "Sistem nema bateriju (desktop racunar)."
            }
            if (Get-Command Update-ServisUI -ErrorAction SilentlyContinue) {
                Update-ServisUI -AppId "BatteryBar" -AppStatus "OK" -StatusMessage "BatteryBar nije potreban (desktop računar)"
            }
            if (Get-Command Update-AppWorkerStatus -ErrorAction SilentlyContinue) {
                Update-AppWorkerStatus -Status "Running" -CurrentApp "BatteryBar preskocen (nije laptop)" -Progress 28 -AppId "BatteryBar" -AppState "Skipped"
            }
        }
    }

    # 3. Microsoft Office 2021/2024 Professional Plus (ODT 100% Offline Click-to-Run)
    if (Get-Command Update-AppWorkerStatus -ErrorAction SilentlyContinue) {
        Update-AppWorkerStatus -Status "Running" -CurrentApp "Instaliram Microsoft Office Pro Plus..." -Progress 30 -AppId "Office" -AppState "Running"
    }
    if (Get-Command Update-ServisUI -ErrorAction SilentlyContinue) {
        Update-ServisUI -AppId "Office" -AppStatus "RUNNING" -SubPhase "Instalacija: Office 2024" -StatusMessage "Instaliram Microsoft Office 2024..."
    }
    $isOfficeInstalled = (Test-Path "C:\Program Files\Microsoft Office\root\Office16\WINWORD.EXE") -or (Test-Path "C:\Program Files (x86)\Microsoft Office\root\Office16\WINWORD.EXE")
    if ($isOfficeInstalled) {
        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
            Write-ServisLog "Microsoft Office je vec instaliran na sistemu. Preskacem instalaciju." -Level "SUCCESS" -Component "Office"
        }
        if (Get-Command Write-DiagnosticEvent -ErrorAction SilentlyContinue) {
            Write-DiagnosticEvent -Event "APP_SKIPPED" -App "Office 2024" -Status "ALREADY_INSTALLED" -Component "Office"
        }
        if (Get-Command Update-ServisUI -ErrorAction SilentlyContinue) {
            Update-ServisUI -AppId "Office" -AppStatus "OK" -StatusMessage "Microsoft Office već instaliran"
        }
    } else {
        $officeCandidates = @(
            "C:\setup\Programi\Office 2024\files",
            "C:\setup\Programi\Office 2024",
            "C:\setup\Programi\Programi\Office 2024\files",
            "C:\setup\Programi\Programi\Office 2024",
            (Join-Path $progDir "Office 2024\files"),
            (Join-Path $progDir "Office 2024"),
            (Join-Path $progDir "Programi\Office 2024\files"),
            (Join-Path $progDir "Programi\Office 2024"),
            $(if ($Global:SourceRoot) { Join-Path $Global:SourceRoot "Programi\Office 2024\files" }),
            $(if ($Global:SourceRoot) { Join-Path $Global:SourceRoot "Programi\Office 2024" })
        )
        $OfficeFilesDir = $officeCandidates | Where-Object { $_ -and (Test-Path (Join-Path $_ "setup.exe")) } | Select-Object -First 1

        if (-not $OfficeFilesDir) {
            if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                Write-ServisLog "Lokalni Office 2024 paketi nisu pronadjeni na SSD/USB. Pokusavam NAS fallback..." -Level "WARN" -Component "Office"
            }
            if (Get-Command Sync-ProgramiFromNas -ErrorAction SilentlyContinue) {
                Sync-ProgramiFromNas -DestinationDir "C:\setup\Programi" | Out-Null
                $OfficeFilesDir = $officeCandidates | Where-Object { $_ -and (Test-Path (Join-Path $_ "setup.exe")) } | Select-Object -First 1
            }
        }

        if ($OfficeFilesDir) {
            $OdtSetup = Join-Path $OfficeFilesDir "setup.exe"
            $OdtConfig = Join-Path $OfficeFilesDir "Configure.xml"

            # Tacna detekcija SourcePath foldera koji sadrzi podfolder Office\Data
            $officeSourcePath = ""
            $dataCandidates = @(
                $OfficeFilesDir,
                (Split-Path $OfficeFilesDir -Parent),
                "C:\setup\Programi\Office 2024",
                "C:\setup\Programi\Office 2024\files",
                (Join-Path $progDir "Office 2024"),
                (Join-Path $progDir "Office 2024\files"),
                $(if ($Global:SourceRoot) { Join-Path $Global:SourceRoot "Programi\Office 2024" }),
                $(if ($Global:SourceRoot) { Join-Path $Global:SourceRoot "Programi\Office 2024\files" }),
                $progDir
            )
            foreach ($cand in $dataCandidates) {
                if ($cand -and (Test-Path (Join-Path $cand "Office\Data"))) {
                    $officeSourcePath = $cand
                    break
                }
            }
            if (-not $officeSourcePath) {
                $officeSourcePath = $OfficeFilesDir
            }

            # Tacna verzija iz Office\Data ili podrazumevana offline verzija
            $officeVersion = "16.0.14334.20848"
            $vDir = Get-ChildItem -Path (Join-Path $officeSourcePath "Office\Data") -Directory -Filter "16.0.*" -ErrorAction SilentlyContinue | Select-Object -First 1
            if ($vDir) {
                $officeVersion = $vDir.Name
            }

            if (-not (Test-Path $OdtConfig)) {
                $cleanConfig = @"
<Configuration>
  <Add OfficeClientEdition="64" Channel="PerpetualVL2021" Version="$officeVersion" SourcePath="$officeSourcePath">
    <Product ID="ProPlus2021Volume">
      <Language ID="en-us" />
      <ExcludeApp ID="Groove" />
      <ExcludeApp ID="Lync" />
      <ExcludeApp ID="OneDrive" />
      <ExcludeApp ID="Teams" />
    </Product>
  </Add>
  <Display Level="None" AcceptEULA="TRUE" />
  <Updates Enabled="TRUE" />
  <Property Name="AUTOACTIVATE" Value="1" />
</Configuration>
"@
                Set-Content -Path $OdtConfig -Value $cleanConfig -Encoding UTF8 -Force
            }
            Unblock-File -Path $OdtSetup -ErrorAction SilentlyContinue
            Unblock-File -Path $OdtConfig -ErrorAction SilentlyContinue

            # Prekini prethodno zaglavljene ODT ili Click-to-Run procese
            @("setup", "officeclicktorun", "officec2rclient", "appvshnotify") | ForEach-Object {
                Get-Process -Name $_ -ErrorAction SilentlyContinue | Where-Object { 
                    $_.Path -like "*Office*" -or $_.Path -like "*setup*" 
                } | Stop-Process -Force -ErrorAction SilentlyContinue
            }

            if (Get-Command Wait-ServisUICooperativePause -ErrorAction SilentlyContinue) {
                Wait-ServisUICooperativePause -CheckpointName "Instalacija: Office 2024"
            }
            if (Get-Command Update-ServisUI -ErrorAction SilentlyContinue) {
                Update-ServisUI -SubPhase "Instalacija: Office 2024" -StatusMessage "Instaliram Microsoft Office 2024..." -ProgressPercent 72
            }
            if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                Write-ServisLog "Pokrecem 100% offline ODT: $OdtSetup /configure `"$OdtConfig`" (SourcePath=$officeSourcePath, Version=$officeVersion)" -Level "STEP" -Component "Office"
            }
            if (Get-Command Write-DiagnosticEvent -ErrorAction SilentlyContinue) {
                Write-DiagnosticEvent -Event "APP_INSTALL_START" -App "Office 2024" -Status "RUNNING" -Component "Office" -Data @{ SourcePath = $officeSourcePath; Version = $officeVersion }
            }

            $OdtConfigFull = (Resolve-Path $OdtConfig -ErrorAction SilentlyContinue).Path
            if (-not $OdtConfigFull) { $OdtConfigFull = $OdtConfig }

            $swOff = [System.Diagnostics.Stopwatch]::StartNew()
            $proc = Start-Process -FilePath $OdtSetup -ArgumentList "/configure `"$OdtConfigFull`"" -WorkingDirectory $OfficeFilesDir -WindowStyle Hidden -PassThru
            $oCode = -1
            if ($proc) {
                $sec = 0
                while (-not $proc.HasExited) {
                    $sec++
                    Start-Sleep -Seconds 1
                    if (-not $IsWorker -and (Get-Command Show-ServisDashboard -ErrorAction SilentlyContinue)) {
                        $pct = [math]::Min(55, [math]::Max(30, [math]::Round(30 + ($sec / 150) * 25)))
                        $slots = 18
                        $filled = [math]::Min($slots, [math]::Max(3, [math]::Round(($pct / 100) * $slots)))
                        $empty = $slots - $filled
                        $bar = ("=" * $filled) + ("-" * $empty)
                        $m = [math]::Floor($sec / 60)
                        $s = $sec % 60
                        $timeStr = "{0:D2}:{1:D2}" -f $m, $s
                        Show-ServisDashboard -LiveStatus "Instaliram Microsoft Office Pro Plus (ODT offline, vreme: $timeStr)..." -InstallProgress "Office Pro Plus ($pct%) [$bar]"
                    } elseif ($IsWorker -and ($sec % 5 -eq 0)) {
                        $workerPct = [math]::Min(55, [math]::Max(30, [math]::Round(30 + ($sec / 150) * 25)))
                        Update-AppWorkerStatus -Status "Running" -CurrentApp "Instaliram Office ($sec s)..." -Progress $workerPct -AppId "Office" -AppState "Running"
                    }
                }

                # Cekanje na OfficeClickToRun i kompletiranje registracije proizvoda u Registru
                $c2rWait = 0
                $c2rMaxWait = 300
                while ($c2rWait -lt $c2rMaxWait) {
                    $wordOk = (Test-Path "C:\Program Files\Microsoft Office\root\Office16\WINWORD.EXE") -or (Test-Path "C:\Program Files (x86)\Microsoft Office\root\Office16\WINWORD.EXE")
                    $regOk = (Get-ItemProperty -Path "HKLM:\SOFTWARE\Microsoft\Office\ClickToRun\Configuration" -Name "ProductReleaseIds" -ErrorAction SilentlyContinue) -or `
                             (Test-Path "HKLM:\SOFTWARE\Microsoft\Office\ClickToRun\ProductReleaseIDs") -or `
                             (Test-Path "HKLM:\SOFTWARE\WOW6432Node\Microsoft\Office\ClickToRun\ProductReleaseIDs")
                    
                    $setupRunning = Get-Process -Name "setup" -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "*Office*" }

                    if ($wordOk -and $regOk -and -not $setupRunning) {
                        # Dodatnih 5 sekundi za flush registarskih zapisa i otpustanje file lock-ova
                        Start-Sleep -Seconds 5
                        break
                    }
                    Start-Sleep -Seconds 2
                    $c2rWait += 2
                    if (-not $IsWorker -and (Get-Command Show-ServisDashboard -ErrorAction SilentlyContinue) -and ($c2rWait % 10 -eq 0)) {
                        Show-ServisDashboard -LiveStatus "Zavrsavam Office instalaciju i registraciju licenci (${c2rWait}s)..."
                    }
                }

                $swOff.Stop()
                $oCode = $proc.ExitCode
                $exitDesc = if (Get-Command Format-ExitCodeDescription -ErrorAction SilentlyContinue) { Format-ExitCodeDescription $oCode } else { "$oCode" }
                if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                    Write-ServisLog "Zavrsena Office ODT instalacija. ExitCode: $exitDesc (Trajanje: $($sec + $c2rWait) s)" -Level $(if ($oCode -eq 0) { 'SUCCESS' } else { 'WARN' }) -Component "Office"
                }
            }

            $offNow = (Test-Path "C:\Program Files\Microsoft Office\root\Office16\WINWORD.EXE") -or (Test-Path "C:\Program Files (x86)\Microsoft Office\root\Office16\WINWORD.EXE")
            if (Get-Command Write-DiagnosticEvent -ErrorAction SilentlyContinue) {
                Write-DiagnosticEvent -Event "APP_INSTALL_RESULT" -App "Office 2024" -Status $(if ($offNow) { 'SUCCESS' } else { 'FAILED' }) -ExitCode $oCode -DurationSec ($swOff.Elapsed.TotalSeconds) -Component "Office"
            }
            if ($offNow) {
                if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                    Write-ServisLog "Microsoft Office uspesno verifikovan na disku (WINWORD.EXE)." -Level "SUCCESS" -Component "Office"
                }
            } else {
                if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                    Write-ServisLog "UPOZORENJE: Office instaler je zavrsio, ali WINWORD.EXE NIJE pronadjen!" -Level "ERROR" -Component "Office"
                }
            }
            if (Get-Command Update-ServisUI -ErrorAction SilentlyContinue) {
                Update-ServisUI -AppId "Office" -AppStatus $(if ($offNow) { "OK" } else { "ERROR" }) -StatusMessage $(if ($offNow) { "Microsoft Office uspešno instaliran" } else { "Greška pri instalaciji Office-a" })
            }
        }
    }

    if (Get-Command Update-AppWorkerStatus -ErrorAction SilentlyContinue) {
        Update-AppWorkerStatus -Status "Running" -CurrentApp "Instaliran Microsoft Office" -Progress 58 -AppId "Office" -AppState "Completed"
    }

    # ==============================================================================
    # GRUPA B: MSI-ZAVISNI PROGRAMI (Zahtevaju proveru Windows Installer mutexa)
    # .NET Runtimes (WiX/MSI bundle), Google Chrome (MSI), Adobe Acrobat Pro (MSI)
    # ==============================================================================

    # 4. Microsoft .NET & ASP.NET Core Runtime
    if ($IsWorker) {
        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
            Write-ServisLog "Pozadinski radnik: Instalacija programa (Microsoft .NET Runtimes) se odlaze za Fazu 4 radi prevencije MSI 1618 kolizije sa Windows Update-om." -Level "INFO" -Component "Worker"
        }
        if (Get-Command Update-AppWorkerStatus -ErrorAction SilentlyContinue) {
            Update-AppWorkerStatus -Status "Running" -CurrentApp "Microsoft .NET Runtimes (odlozeno za Fazu 4)" -Progress 60 -AppId "DotNet" -AppState "Deferred"
        }
    } else {
        if (Get-Command Update-AppWorkerStatus -ErrorAction SilentlyContinue) {
            Update-AppWorkerStatus -Status "Running" -CurrentApp "Instaliram Microsoft .NET Runtimes..." -Progress 60 -AppId "DotNet" -AppState "Running"
        }
        if (Get-Command Update-ServisUI -ErrorAction SilentlyContinue) {
            Update-ServisUI -AppId "DotNet" -AppStatus "RUNNING" -SubPhase "Instalacija: .NET Runtimes" -StatusMessage "Instaliram .NET Desktop Runtime..."
        }
        $dnDesktopCheck = Test-Path "C:\Program Files\dotnet\shared\Microsoft.WindowsDesktop.App"
        if ($dnDesktopCheck) {
            if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                Write-ServisLog "Microsoft .NET Desktop Runtime je vec instaliran na sistemu. Preskacem." -Level "SUCCESS" -Component "DotNet"
            }
            if (Get-Command Write-DiagnosticEvent -ErrorAction SilentlyContinue) {
                Write-DiagnosticEvent -Event "APP_SKIPPED" -App "DotNet" -Status "ALREADY_INSTALLED" -Component "DotNet"
            }
            if (Get-Command Update-ServisUI -ErrorAction SilentlyContinue) {
                Update-ServisUI -AppId "DotNet" -AppStatus "OK" -StatusMessage ".NET Desktop Runtime već instaliran"
            }
        } else {
            if (Get-Command Grant-SetupAclPermissions -ErrorAction SilentlyContinue) {
                Grant-SetupAclPermissions
            }
            $null = Wait-MsiMutexAvailable -TimeoutSec 180

            $dotnetDirCandidates = @(
                "C:\setup\Programi\DotNet",
                "C:\setup\Programi\Programi\DotNet",
                (Join-Path $progDir "DotNet"),
                (Join-Path $progDir "Programi\DotNet"),
                $(if ($Global:SourceRoot) { Join-Path $Global:SourceRoot "Programi\DotNet" }),
                "C:\setup\DotNet"
            )
            $dotnetDir = $dotnetDirCandidates | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1

            $installedDotNet = $false
            if ($dotnetDir) {
                if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                    Write-ServisLog "Pronadjen DotNet direktorijum: $dotnetDir" -Level "INFO" -Component "DotNet"
                }
                $runtimes = @(
                    @{ Name = ".NET 8 Desktop Runtime"; Exe = "windowsdesktop-runtime-8.0-win-x64.exe" },
                    @{ Name = "ASP.NET Core 8 Runtime"; Exe = "aspnetcore-runtime-8.0-win-x64.exe" },
                    @{ Name = ".NET 9 Desktop Runtime"; Exe = "windowsdesktop-runtime-9.0-win-x64.exe" }
                )
                foreach ($rt in $runtimes) {
                    $rtPath = Join-Path $dotnetDir $rt.Exe
                    if (Test-Path $rtPath) {
                        if (Get-Command Grant-SetupAclPermissions -ErrorAction SilentlyContinue) {
                            Grant-SetupAclPermissions
                        }
                        $null = Wait-MsiMutexAvailable -TimeoutSec 120
                        Remove-PendingRebootLocks
                        Unblock-File -Path $rtPath -ErrorAction SilentlyContinue

                        if (Get-Command Wait-ServisUICooperativePause -ErrorAction SilentlyContinue) {
                            Wait-ServisUICooperativePause -CheckpointName "Instalacija: $($rt.Name)"
                        }
                        if (Get-Command Update-ServisUI -ErrorAction SilentlyContinue) {
                            Update-ServisUI -SubPhase "Instalacija: $($rt.Name)" -StatusMessage "Instaliram $($rt.Name)..." -ProgressPercent 76
                        }
                        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                            Write-ServisLog "Pokrecem tihi instaler: $($rt.Name) ($rtPath)" -Level "STEP" -Component "DotNet"
                        }
                        if (Get-Command Write-DiagnosticEvent -ErrorAction SilentlyContinue) {
                            Write-DiagnosticEvent -Event "APP_INSTALL_START" -App "DotNet" -Status "RUNNING" -Component "DotNet" -Data @{ Runtime = $rt.Name }
                        }
                        if (-not $IsWorker -and (Get-Command Show-ServisDashboard -ErrorAction SilentlyContinue)) {
                            Show-ServisDashboard -LiveStatus "Instaliram $($rt.Name) (tihi offline rezim)..." -InstallProgress "$($rt.Name) [==========--------]"
                        }
                        $swRt = [System.Diagnostics.Stopwatch]::StartNew()
                        $rtLog = "C:\setup\dotnet_$($rt.Name -replace '[^a-zA-Z0-9]','_').log"
                        try {
                            $proc = Start-Process -FilePath $rtPath -ArgumentList "/install /quiet /norestart /log `"$rtLog`"" -Wait -PassThru -WindowStyle Hidden
                            $rtCode = if ($proc) { $proc.ExitCode } else { -1 }

                            if ($rtCode -eq 1603 -or $rtCode -eq 0x80070652 -or $rtCode -eq -2147023278 -or ($rtCode -ne 0 -and $rtCode -ne 3010)) {
                                $exitDesc = if (Get-Command Format-ExitCodeDescription -ErrorAction SilentlyContinue) { Format-ExitCodeDescription $rtCode } else { "$rtCode" }
                                if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                                    Write-ServisLog "Instalacija $($rt.Name) vratila kod $exitDesc. Cistim lockove i ponavljam bez log parametra za 3s..." -Level "WARN" -Component "DotNet"
                                }
                                Remove-PendingRebootLocks
                                if (Get-Command Grant-SetupAclPermissions -ErrorAction SilentlyContinue) {
                                    Grant-SetupAclPermissions
                                }
                                Start-Sleep -Seconds 3
                                $null = Wait-MsiMutexAvailable -TimeoutSec 120
                                # Retry sa cistim argumentima (bez navodnika u putanji loga)
                                $proc = Start-Process -FilePath $rtPath -ArgumentList "/install /quiet /norestart" -Wait -PassThru -WindowStyle Hidden
                                $rtCode = if ($proc) { $proc.ExitCode } else { -1 }
                            }

                            $swRt.Stop()
                            $exitDesc = if (Get-Command Format-ExitCodeDescription -ErrorAction SilentlyContinue) { Format-ExitCodeDescription $rtCode } else { "$rtCode" }
                            if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                                Write-ServisLog "Zavrsena instalacija $($rt.Name). ExitCode: $exitDesc" -Level $(if ($rtCode -eq 0 -or $rtCode -eq 3010) { 'SUCCESS' } else { 'WARN' }) -Component "DotNet"
                            }
                            if (Get-Command Write-DiagnosticEvent -ErrorAction SilentlyContinue) {
                                Write-DiagnosticEvent -Event "APP_INSTALL_RESULT" -App "DotNet" -Status $(if ($rtCode -eq 0 -or $rtCode -eq 3010) { 'SUCCESS' } else { 'FAILED' }) -ExitCode $rtCode -DurationSec ($swRt.Elapsed.TotalSeconds) -Component "DotNet" -Data @{ Runtime = $rt.Name }
                            }
                            if ($rtCode -eq 0 -or $rtCode -eq 3010) { $installedDotNet = $true }
                        } catch {
                            $swRt.Stop()
                            if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                                Write-ServisLog "Greska pri instalaciji $($rt.Name): $($_.Exception.Message)" -Level "ERROR" -Component "DotNet"
                            }
                        }
                    }
                }
            }

            if (-not $installedDotNet) {
                $NiniteCandidates = @(
                    (Join-Path $progDir "Ninite ASPNET Core Runtime 10 Installer.exe"),
                    $(if ($Global:SourceRoot) { Join-Path $Global:SourceRoot "Programi\Ninite ASPNET Core Runtime 10 Installer.exe" })
                )
                $NiniteExe = $NiniteCandidates | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
                if ($NiniteExe -and (Test-Internet)) {
                    try {
                        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                            Write-ServisLog "Pokrecem Ninite .NET fallback instaler: $NiniteExe" -Level "STEP" -Component "DotNet"
                        }
                        $proc = Start-Process -FilePath $NiniteExe -PassThru
                        $proc.WaitForExit(90000)
                    } catch {}
                }
            }

            $dnNow = Test-Path "C:\Program Files\dotnet\shared\Microsoft.WindowsDesktop.App"
            if (-not $dnNow) {
                if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                    Write-ServisLog "Lokalni .NET paketi nisu kompletirani. Pokusavam Winget fallback..." -Level "INFO" -Component "DotNet"
                }
                $null = Invoke-WingetAppInstall -PackageId "Microsoft.DotNet.DesktopRuntime.8"
                $null = Invoke-WingetAppInstall -PackageId "Microsoft.DotNet.DesktopRuntime.9"
                $dnNow = Test-Path "C:\Program Files\dotnet\shared\Microsoft.WindowsDesktop.App"
            }
            if (Get-Command Update-ServisUI -ErrorAction SilentlyContinue) {
                Update-ServisUI -AppId "DotNet" -AppStatus $(if ($dnNow) { "OK" } else { "ERROR" }) -StatusMessage $(if ($dnNow) { ".NET Desktop Runtime uspešno instaliran" } else { "Greška pri instalaciji .NET Runtime-a" })
            }
        }

        if (Get-Command Update-AppWorkerStatus -ErrorAction SilentlyContinue) {
            Update-AppWorkerStatus -Status "Running" -CurrentApp "Instaliran .NET Runtime" -Progress 72 -AppId "DotNet" -AppState "Completed"
        }
    }

    # 5. Google Chrome (Standalone Enterprise 64-bit MSI / EXE)
    if ($IsWorker) {
        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
            Write-ServisLog "Pozadinski radnik: Instalacija programa (Google Chrome) se odlaze za Fazu 4 radi prevencije MSI 1618 kolizije sa Windows Update-om." -Level "INFO" -Component "Worker"
        }
        if (Get-Command Update-AppWorkerStatus -ErrorAction SilentlyContinue) {
            Update-AppWorkerStatus -Status "Running" -CurrentApp "Google Chrome (odlozeno za Fazu 4)" -Progress 75 -AppId "Chrome" -AppState "Deferred"
        }
    } else {
        if (Get-Command Update-AppWorkerStatus -ErrorAction SilentlyContinue) {
            Update-AppWorkerStatus -Status "Running" -CurrentApp "Instaliram Google Chrome..." -Progress 75 -AppId "Chrome" -AppState "Running"
        }
        if (Get-Command Update-ServisUI -ErrorAction SilentlyContinue) {
            Update-ServisUI -AppId "Chrome" -AppStatus "RUNNING" -SubPhase "Instalacija: Google Chrome" -StatusMessage "Instaliram Google Chrome..."
        }
        $isChromeInstalled = (Test-Path "C:\Program Files\Google\Chrome\Application\chrome.exe") -or (Test-Path "C:\Program Files (x86)\Google\Chrome\Application\chrome.exe")
        if ($isChromeInstalled) {
            if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                Write-ServisLog "Google Chrome je vec instaliran na sistemu. Preskacem." -Level "SUCCESS" -Component "Chrome"
            }
            if (Get-Command Write-DiagnosticEvent -ErrorAction SilentlyContinue) {
                Write-DiagnosticEvent -Event "APP_SKIPPED" -App "Chrome" -Status "ALREADY_INSTALLED" -Component "Chrome"
            }
            if (Get-Command Update-ServisUI -ErrorAction SilentlyContinue) {
                Update-ServisUI -AppId "Chrome" -AppStatus "OK" -StatusMessage "Google Chrome već instaliran"
            }
        } else {
            # Prioritetna pretraga za GoogleChromeStandaloneEnterprise64.msi (*chrome*.msi)
            $chromeMsiCandidates = @(
                (Join-Path $progDir "GoogleChromeStandaloneEnterprise64.msi"),
                (Join-Path $progDir "Programi\GoogleChromeStandaloneEnterprise64.msi"),
                "C:\setup\Programi\GoogleChromeStandaloneEnterprise64.msi",
                "C:\setup\Programi\Programi\GoogleChromeStandaloneEnterprise64.msi",
                $(if ($Global:SourceRoot) { Join-Path $Global:SourceRoot "Programi\GoogleChromeStandaloneEnterprise64.msi" })
            )
            $chromePkg = $chromeMsiCandidates | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
            if (-not $chromePkg -and (Test-Path $progDir)) {
                $foundMsi = Get-ChildItem -Path $progDir -Filter "*chrome*.msi" -Recurse -File -ErrorAction SilentlyContinue | Select-Object -First 1
                if ($foundMsi) { $chromePkg = $foundMsi.FullName }
            }

            # Fallback na ChromeSetup.exe / *chrome*.exe
            if (-not $chromePkg) {
                $chromeExeCandidates = @(
                    (Join-Path $progDir "ChromeSetup.exe"),
                    (Join-Path $progDir "Programi\ChromeSetup.exe"),
                    "C:\setup\Programi\ChromeSetup.exe",
                    "C:\setup\Programi\Programi\ChromeSetup.exe",
                    $(if ($Global:SourceRoot) { Join-Path $Global:SourceRoot "Programi\ChromeSetup.exe" })
                )
                $chromePkg = $chromeExeCandidates | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
            }

            if (-not $chromePkg) {
                if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                    Write-ServisLog "Lokalni Chrome paketi nisu pronadjeni na SSD/USB. Pokusavam Winget/Internet fallback..." -Level "INFO" -Component "Chrome"
                }
                $wingetCh = Invoke-WingetAppInstall -PackageId "Google.Chrome"
                if (-not $wingetCh -and (Get-Command Test-Internet -ErrorAction SilentlyContinue) -and (Test-Internet)) {
                    try {
                        $dlCh = "C:\setup\Programi\ChromeSetup.exe"
                        $dlDir = Split-Path $dlCh -Parent
                        if (-not (Test-Path $dlDir)) { New-Item -Path $dlDir -ItemType Directory -Force | Out-Null }
                        [System.Net.ServicePointManager]::SecurityProtocol = [System.Net.SecurityProtocolType]::Tls12
                        (New-Object System.Net.WebClient).DownloadFile("https://dl.google.com/chrome/install/latest/chrome_installer.exe", $dlCh)
                        if (Test-Path $dlCh) {
                            $chromePkg = $dlCh
                        }
                    } catch {}
                }
            }

            if ($chromePkg) {
                Unblock-File -Path $chromePkg -ErrorAction SilentlyContinue
                if (Get-Command Wait-ServisUICooperativePause -ErrorAction SilentlyContinue) {
                    Wait-ServisUICooperativePause -CheckpointName "Instalacija: Chrome"
                }
                if (Get-Command Update-ServisUI -ErrorAction SilentlyContinue) {
                    Update-ServisUI -SubPhase "Instalacija: Chrome" -StatusMessage "Instaliram Google Chrome..." -ProgressPercent 80
                }
                if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                    Write-ServisLog "Pokrecem instalaciju Google Chrome: $chromePkg" -Level "STEP" -Component "Chrome"
                }
                if (Get-Command Write-DiagnosticEvent -ErrorAction SilentlyContinue) {
                    Write-DiagnosticEvent -Event "APP_INSTALL_START" -App "Chrome" -Status "RUNNING" -Component "Chrome"
                }
                if (-not $IsWorker -and (Get-Command Show-ServisDashboard -ErrorAction SilentlyContinue)) {
                    Show-ServisDashboard -LiveStatus "Instaliram Google Chrome (tihi offline rezim)..." -InstallProgress "Google Chrome [==========--------]"
                }
                $swCh = [System.Diagnostics.Stopwatch]::StartNew()
                $cCode = -1
                try {
                    $sysGoogle = "C:\Windows\System32\config\systemprofile\AppData\Local\Google"
                    if (-not (Test-Path $sysGoogle)) {
                        New-Item -Path $sysGoogle -ItemType Directory -Force -ErrorAction SilentlyContinue | Out-Null
                    }
                    if ($chromePkg.EndsWith(".msi", [System.StringComparison]::OrdinalIgnoreCase)) {
                        $chDir = Split-Path $chromePkg -Parent
                        $msiArgs = "/i `"$chromePkg`" ALLUSERS=1 /qn /norestart /l*v `"C:\setup\chrome_install.log`""
                        $proc = Invoke-SafeMsi -Arguments $msiArgs -WorkingDirectory $chDir
                        $cCode = if ($proc) { $proc.ExitCode } else { -1 }

                        # Ako vrati gresku (npr 1603), pokusaj cist MSI bez ALLUSERS u radnom direktorijumu
                        if ($cCode -ne 0 -and $cCode -ne 3010) {
                            if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                                Write-ServisLog "Chrome MSI sa ALLUSERS=1 vratio $cCode. Pokusavam direktan MSI bez ALLUSERS parametra..." -Level "WARN" -Component "Chrome"
                            }
                            $null = Wait-MsiMutexAvailable -TimeoutSec 60
                            $proc2 = Start-Process "msiexec.exe" -ArgumentList "/i `"$chromePkg`" /qn /norestart" -WorkingDirectory $chDir -Wait -PassThru -WindowStyle Hidden
                            if ($proc2) { $cCode = $proc2.ExitCode }
                        }
                    } else {
                        $null = Wait-MsiMutexAvailable -TimeoutSec 120
                        $proc = Start-Process -FilePath $chromePkg -ArgumentList "/silent /install" -Wait -PassThru -WindowStyle Hidden
                        $cCode = if ($proc) { $proc.ExitCode } else { -1 }
                    }
                    $swCh.Stop()
                    $exitDesc = if (Get-Command Format-ExitCodeDescription -ErrorAction SilentlyContinue) { Format-ExitCodeDescription $cCode } else { "$cCode" }
                    if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                        Write-ServisLog "Zavrsena primarna instalacija Chrome-a. ExitCode: $exitDesc" -Level $(if ($cCode -eq 0 -or $cCode -eq 3010) { 'SUCCESS' } else { 'WARN' }) -Component "Chrome"
                    }
                } catch {
                    $swCh.Stop()
                    if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                        Write-ServisLog "Greska pri pokretanju Chrome instalera: $($_.Exception.Message)" -Level "ERROR" -Component "Chrome"
                    }
                }

                $chNow = (Test-Path "C:\Program Files\Google\Chrome\Application\chrome.exe") -or (Test-Path "C:\Program Files (x86)\Google\Chrome\Application\chrome.exe")

                # AUTOMATSKI FALLBACK NA ChromeSetup.exe
                if ($chromePkg.EndsWith(".msi", [System.StringComparison]::OrdinalIgnoreCase) -and (-not $chNow -or ($cCode -ne 0 -and $cCode -ne 3010))) {
                    if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                        Write-ServisLog "Chrome MSI nije uspesno instalirao pregledac. Pokrecem automatski fallback na ChromeSetup.exe..." -Level "WARN" -Component "Chrome"
                    }
                    $chromeExeCandidates = @(
                        (Join-Path $progDir "ChromeSetup.exe"),
                        (Join-Path $progDir "Programi\ChromeSetup.exe"),
                        "C:\setup\Programi\ChromeSetup.exe",
                        "C:\setup\Programi\Programi\ChromeSetup.exe",
                        $(if ($Global:SourceRoot) { Join-Path $Global:SourceRoot "Programi\ChromeSetup.exe" }),
                        $(if ($Global:SourceRoot) { Join-Path $Global:SourceRoot "ChromeSetup.exe" })
                    )
                    $chromeExe = $chromeExeCandidates | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
                    if ($chromeExe) {
                        Unblock-File -Path $chromeExe -ErrorAction SilentlyContinue
                        $null = Wait-MsiMutexAvailable -TimeoutSec 120
                        Remove-PendingRebootLocks
                        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                            Write-ServisLog "Pokrecem ChromeSetup.exe fallback: $chromeExe /silent /install" -Level "STEP" -Component "Chrome"
                        }
                        try {
                            $procExe = Start-Process -FilePath $chromeExe -ArgumentList "/silent /install" -Wait -PassThru -WindowStyle Hidden
                            if ($procExe) {
                                $cCode = $procExe.ExitCode
                                $exitDesc = if (Get-Command Format-ExitCodeDescription -ErrorAction SilentlyContinue) { Format-ExitCodeDescription $cCode } else { "$cCode" }
                                if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                                    Write-ServisLog "ChromeSetup.exe fallback zavrsen sa kodom: $exitDesc" -Level $(if ($cCode -eq 0 -or $cCode -eq 3010) { 'SUCCESS' } else { 'WARN' }) -Component "Chrome"
                                }
                            }
                        } catch {
                            if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                                Write-ServisLog "Greska pri pokretanju ChromeSetup.exe fallback-a: $($_.Exception.Message)" -Level "ERROR" -Component "Chrome"
                            }
                        }
                        $chNow = (Test-Path "C:\Program Files\Google\Chrome\Application\chrome.exe") -or (Test-Path "C:\Program Files (x86)\Google\Chrome\Application\chrome.exe")
                    } else {
                        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                            Write-ServisLog "ChromeSetup.exe kandidat nije pronadjen za fallback." -Level "WARN" -Component "Chrome"
                        }
                    }
                }

                if (-not $chNow) {
                    if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                        Write-ServisLog "Google Chrome i dalje nije detektovan. Pokrecem finalni Winget fallback..." -Level "WARN" -Component "Chrome"
                    }
                    $null = Invoke-WingetAppInstall -PackageId "Google.Chrome"
                    $chNow = (Test-Path "C:\Program Files\Google\Chrome\Application\chrome.exe") -or (Test-Path "C:\Program Files (x86)\Google\Chrome\Application\chrome.exe")
                }

                if (Get-Command Write-DiagnosticEvent -ErrorAction SilentlyContinue) {
                    Write-DiagnosticEvent -Event "APP_INSTALL_RESULT" -App "Chrome" -Status $(if ($chNow) { 'SUCCESS' } else { 'FAILED' }) -ExitCode $cCode -DurationSec ($swCh.Elapsed.TotalSeconds) -Component "Chrome"
                }
                if ($chNow) {
                    if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                        Write-ServisLog "Google Chrome uspesno verifikovan na disku." -Level "SUCCESS" -Component "Chrome"
                    }
                } else {
                    if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                        Write-ServisLog "UPOZORENJE: Instaler za Google Chrome je zavrsio, ali chrome.exe NIJE pronadjen!" -Level "ERROR" -Component "Chrome"
                    }
                }
                if (Get-Command Update-ServisUI -ErrorAction SilentlyContinue) {
                    Update-ServisUI -AppId "Chrome" -AppStatus $(if ($chNow) { "OK" } else { "ERROR" }) -StatusMessage $(if ($chNow) { "Google Chrome uspešno instaliran" } else { "Greška pri instalaciji Chrome-a" })
                }
            }
        }

        if (Get-Command Update-AppWorkerStatus -ErrorAction SilentlyContinue) {
            Update-AppWorkerStatus -Status "Running" -CurrentApp "Instaliran Google Chrome" -Progress 82 -AppId "Chrome" -AppState "Completed"
        }
    }

    # 6. Adobe Acrobat Pro 2024
    if ($IsWorker) {
        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
            Write-ServisLog "Pozadinski radnik: Instalacija programa (Adobe Acrobat Pro 2024) se odlaze za Fazu 4 radi prevencije MSI 1618 kolizije sa Windows Update-om." -Level "INFO" -Component "Worker"
        }
        if (Get-Command Update-AppWorkerStatus -ErrorAction SilentlyContinue) {
            Update-AppWorkerStatus -Status "Running" -CurrentApp "Adobe Acrobat Pro 2024 (odlozeno za Fazu 4)" -Progress 85 -AppId "Acrobat" -AppState "Deferred"
        }
    } else {
        if (Get-Command Update-AppWorkerStatus -ErrorAction SilentlyContinue) {
            Update-AppWorkerStatus -Status "Running" -CurrentApp "Instaliram Adobe Acrobat Pro 2024..." -Progress 85 -AppId "Acrobat" -AppState "Running"
        }
        if (Get-Command Update-ServisUI -ErrorAction SilentlyContinue) {
            Update-ServisUI -AppId "Acrobat" -AppStatus "RUNNING" -SubPhase "Instalacija: Adobe Acrobat" -StatusMessage "Instaliram Adobe Acrobat Pro 2024..."
        }
    $isAcroInstalled = (Test-Path "C:\Program Files\Adobe\Acrobat DC\Acrobat\Acrobat.exe") -or `
                       (Test-Path "C:\Program Files (x86)\Adobe\Acrobat DC\Acrobat\Acrobat.exe") -or `
                       (Test-Path "C:\Program Files\Adobe\Acrobat 2024\Acrobat\Acrobat.exe") -or `
                       (Test-Path "C:\Program Files (x86)\Adobe\Acrobat 2024\Acrobat\Acrobat.exe")
    if ($isAcroInstalled) {
        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
            Write-ServisLog "Adobe Acrobat je vec instaliran na sistemu. Preskacem instalaciju." -Level "SUCCESS" -Component "Acrobat"
        }
        if (Get-Command Write-DiagnosticEvent -ErrorAction SilentlyContinue) {
            Write-DiagnosticEvent -Event "APP_SKIPPED" -App "Acrobat" -Status "ALREADY_INSTALLED" -Component "Acrobat"
        }
        if (Get-Command Update-ServisUI -ErrorAction SilentlyContinue) {
            Update-ServisUI -AppId "Acrobat" -AppStatus "OK" -StatusMessage "Adobe Acrobat Pro već instaliran"
        }
    } else {
        $acrobatCandidates = @(
            "C:\setup\Programi\Adobe.Acrobat.Pro.2024.x64.Multilingual\Adobe Acrobat Pro",
            "C:\setup\Programi\Programi\Adobe.Acrobat.Pro.2024.x64.Multilingual\Adobe Acrobat Pro",
            "C:\setup\Programi\Adobe.Acrobat.Pro.2024.x64.Multilingual",
            "C:\setup\Programi\Programi\Adobe.Acrobat.Pro.2024.x64.Multilingual",
            "C:\setup\Programi\Acrobat Pro 2024\Adobe.Acrobat.Pro.2024.x64.Multilingual\Adobe Acrobat Pro",
            "C:\setup\Programi\Adobe Acrobat Pro",
            (Join-Path $progDir "Adobe.Acrobat.Pro.2024.x64.Multilingual\Adobe Acrobat Pro"),
            (Join-Path $progDir "Adobe.Acrobat.Pro.2024.x64.Multilingual"),
            (Join-Path $progDir "Programi\Adobe.Acrobat.Pro.2024.x64.Multilingual\Adobe Acrobat Pro"),
            (Join-Path $progDir "Programi\Adobe.Acrobat.Pro.2024.x64.Multilingual"),
            (Join-Path $progDir "Acrobat Pro 2024\Adobe.Acrobat.Pro.2024.x64.Multilingual\Adobe Acrobat Pro"),
            $(if ($Global:SourceRoot) { Join-Path $Global:SourceRoot "Programi\Adobe.Acrobat.Pro.2024.x64.Multilingual\Adobe Acrobat Pro" }),
            $(if ($Global:SourceRoot) { Join-Path $Global:SourceRoot "Programi\Adobe.Acrobat.Pro.2024.x64.Multilingual" }),
            $(if ($Global:SourceRoot) { Join-Path $Global:SourceRoot "Programi\Acrobat Pro 2024\Adobe.Acrobat.Pro.2024.x64.Multilingual\Adobe Acrobat Pro" }),
            (Join-Path $progDir "Adobe Acrobat Pro")
        )
        $acrobatDir = $acrobatCandidates | Where-Object { $_ -and ((Test-Path (Join-Path $_ "Setup.exe")) -or (Test-Path (Join-Path $_ "AcroPro.msi"))) } | Select-Object -First 1

        if (-not $acrobatDir -and (Test-Path $progDir)) {
            $foundSetup = Get-ChildItem -Path $progDir -Filter "Setup.exe" -Recurse -File -ErrorAction SilentlyContinue | Where-Object { $_.FullName -match '(?i)acrobat' } | Select-Object -First 1
            if ($foundSetup) {
                $acrobatDir = Split-Path $foundSetup.FullName -Parent
            }
        }

        if (-not $acrobatDir) {
            if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                Write-ServisLog "Lokalni Adobe Acrobat paketi nisu pronadjeni na SSD/USB. Proveravam NAS/Winget fallback..." -Level "WARN" -Component "Acrobat"
            }
            if (Get-Command Sync-ProgramiFromNas -ErrorAction SilentlyContinue) {
                Sync-ProgramiFromNas -DestinationDir "C:\setup\Programi" | Out-Null
                $acrobatDir = $acrobatCandidates | Where-Object { $_ -and ((Test-Path (Join-Path $_ "Setup.exe")) -or (Test-Path (Join-Path $_ "AcroPro.msi"))) } | Select-Object -First 1
            }
            if (-not $acrobatDir) {
                $null = Invoke-WingetAppInstall -PackageId "Adobe.Acrobat.Reader.64-bit"
            }
        }

        if ($acrobatDir) {
            # Pre instalacije Acrobata osiguraj da Windows Update nije u toku
            # Ako je Windows Update nit u sred instalacije drajvera/paketa, sacekaj da zavrsi krug
            $wuWait = 0
            while ($wuWait -lt 900) {
                $wuFlag = (Get-ItemProperty -Path "HKLM:\SOFTWARE\WIS" -Name "ServisWuActive" -ErrorAction SilentlyContinue).ServisWuActive
                if ($wuFlag -ne 1) {
                    $wuFlag = (Get-ItemProperty -Path "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Installer" -Name "ServisWuActive" -ErrorAction SilentlyContinue).ServisWuActive
                }
                if ($wuFlag -ne 1) { break }
                if ($wuWait % 30 -eq 0) {
                    if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                        Write-ServisLog "Windows Update je aktivan u paralelnoj niti (ServisWuActive = 1). Cekam zavrsetak kruga azuriranja pre Acrobat instalacije... (${wuWait}/900s)" -Level "INFO" -Component "Acrobat"
                    }
                }
                Start-Sleep -Seconds 5
                $wuWait += 5
            }

            $msiOk = Wait-MsiMutexAvailable -TimeoutSec 600 -LogIntervalSec 30
            Remove-PendingRebootLocks

            # Zaustavljanje zaostalih Adobe pozadinskih procesa
            @("armsvc", "AGSService", "AGMService", "Acrobat", "acrotray", "acrocef") | ForEach-Object {
                Stop-Process -Name $_ -Force -ErrorAction SilentlyContinue
            }

            if (Get-Command Wait-ServisUICooperativePause -ErrorAction SilentlyContinue) {
                Wait-ServisUICooperativePause -CheckpointName "Instalacija: Adobe Acrobat Pro"
            }
            if (Get-Command Update-ServisUI -ErrorAction SilentlyContinue) {
                Update-ServisUI -SubPhase "Instalacija: Adobe Acrobat" -StatusMessage "Instaliram Adobe Acrobat Pro 2024..." -ProgressPercent 85
            }
            if (Get-Command Write-DiagnosticEvent -ErrorAction SilentlyContinue) {
                Write-DiagnosticEvent -Event "APP_INSTALL_START" -App "Acrobat" -Status "RUNNING" -Component "Acrobat"
            }
            $swAc = [System.Diagnostics.Stopwatch]::StartNew()
            $acroInstalled = $false
            $proc = $null
            $procPatch = $null
            $procSetup = $null

            # Korak 1 & 2: Razdvojena MSI instalacija (Bazni MSI sa AcroPro.mst + Kumulativni MSP patch)
            $acroMsi = Join-Path $acrobatDir "AcroPro.msi"
            $acroMsp = Join-Path $acrobatDir "AcrobatDCx64Upd2400120604.msp"
            if (-not (Test-Path $acroMsp)) {
                $foundMsp = Get-ChildItem -Path $acrobatDir -Filter "Acrobat*.msp" -ErrorAction SilentlyContinue | Select-Object -First 1
                if ($foundMsp) { $acroMsp = $foundMsp.FullName }
            }
            $acroMst = Join-Path $acrobatDir "AcroPro.mst"

            # Preduslov: Instalacija Visual C++ Runtime (VCRT_x64) bez koga AcroPro.msi pada sa 1603
            $vcrtCandidates = @(
                (Join-Path $acrobatDir "VCRT_x64\vc_runtimeMinimum_x64.msi"),
                (Join-Path $acrobatDir "..\VCRT_x64\vc_runtimeMinimum_x64.msi"),
                "C:\setup\Programi\Adobe.Acrobat.Pro.2024.x64.Multilingual\Adobe Acrobat Pro\VCRT_x64\vc_runtimeMinimum_x64.msi"
            )
            $vcrtMsi = $vcrtCandidates | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
            if ($vcrtMsi) {
                if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                    Write-ServisLog "Instaliram obavezni preduslov za Acrobat: Visual C++ Runtime ($vcrtMsi)..." -Level "STEP" -Component "Acrobat"
                }
                $null = Invoke-SafeMsi -Arguments "/i `"$vcrtMsi`" /qn /norestart" -WorkingDirectory (Split-Path $vcrtMsi -Parent) -MaxWaitSec 120
            }

            if (Test-Path $acroMsi) {
                # Korak 1 (Bazni MSI sa AcroPro.mst)
                if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                    Write-ServisLog "Pokrecem baznu MSI instalaciju Acrobata (Korak 1): $acroMsi" -Level "STEP" -Component "Acrobat"
                }
                if (-not $IsWorker -and (Get-Command Show-ServisDashboard -ErrorAction SilentlyContinue)) {
                    Show-ServisDashboard -LiveStatus "Instaliram Adobe Acrobat Pro 2024 (bazni MSI)..." -InstallProgress "Adobe Acrobat Pro [==========------]"
                }

                $baseArgs = "/i `"$acroMsi`" ALLUSERS=1"
                if (Test-Path $acroMst) {
                    $baseArgs += " TRANSFORMS=`"$acroMst`""
                }
                $baseArgs += " /qn /norestart /l*v `"C:\setup\acrobat_base.log`""

                $proc = Invoke-SafeMsi -Arguments $baseArgs -WorkingDirectory $acrobatDir -MaxWaitSec 300
                $sCode = if ($proc) { $proc.ExitCode } else { -1 }
                $exitDesc = if (Get-Command Format-ExitCodeDescription -ErrorAction SilentlyContinue) { Format-ExitCodeDescription $sCode } else { "$sCode" }
                if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                    Write-ServisLog "Bazna MSI Acrobat instalacija zavrsena sa kodom: $exitDesc" -Level $(if ($sCode -eq 0 -or $sCode -eq 3010) { 'SUCCESS' } else { 'WARN' }) -Component "Acrobat"
                }

                $acroInstalled = (Test-Path "C:\Program Files\Adobe\Acrobat DC\Acrobat\Acrobat.exe") -or `
                                 (Test-Path "C:\Program Files (x86)\Adobe\Acrobat DC\Acrobat\Acrobat.exe") -or `
                                 (Test-Path "C:\Program Files\Adobe\Acrobat 2024\Acrobat\Acrobat.exe") -or `
                                 (Test-Path "C:\Program Files (x86)\Adobe\Acrobat 2024\Acrobat\Acrobat.exe")

                # Korak 2 (Kumulativni MSP patch)
                $baseSuccess = ($sCode -eq 0 -or $sCode -eq 3010 -or $acroInstalled)
                if ($baseSuccess -and (Test-Path $acroMsp)) {
                    if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                        Write-ServisLog "Pokrecem kumulativni MSP patch za Acrobat (Korak 2): $acroMsp" -Level "STEP" -Component "Acrobat"
                    }
                    if (-not $IsWorker -and (Get-Command Show-ServisDashboard -ErrorAction SilentlyContinue)) {
                        Show-ServisDashboard -LiveStatus "Instaliram kumulativni patch za Adobe Acrobat..." -InstallProgress "Adobe Acrobat Patch [============----]"
                    }
                    $null = Wait-MsiMutexAvailable -TimeoutSec 60
                    Remove-PendingRebootLocks
                    $patchArgs = "/p `"$acroMsp`" /qn /norestart /l*v `"C:\setup\acrobat_patch.log`""
                    $procPatch = Invoke-SafeMsi -Arguments $patchArgs -WorkingDirectory $acrobatDir -MaxWaitSec 300
                    $pCode = if ($procPatch) { $procPatch.ExitCode } else { -1 }
                    $pDesc = if (Get-Command Format-ExitCodeDescription -ErrorAction SilentlyContinue) { Format-ExitCodeDescription $pCode } else { "$pCode" }
                    if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                        Write-ServisLog "Acrobat MSP patch zavrsen sa kodom: $pDesc" -Level $(if ($pCode -eq 0 -or $pCode -eq 3010) { 'SUCCESS' } else { 'WARN' }) -Component "Acrobat"
                    }
                }

                $acroInstalled = (Test-Path "C:\Program Files\Adobe\Acrobat DC\Acrobat\Acrobat.exe") -or `
                                 (Test-Path "C:\Program Files (x86)\Adobe\Acrobat DC\Acrobat\Acrobat.exe") -or `
                                 (Test-Path "C:\Program Files\Adobe\Acrobat 2024\Acrobat\Acrobat.exe") -or `
                                 (Test-Path "C:\Program Files (x86)\Adobe\Acrobat 2024\Acrobat\Acrobat.exe")
            }

            # Korak 3: Sekundarni Fallback na Setup.exe ako bazni MSI nije uspeo
            if (-not $acroInstalled) {
                $setupExe = Join-Path $acrobatDir "Setup.exe"
                if (Test-Path $setupExe) {
                    Start-Sleep -Seconds 5
                    $null = Wait-MsiMutexAvailable -TimeoutSec 180
                    Remove-PendingRebootLocks
                    if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                        Write-ServisLog "Bazni MSI nije uspeo ili Acrobat.exe nedostaje. Pokrecem Setup.exe fallback..." -Level "WARN" -Component "Acrobat"
                    }
                    Unblock-File -Path $setupExe -ErrorAction SilentlyContinue
                    $mstPath = Join-Path $acrobatDir "AcroPro.mst"
                    $hasMst = Test-Path $mstPath
                    $acroArgs = if ($hasMst) { '/sPB /rs /msi TRANSFORMS="AcroPro.mst"' } else { '/sAll /msi /norestart ALLUSERS=1' }
                    try {
                        $procSetup = Start-Process -FilePath $setupExe -ArgumentList $acroArgs -WorkingDirectory $acrobatDir -WindowStyle Hidden -PassThru
                        if ($procSetup) {
                            $procSetup.WaitForExit(300000)
                            $sCode = $procSetup.ExitCode
                            # Fallback na /sAll /msi /norestart ALLUSERS=1 ako primarni pokusaj sa TRANSFORMS nije uspeo
                            if ($sCode -ne 0 -and $sCode -ne 3010 -and $hasMst) {
                                if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                                    Write-ServisLog "Setup.exe sa TRANSFORMS vratio $sCode. Pokusavam fallback /sAll /msi /norestart ALLUSERS=1..." -Level "WARN" -Component "Acrobat"
                                }
                                $procSetup2 = Start-Process -FilePath $setupExe -ArgumentList "/sAll /msi /norestart ALLUSERS=1" -WorkingDirectory $acrobatDir -WindowStyle Hidden -PassThru
                                if ($procSetup2) {
                                    $procSetup2.WaitForExit(300000)
                                    $sCode = $procSetup2.ExitCode
                                }
                            }
                            $exitDesc = if (Get-Command Format-ExitCodeDescription -ErrorAction SilentlyContinue) { Format-ExitCodeDescription $sCode } else { "$sCode" }
                            if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                                Write-ServisLog "Acrobat Setup.exe zavrsen sa kodom: $exitDesc" -Level $(if ($sCode -eq 0 -or $sCode -eq 3010) { 'SUCCESS' } else { 'WARN' }) -Component "Acrobat"
                            }
                        }
                    } catch {
                        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                            Write-ServisLog "Greska pri pokretanju Setup.exe: $($_.Exception.Message)" -Level "WARN" -Component "Acrobat"
                        }
                    }
                    $acroInstalled = (Test-Path "C:\Program Files\Adobe\Acrobat DC\Acrobat\Acrobat.exe") -or `
                                     (Test-Path "C:\Program Files (x86)\Adobe\Acrobat DC\Acrobat\Acrobat.exe") -or `
                                     (Test-Path "C:\Program Files\Adobe\Acrobat 2024\Acrobat\Acrobat.exe") -or `
                                     (Test-Path "C:\Program Files (x86)\Adobe\Acrobat 2024\Acrobat\Acrobat.exe")
                }
            }

            # 4. Postavi Acrobat kao podrazumevani PDF pregledac
            Set-AcrobatDefaultPDF

            $swAc.Stop()
            $acNow = (Test-Path "C:\Program Files\Adobe\Acrobat DC\Acrobat\Acrobat.exe") -or `
                     (Test-Path "C:\Program Files (x86)\Adobe\Acrobat DC\Acrobat\Acrobat.exe") -or `
                     (Test-Path "C:\Program Files\Adobe\Acrobat 2024\Acrobat\Acrobat.exe") -or `
                     (Test-Path "C:\Program Files (x86)\Adobe\Acrobat 2024\Acrobat\Acrobat.exe")
            $acCode = if ($procSetup) { $procSetup.ExitCode } elseif ($procPatch) { $procPatch.ExitCode } elseif ($proc) { $proc.ExitCode } else { 0 }
            if (Get-Command Write-DiagnosticEvent -ErrorAction SilentlyContinue) {
                Write-DiagnosticEvent -Event "APP_INSTALL_RESULT" -App "Acrobat" -Status $(if ($acNow) { 'SUCCESS' } else { 'FAILED' }) -ExitCode $acCode -DurationSec ($swAc.Elapsed.TotalSeconds) -Component "Acrobat"
            }
            if ($acNow) {
                if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                    Write-ServisLog "Adobe Acrobat Pro 2024 uspesno verifikovan na disku." -Level "SUCCESS" -Component "Acrobat"
                }
            } else {
                if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                    Write-ServisLog "UPOZORENJE: Instaler za Acrobat je zavrsio, ali Acrobat.exe NIJE pronadjen!" -Level "ERROR" -Component "Acrobat"
                }
            }
            if (Get-Command Update-ServisUI -ErrorAction SilentlyContinue) {
                Update-ServisUI -AppId "Acrobat" -AppStatus $(if ($acNow) { "OK" } else { "ERROR" }) -StatusMessage $(if ($acNow) { "Adobe Acrobat Pro uspešno instaliran" } else { "Greška pri instalaciji Acrobat-a" })
            }
        }
    }

        if (Get-Command Update-AppWorkerStatus -ErrorAction SilentlyContinue) {
            Update-AppWorkerStatus -Status "Running" -CurrentApp "Instaliran Adobe Acrobat Pro 2024" -Progress 96 -AppId "Acrobat" -AppState "Completed"
        }
    }

    # 7. Prečice na radnoj površini
    if (-not $IsWorker) {
        if (Get-Command Update-AppWorkerStatus -ErrorAction SilentlyContinue) {
            Update-AppWorkerStatus -Status "Running" -CurrentApp "Kreiram precice na radnoj povrsini..." -Progress 98 -AppId "Shortcuts" -AppState "Running"
        }
        if (Get-Command Update-ServisUI -ErrorAction SilentlyContinue) {
            Update-ServisUI -AppId "Shortcuts" -AppStatus "RUNNING" -SubPhase "Prečice na radnoj površini" -StatusMessage "Kreiram prečice na radnoj površini..."
        }
        Create-ServisShortcuts

        if (Get-Command Update-ServisUI -ErrorAction SilentlyContinue) {
            Update-ServisUI -AppId "Shortcuts" -AppStatus "OK" -StatusMessage "Prečice na radnoj površini kreirane"
        }
        if (Get-Command Update-AppWorkerStatus -ErrorAction SilentlyContinue) {
            Update-AppWorkerStatus -Status "Completed" -CurrentApp "Svi programi su obradjeni!" -Progress 100 -AppId "Shortcuts" -AppState "Completed"
        }
        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
            Write-ServisLog "Funkcija Install-AllServisPrograms uspesno kompletirana." -Level "SUCCESS" -Component "Programs"
        }
    } else {
        if (Get-Command Update-AppWorkerStatus -ErrorAction SilentlyContinue) {
            Update-AppWorkerStatus -Status "Completed" -CurrentApp "Non-MSI programi instalirani (MSI odlozen za Fazu 4)" -Progress 60 -AppId "Worker" -AppState "Completed"
        }
        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
            Write-ServisLog "Pozadinski radnik: Zavrsena obrada Non-MSI programa (WinRAR, Office). MSI programi (.NET, Chrome, Acrobat) i BatteryBar prepusteni Fazi 4." -Level "SUCCESS" -Component "Worker"
        }
    }
}

function Wait-OfficeInstallationReady {
    param([int]$TimeoutSec = 60)
    $elapsed = 0
    while ($elapsed -lt $TimeoutSec) {
        $regOk = (Get-ItemProperty -Path "HKLM:\SOFTWARE\Microsoft\Office\ClickToRun\Configuration" -Name "ProductReleaseIds" -ErrorAction SilentlyContinue) -or `
                 (Test-Path "HKLM:\SOFTWARE\Microsoft\Office\ClickToRun\ProductReleaseIDs") -or `
                 (Test-Path "HKLM:\SOFTWARE\WOW6432Node\Microsoft\Office\ClickToRun\ProductReleaseIDs")
        if ($regOk) {
            Start-Sleep -Seconds 1
            return $true
        }
        Start-Sleep -Seconds 1
        $elapsed += 1
    }
    return $false
}

function Invoke-ServisExternalLicensingHook {
    <#
    .SYNOPSIS
        Bezbedno lokalno kešira i izvršava eksterni radionički hook za licenciranje sa 180s timeoutom i sanitizacijom logova.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$ScriptPath,
        [string]$ProgramiDir = "C:\setup\Programi",
        [bool]$OfficeInstalled = $false,
        [bool]$WinActivated = $false,
        [bool]$OffActivated = $false,
        [int]$TimeoutSec = 180
    )

    $failHookResult = {
        param([string]$ErrMessage)
        return [PSCustomObject]@{
            Success          = $false
            TimedOut         = $false
            ExitCode         = -1
            Output           = $ErrMessage
            TailSummary      = $ErrMessage
            CachedScriptPath = ""
        }
    }

    if ([string]::IsNullOrWhiteSpace($ScriptPath) -or ($ScriptPath -match '[\r\n\0"%]') -or -not (Test-Path -LiteralPath $ScriptPath -PathType Leaf -ErrorAction SilentlyContinue)) {
        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
            Write-ServisLog "Eksterni licensing hook nije pronađen ili ima nedozvoljenu putanju: $ScriptPath" -Level "WARN" -Component "License"
        }
        return (& $failHookResult "Skripta nije pronađena ili ima nedozvoljenu putanju")
    }

    $resolvedScriptPath = ""
    try {
        $resolvedScriptPath = [System.IO.Path]::GetFullPath($ScriptPath)
        $scriptItem = Get-Item -LiteralPath $resolvedScriptPath -Force -ErrorAction Stop
        if (($scriptItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                Write-ServisLog "Odbijen eksterni licensing hook jer je fajl ReparsePoint (simlink/junction): $resolvedScriptPath" -Level "WARN" -Component "License"
            }
            return (& $failHookResult "Fajl je ReparsePoint")
        }
    } catch {
        return (& $failHookResult "Neispravna putanja hook-a: $($_.Exception.Message)")
    }

    $ext = [System.IO.Path]::GetExtension($resolvedScriptPath).ToLowerInvariant()
    if (@(".ps1", ".cmd", ".bat") -notcontains $ext) {
        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
            Write-ServisLog "Odbijen eksterni licensing hook sa nedozvoljenom ekstenzijom ($ext): $resolvedScriptPath" -Level "WARN" -Component "License"
        }
        return (& $failHookResult "Nedozvoljena ekstenzija hook skripte: $ext")
    }

    $rawCacheRoot = if (Test-Path -LiteralPath "C:\setup" -PathType Container -ErrorAction SilentlyContinue) {
        "C:\setup\Cache\Licensing"
    } else {
        Join-Path $env:TEMP "WIS\Cache\Licensing"
    }
    $cacheRoot = [System.IO.Path]::GetFullPath($rawCacheRoot).TrimEnd('\')
    $cacheRootPrefix = $cacheRoot + '\'

    $resolvedParentDir = [System.IO.Path]::GetFullPath((Split-Path -Path $resolvedScriptPath -Parent)).TrimEnd('\')
    $parentLeaf = Split-Path -Path $resolvedParentDir -Leaf
    $scriptName = Split-Path -Path $resolvedScriptPath -Leaf

    if ([string]::IsNullOrWhiteSpace($scriptName) -or ($scriptName -eq ".") -or ($scriptName -eq "..") -or ($scriptName -match '[:\\/"%\r\n\0]')) {
        return (& $failHookResult "Neispravno ime fajla skripte")
    }

    $alreadyInCache = $resolvedParentDir.Equals($cacheRoot, [System.StringComparison]::OrdinalIgnoreCase) -or $resolvedParentDir.StartsWith($cacheRootPrefix, [System.StringComparison]::OrdinalIgnoreCase)

    $effectiveWorkDir = ""
    $effectiveScriptPath = ""

    if ($alreadyInCache) {
        try {
            $cacheParentItem = Get-Item -LiteralPath $resolvedParentDir -Force -ErrorAction Stop
            if (($cacheParentItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                return (& $failHookResult "Keširani direktorijum je ReparsePoint")
            }
        } catch {
            return (& $failHookResult "Neuspešna provera keširanog direktorijuma")
        }
        $effectiveWorkDir = $resolvedParentDir
        $effectiveScriptPath = $resolvedScriptPath
    } else {
        $pathRoot = [System.IO.Path]::GetPathRoot($resolvedParentDir)
        if ([string]::IsNullOrWhiteSpace($parentLeaf) -or ($parentLeaf -eq ".") -or ($parentLeaf -eq "..") -or ($parentLeaf -match '[:\\/"%\r\n\0]') -or ($resolvedParentDir.Equals($pathRoot.TrimEnd('\'), [System.StringComparison]::OrdinalIgnoreCase))) {
            if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                Write-ServisLog "Odbijeno keširanje licensing hook-a iz nevalidnog/root direktorijuma: $resolvedParentDir" -Level "WARN" -Component "License"
            }
            return (& $failHookResult "Nedozvoljen izvorni direktorijum za keširanje hook-a")
        }

        $targetFolder = [System.IO.Path]::GetFullPath((Join-Path $cacheRoot $parentLeaf)).TrimEnd('\')
        if (-not $targetFolder.StartsWith($cacheRootPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
            if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                Write-ServisLog "Detektovan pokušaj izlaska van keš korena pri keširanju hook-a: $targetFolder" -Level "WARN" -Component "License"
            }
            return (& $failHookResult "Path traversal detektovan pri keširanju hook-a")
        }

        try {
            if (-not (Test-Path -LiteralPath $cacheRoot -PathType Container -ErrorAction SilentlyContinue)) {
                New-Item -Path $cacheRoot -ItemType Directory -Force -ErrorAction Stop | Out-Null
            }
            if (Test-Path -LiteralPath $targetFolder -ErrorAction SilentlyContinue) {
                Remove-Item -LiteralPath $targetFolder -Recurse -Force -ErrorAction SilentlyContinue
            }
            Copy-WisSafeDirectoryTree -SourceDir $resolvedParentDir -DestinationDir $targetFolder

            $candidateLocal = [System.IO.Path]::GetFullPath((Join-Path $targetFolder $scriptName))
            if (-not $candidateLocal.StartsWith($cacheRootPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
                throw "Ciljna putanja izlazi van keš direktorijuma: $candidateLocal"
            }
            if (-not (Test-Path -LiteralPath $candidateLocal -PathType Leaf -ErrorAction SilentlyContinue)) {
                Copy-Item -LiteralPath $resolvedScriptPath -Destination $candidateLocal -Force -ErrorAction Stop
            }
            if (-not (Test-Path -LiteralPath $candidateLocal -PathType Leaf -ErrorAction SilentlyContinue)) {
                throw "Keširani fajl skripte ne postoji nakon kopiranja: $candidateLocal"
            }
            $effectiveScriptPath = $candidateLocal
            $effectiveWorkDir = $targetFolder
        } catch {
            if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                Write-ServisLog "Neuspešno bezbedno lokalno keširanje licensing hook-a '$scriptName': $($_.Exception.Message)" -Level "WARN" -Component "License"
            }
            return (& $failHookResult "Neuspešno lokalno keširanje licensing hook-a: $($_.Exception.Message)")
        }
    }

    if ([string]::IsNullOrWhiteSpace($effectiveWorkDir) -or ($effectiveWorkDir -like "\\*") -or -not ($effectiveScriptPath.StartsWith($cacheRootPrefix, [System.StringComparison]::OrdinalIgnoreCase)) -or -not (Test-Path -LiteralPath $effectiveWorkDir -PathType Container -ErrorAction SilentlyContinue) -or -not (Test-Path -LiteralPath $effectiveScriptPath -PathType Leaf -ErrorAction SilentlyContinue)) {
        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
            Write-ServisLog "Prekinuto izvršavanje licensing hook-a: lokalna keširana putanja nije validna ($effectiveScriptPath)." -Level "WARN" -Component "License"
        }
        return (& $failHookResult "Lokalna keširana putanja hook-a nije validna")
    }

    # Skidanje MOTW i :Zone.Identifier sa svih keširanih fajlova (strogo preko -LiteralPath)
    try {
        Get-ChildItem -LiteralPath $effectiveWorkDir -Recurse -File -Force -ErrorAction SilentlyContinue | ForEach-Object {
            if (($_.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -eq 0) {
                try { Unblock-File -LiteralPath $_.FullName -ErrorAction SilentlyContinue } catch {}
                try {
                    $zonePath = "$($_.FullName):Zone.Identifier"
                    if (Test-Path -LiteralPath $zonePath -ErrorAction SilentlyContinue) {
                        Remove-Item -LiteralPath $zonePath -Force -ErrorAction SilentlyContinue
                    }
                } catch {}
            }
        }
    } catch {}
    try { Unblock-File -LiteralPath $effectiveScriptPath -ErrorAction SilentlyContinue } catch {}
    try {
        $zoneMain = "$($effectiveScriptPath):Zone.Identifier"
        if (Test-Path -LiteralPath $zoneMain -ErrorAction SilentlyContinue) {
            Remove-Item -LiteralPath $zoneMain -Force -ErrorAction SilentlyContinue
        }
    } catch {}

    $psi = New-Object System.Diagnostics.ProcessStartInfo
    if ($ext -ieq ".ps1") {
        $psExe = Join-Path $PSHOME "powershell.exe"
        if (-not (Test-Path -LiteralPath $psExe -PathType Leaf -ErrorAction SilentlyContinue)) {
            $psExe = "powershell.exe"
        }
        $psi.FileName = $psExe
        $quotedScript = Format-Win32CommandLineArgument -Value $effectiveScriptPath -AlwaysQuote
        $progArg = if ($ProgramiDir) { ($ProgramiDir -replace '[\r\n\0]', '') } else { "C:\setup\Programi" }
        $quotedProg = Format-Win32CommandLineArgument -Value $progArg -AlwaysQuote
        $psi.Arguments = "-NoProfile -ExecutionPolicy Bypass -File $quotedScript -ProgramiDir $quotedProg"
    } elseif ($ext -ieq ".cmd" -or $ext -ieq ".bat") {
        if ($effectiveScriptPath -match '[&|<>^%!"]') {
            if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                Write-ServisLog "Odbijen batch licensing hook zbog nedozvoljenih karaktera u putanji: $scriptName" -Level "WARN" -Component "License"
            }
            return (& $failHookResult "Nedozvoljeni karakteri u putanji batch skripte")
        }
        $comSpec = if ($env:ComSpec -and (Test-Path -LiteralPath $env:ComSpec -PathType Leaf -ErrorAction SilentlyContinue)) {
            $env:ComSpec
        } else {
            "C:\Windows\System32\cmd.exe"
        }
        $psi.FileName = $comSpec
        $psi.Arguments = "/d /c `"`"$effectiveScriptPath`"`""
    } else {
        return (& $failHookResult "Nepodržana ekstenzija skripte: $ext")
    }

    $psi.WorkingDirectory = $effectiveWorkDir
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.CreateNoWindow = $true
    $psi.EnvironmentVariables["WIS_PROGRAMI_DIR"] = if ($ProgramiDir) { $ProgramiDir } else { "C:\setup\Programi" }
    $psi.EnvironmentVariables["WIS_OFFICE_INSTALLED"] = if ($OfficeInstalled) { "1" } else { "0" }
    $psi.EnvironmentVariables["WIS_WIN_ACTIVATED"] = if ($WinActivated) { "1" } else { "0" }
    $psi.EnvironmentVariables["WIS_OFF_ACTIVATED"] = if ($OffActivated) { "1" } else { "0" }

    if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
        Write-ServisLog "Pokrećem eksterni licensing hook '$scriptName' iz lokalne putanje ($effectiveWorkDir, Timeout=${TimeoutSec}s)..." -Level "STEP" -Component "License"
    }

    $procRes = Invoke-ProcessStartInfoWithTimeout -StartInfo $psi -TimeoutSec $TimeoutSec -Label $scriptName
    $timedOut = [bool]$procRes.TimedOut
    $exitCode = [int]$procRes.ExitCode
    $safeOutput = [string]$procRes.Output

    $tailLines = @($safeOutput -split "\r?\n" | ForEach-Object { $_.Trim() } | Where-Object { $_ } | Select-Object -Last 3)
    $tailSummary = $tailLines -join " | "

    if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
        $hookLevel = if ($timedOut) { "WARN" } elseif ($exitCode -eq 0) { "INFO" } else { "WARN" }
        $tailPart = if ($tailSummary) { " | Izlaz: $tailSummary" } else { "" }
        Write-ServisLog "Eksterni licensing hook '$scriptName' završen (ExitCode=$exitCode, TimedOut=$timedOut)$tailPart" -Level $hookLevel -Component "License"
    }

    return [PSCustomObject]@{
        Success          = [bool]$procRes.Success
        TimedOut         = $timedOut
        ExitCode         = $exitCode
        Output           = $safeOutput
        TailSummary      = $tailSummary
        CachedScriptPath = $effectiveScriptPath
    }
}

function Invoke-WindowsOfficeActivation {
    param(
        [string]$ProgramiDir = ""
    )
    if (-not $ProgramiDir -and $Global:ProgramiDir) {
        $ProgramiDir = $Global:ProgramiDir
    }
    if (-not $ProgramiDir) {
        $ProgramiDir = "C:\setup\Programi"
    }

    if (Get-Command Wait-ServisUICooperativePause -ErrorAction SilentlyContinue) {
        Wait-ServisUICooperativePause -CheckpointName "Provera OEM i sistemskih licenci"
    }
    if (Get-Command Update-ServisUI -ErrorAction SilentlyContinue) {
        Update-ServisUI -AppId "License" -AppStatus "RUNNING" -SubPhase "Provera licenci" -StatusMessage "Provera statusa OEM i sistemskih licenci..." -ProgressPercent 88
    }
    if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
        Write-ServisLog "Proveravam status sistemskih licenci za Windows i Office..." -Level "INFO" -Component "License"
    }

    # 1. Detekcija prisustva Office-a i obavezno čekanje registracije PRE bilo kog pokušaja aktivacije ili hooka
    $hasOffice = Test-OfficeInstalled
    if ($hasOffice) {
        $null = Wait-OfficeInstallationReady -TimeoutSec 45
    }

    # 2. Čitanje [Licensing] konfiguracije iz Wifi-Servis.ini (preko WIS.Core ako je dostupan, uz lokalni fallback)
    $licSettings = Get-WisLicensingSettingsLocal
    $officeActivationMode = if ($licSettings -and $licSettings.OfficeActivationMode) { [string]$licSettings.OfficeActivationMode } else { "Auto" }
    $officeKmsHost = if ($licSettings -and $licSettings.OfficeKmsHost) { ([string]$licSettings.OfficeKmsHost).Trim() } else { "" }
    $officeKmsPort = if ($licSettings -and $licSettings.OfficeKmsPort -gt 0) { [int]$licSettings.OfficeKmsPort } else { 1688 }
    $officeProductKey = if ($licSettings -and $licSettings.OfficeProductKey) { ([string]$licSettings.OfficeProductKey).Trim() } else { "" }
    $officeSkipped = ($officeActivationMode -ieq "Skip")

    $winActivationMode = if ($licSettings -and $licSettings.WindowsActivationMode) { [string]$licSettings.WindowsActivationMode } else { "Auto" }
    $winKmsHost = if ($licSettings -and $licSettings.WindowsKmsHost) { ([string]$licSettings.WindowsKmsHost).Trim() } else { "" }
    $winKmsPort = if ($licSettings -and $licSettings.WindowsKmsPort -gt 0) { [int]$licSettings.WindowsKmsPort } else { 1688 }
    $winProductKey = if ($licSettings -and $licSettings.WindowsProductKey) { ([string]$licSettings.WindowsProductKey).Trim() } else { "" }
    $winSkipped = ($winActivationMode -ieq "Skip")

    $sensitiveKmsHosts = @($winKmsHost, $officeKmsHost) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -Unique

    if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
        $cfgFoundTxt = if ($licSettings -and $licSettings.Found) { "Da ($($licSettings.IniPath))" } else { "Ne (podrazumevano)" }
        $kmsTxt = if ($officeKmsHost) { "konfigurisan (port $officeKmsPort, host maskiran)" } else { "nije podešen" }
        $keyTxt = if ($officeProductKey.Length -ge 25) { "konfigurisan (maskiran)" } else { "nije podešen" }
        $winKmsTxt = if ($winKmsHost) { "konfigurisan (port $winKmsPort, host maskiran)" } else { "nije podešen" }
        $winKeyTxt = if ($winProductKey.Length -ge 25) { "konfigurisan (maskiran)" } else { "nije podešen" }
        Write-ServisLog "Konfiguracija [Licensing]: Pronađeno=$cfgFoundTxt | OfficeMode=$officeActivationMode (KMS=$kmsTxt, Key=$keyTxt) | WindowsMode=$winActivationMode (KMS=$winKmsTxt, Key=$winKeyTxt)" -Level "INFO" -Component "License"
    }

    # 3. Provera i buđenje Windows Software Protection servisa (sppsvc) pre slmgr.vbs / OSPP.VBS poziva
    try {
        $sppSvc = Get-Service -Name "sppsvc" -ErrorAction SilentlyContinue
        if ($sppSvc -and $sppSvc.Status -ne [System.ServiceProcess.ServiceControllerStatus]::Running) {
            if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                Write-ServisLog "Servis Software Protection (sppsvc) je u stanju '$($sppSvc.Status)'. Pokrećem servis..." -Level "INFO" -Component "License"
            }
            Start-Service -Name "sppsvc" -ErrorAction SilentlyContinue
            $sppSw = [System.Diagnostics.Stopwatch]::StartNew()
            while ($sppSw.Elapsed.TotalSeconds -lt 5) {
                $sppSvc.Refresh()
                if ($sppSvc.Status -eq [System.ServiceProcess.ServiceControllerStatus]::Running) {
                    break
                }
                Start-Sleep -Milliseconds 300
            }
        }
    } catch {}

    # 4. Windows aktivacija (OA3x BIOS ključ, digitalna licenca /ato, [Licensing] KMS/MAK)
    $winStatus = Get-WindowsLicenseStatus
    $winActive = [bool]($winStatus -and $winStatus.Activated)
    $winOa3Present = [bool]($winStatus -and $winStatus.Oa3xPresent)
    $winEdition = if ($winStatus -and $winStatus.Edition) { [string]$winStatus.Edition } else { "" }
    $winOa3Desc = if ($winStatus -and $winStatus.Oa3xDescription) { [string]$winStatus.Oa3xDescription } else { "" }
    $winAppliedMethod = ""
    $winLastErrorCode = ""
    $winLastErrorDesc = ""
    $winEditionMismatch = $false

    if (-not $winActive) {
        if ($winSkipped) {
            $winAppliedMethod = "Skip"
            if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                Write-ServisLog "WindowsActivationMode=Skip je aktivan — preskačem aktivaciju Windows-a po konfiguraciji." -Level "INFO" -Component "License"
            }
            if (Get-Command Send-IpcLog -ErrorAction SilentlyContinue) {
                if ($Global:WisPreserveCurrentPhase -eq $true) {
                    Send-IpcLog -Message "Windows aktivacija preskočena (WindowsActivationMode=Skip)." -Level "INFO" -Tag "[License]"
                } else {
                    Send-IpcLog -Message "Windows aktivacija preskočena (WindowsActivationMode=Skip)." -Level "INFO" -Tag "[License]" -Phase 4
                }
            }
        } elseif ($winActivationMode -in @("Auto", "Kms", "Mak")) {
            $slmgrPath = "C:\Windows\System32\slmgr.vbs"
            $attemptedSlmgrAto = $false

            # (a) OA3x ugrađeni UEFI/BIOS OEM ključ (uz proveru neslaganja edicije)
            if ($winActivationMode -in @("Auto", "Mak")) {
                try {
                    $sls = Get-CimInstance -ClassName SoftwareLicensingService -ErrorAction SilentlyContinue
                    if (-not $sls) {
                        $sls = Get-WmiObject -Class SoftwareLicensingService -ErrorAction SilentlyContinue
                    }
                    if ($sls -and -not [string]::IsNullOrWhiteSpace($sls.OA3xOriginalProductKey)) {
                        $winOa3Present = $true
                        if (-not [string]::IsNullOrWhiteSpace([string]$sls.OA3xOriginalProductKeyDescription)) {
                            $winOa3Desc = ([string]$sls.OA3xOriginalProductKeyDescription).Trim()
                        }
                        if ($winOa3Desc -and $winEdition) {
                            $oa3Norm = $winOa3Desc.ToLowerInvariant()
                            $edNorm = $winEdition.ToLowerInvariant()
                            $oa3IsHome = ($oa3Norm -match '\b(home|core)\b')
                            $oa3IsPro  = ($oa3Norm -match '\b(pro|professional)\b')
                            $edIsHome  = ($edNorm -match '\b(home|core)\b')
                            $edIsPro   = ($edNorm -match '\b(pro|professional|enterprise|education)\b')
                            if (($oa3IsHome -and $edIsPro) -or ($oa3IsPro -and $edIsHome)) {
                                $winEditionMismatch = $true
                                if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                                    Write-ServisLog "UPOZORENJE: Detektovano neslaganje Windows edicije ($winEdition) i ugrađenog BIOS OA3x ključa ($winOa3Desc)!" -Level "WARN" -Component "License"
                                }
                            }
                        }

                        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                            Write-ServisLog "Detektovan ugrađeni UEFI/BIOS OA3x OEM ključ. Primenjujem sistemsku Windows licencu..." -Level "INFO" -Component "License"
                        }
                        $winAppliedMethod = "OA3x"
                        $ipkRes = Invoke-CscriptWithTimeout -ScriptPath $slmgrPath -Arguments @("/ipk", $sls.OA3xOriginalProductKey) -TimeoutSec 25
                        $atoRes = Invoke-CscriptWithTimeout -ScriptPath $slmgrPath -Arguments @("/ato") -TimeoutSec 25
                        $attemptedSlmgrAto = $true

                        $oa3CombinedOut = Protect-WisLicenseLogText -Text ((@($ipkRes.Output, $atoRes.Output) -join " ") -replace '\r?\n', ' ').Trim()
                        if ($oa3CombinedOut -match '(0x[0-9A-Fa-f]{8})') {
                            $winLastErrorCode = $matches[1]
                        }
                        if ($atoRes.Output) {
                            $winLastErrorDesc = Protect-WisLicenseLogText -Text (([string]$atoRes.Output -replace '\r?\n', ' ').Trim())
                        }
                        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                            $ipkTxt = if ($ipkRes.Output) { Protect-WisLicenseLogText -Text (([string]$ipkRes.Output -replace '\r?\n', ' ').Trim()) } else { "bez izlaza" }
                            $atoTxt = if ($atoRes.Output) { Protect-WisLicenseLogText -Text (([string]$atoRes.Output -replace '\r?\n', ' ').Trim()) } else { "bez izlaza" }
                            Write-ServisLog "Windows OA3x rezultat: /ipk ExitCode=$($ipkRes.ExitCode) ($ipkTxt) | /ato ExitCode=$($atoRes.ExitCode) ($atoTxt)" -Level "INFO" -Component "License"
                        }
                        $winStatus = Get-WindowsLicenseStatus
                        $winActive = [bool]($winStatus -and $winStatus.Activated)
                    }
                } catch {}
            }

            # (b) Ako nema OA3x ili Windows još nije aktiviran: primena [Licensing] KMS/MAK parametara + obavezni slmgr.vbs /ato (za digitalnu licencu)
            if (-not $winActive) {
                $appliedWinCfg = $false
                if (($winActivationMode -in @("Auto", "Kms")) -and -not [string]::IsNullOrWhiteSpace($winKmsHost)) {
                    if ($winKmsHost -match '^[A-Za-z0-9._:-]+$') {
                        $winAppliedMethod = "KMS"
                        $appliedWinCfg = $true
                        $skmsTarget = "${winKmsHost}:${winKmsPort}"
                        $skmsRes = Invoke-CscriptWithTimeout -ScriptPath $slmgrPath -Arguments @("/skms", $skmsTarget) -TimeoutSec 25
                        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                            $skmsMsg = Protect-WisLicenseLogText -Text (([string]$skmsRes.Output -replace '\r?\n', ' ').Trim()) -SensitiveHosts $sensitiveKmsHosts
                            Write-ServisLog "Primenjen Windows KMS host iz [Licensing]: /skms ExitCode=$($skmsRes.ExitCode) ($skmsMsg)" -Level "INFO" -Component "License"
                        }
                    } else {
                        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                            Write-ServisLog "Ignorisan nevalidan WindowsKmsHost u [Licensing] konfiguraciji (dozvoljeni su samo A-Za-z0-9._:-)." -Level "WARN" -Component "License"
                        }
                    }
                }

                if (($winActivationMode -in @("Auto", "Mak")) -and -not [string]::IsNullOrWhiteSpace($winProductKey) -and $winProductKey.Length -ge 25) {
                    if ($winProductKey -match '^[A-Za-z0-9-]+$') {
                        $winAppliedMethod = if ($winAppliedMethod) { "$winAppliedMethod+MAK" } else { "MAK" }
                        $appliedWinCfg = $true
                        $wIpkRes = Invoke-CscriptWithTimeout -ScriptPath $slmgrPath -Arguments @("/ipk", $winProductKey) -TimeoutSec 25
                        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                            $wIpkMsg = Protect-WisLicenseLogText -Text (([string]$wIpkRes.Output -replace '\r?\n', ' ').Trim()) -SensitiveHosts $sensitiveKmsHosts
                            Write-ServisLog "Primenjen Windows MAK/Volume ključ iz [Licensing]: /ipk ExitCode=$($wIpkRes.ExitCode) ($wIpkMsg)" -Level "INFO" -Component "License"
                        }
                    } else {
                        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                            Write-ServisLog "Ignorisan nevalidan WindowsProductKey format u [Licensing] konfiguraciji (dozvoljeni su samo A-Za-z0-9-)." -Level "WARN" -Component "License"
                        }
                    }
                }

                if ((-not $attemptedSlmgrAto) -or $appliedWinCfg) {
                    if (-not $winAppliedMethod) {
                        $winAppliedMethod = "SLMGR_ATO"
                    }
                    if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                        Write-ServisLog "Pokrećem slmgr.vbs /ato proveru Windows licence (digitalna ili konfigurisana licenca, Mod=$winActivationMode)..." -Level "INFO" -Component "License"
                    }
                    $wAtoRes = Invoke-CscriptWithTimeout -ScriptPath $slmgrPath -Arguments @("/ato") -TimeoutSec 25
                    $wAtoOut = Protect-WisLicenseLogText -Text (([string]$wAtoRes.Output -replace '\r?\n', ' ').Trim()) -SensitiveHosts $sensitiveKmsHosts
                    if ($wAtoOut -match '(0x[0-9A-Fa-f]{8})') {
                        $winLastErrorCode = $matches[1]
                    }
                    if ($wAtoOut) {
                        $winLastErrorDesc = $wAtoOut
                    } elseif ($wAtoRes.TimedOut) {
                        $winLastErrorDesc = "slmgr.vbs /ato prekoračio timeout (25s)"
                    }
                    if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                        Write-ServisLog "Windows /ato rezultat: ExitCode=$($wAtoRes.ExitCode) ($winLastErrorDesc)" -Level $(if ($wAtoRes.Success) { "INFO" } else { "WARN" }) -Component "License"
                    }
                    $winStatus = Get-WindowsLicenseStatus
                    $winActive = [bool]($winStatus -and $winStatus.Activated)
                }
            }
        }
    }

    $offStatus = if ($hasOffice) { Get-OfficeLicenseStatus } else { $null }
    $offActive = if ($hasOffice) { [bool]($offStatus -and $offStatus.Activated) } else { $true }

    $appliedAttemptMethod = ""
    $lastOsppErrorCode = ""
    $lastOsppErrorDesc = ""
    $lastHookExitCode = $null
    $lastHookTail = ""

    # 5. Standardna Office Volume/KMS/MAK aktivacija preko OSPP.VBS
    if ($hasOffice -and $officeSkipped) {
        $appliedAttemptMethod = "Skip"
        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
            Write-ServisLog "OfficeActivationMode=Skip je aktivan — preskačem aktivaciju Microsoft Office-a po konfiguraciji." -Level "INFO" -Component "License"
        }
        if (Get-Command Send-IpcLog -ErrorAction SilentlyContinue) {
            if ($Global:WisPreserveCurrentPhase -eq $true) {
                Send-IpcLog -Message "Office aktivacija preskočena (OfficeActivationMode=Skip)." -Level "INFO" -Tag "[License]"
            } else {
                Send-IpcLog -Message "Office aktivacija preskočena (OfficeActivationMode=Skip)." -Level "INFO" -Tag "[License]" -Phase 4
            }
        }
    } elseif ($hasOffice -and -not $offActive -and ($officeActivationMode -in @("Auto", "Kms", "Mak"))) {
        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
            Write-ServisLog "Microsoft Office nije aktiviran (početni status: $($offStatus.Method) / $($offStatus.LicenseStatusText)). Pokušavam standardnu OSPP aktivaciju (Mod=$officeActivationMode)..." -Level "STEP" -Component "License"
        }
        $osppPaths = @(
            "C:\Program Files\Microsoft Office\Office16\OSPP.VBS",
            "C:\Program Files\Microsoft Office\root\Office16\OSPP.VBS",
            "C:\Program Files (x86)\Microsoft Office\Office16\OSPP.VBS",
            "C:\Program Files (x86)\Microsoft Office\root\Office16\OSPP.VBS"
        )
        $ospp = $osppPaths | Where-Object { Test-Path -LiteralPath $_ -ErrorAction SilentlyContinue } | Select-Object -First 1
        if ($ospp) {
            if (($officeActivationMode -in @("Auto", "Kms")) -and -not [string]::IsNullOrWhiteSpace($officeKmsHost)) {
                if ($officeKmsHost -match '^[A-Za-z0-9._:-]+$') {
                    $appliedAttemptMethod = "KMS"
                    $sethstRes = Invoke-CscriptWithTimeout -ScriptPath $ospp -Arguments @("/sethst:$officeKmsHost") -TimeoutSec 25
                    $setprtRes = Invoke-CscriptWithTimeout -ScriptPath $ospp -Arguments @("/setprt:$officeKmsPort") -TimeoutSec 25
                    if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                        $shMsg = Protect-WisLicenseLogText -Text (([string]$sethstRes.Output -replace '\r?\n', ' ').Trim()) -SensitiveHosts $sensitiveKmsHosts
                        $spMsg = Protect-WisLicenseLogText -Text (([string]$setprtRes.Output -replace '\r?\n', ' ').Trim()) -SensitiveHosts $sensitiveKmsHosts
                        Write-ServisLog "Primenjen Office KMS host iz [Licensing] (port $officeKmsPort): /sethst ExitCode=$($sethstRes.ExitCode) ($shMsg) | /setprt ExitCode=$($setprtRes.ExitCode) ($spMsg)" -Level "INFO" -Component "License"
                    }
                } else {
                    if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                        Write-ServisLog "Ignorisan nevalidan OfficeKmsHost u [Licensing] konfiguraciji (dozvoljeni su samo A-Za-z0-9._:-)." -Level "WARN" -Component "License"
                    }
                }
            }

            if (($officeActivationMode -in @("Auto", "Mak")) -and -not [string]::IsNullOrWhiteSpace($officeProductKey) -and $officeProductKey.Length -ge 25) {
                if ($officeProductKey -match '^[A-Za-z0-9-]+$') {
                    $appliedAttemptMethod = if ($appliedAttemptMethod) { "$appliedAttemptMethod+MAK" } else { "MAK" }
                    $inpRes = Invoke-CscriptWithTimeout -ScriptPath $ospp -Arguments @("/inpkey:$officeProductKey") -TimeoutSec 25
                    if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                        $inpMsg = Protect-WisLicenseLogText -Text (([string]$inpRes.Output -replace '\r?\n', ' ').Trim()) -SensitiveHosts $sensitiveKmsHosts
                        Write-ServisLog "Primenjen Office MAK/Volume ključ iz [Licensing]: /inpkey ExitCode=$($inpRes.ExitCode) ($inpMsg)" -Level "INFO" -Component "License"
                    }
                } else {
                    if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                        Write-ServisLog "Ignorisan nevalidan OfficeProductKey format u [Licensing] konfiguraciji (dozvoljeni su samo A-Za-z0-9-)." -Level "WARN" -Component "License"
                    }
                }
            }

            $actRes = Invoke-CscriptWithTimeout -ScriptPath $ospp -Arguments @("/act") -TimeoutSec 25
            $actOut = [string]$actRes.Output
            if (-not $appliedAttemptMethod) {
                $appliedAttemptMethod = "OSPP"
            }

            if ($actOut -match '(?mi)ERROR CODE:\s*(0x[0-9A-Fa-f]+|-?\d+)') {
                $lastOsppErrorCode = $matches[1].Trim()
            }
            if ($actOut -match '(?mi)ERROR DESCRIPTION:\s*(.+)') {
                $lastOsppErrorDesc = Protect-WisLicenseLogText -Text $matches[1].Trim() -SensitiveHosts $sensitiveKmsHosts
            }
            $actSuccMatch = ($actOut -match '(?i)Product activation successful')

            if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                $actSummary = if ($actRes.TimedOut) {
                    "OSPP.VBS /act je prekoračio timeout (25s)."
                } elseif ($actSuccMatch) {
                    "OSPP.VBS /act: Product activation successful."
                } elseif ($lastOsppErrorCode) {
                    "OSPP.VBS /act greška: $lastOsppErrorCode $(if ($lastOsppErrorDesc) { '(' + $lastOsppErrorDesc + ')' } else { '' })"
                } else {
                    $cleanAct = Protect-WisLicenseLogText -Text (([string]$actOut -replace '\r?\n', ' ').Trim()) -SensitiveHosts $sensitiveKmsHosts
                    if ($cleanAct.Length -gt 240) { $cleanAct = $cleanAct.Substring(0, 240) + "..." }
                    "OSPP.VBS /act ExitCode=$($actRes.ExitCode): $cleanAct"
                }
                Write-ServisLog $actSummary -Level $(if ($actSuccMatch) { "SUCCESS" } else { "INFO" }) -Component "License"
            }

            Start-Sleep -Seconds 2
            $offStatus = Get-OfficeLicenseStatus
            $offActive = [bool]($offStatus -and $offStatus.Activated)
        } else {
            if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                Write-ServisLog "OSPP.VBS nije pronađen na očekivanim Office putanjama." -Level "WARN" -Component "License"
            }
        }
    }

    # 6. Eksterni radionički hook (Programi\Licensing i Programi\CustomScripts) + NAS Sync fallback
    $winNeedsHook = (-not $winActive) -and (-not $winSkipped) -and ($winActivationMode -in @("Auto", "ExternalHook"))
    $offNeedsHook = ($hasOffice -and -not $offActive -and -not $officeSkipped) -and ($officeActivationMode -in @("Auto", "ExternalHook"))
    $needsExternalHook = $winNeedsHook -or $offNeedsHook
    if ($needsExternalHook) {
        $hookBaseDirs = [System.Collections.Generic.List[string]]::new()
        foreach ($candBase in @($ProgramiDir, "C:\setup\Programi")) {
            if ($candBase -and (-not $hookBaseDirs.Contains($candBase))) {
                $hookBaseDirs.Add($candBase)
            }
        }
        if ($Global:SourceRoot) {
            $srcProgDir = Join-Path $Global:SourceRoot "Programi"
            if (-not $hookBaseDirs.Contains($srcProgDir)) {
                $hookBaseDirs.Add($srcProgDir)
            }
        }
        $licCacheDir = "C:\setup\Cache\Licensing"
        if (-not $hookBaseDirs.Contains($licCacheDir)) {
            $hookBaseDirs.Add($licCacheDir)
        }

        $findHooksInBases = {
            param([string[]]$Bases)
            $foundLic = $null
            $foundCs = @()
            foreach ($b in $Bases) {
                if ([string]::IsNullOrWhiteSpace($b) -or -not (Test-Path -LiteralPath $b -ErrorAction SilentlyContinue)) { continue }
                if (-not $foundLic) {
                    $c1 = Join-Path $b "Licensing\Invoke-CustomLicensing.ps1"
                    $c2 = Join-Path $b "Licensing\CustomLicense.cmd"
                    if (Test-Path -LiteralPath $c1 -ErrorAction SilentlyContinue) { $foundLic = $c1 }
                    elseif (Test-Path -LiteralPath $c2 -ErrorAction SilentlyContinue) { $foundLic = $c2 }
                }
                if ($foundCs.Count -eq 0) {
                    $csSub = Join-Path $b "CustomScripts"
                    if (Test-Path -LiteralPath $csSub -ErrorAction SilentlyContinue) {
                        $psList = @(Get-ChildItem -Path $csSub -Filter "*.ps1" -File -ErrorAction SilentlyContinue)
                        if ($psList.Count -gt 0) {
                            $foundCs = $psList
                        }
                    }
                }
                if ($foundLic -and $foundCs.Count -gt 0) { break }
            }
            return [PSCustomObject]@{
                LicensingHookPath  = $foundLic
                CustomScriptFiles  = $foundCs
            }
        }

        $discoveredHooks = & $findHooksInBases $hookBaseDirs.ToArray()
        if (-not $discoveredHooks.LicensingHookPath -and $discoveredHooks.CustomScriptFiles.Count -eq 0) {
            if (Get-Command Sync-ServisLicensingFromNas -ErrorAction SilentlyContinue) {
                if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                    Write-ServisLog "Lokalni licensing hook nije pronađen. Pokušavam sinhronizaciju sa NAS-a (Sync-ServisLicensingFromNas)..." -Level "INFO" -Component "License"
                }
                try {
                    $syncLicRes = Sync-ServisLicensingFromNas -DestinationDir "C:\setup\Cache\Licensing"
                    if ($syncLicRes -and (Get-Command Write-ServisLog -ErrorAction SilentlyContinue)) {
                        Write-ServisLog "NAS licensing sync: Synced=$($syncLicRes.Synced), HasLicensingDir=$($syncLicRes.HasLicensingDir), HasCustomScriptsDir=$($syncLicRes.HasCustomScriptsDir)" -Level "INFO" -Component "License"
                    }
                } catch {}
                $discoveredHooks = & $findHooksInBases @("C:\setup\Cache\Licensing")
            }
        }

        if ($discoveredHooks.LicensingHookPath) {
            if ($offNeedsHook) { $appliedAttemptMethod = "ExternalHook" }
            if ($winNeedsHook) { $winAppliedMethod = "ExternalHook" }
            $hookRes = Invoke-ServisExternalLicensingHook -ScriptPath $discoveredHooks.LicensingHookPath -ProgramiDir $ProgramiDir -OfficeInstalled $hasOffice -WinActivated $winActive -OffActivated $offActive -TimeoutSec 180
            $lastHookExitCode = $hookRes.ExitCode
            $lastHookTail = $hookRes.TailSummary
            Start-Sleep -Seconds 2
            $winStatus = Get-WindowsLicenseStatus
            $winActive = [bool]($winStatus -and $winStatus.Activated)
            if ($hasOffice -and -not $officeSkipped) {
                $offStatus = Get-OfficeLicenseStatus
                $offActive = [bool]($offStatus -and $offStatus.Activated)
            }
        }

        $stillNeedsHook = ((-not $winActive) -and (-not $winSkipped) -and ($winActivationMode -in @("Auto", "ExternalHook"))) -or ($hasOffice -and -not $offActive -and -not $officeSkipped -and ($officeActivationMode -in @("Auto", "ExternalHook")))
        if ($stillNeedsHook -and $discoveredHooks.CustomScriptFiles.Count -gt 0) {
            if ($offNeedsHook -and -not $offActive) { $appliedAttemptMethod = "ExternalHook" }
            if ($winNeedsHook -and -not $winActive) { $winAppliedMethod = "ExternalHook" }
            foreach ($sc in $discoveredHooks.CustomScriptFiles) {
                $csRes = Invoke-ServisExternalLicensingHook -ScriptPath $sc.FullName -ProgramiDir $ProgramiDir -OfficeInstalled $hasOffice -WinActivated $winActive -OffActivated $offActive -TimeoutSec 180
                $lastHookExitCode = $csRes.ExitCode
                $lastHookTail = $csRes.TailSummary
                Start-Sleep -Seconds 2
                $winStatus = Get-WindowsLicenseStatus
                $winActive = [bool]($winStatus -and $winStatus.Activated)
                if ($hasOffice -and -not $officeSkipped) {
                    $offStatus = Get-OfficeLicenseStatus
                    $offActive = [bool]($offStatus -and $offStatus.Activated)
                }
                if (($winActive -or $winSkipped) -and ((-not $hasOffice) -or $officeSkipped -or $offActive)) {
                    break
                }
            }
        }

        if (-not $discoveredHooks.LicensingHookPath -and $discoveredHooks.CustomScriptFiles.Count -eq 0) {
            if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                Write-ServisLog "Nije pronađen nijedan eksterni licensing hook (Licensing\Invoke-CustomLicensing.ps1, CustomLicense.cmd ili CustomScripts\*.ps1) ni lokalno ni na NAS-u." -Level "INFO" -Component "License"
            }
        }
    }

    # 7. Završna evaluacija i strukturirani podaci (bez ključeva)
    $winStatus = Get-WindowsLicenseStatus
    $winActive = [bool]($winStatus -and $winStatus.Activated)
    if ($winStatus) {
        if ($winStatus.Oa3xPresent) { $winOa3Present = $true }
        if (-not [string]::IsNullOrWhiteSpace($winStatus.Edition)) { $winEdition = [string]$winStatus.Edition }
    }
    if ($hasOffice) {
        $offStatus = Get-OfficeLicenseStatus
        $offActive = [bool]($offStatus -and $offStatus.Activated)
    } else {
        $offStatus = $null
        $offActive = $true
    }

    $winMethod = "NONE"
    $winDetail = ""
    if ($winActive) {
        $winMethod = if ($winAppliedMethod) { $winAppliedMethod } elseif ($winStatus -and $winStatus.Method -and $winStatus.Method -ne "NONE") { [string]$winStatus.Method } else { "Activated" }
        $wSkuPart = if ($winStatus -and $winStatus.SkuName) { [string]$winStatus.SkuName } elseif ($winEdition) { $winEdition } else { "Windows" }
        $wStatPart = if ($winStatus -and $winStatus.LicenseStatusText) { [string]$winStatus.LicenseStatusText } else { "Licensed" }
        $winDetail = Protect-WisLicenseLogText -Text "$wSkuPart ($wStatPart)"
    } elseif ($winSkipped) {
        $winMethod = "Skip"
        $winDetail = "Windows aktivacija preskočena (WindowsActivationMode=Skip)"
    } else {
        $winMethod = if ($winStatus -and $winStatus.Method -and $winStatus.Method -ne "NONE") { [string]$winStatus.Method } elseif ($winAppliedMethod) { $winAppliedMethod } else { "NONE" }
        $wDetailParts = [System.Collections.Generic.List[string]]::new()
        if ($winEditionMismatch) {
            $wDetailParts.Add("Neslaganje edicije (OS='$winEdition' vs OA3x='$winOa3Desc')")
        }
        if (-not $winOa3Present) {
            $wDetailParts.Add("BIOS OA3x ključ nije pronađen")
        }
        $winErrEff = if ($winStatus -and -not [string]::IsNullOrWhiteSpace($winStatus.LicenseStatusReason)) {
            [string]$winStatus.LicenseStatusReason
        } elseif (-not [string]::IsNullOrWhiteSpace($winLastErrorCode)) {
            $winLastErrorCode
        } else {
            ""
        }
        if ($winErrEff) {
            $wDetailParts.Add("SLMGR $winErrEff")
        }
        if ($winLastErrorDesc) {
            $wDetailParts.Add($winLastErrorDesc)
        } elseif ($winStatus -and -not [string]::IsNullOrWhiteSpace($winStatus.LicenseStatusText)) {
            $wDetailParts.Add([string]$winStatus.LicenseStatusText)
        }
        if ($winNeedsHook -and $null -ne $lastHookExitCode) {
            $wHookInfo = "ExternalHook ExitCode=$lastHookExitCode"
            if ($lastHookTail) { $wHookInfo += " ($lastHookTail)" }
            $wDetailParts.Add($wHookInfo)
        } elseif (-not $winKmsHost -and ($winProductKey.Length -lt 25) -and -not $winOa3Present) {
            $wDetailParts.Add("nema podešenog Windows KMS/MAK ključa u [Licensing] niti eksternog hooka")
        }
        $winDetail = Protect-WisLicenseLogText -Text ($wDetailParts -join " — ") -SensitiveHosts $sensitiveKmsHosts
        if ([string]::IsNullOrWhiteSpace($winDetail)) {
            $winDetail = "Windows nije aktiviran (Mod=$winActivationMode)"
        }
    }

    $officeMethod = "NONE"
    $officeDetail = ""
    if (-not $hasOffice) {
        $officeMethod = "NotInstalled"
        $officeDetail = "Microsoft Office nije instaliran"
    } elseif ($offActive) {
        $officeMethod = if ($offStatus -and $offStatus.Method -and $offStatus.Method -ne "NONE") { [string]$offStatus.Method } elseif ($appliedAttemptMethod) { $appliedAttemptMethod } else { "Activated" }
        $skuPart = if ($offStatus -and $offStatus.SkuName) { $offStatus.SkuName } else { "Microsoft Office" }
        $statPart = if ($offStatus -and $offStatus.LicenseStatusText) { $offStatus.LicenseStatusText } else { "Licensed" }
        $officeDetail = Protect-WisLicenseLogText -Text "$skuPart ($statPart)"
    } elseif ($officeSkipped) {
        $officeMethod = "Skip"
        $officeDetail = "Office aktivacija preskočena (OfficeActivationMode=Skip)"
    } else {
        $officeMethod = if ($offStatus -and $offStatus.Method -and $offStatus.Method -ne "NONE") { [string]$offStatus.Method } elseif ($appliedAttemptMethod) { $appliedAttemptMethod } else { "NONE" }
        $errCodeEff = if ($offStatus -and -not [string]::IsNullOrWhiteSpace($offStatus.LastErrorCode)) {
            [string]$offStatus.LastErrorCode
        } elseif (-not [string]::IsNullOrWhiteSpace($lastOsppErrorCode)) {
            $lastOsppErrorCode
        } else {
            ""
        }
        $detailParts = [System.Collections.Generic.List[string]]::new()
        if ($errCodeEff) {
            $detailParts.Add("OSPP $errCodeEff")
        }
        if ($lastOsppErrorDesc) {
            $detailParts.Add($lastOsppErrorDesc)
        } elseif ($offStatus -and -not [string]::IsNullOrWhiteSpace($offStatus.LicenseStatusText)) {
            $detailParts.Add([string]$offStatus.LicenseStatusText)
        }
        if ($null -ne $lastHookExitCode) {
            $hookInfo = "ExternalHook ExitCode=$lastHookExitCode"
            if ($lastHookTail) { $hookInfo += " ($lastHookTail)" }
            $detailParts.Add($hookInfo)
        } elseif (-not $officeKmsHost -and ($officeProductKey.Length -lt 25)) {
            $detailParts.Add("nema podešenog KMS hosta/ključa u [Licensing] niti eksternog hooka")
        }
        $officeDetail = Protect-WisLicenseLogText -Text ($detailParts -join " — ") -SensitiveHosts $sensitiveKmsHosts
        if ([string]::IsNullOrWhiteSpace($officeDetail)) {
            $officeDetail = "Office nije aktiviran (Mod=$officeActivationMode)"
        }
    }

    $effectiveWinOk = $winActive -or $winSkipped
    $effectiveOfficeOk = (-not $hasOffice) -or $offActive -or $officeSkipped
    $overallSuccess = $effectiveWinOk -and $effectiveOfficeOk

    $setupTargetDir = if ($Global:LocalSetupDir) { $Global:LocalSetupDir } else { "C:\setup" }
    $writtenStatus = Write-WisLicenseStatusFile `
        -WindowsActivated ([bool]$winActive) `
        -WindowsOa3Present ([bool]$winOa3Present) `
        -WindowsEdition $winEdition `
        -WindowsLicenseMethod $winMethod `
        -WindowsLicenseDetail $winDetail `
        -OfficeInstalled ([bool]$hasOffice) `
        -OfficeActivated ([bool]$offActive) `
        -OfficeActivationMode $officeActivationMode `
        -OfficeLicenseMethod $officeMethod `
        -OfficeLicenseDetail $officeDetail `
        -LocalSetupDir $setupTargetDir

    if (Get-Command Write-DiagnosticEvent -ErrorAction SilentlyContinue) {
        Write-DiagnosticEvent -Event "LICENSE_CHECK_RESULT" -App "License" -Status $(if ($overallSuccess) { 'SUCCESS' } else { 'WARN' }) -Data ([ordered]@{
            windows_activated      = [bool]$winActive
            windows_oa3_present    = [bool]$winOa3Present
            windows_edition        = [string]$winEdition
            windows_license_method = [string]$winMethod
            windows_license_detail = [string]$writtenStatus.windows_license_detail
            office_installed       = [bool]$hasOffice
            office_activated       = [bool]$offActive
            office_activation_mode = [string]$officeActivationMode
            office_license_method  = [string]$officeMethod
            office_license_detail  = [string]$writtenStatus.office_license_detail
            timestamp              = [string]$writtenStatus.timestamp
        }) -Component "License"
    }

    if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
        Write-ServisLog "Status sistemskih licenci: WindowsActivated=$winActive (OA3x=$winOa3Present, WinMethod=$winMethod, WinDetail=$($writtenStatus.windows_license_detail)), OfficeInstalled=$hasOffice, OfficeActivated=$offActive, Mode=$officeActivationMode, Method=$officeMethod, Detail=$($writtenStatus.office_license_detail)" -Level $(if ($overallSuccess) { 'SUCCESS' } else { 'WARN' }) -Component "License"
    }

    if (-not $winActive -and -not $winSkipped) {
        $winActionMsg = "Windows nije aktiviran: $($writtenStatus.windows_license_detail)"
        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
            Write-ServisLog $winActionMsg -Level "WARN" -Component "License"
        }
        if (Get-Command Send-IpcLog -ErrorAction SilentlyContinue) {
            if ($Global:WisPreserveCurrentPhase -eq $true) {
                Send-IpcLog -Message $winActionMsg -Level "WARN" -Tag "[License]"
            } else {
                Send-IpcLog -Message $winActionMsg -Level "WARN" -Tag "[License]" -Phase 4
            }
        }
    }

    if ($hasOffice -and -not $offActive -and -not $officeSkipped) {
        $actionMsg = "Office nije aktiviran: $($writtenStatus.office_license_detail)"
        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
            Write-ServisLog $actionMsg -Level "WARN" -Component "License"
        }
        if (Get-Command Send-IpcLog -ErrorAction SilentlyContinue) {
            if ($Global:WisPreserveCurrentPhase -eq $true) {
                Send-IpcLog -Message $actionMsg -Level "WARN" -Tag "[License]"
            } else {
                Send-IpcLog -Message $actionMsg -Level "WARN" -Tag "[License]" -Phase 4
            }
        }
    }

    if (Get-Command Update-ServisUI -ErrorAction SilentlyContinue) {
        $uiLicMsg = if ($overallSuccess) {
            if ($officeSkipped -and $hasOffice) { "Windows licenca potvrđena (Office: Skip)" } else { "OEM i sistemske licence potvrđene" }
        } else {
            $errPreview = if (-not $winActive -and -not $winSkipped) { $writtenStatus.windows_license_detail } else { $writtenStatus.office_license_detail }
            "Potrebna provera licence ($errPreview)"
        }
        Update-ServisUI -AppId "License" -AppStatus $(if ($overallSuccess) { "OK" } else { "WARN" }) -StatusMessage $uiLicMsg
    }

    $officeDashTxt = if (-not $hasOffice) {
        "[OK] Nije instaliran"
    } elseif ($offActive) {
        "[OK] Licenciran"
    } elseif ($officeSkipped) {
        "[OK] Preskočeno (Skip)"
    } else {
        "[!] Nije aktiviran ($($writtenStatus.office_license_detail))"
    }
    $winDashTxt = if ($winActive) {
        "[OK] Licenciran"
    } elseif ($winSkipped) {
        "[OK] Preskočeno (Skip)"
    } else {
        "[!] Nije aktiviran ($($writtenStatus.windows_license_detail))"
    }
    $actLogs = @(
        "Zavrsni Windows status: $winDashTxt",
        "Zavrsni Office status:  $officeDashTxt"
    )
    if (Get-Command Show-ServisDashboard -ErrorAction SilentlyContinue) {
        Show-ServisDashboard -LiveStatus "Provera sistemskih licenci zavrsena." -SubLogs $actLogs
    }
    Start-Sleep -Seconds 1
}

function Invoke-AppsPhase {
    if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
        Write-ServisLog "=== POCETAK FAZE 4: PROGRAMI, PRECICE I AKTIVACIJA ===" -Level "STEP" -Component "Phase4"
    }

    if ($Global:AppWorkerProc -and (-not $Global:AppWorkerProc.HasExited)) {
        if (Get-Command Show-ServisDashboard -ErrorAction SilentlyContinue) {
            Show-ServisDashboard -LiveStatus "Cekam zavrsetak instalacije programa u pozadini..."
        }
        $Global:AppWorkerProc.WaitForExit()
    }

    # Osiguraj da je Windows Installer mutex slobodan nakon zavrsetka Windows Update-a i pozadinskog radnika
    if (Get-Command Wait-MsiMutexAvailable -ErrorAction SilentlyContinue) {
        $null = Wait-MsiMutexAvailable -TimeoutSec 180
    }

    $progStatus = Test-AllServisProgramsInstalled
    if ($progStatus.AllRequiredInstalled) {
        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
            Write-ServisLog "Svi potrebni programi su uspesno instalirani i verifikovani!" -Level "SUCCESS" -Component "Phase4"
        }
        if (Get-Command Update-ServisUI -ErrorAction SilentlyContinue) {
            @("DotNet", "Chrome", "WinRAR", "Acrobat", "Office", "BatteryBar") | ForEach-Object {
                Update-ServisUI -AppId $_ -AppStatus "OK" -StatusMessage "$_ je verifikovan kao instaliran"
            }
        }
        if (Get-Command Update-AppWorkerStatus -ErrorAction SilentlyContinue) {
            Update-AppWorkerStatus -Status "Completed" -CurrentApp "Svi programi su uspesno instalirani!" -Progress 100 -AppId "Shortcuts" -AppState "Completed"
        }
        if (Get-Command Show-ServisDashboard -ErrorAction SilentlyContinue) {
            Show-ServisDashboard -LiveStatus "[OK] Svi programi su verifikovani kao instalirani!"
        }
        Start-Sleep -Seconds 2
    } else {
        $missingStr = $progStatus.MissingList -join ", "
        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
            Write-ServisLog "Detektovano da sledeci programi nedostaju: $missingStr. Pokrecem instalaciju u prednjem planu..." -Level "WARN" -Component "Phase4"
        }
        if ((-not $progStatus.Acrobat.Installed) -or (-not $progStatus.Office.Installed)) {
            $repoRoot = Get-ServisProgramiRoot -LocalSetupDir "C:\setup" -PreferredDir $Global:ProgramiDir
            if (-not (Test-ProgramiPackageComplete -DirPath $repoRoot) -and (Get-Command Sync-ProgramiFromNas -ErrorAction SilentlyContinue)) {
                $null = Sync-ProgramiFromNas -DestinationDir "C:\setup\Programi" -Force
                if (Test-ProgramiPackageComplete -DirPath "C:\setup\Programi") {
                    $repoRoot = "C:\setup\Programi"
                }
            }
            if (-not (Test-ProgramiPackageComplete -DirPath $repoRoot) -and -not (Test-ProgramiPackageComplete -DirPath "C:\setup\Programi")) {
                $repoErrMsg = "KRITIČNA GREŠKA FAZE 4: Ključni instalacioni paketi (Acrobat/Office) ne postoje ni na NAS SMB repozitorijumu ni lokalno (C:\setup\Programi / USB). Zaustavljam Fazu 4."
                if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                    Write-ServisLog $repoErrMsg -Level "ERROR" -Component "NasProgrami"
                }
                if (Get-Command Send-IpcLog -ErrorAction SilentlyContinue) {
                    Send-IpcLog -Message $repoErrMsg -Level "ERROR" -Tag "[NasProgrami]" -Phase 4
                }
                throw $repoErrMsg
            }
        }
        if (Get-Command Show-ServisDashboard -ErrorAction SilentlyContinue) {
            Show-ServisDashboard -LiveStatus "Pokrecem instalaciju programa koji nedostaju ($missingStr)..."
        }
        Install-AllServisPrograms -IsWorker:$false

        $progStatus = Test-AllServisProgramsInstalled
        if ($progStatus.AllRequiredInstalled) {
            if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                Write-ServisLog "Svi programi uspesno kompletirani nakon ponovnog pokretanja!" -Level "SUCCESS" -Component "Phase4"
            }
        } else {
            if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                Write-ServisLog "UPOZORENJE nakon instalacije: I dalje nedostaju: $($progStatus.MissingList -join ', ')" -Level "ERROR" -Component "Phase4"
            }
        }
    }

    if (Get-Command Show-ServisDashboard -ErrorAction SilentlyContinue) {
        Show-ServisDashboard -LiveStatus "Kreiram precice za programe na Desktopu (Word, Excel, Acrobat)..."
    }
    if (Get-Command Update-ServisUI -ErrorAction SilentlyContinue) {
        Update-ServisUI -AppId "Shortcuts" -AppStatus "RUNNING" -SubPhase "Prečice na radnoj površini" -StatusMessage "Kreiram prečice na radnoj površini..."
    }
    Create-ServisShortcuts
    if (Get-Command Update-ServisUI -ErrorAction SilentlyContinue) {
        Update-ServisUI -AppId "Shortcuts" -AppStatus "OK" -StatusMessage "Prečice na radnoj površini kreirane"
    }
    Start-Sleep -Seconds 1

    Invoke-WindowsOfficeActivation -ProgramiDir $Global:ProgramiDir

    # Striktna verifikacija pre prelaska u Fazu 5
    $progStatusFinal = Test-AllServisProgramsInstalled
    if (-not $progStatusFinal.AllRequiredInstalled) {
        $missingStr = $progStatusFinal.MissingList -join ", "
        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
            Write-ServisLog "Verifikacija neuspesna pre Faze 5: I dalje nedostaju programi ($missingStr). Pokrecem dodatni pokusaj instalacije..." -Level "WARN" -Component "Phase4"
        }
        Install-AllServisPrograms -IsWorker:$false
        $progStatusFinal = Test-AllServisProgramsInstalled
    }

    if (-not $progStatusFinal.AllRequiredInstalled) {
        $missingStr = $progStatusFinal.MissingList -join ", "
        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
            Write-ServisLog "KRITICNA BLOKADA: Nisu instalirani svi obavezni programi ($missingStr). ZABRANJEN PRELAZAK U FAZU 5!" -Level "ERROR" -Component "Phase4"
        }
        if (Get-Command Show-ServisDashboard -ErrorAction SilentlyContinue) {
            Show-ServisDashboard `
                -LiveStatus "[GRESKA - FAZA 4 BLOKIRANA] Nedostaju obavezni programi: $missingStr" `
                -SubLogs @(
                    "KRITICNA GRESKA: Skripta je zaustavljena u Fazi 4.",
                    "Sledeci obavezni programi nisu instalirani: $missingStr",
                    "Prelazak u Fazu 5 (ciscenje i zavrsetak) je STRIKTNO ZABRANJEN.",
                    "Otklonite problem sa instalacionim paketima i ponovo pokrenite skriptu."
                )
        }
        try {
            [System.Console]::Beep(1000, 400)
            Start-Sleep -Milliseconds 150
            [System.Console]::Beep(800, 400)
            Start-Sleep -Milliseconds 150
            [System.Console]::Beep(1000, 600)
        } catch {}

        # Zadrzi stanje na Fazi 4, ali omoguci serviseru nastavak ili automatski fallback
        Write-Host ""
        Write-Host "   [!] PAZNJA: Nisu instalirani svi programi ($missingStr)." -ForegroundColor Yellow
        Write-Host "   Pritisnite [ENTER] za ponovni pokusaj Faze 4, ili [S] za prinudni prelazak u Fazu 5..." -ForegroundColor Cyan
        
        $choice = ""
        $isWorker = ($Global:Worker -or $Global:WIS_UI_Backend -eq "Pipeline")
        if (-not $isWorker) {
            $waitSec = 30
            Write-Host "   Automatski nastavak u Fazu 5 za $waitSec sekundi ukoliko nema unosa..." -ForegroundColor DarkGray
            while ($waitSec -gt 0) {
                try {
                    if ([System.Console]::KeyAvailable) {
                        $key = [System.Console]::ReadKey($true)
                        if ($key.Key -eq [System.ConsoleKey]::Enter) { $choice = "R"; break }
                        if ($key.Key -eq [System.ConsoleKey]::S) { $choice = "S"; break }
                    }
                } catch {
                    # Preusmeren STDIN baca InvalidOperationException - ignorisi i nastavi
                    break
                }
                Start-Sleep -Seconds 1
                $waitSec--
            }
        }
        if ($choice -eq "R") {
            if ($Global:State) { $Global:State.Phase = 4; if (Get-Command Save-State -ErrorAction SilentlyContinue) { Save-State } }
            if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                Write-ServisLog "Serviser je zatrazio ponovnu instalaciju programa koji nedostaju ($missingStr)..." -Level "STEP" -Component "Phase4"
            }
            Install-AllServisPrograms -IsWorker:$false
            return
        }
        Write-ServisLog "Serviser ili tajmer (30s) je odobrio prelazak u Fazu 5 uprkos nedostajucim programima ($missingStr)." -Level "WARN" -Component "Phase4"
    }

    if ($Global:State) {
        $Global:State.Phase = 5
        if (Get-Command Save-State -ErrorAction SilentlyContinue) { Save-State }
    }
    if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
        Write-ServisLog "Faza 4 uspesno zavrsena. Stanje prebaceno na Fazu 5." -Level "SUCCESS" -Component "Phase4"
    }
}

function Assert-InstalledApplicationsReality {
    <#
    .SYNOPSIS
        Reality Check: Rigorozna verifikacija stvarnog fizičkog postojanja aplikacija na disku.
    .DESCRIPTION
        Proverava fizičko postojanje ključnih aplikacija na fajl sistemu:
        - Google Chrome: C:\Program Files\Google\Chrome\Application\chrome.exe ili Program Files (x86)
        - Adobe Acrobat: C:\Program Files\Adobe\Acrobat DC\Acrobat\Acrobat.exe ili C:\Program Files (x86)\Adobe\Acrobat 2020\Acrobat\Acrobat.exe
        - Microsoft Office: WINWORD.EXE ili EXCEL.EXE u C:\Program Files\Microsoft Office\root\Office16\ ili odgovarajućoj putanji
        - WinRAR: C:\Program Files\WinRAR\WinRAR.exe ili Program Files (x86)
        - BatteryBar (za prenosne računare ako je zatraženo ili navedeno)
        Vraća strukturisani hashtable:
        @{
            Success     = [bool]
            MissingApps = @([string]...)
            Details     = @([string]...)
        }
    .COMPATIBILITY
        Windows PowerShell 5.1+
    #>
    [CmdletBinding()]
    param(
        [string[]]$RequiredApps = @("Chrome", "Acrobat", "Office", "WinRAR"),
        [switch]$CheckBatteryBarIfLaptop
    )

    $missingApps = [System.Collections.Generic.List[string]]::new()
    $details = [System.Collections.Generic.List[string]]::new()

    # 1. Google Chrome
    if ($RequiredApps -contains "Chrome" -or $RequiredApps -contains "Google Chrome") {
        $chromePaths = @(
            "C:\Program Files\Google\Chrome\Application\chrome.exe",
            "C:\Program Files (x86)\Google\Chrome\Application\chrome.exe"
        )
        $chromeFound = $chromePaths | Where-Object { Test-Path $_ } | Select-Object -First 1
        if ($chromeFound) {
            $details.Add("Google Chrome: Pronađen ($chromeFound)")
        } else {
            $missingApps.Add("Google Chrome")
            $details.Add("Google Chrome: NEDOSTAJE (chrome.exe nije pronađen u Program Files)")
        }
    }

    # 2. Adobe Acrobat
    if ($RequiredApps -contains "Acrobat" -or $RequiredApps -contains "Adobe Acrobat") {
        $acrobatPaths = @(
            "C:\Program Files\Adobe\Acrobat DC\Acrobat\Acrobat.exe",
            "C:\Program Files (x86)\Adobe\Acrobat DC\Acrobat\Acrobat.exe",
            "C:\Program Files\Adobe\Acrobat\Acrobat.exe",
            "C:\Program Files (x86)\Adobe\Acrobat\Acrobat.exe",
            "C:\Program Files\Adobe\Acrobat Pro\Acrobat.exe",
            "C:\Program Files (x86)\Adobe\Acrobat Pro\Acrobat.exe",
            "C:\Program Files\Adobe\Acrobat Pro DC\Acrobat\Acrobat.exe",
            "C:\Program Files (x86)\Adobe\Acrobat Pro DC\Acrobat\Acrobat.exe",
            "C:\Program Files\Adobe\Acrobat 2024\Acrobat\Acrobat.exe",
            "C:\Program Files (x86)\Adobe\Acrobat 2024\Acrobat\Acrobat.exe",
            "C:\Program Files (x86)\Adobe\Acrobat 2020\Acrobat\Acrobat.exe",
            "C:\Program Files\Adobe\Acrobat Reader DC\Reader\AcroRd32.exe",
            "C:\Program Files (x86)\Adobe\Acrobat Reader DC\Reader\AcroRd32.exe"
        )
        $acrobatFound = $acrobatPaths | Where-Object { Test-Path $_ } | Select-Object -First 1
        if ($acrobatFound) {
            $details.Add("Adobe Acrobat: Pronađen ($acrobatFound)")
        } else {
            $missingApps.Add("Adobe Acrobat")
            $details.Add("Adobe Acrobat: NEDOSTAJE (Acrobat.exe nije pronađen u Program Files)")
        }
    }

    # 3. Microsoft Office
    if ($RequiredApps -contains "Office" -or $RequiredApps -contains "Microsoft Office") {
        $officePaths = @(
            "C:\Program Files\Microsoft Office\root\Office16\WINWORD.EXE",
            "C:\Program Files (x86)\Microsoft Office\root\Office16\WINWORD.EXE",
            "C:\Program Files\Microsoft Office\Office16\WINWORD.EXE",
            "C:\Program Files (x86)\Microsoft Office\Office16\WINWORD.EXE",
            "C:\Program Files\Microsoft Office\root\Office16\EXCEL.EXE",
            "C:\Program Files (x86)\Microsoft Office\root\Office16\EXCEL.EXE",
            "C:\Program Files\Microsoft Office\Office16\EXCEL.EXE",
            "C:\Program Files (x86)\Microsoft Office\Office16\EXCEL.EXE"
        )
        $officeFound = $officePaths | Where-Object { Test-Path $_ } | Select-Object -First 1
        if ($officeFound) {
            $details.Add("Microsoft Office: Pronađen ($officeFound)")
        } else {
            $missingApps.Add("Microsoft Office")
            $details.Add("Microsoft Office: NEDOSTAJE (WINWORD.EXE ili EXCEL.EXE nisu pronađeni)")
        }
    }

    # 4. WinRAR
    if ($RequiredApps -contains "WinRAR") {
        $winrarPaths = @(
            "C:\Program Files\WinRAR\WinRAR.exe",
            "C:\Program Files (x86)\WinRAR\WinRAR.exe"
        )
        $winrarFound = $winrarPaths | Where-Object { Test-Path $_ } | Select-Object -First 1
        if ($winrarFound) {
            $details.Add("WinRAR: Pronađen ($winrarFound)")
        } else {
            $missingApps.Add("WinRAR")
            $details.Add("WinRAR: NEDOSTAJE (WinRAR.exe nije pronađen)")
        }
    }

    # 5. BatteryBar
    if ($CheckBatteryBarIfLaptop -or $RequiredApps -contains "BatteryBar") {
        $isLap = Test-BatteryPresent
        if ($isLap) {
            $bbPaths = @(
                "C:\Program Files\BatteryBar\BatteryBar.exe",
                "C:\Program Files (x86)\BatteryBar\BatteryBar.exe"
            )
            $bbFound = $bbPaths | Where-Object { Test-Path $_ } | Select-Object -First 1
            if ($bbFound) {
                $details.Add("BatteryBar: Pronađen ($bbFound)")
            } else {
                $missingApps.Add("BatteryBar")
                $details.Add("BatteryBar: NEDOSTAJE (BatteryBar.exe nije pronađen na laptopu)")
            }
        }
    }

    $isSuccess = ($missingApps.Count -eq 0)

    return @{
        Success     = $isSuccess
        MissingApps = @($missingApps)
        Details     = @($details)
    }
}

function Invoke-Phase4Apps {
    [CmdletBinding()]
    param(
        [string]$LocalSetupDir = "C:\setup",
        [string]$ProgramiDir = "C:\setup\Programi",
        [string]$OnlyApp = "",
        [switch]$PreserveCurrentPhase
    )

    $env:SEE_MASK_NOZONECHECKS = '1'
    try { [Environment]::SetEnvironmentVariable("SEE_MASK_NOZONECHECKS", "1", "Process") } catch {}

    $isSingleAppRetry = -not [string]::IsNullOrWhiteSpace($OnlyApp)
    $runDotNet = $true
    $runWinRAR = $true
    $runChrome = $true
    $runAcrobat = $true
    $runOffice = $true
    $runBatteryBar = $true
    $runShortcuts = $true
    $runPdfDefault = $true
    $runLicense = $true
    $retryCanonicalAppId = ""
    $retryPresenceId = ""
    $retryStepId = ""

    if ($isSingleAppRetry) {
        $runDotNet = $false
        $runWinRAR = $false
        $runChrome = $false
        $runAcrobat = $false
        $runOffice = $false
        $runBatteryBar = $false
        $runShortcuts = $false
        $runPdfDefault = $false
        $runLicense = $false

        $onlyKey = $OnlyApp.Trim().ToLowerInvariant()
        switch ($onlyKey) {
            { $_ -in @("dotnet", "runtimes") } {
                $runDotNet = $true
                $retryCanonicalAppId = "DotNet"
                $retryPresenceId = "DotNet"
                $retryStepId = "P4.Runtimes"
            }
            "winrar" {
                $runWinRAR = $true
                $retryCanonicalAppId = "WinRAR"
                $retryPresenceId = "WinRAR"
                $retryStepId = "P4.WinRAR"
            }
            { $_ -in @("chrome", "google chrome") } {
                $runChrome = $true
                $retryCanonicalAppId = "Chrome"
                $retryPresenceId = "Chrome"
                $retryStepId = "P4.Chrome"
            }
            { $_ -in @("acrobat", "adobe acrobat", "adobe acrobat pro") } {
                $runAcrobat = $true
                $runPdfDefault = $true
                $retryCanonicalAppId = "Acrobat"
                $retryPresenceId = "Acrobat"
                $retryStepId = "P4.Acrobat"
            }
            { $_ -in @("office", "microsoft office", "ms office") } {
                $runOffice = $true
                $retryCanonicalAppId = "Office"
                $retryPresenceId = "Office"
                $retryStepId = "P4.Office"
            }
            "batterybar" {
                $runBatteryBar = $true
                $retryCanonicalAppId = "BatteryBar"
                $retryPresenceId = "BatteryBar"
                $retryStepId = "P4.BatteryBar"
            }
            "shortcuts" {
                $runShortcuts = $true
                $retryCanonicalAppId = "Shortcuts"
                $retryPresenceId = "Shortcuts"
                $retryStepId = "P4.Shortcuts"
            }
            { $_ -in @("license", "activation") } {
                $runLicense = $true
                $retryCanonicalAppId = "License"
                $retryPresenceId = "Activation"
                $retryStepId = "P4.Activation"
            }
            default {
                throw "Nepoznat identifikator aplikacije za pojedinačni Retry: $OnlyApp"
            }
        }
    }

    $preservePhase = [bool]($PreserveCurrentPhase -or $isSingleAppRetry)
    $prevPreserveFlag = $Global:WisPreserveCurrentPhase
    $Global:WisPreserveCurrentPhase = $preservePhase
    $ipcLogPhase = if ($preservePhase) { 0 } else { 4 }

    try {
    if (-not $preservePhase) {
        $Global:CurrentPhase = 4
        if (-not $Global:State) { $Global:State = @{} }
        $Global:State.Phase = 4
    }

    if ($isSingleAppRetry) {
        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
            Write-ServisLog "=== POJEDINAČNI RETRY PROGRAMA: $retryCanonicalAppId ===" -Level "STEP" -Component "Phase4"
        }
        if (Get-Command Send-IpcLog -ErrorAction SilentlyContinue) {
            Send-IpcLog -Message "Pokrećem pojedinačni Retry za: $retryCanonicalAppId..." -Level "STEP" -Tag "[Phase4]" -SkipFileLog
        }
    } else {
        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
            Write-ServisLog "=== POKRETANJE FAZE 4: Korisnicki Programi i Aktivacija ===" -Level "STEP" -Component "Phase4"
        }
        if (-not $preservePhase -and (Get-Command Send-IpcProgress -ErrorAction SilentlyContinue)) {
            Send-IpcProgress -Progress 5 -Phase 4 -PhaseName "Programi" -SubPhase "Inicijalizacija" -StatusMessage "Priprema skladišta programa..."
        }

        # Nakon restarta u Fazi 3, ponovo osiguraj da je pozadinski Windows Update radnik aktivan tokom Faza 4 i 5
        try {
            if (-not (Get-Command Start-BackgroundWindowsUpdateWorker -ErrorAction SilentlyContinue)) {
                $updMod = @(
                    (Join-Path $PSScriptRoot "WIS.Update.psm1"),
                    (Join-Path $PSScriptRoot "..\Modules\WIS.Update.psm1"),
                    (Join-Path $LocalSetupDir "Skripte\Modules\WIS.Update.psm1"),
                    "C:\setup\Skripte\Modules\WIS.Update.psm1"
                ) | Where-Object { Test-Path $_ } | Select-Object -First 1
                if ($updMod) { Import-Module $updMod -Force -DisableNameChecking -ErrorAction SilentlyContinue }
            }
            if (Get-Command Start-BackgroundWindowsUpdateWorker -ErrorAction SilentlyContinue) {
                Start-BackgroundWindowsUpdateWorker | Out-Null
            }
        } catch {}
    }

    if (Get-Command Wait-ServisUICooperativePause -ErrorAction SilentlyContinue) {
        Wait-ServisUICooperativePause -CheckpointName "Faza 4 - Pocetak"
    }

    # Uvoz StepGuard modula ukoliko funkcija Invoke-WisStep nije registrovana u sesiji
    if (-not (Get-Command Invoke-WisStep -ErrorAction SilentlyContinue)) {
        $stepGuardMod = @(
            (Join-Path $PSScriptRoot "WIS.StepGuard.psm1"),
            (Join-Path $PSScriptRoot "..\Modules\WIS.StepGuard.psm1"),
            (Join-Path $LocalSetupDir "Skripte\Modules\WIS.StepGuard.psm1"),
            "C:\setup\Skripte\Modules\WIS.StepGuard.psm1"
        ) | Where-Object { Test-Path $_ } | Select-Object -First 1
        if ($stepGuardMod) { Import-Module $stepGuardMod -Force -DisableNameChecking -ErrorAction SilentlyContinue }
    }

    # Emitovanje liste svih koraka u stanju WAIT da bi UI odmah iscrtao listu koraka
    $phase4StepCatalog = @(
        @{ Id = "P4.Runtimes";   Title = "Sistemski runtime paketi (VC++ / .NET)" },
        @{ Id = "P4.WinRAR";     Title = "WinRAR arhiver" },
        @{ Id = "P4.Chrome";     Title = "Google Chrome pregledač" },
        @{ Id = "P4.Acrobat";    Title = "Adobe Acrobat Pro" },
        @{ Id = "P4.Office";     Title = "Microsoft Office 2024" },
        @{ Id = "P4.BatteryBar"; Title = "BatteryBar indikator baterije" },
        @{ Id = "P4.Shortcuts";  Title = "Radne prečice na radnoj površini" },
        @{ Id = "P4.PdfDefault"; Title = "Podrazumevani PDF pregledač" },
        @{ Id = "P4.Activation"; Title = "Aktivacija Windows i Office paketa" }
    )
    if ($isSingleAppRetry) {
        if (-not $PreserveCurrentPhase) {
            foreach ($sc in $phase4StepCatalog) {
                if ($sc.Id -eq $retryStepId -and (Get-Command Send-IpcStep -ErrorAction SilentlyContinue)) {
                    Send-IpcStep -Phase 4 -StepId $sc.Id -Title $sc.Title -State "WAIT" -Reason ""
                }
            }
        }
    } else {
        foreach ($sc in $phase4StepCatalog) {
            if (Get-Command Send-IpcStep -ErrorAction SilentlyContinue) {
                Send-IpcStep -Phase 4 -StepId $sc.Id -Title $sc.Title -State "WAIT" -Reason ""
            }
        }
    }

    # Pronalazenje foldera sa programima (Prioritet 1: Lokalni C:\setup\Programi, Prioritet 2: SMB 3.1.1 TrueNAS)
    $detectedProgDir = Get-ServisProgramiRoot -LocalSetupDir $LocalSetupDir -PreferredDir $ProgramiDir
    $localCandidates = @(
        $detectedProgDir,
        $ProgramiDir,
        "C:\setup\Programi",
        "C:\setup\Programi\Programi",
        (Join-Path $LocalSetupDir "Programi")
    )
    foreach ($d in (Get-PSDrive -PSProvider FileSystem -ErrorAction SilentlyContinue)) {
        $cand = Join-Path "$($d.Root)" "Programi"
        if ($localCandidates -notcontains $cand) { $localCandidates += $cand }
    }
    $activeProgDir = $localCandidates | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
    if (-not $activeProgDir) {
        $activeProgDir = if ($detectedProgDir) { $detectedProgDir } else { "C:\setup\Programi" }
    }

    if (Get-Command Ensure-ProgramiAvailable -ErrorAction SilentlyContinue) {
        $ensuredDir = Ensure-ProgramiAvailable -Interactive:$false -OnlyApp $OnlyApp
        if ($ensuredDir -and (Test-Path $ensuredDir)) {
            $activeProgDir = $ensuredDir
        }
    }

    if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
        Write-ServisLog "Aktivni direktorijum sa programima: $activeProgDir" -Level "INFO" -Component "Apps"
    }

    # IMP-37: Rana fail-fast provera ključnih paketa (Acrobat, Office) pre kaskadnog pada pojedinačnih P4.* koraka
    $needAcrobatRepo = $runAcrobat -and (-not (Get-WisAppPresence -AppId "Acrobat").Installed)
    $needOfficeRepo  = $runOffice -and (-not (Get-WisAppPresence -AppId "Office").Installed)
    if ($needAcrobatRepo -or $needOfficeRepo) {
        $checkRepoDirs = @($activeProgDir, "C:\setup\Programi", (Join-Path $LocalSetupDir "Programi")) | Where-Object { $_ } | Select-Object -Unique
        $hasAcrobatPkg = $false
        $hasOfficePkg  = $false
        foreach ($crd in $checkRepoDirs) {
            if (Test-Path $crd -ErrorAction SilentlyContinue) {
                if ((Test-Path (Join-Path $crd "Adobe.Acrobat.Pro.2024.x64.Multilingual\Adobe Acrobat Pro\AcroPro.msi")) -or `
                    (Test-Path (Join-Path $crd "Acrobat Pro 2024\Adobe Acrobat Pro\AcroPro.msi")) -or `
                    (Test-Path (Join-Path $crd "Programi\Adobe.Acrobat.Pro.2024.x64.Multilingual\Adobe Acrobat Pro\AcroPro.msi"))) {
                    $hasAcrobatPkg = $true
                }
                if ((Test-Path (Join-Path $crd "Office 2024\files\setup.exe")) -or `
                    (Test-Path (Join-Path $crd "Office 2024\setup.exe")) -or `
                    (Test-Path (Join-Path $crd "Programi\Office 2024\files\setup.exe")) -or `
                    (Test-Path (Join-Path $crd "Programi\Office 2024\setup.exe"))) {
                    $hasOfficePkg = $true
                }
            }
        }
        if ((($needAcrobatRepo -and -not $hasAcrobatPkg) -or ($needOfficeRepo -and -not $hasOfficePkg)) -and (Get-Command Sync-ProgramiFromNas -ErrorAction SilentlyContinue)) {
            if ($isSingleAppRetry) {
                $null = Sync-ProgramiFromNas -DestinationDir "C:\setup\Programi" -OnlyApp $retryCanonicalAppId -Force
            } else {
                $null = Sync-ProgramiFromNas -DestinationDir "C:\setup\Programi" -Force
            }
            foreach ($crd in (@("C:\setup\Programi") + $checkRepoDirs | Select-Object -Unique)) {
                if (Test-Path $crd -ErrorAction SilentlyContinue) {
                    if ((Test-Path (Join-Path $crd "Adobe.Acrobat.Pro.2024.x64.Multilingual\Adobe Acrobat Pro\AcroPro.msi")) -or `
                        (Test-Path (Join-Path $crd "Acrobat Pro 2024\Adobe Acrobat Pro\AcroPro.msi")) -or `
                        (Test-Path (Join-Path $crd "Programi\Adobe.Acrobat.Pro.2024.x64.Multilingual\Adobe Acrobat Pro\AcroPro.msi"))) {
                        $hasAcrobatPkg = $true
                        if ($activeProgDir -ne $crd -and -not (Test-ProgramiPackageComplete -DirPath $activeProgDir)) { $activeProgDir = $crd }
                    }
                    if ((Test-Path (Join-Path $crd "Office 2024\files\setup.exe")) -or `
                        (Test-Path (Join-Path $crd "Office 2024\setup.exe")) -or `
                        (Test-Path (Join-Path $crd "Programi\Office 2024\files\setup.exe")) -or `
                        (Test-Path (Join-Path $crd "Programi\Office 2024\setup.exe"))) {
                        $hasOfficePkg = $true
                        if ($activeProgDir -ne $crd -and -not (Test-ProgramiPackageComplete -DirPath $activeProgDir)) { $activeProgDir = $crd }
                    }
                }
            }
        }
        $missingKeyRepoPkgs = [System.Collections.Generic.List[string]]::new()
        if ($needAcrobatRepo -and -not $hasAcrobatPkg) { $missingKeyRepoPkgs.Add("Acrobat") }
        if ($needOfficeRepo -and -not $hasOfficePkg)   { $missingKeyRepoPkgs.Add("Office") }
        if ($missingKeyRepoPkgs.Count -gt 0) {
            $missingKeyStr = $missingKeyRepoPkgs -join ", "
            $repoFatalMsg = "KRITIČNA GREŠKA FAZE 4: Ključni instalacioni paketi ($missingKeyStr) ne postoje ni na NAS SMB repozitorijumu ni lokalno (C:\setup\Programi / USB). Faza 4 je odmah zaustavljena."
            if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                Write-ServisLog $repoFatalMsg -Level "ERROR" -Component "NasProgrami"
            }
            if (Get-Command Send-IpcLog -ErrorAction SilentlyContinue) {
                Send-IpcLog -Message $repoFatalMsg -Level "ERROR" -Tag "[NasProgrami]" -Phase $ipcLogPhase
            }
            if ($isSingleAppRetry -and $retryCanonicalAppId -and (Get-Command Send-IpcAppUpdate -ErrorAction SilentlyContinue)) {
                Send-IpcAppUpdate -AppId $retryCanonicalAppId -Status "ERROR" -Detail "Nedostupan NAS/lokalni instalacioni paket ($missingKeyStr)"
            }
            throw $repoFatalMsg
        }
    }

    # Rekurzivna deblokada svih instalacionih fajlova ako je skladište lokalno
    if ($activeProgDir -and -not ($activeProgDir -like "\\*") -and (Test-Path $activeProgDir)) {
        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
            Write-ServisLog "Deblokiram sve instalacione fajlove u lokalnom direktorijumu: $activeProgDir" -Level "DEBUG" -Component "Apps"
        }
        Get-ChildItem -Path $activeProgDir -Include "*.exe","*.msi","*.cmd","*.bat","*.xml","*.ps1" -Recurse -File -ErrorAction SilentlyContinue | ForEach-Object {
            Unblock-File -Path $_.FullName -ErrorAction SilentlyContinue
            Remove-Item -Path "$($_.FullName):Zone.Identifier" -ErrorAction SilentlyContinue
        }
    }

    # Otklanjanje reboot i MSI lockova (Best-Effort)
    try {
        Remove-PendingRebootLocks
        $null = Wait-MsiMutexAvailable -TimeoutSec 60
    } catch {}

    $appsStatusMap = [ordered]@{
        WinRAR     = "WAIT"
        BatteryBar = "WAIT"
        Office     = "WAIT"
        Chrome     = "WAIT"
        Acrobat    = "WAIT"
        Shortcuts  = "WAIT"
        License    = "WAIT"
    }

    $isLaptop = Test-BatteryPresent

    $stepRuntimes = $null
    $stepWinrar = $null
    $stepChrome = $null
    $stepAcrobat = $null
    $stepOffice = $null
    $stepBattery = $null
    $stepShortcuts = $null
    $stepPdf = $null
    $stepActivation = $null

    # =========================================================================
    # 1. SISTEMSKI RUNTIME PAKETI (VC++ / .NET) - P4.Runtimes
    # =========================================================================
    if ($runDotNet) {
        $stepRuntimes = Invoke-WisStep -Phase 4 -StepId "P4.Runtimes" -Title "Sistemski runtime paketi (VC++ / .NET)" -NoSkip:$isSingleAppRetry -Test {
            Get-WisAppPresence -AppId "DotNet"
        } -Action {
            if (-not $preservePhase -and (Get-Command Send-IpcProgress -ErrorAction SilentlyContinue)) {
                Send-IpcProgress -Progress 12 -Phase 4 -PhaseName "Programi" -SubPhase "Runtimes" -StatusMessage "Instalacija sistemskih runtime paketa (VC++ / .NET)..."
            } elseif ($isSingleAppRetry -and (Get-Command Send-IpcStatus -ErrorAction SilentlyContinue)) {
                $curP = if ($null -ne $Global:CurrentPhase -and [int]$Global:CurrentPhase -gt 0) { [int]$Global:CurrentPhase } else { 4 }
                $curPrg = if ($null -ne $Global:CurrentProgress -and [int]$Global:CurrentProgress -gt 0) { [int]$Global:CurrentProgress } else { 95 }
                Send-IpcStatus -Phase $curP -Progress $curPrg -SubPhase "Runtimes" -StatusMessage "Instalacija: DotNet..."
            }
            if (Get-Command Send-IpcAppUpdate -ErrorAction SilentlyContinue) {
                Send-IpcAppUpdate -AppId "DotNet" -Status "RUNNING" -Detail "Instalacija sistemskih runtime paketa (.NET / VC++)..."
            }
            $null = Install-ServisVCRedist -ProgramiRoot $activeProgDir
            $null = Install-ServisDotNet -ProgramiRoot $activeProgDir
        }

        $appsStatusMap["DotNet"] = if ($stepRuntimes.State -in @("OK", "SKIPPED_DONE")) { "OK" } else { "WARN" }
        if (Get-Command Send-IpcAppUpdate -ErrorAction SilentlyContinue) {
            $dnDetail = if ($stepRuntimes.State -eq "SKIPPED_DONE") { "Već instalirano" } elseif ($stepRuntimes.State -eq "OK") { "Instaliran .NET Runtime" } else { "Neuspešno - Nastavak" }
            Send-IpcAppUpdate -AppId "DotNet" -Status $appsStatusMap["DotNet"] -Detail $dnDetail
        }
    }

    # =========================================================================
    # 2. WINRAR - P4.WinRAR
    # =========================================================================
    if ($runWinRAR) {
    $stepWinrar = Invoke-WisStep -Phase 4 -StepId "P4.WinRAR" -Title "WinRAR arhiver" -NoSkip:$isSingleAppRetry -Test {
        Get-WisAppPresence -AppId "WinRAR"
    } -Action {
        if (Get-Command Wait-ServisUICooperativePause -ErrorAction SilentlyContinue) {
            Wait-ServisUICooperativePause -CheckpointName "Faza 4 - WinRAR"
        }
        if (-not $preservePhase -and (Get-Command Send-IpcProgress -ErrorAction SilentlyContinue)) {
            Send-IpcProgress -Progress 20 -Phase 4 -PhaseName "Programi" -SubPhase "WinRAR" -StatusMessage "Instalacija: WinRAR..."
        } elseif ($isSingleAppRetry -and (Get-Command Send-IpcStatus -ErrorAction SilentlyContinue)) {
            $curP = if ($null -ne $Global:CurrentPhase -and [int]$Global:CurrentPhase -gt 0) { [int]$Global:CurrentPhase } else { 4 }
            $curPrg = if ($null -ne $Global:CurrentProgress -and [int]$Global:CurrentProgress -gt 0) { [int]$Global:CurrentProgress } else { 95 }
            Send-IpcStatus -Phase $curP -Progress $curPrg -SubPhase "WinRAR" -StatusMessage "Instalacija: WinRAR..."
        }
        if (Get-Command Send-IpcAppUpdate -ErrorAction SilentlyContinue) {
            Send-IpcAppUpdate -AppId "WinRAR" -Status "RUNNING" -Detail "Instalacija u toku..."
        }

        if ((Get-Command Test-ServisSkipRequested -ErrorAction SilentlyContinue) -and (Test-ServisSkipRequested)) {
            if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                Write-ServisLog "Instalacija WinRAR preskočena na zahtev servisera." -Level "WARN" -Component "WinRAR"
            }
            return
        }

        $rarCandidates = @(Get-ChildItem -Path $activeProgDir -Filter "*winrar*.exe" -Recurse -File -ErrorAction SilentlyContinue)
        if ($rarCandidates.Count -eq 0 -and (Get-Command Sync-ProgramiFromNas -ErrorAction SilentlyContinue)) {
            if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                Write-ServisLog "Lokalni WinRAR instaler nije pronadjen u '$activeProgDir'. Pokrecem Sync-ProgramiFromNas retry..." -Level "WARN" -Component "WinRAR"
            }
            $null = Sync-ProgramiFromNas -DestinationDir "C:\setup\Programi" -OnlyApp "WinRAR" -Force
            if ($isSingleAppRetry -and (Get-Command Send-IpcStatus -ErrorAction SilentlyContinue)) {
                $curP = if ($null -ne $Global:CurrentPhase -and [int]$Global:CurrentPhase -gt 0) { [int]$Global:CurrentPhase } else { 4 }
                $curPrg = if ($null -ne $Global:CurrentProgress -and [int]$Global:CurrentProgress -gt 0) { [int]$Global:CurrentProgress } else { 95 }
                Send-IpcStatus -Phase $curP -Progress $curPrg -SubPhase "WinRAR" -StatusMessage "Instalacija: WinRAR..."
            }
            if (Test-Path "C:\setup\Programi") {
                $activeProgDir = "C:\setup\Programi"
            }
            $rarCandidates = @(Get-ChildItem -Path $activeProgDir -Filter "*winrar*.exe" -Recurse -File -ErrorAction SilentlyContinue)
        }
        $wrInstalledNow = $false
        $rarExe = $null

        if ($rarCandidates.Count -gt 0) {
            $rarExe = $rarCandidates[0].FullName
            Unblock-File -Path $rarExe -ErrorAction SilentlyContinue
            Remove-Item -Path "$($rarExe):Zone.Identifier" -ErrorAction SilentlyContinue
            if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                Write-ServisLog "Instaliram WinRAR iz: $rarExe" -Level "STEP" -Component "WinRAR"
            }
            $resWinrar = Invoke-InstallerWithLocalFallback -InstallerPath $rarExe -ArgumentList "/s" -AppName "WinRAR" -WaitSeconds 60
            Start-Sleep -Milliseconds 500
            $wrPresence = Get-WisAppPresence -AppId "WinRAR"
            $wrInstalledNow = [bool]$wrPresence.Installed

            if (-not $wrInstalledNow) {
                $wrExit = if ($resWinrar -and ($resWinrar -is [hashtable] -or $resWinrar -is [System.Collections.IDictionary]) -and $resWinrar.ContainsKey('ExitCode')) { $resWinrar.ExitCode } else { -1 }
                $warnMsg1 = "WinRAR nije pronadjen posle prve instalacije ($rarExe, ExitCode=$wrExit). Pokrecem retry (/S)..."
                if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                    Write-ServisLog $warnMsg1 -Level "WARN" -Component "WinRAR"
                }
                if (Get-Command Send-IpcLog -ErrorAction SilentlyContinue) {
                    Send-IpcLog -Message $warnMsg1 -Level "WARN" -Tag "[WinRAR]" -Phase $ipcLogPhase
                }
                Start-Sleep -Seconds 2
                $retryExit = -1
                try {
                    $retryProc = Start-Process -FilePath $rarExe -ArgumentList "/S" -Wait -PassThru -WindowStyle Hidden
                    $retryExit = if ($retryProc) { $retryProc.ExitCode } else { -1 }
                } catch {
                    $retryErrMsg = "WinRAR retry (/S) izuzetak za ${rarExe}: $($_.Exception.Message)"
                    if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                        Write-ServisLog $retryErrMsg -Level "WARN" -Component "WinRAR"
                    }
                    if (Get-Command Send-IpcLog -ErrorAction SilentlyContinue) {
                        Send-IpcLog -Message $retryErrMsg -Level "WARN" -Tag "[WinRAR]" -Phase $ipcLogPhase
                    }
                }
                Start-Sleep -Milliseconds 500
                $wrPresence = Get-WisAppPresence -AppId "WinRAR"
                $wrInstalledNow = [bool]$wrPresence.Installed
                if (-not $wrInstalledNow) {
                    $warnMsg2 = "WinRAR retry (/S) nije instalirao aplikaciju ($rarExe, ExitCode=$retryExit). Pokrecem Winget fallback..."
                    if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                        Write-ServisLog $warnMsg2 -Level "WARN" -Component "WinRAR"
                    }
                    if (Get-Command Send-IpcLog -ErrorAction SilentlyContinue) {
                        Send-IpcLog -Message $warnMsg2 -Level "WARN" -Tag "[WinRAR]" -Phase $ipcLogPhase
                    }
                }
            }
        } else {
            $missMsg = "WinRAR lokalni instaler (*winrar*.exe) nije pronadjen u '$activeProgDir'. Pokrecem Winget fallback..."
            if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                Write-ServisLog $missMsg -Level "WARN" -Component "WinRAR"
            }
            if (Get-Command Send-IpcLog -ErrorAction SilentlyContinue) {
                Send-IpcLog -Message $missMsg -Level "WARN" -Tag "[WinRAR]" -Phase $ipcLogPhase
            }
        }

        if (-not $wrInstalledNow) {
            $wingetRes = $false
            try {
                if (Get-Command Invoke-WingetAppInstall -ErrorAction SilentlyContinue) {
                    $wingetRes = [bool](Invoke-WingetAppInstall -PackageId "RARLab.WinRAR")
                } else {
                    $wgProc = Start-Process -FilePath "winget.exe" -ArgumentList "install --id RARLab.WinRAR --exact --silent --accept-package-agreements --accept-source-agreements" -Wait -PassThru -WindowStyle Hidden -ErrorAction SilentlyContinue
                    $wingetRes = ($wgProc -and $wgProc.ExitCode -eq 0)
                }
            } catch {
                $wingetRes = $false
            }
            Start-Sleep -Milliseconds 500
            $wrAfterWg = Get-WisAppPresence -AppId "WinRAR"
            if (-not $wrAfterWg.Installed) {
                $wgWarn = "WinRAR nije pronadjen na disku ni nakon Winget fallback-a (wingetOk=$wingetRes, activeProgDir='$activeProgDir')."
                if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                    Write-ServisLog $wgWarn -Level "WARN" -Component "WinRAR"
                }
                if (Get-Command Send-IpcLog -ErrorAction SilentlyContinue) {
                    Send-IpcLog -Message $wgWarn -Level "WARN" -Tag "[WinRAR]" -Phase $ipcLogPhase
                }
            }
        }
        try { Remove-WinRARDesktopShortcut } catch {}
    }

    $appsStatusMap["WinRAR"] = if ($stepWinrar.State -in @("OK", "SKIPPED_DONE")) { "OK" } else { "WARN" }
    if (Get-Command Send-IpcAppUpdate -ErrorAction SilentlyContinue) {
        $wrDetail = if ($stepWinrar.State -eq "SKIPPED_DONE") { "Već instalirano" } elseif ($stepWinrar.State -eq "OK") { "Instaliran" } else { "Neuspešno - Nastavak" }
        Send-IpcAppUpdate -AppId "WinRAR" -Status $appsStatusMap["WinRAR"] -Detail $wrDetail
    }
    }

    # =========================================================================
    # 3. GOOGLE CHROME - P4.Chrome
    # =========================================================================
    if ($runChrome) {
    $stepChrome = Invoke-WisStep -Phase 4 -StepId "P4.Chrome" -Title "Google Chrome pregledač" -NoSkip:$isSingleAppRetry -Test {
        Get-WisAppPresence -AppId "Chrome"
    } -Action {
        if (Get-Command Wait-ServisUICooperativePause -ErrorAction SilentlyContinue) {
            Wait-ServisUICooperativePause -CheckpointName "Faza 4 - Google Chrome"
        }
        if (-not $preservePhase -and (Get-Command Send-IpcProgress -ErrorAction SilentlyContinue)) {
            Send-IpcProgress -Progress 35 -Phase 4 -PhaseName "Programi" -SubPhase "Google Chrome" -StatusMessage "Instalacija: Google Chrome..."
        } elseif ($isSingleAppRetry -and (Get-Command Send-IpcStatus -ErrorAction SilentlyContinue)) {
            $curP = if ($null -ne $Global:CurrentPhase -and [int]$Global:CurrentPhase -gt 0) { [int]$Global:CurrentPhase } else { 4 }
            $curPrg = if ($null -ne $Global:CurrentProgress -and [int]$Global:CurrentProgress -gt 0) { [int]$Global:CurrentProgress } else { 95 }
            Send-IpcStatus -Phase $curP -Progress $curPrg -SubPhase "Google Chrome" -StatusMessage "Instalacija: Google Chrome..."
        }
        if (Get-Command Send-IpcAppUpdate -ErrorAction SilentlyContinue) {
            Send-IpcAppUpdate -AppId "Chrome" -Status "RUNNING" -Detail "Instalacija Chrome pretraživača..."
        }

        if ((Get-Command Test-ServisSkipRequested -ErrorAction SilentlyContinue) -and (Test-ServisSkipRequested)) {
            if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                Write-ServisLog "Instalacija Google Chrome preskočena na zahtev servisera." -Level "WARN" -Component "Chrome"
            }
            return
        }

        # 1. Prioritetna pretraga za GoogleChromeStandaloneEnterprise64.msi (*chrome*.msi)
        $chromeMsiCandidates = @(
            (Join-Path $activeProgDir "GoogleChromeStandaloneEnterprise64.msi"),
            (Join-Path $activeProgDir "Programi\GoogleChromeStandaloneEnterprise64.msi"),
            "C:\setup\Programi\GoogleChromeStandaloneEnterprise64.msi",
            "C:\setup\Programi\Programi\GoogleChromeStandaloneEnterprise64.msi"
        )
        if ($Global:SourceRoot) {
            $chromeMsiCandidates += (Join-Path $Global:SourceRoot "Programi\GoogleChromeStandaloneEnterprise64.msi")
            $chromeMsiCandidates += (Join-Path $Global:SourceRoot "GoogleChromeStandaloneEnterprise64.msi")
        }
        $msiPath = $chromeMsiCandidates | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
        if (-not $msiPath -and (Test-Path $activeProgDir)) {
            $foundMsi = Get-ChildItem -Path $activeProgDir -Filter "*chrome*.msi" -Recurse -File -ErrorAction SilentlyContinue | Select-Object -First 1
            if ($foundMsi) { $msiPath = $foundMsi.FullName }
        }
        if (-not $msiPath -and (Get-Command Sync-ProgramiFromNas -ErrorAction SilentlyContinue)) {
            $hasChromeExeLoc = @(Get-ChildItem -Path $activeProgDir -Filter "*chrome*.exe" -Recurse -File -ErrorAction SilentlyContinue | Where-Object { $_.Name -match "(?i)chrome.*(standalone|setup|install)" -or $_.Name -eq "ChromeStandaloneSetup64.exe" }).Count -gt 0
            if (-not $hasChromeExeLoc) {
                if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                    Write-ServisLog "Lokalni Google Chrome paket nije pronadjen u '$activeProgDir'. Pokrecem Sync-ProgramiFromNas retry..." -Level "WARN" -Component "Chrome"
                }
                $null = Sync-ProgramiFromNas -DestinationDir "C:\setup\Programi" -OnlyApp "Chrome" -Force
                if ($isSingleAppRetry -and (Get-Command Send-IpcStatus -ErrorAction SilentlyContinue)) {
                    $curP = if ($null -ne $Global:CurrentPhase -and [int]$Global:CurrentPhase -gt 0) { [int]$Global:CurrentPhase } else { 4 }
                    $curPrg = if ($null -ne $Global:CurrentProgress -and [int]$Global:CurrentProgress -gt 0) { [int]$Global:CurrentProgress } else { 95 }
                    Send-IpcStatus -Phase $curP -Progress $curPrg -SubPhase "Google Chrome" -StatusMessage "Instalacija: Google Chrome..."
                }
                if (Test-Path "C:\setup\Programi") {
                    $activeProgDir = "C:\setup\Programi"
                }
                $msiPath = $chromeMsiCandidates | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
                if (-not $msiPath -and (Test-Path "C:\setup\Programi")) {
                    $foundMsiAfterSync = Get-ChildItem -Path "C:\setup\Programi" -Filter "*chrome*.msi" -Recurse -File -ErrorAction SilentlyContinue | Select-Object -First 1
                    if ($foundMsiAfterSync) { $msiPath = $foundMsiAfterSync.FullName }
                }
            }
        }

        $installedOk = $false
        if ($msiPath) {
            $effectiveMsiPath = $msiPath
            if ($msiPath -like "\\*") {
                $chromeCacheDir = if (Test-Path "C:\setup") { "C:\setup\Cache\Chrome" } else { Join-Path $env:TEMP "WIS\Cache\Chrome" }
                if (-not (Test-Path $chromeCacheDir)) {
                    New-Item -Path $chromeCacheDir -ItemType Directory -Force -ErrorAction SilentlyContinue | Out-Null
                }
                $localMsi = Join-Path $chromeCacheDir (Split-Path $msiPath -Leaf)
                try {
                    Copy-Item -Path $msiPath -Destination $localMsi -Force -ErrorAction Stop
                    if (Test-Path $localMsi) { $effectiveMsiPath = $localMsi }
                } catch {}
            }
            Unblock-File -Path $effectiveMsiPath -ErrorAction SilentlyContinue
            Remove-Item -Path "$($effectiveMsiPath):Zone.Identifier" -ErrorAction SilentlyContinue
            if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                Write-ServisLog "Instaliram Chrome preko Enterprise MSI: $effectiveMsiPath" -Level "STEP" -Component "Chrome"
            }
            $null = Wait-MsiMutexAvailable -TimeoutSec 180
            $chDir = Split-Path $effectiveMsiPath -Parent
            if ($chDir -like "\\*") { $chDir = "C:\setup" }

            $msiRes = Invoke-SafeMsi -Arguments "/i `"$effectiveMsiPath`" /qn /norestart ALLUSERS=1 /l*v `"C:\setup\chrome_msi.log`"" -WorkingDirectory $chDir -MaxWaitSec 300
            $mCode = if ($msiRes) { $msiRes.ExitCode } else { -1 }
            $chromeInstalledAfterMsi = (Test-Path "C:\Program Files\Google\Chrome\Application\chrome.exe") -or (Test-Path "C:\Program Files (x86)\Google\Chrome\Application\chrome.exe")
            if ($mCode -eq 0 -or $mCode -eq 3010 -or $chromeInstalledAfterMsi) {
                $installedOk = $true
            }
        }

        # 2. Fallback na ChromeSetup.exe ili Winget ukoliko MSI nije instalirao
        if (-not $installedOk) {
            $chromeCandidates = Get-ChildItem -Path $activeProgDir -Filter "*chrome*.exe" -Recurse -File -ErrorAction SilentlyContinue | Where-Object { $_.Name -match "(?i)chrome.*(standalone|setup|install)" -or $_.Name -eq "ChromeStandaloneSetup64.exe" }
            if ($chromeCandidates -and $chromeCandidates.Count -gt 0) {
                $cExe = $chromeCandidates[0].FullName
                Unblock-File -Path $cExe -ErrorAction SilentlyContinue
                Remove-Item -Path "$($cExe):Zone.Identifier" -ErrorAction SilentlyContinue
                if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                    Write-ServisLog "Instaliram Chrome iz: $cExe" -Level "STEP" -Component "Chrome"
                }
                $resChrome = Invoke-InstallerWithLocalFallback -InstallerPath $cExe -ArgumentList "/silent /install" -AppName "Chrome" -WaitSeconds 120
                if ($resChrome.Success) { $installedOk = $true }
            } else {
                try {
                    $null = Start-Process -FilePath "winget.exe" -ArgumentList "install --id Google.Chrome --silent --accept-package-agreements --accept-source-agreements" -Wait -WindowStyle Hidden -ErrorAction SilentlyContinue
                } catch {}
            }
        }
    }

    $appsStatusMap["Chrome"] = if ($stepChrome.State -in @("OK", "SKIPPED_DONE")) { "OK" } else { "WARN" }
    if (Get-Command Send-IpcAppUpdate -ErrorAction SilentlyContinue) {
        $chDetail = if ($stepChrome.State -eq "SKIPPED_DONE") { "Već instalirano" } elseif ($stepChrome.State -eq "OK") { "Instaliran" } else { "Neuspešno - Nastavak" }
        Send-IpcAppUpdate -AppId "Chrome" -Status $appsStatusMap["Chrome"] -Detail $chDetail
    }
    }

    # =========================================================================
    # 4. ADOBE ACROBAT PRO - P4.Acrobat
    # =========================================================================
    if ($runAcrobat) {
    $stepAcrobat = Invoke-WisStep -Phase 4 -StepId "P4.Acrobat" -Title "Adobe Acrobat Pro" -NoSkip:$isSingleAppRetry -Test {
        Get-WisAppPresence -AppId "Acrobat"
    } -Action {
        if (Get-Command Wait-ServisUICooperativePause -ErrorAction SilentlyContinue) {
            Wait-ServisUICooperativePause -CheckpointName "Faza 4 - Adobe Acrobat Pro"
        }
        if (-not $preservePhase -and (Get-Command Send-IpcProgress -ErrorAction SilentlyContinue)) {
            Send-IpcProgress -Progress 50 -Phase 4 -PhaseName "Programi" -SubPhase "Adobe Acrobat Pro" -StatusMessage "Instalacija: Adobe Acrobat Pro..."
        } elseif ($isSingleAppRetry -and (Get-Command Send-IpcStatus -ErrorAction SilentlyContinue)) {
            $curP = if ($null -ne $Global:CurrentPhase -and [int]$Global:CurrentPhase -gt 0) { [int]$Global:CurrentPhase } else { 4 }
            $curPrg = if ($null -ne $Global:CurrentProgress -and [int]$Global:CurrentProgress -gt 0) { [int]$Global:CurrentProgress } else { 95 }
            Send-IpcStatus -Phase $curP -Progress $curPrg -SubPhase "Adobe Acrobat Pro" -StatusMessage "Instalacija: Adobe Acrobat Pro..."
        }
        if (Get-Command Send-IpcAppUpdate -ErrorAction SilentlyContinue) {
            Send-IpcAppUpdate -AppId "Acrobat" -Status "RUNNING" -Detail "Instalacija Adobe Acrobat Pro..."
        }

        if ((Get-Command Test-ServisSkipRequested -ErrorAction SilentlyContinue) -and (Test-ServisSkipRequested)) {
            if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                Write-ServisLog "Instalacija Adobe Acrobat preskočena na zahtev servisera." -Level "WARN" -Component "Acrobat"
            }
            return
        }

        $acroDirCandidates = @(
            (Join-Path $activeProgDir "Adobe.Acrobat.Pro.2024.x64.Multilingual\Adobe Acrobat Pro"),
            (Join-Path $activeProgDir "Acrobat Pro 2024\Adobe Acrobat Pro"),
            (Join-Path $activeProgDir "Adobe Acrobat Pro"),
            "C:\setup\Programi\Adobe.Acrobat.Pro.2024.x64.Multilingual\Adobe Acrobat Pro",
            "C:\setup\Programi\Programi\Adobe.Acrobat.Pro.2024.x64.Multilingual\Adobe Acrobat Pro",
            "C:\setup\Programi\Acrobat Pro 2024\Adobe Acrobat Pro"
        )
        if ($Global:SourceRoot) {
            $acroDirCandidates += (Join-Path $Global:SourceRoot "Programi\Adobe.Acrobat.Pro.2024.x64.Multilingual\Adobe Acrobat Pro")
            $acroDirCandidates += (Join-Path $Global:SourceRoot "Adobe.Acrobat.Pro.2024.x64.Multilingual\Adobe Acrobat Pro")
        }
        $acroDir = $acroDirCandidates | Where-Object { $_ -and (Test-Path (Join-Path $_ "AcroPro.msi")) } | Select-Object -First 1

        if (-not $acroDir -and (Test-Path $activeProgDir)) {
            $foundMsi = Get-ChildItem -Path $activeProgDir -Filter "AcroPro.msi" -Recurse -File -ErrorAction SilentlyContinue | Select-Object -First 1
            if ($foundMsi) { $acroDir = $foundMsi.DirectoryName }
        }

        if (-not $acroDir -and (Get-Command Sync-ProgramiFromNas -ErrorAction SilentlyContinue)) {
            if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                Write-ServisLog "Lokalni Adobe Acrobat Pro paket nije pronadjen. Preuzimam sa servisnog NAS-a..." -Level "WARN" -Component "Acrobat"
            }
            Sync-ProgramiFromNas -DestinationDir "C:\setup\Programi" -OnlyApp "Acrobat" -SubFolders @("Adobe.Acrobat.Pro.2024.x64.Multilingual", "Acrobat Pro 2024") -Force | Out-Null
            if ($isSingleAppRetry -and (Get-Command Send-IpcStatus -ErrorAction SilentlyContinue)) {
                $curP = if ($null -ne $Global:CurrentPhase -and [int]$Global:CurrentPhase -gt 0) { [int]$Global:CurrentPhase } else { 4 }
                $curPrg = if ($null -ne $Global:CurrentProgress -and [int]$Global:CurrentProgress -gt 0) { [int]$Global:CurrentProgress } else { 95 }
                Send-IpcStatus -Phase $curP -Progress $curPrg -SubPhase "Adobe Acrobat Pro" -StatusMessage "Instalacija: Adobe Acrobat Pro..."
            }
            $acroDir = $acroDirCandidates | Where-Object { $_ -and (Test-Path (Join-Path $_ "AcroPro.msi")) } | Select-Object -First 1
            if (-not $acroDir -and (Test-Path "C:\setup\Programi")) {
                $foundMsiAfterSync = Get-ChildItem -Path "C:\setup\Programi" -Filter "AcroPro.msi" -Recurse -File -ErrorAction SilentlyContinue | Select-Object -First 1
                if ($foundMsiAfterSync) { $acroDir = $foundMsiAfterSync.DirectoryName }
            }
        }

        if ($acroDir -and (Test-Path (Join-Path $acroDir "AcroPro.msi"))) {
            $acroMsi = Join-Path $acroDir "AcroPro.msi"
            $acroMst = Join-Path $acroDir "AcroPro.mst"
            $acroMsp = Join-Path $acroDir "AcrobatDCx64Upd2400120604.msp"
            $setupExe = Join-Path $acroDir "Setup.exe"

            foreach ($rootAcroFile in @($setupExe, $acroMsi, $acroMsp)) {
                if ($rootAcroFile -and (Test-Path -LiteralPath $rootAcroFile -PathType Leaf -ErrorAction SilentlyContinue)) {
                    Unblock-File -LiteralPath $rootAcroFile -ErrorAction SilentlyContinue
                    Remove-Item -LiteralPath "$($rootAcroFile):Zone.Identifier" -ErrorAction SilentlyContinue
                }
            }

            $null = Wait-MsiMutexAvailable -TimeoutSec 180
            $acroExeCheck = {
                (Test-Path "C:\Program Files\Adobe\Acrobat DC\Acrobat\Acrobat.exe") -or `
                (Test-Path "C:\Program Files (x86)\Adobe\Acrobat DC\Acrobat\Acrobat.exe") -or `
                (Test-Path "C:\Program Files\Adobe\Acrobat 2024\Acrobat\Acrobat.exe") -or `
                (Test-Path "C:\Program Files\Adobe\Acrobat Pro\Acrobat.exe") -or `
                (Test-Path "C:\Program Files\Adobe\Acrobat\Acrobat.exe")
            }
            $acroInstalled = $false

            if (Test-Path $setupExe) {
                $setupProc = Start-Process -FilePath $setupExe -ArgumentList "/sPB /rs" -WorkingDirectory $acroDir -PassThru -WindowStyle Hidden
                if ($setupProc) { $null = $setupProc.WaitForExit(60000) }

                $swWait = [System.Diagnostics.Stopwatch]::StartNew()
                while ($swWait.Elapsed.TotalSeconds -lt 300) {
                    Start-Sleep -Seconds 1
                    $hasAcroNow = (& $acroExeCheck)
                    $setupActive = [bool]($setupProc -and (-not $setupProc.HasExited))
                    $msiActive = [bool](Get-Process -Name "msiexec" -ErrorAction SilentlyContinue)
                    if ($hasAcroNow -and -not $msiActive -and -not $setupActive) {
                        $acroInstalled = $true
                        break
                    }
                    if (-not $msiActive -and -not $setupActive -and ($swWait.Elapsed.TotalSeconds -ge 30)) {
                        if ($hasAcroNow) { $acroInstalled = $true }
                        break
                    }
                }
                if ($setupProc -and (-not $setupProc.HasExited)) {
                    & taskkill.exe /PID $setupProc.Id /T /F >$null 2>&1
                }
            }

            if (-not $acroInstalled) {
                $msiArgs = "/i `"$acroMsi`""
                if (Test-Path $acroMst) { $msiArgs += " TRANSFORMS=`"$acroMst`"" }
                if (Test-Path $acroMsp) { $msiArgs += " PATCH=`"$acroMsp`"" }
                $msiLogPath = "C:\setup\acrobat_msi.log"
                $msiExecArgs = "$msiArgs /qn /norestart ALLUSERS=1 /l*v `"$msiLogPath`""
                $msiRes = Invoke-SafeMsi -Arguments $msiExecArgs -WorkingDirectory $acroDir -MaxWaitSec 600
                if (-not (& $acroExeCheck)) {
                    if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                        $msiErrHint = ""
                        if (Test-Path $msiLogPath) {
                            try {
                                $errHits = @(Get-Content -Path $msiLogPath -ErrorAction SilentlyContinue | Where-Object { $_ -match '(?i)return value 3|error 16|error 17|failed|fatal' } | Select-Object -Last 3)
                                if ($errHits.Count -gt 0) { $msiErrHint = " | Log: " + ($errHits -join " ; ") }
                            } catch {}
                        }
                        Write-ServisLog "Acrobat.exe nije pronadjen posle prvog MSI pokusaja (ExitCode=$($msiRes.ExitCode))$msiErrHint. Pokrecem automatski retry..." -Level "WARN" -Component "Acrobat"
                    }
                    $null = Wait-MsiMutexAvailable -TimeoutSec 120
                    $msiRetryArgs = "$msiArgs /qn /norestart ALLUSERS=1 /l*v `"C:\setup\acrobat_msi_retry.log`""
                    $null = Invoke-SafeMsi -Arguments $msiRetryArgs -WorkingDirectory $acroDir -MaxWaitSec 600
                }
            }
        }
    }

    $appsStatusMap["Acrobat"] = if ($stepAcrobat.State -in @("OK", "SKIPPED_DONE")) { "OK" } else { "WARN" }
    if (Get-Command Send-IpcAppUpdate -ErrorAction SilentlyContinue) {
        $acDetail = if ($stepAcrobat.State -eq "SKIPPED_DONE") { "Već instalirano" } elseif ($stepAcrobat.State -eq "OK") { "Instaliran" } else { "Neuspešno - Nastavak" }
        Send-IpcAppUpdate -AppId "Acrobat" -Status $appsStatusMap["Acrobat"] -Detail $acDetail
    }
    }

    # =========================================================================
    # 5. MICROSOFT OFFICE 2024 - P4.Office
    # =========================================================================
    if ($runOffice) {
    $stepOffice = Invoke-WisStep -Phase 4 -StepId "P4.Office" -Title "Microsoft Office 2024" -NoSkip:$isSingleAppRetry -Test {
        Get-WisAppPresence -AppId "Office"
    } -Action {
        if (Get-Command Wait-ServisUICooperativePause -ErrorAction SilentlyContinue) {
            Wait-ServisUICooperativePause -CheckpointName "Faza 4 - Office 2024"
        }
        if (-not $preservePhase -and (Get-Command Send-IpcProgress -ErrorAction SilentlyContinue)) {
            Send-IpcProgress -Progress 65 -Phase 4 -PhaseName "Programi" -SubPhase "Office 2024" -StatusMessage "Instalacija: Microsoft Office 2024..."
        } elseif ($isSingleAppRetry -and (Get-Command Send-IpcStatus -ErrorAction SilentlyContinue)) {
            $curP = if ($null -ne $Global:CurrentPhase -and [int]$Global:CurrentPhase -gt 0) { [int]$Global:CurrentPhase } else { 4 }
            $curPrg = if ($null -ne $Global:CurrentProgress -and [int]$Global:CurrentProgress -gt 0) { [int]$Global:CurrentProgress } else { 95 }
            Send-IpcStatus -Phase $curP -Progress $curPrg -SubPhase "Office 2024" -StatusMessage "Instalacija: Microsoft Office 2024..."
        }
        if (Get-Command Send-IpcAppUpdate -ErrorAction SilentlyContinue) {
            Send-IpcAppUpdate -AppId "Office" -Status "RUNNING" -Detail "Instalacija Office 2024 paketa..."
        }

        if ((Get-Command Test-ServisSkipRequested -ErrorAction SilentlyContinue) -and (Test-ServisSkipRequested)) {
            if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                Write-ServisLog "Instalacija Office 2024 preskočena na zahtev servisera." -Level "WARN" -Component "Office"
            }
            return
        }

        $officeCandidates = [System.Collections.Generic.List[string]]::new()
        $seedPaths = @(
            "C:\setup\Programi\Office 2024\files",
            "C:\setup\Programi\Office 2024",
            "C:\setup\Programi\Programi\Office 2024\files",
            "C:\setup\Programi\Programi\Office 2024"
        )
        if ($activeProgDir -and -not ($activeProgDir -like "\\*")) {
            $seedPaths += @(
                (Join-Path $activeProgDir "Office 2024\files"),
                (Join-Path $activeProgDir "Office 2024"),
                (Join-Path $activeProgDir "Programi\Office 2024\files"),
                (Join-Path $activeProgDir "Programi\Office 2024")
            )
        }
        foreach ($sp in $seedPaths) {
            if ($sp -and -not $officeCandidates.Contains($sp)) { $officeCandidates.Add($sp) }
        }
        $removableDrives = Get-PSDrive -PSProvider FileSystem -ErrorAction SilentlyContinue | Where-Object { $_.Root -ne 'C:\' -and $_.Root -ne 'c:\' }
        foreach ($drv in $removableDrives) {
            $dRoot = $drv.Root.TrimEnd('\')
            foreach ($rp in @(
                (Join-Path $dRoot "Programi\Office 2024\files"),
                (Join-Path $dRoot "Programi\Office 2024"),
                (Join-Path $dRoot "Office 2024\files"),
                (Join-Path $dRoot "Office 2024")
            )) {
                if ($rp -and -not $officeCandidates.Contains($rp)) { $officeCandidates.Add($rp) }
            }
        }
        if ($Global:SourceRoot) {
            foreach ($sr in @(
                (Join-Path $Global:SourceRoot "Programi\Office 2024\files"),
                (Join-Path $Global:SourceRoot "Office 2024\files"),
                (Join-Path $Global:SourceRoot "Office 2024")
            )) {
                if ($sr -and -not $officeCandidates.Contains($sr)) { $officeCandidates.Add($sr) }
            }
        }
        if ($activeProgDir -and ($activeProgDir -like "\\*")) {
            foreach ($up in @(
                (Join-Path $activeProgDir "Office 2024\files"),
                (Join-Path $activeProgDir "Office 2024"),
                (Join-Path $activeProgDir "Programi\Office 2024\files"),
                (Join-Path $activeProgDir "Programi\Office 2024")
            )) {
                if ($up -and -not $officeCandidates.Contains($up)) { $officeCandidates.Add($up) }
            }
        }

        $officeSourcePath = $officeCandidates | Where-Object { $_ -and -not ($_ -like "\\*") -and (Test-Path (Join-Path $_ "Office\Data")) } | Select-Object -First 1
        $officeFilesDir = $officeCandidates | Where-Object { $_ -and -not ($_ -like "\\*") -and (Test-Path (Join-Path $_ "setup.exe")) -and (Test-Path (Join-Path $_ "Office\Data")) } | Select-Object -First 1
        if (-not $officeFilesDir) {
            $officeFilesDir = $officeCandidates | Where-Object { $_ -and -not ($_ -like "\\*") -and (Test-Path (Join-Path $_ "setup.exe")) } | Select-Object -First 1
        }

        if ((-not $officeSourcePath -or -not $officeFilesDir) -and (Get-Command Sync-ProgramiFromNas -ErrorAction SilentlyContinue)) {
            if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                Write-ServisLog "Lokalni Office 2024 offline paket nije potpun (SourcePath=$officeSourcePath, FilesDir=$officeFilesDir). Preuzimam sa NAS-a..." -Level "WARN" -Component "Office"
            }
            Sync-ProgramiFromNas -DestinationDir "C:\setup\Programi" -OnlyApp "Office" -SubFolders @("Office 2024") -Force | Out-Null
            if ($isSingleAppRetry -and (Get-Command Send-IpcStatus -ErrorAction SilentlyContinue)) {
                $curP = if ($null -ne $Global:CurrentPhase -and [int]$Global:CurrentPhase -gt 0) { [int]$Global:CurrentPhase } else { 4 }
                $curPrg = if ($null -ne $Global:CurrentProgress -and [int]$Global:CurrentProgress -gt 0) { [int]$Global:CurrentProgress } else { 95 }
                Send-IpcStatus -Phase $curP -Progress $curPrg -SubPhase "Office 2024" -StatusMessage "Instalacija: Microsoft Office 2024..."
            }
            $officeSourcePath = $officeCandidates | Where-Object { $_ -and -not ($_ -like "\\*") -and (Test-Path (Join-Path $_ "Office\Data")) } | Select-Object -First 1
            $officeFilesDir = $officeCandidates | Where-Object { $_ -and -not ($_ -like "\\*") -and (Test-Path (Join-Path $_ "setup.exe")) -and (Test-Path (Join-Path $_ "Office\Data")) } | Select-Object -First 1
            if (-not $officeFilesDir) {
                $officeFilesDir = $officeCandidates | Where-Object { $_ -and (Test-Path (Join-Path $_ "setup.exe")) } | Select-Object -First 1
            }
        }
        if (-not $officeSourcePath) {
            $officeSourcePath = $officeCandidates | Where-Object { $_ -and (Test-Path (Join-Path $_ "Office\Data")) } | Select-Object -First 1
        }

        $runOfficeOdtAttempt = {
            param(
                [string]$ExecDir,
                [string]$DataDir,
                [bool]$UseCleanFallbackXml
            )

            if (-not $ExecDir) { return }
            $setupExe = Join-Path $ExecDir "setup.exe"
            if (-not (Test-Path $setupExe)) { return }

            Unblock-File -Path $setupExe -ErrorAction SilentlyContinue
            Remove-Item -Path "$($setupExe):Zone.Identifier" -ErrorAction SilentlyContinue

            $effectiveDataDir = $DataDir
            if (-not $effectiveDataDir -or -not (Test-Path (Join-Path $effectiveDataDir "Office\Data"))) {
                if (Test-Path (Join-Path $ExecDir "Office\Data")) {
                    $effectiveDataDir = $ExecDir
                }
            }

            $officeVersion = "16.0.14334.20848"
            if ($effectiveDataDir -and (Test-Path (Join-Path $effectiveDataDir "Office\Data"))) {
                $vDir = Get-ChildItem -Path (Join-Path $effectiveDataDir "Office\Data") -Directory -Filter "16.0.*" -ErrorAction SilentlyContinue | Select-Object -First 1
                if ($vDir) {
                    $officeVersion = $vDir.Name
                } else {
                    $vCab = Get-ChildItem -Path (Join-Path $effectiveDataDir "Office\Data") -File -Filter "v*_16.0.*.cab" -ErrorAction SilentlyContinue | Select-Object -First 1
                    if ($vCab -and ($vCab.Name -match '(16\.0\.\d+\.\d+)')) {
                        $officeVersion = $matches[1]
                    }
                }
            }

            $xmlSearchDirs = @(
                $ExecDir,
                $effectiveDataDir,
                (Split-Path $ExecDir -Parent),
                "C:\setup\Programi\Office 2024\files",
                "C:\setup\Programi\Office 2024"
            ) | Where-Object { $_ } | Select-Object -Unique

            $foundXmlPath = $null
            foreach ($xDir in $xmlSearchDirs) {
                foreach ($xName in @("Configure.xml", "configuration.xml")) {
                    $candXml = Join-Path $xDir $xName
                    if (Test-Path $candXml) {
                        $foundXmlPath = $candXml
                        break
                    }
                }
                if ($foundXmlPath) { break }
            }

            if (-not (Test-Path "C:\setup")) {
                New-Item -Path "C:\setup" -ItemType Directory -Force -ErrorAction SilentlyContinue | Out-Null
            }
            $runtimeXmlPath = "C:\setup\Office2024_Install.xml"
            $finalXmlContent = ""

            if (-not $UseCleanFallbackXml -and $foundXmlPath -and (Test-Path $foundXmlPath)) {
                try {
                    Unblock-File -Path $foundXmlPath -ErrorAction SilentlyContinue
                    Remove-Item -Path "$($foundXmlPath):Zone.Identifier" -ErrorAction SilentlyContinue
                    $rawXml = [System.IO.File]::ReadAllText($foundXmlPath, [System.Text.Encoding]::UTF8)
                    $rawXml = $rawXml.TrimStart([char]0xFEFF).Trim()
                    [xml]$xmlDoc = $rawXml
                    $addNode = $xmlDoc.SelectSingleNode("//Add")
                    if ($addNode) {
                        if ($effectiveDataDir -and (Test-Path (Join-Path $effectiveDataDir "Office\Data"))) {
                            $addNode.SetAttribute("SourcePath", $effectiveDataDir)
                        }
                        $curVer = $addNode.GetAttribute("Version")
                        if ([string]::IsNullOrWhiteSpace($curVer)) {
                            if ($officeVersion) { $addNode.SetAttribute("Version", $officeVersion) }
                        } elseif ($effectiveDataDir -and (Test-Path (Join-Path $effectiveDataDir "Office\Data")) -and -not (Test-Path (Join-Path $effectiveDataDir "Office\Data\$curVer"))) {
                            if ($officeVersion -and (Test-Path (Join-Path $effectiveDataDir "Office\Data\$officeVersion"))) {
                                $addNode.SetAttribute("Version", $officeVersion)
                            }
                        }
                    }
                    $finalXmlContent = $xmlDoc.OuterXml
                } catch {
                    $finalXmlContent = ""
                }
            }

            if ([string]::IsNullOrWhiteSpace($finalXmlContent)) {
                $srcAttr = if ($effectiveDataDir) { " SourcePath=`"$effectiveDataDir`"" } else { "" }
                $finalXmlContent = @"
<Configuration>
  <Add OfficeClientEdition="64" Channel="PerpetualVL2021" Version="$officeVersion"$srcAttr>
    <Product ID="ProPlus2021Volume">
      <Language ID="en-us" />
      <ExcludeApp ID="Groove" />
      <ExcludeApp ID="Lync" />
      <ExcludeApp ID="OneDrive" />
      <ExcludeApp ID="Teams" />
    </Product>
  </Add>
  <Display Level="None" AcceptEULA="TRUE" />
  <Updates Enabled="TRUE" />
  <Property Name="AUTOACTIVATE" Value="1" />
</Configuration>
"@
            }

            $utf8NoBom = New-Object System.Text.UTF8Encoding($false)
            [System.IO.File]::WriteAllText($runtimeXmlPath, $finalXmlContent, $utf8NoBom)
            Unblock-File -Path $runtimeXmlPath -ErrorAction SilentlyContinue

            # Zaustavljanje zaostalih Office / ClickToRun procesa
            @("OfficeClickToRun", "OfficeC2RClient", "appvshnotify") | ForEach-Object {
                Get-Process -Name $_ -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
            }
            Get-Process -Name "setup" -ErrorAction SilentlyContinue | Where-Object {
                $_.Path -like "*Office*" -or $_.Path -like "*setup*"
            } | Stop-Process -Force -ErrorAction SilentlyContinue

            if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                $origLabel = if ($foundXmlPath) { $foundXmlPath } else { "(generisan)" }
                Write-ServisLog "Pokrecem offline ODT instalaciju: `"$setupExe`" /configure `"$runtimeXmlPath`" (SourcePath=$effectiveDataDir, Version=$officeVersion, OrigXml=$origLabel, Fallback=$UseCleanFallbackXml)" -Level "STEP" -Component "Office"
            }

            $cfgArg = "/configure `"$runtimeXmlPath`""
            $p = Start-Process -FilePath $setupExe -ArgumentList $cfgArg -WorkingDirectory $ExecDir -PassThru -WindowStyle Hidden
            if ($p) {
                $odtWait = 0
                while ((-not $p.HasExited) -and ($odtWait -lt 420)) {
                    Start-Sleep -Seconds 5
                    $odtWait += 5
                    if (-not $preservePhase -and ($odtWait % 30 -eq 0) -and (Get-Command Send-IpcProgress -ErrorAction SilentlyContinue)) {
                        $offPct = [math]::Min(78, 65 + [int](($odtWait / 420) * 13))
                        Send-IpcProgress -Progress $offPct -Phase 4 -PhaseName "Programi" -SubPhase "Office 2024" -StatusMessage "Instalacija: Microsoft Office 2024 (${odtWait}s)..."
                    }
                }
                if (-not $p.HasExited) {
                    if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                        Write-ServisLog "Office ODT setup.exe prekoracio timeout od 420s. Prekidam proces..." -Level "WARN" -Component "Office"
                    }
                    try { $p.Kill() } catch {}
                } else {
                    if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                        $oCode = $p.ExitCode
                        $oLvl = if ($oCode -eq 0 -or $oCode -eq 3010) { "SUCCESS" } else { "WARN" }
                        Write-ServisLog "Office ODT setup.exe zavrsen (ExitCode=$oCode, Trajanje=${odtWait}s)." -Level $oLvl -Component "Office"
                    }
                }
            }

            # Cekanje da ClickToRun zavrsi upis WINWORD.EXE / EXCEL.EXE i registraciju
            $c2rWait = 0
            while ($c2rWait -lt 120) {
                $wordNow = (Test-Path "C:\Program Files\Microsoft Office\root\Office16\WINWORD.EXE") -or `
                           (Test-Path "C:\Program Files (x86)\Microsoft Office\root\Office16\WINWORD.EXE") -or `
                           (Test-Path "C:\Program Files\Microsoft Office\Office16\WINWORD.EXE") -or `
                           (Test-Path "C:\Program Files\Microsoft Office\root\Office16\EXCEL.EXE") -or `
                           (Test-Path "C:\Program Files (x86)\Microsoft Office\root\Office16\EXCEL.EXE")
                $regNow = (Get-ItemProperty -Path "HKLM:\SOFTWARE\Microsoft\Office\ClickToRun\Configuration" -Name "ProductReleaseIds" -ErrorAction SilentlyContinue) -or `
                          (Test-Path "HKLM:\SOFTWARE\Microsoft\Office\ClickToRun\ProductReleaseIDs") -or `
                          (Test-Path "HKLM:\SOFTWARE\WOW6432Node\Microsoft\Office\ClickToRun\ProductReleaseIDs")
                $setupActive = Get-Process -Name "setup" -ErrorAction SilentlyContinue | Where-Object {
                    $_.Path -like "*Office*" -or $_.Path -like "*setup*"
                }

                if ($wordNow -and $regNow -and (-not $setupActive)) {
                    Start-Sleep -Seconds 1
                    break
                }
                if ($wordNow -and (-not $setupActive) -and ($c2rWait -ge 10)) {
                    Start-Sleep -Seconds 1
                    break
                }
                Start-Sleep -Seconds 1
                $c2rWait += 1
            }
        }

        if ($officeFilesDir) {
            & $runOfficeOdtAttempt $officeFilesDir $officeSourcePath $false

            $postOfficeCheck = Get-WisAppPresence -AppId "Office"
            if (-not $postOfficeCheck.Installed) {
                if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                    Write-ServisLog "Prvi pokusaj instalacije Office 2024 nije stvorio WINWORD.EXE/EXCEL.EXE. Pokrecem prinudno NAS osvezavanje i ponovni pokusaj sa cistim XML-om..." -Level "WARN" -Component "Office"
                }
                if (Get-Command Sync-ProgramiFromNas -ErrorAction SilentlyContinue) {
                    Sync-ProgramiFromNas -DestinationDir "C:\setup\Programi" -OnlyApp "Office" -SubFolders @("Office 2024") -Force | Out-Null
                    if ($isSingleAppRetry -and (Get-Command Send-IpcStatus -ErrorAction SilentlyContinue)) {
                        $curP = if ($null -ne $Global:CurrentPhase -and [int]$Global:CurrentPhase -gt 0) { [int]$Global:CurrentPhase } else { 4 }
                        $curPrg = if ($null -ne $Global:CurrentProgress -and [int]$Global:CurrentProgress -gt 0) { [int]$Global:CurrentProgress } else { 95 }
                        Send-IpcStatus -Phase $curP -Progress $curPrg -SubPhase "Office 2024" -StatusMessage "Instalacija: Microsoft Office 2024..."
                    }
                    $retrySourcePath = $officeCandidates | Where-Object { $_ -and -not ($_ -like "\\*") -and (Test-Path (Join-Path $_ "Office\Data")) } | Select-Object -First 1
                    $retryFilesDir = $officeCandidates | Where-Object { $_ -and -not ($_ -like "\\*") -and (Test-Path (Join-Path $_ "setup.exe")) -and (Test-Path (Join-Path $_ "Office\Data")) } | Select-Object -First 1
                    if (-not $retryFilesDir) {
                        $retryFilesDir = $officeCandidates | Where-Object { $_ -and (Test-Path (Join-Path $_ "setup.exe")) } | Select-Object -First 1
                    }
                    if ($retryFilesDir) { $officeFilesDir = $retryFilesDir }
                    if ($retrySourcePath) { $officeSourcePath = $retrySourcePath }
                }
                if ($officeFilesDir) {
                    & $runOfficeOdtAttempt $officeFilesDir $officeSourcePath $true
                }
            }
        } else {
            if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                Write-ServisLog "Office 2024 instalacioni paket (setup.exe) nije pronadjen ni lokalno ni na NAS-u." -Level "ERROR" -Component "Office"
            }
        }
    }

    $appsStatusMap["Office"] = if ($stepOffice.State -in @("OK", "SKIPPED_DONE")) { "OK" } else { "WARN" }
    $Global:WisOfficeInstallFailed = [bool](($stepOffice.State -notin @("OK", "SKIPPED_DONE")) -or (-not (Test-OfficeInstalled)))
    if (Get-Command Send-IpcAppUpdate -ErrorAction SilentlyContinue) {
        $offDetail = if ($stepOffice.State -eq "SKIPPED_DONE") { "Već instalirano" } elseif ($stepOffice.State -eq "OK") { "Instaliran Office 2024" } else { "Neuspešno - Nastavak" }
        Send-IpcAppUpdate -AppId "Office" -Status $appsStatusMap["Office"] -Detail $offDetail
    }
    }

    # =========================================================================
    # 6. BATTERYBAR - P4.BatteryBar
    # =========================================================================
    if ($runBatteryBar) {
    $stepBattery = Invoke-WisStep -Phase 4 -StepId "P4.BatteryBar" -Title "BatteryBar indikator baterije" -NoSkip:$isSingleAppRetry -Test {
        Get-WisAppPresence -AppId "BatteryBar"
    } -Action {
        if (-not $isLaptop) {
            if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                Write-ServisLog "Uređaj nema bateriju (Desktop). BatteryBar nije potreban." -Level "INFO" -Component "BatteryBar"
            }
            return
        }

        if (Get-Command Wait-ServisUICooperativePause -ErrorAction SilentlyContinue) {
            Wait-ServisUICooperativePause -CheckpointName "Faza 4 - BatteryBar"
        }
        if (-not $preservePhase -and (Get-Command Send-IpcProgress -ErrorAction SilentlyContinue)) {
            Send-IpcProgress -Progress 80 -Phase 4 -PhaseName "Programi" -SubPhase "BatteryBar" -StatusMessage "Instalacija: BatteryBar..."
        } elseif ($isSingleAppRetry -and (Get-Command Send-IpcStatus -ErrorAction SilentlyContinue)) {
            $curP = if ($null -ne $Global:CurrentPhase -and [int]$Global:CurrentPhase -gt 0) { [int]$Global:CurrentPhase } else { 4 }
            $curPrg = if ($null -ne $Global:CurrentProgress -and [int]$Global:CurrentProgress -gt 0) { [int]$Global:CurrentProgress } else { 95 }
            Send-IpcStatus -Phase $curP -Progress $curPrg -SubPhase "BatteryBar" -StatusMessage "Instalacija: BatteryBar..."
        }
        if (Get-Command Send-IpcAppUpdate -ErrorAction SilentlyContinue) {
            Send-IpcAppUpdate -AppId "BatteryBar" -Status "RUNNING" -Detail "Provera baterije i instalacija..."
        }

        $targetBbDir = "C:\Program Files\BatteryBar"
        $targetBbExe = Join-Path $targetBbDir "BatteryBar.exe"
        $installedExe = @(
            $targetBbExe,
            "C:\Program Files (x86)\BatteryBar\BatteryBar.exe"
        ) | Where-Object { Test-Path $_ } | Select-Object -First 1

        if ($installedExe -and (Test-Path $installedExe)) {
            Set-BatteryBarConfiguration -InstalledExe $installedExe | Out-Null
        } else {
            $bbFolderCandidates = @(
                (Join-Path $activeProgDir "BatteryBar"),
                (Join-Path $LocalSetupDir "Programi\BatteryBar"),
                "C:\setup\Programi\BatteryBar"
            )
            $bbFoundDir = $bbFolderCandidates | Where-Object { $_ -and (Test-Path (Join-Path $_ "BatteryBar.exe")) } | Select-Object -First 1
            if (-not $bbFoundDir -and (Get-Command Sync-ProgramiFromNas -ErrorAction SilentlyContinue)) {
                $null = Sync-ProgramiFromNas -DestinationDir "C:\setup\Programi" -OnlyApp "BatteryBar" -SubFolders @("BatteryBar") -Force
                if ($isSingleAppRetry -and (Get-Command Send-IpcStatus -ErrorAction SilentlyContinue)) {
                    $curP = if ($null -ne $Global:CurrentPhase -and [int]$Global:CurrentPhase -gt 0) { [int]$Global:CurrentPhase } else { 4 }
                    $curPrg = if ($null -ne $Global:CurrentProgress -and [int]$Global:CurrentProgress -gt 0) { [int]$Global:CurrentProgress } else { 95 }
                    Send-IpcStatus -Phase $curP -Progress $curPrg -SubPhase "BatteryBar" -StatusMessage "Instalacija: BatteryBar..."
                }
                $bbFoundDir = $bbFolderCandidates | Where-Object { $_ -and (Test-Path (Join-Path $_ "BatteryBar.exe")) } | Select-Object -First 1
            }
            if ($bbFoundDir) {
                if (-not (Test-Path $targetBbDir)) {
                    New-Item -Path $targetBbDir -ItemType Directory -Force -ErrorAction SilentlyContinue | Out-Null
                }
                Copy-Item -Path (Join-Path $bbFoundDir "*") -Destination $targetBbDir -Exclude @("ShowBatteryBar.exe", "BatteryBar.dll", "BarExplorerHook.dll", "BatteryBarSetup*.exe") -Recurse -Force -ErrorAction SilentlyContinue
                if (Test-Path $targetBbExe) {
                    Set-BatteryBarConfiguration -InstalledExe $targetBbExe | Out-Null
                }
            }
        }
    }

    $appsStatusMap["BatteryBar"] = if ($stepBattery.State -in @("OK", "SKIPPED_DONE")) { "OK" } else { "WARN" }
    if (Get-Command Send-IpcAppUpdate -ErrorAction SilentlyContinue) {
        $bbDetail = if ($stepBattery.State -eq "SKIPPED_DONE") {
            if (-not $isLaptop) { "Preskočeno (Desktop)" } else { "Već instalirano" }
        } elseif ($stepBattery.State -eq "OK") {
            "Podešen BatteryBar"
        } else {
            "Preskočeno - paket nije pronađen"
        }
        Send-IpcAppUpdate -AppId "BatteryBar" -Status $appsStatusMap["BatteryBar"] -Detail $bbDetail
    }
    }

    # =========================================================================
    # 7. RADNE PREČICE NA RADNOJ POVRŠINI - P4.Shortcuts
    # =========================================================================
    if ($runShortcuts) {
    $stepShortcuts = Invoke-WisStep -Phase 4 -StepId "P4.Shortcuts" -Title "Radne prečice na radnoj površini" -NoSkip:$isSingleAppRetry -Test {
        Get-WisAppPresence -AppId "Shortcuts"
    } -Action {
        if (Get-Command Wait-ServisUICooperativePause -ErrorAction SilentlyContinue) {
            Wait-ServisUICooperativePause -CheckpointName "Faza 4 - Prečice"
        }
        if (-not $preservePhase -and (Get-Command Send-IpcProgress -ErrorAction SilentlyContinue)) {
            Send-IpcProgress -Progress 88 -Phase 4 -PhaseName "Programi" -SubPhase "Prečice" -StatusMessage "Kreiranje prečica na radnoj površini..."
        } elseif ($isSingleAppRetry -and (Get-Command Send-IpcStatus -ErrorAction SilentlyContinue)) {
            $curP = if ($null -ne $Global:CurrentPhase -and [int]$Global:CurrentPhase -gt 0) { [int]$Global:CurrentPhase } else { 4 }
            $curPrg = if ($null -ne $Global:CurrentProgress -and [int]$Global:CurrentProgress -gt 0) { [int]$Global:CurrentProgress } else { 95 }
            Send-IpcStatus -Phase $curP -Progress $curPrg -SubPhase "Prečice" -StatusMessage "Instalacija: Shortcuts..."
        }
        if (Get-Command Send-IpcAppUpdate -ErrorAction SilentlyContinue) {
            Send-IpcAppUpdate -AppId "Shortcuts" -Status "RUNNING" -Detail "Kreiranje prečica na radnoj površini..."
        }
        Create-ServisShortcuts
    }

    $appsStatusMap["Shortcuts"] = if ($stepShortcuts.State -in @("OK", "SKIPPED_DONE")) { "OK" } else { "WARN" }
    if (Get-Command Send-IpcAppUpdate -ErrorAction SilentlyContinue) {
        $scDetail = if ($stepShortcuts.State -eq "SKIPPED_DONE") { "Već kreirano" } else { "Prečice kreirane" }
        Send-IpcAppUpdate -AppId "Shortcuts" -Status $appsStatusMap["Shortcuts"] -Detail $scDetail
    }
    }

    # =========================================================================
    # 8. PODRAZUMEVANI PDF PREGLEDAČ - P4.PdfDefault
    # =========================================================================
    if ($runPdfDefault) {
    $stepPdf = Invoke-WisStep -Phase 4 -StepId "P4.PdfDefault" -Title "Podrazumevani PDF pregledač" -NoSkip:$isSingleAppRetry -Test {
        Get-WisAppPresence -AppId "PdfDefault"
    } -Action {
        $pdfOk = Set-AcrobatDefaultPDF
        if (-not $pdfOk) {
            throw "SKIPPED_ERROR: Adobe Acrobat (Acrobat.exe) nije instaliran na disku — nije moguće podesiti podrazumevani PDF čitač."
        }
    }
    if (-not (Get-WisAppPresence -AppId "Acrobat").Installed) {
        $pdfErrReason = "SKIPPED_ERROR: Adobe Acrobat (Acrobat.exe) nije instaliran na disku"
        if ($stepPdf) {
            $stepPdf.State  = "SKIPPED_ERROR"
            $stepPdf.Reason = $pdfErrReason
        } else {
            $stepPdf = @{ State = "SKIPPED_ERROR"; Reason = $pdfErrReason; Executed = $true; DurationMs = 0 }
        }
        if (Get-Command Send-IpcStep -ErrorAction SilentlyContinue) {
            Send-IpcStep -Phase 4 -StepId "P4.PdfDefault" -Title "Podrazumevani PDF pregledač" -State "ERROR" -Reason $pdfErrReason
        }
    }
    }

    # =========================================================================
    # 9. PROVERA OEM I SISTEMSKIH LICENCI - P4.Activation
    # =========================================================================
    if ($runLicense) {
    $officePlannedButMissing = [bool]($runOffice -and (($null -eq $stepOffice) -or ($stepOffice.State -notin @("OK", "SKIPPED_DONE")) -or ($appsStatusMap["Office"] -notin @("OK", "SKIP")) -or (-not (Test-OfficeInstalled))))
    $stepActivation = Invoke-WisStep -Phase 4 -StepId "P4.Activation" -Title "Aktivacija Windows i Office paketa" -NoSkip:($isSingleAppRetry -or $officePlannedButMissing) -Test {
        Get-WisAppPresence -AppId "Activation"
    } -Action {
        if (Get-Command Wait-ServisUICooperativePause -ErrorAction SilentlyContinue) {
            Wait-ServisUICooperativePause -CheckpointName "Faza 4 - Provera licenci"
        }
        if (-not $preservePhase -and (Get-Command Send-IpcProgress -ErrorAction SilentlyContinue)) {
            Send-IpcProgress -Progress 93 -Phase 4 -PhaseName "Programi" -SubPhase "Provera licenci" -StatusMessage "Verifikacija OEM i sistemskih licenci..."
        } elseif ($isSingleAppRetry -and (Get-Command Send-IpcStatus -ErrorAction SilentlyContinue)) {
            $curP = if ($null -ne $Global:CurrentPhase -and [int]$Global:CurrentPhase -gt 0) { [int]$Global:CurrentPhase } else { 4 }
            $curPrg = if ($null -ne $Global:CurrentProgress -and [int]$Global:CurrentProgress -gt 0) { [int]$Global:CurrentProgress } else { 95 }
            Send-IpcStatus -Phase $curP -Progress $curPrg -SubPhase "Provera licenci" -StatusMessage "Instalacija: License..."
        }
        if (Get-Command Send-IpcAppUpdate -ErrorAction SilentlyContinue) {
            Send-IpcAppUpdate -AppId "License" -Status "RUNNING" -Detail "Provera sistemskih licenci..."
        }

        if ((Get-Command Test-ServisSkipRequested -ErrorAction SilentlyContinue) -and (Test-ServisSkipRequested)) {
            if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                Write-ServisLog "Provera licenci preskočena na zahtev servisera." -Level "WARN" -Component "License"
            }
            return
        }

        Invoke-WindowsOfficeActivation -ProgramiDir $activeProgDir
    }

    if ($officePlannedButMissing) {
        $offMissingWarn = "UPOZORENJE: Microsoft Office nije instaliran (P4.Office nije uspeo) — Office aktivacija nije izvršena"
        if ($stepActivation) {
            $stepActivation.State = "WARN"
            if ([string]::IsNullOrWhiteSpace($stepActivation.Reason) -or $stepActivation.Reason -match 'Office nije instaliran') {
                $stepActivation.Reason = $offMissingWarn
            }
        }
        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
            Write-ServisLog "P4.Activation: $offMissingWarn" -Level "WARN" -Component "License"
        }
        if (Get-Command Send-IpcStep -ErrorAction SilentlyContinue) {
            Send-IpcStep -Phase 4 -StepId "P4.Activation" -Title "Aktivacija Windows i Office paketa" -State "WARN" -Reason $stepActivation.Reason
        }
    }

    $appsStatusMap["License"] = if (($stepActivation.State -in @("OK", "SKIPPED_DONE")) -and (-not $officePlannedButMissing)) { "OK" } else { "WARN" }
    $licStatusFile = Join-Path $LocalSetupDir "license-status.json"
    if ($stepActivation.State -eq "SKIPPED_DONE" -and -not (Test-Path -LiteralPath $licStatusFile -ErrorAction SilentlyContinue)) {
        try {
            $skipWinSt = Get-WindowsLicenseStatus
            $skipWinAct = [bool]($skipWinSt -and $skipWinSt.Activated)
            $skipWinOa3 = [bool]($skipWinSt -and $skipWinSt.Oa3xPresent)
            $skipWinEd = if ($skipWinSt -and $skipWinSt.Edition) { [string]$skipWinSt.Edition } else { "" }
            $skipWinMethod = if ($skipWinSt -and $skipWinSt.Method) { [string]$skipWinSt.Method } else { "NONE" }
            $skipWinDetail = if ($skipWinSt -and $skipWinSt.SkuName) { "$($skipWinSt.SkuName) ($($skipWinSt.LicenseStatusText))" } elseif ($skipWinSt -and $skipWinSt.LicenseStatusText) { [string]$skipWinSt.LicenseStatusText } else { "Već licencirano" }
            $skipHasOff = Test-OfficeInstalled
            $skipOffSt = if ($skipHasOff) { Get-OfficeLicenseStatus } else { $null }
            $skipOffAct = if ($skipHasOff) { [bool]($skipOffSt -and $skipOffSt.Activated) } else { $true }
            $skipCfg = Get-WisLicensingSettingsLocal
            $skipMode = if ($skipCfg -and $skipCfg.OfficeActivationMode) { [string]$skipCfg.OfficeActivationMode } else { "Auto" }
            $skipMethod = if (-not $skipHasOff) { "NotInstalled" } elseif ($skipOffAct -and $skipOffSt) { [string]$skipOffSt.Method } elseif ($skipMode -ieq "Skip") { "Skip" } else { "NONE" }
            $skipDetailTxt = if ($stepActivation.Reason) { [string]$stepActivation.Reason } else { "Već licencirano" }
            $null = Write-WisLicenseStatusFile -WindowsActivated $skipWinAct -WindowsOa3Present $skipWinOa3 -WindowsEdition $skipWinEd -WindowsLicenseMethod $skipWinMethod -WindowsLicenseDetail $skipWinDetail -OfficeInstalled $skipHasOff -OfficeActivated $skipOffAct -OfficeActivationMode $skipMode -OfficeLicenseMethod $skipMethod -OfficeLicenseDetail $skipDetailTxt -LocalSetupDir $LocalSetupDir
        } catch {}
    }
    if (Get-Command Send-IpcAppUpdate -ErrorAction SilentlyContinue) {
        $licDetail = if ($stepActivation.Reason) {
            Protect-WisLicenseLogText -Text ([string]$stepActivation.Reason)
        } elseif ($stepActivation.State -eq "SKIPPED_DONE") {
            "Već licencirano"
        } elseif ($stepActivation.State -eq "OK") {
            "Verifikovano"
        } else {
            "Best-Effort nastavak"
        }
        Send-IpcAppUpdate -AppId "License" -Status $appsStatusMap["License"] -Detail $licDetail
    }
    }

    # Režim pojedinačnog Retry-a: verifikacija samo izabranog programa i povratak bez resetovanja ostalih
    if ($isSingleAppRetry) {
        $targetStepObj = switch ($retryCanonicalAppId) {
            "DotNet"     { $stepRuntimes }
            "WinRAR"     { $stepWinrar }
            "Chrome"     { $stepChrome }
            "Acrobat"    { $stepAcrobat }
            "Office"     { $stepOffice }
            "BatteryBar" { $stepBattery }
            "Shortcuts"  { $stepShortcuts }
            "License"    { $stepActivation }
            default      { $null }
        }
        $presenceCheck = Get-WisAppPresence -AppId $retryPresenceId
        $stepOk = ($null -ne $targetStepObj) -and ($targetStepObj.State -in @("OK", "SKIPPED_DONE"))
        $retryOk = ([bool]$presenceCheck.Installed) -or $stepOk

        $isLicenseBestEffortWarn = $false
        if ((-not $retryOk) -and ($retryCanonicalAppId -eq "License") -and $presenceCheck -and [bool]$presenceCheck.WindowsActivated) {
            $licCfgRetry = $null
            try { $licCfgRetry = Get-WisLicensingSettingsLocal } catch {}
            $licModeRetry = if ($licCfgRetry -and $licCfgRetry.OfficeActivationMode) { [string]$licCfgRetry.OfficeActivationMode } else { "Auto" }
            $hasKmsOrKeyRetry = ($licCfgRetry -and (
                (-not [string]::IsNullOrWhiteSpace($licCfgRetry.OfficeKmsHost)) -or
                ((-not [string]::IsNullOrWhiteSpace($licCfgRetry.OfficeProductKey)) -and $licCfgRetry.OfficeProductKey.Trim().Length -ge 25)
            ))
            if (($licModeRetry -in @("Auto", "Skip")) -and (-not $hasKmsOrKeyRetry)) {
                $isLicenseBestEffortWarn = $true
            }
        }

        if ($retryOk) {
            $okDetailRaw = if ($presenceCheck.Message) { [string]$presenceCheck.Message } else { "Instalacija uspešno verifikovana" }
            $okDetail = Protect-WisLicenseLogText -Text $okDetailRaw
            if (Get-Command Send-IpcAppUpdate -ErrorAction SilentlyContinue) {
                Send-IpcAppUpdate -AppId $retryCanonicalAppId -Status "OK" -Detail $okDetail
            }
            if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                Write-ServisLog "Pojedinačni Retry za $retryCanonicalAppId uspešan: $okDetail" -Level "SUCCESS" -Component "Phase4"
            }
            if (Get-Command Send-IpcLog -ErrorAction SilentlyContinue) {
                Send-IpcLog -Message "Pojedinačni Retry za $retryCanonicalAppId uspešan: $okDetail" -Level "SUCCESS" -Tag "[Phase4]" -SkipFileLog
            }
            $global:LASTEXITCODE = 0
            return
        } elseif ($isLicenseBestEffortWarn) {
            $warnDetailRaw = if ($presenceCheck.Reason) { [string]$presenceCheck.Reason } elseif ($presenceCheck.Message) { [string]$presenceCheck.Message } else { "Windows aktiviran; Office čeka licenciranje (nema podešenog KMS hosta/ključa u [Licensing])" }
            $warnDetail = Protect-WisLicenseLogText -Text $warnDetailRaw
            if (Get-Command Send-IpcAppUpdate -ErrorAction SilentlyContinue) {
                Send-IpcAppUpdate -AppId "License" -Status "WARN" -Detail $warnDetail
            }
            if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                Write-ServisLog "Pojedinačni Retry za License završen sa WARN (Windows aktiviran, Office bez konfigurisanog KMS/MAK ključa u [Licensing]): $warnDetail" -Level "WARN" -Component "Phase4"
            }
            if (Get-Command Send-IpcLog -ErrorAction SilentlyContinue) {
                Send-IpcLog -Message "Provera licenci (Retry): $warnDetail" -Level "WARN" -Tag "[License]" -SkipFileLog
            }
            $global:LASTEXITCODE = 0
            return
        } else {
            $failDetailRaw = if ($presenceCheck.Reason) { [string]$presenceCheck.Reason } elseif ($presenceCheck.Message) { [string]$presenceCheck.Message } else { "Verifikacija nakon instalacije nije uspela" }
            $failDetail = Protect-WisLicenseLogText -Text $failDetailRaw
            if (Get-Command Send-IpcAppUpdate -ErrorAction SilentlyContinue) {
                Send-IpcAppUpdate -AppId $retryCanonicalAppId -Status "ERROR" -Detail $failDetail
            }
            if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                Write-ServisLog "Pojedinačni Retry za $retryCanonicalAppId nije uspeo: $failDetail" -Level "ERROR" -Component "Phase4"
            }
            if (Get-Command Send-IpcLog -ErrorAction SilentlyContinue) {
                Send-IpcLog -Message "Pojedinačni Retry za $retryCanonicalAppId nije uspeo: $failDetail" -Level "ERROR" -Tag "[Phase4]" -SkipFileLog
            }
            throw "Pojedinačni Retry za $retryCanonicalAppId nije uspeo: $failDetail"
        }
    }

    # Uklanjanje flag datoteke za ponovno pokretanje Faze 4
    if (Test-Path "C:\setup\force-rerun-phase4.flag") {
        Remove-Item -Path "C:\setup\force-rerun-phase4.flag" -Force -ErrorAction SilentlyContinue
    }

    # 10. Reality Check: Fizička verifikacija stvarnog postojanja aplikacija na disku
    if (-not $preservePhase -and (Get-Command Send-IpcProgress -ErrorAction SilentlyContinue)) {
        Send-IpcProgress -Progress 96 -Phase 4 -PhaseName "Programi" -SubPhase "Reality Check" -StatusMessage "Verifikacija fizičkog prisustva aplikacija na disku..."
    }

    $appsToVerify = [System.Collections.Generic.List[string]]::new()
    if ($appsStatusMap["Chrome"] -ne "SKIP") { $appsToVerify.Add("Chrome") }
    if ($appsStatusMap["Acrobat"] -ne "SKIP") { $appsToVerify.Add("Acrobat") }
    if ($appsStatusMap["Office"] -ne "SKIP") { $appsToVerify.Add("Office") }
    if ($appsStatusMap["WinRAR"] -ne "SKIP") { $appsToVerify.Add("WinRAR") }
    if ($isLaptop -and ($appsStatusMap["BatteryBar"] -ne "SKIP") -and ($appsStatusMap["BatteryBar"] -ne "WARN")) {
        $appsToVerify.Add("BatteryBar")
    }

    $realityResult = Assert-InstalledApplicationsReality -RequiredApps $appsToVerify
    if (-not $realityResult.Success) {
        $missingStr = $realityResult.MissingApps -join ", "
        foreach ($missing in $realityResult.MissingApps) {
            if ($missing -eq "Google Chrome") { $appsStatusMap["Chrome"] = "ERROR" }
            elseif ($missing -eq "Adobe Acrobat") { $appsStatusMap["Acrobat"] = "ERROR" }
            elseif ($missing -eq "Microsoft Office") { $appsStatusMap["Office"] = "ERROR" }
            elseif ($missing -eq "WinRAR") { $appsStatusMap["WinRAR"] = "ERROR" }
            elseif ($missing -eq "BatteryBar") { $appsStatusMap["BatteryBar"] = "ERROR" }
        }

        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
            Write-ServisLog "REALITY CHECK GREŠKA: Sledeće obavezne aplikacije fizički ne postoje na disku: $missingStr" -Level "ERROR" -Component "Phase4"
        }
        if (Get-Command Send-IpcLog -ErrorAction SilentlyContinue) {
            Send-IpcLog -Message "FATALNA GREŠKA FAZE: Fizički nedostaju instalirane aplikacije ($missingStr)" -Level "FATAL" -Tag "[FATAL]"
        }
        if (-not $preservePhase -and (Get-Command Send-IpcStatus -ErrorAction SilentlyContinue)) {
            Send-IpcStatus -Phase 4 -Progress 98 -SubPhase "Reality Check Neuspeh" -StatusMessage "Greška: Fizički nedostaju aplikacije ($missingStr)" -Apps $appsStatusMap
        }
        throw "REALITY CHECK GREŠKA: Instalacija aplikacija nije kompletna! Fizički nedostaju na disku: $missingStr"
    }

    # 11. Zavrsni status (sinhronizacija License statusa pre slanja zavrsnog statusa) i perzistencija stanja u deploy-state.json
    try {
        if (-not $appsStatusMap.ContainsKey("License") -or [string]::IsNullOrWhiteSpace($appsStatusMap["License"]) -or $appsStatusMap["License"] -eq "RUNNING") {
            $appsStatusMap["License"] = "WARN"
            if (Test-Path -LiteralPath (Join-Path $LocalSetupDir "license-status.json") -ErrorAction SilentlyContinue) {
                $appsStatusMap["License"] = "OK"
            }
            if (Get-Command Send-IpcAppUpdate -ErrorAction SilentlyContinue) {
                Send-IpcAppUpdate -AppId "License" -Status $appsStatusMap["License"] -Detail "Završna sinhronizacija"
            }
        }
        if (-not $preservePhase -and (Get-Command Send-IpcStatus -ErrorAction SilentlyContinue)) {
            Send-IpcStatus -Phase 4 -Progress 98 -SubPhase "Završna verifikacija" -StatusMessage "Svi korisnički programi su obrađeni i verifikovani na disku." -Apps $appsStatusMap
        }
    } catch {}

    try {
        if (-not $preservePhase -and (Get-Command Save-DeployStatePhase -ErrorAction SilentlyContinue)) {
            Save-DeployStatePhase -Phase 5
        }
    } catch {}

    if (-not $preservePhase -and (Get-Command Send-IpcProgress -ErrorAction SilentlyContinue)) {
        Send-IpcProgress -Progress 100 -Phase 4 -PhaseName "Programi" -SubPhase "Završeno" -StatusMessage "Faza 4 uspešno završena. Programi verifikovani."
    }
    if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
        Write-ServisLog "=== FAZA 4 USPESNO ZAVRSENA: Svi programi su verifikovani na disku ===" -Level "SUCCESS" -Component "Phase4"
    }

    $global:LASTEXITCODE = 0
    } finally {
        $Global:WisPreserveCurrentPhase = $prevPreserveFlag
    }
}

Export-ModuleMember -Function `
    Get-ServisProgramiRoot, `
    Invoke-InstallerWithLocalFallback, `
    Install-ServisVCRedist, `
    Install-ServisDotNet, `
    Remove-PendingRebootLocks, `
    Test-MsiBusy, `
    Wait-MsiMutexAvailable, `
    Invoke-SafeMsi, `
    Invoke-WingetAppInstall, `
    New-AppShortcut, `
    Refresh-ExplorerShell, `
    Remove-WinRARDesktopShortcut, `
    Create-ServisShortcuts, `
    Set-AcrobatDefaultPDF, `
    Get-WindowsLicenseStatus, `
    Test-WindowsActivated, `
    Test-OfficeInstalled, `
    Protect-WisLicenseLogText, `
    Invoke-CscriptWithTimeout, `
    Get-OfficeLicenseStatus, `
    Test-OfficeActivated, `
    Test-AllServisProgramsInstalled, `
    Get-WisAppPresence, `
    Test-BatteryPresent, `
    Install-AllServisPrograms, `
    Set-BatteryBarConfiguration, `
    Wait-OfficeInstallationReady, `
    Invoke-ServisExternalLicensingHook, `
    Invoke-WindowsOfficeActivation, `
    Invoke-AppsPhase, `
    Invoke-Phase4Apps, `
    Assert-InstalledApplicationsReality
