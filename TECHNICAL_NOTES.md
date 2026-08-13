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

All six are in `patches/patch_il2cpp.lbl`, applied by file offset, and the script prints a
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

### Roster-stage cleanup

The cosmetic base-builder workarounds can leave their activated objects alive when a screen is
opened without taking `BaseBoard.LeaveBoard`. The squad-screen cleanup uses slots 122–132
(`TSHIDE`). The matching BOTS-roster cleanup is arm64 slots 133 `RSENTER`
(`HeroesScreen.WindowEnter`, `0xC587B8`) and 134 `RSEXIT`
(`HeroesScreen.WindowExit`, `0xC595AC`): entry hides the tracked residual objects and exit
restores exactly that set. Tapping a roster tile opens `HeroActionPopup`, layered over
`HeroesScreen`, so the roster hide set remains active in the action/detail modal and needs no
second slot pair. The armv7 implementation is deliberately deferred; these slots are arm64-only.
The temporary read-only special/transform and popup probes used to verify this were removed from
the shipped hook.

### Roster-scroll crash guard

The reported symptom was: *"Upon scrolling down the roster list to edit the team, the game
crashes."* Both affected roster lists were native process deaths, not managed exceptions: the
main-menu BOTS grid survived nine downward flicks and died on the tenth, while STORY → EDIT
SQUAD died on its first flick. The decisive arm64-v8a tombstones were SIGSEGVs (signal 11, fault
addresses `0x0` / `0x150048`) and contained no authored server data, so this could not be a
`Server/gamedata.py` fix.

There are two bad delegate/ABI routes. The first invokes
`EditTeamScreenPresentation.OnGridItemClicked` from the no-argument
`UIScrollView.OnDragNotification` chain, leaving it with a bogus or absent grid-item argument
(and on the generic-shared path an absent hidden `MethodInfo*`). The second invokes
`EB.Action.Invoke` at `0x151233c` directly from `UIScrollView.OnDragNotification.Invoke` at
`0x1405050`, bypassing slot 77 entirely.

- Slot 135 `ROSTERDRAGFIX` (`EditTeamScreenPresentation.OnGridItemClicked`, `0xB210CC`) reads
  `a1 -> klass -> klass.name` under `PROTECT` and enters the original only when the class is
  exactly `HeroPortrait`. That is the correct discriminant because a real roster-card tap passes
  that concrete `IGridItem`; the drag delegate does not.
- Slot 136 `ROSTERDRAGCTX` (`UIScrollView.Drag`, `0x1E5F444`) brackets the original call with
  the per-thread `g_uiscrollview_drag_depth` marker.
- Slot 137 `ROSTERDRAGCHOKE` (`UIScrollView.OnDragNotification.Invoke`, `0x1404E6C`) suppresses
  this dedicated no-argument notification delegate. The first dynamic-extent attempt proved a
  second bad invoke can occur after the Drag frame unwinds, so the shipped predicate is the
  notification itself rather than a broad UI-event filter. `UIScrollView.Drag` still performs
  the scroll, and genuine picker-card taps take the distinct `OnGridItemClicked` route.

The pre-existing slot 77 `FIXWRAPMI` (`0x152B570`) still rejects a null or low hidden
`MethodInfo*`. It is hardened to reject an unreadable `a1` only when `a0` is the one wrapper
`this` learned from the known broken null-MethodInfo dispatch. That qualification is
load-bearing: legitimate startup `SafeAction<object>` wrappers can pass `a1 == NULL`, so a
broader predicate regressed startup.

