#!/bin/bash
# 3.1.0 Unity-2020 recompile driver. See README.md. Usage: mono310/run.sh <step> [args]
#   doctor | rip | decompile | assemble | assets | compile | build | finalize | wad | fast | treedb | all
set -euo pipefail
. "$(dirname "$(readlink -f "$0")")/config.sh"
S="$MONO310_DIR/scripts"
need() { [ -n "${!1:-}" ] || { echo "error: set $1 (see mono310/README.md)" >&2; exit 2; }; }
die()  { echo "error: $*" >&2; exit 1; }

unity_run() {  # unity_run [docker -e args...] -- <unity args...>
  need UNITY_2020_ROOT; [ -f "$UNITY_LICENSE" ] || die "Unity license file not found: $UNITY_LICENSE"
  mkdir -p "$TFTF_WORK/docker-tmp" "$TFTF_WORK/docker-root"
  local denv=() uargs=()
  while [ $# -gt 0 ] && [ "$1" != "--" ]; do denv+=("$1"); shift; done
  [ "${1:-}" = "--" ] && shift
  uargs=("$@")
  docker run --rm --init --network host --hostname "$(hostname)" -w /workspace/project \
    -e HOME=/root -e DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 "${denv[@]}" \
    -v /etc/machine-id:/etc/machine-id:ro -v "$TFTF_WORK/docker-tmp":/tmp -v "$TFTF_WORK/docker-root":/root/.cache \
    -v "$UNITY_2020_ROOT":/opt/unity:ro -v "$PROJECT":/workspace/project \
    -v "$UNITY_LICENSE":/root/.local/share/unity3d/Unity/Unity_lic.ulf:ro \
    --entrypoint /opt/unity/Editor/Unity "$UNITY_DOCKER_IMAGE" "${uargs[@]}"
}

step_doctor() {
  local ok=0
  for t in python3 rsync unzip docker "$DOTNET"; do command -v "$t" >/dev/null || { echo "missing tool: $t"; ok=1; }; done
  [ -f "$TFTF_310_APK" ] || { echo "missing 3.1.0 APK: $TFTF_310_APK (set TFTF_310_APK)"; ok=1; }
  [ -f "$TFTF_92_APK" ] || echo "note: no 9.2.0 APK at $TFTF_92_APK (set TFTF_92_APK) - needed only for the story map/arena WAD"
  [ -n "$ASSETRIPPER_BIN" ] && [ -x "$ASSETRIPPER_BIN" ] || { echo "set ASSETRIPPER_BIN to AssetRipper.GUI.Free"; ok=1; }
  [ -n "$ILSPYCMD" ] && [ -f "$ILSPYCMD" ] || { echo "set ILSPYCMD to ilspycmd.dll (9.1.x)"; ok=1; }
  [ -n "$UNITY_2020_ROOT" ] && [ -x "$UNITY_2020_ROOT/Editor/Unity" ] || { echo "set UNITY_2020_ROOT to the 2020.3.31f1 install"; ok=1; }
  docker image inspect "$UNITY_DOCKER_IMAGE" >/dev/null 2>&1 || { echo "build the image: docker build -t $UNITY_DOCKER_IMAGE -f tools/unity/Dockerfile.ubuntu20 tools/unity"; ok=1; }
  [ -f "$UNITY_LICENSE" ] || { echo "missing Unity license: $UNITY_LICENSE"; ok=1; }
  local bt="${ANDROID_HOME:-$HOME/Android/Sdk}/build-tools"
  ls "$bt"/*/apksigner >/dev/null 2>&1 || { echo "missing Android build-tools (apksigner/zipalign) under $bt (set ANDROID_HOME)"; ok=1; }
  [ -f "${TFTF_KEYSTORE:-$HOME/.android/debug.keystore}" ] || { echo "missing signing keystore: ${TFTF_KEYSTORE:-$HOME/.android/debug.keystore} (keytool -genkey ... or set TFTF_KEYSTORE)"; ok=1; }
  [ $ok = 0 ] && echo "doctor: ok (work dir: $TFTF_WORK)"; return $ok
}

step_rip() {
  need ASSETRIPPER_BIN; mkdir -p "$TFTF_WORK"
  python3 "$S/rip_assets.py" "$TFTF_310_APK" "$RIPPED" --assetripper "$ASSETRIPPER_BIN"
}

step_decompile() {
  need ILSPYCMD
  local G="$RIPPED/AuxiliaryFiles/GameAssemblies"
  export DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1
  for n in Assembly-CSharp-firstpass Assembly-CSharp NBidi Fabric.Core Facebook.Unity Facebook.Unity.Settings Facebook.Unity.Android Facebook.Unity.IOS; do
    [ -f "$G/$n.dll" ] || continue
    rm -rf "$ILSPY_OUT/$n"
    "$DOTNET" "$ILSPYCMD" --disable-updatecheck -lv CSharp7_3 -p -r "$G" -o "$ILSPY_OUT/$n" "$G/$n.dll" | tail -2
  done
}

step_assets() {  # non-script assets, audio, replacement shaders (the shell keeps only the shaders)
  local E="$RIPPED/ExportedProject/Assets" N="$PROJECT/Assets"
  mkdir -p "$N"
  if [ "${TFTF_SHELL:-}" != 1 ]; then
  ( cd "$E" && for d in *; do case $d in Scripts|Plugins|Scenes) ;; *) rsync -a "$d" "$N/";; esac; done )
  mkdir -p "$N/Scenes"; rsync -a "$E/Scenes/" "$N/Scenes/"
  python3 "$S/convert_audio.py" "$N"
  fi
  rm -rf "$N/Shader310" "$N/Shader310.meta"
  python3 "$S/gen_shaders.py" "$N/Resources/Shader310"
  if [ -f "$TFTF_92_APK" ]; then python3 "$S/extract_donor_moves.py" "$TFTF_92_APK" "$N/Resources/movedata92" || echo "warn: donor move data not extracted; 9.2 fighters will have no hit events"; fi
  cp "$MONO310_DIR/unity/shaders/EBG_UI_BlendColored.shader" "$N/Resources/Shader310/EBG_UI_BlendColored.shader"
  cp "$MONO310_DIR/unity/shaders/EBBlit.shader" "$N/Resources/Shader310/Hidden_EB_Blit.shader"
  cp "$MONO310_DIR/unity/shaders/EBNoToneMapBlit.shader" "$N/Resources/Shader310/Hidden_EB_PostFX_NoToneMapBlit.shader"
  cp "$MONO310_DIR/unity/shaders/EBToneMapBlit.shader" "$N/Resources/Shader310/Hidden_EB_PostFX_ToneMapBlit.shader"
  cp "$MONO310_DIR/unity/shaders/EBUIScreenCapture.shader" "$N/Resources/Shader310/Hidden_EB_UIScreenCapture.shader"
  cp "$MONO310_DIR/unity/shaders/HudHealth.shader" "$N/Resources/Shader310/Hud_Health.shader"   # found by ShaderFallback under the original shader name
  cp "$MONO310_DIR/unity/shaders/HudHealth.shader" "$N/Resources/Shader310/EBG_UI_HudHealth.shader"   # the HealthBar material declares EBG/UI/HudHealth
  if [ "${TFTF_SHELL:-}" != 1 ]; then cp "$MONO310_DIR/unity/shaders/HudHealth.shader" "$N/Resources/ui/shaders/Hud_Health.shader"; fi   # keeps the ripped .meta guid the HealthBar material points at
}

step_assemble() { python3 "$S/assemble_project.py"; }

step_compile() {  # scripts only (seconds); returns non-zero on compiler errors
  local log="${1:-unity-scripts.log}"; mkdir -p "$PROJECT/scripts-out"
  unity_run -e TFTF_310_SCRIPTS_OUT=/workspace/project/scripts-out -e TFTF_SHELL="${TFTF_SHELL:-}" -- \
    -batchmode -nographics -quit -projectPath /workspace/project \
    -executeMethod Recompile310Build.CompileScriptsOnly -logFile "/workspace/project/$log" || true
  if grep -q "error CS" "$PROJECT/$log"; then grep "error CS" "$PROJECT/$log" | sort -u | head -20; return 1; fi
}

step_build() {  # full licensed player build (minutes)
  local log="${1:-unity-build.log}"
  unity_run -e TFTF_310_OUTPUT=/workspace/project/3.1.0-recompiled-armv7-unity.apk -e TFTF_SHELL="${TFTF_SHELL:-}" -- \
    -batchmode -nographics -projectPath /workspace/project \
    -executeMethod Recompile310Build.BuildAndroid -logFile "/workspace/project/$log" || true
  grep -n "Android build:" "$PROJECT/$log" | tail -1
  grep -q "Android build: Succeeded" "$PROJECT/$log" || die "Unity build failed; see $PROJECT/$log"
}

step_finalize() {  # restore original Java dex, endpoint, align and sign
  python3 "$REPO_DIR/tools/finalize_recompiled_202_apk.py" "$PROJECT/3.1.0-recompiled-armv7-unity.apk" \
    "$TFTF_310_APK" "$OUT_APK" $([ "${TFTF_SHELL:-}" = 1 ] && echo --shell || echo --endpoint "${TFTF_ENDPOINT:-http://127.0.0.1:8080}") \
    ${TFTF_KEYSTORE:+--keystore "$TFTF_KEYSTORE"}
}

step_wad() {  # story map / arena / character bundles from the 9.2.0 APK
  [ -f "$TFTF_92_APK" ] || die "missing 9.2.0 APK: $TFTF_92_APK"
  python3 "$S/build_odr_wad.py" --donor "$TFTF_92_APK" --out "$ODR_DIR" \
    --cdn "${TFTF_ODR_CDN:-http://127.0.0.1:8090}" --timeofday "$@"
  mkdir -p "$ODR_DIR/wad"; ln -f "$ODR_DIR/toc.zip" "$ODR_DIR/wad/toc.zip"
}

step_treedb() {  # type-tree database for the launcher's on-phone converter (full project, not the shell; needs UnityPy)
  [ "${TFTF_SHELL:-}" != 1 ] || die "run treedb without TFTF_SHELL: it needs the full recompiled project (run assemble first)"
  local d="$TFTF_WORK/treedb"; mkdir -p "$d" "$PROJECT/Assets/Editor"
  unzip -q -o "$TFTF_310_APK" 'assets/bin/Data/*' -d "$d/apk"
  python3 "$MONO310_DIR/launcher/convert/list_scripts.py" "$d/apk/assets/bin/Data" "$PROJECT/tt_scripts.json"
  cp "$MONO310_DIR/launcher/spike/TypeTreeDump.cs" "$PROJECT/Assets/Editor/"
  # tt_out is written by the container as root: do not delete it here, check that the bundle is newer than this marker
  touch "$d/.treedb-start"; sleep 1
  unity_run -e TT_SCRIPTS=/workspace/project/tt_scripts.json -e TT_OUT=/workspace/project/tt_out -- \
    -batchmode -nographics -projectPath /workspace/project -buildTarget Android \
    -executeMethod TypeTreeDump.Run -logFile /workspace/project/ttdump.log || true
  [ "$PROJECT/tt_out/typetrees" -nt "$d/.treedb-start" ] || die "TypeTreeDump produced no new bundle; see $PROJECT/ttdump.log"
  python3 "$MONO310_DIR/launcher/convert/export_tree_db.py" "$PROJECT/tt_out/typetrees" "$d/trees.bin"
  echo "type-tree database: $d/trees.bin  (use it as TFTF_TREES_BIN)"
}

step_fast() {  # script-only iteration: assemble, compile, inject DLLs, optional install
  step_assemble; step_compile unity-scripts-fast.log
  python3 "$REPO_DIR/tools/fast_patch_managed.py" "$OUT_APK" "$PROJECT/scripts-out" "$OUT_APK.fast"
  mv -f "$OUT_APK.fast" "$OUT_APK"
  if [ "${1:-}" = "--install" ]; then "$ADB" ${ADB_DEVICE:+-s "$ADB_DEVICE"} install -r "$OUT_APK"; fi
}

step=${1:-}; shift || true
case "$step" in
  doctor) step_doctor ;;  rip) step_rip ;;  decompile) step_decompile ;;  assets) step_assets ;;
  assemble) step_assemble ;;  compile) step_compile "$@" ;;  build) step_build "$@" ;;
  finalize) step_finalize ;;  wad) step_wad "$@" ;;  fast) step_fast "$@" ;;  treedb) step_treedb ;;
  all) step_doctor; step_rip; step_decompile; step_assemble; step_assets; step_compile; step_build; step_finalize
       [ -f "$TFTF_92_APK" ] && step_wad || echo "skipping WAD (no 9.2.0 APK)"
       echo "done: $OUT_APK" ;;
  *) sed -n 2,3p "$0"; exit 2 ;;
esac
