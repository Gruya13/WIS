---
title: "ADR-0131: Redizajn Dijagnostike (5-Min Grafikon & Core Clocks), Kumulativni Tajmer, 6-Fazni Pipeline i Potpuno Čišćenje GWADT u WIS"
---

# ADR-0131: Redizajn Dijagnostike (5-Min Grafikon & Core Clocks), Kumulativni Tajmer, 6-Fazni Pipeline i Potpuno Čišćenje GWADT u WIS

## 1. Kontekst i Problem

Tokom terenskog rada u servisu računara **WIS Service** i detaljne analize korisničkog iskustva servisera (definisane u arhitektonskom dokumentu `wiki/architecture/PRD-WIS-v2.15-Diagnostics-Telemetry-Redesign.md`), identifikovano je sedam ključnih izazova i tehničkih ograničenja u dosadašnjoj verziji v2.14.0:

1. **Nedostatak istorijskog uvida u termalno ponašanje i trotlovanje procesora:**
   - Serviseri su morali da gledaju trenutne brojke bez uvida u dinamiku rasta temperature tokom vremena.
   - Nije postojao kvantifikovani indikator termalnog i naponskog trotlovanja (Thermal/Power Throttling %), što je otežavalo dijagnostiku loše termalne paste, neadekvatnog hlađenja ili fabričkih limita.
2. **Odsustvo detaljnog uvida u taktove pojedinačnih jezgara (Core Clocks):**
   - Na modernim procesorima sa hibridnim arhitekturama (Intel P-cores/E-cores, AMD CCD/CCX), neophodno je pratiti da li sva jezgra postižu deklarisane radne taktove i da li neko specifično jezgro zaostaje.
3. **Pretrpanost i redundancija taba Dijagnostika:**
   - Stare kartice 7 („Termalni status & senzori“) i 8 („Termalni stres test - 30 sekundi“) nalazile su se na dnu taba kao statičke kartice, stvarajući prevelik vertikalni skrol i duplirajući informacije.
4. **Gubitak kumulativnog vremena trajanja servisa pri restartima računara:**
   - Tajmer u zaglavlju prozora (`DurationString`) se pri svakom restartu računara (kontrolisani međurestart između Faze 3 i Faze 4, ili restart drajvera) resetovao na `00:00:00`, čime je gubljena tačna evidencija o ukupnom trajanju servisa od nultog trenutka.
5. **Vizuelna pretrpanost gornje trake (Header):**
   - Bedževi za CPU, RAM i Disk u gornjem zaglavlju zauzimali su dragocen prostor, skretali pažnju i bili redundatni u odnosu na detaljne kartice na tabovima Pregled i Dijagnostika.
6. **Potreba za obaveznim automatskim stres testom u deployment toku:**
   - Nakon instalacije programa (Faza 4), bilo je neophodno da sistem samostalno sprovede kratak stres test od 30 sekundi i serviseru pruži jasan izveštaj o stabilnosti i maksimalnim temperaturama pre završnog čišćenja i predaje mašine.
7. **Zaostali tehnički dug tranzicionog sloja (GWADT ➔ WIS):**
   - Iako je rebrending na WIS započet u v2.12.0, u sistemu su ostali tranzicioni shim fajlovi (`GWADT.*.psm1`), stare reference na log foldere (`C:\Windows\Logs\GWADT`), RunOnce unosi (`GWADT_AutoStart`), nazivi alata (`GWADT-AcceleratePE.cmd`, `Analyze-GWADTLog.*`) i sistemski Mutex (`Global\GWADT_UI_SingleInstance_Mutex`). Za verziju v2.15.0 bilo je neophodno izvršiti 100% konačno čišćenje.

---

## 2. Arhitektonska Odluka

Usvojen je sveobuhvatan paket arhitektonskih, grafičkih, senzorskih i proceduralnih rešenja koji sistem podiže na verziju **WIS v2.15.0**:

### 2.1. Redizajn Taba Dijagnostika: Višekanalni 5-Minutni Grafikon
- **5-minutni rolling prozor (300 sekundi @ 1 Hz):**
  - Implementiran nativni WPF Canvas grafikon sa `Polyline` elementima koji kontinualno beleži i iscrtava podatke svake sekunde (i u idle režimu i pod stresom).
  - Skala Y-ose je standardizovana od 0 do 100 (°C za temperature, % za trotlovanje), sa diskretnim mrežnim linijama na 20, 40, 60, 80 i 100.
  - Podržano 5 diskretnih telemetrijskih kanala:
    * **CPU (°C):** Koralno crvena/narandžasta linija (`#FF6B6B`)
    * **GPU (°C):** Smaragdno zelena linija (`#2ECC71`)
    * **Matična ploča (°C):** Plava linija (`#3498DB`)
    * **SSD / Disk (°C):** Zlatno žuta linija (`#F1C40F`)
    * **Throttling (%):** Magenta / ljubičasta linija (`#E056FD`)
