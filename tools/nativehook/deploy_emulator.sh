#!/usr/bin/env bash
# Fast-iteration deploy for the Linux stock emulator (see offline-apk-emulator-runbook).
# Rebuilds hook.c, pushes a FRESH-inode libdothook.so into the installed app's ext4
# lib dir (ndk-translation re-runs the DT_NEEDED ctor only on inode change), force-stops.
# REQUIRES the app installed with `adb install --no-incremental` (default incremental-fs
# lib dir is read-only even to root -> cp EPERM with no AVC).
set -e
export PATH="$HOME/Android/Sdk/platform-tools:$PATH"
D="${D:-emulator-5554}"; PKG=com.kabam.bigrobot
HERE="$(cd "$(dirname "$0")" && pwd)"
CC="$HOME/Android/Sdk/ndk/26.3.11579264/toolchains/llvm/prebuilt/linux-x86_64/bin/aarch64-linux-android28-clang"
LOGF=/data/data/$PKG/files/dotkeys.log
echo "[*] build"
"$CC" -shared -O2 -fPIC -Wl,-soname,libdothook.so -o "$HERE/libdothook.so" \
  "$HERE/hook.c" "$HERE/inapk_server.c" -llog
LIBDIR=$(adb -s "$D" shell "find /data/app -type d -name arm64 2>/dev/null | grep bigrobot" | tr -d '\r')
echo "[*] deploy -> $LIBDIR"
adb -s "$D" shell am force-stop $PKG
adb -s "$D" push "$HERE/libdothook.so" /data/local/tmp/libdothook.so >/dev/null
adb -s "$D" shell "su 0 sh -c 'rm -f \"$LIBDIR/libdothook.so\"; cp /data/local/tmp/libdothook.so \"$LIBDIR/libdothook.so\"; chown system:system \"$LIBDIR/libdothook.so\"; chmod 755 \"$LIBDIR/libdothook.so\"; restorecon \"$LIBDIR/libdothook.so\"; rm -f $LOGF'"
adb -s "$D" shell "ls -la '$LIBDIR/libdothook.so'"
echo "[+] deployed. relaunch: adb -s $D shell am start -n $PKG/com.explodingbarrel.Activity"
