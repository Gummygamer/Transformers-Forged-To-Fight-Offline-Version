# Technical notes

This is the deeper technical reference behind the offline boot. The README explains the
overall shape and how to run things. This file records the specific findings: the binary
patches, the data structures the client expects, and the gotchas, so the next person does
not have to rediscover them.

## The app

Package `com.kabam.bigrobot`, version name 9.2.0, version code 123129100. Minimum SDK 23,
target SDK 30. Launcher activity `com.explodingbarrel.Activity`. The shipped build is a
third party repackage with injected ad SDKs and a packer present in the lib folder, but the
app entry itself is not wrapped by the packer, so the core is patchable.

## Engine and binary

The game is Unity IL2CPP. The game logic is compiled into a native library,
`lib/arm64-v8a/libil2cpp.so`, and the type information lives in
`assets/bin/Data/Managed/Metadata/global-metadata.dat`. There is no managed assembly to
edit, so all behavior changes are made by patching the native library.

For this binary the runtime virtual address equals the file offset, which makes patching by
offset straightforward.

The metadata is version 27, which hides string cross references from static tools. The type
model is fully intact though. Decompiling a class constructor that takes a dictionary
reveals the exact keys that response is parsed for. This is how most of the data shapes
below were recovered without running anything. When you need to know the shape of a
response, find the constructor or the parser for it and read the keys off the decompiled
body.

## The native patches

All six are in `patches/patch_il2cpp.py`, applied by file offset, and the script prints a
before and after disassembly of each so you can confirm them. In short:

1 and 2. Certificate validation. Two TLS code paths are forced to accept any certificate, so
the fake server's self signed certificate is trusted.

3. Manager registration. The block that registers around forty managers is gated behind a
live config object that is null when there is no server. The branch is redirected so the
registration runs anyway.

4. Login authenticators. The login coroutine fails out if zero authenticators are
registered, which is exactly the offline state. The failure branch is skipped so login
completes on the local device session token.

5 and 6. Subsystem fatal errors. A subsystem that cannot reach its data would otherwise pop
the failed to log in dialog. Both the polling check and the fatal tail call are neutralized,
so a failed subsystem is treated as connected and the boot continues.

The script also re-injects the dependency that loads the runtime hook. See the gotcha below.

## The login fix that unblocked everything

The single most important data finding. The login response user object must carry a valid,
non zero `uid` field. Not `id`, `uid`. The user lookup reads the id through the `uid` key,
and if the resulting id is not valid it returns a null user, which then makes the client
throw a null reference during login. The working shape is in
`server/responses/POST__auth_login.json`. The user object also reads `auth_data`,
`server_tag`, and `userToken`.

## Response envelope

The top level envelope is `{"error": null, "result": ...}`. Note that inside Sparx error
payloads the field is spelled `err`, not `error`. Using the wrong one produces a response
the client silently mishandles.

## Recovered data structures

These are the shapes the client parses. They are served from `server/responses/` or
synthesized in `server/fakeserver.py`.

`/bcg/getUserData` is `{userData: {maxes...}, updates: {heroes: [...], savedTeams: [],
activeTeams: []}, deletes: {}}`. The update handler reads `userData.blueprintsMax`,
`teamSizeMax`, and `teamCountMax`. Owned heroes arrive through `updates` and are processed
by the user data update handler, not through `userData` directly.

`/bcg/getLoginData` carries the config blob: `characters`, `blueprints`, `heroes`, and
`evoBlueprints`.

`/account/data` is the large player state object. Its top level keys are siblings, not
nested: `privacySettings`, `invitesDB`, `invites`, `sentInvites`, `invite_max_helps`,
`imri`, `featuredhero`, and `res`. The player resources live under `res.user_info`, for
example `rt` for raid tickets, `energy`, and `gold`, each an object with `v` and `max`.

The home screen is the base. It is built from `/base/active` with the keys
`user.AvailableBuildings`, `user.Buildings`, `user.Sockets`, and `user.Base`, where
`user.Base` is an active mission structure.

## Roster entity type gotcha

The roster reads entities of type `bot`. For an owned character to appear in the roster,
both the user data hero entity type and the blueprint entity type must be `bot`, not `hero`.
This is what fixed the empty roster grid.

## Why combat is the wall

The damage table, called `attackValues`, was server side balance data and is gone. The move
asset bundles inside the client hold only animation and timing data, not damage numbers. So
even with a character fully loaded, a fight has no numbers to run on. Recreating
`attackValues` and the ability definitions is central to any real revival.

