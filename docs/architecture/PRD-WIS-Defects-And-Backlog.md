# Tehnička Specifikacija i Katalog Nedostataka Sistema (PRD / RFC)

**Naziv Dokumenta:** Tehno kutak Windows Installation Assistant (WIS / GWADT) – Katalog Nedostataka i Arhitektonski Backlog  
**Status:** Spreman za Implementaciju (Ready for Implementation)  
**Autor:** Agency Brainstormer  
**Ciljni Tim:** WIS Multi-Agent Sistem (`wis_orchestrator`, `wis_powershell_expert`, `wis_ui_architect`, `wis_windows_admin`, `wis_qa_debugger`)  
**Datum:** 2026-10-06  

---

## 1. Rezime i Poslovni Kontekst (Executive Summary)

Tokom terenskog rada i automatizovanih instalacija Windows 11 operativnog sistema (verzije 23H2 i 24H2) u servisu Tehno kutak, identifikovano je 5 sistemskih nedostataka koji dovode do lažno pozitivnih statusa, neinstaliranih aplikacija i drajvera, kao i nepotrebnog gubljenja vremena servisera:
1. **Lažni završetak servisa (100% gotovo)** iako drajveri i aplikacije nisu fizički instalirani.
2. **Dugme za nadogradnju drajvera u UI-ju ne preuzima pakete** usled striktne zavisnosti od lokalnog NAS servera.
3. **Preostala neinstalirana ažuriranja u Windows Update-u** nakon proglašenja kraja procesa.
4. **BatteryBar se uopšte ne instalira na laptopovima**, lažno prijavljuje "OK" status, a na Windows 11 mora biti Floating Toolbar.
5. **Launcher pri svakom gašenju i pokretanju prikazuje lažni ekran za novu verziju**, zbunjujući operatera.

Ovaj dokument detaljno dekonstruiše korenske uzroke svakog problema, grupiše ih u **tri ravnopravna modula** (UI & Launcher, Skripte & Aplikacije, Mreža & Ažuriranja), i definiše jasne zahteve i kriterijume gotovosti za buduće sprintove.

---

## 2. Grupisani Katalog Nedostataka po Modulima

```
┌────────────────────────────────────────────────────────────────────────┐
│                   WIS / GWADT ARHITEKTONSKI MODULI                    │
└────────────────────────────────────────────────────────────────────────┘
        │                               │                               │
        ▼                               ▼                               ▼
┌───────────────────┐       ┌───────────────────────┐       ┌───────────────────┐
│     MODUL 1:      │       │       MODUL 2:        │       │     MODUL 3:      │
│   UI & LAUNCHER   │       │ SKRIPTE & INSTALACIJE │       │ MREŽA & AŽURIRANJA│
│ (WPF, Lifecycle,  │       │ (PowerShell Faze,     │       │ (Drajveri, NAS,   │
│   SplashForm)     │       │  BatteryBar, Reality) │       │  Multi-Pass WU)   │
└───────────────────┘       └───────────────────────┘       └───────────────────┘
```

---

### MODUL 1: Korisnički Interfejs i Životni Ciklus (UI & Launcher)

#### [DEF-01a] Odsustvo "Reality Check" Verifikacije u TaskRunner-u
- **Simptom:** Interfejs (`WIS.UI.exe` / `GWADT.UI.exe`) prolazi kroz faze i na kraju ispisuje da je servis završen (100%), iako na računaru nisu instalirani ni programi ni drajveri.
- **Koren Problema (Root Cause):**
  - TaskRunner u `MainWindow.xaml.cs` meri isključivo završetak spoljnog procesa (`powershell.exe`) i oslanja se na njegov `ExitCode == 0`.
  - Ukoliko skripta faze izađe prevremeno, prijavi grešku koja ne okine fatalan izlaz, ili ne uspe da pronađe izvorne fajlove, proces se gasi sa šifrom 0. TaskRunner sekvencijalno inkrementira fazu (`phaseNumber + 1`) dok ne stigne do Faze 5, kada automatski poziva `HandleFinalCompletion()`.
- **Ciljno Rešenje za Sprint:**
  - Implementirati **Post-Execution Reality Check Engine** pre proglašavanja završetka svake faze:
    - Za Fazu 2 (Drajveri): Upit kroz WMI/CIM za postojanje uređaja sa problemom (`ConfigManagerErrorCode != 0`).
    - Za Fazu 4 (Programi): Fizička provera postojanja izvršnih datoteka na disku (`Office\root\Office16\WINWORD.EXE`, `Google\Chrome\Application\chrome.exe`, `Acrobat.exe`).
  - Ako fizički artefakti nedostaju, faza se označava kao **NEUSPEŠNA**, bez obzira na izlazni kod PowerShell-a.

