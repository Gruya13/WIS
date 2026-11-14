# WIS ISO — razvoj

Komponentna razvojna verzija: **3.0.0**, u WIS paketu **3.0.0**. Kanonski izvor je root `version.json`; `python3 ISO/Skripte/Sync-Versions.py --check` iz root-a proverava generisane verzije. [Pravila verzionisanja](../.workspace/wiki/operations/component-versioning-2026-10-15.md).

Mediji, Pre-OOBE bootstrap (`PreOOBE-Network.ps1`, `SetupComplete.ps1`, `Provera_autopilota.ps1`) i instalacione skripte su ovde; ovo je media root. USB Tool je zasebni alat u ovom direktorijumu. Iz root-a repozitorijuma Windows build je `powershell.exe -NoProfile -File ISO/Skripte/Build-WIS.ps1 -OutputDir .workspace/artifacts/windows`. Pakovanje i potpisivanje payload-a vrši `ISO/Skripte/Release-WIS.py` (`wis-payload.zip`, RSA-2048 potpisano), a pripremu medijuma `ISO/Skripte/Prepare-WISMedia.py`. Istorijske `Skripte/Test-Task*` skripte su legacy QA, nisu aktuelni build gate.

Runbook: [build/release/media](../.workspace/wiki/operations/build-release-media-2.17.md). Runtime payload (`C:\setup`) i putanje ostaju stabilni; trenutna validacija je `development` (`windows_validation: pending`, Windows build/VM E2E izvršava se na ciljnoj Windows mašini).
