---
title: "ADR-0007: Proširenje Senzorske Telemetrije, 1 Hz Screen Mirror, Daljinsko Upravljanje i OEM Ekspanzija za Dell i HP"
type: decision
status: accepted
created: 2026-10-10
updated: 2026-10-10
tags:
  - adr
  - decision
  - architecture
  - telemetry
  - screen-mirror
  - remote-command
  - uefi-reboot
  - oem
  - dell
  - hp
  - lenovo
  - servis-hub
---

# ADR-0007: Proširenje Senzorske Telemetrije, 1 Hz Screen Mirror, Daljinsko Upravljanje i OEM Ekspanzija za Dell i HP (v3.2.0)

## 1. Kontekst i Problem

Tokom eksploatacije sistema **ServisHub v3.1.0** u servisu računara "WIS Service", identifikovane su četiri ključne inženjerske potrebe za potpunu automatizaciju radionice i rad "iz fotelje":

1. **Slepe tačke u hardverskoj telemetriji:**
   Iako je Web Cockpit (redizajniran u ADR-0005 i specificiran u ADR-0006) pripremio vizuelne merne instrumente za CPU temperaturu, opterećenje i NVMe S.M.A.R.T. zdravlje, osnovni `POST /api/heartbeat` ugovor je prenosio samo fazu instalacije i radni napredak. Serviseri nisu imali centralizovan uvid u termalno stanje radnih stanica (rizik od termalnog trotlinga ili pregrevanja tokom raspakivanja slike) niti zdravlje SSD diskova u toku rada.
2. **Nepostojanje vizuelnog uvida u radne stanice (Screen Mirroring):**
   Tokom dugotrajnih instalacija Windows-a i aplikativnih paketa u WinPE i Windows okruženju, serviser je morao fizički prilaziti svakom radnom stolu kako bi video dijaloge, greške ili trenutni ekran instalera.
3. **Potreba za fizičkim pristupom radi ulaska u BIOS/UEFI i ponovnog butovanja:**
   Konfigurisanje redosleda butovanja, aktivacija Secure Boot-a ili priprema za mrežni PXE boot zahtevala je stalno ručno pritiskanje tastera (F2, F12, Del) na radnim stanicama. Bilo je neophodno obezbediti pouzdan, dvosmeran daljinski komandni kanal za slanje akcija poput `reboot_uefi` i `reboot_pxe`.
4. **Ograničenost OEM podsistema isključivo na Lenovo hardver:**
   ServisHub v3.1.0 je kroz `LenovoOEMProvider` podržavao proveru fabričke garancije, SCCM pakete i BIOS verzije samo za Lenovo uređaje. Međutim, u radionici "WIS Service", više od 60% servisirane flote čine poslovni i potrošački modeli **Dell** (Latitude, OptiPlex, Precision, XPS) i **HP** (EliteBook, ProBook, EliteDesk, ZBook).

---

## 2. Razmatrane Opcije

### Opcija A: Uvođenje teških eksternih alata (VNC/RDP, Prometheus/Node Exporter, namenski SSH demoni)
- *Prednosti:* Gotova rešenja za streaming slike i daljinsko izvršavanje.
- *Mane:* 
  - Izuzetno teška ili nemoguća integracija u minimalno WinPE okruženje bez mrežnog stacka punog Windows OS-a.
  - Zahteva otvorene dolazne portove na klijentima, fiksne IP adrese i složenu autentifikaciju.
  - Narušava osnovni princip projekta: **Zero-Dependency** i jednostavnost održavanja.

### Opcija B: Delimična implementacija (samo senzori bez daljinskog upravljanja i zadržavanje samo Lenovo provajdera)
- *Prednosti:* Manji obim razvoja na klijentskoj strani.
- *Mane:*
  - Ne rešava fizičko ustajanje servisera radi ulaska u UEFI.
  - Ostavlja većinu računara (Dell i HP) bez fabričke trijaže garancije i automatskih paketa drajvera.

### Opcija C: Potpuno integrisani Zero-Dependency ekosistem unutar ServisHub-a (v3.2.0) [PRIHVAĆENO]
- Uvođenje 1 Hz laganog JPEG bafera u memoriji servera (5s TTL) sa klijentskim PowerShell skriptama (`WIS.ScreenCapture.ps1`).
- Dvosmerni komandni FIFO kanal preko periodičnog `POST /api/heartbeat` pulsa (`pending_commands`), uz implementaciju `Invoke-ServisRemoteCommand` (`reboot_uefi`, `reboot_pxe`) u `WIS.Telemetry.psm1`.
- Proširenje senzorske telemetrije u `FleetStore` i REST ruti `/api/heartbeat` sa podrškom za CPU termiku, RAM opterećenje i NVMe SMART.
- Ekspanzija modularnog OEM podsistema implementacijom `DellOEMProvider` i `HpOEMProvider` provajdera sa striktnim 5s timeout-om, offline radioničkim kešom i potpunom integracijom u fabriku `get_oem_provider` i rutu `POST /api/drivers/resolve`.

