---
title: "ADR-0002: Centralizovani Keš Windows 11 Zakrpi i Status REST API"
type: decision
status: accepted
created: 2026-10-08
updated: 2026-10-08
tags:
  - adr
  - decision
  - architecture
  - windows-update
  - cache
  - rest-api
  - truenas
  - servis-hub
---

# ADR-0002: Centralizovani Keš Windows 11 Zakrpi i Status REST API

## 1. Kontekst i Problem

Tokom automatizovane instalacije i reinstalacije Windows 11 operativnih sistema na radnim stanicama u servisu "WIS Service", primena kumulativnih bezbednosnih zakrpi (Latest Cumulative Updates - LCU) predstavlja jednu od vremenski najzahtevnijih i mrežnih najopterećujućih faza.

### Tehnički Izazovi:
1. **Veliki Obim Podataka:**
   - Kumulativni paketi za Windows 11 x64 dostižu veličine između 1.1 GB i 4.5+ GB po paketu (npr. paketi `KB5129195` sa 4.4 GB i `KB5129242` sa 1.1 GB, ukupno preko 5.5 GB).
2. **Zagušenje Internet Linka:**
   - Kada se u servisu istovremeno instalira između 2 i 10+ računara, paralelno preuzimanje ovih paketa sa eksternih Microsoft servera potpuno zagušuje propusni opseg radionice i traje između 15 i 45 minuta po računaru.
3. **Potreba za Centralizovanim Skladištem na TrueNAS-u:**
   - Na gigabitnom TrueNAS skladištu uspostavljena je centralna lokacija za zakrpe (`/home/gru/Storage/Updates/Win11`).
   - Pozadinska automatizovana skripta `nas_update_sync.py` periodično komunicira sa Microsoft Update Catalog-om, preuzima najnovije `.msu` i `.cab` pakete, proverava njihove kontrolne sume i vrši rotaciju starijih izdanja.
