# Tehnička Specifikacija i PRD/RFC: Redizajn Dijagnostike, Čišćenje Headera, Kumulativni Tajmer, Nova Stres Faza i Potpuno Čišćenje GWADT u WIS (WIS v2.15.0)

- **ID Dokumenta:** WIS-RFC-0131
- **Projekat:** Tehno kutak Windows Installation Assistant (WIS Engine)
- **Status:** Spremno za Implementaciju (Approved PRD / RFC)
- **Ciljna Verzija:** WIS v2.15.0
- **Datum:** 2026-10-08
- **Autor:** Agency Brainstormer
- **Ciljni Tim:** WIS Multi-Agent Sistem (`wis_orchestrator`, `wis_powershell_expert`, `wis_ui_architect`, `wis_windows_admin`, `wis_qa_debugger`, `wis_module_architect`, `wis_docs_sync`)

---

## 1. Rezime i Poslovni Kontekst (Executive Summary)

### 1.1 Problem u Servisnoj Praksi i Tehnički Dug
U svakodnevnom radu servisa računara **Tehno-Kutak**, serviseri moraju pre sklapanja i predaje računara klijentu pouzdano da verifikuju rad hardvera i stabilnost, uz maksimalnu profesionalnost i urednost softvera:
1. **Ponašanje hlađenja pod opterećenjem:** Verifikacija termalne paste, naleganja hladnjaka na CPU/GPU i reakcije ventilatora.
2. **Postojanje i nivo trotlovanja (Thermal / Power Throttling):** Praćenje da li i u kojoj meri procesor obara frekvencije zbog pregrevanja ili limita napajanja.
3. **Ponašanje pojedinačnih jezgara (Core Clocks):** Inspekcija radnih taktova svih jezgara (Performance vs Efficient jezgra kod novijih Intel arhitektura, CCD kod AMD-a).
4. **Tačna evidencija ukupnog vremena servisa:** Tajmer u zaglavlju se do sada resetovao na `00:00:00` pri svakom restartu računara (npr. međurestart između Faze 3 i Faze 4 ili restart drajvera), pa serviser gubi evidenciju o ukupnom vremenu od nule.
5. **Pretrpanost gornje trake:** Bedževi za CPU, RAM i Disk u headeru zagušuju prostor i redundatni su sa karticama na tabu Pregled i Dijagnostika.
6. **Obavezni automatski stres test u deployment toku:** Nakon što se instaliraju svi programi (Faza 4), neophodno je da sistem samostalno pokrene kratak stres test od 30 sekundi i pruži izveštaj o stabilnosti i temperaturi pre nego što se pređe na završno čišćenje.
7. **Zaostali tehnički dug starog naziva (GWADT ➔ WIS):** Iako je u v2.12.0 uveden rebrending na WIS, kroz prelazni režim kompatibilnosti (Transition Alias Layer) u sistemu je ostao veliki broj zaostalih referenci na `GWADT` (stari importi modula `GWADT.*.psm1`, promenljive `GWADT_SOURCE`, log folderi `C:\Windows\Logs\GWADT`, RunOnce `GWADT_AutoStart`, nazivi fajlova `GWADT-AcceleratePE.cmd`, itd.). Za v2.15.0 je neophodno sprovesti kompletno i konačno čišćenje tako da sistem bude 100% čist **WIS**.

---

## 2. Korisničke Uloge i Tokovi (User Personas & Journey Maps)

### 2.1 Primarna Uloga: Serviser / Servisni Tehničar
- **Karakteristike:** Radi pod vremenskim pritiskom, priprema više računara paralelno, potreban mu je jasan vizuelni uvid u hardver jednim pogledom na ekran bez instalacije eksternih programa (AIDA64, HWMonitor, FurMark).

### 2.2 Korisnički Tok kroz Automatizovani Servis (Happy Path):
1. **Pokretanje Instalacije:**
   - Sistem se podiže u FirstLogon, pokreće se `WIS.UI.exe` preko `WIS-AutoStart.cmd`.
   - Tajmer u gornjem desnom uglu meri od `00:00:00` i trajno beleži početni timestamp u `C:\setup\deploy-state.json`.
   - Gornja traka je čista: prikazuje verziju `WIS v2.15.0`, brend `TEHNO-KUTAK`, model i SN, bez pretrpanih CPU/RAM/Disk bedževa.
