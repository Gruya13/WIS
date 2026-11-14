---
title: "Pajplajn Izvršavanja i Upravljanje Stanjem (Pipeline & State Management)"
type: architecture
created: 2026-09-14
updated: 2026-11-11
tags:
  - architecture
  - pipeline
  - state
  - phases
---

# Pajplajn Izvršavanja i Upravljanje Stanjem: WIS (v3.0.0)

U savremenoj arhitekturi sistema **WIS** (*Windows Installation Assistant*, [[decisions/ADR-0131-diagnostics-redesign-6phase-pipeline-and-wis-cleanup]], [[decisions/ADR-0138-non-blocking-diagnostics-soft-handover]], [[decisions/ADR-0141-canonical-signed-release-and-status-contract]], [[decisions/ADR-0181-stepguard-precheck-hardware-identity-and-ui-v23]], [[decisions/ADR-0185-phase4-office-odt-bom-free-and-local-nvme-sourcepath]], [[decisions/ADR-0189-manual-hardware-test-suite]], [[decisions/ADR-0225-wis-v300-embedded-hwtest-bidirectional-wu-and-preoobe-autopilot]]), glavni orkestrator celokupnog post-OOBE toka je **C# WPF Master Task Runner (`WIS.UI.exe`, v3.0.0, `2,977,280 B`)**.

