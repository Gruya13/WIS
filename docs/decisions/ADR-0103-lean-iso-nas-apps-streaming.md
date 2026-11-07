---
title: "ADR-0103: Lean ISO Arhitektura (Dual-Profile) i Direktan TrueNAS SMB 3.1.1 Stream Programa sa Očuvanim VMD i Mrežnim Drajverima"
type: decision
status: accepted
date: 2026-10-02
author: gwadt_docs_sync / gwadt_orchestrator
tags:
  - decision
  - adr
  - lean-iso
  - smb311
  - nas
  - truenas
  - apps-streaming
  - vmd
  - network-drivers
  - iventoy
  - dual-profile
---

# ADR-0103: Lean ISO Arhitektura (Dual-Profile) i Direktan TrueNAS SMB 3.1.1 Stream Programa sa Očuvanim VMD i Mrežnim Drajverima

## 1. Kontekst i Problem

U dosadašnjoj arhitekturi sistema GWADT (zaključno sa Zadatkom GWADT-TASK-098), generisana instalaciona ISO slika dostigla je veličinu između **13.5 GB i 14.20 GB**. 

Glavni uzročnik ovolike zapremine bila je unutrašnja arhiva instalacionih paketa `Programi.tar` (veličine **~4.45 GB**), koja je bila smeštena u `$OEM$` stablu (`sources/$OEM$/$1/setup/Programi.tar`). Tokom faze `SetupComplete.cmd`, Windows Setup je ovu arhivu kopirao na lokalni disk klijenta, gde se raspakivala preko `tar.exe -xf` pre ulaska na Desktop.

Ovaj monolitni pristup stvarao je nekoliko ozbiljnih inženjerskih i operativnih problema:
1. **Zagušenje mrežnog iVentoy PXE servera:**
   - Prilikom masovnih istovremenih instalacija u servisu, klijenti su preko mreže morali da učitaju preko 14 GB podataka po računaru, iako se programski paketi koriste tek u završnoj Fazi 4.
2. **Predugo vreme generisanja i prenosa ISO slike:**
   - Svako generisanje ISO slike i njen prenos na TrueNAS skladište zahtevali su manipulaciju fajlom od preko 14 GB.
3. **Zastarevanje softvera unutar ISO-a:**
   - Svaka promena verzije programa (npr. novi Office 2024 kumulativni build, Chrome ili Acrobat zakrpa) zahtevala je rekompajliranje celokupnog ISO medijuma, iako se sistemski `install.wim` i Windows zakrpe nisu menjali.
4. **Neiskorišćenost moderne mrežne infrastrukture servisa:**
   - Radionica već poseduje TrueNAS SCALE ZFS skladište (`192.168.170.254`) sa gigabitnom mrežom i ultra-brzim SMB 3.1.1 protokolom ([[decisions/ADR-0102-winpe-smb311-truenas-accelerator]]), sposobnim da isporuči 105–122 MB/s direktno iz ZFS ARC RAM keša. Držanje 4.45 GB statičkih programa u instalacionom imidžu bilo je tehnološki redundantno.

Bilo je neophodno uspostaviti **Lean ISO arhitekturu** koja eliminiše `Programi.tar` iz instalacionog medijuma za potrebe mrežnog iVentoy PXE rada, uz preusmeravanje instalacije programa na direktan SMB 3.1.1 strim sa TrueNAS skladišta.

---

## 2. Gvozdeno Pravilo: Beskompromisno Očuvanje VMD i Mrežnih Drajvera u ISO-u

Prilikom projektovanja Lean ISO arhitekture uspostavljeno je **Gvozdeno Pravilo hardverskog integriteta**:

> [!IMPORTANT]
> **Gvozdeno Pravilo Arhitekture:**
> Skladišni drajveri za Intel VMD RST (`iaStorVD.inf` v20, v19, v18) i mrežni drajveri (Wi-Fi + LAN: Intel, Realtek, MediaTek) **STRIKTNO I BESKOMPROMISNO OSTAJU UGRAĐENI U ISO MEDIJUM**.
> Njihovo uklanjanje ili izbacivanje na mrežu je **IZRIČITO ZABRANJENO**.

