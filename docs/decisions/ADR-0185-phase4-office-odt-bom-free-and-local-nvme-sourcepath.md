# ADR-0185: Pozadinski Windows Update, PowerShell 5.1 UTF-8 BOM, Očuvani Međurestart i P4.Office ODT Sanacija (v2.18.0–v2.19.0)

- **Status:** Prihvaćeno (Accepted)
- **Datum:** 2026-11-11
- **Učesnici:** `wis_orchestrator`, `wis_powershell_expert`, `wis_ui_architect`, `wis_module_architect`, `wis_qa_debugger`, `wis_security_hardening`, `wis_docs_sync`
- **Povezani zadaci:** `WIS-TASK-175`, `WIS-TASK-177`, `WIS-TASK-178`, `WIS-TASK-180`, `WIS-TASK-185`

---

## 1. Kontekst i Problemi

Nakon prelaska na paket `v2.18.0` / `v2.19.0` (`UI v2.2.0` → `v2.3.0`), laboratorijsko i radioničko testiranje otkrilo je pet kritičnih problema u Windows PowerShell 5.1 izvršnom sloju i orkestraciji faza:

1. **Zakašnjeli Windows Update u Fazi 6 i dupla instalacija BatteryBar-a:**
   - Windows Update drugi prolaz na kraju Faze 6 nepotrebno je produžavao čekanje pre predaje računara, dok su istovremeno postojali tragovi klasičnog NSIS DeskBand instalera i Floating varijante BatteryBar-a.
2. **PowerShell 5.1 ParserError zbog nedostajućeg UTF-8 BOM-a (`0x93`/`0x94` CP1252 bug):**
   - `WIS.Update.psm1` bez UTF-8 BOM zaglavlja čitan je od strane PowerShell 5.1 pod `Windows-1252`, gde su UTF-8 bajtovi `0x93` i `0x94` (unutar em-dash karaktera `—` ili kineskih znakova) tumačeni kao dvostruki navodnici `"` i rušili parsiranje modula (`Unexpected token 'zakrpa'`).
3. **Pad MSI instalacija u Fazi 4 usled ukidanja međurestarta (`Pending Reboot` brave):**
   - Privremeno uklanjanje međurestarta između Faze 3 i Faze 4 ostavljalo je aktivne `PendingFileRenameOperations`, `TrustedInstaller`/CBS i `msiexec` brave nakon PnP drajvera i Appx debloata, zbog čega Chrome i Adobe Acrobat MSI paketi nisu prolazili do kraja i `Assert-InstalledApplicationsReality` je obarao Fazu 4 na 98%.
4. **`System error 1219` i `ErrorRecord` prekid u `Get-ServisProgramiRoot`:**
   - `Phase4-Apps.ps1` nije uvozio `WIS.Core.psm1` (`Get-ServisNasSettings`), pa je prazna lozinka otvarala konkurentsku `guest` SMB sesiju na već autentifikovanom NAS hostu (`Error 1219` na stderr pod `$ErrorActionPreference = 'Stop'`).
5. **Pad Faze 4 na `P4.Office` (`WIS-TASK-185`):**
   - Korak `P4.Office` tražio je isključivo `configuration.xml` dok je na NAS-u naziv `Configure.xml` (pa je ODT `setup.exe` dobijao prazan `/configure`), originalni XML je imao UTF-8 BOM i UNC `SourcePath` (neupotrebljiv za `OfficeClickToRun.exe` servis koji radi pod `NT AUTHORITY\SYSTEM` bez korisničkih SMB akreditiva), a `Get-WisAppPresence` za `Shortcuts` sa pragom `-ge 2` preskakao je naknadno dodavanje `Word.lnk`/`Excel.lnk`.

---

## 2. Donete Odluke i Rešenja

### A. Pozadinski Windows Update Radnik i UI Status (`WIS.Update.psm1`, `MainWindow.xaml.cs`, `Models.cs`)
1. **`Start-BackgroundWindowsUpdateWorker` odmah nakon Faze 2:**
   - Po završetku Faze 2 pokreće se odvojeni PowerShell proces (`Invoke-BackgroundWindowsUpdateWorker`) koji fazu `Search` i `Download` izvršava u pozadini dok traju Faze 3–5, upisujući strukturirani napredak u `C:\setup\wu-bg-status.json` (`STARTING` → `SEARCHING` → `DOWNLOADING` → `WAITING_MSI` → `INSTALLING` → `COMPLETED`/`SKIPPED`/`WARN`).
   - Faza 6 proverava `wu-bg-status.json` i odmah preskače/preuzima završetak bez ponovnog punog skeniranja, dok `WIS.UI.exe` (`PollBackgroundWuStatusFile`) prikazuje statusni indikator u zaglavlju i na kartici Pregled.

