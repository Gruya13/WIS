#!/usr/bin/env python3
"""
Skripta za determinističko prevođenje (kompilaciju) WIS.Launcher.exe pomoću Mono Roslyn csc.exe
i zvaničnih Microsoft .NET Framework 4.8 referentnih sklopova (C# 5.0 kompatibilno).
"""

import sys
import shutil
import hashlib
import pathlib
import subprocess

ROOT = pathlib.Path(__file__).resolve().parent.parent
LAUNCHER_DIR = ROOT / "Launcher"
REF_DIR = pathlib.Path.home() / ".local/share/wis-tools/net48/build/.NETFramework/v4.8"
CSC_EXE = pathlib.Path("/usr/lib/mono/4.5/csc.exe")


def main():
    if not CSC_EXE.exists():
        print(f"[-] Greška: Roslyn kompajler nije pronađen na {CSC_EXE}", file=sys.stderr)
        return 1

    if not REF_DIR.exists():
        print(f"[-] Greška: .NET Framework reference nisu pronađene na {REF_DIR}", file=sys.stderr)
        return 1

    tmp = pathlib.Path("/tmp/wis-launcher-build")
    tmp.mkdir(parents=True, exist_ok=True)
    out_exe = tmp / "WIS.Launcher.exe"
    if out_exe.exists():
        out_exe.unlink()

    cmd = [
        "mono", str(CSC_EXE),
        "/noconfig", "/nowarn:1701,1702", "/nostdlib+",
        "/platform:anycpu", "/errorreport:prompt", "/warn:4",
        "/define:TRACE", "/highentropyva+", "/langversion:5",
        f"/reference:{REF_DIR}/mscorlib.dll",
        f"/reference:{REF_DIR}/System.dll",
        f"/reference:{REF_DIR}/System.Core.dll",
        f"/reference:{REF_DIR}/System.Drawing.dll",
        f"/reference:{REF_DIR}/System.Windows.Forms.dll",
        f"/reference:{REF_DIR}/System.Web.Extensions.dll",
        "/debug:pdbonly", "/filealign:512", "/optimize+",
        f"/out:{out_exe}",
        "/target:winexe",
        f"/win32icon:{LAUNCHER_DIR}/Assets/WIS.ico",
        f"/win32manifest:{LAUNCHER_DIR}/app.manifest",
        "/deterministic+",
        str(LAUNCHER_DIR / "Program.cs"),
        str(LAUNCHER_DIR / "SplashForm.cs"),
        str(LAUNCHER_DIR / "WifiHelper.cs"),
        str(LAUNCHER_DIR / "HubDiscovery.cs"),
        str(LAUNCHER_DIR / "PayloadManager.cs"),
        str(LAUNCHER_DIR / "Properties/AssemblyInfo.cs"),
    ]

    print("[*] Pokrećem Roslyn csc.exe kompilaciju za WIS.Launcher.exe...")
    res = subprocess.run(cmd, capture_output=True, text=True)
    if res.returncode != 0:
        print("[-] Kompilacija nije uspela! Exit code:", res.returncode, file=sys.stderr)
        if res.stdout:
            print("STDOUT:\n", res.stdout, file=sys.stderr)
        if res.stderr:
            print("STDERR:\n", res.stderr, file=sys.stderr)
        return res.returncode

    if not out_exe.exists():
        print("[-] Greška: Izlazni fajl nije generisan!", file=sys.stderr)
        return 1

    size = out_exe.stat().st_size
    sha256 = hashlib.sha256(out_exe.read_bytes()).hexdigest()
    print(f"[+] Kompilacija uspešna! Veličina: {size} bajtova, SHA-256: {sha256}")

    destinations = [
        LAUNCHER_DIR / "WIS.Launcher.exe",
        ROOT / "ISO/WIS.Launcher.exe",
        ROOT / "ISO/sources/$OEM$/$1/setup/WIS.Launcher.exe",
        ROOT / ".workspace/artifacts/windows/WIS.Launcher.exe",
    ]
    for dst in destinations:
        if dst.parent.exists():
            shutil.copy2(out_exe, dst)
            print(f"    -> Iskopirano u: {dst}")

    return 0


if __name__ == "__main__":
    sys.exit(main())
