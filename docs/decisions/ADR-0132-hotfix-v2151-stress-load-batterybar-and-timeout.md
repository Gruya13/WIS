---
title: "ADR-0132: Hotfix v2.15.1 - 100% Multi-Core CPU Load Generator u Fazi 5, Rešenje BatteryBar Popup-a, Povećan Timeout Faze 6 (45 min) i Eliminacija Security Warning Dijaloga"
---

# ADR-0132: Hotfix v2.15.1 - 100% Multi-Core CPU Load Generator u Fazi 5, Rešenje BatteryBar Popup-a, Povećan Timeout Faze 6 (45 min) i Eliminacija Security Warning Dijaloga

## 1. Kontekst i Problem

Tokom opsežnog terenskog testiranja verzije **WIS v2.15.0** na fizičkom prenosnom računaru **Dell Precision 3570** (Intel Core i7-1255U 12. generacije, hibridna arhitektura sa 2 Performance i 8 Efficient jezgara, ukupno 12 logičkih niti) u radionici **Tehno-Kutak**, identifikovana su četiri kritična operativna problema koja su ugrožavala autonomiju instalacije i pouzdanost hardverske verifikacije:

1. **BatteryBar modalni NSIS popup dijalog *"BatteryBar is already installed/running"*:**
   - Prilikom izvođenja Faze 4 (Aplikacije), pojavljivao se modalni blokirajući dijalog instalera koji je prekidao 100% autonomni Zero-Touch tok i zahtevao ručni klik servisera.
   - Analizom je utvrđeno da je problem nastajao usled konkurentnog pokretanja instalera iz ranog pozadinskog radnika, praćenog ponovnim pozivanjem instalera i izvršne datoteke unutar `Set-BatteryBarConfiguration` preko `Start-Process`, čak i kada je proces `BatteryBar` već bio aktivan u memoriji.
2. **Pasivno čekanje u automatskoj Fazi 5 naspram stvarnog opterećenja pod ručnim testom:**
   - U novouvedenoj Fazi 5 (`Phase5-Stress.ps1`), test stabilnosti je sprovodio pasivno 30-sekundno čekanje (`Start-Sleep`) u idle stanju, očitavajući temperaturu procesora od svega ~45°C i dajući lažan privid besprekornog hlađenja.
   - Nasuprot tome, kada je serviser ručno kliknuo na `[ ▶ Start Stres ]` u UI-ju na tabu Dijagnostika, radne niti su zakucale svih 12 jezgara na 100% opterećenja, što je na testiranom Dell laptopu sa sasušenom fabričkom termalnom pastom dovelo do skoka temperature na **98.4°C** i aktiviranja bezbednosnog `Emergency Abort` mehanizma.
   - Bilo je neophodno da i automatska Faza 5 sprovodi realan, višenitni matematički stres test sa opterećenjem od 100% na svim jezgrima, uz identičan sigurnosni osigurač od 98.0°C.
3. **Prekid Faze 6 usled 20-minutnog timeout-a tokom drugog prolaza Windows Update-a:**
   - Na modernim brendiranim računarima (naročito Dell i HP), drugi prolaz ažuriranja (`Invoke-WindowsUpdateSecondPass`) preuzima i instalira masivne kumulativne pakete i drajvere (Dell Optimizer, Intel Management Engine, firmware ažuriranja).
   - Fiksni timeout TaskRunner-a u `MainWindow.xaml.cs` od 20 minuta (1,200,000 ms) je prekidao radnu skriptu usred preuzimanja velikih paketa, proglašavajući grešku iako je operativni sistem uredno radio na primeni zakrpi.
4. **Windows Shell bezbednosni dijalog *"Open File - Security Warning"* pri startu:**
   - Prilikom pokretanja `WIS.UI.exe` iz `WIS.Launcher.exe`, operativni sistem je povremeno izbacivao Windows Shell dijalog *"The publisher could not be verified. Are you sure you want to run this software?"*.
   - Uzrok je bio u postojanju alternativnog NTFS toka podataka `:Zone.Identifier` (ZoneId=3 za fajlove preuzete sa mreže/interneta) na raspakovanim izvršnim datotekama, u kombinaciji sa parametrom `UseShellExecute = true` u `ProcessStartInfo` klasi Launchera. Takođe, Launcher je nepotrebno pretraživao zaostale `GWADT.UI.exe` putanje pre zvaničnog `WIS.UI.exe`.

---

## 2. Arhitektonska Odluka i Implementacija

Za rešavanje uočenih defekata usvojen je paket hitnih inženjerskih ispravki (Sprint Hotfix) koji podiže sistem na verziju **WIS v2.15.1**:

### 2.1. Stabilizacija BatteryBar-a i Eliminacija Modalnog NSIS Dijaloga
- **Uklanjanje konkurentnosti i striktna obrada u Fazi 4:**
  - U modulu `WIS.Apps.psm1`, funkcija `Set-BatteryBarConfiguration` je refaktorisana tako da pre bilo kakvog pokušaja startovanja procesa proverava aktivne instance:
    ```powershell
    $runningProc = Get-Process -Name "BatteryBar*" -ErrorAction SilentlyContinue
    if (-not $runningProc) {
        Start-Process -FilePath $installedExe -WindowStyle Normal -ErrorAction SilentlyContinue
    }
    ```
  - Uklonjeno je svako skriveno startovanje (`-WindowStyle Hidden`) u korist normalnog prozorskog stila (`-WindowStyle Normal`).
  - U funkciji `Invoke-Phase4Apps`, implementirana je prethodna provera postojanja `BatteryBar.exe` u `C:\Program Files\BatteryBar` i `C:\Program Files (x86)\BatteryBar`. Ako je aplikacija već prisutna na sistemu, instaler se **automatski preskače** i prelazi se direktno na primenu plutajuće XML konfiguracije (`Set-BatteryBarConfiguration`), čime je trajno eliminisan modalni dijalog *"BatteryBar is already installed"*.

### 2.2. Pravi Multi-Threaded CPU Load Generator u Fazi 5
- **100% višenitno opterećenje na svim logičkim jezgrima:**
  - U modulu `WIS.Hardware.psm1` (`Start-ServisStressTest`) i skripti `Phase5-Stress.ps1` uklonjeno je pasivno čekanje `Start-Sleep`.
  - Implementiran je pravi višenitni generator opterećenja zasnovan na PowerShell `RunspacePool` tehnologiji:
    * Broj konkurentnih niti postavlja se tačno na broj logičkih jezgara računara: `[Environment]::ProcessorCount`.
    * Svaka nit u bazenu izvršava intenzivnu matematičku petlju (`[Math]::Sin`, `[Math]::Cos`, `[Math]::Sqrt`) koja procesorska jezgra opterećuje na 100%.
  - **Senzorski nadzor i Emergency Abort:**
    * Glavna orkestratorska nit svake sekunde očitava hardverske senzore procesora (`ThermalSensorEngine` / WMI fallback).
    * Ukoliko temperatura bilo kog jezgra ili paketa dostigne ili prekorači **98.0°C**, sistem momentalno aktivira `Emergency Abort`: prekida rad bazena niti, oslobađa sve resurse, emituje upozoravajući zvučni signal i beleži `throttling_detected = $true` u telemetriju.
  - **Garantovano oslobađanje resursa:**
    * U `finally` bloku garantovano se pozivaju `$pool.Close()` i `$pool.Dispose()`, sprečavajući curenje procesorskih resursa nakon završetka testa.

### 2.3. Povećan Timeout Faze 6 i Ne-Blokirajući Windows Update
- **Povećanje timeout-a Faze 6 na 45 minuta:**
  - U C# klasi `MainWindow.xaml.cs` (i za `WIS.UI` i za `GWADT.UI`), timeout za Fazu 6 je povećan sa 20 minuta na **45 minuta** (2,700,000 ms), obezbeđujući dovoljno vremena za preuzimanje i dekompresiju velikih OEM drajverskih paketa (Dell/HP) bez preuranjenog prekida procesa.
- **Asinhroni ne-blokirajući drugi prolaz ažuriranja:**
  - U modulu `WIS.Update.psm1`, funkcija `Invoke-WindowsUpdateSecondPass` prilagođena je za asinhrono izvršavanje u pozadinskom poslu (`Start-Job`) sa strogim zaštitnim timeout-om od 10 minuta (600 sekundi). Ukoliko servis Windows Update prekorači predviđeno vreme, posao se bezbedno terminira bez rušenja celokupnog deployment pipeline-a.

### 2.4. Eliminacija "Open File - Security Warning" Dijaloga
- **Uklanjanje Shell Execute mehanizma:**
  - U `SplashForm.cs` (Launcher), `ProcessStartInfo` je eksplicitno konfigurisan sa `UseShellExecute = false` i `CreateNoWindow = false`. Time se zaobilazi Windows Shell sigurnosni mehanizam koji prikazuje upozorenja za nedokumentovane izdavače.
  - Iz metoda `FindInstalledUI` i `FindLocalPayloadUI` uklonjena je pretraga zaostalog `GWADT.UI.exe`, čime je `WIS.UI.exe` postao jedini i primarni cilj pokretanja.
- **Rekurzivno brisanje NTFS `:Zone.Identifier` toka:**
  - U `PayloadManager.cs` implementiran je P/Invoke poziv Win32 funkcije `DeleteFile` iz `kernel32.dll` ciljajući alternativni tok podataka:
    ```csharp
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteFile(string lpFileName);
    ```
  - Nakon raspakivanja arhive `wis-payload.zip`, vrši se rekurzivni prolaz kroz sve raspakovane datoteke u `C:\setup` i bezuslovno briše `:Zone.Identifier` tok (`DeleteFile(filePath + ":Zone.Identifier")`), čime se fajlovi trajno deblokiraju na NTFS fajl sistemu.

