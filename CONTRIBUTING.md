# Smernice za doprinos (Contributing Guide) — WIS (`IG-Systems`)

Hvala što doprinosite razvoju projekta **Windows Installation Assistant (WIS)** u vlasništvu **IG-Systems**.

---

## 1. Jezička i Kodna Pravila

1. **Komunikacija i dokumentacija:** Dokumentacija, komentari, logovi za servisera i predlozi izmena pišu se na srpskom jeziku.
2. **Windows PowerShell 5.1 kompatibilnost (`UI/Skripte/`, `ISO/`):**
   - Zabranjeni su operatori specifični za PowerShell 7+ (`??`, `??=`, `?:`, `&&`, `||`).
   - Sve hash mape moraju imati jedinstvene ključeve i čistu sintaksu.
   - Nakon izmena obavezno pokrenuti `python3 ISO/Skripte/Verify-PowerShellSyntax.py`, a na Windows-u `UI/Tests/Test-PowerShell51Parse.ps1`.
3. **C# 5.0 kompatibilnost (`Launcher/`, `UI/`, `UI/HwTest/`, `Tools/AutopilotAuditor/`):**
   - Projekat se na Windows runneru (`windows-2022`) prevodi sa `/p:LangVersion=5`.
   - Zabranjena je interpolacija stringova (`$"..."`), `nameof()`, null-conditional operator (`?.`), expression-bodied članovi (`=>`), `out var` i `using var`.
   - Nakon izmena obavezno pokrenuti `python3 ISO/Skripte/Verify-CSharpSyntax.py` i build skripte.
4. **Upravljanje verzijama:**
   - Korenski [`version.json`](version.json) je jedini izvor istine za verzije.
   - Posle promene verzije ili deljenih skripti pokrenuti `python3 ISO/Skripte/Sync-Versions.py` i proveriti `python3 ISO/Skripte/Sync-Versions.py --check`.

---

## 2. Bezbednost i Politika Tajni

- **Nikada** ne dodavati `Wifi-Servis.ini`, servisne lozinke, `.env` datoteke, WLAN XML profile sa `<keyMaterial>`, privatne ključeve (`*.pem`, `*.key`, `*.pfx`), realne serijske brojeve/hostname-ove klijenata niti ranjive third-party drajvere (`WinRing0` / `LibreHardwareMonitorLib.dll`).
- Svaki novi unos za instalaciju aplikacija ili drajvera mora poštovati `StepGuard`, SHA-256 / RSA-2048 proveru i Faza 6 Anti-Leak čišćenje.

---

## 3. Obavezne Provere Pre Slanja Izmene

Pokrenuti kompletan paket lokalnih verifikacija iz korena repozitorijuma:

```bash
python3 ISO/Skripte/Sync-Versions.py --check
python3 ISO/Skripte/Verify-PowerShellSyntax.py
python3 ISO/Skripte/Verify-CSharpSyntax.py
python3 Launcher/Tests/run_tests.py
python3 -m unittest UI/Tests/Test-HwTestLogic.py UI/Tests/Test-AutopilotPreOobeCache.py UI/Tests/Test-UpdateAndDriversLogic.py
python3 -m py_compile "ISO/USB Tool/core/system_inspector.py"
(cd ServiceHub && python3 -m unittest discover -s tests)
git diff --check
```

---

## 4. Arhitektonske Odluke (ADR)

Značajne izmene arhitekture, protokola ili bezbednosnog modela dokumentuju se kroz novi ADR zapis u `docs/decisions/ADR-XXXX-*.md` i unos u [`CHANGELOG.md`](CHANGELOG.md).
