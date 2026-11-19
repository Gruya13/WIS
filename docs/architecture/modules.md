---
title: "Modularni Sistem i Build Pajplajn (Modules & Build System)"
type: architecture
created: 2026-09-14
updated: 2026-11-18
tags:
  - architecture
  - modules
  - build
  - powershell
  - python
  - csharp
---

# Modularni Sistem i Build Pajplajn: WIS (v3.1.0)

Kako bi se obezbedila maksimalna pouzdanost, čitljivost i striktna kompatibilnost sa **Windows PowerShell 5.1** i **.NET Framework 4.0 / 4.8**, **WIS** (*Windows Installation Assistant*) je organizovan u **četiri glavne razvojne celine** (`ISO/`, `Launcher/`, `UI/`, `ServiceHub/`) i namenski forenzički podsistem `Tools/AutopilotAuditor/` (dok `UI/HwTest/` pruža modularni paket za automatske i interaktivne testove hardvera).

Nekadašnji monolitni skript `Install-ServisApps.ps1`, legacy `GWADT.*.psm1` shimovi i stari alati `Build-DeployScript.py` / `Test-GWADTModules.py` su trajno penzionisani ([[decisions/ADR-0076-decoupling-monolith-into-phase-scripts-ui-orchestrator]], [[decisions/ADR-0131-diagnostics-redesign-6phase-pipeline-and-wis-cleanup]], [[decisions/ADR-0137-pure-wis-cleanup-and-gwadt-retirement]]).

---

## 🗂️ 1. Struktura PowerShell 5.1 Modula (`UI/Skripte/Modules/`)

Celokupna klijentska poslovna logika u Windows okruženju podeljena je na **12 specijalizovanih PowerShell 5.1 modula (`.psm1`)** i **1 pozadinskog radnika (`WIS.ScreenCapture.ps1`)** — ukupno **13 fajlova** i **23,110 linija koda (`1,135,873 B`)**, obavezno čuvanih u **UTF-8 with BOM + CRLF** formatu bez tipografskih navodnika (`0x93`/`0x94`) ili 4-bajtnih emodžija ([[decisions/ADR-0185-phase4-office-odt-bom-free-and-local-nvme-sourcepath]]):

```text
UI/Skripte/Modules/
├── WIS.Common.psm1          # JSON IPC (Send-IpcStep), WIS Benchmark tracker, pauza, rotacija logova i deploy-state.json (1,491 linija)
├── WIS.StatusContract.psm1  # Kanonski statusni ugovor: razdvajanje DONE statusa od cooling_verified (29 linija)
├── WIS.StepGuard.psm1       # Idempotentni StepGuard motor, step-journal.json i pending reboot detekcija (648 linija)
├── WIS.Core.psm1            # Jezgro: Wifi-Servis.ini, SMB auth (Error 1219, Test-ProgramiDirComplete), NTFS SID ACL, MSI mutex (2,158 linija)
├── WIS.Hardware.psm1        # CIM/WMI, S.M.A.R.T., EDID, Model DNA, Select-RelevantDriverInfFiles, StressTest, MS Catalog (3,213 linija)
├── WIS.Network.psm1         # Wi-Fi povezivanje, PnP fallback drajvera, SMB 24H2 i TCP optimizacija (1,249 linija)
├── WIS.Update.psm1          # Dvosmerni HTTP/NAS .msu keš, <Model>.tar (`tar`-first, KeepAlive=$false), GenericPC & 5.0 GB guard (4,691 linija)
├── WIS.Apps.psm1            # 9 StepGuard koraka: Chrome keš, offline VCRedist + winget timeout, Office ODT, Acrobat + 1x retry (4,372 linija)
├── WIS.Branding.psm1        # Regionalne postavke, automatsko DPI skaliranje, OEM brending, debloat-done.flag, Interim Reboot (878 linija)
├── WIS.Cleanup.psm1         # Faza 6 finalizacija, DMTF izveštaj, brza WU barijera, Anti-Leak, UAC restore (1,415 linija)
├── WIS.Telemetry.psm1       # ServiceHub REST/Heartbeat klijent, queued_commands obrada, Benchmark & FinalReport (1,196 linija)
├── WIS.UI.psm1              # Povezivanje PowerShell radnika sa WPF UI modelom i hardverskim profilom (737 linija)
└── WIS.ScreenCapture.ps1    # 1 Hz GDI+ Live Screen Mirror radnik u RAM memoriji (0 Disk I/O) (1,033 linija)
```

