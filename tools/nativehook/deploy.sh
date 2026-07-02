#!/usr/bin/env bash
# Build + deploy the EB.Dot key-logging hook and capture one login's keys.
# WHY THIS WORKS (the breakthrough): LDPlayer runs the arm64 game via libnb
# (ARM->x86 JIT). Frida/Gum and runtime inline-hooks normally crash because libnb
# has already translated the code. BUT:
#   1) libil2cpp.so already has a DT_NEEDED for "libdothook.so" (idx25, string in a
#      cave @ va 0x7D31B4) -> libnb loads our arm64 hook in the ARM context.
#   2) Our hook inline-patches EB.Dot.* prologues in its constructor, which runs
#      BEFORE login calls them -> libnb's LAZY (per-block) translation picks up the
#      patched bytes. Pure byte-overwrite (no Gum) = no crash.
#   3) libnb only re-runs the DT_NEEDED constructor when the .so is FRESHLY copied
#      to the lib dir (mtime/inode change) -> we rm+cp before every launch.
#   4) Logcat drops 24k+ lines; the hook writes keys to a FILE in the app's own
#      writable dir instead.
# Tags in dotkeys.log: S=String O=Object F=Find B=Bool I=Integer G=Single L=Long
#   SL=StringList D=SparxId A=Array K=get_Item(arg1, gated to the heroes window).
set -e
export MSYS_NO_PATHCONV=1
NDK="D:/Android/Sdk/ndk/26.1.10909125/toolchains/llvm/prebuilt/windows-x86_64/bin"
CLANG="$NDK/aarch64-linux-android28-clang.cmd"
ADB="D:/Android/Sdk/platform-tools/adb.exe"; D="127.0.0.1:5555"
HERE="$(cd "$(dirname "$0")" && pwd)"
PKG=com.kabam.bigrobot
LOGF=/data/data/$PKG/files/dotkeys.log

echo "[*] build"
"$CLANG" -shared -O2 -fPIC -Wl,-soname,libdothook.so -o "$HERE/libdothook.so" "$HERE/hook.c" -llog
LIBDIR=$("$ADB" -s $D shell "ls -d /data/app/${PKG}*/lib/arm64" | tr -d '\r')
echo "[*] deploy -> $LIBDIR (fresh cp forces libnb to re-run ctor)"
"$ADB" -s $D shell am force-stop $PKG
"$ADB" -s $D push "$HERE/libdothook.so" /data/local/tmp/libdothook.so >/dev/null
"$ADB" -s $D shell "su 0 sh -c 'rm -f $LIBDIR/libdothook.so; cp /data/local/tmp/libdothook.so $LIBDIR/libdothook.so; chmod 755 $LIBDIR/libdothook.so; rm -f $LOGF'"
echo "[*] launch + login"
"$ADB" -s $D shell am start -n $PKG/com.explodingbarrel.Activity >/dev/null
sleep 32; "$ADB" -s $D shell input tap 960 810; sleep 18
"$ADB" -s $D shell "su 0 cat $LOGF" > "$HERE/../../probe/dotkeys.log" 2>/dev/null
echo "[*] captured $(wc -l < "$HERE/../../probe/dotkeys.log") keys -> probe/dotkeys.log"
