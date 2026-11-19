# WIS UI — razvoj

Komponentna razvojna verzija: **3.1.0**, u WIS paketu **3.1.0**. Kanonski izvor je root [`version.json`](../version.json); `python3 ISO/Skripte/Sync-Versions.py --check` iz root-a proverava generisane verzije.

WPF C# izvor je u ovom direktorijumu (uključujući parcijalne klase `MainWindow.xaml.cs`, `MainWindow.Hardware.cs`, `MainWindow.HwTest.cs` i `MainWindow.Reboot.cs`, kao i XAML/BAML resurse u `Resources/` za clean-clone kompilaciju); PowerShell moduli i faze su u `Skripte/Modules/` i `Skripte/Phases/`, a testovi u `Tests/`. Zajednički Windows build pokreće se kroz `ISO/Skripte/Build-WIS.ps1` iz root-a repozitorijuma (obuhvata i `WIS.UI.csproj` i `HwTest/WIS.HwTest.csproj` pod .NET Framework 4.8 i PowerShell 5.1).

Runtime payload i `C:\setup` putanje ostaju iste; `ThermalSensorEngine.cs` koristi isključivo nativni WMI/ACPI/PerfCounters/SMART/OEM sloj (uklj. Dell `DCIM_NumericSensor`, bez `LibreHardwareMonitorLib.dll` i `WinRing0` Anti-BYOVD); Faza 6 (`WIS.Cleanup.psm1` / `Restore-WisSecurityHardening` + `Remove-LegacyWinRing0Driver`) vraća i post-verifikuje podrazumevane OS bezbednosne polise (`$Global:WisSecurityHardeningVerified`, `security_hardening_verified`) uz strogu Anti-Leak kapiju i `WIS_SecurityRecovery_Sentinel` oporavak.

---

# WIS.UI — WPF servisni panel

Komponentni razvoj **3.1.0**; Windows .NET Framework 4.8 (`WIS.UI.exe` + `WIS.HwTest.exe`). UI orkestrira 6 PowerShell 5.1 faza (`13` datoteka modula u `UI/Skripte/Modules/`, `6` faza u `UI/Skripte/Phases/`), prikazuje hardver, programe, telemetriju (`:8080` / `/api/status`, uz parsiranje `pending_commands` i `skip_step` podršku), `WIS Benchmark` metrike faza/protoka (`Start-WisPhaseBenchmark`, `Record-WisStepBenchmark` za `phase4_steps_sec`, `Add-WisBenchmarkTransfer` u `WIS.Common.psm1`), jedinstvenu WU koordinaciju (`ServisWuActive` u `HKLM:\SOFTWARE\WIS` + `Phase4Active` blokada), `Model DNA` & SUBSYS `.inf` filtriranje (`Send-WisModelDnaToHub` / `Select-RelevantDriverInfFiles` u `WIS.Hardware.psm1`), zaštitu `.tar` arhiva od skripti (`Install-DriverTarArchive` u `WIS.Update.psm1`), integrisani 9. tab `🧪 Hardverski testovi` (`UI/HwTest/` sa `operator_wait_sec`/`active_hwtest_sec` metrikom, audio signalom pažnje `880Hz->1175Hz` i jedinstvenim `95°C × 2 / 97°C` termalnim abortom; Faza 5 `45 min`), termalnu štampu Star TSP700II nalepnica (`LabelPrintService.cs`), završni restart (`MainWindow.Reboot.cs` sa `WrapPanel` + `ScrollViewer`) i trojezičnu lokalizaciju (`sr`/`zh`/`en`). Deterministička Linux Mono kompilacija radi preko `UI/Build-UI.py` i `UI/HwTest/Build-HwTest.py`.

Status završene instalacije odvojen je od dijagnostike: `cooling_verified=true` samo za uspešan rezultat sa dostupnim senzorom (i prenosi se kroz `COMPLETED` lifecycle događaj). `UNKNOWN`, nedostupni senzori, WARN, ERROR i CANCELLED zahtevaju `manual_check_required=true`; pri kritičnom CPU pregrevanju (`>= 95°C × 2` ili `>= 97°C` / `THERMAL_CRITICAL`) stres test se odmah prekida radi zaštite hardvera, a pipeline nastavlja Fazu 6 i završetak uz obavezan `thermal_note` zapis i `manual_check_required=true`. UNKNOWN ne predstavlja dokaz ispravnog hlađenja.

Build je deo [Build-WIS.ps1](../ISO/Skripte/Build-WIS.ps1), zajedno sa Launcher-om i potvrdom istog source commit-a. Registar/Win32 detekcija izbegava neke WMI pozive, ali vreme izvršavanja zavisi od računara i nije garantovano 0 ms. U `UI/Tests/` izvršava se **45** jediničnih testova (`Test-HwTestLogic.py` `28`, `Test-UpdateAndDriversLogic.py` `12`, `Test-AutopilotPreOobeCache.py` `5`; provera `/mnt/Servis` NAS sinhronizacije je opt-in preko `WIS_CHECK_NAS_SYNC=1`).

Prikazana UI verzija čita se iz sopstvenog assembly-ja; nije oznaka WIS paketa.
