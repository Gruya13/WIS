---
title: "ADR-0133: Master Tehnička Dokumentacija Sistema WIS v2.15.1 (TEHNICKI_OPIS_SISTEMA.md i README.md)"
---

# ADR-0133: Master Tehnička Dokumentacija Sistema WIS v2.15.1 (TEHNICKI_OPIS_SISTEMA.md i README.md)

## 1. Kontekst i Problem

Nakon intenzivnog razvojnog ciklusa koji je obuhvatio prelazak sa monolitnog GWADT alata na modularni **WIS (Tehno kutak Windows Installation Assistant)** ekosistem, uvođenje novog 6-faznog pajplajna (sa obaveznim multi-core stres testom u Fazi 5), redizajn taba Dijagnostika (5-minutni telemetrijski Canvas grafikon i Core Clocks tabela taktova jezgara), uvođenje kumulativnog tajmera, 100% kinesku lokalizaciju (288 ključeva), eliminaciju mock podataka, WinPE SMB 3.1.1 akceleraciju i Ultra-Lean Solid LZMS ISO arhitekturu (6.07 GB), postojeća krovna dokumentacija je bila parcijalno zastarela:
1. `TEHNICKI_OPIS_SISTEMA WIS.md` je poslednji put sveobuhvatno ažuriran za verziju v2.12.0 i nije dokumentovao novouvedene module, novi 6-fazni pajplajn, Canvas grafikon, Core Clocks tabelu, 100% RunspacePool CPU load generator u Fazi 5, niti rešenja za greške 0x800700FF, 0x80070103 i BatteryBar modalni NSIS popup.
2. `README.md` je još uvek nosio naziv GWADT, pominjao stari 5-fazni pajplajn i nije sadržao uputstva za nove alate i metrike sistema.
3. Kvantitativna analiza koda (LOC) nije bila osvežena nakon masivnih refaktorisanja i uvođenja novih QA testova.

Bilo je neophodno izraditi **kompletnu, autoritativnu i enciklopedijsku tehničku dokumentaciju** koja detaljno i precizno specifikuje celokupan projekat na verziji **WIS v2.15.1**.

---

## 2. Arhitektonska Odluka

Usvojena je i implementirana sveobuhvatna nadogradnja tehničke dokumentacije projekta kroz sledeće celine:

### 2.1. Izrada Master Inženjerske Specifikacije (`TEHNICKI_OPIS_SISTEMA.md` i `TEHNICKI_OPIS_SISTEMA WIS.md`)
Generisan je iscrpan dokument od **17 tematskih celina** (preko 1,200 linija najpreciznije tehničke specifikacije) koji pokriva:
- Misiju sistema, operativne serviserske izazove i prelazak na model "Jedan klik" sa Zero-Touch autonomijom.
- Globalni Mermaid dijagram životnog ciklusa od iVentoy PXE / USB buta, preko WinPE-a, OOBE-a, C# Launchera, WPF UI-ja, do 60s automatskog gašenja.
- WinPE Pre-Boot skladišnu strategiju: Dual-Index `boot.wim` sinhronizaciju, Intel RST VMD selektivnu PCI detekciju (v20, v19.5 DEV_9A0B, v18), WinPE Turbo akceleraciju (High Perf CPU, 512 MB FBWF), sentinel marker `X:\WIS_PreBoot_Done.tag`, WinPE SMB 3.1.1 akcelerator na portu 445 (105-122 MB/s, dekompresija za 3 min) i Ultra-Lean Solid LZMS ISO arhitekturu (6.07 GB, `install.esd`).
- Deklarativnu automatizaciju Setup-a: 6 `LabConfig` unosa, LanmanWorkstation SMB polise za Windows 11 24H2, `BypassNRO=1`, AutoLogon, nalog Korisnik, čisti Thin-Client `$OEM$` model i `ei.cfg`.
- Post-OOBE Bootstrap podsistem: `SetupComplete.cmd` (SYSTEM nalog, tihi wlansvc start, import Wi-Fi XML profila), `FirstLogon.cmd` (SID NTFS permisije, gašenje QuickEdit moda, zaštita od limita od 8191 karaktera), `FirstLogon.ps1` (12s fast-exit mrežna petlja).
- Thin-Client Launcher (`WIS.Launcher.exe` .NET 4.0, 56 KB): UDP Broadcast Discovery na portu 8888, preuzimanje `wis-payload.zip`, `UseShellExecute = false` i automatska deblokada preuzetih datoteka brisanjem NTFS `:Zone.Identifier` toka preko Win32 `DeleteFile` API-ja.
- WPF C# Servisni Panel (`WIS.UI.exe` .NET 4.8, 387 KB): MVVM arhitektura, višekanalni 5-minutni rolling Canvas grafikon (300 uzoraka @ 1 Hz, 5 linija sa živom legendom), Core Clocks tabela sa radnim taktovima svih jezgara, kumulativni tajmer trajanja servisa, eliminacija mock podataka, dvojezična lokalizacija (Srpski / Kineski sa 288 rečničkih ključeva), hibridna Win32/Kernel32 detekcija u 0 ms, senzori `LibreHardwareMonitorLib` i `HidSharp`, dvosmerni JSON IPC cevovod i kooperativna pauza.
- Šestofazni orkestracioni pajplajn: Faza 1 Mreža, Faza 2 Update, Pre-Reboot Gate (međurestart za rasterećenje CBS/MSI steka), Faza 3 Optimizacija, Faza 4 Programi, Faza 5 Multi-Core Stres Test 30s (100% CPU opterećenje na svim jezgrima preko `RunspacePool`-a sa 98.0°C Emergency Abort osiguračem), Faza 6 Završetak (45 min timeout, asinhroni drugi prolaz WU, DMTF karton, anti-leak brisanje tajni i samouništenje `C:\setup`).
- Modularnu PowerShell arhitekturu: 10 čistih `WIS.*.psm1` modula kodiranih u UTF-8 BOM bez rizičnih emodžija.
- Softverski paket i tihu instalaciju: MSI izolacija preko `Global\_MSIExecute` mutexa, Chrome Enterprise MSI (~8s), Acrobat Pro sa 6-stepenim PDF lockom, Office 2024 LTSC ODT, WinRAR, BatteryBar Floating Toolbar sa prevencijom NSIS modalnog popupa, OEM OA3x verifikacija licence.
- Centralizovani ServisHub na TrueNAS-u: Python HTTP/SSE server na portu 8080 (zaštita ERP porta 8000), SSE 1 Hz ogledalo ekrana, UDP discovery, REST API, dinamički payload packager, web dashboard i CLI monitor.
- Radioničku mrežnu i serversku infrastrukturu: TrueNAS SCALE (192.168.170.254), ZFS pul, SMB 3.1.1, loop mount servis `gwadt-win11-mount.service`, iVentoy PXE server na portu 26000, noćni cron sinhronizator zakrpi.
- Alate za održavanje i build: USB Tool Flask Web GUI (:8092), `Build-Iso.py`, `Patch-BootWim.py`, `Export-SlimWim.py`, Docker Wine MSBuild okruženje (`gwadt-builder-wine:latest`).
- 14 detaljno opisanih inženjerskih zamki (The Gotchas & SRE Solutions).
- QA verifikaciju i Standardne Operativne Procedure (SOP 01 do 04, PC Speaker audio signali).
- Centralnu matricu fajlova i binarne kontrolne sume za v2.15.1.
- Sveobuhvatnu analizu linija koda (LOC) po tehnologijama.

### 2.2. Ažuriranje Glavnog Repozitorijumskog `README.md`
- Usklađen sa novim brendom WIS, verzijom v2.15.1 i modernim statusnim bedževima.
- Prikazuje 6-fazni pajplajn, dijagram toka, strukturu direktorijuma, brze komande za servisere, QA testiranje i reference ka internoj dokumentaciji.

---

## 3. Status Odluke

**Prihvaćeno (Accepted)** — Dokumentacija kreirana, verifikovana, sinhronizovana i povezana u Obsidian bazu znanja projekta.

---

## 4. Posledice Odluke

### Pozitivne:
- **Jedinstveni izvor istine (Single Source of Truth):** Serviseri, administratori i novi developeri imaju kristalno jasan, detaljan uvid u svaki podsistem, registarski ključ, mrežni port i skriptu unutar sistema v2.15.1.
- **Operativna pouzdanost i SOP:** Jasno definisane standardne operativne procedure (SOP) omogućavaju besprekoran rad u radionici bez nagađanja.
- **Olakšano rešavanje incidenata:** Dokumentovanih 14 SRE gotchas pruža trenutno rešenje za sve poznate operativne probleme (od zaključanih mutexa do termalnog trotlovanja).
- **Potpun binarni i metapodatkovni integritet:** Zavedene tačne veličine i SHA-256 sume za sve izvršne fajlove verzije v2.15.1.

---

## 5. Povezane Beleške
- Glavni Indeks Baze Znanja: [[../index|index.md]]
- Master Tehnička Specifikacija: [[../../TEHNICKI_OPIS_SISTEMA|TEHNICKI_OPIS_SISTEMA.md]]
- Pregled Arhitekture: [[../architecture/overview|overview.md]]
- Pajplajn Specifikacija: [[../architecture/pipeline|pipeline.md]]
- Hotfix v2.15.1: [[ADR-0132-hotfix-v2151-stress-load-batterybar-and-timeout]]
- Redizajn Dijagnostike v2.15.0: [[ADR-0131-diagnostics-redesign-6phase-pipeline-and-wis-cleanup]]
