#!/usr/bin/env bash
# From-scratch test of the launcher on a connected phone: wipe the app's data, install the APK, drive the first-run
# screen with adb (it picks the two game APKs from the Downloads folder), wait for the game to boot, and write a report.
#
# usage: phone_firstrun_test.sh LAUNCHER.apk [OUT_DIR]
# env:   ADB_DEVICE   serial (optional)         WAIT_SECONDS   boot wait after the install button (default 100)
#        SWAP=1       wrong-file test: put the APKs in the wrong slots and only take the launcher screenshot
#        APK310_NAME  file name of the 3.1.0 APK in Downloads (default tftf-3.1.0.apk)
#        APK92_NAME   file name of the 9.2.0 APK in Downloads (default tftf-9.2.0.apk)
# The two game APKs must already be in /sdcard/Download (adb push them once). AUTOPICK=1 (default) uses the launcher's
# test hook; AUTOPICK=0 drives the system file picker with uiautomator instead (fragile, depends on the picker's layout). It uses the phone: do not touch it
# while this runs. Output: OUT_DIR/report.txt, first_run.png (launcher screen), result.png (after the wait).
set -uo pipefail          # no -e: a grep without matches must not abort the report
APK=${1:?usage: phone_firstrun_test.sh LAUNCHER.apk [OUT_DIR]}
OUT=${2:-./firstrun-test}
ADB=(adb ${ADB_DEVICE:+-s "$ADB_DEVICE"})
PKG=unicronslair.bigrobot
mkdir -p "$OUT"

"${ADB[@]}" shell am force-stop $PKG
"${ADB[@]}" shell rm -rf /sdcard/Android/data/$PKG/files
"${ADB[@]}" install --no-incremental -r "$APK" | tail -1
FILES=/sdcard/Android/data/$PKG/files
if [ "${AUTOPICK:-1}" = 1 ]; then
  # Pre-select the APKs through the launcher's test hook (tftf/autopick.txt) instead of driving the system file picker.
  "${ADB[@]}" shell mkdir -p $FILES/tftf
  "${ADB[@]}" shell cp "/sdcard/Download/${APK310_NAME:-tftf-3.1.0.apk}" "$FILES/a310.apk"
  "${ADB[@]}" shell cp "/sdcard/Download/${APK92_NAME:-tftf-9.2.0.apk}" "$FILES/a92.apk"
  FIRST=a310.apk; SECOND=a92.apk
  if [ "${SWAP:-0}" = 1 ]; then FIRST=a92.apk; SECOND=a310.apk; fi      # wrong-file test: the two APKs in the wrong slots
  printf '%s\n%s\n' "/storage/emulated/0/Android/data/$PKG/files/$FIRST" "/storage/emulated/0/Android/data/$PKG/files/$SECOND" > "${TMPDIR:-/tmp}/autopick.txt"
  "${ADB[@]}" push "${TMPDIR:-/tmp}/autopick.txt" $FILES/tftf/autopick.txt >/dev/null
fi
"${ADB[@]}" logcat -c
"${ADB[@]}" shell input keyevent KEYCODE_WAKEUP
"${ADB[@]}" shell wm dismiss-keyguard
"${ADB[@]}" shell monkey -p $PKG -c android.intent.category.LAUNCHER 1 >/dev/null 2>&1
sleep 12
"${ADB[@]}" exec-out screencap -p > "$OUT/first_run.png"
if [ "${SWAP:-0}" = 1 ]; then
  echo "swap test: look at $OUT/first_run.png (expected: a message that the file is the other APK, both buttons still unselected, Install disabled)"
  exit 0
fi

if [ "${AUTOPICK:-1}" = 1 ]; then
  "${ADB[@]}" shell input tap 1200 612        # "Install and play"
else
python3 - <<'PY'
import os, re, subprocess, sys, time
ADB = ["adb"] + (["-s", os.environ["ADB_DEVICE"]] if os.environ.get("ADB_DEVICE") else [])
def sh(*a): return subprocess.run(ADB + list(a), capture_output=True, text=True).stdout
def find(name):
    sh("shell", "uiautomator", "dump", "/sdcard/ui.xml"); x = sh("shell", "cat", "/sdcard/ui.xml")
    return re.search(r'text="%s"[^>]*bounds="\[(\d+),(\d+)\]\[(\d+),(\d+)\]"' % re.escape(name), x)
def pick(y, name):
    sh("shell", "input", "tap", "1200", str(y)); time.sleep(3)
    m = None
    for _ in range(4):
        m = find(name)
        if m: break
        sh("shell", "input", "swipe", "1000", "800", "1000", "300", "300"); time.sleep(1)
    print(name, "picked" if m else "NOT FOUND")
    if m:
        a, b, c, d = map(int, m.groups()); sh("shell", "input", "tap", str((a + c) // 2), str((b + d) // 2)); time.sleep(3)
# The tap targets assume a 2400x1080 landscape screen (button rows at y=315, 446, 612); adjust for other screens.
pick(315, os.environ.get("APK310_NAME", "tftf-3.1.0.apk"))
pick(446, os.environ.get("APK92_NAME", "tftf-9.2.0.apk"))
sh("shell", "input", "tap", "1200", "612")
PY
fi

sleep "${WAIT_SECONDS:-100}"
{
  echo "== errors"
  "${ADB[@]}" logcat -d -s Unity | grep -E " E Unity|Exception" | cut -c30-200 | sort | uniq -c | sort -rn | head -10 || true
  echo "== native crash"
  "${ADB[@]}" logcat -d | grep -E "CRASH.*signal" | head -2
  echo "== launcher"
  "${ADB[@]}" logcat -d -s Unity | grep -E "install:|TFTF\]" | cut -c30-150
  echo "== install.log"
  "${ADB[@]}" shell cat /sdcard/Android/data/$PKG/files/tftf/install.log 2>/dev/null || true
} > "$OUT/report.txt"
"${ADB[@]}" exec-out screencap -p > "$OUT/result.png"
echo "report: $OUT/report.txt  screenshots: $OUT/first_run.png $OUT/result.png"
