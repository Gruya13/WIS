---
title: "Windows Update, Single-Stream Drajveri i Samoučeća Berba (Windows Update & Driver Pipeline)"
type: architecture
created: 2026-09-14
updated: 2026-11-18
tags:
  - architecture
  - windows-update
  - drivers
  - com-api
  - nas
---

# Windows Update, Single-Stream Drajveri i Dvosmerna Berba: WIS (v3.0.0)

Faza 2 (`Phase2-Drivers.ps1` — **89 linija**, `3,684 B`, i `WIS.Update.psm1` — **4,691 linija**, `239,936 B`) autonomno rešava instalaciju svih hardverskih drajvera (čipset, grafika, audio, Wi-Fi, senzori, touchpad) i kumulativnih zakrpa kroz **6 idempotentnih `StepGuard` koraka** (`P2.*` — [[decisions/ADR-0181-stepguard-precheck-hardware-identity-and-ui-v23]], [[decisions/ADR-0225-wis-v300-embedded-hwtest-bidirectional-wu-and-preoobe-autopilot]], [[decisions/ADR-0236-wis-benchmark-predictive-cache-and-post-telemetry-hardening]], [[decisions/ADR-0247-field-monitoring-17-devices-and-p0-p2-system-hardening]]), kombinovanjem dvosmernog HTTP/NAS keša (`ServiceHub` + ujedinjena TrueNAS ZFS deljenja), `Model DNA` sinhronizacije, mrežnog auto-oporavka (`Restore-ServisNetworkConnectivityIfNeeded`), Windows Update COM API-ja i OEM provajdera.

---

## 🚀 1. Lokalni NAS Pre-Patch, Single-Stream `<Model>.tar` i `StepGuard` Koraci (`P2.*`)

1. **`P2.NasPrePatch` & `P2.ModelDrivers` (`Invoke-NasUpdatePrePatch`, `Invoke-ModelDriversPreInstall`):**
   - Proverava `ServiceHub` HTTP keš (`/api/updates/status`, `/api/updates/download`, uz `OSBuild` proveru, SHA-256 verifikaciju preko `index.json` i isključivanje `*AggregatedMetadata*.cab` / `wsusscan.cab`) i NAS deljenje `\\192.168.170.254\Servis\Updates\Win11` (u skladu sa `[WindowsUpdate] Mode=All`), snima `C:\setup\nas-prepatch-done.flag` i preuzete `.msu`/`.cab` pakete može dvosmerno otpremiti nazad na hub (`/api/updates/upload`, `/api/updates/cache-kb`, `/api/updates/prune`).
   - Uz `Test-IsGenericComputerModel` zaštitu (blokira `GenericPC`, `Unknown_Model`, `System_Product_Name`, `Default_string`), za poznate modele preuzima jedinstveni `<Model>.tar` iz `/app/harvested/Archives` (ili preko `Model DNA` alias rezolucije) i TrueNAS ZFS ARC RAM keša brzinom **~110 MB/s** (~2s), raspakuje ga lokalno pomoću `tar.exe -xf` za `< 1 s`, instalira preko `pnputil.exe /add-driver ... /install` i snima `C:\setup\model-drivers-preinstalled.flag`, čime se izbegava „SMB Small-File Latency Storm" ([[decisions/ADR-0035-fleet-network-acceleration-tar-streaming]]).
   - Nakon `P2.Pnp`, u `P2.Diag` i pre berbe drajvera poziva se **`Restore-ServisNetworkConnectivityIfNeeded`** kako bi se automatski oporavio LAN/Wi-Fi adapter ukoliko instalacija mrežnog drajvera privremeno prekine vezu ka Hub-u.
2. **`Model DNA` Sinhronizacija & Idempotentnost (`Send-WisModelDnaToHub`, `Test-WisDeviceManagerClean`):**
   - Na ulazu u Fazu 2 i nakon dopunskog rešavanja drajvera klijent šalje PnP HWID otisak i `model_aliases` parove (MTM ⬌ marketinški naziv) na `POST /api/model-profile/dna` za `Predictive Cache` pre-warming.
   - Svih 6 koraka Faze 2 evidentira se u `C:\setup\step-journal.json` (`Send-IpcStep` u UI ekspanderu).
   - `P2.WuDrivers` izvršava `Test-WisDeviceManagerClean` unutar `WIS.StepGuard.psm1` runspace-a (sa `[void]$ps.AddStatement()` između `Import-Module` poziva) i potvrđuje odsustvo uređaja sa greškama (`ConfigManagerErrorCode != 0`).

