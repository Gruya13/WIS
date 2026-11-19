---
title: "Pajplajn Izvršavanja i Upravljanje Stanjem (Pipeline & State Management)"
type: architecture
created: 2026-09-14
updated: 2026-11-18
tags:
  - architecture
  - pipeline
  - state
  - phases
---

# Pajplajn Izvršavanja i Upravljanje Stanjem: WIS (v3.0.0)

U savremenoj arhitekturi sistema **WIS** (*Windows Installation Assistant*, [[decisions/ADR-0131-diagnostics-redesign-6phase-pipeline-and-wis-cleanup]], [[decisions/ADR-0138-non-blocking-diagnostics-soft-handover]], [[decisions/ADR-0141-canonical-signed-release-and-status-contract]], [[decisions/ADR-0181-stepguard-precheck-hardware-identity-and-ui-v23]], [[decisions/ADR-0185-phase4-office-odt-bom-free-and-local-nvme-sourcepath]], [[decisions/ADR-0189-manual-hardware-test-suite]], [[decisions/ADR-0225-wis-v300-embedded-hwtest-bidirectional-wu-and-preoobe-autopilot]], [[decisions/ADR-0236-wis-benchmark-predictive-cache-and-post-telemetry-hardening]], [[decisions/ADR-0247-field-monitoring-17-devices-and-p0-p2-system-hardening]]), glavni orkestrator celokupnog post-OOBE toka je **C# WPF Master Task Runner (`WIS.UI.exe`, v3.0.0, `3,007,488 B`)**.

