---
title: "ADR-0076: Dekompozicija Monolitne Skripte u 5 Modularnih Faza i UI Master Task Runner Orkestracija (GWADT v2.0)"
date: 2026-09-28
status: accepted
tags:
  - architecture
  - modularization
  - task-runner
  - phase-scripts
  - gwadt-common
  - ui-master
  - servishub-payload
  - zero-touch
  - best-effort
deciders:
  - WIS Service Core Team
author: gwadt_docs_sync
---

# ADR-0076: Dekompozicija Monolitne Skripte u 5 Modularnih Faza i UI Master Task Runner Orkestracija (GWADT v2.0)

## 1. Kontekst i Problem

U dosadašnjoj evoluciji GWADT platforme (kroz verzije v1.x i rane v2.x verzije do zadatka GWADT-TASK-075), srž automatizacije oslanjala se na monolitnu PowerShell skriptu `Install-ServisApps.ps1`. Iako je skripta bila sastavljena od logičkih modula u fazi bildovanja, krajnji izvršni artefakt na klijentskoj mašini bio je masivni monolit od preko 6.300 linija koda i približno **517 KB**.

Tokom intenzivne radioničke eksploatacije i rigoroznih QA testiranja uočeni su ozbiljni nedostaci takve arhitekture:

### 1.1. Memorijsko i Parsersko Opterećenje PowerShell 5.1 Endžina
- PowerShell 5.1 mora pri svakom pokretanju da učita, parsira u AST (Abstract Syntax Tree), kompajlira i validira kompletnu skriptu od 517 KB u memoriju.
- Na slabijim i starijim servisnim računarima (npr. stariji Intel Celeron/Core i3 procesori sa sporijim SATA SSD ili eMMC memorijama), ovo inicijalno parsiranje izazivalo je primetna kašnjenja (3 do 8 sekundi) pre nego što se izvrši ijedna stvarna komanda.
- Kada je implementiran C# UI Master Task Runner ([[decisions/ADR-0044-csharp-ui-master-task-runner-and-hub-telemetry]]), svaka faza se pokretala kao `powershell.exe -File Install-ServisApps.ps1 -Phase <N>`. To je značilo da se monolit od 517 KB parsirao iznova **5 puta**, stvarajući bespotrebno memorijsko i CPU opterećenje.

### 1.2. Otežano Debagovanje i Izdvojeno Testiranje Pojedinačnih Faza
- Ukoliko bi serviser ili QA inženjer želeo da testira ili debaguje samo instalaciju programa (Faza 4) ili samo konfiguraciju mreže (Faza 1), morao je pokretati celokupni monolit uz odgovarajuće parametre, ili ručno komentarisati blokove koda.
- Svaka varijabla, funkcija ili privremeno stanje mogli su izazvati neželjene bočne efekte (side-effects) na naredne faze zbog deljenog globalnog opsega (`$Global:`) unutar monolita.

### 1.3. Nužnost Kompletnog Rebuild-a pri Najmanjoj Promeni
- Svaka sitna korekcija u skripti (npr. promena CLI argumenta za jedan program ili promena Wi-Fi parametra) zahtevala je pokretanje Python build skripte (`Build-DeployScript.py`), rekompajliranje monolita, verifikaciju heševa na 6 različitih lokacija i ažuriranje celog paketa.
- Nije postojala mogućnost da se u hodu na klijentsku mašinu ili ServisHub distribuira samo jedna izmenjena skripta faze.

### 1.4. Rizik od Prekida Celog Toka usled Greške u Jednoj Aplikaciji
- U monolitu je postojala tendencija korišćenja strogog error handling-a (`$ErrorActionPreference = 'Stop'`) koji je pri neočekivanoj grešci u instalaciji nekog programa (npr. zaključan fajl ili nepoznat MSI kod) prekidao izvršavanje celokupne skripte, onemogućavajući prelazak na Fazu 5 (finalizaciju, skidanje privremenih tajni, slanje izveštaja).

---

## 2. Razmatrane Alternative

