---
title: "ADR-0123: Otklanjanje Setup Greške 0x80070103 - 0x40031 Kroz Trajno Uklanjanje PnpCustomizationsWinPE iz autounattend.xml i Očuvanje Determinističke WinPE PCI Enum Detekcije"
type: decision
status: accepted
date: 2026-10-03
author: gwadt_docs_sync
tags:
  - decision
  - adr
  - winpe
  - vmd
  - rst
  - setupapi
  - error-0x80070103
  - autounattend
  - storage
  - pci-enum
---

# ADR-0123: Otklanjanje Setup Greške 0x80070103 - 0x40031 Kroz Trajno Uklanjanje PnpCustomizationsWinPE iz autounattend.xml i Očuvanje Determinističke WinPE PCI Enum Detekcije

## 1. Kontekst i Problem

Tokom verifikacije i klijentskih instalacija Windows 11 sa novim Ultra-Lean ISO medijumom (v2.11.0, uvedenim kroz TASK-122), na početku rada Windows Setup-a pojavila se fatalna greška koja je u potpunosti prekidala instalacioni proces:
> *"Windows 11 Setup - Windows installation encountered an unexpected error. Error code: 0x80070103 0x40031"*

### Tehnička dekompozicija greške:
1. **Kod greške `0x80070103`:**
   Odgovara Win32 grešci `ERROR_NO_MORE_ITEMS` (kod 259), odnosno SetupAPI statusu `SPAPI_E_NO_DRIVER_SELECTED` ili `DRIVER_ALREADY_INSTALLED`.
2. **Podkod faze i operacije `0x40031`:**
   - Prva cifra `0x4` označava Fazu 4 Windows Setup-a (**SafeOS / WinPE faza SetupPlatform mehanizma**).
   - Operacija `0x0031` označava internu Setup operaciju staging-a i migracije skladišnih drajvera: `SP_EXECUTION_OP_DRIVERS_INSTALL` / `QueueDriverMigration`.
3. **Mehanizam kolizije (Root Cause):**
   U zadatku GWADT-TASK-122, u pokušaju da se omogući automatska podrška za Intel 11. generaciju (Tiger Lake `DEV_9A0B`), unutar `autounattend.xml` u komponentu `Microsoft-Windows-PnpCustomizationsWinPE` deklarisana su tri paralelna `DriverPath` foldera:
   - `01_RST_v20_Gen12-14_CoreUltra` (RST v20.0.0.1038)
   - `02_RST_v19_Gen11-13` (RST v19.5.1.1040)
   - `03_RST_v18_Legacy_Optane` (RST v18.0.1.1138)

   Sve tri generacije drajvera sadrže INF datoteku identičnog imena: **`iaStorVD.inf`**, registruju isti Windows servis (`iaStorVD`), a njihovi podržani PCI ID opsezi se delimično preklapaju.

   Kada `setup.exe` u WinPE okruženju pokrene procesor odgovora (`autounattend.xml`), SetupPlatform mehanizam automatski pokušava da sve deklarisane foldere unese u lokalni WinPE DriverStore. SetupAPI podsistem pri pozivu `SetupDiSelectBestCompatDrv` registruje više konkurentnih paketa istog imena servisa i istog naziva INF datoteke, upada u unutrašnji konflikt rangiranja drajvera (Driver Ranking Conflict) i baca izuzetak `0x80070103` koji ruši instalacioni wizard pre izbora diska.

### Povezivanje sa presedanima u projektu:
- **GWADT-TASK-083:** Inicijalna analiza uvođenja Intel RST v20 drajvera i deklaracije `PnpCustomizationsWinPE`.
- **GWADT-TASK-085 ([[decisions/ADR-0085-intel-vmd-selective-pci-detection-and-setupapi-conflict-resolution]]):** Eliminacija slepe `for /R` petlje (`drvload.exe`) u `startnet.cmd` i `VentoyAutoRun.bat` koja je izazivala identičnu grešku `0x80070103 0x40031` u radnoj memoriji WinPE-a na Meteor Lake hardveru. Rešena je uvođenjem determinističke PCI enum detekcije.
- *Uočena anomalija:* Iako je problem u memoriji rešen u TASK-085 kroz kontrolisani `drvload`, ponovna deklaracija višestrukih verzija u `autounattend.xml` tokom TASK-122 reaktivirala je isti konflikt na nivou SetupPlatform XML parsiranja.