## The first time experience fight

The scripted intro fight needs its participant blueprints present in the config, both the
player bot and the enemy. The blueprint is referenced from the intro prefab, but the actual
blueprint data has to exist in the config or the blueprint lookup returns null and the fight
fails to load. This is the smallest possible end to end fight and is the best first
milestone for anyone continuing the work.

## Tutorials freeze offline

Interactive tutorial prompt states loop forever with no server to answer them. Do not try to
satisfy a tutorial by answering its request. Instead remove the condition that triggers it.
The shield tutorial freeze was fixed by giving the player raid tickets, because that
tutorial triggers when raid tickets drop below one.

## The runtime hook

Source is in `tools/nativehook/`. It is a plain inline hook, a sixteen byte overwrite plus a
trampoline, installed by a worker thread before login runs. The emulator translates ARM to
x86 lazily, so the patched code is translated on first call and the hook takes effect.
Frida does not survive this translation and crashes on attach, which is why a hand written
inline hook is used instead.

The hook is loaded through a dependency on `libdothook.so` that is added to the patched
library. The original library does not have this dependency, so the patch script re-adds it
on every build (see the gotcha below).

The hook logs every key the client reads. Important detail: the deep parsers use a fast
path whose key argument is a path structure, not a plain string. The key name is the single
path field inside that structure. The hook is the discovery loop. Seed a response, run,
read which keys the client asked for, synthesize the next piece, verify, and repeat. Every
screen in this build was brought up that way.

## Decompile workflow

Use a headless Ghidra project over the library. Import once with the function name labels
and string labels from the dumper output, with analysis disabled to keep the import fast.
Then decompile specific offsets listed in a targets file. Reading a decompiled body, a
pattern where `if (X != 0)` guards a path and there is a throw at the end is the IL2CPP null
check. X being null is what throws. String keys appear as placeholder symbols whose index
resolves in the dumper's string literal output.

## Authored content data (gamedata.py)

`Server/gamedata.py` is the single hand-authored source for the reconstructed content:
the roster (every character bundle listed in `re_notes/ASSET_INVENTORY.txt`, with an
original class/faction/star assignment and an original stat curve) and the `attackValues`
combat balance table. All numbers are original; none are recovered Kabam data (see
`COMPLIANCE.md`). Running `python Server/gamedata.py` regenerates
`responses/GET__bcg_getLoginData.json` and `responses/GET__bcg_getUserData.json` from it,
so the roster grid, hero details, and team select populate offline. The blueprint,
character, and owned-hero JSON keys are exactly the shapes proven to parse (the same ones
the original three-entry seed used); `entity_type`/`et` stays `bot` per the roster gotcha
above, and the msa values of the three original seed entries are preserved so the intro
fight is not regressed. `fakeserver.py`'s `/bcg/getBaseHeroData` handler computes stats
from the same curve. Normal combat damage requires case-sensitive `attackValues` rows named
`Light`, `Medium`, `Heavy`, and `Ranged`; `PlayerAttributes.GetAttackPercent` returns zero
when a row is absent and does not fall back to `default`. The table now carries an authored
row for every normal attack level. A second independent input is required in account data:
the `missionsconfig.configs["bcg-combat"].armorRatingConstant` value consumed by
`TuningGameplay`. `PlayerAttributes.GetArmorDR` divides armor by
`abs(armor) + armorRatingConstant`; zero authored armor and a missing (zero) constant produce
`0/0 = NaN`, after which `GetDamageReceived` clamps otherwise-positive damage to zero. The
authored positive constant keeps armor reduction finite, allowing landed hits to reduce
health. Both the `bcg-combat` mode name and the `armorRatingConstant` key were confirmed from
the live client; the number itself is original offline balance data.

The missions-config account member deliberately satisfies two client contracts. Its top-level
`configsHash` and `configs` fields feed `ConfigManager.OnData`, while `check`, `refresh`,
`cache`, and the nested `missionsconfig` field let `AutoRefreshingManager.Connect` replay the
same object as a direct refresh result after `TuningGameplay` has subscribed. A direct refresh
uses the manager name (`missionsconfig`) for its data field; a grouped refresh update instead
uses the generic `data` field. The fake server also emits that exact grouped update whenever a
client includes `missionsconfig` in `/autorefresh/grouprefresh`. In the successful emulator run,
periodic grouped requests did not include that manager, but the account bootstrap replay was
sufficient: the native trace recorded non-null `CONFIGDATA`, then `TUNECHANGE` for
`bcg-combat`. The per-bot ability definitions are still the open frontier — the balance table
has numbers, but individual special-move effects remain to be authored.

