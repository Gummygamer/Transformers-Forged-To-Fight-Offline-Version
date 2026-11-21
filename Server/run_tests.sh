#!/usr/bin/env bash
# Runs every Server/test_*.lbl in parallel and prints one summary line per file plus every failure.
# Slow files are split into shards (TFTF_TEST_SHARD=k/n, see sharding.lbl), each its own process, so wall time is
# the slowest shard instead of the slowest file. Usage: Server/run_tests.sh [test_name.lbl ...]   (default: all)
# JOBS=n limits parallel processes (default: nproc). SHARDS=0 runs every file unsharded.
set -uo pipefail
cd "$(dirname "$0")"
LOGS=$(mktemp -d)
FILES=("$@"); [ ${#FILES[@]} -eq 0 ] && FILES=(test_*.lbl)

# shards per file; files not listed run as one process
declare -A SHARD_COUNT=([test_gamedata.lbl]=10 [test_jsonout.lbl]=8 [test_fakeserver.lbl]=4)
shards_for() { if [ "${SHARDS:-1}" = 0 ]; then echo 1; else echo "${SHARD_COUNT[$1]:-1}"; fi; }

# one job per (file, shard); every job gets its own state directory (TFTF_LOG_DIR): the fake server keeps saved
# team, positions and sessions in files
for f in "${FILES[@]}"; do
  n=$(shards_for "$f")
  for ((k = 0; k < n; k++)); do echo "$f $k $n"; done
done | xargs -P "${JOBS:-$(nproc)}" -L 1 bash -c '
  f=$1 k=$2 n=$3 out="'"$LOGS"'/$1.$2"
  mkdir -p "$out.state"
  if [ "$n" -gt 1 ]; then export TFTF_TEST_SHARD="$k/$n"; fi
  TFTF_LOG_DIR="$out.state" legible run "$f" > "$out.log" 2>&1
  echo "$?" > "$out.rc"' _

status=0
for f in "${FILES[@]}"; do
  n=$(shards_for "$f") rc=0
  for ((k = 0; k < n; k++)); do
    r=$(cat "$LOGS/$f.$k.rc"); [ "$r" != 0 ] && rc=$r
  done
  if [ "$n" -gt 1 ]; then
    ok=$(cat "$LOGS/$f".*.log | grep -c '^\[ok\]'); bad=$(cat "$LOGS/$f".*.log | grep -c '^\[!\]')
    sum="$ok passed, $bad failed ($n shards)"
  else
    sum=$(grep -E "[0-9]+ passed" "$LOGS/$f.0.log" | tail -1)
  fi
  printf '%-34s rc=%s %s\n' "$f" "$rc" "$sum"
  cat "$LOGS/$f".*.log | grep -E '^\[!\]' | cut -c1-200 | sed 's/^/    /'
  [ "$rc" != 0 ] && status=1
done
echo "logs: $LOGS"
exit $status
