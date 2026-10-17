#!/usr/bin/env bash
# Runs every Server/test_*.lbl in its own process, all at once, and prints one summary line per file plus every failure.
# Wall time is the slowest file instead of the sum. Usage: Server/run_tests.sh [test_name.lbl ...]   (default: all)
# TFTF_TEST_QUICK=1 skips the second full payload build of test_export_payload's determinism check (about half of that file's time).
set -uo pipefail
cd "$(dirname "$0")"
LOGS=$(mktemp -d)
FILES=("$@"); [ ${#FILES[@]} -eq 0 ] && FILES=(test_*.lbl)
# every file gets its own state directory (TFTF_LOG_DIR): the fake server keeps saved team, positions and sessions in files
printf '%s\n' "${FILES[@]}" | xargs -P "${JOBS:-$(nproc)}" -I{} bash -c 'mkdir -p "'"$LOGS"'/state_{}"; TFTF_LOG_DIR="'"$LOGS"'/state_{}" legible run {} > "'"$LOGS"'/{}.log" 2>&1; echo "$?" > "'"$LOGS"'/{}.rc"'
status=0
for f in "${FILES[@]}"; do
  rc=$(cat "$LOGS/$f.rc"); sum=$(grep -E "[0-9]+ passed" "$LOGS/$f.log" | tail -1)
  printf '%-34s rc=%s %s\n' "$f" "$rc" "$sum"
  grep -E '^\[!\]' "$LOGS/$f.log" | cut -c1-200 | sed 's/^/    /'
  [ "$rc" != 0 ] && status=1
done
echo "logs: $LOGS"
exit $status
