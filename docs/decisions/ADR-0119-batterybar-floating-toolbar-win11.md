---
title: "ADR-0119: Prilagođavanje BatteryBar-a za Windows 11 (Ukidanje DeskBand-a, Prelazak na Nativni XML Floating Toolbar, Startup Integracija i Eliminacija Skrivenog Režima)"
type: decision
status: accepted
date: 2026-10-02
author: gwadt_docs_sync / agency-ops-administrator / gwadt_orchestrator
tags:
  - decision
  - adr
  - batterybar
  - floating-toolbar
  - windows-11
  - deskband-deprecation
  - xml-settings
  - startup-shortcut
  - v2.9.1
---

# ADR-0119: Prilagođavanje BatteryBar-a za Windows 11 (Ukidanje DeskBand-a, Prelazak na Nativni XML Floating Toolbar, Startup Integracija i Eliminacija Skrivenog Režima)

## 1. Kontekst i Problem

Tokom automatizovanog postavljanja prenosivih računara (laptopova) u okviru Faze 4 (Aplikacije) GWADT sistema, servisna praksa je decenijama unazad koristila **BatteryBar** (v3.6.6) kao standardni alat za prikaz detaljnih parametara baterije (procenat, kapacitet u mWh, stepen istrošenosti baterije / battery wear, brzina punjenja/pražnjenja i preostalo vreme rada).

Međutim, na operativnom sistemu **Windows 11** (od verzije 21H2 do 24H2), serviseri su uočili da se BatteryBar uopšte ne prikazuje na traci zadataka niti na radnoj površini nakon završene automatizovane instalacije.

Detaljnom forenzikom i dijagnostikom identifikovana su četiri ključna uzroka ovog problema:

1. **Ukidanje DeskBand API-ja u Windows 11 XAML Shell-u:**
   U verzijama Windows 7, 8.1 i 10, BatteryBar se integrisao kao Explorer traka sa alatkama preko klasičnog COM DeskBand interfejsa (`IDeskBand` / `IDeskBand2`). Windows 11 je u potpunosti redizajnirao Taskbar prebacivši ga na moderni XAML/WinUI podsistem i **trajno ukinuo podršku za DeskBand dodatke**. Bilo kakav pokušaj registracije DeskBand komponente u Windows 11 Exploreru biva tiho ignorisan, čime je klasični prikaz na Taskbaru postao nemoguć bez rizičnih modifikacija sistemskih DLL-ova (npr. ExplorerPatching koji ruši Windows Update i izaziva BSOD).

2. **Zabluda oko INI konfiguracionog formata (`BatteryBar.ini`):**
   Ranije verzije automatizacionih skripti pokušavale su da forsiraju prikaz plivajuće trake generisanjem INI fajla u `%APPDATA%\BatteryBar\BatteryBar.ini` sa sekcijom:
   ```ini
   [Settings]
   FloatingToolbar=True
   ```
   Međutim, reverznim inženjeringom i statičkom inspekcijom izvršnog koda BatteryBar-a utvrđeno je da aplikacija koristi isključivo **XML serijalizaciju** u datoteci `BatteryBar.Settings.xml`. INI fajl `BatteryBar.ini` aplikacija uopšte ne parsira za ove postavke, pa je konfigurisanje preko INI formata bilo potpuno jalovo.

3. **Destruktivan parametar `-WindowStyle Hidden`:**
   U instalacionoj skripti, automatsko pokretanje procesa nakon instalacije bilo je konfigurisano kao:
   ```powershell
   Start-Process -FilePath $installedExe -WindowStyle Hidden
   ```
   Kada se proces pokrene sa zastavicom `-WindowStyle Hidden`, operativni sistem Windows nasilno sakriva sve prozore tog procesa. Kako se Floating Toolbar u Windows 11 prikazuje kao samostalni topmost prozor, ovaj parametar ga je činio trajno nevidljivim za korisnika iako je proces radio u pozadini.

4. **Izolacija korisničkog konteksta i izostanak trajnog Startup mehanizma:**
   Tokom deploymenta (koji se izvršava pod administratorskim ili namenskim instalacionim nalogom), podešavanja nisu bila propagirana u `C:\Users\Default`, pa svaki novi korisnički nalog koji se kreira na laptopu nije imao predefinisane koordinate niti konfigurisan XML. Takođe, nedostajala je globalna Startup prečica u All Users direktorijumu.

---

## 2. Donete Odluke

### 2.1. Prelazak na Nativni XML Floating Toolbar (`BatteryBar.Settings.xml`)
Uvedena je namenska funkcija `Set-BatteryBarConfiguration` unutar modula `Skripte/Modules/GWADT.Apps.psm1`. Umesto manipulacije INI fajlom, generiše se čist, nativni UTF-8 XML šablon direktno kompatibilan sa .NET XML serijalizatorom aplikacije:

```xml
<?xml version="1.0" encoding="utf-8"?>
<settings>
  <BatteryBarSolo>
    <AlwaysOnTop>True</AlwaysOnTop>
    <Location>1550, 980</Location>
    <Lock>True</Lock>
    <CloseToTray>False</CloseToTray>
  </BatteryBarSolo>
</settings>
```

- Čvor `<BatteryBarSolo>` aktivira samostalni Floating Toolbar režim rada.
- Koordinate `<Location>1550, 980</Location>` pozicioniraju plivajući indikator u donji desni ugao ekrana (neposredno iznad Windows 11 sistemskog treja), obezbeđujući prirodno i intuitivno korisničko iskustvo identično traci zadataka.
- `<AlwaysOnTop>True</AlwaysOnTop>` i `<Lock>True</Lock>` garantuju da je indikator uvek vidljiv i zaštićen od slučajnog pomeranja.

