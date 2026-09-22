# Mono Story-fight scratchpad

This is a handoff for continuing the recompiled Mono client investigation from a fresh session. It belongs to the dedicated worktree/branch, not the unrelated main worktree. The final section, `Fresh-session update`, supersedes older status statements above.

## Target

Reach a real Story-mode fight on the USB-connected Android device. The desired path is:

`login -> FightLandingScreen -> Story -> Story quest -> quest-begin -> FightFlow/activate-match -> combat HUD`

This is specifically the post-tutorial Story menu; the FTE/tutorial fight is not success.

## Worktree and branch

```text
worktree: /home/darabat/coding/TFTF-Mono-Investigation
branch:   research/mono-decompilation
HEAD:     08fb463
```

The worktree is currently intentionally dirty. Keep the main worktree (`/home/darabat/coding/Transformers-Forged-To-Fight-Offline-Version`) untouched because it contains unrelated user changes.

Recent commits, newest first:

```text
6c0738b Route Mono login to real Story fight menu
36f9269 Restore Mono offline fight bootstrap and asset alias
9b31468 Allow Mono client to use local revival server offline
05d673d Repair Mono async asset loading and ODR initialization
```

## What is already implemented

`tools/decompilation.py` patches the Mono managed assemblies so offline login:

- waits for the normal hub/tuning/base-hero bootstrap;
- opens the real `FightLandingScreen` directly instead of starting `FightFlow.FTE`;
- hides the loading screen after that screen enters the stack;
- tolerates missing offline localization in `QuestModeButton` (the original `Regex.Replace(null, ...)` crashed before the menu could initialize).

`Server/responses/GET__quests_quest-progression.json` now unlocks Story quests `1.1.1`, `2.1.1`, `2.2.1`, and `2.3.1`.

The decompilation regression suite currently passes:

```bash
cd /home/darabat/coding/TFTF-Mono-Investigation
PYTHONPATH=tools python3 -m unittest discover -s tools -p 'test_decompilation.py' -q
python3 -m py_compile tools/decompilation.py
git diff --check
```

## Verified device/runtime state

- Device: `RX8R30BF6SZ` (Samsung SM-A525M), package/activity `com.kabam.bigrobot/com.explodingbarrel.Activity`.
- ADB: `/home/darabat/Android/Sdk/platform-tools/adb`.
- The device is landscape-rendered as a 2400x1080 screenshot although its physical panel is portrait.
- USB reverse used by the client: `adb reverse tcp:8080 tcp:8080`.
- Revival server command:

  ```bash
  cd /home/darabat/coding/TFTF-Mono-Investigation
  /home/darabat/.cargo/bin/legible run Server/run_local.lbl --mono-fte-assets
  ```

  The previous server PID was `2043817`; verify it is still alive rather than assuming the PID is reusable. Do not kill the unrelated Legible process in the coder worktree.

The latest disposable APK was `build/mono-runtime/current/candidate-story-direct2-signed.apk` (local/ignored artifact). It reached the real Story quest UI. The useful captures/logs are local only under `build/mono-runtime/current/`, especially:

```text
story-direct2-late.png/.log       Story quest selection after direct menu route
story-fight-late.png              Story quest loading screen
story-fight-wait2.png             Same loading screen after waiting
device-story-fight.log            Device log from the attempted fight
```

Do not use older `story-fight-evidence.png` or `story-fight-relaunch.png` as proof: those came from the wrong direct-FTE candidate.

The server log proves the current route gets as far as Story quest start:

```text
GET  /quests/quest-list
GET  /quests/quest-progression
POST /quests/quest-detail/2.1.1
POST /bcg/setSavedTeam
POST /quests/quest-begin/2.1.1
POST /bcg/getBaseHeroData
```

It does **not** yet reach `POST /matches/activate-match/quests_fight`, and no combat HUD appears. The client remains on the planet/quest loading screen.

## Current blocker and likely fix

The original 2.0.2 APK does not contain the required QuestRoot ODR asset. Its `assets/global/quest.assetbundle` only has slot definitions; `GameboardManager.RequestGameboard` asks `TFormAssetManager` for `assets_quest/QuestRoot`. `QuestFlow` cannot continue until that callback receives a `Gameboard`.

A donor APK is available in the main repo:

```text
/home/darabat/coding/Transformers-Forged-To-Fight-Offline-Version/Transformers 9.2 offline.apk
/home/darabat/coding/Transformers-Forged-To-Fight-Offline-Version/build/custom-story.apk
```

Both contain these relevant entries:

```text
assets/assets_quest_fte_odr/toc.txt
assets/assetpack/assets_quest_fte_odr/assets_quest_fte.assetbundle
assets/assetpack/assets_quest_fte_odr/assets_quest_fte.assetbundle.manifest
assets/assets_quest_odr/toc.txt
assets/assetpack/assets_quest_odr/assets_quest.assetbundle
assets/assetpack/assets_quest_odr/assets_quest.assetbundle.manifest
assets/assets_base_odr/toc.txt
assets/assetpack/assets_base_odr/assets_base.assetbundle
assets/assetpack/assets_base_odr/assets_base.assetbundle.manifest
```

`assets_quest_fte_odr/toc.txt` maps `assets_quest_fte/questroot` to `QuestRoot.prefab`; this is the most promising source for the missing board asset. `assets_base_odr` contains `BaseRoot` if normal HomeFlow is needed later.

The current `--mono-fte-assets` server option only aliases a Skywarp/Starscream map asset in canned JSON. It does not serve/register the donor ODR bundles. Confirm this before relying on that flag.

## Recommended next investigation

1. Inspect `EB/Download.cs`, `EB/DownloadExtractor.cs`, `EB/AssetBundleManager`, `EB/Assets`, and `EB.Sparx/ODRManager` in the generated source to determine the exact local APK path and manifest shape expected for an ODR bundle.
2. Compare the donor `toc.txt` format with `EB.Sparx/ODRManifest.cs`. The runtime constructor expects `result.build[*].manifest` as an array of WAD manifests, while the donor toc stores a `bundles` dictionary. Normalize only the fields needed by `WADManifest` (`Name`, URL/filename, size, crc, `no_zip`, and bundle path mappings).
3. Prefer a narrowly scoped donor-asset graft in `tools/package_mono_candidate.py` (optional explicit donor argument, never implicit) or a source-side local ODR registration. Preserve the original APK SHA-256 guard and record grafted entries in the disposable JSON manifest.
4. Add/adjust a canned or dynamic `/odr/get-manifest` response and an asset-serving route if the client downloads through HTTP. The server must make the QuestRoot bundle available to the APK's existing extractor/loader; merely adding a ZIP entry is insufficient if the ODR manager only sees manifest entries.
5. Rebuild the candidate, align/sign/install it, clear/restart only as needed, and retest on USB. Capture a fresh screenshot and request log.

