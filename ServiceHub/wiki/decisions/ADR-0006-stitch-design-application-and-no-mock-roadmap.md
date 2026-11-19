---
title: "ADR-0006: Primena Stitch Dizajna, Striktna No-Mock Politika i Mapa Puta za Klijentske Sisteme"
type: decision
status: accepted
created: 2026-10-10
updated: 2026-10-10
tags:
  - adr
  - decision
  - architecture
  - ui
  - stitch
  - no-mock
  - telemetry
  - roadmap
  - zero-dependency
  - servis-hub
---

# ADR-0006: Primena Stitch Dizajna, Striktna No-Mock Politika i Mapa Puta za Klijentske Sisteme

## 1. Kontekst i Problem

Tokom evolucije korisničkog interfejsa ServisHub-a i prelaska na moderni tamni kokpit (`servishub/web/dashboard.py`), generisani su i integrisani elementi dizajna razvijeni pomoću **Google Stitch MCP** platforme (`DESIGN.md`, arhivirani Stitch ekrani u `docs/stitch_designs/`).

Međutim, u početnim eksperimentalnim verzijama prototipa, pojedine komponente korisničkog interfejsa (poput merača temperature procesora, indikatora zdravlja NVMe diska ili simulatora ekrana radne stanice) koristile su privremene statičke ili fiktivne vrednosti kako bi demonstrirale vizuelni izgled kokpita u punom radnom stanju (npr. fiksno 42 °C ili 100% S.M.A.R.T.).

U realnom radioničkom okruženju servisa računara "WIS Service", takva praksa nosi **visok operativni rizik**:
1. **Opasnost od obmane servisera:** Ako instrument na ekranu prikazuje zeleni status diska (100%) ili stabilnu temperaturu dok računar zapravo prolazi kroz termalni trotling ili ima oštećene NVMe sektore, serviser može propustiti kritičan hardverski kvar pre isporuke klijentu.
2. **Kršenje globalnog inženjerskog standarda (Strict No-Mock Policy):** Pravila sistema nalažu potpunu zabranu lažiranja telemetrije, lažnih mock objekata ili simuliranih podataka koji prividno prikazuju "zelene" sisteme.
3. **Potreba za jasnim inženjerskim razgraničenjem:** Korisnički interfejs mora reflektovati isključivo stvarne podatke pristigle sa klijenta, dok sve funkcionalnosti koje zahtevaju klijentsku podršku (agent u WinPE / Windows okruženju) moraju biti formalno dokumentovane kroz inženjersku mapu puta (`docs/BACKEND_IMPLEMENTATION_ROADMAP.md`).

---

## 2. Razmatrane Opcije

### Opcija A: Zadržavanje simuliranih/demo podataka u UI-ju dok se klijentski agent ne kompletira
- *Prednosti:* Ekran izgleda "popunjeno" i atraktivno tokom prezentacija.
- *Mane:* **Nedopustivo za radionicu.** Lažira stanje stvarnih računara, obmanjuje servisere i direktno krši Strict No-Mock politiku.

### Opcija B: Uklanjanje merača i telemetrijskih instrumenata iz UI-ja do implementacije klijenta
- *Prednosti:* Nema rizika od prikaza nepostojećih podataka.
- *Mane:* Narušava ergonomiju pripremljenog Google Stitch layouta, lomi postojeće integracione testove koji očekuju DOM elemente i zahteva ponovno redizajniranje interfejsa kada klijentski agent bude pušten u rad.

### Opcija C: Primena Stitch tokena i hibridnog layouta, uvođenje iskrenog statusa pripravnosti ("Čeka telemetriju") i izrada detaljne mape puta za 4 ključna sistema [PRIHVAĆENO]
- *Prednosti:*
  - Zadržava punu Google Stitch Linear/Vercel estetiku i split-pane ergonomiju.
  - U potpunosti eliminiše lažne vrednosti: tamo gde podatak ne postoji, prikazuje se iskren status `-- °C`, `0%` i `N/A (Čeka telemetriju)`.
  - Ekran ogledala eksplicitno komunicira stanje: *"Povezano na uređaj. Čeka se klijentski video/skrinšot strim sa radne stanice (Pogledaj: docs/BACKEND_IMPLEMENTATION_ROADMAP.md)"*.
  - Uspostavlja jasan tehnički plan u `docs/BACKEND_IMPLEMENTATION_ROADMAP.md` za 4 ključna sistema planirana za realizaciju u v3.2.0.