`WIS.UI.exe` direktno i sekvencijalno podiže **6 procesno izolovanih PowerShell 5.1 skripti** iz direktorijuma `C:\setup\Skripte\Phases\` (`Phase1-Network.ps1` do `Phase6-Finalize.ps1`). Sve skripte faza oslanjaju se na deljene module `WIS.Common.psm1`, `WIS.StepGuard.psm1` i `WIS.StatusContract.psm1`, koji obezbeđuju strukturirano JSON IPC strimovanje na STDOUT (`Send-IpcStep`), idempotentni `Pre-Check -> Action -> Journal -> Verify` ugovor za **21 StepGuard korak** Faza 2–4 (`step-journal.json`), kooperativnu kontrolu toka (pauza/preskakanje), ponovno pokretanje Faze 4 iz UI-ja (`force-rerun-phase4.flag`), pozadinsko praćenje Windows Update-a (`wu-bg-status.json`), rotaciju logova i kanonski statusni ugovor.

---

## 💾 1. Upravljanje Stanjem (`C:\setup\deploy-state.json` i `step-journal.json`)

Sistem perzistira svoje radno stanje na disku u datoteci `C:\setup\deploy-state.json`, dok svaki pojedinačni korak Faza 2–4 dobija sopstveni idempotentni zapis u `C:\setup\step-journal.json`:

```json
{
  "phase": 4,
  "update_cycle": 1,
  "max_cycles": 3,
  "interim_reboot_required": true,
  "initial_start_time": "2026-11-11T10:15:00Z",
  "session_id": "TK-LAPTOP-PF2X98ZA",
  "deployment_status": "ACTIVE",
  "handover_ready": false,
  "cooling_verified": false,
  "manual_check_required": false,
  "diagnostics_status": "PENDING"
}
```

### Mehanizam Otpornosti i Kontinuiteta:
1. **Učitavanje stanja i Kumulativni Tajmer:** Pri svakom pokretanju `WIS.UI.exe` čita `deploy-state.json`, `step-journal.json` i `HKLM\SOFTWARE\WIS\InitialStartTime`, čime kumulativni tajmer u zaglavlju prozora neprekidno meri ukupno vreme servisa kroz sve međurestarte.
2. **Idempotentni StepGuard (`WIS.StepGuard.psm1`):** Svih 21 korak kroz Faze 2 (`P2.*`), 3 (`P3.*`) i 4 (`P4.*`) pre izvršavanja proverava realno stanje na disku/sistemu i preskače već uspešne korake (`SKIPPED_DONE`) osim ako nije aktivan `--force` / `force-rerun-phase4.flag`.
3. **Čisti HKLM `RunOnce` Resume:** `EnsureRunOnceEntry()` upisuje jedinstveni `HKLM\...\RunOnce\WIS_Resume` ključ ka `C:\setup\WIS.Launcher.exe --master`, uklanjajući stare `Startup` `.cmd` lansere i `HKCU` duplikate kako bi se izbeglo dvostruko podizanje.
4. **Obavezni Kontrolisani Međurestart:** Posle završetka Faze 3 (`phase == 3`), `ExecutePhase` obavezno poziva `HandleInterimReboot()` (uz čuvanje `interim_reboot_required` u `step-journal.json`) kako bi se CBS/Installer brave oslobodile pre Faze 4.

---

## ⏱️ 2. Detaljan Tok Kroz 6 Faza i Timeout Osigurači

U `UI/MainWindow.xaml.cs` (`GetPhaseTimeoutMinutes`), svaka faza poseduje strogo definisan zaštitni timeout:

| Faza | Skripta (`UI/Skripte/Phases/`) | Linije / Veličina | Zaštitni Timeout | Ključna Uloga |
| :--- | :--- | :--- | :--- | :--- |
| **Pre-Phase** | `WIS.Launcher.exe` (v3.0.0) | 468,480 B | Reaktivni | Wi-Fi spajanje, UDP :8888 / SMB otkrivanje (`Servis` / `WIS` i `storage\cache`), RSA-2048 verifikacija `wis-payload.zip`, transakciona ekstrakcija u `C:\setup` i start `WIS.UI.exe --master` (sa zaštitom od duple instance). |
| **Faza 1** | `Phase1-Network.ps1` | 133 lin. (5,727 B) | **15 min** | Mrežna povezanost (Fast-Pass), PnP fallback mrežnih drajvera, NVMe/SATA S.M.A.R.T. i baterija trijaža, `HardwareIdentityService` profil, Pre-OOBE Autopilot keš + auto-retry. |
| **Faza 2** | `Phase2-Drivers.ps1` | 83 lin. (3,306 B) | **60 min** | `6 StepGuard` koraka (`P2.*`): dvosmerni HTTP/SMB `.msu`/`.cab` WU keš (`Mode=All`, `OSBuild` + SHA-256 `index.json`), dvosmerni `<Model>.tar` keš (`ShareName=Servis`), Windows Update COM API (6h TTL + `wu-bg-status.json`), `Test-WisDeviceManagerClean` + batch `/api/drivers/resolve`, Driver Harvesting (`HARVESTING`). |
| **Faza 3** | `Phase3-Optimization.ps1` | 75 lin. (2,927 B) | **20 min** | `6 StepGuard` koraka (`P3.*`): sistemska optimizacija, debloat, SRB/US tastatura, 24h sat, automatsko DPI skaliranje po rezoluciji, OEM brendiranje i obavezni **Interim Reboot Gate** (`HandleInterimReboot`). |
| **Faza 4** | `Phase4-Apps.ps1` | 92 lin. (3,783 B) | **60 min** | Uvozi `WIS.Core.psm1`; `9 StepGuard` koraka (`P4.*`): MSI Mutex Gate, direktni SMB 3.1.1 stream sa `\\192.168.170.254\Programi` (`Error 1219` zaštita), Chrome keš, Office ODT BOM-free lokalni SSD `SourcePath`, 6-stepeni PDF lock, per-app `P4.Shortcuts`, Reality Check i OEM licenca. |
| **Faza 5** | `MainWindow.HwTest.cs` (`WIS.HwTest`) + `Phase5-Stress.ps1` fallback | UI `425` lin. + PS `689` lin. | **45 min** | Objedinjeni `ExecutePhase5IntegratedHwTest` / `StartPhase5FullWorkflow`: `P5.AutoDiag` (7 automatskih testova) -> `P5.Stress` (`ThermalSensorEngine` refleksija, `_baseMaxClockMHz` korekcija, 98°C Abort, bez ugrađenog `WinRing0`) -> `P5.Peripherals` (9 interaktivnih testova). `Phase5-Stress.ps1` ostaje za CI/fallback (`WIS_STRESS_TEST_INPUT_JSON`). |
| **Faza 6** | `Phase6-Finalize.ps1` | 696 lin. (37,704 B) | **45 min** | Asinhroni WU Second Pass, gašenje Screen Mirror radnika, slanje DMTF servisnog kartona na ServiceHub, termalna štampa Star TSP700II nalepnice (`LabelPrintService.cs`), Anti-Leak brisanje tajni, UAC restore i brisanje `C:\setup`. |

---

### Inicijalizacija, `$OEM$` WinPE Staging i „Flash & Release" Koncept
- Tokom WinPE faze, Windows Setup kopira sadržaj `sources/$OEM$/$1/setup/` na sistemski SSD (`C:\setup\`), dok `SetupComplete.ps1` pod `SYSTEM` nalogom pokreće `wlansvc` i uvozi servisni Wi-Fi profil pre pojave ekrana za prijavu.
- **„Flash & Release":** Čim se završi WinPE kopiranje i računar krene u prvi restart, operater može odmah izvaditi USB fleš drajv.
- U `FirstLogon.ps1` fazi pokreće se pozadinski 1 Hz Screen Mirror radnik (`WIS.ScreenCapture.ps1`) i `C:\setup\WIS.Launcher.exe`, koji sa `ServiceHub`-a (`http://192.168.170.254:8080`) ili SMB deljenja (`\\192.168.170.254\Servis` / `\WIS`) preuzima i RSA-2048 ključem verifikuje `wis-payload.zip` (`68,475,013 B`, `78` fajlova), uklanja `:Zone.Identifier` i pokreće `WIS.UI.exe --master`.

