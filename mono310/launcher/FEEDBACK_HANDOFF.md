# Player feedback handoff (launcher 0.13 test build)

Status on 2026-10-31. This document lets a fresh session continue the work on the player feedback for the launcher.

**Not fixed:** invisible bullets, missing held objects and screen static. Their causes are unknown. The fixes below are committed
in `78c6d56` and pushed. **Do not change SP3 attacks:** the reporter says they have no issues.

Marks follow [`DOC_STYLE.md`](../../DOC_STYLE.md).

## Table of contents

- [Report status](#report-status)
- [Start here](#start-here)
- [Open: invisible bullets](#open-invisible-bullets)
- [Open: missing held objects](#open-missing-held-objects)
- [Open: screen static](#open-screen-static)
- [Committed fixes](#committed-fixes)
- [Other things to do before release](#other-things-to-do-before-release)
- [Build](#build)
- [Install without the APK picker](#install-without-the-apk-picker)
- [Drive the phone safely](#drive-the-phone-safely)
- [Dead ends](#dead-ends)

## Report status

| Report | Status | Evidence |
|---|---|---|
| Raw text such as `ms_id_fight_ko` | Fixed, committed `78c6d56` | Portuguese menus through the Arena showed no raw ids. **[👁 live]** |
| Arena can only be entered once | Fixed, committed `78c6d56` | `ENTRAR` opened the opponent screen after a full series, on the build before the final one. The final build only had a launch check. **[👁 live]** |
| No sound effects outside fights | Fixed, committed `78c6d56` | The user heard them on the A52. **[👁 live]** |
| Bullets not visible when shooting | **Open** | The user believes it affects all robots. Not reproduced yet. |
| Some bots lack objects in special attacks | **Open** | The user does not know which bots. |
| Screen static when entering somewhere | **Open** | Unconfirmed leads only. |

The reporter's screenshot of the raw text never reached us. The reporter wrote `ms_id_fight_ko`, but the table key is
`ID_UI_FIGHT_KO`. **[📄 binary]**

## Start here

1. Run `git status`. The fixes in [Committed fixes](#committed-fixes) are already in `78c6d56`.
2. Read `ABILITY_AUTHORING.md` before touching `statMods`, `buffs_set` or `stat_mods`, as `AGENTS.md` requires.
3. Check the phone with `adb devices`. It should be the SM-A525M with the final test build installed.
4. Start with bullets: they are reported as universal, so they should be the easiest to reproduce.

## Open: invisible bullets

Nothing has been reproduced. In the 100 s recording of an Arena fight, neither fighter fired. **[👁 live]**

The first job is to make a fighter shoot and record it:

1. `tftfbot.py fight` has no ranged action, and its action list is in `tools/tftfbot.py`. **[📄 binary]** Either add one
   in `shell/TftfMain/TftfBot.cs` or fire by touch on the phone.
2. Record with `adb shell screenrecord`, then step through the frames with `ffmpeg`. Delete the recording from the phone
   afterwards, and keep it out of Git: `media/` and captures are local-only.

Where to look, in the decompiled sources under `ShellProject/Assets/Scripts/Assembly-CSharp`:

- `ProjectileMoveEvent.cs` spawns from `PrefabName` through `ObjectPoolManager.GetPool` in `PrefabMoveEvent.Spawn`.
  **[📄 binary]**
- `Projectile.cs` caches `_renderers`, turns them on in `Fire` and off on explode or recycle. **[📄 binary]**

Things to rule out, all **[⚠ inferred]**:

- The spawn fails, for example a pool or prefab that does not resolve.
- The projectile exists but its material draws nothing: a stand-in shader, or a bullet shader the converter maps wrongly.
- The move data of donor fighters refers to prefabs the 3.1.0 data lacks.

The `run.sh` log warns `extract_donor_moves: UnityPy is missing` and `9.2 fighters will have no hit events`. An earlier
note calls this harmless for the shell because the phone builds `moves92` itself. **[⚠ inferred]** Check it again if the
problem turns out to be move data.

The fight logs showed no projectile errors, but no shots were fired in them, so that says nothing. **[👁 live]**

## Open: missing held objects

No bot is known, so check a sample of fighters, including both 3.1.0 and donor 9.2.0 bots. Avoid SP3.

- Props are driven by `PropsController` and `PropData` (`Prop.cs`, `PropData.cs`, `MatineePropEvent.cs`). **[📄 binary]**
- Bullets may share a cause, because both go through prefab or shader handling. **[⚠ inferred]**

`tftf/fullmana.txt` keeps player 0's special meter full, which helps here. Remove it afterwards.

With the meter full, the bot keeps choosing `special` but did almost no damage, so it may not be triggering specials.
**[👁 live]** Check that before trusting a recording as evidence.

## Open: screen static

Two things were seen, and neither is confirmed as what the reporter means:

- A magenta strip on the right edge of the loading screen, in the Arena fight recording. Magenta usually means a missing
  shader. **[👁 live, cause ⚠ inferred]**
- Small white squares that float over the home screen. **[👁 live, cause unknown]**

Popup backgrounds also look blocky, and a black band covers the lower part of some menu screens. These may be the blurred
screen capture. **[👁 live, cause ⚠ inferred]** Ask the reporter which screen shows the static, and get a screenshot if
possible.

## Committed fixes

All in `78c6d56` (Launcher 0.13), pushed to `development`:

| File | Change |
|---|---|
| `mono310/launcher/shell/Tftf/TftfLanguages.cs` | New. Extracts 9.2.0 string tables, loads fallbacks, translates story text. |
| `mono310/launcher/shell/Tftf/TftfInstaller.cs` | Runs the extraction. Install version is 4 on phones and 6 on Windows, which forces a reinstall. |
| `mono310/launcher/shell/Tftf/TftfLauncher.cs` | Version 0.13. |
| `mono310/scripts/compat310.py` | P60 language and story hooks, P61 UI sounds, P62 language detection. |
| `Server/fakeserver.lbl` | Arena fixes: `/pvp/quit` match data, events refresh reply, `events` group hash. |
| `Server/data/story_i18n.json` | New. 52 story lines in 16 languages plus English. |
| `Server/test_story_i18n.py` | New. Checks the story table is complete. |
| `mono310/launcher/CHANGELOG.md` | Entries for the above, under 0.13. |

Causes:

- **Raw text.** Apart from Spanish, the non-English 3.1.0 tables hold about 1,485 of 6,236 strings. **[📄 binary]** A
  missing string displays as `MS_<id>`, per `EB/Localizer.cs`. **[📄 binary]** Selecting Portuguese now gives 7,851
  strings. **[👁 live]**
- **Load order.** 9.2.0 English, 3.1.0 English, 9.2.0 language, 3.1.0 language, later ones winning. A 9.2.0 string whose
  `{0}` placeholders differ from English keeps English.
- **Language detection.** Detection ran before `LanguageConfiguration` existed, so the log said
  `Portuguese not supported, switching to English`. **[👁 live]**
- **Menu sounds.** The boot objects are created as `audiomanager`, `setup`, `fabric`. **[📄 binary]** `FabricAudioPlayer`
  therefore cached a null event manager and dropped every UI cue without an error. Music and fight sounds use
  `FabricControl`, which initialises later.
- **Arena.** The flow logged `event=False` and fell back to the Arena list, because `/autorefresh/events/refresh` hit the
  generic rule and its reply had no events. **[👁 live]** `/pvp/quit` also left the finished match as current.

## Other things to do before release

- **Fresh install.** The `lang92` tables were pushed by hand. The extractor passed on the PC against the real 9.2.0 APK
  (257 tables), but the install step has not run on a device. **[⚠ inferred]** Do one clean install.
- **Other languages.** Only Portuguese was seen in-game. Chinese, Korean, Japanese and Arabic were only seen as names in
  the language list. **[👁 live]**
- **Story text.** The translations were never seen on a device and nobody has reviewed them. Russian mixes "Оптимус Прайм"
  and "Мегатрон" with Latin names.
- **Gaps.** 9.2.0 has only account strings for Japanese and Norwegian, so they fall back to English. **[📄 binary]** The
  `in` folder is empty.
- **Saved language.** A player with English saved stays on English until changing it in Settings. **[⚠ inferred]**
- **White terrain.** Switching language in-game turns the base terrain white until the next restart. **[👁 live]**
- **Arena art and text.** The Arena card image and its bot requirement and reward areas look blank. **[👁 live]** Numbers
  from the base screen also ghost through some menus, as in the English Arena and squad screens. **[👁 live]**
- **Version.** Bumped to `0.13`.
- **Windows and Quest.** Neither was built or tested with these changes.

## Build

The ARM64 shell uses IL2CPP, so `run.sh fast` (DLL swap) cannot carry code changes. A full build took about 6 minutes.
**[👁 live]** Server-only fixes can be pushed by adb without a build.

The build needs the external drive mounted. Before assembling, load the bootstrap variable from `~/.bashrc`, or the Arena
never pairs. Never write its value into a file. The bootstrap line is what `eval` below is for.

```bash
eval "$(grep -E '^export (FLY_APP|TFTF_P2P_BOOTSTRAP)=' ~/.bashrc)"
M=/run/media/darabat/24f15d44-9691-4cf4-8b52-bc03f1e3693f
W=$HOME/tftf-build/mono310-arm64
export TFTF_SHELL=1 TFTF_ANDROID_ABI=arm64-v8a TFTF_WORK=$W
export TFTF_310_APK=$HOME/coding/tftf-old-revival/com.kabam.bigrobot_v3.1.0-822754_Android-4.4.apk
export TFTF_LEGIBLE_ARM64=$W/inputs/liblegible.so TFTF_TREES_BIN=$W/inputs/trees.bin
export UNITY_2020_ROOT=$M/Unity/Hub/Editor/2020.3.31f1
export TFTF_KEYSTORE=$HOME/.android/tftf-release.keystore TFTF_KEYSTORE_PASS=$(cat $HOME/.android/tftf-release.pass)
export JAVA_HOME=$HOME/.asdf/installs/java/temurin-21.0.12+101.0.LTS ANDROID_HOME=$HOME/Android/Sdk
export PATH=$JAVA_HOME/bin:$HOME/.dotnet:$PATH
cd mono310 && ./run.sh assemble && ./run.sh assets && ./run.sh build && ./run.sh finalize
```

**If the script exits 0 but the APK's time did not change, the build failed.** Look for `error CS` in
`$W/ShellProject/unity-build.log` and confirm `Android build: Succeeded`.

Workspace changes made on 2026-10-31:

- `$W/ripped` now points to `$M/tftf-310-recompile/ripped`, because its old target no longer exists.
- `$W/ilspy-out` is now a real local folder, regenerated with `ilspycmd` 9.1 from `~/tftf-build/tools`. The copy in
  `tftf-310-recompile` has no third-party decompiles, which causes hundreds of compile errors in `NBidi` and `Fabric.Core`.
- Decompiled sources are local only. Never commit them. Patches in `compat310.py` use single-line anchors.

## Install without the APK picker

Installing a new build deletes the phone's `Server` folder, including the save, and the version bump asks for both APKs
again. To skip that during testing:

```bash
D=/sdcard/Android/data/unicronslair.bigrobot/files/tftf
adb shell am force-stop unicronslair.bigrobot
adb pull $D/Server/logs ~/tftf-build/phone-backup/
adb install -r --no-incremental ~/tftf-build/mono310-arm64/tftf-launcher-arm64.apk
adb shell "echo 4 > $D/ready.txt"     # only if ready.txt holds an older version
# start the game once, wait about 30 s so it extracts Server/, then:
adb shell am force-stop unicronslair.bigrobot
adb push ~/tftf-build/phone-backup/logs/. $D/Server/logs/
```

Server files can be pushed the same way to `$D/Server/`. Grep the phone's copy before judging a server change, because a
new build silently replaces it.

The phone's save has changed: test fights took the account from level 1 to 4, and the game language is Portuguese. The
backup in `~/tftf-build/phone-backup` is from after those fights.

## Drive the phone safely

**Never send blind taps.** A blind tap once hit a notification and opened the user's private Messages app, and a scripted
sequence drifted into the Story team screen when a tutorial popup appeared.

```bash
top() { adb shell dumpsys activity activities | grep -m1 topResumedActivity | grep -q unicronslair; }
st() { top || { echo "NOT FOREGROUND"; return 3; }; adb shell input tap "$1" "$2"; sleep "${3:-5}"; }
last() { rg -a "TFTFBOT\] screen" "$LOG" | tail -1 | sed 's/.*screen=//'; }
```

Capture with `adb logcat -v time -s Unity:V > "$LOG"`, and check `last` before each step. Do not use `pkill -f` with a
pattern that appears in your own command, because it kills your shell. Use `rg -a`, since the log can look binary.

A52 taps at 2400x1080, Arena route:

| Step | X, Y |
|---|---|
| Play Now | 1200, 870 |
| Dismiss popup | 1200, 750 |
| FIGHT tab (the first tap is often swallowed) | 1200, 170 |
| Arenas card | 1116, 876 |
| Back arrow | 68, 54 |
| Enter | 1003, 990 |
| Pick squad, three bots in the top row | 654, 552 then 1000, 552 then 1357, 552 |
| Accept squad | 170, 1000 |
| Continue, bottom right | 2228, 1008 |
| Matchup slots | 770, 485 then 770, 655 then 770, 825 |
| Matchup bots | 582, 486 then 582, 660 then 582, 835 |
| After a K.O.: Next round | 1390, 768 |
| After the last K.O.: Continue | 1190, 768 |
| Series results: Back to Arenas | 220, 1007 |
| Settings gear | 72, 54 |
| Language selection, in Settings | 1447, 555 |

An idle fighter loses in about 30 s, which is the fastest way through a series.

## Dead ends

Each of these cost at least one test cycle:

- **`AudioManager` was never a null-player problem.** There was no `Audio player null` log, and `Play Audio >` lines
  appeared. The null reference was one level deeper. **[👁 live]**
- **3D audio attenuation was not the cause of silent menus.** The UI sounds and the listener both sit near the origin.
  **[📄 binary]**
- **A dropped `TeamsReady` callback was not the Arena bug.** A bounded wait changed nothing, and the log showed
  `teamsReady=True`. **[👁 live]** The patch was removed.
- **The `/pvp/quit` match data alone did not fix the Arena.** It changed the button from `CONTINUE` to `ENTRAR`, but the
  button stayed dead until the events refresh was fixed. **[👁 live]**
- **Opening the Arena screen repeatedly, and quitting mid-series, both work.** The bug only appears after a finished
  series. **[👁 live]**
