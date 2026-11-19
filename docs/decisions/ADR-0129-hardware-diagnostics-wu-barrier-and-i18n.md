---
title: "ADR-0129: Unapređenje Hardverske Telemetrije (EDID Matrica, Nits, Gamut, Eliminacija Lažnih Periferija), Windows Update Barijera Faze 5 i Kineska i18n Lokalizacija"
---

# ADR-0129: Unapređenje Hardverske Telemetrije (EDID Matrica, Nits, Gamut, Eliminacija Lažnih Periferija), Windows Update Barijera Faze 5 i Kineska i18n Lokalizacija

## 1. Kontekst i Problem

Tokom rada i servisnih priprema prenosnih računara u sistemu **WIS** (Windows Installation Assistant) v2.13.1, primećeno je nekoliko kritičnih hardverskih, procesnih i lokalizacionih defekata:

1. **Nedovoljno detaljna specifikacija displeja:**
   - Prethodna verzija prikazivala je samo rezoluciju i dijagonalu (npr. `15.6" FHD (1920x1080 @ 60Hz)`), bez fabričkog koda matrice (npr. `B156HAN02.1`), nits osvetljenja (npr. `300 nits`) i raspona boja (% sRGB i % NTSC), što je ključno za procenu vrednosti i tačne specifikacije laptopa.

2. **Lažno pozitivna detekcija IR kamere (Windows Hello Face):**
   - Sistem je često prijavljivao `👁️ Da` za infracrvenu kameru čak i na mašinama koje je nemaju, usled očitavanja generičkog registri ključa `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Authentication\LogonUI\FaceLogon` i virtuelnih softverskih uređaja (`SWC\MS_FACE_AUTH`).

3. **Lažno pozitivna detekcija čitača otiska prsta (Fingerprint):**
   - `Win32_PnPEntity` u klasi `Biometric` obuhvata i `Windows Hello Face Software Device`. Zbog toga je interfejs prijavljivao čitač otiska prsta na uređajima koji ga uopšte ne poseduju.

4. **Nepouzdana detekcija svetleće tastature:**
   - Standardni PnP upiti nisu pokrivali specifične OEM WMI interfejse (Lenovo, Dell, HP), a nedostatak trostepenog statusa (`Da`, `Proveriti ručno`, `Ne`) dovodio je do pogrešnih zaključaka na laptopovima bez WMI drajvera.

5. **Nedostatak detekcije audio izlaza i mikrofona:**
   - Nije postojala jasna telemetrija za ugrađene zvučnike i mikrofone (`AudioEndpoint`), pa serviser nije mogao odmah videti da li audio drajver radi.

6. **Preuranjeni završetak automatizacije pre kompletiranja Windows Update-a:**
   - Program bi završio sve faze i oglasio zvučni signal za kraj servisa dok su se u pozadini i dalje asinhrono preuzimali i instalirali Windows ažuriranja i drajveri (`MoUsoCoreWorker.exe`, `TiWorker.exe`, BITS poslovi).

7. **Nepotpuna lokalizacija na kineski jezik (i18n):**
   - Autopilot statusi, preporuke i forenzički logovi bili su delimično na srpskom/engleskom kada je aktivan kineski jezik, a promena jezika u radu nije reaktivno osvežavala dinamičke hardverske stringove bez ponovnog pokretanja skeniranja.

---

## 2. Arhitektonska Odluka

Usvojen je modularni inženjerski paket izmena koji rešava svih 7 problema:

### 2.1. EDID Dekodiranje i CIE Hromatski Trougao (HW-01 & Ekran)
- **Očitavanje sirovog EDID bloka:** Iz `HKLM\SYSTEM\CurrentControlSet\Enum\DISPLAY\...\Device Parameters\EDID` čita se 128-bajtni blok.
- **Ekstrakcija matrice:** Iz deskriptora na ofsetima 54, 72, 90 i 108 čita se ASCII naziv ekrana (Tag `0xFC` i `0xFE`).
- **Proračun boja:** Iz bajtova 25–34 očitavaju se 10-bitne CIE xy koordinate crvene, zelene i plave tačke. Primenom Shoelace formule računa se površina trougla:
  $$\text{Površina} = 0.5 \times |(R_x(G_y - B_y) + G_x(B_y - R_y) + B_x(R_y - G_y))|$$
  - $\% \text{ NTSC} = \text{Round}\left(\frac{\text{Površina}}{0.1582} \times 100\right)$
  - $\% \text{ sRGB} = \text{Round}\left(\frac{\text{Površina}}{0.1118} \times 100\right)$
