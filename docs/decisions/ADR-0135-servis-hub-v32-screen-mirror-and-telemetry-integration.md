---
title: "ADR-0135: Integracija Servis Hub v3.2.0 - 1 Hz Live Screen Mirror u RAM-u, Hardverska Telemetrija, Daljinski UEFI Reboot i Dynamic Payload Proširenje"
---

# ADR-0135: Integracija Servis Hub v3.2.0 - 1 Hz Live Screen Mirror u RAM-u, Hardverska Telemetrija, Daljinski UEFI Reboot i Dynamic Payload Proširenje

## 1. Kontekst i Problem

Sa nadogradnjom centralnog radioničkog nadzornog servera na **Servis Hub v3.2.0** (modularni Python paket `servishub` na TrueNAS SCALE serveru `192.168.170.254:8080`), uvedene su napredne serverske mogućnosti u Web Cockpitu:
1. **1 Hz Live Screen Mirror (`/api/devices/<id>/screen-frame`):** Potreba da serviser u Web Cockpitu vidi vizuelno stanje klijentskog ekrana u realnom vremenu (hvatanje UAC promptova, neobičnih OEM dijaloga, stanja instalera i radne površine) na taktu od 1 Hz, ali **isključivo u radnoj memoriji (RAM)** bez upisa privremenih slika na klijentski NVMe/SSD kako bi se izbeglo habanje diska (0 Disk I/O) i I/O zagušenje tokom raspakivanja paketa.
2. **Real-Time Hardverska Telemetrija u Heartbeat-u (`/api/heartbeat`):** Web Cockpit u verziji v3.2.0 zahteva strukturisane telemetrijske metrike u svakom heartbeat paketu (`cpu_temp`, `cpu_percent`, `ram_used`, `ram_percent`) radi trenutnog grafičkog prikaza opterećenja i termalnog statusa svake klijentske mašine u floti.
3. **Daljinske Komande i UEFI Firmware Restart (`/api/devices/<id>/command`):** Potreba da serviser jednim klikom iz Web Cockpita pošalje komandu `reboot_uefi` klijentskoj radnoj stanici kako bi računar pri sledećem restartu direktno ušao u UEFI/BIOS podešavanja (ili izvršio standardni restart na Legacy BIOS mašinama).
4. **Automatsko Pakovanje `.ps1` Radnika u Dynamic Payload-u (`PayloadManager`):** Monolitni `PayloadManager.collect_payload_files` u `Skripte/ServisHub/hub_server.py` je prilikom skeniranja direktorijuma `Skripte/Modules/` prikupljao samo `.psm1` module (`if fname.endswith(".psm1"):`), zbog čega samostalne izvršne PowerShell skripte radnika (poput `WIS.ScreenCapture.ps1`) nisu ulazile u dinamički paket `wis-payload.zip` / `gwadt-payload.zip`.

---

## 2. Arhitektonska Odluka i Tehnička Implementacija

Za potpunu integraciju klijentskog ekosistema sa **Servis Hub v3.2.0** usvojena je arhitektura u verziji **WIS v2.15.2** koja obuhvata četiri ključna stuba:

### 2.1. 1 Hz Live Screen Mirror Radnik u Memoriji (`Skripte/Modules/WIS.ScreenCapture.ps1`)
- **Zero Disk I/O GDI+ Pipeline:**
  - Implementirana je samostalna PowerShell 5.1 skripta i modul `WIS.ScreenCapture.ps1` (**20,080 B**, SHA-256: `8aeb60a0f0b2dee1eb6ceac1694da627b64d7074a50a1750c974bc26f9fd895b`).
  - Koristi .NET GDI+ (`System.Drawing.Bitmap`, `System.Drawing.Graphics.CopyFromScreen`) za hvatanje primarnog ekrana, vrši visokokvalitetno bikubično skaliranje (`InterpolationMode.Bicubic`) na ciljnu širinu (podrazumevano 1280 px uz očuvanje proporcija) i kompresuje kadar kao JPEG (kvalitet 65) direktno u **`System.IO.MemoryStream`** u RAM-u.
  - Svi GDI+ objekti (`Graphics`, `Bitmap`, `EncoderParameters`, `MemoryStream`) eksplicitno se oslobađaju u `finally` blokovima (`.Dispose()`), čime je sprečeno curenje GDI hendlova i memorije tokom višesatnog rada.
