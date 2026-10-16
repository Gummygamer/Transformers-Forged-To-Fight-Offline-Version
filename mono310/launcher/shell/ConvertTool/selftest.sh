#!/usr/bin/env bash
# PC self-test of the code that runs on the phone: compiles Tftf/Convert and Tftf/Odr with Unity's mcs, converts the
# player data, builds the content package from the 9.2.0 APK and runs the Python package self-test on the result.
# Needs UNITY_2020_ROOT, TFTF_310_APK, TFTF_92_APK, TFTF_TREES_BIN (see mono310/launcher/README.md).
set -euo pipefail
HERE=$(cd "$(dirname "$0")" && pwd)
SHELLSRC=$HERE/..
MONO=${UNITY_2020_ROOT:?}/Editor/Data/MonoBleedingEdge
LIB=$MONO/lib/mono/4.5
OUT=${TMPDIR:-/tmp}/tftf-selftest.$$
mkdir -p "$OUT/data"
trap 'rm -rf "$OUT"' EXIT

"$MONO/bin/mcs" -noconfig -nostdlib+ -out:"$OUT/ConvertTool.exe" -r:"$LIB/mscorlib.dll" -r:"$LIB/System.dll" \
  -r:"$LIB/System.Core.dll" -r:"$LIB/System.IO.Compression.dll" \
  "$SHELLSRC"/Tftf/Convert/*.cs "$SHELLSRC"/Tftf/Odr/*.cs "$HERE/Program.cs" 2>&1 | grep -v "warning" || true
export LD_LIBRARY_PATH=$MONO/MonoEmbedRuntime        # zlib helper for DeflateStream

echo "== converter"
unzip -q -o "${TFTF_310_APK:?}" 'assets/bin/Data/*' -d "$OUT/apk"
"$MONO/bin/mono" "$OUT/ConvertTool.exe" "$OUT/apk/assets/bin/Data" "${TFTF_TREES_BIN:?}" "$OUT/resources.bundle" | tail -1

echo "== content package"
"$MONO/bin/mono" "$OUT/ConvertTool.exe" odr "${TFTF_92_APK:?}" "$OUT/odr" http://127.0.0.1:8090 822754 | tail -1
python3 "$HERE/../../../scripts/build_odr_wad.py" --selftest --out "$OUT/odr"
