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

# Modularni Sistem i Build Pajplajn: WIS (v2.19.0)

Kako bi se obezbedila maksimalna pouzdanost, čitljivost i striktna kompatibilnost sa **Windows PowerShell 5.1** i **.NET Framework 4.0 / 4.8**, **WIS** (*Windows Installation Assistant*) je organizovan u **četiri glavne razvojne celine** (`ISO/`, `Launcher/`, `UI/`, `ServiceHub/`) i namenski forenzički podsistem `Tools/AutopilotAuditor/`.

Nekadašnji monolitni skript `Install-ServisApps.ps1`, legacy `GWADT.*.psm1` shimovi i stari alati `Build-DeployScript.py` / `Test-GWADTModules.py` su trajno penzionisani ([[decisions/ADR-0076-decoupling-monolith-into-phase-scripts-ui-orchestrator]], [[decisions/ADR-0131-diagnostics-redesign-6phase-pipeline-and-wis-cleanup]], [[decisions/ADR-0137-pure-wis-cleanup-and-gwadt-retirement]]).

---

## 🗂️ 1. Struktura PowerShell 5.1 Modula (`UI/Skripte/Modules/`)

Celokupna klijentska poslovna logika u Windows okruženju podeljena je na **12 specijalizovanih PowerShell 5.1 modula (`.psm1`)** i **1 pozadinskog radnika (`WIS.ScreenCapture.ps1`)** — ukupno **13 fajlova** i **19,863 linija koda (`971,155 B`)**, obavezno čuvanih u **UTF-8 with BOM + CRLF** formatu bez tipografskih navodnika (`0x93`/`0x94`) ili 4-bajtnih emodžija ([[decisions/ADR-0185-phase4-office-odt-bom-free-and-local-nvme-sourcepath]]):