#### [DEF-05] Lažna Detekcija Nove Verzije u Launcheru
- **Simptom:** Pri svakom pokretanju i gašenju UI-ja, `WIS.Launcher` / `GWADT.Launcher` prikazuje ekran *"DOSTUPNO JE AŽURIRANJE UI PAKETA"* (Lokalna verzija: vX ➔ Nova verzija na serveru: vY).
- **Koren Problema (Root Cause):**
  - U `SplashForm.cs` (linija 1197), logički uslov za prikaz glasi:
    ```csharp
    bool updateAvailable = (!isHashMatch) || (cmp < 0);
    ```
  - `PayloadManager.IsLocalPayloadUpToDate()` poziva `GetLocalSha256()`, koji pokušava da pročita heš iz `metadata.json`. Ako taj fajl ne postoji u `C:\setup`, lokalni heš je `null`, pa je `isHashMatch` uvek `false`.
  - Posledično, `(!isHashMatch)` je uvek `true`, što bezuslovno aktivira prozor za ažuriranje.
  - Pored toga, lokalna verzija se čita kao SemVer (`2.12.0`), dok udaljeni server šalje numerički timestamp (`20261002100824`). Matematičko i leksikografsko poređenje uvek vidi timestamp kao noviju verziju (`cmp < 0`).
- **Ciljno Rešenje za Sprint:**
  - Uskladiti format verzije: lokalni `version.json` mora sadržati identičan timestamp format (`BuildTimestamp`) kao i server.
  - Ako lokalni `metadata.json` nedostaje, izračunati SHA-256 direktno nad postojećim `GWADT.UI.exe` / `WIS.UI.exe` umesto vraćanja `null`.
  - Prikazati prozor za ažuriranje **samo ako** je verzija na serveru dokazano veća i heš se razlikuje.

#### [FEAT-UI-03 / Defekt 2.3] Dvojezična Podrška u UI-ju: Srpski (SRB) i Kineski (中文) – 100% Prevod Svih 8 Kartica
> 🟢 **STATUS: ZAVRŠENO / IMPLEMENTIRANO 100% u WIS v2.13.0 (WIS-TASK-127 / ADR-0127)**  
> Implementirano 288 simetričnih dvojezičnih ključeva u `LocalizationService.cs`, 193 reaktivna XAML binding-a bez hardkodovanih statičkih labela, očuvanje softverskih i tehničkih naziva, i 131/131 položenih QA testova.

- **Zahtev / Poslovni Kontekst:**
  - Omogućiti operaterima servisa i korisnicima potpunu lokalizaciju svih 8 kartica interfejsa (`WIS.UI`) između **Srpskog** (`sr-Latn`) i **Kineskog (pojednostavljeni)** (`zh-Hans` / 简体中文).
- **Arhitektonski Izazovi i Trenutno Stanje:**
  - Iako je postavljen kostur `LocalizationService`, u `MainWindow.xaml` se nalazi preko 220+ hardkodovanih srpskih tekstova unutar tela svih 8 tabova.
  - Klik na `[ 🇨🇳 中文 ]` trenutno menja samo naslove tabova, dok je kompletan unutrašnji sadržaj kartica i dalje na srpskom jeziku.
- **Dogovorena Pravila i Obuhvat Prevoda (Scope & Terminology):**
  1. **Šta se 100% prevodi (Statički UI elementi):**
     - Sve statičke labele, naslovi sekcija, opisi polja, dugmad, statusni baneri, dijagnostičke poruke i modali na svih 8 kartica.
     - Terminologija: Zvanični Microsoft Windows IT rečnik na pojednostavljenom kineskom (npr. 中央处理器, 运行内存, 存储设备, 设备管理器, 驱动程序, 硬件健康度, 压力测试, 序列号).
  2. **Šta ostaje u originalnom obliku (Original/Untouched):**
     - **Nazivi softverskih paketa:** `Google Chrome`, `Microsoft Office 2024`, `Adobe Acrobat Pro 2024`, `WinRAR`, `BatteryBar`, `.NET 8 Runtime`, `OEM Licenca` ostaju u originalnom brendiranom obliku.
     - **Tehnički parametri i hardverski podaci:** Nazivi modela procesora i grafičkih kartica (npr. `Intel Core i7-13700H`, `RTX 4060`), memorijski standardi (`DDR4`, `DDR5`, `SO-DIMM`), interfejsi (`PCIe Gen4`, `NVMe`), mrežni parametri (`IPv4`, `MAC`, `1000 Mbps`), hardverski ID-jevi (`PCI\VEN_...`) i merne jedinice (`GHz`, `MHz`, `°C`, `RPM`) ostaju nepromenjeni.
     - **Sistemski raw log:** Konzolni tehnički log u dnu ostaje univerzalan radi standardizacije servisa.