---

## 3. Doneta Odluka i Arhitektonska Specifikacija

Usvojena je **Opcija C**. Sistem **ServisHub v3.2.0** kompletira sve 4 faze definisane u inženjerskoj mapi puta (`docs/BACKEND_IMPLEMENTATION_ROADMAP.md`):

```
+-----------------------------------------------------------------------------------------+
|                        SERVIS HUB v3.2.0 ARHITEKTURA SISTEMA                            |
+-----------------------------------------------------------------------------------------+
|                                                                                         |
|  [ Klijentska Radna Stanica (WinPE / Win11) ]                                           |
|       │                                                                                 |
|       ├─► WIS.ScreenCapture.ps1 (1 Hz)  ──[ POST /api/devices/{id}/screen-frame ]──────┐|
|       │   - .NET Drawing/Forms JPEG 70%                                                 │|
|       │                                                                                 │|
|       └─► WIS.Telemetry.psm1 (2s puls)  ──[ POST /api/heartbeat ]──────────────────────┐│|
|           - cpu_percent, cpu_temp                                                      ││|
|           - ram_used, ram_total, ram_percent                                           ││|
|           - smart (SsdHealth, Category, Temp)                                          ││|
|           - Preuzima: pending_commands ◄─────────────────────────────────────────────┐ ││|
|           - Izvršava: shutdown.exe /r /fw /t 3                                       │ ││|
|                                                                                      │ ││|
|  [ ServisHub Backend (ThreadingHTTPServer & FleetStore) ]                            │ ││|
|       ├─► 1 Hz Screen Frame Buffer (5s TTL, In-Memory Thread-Safe) ◄─────────────────┘ ││|
|       │       └─► GET /api/devices/{id}/screen-frame.jpg                                ││|
|       │                                                                                 ││|
|       ├─► Telemetry Store (Senzorska perzistencija & normalizacija) ◄───────────────────┘│|
|       │       └─► GET /api/devices (has_screen_stream, senzori, statusi)                │|
|       │                                                                                 │|
|       ├─► FIFO Command Queue (Maks. 20 komandi po uređaju) ─────────────────────────────┘|
|       │       ├─► POST /api/devices/{id}/command (Enqueue)                              │
|       │       └─► GET /api/devices/{id}/commands (Idempotentni pregled)                  │
|       │                                                                                 │
|       └─► Modularni OEM Podsistem (get_oem_provider)                                    │
|               ├─► LenovoOEMProvider (Lenovo eSupport, SCCM, BIOS)                       │
|               ├─► DellOEMProvider   (Dell Service Tag garancija, Deploy CAB, BIOS)      │
|               └─► HpOEMProvider     (HP Support API, SoftPaq / Driver Pack, BIOS)       │
|                                                                                         │
|  [ Korisnički Interfejs (Modern Enterprise Dark Mode Cockpit) ]                         |
|       ├─► Live 1 Hz Screen Mirror Viewport (LIVE / Standby indikator)                   |
|       ├─► Senzorski instrumenti: CPU %/°C, RAM %, NVMe SMART bar                        |
|       ├─► Quick Action Toolbar: "🔄 Reboot u UEFI", "🌐 Reboot u PXE"                   |
|       └─► Potvrdni bezbednosni modal (#command-confirm-modal)                           |
+-----------------------------------------------------------------------------------------+
```

### 3.1. Proširena Hardverska Telemetrija & NVMe Senzorika
- **Proširena struktura pulsa:** `POST /api/heartbeat` prihvata nova polja:
  - `cpu_percent` (float/int procenat, npr. `34.5` ili tekstualni `"35%"`).
  - `cpu_temp` (int/float u °C, podržava negativne vrednosti i stringove npr. `"58 °C"`).
  - `ram_used` (string sa jedinicom `"5.4 GB"` ili numerička vrednost u GB).
  - `ram_total` (string npr. `"16.0 GB"` ili broj).
  - `ram_percent` (int/float, npr. `33`).
  - `smart` (ugnježdeni rečnik sa atributima: `SsdHealth`, `Category`, `Temperature`, `TBW`).
