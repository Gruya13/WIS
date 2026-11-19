# WIS-TASK-173 — Plan implementacije zero-touch PXE instalacije

Status: plan za implementaciju; kriterijumi se zamrzavaju u LOCK.json pre runtime izmena.
Datum: 2026-11-10. Polazni zahtev: USB radi, PXE Setup traži drajver za medijum.
VMD kvar nije potvrđen. Normalni tok nema meni, potvrdu, izbor drajvera ili ručne komande.

## 1. Dokazati razliku, bez menjanja radnog USB toka

- Sa istog probnog računara prikupiti USB i PXE rezultat uz isti SHA-256 ISO-a, firmware režim i BIOS storage postavke. Koristiti namenski prazan test disk za instalacije.
- Zabeležiti verzije i identitet stvarnog boot.wim, Setup-a i izvora slike; ne zaključivati ponašanje isporučenog ISO-a iz izvornog Python generatora.
- Automatski logovati ulaz (autorun/startnet), vremena, NIC/PCI, DHCP, slova i tipove diskova, pokušaje otvaranja izvora, odabrani INF, binding i exit kodove.
- Prikupiti X:\Windows\System32\ventoy\vtoype.log, dostupne Panther setupact/setuperr i setupapi.dev.log. Odsutan log označiti kao odsutan, ne kao uspeh.
- Razdvojiti MEDIA_UNAVAILABLE od STORAGE_UNAVAILABLE. Postojeći Accelerator sadrži demontiranje Y:, ali njegov poziv nije potvrđen u aktuelnom autounattend.xml.

Izlaz faze: ponovljiv nalaz tačne faze kvara i trag USB kontrolnog slučaja. Ako se kvar ne reprodukuje, nalaz ostaje nepotvrđen.

## 2. Jedan koordinator i proverljiva stanja

Planirane datoteke: ISO/WIS-Preflight.cmd (ulaz), ISO/WIS-Preflight helper prilagođen dostupnom WinPE-u, ISO/load_vmd.cmd (zajednički storage ulaz), generator ISO/Skripte/Patch-BootWim.py.

- Uvesti zajednički sinhroni tok koji koriste autorun i startnet. Najpre potvrditi iVentoy hook redosled na stvarnoj slici; ne uvoditi drugi nekontrolisani Setup proces.
- Model stanja: INIT -> NETWORK_READY (PXE) -> MEDIA_READY -> STORAGE_READY -> SETUP_STARTED; FAIL ima razlog i fazu. USB lokalni izvor ne zavisi od mreže.
- Atomski session lock sprečava paralelne ulaze. Drugi poziv čeka završetak prvog unutar roka; ne pokreće dodatni Setup. Stari WIS_PreBoot_Done.tag nije dokaz READY.
- Odvojiti stanje mreže, medijuma i storage-a. READY važi samo za tekuću WinPE sesiju i potvrđeni izvor/paket.
- Nijedan preflight korak ne formatira, ne bira disk 0, ne menja SAN politiku niti automatski otključava diskove.

## 3. Mreža i medijum — primarni fokus

- Koristiti ugrađene validirane NIC pakete kada nedostaje vezan funkcionalan adapter; čekati DHCP i vezu sa odabranim serverom.
- Ponovni pokušaji imaju monotoni ukupni rok: mreža 60 s, medijum 30 s, storage 30 s; preflight ukupno najviše 120 s. wpeinit ima zaseban watchdog 120 s. Blokirajuće spoljne komande moraju stati u preostali rok faze.
- Setup i trajanje same instalacije nisu uključeni u ovaj preflight rok. Prvi logovati početak i trajanje wpeinit-a; posle prekoračenja ne nastavljati Setup.
- Sačuvati postojeći iVentoy mount. NAS alternativa bira se i potvrđuje PRE Setup-a; slova/montiranja se ne menjaju dok ih Setup koristi.
- Provera medijuma: uspešno otvaranje i čitanje uzorka instalacione slike i čitanje metapodataka slike kada alat postoji, plus čitljivost Setup zavisnosti. Postojanje fajla ili ping nisu dovoljan dokaz. Pun hash velikog WIM-a radi se pri objavi, ne pri svakom boot-u.
- Objavljeni manifest identifikuje koherentan skup Setup/slika/unattend/OEM fajlova i njihove hash-eve. Izuzetke za različite izvore dozvoliti samo ako manifest eksplicitno opisuje taj skup; ne birati proizvoljno najnoviji NAS Setup uz stari boot ISO.
- Ako primarni izvor ne radi, unutar istog roka automatski koristiti već proverenu alternativu istog izdanja. Ako nijedna nije dostupna, FAIL + log; nema dijaloga za ručni drajver niti pokretanja Setup-a.

## 4. Storage bez ručnog izbora

