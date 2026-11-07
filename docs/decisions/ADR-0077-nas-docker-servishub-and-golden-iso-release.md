---
title: "ADR-0077: Produkciona Docker Implementacija ServisHub-a na TrueNAS-u, Fiksno Rutiranje na Portu 8080, Zlatni ISO Medijum i iVentoy PXE Distribucija"
date: 2026-09-28
status: accepted
tags:
  - architecture
  - truenas
  - docker
  - servishub
  - port-8080
  - golden-iso
  - iventoy
  - pxe
  - zero-touch
deciders:
  - Tehno-Kutak Core Team
author: agency-technical-writer
---

# ADR-0077: Produkciona Docker Implementacija ServisHub-a na TrueNAS-u, Fiksno Rutiranje na Portu 8080, Zlatni ISO Medijum i iVentoy PXE Distribucija

## 1. Kontekst i Problem

Tokom razvoja i testiranja zadataka GWADT-TASK-075 ([[decisions/ADR-0075-thin-client-launcher-and-dynamic-servishub-payload]]) i GWADT-TASK-076 ([[decisions/ADR-0076-decoupling-monolith-into-phase-scripts-ui-orchestrator]]), centralni telemetrijski server radionice (`hub_server.py`) funkcionisao je kao privremeni lokalni pozadinski proces na inženjerskoj radnoj stanici (Linux host `192.168.170.250`, PID `627492`). 

Ovakvo privremeno razvojno stanje stvorilo je kritična operativna uska grla za celokupan radnički pogon servisa:

### 1.1. Zavisnost Servisa od Stanja Radne Stanice
- Ukoliko se radna stanica ugasi, restartuje ili preoptereti kompajliranjem, čitava flota klijentskih računara u instalaciji gubi kontakt sa ServisHub-om (prekid UDP otkrivanja, prekid preuzimanja `gwadt-payload.zip`, izostanak arhiviranja servisnih kartona).
- Pravi produkcioni centar servisa mora raditi 24/7/365 na dediciranom skladišnom serveru — **TrueNAS SCALE** (`192.168.170.254`).

### 1.2. Razrešenje Kolizije Portova sa Servisnim ERP Sistemom (Port 8000 vs 8080)
- Na TrueNAS serveru (`192.168.170.254`) port **8000** je trajno rezervisan za interni radionički ERP softver (`servis-server-erp`).
- ServisHub je ranije podrazumevano ciljao port 8000 ili 8080 zavisno od argumenata, što je moglo izazvati fatalni `bind: address already in use` konflikt sa ERP-om ukoliko bi se pokrenuo na standardnom portu.
- Bilo je neophodno uspostaviti striktan, nepromenljiv standard fiksiranja ServisHub-a na **port 8080 (HTTP / SSE / REST)** i **port 8888 (UDP Zero-Config Discovery)**.

### 1.3. Potreba za Novim Zlatnim Butabilnim ISO Medijumom
- Prethodni ISO medijum na iVentoy PXE serveru (`192.168.170.254:26000`) i USB fleševima sadržao je zastarelu monolitnu skriptu `Install-ServisApps.ps1`, bez integrisanog `GWADT.Launcher.exe` thin-clienta, bez odvojenih 5 faza (`Phase1-5.ps1`) i sa starom konfiguracijom porta u `Wifi-Servis.ini`.
- Bilo je neophodno izgraditi novu hibridnu Zlatnu ISO sliku sa najnovijim binarnim modulima, sinhronizovati je na TrueNAS centralno skladište i osvežiti keš iVentoy PXE servera kako bi klijentske mašine mogle momentalno da butuju najnoviji sistem preko LAN mreže.

---

## 2. Razmatrane Alternative

| Kriterijum | Opcija A: ServisHub na Radnoj Stanici kao systemd servis | Opcija B: TrueNAS VM (KVM) sa virtuelizovanim Linuxom | Opcija C: Trajni Docker Servis na TrueNAS SCALE (`network_mode: host`) [PRIHVAĆENO] |
| :--- | :--- | :--- | :--- |
| **Dostupnost (24/7 Uptime)** | Loša: Radna stanica se gasi na kraju smene | Visoka, ali troši 2-4 GB namenskog RAM-a | **Maksimalna: TrueNAS radi neprekidno sa minimalnim resursima (<50 MB RAM)** |
| **Mrežni Režim & UDP Broadcast** | Lokalno ograničen | Bridge interfejs komplikuje UDP broadcast | **`network_mode: host` omogućava direktan prijem UDP 8888 na svim podmrežama** |
| **Skladište & ZFS Integracija** | Zahteva SMB montiranje sa stanice | Zahteva virtuelne diskove i NFS/SMB deljenja | **Direktni ZFS bind-mountovi (`/mnt/TK_NAS/Servis/`) bez mrežnog overhead-a** |
| **Konflikt Portova sa ERP-om** | Nema (na stanici), ali nije produkcija | Izolovan IP, ali komplikuje DNS/ruting | **Fiksno mapiran na port 8080 (HTTP) i 8888 (UDP), ERP ostaje netaknut na 8000** |
| **Održavanje i Nadogradnja** | Ručni git pull na lokalnoj mašini | Održavanje celog OS-a u VM-u | **Docker Compose: 1-komandni `docker compose restart` ili `build`** |

