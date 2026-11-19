---
title: "Centralizovani Servisni Hub (ServiceHub v3.3.0) - Tehnička i Arhitektonska Specifikacija"
type: architecture
created: 2026-10-08
updated: 2026-11-18
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

# ⚡ WIS Service Centralni Servisni Hub (`ServiceHub/` v3.3.0)
## Sveobuhvatna Tehnička i Arhitektonska Specifikacija u WIS Monorepo-u (v3.0.0)

---

## 1. Pregled i Svrha Podsistema

**ServiceHub** (komponentna verzija **3.3.0** u okviru WIS paketa **3.0.0**, smeštena u kanonskom direktorijumu `/home/gru/Projects/WIS/ServiceHub/`) je centralni kontrolni server, telemetrijski agregator, verifikator potpisanih OTA izdanja, dvosmerni HTTP keš za zakrpe i drajvere, analitički **WIS Benchmark** agregator, **Predictive Cache** (`Model DNA`) pre-arrival motor, registar profila modela za `WIS.HwTest` i upravljač butabilnih USB medijuma za servis računara **Tehno kutak**.

### Ključne Funkcionalnosti (v3.3.0):
1. **Imutabilna Kriptografski Potpisana Izdanja (`servishub/releases.py` & `payload.py`):** Služi isključivo RSA-2048 PKCS#1 v1.5 potpisana i SHA-256 verifikovana `wis-payload.zip` izdanja (`releases/<release_id>/` i atomarni `current.json`). Ne vrši nesigurno automatsko prepakivanje pri HTTP GET zahtevu.
2. **WIS Benchmark & Analitika Flote (`servishub/benchmark.py`):** Prikuplja i agregira trajanja faza (Faze 1–6), LAN/WAN protok u Mbps, `Cache Hit Ratio` / `Byte Hit Ratio`, `First-Pass Success Rate`, P50/P90 percentila, automatski spaja parcijalne `report_DESKTOP-*` sa `TK-*` kartonima (`_merge_report_records` uz očuvanje `hwtest`), usklađuje `recent_runs.status = "NEEDS_ATTENTION"` kada `success == False`, i kanonikalizuje MTM modele (`_resolve_canonical_model_label`).
3. **Predictive Cache & Model DNA (`servishub/predictive_cache.py` & `model_profiles.py`):** Omogućava pre-arrival zagrevanje garancije, hardverskog BOM-a, `.cab`/`.tar` drajvera (uz dvosmernu sinhronizaciju `model_aliases` MTM ⬌ marketinški naziv) i Win11 zakrpi sa računanjem `Readiness Score` (`0–100%`), kao i prioritetni lookup `<Model>.tar` paketa preko `/app/harvested/Archives` uz blokadu generičkih modela (`GenericPC`).
4. **1 Hz Live Ogledalo Ekrana u RAM-u (`Screen Mirror`):** Prima JPEG frejmove sa klijentskih radnih stanica (`POST /api/devices/{id}/screen-frame`), čuva isključivo poslednji frejm u radnoj memoriji sa **5s TTL** istekom (0 Disk I/O) i prikazuje ih uživo u Web Cockpit-u.
5. **Dvosmerno Daljinskog Upravljanje (`Remote Commands`):** Održava FIFO red komandi po uređaju (`POST /api/devices/{id}/command` — npr. `reboot_uefi`, `reboot_pxe`, `skip_step`, `cancel`, `abort`) koje klijenti atomski preuzimaju kroz odgovor na periodični 2s `/api/heartbeat` puls (i u UI zapisuju u `pending-hub-commands.json` + `.skip`).
6. **Trozubac Modularnog OEM Podsistema (Lenovo, Dell, HP) i Microsoft Catalog Scraper:** Automatska provera fabričke garancije (5s timeout sa fallbackom), rezolucija fabričkih enterprise paketa drajvera (Lenovo SCCM, Dell Command | Deploy CAB, HP SoftPaq), provera novijih BIOS verzija i kaskadna normalizacija ACPI/HDAudio/PCI/USB HWID identifikatora.
7. **WIS Centar (UEFI USB Builder sa Safety Guardrails):** Priprema butabilnih USB fleševa (GPT FAT32 `UEFI_BOOT` + NTFS `WIS_DATA`) uz višeslojne blokade formatiranja sistemskih particija i TrueNAS ZFS pool diskova.
8. **6-Fazni Statusni Model, Konsolidacija Identiteta i Digitalni Servisni Kartoni:** Čuva uređaje u `ACTIVE` stanju tokom svih 6 faza dok ne stigne završni izveštaj, trajno čuva `hw`/`hwtest`/`benchmark` kroz restart (`enrich_report_payload`), normalizuje kineski `apps_summary` (`_normalize_apps_summary`), automatski briše privremene pred-OEM `DESKTOP-*`/`APCL-*` duplikate (`cleanup_duplicate_devices_locked`), i arhivira JSON/Markdown servisne kartone (sa sekcijama za WIS Benchmark i `### 📝 Napomene za Servis`).

