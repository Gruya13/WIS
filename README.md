# Windows Installation Assistant (WIS) v3.0.0

**WIS (Windows Installation Assistant)** je celovit, automatizovan **Zero-Touch** sistem za instalaciju operativnog sistema **Windows 11 Home / Pro (24H2+ / Build 10.0.26200.8037)**, mrežnu iVentoy PXE i USB distribuciju, integrisanu automatsku i interaktivnu hardversku dijagnostiku (`WIS.HwTest`), Microsoft Autopilot/MDM forenziku, idempotentnu instalaciju drajvera i aplikacija (`StepGuard`), dvosmerni HTTP/NAS Windows Update i driver keš, termalnu štampu servisnih nalepnica (Star TSP700II), kao i centralizovani nadzor flote računara.

> **Status verzije (`version.json`):** Paket izdanja **`3.0.0`** (`release_status: development`, `windows_validation: pending`) — donosi integraciju `WIS.HwTest` direktno kao **9. tab** u `WIS.UI.exe` i jedinstvenu Fazu 5 (`45 min`), termalnu štampu Star TSP700II nalepnica (`LabelPrintService.cs`), dvosmerni WU & HTTP `<Model>.tar` keš (`Mode=All`, `ShareName=Servis`), Pre-OOBE Autopilot ZTD hvatanje sa zaštitom keša (`AutopilotAuditor v3.0`) i `ServiceHub v3.3.0`.

---

## Arhitektura Sistema (Četiri Razvojne Celine + Alati)

