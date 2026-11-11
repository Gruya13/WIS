# Zaključani testovi zero-touch PXE toka

`acceptance.json` je ugovor prihvatanja sa 22 obavezna scenarija. `example-results.json` je prazna prijava: svi rezultati su NOT_RUN. Ovo nisu već izvršeni Windows/PXE testovi. Laboratorijski testovi zahtevaju test računar i potrošni disk, isti ISO za USB/PXE kontrolu i stvarne logove.

`LOCK.json` čuva SHA-256 matrice, validatora i plana. Izmenu zaključanog ugovora treba posebno obrazložiti i odobriti; ne menjati očekivanja da bi implementacija prošla. Hash zaključavanje otkriva promenu prema sačuvanom manifestu; nije kriptografski potpis ili zaštita od onoga ko može izmeniti i manifest.

```sh
python ISO/Tests/PxeZeroTouch/verify_acceptance.py --check-lock
python ISO/Tests/PxeZeroTouch/verify_acceptance.py
python ISO/Tests/PxeZeroTouch/verify_acceptance.py --results /putanja/do/lab-run/results.json
```

Prva komanda proverava samo integritet. Druga vraća NOT_RUN i nenulti kod. Treća zahteva svih 22 jedinstvenih ID, status PASS za svaki scenario, metapodatke i neprazne fajlove dokaza. Putanje dokaza su relativne u odnosu na results.json i ne smeju izaći iz njegovog direktorijuma. FAIL, BLOCKED, NOT_RUN, izostavljen ili nepoznat scenario blokiraju gate. Podvarijante scenarija moraju sve proći i imati dokaze.

**Ograničenje:** validator proverava kompletnost prijave i postojanje fajlova; ne parsira semantiku logova i ne dokazuje da je uređaj stvarno prošao test. `EVIDENCE_COMPLETE` nije samostalno odobrenje isporuke. QA pregleda stvarne logove i potvrđuje svako očekivanje; sintetički rezultati služe samo proveri validatora i nisu prihvatljivi za produkcioni gate.

Mreža ima budžet 60 s, čitanje/provera medijuma 30 s, storage 30 s, ukupno 120 s od završetka wpeinit do Setup-a. Timeout obuhvata blokirane podprocese. Nijedan neuspeh preflight-a ne sme menjati particije ili podatke ciljnih diskova. Normalan instalacioni tok nema ručnu potvrdu ili meni.