- Sačuvati funkcionalan nativni AHCI/NVMe. Prisustvo USB diska nije dokaz spremnosti internog ciljnog kontrolera.
- Napraviti build katalog iz stvarnih INF paketa: VEN/DEV/SUBSYS, Models dekoracije, arhitektura, podržani OS i validirani OEM prioriteti. Ne prepoznavati generaciju CPU-a kao zamenu za PCI kompatibilnost.
- Sačekati enumeraciju u storage roku; učitati jedan odgovarajući paket. Ne prebacivati verzije iaStorVD nakon neuspeha u istoj sesiji.
- Proveriti exit kod + vezivanje kontrolera + ciljni disk. Nepoznat/nejednoznačan kontroler i exit 0 bez diska nisu READY.
- Osigurati boot-critical instalaciju odabranog paketa u ciljni Windows pre prvog restarta. /InstallDrivers iz WinPE koristiti samo sa potvrđenim Windows 11 24H2+ setup.exe; za stariji Setup validirati odgovarajući podržan put odvojeno, bez prikrivenog preskakanja.

## 5. WinPE implementacioni izbor i dijagnostika

- Inventarisati alate stvarnog boot.wim-a pre izbora helpera. CMD wrapper nema pretpostavku da PowerShell/CIM postoji. Ako se koristi PowerShell, zahteva 5.1 kompatibilnost i ugrađene WinPE komponente.
- Log u RAM-u, automatski izvoz na namensku dozvoljenu NAS log putanju kada je dostupna. Bez mreže ostaje RAM log i jasan status; nema obećanja trajnosti posle reboot-a.
- Ne beležiti SMB lozinke, sadržaj Wifi-Servis.ini ili klijentskih XML profila; ne kopirati ih u QA paket. Jedinstven ID sesije povezuje preflight, Setup i first-boot dokaze.
- Ako veza nestane nakon starta Setup-a, preflight ne pokreće drugu instalaciju i ne premapira izvor; evidentira se greška, sačuvaju dostupni logovi. Oporavak u toku Windows Setup-a nije obećan ovom verzijom.

## 6. Redosled implementacije i provera

1. Dijagnostika i baseline iz faze 1 — wis_windows_admin / wis_qa_debugger.
2. Zajednički ulaz, session lock, statusi, procesni rokovi i logovi — wis_module_architect / wis_powershell_expert.
3. NIC/DHCP i stabilni koherentni izvori — wis_windows_admin.
4. Storage katalog, selekcija i first-boot staging — wis_windows_admin / wis_security_hardening.
5. Generator ugrađuje iste skripte u sve boot indekse koji se stvarno koriste; proveriti ekstrakcijom sadržaj iz gotovog WIM-a, ne samo šablon.
6. Automatizovani testovi kvarova sa kontrolisanim adapterima za komande; zasebni WinPE/PXE testovi iz acceptance.json. Mock test nije dokaz hardverske kompatibilnosti.
7. Kanonski build ISO/Skripte/Build-WIS.ps1 na Windows PowerShell 5.1 i odgovarajuće provere ISO pakovanja; izlaz .workspace/artifacts/windows. Istorijski Test-Task* nisu gate.
8. USB regresija i PXE na istom hardveru, pa reprezentativni VMD v19/v20 i nativni NVMe računari. Potvrditi prvi Windows boot i potrebne runtime putanje C:\setup bez promena.
9. wis_docs_sync beleži rezultate i ADR; isporuka tek kada su svi obavezni scenariji PASS sa dokazima. Nepostojeći hardver/test ostaje NOT_RUN i blokira release tvrdnju.

## 7. Zaključavanje testova i kriterijum završetka

acceptance.json je normativna matrica. verify_acceptance.py proverava integritet zaključanog ugovora i kompletnost prijavljenih rezultata/dokaza; nije emulator WinPE-a i ne potvrđuje istinitost sadržaja loga.
LOCK.json sadrži SHA-256 plana, matrice i validatora. Kontrolna suma otkriva izmene; nije kriptografski potpis, zaštita od autora koji može promeniti i lock, niti zamena za Git review.
Nema automatskog osvežavanja lock-a pri testiranju. Izmena kriterijuma zahteva novu verziju ugovora, zabeležen razlog i korisnikovo odobrenje; postojeći rezultati ne prenose se automatski u novu verziju.
Testiranje validatora sintetičkim rezultatima dokazuje samo njegov rad. Stvarni Windows/PXE testovi ostaju NOT_RUN do izvršavanja.

Završetak implementacije: nula interakcija u normalnom PXE toku od izabranog PXE/ISO ulaza do ugovorene instalacije i prvog boota; nema traženja drajvera za medijum; USB kontrolni tok ostaje funkcionalan; svi obavezni testovi imaju proverene dokaze. Automatski neuspeh na nepoznatom hardveru je očekivan negativan test, ne uspešna instalacija.

## Izvori i ograničenja

- https://www.iventoy.com/en/doc_win_driver.html — media prompt na standardnom ISO-u; WIS uzrok potvrditi logovima.
- https://www.iventoy.com/en/doc_inject_autorun.html — autorun hook.
- https://learn.microsoft.com/en-us/windows-hardware/manufacture/desktop/windows-setup-command-line-options?view=windows-11 — Setup parametri.
- Raniji predlog: .workspace/wiki/operations/vmd-pxe-selection-proposal.md.

Ovaj dokument ne tvrdi da je runtime izmenjen, ISO izgrađen ili PXE instalacija testirana.
