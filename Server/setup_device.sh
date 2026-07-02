#!/usr/bin/env bash
# Redirect TFTF's dead Sparx/CDN hosts to our local server via bind-mounts
# (works on read-only /system because we are root). Also installs our CA into
# the system trust store via a bind-mounted cacerts dir.
set -e
export MSYS_NO_PATHCONV=1
ADB="${ADB:-D:/Android/Sdk/platform-tools/adb.exe}"
D="${D:-127.0.0.1:5555}"
HOSTIP="${HOSTIP:-192.168.0.181}"
HERE="$(cd "$(dirname "$0")" && pwd)"
CAHASH="$(ls "$HERE"/certs/*.0 2>/dev/null | head -1)"

S(){ "$ADB" -s "$D" shell "$@"; }

echo "[*] root + permissive"
"$ADB" -s "$D" root >/dev/null 2>&1 || true
"$ADB" connect "$D" >/dev/null 2>&1 || true
S 'setenforce 0 2>/dev/null; getenforce'

echo "[*] hosts redirect -> $HOSTIP"
HOSTS="/data/local/tmp/hosts"
S "cat > $HOSTS <<'EOF'
127.0.0.1 localhost
::1 ip6-localhost
$HOSTIP tform-0901-hzlhiniyfcwf.tf-cdn.net
$HOSTIP static.tf-cdn.net
$HOSTIP words-express.tf-cdn.net
$HOSTIP tf-odr.mcoc-cdn.cn
$HOSTIP tf-static.mcoc-cdn.cn
$HOSTIP tftf-odr.mcoc-cdn.cn
$HOSTIP gametalk.sparx.io
EOF"
S "mountpoint -q /system/etc/hosts && umount /system/etc/hosts 2>/dev/null; mount -o bind $HOSTS /system/etc/hosts && echo HOSTS_BOUND"
S 'cat /system/etc/hosts'

echo "[*] install CA: $(basename "$CAHASH")"
CADIR="/data/local/tmp/cacerts"
S "rm -rf $CADIR; mkdir -p $CADIR; cp /system/etc/security/cacerts/* $CADIR/ 2>/dev/null; chmod 644 $CADIR/*"
"$ADB" -s "$D" push "$CAHASH" "$CADIR/" >/dev/null && echo "  pushed CA"
S "chmod 644 $CADIR/$(basename "$CAHASH"); chown root:root $CADIR/* 2>/dev/null"
S "mountpoint -q /system/etc/security/cacerts && umount /system/etc/security/cacerts 2>/dev/null; mount -o bind $CADIR /system/etc/security/cacerts && echo CACERTS_BOUND"
S "ls /system/etc/security/cacerts/$(basename "$CAHASH") && echo CA_PRESENT"
echo "[*] done"
