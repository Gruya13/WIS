# ADR-0258: WIS v3.2.0 / ServiceHub v3.5.0 — Jedinstveni deduplikovani Driver Pool (CAS) + Model Manifest + Zvanični OEM Enterprise Katalozi (`WIS-TASK-258`)

- **Datum:** `2026-11-19`
- **Status:** Prihvaćeno i implementirano (`WIS v3.2.0` / `ServiceHub v3.5.0`)
- **Povezani dokumenti:** [`ADR-0254`](ADR-0254-wis-3-1-1-audio-deadlock-driver-resilience-and-ig-systems-ownership.md), [`ADR-0236`](ADR-0236-wis-benchmark-predictive-cache-and-post-telemetry-hardening.md)

---

## 1. Kontekst

U prethodnim verzijama WIS ekosistema drajveri su se čuvali paralelno u tri odvojena silosa:
1. `<Model>.tar` arhive u `Drivers/Harvested/Archives/` (berba sa klijenata, bez međusobne deduplikacije između srodnih modela iste generacije).
2. `drivers_cache/<hwid_sha256>.cab` pojedinačni paketi iz Microsoft Update Catalog-a.
3. `KNOWN_MODELS` statične/simulirane tabele u `drivers/oem/{hp,dell,lenovo}.py` bez pravog preuzimanja i raspakivanja zvaničnih OEM Enterprise XML kataloga.

Zbog takve razdvojenosti, spajanje OEM driver pack-ova sa postojećim `.tar` i `.cab` drajverima uzrokovalo bi višestruko dupliranje identičnih paketa na TrueNAS ZFS disku i mogućnost pregažavanja novijeg drajvera starijim paketom ili inbox `prnms009.inf` fajlovima.

---

## 2. Donete Odluke

1. **Jedinstveni Content-Addressable Driver Pool (`ServiceHub/servishub/drivers/pool.py`):**
   - Sva 3 izvora (`oem_pack`, `ms_catalog`, `harvest`) pune jedinstveni `DriverPoolManager` (`packages/<pkg_sha256>/`, atomski `pool_index.json` i `manifests/<clean_model>.json`).
   - Deterministički `pkg_sha256` računa se nad kanonskim relativnim putanjama, veličinama i SHA-256 sumama datoteka pojedinačne `.inf` jedinice: identičan paket iz različitih izvora ili različitih modela zauzima tačno `0 B` dodatnog prostora (`dedup_hits`, `dedup_saved_bytes`).
   - Pobednik verzije za svaki `HWID` (`hwid_winners` i `manifest.hwid_map`) bira se striktnom funkcijom `_is_package_better`: WHQL `.cat` potpis ima prioritet nad nepotpisanim paketom, a zatim pobeđuje strogo noviji `DriverDate`/`DriverVer` preko `is_catalog_driver_newer`. Starije verzije nikada ne degradiraju Pool ni manifeste.
   - Bezbednost: automatsko odbacivanje `prnms009.inf`, odbacivanje nepotpisanih paketa sa zabranjenim skriptama (`.ps1`/`.bat`/`.cmd`/`.vbs`/`.js`/`.msi`...), izbacivanje zabranjenih ekstenzija iz paketa, te zaštita od path traversal-a i symlinkova u `.tar`/`.zip`/`.cab`/`.7z`/SFX `.exe` arhivama.

2. **Zvanični OEM Enterprise XML Katalozi (`ServiceHub/servishub/drivers/oem/catalogs.py`):**
   - `OemCatalogManager` preuzima, kešira (`24h` TTL) i parsira zvanične kataloge: **Dell** (`DriverPackCatalog.cab`), **HP** (`HPClientDriverPackCatalog.cab`) i **Lenovo** (`catalogv2.xml`), sa XXE zaštitom (`<!ENTITY`/`<!DOCTYPE` blokada), strogom HTTPS host allowlist-om i verifikacijom `SHA-256`/`MD5` kontrolnih suma pri preuzimanju OEM paketa.
   - Offline ponašanje postojećih `HpOEMProvider`, `DellOEMProvider` i `LenovoOEMProvider` ostaje očuvano radi pune unazadne kompatibilnosti.
   - `ServiceHub/Dockerfile` dobija `p7zip-full` i `cabextract` pored `libarchive-tools` (`bsdtar`) za raspakivanje `.cab` i SFX `.exe` paketa na Linux serveru.

3. **Dinamičko sklapanje i REST/Web/Client Integracija:**
   - `build_bundle_tar` sklapa (i kešira u `bundles/`) minimalni `.tar` paket po punom manifestu modela (uklj. `all_hwids` za generičke ploče) ili po konkretnoj listi `missing_hwids` sa prioritetom tačnog `SUBSYS` meča.
   - Nove rute u `handlers.py`: `GET /api/drivers/pool/{stats,manifests}`, klijentski `POST /api/drivers/pool/bundle`, fail-closed admin rute `POST /api/drivers/pool/{sync-catalogs,ingest-oem,migrate,prune}`, automatski pozadinski ingest pri `/api/drivers/model-pack/upload` i `/api/drivers/resolve`, i transparentni fallback u `/api/drivers/model-pack/{status,download}`.
   - Web Cockpit (`index.html`, `dashboard.js`) dobija karticu **Jedinstveni Driver Pool & OEM Enterprise Katalozi** sa `adminFetch` kontrolama.
   - Klijentski PowerShell 5.1 moduli (`WIS.Update.psm1` / `Invoke-DriverPoolBundleInstall` + `WIS.Hardware.psm1` batch pre-pass u `Resolve-MissingDriversFromHub`) koriste Driver Pool kao **Prioritet 1** uz očuvanje SMB i legacy HTTP fallback-a.

---

## 3. Posledice

- **Pozitivne:** Eliminisana duplikacija drajvera između modela i izvora; nemogućnost degradacije verzije drajvera; centralizovana kontrola sa Web Cockpita i podrška čak i za generičke desktop ploče po HWID-u.
- **Operativni zahtev pri produkcijskom cutover-u:** Pri objavi na TrueNAS serveru biće neophodan rebuild `servis-hub` Docker imidža kako bi kontejner dobio `p7zip-full` i `cabextract` pakete iz ažuriranog `Dockerfile`-a, kao i pokretanje migracije postojećih `.tar`/`.cab` arhiva u Pool.
