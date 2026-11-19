# ADR-0238: Nezavisna Bezbednosna Revizija i Jedinstvena Bezbednosna Politika WIS Ekosistema

## Status
Prihvaćeno i dokumentovano (2026-11-17) — politika je aktivna odmah u [`SECURITY.md`](../../SECURITY.md); plan sanacije identifikovanih nalaza (`SEC-WIS-001` .. `SEC-WIS-007`) zakazan je za naredni release ciklus kako se ne bi narušio postojeći potpisani `wis-payload.zip` paket.

## Kontekst
Nakon stabilizacije izdanja `WIS v3.0.0` / `ServiceHub v3.3.0` (`ADR-0225`, `ADR-0236`) i konsolidacije dokumentacije (`WIS-TASK-237`), ukazala se potreba da se sprovede sveobuhvatna, nezavisna bezbednosna revizija celokupnog ekosistema (`ISO/`, `Launcher/`, `UI/`, `ServiceHub/` i `Tools/AutopilotAuditor/`) i da se umesto sažetka od 26 linija u dotadašnjem `SECURITY.md` uspostavi formalna, tehnički obavezujuća bezbednosna politika za sve komponente.

## Donete Odluke

1. **Uspostavljanje Višeslojne Bezbednosne Politike ([`SECURITY.md`](../../SECURITY.md)):**
   - Definisana je matrica podržanih verzija (`WIS 3.0.0`, `ISO 3.0.0`, `Launcher 3.0.0 (min: 2.2.17)`, `UI & HwTest 3.0.0`, `ServiceHub 3.3.0`, `AutopilotAuditor 3.0.0`) i četiri granice poverenja (*Build/Release Host*, *Nepouzdana Servisna LAN/WLAN/PXE Mreža*, *Klijentski Računar `C:\setup\`*, *Eksterni Oblak*).
   - Svakoj od pet celina dodeljena su eksplicitna bezbednosna pravila:
     - **`ISO/`:** Zabrana ugradnje `Wifi-Servis.ini`, `*WLAN*.xml`, `*.pem`/`*.key`/`*.pfx` u medijume i arhive (`forbidden()` u `releases.py`), obavezan XML escaping (`SecurityElement::Escape`) i `finally` brisanje privremenog XML-a u `SetupComplete.ps1` i `PreOOBE-Network.ps1`, te obavezni `build-attestation.json` i `vm-validation.json` za `Release-WIS.py publish`.
     - **`Launcher/`:** Ugrađeni RSA-2048 koren poverenja (`TrustedPublicKeyXml`), kanonska provera `WIS|v2|...`, binarni parser protiv *Zip-Slip*/ZIP64/šifrovanih/symlink ulaza (`ValidateZipEntriesAgainstZipSlip`), blokada NTFS `ReparsePoint` tačaka, brisanje nepotpisanih izvršnih fajlova (`RemoveUnlistedPayloadExecutables`) i transakcioni rollback pod mutexom `Global\WIS_Launcher_Update_Mutex`.
     - **`UI/` & `WIS.HwTest`:** Procesna izolacija faza, `Global\WIS_UI_SingleInstance_Mutex`, `Select-RelevantDriverInfFiles` SUBSYS filter, `Test-IsGenericComputerModel` blokada deljenog `<Model>.tar`, `1.5 GiB` limit u `Export-ServisHardwareDrivers`, odsustvo ugrađenog `WinRing0` drajvera (Anti-BYOVD), te **obavezna Faza 6 Anti-Leak & Handover Gate politika** (`Clear-ServisWifiSecrets`, `anti_leak_verified`, `Restore-ServisUacDefaults`, `Stop-ServisScreenCapture`).
     - **`ServiceHub/`:** Read-only serviranje potpisanog payload-a bez privatnog ključa (`payload.py`), sanitizacija ID-jeva i naziva paketa, atomski upis i ZFS/`zpool`/sistemski safety guardrails u `servishub/wis/usb.py`.
     - **`Tools/AutopilotAuditor/`:** Read-only forenzika sa `try/finally` obnavljanjem OOBE ključeva i zaštitom potvrđenog `autopilot_report.json` keša.
   - Standardizovane su `.gitignore` zabrane, sanitizacija logova i procedura hitne rotacije RSA-2048 i servisnih akreditiva.

2. **Dokumentovanje Nezavisne Bezbednosne Revizije ([`docs/security/NEZAVISNA_BEZBEDNOSNA_REVIZIJA.md`](../security/NEZAVISNA_BEZBEDNOSNA_REVIZIJA.md)):**
   - Potvrđeno je `12` postojećih bezbednosnih kontrola (`CTRL-01` .. `CTRL-12`) u izvornom kodu i Git indeksu.
   - Klasifikovano je `7` konkretnih nalaza sa CVSS v3.1 ocenama i tačnim lokacijama u kodu za plansko otklanjanje u sledećem build ciklusu:
     - **`SEC-WIS-001` [HIGH | 7.5]:** Nevalidirani `directory` / `iso_path` parametri na `ServiceHub` HTTP rutama.
     - **`SEC-WIS-002` [HIGH | 7.1]:** Zaostale privremene polise na klijentu nakon Faze 6 (`AllowInsecureGuestAuth=1`, `RequireSecuritySignature=0`, Defender `ExclusionPath`, `SEE_MASK_NOZONECHECKS=1`, `LowRiskFileTypes`, `AutoAdminLogon`).
     - **`SEC-WIS-003` [MEDIUM | 5.9]:** Odsustvo `SecurityElement.Escape` i wildcard čišćenja za `WIS_TempWifi_*.xml` u `Launcher/WifiHelper.cs`.
     - **`SEC-WIS-004` [MEDIUM | 5.3]:** Nedostatak gornjeg limita `Content-Length` za JSON/upload POST rute i zaštitnih HTTP zaglavlja u `handlers.py`.
     - **`SEC-WIS-005` [MEDIUM | 4.8]:** Široka `icacls` dozvola `*S-1-1-0:(OI)(CI)F` (*Everyone Full Control*) u `FirstLogon.ps1`/`WIS.Core.psm1` i bezuslovni TLS callback u `CloudZtdInspector.cs`.
     - **`SEC-WIS-006` [LOW | 3.3]:** Nepotpuna sanitizacija `<keyMaterial>` i `net use` akreditiva u `Write-ServisLog` (`WIS.Common.psm1`).
     - **`SEC-WIS-007` [LOW | 3.1]:** Odsustvo opcionog API tokena za destruktivne Hub rute uz `privileged: true` u `docker-compose.yml`.

3. **Očuvanje Integriteta Aktivnog Potpisanog Izdanja:**
   - U okviru `WIS-TASK-238` namerno nisu menjani potpisani izvršni binarci, PowerShell moduli niti skripte medijuma kako bi potpisani `wis-payload.zip` ostao kriptografski validan do sledećeg planiranog Windows VM build i release ciklusa.

## Posledice
- **Pozitivne:** Svaka komponenta WIS sistema sada ima jasnu, proverljivu bezbednosnu politiku i mapu pretnji; tim ima prioritetizovan plan sanacije (`P1`/`P2`/`P3`) sa tačnim referencama fajlova i linija koda.
- **Operativne:** Primena kodnih ispravki za `SEC-WIS-002`, `SEC-WIS-003`, `SEC-WIS-005` i `SEC-WIS-006` u narednom izdanju zahtevaće ponovni Windows build, VM validaciju (`vm-validation.json`) i novo `Release-WIS.py publish` potpisivanje, dok se `SEC-WIS-001`, `SEC-WIS-004` i `SEC-WIS-007` mogu primeniti nezavisno na `ServiceHub` serveru.
