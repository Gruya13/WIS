<#
.SYNOPSIS
    GWADT Offline Servicing Tool - Ugradnja (slipstreaming) novih Windows ažuriranja i drajvera u install.wim.
.DESCRIPTION
    Automatizuje DISM operacije za offline integraciju kumulativnih zakrpa (.msu, .cab)
    i osnovnih drajvera (.inf) u Windows 11 instalacionu sliku (sources\install.wim).
    Automatski čisti bazu komponenti (/ResetBase) kako WIM slika ne bi nabujala na fleš disku.
.PARAMETER WimPath
    Putanja do install.wim datoteke. Ako se ne zada, skripta traži sources\install.wim na USB medijumu.
.PARAMETER UpdatesPath
    Direktorijum koji sadrži preuzete .msu ili .cab pakete ažuriranja.
.PARAMETER DriversPath
    Opcioni direktorijum sa .inf drajverima (npr. Intel VMD / IRST za diskove 10+ gen).
.PARAMETER MountDir
    Privremeni folder za montiranje WIM slike (npr. C:\GWADT_Mount).
.PARAMETER Index
    Broj indeksa edicije unutar WIM slike. Podrazumevano automatski detektuje "Windows 11 Pro".
.PARAMETER Optimize
    Pokreće čišćenje baze komponenti (/StartComponentCleanup /ResetBase) radi minimalne veličine slike.
.PARAMETER ExportImage
    Nakon unmount-a, izvozi sliku u novi, maksimalno kompresovani WIM fajl (/Compress:max).
#>

[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [string]$WimPath,

    [Parameter(Position = 1)]
    [string]$UpdatesPath,

    [Parameter(Position = 2)]
    [string]$DriversPath,

    [string]$MountDir,
    [int]$Index = 0,
    [switch]$Optimize,
    [switch]$ExportImage,
    [switch]$NonInteractive
)

# Inicijalizacija i striktno podešavanje
$ErrorActionPreference = "Stop"
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Definition
$ProjectRoot = Split-Path -Parent $ScriptDir
$LogDir = Join-Path $ProjectRoot "Logs"
if (-not (Test-Path $LogDir)) { New-Item -ItemType Directory -Path $LogDir -Force | Out-Null }
$Timestamp = Get-Date -Format "yyyyMMdd_HHmmss"
$LogFile = Join-Path $LogDir "wim_update_$Timestamp.log"

function Write-ServisLog {
    param(
        [string]$Message,
        [string]$Level = "INFO",
        [ConsoleColor]$Color = [ConsoleColor]::White
    )
    $timeStr = Get-Date -Format "yyyy-MM-dd HH:mm:ss"
    $logLine = "[$timeStr] [$Level] $Message"
    
    # Konzola
    $prevColor = $Host.UI.RawUI.ForegroundColor
    $Host.UI.RawUI.ForegroundColor = $Color
    Write-Host $logLine
    $Host.UI.RawUI.ForegroundColor = $prevColor
    
    # Log fajl
    try {
        Add-Content -Path $LogFile -Value $logLine -Encoding UTF8 -ErrorAction SilentlyContinue
    } catch {}
}

function Show-Header {
    Clear-Host
    Write-Host "================================================================================" -ForegroundColor Cyan
    Write-Host "         GWADT - WINDOWS 11 WIM OFFLINE SERVICING & SLIPSTREAMING TOOL           " -ForegroundColor Yellow
    Write-Host "           Automatizovana integracija novih zakrpa i drajvera u WIM             " -ForegroundColor White
    Write-Host "================================================================================" -ForegroundColor Cyan
    Write-Host ""
}

# 1. Provera administratorskih prava
Show-Header
Write-ServisLog "Proveravam administratorske privilegije..." -Level "CHECK" -Color Yellow
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-ServisLog "GRESKA: Ova skripta MORA biti pokrenuta kao Administrator (povišene privilegije)!" -Level "FATAL" -Color Red
    Write-Host ""
    Write-Host "Pokrenite PowerShell kao Administrator ili koristite 'Osvezi-WimSliku.bat'." -ForegroundColor Yellow
    Write-Host ""
    if (-not $NonInteractive) { Read-Host "Pritisnite Enter za izlaz..." }
    exit 1
}
Write-ServisLog "Administratorska prava potvrdjena." -Level "SUCCESS" -Color Green