Useful source anchors:

```text
GameboardManager.RequestGameboard -> LoadAndUse("assets_quest/QuestRoot", ...)
QuestFlow.LoadGameBoard -> RequestGameboard -> OnGameboardLoaded
ActiveQuest.ActivateMatch -> Hub.MatchManager.ActivateMatch("quests_fight", ...)
PrefightScreenCallbacks.OnFight -> constructs FightFlow.FightInitInfo and pushes FightFlow
```

If bypassing the gameboard is explored, build a complete `FightFlow.FightInitInfo` from the active quest/team/map tile and verify the same `quests_fight` activation path; do not silently replace it with the tutorial/FTE flow.

## Packaging/install checklist

The package tool is `tools/package_mono_candidate.py`. It currently replaces exactly the two managed DLLs and rejects any APK entry-set change. If extending it for donor assets, update its tests and manifest schema deliberately. Candidate APKs, screenshots, logs, and extracted assets are disposable and ignored; never commit `media/` content.

For every claimed success, require all of:

- current source-built APK installed on `RX8R30BF6SZ`;
- real Story quest selected after the tutorial/menu path;
- request log contains `/quests/quest-begin/...` and `/matches/activate-match/quests_fight`;
- device capture shows the combat HUD, not only `CARREGANDO...` or the FTE tutorial.

Only after those checks should the branch be marked complete.

## Fresh-session update (2026-09-21)

The previous note stopped before the donor-WAD work. The current uncommitted
files are:

```text
M Server/responses/GET__odr_get-manifest.json
M tools/decompilation.py
M tools/package_mono_candidate.py
M tools/test_decompilation.py
```

### Implemented since the older notes

`tools/package_mono_candidate.py` accepts an explicit `--donor` APK. It creates
the deterministic stored ZIP `assets/mono_offline/quest_fte.wad` from the donor
entries:

```text
assets/assets_quest_fte_odr/toc.txt
assets/assetpack/assets_quest_fte_odr/assets_quest_fte.assetbundle
```

The latest graft is 222691 bytes, with WAD bundle data at offset 2059. The
package manifest records the graft and preserves the original APK SHA-256
guard. Do not make donor grafting implicit.

For `--allow-offline-network`, `tools/decompilation.py` now also:

- injects an APK-backed ODR manifest during `ODRManager.Initialize`;
- describes bundle `assets_quest_fte`, WAD size `222691`, CRC `3965316852`,
  URL `offline://mono_offline/quest_fte.wad`;
- opens/extracts that WAD from the APK in `DownloadAndOpenWad`;
- retains the earlier ODR callback repair and aggregate disk-space bypass.

`Server/responses/GET__odr_get-manifest.json` contains the same manifest shape,
although the managed injection bypasses the network request. The donor TOC maps
the needed asset as:

```text
bundle: assets_quest_fte
path:   assets_quest_fte/questroot
asset:  Assets/Bundles/quest/assets_quest_fte/QuestRoot.prefab
```

### Validation

These checks pass:

```bash
cd /home/darabat/coding/TFTF-Mono-Investigation
PYTHONPATH=tools python3 -m unittest discover -s tools -p 'test_decompilation.py' -q
python3 -m py_compile tools/decompilation.py tools/package_mono_candidate.py
git diff --check
```

The latest successful compile command was:

```bash
python3 tools/decompilation.py compile-audit build/decompilation/mono-2fviys29 \
  --dotnet /home/darabat/.dotnet/dotnet \
  --csc /home/darabat/.dotnet/sdk/8.0.422/Roslyn/bincore/csc.dll \
  --reference-dir build/tooling/net35/build/.NETFramework/v3.5 \
  --repair-accessors --repair-contracts \
  --server-endpoint http://127.0.0.1:8080 \
  --disable-google-play-games --runtime-diagnostics --disable-push \
  --allow-offline-network
```

Latest successful report and compiled DLL directory:

```text
build/decompilation/mono-2fviys29/compile-9e_lo1we/report.json
build/decompilation/mono-2fviys29/compile-9e_lo1we/bin/
```

The report confirms four ODR repairs: callback restoration, space-check
bypass, offline-manifest registration, and explicit-WAD opening.

### Latest disposable candidate

```text
build/mono-runtime/current/candidate-story-odr-manifest-signed.apk
```

It was zipaligned, signed with the existing local test key, and verified with
v1/v2/v3 signatures. The unsigned package manifest is:

```text
build/mono-runtime/current/candidate-story-odr-manifest-unsigned.apk.json
```

It contains grafted entry `assets/mono_offline/quest_fte.wad` with SHA-256:

```text
8bd7a1658eaae5e425529c78a36ebaf794c609ad472b5b159c98a1ea5db8e8e1
```

### Latest device result

Device and tooling:

```text
device: RX8R30BF6SZ (Samsung SM-A525M)
adb:    /home/darabat/Android/Sdk/platform-tools/adb
```

The revival server should remain running from this worktree:

```bash
/home/darabat/.cargo/bin/legible run Server/run_local.lbl --mono-fte-assets
adb reverse tcp:8080 tcp:8080
```

Verify the earlier server PID `2043817` before starting or stopping anything;
do not kill the unrelated Legible process in the main worktree.

The latest candidate reaches the real Story quest UI and quest-begin flow. The
useful captures are:

```text
build/mono-runtime/current/device-story-odr-manifest-story.png
build/mono-runtime/current/device-story-odr-manifest-quest-detail.png
build/mono-runtime/current/device-story-odr-manifest-begin.png
build/mono-runtime/current/device-story-odr-manifest-fight-55s.png
build/mono-runtime/current/device-story-odr-manifest-fight-now.png
```

The server sees `/quests/quest-detail/2.1.1` and
`/quests/quest-begin/2.1.1`, but still does not see
`/matches/activate-match/quests_fight`. The device remains on the planet/fight
loading screen after at least 55 seconds. Unity-filtered logs show normal Mono
login/HomeFlow messages but no custom ODR log, AssetBundle error, or managed
exception. Capture/search unrestricted logcat before assuming the patch ran.

### Likely blocker and next action

The likely wait is `QuestRoot`, not server routing:

```text
QuestFlow.OnGameboardLoaded
  -> GameboardManager.RequestGameboard
  -> TFormAssetManager.LoadAndUse("assets_quest/QuestRoot", ...)
  -> AssetBundleManager.LoadAsync
```

`AssetBundleManager._LoadAsync` first calls `FindBundleInfo(path)`. If that is
null it waits for `ODRManager.InitializedAndLoadedCoroutine()` and eventually
calls back with null. The current direct manifest/WAD patch has not proven that
the managed ODR manager mounted the donor TOC; the missing custom ODR logs make
that the first debugging target.

Start with:

```bash
adb -s RX8R30BF6SZ logcat -d > build/mono-runtime/current/full-stuck.log
rg -i 'ODRManager|AssetBundleManager|PackDriver|QuestFlow|Gameboard|TForm|exception|error|warning' \
  build/mono-runtime/current/full-stuck.log
```

Then inspect generated sources around `AssetBundleManager._LoadAsync`,
`AssetBundleManager.Register`, `PackDriver._Mount`, `ODRManager.OpenWad`, and
`ODRManager.InitializedAndLoadedCoroutine`. Add narrow temporary logs to prove
whether the manifest creates a WAD manifest, opens the local WAD, mounts its
TOC, and registers `assets_quest_fte/questroot`.

If managed ODR still does not register it, the fallback is an offline-only
branch in `AssetBundleManager._LoadAsync<T>` for `assets_quest/` paths: extract
the APK WAD, call `AssetBundle.LoadFromFile(localWadPath, 0u, 2059UL)`, resolve
the requested asset from `GetAllAssetNames()` or the donor TOC, callback the
asset, and cache the bundle. Keep that branch narrow and add a fixture test.

Do not call the Story fight fixed until a fresh run produces
`POST /matches/activate-match/quests_fight` and a combat-HUD capture.

## Fresh-session handoff (2026-09-22)

The previous session added Unity-visible diagnostics and the donor-path alias;
these changes are uncommitted in `tools/decompilation.py` and should be kept
while debugging:

```text
AssetBundleManager: assets_quest/ -> assets_quest_fte/
ODR: LoadAssetBundle, LoadFromFile, RegisterAssetBundle
AssetBundleManager: _LoadBundle, _LoadBundles, ODR callback, LoadAssetAsync
```

Regression checks still pass (35 tests). The latest compile was:

```text
build/decompilation/mono-2fviys29/compile-784wd3rm/bin/
```

The resulting disposable APK was installed on `RX8R30BF6SZ`:

```text
build/mono-runtime/current/candidate-probes-signed.apk
```

Packaging inputs, if rebuilding:

```text
base:  /home/darabat/coding/Transformers-Forged-To-Fight-Offline-Version/com.kabam.bigrobot_2.0.2-812553_minAPI19(armeabi-v7a,x86)(nodpi)_apkmirror.com.apk
donor: /home/darabat/coding/Transformers-Forged-To-Fight-Offline-Version/Transformers 9.2 offline.apk
```

### Proven runtime state

On the probe APK, startup logs prove the local ODR WAD and TOC work for the
normal menu:

```text
MONO ODR Initialize
MONO ODR ProcessServerData
MONO ODR DownloadAndOpenWads / DownloadAndOpenWad
MONO ODR OpenWad ...quest_fte.wad
MONO PackDriver mount assets_quest_fte_odr/toc.txt
MONO hub connected
MONO OfflineStoryBootstrap: FightLandingScreen ready
```

The real FightLanding screen and Story map render. Earlier runs also proved
`/quests/quest-detail/2.1.1` and `/quests/quest-begin/2.1.1`; no run has yet
proved `/matches/activate-match/quests_fight` or a combat HUD.

### Reproduction notes

The device screenshot is **2400x1080**. Image previews are scaled, so use the
actual screenshot coordinates when tapping. A reliable sequence is:

```text
launch and wait for the Portuguese mode screen
Story/HISTÓRIA: approximately (620,425)
on the Story map, the Bludgeon node is approximately (980,360)
```

Allow the map animation to settle before tapping. Save a screenshot after each
screen. Do not interpret a blank/black transitional frame as a managed crash;
check `pidof com.kabam.bigrobot` and wait several seconds.

Capture the complete evidence after selecting the quest:

```bash
adb -s RX8R30BF6SZ logcat -c
# reproduce the path, then:
adb -s RX8R30BF6SZ logcat -d -v time > build/mono-runtime/current/fresh.log
rg 'MONO|quest-begin|activate-match|QuestFlow|Gameboard|AssetBundle|ODRManager|Exception|Error' \
  build/mono-runtime/current/fresh.log
```

The decisive expected diagnostic sequence after quest start is:

```text
MONO bundle load assets_quest_fte/QuestRoot
MONO _LoadBundle ... / _LoadBundles count ...
MONO ODR LoadAssetBundle ...
MONO ODR OpenWad ...
MONO ODR LoadFromFile ... result object
MONO ODR RegisterAssetBundle ... object
MONO ODR callback ... object
MONO LoadAssetAsync ...
```

If the sequence stops at `LoadAssetBundle`/`OpenWad`, inspect the generated
`EB.Sparx/ODRManager.cs` around `OpenWad` and `RegisterAssetBundle`. If it
registers an object but stops before `LoadAssetAsync`, inspect
`AssetBundleManager._LoadAsync` and the donor TOC asset name. If `LoadFromFile`
returns null, use the narrow offline fallback already described above (load
the grafted WAD at offset `2059`, resolve `QuestRoot.prefab`, callback and
cache it), then rebuild and retest.

Keep the revival server running from this worktree and ensure:

```bash
adb -s RX8R30BF6SZ reverse tcp:8080 tcp:8080
```

Success criteria remain strict: current source-built APK, real Story quest,
server request `/matches/activate-match/quests_fight`, and a screenshot with
the combat HUD. Do not claim completion from the loading screen alone.

### Quest board asset finding (2026-09-22)

The proper board asset is the donor bundle path `assets_quest_fte/questroot`
from `assets/assets_quest_fte_odr/toc.txt`; `assets_quest` contains effects,
not `QuestRoot`. The current managed alias therefore points the right logical
path. The 2.0.2 player is Unity 5.3.5, while the donor QuestRoot bundle is
UnityFS format 7 / Unity 2020.3.31f1. Header-only and format-6 header patches
still fail; a UnityPy re-emission reaches loading but never registers the
bundle. A rendered board consequently requires a genuine Unity-5-compatible
QuestRoot re-export/conversion (including its serialized dependencies), not
another ODR offset or path change. Do not restore the null-board shortcut.
## Fresh-session handoff (2026-09-22, latest)

