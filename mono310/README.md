# mono310 - 3.1.0 client recompiled with Unity 2020.3.31f1

Rebuilds the operator's own 3.1.0 (Mono, `com.kabam.bigrobot` build 822754) Android APK with
the Unity 2020 toolchain so it runs on current devices, against this repository's local revival
server (`Server/`, profile `mono310`). The story map, arena and fighter bundles come from the
operator's own 9.2.0 APK. **No game bytes are stored in this repository**: everything is
generated locally from APKs you supply (see `COMPLIANCE.md`, section "3.1.0 recompile").

## Status

Verified end to end on a clean work directory, using only the committed scripts and the two
APKs: rip, decompile, assemble, assets, script compile (no compiler errors), full Unity player
build, finalize/sign, and the WAD self-test all pass. The resulting APK was installed on an
Android device (Samsung A52, armeabi-v7a build) and plays the story path:

- boot, login against the local server, home base with the real 3D terrain board
- FIGHT! -> Solo Missions -> Story Missions -> Act 1 -> Chapter 1 -> "Bludgeon's Ambush"
- quest map (ruinedcity theme), dialogue, node movement, pre-fight screen
- fight in the Karnak arena with 3D fighters, HUD, K.O./results screen, back to the quest map

Known rough edges (cosmetic or untested):

- Materials of the donor PBR family are replaced at runtime by a simple lit placeholder shader
  (`TFTF/LitTex`), so robots and terrain look flatter than the original; some unlit black
  hexes remain on the quest board; a warm tint comes from the placeholder time-of-day globals.
- Some UI atlas art (fight-mode buttons) is missing and draws black.
- A debug component (`TftfDiag`, logs cameras/renderers every 15 s) is still compiled in.
- Only the first story acts have been exercised; other themes' bundles are packed but unverified.
- The first launch downloads a generated WAD of roughly 400 MB once from the static server.
- Verified with the tool versions listed below on one Linux machine; other setups may need
  small path fixes.

The assetless launcher variant (no game assets in the APK; you pick your APKs on the phone) is described in
[`launcher/README.md`](launcher/README.md) and [`ANDROID_LAUNCHER.md`](ANDROID_LAUNCHER.md).

## Requirements

Game inputs (yours; never committed):

| Variable | What |
|---|---|
| `TFTF_310_APK` | 3.1.0 APK (`com.kabam.bigrobot` 3.1.0-822754) |
| `TFTF_92_APK` | 9.2.0 APK (story map, arenas, fighters); needed for the `wad` step and for the `assets` step, which extracts the 9.2 fighters' move data (without it their attacks never hit) |

Public external tools (not authored here, not bundled):

| Variable | What |
|---|---|
| `ASSETRIPPER_BIN` | AssetRipper headless GUI build (`AssetRipper.GUI.Free`), see `DECOMPILATION.md` |
| `ILSPYCMD` | `ilspycmd.dll` 9.1.x (.NET 8); the repairs match this version's output text |
| `UNITY_2020_ROOT` | Unity 2020.3.31f1 install (contains `Editor/Unity`) |
| `UNITY_LICENSE` | `Unity_lic.ulf` (default `~/.local/share/unity3d/Unity/Unity_lic.ulf`) |
| `UNITY_DOCKER_IMAGE` | `docker build -t tftf-unity2020-ubuntu20 -f tools/unity/Dockerfile.ubuntu20 tools/unity` |
| `DOTNET` | .NET 8 runtime (`dotnet`) |
| `ANDROID_HOME` | Android SDK with `build-tools` (`zipalign`, `apksigner`) and `adb` |
| `TFTF_KEYSTORE` | signing keystore (default `~/.android/debug.keystore`) |

Host tools: python3, rsync, unzip, docker. `pip install UnityPy` (reads the 9.2.0 move bundle in the `assets` step). To test on a device you also need the revival
server (Legible runtime) and `Server/odr_static_server.py`.

Repository code it relies on (already committed): `tools/finalize_recompiled_202_apk.py`,
`tools/original_manifest_to_xml.py`, `tools/fast_patch_managed.py`, `tools/unity/`, `Server/`.

## Usage

