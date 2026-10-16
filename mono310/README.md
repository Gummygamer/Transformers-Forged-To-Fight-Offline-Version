# mono310 - 3.1.0 client recompiled with Unity 2020.3.31f1

Rebuilds the operator's own 3.1.0 (Mono, `com.kabam.bigrobot` 822754) Android APK with the
Unity 2020 toolchain so it runs on current devices, against this repository's local revival
server (`Server/`, profile `mono310`). The story map, arena and fighter bundles come from the
operator's own 9.2.0 APK. **No game bytes are stored here** - everything is generated locally
from APKs you supply; see "Compliance".

## What you need

| Input | How |
|---|---|
| 3.1.0 APK (`TFTF_310_APK`) | your own copy of `com.kabam.bigrobot` 3.1.0-822754 |
| 9.2.0 APK (`TFTF_92_APK`) | optional; only for the story map / arena / fighters (`wad` step) |
| AssetRipper headless (`ASSETRIPPER_BIN`) | `AssetRipper.GUI.Free` build, as in `DECOMPILATION.md` |
| ILSpy cmdline 9.1.x (`ILSPYCMD`) | `ilspycmd.dll` (.NET 8) |
| Unity 2020.3.31f1 (`UNITY_2020_ROOT`) + license | installed editor and `Unity_lic.ulf` |
| Docker image | `docker build -t tftf-unity2020-ubuntu20 -f tools/unity/Dockerfile.ubuntu20 tools/unity` |
| .NET 8, python3, rsync, unzip | host tools |

Set the variables (see `config.sh`; defaults put all output in `build/mono310`, which is
git-ignored) and run:

```bash
mono310/run.sh doctor          # checks inputs and tools
mono310/run.sh all             # rip, decompile, assemble, assets, compile, build, finalize, wad
```

Individual steps: `rip`, `decompile`, `assemble`, `assets`, `compile`, `build`, `finalize`,
`wad`, and `fast [--install]` for script-only iteration (reassemble, compile, inject DLLs).

## Running it

```bash
TFTF_ODR_DIR=build/mono310/odr HTTP_PORT=8081 legible run Server/run_local.lbl
TFTF_ODR_DIR=build/mono310/odr PORT=8090 python3 Server/odr_static_server.py
adb reverse tcp:8080 tcp:8081; adb reverse tcp:8090 tcp:8090
adb install -r build/mono310/3.1.0-recompiled-armv7.apk
```

The first launch downloads the generated WAD (hundreds of MB) from the static server; later
launches use the device cache. In-game: FIGHT! -> Solo Missions -> Story -> chapter -> START.

## Status

The scripts were moved here from the working copy that produced the verified build, with host
paths replaced by the variables in `config.sh`. `scripts/assemble_project.py` was additionally
changed to stop depending on a separately built 2.0.2 reference project (ripped settings, the
APK's own plugin DLLs and native libraries are used instead). That change has **not** been
run end to end yet; use `mono310/run.sh doctor` first and expect to fix small path issues.

## Layout

- `run.sh`, `config.sh` - driver and configuration.
- `scripts/assemble_project.py` - builds the Unity project from the rip + decompile.
- `scripts/compat310.py` - authored repairs for decompiler/engine-port defects (anchors are a
  few lines of text; replacements are newly written). `scripts/repairs.json` stores only file
  names, line numbers and repair kinds.
- `scripts/gen_shaders.py` + `unity/shaders/` - newly authored placeholder shaders.
- `scripts/build_odr_wad.py` - repacks bundles from the 9.2.0 APK into the on-demand WAD.
- `unity/Recompile310Build.cs`, `unity/Packages/manifest.json` - authored Unity build glue.

## Compliance

See the `3.1.0 recompile (mono310)` section of `COMPLIANCE.md`.
