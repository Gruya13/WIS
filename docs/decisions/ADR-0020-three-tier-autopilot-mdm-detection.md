---
title: "ADR-0020: Precizna Trobojna Autopilot i MDM Detekcija (CISTO / AAD/MDM / AP-REG)"
type: decision
status: accepted
date: 2026-09-17
author: gwadt_orchestrator
tags:
  - decision
  - hardware
  - autopilot
  - mdm
  - azure-ad
  - tui
---

# ADR-0020: Precizna Trobojna Autopilot i MDM Detekcija (CISTO / AAD/MDM / AP-REG)

## 1. Kontekst i Problem

Tokom servisiranja i pripreme klijentskih računara, jedan od ključnih koraka je detekcija da li je uređaj vezan za kompanijski zakup kroz **Windows Autopilot** ili **Mobile Device Management (MDM / Intune)**:
1. **Ograničenja binarne detekcije:**
   - Prethodna implementacija koristila je striktno binarni model: `AP: [! REG]` (crveni alarm) ili `AP: [* CISTO]` (zeleni status).
   - U isti crveni alarm svrstavani su i hardverski Autopilot (gde je matična ploča/hardverski heš zaključan u Microsoft Intune zakupcu) i običan korisnički Azure AD / MDM join (npr. klijent koji se ulogovao na školski/poslovni Microsoft 365 nalog ili Teams, što se očitava kroz `dsregcmd /status`).
   - Ovo je dovodilo do lažnih alarma kod servisera: uređaj koji je samo privremeno imao ulogovan radni nalog tretiran je kao trajno zaključan korporativni Autopilot uređaj.
2. **Sintaksna mana i nepotpune registarske putanje:**
   - Evaluacija registarskog ključa `EstablishedCorrelations` unutar `HKLM:\SOFTWARE\Microsoft\Provisioning\Diagnostics\AutoPilot` nije imala bezbednu proveru tipa, što je moglo izazvati grešku ili lažnu pozitivnost ukoliko je vrednost prazna.
   - Nisu bile pokrivene moderne lokacije za identifikaciju zakupca, poput `CloudAssignedTenantId` i `AutopilotSettings\TenantId`, kao ni Zero-Touch Deployment (`ZTD`) grane u `HKLM:\SOFTWARE\Microsoft\Enrollments`.

---

## 2. Doneta Odluka

Uvedena je sveobuhvatna, hijerarhijska i trobojna detekcija u modulu `GWADT.Hardware.psm1` i TUI prikazu u `GWADT.Core.psm1`:

### 1. Klasifikacija u Tri Nivoa Detekcije
- 🟢 **Zeleni nivo — `* CISTO` (`Clean`):**
  - Računar nema zabeležene hardverske Autopilot parametre niti je povezan na Azure AD / MDM / Workplace mrežu.
  - Prikazuje se zelenom bojom u TUI hederu: `AP: [* CISTO]`.
- 🟡 **Žuti nivo — `~ AAD/MDM` (`AadMdm`):**
  - Računar **NIJE** hardverski zaključan u Autopilot bazi, ali poseduje aktivan Microsoft Cloud nalog (`AzureAdJoined: YES`, `MdmEnrolled: YES` ili `WorkplaceJoined: YES` detektovan preko `dsregcmd.exe /status`).
  - Prikazuje se žutom bojom: `AP: [~ AAD/MDM]`.
- 🔴 **Crveni nivo — `! AP-REG` (`Autopilot`):**
  - Računar je fabrički ili administrativno hardverski registrovan u **Windows Autopilot** bazi.
  - Prikazuje se upečatljivom crvenom bojom: `AP: [! AP-REG]`.

### 2. Algoritam Detekcije u `GWADT.Hardware.psm1`
Logika je strukturirana u jasne faze provere bez lažnih pozitivnih rezultata:
1. **Provera hardverskih Autopilot registarskih markera:**
   - Očitavanje `HKLM:\SOFTWARE\Microsoft\Provisioning\Diagnostics\AutoPilot`:
     - `CloudAssignedTenantDomain` (domen zakupca)
     - `CloudAssignedTenantId` (GUID zakupca)
     - Bezbedna evaluacija `EstablishedCorrelations`:
       ```powershell
       $estVal = if ($apDiag) { $apDiag.EstablishedCorrelations } else { $null }
       $hasEst = ($null -ne $estVal) -and ($estVal.ToString().Trim() -ne "")
       ```
   - Očitavanje `HKLM:\SOFTWARE\Microsoft\Provisioning\AutopilotSettings`:
     - `TenantId` (sekundarni registarski zapis zakupca)
   - Provera Zero-Touch Deployment ključeva:
     - `HKLM:\SOFTWARE\Microsoft\Enrollments\ZTD` i child ključevi koji sadrže `ZTD`.
2. **Dodela statusa:**
   - Ukoliko bilo koji od hardverskih Autopilot markera postoji:
     `$apStatus = "Autopilot"`, `$apPresent = $true`, `$apTenant` se popunjava sa imenom ili ID-jem zakupca.
   - Ukoliko hardverski Autopilot markeri **ne postoje**, pokreće se `dsregcmd.exe /status`:
     - Ako je `AzureAdJoined`, `MdmEnrolled` ili `WorkplaceJoined` jednako `YES`:
       `$apStatus = "AadMdm"`, `$apPresent = $false`.
   - Ukoliko ništa od navedenog nije detektovano:
     `$apStatus = "Clean"`, `$apPresent = $false`, `$apTenant = ""`.

### 3. TUI Vizualizacija u `GWADT.Core.psm1`
U zaglavlju konzole (`Show-ServisDashboard`) status se renderuje putem `switch ($hw.AutopilotStatus)`:
```powershell
Write-Host "]  AP: [" -ForegroundColor DarkGray -NoNewline
switch ($hw.AutopilotStatus) {
    "Autopilot" { Write-Host "! AP-REG" -ForegroundColor Red -NoNewline }
    "AadMdm"    { Write-Host "~ AAD/MDM" -ForegroundColor Yellow -NoNewline }
    default     {
        if ($hw.Autopilot) {
            Write-Host "! AP-REG" -ForegroundColor Red -NoNewline
        } else {
            Write-Host "* CISTO" -ForegroundColor Green -NoNewline
        }
    }
}
Write-Host "]  SB: [" -ForegroundColor DarkGray -NoNewline
```

---

## 3. Posledice Odluke

### Pozitivne posledice:
1. **Eliminacija lažno pozitivnih alarma:** Serviseri više ne dobijaju crveni alarm na mašinama koje imaju samo ulogovan radni/školski nalog bez hardverske Autopilot registracije.
2. **Granularna transparentnost:** Jasno se razlikuje situacija gde je neophodno kontaktirati IT administraciju kompanije radi deregistracije matične ploče (crveno) od situacije gde je dovoljno samo odjaviti nalog kroz Windows Settings (žuto).
3. **Povećana pouzdanost i robustnost:** Uklonjene sintaksne i tipizacijske slabosti pri obradi `EstablishedCorrelations` i dodate provere za sve poznate Windows 11 Autopilot registarske grane.
4. **Nulta regresija i puna kompatibilnost:** Očuvana 100% kompatibilnost sa Windows PowerShell 5.1 i celokupnim QA test runnerom (`Skripte/Test-GWADTModules.py` — 8/8 testova PASS).

## 4. Povezane Beleške
- Početni indeks: [[index]]
- Pregled arhitekture: [[architecture/overview]]
- Hardverska zaobilaženja: [[architecture/hardware-bypass]]
