---
title: "ADR-0085: Selektivna Hardverska Detekcija Intel VMD Kontrolera u WinPE i Eliminacija Setup Greške 0x80070103 0x40031"
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
  - setupapi
  - error-0x80070103
  - storage
  - meteor-lake
---

# ADR-0085: Selektivna Hardverska Detekcija Intel VMD Kontrolera u WinPE i Eliminacija Setup Greške 0x80070103 0x40031

## 1. Kontekst i Problem

Nakon uvođenja Intel RST v20 drajvera za podršku procesorima 14. generacije i Core Ultra (Meteor Lake) arhitekture, na klijentskim laptopovima se prilikom startovanja Windows Setup-a pojavila perzistentna greška:
> *"Windows 11 Setup - Windows installation encountered an unexpected error. Error code: 0x80070103 0x40031"*

### Tehnička dekompozicija greške:
1. `0x80070103`: `HRESULT_FROM_WIN32(ERROR_NO_MORE_ITEMS)` / `SPAPI_E_NO_DRIVER_SELECTED` / `DRIVER_ALREADY_INSTALLED`.
2. `0x40031`: Faza 4 (SafeOS / WinPE faza Setup-a), operacija 0x31 (`QueueDriverMigration` / `SP_EXECUTION_OP_DRIVERS_INSTALL`).
3. SetupPlatform mehanizam pri startovanju `setup.exe` u WinPE okruženju vrši migraciju i usklađivanje drajvera skladišnih kontrolera iz WinPE memorije ka ciljnoj instalaciji.
4. U prethodnoj implementaciji, `startnet.cmd` i `VentoyAutoRun.bat` su izvršavali neselektivnu ("slepu") petlju:
   ```cmd
   for /R "X:\Drivers" %%f in (*.inf) do ( drvload.exe "%%f" >nul 2>&1 )
   ```
5. Pod folderom `X:\Drivers\VMD\` nalazile su se tri verzije istog drajvera:
   - `01_RST_v20_Gen12-14_CoreUltra` (RST v20.0.0.1038)
   - `02_RST_v19_Gen11-13` (RST v19.5.1.1040)
   - `03_RST_v18_Legacy_Optane` (RST v18.0.1.1138)
   Sve tri verzije registruju isto ime servisa `iaStorVD` i isti binarni drajver `iaStorVD.sys`. Učitavanjem v19 i v18 preko v20, na Meteor Lake hardveru (`DEV_7D0B`) servis je bio prebrisan drajverom koji ne podržava dati hardver, a u WinPE DriverStore-u su ostali konfliktni paketi bez hardverskog uparivanja. Pri pozivu `SetupDiSelectBestCompatDrv` SetupPlatform je naišao na `0x80070103` i oborio instalaciju.

---

## 2. Arhitektonska Odluka

Usvojeno je pravilo: **Striktno nula ili jedan VMD drajver paket sme biti učitan u WinPE sesiji.**

### Implementirane mere:
1. **Potpuna eliminacija slepe `for /R` petlje:**
   Mrežni drajveri se učitavaju odvojeno iz `X:\Drivers\Net`.
2. **Ciljana PCI Enum hardverska detekcija:**
   Pre poziva `drvload.exe`, batch skripte vrše upit nad PCI Enum registrima:
   - `AuthenticAMD`: Na sistemima sa AMD procesorom učitavanje VMD drajvera se u potpunosti preskače.
   - `DEV_7D0B DEV_AD0B DEV_A77F DEV_467F`: Učitava se isključivo `01_RST_v20_Gen12-14_CoreUltra\iaStorVD.inf` (Meteor Lake Core Ultra Series 1, Arrow/Lunar Lake Series 2, Raptor Lake 14th Gen, Alder Lake 12th Gen).
   - `DEV_9A0B`: Učitava se isključivo `02_RST_v19_Gen11-13\iaStorVD.inf` (Tiger Lake 11. gen).
   - `DEV_09AB`: Učitava se isključivo `03_RST_v18_Legacy_Optane\iaStorVD.inf` (Legacy Optane / Managed).
   - Standardni AHCI / Native NVMe sistemi: Ako je disk već prepoznat nativnim drajverom, VMD se ne učitava.
3. **Dual-Index WinPE & Setup WIM ažuriranje:**
   Obe slike (`Index 1` WinPE i `Index 2` Windows Setup) unutar `sources/boot.wim` su sinhronizovane sa novom logikom.
4. **Idempotentni Tag Bypass:**
   Marker `X:\GWADT_PreBoot_Done.tag` obezbeđuje da se drajveri ne učitavaju dvostruko ukoliko je `VentoyAutoRun.bat` već obavio inicijalizaciju pre `startnet.cmd`.

---

## 3. Posledice i Verifikacija

- **Otklanjanje greške:** Windows 11 Setup pokreće se bez greške `0x80070103 0x40031` na svim klijentskim mašinama (Meteor Lake Core Ultra, 14th Gen, 11th Gen i AMD).
- **Čistoća DriverStore-a:** U memoriji WinPE postoji tačno jedan odgovarajući OEM storage drajver bez konflikata servisa `iaStorVD`.
- **Verifikacija:**
  - `Skripte/Test-Task085-QA.py`: 56/56 testova prošlo (100% PASS).
  - TrueNAS `gwadt-win11-mount.service` aktivan i sinhronizovan sa novim Zlatnim ISO-om (MD5: `1041de4720ea91cb3c90407c54bc4053`).
  - iVentoy PXE servis operativan u stanju `running`.
