#!/usr/bin/env bash
# Lockstep self-test of the online Arena code without a phone: compiles shell/Tftf/TftfNet.cs and TftfRelay.cs against a
# UnityEngine stub and, once with Server/pvp_relay.py and once with the phone's own relay (TftfRelay), starts two clients that play three fights of random inputs with timing jitter, and checks that
# both applied the same inputs, random rolls and clock on every tick, then that a dropped connection is noticed.
# Needs UNITY_2020_ROOT (for mcs and mono) and python3. Prints "lockstep test ok" on success.
set -euo pipefail
HERE=$(cd "$(dirname "$0")" && pwd)
REPO=$(cd "$HERE/../../../.." && pwd)
MONO=${UNITY_2020_ROOT:?}/Editor/Data/MonoBleedingEdge
OUT=$(mktemp -d); trap 'kill $(jobs -p) 2>/dev/null; rm -rf "$OUT"' EXIT
"$MONO/bin/mcs" -out:"$OUT/h.exe" "$HERE/UnityStub.cs" "$HERE/Harness.cs" "$REPO/mono310/launcher/shell/Tftf/TftfNet.cs" "$REPO/mono310/launcher/shell/Tftf/TftfRelay.cs" 2>&1 | grep -v warning || true
cd "$OUT"
lockstep() {   # $1 = relay port; both clients, three fights, then the dropped-connection check
  local PORT=$1
  timeout 60 "$MONO/bin/mono" h.exe A "$PORT" 400 a.txt 3 2> a.log & A=$!
  sleep 0.3
  timeout 60 "$MONO/bin/mono" h.exe B "$PORT" 400 b.txt 3 2> b.log
  wait $A
  diff <(grep -v '^round' a.txt) <(grep -v '^round' b.txt) > /dev/null || { echo "FAIL: the two clients diverged"; diff a.txt b.txt | head; exit 1; }
  [ "$(grep -c 'ended tick 391' a.log b.log | awk -F: '{s+=$2} END {print s}')" = 6 ] || { echo "FAIL: fights did not end at the knock-out tick"; cat a.log b.log; exit 1; }
  timeout 60 "$MONO/bin/mono" h.exe A "$PORT" 300 c.txt 1 2> c.log & A=$!
  sleep 0.3
  timeout 60 "$MONO/bin/mono" h.exe B "$PORT" 300 d.txt 1 drop 2> d.log
  wait $A
  grep -q "lost True msg The opponent disconnected" c.log || { echo "FAIL: dropped connection not noticed"; cat c.log; exit 1; }
}
BASE=${PORT:-17777}
python3 "$REPO/Server/pvp_relay.py" --host 127.0.0.1 --port "$BASE" > relay.log 2>&1 &
sleep 0.5
lockstep "$BASE"
"$MONO/bin/mono" h.exe relay $((BASE + 1)) 2> phone-relay.log &
sleep 0.5
lockstep $((BASE + 1))
PVP_RELAY_PORT=$((BASE + 1)) python3 "$REPO/Server/test_pvp_relay.py" > relay-tests.log 2>&1 || { echo "FAIL: relay tests against TftfRelay"; cat relay-tests.log; exit 1; }
echo "lockstep test ok"
