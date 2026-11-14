---
title: "Modularni Sistem i Build Pajplajn (Modules & Build System)"
type: architecture
created: 2026-09-14
updated: 2026-11-11
tags:
  - architecture
  - modules
  - build
  - powershell
  - python
  - csharp
---

# Modularni Sistem i Build Pajplajn: WIS (v3.0.0)

Kako bi se obezbedila maksimalna pouzdanost, čitljivost i striktna kompatibilnost sa **Windows PowerShell 5.1** i **.NET Framework 4.0 / 4.8**, **WIS** (*Windows Installation Assistant*) je organizovan u **četiri glavne razvojne celine** (`ISO/`, `Launcher/`, `UI/`, `ServiceHub/`) i namenski forenzički podsistem `Tools/AutopilotAuditor/` (dok `UI/HwTest/` pruža modularni paket za automatske i interaktivne testove hardvera).

Nekadašnji monolitni skript `Install-ServisApps.ps1`, legacy `GWADT.*.psm1` shimovi i stari alati `Build-DeployScript.py` / `Test-GWADTModules.py` su trajno penzionisani ([[decisions/ADR-0076-decoupling-monolith-into-phase-scripts-ui-orchestrator]], [[decisions/ADR-0131-diagnostics-redesign-6phase-pipeline-and-wis-cleanup]], [[decisions/ADR-0137-pure-wis-cleanup-and-gwadt-retirement]]).

---

## 🗂️ 1. Struktura PowerShell 5.1 Modula (`UI/Skripte/Modules/`)

Celokupna klijentska poslovna logika u Windows okruženju podeljena je na **12 specijalizovanih PowerShell 5.1 modula (`.psm1`)** i **1 pozadinskog radnika (`WIS.ScreenCapture.ps1`)** — ukupno **13 fajlova** i **21,315 linija koda (`1,041,317 B`)**, obavezno čuvanih u **UTF-8 with BOM + CRLF** formatu bez tipografskih navodnika (`0x93`/`0x94`) ili 4-bajtnih emodžija ([[decisions/ADR-0185-phase4-office-odt-bom-free-and-local-nvme-sourcepath]]):

```text
UI/Skripte/Modules/
├── WIS.Common.psm1          # JSON IPC striming (Send-IpcStep), pauza, rotacija logova i deploy-state.json (1,025 linija)
├── WIS.StatusContract.psm1  # Kanonski statusni ugovor: razdvajanje DONE statusa od cooling_verified (29 linija)
├── WIS.StepGuard.psm1       # Idempotentni StepGuard motor, step-journal.json i pending reboot detekcija (647 linija)
├── WIS.Core.psm1            # Jezgro: Wifi-Servis.ini, SMB auth (Error 1219 zaštita), NTFS SID ACL, MSI mutex (2,129 linija)
├── WIS.Hardware.psm1        # CIM/WMI, S.M.A.R.T., EDID, Autopilot keš, DeviceManager, RunspacePool stres test (2,902 linije)
├── WIS.Network.psm1         # Wi-Fi povezivanje, PnP fallback drajvera, SMB 24H2 i TCP optimizacija (1,249 linija)
├── WIS.Update.psm1          # Dvosmerni HTTP/NAS .msu keš, <Model>.tar, HARVESTING, WU COM API (4,177 linija)
├── WIS.Apps.psm1            # 9 StepGuard koraka: Chrome keš, BOM-free lokalni Office ODT, Acrobat, BatteryBar (4,157 linija)
├── WIS.Branding.psm1        # Regionalne postavke, automatsko DPI skaliranje, OEM brending, Interim Reboot (792 linija)
├── WIS.Cleanup.psm1         # Faza 6 finalizacija, DMTF izveštaj, Anti-Leak brisanje tajni, UAC restore (1,332 linija)
├── WIS.Telemetry.psm1       # ServiceHub REST/Heartbeat klijent, DMTF ID i daljinske komande (reboot_uefi) (1,106 linija)
├── WIS.UI.psm1              # Povezivanje PowerShell radnika sa WPF UI modelom i hardverskim profilom (737 linija)
└── WIS.ScreenCapture.ps1    # 1 Hz GDI+ Live Screen Mirror radnik u RAM memoriji (0 Disk I/O) (1,033 linija)
```

