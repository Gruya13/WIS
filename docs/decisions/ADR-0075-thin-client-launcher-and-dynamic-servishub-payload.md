---
title: "ADR-0075: Thin-Client C# Launcher Arhitektura, Flash & Release Koncept, Dinamički ServisHub Payload, Zero-Touch UAC i Flotno Izveštavanje"
date: 2026-09-28
status: accepted
tags:
  - architecture
  - thin-client-launcher
  - flash-and-release
  - servishub-payload
  - zero-touch-uac
  - fleet-reporting
  - anti-leak-security
  - oem-staging
deciders:
  - WIS Service Core Team
author: gwadt_docs_sync
---

# ADR-0075: Thin-Client C# Launcher Arhitektura, Flash & Release Koncept, Dinamički ServisHub Payload, Zero-Touch UAC i Flotno Izveštavanje

## 1. Kontekst i Problem

U sklopu modernizacije instalacionog i servisnog toka u radionici WIS Service, uočeno je nekoliko ključnih uskih grla i operativnih rizika u dotadašnjoj arhitekturi automatizacije (GWADT zadaci od v2.0 do v2.4):

### 1.1. Zavisnost od Fizičkog USB Fleš Medijuma ("USB Tied Deployment")
- U prethodnim verzijama, celokupna instalaciona infrastruktura (teške skripte, programski paketi, ikone, prateći alati) zavisila je od prisustva butabilnog USB fleš drajva tokom ranih faza OOBE i FirstLogon toka.
- Ako bi serviser prerano izvadio USB fleš nakon inicijalnog Windows restarta, skripte bi ušle u blokirajuće for-petlje čekanja (npr. 60 pokušaja / 120 sekundi u `Ensure-ProgramiAvailable`), ili bi došlo do prekida rada zbog nedostatka modula.
- Ovo je drastično smanjivalo radionički kapacitet: serviser sa jednim brzim USB flešom morao je da čeka završetak instalacije na jednom računaru umesto da fleš odmah iskoristi za butovanje sledeće mašine na stolu ("Flash & Release").

### 1.2. UAC Blokada na Windows 11 u FirstLogon Fazi
- Windows 11 fabrički primenjuje striktne polise kontrole korisničkog naloga (User Account Control - UAC).
- Prilikom automatskog podizanja instalacionih skripti i alata sa privilegovanim pravima u FirstLogon fazi (`ConsentPromptBehaviorAdmin = 5`, `PromptOnSecureDesktop = 1`), sistem bi često prikazivao UAC modalni prozor koji je zahtevao ručni klik operatera na "Yes".
- Ukoliko operater nije prisutan kraj računara, instalacija bi stajala satima, što narušava osnovni imperativ sistema — **100% Zero-Touch autonomiju**.

### 1.3. Rigidnost Statičkog Koda na Medijumu naspram Dinamičkih Izmena
- Distribucija ispravki bagova i novih modula zahtevala je rekompajliranje celokupnog ISO medijuma i njegovo ponovno narezivanje na USB fleš drajvove ili rsync prenos teške ISO slike (> 7 GB) na PXE skladište.
- Serviseri na radnim stanicama nisu imali mehanizam da momentalno povuku najnovije verzije skripti i interfejsa bez zamene butabilnog medijuma.

### 1.4. Odsustvo Jedinstvene Flotne Identifikacije i Centralizovanih Servisnih Kartona
- Računari u servisu su se identifikovali generičkim imenima (`DESKTOP-XXXXXX`), što je onemogućavalo precizno flotno praćenje statusa pripreme po hardverskom formatu (Laptop, Desktop, All-in-One, Mini-PC) i serijskom broju mašine.
- Završni statusi instalacije ostajali su rasuti po lokalnim logovima na klijentu bez automatske arhive na centralnom serveru radionice (`ServisHub`).

### 1.5. Curenje Servisnih Tajni (Anti-Leak) i Narušavanje Bezbednosti Kupca
- Servisni Wi-Fi profil (`Wifi-Servis.ini` i odgovarajući WLAN profil u sistemu) ostajao je u konfiguraciji računara, čime su servisne pristupne šifre mogle dospeti u posed krajnjeg kupca.
- Takođe, ako se UAC utiša tokom instalacije, a ne vrati na fabričke vrednosti pre isporuke, računar ostaje bezbednosno kompromitovan.

