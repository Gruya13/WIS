---
title: "ADR-0124: Sveobuhvatni Rebrending Sistema u 'Windows Installation Assistant (WIS)' v2.12.0 sa Punom Kompatibilnošću Unazad"
type: decision
status: accepted
date: 2026-10-06
author: wis_docs_sync
tags:
  - decision
  - adr
  - rebranding
  - wis
  - architecture
  - powershell
  - csharp
  - iso
  - servishub
---

# ADR-0124: Sveobuhvatni Rebrending Sistema u "Windows Installation Assistant (WIS)" v2.12.0 sa Punom Kompatibilnošću Unazad

## 1. Kontekst i Poslovni Zahtev

Sistem za automatizovanu instalaciju i optimizaciju računara razvijan je pod radnim inženjerskim nazivom **GWADT** (*Gruya Windows Automated Deployment Tool*). Sa postizanjem pune stabilnosti, modularizacije, C# UI Task Runner orkestracije, ServisHub dinamičkog OTA ažuriranja i Lean ISO arhitekture, doneta je strateška odluka da se sistem zvanično rebrendira u:

> **"Windows Installation Assistant"**, skraćeno **`WIS`**.

### Ključni inženjerski izazovi tranzicije:
1. **Zero Downtime u servisu:** Na radnim stolovima i terenu postoje USB instalacioni medijumi i PXE klijenti koji se oslanjaju na stare nazive skripti i binarnih fajlova (`GWADT.*.psm1`, `GWADT-AcceleratePE.cmd`, `GWADT.Launcher.exe`). Rebrending nije smeo da dovede do prekida rada na postojećim medijumima.
2. **Konzistentnost na svim nivoima steka:**
   - PowerShell 5.1 moduli i faze izvršavanja (`Skripte/Modules/`, `Skripte/Phases/`).
   - C# WPF UI kontrolni panel i Thin-Client Launcher (`WIS.UI.exe`, `WIS.Launcher.exe`).
   - WinPE Pre-Boot akceleracija i `sources/boot.wim` unutrašnja konfiguracija.
   - Centralizovani ServisHub demon i OTA payload pakovanje (`wis-payload.zip`).
   - TrueNAS SCALE skladište, SMB mount (`gwadt-win11-mount.service`) i iVentoy PXE server.
   - ISO builder mehanizam i metapodaci medijuma.
   - Dokumentacija, Agent Bus (`second_brain/agent_bus/`) i Obsidian Wiki.

---

## 2. Arhitektonska Odluka

Usvojena je sveobuhvatna strategija rebrendiranja u verziju **`v2.12.0`** uz primenu **Tranzicionog Forwarder & Alias obrasca (Transitional Alias Pattern)**:

### 1. PowerShell 5.1 Moduli (`Skripte/Modules/`)
- Svih 10 primarnih modula preimenovano je u standardni WIS format:
  `WIS.Common.psm1`, `WIS.Core.psm1`, `WIS.Network.psm1`, `WIS.Hardware.psm1`, `WIS.Branding.psm1`, `WIS.Apps.psm1`, `WIS.Update.psm1`, `WIS.Telemetry.psm1`, `WIS.UI.psm1`, `WIS.Cleanup.psm1`.
- Svi fajlovi poseduju striktni **UTF-8 BOM (`\xef\xbb\xbf`)** radi besprekornog parsiranja pod Windows PowerShell 5.1.
- Stari `GWADT.*.psm1` moduli su konvertovani u **kompatibilne forwardere** koji dinamički pronalaze i uključuju odgovarajući `WIS.*.psm1` modul:
  ```powershell
  # GWADT forwarder primer
  $wisPath = Join-Path $PSScriptRoot "WIS.Common.psm1"
  if (Test-Path $wisPath) { . $wisPath }
  ```
- Faze izvršavanja (`Phase1-Network.ps1` .. `Phase5-Finalize.ps1`) prevashodno uvoze `WIS.*` module, uz fallback na `GWADT.*` ako se nađu na starijem medijumu.

### 2. C# Binarci i Metapodaci (.NET Framework)
- Kreirani su novi C# 5.0 projekti u `Skripte/WIS.UI` i `Skripte/WIS.Launcher`:
  - `AssemblyInfo.cs` postavljen na `Title: WIS.UI` / `WIS.Launcher`, `Version: 2.12.0.0`.
  - `MainWindow.xaml` naslov: `"WIS v2.12.0 - Windows Installation Assistant"`.
  - Telemetrijski JSON identifikator promenjen u `tool: "wis"`, `version: "2.12.0"`.
- Binarci kompajlirani u izolovanom Wine Docker okruženju (`gwadt-builder-wine:latest`):
  - `WIS.UI.exe` (275,456 bajtova)
  - `WIS.Launcher.exe` (55,296 bajtova)
- Kompatibilne kopije `GWADT.UI.exe` i `GWADT.Launcher.exe` sinhronizovane su i sačuvane na svim ključnim lokacijama (koren repozitorijuma, `sources/$OEM$/$1/setup/`, TrueNAS skladište).

