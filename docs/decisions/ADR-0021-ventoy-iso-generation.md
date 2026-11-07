---
title: "ADR-0021: Generisanje hibridne bootable ISO slike sa No-Prompt podrškom za Ventoy"
type: decision
status: accepted
date: 2026-09-17
updated: 2026-09-18
author: gwadt_docs_sync
tags:
  - decision
  - iso
  - ventoy
  - bootable
  - uefi
  - bios
  - el-torito
  - udf
  - xorriso
  - staging
  - usb-tool
---

# ADR-0021: Generisanje hibridne bootable ISO slike sa No-Prompt podrškom za Ventoy

## 1. Kontekst i Problem

U modernom servisnom radu, standard za višenamenske servisne medijume je **Ventoy** — rešenje koje omogućava držanje više različitih operativnih sistema i dijagnostičkih alata na jednom velikom USB disku (obično 128 GB, 256 GB ili eksterni SSD). 

Iako je GWADT inicijalno razvijen za rad na direktno formatiranom FAT32/NTFS fleš disku, pojava potrebe za skladištenjem GWADT-a unutar Ventoy multiboot okruženja u formi ISO datoteke nametnula je sledeće tehničke izazove:

1. **Specifičnost Ventoy loader-a i gubitak montiranog diska nakon restarta:**
   - Ventoy podiže ISO sliku kreiranjem virtuelnog diska u memoriji ili preko loopback drajvera u WinPE fazi.
   - Nakon što se završi prva faza (WinPE Pass 1), računar se restartuje u novoinstalirani Windows na klijentskom disku (NVMe/SSD).
   - U tom trenutku virtuelni ISO disk **više ne postoji** u operativnom sistemu jer Ventoy drajver nije trajno integrisan u klijentski Windows.
   - Ukoliko bi se instalacija programa i alata oslanjala na čitanje sa virtuelnog CD/DVD drajva (npr. `D:\Programi`), post-install pajplajn (`FirstLogon.cmd` i `Install-ServisApps.ps1`) bi doživeo potpuni krah.
2. **Problem čekanja na taster ("Press any key to boot from CD or DVD..."):**
   - Standardne Windows instalacione ISO slike u El Torito katalogu za BIOS sadrže `boot/bootfix.bin`, dok za UEFI koriste `efisys.bin`.
   - Ovi moduli prikazuju čuvenu poruku *"Press any key to boot from CD or DVD..."* sa tajmautom od samo 5 sekundi.
   - U serijskoj instalaciji u servisu, gde serviser paralelno pokreće više računara, propuštanje ovog prompta dovodi do neuspelog butovanja i zahteva ponovni restart. Za potpuno autonomni (Zero-Touch) servisni rad neophodan je automatski start bez ikakvog dodira tastature.
3. **Problem eksplozije veličine ISO slike (duplikati i sirovi paketi):**
   - GWADT repozitorijum i pripremljeni fleš disk sadrže korenski `Programi/` folder (~18 GB) koji služi kao rezerva i arhiva, dok se istovremeno unutar `sources/$OEM$/$1/setup` već nalazi optimizovan set instalacionih paketa koji Windows Setup automatski kopira na `C:\setup`.
   - Pored toga, u radnom direktorijumu se nalazi i folder `Zakrpe/` sa sirovim `.msu` i `.cab` paketima koji su već integrisani u `sources/install.wim`.
   - Ako bi se nekritički preslikao celokupan repozitorijum u ISO, slika bi premašila 35+ GB, što bi bilo nepraktično za skladištenje na Ventoy flešu i izuzetno sporo za kreiranje.
4. **Nedostatak UEFI podrške i sakaćenje naziva u starim alatima (`genisoimage` problem):**
   - Tokom ranog testiranja, oslanjanje na nasleđene Linux alate poput `genisoimage` (iz `cdrkit` paketa) pokazalo je fundamentalne defekte: `genisoimage` ne podržava UEFI El Torito specifikaciju (ne upisuje Platform ID `0xEF` za EFI), već sve particije tretira isključivo kao x86 BIOS (`0x00`).
   - Pored toga, `genisoimage` sakati nazive modernih Windows fajlova na 8.3 format. Kao posledica toga, Ventoy i savremene UEFI ploče uopšte ne vide UEFI bootloader i butovanje doživljava krah.

---

## 2. Doneta Odluka

