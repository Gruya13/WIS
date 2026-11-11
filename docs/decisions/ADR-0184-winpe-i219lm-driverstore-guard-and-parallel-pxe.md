# ADR-0184: WinPE I219-LM DriverStore Guard, Višekorisnički TrueNAS SMB 3.1.1 Mount i Hibridna PXE Otpornost (ISO v2.18.0)

- **Status:** Prihvaćeno (Accepted)
- **Datum:** 2026-11-11
- **Učesnici:** `wis_orchestrator`, `wis_windows_admin`, `wis_qa_debugger`, `wis_module_architect`, `wis_docs_sync`
- **Povezani zadaci:** `WIS-TASK-173`, `WIS-TASK-174`, `WIS-TASK-176`, `WIS-TASK-179`, `WIS-TASK-182`, `WIS-TASK-183`, `WIS-TASK-184`

---

## 1. Kontekst i Problemi

Tokom paralelnog PXE (iVentoy) testiranja na više modela poslovnih laptopova (uključujući Lenovo ThinkPad sisteme sa Intel Ethernet mrežnim karticama `I219-V` i `I219-LM`) otkrivena je serija povezanih problema u `boot.wim` i WinPE batch okruženju:

1. **Nedostatak OEM LAN drajvera u `DriverStore\FileRepository` (`DEV_0D4F`, `DEV_15FC`):**
   - Fabrički Windows 11 24H2/25H2 `boot.wim` poseduje inbox `net1ix64.inf` (`e1i68x64.sys`), ali ne sadrži `e1d.inf` za veliki broj `Intel I219-V` varijanti (`DEV_0D4F`, `DEV_15FC`). Zbog toga je `VTOYPE.EXE PNPHELP` ostajao bez drajvera, čekao `WAITNIC 120s`, a Setup izbacivao *"A media driver your computer needs is missing"*.
2. **Kolizija `VTOYPE.EXE PNPHELP` na Intel I219-LM (`PCI\VEN_8086&DEV_15FB`):**
   - Disasembliranjem `vtoypxe64.exe` potvrđeno je da `VTOYPE.EXE PNPHELP` abecedno skenira `X:\Windows\System32\DriverStore\FileRepository\*`, odseca ime foldera nakon `.inf` i za prvi pogodak odmah pokreće `drvload`.
   - Na `DEV_15FB`, `wpeinit` najpre učitava ugrađeni inbox `net1ix64.inf` (`e1i68x64.sys`), a zatim je `PNPHELP` pronalazio abecedno raniji OEM paket `e1d.inf` (`e1d < net1ix64`) i pozivao `drvload e1d.inf` preko već aktivnog NDIS drajvera — obarajući mrežni adapter i izazivajući višeminutni zastoj na plavom ekranu.
3. **Predugačak WinPE zbog 137.49 MiB Wi-Fi drajvera i zabranjeni `/Windows/System32/ventoy`:**
   - Prisustvo pre-kreiranog `/Windows/System32/ventoy` direktorijuma u `boot.wim` uzrokovalo je da `vtoypxe64.exe` (`IsDirExist("ventoy")`) preskoči `UnpackExtraData` i trenutno restartuje WinPE.
   - Istovremeno, paket Wi-Fi drajvera (`137.49 MiB`) u `boot.wim` je bespotrebno povećavao vreme TFTP/HTTP prenosa jer WinPE koristi isključivo LAN vezu.
4. **Setup greška `0x800700FF - 0x40030` i nepostojeći WinPE alati:**
   - U fabričkom `boot.wim` ne postoje ni `findstr.exe` (postoji samo `find.exe`) ni `timeout.exe`.
   - Zagrade `(` `)` unutar `echo` naredbi u višerednim `if (...)` blokovima (`load_vmd.cmd`) izazivale su `cmd.exe` grešku parsiranja sa izlaznim kodom `255` (`0x800700FF` u `<RunSynchronousCommand>`).
   - Nepostojeći `timeout.exe` u petlji čekanja na DHCP prouzrokovao je da petlja proleti za `< 10 ms` pre nego što mrežna kartica dobije IP adresu.
5. **Prerano demontiranje iVentoy diska i skalabilnost do 20 klijenata:**
   - Ranije verzije `WIS-AcceleratePE.cmd` brisale su `Y:` i demontirale `T:\iventoy.iso` pre potvrde TrueNAS SMB diska, pa je u slučaju kašnjenja mreže klijent gubio jedini medijum.
   - `iVentoy` demon povremeno bi tiho pao unutar kontejnera uz otkaz PXE klijentima (`PXE-E99`), dok iVentoy Free verzija prihvata do 20 paralelnih klijenata uz poželjno rasterećenje preko TrueNAS SMB 3.1.1 deljenja (`\\truenas.servis\WIS\win11`).

---

## 2. Donete Odluke i Rešenja

### A. Deterministički `DriverStore` staging i Guard `000_wis_net1ix64.inf_guard` (`ISO/Skripte/Patch-BootWim.py`)
1. **`prepare_driverstore_staging()` ubrizgava kurirane LAN drajvere u `DriverStore\FileRepository`:**
   - U `boot.wim` (oba indeksa) direktno se integrišu potpisani LAN paketi: Intel I217/I218/I219 (`e1d.inf`), Intel I225/I226 (`e2fn.inf`), Realtek PCIe (`rt68cx21x64.inf`) i Realtek USB LAN (`rtu53cx22x64.inf`).