2. **Izvršavanje Faza 1 do 4:**
   - Faza 1 (Mreža), Faza 2 (Drajveri), Faza 3 (Optimizacija) i kontrolisani međurestart.
   - Nakon restarta računara, `WIS.UI.exe` se ponovo podiže i čita zabeleženi početni timestamp. Tajmer u headeru nastavlja od tačnog kumulativnog vremena (npr. `00:04:12`), umesto da kreće od nule.
   - Faza 4 (Programi) uspešno instalira sve softverske pakete.
3. **Automatska Faza 5 (Stres Test 30 Sekundi & Izveštaj):**
   - TaskRunner automatski prelazi na **Fazu 5: Stres Test**.
   - Sistem samostalno pokreće multi-core opterećenje na sva jezgra u trajanju od tačno 30 sekundi.
   - Na novom višekanalnom grafikonu serviser prati skok temperature.
   - Na isteku 30 sekundi, stres se automatski zaustavlja, oglašava se zvučni signal, i na ekranu se ispisuje sažetak:  
     *„Termalni test uspešan: Max CPU 72.8°C | Max GPU 51.0°C | Trotlovanje: 0% | Sistem stabilan“*.
4. **Faza 6: Završetak i Rezime:**
   - Sistem čisti privremene datoteke, arhivira logove u `C:\Windows\Logs\WIS` i prikazuje finalni ekran sa ukupnim kumulativnim vremenom servisa.

### 2.3 Ručni Dijagnostički Tok na Tabu Dijagnostika:
1. Serviser u bilo kom trenutku otvara tab `🔍 Dijagnostika`.
2. Na vrhu vidi 5-minutni real-time grafikon koji kontinuirano radi i u idle-u.
3. Ako želi duži ili ponovni stres test, klikne `[ ▶ Start Stres ]` u donjem desnom uglu grafa. Test teče kontinuirano dok serviser ne klikne `[ ⏹ Stop Stres ]` (uz automatski `Emergency Abort` na 98°C).
4. Ispod grafa u tabeli jezgara prati Current/Min/Max frekvencije i klikom na `[ ↺ Reset Min/Max ]` po potrebi nulira ekstrema.

---

## 3. Matrica Zahteva (MoSCoW Prioritizacija)

### 3.1 Funkcionalni Zahtevi za Tab Dijagnostika

- **[MUST] FR-01: Višekanalni Vremenski Grafikon (5 Minuta / 300 Sekundi)**
  - Dominantna pozicija na vrhu taba „Dijagnostika“ punom širinom panela.
  - Vremenski prozor: Tačno 300 sekundi (5 minuta) istorije sa osvežavanjem od 1 Hz.
  - Režim rada: Kontinuirano prikupljanje i iscrtavanje (i u idle-u i tokom stresa).
  - Skala Y-ose: 0°C do 100°C (ujedno 0% do 100% za throttling), diskretne linije mreže na 20, 40, 60, 80 i 100.
  - Kanali temperatura:
    1. **CPU:** Paket / maksimalno jezgro (`_thermalEngine.CpuTemperature`).
    2. **GPU:** Jezgro / Hotspot grafičke karte (`_thermalEngine.GpuTemperature`).
    3. **Matična Ploča (MB):** Sistemski senzor / čipset (`_thermalEngine.MotherboardTemperature`).
    4. **Disk / SSD:** Senzor primarnog diska (`_thermalEngine.SsdTemperature`).

- **[MUST] FR-02: Indikator i Linija Trotlovanja (Throttling %)**
  - Zasebna linija na grafikonu u uočljivoj boji (Magenta / Ljubičasta `#E056FD`).
  - Kombinovana metrika: hardverski PROCHOT / Thermal Limit flegovi iz `LibreHardwareMonitorLib` + procentualni pad frekvencije ispod deklarisanog/maksimalnog pod visokim opterećenjem.

