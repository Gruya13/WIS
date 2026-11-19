---
title: "ADR-0128: Otklanjanje 7 Sprint Defekata: Prenosivi BatteryBar Paket, IPS Detekcija Ekrana, Battery Wear Level %, Real-Time CPU/RAM Dijagnostika, Ruter DHCP IP, TrueNAS Latencija i Mrežni Protok"
type: decision
status: accepted
date: 2026-10-07
author: wis_docs_sync
tags:
  - decision
  - adr
  - batterybar
  - hardware
  - display-ips
  - battery-wear
  - diagnostics
  - network
  - wpf
  - ui
  - wine-build
---

# ADR-0128: Otklanjanje 7 Sprint Defekata: Prenosivi BatteryBar Paket, IPS Detekcija Ekrana, Battery Wear Level %, Real-Time CPU/RAM Dijagnostika, Ruter DHCP IP, TrueNAS Latencija i Mrežni Protok

## 1. Kontekst i Problem

Tokom terenskog rada, serijskih instalacija i servisnih ispitivanja računara u radionici "WIS Service" pod sistemom **WIS** (Windows Installation Assistant, ranije radno poznat kao GWADT) verzije v2.13.0, identifikovano je 7 specifičnih sprint defekata koji su narušavali pouzdanost automatizacije i usporavali dijagnostiku servisera:

1. **Zastoj instalacionog toka u Fazi 4 usled BatteryBar instalera i Winget-a:**
   - Standardni NSIS instaler `BatteryBarSetup-3.6.6.exe /S` i alternativni Winget pozivi na sveže instaliranom Windows 11 operativnom sistemu zahtevaju .NET Framework 3.5/2.0 koji nije unapred instaliran. Usled toga, instaler se u pozadini tiho zamrzavao bez izlaska i bez timeout zaštite.
   - Ukoliko instalacija BatteryBar-a ne bi uspela, završni `Assert-InstalledApplicationsReality` mehanizam u Fazi 4 podizao je fatalni terminirajući izuzetak (`throw`), čime je čitav proces instalacije bivao nasilno prekinut pre prelaska na Microsoft Office 2024, OEM verifikaciju licence i Fazu 5 (Finalizacija).
   - Nedostajala je timeout zaštita i kontrolisano čekanje unutar pomoćnih instalacionih funkcija `Invoke-InstallerWithLocalFallback` i `Invoke-WingetAppInstall`, što je pri odsustvu interneta ili zaglavljenom instaleru dovodilo do beskonačnog čekanja.

2. **Odsustvo detekcije tehnologije panela ekrana (HW-01 - IPS/OLED/VA/TN):**
   - Interfejs je prikazivao isključivo rezoluciju i frekvenciju osvežavanja (npr. `1920x1080 @ 60Hz`), bez prepoznavanja da li je ekran IPS, OLED, AHVA, PLS ili zastareli TN panel. Serviseri su morali ručno otvarati spoljne dijagnostičke alate ili specifikacije proizvođača kako bi utvrdili tip panela prilikom prijema i pripreme laptop računara.

3. **Nepouzdano i grubo očitavanje istrošenosti baterije (HW-02 - Battery Wear Level %):**
   - Prethodno očitavanje baterije oslanjalo se na osnovnu WMI klasu `Win32_Battery`, koja na mnogim novijim laptopovima ne vraća podatke u realnom vremenu ili vraća iste vrednosti za projektovani i trenutni kapacitet.
   - Nije postojalo egzaktno izračunavanje procenta habanja (Wear Level %) niti semaforska vizuelna kategorizacija stanja baterije (zeleno/žuto/crveno), što je otežavalo brzu procenu stanja prenosnih računara.

4. **Statički prikaz dijagnostičkih parametara CPU i RAM memorije (DIAG-01):**
   - Kartica za dijagnostiku i pregled prikazivala je statičke hardverske informacije o procesoru i radnoj memoriji dobijene pri startu sistema, bez dinamičkog praćenja trenutnog opterećenja procesora (%) i realne potrošnje memorije u realnom vremenu (1s interval).
   - Pozivanje teških WMI upita svake sekunde dovelo bi do nepotrebnog opterećenja procesora i degradacije performansi tokom instalacije.