### Matrica Odgovornosti i Ključnih Funkcija po Modulima:

| Modul / Radnik | Veličina / Linije | Ključne Eksportovane Funkcije | Primarna Odgovornost |
| :--- | :--- | :--- | :--- |
| **`WIS.Common.psm1`** | 62,835 B (1,491 lin.) | `Send-IpcMessage`, `Send-IpcStep`, `Start-WisPhaseBenchmark`, `Complete-WisPhaseBenchmark`, `Add-WisBenchmarkTransfer`, `Get-WisBenchmarkSummary`, `Write-ServisLog`, `Save-ServisDeployState` | Jedinstveni UTF-8 JSON IPC cevovod ka `WIS.UI.exe`, **WIS Benchmark** evidencija faza i LAN/WAN prenosa (`benchmark.json`), kooperativna pauza, rotacija logova na 10 MB i atomska perzistencija stanja (uklj. `ThermalCriticalContinue`). |
| **`WIS.StatusContract.psm1`** | 1,622 B (29 lin.) | `New-WISDiagnosticsContract` | Kanonski statusni ugovor: odvaja uspeh instalacije od potvrde hlađenja (`cooling_verified = true` isključivo kada je senzor dostupan i test `PASS`; u suprotnom ili pri `THERMAL_CRITICAL` nastavku `manual_check_required = true`). |
| **`WIS.StepGuard.psm1`** | 24,542 B (648 lin.) | `Invoke-WisStep`, `Test-WisForceRerun`, `Get-WisPendingRebootState`, `Read-WisStepJournal`, `Write-WisStepJournal`, `Get-WisPhaseStepSummary` | Idempotentni `StepGuard` motor: pre-check, izvršavanje, atomski upis u `step-journal.json` (sa `AddStatement()` podrškom za daljinski `skip_step`) i post-verifikacija za 21 korak Faza 2–4. |
| **`WIS.Core.psm1`** | 106,768 B (2,158 lin.) | `Get-ServisNasSettings`, `Sync-ProgramiFromNas`, `Test-ProgramiDirComplete`, `Grant-SetupAclPermissions`, `Wait-MsiMutexAvailable`, `Disable-QuickEdit`, `Test-IsSingleInstance` | Učitavanje `Wifi-Servis.ini` (`servis` -> `admin` normalizacija), autentifikacija na `WIS`/`Programi`/`Servis` (`Error 1219` zaštita), provera kompletnosti `Programi` kad je Lean USB uboden, SID NTFS dozvole i mutexi. |
| **`WIS.Hardware.psm1`** | 149,945 B (3,213 lin.) | `Get-HardwareDiagnosticsInfo`, `Select-RelevantDriverInfFiles`, `Send-WisModelDnaToHub`, `Test-WisDeviceManagerClean`, `Start-ServisStressTest`, `Install-DriverFromMsCatalog` | S.M.A.R.T. i EDID dijagnostika (`Facial Recognition` filter, 2s `PnP`/`WMI_ACPI` zaštita), Pre-OOBE `autopilot_report.json` keš, **SUBSYS/HWID filtriranje multi-OEM `.cab` paketa** bez comma-wrap baga, slanje `Model DNA` + `model_aliases` parova, 30s `RunspacePool` stres test i MS Catalog fallback. |
| **`WIS.Network.psm1`** | 55,195 B (1,249 lin.) | `Connect-ServisWifi`, `Install-NetDriverFallback`, `Optimize-ServisNetworkStack`, `Get-NetworkDetailStatus`, `Test-Internet` | Povezivanje na 5 GHz / 2.4 GHz Wi-Fi sa XML escaping-om, PnP instalacija mrežnih `.inf` drajvera iz `C:\setup\Drivers\Net`, otključavanje SMB 24H2 i TCP steka. |
| **`WIS.Update.psm1`** | 239,936 B (4,691 lin.) | `Test-IsGenericComputerModel`, `Invoke-NasUpdatePrePatch`, `Invoke-ModelDriversPreInstall`, `Export-ServisHardwareDrivers`, `Restore-ServisNetworkConnectivityIfNeeded`, `Invoke-WindowsUpdatePhase`, `Wait-WindowsUpdateCompletionBarrier`, `Invoke-WindowsUpdateSecondPass` | 6 `StepGuard` koraka: dvosmerni HTTP/NAS `.msu`/`.cab` i `<Model>.tar` keš (`tar`-first, `KeepAlive=$false`), `90000 ms` `WaitOne()`, LAN reconnect oporavak, blokada `GenericPC`, `*AggregatedMetadata*.cab` filter, `-SkipDriverPreInstall` u Fazi 6, berba sa **`5.0 GB`** (`5368709120 B`), `.harvest.lock` i `hdx*` zaštitom, MS Update katalog i brza WU barijera. |
| **`WIS.Apps.psm1`** | 244,909 B (4,372 lin.) | `Get-WisAppPresence`, `Get-ServisProgramiRoot`, `Test-ProgramiPackageComplete`, `Invoke-InstallerWithLocalFallback`, `Invoke-OfficeInstall`, `Set-BatteryBarConfiguration`, `Assert-InstalledApplicationsReality` | Automatski restart pozadinskog WU radnika i 9 `StepGuard` koraka: `P4.Runtimes` offline `VC_redist` + `120s` `winget` `/T /F`, Chrome keš, BOM-free lokalni SSD `SourcePath` za Office ODT (`Office2024_Install.xml`), Acrobat praćenje + `acrobat_msi.log` + `1x` MSI retry, GPO+`HKLM` PDF lock, `DotNet` status, OEM OA3x / OSPP / eksterni licensing hook (`Licensing\Invoke-CustomLicensing.ps1`), BatteryBar Solo XML, prečice i Reality Check. |
| **`WIS.Branding.psm1`** | 44,127 B (878 lin.) | `Invoke-SystemOptimizationAndBranding`, `Set-ServisAutoDpiScaling`, `Invoke-ServisInterimReboot`, `Set-DefenderExclusions` | 6 `StepGuard` koraka (`debloat-done.flag`): srpska/US tastatura, 24h sat, automatsko DPI skaliranje (100%/125%/150%/200%), OEM brendiranje i upis `interim_reboot_required` stanja. |
| **`WIS.Cleanup.psm1`** | 78,125 B (1,415 lin.) | `Invoke-ServisCleanupAndFinalize`, `Clear-ServisWifiSecrets`, `Export-ServisSummaryReport`, `Restore-ServisUacDefaults` | Faza 6 koordinacija (WU barijera do 60s + `wu-secondpass-done.flag`), Anti-Leak brisanje servisnog Wi-Fi profila i `Wifi-Servis.ini`, raskidanje SMB sesija, lokalni servisni karton, vraćanje UAC-a na fabrički nivo (`5`/`1`) i samouništenje `C:\setup`. |
| **`WIS.Telemetry.psm1`** | 52,247 B (1,196 lin.) | `Send-ServisHeartbeat`, `Get-HardwareTelemetryMetrics`, `Invoke-ServisRemoteCommand`, `Send-FleetFinalReport`, `Get-FleetDeviceId` | Slanje 2s pulsa sa senzorikom i očuvanjem `cooling_verified`, slanje završnog DMTF kartona (`TK-{Chassis}-{Serial}`) sa `benchmark` objektom i `apps` mapom na `:8080`, i obrada daljinskih komandi (`reboot_uefi`, `reboot_pxe`, `cancel`, `skip_step`). |
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