- **Plan za Implementaciju u Sprintu po Karticama:**
  - **Kartica 1 (Pregled / 概览):** Povezivanje labela (`Model računara:` ➔ `计算机型号:`, `Procesor (CPU):` ➔ `中央处理器 (CPU):`, `Radna memorija (RAM):` ➔ `运行内存 (RAM):`, `SMART Zdravlje:` ➔ `硬盘健康度 (SMART):`, `TOK INSTALACIJE I FAZE` ➔ `部署流程与阶段`).
  - **Kartica 2 (Informacije / 硬件信息):** Prevod svih sekcija (`PREGLED HARDVERSKIH KOMPONENTI` ➔ `硬件配置概览`, `Jezgra i niti:` ➔ `核心与线程:`, `Radni takt:` ➔ `时钟频率:`, `Skladišni uređaji` ➔ `存储设备`, `Grafička karta` ➔ `图形核心 (GPU)`, `Matična ploča & BIOS` ➔ `主板与固件 (BIOS)`).
  - **Kartica 3 (Drajveri / 驱动程序):** Prevod statusa (`ZAHTEVA INTERVENCIJU` ➔ `需要人工处理`, `Klasa:` ➔ `设备类别:`, `Proizvođač:` ➔ `制造商:`, `Kopiraj HWID` ➔ `复制硬件ID`, `Dostupna nova verzija:` ➔ `发现新版本:`, `✓ Najnoviji drajver` ➔ `✓ 驱动已是最新`).
  - **Kartica 4 (Dijagnostika / 系统诊断):** Prevod termalnih senzora (`Trenutna temperatura:` ➔ `CPU当前温度:`, `Maksimalna zabeležena:` ➔ `历史峰值温度:`, `Opterećenje procesora:` ➔ `CPU实时负载:`, `🔥 TERMALNI STRES TEST` ➔ `🔥 处理器压力测试 (30秒)`).
  - **Kartica 5 (Mreža / 网络连接):** Prevod adaptera (`Naziv adaptera:` ➔ `网卡名称:`, `Status veze:` ➔ `连接状态:`, `Brzina linka:` ➔ `连接速率:`).
  - **Kartica 6 (Programi / 常用软件):** Prevod faza (`Status instalacije:` ➔ `部署进度状态:`, `Ponovi instalaciju` ➔ `重新部署`, stanja: `Čeka redosled` ➔ `等待队列`, `Instalira se` ➔ `正在安装`, `Instalirano` ➔ `安装成功`).
  - **Kartica 7 (Dnevnik / 运行日志):** Prevod kontrola (`Pretraga dnevnika` ➔ `检索日志条目`, `Kopiraj sve` ➔ `复制全部日志`, `Očisti prikaz` ➔ `清空日志`).
  - **Kartica 8 (Autopilot / 自动巡检):** Prevod inspekcije (`Zdravlje baterije i habanje:` ➔ `电池健康与损耗情况:`, `Ciklusi punjenja:` ➔ `充电循环计数:`, `TPM 2.0 Bezbednost:` ➔ `TPM 2.0安全芯片:`, `Secure Boot:` ➔ `安全启动:`).
- **Tehnička Realizacija:**
  1. Proširenje `LocalizationService.cs` sa ~180 novih prevodnih parova (`_srDict` i `_zhDict`).
  2. Zamena hardkodovanih `Text="..."` sa `{Binding Loc[Key]}` u `MainWindow.xaml`.
  3. Očuvanje font fallback-a na `Microsoft YaHei` za besprekoran prikaz karaktera.
  4. Perzistencija izbora jezika u `C:\setup\wis-config.json` i `HKLM\SOFTWARE\WIS\Language`.

---

### MODUL 2: Skripte i Automatizacija Paketa (Scripts & Applications)

#### [DEF-01b] Tihi Padovi PowerShell Faza bez Prekida Lanca
- **Simptom:** Skripte faza (`Phase2-Drivers.ps1`, `Phase4-Apps.ps1`) se tiho ugase ili prekinu izvršavanje bez jasnog crvenog statusa u UI-ju.
- **Koren Problema (Root Cause):**
  - PowerShell skripte u određenim blokovima imaju globalni `$ErrorActionPreference = 'Continue'` ili neobuhvaćene blokove gde greška u `Import-Module` ne rezultira sa `exit 1`.
  - Kada skripta naiđe na terminirajuću grešku van `try/catch` bloka, PowerShell host u određenim okolnostima vraća `LASTEXITCODE = 0`.
