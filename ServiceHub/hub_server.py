#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
hub_server.py
=============
Glavni pokretački wrapper za WIS Service Centralni Servisni Hub (ServisHub).
"""

import sys
import os

# Osiguraj da je root u sys.path
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from servishub.cli import main

if __name__ == "__main__":
    main()
