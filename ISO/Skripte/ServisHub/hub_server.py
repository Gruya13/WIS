#!/usr/bin/env python3
"""Kompatibilni entrypoint: jedina implementacija servera je WIS/ServisHub."""
from pathlib import Path
import sys
sys.path.insert(0, str(Path(__file__).resolve().parents[3] / 'ServiceHub'))
from servishub.cli import main
from servishub.payload import PayloadManager
if __name__ == '__main__':
    main()
