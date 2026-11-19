---
title: "ADR-0011: Hibridni Batch Download i Delivery Optimization LAN Peering za Windows Update"
type: decision
status: accepted
created: 2026-09-14
updated: 2026-09-14
tags:
  - adr
  - decision
  - windows-update
  - delivery-optimization
  - batch-download
  - lan-peering
---

# ADR-0011: Hibridni Batch Download i Delivery Optimization LAN Peering za Windows Update

## Kontekst i problem
U ranijim verzijama GWADT sistema (prema [[decisions/ADR-0008-wu-com-api-streaming]]), primenjivan je strogo sekvencijalni "Single-Package Streaming" tok: svaki paket je pojedinačno preuziman sa Microsoft Update servera, pa odmah instaliran.

Iako je ovaj pristup obezbeđivao potpunu izolaciju grešaka (kvar jednog drajvera ne blokira ostale), u radioničkim i terenskim uslovima stvarao je značajna uska grla:
1. **Neiskorišćen mrežni protok tokom instalacije:** Dok se paket instalira (što za kumulativne zakrpe, grafičke drajvere ili .NET traje između 3 i 8 minuta uz visoko opterećenje diska i procesora), mrežni adapter i internet link su stajali potpuno neiskorišćeni (0% protoka).
2. **Podrazumevani Windows Update i Delivery Optimization throttling:** Čist Windows podrazumevano ograničava brzinu pozadinskog preuzimanja na ~45-50% dostupnog propusnog opsega.
3. **Izostanak lokalnog keširanja u servisu:** Kada se istovremeno instalira više računara u istoj radionici, svaki računar je iznova preuzimao identične gigabajtne kumulativne zakrpe (LCU) sa interneta, gušeći spoljnu vezu servisa.
4. **Defender I/O zagušenje:** Tokom instalacije Servicing Stack-a (`TiWorker.exe` i `TrustedInstaller.exe`), raspakuje se na desetine hiljada sitnih manifesta i fajlova u `C:\Windows\SoftwareDistribution`, pri čemu Defender real-time zaštita proverava svaki fajl u letu.

## Razmatrane opcije
- **Opcija 1: Potpuni Batch Download i Batch Install.**
  - Preuzeti sve odjednom i instalirati sve odjednom u jednoj `UpdateColl` kolekciji.
  - *Mane*: Gubi se izolacija grešaka — ako jedan drajver proizvođača ne uspe, celokupna instalaciona sesija pada i abortira preostale pakete.
- **Opcija 2: Zadržavanje sekvencijalnog Single-Package pristupa bez promena.**
  - *Mane*: Vreme trajanja ažuriranja ostaje nepotrebno dugo (20–40 minuta).
- **Opcija 3: Hibridni "Batch Download → Single-Package Isolated Install" uz Delivery Optimization LAN Peering.**
  - *Prednosti*: Preuzimanje svih paketa odjednom u punoj paraleli maksimalnim protokom mreže; potom pojedinačna instalacija svakog paketa uz live dashboard praćenje, štopericu, HRESULT logovanje i fallback preuzimanje ako pojedinačni paket nije stigao u batch-u.

## Odluka
Izabrana je **Opcija 3: Hibridni Batch Download sa Single-Package instalacijom i Delivery Optimization optimizacijama**:

1. **Uklanjanje ograničenja brzine i aktivacija LAN Peering-a (`Optimize-WUPerformance`):**
   - Konfigurisanje polise `HKLM:\SOFTWARE\Policies\Microsoft\Windows\DeliveryOptimization`:
     - `DODownloadMode = 1` (LAN Peering — računari na istoj podmreži dele zakrpe gigabitnom brzinom do 110 MB/s).
     - `DOMaxForegroundDownloadBandwidth = 0` (Neograničeno / 100% linka).
     - `DOMaxBackgroundDownloadBandwidth = 0` (Neograničeno / 100% linka).
     - `DOAbsoluteMaxDownloadBandwidth = 0`.
   - Pokretanje i automatski start servisa `DoSvc`, `wuauserv`, `bits`, `UsoSvc`.
2. **Batch Preuzimanje u `GWADT.Update.psm1`:**
   - Pre početka instalacione petlje, formira se `UpdateColl` sa svim nepreuzetim stavkama.
   - Poziva se `$BatchDownloader.Download()` koji preuzima sve pakete odjednom.
3. **Pojedinačna robusna instalacija (Single Install sa Fallback-om):**
   - Petlja prolazi kroz pakete jedan po jedan.
   - Ako neki paket izuzetno nije preuzet u batch prolazu, izvršava se pojedinačni fallback download.
   - Svaki paket se instalira u izolovanoj sesiji uz beleženje trajanja i koda (`WU_PACKAGE_RESULT`).
4. **Procesorska i I/O akceleracija:**
   - Privremeno aktiviranje "High Performance" power profila (`powercfg -setactive 8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c`) tokom Faze 2.
   - Dodavanje privremenih izuzetaka u Windows Defender za procese `TiWorker.exe`, `TrustedInstaller.exe` i direktorijum `C:\Windows\SoftwareDistribution`.

### Posledice
- **Pozitivne**:
  - Vreme Faze 2 skraćeno za 35–60% na prosečnoj internet vezi, a višestruko na lokalnoj servisnoj mreži gde je više mašina aktivno.
  - Mreža i CPU više ne čekaju naizmenično u praznom hodu.
  - Zadržana 100% stabilnost i otpornost na greške loših drajvera.
  - Zadržan detaljan vizuelni progres i tačna dijagnostika u logovima.
- **Negativne / Zahtevi**:
  - Zahteva stabilan protok tokom batch faze (obezbeđeno proverom interneta i mrežnih adaptera u Fazi 1).

## Povezane beleške
- Glavni MOC: [[index]]
- Arhitektura Windows Update-a: [[architecture/windows-update]]
- Prethodna odluka o COM API-ju: [[decisions/ADR-0008-wu-com-api-streaming]]
- Evidencija zamki: [[memory/gotchas]]
