---
title: "ADR-0100: GWADT.Launcher v1.0 – Reaktivni Mrežni Listener, Lifecycle Dijalozi i Očuvanje Zero-Touch Automatizacije"
date: 2026-10-01
status: accepted
tags:
  - architecture
  - launcher
  - reactive-network
  - lifecycle-dialogs
  - zero-touch-automation
  - dotnet40
  - thin-client
  - fleet-deployment
deciders:
  - WIS Service Core Team
author: gwadt_docs_sync
---

# ADR-0100: GWADT.Launcher v1.0 – Reaktivni Mrežni Listener, Lifecycle Dijalozi i Očuvanje Zero-Touch Automatizacije

## 1. Kontekst i Problem

Nakon uvođenja početnog Thin-Client koncepta (GWADT-TASK-075, [[ADR-0075-thin-client-launcher-and-dynamic-servishub-payload]]) i potpunog prelaska na čist Thin-Client ISO model bez monolita u `$OEM$` stablu (GWADT-TASK-098, [[ADR-0098-pure-thin-client-oem-hygiene]]), `GWADT.Launcher.exe` postao je **jedina ulazna tačka** sistema u FirstLogon fazi na klijentskim računarima.

Tokom intenzivnog terenskog rada u servisu WIS Service uočeno je nekoliko specifičnih operativnih izazova koji su zahtevali prelazak na punu stabilnu verziju **v1.0**:

### 1.1. Mrežno Kašnjenje i Dinamičko Priključenje Kabla (LAN Plug-and-Play Latency)
- Na savremenim računarima (posebno brendiranim poslovnim radnim stanicama i laptopovima sa Realtek/Intel mrežnim adapterima), inicijalizacija fizičkog linka i DHCP pregovaranje često traju između 3 i 10 sekundi nakon što Explorer pokrene lanser.
- Takođe, u radioničkim uslovima serviser često tek nakon butovanja mašine ubaci mrežni RJ45 kabl ili uključi switch.
- U starijoj verziji, lanser bi nakon jednokratne neuspešne provere mrežne veze prikazao statički offline dijagnostički ekran i ostao u blokirajućem stanju, čekajući da serviser ručno klikne na dugme *"Pokušaj ponovo"*. Ovo je direktno narušavalo osnovni imperativ sistema — **100% Zero-Touch automatizaciju**.

### 1.2. Održavanje i Oporavak Lokalnog Paketa (Clean Reinstall i Reset)
- Ukoliko se na disku (`C:\setup\Skripte\GWADT.UI`) već nalazio raspakovan UI paket (iz prethodne instalacije, testiranja ili delimičnog raspakivanja), stari lanser nije pružao način da se izvrši čista reinstalacija ili uklanjanje oštećenih datoteka bez ručne intervencije kroz File Explorer ili PowerShell konzolu.

### 1.3. Upravljanje Verzijama i Ažuriranja sa NAS ServisHub Servera
- Kada administratori radionice ažuriraju skripte i UI na centralnom TrueNAS ServisHub serveru (`192.168.170.254`), klijentska mašina sa postojećim lokalnim paketom mora inteligentno uporediti verzije i ponuditi serviseru izbor između ažuriranja na najnoviji kod ili zadržavanja trenutne verzije.
- Kada su verzije identične (Up-To-Date), lanser mora omogućiti autonoman nastavak bez ikakvog zastoja.

### 1.4. Striktna Zabrana .NET 4.5+ Zavisnosti na Čistom Windows 11
- Windows 11 u sveže instaliranom stanju fabrički garantuje podršku za .NET Framework 4.0 (CLR v4.0.30319).
- Bilo kakvo uvođenje naprednih C# 6.0/7.0/8.0 jezičkih konstrukata, `async`/`await` državnih mašina (`IAsyncStateMachine`), ili sklopa `System.IO.Compression.FileSystem` / `System.Net.Http` dovelo bi do `TypeLoadException` ili nemogućnosti pokretanja na mašinama pre instalacije novijih .NET ažuriranja.

---

## 2. Razmatrane Alternative