Live verification on arm64-v8a was green: BOTS accepted 14 downward and four upward flicks with
a stable pid, genuinely reaching Wheeljack, Scorponok, Prowl, Ironhide, Sunstreaker, and
Sideswipe. EDIT SQUAD accepted eight downward and four upward flicks with a stable pid, reaching
Hound, Grindor, and Bonecrusher. A post-scroll Hound tap remained selectable: it filled the
empty third squad slot and changed TOTAL from 15,138 to 18,223 (+3,085, exactly Hound's rating),
with `ROSTERDRAGFIX pass ... class='HeroPortrait'`, zero skip lines, and a surviving ACCEPT tap.
The evidence capture is `media/roster-scroll-fix.mp4`; it is intentionally ignored and not
committed.

These slots are arm64-v8a only. The `armeabi-v7a` port is out of scope: its addresses must be
re-derived against that binary, not translated from these arm64 values.

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
  is authorable from the offline data layer, but through a different wire object than
  `attackValues`. `PlayerController.ReceiveHit` takes `HitData.HitStun` in frames, divides it
  by 30, and calls `ApplyHitStun(seconds)`; that calls
  `StatModifierController.ApplyStatModifier("gp_hit_stun")`, which requires a
  `BCGStatModifier` row at `result.statMods["gp_hit_stun"]`. The earlier reading that this was
  not server-authorable and must be the asset wall was incorrect. It was tempting because
  `BCGAttackValue` really does contain only Percent/ManaGain/CritChance/CritDamage/CritPierce;
  the incorrect step was concluding that no other login-data field feeds stun. Emulator
  `HitData` was measured as real — 9.000–21.067-frame stun values with named reactions such as
  `HitReactionLightLeftHigh` and `HitReactionMediumBackLow` — so it was not a false negative.
  With an empty `statMods` map, `seg05_before_arena.log` recorded `STATMOD ... ret=0` 68 times
  and zero `HITSTQ ... now=1` events. With the authored row, `seg05_after_arena.log` recorded
  `STATMOD ... ret=1` 51 times with no failures and 229 `HITSTQ ... now=1` events.
  `get_CanAttack` consults `get_HitStunned`, and `TryExecuteAction` consults `get_CanAttack`,
  so this gates the player as well as AI opponents.

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

### Arena authoring

`build_quest_enemy()` in `Server/gamedata.py` authors the QuestBoss wire keys
`mapOverride` (level name) and `todIndex` (time-of-day index). An empty `mapOverride`
means no arena is assigned, so the client falls back to Karnak; its ground renders black in
this offline build. `chicago` is the verified-good default because its ground and street
render completely. Set `TFTF_ARENA_LEVEL` and `TFTF_ARENA_TOD` to sweep the other shipped
levels and time-of-day variants. These settings are read when `gamedata` is imported, so
restart the local server after changing them; no APK rebuild or app restart is needed.

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

## The special-attack meter

The locked special-attack meter had two independent wire-data causes. First,
`special_attacks` was hard-coded as zero. That field becomes
`BCGUserHeroBase._specialAttackCount@0x80`, is copied at `0xA62E34`/`0xA62E38` into
`BCGAttributeDataBase.SpecialAttackCount@0x28`, and reaches
`PlayerAttributes.get_NumSpecials@0xDAC3E4` as a bare `ldr w0,[x8,#0x28]` with no rank
clamp. It then becomes `HudScreen.FighterInitData.NumSpecials@0x30`; at
`HudSpecialMeter.Init@0xFEFD4C` a zero makes every segment render locked through
`_lockIcons` and `LockUnfilledColor`. The same zero reached
`BCGHeroDetails.AttributeData@0x40` through `POST /bcg/getBaseHeroData`
(`BCGHeroDetails..ctor@0xA5AA14` to `BCGAttributeData..ctor@0xC14864`). Emitting the
existing authored `max_special_attacks(bid, star)` rarity curve, which returns one through
three, fixes the padlocks.

That count also determines capacity. `PlayerAttributes.Init@0xDAB16C` computes
`maxMana` as `TuningGameplay.ManaPerSpecial` (`SafeFloat@0x18`, loaded at `0xDABF88`)
times `SpecialAttackCount@0x28` (loaded at `0xDABF3C`); the `scvtf`/`fmul` pair at
`0xDAC044`/`0xDAC048` feeds argument two of `ResourceAttribute.Init@0xE2FC8C`. A count
of zero therefore made the meter capacity zero as well as rendering every segment locked.
`TuningGameplay` (`re_notes/dump.cs:424121`) is a `MonoBehaviour` whose
`ManaPerSpecial` `SafeFloat@0x18` is Unity-serialized in the app's own bundle, so the
300-per-segment capacity comes from the client rather than the server.

Second, `mana_gain` was absent from the two payloads that construct the in-fight attribute
data. The wire field becomes `BCGAttributeDataBase.ManaGain@0x54`, which
`PlayerAttributes.Init@0xDAB16C` loads as base plus modified values at
`0xDABE00`/`0xDABE08` into the `_powerGainRate@0x138` `StatAttribute`. Per-hit gain is
`BCGAttackValue.ManaGain` (wire key `m`, field `@0x1C`) times `HitData.Mana` times that
power-gain rate, using the `fmul` pair at `0xDADC80`/`0xDADC84` in
`PlayerAttributes.GetAttackerManaGain@0xDADB68`; the defender sibling at `0xDADCA8`
also applies `DefenderManaModifier` 1.25, and both paths are called by
`PlayerController.ReceiveHit@0x1178790`. A missing `mana_gain` parses as zero, which is a
multiplicative zero for both fighters regardless of `m`; increasing `attackValues[*].m`
625-fold therefore changed the rendered meter by exactly zero pixels. Emitting
`mana_gain` and `mana_start` from the other builders fixes charging.

Treat combat attributes as a three-builder rule: any future field must be authored in
`build_hero_base()`, `build_hero_entry()`, and `build_base_hero_details()`, with a test for
each payload. Only `build_hero_base()` has the full attribute set; the other two use shorter
subsets, so a field in only one builder can silently be zero in a fight while still looking
authored in source. This also matters for STORY enemies: `PrefightScreenData.InitEnemy` reads
only `character`, `rank`, `level`, `sig_lvl`, and `flvl` from `QuestBoss`, then gets the
opponent's attributes from `POST /bcg/getBaseHeroData`, not from the quest boss entity.

Live verification on an arm64-v8a emulator in STORY 1.1.1, Optimus Primal versus Sharkticon,
showed three unlocked segments, about one full 300-mana segment per six landed light hits, and
all three segments full and lit. Tapping the SP hexagon fired the special, drained the meter,
and took the enemy from 52% to a 0% KO. Both fighters gain mana, confirming the defender path;
the normal fighting-game pace leaves the authored `attackValues[*].m` values unchanged.

## Hit-stun

The reported symptom was that a fighter being struck could still react and land a hit. The
derivation began with `gp_hit_stun` in `PlayerController.DefaultStatMods`, then used temporary,
read-only native probes on `ReceiveHit`, `ApplyHitStun`, `ApplyStatModifier`, `get_HitStunned`,
and `TFormStatModsDB.RegisterStatModifier`. A bracket marker on
`BCGStatModifier..ctor(IDictionary)` harvested the complete wire schema from the running
client. The arm64 locations are `ReceiveHit` `0x1178790` (the `HitData` argument is third,
`x3`, not second), `ApplyHitStun` `0x1179730` (its duration is a float in `v0`, unreadable
through the generic `void *` thunk), `ApplyStatModifier(string,...)` `0xCCF67C`,
`get_HitStunned` `0x1173D94`, `TFormStatModsDB.RegisterStatModifier` `0x10DAABC`, and
`BCGStatModifier..ctor(IDictionary)` `0xA5DFBC`. Because `get_HitStunned` is called every
frame, its probe logged transitions only to avoid flooding the device log.

Those diagnostics were removed before shipping. The local reproduction patch is
`.longtask/hitstun-combat/findings/seg-06-hitstun-diagnostic-hooks.patch`; `.longtask/` is
gitignored, so this RVA list is the durable record. The shipped fix is one authored stat-modifier
row: `build_stat_modifiers()` in `Server/gamedata.py` emits `statMods["gp_hit_stun"]`, covered
by `test_gp_hit_stun_is_a_complete_harvested_stat_modifier_row` in `Server/test_gamedata.lbl`.

| Device log | `STATMOD id=gp_hit_stun` | `HITSTQ ... now=1` (`get_HitStunned` true) |
| --- | --- | --- |
| `probe/seg05_before_arena.log` (empty `statMods`) | `ret=0` x68, zero successes | 0 |
| `probe/seg05_after_arena.log` (authored row) | `ret=1` x51, zero failures | 229 |

This is data-only: the login response is served to whichever ABI is installed, so the fix applies
to armeabi-v7a automatically and needs neither an armv7 RVA nor a change to
`patches/abi_map.lbl`. The paired hook logs objectively verify the effect. The full-speed emulator
captures do not, by eye, show an obviously distinct freeze: with placeholder actors the extra
stun blends into the normal strike/stagger animation.

## The armeabi-v7a port

`patches/abi_map.lbl` translates arm64 method addresses and field offsets to the 32-bit
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

## In-app server payload (v1)

`Server/export_payload.py` turns the authored Python game data into
`assets/tftf_offline_payload.bin`, which the arm64 in-app HTTP server mmaps
directly from `base.apk`. It is a little-endian, deterministic binary: every offset is
absolute from byte zero, four-byte aligned, and every stored key/body is NUL-terminated
then NUL-padded to the next four-byte boundary. The stored length excludes the NUL.

| Offset | Type | Meaning |
| --- | --- | --- |
| 0 | `char[8]` | Magic `TFTFPAY\0` |
| 8 | `u32` | Format version (`1`) |
| 12 | `u32` | Total blob size |
| 16 | `u32` | Loopback listen port |
| 20 | `u32` | Exact-entry count |
| 24 | `u32` | Exact-entry array offset |
| 28 | `u32` | Prefix-rule count |
| 32 | `u32` | Prefix-rule array offset |
| 36 | `u32` | Default-body offset |
| 40 | `u32` | Default-body length |
| 44 | `u32` | CRC-32 of bytes `[64:total_size]` |
| 48 | `u32[4]` | Reserved zeroes |

Exact entries and prefix rules both use a 16-byte record:
`u32 key_off, u32 key_len, u32 body_off, u32 body_len`. Exact records are sorted by raw
key bytes for `bsearch`; prefix records retain the authored `_prefix_rules.json` order
and first matching `path starts with prefix` wins. HTTP keys are `METHOD /path` (no query
string); helper keys begin with `@` and contain no spaces. The template placeholders
`%TID%`, `%BID%`, and `%SIG%` are literal ASCII and are replaced by memcpy splicing.
Hero-data lookup falls back in this order:
`@hero:<bid>:<rank>:<level>`, `@hero:<bid>:1:1`, then `@hero:*:1:1`.

Dispatch order remains the Python server's order: dynamic handling first, then exact
canned route, then prefix rule, then `{"error":null,"result":{}}` (with HEAD returning
an empty 200 response). Payload content is classified as: (A) fully static response files,
prefix response, and default; (B) deterministic finite-input base/quest/grouprefresh
responses; (C) request-body templates for tutorials, hero data, and saved teams; and (D)
quest movement transitions. `_quest_positions` is the only cross-request server state the
C implementation needs to retain: `quest-begin` resets it and `quest-movedir` looks up a
baked transition/body pair before updating it.

### In-app C server

`tools/nativehook/inapk_server.c` is the process-local HTTP server shared by both ABIs. It is
compiled into `libdothook.so` for arm64 with `aarch64-linux-android28-clang` and into
`libdothook-armeabi-v7a.so` for ARMv7 with `armv7a-linux-androideabi21-clang`. Its constructor
caller finds mapped `base.apk` through `/proc/self/maps`, walks its ZIP directory for the stored
payload (with a magic-scan fallback), and mmaps only that entry. It preserves Python dispatch
order: dynamic handlers, exact route, prefix route, then default. `Server/test_inapk_server.lbl`
compiles the host harness and compares live HTTP replies with `fakeserver`. The source is 32-bit
clean through `#define _FILE_OFFSET_BITS 64`: 32-bit bionic's `struct stat.st_size` is 64-bit
while bare `off_t` is 32-bit, so stat/mmap offsets would otherwise truncate. The ARMv7 hook builds
warning-free.

The server was verified on an `android-30 google_apis x86_64` emulator with ARM translation and
`arm64-v8a`, using `Server/build_phone_apk.py --scheme http --server-host 127.0.0.1 --server-port
8080 --bundle-server`. With guest SELinux enforcing, a stock `/system/etc/hosts`, no host server,
and no `adb reverse` forwards, the game boots to the home screen, opens the first STORY mission,
renders the game board, moves the player between board nodes, and plays the fight. The only
listener is `127.0.0.1:8080` inside the game process. `--bundle-server` rejects `--scheme https`,
non-loopback `--server-host` values, and a hook built without the in-app server.

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
