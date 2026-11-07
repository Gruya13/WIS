---
title: "Modularni Sistem i Build Pajplajn (Modules & Build System)"
type: architecture
created: 2026-09-14
updated: 2026-10-26
tags:
  - architecture
  - modules
  - build
  - powershell
  - python
  - csharp
---

# Modularni Sistem i Build Pajplajn: WIS (v2.17.0)

Kako bi se obezbedila maksimalna pouzdanost, čitljivost i striktna kompatibilnost sa **Windows PowerShell 5.1** i **.NET Framework 4.0 / 4.8**, **WIS** (*Windows Installation Assistant*) je organizovan u **četiri glavne razvojne celine** (`ISO/`, `Launcher/`, `UI/`, `ServiceHub/`) i namenski forenzički podsistem `Tools/AutopilotAuditor/`.

Nekadašnji monolitni skript `Install-ServisApps.ps1`, legacy `GWADT.*.psm1` shimovi i stari alati `Build-DeployScript.py` / `Test-GWADTModules.py` su trajno penzionisani ([[decisions/ADR-0076-decoupling-monolith-into-phase-scripts-ui-orchestrator]], [[decisions/ADR-0131-diagnostics-redesign-6phase-pipeline-and-wis-cleanup]], [[decisions/ADR-0137-pure-wis-cleanup-and-gwadt-retirement]]).

---

## 🗂️ 1. Struktura PowerShell 5.1 Modula (`UI/Skripte/Modules/`)

Celokupna klijentska poslovna logika u Windows okruženju podeljena je na **11 specijalizovanih PowerShell 5.1 modula (`.psm1`)** i **1 pozadinskog radnika (`.ps1`)**, ukupnog obima **18,508 linija koda (~920 KB)**:

```text
UI/Skripte/Modules/
├── WIS.Common.psm1          # JSON IPC striming, kooperativna pauza, rotacija logova i deploy-state.json (980 linija)
├── WIS.StatusContract.psm1  # Kanonski statusni ugovor: razdvajanje DONE statusa od cooling_verified (30 linija)
├── WIS.Core.psm1            # Jezgro: Wifi-Servis.ini, SMB auth (WIS/Programi), NTFS SID ACL, MSI mutex (2,117 linija)
├── WIS.Hardware.psm1        # CIM/WMI, NVMe/SATA S.M.A.R.T., EDID, baterija, RunspacePool stres test (2,727 linija)
├── WIS.Network.psm1         # Wi-Fi povezivanje, PnP fallback drajvera, SMB 24H2 i TCP optimizacija (1,250 linija)
├── WIS.Update.psm1          # NAS .msu Pre-Patch, Single-Stream .tar drajveri, WU COM API, Driver Harvesting (2,199 linija)
├── WIS.Apps.psm1            # Tihe instalacije 8 aplikacija, SMB 3.1.1 stream, Acrobat PDF lock, OEM Licenca (4,308 linija)
├── WIS.Branding.psm1        # Regionalne postavke, automatsko DPI skaliranje, OEM brending, Interim Reboot (685 linija)
├── WIS.Cleanup.psm1         # Faza 6 finalizacija, DMTF izveštaj, Anti-Leak brisanje tajni, UAC restore (1,333 linija)
├── WIS.Telemetry.psm1       # ServiceHub REST/Heartbeat klijent, DMTF ID i daljinske komande (reboot_uefi) (1,107 linija)
├── WIS.UI.psm1              # Povezivanje PowerShell radnika sa WPF UI modelom i hardverskim profilom (738 linija)
└── WIS.ScreenCapture.ps1    # 1 Hz GDI+ Live Screen Mirror radnik u RAM memoriji (0 Disk I/O) (1,034 linija)
```

### Matrica Odgovornosti i Ključnih Funkcija po Modulima:

| Modul / Radnik | Veličina / Linije | Ključne Eksportovane Funkcije | Primarna Odgovornost |
| :--- | :--- | :--- | :--- |
| **`WIS.Common.psm1`** | 40,937 B (980 lin.) | `Send-IpcMessage`, `Send-IpcProgress`, `Send-IpcStatus`, `Wait-ServisUICooperativePause`, `Write-ServisLog`, `Rotate-ServisLog`, `Save-ServisDeployState`, `Get-ServisDeployState` | Jedinstveni UTF-8 JSON IPC cevovod ka `WIS.UI.exe`, kooperativna pauza preko `C:\setup\.pause`, multi-target logovanje sa rotacijom na 10 MB i atomska perzistencija stanja. |
| **`WIS.StatusContract.psm1`** | 1,573 B (30 lin.) | `New-WISDiagnosticsContract` | Kanonski statusni ugovor (v2.17.0): odvaja uspeh instalacije od potvrde hlađenja (`cooling_verified = true` isključivo kada je senzor dostupan i test `PASS`; u suprotnom `manual_check_required = true`). |
| **`WIS.Core.psm1`** | 104,239 B (2,117 lin.) | `Get-ServisNasSettings`, `Sync-ProgramiFromNas`, `Grant-SetupAclPermissions`, `Wait-MsiMutexAvailable`, `Remove-PendingRebootLocks`, `Disable-QuickEdit`, `Test-IsSingleInstance`, `Get-RealAppStatuses` | Učitavanje `Wifi-Servis.ini`, autentifikacija na `\\192.168.170.254\WIS` i `\Programi`, dodela SID NTFS dozvola (`*S-1-5-18`), čekanje `Global\_MSIExecute` mutexa i P/Invoke gašenje QuickEdit moda. |
| **`WIS.Hardware.psm1`** | 127,190 B (2,727 lin.) | `Get-HardwareDiagnosticsInfo`, `Get-MonitorEdidDetails`, `Start-ServisStressTest`, `Save-ServisStressTestResult`, `Install-DriverFromMsCatalog` | Hibridna NVMe/SATA S.M.A.R.T. trijaža (`PowerOnHours`, TBW, Wear %), dekodiranje 128B EDID bloka ekrana (IPS/OLED, % sRGB, nits), provera baterije, 30s Multi-Core `RunspacePool` stres test sa 98°C Emergency Abort osiguračem i MS Catalog HWID fallback. |
| **`WIS.Network.psm1`** | 55,210 B (1,250 lin.) | `Connect-ServisWifi`, `Install-NetDriverFallback`, `Optimize-ServisNetworkStack`, `Get-NetworkDetailStatus`, `Test-Internet` | Povezivanje na 5 GHz / 2.4 GHz Wi-Fi sa XML escaping-om, PnP instalacija mrežnih `.inf` drajvera iz `C:\setup\Drivers\Net`, otključavanje SMB 24H2 i TCP steka. |
| **`WIS.Update.psm1`** | 114,179 B (2,199 lin.) | `Invoke-NasUpdatePrePatch`, `Invoke-ModelDriversPreInstall`, `Export-ServisHardwareDrivers`, `Invoke-WindowsUpdatePhase`, `Wait-WindowsUpdateCompletionBarrier`, `Invoke-WindowsUpdateSecondPass` | Lokalni NAS `.msu` pre-patch, preuzimanje i ekstrakcija `<Model>.tar` drajvera za ~3s, samoučeći eksport drajvera sa `model_meta.json` (bez serijskih brojeva), Windows Update COM API i WU barijera pre Faze 5. |
| **`WIS.Apps.psm1`** | 245,424 B (4,308 lin.) | `Get-ServisProgramiRoot`, `Invoke-InstallerWithLocalFallback`, `Install-AllServisPrograms`, `Install-GoogleChrome`, `Install-AdobeAcrobat`, `Install-OfficeODT`, `Install-ServisVCRedist`, `Set-BatteryBarConfiguration`, `Assert-InstalledApplicationsReality` | Direktan SMB 3.1.1 stream sa `\\192.168.170.254\Programi`, tiha instalacija 8 aplikacija, 6-stepeni PDF lock, BatteryBar XML Floating Toolbar za Win11 (uz preskakanje na desktopu), offline OEM licenca i post-install Reality Check. |
| **`WIS.Branding.psm1`** | 34,677 B (685 lin.) | `Invoke-SystemOptimizationAndBranding`, `Set-ServisAutoDpiScaling`, `Invoke-ServisInterimReboot`, `Set-DefenderExclusions` | Srpska/US tastatura, 24h sat, automatsko DPI skaliranje ekrana po rezoluciji (100%/125%/150%/200%), OEM brendiranje po proizvođaču i kontrolisani međurestart (Pre-Reboot Gate). |
| **`WIS.Cleanup.psm1`** | 73,336 B (1,333 lin.) | `Invoke-ServisCleanupAndFinalize`, `Clear-ServisWifiSecrets`, `Export-ServisSummaryReport`, `Restore-ServisUacDefaults` | Anti-Leak brisanje servisnog Wi-Fi profila i `Wifi-Servis.ini`, raskidanje SMB sesija, generisanje lokalnog servisnog kartona, vraćanje UAC-a na fabrički nivo (`5`/`1`) i samouništenje `C:\setup`. |
| **`WIS.Telemetry.psm1`** | 47,254 B (1,107 lin.) | `Send-ServisHeartbeat`, `Get-HardwareTelemetryMetrics`, `Invoke-ServisRemoteCommand`, `Send-FleetFinalReport`, `Get-FleetDeviceId` | Slanje 2s pulsa sa senzorikom na ServiceHub, izvršavanje daljinskih komandi (`reboot_uefi`, `reboot_pxe`, `cancel`) i slanje završnog DMTF servisnog kartona (`TK-{Chassis}-{Serial}`). |
| **`WIS.UI.psm1`** | 34,893 B (738 lin.) | `Initialize-ServisUI`, `Update-ServisUI`, `Close-ServisUI` | Sinhronizacija PowerShell radnika sa WPF interfejsom i obogaćivanje telemetrijskog modela hardverskim podacima. |
| **`WIS.ScreenCapture.ps1`** | 40,735 B (1,034 lin.) | `Start-ServisScreenCapture`, `Stop-ServisScreenCapture`, `Capture-ServisScreenFrame`, `Send-ServisScreenFrame` | Pozadinski 1 Hz radnik za živo ogledalo ekrana: GDI+ `CopyFromScreen`, skaliranje na 1280 px, JPEG kompresija u `MemoryStream` (0 Disk I/O) i slanje na `/api/devices/{id}/screen-frame` uz `ParentPid` nadzor. |

