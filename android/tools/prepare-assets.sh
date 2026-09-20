#!/usr/bin/env bash
set -euo pipefail

ANDROID_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
ROOT_DIR="$(cd "$ANDROID_DIR/.." && pwd)"
PORT="${1:-8080}"
FORCE_ASSETS="${TFTF_FORCE_ASSETS:-0}"
ASSET_DIR="$ANDROID_DIR/app/src/main/assets"
PVPHOST_ASSET_DIR="$ANDROID_DIR/pvphost/src/main/assets"

find_toolchain_binary() {
  local name="$1"
  local candidate

  if command -v "$name" >/dev/null 2>&1; then
    command -v "$name"
    return 0
  fi

  # Also inspect the conventional SDK location.  Release workers often have
  # a local.properties file (or a standard SDK install) without exporting
  # ANDROID_HOME, and silently reusing an old hook in that case is unsafe.
  for root in "${ANDROID_NDK_HOME:-}" "${ANDROID_NDK_ROOT:-}" "${ANDROID_HOME:-}" "${HOME:-}/Android/Sdk"; do
    [ -n "$root" ] || continue
    if [[ "$root" == */ndk/* ]]; then
      candidate="$root/toolchains/llvm/prebuilt/linux-x86_64/bin/$name"
      [ -x "$candidate" ] && { printf '%s\n' "$candidate"; return 0; }
    elif [ -d "$root/ndk" ]; then
      while IFS= read -r candidate; do
        [ -x "$candidate" ] && { printf '%s\n' "$candidate"; return 0; }
      done < <(find "$root/ndk" -type f -path "*/toolchains/llvm/prebuilt/*/bin/$name" -print 2>/dev/null | sort -r)
    fi
  done
  return 1
}

if ! command -v legible >/dev/null 2>&1; then
  echo "error: legible is required to generate the offline payload" >&2
  exit 1
fi

mkdir -p "$ASSET_DIR"

# Rebuild the embedded hooks whenever their sources changed. Copying an older
# ignored .so here is otherwise an easy way to ship a patcher whose in-app
# server predates the payload it embeds.
ARM64_CLANG="$(find_toolchain_binary aarch64-linux-android28-clang || true)"
ARMV7_CLANG="$(find_toolchain_binary armv7a-linux-androideabi21-clang || true)"
if [ "$FORCE_ASSETS" = "1" ] ||
   [ "$ROOT_DIR/tools/nativehook/hook.c" -nt "$ROOT_DIR/tools/nativehook/libdothook.so" ] ||
   [ "$ROOT_DIR/tools/nativehook/inapk_server.c" -nt "$ROOT_DIR/tools/nativehook/libdothook.so" ] ||
   [ ! -s "$ROOT_DIR/tools/nativehook/libdothook.so" ]; then
  [ -n "$ARM64_CLANG" ] || { echo "error: Android NDK clang is required to rebuild the arm64 hook" >&2; exit 1; }
  "$ARM64_CLANG" -shared -O2 -fPIC -Wl,-z,max-page-size=16384 \
    -Wl,-soname,libdothook.so -o "$ROOT_DIR/tools/nativehook/libdothook.so" \
    "$ROOT_DIR/tools/nativehook/hook.c" "$ROOT_DIR/tools/nativehook/inapk_server.c" -llog
fi
if [ "$FORCE_ASSETS" = "1" ] ||
   [ "$ROOT_DIR/tools/nativehook/hook_arm32.c" -nt "$ROOT_DIR/tools/nativehook/libdothook-armeabi-v7a.so" ] ||
   [ "$ROOT_DIR/tools/nativehook/inapk_server.c" -nt "$ROOT_DIR/tools/nativehook/libdothook-armeabi-v7a.so" ] ||
   [ ! -s "$ROOT_DIR/tools/nativehook/libdothook-armeabi-v7a.so" ]; then
  [ -n "$ARMV7_CLANG" ] || { echo "error: Android NDK clang is required to rebuild the armv7 hook" >&2; exit 1; }
  "$ARMV7_CLANG" -shared -O2 -fPIC -Wall -Wextra -Wl,-z,max-page-size=16384 \
    -Wl,-soname,libdothook.so -o "$ROOT_DIR/tools/nativehook/libdothook-armeabi-v7a.so" \
    "$ROOT_DIR/tools/nativehook/hook_arm32.c" "$ROOT_DIR/tools/nativehook/inapk_server.c" -llog
fi
cp "$ROOT_DIR/tools/nativehook/libdothook.so" "$ASSET_DIR/libdothook-arm64.bin"
cp "$ROOT_DIR/tools/nativehook/libdothook-armeabi-v7a.so" "$ASSET_DIR/libdothook-armv7.bin"
legible run "$ROOT_DIR/Server/export_payload.lbl" --out "$ASSET_DIR/tftf_payload.bin" --listen-port "$PORT"

# The PvP host app serves the same blob (export_payload ignores the port when
# building bodies), so copy it rather than generating a second one.
if [ -d "$ANDROID_DIR/pvphost" ]; then
  mkdir -p "$PVPHOST_ASSET_DIR"
  cp "$ASSET_DIR/tftf_payload.bin" "$PVPHOST_ASSET_DIR/tftf_payload.bin"
fi

echo "prepared native hook assets and a port-$PORT offline payload in app/src/main/assets/ (payload also copied to pvphost/src/main/assets/)"
