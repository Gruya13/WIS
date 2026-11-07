---
title: "ADR-0003: Integracija Antigravity CLI Servisera, Pre-flight Istorija i Sinhronizacija Sesija"
type: decision
status: accepted
created: 2026-10-09
updated: 2026-10-09
tags:
  - adr
  - decision
  - architecture
  - agy-serviser
  - telemetry
  - sync
  - rest-api
  - servis-hub
---

# ADR-0003: Integracija Antigravity CLI Servisera, Pre-flight Istorija i Sinhronizacija Sesija

## 1. Kontekst i Problem

U radionici "Tehno-Kutak", računarski serviseri koriste prenosivi dijagnostički alat **Tehno-kutak AGY serviser** (Antigravity CLI na USB fleš medijumu). Prilikom servisiranja Windows 10/11 računara, alat automatski očitava serijski broj i hardver (`Get-DeviceInfo.ps1`), kreira lokalni servisni karton u `devices/<DeviceTag>/` i vodi evidenciju u `SERVISNI_DNEVNIK.md` i `reports/IZVESTAJ_DIJAGNOSTIKE.md`.

### Ograničenja postojećeg stanja:
1. **Izolovanost znanja na USB flešu:**
   - Podaci ostaju isključivo na konkretnom fleš disku. Ako drugi serviser u radionici primi isti računar na reklamaciju ili naknadnu intervenciju, on nema uvid u ranije nalaze kolege.
2. **Rizik od gubitka i kvara medijuma:**
   - USB diskovi su skloni fizičkim oštećenjima i kvarovima kontrolera, što vodi ka trajnom gubitku servisne istorije.
3. **Write-Protect režim na zaključanom USB-u:**
   - Kada se radi sa hardverski zaključanog USB-a (fizički prekidač za zaštitu od malvera), sesija se privremeno odvija u klijentskom `%TEMP%` folderu i uništava po gašenju.
4. **Potreba za centralizovanim skladištem:**
   - ServisHub (v3.0.0) je već uspostavljen kao centralni kontrolni server radionice sa podrškom za perzistenciju izveštaja (`storage/reports`), in-memory evidenciju flote (`servishub.telemetry`) i UDP auto-discovery na portu 8888. Međutim, ServisHub trenutno nema namenski API ugovor prilagođen tokovima Antigravity CLI servisera.

---

## 2. Razmatrane Opcije

- **Opcija A: Prenos kompletnih sirovih LLM transkripata (Raw Chat Logs):**
  - *Nedostaci:* Veliki obim nestrukturisanog teksta, potencijalno curenje privatnih podataka klijenta (imena foldera, lični fajlovi zatečeni u konzoli), opterećenje skladišta i komplikovano pretraživanje.
- **Opcija B: Isključivo jednosmerni push statičkog JSON izveštaja na kraju sesije:**
  - *Nedostaci:* Serviser na klijentskom računaru nema povratnu informaciju (pull) šta je ranije rađeno na tom računaru ako istorija ne postoji na njegovom flešu.
- **Opcija C: Hibridni dvosmerni ugovor sa UDP Auto-Discovery, Pre-flight istorijom i Post-flight arhiviranjem [PRIHVAĆENO]:**
  - **Mrežni pronalazak:** USB klijent automatski detektuje ServisHub preko postojećeg UDP porta 8888 unutar LAN-a, uz rezervni (fallback) statički URL iz konfiguracije za rad van radionice.
  - **Pre-flight Pull:** Pre pokretanja AI sesije, klijent šalje upit za serijski broj. ServisHub vraća zbirni pregled ranijih popravki koji se odmah prikazuje u terminalu i prosleđuje u kontekst agenta.
  - **Post-flight Push:** Po završetku sesije, šalje se strukturirani paket: hardverska specifikacija, unosi u dnevnik, dijagnostički izveštaj i sažeti root-cause zaključak AI agenta.
  - **Offline tolerancija:** Ako klijent nema internet ili je Hub nedostupan, podaci se odlažu u red čekanja (`pending_sync/`) na USB-u i sinhronizuju naknadno.

---

## 3. Doneta Odluka i Tehnička Specifikacija

U ServisHub serveru implementira se proširenje REST API-ja i podsistema izveštaja za punu podršku Antigravity CLI servisnim sesijama:

### 3.1. REST API Rute

#### 1. `GET /api/devices/{device_tag}/history` (ili po serijskom broju `GET /api/devices/by-serial/{serial}/history`)
- **Svrha:** Očitavanje istorije intervencija pre početka rada.
- **Odgovor (200 OK):**
```json
{
  "status": "ok",
  "is_registered": true,
  "device_tag": "Dell_Inc__Latitude_5420_8G7X123",
  "serial_number": "8G7X123",
  "manufacturer": "Dell Inc.",
  "model": "Latitude 5420",
  "first_seen": "2026-08-15T10:30:00Z",
  "last_seen": "2026-09-10T14:22:00Z",
  "total_interventions": 2,
  "interventions": [
    {
      "date": "2026-09-10 14:20:00",
      "session_id": "a1b2c3d4-...",
      "summary": "Zamenjena termalna pasta, očišćen hladnjak od prašine. Temperature normalizovane.",
      "status": "COMPLETED",
      "tools_used": ["Get-DiskHealth", "Get-EventLogTriage"]
    }
  ],
  "dnevnik_markdown": "# Servisni Dnevnik Intervencija...\n"
}
```
- Ako uređaj nikada ranije nije evidentiran na Hub-u, vraća se HTTP 200 sa `"is_registered": false` i praznom listom intervencija (čime se klijentu omogućava čist start bez greške).