---

### Faza 1: Mrežna Povezanost, Hardverska Trijaža i Autopilot Detekcija (`Phase1-Network.ps1`)
- **Fast-Pass provera:** Ako je mreža već aktivna iz `SetupComplete` / `FirstLogon` faze, preskače nepotrebna čekanja. U suprotnom poziva `Install-NetDriverFallback` (`pnputil.exe` nad `C:\setup\Drivers\Net`) i spaja Wi-Fi.
- **Hardverska dijagnostika (`Get-HardwareDiagnosticsInfo`):** Očitava komercijalni naziv modela (Lenovo MTM/Version, Dell, HP, Asus), CPU, RAM, EDID matricu ekrana, zdravlje baterije i S.M.A.R.T. parametre diska (`PowerOnHours`, Wear %, temperaturu).
- **Autopilot / MDM inspekcija i Pre-OOBE keš:** Učitava potvrđeni Pre-OOBE izveštaj (`C:\Windows\Logs\WIS\autopilot_report.json` ili `AutopilotDDSZTDFile.preoobe.json`) ako postoji (uz zaštitu od degradacije na `Unknown`), ili pokreće nativni `WIS-AutopilotAuditor.exe --json` (`ConfirmedFree` za prazan `AutopilotDDSZTDFile.json`, uz `Provera_autopilota.ps1 -JsonOutput` kao fallback) i automatski ponavlja sken po aktivaciji mreže (`TryRetryAutopilotOnNetworkReady`).

---

### Faza 2: Drajveri, Dvosmerni WU/Driver Keš i Berba Drajvera (`Phase2-Drivers.ps1`)
- **Dvosmerni WU Pre-Patch (`Invoke-NasUpdatePrePatch`):** Instalira kumulativne `.msu`/`.cab` zakrpe sa `ServiceHub` HTTP keša (`/api/updates/download`, `OSBuild` + SHA-256 `index.json`) i SMB deljenja `\\192.168.170.254\Servis\Updates\Win11` (u skladu sa `[WindowsUpdate] Mode=All`), a nove zakrpe može vratiti u hub keš (`/api/updates/upload`).
- **Dvosmerni Single-Stream `.tar` Keš (`Invoke-ModelDriversPreInstall`):** Preko ServiceHub HTTP rute (`/api/drivers/model-pack/status` / `download`) ili NAS `Drivers\Harvested\Models` (`ShareName=Servis`) preuzima `<Model>.tar`, raspakuje preko `tar.exe -xf` i instalira drajvere; pri berbi (`Export-ServisHardwareDrivers`) prijavljuje UI status **`HARVESTING`** i otprema paket nazad na hub (`/api/drivers/model-pack/upload`).
- **Windows Update COM API i Catalog Fallback:** Pretražuje WHQL drajvere u do 3 iterativna kruga, a za preostale nepoznate uređaje u Device Manager-u povlači CAB drajvere preko `ServiceHub` `/api/drivers/resolve` i Microsoft Update Catalog-a.
- **Samoučeći Driver Harvesting (`Export-ServisHardwareDrivers`):** Eksportuje sve nove 3rd-party drajvere u `<Model>.tar` na TrueNAS-u (uz `model_meta.json` bez klijentskih serijskih brojeva).

---

