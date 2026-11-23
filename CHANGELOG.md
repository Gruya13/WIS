# Dnevnik Izmena (Changelog) — WIS

Sva značajna izdanja i izmene projekta **Windows Installation Assistant (WIS)** prate Keep a Changelog format i semantičko verzionisanje ([`version.json`](version.json)).

---

## [v3.4.3] / ServiceHub [v3.6.3] — 2026-11-23

### Ispravke bagova, Hard Gate interaktivnih testova, USB Auto-Learn i `zh-CN` Lokalizacija (`WIS-TASK-268`, `V343-01`–`V343-08`, `[SECURITY GREEN]`)
- **`V343-01` — Windows 11 24H2 `ConsentStore\location` & `P5.AutoDiag` (`ISO/SetupComplete.ps1`, `UI/Skripte/Modules/WIS.Branding.psm1`, `UI/HwTest/HwTestShell.cs`, `AutoHardwareDiagnostics.cs`, `NativeMethods.cs`):**
  - `CapabilityAccessManager\ConsentStore\location\Value` i `NonPackaged\Value` postavljaju se na `"Allow"` trajno (uz brisanje `DisableLocation = 1`, omogućavanje `lfsvc` i `Test-WisLocationAccessEnabled`), uz očuvano gašenje telemetrije (`AllowTelemetry = 0`, `DiagTrack`/`dmwappushservice` disabled, `ShowGlobalPrompts = 0`) i SHA-256 sinhronizaciju sve 3 kopije `SetupComplete.ps1` i obe kopije `WIS.Branding.psm1`.
  - `StartPhase5FullWorkflow()` uvek sveže izvršava `auto_battery`, `auto_network` i `auto_security` pri ulasku u Fazu 5 (osim ako su restaurirani iz UEFI BIOS checkpointa) i popunjava konkretan `reason` u `EmitPhase5Step("P5.AutoDiag", ...)`.
- **`V343-02` — Aktivacija Windows-a i Office-a + Anti-Leak (`UI/Skripte/Modules/WIS.Apps.psm1`, `WIS.Core.psm1`, `Wifi-Servis.example.ini`, `ISO/Wifi-Servis.example.ini`, `UI/TelemetryService.cs`, `ServiceHub/servishub/reports.py`):**
  - Dodata kodna podrška i zakomentarisani `[Licensing]` primer za Windows (`WindowsActivationMode`, `WindowsKmsHost`, `WindowsKmsPort`, `WindowsProductKey`) u oba `.example.ini` šablona (živi produkcijski `Wifi-Servis.ini` ostaje van Git-a).
  - Uveden `Get-WindowsLicenseStatus` (primarno WMI/CIM `SoftwareLicensingProduct` + `slmgr.vbs /xpr` timeout fallback), poziv `slmgr.vbs /ato` i bez BIOS `OA3xOriginalProductKey` (za digitalnu HWID licencu), te proširena maskirana polja (`windows_oa3_present`, `windows_edition`, `windows_license_method`, `windows_license_detail`) u `license-status.json`, `TelemetryService.cs` i `ServiceHub/servishub/reports.py`.
  - `Protect-WisLicenseLogText` (`-SensitiveHosts`) aktivno maskira i 25-znakovne ključeve i KMS hostove u logovima (`[SECURITY GREEN]`).
- **`V343-03` — Hard Gate interaktivnih testova pre Završnog kartona i krajnjeg izveštaja (`UI/MainWindow.HwTest.cs`, `MainWindow.xaml`, `MainWindow.xaml.cs`, `MainWindow.Reboot.cs`, `HwTestShell.cs`, `Phase6-Finalize.ps1`, `ServiceHub/servishub/reports.py`):**
  - Uklonjen automatski `manualWaitLimitMs` (`24h`/`45m`) auto-skip na laptopovima; uvedeni prominentni modalni `PendingInteractiveTestsOverlay` i stalna gornja traka `PendingInteractiveTestsBanner` sa brojačem preostalih testova i dugmetom za pokretanje (umesto prevremenog `CompletionOverlay` ili crnog `DisplayTestWindow`).
  - Vanredno preskakanje je moguće isključivo kroz `RequestEmergencySkipPhase5WithConfirmation()` uz obavezan razlog (`>= 5` znakova), dok `HasUnfinishedInteractiveHwTests()` i Hard Gate u `Phase6-Finalize.ps1` blokiraju Fazu 6 / `COMPLETED` dok `remainingPending > 0`, uz `ReuploadUpdatedReportAfterHwTestAsync` za naknadno odrađene testove.
  - `ServiceHub/servishub/reports.py` označava preskočene/nedovršene interaktivne testove kao `NEEDS_SERVICE` i `⚠️ NEPOTPUNO (preskočeni interaktivni testovi)` umesto `✅ PASS`.
- **`V343-04` — Tačna detekcija svetleće tastature bez lažnih pozitiva (`UI/MainWindow.Hardware.cs`, `Models.cs`, `WIS.Hardware.psm1`, `HardwareProbe.cs`, `FactorySpecProbe.cs`, `KeyboardTestWindow.cs`, `ServiceHub/servishub/hardware_spec.py`):**
  - Uklonjeni lažni PnP/WMI/BIOS detektori (`LEN0071`, `DELLABCE`, bezuslovni `Lenovo_SetKeyboardBacklightStatus`, `HP_BIOSSetting`, `DCIM_BiosEnumeration`), popravljena `TryProbeLenovoEnergyDrvBacklight()` provera capability bita `(outVal & 0x01u) != 0u`.
  - `ServiceHub/servishub/hardware_spec.py` podignut na `schema_version = 3` sa `has_keyboard_backlight` i `keyboard_backlight_evidence` (HP non-backlit BOM pravilo), `FactorySpecProbe.cs` dozvoljava da BOM `false` nadglasa slabije signale, dugme `⚪ Model nema osvetljenje` (`backlight_passed = "no_hardware"`) je uvek vidljivo u `KeyboardTestWindow.cs`, a rezultat Faze 5 se sinhronizuje sa Završnim i servisnim kartonom.
- **`V343-05` — Sklopljeni `FAZA TRACKER` po difoltu (`UI/Models.cs`, `UI/MainWindow.xaml`, `UI/MainWindow.xaml.cs`):**
  - Postavljeni `_isPhasesExpanded = false` i `_showStepsInOverview = false`, a `PhasesCollapseToggleBtn` premešten u gornji desni ugao zaglavlja (`▼ Prikaži korake` / `▲ Sakrij korake`) bez preklapanja 5. faze.