`WIS.UI.exe` direktno i sekvencijalno podiže **6 procesno izolovanih PowerShell 5.1 skripti** iz direktorijuma `C:\setup\Skripte\Phases\` (`Phase1-Network.ps1` do `Phase6-Finalize.ps1`). Sve skripte faza oslanjaju se na deljene module `WIS.Common.psm1`, `WIS.StepGuard.psm1` (`AddStatement()` runspace fix) i `WIS.StatusContract.psm1`, koji obezbeđuju strukturirano JSON IPC strimovanje na STDOUT (`Send-IpcStep`), idempotentni `Pre-Check -> Action -> Journal -> Verify` ugovor za **21 StepGuard korak** Faza 2–4 (`step-journal.json`), **WIS Benchmark** merenje trajanja svake faze (sa resetom `duration_sec = 0.0` pri ponovnom pokretanju) i LAN/WAN protoka, kooperativnu kontrolu toka (uklj. daljinski `skip_step` iz Hub-a preko `pending-hub-commands.json` i `.skip`), ponovno pokretanje Faze 4 iz UI-ja (`force-rerun-phase4.flag`), `taskkill /T /F` gašenje procesnog stabla na timeoutu, pozadinsko praćenje Windows Update-a (`wu-bg-status.json`), rotaciju logova i kanonski statusni ugovor.

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
| **Pre-Phase** | `WIS.Launcher.exe` (v3.0.0) | 469,504 B | Reaktivni | Wi-Fi spajanje, UDP :8888 / SMB otkrivanje (`Servis` / `WIS` i `storage\cache`), RSA-2048 verifikacija `wis-payload.zip`, transakciona ekstrakcija u `C:\setup` i direktan start `WIS.UI.exe --master` pri nastavku (`deploy-state.json`). |
| **Faza 1** | `Phase1-Network.ps1` | 143 lin. (6,319 B) | **15 min** | Mrežna povezanost (Fast-Pass), PnP fallback mrežnih drajvera, NVMe/SATA S.M.A.R.T. i baterija trijaža, `HardwareIdentityService` profil, Pre-OOBE Autopilot keš + auto-retry, `Start-WisPhaseBenchmark` (reset `duration_sec = 0.0`). |
| **Faza 2** | `Phase2-Drivers.ps1` | 89 lin. (3,684 B) | **60 min** | `6 StepGuard` koraka (`P2.*`, `[void]$ps.AddStatement()`): `Send-WisModelDnaToHub` (+ `model_aliases`), dvosmerni HTTP/SMB `.msu`/`.cab` WU keš (`Mode=All`, `*AggregatedMetadata*.cab` filter), `<Model>.tar` keš (`Test-IsGenericComputerModel` guard), `Select-RelevantDriverInfFiles` bez comma-wrapa (`90s` resolve timeout), `Restore-ServisNetworkConnectivityIfNeeded`, WU COM API + MS Update katalog, `Test-WisDeviceManagerClean` + `Export-ServisHardwareDrivers` (`5.0 GB`, `.harvest.lock`, `tar`-first & `hdx*` higijena). |
| **Faza 3** | `Phase3-Optimization.ps1` | 84 lin. (3,418 B) | **20 min** | `6 StepGuard` koraka (`P3.*`): sistemska optimizacija, debloat (`debloat-done.flag`), `Disable-LocationPrivacyPrompts` (`ShowGlobalPrompts=0`), SRB/US tastatura, 24h sat, automatsko DPI skaliranje po rezoluciji, offline `VC_redist.x64/x86.exe` + `winget` timeout/kill, OEM brendiranje i obavezni **Interim Reboot Gate** (`HandleInterimReboot`). |
| **Faza 4** | `Phase4-Apps.ps1` | 98 lin. (4,161 B) | **60 min** | Uvozi `WIS.Core.psm1`; `9 StepGuard` koraka (`P4.*`): automatski restart pozadinskog WU radnika, `VCRedist` timeout + `/T /F`, MSI Mutex Gate, `Test-ProgramiDirComplete` / fallback na `\\host\Servis\Programi` (`Error 1219` zaštita), Chrome keš, `P4.Acrobat` (`Setup.exe`/`msiexec` praćenje + `acrobat_msi.log` + `1x` MSI retry), Office ODT BOM-free lokalni SSD `SourcePath`, GPO+`HKLM` `PdfDefault`, per-app `P4.Shortcuts`, Reality Check i OEM OA3x / OSPP / opcioni eksterni licensing hook. |
| **Faza 5** | `MainWindow.HwTest.cs` (`WIS.HwTest`) + `Phase5-Stress.ps1` fallback | UI + PS `712` lin. (`40,585 B`) | **45 min** (+ `24h` wait) | Objedinjeni `ExecutePhase5IntegratedHwTest` / `StartPhase5FullWorkflow`: `P5.AutoDiag` (7 automatskih provera sa filterom `FACIAL RECOGNITION`, aktivnim `wlanapi.dll` Wi-Fi scanom i `Firmware [Code 14]`) -> `P5.Stress` (neblokirajući `eng.Update()`, `_baseMaxClockMHz`, `lowCoolTemp < 75°C`, nastavak uz `thermal_note` kod pregrevanja) -> `P5.Peripherals` (9 testova sa Display start banerom, Touchpad/TrackPoint odvajanjem + ELAN kernel niti i IR-ON filterom; **na desktopu auto-skip u `SKIPPED_DONE`** + `C:\setup\hwtest-result.json`). |
| **Faza 6** | `Phase6-Finalize.ps1` | 734 lin. (39,632 B) | **45 min** | Brza WU barijera (`wu-secondpass-done.flag` + max 60s) + WU Second Pass sa `-SkipDriverPreInstall`, gašenje Screen Mirror radnika, očuvanje `cooling_verified` + `hwtest-result.json` + `hw`/`benchmark` uz čišćenje `DESKTOP-*` duplikata na ServiceHub-u, Star TSP700II nalepnica, završno dugme „Očisti i restartuj računar” (`WrapPanel`), Anti-Leak, UAC restore i brisanje `C:\setup`. |

---

### Inicijalizacija, `$OEM$` WinPE Staging i „Flash & Release" Koncept
- Tokom WinPE faze, Windows Setup kopira sadržaj `sources/$OEM$/$1/setup/` na sistemski SSD (`C:\setup\`), dok `SetupComplete.ps1` pod `SYSTEM` nalogom pokreće `wlansvc` i uvozi servisni Wi-Fi profil pre pojave ekrana za prijavu.
- **„Flash & Release":** Čim se završi WinPE kopiranje i računar krene u prvi restart, operater može odmah izvaditi USB fleš drajv.
- U `FirstLogon.ps1` fazi pokreće se pozadinski 1 Hz Screen Mirror radnik (`WIS.ScreenCapture.ps1`) i `C:\setup\WIS.Launcher.exe` (odnosno direktno `WIS.UI.exe --master` pri nastavku posle restarta kad je binari već na disku), koji sa `ServiceHub`-a (`http://192.168.170.254:8080`) ili SMB deljenja (`\\192.168.170.254\Servis` / `\WIS`) preuzima i RSA-2048 ključem verifikuje `wis-payload.zip` (`68,532,795 B`, `78` fajlova, SHA-256 `8919c260...`), uklanja `:Zone.Identifier` i pokreće `WIS.UI.exe --master`.

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
- **Uvoz `WIS.Core.psm1`, WU Nastavak i MSI Gate:** `Phase4-Apps.ps1` eksplicitno uvozi `WIS.Core.psm1` (čime je obezbeđen `Sync-ProgramiFromNas` i zaštita od SMB `Error 1219`), na ulasku (`Invoke-Phase4Apps`) automatski ponovo pokreće pozadinski WU radnik ako je bio prekinut međurestartom, čeka oslobađanje `Global\_MSIExecute` mutexa (`Wait-MsiMutexAvailable -TimeoutSec 180`) i čisti `PendingFileRenameOperations`.
- **9 `StepGuard` koraka (`P4.Runtimes..P4.Activation`):**
  - `P4.Chrome` kešira i instalira offline Enterprise MSI (`~8s`).
  - `P4.Office` automatski prepoznaje i `Configure.xml` i `configuration.xml`, uklanja UTF-8 BOM, kopira offline `Office\Data` preko `Sync-ProgramiFromNas -Force` na lokalni SSD i upisuje lokalni `SourcePath` u `C:\setup\Office2024_Install.xml` (neophodno pod `NT AUTHORITY\SYSTEM`), uz `420s + 120s` C2R čekanje.
  - `P4.BatteryBar` konfiguriše Win11 Floating Toolbar na laptopovima (`SKIPPED` na desktopu); `P4.Shortcuts` proverava `.lnk` prečice za svaku stvarno instaliranu aplikaciju posebno (`Adobe Acrobat.lnk`, `Word.lnk`, `Excel.lnk`); `P4.PdfDefault` primenjuje DISM XML + GPO `AppAssoc_Acrobat.xml` + direktni upis u `HKLM:\SOFTWARE\Classes\.pdf`; `P4.Activation` obuhvata OEM OA3x (`OA3xOriginalProductKey`), `OSPP.VBS /act` i opcioni eksterni radionički hook (`Licensing\Invoke-CustomLicensing.ps1`).
