---
title: "ADR-0122: Ultra-Lean Solid LZMS ISO Arhitektura (install.esd), Univerzalna PnP VMD Automatizacija i Dvoslojni Instant Wi-Fi Auto-Connect"
type: decision
status: accepted
date: 2026-10-03
author: gwadt_docs_sync / agency-ops-administrator / gwadt_windows_admin / gwadt_powershell_expert / gwadt_qa_debugger
tags:
  - decision
  - adr
  - ultra-lean-iso
  - install-esd
  - solid-lzms
  - vmd-pnp-automation
  - wifi-zero-wait
  - bootwim-repack
  - autounattend
  - iventoy
  - truenas
  - v2.11.0
---

# ADR-0122: Ultra-Lean Solid LZMS ISO Arhitektura (install.esd), Univerzalna PnP VMD Automatizacija i Dvoslojni Instant Wi-Fi Auto-Connect

## 1. Kontekst i Problem

Nakon uvođenja Lean ISO koncepta u zadatku GWADT-TASK-103 ([[decisions/ADR-0103-lean-iso-nas-apps-streaming]]), u kojem je uklanjanjem lokalne arhive programa (`Programi.tar`) veličina ISO medijuma smanjena sa 14.20 GB na 9.09 GB, tokom svakodnevne radioničke eksploatacije uočena su tri ključna inženjerska izazova:

1. **Prevelik instalacioni medijum (9.09 GB) i usporavanje mrežnog PXE transfera:**
   Iako je 9.09 GB predstavljalo značajno poboljšanje u odnosu na 14.20 GB, primarna slika operativnog sistema (`sources/install.wim`) koristila je standardnu LZX kompresiju sa veličinom od preko 7.14 GB (7,142,990,733 bajtova). U servisnom okruženju sa paralelnim instalacijama više računara preko mrežnog iVentoy PXE servera (ili preko prenosivih Ventoy USB fleš diskova), transfer slike od 9.09 GB trošio je dragoceni mrežni propusni opseg i produžavao vreme inicijalnog butovanja.

2. **Kritični incident sa detekcijom NVMe diska na Intel 11. generaciji (Acer Tiger Lake DEV_9A0B):**
   Tokom instalacije na prenosnom računaru *Acer Aspire* sa procesorom Intel Core 11. generacije (Tiger Lake arhitektura, hardverski VMD ID kontrolera `PCI\VEN_8086&DEV_9A0B`), Windows Setup nije video NVMe skladišni disk i zahtevao je ručno učitavanje drajvera ("Load driver"). Forenzičkom analizom utvrđena su dva uzroka:
   - **Restriktivna deklaracija u `autounattend.xml`:** Pod komponentom `Microsoft-Windows-PnpCustomizationsWinPE` u fazi `windowsPE` postojao je samo jedan `DriverPath` unos koji je vodio ka najnovijem Intel RST v20 drajveru (`01_RST_v20_Gen12-14_CoreUltra`), dok su putanje ka drajverima za 11. generaciju (`02_RST_v19_Gen11-13`) i 10. generaciju (`03_RST_v18_Legacy_Optane`) nedostajale.
   - **Destruktivno forsiranje u `startnet.cmd` i `load_vmd.cmd`:** U skripti `startnet.cmd` (linija 217 unutar labele `:LaunchSetup`), neposredno pre pokretanja `setup.exe`, postojala je linija koja je bezuslovno izvršavala:
     ```cmd
     drvload "X:\Drivers\VMD\01_RST_v20_Gen12-14_CoreUltra\iaStorVD.inf"
     ```
     Ova komanda je bezuslovno preko memorije gazila već korektno učitani RST v19 drajver koji je prethodno selektovan kroz detekciju hardvera, prebacujući kontroler na nekompatibilni v20 drajver i čineći NVMe disk nevidljivim za Windows Setup.

3. **Asinhrono kašnjenje bežične mrežne veze pri prvom logovanju (Wi-Fi desinhronizacija):**
   U ranijoj postavci, povezivanje na radionički servisni Wi-Fi vršeno je tek u sklopu `FirstLogon.ps1` skripte nakon prijavljivanja korisnika na radnu površinu. Windows WLAN AutoConfig servis (`wlansvc`) se nakon OOBE faze često pokreće sporo i asinhrono, usled čega `GWADT.Launcher.exe` pri startu radne površine nije zatekao aktivnu mrežnu konekciju na laptopovima bez mrežnog LAN kabla, što je dovodilo do privremenog pada na lokalni offline režim umesto trenutnog povlačenja OTA paketa sa ServisHub-a.

---

