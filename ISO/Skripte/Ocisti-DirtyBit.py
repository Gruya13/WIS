#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
Ocisti-DirtyBit.py - GWADT alat za popravku i uklanjanje 'dirty bit' zastavice sa USB uređaja.
Automatski detektuje povezane USB diskove i izvršava popravku fajl sistema.
Na Linuxu koristi UDisks2 DBus servis (bez potrebe za sudo lozinkom).
"""

import os
import sys
from pathlib import Path

# Dodavanje USB Tool u sys.path
SCRIPT_DIR = Path(__file__).resolve().parent
PROJECT_ROOT = SCRIPT_DIR.parent
USB_TOOL_DIR = PROJECT_ROOT / "USB Tool"

if USB_TOOL_DIR.exists() and str(USB_TOOL_DIR) not in sys.path:
    sys.path.insert(0, str(USB_TOOL_DIR))

from core.usb_detector import get_all_usb_drives, clear_dirty_bit

CYAN = "\033[1;36m"
GREEN = "\033[1;32m"
YELLOW = "\033[1;33m"
RED = "\033[1;31m"
BOLD = "\033[1m"
RESET = "\033[0m"

def main():
    print(f"{CYAN}================================================================================{RESET}")
    print(f"{YELLOW}          GWADT - ALAT ZA ČIŠĆENJE DIRTY BITA SA USB FLEŠ UREĐAJA               {RESET}")
    print(f"{CYAN}================================================================================{RESET}\n")

    drives = get_all_usb_drives(include_project_repo=False)
    if not drives:
        print(f"{RED}[UPOZORENJE]{RESET} Nije detektovan nijedan USB prenosni uređaj!")
        print("Molimo priključite USB fleš disk u računar i pokrenite ponovo.")
        return 1

    print(f"Pronađeno je {len(drives)} particija na prenosnim uređajima:\n")
    for d in drives:
        status = f"{GREEN}Montirano ({d.get('mountpoint')}){RESET}" if d.get('is_mounted') else f"{YELLOW}Nije montirano{RESET}"
        print(f"  • {BOLD}{d.get('device')}{RESET} [{d.get('fstype', 'N/A').upper()}] - {d.get('label', 'Bez Naziva')} ({d.get('size_human')}) -> {status}")

    print(f"\n{CYAN}[KORAK]{RESET} Pokrećem proceduru popravke i uklanjanja dirty bita...")

    uspeh_ukupno = True
    for d in drives:
        dev = d.get('device')
        print(f"\nPopravljam: {BOLD}{dev}{RESET} ({d.get('label')})...")
        ok, msg = clear_dirty_bit(dev)
        if ok:
            print(f"  {GREEN}✔ [USPEH]{RESET} {msg}")
        else:
            print(f"  {RED}✖ [GREŠKA]{RESET} {msg}")
            uspeh_ukupno = False

    print(f"\n{CYAN}================================================================================{RESET}")
    if uspeh_ukupno:
        print(f"{GREEN}  [ZAVRŠENO] Svi detektovani USB uređaji su uspešno očišćeni i popravljeni!      {RESET}")
    else:
        print(f"{YELLOW}  [ZAVRŠENO] Neke particije nisu mogle biti popravljene. Proverite detalje iznad. {RESET}")
    print(f"{CYAN}================================================================================{RESET}\n")
    return 0 if uspeh_ukupno else 1

if __name__ == "__main__":
    sys.exit(main())
