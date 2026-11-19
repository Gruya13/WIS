---
title: "ADR-0006: Šestostepeno trajno zaključavanje Adobe Acrobat Pro kao podrazumevanog PDF handlera"
type: decision
status: accepted
created: 2026-09-08
updated: 2026-09-14
tags:
  - adr
  - decision
  - applications
  - acrobat
  - pdf
  - file-associations
---

# ADR-0006: Šestostepeno trajno zaključavanje Adobe Acrobat Pro kao podrazumevanog PDF handlera

## Kontekst i problem
U modernim verzijama Windows 11 operativnog sistema, Microsoft Edge agresivno preotima asocijaciju za `.pdf` datoteke, čak i kada korisnik ili skripta instalira Adobe Acrobat Pro. Promena registarskih ključeva u `HKCU\...\UserChoice` je zaštićena hešom (`ProgId` hash zaštita), pa direktan upis u registar biva resetovan od strane sistema pri prvom kliku, vraćajući Edge kao podrazumevani program.

## Razmatrane opcije
- **Opcija 1: Samo postavljanje registarskih ključeva u `HKCU\Software\Classes\.pdf`.**
  - *Mane*: Windows 11 odmah prijavljuje "An app default was reset" i vraća Edge.
- **Opcija 2: Šestostepeni integrisani pristup sa DISM uvozom XML asocijacija, GPO polisama, FeatureLockDown zaštitom i sanitizacijom.**
  - *Prednosti*: Koristi zvanične mehanizme za uvoz sistemskih asocijacija (`dism /Import-DefaultAppAssociations`) podržane na nivou kernela i sistemskih polisa, što trajno sprečava Edge da preotme PDF.

## Odluka
Izabrana je **Opcija 2: Šestostepena zaštita asocijacija u funkciji `Set-AcrobatDefaultPDF`**:
1. **Kreiranje XML datoteke:** Generiše se `AppAssoc.xml` koji mapira `.pdf` na `Acrobat.Document.DC`.
2. **DISM uvoz na nivou slike:** Izvršava se `dism.exe /Online /Import-DefaultAppAssociations:"$assocXmlPath"`.
3. **GPO Polisa sistema:** Postavlja se `DefaultAssociationsConfiguration` u `HKLM:\SOFTWARE\Policies\Microsoft\Windows\System`.
4. **Adobe FeatureLockDown polisa:** U `HKLM:\SOFTWARE\Policies\Adobe\Adobe Acrobat\DC\FeatureLockDown` upisuje se `bDisablePDFHandlerSwitching = 1` (onemogućava prebacivanje PDF handlera).
5. **Čišćenje UserChoice ključa:** Uklanjaju se korisnički heširani ključevi kako bi se forsiralo novo očitavanje sistemske asocijacije.
6. **Programska registracija:** Upisuju se nativni ProgID ključevi za Explorer shell.

### Posledice
- **Pozitivne**:
  - Dvoklik na bilo koji PDF fajl odmah i bezuslovno otvara Adobe Acrobat Pro 2024.
  - Microsoft Edge više nikada ne preotima PDF asocijacije.
  - Nema dosadnih notifikacija o resetovanju podrazumevane aplikacije.
- **Negativne / Zahtevi**:
  - Zahteva administratorske privilegije (koje skripta već poseduje).

## Povezane beleške
- Glavni Indeks: [[index]]
- Tihe Instalacije: [[architecture/silent-applications]]
- Poznate Zamke: [[memory/gotchas]]
