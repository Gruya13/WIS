---
title: "ADR-0086: Potpuna Eliminacija 'Load Driver' Zahteva u WinPE Fazi i Determinističko Intel VMD Pre-Boot Učitavanje"
type: decision
status: accepted
date: 2026-09-29
author: gwadt_docs_sync
tags:
  - decision
  - adr
  - winpe
  - vmd
  - rst
  - ventoy
  - diskpart
  - storage
  - zero-touch
---

# ADR-0086: Potpuna Eliminacija "Load Driver" Zahteva u WinPE Fazi i Determinističko Intel VMD Pre-Boot Učitavanje

## 1. Kontekst i Problem

Nakon uvođenja selektivne PCI detekcije u zadatku GWADT-TASK-085, tokom testiranja instalacije sa Ventoy/USB medijuma na klijentskom laptopu Windows 11 Setup je zaustavljao instalaciju na ekranu za odabir diska uz poruku:
> *"Where do you want to install Windows? - We couldn't find any drives. To get a storage driver, click Load driver"*

### Detaljna Forenzika Osnovnih Uzroka (Root Causes):
1. **USB Diskpart konflikt u logici preskakanja:**
   U `startnet.cmd` i `VentoyAutoRun.bat` nalazio se zaštitni upit:
   ```cmd
   echo list disk | diskpart | findstr /I "Disk " >nul 2>&1
   if not errorlevel 1 goto :StartnetVMDDone
   ```
   Prilikom svakog butovanja sa USB fleš diska (Ventoy USB, Rufus, itd.), USB disk se u Windows PE podsistemu enumeriše kao fizički disk (Disk 0 ili Disk 1). Zbog toga je gornji upit uvek vraćao `errorlevel 0`, skripta je lažno zaključivala da je interni NVMe disk već prepoznat i **potpuno preskakala učitavanje Intel VMD drajvera**!
2. **Pre-wpeinit nedostatak hardverske PCI enumeracije u VentoyAutoRun-u:**
   Ventoy/iVentoy pokreće `X:\VentoyAutoRun.bat` pre poziva `wpeinit`. Pre izvršenja `wpeinit`, Windows PE Plug-and-Play podsistem još uvek nije inicijalizovan, pa je grana registra `HKLM\SYSTEM\CurrentControlSet\Enum\PCI` bila prazna (sadržala samo oflajn šablone). Svi `reg query` upiti za `DEV_9A0B` (Tiger Lake 11. gen) su padali, skripta je forsirano birala rezervni RST v20 koji ne podržava Tiger Lake, kreirala `X:\GWADT_PreBoot_Done.tag` i izlazila. Kada bi se potom pokrenuo `startnet.cmd`, video bi postojeći tag i preskakao drajvere.
3. **Desinhronizacija TrueNAS produkcije:**
   Na mrežnom TrueNAS serveru (`192.168.170.254`) nalazio se stari ISO sa pogrešnom `findstr Disk` logikom (`1041de4720ea91cb3c90407c54bc4053`), jer je prethodna sesija prekinuta pre rsync prenosa.

---

## 2. Arhitektonska Odluka

1. **Uklanjanje `list disk | diskpart` skip uslova:**
   Učitavanje odgovarajućeg VMD drajvera se više nikada ne preskače na osnovu puke detekcije prisustva bilo kog diska (jer to može biti USB fleš disk).
2. **Dodavanje `wpeinit` na početak `VentoyAutoRun.bat`:**
   Pre bilo kakvog upita nad registrom, `VentoyAutoRun.bat` poziva `wpeinit`. Time se garantuje da je PnP menadžer u potpunosti enumerisao PCI magistralu i popunio `HKLM\SYSTEM\CurrentControlSet\Enum\PCI`.
3. **Čista linearna kontrola toka u batch skriptama:**
   Otklonjene sve ugnježdene zagrade koje su izazivale cmd.exe parserske greške.
4. **Deterministički izbor tačno jednog VMD drajvera:**
   - Ako je procesor AMD (`AuthenticAMD`): preskače se VMD.
   - Ako je Tiger Lake (`DEV_9A0B`): učitava se RST v19 (`02_RST_v19_Gen11-13\iaStorVD.inf`).
   - Svi ostali Intel sistemi (Meteor Lake Core Ultra `DEV_7D0B`, Arrow Lake `DEV_AD0B`, 14th Gen `DEV_A77F`, 12th Gen `DEV_467F`, Optane `DEV_09AB` i univerzalni fallback): učitava se RST v20 (`01_RST_v20_Gen12-14_CoreUltra\iaStorVD.inf`).
   - Poziva se `drvload.exe`, pauza od 2 sekunde, pa `echo rescan | diskpart` koji forsira kernel da momentalno prepozna NVMe namespace-ove.
5. **Generisanje Zlatnog ISO-a isključivo preko Docker xorriso motora:**
   Zbog ograničenja 32-bitnog genisoimage alata na fajlovima većim od 4 GB (`sources/install.wim` je 7.14 GB), ISO se gradi isključivo pomoću 64-bitnog xorriso 1.5.8 motora unutar `gwadt-iso-builder:latest` kontejnera.

---

## 3. Posledice i Verifikacija

- **Otklanjanje problema:** NVMe diskovi su momentalno prepoznati u WinPE fazi pre pokretanja `setup.exe`. Windows 11 Setup više nikada ne traži ručno učitavanje drajvera ("Load driver") i lista diskova je popunjena.
- **Sprečavanje 0x80070103 kolizije:** U memoriji WinPE DriverStore-a nalazi se tačno jedan odgovarajući OEM drajver (v19 ili v20), čime je očuvana zaštita od SetupPlatform konflikta `0x80070103 0x40031`.
- **Verifikacioni parametri:**
  - Lokalni ISO: `/home/gru/Downloads/GWADT-Win11-Pro-10.0.26200.8037.iso` (11.80 GB, 12,669,876,224 bajtova).
  - MD5: `ee849cac980da464d1ee2e23cf987cc2`.
  - SHA-256: `d52cd0c9291c5beb8991a289774ea05c69ccf1bb828288493516e1a200cf5afc`.
  - TrueNAS ISO: `/mnt/TK_NAS/Servis/Boot_ISO/01_Windows/GWADT-Win11-Pro-10.0.26200.8037.iso` (MD5 identičan: `ee849cac980da464d1ee2e23cf987cc2`).
  - TrueNAS `gwadt-win11-mount.service`: aktivan (`active`), montirani `boot.wim` MD5: `3c83827212ab17d1cb5db988e0fbce25`.
  - iVentoy PXE servis: aktivan (`running`), slika ID 18 registrovana sa tačnom veličinom 12,669,876,224 bajtova.
  - QA Test Suite (`Skripte/Test-Task085-QA.py`): **56/56 POLOŽENO (100%)**.
