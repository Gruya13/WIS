---
title: "Centralizovani Servisni Hub (ServiceHub v3.2.0) - Tehnička i Arhitektonska Specifikacija"
type: architecture
created: 2026-10-08
updated: 2026-10-26
tags:
  - architecture
  - servis-hub
  - telemetry
  - auto-discovery
  - payload
  - driver-proxy
  - sse
  - screen-mirror
---

# ⚡ WIS Service Centralni Servisni Hub (`ServiceHub/` v3.2.0)
## Sveobuhvatna Tehnička i Arhitektonska Specifikacija u WIS Monorepo-u (v2.17.0)

---

## 1. Pregled i Svrha Podsistema

**ServiceHub** (komponentna verzija **3.2.0** u okviru WIS paketa **2.17.0**, smeštena u kanonskom direktorijumu `/home/gru/Projects/WIS/ServiceHub/`) je centralni kontrolni server, telemetrijski agregator, verifikator potpisanih OTA izdanja, pametni proksi za OEM drajvere i upravljač butabilnih USB medijuma za servis računara **WIS Service**.

### Ključne Funkcionalnosti (v3.2.0):
1. **Imutabilna Kriptografski Potpisana Izdanja (`servishub/releases.py` & `payload.py`):** Služi isključivo RSA-2048 PKCS#1 v1.5 potpisana i SHA-256 verifikovana `wis-payload.zip` izdanja (`releases/<release_id>/` i atomarni `current.json`). Ne vrši nesigurno automatsko prepakivanje pri HTTP GET zahtevu.
2. **1 Hz Live Ogledalo Ekrana u RAM-u (`Screen Mirror`):** Prima JPEG frejmove sa klijentskih radnih stanica (`POST /api/devices/{id}/screen-frame`), čuva isključivo poslednji frejm u radnoj memoriji sa **5s TTL** istekom (0 Disk I/O) i prikazuje ih uživo u Web Cockpit-u.
3. **Dvosmerno Daljinsko Upravljanje (`Remote Commands`):** Održava FIFO red komandi po uređaju (`POST /api/devices/{id}/command` — npr. `reboot_uefi`, `reboot_pxe`, `cancel`, `abort`) koje klijenti atomski preuzimaju kroz odgovor na periodični 2s `/api/heartbeat` puls.
4. **Trozubac Modularnog OEM Podsistema (Lenovo, Dell, HP) i Microsoft Catalog Scraper:** Automatska provera fabričke garancije (5s timeout sa fallbackom), rezolucija fabričkih enterprise paketa drajvera (Lenovo SCCM, Dell Command | Deploy CAB, HP SoftPaq), provera novijih BIOS verzija i kaskadna normalizacija ACPI/HDAudio/PCI/USB HWID identifikatora.
5. **WIS Centar (UEFI USB Builder sa Safety Guardrails):** Priprema butabilnih USB fleševa (GPT FAT32 `UEFI_BOOT` + NTFS `WIS_DATA`) uz višeslojne blokade formatiranja sistemskih particija i TrueNAS ZFS pool diskova.
6. **6-Fazni Statusni Model i Digitalni Servisni Kartoni:** Čuva uređaje u `ACTIVE` stanju tokom svih 6 faza dok ne stigne završni izveštaj sa `handover_ready = true`, i trajno arhivira JSON i Markdown servisne kartone (`Logs/reports/`) i Antigravity AI sesije.

---

## 2. Modularna Arhitektura Paketa (`ServiceHub/servishub/`)

Kompletan serverski kod napisan je u čistom Python 3 standardnom okruženju (**Zero External Dependencies**, **32 Python modula**, **12,112 linija izvornog koda**) uz pokrivenost od **259 automatizovanih jediničnih i integracionih testova** (`ServiceHub/tests/`, 6,575 linija):