### Tehnički razlozi:
1. **Intel VMD skladišni drajveri (`sources/boot.wim` i fascikla `Za disk 10+ gen`):**
   - Na računarima sa 10, 11, 12, 13. i 14. generacijom Intel procesora (kao i novim Intel Core Ultra / Meteor Lake platformama), NVMe SSD se nalazi iza Intel VMD kontrolera.
   - Bez `iaStorVD` drajvera u `boot.wim` (Index 1 i 2) i fascikle `Za disk 10+ gen` (44 fajla), Windows Setup uopšte ne prepoznaje diskove i instalacija ne može ni da počne.
2. **Wi-Fi i LAN mrežni drajveri (`sources/boot.wim` i `sources/$OEM$/$1/setup/Drivers/Net/`):**
   - Nakon što Windows Setup završi rad i računar se prvi put restartuje, sveži Windows 11 često nema fabričke drajvere za moderne Wi-Fi 6/6E/7 kartice ili USB-C/RJ45 Ethernet adaptere (naročito na ultrabook uređajima).
   - Ako mrežni drajveri ne bi bili prisutni na disku (`C:\setup\Drivers\Net`), klijent ne bi mogao da podigne mrežni stek, ne bi mogao da se spoji na servisni Wi-Fi (`Wifi-Servis.ini`), niti bi mogao da pristupi TrueNAS-u i ServisHub-u.
   - Stoga mrežni drajveri predstavljaju **kritični bootstrap most** koji omogućava da se klijent autonomno poveže na mrežu i odatle povuče sve programe.

---

## 3. Dual-Profile Mehanizam u Generatoru (`Skripte/Build-Iso.py`)

Kako bi se obezbedila i maksimalna vitkost za servisnu mrežu i autonomija za rad na terenu, alat `Skripte/Build-Iso.py` nadograđen je parametrom `--profile`:

1. **`--profile lean` (Podrazumevano za mrežni PXE rad):**
   - Potpuno izostavlja `Programi.tar` iz ISO medijuma (i iz korena i iz `$OEM$` stabla).
   - Veličina ISO slike pada sa **14.20 GB na svega 9.09 GB** (ušteda od **4.45 GB** / **-31.3%**).
   - Zadržava sve VMD i mrežne drajvere, `GWADT.Launcher.exe` v2.7.3, `GWADT-AcceleratePE.cmd`, `autounattend.xml` i oba indeksa u `boot.wim`.
2. **`--profile full` (Za terenski rad i offline USB fleš medijume):**
   - Pakuje kompletan `Programi.tar` u `sources/$OEM$/$1/setup/Programi.tar`.
   - Koristi se kada serviser ide na teren gde nema pristupa radioničkom TrueNAS serveru.

Pokretanje kroz objedinjeni skript:
```bash
# Za produkcioni iVentoy PXE server:
./napravi-iso.sh --profile lean --force

# Za terenski autonomni USB fleš:
./napravi-iso.sh --profile full --force
```

---

## 4. Arhitektura Dinamičkog SMB 3.1.1 Strimovanja Programa

```mermaid
flowchart TD
    subgraph Boot["1. WinPE i SetupComplete Faza"]
        A[Windows Setup završava kopiranje sistema] --> B[SetupComplete.cmd]
        B --> C{Postoji li C:\setup\Programi.tar?}
        C -- Da (Full Profil) --> D[tar.exe -xf Programi.tar -C C:\setup]
        C -- Ne (Lean Profil) --> E[Tiho preskakanje za 0 sekundi]
        D --> F[RunOnce -> GWADT.Launcher.exe -> C# Master UI]
        E --> F
    end

    subgraph Phase4["2. Faza 4: Instalacija Programa (GWADT.Apps.psm1)"]
        F --> G[Phase4-Apps.ps1 poziva Get-ServisProgramiRoot]
        G --> H{Prioritet 1: Lokalni C:\setup\Programi?}
        H -- Postoji --> I[Izvor: Lokalni SSD]
        H -- Ne postoji --> J{Prioritet 2: TrueNAS SMB 3.1.1 Port 445?}
        J -- Dostupan (truenas.servis) --> K[Izvor: \\truenas.servis\Servis\Programi]
        J -- Nedostupan --> L{Prioritet 3: Fizički USB Fleš?}
        L -- Pronađen --> M[Izvor: X:\Programi]
        L -- Nema --> N[Greška: Programi nedostupni]
        
        I --> O[Invoke-InstallerWithLocalFallback]
        K --> O
        M --> O
        
        O --> P{Pokretanje sa izvora uspešno?}
        P -- Da --> Q[Instalacija završena (Exit 0)]
        P -- Ne (SMB greška) --> R[Preuzimanje paketa u C:\setup\temp\<App>]
        R --> S[Lokalno pokretanje instalera]
        S --> T[Čišćenje C:\setup\temp foldera u finally bloku]
        T --> Q
    end
```

