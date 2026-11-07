# WIS ISO — razvoj

Komponentna razvojna verzija: **2.17.0**, u WIS paketu **2.17.0**. Kanonski izvor je root `version.json`; `python3 ISO/Skripte/Sync-Versions.py --check` iz root-a proverava generisane verzije. [Pravila verzionisanja](../.workspace/wiki/operations/component-versioning-2026-10-15.md).

Mediji i instalacioni bootstrap su ovde; ovo je media root. USB Tool je zasebni alat u ovom direktorijumu. Iz root-a repozitorijuma Windows build je `powershell.exe -NoProfile -File ISO/Skripte/Build-WIS.ps1 -OutputDir .workspace/artifacts/windows`. Release i priprema medijuma koriste `ISO/Skripte/Release-WIS.py` i `ISO/Skripte/Prepare-WISMedia.py`. Istorijske `Skripte/Test-Task*` skripte su legacy QA, nisu aktuelni build gate.

Runbook: [build/release/media](../.workspace/wiki/operations/build-release-media-2.17.md). Runtime payload i `C:\setup` putanje ostaju iste. Windows build/VM/E2E za novi raspored nisu izvršeni.
