#!/usr/bin/env bash
# Builds the three stand-in native libraries for armeabi-v7a into $1 (a Plugins/Android/libs/armeabi-v7a folder).
set -euo pipefail
OUT=${1:?usage: build.sh <output dir>}
NDK=${ANDROID_NDK_HOME:-$(ls -d "${ANDROID_HOME:-$HOME/Android/Sdk}"/ndk/* | sort | tail -1)}
CC=$NDK/toolchains/llvm/prebuilt/linux-x86_64/bin/armv7a-linux-androideabi21-clang
HERE=$(cd "$(dirname "$0")" && pwd)
mkdir -p "$OUT"
for pair in "android-signal:LIB_ANDROID_SIGNAL" "apkprotect:LIB_APKPROTECT" "enetlib:LIB_ENETLIB"; do
  "$CC" -shared -fPIC -O2 -D"${pair#*:}" -o "$OUT/lib${pair%%:*}.so" "$HERE/stubs.c"
done
