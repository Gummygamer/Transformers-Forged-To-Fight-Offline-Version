# Authoring abilities from the server

> Status: the end-to-end pipeline is **proven**. Generalized assignment is **not built yet**
> — that is the current objective, stated below.

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

The real problem is **assignment**, and it is unsolved:

1. **Solve all permutations.** Any bot may carry any subset of the available abilities —
   zero, one, several, all — chosen independently per bot. Today the grant is a hardcoded
   four-bot, three-ability, all-or-nothing expression (see §5).
2. **Then** begin the per-bot design phase — deciding *which* kit each bot should actually
   have. That is a game-design activity and must not start until (1) makes it cheap to
   express.

Do not confuse the two. Authoring one more hardcoded kit is not progress toward (1).

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
Add the modifier id to the hero's `stat_mods` list — in **all four** builders (§5).

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

## 5. The blocker to generalize — read this before designing anything

Kits attach in **four** separate builders, and the grant condition is currently duplicated
**verbatim four times**:

```
build_hero_base          — the roster/base record
build_hero_entry         — the owned-hero entry
build_base_hero_details  — the hero detail panel
quest_team               — THE FIGHT SQUAD
```

```legible
if bid == "optimusprimal_bw_mp32" or bid == "nemesisprime_gs_voyager2015"
   or bid == "optimusprime_cin_tf" or bid == "megatron_gs_leader2015"
then [kit_bleed, kit_shock, kit_burn] else [] end
```

Two consequences:

- **`quest_team` is the one that actually matters in combat, and it is easy to miss.** It
  once hardcoded `[]` and silently stripped every ability from the story squad while the
  other three builders looked correct.
- **This shape cannot express permutations at all.** It is one flat predicate yielding one
  fixed list. Arbitrary per-bot subsets need a single source of truth — a bot→abilities
  table consulted by one shared accessor that all four builders call.

**That refactor is step 1 of the objective.** Do it before authoring more content.

> Note: "give every bot all three kits" is **not** the generalization and has been
> explicitly rejected. All-to-all is just a different hardcoding.

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

---

<!-- 2026-09-14 Claude Opus 5: written at the repo owner's request so downstream
     contributors and their agents can reproduce this pipeline and skip our dead ends.
     Every VERIFIED claim was observed in a running client. -->
