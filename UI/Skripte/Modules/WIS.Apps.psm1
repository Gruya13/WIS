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
    # 1. OS-wide flag ServisWuActive (postavlja Windows Update tokom instalacije drajvera/paketa)
    try {
        $wuFlag = (Get-ItemProperty -Path "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Installer" -Name "ServisWuActive" -ErrorAction SilentlyContinue).ServisWuActive
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
    Wait-MsiMutexAvailable -TimeoutSec $MaxWaitSec
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
                Wait-MsiMutexAvailable -TimeoutSec 120
                Remove-PendingRebootLocks
                continue
            }
            if ($proc.ExitCode -eq 1603 -and $retry -lt 3) {
                if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                    Write-ServisLog "msiexec vratio kod 1603 (Fatal Error). Pokusavam oslobadjanje brava i retry za 5s (Pokusaj $retry/5)..." -Level "WARN" -Component "SafeMsi"
                }
                Start-Sleep -Seconds 5
                Remove-PendingRebootLocks
                Wait-MsiMutexAvailable -TimeoutSec 60
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

        cmd.exe /c "assoc .pdf=AcroExch.Document.DC" | Out-Null
        $acrobatExe = "C:\Program Files\Adobe\Acrobat DC\Acrobat\Acrobat.exe"
        if (-not (Test-Path $acrobatExe)) {
            $acrobatExe = "C:\Program Files\Adobe\Acrobat 2024\Acrobat\Acrobat.exe"
        }
        if (Test-Path $acrobatExe) {
            cmd.exe /c "ftype AcroExch.Document.DC=`"$acrobatExe`" `"%1`"" | Out-Null
        }
    } catch {}
}