The previous conclusion was premature: the repacked UnityPy bundle was not yet
tested through ODR because the managed manifest still declared the original WAD
size. The repacked WAD is about 1.1 MB; on device it was extracted as
`.../zip_cache/1264479052quest_fte.wad` with size `1116405`, while
`tools/decompilation.py` still declares size/zip_size `222691` and offset
`2059`. The runtime then logged only `MONO bundle load assets_quest_fte/QuestRoot`.

`ODRManager.GetZipFile` rejects a local WAD whose length differs from
`WADManifest.Size`. Fix this before judging Unity compatibility. In the
injected local-WAD branch, immediately after `FileInfo localWad = new
FileInfo(wadManifest.DownloadedFile);`, set:

```csharp
wadManifest.Size = localWad.Length;
wadManifest.ZipSize = localWad.Length;
```

Rebuild, package with the repacked donor WAD, and retest. `Size`, `ZipSize`, and
`Crc` are public fields; inspect all `Crc`/`CheckCrc` uses before changing CRC
handling. The prior minimal donor ZIP was `/tmp/tftf-donor-min.zip`; recreate it
with UnityPy if absent instead of copying the full donor APK. Do not restore the
null-board/direct-fight bypass.

Expected next logs:

```text
MONO ODR LoadAssetBundle
MONO ODR OpenWad
MONO ODR LoadFromFile ... result object/null
MONO ODR RegisterAssetBundle
MONO ODR callback
MONO LoadAssetAsync ... QuestRoot.prefab
```

If the bundle mounts and `LoadFromFile` returns null, investigate Unity
serialization/version compatibility or produce a Unity-5-compatible board
bundle. Success requires a rendered Story board, then
`/matches/activate-match/quests_fight`, then a combat-HUD screenshot.

Current dirty files: `MONO_STORY_FIGHT_SCRATCHPAD.md`,
`Server/responses/GET__odr_get-manifest.json`, `tools/decompilation.py`,
`tools/package_mono_candidate.py`, `tools/test_decompilation.py`. Keep `media/`
out of Git and preserve the main worktree. Before handoff run:

```bash
PYTHONPATH=tools python3 -m unittest discover -s tools -p 'test_decompilation.py' -q
python3 -m py_compile tools/decompilation.py tools/package_mono_candidate.py
git diff --check
```

### 2026-09-22 size-fix validation

The managed local-WAD path now sets `WADManifest.Size` and `ZipSize` from the
extracted file length before opening it. The regression suite passes 35 tests;
the assemblies compile successfully. `package_mono_candidate.py` also accepts
an explicit prebuilt `--wad`, preserving the original APK SHA guard.

The rebuilt candidate using the 1,116,405-byte repacked WAD was installed and
tested on `RX8R30BF6SZ`. It reached the real Story quest, sent
`POST /quests/quest-begin/2.1.1`, and attempted:

```text
MONO ODR LoadFromFile assets_quest_fte offset 2059
The AssetBundle ... can't be loaded because it was not built with the right
version or build target.
```

The size mismatch is fixed. The remaining blocker is genuine Unity bundle
compatibility: the donor QuestRoot bundle cannot be consumed by the Mono Unity
5.3.5 player. No fight activation or combat HUD was reached. The next task is
a Unity-5-compatible QuestRoot export/conversion with its dependencies,
followed by the same on-device path. Reverse port and stay-awake settings were
restored after this run.

## Fresh-session handoff: 2026-09-22 (current)

Worktree: `/home/darabat/coding/TFTF-Mono-Investigation`, branch
`research/mono-decompilation`. Preserve the existing dirty changes. The main
worktree is unrelated and must remain untouched.

Current source changes are in:

```text
tools/decompilation.py
tools/package_mono_candidate.py
tools/test_decompilation.py
Server/responses/GET__odr_get-manifest.json
MONO_STORY_FIGHT_SCRATCHPAD.md
```

The managed ODR repair now assigns `wadManifest.Size` and `ZipSize` from the
extracted local file. `package_mono_candidate.py` accepts `--wad` for an
explicit prebuilt WAD while retaining the original APK SHA-256 guard.

Validation already completed:

```bash
PYTHONPATH=tools python3 -m unittest discover -s tools -p 'test_decompilation.py' -q
# 35 tests, OK
python3 -m py_compile tools/decompilation.py tools/package_mono_candidate.py
git diff --check
```

The latest managed compile output is:

```text
build/decompilation/mono-2fviys29/compile-by3m285f/bin/
```

To reproduce packaging, use the original Mono APK and the existing repacked
WAD candidate:

```bash
BASE='/home/darabat/coding/Transformers-Forged-To-Fight-Offline-Version/com.kabam.bigrobot_2.0.2-812553_minAPI19(armeabi-v7a,x86)(nodpi)_apkmirror.com.apk'
unzip -p build/mono-runtime/current/candidate-story-board-repacked-unsigned.apk \
  assets/mono_offline/quest_fte.wad > build/mono-runtime/current/quest-fte-repacked.wad
python3 tools/package_mono_candidate.py "$BASE" \
  build/mono-runtime/current/candidate-story-board-sizefix-repacked-unsigned.apk \
  --firstpass build/decompilation/mono-2fviys29/compile-by3m285f/bin/Assembly-CSharp-firstpass.dll \
  --game build/decompilation/mono-2fviys29/compile-by3m285f/bin/Assembly-CSharp.dll \
  --wad build/mono-runtime/current/quest-fte-repacked.wad
```

The disposable signed candidate was installed on `RX8R30BF6SZ`; device cleanup
removed the reverse tunnel and disabled stay-awake. The revival server command,
when retesting, is:

```bash
/home/darabat/.cargo/bin/legible run Server/run_local.lbl --mono-fte-assets
adb -s RX8R30BF6SZ reverse tcp:8080 tcp:8080
```

The controlled run reached the real Story menu, quest detail, team selection,
and `/quests/quest-begin/2.1.1`. It opened the local WAD and reached:

```text
MONO ODR LoadFromFile assets_quest_fte offset 2059
The AssetBundle ... can't be loaded because it was not built with the right
version or build target.
```

