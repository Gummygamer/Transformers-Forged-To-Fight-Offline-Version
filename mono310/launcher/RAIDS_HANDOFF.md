# Raids: handoff for a fresh session


## Current checkpoint — 2026-11-10

The bootstrap-backed raid integration is implemented. This checkpoint supersedes historical notes below.

- A52 ARM64 live: discovered a signed LAN peer, started its cached base while the defender was offline, won a real fight, and reached `PVPRaidResults` with both portraits and ratings. Reconnecting the defender delivered the signed report with attacker HP 0.8883 and defender HP 0; acknowledgment emptied the phone outbox. The reverse direction also passed: the phone's embedded server accepted a signed defence report, enabled the loss shield, and acknowledged it.
- Native base UI removal and placement passed. The placed Nemesis Prime and the signing-key hash survived APK updates. Edited-base loading now substitutes the saved placements into a static response template, avoiding full-map JSON parsing on Android (measured 0.89 seconds).
- Validation: 176 server tests, 11 signed-record checks, Arena lockstep suite, and two-peer restart/offline/replay integration all pass. Latest integration logs: `/tmp/tftf-raid-integration-hrrlq9ow`; server suite: `/tmp/tmp.Rb4Z0sbro9`; follow-up placement suite: `/tmp/tmp.m989LvP8s9`; Arena: `/tmp/tmp.Fwg5Z5JTOD`.
- Final release-signed APK: `~/tftf-build/mono310-arm64/raid-integration-arm64-release.apk`. Generated project bootstrap restored to `tftf-p2p-bootstrap.fly.dev:7777`; production UDP challenge response verified. PC-server redirect removed. Private LAN fixture snapshot and peer cache entry removed; test reports remain in history. The temporary test-loss shield was cleared after verification.
- Follow-up fixes: foreign profiles now serve the actual `gamestats` wire key; P67 preserves JSON/text stats when display registry metadata is absent. The raid phone helper now returns after starting a fresh raid instead of immediately quitting it.
- First-release scope: up to eight distinct boss blueprint/level pairs, rank-one combat data and a linear raid board; original geometry and towers stay local. Rewards remain empty under current configuration. No central economy or authoritative anti-cheat is introduced.
- Internal disk remains around 20 GB free; external offloads and prior save backups remain intact. Final validation backups: `~/tftf-build/raid-final-validation`.

## Current checkpoint — 2026-11-09

This supersedes the older status below. Work through `dce1a5f` is committed; current fixes remain uncommitted.

- Live A52 ARM64: opponent/team selection, board movement and real combat work against the PC dev server. In `raid.7-0`, the first defender was beaten; a loss to the second preserved the defender at 0.2110936 HP, killed Nemesis Prime, and left two other attackers available. Full NPC victory and PVPRaidResults now pass on the A52 (`raid.8-0`). The loopback claim endpoint was checked twice: the claim flag stays set and the history retains one result; rewards remain empty by current configuration.
- Found the recovery gap: `PVPRaidFlow.GameBoardReady` rebuilds the board without processing queued actions. Added shell patch P64 in `compat310.py` to call `CheckForActions` after the HUD is ready. ARM64 script compilation and the APK build pass. Release-signed `~/tftf-build/mono310-arm64/raid-recovery-arm64-release.apk` is installed on the A52; Live resume and post-loss retry now pass: `raid.8-0` returned automatically to PreFightScreen after Nemesis Prime lost, with two attackers still available. Follow-up passes: Optimus Primal was selected automatically, beat that defender, and retained 0.9109 HP; Nemesis Prime stayed at zero. The second defender was then beaten; the client reached the victory results screen.
- Corrected the raid Continue tap to `(1220,526)` and made `raid_e2e.py` fail when nine fights do not reach results.
- Updated stale loss expectations and added HP/retry/expiry checks. Tests pass: raid 30, raid routes 13, fakeserver 41, gamedata 78. Synchronized the nested account missions config with the existing DisabledLevels changes.
- Phone save backup: `~/tftf-build/devserver/pre-p64-backup/logs`; PC save backup: its `pc-logs` directory. Phone save was restored after the APK update. Previous APK: external `codex-tftf-build/raid-session-backups/pre-p64-launcher.apk`.
- Unity is on the reconnected external drive under `Unity/Hub/Editor/2020.3.31f1`. Build project: `~/tftf-build/mono310-arm64/ShellProject`. Bootstrap setting in that project remains `tftf-p2p-bootstrap.fly.dev:7777`.
- Internal disk cleanup recovered 23 GB free. `media`, `.scratch`, and `.longtask` now link to preserved external copies under `codex-tftf-build/internal-disk-offload-20261109`; keep them ignored and out of Git. Cleanup report: `~/tftf-build/storage-cleanup-20261109.md`.
- The bootstrap remains discovery-only. Defender placement, signed snapshots, mesh distribution, player raid selection and result delivery are unfinished; the overall bootstrap-backed raids objective is not complete.