---

## 3. Doneta Odluka i Arhitektonska Specifikacija

Usvaja se **Opcija C**: ServisHub Web Dashboard primenjuje Google Stitch dizajn uz rigoroznu **Strict No-Mock politiku**, a sve predstojeće komponente klijent-server komunikacije definišu se u mapi puta `docs/BACKEND_IMPLEMENTATION_ROADMAP.md`.

```
+---------------------------------------------------------------------------------+
|                  Google Stitch Enterprise Dark Mode Cockpit                     |
|                                                                                 |
|  +---------------------------+   +-------------------------------------------+  |
|  | Fleet Explorer (Levo)     |   | Live Cockpit & Instrumenti (Desno)        |  |
|  | - Svi računari & filteri  |   | - 1 Hz Mirror: "Čeka Video Strim"         |  |
|  | - Reallni status faza     |   | - CPU / Termika: Realno ili "-- °C"       |  |
|  | - ZFS drajveri & garancija|   | - NVMe SMART: Realno ili "Čeka telemet."  |  |
|  +---------------------------+   +-------------------------------------------+  |
+---------------------------------------------------------------------------------+
                                           │
                        Striktno Razdvajanje (No-Mock)
                                           ▼
+---------------------------------------------------------------------------------+
|             docs/BACKEND_IMPLEMENTATION_ROADMAP.md (Specifikacija)              |
|                                                                                 |
|  1. 1 Hz Screen Mirror (JPEG buffer u RAM-u, WinPE Capture skripta)             |
|  2. Proširena Hardverska Telemetrija & NVMe Senzorika (WMI/ACPI, SMART)        |
|  3. Daljinsko Upravljanje Radnim Stanicama (Remote UEFI Reboot / PXE)           |
|  4. Samostalni OEM API & Ekspanzija Provajdera (Dell/HP, TrueNAS ZFS keš)       |
+---------------------------------------------------------------------------------+
```

### 3.1. Primena Google Stitch Dizajna u Dashboardu (`servishub/web/dashboard.py`)
- **CSS Tokeni:** Standardizovani na nivou `:root` (`--canvas: #0F172A`, `--surface: #1E293B`, `--border: #334155`, `--accent: #38bdf8`, `--text: #F8FAFC`).
- **Hibridni Split-Pane Layout:** Leva polovina nudi kompaktni pregled i pretragu računara sa globalnim brojačima (`#stat-total`, `#stat-active`, `#stat-done`), dok desna polovina pruža fokusirani radionički kokpit.
- **Collapsible Sidebar:** Smanjivanje širine sa 275 px na 68 px jednim klikom radi maksimalne iskorišćenosti radne površine na servisnim stolovima.

### 3.2. Striktna No-Mock Politika (Zero-Simulation Enforcement)
U sklopu funkcije `renderSelectedMachineCockpit()` u `dashboard.py`:
1. **Temperatura procesora (Termika):**
   - Ako klijent pošalje `cpu_temp` $\rightarrow$ prikazuje se stvarna vrednost u °C i status *"Senzor radne stanice aktivan"*.
   - Ako klijent ne pošalje podatak $\rightarrow$ striktno se prikazuje `-- °C` uz status `'N/A (Čeka telemetriju)'`.
2. **NVMe S.M.A.R.T. zdravlje:**
   - Ako podatak postoji $\rightarrow$ prikazuje se realan procenat i kategorija (*ČISTO*, *UPOZORENJE*).
   - Ako podatak ne postoji $\rightarrow$ prikazuje se `'N/A (Čeka telemetriju)'`, a grafički bar ostaje na `0%`.
3. **1 Hz Ogledalo ekrana:**
   - Ako nema video/slikovnog strima, viewport prikazuje fabrički standby okvir:
     > *"Povezano na uređaj. Čeka se klijentski video/skrinšot strim sa radne stanice (Pogledaj: docs/BACKEND_IMPLEMENTATION_ROADMAP.md)"*
   - Prikazuju se isključivo realni podaci o trenutnoj fazi instalacije i završenim aplikacijama iz pristigle telemetrije.

