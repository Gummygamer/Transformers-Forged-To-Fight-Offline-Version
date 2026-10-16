#!/bin/bash
# Load a converted bundle in the Unity 2020 Editor (batchmode, Docker) and print what it contains.
# Usage: bundle_check.sh <bundle file> [max output lines]
# Env: UNITY_2020_ROOT (editor install), TFTF_WORK (work dir, as in mono310/config.sh),
#      UNITY_LICENSE (default ~/.local/share/unity3d/Unity/Unity_lic.ulf),
#      UNITY_DOCKER_IMAGE (default tftf-unity2020-ubuntu20:latest)
set -euo pipefail
: "${UNITY_2020_ROOT:?set UNITY_2020_ROOT}"; : "${TFTF_WORK:?set TFTF_WORK}"
: "${UNITY_LICENSE:=$HOME/.local/share/unity3d/Unity/Unity_lic.ulf}"
: "${UNITY_DOCKER_IMAGE:=tftf-unity2020-ubuntu20:latest}"
HERE="$(cd "$(dirname "$0")" && pwd)"
P="$TFTF_WORK/spike-project"
mkdir -p "$P/Assets/Editor" "$P/Packages" "$TFTF_WORK/docker-tmp" "$TFTF_WORK/docker-root"
cp "$HERE/BundleCheck.cs" "$P/Assets/Editor/"
cp "$HERE/../../unity/Packages/manifest.json" "$P/Packages/" 2>/dev/null || true
cp "$1" "$P/check.bundle"
docker run --rm --init --network host --hostname "$(hostname)" -w /workspace/project \
  -e HOME=/root -e DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 -e CHECK_BUNDLE=/workspace/project/check.bundle \
  -v /etc/machine-id:/etc/machine-id:ro -v "$TFTF_WORK/docker-tmp":/tmp -v "$TFTF_WORK/docker-root":/root/.cache \
  -v "$UNITY_2020_ROOT":/opt/unity:ro -v "$P":/workspace/project \
  -v "$UNITY_LICENSE":/root/.local/share/unity3d/Unity/Unity_lic.ulf:ro \
  --entrypoint /opt/unity/Editor/Unity "$UNITY_DOCKER_IMAGE" \
  -batchmode -nographics -projectPath /workspace/project -executeMethod BundleCheck.Run -logFile - 2>&1 \
  | grep -E "BCHECK|Exception|not compatible|cannot be loaded|Failed" | grep -v "ALSA\|FMOD" | head -"${2:-25}"
