# Raids: step-by-step plan to the end

Written 2026-11-02 after the navigation and diagnostic session. This file is the execution plan. It supersedes the
"Next steps" and "Remaining plan" sections of [`RAIDS_HANDOFF.md`](RAIDS_HANDOFF.md) where they differ. The design
and the eight stages stay as written in [`RAIDS_P2P_PLAN.md`](RAIDS_P2P_PLAN.md).

Marks follow [`DOC_STYLE.md`](../../DOC_STYLE.md): **[👁 device]** = seen on the A52, **[📡 served]** = read from a
server file or the request log, **[📄 binary]** = read from the 3.1.0 decompile, **[⚠ inferred]** = not yet checked.

## 1. Where things stand

### 1.1 Facts established this session

| # | Fact | Mark | Evidence |
|---|---|---|---|
| F1 | The navigation script hung in `wait_home` because its `base/active` baseline was read after home had loaded. Fixed. | 👁 device | `raid_nav.sh` run 4 reached the fight menu |
| F2 | Tutorial popups (ESCUDOS / ShieldTutorial, and the raids tutorial) take the scripted taps. The script's "screen changed" check cannot tell a popup from a real step, so it reports a pass. | 👁 device | Screenshots at 13:18, 13:27; `tut_raidstutorial_00/01` telemetry at 13:22:58 |
| F3 | The "Um erro desconhecido ocorreu" popup is **not a new error**. It is `PVPRaidFlow.OnError` (`PVPRaidFlow.cs:2021-2029`), showing `ID_UI_PVPRAID_UNKNOWN_ERROR` for the same `ID_UI_QUESTS_EXPIRED` failure. | 👁 device, 📄 binary | logcat 13:23:16: `[PVPRAID] Error in: ERRO NA BUSCA POR OPONENTES -- ID_UI_QUESTS_EXPIRED` |
| F4 | The raid flow reaches TeamSelect and fails there, with no server call. Across the whole run `requests.log` has no `find-match`, `activate-match` or `quest-join` request. The NPC list is built on the client. | 👁 device, 📡 served | `Server/logs/requests.log`; logcat 13:23:16 |
| F5 | The server serves `GET__quests_quest-list.json`, not the `.mono310.json` variant, for `quests/quest-list`. The URL has no `cl=822754` marker, so `client_profile` returns `""` and `profile_variant` falls back. **Every earlier edit to the mono310 quest-list files was never served.** | 📡 served | `server.log` line 89: `canned GET__quests_quest-list.json (15972B)`; `fakeserver.lbl:2603-2625` |
| F6 | Diagnostic 2 ran with the served file changed: layout `category "Story"` in `raid.lbl`, and `raid-npc-1`, `-2`, `-3` added to the `story_act1` set in the generic quest list. It still failed with `ID_UI_QUESTS_EXPIRED`. This is the first test where the lookup was against a set the client already accepts. | 👁 device, 📡 served | Run 3 log; `server.log` 13:21:36 (15972B); logcat 13:23:16 |
| F7 | Run 5 (three ids in `story_act1`) did not reach the raid. Popups took the taps. It is **inconclusive**. | 👁 device | `run5.log`, screenshot `raid_nav_132751.png` |
| F8 | `ShieldTutorial` is blocked by default (`fakeserver.lbl:268`). The `start-tutorial` answer for a request with no `cl=` marker is `unavailable`, and the client still shows the ESCUDOS popup. `ANDROID_LAUNCHER.md` says the shell host sets `TFTF_TUTORIAL_BLOCKLIST` to empty. The phone's server does not get that variable. | 📡 served, 📄 docs | `requests.log` 13:27:22; `ANDROID_LAUNCHER.md:217` |
| F9 | `GET__tutorial_get-login-data.mono310.json` reports `RaidsTutorial` as `completed`, yet the client ran the raids tutorial at 13:22. Cause not known. The 13:22 request log was overwritten by a later launch. | 📡 served, 👁 device | Login JSON; telemetry |
| F10 | The served quest-list has the raid set (`setId raid`, `category raid`) with `availableQuests` raid-npc-1..3. The client's set lookup (`Legacy/QuestDB.GetQuestSetOfQuest`, `QuestDB.cs:271`) and parser (`Legacy/QuestSet.AddQuestSummarys`, `QuestSet.cs:102-240`) read the same fields. Nothing read so far explains the drop. | 📄 binary | Decompile, `EB/Missions/Summary.cs:384` |

### 1.2 Open questions