---

## 🛡️ 2. Samostalni Forenzički Podsistem za Autopilot (`Tools/AutopilotAuditor/` & `Provera_autopilota.ps1`)

Za detekciju korporativnog Microsoft Autopilot / Intune MDM zaključavanja, WIS poseduje dvostepeni mehanizam ([[decisions/ADR-0158-autopilot-active-cloud-trigger-and-nas-fallback]], zadaci WIS-TASK-162 i WIS-TASK-164):
1. **Nativni C# izvršni alat `WIS-AutopilotAuditor.exe` v2.0 (`Tools/AutopilotAuditor/`, 442,368 B, 1,997 linija C# koda):**
   - Kompajliran za .NET Framework 4.8 bez zavisnosti od PowerShell-a.
   - Podržava grafički WPF režim, konzolni CLI režim (`--cli`, `--json`), izvoz u HTML (`--export-html`) i Intune 4K Hardware Hash CSV (`--export-hash`), uz automatski WinPE fallback.
   - Sadrži `HardwareInspector.cs`, `LocalForensicsInspector.cs`, `CloudZtdInspector.cs` (Silent OOBE servisi `wlidsvc` / `dmwappushservice`, Task Scheduler provisioning zadaci, `deviceenroller.exe` i 35s Event Log polling za Event 806/807), `DecisionMatrix.cs` (5-stupanjska matrica: `LOCKED_TENANT`, `HIDDEN_LOCK`, `RELEASED_LOCAL_TRACES`, `CONFIRMED_CLEAN`, `UNVERIFIED_CLEAN`) i `ReportExporter.cs`.
2. **PowerShell skripta `UI/Skripte/Provera_autopilota.ps1` (799 linija, 45,850 B):**
   - Služi kao fallback u `WIS.UI.exe` (`FindAutopilotTool`) i samostalna skripta u `C:\setup` i na korenu ISO/NAS medijuma.

---

## 🛠️ 3. Kanonski Build, Release i Media Pajplajn (v2.17.0)

