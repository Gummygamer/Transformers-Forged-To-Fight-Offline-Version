#!/usr/bin/env bash
# The proof gate: regenerate Data.bend from the server's files, then check every law
# twice (bend2, then the Lean-proven BendTT kernel). Prints ALL PROOFS CHECK on success.
set -euo pipefail
cd "$(dirname "$0")"
BEND="${BEND:-$(command -v bend || echo "$HOME/.bend/bin/bend")}"
python3 gen_data.py
git diff --quiet -- Data.bend 2>/dev/null || echo "note: Data.bend changed; the server data moved, commit the regenerated file"
"$BEND" PROOF.bend --verdict
