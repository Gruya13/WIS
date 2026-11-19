---
title: "ADR-0125: Eliminacija Sistemskih Nedostataka, Dvojezični UI (SR/ZH) i Stabilizacija Servisnog Deployment Pipeline-a v2.13.0"
type: decision
status: accepted
date: 2026-10-07
author: wis_docs_sync
tags:
  - decision
  - adr
  - wis
  - bilingual
  - ui
  - batterybar
  - winpe
  - windows-update
  - oem-drivers
  - reality-check
  - launcher
---

# ADR-0125: Eliminacija Sistemskih Nedostataka, Dvojezični UI (SR/ZH) i Stabilizacija Servisnog Deployment Pipeline-a v2.13.0

## 1. Kontekst i Identifikovani Sistemski Nedostaci

Tokom intenzivnog operativnog rada na radioničkim instalacijama Windows 11 sistema kroz verziju v2.12.0, identifikovano je osam (8) ključnih inženjerskih i korisničkih nedostataka koji su zahtevali temeljnu arhitektonsku doradu:

1. **Lažno pozitivni statusi instalacije aplikacija (Lack of Reality Check):** Instalacija aplikacija (Google Chrome, Adobe Acrobat Pro, Microsoft Office, WinRAR) mogla je da vrati izlazni kod 0 ili bude preskočena usled tihih grešaka, a da UI i skripte prijave uspešan status bez fizičke provere prisustva izvršnih datoteka na disku.
2. **Neželjeni Splash prozor Launchera (Launcher False Update):** `WIS.Launcher.exe` je pri svakom pokretanju bez ServisHub servera prikazivao Splash dijalog za ažuriranje jer nije mogao dohvatiti heš udaljenog paketa, iako je lokalna verzija bila najnovija.
3. **Potreba za dvojezičnim interfejsom (Bilingual UI SR/ZH):** U radionici se redovno servisiraju uvozni računari sa kineskog tržišta. Bilo je neophodno omogućiti trenutno prebacivanje celokupnog korisničkog interfejsa na pojednostavljeni kineski jezik (简体中文) uz implementaciju fonta *Microsoft YaHei* kako ne bi dolazilo do pojave praznih kvadrata (*tofu* karaktera).
4. **Nedostatak globalnog Error Trap-a u PowerShell fazama:** Ukoliko bi skripta naišla na nepredviđeni izuzetak, mogla je da nastavi izvršavanje narednih koraka u defektnom stanju, umesto da se trenutno prekine sa statusom 1 i signalizira fatalnu grešku u UI-ju.
5. **Nepouzdana detekcija laptopa i nedostatak indikatora baterije:** Na Windows 11 sistemima uklonjen je klasični sistemski BatteryBar iz Taskbar-a. Bilo je potrebno implementirati dvoslojnu šasijsku/WMI detekciju prenosnih računara i automatski konfigurisati BatteryBar u moderni lebdeći (Floating Toolbar) režim.
6. **Uslovno mapiranje mrežnog diska Z: u WinPE:** Ako je u računaru postojao USB instalacioni medijum, WinPE skripta je preskakala montiranje TrueNAS SMB deljenja na `Z:\`, onemogućavajući mrežnu akceleraciju i pristup centralizovanoj bazi paketa i drajvera.
7. **Jednoprolazni Windows Update i zaostale zakrpe:** Nakon instalacije nedostajućih OEM drajvera u Fazi 2, Windows Update stek je često deblokirao nove zavisne drajvere i kumulativna ažuriranja koja su ostajala neinstalirana bez drugog prolaza pre finalizacije.
8. **Prekid instalacije drajvera bez lokalnog ServisHub-a:** Ako klijent radi na terenu ili ServisHub na TrueNAS-u nije dostupan, instalacija drajvera je obustavljana bez pokušaja preuzimanja sa Microsoft Update Catalog-a.

---

## 2. Arhitektonske Odluke i Rešenja

### 1. Post-Execution Reality Check za Aplikacije i Drajvere
- U modulu `WIS.Apps.psm1` uvedena je striktna funkcija `Assert-InstalledApplicationsReality`:
  - Fizički skenira poznate putanje u `Program Files` i `Program Files (x86)` za `chrome.exe`, `Acrobat.exe`, `WINWORD.EXE`/`EXCEL.EXE` i `WinRAR.exe`.
  - Ako ijedna od selektovanih aplikacija ne postoji fizički na disku, funkcija baca terminirajući izuzetak koji prekida Fazu 4.
- U `MainWindow.xaml.cs` implementiran je `PerformRealityCheck(int phase)` unutar TaskRunner-a:
  - Za Fazu 2 (Drajveri): Skenira `Win32_PnPEntity` za uređaje sa greškom (`ConfigManagerErrorCode != 0`) i prijavljuje hardverske defekte.
  - Za Fazu 4 (Aplikacije): Verifikuje fizičko postojanje `.exe` fajlova pre prelaska na narednu fazu.
  - U `HandleFinalCompletion()` sprovodi se finalni Reality Check koji definiše boju statusnog banera (Zeleno samo ako je verifikacija 100% prošla).

### 2. Eliminacija Lažnog Ažuriranja u Launcher-u (SHA-256 Integritet)
- U `SplashForm.cs` i `PayloadManager.cs` izmenjen je algoritam provere novih verzija:
  - Ako ServisHub nije dostupan ili ne vraća kontrolnu sumu, Launcher izračunava SHA-256 direktno nad lokalnim fajlom.
  - Uslov za pokretanje procesa ažuriranja promenjen je u strogu formulu:
    ```csharp
    bool shouldUpdate = (!isHashMatch) && (versionComparison < 0);
    ```
  - Time je u potpunosti eliminisan lažni prozor za preuzimanje kada je lokalni paket već ažuran.

### 3. Dvojezični UI (Srpski / Kineski) sa Microsoft YaHei Podrškom
- Implementirana namenska klasa `LocalizationService.cs` koja obezbeđuje trenutno prebacivanje jezika u letu (Runtime Switch bez restarta).
- Rečnik obuhvata sve elemente interfejsa: tabove, opise faza, dijagnostičke poruke, dugmad i statistiku.
- U zaglavlju interfejsa dodata je diskretna kontrola:
  `[ 🇷🇸 SRB | 🇨🇳 中文 ]`
- U `MainWindow.xaml` i `App.xaml` definisana je hijerarhija fontova:
  `FontFamily="Segoe UI, Microsoft YaHei, Arial, sans-serif"` čime je garantovan besprekoran rendering kineskih glifova na svim izdanjima Windows-a.
- Izabrani jezik se perzistira u konfiguracionom fajlu `C:\setup\wis-config.json` i Registru.

### 4. Globalni Error Trap i $ErrorActionPreference = Stop
- Sve faze instalacije (`Skripte/Phases/Phase1-Network.ps1` do `Phase5-Finalize.ps1`) standardizovane su sa:
  ```powershell
  $ErrorActionPreference = "Stop"
  trap {
      Write-Host "[FATAL] Greska u liniji $($_.InvocationInfo.ScriptLineNumber): $($_.Exception.Message)" -ForegroundColor Red
      Send-IpcMessage -Type "ERROR" -Data @{ phase = $PhaseNumber; message = $_.Exception.Message }
      exit 1
  }
  ```
- Garantovano je trenutno zaustavljanje pipeline-a uz slanje strukturisane IPC JSON poruke ka C# UI-ju.

### 5. Dvoslojna Detekcija Laptopa i Floating BatteryBar
- U `WIS.Apps.psm1` implementirana je funkcija `Test-IsLaptopDevice`:
  - Sloj 1: `Win32_SystemEnclosure.ChassisTypes` (tipovi 8, 9, 10, 14, 30, 31, 32).
  - Sloj 2: `Win32_Battery` detekcija prisustva baterijskih ćelija.
- U funkciji `Install-BatteryBarFloatingMode`:
  - Kreira se konfiguracioni fajl sa `Solo=True` čime se BatteryBar prebacuje u lebdeći toolbar nezavisan od Windows 11 Taskbar-a.
  - Postavlja se prečica u `shell:Common Startup` za sve korisnike.
  - Fizički se verifikuje postojanje `BatteryBar.exe` pre povratka statusa `OK`.

### 6. WinPE Disk Z: Bezuslovno Mapiranje sa DHCP Polling-om
- U `WIS-AcceleratePE.cmd`, `GWADT-AcceleratePE.cmd` i `Skripte/Patch-BootWim.py`:
  - Uklonjen je uslov koji je preskakao montiranje mrežnog deljenja kada je pronađen USB disk.
  - Implementirana je robusna petlja čekanja na DHCP mrežnu konfiguraciju (do 6 sekundi ping ka `192.168.170.254`).
  - TrueNAS deljenje `\\192.168.170.254\Servis` se bezuslovno montira na `Z:\` preko SMB 3.1.1 protokola.
  - Pri butovanju sa iVentoy-a, automatski se detektuje spori virtuelni CD-ROM i instalacija se preusmerava na mrežni disk `Z:\` (brzina transfera do 115 MB/s).

### 7. Dvoprolazni Windows Update Podsistem
- U modulu `WIS.Update.psm1` razvijena je funkcija `Invoke-WindowsUpdateSecondPass`:
  - Poziva se na početku Faze 5 (`Phase5-Finalize.ps1`).
  - Pokreće re-scan Windows Update kataloga preko COM API-ja (`Microsoft.Update.Session`).
  - Ako su nakon instalacije OEM drajvera oslobođene nove kumulativne zakrpe ili drajverski paketi, automatski ih preuzima i instalira.
  - Ukoliko nema novih zakrpi, trenutno završava i vraća status `Sistem je u potpunosti azuriran`.

### 8. Internet Fallback za OEM Drajvere
- U modulu `WIS.Hardware.psm1` razvijena je funkcija `Install-DriverFromMsCatalog`:
  - Ako ServisHub na lokalnoj mreži nije dostupan ili ne poseduje traženi drajver (`not_found`), skripta se automatski preusmerava na Microsoft Update Catalog preko interneta.
  - Pretražuje katalog na osnovu Hardware ID-ja (HWID), pronalazi odgovarajući paket, dohvata direktan `.cab` link sa Microsoft CDN-a, preuzima ga, dekompresuje putem `expand.exe` i instalira putem `pnputil.exe /add-driver ... /install`.

---

## 3. Parametri Zlatnog ISO Medijuma v2.13.0

| Parametar | Vrednost |
|---|---|
| **Naziv Datoteke** | `WIS-Win11-Pro-10.0.26200.8037-Lean.iso` |
| **Puna Veličina u Bajtovima** | **6,522,376,192 bajtova** |
| **Veličina (GB / GiB)** | **6.07 GB / 6.52 GiB** |
| **Volume Label** | `WIS_WIN11` |
| **MD5 Kontrolna Suma** | `79c29268235b24d3cd831cdf5ee84cfd` |
| **SHA-256 Kontrolna Suma** | `6b7cb48d96b4c54edf6e95bd0e5a16a58759c9e30d0c4450bcf5c89c25558f8b` |
| **Format Slike Sistema** | `sources/install.esd` (Solid LZMS, Home & Pro) |
| **WinPE Slika** | `sources/boot.wim` (638 MiB / 668,893,742 B, LZX, Index 1 zakrpljen sa Z: DHCP polling-om) |
| **Lokalna Lokacija** | `/home/gru/Downloads/WIS-Win11-Pro-10.0.26200.8037-Lean.iso` |
| **TrueNAS ZFS Lokacija** | `/mnt/TK_NAS/Servis/Boot_ISO/01_Windows/WIS-Win11-Pro-10.0.26200.8037-Lean.iso` |
| **TrueNAS Loop Mount** | `/mnt/TK_NAS/Servis/win11/` (`gwadt-win11-mount.service`) |
| **iVentoy Podrška** | Potvrđen UDF ECMA-167 fajlsistem i El Torito BIOS (0x00) i UEFI (0xEF) unosi |

---

## 4. QA Verifikacija i Sertifikacija

Automatizovano testiranje kroz skriptu `Skripte/Test-Sprint-Defects-QA.py` obuhvatilo je 10 modula:
- **Test 1 (BatteryBar Floating Mode & Laptop Detekcija):** 6/6 PASS.
- **Test 2 (Post-Execution Reality Check & Fizička Verifikacija):** 6/6 PASS.
- **Test 3 (Dvojezični UI & YaHei Font):** 6/6 PASS.
- **Test 4 (Launcher SHA-256 & Splash Eliminacija):** 4/4 PASS.
- **Test 5 (PowerShell $ErrorActionPreference & Global Error Trap):** 6/6 PASS.
- **Test 6 (WinPE Disk Z: Bezuslovno Mapiranje & DHCP Polling):** 5/5 PASS.
- **Test 7 (Dvoprolazni Windows Update Podsistem):** 3/3 PASS.
- **Test 8 (Internet Fallback za OEM Drajvere):** 4/4 PASS.
- **Test 9 (C# 5.0 i PowerShell 5.1 Statička Analiza):** 4/4 PASS (0 prekršaja).
- **Test 10 (Binarni Integritet i SHA-256 Paritet):** 4/4 PASS.

**Zvanični QA Rezultat: 48/48 PROVERA USPEŠNO POLOŽENO (100.0% PASS)**.

---

## 5. Povezane Odluke i Reference
- [[index]] — Početni indeks znanja
- [[architecture/overview]] — Sveobuhvatni pregled arhitekture
- [[decisions/ADR-0119-batterybar-floating-toolbar-win11|ADR-0119]] — BatteryBar lebdeći toolbar
- [[decisions/ADR-0120-network-hardware-apps-ui-redesign|ADR-0120]] — Redizajn korisničkog panela
- [[decisions/ADR-0122-ultra-lean-solid-lzms-iso-architecture|ADR-0122]] — Ultra-Lean Solid LZMS ISO arhitektura
- [[decisions/ADR-0123-fix-setup-0x80070103-winpe-pnp-removal|ADR-0123]] — Otklanjanje Setup greške 0x80070103
- [[decisions/ADR-0124-rebranding-to-wis|ADR-0124]] — Sveobuhvatni rebrending u WIS v2.12.0
- [[memory/ops-runbook]] — Operativni runbook servisne infrastrukture