---

## 2. Razmatrane Alternative

| Kriterijum | Opcija A: Zadržavanje USB Zavisnosti uz Poboljšane Skripte | Opcija B: Kompletno Prebacivanje Celog ISO Sadržaja na Klijentski SSD ($OEM$) | Opcija C: Hibridni Thin-Client C# Launcher + $OEM$ Staging + Dinamički ServisHub Payload [PRIHVAĆENO] |
| :--- | :--- | :--- | :--- |
| **Vreme Oslobađanja Fleša** | Fleš mora ostati u računaru do Faze 4/5 | Fleš se oslobađa na restartu, ali ISO raste za više gigabajta | **Fleš se oslobađa odmah na prvom restartu (WinPE $OEM$ prebaci ~36 KB Launcher i SSD keš)** |
| **Ažurnost Koda** | Statičan kôd, vezan za verziju fleša | Statičan kôd, zahteva novi ISO build za svaku skriptu | **Potpuno dinamičan: Launcher proverava SHA-256 i preuzima `gwadt-payload.zip` sa Hub-a u milisekundama** |
| **UAC Ponašanje** | Zahteva ručni klik ili nestabilne batch hakove | Privremeno gašenje bez garancije vraćanja | **Zero-Touch: Utišavanje u `specialize`/`oobeSystem` fazama i garantovano vraćanje na fabričke vrednosti u Fazi 5** |
| **Flotno Praćenje** | Lokalni tekstualni fajlovi na klijentskom Desktopu | Ručna evidencija servisera | **Automatska DMTF identifikacija (`TK-{Chassis}-{Serial}`) i slanje kompletnog kartona na ServisHub** |
| **Zaštita Servisnih Tajni** | Ručno brisanje profila od strane servisera | Skripte često ostavljaju profile u Windows registru | **Automatizovano Anti-Leak čišćenje: `netsh wlan delete profile`, brisanje INI fajlova i samouništenje `C:\setup`** |

---

## 3. Doneta Odluka i Arhitektura Rešenja (GWADT-TASK-075)

Usvojena je nova hibridna **Thin-Client & Dynamic Fleet Architecture** koja u potpunosti transformiše životni ciklus sistema kroz sledeće stubove:

```mermaid
flowchart TD
    subgraph S1["1. WinPE Faza & Staging (USB Fleš)"]
        U1[USB Fleš ili PXE Boot] --> U2[Windows Setup & $OEM$ mehanizam]
        U2 --> U3[Autonomno kopiranje na SSD: C:\setup\GWADT.Launcher.exe]
        U2 --> U4[Kopiranje lokalnog keša: C:\setup\Programi i Wifi-Servis.ini]
        U3 --> U5[PRVI RESTART: USB fleš se vadi iz računara - FLASH & RELEASE!]
    end

    subgraph S2["2. FirstLogon & Thin-Client Kontakt"]
        U5 --> L1[FirstLogonCommands: C:\setup\GWADT.Launcher.exe]
        L1 --> L2[UAC utišan: ConsentPromptBehaviorAdmin=0 / PromptOnSecureDesktop=0]
        L2 --> L3[Dark-Mode Splash ekran sa statusom adaptera]
        L3 --> L4[LAN detektovan ili WifiHelper spaja Wifi-Servis.ini]
        L4 --> L5[UDP Auto-Discovery :8888 ili Gateway :8080 traži ServisHub]
    end

    subgraph S3["3. Dinamički Payload & Master UI Launch"]
        L5 --> H1[GET /api/payload/version - Provera SHA-256 heša]
        H1 -- Lokalni paket zastareo ili nedostaje --> H2[GET /api/payload/download: gwadt-payload.zip]
        H1 -- Lokalni paket ažuran --> H3[Lokalni keš u C:\setup]
        H2 --> H3
        H3 --> H4[Otpakivanje u C:\setup i startovanje: GWADT.UI.exe --master]
    end

    subgraph S4["4. Izvršavanje Faza & Flotna Identifikacija"]
        H4 --> P1[PowerShell Modularni Radnici -Phase 1..5]
        P1 --> P2[Get-FleetDeviceId: TK-{Chassis}-{SerialNumber}]
        P2 --> P3[Programi primarno koriste lokalni SSD C:\setup\Programi]
        P3 --> P4[Fallback lanac: Winget silent -> NAS SMB -> Direct Web]
    end

    subgraph S5["5. Sigurnosno Čišćenje & Predaja Kupcu"]
        P4 --> C1[Send-FleetFinalReport: Slanje servisnog kartona na Hub]
        C1 --> C2[Clear-ServisWifiSecrets: netsh wlan delete profile i brisanje INI fajlova]
        C2 --> C3[Restore-ServisUacDefaults: Vraćanje UAC-a na Consent=5, SecureDesktop=1]
        C3 --> C4[Arhiviranje logova u C:\Windows\Logs\GWADT\]
        C4 --> C5[Asinhrono samouništenje: rmdir /s /q C:\setup]
        C5 --> C6[Čist Desktop i potpuno siguran sistem za kupca]
    end
```

