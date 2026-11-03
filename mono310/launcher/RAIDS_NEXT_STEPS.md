# Raids: what is still missing, step by step

Written 2026-11-03 at the end of the device session that followed
[`RAIDS_EXECUTION_PLAN.md`](RAIDS_EXECUTION_PLAN.md). This file is the plan for the next session. It narrows Phases B
and C of the execution plan to the steps still open, using what this session found. The stage design stays in
[`RAIDS_P2P_PLAN.md`](RAIDS_P2P_PLAN.md); the working-tree notes stay in [`RAIDS_HANDOFF.md`](RAIDS_HANDOFF.md).

Marks follow [`DOC_STYLE.md`](../../DOC_STYLE.md): **[👁 live]** seen on the A52, **[📡 served]** in the payload we
serve, **[📄 binary]** read from the 3.1.0 decompile
(`$M/tftf-310-recompile/ilspy-out/Assembly-CSharp/`, `M=/run/media/darabat/24f15d44-9691-4cf4-8b52-bc03f1e3693f`),
**[⚠ inferred]** reasoning only.

## Table of contents

- [1. Where raids stand](#1-where-raids-stand)
- [2. What this session established](#2-what-this-session-established)
- [3. Step 0: restore the phone to a known state](#3-step-0-restore-the-phone-to-a-known-state)
- [4. Step 1: get past the confirm popup every time](#4-step-1-get-past-the-confirm-popup-every-time)
- [5. Step 2: find and fix ID_UI_QUESTS_EXPIRED at team select](#5-step-2-find-and-fix-id_ui_quests_expired-at-team-select)
- [6. Step 3: board, fight, results, claim](#6-step-3-board-fight-results-claim)
- [7. Step 4: make it repeatable and finish Stage 1](#7-step-4-make-it-repeatable-and-finish-stage-1)
- [8. After Stage 1](#8-after-stage-1)
- [9. Rig notes and traps](#9-rig-notes-and-traps)
- [10. Uncommitted work](#10-uncommitted-work)
- [11. Open questions](#11-open-questions)

## 1. Where raids stand

- The raid unlocks, the opponent list shows the three NPCs, and the confirm popup opens. **[👁 live]**
- The furthest a raid has gone is team select. The 17:40 run logged `[PVPRAID] Enter RaidFlow`, then
  `Current State: TeamSelect`, then `ID_UI_QUESTS_EXPIRED`. **[👁 live]**
- Board, fight, results and claim have never been reached on the device.
- Every later manual attempt looped: CONTINUAR closed the popup and returned to the opponent list, with no request
  and no `PVPRAID` line. **[👁 live]** The cause is in §2, F4.

## 2. What this session established

| # | Fact | Mark | Evidence |
|---|---|---|---|
| F1 | `tutorial/get-login-data` is answered by `tutorial_login_response()` in code, not by a canned file. It listed only FTE, so the client started every other tutorial. | 📡 served | `Server/fakeserver.lbl` `static_dynamic`; `server.log` shows `-> dynamic` |
| F2 | With ShieldTutorial, RaidsTutorial, BotPlacementTutorial, NavToQuestTutorial, FightResultsTutorial, ModTutorial and RelicsTutorial completed in the login data, a launch makes no `start-tutorial`, `start-branch` or `complete-tutorial` request. | 👁 live | `requests.log`, 18:05 run |
| F3 | The APK build packs a fixed list of server files. `raid.lbl` was not in it, so a fresh install re-extracted a server that failed with `Module 'raid' not found` (grey screen, title stall). Fixed. | 👁 live | `server.log` 17:55; `mono310/scripts/assemble_project.py` `SERVER_FILES` |
| F4 | CONTINUAR only starts the raid if `IsExpired()` is false. `IsExpired()` uses `_autoAdvanceTime`, which is the time the opponent list arrived plus `Tuning.AutoAdvanceRaidOpponentDisplayTime` (default 45 s). After that, CONTINUAR returns 0 and the list stays. | 📄 binary | `PVPRaidSelectDataProvider.cs:52-64, 85-117`; `PVPRaidFlow.cs:588`; `Tuning.cs:420, 426` |
| F5 | The manual taps that looped came minutes after the list appeared; the 17:40 run, which tapped within seconds, reached team select. F4 explains the loop. | ⚠ inferred | Timestamps of the 18:09–18:15 attempts vs. the 17:40 run |
| F6 | At home, the legacy quest DB holds the raid set: `cat=raid setId=raid keys=[raid-npc-1,raid-npc-2,raid-npc-3]`, and `GetQuestSetOfQuest("raid", "raid-npc-N")` finds it for N = 1..3. | 👁 live | `[RAIDDIAG]` line, 18:01:01 |
| F7 | So the `ID_UI_QUESTS_EXPIRED` at team select is **not** a parse failure of the quest list. Either the set is removed between home and team select, or team select looks up a different category or id. | ⚠ inferred | F6 plus `Legacy/QuestsManager.cs:572-577` |
| F8 | `Legacy/QuestsManager.OnData` clears `AvailableSets` and re-adds them from `availableQuests` whenever a payload for the `quests` component carries that key. | 📄 binary | `Legacy/QuestsManager.cs:407-426` |
| F9 | The game logs every window as `[TFTFBOT] screen=<name>`. The raid path is `TransformersHomeScreen` → `FightLandingScreen` → `PVPRaidSelect` → `ConfirmationPopup`. A tap that misses the menu on home opens `BaseEditNodePopup`. | 👁 live | logcat, 18:07–18:10 |
| F10 | The raid tier serves `findMatchCost: []`, so the cost check passes and is not the blocker. | 📡 served, 📄 binary | `Server/data/missions_config.json`; `PVPRaidFlow.cs:1725-1850` |
| F11 | Installing the 18:00 build re-extracted the phone `Server` folder and reset the save: the profile is now NeonForge29, level 1 (was RustStriker83, level 2). | 👁 live | Home screen at 18:08 |

## 3. Step 0: restore the phone to a known state

1. Force-stop the app.
2. Push the saved state back: `adb push /tmp/rdiag/phone_backup/Server/logs/. $P/logs/`
   (`P=/sdcard/Android/data/unicronslair.bigrobot/files/tftf/Server`). This holds `.fakeserver-xp` (level 2) and
   the old nickname. If `/tmp` was cleared, set XP to 100 in `logs/.fakeserver-xp` (level thresholds `50*L*(L-1)`).
3. Push the repo server files that are not yet in an APK build: at least `Server/fakeserver.lbl` (the ModTutorial and
   RelicsTutorial entries were added after the 18:00 build).
4. *Check:* `md5sum` on the phone matches the repo for `fakeserver.lbl`, `raid.lbl`, `gamedata.lbl` and
   `responses/GET__quests_quest-list.json`; home shows level 2.

## 4. Step 1: get past the confirm popup every time

Goal: CONTINUAR always reaches `[PVPRAID] Enter RaidFlow`.

1. **Rig rule:** tap ASSALTO and CONTINUAR within 45 s of `PVPRaidSelect` appearing. `raid_nav.sh` already does this
   when it runs from home; do not drive these two taps by hand after the list has been open for a while. If the list
   is stale, back out to the fight menu and re-enter ASSALTOS.
2. **Make the 45 s window go away for testing (server only).** Check how `Tuning` is filled from `/tuning` (look for
   the deserializer of `Tuning.cs`, field names `AutoAdvanceOnExpire`, `AutoAdvanceRaidOpponentDisplayTime`). If the
   served tuning can set them, serve `AutoAdvanceRaidOpponentDisplayTime` large (for example 3600) in
   `GET__tuning.json`, `GET__tuning.mono310.json` and `POST__tuning.mono310.json`. Keep `AutoAdvanceOnExpire` as is
   unless the large value is not enough.
   - *Check:* `server.log` shows the tuning file served; a CONTINUAR tap made two minutes after the list opened still
     logs `Enter RaidFlow`.
3. **Script:** let `raid_nav.sh` start from any state on the raid path. Before the first step, read the last logged
   screen; if it is `PVPRaidSelect`, back out once with BACK and re-enter, so the 45 s window starts fresh.
4. *Exit:* three runs in a row from home log `Enter RaidFlow` and `Current State: TeamSelect`.

## 5. Step 2: find and fix ID_UI_QUESTS_EXPIRED at team select

Goal: team select passes and the board loads.

The failing call chain **[📄 binary]**: `PVPRaidFlow.SelectTeam` → `GetUserProfile(_activeQuest.opponent)`
(`PVPRaidFlow.cs:647-649`) → for a negative uid, `Quests.QuestsManager.GetQuestDetails(layout)`
(`Quests/QuestsManager.cs:420-437`) → `Legacy.QuestsManager.GetQuestDetails(layout.category, layout.id)`
(`Legacy/QuestsManager.cs:572`) → `QuestDB.GetQuestSetOfQuest` (`Legacy/QuestDB.cs:113-123`) → null →
`ID_UI_QUESTS_EXPIRED`.

1. **Capture the DB at the moment of failure.** Run Step 1's flow on the current build (it has `TftfRaidDiag`). Then:
   `adb logcat -d | grep -E "RAIDDIAG|PVPRAID"`. `TftfRaidDiag` logs only when the DB changes (every 2 s check), so a
   line between `Enter RaidFlow` and the error means the DB changed during the flow.
   - *Result A:* a `[RAIDDIAG]` line after `Enter RaidFlow` without the raid set (or with `lookup ...=null`). The set
     is being replaced. Go to 2.
   - *Result B:* no new line, or the raid set still present with all lookups found. The lookup uses another category
     or id. Go to 3.
2. **Result A — find what replaces the set.** Look at every response the client gets between the confirm and team
   select (`requests.log`: `find-match`, `activate-match`, `quest-join`, `quests/quest-active`, autorefresh). For each,
   check for an `availableQuests` key on a `quests` payload (F8) or an async message that does
   `ClearAllAvailableQuests`. The fix is server-side: either drop that key from the response, or include the raid set
   in it.
   - *Check:* after the fix, the `[RAIDDIAG]` line during the flow still shows the raid set; team select passes.
3. **Result B — log the layout the client actually uses.** Extend `TftfRaidDiag.cs` (new file, ours; `TftfBot.cs`
   stays untouched) to log, while a `PVPRaidFlow` is active, `_activeQuest.qid`, `_activeQuest.opponent`, and the
   layout's `type`, `category` and `id` that `GetUserProfile` passes on. Use reflection on the private fields if they
   are not public. Rebuild ARM64 (see §9 for the build traps), install, repeat 1.
   - Then make the served layout (`Server/raid.lbl` `raid_layout`, `raid_quest`) and the quest-list summary agree on
     category and id. The raid summary's category is the builder category `raid` (`EB.Missions/Summary.cs:82`); the
     layout category must match it case-insensitively.
   - *Check:* `GetQuestDetails` passes: the next request is `quests/quest-detail/raid-npc-N` (`raid.detail_result`)
     and the PVPRAID state advances past TeamSelect.
4. Record the cause and evidence in `RAIDS_EXECUTION_PLAN.md` §16 and in `RAIDS_HANDOFF.md` "The open blocker".

*Exit:* team select passes with the repo `raid.lbl` and quest list, and no `ID_UI_QUESTS_EXPIRED` is logged.

## 6. Step 3: board, fight, results, claim

These are C1–C4 of the execution plan, with their checks. Each step: one device run, then read `PVPRAID` lines and
`requests.log`.

1. **Team to board.** Pick the team; expect `quest-join` (or the raid equivalent), then the board with three defenders
   on the path (`raid.lbl` `raid_path`). If the board does not build, compare the served map with a working story map
   (memory notes on tile `links`, `pathData`, `theme`).
2. **Walk and fight.** Move with `quest-movedir` (`raid.lbl` `take_step`). Fight one defender; the fight resolves
   through `resolve-match/quests_fight` and `raid.fight_reply`. *Check:* the tile clears and the journal gets an entry.
3. **Beat all defenders, results, claim.** *Check:* the `raid-end` async arrives, the results screen shows, and
   `POST /raid/claim/<id>` answers `redeemers` and `claimedIds`.
4. **Second raid, attacker loses.** *Check:* `attackerWon false`, the report shows, no double reward on claim.
5. **Restart cases.** Force-stop mid-raid and mid-board; relaunch; the raid resumes; no double reward.

For each failure: find the client reader of the key in the decompile, fix the served shape in `raid.lbl`, add a case
to `Server/test_raid.lbl` or `test_raid_routes.lbl`, run `Server/run_tests.sh test_raid.lbl test_raid_routes.lbl
test_fakeserver.lbl test_gamedata.lbl`, push, retest.

## 7. Step 4: make it repeatable and finish Stage 1

1. **`raid_nav.sh` to the end of a raid.** Extend it past the confirm with screen-name waits for team select, board,
   fight and results. Find the names by tapping once and reading `[TFTFBOT] screen=` (as done for F9). Add the fight
   through the existing TftfBot command file (`tftf/bot.on`, `tftf/botcmd.txt`), not new code in `TftfBot.cs`.
2. **Tutorial gates, permanent.** Decide whether the seven completed tutorials stay in the login data for all builds
   or only while testing (they also hide those tutorials from players). Record it in `RAIDS_P2P_PLAN.md` step 0.14.
3. **armeabi-v7a.** Build the 32-bit variant and repeat §6 steps 1–4 (memory `tftf-armv7-32bit`).
4. **Remove or gate the diagnostic.** `TftfRaidDiag.cs` logs on every DB change. Keep it behind a flag file (like
   `tftf/bot.on`) or delete it before a release build.
5. **Commit split (needs your go-ahead).** Propose separate commits for: the server raid work, the tutorial login data,
   the build packing fix, the navigation script, the diagnostic, and the docs. Leave `README.md`, `TftfBot.cs`,
   `compat310.py` and `FEEDBACK_HANDOFF.md` out (not ours). Push to `gummygamer`, branch `development`.

*Exit of Stage 1:* §6 steps 1–5 pass on ARM64, then steps 1–4 on armv7.

## 8. After Stage 1

Stages 2–8 are unchanged; follow `RAIDS_EXECUTION_PLAN.md` §7–§13 (defenders on the base, identity and snapshots,
P2P spreading, raiding real players, results back to defenders, lifecycle, release). Stages 4–6 still need a second
device or the single-process tests (decision D3 there).

## 9. Rig notes and traps

- **Server files on the phone.** The app reads `Server/` from shared storage per request, so a server-only change is
  `adb push` plus a force-stop and relaunch. **[👁 live]**
- **A new build re-extracts `Server/`** from the APK and overwrites pushed files and the save (F11). Pull `Server/logs/`
  before installing; push it and any newer server files back after the first launch.
- **The APK packs only `SERVER_FILES`** (`mono310/scripts/assemble_project.py`). Any new `.lbl` module must be added
  there, or the installed server fails on import (F3).
- **Build:** `/tmp/rdiag/build_arm64.sh` (env from memory `tftf-arm64-release-build-env`). The script prints
  `BUILD_DONE` even when Unity fails; check `~/tftf-build/mono310-arm64/ShellProject/unity-build.log` for `error CS`.
  A build takes about 5 minutes.
- **Shell code** compiles into Assembly-CSharp; in the decompile the quest set type is `Set` reached through
  `using Quests;`, not `Legacy.Set`. Use `var` when the type name is unclear.
- **Phone drops off adb** now and then. Check `adb devices` before a run; do not chain long scripts without it.
- **`pkill -f raid_nav.sh`** from a Bash tool call kills the calling shell too. Stop runs with the task tool instead.
- **The featured-bot panel** ("Bot em destaque") closes with BACK, not its X.

## 10. Uncommitted work

Added or changed this session (ours):
- `Server/fakeserver.lbl`: `completed_tutorial` and the seven completed tutorials in the login data.
- `Server/test_fakeserver.lbl`: `test_tutorial_login_marks_raid_and_base_tutorials_completed`.
- `mono310/scripts/assemble_project.py`: `raid.lbl` in `SERVER_FILES`.
- `mono310/launcher/tools/raid_nav.sh`: screen-name waits, BaseEditNodePopup handling, fight-mode card tap.
- `mono310/launcher/shell/TftfMain/TftfRaidDiag.cs` (new): the quest DB diagnostic.
- `mono310/launcher/RAIDS_EXECUTION_PLAN.md`: progress log lines; this file.

The raid work from the earlier session is still uncommitted too; see `RAIDS_HANDOFF.md` "Uncommitted work".

## 11. Open questions

- **Q1.** Does the served `/tuning` reach `Tuning.AutoAdvanceRaidOpponentDisplayTime`? Answers §4 step 2.
- **Q2.** Is the raid set replaced during the raid flow (Result A) or looked up under another key (Result B)? Answers
  §5.
- **Q3.** Decided: the completed tutorials stay in the shipped login data (user, 2026-11-03). Recorded in `RAIDS_P2P_PLAN.md` step 0.14.
- **Q4.** Was the raid unlock at level 2 still enforced for the level-1 NeonForge29 profile? The opponent list showed,
  so possibly not; check after Step 0 restores level 2.