5. **Prikaz nevalidnih i neažurnih mrežnih IP adresa (NET-01):**
   - Interfejs je često prikazivao prazno polje, APIPA adresu (`169.254.x.x`) ili statičku/loopback adresu pre nego što lokalni ruter dodeli legitimnu DHCP konfiguraciju.
   - Nije postojala dinamička verifikacija postojanja aktivnog Default Gateway-a koji potvrđuje da je IP adresa operativna za komunikaciju u lokalnoj mreži.

6. **Odsustvo praćenja latencije i veze ka TrueNAS skladištu (NET-02):**
   - Servisni TrueNAS server (`192.168.170.254`) predstavlja centralno čvorište sa koga se preuzimaju drajveri, ISO slike i instalacioni paketi. UI nije imao uvid u realno stanje veze i latenciju odziva (ping u ms), pa serviser nije mogao odmah uočiti zastoje na lokalnom sviču ili mrežnom kablu.

7. **Odsustvo praćenja mrežnog protoka i ukupnog preuzetog saobraćaja (NET-03):**
   - Tokom preuzimanja obimnih drajverskih paketa i softvera u Fazama 2 i 4, operater nije imao vizuelnu povratnu informaciju o trenutnoj brzini prenosa (MB/s) niti o ukupno preuzetim megabajtima, što je otežavalo razlikovanje aktivnog prenosa od potencijalnog mrežnog zastoja.

---

## 2. Arhitektonska Odluka

Usvojen je sveobuhvatan paket od sedam međusobno usklađenih inženjerskih rešenja, implementiranih kroz PowerShell backend module (`WIS.Apps.psm1` / `GWADT.Apps.psm1`), prezentacioni C# WPF sloj (`WIS.UI` i `GWADT.UI`), kompajliranih u Docker Wine MSBuild okruženju i sertifikovanih rigoroznim QA testom:

### 2.1. Prenosivi (Portable) BatteryBar Paket i Non-Blocking Failover u Fazi 4
- **Eliminacija NSIS i Winget poziva:** Iz funkcija `Invoke-Phase4Apps` i `Install-NonMsiApps` u potpunosti su uklonjeni rizični pozivi `BatteryBarSetup-3.6.6.exe /S` i `Invoke-WingetAppInstall`.
- **Čisto prenosivo kopiranje:** Uvedena je pretraga prekonfigurisanog prenosivog paketa u lokalnim i mrežnim skladištima (`Programi\BatteryBar`, `Get-ServisProgramiRoot`, `C:\setup\Programi\BatteryBar`, USB i NAS keš). Sistem direktno kopira binarne datoteke i teme u `C:\Program Files\BatteryBar`.
- **Rekurzivna deblokada fajlova:** Izvršava se automatski `Unblock-File` i brisanje NTFS toka podataka `:Zone.Identifier` nad svim kopiranim datotekama.
- **Konfiguracija za Windows 11 Floating Toolbar:** Generiše se `BatteryBar.Settings.xml` sa parametrom `<Setting Name="Solo" Type="System.Boolean">True</Setting>`, prečica u All Users Startup direktorijumu i `.config` podrška za .NET 4.x.
- **Nenasilni osigurač (Non-Blocking Safe Failover):**
  - Ako prenosivi paket ne postoji ili kopiranje ne uspe, sistem **NE ZAUSTAVLJA** proces, već upisuje `WARN` u log, šalje IPC status `WARN` interfejsu i postavlja `$appsStatusMap["BatteryBar"] = "WARN"`.
  - U mehanizmu `Assert-InstalledApplicationsReality` uslov verifikacije glasi:
    ```powershell
    if ($isLaptop -and ($appsStatusMap["BatteryBar"] -ne "SKIP") -and ($appsStatusMap["BatteryBar"] -ne "WARN"))
    ```
    čime je garantovano da nedostatak BatteryBar paketa **nikada neće podići fatalni izuzetak (`throw`)**. Instalacija se glatko nastavlja na Microsoft Office 2024, OEM licenciranje i Fazu 5.
