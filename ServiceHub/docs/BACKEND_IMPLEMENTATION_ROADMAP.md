# Tehnička Specifikacija i Plan Implementacije (Roadmap)
## ServisHub v3.1: Klijentska i Backend Implementacija Napredne Telemetrije

Ovaj dokument definiše arhitekturu, specifikaciju API ugovora i tehničke zahteve za implementaciju funkcionalnosti koje zahtevaju klijentskog agenta na radnim stanicama u servisu i podršku na ServisHub backend-u. 

U skladu sa **Strict No-Mock Policy**, u korisničkom interfejsu (Web Dashboard) **nema simuliranih ili lažiranih podataka**. Tamo gde klijentski agent još uvek nije poslao telemetriju, prikazuje se eksplicitan status čekanja telemetrije (`-- °C`, `N/A (Čeka telemetriju)`), a za video/skrinšot ogledalo ekrana prikazuje se status:
> *Povezano na uređaj. Čeka se klijentski video/skrinšot strim sa radne stanice (Pogledaj: docs/BACKEND_IMPLEMENTATION_ROADMAP.md)*

---

## 1. Arhitektonski Pregled Komponenti

```
+-----------------------------------------------------------+
|               Radna Stanica (Klijent u Servisu)           |
|                                                           |
|  +---------------------+      +------------------------+  |
|  |  Screen Capture     |      |  Hardware Monitor      |  |
|  |  (1 Hz JPEG Frame)  |      |  (Temp, CPU, RAM, SSD) |  |
|  +----------+----------+      +-----------+------------+  |
|             |                             |               |
+-------------|-----------------------------|---------------+
              | POST /screen-frame          | POST /heartbeat
              v                             v
+-----------------------------------------------------------+
|               ServisHub Centralni Server                  |
|                                                           |
|  +---------------------+      +------------------------+  |
|  | Frame Buffer (RAM)  |      | FleetStore (InMemory)  |  |
|  +----------+----------+      +-----------+------------+  |
|             |                             |               |
+-------------|-----------------------------|---------------+
              | GET /screen-frame.jpg       | GET /api/devices
              v                             v
+-----------------------------------------------------------+
|       Web Dashboard Cockpit (Linear/Vercel Dark Mode)     |
+-----------------------------------------------------------+
```

---

## 2. Funkcionalnost 1: 1 Hz Live Ogledalo Ekrana (Screen Mirror)

### 2.1. Cilj
Prikaz stvarnog ekrana klijentskog računara u radionici na centralnom monitoru (Dashboard Cockpit) sa frekvencijom od 1 frejma u sekundi (1 Hz), bez potrebe za teškim VNC/RDP sesijama tokom automatske instalacije.

### 2.2. Klijentska Implementacija (Windows / WinPE / Setup Worker)
- **Komponenta:** Pozadinski servis ili PowerShell / C# skripta pokrenuta tokom pripreme i instalacije.
- **Tehnologija:**
  - `System.Drawing.Graphics.CopyFromScreen` (.NET / PowerShell) ili DirectX Desktop Duplication API (C++).
  - Skaliranje na 1280x720 ili 960x540 piksela radi uštede propusnog opsega.
  - Kompresija u JPEG sa kvalitetom 65–75% (prosečna veličina slike 40–80 KB).
- **Protok:**
  - Slanje svakih 1000 ms putem HTTP POST poziva:
    ```http
    POST /api/devices/{device_id}/screen-frame HTTP/1.1
    Host: 192.168.88.2:8888
    Content-Type: image/jpeg
    X-Client-Timestamp: 1728562300

    <raw JPEG binary data>
    ```

### 2.3. Backend Implementacija (ServisHub Server)
- **Endpoint:** `POST /api/devices/{device_id}/screen-frame`
  - Prihvata binarni JPEG podatak.
  - Čuva frejm u memorijskom baferu `self.fleet_store.latest_screen_frames[device_id] = (timestamp, jpeg_bytes)`.
  - Ograničava životni vek frejma (stale nakon 5 sekundi).
- **Endpoint za Dashboard:** `GET /api/devices/{device_id}/screen-stream` ili `GET /api/devices/{device_id}/screen-frame.jpg`
  - Servira najnoviji JPEG uz heder `Content-Type: image/jpeg` i `Cache-Control: no-cache`.
  - Omogućava i Server-Sent Events / Multipart MJPEG strim `multipart/x-mixed-replace; boundary=frame`.

---

## 3. Funkcionalnost 2: Proširena Hardverska Telemetrija (Termika, CPU, RAM)

### 3.1. Cilj
Prikaz stvarnih fizičkih očitavanja temperature procesora, iskorišćenja radne memorije i zauzeća procesora tokom instalacionih faza i opterećenja.

### 3.2. Proširenje Heartbeat Ugovora (`/api/heartbeat`)
Klijentski heartbeat payload se proširuje sledećim opcionim poljima:

```json
{
  "computer_name": "TK-DESKTOP-01",
  "phase": 4,
  "worker_progress": 72,
  "cpu_percent": 34,
  "cpu_temp": 58,
  "ram_used": "5.4 GB",
  "ram_total": "16.0 GB",
  "ram_percent": 33,
  "smart": {
    "SsdHealth": 98,
    "Category": "ČISTO",
    "Temperature": 41
  }
}
```

### 3.3. Klijentska Očitavanja:
- **CPU opterećenje:** `Get-CimInstance Win32_Processor | Measure-Object -Property LoadPercentage -Average`.
- **Temperatura procesora (Termika):**
  - ACPI ThermalZone preko WMI: `Get-CimInstance -Namespace "root/wmi" -ClassName "MSAcpi_ThermalZoneTemperature"`.
  - Alternativno: integrisani CLI alat `LibreHardwareMonitorLib.dll` ili `OpenHardwareMonitor`.
  - Ako mašina nema WMI termalne zone u WinPE okruženju, klijent ne šalje polje i Dashboard automatski prikazuje `-- °C` uz status `'N/A (Čeka telemetriju)'`.
