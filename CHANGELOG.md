# Dnevnik Izmena (Changelog) — WIS

Sva značajna izdanja i izmene projekta **Windows Installation Assistant (WIS)** prate Keep a Changelog format i semantičko verzionisanje ([`version.json`](version.json)).

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
