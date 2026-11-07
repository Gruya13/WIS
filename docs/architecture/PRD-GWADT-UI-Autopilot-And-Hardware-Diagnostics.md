# Tehnička Specifikacija i PRD/RFC: GWADT UI Proširenje (Autopilot Tab i Hardverska Dijagnostika)

- **ID Dokumenta:** GWADT-RFC-0079
- **Projekat:** GWADT UI Engine (`gwadt-ui-engine` v2.6.0)
- **Status:** Spremno za Implementaciju (Approved by Brainstormer)
- **Datum:** 2026-09-29
- **Autor:** Agency Brainstormer / GWADT UI Architect

---

## 1. Rezime i Problem Statement (Executive Summary)

### 1.1 Poslovni i Servisni Problem
U svakodnevnom radu servisa računara Tehno-Kutak, dva ključna rizika i uska grla usporavaju predaju računara klijentima:
1. **Rizik od Microsoft Autopilot / Intune zaključavanja:** Računari koji su ranije bili u vlasništvu korporacija često ostaju registrovani u Microsoft oblaku (Zero Touch Deployment - ZTD). Standardno formatiranje diska ne uklanja ovaj problem – računar se pri prvom spajanju na internet i OOBE ponovo zaključa na korporativni tenant. Postojeća skripta `Provera_autopilota.ps1` (v2.0) poseduje kompletnu determinističku 4-stupanjsku matricu stanja, ali se izvršava isključivo u zasebnom PowerShell konzolnom prozoru.
2. **Nepotpuna dijagnostika u postojećem UI-ju:** Trenutni tab `🩺 Dijagnostika` u `GWADT.UI.exe` prikazuje SSD SMART, bateriju i CPU/RAM opterećenje, ali serviser nema direktan uvid u:
   - Grafičku kartu (GPU model, VRAM, drajver),
   - Radne parametre displeja (rezolucija, osvežavanje u Hz, DPI skala),
   - Stanje drajvera u Device Manager-u (PnP Problem Devices sa žutim uzvičnikom),
   - Detalje matične ploče, BIOS-a (verzija, datum) i TPM 2.0 sigurnosnog čipa.

### 1.2 Cilj Rešenja
- Uvesti novi namenski tab **„🛡️ Autopilot“** u `GWADT.UI.exe` koji asinhrono preuzima podatke iz `Provera_autopilota.ps1` i prikazuje determinističku presudu u realnom vremenu uz mogućnost ručnog re-scana.
- Reorganizovati tab **„🩺 Dijagnostika“** u pregledan, moderan **modularni 6-kartični grid** koji objedinjuje celokupan hardverski profil i zdravlje sistema.

---

## 2. Korisničke Uloge i Tokovi (User Personas & Journey Maps)

### 2.1 Primarna Uloga: Serviser / Tehničar (Tehno-Kutak)
- **Cilj:** Brza, pouzdana i 100% tačna verifikacija da je računar čist od korporativnih naloga, da su svi drajveri ispravno instalirani i da su hardverske komponente funkcionalne pre pakovanja i isporuke.

### 2.2 Korisnički Tok (Core Journey - Happy Path):
1. Serviser pokreće `GWADT.UI.exe` (automatski kroz instalaciju ili samostalno u servisu).
2. UI se otvara; pozadinski radnik detektuje dostupnost mreže i asinhrono pokreće `Provera_autopilota.ps1 -JsonOutput`.
3. Serviser otvara tab **„🛡️ Autopilot“**:
   - Prikazuje se veliki zeleni statusni baner: **„[OK] 100% ČISTO - UREĐAJ JE POTPUNO SLOBODAN“**.
   - Ispod su taksativno navedeni hardverski otisak (SN, UUID, TPM 2.0), lokalna registarska provera (čisto) i status Microsoft ZTD oblaka (nije registrovan, server kod 807).
4. Serviser otvara tab **„🩺 Dijagnostika“**:
   - Kartica 1: SSD SMART zdravlje (100% Wear, 0 grešaka).
   - Kartica 2: Baterija (status ciklusa i zdravlja).
   - Kartica 3: CPU i RAM opterećenje u realnom vremenu.
   - Kartica 4: Grafika i Ekran (npr. *Intel Iris Xe Graphics, 1920x1080 @ 60Hz, 125% DPI*).
   - Kartica 5: Upravljač uređaja (*Zelena kvačica: Svi drajveri su ispravno instalirani - 0 problema*).
   - Kartica 6: Matična ploča & BIOS (*HP 840 G8, BIOS T76 v01.12.00, TPM 2.0 Spreman*).
5. Serviser završava pregled bez potrebe za otvaranjem `devmgmt.msc`, `msinfo32` ili zasebnih PowerShell skripti.

### 2.3 Tok kod Kritičnog Autopilot Zaključavanja (Alert Flow):
1. Asinhrona provera detektuje vezanost za tenant (Matrica 1 ili Matrica 3).
2. Tab **„🛡️ Autopilot“** prikazuje crveni alarm baner: **„[!] KRITIČNO - AKTIVNO VEZAN ZA KORPORATIVNI TENANT (Naziv Tenanta: firma.com)“**.
3. Prikazuje se upozorenje serviseru: *„FORMATIRANJE DISKA NE REŠAVA PROBLEM! Računar ne sme ići u prodaju dok se ne obriše iz Microsoft Intune portala!“*.
4. Oglasi se zvučni signal upozorenja (PC Speaker alert).

