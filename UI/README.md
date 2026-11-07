# WIS UI — razvoj

Komponentna razvojna verzija: **2.1.0**, u WIS paketu **2.17.0**. Kanonski izvor je root `version.json`; `python3 ISO/Skripte/Sync-Versions.py --check` iz root-a proverava generisane verzije. [Pravila verzionisanja](../.workspace/wiki/operations/component-versioning-2026-10-15.md).

WPF C# izvor je u ovom direktorijumu; PowerShell moduli i faze su u `Skripte/Modules/` i `Skripte/Phases/`, a PowerShell provere u `Tests/`. Zajednički Windows build pokreće se kroz `ISO/Skripte/Build-WIS.ps1` iz root-a repozitorijuma. Cilj je Windows PowerShell 5.1 i .NET Framework 4.8.

Runbook: [build/release/media](../.workspace/wiki/operations/build-release-media-2.17.md). Runtime payload i `C:\setup` putanje ostaju iste. Windows build/VM/E2E za novi raspored nisu izvršeni.

---

# WIS.UI — WPF servisni panel

Komponentni razvoj 2.1.0; Windows .NET Framework 4.8. UI orkestrira šest PowerShell 5.1 faza i prikazuje hardver, programe, telemetriju i servisne rezultate. IPC razmenjuje JSON sa radničkim procesima.

Status završene instalacije odvojen je od dijagnostike: `cooling_verified=true` samo za uspešan rezultat sa dostupnim senzorom. `UNKNOWN`, nedostupni senzori, WARN, ERROR i CANCELLED zahtevaju `manual_check_required=true`; kritično pregrevanje blokira predaju. UNKNOWN ne predstavlja dokaz ispravnog hlađenja.

Build je deo [Build-WIS.ps1](../ISO/Skripte/Build-WIS.ps1) i [runbook-a](../.workspace/wiki/operations/build-release-media-2.17.md), zajedno sa Launcher-om i potvrdom istog source commit-a. Ne objavljivati prethodne binarce uz nove izvore. Registar/Win32 detekcija izbegava neke WMI pozive, ali vreme izvršavanja zavisi od računara i nije garantovano 0 ms.

WPF runtime, stvarni senzori, UAC i pun Windows Setup tok nisu izvršeni tokom razvoja na Linux-u. Lokalni parser/status testovi ne potvrđuju tu funkcionalnost. Produkcioni 2.16.2 binarci ostaju neizmenjeni do zasebno proverenog build-a i objave.

Prikazana UI verzija čita se iz sopstvenog assembly-ja; nije oznaka WIS paketa.