```bash
export TFTF_310_APK=/path/to/3.1.0.apk TFTF_92_APK=/path/to/9.2.0.apk
export ASSETRIPPER_BIN=... ILSPYCMD=... UNITY_2020_ROOT=... DOTNET=dotnet
mono310/run.sh doctor    # checks inputs and tools (including build-tools and keystore)
mono310/run.sh all       # rip, decompile, assemble, assets, compile, build, finalize, wad
```

Output goes to `build/mono310` (or `TFTF_WORK`), which is git-ignored:
`3.1.0-recompiled-armv7.apk` and `odr/` (`toc.zip`, `manifest.json`, mapping report).

Single steps: `rip`, `decompile`, `assemble`, `assets`, `compile`, `build`, `finalize`,
`wad`. `fast [--install]` is the script-only loop (reassemble, compile, inject the managed
DLLs into the existing APK, optionally `adb install`); it does not rebuild assets, shaders or
project settings, use `build` for those. `TFTF_ENDPOINT` sets the server URL baked into the
APK (default `http://127.0.0.1:8080`); `TFTF_ODR_CDN` sets the WAD host (default
`http://127.0.0.1:8090`).

## Windows build (native, no Docker)

`mono310/winbuild.ps1` builds a Windows x64 Mono player of the full client with the Windows
Unity 2020.3.31f1 editor (its Windows Build Support (Mono) is part of the base install). It reuses
an existing rip and decompile and needs Python 3 with UnityPy (no `rsync` or `aapt2`):

```powershell
$env:RIPPED='D:\tftf-310-cleanroom\ripped'; $env:ILSPY_OUT='D:\tftf-310-cleanroom\ilspy-out'
$env:UNITY_2020_EXE='C:\Program Files\Unity\Hub\Editor\2020.3.31f1\Editor\Unity.exe'
powershell -ExecutionPolicy Bypass -File mono310\winbuild.ps1 all   # or assemble | assets | build
```

Defaults: `TFTF_WORK=C:\tftf-310-win`, `TFTF_310_APK` in `%USERPROFILE%\Downloads`. Output:
`%TFTF_WORK%\UnityProject\win\TFTF.exe`. `TFTF_ENDPOINT` is written into
`Resources/endpointconfig.bytes`; the original APK's streaming assets become the player's
`StreamingAssets`. The build runs the editor with a window (it quits by itself) because a Unity
Hub Personal license is rejected in `-batchmode`; set `TFTF_UNITY_BATCH=1` with a seat or
`.ulf` license that batch mode accepts. Open the project once interactively first to activate.

## Running on a device

```bash
TFTF_ODR_DIR=build/mono310/odr HTTP_PORT=8081 legible run Server/run_local.lbl
TFTF_ODR_DIR=build/mono310/odr PORT=8090 python3 Server/odr_static_server.py
adb reverse tcp:8080 tcp:8081; adb reverse tcp:8090 tcp:8090
adb install -r build/mono310/3.1.0-recompiled-armv7.apk
```

Grant storage permission when asked, tap Play (the splash becomes interactive after about
50 s), then FIGHT! -> Solo Missions -> Story. Set `TFTF_TUTORIAL_BLOCKLIST=` (empty) so every
tutorial request succeeds on the mono310 profile.

## Layout

- `run.sh`, `config.sh` - driver and configuration.
- `scripts/assemble_project.py` - builds the Unity project from the rip and the decompile;
  ripped project settings are patched (bundle version, min SDK, API level, skin weights,
  physics).
- `scripts/compat310.py` - authored repairs for decompiler and engine-port defects (text
  anchors of a few lines; replacements are newly written). `scripts/repairs.json` stores only
  file names, line numbers and repair kinds.
- `scripts/gen_shaders.py`, `unity/shaders/` - newly authored placeholder shaders.
- `scripts/build_odr_wad.py` - repacks bundles from the 9.2.0 APK into the on-demand WAD.
- `scripts/rip_assets.py`, `convert_audio.py`, `closure_split.py` - rip wrapper, audio
  conversion, and a detector for a decompiler closure defect.
- `unity/Recompile310Build.cs`, `unity/Packages/manifest.json` - Unity build glue.

## Compliance

Only newly authored tooling is committed; see `COMPLIANCE.md`. Generated trees, the APK and
the WAD stay on the operator's machine and must not be committed or redistributed.
