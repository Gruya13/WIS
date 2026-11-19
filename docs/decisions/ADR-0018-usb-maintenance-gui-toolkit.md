# ADR-0018: Objedinjeni GUI Centar za Održavanje i Nadogradnju USB Fleša (USB Tool)

- **Status:** Prihvaćeno
- **Datum:** 2026-09-15
- **Autor:** GWADT Multi-Agent Sistem (`gwadt_orchestrator`, `gwadt_module_architect`, `gwadt_ui_architect`, `gwadt_qa_debugger`, `gwadt_docs_sync`)
- **Kontekst:** Jedinstveni grafički kontrolni panel (GUI) za detekciju USB-a, poređenje verzija zakrpa i 1-klik nadogradnju

---

## 1. Kontekst i Problem

Dosadašnji alati za održavanje instalacionog USB fleš medijuma bili su distribuirani kroz više pojedinačnih CLI i batch skripti:
- `Skripte/Update-WimImage.ps1` i `Osvezi-WimSliku.bat` (Windows DISM slipstreaming).
- `Skripte/Update-WimImage-Linux.py` i `osvezi-wim-linux.sh` (Linux UUP / ISO / Hotplug Watcher).
- `Skripte/Build-DeployScript.py` (rekompajliranje monolitnih skripti).
- `Zakrpe/` folder sa preuzetim `.msu` i `.cab` paketima.

Serviserima u svakodnevnom radu bila je potrebna **jedinstvena tačka kontrole sa intuitivnim vizuelnim GUI interfejsom** koja bi automatski:
1. Prikazivala ubačeni USB uređaj, kapacitet i stepen popunjenosti.
2. Nativno analizirala USB i utvrdila da li je na njemu prisutan GWADT sistem i koja je tačna verzija (Windows 11 Build).
3. Povezivala se sa Microsoft serverima i proveravala najnovije kumulativne zakrpe online.
4. Upoređivala stanje USB-a sa verzijom pripremljenom u programu / lokalnom kešu.
5. Omogućavala **1-klik nadogradnju USB-a** sa praćenjem prenosa u realnom vremenu (MB/s, preostalo vreme, logs).

---

## 2. Doneta Odluka

Usvojeno je kreiranje posebnog podsistema u korenu projekta pod nazivom **`USB Tool/`**:
- **Arhitektura nulte zavisnosti (Zero External Dependencies):** GUI je izgrađen kao lokalni HTTP servis baziran na standardnoj Python 3 biblioteci (`http.server.ThreadingHTTPServer`), što omogućava trenutan rad na Linuxu i Windowsu bez potrebe za instalacijom `tkinter`, `PyQt` ili `flask` biblioteka.
- **Moderan Dark-Mode Web Dashboard:** Responzivni Cyber Glassmorphism interfejs (HTML5, moderni CSS tokeni, vanila JavaScript bez teških frameworka).
- **Automatski Hotplug Polling (2.5s):** Klijentska aplikacija kontinualno osluškuje stanje USB portova; čim serviser ubaci fleš, interfejs ga automatski prepoznaje.
- **Nativni WIM Parser:** Ugrađeni binarni parser koji direktno iz WIM zaglavlja (`MSWIM\0\0\0`) i XML resursa očitava verziju i edicije bez pozivanja eksternih alata.
- **1-Klik Quick-Sync Motor:** Višenitni prenos WIM-a u 16 MB blokovima, ažuriranje skripti (`Install-ServisApps.ps1`, `autounattend.xml`, `FirstLogon.cmd`, `Wifi-Servis.ini`), sistemski `sync` i audio signalizacija.

### Arhitektura Podsistema:

```
USB Tool/
├── app.py                     # Centralni GUI REST & Static File Server (Port 8092)
├── pokreni-usb-tool.sh        # Linux pokretač sa automatskim startovanjem pregledača
├── Pokreni-UsbTool.bat        # Windows pokretač
├── core/                      # Modularni motor
│   ├── usb_detector.py        # Detekcija USB diskova (Linux lsblk / Windows CIM)
│   ├── system_inspector.py    # Detekcija GWADT markera i čitanje WIM metapodataka
│   ├── patch_engine.py        # Online Microsoft provera i skeniranje foldera Zakrpe/
│   └── usb_sync.py            # Višenitni prenos WIM-a, praćenje brzine i sync skripti
└── gui/                       # Korisnički interfejs
    ├── index.html             # Kontrolna tabla
    ├── style.css              # Glassmorphism tema, kartice, progress barovi
    └── app.js                 # Frontend logika, real-time polling i event listeneri
```

---

## 3. Posledice Odluke

### Pozitivne posledice:
1. **Objedinjen rad:** Sve operacije održavanja fleša (detekcija, verzije, online provera, zakrpe, nadogradnja, demontiranje) nalaze se na jednom ekranu.
2. **Brzina i bezbednost:** Serviser na jedan klik dobija jasan uvid: `[Verzija na USB-u]` vs `[Verzija u Programu]` vs `[Online Microsoft Verzija]`.
3. **Pouzdanost:** Automatski `sync` bafera garantuje da neće doći do korupcije fajlova prilikom vađenja USB fleša.
4. **100% pokrivenost testovima:** Svi novi moduli integrisani su u `Skripte/Test-GWADTModules.py` i prolaze 100% PASS.

---

## 4. Povezane Beleške
- [[operations/usb-tool|Uputstvo za Korišćenje USB Tool-a]]
- [[operations/media-maintenance|Održavanje Fleš Medijuma]]
- [[decisions/ADR-0017-local-prestage-and-usb-hotplug-sync|ADR-0017: Pre-stage Keš i Hotplug]]
- [[index|Glavni Indeks]]
