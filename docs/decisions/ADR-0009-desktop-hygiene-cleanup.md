---
title: "ADR-0009: Standard desktop higijene, uklanjanje statusnih fajlova i trajno skladištenje logova"
type: decision
status: accepted
created: 2026-09-12
updated: 2026-09-14
tags:
  - adr
  - decision
  - cleanup
  - logging
  - desktop-hygiene
---

# ADR-0009: Standard desktop higijene, uklanjanje statusnih fajlova i trajno skladištenje logova

## Kontekst i problem
Ranije je skripta na kraju rada kopirala zbirni izveštaj (`GWADT_Izvestaj.log`) i statusni fajl (`GWADT_Status_Instalacije.txt`) direktno na radnu površinu klijenta (`C:\Users\Public\Desktop`). Iako je ovo služilo serviseru za brz uvid, ostavljanje tehničkih servisnih logova na desktopu klijenta narušavalo je profesionalni izgled i dovodilo do pitanja kupaca.

Takođe, bilo je neophodno osigurati da se tehnički logovi ne izgube, već da ostanu trajno sačuvani na bezbednom sistemskom mestu za potrebe eventualne naknadne dijagnostike.

## Razmatrane opcije
- **Opcija 1: Ostavljanje logova na radnoj površini klijenta.**
  - *Mane*: Neprofesionalno prema klijentu, rizik od slučajnog brisanja ili zbunjivanja korisnika.
- **Opcija 2: Potpuno ukidanje logovanja.**
  - *Mane*: Nemogućnost dijagnostike i provere šta je tačno instalirano ukoliko se klijent javi na reklamaciju.
- **Opcija 3: Arhiviranje svih logova u sistemsku fasciklu `C:\Windows\Logs\GWADT\` i temeljno čišćenje radne površine (samo prečice za Word, Excel i Acrobat).**
  - *Prednosti*: Klijent dobija besprekoran, čist desktop bez ikakvih servisnih artefakata, dok serviser i podrška uvek imaju kompletan istorijat u Windows sistemskim logovima.

## Odluka
Izabrana je **Opcija 3: Arhiviranje u `C:\Windows\Logs\GWADT\` i Desktop higijena**:
- Svi logovi (`servis_deploy.log`, `servis_deploy.jsonl`, `setupcomplete.log`, `firstlogon.log`, `acrobat_install.log`) trajno se skladište u `C:\Windows\Logs\GWADT\`.
- Sa radne površine (Public Desktop i profil korisnika) automatski se brišu svi fajlovi po maskama `GWADT_*.log` i `GWADT_*.txt`.
- Uklanja se prečica za WinRAR.
- Na radnoj površini ostaju isključivo tri prečice: **Word**, **Excel** i **Adobe Acrobat**.
- Osvežava se Explorer shell preko `SHChangeNotify` API-ja kako bi se promene odmah prikazale.

### Posledice
- **Pozitivne**:
  - Radna površina klijenta je maksimalno čista i profesionalna.
  - Svi dijagnostički podaci su trajno očuvani i zaštićeni u sistemskom folderu.
- **Negativne / Zahtevi**:
  - Serviser mora znati putanju `C:\Windows\Logs\GWADT\` radi provere istorije rada na mašini.

## Povezane beleške
- Glavni Indeks: [[index]]
- Pajplajn Izvršavanja: [[architecture/pipeline]]
- Evidencija Zamki: [[memory/gotchas]]