### Faza 3: Sistemska Optimizacija, Automatski DPI, OEM Brendiranje i Međurestart (`Phase3-Optimization.ps1`)
- **Optimizacija i privatnost (`P3.*` StepGuard):** Gasi telemetrijske servise (`DiagTrack`, `dmwappushservice`), Consumer Features, hibernaciju i QuickEdit mod.
- **Regionalna ergonomija i DPI (`Set-ServisAutoDpiScaling`):** Postavlja srpsku (latinica) i US tastaturu, 24h sat i automatsko Win32 DPI skaliranje prema rezoluciji monitora (100% / 125% / 150% / 200%).
- **OEM Brendiranje:** Preimenuje korisnički nalog prema detektovanom brendu računara i upisuje `OEMInformation`.
- **Pre-Reboot Gate (`HandleInterimReboot`):** Posle Faze 3 (`phase == 3`), `WIS.UI.exe` obavezno poziva `HandleInterimReboot()` (uz upis autoritativnog `interim_reboot_required` stanja u `step-journal.json` i `Phase = 4` u `deploy-state.json`) kako bi oslobodio zaključane CBS i MSI resurse pre Faze 4.

---

### Faza 4: Tiha Instalacija Aplikacija i Licenciranje (`Phase4-Apps.ps1`)
- **Uvoz `WIS.Core.psm1` i MSI Gate:** `Phase4-Apps.ps1` eksplicitno uvozi `WIS.Core.psm1` (čime je obezbeđen `Sync-ProgramiFromNas` i zaštita od SMB `Error 1219`), čeka oslobađanje `Global\_MSIExecute` mutexa (`Wait-MsiMutexAvailable -TimeoutSec 180`) i čisti `PendingFileRenameOperations`.
- **9 `StepGuard` koraka (`P4.Runtimes..P4.Activation`):**
  - `P4.Chrome` kešira i instalira offline Enterprise MSI (`~8s`).
  - `P4.Office` automatski prepoznaje i `Configure.xml` i `configuration.xml`, uklanja UTF-8 BOM, kopira offline `Office\Data` preko `Sync-ProgramiFromNas -Force` na lokalni SSD i upisuje lokalni `SourcePath` u `C:\setup\Office2024_Install.xml` (neophodno pod `NT AUTHORITY\SYSTEM`), uz `420s + 120s` C2R čekanje.
  - `P4.BatteryBar` konfiguriše Win11 Floating Toolbar na laptopovima (`SKIPPED` na desktopu); `P4.Shortcuts` proverava `.lnk` prečice za svaku stvarno instaliranu aplikaciju posebno (`Adobe Acrobat.lnk`, `Word.lnk`, `Excel.lnk`).
- **Post-Execution Reality Check i UI Rerun:** `Assert-InstalledApplicationsReality` proverava fizičko prisustvo aplikacija na disku, a operater po potrebi može iz UI-ja ponovo pokrenuti Fazu 4 (`RerunPhase4Command` / `force-rerun-phase4.flag`).

---

### Faza 5: Ugrađeni Hardverski Test Suite & Termalni Stres (`ExecutePhase5IntegratedHwTest` / `WIS.HwTest`, 45 min)
- **Objedinjena orkestracija (`MainWindow.HwTest.cs` & `HwTestShell.StartPhase5FullWorkflow`):** U `WIS v3.0.0`, `WIS.UI.exe` direktno izvršava Fazu 5 unutar ugrađenog **9. taba (`🧪 Hardverski testovi`)** kroz tri sekvencijalna podkoraka (`GetPhaseTimeoutMinutes(5) = 45` min): `P5.AutoDiag` (7 automatskih testova: `auto_battery`, `auto_network`, `auto_security`, `auto_disk`, `auto_ram`, `auto_cpu`, `auto_gpu`) -> `P5.Stress` (opterećenje sa `ThermalSensorEngine.cs` refleksijom bez ugrađenog `WinRing0` i `_baseMaxClockMHz` throttling korekcijom) -> `P5.Peripherals` (9 interaktivnih testova: `display`, `keyboard`, `touchpad`, `touchscreen`, `audio`, `mic`, `camera`, `usb`, `fingerprint`).
- **CI / CLI Fallback (`Phase5-Stress.ps1`):** Koristi se automatski kada je definisan `WIS_STRESS_TEST_INPUT_JSON` ili pri samostalnom PowerShell testiranju (`Wait-WindowsUpdateCompletionBarrier`, 30s `RunspacePool` load).
- **100% Multi-Core `RunspacePool` Load Generator:** Pokreće N paralelnih niti (`[Environment]::ProcessorCount`) tokom tačno **30 sekundi**, mereći baznu, maksimalnu i prosečnu temperaturu i eventualni termalni throttling.
- **Kanonski Statusni Ugovor (`stress-test-result.json` & `WIS.StatusContract.psm1`):**
  - `PASS` ($< 80^\circ\text{C}$) / `WARNING` ($80\text{–}94^\circ\text{C}$): `cooling_verified = true`, `handover_ready = true`, `exit 0`.
  - `THERMAL_CRITICAL` ($\ge 95^\circ\text{C}$ ili Emergency Abort na $\ge 98.0^\circ\text{C}$): trenutno prekida opterećenje, postavlja `handover_ready = false`, `cooling_verified = false` i vraća **`exit 2`** (crveni ekran u UI-ju, blokada predaje).
  - `UNKNOWN` / `SENSOR_UNAVAILABLE` (hardver ne izlaže termalne senzore): **NE blokira instalaciju** — postavlja `handover_ready = true`, `cooling_verified = false`, `manual_check_required = true`, prikazuje žuto upozorenje u UI-ju i vraća **`exit 0`**.