### 2.2. Globalna Propagacija na Korisničke Profile i Čišćenje Zaostalog INI Formata
Funkcija `Set-BatteryBarConfiguration` deterministički distribuira XML konfiguraciju:
1. **Default korisnički profil (`C:\Users\Default\AppData\Roaming\BatteryBar`):**
   Garantuje da će svaki novokreirani korisnički nalog (klijent, serviser, gost) automatski naslediti gotov XML profil čim se prvi put prijavi na sistem.
2. **Svi postojeći korisnički profili (`C:\Users\*`):**
   Dinamička enumeracija prolazi kroz sve profile na disku (uz strogo filtriranje sistemskih direktorijuma `Public`, `Default User`, `All Users`), prepisuje `BatteryBar.Settings.xml` u UTF-8 formatu i uklanja zaostali, neispravni `BatteryBar.ini` preko `Remove-Item -Force`.

### 2.3. All Users Startup Prečica (`WScript.Shell`) i HKLM Run Rezerva
Kako bi se obezbedilo automatsko podizanje BatteryBar-a pri svakoj prijavi bilo kog korisnika na sistem:
1. U globalnom All Users Startup direktorijumu (`C:\ProgramData\Microsoft\Windows\Start Menu\Programs\Startup\BatteryBar.lnk`) kreira se prečica ka `BatteryBar.exe` pomoću COM objekta `WScript.Shell`.
2. Implementirano je strogo upravljanje COM resursima: nakon kreiranja prečice obavezno se poziva `[System.Runtime.InteropServices.Marshal]::ReleaseComObject($wshShell)` i `[GC]::Collect()` radi sprečavanja curenja memorije i zaključenih ručki.
3. Kao dodatni sloj zaštite (redundancy), upisuje se HKLM Run ključ:
   `HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run -> BatteryBar = "C:\Program Files (x86)\BatteryBar\BatteryBar.exe"`.

### 2.4. Uklanjanje `-WindowStyle Hidden` i .NET 4.8 Konfiguracioni Fajl
1. Nasilni parametar `-WindowStyle Hidden` je **trajno uklonjen** iz svih poziva `Start-Process` za BatteryBar. Proces se pokreće u standardnom režimu rada, čime se omogućava operativnom sistemu da odmah iscrta Floating Toolbar prozor.
2. Generiše se `BatteryBar.exe.config` u instalacionom direktorijumu sa deklaracijom podrške za .NET Framework 4.8 (`supportedRuntime version="v4.0"`), čime se eliminišu potencijalni CLR dijalozi o nedostajućem .NET 2.0/3.5 na čistom Windows 11 24H2.

### 2.5. Zaštita od Reinstalacionog Modalnog Dijaloga
Ukoliko je BatteryBar već instaliran na sistemu, instalaciona sekvenca preskače pokretanje NSIS instalera kako bi se sprečilo blokiranje procesa kroz nezaobilazni modalni upit *"BatteryBar is already installed"*, osvežava konfiguraciju i podiže postojeći proces ukoliko nije aktivan.

---

## 3. Posledice i Koristi

1. **Besprekoran rad na Windows 11:** BatteryBar radi u potpunosti autonomno bez ikakvih spoljnih hakova za Taskbar, pružajući serviserima i klijentima jasan prikaz statusa baterije.
2. **Pouzdanost i konzistentnost:** Nativni XML format (`BatteryBar.Settings.xml`) eliminiše zavisnost od ignorisanih INI parametara.
3. **Automatsko nasleđivanje:** Podešavanje na `C:\Users\Default` garantuje da kupac laptopa odmah po preuzimanju ima aktivan i pozicioniran indikator.
4. **Stabilnost deploymenta:** Uklanjanje `-WindowStyle Hidden` obezbeđuje trenutnu vidljivost prozora, a oslobađanje COM objekta štiti stabilnost radnog PowerShell procesa.

---

## 4. Verifikacija i Testiranje

- **Automatizovana QA skripta:** `Skripte/Test-Task119-BatteryBar-QA.py` (8 test modula, 40 provera):
  * Test 1: Postojanje funkcije `Set-BatteryBarConfiguration` i `Export-ModuleMember` (PASS)
  * Test 2: XML sintaksa i `ElementTree` validacija šablona (PASS)
  * Test 3: Propagacija na `C:\Users\Default` i dinamički profili (PASS)
  * Test 4: All Users Startup prečica i `ReleaseComObject` oslobađanje (PASS)
  * Test 5: Potpuno uklanjanje `[Settings]` i `FloatingToolbar=True` (PASS)
  * Test 6: Uklanjanje `-WindowStyle Hidden` i .NET 4.8 runtime konfiguracija (PASS)
  * Test 7: SHA-256 binarni paritet modula sa skladištem i repozitorijumom (PASS)
  * Test 8: PowerShell 5.1 AST statička analiza `Verify-PowerShellSyntax.py` (PASS)
- **Rezultat testiranja:** **40/40 PASS (100.0%)**, exit code 0.
- **SHA-256 heš modula:** `23ceef50204b1171be50aa69e7931426266ab458a8b84e16e4fac7bebb3534ec`.
- **Git verzija:** Release tag `v2.9.1` u `gwadt-ui-engine` repozitorijumu.
