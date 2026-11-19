# WIS Launcher — razvoj (`IG-Systems`)

Komponentna razvojna verzija: **3.1.1** (minimalni kompatibilni bootstrap protokol **3.1.0** uz anti-downgrade zaštitu), u WIS paketu **3.1.1** (vlasnik sistema: **IG-Systems**). Kanonski izvor je root `version.json`; `python3 ISO/Skripte/Sync-Versions.py --check` iz root-a proverava generisane verzije.

C# izvor Launcher-a nalazi se u ovom direktorijumu (C# 5.0 / .NET Framework 4.0; obuhvata `Program.cs`, `SplashForm.cs`, `WifiHelper.cs`, `HubDiscovery.cs`, `PayloadManager.cs`). Objedinjeni Linux i Windows testovi su u `Tests/`. Iz root-a repozitorijuma pokrenuti `python3 Launcher/Tests/run_tests.py`; Windows runner je `Launcher/Tests/Run-LauncherTests.ps1`. Zajednički build je `ISO/Skripte/Build-WIS.ps1` (ili `python3 Launcher/Build-Launcher.py` na Linuxu).

Runtime payload i `C:\setup` putanje ostaju iste. Kompletan Windows 11 24H2 E2E tok (uključujući bootstrap, XML-escaped Wi-Fi profile sa WPA2/WPA3/Open podrškom, sigurno `runas` citiranje argumenata i međurestart kroz `WIS.Launcher.exe`) koristi RSA-2048 + SHA-256 validaciju paketa pre pokretanja `WIS.UI.exe`.

---

# WIS.Launcher — proveren paket i oporavak

Komponentni razvoj **3.1.1**; WinForms, C# 5 i .NET Framework 4.0. Launcher pronalazi Hub, preuzima RSA-2048 potpisan paket `wis-payload.zip`, pri nastavku posle restarta sa aktivnim `deploy-state.json` i verifikovanim `WIS.UI` odmah pokreće `StartMasterUIAndExit()` bez mrežnog čekanja, odnosno izvršava `Skripte/WIS.UI/WIS.UI.exe --master` tek nakon kriptografske verifikacije.

Lokalni/offline tok proverava RSA-SHA256 manifest, zabranjuje `LibreHardwareMonitorLib.dll`/`HidSharp.dll`/`WinRing0` u izdanju 3.1.0+ (`MandatoryRelease310Files`) i SHA-256 svakog očekivanog fajla, uz odbijanje dodatnog izvršivog sadržaja (uključujući `.sys` drajvere) i automatsko čišćenje zaostalih DLL/`.sys` artefakata pri nadogradnji. Download, staging, backup, promocija i recovery koriste zajednički mutex. Backup se proverava pre potvrđivanja, a rollback čuva potvrđenu verziju. Odvojeni self-update helper čeka izlazak roditeljskog procesa pre zamene Launcher EXE-a.

Stari potpisani paketi (`< 3.1.0`) blokirani su anti-downgrade granicom (`MinAcceptedPayloadVersion = "3.1.0"`). Nova izdanja navode `min_launcher_version=3.1.0`. Ne zaobilaziti proveru potpisa radi prelaza.

Windows build (`ISO/Skripte/Build-WIS.ps1`) i objava zahtevaju čist Git commit i Windows PowerShell 5.1 / MSBuild potvrdu, dok Mono lokalno kompajlira kompletan Launcher (`Launcher/Build-Launcher.py`) i izvršava svih `33` testova u `Launcher/Tests/run_tests.py`. Statičko polje `windows_validation: pending` u korenskom `version.json` ostaje neizmenjeno dok puni fizički VM E2E ne bude potvrđen.

Spoljni `Programi/` installer cache nije automatski deo WIS poverenja. Deklarisani potpisani fajlovi i dalje zahtevaju hash i symlink proveru; WIS PKI ne potvrđuje autentičnost proizvoljnih spoljnih instalera.