---

## 2. Modularna Arhitektura Paketa (`ServiceHub/servishub/`)

Kompletan serverski kod napisan je u čistom Python 3 standardnom okruženju (**Zero External Dependencies**, **37 Python modula**, **17,114 linija izvornog koda**, `733,852 B`) uz pokrivenost od **290 unittest metoda** u **31 test modulu** (`ServiceHub/tests/`, `33` `.py` fajla, `8,327` linija — u baznom Linux Python okruženju bez opcionih paketa `Pillow`/`FastAPI`/`Node`: **270 PASS, 20 skipped**):

```text
ServiceHub/
├── servishub/                     # Glavni modularni Python paket (37 modula, 17,114 linija)
│   ├── __init__.py & version.py   # Kanonska verzija (3.3.0)
│   ├── benchmark.py               # WIS Benchmark agregacija, P50/P90, merge hwtest, status NEEDS_ATTENTION (584 lin.)
│   ├── predictive_cache.py        # Pre-Arrival Pre-Warming motor i Readiness Score (313 linija)
│   ├── model_profiles.py          # Perzistencija profila modela (tastatura/USB/Model DNA & model_aliases, 353 lin.)
│   ├── keyboard_resolver.py       # Rezolucija 12 OEM šablona tastatura
│   ├── hardware_spec.py           # Keš i lookup hardverskih specifikacija modela
│   ├── config.py                  # Upravljanje putanjama i /app/harvested/Archives prioritetom (122 lin.)
│   ├── server.py                  # ThreadingHTTPServer (:8080) i signal handler (144 linije)
│   ├── handlers.py                # REST API ruter, XSS zaštita, SSE striming i cleanup duplikata (1,993 lin.)
│   ├── discovery.py               # UDP Auto-Discovery responder na portu :8888 (48 linija)
│   ├── telemetry.py               # Thread-safe FleetStore, enrich_report_payload, cleanup_duplicate_devices_locked (1,249 lin.)
│   ├── releases.py & payload.py   # RSA-2048 PKCS#1 v1.5 verifikacija manifesta i serviranje wis-payload.zip
│   ├── reports.py                 # Generator JSON/Markdown servisnih kartona (Benchmark + Napomene za Servis) (721 lin.)
│   ├── cli.py & tui_cli.py        # CLI argument parser i terminalski TUI monitor flote (503 linije TUI)
│   ├── api/updates.py             # Skener TrueNAS zakrpi (filter *aggregatedmetadata*) & <Model>.tar keš (1,175 lin.)
│   ├── models/updates.py          # Modeli podataka za inventar zakrpi
│   ├── drivers/                   # Pametni podsistem za drajvere i OEM trijažu
│   │   ├── hwid.py                # Kaskadna normalizacija ACPI, HDAudio (&COLxx), PCI i USB HWID-ja
│   │   ├── version.py             # Semantičko i datumsko poređenje verzija drajvera
│   │   ├── scraper.py             # Microsoft Update Catalog scraper
│   │   ├── cache.py               # Lokalni keš .cab paketa na TrueNAS-u
│   │   └── oem/                   # Lenovo (833 lin.), Dell (954 lin.), HP (1,056 lin.) i BaseOEMProvider
│   ├── wis/                       # WisIsoManager (iso.py 365 lin.) i WisUsbEngine (usb.py 512 lin.)
│   └── web/dashboard.py           # Modern Enterprise Dark Mode Cockpit (3,952 linije, Benchmark & Predictive paneli)
├── tests/                         # 31 testni modul (33 fajla) — 290 metoda (270 PASS, 20 skipped bez Pillow/FastAPI/Node)
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
| `/api/health`, `/api/status` | `GET` | Zdravlje servera, uptime i verzija (`3.3.0`) |
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
| `/api/updates/status`, `/api/updates/download` | `GET` | Inventar i preuzimanje keširanih Windows 11 `.msu`/`.cab` zakrpi (`OSBuild` + `index.json`) |
| `/api/updates/upload`, `/api/updates/cache-kb`, `/api/updates/prune` | `POST` | Dvosmerno otpremanje novih zakrpa u hub keš, indeksiranje KB i čišćenje zastarelih paketa |
| `/api/drivers/model-pack/status`, `/api/drivers/model-pack/download` | `GET` | Provera i preuzimanje `<Model>.tar` paketa drajvera preko HTTP keša |
| `/api/drivers/model-pack/upload` | `POST` | Otpremanje ubranog `<Model>.tar` paketa nazad u `ServiceHub` keš (sa `:ro` fallback-om) |
| `/api/model-profile`, `/api/model-profiles` | `GET` | Čitanje snimljenog profila modela ili cele liste naučenih `Model DNA` profila |
| `/api/model-profile/keyboard\|usb\|dna` | `POST` | Ažuriranje profila tastature / USB portova / HWID otiska sa `model_aliases` parovima |
| `/api/keyboard-layout` | `GET` | Određivanje odgovarajućeg OEM šablona tastature za model (`keyboard_resolver.py`) |
| `/api/hardware-spec` | `GET` | Lookup fabričke specifikacije hardvera modela (`hardware_spec.py`) |
| `/api/benchmark/summary`, `/api/benchmark/stats` | `GET` | Zbirni `WIS Benchmark` izveštaj flote (trajanja faza, LAN/WAN protok, Cache Hit Ratio, P50/P90, po modelima) |
| `/api/predictive/prewarm`, `/api/predictive/status` | `POST`/`GET` | Pre-Arrival zagrevanje keša (garancija, BOM, `.cab`/`.tar`, Win11 LCU) i status spremnosti (`0–100%`) |
| `/api/wis/iso/info`, `/api/wis/usb/*` | `GET`/`POST` | Uvid u WIS ISO sliku, sigurna lista USB diskova i narezivanje sa SSE progresom |

---

## 🔗 Povezane Beleške
- Pregled Arhitekture: [[architecture/overview]]
- Integracija ServiceHub v3.2.0: [[decisions/ADR-0135-servis-hub-v32-screen-mirror-and-telemetry-integration]]
- Proširenje profila modela (`WIS.HwTest`): [[decisions/ADR-0189-manual-hardware-test-suite]]
- Dvosmerni WU/driver keš i `ServiceHub v3.3.0`: [[decisions/ADR-0225-wis-v300-embedded-hwtest-bidirectional-wu-and-preoobe-autopilot]]
- `WIS Benchmark`, `Predictive Cache` & Post-Telemetry Hardening: [[decisions/ADR-0236-wis-benchmark-predictive-cache-and-post-telemetry-hardening]]
- Potpisana Izdanja i Kanonski Monorepo: [[decisions/ADR-0141-canonical-signed-release-and-status-contract]]
- Ops Runbook: [[memory/ops-runbook]]