- **[MUST] FR-03: Interaktivna Legenda i Trenutne Vrednosti**
  - Gornja ivica grafičkog panela sa kružićima u boji i tačnim numeričkim vrednostima u realnom vremenu:
    * `● CPU (°C)` – Koralno crvena / Narandžasta (`#FF6B6B`)
    * `● GPU (°C)` – Smaragdno zelena (`#2ECC71`)
    * `● MB (°C)` – Plava (`#3498DB`)
    * `● SSD (°C)` – Zlatno žuta (`#F1C40F`)
    * `● Throttling (%)` – Magenta / Ljubičasta (`#E056FD`)

- **[MUST] FR-04: Ručne Kontrole Stres Testa u Donjem Desnom Uglu Grafa**
  - Dugmad `[ ▶ Start Stres ]` i `[ ⏹ Stop Stres ]` u donjem desnom uglu panela grafa.
  - Kontinualni rad do pritiska na Stop, uz hardverski `Emergency Abort` na 98.0°C i zvučne signale.

- **[MUST] FR-05: Tabela Taktova po Jezgrima (Core Clocks)**
  - Smeštena direktno ispod panela grafa.
  - Kolone: Jezgro (`Core #0`, `Core #1`...), Trenutni takt (MHz), Minimalni takt, Maksimalni takt i Dinamički bar.
  - Kumulativno pamćenje ekstrema od pokretanja aplikacije uz dugme `[ ↺ Reset Min/Max ]`.
  - Podrška za skaliranje od 2 do 32+ jezgara sa glatkim skrolom.

- **[MUST] FR-06: Konsolidacija i Reorganizacija Taba Dijagnostika**
  - Uklanjanje starih redundantnih kartica sa dna:
    * Stara Kartica 7 („Termalni status & senzori“)
    * Stara Kartica 8 („Termalni stres test - 30 sekundi“)
  - Organizacija preostalih informativnih kartica u stabilan 2-kolonski raspored:
    * Kolona 1: S.M.A.R.T. SSD Zdravlje | Procesor & RAM | Upravljač Uređaja (PnP drajveri)
    * Kolona 2: Baterija & Napajanje | Grafička Karta & Displej | Matična Ploča, BIOS & TPM

---

### 3.2 Funkcionalni Zahtevi za Header i Kumulativni Tajmer

- **[MUST] FR-08: Kumulativni Tajmer Deploymenta (Global Elapsed Time across Reboots)**
  - Tajmer u gornjem desnom uglu prozora (`DurationString`) meri ukupno proteklo vreme celokupnog procesa od inicijalnog starta (početak servisa / FirstLogon / C:\setup).
  - Tajmer se **ne sme resetovati na 00:00:00** kada se računar restartuje.
  - Mehanizam: Inicijalni timestamp (`initial_start_time`) čuva se u `deploy-state.json` i `HKLM\SOFTWARE\WIS\InitialStartTime`. Pri svakom pokretanju, UI izračunava kumulativno vreme i nastavlja brojanje.

- **[MUST] FR-09: Čišćenje Gornje Trake (Uklanjanje CPU, RAM i Disk Bedževa)**
  - Iz gornjeg zaglavlja (`MainWindow.xaml`, Grid.Row 0) trajno se uklanjaju bedževi za CPU, RAM i NVMe/SSD Disk.
  - Gornja traka ostaje čista i minimalistička: verzija (`WIS v2.15.0`), brend `TEHNO-KUTAK`, model, SN, jezički selektor i kumulativni tajmer rada.

---

### 3.3 Funkcionalni Zahtevi za Novu Fazni Tok (Nova Stres Faza)

- **[MUST] FR-11: Nova Automatizovana Faza 5 – Termalni Stres Test i Izveštaj**
  - U sekvencijalni deployment tok ubacuje se nova faza neposredno nakon Faze 4 (Programi):
    * **Faza 1:** 1. Mreža (Wi-Fi i ruting)
    * **Faza 2:** 2. Update (Drajveri i zakrpe)
    * **Faza 3:** 3. Optimizacija (Debloat i brendiranje)
    * **Faza 4:** 4. Programi (Paketi i aktivacija)
    * **Faza 5 (NOVO):** **5. Stres Test (Termalna stabilnost 30s)**
    * **Faza 6 (POMERENO):** **6. Završetak (Dijagnostika i rezime)**
  - Ponašanje nove Faze 5:
    1. TaskRunner po završetku Faze 4 automatski aktivira Fazu 5.
    2. Automatski se pokreće višenitni stres test na svim jezgrima procesora u trajanju od tačno 30 sekundi.
    3. Registruje se `MaxCpuTemp`, `MaxGpuTemp` i `ThrottlingOccurred`.
    4. Na isteku 30 sekundi stres se gasi, oglašava se zvučni signal, i u statusni dnevnik i rezime se upisuje izveštaj.
    5. Prelazi se na Fazu 6 (Završetak).
    6. Sigurnosni prekid: Na 98°C test se prekida momentalno uz alarm.

