---
title: "Sistemske Optimizacije, Automatski DPI i OEM Brendiranje (Branding & Optimization)"
type: architecture
created: 2026-09-26
updated: 2026-10-26
tags:
  - architecture
  - branding
  - optimization
  - dpi-scaling
  - quickedit
  - defender
---

# Sistemske Optimizacije, Automatski DPI i OEM Brendiranje: WIS (v2.17.0)

U okviru Faze 3 (`Phase3-Optimization.ps1`, `WIS.Branding.psm1` i `WIS.Core.psm1`), operativni sistem Windows 11 prolazi kroz prilagođavanje hardveru, ergonomsku optimizaciju radne površine, bezbednosno očvršćavanje i pripremu za kontrolisani međurestart pre Faze 4.

---

## 🎨 1. Pametno Hardversko OEM Brendiranje (`WIS.Branding.psm1` & `WIS.Hardware.psm1`)

WIS dinamički prepoznaje proizvođača i komercijalni naziv modela računara (`Get-HardwareBrand` i hijerarhijska normalizacija modela za Lenovo `Win32_ComputerSystemProduct.Version`, Dell, HP, Asus, Acer, MSI, Gigabyte, Apple):
- **Korisnički nalog:** Inicijalno kreirani nalog `Korisnik` preimenuje se u prepoznati brend (npr. `Lenovo`, `DELL`, `HP`).
- **OEM informacije:** U registarsku granu `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\OEMInformation` upisuju se proizvođač, tačan naziv modela i kontakt podaci servisa **WIS Service**.

---

## 🖥️ 2. Automatsko Dinamičko DPI Skaliranje Ekrana (`Set-ServisAutoDpiScaling`)

Na osnovu fizičke rezolucije ekrana očitane iz displej kontrolera i 128-bajtnog EDID bloka, `Set-ServisAutoDpiScaling` automatski postavlja optimalno skaliranje:
- **Sub-FHD ($< 1920\times 1080$):** `100%` (96 DPI)
- **FHD / FHD+ ($1920\times 1080$, $1920\times 1200$):** `125%` (120 DPI)
- **2K / QHD ($2560\times 1440$, $2560\times 1600$):** `150%` (144 DPI)
- **4K / UHD ($3840\times 2160$):** `200%` (192 DPI)

Putem Win32 P/Invoke poziva (`user32.dll`: `UpdatePerUserSystemParameters` i `SendMessageTimeout` sa `WM_SETTINGCHANGE`) i upisom u `HKCU\Control Panel\Desktop` (`LogPixels`, `Win8DpiScaling = 1`), skaliranje stupa na snagu trenutno ([[decisions/ADR-0023-automatic-display-dpi-scaling]]).

---

## 🛑 3. Imunitet na QuickEdit Zamrzavanje i Skriveni Radnici

1. `FirstLogon.cmd` postavlja `QuickEdit = 0` u `HKCU\Console`.
2. Funkcija `Disable-QuickEdit` u `WIS.Core.psm1` poziva Win32 API `kernel32.dll:SetConsoleMode` i uklanja fleg `ENABLE_QUICK_EDIT_MODE (0x0040)`.
3. Svi fazni PowerShell radnici podižu se iz `WIS.UI.exe` u skrivenom prozoru (`-WindowStyle Hidden`), čime je potpuno onemogućeno slučajno pauziranje izvršavanja klikom miša.

---

## 🛡️ 4. Microsoft Defender Izuzeci i Ergonomija

- **Defender Izuzeci (`Set-DefenderExclusions`):** Tokom rada postavlja izuzetak za `C:\setup` i `C:\Windows\Logs\WIS` kako skeniranje u realnom vremenu ne bi usporavalo raspakivanje arhiva.
- **Regionalne postavke:** Dodaje srpsku latiničnu tastaturu (`sr-Latn-RS`) uz englesku (`en-US`), postavlja 24-časovni format sata (`HH:mm`), vremensku zonu `Central European Standard Time`, prikazuje ikonicu `This PC` na radnoj površini i isključuje telemetrijske servise (`DiagTrack`, `dmwappushservice`) i reklamne predloge u Start meniju.

---

## 🔄 5. Kontrolisani Međurestart (Interim Reboot Gate — `Invoke-ServisInterimReboot`)

Na kraju Faze 3 proverava se marker `C:\setup\.stage_reboot_done`. Ukoliko međurestart još nije obavljen, sistem snima `phase = 4` u `C:\setup\deploy-state.json`, postavlja Winlogon AutoLogon i dvostruki RunOnce/Startup nastavak i izvršava kontrolisani restart kako bi Faza 4 (instalacija MSI aplikacija) počela sa potpuno čistim CBS i Windows Installer stekom ([[decisions/ADR-0039-two-pass-deployment-interim-reboot]]).

---

## 🔗 Povezane Beleške
- Pregled Arhitekture: [[architecture/overview]]
- Pajplajn Faza: [[architecture/pipeline]]
- Automatsko DPI Skaliranje: [[decisions/ADR-0023-automatic-display-dpi-scaling]]
- Dvoetapni Deployment i Međurestart: [[decisions/ADR-0039-two-pass-deployment-interim-reboot]]
