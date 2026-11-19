# ADR-0253: WIS v3.1.0 / ServiceHub v3.4.0 — Bezbednosno Očvršćavanje, Ukidanje WinRing0 i Konsolidacija Audita (`WIS-TASK-253`)

- **Status:** Prihvaćeno i implementirano (`2026-11-19`)
- **Zadatak:** `WIS-TASK-253`
- **Ciljna verzija:** `WIS v3.1.0` (`ISO`, `Launcher`, `UI`, `AutopilotAuditor: 3.1.0`) / `ServiceHub v3.4.0`

---

## 1. Kontekst

Nakon objave `WIS v3.0.0` i terenskog preseka `177` instalacija (`FIELD-2026-11.md`), sprovedena je konsolidovana analiza bezbednosti, arhitekture i pouzdanosti celog monorepo-a koja je istakla sledeće zahteve:
1. Potpuno uklanjanje biblioteke `LibreHardwareMonitorLib.dll` (koja nosi ugrađeni `WinRing0` kernel drajver ranjiv na **CVE-2020-14979** / BYOVD eskalaciju privilegija) i pratećeg `HidSharp.dll` iz Git repozitorijuma, build pipeline-a, OTA manifest ugovora i klijentskih mašina.
2. Očuvanje čistoće javnog GitHub repozitorijuma i čitave Git istorije bez bilo kakvih neovlašćenih alata za aktivaciju ili tragova istih (uz zadržavanje legitimnog mehanizma: BIOS `OA3xOriginalProductKey`, `OSPP.VBS /act` i eksternog radioničkog hook-a van Git-a).
3. Zaštita `ServiceHub` API-ja na LAN mreži (ukidanje wildcard CORS-a `*`, fail-closed autentifikacija za administrativne rute i opcioni upload token za klijente, stroga validacija `.tar` paketa drajvera, ograničenja SSE pretplatnika i memorije, atomični upisi na disk).
4. Zatvaranje operativnih trka i slepih tačaka otkrivenih u produkciji (`ServisWuActive` split-brain ključ, `Phase4Active` kolizija sa pozadinskim WU, nejednak termalni prag `95°C`/`98°C`, nedostatak post-verifikacije vraćanja OS bezbednosnih polisa u Fazi 6, neraslojivo P5 vreme čekanja operatera, nedostatak Dell OEM senzora).
5. Ispravka uočenih defekata (`NameError` u `ISO/USB Tool/core/system_inspector.py` bez `import subprocess`, uklanjanje hardcoded `/home/gru/Storage` putanja u `ServiceHub/servishub/api/updates.py`, lažni `100%` hit ratio kada nema saobraćaja, podrazumevani `Wifi-Servis.example.ini` sa ne-generičkim `wis_service_ro` nalogom).

---

## 2. Donete Odluke

### 2.1. Potpuna eliminacija `WinRing0` i Anti-BYOVD odbrana (`WIS v3.1.0`)
- Uklonjeni `UI/LibreHardwareMonitorLib.dll` i `UI/HidSharp.dll` (`git rm`); ažurirani `UI/WIS.UI.csproj`, `UI/Build-UI.py`, `ISO/Skripte/Build-WIS.ps1`, `ISO/Skripte/Release-WIS.py`, `ISO/Skripte/Build-PreviewMedia.py` i `ISO/Skripte/deployment_manifest.json`.
- `ServiceHub/servishub/releases.py` uvodi `REQUIRED_310_COMPONENTS` (bez tih DLL-ova) i eksplicitno odbija bilo koji `3.1.0+` paket koji sadrži `LibreHardwareMonitorLib.dll`, `HidSharp.dll` ili `WinRing0`.
- `Launcher/PayloadManager.cs` postavlja `MinAcceptedPayloadVersion = "3.1.0"` (anti-downgrade zaštita), uvodi `MandatoryRelease310Files`, odbija `WinRing0`/`LibreHardwareMonitorLib.dll`/`HidSharp.dll` u `3.1.0+`, proverava `.sys` drajvere u `VerifyDirectoryMatchesManifest`/`RemoveUnlistedPayloadExecutables` i automatski uklanja zaostale artefakte pri nadogradnji.
- `UI/ThermalSensorEngine.cs` uklanja `Assembly.LoadFrom` u `InitializeLhm()` (Anti-BYOVD) i proširuje nativno očitavanje na Dell `root\dcim\sysman:DCIM_NumericSensor (SensorType = 2)`.
- `UI/Skripte/Modules/WIS.Cleanup.psm1` dodaje `Remove-LegacyWinRing0Driver` za zaustavljanje i brisanje eventualnog servisa `WinRing0_1_2_0` sa sistema i brisanje zaostalih DLL/`.sys` fajlova.