| Kriterijum | Opcija A: Zadržavanje Monolita sa `-Phase` Parametrom | Opcija B: Potpuna Eliminacija PowerShell-a i Pisanje Svega u C# | Opcija C: Dekompozicija na 5 Faza + `GWADT.Common.psm1` + UI Master Runner [PRIHVAĆENO] |
| :--- | :--- | :--- | :--- |
| **Veličina Fajla i Parsiranje** | Loše: 517 KB se parsira 5 puta | Odlično: C# kompajlirani binarni kod | **Odlično: Male skripte (15–50 KB), munjevito AST parsiranje (<100ms)** |
| **Modularnost i Izolacija** | Slaba: Deljen globalni opseg, mogući side-effects | Visoka, ali kruta (zahteva rekompilaciju UI-ja za svaku izmenu) | **Maksimalna: Svaka faza je samostalan proces sa čistim opsegom i exit kodom** |
| **Brzina Izmena i Održavanje** | Zahteva build pipeline za celokupni monolit | Zahteva Visual Studio / MSBuild i C# build | **Momentalna izmena pojedinačne faze u tekstualnom editoru bez diranja ostalih** |
| **Isporuka preko ServisHub-a** | Uvek se prenosi ceo monolit | Prenosi se novi exe | **`PayloadManager` automatski pakuje samo izmenjene faze i module u `gwadt-payload.zip`** |
| **Tolerancija Grešaka (Best-Effort)** | Teško kontrolisati prekid na nivou celog procesa | Kompleksno upravljanje procesima | **Best-Effort u Fazi 4: pad jednog programa ne prekida druge niti ruši Fazu 5** |

---

## 3. Doneta Odluka i Arhitektura Rešenja (GWADT-TASK-076)

Usvojena je nova modularna arhitektura **GWADT v2.0** zasnovana na tri komplementarna stuba:

```mermaid
flowchart TD
    subgraph UI_ENGINE["1. C# UI Master Task Runner (GWADT.UI.exe)"]
        UI1[MainWindow.xaml.cs] --> UI2[ExecutePhase: phaseNumber 1..5]
        UI2 --> UI3[Pretraga: C:\\setup\\Phases\\Phase{N}-*.ps1]
        UI3 --> UI4[Pokretanje procesa: powershell.exe -File ...]
        UI4 --> UI5[Asinhrono hvatanje STDOUT/STDERR i JSON IPC streaming]
        UI5 --> UI6[TelemetryService: REST & SSE sinhronizacija]
        UI2 --> UI7[Orkestracija međurestarta: deploy-state.json & RunOnce]
    end

    subgraph PHASE_SCRIPTS["2. Modularne Skripte Faza (C:\\setup\\Phases\\)"]
        P1["Phase1-Network.ps1 (Wi-Fi, LAN, Hub Discovery, START signal)"]
        P2["Phase2-Drivers.ps1 (PnP pnputil, Intel VMD, WU Driveri)"]
        P3["Phase3-Optimization.ps1 (Debloat, Brending, DPI, VC++, Flash&Release)"]
        P4["Phase4-Apps.ps1 (WinRAR, BatteryBar, Office, Chrome, Acrobat, OEM Licenca - Best-Effort)"]
        P5["Phase5-Finalize.ps1 (DMTF Report, Arhiva logova, Anti-Leak, UAC Restore, Cleanup)"]
    end

    subgraph COMMON_MODULE["3. Deljeni Zajednički Modul (GWADT.Common.psm1)"]
        CM1[Assert-ServisAdministrator & Initialize-ServisPaths]
        CM2[Send-IpcMessage, Send-IpcLog, Send-IpcProgress, Send-IpcStatus]
        CM3[Wait-ServisUICooperativePause & Test-ServisSkipRequested]
        CM4[Rotate-ServisLog, Write-ServisLog & Write-DiagnosticEvent]
        CM5[Save-DeployStatePhase & Get-DeployStatePhase]
    end

    subgraph HUB_INTEGRATION["4. ServisHub Auto-Packager (hub_server.py)"]
        H1[mtime monitor u Skripte/Phases/ i Skripte/Modules/]
        H1 --> H2[PayloadManager: automatsko pakovanje gwadt-payload.zip]
        H2 --> H3[Klijentski GWADT.Launcher.exe preuzima i raspakuje u C:\\setup]
    end

    UI4 -.-> P1
    UI4 -.-> P2
    UI4 -.-> P3
    UI4 -.-> P4
    UI4 -.-> P5

    P1 --> COMMON_MODULE
    P2 --> COMMON_MODULE
    P3 --> COMMON_MODULE
    P4 --> COMMON_MODULE
    P5 --> COMMON_MODULE
```

