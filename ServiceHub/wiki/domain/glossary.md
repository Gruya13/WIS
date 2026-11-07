---
title: "Rečnik Domena (Domain Glossary)"
type: domain
created: 2026-10-08
updated: 2026-10-08
tags:
  - domain
  - glossary
  - terminology
---

# Rečnik Domena (Domain Glossary)

Ovaj dokument definiše ključne termine i koncepte vezane za rad **ServisHub** sistema u servisu računara "Tehno-Kutak".

## 📖 Pojmovi i Definicije

### 1. Auto-Discovery (Automatsko Otkrivanje)
- Mrežni mehanizam kojim klijentski računari šalju UDP broadcast upit na port `8888` (`GWADT_DISCOVER`) i bez ikakvog ručnog unosa IP adrese dobijaju ponudu sa tačnom adresom i portom ServisHub-a (`GWADT_HUB_OFFER`).

### 2. HWID (Hardware Identifier)
- Standardizovani identifikator hardverske komponente u Windows operativnom sistemu (npr. `ACPI\VEN_INTC&DEV_1078`, `HDAUDIO\FUNC_01&...`, `PCI\VEN_10EC&DEV_8168`). ServisHub poseduje normalizator koji generiše kandidate od najspecifičnijeg ka najopštijem radi pretrage Microsoft Update kataloga.

### 3. Microsoft Update Catalog & OEM CAB Proxy
- Zvanična Microsoft baza sertifikovanih drajvera. ServisHub funkcioniše kao pametni proksi koji pretražuje katalog, dohvata direktan download link sa Microsoft CDN-a i trajno ga kešira u lokalnom skladištu radionice.

### 4. Dvotačkasti Životni Ciklus (Discrete Two-Point Lifecycle)
- Model telemetrije koji minimizuje opterećenje mreže emitovanjem dva ključna događaja:
  - **`STARTED` (Faza 1):** Rana trijaža računara i detekcija Windows Autopilot / Azure AD zaključavanja.
  - **`COMPLETED` (Faza 5):** Završetak instalacije i slanje DMTF digitalnog servisnog kartona.

### 5. DMTF Servisni Karton (Digital Service Report)
- Strukturirani izveštaj o pripremljenom računaru koji sadrži serijski broj, model, procesor, RAM memoriju, zdravlje SSD diska (S.M.A.R.T.), trajanje instalacije i status instaliranih aplikacija. Generiše se u `.json` i `.md` formatu.

### 6. OTA Payload (Over-The-Air Paket)
- ZIP arhiva (`gwadt-payload.zip`) koja sadrži najnovije skripte, module i izvršne binarne fajlove. Klijent poredi SHA-256 heš sa serverom i po potrebi dinamički preuzima paket preko gigabitne mreže u milisekundama.

### 7. SSE Mirror (Server-Sent Events Ogledalo)
- Jednosmerni HTTP strim podataka na taktu od 1 Hz koji prenosi kompletno stanje korisničkog interfejsa na klijentu u web browser servisera, omogućavajući praćenje "iz fotelje".

### 8. Thin-Client Launcher ("Flash & Release")
- Mala C# izvršna komponenta koja se prenosi na disk klijenta tokom particionisanja, čime se omogućava vađenje USB fleš diska odmah nakon prve faze instalacije.

---

## 📚 Povezane Beleške
- Glavni indeks: [[index]]
- Arhitektura sistema: [[architecture/servis-hub]]
- Odluka o osamostaljivanju: [[decisions/ADR-0001-decoupling-servis-hub]]
