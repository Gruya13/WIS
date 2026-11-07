# ADR-0015: Nativno Ažuriranje i Sklapanje Windows 11 WIM Slike na Linux Radnoj Stanici

- **Status:** Prihvaćeno
- **Datum:** 2026-09-14
- **Autor:** GWADT Multi-Agent Sistem (`gwadt_orchestrator`, `gwadt_module_architect`, `gwadt_qa_debugger`, `gwadt_docs_sync`)
- **Kontekst:** Kompletno održavanje i osvežavanje GWADT instalacionog medijuma sa Linux radne stanice

---

## 1. Kontekst i Problem

U servisu "Tehno-Kutak", radna stanica servisera često koristi Linux operativni sistem.
Nakon uvođenja Windows DISM skripte (`Update-WimImage.ps1` u ADR-0014), postojala su dva operativna ograničenja za rad na Linuxu:
1. **Zavisnost od Windows OS-a:** CBS (Component-Based Servicing) engine za direktno ubacivanje `.msu` paketa u postojeći WIM fajl dostupan je isključivo unutar Windows kernela i DISM alata.
2. **Ručni rad:** Serviser je morao ručno proveravati da li je izašlo novo kumulativno ažuriranje, pronalaziti odgovarajuće KB brojeve na Microsoft Catalog-u i ručno ih skidati.

Postavljen je zahtev za **potpunom automatizacijom sa Linuxa**:
- Skripta treba jednim pokretanjem da očita verziju sa USB-a,
- Proveri online stanje bez ručnog unosa,
- Ispiše `UP TO DATE` ako nema novih ažuriranja,
- Ako postoji novija verzija, automatski preuzme sve delove i „zapeče” novu WIM sliku direktno na USB fleš disk.

---

## 2. Razmatrane Opcije

1. **Opcija A: Wine / KVM mikromašina:**
   - Pokretanje `dism.exe` unutar Wine-a ili QEMU virtuelne mašine.
   - *Nedostaci:* DISM ne funkcioniše pod Wine-om zbog specifičnih registry transakcija i drajvera montiranja. KVM zahteva Windows ISO, visoke resurse i kompleksno mapiranje USB diska.
2. **Opcija B: Nativni UUP Dump Pipeline sa Python WIM parserom (Izabrano rešenje):**
   - Samostalni Python 3 alat `Skripte/Update-WimImage-Linux.py` i lanser `osvezi-wim-linux.sh`.
   - Nativno binarno čitanje WIM zaglavlja (`sources/install.wim`) u čistom Pythonu (bez ikakvih zavisnosti).
   - Upit ka UUP Dump API-ju (`https://api.uupdump.net/listid.php`).
   - Sklapanje novog WIM-a korišćenjem Linux paketa otvorenog koda (`cabextract`, `wimlib-imagex`, `chntpw`, `genisoimage`, `aria2c`).
   - Automatski backup i zamena `sources/install.wim` na fleš disku.

---

## 3. Doneta Odluka

Usvojena je **Opcija B**.

### Tehnički tok izvršavanja:
1. **Očitavanje metapodataka:** Python čita WIM zaglavlje na offsetu `0x48`, dekodira UTF-16LE XML i pronalazi tačan Build i SPBuild broj za ediciju Windows 11 Pro (npr. `26200.8037`).
2. **API Upit:** Upit ka `api.uupdump.net` pronalazi najnoviji amd64 build za tu granu.
3. **Poređenje:**
   - Ako je `lokalni >= online`: Prikazuje se zeleni baner `[UP TO DATE]` i proces se završava bez ikakvog bespotrebnog skidanja.
   - Ako je `online > lokalni`: Prikazuje se žuti baner sa razlikom u verzijama.
4. **Provera zavisnosti:** Skripta proverava prisustvo potrebnih CLI alata (`aria2c`, `wimlib-imagex`, `cabextract`, `chntpw`, `genisoimage`). Ako neki nedostaje, ispisuje tačnu `apt` komandu.
5. **Generisanje i zamena:** Preuzima konverzioni paket sa uupdump.net, pokreće višenitno preuzimanje preko `aria2c`, sklapa novi `install.wim` i vrši atomičnu zamenu na USB flešu uz čuvanje `.bak` sigurnosne kopije.

---

## 4. Posledice i Dobici

- **Potpuna nezavisnost od Windowsa:** Serviser na Linuxu može jednim klikom/komandom da osveži celokupan fleš disk.
- **Nema ručnog pretraživanja:** UUP API sam rešava najnovije kumulativne zakrpe, .NET ažuriranja i Servicing Stack.
- **Maksimalna brzina i bezbednost:** Ako nema novih verzija, provera traje svega 2 sekunde i ne troši disk prostor ni protok.
- **Očuvanje postojećeg GWADT sistema:** `autounattend.xml`, `FirstLogon.cmd` i svi PowerShell moduli u `Skripte/Modules/` ostaju netaknuti.

---

## 5. Povezane Beleške
- [[operations/media-maintenance]]
- [[decisions/ADR-0014-offline-wim-slipstreaming]]
- [[architecture/modules]]
- [[index]]
