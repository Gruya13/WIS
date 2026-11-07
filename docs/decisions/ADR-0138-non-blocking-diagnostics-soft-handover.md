---
title: "ADR-0138: Non-Blocking Diagnostics Policy & Soft Handover Warning: Eliminacija zastoja na hardveru bez temperaturnih senzora, neometan završetak pipeline-a i jasno markiranje statusa (v2.16.2)"
type: decision
created: 2026-10-12
status: accepted
tags:
  - adr
  - architecture
  - diagnostics
  - stress-test
  - thermal
  - handover
  - wis
---

# ADR-0138: Non-Blocking Diagnostics Policy & Soft Handover Warning: Eliminacija zastoja na hardveru bez temperaturnih senzora, neometan završetak pipeline-a i jasno markiranje statusa (v2.16.2)

## 1. Kontekst i Problem iz Prakse

Tokom uvođenja očvrsnutih bezbednosnih i dijagnostičkih mehanizama u verziji v2.16.0 ([[decisions/ADR-0136-comprehensive-review-remediation-and-signed-payload]]), uveden je autoritativni ugovor stresa (`stress-test-result.json`) sa strogom kapijom predaje (`Handover Gate`). Cilj tog mehanizma bio je da spreči izdavanje računara sa neispravnim hlađenjem.

Međutim, terensko testiranje u realnom servisu otkrilo je ozbiljan operativni nedostatak:
Korisnik i serviseri su prijavili:
> *"Sistem ne završava, izbacuje grešku za dijagnostiku i tu stoji. Da napravimo da završi ceo pipeline ali da markuje ako dijagnostika nije urađena."*

### Tehnički Uzrok Zastoja:
1. **Prekomerno striktna logika u Fazi 5 (`Phase5-Stress.ps1`):** Na određenim modelima računara (desktop ploče bez WMI/ACPI senzora, virtualizovane mašine, egzotični čipseti gde `LibreHardwareMonitorLib` i WMI vraćaju status `UNKNOWN`, `sensor_available = $false` ili `maxCpuTemp <= 0`), Faza 5 je nedostatak senzora tretirala jednako fatalno kao i stvarno fizičko pregrevanje. Slan je status `NEEDS_ATTENTION` i skripta je prekidala tok sa izlaznim kodom `exit 2`.
2. **Blokada u Fazi 6 (`Phase6-Finalize.ps1`):** Uslov `$blockHandover` je postavljao `$blockHandover = $true` kada god je `$stressStatus -eq "UNKNOWN"`, što je blokiralo izdavanje statusa `DONE`, sprečavalo generisanje završnog servisnog kartona i ostavljalo računar u statusu `NEEDS_ATTENTION`.
3. **Crveni Abort Ekran u C# WPF UI-ju (`MainWindow.xaml.cs`):** Prijem statusa `NEEDS_ATTENTION` ili `UNKNOWN` aktivirao je metodu `HandleCriticalThermalAbort(2)`, koja je zaključavala interfejs crvenim ekranom, obustavljala odbrojavanje tajmera i zahtevala ručnu intervenciju, iako je računar bio potpuno ispravan, a operativni sistem i programi kompletno instalirani.

Doneta je odluka da se uspostavi **Non-Blocking Diagnostics Policy & Soft Handover Warning** u verziji **WIS v2.16.2**: celokupan pipeline mora nesmetano da prođe do samog kraja (`exit 0`), status servisa mora biti uspešno završen (`DONE`), a nemogućnost očitavanja termalnih senzora se transparentno markira kao blago upozorenje (žuti bedž u UI-ju i jasna napomena u servisnom kartonu).

---

## 2. Arhitektonska Odluka i Tehnička Implementacija

U okviru radnog naloga **WIS-TASK-138** usvojeni su sledeći inženjerski principi i implementaciona rešenja:

### 2.1. Jasno Razdvajanje Fizičkog Pregrevanja od Nedostupnosti Senzora
Uvedena je stroga taksonomija termalnih stanja:
- 🔴 **Fizičko termalno pregrevanje (`THERMAL_CRITICAL`):** Kada senzori jesu dostupni i očitana temperatura procesora pređe kritični prag ($\ge 95.0^\circ\text{C}$) ili dođe do Emergency Abort prekida ($\ge 98.0^\circ\text{C}$). **Ovo stanje i dalje blokira predaju (`exit 2`, `handover_ready = false`), jer predstavlja stvarni hardverski defekt.**
- 🔴 **Sigurnosni Anti-Leak prekršaj:** Ako servisne lozinke (`Wifi-Servis.ini`) nisu uspešno obrisane sa diska. **Blokira predaju (`exit 2`).**
- 🟡 **Nedostupnost senzora (`UNKNOWN` / `SENSOR_UNAVAILABLE`):** Kada hardver računara ne podržava softversko očitavanje temperature preko dostupnih biblioteka i ACPI/WMI interfejsa. **Ovo stanje NIKADA NE BLOKIRA završetak servisa.**

