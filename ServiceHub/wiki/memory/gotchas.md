---
title: "Lokalni Registar Zamki i Rešenja (Project Gotchas)"
type: memory
created: 2026-10-08
updated: 2026-10-08
tags:
  - memory
  - gotchas
  - troubleshooting
  - servishub
---

# Lokalni Registar Zamki i Rešenja (Project Gotchas)

Ovaj dokument beleži specifične probleme, zamke i proverena rešenja u razvoju i operativnom radu **ServisHub** sistema.

---

## 🛠️ Mreža i Portovi

### 1. Konflikt Portova na TrueNAS SCALE Serveru (Port 8000 vs 8080)
- **Simptom**: ServisHub ne može da se pokrene na TrueNAS SCALE ili obara interni servis (`servis-server-erp`).
- **Uzrok**: Radionički ERP sistem je podrazumevano vezan za port `8000`.
- **Rešenje**: ServisHub **mora striktno koristiti port 8080** za HTTP komunikaciju. Klijentski moduli (`WIS.Telemetry.psm1`, C# Launcher) su konfigurisani da primarno gađaju port 8080.

### 2. Prijem UDP Broadcasta u Docker Kontejneru
- **Simptom**: Klijenti ne mogu da otkriju server preko `GWADT_DISCOVER` paketa (port 8888).
- **Uzrok**: Dockerov podrazumevani bridge mrežni drajver vrši NAT i blokira broadcast pakete iz drugih VLAN-ova i subnetova.
- **Rešenje**: U `docker-compose.yml` obavezno konfigurisati `network_mode: host`.

---

## 💻 Drajveri i Microsoft Update Catalog

### 3. ACPI i HDAudio HWID Normalizacija
- **Simptom**: Microsoft Update Catalog vraća praznu tabelu za identifikator koji Windows Device Manager prijavljuje.
- **Uzrok**: Microsoft katalog ne indeksira sabirničke prefikse poput `&COL01`, `&REV_xxxx` ili `&SUBSYS_xxxxxxxx`, niti nestandardne ACPI oblike.
- **Rešenje**: Generator kandidata (`servishub.drivers.hwid.generate_hwid_candidates`) automatski generiše listu kandidata od najspecifičnijeg ka najopštijem (`ACPI\VEN_INTC&DEV_1078` -> `ACPI\INTC1078` -> `*INTC1078` -> `INTC1078`), čime je procenat uspešnog pronalaska podignut na preko 95%.

### 4. Filtriranje Arhitekture Drajvera (ARM64 / x86 Eliminacija)
- **Simptom**: Klijentski Windows 11 x64 računar prijavi grešku pri instalaciji preuzetog drajvera.
- **Uzrok**: Microsoft katalog za isti HWID često nudi i ARM64 ili stare x86 pakete.
- **Rešenje**: Metoda `filter_for_win_x64` u `servishub.drivers.scraper` striktno filtrira rezultate i prihvata isključivo pakete namenjene Windows 10/11 x64 arhitekturi.

---

## 📦 Payload i Keširanje

### 5. Regeneracija OTA Paketa i mtime
- **Simptom**: Klijent preuzima stari payload paket iako su izmenjene skripte.
- **Uzrok**: Timestamp izmene izvornih fajlova nije bio noviji od zabeleženog u `payload_meta.json`.
- **Rešenje**: `PayloadManager.ensure_payload(force=True)` omogućava forsirano prepakivanje paketa, a brisanje `storage/cache/payload_meta.json` tera server na trenutnu rekompresiju.

---

## 📚 Povezane Beleške
- Glavni indeks: [[index]]
- Operativni runbook: [[memory/ops-runbook]]
- Arhitektura sistema: [[architecture/servis-hub]]