### Transform damage and hit reactions (playtest feedback)

Two combat-feel reports were checked against the current build:

- *"When a bot transforms he should deal more damage than regular hits (punches and
  kicks)."* A transform is a special attack, whose damage ratio is the blueprint's
  `s1`/`s2`/`s3` (parsed into `BCGBlueprintBase.Special1Damage`/`Special2Damage`/
  `Special3Damage`). These ship on the same scale as the normal-attack `attackValues`
  percents (`Heavy` = 1.0, `Medium` = 0.60, `Light` = 0.35). The proven response ships a
  flat placeholder `1.0/1.0/1.0`, so a transform landed no harder than a Heavy and only
  ~1.7x a Medium — exactly the report. `gamedata.special_damage_ratios` now authors an
  escalating `1.75/2.50/3.50` curve (SP1 < SP2 < SP3, every special above the Heavy normal
  attack), regenerated into `getLoginData`. Covered by `TransformDamageTests`. The normal
  charged/heavy attack already out-damaged punches and kicks and was left unchanged.
- *"When a bot is getting hit he's not supposed to be able to react (land a hit)."* This
  hit-stun is fully engine-implemented and **not** authorable from the offline data layer:
  `PlayerController.ReceiveHit` calls `ApplyHitStun(hitData.HitStun)`, and
  `ConditionNode_HitStunned` / `CompositeNode_CanAct` gate whether a stunned bot may act.
  The stun duration lives in `HitData.HitStun`, a per-move field serialized inside each
  character's move/animation asset bundle — the server `attackValues` rows (`BCGAttackValue`
  = Percent/ManaGain/CritChance/CritDamage/CritPierce only) carry no stun field at all.
  There is therefore no server/JSON knob to change here; if the stagger is not felt in a
  live fight it is the ODR move-bundle not loading (the asset wall), not a data gap.

## STORY board movement milestone

The first authored mission now reaches an interactive board with a live 3D squad leader.
Three details were required beyond rendering the map itself:

- `activeTeams[*].heroes` is a dictionary keyed by bot id, not an array. That gives
  `QuestPlayerController` a lead character to load.
- Each map tile carries absolute `links` and `visibleLinks` vectors. The client rejects a
  move locally unless the destination appears in the current tile's `links` list.
- `/quests/quest-movedir/<qid>-<teamId>/<offX>/<offY>` returns `results`, `progression`, and
  `teamData`. A move result is shaped as
  `{"action":{"moveto":{"x":row,"y":column}}}`. The fake server retains the current tile
  per quest because subsequent requests contain only a direction, not an absolute origin.

The live client has accepted consecutive moves from `(0,1)` to `(1,1)` and then `(2,1)`,
animated the bot between nodes, refreshed reachability, and changed explored progress from
0% through 33% to 66%. See `media/story-board-second-move.mp4`.

## STORY encounter / live-combat milestone

The final tile now carries an authored boss in its `entities` dictionary. Each entity value
must name both `entityType` and `parentEntityType` as `boss`; that makes
`Quests.Builder.NewEntity` construct a real `BCGEntity`, verified by the native hook. The tile's
`boss` field points to the same dictionary key. Crucially, that key is not an arbitrary encounter
identifier: `PrefightScreenData` sends the entity's `key` verbatim as `bid` to
`/bcg/getBaseHeroData`. Using `story_1_1_1_boss` therefore produced the anonymous fist/8888 marker;
using `sharkticon_gs_brawler` resolves the authored blueprint and produces the real Sharkticon
class, name, health, power, and rating.

Arrival returns two ordered action results: the existing `moveto` action followed by
`{"action":{"battle":...}}`. The battle variant reads `x`, `y`, `isFinalBoss`,
`battleEnemy`, and `battleTower` -- notably, `battleEnemy` is the wire key, not `enemy`.
Progression also carries `currentBattleId`, `currentBattlePos`, a compact
`currentBattleEnemy: {"id": ...}` record, and `currentBattleEnemyHealth`. With these fields the
unmodified client completes the board animation and opens its native `SELECT YOUR BOT` screen.
The local `QuestUserInfo` also needs parallel `currentBattleId`, `currentBattlePos`, and
`currentBattleState: "activated"` fields. Its `team` is an object keyed by hero blueprint, not an
array; each value is a `QuestUserHero` record with exactly `hp`, `pi`, `sig_lvl`, `stat_mods`, and
`sig_mods`. A normalized `hp` of `1.0` plus an authored `pi` is enough to populate both selectable
squad entries and the matchup detail. See `media/story-prefight-combatants-populated.png`.