- **Post-Execution Reality Check i UI Rerun:** `Assert-InstalledApplicationsReality` proverava fizičko prisustvo aplikacija na disku, a operater po potrebi može iz UI-ja ponovo pokrenuti Fazu 4 (`RerunPhase4Command` / `force-rerun-phase4.flag`).

---

### Faza 5: Ugrađeni Hardverski Test Suite & Termalni Stres (`ExecutePhase5IntegratedHwTest` / `WIS.HwTest`, 45 min)
- **Objedinjena orkestracija (`MainWindow.HwTest.cs` & `HwTestShell.StartPhase5FullWorkflow`):** U `WIS v3.0.0`, `WIS.UI.exe` direktno izvršava Fazu 5 unutar ugrađenog **9. taba (`🧪 Hardverski testovi`)** kroz tri sekvencijalna podkoraka (`GetPhaseTimeoutMinutes(5) = 45` min): `P5.AutoDiag` (7 automatskih testova: `auto_battery`, `auto_network`, `auto_security`, `auto_disk`, `auto_ram`, `auto_cpu`, `auto_gpu`) -> `P5.Stress` (opterećenje sa `ThermalSensorEngine.cs` refleksijom bez ugrađenog `WinRing0` i `_baseMaxClockMHz` throttling korekcijom) -> `P5.Peripherals` (9 interaktivnih testova: `display`, `keyboard`, `touchpad`, `touchscreen`, `audio`, `mic`, `camera`, `usb`, `fingerprint`). Na desktop računarima (`!_hwInfo.IsLaptop`), svih 9 interaktivnih testova dobija `TestStatus.NotApplicable`, `P5.Peripherals` se automatski označava kao `SKIPPED_DONE`, snima `C:\setup\hwtest-result.json` i odmah prelazi u Fazu 6 bez čekanja na tehničara.
- **CI / CLI Fallback (`Phase5-Stress.ps1`):** Koristi se automatski kada je definisan `WIS_STRESS_TEST_INPUT_JSON` ili pri samostalnom PowerShell testiranju (`Wait-WindowsUpdateCompletionBarrier`, 30s `RunspacePool` load).
- **100% Multi-Core `RunspacePool` Load Generator:** Pokreće N paralelnih niti (`[Environment]::ProcessorCount`) tokom tačno **30 sekundi**, mereći baznu, maksimalnu i prosečnu temperaturu i eventualni termalni throttling.
- **Kanonski Statusni Ugovor (`stress-test-result.json` & `WIS.StatusContract.psm1`):**
  - `PASS` ($< 80^\circ\text{C}$) / `WARNING` ($80\text{–}94^\circ\text{C}$): `cooling_verified = true`, `handover_ready = true`, `exit 0`.
  - `THERMAL_CRITICAL` ($\ge 95^\circ\text{C}$ ili Emergency Abort na $\ge 98.0^\circ\text{C}$): trenutno prekida opterećenje, postavlja `handover_ready = false`, `cooling_verified = false` i vraća **`exit 2`** (crveni ekran u UI-ju, blokada predaje).
  - `UNKNOWN` / `SENSOR_UNAVAILABLE` (hardver ne izlaže termalne senzore): **NE blokira instalaciju** — postavlja `handover_ready = true`, `cooling_verified = false`, `manual_check_required = true`, prikazuje žuto upozorenje u UI-ju i vraća **`exit 0`**.

