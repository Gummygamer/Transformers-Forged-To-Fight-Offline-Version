# Authoring abilities from the server

> Status: the end-to-end pipeline is **proven** and **generalized assignment is built**
> (§5). What remains is verifying the ~43 untested effects (§9), then per-bot design (§2 of
> the objective) and the glossary (§8).

This document exists so the next person (or agent) can reproduce this work without
re-deriving it, and without re-litigating the dead ends listed at the bottom. Everything
marked **VERIFIED** was observed in a running client, not inferred from decompilation.

---

## 1. The objective

**Make every ability assignable to any bot, in any combination.**

The bleed/shock/burn kit that exists today was a **test vehicle**, not the goal. It proved
the chain end to end — server JSON → parsed → granted per-bot → registered in combat →
triggered on hit → ticking damage → icon and floating number on screen. That question is now
closed.

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
| Floating damage numbers render | numerals observed over both combatants |
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
trr  text        trigger rate: repeat | update | none
c    decimal     chance, 1.0 = always
m    decimal     magnitude
d    decimal     duration seconds; -1.0 = INDEFINITE
ta   text        target: self | opponent
mt   text        buff | debuff | passive
st   int         stack count
```

`Damage_BuffEffect` matches on the **`dmg_` prefix** of `t`, so damage buff types must be
named `dmg_*`.

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
5. **Client-side conditions are thin.** Only two condition classes exist
   (`ActiveId_BuffCondition`, `ContainsBuff_BuffCondition`) — essentially "is buff X active".
   There is **no generic predicate system**: "below 20% health" or "opponent is class Y"
   cannot be expressed as a condition. It must come from the trigger type or a preset
   `buffType`. Do not design kits around conditions that do not exist.
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

## 8. Planned — the ability glossary (not built yet)

A designer picking a kit needs to know, per ability, **what it does and who it lands on**.
That is the glossary. It is not yet written, but most of the raw material exists and it is
mostly an organizing job, not new reverse-engineering.

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