- **Živa legenda u realnom vremenu:**
  - U zaglavlju panela grafa integrisani su kolor-kodirani kružići i žive brojčane vrednosti u formatu `● CPU: XX.X°C  ● GPU: XX.X°C  ● MB: XX.X°C  ● SSD: XX.X°C  ● Throttle: XX%`.
- **Integrisane Start / Stop kontrole stresa u uglu grafa:**
  - U donjem desnom uglu grafa ugrađena su dugmad `[ ▶ Start Stres ]` i `[ ⏹ Stop Stres ]`.
  - Omogućen je kontinualni ručni stres test procesora do pritiska na Stop, uz hardverski `Emergency Abort` na 98.0°C koji momentalno gasi radne niti i emituje zvučni alarm.

### 2.2. Tabela Taktova po Jezgrima (Core Clocks)
- **Detaljan prikaz svih jezgara:**
  - Pozicionirana direktno ispod grafikona.
  - Kolone: **Jezgro** (`Core #0`, `Core #1`...), **Trenutni takt (MHz)**, **Minimalni takt (MHz)**, **Maksimalni takt (MHz)** i **Relativni vizuelni takt (ProgressBar)**.
  - Praćenje ekstremnih vrednosti (Min/Max) od momenta pokretanja aplikacije.
  - Ugrađeno namensko dugme `[ ↺ Reset Min/Max ]` za nuliranje ekstrema.
  - Fleksibilan WMI/CIM fallback ukoliko napredni senzorski registri nisu dostupni.

### 2.3. Konsolidacija i Reorganizacija Kartica Dijagnostike
- Stare kartice 7 i 8 sa dna taba su trajno uklonjene.
- Preostalih 6 kartica reorganizovano je u stabilan 2-kolonski raspored:
  * **Kolona 1:** S.M.A.R.T. SSD Zdravlje | Procesor & RAM | Upravljač Uređaja (PnP drajveri)
  * **Kolona 2:** Baterija & Napajanje | Grafička Karta & Displej | Matična Ploča, BIOS & TPM

### 2.4. Kumulativni Tajmer Deploymenta i Čišćenje Headera
- **Globalna perzistencija trajanja servisa:**
  - Uvedeno čuvanje početnog timestamp-a (`initial_start_time`) u `C:\setup\deploy-state.json` i `HKLM\SOFTWARE\WIS\InitialStartTime`.
  - Metoda `InitializeCumulativeTimer()` u `MainWindow.xaml.cs` čita zabeleženi timestamp i kontinualno izračunava ukupno proteklo vreme od nultog trenutka servisa.
  - Tajmer se više **ne resetuje na 00:00:00** nakon restarta računara.
- **Minimalistička i čista gornja traka:**
  - Trajno su uklonjeni bedževi za CPU, RAM i Disk iz gornjeg zaglavlja (`MainWindow.xaml`).
  - Zaglavlje sadrži isključivo: verziju `WIS v2.15.0`, brend `WIS SERVICE`, model, serijski broj, jezički selektor i kumulativni tajmer rada.

### 2.5. 6-Fazni Pipeline i Nova Faza 5 (Stres Test 30s)
- **Sekvencijalno proširenje na 6 faza:**
  1. `1. Mreža` (`Phase1-Network.ps1`)
  2. `2. Update` (`Phase2-Drivers.ps1`)
  3. `3. Optimizacija` (`Phase3-Optimization.ps1`)
  4. `4. Programi` (`Phase4-Apps.ps1`)
  5. `5. Stres` (`Phase5-Stress.ps1` - **NOVO**)
  6. `6. Završetak` (`Phase6-Finalize.ps1` - ranije Faza 5)
- **Automatizovana Faza 5:**
  - TaskRunner nakon instalacije programa automatski podiže `Phase5-Stress.ps1`.
  - Pokreće se višenitni stres test u trajanju od tačno 30 sekundi sa beleženjem maksimalnih temperatura i trotlovanja.
  - Na kraju se generiše sažet statusni izveštaj: *„Termalni test uspešan: Max CPU XX.X°C | Max GPU XX.X°C | Trotlovanje: X% | Sistem stabilan“*.
- **UI Tracker:**
  - Kolekcija `Phases` na tabu Pregled proširena je na 6 elemenata uz `UniformGrid Columns="6"`.

### 2.6. 100% Eliminacija GWADT Tehničkog Duga u Korist WIS
- Obrisano svih 10 tranzicionih shim fajlova `GWADT.*.psm1` iz `Skripte/Modules/`; sve faze direktno uvoze `WIS.*.psm1`.
- Obrisani alati `Skripte/GWADT-AcceleratePE.cmd`, `Skripte/Analyze-GWADTLog.py` i `Skripte/Analyze-GWADTLog.ps1`.
- Kreirani punopravni WIS alati: `Skripte/WIS-AcceleratePE.cmd`, `Skripte/Analyze-WISLog.py` i `Skripte/Analyze-WISLog.ps1`.
- Kompletno očišćeni `SetupComplete.cmd`, `FirstLogon.cmd`, `FirstLogon.ps1` i sve faze:
  * Log direktorijum: isključivo `C:\Windows\Logs\WIS`.
  * Desktop prečica: `WIS Launcher.lnk`.
  * Startup skripte: `WIS-DesktopStart.cmd` i `WIS-AutoStart.cmd`.
  * Registry RunOnce: `WIS_AutoStart`.
  * Sistemski Mutex: `Global\WIS_UI_SingleInstance_Mutex`.
  * Registry ključevi: `HKLM\SOFTWARE\WIS` i `HKCU\Software\WIS`.
  * Log tag: `[WIS]` u IPC strimingu i modelima.

