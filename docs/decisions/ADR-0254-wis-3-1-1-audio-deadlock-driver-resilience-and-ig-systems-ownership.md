# ADR-0254: WIS v3.1.1 / ServiceHub v3.4.1 — Sanacija Audio/Mic Deadloka, Otpornost Driver Pipeline-a, Dual-GPU Provera i IG-Systems Vlasništvo (`WIS-TASK-254`)

- **Datum:** `2026-11-19`
- **Status:** Prihvaćeno i implementirano (`WIS v3.1.1` / `ServiceHub v3.4.1`)
- **Povezani dokumenti:** [`ADR-0253`](ADR-0253-wis-3-1-0-security-hardening-and-audit-remediation.md), [`ADR-0189`](ADR-0189-manual-hardware-test-suite.md), [`TEHNICKI_OPIS_SISTEMA_WIS.md`](../TEHNICKI_OPIS_SISTEMA_WIS.md)

---

## 1. Kontekst

Nakon objave izdanja `v3.1.0`, terenska analiza tri Lenovo laptop konfiguracije (*Legion 5 15AHP11 83Q7* i *LOQ 15IRX10 83JE*) i pratećih `ServiceHub` logova identifikovala je devet konkretnih softverskih defekata (`BUG-01` – `BUG-09`) i zahteve za formalno usklađivanje vlasništva dokumentacije i primera na **IG-Systems**:

1. **`BUG-01` (Sound $\rightarrow$ Mic WinMM/COM deadlock):** Prekidanje 8.2s melodije u `AudioTestWindow` pre završetka ostavljalo je otvoren `waveOut` resurs i cross-apartment COM pozive na `_audioVolume`, dok je sledeći `MicTestWindow` odmah u `Loaded` UI handleru sinhrono zvao CoreAudio `IAudioEndpointVolume` i `waveInOpen`, što je zamrzavalo WPF STA nit.
2. **`BUG-02` (Kaskadni mrežni pad u Fazi 2):** Posle instalacije čipset/SMBus `3010` drajvera i privremenog PnP mrežnog reseta od 1–2s, `Resolve-MissingDriversFromHub` je bez retry-ja i mrežnog oporavka rušio preostale uređaje.
3. **`BUG-03` (Odsecanje zavisnih `.inf` fajlova):** Prag `$allInfs.Count -le 3` u `Select-RelevantDriverInfFiles` sužavao je pakete sa 4 `.inf` fajla (npr. RTX 5050 / 5060 na `1/4`) izostavljajući prateće `Extension`/`SoftwareComponent` module.
4. **`BUG-04` (Tihi padovi u `P2.WuDrivers`):** Nedostajao je `$installer.ForceQuiet = $true`, provera `ResultCode`, retry i logovanje u praznom `catch {}`, a `ServisWuActive` nije pokrivao batch download.
5. **`BUG-05` (`Start-Job` bug `(,$updateTitles)` u `Invoke-WindowsUpdateSecondPass`):** PowerShell 5.1 konvertovao je `-ArgumentList (,$updateTitles)` u literal `"System.Object[]"` kod `param([string[]]$titles)`, zbog čega je drugi prolaz uvek instalirao `0` stavki.
6. **`BUG-06` (`BgWinUpdate` timeout i prerano `COMPLETED`):** Faza 4 na terenu traje preko `360s`, pa je pozadinski WU radnik prerano izlazio iz čekanja na `Phase4Active`, a pri `0/N` instaliranih upisivao lažni `COMPLETED` status.
7. **`BUG-07` (`30s` HTTP timeout kod `CACHE MISS` `.cab` paketa > 100 MB):** Dugometražni download NVIDIA `.cab` paketa (`> 1 GB`) sa Microsoft CDN-a probijao je klijentske timeout-e u `WIS.UI` (`30s`) i slao prazan `model`.
8. **`BUG-08` (Prljava `.tar` berba u Fazi 2):** `Export-ServisHardwareDrivers` je snimao nepotpun `<Model>.tar` na NAS uprkos prisutnim kritičnim PnP greškama u Device Manager-u.
9. **`BUG-09` (Dual-GPU maskiranje u `HwTest`):** `InspectGpuAdapters` je prepisivao `isBasicDisplay` i `errorCode` na integrisanom iGPU-u, skrivajući to što je diskretni GPU i dalje `Microsoft Basic Display Adapter (Err=10)`.

---

## 2. Donete Odluke

1. **Determinističko gašenje zvuka i asinhrona inicijalizacija mikrofona (`BUG-01`):**
   - [`AudioTestWindow.cs`](../../UI/HwTest/Tests/AudioTestWindow.cs) uvodi `ManualResetEventSlim _waveOutDoneEvent` u `PlayWaveOutTracked`, objedinjuje reprodukciju govora cifara pod istim mehanizmom praćenja, čeka do `1500 ms` u `StopActivePlayback` pre zatvaranja prozora i delegira sve `_audioVolume` COM pozive isključivo na STA UI nit preko `Dispatcher.BeginInvoke`.
   - [`MicTestWindow.cs`](../../UI/HwTest/Tests/MicTestWindow.cs) izmešta enumeraciju uređaja, `TryUnmuteAndLogCaptureEndpoint()` i `StartWaveInCore()` iz `Loaded` UI handlera na namensku pozadinsku STA nit (`StartMicInitAsync`) uz `100 ms` grace pauzu i `_waveInLock` zaštitu pri gašenju.
