#!/usr/bin/env bash
# Run the game on the phone against the repository's server on this PC, so a server change needs no push and no app
# restart, and every request and reply is in a local log.
#
#   devserver.sh up        seed the state from the phone (first time), take the phone's port 8080 with adb reverse,
#                          start the PC server and relaunch the game
#   devserver.sh restart   restart only the PC server (after a change to Server/*.lbl); the game keeps running
#   devserver.sh down      stop the PC server, remove the reverse and relaunch the game on its own server
#   devserver.sh status    say which server the phone talks to
#   devserver.sh reqs [N]  the last N requests (default 30), one line each, without telemetry and pings
#   devserver.sh reply PATTERN   the newest request whose line matches PATTERN, with its body and the reply sent
#
# How it works: the game's server (Server/run_local.lbl under liblegible.so) listens on the phone's 127.0.0.1:8080. With
# "adb reverse tcp:8080" set before the game starts, the phone's port belongs to adb, the game's own server cannot bind
# and the game's requests arrive here. Server data files are read per request; .lbl modules are parsed at start, so they
# need "restart" (about a second). The state lives in $DEV/logs (TFTF_LOG_DIR), not in the repository.
# The ODR manifest comes from the phone's copy; the bundles themselves are still served on the phone (port 8090).
set -u
HERE=$(cd "$(dirname "$0")" && pwd)
REPO=$(cd "$HERE/../../.." && pwd)
PKG=unicronslair.bigrobot
PHONE=/sdcard/Android/data/$PKG/files/tftf
DEV=${TFTF_DEV_DIR:-$HOME/tftf-build/devserver}
PORT=8080
LOGS=$DEV/logs

server_pid() {
  local candidate
  for candidate in $(pgrep -f "legible run Server/run_local.lbl"); do
    if [ -r "/proc/$candidate/environ" ] && tr '\0' '\n' < "/proc/$candidate/environ" | rg -F -x -q "TFTF_LOG_DIR=$LOGS"; then
      echo "$candidate"; return
    fi
  done
}

start_server() {
  mkdir -p "$LOGS" "$DEV/odr"
  ( cd "$REPO" && HTTP_PORT=$PORT TFTF_LOG_DIR="$LOGS" TFTF_TUTORIAL_BLOCKLIST= TFTF_ODR_DIR="$DEV/odr" \
      setsid nohup legible run Server/run_local.lbl >> "$DEV/server.log" 2>&1 & )
  for _ in $(seq 1 100); do
    if (exec 3<>/dev/tcp/127.0.0.1/$PORT) 2>/dev/null; then echo "server listening on $PORT (pid $(server_pid))"; return 0; fi
    sleep 0.1
  done
  echo "the server did not start; last lines of $DEV/server.log:" >&2; tail -15 "$DEV/server.log" >&2; return 1
}

stop_server() { local pid; pid=$(server_pid); [ -n "$pid" ] && kill "$pid" 2>/dev/null; sleep 0.3; }

launch() { adb shell monkey -p $PKG -c android.intent.category.LAUNCHER 1 > /dev/null 2>&1; }

case "${1:-}" in
  up)
    adb shell am force-stop $PKG
    if [ ! -f "$LOGS/.seeded" ]; then
      mkdir -p "$LOGS" "$DEV/odr"
      adb pull "$PHONE/Server/logs/." "$LOGS/" > /dev/null && rm -f "$LOGS/requests.log" && touch "$LOGS/.seeded"
      adb pull "$PHONE/odr/manifest.json" "$DEV/odr/manifest.json" > /dev/null
      echo "state seeded from the phone into $LOGS"
    fi
    stop_server
    : > "$DEV/server.log"
    start_server || exit 1
    adb reverse tcp:$PORT tcp:$PORT > /dev/null || { echo "adb reverse failed" >&2; exit 1; }
    adb logcat -c
    launch
    echo "game relaunched against the PC server; state in $LOGS"
    ;;
  restart)
    stop_server
    start_server
    ;;
  down)
    stop_server
    adb reverse --remove tcp:$PORT 2>/dev/null
    adb shell am force-stop $PKG
    launch
    echo "game relaunched on its own server"
    ;;
  status)
    echo "pc server: $(server_pid || true)"
    echo "adb reverse: $(adb reverse --list | tr '\n' ' ')"
    ;;
  reqs)
    grep -n "^\[" "$LOGS/requests.log" | grep -v "telemetry\|util/ping" | sed -E 's/\?[^ ]*//; s#https://127.0.0.1##' | tail -"${2:-30}"
    ;;
  reply)
    # requests.log holds the request; the reply body is in replies.log when TFTF_LOG_REPLIES is on, else replay the request
    line=$(grep -n "^\[.*$2" "$LOGS/requests.log" | tail -1 | cut -d: -f1)
    [ -n "$line" ] || { echo "no request matches $2" >&2; exit 1; }
    sed -n "${line},$((line + 3))p" "$LOGS/requests.log" | cut -c1-2000
    ;;
  *)
    sed -n '2,12p' "$0"; exit 2 ;;
esac
