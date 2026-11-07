# ADR-0013: Centralizovani Servisni Hub, Live Telemetrija i Daljinsko Praćenje Flote

- **Status:** Prihvaćeno
- **Datum:** 2026-09-14
- **Autor:** GWADT Multi-Agent Sistem (`gwadt_orchestrator`, `gwadt_powershell_expert`, `gwadt_ui_architect`, `gwadt_module_architect`, `gwadt_qa_debugger`, `gwadt_docs_sync`)
- **Kontekst:** Servisna radionica "WIS Service", paralelna instalacija više Windows 11 uređaja istovremeno

---

## 1. Kontekst i Problem

Tokom svakodnevnog rada u servisu računara, tehničar često istovremeno priprema i instalira više računara na radnom stolu (npr. 2 do 5 laptopova ili desktop mašina).

Pre ove arhitektonske odluke:
1. **Fizička provera:** Serviser je morao periodično da ustaje i prilazi svakom pojedinačnom ekranu kako bi utvrdio:
   - U kojoj je fazi instalacija (Faza 1 Mreža, Faza 2 Windows Update krugovi, Faza 4 Programi).
   - Koliki je procenat preuzimanja ili ugradnje drajvera.
   - Da li je računar zatražio priključenje LAN mrežnog kabla (zbog nedostatka Wi-Fi drajvera).
   - Da li se desila greška ili blokada (npr. MSI lock, neuspešna aktivacija, nedostatak instalacionog paketa).
2. **Naknadna analiza logova:** Kompletan izveštaj i logovi (`servis_deploy.log`, `.jsonl`) bili su dostupni tek po završetku rada i prenosu sa USB medijuma na radnu stanicu servisera.

Postavljen je zahtev da se omogući **praćenje instalacija i čitanje logova uživo sa serviserskog Linux računara** za sve aktivne klijentske uređaje u realnom vremenu.

---

## 2. Razmatrane Opcije

1. **Opcija A: Deljeni mrežni folder (Samba / SMB Share):**
   - Windows klijent mapira `\\linux-host\servis_logs` i upisuje direktno u fajl.
   - *Nedostaci:* Windows 11 24H2 podrazumevano blokira neautentifikovane (Guest) SMB sesije i zahteva SMB signing. Konkurentno upisivanje preko nepouzdanog Wi-Fi linka može lockovati proces ili baciti terminirajuću I/O grešku na klijentu.
2. **Opcija B: Nativni Syslog (UDP streaming):**
   - Klijent šalje UDP pakete sa svakom linijom loga na syslog port.
   - *Nedostaci:* UDP nema potvrdu prijema, ne pruža strukturirano stanje uređaja (hardware spec, faza, progres radnika u procentima) i zahteva dodatni parser na serveru.
3. **Opcija C: Hibridni HTTP REST + UDP Auto-Discovery + SSE Live Streaming (Izabrano rešenje):**
   - Lagani višenitni Python 3 servis na serviserskom računaru bez eksternih biblioteka (`http.server`, `threading`, `socket`).
   - UDP Auto-Discovery na portu 8888 za "Zero-Config" povezivanje.
   - HTTP REST API sa strogim 700ms non-blocking timeoutom na klijentu.
   - Ugrađeni responzivni Dark-Mode Web Dashboard sa SSE (Server-Sent Events) live log streamingom (`tail -f`).

---

## 3. Doneta Odluka

Implementiran je **GWADT Centralni Servisni Hub i Telemetrijski Sistem**:

### A. Centralni Servisni Hub (Linux / Radna stanica servisera):
- **Skripta:** `Skripte/ServisHub/hub_server.py` (i pokretač `start-servis-hub.sh`).
- **REST API Endpoints:**
  - `POST /api/heartbeat`: Prijem telemetrijskog pulsa (hardver, serijski broj, CPU/RAM/baterija, faza 1–5, naziv faze, ciklus ažuriranja, live status, procenat radnika, mapa aplikacija).
  - `POST /api/log`: Prijem novih linija loga i automatsko upisivanje u lokalni arhivski fajl `Logs/live/servis_deploy_<Machine>_<Date>.log`.
  - `GET /api/devices`: Lista svih detektovanih uređaja sa automatskim proračunom statusa (`Aktivno`, `Restart u toku`, `Završeno`, `Upozorenje`, `Offline`).
  - `GET /api/devices/<id>/logs`: Preuzimanje poslednjih 300 linija loga (sa fallback čitanjem sa diska).
  - `GET /api/stream/<id>`: Server-Sent Events (SSE) live streaming konekcija za browser konzolu.
  - `GET /`: Ugrađeni Web Dashboard (HTML5/CSS3/Vanilla JS) sa karticama uređaja, grafičkim indikatorima faza i dugmetom za otvaranje modalnog terminala.
- **Terminal TUI Monitor:** `Skripte/ServisHub/hub_cli.py` za praćenje flote i live stream direktno u Linux konzoli.
- **UDP Auto-Discovery Responder:** Osluškivač na portu 8888 koji odgovara na `GWADT_DISCOVER` upit klijenta, čime je eliminisana potreba za ručnim unosom IP adrese.

### B. Klijentski Telemetrijski Modul (Windows 11 / PowerShell 5.1):
- **Modul:** `Skripte/Modules/GWADT.Telemetry.psm1`.
- **Ključne funkcije:**
  - `Find-ServisHub`: Očitava konfiguraciju iz `Wifi-Servis.ini` (`ServerHost=auto,192.168.88.50,192.168.180.50,192.168.170.188`).
    * **Multi-Subnet podrška (LAN i WLAN):** Slanje usmerenog UDP broadcasta na svaku detektovanu lokalnu podmrežu klijenta (`192.168.88.255`, `192.168.180.255`), uz paralelno brzo HTTP sondiranje definisanih adresa na svakom subnetu i default gateway-a oba adaptera.
    * **Dinamičko prevezivanje (Failover):** Ukoliko računar tokom rada promeni mrežu (npr. vađenje LAN kabla i prelazak na Wi-Fi), nakon dva neuspela slanja automatski se forsira `Find-ServisHub -ForceRefresh` koji preusmerava saobraćaj na novu aktivnu podmrežu bez gubitka podataka.
  - `Send-ServisHeartbeat`: Priprema sažetak stanja (uključujući parametre veze: LAN kabl, Wi-Fi SSID, Dual-Homing) i šalje ga serveru putem `.NET HttpWebRequest` sa strogim timeoutom od 700ms.
  - `Push-ServisLogToHub` & `Flush-ServisLogBuffer`: Baferuje linije loga i šalje ih u grupama (batch) na `/api/log` čim se nakupi 5 linija ili proteknu 2 sekunde.
- **Fail-Safe Pravilo:** Sve operacije se izvršavaju u `try/catch` blokovima. Ukoliko server nije pokrenut ili mreža privremeno padne, **instalacija na klijentu se nastavlja 100% normalno bez i najmanjeg zastoja ili greške**.

---

## 4. Posledice

### Pozitivne:
- **Centralni nadzor iz fotelje:** Serviser sa jednog ekrana vidi sve mašine u servisu, njihov progres i status softvera.
- **Automatska podrška za odvojene LAN i WLAN podmreže:** Bilo da je računar priključen na gigabitni LAN (`192.168.88.x`), bežični Wi-Fi (`192.168.180.x`), ili koristi Dual-Homing, telemetrija radi potpuno autonomno jer Linux server ima dodeljene IP adrese u svim podmrežama servisa (`192.168.88.50`, `192.168.180.50`).
- **Uživo uvid u problem:** Ukoliko instalacija stane ili prijavi grešku, serviser u Web ili TUI konzoli odmah vidi tačnu liniju loga.
- **Automatska arhiva na Linux disku:** Svi logovi se trajno čuvaju u `Logs/live/` čim instalacija krene, eliminišući potrebu za ručnim kopiranjem logova sa USB-a.
- **Nula spoljnih zavisnosti:** Server koristi isključivo Python standardnu biblioteku; klijent koristi ugrađene .NET klase u PowerShell 5.1.