### 3.3. Dokumentovanje 4 Ključna Sistema u Mapi Puta (`docs/BACKEND_IMPLEMENTATION_ROADMAP.md`)
Definisana je arhitektura i prioriteti implementacije za sledeća 4 sistema:
1. **1 Hz Live Ogledalo Ekrana (Screen Mirror):**
   - Klijentska komponenta: PowerShell / DirectX Desktop Duplication skripta u WinPE-u koja na 1 Hz pravi JPEG frejm (kompresija 70%, 960x540 / 1280x720) i šalje `POST /api/devices/{id}/screen-frame`.
   - Backend: In-memory rotirajući bafer u `FleetStore` i serviranje preko `GET /api/devices/{id}/screen-frame.jpg` ili MJPEG strima za dashboard.
2. **Proširena Hardverska Telemetrija & NVMe Senzorika:**
   - Proširenje ugovora `POST /api/heartbeat` sa poljima `cpu_percent`, `cpu_temp`, `ram_used`, `smart` (SsdHealth, TBW, radni sati, temperatura kontrolera).
   - Očitavanje preko WMI/ACPI (`MSAcpi_ThermalZoneTemperature`) i `LibreHardwareMonitorLib.dll`.
3. **Daljinsko Upravljanje Radnim Stanicama (Remote UEFI Reboot):**
   - REST komandni endpoint: `POST /api/devices/{id}/command` sa akcijama `reboot_uefi` i `reboot_pxe`.
   - Distribucija komandi: Klijentski worker preuzima neizvršene komande u telu odgovora na periodični heartbeat (`pending_commands`) i poziva `shutdown.exe /r /fw /t 0`.
4. **Samostalni OEM API & Ekspanzija Provajdera:**
   - Proširenje postojećeg `LenovoOEMProvider`-a dodavanjem `DellOEMProvider` (Dell TechDirect / Service Tag) i `HpOEMProvider` (HP Image Assistant / SoftPaq).
   - Samostalni lokalni ZFS keš na TrueNAS-u za 100% offline radionice uz endpoint `POST /api/drivers/oem-resolve`.

---

## 4. Posledice i Prednosti

### Pozitivne Posledice:
- **Operativna pouzdanost i poverenje:** Serviseri u radionici imaju 100% tačan uvid u stanje hardvera bez rizika od previđanja kvara usled lažnih demo podataka.
- **Nulta stopa lažnih testova:** Nijedan integracioni test ne prolazi na osnovu veštačkih mockova; verifikacija je vezana za stvarne strukture podataka.
- **Očuvana Zero-Dependency arhitektura:** Korisnički interfejs ostaje implementiran u čistom ugrađenom Vanilla web steku (HTML5/CSS3/JS) unutar Python paketa bez Node.js zavisnosti.
- **Pripremljen teren za verziju v3.2.0:** Razvojni tim ima precizno definisanu mapu puta i API ugovore u `docs/BACKEND_IMPLEMENTATION_ROADMAP.md`.

### Ograničenja / Kompromisi:
- Dok se klijentske skripte ne integrišu u WinPE imidže radnih stanica, pojedini telemetrijski instrumenti u kokpitu prikazivaće indikator pripravnosti (`-- °C`, `N/A (Čeka telemetriju)`), što je svesno prihvaćeno u korist istinitosti prikaza.

---

## 5. Povezani Dokumenti i ADR-ovi

- [[decisions/ADR-0005-enterprise-dark-mode-cockpit-redesign]] — Prethodna odluka o redizajnu kokpita
- [[decisions/ADR-0004-oem-and-wis-tools]] — Modularni OEM podsistem i WIS USB alati
- [Inženjerska Mapa Backend Implementacija (docs/BACKEND_IMPLEMENTATION_ROADMAP.md)](file:///home/gru/Projects/Servis%20Hub/docs/BACKEND_IMPLEMENTATION_ROADMAP.md) — Detaljna specifikacija 4 klijentska sistema
- [Google Stitch Sistem Dizajna (DESIGN.md)](file:///home/gru/Projects/Servis%20Hub/DESIGN.md) — UI tokeni i smernice
- [[architecture/servis-hub]] — Glavna tehnička specifikacija sistema