## 2. Donete Odluke i Arhitektonska Rešenja

### 2.1. Prelazak na Ultra-Lean Solid LZMS Arhitekturu (`sources/install.esd`)
Umesto klasične LZX kompresije pojedinačnih fajlova, uvedena je Microsoft Solid LZMS kompresija sa deljenim rečnikom (chunk size 128 KiB / 131,072 B) za obe edicije Windows 11 operativnog sistema (Index 1: Home, Index 2: Pro):
- **Izvoz slike sistema:**
  ```bash
  wimlib-imagex export sources/install.wim all sources/install.esd \
    --solid --solid-compress=lzms:100 --threads=0
  ```
  Veličina slike `install.esd` pala je sa 7,142,990,733 B na svega **5,344,800,452 bajtova** (**4.98 GiB / 5.34 GB**), ostvarujući uštedu od **1.80 GB** na samoj slici.
- **Optimizacija i defragmentacija `sources/boot.wim`:**
  Korišćenjem maksimalne LZX rekompresije:
  ```bash
  wimlib-imagex optimize sources/boot.wim --recompress --compress=maximum
  ```
  Veličina bootabilne WinPE slike smanjena je na **668,892,448 bajtova** (637.91 MiB), uz uštedu od 5.41 MiB neiskorišćenog i fragmentisanog prostora.
- **Nadogradnja ISO generatora (`Skripte/Build-Iso.py`):**
  Uveden je CLI parametar `--image-format` sa opcijama `['auto', 'esd', 'wim']` (podrazumevano `auto`). Prilikom generisanja Lean profila (`--profile lean --image-format esd`), staging generator striktno kopira samo `install.esd`, automatski eliminiše `install.wim` i briše sve privremene `*.bak*` datoteke, dok je proračun slobodnog prostora prilagođen na 6.5 GB.
- **Konačna veličina ISO-a:**
  Generisani Ultra-Lean Zlatni ISO ima veličinu od **6,522,226,688 bajtova** (**6.07 GB / 6.52 GiB** u poređenju sa prethodnih 9.09 GB), što predstavlja **uštedu od 3.01 GB (33.1%)** na nivou celog instalacionog medijuma.

### 2.2. Univerzalna PnP VMD Automatizacija i Rešenje Tiger Lake Incidenta
Otklonjen je rizik gaženja drajvera i obezbeđena deterministička detekcija NVMe diskova na svim generacijama procesora:
- **Proširenje `autounattend.xml`:**
  U prolazu `windowsPE` unutar komponente `Microsoft-Windows-PnpCustomizationsWinPE` deklarisana su sva tri generacijska foldera za Plug-and-Play drajvere:
  ```xml
  <component name="Microsoft-Windows-PnpCustomizationsWinPE" processorArchitecture="amd64" publicKeyToken="31bf3856ad364e35" language="neutral" versionScope="nonSxS" xmlns:wcm="http://schemas.microsoft.com/WMIConfig/2002/State" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
      <DriverPaths>
          <PathAndCredentials wcm:action="add" wcm:keyValue="1">
              <Path>X:\Drivers\VMD\01_RST_v20_Gen12-14_CoreUltra</Path>
          </PathAndCredentials>
          <PathAndCredentials wcm:action="add" wcm:keyValue="2">
              <Path>X:\Drivers\VMD\02_RST_v19_Gen11-13</Path>
          </PathAndCredentials>
          <PathAndCredentials wcm:action="add" wcm:keyValue="3">
              <Path>X:\Drivers\VMD\03_RST_v18_Legacy_Optane</Path>
          </PathAndCredentials>
      </DriverPaths>
  </component>
  ```
- **Uklanjanje bezuslovnog `drvload` u `:LaunchSetup` (`startnet.cmd` i `load_vmd.cmd`):**
  Iz `sources/boot.wim` (`startnet.cmd` i `Skripte/Patch-BootWim.py`) trajno je uklonjena štetna komanda koja je neposredno pred poziv `setup.exe` nasilno nametala RST v20. Umesto destruktivnog ponovnog učitavanja, zadržana je pametna selektivna detekcija Tiger Lake kontrolera (`DEV_9A0B`), praćena bezbednim osvežavanjem uređaja:
  ```cmd
  echo [VMD] Skeniranje hardvera i montiranje svih particija...
  pnputil.exe /scan-devices >nul 2>&1
  (echo san policy=OnlineAll & echo rescan) | diskpart >nul 2>&1
  ```
  Ovim je omogućeno da Windows Setup samostalno preko PnP mehanizma veže tačan INF drajver prema hardverskom ID-ju kontrolera.