- **`V343-06` — Automatsko samoučenje broja USB portova po modelu (`UI/HwTest/Tests/UsbPortTestWindow.cs`, `ModelProfileClient.cs`, `HardwareProbe.cs`, `ServiceHub/servishub/model_profiles.py`, `handlers.py`):**
  - Razdvojeni `physical_port_locations` (`PCIROOT`/`ACPI`) i `charger_ports_count` uz `hasTypeCController` zaštitu; `POST /api/model-profile/usb-override` čuva `known_port_locations` i `observed_counts` uz anti-poisoning pravila za automatsko učenje naviše (`4 -> 5`).
- **`V343-07` — Unapređena sekcija `Dodatne periferije` (`UI/Models.cs`, `UI/MainWindow.Hardware.cs`, `UI/MainWindow.xaml.cs`):**
  - Uklonjeni bedževi `Thunderbolt / USB4` i `Web kamera`, dodat prikaz Bluetooth verzije (`DEVPKEY_Bluetooth_RadioLmpVersion` LMP `6..14` + fallback), unapređena `4G LTE` / `5G` WWAN detekcija (`netsh mbn` + PCI/USB VID/PID), dodata `eSIM` detekcija (`eUICC`/`EID`/`WwanSvc`) i automatsko osvežavanje posle Faze 2 drajvera.
- **`V343-08` — Kompletna `zh-CN` (`简体中文`) lokalizacija & CI Parity Gate (`UI/HwTest/Core/HwTestLocalization.cs`, `UI/LocalizationService.cs`, `UI/MainWindow.xaml`, `UI/Models.cs`, `UI/HwTest/**`, `UI/Tests/Test-LocalizationParity.py`):**
  - Uveden `HwTestLocalization.cs` (`937` `Pick`/`PickFormat` poziva kroz jezgro, dijagnostiku i svih `11` prozora u `UI/HwTest/Tests/*.cs`, `--lang zh`/`sr`, live `RefreshLocalization()`).
  - Proširen `UI/LocalizationService.cs` na `1021` SR / `1021` ZH ključeva (uz reviziju `207` starih prevoda i live sinhronizaciju sa `HwTestLocalization`), lokalizovano `66` XAML tekstova i svih `28` koraka faza u `Models.cs`, te dodat CI gate `UI/Tests/Test-LocalizationParity.py`.
- **Verzionisanje & Verifikacija:** Root [`version.json`](version.json) i svi podsistemi podignuti na **`WIS v3.4.3`** / **`ServiceHub v3.6.3`** (`Sync-Versions.py --check` `[PASS]`, `[SECURITY GREEN]`).

---

## [v3.4.2] / ServiceHub [v3.6.2] — 2026-11-21

### Live Audit Unapređenja, Sanacija Bagova i Anti-Cheat (`WIS-TASK-265`, `WIS-TASK-266`, `WIS-TASK-267`, `IMP-19`–`IMP-35`, `BUG-01`–`BUG-03`, `HWT-05`, `FEAT-01`)
- **ServiceHub performanse & stabilnost (`ServiceHub/servishub/drivers/pool.py`, `telemetry.py`, `benchmark.py`, `reports.py`, `handlers.py`, `Dockerfile`, `docker-compose.yml`):**
  - `IMP-19`: U `scan_and_ingest_archives` uvedena provera `(size_bytes, mtime_ns)` pre `_file_sha256`, 3-fazni `ingest_directory` i `build_bundle_tar` van `self.lock`, `cleanup_stale_temp_dirs()` (bez praćenja simlinkova) i `_stats_cache` (`< 50 ms`).
  - `IMP-20` & `IMP-23`: Reset polja stare sesije pri re-run-u u Fazi 1/2, čišćenje `DESKTOP-*` siročića u `cleanup_duplicate_devices_locked`, `_resolve_device_key_locked` u `record_logs` i 45s deduplikacija u `record_lifecycle_complete`.
  - `IMP-25`, `IMP-26`, `IMP-27`: Inkrementalni keš u `benchmark.py` (`< 50 ms`), čitanje kanonskih WIS JSON kartona u `get_device_history`, `tzdata` + `TZ=Europe/Belgrade` i `release_status = "production"`.
- **Klijentski pipeline, `BUG-01`–`BUG-03` i bezbednost (`UI/Skripte/Modules/WIS.Branding.psm1`, `WIS.Update.psm1`, `WIS.Core.psm1`, `WIS.Apps.psm1`, `Phase4-Apps.ps1`, `Phase6-Finalize.ps1`, `UI/MainWindow.xaml*`, `UI/App.xaml.cs`):**
  - `BUG-01` (`IMP-34`): Uklonjeni `DisableLocation = 1` i gašenje `lfsvc` iz `WIS.Branding.psm1` uz očuvanje telemetrijskih polisa i dodatu `IsLocationPolicyDisabled()` dijagnostiku u `AutoHardwareDiagnostics.cs` / `NativeMethods.cs`.
  - `BUG-02`: Dozvoljeno minimizovanje (`ResizeMode="CanMinimize"`) uz `EnsureWindowMaximized` čuvar (`SW_MAXIMIZE`, `300 ms` debounce i `WindowMaximizedGuardHook`).
  - `BUG-03` (`IMP-35`), `IMP-24`, `IMP-30`: Re-evaluacija Reality Check-a i automatski prelazak na Fazu 5 u `RetrySingleApp()`, traka `FailedPhaseRecoveryPanel`, `-PreserveCurrentPhase` i `WARN` (`exit 0`) za `License` Retry u `Auto`/`Skip` režimu.
  - `IMP-21`, `IMP-22`, `IMP-28` (kodni deo), `IMP-29`, `IMP-31`, `IMP-32`, `IMP-33`: Proširen `Add-WisBenchmarkTransfer` (`"driver_pool"`), `robocopy` 15s heartbeat, `4 MiB` `FileStream` kopiranje `<Model>.tar` i `15s` HTTP connection timeout, prepoznavanje `0x8024200d` (`WU_E_UH_NEEDANOTHERDOWNLOAD`), uklonjen trajni `_sensorTimedOut` short-circuit (`CreateBoundedWmiSearcher`), zagarantovan Anti-Leak `finally` blok u `Phase6-Finalize.ps1` i `Wait-WindowsUpdateCompletionBarrier` 1× u Fazi 6.