---

## 2. Arhitektonska Odluka

Usvojena je odluka: **Potpuno i trajno uklanjanje komponente `Microsoft-Windows-PnpCustomizationsWinPE` iz `autounattend.xml`.**

### Zašto `PnpCustomizationsWinPE` nije potreban (Inženjersko Obrazloženje):
1. **Deterministička WinPE Pre-Boot detekcija već obavlja posao:**
   Pre nego što se uopšte pokrene `setup.exe`, skripte `VentoyAutoRun.bat` (pri Ventoy/iVentoy butu) i `startnet.cmd` (pri standardnom USB butu) vrše neposrednu inspekciju hardverskih PCI registara:
   - Tiger Lake (`DEV_9A0B`) -> Selektivno učitava isključivo `02_RST_v19_Gen11-13\iaStorVD.inf`.
   - Core Ultra & 12-14. generacija (`DEV_7D0B`, `DEV_AD0B`, `DEV_A77F`, `DEV_467F`) -> Selektivno učitava isključivo `01_RST_v20_Gen12-14_CoreUltra\iaStorVD.inf`.
   - Legacy Optane (`DEV_09AB`) -> Učitava `03_RST_v18_Legacy_Optane\iaStorVD.inf`.
   - AMD procesori (`AuthenticAMD`) -> U potpunosti preskače Intel VMD.
   
   Zbog ovoga je tačno jedan kompatibilan drajver već aktiviran u kernelu i skladišni NVMe disk je u potpunosti vidljiv sistemu pre poziva Setup-a.
2. **Drajveri za ciljni operativni sistem su obezbeđeni kroz OEM sloj:**
   Za instalirani Windows 11 operativni sistem drajveri se ne staging-uju kroz WinPE PnP mehanizam, već se nalaze na putanji `sources/$OEM$/$1/setup/Drivers/VMD/`. Tokom specijalizacije i kasnije Faze 2 deploymenta, sistem povlači drajvere direktno sa diska ili TrueNAS NAS skladišta.
3. **Nula kolizija:**
   Eliminacijom `PnpCustomizationsWinPE` sekcije, Windows Setup pokreće se sa čistom konfiguracijom i ne vrši nasilnu migraciju sukobljenih INF datoteka.

---

## 3. Implementirane Izmene i Životni Ciklus

1. **Čišćenje `autounattend.xml`:**
   Iz prolaza `<settings pass="windowsPE">` u potpunosti je izbrisana komponenta `Microsoft-Windows-PnpCustomizationsWinPE`. Zadržane su sve LabConfig bypass polise (TPM, SecureBoot, RAM, CPU, Storage) i `GWADT-AcceleratePE.cmd` mrežni akcelerator.
2. **Patchovanje i Optimizacija `sources/boot.wim`:**
   - Pokrenut namenski Python skript `Skripte/Patch-BootWim.py` koji je ažurirao ugrađeni `autounattend.xml` u Index 1 slike `boot.wim`.
   - Potvrđeno prisustvo sva 3 foldera VMD drajvera i 7 mrežnih drajvera unutar slike za potrebe pre-boot skripti.
   - Izvršena maksimalna LZX rekompresija (`wimlib-imagex optimize --recompress --compress=maximum`).
   - Nova veličina `sources/boot.wim`: **668,892,186 bajtova** (637.91 MiB).