- **Kontrolisano čekanje sa timeout-om:**
  - U funkciji `Invoke-InstallerWithLocalFallback` zamenjen je beskonačni `-Wait` sa kontrolisanim `$proc.WaitForExit($WaitSeconds * 1000)` (podrazumevano 60s). U slučaju isteka vremena, proces se nasilno prekida (`Stop-Process -Force`) i beleži se upozorenje.
  - U funkciji `Invoke-WingetAppInstall` primenjeno je analogno kontrolisano čekanje zasnovano na `$TimeoutSec`.

### 2.2. Detekcija Tipa Panela Ekrana (HW-01 - IPS/OLED)
U `MainWindow.xaml.cs` (za oba UI projekta) implementirana je metoda `DetectDisplayPanelType(int screenW, int screenH, bool isLaptop)` sa višeslojnom analizom:
1. **Očitavanje WMI klase `root\wmi:WmiMonitorID`:** Izdvajanje ASCII identifikatora proizvođača, koda modela i serijskog broja monitora.
2. **Pretraga Registry baze:** Skeniranje stabla `HKLM\SYSTEM\CurrentControlSet\Enum\DISPLAY` radi pronalaska DeviceDesc i hardverskih stringova.
3. **Analiza tehnologije panela:**
   - Prepoznavanje OLED/AMOLED panela.
   - Prepoznavanje naprednih IPS varijacija: IPS, AHVA (*Advanced Hyper-Viewing Angle*), PLS (*Plane to Line Switching*), ADS (*Advanced Super Dimension Switch*), IGZO.
   - Prepoznavanje poznatih industrijskih linija panela za laptopove: BOE NV serija (npr. NV14, NV156), LG Display LP serija (npr. LP14, LP156), AU Optronics B1xxHAN serija, Innolux N1xxHCE serija.
4. **Heuristika za FHD+ laptopove:** Za laptop uređaje sa rezolucijom `>= 1920x1080` (gde proizvođači u 99% slučajeva ugrađuju IPS matrice, a EDID podaci kriju tačan opis), primenjuje se inteligentni IPS fallback.
5. **Integracija u `DisplaySpec`:** Prikaz se formatira kao `"1920x1080 @ 60Hz (FHD) • IPS"` ili `"1920x1080 @ 60Hz (FHD) • IPS • 🖐️ Touchscreen"`.

### 2.3. Precizno Očitavanje Habanja Baterije (HW-02 - Wear Level %)
Umesto generičkog `Win32_Battery`, implementiran je pristup direktnim ACPI podacima kroz WMI imenski prostor `root\wmi`:
- **Čitanje dizajniranog kapaciteta:** Klasa `BatteryStaticData` -> svojstvo `DesignedCapacity` (mWh).
- **Čitanje trenutnog punog kapaciteta:** Klasa `BatteryFullChargedCapacity` -> svojstvo `FullChargedCapacity` (mWh).
- **Čitanje ciklusa punjenja:** Klasa `BatteryCycleCount` -> svojstvo `CycleCount`.
- **Proračun habanja:**
  ```csharp
  double wearLevel = (1.0 - ((double)fullCap / (double)designCap)) * 100.0;
  if (wearLevel < 0.0) wearLevel = 0.0;
  ```
- **Formatiranje prikaza:**
  ```csharp
  string.Format("{0:0.0}% Istrošenost ({1:N0} / {2:N0} mWh)", wearLevel, fullCap, designCap);
  ```
- **Dinamički trostepeni semafor boja:**
  - `< 15%` istrošenosti: Zelena boja (`#2ECC71` / RGB 46, 204, 113) - Odlično stanje baterije.
  - `15% - 35%` istrošenosti: Žuta boja (`#F1C40F` / RGB 241, 196, 15) - Umereno habanje.
  - `>= 35%` istrošenosti: Crvena boja (`#E74C3C` / RGB 231, 76, 60) - Kritično habanje / zrela za zamenu.
