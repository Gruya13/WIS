# ADR-0189: Ručni i Automatski Hardverski Test Suite (`WIS.HwTest`) i ServiceHub Profili Modela

## Status
Prihvaćeno i implementirano (2026-11-12; dopunjeno do 2026-11-14 u `WIS v3.0.0`)

## Kontekst
U procesu instalacije i pripreme računara u servisu Tehno kutak, automatizovana instalacija drajvera i softvera uspešno dovodi operativni sistem do stanja spremnosti. Međutim, pored dubinske automatske provere komponenti (baterija, mreža, TPM/SecureBoot/BitLocker bezbednost, SMART/disk, RAM, CPU, GPU), fizička ispravnost korisničkih periferija (mrtvi pikseli na matrici ekrana, zaglavljeni ili neodzivni tasteri na tastaturi, TrackPoint, pozadinsko osvetljenje, levi/desni klik i skrol na touchpadu, digitajzer i Stylus na touchscreen-u, stereo zvučnici, mikrofon, kamera sa IR senzorom, fizički USB portovi uključujući USB-to-LAN adapter i čitač otiska prsta) ne može se pouzdano utvrditi bez standardizovanog test suite-a.

Prethodno stanje:
1. Nije postojao standardizovan postupak automatske + ručne provere hardvera; serviseri su improvizovali provere ili preskakali testiranje pojedinih portova/tastera.
2. Nije bilo evidencije o oborenim (`FAIL`) ili preskočenim (`SKIP`) komponentama u centralnom izveštaju servisa i na servisnoj etiketi.
3. Različiti modeli laptopova i desktop računara poseduju specifične tastature (12 različitih OEM rasporeda — ISO/ANSI, sa ili bez numpada, ThinkPad Fn/Ctrl zamene, različiti F-tasteri), što je otežavalo uniformnu validaciju.

## Donete Odluke

