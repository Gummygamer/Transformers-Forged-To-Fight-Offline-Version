# Authoring abilities from the server

> Status: the end-to-end pipeline is **proven**, **generalized assignment is built** (§5),
> and the **glossary now exists** (§8) — asset channels in §3.2.1, an icon candidate map in
> §4.1, and the working/non-working combinations in §8.1–8.2. What remains is per-bot design
> and verifying the untested effects (§9).

This document exists so the next person (or agent) can reproduce this work without
re-deriving it, and without re-litigating the dead ends listed at the bottom. Everything
marked **VERIFIED** was observed in a running client, not inferred from decompilation.

**Start with §0 — the exact APK, emulator and toolchain this was verified on.** Every RVA
here is an offset into one specific binary and is meaningless against a different build.

---

## 0. Environment — what this was verified on

Every **VERIFIED** claim, and **every RVA in this document**, was observed on exactly this
setup. RVAs are offsets into one specific binary; on a different build they point at
unrelated code. **Check the `libil2cpp.so` hash before trusting any address here.**

### The APK

| field | value |
|---|---|
| package | `com.kabam.bigrobot` |
| versionName / versionCode | **9.2.0** / `123129100` |
| launcher activity | `com.explodingbarrel.Activity` |
| minSdk / targetSdk | 23 / 30 |
| `lib/arm64-v8a/libil2cpp.so` SHA-256 | `575aa973ed8fd54e79c70abdaed5b5a3b013e8e3ec68e0fa64e98f6bdfba9b8a` |
| `lib/armeabi-v7a/libil2cpp.so` SHA-256 | `55f596ba20d3226afde54016fbbae9c2c7fc7d1a3aca41db8a96db8ea75770c3` |
| signing cert SHA-256 | `A8:21:3D:06:2F:72:07:75:26:0A:2F:96:E0:1A:E5:AD:27:9A:FE:DF:A4:D6:30:50:EB:81:51:49:F3:69:C5:21` |
| cert owner | `O=Exploding Barrel Games Inc., L=Vancouver, ST=BC, C=CA` |

This is the **developer-signed retail build, not a repack** — Exploding Barrel Games is the
original developer, which matches the launcher activity namespace.

> ⚠️ **Not every 9.2.0 APK is this APK.** Repacks exist with the same version string but
> resigned with AOSP test keys and carrying injected payloads. Match the **hashes**, not the
> version number. All work here targets **arm64-v8a**.

### The emulator

| field | value |
|---|---|
| Android emulator | **37.1.11.0** (build_id 15917651) |
| adb | 1.0.41 / platform-tools **35.0.2** |
| system image | `system-images/android-30/google_apis/x86_64/` |
| Android | **11** (API **30**), build `RSR1.240422.006` |
| device ABI | `x86_64`; abilist includes `arm64-v8a` |
| AVD RAM | **8192 MB** |
| AVD VM heap | **1024 MB** |
| AVD cores | **8** |
| data partition | 10 GB |
| GPU | `hw.gpu.enabled=no` — software rendering, runs headless (`-no-window`) |

> 🔑 **The emulator is x86_64 but the game is arm64.** It runs the `arm64-v8a` `libil2cpp.so`
> under the system image's ARM translation layer. That is why the binary you disassemble is
> ARM64 while the device reports `x86_64`. Do not "fix" this by switching to the armeabi-v7a
> library — the addresses in this document are arm64.

> ⚠️ **RAM and heap are not optional tuning.** At the default 2 GB / 256 MB / 4 cores this
> AVD thrashed badly enough to look like unrelated bugs. Use the values above.

### Host toolchain

