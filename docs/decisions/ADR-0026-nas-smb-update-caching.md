---
title: "ADR-0026: Lokalno NAS SMB Keširanje Kumulativnih Windows Zakrpi (Pre-Patch Faza)"
type: decision
status: accepted
date: 2026-09-23
author: gwadt_docs_sync
tags:
  - decision
  - adr
  - update
  - nas
  - smb
  - wusa
  - performance
  - security
  - caching
---

# ADR-0026: Lokalno NAS SMB Keširanje Kumulativnih Windows Zakrpi (Pre-Patch Faza)

## 1. Kontekst i Problem

Tokom serijskog servisnog rada na većem broju računara istovremeno, Faza 2 (Windows Update) tradicionalno je predstavljala vremenski najzahtevniji i najsporiji deo celokupnog automatizacionog pajplajna.

Uočeni su sledeći problemi:
1. **Zagušenje internet propusnog opsega (Bandwidth Bottleneck):** Kada se u radionici istovremeno priprema 5 do 10 računara, svaki računar samostalno preko javnog interneta preuzima najnovije kumulativne zakrpe (Cumulative Update - LCU, .NET Cumulative Update), čija veličina često dostiže od 500 MB do 1.5 GB po paketu. Ovo je dovodilo do zasićenja internet linka i produžavalo vreme instalacije na 30 do 45 minuta po mašini.
2. **Nepredvidivost Delivery Optimization protokola:** Iako Windows 11 poseduje *Delivery Optimization* P2P razmenu, ona se u radioničkim uslovima na sveže instaliranim mašinama (bez domena i zajedničkog tenant-a) često ne aktivira odmah, ili ima veliku latenciju pregovaranja.
3. **Potreba za bezbednim skladištem u lokalnoj mreži:** Servis poseduje interni NAS server (192.168.170.254) koji već služi za skladištenje ISO slika i iVentoy PXE butovanje. Bilo je prirodno iskoristiti gigabitni/10-gigabitni lokalni SMB share za keširanje `.msu` instalacionih paketa.
4. **Striktni bezbednosni zahtevi:** Klijentski računar nakon završetka instalacije **NIKADA NE SME** zadržati otvorene SMB sesije, mapirane mrežne diskove niti zapamćene akreditive servisnog NAS naloga.

Bilo je neophodno implementirati namensku **Pre-Patch fazu** koja pre pokretanja COM API pretrage proverava prisustvo lokalnog NAS servera, tiho instalira kumulativne zakrpe sa lokalnog gigabitnog deljenja preko alata `wusa.exe`, i po završetku bezbedno i nepovratno prekida sve mrežne sesije.

---

## 2. Razmatrane Opcije

- **Opcija 1: Oslanjanje isključivo na javne Microsoft Windows Update servere.**
  - *Mane:* Svaki računar preuzima gigabajte identičnih podataka preko interneta; osetljivost na variranje brzine provajdera i nepotrebno trošenje radioničkog interneta.
- **Opcija 2: Implementacija lokalnog WSUS (Windows Server Update Services) servera.**
  - *Mane:* Izuzetno glomazno rešenje; zahteva Windows Server licencu, Active Directory ili kompleksnu konfiguraciju GPO/registarskih ključeva na svakoj klijentskoj mašini, što komplikuje čišćenje pre isporuke klijentu.
- **Opcija 3: SMB deljenje na postojećem Linux/TrueNAS serveru sa dinamičkom `wusa.exe` instalacijom i striktnim `net.exe use` čišćenjem (Usvojeno).**
  - *Prednosti:* Jednostavna konfiguracija preko INI datoteke (`Wifi-Servis.ini`); provera dostupnosti NAS-a preko TCP porta 445; tiha instalacija preko zvaničnog `wusa.exe /quiet /norestart` alata; brzina prenosa do 100-120 MB/s (1 Gbps LAN) ili preko 500 MB/s (10 Gbps LAN); zagarantovano uklanjanje sesija u `finally` bloku i u završnoj fazi čišćenja (`GWADT.Cleanup.psm1`).