# 2. Provera DISM alata
$dismExe = "$env:SystemRoot\System32\dism.exe"
if (-not (Test-Path $dismExe)) {
    Write-ServisLog "GRESKA: dism.exe nije pronadjen na lokaciji $dismExe!" -Level "FATAL" -Color Red
    exit 1
}

# 3. Lociranje install.wim
if ([string]::IsNullOrWhiteSpace($WimPath)) {
    $candidatePaths = @(
        (Join-Path $ProjectRoot "sources\install.wim"),
        (Join-Path $ScriptDir "..\sources\install.wim"),
        "sources\install.wim"
    )
    foreach ($cand in $candidatePaths) {
        if (Test-Path $cand) {
            $WimPath = (Resolve-Path $cand).Path
            break
        }
    }
}

if (-not (Test-Path $WimPath)) {
    Write-ServisLog "Nije automatski pronadjen 'sources\install.wim'." -Level "WARN" -Color Yellow
    if ($NonInteractive) {
        Write-ServisLog "GRESKA: Nije navedena validna putanja do install.wim fajla!" -Level "FATAL" -Color Red
        exit 1
    }
    $enteredWim = Read-Host "Unesite punu putanju do install.wim datoteke"
    if (Test-Path $enteredWim) {
        $WimPath = (Resolve-Path $enteredWim).Path
    } else {
        Write-ServisLog "GRESKA: Datoteka '$enteredWim' ne postoji!" -Level "FATAL" -Color Red
        exit 1
    }
}
Write-ServisLog "Ciljni WIM fajl: $WimPath" -Level "INFO" -Color Cyan

# 4. Lociranje foldera sa zakrpama
if ([string]::IsNullOrWhiteSpace($UpdatesPath)) {
    $candidateUpdates = @(
        (Join-Path $ProjectRoot "Zakrpe"),
        (Join-Path $ProjectRoot "Updates"),
        (Join-Path $ScriptDir "Zakrpe"),
        ".\Zakrpe"
    )
    foreach ($cand in $candidateUpdates) {
        if (Test-Path $cand) {
            $UpdatesPath = (Resolve-Path $cand).Path
            break
        }
    }
}

if (-not (Test-Path $UpdatesPath)) {
    Write-ServisLog "Folder sa zakrpama nije pronadjen." -Level "WARN" -Color Yellow
    $defaultPatchDir = Join-Path $ProjectRoot "Zakrpe"
    if (-not $NonInteractive) {
        Write-Host ""
        Write-Host "Folder sa preuzetim zakrpama ne postoji ($defaultPatchDir)." -ForegroundColor Yellow
        $odgovor = Read-Host "Zelite li da kreirate folder 'Zakrpe' sada i ubacite .msu pakete? (D/N) [D]"
        if ([string]::IsNullOrWhiteSpace($odgovor) -or $odgovor.ToUpper() -eq "D") {
            New-Item -ItemType Directory -Path $defaultPatchDir -Force | Out-Null
            Write-ServisLog "Kreiran prazan folder: $defaultPatchDir" -Level "SUCCESS" -Color Green
            Write-Host ""
            Write-Host "--> Prekopirajte zeljene .msu / .cab fajlove u folder: $defaultPatchDir" -ForegroundColor Cyan
            Write-Host "    (Npr. Cumulative Update, Servicing Stack Update, .NET Update)" -ForegroundColor Gray
            Write-Host ""
            Read-Host "Kada iskopirate fajlove, pritisnite Enter za nastavak..."
            $UpdatesPath = $defaultPatchDir
        } else {
            $enteredUpdates = Read-Host "Unesite punu putanju do foldera sa zakrpama"
            if (Test-Path $enteredUpdates) {
                $UpdatesPath = (Resolve-Path $enteredUpdates).Path
            } else {
                Write-ServisLog "GRESKA: Putanja '$enteredUpdates' ne postoji!" -Level "FATAL" -Color Red
                exit 1
            }
        }
    } else {
        Write-ServisLog "GRESKA: Nije navedena validna putanja do foldera sa zakrpama!" -Level "FATAL" -Color Red
        exit 1
    }
}

