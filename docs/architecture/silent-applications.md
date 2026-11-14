---
title: "Tihe Instalacije Aplikacija, SMB 3.1.1 Stream i Licenciranje (Silent Applications & Licensing)"
type: architecture
created: 2026-09-14
updated: 2026-11-11
tags:
  - architecture
  - applications
  - silent-install
  - licensing
  - oem-licensing
  - smb
  - stepguard
---

# Tihe Instalacije Aplikacija, SMB 3.1.1 Stream i Licenciranje: WIS (v3.0.0)

U Fazi 4 (`Phase4-Apps.ps1` i `WIS.Apps.psm1`) celokupan paket od **8 standardnih servisnih aplikacija i alata** izvršava se kroz **9 idempotentnih `StepGuard` koraka** (`P4.Runtimes..P4.Activation` u `step-journal.json`) u 100% tihom (*unattended*) režimu, uz direktni gigabitni SMB 3.1.1 striming sa TrueNAS deljenja `\\192.168.170.254\Programi`, sekvencijalnu zaštitu Windows Installer servisa (`WIS.Core.psm1`) i fizičku proveru prisustva izvršnih fajlova i prečica ([[decisions/ADR-0185-phase4-office-odt-bom-free-and-local-nvme-sourcepath]], [[decisions/ADR-0225-wis-v300-embedded-hwtest-bidirectional-wu-and-preoobe-autopilot]]).

---

## 🌐 1. Direktni SMB 3.1.1 Stream, `WIS.Core` Uvoz i Zaštita od `Error 1219` (`Get-ServisProgramiRoot`)

Zahvaljujući Lean ISO arhitekturi ([[decisions/ADR-0103-lean-iso-nas-apps-streaming]]), instalacioni paketi programa se ne dupliraju u ISO slici, već se strimuju ili keširaju sa TrueNAS servera:
1. Lokalni keš `C:\setup\Programi` (sa kojim rade lokalizovani instaleri poput Chrome MSI keša i Office ODT `Office\Data`).
2. Primarno namensko SMB 3.1.1 deljenje `\\192.168.170.254\Programi` / `\\truenas.servis\Programi`. `Phase4-Apps.ps1` eksplicitno uvozi `WIS.Core.psm1`, gde `Sync-ProgramiFromNas` uklanja prethodne suprotstavljene konekcije (`Error 1219` zaštita) pre autentifikacije.
3. Fallback SMB deljenja (`\\192.168.170.254\WIS\Programi`, `\Servis\Programi`) ili priključeni USB diskovi.

Funkcija `Invoke-InstallerWithLocalFallback` po potrebi kešira instalere u `C:\setup\temp\<AppName>` (ili trajno za Chrome/Office u `C:\setup\Programi`) uz garantovano čišćenje privremenih fajlova u `finally` bloku.

---

## 🔒 2. Obavezni Međurestart i Izolacija Windows Installer Servisa (`1618` / `1603`)

1. **Obavezni Kontrolisani Međurestart (`HandleInterimReboot`):** Po završetku Faze 3, `WIS.UI.exe` obavezno pokreće kontrolisani međurestart (`step-journal.json` čuva `interim_reboot_required`) kako bi oslobodio CBS stek i `PendingFileRenameOperations` brave pre instalacije aplikacija u Fazi 4 ([[decisions/ADR-0185-phase4-office-odt-bom-free-and-local-nvme-sourcepath]]).
2. **NTFS SID Dozvole (`Grant-SetupAclPermissions`):** Obezbeđuje pun pristup (`(OI)(CI)F`) za `NT AUTHORITY\SYSTEM` (`*S-1-5-18`), `Administrators` (`*S-1-5-32-544`) i `Everyone` (`*S-1-1-0`).
3. **Sekvencijalni MSI Mutex Gate (`Wait-MsiMutexAvailable -TimeoutSec 180`):** Pre svake MSI instalacije čeka da sistemski mutex `Global\_MSIExecute` bude slobodan.

---

## 📦 3. Specifikacija 8 Aplikacija (`9 StepGuard` Koraka)