### 2.3. Dvoslojni Instant Wi-Fi Zero-Wait Mehanizam
Uvedena je dvoetapna arhitektura koja garantuje da je bežična mrežna veza u potpunosti aktivna i stabilna u sekundi kada se korisnik prvi put uloguje:
- **Sloj 1 — Pre-Logon Tihi Import pod SYSTEM Nalogom (`SetupComplete.cmd`):**
  Tokom faze završetka Windows Setup-a pod `NT AUTHORITY\SYSTEM` privilegijama:
  1. Servis `wlansvc` se konfiguriše na automatsko pokretanje i odmah startuje:
     ```cmd
     sc.exe config wlansvc start= auto >nul 2>&1
     net.exe start wlansvc >nul 2>&1
     ```
  2. Ako u `C:\setup\Wifi-Servis.ini` postoji konfiguracija sa `Enabled=1`, dinamički se kreira `C:\setup\Wifi-Servis.xml` sa parametrima `<connectionType>ESS</connectionType>`, `<connectionMode>auto</connectionMode>` i unetom WPA2-PSK lozinkom.
  3. Profil se uvozi kao trajni mašinski profil za sve korisnike:
     ```cmd
     netsh wlan add profile filename="C:\setup\Wifi-Servis.xml" user=all >nul 2>&1
     netsh wlan connect name="%WIFI_SSID%" >nul 2>&1
     ```
- **Sloj 2 — Instant DesktopStart Hook pre Launchera i UI-ja (`FirstLogon.cmd`):**
  Kroz kreiranje `C:\setup\GWADT-DesktopStart.cmd` skripte koja se poziva čim Windows Explorer podigne radnu površinu, vrši se momentalna provera mrežnog statusa. Uz `setlocal EnableDelayedExpansion`, skripta proverava postojanje bežične veze i forsira povezivanje pre nego što se uopšte pokrenu `GWADT.Launcher.exe` i `GWADT.UI.exe`. Čim Launcher startuje, mrežni stek je 100% operativan.

### 2.4. Gvozdeno Pravilo Hardverskog Integriteta
Bez obzira na radikalno smanjenje ISO medijuma (ušteda od 3.01 GB):
- Svi Intel VMD skladišni drajveri (`iaStorVD.inf` v18/v19/v20) u fascikli `/Za disk 10+ gen` (44 fajla) i u `sources/boot.wim` **STRIKTNO I BESKOMPROMISNO OSTAJU UGRAĐENI U ISO MEDIJUM**.
- Svi mrežni LAN i Wi-Fi drajveri (Intel, Realtek, MediaTek) u `boot.wim` i `$OEM$` stablu ostaju netaknuti.
- Bilo kakvo uklanjanje drajvera radi uštede megabajta predstavlja kršenje ovog pravila.

---

## 3. Izmerene Veličine i Kontrolne Sume

Sve komponente su matematički izmerene i heširane pre puštanja u produkciju:

| Komponenta | Veličina (Bajtovi) | Čitljiva Veličina | MD5 Kontrolna Suma | SHA-256 Kontrolna Suma |
|---|---|---|---|---|
| **Ultra-Lean ISO** | 6,522,226,688 B | **6.07 GB** (6.52 GiB) | `2620818309483cc162ba917859074b68` | `ca0189251bf8a0cadd9dd828d656b44ea9f50902155fbe4a0c9a4b28b16b493a` |
| **sources/install.esd** | 5,344,800,452 B | **4.98 GiB** (5.34 GB) | `8f04799d0c840d3af02b7335eda58d37` | `068a33d4dd15acb64a4de33c331063aa2d07ffcc57dcd4e7c38377c875b05c9b` |
| **sources/boot.wim** | 668,892,448 B | **637.91 MiB** (668 MB) | `12564aae723770c1db22b31ac2b95fe3` | `0ca3376b8ce916124a54876badd5f6077f3e8e69098a88f6859a9d39e3437558` |
| **Prethodni Lean ISO** | 9,755,463,680 B | **9.09 GB** | `0d9a527da439c5348bcc9c058c729373` | `e43f544a4ea1105c086a5d8f7fe5d6405d02094c770932a0f6f7bdb58d3c6944` |
| **Ušteda na ISO-u** | **-3,233,236,992 B** | **-3.01 GB (-33.1%)** | — | — |

---

## 4. Rigorozna QA Verifikacija (30/30 PASS)

Kroz namensku test skriptu `Skripte/Test-Task122-UltraLean-QA.py`, verifikovano je 7 tematskih celina sa 100% prolaznošću (Exit Code 0):

