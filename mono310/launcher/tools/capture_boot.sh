#!/usr/bin/env bash
# Starts the app on the connected phone and saves a screenshot every few seconds, to see the boot sequence (Kabam logo,
# revival splash, title screen). Uses the phone: do not touch it while this runs.
# usage: capture_boot.sh OUT_DIR [SECONDS=90] [INTERVAL=3]      env: ADB_DEVICE
set -uo pipefail
OUT=${1:?usage: capture_boot.sh OUT_DIR [SECONDS] [INTERVAL]}
TOTAL=${2:-90}; STEP=${3:-3}
ADB=(adb ${ADB_DEVICE:+-s "$ADB_DEVICE"})
PKG=unicronslair.bigrobot
mkdir -p "$OUT"
"${ADB[@]}" shell am force-stop $PKG
"${ADB[@]}" shell input keyevent KEYCODE_WAKEUP
"${ADB[@]}" shell wm dismiss-keyguard
"${ADB[@]}" shell monkey -p $PKG -c android.intent.category.LAUNCHER 1 >/dev/null 2>&1
t=0
while [ "$t" -le "$TOTAL" ]; do
  "${ADB[@]}" exec-out screencap -p > "$OUT/$(printf '%03d' "$t").png"
  sleep "$STEP"; t=$((t + STEP))
done
echo "saved $((TOTAL / STEP + 1)) screenshots in $OUT"