| StepGuard ID | Paket | Izvor i Komanda | Ključni Inženjerski Detalji |
| :--- | :--- | :--- | :--- |
| **`P4.Runtimes`** | **.NET Desktop & VC++ AIO** | `DotNet\*.exe` + `VCRedist\VisualCppRedist_AIO_x86_x64.exe` | 120s timeout i automatski retry mehanizam za kodove `1603` / `0x80070652`; AIO `/ai /gm2` za VC++ 2005–2022. |
| **`P4.WinRAR`** | **WinRAR 7.01 x64** | `winrar-x64-701.exe /S` | Tiha SFX instalacija, registracija licence i uklanjanje suvišne prečice sa radne površine (`Remove-WinRARDesktopShortcut`). |
| **`P4.Chrome`** | **Google Chrome Enterprise x64** | Lokalni keš `C:\setup\Programi\...msi` sa NAS fallback-om | Preuzima/kešira i pokreće offline MSI (`/qn /norestart ALLUSERS=1`) za **~8 s** (uz fallback na `ChromeSetup.exe` / Winget). |
| **`P4.Acrobat`** | **Adobe Acrobat Pro 2024 x64** | `Adobe.Acrobat.Pro.2024.x64.Multilingual\Setup.exe` (`/sPB /rs /msi TRANSFORMS="AcroPro.mst"`) | Dvokoračni MSI/MSP tok uz čišćenje zaključavanja i automatsku aktivaciju. |
| **`P4.Office`** | **Microsoft Office 2024 LTSC** | `Office 2024\files\setup.exe /configure C:\setup\Office2024_Install.xml` | Prepoznaje i **`Configure.xml`** i **`configuration.xml`**, uklanja UTF-8 BOM, preko `Sync-ProgramiFromNas -Force` povlači offline `Office\Data` na NVMe SSD, upisuje lokalni `SourcePath` (neophodan pod `NT AUTHORITY\SYSTEM` `OfficeClickToRun.exe`) i čeka `420s + 120s` C2R flush ([[decisions/ADR-0185-phase4-office-odt-bom-free-and-local-nvme-sourcepath]]). |
| **`P4.BatteryBar`** | **BatteryBar Pro 3.6.6** | `BatteryBarSetup-3.6.6.exe /S` + `Set-BatteryBarConfiguration` | **Na desktopu `SKIPPED`.** Na laptopovima proverava x86/x64 putanje bez NSIS popup-a i upisuje `BatteryBar.Settings.xml` (`Solo=True`, `1550, 980`). |
| **`P4.Shortcuts`** | **Prečice na Desktopu** | Per-app verifikacija u `Get-WisAppPresence` | Proverava `.lnk` prečice pojedinačno za svaku stvarno instaliranu aplikaciju (`Adobe Acrobat.lnk`, `Word.lnk`, `Excel.lnk`...) radi naknadne dopune. |
| **`P4.PdfDefault`** | **6-stepeni PDF Lockdown** | DISM + GPO + Acrobat Pro asocijacija | DISM XML uvoz, GPO polisa, `bDisablePDFHandlerSwitching = 1` protiv Edge preuzimanja PDF-a. |
| **`P4.Activation`** | **OEM Licenca (OA3x) & CustomScripts** | UEFI/BIOS MSDM OA3x (`SoftwareLicensingService`) | Verifikacija ugrađene OEM digitalne licence i opcioni lokalni post-install hook (`CustomScripts\*.ps1`). |

---

## ✅ 4. Post-Execution Reality Check i UI Rerun Kontrola (`Assert-InstalledApplicationsReality`)

Pre završetka Faze 4, funkcija `Assert-InstalledApplicationsReality` fizički proverava prisustvo izvršnih fajlova aplikacija na disku (`chrome.exe`, `Acrobat.exe`, `WINWORD.EXE`, `WinRAR.exe`, `BatteryBar.exe`, `.NET` / `VCRedist`), dok operater iz `WIS.UI.exe` može po potrebi aktivirati `RerunPhase4Command` (`force-rerun-phase4.flag`) za ponovljeni prolaz Faze 4 bez gubitka prethodnog progresa ([[decisions/ADR-0125-system-defects-and-bilingual-ui]], [[decisions/ADR-0181-stepguard-precheck-hardware-identity-and-ui-v23]]).

---

## 🔗 Povezane Beleške
- Pregled Arhitekture: [[architecture/overview]]
- Pajplajn Izvršavanja: [[architecture/pipeline]]
- Sanacija Faze 4 i Office 2024 ODT: [[decisions/ADR-0185-phase4-office-odt-bom-free-and-local-nvme-sourcepath]]
- StepGuard i UI v2.3.0: [[decisions/ADR-0181-stepguard-precheck-hardware-identity-and-ui-v23]]
- Optimizacija Deployment Pipeline-a: [[decisions/ADR-0101-deployment-pipeline-optimization]]
- BatteryBar Floating Toolbar za Win11: [[decisions/ADR-0119-batterybar-floating-toolbar-win11]]
