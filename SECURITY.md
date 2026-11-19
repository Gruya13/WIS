# Politika Bezbednosti (Security Policy) — WIS Ekosistem (`IG-Systems`)

Ova bezbednosna politika definiše obavezujuće bezbednosne zahteve, granice poverenja, mehanizme zaštite podataka i operativne kontrole za sve komponente **Windows Installation Assistant (WIS)** ekosistema u vlasništvu **IG-Systems** (`WIS v3.1.1` / `ServiceHub v3.4.1`).

---

## 1. Opseg i Matrica Podržanih Verzija

Politika se primenjuje na sve razvojne celine i alate definisane u kanonskom manifestu [`version.json`](version.json), uključujući proces izgradnje medijuma, distribuciju paketa, izvršavanje na klijentskim računarima i centralni nadzorni server.

| Komponenta / Celina | Verzija | Podržana | Uloga u Bezbednosnoj Arhitekturi |
| :--- | :--- | :--- | :--- |
| **WIS Paket Izdanja** | `3.1.1` | :white_check_mark: | Jedinstveni RSA-2048 potpisani OTA paket (`wis-payload.zip`) bez `WinRing0`/`LHM` DLL-ova (`REQUIRED_310_COMPONENTS`). |
| [`ISO/`](ISO/) | `3.1.1` | :white_check_mark: | WinPE / Unattend / `SetupComplete.ps1` / `PreOOBE-Network.ps1`, priprema medijuma i `Release-WIS.py` objava. |
| [`Launcher/`](Launcher/) | `3.1.1` (`min: 3.1.0`) | :white_check_mark: | Koren poverenja (*Root of Trust*) na klijentu (`WIS.Launcher.exe`): anti-downgrade zaštita (`>= 3.1.0`), RSA-2048 verifikacija, Zip-Slip/`.sys` zaštita i transakcioni rollback. |
| [`UI/`](UI/) i `WIS.HwTest` | `3.1.1` | :white_check_mark: | Master orkestrator faza (`WIS.UI.exe`), 6-fazni PowerShell 5.1 pajplajn, Anti-BYOVD senzorika, post-verifikacija OS hardeninga i Faza 6 Anti-Leak čišćenje. |
| [`ServiceHub/`](ServiceHub/) | `3.4.1` | :white_check_mark: | Read-only payload servis, fail-closed LAN autentifikacija (`SERVIS_HUB_ADMIN_TOKEN` / `CLIENT_TOKEN`), CORS allowlist, `.tar` sanitizacija i ZFS/disk zaštita u USB Builder-u. |
| [`Tools/AutopilotAuditor/`](Tools/AutopilotAuditor/) | `3.1.1` | :white_check_mark: | Troslojna Autopilot/ZTD forenzika uz `try/finally` očuvanje OOBE ključeva i keša. |
| Starije verzije (`< 3.1.0`) | `< 3.1.0` | :x: | Zastarele i blokirane podrazumevanom anti-downgrade zaštitom u `WIS.Launcher.exe` v3.1.0+. |

---

## 2. Model Pretnji i Granice Poverenja (Trust Boundaries)

WIS operiše preko četiri jasno razdvojene granice poverenja:

1. **Granica 1 — Build i Release Host (Koren Potpisivanja):**
   - Privatni ključ za potpisivanje (`wis-release-private.pem`) mora se čuvati isključivo van Git repozitorijuma, sa strogim dozvolama fajl sistema (`0600`).
   - Nijedna objava OTA paketa nije dozvoljena bez čistog Git commita, `build-attestation.json` potvrde (`windows_powershell: 5.1`, `validation: passed`) i potvrđenog `vm-validation.json` zapisa (`status: PASS`, `restart_verified: true`, `anti_leak_verified: true`).
