#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
Root wrapper za Skripte/Verify-PowerShellSyntax.py
"""
import sys
import subprocess
from pathlib import Path

if __name__ == "__main__":
    target = Path(__file__).parent / "Skripte" / "Verify-PowerShellSyntax.py"
    res = subprocess.run([sys.executable, str(target)] + sys.argv[1:])
    sys.exit(res.returncode)
