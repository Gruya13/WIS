# ADR-0247: Živo Terensko Praćenje 17 Uređaja, P0–P2 Očvršćavanje i Dijagnostičko-Telemetrijska Poboljšanja (`WIS-TASK-239 .. WIS-TASK-247`)

## Status
Prihvaćeno, implementirano, testirano, potpisano RSA-2048 izdanje (`0fc688afa092c84ab05239dfb8cc4c190f7779b8-8919c26072904c75`) i potvrđeno na TrueNAS / ServiceHub produkciji (2026-11-18)

## Kontekst
Nakon stabilizacije `WIS Benchmark` i `Predictive Cache` mehanizama (`ADR-0236`) i nezavisne bezbednosne revizije (`ADR-0238`), u periodu `2026-11-17 .. 2026-11-18` sproveden je niz terenskih validacija uključujući real-time praćenje **`14` novih + `3` referentna uređaja** kroz Faze 1–6 (`WIS-TASK-246`, `39` preseka, ukupno `171` flotnih instalacija u benchmark agregatu). Analiza živih logova i benchmark telemetrije identifikovala je konkretna uska grla i rubne slučajeve (`WIS-TASK-239 .. WIS-TASK-247`):
1. **Gubitak identiteta i dijagnostike preko restarta:** Parcijalni završni izveštaji i restart ServiceHub servera gubili su `hw`/`serial_number`/`hwtest`/`benchmark` polja, pred-OEM `DESKTOP-*`/`APCL-*` zapisi su duplirali uređaje u floti, a `recent_runs` je za `NEEDS_ATTENTION` uređaje prikazivao `status: "COMPLETED"`.
2. **Blokeri Faza 2, 3 i 4 (`P0`):**
   - Kada `/mnt/Servis/Programi/VCRedist/` nije imao offline instalere, `P3.VcRedist` i `Install-ServisVCRedist` su pokretali `winget.exe -Wait` bez timeouta, ostavljajući zaglavljeno procesno stablo čak i nakon UI timeouta faze.
   - `P4.Acrobat` nije čekao prateći `msiexec` proces uz `Setup.exe`, nije čitao `acrobat_msi.log` niti imao automatski retry.
   - `Select-RelevantDriverInfFiles` u `WIS.Hardware.psm1` vraćao je ugnježdeni niz (`return ,$allInfs`), pa je u PowerShell-u 5.1 spajao listu u jedan objekat (`InfCount: 1`), dok je `/api/drivers/resolve` timeout od `45s` bio prekratak.
   - U `WIS.StepGuard.psm1` falio je `[void]$ps.AddStatement()` između `Import-Module` poziva, izazivajući lažni `P2.WuDrivers=WARN`.
   - U `Export-ServisHardwareDrivers`, realne `.tar` arhive drajvera dostizale su `1.79–3.21 GB`, pa je stari limit od `1.5 GB` poništavao deduplikaciju, a direktni SMB eksport fajl-po-fajl trajao je do `22 min` uz trke pri paralelnom pisanju i pucanje `Invoke-ServisHubBinaryUpload`.
3. **Mreža, UI, HwTest i Daljinske Komande (`P1/P2`):**
   - Instalacija mrežnih/PNP drajvera ponekad je privremeno obarala LAN konekciju u Fazi 2 bez automatskog oporavka.
   - `TelemetryService.cs` nije čitao `pending_commands` iz HTTP odgovora na `/api/heartbeat`, a `WIS.Telemetry.psm1` nije imao handler za `"skip_step"`.
   - `AutoHardwareDiagnostics.cs` pozivao je blokirajući `eng.Update()` unutar `RunCpuStress` petlje, razvlačeći `60s` stres test na `139–406s` na Core Ultra uređajima sa sporim `WMI_ACPI`; `HardwareProbe.cs` je detektovao `Facial Recognition Software Device` kao čitač otiska prsta; termalno pregrevanje (`THERMAL_CRITICAL`) prekidalo je dalji rad umesto da bezbedno prekine stres i nastavi pipeline uz napomenu.

## Donete Odluke

1. **P0 Sanacija Runtimes, Acrobat, INF Filter, StepGuard i `DriverHarvest` (`WIS.Branding.psm1`, `WIS.Apps.psm1`, `WIS.Hardware.psm1`, `WIS.StepGuard.psm1`, `WIS.Update.psm1`, `MainWindow.xaml.cs`):**
   - Dodati zvanični offline `VC_redist.x64.exe` (`25,635,768 B`) i `VC_redist.x86.exe` (`13,953,392 B`) u `Programi/VCRedist/` (lokalno OEM stablo i NAS `/mnt/TK_NAS/Servis/Programi/VCRedist/` & `/mnt/TK_NAS/WIS/shared/Programi/VCRedist/`).
   - `P3.VcRedist` i `Install-ServisVCRedist` zamenili su `-Wait` sa `PassThru` + `WaitForExit` (`180s` lokalno / `120s` `winget`) + `taskkill.exe /PID ... /T /F`; isti `/T /F` tree-kill dodat je u `MainWindow.xaml.cs` na timeoutu faze.
   - `P4.Acrobat` prati `Setup.exe` i `msiexec` procese, analizira `C:\setup\acrobat_msi.log` i vrši automatski `1x` MSI retry (`acrobat_msi_retry.log`).
   - Uklonjen comma-wrap iz `Select-RelevantDriverInfFiles` uz izravnavanje kod pozivaoca, a `/api/drivers/resolve` timeout povećan na `90000 ms`.
   - Dodat `[void]$ps.AddStatement()` posle `Import-Module` u `WIS.StepGuard.psm1`.
   - U `Export-ServisHardwareDrivers`: prag `$maxHealthyTarBytes` podignut na **`5.0 GB`** (`5368709120 B`), uvedena `$cleanModel.harvest.lock` zaštita (`< 30 min`), lokalno `tar.exe` pakovanje i atomski prenos pre sporog SMB eksporta, `Invoke-ServisHubBinaryUpload` (`KeepAlive = $false`, `Expect100Continue = $false`, `262144 B` bafer) + `Send-ModelDriverPackToServisHub` (`1200000 ms` + `1x` retry), te nova funkcija **`Restore-ServisNetworkConnectivityIfNeeded`** u Fazi 2.

