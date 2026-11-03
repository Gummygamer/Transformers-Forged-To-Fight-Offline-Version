#!/usr/bin/env bash
# Drive the A52 from the title screen (or home) to the first raid start, for the device check in RAIDS_P2P_PLAN.md stage 1.
#
#   raid_nav.sh [--no-play] [--shot DIR]
#
#   --no-play   the game is already in front (home or the fight menu): skip the title screen
#   --shot DIR  screenshot directory (default /tmp)
#
# Each step taps, then waits for the game to log the screen it should open: the shell's TftfBot logs every window as
# "[TFTFBOT] screen=<name>" (logcat), so a step passes only when its expected screen appears. A stray base edit popup
# (BaseEditNodePopup) is closed with BACK, because a tap that misses the menu opens it. Exit status: 0 only when the raid
# flow started (PVPRAID "Enter RaidFlow") with no PVPRAID error, 1 otherwise.
set -u

PKG=unicronslair.bigrobot
PLAY=(1200 870)          # JOGUE AGORA! on the title screen
FIGHT_TAB=(1200 178)     # LUTAR! on home
ASSALTOS_CARD=(1220 526) # the CONTINUAR button on the ASSALTOS card in the fight-mode menu (y=400 hit the info tooltip)
FIRST_NPC=(726 956)      # ASSALTO button of the first raid opponent
CONFIRM=(1354 508)       # CONTINUAR in the start-raid confirmation
STEP_TIMEOUT=60          # seconds a step waits for its expected screen
HOME_TIMEOUT=300         # seconds to wait for home after the title tap
TITLE_TRIES=150          # title polling, 2 s each

NO_PLAY=0
SHOT_DIR=/tmp
while [ $# -gt 0 ]; do
  case "$1" in
    --no-play) NO_PLAY=1 ;;
    --shot) SHOT_DIR="$2"; shift ;;
    *) echo "unknown argument: $1" >&2; exit 2 ;;
  esac
  shift
done
mkdir -p "$SHOT_DIR"
adb logcat -c 2>/dev/null

fail() { echo "FAILED at $1: $2" >&2; exit 1; }

focus() { adb shell dumpsys window | grep -m1 -o "mCurrentFocus=.*"; }

guard() {
  focus | grep -q "$PKG" || fail "$1" "the game is not in front ($(focus))"
}

screen_png() { adb exec-out screencap -p; }

# the screen names the game logged since the last logcat clear
screens_logged() { adb logcat -d 2>/dev/null | grep -o "TFTFBOT\] screen=[A-Za-z0-9_]*" | sed 's/.*screen=//'; }

# wait until the expected screen is logged; a stray base edit popup is closed with BACK and the wait goes on
wait_screen() {
  local step=$1 want=$2 timeout=$3 waited=0 seen
  while [ "$waited" -lt "$timeout" ]; do
    seen=$(screens_logged)
    if echo "$seen" | grep -qx "$want"; then
      echo "step $step: screen $want"
      return 0
    fi
    if echo "$seen" | grep -qx "BaseEditNodePopup"; then
      echo "step $step: closing a stray base edit popup"
      adb shell input keyevent KEYCODE_BACK
      adb logcat -c 2>/dev/null
    fi
    sleep 2
    waited=$((waited + 2))
  done
  return 1
}

# one step: clear the log, tap, and wait for the expected screen; one retry, then the run stops with a screenshot
step() {
  local name=$1 x=$2 y=$3 want=$4 try
  guard "$name"
  for try in 1 2; do
    adb logcat -c 2>/dev/null
    echo "step $name: tap $x,$y (try $try), expecting $want"
    adb shell input tap "$x" "$y"
    if wait_screen "$name" "$want" "$STEP_TIMEOUT"; then
      return 0
    fi
  done
  screen_png > "$SHOT_DIR/raid_nav_stuck_$name.png"
  fail "$name" "screen $want did not appear (screen: $(screens_logged | tail -1)); screenshot in $SHOT_DIR"
}

if [ "$NO_PLAY" -eq 0 ]; then
  # the title screen is ready when the green JOGUE AGORA! button is drawn at its position
  tries=0
  until python3 - <<'PY' "$SHOT_DIR" "${PLAY[0]}" "${PLAY[1]}"
import subprocess, sys
from PIL import Image
out = sys.argv[1] + "/.title_probe.png"
with open(out, "wb") as f:
    f.write(subprocess.run(["adb", "exec-out", "screencap", "-p"], capture_output=True).stdout)
r, g, b = Image.open(out).convert('RGB').getpixel((int(sys.argv[2]), int(sys.argv[3])))
sys.exit(0 if g > 90 and g > r + 60 and g > b + 60 else 1)
PY
  do
    tries=$((tries + 1))
    [ "$tries" -gt "$TITLE_TRIES" ] && fail "title" "the title screen did not appear within about 5 minutes"
    sleep 2
  done
  echo "title screen ready"
  adb logcat -c 2>/dev/null
  echo "step title: tap ${PLAY[0]},${PLAY[1]}, waiting for home"
  adb shell input tap "${PLAY[0]}" "${PLAY[1]}"
  wait_screen "title" "TransformersHomeScreen" "$HOME_TIMEOUT" || fail "home" "TransformersHomeScreen was not logged within $HOME_TIMEOUT s"
  echo "home loaded"
  sleep 3   # the home screen settles after its screen is logged
else
  guard "start"
fi

step "fight-tab" "${FIGHT_TAB[0]}" "${FIGHT_TAB[1]}" "FightLandingScreen"
step "assaltos" "${ASSALTOS_CARD[0]}" "${ASSALTOS_CARD[1]}" "PVPRaidSelect"
step "first-npc" "${FIRST_NPC[0]}" "${FIRST_NPC[1]}" "ConfirmationPopup"
guard "confirm"
adb logcat -c 2>/dev/null
echo "step confirm: tap ${CONFIRM[0]},${CONFIRM[1]}, waiting for PVPRAID Enter RaidFlow"
adb shell input tap "${CONFIRM[0]}" "${CONFIRM[1]}"
raid_started=0
for waited in $(seq 1 "$STEP_TIMEOUT"); do
  if adb logcat -d 2>/dev/null | grep -q "PVPRAID.*Enter RaidFlow"; then raid_started=1; break; fi
  sleep 1
done
[ "$raid_started" -eq 1 ] || echo "step confirm: no PVPRAID Enter RaidFlow within $STEP_TIMEOUT s" >&2

shot="$SHOT_DIR/raid_nav_$(date +%H%M%S).png"
screen_png > "$shot"
echo "screenshot: $shot"
echo "focus: $(focus)"
echo "--- PVPRAID log ---"
adb logcat -d 2>/dev/null | grep "PVPRAID" | tail -6 | cut -c1-200

# success needs the raid flow itself to start
if ! adb logcat -d 2>/dev/null | grep -q "PVPRAID.*Enter RaidFlow"; then
  echo "FAILED at raid-start: the raid flow never started (no PVPRAID 'Enter RaidFlow'); see the screenshot" >&2
  exit 1
fi
if adb logcat -d 2>/dev/null | grep -q "PVPRAID.*Error"; then
  exit 1
fi
exit 0