- **Radna Memorija (RAM):**
  - `GlobalMemoryStatusEx` API ili `Win32_OperatingSystem` (FreePhysicalMemory vs TotalVisibleMemorySize).

---

## 4. Funkcionalnost 3: Daljinsko Upravljanje Radnim Stanicama (Remote BIOS/UEFI Reboot)

### 4.1. Cilj
Mogućnost da serviser iz Dashboard Cockpita pošalje komandu za restartovanje mašine direktno u UEFI/BIOS podešavanja ili ponovni PXE mrežni but.

### 4.2. Arhitektura Komandnog Kanala
- **Endpoint za slanje komande:** `POST /api/devices/{device_id}/command`
  ```json
  {
    "action": "reboot_uefi",
    "params": {}
  }
  ```
- **Klijentski prijem:**
  - Klijentski setup worker u sklopu periodičnog heartbeat-a (`POST /api/heartbeat`) dobija listu beklogovanih komandi u odgovoru:
  ```json
  {
    "status": "ok",
    "pending_commands": [
      { "id": "cmd_001", "action": "reboot_uefi" }
    ]
  }
  ```
- **Izvršavanje na klijentu:**
  - Za UEFI/BIOS reboot: `shutdown.exe /r /fw /t 0` (zahteva UEFI firmware podršku).
  - Za ponovni PXE boot: podešavanje privremenog BCD boot-next indikatora.

---

## 5. Funkcionalnost 4: Samostalni OEM API i Ekspanzija Provajdera (Dell, HP, Offline Fallback)

### 5.1. Cilj
Osamostaljivanje i standardizacija OEM podsistema (`servishub.drivers.oem`) kako bi centralni server omogućio ujednačen REST interfejs za identifikaciju modela, garancije i automatsko preuzimanje proizvođačkih paketa (SCCM / Driver Pack) bez oslanjanja na mock podatke, uz striktne mrežne zaštite (5s timeout i lokalni offline fallback).

### 5.2. Proširenje Provajdera:
- **Trenutno implementirano:** `LenovoOEMProvider` (eSupport API, LCSC serijski brojevi, SCCM katalog).
- **Planirano u v3.2.0:**
  - `DellOEMProvider`: Integracija sa Dell TechDirect / Driver Catalog API-jem na osnovu Service Tag-a.
  - `HpOEMProvider`: HP Image Assistant (HPIA) katalog i SoftPaq integracija na osnovu BaseBoard / System ID-ja.
  - **Samostalni Lokalni OEM Repozitorijum:** ZFS skladište na TrueNAS-u (`storage/drivers_cache/oem/{brand}/`) sa indeksiranim metapodacima u SQLite/JSON formatu za 100% offline radionice.

### 5.3. API Ugovor za OEM Drajvere i Garanciju:
```http
POST /api/drivers/oem-resolve HTTP/1.1
Content-Type: application/json

{
  "manufacturer": "Dell Inc.",
  "serial_number": "8X7Y6Z2",
  "model": "Latitude 5430",
  "os": "win11"
}
```
**Odgovor:**
```json
{
  "status": "ok",
  "provider": "dell",
  "warranty": {
    "status": "active",
    "service_level": "ProSupport Plus",
    "expires_at": "2026-11-30"
  },
  "package": {
    "name": "Latitude-5430-win11-A04.cab",
    "download_url": "http://192.168.88.2:8080/storage/drivers_cache/oem/dell/Latitude-5430-win11-A04.cab",
    "cached": true,
    "size_bytes": 1450289152
  }
}
```

---

## 6. Faze Implementacije i Prioriteti

| Prioritet | Funkcionalnost | Lokacija Koda | Zavisnosti i Status |
|---|---|---|---|
| **P1** | Proširenje Heartbeat-a (CPU, RAM, Temp, NVMe) u `telemetry.py` | `servishub/telemetry.py` | Nema zavisnosti, backend spreman za prijem |
| **P1** | Prikaz u Web Dashboardu sa `-- °C` i `N/A` zaštitom | `servishub/web/dashboard.py` | **Završeno** (Strict No-Mock Policy implementiran) |
| **P2** | In-Memory Screen Frame Buffer u `handlers.py` | `servishub/handlers.py` | `POST /api/devices/{id}/screen-frame` |
| **P2** | PowerShell Klijentski Frame Capture za WinPE | `payload/scripts/screen_capture.ps1` | Setup okruženje na USB-u / PXE |
| **P3** | Komandni mehanizam (`/api/devices/{id}/command`) | `servishub/handlers.py` | Daljinski UEFI reboot & PXE re-trigger |
| **P3** | Samostalni OEM API & Ekspanzija Provajdera (Dell/HP) | `servishub/drivers/oem/` | Dell/HP API konektori i lokalni ZFS keš |

---

## 7. Zaključak

Primenom ove specifikacije omogućava se postepena integracija 4 ključna sistema bez narušavanja integriteta sistema i bez oslanjanja na veštačke simulacije ili lažne mock podatke:
1. **1 Hz Screen Mirror** (klijentski JPEG bafer)
2. **Proširena Hardverska Telemetrija & NVMe Senzorika** (stvarna WMI očitavanja i SMART podaci)
3. **Daljinsko Upravljanje Radnim Stanicama** (Remote BIOS/UEFI Reboot)
4. **Samostalni OEM API** (Lenovo, Dell, HP drajver paketi i fabrička garancija)

Dashboard ostaje čist, tačan i transparentan prema serviserima i realnom stanju hardvera u radionici.