---

## ⚙️ 2. Nativni Windows Update COM API i Pozadinski Radnik (`wu-bg-status.json`)

Za preostale drajvere i zakrpe, `WIS.Update.psm1` koristi ugrađeni COM interfejs `Microsoft.Update.Session`:
- Eksplicitno registruje **Microsoft Update katalog** (`Microsoft.Update.ServiceManager` / Service ID `7971f918-a847-4430-9279-4a52d1efe18d`) i postavlja `ServerSelection = 3` (`ssOthers`) kako bi pored osnovnih Windows zakrpa pronalazio i ažuriranja za **Microsoft Office** i druge Microsoft proizvode, uz pretragu `IsInstalled=0 and (Type='Driver' or Type='Software')` kroz do **3 iterativna kruga (`MaxCycles = 3`)**.
- Primenjuje **Batch Download** uz aktiviran **Delivery Optimization LAN Peering** (`DODownloadMode = 1`) i **pozadinski WU radnik** koji upisuje progres u `C:\setup\wu-bg-status.json` (`PollBackgroundWuStatusFile()` u `WIS.UI.exe` uživo prikazuje napredak preuzimanja/instalacije u UI-ju). Na ulasku u Fazu 4 (`Invoke-Phase4Apps`) ovaj pozadinski radnik se automatski ponovo pokreće ukoliko je bio prekinut međurestartom posle Faze 3.
- **Tolerantno rukovanje HRESULT kodovima (Zamka 31):** Kodovi `0x8024200b` (`WU_E_LEGACYOPERATIONRESULT`) i `0x80070103` (`ERROR_ALREADY_EXISTS`) tretiraju se kao benigno `INFO` stanje uz `ExitCode = 0`.

---

## 🛡️ 3. ServiceHub OEM Trozubac i Microsoft Update Catalog Fallback (`Install-DriverFromMsCatalog`)

Ukoliko nakon Windows Update prolaza u Device Manager-u i dalje postoje uređaji sa greškom (`ConfigManagerErrorCode != 0`):
- Klijent šalje spisak HWID identifikatora, proizvođača i modela na `ServiceHub` (`POST /api/drivers/resolve`, timeout **`90000 ms`**).
- `ServiceHub` normalizuje ACPI, HDAudio, PCI i USB identifikatore (`servishub/drivers/hwid.py`), pasivno uči `Model DNA`, proverava OEM pakete (Lenovo SCCM, Dell Command | Deploy, HP SoftPaq) i pretražuje Microsoft Update Catalog, keširajući `.cab` drajvere na TrueNAS-u i isporučujući ih klijentu.
- Klijentski **`Select-RelevantDriverInfFiles`** u `WIS.Hardware.psm1` (bez PowerShell comma-wrapa niza i sa izravnavanjem kod pozivaoca) iz raspakovanih multi-OEM `.cab` paketa instalira isključivo `.inf` fajlove koji se poklapaju sa konkretnim `SUBSYS`/`VEN`/`DEV` HWID-om računara (eliminišući 3+ min instalacije svih `212` stranih varijanti i punjenje `DriverStore`-a tuđim OEM drajverima).

---

## 🧠 4. Samoučeća Berba Drajvera (`Export-ServisHardwareDrivers`)

