# GWADT USB Tool - Centar za Održavanje i Nadogradnju USB Fleša

**GWADT USB Tool** je samostalna kontrolna tabla sa modernim grafičkim interfejsom (Dark-Mode Web Dashboard) namenjena serviserima za kompletno upravljanje, dijagnostiku, verifikaciju i 1-klik nadogradnju Windows 11 automatizovanog USB instalacionog medijuma.

---

## 🚀 Ključne Mogućnosti

1. **Automatsko Praćenje USB Portova (Hotplug Watcher)**:
   - Kontinuirano detektuje ubacivanje i vađenje USB fleš diskova u realnom vremenu bez potrebe za ručnim osvežavanjem.
   - Prikazuje model uređaja, oznaku (label), montiranu putanju, kapacitet diska i vizuelni indikator zauzetog prostora.

2. **Nativna Inspekcija GWADT Sistema & WIM Slike**:
   - Automatski proverava ključne markere sistema na ubačenom USB-u (`sources/install.wim`, `autounattend.xml`, `FirstLogon.cmd`, `Install-ServisApps.ps1`, `Programi/`, `Zakrpe/`).
   - Nativno čita WIM zaglavlje i XML metapodatke i prikazuje instalirani **Windows 11 Build** (npr. `Build 26200.8037`, edicija `Windows 11 Pro`).

3. **Online Provera Najnovijih Zakrpa (Microsoft Catalog / UUP)**:
   - Povezuje se na Microsoft servise i očitava najnoviji dostupni zvanični Windows 11 Build i naziv kumulativnog paketa.

4. **Pregled Stanja u Lokalnom Programu**:
   - Prikazuje verziju WIM slike i zakrpa spremnih na radnoj stanici servisa (Pre-Stage keš ili lokalni repozitorijum).
   - Izlistava sve preuzete kumulativne `.msu` i `.cab` zakrpe u direktorijumu `Zakrpe/` sa KB brojevima i veličinama.

5. **1-Klik Nadogradnja USB Fleša (Upgrade USB)**:
   - Kada je u programu pripremljena novija verzija, dugme **"Nadogradi USB na Novu Verziju"** automatski se aktivira.
   - Klikom na dugme pokreće se automatizovani Quick-Sync prenos:
     - Višenitni prenos WIM-a u blokovima od 16 MB sa praćenjem brzine (MB/s), procenta i preostalog vremena (ETA).
     - Automatska sinhronizacija deploy skripti (`Install-ServisApps.ps1`, `autounattend.xml`, `FirstLogon.cmd`, `Wifi-Servis.ini`).
     - Pozivanje sistemskog `sync` bafer flush-a i emitovanje zvučnog signala završetka!

6. **Brzo Osvežavanje Skripti (Quick Script Sync)**:
   - Mogućnost ažuriranja samo PowerShell deploy skripti i XML konfiguracije za manje od 2 sekunde, bez potrebe za ponovnim prenosom velikog WIM fajla.

7. **Bezbedno Demontiranje (Eject)**:
   - 1-klik demontiranje USB uređaja uz automatski `sync` radi bezbednog fizičkog vađenja iz računara.

---

## 📁 Struktura Direktorijuma

```
USB Tool/
├── app.py                     # Glavni backend server (Python 3 HTTP + REST API, bez eksternih paketa)
├── Dockerfile                 # Kontejnerska definicija (Python 3.12 + wimtools + ntfs-3g + gdbus)
├── docker-compose.yml         # Orkestracija servisa (Always-Available, rslave mount propagacija)
├── docker-usb-tool.sh         # Kontrolna skripta (start, stop, status, logs, build)
├── pokreni-usb-tool.sh        # Lokalni pokretač za Linux sisteme
├── Pokreni-UsbTool.bat        # Lokalni pokretač za Windows sisteme
├── core/                      # Modularna logika
│   ├── usb_detector.py        # Detekcija USB drajvova (lsblk na Linuxu, CIM na Windowsu)
│   ├── system_inspector.py    # Nativni WIM parser i detektor GWADT markera
│   ├── patch_engine.py        # Online/offline provera verzija i analiza foldera Zakrpe
│   ├── iso_finder.py          # Skener lokalnih diskova i Microsoft portali
│   └── usb_sync.py            # Motor višenitnog prenosa, praćenja progresa i sinhronizacije
├── gui/                       # Korisnički interfejs (Dark Mode Glassmorphism)
│   ├── index.html             # HTML5 kontrolna tabla
│   ├── style.css              # Stilovi, kartice, progress barovi i animacije
│   └── app.js                 # Klijentska logika i real-time polling
└── README.md                  # Ova dokumentacija
```

---

## 🛠️ Pokretanje

### 🐳 A. Kao trajni Docker servis (Preporučeno - Uvek Dostupno):
Za celodnevni rad servisa gde kontrolna tabla neprekidno radi u pozadini bez potrebe za otvorenim terminalom:
```bash
./docker-usb-tool.sh start
```
Upravljanje kontejnerom:
- Provera stanja i pristupnih adresa: `./docker-usb-tool.sh status`
- Pregled logova u realnom vremenu: `./docker-usb-tool.sh logs`
- Zaustavljanje servisa: `./docker-usb-tool.sh stop`
- Restartovanje servisa: `./docker-usb-tool.sh restart`
- Ponovna izgradnja slike: `./docker-usb-tool.sh build`

### 💻 B. Direktno pokretanje na Linux radnoj stanici:
```bash
./pokreni-usb-tool.sh
```
Ili iz samog direktorijuma:
```bash
cd "USB Tool"
./pokreni-usb-tool.sh
```

### 🪟 C. Na Windows mašini:
Dvokliknite na:
```cmd
Pokreni-UsbTool.bat
```

Kontrolni panel se automatski otvara u vašem internet pregledaču na adresi:
`http://127.0.0.1:8092` (ili prvom sledećem slobodnom portu).

### 🌐 Pristup sa drugog računara u lokalnoj mreži (LAN):
Server podrazumevano sluša na svim interfejsima (`0.0.0.0`), pa mu možete pristupiti sa bilo kog uređaja u mreži (npr. iz `192.168.180.x` podmreže):
```
http://<IP_ADRESA_SERVERA>:8092
```
Primer za ovu mašinu: `http://192.168.180.50:8092`.