# Prebrojavanje paketa
$updatePackages = Get-ChildItem -Path $UpdatesPath -File | Where-Object { $_.Extension -in @(".msu", ".cab") }
if ($updatePackages.Count -eq 0) {
    Write-ServisLog "UPOZORENJE: U folderu '$UpdatesPath' nema .msu ili .cab paketa!" -Level "WARN" -Color Yellow
    if (-not $NonInteractive) {
        $nastaviBez = Read-Host "Zelite li ipak da nastavite (npr. samo za drajvere ili ciscenje)? (D/N) [N]"
        if ($nastaviBez.ToUpper() -ne "D") {
            Write-ServisLog "Operacija prekinuta na zahtev korisnika." -Level "INFO" -Color Gray
            exit 0
        }
    }
} else {
    Write-ServisLog "Pronadjeno $($updatePackages.Count) paketa za integraciju u '$UpdatesPath':" -Level "INFO" -Color Green
    foreach ($pkg in $updatePackages) {
        $sizeMb = [math]::Round($pkg.Length / 1MB, 2)
        Write-Host "  * $($pkg.Name) ($sizeMb MB)" -ForegroundColor Gray
    }
}

# 5. Drajveri (opciono)
if ([string]::IsNullOrWhiteSpace($DriversPath)) {
    $vmdPath = Join-Path $ProjectRoot "Za disk 10+ gen"
    if (Test-Path $vmdPath) {
        $DriversPath = $vmdPath
        Write-ServisLog "Pronadjen podrazumevani folder sa drajverima diskova: $DriversPath" -Level "INFO" -Color Cyan
    }
}

# 6. Analiza edicija u WIM-u
Write-Host ""
Write-ServisLog "Citavam strukturu i edicije unutar install.wim..." -Level "STEP" -Color Yellow
$wimInfoRaw = & $dismExe /Get-WimInfo /WimFile:"$WimPath" 2>&1
$wimEditions = @()
$currIdx = $null
$currName = $null

foreach ($line in ($wimInfoRaw -split "`r?`n")) {
    if ($line -match "Index\s*:\s*(\d+)") {
        $currIdx = [int]$matches[1]
    }
    if ($line -match "Name\s*:\s*(.+)") {
        $currName = $matches[1].Trim()
        if ($currIdx -ne $null) {
            $wimEditions += [PSCustomObject]@{
                Index = $currIdx
                Name  = $currName
            }
            $currIdx = $null
            $currName = $null
        }
    }
}

Write-Host "--------------------------------------------------------------------------------" -ForegroundColor Gray
Write-Host " Pronadjene edicije u install.wim:" -ForegroundColor White
foreach ($ed in $wimEditions) {
    Write-Host ("  [{0}] {1}" -f $ed.Index, $ed.Name) -ForegroundColor Cyan
}
Write-Host "--------------------------------------------------------------------------------" -ForegroundColor Gray

# Izbor indeksa
if ($Index -le 0) {
    # Podrazumevano tražimo Windows 11 Pro (autounattend.xml standard)
    $proEdition = $wimEditions | Where-Object { $_.Name -match "Windows 11 Pro\b" -or $_.Name -match "Pro\b" } | Select-Object -First 1
    if ($proEdition) {
        $Index = $proEdition.Index
        Write-ServisLog "Automatski detektovan preporuceni indeks: [$Index] $($proEdition.Name)" -Level "INFO" -Color Green
    } else {
        $Index = 1
        Write-ServisLog "Koristim podrazumevani indeks [1] ($($wimEditions[0].Name))" -Level "WARN" -Color Yellow
    }

    if (-not $NonInteractive) {
        $odabir = Read-Host "Pritisnite Enter da potvrdite indeks [$Index] ili unesite zeljeni broj indeksa"
        if (-not [string]::IsNullOrWhiteSpace($odabir) -and ($odabir -as [int])) {
            $Index = [int]$odabir
        }
    }
}
Write-ServisLog "Izabran radni indeks: $Index" -Level "INFO" -Color Cyan

