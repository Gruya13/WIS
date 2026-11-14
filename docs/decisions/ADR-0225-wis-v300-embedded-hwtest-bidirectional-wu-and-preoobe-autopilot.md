# ADR-0225: WIS v3.0.0 — Ugrađeni HwTest (Faza 5 + 9. Tab), Dvosmerni WU/Driver Keš, Pre-OOBE Autopilot i Termalna Štampa

## Status
Prihvaćeno i implementirano (2026-11-14)

## Kontekst
Između verzija `v2.19.0` (`ServiceHub v3.2.0`) i velikog izdanja `WIS v3.0.0` (`ServiceHub v3.3.0`, zadaci `WIS-TASK-188` do `WIS-TASK-226`), sistem je prošao kroz značajna funkcionalna i arhitekturna unapređenja:
1. Ručni i automatski hardverski testovi iz `WIS.HwTest.exe` bili su odvojeni u spoljašnji prozor nakon Faze 6, dok je Faza 5 pokretala zasebnu PowerShell skriptu (`Phase5-Stress.ps1`) sa kratkim 5-minutnim timeout-om i oslanjanjem na `WinRing0`/LHM koje je izazivalo lažna blokiranja Defender/HVCI zaštite.
2. Instalacija Windows Update zakrpa (CU/.NET/Defender) i drajvera zahtevala je dvosmernu sinhronizaciju sa TrueNAS `ServisHub` kešom (bez ručnog preslaganja na NAS deljenju), uz strogu proveru `OSBuild`-a i SHA-256 integriteta.
3. Na pojedinim uređajima sa preskočenim klasičnim OOBE ekranima ili slabijom Pre-OOBE konekcijom nedostajala je rana potvrda Windows Autopilot statusa i zaštita već potvrđenog izveštaja (`autopilot_report.json`) od degradacije na `Unknown`.
4. Servisni pult je zahtevao direktnu štampu identifikacionih i dijagnostičkih nalepnica na Star TSP700II termalnom štampaču direktno iz `WIS.UI.exe`.

## Donete Odluke

1. **Objedinjena Faza 5 i ugradnja `WIS.HwTest` kao 9. taba (`UI/MainWindow.HwTest.cs`)**:
   - `HwTestShell.CreateEmbeddedView()` ugrađuje kompletan hardverski suite direktno u `WIS.UI.exe` kao **9. tab** (`🧪 Hardverski testovi`, indeks `8`, tag `WisHwTestTab`).
   - Tokom Faze 5 (`ExecutePhase5IntegratedHwTest`), `WIS.UI.exe` izvršava objedinjeni orkestracioni tok `StartPhase5FullWorkflow()` kroz podkorake `P5.AutoDiag` -> `P5.Stress` -> `P5.Peripherals`, sa podrazumevanim timeout-om podignutim sa `5` na **`45` minuta**.
   - `Phase5-Stress.ps1` ostaje podržan kao kompatibilni fallback / CI režim kada je definisan `WIS_STRESS_TEST_INPUT_JSON`.
   - `ThermalSensorEngine.cs` (1.426 linija) uklanja ugradnju `WinRing0`/LHM binarca iz `WIS.HwTest.exe`, oslanja se na runtime refleksiju + WMI/ACPI/OEM WMI izvore i koriguje lažni prikaz throttlinga kroz `_baseMaxClockMHz`.

2. **Termalna štampa servisnih nalepnica (`UI/LabelPrintService.cs`)**:
   - Uveden servis štampe za **Star TSP700II** (1.401 linija) sa podrškom za `Transport=Usb|Queue|Network`, `Emulation=StarLine|EscPos`, port `9100`, širinu `576` tačaka i `[Printer]` sekciju u `Wifi-Servis.ini` / `Wifi-Servis.example.ini`.
   - Nalepnica automatski uključuje naziv prodavnice/servisa (`ShopName`), specifikaciju hardvera, status Autopilota i sažetak hardverskih testova (`HW test: ...`).

3. **Dvosmerno Windows Update i Driver keširanje (`WIS.Update.psm1`, `WIS.Hardware.psm1`, `ServiceHub v3.3.0`)**:
   - Podrazumevani WU režim u primeru konfiguracije postavljen je na `[WindowsUpdate] Mode=All`, dok su NAS putanje objedinjene pod `ShareName=Servis`, `ProgramiShare=Programi`, `UpdatesSubfolder=Updates\Win11` i `DriversSubDir=Drivers\Harvested\Models`.
   - `WIS.Update.psm1` (4.177 linija) i `ServiceHub/servishub/api/updates.py` implementiraju proveru `OSBuild`-a, `index.json` SHA-256 verifikaciju i dvosmerne HTTP rute (`/api/updates/status`, `/api/updates/download`, `/api/updates/upload`, `/api/updates/cache-kb`, `/api/updates/prune`) za `.msu`/`.cab` pakete.
   - `WIS.Hardware.psm1` (2.902 linije) i `Phase2-Drivers.ps1` (83 linije) koriste dvosmerni HTTP `<Model>.tar` keš (`/api/drivers/model-pack/status`, `/api/drivers/model-pack/download`, `/api/drivers/model-pack/upload`), batch upit `/api/drivers/resolve`, te prijavljuju novi UI status `HARVESTING` tokom pakovanja i slanja drajvera.

4. **Pre-OOBE Autopilot ZTD hvatanje i zaštita potvrđenog keša**:
   - `PreOOBE-Network.ps1`, `SetupComplete.ps1` (korak `6b`) i `Provera_autopilota.ps1` (974 linije) hvataju ZTD status pre OOBE-a i upisuju potvrđeni `autopilot_report.json`.
   - `CloudZtdInspector.cs` i `Provera_autopilota.ps1` tretiraju prazan `AutopilotDDSZTDFile.json` (validan prazan odgovor ZTD servisa) kao `ConfirmedFree`, dok zaštita keša sprečava da kasniji mrežni prekid pregazi već potvrđen status na `Unknown` (uz automatski retry u `WIS.UI.exe` nakon Faze 1).

5. **Kanonsko pakovanje i RSA-2048 potpisivanje za `v3.0.0`**:
   - Root `version.json` podignut na **`3.0.0`** za `WIS`, `UI`, `Launcher` i `ISO`, odnosno **`3.3.0`** za `ServiceHub` (`os_build_target: 10.0.26200.8037`, `min_launcher_version: 2.2.17`).
   - `wis-payload.zip` (`68.475.013 B`, SHA-256 `ea2ca92892249cda7106f35d85b7b02a4115527eaae57da1d98ccb85c9e44e30`, `78` potpisanih fajlova) obuhvata deterministički kompajlirane binarne celine `WIS.UI.exe` (`2.977.280 B`), `WIS.HwTest.exe` (`1.736.704 B`), `WIS.Launcher.exe` (`468.480 B`) i `WIS-AutopilotAuditor.exe` (`447.488 B`).

## Posledice
- **Pozitivne**: Jedinstven korisnički tok od instalacije do potpisane servisne nalepnice, znatno brža priprema ponovljenih modela računara kroz dvosmerni HTTP/NAS keš i pouzdanija Pre-OOBE Autopilot evidencija.
- **Operativne**: Produžen timeout Faze 5 na 45 minuta zbog kombinovanog stress + interaktivnog hardverskog testiranja; `ServiceHub` zahteva pokrenute nove servise (`v3.3.0`) sa ignorisanim runtime direktorijumima u `ServiceHub/storage/`.
