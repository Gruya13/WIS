---
title: "ADR-0136: Arhitektonsko saniranje 19 nalaza review-a, RSA-2048 potpisani payload manifest, transakcioni update sa rollback-om, 6-fazni Hub ugovor i eliminacija bezbednosnih rizika (v2.16.0)"
---

# ADR-0136: Arhitektonsko saniranje 19 nalaza review-a, RSA-2048 potpisani payload manifest, transakcioni update sa rollback-om, 6-fazni Hub ugovor i eliminacija bezbednosnih rizika (v2.16.0)

## 1. Kontekst i Problem

Detaljnim pregledom celokupnog WIS/GWADT ekosistema od 12. oktobra 2026. godine (`REVIEW-2026-10-12.md`), identifikovano je ukupno **19 nalaza (10 prioriteta P1, 9 prioriteta P2)** i **2 uslovna problema (U1 i U2)**. Ovi nalazi su ukazivali na suptilne, ali kritične ranjivosti u životnom ciklusu instalacije, integritetu distribucije, pouzdanosti pozadinskih servisa i tačnosti telemetrije:

1. **Prekid izvršenja u OOBE/Setup fazi (Nalaz 1 - P1):** `SetupComplete.cmd` je sadržao predugačke linije sa `^` nastavljanjem i ugnježdenim inline PowerShell blokovima, što je u `cmd.exe` okruženju dovodilo do tihog prekida pre nego što se prenesu greške i preuzme priprema.
2. **Neovlašćeni pristup i DNS Rebinding na USB Tool API-ju (Nalaz 2 - P1):** Podrazumevani mrežni bind na svim interfejsima bez obavezne sesije i CSRF validacije omogućavao je manipulaciju ciljnim diskovima.
3. **Integritet i poreklo klijentskog payload paketa (Nalazi 3, 4, 5 - P1):** Odsustvo asimetričnog kriptografskog potpisa omogućavalo je prihvatanje nepotvrđenih izmena; parametar `force=true` je zaobilazio SHA-256 heš proveru; ekstrakcija ZIP arhive nije imala zaštitu od Zip-Slip putanja niti mehanizam atomarnog rollback-a u slučaju prekida raspakivanja.
4. **Gubitak prethodno ispravnih slika pri neuspešnom WIM/USB sync-u (Nalazi 6, 7 - P1, Nalazi 11, 12 - P2):** Prebrisavanje ciljnih slika pre provere prostora/FAT32 limita (4 GiB), deljenje privremenih radnih fajlova između paralelnih build pokušaja i nedostatak jedinstvenog autoritativnog manifesta komponenti.
5. **Lažni prolaz kritičnih stres testova (Nalaz 8 - P1, Nalazi 15, 16, 19 - P2):** Prekomerna temperatura procesora (`THERMAL_CRITICAL`) i prekid testa nisu blokirali automatsku predaju računara; nedostatak senzora je interpretiran kao `0°C` i položen test; računat je nepostojeći throttling procenat direktno iz temperature; gašenje `CancellationTokenSource` je lažno označavalo normalan istek testa kao korisnički cancel.
6. **Deadlock i XSS u ServisHub nadzoru (Nalazi 9, 10 - P1, Nalaz 13 - P2):** Simultani prijem telemetrije i SSE strimovanje blokirali su server zbog nekonzistentnog zaključavanja; nefiltrirani unosi u Web Cockpitu omogućavali su ubacivanje zlonamernog koda; status `DONE` se preuranjeno dodeljivao mašinama koje su još bile u toku stres testa (Faza 5) ili finalizacije (Faza 6).
7. **Praznine u klijentskom UI i servisnom toku (Nalazi 14, 18 - P2, U1, U2):** Dugme za odlaganje restarta nije odlagalo stvarni Windows restart; Fast-Pass mreža je preskakala proveru prilagođenog ServisHub URL-a; privremeni Wi-Fi XML profili sa specijalnim znakovima (`&`, `<`) izazivali su neuspeh netsh importa, a zaostali plaintext profili nisu bili 100% počišćeni; `WIS.ScreenCapture.ps1` nije imao kontrolu jedinstvene instance niti gašenje pri izlasku roditeljskog procesa.

---

## 2. Arhitektonska Odluka i Tehnička Implementacija

Za potpuno saniranje svih 21 stavki iz `PLAN-POPRAVKI-2026-10-12.md` implementirana je robusna arhitektura **WIS v2.16.0** kroz 4 talasa i 3 završna paketa:

### 2.1. Blok A1: Robusni SetupComplete Wrapper, XML Escaping i Anti-Leak Garancija (Nalazi 1, 17, U1)
- **33-linijski čisti `SetupComplete.cmd` wrapper:** Uklonjeni su svi kompleksni inline PowerShell blokovi i prelomljene linije sa `^`. CMD skripta poziva `SetupComplete.ps1` putem `-NoProfile -ExecutionPolicy Bypass -File`, beleži tačan `%ERRORLEVEL%` i tek na osnovu njega odlučuje o statusu uspeha.
- **Autoritativni `SetupComplete.ps1`:** Celokupna logika uvoza mrežnih postavki, raspakivanja i postavljanja okruženja smeštena je u strukturirani PowerShell skript sa `try/catch/finally` blokovima i detaljnim logovanjem u `servis_deploy.log`.
- **Siguran XML Escaping Wi-Fi profila (`PreOOBE-Network.ps1` & `WIS.Network.psm1`):** Wi-Fi SSID i lozinka se sanitizuju korišćenjem `[System.Security.SecurityElement]::Escape()`, sprečavajući pucanje `netsh wlan add profile` pri postojanju specijalnih XML karaktera (`&`, `<`, `>`, `"`, `'`). Privremeni XML fajlovi profila se garantovano brišu u `finally` bloku.
- **Prošireno Anti-Leak čišćenje tajni (`WIS.Cleanup.psm1`):** Čišćenje identifikuje i uklanja sve servisne WLAN profile i INI/XML datoteke isključivo sa klijentskih fiksnih diskova, uz striktno preskakanje servisnih USB instalacionih medijuma. Status uspeha se izdaje isključivo nakon verifikacije odsustva svih tajni.

### 2.2. Blok A2 & C1: Autoritativni Ugovor Stres Testa i Termalne Dijagnostike (Nalazi 8, 15, 16, 19)
- **Autoritativni ugovor `stress-test-result.json`:** Faza 5 (`Phase5-Stress.ps1`) i radnik generišu jedinstven JSON fajl sa poljima: `status`, `reason`, `emergency_abort`, `sensor_available`, `sensor_source`, `duration_sec`, `max_cpu_temp_c` i `timestamp`. Trajanje se meri preciznim .NET `Stopwatch` tajmerom.
- **Status `THERMAL_CRITICAL` i blokada predaje:** U slučaju prekoračenja kritične temperature, radnik postavlja status `THERMAL_CRITICAL`, vraća izlazni kod `2`, upisuje `emergency_abort = true` i postavlja globalni status raspoređivanja na `NEEDS_ATTENTION`. Faza 6 i Master UI bezuslovno blokiraju automatsku predaju računara (`handover_ready = false`).
- **Razdvajanje dostupnosti senzora od nulte vrednosti (Nalaz 15):** Ukoliko senzori temperature nisu dostupni, sistem ne pretpostavlja `0°C` niti dodeljuje `PASS`, već eksplicitno označava `sensor_available = false`, status `UNKNOWN` i zahteva pažnju servisera.
- **Uklanjanje fiktivnog throttling-a (Nalaz 16):** U `ThermalSensorEngine.cs` uklonjena je formula koja je linearno izmišljala procenat termalnog obaranja takta iz same temperature; throttling se meri isključivo stvarnim padom radnog takta procesorskih jezgara.
- **Ispravka detekcije korisničkog prekida (Nalaz 19):** U `MainWindow.xaml.cs` stanje `isManualCancel` se evaluira pre poziva `_stressCts.Cancel()`, čime je sprečeno da normalan istek trajanja stres testa (30s) bude lažno klasifikovan kao ručni prekid.

### 2.3. Blok A3: Sigurnosni Bedem USB Tool REST API-ja (Nalaz 2)
- **Lokalni Bind i Zaštita od DNS Rebinding-a:** HTTP server u `USB Tool/app.py` se podrazumevano vezuje na `127.0.0.1`. Server proverava `Host` i `Origin` zaglavlja i odbija zahteve koji ne potiču sa odobrene lokalne instance.
- **Autentifikacija sesije i CSRF tokeni:** Uveden je `HttpOnly; SameSite=Strict` kolačić za sesiju i obavezno zaglavlje `X-USB-Tool-Token` za sve mutirajuće POST zahteve (`/api/upgrade-usb`, `/api/repair-stick`, `/api/unmount`).
- **Autoritativna validacija cilja (`resolve_and_validate_usb_target`):** Server odbija zahteve koji ciljaju sistemske i root diskove (`/`, `/boot`, `/home`, sistemski Windows disk), symlink-ove i NAS uređaje.
- **Busy Lock i Strukturirani Audit Log:** Svaki ciljni uređaj dobija ekskluzivni lock tokom operacije (vraća HTTP 409 Conflict pri konkurentnim zahtevima), a svaka akcija se beleži u strukturirani audit log bez skladištenja poverljivih podataka.

