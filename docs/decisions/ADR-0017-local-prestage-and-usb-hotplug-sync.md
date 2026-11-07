# ADR-0017: Lokalni Pre-Stage Keš i USB Hotplug Automatski Sinhronizator na Linuxu

- **Status:** Prihvaćeno
- **Datum:** 2026-09-15
- **Autor:** GWADT Multi-Agent Sistem (`gwadt_orchestrator`, `gwadt_module_architect`, `gwadt_ui_architect`, `gwadt_qa_debugger`, `gwadt_docs_sync`)
- **Kontekst:** Asinhrona priprema WIM slike na računaru i trenutno ažuriranje pri umetanju USB fleša

---

## 1. Kontekst i Problem

Nakon uvođenja Direct Microsoft Refresh Pipeline-a (ADR-0016), proces osvežavanja na Linuxu je ubrzan na svega par minuta. Međutim, operativni proces u servisu "WIS Service" imao je još jedno usko grlo:
- Dok se vrši preuzimanje, montiranje ISO-a, izdvajanje Pro edicije i primena maksimalne LZX Solid kompresije, **USB fleš disk je morao biti fizički prisutan u radnoj stanici**.
- Serviseri su često na terenu, ili je fleš disk u tom trenutku angažovan na računaru klijenta gde se instalira sistem.
- Kada serviser donese fleš u servis, nije želeo da čeka ni nekoliko minuta na pripremu slike, već je želeo **trenutno (instant) osvežavanje**.

Postavljen je zahtev: **Odvojiti fazu teške pripreme slike (CPU/Disk zahtevan posao) od faze upisa na fleš memoriju (I/O transfer)**.

---

## 2. Doneta Odluka

Usvojena je dvofazna arhitektura: **Lokalni Pre-Stage Keš + USB Hotplug Watcher**.

### Arhitektura rešenja:

```
+-------------------------------------------------------------------------------+
|                       FAZA 1: LOKALNI PRE-STAGE KEŠ                           |
|       (Izvodi se na računaru unapred, USB fleš NIJE potreban!)                |
+-------------------------------------------------------------------------------+
       |
       +--> Microsoft Refresh ISO / ESD / URL
       +--> wimlib-imagex export (Windows 11 Pro, Maximum Solid LZX)
       +--> Radni folder: /home/gru/Storage/gwadt_staging/
       |       - install.wim (~4.3 GB, fabrički najnoviji build)
       |       - staging_meta.json (build, spbuild, timestamp, hash)
       v
+-------------------------------------------------------------------------------+
|                       FAZA 2: USB HOTPLUG WATCHER                             |
|          (Serviser ubacuje USB fleš disk u bilo koji port)                    |
+-------------------------------------------------------------------------------+
       |
       +--> Skripta detektuje montiranje USB fleša (/run/media/gru/*)
       +--> Očitava verziju na flešu vs verziju u lokalnom kešu
       +--> QUICK-SYNC: Prenos 4.3 GB WIM-a u blokovima od 16 MB sa TUI progresom
       +--> sync (pražnjenje diskovnih bafera)
       +--> Audio notifikacija (terminal bell) + zeleni status
       v
+-------------------------------------------------------------------------------+
|         GOTOVO ZA ~60 SEKUNDI! FLEŠ JE AŽURIRAN I SPREMAN ZA RAD!            |
+-------------------------------------------------------------------------------+
```

---

## 3. Komandni Interfejs i Režimi Rada

1. **Pre-stage priprema na računaru (bez fleša):**
   ```bash
   # Priprema iz lokalnog ISO-a:
   ./osvezi-wim-linux.sh --prepare --iso Win11.iso

   # Ili preuzimanje direktnim linkom sa Microsofta i priprema:
   ./osvezi-wim-linux.sh --prepare --download-url "https://software-download.prss.microsoft.com/..."
   ```

2. **Hotplug Watcher (čekanje na fleš):**
   ```bash
   ./osvezi-wim-linux.sh --watch
   ```
   Skripta prelazi u pasivno praćenje portova. Onog trenutka kada serviser ubode fleš, skripta ga sama prepoznaje, izvršava Quick-Sync prenos, oglašava se zvučnim signalom i ispisuje potvrdu da fleš može bezbedno da se izvadi.

3. **Trenutni sinhronizator ubačenog fleša:**
   ```bash
   ./osvezi-wim-linux.sh --sync-only
   ```

4. **Interaktivni pametni meni:**
   Ako se `./osvezi-wim-linux.sh` pokrene bez argumenata, sama detektuje stanje:
   - Ako fleš nije ubačen, prikazuje status pripremljenog keša na računaru i nudi pokretanje Watcher-a ili pripremu nove slike.
   - Ako je fleš ubačen, upoređuje verzije i ako na računaru postoji noviji WIM, pita servisera: `Želite li brzi prenos na fleš (~60s)? [D/n]`.

---

## 4. Posledice i Dobici

- **Ušteda vremena servisera na terenu:** Vreme provedeno uz fleš disk je svedeno na samo **oko 60 sekundi** (brzina sekvencijalnog upisa na USB 3.0 fleš).
- **Potpuno autonomna priprema:** Radna stanica može noću ili u pozadini tokom radnog vremena pripremiti najnoviji build čim Microsoft objavi osveženje.
- **Zvučna notifikacija:** Serviser ne mora gledati u ekran; zvučni signal ga obaveštava kada je fleš spreman i bafer bezbedno ispražnjen (`sync`).
- **Nula zavisnosti od Windows OS-a:** Celokupan pipeline radi 100% nativno na Linuxu.

---

## 5. Povezane Beleške

- [[operations/media-maintenance]]
- [[memory/gotchas]]
- [[decisions/ADR-0016-direct-microsoft-refresh-pipeline]]
- [[index]]
