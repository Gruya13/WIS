---
title: "ADR-0137: Potpuna eliminacija GWADT tranzicionih artefakata, optimizacija payload paketa na 4.13 MB i prelazak na 100% čist WIS sistem (v2.16.1)"
type: decision
created: 2026-10-12
status: accepted
tags:
  - adr
  - architecture
  - cleanup
  - wis
  - payload
  - security
---

# ADR-0137: Potpuna eliminacija GWADT tranzicionih artefakata, optimizacija payload paketa na 4.13 MB i prelazak na 100% čist WIS sistem (v2.16.1)

## 1. Kontekst i Problem

U sklopu arhitektonske odluke [[decisions/ADR-0124-rebranding-to-wis]] uveden je sveobuhvatni proces rebrendiranja operativnog servisnog sistema iz radnog naziva **GWADT** u zvanični naziv **WIS (Tehno kutak Windows Installation Assistant)**. Tokom tranzicionog perioda (v2.12.0 – v2.16.0), sistem je namerno zadržao dvostruku kompatibilnost:
1. Koegzistirali su C# izvršni fajlovi oba brenda (`WIS.UI.exe` / `GWADT.UI.exe` i `WIS.Launcher.exe` / `GWADT.Launcher.exe`).
2. Skladišteni su i održavani paralelni C# projekti (`Skripte/WIS.*` i `Skripte/GWADT.*`).
3. ServisHub i distributivni kanali su generisali i servirali paralelne arhive (`wis-payload.zip` i `gwadt-payload.zip`).

Nakon uvođenja očvrsnute verzije v2.16.0 ([[decisions/ADR-0136-comprehensive-review-remediation-and-signed-payload]]), sprovedena su temeljna testiranja na radioničkim klijentima koja su potvrdila besprekornu pouzdanost i stabilnost nativnih WIS komponenti ("WIS radi odlično"). Održavanje paralelnih GWADT artefakata postalo je čist tehnički dug:
- Distributivni payload paket bio je veštački naduvan na skoro 7 MB (6,993,051 B) zbog prisustva duplih PE32 izvršnih fajlova i test skripti.
- Manifest komponenti (`deployment_manifest.json`) morao je da mapira 37 komponenti na 74 fizičke lokacije umesto čistog kanonskog skupa.
- Postojao je rizik slučajnog pokretanja ili pozivanja starih GWADT putanja u novim skriptama i logovima.

Doneta je operativna odluka da se tranzicioni period zvanično i trajno zaključi potpunom eliminacijom svih GWADT artefakata i prelaskom na **100% čisti WIS ekosistem (v2.16.1 — Pure WIS Clean Edition)**.

---

## 2. Arhitektonska Odluka i Tehnička Implementacija

U okviru radnog naloga **WIS-TASK-137** realizovana su četiri specijalizovana paketa promena:

### 2.1. Trajna Eliminacija GWADT C# Projekata i Binarnih Artefakata
Pod vođstvom agenta `wis_module_architect`:
- **Uklonjeni izvorni C# projekti:** Trajno su obrisani direktorijumi `Skripte/GWADT.Launcher/`, `Skripte/GWADT.UI/`, kao i njihove kopije u `sources/$OEM$/$1/setup/Skripte/GWADT.UI/` i skladišnom mirror-u `/home/gru/Storage/`.
- **Uklonjeni izvršni binarci i test skripte:** Uklonjeni su svi `GWADT.UI.exe` i `GWADT.Launcher.exe` fajlovi iz korena repozitorijuma, `sources/$OEM$/$1/setup/`, `Skripte/WIS.Launcher/` i `/home/gru/Storage/`. Takođe su uklonjeni `Test-GWADTUI-IPC.ps1` i `Test-GWADTModules.py`.
- **Ažurirane build skripte:** U `Skripte/WIS.Launcher/Build.bat` i `Skripte/WIS.UI/Build.bat` izlazni log folder je preusmeren sa `Logs\GWADT` na `Logs\WIS`, a naslovi konzolnih prozora su unificirani pod WIS brendom.

### 2.2. Ažuriranje Kanonskog Manifesta na 35 Čistih WIS Komponenti
- `Skripte/deployment_manifest.json` je ažuriran na verziju `2.16.1`.
- Uklonjene su komponente `bin_gwadt_ui_exe` i `bin_gwadt_launcher_exe`.
- Manifest sada sadrži tačno **35 autoritativnih WIS komponenti** (svih 35 izvornih fajlova fizički postoji na disku, validirani su kroz `manifest_loader.py` i ne sadrže nijednu GWADT referencu).

