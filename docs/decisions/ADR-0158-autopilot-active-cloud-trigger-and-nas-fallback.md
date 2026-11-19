---
title: "ADR-0158: Aktivna Cloud ZTD Provera Autopilota, Eliminacija Lažno Čistog Stanja i NAS SMB Fallback"
type: decision
status: accepted
date: 2026-10-19
author: wis_orchestrator
tags:
  - decision
  - autopilot
  - ztd
  - intune
  - cloud
  - nas-fallback
---

# ADR-0158: Aktivna Cloud ZTD Provera Autopilota, Eliminacija Lažno Čistog Stanja i NAS SMB Fallback

## 1. Kontekst i Problem

Nakon puštanja u rad taba „🛡️ Autopilot“ u sklopu klijentskog interfejsa `WIS.UI.exe`, uočena su tri kritična arhitektonska i operativna problema u podsistemu za proveru Microsoft Autopilot / Intune statusa:

1. **Lažno čisto stanje (`False Clean / False Negative`):**
   - Microsoft Autopilot registracija čuva se u ZTD bazi (`ztd.dds.microsoft.com`) na osnovu 4K hardverskog TPM heša, a provera se u Windows-u fabrički odvija isključivo tokom Out-of-Box Experience (OOBE) ekrana.
   - Pošto WIS vrši brzu, automatizovanu instalaciju preko `autounattend.xml` (gde se OOBE ekran preskače i odmah kreira lokalni korisnik), Windows tokom instalacije **uopšte nije kontaktirao Microsoft ZTD oblak**.
   - Posledično, na sveže formatiranom disku nije postojao `AutopilotDDSZTDFile.json` niti registarski zapisi, pa je prethodna logika dodeljivala status `CLEAN` („100% ČISTO / SLOBODAN“). Računar koji je u stvarnosti vezan za korporativni Intune zakupac proglašavan je bezbednim za prodaju.
2. **Krhko jednolinijsko parsiranje u C# UI-ju:**
   - `ExecuteAutopilotScanWork()` u `MainWindow.xaml.cs` očekivao je da celokupan JSON stigne u jednoj jedinoj liniji stdout-a (`line.StartsWith("{") && line.EndsWith("}")`). Bilo kakvo CLR upozorenje, prelom linije ili tekst pre JSON-a dovodio je do odbacivanja izlaza i statusa „GREŠKA PRI SKENIRANJU“.
3. **Nedostatak skripte na sistemu bez instalacionog USB-a:**
   - Ukoliko računar nema priključen instalacioni USB i `Provera_autopilota.ps1` nije unapred bila u `C:\setup`, UI nije mogao da pokrene proveru, iako je na lokalnoj servisnoj mreži stalno dostupan TrueNAS server (`192.168.170.254`).

---

## 2. Doneta Odluka

Implementiran je sveobuhvatan, stabilan i višenivonski podsistem za aktivnu proveru:

### 1. Aktivno buđenje Windows OOBE ZTD podsistema u pozadini (100% Tiho / Headless)
U skripti `Provera_autopilota.ps1`:
- Proverava se mrežni izlaz ka `ztd.dds.microsoft.com:443`.
- **Ultra-Fast Path:** Pretežno se očitava živi Windows Event Log (`Microsoft-Windows-ModernDeployment-Diagnostics-Provider/Autopilot`). Ukoliko događaj `806` (zaključan) ili `807` (`ZtdDeviceIsNotRegistered`) već postoji, presuda se donosi za `< 0.3s`.
- **Aktivni Cloud Trigger:** Ako nema zabeleženih događaja, skripta u pozadini bez ikakvog iskakanja prozora pokreće Windows Provisioning Autopilot zadatke (`\Microsoft\Windows\Management\Provisioning\*`), šalje upit ka ZTD serveru i ispituje generisani `AutopilotDDSZTDFile.json`.

### 2. Nova 5-Stupanjska Deterministička Matrica
Potpuno je ukinuto pretpostavljanje da je nepostojanje fajla dokaz o slobodnom hardveru:
- `LOCKED_TENANT` (🔴 Kritično): Potvrđeno zaključan i u oblaku i na disku.
- `HIDDEN_LOCK` (🔴 Kritično): Lokalni disk je čist (formatiran), ali je Microsoft ZTD oblak vratio profil tenanta.
- `RELEASED_LOCAL_TRACES` (🟡 Upozorenje): Postoje stari registarski tragovi, ali je ZTD oblak potvrdio slobodu (Event 807).
- `CONFIRMED_CLEAN` (🟢 100% Čisto): Microsoft ZTD oblak je eksplicitno vratio potvrdu da hardver nije registrovan (Event 807).
- `UNVERIFIED_CLEAN` (🔵 Uslovno čisto / Tirkizno): Lokalni disk je čist, ali Microsoft ZTD oblak nije dao eksplicitan odgovor tokom ove sesije. UI jasno savetuje servisera da pre prodaje računara proveri OOBE ekran ukoliko je mašina otkupljena iz firme.

### 3. C# WPF Otpornost i TrueNAS SMB Fallback
U `UI/MainWindow.xaml.cs`:
- **`StringBuilder` bafer:** Sav stdout se akumulira i bezbedno izoluje JSON blok između prvog `{` i poslednjeg `}`, neutrališući prelom linija i CLR warninge.
- **Produženi timeout:** Povećan sa 45s na 60s uz automatski `Kill()` osigurač.
- **NAS SMB Fallback:** `FindAutopilotScript()` automatski pokušava povezivanje na `\\192.168.170.254\WIS` i `\\truenas.servis\WIS` uz servisne akreditive (`admin:<SMB_PASSWORD>`), automatski preuzima najnoviju skriptu i trajno je kešira u `C:\setup`.
- **Dvojezična lokalizacija:** Dodati srpski i kineski prevodi za nove statuse u `LocalizationService.cs`.

---

## 3. Posledice Odluke

### Pozitivne posledice:
1. **Eliminacija lažno čistih rezultata:** Tehničari više ne mogu greškom pustiti u prodaju računar koji je zaključan u Microsoft Intune bazi.
2. **Transparentnost:** Razlika između računara za koji je Microsoft eksplicitno rekao da je slobodan (`CONFIRMED_CLEAN`) i računara gde upit nije prošao (`UNVERIFIED_CLEAN`) je vizuelno i semantički jasna.
3. **Visoka pouzdanost i brzina:** Direktno čitanje živog loga štedi 30+ sekundi čekanja na većini mašina, dok `StringBuilder` bafer eliminiše greške u parsiranju.
4. **Nezavisnost od fleša:** Računari povezani na lokalnu servisnu mrežu uvek automatski dobijaju skriptu sa NAS-a.

---

## 4. Povezane Beleške
- [[../index]]
- [[../architecture/overview]]
- [[ADR-0020-three-tier-autopilot-mdm-detection]]
- [[ADR-0141-canonical-signed-release-and-status-contract]]