| field | value |
|---|---|
| host | NixOS, Linux 6.18 (via the repo's `flake.nix` dev shell) |
| Android NDK | **26.3.11579264** |
| `legible` | 0.1.0, built from source into `.cargo-home/bin` (**not** on the default PATH) |

`legible` is not on the plain PATH — `export PATH="$PWD/../.cargo-home/bin:$PATH"` before
running it (see §3.5).

---

## 1. The objective

**Make every ability assignable to any bot, in any combination.**

The bleed/shock/burn kit that exists today was a **test vehicle**, not the goal. It proved
the chain end to end — server JSON → parsed → granted per-bot → registered in combat →
triggered on hit → ticking damage → icon on screen. That question is now closed.
(The *floating number* for ability damage is a separate, still-open problem — §8.2 ⑦.)

The real problem is **assignment**:

1. ✅ **Solve all permutations — DONE (§5).** Any bot may carry any subset of the available
   abilities — zero, one, several, all — chosen independently per bot. A single
   `bot_abilities(bid)` table now drives all four builders. Verified behaviour-preserving
   *and* verified capable of expressing distinct per-bot subsets.
2. **Then** begin the per-bot design phase — deciding *which* kit each bot should actually
   have. That is a game-design activity and must not start until (1) makes it cheap to
   express.
3. **Alongside (2), build the ability glossary** — see §8. Designers cannot choose kits
   sensibly until each ability states, in plain language, what it does and **who it lands
   on**.

Do not confuse these. Authoring one more hardcoded kit is not progress toward (1).

---

## 2. What is proven today — VERIFIED in a live fight

| capability | evidence |
|---|---|
| Authored abilities deal real damage in combat | `type='dmg_bleed' id='kit_bleed' origMod=0.4000 amount=0.4000 dur=6.00 tick=0.50` |
| Per-bot granting works | different `stat_mods` lists per bot are honoured |
| Effect icons render in-fight | three correct glyphs on the opponent's health bar |
| Floating damage numbers render **for normal attacks** | numerals observed over both combatants |

⚠️ **Do not read row 4 as "ability damage shows a number."** It does not, today. Normal
attacks have duration `0.0` and draw directly; a damage-over-time buff instead accumulates
into a per-player cache that only a deployed `FloatingText_BuffEffect` can flush to the
screen — and no hero currently references one. See §8.2 ⑦ and ①. This row has been misread
that way before, including by the people who wrote it.
| Indefinite buffs | `d = -1.0` → 3788 ticks in one fight (was dying after 4) |

**Icons require real Unicode.** See §4 — this is the single most expensive lesson here.

---

## 3. The recipe

### 3.1 Define the buff behaviour — `build_buffs_set()`
`buffs_set.globalBuffs["<id>"]`. Keys are **camelCase** here (unlike the statMods row):
`buffType`, `valueType`, `value`, `displayValue`, `hasDuration`, `time:{amount}`, `group`,
`scope`, `modeAvail`, `p`.

`p` is the per-type parameter object — e.g. `{"damage_type":"bleed"}` for damage,
`{"key":"_ftd","style":0}` for floating text.

### 3.2 Define the modifier — `build_stat_modifiers()`
`statMods["<id>"]`. Keys are **short codes**, and several are **lists** — a wrong accessor
type yields a silent empty value and no error anywhere:

```
t    buff type   -> MUST equal the buffs_set globalBuffs id
tr   LIST        triggers, camelCase (onHit, onCrit, onSpecialActivate, onIntroStart, ...)
uit  LIST        UI triggers
a    LIST        appearance ids -> statModAppears
trr  text        trigger rate: none | update | once | repeat
c    decimal     chance, 1.0 = always
m    decimal     magnitude: an ABSOLUTE TOTAL, not a fraction of Attack.
                 Per tick = m / d / 2 (tick interval is 0.50s). See the note below.
d    decimal     duration seconds; -1.0 = INDEFINITE
ta   text        target: none | self | opponent (alias opp) | owner | tower | opp_tower
mt   text        BITFLAG: buff | debuff | passive | passive_buff | passive_debuff
st   int         stack count
```

**The three enum fields, read off the client rather than inferred.** An earlier revision of
this table listed only the values we happened to be using, which is how you end up authoring a
value the client silently ignores:

| field | client enum | values |
|---|---|---|
| `trr` | `BuffTriggerRate` | `none=0` `update=1` `once=2` `repeat=3` |
| `ta` | `BuffTargetTypes` | `none=0` `self=1` `opponent=2` `opp=2` `owner=3` `tower=4` `opp_tower=5` |
| `mt` | `BuffModTypes` | `none=0` `buff=1` `debuff=2` `passive=4` `passive_buff=8` `passive_debuff=16` |

Three traps in that table:

1. **`mt` is a bitflag set** — the values are powers of two. `passive_buff` is **8**, *not*
   `passive|buff` (which would be 5). Do not compose these by OR-ing; use the named value.
2. **`opponent` and `opp` are the same value (2).** Two spellings, one meaning. A table
   generated from the enum will look like it has seven targets; it has six.
3. **`ta` selects which `BuffsController` owns the buff.** The client holds
   `Dictionary<BuffTargetTypes, BuffsController>` — one controller per target type. `ta` is not
   a hint or a label; it decides which combatant's controller the effect is installed on. This
   is why `ta` and `mt` are independent (§8): `ta` is placement, `mt` is classification.

`Damage_BuffEffect` matches on the **`dmg_` prefix** of `t`, so damage buff types must be
named `dmg_*`.

### 3.2.1 The asset channels — everything an ability can drive

The `statMods` row decides *what happens*. These decide *what the player perceives*. The
presentation record is `BCGStatModifierAppearance`; the rest are parameters on effect classes.

| channel | wire field / effect | consumed by | authorable from the server |
|---|---|---|---|
| ability name | `AbilityTitleID` | ability/info panels | yes |
| descriptions ×3 | `ShortStringID`, `LongStringID`, `SimpleStringID` | info panels | yes |
| **icon** | `IconTexture` | HUD + info panels | yes — §4 |
| **particle FX** | `FXProfile` | FX system | yes — **we currently serve `""`** |
| **callout text** | `CalloutStringID` | `HudScreen.PlayCallout` | yes |
| **callout colour ×3** | `CalloutTextColor`, `…GradientTop`, `…GradientBottom` | same | yes — **we serve `#FFFFFF` for all** |
| pause-screen text ×2 | `PauseShortStringID`, `PauseLongStringID` | pause UI | yes |
| floating number | `FloatingText_BuffEffect` | `HudScreen.PlayFloatingText` | yes, but see §8.2 ⑦ |
| streak counter | `HudScreen.ShowStreakCounter` | HUD | untested |
| HUD banner | `HudAnnouncer_BuffEffect._message` | HUD | yes, untested |
| animation override | `OverrideAnimation_BuffEffect` | animator | yes, untested |
| forced move | `PlayMove_BuffEffect._sequencer` | animator | yes, untested |
| time scale | `SpeedCurve` / `SlowdownCurve_BuffEffect` | combat sim | yes, untested |
| spatial zone | `CreateArea_BuffEffect` — `_prefabName`, `_radius`, `_offset` | world | yes, prefab must exist |
| **audio** | — | — | **NO PATH** |

**There is no audio channel.** No `Audio_BuffEffect` exists and the appearance record has no
sound field. Two independent sweeps of all effect classes found none. A sound can only reach the
player by riding on something else — an `AudioSource` on a particle prefab, or an animation
event — so it is a property of the asset you reference, never of the ability row.

The three rows in bold are live channels we currently send empty or blank. They need no new
mechanism, only values.

### 3.3 Define the appearance — `build_stat_mod_appears()`
`statModAppears["<id>"]`. **These wire keys were read off the client itself** (see §6), not
guessed:

```
t  -> IconTexture      (the effect glyph — see §4)
f  -> FXProfile
a  -> AbilityTitleID
s  -> ShortStringID
l  -> LongStringID
st -> CalloutStringID
tc -> CalloutTextColor        gt -> gradient top        gb -> gradient bottom
```

### 3.4 Grant it to bots
Add the modifier id to that bot's list in **`bot_abilities(bid)`** — one function, one edit.
Do not touch the builders; they all read from it (§5).

### 3.5 Regenerate, or nothing happens
```bash
export PATH="$PWD/../.cargo-home/bin:$PATH"
legible run Server/gamedata.lbl      # no args
```
`Server/responses/GET__bcg_getLoginData.json` is a **static file that overrides**
`build_login_data()`. Editing `gamedata.lbl` without regenerating changes nothing. This has
cost multiple wasted test cycles.

---

## 4. Effect icons are font glyphs — and need real codepoints

Ability/effect icons are **not textures**. They are glyphs in `Tecnica_Bold_116`
(`assets/bin/Data/811e9b20e41fd447796b1e264201af71`), addressed by **Private Use Area
codepoint**. The font maps 523 PUA codepoints; only ~72 are referenced by the client's own
string table (UI chrome). The remaining ~451 are referenced nowhere — which is exactly the
signature of glyphs intended to arrive in **server-supplied strings**. Effect icons sit
mostly in **E401–E60C**.

Put the **raw codepoint** in the appearance record's `t`:

```
E402 bleed      E412 shock      E41D burn
```

**There is no `@@XXXX@@` escape, or any other ASCII marker.** `grep -o '@@'` over the
client's string-literal table returns **zero**. Any ASCII placeholder will be rendered
literally by an icon-only font, i.e. as visible garbage.

Emitting a raw codepoint requires the **jsonout Unicode round-trip fix**; before it,
`jsonout` refused non-ASCII outright (`[!] jsonout: non-ASCII text is not supported`). It
emits a standard `\uXXXX` escape, which is valid JSON and decodes client-side.

### 4.1 Which glyph means what — the candidate map

`Tecnica_Bold_116` maps **523 PUA codepoints**. They are not all icons, and the font tells you
which are which through its own glyph names:

| group | count | how to tell | use? |
|---|---|---|---|
| small-caps typography | 127 | glyph name contains `.sc` (`a.sc`, `k.sc`, `thorn.sc`) | **no** — letterforms |
| descriptively named | 56 | real names (`shield_bleed`, `heart`, `spade`) | **yes, authoritative** |
| unnamed | 340 | named `uniEXXX` | candidates, identify by eye |

**Filter on `.sc` as a substring, not a suffix** — `i.sc.loclTRK` is a small cap that ends in
`.loclTRK` and slips a suffix test.

**72 codepoints are referenced by the client's own string table.** Those are UI chrome the game
already draws — faction badges, rank chevrons, calendars. Avoid them for effects unless you
intend the overlap; the remainder are unreferenced, which is the signature of glyphs meant to
arrive in server-supplied strings.

A working candidate map, one ability per row:

| ability | codepoint | ability | codepoint |
|---|---|---|---|
| Bleed | `U+E414` | Stagger | `U+E810` |
| Shock / Overcharge | `U+E914` | Stun | `U+E15E` |
| Burn | `U+E41D` | Disable run | `U+E933` |
| Poison | `U+E50C` | Disable special | `U+E953` |
| Acid / Corrosion | `U+E40A` | Speed up | `U+E952` |
| Direct damage | `U+E404` | Slow | `U+E901` |
| Heal | `U+E93A` | Attack chain | `U+E41F` |
| Protection | `U+E512` | State enable | `U+E942` |
| Resist damage | `U+E949` | State disable | `U+E99C` |
| Armour break | `U+E516` | Power gain | `U+E91B` |
| | | Power sting / drain | `U+E905` |

⚠️ **Status of this table: visual identification, not verified in-game.** It is offered so you
do not start from 523 unknowns. **None of these codepoints are served today** — `Server/gamedata.lbl`
still emits the original `E402`/`E412`/`E41D` set (see §4). The swap to `U+E414`/`U+E914` shipped
once, unverified in-game, and was reverted: a glyph you have only looked at in a font atlas is not
a glyph you have seen the client render. Adopt a row from this table only after it has been observed
in a running client, per the VERIFIED convention at the top of this document.

Three caveats worth inheriting:

- **A confident reading is not a correct one.** An earlier map recorded `U+E412` as *"fist
  wreathed in sparks — SHOCK"* at med-high confidence. On inspection of the atlas it is a **bare
  fist with no sparks**; the sparking fist is `U+E41F`. It was also marked "confirmed in-client" —
  but the screenshot only proved that the codepoint we sent rendered, which at icon size a fist does
  regardless. **Confirming that a glyph appears is not confirming that it means what you think.**
  `U+E412` is nevertheless still what the server emits for shock, because the replacement was
  reverted before it was ever seen in-game; the disagreement is recorded here, not silently
  re-decided.
- Where the font names a glyph, the name wins. `U+E50E` is `shield_bleed`, not the acid shield
  an earlier reading claimed; `U+E510` is `shield_mana`, not ice.
- `U+E512` is named `shield_new` and is used here for Protection on appearance alone. Treat as
  provisional.

---

## 5. Assignment — DONE, this is where you change who gets what

> ✅ **Built.** The refactor described here has been applied. Per-bot subsets now work.

**To change what a bot carries, edit `bot_abilities(bid)` in `Server/gamedata.lbl` — and
nothing else.**

```legible
public function bot_abilities(bid: text): a list of text
  intent: return
  if bid == "optimusprimal_bw_mp32" then ["kit_bleed", "kit_shock", "kit_burn"]
  else if bid == "some_other_bot" then ["kit_burn"]
  else no_abilities() end
end
```

> ⚠️ **`intent:` is mandatory and its omission is a hard compile error**, not a warning:
> `[E_UNEXPECTED_TOKEN] Missing intent declaration`. Every function in this codebase
> declares one. (The separate `E_INTENT_MISMATCH` output *is* only a warning — it complains
> that an intent's wording doesn't match the body, and the build still succeeds. Don't
> confuse the two.)
>
> The empty case is a named helper (`no_abilities()` returning `[]`) rather than a bare
> `[]` in the `else` branch, so the branch has an unambiguous `a list of text` type.

Each bot may name any subset of the authored ability ids — none, one, several, all —
independently of every other bot. Every id must exist in `build_stat_modifiers()`
(`statMods`) and should have a matching record in `build_stat_mod_appears()`.

One accessor, `bot_abilities_json(bid)`, is called by **all four** builders that attach
kits:

```
build_hero_base          — the roster/base record
build_hero_entry         — the owned-hero entry
build_base_hero_details  — the hero detail panel
quest_team               — THE FIGHT SQUAD
```

⚠️ **`quest_team` is the builder that governs combat and is the easy one to forget.** It
once hardcoded `[]` and silently stripped every ability from the story squad while the
other three looked correct. It is now wired to the shared accessor so it cannot drift —
**do not re-derive assignment in any builder.**

### What this replaced, and why it was a blocker

The grant condition was previously duplicated **verbatim four times** as one flat predicate
yielding one fixed list:

```legible
if bid == "optimusprimal_bw_mp32" or bid == "nemesisprime_gs_voyager2015"
   or bid == "optimusprime_cin_tf" or bid == "megatron_gs_leader2015"
then [kit_bleed, kit_shock, kit_burn] else [] end
```

That shape **cannot express permutations at all** — it has no way to say "this bot gets two
of the three". Every assignment change also meant four synchronized edits, with `quest_team`
the silent failure mode.

> "Give every bot all three kits" is **not** the generalization and was explicitly
> rejected. All-to-all is just a different hardcoding.

### How the refactor was verified

- **Behaviour-preserving:** all three regenerated payloads
  (`GET__bcg_getLoginData.json`, `GET__bcg_getUserData.json`, `GET__account_data.json`)
  are **byte-identical** to their pre-refactor versions.
- **Permutations actually work:** a throwaway subset produced three distinct assignment
  states in one payload — `optimusprimal_bw_mp32` all three, `optimusprime_cin_tf` exactly
  `["kit_burn"]`, `jetfire_gs_leader2014` `[]` — then was reverted and re-verified
  byte-identical.

Byte-identity alone would only prove nothing broke; the subset test is what proves the
capability exists. Apply the same pairing when changing this function.

---

## 6. The method that found all of this

A native hook (`tools/nativehook/hook.c`) logs **every JSON key the client reads and the
accessor type it used** to `/data/data/com.kabam.bigrobot/files/dotkeys.log`. Tags:
`hbL`=list, `fG`/`hbF`=float, `fS`=string, `fI`/`hbI`=int, `fB`=bool, `fO`=object,
`FDS2 name=<short> alt=<long>`=the short-code↔long-name pair.

`FDS2` lines are how the wire keys in §3.3 were established as fact rather than inference.

**Audit all keys at once, never one per test cycle.** Extract key→type from the log, diff
against what you serve, fix every mismatch in one pass. A wrong accessor type produces a
silent empty value and **no error anywhere** — that is the trap, every time.

---

## 7. Pitfalls — settled, do not relitigate

1. **No `@@` marker expander exists.** Zero `@@` literals in the client. An ASCII
   placeholder in an icon field renders as literal garbage. Use the real codepoint.
2. **A missing appearance record looks *better* than a broken one.** If the lookup misses,
   the client falls back to its own default icon and everything looks fine. Adding a record
   whose icon field is unrenderable replaces a working default with visible garbage. When an
   icon regresses, suspect the record you *added*, not one you're missing.
3. **Static responses override the builder.** Always regenerate (§3.5).
4. **`tr`, `uit`, `a` are lists.** Wrong accessor type = silent empty value, no error.
5. **Conditions ARE server-authorable — via `trs`.** (Corrected 2026-09-18; this section
   previously said no predicate system existed. It does.) Write them as
   `<target>:<key><op><value>`, e.g. `trs: "opponent:isAi=true"`. Parsed by
   `BuffTriggerFactory.ParseConditions` with regex `([\w\.]+)(=|<=|>=|!=|>|<)(.+)`.
   Operators: `=` `<` `>` `<=` `>=` `!=` — **single `=`, not `==`**. Readable keys:
   `arena canAttack class currAnim fightType heavyType isAi isFinalBoss playerID prevAnim
   state tags`. So *"opponent is class Y"* **is** expressible. *"below 20% health"* still is
   not — there is no health key. ⚠️ The value group is `(.+)`, so a misspelled value is
   syntactically valid and simply never true, with no error. Always confirm with a condition
   you predicted would pass.
6. **Three different classes are named `Buff`.** The combat one is
   `public sealed class Buff`; the config record from `buffs_set` is a different type, and a
   third unrelated `Buff` belongs to map tiles. Probing the wrong one burned three sessions
   chasing a magnitude that was never actually zero.
7. **`sig_lvl > 0` crashes the hero-detail path** for *every* bot, not just the awakened
   one. Parked deliberately. Signature abilities are out of scope until the assignment
   problem is solved.
8. **A liveness check only counts after the step under test**, and a running process proves
   nothing until the server log shows the client actually **requested the payload**
   (`getLoginData` + `getUserData` + `getBaseHeroData`). A client sitting on the title screen
   is "alive" and has consumed nothing.
9. **For a regression, diff the payload before reverse-engineering the client.** If the
   client binary did not change, the cause is in what you serve. Disassembly explains the
   mechanism; the diff finds the cause.
10. **`.lbl` gotcha — every function needs an `intent:` line.** Omitting it is a hard
    compile error (`[E_UNEXPECTED_TOKEN] Missing intent declaration`). The similarly-named
    `E_INTENT_MISMATCH` is only a warning about wording and does not fail the build; the
    codebase emits several of these already on unrelated functions, so do not read them as
    breakage you introduced.
11. **Verify any code sample you publish by compiling it.** The §5 sample in this very
    document shipped once without its `intent:` line and would have failed for the first
    person who copied it. A replication guide that does not compile is worse than none.

---

## 8. The ability glossary — combinations that work, and that do not

A designer picking a kit needs to know, per ability, **what it does and who it lands on**.
That is the glossary. §8.1 and §8.2 below are the usable core of it; what remains is
per-ability detail, not new reverse-engineering.

### The axis that matters: `ta` and `mt` are INDEPENDENT

```
ta   who RECEIVES the effect        self | opponent
mt   how it is CLASSIFIED/displayed buff | debuff | passive
```

These are **two separate fields and the engine does not tie them together.** The catalogue
records a *conventional* pairing per effect — `armor_break` is documented as
`ta:"opponent"` + `mt:"debuff"` — but that is convention, not enforcement. Nothing prevents
serving `ta:"self"` + `mt:"buff"` on `armor_break` and handing your own hero a penalty
presented as a blessing.

**So "buff the hero" vs "debuff the opponent" is a pair of choices, not one.** A usable
glossary must state both per ability, and should make the incoherent combinations
impossible to express rather than merely discouraged.

⚠️ **Open question, do not assume either way:** whether `mt` is purely presentational
(grouping/colour in the buff HUD) or also drives mechanics. Untested. Settle it before the
glossary asserts a meaning for it.

What *is* settled: **`ta` is placement, not labelling.** The client keys its buff controllers
by target type — `Dictionary<BuffTargetTypes, BuffsController>` — so `ta` chooses whose
controller receives the effect. That is the mechanical reason the two fields cannot be folded
into one.

### 8.1 Combinations that work

Four cells of the `ta` × `mt` matrix cover almost every kit you would want:

| intent | `ta` | `mt` | example |
|---|---|---|---|
| harm the opponent over time | `opponent` | `debuff` | bleed, burn, shock — **proven in a live fight** |
| strengthen your own bot | `self` | `buff` | attack up, regeneration |
| a standing penalty on yourself | `self` | `debuff` | a drawback traded for a stronger effect |
| an always-on trait | `self` | `passive` | permanent stat shaping, `d: -1.0` |

`owner`, `tower` and `opp_tower` exist for base-defence contexts and are outside anything
proven here.

### 8.2 Combinations that DO NOT work — and why

These are not style advice. Each one produces silence, and silence in this system looks
identical to "the feature is broken".

**1. A row no hero references is never deployed.**
The single most expensive mistake available. A `statMods` row is a *definition*; it does
nothing until some hero's `stat_mods` list names it. The client auto-applies exactly five ids
of its own accord — `gp_attack_chain`, `gp_hit_stun`, `gp_close_atk_window`, `gp_disable_run`,
`gp_sp3_minhp` — and **nothing else**. Everything else must be granted (§5). A perfectly
authored row that nobody references parses cleanly, validates, and does nothing forever.

**2. `t` that does not exactly equal a `buffs_set.globalBuffs` id.**
The binding is by exact string. A typo yields a buff with no behaviour — the appearance may
still render, so you get an icon for an ability that does nothing. **Icon-without-effect is the
signature of this mistake.**

**3. A damage buff whose `t` lacks the `dmg_` prefix.**
`Damage_BuffEffect` matches on the prefix. `t: "bleed"` will not deal damage; `t: "dmg_bleed"`
will. Same signature as above — the icon still appears.

**4. `trr: "update"` with an empty `uit`.**
Inert. Update-mode with no update triggers is a buff told to refresh and never told when.

**5. Composing `mt` by OR-ing.**
`passive_buff` is `8`. `passive|buff` is `5`. Five is not a defined member.

**6. Assuming conditions do not exist.** *(Corrected 2026-09-18.)*
This section used to claim there was no predicate system. That was wrong — see pitfall 5 in
§7 for the `trs` format. Beyond the two id-matching classes (`ActiveId_BuffCondition`,
`ContainsBuff_BuffCondition`) there is a full comparison family — `BuffFloatCondition`,
`BuffIntCondition`, `BuffBoolCondition`, `BuffBitFieldCondition`, `BuffSetCondition<T>`,
`BuffSetOverlapCondition<T>` — driven by `BuffConditionOp`
(`Equal GreaterThan LessThan GreaterThanOrEqual LessThanOrEqual NotEqual`). The real limit is
narrower than "no conditions": there is **no health key**, so *"below 20% health"* genuinely
cannot be expressed, and neither can *"after 3 hits"*.

**7. Serving `m` as a fraction, expecting it to scale with Attack.** *(Added 2026-09-18.)*
`m` is an **absolute total**, spread across the duration:

```
per-tick value = m / d / 2        (tick interval is 0.50s, so 2 ticks per second)
```

Verified in a live fight to three decimals on two independent rows:
`m=250, d=7.0` produced `17.8571` per tick (250/7/2 = 17.857), and `m=400, d=6.0` produced
`33.3333` (400/6/2 = 33.333). `Damage_BuffEffect.OnTick` contains no multiply against any
attack attribute — it uses `Buff._amount` directly.

**Why this is the worst silent failure in the system:**
`HudFloatingTextController.Play` takes an **`int`**, and `HudFloatingText.Config.Amount` is an
`int`. Author `m: 0.30` believing it means "30% of Attack" and you get `0.30 / 7 / 2 = 0.0214`
per tick, which truncates to **`0`**. The HUD then correctly displays zero, every tick, with
no error anywhere. The ability registers, ticks, passes its conditions, reaches the HUD cache,
and appears completely dead. **If an ability seems to do nothing, log the magnitude before you
debug the plumbing.**

⚠️ Note `statMods` rows are **global**, so a single absolute `m` cannot be balanced across
bots of very different Attack. Per-bot scaling via `gc`/`gcv`/`rcv` is unexplored.

**8. Expecting a floating number for an effect that has no producer.**
`HudFloatingTextStyleFlags` offers ten styles including `Fury` and `Weakness`, but only two
cache keys exist in the entire binary — `_ftd` (`Damage_BuffEffect`) and `_fth`
(`Heal_BuffEffect`). Damage and heal numbers have plumbing behind them. **Strengthen and weaken
numbers have a paint style and nothing that fills it.**

### Material that already exists
- **`research/ability-catalogue.md`** — the backbone. ~46 `*_BuffEffect` classes with
  mechanical function, required fields, `p` params, stacking rules, and a **proven vs
  untested** matrix: 3 live-verified, ~43 decompiled specs.
- **`research/ability-grammar.md`** — trigger/condition vocabulary.
- **`research/glyph-map.md`** — codepoint → what the icon depicts, with confidence marked.
- **`research/VOCABULARY.md`** — wire-format terms.

### What the glossary must add on top
1. **Plain-language effect description** a designer can read without the decompilation.
2. **`ta` / `mt` stated explicitly per ability**, not left to convention.
3. **Proven vs untested carried through prominently** — ~43 of ~46 effects have never been
   run. A glossary that presents all of them as equally available will send designers into
   untested engine paths.
4. **The icon** the player will actually see, tied to its codepoint.

**Single source of truth:** generate the glossary from the same table that drives the
grants (§5), or it will drift from what the server actually serves. Do not hand-maintain a
parallel list.

---

## 9. Planned — the verification campaign

Of ~46 catalogued effects, **3 are live-verified** (`dmg_bleed`, `dmg_shock`, `dmg_burn`).
The other ~43 are decompiled specs that have never been executed. Verifying them is the next
step, and the point of this section is that **it is not 43 fights.**

Fight cycles are the scarcest resource in this project — only a human can play one. Design
the campaign accordingly.

### 9.1 Do not multiply the matrix: it is 43 + 11, not 43 × 11

There are two untested axes: ~43 **effects** and 11 untested **triggers** (`onCrit`,
`onBlocked`, `onSpecialActivate`, `onHpLost`, …). Testing the cross product is ~470
combinations and is pointless. Vary one axis at a time against a known-good control:

- **To test an effect** — pair it with the proven trigger: `tr:["onHit"]`, `c:1.0`.
- **To test a trigger** — pair it with a proven effect: `dmg_bleed` at a visible magnitude.
  Eleven variants of the same known-good effect, one fight, read which ones fired.

If a test changes two unknowns at once its result is uninterpretable. This is the single
most common way to waste a fight.

### 9.2 Tier what "verified" means

Most of the value is in T1, and T1 is nearly free:

| tier | question | how observed | batchable |
|---|---|---|---|
| **T1 registers** | parsed, granted, registered in combat, does not crash | `KITREG2` / `KITREG4` hook lines | **yes, ~10 per fight** |
| **T2 fires** | the trigger actually delivers it | hook line at trigger time | partly — needs the trigger to occur |
| **T3 behaves** | produces the intended mechanical result | hook values, HP/power deltas, frames | usually one at a time |

**Getting all ~43 to T1 first is the highest-value move in the whole campaign.** It costs
about two fights and tells you which effects are real and which detonate — before anyone
designs a kit around them. Recall that `sig_lvl > 0` crashes the hero-detail path for every
bot (§7.7); assume other landmines exist and find them cheaply.

### 9.3 Natural batches

Group by how the effect is observed, not by what it means:

| batch | members | notes |
|---|---|---|
| **Variables** | `set_var` `add_var` `clear_var` `set_tel_var` `add_tel_var` `clear_tel_var` | Pure state. Verifiable from hooks with **zero gameplay observation** — plausibly all six in one fight. Cheapest batch; do it first. |
| **Numeric** | `heal` `power_gain` `power_sting` `protection` `resist_*` `armor_break` `attack_chain` `stagger` `speed_curve` `slowdown_curve` `<attr_name>` `<attr_name>_flat` | Change a stat/HP/power the hooks already log with amount and duration. Batch 6–8. |
| **Buff-graph ops** | `nullify` `purify` `remove` `refresh` `refresh_id` `purge` `copy_buffs` `sequence` | ⚠️ These operate **on other buffs** and cannot be tested standalone. Each needs a paired setup: apply a known buff, then the operator, observe removal/refresh. Naturally batched in pairs. |
| **Audiovisual** | `create_area` `area_ring_spawner` `area_line_spawner` `override_anim` `clear_override_anim` `play_misc_anim` `play_move` `announcer` | Needs frames. Burst-capture via `adb exec-out screencap` through the fight and judge from stills — real-time observation is unreliable, a fight is too busy to watch for a specific cue. |
| **Interfering — ISOLATE** | `disable_sp1` `disable_sp2` `disable_sp3` `disable_run` `state_disable` `swap_ai` `ai_rage_mod` | ⚠️ **Never put these in a mixed batch.** `disable_sp*` blocks the special attacks you need in order to trigger other effects, so it silently poisons every other result in the fight. |

Realistically **~10–12 fights**, not 43.

### 9.4 Swapping a batch — ✅ the prerequisite is already done

Batch verification means assigning ~8 effects to one bot and swapping the whole set between
fights. **That is now a one-line edit to `bot_abilities(bid)` (§5)** — previously it meant
editing a hardcoded predicate in four builders per batch, a dozen times over, with
`quest_team` the easy one to forget, which would have turned every result in that fight
into a false negative.

Recommended shape: give one dedicated bot the batch under test and leave the rest of the
squad empty, so any effect you observe is unambiguously attributable.

### 9.5 Record results where they will be believed

Update the proven-vs-untested matrix in `research/ability-catalogue.md` as each effect
promotes, and cite the evidence — the hook line or the frame — the way the existing three
entries do. An effect marked "proven" without a citation is worth nothing to the person who
has to trust it later, and the glossary (§8) reads from this distinction.

---

<!-- 2026-09-14 Claude Opus 5: written at the repo owner's request so downstream
     contributors and their agents can reproduce this pipeline and skip our dead ends.
     Every VERIFIED claim was observed in a running client. -->