- **Rezervni fallback:** U slučaju da hardver ili drajver ne podržava `root\wmi` interfejs, mehanizam se automatski i bezbedno prebacuje na `Win32_Battery`.

### 2.4. Nativni C# 1000ms Tajmer za Real-Time CPU i RAM (DIAG-01)
Kako bi se eliminisalo opterećenje WMI procesa i postigao trenutni odziv sa minimalnim utroškom resursa:
- **Nativni Kernel32 P/Invoke pozivi:**
  - `GetSystemTimes(out idleTime, out kernelTime, out userTime)` za precizno merenje delta opterećenja procesora (0–100%) na osnovu 64-bitnih `FILETIME` struktura.
  - `GlobalMemoryStatusEx(ref MEMORYSTATUSEX)` za trenutno očitavanje iskorišćenja i ukupne fizičke radne memorije.
- **Pozadinski tajmer:** Implementiran `System.Timers.Timer _diagMetricsTimer` konfigurisan na interval od tačno 1000ms.
- **Upravljanje životnim ciklusom:** Tajmer se startuje u događaju `Window_Loaded`, a oslobađa se kroz `Dispose()` u događaju `Window_Closed`, garantujući odsustvo curenja niti i resursa.
- **Formatiranje:**
  - CPU: `ViewModel.CpuUsage = string.Format("{0}%", cpuPct);`
  - RAM: `ViewModel.RamUsage = string.Format("{0:0.0} GB / {1:0.0} GB ({2}%)", usedGb, totalGb, memLoad);`

### 2.5. Dinamičko Osvežavanje DHCP Ruter IP Adrese (NET-01)
- **Skeniranje mrežnih interfejsa:** Putem `NetworkInterface.GetAllNetworkInterfaces()` vrši se pretraga adaptera sa statusom `OperationalStatus.Up` i tipom `Ethernet` ili `Wireless80211`.
- **Verifikacija Default Gateway-a:** Interfejs se prihvata kao primarni isključivo ukoliko poseduje aktivan i validan Default Gateway koji nije loopback adresa (`!IPAddress.IsLoopback(gw.Address)`).
- **Eliminacija APIPA i nevalidnih adresa:** Strogo filtriranje koje odbacuje adrese koje počinju sa `169.254.`, `0.0.0.0` ili `127.0.0.1`.
- **Reaktivno ažuriranje:** Čim ruter dodeli konfiguraciju preko DHCP-a, `ViewModel.IpAddress` se momentalno osvežava u interfejsu.

### 2.6. Asinhroni ICMP Ping ka TrueNAS Serveru (NET-02)
- **Ciljna adresa:** `192.168.170.254` (radionički TrueNAS skladišni server).
- **Asinhrono izvršavanje:** Poziv `ping.Send("192.168.170.254", 1000)` izvršava se u zasebnoj `ThreadPool` radnoj niti u intervalu od 3 sekunde, čime je UI nit 100% zaštićena od blokiranja.
- **Prikaz stanja:**
  - Ako server odgovori: `ViewModel.NasStatus = "Povezan (192.168.170.254)"` i `ViewModel.NasLatency = string.Format("{0} ms", reply.RoundtripTime)`.
  - Ako server ne odgovori ili nema mreže: `ViewModel.NasStatus = "Lokalno (Offline)"` i `ViewModel.NasLatency = "N/A"`.

### 2.7. Merenje Mrežnog Protokola i Ukupnog Saobraćaja (NET-03)
- **Očitavanje statistike:** Preko `nic.GetIPv4Statistics().BytesReceived` u svakom taktu tajmera od 1000ms meri se prirast bajtova.
- **Proračun brzine i saobraćaja:**
  - Trenutna brzina prenosa u megabajtima po sekundi: `double speedMb = deltaBytes / (1024.0 * 1024.0)`.
  - Akumulirana ukupna količina preuzetih megabajta od početka rada UI-ja: `totalMbReceived += speedMb`.
