---
title: "Windows Update, Single-Stream Drajveri i Samoučeća Berba (Windows Update & Driver Pipeline)"
type: architecture
created: 2026-09-14
updated: 2026-11-11
tags:
  - architecture
  - windows-update
  - drivers
  - com-api
  - nas
---

# Windows Update, Single-Stream Drajveri i Dvosmerna Berba: WIS (v3.0.0)

Faza 2 (`Phase2-Drivers.ps1` — **83 linije**, `3,306 B`, i `WIS.Update.psm1` — **4,177 linija**, `208,224 B`) autonomno rešava instalaciju svih hardverskih drajvera (čipset, grafika, audio, Wi-Fi, senzori, touchpad) i kumulativnih zakrpa kroz **6 idempotentnih `StepGuard` koraka** (`P2.*` — [[decisions/ADR-0181-stepguard-precheck-hardware-identity-and-ui-v23]], [[decisions/ADR-0225-wis-v300-embedded-hwtest-bidirectional-wu-and-preoobe-autopilot]]), kombinovanjem dvosmernog HTTP/NAS keša (`ServiceHub` + TrueNAS ZFS), Windows Update COM API-ja i OEM provajdera.

---

## 🚀 1. Lokalni NAS Pre-Patch, Single-Stream `<Model>.tar` i `StepGuard` Koraci (`P2.*`)

1. **`P2.NasPrePatch` & `P2.ModelDrivers` (`Invoke-NasUpdatePrePatch`, `Invoke-ModelDriversPreInstall`):**
   - Proverava `ServiceHub` HTTP keš (`/api/updates/status`, `/api/updates/download`, uz `OSBuild` proveru i SHA-256 verifikaciju preko `index.json`) i NAS deljenje `\\192.168.170.254\Servis\Updates\Win11` (u skladu sa `[WindowsUpdate] Mode=All`); preuzete `.msu`/`.cab` pakete može dvosmerno otpremiti nazad na hub (`/api/updates/upload`, upravljanje kešom `/api/updates/cache-kb` i `/api/updates/prune`).
   - Za poznate modele preuzima jedinstveni `<Model>.tar` iz TrueNAS ZFS ARC RAM keša brzinom **~110 MB/s** (~2s), raspakuje ga lokalno pomoću `tar.exe -xf` za `< 1 s` i instalira preko `pnputil.exe /add-driver ... /install`, čime se izbegava „SMB Small-File Latency Storm" ([[decisions/ADR-0035-fleet-network-acceleration-tar-streaming]]).
2. **Idempotentnost i Device Manager Verifikacija (`Test-WisDeviceManagerClean`):**
   - Svih 6 koraka Faze 2 evidentira se u `C:\setup\step-journal.json` (`Send-IpcStep` u UI ekspanderu).
   - `P2.WuDrivers` primenjuje **6-satni TTL** na već uspešno završen WU prolaz pri ponovnom pokretanju, dok `Test-WisDeviceManagerClean` u `WIS.Hardware.psm1` autoritativno potvrđuje odsustvo uređaja sa greškama (`ConfigManagerErrorCode != 0`).

---

## ⚙️ 2. Nativni Windows Update COM API i Pozadinski Radnik (`wu-bg-status.json`)

Za preostale drajvere i zakrpe, `WIS.Update.psm1` koristi ugrađeni COM interfejs `Microsoft.Update.Session`:
- Pretražuje `IsInstalled=0 and (Type='Driver' or Type='Software')` kroz do **3 iterativna kruga (`MaxCycles = 3`)**.
- Primenjuje **Batch Download** uz aktiviran **Delivery Optimization LAN Peering** (`DODownloadMode = 1`) i **pozadinski WU radnik** koji upisuje progres u `C:\setup\wu-bg-status.json` (`PollBackgroundWuStatusFile()` u `WIS.UI.exe` uživo prikazuje napredak preuzimanja/instalacije u UI-ju).
- **Tolerantno rukovanje HRESULT kodovima (Zamka 31):** Kodovi `0x8024200b` (`WU_E_LEGACYOPERATIONRESULT`) i `0x80070103` (`ERROR_ALREADY_EXISTS`) tretiraju se kao benigno `INFO` stanje uz `ExitCode = 0`.

