#!/usr/bin/env bash
# Provision the arm64 AOSP emulator (emulator-5556) to run TFTF against our local
# Sparx server, with the native patches + frida-server. Host reachable at 10.0.2.2.
set -e
export MSYS_NO_PATHCONV=1
ADB="D:/Android/Sdk/platform-tools/adb.exe"
D="emulator-5556"
HOSTIP="10.0.2.2"
HERE="$(cd "$(dirname "$0")/.." && pwd)"
S(){ "$ADB" -s "$D" shell "$@"; }

echo "[*] root + permissive"
"$ADB" -s "$D" root >/dev/null 2>&1 || true; sleep 2
S 'setenforce 0 2>/dev/null; getenforce' || true

echo "[*] install APK (839MB, slow on TCG)"
"$ADB" -s "$D" install -r -g "$HERE/com.kabam.bigrobot_9.2.0.apk" 2>&1 | tail -2

LIBDIR=$(S 'ls -d /data/app/com.kabam.bigrobot*/lib/arm64 2>/dev/null' | tr -d '\r')
echo "[*] lib dir: $LIBDIR"
echo "[*] push patched libil2cpp.so (cert-pinning + auth-chain patches)"
"$ADB" -s "$D" push "$HERE/extracted/lib/arm64-v8a/libil2cpp.patched.so" /data/local/tmp/libil2cpp.so >/dev/null
S "cp /data/local/tmp/libil2cpp.so $LIBDIR/libil2cpp.so && chmod 755 $LIBDIR/libil2cpp.so; restorecon $LIBDIR/libil2cpp.so 2>/dev/null; echo lib_ok"

echo "[*] hosts redirect -> $HOSTIP (bind-mount)"
HOSTS=/data/local/tmp/hosts
S "printf '127.0.0.1 localhost\n$HOSTIP tform-0901-hzlhiniyfcwf.tf-cdn.net\n$HOSTIP static.tf-cdn.net\n$HOSTIP words-express.tf-cdn.net\n$HOSTIP tf-odr.mcoc-cdn.cn\n$HOSTIP tf-static.mcoc-cdn.cn\n$HOSTIP tftf-odr.mcoc-cdn.cn\n$HOSTIP gametalk.sparx.io\n' > $HOSTS"
S "mountpoint -q /system/etc/hosts && umount /system/etc/hosts 2>/dev/null; mount -o bind $HOSTS /system/etc/hosts && echo hosts_ok"

echo "[*] install CA (bind-mount cacerts)"
CADIR=/data/local/tmp/cacerts
S "rm -rf $CADIR; mkdir -p $CADIR; cp /system/etc/security/cacerts/* $CADIR/ 2>/dev/null"
"$ADB" -s "$D" push "$HERE/server/certs/4987303d.0" "$CADIR/" >/dev/null
S "chmod 644 $CADIR/*; mountpoint -q /system/etc/security/cacerts && umount /system/etc/security/cacerts 2>/dev/null; mount -o bind $CADIR /system/etc/security/cacerts && echo ca_ok"

echo "[*] push frida-server-arm64 (NATIVE arm64 -> Frida works here)"
"$ADB" -s "$D" push "$HERE/tools/frida/frida-server-arm64" /data/local/tmp/frida-server >/dev/null
S "chmod 755 /data/local/tmp/frida-server; echo frida_ok"
echo "[*] setup_arm64 done"