### 3.1. Izdvajanje Deljenog Modula `GWADT.Common.psm1`
Sva zajednička infrastruktura, standardizovane funkcije i globalne definicije izvučene su u visokoperformantni, samostalni modul `GWADT.Common.psm1` (lociran u `Skripte/Modules/` i `C:\setup\Modules\`):
1. **Administratorska elevacija:** `Test-ServisAdministrator` i `Assert-ServisAdministrator` garantuju da procesi rade sa punim sistemskim pravima.
2. **Standardizovani JSON IPC Cevovod:** Funkcije `Send-IpcMessage`, `Send-IpcLog`, `Send-IpcProgress` i `Send-IpcStatus` emituju čiste, jednolokacijske JSON objekte direktno na STDOUT za C# UI Master bez ikakvog neželjenog curenja teksta.
3. **Kooperativna kontrola toka:** `Wait-ServisUICooperativePause` (provera markera `C:\setup\.pause` sa periodičnim spavanjem od 500ms) i `Test-ServisSkipRequested` (marker `C:\setup\.skip`).
4. **Multi-Target logovanje i automatska rotacija:** `Write-ServisLog` i `Rotate-ServisLog` sa pragom od 10 MB obezbeđuju upis u konzolu, lokalni fajl `C:\setup\servis_deploy.log`, JSONL arhivu `servis_deploy.jsonl` i trajni sistemski direktorijum `C:\Windows\Logs\GWADT\`.
5. **Perzistencija stanja:** `Save-DeployStatePhase` i `Get-DeployStatePhase` za manipulaciju stanjem u `C:\setup\deploy-state.json`.

### 3.2. Pet Samostalnih Skripti Faza (`C:\setup\Phases\`)
Svaka faza je implementirana kao izolovani PowerShell 5.1 skript fajl sa eksplicitnim uvozom `GWADT.Common.psm1` i striktnim `exit 0` na uspešnom završetku:
- **`Phase1-Network.ps1` (Mreža i Servisni Wi-Fi):** Autonomno učitava konfiguraciju iz `Wifi-Servis.ini`, konfiguriše WLAN profil, optimizuje TCP/IP stek, proverava DHCP na LAN adapterima i vrši UDP/HTTP auto-discovery ServisHub-a na portu 8888/8080.
- **`Phase2-Drivers.ps1` (Drajveri i Hardverska Podrška):** PnP instalacija preko `pnputil.exe`, prioritetno ubacivanje Intel VMD / RST drajvera za skladišta, i selektivna instalacija nedostajućih drajvera preko Windows Update COM API-ja (`Microsoft.Update.Session`).
- **`Phase3-Optimization.ps1` (Optimizacija, Debloat, Brending i VC++):** Uklanjanje nepoželjnih UWP/Appx paketa, gašenje telemetrije (`DiagTrack`, `dmwappushservice`), preimenovanje korisničkog naloga u brend računara na osnovu CIM matične ploče, automatsko DPI skaliranje ekrana, instalacija kompletnog VC++ Redistributable paketa (2005-2022) i rano oslobađanje USB fleša (Flash & Release).
- **`Phase4-Apps.ps1` (Korisnički Programi i Aktivacija - Best-Effort):** Tiha instalacija lokalnih programa sa SSD-a (`C:\setup\Programi`): WinRAR, BatteryBar, Office 2024 (ODT), Google Chrome, Adobe Acrobat Pro (MSI/MSP), kreiranje desktop prečica i OEM licenca.
  * **Best-Effort princip:** Nijedan neuspeh pojedinačnog programa ne koristi prekidajući `throw`; greške se beleže, emituje se JSON status sa statusom `FAILED`, i skripta nastavlja na sledeću aplikaciju kako bi se osiguralo da računar dobije sve ostale programe.
- **`Phase5-Finalize.ps1` (Završetak, Flotno Izveštavanje, Anti-Leak i UAC):** DMTF očitavanje šasije (`TK-{Chassis}-{SerialNumber}`), slanje konačnog servisnog kartona na ServisHub `/api/devices/{id}/report`, arhiviranje dnevnika u `C:\Windows\Logs\GWADT\`, brisanje servisnog Wi-Fi profila (`netsh wlan delete profile`), vraćanje UAC nivoa na fabričke vrednosti (`ConsentPromptBehaviorAdmin = 5`, `PromptOnSecureDesktop = 1`) i pokretanje asinhronog samouništenja foldera `C:\setup`.

### 3.3. `GWADT.UI.exe` kao Master Task Runner Orkestrator
U klasi `MainWindow.xaml.cs`, metoda `ExecutePhase(int phaseNumber)` preuzela je punu ulogu Master Task Runner-a:
1. **Pronalaženje skripte faze:** Pretražuje kanonske putanje: `C:\setup\Phases\Phase{N}-*.ps1`, `C:\setup\Skripte\Phases\Phase{N}-*.ps1` i relativne putanje u odnosu na izvršni fajl.
2. **Pokretanje bez monolitnih parametara:** Poziva `powershell.exe -NoProfile -ExecutionPolicy Bypass -File "<scriptPath>"` bez starog parametra `-Phase`.
3. **Upravljanje međurestartom:** Na završetku Faze 3, metoda `HandleInterimReboot()` proverava da li je međurestart obavljen. Ako nije, upisuje `phase: 4` u `deploy-state.json`, postavlja `GWADT_Resume` RunOnce unos i pokreće kontrolisani 5s restart.
4. **Automatski nastavak:** Nakon restarta, metoda `GetSavedDeployStatePhase()` prepoznaje fazu 4 i momentalno pokreće `Phase4-Apps.ps1` bez ikakve potrebe za intervencijom servisera (100% Zero-Touch).

### 3.4. Dinamička ServisHub Auto-Packager Integracija
Na serveru radionice (`hub_server.py`), klasa `PayloadManager` proširena je tako da:
- Prati `mtime` direktorijuma `Skripte/Phases/` i `Skripte/Modules/`.
- Automatski pakuje skripte faza u arhivu `gwadt-payload.zip` pod obe putanje (`Phases/Phase{N}-*.ps1` i `Skripte/Phases/Phase{N}-*.ps1`), kao i deljeni modul `GWADT.Common.psm1`.
- Klijentski `GWADT.Launcher.exe` pri kontaktu sa hub-om raspakuje novu verziju u `C:\setup\Phases\`, omogućavajući trenutno ažuriranje bilo koje faze na celoj floti bez rekompilacije ISO medijuma.

---

## 4. Posledice

### 4.1. Pozitivne Posledice (Prednosti)
- 🚀 **Drastično Ubrzanje Pokretanja (Munjevito Parsiranje):** Veličina skripti smanjena je sa ~517 KB na 15–50 KB po fazi. Vreme parsiranja u PowerShell AST endžinu smanjeno je sa nekoliko sekundi na ispod 100 ms.
- 🧩 **Izolacija i Nezavisnost Faza:** Svaka faza ima čist varijabilni opseg i sopstveni procesni prostor. Pad jedne komponente u Fazi 2 ne može korumpirati memoriju u Fazi 4.
- 🛡️ **Pouzdanost i Best-Effort Izvršavanje:** Faza 4 garantuje da će se svi programi pokušati instalirati; nemogućnost instalacije jednog programa ne blokira ostale niti sprečava slanje izveštaja i finalizaciju u Fazi 5.
- 🔧 **Trivijalno Debagovanje i Održavanje:** Serviser može samostalno iz komandne linije pokrenuti npr. `powershell.exe -File C:\setup\Phases\Phase4-Apps.ps1` i odmah videti rezultat bez pokretanja prethodnih faza.
- ⚡ **OTA (Over-The-Air) Distribucija:** Izmene faza na ServisHub-u odmah se prenose na klijentske mašine kroz dinamički payload bez ponovnog bildovanja ISO-a.

### 4.2. Kompromisi i Ublažavanja
- **Zavisnost od Deljenog Modula:** Skripte faza zahtevaju postojanje `GWADT.Common.psm1`.
  * *Ublažavanje:* Skripte faza poseduju višestruke fallback putanje za pronalaženje modula (`$PSScriptRoot\..\Modules`, `$PSScriptRoot`, `C:\setup\Modules`, `C:\setup\Skripte\Modules`), a `PayloadManager` garantuje prisustvo modula u payload arhivi.
- **Upravljanje Međurestartom u C# Umesto u PowerShell-u:** Logika prelaska na Fazu 4 nakon restarta sada živi u C# kodu (`MainWindow.xaml.cs`).
  * *Ublažavanje:* Implementirana je robusna provera u `GetSavedDeployStatePhase()` i `HandleInterimReboot()` koja je pokrivena integracionim testovima sa 100% uspeha.

---

## 5. Verifikacija i QA Sertifikacija

Arhitektura je podvrgnuta sveobuhvatnom integracionom testiranju kroz `Skripte/Test-Task076-QA.py`:
- **148/148 testova uspešno položeno (100% PASS)**:
  - 100% AST validnost PowerShell 5.1 sintakse u svim fazama i modulu `GWADT.Common.psm1` (0 grešaka).
  - 100% C# 5.0 usaglašenost koda u `MainWindow.xaml.cs` (0 C# 6+ elemenata).
  - Verifikovan PE32 integritet i MD5 kontrolna suma `GWADT.UI.exe`: `bb7242151851d0ca9a79f81e2bd5539f`.
  - Verifikovana automatska regeneracija `gwadt-payload.zip` (SHA-256: `fd82c91a10ee...`, veličina: ~806–825 KB).
  - Potvrđen rad međurestart mehanizma i Best-Effort ponašanja u Fazi 4.

---

## 🔗 Povezane Beleške
- Glavni Indeks: [[index]]
- Pregled Arhitekture: [[architecture/overview]]
- Pajplajn Izvršavanja: [[architecture/pipeline]]
- PowerShell Moduli: [[architecture/modules]]
- C# Master Task Runner: [[decisions/ADR-0044-csharp-ui-master-task-runner-and-hub-telemetry]]
- Thin-Client Launcher i Payload: [[decisions/ADR-0075-thin-client-launcher-and-dynamic-servishub-payload]]
- Dvoetapni Deployment i Međurestart: [[decisions/ADR-0039-two-pass-deployment-interim-reboot]]
- Tiha Instalacija Aplikacija: [[architecture/silent-applications]]