---

## 3. Doneta Odluka

Usvojena je **Opcija 3**. U modulu [`GWADT.Update.psm1`](file:///home/gru/Projects/WIS Service%20windows%2011%20automat/Skripte/Modules/GWADT.Update.psm1) kreirana je funkcija `Invoke-NasUpdatePrePatch`, koja se poziva na samom početku Faze 2 (pre nativnog COM API ciklusa).

```mermaid
flowchart TD
    A[Start: Faza 2 Windows Update] --> B[Učitaj konfiguraciju iz Wifi-Servis.ini]
    B --> C{Sekcija [ServisNas]<br/>Enabled == 1?}
    C -- Ne / Nije konfigurisano --> D[Nastavi direktno na COM API internet ažuriranje]
    
    C -- Da --> E[Brza provera NAS hosta:<br/>TCP utičnica Port 445 / 1500ms tajmaut]
    E --> F{NAS dostupan na LAN-u?}
    F -- Ne --> G[Log INFO: NAS nedostupan, preusmeri na internet WU]
    G --> D
    
    F -- Da --> H["Mapiraj SMB sesiju:<br/>net.exe use \\NasHost\ShareName /user:..."]
    H --> I[Skeniraj .msu pakete u Updates direktorijumu]
    I --> J{Pronađene .msu zakrpe?}
    J -- Ne --> K[Log INFO: Nema paketa na NAS-u]
    
    J -- Da --> L[Prikaži TUI status:<br/>Instaliram lokalne .msu zakrpe sa NAS-a...]
    L --> M["Izvrši tiho:<br/>wusa.exe &lt;paket.msu&gt; /quiet /norestart"]
    M --> N{Evaluacija Exit Koda:<br/>0 = Success<br/>3010 = Reboot Required<br/>2359302 = Already Installed}
    N --> O[Evidentiraj status u servisni log]
    O --> P{Ima još .msu paketa?}
    P -- Da --> M
    P -- Ne --> Q
    
    K --> Q[Finally Blok: Garantovano zatvaranje sesije]
    Q --> R["net.exe use \\NasHost\ShareName /delete /y"]
    R --> D
```

### 1. Konfiguracija u `Wifi-Servis.ini`

Sekcija `[ServisNas]` dodata je sinhronizovano u sve tri kopije konfiguracionog fajla (`Wifi-Servis.ini` u korenu, `Skripte/`, i `sources/$OEM$/$1/setup/`):

```ini
[ServisNas]
Enabled=1
NasHost=192.168.170.254
ShareName=gwadt-cache
UpdatesSubfolder=Updates\Win11
Username=servis
Password=servisnalog
```

> [!NOTE]
> Regex parser u modulu `GWADT.Update.psm1` koristi obrazac `^[ \t]*<Key>[ \t]*=[ \t]*([^\r\n#;]+)` koji striktno ograničava razmake na istu liniju, sprečavajući gutanje narednih parametara u slučaju praznih polja.

### 2. Tiha Instalacija i Rukovanje Exit Kodovima

Instalacija se vrši putem alata `wusa.exe` bez restartovanja sistema između paketa:
```powershell
$p = Start-Process "wusa.exe" -ArgumentList "`"$($msu.FullName)`" /quiet /norestart" -Wait -PassThru
$exitCode = if ($p) { $p.ExitCode } else { -1 }
```

Evaluacija povratnih kodova prati Microsoft specifikaciju:
- `0 (0x0)` — Uspešno instalirano (*Success*).
- `3010 (0xBC2)` — Uspešno instalirano, ali zahteva restart računara (*Success, reboot required*).
- `2359302 (0x00240006)` — Ažuriranje je već instalirano na sistemu (*WU_S_ALREADY_INSTALLED*).
- Ostali kodovi — Beleže se kao upozorenje (*WARN*) u servisni log, bez prekida rada skripte.

### 3. Sigurnosno Čišćenje i Uništavanje Kredencijala

Kako bi se garantovala stopostotna bezbednost serviserske mreže i sprečilo curenje lozinki:
1. **Lokalno zatvaranje u `finally` bloku:**
   ```powershell
   finally {
       & net.exe use "$uncShare" /delete /y >$null 2>&1
   }
   ```
2. **Globalno čišćenje u Fazi 5 (`GWADT.Cleanup.psm1`):**
   Tokom završne higijene računara, funkcija `Invoke-ServisCleanupAndFinalize` izvršava:
   ```powershell
   & net.exe use * /delete /y >$null 2>&1
   ```
   Ova komanda se takođe stavlja u odloženi batch proces neposredno pre nego što se ukloni radni direktorijum `C:\setup`.

---

## 4. Posledice i Benefiti

### Pozitivne Posledice:
- **Radikalno ubrzanje Faze 2:** Vreme potrebno za primenu kumulativnih zakrpi smanjeno je sa 20-30 minuta na svega 2-4 minuta, zahvaljujući lokalnom gigabitnom prenosu.
- **Rastelrećenje internet protoka radionice:** Više računara može se istovremeno pripremati bez degradacije brzine mrežne veze za ostale radne stanice.
- **Glatka integracija sa postojećim COM API-jem:** Nakon što `Invoke-NasUpdatePrePatch` instalira teške kumulativne pakete, COM API pretraga samo detektuje specifične hardverske drajvere, čime se ukupan broj preuzetih megabajta sa interneta svodi na minimum.
- **Bezbednosna izolacija klijentskog sistema:** Servisne lozinke i mrežni diskovi se garantovano brišu, čineći nemogućim neovlašćeni pristup servisnom NAS-u sa klijentske mašine.

### Negativne Posledice i Rizici (i ublažavanje):
- *Rizik:* Zastareli `.msu` paketi na NAS share-u mogu izazvati konflikt ili produžiti instalaciju.
  *Ublažavanje:* Kod rukuje kodom `2359302` (već instalirano) kao uspehom; servisni administrator kroz planirano mesečno osvežavanje medijuma ažurira i folder na NAS-u najnovijim zakrpama.

---

## 5. Povezane Datoteke i Arhitektonske Odluke

- **Implementirani kod:**
  - [`Skripte/Modules/GWADT.Update.psm1`](file:///home/gru/Projects/WIS Service%20windows%2011%20automat/Skripte/Modules/GWADT.Update.psm1) — Funkcija `Invoke-NasUpdatePrePatch` i pozivi u `Invoke-WindowsUpdatePhase`.
  - [`Skripte/Modules/GWADT.Cleanup.psm1`](file:///home/gru/Projects/WIS Service%20windows%2011%20automat/Skripte/Modules/GWADT.Cleanup.psm1) — Sanitizacija SMB sesija (`net.exe use * /delete /y`).
  - [`Wifi-Servis.ini`](file:///home/gru/Projects/WIS Service%20windows%2011%20automat/Wifi-Servis.ini) — Konfiguraciona sekcija `[ServisNas]`.
  - [`Skripte/Test-Task043-QA.py`](file:///home/gru/Projects/WIS Service%20windows%2011%20automat/Skripte/Test-Task043-QA.py) — Testiranje parsiranja INI sekcije, rubnih slučajeva i SMB sanitizacije.
- **Povezane Arhitektonske Odluke:**
  - [[ADR-0008-wu-com-api-streaming]] — Nativni Windows Update COM API.
  - [[ADR-0009-desktop-hygiene-cleanup]] — Standard desktop higijene i čišćenje tajni.
  - [[ADR-0011-wu-batch-download-delivery-optimization]] — Delivery Optimization i ubrzanje zakrpi.