- **Ciljno Rešenje za Sprint:**
  - Standardizovati zaglavlje svih skripti faza (`$ErrorActionPreference = 'Stop'`).
  - Uvesti striktnu globalnu zamku grešaka (`trap { exit 1 }`) i slanje strukturirane IPC poruke pre napuštanja procesa (`Send-IpcLog -Level "FATAL"`).

#### [DEF-04] BatteryBar Lažni Status i Neuspeh Instalacije na Laptopovima
> 🟢 **STATUS: ZAVRŠENO / IMPLEMENTIRANO 100% u WIS v2.13.1 (WIS-TASK-128 / ADR-0128)**  
> Implementirano prenosivo (portable) kopiranje u `C:\Program Files\BatteryBar`, rekurzivna deblokada fajlova (`Unblock-File` i `:Zone.Identifier`), non-blocking WARN failover bez prekidanja Faze 4, izuzeće iz `Assert-InstalledApplicationsReality` i kontrolisano čekanje sa timeout-om ($WaitSeconds / $TimeoutSec).

- **Simptom:** BatteryBar se uopšte ne instalira na laptopu, dok u UI-ju stoji zeleni status "Instaliran". Na Windows 11 se ne prikazuje u donjem desnom uglu.
- **Koren Problema (Root Cause):**
  - **Lažna detekcija baterije:** Na sveže instaliranom sistemu, generički Microsoft ACPI Control Method Battery drajver često još nije učitan ili inicijalizovan u trenutku kada se pokrene Faza 4. Funkcija `Test-BatteryPresent` u `WIS.Apps.psm1` (linija 1622) vrati `$false`. Skripta zaključi da se radi o desktop računaru i pozove:
    ```powershell
    Update-ServisUI -AppId "BatteryBar" -AppStatus "OK" -StatusMessage "BatteryBar nije potreban (desktop računar)"
    ```
    UI ovo interpretira kao zelen status, dajući privid da je sve u redu.
  - **Inkompatibilnost sa Win 11 Taskbarom:** Standardni instaler pokušava instalaciju Deskband komponente koja je ukinuta na Windows 11.
- **Ciljno Rešenje za Sprint:**
  - **Dvoslojna detekcija prenosnih računara:** Pored baterije (`Win32_Battery`), proveravati i tip šasije iz BIOS-a (`Win32_SystemEnclosure.ChassisTypes` – tipovi 8, 9, 10, 14 označavaju Laptop/Notebook/SubNotebook). Ako je šasija laptop, BatteryBar je obavezan čak i ako baterija trenutno nije prijavljena preko WMI-ja.
  - **Striktna Floating Toolbar konfiguracija:**
    - Direktno raspakivanje prekonfigurisanog paketa u `C:\Program Files\BatteryBar`.
    - Generisanje `BatteryBar.Settings.xml` sa parametrima:
      `<Setting Name="Solo" Type="System.Boolean">True</Setting>`
      čime se garantuje režim nezavisnog plutajućeg prozora prilagođenog Windows 11 taskbaru.
    - Postavljanje `.config` fajla za .NET 4.x runtime kompatibilnost.
    - Prečica u `C:\ProgramData\Microsoft\Windows\Start Menu\Programs\Startup\BatteryBar.lnk`.
    - Fizička provera postojanja pre nego što se emituje "OK" status.

---

### MODUL 3: Mreža, Drajveri i Windows Update (Network & Updates)

#### [DEF-02] Neuspeh Preuzimanja i Instalacije Ključnih Drajvera sa UI-ja
- **Simptom:** UI pronađe novu verziju drajvera za problematičan ili ključni uređaj, prikaže zlatni bedž *"Dostupna nova verzija"*, ali klik na dugme `[⬇ Nadogradi]` ne preuzima i ne instalira drajver.
- **Koren Problema (Root Cause):**
  - Metoda `ExecuteUpgradeDriver` u `MainWindow.xaml.cs` (linije 3850–3851) pokušava preuzimanje isključivo sa privatnog endpointa:
    `hubUrl + "/api/drivers/download?id=" + targetId`.
  - Ako računar nije u servisu ili nema direktan pristup lokalnom NAS ServisHub-u (npr. rad na terenu, odvojen VLAN, mobilna pristupna tačka), `WebClient` momentalno puca sa mrežnim izuzetkom u pozadinskom Task threadu.
  - Ne postoji Internet fallback (npr. Microsoft Update Catalog pretraga preko Hardware ID-ja).
  - Dodatno, `expand.exe -R` pretpostavlja uniformnu `.cab` strukturu, dok noviji drajveri sadrže ugnježdene podfoldere i zahtevaju specifične parametre za `pnputil.exe`.