- **[MUST] FR-12: Ažuriranje Faza Trackera na Tabu Pregled**
  - Kolekcija `Phases` na tabu 1 (Pregled) se proširuje na **6 stavki** (`UniformGrid Columns="6"`).

---

### 3.4 Funkcionalni Zahtevi za Potpunu Eliminaciju 'GWADT' u 'WIS'

- **[MUST] FR-13: Sveobuhvatno Čišćenje Svih Zaostalih Referenci 'GWADT' u 'WIS'**
  - U celokupnom repozitorijumu i deployment artefaktima eliminišu se svi zaostali tragovi starog imena `GWADT` i konvertuju u zvanični `WIS`:
  
  1. **PowerShell Moduli i Faze:**
     - U svim skriptama faza (`Phase1-Network.ps1` do `Phase6-Finalize.ps1`) uklanjaju se importi starog formata `GWADT.*.psm1` i zamenjuju direktnim uvozom zvaničnih modula:
       * `WIS.Common.psm1`
       * `WIS.Network.psm1`
       * `WIS.Hardware.psm1`
       * `WIS.Branding.psm1`
       * `WIS.Apps.psm1`
       * `WIS.Cleanup.psm1`
       * `WIS.Telemetry.psm1`
       * `WIS.Update.psm1`
     - Uklanjaju se suvišni mali shim fajlovi `GWADT.*.psm1` iz `Skripte/Modules/` kako bi u repozitorijumu postojali isključivo pravi `WIS.*.psm1` moduli.
  
  2. **Boot, Startup i CMD Skripte:**
     - `sources/$OEM$/$1/setup/FirstLogon.cmd` i `FirstLogon.ps1`:
       * Preimenovanje svih log foldera: Isključivo `C:\Windows\Logs\WIS` (trajno brisanje referenci na `C:\Windows\Logs\GWADT`).
       * Preimenovanje desktop prečica: Kreira se isključivo `WIS Launcher.lnk` (ukloniti kreiranje `GWADT Launcher.lnk`).
       * Preimenovanje skripti u Startup folderu: `WIS-DesktopStart.cmd` i `WIS-AutoStart.cmd` (umesto `GWADT-DesktopStart.cmd` i `GWADT-AutoStart.cmd`).
       * Registry RunOnce: `WIS_AutoStart` (umesto `GWADT_AutoStart`).
     - Preimenovanje `Skripte/GWADT-AcceleratePE.cmd` u `Skripte/WIS-AcceleratePE.cmd` (uz osvežavanje poziva u `startnet.cmd`).
     - U `startnet.cmd`: Promenljiva `WIS_SOURCE` i tag fajl `X:\WIS_PreBoot_Done.tag` (umesto `GWADT_SOURCE` i `GWADT_PreBoot_Done.tag`).
     - `SetupComplete.cmd`: Kompletno čišćenje svih zaostalih komentara i promenljivih na WIS.
  
  3. **WPF UI i C# Source Code:**
     - Sva zaglavlja prozora (`WindowTitle`), tekstovi u `App.xaml`, `MainWindow.xaml` i `LocalizationService.cs` glase isključivo **WIS**.
     - U `Models.cs` (linija 2198): Log tag postaje `[WIS]` umesto `[GWADT]`.
     - Sistemski Mutex: `Global\WIS_UI_SingleInstance_Mutex` (umesto `Global\GWADT_UI_SingleInstance_Mutex`).
     - Registarski ključevi: Isključivo `HKLM\SOFTWARE\WIS` i `HKCU\Software\WIS` (trajno ukidanje `SOFTWARE\GWADT`).
     - Konsolidacija koda: Primarni projekti su `Skripte/WIS.UI` i `Skripte/WIS.Launcher`.
  
  4. **Pomoćni Analitički i QA Alati:**
     - Preimenovanje `Skripte/Analyze-GWADTLog.py` u `Skripte/Analyze-WISLog.py`.
     - Preimenovanje `Skripte/Analyze-GWADTLog.ps1` u `Skripte/Analyze-WISLog.ps1`.
     - U svim Python i PowerShell skriptama uskladiti parsiranje novog formata logova `wis.log` i `[WIS]`.