- **Q1.** Why does `GetQuestSetOfQuest("Story", "raid-npc-1")` return null after F6, when `story_act1` holds that id?
  Candidate causes, in the order to test them:
  (a) the quest-list callback is not the one that fills `_questDB`, or a later `ClearAllAvailableQuests`/`Clear()` runs
  (`QuestDB.cs:194`, `:393`); (b) `Summary.Deserialize` returns false for these summaries (it would log
  `Error deserializing EB.Missions.Summary`, which is not in logcat); (c) the layout id differs from `raid-npc-N`.
- **Q2.** Why does the client start `RaidsTutorial` when the login data says `completed`?
- **Q3.** Does the 3.1.0 client pick its quest variant by the `cl=` marker on every route, or only on some?

### 1.3 What is in the working tree

- Repo: unchanged from the handoff, plus `mono310/launcher/tools/raid_nav.sh` (rewritten this session) and this file.
- Nothing is committed. `README.md`, `TftfBot.cs`, `compat310.py` and `FEEDBACK_HANDOFF.md` are not ours (see the handoff).
- **Phone, diagnostic copies still in place. Revert before any other test:**
  - `Server/raid.lbl` has layout category `Story` (repo has `raid`).
  - `Server/responses/GET__quests_quest-list.json` has `raid-npc-1..3` in `story_act1` (repo does not).
  - The `.mono310.json` quest-list copies were pushed back from the repo.

## 2. Principles for the rest of the work

1. **Each step ends with a check that could fail.** Use the logcat `[PVPRAID]` lines and the server request log as
   pass/fail signals. Screen-change detection is not a pass.
2. **Read the served file before blaming the code.** Check `server.log` for `-> canned <file>` or `-> dynamic` on every
   route under test (F5).
3. **One change per test.** Each device run changes one thing and records its result in this file.
4. **Server first.** Server-only changes need no APK build (memory: "Prefer DLL-swap builds"; server data changes reach
   the phone by push and an app restart). Build only when the question needs client code.
5. **Test ARM64 first** (memory: `tftf-test-arm64-first`). Port to armeabi-v7a after the ARM64 exit passes.
6. **Do not commit or push** until the user says so (see §4.K). The plan prepares the commit; it does not make it.

## 3. Phase A: make the test rig reliable

Goal: a navigation run that either reaches the raid or says exactly which step stopped it, with no popup taps.

**A1. Revert the phone to repo state.**
- Push the repo `Server/raid.lbl` and the repo `Server/responses/GET__quests_quest-list.json`.
- Check md5 on the phone against the repo.
- *Check:* `adb shell md5sum` matches `md5sum` on the repo files.

**A2. Find out what the tutorial popups are, from the device.**
- Capture the request log for one full navigation run: `adb shell cat .../Server/logs/requests.log > /tmp/rdiag/req_run.log`
  right after the run, before the next launch overwrites it.
- Make a list of every `tutorial/*` request with its `tid`, and whether the answer was `dynamic`, `unavailable`, `started`
  or `completed`.
- *Check:* the list accounts for every popup seen in the screenshots (ESCUDOS, the raids tutorial prompt, any other).

**A3. Decide how the tutorials are answered on the phone (decision: user).**
Options:
- **(a) Server answers completed for the tutorials in the way.** Set `ShieldTutorial` and `RaidsTutorial` to completed in
  `tutorial/get-login-data` and make `start-tutorial` return `completed` for them. No client change.
- **(b) Empty `TFTF_TUTORIAL_BLOCKLIST`** on the phone server, as the shell host does (`ANDROID_LAUNCHER.md:217`).
- **(c) Dismiss popups in the script only.** Fragile; this session showed it is.

Recommendation: **(a)**, because it is server data and keeps the script simple. The gate (`PVPRaidFlow.cs:1356`,
`TutorialManagerHelper.IsTutorialComplete`) reads the same state. Record the choice in this file.

**A4. Make the navigation script fail on the first step that does not produce the expected screen.**
- Replace the screen-change check with a check of the pixel signature of the expected next screen, for each step:
  title (green JOGUE AGORA!), home (`base/active` requested), fight menu (SELECIONE UM MODO DE LUTA header),
  ASSALTOS unlock popup (blue popup, then its close), NPC list (three ASSALTO buttons), confirm popup.
- Keep the popup check, but require the popup pixel to be absent before the next step, not just closed once.
- *Check:* run it from the title screen three times in a row. Each run reaches "Enter RaidFlow", or it stops at a
  named step with a screenshot.

