#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
hub_cli.py
==========
Konzolni terminal monitor za praćenje rada flote računara u servisu Tehno-Kutak.
"""

import sys
import os

# Osiguraj da je root u sys.path
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from servishub.tui_cli import main

if __name__ == "__main__":
    main()