| Kriterijum | Opcija A: Statički Tajmer i Ručni Klikovi | Opcija B: Forsirano Preuzimanje pri Svakom Pokretanju | Opcija C: Reaktivni Mrežni Listener + Lifecycle Dijalozi sa 7s Odbrojavanjem + .NET 4.0 [PRIHVAĆENO] |
| :--- | :--- | :--- | :--- |
| **Ponašanje pri uključenju kabla** | Zahteva ručni klik servisera na dugme | Zahteva ručni klik servisera na dugme | **Automatski prepoznaje mrežnu vezu u realnom vremenu (Auto-Resume) i nastavlja tok** |
| **Zero-Touch garancija** | Narušena pri svakom mrežnom kašnjenju | Očuvana samo ako je mreža 100% spremna u prvoj sekundi | **100% garantovana: 7s autonomno odbrojavanje automatski aktivira [Nastavi]** |
| **Ista verzija paketa** | Uvek pokreće lokalni kod bez opcija | Uvek nepotrebno ponovo preuzima megabajte sa servera | **Prikazuje status, nudi Clean Reinstall / Delete, a nakon 7s automatski nastavlja** |
| **Novija verzija na NAS-u** | Ignoriše ažuriranja na serveru | Prisilno prepisuje lokalni kod bez pitanja | **Jasno obaveštava servisera uz opcije [Ažuriraj] i [Zadrži trenutnu verziju]** |
| **Offline rad** | Blokada na ekranu greške | Prekid instalacije uz fatalnu grešku | **Aktivira istaknuto dugme [Pokreni lokalno van mreže] ako UI postoji na disku** |
| **Kompatibilnost koda** | Može sadržati skrivene .NET 4.5+ pozive | Zavisnost od eksternih Zip biblioteka | **100% čist .NET 4.0 CLR v4.0.30319 (C# 5.0, PE32, Shell.Application / tar ekstrakcija)** |

---

## 3. Odluka i Arhitektura Implementacije

Usvojena je arhitektura **GWADT.Launcher v1.0** koja uvodi sledeće ključne podsisteme:

```
                                  ┌──────────────────────────────┐
                                  │   GWADT.Launcher v1.0        │
                                  │   (.NET 4.0 CLR v4.0.30319)  │
                                  └──────────────┬───────────────┘
                                                 │
                                     [Provera Mreže]
                                        /         \
                             (Nema mreže)         (Mreža dostupna)
                                    /                 \
            ┌─────────────────────────────┐            ▼
            │ Reaktivni Mrežni Listener   │     [UDP Discovery 8888]
            │ - NetworkAddressChanged     │     [Hub GET /api/payload/version]
            │ - Forms Timer (1500 ms)     │            │
            │                             │            ▼
            │ Pri uključenju kabla:       │     [CompareVersions]
            │  => Sakrij dijagnostiku     │       /         \
            │  => Auto-Resume radnog toka │      /           \
            └─────────────────────────────┘  (Ista verzija)  (Nova verzija)
                           │                        │               │
            [UI postoji na disku?]                  ▼               ▼
                 /          \             ┌────────────────┐ ┌────────────────┐
              (Da)         (Ne)           │  pnlUpToDate   │ │pnlUpdateAvail. │
               │            │             │  [Nastavi(7s)] │ │ [Ažuriraj]     │
               ▼            ▼             │  [Preuzmi pon.]│ │ [Zadrži tren.] │
       [Pokreni van mreže] [Čekaj LAN]    │  [Obriši]      │ └────────────────┘
                                          └───────┬────────┘
                                                  │ (7s isteklo)
                                                  ▼
                                      [GWADT.UI.exe --master]
```

### 3.1. Reaktivni Mrežni Listener (`NetworkChange.NetworkAddressChanged` + Forms Timer)
U modulima `WifiHelper.cs` i `SplashForm.cs` implementiran je dvoslojni asinhroni sistem praćenja mrežnog steka:
1. **Sistemski mrežni događaji:** Pretplata na `NetworkChange.NetworkAddressChanged` i `NetworkChange.NetworkAvailabilityChanged` koja trenutno registruje dodelu IP adrese ili podizanje interfejsa.
2. **Zaštitni Forms Timer (1500 ms):** S obzirom na to da virtuelni mrežni adapteri i specifični Wi-Fi drajveri ne okidaju uvek nativne Win32 mrežne događaje pri promeni stanja linka, `SplashForm.cs` pokreće pozadinski WinForms tajmer koji na svakih 1.5 sekundi evaluira `WifiHelper.IsNetworkConnected()`.
3. **Auto-Resume ponašanje:** Čim se detektuje stabilna veza (LAN kabl uštekan ili Wi-Fi povezan), lanser automatski:
   - Zaustavlja mrežni tajmer i odjavljuje događaje (`StopNetworkListener()`).
   - Trenutno sakriva dijagnostički panel (`pnlDiagnostic.Visible = false`).
   - Vraća status na *"Mrežna veza uspostavljena. Povezivanje sa ServisHub..."*.
   - Poziva `StartWorkflow()` i nastavlja automatizovani tok bez ikakve potrebe za klikom servisera.

### 3.2. Up-To-Date Dijalog sa Autonomnim 7-Sekundnim Tajmerom
Kada lanser kontaktira ServisHub i utvrdi da je lokalni paket na disku identičan najnovijoj verziji sa servera (`CompareVersions == 0`):
- Prikazuje se stilizovani panel `pnlUpToDate` (tamna GitHub tema `#0d1117`, zeleni obrub `#3fb950`).
- Serviseru se prikazuju tri jasne akcije:
  1. **`[▶ Nastavi ({0})]` (Default / Auto-Start):** Dugme poseduje aktivni brojač sekundi od 7 ka 0. Istekom 7 sekundi, lanser **automatski simulira klik na Nastavi**, zatvara formu i pokreće `GWADT.UI.exe --master`. Ovim je 100% garantovan **Zero-Touch** rad na novim računarima.
  2. **`[⟳ Preuzmi ponovo]` (Clean Reinstall):** Odmah zaustavlja tajmer odbrojavanja, briše lokalne keširane metapodatke, preuzima svež `gwadt-payload.zip` sa servera, raspakuje ga i priprema čist UI.
  3. **`[✕ Obriši]` (Reset Payload):** Odmah zaustavlja tajmer, poziva `PayloadManager.DeleteLocalPayload()` koji bezbedno uklanja `C:\setup\Skripte\GWADT.UI` i prateće meta fajlove, i vraća lanser u početno stanje čekanja preuzimanja novog paketa.

### 3.3. Update-Available Dijalog za Novije Verzije
Kada ServisHub javi da postoji novija verzija paketa u odnosu na lokalnu (`CompareVersions < 0`):
- Prikazuje se panel `pnlUpdateAvailable` sa prepoznatljivim cyan obrubom (`#38bdf8`).
- Serviseru se nudi izbor:
  - **`[⬇ Ažuriraj sada]`:** Preuzima novi `gwadt-payload.zip`, raspakuje ga preko postojećeg i pokreće najnoviji `GWADT.UI.exe`.
  - **`[▶ Zadrži trenutnu verziju]`:** Preskače mrežni prenos i odmah startuje lokalni `GWADT.UI.exe --master`.

### 3.4. Offline Fallback i Dijagnostički Panel
Ukoliko računar nema mrežnu vezu i provera ServisHub-a ne uspe:
- Lanser prikazuje detaljan dijagnostički panel sa tabelarnim pregledom svih fizičkih adaptera (Tip, Status, Brzina, IP adresa).
- Generiše se zvučni signal upozorenja preko PC Speaker-a (`Console.Beep(800, 300)`).
- **Offline Dugme `[▶ Pokreni lokalno van mreže]`:** Dinamički proverava `File.Exists(PayloadManager.TargetUiExe)`. Ako izvršni modul postoji na SSD-u, dugme postaje aktivno, boji se u prepoznatljivu GitHub plavu boju (`#1f6feb`) i omogućava serviseru da jednim klikom nastavi rad čak i u potpunom odsustvu mreže.

### 3.5. Striktni .NET Framework 4.0 Runtime Standard
Celokupan C# izvorni kod i kompajlirani binarni modul prilagođeni su najvišim standardima robustnosti:
- **C# 5.0 Jezički Nivo:** Stroga zabrana C# 6.0+ konstrukcija (nema interpolacije stringova `$"{var}"`, expression-bodied members `=>`, elvis operatora `?.`).
- **Nema .NET 4.5+ Sklopova:** Zabranjeni `System.IO.Compression.FileSystem` i `System.Net.Http`. Dekompresija ZIP arhiva vrši se preko robusnog fallback mehanizma (COM `Shell.Application` ili Windows 11 ugrađeni `tar.exe -xf`).
- **Nema Async/Await Državne Mašine:** Uklonjen `IAsyncStateMachine` kako bi binarni fajl bio 100% čist IL kôd za CLR `v4.0.30319`.

---

## 4. Posledice Odluke

### Pozitivne Posledice:
1. **Potpuna Otpornost na Radioničko Kašnjenje Mreže:** Serviser više ne mora da brine da li je LAN kabl uključen pre paljenja računara ili 10 sekundi kasnije — Launcher automatski detektuje link i nastavlja rad.
2. **Neprikosnovena Zero-Touch Garancija:** Zahvaljujući 7s autonomnom odbrojavanju na Up-To-Date panelu, mašine na stolu prolaze kompletnu instalaciju od OOBE do gašenja bez ijednog dodira tastature ili miša.
3. **Fleksibilnost Održavanja:** Serviser u svakom trenutku može jednim klikom osvežiti lokalne skripte, obrisati oštećeno stanje ili zadržati testiranu verziju.
4. **Offline Autonomija:** Računari koji poseduju keširan UI mogu raditi u uslovima potpunog prekida mrežne infrastrukture.
5. **Apsolutna Prenosivost:** Binarni modul veličine svega **54 KB** pokreće se trenutno na bilo kom Windows 11 računaru bez potrebe za preuzimanjem .NET biblioteka.

### Negativne Posledice i Ograničenja:
- Zahteva preciznu sinhronizaciju verzija (`version.json`, `AssemblyInfo.cs`, `payload_meta.json`) kako ne bi dolazilo do lažnih obaveštenja o novim verzijama.
- Ograničenje na C# 5.0 zahteva disciplinu programera (korišćenje `string.Format`, eksplicitnih delegata i `ThreadPool` niti umesto modernog `async`/`await`).

---

## 5. QA Verifikacija i Sertifikacija

Kompletna implementacija verifikovana je namenskom automatizovanom QA skriptom `Test-Task100-Launcher-v10-QA.py`:

```
======================================================================
  GWADT-TASK-100 QA VERIFIKACIJA: GWADT.Launcher v1.0
======================================================================
[PASS] Test 1: Verifikacija verzije binarca (FileVersion 1.0.0.0, ProductVersion 1.0)
[PASS] Test 2: PE32 zaglavlje, sekcije i .NET CLR v4.0.30319 runtime
[PASS] Test 3: Odsustvo zabranjenih .NET 4.5+ zavisnosti i async/await mašine
[PASS] Test 4: Prisustvo novih metoda i kontrola (CompareVersions, pnlUpToDate, Timer)
[PASS] Test 5: 4-Way binarni paritet (54,272 B, SHA-256: 8d337c7b...)
[PASS] Test 6: Unit test logike CompareVersions (svih 11 scenarija položeno)
----------------------------------------------------------------------
REZULTAT: 77/77 TESTOVA POLOŽENO (100.0% USPEŠNOST) - SERTIFIKOVANO ZA PRODUKCIJU!
======================================================================
```

### Proverene Lokacije i Binarni Paritet (SHA-256):
- **Veličina:** `54,272` bajtova
- **SHA-256:** `8d337c7b57f86401283ab6e00be5fe9d2cce037a20bce109d20f3379d5a8c084`
- **Potvrđene instance:**
  1. `Skripte/GWADT.Launcher/bin/Release/GWADT.Launcher.exe`
  2. `Skripte/GWADT.Launcher/GWADT.Launcher.exe`
  3. `GWADT.Launcher.exe` (koren radnog prostora)
  4. `sources/$OEM$/$1/setup/GWADT.Launcher.exe` (instalaciono stablo)
  5. `192.168.170.254:/mnt/TK_NAS/Servis/GWADT.Launcher.exe` (TrueNAS skladište)
  6. `/home/gru/Projects/GWADT/gwadt-launcher/GWADT.Launcher.exe` (Standalone repo)

---

## 6. Povezane Odluke i Reference

- [[ADR-0075-thin-client-launcher-and-dynamic-servishub-payload]] — Inicijalna Thin-Client C# Launcher arhitektura i Flash & Release koncept.
- [[ADR-0076-decoupling-monolith-into-phase-scripts-ui-orchestrator]] — Dekompozicija monolita u 5 modularnih faza i UI Master Task Runner.
- [[ADR-0077-nas-docker-servishub-and-golden-iso-release]] — Produkciona Docker implementacija ServisHub-a na TrueNAS-u (192.168.170.254).
- [[ADR-0098-pure-thin-client-oem-hygiene]] — Prelazak na čist Thin-Client ISO model i uklanjanje skripti iz $OEM$.
- [[../architecture/overview]] — Sveobuhvatni arhitektonski pregled GWADT sistema.
- [[../memory/gotchas]] — Centralna baza uočenih zamki i rešenja.
