#!/usr/bin/env bash
# Compiles the whole phone-side C# (Tftf/*.cs) against the Unity 2020 engine modules, with stubs for the two game types
# it uses (ShaderFallback, UITexture). Catches syntax and API errors in seconds instead of after a full player build.
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
public class UIWidget : MonoBehaviour { public Texture mainTexture; public int width, height; }
public class UITexture : UIWidget { public Shader shader; public Rect uvRect; }
namespace EB
{
    public static class Assets
    {
        public static T[] LoadAll<T>(string path) where T : Object { return null; }
        public static void Unload(Object o) { }
    }
    public static class Localizer
    {
        public static System.Collections.Generic.Dictionary<string, string> Strings;
        public static System.Collections.Hashtable LoadStringsFromText(string text) { return null; }
    }
    public static class JSON { public static object Parse(string text) { return null; } }
}
S
REFS=(-r:"$LIB/mscorlib.dll" -r:"$LIB/System.dll" -r:"$LIB/System.Core.dll" -r:"$LIB/System.IO.Compression.dll")
for m in CoreModule IMGUIModule AndroidJNIModule UnityWebRequestModule UnityWebRequestTextureModule InputLegacyModule ImageConversionModule AssetBundleModule TextRenderingModule; do
  REFS+=(-r:"$ED/Managed/UnityEngine/UnityEngine.$m.dll")
done
"$MONO/bin/mcs" -noconfig -nostdlib+ -define:UNITY_ANDROID -target:library -out:"$OUT/Tftf.dll" "${REFS[@]}" \
  "$OUT/Stub.cs" "$HERE"/../Tftf/*.cs "$HERE"/../Tftf/Convert/*.cs "$HERE"/../Tftf/Odr/*.cs 2>&1 | grep -v "warning" || true
test -f "$OUT/Tftf.dll" && echo "compile check ok"