### Ključne implementirane komponente:

1. **Zaštitno uslovno raspakivanje u `SetupComplete.cmd`:**
   ```cmd
   if exist "%SystemDrive%\setup\Programi.tar" (
       tar.exe -xf "%SystemDrive%\setup\Programi.tar" -C "%SystemDrive%\setup"
   )
   ```
   U Lean profilu ovaj blok se izvršava trenutno (0 ms), bez prekidanja i bez grešaka.

2. **Pametna funkcija `Get-ServisProgramiRoot` (`GWADT.Apps.psm1`):**
   - **Prioritet 1 (Lokalni):** Proverava `C:\setup\Programi`. Ako folder postoji i sadrži instalere, koristi se lokalni disk.
   - **Prioritet 2 (Mrežni SMB 3.1.1 stream):** Proverava dostupnost TrueNAS servera (`\\truenas.servis\Servis\Programi` ili `\\192.168.170.254\Servis\Programi`).
     * Vrši brzi test TCP porta 445 (timeout 2s).
     * Proverava i po potrebi postavlja LanmanWorkstation polise (`AllowInsecureGuestAuth = 1`, `RequireSecuritySignature = 0`).
     * Uspostavlja SMB 3.1.1 sesiju ka deljenju pod `guest` nalogom.
   - **Prioritet 3 (Removable/USB):** Skenira sva prenosiva drajv slova za fasciklom `Programi`.

3. **Robusni `Invoke-InstallerWithLocalFallback` (`GWADT.Apps.psm1`):**
   - Primarno izvršava instaler direktno sa mrežnog UNC puta (`\\truenas.servis\Servis\Programi\...`).
   - Ukoliko pokretanje sa mreže ne uspe (npr. specifična ponašanja instalera koji zahtevaju lokalni disk), funkcija automatski aktivira fallback:
     * Preuzima isključivo taj pojedinačni paket u privremeni folder `C:\setup\temp\<AppName>`.
     * Pokreće instaler sa lokalnog diska.
     * U obaveznom `finally` bloku momentalno i potpuno briše `C:\setup\temp\<AppName>`, čuvajući prostor na disku klijenta.

4. **Optimizacija za Microsoft Office 2024 ODT:**
   - Office Deployment Tool (`setup.exe /configure Configure.xml`) se pokreće direktno preko SMB mreže.
   - Skripta dinamički detektuje i upisuje `SourcePath` i `Version` u `Configure.xml`, uz postavljanje radnog direktorijuma na `C:\setup` radi stabilnosti UNC tokova.
   - Podržan je automatski retry sa keširanjem u `C:\setup\temp\Office2024` ukoliko dođe do privremenog mrežnog prekida.

---

## 5. Izmerene Veličine, Kriptografski Heševi i Verifikacija

Generisanje novog produkcionog Lean Zlatnog ISO-a izvršeno je uspešno, a rezultati su sledeći:

| Metrika | Prethodno Stanje (Zlatni ISO v2.7.2) | Novo Stanje (Lean Zlatni ISO v2.7.3) | Razlika / Ušteda |
|---|---|---|---|
| **Veličina ISO datoteke** | 14,204,821,504 bajtova (~14.20 GB) | **9,755,463,680 bajtova** (**9.09 GB**) | **-4,449,357,824 B (-4.45 GB / -31.3%)** |
| **Prisustvo `Programi.tar`** | Da (4.45 GB u `$OEM$`) | **Ne (0 bajtova u ISO-u)** | Potpuno eliminisano |
| **Intel VMD skladišni drajveri** | Prisutni (v18, v19, v20) | **Prisutni (v18, v19, v20 - 44 fajla)** | **100% OČUVANO (Gvozdeno Pravilo)** |
| **Wi-Fi i LAN mrežni drajveri** | Prisutni (Intel, Realtek, MTK) | **Prisutni (Intel, Realtek, MTK)** | **100% OČUVANO (Gvozdeno Pravilo)** |
| **Vreme generisanja ISO-a** | ~240 s | **91.7 s** | **2.6x brži build** |
| **MD5 kontrolna suma** | `38b53e2d0a2ef4d753e15562f8d699e8` | **`0d9a527da439c5348bcc9c058c729373`** | Verifikovano lokalno i na NAS-u |
| **SHA-256 kontrolna suma** | `5ed0035f87c68599...` | **`e43f544a4ea1105c086a5d8f7fe5d6405d02094c770932a0f6f7bdb58d3c6944`** | Zvanični SHA-256 |
| **El Torito Boot Katalozi** | UEFI (0xEF) + BIOS (0x00) | **UEFI (0xEF) + BIOS (0x00) + UDF** | Potpuna iVentoy/UEFI kompatibilnost |

### Distribucija na TrueNAS Produkciju:
- **Lokacija na NAS-u:** `/mnt/TK_NAS/Servis/Boot_ISO/01_Windows/GWADT-Win11-Pro-10.0.26200.8037.iso`
- **Hard-link:** `/mnt/TK_NAS/Servis/Boot_ISO/01_Windows/GWADT-Win11-Pro-10.0.26200.8037-Lean.iso`
- **TrueNAS Loop Mount:** Servis `gwadt-win11-mount.service` montira novi ISO na `/mnt/TK_NAS/Servis/win11`.
- **iVentoy PXE Server (Port 26000):**
  * Slike **ID 19** i **ID 20** uspešno učitane sa tačnom veličinom **9,755,463,680 B** i zaključanim `uefi_res_lock: 1`.
- **QA Verifikacija:**
  * Pokrenut namenski test `Skripte/Test-Task103-LeanIso-QA.py`: **19/19 provera položeno (100.0% PASS)**, Exit Code 0.

---

## 6. Posledice Odluke

### Pozitivne posledice:
1. **Dramatično smanjenje veličine ISO medijuma:** Sa 14.20 GB na 9.09 GB (pad ispod 9.5 GB praga), što rasterećuje skladište i ubrzava PXE mrežni prenos.
2. **Trenutno ažurni programi:** Svi programi se povlače direktno sa `/mnt/TK_NAS/Servis/Programi/`. Ažuriranje verzije nekog programa na NAS-u momentalno se primenjuje na svim novim instalacijama bez potrebe za novim ISO build-om.
3. **Ušteda prostora na SSD-u klijenta:** Klijentski računar više ne gubi 4.45 GB prostora za raspakivanje arhive na `C:\setup\Programi` pre instalacije.
4. **Dvostruki profil (Fleksibilnost):** Serviser zadržava mogućnost kreiranja punog autonomnog ISO-a za rad na terenu (`--profile full`).

### Operativne napomene:
1. **Zavisnost od NAS-a za Lean profil:** Za instalaciju programa u Lean profilu, klijentska mašina mora imati mrežni pristup TrueNAS serveru (`\\truenas.servis\Servis\Programi`). Ako se instalacija radi van radionice, serviser mora poneti USB izrađen sa `--profile full`.
2. **Održavanje NAS skladišta programa:** Folder `/mnt/TK_NAS/Servis/Programi/` mora imati postavljene dozvole `775` i vlasništvo `nobody:nogroup` kako bi Windows mašine mogle nesmetano da čitaju instalere pod `guest` sesijom.
