---
title: "ADR-0126: Eliminacija Setup Greške 0x800700FF - 0x40030 Kroz Uklanjanje Skripti iz windowsPE Faze autounattend.xml, Prebacivanje Mrežne Akceleracije u startnet.cmd i Zaštitu Radnog Diska"
type: decision
status: accepted
date: 2026-10-07
author: wis_docs_sync
tags:
  - decision
  - adr
  - winpe
  - autounattend
  - setup-error
  - error-0x800700ff
  - acceleratepe
  - boot-wim
  - esd
  - truenas
  - iventoy
---

# ADR-0126: Eliminacija Setup Greške 0x800700FF - 0x40030 Kroz Uklanjanje Skripti iz windowsPE Faze autounattend.xml, Prebacivanje Mrežne Akceleracije u startnet.cmd i Zaštitu Radnog Diska

## 1. Kontekst i Problem

Tokom pokretanja Windows Setup-a na pojedinim klijentskim mašinama u servisu (posebno pri butovanju preko iVentoy PXE mreže ili hibridnih USB medijuma), pojavila se fatalna greška koja je momentalno zaustavljala instalaciju pre izbora jezika ili particije:
> *"Windows Setup - Windows could not parse or process the unattend answer file for pass [windowsPE]. The settings specified in the answer file cannot be applied. Error code: 0x800700FF - 0x40030"*

### Tehnička dekompozicija greške:
1. **Kod greške `0x800700FF`:**
   - Prevedeno iz Win32/HRESULT notacije: `ERROR_NOT_SUPPORTED` / statusni kod završetka `0xFF` (decimalno **255**).
   - SetupPlatform podsistem interpretira bilo koji ne-nula kod koji vrati komanda u `<RunSynchronousCommand>` bloku kao nepopravljivi prekid izvršavanja odgovorne datoteke.
2. **Podkod faze i operacije `0x40030`:**
   - Prva cifra `0x4` označava Fazu 4 Windows Setup arhitekture (**SafeOS / WinPE faza SetupPlatform mehanizma**).
   - Operacija `0x0030` označava internu operaciju primene datoteke odgovora: `SP_EXECUTION_OP_APPLY_UNATTEND` (`ProcessUnattend`).
3. **Korenski uzrok problema (Root Cause):**
   - U prethodnoj arhitekturi, unutar `autounattend.xml` u sekciji `<settings pass="windowsPE">` -> `<component name="Microsoft-Windows-Setup">` nalazio se unos:
     ```xml
     <RunSynchronousCommand wcm:action="add">
         <Order>1</Order>
         <Path>cmd.exe /c "WIS-AcceleratePE.cmd"</Path>
         <Description>Mrezno Ubrzanje i Mapiranje Z diska</Description>
     </RunSynchronousCommand>
     ```
   - Skripta `WIS-AcceleratePE.cmd` (i kompatibilni `GWADT-AcceleratePE.cmd`) imala je sledeće slabosti u specifičnim scenarijima:
     * **Pokušaj samoubistva procesa na radnom disku:** Ukoliko se skripta izvršavala direktno sa montiranog iVentoy virtuelnog diska `Y:`, komande `mountvol Y: /D` i `diskpart detach vdisk` pokušavale su da nasilno demontiraju i odvoje disk sa koga se sam interpretator `cmd.exe` u tom trenutku izvršavao. To je dovodilo do I/O greške, prekida interpretacije i vraćanja izlaznog koda 255 (`0xFF`).
     * **Izostanak Solid LZMS `install.esd` detekcije u pojedinim granama:** Skripte su u nekim uslovima proveravale isključivo prisustvo `install.wim`, zanemarujući novouvedeni Microsoft Solid LZMS `sources/install.esd`.
     * **Osetljivost `windowsPE` prolaza:** `RunSynchronousCommand` unutar `windowsPE` prolaza je izuzetno restriktivan i nije namenjen za izvršavanje kompleksnih batch skripti koje rukuju mrežnim interfejsima, montiranjem pogona ili zavisnostima koje mogu vratiti ne-nula status.