- **Osvetljenje (Nits):** Određuje se na bazi gamuta i tipa matrice (400 nits za OLED, 350 nits za 100% sRGB, 300 nits za IPS, 250 nits za osnovne panele).
- **Implementacija:** Realizovano simetrično u C# WPF engine-u (`WIS.UI` / `GWADT.UI`) i PowerShell modulu `WIS.Hardware.psm1`.

### 2.2. Rigorozno Filtriranje IR Kamere i Fingerprinta (HW-04 & HW-05)
- **IR Kamera:** Trajno uklonjen `FaceLogon` registri ključ; filtriraju se uređaji sa prefiksima `SWC\`, `MS_FACE_AUTH`, `ROOT\`, kao i softverski emulatori. Zahteva se stvaran fizički uređaj sa `ConfigManagerErrorCode == 0`.
- **Otisak prsta:** Iz klase `Biometric` eksplicitno se filtriraju uređaji koji sadrže reči `Face`, `Software`, `Virtual`, `MS_FACE` ili počinju sa `ROOT\`.

### 2.3. OEM WMI Tastatura i Trostepeni Status (HW-06)
- Uvedena hijerarhija provera: PnP pretraga -> Lenovo WMI (`Lenovo_SetKeyboardBacklightStatus`) -> Dell WMI (`DCIM_BiosEnumeration`) -> HP WMI (`HP_BIOSSetting`).
- Uveden trostepeni status za laptop računare: ukoliko WMI telemetrija nije izložena, status je `"Standardna / Proveri ručno (Fn+Space)"` uz žuti indikator, umesto kategoričkog "Ne".

### 2.4. Audio Podsistem i Mikrofon (HW-07)
- Pretraga `Win32_PnPEntity` za `AudioEndpoint` klasu: `Render` za zvučnike i `Capture` za mikrofon, uz fallback na `Win32_SoundDevice`. Prikaz u Kartici 7 (Periferije) i u servisnom kartonu.

### 2.5. Windows Update Barijera Faze 5 (`Wait-WindowsUpdateCompletionBarrier`)
- Implementirana barijera u `WIS.Update.psm1` koja se poziva u `Phase5-Finalize.ps1` i `Invoke-Phase5Finalize`.
- Barijera u petlji (interval 4s, timeout 15 min / 900s) prati:
  - Procese: `MoUsoCoreWorker.exe`, `TiWorker.exe`, `TrustedInstaller.exe`, `UsoClient.exe`.
  - BITS poslove (`Get-BitsTransfer`).
- Podržava trenutni prekid ukoliko operater klikne "Preskoči" (`Test-ServisSkipRequested`).
- Periodično šalje IPC napredak i Write-ServisLog zapise sa preostalim vremenom do timeout-a.

### 2.6. Potpuna Kineska i18n Lokalizacija i Reaktivnost
- Dodato 45+ novih ključeva u `LocalizationService.cs` sa 100% simetrijom (363 SR i 363 ZH ključeva).
- Uveden statički događaj `LanguageChanged` i metoda `GetString(key, fallback)`.
- Svi dinamički hardverski i Autopilot statusi ažuriraju se reaktivno pri promeni jezika preko `RefreshLocalization()` metoda na ViewModelima.

---

## 3. Posledice i Verifikacija

- **Kompilacija:** Oba projekta (`WIS.UI` i `GWADT.UI`) kompajlirani u Docker Wine MSBuild okruženju (`gwadt-builder-wine:latest`) sa 0 Errors / 0 Warnings (veličina 363,008 B).
- **Binarni paritet:** Očuvan 100% SHA-256 binarni paritet na svih 19 lokacija u sistemu:
  - `WIS.UI.exe`: `03f1af4a3385af915f91158be324ad87ec691fd01e8b42b202d662f0fbe16e4b` (9 lokacija)
  - `GWADT.UI.exe`: `40b31c2296eef426b7106b9dcd2b104c84ca82cb449a5447667e93430a9341bb` (10 lokacija)
- **Statička analiza:**
  - `Verify-CSharpSyntax.py`: 43/43 PASS (100% C# 5.0).
  - `Verify-PowerShellSyntax.py`: 100% validan AST za PS 5.1.
- **QA Verifikacija:** Namenska skripta `Test-Task129-Hardware-WU-And-i18n-QA.py` prošla sa **70/70 PASS (100.0%)**.
