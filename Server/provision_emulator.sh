#!/usr/bin/env bash
# Provision a STOCK Google Android emulator (Linux) to boot the offline TFTF build.
#
# Unlike tools/provision_ldplayer.sh (which pushes the patched lib into an
# already-installed app on LDPlayer), this assumes you install the self-contained
# "Transformers 9.2 offline.apk" -- it already bundles the patched libil2cpp.so and
# libdothook.so, so no per-boot lib push is needed. All that has to be re-done after
# each cold boot is: SELinux permissive, the hosts redirect, the CA, and the adb
# reverse port forwards. The Kabam domains are pointed at 127.0.0.1 and the guest's
# privileged :443/:80 are forwarded (via `adb reverse`) to the unprivileged host
# ports that Server/run_local.py listens on, so nothing here needs sudo.
#
# Usage:
#   Server/provision_emulator.sh            # provision + (re)launch the game
#   D=emulator-5554 Server/provision_emulator.sh
#
# Prereqs: emulator booted, `adb root` available (google_apis image, not playstore),
# and Server/run_local.py running on the host (HTTP :8080 / HTTPS :8443).
set -e
ADB="${ADB:-adb}"
D="${D:-emulator-5554}"
PKG="${PKG:-com.kabam.bigrobot}"
ACTIVITY="${ACTIVITY:-com.explodingbarrel.Activity}"
HERE="$(cd "$(dirname "$0")" && pwd)"
CAHASH="$(ls "$HERE"/certs/*.0 2>/dev/null | head -1)"

S(){ "$ADB" -s "$D" shell "$@"; }

echo "[*] root + wait"
"$ADB" -s "$D" root >/dev/null 2>&1 || true
"$ADB" -s "$D" wait-for-device
S 'setenforce 0 2>/dev/null; echo -n "selinux="; getenforce'

echo "[*] forward guest :443/:80 -> host :8443/:8080 (adb reverse)"
"$ADB" -s "$D" reverse --remove-all >/dev/null 2>&1 || true
"$ADB" -s "$D" reverse tcp:443 tcp:8443
"$ADB" -s "$D" reverse tcp:80  tcp:8080
"$ADB" -s "$D" reverse --list

echo "[*] hosts redirect (Kabam domains -> 127.0.0.1) via bind-mount"
HOSTS="/data/local/tmp/hosts"
S "cat > $HOSTS <<'EOF'
127.0.0.1 localhost
::1 ip6-localhost
127.0.0.1 tform-0901-hzlhiniyfcwf.tf-cdn.net
127.0.0.1 static.tf-cdn.net
127.0.0.1 words-express.tf-cdn.net
127.0.0.1 tf-odr.mcoc-cdn.cn
127.0.0.1 tf-static.mcoc-cdn.cn
127.0.0.1 tftf-odr.mcoc-cdn.cn
127.0.0.1 gametalk.sparx.io
EOF"
S "mountpoint -q /system/etc/hosts && umount /system/etc/hosts 2>/dev/null; mount -o bind $HOSTS /system/etc/hosts && echo HOSTS_BOUND"
S 'cat /system/etc/hosts'

echo "[*] install CA into system trust store (bind-mount): $(basename "$CAHASH")"
CADIR="/data/local/tmp/cacerts"
S "rm -rf $CADIR; mkdir -p $CADIR; cp /system/etc/security/cacerts/* $CADIR/ 2>/dev/null; chmod 644 $CADIR/*"
"$ADB" -s "$D" push "$CAHASH" "$CADIR/" >/dev/null && echo "  pushed CA"
S "chmod 644 $CADIR/$(basename "$CAHASH"); chown root:root $CADIR/* 2>/dev/null"
S "mountpoint -q /system/etc/security/cacerts && umount /system/etc/security/cacerts 2>/dev/null; mount -o bind $CADIR /system/etc/security/cacerts && echo CACERTS_BOUND"
S "ls /system/etc/security/cacerts/$(basename "$CAHASH") >/dev/null && echo CA_PRESENT"

echo "[*] launch $PKG/$ACTIVITY"
S am force-stop "$PKG"
S am start -n "$PKG/$ACTIVITY" >/dev/null
echo "[+] provisioned. Wait ~45s, then tap CONTINUE (language) and PLAY NOW (title)."
echo "    Make sure Server/run_local.py is running on the host."
