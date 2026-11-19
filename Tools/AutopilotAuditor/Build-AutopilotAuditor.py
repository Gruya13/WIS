#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
Build skripta za WIS Autopilot Forensic Auditor Standalone EXE
Kompajlira C# kod u prenosivi .NET Framework 4.8 Single-File izvršni fajl.
"""

import os
import sys
import subprocess
import shutil
import hashlib

def main():
    script_dir = os.path.dirname(os.path.abspath(__file__))
    project_root = os.path.abspath(os.path.join(script_dir, "..", ".."))
    
    net48_ref_dir = os.path.expanduser("~/.local/share/wis-tools/net48/build/.NETFramework/v4.8")
    if not os.path.isdir(net48_ref_dir):
        print(f"[-] Greška: Referentni .NET 4.8 direktorijum nije pronađen: {net48_ref_dir}")
        sys.exit(1)

    csc_cmd = shutil.which("csc")
    if not csc_cmd:
        print("[-] Greška: C# Roslyn kompajler (csc) nije instaliran na sistemu.")
        sys.exit(1)

    out_exe = os.path.join(script_dir, "WIS-AutopilotAuditor.exe")
    manifest_path = os.path.join(script_dir, "app.manifest")
    icon_path = os.path.join(project_root, "UI", "Assets", "WIS.ico")
    if not os.path.isfile(icon_path):
        icon_path = os.path.join(project_root, "Launcher", "Assets", "WIS.ico")

    cs_files = [
        os.path.join(script_dir, "Model.cs"),
        os.path.join(script_dir, "HardwareInspector.cs"),
        os.path.join(script_dir, "LocalForensicsInspector.cs"),
        os.path.join(script_dir, "CloudZtdInspector.cs"),
        os.path.join(script_dir, "DecisionMatrix.cs"),
        os.path.join(script_dir, "ReportExporter.cs"),
        os.path.join(script_dir, "MainWindow.cs"),
        os.path.join(script_dir, "Program.cs")
    ]

    for f in cs_files:
        if not os.path.isfile(f):
            print(f"[-] Greška: Nedostaje C# izvorni fajl: {f}")
            sys.exit(1)

    ref_dlls = [
        "mscorlib.dll",
        "System.dll",
        "System.Core.dll",
        "System.Data.dll",
        "System.Xml.dll",
        "System.Management.dll",
        "System.Net.Http.dll",
        "WindowsBase.dll",
        "PresentationCore.dll",
        "PresentationFramework.dll",
        "System.Xaml.dll"
    ]

    r_args = []
    for dll in ref_dlls:
        dll_path = os.path.join(net48_ref_dir, dll)
        if not os.path.isfile(dll_path):
            print(f"[-] Upozorenje: Nedostaje referenca: {dll_path}")
        r_args.append(f"/r:{dll_path}")

    compile_cmd = [
        csc_cmd,
        "/noconfig",
        "/nostdlib+",
        "/target:winexe",
        "/platform:anycpu",
        "/optimize+",
        "/deterministic+",
        f"/out:{out_exe}",
        f"/win32manifest:{manifest_path}",
    ]

    if os.path.isfile(icon_path):
        compile_cmd.append(f"/win32icon:{icon_path}")

    compile_cmd.extend(r_args)
    compile_cmd.extend(cs_files)

    print("================================================================================")
    print("     KOMPILACIJA: WIS Autopilot Forensic Auditor Standalone EXE")
    print("================================================================================")
    print(f"[*] Kompajler:  {csc_cmd}")
    print(f"[*] Target:     .NET Framework 4.8 (Any CPU, WinExe)")
    print(f"[*] Manifest:   {manifest_path}")
    print(f"[*] Ikona:      {icon_path}")
    print(f"[*] Izlaz:      {out_exe}")
    print("[*] Kompajliranje u toku...")

    res = subprocess.run(compile_cmd, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
    if res.returncode != 0:
        print("[-] GREŠKA PRI KOMPILACIJI:")
        print(res.stdout)
        print(res.stderr)
        sys.exit(res.returncode)

    file_size = os.path.getsize(out_exe)
    with open(out_exe, "rb") as f:
        sha256 = hashlib.sha256(f.read()).hexdigest()

    print("[+] KOMPILACIJA USPEŠNA!")
    print(f"    • Izlazni fajl: {out_exe}")
    print(f"    • Veličina:     {file_size:,} bajtova ({file_size / (1024*1024):.2f} MB)")
    print(f"    • SHA-256:      {sha256}")
    print("================================================================================")

    # Opciono: Kopiranje u .workspace/artifacts/ za centralno čuvanje
    artifacts_dir = os.path.join(project_root, ".workspace", "artifacts", "tools")
    os.makedirs(artifacts_dir, exist_ok=True)
    target_artifact = os.path.join(artifacts_dir, "WIS-AutopilotAuditor.exe")
    shutil.copy2(out_exe, target_artifact)
    print(f"[+] Kopirano u arhivu: {target_artifact}")

    return 0

if __name__ == "__main__":
    sys.exit(main())