- **`HWT-05` Anti-Cheat & `FEAT-01` USB Easter Egg (`UI/HwTest/Tests/TouchpadTestWindow.cs`, `HwTestShell.cs`, `UsbPortTestWindow.cs`, `UI/HwTest/Assets/usb_skip_easter_egg.jpg`):**
  - `HWT-05`: `SPI_SETTOUCHPADPARAMETERS` (`SPIF_SENDCHANGE`), atomični `hwtest-touchpad-restore.json` i 3-slojni Anti-Brick recovery, RawInput klasifikacija (`ClassifyMouseRawDevice`) i `F9`/`Ctrl+F` prečice.
  - `FEAT-01`: Jednokratno mamac dugme `_btnUsbEasterEgg` u `UsbPortTestWindow.cs` sa `2.0 s` fullscreen slikom i `2 s` PCM sirenom u RAM-u uz puno očuvanje `HWT-02` zabrane preskakanja.

---

## [v3.4.1] / ServiceHub [v3.6.1] — 2026-11-21

### Dijagnostika Licenciranja u Fazi 4 i Strukturisana Telemetrija (`WIS-TASK-264`, `LIC-01`–`LIC-06`)
- **Centralizovana konfiguracija i ciljani NAS fallback (`UI/Skripte/Modules/WIS.Core.psm1`, `Wifi-Servis.example.ini`, `ISO/Wifi-Servis.example.ini`):**
  - Dodata funkcija `Get-ServisLicensingSettings` za čitanje `[Licensing]` sekcije iz `Wifi-Servis.ini` (`OfficeActivationMode`: `Auto` [podrazumevano], `Kms`, `Mak`, `ExternalHook`, `Skip`; `OfficeKmsHost`, `OfficeKmsPort` podrazumevano `1688`, `OfficeProductKey` bez logovanja vrednosti ključa).
  - U oba `.example.ini` šablona dodata je zakomentarisana `; [Licensing]` sekcija sa objašnjenjima parametara bez tajni.
  - Dodata ciljana funkcija `Sync-ServisLicensingFromNas` koja, kada je bar jedna licenca neaktivna, sa NAS deljenja (`Programi`, `Servis`, `WIS`) preuzima isključivo `Licensing` i `CustomScripts` u `C:\setup\Cache\Licensing`, nezavisno od toga da li je lokalni `C:\setup\Programi` već kompletan.
- **Tačna detekcija Office-a, puna `sppsvc` / `OSPP.VBS` dijagnostika i bezbedan lokalni runner (`UI/Skripte/Modules/WIS.Apps.psm1`):**
  - Uveden jedinstveni `Test-OfficeInstalled` (proverava svih 8 64-bit i 32-bit C2R/MSI putanja i uklanja dupliranu `WINWORD.EXE` putanju u `Get-WisAppPresence`), kao i `Get-OfficeLicenseStatus` (`$vfsSppPaths`, WMI/CIM `SoftwareLicensingProduct` i `OSPP.VBS /dstatus` sa `20s` timeoutom) uz očuvanje `Test-OfficeActivated` kao tankog wrappera.
  - U `Invoke-WindowsOfficeActivation` odmah po detekciji Office-a poziva se `Wait-OfficeInstallationReady -TimeoutSec 45`, proverava i pokreće servis `sppsvc` (do `5s`), hvata izlaz i exit kod alata `slmgr.vbs` i `OSPP.VBS` preko `Invoke-CscriptWithTimeout` (`25s` timeout + `taskkill.exe /T /F`), izdvaja `ERROR CODE` (npr. `0xC004F074`) u `Write-ServisLog` / `Send-IpcLog` / `P4.Activation` i atomično upisuje `C:\setup\license-status.json` uz događaj `LICENSE_CHECK_RESULT`.
  - Uveden jedinstven lokalni runner `Invoke-ServisExternalLicensingHook` sa keširanjem u `C:\setup\Cache\Licensing`, uklanjanjem MOTW oznaka (`Unblock-File` + `:Zone.Identifier`), lokalnim `WorkingDirectory`, kontekstnim promenljivama okruženja (`WIS_*`) i `180s` timeoutom sa prekidom stabla procesa.