- **Binarni HTTP Transport i Kooperativna Sinhronizacija:**
  - JPEG bajtovi se iz `MemoryStream.ToArray()` šalju direktno putem binarnog `HTTP POST` zahteva (`Content-Type: image/jpeg`) na `/api/devices/<device_id>/screen-frame` uz zaglavlje `X-Client-Timestamp`.
  - Za međuprocesnu kontrolu zaustavljanja koristi se imenovani sistemski događaj `System.Threading.EventWaitHandle` (`Global\WIS_ScreenCapture_StopEvent` sa automatskim fallback-om na `Local\WIS_ScreenCapture_StopEvent`), što omogućava trenutnu i čistu terminaciju pozadinskog radnika iz bilo kog procesa pozivom `Stop-ServisScreenCapture` ili `& WIS.ScreenCapture.ps1 -Stop`.

### 2.2. Proširena Hardverska Telemetrija i Daljinski UEFI Reboot (`Skripte/Modules/WIS.Telemetry.psm1`)
- **Senzorske Metrike (`Get-HardwareTelemetryMetrics`):**
  - U modulu `WIS.Telemetry.psm1` (**44,705 B**, SHA-256: `e9e7baf3dd55a04963e82cf24b222dc9d3103dffc3fde431d0d3c440777ad450`) implementirana je funkcija `Get-HardwareTelemetryMetrics` koja očitava:
    * `cpu_temp`: Temperatura procesora u °C (iz LibreHardwareMonitor WMI senzora ili `MSAcpi_ThermalZoneTemperature` u decikelvinima).
    * `cpu_percent`: Trenutno zauzeće procesora (`Win32_Processor.LoadPercentage`).
    * `ram_used` i `ram_percent`: Zauzeta radna memorija u GB i procentima (`Win32_OperatingSystem`).
  - Funkcija `Send-ServisHeartbeat` automatski ugrađuje ove metrike u svaki JSON heartbeat payload ka Servis Hub-u.
- **Izvršavanje Daljinskih Komandi (`Invoke-ServisRemoteCommand`):**
  - Prilikom prijema odgovora sa `/api/heartbeat` ili `/api/session/<id>/state`, klijent proverava niz `pending_commands`.
  - Za komandu `reboot_uefi`, `Invoke-ServisRemoteCommand` izvršava `shutdown.exe /r /fw /t 3` (direktan ulazak u UEFI firmware interfejs pri restartu). Ukoliko mašina radi u Legacy BIOS režimu ili `/fw` vrati ne-nula exit kod, automatski se aktivira fallback na standardni restart `shutdown.exe /r /t 3`.

### 2.3. Integracija u Životni Ciklus Instalacije (`FirstLogon.ps1`, `Phase6-Finalize.ps1`, `WIS.Cleanup.psm1`)
- **Rano Pokretanje u `FirstLogon.ps1` (Sekcija 10d):**
  - Neposredno pre pokretanja glavnog `WIS.UI.exe` interfejsa (sekcija `10c`), ubačena je sekcija `10d` koja locira `WIS.ScreenCapture.ps1`, čisti eventualne zaostale `WIS_ScreenCapture*` poslove i pokreće pozadinskog radnika preko `Start-Job -Name "WIS_ScreenCapture_Worker" -FilePath $screenWorkerScript -ArgumentList @("-RunAsJob")`.
- **Trostepena Terminacija u Fazi 6 (`Phase6-Finalize.ps1` i `WIS.Cleanup.psm1`):**
  - Pre izvršavanja završnog Anti-Leak čišćenja i brisanja foldera `C:\setup`, i u `Phase6-Finalize.ps1` i unutar `Invoke-Phase6Finalize` (`WIS.Cleanup.psm1`) implementirano je trostepeno zaustavljanje:
    1. Poziv `Stop-ServisScreenCapture` (signaliziranje `EventWaitHandle` događaja).
    2. Fallback poziv `& $screenWorkerPath -Stop`.
    3. Zaštitno gašenje i uklanjanje svih `WIS_ScreenCapture*` PowerShell poslova (`Stop-Job` + `Remove-Job -Force`), čime se oslobađaju svi fajl hendlovi nad `C:\setup` pre njegovog samouništenja.