---

## 4. Detaljan Prikaz Komponenti

### 4.1. C# Thin-Client Splash Launcher (`GWADT.Launcher.exe`)
- **Tehnološki profil:** Samostalni .NET Framework 4.0 / 4.5 izvršni fajl razvijen u čistom C# 5.0 kodu, bez eksternih zavisnosti. Veličina binarnog modula je samo **36,864 bajtova (~36 KB)**.
- **Kompilacija:** Unapred se gradi u Docker Wine MSBuild okruženju (`randomwinedocker/wine-dotnet472-msbuild:latest`) i smešta u `sources/$OEM$/$1/setup/GWADT.Launcher.exe`.
- **Funkcionalnosti:**
  1. *Vizuelni Dark-Mode interfejs:* Dizajniran u skladu sa radioničkim identitetom (tamna paleta `#0c1017`, kartica `#161b22`, cyan akcenat `#38bdf8`), sa real-time procentualnim barom i prikazom brzine mrežnog prenosa.
  2. *Autonomna mrežna inicijalizacija (`WifiHelper.cs`):* Ukoliko žični Ethernet LAN kabl nije priključen, launcher automatski parsira `C:\setup\Wifi-Servis.ini`, generiše privremeni XML profil i povezuje računar na radioničku Wi-Fi mrežu.
  3. *Hub Discovery (`HubDiscovery.cs`):* Putem UDP broadcast-a na portu 8888 pronalazi `ServisHub` server. U slučaju da je UDP broadcast blokiran mrežnim svičem, automatski testira IP adresu Default Gateway-a na portu 8080.
  4. *Upravljanje paketom (`PayloadManager.cs`):* Poziva `GET /api/payload/version` i upoređuje primljeni SHA-256 sa lokalnim `payload_meta.json`. Ako je paket izmenjen ili nedostaje, preuzima `gwadt-payload.zip`, vrši integritetnu verifikaciju i raspakuje ga u `C:\setup\`.
  5. *Pokretanje Master Shell-a:* Po uspešnom raspakivanju, pokreće `C:\setup\Skripte\GWADT.UI\GWADT.UI.exe --master` i gasi sopstveni proces.
  6. *Offline dijagnostički ekran:* Ako mrežna veza ili Hub nisu dostupni, prikazuje dijagnostičku tabelu sa svim mrežnim adapterima, generiše PC Speaker audio bip (`Console.Beep(800, 300)`) i nudi dugmad za ponovni pokušaj ("Pokušaj ponovo") ili pokretanje postojećeg lokalnog interfejsa.

### 4.2. WinPE $OEM$ Staging i "Flash & Release" Koncept
- Windows Setup nativno podržava `$OEM$` distribuciju: direktorijum `sources/$OEM$/$1/` tokom WinPE faze automatski preslikava svoj sadržaj u koren sistemskog diska (`C:\`).
- U `sources/$OEM$/$1/setup/` se smeštaju:
  * `GWADT.Launcher.exe` (36 KB)
  * `Install-ServisApps.ps1` i moduli
  * `Wifi-Servis.ini`
  * `Programi\` (osnovni instalacioni paketi: Chrome, WinRAR, Office ODT, Acrobat, DotNet, BatteryBar)
- **Operativna prednost ("Flash & Release"):** Čim Windows Setup završi kopiranje fajlova i inicira prvi restart računara, operater može fizički izvaditi USB fleš. Računar nastavlja kompletnu instalaciju sa lokalnog SSD-a i lokalne mreže.

### 4.3. Dinamički ServisHub Auto-Packager (`hub_server.py`)
- U centralni Python servis `hub_server.py` implementirana je klasa `PayloadManager`:
  * **Dinamičko praćenje `mtime`:** Prati vreme poslednje izmene nad svim fajlovima u folderu `Skripte/` (skeniranje traje svega ~0.5 ms).
  * **Automatsko prepakivanje:** Bilo kakva izmena koda koju programer sačuva na serveru automatski izaziva generisanje novog `gwadt-payload.zip` paketa (762 KB, kompresija nivoa 9).
  * **Keširanje i verzionisanje:** Kreira se `storage/cache/payload_meta.json` koji sadrži ISO timestamp, veličinu u bajtovima i SHA-256 heš.
  * **REST Rute:**
    - `GET /api/payload/version`: Vraća JSON sa metapodacima najnovijeg paketa.
    - `GET /api/payload/download`: Šalje binarni ZIP strim sa hederima `application/zip` i `X-Payload-SHA256`.

### 4.4. Zero-Touch UAC Utišavanje i Fabrička Restauracija
- **Utišavanje tokom instalacije:**
  U `autounattend.xml` (faze `specialize` kroz Order 7-8 i `oobeSystem` kroz FirstLogonCommands Order 2-3) upisuju se registarski ključevi:
  ```cmd
  reg.exe add "HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System" /v ConsentPromptBehaviorAdmin /t REG_DWORD /d 0 /f
  reg.exe add "HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System" /v PromptOnSecureDesktop /t REG_DWORD /d 0 /f
  ```
  Ovim se garantuje da nijedan sistemski servis, PowerShell proces ili MSI instaler ne može izazvati UAC prekid tokom rada.
- **Fabričko vraćanje pre predaje kupcu:**
  U modulu `GWADT.Cleanup.psm1`, funkcija `Restore-ServisUacDefaults` garantovano vraća standardne bezbednosne nivoe:
  ```powershell
  Set-ItemProperty -Path "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System" -Name "ConsentPromptBehaviorAdmin" -Value 5 -Force
  Set-ItemProperty -Path "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System" -Name "PromptOnSecureDesktop" -Value 1 -Force
  ```

### 4.5. Flotna Identifikacija i Centralizovani Servisni Kartoni
- **Funkcija `Get-FleetDeviceId` (`GWADT.Telemetry.psm1`):**
  * Analizira WMI/CIM `Win32_SystemEnclosure.ChassisTypes` (DMTF standard), prisustvo baterije (`Win32_Battery`) i regex modela.
  * Klasifikuje hardver u 4 tipa: `All-in-One`, `Laptop`, `Mini-PC`, `Desktop`.
  * Očitava serijski broj iz BIOS-a (`Win32_Bios.SerialNumber`).
  * Generiše jedinstveni identifikator u formatu: `TK-{Chassis}-{SerialNumber}` (npr. `TK-LAPTOP-5CD1234` ili `TK-DESKTOP-CZC9876`).
- **Funkcija `Send-FleetFinalReport` (`GWADT.Telemetry.psm1`):**
  * Agregira konačne statuse svih aplikacija, ukupno vreme deploymenta i listu upozorenja/grešaka.
  * Šalje HTTP POST zahtev na `POST /api/devices/{id}/report` na ServisHub.
  * ServisHub automatski validira podatke i arhivira ih u trajni JSON i formatirani Markdown servisni karton (`Logs/reports/{date}_{id}.md`).

### 4.6. Anti-Leak Sigurnosno Čišćenje (`Clear-ServisWifiSecrets`)
- Funkcija u `GWADT.Cleanup.psm1`:
  * Pronalazi sve definisane servisne SSID-ove u konfiguraciji.
  * Izvršava `netsh.exe wlan delete profile name="$ssid"` i garantovano `name="Wifi-Servis"`.
  * Trajno uklanja konfiguracione datoteke sa šiframa (`Wifi-Servis.ini` i `wifi_profile.xml`).
  * Sinhronizuje logove u trajni sistemski folder `C:\Windows\Logs\GWADT\`.
  * Pokreće asinhrono samouništenje privremenog radnog direktorijuma: `cmd.exe /c timeout /t 3 /nobreak >nul & rmdir /s /q "C:\setup"`.

---

## 5. Kontrolni Zbirovi Ključnih Artefakata (Paritet 100%)

| Artefakt | Lokacija | Veličina | MD5 kontrolna suma | SHA-256 kontrolna suma |
| :--- | :--- | :--- | :--- | :--- |
| **`GWADT.Launcher.exe`** | `Skripte/GWADT.Launcher/GWADT.Launcher.exe`<br>`sources/$OEM$/$1/setup/GWADT.Launcher.exe` | 36,864 B | `51d19a629ecc728a382158117f1bf812` | `47940884b18f9c5063f4d44931b58dcbaf5908dd30940f1453f15d9b1cacef8c` |
| **`Install-ServisApps.ps1`** | `Install-ServisApps.ps1`<br>`Skripte/Install-ServisApps.ps1`<br>`sources/$OEM$/$1/setup/Install-ServisApps.ps1` | 520.8 KB<br>(10,365 l.) | `5c7d3a354698d7e3d48b77c18cba558c` | `829b351f4d7ff1ca12a88431f9287c229ca9633ba7ab33242b7ed5cc312a52a4` |
| **`autounattend.xml`** | `autounattend.xml` | ~18 KB | `457f66051a74b8e08b6f00f1f4064242` | `67408e92ac937af9c85a37c552d4dea1de9575811567f10c0bb3d9a0078e46be` |
| **`FirstLogon.cmd`** | `FirstLogon.cmd`<br>`sources/$OEM$/$1/setup/FirstLogon.cmd` | 1,842 B | `005a98d46e8fb9bc67235ab05d3632cd` | `e03e54b68e7ec8ff1c05d761c5c70dbad694e9f7fe4ca7cf4dbd22384a6c6c74` |
| **`FirstLogon.ps1`** | `FirstLogon.ps1`<br>`sources/$OEM$/$1/setup/FirstLogon.ps1` | 8,245 B | `55135373197eda6c01016e3e5b309744` | `75317c2a71f00bf79fb04bb5288b438258b68aa4a8f946e3cb588a4eec9846b4` |

---

## 6. Posledice Odluke

### Pozitivne Posledice (Benefiti)
1. **Dramatično ubrzanje radioničkog toka:** Serviser može izvaditi fleš nakon ~2 minuta od starta instalacije i momentalno započeti rad na sledećoj mašini.
2. **Potpuni imunitet na UAC blokade:** Instalacija teče 100% Zero-Touch bez ikakvih modalnih dijaloga na Windows 11.
3. **Automatsko ažuriranje koda u realnom vremenu:** Nova verzija skripte ili interfejsa postaje aktivna na svim novim instalacijama čim se sačuva na serveru, bez novog ISO narezivanja.
4. **Višeslojni fallback za aplikacije:** Ako paket ne postoji u lokalnom SSD kešu, automatski se preuzima preko `winget.exe`, NAS sinhronizacije ili direktnog web linka.
5. **Apsolutna bezbednost i higijena:** Računar se klijentu isporučuje sa fabričkim UAC nivoom, bez zaostalih servisnih Wi-Fi šifara i sa potpuno očišćenim diskom.
6. **Centralizovana flotna arhiva:** ServisHub automatski čuva kartone svih pripremljenih računara sa hardverskim specifikacijama i trajanjem rada.

### Izazovi i Rešenja
- *Izazov:* Zavisnost od lokalne mrežne veze za osvežavanje payload-a.
  *Rešenje:* Ako mreža ili ServisHub nisu dostupni, `GWADT.Launcher.exe` poseduje "Offline Mode" koji automatski pokreće postojeće lokalne skripte već kopirane na SSD-u kroz `$OEM$` prenos.

---

## 7. Povezani Dokumenti i Reference
- Glavni Indeks: [[index]]
- Pregled Arhitekture: [[architecture/overview]]
- Pajplajn Izvršavanja: [[architecture/pipeline]]
- C# Master Task Runner: [[decisions/ADR-0044-csharp-ui-master-task-runner-and-hub-telemetry]]
- Tiha Instalacija Aplikacija: [[architecture/silent-applications]]
- Mrežna Telemetrija i ServisHub: [[decisions/ADR-0013-live-fleet-telemetry-servis-hub]]
- Poznate Zamke i Rešenja: [[memory/gotchas]]
