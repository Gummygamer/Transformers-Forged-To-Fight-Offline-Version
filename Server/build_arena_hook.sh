#!/usr/bin/env bash
# Build the arm64 runtime hook with the optional LAN Arena fight relay enabled.
#
# Build one hook per device, changing --peer for each APK.  The resulting hook is
# picked up by build_phone_apk.lbl, so build the hook immediately before building
# that device's separated-server APK.  The relay must be reachable directly over
# UDP from both devices; adb reverse only carries TCP and is not suitable here.
set -euo pipefail

HERE="$(cd "$(dirname "$0")/.." && pwd)"
NATIVEHOOK="$HERE/tools/nativehook"
OUTPUT="$NATIVEHOOK/libdothook.so"
RELAY_HOST=""
RELAY_PORT="8777"
PEER=""

usage() {
  cat <<'EOF'
usage: Server/build_arena_hook.sh --relay-host HOST --peer NAME [--relay-port PORT] [--output FILE]

Builds the arm64 libdothook.so used by a separated-LAN Arena APK. HOST must be
the LAN/tunnel address at which tools/netrelay/netrelay is listening. Build a
separate hook/APK for each device with a distinct peer NAME.
EOF
}

while (($#)); do
  case "$1" in
    --relay-host) RELAY_HOST="${2:-}"; shift 2 ;;
    --relay-port) RELAY_PORT="${2:-}"; shift 2 ;;
    --peer) PEER="${2:-}"; shift 2 ;;
    --output) OUTPUT="${2:-}"; shift 2 ;;
    -h|--help) usage; exit 0 ;;
    *) echo "Unknown argument: $1" >&2; usage >&2; exit 2 ;;
  esac
done

# These values become C string literals. Restrict them rather than making a shell
# quoting mistake turn a build argument into a compiler directive.
[[ "$RELAY_HOST" =~ ^[A-Za-z0-9.-]+$ ]] || { echo "--relay-host must be an IPv4 address or hostname." >&2; exit 2; }
[[ "$PEER" =~ ^[A-Za-z0-9._-]+$ ]] || { echo "--peer must contain only letters, digits, dot, underscore, or dash." >&2; exit 2; }
[[ "$RELAY_PORT" =~ ^[0-9]+$ ]] && (( RELAY_PORT >= 1 && RELAY_PORT <= 65535 )) || { echo "--relay-port must be between 1 and 65535." >&2; exit 2; }

NDK_ROOT="${ANDROID_NDK_HOME:-$HOME/Android/Sdk/ndk}"
NDK_BIN="$(find "$NDK_ROOT" -path '*/toolchains/llvm/prebuilt/*/bin/aarch64-linux-android28-clang' -type f -print -quit 2>/dev/null || true)"
[[ -n "$NDK_BIN" && -x "$NDK_BIN" ]] || { echo "Android arm64 NDK clang was not found; set ANDROID_NDK_HOME." >&2; exit 1; }

mkdir -p "$(dirname "$OUTPUT")"
"$NDK_BIN" -shared -O2 -fPIC -Wl,-soname,libdothook.so \
  -DTFTF_ENABLE_ARENA=1 \
  "-DTFTF_ARENA_DEFAULT_HOST=\"$RELAY_HOST\"" \
  "-DTFTF_ARENA_DEFAULT_PORT=$RELAY_PORT" \
  "-DTFTF_ARENA_DEFAULT_ROOM=\"arena_versus\"" \
  "-DTFTF_ARENA_DEFAULT_PEER=\"$PEER\"" \
  -o "$OUTPUT" "$NATIVEHOOK/hook.c" "$NATIVEHOOK/inapk_server.c" \
  "$NATIVEHOOK/arena.c" "$NATIVEHOOK/netclient.c" -llog

echo "Built Arena relay hook: $OUTPUT"
echo "Embed it next with build_phone_apk.lbl, then repeat with a different --peer for the other device."
