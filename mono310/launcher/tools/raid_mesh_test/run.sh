#!/usr/bin/env bash
set -euo pipefail
HERE=$(cd "$(dirname "$0")" && pwd)
REPO=$(cd "$HERE/../../../.." && pwd)
MONO=${UNITY_2020_ROOT:?}/Editor/Data/MonoBleedingEdge
OUT=$(mktemp -d)
trap 'rm -rf "$OUT"' EXIT
"$MONO/bin/mcs" -out:"$OUT/raid.exe" "$HERE/Harness.cs" "$HERE/../net_lockstep_test/UnityStub.cs" "$REPO/mono310/launcher/shell/Tftf/"{TftfNet,TftfBuildConfig,TftfMesh,TftfRaidNet}.cs "$REPO/mono310/launcher/shell/Tftf/Odr/JsonLite.cs"
"$MONO/bin/mono" "$OUT/raid.exe" records "$OUT/identity"
