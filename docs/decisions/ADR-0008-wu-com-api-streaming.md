---
title: "ADR-0008: Nativni Windows Update COM API i Single-Package Streaming pristup"
type: decision
status: accepted
created: 2026-09-08
updated: 2026-09-14
tags:
  - adr
  - decision
  - windows-update
  - com-api
  - streaming
---

# ADR-0008: Nativni Windows Update COM API i Single-Package Streaming pristup

## Kontekst i problem
Kompletiranje svih drajvera na klijentskom računaru je kritična tačka servisa. Uobičajena praksa korišćenja glomaznih drajver paketa (DriverPack Solution, Snappy Driver Installer) nosi rizik instalacije generičkih ili pogrešnih drajvera, dok online PowerShell moduli (`PSWindowsUpdate`) zahtevaju `NuGet` provajder i instalaciju modula sa PowerShell Gallery pre nego što uopšte započnu pretragu.

Pored toga, kada se više desetina drajvera preuzima odjednom, kvar samo jednog paketa često blokira čitavu instalaciju.

## Razmatrane opcije
- **Opcija 1: Instalacija PSWindowsUpdate modula sa interneta.**
  - *Mane*: Zahteva instalaciju paketa, menja PowerShell politike, nepouzdan na čistom sistemu bez interneta.
- **Opcija 2: Nativni Windows COM API (`Microsoft.Update.Session`) sa Single-Package Streaming pristupom.**
  - *Prednosti*: Koristi ugrađeni Windows API, ne zahteva eksterne biblioteke, preuzima Microsoft WHQL sertifikovane drajvere direktno od proizvođača i omogućava pojedinačnu instalaciju svakog paketa.

## Odluka
Izabrana je **Opcija 2: Nativni COM API sa Single-Package Streaming metodom**:
- Koristi se COM objekat `Microsoft.Update.Session` sa pretragom `Type='Driver' or Type='Software'`.
- Umesto grupnog preuzimanja, svaki paket se preuzima pojedinačno u sopstvenoj `UpdateColl` kolekciji i odmah instalira.
- Progres se u realnom vremenu prikazuje na konzoli.
- Podržano je do 3 iterativna kruga (`MaxCycles = 3`) sa automatskim restartom i nastavkom rada preko `RunOnce` unosa.

### Posledice
- **Pozitivne**:
  - 100% nativno, bez eksternih zavisnosti i modula.
  - Ako jedan paket pukne, ostali paketi se uspešno instaliraju.
  - Serviser na ekranu vidi tačno koji drajver se preuzima i instalira u svakom trenutku.
- **Negativne / Zahtevi**:
  - Neophodna je stabilna mrežna veza (obezbeđena u Fazi 1).

## Povezane beleške
- Glavni Indeks: [[index]]
- Windows Update Podsistem: [[architecture/windows-update]]
- Evidencija Zamki: [[memory/gotchas]]