1. **Test 1 — Veličina i Butabilnost (4/4 PASS):** Veličina tačno 6,522,226,688 B ($\le 6.6\text{ GB}$), BIOS bootable (0x88, LBA: 388), UEFI bootable (0xEF, LBA: 712), 100% MD5 i SHA-256 verifikacija.
2. **Test 2 — Struktura ISO Medijuma (6/6 PASS):** Prisustvo `install.esd`, odsustvo `install.wim` i privremenih `*.bak*` datoteka (0 bak fajlova), prisustvo `/Za disk 10+ gen` (44 fajla), prisustvo drajvera i `.NET 3.5` offline paketa.
3. **Test 3 — sources/boot.wim (5/5 PASS):** 1 indeks, LZX kompresija, potpuno odsustvo bezuslovnog `drvload` RST v20 u `:LaunchSetup`, selektivna detekcija `DEV_9A0B`, `pnputil /scan-devices` i `diskpart rescan`.
4. **Test 4 — autounattend.xml (4/4 PASS):** XML validnost (ElementTree), sva 3 deklarativna VMD DriverPath-a (v20, v19, v18), LabConfig hardverski bypass-evi i `GWADT-AcceleratePE.cmd` Order 1.
5. **Test 5 — Wi-Fi Zero-Wait (4/4 PASS):** `SetupComplete.cmd` (auto wlansvc, `Wifi-Servis.xml`, `user=all`, tiho povezivanje pod SYSTEM nalogom), 100% SHA-256 binarni paritet na 3 lokacije (`fc34d416...`), `FirstLogon.cmd` hook sa `setlocal EnableDelayedExpansion` i trenutnim Wi-Fi startom pre Launchera (100% paritet na 5 lokacija `d552559b...`).
6. **Test 6 — AST i Statička Analiza (3/3 PASS):** `Verify-PowerShellSyntax.py` 100% validan AST (0 grešaka), Python AST za `Build-Iso.py` (1,042 linije) i `Patch-BootWim.py` (806 linija) čist.
7. **Test 7 — TrueNAS i iVentoy PXE (4/4 PASS):** Montirana tačka `/mnt/TK_NAS/Servis/win11/sources/install.esd` (5.0 GB), loop mount servis aktivan, iVentoy API prepoznao slike ID 19 i ID 20 sa tačnih 6,522,226,688 bajtova i `uefi_res_lock: 1`.

---

## 5. Posledice i Koristi

### Pozitivne posledice:
- **33.1% manji ISO:** Sa 6.07 GB, prenos instalacije preko mreže i kopiranje na USB fleš diskove je znatno ubrzano.
- **Deterministička VMD instalacija:** Rešen problem sa Intel Tiger Lake 11. generacijom na Acer i srodnim uređajima bez ikakve potrebe za ručnim učitavanjem drajvera.
- **Zero-Wait Wi-Fi konekcija:** Bežična veza je spremna pre nego što serviser uopšte ugleda ikone na radnoj površini, eliminišući kašnjenje pri pokretanju Launchera i komunikaciji sa ServisHub-om.
- **Zadržana puna kompatibilnost:** Oba indeksa (Home i Pro) su sačuvana u `install.esd`, omogućavajući rad sa fabričkim digitalnim licencama.

### Negativne posledice i ograničenja:
- **Vreme kompresije u ESD:** Generisanje Solid LZMS `install.esd` slike zahteva višenitno procesorsko vreme (oko 5-8 minuta na višejedarnom procesoru), što je jednokratan proces tokom kreiranja bazične slike sistema. Sam ISO generator `Build-Iso.py` zatim pakuje već gotov ESD za svega 72.7 sekundi.

---

## 6. Povezani Dokumenti i Zadaci
- Prethodni Lean ISO: [[decisions/ADR-0103-lean-iso-nas-apps-streaming]]
- VMD Pre-Boot Arhitektura: [[decisions/ADR-0029-winpe-vmd-preboot-and-smb-hardening]], [[decisions/ADR-0030-universal-dual-index-winpe-preboot]], [[decisions/ADR-0081-intel-vmd-rst-v20-storage-detection]], [[decisions/ADR-0085-intel-vmd-selective-pci-detection-and-setupapi-conflict-resolution]]
- SMB 3.1.1 Mrežni Akcelerator: [[decisions/ADR-0102-winpe-smb311-truenas-accelerator]]
- Pregled Arhitekture: [[architecture/overview]]
- Ops Runbook: [[memory/ops-runbook]]
- Tehnički Opis Sistema: [[../TEHNICKI_OPIS_SISTEMA|TEHNICKI_OPIS_SISTEMA.md]]
