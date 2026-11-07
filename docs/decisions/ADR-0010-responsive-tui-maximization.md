---
title: "ADR-0010: Troslojna maksimizacija PowerShell konzole i responsivni TUI interfejs punom širinom ekrana"
type: decision
status: accepted
created: 2026-09-14
updated: 2026-09-14
tags:
  - adr
  - decision
  - tui
  - ui
  - console
  - responsive
  - maximization
---

# ADR-0010: Troslojna maksimizacija PowerShell konzole i responsivni TUI interfejs punom širinom ekrana

## Kontekst i problem
Prvobitni TUI dashboard u skripti (`Show-ServisDashboard`) koristio je fiksnu širinu od 80 karaktera (`$W = 80`). Na modernim monitorima (1366x768, 1920x1080 Full HD, 2K ili 4K), standardni PowerShell prozor se često otvarao u malim podrazumevanim dimenzijama (80x25), zauzimajući tek mali fragment ekrana na levoj strani.

Takođe, fiksna širina od 80 karaktera nametala je agresivno skraćivanje teksta:
- Nazivi modela uređaja, matičnih ploča i serijski brojevi su se sekli na 25 karaktera sa tri tačke (`...`).
- Dugi nazivi procesora (npr. Intel 12th/13th Gen sa taktom i jezgrima) su bili isečeni.
- Windows Update KB paketi sa dugačkim opisima nisu mogli biti prikazani u celosti.
- Progres barovi su bili minijaturni (14-16 slotova).
- Tabela od 7 aplikacija je morala biti na silu deljena u 2 uska reda, umesto da se iskoristi široka radna površina.

Cilj je bio da PowerShell prozor uvek bude automatski maksimizovan po startovanju i da svi CLI elementi dinamički zauzimaju 100% širine ekrana bez obzira na rezoluciju monitora.

## Razmatrane opcije
- **Opcija 1: Samo prečica ili argument `/MAX` u `.bat` fajlovima.**
  - *Mane*: Ne radi ako serviser pokrene skriptu direktno, kroz UAC elevaciju, kroz kontekstni meni, ili ako se skripta izvršava pod Windows Terminal-om.
- **Opcija 2: Samo podešavanje `$Host.UI.RawUI.WindowSize`.**
  - *Mane*: PowerShell `RawUI` ne može samostalno da maksimizuje Windows OS prozor ako je OS prozor manji od maksimalnog bafera, već samo menja unutrašnji bafer i može baciti izuzetak ako `WindowSize` pređe dozvoljeni maksimum.
- **Opcija 3: Troslojni mehanizam maksimizacije (Launcher + UAC + Win32 API) uz potpuno dinamički TUI render.**
  - *Prednosti*: Garantuje maksimizovan prozor u 100% scenarija pokretanja, dok se svi TUI elementi automatski prilagođavaju rezoluciji pomoću `Get-ServisConsoleWidth`.

## Odluka
Usvojena je **Opcija 3**:

### 1. Troslojno osiguranje maksimizacije prozora:
1. **Launcher sloj (`FirstLogon.cmd`, `Pokreni-Instalaciju.bat`)**:
   PowerShell se pokreće uz `start "" /MAX powershell.exe -NoProfile -NoExit -ExecutionPolicy Bypass -File ...`.
2. **UAC Elevacija (`Install-ServisApps.ps1` Preamble)**:
   Dodata opcija `-WindowStyle Maximized` u `Start-Process powershell.exe ... -Verb RunAs`.
3. **Nativni Win32 API sloj (`Set-ConsoleMaximized`)**:
   Unutar PowerShell modula `GWADT.Core.psm1` definisana je funkcija `Set-ConsoleMaximized` koja:
   - Poziva `[user32.dll]::ShowWindow(hWnd, 3)` (`SW_MAXIMIZE`) za standardni Console Host (`conhost.exe`).
   - Pronalazi i maksimizuje prozor roditeljskog Windows Terminal-a (`wt.exe` / `WindowsTerminal.exe`) ukoliko je terminal podrazumevani emulator u Windows 11.
   - Sinhronizuje `$Host.UI.RawUI.BufferSize` i `$Host.UI.RawUI.WindowSize` sa fizičkim maksimumom ekrana (`MaxPhysicalWindowSize`).

### 2. Responsivni TUI sistem punom širinom ekrana:
- **Dinamička širina**: Uvedena funkcija `Get-ServisConsoleWidth` koja vraća `$W = [math]::Max(80, $Host.UI.RawUI.WindowSize.Width - 1)`. Oduzimanje 1 karaktera sprečava automatski prelom linije na desnoj ivici konzole.
- **Razvučeni naslov i desno poravnati pulsator**: Naslov servisa ostaje na levoj ivici, dok se indikator rada sa rotirajućim spinnerom pomera na sam desni rub prozora.
- **Proporcionalni hardverski blok**:
  - Linija 1: Uređaj (50%), S/N (28%), BIOS (ostatak).
  - Linija 2: CPU (50%), RAM (25%), Disk (ostatak).
  - Linija 3: OS (65%), Napajanje (ostatak).
  - Na Full HD i širokim rezolucijama nazivi se više ne seku sa `...`.
- **Centrirana obaveštenja**: Zvučna i vizuelna upozorenja za vađenje USB-a i Wi-Fi drajvere automatski se centriraju unutar širine `$W`.
- **Elastični progres barovi**: Glavni bar za instalaciju programa se širi do 70 karaktera, a mini-bar u toku do 45 karaktera.
- **Adaptivna mreža aplikacija (`Write-ServisItemsGrid`)**:
  - Širina $\ge 140$ kolona (Full HD, 2K, 4K): Svih 7 aplikacija se prikazuje u **jednom jedinstvenom horizontalnom redu** sa jednakim kolonama punom širinom ekrana.
  - Širina $95 - 139$ kolona: 2 balansirana reda (4 + 3 stavke).
  - Širina $< 95$ kolona: Kompaktni prikaz (3 + 4 stavke).
- **Završni meniji (`GWADT.Cleanup.psm1`)**: Interaktivni meniji u Fazi 5 sada koriste pune horizontalne razdelnike `cW - 6`.

## Posledice
- **Pozitivne**:
  - Skripta izgleda maksimalno pregledno, profesionalno i uočljivo sa veće udaljenosti u servisnoj radionici.
  - Nema sečenja ključnih hardverskih podataka i naziva Windows Update paketa.
  - Podržane su sve rezolucije monitora (od starih laptop ekrana 1366x768 do 4K serviserskih monitora).
  - 100% PowerShell 5.1 kompatibilno.
- **Negativne / Zahtevi**:
  - Pri promeni veličine prozora tokom samog rada skripte, sledeće osvežavanje dashboarda automatski prilagođava raspored na novu širinu.

## Povezane beleške
- Glavni Indeks: [[index]]
- Pregled Arhitekture: [[architecture/overview]]
- Moduli Sistema: [[architecture/modules]]
- Evidencija Zamki: [[memory/gotchas]]