- **Strukturisani izveštaji i maskiranje ključeva u sva 3 sloja (`UI/Skripte/Modules/WIS.Cleanup.psm1`, `UI/Skripte/Phases/Phase6-Finalize.ps1`, `UI/TelemetryService.cs`, `ServiceHub/servishub/reports.py`, `ServiceHub/tests/test_reports.py`):**
  - Prošireni završni podaci i servisni karton (`windows_activated`, `office_installed`, `office_activated`, `office_license_method`, `office_license_detail`, `office_activation_mode`, `licenses`) kroz `Export-ServisSummaryReport`, `EnrichLicenseTelemetryPayload` (za `COMPLETED` i `FINAL_REPORT`) i `reports.py` (`save_device_report`, `generate_markdown_report` sa sekcijom **`## 🔑 Status Licenciranja (Windows & Office)`**).
  - Aktivno regex maskiranje 25-znakova product ključeva (`*****-*****-*****-*****-XXXXX`) kroz sva 3 sloja: `Protect-WisLicenseLogText` (PowerShell), `MaskLicenseKeyText` (C#) i `_mask_license_keys` (Python).

### Bezbednosno Ojačanje Licenciranja i Izvršavanja Procesa (`[SECURITY GREEN]`)
- **Eliminisana shell interpolacija SMB lozinke (`UI/Skripte/Modules/WIS.Core.psm1`):** `Sync-ServisLicensingFromNas` koristi direktan poziv `net.exe` preko `ProcessStartInfo` (`Invoke-WisCoreNetUse` + `Format-WisCoreProcessArg`) bez `cmd.exe /c` shell interpolacije i bez logovanja lozinke, uz zaštitu od Error 1219 i bezbedni obilazak stabla bez praćenja `ReparsePoint`/`Hidden`/`System` stavki i neželjenih fajlova (`Copy-WisSafeLicensingTree`).
- **Path Traversal zaštita, `CommandLineToArgvW` citiranje i čišćenje keša (`UI/Skripte/Modules/WIS.Apps.psm1`, `WIS.Cleanup.psm1`, `Phase6-Finalize.ps1`):** `Invoke-ServisExternalLicensingHook` kanonizuje putanje (`GetFullPath`), primenjuje belu listu ekstenzija (`.ps1`/`.cmd`/`.bat`), odbija `ReparsePoint`-ove i opasne metakaraktere, koristi fail-closed `Copy-WisSafeDirectoryTree` keširanje u `C:\setup\Cache\Licensing`, pravilno Win32 citiranje (`Format-Win32CommandLineArgument`) i `cmd.exe /d /c`, dok `Invoke-WindowsOfficeActivation` validira `$officeKmsHost` i `$officeProductKey` i sanitizuje statuse. `Start-FinalCleanup` i `NEEDS_ATTENTION` grana brišu `C:\setup\Cache\Licensing` u Fazi 6.

### Backlog Unapređenja (`B1`–`B3`) & Verzionisanje
- **`B1` — Heartbeat sa veličinom za `.msu`/`.cab` (`UI/Skripte/Modules/WIS.Update.psm1`):** `Install-LocalUpdatePackageFile` izračunava veličinu paketa (`GB`/`MB`/`KB`), na svakih `20s` šalje `Send-IpcStatus` i `Send-IpcProgress` (uz clamp `0..99` kad je definisan `$Global:CurrentProgress`) i na svakih `60s` beleži napredak u log.
- **`B2` — Responzivni `150% DPI` raspored (`UI/MainWindow.xaml`, `UI/MainWindow.xaml.cs`):** Uvedena metoda `ApplyCompactViewportLayoutIfNeeded()` (povezana na `Loaded` i `SizeChanged`, sa generičkim helperom `FindTaggedElement<T>`) koja za kompaktne visine prozora (`ActualHeight < 760`) prilagođava `ActiveStepBarsScroll` (`MaxHeight = 96.0`), `GlobalProgressCard.Padding` i `OverviewMiddleGrid` `MinHeight`.
- **`B3` — Podrazumevani `DriversOnly` režim (`UI/Skripte/Modules/WIS.Update.psm1`):** Podrazumevani fallback u `Get-WUConfigMode` kada `[WindowsUpdate] Mode=` nije naveden promenjen je sa `All` na `DriversOnly`.
- **Verzionisanje:** Root [`version.json`](version.json) i svi sinhronizovani podsistemi podignuti na **`WIS v3.4.1`** / **`ServiceHub v3.6.1`** (`Sync-Versions.py --check` `[PASS]`).

---

## [v3.4.0] / ServiceHub [v3.6.0] — 2026-11-21

### UI/UX Redizajn i Live Kokpit (`WIS-TASK-261`, Koraci 1–35)
- **Čistiji okvir, globalni watermark i 3-zonski footer (`UI/App.xaml`, `UI/MainWindow.xaml`, `UI/MainWindow.xaml.cs`, `UI/LocalizationService.cs`, `UI/Models.cs`):**
  - Uklonjen tab `Mreža` iz vizuelnog interfejsa (ostaje **8 tabova** bez emoji prefiksa, dok mrežna svojstva ostaju za IPC/telemetriju).
  - Globalni pozadinski **`WIS`** watermark premešten iza celog `MainTabControl`-a (vidljiv na svih 8 kartica), dok tab `Autopilot` dobija pulsirajući crveni okvir pri aktivnom alarmu.
  - Novi 3-zonski footer: levo indikator + dinamička verzija **`ServiceHUB v<verzija>`** (`ProbeHub`), u sredini `Razvija i održava IG-Systems. Sva prava zadržana`, desno **`WIS v<verzija>`**; uklonjena pred-start prepreka `SERVIS SPREMAN` i kontrolna dugmad `Pauza`/`Preskoči`/`Odloži restart`.
- **Redizajn taba `Pregled` i `CompletionOverlay` (`UI/MainWindow.Hardware.cs`, `UI/MainWindow.Reboot.cs`, `UI/TelemetryService.cs`, `ServiceHub/servishub/handlers.py`, `dashboard.js`):**
  - Srednji panel `OverviewMiddleGrid` objedinjuje kompletne specifikacije uređaja (EDID/WMI dijagonala + format + tip panela @ Hz, npr. `14" FHD+ IPS @ 60 Hz`, proširena detekcija `WWAN` modema i periferija), live podkartice **`HARDVER MONITOR`** (`ReadLivePowerTelemetry`) i klikabilna **`UPOZORENJA`** (`RebuildOverviewAlerts`), kao i **`260×260` ServiceHub QR kod** sa deep-link rutama `/device/<identifier>` i `/karton/<identifier>`.
  - Sekcija `TOK INSTALACIJE I FAZE` prikazuje **5 vizuelnih faza** (`DisplayNumber` `1.`–`5.` uz očuvani backend ugovor faza `2..6`) sa uvek vidljivim koracima i `▲`/`▼` skupljanjem, a donja fiksirana `GlobalProgressCard` prikazuje trajanje i procenu **`Traje mm:ss | ETA ~mm:ss`** iz `/api/benchmark/summary`.
  - Redizajnirani Završni karton (`CompletionOverlay`) objedinjuje specifikacije, periferije, QR kod kartona i klikabilnu listu upozorenja sa kojima je proces završen.
- **Detaljni tab `HW Monitor` i `↻ Retry` programa (`Phase4-Apps.ps1`, `WIS.Apps.psm1`):**
  - Tab `Dijagnostika` pretvoren u **`HW Monitor`** sa 5 živih KPI kartica, 6-kanalnim 5-minutnim grafikonom (`CpuLoadGraphPoints` + termika), tabelom od 13 detaljnih senzora (`Min / Trenutno / Max`), taktovima po jezgrima i lazy `PerformanceCounter("PhysicalDisk")` bez `WinRing0`/LHM (Anti-BYOVD).
  - Kartice programa u tabu `Programi` dobijaju dugme **`↻ Retry`** za neuspešne/nepotpune aplikacije (`-OnlyApp` podrška za `DotNet`, `WinRAR`, `Chrome`, `Acrobat`, `Office`, `BatteryBar`, `Shortcuts`, `License`).
  - Vizuelne izmene u XAML-u podržane su idempotentnim runtime BAML rekonstrukcijama za kompatibilnost sa Linux Roslyn buildom do pune Windows MSBuild regeneracije `UI/Resources/WIS.UI.g.resources`.

### Unapređenja Hardverskih Testova (`WIS-TASK-263`, `HWT-01`–`HWT-07`)
- **`HWT-01` (`UI/HwTest/Tests/UsbPortTestWindow.cs`, `UI/HwTest/Interop/NativeMethods.cs`):** Rešena dvostruka detekcija USB porta pri izvlačenju uređaja uvođenjem `CM_Get_DevNode_Status` / `IsUsbDevNodeLiveAndStarted` filtera, odvajanjem kanonskog `RawPortKey` od prikaznog `DisplayLocationInfo`, zabranom upisa za `!newlyAttached`, `280 ms` `DispatcherTimer` debounce-om na `WM_DEVICECHANGE` i `< 500 ms` unplug cooldown-om.
- **`HWT-02` (`UI/HwTest/HwTestShell.cs`, `UI/HwTest/Tests/*.cs`):** Uklonjena mogućnost da serviser preskoči testove (dugme `»`, `PromptSkipReason`, `Preskoči test ...`, `ESC = preskoči` u `DisplayTestWindow`, ručno `Zaustavi`/`Prekini` tokom stresa); dugme za završetak je zaključano dok svi primenljivi testovi nemaju `PASS` ili `FAIL`, a nasilno zatvaranje prozora ostavlja `Pending` (uz očuvanje termalne zaštite `>= 95°C`, desktop `N/A` i daljinskog ServiceHub override-a).
- **`HWT-03` (`UI/HwTest/Core/AutoHardwareDiagnostics.cs`, `HwTestShell.cs`, `GpuStressWindow.cs`):** Dodata pre-flight blokada `TryValidateGpuDriverForStress` (plus odbrana po dubini u `GpuStressWindow`) koja sprečava pokretanje GPU 3D testa kada je aktivan `Microsoft Basic Display Adapter`, postoji PnP greška ili je `RenderCapability.Tier == 0`.
- **`HWT-04` (`HardwareProbe.cs`, `HwTestShell.cs`, `FingerprintTestWindow.cs`, `UI/MainWindow.HwTest.cs`):** Strogo filtriranje `IsFingerprintPnpCandidate` (eliminacija ELAN/Goodix touch lažnih pogodaka), buđenje `WbioSrvc` (`TryWakeWbioService`), uklanjanje `!FactoryBomLoaded` uslova, dugme **`🔧 Restartuj u UEFI BIOS (i nastavi WIS)`** i atomični checkpoint `hwtest-checkpoint.json` (`wis.hwtest.checkpoint.v1`) za automatski nastavak Faze 5 tačno gde je stao (uz race-zaštitu `_phase5UefiRebootInProgress`, `_uefiBiosPromptOpen`, `_uefiBiosRebootRequested`).
- **`HWT-05` (`HwTestShell.cs`, `TouchpadTestWindow.cs`, `HardwareProbe.cs`, `FactorySpecProbe.cs`):** Razdvojeni **`touchpad`** (`Touchpad`) i **`trackpoint`** (`TrackPoint`) testovi preko `PointingTestMode` (`TouchpadOnly` / `TrackPointOnly`) sa RawInput/HID razdvajanjem dodira i nezavisnim BIOS/BOM prepoznavanjem.
- **`HWT-06` (`HwTestShell.cs`, `CameraTestWindow.cs`, `HardwareProbe.cs`):** Razdvojeni **`camera`** (`Web kamera (QR kod)`) i **`ir_camera`** (`IR kamera (Windows Hello)`) preko `CameraTestMode` (`RgbOnly` / `IrOnly`), popravljena RGB vs IR PnP klasifikacija (`IsIrCameraEntry`, `IsNonPhysicalCameraImageDevice`), i unificirana matrica vidljivosti + UEFI BIOS restarta za sve periferne testove.
- **`HWT-07` (`AutoHardwareDiagnostics.cs`, `NativeMethods.cs`):** Unapređen pasivni RF Wi-Fi test unutar `auto_network` (`WlanQueryInterface` sa `wlan_intf_opcode_radio_state = 4u`, svež `WlanScan` + `WlanGetNetworkBssList` za `2.4/5/6 GHz` i RSSI, `TryReadServiceWifiSsid` bez čitanja lozinke i bez `WlanSetProfile`/`WlanConnect`, `FAIL` na RF Kill / `0` mreža, `PASS + WARN` na slab signal `< 55%` / `< -75 dBm` ili izostanak `5 GHz`).

### Integrisana Terenska Unapređenja (`WIS-TASK-262`, `IMP-01`–`IMP-18`) & Bezbednosna Revizija
- **Terenske popravke (`patch/v3.2.2` integrisan u radno drvo):** Auto-ingest Driver Pool arhiva (`scan_and_ingest_archives`, `_harvest_pool_watcher`, `POST /api/drivers/pool/notify-harvest`, `Send-ServisHubHarvestNotify`), `14400s` outlier filter u `benchmark.py`, deduplikacija izveštaja (`reports.py`), `Mode=DriversOnly` & chunkovani upload berbe (`Copy-FileWithHeartbeat`), asinhroni `120s` `SEARCH_TIMEOUT` u WU Pass 2, `Get-WisProblematicPnpDevices` za generičke GPU adaptere, `Sync-ProgramiFromNas` pre `winget` u `P4.WinRAR`/`P4.Chrome`, `Send-IpcAppUpdate`, `4000 ms` watchdog senzora i detekcija statičke ACPI zone (`WMI_ACPI_STATIC`).
- **Security Gate (`[SECURITY GREEN]`):** Dodata fail-closed token autentifikacija na `POST /api/drivers/pool/notify-harvest` (`_require_client_upload_token()` u `ServiceHub/servishub/handlers.py` + `Get-ServisHubClientToken` / `X-ServisHub-Client-Token` u `UI/Skripte/Modules/WIS.Update.psm1`) i zatvoren race prozor oko `MessageBox.Show` u `HwTestShell.RequestUefiBiosReboot` (`_uefiBiosPromptOpen`).
- **Verzionisanje:** Root [`version.json`](version.json) i svi sinhronizovani podsistemi podignuti na **`WIS v3.4.0`** / **`ServiceHub v3.6.0`** (`Sync-Versions.py --check` `[PASS]`).

---

## [v3.2.1] / ServiceHub [v3.5.0] — 2026-11-19

### Hitni hotfix: StepGuard PowerShell 5.1 Deadlock & `P4.WinRAR` otpornost (`WIS-TASK-260`)
- **Uklonjen PowerShell 5.1 deadlock u `Invoke-WisScriptBlockWithTimeout` (`UI/Skripte/Modules/WIS.StepGuard.psm1`):**
  - Zamenjen `PSThreadOptions::ReuseThread` sa `UseNewThread` (uz `ApartmentState::MTA`), čime je sprečena 100% blokada u koraku `P2.Identify` (`15%`).
  - Uveden selektivan uvoz samo potrebnih `WIS.*` modula za dati `$ScriptBlock` (preko `ExportedCommands` i `WisTelemetryConfig|WisBloatPatterns`, uz automatsku prateću podršku `WIS.StatusContract` / `WIS.Common`) umesto slepog uvoza svih učitanih modula pri svakoj `-Test` proveri.
  - Zamenjen blokirajući `Stop()`/`Dispose()`/`Close()` pri isteku timeouta non-blocking pozivima `BeginStop($null, $null)` i `BeginClose($null, $null)`.
- **Otpornost i dijagnostika `P4.WinRAR` (`UI/Skripte/Modules/WIS.Apps.psm1`):**
  - Rezultat `Get-ChildItem` umotan u `@(...)` niz za bezbednost u PowerShell 5.1, dodata post-provera prisustva WinRAR-a sa jasnim `WARN` / `Send-IpcLog` logovanjem putanje i `ExitCode`, automatski `1×` retry sa `/S` posle `2s` pauze i objedinjeni `Winget` fallback.
- **Windows PowerShell 5.1 CI provera i verzionisanje:**
  - `UI/Tests/Test-StepGuard.ps1` proširen testovima 10–12 (reprodukcija `P2.Identify` sa svim `WIS.*` modulima, non-blocking timeout, selektivno učitavanje `WIS.Apps`/`WIS.Branding`) i uključen u `ISO/Skripte/Build-WIS.ps1` za `windows-2022` CI.
  - Verzija WIS komponenti podignuta na `3.2.1` (uz nepromenjen `ServiceHub v3.5.0`).

---

## [v3.2.0] / ServiceHub [v3.5.0] — 2026-11-19

### Jedinstveni deduplikovani Driver Pool (CAS) & Zvanični OEM Enterprise Katalozi (`WIS-TASK-258`)
- **Jedinstveni Content-Addressable Driver Pool (`ServiceHub/servishub/drivers/pool.py`):**
  - Centralno skladište pojedinačnih `.inf` drajver jedinica indeksiranih po determinističkom `SHA-256` sadržaja (`packages/<pkg_sha256>/`), globalnom pobedniku po HWID-u (`hwid_winners` u `pool_index.json`) i lakim manifestima po modelu (`manifests/<clean_model>.json`).
  - **0 B dupliranja preko sva 3 izvora** (**OEM Enterprise Driver Packs**, **Microsoft Update Catalog** `.cab` paketi i **klijentska berba** `<Model>.tar`): identičan paket se nikada ne čuva dvaput na disku, stariji paketi za isti HWID se odbacuju bez degradacije, dok novija verzija (`DriverDate`/`DriverVer`) i WHQL `.cat` potpis automatski pobeđuju i ažuriraju manifeste.
  - Stroga bezbednost na ulazu i izlazu: automatsko ignorisanje inbox štampača `prnms009.inf`, odbijanje nepoptisanih paketa sa skriptama, uklanjanje `.ps1`/`.bat`/`.cmd`/... iz paketa u Pool-u, i zaštita od path traversal-a i symlinkova za `.tar`, `.zip`, `.cab`, `.7z` i SFX `.exe` arhive.
  - Dinamičko sklapanje i keširanje optimizovanih `.tar` paketa (`build_bundle_tar`) — po punom manifestu modela (uz `all_hwids` dopunu za generičke desktop ploče) ili ciljanih mini-paketa po `missing_hwids` sa prioritetom tačnog `SUBSYS` meča.
- **Zvanični OEM Enterprise XML Katalozi (`ServiceHub/servishub/drivers/oem/catalogs.py`):**
  - `OemCatalogManager` sinhronizuje i lokalno kešira zvanične kataloge za **Dell** (`DriverPackCatalog.cab`), **HP** (`HPClientDriverPackCatalog.cab`) i **Lenovo** (`catalogv2.xml`), sa XXE zaštitom (`<!ENTITY`/`<!DOCTYPE` blokada), strogom HTTPS host allowlist-om i verifikacijom `SHA-256`/`MD5` kontrolnih suma pri preuzimanju paketa.
  - `ServiceHub/Dockerfile` proširen je alatima `p7zip-full` i `cabextract` uz `libarchive-tools` (`bsdtar`) radi raspakivanja `.cab` i SFX `.exe` OEM paketa.
- **REST API, Auto-Ingest, Migracija i Web Cockpit UI (`ServiceHub v3.5.0`):**
  - Nove rute u `handlers.py`: `GET /api/drivers/pool/{stats,manifests}`, klijentski `POST /api/drivers/pool/bundle` i fail-closed admin rute `POST /api/drivers/pool/{sync-catalogs,ingest-oem,migrate,prune}` (`_require_admin_token`).
  - Automatsko punjenje Pool-a u pozadini pri `POST /api/drivers/model-pack/upload` (`harvest`) i `DriverCacheManager.save_cache` (`ms_catalog`), kao i transparentni fallback na Driver Pool manifest u `GET /api/drivers/model-pack/{status,download}` i integracija u `PredictiveCacheManager`.
  - Nova kartica u Web Cockpit-u (`tab-updates`): pregled statistike deduplikacije, statusa OEM kataloga, manifesta modela i `adminFetch` kontrole za sinhronizaciju kataloga, preuzimanje OEM paketa, migraciju i `prune`.
- **Klijentska PowerShell 5.1 integracija (`UI/Skripte/Modules/WIS.Update.psm1`, `WIS.Hardware.psm1`):**
  - Dodat `Invoke-DriverPoolBundleInstall` i ugrađen kao **Prioritet 1** u `Invoke-ModelDriversPreInstall` (uključujući HWID rezoluciju i za generičke matične ploče) uz potpuni SMB/`model-pack` fallback, kao i jednokratni batch pre-pass za `missing_hwids` u `Resolve-MissingDriversFromHub` (`WIS-DriverEngine/3.2`).
- **Verzionisanje i Testovi:** Root [`version.json`](version.json) podignut na `WIS v3.2.0` / `ServiceHub v3.5.0` (`Sync-Versions.py` proširen i za `UI/HwTest`), dodat obiman test paket [`ServiceHub/tests/test_driver_pool.py`](ServiceHub/tests/test_driver_pool.py) i prošireni [`test_dashboard_ui.py`](ServiceHub/tests/test_dashboard_ui.py) i [`Test-UpdateAndDriversLogic.py`](UI/Tests/Test-UpdateAndDriversLogic.py).

---

## [v3.1.1] / ServiceHub [v3.4.1] — 2026-11-19

### Ispravke bagova sa terena (`BUG-01` – `BUG-09`)
- **`BUG-01` (`UI/HwTest/Tests/AudioTestWindow.cs`, `MicTestWindow.cs`):** Eliminisan WinMM/COM deadlock na prelazu Audio $\rightarrow$ Mic testa uvođenjem `ManualResetEventSlim _waveOutDoneEvent` sinhronizacije (`StopActivePlayback`), jedinstvenim praćenjem govora cifara preko `PlayWaveOutTracked`, izmeštanjem svih `_audioVolume` COM poziva na STA UI nit preko `Dispatcher.BeginInvoke`, te prebacivanjem enumeracije mikrofona, `TryUnmuteAndLogCaptureEndpoint()` i `waveInOpen` iz `Loaded` UI handlera na namensku pozadinsku STA nit (`StartMicInitAsync`) sa `100 ms` grace pauzom i thread-safe `_waveInLock` čišćenjem.
- **`BUG-02` (`UI/Skripte/Modules/WIS.Hardware.psm1`):** U `Resolve-MissingDriversFromHub` uveden `Invoke-HubDriverResolvePost` sa `Restore-ServisNetworkConnectivityIfNeeded -WaitSeconds 15` i do `2` retry pokušaja pri mrežnim greškama (opravak od PnP mrežnog reseta posle čipset/SMBus `3010` drajvera), plus `2s` stabilizacija posle instalacije čipset/mrežnih/`3010` drajvera.
- **`BUG-03` (`UI/Skripte/Modules/WIS.Hardware.psm1`):** Prag u `Select-RelevantDriverInfFiles` podignut sa `<= 3` na `<= 12` `.inf` fajlova i dodato očuvanje pratećih `Extension`/`SoftwareComponent` `.inf` modula u većim paketima; `Install-OemDriverFromHub` instalira kompletan paket sa `pnputil.exe /add-driver "<extractDir>\*.inf" /subdirs /install` kada nisu vršena rezanja.
- **`BUG-04` (`UI/Skripte/Modules/WIS.Update.psm1`):** `ServisWuActive=1/0` proširen oko batch download-a u `P2.WuDrivers`, dodati `$installer.ForceQuiet = $true`, provera `ResultCode -in @(2, 3)` / `HResult`, `1` retry posle `3s`, uklonjen prazan `catch {}` uz eksplicitno logovanje i dodat dopunski `Resolve-MissingDriversFromHub` po završetku `P2.WuDrivers`.
- **`BUG-05` (`UI/Skripte/Modules/WIS.Update.psm1`):** Popravljen PowerShell 5.1 bag u `Invoke-WindowsUpdateSecondPass` gde se `-ArgumentList (,$updateTitles)` vezao kao `@("System.Object[]")` — sada se prosleđuje `$titlesJson` (`ConvertTo-Json` / `ConvertFrom-Json`) i broji se stvarni uspeh svake pojedinačne stavke po `GetUpdateResult($k).ResultCode -in @(2, 3)`.
- **`BUG-06` (`UI/Skripte/Modules/WIS.Update.psm1`, `Phase6-Finalize.ps1`):** `Invoke-BackgroundWindowsUpdateWorker` produžava čekanje na kraj Faze 4 (`Phase4Active`) sa `360s` (`120` ciklusa) na `900s` (`300` ciklusa) i prijavljuje `Status = "WARN"` (umesto `COMPLETED`) kada pri `0/N` ništa nije instalirano; Faza 6 izvršava završni `Resolve-MissingDriversFromHub` pre gašenja mreže ako Device Manager ima preostale greške.
- **`BUG-07` (`ServiceHub/servishub/handlers.py`, `scraper.py`, `UI/MainWindow.xaml.cs`, `WIS.Hardware.psm1`):** Uvedeno dedupovano pozadinsko preuzimanje velikih MS Catalog `.cab` paketa u `ServiceHub` (`async_download: true` $\rightarrow$ `status: "downloading"` + `retry_after_sec`) i klijentski polling petlje u `WIS.UI` (sa slanjem stvarnog `model`/`serial_number` i `Timeout = 120000 ms`) i `Resolve-MissingDriversFromHub`.
- **`BUG-08` (`WIS.Update.psm1`, `Phase6-Finalize.ps1`):** Dodat `Test-WisDeviceManagerReadyForHarvest` guard u `Export-ServisHardwareDrivers` i na kraju `Invoke-Phase2Drivers` (`driver-harvest-deferred.flag`), čime je sprečeno snimanje nepotpunih `<Model>.tar` arhiva na NAS dok Device Manager ima kritične greške (odlaže se do čistog stanja na kraju Faze 6); takođe obrisan ranije zagađen `83JE.tar` (`324 MB`) sa produkcionog NAS-a.
- **`BUG-09` (`UI/HwTest/Core/AutoHardwareDiagnostics.cs`, `GpuStressWindow.cs`):** Proširen `InspectGpuAdapters` sa praćenjem `hasAnyBasicDisplay`, `hasAnyErrorAdapter` i `problemAdaptersSummary` preko svih `Win32_VideoController` adaptera tako da neispravan sekundarni/diskretni GPU obavezno daje `TestStatus.Fail` umesto lažnog `PASS` na integrisanom iGPU-u.

### Vlasništvo i Dokumentacija
- **IG-Systems:** Kompletna javna i interna dokumentacija, licencni zaglavlja (`LICENSE`, `AssemblyInfo.cs`), konfiguracioni šabloni (`Wifi-Servis.example.ini`), QR PDF generator, QA nazivi uređaja i podrazumevani `ShopName` u `LabelPrintService.cs` prebačeni na vlasnika sistema **IG-Systems**.

---

## [v3.1.0] / ServiceHub [v3.4.0] — 2026-11-19

### Bezbednost (Security Hardening)
- **Potpuno uklanjanje `WinRing0` / `LibreHardwareMonitorLib.dll` i `HidSharp.dll` (Anti-BYOVD, CVE-2020-14979):** Trajno izbačeni iz Git repozitorijuma, `.csproj` i Mono build skripti, OTA manifest ugovora (`REQUIRED_310_COMPONENTS` / `MandatoryRelease310Files`) i USB/media pakovanja (`Release-WIS.py`, `Build-PreviewMedia.py`, `deployment_manifest.json`).
- **Anti-BYOVD zaštita i čišćenje zaostalog drajvera:** `UI/ThermalSensorEngine.cs` blokira i uklanja eventualno ručno postavljeni `LibreHardwareMonitorLib.dll` (bez `Assembly.LoadFrom`); `WIS.Cleanup.psm1` (`Remove-LegacyWinRing0Driver`) gasi i briše eventualni servis `WinRing0_1_2_0` i prateće artefakte pri Fazi 6; `ISO/FirstLogon.cmd` čisti zaostale kopije iz prethodnih medijuma.
- **Anti-downgrade zaštita i `.sys` nadzor u Launcheru:** `Launcher/PayloadManager.cs` uvodi `MinAcceptedPayloadVersion = "3.1.0"`, odbija pakete sa `WinRing0`/`LibreHardwareMonitorLib.dll`/`HidSharp.dll` za `3.1.0+`, i uključuje `.sys` u proveru nepotpisanih izvršnih/drajverskih datoteka.
- **Post-verifikacija bezbednosnog oporavka i Sentinel:** `Restore-WisSecurityHardening` (`WIS.Cleanup.psm1`) čitanjem registra i Defender polisa verifikuje povratak UAC, SMB, AutoAdminLogon i Defender parametara (`$Global:WisSecurityHardeningVerified`), dok `Phase6-Finalize.ps1` upisuje `security_hardening_verified` i blokira predaju (`NEEDS_ATTENTION`, `exit 2`) ako provera ne uspe. `WIS.Core.psm1` (`Register-WisSecurityRecoverySentinel`) registruje `OnStartup` sentinel zadatak u slučaju nasilnog prekida pre Faze 6.
- **Fail-closed autentifikacija i CORS allowlist (`ServiceHub v3.4.0`):** Administrativne/destruktivne rute zahtevaju `SERVIS_HUB_ADMIN_TOKEN` (HTTP `503` bez tokena ako nije eksplicitno uključen `SERVIS_HUB_ALLOW_UNAUTHENTICATED_LAN`); `/api/updates/upload` i `/api/drivers/model-pack/upload` podržavaju `SERVIS_HUB_CLIENT_TOKEN` (`X-ServisHub-Client-Token`, uključujući klijentsko čitanje iz `Wifi-Servis.ini` u `WIS.Update.psm1`); zamenjeno globalno `Access-Control-Allow-Origin: *` sa konfigurabilnom allowlist-om (`SERVIS_HUB_CORS_ORIGINS`); uvedena `429` ograničenja za SSE pretplatnike (`max_sse_subscribers_total` / `per_device`).
- **Bezbednost `.tar` / model paketa:** Dodata validacija arhive (`_validate_model_pack_tar_archive` u `ServiceHub/servishub/api/updates.py` i provera zabranjenih skripti `.ps1`/`.bat`/`.cmd`/`.vbs`/`.js` u `Install-DriverTarArchive` u `WIS.Update.psm1` + logovanje `.cat` kataloga); uklonjene hardcoded host putanje.
- **Dokumentovana odluka za lokalni nalog `Korisnik`:** Svesna odluka za radionički „ključ u ruke“ model opisana u [`SECURITY.md`](SECURITY.md) sa preporukom kupcu da postavi PIN/lozinku po preuzimanju.
- **CI i čista istorija:** GitHub Actions pinovan po punom SHA-256/commit hešu, `permissions: contents: read`, dodati `ruff --select F821` i provera zabranjenih DLL-ova; očišćeni svi tragovi neovlašćenih aktivacionih alata iz koda, testova i Git istorije, uz anonimizaciju serijskih brojeva i hostname-ova u dokumentaciji.

### Pouzdanost, Telemetrija i Performanse
- **Popravka split-brain trke `ServisWuActive` i koordinacija Faze 4 (`Phase4Active`):** Svi upisi/čitanja `ServisWuActive` objedinjeni na `HKLM:\SOFTWARE\WIS`, a pozadinski Windows Update radnik (`Invoke-BackgroundWindowsUpdateWorker`) čeka završetak Faze 4 (`Phase4Active = 1` postavljen u `try/finally` u `Phase4-Apps.ps1`) pre pokretanja MSI/WU instalacija.
- **Jedinstveni termalni prag (`95°C × 2` / `97°C` instant abort):** Poravnat u `UI/MainWindow.xaml.cs`, `UI/Skripte/Modules/WIS.Hardware.psm1` i `UI/Skripte/Phases/Phase5-Stress.ps1` (sa `AutoHardwareDiagnostics.cs`), uz uklanjanje `98°C` pragova i dodavanje Dell `root\dcim\sysman:DCIM_NumericSensor` fallback-a u `UI/ThermalSensorEngine.cs`.
- **`WIS.HwTest` ergonomija i razdvajanje vremena čekanja operatera (`UI/HwTest/HwTestShell.cs`):** Uvedena polja `operator_wait_sec` i `active_hwtest_sec` u `hwtest-result.json`, diskretni pozadinski dvotonski audio signal pažnje (`880Hz` -> `1175Hz`) nakon automatskih testova, i agregacija kohorti (`v3_modern` vs `legacy`) i P5 čekanja u `ServiceHub/servishub/benchmark.py`.
- **Granularna `phase4_steps_sec` telemetrija:** Dodata funkcija `Record-WisStepBenchmark` u `WIS.Common.psm1` i automatsko beleženje u `WIS.StepGuard.psm1`.
- **Cross-model `.cab` HWID indeksiranje i kanonska konsolidacija uređaja (`ServiceHub v3.4.0`):** `servishub/drivers/cache.py` indeksira raspakovane `.inf` HWID-ove pomoću `bsdtar` (`inf_hwid_index.json`); `servishub/telemetry.py` deduplikuje uređaje po serijskom broju / SMBIOS UUID-u, ograničava RAM za Screen Mirror (`TTL` + FIFO izbacivanje), rotira žive logove (30 dana) i sesije (24h), i koristi atomske upise preko `.tmp + fsync + os.replace`.
- **Uklonjene lažne 100% keš vrednosti i popravljen `system_inspector.py`:** `benchmark.py` vraća `None` (`N/A` u Cockpit UI-ju) kada nema zahteva/bajtova, koristi jedinstveni `compute_handover_status` iz `reports.py`, a `ISO/USB Tool/core/system_inspector.py` dobija nedostajući `import subprocess`.

---

## [v3.0.0] / ServiceHub [v3.3.0] — 2026-11-18
- Integrisani `WIS.HwTest` kao 9. tab i objedinjena Faza 5 (`45 min`), `WIS Benchmark` i `Predictive Cache` (`Model DNA`), dvosmerni HTTP/NAS `<Model>.tar` i `.msu`/`.cab` keš, Pre-OOBE Autopilot keš, Star TSP700II termalna štampa nalepnica i konsolidacija `ServiceHub` izveštaja (`ADR-0225`, `ADR-0236`, `ADR-0247`).
