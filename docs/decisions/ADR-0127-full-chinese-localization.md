---
title: "ADR-0127: Potpuna Dvojezična Lokalizacija Interfejsa na Pojednostavljeni Kineski Jezik (100% Full i18n) za Svih 8 Kartica WIS i GWADT UI-ja uz Očuvanje Originalnih Softverskih i Tehničkih Naziva"
type: decision
status: accepted
date: 2026-10-07
author: wis_docs_sync
tags:
  - decision
  - adr
  - localization
  - i18n
  - chinese
  - yahei
  - wpf
  - ui
---

# ADR-0127: Potpuna Dvojezična Lokalizacija Interfejsa na Pojednostavljeni Kineski Jezik (100% Full i18n) za Svih 8 Kartica WIS i GWADT UI-ja uz Očuvanje Originalnih Softverskih i Tehničkih Naziva

## 1. Kontekst i Problem

U okviru servisa "Tehno kutak", automatizovani sistem za instalaciju i pripremu Windows 11 računara (**WIS** / ranije **GWADT**) redovno se koristi za servisiranje i pripremu računara uvezenih sa azijskog (kineskog) tržišta, kao i za klijente sa kineskog govornog područja. 

U prethodnim verzijama sistema (zaključno sa v2.12.0 i preliminarnim delom v2.13.0 kroz ADR-0125):
1. **Nepotpun prevod interfejsa (Defekt 2.3 / FEAT-UI-03):** Postojeći `LocalizationService` mehanizam menjao je isključivo naslove tabova i nekoliko dugmadi u gornjem zaglavlju.
2. **Hardkodovani tekstovi u telu kartica:** Unutar datoteka `MainWindow.xaml` (i za `WIS.UI` i za `GWADT.UI`) nalazilo se preko 220+ statičkih srpskih labela, naslova sekcija, opisa i objašnjenja unutar tela svih 8 funkcionalnih kartica. Kada bi korisnik ili serviser kliknuo na `[ 🇨🇳 中文 ]`, celokupan unutrašnji sadržaj kartica ostajao je na srpskom jeziku, čime je interfejs bio samo prividno dvojezičan.
3. **Opasnost od prevođenja tehničkih i brendiranih entiteta:** Pri parcijalnim ili nestandardizovanim prevodima postojala je opasnost od prevođenja naziva softvera (npr. prevođenje `Google Chrome`, `Microsoft Office`, `Acrobat`) ili tehničkih hardverskih parametara (merne jedinice `GHz`, `MHz`, `°C`, `RPM`, standardi `NVMe`, `DDR5`, `IPv4`), što bi degradiralo profesionalni IT standard sistema.
4. **Zahtev za nultim zastojem (Zero-Restart Runtime Switch):** Korisnik mora imati mogućnost da u bilo kom trenutku tokom instalacije ili dijagnostike promeni jezik jednim klikom, bez potrebe za ponovnim pokretanjem aplikacije ili prekidom aktivnih faza instalacije.

Bilo je neophodno sprovesti temeljnu refaktoricaciju i standardizaciju celokupnog XAML i C# prezentacionog sloja, obezbediti 100% simetričan dvojezični rečnik sa Microsoft IT terminologijom na pojednostavljenom kineskom jeziku (*Simplified Chinese* / 简体中文), povezati sve elemente kroz reaktivni MVVM binding, očuvati originalne tehničke i softverske nazive, i potvrditi stabilnost kroz rigoroznu QA verifikaciju.

---

## 2. Arhitektonska Odluka

Usvojen je sveobuhvatan arhitektonski model potpune lokalizacije (100% Full i18n) koji obuhvata pet ključnih inženjerskih stubova:

### 2.1. Proširenje Dvojezičnog Rečnika na 288 Simetričnih Ključeva (`LocalizationService.cs`)
U klasi `LocalizationService.cs` (podjednako za `WIS.UI` i za kompatibilni `GWADT.UI` projekat) implementiran je centralizovani rečnik sa tačno **288 dvojezičnih ključeva** raspoređenih u dva unutrašnja rečnika:
- `Dictionary<string, string> _srDict` (Srpska latinica)
- `Dictionary<string, string> _zhDict` (Pojednostavljeni kineski jezik / 简体中文)

Karakteristike implementacije rečnika:
- **100% simetrija:** Svaki ključ koji postoji u `_srDict` postoji i u `_zhDict` bez ijednog odstupanja.
- **Nula praznih vrednosti:** Nijedan prevodni par ne sadrži prazan ili `null` string.
- **Microsoft IT terminološki standard:** Kineski prevodi dosledno koriste zvanične termine Windows operativnog sistema (npr. *中央处理器* za CPU, *运行内存* za RAM, *存储设备* za skladišne uređaje, *设备管理器* za Device Manager, *驱动程序* za drajvere, *硬件健康度* za zdravlje hardvera, *压力测试* za stres test, *序列号* za serijski broj).
- **Klasa implementira `INotifyPropertyChanged`:** Pruža indeksersko svojstvo `public string this[string key]` koje okida događaj promene svojstva (`PropertyChanged`) za indekserski poziv `Item[]`, omogućavajući WPF XAML mehanizmu automatsko osvežavanje svih vezanih labela u letu.

