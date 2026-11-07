# ServisHub u WIS monorepo-u

Komponentna razvojna verzija: **3.2.0**, u WIS paketu **2.17.0**. Kanonski izvor je root `version.json`; `python3 ISO/Skripte/Sync-Versions.py --check` iz root-a proverava generisane verzije. [Pravila verzionisanja](../.workspace/wiki/operations/component-versioning-2026-10-15.md).

Aktivni razvoj servera nalazi se u `ServiceHub/` ovog repozitorijuma. Prethodni samostalni direktorijum je arhiva; produkcioni NAS nije menjan. Opis API-ja ispod čuva prethodne funkcionalnosti, dok je sledeći ugovor merodavan za release pipeline razvoja 2.17.

`PayloadManager` ne regeneriše niti potpisuje payload pri GET-u, startup-u ili `force` zahtevu. Služi poslednje provereno izdanje ili javlja grešku. Potpis, ZIP i deklarisani fajlovi proveravaju se pomoću `servishub/releases.py`. Nova izdanja potpisuju svaki ZIP fajl, isključuju servisne tajne i vezuju `release_id` za `source_commit`. Objava pravi nepromenljivi `cache/releases/<release_id>/` i atomarno menja `cache/current.json`.

Postojeći potpisani 2.16.2 schema-2 cache može se read-only proveriti, ali nije paket za novi Launcher koji zahteva kompletan manifest. Legacy `ISO/Skripte/ServisHub/hub_server.py` je tanak compatibility wrapper ka ovom serveru. CLI objava dodatno zahteva Windows VM PASS izveštaj istog commit-a kroz `--vm-report`. Build/verify/publish postupak nalazi se u [runbook-u](../.workspace/wiki/operations/build-release-media-2.17.md). Ne brisati cache i ne restartovati server radi automatskog pakovanja; objava je eksplicitan korak.

Test suite se izvršava sa `cd ServiceHub && python3 -m unittest discover -s tests` iz direktorijuma `ServiceHub/`. Tačan završni rezultat i neizvršeni Windows gate-ovi beleže se u [WIS-TASK-141](../.workspace/docs/WIS-TASK-141.md); zbir prošlih testova nije produkcioni sertifikat.


Razvojni raspored: ovaj direktorijum je jedna od četiri celine (`ISO`, `Launcher`, `UI`, `ServiceHub`). Zajednička dokumentacija i artefakti su u `../.workspace/`; lokalni test ne potvrđuje Windows runtime.

---

# ⚡ Tehno-Kutak Centralni Servisni Hub (ServisHub)
### Samostalni kontrolni server za telemetriju, proksiranje drajvera, OEM podsistem, WIS alate i nadzor radioničke flote (v3.2.0)