---

## 3. Doneta Odluka i Arhitektura Rešenja (GWADT-TASK-077)

Usvojena je stabilna produkciona arhitektura koja se sastoji od:
1. **Docker kontejnerizacije ServisHub-a na TrueNAS SCALE** (`192.168.170.254`).
2. **Fiksnog rutiranja na portovima 8080 (HTTP) i 8888 (UDP)** u `network_mode: host` režimu.
3. **Direktnih ZFS bind-mountova** sa skladišta `/mnt/TK_NAS/Servis/`.
4. **Izrade novog Zlatnog ISO medijuma** (11.76 GB) sa ugrađenim launcherom i 5 faza.
5. **Sinhronizacije na TrueNAS i osvežavanja iVentoy PXE servera** (port 26000).

```mermaid
flowchart TD
    subgraph TRUENAS["TrueNAS SCALE Server (192.168.170.254)"]
        subgraph DOCKER_INFRA["Docker Engine & Network Host"]
            SH["gwadt-servis-hub (Kontejner)<br/>Python 3.11-slim | PID: host<br/>HTTP :8080 | UDP :8888"]
            ERP["servis-server-erp (Kontejner)<br/>Port :8000 (Rezervisano za ERP)"]
            IV["iventoy (Kontejner: ziggyds/iventoy)<br/>Web UI :26000 | PXE DHCP/TFTP/NBD"]
        end

        subgraph ZFS_STORAGE["ZFS Pool: /mnt/TK_NAS/Servis/"]
            Z1["Skripte/ (Phases/, Modules/, ServisHub/) [ro mount]"]
            Z2["servis-hub/storage/ (cache, payload_meta.json) [rw mount]"]
            Z3["servis-hub/Logs/reports/ (Servisni kartoni .json/.md) [rw mount]"]
            Z4["Boot_ISO/01_Windows/GWADT-Win11-Pro-10.0.26200.8037.iso"]
        end
    end

    subgraph CLIENT_BOOT["Klijentski Računar (Novi Servisni Uređaj)"]
        CB1["1. PXE But preko mreže (iVentoy :26000)"]
        CB2["2. WinPE Setup: Brza dekompresija WIM-a & $OEM$ kopiranje na SSD"]
        CB3["3. FirstLogon: GWADT.Launcher.exe (~36 KB)"]
        CB4["4. UDP Discovery :8888 -> Povezivanje na ServisHub :8080"]
        CB5["5. Dinamički Payload: Preuzimanje gwadt-payload.zip"]
        CB6["6. Izvršavanje 5 Modularnih Faza preko GWADT.UI.exe"]
        CB7["7. Slanje završnog kartona na POST /api/devices/{id}/report"]
    end

    SH ---|bind-mount :ro| Z1
    SH ---|bind-mount :rw| Z2
    SH ---|bind-mount :rw| Z3
    IV ---|čita ISO| Z4

    CB1 -.->|TFTP/NBD| IV
    CB4 -.->|UDP :8888 broadcast| SH
    CB5 -.->|GET :8080/api/payload/download| SH
    CB7 -.->|POST :8080/api/devices/{id}/report| SH
```

### 3.1. TrueNAS Docker Kontejnerizacija (`gwadt-servis-hub`)
U direktorijumu `/mnt/TK_NAS/Servis/servis-hub/` na TrueNAS serveru postavljen je produkcioni Docker stack:
- **`Dockerfile`:**
  - Baza: `python:3.11-slim`.
  - Minimalna radna struktura: `/app/Skripte/ServisHub`, `/app/storage/cache`, `/app/Logs/reports`, `/app/Logs/live`.
  - Izvršna komanda:
    ```bash
    CMD ["python3", "Skripte/ServisHub/hub_server.py", "--host", "0.0.0.0", "--port", "8080", "--udp-port", "8888", "--root", "/app"]
    ```