Kada su svi drajveri na novom modelu računara uspešno instalirani:
- `Export-ServisHardwareDrivers` (sa UI statusom **`HARVESTING`** tokom pakovanja) proverava `Test-IsGenericComputerModel` (ne snima arhive za generičke ploče), koristi **`$cleanModel.harvest.lock`** zaštitu od paralelnog pisanja (`< 30 min`), vrši deduplikaciju u odnosu na postojeću zdravu `.tar` arhivu (`1 MB .. 5.0 GB` = `5368709120 B`), automatski uklanja tuđe `hdx*.inf_*` OEM varijante i starije duplikate istog prefiksa pre pakovanja, štiti postojeće arhive od prepisivanja prevelikim paketom (`> 5.0 GB`), pakuje čist `<Model>.tar` lokalno (`tar.exe`-first) uz atomski prenos pre fallback SMB eksporta fajl-po-fajl, upisuje anonimizovani `model_meta.json` (proizvođač, komercijalni naziv modela i MTM/SKU, **bez serijskih brojeva klijentskih uređaja**) u `Drivers\Harvested\Models\<Model>\` (`ShareName=Servis`, `DriversSubDir=Drivers\Harvested\Models`), i sinhronizuje ga preko ServiceHub HTTP keša (`Invoke-ServisHubBinaryUpload` sa `KeepAlive = $false`, `Expect100Continue = $false`, `262144 B` baferom i `Send-ModelDriverPackToServisHub` `1200s` + `1x` retry).
- Svaki naredni računar istog modela u servisu dobija kompletne drajvere za **~3 sekunde** bez preuzimanja sa interneta.

---

## ⏳ 5. WU Barijera, Desktop HwTest Auto-Skip u Fazi 5 i Brza Barijera / Drugi Prolaz u Fazi 6

- **Faza 5 (`Wait-WindowsUpdateCompletionBarrier` & Integrisani HwTest):** U `WIS v3.0.0`, Faza 5 se izvršava integrisano unutar `WIS.UI.exe` (`ExecutePhase5IntegratedHwTest`, `45 min`). Na desktop računarima bez laptop hardvera (`!_hwInfo.IsLaptop`), svih 9 interaktivnih periferijskih testova dobija `TestStatus.NotApplicable`, `P5.Peripherals` se označava sa `SKIPPED_DONE`, snima se `C:\setup\hwtest-result.json` i odmah se prelazi u Fazu 6; za termalno opterećenje (`Phase5-Stress.ps1` u CI/fallback režimu) barijera proverava da su `TiWorker.exe` i `TrustedInstaller.exe` neaktivni kako ne bi remetili merenje temperatura ([[decisions/ADR-0129-hardware-diagnostics-wu-barrier-and-i18n]]).
- **Faza 6 (`Wait-WindowsUpdateCompletionBarrier` & `Invoke-WindowsUpdateSecondPass`):** Brza WU barijera u `Phase6-Finalize.ps1` ima strogi gornji limit (`60s`, odnosno `45s` kada preostane manje do timeout-a), podržava trenutni izlaz uz `C:\setup\wu-secondpass-done.flag` i meri CPU deltu (`>= 150ms`) isključivo nad `tiworker` / `trustedinstaller` procesima bez čekanja na neaktivne `MoUsoCoreWorker` / `UsoClient` procese. Drugi prolaz `Invoke-WindowsUpdateSecondPass` (`Start-Job`, `300s`) poziva `Invoke-NasUpdatePrePatch -SkipDriverPreInstall` (uz proveru `model-drivers-preinstalled.flag`/`nas-prepatch-done.flag`) kako se nikad ne bi ponovo izvršavala pre-instalacija `<Model>.tar` paketa, već samo hvata zakrpe i Office ažuriranja (kroz Microsoft Update katalog `7971f918-a847-4430-9279-4a52d1efe18d`) koje su postale primenjive tek nakon aktivacije čipset i grafičkih drajvera.

---

## 🔗 Povezane Beleške
- Pregled Arhitekture: [[architecture/overview]]
- Pajplajn Izvršavanja: [[architecture/pipeline]]
- StepGuard i UI v2.3.0: [[decisions/ADR-0181-stepguard-precheck-hardware-identity-and-ui-v23]]
- Dvosmerni WU i Driver Keš v3.0.0: [[decisions/ADR-0225-wis-v300-embedded-hwtest-bidirectional-wu-and-preoobe-autopilot]]
- WIS Benchmark, Predictive Cache & Post-Telemetry Hardening: [[decisions/ADR-0236-wis-benchmark-predictive-cache-and-post-telemetry-hardening]]
- Mrežno Ubrzanje i Single-Stream `.tar` Drajveri: [[decisions/ADR-0035-fleet-network-acceleration-tar-streaming]]
- Samoučeća Berba Drajvera: [[decisions/ADR-0028-universal-driver-harvesting-and-nas-sync]]
