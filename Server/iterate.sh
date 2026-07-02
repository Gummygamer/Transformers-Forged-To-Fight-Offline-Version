#!/usr/bin/env bash
# One-shot empirical iteration: relaunch TFTF, tap PLAY NOW, capture the new
# request sequence + final screen. Use after editing server/responses/*.json.
#   bash server/iterate.sh <tag>
set -e
export MSYS_NO_PATHCONV=1
ADB="${ADB:-D:/Android/Sdk/platform-tools/adb.exe}"
D="${D:-127.0.0.1:5555}"
TAG="${1:-run}"
HERE="$(cd "$(dirname "$0")" && pwd)"
REQ="$HERE/logs/requests.log"
PROBE="$(cd "$HERE/.." && pwd)/probe"
mkdir -p "$PROBE"

mark=$(wc -l < "$REQ" 2>/dev/null || echo 0)
"$ADB" -s "$D" shell am force-stop com.kabam.bigrobot
"$ADB" -s "$D" logcat -c
"$ADB" -s "$D" shell am start -n "com.kabam.bigrobot/com.explodingbarrel.Activity" >/dev/null 2>&1
echo "[$TAG] booting (22s)..."
timeout 22 "$ADB" -s "$D" logcat -v time > "$PROBE/iter_${TAG}.log" 2>&1 || true
echo "[$TAG] tap PLAY NOW (960,810)"
"$ADB" -s "$D" shell input tap 960 810
echo "[$TAG] capturing post-tap (18s)..."
timeout 18 "$ADB" -s "$D" logcat -v time >> "$PROBE/iter_${TAG}.log" 2>&1 || true
"$ADB" -s "$D" exec-out screencap -p > "$PROBE/iter_${TAG}.png" 2>/dev/null
echo ""
echo "=== [$TAG] NEW requests this run ==="
tail -n +"$((mark+1))" "$REQ" 2>/dev/null | grep -aE '^\[2.*\] (POST|GET|PUT|DELETE) https' | sed -E 's/\?sig=[^ ]*//; s/\?sver=3&sig=[^ ]*//' | awk '{print "   ", $2, $3}'
echo "=== screen: $PROBE/iter_${TAG}.png ==="