### 2.4. Blok A4 & B1: RSA-2048 Potpisani Payload, Zip-Slip Zaštita i Transakcioni Rollback (Nalazi 3, 4, 5)
- **Asimetrični RSA-2048 PKCS#1 v1.5 potpis:** Implementiran alat `Skripte/Sign-PayloadManifest.py` koji potpisuje `payload_meta.json`. Privatni ključ se čuva u restriktivnom direktorijumu `~/.config/wis-signing/` (dozvole 0600), dok je javni ključ ugrađen u klijentske binarce.
- **Striktna verifikacija čak i uz `force=true` (Nalaz 4):** U `SplashForm.cs` parametar `force=true` zaobilazi proveru ažurnosti verzije, ali i dalje obavezno zahteva validan SHA-256 heš i validan RSA potpis.
- **Zip-Slip zaštita i staging ekstrakcija (Nalaz 5):** U `PayloadManager.cs` uvedena je kanonska normalizacija putanja tokom raspakivanja arhive koja blokira iskakanje van ciljnog foldera. Ekstrakcija se vrši u privremeni direktorijum `.staging_extract\`.
- **Transakcioni zapis i automatski rollback:** Stanje ažuriranja se prati u `.update_tx.json`. Pre zamene fajlova pravi se bekap u `.backup_prev\`. U slučaju greške ili prekida tokom instalacije novog payload-a, `PayloadManager` automatski vraća sistem u stabilno prethodno stanje.

### 2.5. Blok B2: Transakcioni WIM Transfer, Izolovani Build i Kanonski Manifest (Nalazi 6, 7, 11, 12)
- **Pre-flight inspekcija fajlsistema i prostora (`usb_sync.py`):** Pre bilo kakvog brisanja postojećih datoteka na USB-u, funkcija `validate_target_space_and_fs` proverava FAT32 limit (odbija datoteke veće od 4 GiB) i dostupan slobodan prostor na medijumu.
- **Atomarna zamena slike:** Novi WIM/ESD fajl se prenosi pod privremenim imenom `.tmp.<attempt_id>`. Tek nakon uspešnog heširanja i provere integriteta vrši se preimenovanje, dok se prethodna slika čuva kao `.bak` do potvrde.
- **Izolacija online build radnog prostora (Nalaz 7):** U `Update-WimImage-Linux.py` svaki build pokušaj generiše unikatni radni direktorijum `work_dir`, čime je eliminisano međusobno prebrisavanje privremenih `install_building.wim` datoteka pri paralelnim operacijama.
- **Kanonski `deployment_manifest.json` (Nalaz 11):** Kreiran centralni manifest sa tačno 37 validiranih komponenti mapiranih na 74 ciljne lokacije (repozitorijum, OEM stabla, Storage mirror), uz potpuno uklanjanje zastarelih referenci.

### 2.6. Blok B3: ServisHub Concurrency, XSS Zaštita i 6-Fazni Model (Nalazi 9, 10, 13)
- **Eliminacija Deadlock-a i ograničeni SSE red (Nalaz 9):** Usklađen je redosled zaključavanja resursa u višenitnom Python serveru (`hub_server.py`) i postavljen limit `MAX_SSE_QUEUE_SIZE = 32`. 50 paralelnih POST zahteva i SSE strim se izvršavaju bez zastoja za manje od 0.15s.
- **XSS sanitizacija i uklanjanje inline `onclick` handlera (Nalaz 10):** Svi podaci koji se renderuju u Web Cockpitu prolaze kroz `escapeHtml()`. Dinamičko vezivanje događaja vrši se preko `data-action` atributa i delegiranih event listenera.
- **6-fazni model i pravila statusa (Nalaz 13):** Faza 5 (stres test) i tekuća Faza 6 vraćaju status `ACTIVE`. Status `DONE` ('Završeno') se dodeljuje isključivo na osnovu eksplicitnog `COMPLETED` događaja uz uslov `handover_ready = true`. `THERMAL_CRITICAL` trajno zaključava status `NEEDS_ATTENTION`.

### 2.7. Blok C2: Odlaganje Restarta, Fast-Pass Provera i Screen Worker Zaštita (Nalazi 14, 18, U2)
- **Akumulativno odlaganje restarta (Nalaz 14):** U `MainWindow.xaml.cs` klik na dugme za odlaganje restarta poziva `shutdown.exe /a`, dodaje +10 minuta na `_restartDueAtUtc` i ažurira `deploy-state.json`.
- **Fast-Pass Hub validacija (Nalaz 18):** U `Phase1-Network.ps1` Fast-Pass grana (kada je internet već aktivan) poziva funkciju `Initialize-ServisHubFastPass`, proverava i loguje dostupnost zadatog ServisHub URL-a pre završetka faze.
- **Sigurni `WIS.ScreenCapture.ps1` radnik (Uslov U2):** Implementirana robusna zaštita pozicionih argumenata, imenovana `Mutex` kontrola jedne instance i automatsko gašenje radnika ukoliko roditeljski proces (`ParentPid`) prestane sa radom.

### 2.8. Blok D1 & D2: C# 5.0 MSBuild Kompilacija v2.16.0 i 41/41 E2E QA Verifikacija
- **Kompilacija u Wine okruženju:** Kompajlirana sva 4 izvršna C# projekta u verziju **v2.16.0**:
  * `WIS.UI.exe` (406,528 B, SHA-256: `84811a7dfa0b9e9a4284f4626ff43ba8e577d0ebb01cc96d4be69e09a0beb395`)
  * `GWADT.UI.exe` (406,528 B, SHA-256: `d4ab699c5c4b6b5fc2f1189c55779dcb5b3f9a35d580477e99778943de8b2d7f`)
  * `WIS.Launcher.exe` (82,432 B, SHA-256: `a3631eeba64b9bead2367376b5a0642f0a7f31d54db8730bb803b006fe81dbd9`)
  * `GWADT.Launcher.exe` (82,432 B, SHA-256: `27e099ef954ba3b4fac1cdb846755d30b1a90e019d4376a5084cf27e25cccc90`)
- **Regeneracija i potpisivanje Payload paketa:**
  * `wis-payload.zip` / `gwadt-payload.zip`: **6,993,051 bajtova**, SHA-256: `893861fca96f3847d4c86b97bc53a38dc73e486a235347e872382ac4d7e4ca45`.
  * Potpisan manifest `storage/cache/payload_meta.json` važećim RSA-2048 ključem.
- **Završna QA Verifikacija (`Skripte/Test-Task136-Remediation-QA.py`):**
  * Pokrenut kompletan E2E test suite bez ijednog mock-a.
  * **Rezultat: 41/41 položenih provera (100.0% PASS, 0 FAIL, vreme izvršenja 1.89 s).**

---

## 3. Status Odluke

**Prihvaćeno (Accepted)** — Implementirano, kompajlirano, kriptografski potpisano i 100% verifikovano u verziji **WIS v2.16.0**.

---

## 4. Posledice

### Pozitivne posledice:
- **Potpuna eliminacija P1 i P2 ranjivosti:** Svih 19 nalaza i 2 uslovna problema su trajno rešeni u izvornom kodu.
- **Kriptografska sigurnost klijentskog koda:** Nemoguće je plasirati neautorizovani ili oštećeni payload klijentskim mašinama.
- **Otpornost na prekid napajanja i mrežne ispade:** Transakcioni staging sa automatskim rollback-om štiti i WIM slike i klijentske skripte.
- **Garantovana pouzdanost hardverske dijagnostike:** Neispravan ili pregrejan računar nikada više ne može lažno proći kao ispravan i biti predat klijentu.
- **Visoke performanse i stabilnost ServisHub-a:** Uklonjen deadlock, ubrzano opsluživanje paralelnih mašina, potpuno sprečen XSS.

### Negativne i operativne posledice:
- Potrebno je voditi računa o RSA-2048 ključu za potpisivanje u `~/.config/wis-signing/` i sprovoditi periodičnu rotaciju po proceduri iz ops-runbook-a.
- Stres test zahteva striktno prisustvo validnih očitavanja senzora; mašine sa nestandardnim ACPI tablicama zahtevaju ručnu inspekciju servisera (`NEEDS_ATTENTION`).