### 2.2. Ugovor Faze 5 (`Phase5-Stress.ps1`)
Kada se detektuje da senzori nisu podržani (`$stressStatus -eq "UNKNOWN"`, `$sensorAvailable -eq $false` ili `$maxCpuTemp -le 0.0`):
- Postavljaju se ciljne vrednosti: `$targetPhase = 6`, `$targetDeploymentStatus = "ACTIVE"` i `$targetHandoverReady = $true`.
- U ugovore `deploy-state.json` i `stress-test-result.json` upisuju se:
  * `status = "UNKNOWN"`
  * `reason = "SENSOR_UNAVAILABLE"`
  * `sensor_available = false`
  * `diagnostics_status = "SENSOR_UNAVAILABLE"`
  * `diagnostics_warning = "Senzori temperature nisu dostupni na hardveru"`
- `Send-IpcStatus` šalje:
  ```powershell
  Send-IpcStatus -Phase 5 -DeploymentStatus "ACTIVE" -HandoverReady $true -SubPhase "Završeno (Senzori nedostupni)"
  ```
- Beleži se log zapis nivoa `WARN`, a skripta Faze 5 se završava uspešno sa izlaznim kodom **`exit 0`**, omogućavajući TaskRunner-u da momentalno i automatski pređe na Fazu 6.

### 2.3. Ugovor Faze 6 (`Phase6-Finalize.ps1`) i Servisni Karton
U Fazi 6 uklonjena je blokada predaje za nedostupne senzore:
- Promenljiva `$blockHandover` se postavlja na `$true` **isključivo** pri stvarnom pregrevanju ili neuspešnom Anti-Leak čišćenju.
- Kada su samo senzori nedostupni:
  * `$blockHandover = $false`
  * Postavlja se `FinalStatus = "DONE"`, `HandoverReady = $true`.
  * U `deploy-state.json` se trajno perzistira `diagnostics_status = "SENSOR_UNAVAILABLE"` i `diagnostics_warning = "Senzori temperature nisu dostupni na ovom hardveru"`.
  * U digitalnom servisnom kartonu (`servisni_karton.json` i `servisni_karton.md`) u sekciji hlađenja upisuje se jasna servisna napomena:
    `[DIJAGNOSTIKA: NIJE OČITANA (Senzori temperature nisu podržani na ovom računaru)]`.
  * Zaključak glasi: `SERVIS USPEŠNO ZAVRŠEN | ⚠️ DIJAGNOSTIKA HLAĐENJA: Senzori temperature nisu podržani na ovom računaru`.
  * Skripta Faze 6 se završava sa kodom **`exit 0`**.

### 2.4. Prilagođavanje C# WPF UI-ja (`MainWindow.xaml.cs` & `Models.cs`)
U `Skripte/WIS.UI/`:
1. **Uklanjanje Crvene Blokade:**
   - U metodama `ProcessIncomingJson` i `HandleSpecificEvent`, ukoliko stigne status `NEEDS_ATTENTION` ili `THERMAL_CRITICAL`, vrši se provera da li je u pitanju `UNKNOWN` / `SENSOR_UNAVAILABLE` ili `maxT <= 0.5f`. U tom slučaju **ne poziva se `HandleCriticalThermalAbort(2)`**.
   - Na samom početku metode `HandleCriticalThermalAbort` dodat je bezuslovni osigurač:
     ```csharp
     if (_lastStressStatus == "UNKNOWN" || _lastStressMaxTemp <= 0.5f) {
         return; // Ne blokiraj računar ako su senzori samo nedostupni
     }
     ```
2. **Žuti Indikator Upozorenja umesto Crvenog Aborta:**
   - Uvedeno reaktivno svojstvo `IsDiagnosticsUnavailable` u `Models.cs`.
   - Pri nedostatku senzora postavlja se žuti bedž upozorenja: `⚠️ TERMALNI SENZORI NISU DOSTUPNI (UNKNOWN)`.
3. **Neometan Završetak u `HandleFinalCompletion`:**
   - Servis se normalno proglašava završenim: `ViewModel.DeploymentStatus = "READY"`, `ViewModel.IsCompleted = true`, `ViewModel.IsServiceFinished = true`.
   - U `ViewModel.CompletionSummary` i `ViewModel.LiveStatus` upisuje se:
     `"⚠️ Instalacija uspešna. Dijagnostika hlađenja: Senzori temperature nisu podržani na ovom računaru."`.
   - Pokreće se standardni tajmer do automatskog restarta/gašenja, bez potrebe za ručnom deblokadom.

