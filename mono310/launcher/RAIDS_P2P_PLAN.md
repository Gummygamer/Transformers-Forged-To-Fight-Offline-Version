# Plan: raids over the Arena's P2P network

Status on 2026-11-02: Stage 0 is recorded below. Stage 1 is built on the server side and tested offline; its device exit is
not run, because no phone was attached. Stages 2 to 8 have not started. The per-stage state is in
[Status by stage](#status-by-stage). Marks follow [`DOC_STYLE.md`](../../DOC_STYLE.md).

Read [`ONLINE_PVP.md`](ONLINE_PVP.md) first: this plan reuses the network it describes and does not restate it.

## Table of contents

- [Summary](#summary)
- [Stage 0 findings](#stage-0-findings)
- [Status by stage](#status-by-stage)
- [What a raid is in the client](#what-a-raid-is-in-the-client)
- [What exists today](#what-exists-today)
- [Design decisions](#design-decisions)
- [Architecture](#architecture)
- [Wire protocol](#wire-protocol)
- [Step by step](#step-by-step)
- [Test matrix](#test-matrix)
- [Risks](#risks)
- [Out of scope for the first release](#out-of-scope-for-the-first-release)

## Summary

A raid is **asynchronous**: the attacker walks a board made from another player's base and fights that player's defenders
under AI control. The defender plays no part and may be offline **[📄 binary]**. So raids do not need the Arena's lockstep.
They need the network for three other things:

1. spreading each player's signed defence snapshot, so that attackers can find bases whose owners are offline;
2. carrying the signed raid result back to the defender, possibly much later;
3. telling a game when a defender it owes a result to is back online.

The work runs in this order, and each stage ends with something that can be tested:

| Stage | Result | Size |
|---|---|---|
| 1 | A complete raid against an NPC, with no network at all | L |
| 2 | Players place defenders on their base, and the placement persists | M |
| 3 | Stable player identity and signed snapshots | M |
| 4 | Snapshots spread through the P2P network, tested without Unity | M |
| 5 | Raids against real players' snapshots | M |
| 6 | Results reach the defender: reports, losses, shield, revenge | M |
| 7 | Node lifecycle, status text, translations | S |
| 8 | Device tests, documentation, release | M |

Stage 1 comes first on purpose. No part of the raid path has ever run against this server, so it would otherwise be
impossible to tell a network fault from a contract fault.

The bootstrap node needs no change, and raid traffic never reaches it. Its spending guard is affected only by more games
sending heartbeats (step 7.1).

## Stage 0 findings

Stage 0 is read-only. The sources are the 3.1.0 decompile on the external drive,
`/run/media/darabat/24f15d44-9691-4cf4-8b52-bc03f1e3693f/tftf-310-recompile/ripped/ExportedProject/Assets/`. `AC/` below is
`Scripts/Assembly-CSharp/` and `FP/` is `Plugins/Assembly-CSharp-firstpass/`. The `$M/tftf-310-cleanroom/` root named in the
next section is not on this machine. Use the paths here instead.

| # | Finding | Mark | Citation |
|---|---|---|---|
| 0.1 | The raid config is the missions config entry whose mode is `pvp`. The client keys `configs` by mode name and builds `RaidConfig` from `pvp`. The plan's search for `raidConfig` was looking for the wrong key | [📄 binary] | `AC/Legacy/QuestsConfig.cs:91-146`, `FP/EB/Missions/ConfigManager.cs:54-80` |
| 0.2 | The server's default `missions_config.json` nests `quests.ave`. No 3.1.0 code reads that nesting, so the key is inert there. Left unchanged | [📄 binary] | same as 0.1 |
| 0.3 | `RaidConfig` keys: `enabled`, `attackTeamSizeMin`, `attackTeamSizeMax`, `maxConsumablesPerPlayer`, `leaderboardId`, `revengeValidTime`, `maxRevengeChain`, `bucketSettings`, `DisabledLevels`. Tier keys: `id`, `attackWinBonus`, `findMatchCost`, `refreshMatchCost`, `minRating`, `maxRating`. The plan's list is correct | [📄 binary] | `AC/Quests/RaidConfig.cs:188-196`, `AC/Quests/RaidTierConfig.cs:117-122` |
| 0.4 | Find: `POST /matches/find-match/raid` returns `matches`, `matchID`, `expiry`. Entries read `uid` (`SparxId`), `n`, and the rest of the plan's list | [📄 binary] | `FP/EB/Sparx/MatchAPI.cs:159`, `FP/EB/Sparx/FindMatchResult.cs:58-60`, `AC/AvailableRaidMatch.cs:133-148` |
| 0.5 | Activate: the request posts `team` (a list) with the base fields. The response is read by `DeserializeActiveQuests`: `activeQuests` keyed by quest id, each with `instances` | [📄 binary] | `AC/ActivateRaidMatchData.cs:38-42`, `AC/Legacy/QuestDB.cs:641-674` |
| 0.6 | Correction: raid fights use the match type `quests_fight` for both activate and resolve, not a raid-specific type. Stage 1.6 must recognise a raid by its active quest id | [📄 binary] | `AC/Quests/ActiveQuest.cs:641`, `:655` |
| 0.7 | Join: `POST /quests/quest-join/<qid>` with `tm0..`. Its reply goes through `ProcessActionResultsAndProgression`, so it carries `results` and `progression` | [📄 binary] | `AC/Legacy/QuestsAPI.cs:463-475`, `AC/Legacy/QuestsManager.cs:319-345` |
| 0.8 | Raid end: `RaidManager.Async("raid-end")` reads `raidResult` from the payload object. The component name `RaidManager` is inferred from the class-name convention that `QuestsManager` and `PVPManager` follow | [📄 binary], component name [⚠ inferred] | `AC/Quests/RaidManager.cs:167-184`, `:229-240` |
| 0.9 | `historicalResults` and `activeQuests` are read from the raid autorefresh data. Which level of the group carries them is not in the code | [📄 binary], placement [⚠ inferred] | `AC/Quests/RaidManager.cs:187-226` |
| 0.10 | Claim: replies carry `redeemers` and `claimedIds` | [📄 binary] | `AC/Quests/RaidManager.cs:263-290`, `AC/Quests/RaidAPI.cs:60-66` |
| 0.11 | `RaidResult` keys match the plan's list. `journal` is read as an array | [📄 binary] | `AC/Quests/RaidResult.cs:364-399` |
| 0.12 | `revengeValidTime` is added to `endTime`. Both are seconds, which is inferred from the field names and the integer type | [📄 binary], unit [⚠ inferred] | `AC/PVPRaidReportDetailsData.cs:524-531` |
| 0.13 | The local user id is served as `1000000000001`. The plan's citation `Server/fakeserver.lbl:1497` is stale: the value lives in `Server/gamedata.lbl`, `local_uid` | [📡 served] | `Server/gamedata.lbl` |
| 0.14 | Tutorial gates: **decided 2026-11-03, keep the completed tutorials in the shipped login data** (user decision). `fakeserver.lbl` marks ShieldTutorial, RaidsTutorial, BotPlacementTutorial, NavToQuestTutorial, FightResultsTutorial, ModTutorial and RelicsTutorial completed; device run 18:05 made no `start-tutorial` request. | done | `AC/PVPRaidFlow.cs:1356` |

Corrections to the plan body that these findings require:

- "Raid config: not found" under [What exists today](#what-exists-today) is wrong. The config exists under the `pvp` mode (0.1).
- "Routes ... not in the server" was true before Stage 1. It is no longer true; see [Status by stage](#status-by-stage).
- Stage 1.4 named theme `base` for raid boards. Stage 1 uses theme `primordial` instead, the theme the story boards render with. `base` has not been tried on a quest board [⚠ inferred].

## Status by stage

| Stage | State | Where |
|---|---|---|
| 0 | Done (0.14 decided 2026-11-03: keep completed tutorials) | [Stage 0 findings](#stage-0-findings) |
Device status, 2026-11-02 (A52, 3.1.0 build; stage 1 device exit not yet passed):

- The raid unlocks at level 2 (`fightLanding_raid` tuning key; verified at NVL 2 and NVL 4).
- The raid list shows the three NPC opponents. Choosing one and confirming reaches the team-select step.
- Team select fails with `ID_UI_QUESTS_EXPIRED`: `Legacy/QuestsManager.GetQuestDetails(category, qid)` finds no set in `_questDB.AvailableSets` for the NPC layout (`category "raid"`, `id "raid-npc-N"`). The server serves the raid set in `quests/quest-list` (fetched at launch), and each summary carries `id`, `sid`, `category`, `setId`. The cause is not yet found. Logcat shows no parse exception or "Error deserializing".
- Next: confirm which quest sets the client holds after load. That needs client instrumentation (hook or log), not more server data changes.
- `mono310/launcher/tools/raid_nav.sh` navigates title or home to the raid confirmation in about a minute.

| 1 | Server side built and tested offline. 1.1 to 1.10 are in place; 1.11 (the TftfBot raid path) is not. The device exit has not run, because no phone was attached | `Server/raid.lbl` and the raid routes in `Server/fakeserver.lbl`; tests `Server/test_raid.lbl` (23 rules) and `Server/test_raid_routes.lbl` (11 routes); the config is in `Server/responses/GET__autorefresh_missionsconfig_refresh.mono310.json` and its POST twin |
| 2 to 8 | Not started | |

Stage 1 choices the plan left open. Each is a decision to review, not a finding:

- A loss ends the raid. The attacker's team is treated as beaten by one defender, so a raid has one life. Plan 1.7 allowed
  "attacker team dead" as an end condition; one loss is the simpler rule that stage 1 can test.
- Raid tiers carry empty reward lists, so claims grant nothing. Plan 1.1 ("choose rewards") is not done.
- One raid at a time. Starting a second raid records the first as a quit.
- The board is one path of tiles, one per defender, and the last defender is the final boss. The board uses theme
  `primordial`, time of day 0.
- Raid state is one JSON file, `.fakeserver-raid.json`, in the server's log directory. It is separate from the story
  positions, so story tests do not change.
- The raid config is served only in the 3.1.0 canned missions response (`.mono310.json`), so 2.0.2 receives none. The
  dynamic `grouprefresh` missions update does not carry it, and `grouprefresh` URLs carry no `cl=` marker, so the profile
  cannot gate that path. Whether the 3.1.0 client keeps `pvp` through a later `grouprefresh` is [⚠ inferred], to check on the
  device.
- The in-app server serves a baked payload, so server edits reach a phone only after the payload is rebuilt [⚠ inferred].
  Check the payload build before the device test.

Run the tests with `Server/run_tests.sh`, or one file at a time with `legible run Server/test_raid.lbl`.

## What a raid is in the client

Paths in this section are relative to `$M/tftf-310-cleanroom/ShellProject/Assets/` on the external drive, as
[decompiled source is never committed](../../COMPLIANCE.md). `AC` stands for `Scripts/Assembly-CSharp`, and `FP` for
`Plugins/Assembly-CSharp-firstpass`.

**Flow.** `PVPRaidFlow` steps through opponent selection, team selection, joining, loading the board and the results
screen **[📄 binary: `AC/PVPRaidFlow.cs:1511` `StartRaidFlow`, `:1136` `FinishQuest`]**.

| Step | Request | Citation **[📄 binary]** |
|---|---|---|
| Find opponents | `POST /matches/find-match/raid` | `FP/EB/Sparx/MatchAPI.cs:38` |
| Pick one | `POST /matches/activate-match/raid` (`matches`, `midx`, `id`) | `MatchAPI.cs:75`, `ActivateLeaderboardMatchData.cs:57-60` |
| Add the team | `POST /quests/quest-join/<qid>` | `AC/Legacy/QuestsAPI.cs:180` |
| Walk the board | `quest-moveto`, `quest-movedir` | `QuestsAPI.cs:200`, `:253` |
| Fight | `POST /matches/resolve-match/<type>` | `MatchAPI.cs:132`, `AC/Quests/ActiveQuest.cs:284` |
| Give up | `POST /quests/quest-quit/<qid>` | `QuestsAPI.cs:269` |
| Collect rewards | `POST /raid/claim/<id>`, `POST /raid/claim/defense` | `AC/Quests/RaidAPI.cs` |
| Place defenders (own base) | `quest-placehero/<qid>` (`hero`, `x`, `y`), `quest-swapheroes/<qid>` | `QuestsAPI.cs:350`, `:371` |

**Gates.** The feature needs the raid config's `enabled` flag **[📄 binary: `PVPRaidFlow.cs:1365`]**, and the home-screen
entry needs `BotPlacementTutorial` and `RaidsTutorial` complete or tutorials skipped **[📄 binary: `PVPRaidFlow.cs:1356`]**.
Starting a raid also checks the attacker's shield, name, cost and active bots **[📄 binary: `PVPRaidFlow.cs:1558`, `:1688`]**.

**Wire keys.** **[📄 binary]** `AvailableRaidMatch` was compared and is identical in 2.0.2 and 3.1.0. The other classes
were read in the 2.0.2 copy, so step 0.1 re-checks them in 3.1.0.

| Object | Keys |
|---|---|
| Find result | `matches`, `matchID`, `expiry` |
| Match entry | `uid` (**negative = NPC**), `s`, `n`, `t`, `pr`, `sd` (medals), `rc` (raid chips), `bs` (base strength), `td`, `tt`, `pi`, `layout` |
| Raid config | `enabled`, `attackTeamSizeMin`, `attackTeamSizeMax`, `maxConsumablesPerPlayer`, `leaderboardId`, `revengeValidTime`, `maxRevengeChain`, `bucketSettings`, `DisabledLevels` |
| Tier (`bucketSettings`) | `id`, `attackWinBonus`, `findMatchCost`, `refreshMatchCost`, `minRating`, `maxRating` |
| Raid result | `_id`, `qid`, `category`, `s` (`e` expired, `q` quit), `layout`, `attacker`, `defender`, `attackerProfile`, `defenderProfile`, `attackerWon`, `attackerRewards`, `defenderRewards`, `rewardsClaimed`, `claimableRewards`, `startTime`, `endTime`, `targeted`, `journal` |
| Profile | `name`, `tag`, `mdl`, `bid`, `pi` |
| Journal entry | `action` (`start`, `join`, `fightwon`, `fightlost`, `move`), `time`, `position`, rewards, `updates.attackers[]` / `updates.defenders[]` (`key`, `hp`); `start` carries `defenseTeam`, `join` carries `attackTeam` |
| Team entity | `key`, `entityType`, `position`, `rank`, `level`, `sig_lvl`, `rating`, `hp` |

**Other facts the design relies on. [📄 binary]**

- The raid is an active quest with category `raid` (`ActiveQuest.cs:71`), so the existing quest board and fight paths
  apply. Defenders are the `boss` and `tower` entities on map tiles.
- Before the board loads, the flow asks for base attributes of each defender's blueprint, rank, level and signature level.
  Both sides can therefore build a defender from four small values, as the Arena does.
- The server ends a raid with a `raid-end` async message carrying `raidResult` (`Quests/RaidManager.cs`).

## What exists today

| Part | State |
|---|---|
| P2P network, bootstrap node | Built and tested without phones; bootstrap deployed **[👁 live, PC only: `ONLINE_PVP.md` Status]** |
| Arena over P2P | Not yet run on two devices **[`ONLINE_PVP.md` Limits]**. Stage 4 relies on the same links, so run that two-device test before stage 4 |
| Raid refresh | Serves an empty object **[📡 served: `Server/responses/GET__autorefresh_raid_refresh.mono310.json`]** |
| Raid config | Served under the `pvp` mode in the 3.1.0 canned missions response, with the raid keys added in stage 1 **[📡 served; 📄 binary, [Stage 0](#stage-0-findings) 0.1]** |
| Routes | Stage 1 adds `find-match/raid`, `activate-match/raid`, `quest-join`, `quest-movedir`, `quest-quit`, the raid fight, `raid/claim` and the raid refresh, in `Server/raid.lbl` and `Server/fakeserver.lbl`. `quest-placehero` and `quest-swapheroes` are still missing (stage 2) |
| Fight results | Raid fights resolve through `resolve-match/quests_fight` when the quest id names the raid in progress; story fights are unchanged **[`Server/fakeserver.lbl`, `raid_dynamic`]** |
| Base | Served and rendered with authored buildings **[`Server/gamedata.lbl:2692-2697`]** |

Network properties that shape the design **[📄 binary, this repository]**:

| Property | Citation | Consequence |
|---|---|---|
| One session at a time (`_peer`, `_sid`), and `Seek` ends it | `shell/Tftf/TftfMesh.cs:89`, `:118` | Raid traffic must stay out of the session, or it would break an Arena pairing |
| Node id is new on every run | `TftfMesh.cs:142` | Useless as a player identity; raids need their own |
| Any game can claim any id; nothing is authenticated | `ONLINE_PVP.md` Limits | Snapshots and results must be signed |
| Oversized datagrams are dropped silently (1200 bytes), ASCII only | `TftfMesh.cs:32`, `:253`, `:266-267` | Encoders must check their own size |
| Unknown verbs are ignored, and routed messages are forwarded before being read | `TftfMesh.cs:541`, `:630-631`, `:673` | 0.13 games carry raid traffic and are not disturbed by it |
| Handling runs under `_gate` on the network thread | `TftfMesh.cs:248-253` | No file access or signature checks inside handlers |
| Only the Arena screen starts a node | `TftfArenaOnline.cs:41`, `ONLINE_PVP.md` | Raids need a node outside the Arena |
| Local players may share one user id: the session's, defaulting to `1000000000001` | `Server/gamedata.lbl` `local_uid` (served as `1000000000001`, Stage 0 0.13); the client's use of it is still re-checked in step 0.1 **[⚠ inferred]** | Remote players need ids of their own, translated on arrival |
| The server is a separate local process, bound to loopback | `shell/Tftf/TftfServer.cs:70` | Mesh code and server state need a bridge |

## Design decisions

1. **Raids stay asynchronous, with AI defenders.** That is how the client works, and players are rarely online together.
   Nothing about the fight runs over the network, so it avoids the Arena's unproven lockstep entirely.
2. **The local server stays the authority for its own player.** It builds the board and owns resources, quest state and
   reports. The network layer knows no game rules and only moves signed, opaque records.
3. **Offers come from a local store, not a live search.** Each game keeps other players' snapshots and answers
   `find-match` from them instantly, so a defender's being offline does not matter and the screen never waits on the
   network.
4. **Snapshots spread by neighbours comparing lists, not by flooding.** With 1000 players, flooding a 1.1 KB snapshot
   every 5 minutes costs each phone at least 3.7 KB/s, and several times that with duplicates **[⚠ inferred]**. Comparing
   lists costs at most about 1 KB/s per phone (step 4.4) and spreads gradually.
5. **A snapshot fits one datagram.** Bases share one authored layout **[📄 binary: `Server/gamedata.lbl:2697`]**, so a
   snapshot carries only placements, about 16 of them within the limit. No chunking protocol. Whether a future custom base
   layout would need one is left for later.
6. **Each install has one RSA-1024 key pair**, from .NET's own crypto, with no new dependency. The player id is derived
   from the public key, so an id cannot be claimed without its key. RSA-1024 is weak by modern standards but sufficient
   for game records. Step 3.1 confirms that it works under IL2CPP.
7. **Each phone keeps its own economy.** Rewards are never transferred. The attacker's server computes the attacker's
   rewards, and the defender's server computes the defender's losses from its own config. Claimed amounts in a record
   are ignored.
8. **There is always something to raid.** NPC opponents fill the list up to its size, so the mode works on an empty
   network, or with no network at all.
9. **Concurrent raids on one base are allowed.** There is no global lock without a central server. Each attacker fights
   its own copy; results merge when they arrive.
10. **Stale snapshots stay raidable for 7 days.** A newer signed version replaces an older one everywhere it reaches.

## Architecture

```text
 game (PVPRaidFlow) ── HTTP ──> local server (fakeserver.lbl)
                                  │  raid state, offers, reports, own snapshot
                                  │  loopback routes /tftf/raid/*
                                  ▼
                       TftfRaidNet (shell/Tftf, no game types)
                         key pair, snapshot store, outbox
                         signing and checking on a worker thread
                                  │  service channel
                                  ▼
                       TftfMesh ── UDP ── other games
```

| Part | File | Assembly |
|---|---|---|
| Service channel | `shell/Tftf/TftfMesh.cs` (small additions) | first-pass |
| Identity, store, protocol | `shell/Tftf/TftfRaidNet.cs` (new) | first-pass |
| Start, stop, bridge calls | `shell/TftfMain/TftfRaidGlue.cs` (new) | Assembly-CSharp |
| Raid rules and routes | `Server/raid.lbl` (new), routed from `Server/fakeserver.lbl` | server |
| Raid config, tiers, NPCs | `Server/gamedata.lbl` | server |
| Network test without Unity | `tools/raid_mesh_test/` (new, modelled on `tools/net_lockstep_test/`) | test |
| Server tests | `Server/test_raid.lbl` (new) | test |

Files on the phone, all in the app's data folder and written atomically as `TftfMesh.SaveCache` does:

| File | Contents | Limit |
|---|---|---|
| `tftf_raid_key.txt` | The private key. Never sent, and excluded from any logs | 1 |
| `tftf_raid_store.txt` | Other players' snapshots, newest first | 256, 7 days |
| `tftf_raid_outbox.txt` | Results not yet acknowledged | 64, 14 days |
| Server state files | Defenders, active raid, results, applied result ids | 50 results |

## Wire protocol

All raid messages use the existing `TF1` datagram, so 0.13 games pass them on or ignore them. Every record begins with
protocol version `1`, and games skip versions they do not know.

**Link messages** (to direct neighbours only):

| Datagram | Meaning | Limit |
|---|---|---|
| `RD <uid>:<ver>,...` | The newest snapshots this game holds, own first | One per link per 30 s, recently changed first, under 1100 bytes |
| `RG <uid>` | Send me this snapshot | Four per link per minute |
| `RS <snapshot>` | The snapshot | Answers `RG` only |

**Routed messages** (inside the existing `R` envelope, which costs about 50 bytes):

| Body | Destination | Meaning |
|---|---|---|
| `RQ <rating> <wanted>` | `*`, ttl 4 | The store is short of opponents; at most one per 60 s while the raid screen is open |
| `RA <snapshot>` | The asker | Answer to `RQ`, after a random delay of up to 2 s, at most 4 per asker |
| `RR <result>` | `*`, ttl 8, or the defender's last known node | A raid result; repeated by the attacker (step 6.2) |
| `RK <raid id> <defender uid> <signature>` | The attacker | The defender has stored the result; the attacker deletes it |

**Records** (fields are URL-escaped and space-separated, signatures cover everything before them):

```text
snapshot: 1 <uid> <version> <shield until> <name> <tag> <medals> <content> <defenders> <public key> <signature>
defender: <blueprint>:<rank>:<level>:<sig>:<x>:<y>:<boss|tower>
result:   1 <raid id> <defender uid> <attacker uid> <snapshot version> <0|1 attacker won> <end time>
          <hp updates> <attacker name> <public key> <signature>
```

- `uid` is 48 bits of the SHA-256 of the public key, as a positive number, never the local id. A record whose `uid` does
  not match its key is dropped.
- `version` is the owner's Unix time at publication. Only a higher version replaces a stored one.
- `content` is a short hash of the server's game data. A store keeps other versions, but offers only snapshots whose
  content matches, so nobody is offered a bot their game does not have.
- The sender's own entry in `RD` is `<uid>:<ver>:<node id>`, with its node id for this run, so results can go straight to
  a defender who is online.

## Step by step

### Stage 0: confirm the contract

Read-only, and it keeps later stages from guessing.

- **0.1** Re-read the raid classes in 3.1.0 and list differences from the keys above. `PVPRaidFlow` has real differences
  (2005 lines against 1830 in 2.0.2). Confirm the local user id and how `Id` is parsed.
- **0.2** Trace from the code: which config key carries `raidConfig` (`AC/Legacy/QuestsConfig.cs:55`); the match type in
  `ActiveQuest.ResolveMatch`; the shape of the `activate-match/raid` response read by `QuestDB.DeserializeActiveQuests`;
  how `PVPRaidFlow.GetUserProfile` (`PVPRaidFlow.cs:1370`) fetches a foreign user; how the `raid-end` async names its
  component; the board's use of `layout`; shield fields in the user profile.
- **0.3** Decide how `RaidsTutorial` and `BotPlacementTutorial` complete. Reuse the existing tutorial blocklist unless
  that hides the screens.
- **0.4** Record the findings at the top of this document, each with a mark.

**Exit:** every key that stage 1 serves has a citation.

### Stage 1: a complete raid against an NPC, offline

Server only, so most iterations can be pushed to the phone without a rebuild.

- **1.1** Serve the raid config with `enabled: true`, sizes, tiers and `DisabledLevels`, with no costs at first. Choose
  rewards and tier ratings in `gamedata`.
- **1.2** Add `Server/raid.lbl` with its state file: active raid, results, match list and expiry.
- **1.3** `find-match/raid`: return three NPC entries with negative `uid`, `layout`, display fields and `expiry`.
- **1.4** `activate-match/raid`: build the active quest with category `raid`, reusing the code behind quest boards. Place
  the NPC's defenders as `boss` and `tower` entities on the base map with `todIndex` 0 and theme `base`
  **[📄 binary: `BOARD_AUTHORING.md`, Raids and your base]**. Add a list of NPC defence teams.
- **1.5** `quest-join`, then route `quest-moveto`, `quest-movedir` and `quest-quit` to the raid when the `qid` is one.
- **1.6** `resolve-match`: extend `resolve_match_response` to raid fights. Update defender and attacker health, add journal
  entries, and return the `actions` that `FightFlow` reads for a win **[📄 binary: 2.0.2 `FightFlow.cs:610`]**.
- **1.7** End of raid (last defender beaten, attacker team dead, expiry or quit): build the `raidResult`, send `raid-end`,
  and add it to `historicalResults` in `autorefresh/raid/refresh`.
- **1.8** `/raid/claim/<id>` and `/raid/claim/defense`: apply rewards and return `redeemers` and `claimedIds`.
- **1.9** Restart cases: leaving and reopening the app mid-raid returns to the active raid, and a stored `fightId` resolves
  as unknown (`PVPRaidFlow.cs:845`).
- **1.10** Add `Server/test_raid.lbl` for each route, using the existing fixtures.
- **1.11** Add a raid path to `TftfBot` (screen waits and Jev-driven fights) so that device checks can repeat it. Follow
  the phone drive guard: check the foreground app and expected screen before every tap.

**Exit, on the A52, ARM64 first:** FIGHT → RAIDS → pick an NPC → team → board → beat every defender → results → claim, and
a second raid where the attacker loses. Then port to armeabi-v7a.

### Stage 2: defenders on the player's base

- **2.1** Implement `quest-placehero` and `quest-swapheroes` for the base quest, persisted across restarts.
- **2.2** Serve placed defenders as entities in `/base/active`. The `DefenderSelectPopup` and `BaseEditNodePopup` screens
  are the client's editor **[📄 binary]**.
- **2.3** Decide with step 0.2's findings whether towers ship now. If towers need mod data the server lacks, ship bosses
  first and leave `tt` empty.
- **2.4** Shield: serve expiry in the profile, grant it for new players, and void it when the player attacks
  (`PVPRaidFlow.cs:1688`).
- **2.5** Compute `bs`, `td` and `pi` from placed defenders.

**Exit:** placing, swapping and removing defenders survive an app restart and show on the base.

### Stage 3: identity and snapshots

- **3.1** Before anything else, prove in a tiny IL2CPP ARM64 build that RSA key generation, signing and checking work. If
  they do not, switch to a small audited managed Ed25519 file, which also shrinks each record by about 250 bytes.
- **3.2** `TftfRaidNet.Identity`: create the key on first use, off the main thread (generation can take noticeable time),
  and derive `uid`.
- **3.3** Add loopback routes to `Server/raid.lbl`: `GET /tftf/raid/snapshot-fields` returns unsigned fields and bumps
  the version when defenders change. Only requests from this phone reach them **[📄 binary: `TftfServer.cs:70`]**.
- **3.4** Encoder and decoder with strict limits on field lengths and defender count. An encoder over 1100 bytes reports an
  error and drops its lowest-rated defenders rather than sending nothing.
- **3.5** Unit tests in `tools/raid_mesh_test`: round trip, tampered fields, wrong key, oversize, bad version, malformed
  input.

**Exit:** a snapshot of the largest allowed base fits, and every tampered variant is rejected.

### Stage 4: spreading snapshots

- **4.1** Add a service channel to `TftfMesh`: `ServiceLink(linkId, text)` and `ServiceMessage(src, body)` callbacks, and
  `SendService(dst, body)`, `BroadcastService(body, ttl)`, `ToNeighbour(linkId, text)`, `LinkIds()`. Add `R*` cases to
  `Handle` and `Message`. Session code stays untouched.
- **4.2** Handlers only copy into a bounded queue (256 items, oldest dropped) and return. A worker thread checks
  signatures and touches files, so `_gate` is never held for either.
- **4.3** Snapshot store: keeps the newest version per `uid`; evicts expired first, then the oldest; at most 8 entries per
  sender IP while learning from unverified newcomers **[⚠ inferred: needs review once real traffic exists]**.
- **4.4** Comparing lists: send `RD`, pull with `RG`, answer with `RS`, within the limits above. The ceiling per phone is
  about 0.3 KB/s for lists and 0.6 KB/s for pulls at 8 links **[⚠ inferred]**.
- **4.5** `RQ` and `RA`, sent only while the store is short of opponents.
- **4.6** `tools/raid_mesh_test` scenarios with several nodes in one process (`TestId`, `TestBlock`), through a local
  bootstrap node:
  - a snapshot reaches a node two hops away, then remains after its owner stops;
  - a newer version replaces an older one everywhere;
  - forged snapshots do not spread;
  - an Arena pairing's lines remain complete and in order during raid traffic;
  - a node with no raid support (simulated by ignoring `R*`) still forwards;
  - measured bytes per node stay within 4.4's ceilings.

**Exit:** all scenarios pass, and the lockstep test and `Server/test_p2p_bootstrap.py` still pass unchanged.

### Stage 5: raiding real players

- **5.1** `TftfRaidGlue` pushes store changes to the server over loopback (`POST /tftf/raid/offers`), and fetches own
  snapshot fields on start and after defenders change.
- **5.2** The server validates each offer again: blueprints exist, ranks and levels are in range, position is a real node.
  Invalid offers are dropped. Rating is computed from local data, never trusted.
- **5.3** `find-match/raid`: choose from offers in the player's tier (`minRating`, `maxRating`), excluding shielded
  defenders and the player's own snapshot, and fill up with NPCs.
- **5.4** `activate-match/raid`: fix the chosen snapshot inside the raid state, so a newer version arriving mid-raid does
  not change the board. Serve `GetUserProfile` from the snapshot for a foreign `uid`.
- **5.5** Build defenders with the same base-attribute path as NPCs.
- **5.6** Check the Arena glue stays out of the way: `TftfNetGlue.FilterAI` turns AI off while an Arena fight is pending
  (`TftfNetGlue.cs:154`). A raid started while paired must keep its AI, so assert that raid fights never arm the lockstep.

**Exit:** phone A places defenders and goes offline; phone B, having received the snapshot, raids that base and sees A's
name and bots.

### Stage 6: results back to the defender

- **6.1** At raid end the attacker's server hands the unsigned result to `TftfRaidGlue`, which signs and stores it in the
  outbox before sending anything.
- **6.2** Delivery: send `RR` directly if the defender's current node is known. Otherwise flood it at most once per
  10 minutes, plus once when an `RD` shows the defender has a newer session. Stop on `RK` or after 14 days.
- **6.3** Defender: after checking the signature, confirm the `snapshot version` is one it published in the last 7 days
  (keep a list). Store it, deduplicate by raid id, and answer `RK` even for a duplicate.
- **6.4** Apply the result to the defender's server, computing losses and defence rewards from local config. Limits against
  abuse, since keys are free to create: at most 3 losses applied per 24 hours, no more than 1 per attacker id, and a
  shield starting at the first loss. All results still appear in the report, up to 50.
- **6.5** Publish a new snapshot version when a shield starts, so attackers stop being offered that base.
- **6.6** Serve results through `historicalResults`, with the attacker's foreign `uid` and the local player as defender, so
  `isDefenseEntry` holds. Badges follow from `RaidBadgeManager`, and `PVPRaidReport` opens from the base building.
- **6.7** Revenge: `find-match` with an opponent id offers that player's stored snapshot, within `revengeValidTime` and
  `maxRevengeChain`. If it is not in the store, query with `RQ`, then report "not available" (`notavailable` is the error
  the client understands).

**Exit:** A is offline during B's raid. When A returns, A sees the report once, the loss is applied once, B's outbox entry
is cleared, and A can take revenge.

### Stage 7: lifecycle and player-facing text

- **7.1** Start the node when raids are unlocked and the home screen first shows, not just in the Arena. Move the
  `CachePath` setup out of `TftfNet.JoinOnline` (`TftfNet.cs:94`) so that both paths use it. More games now send heartbeats:
  1000 players cost about 4 KB/s, far under the guard's ceiling **[📄 binary: `ONLINE_PVP.md`, Rate ceiling]**.
- **7.2** Pause the raid timers when the app is in the background, with one pass through outbox and store on return.
- **7.3** Add a status line to the raid screen, modelled on `TftfArenaOnline`: online with how many opponents are stored,
  or offline and NPCs only. An unset `TFTF_P2P_BOOTSTRAP` means NPCs only.
- **7.4** Route every new string through the launcher's 0.13 translations.
- **7.5** Leave `Hold` and the Arena search behaviour alone: the Arena's queue must not start because the node is up.

**Exit:** a fresh install with no network reaches an NPC raid and shows why; connecting later adds real opponents with no
restart.

### Stage 8: device tests, documentation, release

- **8.1** Build as usual: ARM64 first with a full build, then armeabi-v7a, using `run.sh fast` DLL replacement wherever
  the ABI allows. `TFTF_P2P_BOOTSTRAP` comes from the environment and is never written into a file.
- **8.2** Run the [test matrix](#test-matrix) on the A52 and a second device. The emulator cannot be the second device if
  the launcher's first-run conversion still crashes there, so check this first.
- **8.3** Move raid material into a new `RAIDS.md` for players and maintainers, with this plan's decisions marked by
  what was observed. Link it from `ONLINE_PVP.md`, add the protocol to its wire tables, and update `README.md`.
- **8.4** Patches in `compat310.py`, if any are needed, use one-line or signature anchors only; no decompiled blocks enter
  the repository.
- **8.5** Add a `CHANGELOG.md` entry, follow `RELEASE_CHECKLIST.md`, push to `gummygamer` `development`.

## Test matrix

| # | Scenario | Expected |
|---|---|---|
| 1 | No network, fresh install | NPC raid completes; status says offline |
| 2 | Win, loss, quit, timer expiry | Correct result screen and report each time |
| 3 | App killed mid-fight and mid-board | Returns to the raid; no double reward |
| 4 | Both phones online, different mobile networks | Snapshots spread; raid works; result arrives within a minute |
| 5 | Defender offline for the raid, returns the next day | Report and loss applied exactly once |
| 6 | Attacker offline after the raid, both return later | Outbox still delivers |
| 7 | Two attackers raid one base at once | Both reports; loss limit holds |
| 8 | Paired in the Arena, then raid and return | Arena pairing intact; raid AI active; Arena fight still in lockstep |
| 9 | Defender changes the team during a raid | Raid continues on the old team; new offers show the new one |
| 10 | Tampered snapshot or result from a test client | Dropped, not spread, not applied |
| 11 | Defender's bot unknown to the attacker's content | Not offered; no crash |
| 12 | Upgrade from 0.13, mixed with 0.13 games | Old games forward and ignore; Arena unaffected |
| 13 | One hour idle on mobile data | Traffic within stage 4's ceilings; report the measured battery and data use |

## Risks

| Risk | Consequence | Handling |
|---|---|---|
| The raid client path has more server demands than stage 0 reveals | Stage 1 grows; it is the largest unknown **[⚠ inferred]** | Stage 1 is done first and needs no network work to be thrown away |
| The Arena's P2P network has not run across real NATs | Stages 4–6 inherit unknown faults | Run the Arena's two-device test before stage 4 |
| Small network, few snapshots | Mostly NPC raids | NPC fallback; replicas keep offline bases available |
| Symmetric NATs with no reachable game | That player has NPCs only; results wait in the outbox | Accepted; shown in the status line. A relay remains possible later |
| Android stops the socket in the background | Slow spreading and delivery | Store and outbox persist, and results retry for 14 days |
| Cheating: modified games can report any result or publish a weak base | Unfair medals and reports | Accepted with no central authority. Signing stops forgery by others, and limits in 6.4 bound the harm |
| Griefing with many generated keys | Report spam, repeated losses | 6.4's daily limits, store caps, bounded queues |
| Snapshots reveal names and teams to every game, and addresses are visible as the Arena's are | Privacy | Snapshots hold nothing else. Say so in `RAIDS.md` |
| Local-id assumption is wrong (step 0.1) | Reports attributed to the wrong side | Translate ids in one server function, covered by tests |

## Out of scope for the first release

- A live defender playing during the raid (it would add lockstep, and both players being online).
- Global leaderboards; `leaderboardId` stays empty, since there is no trusted scorekeeper.
- Alliance raids and alliance-wide bonuses.
- Custom base layouts in snapshots, and the chunking they would require.
- Raid events (`GetActiveRaidEvent`) and paid match refreshes.
- Custody of results by third-party games. Add it only if test 5 or 6 shows the attacker's outbox is not enough.