---

#### 2. `POST /api/devices/{device_tag}/agy-session`
- **Svrha:** Zaprimanje i centralno arhiviranje završene dijagnostičke sesije.
- **Telo Zahteva (JSON):**
```json
{
  "device_tag": "Dell_Inc__Latitude_5420_8G7X123",
  "serial_number": "8G7X123",
  "session_id": "f81d4fae-7dec-11d0-a765-00a0c91e6bf6",
  "timestamp": "2026-10-09T14:45:00Z",
  "serviser_id": "USB_TEREN_01",
  "hardware": {
    "manufacturer": "Dell Inc.",
    "model": "Latitude 5420",
    "cpu": "Intel Core i5-1145G7",
    "ram_gb": 16.0,
    "disks": "NVMe PC711 SK hynix 512GB (477 GB)",
    "os": "Windows 11 Pro 64-bit - Build 22631",
    "bios": "1.28.0 (2024-03-12)"
  },
  "summary": "Root cause: Detektovan NVMe SMART critical warning (Media Errors: 142). Preporučena hitna zamena diska.",
  "status": "CRITICAL_ISSUE",
  "tools_executed": ["Get-DiskHealth.ps1", "Get-EventLogTriage.ps1"],
  "dnevnik_entry": "### [Datum: 2026-10-09 14:45:00] - Otkaz NVMe diska\n- **Nalaz:** 142 nepopravljive greške na SK Hynix disku.\n- **Preporuka:** Kloniranje ili zamena SSD-a.",
  "report_markdown": "# Detaljni Izveštaj Servisne Dijagnostike...\n"
}
```
- **Odgovor (200 OK):**
```json
{
  "status": "ok",
  "message": "Servisna sesija uspešno arhivirana na ServisHub.",
  "device_tag": "Dell_Inc__Latitude_5420_8G7X123",
  "session_id": "f81d4fae-7dec-11d0-a765-00a0c91e6bf6",
  "json_archive": "storage/reports/Dell_Inc__Latitude_5420_8G7X123/sessions/f81d4fae.json",
  "dnevnik_updated": true
}
```

---

### 3.2. Skladištenje i Centralna Evidencija u ServisHub-u

1. **Struktura na Disku (`storage/reports/<DeviceTag>/`):**
   ```text
   storage/reports/Dell_Inc__Latitude_5420_8G7X123/
   ├── DEVICE_INFO.json               # Uvek ažuran zbirni hardverski profil
   ├── SERVISNI_DNEVNIK.md            # Spojeni centralni servisni dnevnik
   ├── latest_report.md               # Poslednji primljeni dijagnostički izveštaj
   └── sessions/                      # Istorijat svih pojedinačnih sesija
       ├── 2026-09-10_a1b2c3d4.json
       └── 2026-10-09_f81d4fae.json
   ```
2. **In-Memory Fleet Integracija (`servishub.telemetry.FLEET_STORE`):**
   - Po prijemu sesije, uređaj se registruje ili osvežava u `fleet_devices` tabeli.
   - Prikazuje se u Web Dashboard-u sa statusom `"Servisiran (AGY)"` ili `"Upozorenje / Kvar"` uz direktan uvid u poslednji zaključak.

---

### 3.3. Povezivanje sa Antigravity USB Klijentom

- Na strani USB klijenta (`Tehno-kutak AGY serviser`):
  1. Kreira se skripta `scripts/Sync-ServiceHub.ps1`.
  2. U `Start-ServisAI.ps1` se pre pokretanja CLI-ja izvršava:
     ```powershell
     $hubHistory = .\scripts\Sync-ServiceHub.ps1 -Pull -DeviceTag $device.DeviceTag -SerialNumber $device.SerialNumber
     ```
  3. U `finally` bloku `Start-ServisAI.ps1` izvršava se:
     ```powershell
     .\scripts\Sync-ServiceHub.ps1 -Push -DeviceFolder $device.DeviceFolder
     ```

---

## 4. Akcioni Plan Implementacije (Sprint Zadaci)

| Zadatak | Modul u ServisHub-u | Opis |
|---|---|---|
| **T1: REST Rute** | `servishub/handlers.py` | Dodavanje handler metoda za `GET /api/devices/{id}/history` i `POST /api/devices/{id}/agy-session`. |
| **T2: Modul za Perzistenciju** | `servishub/reports.py` | Funkcije `save_agy_session()` i `get_device_history()` koje održavaju `storage/reports/<DeviceTag>/`. |
| **T3: Telemetrija i Dashboard** | `servishub/telemetry.py` | Prikaz AGY sesija i statusa popravki u postojećoj evidenciji flote. |
| **T4: Test Pokrivenost** | `tests/test_agy_sync_api.py` | Pytest integracioni testovi za pull i push rute, validaciju JSON tela i rukovanje novim uređajima. |
| **T5: USB Klijentski Modul** | `Tehno-kutak AGY serviser` | Izrada `scripts/Sync-ServiceHub.ps1` sa UDP Auto-Discovery podrškom i offline redom čekanja. |

---

## 5. Status i Verifikacija

- **Status:** Prihvaćeno (Accepted).
- **Ciljna verzija ServisHub-a:** v3.1.0
- **Verifikacija:** Uspešan prolaz automatskih integracionih testova u `tests/test_agy_sync_api.py` i uspešna dvosmerna simulacija sa PowerShell klijentom.
