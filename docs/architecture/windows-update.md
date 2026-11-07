---
title: "Windows Update, Single-Stream Drajveri i Samoučeća Berba (Windows Update & Driver Pipeline)"
type: architecture
created: 2026-09-14
updated: 2026-10-26
tags:
  - architecture
  - windows-update
  - drivers
  - com-api
  - nas
---

# Windows Update, Single-Stream Drajveri i Samoučeća Berba: WIS (v2.17.0)

Faza 2 (`Phase2-Drivers.ps1` i `WIS.Update.psm1`, 2,199 linija) autonomno rešava instalaciju svih hardverskih drajvera (čipset, grafika, audio, Wi-Fi, senzori, touchpad) i kumulativnih zakrpa kombinovanjem lokalnog TrueNAS ZFS keša, Windows Update COM API-ja i `ServiceHub` OEM proksija.

---

## 🚀 1. Lokalni NAS Pre-Patch i Single-Stream `<Model>.tar` Drajveri

1. **NAS `.msu` Pre-Patch (`Invoke-NasUpdatePrePatch`):**
   - Proverava `\\192.168.170.254\WIS\Updates\Win11` za kumulativne Windows 11 zakrpe i instalira ih preko `wusa.exe`.
   - Poštuje postavku `[WindowsUpdate] Mode=DriversOnly` iz `Wifi-Servis.ini` (kada je zlatna slika već osvežena najnovijim LCU paketom, preskače `.msu` pre-patch i štedi 2–4 minuta rada).
2. **Single-Stream `.tar` Pre-Install Drajvera Modela (`Invoke-ModelDriversPreInstall`):**
   - Da bi se izbegla „SMB Small-File Latency Storm" pri kopiranju hiljada sitnih `.inf`/`.sys`/`.cat` fajlova preko mreže, drajveri svakog poznatog modela čuvaju se na TrueNAS-u kao jedinstvena arhiva `\\192.168.170.254\WIS\Drivers\Harvested\Archives\<Model>.tar`.
   - Klijent preuzima jedan linearni `.tar` fajl iz TrueNAS ZFS ARC RAM keša brzinom od **~110 MB/s** (~2 sekunde), raspakuje ga lokalno na NVMe SSD pomoću `tar.exe -xf` za `< 1 s` i instalira drajvere lokalnim pozivom `pnputil.exe /add-driver ... /install` ([[decisions/ADR-0035-fleet-network-acceleration-tar-streaming]]).

---

## ⚙️ 2. Nativni Windows Update COM API i Tolerantno Rukovanje Kodovima

Za preostale drajvere i zakrpe, `WIS.Update.psm1` koristi ugrađeni COM interfejs `Microsoft.Update.Session`:
- Pretražuje `IsInstalled=0 and (Type='Driver' or Type='Software')` kroz do **3 iterativna kruga (`MaxCycles = 3`)**.
- Primenjuje **Batch Download** uz aktiviran **Delivery Optimization LAN Peering** (`DODownloadMode = 1`) i pojedinačnu izolovanu instalaciju sa živim JSON IPC progresom u `WIS.UI.exe`.
- **Tolerantno rukovanje HRESULT kodovima (Zamka 31):** Kodovi `0x8024200b` (`WU_E_LEGACYOPERATIONRESULT`) i `0x80070103` (`ERROR_ALREADY_EXISTS` — drajver je već instaliran kroz `.tar` pre-install) tretiraju se kao benigno `INFO` stanje uz `ExitCode = 0`.

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
- Mrežno Ubrzanje i Single-Stream `.tar` Drajveri: [[decisions/ADR-0035-fleet-network-acceleration-tar-streaming]]
- Samoučeća Berba Drajvera: [[decisions/ADR-0028-universal-driver-harvesting-and-nas-sync]]