---

## 3. Matrica Zahteva (MoSCoW Prioritizacija)

### 3.1 Funkcionalni Zahtevi (Functional Requirements)
- **[MUST] FR-01: Proširenje `Provera_autopilota.ps1` sa `-JsonOutput` parametrom:**
  - Kada se pozove sa `-JsonOutput`, skripta suzbija interaktivni izlaz (`Write-Host`, `Clear-Host`, `Read-Host`) i na STDOUT vraća čist, validiran JSON objekat sa kompletnom strukturom nalaza.
  - Kada se pozove bez parametra, zadržava 100% unazadnu kompatibilnost sa postojećim kolor TUI konzolnim prikazom.
- **[MUST] FR-02: Novi tab „🛡️ Autopilot“ u WPF XAML interfejsu (`MainWindow.xaml`):**
  - Implementiran kao 6. tab u postojećem `TabControl` sa čistim nazivom `🛡️ Autopilot` (bez bedža na hederu, prema odluci korisnika).
  - Prikaz velikog statusnog banera sa bojom prema matrici stanja (Zelena, Žuta, Crvena, Siva dok traje analiza).
  - Prikaz 3 detaljna panela: Hardverski identifikatori, Lokalna forenzika (registar/sertifikati), Microsoft ZTD Cloud status.
  - Dugme „Ponovi analizu (Re-Scan)“ sa animacijom progresa / onemogućenim klikom tokom analize.
