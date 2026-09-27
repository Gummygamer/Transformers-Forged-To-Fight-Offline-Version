# Authoring story boards from the server

## Table of contents

- [About this document](#about-this-document)
- [Start here — the three layers](#start-here--the-three-layers)
  - [The objective](#the-objective)
  - [Three layers, and the boundary that matters](#three-layers-and-the-boundary-that-matters)
  - [Two names that collide, and are not the same thing](#two-names-that-collide-and-are-not-the-same-thing)
- [Vocabulary — what legally fits in each slot](#vocabulary--what-legally-fits-in-each-slot)
  - [Board themes](#board-themes)
  - [Arena levels — the fight background](#arena-levels--the-fight-background)
  - [Grid dimension](#grid-dimension)
- [The piece contract — what a theme library must provide](#the-piece-contract--what-a-theme-library-must-provide)
- [The recipe — changing a board's terrain end to end](#the-recipe--changing-a-boards-terrain-end-to-end)
- [Worked example — what was actually proven](#worked-example--what-was-actually-proven)
- [Fight backgrounds — opt-in, and why](#fight-backgrounds--opt-in-and-why)
- [Pitfalls — settled, do not relitigate](#pitfalls--settled-do-not-relitigate)
- [What would settle the open questions](#what-would-settle-the-open-questions)
- [Appendix A — the method that found this](#appendix-a--the-method-that-found-this)
- [Appendix B — environment and toolchain](#appendix-b--environment-and-toolchain)
  - [The APK](#the-apk)
  - [The emulator](#the-emulator)
  - [Host toolchain](#host-toolchain)
- [Appendix C — how to replicate this from a clean checkout](#appendix-c--how-to-replicate-this-from-a-clean-checkout)

## About this document

**This is the single source of truth for server-authored board terrain.** Markdown is
canonical — please don't fork a second copy in another format. Two documents describing the
same wire format disagree within weeks; `ABILITY_AUTHORING.md` already carried a wrong claim
for days for exactly that reason.

**Need HTML, Word or PDF?** Generate it, don't rewrite it:

```bash
pandoc BOARD_AUTHORING.md -o boards.html --standalone --toc --toc-depth=3
pandoc BOARD_AUTHORING.md -o boards.docx --toc --toc-depth=3
pandoc BOARD_AUTHORING.md -o boards.pdf  --toc --toc-depth=3
```

**Every factual claim carries a provenance mark**, using the same legend as
`ABILITY_AUTHORING.md`:

| Mark | Meaning |
|---|---|
| **[👁 live]** | **Observed in a running client** — appears in `TFTFHOOK` instrumentation or was seen on screen. The strongest mark. |
| **[📡 served]** | **Present in the payload we serve** and accepted by the client without error, but never observed doing anything. |
| **[📄 binary]** | **Read from decompiled source, `dump.cs`, or a shipped asset bundle manifest**, with a line number or entry path. True about the client; says nothing about whether it works as authored. |
| **[⚠ inferred]** | **Reasoning only.** No citation. A hypothesis, never a fact. |

**The two-source rule:** a claim needs two different kinds of evidence before it is settled.
Single-source claims stay provisional no matter how convincing they read.

Unless stated otherwise, `file:line` citations are into `work/decompile_310/src` (Mono 3.1.0),
because that is the readable source. **The build we actually run is IL2CPP 9.2, so where the
two disagree, 9.2 wins** and the disagreement is called out explicitly.

## Start here — the three layers

### The objective

Before this, every story board in the game rendered identically: one terrain, one time of
day, every quest. Not because the client couldn't do otherwise — because the server never
told it otherwise. This document is about which parts of a board's appearance the server can
name, and which parts it cannot touch at any price.

### Three layers, and the boundary that matters

```
SERVER    theme name, time-of-day index, grid dimension, walkable grid,
          links, node positions, enemy arena override
              │
CLIENT    generates the ground mesh at runtime, places nodes and gates,
          picks props and landmarks, resolves every asset by name
              │
BUNDLE    the art: meshes, textures, the terrain blend mask, the prefab library
```

**The boundary is the whole story: the server names things, the client resolves them.**
There is no client-side list of legal theme names — `themeName` is a free-form string that
gets pasted into an asset path. [📄 binary: `GameboardManager.cs` `GetBasePathForType`, case
`QuestboardPrefabType.Theme`] It arrives on the quest Summary. [📄 binary:
`GameboardManager.cs:217`; 9.2 `dump.cs:309577` still declares `theme` as a bare `public
string` with no enum]

That is why adding terrain is cheap on the server and expensive in Unity, and why no amount
of server work can place a single prop.

### Two names that collide, and are not the same thing

| | what it is | where it is set |
|---|---|---|
| **board theme** | the 3D terrain you walk the node path on | `theme` + `todIndex` on the quest Summary |
| **arena level** | the fight background, loaded when a battle starts | `mapOverride` + `todIndex` on the enemy |

They ship as **separate asset sets** with separate vocabularies, and they share the
`_timeofday_` naming convention, which makes them easy to confuse. A "chicago" board does not
exist; a "quintessa" fight background does not exist.

## Vocabulary — what legally fits in each slot

### Board themes

Four are present in the 9.2 offline repack, each with a different number of times of day.
[📄 binary: `assets/assetpack/<theme>/<theme>_merged.assetbundle.manifest`, listing
`<theme>_timeofday_<N>_<render>.prefab`]

| theme | times of day | count |
|---|---|---|
| `primordial` | 0–5 | 6 |
| `quintessa` | 0–7 | 8 |
| `ruinedcity` | 0–2 | 3 |
| `unicron` | 0–7 | 8 |

**25 board terrains, of which stock upstream reached exactly one** (`primordial`, tod 0).

⚠️ **"Present in the repack" is not "all the game shipped."** The original store APKs are
~103 MB and contain only `karnak`; the game fetched everything else on demand from a CDN.
[📄 binary: `apk-candidates/reference-3.1.0-MONO-LAST-2017`, `assets/` contains only
`bin,characters,frontendfx,global,karnak,ui`] The 856 MB offline repack holds whatever its
author collected. **Four is a floor, not a ceiling**, and nothing in any binary can tell us
the real number, because themes were named by a server that no longer exists.

⚠️ `todIndex` is **not validated against the theme.** An out-of-range value logs
`"Time of day not found, loading time of day 0"` and falls back to 0. [📄 binary:
`GameboardBuilder.cs:1241-1244`; string present verbatim in 9.2] Index **0 is valid for all
four themes**, so it is always a safe default.

### Arena levels — the fight background

Five ship, each with times of day 0–2. [📄 binary: `assets/assetpack/<level>/` manifests;
`gamedata.lbl` `arena_levels_json`]

```
chicago   hongkong   karnak   mine   rust
```

`arena_level()` **exits the process** on an unrecognised name, so this list is a hard gate
server-side. [📄 binary: `Server/gamedata.lbl`, `arena_level`]

### Grid dimension

`gridDimension` sets the board size and is server-authored. [📡 served: emitted by
`build_linear_quest_map`]

⚠️ **Two different pieces of client code read the terrain mask, and they scale it
differently.** This is the sharpest hazard in this document:

```csharp
Mathf.Clamp01((item.x  + num3) / num2) * 50f                  // QuestMapTerrain.cs:160
Mathf.Clamp01((val4.x  + num2) / num)  * (float)gridDimension // GameboardBuilder.cs:899
```

`QuestMapTerrain` stretches the 50×50 mask across the board whatever its size.
`GameboardBuilder` indexes by grid cell instead. They disagree about what a mask cell means.
[📄 binary, both lines]

**There is a hard ceiling on `gridDimension`, and it is 50.** `MaskValues` holds 51 entries,
indices 0–50 [👁 live: `BASEDIAG … len=51`]. At the far edge `Mathf.Clamp01` returns exactly
`1.0`, so `GameboardBuilder` computes `1.0 * gridDimension` as its index. At
`gridDimension = 50` that is index 50 — the last valid entry. **At 51 or above it indexes
past the end and throws `IndexOutOfRangeException`, killing the board build.**
[⚠ inferred: derived from the two cited lines; never tested, because no shipped board goes
there] `QuestMapTerrain` is not exposed to this — it scales by a constant `50f` regardless of
grid size.

Shipped boards are 11×11 for linear story quests and 50×50 for the base — the latter sitting
exactly on the boundary. **Do not exceed 50 without testing it first.**

## The piece contract — what a theme library must provide

The client resolves every board object by name out of three prefab libraries, requested
together: `library_<themeName>`, `library_common`, and `library_buildings`. [📄 binary:
`GameboardBuilder.cs:1136`, `:1149`, `:1163`]

**A new theme owes exactly one required asset.** Everything else comes from the shared
`library_common`, which you do not need to rebuild.

| name | library | required? | on miss |
|---|---|---|---|
| `qb_theme_material` (or `qb_theme_material_base`) | **Theme** | **REQUIRED** | `"<name> could not be found in library for theme <theme>"` [📄 binary: `GameboardBuilder.cs:1197`; the name is chosen at `:1181`] |
| `qb_terrain_fringe_lrg` | Theme | optional | warns, board still builds [📄 binary: `:541-543`, `:575`] |
| `qb_node_01` | Common | required | `"[GB] qb_node_01 could not be found in common library"` [📄 binary: `:1268`] |
| `qb_gate_01` | Common | required | `"[GB] qb_gate_01 could not be found in common library"` [📄 binary: `:1277`] |
| `qb_relic_pedestal_01` | Common | required | `"[GB] qb_relic_pedestal_01 could not be found in common library"` [📄 binary: `:1471`] |
| `qb_chest` | Common | optional | **logs an error** — `"<name> could not be found in common library"`, no `[GB]` prefix; the board still builds [📄 binary: `:1290`] |
| `qb_proxy_material` | Common | optional | silently skipped [📄 binary: `:1199`] |

Sub-components looked up *inside* an already-instantiated prefab, all null-safe:
`qb_node` inside `qb_node_01` [📄 binary: `NodeController.cs:96`]; `qb_gate_classicon`,
`qb_gate_fx_left`, `qb_gate_fx_right` inside `qb_gate_01` [📄 binary:
`QuestGateController.cs:65`, `:146`, `:151`].

⚠️ **The `[GB]` prefix is 9.2-only.** Mono 3.1.0 logs these without it; the prefix appears in
9.2's literals. Three carry it there — `qb_node_01`, `qb_gate_01`, `qb_relic_pedestal_01` —
while the chest error does not. Grep for the message text, not the prefix.
[📄 binary: `GameboardBuilder.cs:1268` vs `dumper-out/stringliteral.json`]

## The recipe — changing a board's terrain end to end

1. **Author the row** in `Server/data/quest_terrain.json`:

   ```json
   "quests": {
     "1.1.2": { "theme": "quintessa", "todIndex": 3 }
   }
   ```

   A quest with no entry falls back to `_default`, then to `primordial`/0.

2. **That is usually the whole change.** `Server/data/quest_terrain.json` is read with
   `read_file` **on every request** [📄 binary: `Server/gamedata.lbl`, `load_data`], and the
   quest Summary that carries `theme` is built dynamically per request — `fakeserver.lbl:1844`
   routes `/quests/quest-begin/` to `quest_begin_response`, which calls
   `gamedata.build_quest_begin_with_cleared` [📄 binary: `Server/fakeserver.lbl:1844`, `:1720`].
   **A data-only edit needs no regeneration and no server restart.** [👁 live: edited the file
   and the very next request served the new theme, with neither step]

   ⚠️ **Do not confuse this with the static fixtures.** `legible run Server/gamedata.lbl`
   writes exactly three files — login/user data, account data, and the missions autorefresh
   [📄 binary: `Server/gamedata.lbl`, `build_responses`]. It has **no effect on the board
   theme.** Regenerate when you change something feeding those baked responses, such as the
   ability kit tables in `getLoginData`; it is irrelevant here.

   Restart the servers (`ftf servers`) when you change **`.lbl` code**, which the interpreter
   loads at startup — not when you change JSON data.

   ⚠️ **This distinction will catch you, and it looks exactly like a broken feature.** While
   verifying this document, arena variation appeared not to work: every one of 148 enemies
   served `chicago`. The data was right, the code was right, the tests passed. The running
   server was simply still executing the `gamedata.lbl` it had loaded *before* the arena code
   was added. One `ftf servers` and all five arenas appeared. **If a data edit takes effect
   but a behaviour change does not, you changed code and did not restart.** [👁 live]

3. **Verify the served value before you look at the screen.** The theme rides on the quest
   Summary, so query the quest route, not `getLoginData` — the login payload does not contain
   quest summaries at all:

   ```bash
   curl -s -X POST -d '{}' localhost:8080/quests/quest-begin/1.1.2 \
     | grep -oE '"theme": *"[a-z]*"'
   ```

   ⚠️ **That route emits spaced JSON** (`spaced_envelope`), so a pattern like `"theme":"x"`
   with no space silently matches nothing and looks like failure. Match `"theme": *"`.

4. **Verify in the client.** The 9.2 build logs the theme on every board build:

   ```bash
   adb logcat | grep -E '==FIXTERRAIN==|could not be found in library|Time of day not found'
   ```

   ```
   ==FIXTERRAIN== builder=0x… isUsersBase=0 theme='ruinedcity' themeMat=0x… maskValues=0x…
   ```

   [👁 live] `isUsersBase=0` is the quest board; `isUsersBase=1` is your base, which always
   renders `primordial`.

⚠️ The 3.1.0 log line `[GB] LOADING TIME OF DAY:` **does not exist in 9.2** — do not grep for
it. [📄 binary: absent from `stringliteral.json`] Use `==FIXTERRAIN==` instead.

## Worked example — what was actually proven

Authoring `2.1.1 → ruinedcity / tod 2` and entering that quest produced, with **no client
change and no new art**:

```
==FIXTERRAIN== isUsersBase=1 theme='primordial'    ← the base, untouched
==FIXTERRAIN== isUsersBase=0 theme='ruinedcity'    ← the quest board
```

and a visibly different terrain on screen. [👁 live] No `could not be found in library for
theme`, no `Could not lot library`, no `Time of day not found`, no fringe warning. [👁 live]

A second observation, made while checking this document rather than while writing it: editing
`quest_terrain.json` and issuing the very next request — **no regeneration, no restart** —
served the new theme immediately. [👁 live] The original run had done both steps, so which one
mattered had simply never been isolated. Worth stating plainly: the recipe above is shorter
than the one this document first shipped with, because the first version credited a step that
does nothing here.

Separately, regeneration of the *static* fixtures is byte-identical across runs, so the
CRC-guarded export payload stays reproducible. [📡 served]

## Fight backgrounds — opt-in, and why

Per-encounter arena variety is authored alongside the board terrain:

```json
"2.1.1": {
  "theme": "ruinedcity", "todIndex": 2,
  "arena": { "vary": true, "pool": ["mine", "rust"] }
}
```

`vary` turns it on; `pool` restricts the draw to a chosen grouping instead of all five. A
quest with no `arena` key keeps the previous behaviour exactly.

**Why it is opt-in rather than global — a scoping decision, not a technical limit.** Turning
it on everywhere contradicts two things upstream wrote to defend the current behaviour:

- `Server/test_gamedata.lbl:1068` asserts `enemy.mapOverride == arena_level()`, i.e. that
  every story fight uses the one global `TFTF_ARENA_LEVEL`.
- `Server/fixtures/story_act1_848e4d3.json` is a golden payload **pinned to upstream commit
  `848e4d3`**, compared against by three tests.

Flipping the default means editing upstream's test and regenerating a fixture tied to their
commit. **That is a design call for the maintainers, not something to change silently.**

**To go global later:** default `vary` to true in `encounter_arena_for`, update that one
assertion, regenerate the fixture. No other code changes — `pool` already expresses "these
quests draw from these arenas."

⚠️ **The Karma Six challenge board seeds on the encounter key, not the row.** Its tiles call
`encounter_arena_for(challenge_qid(), length(key), …)`, and `challenge_qid()` is `1.1.2`
[📄 binary: `Server/gamedata.lbl`, `challenge_qid`, `challenge_encounter_tile`]. Because the
seed is a key *length* rather than a position, the spread is lumpy rather than even — across
its 148 nodes the observed split was mine 46, karnak 30, chicago 30, hongkong 24, rust 18.
[👁 live] Every arena appears, which is the point, but do not expect uniformity.

⚠️ **Quests `2.1.1`, `2.2.1`, `2.3.1` are the custom story acts**, and their tests pin the
full encounter chain including the enemy payload. Opting those in requires updating those
tests too. `1.1.2` is the worked example because it is not pinned.

**The pick is seeded from quest id and row, not `random_int`.** `build_quest_begin` and
`build_quest_detail` are baked into an export payload guarded by a CRC-32 and a fixed entry
count [📄 binary: `Server/export_payload.lbl:681`, `:701`, `:895`]. A fresh roll per call
would make every export a different blob, and could show one arena on the map preview and a
different one in the fight. The seed keeps every encounter varied but reproducible.

## Pitfalls — settled, do not relitigate

- **The server cannot place props or landmarks.** Not by any mechanism. `renderId`,
  `renderRotation` and `renderDecorations` exist on the client's tile model and our server
  emits none of them; selection and placement are entirely client-side. [📄 binary:
  `EB.Missions.MapTile`, `dump.cs:309147`/`:309151`; consumed at
  `GameboardBuilder.cs:1392`/`:1544`] A `renderId` resolves to *both* a prefab key and a
  `SlotDefinitions.SlotInfo` giving footprint (`width`, `length`, `center`, `placeTerrain`).
  Wiring the server to emit `renderId` is the obvious next frontier and is **not done**.

- **You cannot paint the ground from the server.** `ThemeMaterial.MaskValues` is read-only —
  no writer exists in 3.1.0 or 9.2. It is a serialized field hydrated by Unity at prefab
  load, sampled into vertex-colour alpha. [📄 binary: `ThemeMaterial.cs`,
  `QuestMapTerrain.cs:167-174`] Ground painting is baked at bundle-build time.

- **`TOTAL_SIZE 1000 / TILE_SIZE 20` means 50×50**, and the live array reports `len=51` —
  a vertex grid is tiles+1. [📄 binary: `ThemeMaterial.cs`] [👁 live: `BASEDIAG … len=51`]

- **`LowResMask` is null at runtime.** Only the float array is live; the texture field is
  unused. [👁 live: `lowResMask=0x0`]

- **Raids and your base force `todIndex = 0`** and use the theme name `"base"` for the
  time-of-day asset. Time-of-day variety works on quest boards only. [📄 binary:
  `GameboardBuilder.cs:1217-1219`, `:1227`]

- **A confident reading of an asset is not a verified behaviour.** The sibling document
  records an icon swap that shipped from a font-atlas reading and was reverted. Board claims
  here marked `[📄 binary]` alone have the same status: true about the code, unproven in play.

## What would settle the open questions

- **Does a `gridDimension` above the mask size break the `GameboardBuilder` path?** One
  board with a large grid answers it.
- **Can the server drive `renderId`?** Emit one on a tile and watch for `[GB] No SlotInfo for
  renderID` or a prop appearing. This is the highest-value unknown in this document.
- **How many themes did the game actually ship?** Only a CDN catalogue or captured traffic
  can say. The Netflix build ran until 2026-05-08, so captures may exist.

## Appendix A — the method that found this

Nothing here came from reading the decompile alone. The decompile says what the client
*would* do; only the running client says what it *does*. Every `[👁 live]` claim above came
from the same four-step loop, and it is cheap enough to repeat for anything you doubt.

1. **Read the Mono source for the mechanism.** `work/decompile_310/src/Quests/Presentation/`
   is readable C# and is where the system is legible *as a system*. Grepping the IL2CPP dump
   first is how arena levels get mistaken for board themes — they share the `_timeofday_`
   naming and nothing but the surrounding code distinguishes them.
2. **Confirm the mechanism survived into 9.2.** The dump and `stringliteral.json` are the
   authority for the build we run. Log strings are the cheapest tell: `[GB] LOADING TIME OF
   DAY:` exists in 3.1.0 and **not** in 9.2, so a verification plan built on it would have
   looked like a failed feature.
3. **Change one value on the server and query the live route.** Not the file you edited —
   the route. `curl -X POST -d '{}' localhost:8080/quests/quest-begin/<qid>`.
4. **Watch the client say what it loaded.** `adb logcat | grep '==FIXTERRAIN=='`. This is the
   terminal artifact; everything before it is a proxy.

**Three separate wrong conclusions in this project came from a stale artifact rather than
faulty reasoning**, and they are worth naming because the next one will look the same:

- A `legible` binary built 15 days earlier made `GET /base/active` take 26–33 s. It was read
  as a server performance bug and nearly shipped as one. The interpreter had an `O(n²)`
  `substring`; rebuilding it took the same request to 3.2 s with no code change.
- `git fetch` on the interpreter repo had been failing silently for twelve days because
  upstream renamed `master` to `main` and the local refspec still pointed at `master`.
- A running server holding the previously loaded `gamedata.lbl` served `chicago` for all 148
  enemies while the source, the data and the tests were all correct.

**The shape is always the same: the thing reporting the answer was not the thing that
changed.** That is why step 3 says query the route and step 4 says read the client log.

## Appendix B — environment and toolchain

Every `[👁 live]` claim in this document was observed on exactly this setup.

### The APK

| field | value |
|---|---|
| package | `com.kabam.bigrobot` |
| versionName / versionCode | **9.2.0** / `123129100` |
| minSdk / targetSdk | 23 / 30 |

Board and arena behaviour is driven by server strings and asset-bundle names, so unlike the
RVAs in `ABILITY_AUTHORING.md` **nothing here is tied to a specific binary offset.** The
claims should hold on any 9.2.0 build carrying the same bundles.

Asset-bundle facts (theme lists, time-of-day ranges, the art inventories) were read from the
**856 MB offline repack**, `work/tftf-offline-aligned.apk`, by extracting
`assets/assetpack/*.manifest` and by opening bundles with UnityPy.

> ⚠️ **The repack is not the shipped game.** Original store APKs are ~103 MB and contain only
> the `karnak` bundle; everything else was fetched on demand from a CDN that no longer
> exists. Any count of themes or arenas taken from the repack is a floor, not a census.

### The emulator

| field | value |
|---|---|
| Android emulator | **37.1.11.0** (build_id 15917651) |
| adb | 1.0.41 / platform-tools **37.0.1** |
| system image | `system-images/android-30/google_apis/x86_64/` |
| Android | **11** (API **30**) |
| AVD RAM / heap / cores | 8192 MB / 1024 MB / 8 |
| launch flags | `-no-window -gpu host -accel on` |
| GPU selection | `DRI_PRIME=1`, `MESA_VK_DEVICE_SELECT=1002:731f` |
| observed renderer | **AMD Radeon RX 5600M** (`radeonsi`, navi10, ACO) |
| viewer | `xwayland-satellite` then `scrcpy` |

> 🔑 **Board work needs real GPU rendering, and the AVD config file lies about it.**
> `config.ini` says `hw.gpu.enabled=no`, but the command line passes `-gpu host`, which
> overrides it. Under SwiftShader the game runs but is unusably choppy and terrain
> inspection is pointless. **Confirm the renderer in the boot log, not in `config.ini`** —
> if it says SwiftShader, the dGPU did not engage.

### Host toolchain

| tool | version / value |
|---|---|
| interpreter | `legible` built from `Gummygamer/legible-lang` @ **`b3f02e5`** |
| bundle reader | UnityPy **1.25.3** (`.venv-unitypy/bin/python`) |
| decompiled sources | `work/decompile_210/src` (Mono 2.1.0), `work/decompile_310/src` (Mono 3.1.0) |
| IL2CPP dump | `work/dumper-out/dump.cs`, `stringliteral.json` (9.2) |

> ⚠️ **Pin the interpreter.** An older `legible` made an unrelated request 8× slower and was
> misdiagnosed as a server bug for most of a day. If timings here look wildly wrong, check
> your interpreter commit before you profile anything.

## Appendix C — how to replicate this from a clean checkout

1. **Build the interpreter** at the commit above; put `legible` on `PATH`.
2. **Start the servers** — `ftf servers` from the project root inside `direnv`.
3. **Change a board theme** — edit `Server/data/quest_terrain.json`, no restart needed.
4. **Confirm the served value** — `curl -X POST -d '{}' localhost:8080/quests/quest-begin/1.1.2 | grep -oE '"theme": *"[a-z]*"'`.
5. **Start the emulator on the dGPU** — `ftf up host`, confirm the AMD renderer in the boot log.
6. **Watch the board build** — `adb logcat | grep -E '==FIXTERRAIN==|could not be found in library|Time of day not found'`.
7. **Enter the quest.** `isUsersBase=0` lines are quest boards; `isUsersBase=1` is your base.

Changing `.lbl` code instead of JSON? Add `ftf servers` between steps 3 and 4, or you will
verify the old behaviour and conclude the feature is broken.
