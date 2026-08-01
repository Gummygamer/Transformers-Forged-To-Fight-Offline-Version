#!/usr/bin/env bash
# Relaunch and drive the cold-run language/title flow to Arrival's combat bot selector.
# It chooses the second squad portrait (Optimus Prime) before entering combat.  The route
# depends on the current offline story map: intermediate node -> Sharkticon node -> FIGHT.
set -e
export PATH="$HOME/Android/Sdk/platform-tools:$PATH"
D="${D:-emulator-5554}"
PKG=com.kabam.bigrobot

tap() { adb -s "$D" shell input tap "$1" "$2"; }
wait() { adb -s "$D" shell "sleep $1"; }

adb -s "$D" shell am start -n "$PKG/com.explodingbarrel.Activity" >/dev/null
echo '[*] language'; wait 22; tap 827 552       # English CONTINUE (cold-run only)
echo '[*] title'; wait 8; tap 827 597           # PLAY NOW
echo '[*] home'; wait 45; tap 826 130           # FIGHT!
echo '[*] modes'; wait 7; tap 530 260           # STORY
echo '[*] story'; wait 7; tap 1025 240          # Arrival
echo '[*] squad'; wait 7; tap 1395 650          # START
echo '[*] warning'; wait 7; tap 930 365         # CONTINUE with partial squad
echo '[*] board'; wait 8; tap 840 395           # intermediate node
wait 6; tap 770 300                             # Sharkticon node -> bot selector
echo '[*] select'; wait 10
tap 230 285                                     # Optimus Prime (msa=3 test bot)
wait 3; tap 1280 660                            # combat FIGHT!
echo '[+] combat loading'
