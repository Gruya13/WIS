---
title: "ADR-0001: Osamostaljivanje i Dekaplovanje ServisHub-a iz WIS Monolita"
type: decision
status: accepted
created: 2026-10-08
updated: 2026-10-08
tags:
  - adr
  - decision
  - architecture
  - decoupling
  - servis-hub
  - standalone
---

# ADR-0001: Osamostaljivanje i Dekaplovanje ServisHub-a iz WIS Monolita

## 1. Kontekst i Problem

U ranijim iteracijama sistema automatizacije servisa "WIS Service", **ServisHub** je nastao kao interna pomoćna skripta unutar repozitorijuma `WIS Service windows 11 automat` (`Skripte/ServisHub/hub_server.py`). 

Vremenom je ServisHub prerastao u ključnu mrežnu okosnicu servisa računara sa širokim spektrom odgovornosti:
- UDP Auto-Discovery responder na portu 8888 za automatsko otkrivanje servera na lokalnoj mreži.
- Pametni proksi i keš za drajvere sa integracijom prema Microsoft Update Catalog-u i WHQL CDN-u.
- Live Server-Sent Events (SSE) telemetrijsko ogledalo GUI sesija i live streaming logova radnika (`tail -f`).
- Dvotačkasti životni ciklus sa ranom detekcijom Intune/Autopilot zaključanog hardvera i trajnom arhivom digitalnih servisnih kartona (JSON + Markdown).
- Dinamički OTA Payload Auto-Packager (`PayloadManager`).

### Razlozi za Odvajanje (Pain Points):
1. **Rigidna Sprega (Tight Coupling):**
   - Server je imao čvrsto vezane putanje ka instalacionom root-u (`../../Skripte/`, `Install-ServisApps.ps1`, `WIS.UI.exe`).
   - Server nije mogao da se razvija, verzira i testira nezavisno od verzija Windows instalacionih skripti.
2. **Kontejnerizacija i Deployment Ograničenja:**
   - Na TrueNAS SCALE serveru radionice, ServisHub se izvršava kao Docker kontejner koji ne bi trebalo da zavisi od celog git stabla Windows automatizacije.
3. **Monolitni Tehnički Dug:**
   - `hub_server.py` je narastao na skoro 3.000 linija u jednom fajlu, gde su bili pomešani HTML/CSS/JS web dashboard, scrapovanje Microsoft kataloga, in-memory skladište, UDP mrežni kod i HTTP ruter.
4. **Potreba za Širim Ekosistemom:**
   - ServisHub treba da postane univerzalna servisna platforma koja može opsluživati više različitih alata u radionici (ne samo WIS, već i dijagnostičke Linux USB fleševe, eksterne skripte, web administratore i ERP).

---

## 2. Razmatrane Opcije

- **Opcija A: Zadržavanje ServisHub-a u sklopu WIS repozitorijuma:**
  - *Nedostaci:* Nastavak rasta monolitnog koda, komplikovano verzionisanje, nemogućnost nezavisnog CI/CD pipeline-a.
- **Opcija B: Potpuna separacija u nezavisni repozitorijum sa modularnim Python paketom [PRIHVAĆENO]:**
  - Izdvajanje ServisHub-a u namenski repozitorijum `/home/gru/Projects/Servis Hub`.
  - Refaktorisanje u čist Python paket `servishub/` sa jasnom separacijom briga.
  - Očuvanje 100% ugovora na REST i UDP nivou radi unazadne kompatibilnosti sa postojećim klijentima.

---

## 3. Doneta Odluka

ServisHub se u potpunosti izdvaja u samostalni projekat sa sledećim arhitektonskim principima:

1. **Čist Standard Library Backend:**
   - Zadržavanje principa nula eksternih pip zavisnosti (`http.server.ThreadingHTTPServer`, `socket`, `threading`, `urllib.request`, `json`, `zipfile`).
2. **Konfigurabilni Payload Manager:**
   - Omogućavanje definisanja izvornog direktorijuma za payload preko konfiguracije/okruženja, bez hardkodovane zavisnosti od WIS skripti.
3. **Modularna Struktura Paketa:**
   - Razdvajanje logike na `servishub.drivers` (scraper, cache, hwid, version), `servishub.telemetry`, `servishub.sse`, `servishub.payload`, `servishub.reports` i `servishub.web`.
4. **Samostalni Docker i Runbook:**
   - Nezavisni `Dockerfile` i `docker-compose.yml` za TrueNAS SCALE okruženje na portu 8080 (`network_mode: host`).
5. **Dvoslojni Protokol Memorije:**
   - Održavanje nezavisnog Obsidian Wiki-ja (`wiki/`) u korenu projekta.

---

## 4. Posledice

### Pozitivne:
- Nezavisan životni ciklus i agilniji razvoj novih mogućnosti.
- Izuzetno visoka testabilnost (brzi unit i integracioni testovi).
- Čista i pregledna baza koda, pristupačna za održavanje.
- Postojeći klijenti (PowerShell 5.1 i C# ekosistem) ne zahtevaju nikakve izmene.

### Zahtevi:
- Održavanje API kompatibilnosti sa klijentima na terenu.
- Praćenje TrueNAS konfiguracije i volumena za skladište.

---

## 📚 Povezane Beleške
- Pregled arhitekture: [[architecture/overview]]
- Tehnička specifikacija: [[architecture/servis-hub]]
- Početni indeks: [[index]]
