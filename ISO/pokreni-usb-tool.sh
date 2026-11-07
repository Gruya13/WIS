#!/usr/bin/env bash
# ==============================================================================
# GWADT USB Tool - Pokretac iz Korena Projekta
# ==============================================================================
DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
exec "$DIR/USB Tool/pokreni-usb-tool.sh" "$@"
