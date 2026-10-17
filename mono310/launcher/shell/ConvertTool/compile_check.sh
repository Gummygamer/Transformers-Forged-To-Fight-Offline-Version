#!/usr/bin/env bash
# Compiles the whole phone-side C# (Tftf/*.cs) against the Unity 2020 engine modules, with a stub for the one game type
# it uses (ShaderFallback). Catches syntax and API errors in seconds instead of after a full player build.
# Needs UNITY_2020_ROOT. Prints nothing and exits 0 when everything compiles.
set -euo pipefail
HERE=$(cd "$(dirname "$0")" && pwd)
ED=${UNITY_2020_ROOT:?}/Editor/Data
MONO=$ED/MonoBleedingEdge
LIB=$MONO/lib/mono/4.5
OUT=${TMPDIR:-/tmp}/tftf-compilecheck.$$
mkdir -p "$OUT"; trap 'rm -rf "$OUT"' EXIT
cat > "$OUT/Stub.cs" <<'S'
using UnityEngine;
public static class ShaderFallback { public static Shader Find(string name) { return null; } }
S
REFS=(-r:"$LIB/mscorlib.dll" -r:"$LIB/System.dll" -r:"$LIB/System.Core.dll" -r:"$LIB/System.IO.Compression.dll")
for m in CoreModule IMGUIModule AndroidJNIModule UnityWebRequestModule UnityWebRequestTextureModule InputLegacyModule AssetBundleModule TextRenderingModule; do
  REFS+=(-r:"$ED/Managed/UnityEngine/UnityEngine.$m.dll")
done
"$MONO/bin/mcs" -noconfig -nostdlib+ -define:UNITY_ANDROID -target:library -out:"$OUT/Tftf.dll" "${REFS[@]}" \
  "$OUT/Stub.cs" "$HERE"/../Tftf/*.cs "$HERE"/../Tftf/Convert/*.cs "$HERE"/../Tftf/Odr/*.cs 2>&1 | grep -v "warning" || true
test -f "$OUT/Tftf.dll" && echo "compile check ok"
