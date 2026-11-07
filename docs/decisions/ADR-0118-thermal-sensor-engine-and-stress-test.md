---
title: "ADR-0118: Integracija Senzorskog Telemetrijskog Motora (LibreHardwareMonitorLib + WMI Fallback), 30s Stres Testa i Semafora Hlađenja"
type: decision
status: accepted
date: 2026-10-02
author: gwadt_docs_sync / agency-ops-administrator / gwadt_orchestrator
tags:
  - decision
  - adr
  - thermal-sensors
  - hardware-telemetry
  - stress-test
  - librehardwaremonitor
  - wmi-fallback
  - fan-rpm
  - cooling-verdict
  - v2.9.0
---

# ADR-0118: Integracija Senzorskog Telemetrijskog Motora (LibreHardwareMonitorLib + WMI Fallback), 30s Stres Testa i Semafora Hlađenja

## 1. Kontekst i Problem

Nakon uspešne automatizacije instalacije operativnog sistema Windows 11 24H2, implementacije Thin-Client modela, dinamičkog ServisHub streaming-a i automatske provere ključnih OEM drajvera (v2.8.1), serviserima u radionici nedostajao je integrisani alat za brzu hardversku termalnu verifikaciju tek sklopljenih ili servisiranih računara (laptopova i desktop mašina).

Specifični izazovi:
1. **Rizik od termalnog gušenja (thermal throttling) i pregrevanja:** Računari sa osušenom termalnom pastom, začepljenim hladnjacima ili neispravnim ventilatorima mogli su proći instalaciju bez primećenog problema, a potom se gasiti kod klijenta pod opterećenjem.
2. **Potreba za spoljnim dijagnostičkim alatima:** Serviseri su morali ručno pokretati glomazne alate treće strane (AIDA64, FurMark, HWMonitor, Cinebench) kako bi opteretili mašinu na kratko i očitali temperature i brzinu ventilatora.
3. **Kompatibilnost sa HVCI / Core Isolation / Windows 11 bezbednošću:** Alati koji koriste nestandardne ili nepotpisane kernel drajvere bivaju blokirani od strane Windows Defender Device Guard / Core Isolation zaštite ili izazivaju BSOD.
4. **Strogi zahtevi okruženja:** Rešenje mora funkcionisati unutar .NET Framework 4.8 / C# 5.0 okruženja, kompajlirati se bez upozorenja kroz Wine MSBuild na Linuxu i trošiti minimalno resursa kada test nije aktivan.

---

## 2. Donete Odluke

### 2.1. Hibridni Senzorski Motor (`ThermalSensorEngine.cs`)
Implementiran je dvoslojni senzorski podsistem koji implementira `IDisposable`:
- **Primarni sloj (LibreHardwareMonitorLib v0.9.3 + HidSharp v2.1.0):**
  - Pristup hardverskim senzorima direktno preko biblioteke otvorenog koda prilagođene savremenom hardveru (Intel Core Ultra, AMD Ryzen 7000/8000/9000, NVIDIA RTX 40/50, Radeon RX 7000).
  - Bezbedna inicijalizacija `Computer` objekta u `try/catch` bloku (`IsLhmActive = true/false`), čime se garantuje da bilo kakva restrikcija drajvera ili HVCI izolacija ne može oboriti proces.
  - Reaktivno očitavanje: CPU (Package, Core Max, prosek jezgara), GPU (Core, Hotspot), Fan RPM (brzina obrtaja ventilatora > 0), SSD (temperatura diskova).
- **Sekundarni sloj (WMI / ACPI Fallback):**
  - Ukoliko LHM nije dostupan ili je blokiran, motor automatski prelazi na WMI upite:
    * CPU: `root\wmi:MSAcpi_ThermalZoneTemperature` (konverzija iz deci-Kelvina: `(CurrentTemperature - 2732) / 10`), sa fallback-om na `Win32_PerfFormattedData_Counters_ThermalZoneInformation`.
    * SSD: SMART upiti preko `root\wmi:MSStorageDriver_ATAPISmartData` i `root\microsoft\windows\storage:MSFT_PhysicalDisk`.
    * Ventilatori: `root\cimv2:Win32_Fan` (`DesiredSpeed`).
- **Determinističko oslobađanje resursa:**
  - Metoda `Dispose()` bezbedno zatvara i nulira sve LHM i WMI ručke (`computer.Close()`).