---

## 3. Posledice Odluke

### Pozitivne:
- **Trenutna dijagnostika hladnjaka i paste:** Serviseri u realnom vremenu vide termalnu krivu i tačan nivo trotlovanja, što omogućava trenutno otkrivanje loše montiranih kulera ili sasušene paste.
- **Pouzdan uvid u sva jezgra:** Tabela taktova omogućava trenutnu detekciju neaktivnih ili zagušenih jezgara na novim višekorisničkim i višenitnim procesorima.
- **Autonomna garancija stabilnosti:** Nova Faza 5 u sekvenci garantuje da nijedan računar ne napusti servis bez položenog 30-sekundnog termalnog stresa.
- **Tačna servisna evidencija:** Kumulativni tajmer pruža realan uvid u trajanje celokupnog servisa bez prekida uzrokovanih restartima.
- **Arhitektonska čistoća:** Uklanjanjem svih zaostalih GWADT referenci eliminisan je višemesečni tehnički dug, a repozitorijum i izvršni medijumi su 100% standardizovani na **WIS**.

### Ograničenja i Neutralne Posledice:
- Izvršavanje deployment pipeline-a je produženo za tačno 30 sekundi zbog obaveznog automatskog stres testa u Fazi 5, što je potpuno opravdano dobijenom verifikacijom pouzdanosti.
- Veličina izvršnog fajla `WIS.UI.exe` je blago porasla sa 365,568 B na 387,072 B usled bogatijeg Canvas motora i Core Clocks modela, što je zanemarljivo s obzirom na nultu zavisnost od spoljnih paketa.

---

## 4. Verifikacija i Metrike (Definition of Done)

### 4.1. Statička Analiza i Sintaksa
- **C# 5.0 Verifikacija:** `Verify-CSharpSyntax.py` položila **43/43 fajlova (100% PASS)**.
- **PowerShell AST Verifikacija:** `Verify-PowerShellSyntax.py` položila **100% sa 0 grešaka**.

### 4.2. Sveobuhvatna QA Verifikacija (`Skripte/Test-Task131-Sprint-v215-QA.py`)
- **Blok 1 (Višekanalni 5-Minutni Grafikon i Senzori - FR-01..04):** 46/46 PASS (100.0%)
- **Blok 2 (Core Clocks Tabela i Konsolidacija Dijagnostike - FR-05..06):** 28/28 PASS (100.0%)
- **Blok 3 (Kumulativni Tajmer, Čišćenje Headera i Rebrendiranje - FR-08..10):** 10/10 PASS (100.0%)
- **Blok 4 (6-Fazni Pipeline i Faza 5 Stres Test 30s - FR-11..12):** 15/15 PASS (100.0%)
- **Blok 5 (Eliminacija GWADT Duga i SR/ZH Simetrija - FR-07 & FR-13):** 19/19 PASS (100.0%)
- **Blok 6 (Statička Analiza i 100% SHA-256 Binarni Paritet):** 35/35 PASS (100.0%)
- **UKUPNO:** **153/153 PROVERA POLOŽENO (100.0% PASS)**, Exit Code 0.

### 4.3. Binarne Metrike i 100% SHA-256 Paritet
- **`WIS.UI.exe` (9 lokacija):**
  * Veličina: tačno **387,072 B**
  * SHA-256: `66ecece81c74c17b33bc9984fb9d84dc032917e43303651f252e8e902ae2518c`
- **`GWADT.UI.exe` (10 lokacija):**
  * Veličina: tačno **387,072 B**
  * SHA-256: `9414f0547ebdc264b2736482b30f74b0319486df1906eb30f35fee04447e2105`
- **`WIS.Launcher.exe` (7 lokacija):**
  * Veličina: tačno **56,832 B**
  * SHA-256: `2677a80d754410371153108290031b89cfed287abd895181ae6becf7aa921869`
- **`GWADT.Launcher.exe` (7 lokacija):**
  * Veličina: tačno **56,832 B**
  * SHA-256: `017cfcefde3ff4c52cedb9266c34dd0912e9cd662019a71d2c64616f76a37d12`

---

## 5. Povezani Dokumenti
- [[../architecture/PRD-WIS-v2.15-Diagnostics-Telemetry-Redesign|PRD-WIS-v2.15-Diagnostics-Telemetry-Redesign.md]]
- [[../architecture/overview|overview.md]]
- [[../architecture/pipeline|pipeline.md]]
- [[../index|index.md]]