Odlučeno je da se implementira namenski, optimizovani i automatizovani sistem za generisanje **hibridne bootable ISO slike** sa punom podrškom za Ventoy, kroz sledeće inženjerske mehanizme:

### 1. Hard-Link Staging Stablo (Zero-Disk-Overhead & Trenutna Priprema)
Umesto sporog fizičkog kopiranja 12+ GB fajlova u privremeni direktorijum, generator koristi POSIX **hard-linkove** (`os.link` u Pythonu):
- Celokupno staging stablo se formira za **manje od 1 sekunde**.
- Zauzeće dodatnog prostora na disku je **tačno 0 bajtova** jer hard-linkovi dele postojeće inod-e na fajl sistemu.
- Generator rigorozno filtrira elemente:
  - **Uključuje:** `sources/` (sa `install.wim`, `ei.cfg`, `$OEM$`), `boot/`, `efi/`, `support/`, `Za disk 10+ gen/` (VMD drajveri), `autounattend.xml`, `FirstLogon.cmd`, `autorun.inf`, `version.json` i prateće skripte.
  - **Striktno isključuje:** Dupli korenski `Programi/` folder, sirovi folder `Zakrpe/`, `.git`, `.agents`, `second_brain`, `wiki/`, `USB Tool/`, servisne logove i privremene datoteke.

### 2. Optimizovan Obim (~12 GB) i Post-Reboot Autonomija
- Zahvaljujući oslanjanju na Windows Setup nativni mehanizam `sources/$OEM$/$1/setup/`, Windows Setup tokom prve faze (WinPE) **samostalno kopira celokupan sadržaj na klijentski `C:\setup`**.
- Kada se računar restartuje u klijentski Windows i Ventoy virtuelni ISO nestane, svi instalacioni paketi, skripte i moduli se već nalaze na lokalnom SSD-u.
- Time je garantovana 100% autonomija post-install ciklusa uz minimalnu veličinu ISO slike od samo ~12 GB (umesto 35+ GB).

### 3. Hibridni El Torito Boot Katalog (No-Prompt Podrška)
Generator konfiguriše dvostruki hibridni boot zapis koji podržava i starije Legacy BIOS mašine i savremene UEFI platforme:
1. **Legacy BIOS Boot:**
   - Koristi se `boot/etfsboot.com` kao primarni boot image (sektori od 4 bloka po 512 bajtova = 2048 bajtova, load size 8).
   - U No-Prompt režimu, generator privremeno **uklanja fajl `boot/bootfix.bin`** iz staging stabla. Time `etfsboot.com` ne nalazi prompt modul i trenutno pokreće Windows Setup bez pitanja.
2. **UEFI Boot:**
   - Koristi se `efi/microsoft/boot/efisys_noprompt.bin` kao El Torito UEFI boot sektor (`-eltorito-alt-boot -e ... -no-emul-boot`).
   - Ukoliko korisnik eksplicitno zatraži standardni prompt, bira se `efisys.bin`.

### 4. Trajna Diskvalifikacija Alata `genisoimage` i Prelazak na Hibridni `xorriso` Motor
- **Trajna zabrana alata `genisoimage` i `mkisofs`:**
  * Ovi nasleđeni alati su **trajno diskvalifikovani i zabranjeni** u GWADT sistemu. Svaki pokušaj njihovog korišćenja rezultira momentalnim prekidom i bacanjem `ValueError`/`RuntimeError` sa detaljnim obrazloženjem.
  * Razlozi: nedostatak podrške za UEFI El Torito specifikaciju (Platform ID `0xEF`) i sakaćenje naziva datoteka na 8.3 format.
- **Uvođenje Hibridnog `xorriso` motora:**
  * `xorriso` je jedini ovlašćeni i sertifikovani ISO builder alat u GWADT-u. Podržava ISO-9660:1999 relaxed, Joliet Level 3, Rock Ridge v1, UDF i punu El Torito multi-boot specifikaciju.
  * Motor podržava tri režima rada (`--engine auto|host|docker`):
    1. `host_xorriso`: Nativno izvršavanje preko lokalnog `xorriso` binarnog alata na host sistemu.
    2. `docker_xorriso`: Automatski fallback na Docker kontejner ukoliko `xorriso` nije instaliran na hostu. Sistem automatski kreira namensku mikro-sliku `gwadt-iso-builder:latest` (Alpine Linux + xorriso, svega ~10 MB, priprema se za ~3s) i pokreće izgradnju u izolovanom kontejneru sa bind mount staging direktorijumom i očuvanim korisničkim UID/GID pravima.
    3. `auto`: Podrazumevani inteligentni režim koji automatski bira `host_xorriso` ako je dostupan, a u suprotnom transparentno prelazi na `docker_xorriso`.