- **Ciljno Rešenje za Sprint:**
  - **Dual-Source Driver Fetching:**
    1. Primarni izvor: Lokalni NAS ServisHub (velika brzina u servisu).
    2. Sekundarni fallback izvor: Direktno preuzimanje sa Microsoft Update Catalog API-ja ili vendor repozitorijuma preko interneta ukoliko je NAS nedostupan.
  - Robusna obrada grešaka: Prikazati tačan status u UI-ju (`Preuzimanje nije uspelo: Server nedostupan`) umesto tihog zastoja na *"Nadogradnja u toku..."*.

#### [DEF-03] Preostala Neažurirana Stanja u Windows Update-u
- **Simptom:** Nakon što automatizacija završi i prijavi da je sve gotovo, ručna provera u Windows Settings -> Windows Update prikazuje dodatna dostupna ažuriranja.
- **Koren Problema (Root Cause):**
  - **Jednoprolazna arhitektura (Single-Pass WU):** Windows Update funkcioniše kaskadno. Prvi prolaz instalira drajvere i Servicing Stack Update (SSU). Tek nakon njihove uspešne registracije i inicijalizacije, Windows Update katalog oslobađa zavisne kumulativne nadogradnje.
  - API poziv vrši samo jedno skeniranje na početku Faze 2 i ne ponavlja proveru pre Faze 5.
- **Ciljno Rešenje za Sprint:**
  - Uvesti **Dvoprolazni (Dual-Pass) kaskadni WU mehanizam**:
    - Prolaz 1 (Faza 2): Instalacija osnovnih čipset, mrežnih i grafičkih drajvera + SSU.
    - Prolaz 2 (kraj Faze 3 ili početak Faze 5, pre čišćenja): Kratak re-scan preostalih ažuriranja. Ukoliko postoje neinstalirane kritične/kumulativne zakrpe, povući ih automatski pre finalizacije sistema.

#### [DEF-06] Odsustvo Determinističkog Pristupa NAS-u u WinPE Fazi
- **Simptom:** Kada se sistem butuje u WinPE sa fizičkog USB fleša ili lokalnog medijuma, WinPE nema mapiran pristup TrueNAS skladištu (`\\192.168.170.254\Servis`). Serviser i skripte u WinPE-u ostaju izolovani bez centralne baze drajvera, alata i novih ISO/WIM slika.
- **Koren Problema (Root Cause):**
  - U postojećim skriptama `WIS-AcceleratePE.cmd` (linije 26–29) i `startnet.cmd` (linija 142), povezivanje na TrueNAS je **uslovljeno odsustvom USB-a** ili prisustvom iVentoy virtuelnog diska (`if not defined IS_IVENTOY ... goto :Done` / `if defined GWADT_SOURCE ... goto :LaunchSetup`).
  - Ukoliko je USB prisutan, povezivanje na NAS se potpuno preskače.
  - Pored toga, nakon `wpeutil InitializeNetwork`, mrežnom adapteru je potrebno 2 do 4 sekunde za DHCP dodelu IP adrese. Ako se `net use` ili `ping` izvrše pre nego što adapter dobije IP, povezivanje puca zbog race-condition-a.
