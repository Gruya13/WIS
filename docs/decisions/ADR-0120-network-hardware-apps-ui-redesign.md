---
title: "ADR-0120: FirstLogon Wi-Fi Bootstrap, Preskakanje Faze 1, Normalizacija OEM Modela, Hibridna Win32 Detekcija Hardvera, Stabilizacija Stres Testa i Redizajn Taba Programi"
type: decision
status: accepted
date: 2026-10-02
author: gwadt_docs_sync / agency-ops-administrator / gwadt_powershell_expert / gwadt_ui_architect
tags:
  - decision
  - adr
  - wifi-bootstrap
  - firstlogon
  - phase1-bypass
  - model-normalization
  - hybrid-hardware-detection
  - win32-registry
  - kernel32-memory
  - stress-test-stabilization
  - ui-redesign
  - uniformgrid
  - v2.10.0
---

# ADR-0120: FirstLogon Wi-Fi Bootstrap, Preskakanje Faze 1, Normalizacija OEM Modela, Hibridna Win32 Detekcija Hardvera, Stabilizacija Stres Testa i Redizajn Taba Programi

## 1. Kontekst i Problem

U toku produkcione eksploatacije sistema GWADT (v2.9.1) u servisu računara, uočeno je nekoliko uskih grla i ergonomskih nedostataka koji su usporavali proces automatizovane instalacije i narušavali korisničko iskustvo servisera:

1. **Kasno uspostavljanje bežične mrežne veze (Wi-Fi desinhronizacija):**
   U ranijoj arhitekturi, povezivanje na servisni Wi-Fi vršeno je tek u okviru Faze 1 (`Phase1-Network.ps1`), nakon što se `GWADT.Launcher.exe` već pokrenuo. Pošto se pri prvom logovanju Windows WLAN AutoConfig servis (`wlansvc`) često pokreće sporo i asinhrono, Launcher na bežičnim laptopovima bez Ethernet kabla nije mogao da kontaktira ServisHub niti proveri najnoviji OTA payload, što je dovodilo do pokretanja lokalnog offline paketa i naknadnog ponovnog pokretanja mrežnog modula.

2. **Suvišno ponavljanje Faze 1 u TaskRunner-u:**
   Kada Launcher uspešno preuzme ili verifikuje najnoviji paket sa ServisHub-a, postojanje mrežne i internet konekcije je već praktično dokazano. Uprkos tome, C# WPF interfejs (`GWADT.UI.exe`) je u svom sekvencijalnom TaskRunner-u bezuslovno ponovo pokretao Fazu 1 (trošeći nepotrebnih 5 do 20 sekundi na re-evaluaciju adaptera i gateway-a).

3. **Nečitka i pogrešna identifikacija modela računara:**
   Standardni WMI upiti nad `Win32_ComputerSystem.Model` na brendiranim prenosnim računarima (posebno Lenovo ThinkPad/ThinkCentre) često vraćaju internu šifru matične ploče (npr. `20L6S01W00` ili `10MUS02800`) umesto prepoznatljivog komercijalnog imena (npr. *ThinkPad T480* ili *ThinkCentre M720q*). S druge strane, kod sklopljenih desktop konfiguracija (custom buildovi), WMI vraća generičke stringove poput `'System Product Name'` ili `'To be filled by O.E.M.'`, umesto naziva matične ploče (npr. *B450M PRO-VDH MAX*).

4. **Kašnjenje prikaza i zastoje UI niti usled sporih WMI upita:**
   Očitavanje procesora, radne memorije i rezolucije ekrana u `DetectHardware()` zavisilo je isključivo od teških WMI upita i sinhronog `Dispatcher.Invoke` poziva iz pozadinske niti radi očitavanja `SystemParameters.PrimaryScreenWidth`. U trenucima visoke disk i CPU aktivnosti, ovo je izazivalo mikrozastajkivanja pri prvom startu UI-ja.

5. **Zamrzavanje korisničkog interfejsa tokom termalnog stres testa:**
   Termalni stres test (uveden u v2.9.0) koristio je `Task.Factory.StartNew` sa opterećenjem svih jezgara kroz `ThreadPool`. Zbog agresivnog matematičkog računanja bez predaje procesorskog vremena (`Thread.Yield`), dispečerska nit WPF-a i DWM kompozitor su ostajali bez procesorskih resursa, što je izazivalo vizuelno zamrzavanje tajmera, progres bara i prozora aplikacije tokom 30 sekundi rada testa.

6. **Zastareo i skučen vertikalni raspored u tabu `📦 Programi`:**
   Spisak od 8 sistemskih i servisnih programa bio je prikazan u jednostavnoj jednokolonskoj listi sa vertikalnim skrolovanjem, bez jasnih vizuelnih glifova, bez prepoznatljivog statusa za preskočene programe (npr. BatteryBar na desktop računarima ili Office na specifičnim profilima), i bez mini indikatora progresa u samoj stavci.

---

## 2. Donete Odluke i Arhitektonska Rešenja