**Exit of Phase A:** three clean runs in a row to `Enter RaidFlow`.

## 4. Phase B: find the cause of `ID_UI_QUESTS_EXPIRED`

Goal: know which lookup fails, and why.

**B1. Re-run diagnostic 2 on the right file, with the phone clean (after A1).**
- Served file: the generic `GET__quests_quest-list.json` with `raid-npc-1..3` in `story_act1`, and `raid.lbl` with
  category `Story`. This is what F6 already tested. Repeat it once with A4's script to make the result certain.
- *Result A (fails again):* the summaries never reach `QuestSummarys`. Go to B2.
- *Result B (passes):* the `raid` category is the problem. Go to B4.

**B2. Server-side probe of the quest-list parse path (no build).**
Test the candidate causes in Q1 one at a time:
- (a) Check the quest-list callback is the last writer: add a unique summary `id` (`probe-1`) to `story_act1` and
  look for it in the next navigation. This is not visible without client code, so instead put the probe id in
  a route that the client reads through the same `QuestDB` (for example the `quest-detail` response for story 1.1.1).
  *Check:* whether `probe-1` lookups fail the same way. Only needed if B1 gives Result A.
- (b) Make `Summary.Deserialize` succeed or fail in a known way: give one story summary a deliberately broken field
  and see whether the same `ID_UI_QUESTS_EXPIRED` appears. If it does not, the parse is not reached.
- (c) Confirm the layout id: the NPC list's tap picks one NPC. Log the picked id by making the layout id unique per NPC
  (`raid-npc-1..3` already are). Check which one the lookup used by putting a distinct `friendlyName` on each.

**B3. Client-side instrumentation (only if B1 and B2 leave the cause open).**
This needs the ARM64 build. Steps:
- Confirm `mono310/launcher/shell/TftfMain/TftfBot.cs` is still the only dirty shell file. Commit or stash it *first*,
  or build additively on top (`dont-stash-inprogress-work` memory: build additively, do not stash by default). Ask the
  user which before touching it, because `TftfBot.cs` is not ours.
- Add one method to `TftfBot` that, when the raid flow starts, logs `Legacy.QuestsManager.Instance.DB.AvailableSets`:
  for each set, its `category`, `setId` and the keys of `QuestSummarys`. Log the lookup arguments in the raid
  flow's `GetQuestDetails` call.
- Build with the release env (memory: `tftf-arm64-release-build-env`; about 6 minutes). **A build with a new buildGUID
  deletes the phone's `Server` folder.** Pull `Server/logs/` first and push it back after.
- *Check:* the logged set list shows whether `story_act1` is present and which keys it has. That answers Q1.

**B4. Fix the cause found in B1–B3.**
- If `raid` category is the problem: change the raid set's category or layout category to match what the client
  accepts, rerun B1 without the Story diagnostic, and keep the raid category only if it passes.
- If the summaries are dropped: fix the field the parser rejects, in the server quest-list data.
- Update `RAIDS_HANDOFF.md` section "The open blocker" with the cause and the evidence.

**Exit of Phase B:** team select passes on the phone with the repo `raid.lbl` and the repo quest list, and the log
shows no `ID_UI_QUESTS_EXPIRED`.

## 5. Phase C: finish Stage 1 on the A52 (ARM64)

Stage 1 exit (from `RAIDS_P2P_PLAN.md`): FIGHT → RAIDS → pick an NPC → team → board → beat every defender → results →
claim, and a second raid where the attacker loses. Then armeabi-v7a.

**C1. Team select to board.**
- Confirm `[PVPRAID] Enter RaidFlow`, then the board loads. Check the `PVPRAID` lines and the server requests
  `quest-join`, `quest-moveto`, `quest-movedir`.
- *Check:* the board appears with three defenders in a path (`raid_path`).

**C2. Walk and fight.**
- Use the existing `quest-movedir` path (`raid.lbl` `take_step`). Fight one defender with `resolve-match/quests_fight`.
- *Check:* `raid.fight_reply` returns the actions; the defender's tile clears; the journal gets entries.

**C3. Beat all defenders → results → claim.**
- *Check:* `raid-end` async arrives; the results screen shows; `POST /raid/claim/<id>` answers `redeemers` and `claimedIds`.

**C4. Second raid, attacker loses.**
- *Check:* the result is `attackerWon false`; the report shows; no double reward on claim.

**C5. Restart cases (plan step 1.9).**
- Force-stop the app mid-raid, relaunch, and check the raid returns. Then force-stop mid-board. Check no double reward.

