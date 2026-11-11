# Windows Installation Assistant (WIS) v2.19.0

**WIS (Windows Installation Assistant)** je celovit, automatizovan **Zero-Touch** sistem za instalaciju operativnog sistema **Windows 11 Home / Pro (24H2 / Build 10.0.26200.8037)**, mrežnu iVentoy PXE i USB distribuciju, hardversku dijagnostiku, Microsoft Autopilot/MDM forenziku, idempotentnu instalaciju drajvera i aplikacija (`StepGuard`), kao i centralizovani nadzor flote računara.

> **Status verzije (`version.json`):** Paket izdanja **`2.19.0`** (`release_status: development`, `windows_validation: pending`) — donosi ključne arhitektonske nadogradnje preko izdanja `v2.17.0` (WinPE `000_wis_net1ix64.inf_guard`, LAN-only `boot.wim`, `UI v2.3.0` sa `StepGuard` sistemom, pozadinskim WU radnikom, `HardwareIdentityService`/`DeviceChangeMonitor` motorom, HKLM-only `EnsureRunOnceEntry` i lokalnom SSD ODT instalacijom za Microsoft Office 2024).

---

## Arhitektura Sistema (Četiri Razvojne Celine + Alati)

Kanonski izvor verzija svih komponenti nalazi se u [`version.json`](version.json). Projekat je organizovan u četiri glavne razvojne celine i namenski forenzički alat (pri čemu izvršni raspored na klijentu u `C:\setup\` ostaje nepromenjen):

| Celina / Alat | Verzija | Tehnologija | Opis i Uloga |
| :--- | :--- | :--- | :--- |
| **[`ISO/`](ISO/)** | `2.18.0` | PowerShell 5.1, Python 3, Unattend XML, WinPE Batch | Izgradnja **Ultra-Lean Solid LZMS** Windows 11 ISO medijuma (`~6.07 GB`), Dual-Index `boot.wim` (`~582 MiB` sa LAN-only `/Drivers/Net`), `000_wis_net1ix64.inf_guard` zaštita za Intel I219-LM (`DEV_15FB`), Intel VMD Gen 10–14 detekcija, WinPE SMB 3.1.1 akceleracija (`WIS-AcceleratePE.cmd`, `WIS-ConnectNAS.cmd`), `Build-WIS.ps1` i `Release-WIS.py`. |
| **[`Launcher/`](Launcher/)** | `2.2.17` | C# 5.0 / .NET Framework 4.0 (`6` prod. + `1` test fajl) | **`WIS.Launcher.exe`** — Samostalni *Thin-Client* pokretač ugrađen u `$OEM$`. Povezuje Wi-Fi, otkriva `ServiceHub` (UDP `8888`) i SMB deljenja (sa prioritetom `\\host\servis` manifesta), verifikuje RSA-2048 potpis i SHA-256 sume `wis-payload.zip` paketa, vrši Zip-Slip i transakcioni rollback (`.update_tx.json`), deblokira `:Zone.Identifier` i proverava postojeći `WIS.UI` proces pre pokretanja. |
| **[`UI/`](UI/)** | `2.3.0` | C# WPF (.NET 4.8 + `v4.0` fallback) + PowerShell 5.1 (`13` modula, `6` faza) | **`WIS.UI.exe`** — Trojezični (**SRB `414` / 中文 `414` / EN `72`** ključa) kontrolni panel sa **8 tabova**, `WIS.StepGuard.psm1` mehanizmom (`step-journal.json` + `Expander` koraci), `HardwareIdentityService` i `DeviceChangeMonitor` motorima, obrnutim prikazom dnevnika, pozadinskim WU indikatorom (`wu-bg-status.json`), 30s `RunspacePool` stres testom i IPC kanalom. |
| **[`ServiceHub/`](ServiceHub/)** | `3.2.0` | Python 3.10+ (`32` modula, 0 pip zavisnosti), Docker | Centralni nadzorni i distribucijski server na TCP `8080` i UDP `8888`. Pruža **Modern Enterprise Dark Mode Cockpit**, 1 Hz Live Screen Mirror u RAM-u (5s TTL), OEM provajdere drajvera (Lenovo, Dell, HP), MS Update Catalog scraper, DMTF servisne kartone i UEFI USB Builder sa ZFS/Disk zaštitom. |
| **[`Tools/AutopilotAuditor/`](Tools/AutopilotAuditor/)** | `2.0.0` | C# (.NET Framework 4.8, `8` fajlova) | **`WIS-AutopilotAuditor.exe`** — Samostalni troslojni forenzički alat za proveru Microsoft Intune / Autopilot ZTD statusa u registru, UEFI/SMBIOS-u i Microsoft oblaku (GUI, `--cli`, `--json`, `--export-html`, `--export-hash`), uz PowerShell fallback `Provera_autopilota.ps1`. |

---

## 6-Fazni Servisni Pajplajn (`UI/Skripte/Phases/`)

`WIS.UI.exe` orkestrira **6 procesno izolovanih PowerShell 5.1 faza** sa pojedinačnim zaštitnim tajmautima i `WIS.StepGuard.psm1` kontrolom (21 atomski `Invoke-WisStep` korak kroz Faze 2, 3 i 4):

1. **Faza 1 — Mreža i Servisni Wi-Fi (`Phase1-Network.ps1`, 15 min):** Fast-Pass mrežna provera, PnP fallback mrežnih drajvera, hardverska/S.M.A.R.T. trijaža, Autopilot forenzika i `STARTED` telemetrija.
2. **Faza 2 — Drajveri i Windows Update (`Phase2-Drivers.ps1`, 60 min):** 6 `StepGuard` koraka (`P2.Identify` .. `P2.WuDrivers`): NAS `.msu` Pre-Patch, Single-Stream `<Model>.tar` drajveri (~3s sa TrueNAS-a), selektivni Windows Update COM API (sa TTL od 6h), MS Catalog OEM fallback, berba novih drajvera (`Export-ServisHardwareDrivers`) i pokretanje pozadinskog WU radnika (`wu-bg-status.json`).
3. **Faza 3 — Sistemska Optimizacija i Brendiranje (`Phase3-Optimization.ps1`, 20 min):** 6 `StepGuard` koraka (`P3.Debloat` .. `P3.UsbFree`): uklanjanje bloatware-a, gašenje telemetrije, OEM brendiranje, automatsko DPI skaliranje ekrana, Visual C++ AIO runtimes i autoritativna provera stanja restarta (`Get-WisPendingRebootState`), nakon čega `WIS.UI.exe` vrši kontrolisani međurestart (`HandleInterimReboot`) sa HKLM-only `RunOnce` upisom (`WIS.Launcher.exe` prioritet).
4. **Faza 4 — Programi i OEM Licenca (`Phase4-Apps.ps1`, 60 min):** 9 `StepGuard` koraka (`P4.Runtimes` .. `P4.Activation`): rani uvoz `WIS.Core.psm1`, sekvencijalna `Global\_MSIExecute` zaštita, lokalni Chrome MSI keš, Acrobat Pro 2024 + 6-stepeni PDF lock, Microsoft Office 2024 LTSC (ODT pretraga `Configure.xml`/`configuration.xml`, uklanjanje UTF-8 BOM-a u `C:\setup\Office2024_Install.xml` sa lokalnim SSD `SourcePath`-om radi `SYSTEM` C2R kompatibilnosti i `-Force` fallback), WinRAR, BatteryBar Solo za Win11 (`SKIPPED` na desktopu), prečice, `Assert-InstalledApplicationsReality`, OEM OA3x aktivacija i `[↻ Ponovi Fazu 4]` kontrola u UI-ju.
5. **Faza 5 — Stres Test i Dijagnostika (`Phase5-Stress.ps1`, 5 min):** Čekanje pozadinske WU barijere, 30-sekundni Multi-Core `RunspacePool` CPU stres test sa `98°C` Emergency Abort osiguračem, `WIS.StatusContract.psm1` ugovor (stvarno pregrevanje blokira predaju sa `exit 2`; `UNKNOWN` nedostatak senzora ne blokira instalaciju i postavlja `manual_check_required = true`).
6. **Faza 6 — Finalizacija i Anti-Leak Čišćenje (`Phase6-Finalize.ps1`, 45 min):** Asinhroni WU Pass 2 (timeout 600s), gašenje Screen Mirror radnika, slanje DMTF servisnog kartona na `ServiceHub`, trajno uklanjanje `Wifi-Servis.ini` i servisnih WLAN profila, vraćanje UAC-a na podrazumevane vrednosti (`5`/`1`) i samouništenje `C:\setup`.

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
- Za potpisivanje OTA paketa (zahteva čist Git commit, `build-attestation.json` i PASS izveštaj namenske Windows VM za isti commit) koristi se `ISO/Skripte/Release-WIS.py publish ...`, dok se Linux rekompilacija `UI` sloja i izrada hibridnog UDF/UEFI/BIOS ISO medijuma obavljaju kroz `UI/Build-UI.py`, `ISO/Skripte/Patch-BootWim.py` i `ISO/Skripte/Build-Iso.py`.

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

# ServiceHub unittest paket (259 testova ukupno: 239 PASS, 20 skipped kada opciono Pillow/FastAPI/Node nisu prisutni)
cd ServiceHub && python3 -m unittest discover -s tests
```

### 2. Windows i laboratorijski testovi (`NOT_RUN` van Windows/PXE laboratorije)
- **Windows PowerShell 5.1 & E2E skripte ([`UI/Tests/`](UI/Tests/)):** `Test-PowerShell51Parse.ps1`, `Test-StatusContract.ps1`, `Test-StepGuard.ps1`, `Test-InstalledWindowsE2E.ps1`, `Test-DisposableWindowsVM.ps1` — izvršavaju se isključivo u Windows okruženju / namenskoj VM.
- **PXE Zero-Touch matrica prihvatanja ([`ISO/Tests/PxeZeroTouch/`](ISO/Tests/PxeZeroTouch/)):** Definiše ugovor prihvatanja sa 22 scenarija (`PXE-01`..`PXE-22` u `acceptance.json`) i validatorom `verify_acceptance.py`; zahteva generisan `LOCK.json` i stvarne laboratorijske logove sa fizičkog/PXE testiranja.

---

## Dokumentacija

Detaljna tehnička specifikacija i zapisi o arhitektonskim odlukama nalaze se u [`docs/`](docs/):
- **[Tehnički opis sistema WIS v2.19.0](docs/TEHNICKI_OPIS_SISTEMA_WIS.md)** — Kompletna inženjerska specifikacija svih podsistema, ugovora i SRE zamki.
- **[Arhitektura podsistema (`docs/architecture/`)](docs/architecture/overview.md)** — Detaljni pregledi pajplajna, WinPE drajvera, Windows Update COM API-ja, tihih instalacija, modula, mreže i ServiceHub-a.
- **[Arhitektonske odluke (`docs/decisions/`)](docs/decisions/)** — Zbirka ADR dokumenata (`ADR-0001` do `ADR-0185`), uključujući novije odluke:
  - [`ADR-0181`](docs/decisions/ADR-0181-stepguard-precheck-hardware-identity-and-ui-v23.md) — `StepGuard` pre-provera, `HardwareIdentityService`, `DeviceChangeMonitor`, `UI v2.3.0`, HKLM-only `RunOnce` i procesna single-instance zaštita.
  - [`ADR-0184`](docs/decisions/ADR-0184-winpe-i219lm-driverstore-guard-and-parallel-pxe.md) — WinPE `000_wis_net1ix64.inf_guard` (`DEV_15FB`), LAN-only `boot.wim`, bezbedno SMB 3.1.1 montiranje i paralelni PXE rad.
  - [`ADR-0185`](docs/decisions/ADR-0185-phase4-office-odt-bom-free-and-local-nvme-sourcepath.md) — Pozadinski WU status, UTF-8 BOM za PS 5.1, očuvani međurestart, lokalni Chrome/Office NVMe `SourcePath` i BOM-free XML za `NT AUTHORITY\SYSTEM`.

---

## Licenca

Ovaj projekat je objavljen pod [MIT licencom](LICENSE).