1. **Modularni suite `WIS.HwTest` (standalone + ugrađeni mod u `WIS.UI.exe`)**:
   - Razvijen u `UI/HwTest/` (21 C# fajl, 21.284 linija) koristeći C# 5.0, .NET Framework 4.8 i WPF sa čistim code-behind prozorima (bez BAML zavisnosti od glavnog GUI-ja).
   - Kompajlira se deterministički na Linux sistemu preko Mono Roslyn kompajlera (`UI/HwTest/Build-HwTest.py` / `csc.exe`), a ujedno se ugrađuje direktno u `WIS.UI.exe` preko `UI/Build-UI.py`.
   - Uklonjena je ranija ugradnja `WinRing0` / `LibreHardwareMonitor` biblioteke iz `WIS.HwTest.exe` kako Windows Defender i HVCI (Memory Integrity) ne bi blokirali pokretanje; očitavanje telemetrije oslanja se na WMI/ACPI/OEM interfejse i `ThermalSensorEngine.cs`.
   - Može se pokrenuti samostalno (`WIS.HwTest.exe --standalone`) na prijemu računara ili integrisano u okviru `WIS.UI.exe` tokom Faze 5 i na 9. tabu `🧪 Hardverski testovi` (`MainWindow.HwTest.cs`).

2. **Skup od 7 automatskih i 9 interaktivnih testova**:
   - **7 automatskih dijagnostika (`AutoHardwareDiagnostics.cs`)**: `auto_battery`, `auto_network`, `auto_security`, `auto_disk`, `auto_ram`, `auto_cpu` i `auto_gpu`, sa pregledom detalja kroz `AutoTestDetailsWindow.cs`.
   - **Ekran (`display`)**: Prikaz 6 punih boja preko celog ekrana (crna, bela, crvena, zelena, plava, 50% siva) sa beleženjem koordinata prijavljenih mrtvih piksela i eksplicitnim `FAIL` dugmetom.
   - **Tastatura (`keyboard`)**: Faza mirovanja (5 sekundi) za otkrivanje zaglavljenih kontakata, vizuelna mapa tastature sa **12 OEM šablona rasporeda** (`Assets.keyboard_layouts.json`), low-level kuka `WH_KEYBOARD_LL` (guta sistemske prečice dok traje test), dualno mapiranje F-tastera, podrška za `TrackPoint` preko `WH_MOUSE_LL`, kontrola pozadinskog osvetljenja preko `\\.\IBMPmDrv`, `\\.\EnergyDrv` i Lenovo/Dell WMI metoda, te dugme za uklanjanje nepostojećih tastera za dati model.
   - **Touchpad (`touchpad`)**: Detekcija ulaza pokazivača u 4 ugla ekrana, test levog i desnog klika i skrola sa dva prsta (gore/dole).
   - **Touchscreen i Stylus (`touchscreen`)**: Trostepeni (`3-korak`) tok sa podrškom za **Stylus (olovku)**, DFS generator lavirinta širine hodnika 12 mm skalirane prema sistemskom DPI-ju (`MazeGenerator.cs`), checkpoint sistem i multitouch provera držanjem dva prsta 2 sekunde.
   - **Zvuk L/D (`audio`)**: Postavljanje jačine preko CoreAudio `IAudioEndpointVolume`, reprodukcija nasumičnih govornih cifara (`Assets.digits_en.0..9.wav`) panned 100% na levi i 100% na desni kanal, verifikacija preko on-screen tastature.
   - **Mikrofon (`mic`)**: `waveIn` merenje signala sa dinamičkim VU metrom; automatski prolaz na signal > -30 dBFS uz 3 sekunde snimanja i automatsko preslušavanje.
   - **Kamera i IR (`camera`)**: Media Foundation video stream (RGB32) sa real-time `WriteableBitmap` prikazom i `ZXing.Net` (`UI/zxing.dll` / `Assets.zxing.dll`) očitavanjem verifikacionog QR koda `WIS-CAMTEST-v1`, uz **automatsko otkrivanje IR senzora** za Windows Hello Face.
   - **USB portovi (`usb`)**: Praćenje `WM_DEVICECHANGE` poruka i `LocationInformation` putanja za sukcesivno priključivanje u portove, sa režimom **single-device USB replug** i verifikacijom **USB-to-LAN adaptera**.
   - **Čitač otiska prsta (`fingerprint`)**: Biometrijska provera preko Windows Biometric Framework-a (`WINBIO_POOL_PRIVATE`) sa **4.5s watchdog** osiguračem radi sprečavanja blokade na nestandardnim senzorima.
   - Svi interaktivni testovi poseduju jasno razdvojena `PASS`, `FAIL` i `SKIP` stanja sa obaveznim evidencionim tragom.

3. **Centralizovani ServiceHub servisi za profile modela, rasporede i specifikacije**:
   - Uvedeni REST API endpointi (`ServiceHub/servishub/model_profiles.py`, `keyboard_resolver.py`, `hardware_spec.py`):
     - `GET /api/model-profile?model=<model>`
     - `POST /api/model-profile/keyboard`
     - `POST /api/model-profile/usb`
     - `GET /api/keyboard-layout`
     - `GET /api/hardware-spec`
   - ServisHub perzistira specifičnosti u `storage/model_profiles/<normalized_model>.json` (ignorisan u Git-u), kešira specifikacije u `storage/hw_spec_cache/` i čuva lokalni red izmena `C:\setup\hwtest-pending.json` kada hub nije dostupan.

4. **Integracija u `WIS.UI.exe` Fazu 5 i štampu servisnih nalepnica (`v3.0.0`)**:
   - Ranije pokretanje eksternog `WIS.HwTest.exe` nakon Faze 6 zamenjeno je u `v3.0.0` potpunom ugradnjom u `WIS.UI.exe` (`MainWindow.HwTest.cs`):
     - Faza 5 (`ExecutePhase5IntegratedHwTest`) izvršava jedinstveni objedinjeni tok `StartPhase5FullWorkflow` (`P5.AutoDiag` -> `P5.Stress` -> `P5.Peripherals`) sa podrazumevanim timeout-om od **45 minuta**.
     - Pored toga, `WIS.HwTest` ostaje trajno dostupan na **9. tabu** glavnog interfejsa (`🧪 Hardverski testovi`).
   - Rezultat se čuva u `C:\setup\hwtest-result.json`, učitava u `ViewModel` i automatski štampa na servisnoj etiketi (Star TSP700II via `UI/LabelPrintService.cs`).

## Posledice
- **Pozitivne**:
  - Potpuno standardizovana automatska i ručna verifikacija hardvera objedinjena u jedan tok (Faza 5 + 9. tab) bez žrtvovanja `--standalone` prijema uređaja.
  - Svi `PASS`/`FAIL`/`SKIP` nalazi postaju trajni deo ServiceHub izveštaja i štampane servisne nalepnice.
  - Uklanjanjem ugrađenog `WinRing0` drajvera eliminisana su lažna blokiranja od strane Defender/HVCI zaštite u servisu.
- **Kompatibilnost**:
  - 100% C# 5.0 / .NET Framework 4.8 kompatibilnost; deterministička kompilacija na Linuxu preko Mono Roslyn `csc.exe` (`UI/Build-UI.py` i `UI/HwTest/Build-HwTest.py`).