Pressing the enabled FIGHT button now posts:

`POST /matches/activate-match/quests_fight`

with `hero`, `qid`, and `battleId`. The current generic success envelope is sufficient for the
client to leave pre-fight, load the Karnak assets, hide the fight loading screen, and enter the
interactive 3D arena. Attack input visibly animates the fighters; the HUD shows the authored
Optimus Primal and Sharkticon names, ratings, and full health bars. This is live-verified in
`media/story-live-3d-combat.png` and the 18-second `media/story-live-3d-combat.mp4` capture. The
remaining identity bug is equally specific: the player HUD says Optimus Primal, but the player
currently renders with the Sharkticon model.

Mission-fight completion is now live-verified as well. With the authored attack rows and armor
constant loaded, the trace reports finite `armordr=0`, positive `DMGREC out` values, and
`APPLYDMG` transitions all the way from Sharkticon's `2750` health to `0`. The client then posts
`/matches/resolve-match/quests_fight` with `result: "WON"`, `enemyHealth: 0`, and telemetry that
records all 2750 enemy HP lost, and it returns to the mission board. The generic success envelope
is sufficient for this result submission. A local verification capture is stored under `media/`,
which remains ignored and must never be committed.

## The armeabi-v7a port

`patches/abi_map.py` translates arm64 method addresses and field offsets to the 32-bit
build by pairing the two Il2CppDumper dumps, which both come from the same
`global-metadata.dat`. That covers the six native patches and ten of the twelve hook sites.
It does not cover anything that is not a method address, and two fixes are exactly that.
Both were re-derived against the 32-bit binary; the reasoning is written out in
`tools/nativehook/hook_arm32.c` next to each fix, and both were confirmed firing live.

`SETACTFIX` needs the combat game clock, which the arm64 hook reaches through a hard-coded
`.got` slot (`0x2c1a928`). GOT layout differs between the builds, so the address cannot be
translated. It does not have to be: `PlayerInput.QueuedAction.HasAction` returns
`TimeStamp > now`, so it must read the same clock, and it spells the chain out in its own
code. On armv7 (`0x907EC0`) that is a pc-relative pair resolving to GOT slot `0x2826E00`,
then `[.] -> [.+0x5c] -> [.] -> float @0xC`, against `TimeStamp` at `this+0xC`. The arm64
build does the identical four derefs with `0xb8`/`0x18` off `0x2c1a928` — the usual pointer
halving. `SetAction`'s own copy of the chain resolves to the same slot, which is the
cross-check, and both slots are in `.got` in their respective binaries. Live log:
`SETACTFIX clk=652.300 ts=652.800`, the clock advancing monotonically across taps.

`FIXSYN` is a branch rewrite inside `BCGBlueprintBase.get_SynergyBonuses`. arm64 re-points
a `cbz` from the throw block to the empty-list return. ARM32 has no throw block to
re-point: the compiler emits the il2cpp null check as a *call* that only falls through.

    0x7A3EA4  ldr r4,[r6,#0x7c]     ; this->_synergyBonuses (a64 0xE0)
    0x7A3EA8  cmp r4,#0
    0x7A3EAC  bne 0x7A3EB4          ; non-null: run the loop
    0x7A3EB0  bl  0x4F1EA4          ; null: throw (noreturn)

`0x7A3EB0` is therefore reached only when the field is null, and nothing else branches to
it, so overwriting that call with `b 0x7A3FB8` — the `ldr r0,[sp,#4]` that loads the fresh
empty `List<string>` built before the null check, plus the epilogue — is the same fix. It
also skips the enumerator's `Dispose`, which is correct, because it skips the enumerator's
construction too and the slot was zeroed at entry. The hook's `poke32` refuses to write
unless the target still holds the exact instruction the RE was done against
(`ebf537fb`), so a different binary fails loudly instead of being corrupted.

## Known remaining frontiers

1. Correct the STORY player's rendered model identity. Both sides currently load the Sharkticon
model even though the player HUD and selected hero are Optimus Primal. Trace the match builder's
fighter-data source and replace the generic `/matches/activate-match/quests_fight` response with
the exact contract if required.

2. Persist completed quest progression and rewards after a resolved fight. Combat and result
submission now finish, but the authored fake server does not yet retain completion across
sessions or implement the full reward contract.

3. The wider server content database: more quests and maps, opponent lineups, per-bot
abilities, rewards, persistence, and the economy.