3. **Izgradnja Novog Zlatnog Ultra-Lean ISO-a:**
   - Izvršena komanda: `./napravi-iso.sh --profile lean --image-format esd --force`.
   - Vreme generisanja: 56.9 sekundi kroz `genisoimage` (UDF 1.02/2.00 ECMA-167, El Torito 0xEF UEFI/BIOS bootable).
   - Slika sistema: `sources/install.esd` (Solid LZMS, 2 indeksa: Home i Pro).

---

## 4. Parametri Zlatnog ISO Medijuma i Produkciono Stanje

| Parametar | Vrednost |
|---|---|
| **Puna Veličina u Bajtovima** | **6,522,224,640 bajtova** |
| **Veličina (Decimalno / Binarno)** | **6.52 GB / 6.07 GiB** |
| **MD5 Kontrolna Suma** | `8d2e7bd4a1b48d2f73f368acc1eab1e1` |
| **SHA-256 Kontrolna Suma** | `a8f5d31331789088da24746f996d53b3912d0b69954303b7265bc3ae31cc105f` |
| **Lokalna Skladišna Kopija** | `/home/gru/Storage/GWADT-Win11-Pro-10.0.26200.8037-Lean.iso` |
| **TrueNAS Primarni ISO** | `/mnt/TK_NAS/Servis/Boot_ISO/01_Windows/GWADT-Win11-Pro-10.0.26200.8037-Lean.iso` |
| **TrueNAS Hard-Link** | `/mnt/TK_NAS/Servis/Boot_ISO/01_Windows/GWADT-Win11-Pro-10.0.26200.8037.iso` (inode 166628) |
| **TrueNAS Loop Mount Tačka** | `/mnt/TK_NAS/Servis/win11/` (`gwadt-win11-mount.service` - active) |
| **iVentoy PXE Server (26000)** | Slike ID 19 i ID 20 (6,522,224,640 B, OS Windows, UEFI Res Lock: 1) |

---

## 5. Posledice i Verifikacija

- **Otklanjanje defekta:** Windows 11 Setup se pokreće bez greške `0x80070103 0x40031` na svim klijentskim mašinama bez obzira na generaciju Intel VMD kontrolera.
- **Čistoća XML specifikacije:** `autounattend.xml` je 100% usklađen sa Microsoft unattended šemom, bez konfliktnih PnP deklaracija.
- **Rigorozna QA Verifikacija (`Skripte/Test-Task123-QA.py`):**
  - Test 1 (Odsustvo PnpCustomizationsWinPE na svim nivoima): **5/5 PASS**
  - Test 2 (Prisustvo VMD i Net drajvera i selektivna detekcija): **4/4 PASS**
  - Test 3 (Očuvanost dvoslojnog instant Wi-Fi Zero-Wait mehanizma): **2/2 PASS**
  - Test 4 (Veličina i ESD format novog ISO-a): **3/3 PASS**
  - Test 5 (TrueNAS ZFS MD5 paritet i iVentoy status): **3/3 PASS**
  - **Ukupan skor:** **17/17 PROVERA POLOŽENO (100.0% PASS)**, Exit Code 0.

---

## 🔗 Povezane Odluke i Reference
- [[index]] — Početni indeks znanja
- [[architecture/overview]] — Pregled arhitekture sistema
- [[decisions/ADR-0083-intel-vmd-rst-v20-storage-detection|ADR-0083]] — Integracija Intel RST v20 drajvera
- [[decisions/ADR-0085-intel-vmd-selective-pci-detection-and-setupapi-conflict-resolution|ADR-0085]] — Selektivna PCI Enum detekcija u WinPE i eliminacija SetupAPI konflikta
- [[decisions/ADR-0122-ultra-lean-solid-lzms-iso-architecture|ADR-0122]] — Ultra-Lean Solid LZMS ISO arhitektura i dvoslojni instant Wi-Fi
- [[memory/ops-runbook]] — Operativni runbook servisne infrastrukture
- [[memory/gotchas]] — Centralna baza evidentiranih bagova i rešenja
