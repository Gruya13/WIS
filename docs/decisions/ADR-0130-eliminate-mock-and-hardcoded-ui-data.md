---
title: "ADR-0130: Eliminacija Mockovanih i Hardkodovanih Telemetrijskih Podataka iz WPF Interfejsa (WIS.UI & GWADT.UI)"
---

# ADR-0130: Eliminacija Mockovanih i Hardkodovanih Telemetrijskih Podataka iz WPF Interfejsa (WIS.UI & GWADT.UI)

## 1. Kontekst i Problem

Tokom detaljne arhitektonske revizije korisničkog interfejsa (**WIS.UI** i **GWADT.UI** v2.14.0) pod režimom `/advisor`, uočeno je postojanje preostalih mockovanih, hardkodovanih i statički definisanih podataka koji su mogli dovesti servisere u zabludu:

1. **Hardkodovana temperatura SSD/HDD-a:**
   - U `MainWindow.xaml.cs` (oko linije 2655), temperatura skladišta je bila trajno upisana kao `"Normalna (< 45°C)"` umesto stvarnog očitavanja sa senzora.
2. **Lažni radni sati diska i mešanje sa TBW-om:**
   - Radni sati su se aproksimirali statičkim tekstom `"Radni sati: U redu"` ili su bili prepisivani iz TBW procene, bez jasnog razdvajanja S.M.A.R.T. atributa `Power_On_Hours` (ID 9 za ATA/SATA i `power_on_hours` za NVMe).
3. **Statički mrežni status i izvor distribucije u Kartici 1 (Pregled):**
   - Tekst statusa mreže bio je čvrsto vezan u XAML-u za statički lokalizacioni string `{Binding Loc[NetServiceWifiAutoText]}`, ignorišući realno stanje veze (Ethernet kabl, Wi-Fi ili potpuni nedostatak mreže).
   - Izvor instalacionih paketa bio je fiksiran na statički string `{Binding Loc[NetDistributionSourceValue]}`.
4. **Obmanjujući prikaz baterije na Desktop računarima:**
   - Na računarima bez baterije (`hasBattery == false`), interfejs je prikazivao 100% zelen status, a `BatteryBar` je bio označen kao uspešno instaliran/aktivan, umesto da se jasno stavi do znanja da je uređaj fiksno napajan (Desktop) i da je baterijska aplikacija preskočena.
5. **Hardkodovan broj verzije u zaglavlju (Header):**
   - U `MainWindow.xaml` stajao je fiksni tekst `Text="WIS v2.13.0"` umesto dinamičkog vezivanja na verziju aplikacije.
6. **Aproksimacija nits osvetljenja ekrana bez jasne naznake:**
   - Procenjene vrednosti za nits (dobijene Shoelace formulom iz EDID hromatskih koordinata) nisu imale transparentnu oznaku da se radi o proceni.

---

## 2. Arhitektonska Odluka

Usvojen je sveobuhvatan paket izmena u WPF MVVM C# 5.0 jezgru i PowerShell telemetrijskom modulu kojim se svi mockovani podaci trajno zamenjuju realnim i transparentnim podacima:

### 2.1. Prava SSD/HDD Temperatura i S.M.A.R.T. Radni Sati
- **Senzorska temperatura:** Uklonjen je string `"Normalna (< 45°C)"`. Temperatura skladišta je povezana sa `_thermalEngine.SsdTemperature`. U metodi `UpdateThermalTelemetry()` u realnom vremenu se proverava očitana vrednost: ukoliko je `ssd > 0`, prikazuje se tačna temperatura (`"{0:F1} °C"`), a ukoliko senzor nije dostupan, prikazuje se pošteni fallback `"N/A (Senzor nedostupan)"`.
- **PowerOnHours telemetrija:**
  - U `WIS.Hardware.psm1` (funkcija `Get-HardwareHealthStatus` i `Get-HardwareDiagnosticsInfo`), iz `smartctl.exe` JSON izlaza očitava se `power_on_hours` (NVMe log) ili ID 9 `Power_On_Hours` (ATA/SATA tabela).
  - Ukoliko `smartctl.exe` nije dostupan, status zdravlja se eksplicitno označava kao `IsSmartEstimated = $true` ("(WMI bazična provera)").
  - U `Update-ServisUI` (`WIS.UI.psm1`), polja `power_on_hours` i `is_smart_estimated` se propagiraju u IPC dijagnostički payload.
  - U `MainWindow.xaml.cs`, radni sati se očitavaju iz JSON-a i formatiraju kao `"{0:N0} h"`, dok se u odsustvu S.M.A.R.T.-a prikazuje pošteni fallback `"N/A (Zahteva S.M.A.R.T.)"`.