---

## 4. Arhitektura Sistema i Tehnički Dizajn

### 4.1 Šematski Prikaz Novog Izgleda Prozora (WIS v2.15.0)

```
┌──────────────────────────────────────────────────────────────────────────────────┐
│ [WIS v2.15.0 - Tehno kutak Windows Installation Assistant]                       │
├──────────────────────────────────────────────────────────────────────────────────┤
│ HEADER TRAKA (ČISTA I RASTEREĆENA):                                              │
│ [WIS v2.15.0] TEHNO-KUTAK                                                        │
│ Model: Lenovo ThinkPad T14 Gen 3                                                 │
│ SN: PF-2X99AB                    [ 🇷🇸 SRB | 🇨🇳 中文 ]   ⏱ TRAJANJE: 00:08:45     │
├──────────────────────────────────────────────────────────────────────────────────┤
│ TRACKER FAZA (6 FAZA):                                                           │
│ ┌──────────┐ ┌──────────┐ ┌──────────┐ ┌──────────┐ ┌──────────┐ ┌──────────┐    │
│ │ 1. Mreža │ │2. Update │ │3. Optimiz│ │4.Programi│ │ 5. Stres │ │6.Završetak│   │
│ │   [✓]    │ │   [✓]    │ │   [✓]    │ │   [✓]    │ │   [▶]    │ │   [ ]    │    │
│ └──────────┘ └──────────┘ └──────────┘ └──────────┘ └──────────┘ └──────────┘    │
├──────────────────────────────────────────────────────────────────────────────────┤
│ TAB 4: 🔍 DIJAGNOSTIKA                                                           │
│ ┌──────────────────────────────────────────────────────────────────────────────┐ │
│ │ [GRAFIKON TEMPERATURA I TROTLOVANJA KROZ VREME (5 MINUTA)]                   │ │
│ │ Legenda: ● CPU: 65°C  ● GPU: 52°C  ● MB: 38°C  ● SSD: 42°C  ● Throttle: 0%  │ │
│ │ 100°C ┌────────────────────────────────────────────────────────────────────┐ │ │
│ │  80°C │                 /\                                                 │ │ │
│ │  60°C │    /\__________/  \___________ (CPU Linija)                        │ │ │
│ │  40°C │   /                            (GPU Linija)                        │ │ │
│ │  20°C │  /                             (SSD / MB Linija)                   │ │ │
│ │   0°C └────────────────────────────────────────────────────────────────────┘ │ │
│ │   -5m         -4m         -3m         -2m         -1m          0s (Sada)   │ │
│ │                                               [ ▶ Start Stres ] [ ⏹ Stop ]  │ │
│ └──────────────────────────────────────────────────────────────────────────────┘ │
│ ┌──────────────────────────────────────────────────────────────────────────────┐ │
│ │ [TAKTOVI PO JEZGRIMA (CORE CLOCKS)]                   [ ↺ Reset Min/Max ]    │ │
│ │ ┌─────────┬──────────────┬──────────────┬──────────────┬───────────────────┐ │ │
│ │ │ Jezgro  │ Trenutni     │ Minimalni    │ Maksimalni   │ Dinamički Bar     │ │ │
│ │ ├─────────┼──────────────┼──────────────┼──────────────┼───────────────────┤ │ │
│ │ │ Core #0 │ 4,300 MHz    │ 800 MHz      │ 4,600 MHz    │ [███████████░░]   │ │ │
│ │ │ Core #1 │ 4,280 MHz    │ 800 MHz      │ 4,600 MHz    │ [██████████░░░]   │ │ │
│ │ └─────────┴──────────────┴──────────────┴──────────────┴───────────────────┘ │ │
│ └──────────────────────────────────────────────────────────────────────────────┘ │
│ ┌──────────────────────────────────────┐  ┌──────────────────────────────────┐ │
│ │ S.M.A.R.T. SSD ZDRAVLJE              │  │ BATERIJA & NAPAJANJE             │ │
│ ├──────────────────────────────────────┤  ├──────────────────────────────────┤ │
│ │ PROCESOR & RADNA MEMORIJA            │  │ GRAFIČKA KARTA & DISPLEJ         │ │
│ ├──────────────────────────────────────┤  ├──────────────────────────────────┤ │
│ │ UPRAVLJAČ UREĐAJA & DRAJVERI         │  │ MATIČNA PLOČA, BIOS & TPM        │ │
│ └──────────────────────────────────────┘  └──────────────────────────────────┘ │
└──────────────────────────────────────────────────────────────────────────────────┘
```

