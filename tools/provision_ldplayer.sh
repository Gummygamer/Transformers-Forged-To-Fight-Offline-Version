#!/usr/bin/env bash
# Re-provision the LDPlayer instance to the WORKING (PC-dependent) state -> boots to home.
# Run this after every emulator restart (the hosts/CA redirects are bind-mounts that don't
# survive a reboot). The PC fake server (server/fakeserver.py) must be running on :443/:80,
# and PC_IP must be this PC's LAN address that the emulator can reach.
#
# Prereqs (one-time): build the patched lib + hook:
#   python patches/patch_il2cpp.py extracted/lib/arm64-v8a/libil2cpp.so --apply
#   (cd tools/nativehook && aarch64-linux-android28-clang.cmd -shared -O2 -fPIC \
#        -Wl,-soname,libdothook.so -o libdothook.so hook.c -llog)
set -e
export MSYS_NO_PATHCONV=1
ADB="D:/Android/Sdk/platform-tools/adb.exe"
D="127.0.0.1:5555"
PC_IP="${1:-192.168.0.181}"          # override: ./provision_ldplayer.sh 192.168.x.x
HERE="$(cd "$(dirname "$0")/.." && pwd)"
PKG=com.kabam.bigrobot
S(){ "$ADB" -s "$D" shell "$@"; }

"$ADB" connect "$D" >/dev/null 2>&1 || true
LIBDIR=$(S "su 0 sh -c 'ls -d /data/app/${PKG}*/lib/arm64'" | tr -d '\r')
echo "[*] lib dir: $LIBDIR ; PC_IP=$PC_IP"
S "su 0 setenforce 0" || true
S am force-stop $PKG

echo "[*] deploy patched libil2cpp + hook (fresh copy so the translator re-runs the hook ctor)"
"$ADB" -s "$D" push "$HERE/extracted/lib/arm64-v8a/libil2cpp.patched.so" /data/local/tmp/il2.so >/dev/null
"$ADB" -s "$D" push "$HERE/tools/nativehook/libdothook.so" /data/local/tmp/dh.so >/dev/null
S "su 0 sh -c 'cp /data/local/tmp/il2.so $LIBDIR/libil2cpp.so; chown system:system $LIBDIR/libil2cpp.so; chmod 755 $LIBDIR/libil2cpp.so; restorecon $LIBDIR/libil2cpp.so; rm -f $LIBDIR/libdothook.so; cp /data/local/tmp/dh.so $LIBDIR/libdothook.so; chmod 755 $LIBDIR/libdothook.so; restorecon $LIBDIR/libdothook.so'"

echo "[*] hosts redirect (Kabam domains -> $PC_IP) via bind-mount"
S "su 0 sh -c 'printf \"127.0.0.1 localhost\n$PC_IP tform-0901-hzlhiniyfcwf.tf-cdn.net\n$PC_IP static.tf-cdn.net\n$PC_IP words-express.tf-cdn.net\n$PC_IP tf-odr.mcoc-cdn.cn\n$PC_IP tf-static.mcoc-cdn.cn\n$PC_IP tftf-odr.mcoc-cdn.cn\n$PC_IP gametalk.sparx.io\n\" > /data/local/tmp/hosts; mountpoint -q /system/etc/hosts && umount /system/etc/hosts 2>/dev/null; mount -o bind /data/local/tmp/hosts /system/etc/hosts'"

echo "[*] CA bind-mount (trust the fake server cert)"
"$ADB" -s "$D" push "$HERE/server/certs/4987303d.0" /data/local/tmp/ca.0 >/dev/null
S "su 0 sh -c 'rm -rf /data/local/tmp/cac; mkdir -p /data/local/tmp/cac; cp /system/etc/security/cacerts/* /data/local/tmp/cac/ 2>/dev/null; cp /data/local/tmp/ca.0 /data/local/tmp/cac/4987303d.0; chmod 644 /data/local/tmp/cac/*; mountpoint -q /system/etc/security/cacerts && umount /system/etc/security/cacerts 2>/dev/null; mount -o bind /data/local/tmp/cac /system/etc/security/cacerts'"

echo "[*] launch"
S am start -n $PKG/com.explodingbarrel.Activity >/dev/null
echo "[+] provisioned. Wait ~45s; tap the title screen to log in. Make sure fakeserver.py is running."