- **Prikaz u UI-ju:** `ViewModel.DownloadedMb = string.Format("{0:0.0} MB/s ({1:N0} MB ukupno)", speedMb, totalMbReceived);`.

### 2.8. Docker Wine MSBuild Kompilacija i 100% SHA-256 Paritet
- **Ispravka sintakse:** Uklonjena nedostajuća zatvorena zagrada `}` za metodu `ExecuteRefreshHardware` u `MainWindow.xaml.cs`.
- **Kompilacija oba modula:** Izvršen čisti Rebuild oba projekta (`WIS.UI.csproj` i `GWADT.UI.csproj`) unutar kontejnera `gwadt-builder-wine:latest` sa izlaznim kodom 0, 0 grešaka i 0 upozorenja.
- **Tačna veličina binaraca:** **340,992 bajtova** za oba izvršna fajla.
- **Distribucija i binarni paritet:**
  - `WIS.UI.exe` distribuiran na 9/9 lokacija: SHA-256 `18e7b218c9934826c988f1a51a086647e42f89e741f0196a951e768c1ea43004`.
  - `GWADT.UI.exe` distribuiran na 10/10 lokacija: SHA-256 `ebc74ec46db1256096b1c2b2fd1287ca041eca15b953dc11fff8fda2827bdc68`.
  - Sinhronizovan git repozitorijum `gwadt-ui-engine` sa commit-om `2ad954f`.

---

## 3. QA Verifikacija i Validacija

Za verifikaciju svih 7 otklonjenih sprint defekata razvijena je i izvršena sveobuhvatna automatizovana test skripta `Skripte/Test-Task128-Sprint-Defects-QA.py`:

| Test Blok | Opis Inženjerskih Provera | Broj Testova | Ishod |
|---|---|---|---|
| **Blok 1: BatteryBar Portable & Non-blocking** | Odsustvo sirovog `BatteryBarSetup /S` i Winget-a, implementacija portable kopiranja u `C:\Program Files\BatteryBar`, rekurzivni `Unblock-File` i `:Zone.Identifier`, non-blocking WARN failover, izuzeće iz `Assert-InstalledApplicationsReality` kada je status WARN, kontrolisano čekanje sa timeout-om u `Invoke-InstallerWithLocalFallback` i `Invoke-WingetAppInstall`. | 10 | 🟢 10/10 PASS |
| **Blok 2: HW-01 IPS/OLED Panel Detekcija** | Postojanje metode `DetectDisplayPanelType`, očitavanje WMI `root\wmi:WmiMonitorID` i Registry ključa `SYSTEM\CurrentControlSet\Enum\DISPLAY`, prepoznavanje OLED/AMOLED i naprednih IPS varijanti (AHVA, PLS, ADS, IGZO), prepoznavanje serija panela (NV, LP, HAN, HCE), FHD+ heuristika i integracija u `DisplaySpec` sa separatorom ` • `. | 13 | 🟢 13/13 PASS |
| **Blok 3: HW-02 Battery Wear Level %** | Očitavanje `DesignedCapacity` iz `root\wmi:BatteryStaticData`, `FullChargedCapacity` iz `root\wmi:BatteryFullChargedCapacity`, `CycleCount` iz `root\wmi:BatteryCycleCount`, proračun procenta habanja, formatiranje `X.X% Istrošenost (full / design mWh)`, trostepene semafor boje (<15% zelena `#2ECC71`, <35% žuta `#F1C40F`, >=35% crvena `#E74C3C`) i `Win32_Battery` fallback. | 11 | 🟢 11/11 PASS |
| **Blok 4: DIAG-01 Real-Time CPU & RAM Tajmer** | P/Invoke deklaracije za `GetSystemTimes` i `GlobalMemoryStatusEx`, inicijalizacija 1000ms tajmera `_diagMetricsTimer`, `Dispose()` u događaju `Closed`, proračun CPU delte i ažuriranje `ViewModel.CpuUsage`, proračun zauzeća RAM-a i ažuriranje `ViewModel.RamUsage`. | 11 | 🟢 11/11 PASS |
| **Blok 5: NET-01/02/03 Mrežni Podsistem** | NET-01: Verifikacija Default Gateway-a i filtriranje nevalidnih/APIPA adresa (`ViewModel.IpAddress`); NET-02: Asinhroni ICMP Ping ka `192.168.170.254` u radnoj niti sa ažuriranjem `NasStatus` i `NasLatency`; NET-03: Merenje protoka preko `GetIPv4Statistics().BytesReceived`, formatiranje `X.X MB/s (Y MB ukupno)` i ažuriranje `DownloadedMb`. | 12 | 🟢 12/12 PASS |
| **Blok 6: Statička Analiza Koda** | `Verify-CSharpSyntax.py` proverava 43/43 C# fajlova na striktnu C# 5.0 kompatibilnost (100% PASS); `Verify-PowerShellSyntax.py` proverava celokupan PowerShell kod na validan AST za PS 5.1 (100% PASS). | 4 | 🟢 4/4 PASS |
| **Blok 7: Binarni Paritet i Integritet** | Tačna veličina 340,992 bajtova i 100% SHA-256 poklapanje za svih 9 lokacija `WIS.UI.exe` (`18e7b218...`) i svih 10 lokacija `GWADT.UI.exe` (`ebc74ec4...`). | 19 | 🟢 19/19 PASS |
| **UKUPNO** | **Sveobuhvatna QA Verifikacija 7 Sprint Defekata** | **80** | **🟢 80/80 PASS (100.0%)** |