Kanonski izvor verzija svih komponenti nalazi se u [`version.json`](version.json). Projekat je organizovan u četiri glavne razvojne celine i namenski forenzički alat (pri čemu izvršni raspored na klijentu u `C:\setup\` ostaje nepromenjen):

| Celina / Alat | Verzija | Tehnologija | Opis i Uloga |
| :--- | :--- | :--- | :--- |
| **[`ISO/`](ISO/)** | `3.0.0` | PowerShell 5.1, Python 3, Unattend XML, WinPE Batch | Izgradnja **Ultra-Lean Solid LZMS** Windows 11 ISO medijuma, Dual-Index `boot.wim` sa LAN-only `/Drivers/Net`, `000_wis_net1ix64.inf_guard` zaštita za Intel I219-LM (`DEV_15FB`), Intel VMD detekcija, Pre-OOBE bootstrap (`PreOOBE-Network.ps1`, `SetupComplete.ps1` korak `6b`, `Provera_autopilota.ps1`), WinPE SMB 3.1.1 akceleracija, `Build-WIS.ps1` i RSA-2048 potpisano izdanje `Release-WIS.py` (`wis-payload.zip`, `78` fajlova, `68,475,013 B`). |
| **[`Launcher/`](Launcher/)** | `3.0.0` (`min: 2.2.17`) | C# 5.0 / .NET Framework 4.0 (`6` prod. + `1` test fajl) | **`WIS.Launcher.exe`** (`468,480 B`) — Samostalni *Thin-Client* pokretač ugrađen u `$OEM$`. Povezuje Wi-Fi, otkriva `ServiceHub` (UDP `8888`) i SMB deljenja (`Servis` / `WIS`), verifikuje RSA-2048 potpis i SHA-256 sume `wis-payload.zip` paketa, vrši transakcioni rollback (`.update_tx.json`), deblokira `:Zone.Identifier` i proverava postojeći `WIS.UI` proces pre pokretanja. |
| **[`UI/`](UI/)** | `3.0.0` | C# WPF (.NET 4.8 + `v4.0` fallback) + PowerShell 5.1 (`13` modula, `6` faza) | **`WIS.UI.exe`** (`2,977,280 B`) i **`WIS.HwTest.exe`** (`1,736,704 B`) — Trojezični (**SRB `435` / 中文 `435` / EN `73`** ključa) kontrolni panel sa **9 tabova** (uključujući integrisani `🧪 Hardverski testovi` tab sa **7 automatskih** + **9 interaktivnih** testova i 12 OEM šablona tastature), `LabelPrintService.cs` štampom nalepnica (Star TSP700II), `StepGuard` mehanizmom, `ThermalSensorEngine` refleksijom bez ugrađenog `WinRing0`, dvosmernim WU/driver klijentom i IPC kanalom. |
| **[`ServiceHub/`](ServiceHub/)** | `3.3.0` | Python 3.10+ (`35` modula, 0 pip zavisnosti), Docker | Centralni nadzorni i distribucijski server na TCP `8080` i UDP `8888` (`279` unit testova). Pruža Web Cockpit, 1 Hz Live Screen Mirror, dvosmerni HTTP WU keš (`/api/updates/*`), dvosmerni `<Model>.tar` keš (`/api/drivers/model-pack/*`), profile modela (`/api/model-profile*`), rasporede tastature (`/api/keyboard-layout`), hardverske specifikacije (`/api/hardware-spec`), OEM provajdere drajvera i USB Builder. |
| **[`Tools/AutopilotAuditor/`](Tools/AutopilotAuditor/)** | `3.0.0` | C# (.NET Framework 4.8, `8` fajlova, `2,333` lin.) | **`WIS-AutopilotAuditor.exe`** (`447,488 B`) — Samostalni troslojni forenzički alat za proveru Microsoft Intune / Autopilot ZTD statusa u registru, UEFI/SMBIOS-u i Microsoft oblaku (GUI, `--cli`, `--json`, `--export-html`, `--export-hash`), tretman praznog `AutopilotDDSZTDFile.json` kao `ConfirmedFree` i Pre-OOBE zaštitu `autopilot_report.json` keša, uz `Provera_autopilota.ps1` (`974` lin.). |

---

## 6-Fazni Servisni Pajplajn (`UI/Skripte/Phases/`)

`WIS.UI.exe` orkestrira **6 procesno izolovanih PowerShell 5.1 faza** sa pojedinačnim zaštitnim tajmautima i `WIS.StepGuard.psm1` kontrolom (21 atomski `Invoke-WisStep` korak kroz Faze 2, 3 i 4):

1. **Faza 1 — Mreža i Servisni Wi-Fi (`Phase1-Network.ps1`, 15 min):** Fast-Pass mrežna provera, PnP fallback mrežnih drajvera, hardverska/S.M.A.R.T. trijaža, učitavanje ili auto-retry Pre-OOBE Autopilot ZTD keša (`autopilot_report.json`) i `STARTED` telemetrija.
2. **Faza 2 — Drajveri i Windows Update (`Phase2-Drivers.ps1`, 60 min):** 6 `StepGuard` koraka (`P2.Identify` .. `P2.WuDrivers`): dvosmerni HTTP/SMB WU keš (`Mode=All`, `OSBuild` + SHA-256 `index.json`, `/api/updates/*`), dvosmerni `<Model>.tar` driver keš (`/api/drivers/model-pack/*`, `ShareName=Servis`, `DriversSubDir=Drivers\Harvested\Models`), batch `/api/drivers/resolve`, selektivni WU COM API, status `HARVESTING` pri pakovanju novih drajvera (`Export-ServisHardwareDrivers`) i pozadinski WU radnik (`wu-bg-status.json`).
3. **Faza 3 — Sistemska Optimizacija i Brendiranje (`Phase3-Optimization.ps1`, 20 min):** 6 `StepGuard` koraka (`P3.Debloat` .. `P3.UsbFree`): uklanjanje bloatware-a, gašenje telemetrije, OEM brendiranje, automatsko DPI skaliranje ekrana, Visual C++ AIO runtimes i autoritativna provera stanja restarta (`Get-WisPendingRebootState`), nakon čega `WIS.UI.exe` vrši kontrolisani međurestart (`HandleInterimReboot`) sa HKLM-only `RunOnce` upisom (`WIS.Launcher.exe` prioritet).
4. **Faza 4 — Programi i OEM Licenca (`Phase4-Apps.ps1`, 60 min):** 9 `StepGuard` koraka (`P4.Runtimes` .. `P4.Activation`): rani uvoz `WIS.Core.psm1`, sekvencijalna `Global\_MSIExecute` zaštita, lokalni Chrome MSI keš, Acrobat Pro 2024 + 6-stepeni PDF lock, Microsoft Office 2024 LTSC (ODT pretraga `Configure.xml`/`configuration.xml`, uklanjanje UTF-8 BOM-a u `C:\setup\Office2024_Install.xml` sa lokalnim SSD `SourcePath`-om radi `SYSTEM` C2R kompatibilnosti i `-Force` fallback), WinRAR, BatteryBar Solo za Win11 (`SKIPPED` na desktopu), prečice, `Assert-InstalledApplicationsReality`, OEM OA3x aktivacija i `[↻ Ponovi Fazu 4]` kontrola u UI-ju.
5. **Faza 5 — Ugrađeni Hardverski Testovi & Stres Dijagnostika (`ExecutePhase5IntegratedHwTest` / `WIS.HwTest`, 45 min):** Objedinjeni tok unutar `WIS.UI.exe` (`MainWindow.HwTest.cs` / `StartPhase5FullWorkflow`: `P5.AutoDiag` 7 automatskih testova -> `P5.Stress` sa `ThermalSensorEngine` refleksijom i `_baseMaxClockMHz` korekcijom bez ugrađenog `WinRing0` -> `P5.Peripherals` 9 interaktivnih testova), uz `Phase5-Stress.ps1` za CI/fallback režim (`WIS_STRESS_TEST_INPUT_JSON`) i `WIS.StatusContract.psm1` politiku.
6. **Faza 6 — Finalizacija, Štampa Nalepnica i Anti-Leak Čišćenje (`Phase6-Finalize.ps1`, 45 min):** Asinhroni WU Pass 2 (timeout 600s), gašenje Screen Mirror radnika, slanje DMTF servisnog kartona na `ServiceHub`, opciona termalna štampa nalepnice na Star TSP700II (`LabelPrintService.cs`) sa `HW test` rezimeom, trajno uklanjanje `Wifi-Servis.ini` i servisnih WLAN profila, vraćanje UAC-a (`5`/`1`) i samouništenje `C:\setup`.

---

## Brzi Početak i Konfiguracija

### 1. Priprema `Wifi-Servis.ini` konfiguracije
Kopirajte šablon `Wifi-Servis.example.ini` u `ISO/Wifi-Servis.ini` (i po potrebi `ISO/Skripte/Wifi-Servis.ini`) i unesite parametre servisne mreže i SMB servera:

```bash
cp Wifi-Servis.example.ini ISO/Wifi-Servis.ini
```

> **Napomena o bezbednosti:** Datoteka `Wifi-Servis.ini` i direktorijum `ISO/sources/` nalaze se u `.gitignore` i nikada se ne prate u Git repozitorijumu.

### 2. Pokretanje `ServiceHub` servera (Linux / TrueNAS Docker)
```bash
cd ServiceHub
python3 main.py --port 8080
# ili putem Docker Compose-a:
docker compose up -d --build
```

### 3. Build Windows binarnih komponenti, Release pakovanje i ISO medijum
- Na čistom Git commitu pod **Windows PowerShell 5.1** i MSBuild-om:
  ```powershell
  powershell -ExecutionPolicy Bypass -File .\ISO\Skripte\Build-WIS.ps1
  ```
- Za potpisivanje OTA paketa (zahteva čist Git commit, `build-attestation.json` i PASS izveštaj namenske Windows VM za isti commit) koristi se `ISO/Skripte/Release-WIS.py publish ...`, dok se Linux rekompilacija `UI`, `HwTest`, `Launcher` i `AutopilotAuditor` slojeva i izrada hibridnog UDF/UEFI/BIOS ISO medijuma obavljaju kroz `UI/Build-UI.py`, `UI/HwTest/Build-HwTest.py`, `Launcher/Build-Launcher.py`, `Tools/AutopilotAuditor/Build-AutopilotAuditor.py`, `ISO/Skripte/Patch-BootWim.py` i `ISO/Skripte/Build-Iso.py`.

---

## Verifikacija i Testovi

### 1. Linux provere (statička kontrola, verzionisanje i jedinični/behavioral testovi)
```bash
# Usklađenost verzija sa version.json
python3 ISO/Skripte/Sync-Versions.py --check

# Pomoćna Linux statička analiza PowerShell 5.1 (22 fajla) i C# 5.0 sintakse
# (ne zamenjuje stvarni Windows Parser.ParseFile niti MSBuild kompilaciju)
python3 ISO/Skripte/Verify-PowerShellSyntax.py
python3 ISO/Skripte/Verify-CSharpSyntax.py

# Launcher behavioral testovi u Mono okruženju (31/31 PASS)
python3 Launcher/Tests/run_tests.py

# UI / HwTest / Pre-OOBE Autopilot / WU & Driver jedinica testovi (29 testova)
python3 -m unittest UI/Tests/Test-HwTestLogic.py UI/Tests/Test-AutopilotPreOobeCache.py UI/Tests/Test-UpdateAndDriversLogic.py

# ServiceHub unittest paket (279 testova ukupno: 259 PASS, 20 skipped kada opciono Pillow/FastAPI/Node nisu prisutni)
cd ServiceHub && python3 -m unittest discover -s tests
```

### 2. Windows i laboratorijski testovi (`NOT_RUN` van Windows/PXE laboratorije)
- **Windows PowerShell 5.1 & E2E skripte ([`UI/Tests/`](UI/Tests/)):** `Test-PowerShell51Parse.ps1`, `Test-StatusContract.ps1`, `Test-StepGuard.ps1`, `Test-InstalledWindowsE2E.ps1`, `Test-DisposableWindowsVM.ps1` — izvršavaju se isključivo u Windows okruženju / namenskoj VM.
- **PXE Zero-Touch matrica prihvatanja ([`ISO/Tests/PxeZeroTouch/`](ISO/Tests/PxeZeroTouch/)):** Definiše ugovor prihvatanja sa 22 scenarija (`PXE-01`..`PXE-22` u `acceptance.json`) i validatorom `verify_acceptance.py`; zahteva generisan `LOCK.json` i stvarne laboratorijske logove sa fizičkog/PXE testiranja.

---

## Dokumentacija

Detaljna tehnička specifikacija i zapisi o arhitektonskim odlukama nalaze se u [`docs/`](docs/):
- **[Tehnički opis sistema WIS v3.0.0](docs/TEHNICKI_OPIS_SISTEMA_WIS.md)** — Kompletna inženjerska specifikacija svih podsistema, ugovora i SRE zamki.
- **[ADR-0189: Hardverski test suite `WIS.HwTest` i ServiceHub profili modela](docs/decisions/ADR-0189-manual-hardware-test-suite.md)** — 7 automatskih i 9 interaktivnih testova, 12 OEM šablona tastatura, integracija sa `WIS.UI.exe`.
- **[ADR-0225: WIS v3.0.0 Arhitektonske Odluke](docs/decisions/ADR-0225-wis-v300-embedded-hwtest-bidirectional-wu-and-preoobe-autopilot.md)** — Ugrađeni 9. tab i objedinjena Faza 5 (`45 min`), Star TSP700II štampa, dvosmerni WU & `<Model>.tar` keš i Pre-OOBE Autopilot keš.
- **[Arhitektura podsistema (`docs/architecture/`)](docs/architecture/overview.md)** — Detaljni pregledi pajplajna, WinPE drajvera, Windows Update COM API-ja, tihih instalacija, modula, mreže i ServiceHub-a.
- **[Arhitektonske odluke (`docs/decisions/`)](docs/decisions/)** — Zbirka ADR dokumenata (`ADR-0001` do `ADR-0185`), uključujući novije odluke:
  - [`ADR-0181`](docs/decisions/ADR-0181-stepguard-precheck-hardware-identity-and-ui-v23.md) — `StepGuard` pre-provera, `HardwareIdentityService`, `DeviceChangeMonitor`, `UI v2.3.0`, HKLM-only `RunOnce` i procesna single-instance zaštita.
  - [`ADR-0184`](docs/decisions/ADR-0184-winpe-i219lm-driverstore-guard-and-parallel-pxe.md) — WinPE `000_wis_net1ix64.inf_guard` (`DEV_15FB`), LAN-only `boot.wim`, bezbedno SMB 3.1.1 montiranje i paralelni PXE rad.
  - [`ADR-0185`](docs/decisions/ADR-0185-phase4-office-odt-bom-free-and-local-nvme-sourcepath.md) — Pozadinski WU status, UTF-8 BOM za PS 5.1, očuvani međurestart, lokalni Chrome/Office NVMe `SourcePath` i BOM-free XML za `NT AUTHORITY\SYSTEM`.

---

## Licenca

Ovaj projekat je objavljen pod [MIT licencom](LICENSE).