---

### Faza 6: Drugi WU Prolaz, DMTF Servisni Karton, Anti-Leak Čišćenje i Samouništenje (`Phase6-Finalize.ps1`)
- **WU Second Pass (`Invoke-WindowsUpdateSecondPass`):** Asinhroni `Start-Job` (do 600s) koji instalira preostale zakrpe nakon što su svi drajveri aktivni.
- **Zaustavljanje Screen Mirror radnika:** Signalizira `Global\WIS_ScreenCapture_StopEvent` i gasi pozadinski posao snimanja ekrana.
- **DMTF Servisni Karton (`Send-FleetFinalReport`) & Termalna Štampa Nalepnica (`LabelPrintService.cs`):** Šalje kompletan JSON izveštaj (`TK-{Chassis}-{Serial}`) na `ServiceHub` `/api/devices/{id}/report`, čuva lokalnu kopiju u `C:\Windows\Logs\WIS\` i omogućava automatsku štampu servisne etikete na Star TSP700II štampaču (konfiguracija `[Printer]`, sa `HW test` rezimeom iz `C:\setup\hwtest-result.json`).
- **Anti-Leak Sanitizacija i Samouništenje (`Invoke-ServisCleanupAndFinalize`):**
  1. Briše servisni Wi-Fi profil iz Windows WLAN baze (`netsh wlan delete profile`) i trajno uklanja `Wifi-Servis.ini` i XML fajlove sa diska.
  2. Raskida sve SMB sesije (`net use * /delete /y`).
  3. Uklanja privremene `RunOnce` ključeve i `Startup` lansere.
  4. Vraća UAC na fabričke bezbednosne vrednosti (`ConsentPromptBehaviorAdmin = 5`, `PromptOnSecureDesktop = 1`).
  5. Ukoliko su sve provere prošle, pokreće asinhrono brisanje `C:\setup\` i predaje kontrolu `WIS.UI.exe` završnom ekranu (60s odbrojavanje do gašenja sa opcijom `+10 min` odlaganja).

---

## 🔗 Povezane Beleške
- Pregled Arhitekture: [[architecture/overview]]
- PowerShell Moduli i Build: [[architecture/modules]]
- StepGuard, Hardware Identity & UI v2.3.0: [[decisions/ADR-0181-stepguard-precheck-hardware-identity-and-ui-v23]]
- Ručni i automatski `WIS.HwTest` suite: [[decisions/ADR-0189-manual-hardware-test-suite]]
- `WIS v3.0.0` integrisana Faza 5, dvosmerni WU/driver keš, Pre-OOBE Autopilot i Star TSP700II: [[decisions/ADR-0225-wis-v300-embedded-hwtest-bidirectional-wu-and-preoobe-autopilot]]
- Sanacija Faze 4 i Office 2024 ODT: [[decisions/ADR-0185-phase4-office-odt-bom-free-and-local-nvme-sourcepath]]
- Non-Blocking Diagnostics Policy: [[decisions/ADR-0138-non-blocking-diagnostics-soft-handover]]
- Kanonski Statusni Ugovor: [[decisions/ADR-0141-canonical-signed-release-and-status-contract]]
- Poznate Zamke: [[memory/gotchas]]