---

## 2. Arhitektonska Odluka

Usvojen je sveobuhvatan paket od četiri međusobno povezane inženjerske odluke:

### 1. Potpuno uklanjanje eksternih skripti iz prolaza `windowsPE` u `autounattend.xml`
- Iz `autounattend.xml` je trajno eliminisan bilo kakav poziv ka batch ili PowerShell skriptama unutar prolaza `windowsPE`.
- Zadržano je isključivo **6 deklarativnih `LabConfig` registarskih zaobilaženja**, renumerisanih striktno od `<Order>1</Order>` do `<Order>6</Order>`:
  1. `BypassTPMCheck` (REG_DWORD = 1)
  2. `BypassSecureBootCheck` (REG_DWORD = 1)
  3. `BypassRAMCheck` (REG_DWORD = 1)
  4. `BypassStorageCheck` (REG_DWORD = 1)
  5. `BypassCPUCheck` (REG_DWORD = 1)
  6. `BypassDiskCheck` (REG_DWORD = 1)
- Ovi unosi garantuju uspešan prolazak kroz Setup zahteve bez ijednog eksternog procesnog rizika.

### 2. Premeštanje mrežne akceleracije i mapiranja diska Z: u `startnet.cmd`
- Celokupna mrežna inicijalizacija, DHCP polling (do 6s) ka TrueNAS serveru (`192.168.170.254`), autentifikacija i mapiranje TrueNAS Samba SMB 3.1.1 deljenja na `Z:\` prebačeni su u `X:\Windows\System32\startnet.cmd` unutar slike `boot.wim`.
- `startnet.cmd` se izvršava **pre nego što se uopšte pozove `setup.exe`**.
- Time je postignuto da `setup.exe` startuje u već kompletno pripremljenom okruženju, sa aktivnim gigabitnim linkom i montiranim brzim diskom `Z:\win11` (105–122 MB/s).

### 3. Zaštita radnog diska i garantovani `exit /b 0` u AcceleratePE skriptama
- U `WIS-AcceleratePE.cmd` i `GWADT-AcceleratePE.cmd` ugrađena je zaštita radnog diska:
  ```cmd
  if /I "%~d0"=="Y:" (
      echo [INFO] Skripta se izvrsava sa diska Y: - preskacem demontazu radi sprecavanja konflikta.
      goto :SkipDetachY
  )
  ```
- Obezbeđena je ravnopravna provera za `install.esd` i `install.wim` formate na svim ciljnim drajvovima.
- Na samom kraju svake skripte garantovan je bezuslovni `exit /b 0`.

### 4. Zakrpljen i maksimalno optimizovan `sources/boot.wim`
- Ažurirana skripta `Skripte/Patch-BootWim.py` koja ugrađuje očišćeni `autounattend.xml` i optimizovani `startnet.cmd` direktno u Index 1 slike `boot.wim`.
- Izvršena LZX maksimalna rekompresija (`wimlib-imagex optimize --recompress --compress=maximum`), postižući optimalnu veličinu od **637.91 MiB** (668,894,208 bajtova).

---

## 3. Parametri Zlatnog ISO Medijuma i Produkciono Stanje

| Parametar | Vrednost |
|---|---|
| **Zvanični Naziv Datoteke** | `WIS-Win11-Pro-10.0.26200.8037-Lean.iso` |
| **Tačna Veličina u Bajtovima** | **6,522,376,192 bajtova** |
| **Veličina (Decimalno / Binarno)** | **6.52 GB / 6.07 GiB (6,220.2 MB)** |
| **MD5 Kontrolna Suma** | `412ecfcb0c815c752066b4971ccceb4e` |
| **SHA-256 Kontrolna Suma** | `dd575aa042e575937452e2dcd83c9a7ac0b427ca9aee1a1a8a2333cc0bd21166` |
| **Boot Arhitektura** | El Torito Hibrid (UEFI 0xEF + BIOS 0x00 No-Prompt, UDF ECMA-167) |
| **Instalaciona Slika Sistema** | `sources/install.esd` (Microsoft Solid LZMS, Index 1: Home, Index 2: Pro) |
| **WinPE Slika** | `sources/boot.wim` (Index 1 Setup, LZX, 637.91 MiB) |
| **Lokalna Skladišna Kopija** | `/home/gru/Storage/WIS-Win11-Pro-10.0.26200.8037-Lean.iso` |
| **TrueNAS Primarni ISO** | `/mnt/TK_NAS/Servis/Boot_ISO/01_Windows/WIS-Win11-Pro-10.0.26200.8037-Lean.iso` |
| **TrueNAS ZFS Hard-Linkovi** | Inode **167532** (link count 8, 0B dodatnog prostora na ZFS pulu) |
| **TrueNAS Loop-Mount Tačka** | `/mnt/TK_NAS/Servis/win11/` (`gwadt-win11-mount.service` - active) |
| **iVentoy PXE Katalog (26000)** | Slike ID 13, 14, 21, 22 (6,522,376,192 B, Windows, UEFI Res Lock: 1) |

---

## 4. Posledice i Verifikacija

- **Otklanjanje defekta:** Fatalna greška `0x800700FF - 0x40030` je trajno eliminisana. Windows Setup se pokreće 100% deterministički na svim platformama (PXE iVentoy mreža, Ventoy fleš, klasični USB medijumi).
- **Zadržana puna brzina instalacije:** Prebacivanjem mapiranja na `startnet.cmd`, Z:\ disk preko SMB 3.1.1 i dalje postiže gigabitnih 105–122 MB/s, čime instalacija celog Windows 11 sistema traje ~3 minuta.
- **Rigorozna QA Sertifikacija (`Skripte/Test-Task126-WinPE-Fix-QA.py`):**
  * Test 1 (Čistoća `autounattend.xml` i odsustvo skripti u windowsPE): **14/14 PASS**
  * Test 2 (LabConfig numeracija i registarski integritet 1-6): **8/8 PASS**
  * Test 3 (Robustifikacija AcceleratePE skripti, ESD podrška i zaštita diska Y:): **61/61 PASS**
  * Test 4 (WinPE Pre-Boot mrežna logika u `startnet.cmd` i parametri boot.wim): **17/17 PASS**
  * Test 5 (Statička analiza koda, PowerShell AST i Python parseri): **7/7 PASS**
  * **Ukupan rezultat:** **107/107 PROVERA POLOŽENO (100.0% PASS)**, Exit Code 0.

---

## 🔗 Povezane Odluke i Reference
- [[index]] — Početni indeks baze znanja
- [[architecture/overview]] — Pregled arhitekture sistema
- [[decisions/ADR-0102-winpe-smb311-truenas-accelerator|ADR-0102]] — WinPE SMB 3.1.1 Akcelerator i zaobilaženje iVentoy CIFS uskog grla
- [[decisions/ADR-0122-ultra-lean-solid-lzms-iso-architecture|ADR-0122]] — Ultra-Lean Solid LZMS ISO arhitektura (install.esd)
- [[decisions/ADR-0123-fix-setup-0x80070103-winpe-pnp-removal|ADR-0123]] — Otklanjanje Setup greške 0x80070103 uklanjanjem PnpCustomizationsWinPE
- [[decisions/ADR-0124-rebranding-to-wis|ADR-0124]] — Rebrending sistema u Tehno kutak Windows Installation Assistant (WIS)
- [[decisions/ADR-0125-system-defects-and-bilingual-ui|ADR-0125]] — Eliminacija sistemskih nedostataka i dvojezični UI v2.13.0
- [[memory/ops-runbook]] — Operativni runbook servisne infrastrukture
- [[memory/gotchas]] — Centralna evidencija sistemskih zamki
