---
title: "Pajplajn Izvršavanja i Upravljanje Stanjem (Pipeline & State Management)"
type: architecture
created: 2026-09-14
updated: 2026-10-26
tags:
  - architecture
  - pipeline
  - state
  - phases
---

# Pajplajn Izvršavanja i Upravljanje Stanjem: WIS (v2.17.0)

U savremenoj arhitekturi sistema **WIS** (*Tehno kutak Windows Installation Assistant*, [[decisions/ADR-0131-diagnostics-redesign-6phase-pipeline-and-wis-cleanup]], [[decisions/ADR-0138-non-blocking-diagnostics-soft-handover]], [[decisions/ADR-0141-canonical-signed-release-and-status-contract]]), glavni orkestrator celokupnog post-OOBE toka je **C# WPF Master Task Runner (`WIS.UI.exe`, v2.1.0)**.

`WIS.UI.exe` direktno i sekvencijalno podiže **6 procesno izolovanih PowerShell 5.1 skripti** iz direktorijuma `C:\setup\Skripte\Phases\` (`Phase1-Network.ps1` do `Phase6-Finalize.ps1`). Sve skripte faza oslanjaju se na deljene module `WIS.Common.psm1` i `WIS.StatusContract.psm1`, koji obezbeđuju strukturirano JSON IPC strimovanje na STDOUT, kooperativnu kontrolu toka (pauza/preskakanje), rotaciju logova i kanonski statusni ugovor.

---

## 💾 1. Upravljanje Stanjem (`C:\setup\deploy-state.json`)

Sistem perzistira svoje radno stanje na disku u datoteci `C:\setup\deploy-state.json`:

```json
{
  "phase": 4,
  "update_cycle": 1,
  "max_cycles": 3,
  "initial_start_time": "2026-10-26T10:15:00Z",
  "session_id": "TK-LAPTOP-PF2X98ZA",
  "deployment_status": "ACTIVE",
  "handover_ready": false,
  "cooling_verified": false,
  "manual_check_required": false,
  "diagnostics_status": "PENDING"
}
```

### Mehanizam Otpornosti i Kontinuiteta:
1. **Učitavanje stanja i Kumulativni Tajmer:** Pri svakom pokretanju `WIS.UI.exe` čita `deploy-state.json` i `HKLM\SOFTWARE\WIS\InitialStartTime`, čime kumulativni tajmer u zaglavlju prozora neprekidno meri ukupno vreme servisa kroz sve međurestarte.
2. **Atomski upis:** Svaki prelazak iz faze u fazu ili odlaganje restarta (+10 min) odmah se serijalizuje u JSON fajl.
3. **Dvostruki Fail-Safe Resume:** Ukoliko računar izvrši planirani međurestart (Interim Reboot) ili doživi prekid napajanja, sistem se automatski prijavljuje (`AutoAdminLogon = 1`) i ponovo podiže orkestrator preko `HKLM\...\RunOnce` ključa i `Startup` foldera, nastavljajući tačno od zapamćene faze.

---

## ⏱️ 2. Detaljan Tok Kroz 6 Faza i Timeout Osigurači

U `UI/MainWindow.xaml.cs` (`GetPhaseTimeoutMinutes`), svaka faza poseduje strogo definisan zaštitni timeout:

| Faza | Skripta (`UI/Skripte/Phases/`) | Linije / Veličina | Zaštitni Timeout | Ključna Uloga |
| :--- | :--- | :--- | :--- | :--- |
| **Pre-Phase** | `WIS.Launcher.exe` (v2.2.17) | 466,944 B | Reaktivni | Wi-Fi spajanje, UDP :8888 / SMB otkrivanje, RSA-2048 verifikacija `wis-payload.zip`, transakciona ekstrakcija u `C:\setup` i start `WIS.UI.exe --master`. |
| **Faza 1** | `Phase1-Network.ps1` | 134 lin. (5,736 B) | **15 min** | Mrežna povezanost (Fast-Pass), PnP fallback mrežnih drajvera, NVMe/SATA S.M.A.R.T. i baterija trijaža, Autopilot/MDM detekcija. |
| **Faza 2** | `Phase2-Drivers.ps1` | 96 lin. (4,230 B) | **60 min** | NAS `.msu` Pre-Patch, Single-Stream `<Model>.tar` drajveri, Windows Update COM API, MS Catalog fallback, Driver Harvesting i paralelni Non-MSI radnik. |
| **Faza 3** | `Phase3-Optimization.ps1` | 68 lin. (2,616 B) | **20 min** | Sistemska optimizacija, debloat, SRB/US tastatura, 24h sat, automatsko DPI skaliranje po rezoluciji, OEM brendiranje i **Interim Reboot Gate**. |
| **Faza 4** | `Phase4-Apps.ps1` | 77 lin. (3,067 B) | **60 min** | Sekvencijalni MSI Mutex Gate, direktni SMB 3.1.1 stream sa `\\192.168.170.254\Programi`, tiha instalacija 8 aplikacija, Reality Check i OEM licenca. |
| **Faza 5** | `Phase5-Stress.ps1` | 690 lin. (38,728 B) | **5 min** | WU Completion Barijera, 30s Multi-Core 100% CPU `RunspacePool` stres test, 98°C Emergency Abort i Non-Blocking Diagnostics Policy (`stress-test-result.json`). |
| **Faza 6** | `Phase6-Finalize.ps1` | 697 lin. (37,716 B) | **45 min** | Asinhroni WU Second Pass, gašenje Screen Mirror radnika, slanje DMTF servisnog kartona na ServiceHub, Anti-Leak brisanje tajni, UAC restore i brisanje `C:\setup`. |

---

### Inicijalizacija, `$OEM$` WinPE Staging i „Flash & Release" Koncept
- Tokom WinPE faze, Windows Setup kopira sadržaj `sources/$OEM$/$1/setup/` na sistemski SSD (`C:\setup\`), dok `SetupComplete.ps1` pod `SYSTEM` nalogom pokreće `wlansvc` i uvozi servisni Wi-Fi profil pre pojave ekrana za prijavu.
- **„Flash & Release":** Čim se završi WinPE kopiranje i računar krene u prvi restart, operater može odmah izvaditi USB fleš drajv.
- U `FirstLogon.ps1` fazi pokreće se pozadinski 1 Hz Screen Mirror radnik (`WIS.ScreenCapture.ps1`) i `C:\setup\WIS.Launcher.exe`, koji sa `ServiceHub`-a (`http://192.168.170.254:8080`) ili SMB deljenja `\\192.168.170.254\WIS` preuzima i RSA-2048 ključem verifikuje `wis-payload.zip`, uklanja `:Zone.Identifier` i pokreće `WIS.UI.exe --master`.