**C6. TftfBot raid path (plan step 1.11).**
- Add the raid path to `mono310/launcher/tools/tftfbot.py` and `TftfBot.cs` so C1–C5 repeat without manual taps. This
  depends on A4 and on the decision in B3 about `TftfBot.cs`.

**C7. Port to armeabi-v7a.**
- Build the 32-bit variant (memory: `tftf-armv7-32bit`). Repeat C1–C4 on the same phone; note the AVD switch is only
  for the emulator.

**Exit of Phase C:** C1–C5 pass on ARM64, then C7 passes on armv7.

## 6. Phase D: Stage 0.14 tutorial gates

Decision already in A3. Make it permanent:
- If option (a) was chosen: confirm `RaidsTutorial` and `BotPlacementTutorial` are `completed` in
  `GET__tutorial_get-login-data.mono310.json`, `POST__…` and in `auth_login` `tut`. Confirm no `start-tutorial` for them
  appears in a fresh-account run (check `requests.log`).
- Record in `RAIDS_P2P_PLAN.md` step 0.14: decided, with the evidence.

## 7. Phase E: Stage 2, defenders on the base

**E1.** Implement `quest-placehero/<qid>` and `quest-swapheroes/<qid>` in `Server/raid.lbl` and route them from
`fakeserver.lbl`. Persist in the raid state file (`.fakeserver-raid.json` for raids, or a base file if the base owns
it; check `BaseBuilding` / `DefenderSelectPopup` first).
**E2.** Serve placed defenders as entities in `/base/active` (`DefenderSelectPopup` and `BaseEditNodePopup` are the
editor, `RAIDS_P2P_PLAN.md` step 2.2).
**E3.** Decide on towers (step 2.3): ship bosses first, leave `tt` empty.
**E4.** Shield: serve expiry in the profile; void it when the player attacks (`PVPRaidFlow.cs:1688`).
**E5.** Compute `bs`, `td`, `pi` from placed defenders.

**Tests:** `Server/test_raid.lbl` and `test_raid_routes.lbl` extended for each route.
**Device exit:** place, swap and remove defenders on the A52; restart the app; they are still there.

## 8. Phase F: Stage 3, identity and snapshots

**F1. RSA under IL2CPP (step 3.1).** Build a tiny ARM64 IL2CPP test app: generate an RSA-1024 key, sign, verify.
- *If it works:* keep RSA.
- *If not:* switch to a small managed Ed25519 file. Record the decision in this file.
**F2. `TftfRaidNet.Identity`** (step 3.2): key on first use, off the main thread, derive `uid`.
**F3. Loopback routes** `GET /tftf/raid/snapshot-fields` in `raid.lbl` (step 3.3).
**F4. Encoder and decoder** with the limits in step 3.4.
**F5. Unit tests** in `tools/raid_mesh_test` (step 3.5): round trip, tampering, wrong key, oversize, bad version,
malformed input.

**Exit:** the largest allowed snapshot fits in 1100 bytes; every tampered variant is rejected.

## 9. Phase G: Stage 4, spreading snapshots over P2P

**G0. Prerequisite (blocker: a second device).** Only one phone is attached. Stage 4 needs the two-device Arena test
from `ONLINE_PVP.md` first. Decision for the user: provide a second device, or accept a host-side test that runs
several nodes in one process (step 4.6 already does that).

**G1.** Service channel in `TftfMesh` (step 4.1). Session code stays untouched.
**G2.** Bounded queue and worker thread (step 4.2): `_gate` is never held for signature checks or file access.
**G3.** Snapshot store with caps and eviction (step 4.3).
**G4.** `RD`/`RG`/`RS` list comparison within the limits (step 4.4).
**G5.** `RQ`/`RA` when short of opponents (step 4.5).
**G6.** Scenario tests (step 4.6), in one process through a local bootstrap node, including the Arena pairing check.

**Exit:** all scenarios pass; `Server/test_p2p_bootstrap.py` and the lockstep test still pass unchanged.

## 10. Phase H: Stage 5, raiding real players

- **H1.** `TftfRaidGlue` pushes store changes over loopback (step 5.1).
- **H2.** The server validates each offer again (step 5.2).
- **H3.** `find-match/raid` chooses offers in the tier, excluding shielded and own snapshots, filling with NPCs (5.3).
- **H4.** `activate-match/raid` fixes the chosen snapshot in the raid state (5.4).
- **H5.** Defenders built from the snapshot with the NPC path (5.5).
- **H6.** Assert raid fights never arm the Arena lockstep (5.6, `TftfNetGlue.cs:154`).

