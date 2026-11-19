#!/usr/bin/env bash
# ==============================================================================
# WIS Service Centralni Servisni Hub (ServisHub) Pokretač
# ==============================================================================
DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PYTHON_BIN="python3"

if ! command -v "$PYTHON_BIN" &> /dev/null; then
    echo "[!] Greška: python3 nije instaliran na ovom sistemu!"
    exit 1
fi

echo "=================================================================="
echo " ⚡ Pokrećem WIS Service Centralni Servisni Hub (ServisHub v3.1)..."
echo "=================================================================="

exec "$PYTHON_BIN" "$DIR/hub_server.py" "$@"
