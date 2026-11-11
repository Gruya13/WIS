# ADR-0181: StepGuard deterministička pre-provera, HardwareIdentity jedan izvor istine, dinamički DeviceChangeMonitor i obrnuti dnevnik rada (WIS UI v2.3.0 / Paket v2.19.0)

- **Status:** Prihvaćeno (Accepted)
- **Datum:** 2026-11-11
- **Učesnici:** `wis_orchestrator`, `wis_powershell_expert`, `wis_ui_architect`, `wis_windows_admin`, `wis_qa_debugger`, `wis_module_architect`, `wis_security_hardening`, `wis_docs_sync`
- **Povezani zadatak:** `WIS-TASK-181`

---

## 1. Kontekst i Problem

U prethodnim verzijama WIS sistema (v2.12.0 – v2.18.0 / UI v2.2.0):
1. **Nepotrebno ponavljanje koraka (Nedostatak pametne pre-provere):**
   - Ako bi serviser ponovo pokrenuo fazu ili ako bi računar već imao instalirane pojedine programe (npr. Chrome, Acrobat, Office), konfigurisan OEM brending ili instalirane drajvere, sistem bi uvek pokretao teške instalacione procese iznova, gubeći vreme i rizikujući MSI/CBS kolizije.
2. **Problem zaglavljivanja modela i serijskog broja u zaglavlju (Header):**
   - U `MainWindow.xaml.cs`, serijski broj se ažurirao isključivo ako je polje bilo prazno ili `"N/A"`, pa bi prvi primljeni generički odgovor (npr. `"To Be Filled By O.E.M."` ili `"System Serial Number"`) ostajao trajno zaključan u zaglavlju.
   - Slično tome, WMI ili IPC poruke mogle su prepisati model računara generičkim placeholderom poput `"System Product Name"`.
3. **Statičnost hardverskih informacija (GPU "Microsoft Basic Display Adapter"):**
   - Detekcija hardvera (`DetectHardware()`) bila je monolitna metoda od 1800 linija koja se pokretala isključivo pri startu aplikacije i na ručni klik servisera.
   - Nakon što Faza 2 uspešno instalira grafički drajver (npr. NVIDIA GeForce ili Intel Iris Xe), UI bi u informacijama i dalje prikazivao `"Microsoft Basic Display Adapter"`.
4. **Ergonomija dnevnika rada:**
   - Dnevnik rada je nove zapise dodavao na dno (`AllLogs.Add`), primoravajući servisera da stalno skroluje dole, dok je panel „Poslednja aktivnost“ na pregledu prikazivao najstarije poruke na vrhu.

---

## 2. Donete Odluke i Rešenja

### A. StepGuard sistem (PowerShell 5.1 & Hibridni Žurnal)
1. **Novi modul `UI/Skripte/Modules/WIS.StepGuard.psm1`:**
   - Uvedena funkcija `Invoke-WisStep` koja pre izvršavanja bilo kog koraka pokreće lagani `-Test` skript-blok.
   - Ako test vrati `@{ Done = $true; Reason = "..." }`, korak dobija status `SKIPPED_DONE`, upisuje se u `C:\setup\step-journal.json`, emituje IPC `step` poruku u UI i trenutno preskače akciju.
   - Ako korak nije gotov ili baci izuzetak, pokreće se stvarna akcija (`-Action`), nakon čega se pokreće post-verifikacija (isti `-Test`). Ako post-verifikacija uspe, status je `OK`; ako ne uspe, status je `WARN` (ili `ERROR` za `-Critical`).
2. **Podrška za prinudno izvršavanje (ForceRerun):**
   - Čitanje `ForceRerun=1` iz `[Servis]` sekcije `Wifi-Servis.ini` i flag datoteka `C:\setup\force-rerun-*.flag`.
3. **Refaktorisane Faze 2, 3 i 4 na 21 atomski korak:**
   - Faza 2: `P2.Identify`, `P2.NasCache`, `P2.Vmd`, `P2.Pnp`, `P2.Diag`, `P2.WuDrivers` (sa TTL-om od 6 sati za WU).
   - Faza 3: `P3.Debloat`, `P3.Telemetry`, `P3.Oem`, `P3.Scaling`, `P3.VcRedist`, `P3.UsbFree`.
   - Faza 4: `P4.Runtimes`, `P4.WinRAR`, `P4.Chrome`, `P4.Acrobat`, `P4.Office`, `P4.BatteryBar`, `P4.Shortcuts`, `P4.PdfDefault`, `P4.Activation`.
4. **Optimizovani međurestart između Faza 3 i 4:**
   - Na kraju Faze 3 snima se `interim_reboot_required` na osnovu `Get-WisPendingRebootState`. Međurestart se preskače samo ako nijedan korak Faza 2 i 3 nije stvarno izvršen i nema nijedne pending-reboot registarske brave; inače se međurestart bezuslovno izvršava radi stabilnosti MSI podsistema.