---

### Faza 6: Drugi WU Prolaz, DMTF Servisni Karton, Anti-Leak Čišćenje i Samouništenje (`Phase6-Finalize.ps1`)
- **Brza WU Barijera & Second Pass (`Wait-WindowsUpdateCompletionBarrier` & `Invoke-WindowsUpdateSecondPass`):** Brzi prolaz barijere (`wu-secondpass-done.flag` za trenutni izlaz bez čekanja, gornji cap `60s`/`45s` u `Phase6-Finalize.ps1`, merenje CPU delte `>= 150ms` isključivo nad `tiworker`/`trustedinstaller` procesima bez čekanja na neaktivni `MoUsoCoreWorker`/`UsoClient`) sprečava 15-minutni zastoj pre čišćenja; zatim asinhroni `Start-Job` (do `300s`) instalira preostale zakrpe i Office ažuriranja (Microsoft Update katalog `7971f918-a847-4430-9279-4a52d1efe18d`).
- **Zaustavljanje Screen Mirror radnika:** Signalizira `Global\WIS_ScreenCapture_StopEvent` i gasi pozadinski posao snimanja ekrana.
- **DMTF Servisni Karton (`Send-FleetFinalReport` / `TelemetryService.cs`) & Termalna Štampa Nalepnica (`LabelPrintService.cs`):** Čuva autoritativnu vrednost `cooling_verified` iz `stress-test-result.json` u `COMPLETED` životnom događaju, šalje kompletan JSON izveštaj (`TK-{Chassis}-{Serial}`) na `ServiceHub` `/api/devices/{id}/report`, čuva lokalnu kopiju u `C:\Windows\Logs\WIS\` i omogućava automatsku štampu servisne etikete na Star TSP700II štampaču (konfiguracija `[Printer]`, sa `HW test` rezimeom iz `C:\setup\hwtest-result.json`).
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