**Exit:** phone A places defenders and goes offline; phone B raids that base and sees A's name and bots. **Needs two
devices** (see G0).

## 11. Phase I: Stage 6, results back to the defender

- **I1.** The attacker signs the result and stores it in the outbox before sending (6.1).
- **I2.** Delivery by `RR` directly or by flood at most every 10 minutes; stop on `RK` or after 14 days (6.2).
- **I3.** Defender checks the signature and the snapshot version; deduplicates by raid id; answers `RK` (6.3).
- **I4.** Apply the result: losses and rewards from local config, with the abuse limits in 6.4.
- **I5.** Publish a new snapshot version when a shield starts (6.5).
- **I6.** Serve results through `historicalResults` (6.6).
- **I7.** Revenge (6.7).

**Exit:** A is offline during B's raid; when A returns, A sees the report once, the loss is applied once, B's outbox
entry clears, and A can take revenge. **Needs two devices.**

## 12. Phase J: Stage 7, lifecycle and text

- **J1.** Start the node when raids unlock and home first shows (7.1). Move `CachePath` setup out of
  `TftfNet.JoinOnline` (`TftfNet.cs:94`).
- **J2.** Pause raid timers in the background; one pass of outbox and store on return (7.2).
- **J3.** Status line on the raid screen, modelled on `TftfArenaOnline` (7.3).
- **J4.** New strings through the launcher's translations (7.4).
- **J5.** Leave `Hold` and the Arena search alone (7.5).

**Exit:** a fresh install with no network reaches an NPC raid and shows why; connecting later adds real opponents with
no restart.

## 13. Phase K: Stage 8, device tests, documentation, release

- **K1.** Full ARM64 build, then armeabi-v7a, using `run.sh fast` DLL replacement where the ABI allows (8.1).
  `TFTF_P2P_BOOTSTRAP` comes from the environment, never a file.
- **K2.** Run the test matrix (plan §Test matrix, 13 scenarios) on the A52 and a second device. Test 13 needs a
  measurement of battery and data over an hour.
- **K3.** `RAIDS.md` for players and maintainers; link from `ONLINE_PVP.md`; add the protocol to its wire tables;
  update `README.md` (8.3).
- **K4.** Any `compat310.py` patch uses one-line or signature anchors only, no decompiled blocks (8.4).
  See `no-decompiled-code-in-git`.
- **K5.** `CHANGELOG.md` entry; follow `RELEASE_CHECKLIST.md` (8.5).
- **K6.** **Commit and push — needs the user's go-ahead.** The working tree mixes raid work with files that are not ours
  (`README.md`, `TftfBot.cs`, `compat310.py`, `FEEDBACK_HANDOFF.md`). Propose the split, then commit. Push target:
  `gummygamer`, branch `development`.

## 14. Decisions needed from the user

| # | Decision | Needed by | Recommendation |
|---|---|---|---|
| D1 | How tutorials are answered on the phone: (a) server data, (b) empty blocklist, (c) script only | A3 | (a) |
| D2 | Permission to change `TftfBot.cs` (not ours) for the client log in B3, and how to handle its dirty state | B3 | Build additively on top, after asking |
| D3 | A second device for Stages 4–6 and 8 (the A52 is the only one attached) | G0 | Provide one, or accept single-process tests for Stage 4 |
| D4 | Commit and push timing and the commit split | K6 | After Phase C passes |
| D5 | Towers in Stage 2 (step 2.3) | E3 | Bosses first |

## 15. Risks

| Risk | Effect | Handling |
|---|---|---|
| Served-file mistakes (F5) repeat | Tests look valid but use the wrong file | Check `server.log` for `-> canned <file>` on every test (§2.2) |
| Client build wipes the phone `Server` folder | Lost raid state and logs | Pull `Server/logs/` first; push back after (B3) |
| Tutorials change between launches | Popups appear at new places | A2/A3 remove the popups from the test path |
| One device only | Stages 4–6 untested live | D3; host-side tests meanwhile |
| The fix for Q1 is in the client | More builds, slower loop | B1–B2 first; use B3 only if they fail |

## 16. Progress log

Update this section after each step. One line per step: date, step, result, evidence.