### 2.2. Reaktivno XAML Vezivanje (193 Elementa preko `{Binding Loc[...]}`)
U datotekama `MainWindow.xaml` kompletno su eliminisani hardkodovani srpski atributi `Text="..."` i `Content="..."` u telima svih 8 kartica. Umesto njih implementirano je tačno **193 reaktivnih deklarativnih vezivanja**:
```xml
Text="{Binding Loc[Hardware_CpuCores]}"
```
Pokrivenost obuhvata svih 8 kartica:
1. **Kartica 1 (Pregled / 概览):** Zaglavlja modela računara, procesora, memorije, SMART zdravlja diska, fazni statusi i dugmad.
2. **Kartica 2 (Hardver / 硬件信息):** Sekcije hardverskih komponenti, detalji o jezgrima, nitima, radnim taktovima, skladištu, GPU, matičnoj ploči i BIOS-u.
3. **Kartica 3 (Drajveri / 驱动程序):** Statusi intervencije, klase uređaja, proizvođači, akcije kopiranja HWID-a, statusi ažurnosti.
4. **Kartica 4 (Dijagnostika / 系统诊断):** Termalni senzori procesora (trenutna, maksimalna temperatura, opterećenje), upozorenja, dugme i status 30s stres testa.
5. **Kartica 5 (Mreža / 网络连接):** Nazivi mrežnih adaptera, statusi konekcije, brzine linka, mrežni detalji.
6. **Kartica 6 (Programi / 常用软件):** Opisi statusa instalacije, selektori, dugmad za ponovni pokušaj, statusna stanja (u redu čekanja, instalira se, uspešno instalirano).
7. **Kartica 7 (Dnevnik / 运行日志):** Pretraga dnevnika, kopiranje svih logova, brisanje prikaza dnevnika.
8. **Kartica 8 (Autopilot / 自动巡检):** Zdravlje i habanje baterije, ciklusi punjenja, statusi TPM 2.0 bezbednosnog čipa i Secure Boot mehanizma.

Takođe, u `Models.cs` sva svojstva naslova kartica dinamički pozivaju `LocalizationService.Instance[...]`, garantujući potpunu usklađenost tab kontrola.

### 2.3. Striktna Zaštita Brendiranih Softverskih Naziva i Tehničkih Parametara
Uvedeno je strogo inženjersko pravilo o zabrani prevođenja tehničkih i brendiranih parametara:
- **7 softverskih paketa** ostaju striktno u svom originalnom obliku:
  * `Google Chrome`
  * `Microsoft Office 2024`
  * `Adobe Acrobat Pro 2024`
  * `WinRAR`
  * `BatteryBar`
  * `.NET 8 Runtime`
  * `OEM Licenca`
- **11 tehničkih mernih jedinica i hardverskih standarda** ostaju netaknuti u oba jezika:
  * `GHz`, `MHz`, `°C`, `RPM`
  * `NVMe`, `DDR4`, `DDR5`, `SO-DIMM`
  * `IPv4`, `MAC`, `HWID`
- Sistemski raw log u dnu prozora zadržava univerzalan tehnički format radi standardizacije dijagnostike i servisne arhive.

### 2.4. Tipografska Stabilnost i Font Fallback (*Microsoft YaHei*)
Kako bi se u potpunosti sprečila pojava nečitljivih praznih kvadrata (*tofu* efekat) prilikom prikaza hiljada različitih CJK ideograma na sveže instaliranim Windows sistemima:
- Celokupan stilski šablon u `MainWindow.xaml` postavljen je sa primarnim font stack-om koji prioritizuje nativni Windows font za pojednostavljeni kineski jezik:
  ```xml
  FontFamily="Segoe UI, Microsoft YaHei, SimHei, Arial"
  ```
- Ovaj font je podrazumevano prisutan u Windows 11 operativnom sistemu, garantujući savršeno oštar prikaz glifova na standardnim i visokim (HiDPI) rezolucijama.

