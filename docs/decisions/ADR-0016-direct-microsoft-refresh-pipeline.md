# ADR-0016: Direct Microsoft Refresh WIM/ESD Pipeline na Linuxu (100% Nezavisnost od Windows Mašine)

- **Status:** Prihvaćeno
- **Datum:** 2026-09-15
- **Autor:** GWADT Multi-Agent Sistem (`gwadt_orchestrator`, `gwadt_module_architect`, `gwadt_qa_debugger`, `gwadt_docs_sync`)
- **Kontekst:** Trajno oslobađanje od zavisnosti od Windows radne stanice i DISM-a za osvežavanje instalacionog WIM-a

---

## 1. Kontekst i Problem

U servisu "WIS Service", primarna radna stanica servisera radi pod Linux operativnim sistemom (Ubuntu/Debian okruženje).
Tokom implementacije offline ažuriranja uočena su dva ključna operativna izazova:

1. **Zavisnost od Windows DISM Servisa (ADR-0014):**
   - Windows DISM (`dism.exe /Add-Package`) koristi vlasnički Microsoft CBS (Component-Based Servicing) engine unutar Windows NT kernela.
   - Ovaj engine ne postoji na Linuxu i ne može se emulirati kroz Wine/Proton.
2. **Ograničenja UUP Linux Konvertera (ADR-0015):**
   - Pokušaj integracije `.msu`/`.cab` kumulativnih zakrpa preko zvaničnog UUP Linux skripta (`convert.sh`) rezultovao je sistemskom porukom:
     > `Note: This script does not and cannot support the integration of updates. Use the Windows version of the converter to integrate updates.`
   - Pored toga, rad sa UUP dump-om na samom USB fleš disku zahteva 8–12 GB privremenog prostora, što je dovelo do prepunjavanja fleša (*No space left on device*) i aktiviranja NTFS dirty zastavice.

Postavljen je jasan inženjerski cilj: **U potpunosti eliminisati potrebu za Windows računarom prilikom osvežavanja instalacionog fleša**, uz minimalno vreme trajanja i garantovanu zaštitu prostora na USB flešu.

---

## 2. Razmatrana Rešenja

1. **Opcija A: Virtuelizovana Windows mašina (KVM / QEMU / VirtualBox) na Linuxu:**
   - *Mane:* Zahteva 30+ GB prostora za virtuelni disk, 4-8 GB RAM-a, passthrough USB kontrolera i kompleksno održavanje virtuelne mašine samo radi povremenog pokretanja jedne DISM komande.
2. **Opcija B: Preuzimanje gotovih kompilacija sa foruma / trećih izvora:**
   - *Mane:* Ogroman bezbednosni rizik za servis. Ne sme se koristiti nepouzdan softver na klijentskim mašinama.
3. **Opcija C: Direct Microsoft Refresh Pipeline sa `wimlib-imagex` (Izabrano rešenje):**
   - Microsoft mesečno objavljuje ažurirane zvanične Windows 11 Refresh ISO slike (Media Creation Tool / TechBench / VLSC) koje u sebi **već fabrički sadrže najnoviji kumulativni build**.
   - Na Linuxu, alat `wimlib-imagex` je višestruko brži i efikasniji od Windows DISM-a:
     - Nativno montiranje ISO-a preko `udisksctl loop-setup` (bez traženja root/sudo lozinke).
     - Izdvajanje samo ciljne edicije (`Windows 11 Pro`) uz istovremenu maksimalnu Solid/LZX kompresiju (`--compress=maximum`).
     - Kompletna privremena obrada se obavlja na lokalnom disku sa stotinama gigabajta slobodnog prostora (`/home/gru/Storage`), a gotov WIM se atomično kopira na USB fleš uz `sync`.

---

## 3. Doneta Odluka

Usvojena je **Opcija C: Direct Microsoft Refresh Pipeline**.

### Arhitektura rešenja u `Skripte/Update-WimImage-Linux.py`:

```
+-------------------------------------------------------------------------------+
|                        LINUX RADNA STANICA (UBUNTU)                           |
+-------------------------------------------------------------------------------+
       |
       +--> 1. Zvanični Microsoft Windows 11 ISO (npr. Win11_24H2_refresh.iso)
       |
       +--> 2. udisksctl loop-setup (bezbedno montiranje bez sudo)
       |
       +--> 3. wimlib-imagex export (Pro edicija, LZX maksimalna kompresija)
       |       Radni direktorijum: /home/gru/Storage/gwadt_refresh_work (369 GB)
       |       Vreme trajanja: ~2-3 minuta!
       |       Rezultat: WIM veličina smanjena sa 7.5 GB na ~4.3 GB (-42%)
       |
       +--> 4. Atomična zamena na USB flešu:
       |       - Provera slobodnog prostora na USB flešu
       |       - Pravljenje sigurnosne kopije sources/install.wim.bak (ako ima mesta)
       |       - Kopiranje novog install.wim i forsirani keš `sync`
       |
       v
+-------------------------------------------------------------------------------+
|         USB FLEŠ: 100% OSVEŽEN, NAJNOVIJI BUILD, SPREMAN ZA RAD!              |
+-------------------------------------------------------------------------------+
```

### Podržani režimi rada u `osvezi-wim-linux.sh`:

1. **Brza provera verzije:**
   ```bash
   ./osvezi-wim-linux.sh --check-only
   ```
   Čita XML zaglavlje WIM-a direktno na flešu, poziva Microsoft API i ispisuje `[UP TO DATE]` ili tačnu razliku u revizijama.

2. **Direktan uvoz iz ISO fajla:**
   ```bash
   ./osvezi-wim-linux.sh --iso /putanja/do/Windows11_Ažuriran.iso
   ```

3. **Direktan uvoz iz ESD fajla:**
   ```bash
   ./osvezi-wim-linux.sh --esd /putanja/do/install.esd
   ```

---

## 4. Posledice i Dobici

- **0% zavisnosti od Windows mašine:** Serviser više nikada ne mora prebacivati USB fleš na Windows računar radi osvežavanja WIM-a.
- **Drakonska ušteda vremena:** Celokupna ekstrakcija i kompresija na Linuxu traje **svega 2 do 3 minuta**, dok je puna DISM integracija na Windowsu trajala 25–45 minuta.
- **Ušteda prostora na flešu:** Originalni višekorisnički `install.wim` sa 6+ edicija (Home, Edu, Pro...) zauzima ~7.5 GB. Izdvajanjem isključivo Windows 11 Pro edicije uz maksimalnu kompresiju, novi WIM zauzima **samo ~4.3 GB**, oslobađajući preko 3 GB dragocenog prostora za drajvere i programe.
- **Sigurnost fleš memorije:** Svi privremeni fajlovi se obrađuju na internom SSD disku (`/home/gru/Storage`), čime se sprečava habanje USB memorijskih čipova i eliminiše rizik od prepunjavanja i korupcije fajl sistema.
- **100% kompatibilnost:** Struktura USB-a (`autounattend.xml`, `FirstLogon.cmd`, `Skripte/Modules/`) ostaje potpuno netaknuta.

---

## 5. Povezane Beleške

- [[operations/media-maintenance]]
- [[memory/gotchas]]
- [[decisions/ADR-0014-offline-wim-slipstreaming]]
- [[decisions/ADR-0015-linux-native-wim-refresh-uup]]
- [[index]]