4. **Nedostatak Programabilnog REST Uvida:**
   - Klijentske instalacione komponente (C# Launcher, C# Master UI, PowerShell skripte) i Web Dashboard nisu imale standardizovan način da pre početka instalacije provere koje su zakrpe zapravo dostupne u lokalnom kešu, koje su njihove veličine i da li je skladište montirano i spremno za brzi gigabitni prenos.

---

## 2. Razmatrane Opcije

- **Opcija A: Direktan SMB pristup sa klijentskih mašina (`\\NAS\Updates\Win11`):**
  - *Nedostaci:* Zahteva hardkodovane ili distribuirane SMB kredencijale u skriptama; osetljivo na restrikcije mreže i firewall pravila tokom rane faze instalacije; nema API validacije; klijenti mogu slučajno početi kopiranje fajlova koji su trenutno u fazi preuzimanja (`.part` ili `.tmp`).
- **Opcija B: Preuzimanje sa eksternog Microsoft Update Catalog CDN-a na svakom klijentu:**
  - *Nedostaci:* Potpuna zavisnost od spoljnog interneta, spor rad, nekontrolisan mrežni saobraćaj, nemogućnost rada u izolovanom ili zaštićenom laboratorijskom režimu.
- **Opcija C: Centralizovani REST API endpoint u ServisHub-u sa inteligentnim skenerom skladišta [PRIHVAĆENO]:**
  - ServisHub izlaže unificirani REST endpoint `GET /api/updates/status`.
  - Server na zahtev (ili kroz konfigurisanu putanju `SERVIS_UPDATES_DIR`) vrši inspekciju lokalnog ili montiranog NAS direktorijuma zakrpi.
  - Automatski prepoznaje i ekstraktuje KB brojeve iz naziva datoteka pomoću regularnih izraza (`extract_kb`).
  - Filtrira privremene i nedovršene datoteke (`.part`, `.tmp`, `.crdownload`, `.aria2`).
  - Vraća strukturirane podatke o ukupnom broju paketa, ukupnoj veličini u bajtovima i megabajtima, listi KB brojeva i detaljnim podacima o svakom paketu.

---

## 3. Doneta Odluka

Odlučeno je da se u ServisHub implementira kompletan podsistem za inspekciju i evidenciju Windows 11 kumulativnih zakrpi:

1. **REST Endpoint `GET /api/updates/status`:**
   - Podržan kroz primarni višenitni ruter (`ServisHubHandler`) i kroz FastAPI ruter (`servishub.api.updates.router`) sa `UpdatesStatusResponse` Pydantic modelom.
   - Podržava opcioni query parametar `?directory=<path>` za dinamičko ispitivanje alternativnih lokacija skladišta.
2. **Regex Ekstrakcija KB Identifikatora (`extract_kb`):**
   - Standardizovano prepoznavanje formata: `Windows11.0-KB5043080-x64.msu`, `windows11.0-kb5129195-x64.msu`, `KB5129242-amd64.cab` i hashovanih naziva.
3. **Hijerarhijsko Određivanje Putanje (`resolve_updates_directory`):**
   - Redosled rezolucije:
     1. Eksplicitno prosleđeni parametar (query param ili CLI `--updates-dir`)
     2. `SERVIS_UPDATES_DIR` promenljiva okruženja
     3. `SERVIS_HUB_UPDATES_DIR` promenljiva okruženja
     4. `HubConfig.updates_dir` konfiguracioni parametar
     5. Fizički mount `/home/gru/Storage/Updates/Win11`
     6. Relativna putanja `./Updates/Win11`
4. **Zaštita od Korumpiranih i Nedovršenih Preuzimanja:**
   - Skener automatski ignoriše datoteke sa ekstenzijama privremenih prenosa (`.part`, `.tmp`, `.crdownload`, `.aria2`), kao i direktorijume i nepovezane tekstualne fajlove.
5. **Dvoslojna Model Arhitektura (`servishub.models.updates`):**
   - Koristi se Pydantic kada je dostupan u okruženju, sa ugrađenim Standard Library fallback-om (`BaseModel` / `to_dict`) radi očuvanja nula eksternih zavisnosti.

---

## 4. Posledice

### Pozitivne:
- **Drakonsko Ubrzanje Instalacije (Pre-patching & Slipstreaming):**
  - Klijentske skripte preuzimaju pakete (`KB5129195` 4.4 GB i `KB5129242` 1.1 GB) preko lokalne gigabitne LAN mreže brzinom od 110–125 MB/s umesto preko interneta. Vreme primene zakrpa skraćeno je sa 30+ minuta na svega 45-60 sekundi.
- **Uvid u Realnom Vremenu:**
  - Web Dashboard, TUI CLI monitor i klijentske skripte u svakom trenutku imaju tačan uvid u stanje keša (`available`, `total_packages`, `kb_list`, `total_size_mb`).
- **Sinergija sa `nas_update_sync.py`:**
  - Pozadinski TrueNAS cron posao može autonomno osvežavati i rotirati pakete na disku, dok ServisHub bez restarta odmah reflektuje novonastalo stanje kroz REST API.
- **100% Unazadna Kompatibilnost:**
  - Endpoint je dostupan i na standardnom HTTP serveru i kroz opcioni FastAPI ruter, sa identičnom JSON strukturom.

### Zahtevi i Održavanje:
- Neophodno je obezbediti odgovarajući volumen mount za `/home/gru/Storage/Updates/Win11` unutar Docker kontejnera na TrueNAS SCALE serveru.
- Konfiguracija promenljive `SERVIS_UPDATES_DIR` u `.env` fajlu produkcije.

---

## 📚 Povezane Beleške
- Pregled arhitekture: [[architecture/overview]]
- Tehnička specifikacija: [[architecture/servis-hub]]
- Odluka o osamostaljivanju: [[decisions/ADR-0001-decoupling-servis-hub]]
- Početni indeks: [[index]]
- Zamke i specifičnosti: [[memory/gotchas]]
