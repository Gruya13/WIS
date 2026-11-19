#!/usr/bin/env bash
# ==============================================================================
# GWADT USB Tool - Linux Pokretač (Podrazumevani lokalni bind: 127.0.0.1)
# Za eksplicitni LAN pristup proslediti: --allow-lan
# ==============================================================================
set -euo pipefail

DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PYTHON_BIN="python3"

if ! command -v "$PYTHON_BIN" &> /dev/null; then
    echo "[!] Greška: python3 nije instaliran na ovom sistemu!"
    exit 1
fi

echo "=================================================================="
echo " ⚡ Pokrećem GWADT USB Tool (Centar za Održavanje USB-a)..."
echo " 🔒 Bezbednosni profil: Lokalni pristup (127.0.0.1)"
echo "=================================================================="

exec "$PYTHON_BIN" "$DIR/app.py" --host 127.0.0.1 "$@"