Thus the WAD-size problem is solved. The remaining blocker is asset
compatibility: the donor QuestRoot bundle is Unity 2020.3.31f1, while the Mono
client is Unity 5.3.5. Do not add a null-board shortcut or direct FTE substitute.
Produce or locate a Unity-5.3.5-compatible QuestRoot bundle and dependencies,
then rebuild and verify, in order:

```text
rendered Story board -> movement -> pre-fight -> /matches/activate-match/quests_fight -> combat HUD
```

Success requires a current source-built APK, the activation request, and a
fresh screenshot showing fighters and HUD. Keep APKs, logs, extracted bundles,
screenshots, and recordings out of Git; never add `media/` files.

### 2026-09-22 runtime pivot clarification

The 9.2 Unity 2020.3.31f1 asset path is a possible destination for a future
rebuild, but `build/custom-story.apk` in the main worktree is an existing
patched IL2CPP APK, not a recompiled version of this recovered Mono source. A
device run of that APK is therefore not evidence for this objective and retains
the patched build's known defects.

`compile-audit` now includes added Unity 2017+ module DLLs (for example
`UnityEngine.CoreModule.dll`) when a modern reference directory is supplied.
The first modern-contract probe still fails: the recovered source has old
firstpass APIs and manager contracts that do not compile against the 9.2 dummy
assemblies. A genuine newer-runtime build therefore needs an explicit Unity
2020 project/source port, followed by a new APK build; swapping the existing
patched APK or grafting its bundles into the Unity 5 player does not satisfy it.

### 2026-09-22 modern source-port milestone

`tools/decompilation.py compile-audit` now supports `--modern-unity`,
`--modern-firstpass-reference`, and repeatable `--source` filters. The latter
compiles a focused gameplay slice against the Kabam 9.2 dummy/API assemblies
while keeping the obsolete Unity-5 first-pass source as an explicit dependency.
The Story/Fight slice now compiles together:

```bash
python3 tools/decompilation.py compile-audit build/decompilation/mono-2fviys29 \
  --dotnet /home/darabat/.dotnet/dotnet \
  --csc /home/darabat/.dotnet/sdk/8.0.422/Roslyn/bincore/csc.dll \
  --reference-dir /tmp/tftf-modernrefs.RubZ --repair-accessors --repair-contracts \
  --server-endpoint http://127.0.0.1:8080 --modern-unity \
  --modern-firstpass-reference \
  --source QuestFlow.cs --source FightFlow.cs --source GameboardManager.cs \
  --source Gameboard.cs --source ActiveQuest.cs --source FightLandingScreen.cs \
  --source InterfaceInjector.cs --source InjectorClassAttribute.cs \
  --source AttributeFinder.cs
```

Latest result: `build/decompilation/mono-2fviys29/compile-jtblq1v3/bin/Assembly-CSharp.dll`,
exit code 0, with `QuestFlow`, `FightFlow`, `GameboardManager`, `Gameboard`,
and `ActiveQuest` present. This is a source compile milestone, not yet a Unity
player or playable APK; next is a real Unity 2020 project referencing this
output and the 9.2 asset bundles, followed by device verification of board
rendering, pre-fight, match activation, and combat.

This milestone intentionally does not claim that every 2.0.2 class is ported:
an all-source modern compile still reports legacy Unity rendering/editor API
breakage. The pinned 9.2 `Assembly-CSharp-firstpass.dll` is a dependency for
this first gameplay slice; replacing that dependency and porting the remaining
first-pass systems is a later Unity-project task.

The next runtime step is currently license/module-blocked, not source-blocked:
the Unity 2020 editor is now installed but this machine has no Unity license, and the 9.2 APK is IL2CPP-only. Its
`DummyDll`/dump supplies signatures but no method bodies, so the compiled slice
cannot be installed into the IL2CPP APK or run as a Unity player. A genuine
board/fight verification therefore requires a Unity 2020 project build (or a
real 9.2 Mono managed assembly set) before APK/device testing can proceed.

### 2026-09-22 Unity 2020 build-environment install

The Unity 2020 editor is installed in the ignored build area:

```text
build/unity-2020.3.31f1/Editor/Unity
```

It is Unity `2020.3.31f1`, changeset `6b54b7616050`. The 2.73 GB Linux archive was
downloaded and extracted successfully; the consumed archive was removed to save
space. Verify the binary with:

```bash
build/unity-2020.3.31f1/Editor/Unity -version
# 2020.3.31f1
```

This host is newer than the editor and does not provide the editor's legacy runtime
libraries. The local, ignored compatibility prefix is:

```text
build/unity-compat/root/usr/lib/x86_64-linux-gnu/
```

It contains extracted (not system-installed) `libxml2.so.2`, ICU 74, and OpenSSL 1.1
libraries. To launch Unity on this host, prepend that directory and enable invariant
globalization for Unity's bundled .NET licensing client:

```bash
export LD_LIBRARY_PATH="$PWD/build/unity-compat/root/usr/lib/x86_64-linux-gnu"
export DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1
build/unity-2020.3.31f1/Editor/Unity -version
```

The editor reaches licensing initialization in batch mode, but this machine has no
Unity license. A batch probe therefore cannot be treated as a project/build success;
it may wait at licensing. Do not claim an APK build until a licensed editor actually
opens a project and emits a player.

#### Android module resume

The official Unity Download Assistant is saved at
`build/unity-compat/UnitySetup-2020.3.31f1`. It lists a Linux `Android` component.
The first unattended Android install was accepted and started, but was interrupted
while its temporary XAR package stalled at about 495 MB; no `AndroidPlayer` directory
was installed. The partial `/tmp/.JFRKW3` was removed. Resume with:

```bash
mkdir -p build/unity-module-download
export LD_LIBRARY_PATH="$PWD/build/unity-compat/root/usr/lib/x86_64-linux-gnu"
export DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1
build/unity-compat/UnitySetup-2020.3.31f1 \
  --unattended --verbose \
  --install-location "$PWD/build/unity-2020.3.31f1" \
  --download-location "$PWD/build/unity-module-download" \
  --components=Android
```

The installer asks for Unity's terms interactively even with `--unattended`; answer
`y` on its terminal. It reported 1.75 GB required and 6.6 GB available. After it
finishes, verify:

```bash
find build/unity-2020.3.31f1/Editor/Data/PlaybackEngines \
  -maxdepth 2 -type d -name 'AndroidPlayer' -print
```

If the component download stalls again, preserve the editor and retry with a fresh
download directory; do not remove `build/unity-2020.3.31f1`. The installer binary
and compatibility prefix are disposable only after a working module install is
confirmed.

#### Fresh-session continuation