### 3. WinPE, Unattended i Setup Skripte
- Kreiran primarni mrežni akcelerator `WIS-AcceleratePE.cmd`, dok `GWADT-AcceleratePE.cmd` služi kao forwarder.
- `autounattend.xml` ažuriran da direktno poziva `WIS-AcceleratePE.cmd` i startuje `WIS.Launcher.exe`.
- `SetupComplete.cmd` i `FirstLogon.cmd` prilagođeni da detektuju i pokreću `WIS.Launcher.exe`, a logove zapisuju u `C:\setup\Logs\WIS\`.
- Ažuriran `Skripte/Patch-BootWim.py` i ponovo zapakovan `sources/boot.wim` sa `WIS-AcceleratePE.cmd` u `X:\Windows\System32\` i pre-boot flagom `X:\WIS_PreBoot_Done.tag`.

### 4. ServisHub OTA Arhitektura i TrueNAS Skladište
- Centralizovani `Skripte/ServisHub/hub_server.py` i kontejner na TrueNAS-u (`gwadt-servis-hub`) ažurirani su:
  - Automatski pakuju i `wis-payload.zip` i `gwadt-payload.zip`.
  - Serviraju novi `version.json` sa verzijom `2.12.0` i nazivom `wis`.
- Na TrueNAS ZFS pulu (`/mnt/TK_NAS/Servis/`):
  - Postavljen novi Lean Zlatni ISO: `WIS-Win11-Pro-10.0.26200.8037-Lean.iso`.
  - Kreirani ZFS hard-linkovi na `GWADT-Win11-Pro-10.0.26200.8037-Lean.iso` i `GWADT-Win11-Pro-10.0.26200.8037.iso` čime je sačuvana nulta potrošnja skladišta i 100% kompatibilnost postojećih PXE unosa.
  - Restartovan `gwadt-win11-mount.service` i osvežen `iventoy` kontejner.

---

## 3. Parametri Zlatnog ISO Medijuma v2.12.0

| Parametar | Vrednost |
|---|---|
| **Naziv Fajla** | `WIS-Win11-Pro-10.0.26200.8037-Lean.iso` |
| **Puna Veličina u Bajtovima** | **6,522,349,568 bajtova** |
| **Veličina (GB / GiB)** | **6.52 GB / 6.07 GiB** |
| **Volume Label** | `WIS_WIN11` |
| **MD5 Kontrolna Suma** | `1bb25360a821241c492c39a906766e1b` |
| **SHA-256 Kontrolna Suma** | `c319110227b61e0736c1bbc08e03db622c67a9ccd2d42ab19ca7d2d2a5ec6477` |
| **Format Slike Sistema** | `sources/install.esd` (Solid LZMS, Home & Pro) |
| **WinPE Slika** | `sources/boot.wim` (638 MiB / 668,893,742 B, LZX) |
| **Lokalna Lokacija** | `/home/gru/Downloads/WIS-Win11-Pro-10.0.26200.8037-Lean.iso` |
| **TrueNAS ZFS Lokacija** | `/mnt/TK_NAS/Servis/Boot_ISO/01_Windows/WIS-Win11-Pro-10.0.26200.8037-Lean.iso` |
| **TrueNAS Loop Mount** | `/mnt/TK_NAS/Servis/win11/` (`gwadt-win11-mount.service`) |

---

## 4. Rigorozna QA Verifikacija (`Skripte/Test-Task124-WIS-Rename-QA.py`)

Pokrenuta je sveobuhvatna automatizovana verifikacija:
- **Test 1 (PowerShell moduli i forwarderi):** 5/5 PASS (Prisustvo svih 10 WIS modula, 10 GWADT wrappera, novi log analizatori, PowerShell 5.1 statička analiza bez sintaksnih grešaka).
- **Test 2 (C# binarci i metapodaci):** 7/7 PASS (Prisustvo WIS.UI.exe, WIS.Launcher.exe, GWADT ekvivalenata, AssemblyInfo 2.12.0.0, MainWindow XAML naslov, C# 5.0 sintaksna analiza 38/38 PASS).
- **Test 3 (Setup skripte, XML i WinPE):** 8/8 PASS (WIS-AcceleratePE.cmd, XML validnost, autounattend reference, SetupComplete i FirstLogon hookovi, boot.wim index inspekcija).
- **Test 4 (ServisHub telemetrija i verzija):** 3/3 PASS (version.json v2.12.0, live REST API odgovor, payload v2.12.0 usklađenost).
- **Test 5 (ISO struktura i skladišna distribucija):** 3/3 PASS (Lokalni ISO 6.07 GB, Storage SMB mountovani ISO na TrueNAS-u, GWADT hard-link paritet).
- **UKUPAN REZULTAT: 26/26 PROVERA POLOŽENO (100.0% PASS)**.

---

## 5. Povezane Odluke i Reference
- [[index]] — Početni indeks znanja
- [[architecture/overview]] — Sveobuhvatni pregled arhitekture
- [[decisions/ADR-0075-thin-client-launcher-and-dynamic-servishub-payload|ADR-0075]] — Thin-Client Launcher arhitektura i ServisHub payload
- [[decisions/ADR-0076-decoupling-monolith-into-phase-scripts-ui-orchestrator|ADR-0076]] — Dekompozicija u 5 faza i C# UI Task Runner
- [[decisions/ADR-0102-winpe-smb311-truenas-accelerator|ADR-0102]] — WinPE SMB 3.1.1 akcelerator
- [[decisions/ADR-0122-ultra-lean-solid-lzms-iso-architecture|ADR-0122]] — Ultra-Lean Solid LZMS ISO arhitektura
- [[decisions/ADR-0123-fix-setup-0x80070103-winpe-pnp-removal|ADR-0123]] — Otklanjanje Setup greške 0x80070103
- [[memory/ops-runbook]] — Operativni runbook servisne infrastrukture
