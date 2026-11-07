#!/usr/bin/env bash
# ==============================================================================
# GWADT - Linux WIM Auto-Refresh Launcher
# Pokreće proveru verzije i automatizovano sklapanje WIM-a preko UUP pipeline-a
# ==============================================================================

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

if [ -f "$SCRIPT_DIR/Skripte/Update-WimImage-Linux.py" ]; then
    python3 "$SCRIPT_DIR/Skripte/Update-WimImage-Linux.py" "$@"
elif [ -f "$SCRIPT_DIR/Update-WimImage-Linux.py" ]; then
    python3 "$SCRIPT_DIR/Update-WimImage-Linux.py" "$@"
else
    echo "[GRESKA] Update-WimImage-Linux.py nije pronadjen!"
    exit 1
fi
