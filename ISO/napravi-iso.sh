#!/usr/bin/env bash
# ==============================================================================
# GWADT (Gruya Windows Automated Deployment Tool)
# Skripta za generisanje Ventoy Bootable Windows 11 ISO slike
# Podržava Dual-Profile izgradnju:
#   ./napravi-iso.sh                     # Podrazumevano: Lean Network ISO (~8 GB, bez Programi.tar)
#   ./napravi-iso.sh --profile lean      # Lean Network ISO za radionicu i iVentoy PXE
#   ./napravi-iso.sh --profile full      # Full Offline ISO za terenske USB-ove (~13 GB, sa Programi.tar)
#   ./napravi-iso.sh --dry-run           # Brza simulacija bez izmena
# ==============================================================================
set -euo pipefail

# 1. Provera prisustva Python 3 interpreter-a
if ! command -v python3 &>/dev/null; then
    echo "❌ [GRESKA] Python 3 nije instaliran ili se ne nalazi u PATH-u!" >&2
    echo "   Molimo instalirajte Python 3 (npr. 'sudo apt install python3')." >&2
    exit 1
fi

# 2. Određivanje putanja
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
BUILD_SCRIPT="${SCRIPT_DIR}/Skripte/Build-Iso.py"

if [[ ! -f "$BUILD_SCRIPT" ]]; then
    echo "❌ [GRESKA] Skripta za izgradnju ISO slike nije pronađena na putanji:" >&2
    echo "   $BUILD_SCRIPT" >&2
    exit 1
fi

# 3. Pokretanje Python generatora uz prosleđivanje svih CLI argumenata
# Podrazumevano prepusta odabir motora i profila Build-Iso.py skripti
EXTRA_ARGS=()

exec python3 "$BUILD_SCRIPT" "${EXTRA_ARGS[@]}" "$@"