### B. Isključivo Floating BatteryBar, UTF-8 BOM Standard i Očuvani Međurestart
1. **BatteryBar Solo (`UI/Skripte/Modules/WIS.Apps.psm1`, `WIS.Core.psm1`):**
   - Isključeni su klasični NSIS/DeskBand fajlovi (`BatteryBarSetup*.exe`, `ShowBatteryBar.exe`, `BatteryBar.dll`, `BarExplorerHook.dll`) pri sinhronizaciji i instalaciji; ostaje isključivo jedna Floating (`Solo`) konfiguracija (`BatteryBar.Settings.xml`, `.NET 4.8` `.config`) i jedna prečica u All Users `Startup` folderu.
2. **UTF-8 BOM i uklanjanje `0x93`/`0x94` (`UI/Skripte/**/*`):**
   - Sve `.ps1` i `.psm1` datoteke u `UI/Skripte/` standardizovane su sa UTF-8 BOM (`EF BB BF`) i bez spornih em-dash / multibyte stringova van C# resursa koji bi pokvarili CP1252 parser.
3. **Očuvani hibridni međurestart posle Faze 3 (`UI/MainWindow.xaml.cs`, `WIS.Branding.psm1`):**
   - Nakon Faze 3 (`phaseNumber == 3`) vraćen je `HandleInterimReboot()`, dok `StepGuard` dodatno proverava `Get-WisPendingRebootState` (`interim_reboot_required`) kako bi se u Fazu 4 uvek ulazilo sa čistim CBS/MSI okruženjem.

### C. Bezbedna SMB Autentifikacija u Fazi 4 i Kompletna `P4.Office` Sanacija (`Phase4-Apps.ps1`, `WIS.Apps.psm1`)
1. **Otklanjanje greške `1219` i lokalni Chrome keš:**
   - `Phase4-Apps.ps1` obavezno uvozi `WIS.Core.psm1`; `Get-ServisProgramiRoot` i `Sync-ProgramiFromNas` najpre proveravaju već postojeću SMB sesiju, a `net.exe use` izvršavaju pod `$ErrorActionPreference = 'Continue'` kroz `cmd.exe /c` uz čišćenje konflikata (`net use * /delete /y`) na `Error 1219`.
   - `GoogleChromeStandaloneEnterprise64.msi` sa UNC putanje prethodno se kešira u `C:\setup\Cache\Chrome\` pre poziva `msiexec.exe`.
2. **`P4.Office` ODT tok i bezbednosna higijena XML-a:**
   - Prioritizovan lokalni paket `C:\setup\Programi\Office 2024\files` (`setup.exe` + `Office\Data`); ako nije kompletan, automatski se poziva `Sync-ProgramiFromNas -DestinationDir "C:\setup\Programi" -Force`.
   - Traže se oba imena konfiguracije (`Configure.xml` i `configuration.xml`), uklanja se UTF-8 BOM, XML se parsira, `SourcePath` se obavezno preusmerava na verifikovani lokalni `$effectiveDataDir` na NVMe SSD-u, i zapisuje se čist runtime XML bez BOM-a u `C:\setup\Office2024_Install.xml` (bez hardkodiranog GVLK ključa u javnom fallback bloku).
   - ODT `setup.exe` čeka do `420s`, potom sledi C2R flush čekanje (`WINWORD.EXE`/`EXCEL.EXE` + registry) do `120s`, uz automatski ponovni pokušaj i `-Force` osvežavanje paketa ako izvršni fajlovi izostanu.
   - `Get-WisAppPresence -AppId "Shortcuts"` sada proverava svaku stvarno instaliranu aplikaciju pojedinačno (`Adobe Acrobat.lnk`, `Word.lnk`, `Excel.lnk`).

---

## 3. Posledice i Verifikacija

- **Stabilnost Faze 4 i PowerShell 5.1 kompatibilnost:** Sva `22` PowerShell fajla (uključujući `WIS.StepGuard.psm1` i `WIS.Apps.psm1`) prolaze `Verify-PowerShellSyntax.py` bez grešaka, a vraćeni međurestart + lokalni ODT `SourcePath` otklanjaju `Error 1219`, `1603` i `P4.Office` pad na `NT AUTHORITY\SYSTEM`.
- **Javna bezbednost koda:** U javnom repozitorijumu nema GVLK ključeva (`FXYTK-...`), niti internih akreditiva; opcione licence i hookovi ostaju lokalno u `Wifi-Servis.ini` i `CustomScripts\` van Git-a.