```text
UI/Skripte/Modules/
├── WIS.Common.psm1          # JSON IPC striming (Send-IpcStep), pauza, rotacija logova i deploy-state.json (1,025 linija)
├── WIS.StatusContract.psm1  # Kanonski statusni ugovor: razdvajanje DONE statusa od cooling_verified (29 linija)
├── WIS.StepGuard.psm1       # Idempotentni StepGuard motor, step-journal.json i pending reboot detekcija (647 linija)
├── WIS.Core.psm1            # Jezgro: Wifi-Servis.ini, SMB auth (Error 1219 zaštita), NTFS SID ACL, MSI mutex (2,129 linija)
├── WIS.Hardware.psm1        # CIM/WMI, S.M.A.R.T., EDID, Test-WisDeviceManagerClean, RunspacePool stres test (2,826 linija)
├── WIS.Network.psm1         # Wi-Fi povezivanje, PnP fallback drajvera, SMB 24H2 i TCP optimizacija (1,249 linija)
├── WIS.Update.psm1          # NAS .msu Pre-Patch, .tar drajveri, WU COM API (6h TTL + wu-bg-status.json) (2,801 linija)
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
| **`WIS.StepGuard.psm1`** | 23,822 B (647 lin.) | `Invoke-WisStep`, `Test-WisForceRerun`, `Get-WisPendingRebootState`, `Read-WisStepJournal`, `Write-WisStepJournal`, `Get-WisPhaseStepSummary` | Idempotentni `StepGuard` motor: pre-check, izvršavanje, atomski upis u `step-journal.json` i post-verifikacija za 21 korak Faza 2–4. |
| **`WIS.Core.psm1`** | 105,117 B (2,129 lin.) | `Get-ServisNasSettings`, `Sync-ProgramiFromNas`, `Grant-SetupAclPermissions`, `Wait-MsiMutexAvailable`, `Remove-PendingRebootLocks`, `Disable-QuickEdit`, `Test-IsSingleInstance` | Učitavanje `Wifi-Servis.ini`, autentifikacija na `WIS`/`Programi` (sa `Error 1219` zaštitom), dodela SID NTFS dozvola (`*S-1-5-18`), čekanje `Global\_MSIExecute` mutexa i P/Invoke gašenje QuickEdit moda. |
| **`WIS.Hardware.psm1`** | 130,917 B (2,826 lin.) | `Get-HardwareDiagnosticsInfo`, `Get-MonitorEdidDetails`, `Test-WisDeviceManagerClean`, `Start-ServisStressTest`, `Save-ServisStressTestResult`, `Install-DriverFromMsCatalog` | Hibridna NVMe/SATA S.M.A.R.T. trijaža, EDID dekodiranje, autoritativna Device Manager provera, 30s Multi-Core `RunspacePool` stres test sa 98°C abortom i MS Catalog HWID fallback. |
| **`WIS.Network.psm1`** | 55,195 B (1,249 lin.) | `Connect-ServisWifi`, `Install-NetDriverFallback`, `Optimize-ServisNetworkStack`, `Get-NetworkDetailStatus`, `Test-Internet` | Povezivanje na 5 GHz / 2.4 GHz Wi-Fi sa XML escaping-om, PnP instalacija mrežnih `.inf` drajvera iz `C:\setup\Drivers\Net`, otključavanje SMB 24H2 i TCP steka. |
| **`WIS.Update.psm1`** | 144,104 B (2,801 lin.) | `Invoke-NasUpdatePrePatch`, `Invoke-ModelDriversPreInstall`, `Export-ServisHardwareDrivers`, `Invoke-WindowsUpdatePhase`, `Wait-WindowsUpdateCompletionBarrier`, `Invoke-WindowsUpdateSecondPass` | 6 `StepGuard` koraka: lokalni `.msu` pre-patch, `<Model>.tar` drajveri za ~3s, samoučeći eksport drajvera, WU COM API (6h TTL + `wu-bg-status.json`) i WU barijera pre Faze 5. |
| **`WIS.Apps.psm1`** | 232,220 B (4,157 lin.) | `Get-WisAppPresence`, `Get-ServisProgramiRoot`, `Invoke-InstallerWithLocalFallback`, `Install-AllServisPrograms`, `Invoke-OfficeInstall`, `Set-BatteryBarConfiguration`, `Assert-InstalledApplicationsReality` | 9 `StepGuard` koraka: lokalni Chrome keš, BOM-free lokalni SSD `SourcePath` za Office ODT (`Office2024_Install.xml`), 6-stepeni PDF lock, BatteryBar Solo XML, per-app `Shortcuts` provera i Reality Check. |
| **`WIS.Branding.psm1`** | 39,869 B (792 lin.) | `Invoke-SystemOptimizationAndBranding`, `Set-ServisAutoDpiScaling`, `Invoke-ServisInterimReboot`, `Set-DefenderExclusions` | 6 `StepGuard` koraka: srpska/US tastatura, 24h sat, automatsko DPI skaliranje (100%/125%/150%/200%), OEM brendiranje i upis `interim_reboot_required` stanja. |
| **`WIS.Cleanup.psm1`** | 73,327 B (1,332 lin.) | `Invoke-ServisCleanupAndFinalize`, `Clear-ServisWifiSecrets`, `Export-ServisSummaryReport`, `Restore-ServisUacDefaults` | Anti-Leak brisanje servisnog Wi-Fi profila i `Wifi-Servis.ini`, raskidanje SMB sesija, lokalni servisni karton, vraćanje UAC-a na fabrički nivo (`5`/`1`) i samouništenje `C:\setup`. |
| **`WIS.Telemetry.psm1`** | 47,254 B (1,106 lin.) | `Send-ServisHeartbeat`, `Get-HardwareTelemetryMetrics`, `Invoke-ServisRemoteCommand`, `Send-FleetFinalReport`, `Get-FleetDeviceId` | Slanje 2s pulsa sa senzorikom na ServiceHub, izvršavanje daljinskih komandi (`reboot_uefi`, `reboot_pxe`, `cancel`) i slanje završnog DMTF servisnog kartona (`TK-{Chassis}-{Serial}`). |
| **`WIS.UI.psm1`** | 34,884 B (737 lin.) | `Initialize-ServisUI`, `Update-ServisUI`, `Close-ServisUI` | Sinhronizacija PowerShell radnika sa WPF interfejsom i obogaćivanje telemetrijskog modela hardverskim podacima. |
| **`WIS.ScreenCapture.ps1`** | 40,738 B (1,033 lin.) | `Start-ServisScreenCapture`, `Stop-ServisScreenCapture`, `Capture-ServisScreenFrame`, `Send-ServisScreenFrame` | Pozadinski 1 Hz radnik za živo ogledalo ekrana: GDI+ `CopyFromScreen`, skaliranje na 1280 px, JPEG kompresija u `MemoryStream` (0 Disk I/O) i slanje na `/api/devices/{id}/screen-frame` uz `ParentPid` nadzor. |

---

## 🛡️ 2. Samostalni Forenzički Podsistem za Autopilot (`Tools/AutopilotAuditor/` & `Provera_autopilota.ps1`)

Za detekciju korporativnog Microsoft Autopilot / Intune MDM zaključavanja, WIS poseduje dvostepeni mehanizam ([[decisions/ADR-0158-autopilot-active-cloud-trigger-and-nas-fallback]]):
1. **Nativni C# izvršni alat `WIS-AutopilotAuditor.exe` v2.0.0 (`Tools/AutopilotAuditor/`, `442,368 B`, `8` `.cs` fajlova / `1,989` linija C# koda):**
   - Kompajliran za .NET Framework 4.8 bez zavisnosti od PowerShell-a.
   - Podržava grafički WPF režim, konzolni CLI režim (`--cli`, `--json`), izvoz u HTML (`--export-html`) i Intune 4K Hardware Hash CSV (`--export-hash`), uz automatski WinPE fallback.
   - Sadrži `HardwareInspector.cs`, `LocalForensicsInspector.cs`, `CloudZtdInspector.cs` (Silent OOBE servisi, Task Scheduler provisioning zadaci i Event 806/807), `DecisionMatrix.cs` (`LOCKED_TENANT`, `HIDDEN_LOCK`, `RELEASED_LOCAL_TRACES`, `CONFIRMED_CLEAN`, `UNVERIFIED_CLEAN`) i `ReportExporter.cs`.
2. **PowerShell skripta `UI/Skripte/Provera_autopilota.ps1` (`798` linija, `45,853 B`):**
   - Služi kao fallback u `WIS.UI.exe` (`FindAutopilotTool`) i samostalna skripta u `C:\setup` i na korenu ISO/NAS medijuma.

---

## 🛠️ 3. Kanonski Build, Release i Media Pajplajn (`v2.19.0`)

Build i pakovanje sistema izvršavaju se kroz determinističke alate u okviru četiri razvojne celine:

1. **Usklađivanje verzija (`ISO/Skripte/Sync-Versions.py`):**
   - Čita kanonski `version.json` (`release_package: 2.19.0`, `iso: 2.18.0`, `launcher: 2.2.17`, `ui: 2.3.0`, `servicehub: 3.2.0`, `autopilot_auditor: 2.0.0`) i proverava usklađenost komponentnih deklaracija (`--check`).
2. **Kompilacija C# Binaraca (`ISO/Skripte/Build-WIS.ps1`, `UI/Build-UI.py`, `Launcher/Build-Launcher.py`, `Tools/AutopilotAuditor/Build-AutopilotAuditor.py`):**
   - `WIS.UI.exe` (**v2.3.0**, `1,200,128 B`, `13` fajlova / `18,515` linija, C# 5.0 / .NET Framework 4.8, dual `App.config` `<supportedRuntime>`, `HardwareIdentityService.cs`, `StepItem` ekspander, `414` SR/ZH ključa u `LocalizationService.cs`, `WIS.ico`).
   - `WIS.Launcher.exe` (**v2.2.17**, `468,480 B`, `6` produkcionih C# fajlova / `4,739` linija, .NET Framework 4.0, RSA-2048 `trusted_public_key.xml`, `StartMasterUIAndExit` / `GetRemoteSmbVersion` za `\\host\servis` i `\\host\WIS`, `WIS.ico`).
   - `WIS-AutopilotAuditor.exe` (**v2.0.0**, `442,368 B`, `8` `.cs` fajlova / `1,989` linija).
3. **Potpisivanje i Objavljivanje Izdanja (`ISO/Skripte/Release-WIS.py` & `ISO/Skripte/Build-PreviewMedia.py`):**
   - Pakuje `wis-payload.zip` (`64,674,440 B` u `v2.19.0`) iz kanonskih izvora, generiše SHA-256 kontrolne sume svih fajlova i asimetrično potpisuje `payload_meta.json` privatnim RSA-2048 PKCS#1 v1.5 ključem (`~/.config/wis-signing/payload_signing_private.pem`).
4. **Priprema WinPE i Hibridnog UDF ISO Medijuma (`ISO/Skripte/Patch-BootWim.py` & `ISO/Skripte/Build-Iso.py`):**
   - `Patch-BootWim.py` (`1,313` linija) ažurira oba indeksa u `sources/boot.wim` (`~582 MiB`, LAN-only `15` fajlova, `000_wis_net1ix64.inf_guard`, **32 provere**).
   - `Build-Iso.py` (`1,103` linije) generiše hibridni UDF (`ECMA-167`) + El Torito UEFI (`0xEF`) + Legacy BIOS (`0x00`) Zlatni Lean ISO (`WIS-Win11-Pro-10.0.26200.8037-Lean.iso`, `6,522,753,024 B` / `6.07 GB`).

---

## 🧪 4. Višeslojna QA Verifikacija (Linux host vs Windows/PXE lab)

- **Linux statičke i unit provere:**
  - `python3 Launcher/Tests/run_tests.py`: **31/31** funkcionalnih testova (`PASS`).
  - `cd ServiceHub && python3 -m unittest discover -s tests`: **259** unittest metoda (`27` fajlova, `6,505` linija); bez opcionih `Pillow`/`FastAPI`/`Node` paketa na Linux hostu izvršava se **239 PASS, 20 skipped** (`0` padova).
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
- WinPE I219-LM Guard & Paralelni PXE: [[decisions/ADR-0184-winpe-i219lm-driverstore-guard-and-parallel-pxe]]
- Sanacija Faze 4 i Office 2024 ODT: [[decisions/ADR-0185-phase4-office-odt-bom-free-and-local-nvme-sourcepath]]