### 2.3. Penzionisanje `gwadt-payload.zip` i Očvršćavanje ServisHub PayloadManagera
Pod vođstvom agenta `wis_security_hardening`:
- **Brisanje zaostalih arhiva:** Obrisane su sve 4 instance `gwadt-payload.zip` sa lokalnog diska i storage lokacija.
- **Kanonska putanja:** U `Skripte/ServisHub/hub_server.py` `PayloadManager` kanonski gradi i servira isključivo `storage/cache/wis-payload.zip`.
- **Sigurnosni filteri:** U metodu `collect_payload_files` uveden je striktni bezbednosni filter koji automatski odbacuje bilo koje fajlove sa `gwadt` u imenu ili relativnoj putanji, tajne (`Wifi-Servis.ini`) i RSA privatne ključeve (`.pem`, `.key`).
- **Automatsko RSA-2048 potpisivanje:** Po završetku kreiranja ZIP arhive, server automatski poziva `Skripte/Sign-PayloadManifest.py`, obezbeđujući atomarnu sinhronizaciju manifesta i potpisa.
- **Ruta za preuzimanje:** Endpoint `/api/payload/download` eksplicitno postavlja HTTP zaglavlje `Content-Disposition: attachment; filename="wis-payload.zip"`.

### 2.4. Optimizacija Arhive i Kriptografski Potpis Manifesta
- **Dramatična optimizacija veličine:** Veličina payload paketa smanjena je sa 6,993,051 B na **4,128,294 B** (ušteda od **2,864,757 B** odnosno **39.5%** manje mrežnog saobraćaja pri svakom klijentskom povlačenju paketa).
- **Sadržaj arhive:** Arhiva sadrži tačno 125 čistih kanonskih WIS datoteka (izvršni binarci `WIS.Launcher.exe` 82,432 B, `WIS.UI.exe` 406,528 B, zavisnosti `HidSharp.dll`, `LibreHardwareMonitorLib.dll`, faze 1–6, svi WIS moduli, OOBE skripte, `autounattend.xml` i `version.json`).
- **RSA-2048 PKCS#1 v1.5 potpis:**
  * Manifest `storage/cache/payload_meta.json` je digitalno potpisan važećim asimetričnim privatnim ključem.
  * SHA-256 kontrolna suma paketa `wis-payload.zip`: `a6f6311736129eb59de88125e7da6de92fd6995d07ba55dbc0b320a0adc87450`.
  * Verifikacija preko `Sign-PayloadManifest.py --verify-only` potvrđuje 100% validnost potpisa (Exit Code 0).

### 2.5. Sveobuhvatna E2E QA Verifikacija Striktno Bez Mock-ova
Pod vođstvom agenta `wis_qa_debugger`:
- **Statička analiza PowerShell koda:** `Verify-PowerShellSyntax.py` potvrdio 20/20 skripti i modula ispravnim (0 sintaksnih grešaka).
- **Statička analiza C# koda:** `Verify-CSharpSyntax.py` potvrdio 21/21 datoteka u `WIS.UI` i `WIS.Launcher` (100% čist C# 5.0 kod).
- **Integritet stabla:** Potvrđeno 0 zaostalih GWADT binarnih ili izvršnih datoteka u aktivnom stablu repozitorijuma.
- **Automatizovani E2E QA testovi (`Test-Task136-Remediation-QA.py`):**
  * Blok 5 prilagođen na tačno 35 komponenti bez GWADT-a.
  * Blok 8 prilagođen na proveru isključivo čistih PE32 binaraca (`WIS.UI.exe` i `WIS.Launcher.exe`) sa 100% SHA-256 binarnim paritetom i 0 zaostalih GWADT binaraca.
  * Rezultat izvršenja: **41/41 položenih testova (100.0% PASS, 0 FAIL, vreme 2.64 s, Exit Code 0)**.

---

## 3. Status Odluke

**Prihvaćeno (Accepted)** — Datum: 2026-10-12.
Implementirano, verifikovano i potpisano u verziji **WIS v2.16.1 (Pure WIS Clean Edition)**.

---

## 4. Posledice

### Pozitivne posledice:
- **Radikalna ušteda protoka i brži deployment:** Smanjenje veličine payload paketa za ~40% (sa 6.99 MB na 4.13 MB) ubrzava preuzimanje paketa preko gigabitne i bežične radioničke mreže, posebno pri istovremenom radu na više računara.
- **Nula tehničkog duga:** Celokupan repozitorijum, skripte, manifesti i C# binarci sada su ujednačeni pod jedinstvenim WIS imenom. Uklonjeni su svi redundantni C# projekti koji su morali paralelno da se kompajliraju i testiraju.
- **Kriptografska garancija integriteta:** Payload je zaštićen važećim asimetričnim RSA-2048 PKCS#1 v1.5 potpisom, sprečavajući distribuciju neautorizovanih izmena.
- **Jednostavniji manifest za distribuciju:** Kanonski spisak od 35 komponenti eliminiše kompleksnost i rizik neusklađenosti između razvojnog i produkcionog okruženja.

### Operativne posledice:
- Zvanično je okončana kompatibilnost sa starim `GWADT.Launcher.exe` i `gwadt-payload.zip` nazivima. Sve postojeće i nove instalacije na klijentskim mašinama oslanjaju se isključivo na `WIS.Launcher.exe` i `wis-payload.zip`.
- ServisHub više ne podržava zahteve za `gwadt-payload.zip`, već klijente transparentno usmerava na `wis-payload.zip`.