2. **Granica 2 — Servisna LAN/WLAN i PXE Mreža (Nepouzdani Transport):**
   - Lokalni servisni saobraćaj (UDP broadcast na portu `8888`, HTTP API na `8080`, TrueNAS SMB `445`, iVentoy PXE) odvija se u servisnoj mreži bez TLS-a ili sa Guest/servisnim SMB pristupom.
   - Zbog toga se **mrežni transport tretira kao nepouzdan**: `HubDiscovery` (`Launcher/HubDiscovery.cs`) služi isključivo za pronalaženje mrežnih kandidata. Dostupnost endpointa ili SMB deljenja nikada nije dokaz autentičnosti; izvršavanje koda je blokirano sve dok `PayloadManager` ne potvrdi RSA-2048 potpis manifesta i SHA-256 sume svakog fajla.
3. **Granica 3 — Klijentski Računar u Servisu (`C:\setup\`):**
   - Tokom instalacije skripte i aplikacije rade pod povišenim privilegijama (`NT AUTHORITY\SYSTEM` i `Administrator`), uz privremeno spuštanje pojedinih Windows barijera radi automatizacije (UAC prompt, Defender skeniranje `C:\setup\`, SMB Guest pristup za Win11 24H2 NAS deljenje).
   - **Pre predaje računara kupcu**, Faza 6 (`WIS.Cleanup.psm1`) mora ukloniti sve servisne tajne, Wi-Fi profile, SMB sesije, privremene prečice i radni direktorijum `C:\setup\`, kao i vratiti podrazumevane bezbednosne polise operativnog sistema.
4. **Granica 4 — Eksterni Oblak (Microsoft Update, Microsoft Update Catalog, ZTD/Intune):**
   - Komunikacija ka eksternim servisima ograničena je na dohvatanje javnih zakrpi (`.msu`/`.cab`), OEM drajvera i očitavanje Microsoft Autopilot ZTD statusa bez slanja servisnih akreditiva.

---

## 3. Bezbednosna Politika po Komponentama

### 3.1. `ISO/` (WinPE, Unattend, SetupComplete i Release Pipeline)
- **Zabrana ugradnje tajni u medijume i arhive:** `Wifi-Servis.ini`, servisni WLAN XML profili (`*WLAN*.xml`, `*WiFi*.xml`), privatni ključevi (`*.pem`, `*.key`, `*.pfx`) i `.env` datoteke **ne smeju** biti ugrađeni u Git indeks, `boot.wim`, `install.wim`/`install.esd`, niti u potpisani `wis-payload.zip`.
- **Priprema medijuma (`ISO/Skripte/Prepare-WISMedia.py`):** Priprema `$OEM$` stabla dozvoljena je isključivo iz već potpisanog, commit-vezanog izdanja (`verify_release`), bez symlinkova (`reject_symlink_path`), uz čuvanje lokalne kopije `Wifi-Servis.ini` sa `0o600` permisijama van potpisanog manifesta.
- **Rukovanje Wi-Fi profilima (`SetupComplete.ps1`, `ISO/media/oem/Setup/Scripts/PreOOBE-Network.ps1`):**
  - SSID i lozinka moraju se XML-kodirati putem `[System.Security.SecurityElement]::Escape()` pre upisa privremenog profila.
  - Privremeni XML fajl mora biti obrisan u `finally` bloku odmah nakon `netsh wlan add profile`.
  - Logovanje mora automatski maskirati obrasce `Password=...` i `<keyMaterial>...</keyMaterial>`.
- **Kontrolisana objava (`ISO/Skripte/Release-WIS.py` i `ServiceHub/servishub/releases.py`):**
  - Zabranjuje symlinks, nekanonske putanje (`safe_name`), pakete iznad limita (`MAX_ZIP_BYTES = 256 MiB`, `MAX_UNPACKED_BYTES = 512 MiB`, max `4096` fajlova) i tajne otkrivene pomoću `forbidden()`.
  - Svaka datoteka u paketu mora imati zabeležen i potpisan SHA-256 heš u `required_files` (`schema_version = 2`), vezan za 40-karakterni Git `source_commit` i `release_id`.

### 3.2. `Launcher/` (`WIS.Launcher.exe` Thin-Client Bootstrapper)
- **Koren poverenja (*Root of Trust*):** RSA-2048 javni ključ (`TrustedPublicKeyXml`) ugrađen je direktno u `Launcher/PayloadManager.cs` i `Launcher/trusted_public_key.xml`. Zamena javnog ključa zahteva ponovnu kompilaciju i objavu Launchera.
- **Višestepena verifikacija pre izvršavanja (`ParseAndVerifyManifest`, `VerifyDirectoryMatchesManifest`):**
  1. Provera `product == "WIS"`, `schema_version == 2`, `parsed.Version >= MinAcceptedPayloadVersion` (`"3.1.0"` anti-downgrade zaštita), `min_launcher_version <= CurrentLauncherSemVer`, prisustva obaveznih komponenti (`MandatoryRelativeFiles` + `MandatoryRelease310Files`) i zabrane `LibreHardwareMonitorLib.dll`/`HidSharp.dll`/`WinRing0`.
  2. Provera RSA-2048 PKCS#1 v1.5 SHA-256 digitalnog potpisa nad kanonskim stringom `WIS|v2|<version>|<release_id>|<size>|<sha256>|<min_launcher_version>|<required_files_canonical_sha256>`.
  3. Verifikacija tačne veličine i SHA-256 heša preuzetog `wis-payload.zip` paketa (HTTP i SMB kanal podležu identičnoj verifikaciji).
- **Zaštita od napada na raspakivanje (`ValidateZipEntriesAgainstZipSlip`, `IsSafeRelativeZipEntry`, `ContainsReparsePointsRecursive`):**
  - Binarni parser ZIP Central Directory i Local File Header struktura odbija višedelne/ZIP64 arhive, šifrovane ulaze, Unix symlinkove, prazne/null putanje, `..`/`.`, apsolutne i UNC putanje, oznake diskova i NTFS ADS tokove (`:`), kao i rezervisana Windows imena (`CON`, `PRN`, `AUX`, `NUL`, `COM1..9`, `LPT1..9`).
  - Pre i posle ekstrakcije proverava se odsustvo NTFS ReparsePoint / junction / symlink tačaka unutar staging i `C:\setup\` stabla.
  - Nepotpisani izvršni fajlovi, kernel drajveri i skripte (`.exe`, `.dll`, `.sys`, `.ps1`, `.psm1`, `.cmd`, `.bat`, `.vbs`, `.js`, `.py`) van `required_files` automatski se odbijaju i brišu (`RemoveUnlistedPayloadExecutables`), sa jedinim dokumentovanim izuzetkom za spoljašnji installer keš `C:\setup\Programi\`.

### 3.2.1. Dokumentovana Poslovna Odluka — Lokalni Nalog `Korisnik` bez Lozinke (`ISO/autounattend.xml`)
- **Kontekst:** U `ISO/autounattend.xml` podrazumevano se kreira standardni lokalni nalog **`Korisnik`** bez početne lozinke, u skladu sa radioničkim modelom isporuke „ključ u ruke“ gde krajnji kupac prvi put pali računar kod kuće bez poznavanja servisne lozinke.
- **Bezbednosni okvir:** Tokom instalacije `Restore-WisSecurityHardening` garantuje gašenje `AutoAdminLogon = 0` i brisanje eventualne `DefaultPassword` vrednosti, dok Faza 6 briše sve servisne Wi-Fi profile, `Wifi-Servis.ini` i SMB sesije.
- **Preporuka pri predaji računara:** Krajnjem korisniku se pri preuzimanju računara preporučuje da odmah postavi ličnu lozinku ili Windows Hello PIN/otiskom prsta u Podešavanjima sistema Windows.
- **Transakciona izolacija:** Preuzimanje i ekstrakcija se izvršavaju pod `Global\WIS_Launcher_Update_Mutex` u izolovanim folderima `.update_attempt/` i `.staging_extract/`, sa sigurnosnom kopijom u `.backup_prev/` i dnevnikom `.update_tx.json` radi automatskog rollback-a u slučaju prekida.

### 3.3. `UI/` (`WIS.UI.exe`, `WIS.HwTest.exe` i 6-Fazni PowerShell Pajplajn)
- **Procesna izolacija i privilegije:** `WIS.UI.exe` obezbeđuje single-instance rad putem `Global\WIS_UI_SingleInstance_Mutex`, zahteva administratorske privilegije (`app.manifest` / `RunAs`), i registruje nastavak nakon međurestarta isključivo preko `HKLM\...\RunOnce`.
- **Integritet drajvera i zakrpi (`WIS.Hardware.psm1`, `WIS.Update.psm1`):**
  - `Select-RelevantDriverInfFiles` filtrira multi-OEM `.cab` pakete po stvarnom `SUBSYS` / `VEN_DEV` HWID otisku računara pre poziva `pnputil.exe`.
  - `Test-IsGenericComputerModel` zabranjuje čuvanje i preuzimanje deljenog `<Model>.tar` paketa za generičke identifikatore (`GenericPC`, `System Product Name`, `Default String` i sl.).
  - `Export-ServisHardwareDrivers` primenjuje gornji limit veličine od `5.0 GB` (`5368709120 B`), `$cleanModel.harvest.lock` zaštitu od paralelnog upisa i automatski uklanja tuđe `hdx*.inf_*` pakete.
  - `Invoke-NasUpdatePrePatch` i `Invoke-WindowsUpdateSecondPass` odbijaju `*AggregatedMetadata*.cab` i `wsusscan.cab`.
- **Zabrana ranjivih kernel drajvera (Anti-BYOVD — `CVE-2020-14979`):** Od `WIS v3.1.0`, `LibreHardwareMonitorLib.dll` i `HidSharp.dll` trajno su uklonjeni iz repozitorijuma i paketa; `ThermalSensorEngine.cs` nikada ne poziva `Assembly.LoadFrom` (nativni WMI/ACPI/PerfCounters/SMART + OEM WMI uključujući Lenovo, HP i Dell `DCIM_NumericSensor`), dok `WIS.Cleanup.psm1` (`Remove-LegacyWinRing0Driver`) uklanja eventualni nasleđeni servis `WinRing0_1_2_0`.
- **Zaštita `.tar` drajver paketa (`WIS.Update.psm1`):** `Install-DriverTarArchive` pre poziva `pnputil.exe` odbija pakete koji sadrže izvršne skripte (`.ps1`, `.psm1`, `.bat`, `.cmd`, `.vbs`, `.js`, `.wsf`, `.hta`) i loguje odsustvo `.cat` potpisa.
- **Obavezna Faza 6 Anti-Leak, Sentinel i Post-Verifikovana Hardening Politika (`WIS.Core.psm1`, `WIS.Cleanup.psm1`, `Phase6-Finalize.ps1`):**
  1. Tokom inicijalizacije okruženja (`Register-WisSecurityRecoverySentinel`) registruje se jednokratni `OnStartup` zadatak oporavka ako instalacija bude nasilno prekinuta pre Faze 6.
  2. Zaustavljanje `1 Hz Live Screen Mirror` radnika (`Stop-ServisScreenCapture`) pre završnog čišćenja i brisanje sentinel zadatka u Fazi 6.
  3. Raskidanje svih aktivnih SMB mrežnih sesija (`net use * /delete /y`), brisanje servisnih WLAN profila i svih kopija `Wifi-Servis.ini` / `WIS_TempWifi_*.xml` sa diska `C:\` (`anti_leak_verified`).
  4. **Vraćanje i post-verifikacija OS polisa (`Restore-WisSecurityHardening`):** Pored vraćanja UAC-a (`5`/`1`), SMB polisa (`AllowInsecureGuestAuth = 0`, `RequireSecuritySignature = 1`), brisanja Defender izuzetaka, `SEE_MASK_NOZONECHECKS`, `LowRiskFileTypes`, `ZoneMap` i `AutoAdminLogon = 0`, Faza 6 čitanjem registra i Defender konfiguracije **verifikuje** uspeh oporavka (`$Global:WisSecurityHardeningVerified`).
  5. **Dvostruka kapija predaje:** Ukoliko padne `anti_leak_verified` ili `security_hardening_verified`, Faza 6 beleži `NEEDS_ATTENTION`, postavlja `handover_ready = false`, izlazi sa kodom `2` i **blokira predaju računara kupcu**.

### 3.4. `ServiceHub/` (`v3.4.0` Centralni Server, Keš i USB Builder)
- **Read-only Payload Arhitektura (`servishub/payload.py`, `servishub/releases.py`):** Server poseduje isključivo javni ključ (`ServiceHub/trusted_public_key.xml`), proverava otisak fajlova (`REQUIRED_310_COMPONENTS` bez `WinRing0`/LHM DLL-ova) i RSA-2048 potpis pre serviranja, i nikada na HTTP zahtev ne generiše ili potpisuje novi `wis-payload.zip`.
- **Fail-Closed Autentifikacija, CORS Allowlist i Limiti (`servishub/handlers.py`, `config.py`):**
  - Administrativne i destruktivne rute (`/api/wis/usb/burn`, `/api/updates/prune`, `/api/devices/<id>/command`) zahtevaju `SERVIS_HUB_ADMIN_TOKEN` (uz HTTP `503 Service Unavailable` bez konfigurisanog tokena osim ako `SERVIS_HUB_ALLOW_UNAUTHENTICATED_LAN=true`).
  - Upload rute (`/api/updates/upload`, `/api/drivers/model-pack/upload`) podržavaju `SERVIS_HUB_CLIENT_TOKEN` (`X-ServisHub-Client-Token`).
  - Zamenjeno globalno `Access-Control-Allow-Origin: *` konfigurisanom `SERVIS_HUB_CORS_ORIGINS` listom; ograničen broj SSE pretplatnika (HTTP `429`), veličina zahteva (`MAX_JSON_BODY_BYTES = 16 MiB`, `MAX_PACKAGE_UPLOAD_BYTES = 6 GiB` sa HTTP `413`), RAM bafer Screen Mirror-a uz TTL izbacivanje i 30-dnevno zadržavanje živih logova.
- **Sanitizacija ulaza i skladišta (`servishub/drivers/cache.py`, `servishub/api/updates.py`):**
  - Validacija `.tar` model pakova (`_validate_model_pack_tar_archive`) odbija path traversal, symlinkove/specijalne čvorove i skripte, zahteva najmanje jedan `.inf` i beleži prisustvo `.cat` kataloga; svi host putevi su izvučeni iz konfiguracije umesto hardkodovanja.
  - Ograničenje `directory`/`iso_path` parametara (`validate_http_storage_directory`, `validate_http_iso_path`) i atomski `.tmp + fsync + os.replace` upisi izveštaja i profila.
- **ZFS i Sistemski Safety Guardrails (`servishub/wis/usb.py`):**
  - Formatiranje preko `WisUsbEngine.format_uefi_usb` fizički je blokirano (`is_safe = False`) za sistemske tačke montiranja (`/`, `/boot`, `/boot/efi`, `/home`, `/var`, itd.), aktivne ZFS diskove, ne-USB i ne-removable diskove, uz globalni `WIS_BURN_LOCK` i obavezno `confirm_wipe=True`.

### 3.5. `Tools/AutopilotAuditor/` (`v3.1.0` Forenzički Alat)
- Pregled lokalnog registra, UEFI/SMBIOS tabela i Microsoft ZTD oblaka obavlja se bez izmene identiteta hardvera, uz strogu proveru TLS sertifikata (`sslPolicyErrors == SslPolicyErrors.None` u `CloudZtdInspector.cs`).
- Privremeno uklanjanje `SkipNetworkSetup` / `DisableOOBEConnection` vrednosti tokom aktivnog ZTD upita mora biti uokvireno `try/finally` blokom koji obnavlja originalne vrednosti ukoliko Windows slika još nije u stanju `IMAGE_STATE_COMPLETE`.
- Potvrđeni forenzički nalaz (`LOCKED_TENANT`, `HIDDEN_LOCK`, `RELEASED_LOCAL_TRACES`, `CONFIRMED_CLEAN`) zaštićen je od prepisivanja kasnijim nepotvrđenim/offline očitavanjem (`SaveCachedReport`).

---

## 4. Upravljanje Tajnama, Git Higijena i Rotacija Ključeva

1. **Git Ignorisanje Tajni ([`.gitignore`](.gitignore)):**
   - U repozitorijumu je dozvoljen isključivo šablon [`Wifi-Servis.example.ini`](Wifi-Servis.example.ini) / [`ISO/Wifi-Servis.example.ini`](ISO/Wifi-Servis.example.ini).
   - Datoteke `*Wifi-Servis.ini`, `*WLAN*.xml`, `*WiFi*.xml`, `*.pem`, `*.key`, `*.pfx`, `.env`, `.env.*`, `/ISO/sources/`, `/ISO/wis-payload.zip`, `/ISO/payload_meta.json`, `/ServiceHub/storage/` i `/.workspace/` trajno su isključene iz praćenja.
2. **Sanitizacija Logova i Telemetrije:**
   - `Write-ServisLog` (`WIS.Common.psm1` / `WIS.Core.psm1`), `Write-Log` (`SetupComplete.ps1`) i `ServiceHub` automatski maskiraju lozinke, Wi-Fi ključeve (`<keyMaterial>`) i `net use` / SMB akreditive pre pisanja na disk ili slanja preko HTTP-a.
3. **Licencna Usklađenost i Komponente Trećih Strana:**
   - Javni repozitorijum koristi isključivo ugrađeni BIOS OEM ključ (`OA3xOriginalProductKey`), legitimnu Office `OSPP.VBS /act` aktivaciju i opcioni eksterni workshop hook (`Programi\Licensing\Invoke-CustomLicensing.ps1` / `CustomLicense.cmd`), dok je jedina prateća biblioteka treće strane (`zxing.dll` Apache-2.0; `LibreHardwareMonitorLib` i `HidSharp` su uklonjeni u v3.1.0) dokumentovana u [`THIRD-PARTY-NOTICES.md`](THIRD-PARTY-NOTICES.md).
4. **Procedura Rotacije Ključeva u Slučaju Kompromitacije:**
   - **Kompromitacija servisnog Wi-Fi ili NAS naloga:** Odmah promeniti Wi-Fi PSK i TrueNAS SMB lozinku na infrastrukturi, ažurirati lokalni `Wifi-Servis.ini` na servisnim USB/PXE medijumima, i opozvati stare SMB sesije.
   - **Kompromitacija `wis-release-private.pem`:** Generisati novi RSA-2048 par ključeva van repozitorijuma (`chmod 600`), ažurirati javni ključ u `Launcher/trusted_public_key.xml`, `Launcher/PayloadManager.cs` i `ServiceHub/trusted_public_key.xml`, rekompajlirati `WIS.Launcher.exe` i `ISO`, pa ponovo potpisati i objaviti `wis-payload.zip` preko `Release-WIS.py publish`.

---

## 5. Interna Bezbednosna Revizija i Prijava Propusta

Kompletan izveštaj interne bezbednosne samoprocene sa verifikovanim kontrolama i saniranim nalazima (`SEC-WIS-001` .. `SEC-WIS-007` — svi označeni kao `SANIRANO / REŠENO` u `WIS-TASK-249`, `2026-11-18`) nalazi se u:
- [`docs/security/NEZAVISNA_BEZBEDNOSNA_REVIZIJA.md`](docs/security/NEZAVISNA_BEZBEDNOSNA_REVIZIJA.md)
- [`docs/decisions/ADR-0238-independent-security-audit-and-unified-security-policy.md`](docs/decisions/ADR-0238-independent-security-audit-and-unified-security-policy.md)

### Prijava Bezbednosnih Propusta
Ukoliko uočite bezbednosni propust u kodu, konfiguraciji ili infrastrukturi, **nemojte ga objavljivati kroz javni GitHub Issue ili Pull Request**. Kontaktirajte održavaoce repozitorijuma direktno i privatno sa opisom komponente, koracima za reprodukciju i predloženim ublažavanjem radi koordinisanog otklanjanja i potpisivanja zakrpljenog izdanja.