- **Ciljno Rešenje za Sprint:**
  - **Univerzalni WinPE NAS Connect Engine:**
    1. U `startnet.cmd` i `WIS-AcceleratePE.cmd`, bez obzira na vrstu but medijuma (USB, iVentoy PXE ili lokalni disk), pokrenuti namensku mrežnu inicijalizaciju.
    2. Uvesti kratku proveru dodele IP adrese (polling do 6 sekundi za DHCP odgovor pre mrežnih poziva).
    3. Bezuslovno mapirati `\\192.168.170.254\Servis` na disk **`Z:\`** preko SMB 3.1.1 (Guest/Anonymous autentifikacija):
       `net use Z: \\192.168.170.254\Servis /user:guest ""`
    4. Prikazati status u WinPE zaglavlju:
       - `[+] TrueNAS Servis deljenje aktivno na Z:\ (115 MB/s)`
       - `[-] NAS nije dostupan na mreži – rad se nastavlja sa lokalnog USB medijuma`
    5. Omogućiti da WinPE koristi resurse sa diska `Z:\` (npr. povlačenje najnovijih drajvera `Z:\Drivers` ili alata koji nedostaju na USB-u).

---

### 🟢 Realizovani Sprint Defekti u Verziji v2.13.1 (WIS-TASK-128 / ADR-0128)

U okviru sprinta **WIS-TASK-128** (ADR-0128) uspešno je razrešeno i 100% QA verifikovano svih 7 identifikovanih sprint defekata:

1. **[SPRINT-DEF-01 / REQ-SCR-02] Prenosivi (Portable) BatteryBar Paket i Non-Blocking Failover u Fazi 4:**
   - **Status:** 🟢 **ZAVRŠENO / IMPLEMENTIRANO 100% u WIS v2.13.1**
   - **Rešenje:** Uklonjen sirovi instaler `BatteryBarSetup-3.6.6.exe /S` i Winget koji su tražili .NET 3.5 i zamrzavali Fazu 4. Implementirano prenosivo kopiranje u `C:\Program Files\BatteryBar`, automatska rekurzivna deblokada datoteka (`Unblock-File`, `:Zone.Identifier`), non-blocking WARN failover bez prekidanja Faze 4, izuzeće iz `Assert-InstalledApplicationsReality` i kontrolisano čekanje ($WaitSeconds / $TimeoutSec) sa prisilnim terminisanjem zaglavljenih procesa.

2. **[SPRINT-DEF-02 / HW-01] Detekcija Tehnologije Panela Ekrana (IPS/OLED/VA/TN):**
   - **Status:** 🟢 **ZAVRŠENO / IMPLEMENTIRANO 100% u WIS v2.13.1**
   - **Rešenje:** Implementirana metoda `DetectDisplayPanelType`, očitavanje `root\wmi:WmiMonitorID` i `HKLM\SYSTEM\CurrentControlSet\Enum\DISPLAY`, detekcija OLED/AMOLED, IPS, AHVA, PLS, ADS, IGZO tehnologija, serija panela (NV, LP, HAN, HCE) i FHD+ heuristika. Integrisano u `DisplaySpec` sa oznakom ` • IPS`.

3. **[SPRINT-DEF-03 / HW-02] Precizno Očitavanje Habanja Baterije u % sa Trostepenim Semafor Bojama:**
   - **Status:** 🟢 **ZAVRŠENO / IMPLEMENTIRANO 100% u WIS v2.13.1**
   - **Rešenje:** Direktno očitavanje ACPI podataka iz `root\wmi:BatteryStaticData` (`DesignedCapacity`), `BatteryFullChargedCapacity` (`FullChargedCapacity`) i `BatteryCycleCount`, proračun istrošenosti `wearLevel = (1.0 - (fullCap / designCap)) * 100.0`, prikaz `{0:0.0}% Istrošenost ({1:N0} / {2:N0} mWh)` i trostepeni semafor boja (<15% zelena `#2ECC71`, <35% žuta `#F1C40F`, >=35% crvena `#E74C3C`) sa `Win32_Battery` fallback-om.

4. **[SPRINT-DEF-04 / DIAG-01] Nativni C# 1000ms Tajmer za Real-Time CPU i RAM Dijagnostiku:**
   - **Status:** 🟢 **ZAVRŠENO / IMPLEMENTIRANO 100% u WIS v2.13.1**
   - **Rešenje:** Kernel32 P/Invoke `GetSystemTimes` (CPU delta %) i `GlobalMemoryStatusEx` (RAM zauzeće), pozadinski `System.Timers.Timer` na 1000ms sa `Dispose()` u `Closed` događaju, živo osvežavanje `ViewModel.CpuUsage` i `ViewModel.RamUsage` bez teških WMI upita i opterećenja procesora.

5. **[SPRINT-DEF-05 / NET-01] Dinamička DHCP IP Adresa od Rutera sa Proverom Default Gateway-a:**
   - **Status:** 🟢 **ZAVRŠENO / IMPLEMENTIRANO 100% u WIS v2.13.1**
   - **Rešenje:** Skeniranje mrežnih interfejsa sa validacijom Default Gateway-a, filtriranje loopback, APIPA (`169.254.x.x`) i `0.0.0.0` adresa, trenutno ažuriranje `ViewModel.IpAddress` po dodeli od rutera.

6. **[SPRINT-DEF-06 / NET-02] Asinhroni ICMP Ping ka TrueNAS Skladištu sa Merenjem Latencije:**
   - **Status:** 🟢 **ZAVRŠENO / IMPLEMENTIRANO 100% u WIS v2.13.1**
   - **Rešenje:** Asinhroni ping ka `192.168.170.254` u pozadinskoj radnoj niti (3s interval, 1000ms timeout), trenutno ažuriranje `ViewModel.NasStatus` ("Povezan" / "Offline") i `ViewModel.NasLatency` ("X ms" / "N/A").