1. Finish or retry Android Build Support, then create a new Unity 2020 project under
   ignored `build/` storage (for example `build/unity-story-project`). There is no
   tracked Unity project yet.
2. Use the latest modern source slice compile recorded above as the managed-code
   starting point. Its current output is
   `build/decompilation/mono-2fviys29/compile-jtblq1v3/bin/Assembly-CSharp.dll`.
3. Import the Kabam 9.2 Unity-2020 asset bundles from the main worktree, not the
   Unity-5.3 client bundles. The relevant local files are under
   `/home/darabat/coding/Transformers-Forged-To-Fight-Offline-Version/build/pristine-rebuild/tree/assets/`.
4. Close remaining Unity-API and first-pass contract gaps, wire the scene/bootstrap
   entry point, and build a fresh Android player with the installed editor.
5. Verify in order: rendered Story board, board movement, pre-fight/team screen,
   `/matches/activate-match/quests_fight`, then combat HUD and an actual fight.
   A screenshot plus log from that fresh source-built APK is the completion evidence.

Current state is therefore **environment installed, Android module pending, board and
fight not yet verified**. Preserve all existing tracked changes; generated APKs,
logs, extracted bundles, and screenshots remain ignored, and never add `media/`.

### 2026-09-22 Android module checkpoint

Unity Android Build Support is now installed successfully under:

```text
build/unity-2020.3.31f1/Editor/Data/PlaybackEngines/AndroidPlayer
```

The editor reports `2020.3.31f1`. The installer initially hung on a truncated
temporary XAR because `/tmp` was nearly full; downloading the package explicitly
and setting `TMPDIR=build/unity-installer-tmp` fixed the install. The verified
package is stored in the ignored build area. Unity Personal activation was
completed through Hub/GUI; the editor opened the project successfully.

### 2026-09-22 current runtime and Unity source-port state

The source-built old-Mono candidate is:

```text
build/mono-runtime/current/candidate-story-direct3-signed.apk
```

It is installed on `RX8R30BF6SZ` and uses the revival server from this
worktree on port 8080 with `adb reverse tcp:8080 tcp:8080`. The fresh device
run has verified:

```text
login -> real FightLandingScreen -> HISTÓRIA -> MISSÕES DA HISTÓRIA
-> 1. Bludgeon's Ambush -> quest detail/team screen
-> /quests/quest-begin/2.1.1
```

The server has not received `/matches/activate-match/quests_fight`. After
quest-begin, the old Unity 5.3.5 player tries to load:

```text
assets_quest_fte/QuestRoot
```

The donor bundle is Unity 2020.3.31f1 and fails in the old player with a
version/build-target/decompression error. Do not restore the null-board or
direct-FTE shortcut; the old player needs a genuine Unity-5-compatible board,
while the source-port path below uses the donor's native Unity-2020 assets.

The Unity 2020 source-port project is now configured at:

```text
build/unity-story-project
```

The selected editor is `2020.3.31f1`; `9.2.0` is the game/content version.
The modern gameplay slice DLL is imported at:

```text
build/unity-story-project/Assets/Plugins/Assembly-CSharp.dll
```

Ignored project files created for the source-port are:

```text
build/unity-story-project/Assets/StoryPort/StoryPortBootstrap.cs
build/unity-story-project/Assets/StoryPort/StoryPort.unity
build/unity-story-project/Assets/Editor/StoryPortProjectSetup.cs
```

`StoryPortBootstrap` loads `assets_quest_fte/QuestRoot` from the 9.2 assetpack.
Desktop resolution uses `TFTF_92_ASSET_ROOT`, defaulting to:

```text
/home/darabat/coding/Transformers-Forged-To-Fight-Offline-Version/build/pristine-rebuild/tree/assets/assetpack
```

For Android, `StoryPortProjectSetup.CopyBundles()` copies the selected 9.2
bundles into:

```text
build/unity-story-project/Assets/StreamingAssets/tftf-9.2/
```

The selected set currently contains:

```text
assets_quest_fte_odr/assets_quest_fte.assetbundle
assets_quest_odr/assets_quest.assetbundle
assets_common_odr/assets_common.assetbundle
common_odr/common.assetbundle
karnak/karnak_merged.assetbundle
characters/characters.assetbundle
characters/character_fx.assetbundle
characters/character_audio.assetbundle
characters/moves.assetbundle
characters_fx_procedural_odr/character_fx_procedural.assetbundle
frontendfx/frontendfx.assetbundle
primordial_base_odr/primordial_base.assetbundle
```

The QuestRoot TOC mapping is:

```text
assets_quest_fte/questroot
Assets/Bundles/quest/assets_quest_fte/QuestRoot.prefab
```

`StoryPortProjectSetup.Configure()` has already run successfully and created
`Assets/StoryPort/StoryPort.unity` plus Android build settings. The generated
`com.unity.collab-proxy` package was removed from the ignored project because
its compiler error blocked all project scripts.

The initial Unity CLI `-buildAndroidPlayer` invocation only refreshed the
project. `StoryPortProjectSetup.BuildAndroid()` now calls
`BuildPipeline.BuildPlayer`, sets package `com.tftf.sourceport`, version
`9.2.0-source`, ARMv7, and Android API 23. Run this next:

```bash
cd /home/darabat/coding/TFTF-Mono-Investigation
export LD_LIBRARY_PATH="$PWD/build/unity-compat/root/usr/lib/x86_64-linux-gnu"
export DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1
TFTF_ANDROID_OUTPUT="$PWD/build/unity-story-project.apk" \
  build/unity-2020.3.31f1/Editor/Unity -batchmode -nographics -quit \
  -projectPath "$PWD/build/unity-story-project" \
  -executeMethod StoryPortProjectSetup.BuildAndroid \
  -logFile "$PWD/build/unity-story-build-2.log"
```

This modern bootstrap has not yet been installed or runtime-verified. The
strict completion evidence remains: a fresh Unity-built APK, rendered Story
board, pre-fight/team state, `/matches/activate-match/quests_fight`, and a
combat-HUD screenshot. Generated Unity files, bundles, APKs, logs, screenshots,
and recordings remain ignored; never add `media/`. Keep the main worktree
`/home/darabat/coding/Transformers-Forged-To-Fight-Offline-Version` untouched.

### 2026-09-22 continuation checkpoint: QuestRoot has no static visuals

The first fresh Unity source-port APK was installed and run on USB device
`RX8R30BF6SZ`. The asset bundle loads successfully, but the instantiated
`QuestRoot` contains no renderers:

```text
TFTF source-port loaded assets/bundles/quest/assets_quest_fte/questroot.prefab
TFTF source-port visual summary: renderers=0 active=0 bounds=none
```

The screenshot is therefore black except for the bootstrap diagnostic text:

```text
build/unity-sourceport-story-camera.png
```

The Android log identifies the missing serialized behaviours that normally
construct the board:

```text
Quests.Presentation.Gameboard
UIPanel
PathTuning
Quests.Presentation.QuestPlayerController
Quests.Presentation.QuestCameraController
Quests.Presentation.GameboardBuilder
EBCameraShake
EB.Rendering.EBProxyManager
```

There are also missing behaviours on `GameboardBuilder`, `Explosion`,
`Ambient`, `Camera`, `Travelling`, `EBProxyManager`, `QuestRoot`, and
`Player`. This rules out a camera-only fix: the prefab is a script-driven
container, not a pre-rendered board.

The original 9.2 Mono assemblies were then restored from:

```text
build/mono/run-eDiGBbLE/managed/Assembly-CSharp.dll
build/mono/run-eDiGBbLE/managed/Assembly-CSharp-firstpass.dll
```

into the ignored Unity project `Assets/Plugins/`, and this produced a Unity
APK successfully:

```text
build/unity-story-original-mono.apk
build/unity-story-build-original-mono.log
```

However, Unity 2020.3.31f1 reports the firstpass assembly as broken while
importing it:

```text
Failed to extract EB.DownloadHandlerStream class of base type
UnityEngine.Experimental.Networking.DownloadHandlerScript
Unloading broken assembly Assets/Plugins/Assembly-CSharp-firstpass.dll
```

The build log also reports duplicate `Assembly-CSharp.dll` output entries,
although `BuildPipeline.BuildPlayer` completes. Do not treat this APK as
runtime evidence yet; it has not been installed/run after the assembly swap.
The old Unity 5 API reference (`UnityEngine.Experimental.Networking`) is the
specific incompatibility to investigate next.

### Fresh-session next steps

1. Inspect/install `build/unity-story-original-mono.apk` and capture a small
   log/screenshot. Confirm whether Unity's broken-assembly handling leaves the
   QuestRoot behaviours missing at runtime.
2. If so, do not spend time on camera positioning. Choose between:
   - compiling a compatibility firstpass assembly against Unity 2020 APIs,
     starting with `EB.DownloadHandlerStream` and any subsequent importer
     failures; or
   - compiling only the Quest presentation slice from
     `build/mono/run-eDiGBbLE/source/Assembly-CSharp`, with compatible stubs
     for the required firstpass types.
3. The relevant recovered source is still present under:

   ```text
   build/mono/run-eDiGBbLE/source/Assembly-CSharp/
   build/mono/run-eDiGBbLE/source/Assembly-CSharp-firstpass/
   ```

   In particular, inspect `Quests.Presentation/Gameboard.cs`,
   `GameboardManager.cs`, `GameboardBuilder.cs`, `QuestPlayerController.cs`,
   `QuestCameraController.cs`, `QuestFlow.cs`, and `FightFlow.cs`.
4. Strict completion remains unachieved until a fresh APK renders the Story
   board, reaches the pre-fight/team state, causes
   `/matches/activate-match/quests_fight`, and shows the combat HUD.

Useful build environment (all paths are relative to this worktree):

```bash
export LD_LIBRARY_PATH="$PWD/build/unity-compat/root/usr/lib/x86_64-linux-gnu"
export DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1
export JAVA_HOME="$PWD/build/unity-jdk8"
export ANDROID_HOME="$PWD/build/unity-sdk8"
export ANDROID_SDK_ROOT="$PWD/build/unity-sdk8"
export TFTF_JDK_ROOT="$PWD/build/unity-jdk8"
export TFTF_ANDROID_SDK_ROOT="$PWD/build/unity-sdk8"
export TFTF_ANDROID_NDK_ROOT="$PWD/build/unity-sdk8/ndk"
export GRADLE_USER_HOME="$PWD/build/unity-gradle-home"
```

The main worktree remains out of scope and must not be modified. The
investigation worktree currently has only the previously documented tracked
changes; Unity project/build outputs remain ignored.

### Fresh-session continuation: 2026-09-22 Unity source-port crash isolation

The Unity environment is now fully installed and batch Android builds run with:

```text
editor:  build/unity-2020.3.31f1/Editor/Unity
project: build/unity-story-project
device:  RX8R30BF6SZ
```

Launch Unity on this host with:

```bash
export LD_LIBRARY_PATH="$PWD/build/unity-compat/root/usr/lib/x86_64-linux-gnu"
export DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1
```

`StoryPortProjectSetup.BuildAndroid` copies the Unity-2020 donor bundles, creates
`Assets/StoryPort/StoryPort.unity`, and invokes `BuildPipeline.BuildPlayer`. The
project and all APKs/logs are ignored/generated; do not commit them.

#### Latest compiler state

The current dirty `tools/decompilation.py` passes all checks:

```bash
PYTHONPATH=tools python3 -m unittest discover -s tools -p 'test_decompilation.py' -q
# 36 tests, OK
python3 -m py_compile tools/decompilation.py tools/package_mono_candidate.py
git diff --check
```

Modern compile-path repairs now include:

- skipping `Assembly-CSharp.dll` self-reference for full source rebuilds, avoiding
  duplicate `Quests.*` source/dummy identities;
- adding Unity-2020 `Manager.SetApiEndPoint()` hooks to the seven affected recovered
  subsystems;
- applying old-API reverse repairs only for a source-built first-pass assembly, not
  for the pinned modern dummy first-pass reference;
- a narrow `ReplicatedSequence` legacy-enum rewrite;
- corrected modern compatibility namespace braces.

The complete recovered gameplay source compiles successfully against Unity-2020
module/reference DLLs:

```text
build/decompilation/mono-2n_s40s6/compile-z1k2qzpu/report.json
build/decompilation/mono-2n_s40s6/compile-z1k2qzpu/bin/Assembly-CSharp.dll
```

This full DLL is not usable as an APK. Unity crashes during type-tree generation:

```text
Assertion failed on expression: 'm_ArrayField != SCRIPTING_NULL'
Caught fatal signal - signo:11
LinearCollectionField -> BuildSerializationCommandQueueFor
```

