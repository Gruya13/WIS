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

# Windows Update, Single-Stream Drajveri i Samoučeća Berba: WIS (v2.19.0)

Faza 2 (`Phase2-Drivers.ps1` i `WIS.Update.psm1`, **2,801 linija**, `144,104 B`) autonomno rešava instalaciju svih hardverskih drajvera (čipset, grafika, audio, Wi-Fi, senzori, touchpad) i kumulativnih zakrpa kroz **6 idempotentnih `StepGuard` koraka** (`P2.*` — [[decisions/ADR-0181-stepguard-precheck-hardware-identity-and-ui-v23]]), kombinovanjem lokalnog TrueNAS ZFS keša, Windows Update COM API-ja i `ServiceHub` OEM proksija.

---

## 🚀 1. Lokalni NAS Pre-Patch, Single-Stream `<Model>.tar` i `StepGuard` Koraci (`P2.*`)

1. **`P2.NasPrePatch` & `P2.ModelDrivers` (`Invoke-NasUpdatePrePatch`, `Invoke-ModelDriversPreInstall`):**
   - Proverava `\\192.168.170.254\WIS\Updates\Win11` za kumulativne Windows 11 zakrpe (poštuje `[WindowsUpdate] Mode=DriversOnly`).
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
- `Export-ServisHardwareDrivers` ekstrahuje sve 3rd-party drajvere (`pnputil.exe /export-driver`), upisuje anonimizovani `model_meta.json` (proizvođač, komercijalni naziv modela i MTM/SKU, **bez serijskih brojeva klijentskih uređaja**) u `Drivers\Harvested\Models\<Model>\` i pakuje `<Model>.tar` u `Drivers\Harvested\Archives\`.
- Svaki naredni računar istog modela u servisu dobija kompletne drajvere za **~3 sekunde** bez preuzimanja sa interneta.

---

## ⏳ 5. WU Barijera u Fazi 5 i Drugi Prolaz u Fazi 6

- **Faza 5 (`Wait-WindowsUpdateCompletionBarrier`):** Pre pokretanja 30s CPU stres testa čeka da se pozadinski `TiWorker.exe`, `TrustedInstaller.exe` i `MoUsoCoreWorker.exe` procesi završe (timeout 15 min) kako ne bi remetili merenje temperatura ([[decisions/ADR-0129-hardware-diagnostics-wu-barrier-and-i18n]]).
- **Faza 6 (`Invoke-WindowsUpdateSecondPass`):** Asinhroni `Start-Job` (timeout 600s) hvata zakrpe koje su postale primenjive tek nakon aktivacije čipset i grafičkih drajvera.

---

## 🔗 Povezane Beleške
- Pregled Arhitekture: [[architecture/overview]]
- Pajplajn Izvršavanja: [[architecture/pipeline]]
- StepGuard i UI v2.3.0: [[decisions/ADR-0181-stepguard-precheck-hardware-identity-and-ui-v23]]
- Mrežno Ubrzanje i Single-Stream `.tar` Drajveri: [[decisions/ADR-0035-fleet-network-acceleration-tar-streaming]]
- Samoučeća Berba Drajvera: [[decisions/ADR-0028-universal-driver-harvesting-and-nas-sync]]
