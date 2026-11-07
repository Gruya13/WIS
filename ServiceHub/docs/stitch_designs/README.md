# Google Stitch Dizajn Katalog & Vodič: Servis Hub Enterprise Cockpit

Dobrodošli u zvanični **Google Stitch dizajn katalog** za projekat **Servis Hub** (v3.1.0).

Ovaj dokument služi kao jedinstveni izvor istine za korisnički interfejs (UI/UX) ServisHub-a, generisan i održavan na [Google Stitch AI platformi](https://stitch.withgoogle.com) u skladu sa [[wiki/decisions/ADR-0005-enterprise-dark-mode-cockpit-redesign|ADR-0005: Modern Enterprise Dark Mode Cockpit Redizajn]].

---

## 📌 Osnovne Informacije o Projektu na Google Stitch-u

| Parametar | Vrednost |
|---|---|
| **Stitch Projekat** | `Servis Hub Enterprise Cockpit` |
| **Project ID** | `16865739281301355668` |
| **Platforma** | [stitch.withgoogle.com](https://stitch.withgoogle.com) |
| **Dizajn Sistem** | [DESIGN.md](file:///home/gru/Projects/Servis%20Hub/DESIGN.md) (Linear / Vercel Modern Dark Mode) |
| **Lokalni Direktorijum Asseta** | [docs/stitch_designs/](file:///home/gru/Projects/Servis%20Hub/docs/stitch_designs/) |
| **Arhitektonska Odluka** | [[wiki/decisions/ADR-0005-enterprise-dark-mode-cockpit-redesign|ADR-0005]] |
| **Arhitektura Implementacije** | Zero-dependency (Vanilla HTML5, moderni CSS3, Vanilla JS ugrađen u `servishub.web.dashboard`) |

---

## 🎨 1. Sistem Dizajna: `DESIGN.md`

Dizajn sistem definisan je u datoteci [DESIGN.md](file:///home/gru/Projects/Servis%20Hub/DESIGN.md) i primenjuje se uniformno na sve ekrane servisa:

* **Estetika i Atmosfera:** Modern Enterprise Dark Mode, Linear/Vercel vizuelni identitet, radionički cockpit visoke informacione gustine, suptilne 1px ivice bez teških senki.
* **Paleta Boja:**
  - `Canvas / Platno`: `#0F172A` (Duboka tamna podloga)
  - `Surface / Kontejner`: `#1E293B` (Povišene kartice, paneli, sidebari)
  - `Border / Razdelnik`: `#334155` (Suptilne 1px granice)
  - `Primary Brand / Akcenat`: `#38bdf8` (Sky Blue — primarni CTA tasteri, aktivni tabovi, progress barovi)
  - `Secondary Accent`: `#3B82F6` (Hover stanja, statusni tagovi)
  - `Text Primary`: `#F8FAFC` (Visoki kontrast, naslovi i primarni tekst — WCAG AAA)
  - `Text Muted`: `#94A3B8` (Pomoćne oznake, jedinice mere, placeholderi)
  - `Status Success`: `#10B981` (Završene faze, aktivne veze, zeleni indikatori)
  - `Status Warning`: `#F59E0B` (Reboot, pažnja, privremeni zastoj)
  - `Status Danger`: `#EF4444` (Greške u instalaciji, blokirani diskovi)
* **Tipografija:** Primarni font `Inter` (`-apple-system`, `BlinkMacSystemFont`), za telemetriju i terminale `JetBrains Mono` / `Cascadia Code`.
* **Geometrija:** Radijus kartica i panela `12px` (mali elementi `6px`, pilule/bedževi `9999px`).

---

## 🖥️ 2. Katalog Generisanih Ekrana

Katalog sadrži 5 produkcionih ekrana koji u potpunosti pokrivaju operativne potrebe servisa računara "Tehno-Kutak":

```
docs/stitch_designs/
├── DESIGN.md                          <-- Globalni Design Tokens & Typography
├── live_fleet_cockpit.html            <-- Ekran 1: Fleet Cockpit Dashboard
├── live_fleet_cockpit.png
├── wis_usb_builder.html               <-- Ekran 2: WIS USB & ISO Builder Centar
├── wis_usb_builder.png
├── service_record_agy.html            <-- Ekran 3: Digitalni Servisni Karton & AGY Sesije
├── service_record_agy.png
├── oem_drivers_firmware.html          <-- Ekran 4: OEM Drajveri & Firmware Centar
├── oem_drivers_firmware.png
├── windows_updates_cache.html         <-- Ekran 5: Windows 11 Zakrpe (TrueNAS Keš)
├── windows_updates_cache.png
└── generation_summary.json            <-- Tehnički metapodaci generisanja
```

---

### Ekran 1: Radionička Flota & Live Cockpit (`live_fleet_cockpit` - v2)
* **Naziv na Stitch-u:** `Servis Hub - Telemetry Cockpit` (Radionička Flota v2)
* **Screen ID:** `3945733ff2924a26ac6382a52b1811d1` (v2 revizija)
* **Lokalni Fajlovi:**
  - HTML: [live_fleet_cockpit.html](file:///home/gru/Projects/Servis%20Hub/docs/stitch_designs/live_fleet_cockpit.html) (29.2 KB)
  - Screenshot: [live_fleet_cockpit.png](file:///home/gru/Projects/Servis%20Hub/docs/stitch_designs/live_fleet_cockpit.png) (162.6 KB)
* **Canvas Rezolucija:** `2560 x 2444` (Desktop)
* **Opis i Ključne Komponente:**
  - **Ergonomski Collapsible Sidebar:** Smanjenje sa 275px na 68px jednim klikom radi oslobađanja radnog prostora. U dnu sidebara je Server Vitals panel (TrueNAS ZFS status, Sistemski Uptime, UDP:8888 Auto-Discovery, sinhronizovani sat).
  - **Top Bar sa Brzim Filterima:** Globalna pretraga računara po imenu/IP/HWID-u i filteri (`Sve`, `Aktivno`, `Završeno`, `Greška`).
  - **Leva Kolona (Fleet Grid):** Kartice aktivnih računara na stolovima (Sto #1 do Sto #4) sa statusnim indikatorima faza (1: UEFI Boot $\rightarrow$ 2: Particionisanje $\rightarrow$ 3: Drajveri $\rightarrow$ 4: Skripte $\rightarrow$ 5: Gotovo).
  - **Desna Kolona (Fokusirani Cockpit):**
    - **1 Hz Live Ogledalo Ekrana (Screen Mirror):** Web stream radne površine klijenta u realnom vremenu (1 frejm u sekundi).
    - **Merači Hardverske Telemetrije:** CPU temperatura i opterećenje, RAM zauzeće i NVMe S.M.A.R.T. zdravlje diska.
    - **Quick Action Traka:** Restartovanje klijenta, ponovno pokretanje PXE procesa, preuzimanje logova i pokretanje AGY sesije.
    - **SSE Dijagnostička Konzola:** Ugrađeni tamni terminal sa live praćenjem logova (`tail -f`).

---

### Ekran 2: WIS USB & ISO Builder Centar (`wis_usb_builder` - v2)
* **Naziv na Stitch-u:** `Servis Hub - UEFI USB & ISO Builder Center` (WIS USB Builder v2)
* **Screen ID:** `da90fa64e14d48a397a03ce534477ec9` (v2 revizija)
* **Lokalni Fajlovi:**
  - HTML: [wis_usb_builder.html](file:///home/gru/Projects/Servis%20Hub/docs/stitch_designs/wis_usb_builder.html) (23.1 KB)
  - Screenshot: [wis_usb_builder.png](file:///home/gru/Projects/Servis%20Hub/docs/stitch_designs/wis_usb_builder.png) (107.0 KB)
* **Canvas Rezolucija:** `2560 x 2432` (Desktop)
* **Opis i Ključne Komponente:**
  - **Safety Guardrails Selektor USB Fleševa:** Vizuelni prikaz svih priključenih diskova. Sistemski diskovi (Root `/dev/sda` i ZFS Pool `/dev/sdb`) su crveno zaključani sa oznakom `FORBIDDEN / SYSTEM DRIVE` i onemogućeni za izbor. Eksterni prenosivi USB mediji (npr. Kingston DataTraveler 64GB na `/dev/sdc`) su jasno označeni za selekciju.
  - **Windows 11 Verzija i Edicija:** Izbor ISO slike (Windows 11 24H2 Enterprise, IoT Enterprise LTSC, Pro for Workstations).
  - **UEFI GPT Hibridna Particiona Šema:** Automatsko kreiranje FAT32 boot particije (Secure Boot kompatibilnost) i NTFS data particije za `install.wim` datoteke veće od 4GB.
  - **OEM & Pre-Patch Injekcija:** Čekboksovi za automatsko ubacivanje Lenovo/HP/Dell drajvera iz lokalnog keša i `Autounattend.xml` automatizaciju.
  - **SSE Traka Napretka:** Prikaz toka upisa u realnom vremenu (Particionisanje $\rightarrow$ Ekstrakcija $\rightarrow$ Verifikacija) sa prikazom brzine prenosa podataka (MB/s).

---

### Ekran 3: Digitalni Servisni Karton & AGY Sesije (`service_record_agy`)
* **Naziv na Stitch-u:** `Servis Hub - Digital Service Record & Diagnostics`
* **Screen ID:** `58a0b24e69c746bdb7ba29c953a5f9f8`
* **Lokalni Fajlovi:**
  - HTML: [service_record_agy.html](file:///home/gru/Projects/Servis%20Hub/docs/stitch_designs/service_record_agy.html) (27.6 KB)
  - Screenshot: [service_record_agy.png](file:///home/gru/Projects/Servis%20Hub/docs/stitch_designs/service_record_agy.png) (119.9 KB)
* **Canvas Rezolucija:** `2560 x 2226` (Desktop)
* **Opis i Ključne Komponente:**
  - **Zaglavlje Radnog Naloga:** Broj servisnog naloga, datum prijema, identifikacija klijenta, serijski broj i model mašine (npr. Lenovo ThinkStation P360).
  - **Hardverska Inspekcija & Dijagnostika:** Detaljna lista komponenti sa serijskim brojevima, S.M.A.R.T. statusom skladišta, termalnim profilom i memorijskim testovima.
  - **Pre-Flight Kontrolna Lista:** Interaktivni checklist pre predaje računara (BIOS ažuriran, TPM 2.0 aktivan, Windows digitalna licenca, Device Manager bez nepoznatih uređaja, MemTest čist).
  - **Antigravity CLI Serviser Dnevnik:** Sinhronizovane AI sesije sa zabeleženim automatizovanim koracima, pokrenutim skriptama i verifikacionim izveštajima.
  - **Centar za Izvoz:** Dugmad za generisanje štampanog servisnog sertifikata (Markdown/PDF) i trajno arhiviranje u `storage/reports/`.

---

### Ekran 4: OEM Drajveri & Firmware Centar (`oem_drivers_firmware`)
* **Naziv na Stitch-u:** `Servis Hub - OEM Drivers & Firmware Hub`
* **Screen ID:** `2b3a6237f4b141a1929eb6ccc0fabd3d`
* **Lokalni Fajlovi:**
  - HTML: [oem_drivers_firmware.html](file:///home/gru/Projects/Servis%20Hub/docs/stitch_designs/oem_drivers_firmware.html) (24.1 KB)
  - Screenshot: [oem_drivers_firmware.png](file:///home/gru/Projects/Servis%20Hub/docs/stitch_designs/oem_drivers_firmware.png) (100.6 KB)
* **Canvas Rezolucija:** `2560 x 2048` (Desktop)
* **Opis i Ključne Komponente:**
  - **Fabrička Garancija (Lenovo eSupport):** Pretraga po serijskom broju / MTM kodu, prikaz preostale garancije, datuma isteka i nivoa podrške (On-site / Depot), uz 5s failover na lokalni keš.
  - **TrueNAS ZFS SCCM Repozitorijum:** Lista keširanih paketa drajvera organizovanih po modelima (ThinkPad T14 Gen3, ThinkCentre M70q, itd.), veličine paketa i integriteta (SHA-256).
  - **BIOS/UEFI Firmware & Sigurnosne Preporuke:** Poređenje trenutne verzije BIOS-a sa najnovijom fabričkom, preporuke za Intel ME i AMD PSP zakrpe, uz dugme "Preuzmi u lokalni keš".
  - **Microsoft Update Catalog Scraper Status:** Uvid u automatski proxy drajvera, broj pogođenih CAB paketa i uštedu protoka.

---

### Ekran 5: Windows 11 Zakrpe (TrueNAS Keš) (`windows_updates_cache`)
* **Naziv na Stitch-u:** `Servis Hub - Windows Updates & Pre-Patching Cache`
* **Screen ID:** `bd444814b01e45f38fd04d28f3f4a703`
* **Lokalni Fajlovi:**
  - HTML: [windows_updates_cache.html](file:///home/gru/Projects/Servis%20Hub/docs/stitch_designs/windows_updates_cache.html) (26.7 KB)
  - Screenshot: [windows_updates_cache.png](file:///home/gru/Projects/Servis%20Hub/docs/stitch_designs/windows_updates_cache.png) (99.0 KB)
* **Canvas Rezolucija:** `2560 x 2048` (Desktop)
* **Opis i Ključne Komponente:**
  - **TrueNAS Update Cache Pregled:** Ukupna veličina skladišta zakrpi, slobodan prostor na ZFS pool-u i broj indeksiranih `.msu` i `.cab` paketa.
  - **Metrike Uštede i Protokola:** Grafički prikaz uštede propusnog opsega (85%+ keš hitova) i brzine isporuke kroz gigabitnu lokalnu mrežu servisa.
  - **Katalog Kumulativnih Ažuriranja:** Tabela sa najnovijim kumulativnim zakrpama za Windows 11 (23H2 / 24H2), arhitekturom (x64 / ARM64), datumom objave i statusom na disku.
  - **Pre-Patching Pipeline Kontrola:** Pokretanje pozadinske sinhronizacije (`nas_update_sync.py`), skeniranje novih zakrpi i integracija u offline instalacioni proces.

---

## 📊 3. Tabela Komponenti, Rezolucija i Veličina Fajlova

| # | Ekran / Modul | Stitch Screen ID | Rezolucija Platna | HTML Fajl (Veličina) | PNG Screenshot (Veličina) | Ključne UI Komponente |
|---|---|---|---|---|---|---|
| **0** | **DESIGN.md** | — | — | [DESIGN.md](file:///home/gru/Projects/Servis%20Hub/DESIGN.md) (2.9 KB) | — | Design tokeni, paleta boja, tipografija, WCAG AA pravila |
| **1** | **Fleet Cockpit Dashboard (v2)** | `3945733ff2924a26ac6382a52b1811d1` | 2560 x 2444 | [live_fleet_cockpit.html](file:///home/gru/Projects/Servis%20Hub/docs/stitch_designs/live_fleet_cockpit.html) (29.2 KB) | [live_fleet_cockpit.png](file:///home/gru/Projects/Servis%20Hub/docs/stitch_designs/live_fleet_cockpit.png) (162.6 KB) | Collapsible sidebar, Split-pane fleet grid, 1 Hz screen mirror, CPU/RAM/NVMe telemetrija, SSE terminal |
| **2** | **WIS USB Builder Centar (v2)** | `da90fa64e14d48a397a03ce534477ec9` | 2560 x 2432 | [wis_usb_builder.html](file:///home/gru/Projects/Servis%20Hub/docs/stitch_designs/wis_usb_builder.html) (23.1 KB) | [wis_usb_builder.png](file:///home/gru/Projects/Servis%20Hub/docs/stitch_designs/wis_usb_builder.png) (107.0 KB) | Safety guardrails disk selektor (zaključan root/ZFS), Win11 verzije, UEFI GPT hibrid, SSE progress bar |
| **3** | **Digitalni Servisni Karton** | `58a0b24e69c746bdb7ba29c953a5f9f8` | 2560 x 2226 | [service_record_agy.html](file:///home/gru/Projects/Servis%20Hub/docs/stitch_designs/service_record_agy.html) (27.6 KB) | [service_record_agy.png](file:///home/gru/Projects/Servis%20Hub/docs/stitch_designs/service_record_agy.png) (119.9 KB) | Karton radnog naloga, HW inspekcija, Pre-flight checklist, AGY CLI sesije, Izvoz u Markdown/JSON |
| **4** | **OEM Drajveri & Firmware** | `2b3a6237f4b141a1929eb6ccc0fabd3d` | 2560 x 2048 | [oem_drivers_firmware.html](file:///home/gru/Projects/Servis%20Hub/docs/stitch_designs/oem_drivers_firmware.html) (24.1 KB) | [oem_drivers_firmware.png](file:///home/gru/Projects/Servis%20Hub/docs/stitch_designs/oem_drivers_firmware.png) (100.6 KB) | Lenovo eSupport provera garancije, TrueNAS ZFS SCCM keš, BIOS verzije i bezbednosni saveti, CAB scraper |
| **5** | **Windows 11 Zakrpe Keš** | `bd444814b01e45f38fd04d28f3f4a703` | 2560 x 2048 | [windows_updates_cache.html](file:///home/gru/Projects/Servis%20Hub/docs/stitch_designs/windows_updates_cache.html) (26.7 KB) | [windows_updates_cache.png](file:///home/gru/Projects/Servis%20Hub/docs/stitch_designs/windows_updates_cache.png) (99.0 KB) | TrueNAS skladište, metrike uštede protoka (85%+ hit), katalog KB zakrpi, pre-patching kontrolna tabla |

---

## 🛠️ 4. Uputstvo za Korisnika: Rad na Google Stitch Platformi

Korisnici i inženjeri mogu u bilo kom trenutku pregledati, doraditi i izmeniti dizajn ekrana na platformi [stitch.withgoogle.com](https://stitch.withgoogle.com).

### Korak 1: Pristup i Pregled Ekrana u Browseru
1. Otvorite pretraživač i idite na adresu: **[stitch.withgoogle.com](https://stitch.withgoogle.com)**.
2. Prijavite se sa povezanim Google nalogom.
3. Otvorite projekat: **`Servis Hub Enterprise Cockpit`** (Project ID: `16865739281301355668`).
4. U levom panelu ili na centralnom platnu izaberite željeni ekran prema njegovom Screen ID-ju. Možete pregledati interaktivni HTML prikaz, responsive tačke prekida i vizuelni screenshot.

### Korak 2: Prompt Editovanje i Dorada Ekrana

Postoje dva načina za unos izmena:

#### A) Kroz Stitch Web Interfejs (Preporučeno za brzi vizuelni fidbek)
- Kliknite na ekran koji želite da izmenite.
- U desnom prompt panelu unesite prirodnim jezikom šta želite da promenite (npr. *"Dodaj dugme za izvoz servisnog kartona u CSV u gornjem desnom uglu"* ili *"Promeni boju statusnog bedža za neuspele instalacije u tamnocrvenu"*).
- Kliknite na **Generate / Update**. Stitch će generisati novu reviziju ekrana.

#### B) Kroz Google Antigravity Stitch MCP Alat (`edit_screens`)
U Antigravity asistentu ili CLI-ju možete pokrenuti programsko prompt uređivanje pozivom MCP alata:
```json
{
  "project_id": "16865739281301355668",
  "screen_ids": ["58a0b24e69c746bdb7ba29c953a5f9f8"],
  "prompt": "Dodaj interaktivnu pretragu po serijskom broju u sekciju sa Antigravity CLI logovima i naglasi datum poslednje uspešne dijagnostike."
}
```

### Korak 3: Generisanje Alternativnih Varijanti (`generate_variants`)
Ako želite da istražite alternativne rasporede elemenata za određeni ekran:
- U web interfejsu kliknite na **Generate Variants**.
- Ili kroz MCP alat `generate_variants` prosledite `project_id` i `screen_id`. Stitch kreira do 3 dizajnerske varijante sa različitom ergonomijom.

### Korak 4: Preuzimanje Izmenjenih Asseta na Lokalni Disk
Kada ste zadovoljni novim izgledom ekrana, preuzmite najnoviji HTML i PNG direktno u radni direktorijum projekta:

Preko Stitch MCP alata `download_screen_assets`:
```json
{
  "project_id": "16865739281301355668",
  "output_dir": "/home/gru/Projects/Servis Hub/docs/stitch_designs"
}
```
Ova komanda automatski sinhronizuje najnovije HTML i PNG datoteke u folder `docs/stitch_designs/`.

### Korak 5: Integracija u ServisHub Aplikaciju
U skladu sa pravilom **Zero-Dependency Arhitekture** (bez npm i Webpack-a):
1. Otvorite preuzeti HTML fajl iz `docs/stitch_designs/`.
2. Proverite CSS klase i DOM identifikatore (`tab-btn-*`, `wis-*`, `updates-*`, `modal-*`).
3. Integraciju u Python web server vršite u modulu [servishub/web/dashboard.py](file:///home/gru/Projects/Servis%20Hub/servishub/web/dashboard.py), gde se ugrađeni HTML5/CSS3 generiše direktno kroz čiste string templejte.
4. Pokrenite automatizovane testove (`pytest`) da potvrdite očuvanje svih postojećih REST API ugovora.

---

## 🔗 Povezani Dokumenti i Memorija

* **Arhitektonska Odluka o Redizajnu:** [[wiki/decisions/ADR-0005-enterprise-dark-mode-cockpit-redesign]]
* **Pregled Arhitekture Sistema:** [[wiki/architecture/overview]]
* **Glavni Tehnički Priručnik:** [docs/TEHNICKA_DOKUMENTACIJA.md](file:///home/gru/Projects/Servis%20Hub/docs/TEHNICKA_DOKUMENTACIJA.md)
* **Master Indeks Znanja (MOC):** [[wiki/index]]