### Matrica Odgovornosti i Ključnih Funkcija po Modulima:

| Modul / Radnik | Veličina / Linije | Ključne Eksportovane Funkcije | Primarna Odgovornost |
| :--- | :--- | :--- | :--- |
| **`WIS.Common.psm1`** | 42,135 B (1,025 lin.) | `Send-IpcMessage`, `Send-IpcProgress`, `Send-IpcStatus`, `Send-IpcStep`, `Wait-ServisUICooperativePause`, `Write-ServisLog`, `Rotate-ServisLog`, `Save-ServisDeployState` | Jedinstveni UTF-8 JSON IPC cevovod ka `WIS.UI.exe`, emitovanje koraka, kooperativna pauza, rotacija logova na 10 MB i atomska perzistencija stanja. |
| **`WIS.StatusContract.psm1`** | 1,573 B (29 lin.) | `New-WISDiagnosticsContract` | Kanonski statusni ugovor: odvaja uspeh instalacije od potvrde hlađenja (`cooling_verified = true` isključivo kada je senzor dostupan i test `PASS`; u suprotnom `manual_check_required = true`). |
| **`WIS.StepGuard.psm1`** | 24,472 B (647 lin.) | `Invoke-WisStep`, `Test-WisForceRerun`, `Get-WisPendingRebootState`, `Read-WisStepJournal`, `Write-WisStepJournal`, `Get-WisPhaseStepSummary` | Idempotentni `StepGuard` motor: pre-check, izvršavanje, atomski upis u `step-journal.json` i post-verifikacija za 21 korak Faza 2–4. |
| **`WIS.Core.psm1`** | 105,117 B (2,129 lin.) | `Get-ServisNasSettings`, `Sync-ProgramiFromNas`, `Grant-SetupAclPermissions`, `Wait-MsiMutexAvailable`, `Remove-PendingRebootLocks`, `Disable-QuickEdit`, `Test-IsSingleInstance` | Učitavanje `Wifi-Servis.ini`, autentifikacija na `WIS`/`Programi` (sa `Error 1219` zaštitom), dodela SID NTFS dozvola (`*S-1-5-18`), čekanje `Global\_MSIExecute` mutexa i P/Invoke gašenje QuickEdit moda. |
| **`WIS.Hardware.psm1`** | 136,306 B (2,902 lin.) | `Get-HardwareDiagnosticsInfo`, `Get-MonitorEdidDetails`, `Test-WisDeviceManagerClean`, `Start-ServisStressTest`, `Save-ServisStressTestResult`, `Install-DriverFromMsCatalog` | Hibridna NVMe/SATA S.M.A.R.T. trijaža, EDID dekodiranje, čitanje Pre-OOBE `autopilot_report.json` keša bez degradacije, autoritativna Device Manager provera, 30s `RunspacePool` stres test sa 98°C abortom i MS Catalog HWID fallback. |
| **`WIS.Network.psm1`** | 55,195 B (1,249 lin.) | `Connect-ServisWifi`, `Install-NetDriverFallback`, `Optimize-ServisNetworkStack`, `Get-NetworkDetailStatus`, `Test-Internet` | Povezivanje na 5 GHz / 2.4 GHz Wi-Fi sa XML escaping-om, PnP instalacija mrežnih `.inf` drajvera iz `C:\setup\Drivers\Net`, otključavanje SMB 24H2 i TCP steka. |
| **`WIS.Update.psm1`** | 208,224 B (4,177 lin.) | `Invoke-NasUpdatePrePatch`, `Invoke-ModelDriversPreInstall`, `Export-ServisHardwareDrivers`, `Invoke-WindowsUpdatePhase`, `Wait-WindowsUpdateCompletionBarrier`, `Invoke-WindowsUpdateSecondPass` | 6 `StepGuard` koraka: dvosmerni HTTP/NAS `.msu`/`.cab` keš (`OSBuild` + SHA-256 `index.json`, `Mode=All`), dvosmerni HTTP `<Model>.tar` keš (`ShareName=Servis`, `DriversSubDir=Drivers\Harvested\Models`), batch `/api/drivers/resolve`, UI status `HARVESTING` i WU barijera. |
| **`WIS.Apps.psm1`** | 232,223 B (4,157 lin.) | `Get-WisAppPresence`, `Get-ServisProgramiRoot`, `Invoke-InstallerWithLocalFallback`, `Install-AllServisPrograms`, `Invoke-OfficeInstall`, `Set-BatteryBarConfiguration`, `Assert-InstalledApplicationsReality` | 9 `StepGuard` koraka: lokalni Chrome keš, BOM-free lokalni SSD `SourcePath` za Office ODT (`Office2024_Install.xml`), 6-stepeni PDF lock, BatteryBar Solo XML, per-app `Shortcuts` provera i Reality Check. |
| **`WIS.Branding.psm1`** | 39,869 B (792 lin.) | `Invoke-SystemOptimizationAndBranding`, `Set-ServisAutoDpiScaling`, `Invoke-ServisInterimReboot`, `Set-DefenderExclusions` | 6 `StepGuard` koraka: srpska/US tastatura, 24h sat, automatsko DPI skaliranje (100%/125%/150%/200%), OEM brendiranje i upis `interim_reboot_required` stanja. |
| **`WIS.Cleanup.psm1`** | 73,327 B (1,332 lin.) | `Invoke-ServisCleanupAndFinalize`, `Clear-ServisWifiSecrets`, `Export-ServisSummaryReport`, `Restore-ServisUacDefaults` | Anti-Leak brisanje servisnog Wi-Fi profila i `Wifi-Servis.ini`, raskidanje SMB sesija, lokalni servisni karton, vraćanje UAC-a na fabrički nivo (`5`/`1`) i samouništenje `C:\setup`. |
| **`WIS.Telemetry.psm1`** | 47,254 B (1,106 lin.) | `Send-ServisHeartbeat`, `Get-HardwareTelemetryMetrics`, `Invoke-ServisRemoteCommand`, `Send-FleetFinalReport`, `Get-FleetDeviceId` | Slanje 2s pulsa sa senzorikom na ServiceHub, izvršavanje daljinskih komandi (`reboot_uefi`, `reboot_pxe`, `cancel`) i slanje završnog DMTF servisnog kartona (`TK-{Chassis}-{Serial}`). |
| **`WIS.UI.psm1`** | 34,884 B (737 lin.) | `Initialize-ServisUI`, `Update-ServisUI`, `Close-ServisUI` | Sinhronizacija PowerShell radnika sa WPF interfejsom i obogaćivanje telemetrijskog modela hardverskim podacima. |
| **`WIS.ScreenCapture.ps1`** | 40,738 B (1,033 lin.) | `Start-ServisScreenCapture`, `Stop-ServisScreenCapture`, `Capture-ServisScreenFrame`, `Send-ServisScreenFrame` | Pozadinski 1 Hz radnik za živo ogledalo ekrana: GDI+ `CopyFromScreen`, skaliranje na 1280 px, JPEG kompresija u `MemoryStream` (0 Disk I/O) i slanje na `/api/devices/{id}/screen-frame` uz `ParentPid` nadzor. |

