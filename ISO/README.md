# WIS ISO — razvoj (`IG-Systems`)

Komponentna razvojna verzija: **3.4.0**, u WIS paketu **3.4.0** (vlasnik sistema: **IG-Systems**). Kanonski izvor je root [`version.json`](../version.json); `python3 ISO/Skripte/Sync-Versions.py --check` iz root-a proverava generisane verzije i usklađenost deljenih skripti.

Mediji, Pre-OOBE bootstrap (`PreOOBE-Network.ps1`, `SetupComplete.ps1`, `Provera_autopilota.ps1`), offline `Programi/VCRedist` lokacija (van Git-a) i instalacione skripte su ovde; ovo je media root. Iz root-a repozitorijuma Windows build je `powershell.exe -NoProfile -File ISO/Skripte/Build-WIS.ps1 -OutputDir .workspace/artifacts/windows`. Pakovanje i RSA-2048 potpisivanje OTA paketa vrši [`ISO/Skripte/Release-WIS.py`](Skripte/Release-WIS.py), pripremu `$OEM$` medijuma [`ISO/Skripte/Prepare-WISMedia.py`](Skripte/Prepare-WISMedia.py), a proveru potpisanog paketa [`ISO/Skripte/Sign-PayloadManifest.py`](Skripte/Sign-PayloadManifest.py) (`--verify-only`). Istorijske `Skripte/Test-Task*` skripte su legacy QA i nisu aktuelni build gate.

Runtime payload (`C:\setup`) i putanje ostaju stabilni; validacija Windows PowerShell 5.1 AST parsera i MSBuild kompilacije pokreće se kroz `Build-WIS.ps1` u CI/VM okruženju (`windows-2022`), dok potpuna OOBE/WinPE reboot + Anti-Leak provera zahteva namensku Windows 11 VM ili servisni hardver.