2. **Nastavak Pri Pregrevanju, Resume Optimizacija, `Display/Touchpad/IR/Location` Finese (`WIS.HwTest`, `MainWindow.xaml.cs`, `FirstLogon`, `SplashForm`):**
   - Pri `THERMAL_CRITICAL` (`>= 95°C`) CPU/GPU stres se odmah prekida, ali pipeline nastavlja Fazu 5/6 i završni ekran uz `thermal_note`/`notes` u `hwtest-result.json`, `servisni_karton.md/json` i `### 📝 Napomene za Servis` u `ServiceHub/servishub/reports.py` (`WIS-TASK-245`).
   - Završnica `WIS.UI.exe` prebačena na **„Očisti i restartuj računar”** (`shutdown.exe /r /t 3`) sa responzivnim `WrapPanel` + `ScrollViewer` rasporedom (`WIS-TASK-243`); restart nakon Faze 3/OOBE direktno diže `WIS.UI.exe --master` bez ponovnog Launchera kad je binari već na disku (`WIS-TASK-244`); `ShowGlobalPrompts = 0` suzbija Windows 11 Location popup (`WIS-TASK-242`).
   - `DisplayTestWindow` dobija startni info baner (`WIS-TASK-240`); `TouchpadTestWindow` odvaja Touchpad od TrackPoint 3 gornja tastera uz posvećenu kernel nit `StartElanKernelWaitThread`; `CameraTestWindow` filtrira tamne `IR-OFF` frejmove; `HwTestShell` izdvaja `manualWaitLimitMs = 24h` za noćno čekanje (`WIS-TASK-241`).
   - `AutoHardwareDiagnostics.RunCpuStress` pokreće `eng.Update()` asinhrono preko `ThreadPool.QueueUserWorkItem` sa `Interlocked` guard-om; `HardwareProbe.cs` isključuje `ROOT\`, `SWD\`, `FACIAL RECOGNITION`, `HELLO FACE` i `SOFTWARE DEVICE` iz detekcije čitača otiska prsta.

3. **Daljinske Komande, Benchmark Reset i `ServiceHub` Konsolidacija (`TelemetryService.cs`, `WIS.Telemetry.psm1`, `WIS.Common.psm1`, `telemetry.py`, `benchmark.py`, `handlers.py`):**
   - `TelemetryService.cs` parsira `pending_commands` iz `/api/heartbeat` odgovora (`HttpPostWithResponse`), upisuje `C:\setup\pending-hub-commands.json` i kreira `C:\setup\.skip` za `skip_step`, dok `WIS.Telemetry.psm1` podržava `"skip_step"` i prilaže `hwtest-result.json` u `Send-FleetFinalReport`; UI throttling prag usklađen na `maxT >= 85.0f || (maxThrot >= 25.0f && maxT >= 80.0f)`; `Start-WisPhaseBenchmark` resetuje `duration_sec = 0.0` uz čuvanje `prev_duration_sec`.
   - ServiceHub čuva `hw`/`hwtest`/`benchmark` kroz restart i merge (`enrich_report_payload`, `_merge_report_records`), normalizuje kineski `apps_summary` (`_normalize_apps_summary`), usklađuje `recent_runs.status = "NEEDS_ATTENTION"` kada `success == False`, i uklanja privremene pred-OEM `DESKTOP-*`/`APCL-*` duplikate putem `cleanup_duplicate_devices_locked`.

## Posledice
- **Produkcija (`2026-11-18 16:47 CET`):** Potpisan paket `wis-payload.zip` (`68,532,795 B`, `SHA-256: 8919c26072904c75efc96d4543897deb2358deb6e4a9fc6f4894b36c32eace29`, `release_id: 0fc688afa092c84ab05239dfb8cc4c190f7779b8-8919c26072904c75`) i postavljen na TrueNAS uz `VC_redist.x64/x86.exe`, nove ServiceHub module i restart `servis-hub` kontejnera (`/api/health` i `/api/payload/version` `200 OK`; `/api/benchmark/summary` sa `171` instalacijom odmah prikazuje `TK-LAPTOP-DEMO01` sa `status: "NEEDS_ATTENTION"`).
- **Verifikacija:** `Sync-Versions.py --check` (`PASS`), `Verify-PowerShellSyntax.py` (`22/22`), `Verify-CSharpSyntax.py` (`48/48`), `Build-HwTest.py` (`1,761,280 B`, `fc77f8a8...`), `Build-UI.py` (`3,007,488 B`, `406db994...`), `Launcher/Tests/run_tests.py` (`31/31`), `UI/Tests` (`44/44`: `27 + 12 + 5`, uklj. `/mnt/Servis`), `ServiceHub/tests` (`290`: `270 PASS, 20 skipped`), `Sign-PayloadManifest.py --verify-only` (`[PASS]`).
