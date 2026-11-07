---
title: "Tihe Instalacije Aplikacija, SMB 3.1.1 Stream i Licenciranje (Silent Applications & Licensing)"
type: architecture
created: 2026-09-14
updated: 2026-10-26
tags:
  - architecture
  - applications
  - silent-install
  - licensing
  - oem-licensing
  - smb
---

# Tihe Instalacije Aplikacija, SMB 3.1.1 Stream i Licenciranje: WIS (v2.17.0)

U Fazi 4 (`Phase4-Apps.ps1` i `WIS.Apps.psm1`, 4,308 linija) celokupan paket od **8 standardnih servisnih aplikacija i alata** instalira se u 100% tihom (*unattended*) režimu, uz direktni gigabitni SMB 3.1.1 striming sa TrueNAS deljenja `\\192.168.170.254\Programi`, sekvencijalnu zaštitu Windows Installer servisa i fizičku proveru prisustva izvršnih fajlova na disku.

---

## 🌐 1. Direktni SMB 3.1.1 Stream sa Deljenja `Programi` (`Get-ServisProgramiRoot`)

Zahvaljujući Lean ISO arhitekturi ([[decisions/ADR-0103-lean-iso-nas-apps-streaming]], WIS-TASK-146), instalacioni paketi programa se ne dupliraju u ISO slici (ušteda ~4.45 GB), već se strimuju sa TrueNAS servera po sledećoj hijerarhiji:
1. Lokalni keš `C:\setup\Programi` (ukoliko se koristi terenski `full` USB profil).
2. Primarno namensko SMB 3.1.1 deljenje `\\192.168.170.254\Programi` / `\\truenas.servis\Programi` (autentifikovano sa `admin:<SMB_PASSWORD>`).
3. Fallback SMB deljenja (`\\192.168.170.254\WIS\Programi`, `\Servis\Programi`) ili priključeni USB diskovi.

Funkcija `Invoke-InstallerWithLocalFallback` pokreće instalere direktno sa mreže, a po potrebi ih privremeno kešira u `C:\setup\temp\<AppName>` uz garantovano brisanje u `finally` bloku.

---

## 🔒 2. Izolacija Windows Installer Servisa i Prevencija Grešaka 1618 i 1603

1. **Kontrolisani Međurestart pre Faze 4 (`Invoke-ServisInterimReboot`):** Oslobađa Component-Based Servicing (CBS) stek i `PendingFileRenameOperations` nakon instalacije drajvera u Fazi 2.
2. **NTFS SID Dozvole (`Grant-SetupAclPermissions`):** Obezbeđuje pun pristup (`(OI)(CI)F`) za `NT AUTHORITY\SYSTEM` (`*S-1-5-18`), `Administrators` (`*S-1-5-32-544`) i `Everyone` (`*S-1-1-0`).
3. **Sekvencijalni MSI Mutex Gate (`Wait-MsiMutexAvailable -TimeoutSec 180`):** Pre svake MSI instalacije čeka da sistemski mutex `Global\_MSIExecute` bude slobodan.

---

## 📦 3. Specifikacija 8 Aplikacija i Mehanizama Instalacije

| # | Paket | Izvor i Komanda | Ključni Inženjerski Detalji |
| :--- | :--- | :--- | :--- |
| **1.** | **Google Chrome Enterprise x64** | `GoogleChromeStandaloneEnterprise64.msi` (`/qn /norestart ALLUSERS=1`) | Offline MSI instalacija za **~8 sekundi** (uz fallback na `ChromeSetup.exe /silent /install`). |
| **2.** | **Adobe Acrobat Pro 2024 x64** | `Adobe.Acrobat.Pro.2024.x64.Multilingual\Setup.exe` (`/sPB /rs /msi TRANSFORMS="AcroPro.mst"`) | Dvokoračni MSI/MSP tok + tihi aktivator + **6-stepeno zaključavanje PDF asocijacije** (DISM XML uvoz, GPO polisa, `bDisablePDFHandlerSwitching = 1`). |
| **3.** | **Microsoft Office 2024 LTSC** | `Office 2024\setup.exe /configure Configure.xml` | Dinamički generiše `Configure.xml` sa `SourcePath` usmerenim na SMB deljenje; instalira Word, Excel, PowerPoint i Outlook bez kopiranja CAB arhive na SSD. |
| **4.** | **WinRAR 7.01 x64** | `winrar-x64-701.exe /S` (tihi SFX instaler) | Tiha instalacija, registracija licence i uklanjanje suvišne prečice sa radne površine (`Remove-WinRARDesktopShortcut`). |
| **5.** | **BatteryBar Pro 3.6.6** | `BatteryBarSetup-3.6.6.exe /S` + `Set-BatteryBarConfiguration` | **Na desktop računarima automatski dobija status `SKIPPED`.** Na laptopovima proverava obe putanje (`Program Files` i `Program Files (x86)`) kako nikada ne bi otvorio blokirajući NSIS dijalog, upisuje `BatteryBar.Settings.xml` (**Floating Toolbar** na koordinatama `1550, 980`, `AlwaysOnTop=True`), kreira Startup prečicu i pokreće vidljivi plivajući indikator ([[decisions/ADR-0119-batterybar-floating-toolbar-win11]], [[decisions/ADR-0132-hotfix-v2151-stress-load-batterybar-and-timeout]]). |
| **6.** | **Microsoft .NET Desktop Runtime** | `DotNet\windowsdesktop-runtime-*.exe` (`/install /quiet /norestart`) | 120s timeout i automatski retry mehanizam za kodove `1603` i `0x80070652`. |
| **7.** | **Visual C++ AIO Runtimes** | `VCRedist\VisualCppRedist_AIO_x86_x64.exe` (`/ai /gm2`) | Instalira sve x86 i x64 Visual C++ biblioteke (2005–2022) u jednom prolazu ([[decisions/ADR-0101-deployment-pipeline-optimization]]). |
| **8.** | **OEM Licenca (OA3x)** | UEFI/BIOS MSDM OA3x (`SoftwareLicensingService`) | Verifikacija ugrađene OEM digitalne licence matične ploče i opcioni post-install hook (`CustomScripts`). |

---

## ✅ 4. Post-Execution Reality Check (`Assert-InstalledApplicationsReality`)

Pre završetka Faze 4, funkcija `Assert-InstalledApplicationsReality` fizički proverava prisustvo izvršnih fajlova svih aplikacija na disku (`chrome.exe`, `Acrobat.exe`, `WINWORD.EXE`, `WinRAR.exe`, `BatteryBar.exe`, `.NET` / `VCRedist` registri), garantujući da `WIS.UI.exe` i završni servisni karton prikazuju status `INSTALIRANO` isključivo kada je softver zaista prisutan na sistemu ([[decisions/ADR-0125-system-defects-and-bilingual-ui]]).

---

## 🔗 Povezane Beleške
- Pregled Arhitekture: [[architecture/overview]]
- Pajplajn Izvršavanja: [[architecture/pipeline]]
- Optimizacija Deployment Pipeline-a: [[decisions/ADR-0101-deployment-pipeline-optimization]]
- BatteryBar Floating Toolbar za Win11: [[decisions/ADR-0119-batterybar-floating-toolbar-win11]]