---

## 3. Status Odluke

**Prihvaćeno (Accepted)** — Implementirano, kompajlirano, binarno verifikovano i distribuirano u verziji **WIS v2.15.1**.

---

## 4. Posledice i Dobici

### Pozitivne posledice:
- **100% Autonomni Zero-Touch Tok:** Potpuno eliminisani prekidni modalni dijalozi (BatteryBar NSIS popup i Windows Shell Security Warning), omogućavajući nesmetanu instalaciju bez intervencije servisera.
- **Realan Termalni Test Stabilnosti:** Automatska Faza 5 sada opterećuje procesor identično kao ručni stres test u interfejsu (100% na svim jezgrima), pravovremeno detektujući mašine kojima je neophodan servis hlađenja pre predaje klijentu.
- **Hardverska Bezbednost:** Sigurnosni osigurač od 98.0°C u Fazi 5 garantuje zaštitu hardvera od termalnog oštećenja pri testiranju laptopova sa degradiranim hlađenjem.
- **Otpornost na Masivna Ažuriranja:** Povećan timeout od 45 minuta i asinhroni zaštitni mehanizam eliminišu lažne greške i prekide na mašinama sa masivnim drajverskim paketima.

### Neutralne / Kompatibilnost:
- Zadržana puna unazadna kompatibilnost i podrška za prenosive binarce u svim radioničkim okruženjima.

---

## 5. Metrike, Binarni Paritet i Distribucija (v2.15.1)

Svi C# projekti su uspešno rekompajlirani u Docker Wine MSBuild okruženju (`gwadt-builder-wine:latest`) sa **0 Warnings / 0 Errors (Exit code 0)**:

| Izvršni Fajl | Veličina (B) | SHA-256 Heš Suma | Pokrivenost Lokacija |
|---|---|---|---|
| **`WIS.UI.exe`** | 387,072 | `3d0f83e08370ceb9409909421400a97524ef23fb57caaa59bb0717584cc97141` | 9/9 lokacija (100%) |
| **`GWADT.UI.exe`** | 387,072 | `8eb2cd6429b2e70d8b0c538b719da8fd3641cb352357de707524b90d750b6935` | 10/10 lokacija (100%) |
| **`WIS.Launcher.exe`** | 56,832 | `6fb6cc7f35db7abb5e0f6fe299dc5051cd5ef2ed4a211839eced658c6a3ccfe9` | 7/7 lokacija (100%) |
| **`GWADT.Launcher.exe`** | 56,832 | `db9dfb2e9dfe86a1133207ad314833e6fecda7195adbf9caf9370df85ee03531` | 7/7 lokacija (100%) |

- Svi `version.json` fajlovi na projektu, Storage-u i TrueNAS serveru (`192.168.170.254`) ažurirani na `"version": "2.15.1"`.
- Očišćen ServisHub keš i restartovan kontejner `gwadt-servis-hub`.
- REST API endpoint `GET http://192.168.170.254:8080/api/payload/version` verifikovan sa odzivom: `{"version": "2.15.1", "sem_version": "2.15.1"}`.

---

## 6. Automatizovana QA Verifikacija

Izvršena je sveobuhvatna automatizovana verifikacija kroz namensku skriptu `Skripte/Test-Task132-Hotfix-v2151-QA.py`:
- **Ukupno provera:** **92 / 92 POLOŽENO (100.0% PASS)**, Exit Code 0.
- **Blok 1 (BatteryBar stabilizacija):** 8/8 PASS.
- **Blok 2 (Multi-core CPU Stres Generator & 98°C Abort):** 16/16 PASS.
- **Blok 3 (Timeout Faze 6 i Asinhroni WU):** 8/8 PASS.
- **Blok 4 (Eliminacija Security Warning i Zone.Identifier):** 9/9 PASS.
- **Blok 5 (Statička analiza):** 2/2 PASS (`Verify-CSharpSyntax.py` 43/43 PASS, `Verify-PowerShellSyntax.py` 100% validna AST).
- **Blok 6 (Binarni paritet i TrueNAS REST API v2.15.1):** 49/49 PASS.

---

## 7. Povezane Beleške
- Glavni Indeks: [[index]]
- Pregled Arhitekture: [[architecture/overview]]
- Redizajn Dijagnostike i 6-Fazni Pipeline v2.15.0: [[decisions/ADR-0131-diagnostics-redesign-6phase-pipeline-and-wis-cleanup]]
- Prilagođavanje BatteryBar Floating Toolbar-a: [[decisions/ADR-0119-batterybar-floating-toolbar-win11]]
- Integracija Senzorskog Telemetrijskog Motora: [[decisions/ADR-0118-thermal-sensor-engine-and-stress-test]]
- Thin-Client Launcher Arhitektura: [[decisions/ADR-0075-thin-client-launcher-and-dynamic-servishub-payload]]
- Radni Nalog i Status: `second_brain/agent_bus/CURRENT_TASK.md` (WIS-TASK-132)