---

## 🛡️ 2. Samostalni Forenzički Podsistem za Autopilot (`Tools/AutopilotAuditor/` & `Provera_autopilota.ps1`)

Za detekciju korporativnog Microsoft Autopilot / Intune MDM zaključavanja, WIS poseduje dvostepeni mehanizam ([[decisions/ADR-0158-autopilot-active-cloud-trigger-and-nas-fallback]]):
1. **Nativni C# izvršni alat `WIS-AutopilotAuditor.exe` v3.0.0 (`Tools/AutopilotAuditor/`, `447,488 B`, `8` `.cs` fajlova / `2,333` linija C# koda):**
   - Kompajliran za .NET Framework 4.8 bez zavisnosti od PowerShell-a i čuva potvrđene Pre-OOBE/OOBE izveštaje (`TryLoadCachedReport`, `SaveCachedReport` -> `C:\Windows\Logs\WIS\autopilot_report.json`, `C:\setup\autopilot_report.json` i `AutopilotDDSZTDFile.preoobe.json`).
   - Podržava grafički WPF režim, konzolni CLI režim (`--cli`, `--json`), izvoz u HTML (`--export-html`) i Intune 4K Hardware Hash CSV (`--export-hash`), uz automatski WinPE fallback.
   - Sadrži `HardwareInspector.cs`, `LocalForensicsInspector.cs`, `CloudZtdInspector.cs` (`TryParseZtdJsonContent` označava prazan `AutopilotDDSZTDFile.json` / prazan `CloudAssignedTenantId` kao `ConfirmedFree`, uz Silent OOBE servise i Event `806`/`807 - ZtdDeviceIsNotRegistered`), `DecisionMatrix.cs` (`LOCKED_TENANT`, `HIDDEN_LOCK`, `RELEASED_LOCAL_TRACES`, `CONFIRMED_CLEAN`, `UNVERIFIED_CLEAN`) i `ReportExporter.cs`.