- **Otpornost na parcijalna ažuriranja:** `FleetStore.update_fleet_heartbeat` primenjuje inkrementalno očuvanje stanja. Ako klijent u međuvremenu pošalje puls samo sa radnim progresom ili `None` vrednostima, ranije očitani senzorski podaci se ne brišu niti gaze.
- **Klijentski modul (`WIS.Telemetry.psm1`):** Funkcija `Get-ServisHardwareTelemetry` očitava WMI/ACPI termalne zone (`MSAcpi_ThermalZoneTemperature`, Win32_PerfFormattedData), Win32_OperatingSystem memoriju i `Get-PhysicalDisk` / SMART atribute.

### 3.2. 1 Hz Live Ogledalo Ekrana (Screen Mirror)
- **Klijentski radnik (`WIS.ScreenCapture.ps1`):**
  - Koristi ugrađeni .NET `System.Drawing` i `System.Windows.Forms` bez eksternih DLL biblioteka.
  - `CopyFromScreen` hvata primarni radni ekran, skalira ga na `MaxWidth` (standardno 960 px ili 1280 px) sa visokokvalitetnom bikubičnom interpolacijom i kompresuje u JPEG format (kvalitet 70%).
  - Šalje binarni frejm na `POST /api/devices/{id}/screen-frame` uz heder `Content-Type: image/jpeg` i `X-Client-Timestamp`.
  - Mrežni timeout je fiksiran na 800 ms kako bi se osiguralo da kašnjenje mreže ne zaguši klijentsku radnu nit.
  - Strogo oslobađa GDI+ resurse (`.Dispose()` nad Bitmap, Graphics i MemoryStream objektima u `finally` bloku) radi sprečavanja curenja memorije.
- **Serverski in-memory bafer (`FleetStore`):**
  - Thread-safe skladište u RAM-u: čuva se isključivo poslednji primljeni frejm po uređaju uz vremensku oznaku prijema.
  - Striktni TTL od 5.0 sekundi: ukoliko klijent prestane sa slanjem frejmova duže od 5s, `get_screen_frame` vraća `None`, a REST ruta `GET /api/devices/{id}/screen-frame.jpg` vraća HTTP 404 Not Found.
  - Ograničenje veličine: maksimalno 10 MB po frejmu uz odbacivanje ne-JPEG tela (HTTP 400/415).
  - `has_screen_stream`: automatski se evaluira u `get_devices_summary()` i signalizira Web Cockpitu da li je video strim aktivan.
- **Web Cockpit integracija (`dashboard.py`):**
  - Viewport dinamički prikazuje sliku sa `?t=${Date.now()}` keš-bastingom kada je `has_screen_stream == true`.
  - Kada frejmovi izostanu, viewport se elegantno prebacuje u Standby režim sa jasnom porukom za servisera.

### 3.3. Dvosmerno Daljinsko Upravljanje Radnim Stanicama
- **Komandni REST API:**
  - `POST /api/devices/{id}/command`: Prijem komande. Zahteva JSON telo `{"action": "reboot_uefi", "params": {...}}`. Generiše jedinstveni `command_id` (`cmd_<uuid>`), validira akciju i smešta je u FIFO red.
  - `GET /api/devices/{id}/commands`: Idempotentni pregled trenutno beklogovanih komandi u redu bez njihovog pražnjenja.
- **FIFO Red i Zaštita Memorije:**
  - Maksimalno 20 komandi po uređaju (`queue[-20:]`) kako bi se sprečilo preopterećenje memorije servera ako uređaj duže vreme nije na mreži.
- **Asinhrona distribucija kroz Heartbeat:**
  - Kada klijent pošalje `POST /api/heartbeat`, server atomski prazni komandni red za taj uređaj (`pop_pending_commands`) i vraća ih u telu JSON odgovora u polju `pending_commands: [...]`.
- **Klijentska egzekucija (`WIS.Telemetry.psm1`):**
  - Funkcija `Invoke-ServisRemoteCommand` obrađuje primljene akcije:
    * `reboot_uefi`: Pokreće `shutdown.exe /r /fw /t 3`. Ako mašina nema UEFI ili fw reboot nije podržan, radi graceful fallback na standardni restart `shutdown.exe /r /t 3`.
    * `reboot_pxe`: Priprema privremeni BCD unos i restartuje radnu stanicu.
    * `cancel` / `abort`: Prekida tekuće skripte ili operacije.
- **Bezbednosni UI Modal (`dashboard.py`):**
  - Klik na Quick Action dugme ("🔄 Reboot u UEFI" ili "🌐 Reboot u PXE") ne šalje komandu direktno, već otvara `#command-confirm-modal` koji zahteva eksplicitnu potvrdu servisera, čime se eliminiše slučajni restart radne stanice u toku instalacije.

