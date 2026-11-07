# ADR-0019: Kontejnerizacija i Autonomno Podizanje USB Tool Servisa u Docker Okruženju (Always-Available Daemon)

- **Status:** Prihvaćeno
- **Datum:** 2026-09-16
- **Autor:** GWADT Multi-Agent Sistem (`gwadt_orchestrator`, `gwadt_module_architect`, `gwadt_docs_sync`)
- **Kontekst:** Izdvajanje GWADT USB Tool servisa u samostalni Docker kontejner koji uvek radi u pozadini kao servisni demon, dostupan na servisnoj mreži na portu 8092 sa punim pristupom USB hardveru i mount namespace propagaciji.

---

## 1. Kontekst i Problem

U prethodnoj fazi (ADR-0018), razvijen je **GWADT USB Tool** kao lokalna Python 3 web kontrolna tabla (`http://127.0.0.1:8092`).
Iako je servis radio pouzdano, njegovo pokretanje je zavisilo od interaktivne CLI skripte (`./pokreni-usb-tool.sh`):
1. **Zavisnost od terminala:** Servis je radio samo dok je pokrenut prozor terminala. Zatvaranjem prozora ili odjavom servis bi se ugasio.
2. **Zahtev za stalnom dostupnošću:** U servisnom okruženju, serviserima je potrebno da kontrolna tabla bude **uvek aktivna** na radnoj stanici (`http://localhost:8092` ili preko LAN mreže `http://192.168.180.50:8092`), spremna da u deliću sekunde reaguje čim se USB utakne u bilo koji port.
3. **Izolacija i upravljanje zavisnostima:** Alati za rad sa diskovima i slikama (`wimlib-imagex`, `lsblk`, `ntfs-3g`, `dosfstools`, `xorriso`, `cabextract`, `aria2`, `gdbus`) treba da budu standardizovani unutar kontejnera, bez uticaja na promene na host operativnom sistemu.

---

## 2. Doneta Odluka

Usvojena je kompletna kontejnerizacija USB Tool podsistema putem namenskog **`Dockerfile`** i orkestracije kroz **`docker-compose.yml`**, uz uvođenje kontrolne skripte **`docker-usb-tool.sh`**:

### 1. Arhitektura Docker Slike (`USB Tool/Dockerfile`)
- **Bazni operativni sistem:** `python:3.12-slim-bookworm` sa minimalnim otiskom resursa.
- **Sistemski paketi:** `wimtools` (`wimlib-imagex`), `util-linux` (`lsblk`, `findmnt`, `blkid`), `ntfs-3g` (`ntfsfix`), `dosfstools` (`fsck.vfat`), `xorriso`, `genisoimage`, `cabextract`, `aria2`, `curl`, `dbus`, `libglib2.0-bin` (`gdbus`), `procps`.
- **Ekzekucija:** `python3 -u app.py --host 0.0.0.0 --port 8092 --no-browser`.

### 2. Orkestracija i Hardverski Pristup (`docker-compose.yml`)
- **Trajna dostupnost:** `restart: unless-stopped` obezbeđuje da se kontejner automatski startuje sa sistemom i ponovo podiže u slučaju restarta.
- **Mrežni režim:** `network_mode: host` omogućava direktno slušanje na host mrežnim interfejsima (LAN IP `192.168.180.50:8092` i `127.0.0.1:8092`) bez NAT kašnjenja i sa podrškom za lokalne broadcast poruke.
- **Hardverski pristup:** `privileged: true` i mapiranje `/dev:/dev` sa `/run/udev:/run/udev:ro`.
- **Propagacija tačaka montiranja sa Linux hosta (`:rslave`):**
  - Mapiranjem `/run/media:/run/media:rslave` i `/media:/media:rslave`, kada desktop okruženje (GNOME/UDisks2) na hostu automatski montira novoubačeni USB fleš, tačka montiranja se trenutno propagira unutar kontejnera.
- **DBus komunikacija:** Mapiranjem `/run/dbus/system_bus_socket:/run/dbus/system_bus_socket`, kontejner može direktno pozivati UDisks2 metode na hostu za čišćenje "dirty bit" zastavice i demontiranje diska.
- **Pre-Stage keš i ISO skladišta:** Povezani volumeni za Pre-Stage WIM (`/home/gru/Storage/gwadt_staging:/staging`), preuzimanja (`~/Downloads:/host_downloads:ro`) i skladište (`/home/gru/Storage:/host_storage:ro`).

### 3. Dvosmerna Kompatibilnost Koda
Python moduli (`app.py`, `usb_detector.py`, `patch_engine.py`, `system_inspector.py`, `usb_sync.py`, `iso_finder.py`) su prilagođeni da primarno očitavaju konfiguraciju iz promenljivih okruženja (`GWADT_PROJECT_ROOT`, `GWADT_STAGING_DIR`, `GWADT_PORT`, `GWADT_HOST_DOWNLOADS`, `GWADT_HOST_STORAGE`), a ukoliko one nisu definisane, automatski prelaze na lokalne relativne putanje. Time je očuvana 100% funkcionalnost direktnog pokretanja van kontejnera (`./pokreni-usb-tool.sh` i `Pokreni-UsbTool.bat`).

### 4. Kontroler Usluge (`docker-usb-tool.sh`)
Kreirana je objedinjena CLI skripta sa komandama:
```bash
./docker-usb-tool.sh start    # Podizanje kontejnera u pozadini
./docker-usb-tool.sh stop     # Zaustavljanje servisa
./docker-usb-tool.sh restart  # Restartovanje
./docker-usb-tool.sh status   # Pregled stanja, HTTP 200 provera i pristupnih adresa
./docker-usb-tool.sh logs     # Praćenje logova uživo
./docker-usb-tool.sh build    # Ponovna izgradnja slike
./docker-usb-tool.sh shell    # Ulazak u bash konzolu kontejnera
```

---

## 3. Posledice Odluke

### Pozitivne posledice:
1. **Always-On Servis:** Serviseri u svakom trenutku imaju dostupan kontrolni centar bez potrebe za otvaranjem konzole ili ručnim pokretanjem skripte.
2. **Hardverska transparentnost:** Zahvaljujući `:rslave` propagaciji i `/dev` mapiranju, kontejner se ponaša kao nativni sistemski servis sa trenutnom detekcijom USB fleš medijuma.
3. **Izolacija alata:** Svi teški alati (`wimlib-imagex`, `ntfsfix`, `genisoimage`) su zapakovani u kontejneru sa tačno kontrolisanim verzijama.
4. **Nulta regresija:** Kôd zadržava punu podršku za tradicionalno pokretanje direktno na hostu i na Windows operativnom sistemu.

### Zabeležene specifičnosti (Gotchas):
- Prilikom korišćenja Docker Compose-a sa `:rslave` propagacijom, izvorna putanja na hostu (`/run/media`) mora postojati pre kreiranja kontejnera (kreira se automatski ili postoji na svim modernim Linux distribucijama).