### 2.1. FirstLogon Wi-Fi Bootstrap pre Launchera (`FirstLogon.ps1`)
U skriptu `FirstLogon.ps1` ugrađen je samostalni pred-pokretački bootstrap blok koji se izvršava pre poziva `GWADT.Launcher.exe`:
- **Inicijalizacija `wlansvc` servisa:** Proverava se stanje servisa `wlansvc`; ukoliko nije aktivan, pokreće se preko `Start-Service wlansvc` uz 500ms bezbedne pauze za podizanje steka.
- **Dinamički uvoz i servisna konekcija:** Modul `GWADT.Network.psm1` se uvozi dinamički i poziva se `Connect-ServisWifi -LocalSetupDir 'C:\setup'`. Funkcija kreira trajni `user=all` Wi-Fi profil iz `Wifi-Servis.ini` i forsira trenutno pridruživanje servisnoj mreži.
- **Pametna petlja čekanja (do 12 sekundi sa Fast-Exitom):** Skripta u petlji od najviše 12 iteracija (po 1s) proverava postojanje IPv4 adrese koja nije link-local (`169.254.*`) i verifikuje internet konekciju preko `Test-Connection -ComputerName 8.8.8.8 -Count 1 -Quiet`. Čim se veza uspostavi (ili ako je LAN kabl već bio povezan), petlja se trenutno prekida (`break`).
- **Fail-safe zaštita za računare bez Wi-Fi adaptera:** Ukoliko uređaj nema bežičnu karticu, skripta nakon 12s nastavlja regularan tok bez ikakvog blokiranja ili izbacivanja grešaka.

### 2.2. Preskakanje Faze 1 i Direktan Start od Faze 2 u C# UI (`MainWindow.xaml.cs`)
Pošto je FirstLogon bootstrap uspešno uspostavio mrežu pre starta UI-ja, re-evaluacija mreže u C# TaskRunner-u je redundanta:
- U metodama `_autoStartTimer.Tick`, `ExecuteStartService()` i `ExecutePause()` (nastavak rada), izvršavanje započinje direktno pozivom `StartPhaseExecution(2)` (Faza 2: Drajveri).
- Pre pokretanja tajmera, u log se upisuje jasna servisna beleška:
  `[MREŽA] FirstLogon mrežni bootstrap verifikovan — prelazak direktno na Fazu 2 (Drajveri).`
- U `GWADT.Network.psm1` i `Phase1-Network.ps1` zadržan je instant Fast-Pass: ako se skripta ikada pozove ručno, u 0 milisekundi vraća `exit 0` ukoliko je adapter aktivan. Takođe je obezbeđen tolerantan internet režim (ukoliko ServisHub nije dostupan na LAN-u ali internet jeste, Faza 1 uspešno prolazi uz Winget fallback umesto nasilnog prekida).

### 2.3. Hijerarhijska Normalizacija Modela Računara
Implementirana je objedinjena logika normalizacije modela u PowerShell podsistemu (`GWADT.Telemetry.psm1`, `GWADT.Hardware.psm1`) i C# WPF jezgru (`MainWindow.xaml.cs`):
- **Lenovo uređaji:** Primarni izvor naziva je `Win32_ComputerSystemProduct.Version` (gde Lenovo upisuje komercijalni naziv brenda, npr. `'ThinkPad T480'`), dok se interna oznaka tipa ploče (`Win32_ComputerSystem.Model` npr. `'20L6S01W00'`) ignoriše ako je komercijalno ime dostupno.
- **Dell uređaji:** Primarni izvor je `Win32_ComputerSystem.Model` (npr. `'Latitude 5490'`, `'OptiPlex 7050'`).
- **HP i Generic / Custom računari:** Ukoliko je očitana vrednost generička oznaka (`'System Product Name'`, `'To be filled by O.E.M.'`, `'Default string'`, `'All Series'`), vrši se automatski fallback na model matične ploče iz `Win32_BaseBoard.Product` (npr. `'B450M PRO-VDH MAX'`).

### 2.4. Hibridna Win32 Detekcija Hardvera u 0ms
U `DetectHardware()` sekciji implementiran je višeslojni hibridni pristup sa trenutnim odzivom:
- **Radna memorija (RAM):** Preko P/Invoke deklaracije `GlobalMemoryStatusEx` iz `kernel32.dll` (`MEMORYSTATUSEX`), ukupna fizička memorija se očitava trenutno u 0 milisekundi bez čekanja na WMI.
- **Procesor (CPU):** WMI upit je osiguran ultra-brzim Registry fallback-om iz ključa `HARDWARE\DESCRIPTION\System\CentralProcessor\0` (`ProcessorNameString`, `~MHz`), dok se broj logičkih jezgara očitava preko nativnog `Environment.ProcessorCount`.
- **Rezolucija ekrana:** Uklonjen je sinhroni `Dispatcher.Invoke` ka `SystemParameters.PrimaryScreenWidth/Height` koji je blokirao pozadinske niti, i zamenjen trenutnim Win32 API pozivima `GetSystemMetrics(0)` (SM_CXSCREEN) i `GetSystemMetrics(1)` (SM_CYSCREEN).
- **Osvežavanje hardvera na zahtev:** U tabu `💻 Informacije` ugrađeno je dugme `[🔄 Osveži informacije]` povezano sa `RefreshHardwareCommand` koja asinhrono u pozadinskoj niti ponovo pokreće kompletnu detekciju.