---

## 5. Kriterijumi Gotovosti (Definition of Done)

- [ ] **Grafikon & Dijagnostika:** 5-minutni grafikon sa 4 temperature i linijom trotlovanja iscrtava podatke kontinuirano u realnom vremenu (1 Hz).
- [ ] **Tabela Jezgara:** Tabela prikazuje Current/Min/Max frekvencije i dinamički bar za sva jezgra, uz funkcionalno `[ ↺ Reset Min/Max ]` dugme.
- [ ] **Čišćenje Taba:** Stare kartice 7 i 8 uklonjene sa dna taba Dijagnostika; ostale složene u pregledan 2-kolonski raspored.
- [ ] **Kumulativni Tajmer:** Vreme u headeru meri ukupno vreme od prvog starta i ne resetuje se na nulu nakon restarta računara.
- [ ] **Čist Header:** CPU, RAM i Disk bedževi uklonjeni iz gornje trake; prikaz je rasterećen i elegantan.
- [ ] **Faza 5 Stres:** TaskRunner nakon Faze 4 automatski pokreće Fazu 5 (30s stres test), daje izveštaj o stabilnosti i prelazi na Fazu 6 (Završetak).
- [ ] **Faza Tracker:** Tracker na Tabu Pregled ažuriran na 6 faza sa punom podrškom za srpski i kineski jezik.
- [ ] **100% Čišćenje GWADT:** Svi zaostali fajlovi, importi, registarski ključevi, log putanje, mutexi i tagovi sa nazivom `GWADT` očišćeni i preimenovani u `WIS`.
- [ ] **Kompilacija & Paritet:** Uspešan Docker Wine MSBuild sa 0 grešaka i 0 upozorenja, uz 100% SHA-256 binarni paritet na svih 19 lokacija.
- [ ] **QA Test Skripta:** Automatizovana Python QA test skripta verifikuje sve komponente, sintaksu, eliminaciju GWADT stringova i paritet.

---

## 6. Smernice za Multi-Agent Implementaciju (Handover Directives)

### 🏛️ Za Orkestratora (`wis_orchestrator`)
- Formirati zadatak **WIS-TASK-131** u `CURRENT_TASK.md` sa dekomponovanim fazama:
  * **Faza 1 (`wis_powershell_expert`):** Čišćenje svih PowerShell skripti faza, modula i CMD/boot fajlova sa `GWADT` na `WIS`, uklanjanje shima i uvođenje `Phase5-Stress.ps1` i `Phase6-Finalize.ps1`.
  * **Faza 2 (`wis_ui_architect`):** C# proširenje senzora (`MotherboardTemperature`, `CoreClocks`, `ThrottlingPercentage`), kumulativni tajmer `DurationString`, čišćenje CPU/RAM/Disk bedževa iz headera, rebrendiranje `[WIS]` i mutexa.
  * **Faza 3 (`wis_ui_architect`):** XAML izrada novog 5-minutnog grafa, tabele taktova jezgara, čišćenje starih kartica i tracker na 6 faza.
  * **Faza 4 (`wis_module_architect`):** Docker Wine MSBuild kompilacija `WIS.UI.exe` i `WIS.Launcher.exe` i distribucija na svih 19 lokacija.
  * **Faza 5 (`wis_qa_debugger`):** Sveobuhvatna automatizovana QA verifikacija (pokrivenost novih funkcija, 0 zaostalih GWADT stringova u aktivnom kodu, 100% binarni paritet).
  * **Faza 6 (`wis_docs_sync`):** Izrada `ADR-0131` i ažuriranje `istorija_izrade.md`.
