---
title: "ADR-0005: Tiha instalacija Microsoft Office preko ODT Configure.xml umesto GUI OInstall alata"
type: decision
status: accepted
created: 2026-09-08
updated: 2026-09-14
tags:
  - adr
  - decision
  - applications
  - office
  - silent-install
---

# ADR-0005: Tiha instalacija Microsoft Office preko ODT Configure.xml umesto GUI OInstall alata

## Kontekst i problem
U ranijim verzijama skripte, instalacija Microsoft Office paketa se oslanjala na eksterni alat `OInstall_x64.exe`. Prilikom izvršavanja, ovaj alat je otvarao interaktivni grafički prozor koji je zahtevao od servisera da ručno štiklira aplikacije (Word, Excel...), klikne "Install Office" i ručno zatvori prozor po završetku. To je direktno kršilo princip nula-klikova i zaustavljalo automatizaciju ukoliko serviser nije bio za stolom.

## Razmatrane opcije
- **Opcija 1: Simulacija klikova kroz AutoIt ili SendKeys.**
  - *Mane*: Ekstremno krhko, podložno promenama rezolucije i kašnjenjima u odzivu sistema.
- **Opcija 2: Korišćenje zvaničnog Microsoft Office Deployment Tool-a (ODT) sa `Configure.xml` konfiguracijom.**
  - *Prednosti*: Industrijski standard za korporativne instalacije, 100% tihi rad bez interfejsa (`Display Level="None"`), stabilno raspakivanje sa lokalnog diska.

## Odluka
Izabrana je **Opcija 2: Zvanični Microsoft ODT sa prilagođenim `Configure.xml`**:
- Zvanični `setup.exe` (ODT) postavljen je u direktorijum `Programi/Office 2024/files/`.
- Izmenjen je `Configure.xml`: uklonjena je fiksna putanja na `D:\` i postavljen parametar `<Display Level="None" AcceptEULA="TRUE" />`.
- Skripta pokreće instalaciju komandom:
  ```powershell
  Start-Process -FilePath $odtExe -ArgumentList "/configure `"$cfgPath`"" -WorkingDirectory $filesDir -Wait -WindowStyle Hidden
  ```

### Posledice
- **Pozitivne**:
  - Instalacija Office-a (Word, Excel, PowerPoint, Outlook, OneNote, Access, Publisher) teče potpuno autonomno u pozadini.
  - Vreme instalacije smanjeno na 2–3 minuta pri čitanju sa lokalnog SSD-a.
  - Nema iskakanja prozora i blokade servisnog toka.
- **Negativne / Zahtevi**:
  - Neophodno je osigurati da XML sintaksa ostane validna pri svakom ažuriranju paketa.

## Povezane beleške
- Glavni Indeks: [[index]]
- Tihe Instalacije: [[architecture/silent-applications]]
- Poznate Zamke: [[memory/gotchas]]