Build i pakovanje sistema izvršavaju se kroz determinističke alate u okviru četiri razvojne celine:

1. **Usklađivanje verzija (`ISO/Skripte/Sync-Versions.py`):**
   - Čita kanonski `version.json` (`release_package: 2.17.0`, `iso: 2.17.0`, `launcher: 2.2.17`, `ui: 2.1.0`, `servicehub: 3.2.0`) i propagira verzije kroz sve komponente (`--check` detektuje bilo kakav drift).
2. **Kompilacija C# Binaraca (`ISO/Skripte/Build-WIS.ps1`, `UI/Build-UI.py`, `Tools/AutopilotAuditor/Build-AutopilotAuditor.py`):**
   - `WIS.UI.exe` (`1,165,312 B`, C# 5.0 / .NET Framework 4.8, ugrađeni BAML resursi, `app.manifest` sa `requireAdministrator` i `WIS.ico`).
   - `WIS.Launcher.exe` (`466,944 B`, C# 5.0 / .NET Framework 4.0, ugrađeni javni RSA-2048 ključ `trusted_public_key.xml` i `WIS.ico`).
   - `WIS-AutopilotAuditor.exe` (`442,368 B`, C# / .NET Framework 4.8).
3. **Potpisivanje i Objavljivanje Izdanja (`ISO/Skripte/Release-WIS.py` & `ISO/Skripte/Build-PreviewMedia.py`):**
   - Pakuje `wis-payload.zip` iz kanonskih izvora, generiše SHA-256 kontrolne sume svih fajlova i asimetrično potpisuje `payload_meta.json` privatnim RSA-2048 PKCS#1 v1.5 ključem (`~/.config/wis-signing/payload_signing_private.pem`).
4. **Priprema WinPE i Hibridnog UDF ISO Medijuma (`ISO/Skripte/Patch-BootWim.py` & `ISO/Skripte/Build-Iso.py`):**
   - `Patch-BootWim.py` ažurira oba indeksa u `sources/boot.wim` (`668.9 MB`).
   - `Build-Iso.py` generiše hibridni UDF (`ECMA-167`) + El Torito UEFI (`0xEF`) + Legacy BIOS (`0x00`) Zlatni Lean ISO (`WIS-Win11-Pro-10.0.26200.8037-Lean.iso`, `6,531,049,472 B` / `6.08 GB`).

---

## 🧪 4. Višeslojna QA Verifikacija

Pre svakog izdanja ili distribucije na TrueNAS / iVentoy izvršavaju se 4 nezavisna verifikaciona paketa:
- **Statička kontrola PowerShell 5.1 sintakse (`python3 ISO/Skripte/Verify-PowerShellSyntax.py`):** Proverava svih **21** PowerShell modula, faza i bootstrap skripti (balans zagrada, here-stringova, PowerShell 5.1 pravila i duplikata ključeva — **21/21 PASS**).
- **Statička kontrola C# 5.0 sintakse (`python3 ISO/Skripte/Verify-CSharpSyntax.py`):** Proverava svih **22** C# izvornih fajlova u `UI/` i `Launcher/` (**22/22 PASS**).
- **Launcher Behavioral Test Suite (`python3 Launcher/Tests/run_tests.py`):** Kompajlira i izvršava **30/30** funkcionalnih testova (RSA-2048 verifikacija potpisa, Zip-Slip zaštita, transakcioni rollback, kompatibilnost verzija).
- **ServiceHub Test Suite (`cd ServiceHub && python3 -m unittest discover -s tests`):** Izvršava **259/259** jediničnih i integracionih testova bez preskakanja (REST API, 1 Hz Screen Mirror, Remote Commands, OEM Lenovo/Dell/HP provajderi, WIS USB safety guardrails, potpisana izdanja).

---

## 🔗 Povezane Beleške
- Pregled Arhitekture: [[architecture/overview]]
- Pajplajn Izvršavanja: [[architecture/pipeline]]
- Centralizovani Servisni Hub: [[architecture/servis-hub]]
- Kanonsko Verzionisanje: [[operations/component-versioning-2026-10-15]]
- Build, Release i Media Runbook: [[operations/build-release-media-2.17]]
