# Tehno kutak Windows Installation Assistant (WIS) v2.17.0

**WIS (Windows Installation Assistant)** je celovit, produkcioni **Zero-Touch** sistem za automatizovanu instalaciju, mrežnu PXE/USB distribuciju, hardversku dijagnostiku, Microsoft Autopilot/MDM forenziku, tihu instalaciju drajvera i aplikacija, kao i centralizovani nadzor flote računara na operativnom sistemu **Windows 11 Pro (24H2 / 25H2)**.

---

## Arhitektura Sistema (Četiri Razvojne Celine + Alati)

Projekat je organizovan u četiri glavne razvojne celine i namenske alate:

| Celina | Tehnologija | Opis i Uloga |
| :--- | :--- | :--- |
| **[`ISO/`](ISO/)** | PowerShell 5.1, Python 3, Windows Unattend XML, WinPE Batch | Izgradnja **Ultra-Lean Solid LZMS** Windows 11 ISO medijuma, Intel VMD Gen 11–14 automatska detekcija u WinPE, TrueNAS SMB 3.1.1 mrežna akceleracija (`WIS-ConnectNAS.cmd`, `WIS-AcceleratePE.cmd`) i RSA-2048 potpisivanje izdanja (`Build-WIS.ps1`, `Release-WIS.py`). |
| **[`Launcher/`](Launcher/)** | C# (.NET Framework 4.8) | **`WIS.Launcher.exe`** — Samostalni *Thin-Client* pokretač ugrađen u ISO sliku (`$OEM$`). Otkriva `ServiceHub` putem UDP 8888 broadcast-a ili SMB deljenja, kriptografski proverava RSA-2048 potpis i SHA-256 sumu `wis-payload.zip` paketa, raspakuje ga u `C:\setup` i pokreće `WIS.UI.exe`. |
| **[`UI/`](UI/)** | C# WPF (.NET 4.8) + PowerShell 5.1 (`Skripte/`) | **`WIS.UI.exe`** — Dvojezični (SRB/ZH) dijagnostički i orkestratorski kontrolni panel sa **8 tabova** i **6-faznim pajplajnom** (`Phase1-Network.ps1` do `Phase6-Finalize.ps1`), WMI/LHM telemetrijom na 1 Hz, 30-sekundnim CPU stres testom i IPC kanalom. |
| **[`ServiceHub/`](ServiceHub/)** | Python 3.10+ (0 eksternih pip zavisnosti), Docker | Centralni nadzorni i distribucijski server (`v3.2.0`, 32 modula) na portu `8080` i UDP `8888`. Pruža **Modern Enterprise Dark Mode Cockpit**, 1 Hz Live Screen Mirror u RAM-u, OEM provajdere drajvera (Lenovo, Dell, HP), MS Update Catalog scraper, DMTF kartone i UEFI USB Builder. |
| **[`Tools/AutopilotAuditor/`](Tools/AutopilotAuditor/)** | C# (.NET Framework 4.8) | **`WIS-AutopilotAuditor.exe`** — Samostalni troslojni forenzički alat za proveru Microsoft Intune / Autopilot ZTD statusa u registru, UEFI/SMBIOS-u i Microsoft oblaku. |

---

## 6-Fazni Servisni Pajplajn (`UI/Skripte/Phases/`)

1. **Faza 1 — Mreža i Servisni Wi-Fi (`Phase1-Network.ps1`):** Automatsko povezivanje na LAN ili servisni Wi-Fi iz `Wifi-Servis.ini`, fallback instalacija mrežnih drajvera, otkrivanje `ServiceHub` servera i slanje `STARTED` telemetrije.
2. **Faza 2 — Drajveri i Windows Update (`Phase2-Drivers.ps1`):** Pre-instalacija prikupljenih drajvera za konkretan model sa NAS-a (`<Model>.tar`), pretraga i instalacija WHQL drajvera putem Windows Update COM API-ja i automatsko prikupljanje (*harvesting*) novih drajvera nazad na NAS.
3. **Faza 3 — Sistemska Optimizacija i Brendiranje (`Phase3-Optimization.ps1`):** Uklanjanje bloatware-a, gašenje telemetrije, automatsko DPI skaliranje ekrana, instalacija Visual C++ AIO biblioteka, OEM brendiranje prema proizvođaču i kontrolisani međurestart.
4. **Faza 4 — Programi i OEM Licenca (`Phase4-Apps.ps1`):** Sekvencijalna instalacija aplikacija uz `Global\_MSIExecute` mutex zaštitu, direktan SMB 3.1.1 stream sa NAS deljenja, 6-stepeno zaključavanje PDF asocijacije, BatteryBar Floating Toolbar za laptopove, verifikacija UEFI/BIOS OA3x OEM licence i Post-Execution Reality Check.
5. **Faza 5 — Stres Test i Dijagnostika (`Phase5-Stress.ps1`):** 30-sekundni sintetički CPU stres test sa praćenjem termalne krive, provera S.M.A.R.T. zdravlja SSD-a, stepena istrošenosti baterije (*wear level*) i verifikacija 0 grešaka u Device Manager-u.
6. **Faza 6 — Finalizacija i Anti-Leak Čišćenje (`Phase6-Finalize.ps1`):** Slanje završnog DMTF servisnog kartona na `ServiceHub`, uklanjanje servisnih Wi-Fi profila i `Wifi-Servis.ini` datoteke, vraćanje UAC-a na podrazumevani nivo i samouništenje `C:\setup` direktorijuma pre gašenja računara.

