# Dnevnik Izmena (Changelog) — WIS

Sva značajna izdanja i izmene projekta **Windows Installation Assistant (WIS)** prate Keep a Changelog format i semantičko verzionisanje ([`version.json`](version.json)).

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
