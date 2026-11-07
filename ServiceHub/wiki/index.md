---
title: "Početni Indeks Znanja (Map of Content)"
type: index
created: 2026-10-08
updated: 2026-10-10
tags:
  - moc
  - index
  - wiki
---

# Početni Indeks Znanja: Servis Hub

Dobrodošli u centralnu bazu znanja i dugoročnu memoriju za projekat **Servis Hub** (Tehno-Kutak Centralni Servisni Hub).

---

## 🗺️ Mape Sadržaja (Navigacija)

### 1. 🏛️ Arhitektura Sistema (`architecture/`)
- [[architecture/overview]] — Osnovni ciljevi, slojevi i dijagram toka podataka (v3.2.0).
- [[architecture/servis-hub]] — Sveobuhvatna tehnička specifikacija sistema (v3.1.0 Standalone).
- [Google Stitch Dizajn Katalog (docs/stitch_designs/README.md)](file:///home/gru/Projects/Servis%20Hub/docs/stitch_designs/README.md) — Katalog UI ekrana, DESIGN.md i vodič za rad na Stitch platformi.
- [Inženjerska Mapa Backend Implementacija (docs/BACKEND_IMPLEMENTATION_ROADMAP.md)](file:///home/gru/Projects/Servis%20Hub/docs/BACKEND_IMPLEMENTATION_ROADMAP.md) — Arhitektonski jaz, No-Mock principi i plan novih API ruta.
- [Master Tehnička Dokumentacija (docs/TEHNICKA_DOKUMENTACIJA.md)](file:///home/gru/Projects/Servis%20Hub/docs/TEHNICKA_DOKUMENTACIJA.md) — Celokupna tehnička knjiga projekta.

### 2. 📋 Arhitektonske Odluke (`decisions/`)
- [[decisions/ADR-0001-decoupling-servis-hub]] — Odluka o osamostaljivanju i dekaplovanju ServisHub-a iz WIS sistema.
- [[decisions/ADR-0002-windows-update-cache-and-status-api]] — Centralizovani keš Windows 11 zakrpi i status REST API.
- [[decisions/ADR-0003-antigravity-serviser-sync-api]] — Integracija Antigravity CLI Servisera, Pre-flight istorija i sinhronizacija sesija.
- [[decisions/ADR-0004-oem-and-wis-tools]] — Modularni OEM podsistem (Lenovo, SCCM keš, BIOS) i WIS USB/ISO alati sa SSE praćenjem.
- [[decisions/ADR-0005-enterprise-dark-mode-cockpit-redesign]] — Modern Enterprise Dark Mode Cockpit redizajn (Google Stitch, Linear/Vercel estetika, Collapsible Sidebar, 1 Hz ogledalo ekrana).
- [[decisions/ADR-0006-stitch-design-application-and-no-mock-roadmap]] — Primena Stitch dizajna u dashboardu, Striktna No-Mock politika i inženjerska mapa puta za klijentske sisteme (Screen Mirror, UEFI reboot, NVMe senzorika, OEM API).
- [[decisions/ADR-0007-telemetry-screen-mirror-and-oem-expansion]] — Proširenje telemetrije, 1 Hz Screen Mirror, daljinsko upravljanje i OEM ekspanzija za Dell i HP (v3.2.0).

### 3. 📖 Domen i Rečnik (`domain/`)
- [[domain/glossary]] — Rečnik termina i pojmova u projektu (Auto-discovery, HWID, CAB Proxy, SSE, DMTF, SCCM, WIS...).

### 4. 🧠 Memorija, Zamke i Operacije (`memory/`)
- [[memory/gotchas]] — Evidencija uočenih problema, bagova i rešenja (Port 8000/8080, Docker Host mode, HWID kandidati, ZFS disk zaštita).
- [[memory/ops-runbook]] — Operativni runbook sa komandama za pokretanje, TrueNAS Docker održavanje i dijagnostiku.

### 5. 📑 Šabloni (`templates/`)
- [[templates/template-adr]] — Šablon za novu arhitektonsku odluku.
- [[templates/template-concept]] — Šablon za opis novog koncepta.
- [[templates/template-gotcha]] — Šablon za veći izolovani problem.
- [[templates/template-task]] — Šablon za izveštaj o završenom zadatku.

---

## 🤖 Pravila za Agente
- Pre početka rada konsultovati: [[architecture/overview]], [[architecture/servis-hub]] i [[memory/gotchas]].
- Sve nove tehničke odluke zabeležiti u `decisions/ADR-XXXX-opis.md` i povezati u ovaj indeks.