7. **[SPRINT-DEF-07 / NET-03] Merenje Mrežnog Protokola u Realnom Vremenu i Ukupnog Saobraćaja:**
   - **Status:** 🟢 **ZAVRŠENO / IMPLEMENTIRANO 100% u WIS v2.13.1**
   - **Rešenje:** Očitavanje `nic.GetIPv4Statistics().BytesReceived` na 1s, proračun delta brzine u `MB/s` i akumulacija ukupnog prenosa, formatiranje `{0:0.0} MB/s ({1:N0} MB ukupno)` i ažuriranje `ViewModel.DownloadedMb`.

---

## 3. Matrica Zahteva za Implementaciju (Requirements Matrix)

| ID Zahteva | Modul | Opis Zahteva | Kriterijum Prihvatanja (Acceptance Criteria) |
|---|---|---|---|
| **REQ-UI-01** | UI | Post-Execution Reality Check | UI ne sme preći u status "Završeno" dok fizički ne potvrdi prisustvo ključnih aplikacija (Office, Chrome, Acrobat) i ispravnost drajvera. |
| **REQ-UI-02** | UI | Ispravka Logike Ažuriranja u Launcheru | Prozor za ažuriranje se prikazuje isključivo ako je udaljeni timestamp veći od lokalnog i SHA-256 heševi se razlikuju. Ne sme se oslanjati na nepostojeći fajl. |
| **REQ-UI-03** | UI | Dvojezična Podrška (Srpski / Kineski) | Dinamički ResourceDictionary / Localization Manager omogućava izbor između srpskog i kineskog (pojednostavljenog) u letu bez restarta, uz YaHei font fallback i perzistenciju izbora. **[🟢 ZAVRŠENO 100% u v2.13.0 (WIS-TASK-127)]** |
| **REQ-SCR-01** | Skripte | Globalno Hvatanje Grešaka u Fazama | Svaka faza mora imati `$ErrorActionPreference = 'Stop'` i vraćati nestandardan ExitCode pri neuspehu, uz obaveznu IPC notifikaciju. |
| **REQ-SCR-02** | Skripte | Dvoslojna Detekcija Laptopa & Floating BatteryBar | Sistem detektuje laptop preko tipa šasije i baterije; instalira BatteryBar kao nezavisni Floating Toolbar sa `Solo=True` XML konfiguracijom i portable kopiranjem bez blokiranja Faze 4. **[🟢 ZAVRŠENO 100% u v2.13.1 (WIS-TASK-128)]** |
| **REQ-HW-01** | UI | Detekcija Tipa Panela Ekrana (IPS/OLED) | Automatsko prepoznavanje tehnologije panela (OLED, IPS, AHVA, PLS, ADS, IGZO, TN) preko WmiMonitorID i DISPLAY Registry ključa uz FHD+ heuristiku. **[🟢 ZAVRŠENO 100% u v2.13.1 (WIS-TASK-128)]** |
| **REQ-HW-02** | UI | Battery Wear Level % & Trostepeni Semafor | Egzaktno očitavanje dizajniranog i trenutnog kapaciteta baterije iz `root\wmi:BatteryStaticData`, proračun procenta istrošenosti i semaforska vizuelizacija (<15% zelena, <35% žuta, >=35% crvena). **[🟢 ZAVRŠENO 100% u v2.13.1 (WIS-TASK-128)]** |
| **REQ-DIAG-01** | UI | Real-Time CPU i RAM 1000ms Dijagnostika | Nativni Kernel32 P/Invoke pozivi (`GetSystemTimes`, `GlobalMemoryStatusEx`) sa 1000ms tajmerom za živo praćenje zauzeća bez WMI opterećenja procesora. **[🟢 ZAVRŠENO 100% u v2.13.1 (WIS-TASK-128)]** |
| **REQ-NET-01** | Mreža | Internet Fallback za OEM Drajvere | Nadogradnja drajvera mora podržavati preuzimanje sa interneta ukoliko lokalni ServisHub NAS nije dostupan na mreži. |
| **REQ-NET-01b** | Mreža | Dinamičko Osvežavanje DHCP Ruter IP Adrese | Prepoznavanje operativnog mrežnog adaptera sa validnim Default Gateway-om, filtriranje APIPA (`169.254.x.x`) adresa i trenutno ažuriranje IP-ja u UI-ju. **[🟢 ZAVRŠENO 100% u v2.13.1 (WIS-TASK-128)]** |
| **REQ-NET-02** | Mreža | Dvoprolazno Windows Update Skeniranje | Implementacija drugog prolaza (Re-Scan & Install) kako bi se eliminisale preostale kaskadne nadogradnje pre predaje računara. |
| **REQ-NET-02b** | Mreža | TrueNAS Asinhroni Ping & Latencija | Neometajući ICMP ping ka `192.168.170.254` u pozadinskoj niti sa prikazom latencije u ms i statusa veze u realnom vremenu. **[🟢 ZAVRŠENO 100% u v2.13.1 (WIS-TASK-128)]** |
| **REQ-NET-03** | Mreža | Univerzalni WinPE Pristup NAS-u (Disk Z:) | WinPE pri svakom butovanju (i sa USB-a) čeka IP adresu i automatski mapira TrueNAS share na disk `Z:\`, čineći centralne resurse uvek dostupnim. |
| **REQ-NET-03b** | Mreža | Merenje Protokola (MB/s) i Ukupnog Saobraćaja | Praćenje `BytesReceived` mrežne statistike u taktu od 1s, proračun delta brzine u MB/s i akumulacija ukupno prenetih MB. **[🟢 ZAVRŠENO 100% u v2.13.1 (WIS-TASK-128)]** |

---

## 4. Kriterijumi Gotovosti (Definition of Done)

Zadaci iz ovog kataloga smatraju se završenim u narednim sprintovima kada:
1. **Fizička validacija prolazi:** Test instalacija na test mašini fizički sadrži sve programe, bez ijednog lažno pozitivnog zelenog bedža.
2. **BatteryBar radi iz prve:** Na bilo kom test laptopu BatteryBar se podiže kao plutajući prozor nakon logovanja bez potrebe za intervencijom servisera.
3. **Launcher je stabilan:** Ponovno pokretanje Launchera kada nema realnih izmena na serveru ide direktno u rad bez lažnog ekrana za ažuriranje.
4. **Drajveri se preuzimaju:** Klik na dugme `[Nadogradi]` uspešno preuzima drajver i van lokalne servisne mreže.
5. **Nula preostalih zakrpa:** Ručni klik na "Check for updates" u Windowsu nakon završenog procesa javlja: *"You're up to date"*.
6. **WinPE ima univerzalan pristup NAS-u:** Pri butovanju u WinPE sa USB fleša (ili iVentoy-a), disk `Z:\` je uspešno mapiran na `\\192.168.170.254\Servis` i resursi su momentalno čitljivi.
7. **Dvojezični interfejs funkcioniše u realnom vremenu:** Prekidač jezika `[ 🇷🇸 SRB | 🇨🇳 中文 ]` u gornjem desnom uglu momentalno prevodi sve elemente interfejsa na kineski jezik (sa oštrim prikazom YaHei fonta) i nazad na srpski, a izabrani jezik ostaje trajno zapamćen za naredna pokretanja.

---

## 5. Direktive za Multi-Agent Tim za Buduće Sprintove

- 🏛️ **`wis_orchestrator`**:
  - Upravljanje stanjem u `second_brain/agent_bus/CURRENT_TASK.md` pod task ID-jem `WIS-TASK-122-SYSTEM-DEFECTS-FIX`.
  - Sekvencijalna koordinacija: prvo Modul Skripti (BatteryBar i Reality Check), zatim Modul UI-ja (Launcher heš i TaskRunner), i na kraju Modul Mreže (Dual-Pass WU i Driver fallback).
- ⚡ **`wis_powershell_expert`**:
  - Implementacija dvoslojne detekcije šasije u `WIS.Apps.psm1` (`Win32_SystemEnclosure`).
  - Kreiranje robusnog XML generatora za `BatteryBar.Settings.xml` (Floating Toolbar).
  - Implementacija funkcije `Assert-InstalledApplicationsReality` pre izlaska iz Faze 4.
- 🎨 **`wis_ui_architect`**:
  - Refaktorisanje logike u `SplashForm.cs` (eliminacija lažnog `(!isHashMatch)` okidanja).
  - Usklađivanje prikaza verzija (datumski timestamp vs SemVer).
  - Dodavanje vizuelnog upozorenja ukoliko Reality Check prijavi nedostajuće pakete.
- 🛠️ **`wis_windows_admin`**:
  - Konfiguracija dvoprolaznog WU skeniranja (kaskadni SSU/drajveri ciklus).
  - Implementacija Catalog/Internet fallback-a za preuzimanje `.cab` drajvera u `MainWindow.xaml.cs`.
- 🧪 **`wis_qa_debugger`**:
  - Izrada automatizovane test skripte (`Test-Defects-RealityCheck.py`) koja simulira odsustvo NAS servera, proverava postojanje fajlova na disku i verifikuje Launcher heš poređenje.
- 📚 **`wis_docs_sync`**:
  - Upisivanje novog ADR-a (`ADR-0105-defects-resolution-and-reality-check.md`) i ažuriranje `istorija_izrade.md`.