function Test-WindowsActivated {
    try {
        $res = (cscript //nologo C:\Windows\System32\slmgr.vbs /xpr 2>$null) -join " "
        if ($res -match "permanently activated|volume activation will expire|is activated|trajno aktiviran|aktiviran") {
            return $true
        }
    } catch {}
    return $false
}

function Test-OfficeActivated {
    # 1. Provera OSPP.VBS statusa (za standardne KMS/MAK/Retail licence)
    $osppPaths = @(
        "C:\Program Files\Microsoft Office\Office16\OSPP.VBS",
        "C:\Program Files\Microsoft Office\root\Office16\OSPP.VBS",
        "C:\Program Files (x86)\Microsoft Office\Office16\OSPP.VBS",
        "C:\Program Files (x86)\Microsoft Office\root\Office16\OSPP.VBS"
    )
    $ospp = $osppPaths | Where-Object { Test-Path $_ } | Select-Object -First 1
    if ($ospp) {
        try {
            $out = (cscript //nologo $ospp /dstatus 2>$null) -join "`n"
            if ($out -match "LICENSE STATUS:\s+---LICENSED---") {
                return $true
            }
        } catch {}
    }

    # 2. Provera WMI / CIM SoftwareLicensingProduct (za KMS/MAK/Retail licence)
    try {
        $c2rLic = Get-CimInstance -ClassName SoftwareLicensingProduct -Filter "PartialProductKey IS NOT NULL" -ErrorAction SilentlyContinue |
            Where-Object { ($_.Name -like "*Office*" -or $_.Description -like "*Office*") -and $_.LicenseStatus -eq 1 }
        if ($c2rLic) {
            return $true
        }
    } catch {}

    return $false
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

function Test-AllServisProgramsInstalled {
    $isLaptop = Test-BatteryPresent

    $results = @{
        Chrome = @{ Installed = $false; Version = ""; Path = ""; Message = "" }
        WinRAR = @{ Installed = $false; Version = ""; Path = ""; Message = "" }
        Acrobat = @{ Installed = $false; Version = ""; Path = ""; Message = "" }
        Office = @{ Installed = $false; WordVersion = ""; ExcelVersion = ""; Path = ""; Message = "" }
        DotNet = @{ Installed = $false; Version = ""; Message = "" }
        BatteryBar = @{ Installed = $false; IsLaptop = $isLaptop; Required = $false; Path = ""; Message = "" }
        WindowsActivation = @{ Activated = (Test-WindowsActivated) }
        OfficeActivation = @{ Activated = (Test-OfficeActivated) }
        AllRequiredInstalled = $false
        MissingList = [System.Collections.Generic.List[string]]::new()
    }

    # 1. Google Chrome
    $chromePaths = @(
        "C:\Program Files\Google\Chrome\Application\chrome.exe",
        "C:\Program Files (x86)\Google\Chrome\Application\chrome.exe"
    )
    $chPath = $chromePaths | Where-Object { Test-Path $_ } | Select-Object -First 1
    if ($chPath) {
        $results.Chrome.Installed = $true
        $results.Chrome.Path = $chPath
        try { $results.Chrome.Version = (Get-Item $chPath).VersionInfo.ProductVersion } catch { $results.Chrome.Version = "OK" }
        $results.Chrome.Message = "Instaliran (v$($results.Chrome.Version))"
    } else {
        $results.Chrome.Message = "NEDOSTAJE (chrome.exe nije pronadjen)"
        $results.MissingList.Add("Google Chrome")
    }

    # 2. WinRAR x64
    $winrarPaths = @(
        "C:\Program Files\WinRAR\WinRAR.exe",
        "C:\Program Files (x86)\WinRAR\WinRAR.exe"
    )
    $wrPath = $winrarPaths | Where-Object { Test-Path $_ } | Select-Object -First 1
    if ($wrPath) {
        $results.WinRAR.Installed = $true
        $results.WinRAR.Path = $wrPath
        try { $results.WinRAR.Version = (Get-Item $wrPath).VersionInfo.ProductVersion } catch { $results.WinRAR.Version = "OK" }
        $results.WinRAR.Message = "Instaliran (v$($results.WinRAR.Version))"
    } else {
        $results.WinRAR.Message = "NEDOSTAJE (WinRAR.exe nije pronadjen)"
        $results.MissingList.Add("WinRAR x64")
    }

    # 3. Adobe Acrobat Pro 2024
    $acroPaths = @(
        "C:\Program Files\Adobe\Acrobat DC\Acrobat\Acrobat.exe",
        "C:\Program Files (x86)\Adobe\Acrobat DC\Acrobat\Acrobat.exe",
        "C:\Program Files\Adobe\Acrobat 2024\Acrobat\Acrobat.exe",
        "C:\Program Files (x86)\Adobe\Acrobat 2024\Acrobat\Acrobat.exe"
    )
    $acPath = $acroPaths | Where-Object { Test-Path $_ } | Select-Object -First 1
    if ($acPath) {
        $results.Acrobat.Installed = $true
        $results.Acrobat.Path = $acPath
        try { $results.Acrobat.Version = (Get-Item $acPath).VersionInfo.ProductVersion } catch { $results.Acrobat.Version = "OK" }
        $results.Acrobat.Message = "Instaliran (v$($results.Acrobat.Version))"
    } else {
        $results.Acrobat.Message = "NEDOSTAJE (Acrobat.exe nije pronadjen)"
        $results.MissingList.Add("Adobe Acrobat Pro")
    }

    # 4. Microsoft Office
    $wordPaths = @(
        "C:\Program Files\Microsoft Office\root\Office16\WINWORD.EXE",
        "C:\Program Files (x86)\Microsoft Office\root\Office16\WINWORD.EXE"
    )
    $excelPaths = @(
        "C:\Program Files\Microsoft Office\root\Office16\EXCEL.EXE",
        "C:\Program Files (x86)\Microsoft Office\root\Office16\EXCEL.EXE"
    )
    $wPath = $wordPaths | Where-Object { Test-Path $_ } | Select-Object -First 1
    $ePath = $excelPaths | Where-Object { Test-Path $_ } | Select-Object -First 1
    if ($wPath -or $ePath) {
        $results.Office.Installed = $true
        $results.Office.Path = if ($wPath) { $wPath } else { $ePath }
        try { if ($wPath) { $results.Office.WordVersion = (Get-Item $wPath).VersionInfo.ProductVersion } } catch {}
        try { if ($ePath) { $results.Office.ExcelVersion = (Get-Item $ePath).VersionInfo.ProductVersion } } catch {}
        $results.Office.Message = "Instaliran (Word & Excel $(if ($results.Office.WordVersion) { 'v' + $results.Office.WordVersion } else { '' }))"
    } else {
        $results.Office.Message = "NEDOSTAJE (WINWORD.EXE i EXCEL.EXE nisu pronadjeni)"
        $results.MissingList.Add("Microsoft Office")
    }

    # 5. Microsoft .NET Runtimes
    $dotnetDesktopDir = "C:\Program Files\dotnet\shared\Microsoft.WindowsDesktop.App"
    $hasDotNetDesktop = $false
    $dnVersions = @()
    if (Test-Path $dotnetDesktopDir) {
        $dnVersions = Get-ChildItem -Path $dotnetDesktopDir -Directory -ErrorAction SilentlyContinue | ForEach-Object { $_.Name }
        if ($dnVersions.Count -gt 0) { $hasDotNetDesktop = $true }
    }
    if ($hasDotNetDesktop) {
        $results.DotNet.Installed = $true
        $results.DotNet.Version = ($dnVersions -join ", ")
        $results.DotNet.Message = "Instaliran (.NET Desktop: $($results.DotNet.Version))"
    } else {
        $regDn = (Get-ItemProperty "HKLM:\SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedhost" -ErrorAction SilentlyContinue).Version
        if ($regDn) {
            $results.DotNet.Installed = $true
            $results.DotNet.Version = $regDn
            $results.DotNet.Message = "Instaliran (.NET Shared Host: $regDn)"
        } else {
            $results.DotNet.Message = "NEDOSTAJE (.NET Desktop Runtime nije pronadjen)"
            $results.MissingList.Add("Microsoft .NET Runtime")
        }
    }

    # 6. BatteryBar
    if ($results.BatteryBar.IsLaptop) {
        $results.BatteryBar.Required = $true
        $bbPaths = @(
            "C:\Program Files\BatteryBar\BatteryBar.exe",
            "C:\Program Files (x86)\BatteryBar\BatteryBar.exe"
        )
        $bbPath = $bbPaths | Where-Object { Test-Path $_ } | Select-Object -First 1
        if ($bbPath) {
            $results.BatteryBar.Installed = $true
            $results.BatteryBar.Path = $bbPath
            $results.BatteryBar.Message = "Instaliran (Laptop sa baterijom konfigurisan - $bbPath)"
        } else {
            $results.BatteryBar.Message = "NEDOSTAJE (BatteryBar.exe nije pronadjen na laptopu)"
            $results.MissingList.Add("BatteryBar (Laptop)")
        }
    } else {
        $results.BatteryBar.Required = $false
        $results.BatteryBar.Installed = $true
        $results.BatteryBar.Message = "Nije potrebno (Desktop racunar bez baterije)"
    }

    if ($results.MissingList.Count -eq 0) {
        $results.AllRequiredInstalled = $true
    }

    return $results
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
        if ($loc -and (Test-Path $loc)) {
            # Provera da li folder sadrzi programe (Office, Chrome, WinRAR ili bilo koji fajl)
            $hasFiles = (Get-ChildItem -Path $loc -ErrorAction SilentlyContinue | Select-Object -First 1)
            if ($hasFiles) {
                if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                    Write-ServisLog "Get-ServisProgramiRoot: Pronadjen lokalni izvor programa (Prioritet 1 - Full profil): $loc" -Level "SUCCESS" -Component "Apps"
                }
                return $loc
            }
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
    $nasShares = $nasShares | Select-Object -Unique

    $nasUser = "admin"
    $nasPass = ""
    if ($nasIni) {
        if (-not [string]::IsNullOrWhiteSpace($nasIni.Username)) { $nasUser = $nasIni.Username }
        if (-not [string]::IsNullOrWhiteSpace($nasIni.Password) -and $nasIni.Password -ne "promeni_lozinku") { $nasPass = $nasIni.Password }
    }

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
                Write-ServisLog "Get-ServisProgramiRoot: Host $h nije dostupan na portu 445 (SMB)." -Level "DEBUG" -Component "Apps"
            }
            continue
        }

        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
            Write-ServisLog "Get-ServisProgramiRoot: Host $h je dostupan (port 445 otvoren). Proveravam deljene resurse..." -Level "INFO" -Component "Apps"
        }

        foreach ($sh in $nasShares) {
            $uncShare = "\\$h\$sh"

            # 1. Osiguraj SMB autentifikovanu sesiju iz konfiguracije (uz guest fallback)
            if (-not [string]::IsNullOrWhiteSpace($nasPass)) {
                $userArg = if ($nasUser -match "\\") { $nasUser } else { "$h\$nasUser" }
                & net.exe use $uncShare $nasPass "/user:$userArg" /persistent:no >$null 2>&1
                if ($LASTEXITCODE -ne 0) {
                    & net.exe use $uncShare $nasPass "/user:$nasUser" /persistent:no >$null 2>&1
                }
            }
            if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($nasPass)) {
                & net.exe use $uncShare '""' /user:guest /persistent:no >$null 2>&1
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
            if ($uc -and (Test-Path $uc)) {
                $hasUsbItems = (Get-ChildItem -Path $uc -ErrorAction SilentlyContinue | Select-Object -First 1)
                if ($hasUsbItems) {
                    if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                        Write-ServisLog "Get-ServisProgramiRoot: Pronadjen USB izvor programa: $uc" -Level "INFO" -Component "Apps"
                    }
                    return $uc
                }
            }
        }
    }

    # Krajnji fallback
    $fallback = if ($PreferredDir) { $PreferredDir } else { "C:\setup\Programi" }
    if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
        Write-ServisLog "Get-ServisProgramiRoot: Nijedan izvor nije potpuno verifikovan, koristim podrazumevanu putanju: $fallback" -Level "WARN" -Component "Apps"
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
                & robocopy.exe "$PackageSourceDir" "$appCacheDir" /E /MT:16 /R:2 /W:2 /NDL /NFL /NJH /NJS >$null 2>&1
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
            Write-ServisLog "Nisu pronadjeni lokalni/SMB VC++ paketi. Pokusavam Winget fallback..." -Level "DEBUG" -Component "VCRedist"
        }
        try {
            Start-Process -FilePath "winget.exe" -ArgumentList "install --id Microsoft.VCRedist.2015+.x64 --silent --accept-package-agreements --accept-source-agreements" -Wait -WindowStyle Hidden -ErrorAction SilentlyContinue
            Start-Process -FilePath "winget.exe" -ArgumentList "install --id Microsoft.VCRedist.2015+.x86 --silent --accept-package-agreements --accept-source-agreements" -Wait -WindowStyle Hidden -ErrorAction SilentlyContinue
        } catch {}
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
        # 1. HKLM Run registarski kljuc
        $regRun = "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run"
        Set-ItemProperty -Path $regRun -Name "BatteryBar" -Value "`"$InstalledExe`"" -Force -ErrorAction SilentlyContinue

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

        # 5. Distribucija XML-a u sve postojece profile na sistemu
        Get-ChildItem -Path "C:\Users" -Directory -Force -ErrorAction SilentlyContinue | Where-Object {
            $_.Name -notmatch '^(Public|Default User|All Users)$'
        } | ForEach-Object {
            $userBbDir = Join-Path $_.FullName "AppData\Roaming\BatteryBar"
            if (-not (Test-Path $userBbDir)) { New-Item -Path $userBbDir -ItemType Directory -Force -ErrorAction SilentlyContinue | Out-Null }
            Set-Content -Path (Join-Path $userBbDir "BatteryBar.Settings.xml") -Value $bbXmlContent -Encoding UTF8 -Force -ErrorAction SilentlyContinue
            Remove-Item -Path (Join-Path $userBbDir "BatteryBar.ini") -Force -ErrorAction SilentlyContinue
        }

        # 6. Kreiranje Startup precice za sve korisnike (All Users Startup)
        $startupDir = "C:\ProgramData\Microsoft\Windows\Start Menu\Programs\Startup"
        if (-not (Test-Path $startupDir)) { New-Item -Path $startupDir -ItemType Directory -Force -ErrorAction SilentlyContinue | Out-Null }
        $startupLnk = Join-Path $startupDir "BatteryBar.lnk"
        try {
            $wshShell = New-Object -ComObject WScript.Shell
            $shortcut = $wshShell.CreateShortcut($startupLnk)
            $shortcut.TargetPath = $InstalledExe
            $shortcut.WorkingDirectory = Split-Path $InstalledExe -Parent
            $shortcut.Description = "BatteryBar Floating Toolbar"
            $shortcut.Save()
            [System.Runtime.InteropServices.Marshal]::ReleaseComObject($wshShell) | Out-Null
            if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                Write-ServisLog "Kreirana BatteryBar Startup precica: $startupLnk" -Level "SUCCESS" -Component "BatteryBar"
            }
        } catch {
            if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                Write-ServisLog "Neuspelo kreiranje Startup prečice za BatteryBar: $($_.Exception.Message)" -Level "WARN" -Component "BatteryBar"
            }
        }

        # 7. Pokretanje procesa ako nije aktivan (normalno, BEZ -WindowStyle Hidden)
        $existingProc = Get-Process -Name "BatteryBar*" -ErrorAction SilentlyContinue
        if ($existingProc) {
            if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                Write-ServisLog "BatteryBar proces je već aktivan, preskačem pokretanje procesa." -Level "INFO" -Component "BatteryBar"
            }
        } else {
            if (Test-Path $InstalledExe) {
                if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                    Write-ServisLog "Pokrećem BatteryBar proces: $InstalledExe" -Level "STEP" -Component "BatteryBar"
                }
                Start-Process -FilePath $InstalledExe -ErrorAction SilentlyContinue
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
                        Write-ServisLog "Pronadjen BatteryBar folder: $bbDir. Kopiram u Program Files..." -Level "STEP" -Component "BatteryBar"
                    }
                    if (-not (Test-Path $targetDir)) { New-Item -Path $targetDir -ItemType Directory -Force | Out-Null }
                    Copy-Item -Path "$bbDir\*" -Destination $targetDir -Recurse -Force -ErrorAction SilentlyContinue
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
            Wait-MsiMutexAvailable -TimeoutSec 180

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
                        Wait-MsiMutexAvailable -TimeoutSec 120
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
                                Wait-MsiMutexAvailable -TimeoutSec 120
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
                            Wait-MsiMutexAvailable -TimeoutSec 60
                            $proc2 = Start-Process "msiexec.exe" -ArgumentList "/i `"$chromePkg`" /qn /norestart" -WorkingDirectory $chDir -Wait -PassThru -WindowStyle Hidden
                            if ($proc2) { $cCode = $proc2.ExitCode }
                        }
                    } else {
                        Wait-MsiMutexAvailable -TimeoutSec 120
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
                        Wait-MsiMutexAvailable -TimeoutSec 120
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
                $wuFlag = (Get-ItemProperty -Path "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Installer" -Name "ServisWuActive" -ErrorAction SilentlyContinue).ServisWuActive
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
                Invoke-SafeMsi -Arguments "/i `"$vcrtMsi`" /qn /norestart" -WorkingDirectory (Split-Path $vcrtMsi -Parent) -MaxWaitSec 120
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
                    Wait-MsiMutexAvailable -TimeoutSec 60
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
                    Wait-MsiMutexAvailable -TimeoutSec 180
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
            Start-Sleep -Seconds 3
            return $true
        }
        Start-Sleep -Seconds 2
        $elapsed += 2
    }
    return $false
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

    # Opcioni post-install hook iz eksternog skladišta (Programi\CustomScripts\*.ps1) ako postoji
    $customScriptDirs = @()
    if ($ProgramiDir) {
        $customScriptDirs += (Join-Path $ProgramiDir "CustomScripts")
    }
    $customScriptDirs += "C:\setup\Programi\CustomScripts"
    if ($Global:SourceRoot) {
        $customScriptDirs += (Join-Path $Global:SourceRoot "Programi\CustomScripts")
    }
    foreach ($csDir in ($customScriptDirs | Select-Object -Unique)) {
        if ($csDir -and (Test-Path $csDir)) {
            $scripts = Get-ChildItem -Path $csDir -Filter "*.ps1" -File -ErrorAction SilentlyContinue
            foreach ($sc in $scripts) {
                try {
                    Unblock-File -Path $sc.FullName -ErrorAction SilentlyContinue
                    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $sc.FullName -ProgramiDir $ProgramiDir
                } catch {}
            }
            break
        }
    }

    # Očitavanje BIOS OA3x OEM ključa ako sistem još nije aktiviran
    $winActive = Test-WindowsActivated
    if (-not $winActive) {
        try {
            $sls = Get-CimInstance -ClassName SoftwareLicensingService -ErrorAction SilentlyContinue
            if ($sls -and -not [string]::IsNullOrWhiteSpace($sls.OA3xOriginalProductKey)) {
                if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                    Write-ServisLog "Detektovan ugrađeni UEFI/BIOS OA3x OEM ključ. Primenjujem sistemsku licencu..." -Level "INFO" -Component "License"
                }
                & cscript.exe //nologo C:\Windows\System32\slmgr.vbs /ipk $sls.OA3xOriginalProductKey >$null 2>&1
                & cscript.exe //nologo C:\Windows\System32\slmgr.vbs /ato >$null 2>&1
                $winActive = Test-WindowsActivated
            }
        } catch {}
    }

    $hasOffice = (Test-Path "C:\Program Files\Microsoft Office\root\Office16\WINWORD.EXE") -or `
                 (Test-Path "C:\Program Files (x86)\Microsoft Office\root\Office16\WINWORD.EXE") -or `
                 (Test-Path "C:\Program Files\Microsoft Office\root\Office16\EXCEL.EXE") -or `
                 (Test-Path "C:\Program Files (x86)\Microsoft Office\root\Office16\EXCEL.EXE")
    $offActive = if ($hasOffice) { Test-OfficeActivated } else { $true }
    $overallSuccess = $winActive -and $offActive

    if (Get-Command Write-DiagnosticEvent -ErrorAction SilentlyContinue) {
        Write-DiagnosticEvent -Event "LICENSE_CHECK_RESULT" -App "License" -Status $(if ($overallSuccess) { 'SUCCESS' } else { 'WARN' }) -Data @{ WindowsActivated = [bool]$winActive; OfficeInstalled = [bool]$hasOffice; OfficeActivated = [bool]$offActive } -Component "License"
    }
    if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
        Write-ServisLog "Status sistemskih licenci: WindowsActivated=$winActive, OfficeActivated=$offActive" -Level $(if ($overallSuccess) { 'SUCCESS' } else { 'WARN' }) -Component "License"
    }
    if (Get-Command Update-ServisUI -ErrorAction SilentlyContinue) {
        Update-ServisUI -AppId "License" -AppStatus $(if ($overallSuccess) { "OK" } else { "WARN" }) -StatusMessage $(if ($overallSuccess) { "OEM i sistemske licence potvrđene" } else { "Potrebna provera licence" })
    }

    $actLogs = @(
        "Zavrsni Windows status: $(if ($winActive) { '[OK] Licenciran' } else { '[!] Nije aktiviran' })",
        "Zavrsni Office status:  $(if (-not $hasOffice) { '[OK] Nije instaliran' } elseif ($offActive) { '[OK] Licenciran' } else { '[!] Nije aktiviran' })"
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
        Wait-MsiMutexAvailable -TimeoutSec 180
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
            "C:\Program Files\Adobe\Acrobat Pro\Acrobat.exe",
            "C:\Program Files\Adobe\Acrobat 2024\Acrobat\Acrobat.exe",
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
        [string]$ProgramiDir = "C:\setup\Programi"
    )

    $env:SEE_MASK_NOZONECHECKS = '1'
    try { [Environment]::SetEnvironmentVariable("SEE_MASK_NOZONECHECKS", "1", "Process") } catch {}

    if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
        Write-ServisLog "=== POKRETANJE FAZE 4: Korisnicki Programi i Aktivacija ===" -Level "STEP" -Component "Phase4"
    }
    if (Get-Command Send-IpcProgress -ErrorAction SilentlyContinue) {
        Send-IpcProgress -Progress 5 -Phase 4 -PhaseName "Programi" -SubPhase "Inicijalizacija" -StatusMessage "Priprema skladišta programa..."
    }

    if (Get-Command Wait-ServisUICooperativePause -ErrorAction SilentlyContinue) {
        Wait-ServisUICooperativePause -CheckpointName "Faza 4 - Pocetak"
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
        $ensuredDir = Ensure-ProgramiAvailable -Interactive:$false
        if ($ensuredDir -and (Test-Path $ensuredDir)) {
            $activeProgDir = $ensuredDir
        }
    }

    if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
        Write-ServisLog "Aktivni direktorijum sa programima: $activeProgDir" -Level "INFO" -Component "Apps"
    }

    # Rekurzivna deblokada svih instalacionih fajlova ako je skladište lokalno (sprečava Open File - Security Warning dijaloge)
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
        Wait-MsiMutexAvailable -TimeoutSec 60
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

    # 1. WINRAR
    if (Get-Command Wait-ServisUICooperativePause -ErrorAction SilentlyContinue) {
        Wait-ServisUICooperativePause -CheckpointName "Faza 4 - WinRAR"
    }
    if (Get-Command Send-IpcProgress -ErrorAction SilentlyContinue) {
        Send-IpcProgress -Progress 15 -Phase 4 -PhaseName "Programi" -SubPhase "WinRAR" -StatusMessage "Instalacija: WinRAR..."
    }
    if (Get-Command Send-IpcAppUpdate -ErrorAction SilentlyContinue) {
        Send-IpcAppUpdate -AppId "WinRAR" -Status "RUNNING" -Detail "Instalacija u toku..."
    }

    $winrarOk = $false
    if ((Get-Command Test-ServisSkipRequested -ErrorAction SilentlyContinue) -and (Test-ServisSkipRequested)) {
        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
            Write-ServisLog "Instalacija WinRAR preskočena na zahtev servisera." -Level "WARN" -Component "WinRAR"
        }
        $appsStatusMap["WinRAR"] = "WARN"
        if (Get-Command Send-IpcAppUpdate -ErrorAction SilentlyContinue) {
            Send-IpcAppUpdate -AppId "WinRAR" -Status "WARN" -Detail "Preskočeno"
        }
    } else {
        try {
            $winrarInstalled = (Test-Path "C:\Program Files\WinRAR\WinRAR.exe") -or (Test-Path "C:\Program Files (x86)\WinRAR\WinRAR.exe")
            if ($winrarInstalled) {
                $winrarOk = $true
                if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                    Write-ServisLog "WinRAR je već instaliran na sistemu." -Level "SUCCESS" -Component "WinRAR"
                }
            } else {
                $rarCandidates = Get-ChildItem -Path $activeProgDir -Filter "*winrar*.exe" -Recurse -File -ErrorAction SilentlyContinue
                if ($rarCandidates -and $rarCandidates.Count -gt 0) {
                    $rarExe = $rarCandidates[0].FullName
                    Unblock-File -Path $rarExe -ErrorAction SilentlyContinue
                    Remove-Item -Path "$($rarExe):Zone.Identifier" -ErrorAction SilentlyContinue
                    if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                        Write-ServisLog "Instaliram WinRAR iz: $rarExe" -Level "STEP" -Component "WinRAR"
                    }
                    $resWinrar = Invoke-InstallerWithLocalFallback -InstallerPath $rarExe -ArgumentList "/s" -AppName "WinRAR" -WaitSeconds 60
                    if ($resWinrar.Success -or (Test-Path "C:\Program Files\WinRAR\WinRAR.exe") -or (Test-Path "C:\Program Files (x86)\WinRAR\WinRAR.exe")) {
                        $winrarOk = $true
                    }
                } else {
                    if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                        Write-ServisLog "WinRAR lokalni instaler nije pronadjen. Pokusavam Winget fallback..." -Level "DEBUG" -Component "WinRAR"
                    }
                    try {
                        Start-Process -FilePath "winget.exe" -ArgumentList "install --id RARLab.WinRAR --silent --accept-package-agreements --accept-source-agreements" -Wait -WindowStyle Hidden -ErrorAction SilentlyContinue
                    } catch {}
                    if ((Test-Path "C:\Program Files\WinRAR\WinRAR.exe") -or (Test-Path "C:\Program Files (x86)\WinRAR\WinRAR.exe")) {
                        $winrarOk = $true
                    }
                }
            }
        } catch {
            if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                Write-ServisLog "Upozorenje pri instalaciji WinRAR: $($_.Exception.Message)" -Level "WARN" -Component "WinRAR"
            }
            if (Get-Command Send-IpcLog -ErrorAction SilentlyContinue) {
                Send-IpcLog -Message "WinRAR instalacija nije uspela: $($_.Exception.Message), nastavljam dalje..." -Level "WARN" -Phase 4
            }
        }

        try {
            Remove-WinRARDesktopShortcut
        } catch {}

        $appsStatusMap["WinRAR"] = if ($winrarOk) { "OK" } else { "WARN" }
        if (Get-Command Send-IpcAppUpdate -ErrorAction SilentlyContinue) {
            Send-IpcAppUpdate -AppId "WinRAR" -Status $appsStatusMap["WinRAR"] -Detail $(if ($winrarOk) { "Instaliran" } else { "Neuspešno - Nastavak" })
        }
    }

    # 2. BATTERYBAR
    if (Get-Command Wait-ServisUICooperativePause -ErrorAction SilentlyContinue) {
        Wait-ServisUICooperativePause -CheckpointName "Faza 4 - BatteryBar"
    }
    if (Get-Command Send-IpcProgress -ErrorAction SilentlyContinue) {
        Send-IpcProgress -Progress 28 -Phase 4 -PhaseName "Programi" -SubPhase "BatteryBar" -StatusMessage "Instalacija: BatteryBar..."
    }
    if (Get-Command Send-IpcAppUpdate -ErrorAction SilentlyContinue) {
        Send-IpcAppUpdate -AppId "BatteryBar" -Status "RUNNING" -Detail "Provera baterije i instalacija..."
    }

    $batteryOk = $false
    $isLaptop = Test-BatteryPresent

    if (-not $isLaptop) {
        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
            Write-ServisLog "Uređaj nema bateriju niti laptop šasiju (Desktop / All-in-One). BatteryBar nije potreban." -Level "INFO" -Component "BatteryBar"
        }
        $appsStatusMap["BatteryBar"] = "OK"
        if (Get-Command Send-IpcAppUpdate -ErrorAction SilentlyContinue) {
            Send-IpcAppUpdate -AppId "BatteryBar" -Status "OK" -Detail "Preskočeno (Desktop)"
        }
    } else {
        # Detektovan prenosni računar (Laptop / Notebook)
        $targetBbDir = "C:\Program Files\BatteryBar"
        $targetBbExe = Join-Path $targetBbDir "BatteryBar.exe"
        $installedExe = @(
            $targetBbExe,
            "C:\Program Files (x86)\BatteryBar\BatteryBar.exe"
        ) | Where-Object { Test-Path $_ } | Select-Object -First 1

        try {
            if ($installedExe -and (Test-Path $installedExe)) {
                # BatteryBar već postoji na disku - preskačemo bilo kakav instaler ili raspakivanje paketa
                if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                    Write-ServisLog "BatteryBar je već prisutan na disku ($installedExe). Preskačem instaler i raspakivanje; primenjujem konfiguraciju." -Level "SUCCESS" -Component "BatteryBar"
                }
                $batteryConfigOk = Set-BatteryBarConfiguration -InstalledExe $installedExe
                $batteryOk = [bool]$batteryConfigOk
            } else {
                # BatteryBar.exe ne postoji - tražimo isključivo prenosivi paket (nikada ne pokretati instaler ako je exe prisutan)
                $bbFolderCandidates = @(
                    (Join-Path $activeProgDir "BatteryBar"),
                    (Join-Path $LocalSetupDir "Programi\BatteryBar"),
                    "C:\setup\Programi\BatteryBar",
                    "C:\setup\Programi\Programi\BatteryBar",
                    "D:\Programi\BatteryBar",
                    "E:\Programi\BatteryBar"
                )
                if (Get-Command Get-ServisProgramiRoot -ErrorAction SilentlyContinue) {
                    $pRoot = Get-ServisProgramiRoot
                    if ($pRoot) {
                        $bbFolderCandidates += (Join-Path $pRoot "BatteryBar")
                        $bbFolderCandidates += (Join-Path $pRoot "Programi\BatteryBar")
                    }
                }

                $bbFoundDir = $bbFolderCandidates | Where-Object { $_ -and (Test-Path (Join-Path $_ "BatteryBar.exe")) } | Select-Object -First 1

                if (-not $bbFoundDir) {
                    $foundItem = Get-ChildItem -Path $activeProgDir -Filter "BatteryBar.exe" -Recurse -File -ErrorAction SilentlyContinue | Select-Object -First 1
                    if ($foundItem) {
                        $bbFoundDir = $foundItem.DirectoryName
                    }
                }

                if (-not $bbFoundDir -and (Get-Command Sync-ProgramiFromNas -ErrorAction SilentlyContinue)) {
                    if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                        Write-ServisLog "BatteryBar paket nije pronađen u $activeProgDir. Pokrećem preuzimanje sa NAS-a..." -Level "INFO" -Component "BatteryBar"
                    }
                    Sync-ProgramiFromNas -DestinationDir "C:\setup\Programi" | Out-Null
                    $bbFoundDir = $bbFolderCandidates | Where-Object { $_ -and (Test-Path (Join-Path $_ "BatteryBar.exe")) } | Select-Object -First 1
                }

                if ($bbFoundDir -and (Test-Path (Join-Path $bbFoundDir "BatteryBar.exe"))) {
                    if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                        Write-ServisLog "Pronađen BatteryBar prenosivi paket u: $bbFoundDir. Kopiram u $targetBbDir..." -Level "STEP" -Component "BatteryBar"
                    }
                    if (-not (Test-Path $targetBbDir)) {
                        New-Item -Path $targetBbDir -ItemType Directory -Force -ErrorAction SilentlyContinue | Out-Null
                    }
                    Copy-Item -Path (Join-Path $bbFoundDir "*") -Destination $targetBbDir -Recurse -Force -ErrorAction SilentlyContinue

                    # Rekurzivna deblokada i čišćenje Zone.Identifier za BatteryBar
                    Get-ChildItem -Path $targetBbDir -Recurse -File -ErrorAction SilentlyContinue | ForEach-Object {
                        Unblock-File -Path $_.FullName -ErrorAction SilentlyContinue
                        Remove-Item -Path "$($_.FullName):Zone.Identifier" -ErrorAction SilentlyContinue
                    }

                    if (Test-Path $targetBbExe) {
                        $installedExe = $targetBbExe
                        $batteryConfigOk = Set-BatteryBarConfiguration -InstalledExe $installedExe
                        $batteryOk = [bool]$batteryConfigOk
                        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                            Write-ServisLog "BatteryBar prenosivi fajlovi uspešno kopirani u $targetBbDir." -Level "SUCCESS" -Component "BatteryBar"
                        }
                    } else {
                        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                            Write-ServisLog "BatteryBar prenosivi paket nije pronađen ili kopiranje nije uspelo, preskačem..." -Level "WARN" -Component "BatteryBar"
                        }
                    }
                } else {
                    if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                        Write-ServisLog "BatteryBar prenosivi paket nije pronađen, preskačem..." -Level "WARN" -Component "BatteryBar"
                    }
                    if (Get-Command Send-IpcAppUpdate -ErrorAction SilentlyContinue) {
                        Send-IpcAppUpdate -AppId "BatteryBar" -Status "WARN" -Detail "Preskočeno - paket nije pronađen"
                    }
                }
            }
        } catch {
            if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                Write-ServisLog "Upozorenje pri obradi BatteryBar: $($_.Exception.Message), preskačem bez zaustavljanja sistema." -Level "WARN" -Component "BatteryBar"
            }
            if (Get-Command Send-IpcLog -ErrorAction SilentlyContinue) {
                Send-IpcLog -Message "BatteryBar upozorenje: $($_.Exception.Message), nastavljam dalje..." -Level "WARN" -Phase 4
            }
            $batteryOk = $false
        }

        # Fizička provera - ako postoji na disku, označi status OK
        $bbPhysical = (Test-Path "C:\Program Files\BatteryBar\BatteryBar.exe") -or (Test-Path "C:\Program Files (x86)\BatteryBar\BatteryBar.exe")
        if ($bbPhysical) {
            $appsStatusMap["BatteryBar"] = "OK"
            if (Get-Command Send-IpcAppUpdate -ErrorAction SilentlyContinue) {
                Send-IpcAppUpdate -AppId "BatteryBar" -Status "OK" -Detail "Podešen BatteryBar"
            }
        } else {
            $appsStatusMap["BatteryBar"] = "WARN"
            if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                Write-ServisLog "BatteryBar prenosivi paket nije pronađen ili BatteryBar.exe nedostaje, nastavljam dalje (status WARN)..." -Level "WARN" -Component "BatteryBar"
            }
            if (Get-Command Send-IpcAppUpdate -ErrorAction SilentlyContinue) {
                Send-IpcAppUpdate -AppId "BatteryBar" -Status "WARN" -Detail "Preskočeno - paket nije pronađen"
            }
        }
    }

    # 3. MICROSOFT OFFICE 2024
    if (Get-Command Wait-ServisUICooperativePause -ErrorAction SilentlyContinue) {
        Wait-ServisUICooperativePause -CheckpointName "Faza 4 - Office 2024"
    }
    if (Get-Command Send-IpcProgress -ErrorAction SilentlyContinue) {
        Send-IpcProgress -Progress 45 -Phase 4 -PhaseName "Programi" -SubPhase "Office 2024" -StatusMessage "Instalacija: Microsoft Office 2024..."
    }
    if (Get-Command Send-IpcAppUpdate -ErrorAction SilentlyContinue) {
        Send-IpcAppUpdate -AppId "Office" -Status "RUNNING" -Detail "Instalacija Office 2024 paketa..."
    }

    $officeOk = $false
    if ((Get-Command Test-ServisSkipRequested -ErrorAction SilentlyContinue) -and (Test-ServisSkipRequested)) {
        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
            Write-ServisLog "Instalacija Office 2024 preskočena na zahtev servisera." -Level "WARN" -Component "Office"
        }
        $appsStatusMap["Office"] = "WARN"
        if (Get-Command Send-IpcAppUpdate -ErrorAction SilentlyContinue) {
            Send-IpcAppUpdate -AppId "Office" -Status "WARN" -Detail "Preskočeno"
        }
    } else {
        try {
            $officeInstalled = (Test-Path "C:\Program Files\Microsoft Office\root\Office16\WINWORD.EXE") -or
                               (Test-Path "C:\Program Files (x86)\Microsoft Office\root\Office16\WINWORD.EXE") -or
                               (Test-Path "C:\Program Files\Microsoft Office\Office16\WINWORD.EXE")
            $officeDetail = "Neuspešno - Nastavak"
            if ($officeInstalled) {
                $officeOk = $true
                $officeDetail = "Instaliran Office 2024"
                if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                    Write-ServisLog "Microsoft Office je već instaliran na sistemu." -Level "SUCCESS" -Component "Office"
                }
            } else {
                # 1) Pretraga kandidata za Office folder (Lokalno u $activeProgDir\Office 2024 ili C:\setup\Programi\Office 2024)
                $officeCandidates = @(
                    (Join-Path $activeProgDir "Office 2024\files"),
                    (Join-Path $activeProgDir "Office 2024"),
                    (Join-Path $activeProgDir "Programi\Office 2024\files"),
                    (Join-Path $activeProgDir "Programi\Office 2024"),
                    "C:\setup\Programi\Office 2024\files",
                    "C:\setup\Programi\Office 2024",
                    "C:\setup\Programi\Programi\Office 2024\files",
                    "C:\setup\Programi\Programi\Office 2024"
                )

                # 2) Pretraga na svim priključenim diskovima osim C:\ (Programi\Office 2024 ili oem\Programi\Office 2024)
                $removableDrives = Get-PSDrive -PSProvider FileSystem -ErrorAction SilentlyContinue | Where-Object { $_.Root -ne 'C:\' -and $_.Root -ne 'c:\' }
                foreach ($drv in $removableDrives) {
                    $dRoot = $drv.Root.TrimEnd('\')
                    $officeCandidates += (Join-Path $dRoot "Programi\Office 2024\files")
                    $officeCandidates += (Join-Path $dRoot "Programi\Office 2024")
                    $officeCandidates += (Join-Path $dRoot "oem\Programi\Office 2024\files")
                    $officeCandidates += (Join-Path $dRoot "oem\Programi\Office 2024")
                    $officeCandidates += (Join-Path $dRoot "Office 2024\files")
                    $officeCandidates += (Join-Path $dRoot "Office 2024")
                }
                if ($Global:SourceRoot) {
                    $officeCandidates += (Join-Path $Global:SourceRoot "Programi\Office 2024\files")
                    $officeCandidates += (Join-Path $Global:SourceRoot "Programi\Office 2024")
                    $officeCandidates += (Join-Path $Global:SourceRoot "oem\Programi\Office 2024\files")
                    $officeCandidates += (Join-Path $Global:SourceRoot "oem\Programi\Office 2024")
                }

                $officeFilesDir = $officeCandidates | Where-Object { $_ -and (Test-Path (Join-Path $_ "setup.exe")) } | Select-Object -First 1

                # 3) Ako paket nije pronađen, automatski pozovi Sync-ProgramiFromNas -DestinationDir "C:\setup\Programi" pretražujući Programi deljenje na 192.168.170.254
                if (-not $officeFilesDir) {
                    if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                        Write-ServisLog "Lokalni Office 2024 paketi nisu pronađeni na SSD/USB diskovima. Pokušavam NAS preuzimanje (Programi na 192.168.170.254)..." -Level "WARN" -Component "Office"
                    }
                    if (Get-Command Sync-ProgramiFromNas -ErrorAction SilentlyContinue) {
                        Sync-ProgramiFromNas -DestinationDir "C:\setup\Programi" | Out-Null
                        $officeFilesDir = $officeCandidates | Where-Object { $_ -and (Test-Path (Join-Path $_ "setup.exe")) } | Select-Object -First 1
                    }

                    # Direktni fallback koristi zajednicku detekciju i autentifikaciju.
                    if (-not $officeFilesDir) {
                        $nasProgramiRoot = Get-ServisProgramiRoot -LocalSetupDir $LocalSetupDir
                        $uncShares = @(
                            "\\192.168.170.254\Programi",
                            "\\truenas.servis\Programi",
                            "\\192.168.170.254\WIS\Programi",
                            "\\truenas.servis\WIS\Programi",
                            "\\192.168.170.254\Servis\Programi",
                            "\\truenas.servis\Servis\Programi"
                        )
                        if ($nasProgramiRoot -and $nasProgramiRoot.StartsWith('\\')) {
                            $uncShares = @($nasProgramiRoot) + $uncShares
                        }

                        foreach ($programRoot in ($uncShares | Select-Object -Unique)) {
                            $uncCandidates = @(
                                (Join-Path $programRoot "Office 2024\files"),
                                (Join-Path $programRoot "Office 2024")
                            )
                            $foundUnc = $uncCandidates | Where-Object { $_ -and (Test-Path (Join-Path $_ "setup.exe")) } | Select-Object -First 1
                            if ($foundUnc) {
                                $officeFilesDir = $foundUnc
                                if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                                    Write-ServisLog "Pronađen Office 2024 na direktnom NAS deljenju: $officeFilesDir" -Level "INFO" -Component "Office"
                                }
                                break
                            }
                        }
                    }
                }

                # b) Ako je $officeFilesDir pronađen:
                if ($officeFilesDir) {
                    # Detektovati direktorijum koji sadrzi Office\Data ($officeSourcePath)
                    $sourceCandidates = @(
                        $officeFilesDir,
                        (Split-Path $officeFilesDir -Parent),
                        $activeProgDir,
                        "C:\setup\Programi\Office 2024",
                        "C:\setup\Programi\Office 2024\files",
                        "C:\setup\Programi"
                    )
                    $officeSourcePath = $sourceCandidates | Where-Object { 
                        $_ -and (Test-Path (Join-Path $_ "Office\Data")) 
                    } | Select-Object -First 1

                    if (-not $officeSourcePath) {
                        $officeSourcePath = $officeFilesDir
                    }

                    # Detektovati offline verziju iz $officeSourcePath\Office\Data (pretražiti 16.0.*, default 16.0.14334.20848)
                    $officeVersion = "16.0.14334.20848"
                    $officeDataDir = Join-Path $officeSourcePath "Office\Data"
                    if (Test-Path $officeDataDir) {
                        $verDir = Get-ChildItem -Path $officeDataDir -Directory -Filter "16.0.*" -ErrorAction SilentlyContinue | Select-Object -First 1
                        if ($verDir) {
                            $officeVersion = $verDir.Name
                        } else {
                            $verCab = Get-ChildItem -Path $officeDataDir -File -Filter "v64_16.0.*.cab" -ErrorAction SilentlyContinue | Select-Object -First 1
                            if ($verCab -and ($verCab.Name -match 'v64_(16\.0\.[0-9]+(?:\.[0-9]+)?)\.cab')) {
                                $officeVersion = $matches[1]
                            }
                        }
                    }

                    $odtSetup = Join-Path $officeFilesDir "setup.exe"
                    $odtConfig = Join-Path $officeFilesDir "Configure.xml"

                    if (-not (Test-Path $odtConfig)) {
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
                        try {
                            Set-Content -Path $odtConfig -Value $cleanConfig -Encoding UTF8 -Force -ErrorAction Stop
                        } catch {
                            $odtConfig = "C:\setup\Configure.xml"
                            Set-Content -Path $odtConfig -Value $cleanConfig -Encoding UTF8 -Force
                        }
                    }

                    $effectiveOdtSetup = $odtSetup
                    $effectiveOdtConfig = $odtConfig

                    # Ako je ODT setup na mreži, keširamo ga lokalno pre pokretanja radi garancije 0 sigurnosnih dijaloga
                    if ($odtSetup -like "\\*") {
                        $odtCacheDir = if (Test-Path "C:\setup") { "C:\setup\Cache\Office" } else { Join-Path $env:TEMP "WIS\Cache\Office" }
                        if (-not (Test-Path $odtCacheDir)) { New-Item -ItemType Directory -Path $odtCacheDir -Force -ErrorAction SilentlyContinue | Out-Null }
                        $cachedOdtSetup = Join-Path $odtCacheDir "setup.exe"
                        try {
                            Copy-Item -Path $odtSetup -Destination $cachedOdtSetup -Force -ErrorAction SilentlyContinue
                            if (Test-Path $cachedOdtSetup) {
                                $effectiveOdtSetup = $cachedOdtSetup
                            }
                        } catch {}
                    }

                    Unblock-File -Path $effectiveOdtSetup -ErrorAction SilentlyContinue
                    Remove-Item -Path "$($effectiveOdtSetup):Zone.Identifier" -ErrorAction SilentlyContinue
                    Unblock-File -Path $effectiveOdtConfig -ErrorAction SilentlyContinue
                    Remove-Item -Path "$($effectiveOdtConfig):Zone.Identifier" -ErrorAction SilentlyContinue

                    # 4) Provera postojanja setup.exe i Configure.xml pre pokretanja tihe instalacije
                    if ((Test-Path $effectiveOdtSetup) -and (Test-Path $effectiveOdtConfig)) {
                        # Zaustaviti eventualno zaglavljene ODT/ClickToRun procese
                        @("setup", "officeclicktorun", "officec2rclient", "appvshnotify") | ForEach-Object {
                            Get-Process -Name $_ -ErrorAction SilentlyContinue | Where-Object { 
                                $_.Path -like "*Office*" -or $_.Path -like "*setup*" 
                            } | Stop-Process -Force -ErrorAction SilentlyContinue
                        }

                        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                            Write-ServisLog "Pokrecem 100% offline Office instaler: $effectiveOdtSetup /configure `"$effectiveOdtConfig`" (Verzija: $officeVersion, Izvor: $officeSourcePath)" -Level "STEP" -Component "Office"
                        }

                        # Pokrenuti ODT
                        $odtWorkDir = Split-Path $effectiveOdtSetup -Parent
                        $p = Start-Process -FilePath $effectiveOdtSetup -ArgumentList "/configure `"$effectiveOdtConfig`"" -WorkingDirectory $odtWorkDir -PassThru -WindowStyle Hidden

                    # Pratiti proces i cekati do 300 sekundi dok proces ne zavrsi uz IPC telemetriju
                    $odtMaxWait = 300
                    $elapsed = 0
                    while ($elapsed -lt $odtMaxWait) {
                        if ($p.HasExited) {
                            break
                        }

                        $earlyWord = (Test-Path "C:\Program Files\Microsoft Office\root\Office16\WINWORD.EXE") -or
                                     (Test-Path "C:\Program Files (x86)\Microsoft Office\root\Office16\WINWORD.EXE") -or
                                     (Test-Path "C:\Program Files\Microsoft Office\Office16\WINWORD.EXE")
                        $earlyExcel = (Test-Path "C:\Program Files\Microsoft Office\root\Office16\EXCEL.EXE") -or
                                      (Test-Path "C:\Program Files (x86)\Microsoft Office\root\Office16\EXCEL.EXE") -or
                                      (Test-Path "C:\Program Files\Microsoft Office\Office16\EXCEL.EXE")
                        if ($earlyWord -and $earlyExcel -and ($elapsed -ge 60)) {
                            $c2rProc = Get-Process -Name "OfficeClickToRun" -ErrorAction SilentlyContinue
                            if (-not $c2rProc) {
                                break
                            }
                        }

                        Start-Sleep -Seconds 5
                        $elapsed += 5

                        if ($elapsed % 15 -eq 0) {
                            $progVal = [Math]::Min(55, 45 + [int]($elapsed / 30))
                            if (Get-Command Send-IpcProgress -ErrorAction SilentlyContinue) {
                                Send-IpcProgress -Progress $progVal -Phase 4 -PhaseName "Programi" -SubPhase "Office 2024" -StatusMessage "Instalacija: Office 2024 ($elapsed s)..."
                            }
                            if (Get-Command Send-IpcAppUpdate -ErrorAction SilentlyContinue) {
                                Send-IpcAppUpdate -AppId "Office" -Status "RUNNING" -Detail "Instalacija u toku ($elapsed s)..."
                            }
                        }
                    }

                    if (-not $p.HasExited) {
                        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                            Write-ServisLog "Office setup proces ($($p.Id)) prekoračio 300s. Proveravam status servisa..." -Level "WARN" -Component "Office"
                        }
                    } else {
                        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                            Write-ServisLog "Office setup proces završen sa izlaznim kodom: $($p.ExitCode) nakon $elapsed s." -Level "INFO" -Component "Office"
                        }
                    }

                    # U petlji do 60 sekundi proveravati WINWORD.EXE i registraciju u HKLM:\SOFTWARE\Microsoft\Office\ClickToRun
                    $c2rWait = 0
                    while ($c2rWait -lt 60) {
                        $hasWord = (Test-Path "C:\Program Files\Microsoft Office\root\Office16\WINWORD.EXE") -or
                                   (Test-Path "C:\Program Files (x86)\Microsoft Office\root\Office16\WINWORD.EXE") -or
                                   (Test-Path "C:\Program Files\Microsoft Office\Office16\WINWORD.EXE")
                        $hasExcel = (Test-Path "C:\Program Files\Microsoft Office\root\Office16\EXCEL.EXE") -or
                                    (Test-Path "C:\Program Files (x86)\Microsoft Office\root\Office16\EXCEL.EXE") -or
                                    (Test-Path "C:\Program Files\Microsoft Office\Office16\EXCEL.EXE")

                        if ($hasWord -and $hasExcel) {
                            $officeOk = $true
                            break
                        }
                        Start-Sleep -Seconds 3
                        $c2rWait += 3
                    }

                    # Proveriti prisustvo WINWORD.EXE i EXCEL.EXE
                    if ($officeOk) {
                        $officeDetail = "Instaliran Office 2024"
                        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                            Write-ServisLog "Microsoft Office 2024 uspešno verifikovan na disku (WINWORD.EXE i EXCEL.EXE pronađeni)." -Level "SUCCESS" -Component "Office"
                        }
                    } else {
                        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                            Write-ServisLog "Office instaler je završio, ali WINWORD.EXE ili EXCEL.EXE nisu pronađeni." -Level "WARN" -Component "Office"
                        }
                    }

                    # 5) HIBRIDNI RETRY & LOCAL FALLBACK ZA OFFICE 2024
                    # Ako direktno pokretanje sa SMB-a nije potvrdilo instalaciju, preuzimamo paket u C:\setup\temp\Office2024
                    if (-not $officeOk -and ($officeFilesDir -like "\\*")) {
                        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                            Write-ServisLog "Direktno SMB pokretanje za Office 2024 nije potvrdilo instalaciju. Pokrećem Retry & Local Fallback (preuzimanje u privremeni folder)..." -Level "WARN" -Component "Office"
                        }
                        if (Get-Command Send-IpcProgress -ErrorAction SilentlyContinue) {
                            Send-IpcProgress -Progress 50 -Phase 4 -PhaseName "Programi" -SubPhase "Office 2024 (Fallback)" -StatusMessage "Preuzimanje Office paketa u C:\setup\temp (Local Fallback)..."
                        }

                        $tempOfficeDir = "C:\setup\temp\Office2024"
                        try {
                            if (Test-Path $tempOfficeDir) { Remove-Item -Path $tempOfficeDir -Recurse -Force -ErrorAction SilentlyContinue }
                            New-Item -ItemType Directory -Path $tempOfficeDir -Force -ErrorAction SilentlyContinue | Out-Null

                            $srcCopyDir = if ($officeSourcePath -and (Test-Path (Join-Path $officeSourcePath "Office\Data"))) { $officeSourcePath } else { $officeFilesDir }
                            if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                                Write-ServisLog "Kopiram Office fajlove iz: $srcCopyDir u: $tempOfficeDir" -Level "STEP" -Component "Office"
                            }

                            & robocopy.exe "$srcCopyDir" "$tempOfficeDir" /E /R:2 /W:3 /NP /NDL /NFL /NJH /NJS | Out-Null

                            $localOdtSetup = Join-Path $tempOfficeDir "setup.exe"
                            if (-not (Test-Path $localOdtSetup)) {
                                $setupInFiles = Join-Path $tempOfficeDir "files\setup.exe"
                                if (Test-Path $setupInFiles) { $localOdtSetup = $setupInFiles }
                            }

                            $localOdtConfig = "C:\setup\Configure.xml"
                            $localConfigContent = @"