- **`docker-compose.yml`:**
  ```yaml
  services:
    gwadt-servis-hub:
      container_name: gwadt-servis-hub
      build: .
      restart: unless-stopped
      network_mode: host
      environment:
        - PYTHONUNBUFFERED=1
      volumes:
        - /mnt/TK_NAS/Servis/Skripte:/app/Skripte:ro
        - /mnt/TK_NAS/Servis/servis-hub/storage:/app/storage:rw
        - /mnt/TK_NAS/Servis/servis-hub/Logs/reports:/app/Logs/reports:rw
  ```
- **Ključne bezbednosne i operativne prednosti:**
  - `Skripte:` montiran kao **read-only (`:ro`)**, što garantuje nemogućnost slučajne korupcije koda iz kontejnera.
  - `network_mode: host` eliminiše NAT/Bridge prepreke i omogućava da UDP auto-discovery responder na portu 8888 neposredno čuje broadcast pakete sa svih radioničkih podmreža (`192.168.170.0/24`, `192.168.180.0/24`, `192.168.88.0/24`).
  - `restart: unless-stopped` garantuje automatsko podizanje servisa pri restartu TrueNAS-a ili oporavku od nestanka struje.

### 3.2. Izgradnja Zlatnog Butabilnog ISO Medijuma
Na inženjerskoj radnoj stanici izvršeno je generisanje sveže Zlatne butabilne Windows 11 ISO slike pomoću `./napravi-iso.sh --force`:
- **Naziv artefakta:** `GWADT-Win11-Pro-10.0.26200.8037.iso`.
- **Lokalna putanja:** `/home/gru/Downloads/GWADT-Win11-Pro-10.0.26200.8037.iso`.
- **Veličina:** **11.76 GB** (12,625,389,568 bajtova).
- **Kontrolne sume:**
  - **MD5:** `b4ffeedc8f0e71c9ef15f8ddab1fd24e`
  - **SHA-256:** `8b5baf8dc992d4e35b3aaf9aa4b0d49dda35113bdc1194dda999bab689750cb0`
- **Specifikacija butovanja:**
  - Hibridni El Torito Boot katalog: UEFI (`0xEF`, `efi/microsoft/boot/efisys.bin`) i BIOS (`0x00`, `boot/etfsboot.com`).
  - UDF 1.02/2.00 ECMA-167 fajlsistem kreiran host alatom `genisoimage`.
- **Verifikovani sadržaj ISO strukture (`isoinfo`):**
  - Thin-Client Launcher: `/sources/$OEM$/$1/setup/GWADT.Launcher.exe` (~36 KB).
  - Svih 5 modularnih faza: `/sources/$OEM$/$1/setup/Skripte/Phases/Phase{1..5}-*.ps1`.
  - Deljeni modul: `/sources/$OEM$/$1/setup/Skripte/Modules/GWADT.Common.psm1`.
  - Centralna konfiguracija: `/Wifi-Servis.ini` i `/sources/$OEM$/$1/setup/Wifi-Servis.ini` sa konfigurisanom sekcijom `[ServisHub]` (`ServerHost=192.168.170.254`, `ServerPort=8080`, `Enabled=1`).

### 3.3. Mrežna Distribucija i Osvežavanje iVentoy PXE Servisa
1. **Prenos na TrueNAS skladište:**
   - Slika prebačena preko `rsync` na: `/mnt/TK_NAS/Servis/Boot_ISO/01_Windows/GWADT-Win11-Pro-10.0.26200.8037.iso`.
   - Potvrđen 100% paritet MD5 kontrolne sume na TrueNAS ZFS pulu: `b4ffeedc8f0e71c9ef15f8ddab1fd24e`.
2. **Osvežavanje iVentoy PXE servera:**
   - Izvršen kontrolisani restart iVentoy Docker kontejnera na TrueNAS-u (`ssh truenas "sudo docker restart iventoy"`).
   - iVentoy je ponovo skenirao repozitorijum, oslobodio prethodno keširane deskriptore blokova i registrovao novu Zlatnu sliku veličine 11.76 GB za mrežni PXE boot na svim klijentskim stolovima.
   - Web administrativni portal na `http://192.168.170.254:26000` potvrđen kao 100% operativan (JSON status: `running`).

---

## 4. Posledice