---

## 3. Kompilacija, Distribucija i Kriptografski Potpis (v2.16.2)

### 3.1. Docker Wine MSBuild Kompilacija
Oba C# projekta uspešno su kompajlirana u izolovanom Docker Wine okruženju (`gwadt-builder-wine:latest`):
- `WIS.UI.exe`: **410,112 B**, PE32 executable, SHA-256: `beea1cc3a89afca1e4cf61b91db0101ea7f3eadae7b795406b4493763d02a68a`.
- `WIS.Launcher.exe`: **82,432 B**, PE32 executable, SHA-256: `c23863919c71912b31b87b433e0b6aeb62451fd75edd1fc56bcff17afe64697c`.
- Postignut je 100% SHA-256 binarni paritet na svim kanonskim lokacijama na disku i skladištu.

### 3.2. Regenerisanje i RSA-2048 Potpisivanje Payload Arhive
- Regenerisan paket `storage/cache/wis-payload.zip`:
  * Veličina: tačno **4,140,723 B**.
  * SHA-256 kontrolna suma: `1f8b5f694a618eac083a6ec4fbee9350a9c50e59b498e490f41bfbf537e33e30`.
- Kriptografski potpisan manifest `storage/cache/payload_meta.json` važećim asimetričnim RSA-2048 PKCS#1 v1.5 ključem:
  * SHA-256 kontrolna suma potpisa: `9ce2386a736de043047f8fcd264a1e2c38ff129590307df31bbd4dd1c8623bfb`.
  * Verifikacija preko `Sign-PayloadManifest.py --verify-only` -> `[PASS] 100% VALID` (Exit Code 0).

---

## 4. Sveobuhvatna QA Verifikacija (100% PASS Bez Mock-ova)

Sistem je u potpunosti verifikovan kroz automatske test pakete nad realnim skriptama i binarnim datotekama:
1. **`Skripte/Test-Task138-Diagnostics-Policy-QA.py`:**
   - 35/35 provera uspešno položeno (**100.0% PASS**, Exit Code 0).
   - Testirano: ugovor Faze 5 (exit 0, ACTIVE, HandoverReady $true za UNKNOWN; exit 2 za THERMAL_CRITICAL), ugovor Faze 6 (exit 0 i DONE za UNKNOWN; blokada samo za stvarno pregrevanje i leak), C# UI rukovanje (uklonjen abort za UNKNOWN, žuto upozorenje), JSON šeme ugovora i binarni paritet.
2. **`Skripte/Test-Task136-Remediation-QA.py`:**
   - 41/41 regresionih provera uspešno položeno (**100.0% PASS**, Exit Code 0).
   - Potvrđen kompletan integritet 35 manifest komponenti, RSA-2048 potpisa, PE32 zaglavlja i odsustvo tehničkog duga.
3. **Statička analiza koda:**
   - `Verify-PowerShellSyntax.py`: 20/20 skripti bez ijedne sintaksne greške.
   - `Verify-CSharpSyntax.py`: 21/21 datoteka u potpunosti zadovoljava C# 5.0 standard.

---

## 5. Posledice Odluke

### Pozitivne posledice:
- **100% Pouzdanost Završetka Pipeline-a:** Računari sa matičnim pločama ili procesorima bez podržanih senzora više se nikada ne zamrzavaju na Fazi 5 ili Fazi 6.
- **Puna Operativna Transparentnost:** Serviser i klijent u servisnom kartonu i UI-ju imaju nedvosmislenu informaciju da dijagnostika temperature nije podržana na tom hardveru, umesto lažne uzbune o kvaru.
- **Očuvanje Sigurnosnih Barijera:** Stvarno pregrevanje procesora ($\ge 95^\circ\text{C}$) i neobrisane Wi-Fi lozinke i dalje striktno blokiraju predaju računara.

### Negativne posledice / Kompromisi:
- Na računarima bez senzora serviser mora ručno proveriti izduvni vazduh i rad ventilatora ukoliko sumnja na zaprljan hladnjak, što je u servisnoj praksi standardna procedura.

---

## 🔗 Povezane Beleške
- Glavni Indeks: [[index]]
- Pregled Arhitekture: [[architecture/overview]]
- Sveobuhvatno Saniranje i 6-Fazni Ugovor: [[decisions/ADR-0136-comprehensive-review-remediation-and-signed-payload]]
- Pure WIS Cleanup v2.16.1: [[decisions/ADR-0137-pure-wis-cleanup-and-gwadt-retirement]]
- Poznate Zamke i Rešenja: [[memory/gotchas]]
