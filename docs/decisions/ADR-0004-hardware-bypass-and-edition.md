---
title: "ADR-0004: Zaobilaženje hardverskih zahteva kroz LabConfig i forsiranje Windows 11 Pro preko ei.cfg"
type: decision
status: accepted
created: 2026-09-08
updated: 2026-09-14
tags:
  - adr
  - decision
  - unattend
  - bypass
  - windows-pro
---

# ADR-0004: Zaobilaženje hardverskih zahteva kroz LabConfig i forsiranje Windows 11 Pro preko ei.cfg

## Kontekst i problem
1. Windows 11 zvanično nameće striktna ograničenja (TPM 2.0, SecureBoot, podržani procesori 8. generacije i noviji, 4GB RAM-a). Na klijentskim mašinama u servisu ove provere često onemogućavaju instalaciju.
2. Laptopovi sa fabrički utisnutom licencom u UEFI BIOS MSDM tabeli uvek automatski instaliraju Windows Home ediciju, ignorišući želju korisnika za Pro edicijom. Takođe, instalater bez konfigurisanog ključa zaustavlja proces i traži unos serijskog broja ili izbor jedne od 11 edicija.

## Razmatrane opcije
- **Opcija 1: Ručno modifikovanje registra preko Shift+F10 konzole tokom instalacije.**
  - *Mane*: Zahteva prisustvo servisera, kucanje komandi i usporava rad.
- **Opcija 2: Automatizovani unosi u `HKLM\SYSTEM\Setup\LabConfig` unutar `autounattend.xml` uz uvođenje `sources/ei.cfg` i Generic Pro Setup ključa.**
  - *Prednosti*: 100% automatizovano, bez ijednog klika, garantuje instalaciju Windows 11 Pro i preskače ekran za unos serijskog broja.

## Odluka
Izabrana je **Opcija 2: Automatizovani LabConfig + ei.cfg + Generic Setup Key**:
- U `autounattend.xml` (Pass 1 WinPE) dodati su DWORD registarski ključevi u `LabConfig`: `BypassTPMCheck`, `BypassSecureBootCheck`, `BypassRAMCheck`, `BypassStorageCheck`, `BypassCPUCheck`, `BypassDiskCheck`.
- Kreiran je konfiguracioni fajl `sources/ei.cfg` sa direktivom `[EditionID] Professional` i `[Channel] Retail`.
- U blok `<UserData><ProductKey>` postavljen je Microsoft Default Setup ključ za Pro ediciju: `VK7JG-NPHTM-C97JM-9MPGT-3V66T` uz `<WillShowUI>OnError</WillShowUI>`.

### Posledice
- **Pozitivne**:
  - Windows 11 se instalira na bilo koji hardver bez zapinjanja.
  - Setup nikada ne traži unos licence i nikada ne instalira Home verziju.
  - Serviser vidi samo ekran sa listom diskova i jednim klikom pokreće instalaciju.
- **Negativne / Zahtevi**:
  - Generic Setup ključ ne licencira sistem trajno; licenca se aktivira u Fazi 4 preko OEM OA3x verifikacije.

## Povezane beleške
- Glavni Indeks: [[index]]
- Zaobilaženje Hardvera: [[architecture/hardware-bypass]]
- Poznate Zamke: [[memory/gotchas]]