2. **Abecedni Guard paket `000_wis_net1ix64.inf_guard`:**
   - Pri zakrpljivanju `boot.wim`, skripta izdvaja inbox `net1ix64.inf` i kopira ga bez `.sys`/`.cat` fajlova u `DriverStore\FileRepository\000_wis_net1ix64.inf_guard\000_wis_net1ix64.inf` (uz automatsku proveru da sadrži `PCI\VEN_8086&DEV_15FB` i ne sadrži `DEV_15FC`/`DEV_0D4F`).
   - Pošto stoji prvi abecedno, `VTOYPE.EXE PNPHELP` za `DEV_15FB` staje na inbox definiciji i čuva aktivan `e1i68x64.sys`, dok `DEV_15FC` i `DEV_0D4F` neometano nalaze i učitavaju `e1d.inf`.
3. **Zabrana `/Windows/System32/ventoy` i LAN-only `/Drivers/Net`:**
   - `Patch-BootWim.py` eksplicitno uklanja i zabranjuje `/Windows/System32/ventoy` u `boot.wim` (`verify_boot_wim_contents()` proverava 32 tačke).
   - WinPE `/Drivers/Net` sadrži isključivo LAN drajvere (~7.57 MiB u 15 fajlova), čime je `boot.wim` smanjen na ~582 MiB, dok Wi-Fi drajveri ostaju netaknuti u `$OEM$\$1\setup\Drivers\Net\` za instalirani Windows.

### B. Otpornost WinPE Batch Skripti i Nepovratnost Medijuma (`load_vmd.cmd`, `WIS-AcceleratePE.cmd`, `WIS-ConnectNAS.cmd`, `autounattend.xml`)
1. **Batch pravila za WinPE (`cmd.exe`):**
   - Zabranjen `findstr` → koristi se isključivo `find.exe`.
   - Zabranjen `timeout.exe` → koristi se `ping.exe -n 2 127.0.0.1 >nul 2>&1` (pouzdan 1s interval).
   - Uklonjeni višeredni `if (...)` blokovi i oble zagrade `(` `)` u `echo` porukama (zamenjene uglastim `[` `]`).
   - Dodata `ver >nul` zaštita u `load_vmd.cmd` i u `autounattend.xml` `<RunSynchronousCommand>` (`<Order>7</Order>` i `<Order>8</Order>`), garantujući izlazni kod `0`.
2. **Pravilo nepovratnosti montiranja i brzo povezivanje:**
   - U `WIS-AcceleratePE.cmd` i `WIS-ConnectNAS.cmd` (sinhronizovano na svih 5 lokacija u `ISO/`): ako je IPv4 već aktivan (`HAS_IPV4=1`), preskače se `drvload` i DHCP čekanje (`0s`); ping ka TrueNAS-u skraćen na `3×1s`.
   - iVentoy disk `T:\iventoy.iso` i slovo `Y:` se demontiraju **isključivo nakon potvrde** da postoji `Z:\win11\sources\install.esd` ili `install.wim`; u suprotnom, iVentoy virtuelni medijum ostaje dostupan kao pouzdan fallback.
   - `startnet.cmd` poseduje petlju oporavka (`:SearchMediaLoop`, do 30 iteracija) sa interaktivnim `cmd.exe /k` umesto tihog gašenja pri otkazu mreže.

### C. Infrastrukturni Watchdog i Zaključana PXE Matrica Testova (`ISO/Tests/PxeZeroTouch/`)
1. **iVentoy Docker Watchdog (`entrypoint.sh` na TrueNAS-u):** Nadzire PID iVentoy procesa na svake 3 sekunde i automatski restartuje kontejner pri tihom padu (eliminisan `PXE-E99`).
2. **PXE Ugovor Prihvatanja (`ISO/Tests/PxeZeroTouch/`):** Paket definiše **22** scenarija (`PXE-01`..`PXE-22` u `acceptance.json`, `IMPLEMENTATION_PLAN.md`, `verify_acceptance.py`, `example-results.json`, `README.md`) za laboratorijsku verifikaciju WinPE mreže (budžet 60s), čitanja medijuma (30s) i VMD skladišta (30s). `LOCK.json` još nije generisan jer laboratorijsko zaključavanje sa fizičkog/QEMU-KVM testiranja tek sledi (`verify_acceptance.py --check-lock` zato vraća `GATE_FAILED`).

---

## 3. Posledice i Granice

- **Brže PXE pokretanje i paralelizam:** Klijenti sa `I219-V` i `I219-LM` odmah podižu mrežu bez ručne intervencije i prelaze na TrueNAS SMB 3.1.1 (`105–122 MB/s`), dok je `boot.wim` lakši za ~57 MiB.
- **Status verifikacije:** `Patch-BootWim.py --verify-only` (32/32 provere) prošao je pri izgradnji ISO medijuma `6.07 GB`, a laboratorijska matrica `ISO/Tests/PxeZeroTouch/` ima status `NOT_RUN` (čeka kontrolni laboratorijski unos logova u `results.json`).