### 2.4. Nadogradnja `PayloadManager`-a i Regeneracija Dynamic Payload Paketa
- U `Skripte/ServisHub/hub_server.py` (metoda `PayloadManager.collect_payload_files`) proširen je filter sa `if fname.endswith(".psm1"):` na `if fname.endswith(".psm1") or fname.endswith(".ps1"):`, čime se `WIS.ScreenCapture.ps1` automatski pakuje u obe putanje unutar ZIP arhive (`Skripte/Modules/WIS.ScreenCapture.ps1` i `Modules/WIS.ScreenCapture.ps1`).
- Na TrueNAS produkcionom serveru verifikovano je da modularni paket `servishub` v3.2.0 (`servishub/handlers.py` i `servishub/payload.py`) ostaje 100% očuvan i servira novogenerisani master paket iz `storage/cache/`.

---

## 3. Status Odluke

**Prihvaćeno (Accepted)** — Implementirano, kriptografski sinhronizovano, distribuirano na TrueNAS ServisHub i verifikovano u verziji **WIS v2.15.2**.

---

## 4. Metrike, SHA-256 Paritet i Distribucija (v2.15.2)

| Datoteka / Paket | Veličina (B) | SHA-256 Kontrolna Suma | Status Pariteta |
|---|---|---|---|
| **`WIS.ScreenCapture.ps1`** | 20,080 | `8aeb60a0f0b2dee1eb6ceac1694da627b64d7074a50a1750c974bc26f9fd895b` | 100% (Projekt, $OEM$, Storage, TrueNAS, ZIP) |
| **`WIS.Telemetry.psm1`** | 44,705 | `e9e7baf3dd55a04963e82cf24b222dc9d3103dffc3fde431d0d3c440777ad450` | 100% (Projekt, $OEM$, Storage, TrueNAS, ZIP) |
| **`WIS.Cleanup.psm1`** | 66,648 | `420df50353c5ef6163e74a699b9de17411937f9ad9e63acd9e2367bda8f7cf78` | 100% (Projekt, $OEM$, Storage, TrueNAS, ZIP) |
| **`FirstLogon.ps1`** | 31,575 | `82befc75145f30ac2e63eb15f3774be9505c1d37d75ba34768d4297faad55dda` | 100% (8 lokacija + TrueNAS + ZIP, 100% CRLF) |
| **`Phase6-Finalize.ps1`** | 9,253 | `f2d0afe071f85909ae235b22e81e99b49c7a33869fa4631e81a9bf134dd5aaf3` | 100% (4 lokacije + TrueNAS + ZIP) |
| **`wis-payload.zip` / `gwadt-payload.zip`** | **6,266,372** | `5473aae7ea61e3cff5040906d39280f4d987a4b1063c77adbbd887e5fabce1c7` | 100% (Lokalni keš, TrueNAS keš, SMB root, HTTP API) |

---

## 5. Automatizovana QA Verifikacija

Izvršena je sveobuhvatna automatizovana QA verifikacija (STRIKTNO BEZ MOCK-OVA) kroz namensku skriptu `Skripte/Test-Task135-ServisHub-v32-Integration-QA.py`:
- **Ukupno provera:** **78 / 78 POLOŽENO (100.0% PASS, 0 FAIL, Exit Code 0)**.
- **Blok 1 (PowerShell 5.1 AST sintaksa i završeci linija):** 11/11 PASS (`Verify-PowerShellSyntax.py` 0 grešaka).
- **Blok 2 (Funkcionalna inspekcija `WIS.ScreenCapture.ps1` i `WIS.Telemetry.psm1`):** 20/20 PASS.
- **Blok 3 (Integracija životnog ciklusa u `FirstLogon.ps1`, `Phase6-Finalize.ps1`, `WIS.Cleanup.psm1`):** 11/11 PASS.
- **Blok 4 (100% SHA-256 paritet na svim diskovnim, OEM i Storage lokacijama):** 18/18 PASS.
- **Blok 5 (Validacija `PayloadManager`-a i lokalnih ZIP paketa `6,266,372 B`):** 11/11 PASS.
- **Blok 6 (Živa E2E verifikacija TrueNAS ServisHub REST API-ja `http://192.168.170.254:8080`):** 7/7 PASS (`/api/payload/version` i živo preuzimanje `/api/payload/download` u RAM).

---

## 6. Povezane Beleške
- Glavni Indeks: [[index]]
- Pregled Arhitekture: [[architecture/overview]]
- Servisni Hub Arhitektura: [[architecture/servis-hub]]
- Ops Runbook: [[memory/ops-runbook]]
- Master Tehnička Dokumentacija v2.15.1: [[decisions/ADR-0133-master-technical-documentation-v2151]]
- Radni Nalog i Status: `second_brain/agent_bus/CURRENT_TASK.md` (WIS-TASK-135)