### 2.2. Dinamički Status Mreže i Izvora Distribucije
- U `Models.cs` (`MainViewModel`) dodata su nova reaktivna svojstva:
  - `ConnectionStatusText` (dinamički opis mreže)
  - `ConnectionStatusBrush` (dinamička boja indikatora)
  - `DistributionSourceText` (izvor instalacionih paketa)
  - `AppVersionText` (dinamički tekst verzije u zaglavlju)
- U `MainWindow.xaml.cs` (metoda `UpdateDiagMetrics()`):
  - Ako je detektovan Ethernet: `"✓ Ethernet (LAN) aktivan"` uz zeleni indikator.
  - Ako je detektovan Wi-Fi: `"✓ Wi-Fi konekcija aktivna"` uz zeleni indikator.
  - Ako nema veze: `"⚠ Nema mrežne veze"` uz žuto-narandžasti indikator.
- U `MainWindow.xaml` izvršena je zamena statičkih bindinga novim dinamičkim svojstvima.

### 2.3. Ispravan Prikaz Baterije za Desktop Uređaje
- Kada računar nema bateriju (`hasBattery == false`):
  - `BatteryHealth` se postavlja na `0` (umesto lažnih 100%).
  - `BatteryHealthBrush` se postavlja na neutralnu sivu boju (`#646464`).
  - `BatteryHealthText` dobija status `"Fiksno napajanje (Desktop)"`.
  - `IsLaptop` se postavlja na `false`.
- U `InitializeDefaultApps()` (`Models.cs`):
  - Ukoliko uređaj nije laptop ili nema bateriju, stavka `BatteryBar` se automatski inicijalizuje sa statusom `SKIPPED` i detaljem `"Nije primenjivo na desktopu"`.

### 2.4. Transparentnost Procene Osvetljenja (Nits)
- U metodi `GetEdidDisplayDetails()` dodata je eksplicitna oznaka procene: `~{0} nits (Procenjeno)` ili `"Standardno (~250-300 nits)"` ukoliko EDID ne sadrži specifične parametre.

### 2.5. 100% Simetrija i Binarni Paritet
- Sve izmene su primenjene 100% identično u `Skripte/WIS.UI` i `Skripte/GWADT.UI`.
- Kompilacija u Docker Wine MSBuild okruženju (`gwadt-builder-wine:latest`) donosi binarce veličine tačno **365,568 B**.
- Distribucija je izvršena na svih 19 projektnih i eksternih lokacija uz 100% SHA-256 binarni paritet.

---

## 3. Posledice Odluke

### Pozitivne:
- **Poverenje i integritet servisa:** Serviseri u radionici više ne dobijaju fiktivne podatke niti lažni utisak da je hardver ispitan tamo gde senzor ili S.M.A.R.T. nisu dostupni.
- **Automatska adaptacija:** Interfejs se trenutno vizuelno prilagođava tipu uređaja (Desktop vs. Laptop) i mrežnom okruženju (LAN vs. Wi-Fi vs. Offline).
- **Striktna kompatibilnost:** Očuvan je standard C# 5.0 (.NET 4.0/4.8) i validan PowerShell 5.1 AST bez ikakvog tehničkog duga.

### Negativne / Ograničenja:
- Na uređajima gde drajveri nisu instalirani ili S.M.A.R.T. kontroler nije prepoznat, interfejs pošteno prikazuje `"N/A"`, što zahteva pokretanje eksterne dijagnostike ukoliko su radni sati neophodni za procenu.

---

## 4. Verifikacija (Definition of Done)

- **Statička analiza:**
  - `Verify-CSharpSyntax.py`: 43/43 PASS (100% C# 5.0).
  - `Verify-PowerShellSyntax.py`: 0 grešaka (100% validan AST).
- **QA Verifikacija (`Skripte/Test-Task130-Eliminate-Mock-UI-Data-QA.py`):**
  - Ukupno testova: **63/63 PASS (100.0%)**, Exit Code 0.
- **Binarni paritet na 19 lokacija:**
  - `WIS.UI.exe` (9 lokacija, 365,568 B): `a0c759bed204d6a3d7b54f184fc0597b44d11459859e70a7cab2c1f41971dbfe`
  - `GWADT.UI.exe` (10 lokacija, 365,568 B): `90c48902a1a06960d27d52b98f438c65a0cf1ad584ca11430db1cbdb921a742d`