2. **PowerShell skripta `UI/Skripte/Provera_autopilota.ps1` (`973`/`974` linija, `56,662 B`, bez UTF-8 BOM-a):**
   - Usklađena sa C# auditorom (`Resolve-ZtdJsonContent`, `Test-SavedAutopilotReportConfirmed`), obezbeđuje zaštitu potvrđenog keša od degradacije i radi kao pouzdan fallback unutar `Phase1-Network.ps1` i `SetupComplete.ps1`.

---

## 🛠️ 3. Kanonski Build, Release i Media Pajplajn (`v3.0.0`)

Build i pakovanje sistema izvršavaju se kroz determinističke alate u okviru četiri razvojne celine:

1. **Usklađivanje verzija (`ISO/Skripte/Sync-Versions.py`):**
   - Čita kanonski `version.json` (`release_package: 3.0.0`, `iso: 3.0.0`, `launcher: 3.0.0`, `ui: 3.0.0`, `servicehub: 3.3.0`, `autopilot_auditor: 3.0.0`, `min_launcher_version: 2.2.17`) i proverava usklađenost komponentnih deklaracija (`--check`).
2. **Kompilacija C# Binaraca (`ISO/Skripte/Build-WIS.ps1`, `UI/Build-UI.py`, `UI/HwTest/Build-HwTest.py`, `Launcher/Build-Launcher.py`, `Tools/AutopilotAuditor/Build-AutopilotAuditor.py`):**
   - `WIS.UI.exe` (**v3.0.0**, `2,977,280 B`; ukupno `UI/` obuhvata **`36` C#/XAML fajlova / `42,431` linija / `2,003,927 B`**: **`15` fajlova u korenu / `21,147` linija** uključujući `MainWindow.HwTest.cs` `425` lin., `LabelPrintService.cs` `1,401` lin. za Star TSP700II, `ThermalSensorEngine.cs` `1,426` lin. sa refleksijom bez ugrađenog `WinRing0`, `LocalizationService.cs` `435/435/73` SR/ZH/EN ključeva; plus **`21` fajl u `UI/HwTest/` / `21,284` linija** sa resursima `Assets.zxing.dll`, `Assets.keyboard_layouts.json`, `Assets.digits_en.0..9.wav`, i samostalnim binarcem `WIS.HwTest.exe` od `1,736,704 B`).
   - `WIS.Launcher.exe` (**v3.0.0**, `468,480 B`, `6` produkcionih C# fajlova / `4,739` linija, .NET Framework 4.0, RSA-2048 `trusted_public_key.xml`, `StartMasterUIAndExit` / `GetRemoteSmbVersion` za `\\host\servis` i `\\host\WIS`, `WIS.ico`).
   - `WIS-AutopilotAuditor.exe` (**v3.0.0**, `447,488 B`, `8` `.cs` fajlova / `2,333` linija).
3. **Potpisivanje i Objavljivanje Izdanja (`ISO/Skripte/Release-WIS.py` & `ISO/Skripte/Build-PreviewMedia.py`):**
   - Pakuje `wis-payload.zip` (`68,475,013 B`, `78` fajlova u `v3.0.0`, `release_id: e98859234b5c500f9752ddd745251b6fcfec47e0-ea2ca92892249cda`) iz kanonskih izvora, generiše SHA-256 kontrolne sume svih fajlova i asimetrično potpisuje `payload_meta.json` privatnim RSA-2048 PKCS#1 v1.5 ključem (`~/.config/wis-signing/payload_signing_private.pem`).
4. **Priprema WinPE i Hibridnog UDF ISO Medijuma (`ISO/Skripte/Patch-BootWim.py` & `ISO/Skripte/Build-Iso.py`):**
   - `Patch-BootWim.py` (`1,313` linija) ažurira oba indeksa u `sources/boot.wim` (`~582 MiB`, LAN-only `15` fajlova, `000_wis_net1ix64.inf_guard`, **32 provere**).
   - `Build-Iso.py` (`1,103` linije) generiše hibridni UDF (`ECMA-167`) + El Torito UEFI (`0xEF`) + Legacy BIOS (`0x00`) Zlatni Lean ISO (`WIS-Win11-Pro-10.0.26200.8037-Lean.iso`, `6,522,753,024 B` / `6.07 GB`).

---

## 🧪 4. Višeslojna QA Verifikacija (Linux host vs Windows/PXE lab)

- **Linux statičke i unit provere:**
  - `python3 Launcher/Tests/run_tests.py`: **31/31** funkcionalnih testova (`PASS`).
  - `python3 ISO/Skripte/Verify-CSharpSyntax.py`: **48/48** C# fajlova (`UI/`, `UI/HwTest/`, `Launcher/`, `PASS`).
  - `python3 -m unittest UI/Tests/Test-HwTestLogic.py UI/Tests/Test-AutopilotPreOobeCache.py UI/Tests/Test-UpdateAndDriversLogic.py`: **29/29** regresionih testova (`PASS`).
  - `cd ServiceHub && python3 -m unittest discover -s tests`: **279** unittest metoda (`30` fajlova, `7,614` linija); bez opcionih `Pillow`/`FastAPI`/`Node` paketa na Linux hostu izvršava se **259 PASS, 20 skipped** (`0` padova).
  - `python3 ISO/Skripte/Verify-PowerShellSyntax.py` (**22/22 PASS**), `python3 ISO/Skripte/Verify-CSharpSyntax.py` (**25/25 PASS**) i `python3 ISO/Skripte/Sync-Versions.py --check` (`PASS`).
- **Windows PowerShell 5.1 & PXE Zero-Touch ugovori (`NOT_RUN` na Linuxu):**
  - `UI/Tests/Test-PowerShell51Parse.ps1` i `UI/Tests/Test-StepGuard.ps1` zahtevaju Windows PS 5.1 host.
  - `ISO/Skripte/Build-WIS.ps1` zahteva Windows MSBuild okruženje.
  - `ISO/Tests/PxeZeroTouch/verify_acceptance.py --check-lock` definiše **22** E2E scenarija (`PXE-01..PXE-22`) i vraća `GATE_FAILED` (`exit 1`) dok ne postoji laboratorijski `LOCK.json`.

---

## 🔗 Povezane Beleške
- Pregled Arhitekture: [[architecture/overview]]
- Pajplajn Izvršavanja: [[architecture/pipeline]]
- Centralizovani Servisni Hub: [[architecture/servis-hub]]
- StepGuard i UI v2.3.0: [[decisions/ADR-0181-stepguard-precheck-hardware-identity-and-ui-v23]]
- Ručni i automatski `WIS.HwTest`: [[decisions/ADR-0189-manual-hardware-test-suite]]
- `WIS v3.0.0` integrisana Faza 5, dvosmerni keš i Star TSP700II: [[decisions/ADR-0225-wis-v300-embedded-hwtest-bidirectional-wu-and-preoobe-autopilot]]
- WinPE I219-LM Guard & Paralelni PXE: [[decisions/ADR-0184-winpe-i219lm-driverstore-guard-and-parallel-pxe]]
- Sanacija Faze 4 i Office 2024 ODT: [[decisions/ADR-0185-phase4-office-odt-bom-free-and-local-nvme-sourcepath]]