### 2.2. Zaštita bezbednosnih polisa, `Phase4Active`/`ServisWuActive` i termalna unifikacija
- **Jedinstveni `ServisWuActive` i `Phase4Active`:** Svi WU radnici u `WIS.Update.psm1` i provere u `WIS.Apps.psm1` unifikovani su na `HKLM:\SOFTWARE\WIS`; `Phase4-Apps.ps1` drži `Phase4Active = 1` u `try/finally`, a pozadinski WU radnik čeka slobodnu Fazu 4 pre instalacije paketa.
- **Bezbednost `.tar` drajvera:** `Install-DriverTarArchive` odbija arhive sa bilo kojom skriptom (`.ps1`, `.bat`, `.cmd`, `.vbs`, `.js`, itd.) pre `pnputil.exe` i evidentira prisustvo `.cat` potpisa.
- **Post-verifikacija hardeninga i Sentinel:** `WIS.Core.psm1` registruje `WIS_SecurityRecovery_Sentinel` (`OnStartup`); `WIS.Cleanup.psm1` ga uklanja u Fazi 6 i nakon `Restore-WisSecurityHardening` čitanjem registra i Defender polisa potvrđuje uspešan povratak (`$Global:WisSecurityHardeningVerified`), dok `Phase6-Finalize.ps1` u suprotnom blokira predaju (`security_hardening_verified = false`, `handover_ready = false`, `exit 2`).
- **Termalni prag:** Unifikovan u `UI/MainWindow.xaml.cs`, `UI/Skripte/Modules/WIS.Hardware.psm1` i `UI/Skripte/Phases/Phase5-Stress.ps1` na **`>= 97°C` momentalno ili `>= 95°C` u 2 uzastopna uzorka**.

### 2.3. `ServiceHub v3.4.0` i analitika performansi
- `ServiceHub` uvodi fail-closed autentifikaciju (`SERVIS_HUB_ADMIN_TOKEN`, `allow_unauthenticated_lan` podrazumevano `False`, HTTP `503`), `SERVIS_HUB_CLIENT_TOKEN` (`X-ServisHub-Client-Token` za upload rute i klijentski `Invoke-ServisHubBinaryUpload`), CORS allowlist (`SERVIS_HUB_CORS_ORIGINS`), `429` SSE limite, `_validate_model_pack_tar_archive()`, `bsdtar` cross-model `.inf` HWID indeksiranje (`drivers/cache.py`), kanonsku deduplikaciju uređaja po serijskom broju / SMBIOS UUID-u (`telemetry.py`), RAM TTL za Screen Mirror, 30-dnevnu rotaciju logova, atomične JSON/MD upise (`reports.py`, `model_profiles.py`), te kohortnu (`v3_modern` vs `legacy`) i P5 (`operator_wait_sec` / `active_hwtest_sec` + dvotonski audio signal `880Hz`->`1175Hz`) i P4 (`phase4_steps_sec`) analitiku.

### 2.4. Poslovna odluka za lokalni nalog `Korisnik` i čista istorija repozitorijuma
- Podrazumevani lokalni nalog `Korisnik` u `ISO/autounattend.xml` namerno ostaje bez lozinke kao deo radioničkog modela „ključ u ruke“ (kako bi kupac mogao odmah da preuzme računar bez poznavanja privremene šifre), što je eksplicitno dokumentovano u [`SECURITY.md`](../../SECURITY.md) uz preporuku kupcu da postavi PIN/lozinku.
- Za javnu GitHub distribuciju `v3.1.0`, istorija grane `main` se resetuje na čist polazni commit bez tragova ranijih nekompatibilnih referenci na aktivacione alate, bez realnih serijskih brojeva i hostname-ova u dokumentaciji i sa pinovanim SHA-256 GitHub Actions workflow-ovima.

---

## 3. Posledice
- Potpuno uklonjena izloženost `WinRing0` BYOVD ranjivosti i ranjivim DLL-ovima u paketima i Git stablu.
- Sve administrativne operacije nad `ServiceHub`-om su fail-closed zaključane van eksplicitnog dev/LAN režima.
- Puna sledljivost Faze 4 (`phase4_steps_sec`) i Faze 5 (`operator_wait_sec`, `active_hwtest_sec`) za buduću optimizaciju radioničkog P50/P90 vremena.