- **[MUST] FR-03: Asinhrono pokretanje i obrada u C# (`MainWindow.xaml.cs`):**
  - Pozadinski `Task.Run` pokreće `powershell.exe -NoProfile -ExecutionPolicy Bypass -File <scriptPath> -JsonOutput`.
  - Parsiranje JSON-a preko postojećeg `JsonHelper.cs` (C# 5.0 kompatibilan, bez JSON.NET DLL-a).
  - Dispatcher ažuriranje `AutopilotViewModel` svojstava.
- **[MUST] FR-04: Modularni 6-kartični grid u tabu „🩺 Dijagnostika“:**
  - Kartica 1: S.M.A.R.T. & SSD Zdravlje.
  - Kartica 2: Baterija & Napajanje.
  - Kartica 3: Procesor & Radna memorija (opterećenje).
  - Kartica 4: Grafička karta & Displej (GPU model, VRAM, rezolucija, frekvencija osvežavanja Hz, DPI skaliranje).
  - Kartica 5: Upravljač uređaja (PnP Problematic Devices - zeleni status ako nema grešaka, ili lista uređaja sa žutim uzvičnikom gde je `ConfigManagerErrorCode != 0`).
  - Kartica 6: Matična ploča, BIOS i TPM (Matična ploča model, BIOS verzija i datum, TPM 2.0 status čipa i SecureBoot).
- **[SHOULD] FR-05: Zvučna notifikacija upozorenja:**
  - Kratak PC Speaker ton (`System.Media.SystemSounds.Exclamation.Play()` ili `Console.Beep`) ukoliko se detektuje `LOCKED` Autopilot status.

### 3.2 Eksplicitno Van Opsega (Out-of-Scope)
- **[WON'T] OOS-01:** Automatsko brisanje Autopilot tenanta iz Intune portala (lokalni računar nema administratorska prava za korporativni Azure nalog).
- **[WON'T] OOS-02:** Automatsko preuzimanje drajvera sa interneta unutar taba dijagnostike (dijagnostika samo detektuje nedostajuće drajvere; instalacija je odgovornost Faze 2).
- **[WON'T] OOS-03:** Dinamički statusni bedž na hederu Autopilot taba (korisnik je eksplicitno izabrao čist naslov taba).

### 3.3 Nefunkcionalni Zahtevi (NFR)
- **C# 5.0 Kompatibilnost:** Sav C# kod mora se kompajlirati preko standardnog .NET Framework 4.0/4.8 MSBuild alata bez novijih C# 6+ jezičkih konstrukata (bez string interpolacije `$""`, bez `nameof()`, bez `?.`).
- **PowerShell 5.1 Kompatibilnost:** Skripta `Provera_autopilota.ps1` i moduli moraju raditi pod čistim Windows PowerShell 5.1 okruženjem.
- **UI Odzivnost:** Provera Autopilota i PnP drajvera ne sme blokirati UI nit (glavni prozor mora ostati 100% odzivan za praćenje faza instalacije).

---

## 4. Arhitektura i Tehnički Dizajn

### 4.1 JSON Ugovor Između `Provera_autopilota.ps1` i C# UI-ja
Kada se skripta pozove sa `-JsonOutput`, izlaz na STDOUT je formatiran kao:

```json
{
  "timestamp": "2026-09-29T12:45:00",
  "matrix_code": "CLEAN",
  "status_level": "OK",
  "status_title": "100% CISTO - UREDJAJ JE POTPUNO SLOBODAN",
  "status_description": "Racunar nema lokalnih tragova, a Microsoft ZTD servis potvrdjuje da hardver nije registrovan.",
  "recommended_action": "Racunar je u potpunosti cist i bezbedan za rad bilo kog korisnika.",
  "hardware": {
    "manufacturer": "HP",
    "model": "HP EliteBook 840 G8 Notebook PC",
    "serial_number": "5CD1234XYZ",
    "smbios_uuid": "4C4C4544-0050-4E10-8042-C8C04F343832",
    "tpm_present": true,
    "tpm_ready": true
  },
  "local_forensics": {
    "has_traces": false,
    "tenant_domain": "",
    "tenant_id": "",
    "assigned_device_name": "",
    "policy_cache_found": false,
    "azure_ad_joined": false,
    "mdm_enrolled": false,
    "org_certificates_count": 0
  },
  "cloud_ztd": {
    "online_checked": true,
    "is_registered": false,
    "error_code": "807 - ZtdDeviceIsNotRegistered",
    "raw_status_message": "Slobodan (Microsoft potvrdio: Uredjaj nije registrovan)",
    "profile_tenant_domain": "",
    "profile_tenant_id": ""
  }
}
```

### 4.2 C# ViewModel Struktura (`Models.cs`)
Dodaju se novi modeli:
1. `AutopilotViewModel`:
   - `MatrixCode`, `StatusLevel`, `StatusTitle`, `StatusDescription`, `RecommendedAction`, `StatusBrush`
   - `HwManufacturer`, `HwModel`, `HwSerialNumber`, `HwSmbiosUuid`, `TpmStatusText`, `TpmStatusBrush`
   - `LocalTracesText`, `LocalTracesBrush`, `TenantDomain`, `TenantId`, `AzureAdStatus`, `MdmStatus`
   - `CloudCheckedText`, `CloudStatusText`, `CloudStatusBrush`, `ZtdErrorCode`
   - `IsScanning` (bool za prikaz spinnera / onemogućavanje dugmeta za re-scan)
   - `LastScanTimeString`
2. `PnpProblemDevice`:
   - `Name`, `DeviceID`, `StatusDescription`, `ErrorCode`
3. Proširenje `MainViewModel`:
   - `Autopilot` (instanca `AutopilotViewModel`)
   - `GpuName`, `GpuVram`, `GpuDriverVersion`
   - `DisplayResolution`, `DisplayRefreshRate`, `DisplayDpiScale`
   - `MotherboardModel`, `BiosVersion`, `BiosReleaseDate`, `SecureBootStatus`
   - `PnpProblemDevices` (`ObservableCollection<PnpProblemDevice>`)
   - `HasPnpProblems` (bool) i `PnpSummaryText`

---

## 5. Kriterijumi Gotovosti (Definition of Done)

- [ ] `Provera_autopilota.ps1` podržava `-JsonOutput` i uspešno validira JSON izlaz u PowerShell 5.1.
- [ ] `GWADT.Hardware.psm1` prikuplja GPU, Display, BIOS/Matičnu ploču i PnP greške.
- [ ] `Models.cs` sadrži sve C# 5.0 tipizirane modele i binding propertije.
- [ ] `MainWindow.xaml` sadrži:
  - 6-kartični modularni grid u tabu `🩺 Dijagnostika`.
  - Novi 6. tab `🛡️ Autopilot` sa velikim statusnim banerom, 3 info sekcije i dugmetom za re-scan.
- [ ] `MainWindow.xaml.cs` sadrži asinhronog radnika za pokretanje provere Autopilota i parsiranje JSON-a.
- [ ] `Verify-CSharpSyntax.py` prolazi sa **100% PASS** (0 grešaka, striktan C# 5.0).
- [ ] Testirano u scenarijima:
  - Online čisto (ZTD 807),
  - Offline provera (Fallback status),
  - Detekcija PnP drajvera koji nedostaju.
- [ ] Ažuriran `istorija_izrade.md` sa opisom realizovanog posla.

---

## 6. Direktive za Multi-Agent Tim (Squad Handover Directives)

### 🏛️ Za Orkestratora (`gwadt_orchestrator`):
1. **Faza 1 (PowerShell & Forenzika):** Delegirati agentu `gwadt_powershell_expert` ažuriranje `Provera_autopilota.ps1` sa `-JsonOutput` prekidačem i proširenje `GWADT.Hardware.psm1`.
2. **Faza 2 (C# UI & XAML Dizajn):** Delegirati agentu `gwadt_ui_architect` ažuriranje `Models.cs`, `MainWindow.xaml` (novi tab i 6-kartični grid) i `MainWindow.xaml.cs` (asinhroni radnik i IPC).
3. **Faza 3 (QA & Verifikacija):** Delegirati agentu `gwadt_qa_debugger` pokretanje `Verify-CSharpSyntax.py`, sintaksnu proveru PowerShell koda i testiranje JSON parsinga.
4. **Faza 4 (Dokumentacija):** Delegirati agentu `gwadt_docs_sync` ažuriranje `istorija_izrade.md`.