2. **Otpornost rezolucije drajvera, puna `.inf` pokrivenost i bezbedna berba (`BUG-02`, `BUG-03`, `BUG-08`):**
   - [`WIS.Hardware.psm1`](../../UI/Skripte/Modules/WIS.Hardware.psm1) uvodi retry oporavak (`Invoke-HubDriverResolvePost` sa `Restore-ServisNetworkConnectivityIfNeeded` do `2` retry-ja), `2s` PnP pauzu posle čipset/mrežnih/`3010` drajvera, prag `<= 12` uz očuvanje `Extension`/`SoftwareComponent` pratećih `.inf` fajlova i `pnputil.exe /add-driver "<extractDir>\*.inf" /subdirs /install` kada se instalira ceo paket.
   - [`WIS.Update.psm1`](../../UI/Skripte/Modules/WIS.Update.psm1) i [`Phase6-Finalize.ps1`](../../UI/Skripte/Phases/Phase6-Finalize.ps1) uvode `Test-WisDeviceManagerReadyForHarvest`: nekompletan Device Manager odlaže berbu `<Model>.tar` iz Faze 2 za kraj Faze 6 (nakon međurestarta, WU drugog prolaza i završnog `Resolve-MissingDriversFromHub`), dok je postojeći neispravan `83JE.tar` uklonjen sa NAS skladišta.
3. **Pouzdan Windows Update u Fazi 2, pozadini i Fazi 6 (`BUG-04`, `BUG-05`, `BUG-06`):**
   - `P2.WuDrivers` koristi `$installer.ForceQuiet = $true`, proveru `ResultCode`/`HResult`, `1` retry i eksplicitno logovanje bez praznog `catch {}`, prošireni `ServisWuActive` i dopunski `Resolve-MissingDriversFromHub`.
   - `Invoke-WindowsUpdateSecondPass` zamenjuje `-ArgumentList (,$updateTitles)` JSON serijalizacijom (`$titlesJson` / `ConvertFrom-Json`) i broji uspehe po `GetUpdateResult($k).ResultCode -in @(2, 3)`.
   - `Invoke-BackgroundWindowsUpdateWorker` čeka na `Phase4Active` do `900s` (`300` ciklusa) i prijavljuje `WARN` kada je pri `0/N` instalirano `0` zakrpi.
4. **Dedupovani asinhroni `.cab` resolver i klijentski polling (`BUG-07`):**
   - [`ServiceHub/servishub/handlers.py`](../../ServiceHub/servishub/handlers.py) i [`scraper.py`](../../ServiceHub/servishub/drivers/scraper.py) dobijaju dedupovani pozadinski downloader (`_driver_dl_lock` / `_driver_dl_jobs`) sa chunked downloadom (`timeout=90`); za klijente koji pošalju `async_download: true` (poput `WIS.UI` sa `Timeout = 120000 ms` i `Resolve-MissingDriversFromHub`), server vraća `status: "downloading"` + `retry_after_sec: 5` bez prekida konekcije dok se `.cab` ne kešira.
5. **Dual-GPU verifikacija bez lažnog `PASS`-a (`BUG-09`):**
   - [`AutoHardwareDiagnostics.cs`](../../UI/HwTest/Core/AutoHardwareDiagnostics.cs) i [`GpuStressWindow.cs`](../../UI/HwTest/Tests/GpuStressWindow.cs) dodaju `hasAnyBasicDisplay`, `hasAnyErrorAdapter` i `problemAdaptersSummary` kroz sve `Win32_VideoController` adaptere i odmah prijavljuju `TestStatus.Fail` ako bilo koji GPU nema ispravan drajver.
6. **IG-Systems vlasništvo dokumentacije i primera:**
   - Uklonjena su sva prethodna brending imena radionice iz konfiguracionih šablona (`Wifi-Servis.example.ini`), QR PDF generatora, podrazumevanih natpisa u `LabelPrintService.cs`, `LICENSE`, `AssemblyInfo.cs` i celokupne javne i interne dokumentacije, uz jasno navođenje **IG-Systems** kao vlasnika sistema.

---

## 3. Posledice i Verifikacija

- Otklanja se zamrzavanje UI-ja u `WIS.HwTest`, višestruko smanjuje verovatnoća zaostalog `Basic Display` drajvera na dual-GPU laptopovima sa čipset/SMBus PnP resetima, i obezbeđuje čista `Model DNA` / `<Model>.tar` arhiva na NAS-u.
- Zadržava se stroga kompatibilnost sa `PowerShell 5.1`, `C# 5.0` (`/p:LangVersion=5`), `min_launcher_version: 3.1.0` i pošteno praćenje statusa fizičke VM E2E validacije (`windows_validation: pending` u `version.json`).