### 2.2. Nova Kartica 7: Termalni Status & Senzori (WPF)
Na tabu dijagnostike u `MainWindow.xaml` integrisane su četiri stilizovane kartice u tamnoj temi radionice:
- **CPU Temperatura:** Trenutno očitavanje sa dinamičkom semafor bojom:
  * Zelena (`#4EC9B0`): `< 70°C`
  * Žuta (`#F1C40F`): `70°C – 85°C`
  * Crvena (`#E74C3C`): `> 85°C`
- **GPU Temperatura:** Očitavanje grafičkog čipa sa istom semafor logikom.
- **SSD Temperatura:** Očitavanje NVMe/SATA diska (`< 50°C` zeleno, `50-65°C` žuto, `> 65°C` crveno).
- **Ventilatori (Fan RPM):** Prikaz obrtaja ventilatora ili status "Auto / N/A" ukoliko hardver nema tacho senzor.
- **Status senzora:** Istaknuti bedž koji prikazuje aktivan izvor (`LHM Active` ili `WMI Fallback`).

### 2.3. Nova Kartica 8: 30-Sekundni Termalni Stres Test i Semafor Presuda
- **Multi-Core Stres Test Logika:**
  - Detekcija broja logičkih procesora preko `Environment.ProcessorCount`.
  - Pokretanje radnih niti preko `Task.Factory.StartNew` koje vrše matematičke kalkulacije (trigonometrija i floating-point operacije) u punom kapacitetu.
  - Vremenski ograničeno na tačno 30 sekundi (60 iteracija sa uzorkovanjem na 500 ms).
- **Sigurnosni Mehanizam Emergency Abort (98°C):**
  - Ukoliko tokom testa bilo koje jezgro dostigne ili pređe **98°C**, test se trenutno nasilno prekida (`EmergencyAbortTriggered = true`).
  - Oglašava se zvučno upozorenje (`SystemSounds.Exclamation.Play()`).
  - Korisniku se ispisuje crveni alarm sa hitnom preporukom za gašenje i zamenu termalnih podloga/pasti.
- **Trostepeni Semafor Presude Hlađenja:**
  1. 🟢 **ODLIČNO HLADENJE (< 80°C):** Boja `#4EC9B0`. Hladnjak i protok vazduha su besprekorni. Računar je spreman za isporuku.
  2. 🟡 **POVIŠENA TEMPERATURA / PREVENTIVNI SERVIS (80°C – 95°C):** Boja `#F1C40F`. Preporučuje se čišćenje prašine ili zamena termalne paste u dogledno vreme.
  3. 🔴 **KRITIČNO PREGREVANJE / HITAN SERVIS (>= 95°C ili Emergency Abort):** Boja `#E74C3C`. Računar throtluje ili je prešao 98°C. Neophodan hitan servis rashladnog sistema pre isporuke.

### 2.4. Strogi C# 5.0 Standard Koda
Zbog kompatibilnosti sa Mono / Wine MSBuild 4.0 kompajlerom na Linux build agentu, zabranjeno je korišćenje C# 6.0+ konstrukcija:
- Nema interpolacije stringova `$"..."` -> koristi se `string.Format(...)`.
- Nema `nameof(...)` -> koriste se literalni stringovi.
- Nema null-conditional operatora `?.` -> koristi se eksplicitna provera `if (obj != null)`.

---

## 3. Posledice i Koristi

1. **Ušteda vremena u radionici:** Serviser dobija kompletnu sliku hlađenja i stabilnosti računara za samo 30 sekundi, direktno unutar GWADT.UI bez instaliranja ikakvih eksternih alata.
2. **Bezbednost hardvera:** Emergency Abort na 98°C garantuje da nijedan klijentski procesor neće pretrpeti toplotni šok ili nekontrolisano gašenje tokom testa.
3. **Pouzdana telemetrija:** Podrška za LHM i WMI omogućava rad i na najmodernijim mašinama i na starijim BIOS/legacy konfiguracijama.
4. **Automatizovan OTA Deployment:** Integracija sa ServisHub-om omogućava automatsku distribuciju nove verzije na klijente u sekundi preko dinamičkog `gwadt-payload.zip`.

---

## 4. Verifikacija
- Automatizovani testovi: `Test-Task118-Thermal-QA.py` (70/70 testova, 100% PASS).
- Statička analiza koda: `Verify-CSharpSyntax.py` (21/21 PASS), `Verify-PowerShellSyntax.py` (11/11 PASS).
- Kompilacija: Wine MSBuild Release build bez ijedne greške ili upozorenja.
- Binarni SHA-256 heš: `8dc60430dee3aa7843722e7f8e1c11a16531ab4f51c1736afa8fc585b1c75b8d`.