# 7. Mount direktorijum i provera slobodnog prostora
if ([string]::IsNullOrWhiteSpace($MountDir)) {
    # Najbolje na najbržem disku sa puno mesta (C:\GWADT_Mount)
    $MountDir = "C:\GWADT_Mount"
}

$mountDrive = [System.IO.Path]::GetPathRoot($MountDir)
$driveObj = Get-PSDrive -Name ($mountDrive.TrimEnd(":\")) -ErrorAction SilentlyContinue
if ($driveObj) {
    $freeGb = [math]::Round($driveObj.Free / 1GB, 2)
    Write-ServisLog "Slobodan prostor na disku $mountDrive : $freeGb GB" -Level "INFO" -Color White
    if ($freeGb -lt 15) {
        Write-ServisLog "UPOZORENJE: Za montiranje i servisiranje WIM-a preporucuje se minimalno 15-20 GB slobodnog prostora!" -Level "WARN" -Color Yellow
    }
}

if (-not (Test-Path $MountDir)) {
    New-Item -ItemType Directory -Path $MountDir -Force | Out-Null
}

# 8. Glavni proces - Mount, Slipstream, Cleanup, Unmount
Write-Host ""
Write-Host "================================================================================" -ForegroundColor Cyan
Write-Host "                 POKRETANJE OFFLINE SERVICING PROCESA                           " -ForegroundColor Green
Write-Host "================================================================================" -ForegroundColor Cyan

$mountSuccess = $false
try {
    # Provera da li je nešto već montirano na tom folderu
    $existingMounts = & $dismExe /Get-MountedWimInfo 2>&1
    if ($existingMounts -match [regex]::Escape($MountDir)) {
        Write-ServisLog "Detektovana zaostala montirana slika na '$MountDir'. Pokusavam cleanup..." -Level "WARN" -Color Yellow
        & $dismExe /Cleanup-Wim | Out-Null
    }

    # A. Montiranje slike
    Write-ServisLog "Montiram WIM sliku (Indeks: $Index) u '$MountDir'..." -Level "STEP" -Color Yellow
    Write-ServisLog "Ovo moze potrajati 1 do 3 minuta zavisno od brzine diska..." -Level "INFO" -Color Gray
    $mountOut = & $dismExe /Mount-Wim /WimFile:"$WimPath" /Index:$Index /MountDir:"$MountDir" 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "Greska pri montiranju WIM-a: `n$mountOut"
    }
    $mountSuccess = $true
    Write-ServisLog "WIM slika uspesno montirana." -Level "SUCCESS" -Color Green

    # B. Integracija drajvera (ako je naveden i postoji)
    if (-not [string]::IsNullOrWhiteSpace($DriversPath) -and (Test-Path $DriversPath)) {
        $infCount = (Get-ChildItem -Path $DriversPath -Filter "*.inf" -Recurse -File).Count
        if ($infCount -gt 0) {
            Write-ServisLog "Dodajem $infCount .inf drajvera iz '$DriversPath'..." -Level "STEP" -Color Yellow
            $drvOut = & $dismExe /Image:"$MountDir" /Add-Driver /Driver:"$DriversPath" /Recurse 2>&1
            if ($LASTEXITCODE -ne 0) {
                Write-ServisLog "Upozorenje pri dodavanju drajvera: $drvOut" -Level "WARN" -Color Yellow
            } else {
                Write-ServisLog "Drajveri uspesno integrisani." -Level "SUCCESS" -Color Green
            }
        }
    }

    # C. Integracija ažuriranja (.msu / .cab)
    if ($updatePackages.Count -gt 0) {
        Write-ServisLog "Integrisem zakrpe iz '$UpdatesPath'..." -Level "STEP" -Color Yellow
        Write-ServisLog "Napomena: DISM automatski obradjuje Servicing Stack i Cumulative Update..." -Level "INFO" -Color Gray
        
        # Pojedinačno ili grupno dodavanje
        $pkgOut = & $dismExe /Image:"$MountDir" /Add-Package /PackagePath:"$UpdatesPath" 2>&1
        if ($LASTEXITCODE -ne 0) {
            Write-ServisLog "UPOZORENJE ili GRESKA pri ugradnji paketa: `n$pkgOut" -Level "WARN" -Color Yellow
        } else {
            Write-ServisLog "Svi paketi azuriranja uspesno ugradjeni u sliku!" -Level "SUCCESS" -Color Green
        }
    }

    # D. Čišćenje baze komponenti (/ResetBase)
    Write-Host ""
    Write-ServisLog "Pokrecem duboko ciscenje i konsolidaciju baze komponenti (/ResetBase)..." -Level "STEP" -Color Yellow
    Write-ServisLog "Ova operacija uklanja zastarele verzije sistemskih fajlova i stedi gigabajte prostora!" -Level "INFO" -Color Gray
    $cleanOut = & $dismExe /Image:"$MountDir" /Cleanup-Image /StartComponentCleanup /ResetBase 2>&1
    if ($LASTEXITCODE -ne 0) {
        Write-ServisLog "Napomena: ResetBase je vratio izlaz: $cleanOut" -Level "WARN" -Color Yellow
    } else {
        Write-ServisLog "Baza komponenti uspesno optimizovana i ociscena." -Level "SUCCESS" -Color Green
    }

    # E. Snimanje izmena i demontiranje (Commit & Unmount)
    Write-Host ""
    Write-ServisLog "Snimam izmene i demontiram WIM sliku (/Commit)..." -Level "STEP" -Color Yellow
    Write-ServisLog "Molimo sacekajte, u toku je trajno upisivanje izmena na disk..." -Level "INFO" -Color Gray
    $unmountOut = & $dismExe /Unmount-Wim /MountDir:"$MountDir" /Commit 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "Greska pri demontiranju sa snimanjem: `n$unmountOut"
    }
    $mountSuccess = $false
    Write-ServisLog "Slika uspesno demontirana i izmene su trajno sacuvane!" -Level "SUCCESS" -Color Green

    # F. Opciona Rekompresija (Export-Image)
    if ($ExportImage) {
        Write-Host ""
        $newWimPath = [System.IO.Path]::Combine((Split-Path $WimPath), "install_optimized.wim")
        Write-ServisLog "Izvozim sliku u maksimalno kompresovan format ($newWimPath)..." -Level "STEP" -Color Yellow
        $exportOut = & $dismExe /Export-Image /SourceImageFile:"$WimPath" /SourceIndex:$Index /DestinationImageFile:"$newWimPath" /Compress:max 2>&1
        if ($LASTEXITCODE -eq 0) {
            Write-ServisLog "Optimizovani WIM uspesno kreiran: $newWimPath" -Level "SUCCESS" -Color Green
            Write-ServisLog "Možete zameniti originalni install.wim novim fajlom radi maksimalne ustede prostora." -Level "INFO" -Color Cyan
        } else {
            Write-ServisLog "Greska pri izvozu slike: $exportOut" -Level "WARN" -Color Yellow
        }
    }

} catch {
    Write-ServisLog "DOSLO JE DO GRESKE U TOKU RADA: $_" -Level "FATAL" -Color Red
} finally {
    # Zastitni mehanizam: ako je slika ostala montirana usled greske, pitaj za discard
    if ($mountSuccess) {
        Write-ServisLog "UPOZORENJE: WIM slika je ostala montirana na '$MountDir'!" -Level "WARN" -Color Yellow
        if (-not $NonInteractive) {
            $discard = Read-Host "Zelite li da ponistite izmene i bezbedno demontirate sliku (/Discard)? (D/N) [D]"
            if ([string]::IsNullOrWhiteSpace($discard) -or $discard.ToUpper() -eq "D") {
                Write-ServisLog "Demontiram sliku sa odbacivanjem izmena..." -Level "INFO" -Color Gray
                & $dismExe /Unmount-Wim /MountDir:"$MountDir" /Discard 2>&1 | Out-Null
            }
        }
    }
}

# Kraj i izveštaj
Write-Host ""
Write-Host "================================================================================" -ForegroundColor Cyan
Write-Host "                      ZAVRSEN PROCES OFFLINE SERVISIRANJA                       " -ForegroundColor Green
Write-Host "================================================================================" -ForegroundColor Cyan
Write-Host " Detaljan operativni log je sacuvan u: $LogFile" -ForegroundColor White
Write-Host ""
if (-not $NonInteractive) {
    Read-Host "Pritisnite Enter za zavrsetak..."
}
