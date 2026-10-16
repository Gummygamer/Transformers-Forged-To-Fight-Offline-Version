# Shared configuration for the 3.1.0 Unity-2020 recompile. Source it; every value can be
# overridden from the environment. Nothing here points at a game file inside the repository.
MONO310_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_DIR="$(cd "$MONO310_DIR/.." && pwd)"

# Inputs the operator supplies (entitled copies of the game).
: "${TFTF_310_APK:=$REPO_DIR/com.kabam.bigrobot_v3.1.0-822754_Android-4.4.apk}"
: "${TFTF_92_APK:=$REPO_DIR/transformers-forged-to-fight-9-2-0.apk}"   # optional: story map/arena/character bundles

# Everything generated lives under one ignored work directory.
: "${TFTF_WORK:=$REPO_DIR/build/mono310}"
RIPPED="$TFTF_WORK/ripped"            # AssetRipper export of the 3.1.0 APK
ILSPY_OUT="$TFTF_WORK/ilspy-out"      # decompiled C# (never committed)
PROJECT="$TFTF_WORK/UnityProject"     # assembled Unity project
ODR_DIR="$TFTF_WORK/odr"              # toc.wad / toc.zip / manifest.json
OUT_APK="$TFTF_WORK/3.1.0-recompiled-armv7.apk"

# Tools (not bundled; see mono310/README.md).
: "${ASSETRIPPER_BIN:=}"              # AssetRipper.GUI.Free (headless)
: "${ILSPYCMD:=}"                     # path to ilspycmd.dll (9.1.x, .NET 8)
: "${DOTNET:=dotnet}"
: "${UNITY_2020_ROOT:=}"              # .../Editor/2020.3.31f1 (contains Editor/Unity)
: "${UNITY_LICENSE:=$HOME/.local/share/unity3d/Unity/Unity_lic.ulf}"
: "${UNITY_DOCKER_IMAGE:=tftf-unity2020-ubuntu20:latest}"   # tools/unity/Dockerfile.ubuntu20
: "${ADB:=adb}"
: "${ADB_DEVICE:=}"

export TFTF_WORK TFTF_310_APK TFTF_92_APK RIPPED ILSPY_OUT PROJECT ODR_DIR OUT_APK