Status as of 2026-11-02. Read this with [`RAIDS_P2P_PLAN.md`](RAIDS_P2P_PLAN.md), which holds the eight-stage plan,
the Stage 0 findings and the "Device status" section. This file is the working state: what is uncommitted, what is
running on the phone, the open blocker, and what to try next.

## Table of contents

- [Where things stand](#where-things-stand)
- [Uncommitted work](#uncommitted-work)
- [Phone state](#phone-state)
- [How to run and test](#how-to-run-and-test)
- [The open blocker: ID_UI_QUESTS_EXPIRED](#the-open-blocker-id_ui_quests_expired)
- [Experiments already run](#experiments-already-run)
- [Next steps](#next-steps)
- [Remaining plan](#remaining-plan)

## Where things stand

- Stage 0 (contract) is recorded, except 0.14 (tutorial gates).
- Stage 1 server side is built and passes offline tests. Routes, the raid rules, the NPC data and the board are in
  `Server/raid.lbl` and the raid routes in `Server/fakeserver.lbl`.
- Stage 1 device exit is **not** passed. On the A52 (3.1.0 build, `unicronslair.bigrobot`), the raid unlocks at level 2,
  the NPC list shows the three opponents, and the first raid reaches **team select**. Team select then fails with
  `ID_UI_QUESTS_EXPIRED`. Board and results have never been reached.
- Stage 1.11 (TftfBot raid path) is not started. Stage 0.14 (tutorial gates) is not done. Stages 2 to 8 are not started.
- Nothing is committed. The work is in the working tree.

## Uncommitted work

Raid work (ours):
- `Server/raid.lbl` (new): NPC data, the raid board, the fight loop, results, claims, the quest set and the quest detail.
- `Server/test_raid.lbl` (new, 23 rule tests), `Server/test_raid_routes.lbl` (new, 13 route tests).
- `Server/fakeserver.lbl`: the raid routes (`raid_dynamic`), the quest-detail route for `raid-npc-*`.
- `Server/gamedata.lbl`: `quest_team_json` wrapper; the missions update check.
- `Server/test_gamedata.lbl`, `Server/test_fakeserver.lbl`: the missions check expectation; the raid-set exemption in the
  quest-set slot test (`quest_set_slots_ok` skips category `raid`).
- `Server/data/missions_config.json`: a `pvp` entry (the raid config), check `offline-v2`.
- `Server/responses/`: the `GET__tuning*.json` and `POST__tuning.mono310.json` files (`fightLanding_raid` ml 2); the
  missions canned responses (`pvp` entry, check `offline-v2`); `GET__account_data.json`; the quest lists
  (`GET__quests_quest-list*.json`, `POST__quests_quest-list.mono310.json`) and the quests autorefresh mono310 pair
  (raid set appended to `result.quests`).
- `mono310/launcher/RAIDS_P2P_PLAN.md`: Stage 0 findings, status, device status.
- `mono310/launcher/tools/raid_nav.sh` (new): drives the phone from the title screen (or home) to the raid
  confirmation, waiting for screen changes.
- This file.

Not ours (already dirty before this work, do not commit with the raid work):
`mono310/launcher/README.md`, `mono310/launcher/shell/TftfMain/TftfBot.cs`, `mono310/scripts/compat310.py`,
`mono310/launcher/FEEDBACK_HANDOFF.md`.

Note: the 3.1.0 `grouprefresh` missions update is built from `data/missions_config.json`, and the 2.0.2 client also
reads the default files. The raid config and the level-2 raid unlock therefore reach the 2.0.2 build too. Decide before
committing.

## Phone state

- Package `unicronslair.bigrobot` (3.1.0, versionCode 822754). Its server runs from
  `/sdcard/Android/data/unicronslair.bigrobot/files/tftf/Server/`, which holds the same files as the repo's `Server/`.
  Code changes need an app restart (`adb shell am force-stop unicronslair.bigrobot`, then relaunch with `monkey`).
- Saved XP is 100 (level 2): `logs/.fakeserver-xp`. Level thresholds are `50*L*(L-1)`.
- Server files on the phone match the repo as of the last push. The last edits pushed were the tier `maxRating`
  fix and the reverted probes.
- Starting the app on a cold launch takes a few minutes before the title screen (asset loads of about 45 s each).

## How to run and test

Server tests (run in the background, they take minutes):

```bash
cd Server && ./run_tests.sh test_raid.lbl test_raid_routes.lbl test_fakeserver.lbl test_gamedata.lbl
```

Single file: `legible run Server/test_raid.lbl`. Check syntax: `legible check file.lbl 2>&1 | grep -v '"severity":"warning"'`.

Push server files to the phone (the phone copy must match the repo before a test):

```bash
P=/sdcard/Android/data/unicronslair.bigrobot/files/tftf/Server
adb push Server/raid.lbl Server/fakeserver.lbl Server/gamedata.lbl $P/
adb push Server/responses/GET__quests_quest-list.json $P/responses/   # and the other changed responses
adb shell am force-stop unicronslair.bigrobot
adb shell monkey -p unicronslair.bigrobot -c android.intent.category.LAUNCHER 1
```

Navigate to the raid confirmation (run in the background, it takes about a minute from launch):

```bash
mono310/launcher/tools/raid_nav.sh --shot /tmp        # from the title screen
mono310/launcher/tools/raid_nav.sh --no-play --shot /tmp   # from home or the fight menu
```

Read the result: the script prints the PVPRAID lines. Logs: `adb logcat -d | grep PVPRAID`.
Server log: `adb shell "tail -c 800 .../files/tftf/server.log"`.

## The open blocker: ID_UI_QUESTS_EXPIRED

At team select, the client asks for the NPC opponent's profile. `PVPRaidFlow.GetUserProfile` (decompile
`Assembly-CSharp/PVPRaidFlow.cs:2069`) runs for a negative uid and calls
`Quests.QuestsManager.GetQuestDetails(layout)`. That method (`Assembly-CSharp/Quests/QuestsManager.cs:420-437`)
requires `layout.type == "quests"` and then calls
`Legacy.QuestsManager.GetQuestDetails(layout.category, layout.id)`
(`Assembly-CSharp/Legacy/QuestsManager.cs:1280-1300`). That calls
`_questDB.GetQuestSetOfQuest(category, qid)` (`Assembly-CSharp/Legacy/QuestDB.cs:271-283`), and if the result is null
it reports `ID_UI_QUESTS_EXPIRED`.

`GetQuestSetOfQuest` matches a set whose `category` equals the layout category (case-insensitive) and whose
`QuestSummarys` contains the layout id. The layout of the NPC match is `category "raid"`, `id "raid-npc-N"`, and the
client reports the error for that lookup.

The set list comes from `Legacy/QuestsManager.GetAvailableQuests` (`Legacy/QuestsManager.cs:1216-1222`), whose callback
(`:28-38`) calls `ClearAllAvailableQuests` and `AddQuestSummarys` on the `quests/quest-list` result. The parser is
`Legacy/QuestDB.AddQuestSummarys` (`Legacy/QuestDB.cs:~350-360`): each set becomes a `Quests.Set`
(`Legacy/QuestSet.cs`), and each summary is added to `QuestSummarys[summary.sid]` only if `Summary.Deserialize` returns
true. `sid` is the `id` field (`Legacy/QuestSummary.cs:74-79`). `Quests/Summary.Deserialize` (`Quests/Summary.cs:221`)
needs `id` and `category` and sets `layout` for `isRaid`.

What is known for certain: the device fetches `quests/quest-list` at launch (server log), and the served list includes the
raid set. What is not known: whether the set is added and then dropped, or never added. No "Error deserializing" or
exception appears in logcat.

## Experiments already run

| Change | Result |
|---|---|
| Raid layout pointed at a story quest (`Story`, `1.1.1`) | Lookup passes: the game moves on to the ticket check. Proves the failing step is the NPC set lookup. Reverted. |
| Tier `findMatchCost` set to a resource cost (`rc`) | No change to the ticket popup. Reverted. |
| Tier `maxRating` was `-1`, so no tier matched the player's `mdl` rating | Fixed to `2000000000`. This removed the out-of-tickets popup. Kept. |
| `sid` and `setId` added to the raid summaries | No change to `ID_UI_QUESTS_EXPIRED`. Kept (harmless). |
| `opponent` text key added to the active quest | Removed the earlier null-user-id error. Kept. |
| Diagnostic 2 (option 2 below): NPC summary copied into `story_act1` as id `raid-npc-1`, layout `Story`/`raid-npc-1` | Prepared on the phone copies only and pushed. The run did not finish: `raid_nav.sh` was still waiting for the title button, behind a loading screen, when it was stopped. Phone copies reverted to the repo versions. Not yet answered. |
| Tutorial popups on base after launch | Must be dismissed before the script's taps; this is plan item 0.14. |

## Next steps

Two ways to find out why the raid set is not in the quest sets:

1. **Client instrumentation.** The client is an IL2CPP build, so C# code cannot be changed by copying a DLL. A log line
   has to go into the launcher shell (`mono310/launcher/shell/TftfMain/`, compiled into the APK). The shell's
   `TftfBot.cs` already writes `[TFTFBOT]` lines; a new method there could dump `Legacy.QuestsManager.Instance.DB`'s
   `AvailableSets` (category, set id, summary keys) whenever the raid flow starts. Building it needs the ARM64 pipeline
   on the external drive (see `tftf-arm64-release-build-env` in memory: a full build takes about 6 minutes). Installing a
   build with a new buildGUID deletes the phone's `Server` folder: pull `Server/logs/` first and push it back after.
   Commit or stash the dirty `TftfBot.cs` changes first, or build additively on top of them.

2. **Diagnostic through the server only.** Put the NPC summaries into an existing set that the client already accepts
   (`story_act1`, category `Story`), and point the layout at `Story` with the NPC id. If team select then passes, the
   problem is specific to the `raid` category set (for example, a category-specific check in `AddQuestSummarys`). If it
   still fails, the summaries themselves are being dropped. This is cheap and needs no build. It changes what the story
   list shows, so it is temporary and phone-only until the cause is known.

Recommended order: run 2 first. Then decide on 1 with its result.

How to rerun diagnostic 2 (phone copies only; the repo must stay unchanged):

1. Pull `raid.lbl` and `responses/GET__quests_quest-list.json` from the phone's `Server/`.
2. In the quest list, append a copy of the `1.1.1` summary to the `story_act1` set with `id` `raid-npc-1` (keep act 1,
   chapter 1, mission 1; the set has one act and two chapters).
3. In `raid.lbl`, change the NPC layout `ptext("category", "raid")` to `ptext("category", "Story")` and keep
   `ptext("id", "raid-npc-" ++ ...)`.
4. Push both, relaunch, and run `raid_nav.sh --shot /tmp` (wait for the title screen and home, not a fixed time).
5. Read `adb logcat -d | grep PVPRAID`. If team select passes, the `raid` category is the problem. If it still fails,
   the summaries are being dropped.
6. Push the repo versions of both files back.

Caveat: `raid_nav.sh` waits for the green JOGUE AGORA! button at (1200,870). During a cold start the game shows several
loading screens first (asset loads take about 45 s each), and the script keeps polling until the button appears. The
diagnostic run was stopped by hand while it was still waiting on one of those loading screens. Allow several minutes.

Once team select passes: the raid enters the board, the NPC's defenders are boss tiles, and the fight goes through
`resolve-match/quests_fight`. Check the board and the result screen on the phone, then the journal and claim.

## Remaining plan

- **Stage 1 device exit:** completed raid through team select, board and results (blocked above).
- **Stage 1.11:** TftfBot raid path (`mono310/launcher/tools/tftfbot.py`, `TftfBot.cs`) so device checks can repeat the
  raid without manual taps.
- **Stage 0.14:** tutorial gates (`RaidsTutorial`, `BotPlacementTutorial`). The raid screen and the base popups depend
  on them. Decide whether to complete them on the server or to dismiss them.
- **Stages 2 to 8:** see the plan. Stage 2 is placed defenders on the player's base; Stage 3 is identity and signed
  snapshots; Stage 4 is P2P spreading (needs the two-device Arena test first); Stage 5 raids real players; Stage 6
  results back to defenders; Stage 7 lifecycle; Stage 8 device tests, documentation and release.
