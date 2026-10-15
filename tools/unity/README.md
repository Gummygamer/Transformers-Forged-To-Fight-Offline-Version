# Unity 2020.3.31f1 in Docker

The recovery image supplies Ubuntu 20.04's Unity runtime dependencies. The
Unity Editor and its bundled Mono runtime stay on the external volume and are
mounted read-only, avoiding an additional 11 GB copy in Docker storage.

```bash
export UNITY_2020_ROOT=/run/media/darabat/24f15d44-9691-4cf4-8b52-bc03f1e3693f/Unity/Hub/Editor/2020.3.31f1
export TFTF_UNITY_PROJECT=/run/media/darabat/24f15d44-9691-4cf4-8b52-bc03f1e3693f/Transformers-Forged-To-Fight-Offline-Version/build/analysis/rea/9.2/recovery-9.2-confirmed-source-v1/unity-rebuild
docker compose -f tools/unity/compose.ubuntu20.yaml run --rm unity2020-31f1
docker compose -f tools/unity/compose.ubuntu20.yaml run --rm --entrypoint /bin/bash unity2020-31f1 -lc '/opt/unity/Editor/Data/MonoBleedingEdge/bin-linux64/mono --version; /opt/unity/Editor/Data/MonoBleedingEdge/bin-linux64/mcs --version'
```

The first command should print Unity `2020.3.31f1`; the second reports the
Mono runtime and C# compiler shipped with that same Editor. Set both variables
to the current external mount paths before running Compose. The regular guarded
rebuild remains `tools/build_recovered_9_2.py`; this Compose service provides a
reproducible container environment without relocating or duplicating the
installation.

## Licensed batch build of the recovered 2.0.2 client

Unity binds `Unity_lic.ulf` to the host machine id. The container needs the host
identity and the license file, or it exits with "Machine identification is invalid":

```bash
docker run --rm --init --network host --hostname "$(hostname)" -w /workspace/project \
  -e HOME=/root -e DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 \
  -e TFTF_202_OUTPUT=/workspace/project/2.0.2-recompiled-armv7.apk \
  -v /etc/machine-id:/etc/machine-id:ro \
  -v "$UNITY_2020_ROOT":/opt/unity:ro -v "$TFTF_UNITY_PROJECT":/workspace/project \
  -v "$HOME/.local/share/unity3d/Unity/Unity_lic.ulf":/root/.local/share/unity3d/Unity/Unity_lic.ulf:ro \
  --entrypoint /opt/unity/Editor/Unity tftf-unity2020-ubuntu20:latest \
  -batchmode -nographics -projectPath /workspace/project \
  -executeMethod Recompile202Build.BuildAndroid -logFile /workspace/project/build.log
```

Before building, copy `android-plugin/` into `Assets/Plugins/Android/` and generate
the manifest override with `tools/original_manifest_to_xml.py ORIGINAL.apk
Assets/Plugins/Android/AndroidManifest.xml`. Afterwards run
`tools/finalize_recompiled_202_apk.py` (restores the original Java dex, optional
endpoint, align and sign).

### Fast iteration for managed-code fixes

A full player build takes about 9 minutes. For C#-only changes run
`Recompile202Build.CompileScriptsOnly` (set `TFTF_202_SCRIPTS_OUT`, pass `-quit`)
and inject the DLLs into an existing finished APK with
`tools/fast_patch_managed.py BASE.apk SCRIPTS_DIR OUT.apk` (about 40 seconds).
`Recompile202Build.BuildAndroid` now calls `EditorApplication.Exit(0)` so batch
runs do not linger after `BuildResult.Succeeded`.