### 5. Obavezna Post-Build Verifikacija (`audit_boot_catalog`)
- Nijedna generisana ISO slika se ne proglašava uspešnom bez automatske El Torito binarne revizije.
- Funkcija `audit_boot_catalog(iso_path)` čita El Torito Volume Descriptor na ofsetu `0x8800` (sektor 17) i Boot Catalog na ciljnom LBA sektoru:
  * Potvrđuje Validation Entry (`0x01`, `0x00`, `0x55AA`).
  * Potvrđuje Default/Initial Entry za Legacy BIOS boot (`0x00`).
  * Skenira sekcije kataloga i **garantuje postojanje Platform ID `0xEF` (UEFI boot sektor)**.
- **Safety Gate:** Ukoliko zapis `0xEF` izostane, ISO fajl se **automatski momentalno briše sa diska**, sprečavajući distribuciju neispravnih medijuma na servise.

### 6. Dvoslojni Interfejs: CLI + Web Dashboard
1. **Nativni CLI interfejs (`napravi-iso.sh` / `Skripte/Build-Iso.py`):**
   - Brza komandnolinijska izrada sa parametrima `--output`, `--no-prompt`, `--dry-run`, `--force`, `--engine auto|host|docker`.
   - Automatski formatira naziv izlaznog fajla na osnovu `version.json` (npr. `GWADT-Win11-Pro-10.0.26200.8037.iso`).
2. **Web Dashboard integracija (`USB Tool` na portu 8092):**
   - Poseban tab u GUI-ju: "Ventoy ISO Generator".
   - Asinhroni pozadinski menadžer (`IsoBuildManager`) sa praćenjem progresa u realnom vremenu (0-100%).
   - Live konzola logova, zvučni signal po završetku (Web Audio API) i automatsko generisanje SHA-256 i MD5 kontrolnih suma sa mogućnošću kopiranja putanje jednim klikom.

---

## 3. Posledice Odluke

### Pozitivne posledice:
1. **Puna Ventoy i UEFI Kompatibilnost:** Zahvaljujući garantovanom El Torito `0xEF` UEFI zapisu generisanom preko `xorriso`, ISO slika se besprekorno butuje na svim modernim UEFI SecureBoot platformama i Ventoy multiboot medijumima.
2. **Nula Dodira Tastature (True Zero-Touch):** Zahvaljujući No-Prompt El Torito strukturi, računar automatski započinje instalaciju bez zastoja na promptu.
3. **Ekstremna Efikasnost Resursa:** Korišćenje hard-linkova eliminiše potrebu za slobodnim prostorom od 12 GB pre samog kreiranja ISO-a i ubrzava pripremu na delić sekunde.
4. **Optimalna Veličina Medijuma:** Izbacivanjem duplikata i sirovih zakrpa, ISO zauzima ~8 GB (u zavisnosti od WIM kompresije), ostavljajući maksimalan prostor na Ventoy disku za druge alate.
5. **Autonomija i Portabilnost (Docker Fallback):** Zahvaljujući `docker_xorriso` mehanizmu, ISO se može pouzdano generisati na bilo kojoj Linux mašini koja ima Docker, čak i ako host nema instaliran paket `xorriso`.
6. **Nemogućnost Distribucije Defektne Slike:** Obavezni `audit_boot_catalog` štiti servisere od gubljenja vremena na neispravne ISO slike na terenu.

### Kompromisi:
- Zahteva `xorriso` na host sistemu ili aktivan Docker servis (`docker` daemon) za fallback.
- `genisoimage` i `mkisofs` više nisu podržani ni u kakvom obliku.

---

## 4. Povezane Beleške
- Početni indeks: [[index]]
- Pregled arhitekture: [[architecture/overview]]
- Upravljanje USB alatom: [[operations/usb-tool]]
- Hardverska zaobilaženja: [[architecture/hardware-bypass]]
- Održavanje medijuma i WIM-a: [[operations/media-maintenance]]
- Poznate zamke i rešenja: [[memory/gotchas]]
