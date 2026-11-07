#!/usr/bin/env bash
# ==============================================================================
# GWADT - Čišćenje 'dirty bit' zastavice i popravka USB fajl sistema na Linuxu
# ==============================================================================

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

if [ -f "$SCRIPT_DIR/Skripte/Ocisti-DirtyBit.py" ]; then
    python3 "$SCRIPT_DIR/Skripte/Ocisti-DirtyBit.py" "$@"
else
    echo "[GREŠKA] Skripte/Ocisti-DirtyBit.py nije pronađen!"
    exit 1
fi
