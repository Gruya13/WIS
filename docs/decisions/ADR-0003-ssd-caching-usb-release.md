---
title: "ADR-0003: Lokalno keširanje paketa na SSD (C:\\setup) i rano oslobađanje USB fleša"
type: decision
status: accepted
created: 2026-09-08
updated: 2026-09-14
tags:
  - adr
  - decision
  - architecture
  - performance
  - caching
---

# ADR-0003: Lokalno keširanje paketa na SSD (`C:\setup`) i rano oslobađanje USB fleša

## Kontekst i problem
U servisu računara gde se istovremeno priprema više mašina, fleš medijum je usko grlo ukoliko mora ostati priključen u računar tokom celokupnog trajanja instalacije ažuriranja i aplikacija (40–60 minuta). Cilj je da se fleš oslobodi što je pre moguće, kako bi jedan USB fleš mogao da opsluži više računara zaredom.

## Razmatrane opcije
- **Opcija 1: Izvršavanje svih instalacija direktno sa USB fleša.**
  - *Mane*: Fleš mora ostati u računaru do samog kraja. Takođe, brzina čitanja sa USB-a je znatno manja u poređenju sa NVMe/SATA SSD diskom.
- **Opcija 2: Rano asinhrono keširanje softverskih paketa na lokalni SSD (`C:\setup`) u toku WinPE i FirstLogon faze.**
  - *Prednosti*: Čim se fajlovi prebace na interni brzi SSD, fizički USB se može bezbedno izvaditi, a instalacija programa teče maksimalnom brzinom internog drajva.

## Odluka
Izabrana je **Opcija 2: Rano keširanje na `C:\setup`**:
- Paketi iz foldera `Programi` se automatski sinhronizuju na `C:\setup\Programi` u prvim minutima rada.
- Čim je transfer završen, sistem prikazuje uočljiv zeleni baner `[OK] USB FLES JE SLOBODAN`, emituje zvučni signal sa zvučnika i omogućava serviseru da odmah izvadi fleš.
- Sve dalje faze (Faza 1 do Faze 4) pokreću se sa `C:\setup`.

### Posledice
- **Pozitivne**:
  - Serviser sa jednim USB flešom može da inicijalizuje 5-10 mašina za sat vremena.
  - Znatno ubrzana instalacija Office-a i Acrobata (čitanje sa NVMe diska umesto USB magistrale).
- **Negativne / Zahtevi**:
  - Neophodna je Faza 5 koja na kraju mora kompletno obrisati radni folder `C:\setup` sa internog diska kako bi klijent dobio pun skladišni prostor.

## Povezane beleške
- Glavni Indeks: [[index]]
- Pajplajn Izvršavanja: [[architecture/pipeline]]
- Gotchas: [[memory/gotchas]]
