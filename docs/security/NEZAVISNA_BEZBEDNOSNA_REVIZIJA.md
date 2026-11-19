# Interna Bezbednosna Revizija (Security Self-Audit) — WIS Ekosistem (`IG-Systems`)

- **Vlasnik sistema:** `IG-Systems`
- **Datum revizije:** `2026-11-17` (sanacija: `2026-11-18` / `WIS-TASK-249`; dopunsko očvršćavanje: `2026-11-19` / `WIS-TASK-253` & `WIS-TASK-254`)
- **Aktuelna verzija:** `WIS v3.1.1` / `ServiceHub v3.4.1` (polazna Git baza prvobitne revizije: `3.0.0` / `3.3.0`)
- **Vrsta pregleda:** Interna tehnička bezbednosna samoprocena (*Security Self-Audit*, nije eksterni audit treće strane).
- **Opseg revizije:** Sve razvojne celine (`ISO/`, `Launcher/`, `UI/`, `ServiceHub/`), forenzički alat (`Tools/AutopilotAuditor/`), mehanizmi potpisivanja i objave, upravljanje tajnama, TrueNAS Docker konfiguracija i Git higijena.
- **Metodologija:** Statička analiza izvornog koda (C# 4.0/5.0, PowerShell 5.1, Python 3, Batch/XML), pregled granica poverenja (*Trust Boundaries*), provera kriptografskih tokova (`RSA-2048` / `SHA-256`), HTTP/API regresioni testovi i provera Git indeksa/tajni.

---

## 1. Izvršni Rezime (Executive Summary)

WIS ekosistem poseduje **zrelu kriptografsku i anti-leak osnovu** u ključnim tačkama distribucije paketa i završnog čišćenja:
- OTA distribucija počiva na `RSA-2048` PKCS#1 v1.5 potpisu kanonskog manifesta (`schema_version = 2`), obaveznoj proveri SHA-256 za ZIP i svaku datoteku iz manifesta, binarnom pregledu ZIP strukture protiv *Zip-Slip* i *symlink* napada, te transakcionom rollback-u u `WIS.Launcher.exe`.
- Završna faza (`WIS.Cleanup.psm1`) poseduje eksplicitnu `anti_leak_verified` kapiju koja blokira predaju računara ukoliko zaostane `Wifi-Servis.ini`, privremeni Wi-Fi XML ili servisni WLAN profil, uz strogu zaštitu priključenog servisnog USB diska.
- `ServiceHub` poseduje snažne ZFS i sistemske zaštitne mehanizme (`servishub/wis/usb.py`), dok je u **WIS v3.1.0 (`WIS-TASK-253`)** iz celokupnog repozitorijuma, build pipeline-a i OTA ugovora potpuno uklonjen `LibreHardwareMonitorLib.dll` (`WinRing0`, `CVE-2020-14979`) i `HidSharp.dll`, uz uvođenje post-verifikacije hardeninga (`$Global:WisSecurityHardeningVerified`), `Remove-LegacyWinRing0Driver`, fail-closed `ServiceHub v3.4.0` autentifikacije i CORS allowlist-a.

Prvobitna interna samoprocena identifikovala je **7 bezbednosnih nalaza** (`2 HIGH`, `3 MEDIUM`, `2 LOW`). Svi nalazi (`SEC-WIS-001` .. `SEC-WIS-007`) su **SANIRANI / REŠENI** (`WIS-TASK-249`, `2026-11-18`) i dodatno ojačani kroz konsolidovanu eksternu sanaciju u **`WIS v3.1.0` / `ServiceHub v3.4.0`** (`WIS-TASK-253`, `2026-11-19`; [`ADR-0253`](../decisions/ADR-0253-wis-3-1-0-security-hardening-and-audit-remediation.md)).

---

## 2. Verifikovane Postojeće Bezbednosne Kontrole (Potvrđene Snage)

Revizijom izvornog koda potvrđeno je 12 funkcionalnih bezbednosnih kontrola:

| ID | Kontrola | Lokacija u Kodu | Status |
| :--- | :--- | :--- | :--- |
| **CTRL-01** | `RSA-2048` + `SHA-256` verifikacija OTA manifesta i svakog fajla | `Launcher/PayloadManager.cs` (`ParseAndVerifyManifest`, `VerifyRsaSha256Signature`), `ServiceHub/servishub/releases.py` (`verify_release`) | **VERIFIKOVANO** |
| **CTRL-02** | Read-only `ServiceHub` payload bez privatnog ključa na serveru | `ServiceHub/servishub/payload.py` (`PayloadManager`), `ServiceHub/trusted_public_key.xml` | **VERIFIKOVANO** |
| **CTRL-03** | Zabrana ugradnje tajni pri pakovanju (`forbidden()`) | `ServiceHub/servishub/releases.py:62-67`, `ISO/Skripte/Release-WIS.py:98-99` | **VERIFIKOVANO** |
| **CTRL-04** | Binarni .NET 4.0 *Zip-Slip*, *ZIP64*, *Encrypted* i *ReparsePoint* validator | `Launcher/PayloadManager.cs:1513-1721` (`ValidateZipEntriesAgainstZipSlip`, `ContainsReparsePointsRecursive`) | **VERIFIKOVANO** |
| **CTRL-05** | Brisanje nepotpisanih izvršnih fajlova i transakcioni rollback | `Launcher/PayloadManager.cs:1770-1828, 2057-2083` (`RecoverInterruptedUpdateIfNeeded`, `RemoveUnlistedPayloadExecutables`) | **VERIFIKOVANO** |
| **CTRL-06** | XML escaping za Wi-Fi profile i trenutno brisanje u `finally` | `ISO/Skripte/SetupComplete.ps1:295-333`, `ISO/media/oem/Setup/Scripts/PreOOBE-Network.ps1:58-96`, `UI/Skripte/Modules/WIS.Network.psm1:233-278` | **VERIFIKOVANO** |
| **CTRL-07** | Faza 6 Anti-Leak kapija sa zaštitom USB-a i UAC oporavkom | `UI/Skripte/Modules/WIS.Cleanup.psm1:677-909, 1301-1333` (`Clear-ServisWifiSecrets`, `Restore-ServisUacDefaults`) | **VERIFIKOVANO** |
| **CTRL-08** | Potpuna eliminacija `WinRing0` / LHM DLL-a i Anti-BYOVD | `UI/ThermalSensorEngine.cs` (bez `Assembly.LoadFrom`, WMI/ACPI/OEM), `WIS.Cleanup.psm1` (`Remove-LegacyWinRing0Driver`) | **VERIFIKOVANO (v3.1.0)** |
| **CTRL-09** | ZFS `zpool`, `fstype=zfs` i sistemske tačke montiranja guardrails | `ServiceHub/servishub/wis/usb.py:20-34, 62-187, 347-387` (`_evaluate_device_safety`, `format_uefi_usb`) | **VERIFIKOVANO** |
| **CTRL-10** | Sanitizacija naziva fajlova i atomski upis zakrpi/model pakova | `ServiceHub/servishub/api/updates.py:304-386, 717-745, 971-1042`, `ServiceHub/servishub/drivers/cache.py:18-22` | **VERIFIKOVANO** |
| **CTRL-11** | Ograničenje frejma ekrana (`10 MiB`) i `Content-Type` provera | `ServiceHub/servishub/handlers.py:982-1018` | **VERIFIKOVANO** |
| **CTRL-12** | Git indeks čist od servisnih tajni i privatnih ključeva | `.gitignore:47-57`, provera `git ls-files` (samo `Wifi-Servis.example.ini` i javni `trusted_public_key.xml`) | **VERIFIKOVANO** |

---

## 3. Katalog Identifikovanih i Saniranih Bezbednosnih Nalaza

### `SEC-WIS-001` — Nekontrolisana Putanja (`directory` / `iso_path`) na `ServiceHub` HTTP Rutama
- **Status:** **SANIRANO / REŠENO (`WIS-TASK-249`, `2026-11-18`)**
- **Ozbiljnost:** `HIGH` (**CVSS v3.1: `7.5`** — `AV:N/AC:L/PR:N/UI:N/S:U/C:H/I:N/A:N` za čitanje, odnosno povišen rizik pisanja na montirane volumen putanje)
- **Pogođene komponente:** `ServiceHub/servishub/handlers.py`, `ServiceHub/servishub/api/updates.py`, `ServiceHub/servishub/config.py`
- **Prvobitni nalaz:** HTTP rute `/api/updates/*`, `/api/drivers/model-pack/*` i `/api/wis/usb/burn` prihvatale su proizvoljan `directory` odnosno `iso_path` parametar bez ograničenja unutar konfigurisanih korenskih direktorijuma servera.
- **Primenjena sanacija:**
  1. Uvedene funkcije `validate_http_storage_directory` i `validate_http_iso_path` u `ServiceHub/servishub/api/updates.py` (strogo poređenje `os.path.commonpath`, odbijanje `..`, `\x00` i putanja van dozvoljenog skladišta uz HTTP `400`).
  2. Validacija povezana na sve standardne `ThreadingHTTPServer` (`handlers.py`) i FastAPI (`updates.py`) rute, uz uklanjanje slepe podrazumevane host putanje u `config.py`. Regresiono pokriveno u `ServiceHub/tests/test_updates_api.py`.

---

### `SEC-WIS-002` — Zaostale Privremene Oslabljene Bezbednosne Polise na Klijentskom Windows 11 Sistemu
- **Status:** **SANIRANO / REŠENO (`WIS-TASK-249`, `2026-11-18`)**
- **Ozbiljnost:** `HIGH` (**CVSS v3.1: `7.1`** — `AV:A/AC:L/PR:N/UI:N/S:C/C:L/I:H/A:N`)
- **Pogođene komponente:** `UI/Skripte/Modules/WIS.Cleanup.psm1` (`Restore-WisSecurityHardening`, `Restore-ServisUacDefaults`, `Start-FinalCleanup`, `Invoke-ServisCleanupAndFinalize`)
- **Prvobitni nalaz:** Tokom servisne instalacije spuštaju se SMB Guest/potpisivanje polise, privremeni Defender izuzeci, `SEE_MASK_NOZONECHECKS` / `LowRiskFileTypes` / `ZoneMap` / `Zones\1\1806`, te `AutoAdminLogon = 1`, dok je Faza 6 ranije vraćala isključivo UAC.
- **Primenjena sanacija:**
  1. Dodata i izvezena funkcija `Restore-WisSecurityHardening` u `UI/Skripte/Modules/WIS.Cleanup.psm1` koja vraća UAC (`ConsentPromptBehaviorAdmin = 5`, `PromptOnSecureDesktop = 1`), vraća SMB (`AllowInsecureGuestAuth = 0`, `RequireSecuritySignature = 1`), uklanja privremene Defender `ExclusionPath` izuzetke (uključujući detektovane eksterne diskove), uklanja `SEE_MASK_NOZONECHECKS` (`Machine`/`User`), `LowRiskFileTypes` i privremenu `ZoneMap\Ranges\Range1` (`192.168.*.*`), resetuje `Zones\1\1806 = 1`, postavlja `AutoAdminLogon = 0` i briše `DefaultPassword`.
  2. Funkcija je povezana u `Restore-ServisUacDefaults`, `Start-FinalCleanup` i `Invoke-ServisCleanupAndFinalize`.

---

### `SEC-WIS-003` — Nedostatak XML Escaping-a i Zaostali Obrazac Privremenog Fajla u `Launcher/WifiHelper.cs`
- **Status:** **SANIRANO / REŠENO (`WIS-TASK-249`, `2026-11-18`)**
- **Ozbiljnost:** `MEDIUM` (**CVSS v3.1: `5.9`** — `AV:L/AC:L/PR:L/UI:N/S:U/C:H/I:L/A:N`)
- **Pogođene komponente:** `Launcher/WifiHelper.cs`, `UI/Skripte/Modules/WIS.Cleanup.psm1` (`Clear-ServisWifiSecrets`)
- **Prvobitni nalaz:** `WifiHelper.cs` je koristio sirov `string.Format(..., ssid, password)` pri formiranju `<WLANProfile>` XML-a, a privremeni `WIS_TempWifi_*.xml` fajlovi nisu bili obuhvaćeni wildcard čišćenjem u `Clear-ServisWifiSecrets`.
- **Primenjena sanacija:**
  1. U `Launcher/WifiHelper.cs` primenjen `System.Security.SecurityElement.Escape()` za `ssid` i `password`, uz podršku za `OPEN`/`NONE` i `WPA3SAE` autorizaciju (verifikovano unit testovima u `Launcher/Tests/run_tests.py`).
  2. U `Clear-ServisWifiSecrets` (`WIS.Cleanup.psm1`) dodato brisanje i verifikacija odsustva `WIS_TempWifi_*.xml` u `$env:TEMP` i `C:\Windows\Temp`.

---

### `SEC-WIS-004` — Odsustvo Limita Veličine Tela za JSON/Upload POST Zahteve i HTTP Bezbednosnih Zaglavlja
- **Status:** **SANIRANO / REŠENO (`WIS-TASK-249`, `2026-11-18`)**
- **Ozbiljnost:** `MEDIUM` (**CVSS v3.1: `5.3`** — `AV:N/AC:L/PR:N/UI:N/S:U/C:N/I:N/A:H`)
- **Pogođene komponente:** `ServiceHub/servishub/handlers.py`
- **Prvobitni nalaz:** JSON i binarne `POST` upload rute nisu imale gornju granicu za `Content-Length`, a HTTP odgovori nisu slali zaštitna bezbednosna zaglavlja.
- **Primenjena sanacija:**
  1. Uvedeni limiti `MAX_JSON_BODY_BYTES = 16 * 1024 * 1024` (16 MiB) i `MAX_PACKAGE_UPLOAD_BYTES = 6 * 1024 * 1024 * 1024` (6 GiB) sa HTTP `413 Payload Too Large` odgovorom pri prekoračenju.
  2. Uveden helper `_send_security_headers()` koji na JSON, binarne, static i dashboard HTML odgovore dodaje `X-Content-Type-Options: nosniff`, `X-Frame-Options: SAMEORIGIN` i `Referrer-Policy: no-referrer`.

---

### `SEC-WIS-005` — Preširoka NTFS `Everyone:Full` Dozvola i Bezuslovno Prihvatanje TLS Sertifikata u Alatu
- **Status:** **SANIRANO / REŠENO (`WIS-TASK-249`, `2026-11-18`)**
- **Ozbiljnost:** `MEDIUM` (**CVSS v3.1: `4.8`** — `AV:A/AC:H/PR:N/UI:N/S:U/C:L/I:L/A:N`)
- **Pogođene komponente:** `ISO/FirstLogon.cmd`, `ISO/FirstLogon.ps1`, `UI/Skripte/Modules/WIS.Core.psm1`, `Tools/AutopilotAuditor/CloudZtdInspector.cs`
- **Prvobitni nalaz:** Skripte su dodeljivale `*S-1-1-0:(OI)(CI)F` (*Everyone Full Control*) na `C:\setup` putanje, a `CloudZtdInspector.cs` je koristio bezuslovni TLS callback `=> true`.
- **Primenjena sanacija:**
  1. `*S-1-1-0:(OI)(CI)F` zamenjen je sa `*S-1-5-32-545:(OI)(CI)RX` (`Users: Read & Execute`) uz puna prava za `SYSTEM` i `Administrators` u `FirstLogon.cmd`, `FirstLogon.ps1` i `WIS.Core.psm1`.
  2. U `Tools/AutopilotAuditor/CloudZtdInspector.cs:329` callback je zamenjen strogom proverom `sslPolicyErrors == System.Net.Security.SslPolicyErrors.None`.

---

### `SEC-WIS-006` — Nepotpuno Maskiranje `<keyMaterial>` i `net use` Akreditiva u Deljenom Loggeru
- **Status:** **SANIRANO / REŠENO (`WIS-TASK-249`, `2026-11-18`)**
- **Ozbiljnost:** `LOW` (**CVSS v3.1: `3.3`** — `AV:L/AC:L/PR:L/UI:N/S:U/C:L/I:N/A:N`)
- **Pogođene komponente:** `UI/Skripte/Modules/WIS.Common.psm1` (`Write-ServisLog`), `UI/Skripte/Modules/WIS.Core.psm1` (`Write-ServisLog`)
- **Prvobitni nalaz:** Deljeni PowerShell logger nije maskirao `<keyMaterial>...</keyMaterial>` i argumente lozinke kod `net use` komandi.
- **Primenjena sanacija:**
  1. U `Write-ServisLog` (u `WIS.Common.psm1` i `WIS.Core.psm1`) dodata automatska regex sanitizacija za `<keyMaterial>...</keyMaterial>` i `net use <putanja> <lozinka>` obrasce pre upisa na disk i prenosa na `ServiceHub`. *(Napomena: zamena `net.exe` poziva Win32 P/Invoke `WNetAddConnection2W` mehanizmom u `Launcher/PayloadManager.cs` ostaje opciono buduće unapređenje.)*

---

### `SEC-WIS-007` — Odsustvo API Autentifikacije za Destruktivne Hub Rute uz `privileged: true` Kontejner
- **Status:** **SANIRANO / REŠENO (`WIS-TASK-249`, `2026-11-18`)**
- **Ozbiljnost:** `LOW` (**CVSS v3.1: `3.1`** — `AV:A/AC:H/PR:N/UI:N/S:U/C:N/I:L/A:H` u izolovanoj radioničkoj VLAN mreži)
- **Pogođene komponente:** `ServiceHub/servishub/config.py`, `ServiceHub/servishub/handlers.py`, `ServiceHub/docker-compose.yml`, `ServiceHub/.env.example`
- **Prvobitni nalaz:** Destruktivne/operaterske HTTP rute nisu podržavale administratorsku autentifikaciju.
- **Primenjena sanacija:**
  1. Uveden opcioni `SERVIS_HUB_ADMIN_TOKEN` (`HubConfig.admin_token`, verifikacija preko `hmac.compare_digest` putem `Authorization: Bearer <token>` ili `X-ServisHub-Token`, HTTP `401 Unauthorized` kada je token podešen) selektivno za `/api/wis/usb/burn`, `/api/updates/prune` i `/api/devices/<id>/command`, dok klijentske heartbeat/download rute ostaju zero-touch na radioničkoj mreži.
  2. Konsolidovani `ServiceHub/Dockerfile`, `ServiceHub/docker-compose.yml` i `ServiceHub/.env.example` sa dokumentacijom za `SERVIS_HUB_ADMIN_TOKEN`.

---

## 4. Matrica Statusa Sanacije

| Prioritet | Nalazi | Status (`WIS-TASK-249`) | Uticaj na Izgradnju i Validaciju |
| :--- | :--- | :--- | :--- |
| **P1 (Visoki rizik)** | `SEC-WIS-001`, `SEC-WIS-002` | **SANIRANO / REŠENO** (`2026-11-18`) | `ServiceHub` regresioni testovi prošli; `WIS.Cleanup.psm1` izmenjen i zahteva osveženje lokalnog/OTA potpisanog paketa. |
| **P2 (Srednji rizik)** | `SEC-WIS-003`, `SEC-WIS-004`, `SEC-WIS-005` | **SANIRANO / REŠENO** (`2026-11-18`) | Sva 4 C# binara (`WIS.Launcher.exe`, `WIS.UI.exe`, `WIS.HwTest.exe`, `WIS-AutopilotAuditor.exe`) uspešno rekompajlirana. |
| **P3 (Higijena i dubinska odbrana)** | `SEC-WIS-006`, `SEC-WIS-007` | **SANIRANO / REŠENO** (`2026-11-18`) | Potvrđeno kroz PowerShell sintaksne provere, `Launcher` testove i `ServiceHub` API regresione testove. |

---

## 5. Rezultat Verifikacije Aktivnog Stanja

- **Istorijska polazna tačka (`2026-11-17`):** Pre izmena u `WIS-TASK-249`, potvrđena je validnost prethodnog potpisanog OTA paketa (`release_id`: `9ad1a8c873f02f9b4d4d93823c3e0e1de0f5d576-d1747e6bd3a0a527`) i Windows VM validacija za bazu `65d8cae74b30e2912da71832589a7b6fabee31d5`.
- **Stanje nakon sanacije (`2026-11-18` / `WIS-TASK-249`):** Svi bezbednosni nalazi `SEC-WIS-001` .. `SEC-WIS-007` sanirani su u kodu, verifikovani lokalnim Python/C#/PowerShell testovima i sintaksnim proverama na Linux hostu, dok je za novu produkcionu distribuciju neophodno ponovno potpisivanje `wis-payload.zip` paketa (`Release-WIS.py publish`) i završna Windows VM validacija na realnom okruženju.
- **Git higijena:** `git ls-files` i `.gitignore` garantuju da nijedan `Wifi-Servis.ini`, `.pem`, `.key`, `.pfx` niti `*WLAN*.xml` fajl nije praćen u Git repozitorijumu. Licence komponenti trećih strana dokumentovane su u [`THIRD-PARTY-NOTICES.md`](../../THIRD-PARTY-NOTICES.md).