## 🛠️ 3. Kanonski Build, Release i Media Pajplajn (`v3.1.0`)

Build i pakovanje sistema izvršavaju se kroz determinističke alate u okviru četiri razvojne celine:

1. **Usklađivanje verzija (`ISO/Skripte/Sync-Versions.py`):**
   - Čita kanonski `version.json` (`release_package: 3.1.0`, `iso: 3.1.0`, `launcher: 3.1.0`, `ui: 3.1.0`, `servicehub: 3.4.0`, `autopilot_auditor: 3.1.0`, `min_launcher_version: 3.1.0`) i proverava usklađenost komponentnih deklaracija (`--check`).
2. **Kompilacija C# Binaraca (`ISO/Skripte/Build-WIS.ps1`, `UI/Build-UI.py`, `UI/HwTest/Build-HwTest.py`, `Launcher/Build-Launcher.py`, `Tools/AutopilotAuditor/Build-AutopilotAuditor.py`):**
   - `WIS.UI.exe` (**v3.1.0**; `UI/ThermalSensorEngine.cs` koristi isključivo nativni WMI/ACPI/OEM sloj sa Dell `DCIM_NumericSensor`, bez `LibreHardwareMonitorLib.dll` i `HidSharp.dll`, dok `HwTestShell.cs` evidentira `operator_wait_sec`/`active_hwtest_sec` uz dvotonski audio signal pažnje).
   - `WIS.Launcher.exe` (**v3.1.0**, `min_launcher_version: 3.1.0`, anti-downgrade zaštita i `.sys` nadzor).
   - `WIS-AutopilotAuditor.exe` (**v3.1.0**).