---

## 🛡️ 3. ServiceHub OEM Trozubac i Microsoft Update Catalog Fallback (`Install-DriverFromMsCatalog`)

Ukoliko nakon Windows Update prolaza u Device Manager-u i dalje postoje uređaji sa greškom (`ConfigManagerErrorCode != 0`):
- Klijent šalje spisak HWID identifikatora, proizvođača i modela na `ServiceHub` (`POST /api/drivers/resolve`).
- `ServiceHub` normalizuje ACPI, HDAudio, PCI i USB identifikatore (`servishub/drivers/hwid.py`), proverava OEM pakete (Lenovo SCCM, Dell Command | Deploy, HP SoftPaq) i pretražuje Microsoft Update Catalog, keširajući `.cab` drajvere na TrueNAS-u i isporučujući ih klijentu.

---

## 🧠 4. Samoučeća Berba Drajvera (`Export-ServisHardwareDrivers`)

Kada su svi drajveri na novom modelu računara uspešno instalirani:
- `Export-ServisHardwareDrivers` (sa UI statusom **`HARVESTING`** tokom pakovanja) ekstrahuje sve 3rd-party drajvere (`pnputil.exe /export-driver`), upisuje anonimizovani `model_meta.json` (proizvođač, komercijalni naziv modela i MTM/SKU, **bez serijskih brojeva klijentskih uređaja**) u `Drivers\Harvested\Models\<Model>\` (`ShareName=Servis`, `DriversSubDir=Drivers\Harvested\Models`), pakuje `<Model>.tar` i sinhronizuje ga dvosmerno preko ServiceHub HTTP keša (`/api/drivers/model-pack/status`, `/api/drivers/model-pack/download`, `/api/drivers/model-pack/upload`), uz batch HWID razrešavanje preko `/api/drivers/resolve`.
- Svaki naredni računar istog modela u servisu dobija kompletne drajvere za **~3 sekunde** bez preuzimanja sa interneta.

---

## ⏳ 5. WU Barijera u Fazi 5 i Drugi Prolaz u Fazi 6

- **Faza 5 (`Wait-WindowsUpdateCompletionBarrier` & Integrisani HwTest):** Pre pokretanja termalnog opterećenja čeka da se pozadinski `TiWorker.exe`, `TrustedInstaller.exe` i `MoUsoCoreWorker.exe` procesi završe (timeout 15 min) kako ne bi remetili merenje temperatura ([[decisions/ADR-0129-hardware-diagnostics-wu-barrier-and-i18n]]). U `WIS v3.0.0`, Faza 5 se izvršava integrisano unutar `WIS.UI.exe` (`ExecutePhase5IntegratedHwTest`, `45 min`), dok `Phase5-Stress.ps1` ostaje dostupan za CI/fallback.
- **Faza 6 (`Invoke-WindowsUpdateSecondPass`):** Asinhroni `Start-Job` (timeout 600s) hvata zakrpe koje su postale primenjive tek nakon aktivacije čipset i grafičkih drajvera.

---

## 🔗 Povezane Beleške
- Pregled Arhitekture: [[architecture/overview]]
- Pajplajn Izvršavanja: [[architecture/pipeline]]
- StepGuard i UI v2.3.0: [[decisions/ADR-0181-stepguard-precheck-hardware-identity-and-ui-v23]]
- Dvosmerni WU i Driver Keš v3.0.0: [[decisions/ADR-0225-wis-v300-embedded-hwtest-bidirectional-wu-and-preoobe-autopilot]]
- Mrežno Ubrzanje i Single-Stream `.tar` Drajveri: [[decisions/ADR-0035-fleet-network-acceleration-tar-streaming]]
- Samoučeća Berba Drajvera: [[decisions/ADR-0028-universal-driver-harvesting-and-nas-sync]]
