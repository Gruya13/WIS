#!/usr/bin/env python3
"""Compile production C#5/.NET4 source and execute the real PayloadManager in Mono.
Windows process locking, COM extraction and elevated helper startup require Windows QA.
Only LAUNCHER_TESTS redirects C:\\setup and substitutes an ephemeral test public key.
"""
from pathlib import Path
import os
import shutil
import subprocess
import tempfile

SOURCE = Path(__file__).resolve().parents[1]
with tempfile.TemporaryDirectory(prefix='wis-launcher-tests-') as temporary:
    temp = Path(temporary)
    subprocess.run(['mcs', '-sdk:4', '-langversion:5', '-target:winexe',
                    '-r:System.Drawing', '-r:System.Windows.Forms', '-r:System.Web.Extensions',
                    f'-out:{temp / "WIS.Launcher.exe"}',
                    *map(str, SOURCE.glob('*.cs')), str(SOURCE / 'Properties' / 'AssemblyInfo.cs')], check=True)
    subprocess.run(['mcs', '-sdk:4.5', '-langversion:5', '-define:LAUNCHER_TESTS',
                    '-r:System.Web.Extensions', '-r:System.IO.Compression', '-r:System.IO.Compression.FileSystem',
                    f'-out:{temp / "LauncherTests.exe"}', str(SOURCE / 'PayloadManager.cs'),
                    str(Path(__file__).with_name('LauncherBehaviorTests.cs'))], check=True)
    # Windows uses tar.exe; Linux libarchive exercises the same production process/extract path.
    os.symlink(shutil.which('bsdtar'), temp / 'tar.exe')
    environment = dict(os.environ, WIS_TEST_SETUP_DIR=str(temp / 'setup'), PATH=str(temp) + os.pathsep + os.environ['PATH'])
    subprocess.run(['mono', str(temp / 'LauncherTests.exe')], env=environment, check=True)
