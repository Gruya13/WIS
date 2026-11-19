# ADR-0236: WIS Benchmark, Predictive Cache (`Model DNA`) i Post-Telemetry Sistemsko Očvršćavanje

## Status
Prihvaćeno, implementirano, testirano i potvrđeno u živom radu na TrueNAS produkciji (2026-11-17)

## Kontekst
Nakon stabilizacije `WIS v3.0.0` (`ADR-0225`), tokom praćenja živih instalacija na različitim klasama uređaja (**Lenovo ThinkCentre M70q Gen 3**, **HP EliteBook 840 14 inch G10** `LAPTOP-DEMO-01`, **Lenovo ThinkPad T14 Gen 2i** `LAPTOP-DEMO-02` / `SN-DEMO-02` i **Lenovo ThinkPad X13s Gen 1** ARM64 `ARM-DEMO-03` / `21BYS0211G` — `WIS-TASK-232 .. WIS-TASK-236`), uočene su ključne potrebe i prilike za sistemsko unapređenje:
1. Nedostajala je objektivna, automatska analitika trajanja pojedinačnih faza (`Phase1`–`Phase6`), LAN/WAN protoka, efikasnosti keša (`Cache Hit Ratio`) i P50/P90 statistike po modelima računara, kao i mogućnost pripreme (pre-warming) keša za najavljeni računar pre nego što stigne na servisni sto.
2. Tokom živog testa na `ThinkPad T14 Gen 2i` (`20W0009HMH`), multi-OEM `.cab` paket sa `212` Realtek `.inf` datoteka instalirao se `3m 23s` i izazvao naduvavanje ubranog `.tar` paketa sa `250 MB` na `21 GB`, dok su na TrueNAS-u uočene i starije naduvane arhive, nesinhronizovani `/mnt/TK_NAS/Servis/*` i `/mnt/TK_NAS/WIS/shared/*` putevi, `GenericPC.tar` (`2.80 GB`) i `outer.AggregatedMetadata_KB5132061.cab`.
3. Na `HP EliteBook 840 G10` (`LAPTOP-DEMO-01`), ostavljeni Lean USB fleš blokirao je NAS fallback na `Programi`, dok je u Fazi 6 ponovna pre-instalacija `<Model>.tar` drajvera bespotrebno produžavala završnicu.
4. Na `ThinkPad T14 Gen 2i` i ARM64 `ThinkPad X13s Gen 1` u `WIS.HwTest`, pasivni `netsh` scan na čistom LAN kablu vraćao je `0` Wi-Fi mreža, UEFI `Firmware [Code 14]` (`CM_PROB_NEED_RESTART`) tretirao se kao PnP greška, a big.LITTLE ARM64 procesor pod niskom temperaturom (`< 75°C`) prijavljivao je lažno throttling upozorenje.

## Donete Odluke

1. **WIS Benchmark Analitika (`UI/Skripte/Modules/WIS.Common.psm1`, `WIS.Telemetry.psm1`, `ServiceHub/servishub/benchmark.py`):**
   - Klijent prati start/kraj svake faze i sve mrežne/keš transfere (`Start-WisPhaseBenchmark`, `Complete-WisPhaseBenchmark`, `Add-WisBenchmarkTransfer`, `Get-WisBenchmarkSummary`) u `C:\setup\benchmark-metrics.json` i ugrađuje sažetak u završni izveštaj (`Send-FleetFinalReport`).
   - `ServiceHub` uvodi `servishub/benchmark.py` (`574` linije) sa rutama `GET /api/benchmark/summary` i `GET /api/benchmark/stats`: računa trajanja po fazama (sa `_parse_duration_str` fallback-om na `"HH:MM:SS"`), LAN/WAN `Mbps` protok, `Cache Hit Ratio` / `Byte Hit Ratio`, `First-Pass Success Rate`, `P50`/`P90`, spaja parcijalne `report_DESKTOP-*` i pune `TK-*` zapise (`_merge_report_records`), kanonikalizuje MTM nazive preko `model_profiles` aliasa (`_resolve_canonical_model_label`) i prikazuje `#wis-benchmark-panel` u Web Cockpit-u.

