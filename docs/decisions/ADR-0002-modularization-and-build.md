---
title: "ADR-0002: Modularizacija monolitne skripte na 7 modula i dvostruki build sistem"
type: decision
status: accepted
created: 2026-09-10
updated: 2026-09-14
tags:
  - adr
  - decision
  - architecture
  - modules
  - build
---

# ADR-0002: Modularizacija monolitne skripte na 7 modula i dvostruki build sistem

## Kontekst i problem
Inicijalna skripta `Install-ServisApps.ps1` narasla je na preko 250 KB (preko 1100 linija koda). Sve funkcionalnosti (TUI prikaz, WMI upiti, COM API ažuriranja, tihe instalacije, mrežna komunikacija, brisanje logova) bile su smeštene u jedan jedini fajl. To je dovodilo do:
- Otežanog debagovanja i čestih regresija pri izmenama.
- Teškog paralelnog rada specijalizovanih agenata.
- Nepreglednosti i nemogućnosti automatizovanog jediničnog testiranja funkcija.

## Razmatrane opcije
- **Opcija 1: Ostanak na monolitnom fajlu.**
  - *Mane*: Visok tehnički dug, rizik od novih bagova, težak code review.
- **Opcija 2: Dinamičko učitavanje eksternih modula tokom izvršavanja na računaru klijenta (`Import-Module`).**
  - *Mane*: Zahteva da svi pojedinačni `.psm1` moduli uvek budu prekopirani na disk klijenta pre pokretanja, što unosi zavisnosti i rizik od nedostajućih fajlova tokom rane WinPE faze.
- **Opcija 3: Razdvajanje izvornog koda na 7 tematskih `.psm1` modula u `Skripte/Modules/` uz automatizovani Build sistem koji generiše samostalnu monolitnu skriptu.**
  - *Prednosti*: Izvorni kôd je modularan i lak za održavanje, a u produkciji se izvršava jedan pouzdan, samostalan fajl bez eksternih zavisnosti.

## Odluka
Izabrana je **Opcija 3: Razbijanje na 7 modula sa build kompajlerom**:
- Kreirani su moduli: `GWADT.Core`, `GWADT.Hardware`, `GWADT.Network`, `GWADT.Update`, `GWADT.Apps`, `GWADT.Branding`, `GWADT.Cleanup`.
- Kreiran je kros-platformski Python builder (`Skripte/Build-DeployScript.py`) za Linux i PowerShell builder (`Skripte/Build-DeployScript.ps1`) za Windows okruženje.
- Uveden je statički QA analizator (`Skripte/Test-GWADTModules.py`) koji proverava balans zagrada i prisustvo svih 40 funkcija.

### Posledice
- **Pozitivne**:
  - Jasno definisane odgovornosti po modulima.
  - Generisani monolitni skript je uvek samostalan i otporan.
  - Automatska sinhronizacija na sve projektne putanje i USB fleš sa verifikacijom MD5 kontrolne sume.
- **Negativne / Zahtevi**:
  - Nakon izmene bilo kog `.psm1` modula obavezno je pokretanje build skripte i QA test suite-a pre testiranja na hardveru.

## Povezane beleške
- Glavni Indeks: [[index]]
- Arhitektura Modula: [[architecture/modules]]
- Gotchas: [[memory/gotchas]]