```text
ServiceHub/
├── servishub/                     # Glavni modularni Python paket (12,112 linija)
│   ├── __init__.py & version.py   # Kanonska verzija (3.2.0)
│   ├── config.py                  # Upravljanje putanjama i okruženjem
│   ├── server.py                  # ThreadingHTTPServer (:8080) i signal handler
│   ├── handlers.py                # REST API ruter, XSS zaštita i SSE striming (1,480 linija)
│   ├── discovery.py               # UDP Auto-Discovery responder na portu :8888
│   ├── telemetry.py               # Thread-safe FleetStore, sesije, FIFO komande i 1Hz RAM frejm bafer (930 linija)
│   ├── releases.py & payload.py   # RSA-2048 PKCS#1 v1.5 verifikacija manifesta i serviranje wis-payload.zip
│   ├── reports.py                 # Generator JSON/Markdown servisnih kartona i AGY sesija (632 linije)
│   ├── cli.py & tui_cli.py        # CLI argument parser i terminalski TUI monitor flote (588 linija)
│   ├── api/updates.py             # Skener TrueNAS skladišta Windows 11 kumulativnih zakrpi (.msu/.cab)
│   ├── models/updates.py          # Modeli podataka za inventar zakrpi
│   ├── drivers/                   # Pametni podsistem za drajvere i OEM trijažu
│   │   ├── hwid.py                # Kaskadna normalizacija ACPI, HDAudio (&COLxx), PCI i USB HWID-ja
│   │   ├── version.py             # Semantičko i datumsko poređenje verzija drajvera
│   │   ├── scraper.py             # Microsoft Update Catalog scraper
│   │   ├── cache.py               # Lokalni keš .cab paketa na TrueNAS-u
│   │   └── oem/                   # Lenovo (575 lin.), Dell (740 lin.), HP (758 lin.) i BaseOEMProvider
│   ├── wis/                       # WisIsoManager (iso.py) i WisUsbEngine sa ZFS/System zaštitom (usb.py)
│   └── web/dashboard.py           # Modern Enterprise Dark Mode Cockpit (3,682 linija, Vanilla HTML/CSS/JS)
├── tests/                         # 29 testnih modula — 259/259 PASS bez preskakanja
├── hub_server.py & hub_cli.py     # Glavni pokretački skriptovi
└── docker/                        # Dockerfile i docker-compose.yml za TrueNAS SCALE
```

*(Napomena: `ISO/Skripte/ServisHub/hub_server.py` je tanak kompatibilni wrapper koji delegira izvršavanje kanonskom serveru u `ServiceHub/`).*

---

## 3. Mrežna Konfiguracija i TrueNAS SCALE Produkcija

- **TrueNAS Server IP:** `192.168.170.254` (`truenas.servis`)
- **HTTP REST & SSE Port:** `8080` (strogo odvojen od internog ERP sistema servisa na portu `8000`)
- **UDP Auto-Discovery Port:** `8888` (odgovara na `GWADT_DISCOVER` / `WIS_DISCOVER` pakete sa `GWADT_HUB_OFFER port=8080 host=192.168.170.254 name=WISService-Hub`)
- **Docker Mrežni Režim:** `network_mode: host` (obavezan za prijem UDP broadcast paketa sa svih radioničkih podmreža).

---

## 4. Pregled Ključnih REST API Ruta

| Endpoint | Metod | Opis |
| :--- | :--- | :--- |
| `/` | `GET` | Modern Enterprise Dark Mode Web Cockpit (Flota, 1Hz Screen Mirror, WIS Centar, Zakrpe) |
| `/api/health`, `/api/status` | `GET` | Zdravlje servera, uptime i verzija (`3.2.0`) |
| `/api/payload/version` | `GET` | Metapodaci i RSA-2048 potpis aktivnog `wis-payload.zip` izdanja |
| `/api/payload/download` | `GET` | Binarni stream verifikovanog `wis-payload.zip` paketa |
| `/api/heartbeat` | `POST` | Prijem 2s pulsa i senzorike (CPU %, °C, RAM, NVMe SMART) + isporuka `pending_commands` |
| `/api/devices` | `GET` | Spisak svih radnih stanica u floti sa statusom faza (1..6), senzorima i `has_screen_stream` |
| `/api/devices/<id>/screen-frame` | `POST` | Prijem binarnog 1 Hz JPEG frejma ekrana sa klijenta u RAM bafer |
| `/api/devices/<id>/screen-frame.jpg` | `GET` | Dohvatanje svežeg JPEG frejma ekrana (max 5s TTL, ili HTTP 404 za Standby prikaz) |
| `/api/devices/<id>/command` | `POST` | Zadavanje daljinske komande klijentskoj radnoj stanici (`reboot_uefi`, `reboot_pxe`, `cancel`) |
| `/api/devices/<id>/report` | `POST`/`GET` | Arhiviranje i čitanje završnog DMTF servisnog kartona (`.json` i `.md`) |
| `/api/devices/by-serial/<sn>/history` | `GET` | Istorijat ranijih servisnih intervencija po serijskom broju uređaja |
| `/api/drivers/resolve` | `POST` | Rezolucija drajvera (MS Catalog + keš) + OEM garancija i fabrički paket (Lenovo, Dell, HP) |
| `/api/updates/status` | `GET` | Inventar keširanih Windows 11 kumulativnih zakrpi (`.msu`/`.cab`) na NAS-u |
| `/api/wis/iso/info`, `/api/wis/usb/*` | `GET`/`POST` | Uvid u WIS ISO sliku, sigurna lista USB diskova i narezivanje sa SSE progresom |

---

## 🔗 Povezane Beleške
- Pregled Arhitekture: [[architecture/overview]]
- Integracija ServiceHub v3.2.0: [[decisions/ADR-0135-servis-hub-v32-screen-mirror-and-telemetry-integration]]
- Potpisana Izdanja i Kanonski Monorepo: [[decisions/ADR-0141-canonical-signed-release-and-status-contract]]
- Ops Runbook: [[memory/ops-runbook]]
