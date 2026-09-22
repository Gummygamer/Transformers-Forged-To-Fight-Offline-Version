# Mono Story-fight scratchpad

This is a handoff for continuing the recompiled Mono client investigation from a fresh session. It belongs to the dedicated worktree/branch, not the unrelated `development` worktree.

## Target

Reach a real Story-mode fight on the USB-connected Android device. The desired path is:

`login -> FightLandingScreen -> Story -> Story quest -> quest-begin -> FightFlow/activate-match -> combat HUD`

This is specifically the post-tutorial Story menu; the FTE/tutorial fight is not success.

## Worktree and branch

```text
worktree: /home/darabat/coding/TFTF-Mono-Investigation
branch:   research/mono-decompilation
HEAD:     6c0738b Route Mono login to real Story fight menu
```

The branch was clean when this note was written. Keep the main worktree (`/home/darabat/coding/Transformers-Forged-To-Fight-Offline-Version`) untouched because it contains unrelated user changes.

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
