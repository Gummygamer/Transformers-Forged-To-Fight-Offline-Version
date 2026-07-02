#!/usr/bin/env bash
# After an emulator restart (e.g. graphics-mode change): reconnect ADB, redeploy the
# hook (fresh cp so libnb re-runs the ctor -> force-connect hooks install), relaunch the
# game, log it in, then start a CONTINUOUS filtered logcat capture to a file so we can
# catch a crash while the user navigates manually.
set -e
export MSYS_NO_PATHCONV=1
ADB="D:/Android/Sdk/platform-tools/adb.exe"; D="127.0.0.1:5555"
HERE="$(cd "$(dirname "$0")" && pwd)"
PKG=com.kabam.bigrobot
LOGF=/data/data/$PKG/files/dotkeys.log
CAP="$HERE/../../probe/crashcap.log"

echo "[*] reconnect adb"
"$ADB" connect $D >/dev/null 2>&1 || true
for i in $(seq 1 60); do
  st=$("$ADB" -s $D get-state 2>/dev/null | tr -d '\r')
  [ "$st" = "device" ] && break
  "$ADB" connect $D >/dev/null 2>&1 || true
  sleep 2
done
echo "[*] adb state: $("$ADB" -s $D get-state 2>/dev/null)"

LIBDIR=$("$ADB" -s $D shell "ls -d /data/app/${PKG}*/lib/arm64" 2>/dev/null | tr -d '\r')
echo "[*] re-push hook (in case /data/local/tmp was cleared on reboot)"
"$ADB" -s $D push "$HERE/libdothook.so" /data/local/tmp/libdothook.so >/dev/null
echo "[*] libdir=$LIBDIR ; fresh cp hook"
"$ADB" -s $D shell am force-stop $PKG
"$ADB" -s $D shell "su 0 sh -c 'rm -f $LIBDIR/libdothook.so; cp /data/local/tmp/libdothook.so $LIBDIR/libdothook.so; chmod 755 $LIBDIR/libdothook.so; rm -f $LOGF'"
"$ADB" -s $D logcat -c 2>/dev/null || true
echo "[*] launch + login"
"$ADB" -s $D shell am start -n $PKG/com.explodingbarrel.Activity >/dev/null
sleep 42
"$ADB" -s $D shell input tap 960 810   # tap-to-continue on title
sleep 14
echo "[*] login done; game should be at home. starting continuous logcat capture -> $CAP"
: > "$CAP"
echo "READY: navigate the game now; crash traces will be captured."