- 2026-11-02: plan written. Phase A not started. Phone holds diagnostic copies (see §1.3).
- 2026-11-03: A1 done. Phone `raid.lbl` and quest list pushed from repo; md5 matches.
- 2026-11-03: A3 done (option a, plan recommendation). Cause of F9: `tutorial/get-login-data` is answered by `tutorial_login_response()`, which listed only FTE, so the client started RaidsTutorial/ShieldTutorial/etc. Fix in `Server/fakeserver.lbl`: login data also marks ShieldTutorial, RaidsTutorial, BotPlacementTutorial, NavToQuestTutorial, FightResultsTutorial as completed. Test `test_tutorial_login_marks_raid_and_base_tutorials_completed` added; test_fakeserver 41/41 passed. Device: no tutorial/start-tutorial, complete-tutorial or start-branch request in `requests.log` on the 17:38 run.
- 2026-11-03: A4 partial. Found a popup the script did not handle: the "Bot em destaque" featured-bot panel. Its X button does not close it; KEYCODE_BACK does. `raid_nav.sh` now detects it by the green VER DETALHES pixel at (1206,708) and presses BACK in `dismiss_popups`. Still not a pixel check per step (the "screen changed" check remains).
- 2026-11-03: A2/B-entry result. Run 17:40 (script from home) reached `Enter RaidFlow` → TeamSelect → `ID_UI_QUESTS_EXPIRED` again (F3/F4 unchanged). Script exits 1 by design because of the PVPRAID Error line. Phase B (ID_UI_QUESTS_EXPIRED cause) is next, not started.
- 2026-11-03: session end. Server packing fix (`raid.lbl` in `SERVER_FILES`), seven tutorials completed in login data, `raid_nav.sh` rewritten to wait on `[TFTFBOT] screen=` names, `TftfRaidDiag.cs` added (raid set present at home). Cause of the CONTINUAR loop found: the 45 s auto-advance expiry (`PVPRaidSelectDataProvider.cs:52-64`). The open steps are in [`RAIDS_NEXT_STEPS.md`](RAIDS_NEXT_STEPS.md).
- 2026-11-03 (session 2): Step 0 of RAIDS_NEXT_STEPS done: force-stop, state files pushed back (`.fakeserver-xp`=100, level 2), md5 matches repo. Step 1.2 done: `AutoAdvanceRaidOpponentDisplayTime`=3600 added as `{"tuning":{...}}` to GET/POST tuning files; served canned (`GET__tuning.json` 287B in server.log). `raid_nav.sh` ASSALTOS tap moved from the card title (y=400, opened a tooltip) to the card's CONTINUAR button (y=526). Result: NOT passed. Run 2 reached the fight menu, then CONTINUAR gave "Um erro desconhecido ocorreu" (ERRO popup) with no request in `requests.log` and no PVPRAID/TFTFBOT lines in logcat. Logcat rolled over before it could be read. Next: a run that dumps `logcat -d` right after the popup appears. Test suite: `test_raid` 22/23; the failing `test_310_missions_response_serves_an_enabled_raid_config` expects tier `maxRating == -1`, the served file has 2000000000 (pre-existing, not touched this session).
- 2026-11-03 (session 2, cont.): Q3 decided by the user: keep the completed tutorials in the shipped login data (P2P plan step 0.14 closed). Step 2 cause found and fixed, server only. Run 3 (logcat captured to file) showed `[RAIDDIAG]` at 18:43:59 with the raid set present and all three lookups found, and no DB change before the error, so Result B. The error came from `GetUserProfile` (`PVPRaidFlow.cs:1383-1386`) → `QuestsManager.GetQuestDetails(layout)`, where the layout for a raid summary is `Layout("quests", category, id)` (`Quests/Summary.cs:54`; `sid` is an alias of `id`, `Legacy/QuestSummary.cs:26`). The active raid's summary had `id = "raid.N"`, but the set's summaries are `raid-npc-N`. Fix in `Server/raid.lbl`: the active quest and instance `data` summaries carry `id = raid-npc-N` (`raid_npc_set_id`), while `qid` stays `raid.N`. Test `test_active_quest_layout_names_the_npc_set_summary` added. Device run 5 (logcat `/tmp/rdiag/s5/logcat.txt`, 18:53:31): `Enter RaidFlow`, `Current State: TeamSelect`, and no `ID_UI_QUESTS_EXPIRED`. Phase B exit met on ARM64. No client build was needed. `raid_nav.sh` still reports failure at the ASSALTOS step, because the raid path logs no `PVPRaidSelect` screen name; that is Step 1/Step 4 work. Not done: team select → board (C1), and the flow is left at the fight menu after the run.