### 2.5. Stabilizacija i Optimizacija Termalnog Stres Testa
Otklonjen je uzrok zamrzavanja interfejsa tokom multi-core opterećenja:
- Umesto korišćenja deljenog `ThreadPool`-a preko `Task.Factory.StartNew`, kreira se namenski niz samostalnih niti `Thread[] workerThreads = new Thread[logicalCores]`.
- Svim radnim nitima se eksplicitno dodeljuje najniži prioritet:
  `t.Priority = ThreadPriority.Lowest;`
- Unutar radne petlje svake niti ugrađen je periodični poziv `Thread.Yield()` na svakih 20 iteracija, garantujući da DWM kompozitor i dispečerska nit WPF-a uvek imaju prednost pri dodeli procesorskog vremena.
- Vreme trajanja testa je deterministički fiksirano na 30 sekundi pomoću `Stopwatch.StartNew()` i preciznog uslova `sw.ElapsedMilliseconds < 30000`.
- Sva periodična ažuriranja GUI kontrola (tajmer, senzori, semafor) šalju se asinhrono dispečeru sa niskim prioritetom:
  `Dispatcher.BeginInvoke(DispatcherPriority.Background, ...)`
  čime je obezbeđen 100% gladak, responzivan i fluidan rad interfejsa.

### 2.6. Redizajn Taba `📦 Programi` (2x4 Kartični UniformGrid)
Tab je u potpunosti modernizovan po ugledu na savremene operativne panele:
- **WPF Izgled:** Uveden je `UniformGrid Columns="2"` unutar `ScrollViewer`-a, raspoređujući 8 programa u čist i pregledan 2x4 matrični format kartica.
- **Glif ikone:** Svakom programu je preko statičke metode `GetGlyphForAppId` dodeljen prepoznatljiv vizuelni glif (⚡ .NET Desktop Runtime, 🌐 Google Chrome, 🗜️ WinRAR, 📄 Adobe Acrobat Pro, 📊 Microsoft Office, 🔋 BatteryBar Pro, 🔑 Microsoft Activation Scripts, 🔗 Servisne Prečice).
- **Podrška za status `SKIPPED`:** Za programe koji se namenski preskaču uveden je elegantan bedž tamno plavo-sive boje (`#34495E`) sa oznakom `⏭ PRESKOČENO`.
- **Živi indikatori toka:** Kartica programa tokom instalacije svetli u plavoj boji (`#3498DB`), pri čemu se unutar nje automatski prikazuje i animira mini `ProgressBar`. Po završetku prelazi u zelenu (`#2ECC71`), a u slučaju greške u crvenu (`#E74C3C`).
- **Proširena statistika:** Zaglavlje taba u realnom vremenu prikazuje sažetak:
  `INSTALIRANO: X / 8  |  U TOKU: Y  |  PRESKOČENO: Z  |  GREŠKE: W`.

---

## 3. Posledice i Koristi

1. **Ubrzanje celokupnog toka za 20+ sekundi:** Automatski Wi-Fi bootstrap u `FirstLogon.ps1` i direktan prelazak na Fazu 2 eliminišu prazan hod i nepotrebne mrežne re-evaluacije.
2. **Besprekorna identifikacija flote:** Servisni izveštaji i telemetrija na ServisHub-u sada beleže stvarne modele uređaja (*ThinkPad T480*, *OptiPlex 7050*, *B450M PRO-VDH MAX*) umesto nerazumljivih šifara.
3. **Maksimalna stabilnost pod punim opterećenjem:** Serviser može nesmetano da koristi interfejs i pregleda parametre dok procesor radi na 100% u okviru termalnog testa.
4. **Vizuelna preglednost taba Programi:** Serviser jednim pogledom vidi kompletan status svih 8 aplikacija, uključujući mini progres barove i razlog preskakanja.
5. **100% C# 5.0 i PowerShell 5.1 kompatibilnost:** Sav kôd se bez ijednog upozorenja i greške kompajlira pod .NET Framework 4.8 / MSBuild 4.0 i izvršava na Windows 11 24H2.

---

## 4. Status i Verifikacija

- **Status:** Prihvaćeno (Accepted).
- **Verzija:** v2.10.0 (Release 2026-10-02 18:40:00).
- **QA Verifikacija:** Automatizovana skripta `Skripte/Test-Task120-QA.py` položena sa **86/86 PASS (100.0%)**.
- **Kompilacija:** Docker Wine MSBuild `gwadt-builder-wine:latest` uspešno generisao `GWADT.UI.exe` (275,456 bajtova, SHA-256: `8cb84a3ce3589be36175f497d077dc2827eee886cbb3f4841a78b5e6b2c1ea4a`).
- **OTA Distribucija:** ServisHub regenerisao dinamički paket `gwadt-payload.zip` (2,949,424 bajtova, SHA-256: `7c1bae0a7cbed80fe4cbafc7fb28cc882a55db2ab724881c596c1d5bf7c2dde7`).