### B. HardwareIdentityService i DeviceChangeMonitor
1. **`HardwareIdentityService.cs`:**
   - Implementiran centralni filter kvaliteta podataka (`IsPlaceholder`) koji prepoznaje `System Product Name`, `To Be Filled By O.E.M.`, `Default string`, `Microsoft Basic Display Adapter`, itd.
   - Pravilo prioriteta: stvarna hardverska vrednost uvek zamenjuje placeholder; lokalno očitavanje (`WmiFresh`) ima prioritet nad IPC/Autopilot pretpostavkama; tiha zamena u UI sa logovanjem u `servis_deploy.log`.
2. **`DeviceChangeMonitor.cs`:**
   - Povezivanje na Win32 `WM_DEVICECHANGE` (`0x0219`) poruku kroz WPF `HwndSource.AddHook`.
   - 3-sekundni debounce i 30-sekundni rezervni polling koji detektuje promene hardvera u pozadini (automatski prelazak sa Basic Display Adaptera na stvarni GPU naziv).
   - Automatsko pauziranje pollinga tokom termalnog stres testa (Faza 5).
3. **Modularizacija detekcije hardvera (`MainWindow.Hardware.cs`):**
   - Razbijanje monolitne detekcije na granularne metode: `DetectCpu`, `DetectMemory`, `DetectStorage`, `DetectBattery`, `DetectGpu`, `DetectDisplay`, `DetectSystemIdentity`, `DetectPeripherals`.

### C. Dnevnik rada sa najnovijim zapisom na vrhu
1. **`AddLog` u `Models.cs`:**
   - Novi zapisi se ubacuju na indeks 0 (`AllLogs.Insert(0, entry)` i `RecentLogs.Insert(0, entry)`), dok se prekobrojni uklanjaju sa kraja (`RemoveAt(Count - 1)`).
2. **WPF UI kompenzacija skrolovanja:**
   - Kada je `AutoScroll` aktivan, lista prati vrh (`AllLogs[0]`).
   - Kada je `AutoScroll` isključen, `ScrollViewer.ScrollToVerticalOffset` dinamički prilagođava vertikalni pomak tako da korisnikov pogled na starije zapise ostaje neometan.

### D. Zaštita od duplog pokretanja UI-ja i elastični .NET runtime
1. **Isključivo HKLM `RunOnce` sa `WIS.Launcher.exe` prioritetom (`UI/MainWindow.xaml.cs`):**
   - `EnsureRunOnceEntry()` upisuje `WIS_Resume` isključivo u HKLM (HKCU samo kao fallback), brišući pri tome konkurentske `RunOnce` unose (`WIS_AutoStart`, `WIS_Launcher`, `ServisDeployResume`, `GWADT_Resume`) i zastarele Startup skripte (`WIS-AutoStart.cmd`, `WIS-Resume.cmd`). Primarni cilj je `WIS.Launcher.exe --master` ako postoji na `C:\setup\`, uz fallback na `WIS.UI.exe --master`.
2. **Procesna zaštita pre kreiranja prozora (`UI/App.xaml.cs`, `Launcher/SplashForm.cs`):**
   - `App.OnStartup` odmah proverava `Process.GetProcessesByName`; ako `WIS.UI` već radi, aktivira postojeći prozor (`ShowWindow` / `SetForegroundWindow`) i izlazi sa `Environment.Exit(0)` pored postojećeg `Global\WIS_UI_SingleInstance_Mutex` mutexa.
   - `SplashForm.StartMasterUIAndExit` u Launcheru takođe proverava postojeći `WIS.UI` proces pre pokretanja.
3. **Prioritet SMB manifesta (`Launcher/PayloadManager.cs`, `Launcher/SplashForm.cs`):**
   - Prioritizovano deljenje `\\host\servis` ispred `\\host\WIS` i poređenje verzije verifikovanog SMB manifesta u odnosu na HTTP odziv (`CompareVersions >= 0`).
4. **Elastični .NET runtime fallback (`UI/App.config`):**
   - Pored `<supportedRuntime version="v4.0" sku=".NETFramework,Version=v4.8" />` dodat je fallback `<supportedRuntime version="v4.0" />` radi otpornosti na različite klijentske Windows 11 CLR profile.

---

## 3. Posledice i Verifikacija

1. **Ubrzanje i otpornost:**
   - Sistemi sa već instaliranim programima ili konfigurisanim postavkama preskaču redundantne korake u deliću sekunde.
   - Prikaz koraka unutar `Expander`-a na karticama faza pruža transparentan uvid u status svakog pojedinačnog zadatka.
2. **Verifikacija:**
   - Statička provera PowerShell sintakse (`ISO/Skripte/Verify-PowerShellSyntax.py`): svi PowerShell fajlovi (`22/22`) imaju UTF-8 BOM i prošli su analizu bez grešaka.
   - Statička provera C# 5.0 kompatibilnosti (`ISO/Skripte/Verify-CSharpSyntax.py`): svi izvorni `.cs` fajlovi (`UI/` i `Launcher/`) prošli su analizu bez C# 6+ konstrukcija.
   - Jedinični testovi u `UI/Tests/Test-StepGuard.ps1` i `UI/Tests/Test-StatusContract.ps1` pokrivaju logiku `StepGuard`-a i IPC `step` ugovora na Windows PowerShell 5.1 okruženju.
