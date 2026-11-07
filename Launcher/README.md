# WIS Launcher — razvoj

Komponentna razvojna verzija: **2.2.17**, u WIS paketu **2.17.0**. Kanonski izvor je root `version.json`; `python3 ISO/Skripte/Sync-Versions.py --check` iz root-a proverava generisane verzije. [Pravila verzionisanja](../.workspace/wiki/operations/component-versioning-2026-10-15.md).

C# izvor Launcher-a nalazi se u ovom direktorijumu. Objedinjeni Linux i Windows testovi su u `Tests/`. Iz root-a repozitorijuma pokrenuti `python3 Launcher/Tests/run_tests.py`; Windows runner je `Launcher/Tests/Run-LauncherTests.ps1`. Zajednički build je `ISO/Skripte/Build-WIS.ps1`.

Runbook: [build/release/media](../.workspace/wiki/operations/build-release-media-2.17.md). Runtime payload i `C:\setup` putanje ostaju iste. Windows build/VM/E2E za novi raspored nisu izvršeni.

---

# WIS.Launcher — proveren paket i oporavak

Komponentni razvoj 2.2.17; WinForms, C# 5 i .NET Framework 4.0. Launcher pronalazi Hub, preuzima paket i pokreće `Skripte/WIS.UI/WIS.UI.exe --master` tek nakon verifikacije.

Lokalni/offline tok proverava RSA-SHA256 manifest i SHA-256 svakog očekivanog fajla, uz odbijanje dodatnog izvršivog sadržaja. Download, staging, backup, promocija i recovery koriste zajednički mutex. Backup se proverava pre potvrđivanja, a rollback čuva potvrđenu verziju. Odvojeni self-update helper čeka izlazak roditeljskog procesa pre zamene Launcher EXE-a.

Stari potpisani 2.16.2 paket ima minimalan 13-file manifest i novi Launcher ga odbija. Prelaz zahteva svež 2.17 instalacioni medijum sa novim Launcher-om; nova izdanja navode `min_launcher_version=2.2.17`. Ne zaobilaziti proveru potpisa radi prelaza.

[Windows build i objava](../.workspace/wiki/operations/build-release-media-2.17.md) zahtevaju čist Git commit i Windows PowerShell 5.1 potvrdu. Mono je lokalno kompajlirao kompletan Launcher sa C# 5/.NET 4 ciljem i prošle su 28 razvojnih provera. To ne dokazuje Windows EXE zaključavanje, UAC, stvarni self-update i oporavak kroz prekid procesa; te provere ostaju Windows gate. Gotovi produkcioni binarci nisu zamenjeni.

Spoljni `Programi/` installer cache nije automatski deo WIS poverenja. Deklarisani potpisani fajlovi i dalje zahtevaju hash i symlink proveru; WIS PKI ne potvrđuje autentičnost proizvoljnih spoljnih instalera.