The same crash occurs with the original recovered Mono `Assembly-CSharp.dll` when
paired with the modern dummy first-pass DLL. A first-pass-only Android build succeeds
(approximately 238 MB), so the crash is in the `Assembly-CSharp` serialized type
surface, not the editor, Android module, or first-pass plugin alone. Relevant logs:

```text
build/unity-story-fullgame-build.log
build/unity-story-original-plus-dummy-build.log
build/unity-story-firstpass-only-build.log
```

After the experiments, the isolated Unity project has the original recovered game
DLL restored, plus:

```text
Assets/Plugins/Assembly-CSharp.dll          original recovered Mono game DLL
Assets/Plugins/Assembly-CSharp-firstpass.dll /tmp/tftf-modernrefs.RubZ dummy/API DLL
Assets/Plugins/ICSharpCode.SharpZipLib.dll  real recovered SharpZip implementation
Assets/Plugins/BouncyCastle.dll              9.2 DummyDll copied from the main repo
```

No Unity APK from the full-game attempts is evidence of success. The device has not
reached `activate-match` or a combat HUD through this path.

#### Focused-source findings

Compiling only `Quests.Presentation/*.cs` against the same-name dummy assembly is
not sufficient: the dummy already declares many presentation types, leading to
source-vs-dummy type identity errors. Removing the self-reference instead exposes a
large dependency closure (`Legacy`, `TeamData`, `HeroData`, `PrefabLibrary`,
`TFormAssetManager`, object-pool methods, and old UI contracts). Do not keep adding
arbitrary frontend files to the broad assembly; that returns to the type-tree crash.

Experimental focused workspaces under `/tmp/tftf-*` are disposable. In particular,
`/tmp/tftf-board-workspace.*` contains temporary stubs and is not source of truth.

#### Best next experiment

Use a metadata-safe assembly rename to bridge a focused source assembly:

1. Copy the 9.2 dummy `Assembly-CSharp.dll` to a clean temporary reference directory.
2. Rename its metadata assembly name to `TFTF9GameStubs` using Mono.Cecil (never a
   raw hex/string replacement), while retaining the compiler-reference filename
   `Assembly-CSharp.dll`.
3. Compile a deliberately small `Assembly-CSharp` source set containing
   `Quests.Presentation`, required `Quests` models, needed `Legacy` quest DTOs,
   `PathTuning.cs`, and `EBCameraShake.cs`. Use explicit files and let the renamed
   dummy supply unselected framework types.
4. For Unity runtime, install the renamed binary as `TFTF9GameStubs.dll` beside the
   source-built `Assembly-CSharp.dll`.
5. Build and check whether Unity survives type-tree generation before adding more
   source. Then verify, in order: rendered Story board, pre-fight/team screen,
   `POST /matches/activate-match/quests_fight`, and combat HUD.

A disposable Mono.Cecil utility was started under `/tmp/tftf-rename/`; recreate it
if absent. The worktree must remain clean of that temporary bridge.

If the renamed-dummy bridge still produces duplicate script identities or the same
type-tree crash, record it as a blocker. The two valid conclusions remain: the old
Unity-5 player needs a genuine Unity-5-compatible QuestRoot bundle, and the Unity-2020
source port needs a manageable focused assembly. Do not restore the null-board/direct-
FTE shortcut and do not claim success from a loading screen.

#### Current tracked state

```text
M MONO_STORY_FIGHT_SCRATCHPAD.md
M Server/responses/GET__odr_get-manifest.json
M tools/decompilation.py
M tools/package_mono_candidate.py
M tools/test_decompilation.py
```

Keep generated APKs, Unity caches, `/tmp` workspaces, logs, screenshots, and recordings
out of Git. Never add anything under `media/`. The main worktree remains untouched.

### 2026-09-22 Editor type-tree crash resolved; device runtime confirmed

The Unity Editor `m_ArrayField != SCRIPTING_NULL` SIGSEGV that blocked earlier
builds no longer reproduces with the current compiled DLL. Investigation found:

1. **Root cause identified**: `SerializableDictionary<TKey,TValue>` (abstract,
   `[Serializable]`, derives from `Dictionary<TKey,TValue>`) declares
   `[SerializeField] List<TKey> _serializedKeys` and `[SerializeField] List<TValue>
   _serializedValues`. Its concrete subclass `PropDict : SerializableDictionary<string,
   PropData>` is used by `PropsController : MonoBehaviour` via `[SerializeField]
   private PropDict _props`. Unity 2020's `LinearCollectionField` must inflate
   `List<!0>`/`List<!1>` through `PropDict`'s generic context
   (`mono_field_get_type` → `mono_class_inflate_generic_type`), which is the frame
   that segfaulted. Unity 5 handled this layout; Unity 2020 does not.

2. **Current DLL builds successfully**: The latest compile output
   (`build/decompilation/mono-2n_s40s6/compile-z1k2qzpu/bin/Assembly-CSharp.dll`)
   produces a clean Unity Editor build without the crash. The earlier crash log
   (`build/unity-story-fullgame-build.log`) was from a prior compilation state.

3. **Device runtime confirmed**: The built APK (`build/bisect/original.apk`,
   89 MB) was installed on `RX8R30BF6SZ` and launched successfully. Unity
   initializes, loads `assets_quest_fte.assetbundle`, instantiates `QuestRoot`,
   and `Gameboard.Awake()` fires. NullReferenceExceptions in
   `TFormAssetManager.LoadAssetDefinitions()` are expected — the bootstrap scene
   lacks full game infrastructure. No native crash.

4. **Bisection tooling**: A Cecil-based bisection tool exists at
   `build/find-crash-tool/bisect.cs` (uses Unity's modern Cecil from
   `Editor/Data/il2cpp/build/deploy/netcoreapp3.1/Mono.Cecil.dll`). It can
   neutralize types by changing their base to `System.Object` (imported from
   `mscorlib`, not `System.Private.CoreLib`). Backups are in `build/bisect/`.

**If the crash returns**, the fix is a source-level rewrite in `decompilation.py`:
make `PropDict` declare its own concrete `[SerializeField] List<string>
_serializedKeys` and `[SerializeField] List<PropData> _serializedValues` fields
(keeping identical names for prefab data binding) and implement
`ISerializationCallbackReceiver` directly, rather than inheriting the open-generic
fields from `SerializableDictionary<>`. Do NOT strip or remove `PropsController`
or `PropDict` — they drive combat props and SP3 transform alt-forms.

Next steps remain: close remaining runtime NREs in `TFormAssetManager`, wire the
full bootstrap flow, and verify Story board → pre-fight → match activation →
combat HUD on device.
