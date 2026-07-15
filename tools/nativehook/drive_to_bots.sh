#!/usr/bin/env bash
# Relaunch the game and drive login -> BOTS roster, then dump the hook log.
# Assumes hook already deployed and networking provisioned. Prints log path.
set -e
export PATH="$HOME/Android/Sdk/platform-tools:$PATH"
D="${D:-emulator-5554}"; PKG=com.kabam.bigrobot
SP="${SP:-/tmp/drive}"; mkdir -p "$SP"
LOGF=/data/data/$PKG/files/dotkeys.log
adb -s $D shell am start -n $PKG/com.explodingbarrel.Activity >/dev/null
echo "[*] waiting for title (22s)"; adb -s $D shell 'sleep 22'
adb -s $D shell input tap 827 597    # PLAY NOW
echo "[*] logging in (44s)"; adb -s $D shell 'sleep 44'
adb -s $D shell "su 0 sh -c 'echo ===DRIVE=== > $LOGF'"
adb -s $D shell input tap 434 130    # BOTS
echo "[*] on BOTS (7s)"; adb -s $D shell 'sleep 7'
adb -s $D exec-out screencap -p > "$SP/bots.png"
adb -s $D shell "su 0 cat $LOGF" > "$SP/bots.log" 2>/dev/null
echo "[*] proc: $(adb -s $D shell pidof $PKG)"
echo "[*] screenshot: $SP/bots.png  log: $SP/bots.log"
