#!/usr/bin/env python3
"""Kompatibilni verify-only ulaz. Objavu vodi Release-WIS.py, bez rotacije ključa."""
import argparse
from pathlib import Path
import sys
ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / 'ServiceHub'))
from servishub.releases import verify_release

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--verify-only', action='store_true')
    parser.add_argument('--zip', type=Path, default=ROOT/'ISO/storage/cache/wis-payload.zip')
    parser.add_argument('--meta', type=Path, default=ROOT/'ISO/storage/cache/payload_meta.json')
    args = parser.parse_args()
    if not args.verify_only:
        parser.error('Za objavu koristite Release-WIS.py publish; ovaj alat ne pravi kljuceve niti prepisuje release.')
    try:
        verify_release(args.zip, args.meta, ROOT/'Launcher/trusted_public_key.xml')
    except Exception as e:
        print('[FAIL] '+str(e), file=sys.stderr); return 1
    print('[PASS] RSA-SHA256, ZIP i komponente su validni.'); return 0
if __name__ == '__main__': sys.exit(main())