<Configuration>
  <Add OfficeClientEdition="64" Channel="PerpetualVL2021" Version="$officeVersion" SourcePath="$tempOfficeDir">
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
                            Set-Content -Path $localOdtConfig -Value $localConfigContent -Encoding UTF8 -Force
                            Unblock-File -Path $localOdtSetup -ErrorAction SilentlyContinue
                            Remove-Item -Path "$($localOdtSetup):Zone.Identifier" -ErrorAction SilentlyContinue
                            Unblock-File -Path $localOdtConfig -ErrorAction SilentlyContinue
                            Remove-Item -Path "$($localOdtConfig):Zone.Identifier" -ErrorAction SilentlyContinue

                            if (Test-Path $localOdtSetup) {
                                if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                                    Write-ServisLog "Pokrećem lokalni ODT fallback: $localOdtSetup /configure `"$localOdtConfig`"" -Level "STEP" -Component "Office"
                                }
                                $pFallback = Start-Process -FilePath $localOdtSetup -ArgumentList "/configure `"$localOdtConfig`"" -WorkingDirectory (Split-Path $localOdtSetup -Parent) -PassThru -WindowStyle Hidden
                                $fElapsed = 0
                                while ($fElapsed -lt 300) {
                                    if ($pFallback.HasExited) { break }
                                    $hasW = (Test-Path "C:\Program Files\Microsoft Office\root\Office16\WINWORD.EXE") -or (Test-Path "C:\Program Files (x86)\Microsoft Office\root\Office16\WINWORD.EXE")
                                    $hasE = (Test-Path "C:\Program Files\Microsoft Office\root\Office16\EXCEL.EXE") -or (Test-Path "C:\Program Files (x86)\Microsoft Office\root\Office16\EXCEL.EXE")
                                    if ($hasW -and $hasE -and ($fElapsed -ge 60)) { break }
                                    Start-Sleep -Seconds 5
                                    $fElapsed += 5
                                }

                                $hasWordFallback = (Test-Path "C:\Program Files\Microsoft Office\root\Office16\WINWORD.EXE") -or (Test-Path "C:\Program Files (x86)\Microsoft Office\root\Office16\WINWORD.EXE")
                                $hasExcelFallback = (Test-Path "C:\Program Files\Microsoft Office\root\Office16\EXCEL.EXE") -or (Test-Path "C:\Program Files (x86)\Microsoft Office\root\Office16\EXCEL.EXE")
                                if ($hasWordFallback -and $hasExcelFallback) {
                                    $officeOk = $true
                                    $officeDetail = "Instaliran Office 2024 (Local Fallback)"
                                    if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                                        Write-ServisLog "Microsoft Office 2024 uspešno instaliran kroz lokalni fallback." -Level "SUCCESS" -Component "Office"
                                    }
                                }
                            }
                        } catch {
                            if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                                Write-ServisLog "Upozorenje pri lokalnom Office fallback-u: $($_.Exception.Message)" -Level "WARN" -Component "Office"
                            }
                        } finally {
                            # Obavezno ciscenje privremenog foldera
                            if (Test-Path $tempOfficeDir) {
                                Remove-Item -Path $tempOfficeDir -Recurse -Force -ErrorAction SilentlyContinue
                            }
                        }
                    }
                } else {
                    if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                        Write-ServisLog "Office 2024 instaler ili Configure.xml nedostaje ($odtSetup / $odtConfig)." -Level "WARN" -Component "Office"
                    }
                }
            } else {
                # c) Ako $officeFilesDir nije pronađen:
                    $officeDetail = "Nedostaju instalacioni paketi"
                    if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                        Write-ServisLog "Nedostaju instalacioni paketi: Office 2024 setup.exe nije pronađen ni na lokalnom disku ni na NAS resursu." -Level "ERROR" -Component "Office"
                    }
                    if (Get-Command Send-IpcLog -ErrorAction SilentlyContinue) {
                        Send-IpcLog -Message "Office 2024 paketi nisu pronađeni (ni lokalno ni na NAS-u)." -Level "ERROR" -Phase 4
                    }
                    if (Get-Command Send-IpcAppUpdate -ErrorAction SilentlyContinue) {
                        Send-IpcAppUpdate -AppId "Office" -Status "WARN" -Detail "Nedostaju instalacioni paketi"
                    }
                }
            }
        } catch {
            if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                Write-ServisLog "Upozorenje pri instalaciji Office: $($_.Exception.Message)" -Level "WARN" -Component "Office"
            }
            if (Get-Command Send-IpcLog -ErrorAction SilentlyContinue) {
                Send-IpcLog -Message "Greška pri instalaciji Office: $($_.Exception.Message), nastavljam dalje..." -Level "WARN" -Phase 4
            }
        }
        $appsStatusMap["Office"] = if ($officeOk) { "OK" } else { "WARN" }
        if (Get-Command Send-IpcAppUpdate -ErrorAction SilentlyContinue) {
            Send-IpcAppUpdate -AppId "Office" -Status $appsStatusMap["Office"] -Detail $officeDetail
        }
    }

    # 4. GOOGLE CHROME
    if (Get-Command Wait-ServisUICooperativePause -ErrorAction SilentlyContinue) {
        Wait-ServisUICooperativePause -CheckpointName "Faza 4 - Google Chrome"
    }
    if (Get-Command Send-IpcProgress -ErrorAction SilentlyContinue) {
        Send-IpcProgress -Progress 62 -Phase 4 -PhaseName "Programi" -SubPhase "Google Chrome" -StatusMessage "Instalacija: Google Chrome..."
    }
    if (Get-Command Send-IpcAppUpdate -ErrorAction SilentlyContinue) {
        Send-IpcAppUpdate -AppId "Chrome" -Status "RUNNING" -Detail "Instalacija Chrome pretraživača..."
    }

    $chromeOk = $false
    if ((Get-Command Test-ServisSkipRequested -ErrorAction SilentlyContinue) -and (Test-ServisSkipRequested)) {
        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
            Write-ServisLog "Instalacija Google Chrome preskočena na zahtev servisera." -Level "WARN" -Component "Chrome"
        }
        $appsStatusMap["Chrome"] = "WARN"
        if (Get-Command Send-IpcAppUpdate -ErrorAction SilentlyContinue) {
            Send-IpcAppUpdate -AppId "Chrome" -Status "WARN" -Detail "Preskočeno"
        }
    } else {
        try {
            $chromeInstalled = (Test-Path "C:\Program Files\Google\Chrome\Application\chrome.exe") -or
                                (Test-Path "C:\Program Files (x86)\Google\Chrome\Application\chrome.exe")
            if ($chromeInstalled) {
                $chromeOk = $true
                if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                    Write-ServisLog "Google Chrome je već instaliran." -Level "SUCCESS" -Component "Chrome"
                }
            } else {
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

                if ($msiPath) {
                    Unblock-File -Path $msiPath -ErrorAction SilentlyContinue
                    Remove-Item -Path "$($msiPath):Zone.Identifier" -ErrorAction SilentlyContinue
                    if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                        Write-ServisLog "Instaliram Chrome preko Enterprise MSI: $msiPath" -Level "STEP" -Component "Chrome"
                    }
                    if (Get-Command Wait-MsiMutexAvailable -ErrorAction SilentlyContinue) {
                        Wait-MsiMutexAvailable -TimeoutSec 180
                    }
                    $chDir = Split-Path $msiPath -Parent
                    $msiProc = Start-Process -FilePath "msiexec.exe" -ArgumentList "/i `"$msiPath`" /qn /norestart ALLUSERS=1" -WorkingDirectory $chDir -Wait -PassThru -WindowStyle Hidden
                    $mCode = if ($msiProc) { $msiProc.ExitCode } else { -1 }
                    if ($mCode -eq 0 -or $mCode -eq 3010 -or
                        (Test-Path "C:\Program Files\Google\Chrome\Application\chrome.exe") -or
                        (Test-Path "C:\Program Files (x86)\Google\Chrome\Application\chrome.exe")) {
                        $chromeOk = $true
                        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                            Write-ServisLog "Chrome Enterprise MSI uspešno instaliran (ExitCode: $mCode)." -Level "SUCCESS" -Component "Chrome"
                        }
                    } else {
                        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                            Write-ServisLog "Chrome MSI vratio kod $mCode. Pokrećem fallback..." -Level "WARN" -Component "Chrome"
                        }
                    }
                }

                # 2. Fallback na ChromeSetup.exe / *chrome*.exe ili Winget ukoliko MSI nije instalirao
                if (-not $chromeOk) {
                    $chromeCandidates = Get-ChildItem -Path $activeProgDir -Filter "*chrome*.exe" -Recurse -File -ErrorAction SilentlyContinue | Where-Object { $_.Name -match '(?i)chrome.*(standalone|setup|install)' -or $_.Name -eq 'ChromeStandaloneSetup64.exe' }
                    if ($chromeCandidates -and $chromeCandidates.Count -gt 0) {
                        $cExe = $chromeCandidates[0].FullName
                        Unblock-File -Path $cExe -ErrorAction SilentlyContinue
                        Remove-Item -Path "$($cExe):Zone.Identifier" -ErrorAction SilentlyContinue
                        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                            Write-ServisLog "Instaliram Chrome iz: $cExe" -Level "STEP" -Component "Chrome"
                        }
                        $resChrome = Invoke-InstallerWithLocalFallback -InstallerPath $cExe -ArgumentList "/silent /install" -AppName "Chrome" -WaitSeconds 120
                        if ($resChrome.Success -or (Test-Path "C:\Program Files\Google\Chrome\Application\chrome.exe") -or (Test-Path "C:\Program Files (x86)\Google\Chrome\Application\chrome.exe")) {
                            $chromeOk = $true
                        }
                    } else {
                        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                            Write-ServisLog "Chrome lokalni instaler nije pronadjen. Pokusavam Winget..." -Level "DEBUG" -Component "Chrome"
                        }
                        try {
                            Start-Process -FilePath "winget.exe" -ArgumentList "install --id Google.Chrome --silent --accept-package-agreements --accept-source-agreements" -Wait -WindowStyle Hidden -ErrorAction SilentlyContinue
                        } catch {}
                        if ((Test-Path "C:\Program Files\Google\Chrome\Application\chrome.exe") -or (Test-Path "C:\Program Files (x86)\Google\Chrome\Application\chrome.exe")) {
                            $chromeOk = $true
                        }
                    }
                }
            }
        } catch {
            if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                Write-ServisLog "Upozorenje pri instalaciji Chrome: $($_.Exception.Message)" -Level "WARN" -Component "Chrome"
            }
            if (Get-Command Send-IpcLog -ErrorAction SilentlyContinue) {
                Send-IpcLog -Message "Greška pri instalaciji Chrome: $($_.Exception.Message), nastavljam dalje..." -Level "WARN" -Phase 4
            }
        }
        $appsStatusMap["Chrome"] = if ($chromeOk) { "OK" } else { "WARN" }
        if (Get-Command Send-IpcAppUpdate -ErrorAction SilentlyContinue) {
            Send-IpcAppUpdate -AppId "Chrome" -Status $appsStatusMap["Chrome"] -Detail $(if ($chromeOk) { "Instaliran" } else { "Neuspešno - Nastavak" })
        }
    }

    # 5. ADOBE ACROBAT PRO
    if (Get-Command Wait-ServisUICooperativePause -ErrorAction SilentlyContinue) {
        Wait-ServisUICooperativePause -CheckpointName "Faza 4 - Adobe Acrobat Pro"
    }
    if (Get-Command Send-IpcProgress -ErrorAction SilentlyContinue) {
        Send-IpcProgress -Progress 75 -Phase 4 -PhaseName "Programi" -SubPhase "Adobe Acrobat Pro" -StatusMessage "Instalacija: Adobe Acrobat Pro..."
    }
    if (Get-Command Send-IpcAppUpdate -ErrorAction SilentlyContinue) {
        Send-IpcAppUpdate -AppId "Acrobat" -Status "RUNNING" -Detail "Instalacija Adobe Acrobat Pro..."
    }

    $acrobatOk = $false
    if ((Get-Command Test-ServisSkipRequested -ErrorAction SilentlyContinue) -and (Test-ServisSkipRequested)) {
        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
            Write-ServisLog "Instalacija Adobe Acrobat preskočena na zahtev servisera." -Level "WARN" -Component "Acrobat"
        }
        $appsStatusMap["Acrobat"] = "WARN"
        if (Get-Command Send-IpcAppUpdate -ErrorAction SilentlyContinue) {
            Send-IpcAppUpdate -AppId "Acrobat" -Status "WARN" -Detail "Preskočeno"
        }
    } else {
        try {
            $acrobatInstalled = (Test-Path "C:\Program Files\Adobe\Acrobat DC\Acrobat\Acrobat.exe") -or
                                (Test-Path "C:\Program Files (x86)\Adobe\Acrobat DC\Acrobat\Acrobat.exe") -or
                                (Test-Path "C:\Program Files\Adobe\Acrobat Pro\Acrobat.exe")
            if ($acrobatInstalled) {
                $acrobatOk = $true
                if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                    Write-ServisLog "Adobe Acrobat je već instaliran." -Level "SUCCESS" -Component "Acrobat"
                }
            } else {
                # Pretraga za Setup.exe unutar podfoldera Adobe.Acrobat.Pro.2024.x64.Multilingual (posebno 'Adobe Acrobat Pro\Setup.exe' ili 'Setup.exe')
                $acroSetupCandidates = @(
                    (Join-Path $activeProgDir "Adobe.Acrobat.Pro.2024.x64.Multilingual\Adobe Acrobat Pro\Setup.exe"),
                    (Join-Path $activeProgDir "Adobe.Acrobat.Pro.2024.x64.Multilingual\Setup.exe"),
                    (Join-Path $activeProgDir "Acrobat Pro 2024\Adobe.Acrobat.Pro.2024.x64.Multilingual\Adobe Acrobat Pro\Setup.exe"),
                    (Join-Path $activeProgDir "Adobe Acrobat Pro\Setup.exe"),
                    "C:\setup\Programi\Adobe.Acrobat.Pro.2024.x64.Multilingual\Adobe Acrobat Pro\Setup.exe",
                    "C:\setup\Programi\Programi\Adobe.Acrobat.Pro.2024.x64.Multilingual\Adobe Acrobat Pro\Setup.exe"
                )
                if ($Global:SourceRoot) {
                    $acroSetupCandidates += (Join-Path $Global:SourceRoot "Programi\Adobe.Acrobat.Pro.2024.x64.Multilingual\Adobe Acrobat Pro\Setup.exe")
                    $acroSetupCandidates += (Join-Path $Global:SourceRoot "Programi\Adobe.Acrobat.Pro.2024.x64.Multilingual\Setup.exe")
                }
                $aExe = $acroSetupCandidates | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1

                # Dinamicka pretraga podfoldera ako nije na standardnoj lokaciji
                if (-not $aExe -and (Test-Path $activeProgDir)) {
                    $foundSetups = Get-ChildItem -Path $activeProgDir -Filter "Setup.exe" -Recurse -File -ErrorAction SilentlyContinue | Where-Object { $_.FullName -match '(?i)acrobat' }
                    if ($foundSetups -and $foundSetups.Count -gt 0) {
                        # Prioritet dajemo podfolderu 'Adobe Acrobat Pro\Setup.exe'
                        $pref = $foundSetups | Where-Object { $_.FullName -match '(?i)Adobe Acrobat Pro[\\/]Setup\.exe' } | Select-Object -First 1
                        $aExe = if ($pref) { $pref.FullName } else { $foundSetups[0].FullName }
                    }
                }

                # Dodatni fallback na *acrobat*.exe ako Setup.exe nije pronadjen
                if (-not $aExe -and (Test-Path $activeProgDir)) {
                    $fallbackExe = Get-ChildItem -Path $activeProgDir -Filter "*acrobat*.exe" -Recurse -File -ErrorAction SilentlyContinue | Select-Object -First 1
                    if ($fallbackExe) { $aExe = $fallbackExe.FullName }
                }

                if ($aExe) {
                    Unblock-File -Path $aExe -ErrorAction SilentlyContinue
                    Remove-Item -Path "$($aExe):Zone.Identifier" -ErrorAction SilentlyContinue
                    $aDir = Split-Path $aExe -Parent
                    $mstPath = Join-Path $aDir "AcroPro.mst"
                    $hasMst = Test-Path $mstPath

                    $acroArgs = if ($hasMst) {
                        '/sPB /rs /msi TRANSFORMS="AcroPro.mst"'
                    } else {
                        '/sAll /msi /norestart ALLUSERS=1'
                    }

                    if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                        Write-ServisLog "Instaliram Adobe Acrobat iz: $aExe sa argumentima $acroArgs" -Level "STEP" -Component "Acrobat"
                    }
                    if (Get-Command Wait-MsiMutexAvailable -ErrorAction SilentlyContinue) {
                        Wait-MsiMutexAvailable -TimeoutSec 180
                    }

                    $resAcro = Invoke-InstallerWithLocalFallback -InstallerPath $aExe -ArgumentList $acroArgs -AppName "Acrobat" -WorkingDirectory $aDir -IsDirectoryPackage:($aExe -like "\\*") -PackageSourceDir $aDir -WaitSeconds 300

                    # Fallback na /sAll /msi /norestart ALLUSERS=1 ukoliko primarni pokusaj sa TRANSFORMS nije prosao
                    $isInstalledNow = (Test-Path "C:\Program Files\Adobe\Acrobat DC\Acrobat\Acrobat.exe") -or
                                      (Test-Path "C:\Program Files (x86)\Adobe\Acrobat DC\Acrobat\Acrobat.exe") -or
                                      (Test-Path "C:\Program Files\Adobe\Acrobat Pro\Acrobat.exe") -or
                                      (Test-Path "C:\Program Files\Adobe\Acrobat 2024\Acrobat\Acrobat.exe")

                    if (-not $resAcro.Success -and -not $isInstalledNow -and $hasMst) {
                        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                            Write-ServisLog "Acrobat instalacija sa TRANSFORMS nije uspela. Pokrecem fallback /sAll /msi /norestart ALLUSERS=1..." -Level "WARN" -Component "Acrobat"
                        }
                        $resAcro = Invoke-InstallerWithLocalFallback -InstallerPath $aExe -ArgumentList "/sAll /msi /norestart ALLUSERS=1" -AppName "Acrobat" -WorkingDirectory $aDir -IsDirectoryPackage:($aExe -like "\\*") -PackageSourceDir $aDir -WaitSeconds 300
                    }

                    if ($resAcro.Success -or $resAcro.ExitCode -eq 0 -or $resAcro.ExitCode -eq 3010 -or
                        (Test-Path "C:\Program Files\Adobe\Acrobat DC\Acrobat\Acrobat.exe") -or
                        (Test-Path "C:\Program Files (x86)\Adobe\Acrobat DC\Acrobat\Acrobat.exe") -or
                        (Test-Path "C:\Program Files\Adobe\Acrobat Pro\Acrobat.exe") -or
                        (Test-Path "C:\Program Files\Adobe\Acrobat 2024\Acrobat\Acrobat.exe")) {
                        $acrobatOk = $true
                    }
                } else {
                    if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                        Write-ServisLog "Adobe Acrobat lokalni instaler nije pronadjen. Pokusavam Winget..." -Level "DEBUG" -Component "Acrobat"
                    }
                    try {
                        Start-Process -FilePath "winget.exe" -ArgumentList "install --id Adobe.Acrobat.Reader.64-bit --silent --accept-package-agreements --accept-source-agreements" -Wait -WindowStyle Hidden -ErrorAction SilentlyContinue
                    } catch {}
                    if ((Test-Path "C:\Program Files\Adobe\Acrobat DC\Acrobat\Acrobat.exe") -or
                        (Test-Path "C:\Program Files (x86)\Adobe\Acrobat DC\Acrobat\Acrobat.exe") -or
                        (Test-Path "C:\Program Files\Adobe\Acrobat Pro\Acrobat.exe") -or
                        (Test-Path "C:\Program Files\Adobe\Acrobat 2024\Acrobat\Acrobat.exe")) {
                        $acrobatOk = $true
                    }
                }
            }
        } catch {
            if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                Write-ServisLog "Upozorenje pri instalaciji Adobe Acrobat: $($_.Exception.Message)" -Level "WARN" -Component "Acrobat"
            }
            if (Get-Command Send-IpcLog -ErrorAction SilentlyContinue) {
                Send-IpcLog -Message "Greška pri instalaciji Acrobat: $($_.Exception.Message), nastavljam dalje..." -Level "WARN" -Phase 4
            }
        }
        $appsStatusMap["Acrobat"] = if ($acrobatOk) { "OK" } else { "WARN" }
        if (Get-Command Send-IpcAppUpdate -ErrorAction SilentlyContinue) {
            Send-IpcAppUpdate -AppId "Acrobat" -Status $appsStatusMap["Acrobat"] -Detail $(if ($acrobatOk) { "Instaliran" } else { "Neuspešno - Nastavak" })
        }
    }

    # 5.B VC++ RUNTIMES & .NET RUNTIMES
    try {
        if (Get-Command Send-IpcProgress -ErrorAction SilentlyContinue) {
            Send-IpcProgress -Progress 82 -Phase 4 -PhaseName "Programi" -SubPhase "Runtimes" -StatusMessage "Instalacija sistemskih runtime paketa (VC++ / .NET)..."
        }
        Install-ServisVCRedist -ProgramiRoot $activeProgDir
        Install-ServisDotNet -ProgramiRoot $activeProgDir
    } catch {
        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
            Write-ServisLog "Upozorenje pri instalaciji VC++/DotNet paketa: $($_.Exception.Message)" -Level "WARN" -Component "Runtimes"
        }
    }

    # 6. PREČICE NA RADNOJ POVRŠINI
    if (Get-Command Wait-ServisUICooperativePause -ErrorAction SilentlyContinue) {
        Wait-ServisUICooperativePause -CheckpointName "Faza 4 - Prečice"
    }
    if (Get-Command Send-IpcProgress -ErrorAction SilentlyContinue) {
        Send-IpcProgress -Progress 88 -Phase 4 -PhaseName "Programi" -SubPhase "Prečice" -StatusMessage "Kreiranje prečica na radnoj površini..."
    }
    if (Get-Command Send-IpcAppUpdate -ErrorAction SilentlyContinue) {
        Send-IpcAppUpdate -AppId "Shortcuts" -Status "RUNNING" -Detail "Kreiranje ikonica..."
    }

    try {
        Create-ServisShortcuts
        $appsStatusMap["Shortcuts"] = "OK"
        if (Get-Command Send-IpcAppUpdate -ErrorAction SilentlyContinue) {
            Send-IpcAppUpdate -AppId "Shortcuts" -Status "OK" -Detail "Prečice kreirane"
        }
    } catch {
        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
            Write-ServisLog "Upozorenje pri kreiranju prečica: $($_.Exception.Message)" -Level "WARN" -Component "Shortcuts"
        }
        $appsStatusMap["Shortcuts"] = "OK"
        if (Get-Command Send-IpcAppUpdate -ErrorAction SilentlyContinue) {
            Send-IpcAppUpdate -AppId "Shortcuts" -Status "OK" -Detail "Delimično kreirano"
        }
    }

    # 7. PROVERA OEM I SISTEMSKIH LICENCI
    if (Get-Command Wait-ServisUICooperativePause -ErrorAction SilentlyContinue) {
        Wait-ServisUICooperativePause -CheckpointName "Faza 4 - Provera licenci"
    }
    if (Get-Command Send-IpcProgress -ErrorAction SilentlyContinue) {
        Send-IpcProgress -Progress 93 -Phase 4 -PhaseName "Programi" -SubPhase "Provera licenci" -StatusMessage "Verifikacija OEM i sistemskih licenci..."
    }
    if (Get-Command Send-IpcAppUpdate -ErrorAction SilentlyContinue) {
        Send-IpcAppUpdate -AppId "License" -Status "RUNNING" -Detail "Provera sistemskih licenci..."
    }

    $licOk = $false
    if ((Get-Command Test-ServisSkipRequested -ErrorAction SilentlyContinue) -and (Test-ServisSkipRequested)) {
        if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
            Write-ServisLog "Provera licenci preskočena na zahtev servisera." -Level "WARN" -Component "License"
        }
        $appsStatusMap["License"] = "OK"
        if (Get-Command Send-IpcAppUpdate -ErrorAction SilentlyContinue) {
            Send-IpcAppUpdate -AppId "License" -Status "OK" -Detail "Preskočeno"
        }
    } else {
        try {
            Invoke-WindowsOfficeActivation -ProgramiDir $activeProgDir
            $licOk = $true
        } catch {
            if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
                Write-ServisLog "Upozorenje pri proveri licenci: $($_.Exception.Message)" -Level "WARN" -Component "License"
            }
            if (Get-Command Send-IpcLog -ErrorAction SilentlyContinue) {
                Send-IpcLog -Message "Provera licenci upozorenje: $($_.Exception.Message), nastavljam..." -Level "WARN" -Phase 4
            }
        }
        $appsStatusMap["License"] = if ($licOk) { "OK" } else { "WARN" }
        if (Get-Command Send-IpcAppUpdate -ErrorAction SilentlyContinue) {
            Send-IpcAppUpdate -AppId "License" -Status $appsStatusMap["License"] -Detail $(if ($licOk) { "Verifikovano" } else { "Best-Effort nastavak" })
        }
    }

    # 8. Reality Check: Fizička verifikacija stvarnog postojanja aplikacija na disku
    if (Get-Command Send-IpcProgress -ErrorAction SilentlyContinue) {
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
        if (Get-Command Send-IpcStatus -ErrorAction SilentlyContinue) {
            Send-IpcStatus -Phase 4 -Progress 98 -SubPhase "Reality Check Neuspeh" -StatusMessage "Greška: Fizički nedostaju aplikacije ($missingStr)" -Apps $appsStatusMap
        }
        throw "REALITY CHECK GREŠKA: Instalacija aplikacija nije kompletna! Fizički nedostaju na disku: $missingStr"
    }

    # 9. Zavrsni status i perzistencija stanja u deploy-state.json
    try {
        if (Get-Command Send-IpcStatus -ErrorAction SilentlyContinue) {
            Send-IpcStatus -Phase 4 -Progress 98 -SubPhase "Završna verifikacija" -StatusMessage "Svi korisnički programi su obrađeni i verifikovani na disku." -Apps $appsStatusMap
        }
    } catch {}

    try {
        if (Get-Command Save-DeployStatePhase -ErrorAction SilentlyContinue) {
            Save-DeployStatePhase -Phase 5
        }
    } catch {}

    if (Get-Command Send-IpcProgress -ErrorAction SilentlyContinue) {
        Send-IpcProgress -Progress 100 -Phase 4 -PhaseName "Programi" -SubPhase "Završeno" -StatusMessage "Faza 4 uspešno završena. Programi verifikovani."
    }
    if (Get-Command Write-ServisLog -ErrorAction SilentlyContinue) {
        Write-ServisLog "=== FAZA 4 USPESNO ZAVRSENA: Svi programi su verifikovani na disku ===" -Level "SUCCESS" -Component "Phase4"
    }

    $global:LASTEXITCODE = 0
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
    Test-WindowsActivated, `
    Test-OfficeActivated, `
    Test-AllServisProgramsInstalled, `
    Test-BatteryPresent, `
    Install-AllServisPrograms, `
    Set-BatteryBarConfiguration, `
    Wait-OfficeInstallationReady, `
    Invoke-WindowsOfficeActivation, `
    Invoke-AppsPhase, `
    Invoke-Phase4Apps, `
    Assert-InstalledApplicationsReality
