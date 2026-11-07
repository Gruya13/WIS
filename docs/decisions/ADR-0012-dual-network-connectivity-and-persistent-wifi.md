---
title: "ADR-0012: Mrežni Dual-Homing (LAN + Wi-Fi), TUI Indikatori i Trajno Čuvanje Wi-Fi Profila"
type: decision
status: accepted
date: 2026-09-14
author: gwadt_orchestrator
tags:
  - decision
  - network
  - wifi
  - dual-homing
  - tui
---

# ADR-0012: Mrežni Dual-Homing (LAN + Wi-Fi), TUI Indikatori i Trajno Čuvanje Wi-Fi Profila

## Context (Kontekst)
Tokom procesa servisiranja i automatizovane instalacije računara:
1. U dosadašnjoj implementaciji funkcija `Connect-ServisWifi` imala je preuranjeni prekid `if ($hasNet) { return $true }`. Kada je računar bio povezan LAN mrežnim kablom, proces konfiguracije Wi-Fi mreže se preskakao. To je onemogućavalo klijentu ili serviseru da predefinisani Wi-Fi profil bude unet i zapamćen ako je računar bio uštekan na kabl.
2. Serviseri su zahtevali mogućnost istovremenog povezivanja (Dual-Homing: LAN mrežni kabl + Wi-Fi) kako bi oba mrežna interfejsa bila aktivna i verifikovana tokom automatizacije.
3. TUI dashboard je u statusnoj liniji 4 prikazivao samo jedan generički string `Net: [...]` koji nije razlikovao stanje kabla od bežične mreže niti prikazivao tačnu vezu i brzinu.
4. Korisnik je eksplicitno definisao zahtev: pri završnom čišćenju i brisanju foldera `C:\setup` u Fazi 5, **sistemski zapamćene Wi-Fi mreže se NE SMEJU brisati iz Windowsa**, kako bi računar nakon isporuke i restarta ostao trajno konfigurisan za servisnu/klijentsku Wi-Fi mrežu.

## Decision (Odluka)
1. **Mrežni Dual-Homing i Uklanjanje Blokade:**
   - Iz funkcije `Connect-ServisWifi` uklonjen je uslov koji prekida izvršavanje ukoliko je LAN aktivan.
   - Ukoliko je Wi-Fi adapter prisutan, skripta uvek uvozi servisne profile iz `Wifi-Servis.ini` preko `netsh wlan add profile filename="..." user=all` (trajni unos za sve korisnike u Windows WLAN bazu).
   - Skripta pokreće `netsh wlan connect name="$currentSsid"`, omogućavajući da Windows 11 drži paralelnu aktivnu vezu i na gigabitnom Ethernet kablu i na bežičnoj mreži.
2. **Nova funkcija `Get-NetworkDetailStatus`:**
   - U modul `GWADT.Network.psm1` ugrađena je funkcija `Get-NetworkDetailStatus` koja kroz `Get-NetAdapter` i `netsh wlan show interfaces` pruža granularne podatke:
     - `LanPresent`, `LanConnected`, `LanName`, `LanSpeed`
     - `WifiPresent`, `WifiConnected`, `WifiSSID`, `WifiSignal`, `WifiAdapterName`
     - `InternetActive`, `BothConnected` (Dual-Homing fleg).
3. **Zasebni TUI Indikatori u `GWADT.Core.psm1`:**
   - U liniji 4 zaglavlja konzole uvedeni su zasebni indikatori:
     `Kabl: [* 1 Gbps]  Wi-Fi: [* Skorojevici 5-5g]  AP: [* CISTO]  SB: [* ON]  BitLock: [o OFF]`
   - U panelu Faze 1 (`STATUS MREZE I POVEZIVANJA`) prikazuju se tri zasebna statusa:
     - `Mrezni kabl: [OK] POVEZAN (1 Gbps) - Naziv adaptera`
     - `Wi-Fi mreza: [OK] POVEZAN NA: 'SSID' (Signal: 95%) - Naziv adaptera`
     - `Internet: [OK] DUAL-HOMING AKTIVAN! (LAN Kabl + Wi-Fi rade istovremeno)`
4. **Trajno Čuvanje Wi-Fi Profila u Fazi 5 (`GWADT.Cleanup.psm1`):**
   - Garantuje se da se ni pod kojim uslovima ne poziva `netsh wlan delete profile`.
   - Brišu se isključivo privremeni fajlovi na disku (`C:\setup\Wifi-Servis.ini` i privremeni XML fajlovi) radi zaštite servisnih tajni pre uklanjanja `C:\setup`.
   - Sistemski WLAN profili ostaju trajno zabeleženi u Windowsu (`user=all`), što obezbeđuje automatsko povezivanje i nakon finalnog restarta.

## Consequences (Posledice)
- **Pozitivne:**
  - Serviser u realnom vremenu vidi tačan status oba adaptera, brzinu LAN linka i jačinu Wi-Fi signala.
  - Oba adaptera mogu raditi istovremeno (maksimalna mrežna redundansa i testiranje oba kontrolera pre predaje).
  - Wi-Fi profil ostaje sačuvan u operativnom sistemu, rešavajući korisnički zahtev o perzistentnosti bežične mreže.
  - Održana 100% kompatibilnost sa Windows PowerShell 5.1 i uspešno položen celokupan QA test suite (8/8 testova).
