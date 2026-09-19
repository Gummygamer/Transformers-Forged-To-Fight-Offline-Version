# Authoring abilities from the server

## Table of contents
- [About this document](#about-this-document)
- [Start here — the three layers](#start-here--the-three-layers)
  - [The objective](#the-objective)
  - [Three layers, and why the words keep colliding](#three-layers-and-why-the-words-keep-colliding)
- [The row — six questions, twenty-eight fields](#the-row--six-questions-twenty-eight-fields)
- [Vocabulary — what legally fits in each slot](#vocabulary--what-legally-fits-in-each-slot)
  - [Target — ta](#target--ta)
  - [Modifier type — mt](#modifier-type--mt)
  - [The axis that matters: ta and mt are INDEPENDENT](#the-axis-that-matters-ta-and-mt-are-independent)
  - [Combinations that work](#combinations-that-work)
  - [Triggers — tr](#triggers--tr)
  - [Conditions — trs](#conditions--trs)
  - [States — values for the state key](#states--values-for-the-state-key)
  - [Stats — what a modifier can change](#stats--what-a-modifier-can-change)
  - [Effect icons are font glyphs — and need real codepoints](#effect-icons-are-font-glyphs--and-need-real-codepoints)
  - [Which glyph means what — the candidate map](#which-glyph-means-what--the-candidate-map)
- [Effect classes — the t registry and tm parameters](#effect-classes--the-t-registry-and-tm-parameters)
  - [Capability map — what is possible, graded honestly](#capability-map--what-is-possible-graded-honestly)
  - [How tm actually reaches a class](#how-tm-actually-reaches-a-class)
  - [The t registry](#the-t-registry)
  - [The tm keys](#the-tm-keys)
- [The recipe — building an ability end to end](#the-recipe--building-an-ability-end-to-end)
  - [Define the buff behaviour — build_buffs_set()](#define-the-buff-behaviour--build_buffs_set)
  - [Define the modifier — build_stat_modifiers()](#define-the-modifier--build_stat_modifiers)
  - [The asset channels — everything an ability can drive](#the-asset-channels--everything-an-ability-can-drive)
  - [Define the appearance — build_stat_mod_appears()](#define-the-appearance--build_stat_mod_appears)
  - [Assignment — grant it to bots](#assignment--grant-it-to-bots)
  - [Regenerate, or nothing happens](#regenerate-or-nothing-happens)
- [Worked examples](#worked-examples)
  - [Jamming Field](#jamming-field)
  - [Killing Edge](#killing-edge)
  - [The two open questions](#the-two-open-questions)
  - [How to ask for an ability](#how-to-ask-for-an-ability)
- [Pitfalls — settled, do not relitigate](#pitfalls--settled-do-not-relitigate)
  - [Combinations that DO NOT work — and why](#combinations-that-do-not-work--and-why)
  - [Other pitfalls](#other-pitfalls)
  - [Corrections: what this page got wrong, and why](#corrections-what-this-page-got-wrong-and-why)
- [What's proven, and how we know](#whats-proven-and-how-we-know)
  - [What is proven today — VERIFIED in a live fight](#what-is-proven-today--verified-in-a-live-fight)
  - [Provenance legend](#provenance-legend)
  - [Planned — the verification campaign](#planned--the-verification-campaign)
  - [Do not multiply the matrix: it is 43 + 11, not 43 × 11](#do-not-multiply-the-matrix-it-is-43--11-not-43--11)
  - [Tier what "verified" means](#tier-what-verified-means)
  - [Natural batches](#natural-batches)
  - [Swapping a batch — the prerequisite is already done](#swapping-a-batch--the-prerequisite-is-already-done)
  - [Record results where they will be believed](#record-results-where-they-will-be-believed)
  - [What the glossary must add on top](#what-the-glossary-must-add-on-top)
- [Appendix A — the method that found this](#appendix-a--the-method-that-found-this)
- [Appendix B — environment and toolchain](#appendix-b--environment-and-toolchain)
  - [The APK](#the-apk)
  - [The emulator](#the-emulator)
  - [Host toolchain](#host-toolchain)
- [Appendix C — project history](#appendix-c--project-history)
  - [What this replaced, and why it was a blocker](#what-this-replaced-and-why-it-was-a-blocker)
  - [How the refactor was verified](#how-the-refactor-was-verified)
  - [Material that already exists](#material-that-already-exists)

## About this document

**This is the single source of truth for server-authored abilities.** Markdown is canonical —
please don't fork a second copy in another format, because two documents describing the same
wire format will disagree within weeks. This one already carried a wrong claim for days for
exactly that reason (see *Pitfalls*).

**Need HTML, Word or PDF?** Generate it, don't rewrite it:

```bash
# HTML with a sidebar table of contents
pandoc ABILITY_AUTHORING.md -o abilities.html --standalone --toc --toc-depth=3

# Word, with a working navigation pane
pandoc ABILITY_AUTHORING.md -o abilities.docx --toc --toc-depth=3

# PDF
pandoc ABILITY_AUTHORING.md -o abilities.pdf --toc --toc-depth=3
```

The structure is built to survive that conversion: heading levels are strict with no skipped
ranks, so Word's outline view and HTML's `<h1>`–`<h4>` nest correctly; tables are plain GFM;
code fences carry language tags; and there is no raw HTML to trip a converter.

**Every factual claim carries a provenance mark** showing how it is known — see the legend in
*What's proven, and how we know*. A claim with a single mark is provisional by definition.
When you add to this document, add the mark too; an unmarked claim is indistinguishable from
a guess, and guesses here have cost real debugging time.

## Start here — the three layers

### The objective

**Make every ability assignable to any bot, in any combination.**

The bleed/shock/burn kit that exists today was a **test vehicle**, not the goal. It proved
the chain end to end — server JSON → parsed → granted per-bot → registered in combat →
triggered on hit → ticking damage → icon on screen. That question is now closed.
(The *floating number* for ability damage is a separate, still-open problem — §8.2 ⑦.)

The real problem is **assignment**:

1. ✅ **Solve all permutations — DONE.** Any bot may carry any subset of the available
   abilities — zero, one, several, all — chosen independently per bot. A single
   `bot_abilities(bid)` table now drives all four builders. Verified behaviour-preserving
   *and* verified capable of expressing distinct per-bot subsets.
2. **Then** begin the per-bot design phase — deciding *which* kit each bot should actually
   have. That is a game-design activity and must not start until (1) makes it cheap to
   express.
3. **Alongside (2), build the ability glossary**. Designers cannot choose kits
   sensibly until each ability states, in plain language, what it does and **who it lands
   on**.

Do not confuse these. Authoring one more hardcoded kit is not progress toward (1).

### Three layers, and why the words keep colliding

“Synergy,” “evade,” and “teleport” each belong to a different layer of this system, but in conversation they sound like the same kind of request. Separating them dissolves most of the confusion.

**Layer 1: Grant**
Which bots *have* the row at all. Decided entirely by our server as it builds the payload.
**Synergy lives here** — it is a grant rule, never an effect.
**[👁 live]** **[📡 served]**

**Layer 2: Activation**
When it fires — a trigger event, an optional predicate, and a chance roll.
**Triggers and conditions live here.**
**[👁 live]** **[📄 binary]**

**Layer 3: Effect**
What happens — which effect class runs, and the parameters it reads.
**Evade and teleport live here.**
**[👁 live]** **[📄 binary]**

> [!NOTE]
> **The reframe:** “these three bots together unlock an ability” is not an effect to build — it is a Layer 1 grant rule, and it already works. “Evade” is not a trigger; it is a Layer 3 effect. “Teleport” is not one ability; it is several Layer 3 effects in sequence.

[↑ Contents](#table-of-contents)

## The row — six questions, twenty-eight fields

A `statMods` row has 28 fields grouping into six questions. Answer these six in order and you have specified an ability completely.

**When does it fire?**
`tr`, `trr`, `c`
Trigger events (a **list**), repeat mode, 0–1 chance roll.
**[👁 live]**

**Only if what's true?**
`trs`
One predicate: `target:key op value`. Optional.
**[👁 live]**

**On whom does it land?**
`ta`, `mt`
Target, and modifier type — which includes the passive flags.
**[👁 live]**

**What does it do?**
`t`, `tm`
`t` selects the effect class; `tm` carries that class's parameters.
**[👁 live]**

**How much, how long?**
`m`, `d`, `st`
Magnitude, duration in seconds, stack count.
**[👁 live]**

**What does the player see?**
`a`
Appearance id (a **list**) → icon and name in the panel.
**[👁 live]**

> [!WARNING]
> **The arithmetic that has burned us most.** `m` is an *absolute total* spread across the duration. Per tick you get `m ÷ d ÷ 2`, and the renderer takes an **integer**. So `m: 0.3` over 7 seconds is `0.021` per tick → displays as **0** and looks broken while being perfectly correct. Use `m: 250`. **[👁 measured 17.8571]**
>
> ⚠ This was derived for **damage-over-time only**. Whether it governs stat modifiers is untested. **[⚠ inferred]**

[↑ Contents](#table-of-contents)

## Vocabulary — what legally fits in each slot

A value outside these lists is not an error. It silently never matches — the single most expensive failure mode in this system, and the reason nothing here is abbreviated.

### Target — ta

7 names, 6 distinct values · dump.cs:401235
`self=1`, `opponent=2`, `opp=2`, `none=0`, `owner=3`, `tower=4`, `opp_tower=5`

**Not a bitflag.** `opp` is an alias for `opponent`, same value. Serialised as the **name string**, parsed via `EnumCache.ParseEnum` — so any member name is legal spelling. **[⚙ 0xEE853C]** **[👁 self, opponent]**

### Modifier type — mt

6 values · dump.cs:401160
`buff=1`, `debuff=2`, `passive=4`, `none=0`, `passive_buff=8`, `passive_debuff=16`

**This one IS a bitflag** — but `passive_buff` is `8`, not `passive|buff` (5). Serialise the **name string**, not the number: our working rows carry `mt:"passive"`, never `mt:4`. **[📡 served]**

**Passives still carry triggers.** `gp_dmg_ft` is served as `mt:"passive"` *with* `tr:["onIntroStart"]`, and it executes. So “passive” does not mean “no trigger”. **[📡 served]** **[👁 live]**

### The axis that matters: ta and mt are INDEPENDENT

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

### Combinations that work

Four cells of the `ta` × `mt` matrix cover almost every kit you would want:

| intent | `ta` | `mt` | example |
|---|---|---|---|
| harm the opponent over time | `opponent` | `debuff` | bleed, burn, shock — **proven in a live fight** |
| strengthen your own bot | `self` | `buff` | attack up, regeneration |
| a standing penalty on yourself | `self` | `debuff` | a drawback traded for a stronger effect |
| an always-on trait | `self` | `passive` | permanent stat shaping, `d: -1.0` |

`owner`, `tower` and `opp_tower` exist for base-defence contexts and are outside anything
proven here.

### Triggers — tr

34 values · TFormBuffTriggerConstants
`onHit`, `onIntroStart`, `onLightHit`, `onMediumHit`, `onHeavyHit`, `onRangedHit`, `onNormalHit`, `onCrit`, `onBlocked`, `onBlockedResolved`, `onHitStarted`, `onHitResolved`, `onPreDamage`, `onAttackEnded`, `onSpecialActivate`, `onSpecialHit`, `onSpecial1Activate`, `onSpecial1Hit`, `onSpecial2Activate`, `onSpecial2Hit`, `onSpecial3Activate`, `onSpecial3Hit`, `onSpecial3Expiry`, `onHpGain`, `onHpLost`, `onPowerGain`, `onPowerLost`, `onPlayerStateEnter`, `onAnimStateEnter`, `onAnimStateExit`, `onAreaEnter`, `onAreaExit`, `onAreaEnable`, `onAreaDisable`

Only **two of 34** have ever fired in a fight. The rest are **[📄 binary]** only. `onPlayerStateEnter` and the four `onArea*` triggers open categories we have never touched.

### Conditions — trs

12 keys · 6 operators · grammar proven

Format is `target:key` + operator + value. Single `=`, never `==`.

Keys: `isAi`, `class`, `state`, `tags`, `arena`, `canAttack`, `currAnim`, `prevAnim`, `fightType`, `heavyType`, `isFinalBoss`, `playerID`
Operators: `=`, `!=`, `<`, `>`, `<=`, `>=`

**No health key exists.** “Below 20% HP” cannot be expressed. The grammar itself is proven — matched pass/fail rows differing only in operator. **[👁 live]** Only the `isAi` key has been exercised; the other 11 are **[📄 binary]**.

### States — values for the state key

19 values · dump.cs:406148
`Idle`, `Run`, `Block`, `Attack`, `Shoot`, `HeavyAttack`, `Dodge`, `Dash`, `Sidestep`, `HitReact`, `SpecialAttack`, `CinematicSpecial`, `Cinematic`, `Dead`, `Stun`, `EvadeMelee`, `EvadeRanged`, `Reflect`, `Misc`

These are readable as a *condition* — you can gate on “while they're stunned”. Whether any can be **caused** from the server is a separate, open question. **[📄 binary]**

### Stats — what a modifier can change

11 attributes · dump.cs:387120
`HP`, `MaxHP`, `Attack`, `Armor`, `Mana`, `ManaGain`, `CritChance`, `CritDamage`, `StunChance`, `BlockProficiency`, `PerfectBlockChance`

Crossed with the four modifier types (`mod_percent`, `mod_flat`, and their `_id` variants), this is **44 distinct abilities from one pattern** — the largest untapped area we have. None fired yet. **[📄 binary]**

### Effect icons are font glyphs — and need real codepoints

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

### Which glyph means what — the candidate map

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
still emits the original `E402`/`E412`/`E41D` set. The swap to `U+E414`/`U+E914` shipped
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

[↑ Contents](#table-of-contents)

## Effect classes — the t registry and tm parameters

### Capability map — what is possible, graded honestly

| Family | What a player calls it | `t` values | Evidence |
|---|---|---|---|
| **Damage over time** | Bleed, burn, shock, poison, acid — one class, five skins | `dmg_` | **[👁 live]** |
| **Restore** | Heal and regeneration | `heal` | **[👁 live]** |
| **Power & tempo** | Power gain, armour shred, power drain on hit | `power_gain` · `armor_break` · `power_sting` | **[👁 live]** |
| **Mitigation** | Damage shield, resistance, combo chaining | `protection` · `attack_chain` · `resist_` | **[📡 served]** |
| **Denial** | Stop them running; silence one specific special | `disable_run` · `disable_sp1/2/3` | **[📡 disable_run]** **[📄 sp1/2/3]** |
| **Stat modification** | Crit boost, armour shred, block mastery, power lock | `mod_percent` · `mod_flat` · `mod_id_*` | **[⚙ RVA]** |
| **Buff manipulation** | Purge, cleanse, steal a buff, refresh a timer | `purge` · `remove` · `copy_buffs` · `refresh` | **[📡 remove]** **[📄 rest]** |
| **Buffs that modify buffs** | Cancel, cleanse, stretch a duration, change proc chance | `nullify` · `purify` · `duration_*` · `effect_accuracy_*` | **[📄 binary]** |
| **Ability gating** | Turn another ability on or off mid-fight | `state_enable` · `state_disable` | **[📄 binary]** **[⚠ format]** |
| **Spatial** | Zones and traps — pairs with the `onArea*` triggers | `create_area` · `area_ring_spawner` · `area_line_spawner` | **[📄 binary]** |
| **AI manipulation** | Enrage the opponent, change how the AI fights | `ai_` · `ai_rage_mod` · `swap_ai` | **[📄 binary]** |
| **Animation & motion** | Knockback, stagger, speed change, animation override | `stagger` · `speed_curve` · `override_anim` · `play_misc_anim` | **[📄 binary]** |
| **Composition** | Never visible alone — chains other rows together | `sequence` · `announcer` · `*_var` · `*_tel_var` | **[📡 vars]** **[📄 sequence]** |

> [!NOTE]
> **Where we actually stand.** Of ~60 type strings, **6 have fired in a fight**, **8 more are served and accepted**, and the rest exist only in the binary. We have been using roughly seven mechanisms out of a vocabulary several times that size — and the gap is not missing capability, it is untested capability.

### How tm actually reaches a class

Whether an effect can take parameters at all is decided by its **constructor signature**, not by anything in the payload. There are three populations, and mixing them up produces an ability that runs and does nothing.

| Population | Count | What it means for `tm` | Evidence |
|---|---|---|---|
| **2-arg ctor**<br>(type, modType) | **11** | `tm` is **structurally ignored** — there is no parameter to receive it. Includes `heal`, `power_gain`, `attack_chain`, `disable_run`, `disable_sp*`, `ai_rage_mod`, `play_move`. | **[📄 binary]** |
| **3-arg ctor, ParamsTable**<br>(type, modType, strParams) | **18** | Parses `tm` as `key=value` pairs. These are the classes whose key names are tabulated below. | **[⚙ RVA]** |
| **3-arg ctor, no ParamsTable** | **19** | Accepts `tm` but parses it some other way — a raw string or a plain split. Includes `dmg_`, `protection`, `armor_break`, `purge`, `state_enable`. **The parse format for these is unknown.** | **[📄 binary]** **[⚠ format]** |

> [!NOTE]
> **An earlier version of this page said “31 classes take no parameters.”** That measured the wrong thing — whether a class constructs a `ParamsTable` — and conflated the first and third populations above. Classes like `state_enable` do receive `tm`; they just do not parse it with the key=value machinery.

### The t registry

Two dispatchers map the `t` string to a class. 60 values were recovered by resolving string literals inside their bodies.

**Game-specific factory**
31 values · TFormBuffEffectFactory @ 0xB07138
`dmg_`, `heal`, `power_gain`, `armor_break`, `power_sting`, `floating_text`, `protection`, `attack_chain`, `disable_run`, `play_move`, `resist_`, `disable_sp1`, `disable_sp2`, `disable_sp3`, `stagger`, `speed_curve`, `slowdown_curve`, `override_anim`, `clear_override_anim`, `play_misc_anim`, `create_area`, `area_line_spawner`, `area_ring_spawner`, `ai_`, `ai_rage_mod`, `swap_ai`, `announcer`, `avoid_tut`, `add_tel_var`, `set_tel_var`, `clear_tel_var`

**Base engine factory**
29 values · BuffEffectFactory @ 0xE5DCC0
`remove`, `set_var`, `clear_var`, `mod_percent`, `mod_flat`, `mod_id_percent`, `mod_id_flat`, `state_enable`, `state_disable`, `sequence`, `refresh`, `refresh_id`, `purge`, `copy_buffs`, `nullify`, `nullify_original`, `nullify_copied`, `purify`, `purify_original`, `purify_copied`, `duration_percent`, `duration_flat`, `duration_id_percent`, `duration_id_flat`, `effect_accuracy`, `effect_accuracy_flat`, `effect_accuracy_id`, `effect_accuracy_id_flat`, `add_var`

> [!WARNING]
> **This registry is not proven complete, and our own payload is the counterexample.** We serve `t:"hit_stun"` on the stock `gp_hit_stun` row. That string exists as a client literal — but it was *not* found in either dispatcher body. So either the scan missed a code path, or a third registration route exists (`RegisterCustomFactory(IBuffEffectFactory)` is present in the binary), or that stock row is inert.
>
> **Unresolved.** Treat the 60 as “known-good”, not “exhaustive”. **[⚠ completeness]**

### The tm keys

Recovered by resolving each ctor's string literals through a double indirection, then corroborated against each class's private field names — two independent signals agreeing.

| Class | `tm` keys | Corroborating fields |
|---|---|---|
| PercentModifier · FlatModifier | type, modType | _typeFullNames, _buffModifiers |
| PercentModifierForID · FlatModifierForID | id | _IDs |
| FloatingText | v, s, t | _key, _style **[👁 calibration]** |
| PowerSting | dmgType | _dmgType **[👁 calibration]** |
| Sequence · RefreshID | id | _ids, _IDs |
| Refresh | type, modType | _types, _refreshModTypes |
| RemoveBuffs | type, modType, source, removeOrder | _types, _removeFlags, _removeModTypes |
| CopyBuffs | count, type, modType, copyOrder, duration, srcTarget, id, cache | _types, _modType, _ids |
| Stagger | ub, kb, hr, hs, dt | _hitReaction, _knockback, _hitStun |
| SpeedCurve | curve | _curve |
| CreateArea | ro, o, p, t, r, ad, eot | _prefabName, _targetType, _targetId |
| area_ring_spawner · area_line_spawner | pos, rot, ok, t, id **[⚠ inherited]** | _id, _offsetKey, _targetId |
| OverrideAnimation | anim, slot | _overrideHash, _player |
| ClearOverrideAnimation | anim | _originalHash |
| PlayMiscAnimation | slot | _slot |

> [!NOTE]
> **Two caveats, stated rather than buried.** The spawner keys were read from the *abstract* `SpawnAreaGroup` base — attributing them to the concrete ring/line spawners assumes inheritance carries them, which is reasonable and unverified. And a few recovered strings are probably *values* rather than keys (`PRESTIGE` on RemoveBuffs, `HitReactionHeavyBack` on Stagger); they are omitted above but the omission is a judgement call.
>
> **The separator between multiple keys is unconfirmed.** `;` is the convention elsewhere in this payload, and every proven example so far has been a *single* key. **[⚠ inferred]**

[↑ Contents](#table-of-contents)

## The recipe — building an ability end to end

### Define the buff behaviour — build_buffs_set()
`buffs_set.globalBuffs["<id>"]`. Keys are **camelCase** here (unlike the statMods row):
`buffType`, `valueType`, `value`, `displayValue`, `hasDuration`, `time:{amount}`, `group`,
`scope`, `modeAvail`, `p`.

`p` is the per-type parameter object — e.g. `{"damage_type":"bleed"}` for damage,
`{"key":"_ftd","style":0}` for floating text.

### Define the modifier — build_stat_modifiers()
`statMods["<id>"]`. Keys are **short codes**, and several are **lists** — a wrong accessor
type yields a silent empty value and no error anywhere:

```
t    buff type   -> MUST equal the buffs_set globalBuffs id
tr   LIST        triggers, camelCase (onHit, onCrit, onSpecialActivate, onIntroStart, ...)
uit  LIST        UI triggers
a    LIST        appearance ids -> statModAppears
trr  text        trigger rate: none | update | once | repeat
c    decimal     chance, 1.0 = always
m    decimal     magnitude
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
   is why `ta` and `mt` are independent: `ta` is placement, `mt` is classification.

`Damage_BuffEffect` matches on the **`dmg_` prefix** of `t`, so damage buff types must be
named `dmg_*`.

### The asset channels — everything an ability can drive

The `statMods` row decides *what happens*. These decide *what the player perceives*. The
presentation record is `BCGStatModifierAppearance`; the rest are parameters on effect classes.

| channel | wire field / effect | consumed by | authorable from the server |
|---|---|---|---|
| ability name | `AbilityTitleID` | ability/info panels | yes |
| descriptions ×3 | `ShortStringID`, `LongStringID`, `SimpleStringID` | info panels | yes |
| **icon** | `IconTexture` | HUD + info panels | yes |
| **particle FX** | `FXProfile` | FX system | yes — **we currently serve `""`** |
| **callout text** | `CalloutStringID` | `HudScreen.PlayCallout` | yes |
| **callout colour ×3** | `CalloutTextColor`, `…GradientTop`, `…GradientBottom` | same | yes — **we serve `#FFFFFF` for all** |
| pause-screen text ×2 | `PauseShortStringID`, `PauseLongStringID` | pause UI | yes |
| floating number | `FloatingText_BuffEffect` | `HudScreen.PlayFloatingText` | yes |
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

### Define the appearance — build_stat_mod_appears()
`statModAppears["<id>"]`. **These wire keys were read off the client itself**, not
guessed:

```
t  -> IconTexture      (the effect glyph)
f  -> FXProfile
a  -> AbilityTitleID
s  -> ShortStringID
l  -> LongStringID
st -> CalloutStringID
tc -> CalloutTextColor        gt -> gradient top        gb -> gradient bottom
```

### Assignment — grant it to bots

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

### Regenerate, or nothing happens
```bash
export PATH="$PWD/../.cargo-home/bin:$PATH"
legible run Server/gamedata.lbl      # no args
```
`Server/responses/GET__bcg_getLoginData.json` is a **static file that overrides**
`build_login_data()`. Editing `gamedata.lbl` without regenerating changes nothing. This has
cost multiple wasted test cycles.

[↑ Contents](#table-of-contents)

## Worked examples

### Jamming Field

**[📄 binary only]** A heavy hit locks out their strongest special

```json
{
  "id": "kit_jam",
  "t":   "disable_sp3",       // WHAT  -> 2-arg ctor, tm is ignored
  "tm":  "",
  "tr":  ["onHeavyHit"],       // WHEN  -> list, always
  "uit": ["onHeavyHit"],
  "trr": "repeat",
  "c":   1.0,
  "trs": "",                  // IF    -> unconditional
  "ta":  "opponent",           // WHOM  -> name string, not a number
  "mt":  "debuff",
  "m":   1.0,
  "d":   8.0,
  "a":   ["kit_jam"]          // SEEN  -> list
}
```

**Predicts:** `KITGATE_ROLL id='kit_jam' pass=1` on every heavy, and their SP3 greys out for 8 seconds.
**Falsified if:** the gate passes and the special still fires — meaning slot numbering does not map the way the name implies.
*Why not graded higher:* `disable_sp3` has never been served or fired. Its ingredients are proven; this row is not.

### Killing Edge

**[⚙ RVA]** **[⚠ separator, units]** Crits build toward more crits

```json
{
  "id": "kit_edge",
  "t":   "mod_percent",
  "tm":  "type=CritChance;modType=buff",   // keys resolved; separator is NOT
  "tr":  ["onCrit"],
  "uit": ["onCrit"],
  "trr": "repeat",
  "c":   1.0,
  "ta":  "self",
  "mt":  "buff",
  "m":   15.0,                             // units UNKNOWN for modifiers
  "d":   6.0,
  "st":  3,
  "a":   ["kit_edge"]
}
```

**Predicts:** the row registers and crit rate climbs across a fight.
**Falsified if:** it registers but crit never moves — most likely `type` wants a fully-qualified name, not bare `CritChance`.
*Three unknowns stacked:* the `;` separator, what `m` means for a modifier, and whether `st` stacking applies. **This is the highest-value single test available** — it unlocks 44 abilities if it lands.

### The two open questions

**Can we cause an evade?** Probably not. `EvadeMelee` is a read-only bool on `PlayerAttributes` (`dump.cs:405709`), absent from the stat attributes list. The effect `override_anim` might let us play the dodge animation, but it would not confer invulnerability. **[📄 binary]**

**Can we cause a teleport?** A teleport decomposes into immunity (`resist_`), an animation (`override_anim`), and ordering (`sequence`), all of which look reachable; the repositioning step does not. **The honest version is a “phase”** — untouchable and visually warping in place. Most of the fantasy, minus the part with no mechanism. **[📄 binary]** **[⚠ composition]**

### How to ask for an ability

Answer these five and the tier, the class, and the likely failure mode can be named before a line is written. You do not need the field names.

1. **What does the player see happen?** Plain language. “Their armour cracks and they take more damage for a while.”
2. **What sets it off?** A hit, a block, a crit, a special, the fight starting. If it should be always-on, say so — that is the passive flag.
3. **Any conditions?** “Only against Tacticians,” “only in Arena.” HP thresholds are not expressible.
4. **Who does it affect?** The hero, the opponent, or both. The most common source of a wrong-feeling result.
5. **How strong, how long, how often?** Rough is fine — “a lot, for 5 seconds, maybe a third of the time.”

> [!NOTE]
> **A synergy is a different question.** Name the bots, name the theme, say what each member gains when they are together. That is a grant rule — no JSON, no conditions, no client involvement — and it is the part that already works.

[↑ Contents](#table-of-contents)

## Pitfalls — settled, do not relitigate

### Combinations that DO NOT work — and why

These are not style advice. Each one produces silence, and silence in this system looks
identical to "the feature is broken".

**1. A row no hero references is never deployed.**
The single most expensive mistake available. A `statMods` row is a *definition*; it does
nothing until some hero's `stat_mods` list names it. The client auto-applies exactly five ids
of its own accord — `gp_attack_chain`, `gp_hit_stun`, `gp_close_atk_window`, `gp_disable_run`,
`gp_sp3_minhp` — and **nothing else**. Everything else must be granted. A perfectly
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

**6. Conditions that do not exist.**
There are exactly **two** condition classes — `ActiveId_BuffCondition` and
`ContainsBuff_BuffCondition` — and both only ask *"is buff X active?"*. There is **no predicate
system**: "below 20% health", "opponent is class Y" and "after 3 hits" cannot be expressed as
conditions. Conditionality must come from **which trigger fires** plus the chance roll `c`. Do
not design a kit around a condition the engine cannot evaluate.

**7. Expecting a floating number for an effect that has no producer.**
`HudFloatingTextStyleFlags` offers ten styles including `Fury` and `Weakness`, but only two
cache keys exist in the entire binary — `_ftd` (`Damage_BuffEffect`) and `_fth`
(`Heal_BuffEffect`). Damage and heal numbers have plumbing behind them. **Strengthen and weaken
numbers have a paint style and nothing that fills it.**

### Other pitfalls

1. **No `@@` marker expander exists.** Zero `@@` literals in the client. An ASCII
   placeholder in an icon field renders as literal garbage. Use the real codepoint.
2. **A missing appearance record looks *better* than a broken one.** If the lookup misses,
   the client falls back to its own default icon and everything looks fine. Adding a record
   whose icon field is unrenderable replaces a working default with visible garbage. When an
   icon regresses, suspect the record you *added*, not one you're missing.
3. **Static responses override the builder.** Always regenerate.
4. **`tr`, `uit`, `a` are lists.** Wrong accessor type = silent empty value, no error.
5. **Client-side conditions are thin.** (See point 6 above).
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
11. **Verify any code sample you publish by compiling it.** The sample in this very
    document shipped once without its `intent:` line and would have failed for the first
    person who copied it. A replication guide that does not compile is worse than none.

### Corrections: what this page got wrong, and why

Kept deliberately, because the failure modes repeat and naming them is cheaper than rediscovering them.

- **“Evade is easy via `state_enable`.”** The type string was right; the mechanism was invented from the class *name*. The field is `_statModIds`. → *Never infer behaviour from a symbol name.*
- **“31 classes take no parameters.”** Measured `ParamsTable` construction, a proxy, instead of ctor arity, the actual constraint. → *Measure the thing, not something correlated with it.*
- **“The 60-value registry is complete.”** Our own payload serves `hit_stun`, which is in neither dispatcher. → *A negative from one instrument is not a fact.*
- **“Literals are unrecoverable from the binary.”** They are — through a double indirection. The first method failing was read as the thing being impossible. → *Calibrate a method on a known answer before trusting its negatives.*
- **`mt: 16` as a number.** Correct enum, wrong wire format; we serve name strings. Reading the binary without reading the payload. → *Two sources, always.*

[↑ Contents](#table-of-contents)

## What's proven, and how we know

### What is proven today — VERIFIED in a live fight

| capability | evidence |
|---|---|
| Authored abilities deal real damage in combat | `type='dmg_bleed' id='kit_bleed' origMod=0.4000 amount=0.4000 dur=6.00 tick=0.50` |
| Per-bot granting works | different `stat_mods` lists per bot are honoured |
| Effect icons render in-fight | three correct glyphs on the opponent's health bar |
| Floating damage numbers render **for normal attacks** | numerals observed over both combatants |

⚠️ **Do not read row 4 as "ability damage shows a number."** It does not, today. Normal
attacks have duration `0.0` and draw directly; a damage-over-time buff instead accumulates
into a per-player cache that only a deployed `FloatingText_BuffEffect` can flush to the
screen — and no hero currently references one. This row has been misread
that way before, including by the people who wrote it.

| capability | evidence |
|---|---|
| Indefinite buffs | `d = -1.0` → 3788 ticks in one fight (was dying after 4) |

**Icons require real Unicode.**

### Provenance legend

Confidence and evidence are tracked separately. A claim can only be graded on what is actually behind it, and the weakest mark wins.

| Mark | Meaning |
|---|---|
| **[👁 live]** | **Observed firing in a real fight** — appears in our combat instrumentation (`KITGATE_ROLL`, `KITREG`, or a measured buff value). The strongest mark. |
| **[📡 served]** | **Present in the payload we serve** and accepted by the client without error — but never observed doing anything. |
| **[📄 binary]** | **Read from `dump.cs` or resolved from disassembly**, with a line number or RVA. True about the client; says nothing about whether it works as authored. |
| **[⚠ inferred]** | **Reasoning only.** No citation exists. Treat as a hypothesis, never as a fact. |

**The two-source rule:** a claim needs evidence of two different kinds — binary plus payload, or binary plus a live observation — before it is treated as settled. Single-source claims stay provisional no matter how convincing they read.

### Planned — the verification campaign

Of ~46 catalogued effects, **3 are live-verified** (`dmg_bleed`, `dmg_shock`, `dmg_burn`).
The other ~43 are decompiled specs that have never been executed. Verifying them is the next
step, and the point of this section is that **it is not 43 fights.**

Fight cycles are the scarcest resource in this project — only a human can play one. Design
the campaign accordingly.

### Do not multiply the matrix: it is 43 + 11, not 43 × 11

There are two untested axes: ~43 **effects** and 11 untested **triggers** (`onCrit`,
`onBlocked`, `onSpecialActivate`, `onHpLost`, …). Testing the cross product is ~470
combinations and is pointless. Vary one axis at a time against a known-good control:

- **To test an effect** — pair it with the proven trigger: `tr:["onHit"]`, `c:1.0`.
- **To test a trigger** — pair it with a proven effect: `dmg_bleed` at a visible magnitude.
  Eleven variants of the same known-good effect, one fight, read which ones fired.

If a test changes two unknowns at once its result is uninterpretable. This is the single
most common way to waste a fight.

### Tier what "verified" means

Most of the value is in T1, and T1 is nearly free:

| tier | question | how observed | batchable |
|---|---|---|---|
| **T1 registers** | parsed, granted, registered in combat, does not crash | `KITREG2` / `KITREG4` hook lines | **yes, ~10 per fight** |
| **T2 fires** | the trigger actually delivers it | hook line at trigger time | partly — needs the trigger to occur |
| **T3 behaves** | produces the intended mechanical result | hook values, HP/power deltas, frames | usually one at a time |

**Getting all ~43 to T1 first is the highest-value move in the whole campaign.** It costs
about two fights and tells you which effects are real and which detonate — before anyone
designs a kit around them. Recall that `sig_lvl > 0` crashes the hero-detail path for every
bot; assume other landmines exist and find them cheaply.

### Natural batches

Group by how the effect is observed, not by what it means:

| batch | members | notes |
|---|---|---|
| **Variables** | `set_var` `add_var` `clear_var` `set_tel_var` `add_tel_var` `clear_tel_var` | Pure state. Verifiable from hooks with **zero gameplay observation** — plausibly all six in one fight. Cheapest batch; do it first. |
| **Numeric** | `heal` `power_gain` `power_sting` `protection` `resist_*` `armor_break` `attack_chain` `stagger` `speed_curve` `slowdown_curve` `<attr_name>` `<attr_name>_flat` | Change a stat/HP/power the hooks already log with amount and duration. Batch 6–8. |
| **Buff-graph ops** | `nullify` `purify` `remove` `refresh` `refresh_id` `purge` `copy_buffs` `sequence` | ⚠️ These operate **on other buffs** and cannot be tested standalone. Each needs a paired setup: apply a known buff, then the operator, observe removal/refresh. Naturally batched in pairs. |
| **Audiovisual** | `create_area` `area_ring_spawner` `area_line_spawner` `override_anim` `clear_override_anim` `play_misc_anim` `play_move` `announcer` | Needs frames. Burst-capture via `adb exec-out screencap` through the fight and judge from stills — real-time observation is unreliable, a fight is too busy to watch for a specific cue. |
| **Interfering — ISOLATE** | `disable_sp1` `disable_sp2` `disable_sp3` `disable_run` `state_disable` `swap_ai` `ai_rage_mod` | ⚠️ **Never put these in a mixed batch.** `disable_sp*` blocks the special attacks you need in order to trigger other effects, so it silently poisons every other result in the fight. |

Realistically **~10–12 fights**, not 43.

### Swapping a batch — the prerequisite is already done

Batch verification means assigning ~8 effects to one bot and swapping the whole set between
fights. **That is now a one-line edit to `bot_abilities(bid)`** — previously it meant
editing a hardcoded predicate in four builders per batch, a dozen times over, with
`quest_team` the easy one to forget, which would have turned every result in that fight
into a false negative.

Recommended shape: give one dedicated bot the batch under test and leave the rest of the
squad empty, so any effect you observe is unambiguously attributable.

### Record results where they will be believed

Update the proven-vs-untested matrix in `research/ability-catalogue.md` as each effect
promotes, and cite the evidence — the hook line or the frame — the way the existing three
entries do. An effect marked "proven" without a citation is worth nothing to the person who
has to trust it later, and the glossary reads from this distinction.

### What the glossary must add on top

1. **Plain-language effect description** a designer can read without the decompilation.
2. **`ta` / `mt` stated explicitly per ability**, not left to convention.
3. **Proven vs untested carried through prominently** — ~43 of ~46 effects have never been
   run. A glossary that presents all of them as equally available will send designers into
   untested engine paths.
4. **The icon** the player will actually see, tied to its codepoint.

**Single source of truth:** generate the glossary from the same table that drives the
grants, or it will drift from what the server actually serves. Do not hand-maintain a
parallel list.

[↑ Contents](#table-of-contents)

## Appendix A — the method that found this

A native hook (`tools/nativehook/hook.c`) logs **every JSON key the client reads and the
accessor type it used** to `/data/data/com.kabam.bigrobot/files/dotkeys.log`. Tags:
`hbL`=list, `fG`/`hbF`=float, `fS`=string, `fI`/`hbI`=int, `fB`=bool, `fO`=object,
`FDS2 name=<short> alt=<long>`=the short-code↔long-name pair.

`FDS2` lines are how the wire keys in this document were established as fact rather than inference.

**Audit all keys at once, never one per test cycle.** Extract key→type from the log, diff
against what you serve, fix every mismatch in one pass. A wrong accessor type produces a
silent empty value and **no error anywhere** — that is the trap, every time.

[↑ Contents](#table-of-contents)

## Appendix B — environment and toolchain

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
running it.

[↑ Contents](#table-of-contents)

## Appendix C — project history

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

### Material that already exists
- **`research/ability-catalogue.md`** — the backbone. ~46 `*_BuffEffect` classes with
  mechanical function, required fields, `p` params, stacking rules, and a **proven vs
  untested** matrix: 3 live-verified, ~43 decompiled specs.
- **`research/ability-grammar.md`** — trigger/condition vocabulary.
- **`research/glyph-map.md`** — codepoint → what the icon depicts, with confidence marked.
- **`research/VOCABULARY.md`** — wire-format terms.

[↑ Contents](#table-of-contents)

<!-- 2026-09-14 Claude Opus 5: written at the repo owner's request so downstream
     contributors and their agents can reproduce this pipeline and skip our dead ends.
     Every VERIFIED claim was observed in a running client. -->