[![Python 3.10+](https://img.shields.io/badge/Python-3.10%2B-blue.svg)](https://www.python.org/)
[![Zero External Dependencies](https://img.shields.io/badge/Dependencies-Standard%20Library%20Only-brightgreen.svg)]()
[![Docker Ready](https://img.shields.io/badge/Docker-TrueNAS%20SCALE-blueviolet.svg)]()

[![Version](https://img.shields.io/badge/Release-v3.2.0-orange.svg)]()

---

## 📑 Sadržaj
1. [Pregled i Inženjerski Ciljevi](#-pregled-i-inženjerski-ciljevi)
2. [Arhitektura Sistema](#-arhitektura-sistema)
3. [Ključne Funkcionalnosti](#-ključne-funkcionalnosti)
4. [Sveobuhvatna REST API Specifikacija](#-sveobuhvatna-rest-api-specifikacija)
5. [Uputstvo za Klijentske Skripte (PowerShell)](#-uputstvo-za-klijentske-skripte-powershell)
6. [Modern Enterprise Dark Mode Cockpit](#-modern-enterprise-dark-mode-cockpit-web-dashboard)
7. [Mrežni UDP Auto-Discovery (Port 8888)](#-mrežni-udp-auto-discovery-port-8888)
8. [Pokretanje i Upotreba](#-pokretanje-i-upotreba)
9. [TrueNAS SCALE i Docker Produkcija](#-truenas-scale-i-docker-produkcija)
10. [Automatizovani Testovi](#-automatizovani-testovi)
11. [Dokumentacija i Baza Znanja](#-dokumentacija-i-baza-znanja)

---

## 🎯 Pregled i Inženjerski Ciljevi

**ServisHub** je centralni višenamenski kontrolni server, telemetrijski agregator, pametni keširajući proksi za drajvere, upravljač butabilnih instalacionih medija i OTA (Over-The-Air) distributivni repozitorijum razvijen za potrebe servisa računara **"Tehno-Kutak"**.

U verziji **v3.2.0**, ServisHub zaokružuje radionički rad "iz fotelje" implementacijom:
- **1 Hz Live Ogledala Ekrana (Screen Mirror):** Uživo praćenje ekrana klijentskih mašina u radionici preko laganog in-memory JPEG bafera (5s TTL).
- **Dvosmernog Daljinskog Upravljanja Radnim Stanicama:** Enqueue komandi (`reboot_uefi`, `reboot_pxe`, `cancel`, `abort`) i njihova asinhrona distribucija kroz odgovore na heartbeat puls radnika.
- **Proširene Hardverske Telemetrije & NVMe Senzorike:** Real-time očitavanje opterećenja procesora, termike (°C), RAM zauzeća i NVMe S.M.A.R.T. zdravlja.
- **Trozupca Modularnog OEM Podsistema (Lenovo, Dell i HP):** Fabrička provera garancije uz 5s fallback, rezolucija zvaničnih Enterprise paketa drajvera (SCCM / Deploy CAB / SoftPaq) i inspekcija novijih BIOS/UEFI verzija.

### Glavni Ciljevi:
- **Centralizovano Daljinsko Praćenje Flote ("Iz Fotelje"):** Praćenje instalacije i stanja 2 do 10+ računara istovremeno preko ugrađenog Web Dashboard-a (1 Hz ogledalo ekrana) ili Linux terminala (`hub_cli.py`).
- **Daljinski Reboot u UEFI/PXE:** Slanje komandi radnim stanicama jednim klikom iz Cockpita bez ručnog pritiskanja F2/F12 na svakoj mašini.
- **"Flash & Release" Autonomija:** Oslobađa butabilni USB fleš disk odmah nakon prve faze primene slike u WinPE-u. Računari sve dalje module i skripte dinamički preuzimaju sa ServisHub-a preko gigabitne mreže.
- **Fabrička OEM Trijaža (Lenovo, Dell, HP):** Automatsko očitavanje fabričke garancije preko zvaničnih API-ja uz striktni 5s timeout i graceful fallback na lokalni keš, provera BIOS/UEFI ažuriranja i rezolucija fabričkih enterprise paketa drajvera.
- **WIS Centar za UEFI USB Instalere:** Automatizovano formatiranje i priprema butabilnih fleševa (GPT FAT32 bootloader + NTFS podaci za `install.wim` > 4 GB) uz hardverske guardrail zaštite od formatiranja sistemskih particija i TrueNAS ZFS diskova.
- **Rana Detekcija Zaključanog Hardvera (Autopilot / MDM):** Prepoznavanje Intune / Autopilot korporativnih zakupaca u prvih 60 sekundi rada (Faza 1).
- **Pametna Rezolucija Drajvera bez Interneta na Klijentu:** ServisHub samostalno pretražuje Microsoft Update Catalog za date HWID identifikatore, preuzima zvanične OEM CAB pakete sa Microsoft CDN-a i proksira ih klijentu iz lokalnog keša.
- **Munjeviti TrueNAS Keš za Windows 11 Zakrpe:** Centralizovano čuvanje višemesečnih kumulativnih zakrpi (LCU) na gigabitnom NAS-u uz REST statusni uvid za klijente i autonomnu rotaciju paketa.
- **Dvosmerna Antigravity Sinhronizacija:** Pre-flight pull ranije servisne istorije po serijskom broju i post-flight push dijagnostičkih sesija sa prenosivog Antigravity USB fleša u centralni servisni karton.

---

## 🏗️ Arhitektura Sistema

```text
/home/gru/Projects/WIS/ServiceHub/
├── servishub/                     # Glavni modularni Python paket (32 modula, 12,112 linija)
│   ├── __init__.py & version.py   # Verzija (v3.2.0) i metapodaci
│   ├── config.py                  # Upravljanje konfiguracijom i putanjama
│   ├── server.py                  # Višenitni HTTP server i signal handler
│   ├── handlers.py                # REST API ruter sa CORS i XSS zaštitom
│   ├── discovery.py               # UDP Auto-Discovery osluškivač (:8888)
│   ├── telemetry.py               # In-memory evidencija flote, sesija, komandi i frejmova
│   ├── releases.py & payload.py   # Kriptografska verifikacija izdanja i serviranje wis-payload.zip
│   ├── reports.py                 # Generator JSON/Markdown kartona i AGY sesija
│   ├── cli.py                     # CLI parser komandne linije sa argparse
│   ├── tui_cli.py                 # Terminal TUI monitor flote
│   ├── api/                       # REST API ruteri (FastAPI i modularni)
│   │   ├── __init__.py
│   │   └── updates.py             # Ruter i skener za Windows 11 zakrpe (.msu/.cab)
│   ├── models/                    # Pydantic & Standard Library šeme
│   │   ├── __init__.py
│   │   └── updates.py             # Modeli podataka za inventar zakrpi
│   ├── drivers/                   # Podsistem za pametne OEM drajvere
│   │   ├── __init__.py
│   │   ├── hwid.py                # Normalizacija HWID-ja (ACPI, Audio, PCI, USB)
│   │   ├── version.py             # Semantičko i datumsko poređenje verzija
│   │   ├── scraper.py             # Microsoft Update Catalog scraper
│   │   ├── cache.py               # Menadžer lokalnog keša za .cab fajlove
│   │   └── oem/                   # Modularni OEM podsistem (Lenovo, Dell, HP)
│   │       ├── __init__.py        # get_oem_provider factory
│   │       ├── base.py            # BaseOEMProvider interfejs
│   │       ├── lenovo.py          # Lenovo provajder (garancija, SCCM, BIOS)
│   │       ├── dell.py            # Dell provajder (Service Tag, Deploy CAB, BIOS)
│   │       └── hp.py              # HP provajder (Support API, SoftPaq / Driver Pack, BIOS)
│   ├── wis/                       # WIS ISO i USB menadžer
│   │   ├── __init__.py
│   │   ├── iso.py                 # WisIsoManager (validacija i ekstrakcija)
│   │   └── usb.py                 # WisUsbEngine (Safety guardrails i UEFI particionisanje)
│   └── web/                       # Korisnički interfejs
│       ├── __init__.py
│       └── dashboard.py           # Modern Enterprise Dark Mode Cockpit (Linear/Vercel, Stitch)
├── tests/                         # Kompletna automatizovana testna pokrivenost (29 fajlova, 259 testova PASS)
│   ├── test_hwid.py               # Testovi normalizacije hardvera
│   ├── test_driver_versions.py    # Testovi poređenja verzija
│   ├── test_payload.py & test_payload_http.py # Testovi potpisanih izdanja i HTTP payload-a
│   ├── test_release_pipeline.py & test_preview_media.py # Testovi release i preview medijuma
│   ├── test_diagnostics_contract.py & test_component_versions.py # Testovi statusnog ugovora i verzija
│   ├── test_reports.py            # Testovi generisanja servisnih kartona
│   ├── test_udp_discovery.py      # Testovi UDP Auto-Discovery protokola
│   ├── test_api_integration.py    # Integracioni testovi osnovnih REST ruta
│   ├── test_updates_api.py        # Testovi status API-ja za Windows zakrpe
│   ├── test_oem_lenovo.py         # Testovi Lenovo garancije, SCCM i 5s fallbacka
│   ├── test_oem_dell.py           # Testovi Dell garancije, CAB paketa i BIOS-a
│   ├── test_oem_hp.py             # Testovi HP garancije, SoftPaq paketa i BIOS-a
│   ├── test_oem_expansion_integration.py # E2E integracija OEM proširenja (Dell, HP, Lenovo)
│   ├── test_screen_mirror.py      # Testovi 1 Hz screen frame bafera i REST ruta
│   ├── test_screen_capture_client.py # Validacija WIS.ScreenCapture.ps1 klijentskog modula
│   ├── test_remote_commands.py    # Integracioni testovi za daljinsko upravljanje (FIFO red)
│   ├── test_heartbeat_telemetry.py# Testovi senzorske telemetrije (CPU, RAM, SMART)
│   ├── test_wis.py                # Testovi bezbednosti, UEFI GPT layouta i ISO-a
│   ├── test_wis_and_oem_api.py    # Integracioni testovi za WIS rute i OEM rezoluciju
│   ├── test_agy_sync_api.py       # Testovi pre-flight i post-flight AGY sesija
│   ├── test_dashboard_ui.py       # Testovi Web Dashboard-a i tabova
│   └── test_tui_cli.py            # Testovi konzolnog monitora
├── docker/                        # Kontejnerizacija za TrueNAS SCALE
│   ├── Dockerfile
│   └── docker-compose.yml
├── wiki/                          # Lokalni Obsidian Wiki sistem znanja
├── hub_server.py                  # Glavni pokretački wrapper
├── hub_cli.py                     # TUI klijent za praćenje iz terminala
├── start-servis-hub.sh            # Pomoćna bash skripta za brzo pokretanje
├── .env.example                   # Primer konfiguracije promenljivih
├── requirements.txt               # Nula eksternih zavisnosti
└── README.md
```

---

## ⚡ Ključne Funkcionalnosti

### 1. Inteligentna HWID Normalizacija Drajvera
Kada Windows prijavi nepoznat uređaj u Device Manager-u, njegov identifikator često sadrži sabirničke prefikse i revizije. Generator kandidata (`servishub.drivers.hwid`) automatski kreira kaskadnu listu od najspecifičnijeg ka najopštijem:
- **ACPI Uređaji:** `ACPI\VEN_INTC&DEV_1078` $\rightarrow$ `ACPI\INTC1078` $\rightarrow$ `*INTC1078` $\rightarrow$ `INTC1078` $\rightarrow$ `VEN_INTC&DEV_1078`
- **Audio (HDAUDIO):** Uklanjanje sabirničkih kolekcija `&COLxx`, revizija `&REV_xxxx` i podsistema `&SUBSYS_xxxxxxxx`.
- **PCI i USB Uređaji:** Hijerarhijsko skidanje revizija do `VEN_xxxx&DEV_xxxx` i `VID_xxxx&PID_xxxx`.

### 2. Trozubac Modularnog OEM Podsistema (Lenovo, Dell i HP)
- **Lenovo eSupport:** Automatska provera statusa garancije sa trajanjem od max 5 sekundi (graceful offline fallback), zvanični SCCM paketi u `storage/drivers_cache/oem/lenovo/{MTM}/` i poređenje BIOS verzija.
- **Dell Support & Command | Deploy:** Upiti o garanciji preko Dell Service Tag-a (7 alfanumeričkih znakova), rezolucija zvaničnih Dell Command | Deploy enterprise CAB paketa u `storage/drivers_cache/oem/dell/{model}/` i preporuke za novije BIOS/UEFI verzije.
- **HP Support & SoftPaq:** Upiti o garanciji preko HP serijskih brojeva i Product ID-ja, preuzimanje i keširanje zvaničnih HP SoftPaq / Driver Pack (HPIA) paketa u `storage/drivers_cache/oem/hp/{model}/` i detekcija novijih BIOS ažuriranja.
- **Zajednički standard:** Svi provajderi poštuju `BaseOEMProvider` interfejs, imaju striktno vremensko ograničenje mrežnog poziva na 5s, lokalni perzistentni keš na TrueNAS disku i višenitnu zaštitu.

### 3. 1 Hz Live Ogledalo Ekrana (Screen Mirror)
- **Vizuelni uvid u realnom vremenu:** Klijentska radna stanica šalje JPEG frejmove na 1 Hz (`POST /api/devices/{id}/screen-frame`).
- **Lagani RAM bafer sa 5s TTL:** ServisHub čuva isključivo poslednji primljeni frejm u radnoj memoriji. Ukoliko klijent prestane sa slanjem, frejm automatski ističe nakon 5 sekundi kako bi se sprečio prikaz zastarelog stanja.
- **Ugrađeno u Web Cockpit:** Viewport na dashboardu automatski osvežava sliku uz LIVE bedž, a u odsustvu signala prikazuje Standby status pripravnosti.

### 4. Dvosmerno Daljinsko Upravljanje Radnim Stanicama (Remote Commands)
- **Asinhroni komandni kanal:** Serviser iz Cockpita jednim klikom šalje komande radnim stanicama (`POST /api/devices/{id}/command`).
- **FIFO red i zaštita resursa:** Server održava FIFO red komandi po uređaju (maksimalno 20 komandi) koji se atomski prazni i prosleđuje klijentu pri svakom redovnom pulsu (`POST /api/heartbeat`).
- **Klijentska egzekucija:** `WIS.Telemetry.psm1` preuzima komande i pokreće ciljanu akciju (`reboot_uefi` poziva `shutdown.exe /r /fw /t 3`, `reboot_pxe` menja redosled butovanja, `cancel`/`abort` prekida procese).
- **Potvrdni sigurnosni modal:** Cockpit sprečava slučajni prekid instalacije zahtevanjem eksplicitne potvrde pre slanja komande za restart.

### 5. Proširena Hardverska Telemetrija & NVMe Senzorika
- **Real-time senzori:** Svaki periodični heartbeat prenosi `cpu_percent`, `cpu_temp`, `ram_used`, `ram_total`, `ram_percent` i ugnježdeni `smart` rečnik (`SsdHealth`, `Category`, `Temperature`).
- **Inkrementalno očuvanje stanja:** `FleetStore` inteligentno čuva postojeće senzorske podatke i kada klijent privremeno pošalje samo status instalacije bez novih očitavanja senzora.

### 6. WIS Centar: UEFI USB Builder sa Sigurnosnim Zaštitama
- **Safety Guardrails:** Pre bilo kakve intervencije na disku, sistem proverava sistemske tačke montiranja (`CRITICAL_SYSTEM_MOUNTS`), aktivne TrueNAS ZFS pool-ove (`get_active_zpool_devices`) i zahteve za prenosivost. Pokušaj formatiranja sistemskog ili ZFS diska se trenutno blokira.
- **Hibridni GPT FAT32 + NTFS Layout:** Particija 1 (FAT32, `UEFI_BOOT`, 512 MB) omogućava čist UEFI boot, dok Particija 2 (NTFS, `WIS_DATA`) skladišti Windows sliku bez 4 GB ograničenja.
- **Real-time SSE:** Korisnik na Web Dashboard-u ili preko API-ja prati narezivanje u realnom vremenu.

### 7. Dvosmerna Antigravity Sinhronizacija Sesija
- **Pre-flight Pull:** Terenski serviser odmah po povezivanju na USB flešu dobija kompletnu servisnu istoriju računara sa Hub-a.
- **Post-flight Push:** Zaključak AI dijagnostike, izvršeni alati i unosi u dnevnik trajno se arhiviraju u `storage/reports/<DeviceTag>/SERVISNI_DNEVNIK.md`.

---

## 📡 Sveobuhvatna REST API Specifikacija

| Endpoint | Metod | Opis | Request | Response |
| :--- | :--- | :--- | :--- | :--- |
| `/` ili `/index.html` | `GET` | Ugrađeni Dark-Mode Web Dashboard sa 3 taba | - | `text/html` |
| `/api/health`, `/api/status` | `GET` | Health check i status provera | - | `{"status": "ok", "uptime_sec": int}` |
| `/api/payload/version` | `GET` | Provera aktuelne verzije i SHA-256 heša OTA paketa | - | JSON meta podaci |
| `/api/payload/download` | `GET` | Binarno preuzimanje `gwadt-payload.zip` paketa | - | `application/zip` stream |
| `/api/drivers/resolve` | `POST` | Rezolucija drajvera + fabrička garancija i OEM paket (Lenovo, Dell, HP) | `{"hwids": [...], "manufacturer": str, "model": str, "serial_number": str}` | JSON lista drajvera + `"warranty"` + `"oem"` |
| `/api/drivers/check_update`| `POST` | Provera postojanja novije verzije drajvera | `{"hwids": [...], "current_version": str}` | JSON status ažuriranja |
| `/api/drivers/download` | `GET` | Preuzimanje keširanog OEM `.cab` drajvera po ID-ju | Query: `?id=<hash>` | `application/vnd.ms-cab-compressed` |
| `/api/drivers/cache/list` | `GET` | Pregled svih keširanih drajvera | - | `{"drivers": [...], "count": int}` |
| `/api/devices` | `GET` | Sumarni spisak svih mašina flote (sa `has_screen_stream` i senzorima) | - | JSON mapa uređaja |
| `/api/devices/<id>/logs` | `GET` | Preuzimanje poslednjih do 300 linija loga | - | JSON niz stringova |
| `/api/devices/<id>/download`| `GET` | Preuzimanje kompletnog tekstualnog `.log` fajla | - | `text/plain` |
| `/api/devices/<id>/report`| `GET` | Preuzimanje sačuvanog JSON servisnog kartona | - | JSON servisni karton |
| `/api/devices/<id>/report`| `POST` | Zaprima završni izveštaj i generiše `.json` i `.md` | JSON završnog kartona | `{"status": "ok", "json_file": str}` |
| `/api/devices/<id>/history`| `GET` | Istorijat ranijih intervencija za dati ID uređaja | - | JSON istorijat (`is_registered`, `interventions`) |
| `/api/devices/by-serial/<serial>/history`| `GET` | Istorijat ranijih intervencija po serijskom broju | - | JSON istorijat po serijskom broju |
| `/api/devices/<id>/agy-session`| `POST` | Arhiviranje servisne sesije Antigravity AI servisera | JSON AGY sesije | `{"status": "ok", "session_id": str}` |
| `/api/devices/<id>/screen-frame`| `POST` | Prijem binarnog 1 Hz JPEG frejma ekrana sa klijenta | `image/jpeg` binarni podaci (heder `X-Client-Timestamp`) | `{"status": "ok", "device_id": str, "size_bytes": int}` |
| `/api/devices/<id>/screen-frame.jpg`| `GET` | Dohvatanje svežeg JPEG frejma ekrana (max 5s TTL) | Query: `?t=<timestamp>` (keš-basting) | `image/jpeg` frejm ili HTTP 404 |
| `/api/devices/<id>/command`| `POST` | Enqueue daljinske komande radnoj stanici (FIFO red) | `{"action": "reboot_uefi", "params": {...}}` | `{"status": "ok", "device_id": str, "command": {...}}` |
| `/api/devices/<id>/commands`| `GET` | Idempotentni pregled beklogovanih komandi u redu | - | `{"status": "ok", "device_id": str, "commands": [...]}` |
| `/api/wis/iso/info` | `GET` | Informacije o WIS ISO slici na serveru | - | JSON info (`available`, `version`, `sha256`) |
| `/api/wis/iso/download` | `GET` | Preuzimanje WIS ISO slike sa servera | - | `application/octet-stream` |
| `/api/wis/usb/list` | `GET` | Lista USB diskova sa bezbednosnim statusom | Query: `?all=true` | JSON lista diskova (`is_safe`, `blocked_reason`) |
| `/api/wis/usb/burn` | `POST` | Pokretanje asinhronog narezivanja USB instalera | `{"device": "/dev/sdX", "confirm_wipe": true}` | `{"status": "started", "device": str}` (HTTP 202) |
| `/api/wis/usb/progress` | `GET` | SSE živi strim napretka narezivanja USB diska | - | `text/event-stream` (faza, procenat, poruka) |
| `/api/session/<id>/state` | `POST` | Prijem trenutnog stanja klijentskog GUI-ja | JSON stanje sesije | `{"ok": true, "session_id": str}` |
| `/api/session/<id>/stream`| `GET` | Server-Sent Events (SSE 1 Hz) živi strim ekrana | - | `text/event-stream` |
| `/api/sessions` | `GET` | Lista svih aktivnih i STALE sesija na serveru | - | JSON lista sesija |
| `/api/heartbeat` | `POST` | Periodični puls radnika (senzori, napredak i preuzimanje komandi) | JSON (`computer_name`, `phase`, `cpu_percent`, `cpu_temp`, `ram_used`, `ram_total`, `smart`) | `{"status": "ok", "timestamp": float, "pending_commands": [...]}` |
| `/api/log` | `POST` | Batch slanje novih linija loga sa klijenta | `{"lines": [...]}` | `{"status": "ok", "appended": int}` |
| `/api/stream/<dev_id>` | `GET` | SSE živi prenos linija loga (`tail -f`) za mašinu | - | `text/event-stream` |
| `/api/lifecycle` | `POST` | Dvotačkasti životni ciklus (`STARTED` i `COMPLETED`) | JSON događaja | `{"status": "ok", "event": str}` |
| `/api/updates/status` | `GET` | Status i inventar Windows 11 kumulativnih zakrpi na NAS kešu | Query: `?directory=<path>` (opciono) | JSON status skladišta zakrpi (`UpdatesStatusResponse`) |

---

### 🔹 Primeri Poziva Novih Ruta

#### 1. Provera i Narezivanje WIS USB Medijuma

**Prikaz informacija o ISO slici:**
```bash
curl -s http://127.0.0.1:8080/api/wis/iso/info
```
```json
{
  "available": true,
  "filename": "WIS_Win11_24H2_v3.1.iso",
  "version": "3.1.0",
  "size_bytes": 6245120000,
  "sha256": "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"
}
```

**Lista USB uređaja sa bezbednosnim statusom:**
```bash
curl -s http://127.0.0.1:8080/api/wis/usb/list
```
```json
[
  {
    "device_path": "/dev/sdb",
    "model": "Kingston DataTraveler 3.0",
    "size_formatted": "28.8 GiB",
    "is_safe": true,
    "blocked_reason": null
  },
  {
    "device_path": "/dev/sda",
    "model": "Samsung SSD 870 EVO",
    "size_formatted": "465.8 GiB",
    "is_safe": false,
    "blocked_reason": "Disk sadrži sistemsku particiju montiranu na /"
  }
]
```

**Pokretanje narezivanja USB instalera:**
```bash
curl -s -X POST http://127.0.0.1:8080/api/wis/usb/burn \
  -H "Content-Type: application/json" \
  -d '{"device": "/dev/sdb", "confirm_wipe": true}'
```
```json
{
  "status": "started",
  "device": "/dev/sdb",
  "message": "Započeto narezivanje WIS ISO slike na /dev/sdb."
}
```

**Praćenje napretka narezivanja (SSE):**
```bash
curl -N http://127.0.0.1:8080/api/wis/usb/progress
```
```text
data: {"phase": "partitioning", "percent": 50, "message": "Kreiranje GPT tabele..."}

data: {"phase": "formatting_ntfs", "percent": 90, "message": "Formatiranje WIS Data particije..."}

data: {"phase": "completed", "percent": 100, "message": "UEFI USB je uspešno formatiran."}
```

---

#### 2. Antigravity AI Serviser: Istorija i Sinhronizacija

**Pre-flight očitavanje istorije po serijskom broju:**
```bash
curl -s http://127.0.0.1:8080/api/devices/by-serial/PF2X98ZA/history
```
```json
{
  "status": "ok",
  "is_registered": true,
  "device_tag": "Lenovo_ThinkPad_T14_PF2X98ZA",
  "serial_number": "PF2X98ZA",
  "manufacturer": "Lenovo",
  "model": "ThinkPad T14 Gen 2",
  "total_interventions": 1,
  "interventions": [
    {
      "date": "2026-09-15 11:20:00",
      "session_id": "a1b2c3d4-...",
      "summary": "Čišćenje prašine i zamena paste. Normalizovana temperatura.",
      "status": "COMPLETED"
    }
  ]
}
```

**Post-flight arhiviranje završene dijagnostičke sesije:**
```bash
curl -s -X POST http://127.0.0.1:8080/api/devices/Lenovo_ThinkPad_T14_PF2X98ZA/agy-session \
  -H "Content-Type: application/json" \
  -d '{
    "device_tag": "Lenovo_ThinkPad_T14_PF2X98ZA",
    "serial_number": "PF2X98ZA",
    "session_id": "f81d4fae-7dec-11d0-a765-00a0c91e6bf6",
    "timestamp": "2026-10-09T14:45:00Z",
    "serviser_id": "USB_TEREN_01",
    "summary": "Otklonjen problem sa pregrevanjem NVMe drajva.",
    "status": "COMPLETED",
    "dnevnik_entry": "### [2026-10-09] Uspešno rešen termalni throttling."
  }'
```

---

#### 3. Proširena Rezolucija Drajvera sa OEM Garancijom (Lenovo, Dell, HP)

**Lenovo primer (eSupport API + SCCM):**
```bash
curl -s -X POST http://127.0.0.1:8080/api/drivers/resolve \
  -H "Content-Type: application/json" \
  -d '{
    "hwids": ["PCI\\VEN_8086&DEV_15F3"],
    "manufacturer": "Lenovo",
    "model": "21AH",
    "serial_number": "PF2X98ZA"
  }'
```
```json
{
  "status": "ok",
  "results": [...],
  "warranty": {
    "status": "ok",
    "in_warranty": true,
    "status_label": "U garanciji",
    "service_level": "Premier Support",
    "end_date": "2027-05-15",
    "days_remaining": 218
  },
  "oem": {
    "status": "ok",
    "package_name": "tp_t14_gen3_win11_21ah.exe",
    "download_url": "https://download.lenovo.com/pccbbs/mobiles/tp_t14_gen3_win11_21ah.exe",
    "size_bytes": 1684275200
  }
}
```

**Dell primer (Service Tag + Command | Deploy CAB):**
```bash
curl -s -X POST http://127.0.0.1:8080/api/drivers/resolve \
  -H "Content-Type: application/json" \
  -d '{
    "hwids": ["PCI\\VEN_8086&DEV_06E0"],
    "manufacturer": "Dell Inc.",
    "model": "Latitude 5420",
    "serial_number": "8ABC123"
  }'
```
```json
{
  "status": "ok",
  "results": [...],
  "warranty": {
    "status": "ok",
    "in_warranty": true,
    "status_label": "U garanciji",
    "service_level": "ProSupport Plus",
    "end_date": "2026-11-30",
    "days_remaining": 51
  },
  "oem": {
    "status": "ok",
    "package_name": "Latitude-5420-win11-A07-6N8DF.cab",
    "download_url": "https://downloads.dell.com/FOLDER10992341M/1/Latitude-5420-win11-A07-6N8DF.cab",
    "size_bytes": 1785421000
  }
}
```

**HP primer (Product ID / Serial + SoftPaq Driver Pack):**
```bash
curl -s -X POST http://127.0.0.1:8080/api/drivers/resolve \
  -H "Content-Type: application/json" \
  -d '{
    "hwids": ["PCI\\VEN_8086&DEV_02F0"],
    "manufacturer": "HP",
    "model": "EliteBook 840 G8",
    "serial_number": "5CG1234XYZ"
  }'
```
```json
{
  "status": "ok",
  "results": [...],
  "warranty": {
    "status": "ok",
    "in_warranty": false,
    "status_label": "Van garancije",
    "service_level": "Standard Warranty",
    "end_date": "2024-08-10",
    "days_remaining": 0
  },
  "oem": {
    "status": "ok",
    "package_name": "sp141520.exe",
    "download_url": "https://ftp.hp.com/pub/caps-softpaq/HP_Driver_Packs/sp141520.exe",
    "size_bytes": 1845493760
  }
}
```

---

#### 4. 1 Hz Live Ogledalo Ekrana (Screen Mirror)

**Slanje binarnog JPEG frejma sa radne stanice:**
```bash
curl -s -X POST http://127.0.0.1:8080/api/devices/TK-BENCH-01/screen-frame \
  -H "Content-Type: image/jpeg" \
  -H "X-Client-Timestamp: 1773244800" \
  --data-binary "@/tmp/screenshot.jpg"
```
```json
{
  "status": "ok",
  "device_id": "TK-BENCH-01",
  "size_bytes": 84210
}
```

**Preuzimanje svežeg frejma (sa keš-bastingom):**
```bash
curl -s "http://127.0.0.1:8080/api/devices/TK-BENCH-01/screen-frame.jpg?t=$(date +%s%N)" -o /tmp/current.jpg
```

---

#### 5. Daljinsko Upravljanje Radnim Stanicama (Remote Commands)

**Slanje komande za restart u UEFI/BIOS:**
```bash
curl -s -X POST http://127.0.0.1:8080/api/devices/TK-BENCH-01/command \
  -H "Content-Type: application/json" \
  -d '{
    "action": "reboot_uefi",
    "params": {"reason": "setup_bios_password"}
  }'
```
```json
{
  "status": "ok",
  "device_id": "TK-BENCH-01",
  "command": {
    "command_id": "cmd_a4f18c2b7e8d",
    "device_id": "TK-BENCH-01",
    "action": "reboot_uefi",
    "params": {"reason": "setup_bios_password"},
    "created_at": 1773244805.12
  }
}
```

**Idempotentni pregled komandi u redu:**
```bash
curl -s http://127.0.0.1:8080/api/devices/TK-BENCH-01/commands
```
```json
{
  "status": "ok",
  "device_id": "TK-BENCH-01",
  "commands": [
    {
      "command_id": "cmd_a4f18c2b7e8d",
      "action": "reboot_uefi",
      "params": {"reason": "setup_bios_password"},
      "created_at": 1773244805.12
    }
  ]
}
```

---

#### 6. Prošireni Senzorski Heartbeat

**Slanje pulsa radnika sa kompletnom telemetrijom:**
```bash
curl -s -X POST http://127.0.0.1:8080/api/heartbeat \
  -H "Content-Type: application/json" \
  -d '{
    "computer_name": "TK-BENCH-01",
    "phase": 3,
    "worker_progress": 65,
    "cpu_percent": 42.5,
    "cpu_temp": 58,
    "ram_used": "6.2 GB",
    "ram_total": "16.0 GB",
    "ram_percent": 38,
    "smart": {
      "SsdHealth": 98,
      "Category": "ČISTO",
      "Temperature": 41
    }
  }'
```
**Odgovor servera (sa atomskim preuzimanjem komandi za izvršavanje):**
```json
{
  "status": "ok",
  "timestamp": 1773244810.55,
  "pending_commands": [
    {
      "command_id": "cmd_a4f18c2b7e8d",
      "action": "reboot_uefi",
      "params": {"reason": "setup_bios_password"}
    }
  ]
}
```

---

## 💻 Uputstvo za Klijentske Skripte (PowerShell)

Klijentski moduli nalaze se u deljenom repozitorijumu skripti na TrueNAS skladištu:
`/home/gru/Storage/Skripte/Modules/` (i integrišu se u WIS WinPE i Windows radne slike).

### 1. `WIS.ScreenCapture.ps1` (1 Hz Live Screen Mirror)
- **Svrha:** Lagani proces u pozadini koji na 1 Hz pravi snimak ekrana primarnog monitora pomoću .NET `System.Drawing` i `System.Windows.Forms` biblioteka, kompresuje ga u JPEG (70% kvalitet, širina do 1280 px) i šalje na `/api/devices/{id}/screen-frame`.
- **Ključne funkcije:**
  * `Start-ServisScreenCapture`: Pokreće petlju snimanja u trenutnoj niti ili pozadinskom job-u.
  * `Stop-ServisScreenCapture`: Signalizira globalni `EventWaitHandle` za elegantno zaustavljanje.
  * `Capture-ServisScreenFrame`: Nisko-nivosko snimanje ekrana preko `CopyFromScreen` i skaliranje sa bikubičnom interpolacijom.
  * `Send-ServisScreenFrame`: HTTP POST prenos sa `Content-Type: image/jpeg` i timeout-om od 800 ms.
- **Primer pokretanja u pozadinskom PowerShell poslu:**
```powershell
# Pokretanje u pozadini tokom WinPE ili Windows instalacije
Start-Job -ScriptBlock {
    param($hubUrl, $devId)
    . "\\192.168.170.254\Storage\Skripte\Modules\WIS.ScreenCapture.ps1"
    Start-ServisScreenCapture -HubUrl $hubUrl -DeviceId $devId -IntervalMs 1000 -Quality 70 -MaxWidth 1280
} -ArgumentList "http://192.168.170.254:8080", $env:COMPUTERNAME
```
- **Zaustavljanje snimanja:**
```powershell
. "\\192.168.170.254\Storage\Skripte\Modules\WIS.ScreenCapture.ps1"
Stop-ServisScreenCapture
```

### 2. `WIS.Telemetry.psm1` (Hardverska Telemetrija i Remote Commands)
- **Svrha:** Centralni klijentski modul za periodičnu komunikaciju sa ServisHub-om (2s puls), očitavanje termike i memorije, kao i preuzimanje i izvršavanje daljinskih komandi.
- **Ključne funkcije:**
  * `Send-ServisHeartbeat`: Formira JSON sa trenutnom fazom, napretkom i poziva `Get-ServisHardwareTelemetry`. Iz primljenog odgovora automatski izdvaja `pending_commands` i prosleđuje ih na `Invoke-ServisRemoteCommand`.
  * `Get-ServisHardwareTelemetry`: Očitava WMI/ACPI termalne zone (`MSAcpi_ThermalZoneTemperature`), opterećenje procesora, memoriju (`Win32_OperatingSystem`) i NVMe SMART (`Get-PhysicalDisk`).
  * `Invoke-ServisRemoteCommand`: Obrađuje primljene akcije:
    - `"reboot_uefi"` $\rightarrow$ izvršava `shutdown.exe /r /fw /t 3` (uz fallback na `shutdown.exe /r /t 3` za mašine bez UEFI fw podrške).
    - `"reboot_pxe"` $\rightarrow$ priprema jednokratni mrežni boot i restartuje mašinu.
    - `"cancel"` / `"abort"` $\rightarrow$ prekida radne skripte.
- **Primer integracije u radioničku skriptu:**
```powershell
Import-Module "\\192.168.170.254\Storage\Skripte\Modules\WIS.Telemetry.psm1"

# Slanje pulsa sa automatskom telemetrijom i egzekucijom beklogovanih komandi
Send-ServisHeartbeat -HubUrl "http://192.168.170.254:8080" `
                     -DeviceId $env:COMPUTERNAME `
                     -Phase 3 `
                     -Progress 50 `
                     -Status "Instalacija drajvera i kumulativnih zakrpi"
```

---

## 🖥️ Modern Enterprise Dark Mode Cockpit (Web Dashboard)

Web interfejs na adresi `http://<IP_Servera>:8080/` kompletno je redizajniran u **Modern Enterprise Dark Mode Cockpit** vođen **Google Stitch MCP** dizajnerskim sistemom i specifikacijama iz [`DESIGN.md`](DESIGN.md) i `servis_hub_ui_prd_rfc.md`. 

Referentni Stitch prototipovi i ekrani trajno su arhivirani u [`docs/stitch_designs/`](docs/stitch_designs/) (`gen_4554aa07`, `gen_3013e442`, `gen_f0534f88`).

### 1. Vizuelni Identitet i Google Stitch Dizajn Sistem
- **Linear / Vercel Estetika:** Duboka slate pozadina platna (`#0F172A`), povišene kartice i paneli (`#1E293B`), suptilne 1px bordure (`#334155`) i Sky Blue akcenat (`#38bdf8`).
- **WCAG AA Kontrast & Tipografija:** Visoki kontrast teksta (`#F8FAFC`), podrška za sistem fontove (`Inter`, `-apple-system`, `Cascadia Code` za logove) i 4px baseline mreža za maksimalnu informacionu gustinu bez vizuelnog zamora.
- **Zero-Dependency Arhitektura:** 100% čist ugrađeni Vanilla HTML5, CSS3 i Vanilla JavaScript unutar Python modula `servishub.web.dashboard` — nula eksternih Node.js/npm zavisnosti i trenutno učitavanje (<150 ms na lokalnom LAN-u).

### 2. Collapsible Levi Sidebar (`.sidebar`)
- **Ergonomija Radnog Stola:** Oslobađa horizontalni i vertikalni radni prostor. Širina je 275 px u punom prikazu, sa mogućnošću trenutnog skupljanja na 68 px (`toggleSidebar()`).
- **Navigacione Sekcije:**
  - `🖥️ Radionička Flota & Telemetrija` (`#tab-btn-fleet` $\rightarrow$ `#tab-fleet`)
  - `📀 WIS Centar (ISO & USB Builder)` (`#tab-btn-wis` $\rightarrow$ `#tab-wis`)
  - `📦 Windows 11 Zakrpe` (`#tab-btn-updates` $\rightarrow$ `#tab-updates`)
  - `🛡️ OEM Drajveri & Garancija` (Brzi filter flote)
  - `📋 Servisni Dnevnici & AGY Sesije` (Brzi filter flote)
- **Server Vital Stats Panel (Footer Sidebara):** Prikaz ključnih parametara servera u realnom vremenu:
  - TrueNAS ZFS keš status (`ZFS Keš Aktivan`)
  - Uptime radnog servera
  - UDP Auto-Discovery na portu 8888 (`Aktivan :8888`)
  - Lokalni sinhronizovani radionički sat

### 3. Hibridni Split-Pane Cockpit za Flotu
Glavna radna površina flote podeljena je na dva međusobno povezana panela:

- **Leva Kolona (Fleet Explorer & Cards):**
  - **Globalni Brojači:** Ukupno mašina (`#stat-total`), Aktivno (`#stat-active`), Završeno (`#stat-done`), Restart u toku, Greške.
  - **Brza Pretraga i Filteri:** Filtriranje po stanju (*Sve*, *Aktivno*, *Završeno*, *Greška*) i modelu.
  - **Kartice Radnih Stanica:** Prikaz faza instalacije (Faze 1..5), S.M.A.R.T. indikatora diska, serijskog broja, fabričke garancije (*Lenovo Premier Onsite*, *U Garanciji*, *Van Garancije*), izvora drajvera (*Lenovo SCCM ZFS Keš*, *Microsoft Catalog*) i Antigravity bedževa (*Servisiran (AGY)*, *Kvar (AGY)*).
- **Desna Kolona (Focused Machine Live Cockpit):**
  - **1 Hz Workstation Screen Mirror:** Radionički viewport za praćenje ekrana klijentskog računara u realnom vremenu (1 Hz). Kada radna stanica šalje JPEG frejmove, viewport prikazuje sliku uživo uz zeleni `LIVE` indikator u gornjem desnom uglu. Ukoliko frejmovi izostanu ili su stariji od 5 sekundi (TTL), viewport se automatski prebacuje u iskren status pripravnosti (*Standby* režim: *"Povezano na uređaj. Čeka se klijentski video/skrinšot strim sa radne stanice"*), u skladu sa **Strict No-Mock politikom**.
  - **Hardware Telemetry Instrumenti (Strict No-Mock):** Merne komponente za temperaturu procesora (°C), radno opterećenje CPU-a (%), radnu memoriju (RAM zauzeće i procenat) i NVMe S.M.A.R.T. zdravlje (grafički bar i kategorija *ČISTO* / *UPOZORENJE*). Ukoliko klijent nije poslao telemetriju, prikazuju se stvarni statusi `-- °C`, `0%` i `N/A (Čeka telemetriju)`, čime se eliminiše rizik od previda kvarova usled fiksnih demo vrednosti.
  - **Quick Action Toolbar sa Zaštitnim Modalom:** Brze radnje za selektovanu radnu stanicu jednim klikom:
    - `🔄 Reboot u UEFI`: Slanje komande radnoj stanici za ulazak u UEFI/BIOS podešavanja.
    - `🌐 Reboot u PXE`: Priprema mrežnog butovanja i restart.
    - `🤖 Pokreni AGY Dijagnostiku`: Pokretanje Antigravity AI trijaže.
    - `📋 Servisni Karton & Dnevnik`: Otvaranje servisnog kartona i istorijata.
    - `✅ Označi Završeno`: Završetak servisnog ciklusa.
  - **Ugrađena SSE Dijagnostička Konzola:** Živi prikaz najnovijih linija loga (`tail -f`) direktno unutar Cockpita.

### 4. Načelo "Strict No-Mock Policy" i Realizovane Funkcionalnosti (v3.2.0)
U radioničkom radu sa stvarnim klijentskim računarima, **fiktivni podaci i lažni mockovi su strogo zabranjeni**. Serviser mora u svakom trenutku imati apsolutno istinit uvid u hardver.

Sve funkcionalnosti prethodno specificirane u [`docs/BACKEND_IMPLEMENTATION_ROADMAP.md`](docs/BACKEND_IMPLEMENTATION_ROADMAP.md) u potpunosti su realizovane u verziji v3.2.0 (detaljnije u [[decisions/ADR-0007-telemetry-screen-mirror-and-oem-expansion]]):
1. **1 Hz Live Ogledalo Ekrana (Screen Mirror):** Klijentski JPEG frejm bafer (1000 ms interval, kompresija 70%) u RAM-u servera i serviranje preko `GET /api/devices/{id}/screen-frame.jpg`.
2. **Proširena Hardverska Telemetrija & NVMe Senzorika:** Očitavanje stvarnih WMI/ACPI termalnih zona, CPU/RAM metrike i detaljnih NVMe S.M.A.R.T. atributa.
3. **Daljinsko Upravljanje Radnim Stanicama (Remote UEFI Reboot):** Dvosmerni komandni kanal kroz `/api/devices/{id}/command` i izvršavanje komandi (`shutdown.exe /r /fw /t 3`) putem periodičnog heartbeat odgovora.
4. **Samostalni OEM API & Ekspanzija Provajdera:** Proširenje modularnog OEM podsistema na Dell (Service Tag, Command | Deploy CAB) i HP (SoftPaq / HPIA), uz lokalni ZFS keš drajvera za radionice bez stalnog interneta.

### 5. WIS Centar & Windows 11 Zakrpe
- **📀 WIS Centar (ISO & UEFI USB Builder):**
  - Automatski uvid u aktuelnu WIS ISO sliku (`/api/wis/iso/info`), SHA-256 heš i preuzimanje.
  - Sigurna selekcija USB diskova sa višeslojnim filterom: automatsko blokiranje sistemskih i TrueNAS ZFS diskova.
  - Interaktivno narezivanje butabilnog fleša (GPT FAT32 bootloader + NTFS podaci) uz SSE praćenje progresa u realnom vremenu (`/api/wis/usb/progress`).
- **📦 Windows 11 Zakrpe (LCU Keš):**
  - Uvid u skladište kumulativnih zakrpi na gigabitnom TrueNAS serveru (`/api/updates/status`).
  - Pretraga i filtriranje `.msu` i `.cab` datoteka sa datumima i veličinama paketa.

### 6. Očuvanost Testnih Ugovora i Modala
Istorijski opis ranijih modalnih dijaloga i testova:
- **Potvrdni Modal za Daljinske Komande (`#command-confirm-modal`):** Zahteva eksplicitnu potvrdu servisera pre slanja restarta u UEFI/PXE radi sprečavanja slučajnih prekida radnog toka.
- **Terminal Log Modal (`#log-modal`):** Puni prikaz konzole za dubinsku dijagnostiku.
- **Servisni Karton Modal (`#report-modal`):** Prikaz izgenerisanog servisnog kartona sa tabelama i specifikacijom.
- **Servisna Istorija i Dnevnik Modal (`#history-modal`):** Prikaz hronologije intervencija i `SERVISNI_DNEVNIK.md`.
- Detaljnije u arhitektonskim odlukama: [`wiki/decisions/ADR-0005-enterprise-dark-mode-cockpit-redesign.md`](wiki/decisions/ADR-0005-enterprise-dark-mode-cockpit-redesign.md), [`wiki/decisions/ADR-0006-stitch-design-application-and-no-mock-roadmap.md`](wiki/decisions/ADR-0006-stitch-design-application-and-no-mock-roadmap.md) i [`wiki/decisions/ADR-0007-telemetry-screen-mirror-and-oem-expansion.md`](wiki/decisions/ADR-0007-telemetry-screen-mirror-and-oem-expansion.md).

---

## 🔍 Mrežni UDP Auto-Discovery (Port 8888)

1. Klijent na lokalnoj mreži šalje broadcast paket:
   ```text
   GWADT_DISCOVER client=DESKTOP-ABC1234
   ```
2. ServisHub na portu 8888 automatski odgovara pošiljaocu:
   ```text
   GWADT_HUB_OFFER port=8080 host=<IP_Servera> name=TehnoKutak-Hub
   ```
3. Klijent formira bazni URL `http://<IP_Servera>:8080` i odmah započinje komunikaciju.

---

## 🚀 Pokretanje i Upotreba

### 1. Brzi Start (Lokalno):
```bash
# Pokretanje servera
./start-servis-hub.sh

# Ili direktno preko Python-a
python3 hub_server.py --port 8080 --udp-port 8888
```

### 2. Parametri Komandne Linije:
```bash
python3 hub_server.py --help
# Opcije:
#   --host HOST               IP adresa (default: 0.0.0.0)
#   -p, --port PORT           HTTP port (default: 8080)
#   --udp-port UDP_PORT       UDP Auto-Discovery port (default: 8888)
#   --storage-dir DIR         Putanja do storage foldera
#   --logs-dir DIR            Putanja do foldera sa logovima
#   --payload-dir DIR         Izvorni folder za dinamičko pakovanje OTA arhive
#   --updates-dir DIR         Direktorijum za Windows 11 kumulativna ažuriranja (.msu/.cab)
```

### 3. Konzolni TUI Monitor Flote:
```bash
# Pregled tabele svih mašina u servisu
python3 hub_cli.py

# Uživo praćenje logova određene mašine (tail -f)
python3 hub_cli.py --watch TK-LAPTOP-PF2X98ZA
```

---

## 🐳 TrueNAS SCALE i Docker Produkcija

Na radioničkom TrueNAS SCALE serveru (`192.168.170.254`), ServisHub radi unutar Docker kontejnera sa host mrežnim drajverom:

```yaml
services:
  servis-hub:
    container_name: servis-hub
    build: .
    restart: unless-stopped
    network_mode: host
    environment:
      - PYTHONUNBUFFERED=1
      - SERVIS_HUB_PORT=8080
      - SERVIS_HUB_UDP_PORT=8888
      - SERVIS_UPDATES_DIR=/home/gru/Storage/Updates/Win11
    volumes:
      - /mnt/TK_NAS/Servis/servis-hub/storage:/app/storage:rw
      - /mnt/TK_NAS/Servis/servis-hub/Logs/reports:/app/Logs/reports:rw
      - /mnt/TK_NAS/Servis/servis-hub/Logs/live:/app/Logs/live:rw
      - /mnt/TK_NAS/Servis/Updates/Win11:/home/gru/Storage/Updates/Win11:ro
```

> [!IMPORTANT]
> - **`network_mode: host`** je obavezan kako bi kontejner mogao nesmetano da prima UDP broadcast pakete na portu 8888 iz različitih subnetova radionice.
> - **Port 8080** je striktno alociran jer radionički ERP sistem na TrueNAS-u koristi port 8000.

### 🚀 Standardna Procedura Sinhronizacije i Rebuilda (Sync & Deploy):
```bash
# 1. Sinhronizacija sa dev mašine (bez privremenih fajlova i skladišta)
rsync -av --exclude=".git" --exclude=".venv" --exclude=".pytest_cache" --exclude="__pycache__" --exclude="*.pyc" --exclude="Logs/" --exclude="storage/" "/home/gru/Projects/Tehno-kutak windows 11 automat/ServisHub/" "/home/gru/Storage/servis-hub/"

# 2. Remote SSH Docker Compose rebuild i redeploy na TrueNAS SCALE
ssh truenas "cd /mnt/TK_NAS/Servis/servis-hub && sudo docker compose build --no-cache && sudo docker compose up -d --force-recreate"
```

---

## 🧪 Automatizovani Testovi

Kompletan test suite se pokreće pomoću ugrađenog `unittest` mehanizma:

```bash
python3 -m unittest discover -s tests -v
```

Pokriveni scenariji (214 testova):
- **`test_hwid.py`:** ACPI, HDAudio, PCI i USB normalizacija.
- **`test_driver_versions.py`:** Poređenje semantičkih verzija i formata datuma (ISO, WMI, evropski).
- **`test_payload.py`:** Atomsko kreiranje, SHA-256 heširanje i verifikacija metapodataka.
- **`test_reports.py`:** Markdown i JSON generisanje servisnih kartona.
- **`test_udp_discovery.py`:** UDP discovery protokol na mreži.
- **`test_api_integration.py`:** Živi integracioni testovi za sve REST rute.
- **`test_updates_api.py`:** Skeniranje skladišta zakrpi, regex ekstrakcija KB brojeva, ignorisanje `.part` fajlova i HTTP/FastAPI integracija.
- **`test_oem_lenovo.py`:** Lenovo fabrička garancija, 5s timeout sa fallbackom, SCCM enterprise paketi i poređenje BIOS verzija.
- **`test_oem_dell.py`:** Dell Service Tag garancija, Dell Command | Deploy CAB paketi i BIOS poređenje.
- **`test_oem_hp.py`:** HP garancija, SoftPaq / Driver Pack (HPIA) paketi i BIOS detekcija.
- **`test_oem_expansion_integration.py`:** E2E integracija modularnog OEM podsistema (Lenovo, Dell, HP) i višenitna bezbednost.
- **`test_screen_mirror.py`:** In-memory 1 Hz JPEG frejm bafer, TTL istek (5s) i REST endpoint-i (`POST/GET`).
- **`test_screen_capture_client.py`:** PowerShell sintaksna i ugovorna validacija klijentskog modula `WIS.ScreenCapture.ps1`.
- **`test_remote_commands.py`:** Integracioni testovi FIFO komandnog reda, heartbeat distribucija i `WIS.Telemetry.psm1` ugovor.
- **`test_heartbeat_telemetry.py`:** Senzorska telemetrija (CPU %, termika, RAM, NVMe S.M.A.R.T.) i očuvanje stanja.
- **`test_wis.py`:** Bezbednosna verifikacija USB diskova, blokiranje sistemskih i ZFS mountova, GPT FAT32+NTFS formatiranje i ISO ekstrakcija.
- **`test_wis_and_oem_api.py`:** Integracioni testovi za `/api/wis/*` rute, SSE strim narezivanja i OEM integraciju u `/api/drivers/resolve`.
- **`test_agy_sync_api.py`:** Pre-flight pull i post-flight push servisnih sesija Antigravity CLI alata.
- **`test_historical_devices.py`:** Perzistencija i automatsko učitavanje istorijskih uređaja iz ranijih JSON izveštaja i AGY sesija.
- **`test_dashboard_ui.py`:** Validacija HTML strukture Modern Enterprise Dark Mode Cockpita, collapsible sidebara, navigacionih sekcija i modala.
- **`test_tui_cli.py`:** Testovi TUI terminal monitora flote (`hub_cli.py`).

---

## 📚 Dokumentacija i Baza Znanja

- **📖 Master Tehnička Dokumentacija:** [`docs/TEHNICKA_DOKUMENTACIJA.md`](docs/TEHNICKA_DOKUMENTACIJA.md) — Sveobuhvatni arhitektonski i inženjerski priručnik (v3.1.0).
- **🗺️ Inženjerska Mapa Puta & No-Mock Specifikacija:** [`docs/BACKEND_IMPLEMENTATION_ROADMAP.md`](docs/BACKEND_IMPLEMENTATION_ROADMAP.md) — Arhitektonski plan implementacije 4 ključna sistema (Screen Mirror, NVMe senzorika, UEFI Reboot, OEM API).
- **🎨 Google Stitch Dizajn Sistem:** [`DESIGN.md`](DESIGN.md) i sačuvani aseti u [`docs/stitch_designs/`](docs/stitch_designs/).
- **Obsidian Wiki Baza Znanja (`wiki/`):**
  - **Pregled Arhitekture:** `wiki/architecture/overview.md`
  - **Tehnička Specifikacija:** `wiki/architecture/servis-hub.md`
  - **Arhitektonske Odluke:**
    - `wiki/decisions/ADR-0001-decoupling-servis-hub.md` — Odluka o dekaplovanju ServisHub-a
    - `wiki/decisions/ADR-0002-windows-update-cache-and-status-api.md` — Windows Update keš i status API
    - `wiki/decisions/ADR-0003-antigravity-serviser-sync-api.md` — Integracija Antigravity CLI servisera
    - `wiki/decisions/ADR-0004-oem-and-wis-tools.md` — Modularni OEM podsistem i WIS USB/ISO alati
    - `wiki/decisions/ADR-0005-enterprise-dark-mode-cockpit-redesign.md` — Modern Enterprise Dark Mode Cockpit redizajn (Stitch, Sidebar, 1 Hz ogledalo)
    - `wiki/decisions/ADR-0006-stitch-design-application-and-no-mock-roadmap.md` — Primena Stitch dizajna, Striktna No-Mock politika i mapa puta za klijentske sisteme
    - `wiki/decisions/ADR-0007-telemetry-screen-mirror-and-oem-expansion.md` — Proširenje telemetrije, 1 Hz Screen Mirror, daljinsko upravljanje i OEM ekspanzija za Dell i HP (v3.2.0)
  - **Rečnik Pojmova:** `wiki/domain/glossary.md`
  - **Evidencija Zamki:** `wiki/memory/gotchas.md`
  - **Operativni Runbook:** `wiki/memory/ops-runbook.md`
  - **Glavni Indeks (MOC):** `wiki/index.md`

`servishub.__version__` koristi generisani `VERSION=3.2.0`. Arhivski samostalni Hub ima potvrđen tag `v3.1.0`; 3.2.0 je trenutno slobodna razvojna oznaka, bez tvrdnje da je izdanje objavljeno.
