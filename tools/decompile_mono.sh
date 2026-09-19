#!/usr/bin/env bash
# Compatibility entry point for the complete managed-client exporter.
set -euo pipefail

if [[ $# != 1 ]]; then
    echo "Usage: ILSPYCMD=/path/to/ilspycmd bash tools/decompile_mono.sh old-game.apk" >&2
    exit 2
fi
repo=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)
exec python3 "$repo/tools/decompilation.py" export "$(realpath -- "$1")" \
    --ilspy "${ILSPYCMD:-ilspycmd}"