### 4.1. Pozitivne Posledice (Prednosti)
- 🏢 **Prava Radionička Produkcija (99.9%+ Uptime):** ServisHub više ne zavisi od lokalnog računara programera; trajno je aktivan na TrueNAS-u sa automatskim restartom.
- 🔀 **Stabilno Suživotno Rutiranje:** ERP sistem na portu 8000 i ServisHub na portu 8080 rade u potpunoj harmoniji bez međusobnog ometanja.
- 📡 **Univerzalni Auto-Discovery:** Zahvaljujući `network_mode: host` i UDP portu 8888, svaki klijentski računar čim dobije IP adresu automatski pronalazi ServisHub bez ručnog unosa IP-a.
- 💿 **Spreman Zlatni PXE i USB Medijum:** Nova ISO slika objedinjuje sve revolucionarne komponente iz zadataka 075 i 076 (`GWADT.Launcher.exe`, 5 faza, `GWADT.Common.psm1`, Flash & Release, Best-Effort aplikacije).
- 🔒 **Zaštita Podataka:** Read-only bind-mount sprečava bilo kakvu manipulaciju izvornim skriptama iznutra kontejnera, dok se servisni kartoni trajno čuvaju na ZFS-u.

### 4.2. Kompromisi i Održavanje
- **Održavanje Skripti na TrueNAS-u:** Izmene u fazama ili modulima na inženjerskoj stanici moraju biti sinhronizovane na `/mnt/TK_NAS/Servis/Skripte/` kako bi ServisHub `PayloadManager` mogao dinamički da ih spakuje klijentima.
  * *Ublažavanje:* Uspostavljena je `rsync` rutina u sklopu build procesa koja automatski ažurira TrueNAS skladište.

---

## 5. Verifikacija i QA Sertifikacija

Sistem je prošao rigorozno automatizovano testiranje kroz skriptu `Skripte/Test-Task077-QA.py`:
- **67/67 testova uspešno položeno (100% PASS)**:
  - **TEST 1 (ServisHub REST API & Payload):**
    - `GET /api/status` -> HTTP 200 OK, `status="ok"`, aktivan `uptime_sec`.
    - `GET /api/payload/version` -> HTTP 200 OK, validan timestamp i SHA-256 heš.
    - `GET /api/payload/download` -> HTTP 200 OK, preuzet binarni ZIP identične veličine i heša, potvrđeno prisustvo svih 5 faza i modula `GWADT.Common.psm1`.
    - `POST /api/devices/{id}/report` -> HTTP 200 OK, uspešan upis testnog kartona.
    - `GET /api/devices/{id}/report` -> HTTP 200 OK, verifikovan identičan karton sačuvan na ZFS skladištu.
  - **TEST 2 (UDP Auto-Discovery):**
    - Poslat `GWADT_DISCOVER client=QA-TEST-TASK077` na `192.168.170.254:8888`.
    - Primljen `GWADT_HUB_OFFER port=8080 name=TehnoKutak-Hub` u roku od 15 ms.
  - **TEST 3 (iVentoy PXE Server):**
    - `GET http://192.168.170.254:26000/` -> HTTP 200 OK.
    - `POST /iventoy/json` (`query_status`) -> `status="running"`.
    - `POST /iventoy/json` (`sysinfo`) -> `curdir="/app"`, verzija prepoznata.
  - **TEST 4 (Zlatni ISO Medijum):**
    - Lokalni ISO potvrđen: 11.76 GB, MD5 `b4ffeedc8f0e71c9ef15f8ddab1fd24e`.
    - TrueNAS remote ISO potvrđen: 11.76 GB, MD5 `b4ffeedc8f0e71c9ef15f8ddab1fd24e` (100% paritet).
    - `isoinfo` verifikacija: potvrđeno prisustvo `/sources/$OEM$/$1/setup/GWADT.Launcher.exe`, svih 5 faza i `Wifi-Servis.ini`.
  - **TEST 5 (Konfiguracioni Paritet):**
    - Potvrđen `[ServisHub] ServerPort=8080`, `ServerHost=192.168.170.254`, `Enabled=1` na svim instancama `Wifi-Servis.ini` u repozitorijumu.

---

## 🔗 Povezane Beleške
- Glavni Indeks: [[index]]
- Pregled Arhitekture: [[architecture/overview]]
- Ops Runbook: [[memory/ops-runbook]]
- Thin-Client Launcher i Dynamic Payload: [[decisions/ADR-0075-thin-client-launcher-and-dynamic-servishub-payload]]
- Dekompozicija u 5 Modularnih Faza: [[decisions/ADR-0076-decoupling-monolith-into-phase-scripts-ui-orchestrator]]
- Centralizovani Servisni Hub: [[decisions/ADR-0013-live-fleet-telemetry-servis-hub]]
- iVentoy PXE UDF Builder: [[decisions/ADR-0031-udf-hybrid-iso-engine-for-iventoy-pxe]]