### 3.4. Modularna OEM Ekspanzija za Dell i HP
- **Arhitektura Provajdera:**
  - `servishub.drivers.oem.base.BaseOEMProvider`: Definiše ugovor za sve brendove: `get_warranty_info(serial)`, `resolve_driver_pack(model, os_version)`, `check_bios_update(model, current_bios)`.
  - `DellOEMProvider` (`servishub/drivers/oem/dell.py`):
    * Podrška za Dell Service Tag (7 alfanumeričkih simbola).
    * Upiti o garanciji uz striktan mrežni timeout od 5s, lokalni JSON keš na disku (`storage/drivers_cache/oem/dell/warranty/`) i offline radionički katalog za modele bez interneta.
    * Rezolucija zvaničnih Dell Command | Deploy CAB paketa sa proverom lokalnog keša u `storage/drivers_cache/oem/dell/{model}/`.
    * Semantičko poređenje verzija i BIOS preporuke.
  - `HpOEMProvider` (`servishub/drivers/oem/hp.py`):
    * Podrška za HP serijske brojeve (10 alfanumeričkih znakova) i Product ID.
    * Upiti o garanciji uz 5s timeout, lokalno keširanje i graceful fallback.
    * Rezolucija zvaničnih HP SoftPaq / Driver Pack (HPIA) paketa sa keširanjem u `storage/drivers_cache/oem/hp/{model}/`.
    * Semantičko poređenje verzija HP BIOS/UEFI firmware-a.
- **Proširenje Fabrike (`get_oem_provider`):**
  - Prepoznavanje svih varijacija naziva proizvođača (npr. `"Dell"`, `"DELL INC."`, `"Dell Technologies"`, `"HP"`, `"Hewlett-Packard"`, `"Hewlett Packard Company"`, `"Lenovo"`).
  - Nepoznati proizvođači elegantno vraćaju `None` uz fallback na standardno Microsoft Update Catalog pretraživanje.

---

## 4. Posledice i Prednosti

### Pozitivne Posledice:
1. **Potpuni rad "Iz Fotelje" (100% Remote Workflow):** Serviser iz centralnog Cockpita u realnom vremenu vidi tačan ekran svakog računara na radnom stolu (1 Hz Screen Mirror) i može jednim klikom poslati komandu za restart u UEFI podešavanja.
2. **Hardverska Bezbednost i Rano Upozorenje:** Zahvaljujući prenosu stvarne temperature procesora i NVMe S.M.A.R.T. atributa, serviser odmah uočava defektne ventilatore, lošu termalnu pastu ili istrošene SSD diskove.
3. **Ekspanzija Tržišne Pokrivenosti Radionice:** ServisHub sada nativno pokriva tri najveća poslovna proizvođača računara na svetu (**Lenovo, Dell i HP**), koji zajedno čine više od 90% svih brendiranih mašina u radionici.
4. **Nula Eksternih Zavisnosti (Zero-Dependency):** Klijentske skripte koriste isključivo čisti PowerShell 5.1 i ugrađene .NET API-je (`System.Drawing`), a server ostaje na standardnoj Python biblioteci (`ThreadingHTTPServer`).
5. **Rigorozna Test Pokrivenost:** Sistem poseduje 214 automatizovanih testova koji prolaze u celosti, uz striktno poštovanje **Strict No-Mock politike**.

### Ograničenja i Kompromisi:
1. **1 Hz Frejm-rejt:** Screen Mirror je optimizovan za praćenje toka instalacije i tekstualnih dijaloga (propusni opseg ~50-100 KB/s po mašini), ali nije namenjen za video reprodukciju ili interaktivni daljinski desktop (kao npr. RDP).
2. **WinPE .NET Podrška:** Klijentska skripta `WIS.ScreenCapture.ps1` zahteva da WinPE slika ima uključene `WinPE-NetFX` i `WinPE-WMI` opcione komponente (standardno u WIS ISO graditelju).

---

## 5. Povezani Dokumenti i ADR-ovi

- [[decisions/ADR-0004-oem-and-wis-tools]] — Modularni OEM podsistem (Lenovo) i WIS alati
- [[decisions/ADR-0005-enterprise-dark-mode-cockpit-redesign]] — Enterprise Dark Mode Cockpit redizajn
- [[decisions/ADR-0006-stitch-design-application-and-no-mock-roadmap]] — Primena Stitch dizajna, No-Mock politika i mapa puta za 4 podsistema
- [Inženjerska Mapa Backend Implementacija (docs/BACKEND_IMPLEMENTATION_ROADMAP.md)](file:///home/gru/Projects/Servis%20Hub/docs/BACKEND_IMPLEMENTATION_ROADMAP.md) — Prvobitna specifikacija faza 1-4
- [[architecture/overview]] — Ažurirani pregled arhitekture sistema (v3.2.0)
- [[architecture/servis-hub]] — Tehnička specifikacija Servis Hub-a
