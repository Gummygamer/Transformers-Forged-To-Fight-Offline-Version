#!/usr/bin/env bash
set -euo pipefail

ANDROID_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
ROOT_DIR="$(cd "$ANDROID_DIR/.." && pwd)"
PORT="${1:-8080}"
ASSET_DIR="$ANDROID_DIR/app/src/main/assets"

if ! command -v legible >/dev/null 2>&1; then
  echo "error: legible is required to generate the offline payload" >&2
  exit 1
fi

mkdir -p "$ASSET_DIR"
cp "$ROOT_DIR/tools/nativehook/libdothook.so" "$ASSET_DIR/libdothook-arm64.bin"
cp "$ROOT_DIR/tools/nativehook/libdothook-armeabi-v7a.so" "$ASSET_DIR/libdothook-armv7.bin"
legible run "$ROOT_DIR/Server/export_payload.lbl" --out "$ASSET_DIR/tftf_payload.bin" --listen-port "$PORT"

echo "prepared native hook assets and a port-$PORT offline payload in app/src/main/assets/"
