#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" &>/dev/null && pwd)"
PROJECT_ROOT="$(cd -- "$SCRIPT_DIR/.." &>/dev/null && pwd)"
SOURCE="$SCRIPT_DIR/parallel_zip_writer.c"
BINARY="$PROJECT_ROOT/build/parallel_zip_writer"

mkdir -p "$PROJECT_ROOT/build"
if [[ ! -x "$BINARY" || "$SOURCE" -nt "$BINARY" ]]; then
  cc -std=c11 -O3 -Wall -Wextra -Werror -pthread -o "$BINARY" "$SOURCE"
fi

exec "$BINARY" "$@"