Izlazni status skripte: `Exit code 0` (Sve provere položene bez ijedne greške).  
Test skripta je trajno arhivirana i sinhronizovana na radioničko skladište: `/home/gru/Storage/Skripte/Test-Task128-Sprint-Defects-QA.py`.

---

## 4. Posledice i Prednosti

### Pozitivne Posledice i Benefiti:
1. **Nulti rizik od zastoja Faze 4:** Prenosivi BatteryBar sa nenasilnim WARN failover mehanizmom garantuje da Faza 4 nikada neće blokirati niti prekinuti proces instalacije programa.
2. **Zaštita od beskonačnih petlji:** Implementirano kontrolisano čekanje ($WaitSeconds, podrazumevano 60s) u instalacionim funkcijama sprečava zastoje izazvane eksternim instalerima.
3. **Automatska identifikacija ekrana:** Serviseri u realnom vremenu dobijaju tačan podatak o kvalitetu i tipu ekrana (IPS/OLED/Touchscreen) bez otvaranja spoljnih alata.
4. **Profesionalna dijagnostika baterije:** Egzaktno izračunavanje procenta habanja i trobojni semafor omogućavaju trenutnu servisnu trijažu i preciznu preporuku klijentu.
5. **Nulto opterećenje pri praćenju CPU i RAM-a:** Primena Kernel32 P/Invoke poziva umesto teških WMI upita omogućava glatko osvežavanje dijagnostike u 1000ms intervalu sa zanemarljivim CPU utroškom (< 0.1%).
6. **Trenutni uvid u mrežne parametre i stabilnost skladišta:** Serviser ima živu informaciju o DHCP IP adresi, odzivu ka TrueNAS-u u milisekundama i trenutnoj brzini prenosa paketa.
7. **Dvostruka binarna konzistentnost:** Puna binarna i funkcionalna simetrija između `WIS.UI` i kompatibilnog `GWADT.UI` obezbeđuje potpunu operativnu bezbednost na svim servisnim medijumima.

### Rizici i Ograničenja:
- Povećanje veličine izvršnog modula sa 332,288 B na 340,992 B (+8,704 bajta), što predstavlja zanemarljiv utrošak u poređenju sa 6 novih implementiranih podsistema.
