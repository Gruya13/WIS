---
title: "Početni Indeks Znanja (Map of Content)"
type: index
created: 2026-10-08
updated: 2026-11-18
tags:
  - moc
  - index
  - wiki
---

# Početni Indeks Znanja: Servis Hub

Dobrodošli u centralnu bazu znanja i dugoročnu memoriju za projekat **Servis Hub** (WIS Service Centralni Servisni Hub).

---

## 🗺️ Mape Sadržaja (Navigacija)

### 1. 🏛️ Arhitektura Sistema (`architecture/`)
- [[architecture/overview]] — Osnovni ciljevi, slojevi i dijagram toka podataka (`v3.3.0` / `WIS v3.0.0`, `WIS Benchmark` & `Predictive Cache`).
- [[architecture/servis-hub]] — Sveobuhvatna tehnička specifikacija sistema (`v3.3.0`).
- [Master Tehnička Dokumentacija ServiceHub-a (docs/TEHNICKA_DOKUMENTACIJA.md)](../../ServiceHub/docs/TEHNICKA_DOKUMENTACIJA.md) — Celokupna tehnička knjiga podsistema (`v3.3.0`, `37` modula, `290` unittest metoda).
- [ADR-0236 (WIS Benchmark, Predictive Cache & Post-Telemetry Hardening)](../../docs/decisions/ADR-0236-wis-benchmark-predictive-cache-and-post-telemetry-hardening.md) — Arhitektonske odluke (`WIS-TASK-232..236`).
- [ADR-0247 (Terensko Praćenje 17 Uređaja & P0–P2 Očvršćavanje)](../../docs/decisions/ADR-0247-field-monitoring-17-devices-and-p0-p2-system-hardening.md) — Očuvanje `hw`/`hwtest`/`benchmark`, `cleanup_duplicate_devices_locked`, `NEEDS_ATTENTION` status i `skip_step` (`WIS-TASK-239..247`).

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
