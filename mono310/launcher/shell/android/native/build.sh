#!/usr/bin/env bash
# Builds the three stand-in native libraries for the requested Android ABI into $1.
set -euo pipefail
OUT=${1:?usage: build.sh <output dir> [armeabi-v7a|arm64-v8a]}
ABI=${2:-armeabi-v7a}
NDK=${ANDROID_NDK_HOME:-$(ls -d "${ANDROID_HOME:-$HOME/Android/Sdk}"/ndk/* | sort | tail -1)}
case "$ABI" in
  armeabi-v7a) CC=$NDK/toolchains/llvm/prebuilt/linux-x86_64/bin/armv7a-linux-androideabi21-clang ;;
  arm64-v8a) CC=$NDK/toolchains/llvm/prebuilt/linux-x86_64/bin/aarch64-linux-android21-clang ;;
  *) echo "error: unsupported ABI: $ABI" >&2; exit 2 ;;
esac
HERE=$(cd "$(dirname "$0")" && pwd)
mkdir -p "$OUT"
for pair in "android-signal:LIB_ANDROID_SIGNAL" "apkprotect:LIB_APKPROTECT" "enetlib:LIB_ENETLIB"; do
  "$CC" -shared -fPIC -O2 -D"${pair#*:}" -o "$OUT/lib${pair%%:*}.so" "$HERE/stubs.c"
done
