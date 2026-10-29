# Shared configuration for the 3.1.0 Unity-2020 recompile. Source it; every value can be
# overridden from the environment. Nothing here points at a game file inside the repository.
MONO310_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_DIR="$(cd "$MONO310_DIR/.." && pwd)"

TFTF_VR="${TFTF_VR:-}"
TFTF_P2P_BOOTSTRAP="${TFTF_P2P_BOOTSTRAP:-}"   # shell: host:port of the online Arena's bootstrap node, baked into the build

# Inputs the operator supplies (entitled copies of the game).
: "${TFTF_310_APK:=$REPO_DIR/com.kabam.bigrobot_v3.1.0-822754_Android-4.4.apk}"
: "${TFTF_92_APK:=$REPO_DIR/transformers-forged-to-fight-9-2-0.apk}"   # optional: story map/arena/character bundles

# Everything generated lives under one ignored work directory.
: "${TFTF_WORK:=$REPO_DIR/build/mono310}"
# TFTF_SHELL=1 selects the assetless launcher shell: its own project and APK, scripts only.
# ARM64 is supported only for that shell (IL2CPP); the full recompile still uses Mono/ARMv7.
: "${TFTF_ANDROID_ABI:=armeabi-v7a}"
case "$TFTF_ANDROID_ABI" in
  armeabi-v7a) TFTF_ANDROID_ARCH=armv7 ;;
  arm64-v8a)   TFTF_ANDROID_ARCH=arm64 ;;
  *) echo "error: TFTF_ANDROID_ABI must be armeabi-v7a or arm64-v8a" >&2; exit 2 ;;
esac
if [ "${TFTF_SHELL:-}" = 1 ]; then SHELL_NAME=ShellProject; SHELL_APK="tftf-launcher-$TFTF_ANDROID_ARCH.apk"; else SHELL_NAME=UnityProject; SHELL_APK=3.1.0-recompiled-armv7.apk; fi
# TFTF_VR=1 (with TFTF_SHELL=1 and arm64-v8a) selects the Meta Quest VR shell: its own project and APK beside the phone shell.
if [ "${TFTF_VR:-}" = 1 ]; then
  [ "${TFTF_SHELL:-}" = 1 ] && [ "$TFTF_ANDROID_ABI" = arm64-v8a ] || { echo "error: TFTF_VR=1 needs TFTF_SHELL=1 and TFTF_ANDROID_ABI=arm64-v8a" >&2; exit 2; }
  SHELL_NAME=ShellProjectVR; SHELL_APK=tftf-launcher-quest.apk
fi
RIPPED="$TFTF_WORK/ripped"            # AssetRipper export of the 3.1.0 APK
ILSPY_OUT="$TFTF_WORK/ilspy-out"      # decompiled C# (never committed)
PROJECT="$TFTF_WORK/$SHELL_NAME"     # assembled Unity project
ODR_DIR="$TFTF_WORK/odr"              # toc.wad / toc.zip / manifest.json
OUT_APK="$TFTF_WORK/$SHELL_APK"

# Tools (not bundled; see mono310/README.md).
: "${ASSETRIPPER_BIN:=}"              # AssetRipper.GUI.Free (headless)
: "${ILSPYCMD:=}"                     # path to ilspycmd.dll (9.1.x, .NET 8)
: "${DOTNET:=dotnet}"
: "${UNITY_2020_ROOT:=}"              # .../Editor/2020.3.31f1 (contains Editor/Unity)
: "${UNITY_LICENSE:=$HOME/.local/share/unity3d/Unity/Unity_lic.ulf}"
: "${UNITY_DOCKER_IMAGE:=tftf-unity2020-ubuntu20:latest}"   # tools/unity/Dockerfile.ubuntu20
: "${ADB:=adb}"
: "${ADB_DEVICE:=}"

export TFTF_VR TFTF_P2P_BOOTSTRAP TFTF_WORK TFTF_310_APK TFTF_92_APK RIPPED ILSPY_OUT PROJECT ODR_DIR OUT_APK TFTF_ANDROID_ABI TFTF_ANDROID_ARCH