---

### Faza 1: Mrežna Povezanost, Hardverska Trijaža i Autopilot Detekcija (`Phase1-Network.ps1`)
- **Fast-Pass provera:** Ako je mreža već aktivna iz `SetupComplete` / `FirstLogon` faze, preskače nepotrebna čekanja. U suprotnom poziva `Install-NetDriverFallback` (`pnputil.exe` nad `C:\setup\Drivers\Net`) i spaja Wi-Fi.
- **Hardverska dijagnostika (`Get-HardwareDiagnosticsInfo`):** Očitava komercijalni naziv modela (Lenovo MTM/Version, Dell, HP, Asus), CPU, RAM, EDID matricu ekrana, zdravlje baterije i S.M.A.R.T. parametre diska (`PowerOnHours`, Wear %, temperaturu).
- **Autopilot / MDM inspekcija:** Pokreće nativni `WIS-AutopilotAuditor.exe --json` (ili `Provera_autopilota.ps1 -JsonOutput` kao fallback) radi provere korporativnog zaključavanja.

---

### Faza 2: Drajveri, NAS Pre-Patch, WU COM API i Berba Drajvera (`Phase2-Drivers.ps1`)
- **NAS Pre-Patch (`Invoke-NasUpdatePrePatch`):** Instalira kumulativne `.msu` zakrpe sa `\\192.168.170.254\WIS\Updates\Win11` (ili preskače ako je u `Wifi-Servis.ini` aktivan `Mode=DriversOnly`).
- **Single-Stream `.tar` Pre-Install (`Invoke-ModelDriversPreInstall`):** Ako na `\\192.168.170.254\WIS\Drivers\Harvested\Archives\` postoji `<Model>.tar`, preuzima ga brzinom ~110 MB/s, raspakuje preko `tar.exe -xf` za <1 s i instalira sve drajvere.
- **Windows Update COM API i Catalog Fallback:** Pretražuje WHQL drajvere u do 3 iterativna kruga, a za preostale nepoznate uređaje u Device Manager-u povlači CAB drajvere preko `ServiceHub` `/api/drivers/resolve` i Microsoft Update Catalog-a.
- **Samoučeći Driver Harvesting (`Export-ServisHardwareDrivers`):** Eksportuje sve nove 3rd-party drajvere u `<Model>.tar` na TrueNAS-u (uz `model_meta.json` bez klijentskih serijskih brojeva).

---

### Faza 3: Sistemska Optimizacija, Automatski DPI, OEM Brendiranje i Međurestart (`Phase3-Optimization.ps1`)
- **Optimizacija i privatnost:** Gasi telemetrijske servise (`DiagTrack`, `dmwappushservice`), Consumer Features, hibernaciju i QuickEdit mod.
- **Regionalna ergonomija i DPI (`Set-ServisAutoDpiScaling`):** Postavlja srpsku (latinica) i US tastaturu, 24h sat i automatsko Win32 DPI skaliranje prema rezoluciji monitora (100% / 125% / 150% / 200%).
- **OEM Brendiranje:** Preimenuje korisnički nalog prema detektovanom brendu računara i upisuje `OEMInformation`.
- **Pre-Reboot Gate (`Invoke-ServisInterimReboot`):** Ako marker `C:\setup\.stage_reboot_done` ne postoji, postavlja `Phase = 4` u `deploy-state.json` i vrši kontrolisani restart kako bi oslobodio CBS i MSI brave pre Faze 4.

---

### Faza 4: Tiha Instalacija Aplikacija i Licenciranje (`Phase4-Apps.ps1`)
- **Sekvencijalni MSI Gate:** Čeka oslobađanje `Global\_MSIExecute` mutexa (`Wait-MsiMutexAvailable -TimeoutSec 180`) i čisti `PendingFileRenameOperations`.
- **Direktni SMB 3.1.1 Stream (`Get-ServisProgramiRoot`):** Strimuje instalere sa `\\192.168.170.254\Programi` (uz fallback na `\WIS` i `C:\setup\Programi`).
- **8 standardnih paketa:** Google Chrome Enterprise MSI (~8s), Adobe Acrobat Pro 2024 (sa 6-stepenim PDF zaključavanjem), Microsoft Office 2024 LTSC (ODT direktno sa SMB-a), WinRAR 7.01, BatteryBar Pro 3.6.6 (Win11 Floating Toolbar na laptopovima; `SKIPPED` na desktopu), .NET Desktop Runtime, Visual C++ AIO Runtimes i OEM licenca (`/HWID` + `/Volume`).
- **Post-Execution Reality Check:** `Assert-InstalledApplicationsReality` proverava stvarno prisustvo izvršnih fajlova na disku.

---

### Faza 5: 30s Multi-Core CPU Stres Test i Non-Blocking Diagnostics Policy (`Phase5-Stress.ps1`)
- **Windows Update Barijera (`Wait-WindowsUpdateCompletionBarrier`):** Pre početka merenja čeka da se pozadinski `TiWorker.exe` i `MoUsoCoreWorker.exe` procesi smire (do 15 min timeout) kako ne bi remetili termalnu sliku.
- **100% Multi-Core `RunspacePool` Load Generator:** Pokreće N paralelnih niti (`[Environment]::ProcessorCount`) tokom tačno **30 sekundi**, mereći baznu, maksimalnu i prosečnu temperaturu i eventualni termalni throttling.
- **Kanonski Statusni Ugovor (`stress-test-result.json` & `WIS.StatusContract.psm1`):**
  - `PASS` ($< 80^\circ\text{C}$) / `WARNING` ($80\text{–}94^\circ\text{C}$): `cooling_verified = true`, `handover_ready = true`, `exit 0`.
  - `THERMAL_CRITICAL` ($\ge 95^\circ\text{C}$ ili Emergency Abort na $\ge 98.0^\circ\text{C}$): trenutno prekida opterećenje, postavlja `handover_ready = false`, `cooling_verified = false` i vraća **`exit 2`** (crveni ekran u UI-ju, blokada predaje).
  - `UNKNOWN` / `SENSOR_UNAVAILABLE` (hardver ne izlaže termalne senzore): **NE blokira instalaciju** — postavlja `handover_ready = true`, `cooling_verified = false`, `manual_check_required = true`, prikazuje žuto upozorenje u UI-ju i vraća **`exit 0`**.

---

### Faza 6: Drugi WU Prolaz, DMTF Servisni Karton, Anti-Leak Čišćenje i Samouništenje (`Phase6-Finalize.ps1`)
- **WU Second Pass (`Invoke-WindowsUpdateSecondPass`):** Asinhroni `Start-Job` (do 600s) koji instalira preostale zakrpe nakon što su svi drajveri aktivni.
- **Zaustavljanje Screen Mirror radnika:** Signalizira `Global\WIS_ScreenCapture_StopEvent` i gasi pozadinski posao snimanja ekrana.
- **DMTF Servisni Karton (`Send-FleetFinalReport`):** Šalje kompletan JSON izveštaj (`TK-{Chassis}-{Serial}`) na `ServiceHub` `/api/devices/{id}/report` i čuva lokalnu kopiju u `C:\Windows\Logs\WIS\`.
- **Anti-Leak Sanitizacija i Samouništenje (`Invoke-ServisCleanupAndFinalize`):**
  1. Briše servisni Wi-Fi profil iz Windows WLAN baze (`netsh wlan delete profile`) i trajno uklanja `Wifi-Servis.ini` i XML fajlove sa diska.
  2. Raskida sve SMB sesije (`net use * /delete /y`).
  3. Uklanja privremene `RunOnce` ključeve i `Startup` lansere.
  4. Vraća UAC na fabričke bezbednosne vrednosti (`ConsentPromptBehaviorAdmin = 5`, `PromptOnSecureDesktop = 1`).
  5. Ukoliko su sve provere prošle, pokreće asinhrono brisanje `C:\setup\` i predaje kontrolu `WIS.UI.exe` završnom ekranu (60s odbrojavanje do gašenja sa opcijom `+10 min` odlaganja).

---

## 🔗 Povezane Beleške
- Pregled Arhitekture: [[architecture/overview]]
- PowerShell Moduli i Build: [[architecture/modules]]
- Non-Blocking Diagnostics Policy: [[decisions/ADR-0138-non-blocking-diagnostics-soft-handover]]
- Kanonski Statusni Ugovor: [[decisions/ADR-0141-canonical-signed-release-and-status-contract]]
- Poznate Zamke: [[memory/gotchas]]
