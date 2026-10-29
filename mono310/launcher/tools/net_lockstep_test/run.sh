#!/usr/bin/env bash
# Lockstep self-test of the online Arena code without a phone: compiles shell/Tftf/TftfNet.cs and TftfMesh.cs against a
# UnityEngine stub and, through the P2P network (Server/p2p_bootstrap/bootstrap.py), starts two clients that play three
# fights of random inputs with timing jitter, and checks that both applied the same inputs, random rolls and clock on every
# tick, then that a dropped connection is noticed: once with the two players linked directly, once with them unable to reach
# each other so that every line goes through a third node, and once after the bootstrap node is gone, so the two games
# must find each other from the games they remember. The same fights also run through Server/pvp_relay.py (first).
# Needs UNITY_2020_ROOT (for mcs and mono) and python3. Prints "lockstep test ok" on success.
set -euo pipefail
HERE=$(cd "$(dirname "$0")" && pwd)
REPO=$(cd "$HERE/../../../.." && pwd)
MONO=${UNITY_2020_ROOT:?}/Editor/Data/MonoBleedingEdge
OUT=$(mktemp -d); trap 'kill $(jobs -p) 2>/dev/null; if [ -n "${KEEP:-}" ]; then echo "logs in $OUT"; else rm -rf "$OUT"; fi' EXIT   # KEEP=1 keeps the logs
"$MONO/bin/mcs" -out:"$OUT/h.exe" "$HERE/UnityStub.cs" "$HERE/Harness.cs" "$REPO/mono310/launcher/shell/Tftf/TftfNet.cs" "$REPO/mono310/launcher/shell/Tftf/TftfMesh.cs" "$REPO/mono310/launcher/shell/Tftf/TftfBuildConfig.cs" 2>&1 | grep -v warning || true
cd "$OUT"
lockstep() {   # $1 = relay or bootstrap port; both clients, three fights, then the dropped-connection check. $A_ENV / $B_ENV: env for each
  local PORT=$1
  env ${A_ENV:-} timeout 60 "$MONO/bin/mono" h.exe A "$PORT" 400 a.txt 3 2> a.log & A=$!
  sleep 0.3
  env ${B_ENV:-} timeout 60 "$MONO/bin/mono" h.exe B "$PORT" 400 b.txt 3 2> b.log
  wait $A
  diff <(grep -v '^round' a.txt) <(grep -v '^round' b.txt) > /dev/null || { echo "FAIL: the two clients diverged"; diff a.txt b.txt | head; exit 1; }
  [ "$(grep -c 'ended tick 391' a.log b.log | awk -F: '{s+=$2} END {print s}')" = 6 ] || { echo "FAIL: fights did not end at the knock-out tick"; cat a.log b.log; exit 1; }
  env ${A_ENV:-} MESH_SUFFIX=d timeout 60 "$MONO/bin/mono" h.exe A "$PORT" 300 c.txt 1 2> c.log & A=$!   # new games, new ids
  sleep 0.3
  env ${B_ENV:-} MESH_SUFFIX=d timeout 60 "$MONO/bin/mono" h.exe B "$PORT" 300 d.txt 1 drop 2> d.log
  wait $A
  grep -q "lost True msg The opponent disconnected" c.log || { echo "FAIL: dropped connection not noticed"; cat c.log; exit 1; }
}
BASE=${PORT:-17777}
python3 "$REPO/Server/pvp_relay.py" --host 127.0.0.1 --port "$BASE" > relay.log 2>&1 &
sleep 0.5
lockstep "$BASE"
python3 "$REPO/Server/p2p_bootstrap/bootstrap.py" --host 127.0.0.1 --port $((BASE + 2)) > bootstrap.log 2>&1 &
sleep 0.5
A_ENV="MESH_ID=a1" B_ENV="MESH_ID=b1" lockstep $((BASE + 2))
grep -q "A direct True" a.log || { echo "FAIL: P2P players did not link directly"; cat a.log; exit 1; }
"$MONO/bin/mono" h.exe node $((BASE + 2)) c2 2> node.log &
sleep 1
A_ENV="MESH_ID=a2 MESH_BLOCK=b2" B_ENV="MESH_ID=b2 MESH_BLOCK=a2" lockstep $((BASE + 2))
grep -q "A direct False" a.log || { echo "FAIL: blocked P2P players linked directly"; cat a.log; exit 1; }
cached() {   # one fight between two games that remember each other in $OUT/*.cache; $1 = bootstrap port
  env MESH_ID=a3 MESH_CACHE="$OUT/a3.cache" timeout 60 "$MONO/bin/mono" h.exe A "$1" 200 a.txt 1 2> a.log & A=$!
  sleep 0.3
  env MESH_ID=b3 MESH_CACHE="$OUT/b3.cache" timeout 60 "$MONO/bin/mono" h.exe B "$1" 200 b.txt 1 2> b.log
  wait $A
  diff <(grep -v '^round' a.txt) <(grep -v '^round' b.txt) > /dev/null || { echo "FAIL: the two clients diverged"; exit 1; }
  [ "$(grep -c 'ended tick 191' a.log b.log | awk -F: '{s+=$2} END {print s}')" = 2 ] || { echo "FAIL: cached fight did not end"; cat a.log b.log; exit 1; }
}
python3 "$REPO/Server/p2p_bootstrap/bootstrap.py" --host 127.0.0.1 --port $((BASE + 3)) > bootstrap2.log 2>&1 & BS=$!
sleep 0.5
cached $((BASE + 3))
grep -q "^b3/" "$OUT/a3.cache" || { echo "FAIL: A did not remember B"; cat "$OUT/a3.cache"; exit 1; }
kill $BS; wait $BS 2>/dev/null || true
cached $((BASE + 3))           # the bootstrap node is gone
echo "lockstep test ok"
