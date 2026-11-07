---
title: "ADR-0001: Inicijalna arhitektura i petofazni automatizacioni pajplajn"
type: decision
status: accepted
created: 2026-09-08
updated: 2026-09-14
tags:
  - adr
  - decision
  - architecture
  - pipeline
---

# ADR-0001: Inicijalna arhitektura i petofazni automatizacioni pajplajn

## Kontekst i problem
Proces instalacije Windows 11 operativnog sistema u računarskom servisu obično zahteva 1-2 sata manuelnog rada servisera po mašini: odabir jezika i naloga, skidanje drajvera za zvuk i grafiku, instalaciju aplikacija, podešavanje tastatura i aktivaciju licenci. Cilj projekta je eliminisanje manuelnog rada i dovođenje sistema na radnu površinu spremnu za klijenta uz samo jedan klik servisera.

## Razmatrane opcije
- **Opcija 1: Korišćenje generičkih softvera za kloniranje diska (Clonezilla / Acronis).**
  - *Mane*: Klonirana slika nije prilagođena različitim modelima matičnih ploča i procesora (različiti HAL/čipset drajveri često izazivaju plave ekrane BSOD).
- **Opcija 2: Nativni Windows Setup Answer File (`autounattend.xml`) u kombinaciji sa centralnim PowerShell orkestratorom kroz 5 faza.**
  - *Prednosti*: Koristi zvanične Microsoft mehanizme, garantuje hardversku kompatibilnost na svakom uređaju, omogućava dinamičko skidanje WHQL drajvera i namensko brendiranje.

## Odluka
Izabrana je **Opcija 2: Nativni Windows Setup (`autounattend.xml`) sa PowerShell orkestratorom u 5 faza**:
- **Faza 1:** Povezivanje na servisnu mrežu (Wi-Fi ili Ethernet).
- **Faza 2:** Autonomno ažuriranje drajvera preko Windows Update-a (do 3 kruga sa restartima).
- **Faza 3:** Sistemske optimizacije, gašenje telemetrije i OEM brendiranje korisničkog naloga.
- **Faza 4:** Tiha instalacija osnovnog softverskog paketa (Office, Acrobat, Chrome, BatteryBar, DotNet) i aktivacija licenci.
- **Faza 5:** Čišćenje privremenih fajlova i završni restart.

### Posledice
- **Pozitivne**:
  - Rad servisera je sveden na odabir particije za instalaciju.
  - Sistem automatski prilagođava drajvere i brend konkretnom računaru.
  - Visoka stabilnost bez potrebe za održavanjem desetina različitih sistemskih slika.
- **Negativne / Zahtevi**:
  - Neophodno je očuvati striktnu kompatibilnost sa Windows PowerShell 5.1 okruženjem.
  - Potrebno je upravljati stanjem kroz restarte sistema (`deploy-state.json`).

## Povezane beleške
- Glavni Indeks: [[index]]
- Pregled Arhitekture: [[architecture/overview]]
- Pajplajn Izvršavanja: [[architecture/pipeline]]
