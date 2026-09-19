#!/usr/bin/env bash
# Local analysis only: never commit the extracted assemblies or generated source.
set -euo pipefail

if [[ $# != 1 ]]; then
    echo "Usage: ILSPYCMD=/path/to/ilspycmd bash tools/decompile_mono.sh old-game.apk" >&2
    exit 2
fi
repo=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)
apk=$(realpath -- "$1")
ilspy=${ILSPYCMD:-ilspycmd}
for tool in unzip sha256sum realpath "$ilspy"; do
    command -v "$tool" >/dev/null || { echo "Missing tool: $tool" >&2; exit 1; }
done
[[ -f "$apk" ]] || { echo "Not a file: $apk" >&2; exit 1; }
# Validate before creating output; a 9.2 IL2CPP APK is not a Mono input.
entries=$(unzip -Z1 "$apk")
for name in Assembly-CSharp.dll Assembly-CSharp-firstpass.dll; do
    if ! grep -Fxq "assets/bin/Data/Managed/$name" <<< "$entries"; then
        echo "Missing managed game assembly: $name (is this a Mono APK?)" >&2
        exit 1
    fi
done
mkdir -p "$repo/build/mono"
# Fresh directories preserve earlier evidence and prevent mixed-version exports.
out=$(mktemp -d "$repo/build/mono/run-XXXXXXXX")
echo "Local analysis output: $out"
mkdir "$out/managed" "$out/source"
sha256sum "$apk" > "$out/input.sha256"
"$ilspy" --version > "$out/tool-version.txt"
while IFS= read -r entry; do
    # Exact flat DLL names only; do not extract arbitrary APK paths or assets.
    if [[ "$entry" =~ ^assets/bin/Data/Managed/([A-Za-z0-9_.-]+\.dll)$ ]]; then
        name=${BASH_REMATCH[1]}
        [[ ! -e "$out/managed/$name" ]] || { echo "Duplicate DLL: $name" >&2; exit 1; }
        unzip -p "$apk" "$entry" > "$out/managed/$name"
    fi
done <<< "$entries"
(cd "$out/managed" && sha256sum ./*.dll) > "$out/assemblies.sha256"
for name in Assembly-CSharp Assembly-CSharp-firstpass; do
    echo "Decompiling $name..."
    for kind in c i s d e; do
        "$ilspy" --disable-updatecheck -l "$kind" "$out/managed/$name.dll"
    done > "$out/$name.types.txt"
    [[ -s "$out/$name.types.txt" ]] || { echo "Empty type inventory: $name" >&2; exit 1; }
    "$ilspy" --disable-updatecheck -p -r "$out/managed" \
        -o "$out/source/$name" "$out/managed/$name.dll" > "$out/$name.log" 2>&1
done
echo "Complete: $out/source"
echo "These are recovered reference sources, not a verified rebuildable Unity project."
