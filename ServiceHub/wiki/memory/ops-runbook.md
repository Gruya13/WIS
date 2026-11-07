---
title: "Operativni Runbook za ServisHub (Ops Runbook)"
type: memory
created: 2026-10-08
updated: 2026-10-10
tags:
  - ops
  - runbook
  - troubleshooting
  - docker
  - truenas
  - deployment
---

# Operativni Runbook za ServisHub (Ops Runbook)

Ovaj vodič sadrži konkretne komande i procedure za administraciju, deployment, održavanje i rešavanje incidenata na **ServisHub** serveru u radionici "WIS Service".

---

## 🚀 1. Pokretanje i Upravljanje Servisom

### A. Lokalno Pokretanje (Razvoj i Testiranje):
```bash
# Pokretanje sa podrazumevanim parametrima (Port 8080, UDP 8888)
./start-servis-hub.sh

# Pokretanje na specifičnom portu
python3 hub_server.py --port 8080 --udp-port 8888

# Pokretanje sa izričitim folderom za skladište
python3 hub_server.py --storage-dir /putanja/do/storage
```

---

## 🐳 2. Produkcijski Deployment Pipeline (TrueNAS SCALE)

Produkcijsko okruženje se nalazi na centralnom radioničkom serveru **TrueNAS SCALE** (`192.168.170.254`). ServisHub se izvršava unutar Docker kontejnera sa `network_mode: host` radi direktnog prijema UDP broadcast paketa na portu 8888.

### A. Standardna Procedura Sinhronizacije i Rebuilda (Sync & Deploy)

Izvršava se sa razvojne radne stanice u dva koraka:

#### Korak 1: RSync Sinhronizacija Koda
Sinhronizacija koda sa dev mašine na TrueNAS NFS/SMB deljeni direktorijum (`/mnt/TK_NAS/Servis/servis-hub` montiran lokalno kao `/home/gru/Storage/servis-hub/`), uz strogo ignorisanje repozitorijuma, privremenih fajlova i radnih podataka (`storage/` i `Logs/`):

```bash
rsync -av --exclude=".git" --exclude=".venv" --exclude=".pytest_cache" --exclude="__pycache__" --exclude="*.pyc" --exclude="Logs/" --exclude="storage/" "/home/gru/Projects/Servis Hub/" "/home/gru/Storage/servis-hub/"
```

#### Korak 2: Remote Rebuild i Pokretanje na TrueNAS Serveru
Pokretanje čistog Docker Compose rebuild-a bez keša (`--no-cache`) i forsirano podizanje nove instance servisa:

```bash
ssh truenas "cd /mnt/TK_NAS/Servis/servis-hub && sudo docker compose build --no-cache && sudo docker compose up -d --force-recreate"
```

### B. Svakodnevno Upravljanje Docker Kontejnerom na TrueNAS-u
Komande koje se izvršavaju direktno na TrueNAS-u (ili preko SSH sesije):

```bash
# Provera statusa kontejnera
docker ps | grep servis-hub

# Pregled logova servera uživo (streaming)
docker logs -f servis-hub

# Restart kontejnera bez rebuilda
docker restart servis-hub

# Ručno zaustavljanje i uklanjanje
cd /mnt/TK_NAS/Servis/servis-hub && sudo docker compose down
```

---

## 📊 3. Nadzor Flote i Verifikacija

### A. Terminal Monitor (`hub_cli.py`):
```bash
# Prikaz tabele svih povezanih uređaja
python3 hub_cli.py

# Uživo praćenje loga mašine (tail -f)
python3 hub_cli.py --watch TK-LAPTOP-PF2X98ZA
```

### B. Brza HTTP Provera (Health Check & Telemetrija):
```bash
# 1. Provera rada i zdravlja servera (odziv < 7ms)
curl -s http://192.168.170.254:8080/api/health

# 2. Provera liste registrovanih uređaja (93 aktivna uređaja)
curl -s http://192.168.170.254:8080/api/devices | jq .

# 3. Provera keširanih drajvera (589 drajvera u kešu)
curl -s http://192.168.170.254:8080/api/drivers/cache/list | jq .

# 4. Provera dostupnih Windows 11 zakrpi
curl -s http://192.168.170.254:8080/api/updates/status | jq .
```

---

## 📋 4. Dnevnik Produkcijskih Izdanja (Deployment History)

### 🏷️ Izdanje ServisHub v3.1.0 (2026-10-10)
- **Status:** 🟢 Uspešno pušteno u produkciju (UP & OPERATIONAL)
- **Ciljni Host:** TrueNAS SCALE (`192.168.170.254`)
- **Portovi:** Port 8080 (REST API & Dashboard), Port 8888 (UDP Auto-Discovery)
- **Konfiguracija:** Docker Compose (`network_mode: host`, ZFS Volume Mounts)
- **Procedura deploy-a:** Izvršen rsync (exclude `.git`, `Logs/`, `storage/`) + `ssh truenas "docker compose build --no-cache && docker compose up -d --force-recreate"`.
- **Rezultati Verifikacije Produkcije:**
  - **Zdravlje Servisa (`/api/health`):** `status: "ok"`, verifikovan stabilan rad višenitnog servera.
  - **Mrežna Latencija:** Prosečno vreme HTTP odziva **< 7ms** na internoj radioničkoj gigabitnoj mreži.
  - **Registrovana Flota (`/api/devices`):** **93 uređaja** uspešno očuvano i prepoznato u stanju sistema.
  - **Lokalni Keš Drajvera (`/api/drivers/cache/list`):** **589 drajvera** indeksirano i spremno za offline klijente.
  - **Web Dashboard:** Modern Enterprise Dark Mode Cockpit (Google Stitch v2, Collapsible Sidebar, 1 Hz ogledalo ekrana) kompletno funkcionalan na `http://192.168.170.254:8080/`.

---

## 🔧 5. Otklanjanje Najčešćih Problema (Troubleshooting)

### Problem 1: Klijenti ne vide ServisHub (Offline u Launcheru)
1. Proveriti da li je server pokrenut i da li osluškuje na portovima:
   ```bash
   ss -tulpn | grep -E '8080|8888'
   ```
2. Ako je na TrueNAS-u, proveriti da li je `network_mode: host` aktivan u `docker-compose.yml`.
3. Poslati test UDP paket:
   ```bash
   echo -n "GWADT_DISCOVER client=TEST" | nc -u -w1 127.0.0.1 8888
   ```

### Problem 2: Čišćenje Keša Drajvera ili Payload Paketa
Ako je potrebno osloboditi prostor ili forsirati novo preuzimanje:
```bash
# Brisanje starog payload paketa
rm -rf storage/cache/gwadt-payload.zip storage/cache/payload_meta.json

# Brisanje keširanih drajvera
rm -rf storage/drivers_cache/*.cab storage/drivers_cache/index.json
```

---

## 📚 Povezane Beleške
- Glavni indeks: [[index]]
- Arhitektura sistema: [[architecture/overview]] i [[architecture/servis-hub]]
- Registar zamki: [[memory/gotchas]]
- Arhitektonska odluka o Cockpitu: [[decisions/ADR-0005-enterprise-dark-mode-cockpit-redesign]]
- Arhitektonska odluka o No-Mock i Stitch mapi: [[decisions/ADR-0006-stitch-design-application-and-no-mock-roadmap]]