---

## Brzi Početak i Konfiguracija

### 1. Priprema `Wifi-Servis.ini` konfiguracije
Za rad u servisnoj mreži kopirajte šablon `Wifi-Servis.example.ini` u `ISO/Wifi-Servis.ini` (i po potrebi `ISO/Skripte/Wifi-Servis.ini`) i unesite parametre vaše mreže i SMB servera:

```bash
cp Wifi-Servis.example.ini ISO/Wifi-Servis.ini
```

> **Napomena o bezbednosti:** Fajl `Wifi-Servis.ini` je u `.gitignore` i nikada se ne prati u Git repozitorijumu.

### 2. Pokretanje `ServiceHub` servera (Linux / TrueNAS Docker)
`ServiceHub` koristi isključivo standardnu biblioteku jezika Python 3 (bez eksternih `pip` zavisnosti):

```bash
cd ServiceHub
python3 main.py --port 8080
```
Ili putem Docker Compose-a:
```bash
cd ServiceHub
docker compose up -d --build
```

### 3. Izgradnja Windows binarnih komponenti (`WIS.UI.exe` i `WIS.Launcher.exe`)
Na Windows radnoj stanici sa PowerShell 5.1 i .NET Framework 4.8 SDK / MSBuild:

```powershell
powershell -ExecutionPolicy Bypass -File .\ISO\Skripte\Build-WIS.ps1
```

### 4. Izgradnja potpisanog OTA paketa i Windows 11 ISO medijuma
Za generisanje butabilnog ISO medijuma, potrebno je da u lokalni direktorijum `ISO/` (koji je izuzet iz Git-a za binarne WIM/EFI fajlove) postavite standardne Windows 11 instalacione fajlove (`boot/`, `efi/`, `sources/boot.wim`, `sources/install.esd`, `bootmgr*`), a zatim pokrenete:

```bash
python3 ISO/Skripte/Release-WIS.py
```

---

## Verifikacija i Testovi

Kompletna statička verifikacija sintakse (PowerShell 5.1 i C# 5), sinhronizacije verzija i jediničnih testova može se pokrenuti na Linux ili Windows okruženju:

```bash
# Provera PowerShell i C# sintakse i usklađenosti verzija
python3 ISO/Skripte/Verify-PowerShellSyntax.py
python3 ISO/Skripte/Verify-CSharpSyntax.py
python3 ISO/Skripte/Sync-Versions.py --check

# Jedinični i integracioni testovi za Launcher i ServiceHub
python3 Launcher/Tests/run_tests.py
cd ServiceHub && python3 -m unittest discover -s tests
```

---

## Dokumentacija

Detaljna tehnička dokumentacija i zapisi o arhitektonskim odlukama (ADR) nalaze se u direktorijumu [`docs/`](docs/):
- **[Tehnički opis sistema WIS v2.17.0](docs/TEHNICKI_OPIS_SISTEMA_WIS.md)** — Kompletna inženjerska specifikacija svih podsistema.
- **[Arhitektura podsistema (`docs/architecture/`)](docs/architecture/overview.md)** — Detaljni pregledi mreže, WinPE drajvera, Windows Update COM API-ja, UI-ja i ServiceHub-a.
- **[Arhitektonske odluke (`docs/decisions/`)](docs/decisions/)** — Zbirka ADR dokumenata (`ADR-0001` do `ADR-0158`).

---

## Licenca

Ovaj projekat je objavljen pod [MIT licencom](LICENSE).