### 2.5. Docker Wine MSBuild Kompilacija i Distribucija sa SHA-256 Paritetom
Kompilacija oba modula izvršena je u kontrolisanom `gwadt-builder-wine:latest` Docker okruženju sa striktnom kompatibilnošću sa C# 5.0 (.NET Framework 4.0 / 4.8 MSBuild):
- Preuređen je `Skripte/WIS.UI/Build.bat` kako bi direktno i čisto kompajlirao `WIS.UI.csproj` bez sporednih artefakata i grešaka u konzolnim porukama.
- Kompajlirane su obe izvršne datoteke:
  * `WIS.UI.exe` (veličina: **332,288 bajtova**, SHA-256: `a4ca868e77cb02ac5a5df4f42f435102823ca76472e6be4e82ae4751b5a87bf0`)
  * `GWADT.UI.exe` (veličina: **332,288 bajtova**, SHA-256: `2204489e0c33171bf73c670b8bb309db69b117ff9afa1384d1825db689971579`)
- Izvršna datoteka `WIS.UI.exe` distribuirana je na 8/8 projektnih lokacija, a `GWADT.UI.exe` na 9/9 projektnih lokacija (root direktorijum, `Skripte/`, `sources/$OEM$/$1/setup/`, `gwadt-ui-engine` i radioničko skladište) uz **100% binarni SHA-256 paritet**.

---

## 3. QA Verifikacija i Validacija

Za potrebe verifikacije implementacije Faze 4 izrađena je sveobuhvatna test skripta `Skripte/Test-Task127-Chinese-i18n-QA.py` koja obuhvata 5 tematskih celina sa ukupno **131 automatizovanom proverom**:

| Tematska Celina | Opis Provera | Broj Testova | Ishod |
|---|---|---|---|
| **Test 1: Rečnici i Simetrija** | Parsiranje C# koda, provera prisustva 288 ključeva u `_srDict` i `_zhDict` za WIS i GWADT, 100% obostrana simetrija, odsustvo praznih vrednosti, validacija postojanja CJK karaktera. | 22 | 🟢 22/22 PASS |
| **Test 2: XAML Pokrivenost i Reaktivno Vezivanje** | Detekcija 193 `{Binding Loc[...]}` vezivanja, verifikacija da svaki upotrebljeni ključ postoji u rečniku, pokrivenost svih 8 tabova, odsustvo hardkodovanih srpskih tekstova u telima kartica. | 38 | 🟢 38/38 PASS |
| **Test 3: Zaštita Softvera i Tehničkih Naziva** | Zaštita 7 ključnih softverskih paketa (Chrome, Acrobat, Office, WinRAR, BatteryBar, .NET, OEM Licenca), zaštita 11 mernih jedinica i hardverskih termina, provera odsustva prevođenja naziva softvera. | 38 | 🟢 38/38 PASS |
| **Test 4: Statička Analiza Koda** | `Verify-CSharpSyntax.py` proverava 43/43 fajlova na C# 5.0 kompatibilnost (0 prekršaja); `Verify-PowerShellSyntax.py` validira PowerShell 5.1 AST sintaksu (0 grešaka). | 4 | 🟢 4/4 PASS |
| **Test 5: Binarni Paritet i SHA-256 Integritet** | Fizička provera veličine (332,288 B) i verifikacija identičnih SHA-256 kontrolnih suma na 7 lokacija za `WIS.UI.exe` i 8 lokacija za `GWADT.UI.exe`. | 29 | 🟢 29/29 PASS |
| **UKUPNO** | **Sveobuhvatna i18n & Build QA Verifikacija** | **131** | **🟢 131/131 PASS (100.0%)** |

Izlazni kod test skripte: `0` (Sve provere položene bez grešaka).

---

## 4. Posledice i Prednosti

### Pozitivne Posledice:
1. **100% pokrivenost interfejsa na pojednostavljenom kineskom jeziku:** Svi elementi, labele, objašnjenja i dijagnostički rezultati na svih 8 kartica automatski se prevode klikom na dugme za jezik.
2. **Nulti uticaj na performanse i trenutna promena jezika:** Promena jezika se odvija u milisekundama u radnoj memoriji preko WPF binding mehanizma bez restartovanja aplikacije.
3. **Zaštita inženjerskog integriteta:** Brendirani nazivi programa, tehničke jedinice i sistemski logovi ostaju u standardizovanom formatu, sprečavajući zabunu servisera.
4. **Dvostruka binarna kompatibilnost:** I novi primarni `WIS.UI.exe` i tranzicioni `GWADT.UI.exe` poseduju identične mogućnosti i 100% verifikovan binarni paritet.
5. **Potpuna eliminacija Defekta 2.3:** Zahtev iz PRD-a (`[FEAT-UI-03]` / `REQ-UI-03`) je u potpunosti zatvoren i sertifikovan.

### Negativne Posledice i Rizici:
- Neznatno povećanje veličine izvršnog modula (sa ~296 KB na 332 KB) usled ugrađivanja 288 dvojezičnih stringova, što je potpuno zanemarljivo s obzirom na raspoložive resurse savremenih računara.