3. **Potpisivanje i Objavljivanje Izdanja (`ISO/Skripte/Release-WIS.py` & `ISO/Skripte/Build-PreviewMedia.py`):**
   - Pakuje `wis-payload.zip` (`68,532,795 B`, `78` fajlova u `v3.0.0`, `release_id: 0fc688afa092c84ab05239dfb8cc4c190f7779b8-8919c26072904c75`, SHA-256: `8919c260...`) iz kanonskih izvora, generiše SHA-256 kontrolne sume svih fajlova i asimetrično potpisuje `payload_meta.json` privatnim RSA-2048 PKCS#1 v1.5 ključem (`~/.config/wis-signing/payload_signing_private.pem`).
4. **Priprema WinPE i Hibridnog UDF ISO Medijuma (`ISO/Skripte/Patch-BootWim.py` & `ISO/Skripte/Build-Iso.py`):**
   - `Patch-BootWim.py` (`1,313` linija) ažurira oba indeksa u `sources/boot.wim` (`~582 MiB`, LAN-only `15` fajlova, `000_wis_net1ix64.inf_guard`, **32 provere**).
   - `Build-Iso.py` (`1,103` linije) generiše hibridni UDF (`ECMA-167`) + El Torito UEFI (`0xEF`) + Legacy BIOS (`0x00`) Zlatni Lean ISO (`WIS-Win11-Pro-10.0.26200.8037-Lean.iso`, `6,522,753,024 B` / `6.07 GB`).

---

## 🧪 4. Višeslojna QA Verifikacija (Linux host i Windows E2E)

- **Linux statičke i unit provere:**
  - `python3 Launcher/Tests/run_tests.py`: **31/31** funkcionalnih testova (`PASS`).
  - `python3 ISO/Skripte/Verify-CSharpSyntax.py`: **48/48** C# fajlova (`UI/`, `UI/HwTest/`, `Launcher/`, `PASS`).
  - `python3 -m unittest UI/Tests/Test-HwTestLogic.py UI/Tests/Test-AutopilotPreOobeCache.py UI/Tests/Test-UpdateAndDriversLogic.py`: **44/44** regresionih testova (`PASS`).
  - `cd ServiceHub && python3 -m unittest discover -s tests`: **290** unittest metoda (`31` test modul / `33` `.py` fajla, `8,327` linija); bez opcionih `Pillow`/`FastAPI`/`Node` paketa na Linux hostu izvršava se **270 PASS, 20 skipped** (`0` padova).
  - `python3 ISO/Skripte/Verify-PowerShellSyntax.py` (**22/22 PASS**) i `python3 ISO/Skripte/Sync-Versions.py --check` (`PASS`).
- **Živa Windows 11 24H2 E2E validacija (`PASS`):**
  - Anonimizovani referentni E2E profili: (`Lenovo ThinkCentre M70q Gen 3` `WORKSTATION-DEMO-01`/`WORKSTATION-DEMO-02`, `HP EliteBook 840 14 inch G10` `LAPTOP-DEMO-01`, `Lenovo ThinkPad T14 Gen 2i` `LAPTOP-DEMO-02` i `Lenovo ThinkPad X13s Gen 1` ARM64 `ARM-DEMO-03`), kao i `177` instalacija analiziranih u [`FIELD-2026-11.md`](../benchmark/FIELD-2026-11.md). Za detalje `v3.1.0` bezbednosnog ojačanja videti [`ADR-0253`](../decisions/ADR-0253-wis-3-1-0-security-hardening-and-audit-remediation.md).
  - `.workspace/artifacts/windows/vm-validation.json`: `"status": "PASS"`, `"restart_verified": true`, `"anti_leak_verified": true`.
- **Windows PowerShell 5.1 & PXE Zero-Touch ugovori (`NOT_RUN` na Linux hostu):**
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
- `WIS Benchmark`, `Predictive Cache (Model DNA)` & Post-Telemetry Hardening: [[decisions/ADR-0236-wis-benchmark-predictive-cache-and-post-telemetry-hardening]]
- WinPE I219-LM Guard & Paralelni PXE: [[decisions/ADR-0184-winpe-i219lm-driverstore-guard-and-parallel-pxe]]
- Sanacija Faze 4 i Office 2024 ODT: [[decisions/ADR-0185-phase4-office-odt-bom-free-and-local-nvme-sourcepath]]
