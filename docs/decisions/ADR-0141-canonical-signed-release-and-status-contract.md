---
title: "Kanonski WIS izvori, potpisana izdanja i dijagnostički ugovor"
type: decision
created: 2026-10-14
status: accepted-development
---

# ADR-0141 — razvoj WIS 2.17

## Kontekst

NVMe i NAS kod nisu potpuno sinhronizovani uprkos identičnim gotovim binarcima. Automatsko pakovanje na serveru i nepotpuni lokalni manifest ne daju pouzdan release ugovor. Završena instalacija ne dokazuje ispravno hlađenje kada senzori nisu dostupni.

## Odluka

Kanonski razvojni Git monorepo uključuje klijent i `ServisHub/`; arhive i originalna Hub istorija ostaju sačuvani. Windows PowerShell 5.1 build potvrda sa proverom EXE verzija, stvarni Windows VM restart/cleanup PASS istog commit-a i čist source commit obavezni su za eksplicitno potpisivanje. Nova izdanja potpisuju svaki ZIP fajl, vezuju release ID za commit i objavljuju se u nepromenljivom direktorijumu sa atomarnim current pointer-om. Server ih samo verifikuje i služi. Medijum potiče iz istog potpisanog izdanja/commit-a; spoljni mrežni resursi imaju SHA-256 lock.

Launcher primenjuje kompletnu proveru i offline, mutex i provereni backup/recovery, uz odvojeni self-update helper. Dijagnostika odvojeno beleži `cooling_verified` i `manual_check_required`; nedostupni senzori zahtevaju ručnu proveru.

## Posledice i granice

Stari minimalni manifest 2.16.2 server može read-only proveriti, dok ga novi Launcher odbija. Prelaz zahteva svež 2.17 medijum sa novim Launcher-om. Postojeći privatni ključ ostaje van Git-a bez rotacije. Odluka je implementirana u razvoju; produkcioni NAS/binari/ISO nisu menjani. Windows runtime, self-update/UAC i VM E2E ostaju neizvršeni gate-ovi. Istorijski ADR nisu prepisani.

Detalji: [[../operations/build-release-media-2.17]], [izveštaj](../../docs/WIS-TASK-141.md).