2. **Predictive Cache & Model DNA (`ServiceHub/servishub/predictive_cache.py`, `model_profiles.py`, `WIS.Hardware.psm1`):**
   - `Send-WisModelDnaToHub` u `WIS.Hardware.psm1` šalje PnP HWID otiske i `model_aliases` parove (MTM ⬌ marketinški naziv) na `POST /api/model-profile/dna`, dok `update_model_dna` i pasivni `/api/drivers/resolve` uče profil modela bez duplikata (`GET /api/model-profiles`).
   - `PredictiveCacheManager` (`servishub/predictive_cache.py`, `313` linija) i rute `POST /api/predictive/prewarm` / `GET /api/predictive/status` (UI panel `#predictive-prewarm-panel`) unapred pripremaju garanciju, BOM (`hardware_spec.py`), `.cab` / `<Model>.tar` drajvere i proveru Win11 zakrpi uz `readiness_score_pct` (`0–100%`).

3. **SUBSYS/HWID `.inf` Filtriranje, `DriverHarvest` Zaštite i Faza 4/6 Očvršćavanje (`WIS.Hardware.psm1`, `WIS.Update.psm1`, `WIS.Core.psm1`, `WIS.Apps.psm1`):**
   - `Select-RelevantDriverInfFiles` filtrira raspakovane multi-OEM `.cab` pakete isključivo na `.inf` fajlove relevantne za HWID/SUBSYS prisutnog hardvera pre `pnputil.exe`.
   - `Test-IsGenericComputerModel` blokira preuzimanje/snimanje `<Model>.tar` za generičke matične ploče (`GenericPC`, `Unknown_Model`), dok `Export-ServisHardwareDrivers` štiti postojeće zdrave arhive (`1 MB .. 1.5 GB`) i automatski čisti tuđe `hdx*.inf_*` OEM varijante i starije duplikate po `DriverVer`.
   - Faza 2 snima `C:\setup\model-drivers-preinstalled.flag` i `C:\setup\nas-prepatch-done.flag`; Faza 6 poziva `Invoke-NasUpdatePrePatch -SkipDriverPreInstall`, a oba dela ignorišu `*AggregatedMetadata*.cab` / `wsusscan.cab`.
   - `WIS.Core.psm1` (`Test-ProgramiDirComplete`) i `WIS.Apps.psm1` (`Test-ProgramiPackageComplete`) normalizuju `Username="servis"` u `"admin"` i forsiraju `Sync-ProgramiFromNas -Force` ako Lean USB ne sadrži kompletan Office/Acrobat paket.

4. **`WIS.HwTest` Refinements (`NativeMethods.cs`, `AutoHardwareDiagnostics.cs`) i TrueNAS Unifikacija:**
   - `NativeMethods.cs` dodaje `wlanapi.dll` P/Invoke (`TriggerActiveWlanScan`) koji se okida kada `netsh` na LAN-only mašini vrati `0` Wi-Fi mreža.
   - `AutoHardwareDiagnostics.cs` izdvaja UEFI `Firmware` sa `ConfigManagerErrorCode == 14` (`CM_PROB_NEED_RESTART`) u `pending_firmware_reboot` informaciju, i uvodi `lowCoolTemp` (`< 75.0°C`) uslov da ARM64/big.LITTLE procesori ne prijavljuju lažni thermal throttling pri efikasnim frekvencijama.
   - `ServiceHub` (`config.py`, `api/updates.py`) daje prioritet `/app/harvested/Archives` putanji sa `:ro` upload fallback-om i multi-path/alias lookup-om; na TrueNAS hostu `/mnt/TK_NAS/Servis/Drivers` i `Updates` povezani su bind-mountom sa `/mnt/TK_NAS/WIS/shared/*`, očišćeno je 12 naduvanih `.tar` arhiva, uklonjeni `GenericPC.tar` i `outer.AggregatedMetadata_KB5132061.cab`, i urađen backfill za `34` `Model DNA` profila i `41` legacy izveštaj.

## Posledice
- **Pozitivne:** Eliminisano `21 GB` `.tar` naduvavanje, `3m 23s` čekanje na strane `.inf` fajlove, dupla Faza 6 instalacija drajvera i lažni `WIS.HwTest` alarmi na ARM64/UEFI/LAN uređajima; omogućeno kvantitativno praćenje performansi radionice kroz `WIS Benchmark` i pre-arrival keširanje preko `Predictive Cache`.
- **Verifikacija:** `Sync-Versions.py --check` (`PASS`), `Verify-PowerShellSyntax.py` (`22/22`), `Verify-CSharpSyntax.py` (`48/48`), `UI/Tests` (`37/37`), `Launcher/Tests` (`31/31`), `ServiceHub/tests` (`286`: `266 PASS, 20 skipped`), potpisani `wis-payload.zip` (`68,495,850 B`, `db73b6ec...`) i live API potvrda na TrueNAS-u (`144` benchmark uređaja, `33` modela u `predictive/status`).
