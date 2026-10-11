# Copyright and security notes for the content-reconstruction work

This file records how the backend-content reconstruction in this package was done so
that it stays on the right side of copyright and stays defensive/interoperability-only.
It covers the data added in `Server/gamedata.lbl` and the regenerated response files.

## 9.2.0 local recovery tooling

The optional 9.2.0 decompilation workflow includes a source patch for
[SamboyCoding/Cpp2IL](https://github.com/SamboyCoding/Cpp2IL), pinned to upstream commit
`b5ad444b82267cb1e4b88b8b373c008105bdea52`. The patch changes generic IL generation in
`IlGenerator.cs`; its upstream MIT license and copyright notice are preserved in
`tools/patches/CPP2IL-MIT-LICENSE`. The accompanying builder verifies the patch hash and
upstream revision before building. These files contain no APK bytes, game assets, recovered
game code, or game-authored content. Generated recovery files stay in ignored local build
directories and must not be committed or redistributed.

The Unity script-binding generator creates only thin class declarations and GUID metadata
from the operator's local AssetRipper export. The implementation remains in the locally
generated Cpp2IL assemblies; the repository contains neither those assemblies nor copied
game method bodies. A handful of unresolved debug-prefab script GUIDs receive empty local
placeholders and are explicitly reported in the generated manifest.

`EB.Collections.Pool<T>` is a special local import repair: REA inspection of the user's
confirmed 9.2 assembly showed generated placeholder logging in its recovered method bodies,
so the tool emits a conventional `Stack<T>` pool implementation from the exposed member
signatures. This is an approximation, not a claim of exact Kabam behavior. The repository
contains only newly authored repair logic; patched assemblies remain in ignored local staging.

The 9.2 request-signing repair is newly authored CIL guided by retained ARM64 observations of
`EB.Sparx.HttpEndPoint.Sign`, its initialization, and supporting HMAC/encoding calls. It uses
Unity's framework cryptography implementation and does not copy the native disassembly or a
recovered managed method body. The focused URI replacement uses `System.Uri` for absolute URL
components; only the tested HTTP host/path signing case is claimed here, not exact behavior for
every legacy URI form. Smoke harnesses and staged assemblies remain local.

The Unity import-repair script applies newly authored Mono.Cecil rewrites to diagnosed,
malformed methods in those local generated assemblies. It stores the identifiers needed to
locate affected types and fields, short tag labels and RVA-data field names needed to rebuild
standard constructor state, and newly authored AI-assisted repair logic; it does not embed APK data, assets,
or recovered method bodies. Repairs whose original mapping values are unrecoverable are
documented as empty-cache fallbacks, and the generated patched assemblies remain local.
For five ARM64-invalid methods, the repair workflow now starts from a short, human-readable
C#-equivalent reconstruction and verifies field types and values against retained constructor
IL and local 9.2 native trace evidence before emitting replacement IL. The
`EBRBSimulationChunk` constructor preserves its zero-initialized vector state and two `-1`
mapping sentinels; `TrailConfig` preserves the traced scalar defaults, two width-curve keyframes,
and white color; `MoveSequencer` restores the 64-entry event pool, three priorities, and both
capacity tables (`[32,16,8]` and `[8,2,2]`) from the native trace and preserved RVA data;
`DynamicScrollView` restores its item pool, enabled defaults, zero offsets, and two caches from
the native field offsets and constructor call trace; `HeroPortrait` restores its colors, overlay
map, border dimensions, and other traced constructor defaults. Earlier-version method behavior
is used only as a cross-check where identified in the tool; no earlier-version method body is
copied. These C#-equivalent reconstructions are versionable repair/tool source, not standalone
replacement game source or a complete project rebuild path. The C# Cecil repair implementation
is kept in `tools/RepairRecoveredConstructor.cs`; the Python entry point only prepares its input
plan, compiles it, and applies it to local assemblies. This authored repair source is directly
buildable as a tool, while the decompiler-generated C# and base assemblies remain local. Do not
check in initial Ghidra/Rosetta output; if local decompiler C# needs source-level edits, version
only narrow authored patch files against that external base.

AI-assisted source is labeled as such. Its presence does not by itself establish that every
portion is copyrightable or that the repository owner holds exclusive copyright in it. Human
review, selection, technical reconstruction, and edits should be preserved in the normal Git
history; do not describe AI-generated portions as wholly human-authored. The U.S. Copyright
Office treats copyrightability of AI-assisted work as dependent on human-authored expressive
contributions, such as sufficiently creative selection, arrangement, or modification; this
project note is not a legal opinion and does not resolve other jurisdictions. See the
[Copyright Office's Part 2 report](https://www.copyright.gov/ai/Copyright-and-Artificial-Intelligence-Part-2-Copyrightability-Report.pdf).

The checked-in tool contains only these newly authored semantic rewrites and field identifiers,
not the recovered method bodies or assets; repaired assemblies remain in ignored local staging.
It also restores missing P/Invoke linkage metadata for the APK-signature, bug-report, ENet,
Google Play Games, Firebase, and Krash wrappers. Module names are corroborated by a prior local
recovered import table, the native-library names in the operator-supplied 9.2 APK, and the
corresponding wrapper types. Entry points use the import method identifiers preserved in the
recovered assemblies. These repairs change declarations only; the APK, native libraries, and
method bodies are neither included nor regenerated by that repair.
For newer Unity core libraries, the repair also retargets references to the renamed
`System.String` length field while preserving the original field load/store instructions. The
field name is selected from the editor's local `mscorlib.dll`; no string data or recovered
method body is introduced. It applies the same metadata-only treatment to confirmed Unity 6
renames among private `Dictionary<TKey,TValue>`, `List<T>`, and `Hashtable` fields, plus matching
`System.Text.RegularExpressions.Capture`, `System.Net.IPEndPoint`, and
`UnhandledExceptionEventArgs` fields. It applies these aliases only when the selected editor's
local framework assemblies expose the matching replacement fields.
For the Unity 2020 Android rebuild, it also substitutes the editor's local `Mono.Security.dll`
for the recovered framework copy only when the full strong-name identity matches and the
replacement exposes `PKCS12.GetExistingParameters(Boolean&)`. This uses an installed engine
framework dependency; no Unity or recovered binary is included in source control.

The module descriptions in `Server/mods_catalog.json` are newly AI-authored generic
gameplay summaries. They were not transcribed or paraphrased from the game. Module names,
catalog identifiers, categories, rarities, and asset references are retained as the data
needed to identify existing entries in an operator-supplied client.

## What was added

`Server/gamedata.lbl` is a hand-authored source of the server-side content that Kabam
used to stream to the client and that shut down with their servers in early 2020. From
it, `legible run Server/gamedata.lbl` regenerates two response files:

- `Server/responses/GET__bcg_getLoginData.json` — the config blob (blueprints,
  characters, and the `attackValues` combat balance table).
- `Server/responses/GET__bcg_getUserData.json` — the owned roster.

`Server/fakeserver.lbl`'s dynamic `/bcg/getBaseHeroData` handler now computes hero stats
from the same authored curve, so on-screen numbers stay consistent with the roster.

The special-attack-meter work adds only newly authored original values, invented for this
revival and never transcribed from recovered Kabam data: `_MANA_GAIN_RATE = 1.0` for wire
`mana_gain`; `attackValues[*].m` values of 50, 75, 120, and 55 for Light, Medium, Heavy,
and Ranged; and the authored uniform per-hero `special_attacks` value of three. This changes
an original server-side scalar and references attacks already present in the operator-supplied
client; it adds no APK, asset, captured audiovisual content, credentials, or recovered server
dataset. The `bcg-combat` config also carries
`manaPerSpecial: 300.0`, deliberately mirroring the client's built-in default so the
absolute unit of `m` is self-documenting.

`bcg-combat`'s `maxQueuedActionTime = 0.2` is likewise an ORIGINAL authored value invented
for this revival, not transcribed from recovered Kabam data. Only its wire-key name comes
from the user's own client binary. It supplies the client's buffered-input window; when
unset that window is zero, so queued attacks never fire.

The hit-stun work likewise adds only newly authored original values, invented for this revival
and never transcribed from recovered Kabam data. Its `statMods["gp_hit_stun"]` row uses
`t="hit_stun"`, `ta="self"`, `mt="debuff"`, `c=1.0`, `m=1.0`, `d=0.5`, `pri=0`,
`trr="none"`, and `s="none"`. The 0.5-second `d` is an authored fallback floor; real per-hit
duration comes from `ApplyHitStun`. The remaining values in the 27-key row are empty strings,
empty lists, or zeros. The identifier `gp_hit_stun` is a constant already present in the user's
own client (`PlayerController.DefaultStatMods.kHitStun`), not recovered Kabam server data; the
27 wire key names were observed from that client's parser at runtime as an interoperability
schema observation, not copied content.

The `_ART_BASE` portrait mapping adds no new creative content. Every right-hand value is the
file name of an asset that already ships inside the user's own copy of the app; the change only
corrects which existing on-device asset the offline server points the client at. No artwork, no
recovered server data, and no third-party text is introduced.

The selected-squad persistence work authors no new game values: no numbers, names, balance data,
or strings of game content. It is pure plumbing: the squad the user's own client sends in
`POST /bcg/setSavedTeam` and `POST /quests/quest-begin/<qid>` is now echoed through the
quest-progression, active-quest, movedir, and user-data responses instead of those responses
hardcoding a default squad. The only identifiers involved — `setSavedTeam`, `activeTeams`,
`savedTeams`, the `aid` key of the form `"<qid>-<teamID>"`, and the blueprint ids the client
itself sends — are wire-key and asset-id names that already exist inside the user's own copy
of the app. Observing which key the client sends and answering in the same shape is an
interoperability schema observation, the same established precedent recorded above for the
hit-stun wire keys. The in-APK C server change introduces no new capability: it makes the
already-shipped local loopback server compute the same response bodies the Python server already
produced, so the bundled build behaves like the host-server build. It adds no network
interception, no new binary patch, and no new hook.

The five-slot change exposes two existing pre-mission client slots and echoes only locally
selected roster identifiers. It adds no game assets, interception, certificate bypass, or
client-binary patch.

The BOTS-roster navigation fix likewise introduces no authored game values: `Server/gamedata.lbl`
is unchanged, so it adds no invented numbers, names, or content. Its arm64 `RSENTER`, `RSEXIT`,
and `RSHOME` hooks only call `GameObject.SetActive` on base-builder objects that the client's own base-board
code already activated and then failed to deactivate when roster navigation bypassed
`BaseBoard.LeaveBoard`. `RSENTER` hides the recorded set, `RSEXIT` retains it through the detail
overlay, and `RSHOME` restores it on return to the home screen. They add no network interception,
no binary patch of shipped content, and no new capability. Temporary read-only diagnostic hooks
used to establish the affected navigation path were removed before shipping.

The earlier level-3 cinematic-special negative verdict is historical and superseded by the later
measured arm64 implementation. The shipped hook now resolves the missing usable cinematic move,
mirrors ineffective renderer visibility onto renderer GameObjects, constrains body forcing to the
existing `character_model` and `transformed` props, directly starts the alternate prop's existing
`SpecialAttack03` animation, measures the currently playing fighter's own existing
`SpecialAttack03` Animator state length and uses it when it resolves while retaining the original
1000–2500 ms alternate-form block as a fallback, before restoring robot form on cinematic exit.
These hooks introduce no game asset, recovered
server value, external audiovisual material, network interception, binary patch of shipped
content, or new capability. `Server/gamedata.lbl` remains unchanged. The regression tests merely
lock already-documented interoperability addresses and the existing authored timings.

## Expanded STORY board and authored dialogue

The expanded `1.1.1` STORY board grows from a 3x3 map to an 11x11 single-column path, and
from two fights to ten. Every tile label and every assignment of an enemy to a tile is an
original authoring choice for this revival, not recovered Kabam mission data. The choices form
nine escalating encounters followed by the final boss.

The identifiers `sharkticon_gs_scout`, `sharkticon_gs_tech`,
`sharkticon_gs_demolition`, `sharkticon_gs_tactician`, `sharkticon_gs_kabam`,
`kickback_gs_kabam`, `waspinator_gs_deluxe`, `soundwave_gs`, and
`sharkticon_gs_brawler` already exist in the shipped client roster. Referencing an existing
identifier follows the same precedent as the other authored content in this repository. What
is new is the original choice of which existing enemy occupies each authored tile and the
original tile labels; `sharkticon_gs_warrior` remains the first Patrol's existing roster id.

The four dialogue sets contain ten lines of 100% original writing. They were never transcribed
or paraphrased from the Transformers: Forged to Fight script, its cutscenes, or any other
copyrighted Transformers media. For example: “Sensors read a cold beachhead ahead. Whatever
landed here is already moving.” and “Then we walk it to the end. Steady spark, higher guard.”
The character identifiers in those entries are existing client roster ids, not newly supplied
assets or recovered text.

Nothing under `media/`, no APK, no game asset, and no recovered Kabam server data was added.

## Why this is copyright-compliant

- **Everything authored here is original.** Kabam's real balance data (the roster
  numbers, the class/star assignments, the `attackValues` table, the ability
  definitions) was server-side and is gone. It was not recovered, copied, or
  reproduced. Every number, class, faction, and rarity in `gamedata.lbl` was invented
  for this offline revival. The point of the file's header comment is to make that
  explicit and auditable.

- **No game assets or binaries are included or distributed.** No APK, Kabam binary, or
  recovered Kabam server dataset is in this package. The character
  **ids** used (e.g. `bumblebee_gs_kabam`) are asset-bundle names that already ship
  inside the user's own copy of the app (see `re_notes/ASSET_INVENTORY.txt`); the data
  here only points fresh numbers at art the user already legally possesses.

- **The service is dead.** The official servers were permanently shut down in 2020.
  This is preservation / interoperability with software the user owns, not
  circumvention of a live commercial service and not competition with an operating
  product.

- **Trademarks.** Character and franchise names are trademarks of their owners. This is
  a private, non-commercial preservation server. Nothing here is offered for sale,
  presented as an official product, or used to pass work off as the rights holder's.

## Why this is security-compliant

- **No new offensive capability was added.** The reconstruction work is pure data
  authoring plus a stat-calculation helper. No new binary patches, no new certificate
  bypass, no new hooks, and no new network interception were introduced beyond what
  the package already contained for its own local loopback. Temporary read-only diagnostic
  hooks were used during investigation and removed before shipping, so this describes the
  shipped state deliberately rather than by omission.

- **Local, self-directed, and owner-operated.** The fake server answers only the user's
  own emulator over their own LAN/loopback. There is no targeting of third parties, no
  credential handling, and no exfiltration.

- **Scope is interoperability.** The existing patches/hook exist to let software the
  user owns run against a stand-in for a service that no longer exists. This change does
  not extend that scope.

## If you extend this

Keep the same discipline: author original numbers and text, do not import or transcribe
any recovered Kabam data if some ever surfaces, and never add game assets or binaries to
this repository. Follow the loop the README describes — author in the shapes
`re_notes/dump.cs` proves the client parses, verify against your own client, repeat.

## Roster-scroll crash guard

The roster-scroll crash guard authors no new game values whatsoever: no numbers, names,
balance data, stats, or strings of game content. Nothing was transcribed from recovered Kabam
server data. It is pure client-side crash-guard plumbing. `Server/gamedata.lbl` is unchanged;
the change is confined to this revival's own native hook shim, which is original code written
for the project.

The only externally-derived facts used are observations of the user's own installed client
binary: the three IL2CPP function addresses `0xB210CC`, `0x1E5F444`, and `0x1404E6C`, plus the
runtime type name `HeroPortrait` used as a discriminant. Like the wire-key names and addresses
already documented above, these are interoperability and diagnostic observations of the user's
own binary, not redistributed game content.

The guard adds no capability. It adds guarded early returns to three client functions to prevent
a segfault; it adds no network interception, no certificate bypass, and no patching of shipped
content bundles.

## Alternate-form rendering on level-3 special attacks

The alternate-form rendering change authors no new game values whatsoever: no numbers, names,
balance data, stats, or strings of game content. Nothing was transcribed from recovered Kabam
server data. It is confined to this revival's own native hook shim, which is original code written
for the project. `Server/gamedata.lbl` is unchanged.

No game asset is added, copied, or transcribed. The alternate-form model is the client's own
already-shipped `transformed` prop; the change only makes the user's own client display a model it
already contains and already instantiates. No content bundle is patched or repacked. The only
externally-derived facts used are observations of the user's own installed client binary: the six
IL2CPP function addresses `0xEA023C`, `0x100A76C`, `0xDE7CF4`, `0x117A67C`, `0x1174038`, and
`0x1174484`, plus the prop-name discriminants `transformed` and `character_model`. Like the
wire-key names and addresses already documented above, these are interoperability and diagnostic
observations of the user's own binary, not redistributed game content.

The change adds no capability. It adds no network interception, no certificate bypass, and no
patching of shipped content bundles. No temporary diagnostic hooks remain in the shipped build,
so the earlier statement about their removal remains accurate.

## Alternate-form animation on level-3 special attacks

The alternate-form animation change authors no new game values whatsoever: no numbers, names,
balance data, stats, or strings of game content. Nothing was transcribed from recovered Kabam
server data. It is confined to this revival's own native hook shim, which is original code written
for the project. `Server/gamedata.lbl` is unchanged.

No game asset is added, copied, or transcribed. The animation played is the client's own
already-shipped clip on its own already-shipped alternate-form prop; the change only makes the
user's own client play a clip it already contains. No content bundle is patched or repacked. The
only externally-derived facts used are observations of the user's own installed client binary: the
IL2CPP function addresses `0xEA023C` (`PropData.SetActiveInternal`) and `0xEA05B4`
(`PropData.PlayAnimatorState`), the prop-name discriminant `transformed`, and the animator state
name `SpecialAttack03`. Like the wire-key names and addresses already documented above, these are
interoperability and diagnostic observations of the user's own binary, not redistributed game
content.

The change adds no capability. It adds no network interception, no certificate bypass, and no
patching of shipped content bundles. No temporary diagnostic hooks remain in the shipped build,
so the earlier statement about their removal remains accurate.

## Player-feedback defect triage

This change set triages six player-reported defects on this build. Two were reproduced and fixed:
the mission presentation now retains all three squad members the player selected, and the bot
detail view no longer has the base geometry restored beneath it. The other four reports were
measured as not reproducible on this build and were deliberately left unchanged rather than given
speculative fixes.

The squad change authors no game values of any kind. It removes response and rendering truncations
that discarded squad members already selected from the already-authored roster; it does not add
numbers, names, balance data, or strings of game content. Nothing in this work was transcribed
from recovered Kabam server data.

The detail-view change is confined to this revival's original native hook shim. It contributes no
game values, assets, or strings of game content, and it does not patch a shipped content bundle.
It only prevents the client from re-showing its own cosmetic base-building objects while its own
bot-detail camera is drawing. The one newly used externally-derived fact is the IL2CPP address
`0xE746B8` for `TransformersHomeScreen.WindowEnter`, observed in the user's own installed client
binary. Like the wire-key names and addresses recorded above, that is an interoperability and
diagnostic observation of the user's own binary, not redistributed game content.

The change adds no capability: it introduces no network interception, no certificate bypass, and
no patching of shipped content bundles. Temporary diagnostic hooks have been removed from the
shipped build, so the earlier statement about their removal remains accurate.

## Alternate-form rendering during level-3 special attacks

During a level-3, three-energy-bar special attack, the fighter previously remained in its
alternate vehicle form throughout the cinematic. It now alternates between the alternate form
and robot form across the cinematic, so vehicle beats render as the vehicle and intervening beats
render as the robot.

This change authors no game values, names, balance data, or strings of game content. It contains
no numbers, names, or text of game content. Its alternation period is not an invented hardcoded
timing: it is read at runtime from the character's own already-shipped move data, using the
duration of the client's own transform-move event. Anything this revival has ever authored
elsewhere is original invention for this revival and was never transcribed from recovered Kabam
server data.

The identifiers used are wire-key, asset-id, and symbol names already present in the user's own
installed client, observed for interoperability and diagnostic purposes, and are not
redistributed game content. The newly used externally-derived fact is the IL2CPP address
`0xDE8750` for `Simulation.FixedUpdate`, observed in the user's own installed client binary;
the addresses for `PropData.SetActiveInternal`, `MoveSet.GetMove`,
`PlayerCinematicSpecialAttackState.OnEnter`, and `PlayerCinematicSpecialAttackState.OnExit` were
already recorded above.

No game assets or binaries were added to the repository. Both the vehicle body and robot body
are assets that already ship inside the user's own client; nothing was extracted, added, or
redistributed. Screen recordings made as evidence are local-only captures and are not committed;
the repository ignores `media/`. The change adds no capability: it introduces no network
interception, no certificate bypass, and no patching of shipped content bundles. Temporary
read-only diagnostic hooks added during investigation were removed before shipping, so the
earlier statements about their removal remain accurate.

## Level-3 special-attack transform timing

During a level-3, three-energy-bar special attack the fighter previously switched repeatedly
between its alternate form and its robot form across the cinematic, alternating on a beat read
from shipped move data, which produced roughly seven form changes in one sequence. It now shows
a single alternate-form block: the fighter holds its robot body for a short wind-up, changes once
into the alternate form, holds that form, changes once back to the robot, and finishes the
cinematic as a robot. For the player this means one clean transformation out and one clean
transformation back where there used to be a rapid flutter.

The window length is read at runtime, per cinematic, from whichever fighter's own already-shipped
alternate-body `SpecialAttack03` animation state is running. No fixed long window is applied to
any fighter: a rig whose state does not resolve retains the original authored 1.0–2.5 second
fallback. The measured window is capped below the existing 12-second safety bound. Optimus
Primal's approximately 8.833-second state is one observed example of this per-rig measurement.

Two publicly posted recordings of the original game were consulted as an observational timing
reference only. They were downloaded to a local, version-ignored working directory, decomposed to
frames locally, and measured. Nothing derived from them is redistributed with this package: no
video, no frames, no contact sheets, no audio, no on-screen text, and no artwork was copied into
this repository or into any build output. The only thing carried forward is the reviving author's
own measurements of elapsed durations in seconds and the author's own behavioural description of
what the footage shows. Elapsed-time measurements are facts about observed timing, not expression.
The recordings themselves are not committed, not redistributed, and not required to build or run
anything here.

This change authors no game values, names, balance data, or strings of game content, adds no game
assets or binaries to the repository, and patches no shipped content bundle, matching the
assurances the existing sections make. Both the alternate-form body and the robot body are assets
that already ship inside the user's own installed client; nothing was extracted, added, or
redistributed. Screen recordings made as evidence are local-only captures and are not committed;
the repository ignores `media/`. The change adds no capability: it introduces no network
interception, no certificate bypass, and no patching of shipped content bundles. New
interoperability observations are limited to the user's installed client: `PropsController.GetProp`
at `0xEA16C0`, `Animator.StringToHash` at `0x219B864`, and
`Animator.GetCurrentAnimatorStateInfo` at `0x219B470`. The existing auxiliary-prop exception
remains limited to the already documented Primal/`shoulderguns` identifiers.

That limit is deliberate and was confirmed against the user's own installed client rather than
assumed. Two fighters' level-3 blocks were observed live: one whose alternate-body block leaves
an auxiliary prop still requested and visibly detached, and one whose block leaves none. Only the
first needs the exception; for the second the ownership check finds no matching prop and the path
stays inert. Widening the exception to every prop a fighter owns would suppress the swords, guns
and effects the client legitimately requests during the block, so it is not done. This observation
authors no game values, names, balance data or strings of game content, adds no assets or binaries
to the repository, and introduces no new capability; the recordings it rests on are local-only
captures under the ignored `media/` path and are not committed.

Additional local capture records `aux_rig=0` for Sharkticon, Kickback, Waspinator, and Grimlock;
the retained-weapon observations (`Sword` on Sharkticon and `sword` on Grimlock) are a
visual-verification follow-up, not a broadened suppression rule.

## Special-attack prop visibility

During special attacks, the fighter's energy swords and other props its moves activate previously
did not appear even when the client requested them. They now appear when requested, except that
Primal's player-owned `shoulderguns` is temporarily suppressed during its beast-form level-3 block
and restored to the latest authored requested state afterward.

This change authors no game values, names, balance data, or strings of game content. It contains
no numbers or text of game content.

The prop names `LeftToe_ebrb`, `Neck_ebrb`, `RightToe_ebrb`, `Sword`, `character_model`,
`doublesword`, `gun`, `leftSword`, `leftsword`, `rightSword`, `rightsword`, `shoulderguns`, and
`transformed` are the client's own object names, observed in the user's own installed client for
interoperability and diagnostic purposes, and are not redistributed game content. The ownership
check additionally uses the installed client's `PropsController.GetProp` entry point at `0xEA16C0`.

No game assets or binaries were added to the repository, and no shipped content bundle was
patched. The props are assets that already ship inside the user's own installed client; nothing
was extracted, added, or redistributed. The change adds no capability: it introduces no network
interception or certificate bypass. Evidence recordings are local-only captures and are not
committed; the repository ignores `media/`. Broad temporary diagnostic probes added during the
investigation were removed before shipping; only bounded production lifecycle diagnostics remain,
so the earlier statements about their removal remain
accurate.

## Distant opponent ranged-attack nudge

The distant-opponent ranged-attack change is original local hook-shim code. It asks the
owner-operated client's existing controller to select its existing basic `Attack` behavior when
the client's own ranged gate permits it. It adds no assets, binaries, recovered server data,
network interception, credential access, or third-party targeting.

## STORY enemy variety, the Nemesis Prime boss, and dialogue delivery

The revised encounter order for the `1.1.1` STORY path is an original authoring choice for
this revival, not recovered Kabam mission data. Seven of the nine non-boss tiles were
reassigned so that Sharkticons are no longer the majority, and the new tile labels
"Insecticon Scouts", "Buzzsaw Swarm", "Interceptor", "Sweep Patrol", "Highway Blockade",
"Blade Duel", and "Siege Breaker" are original writing.

The blueprint identifiers used for those tiles — `kickback_gs_kabam`,
`waspinator_gs_deluxe`, `sharkticon_gs_demolition`, `soundwave_gs`, `cyclonus_gs_uw06`,
`motormaster_gs_voyager2015`, `bludgeon_gs_rd20`, `necrotronus_gs_kabam`, and
`nemesisprime_gs_voyager2015` — already exist in the owner-operated client's shipped
roster, with art already resolved by `art_overrides()`. Referencing an existing identifier
follows the same precedent as the rest of the authored content here. No asset, portrait,
model, or recovered server record was added.

The final boss's statline, 20000 max HP and 800 attack for a displayed power of 20800, is
an original balance choice invented for this revival to satisfy a request for a roughly
20800-power final boss. It is not a recovered Kabam value.

One dialogue line changed. The `final_stand` set's antagonist speaker moved from the
retired Sharkticon Brawler to Nemesis Prime, and its line is new, 100% original writing for
this repository: "You climbed all this way just to meet the shape of your own ending." It
was not transcribed or paraphrased from Transformers: Forged to Fight or any other
copyrighted Transformers media. Optimus Prime's existing reply, "We climbed a long way to
end this. Stand down or fall down.", is unchanged and equally original. No other authored
line was edited.

The dialogue-delivery work is a JSON shape and key-name correction on this project's own
server plus a read-only diagnostic hook slot (`DIALOGDIAG`) that logs the client's own
dialogue state before calling the original method. It adds no assets, no binaries, no
recovered server data, no network interception, and no credential access. Nothing under
`media/`, no APK, and no game asset was added to the repository.

## Custom story 2.1.1 final encounter — Ironhide boss swap

The 2.1.1 custom story's final boss encounter was changed from the shipped Starscream
blueprint (`fte_stars_gs_t3`) to the already-shipped roster id `ironhide_cin_rotf`
(demolition, star 3, autobot). The `ironhide_cin_rotf` art is already resolved by the
existing `art_overrides()` entry (`ironh_c_rotf`); no new art, asset, or roster entry was
added.

The two dialogue sets that reference the final boss — the pre-battle ambush set (renamed
`custom_ironhide_ambush`) and the post-battle defeated set (`custom_ironhide_defeated`) —
were re-voiced with new 100%-original lines written for this repository. None of these
lines transcribes or paraphrases any Transformers media, game dialogue, or copyrighted
source. Optimus Prime's lines remain original writing created for this project. The
`custom_opening_intro` (5 entries) and `custom_bludgeon_defeated` (2 entries) sets are
unchanged.

No asset, binary, APK, recovered Kabam server data, or network interception was added.
Nothing under `media/` was touched.

## Guard `read_file` behind nested `if` — Legible's `and` does not short-circuit

Contributed by **@galvatron** (Discord).

Four call sites were changed so that a `read_file` is only reached after its
`file_exists` guard has actually passed. `Server/fakeserver.lbl` (`tutorial_login_seen`)
and the former desktop GUI runner (`append_worker_start_log` ×2,
`worker_logged_success`) each expressed the guard as
`file_exists(path) and <something that reads path>`. Because `and` evaluates both
operands, the read ran even when the file was absent and aborted the process. Each site
now places the read inside a nested `if`, which is the existing idiom elsewhere in these
same files.

This is a control-flow correction to this repository's own Legible source. It is
100% original work written for this repository. No logic, string, constant, or value was
transcribed or paraphrased from Transformers: Forged to Fight, from any decompiled or
disassembled game code, or from any other copyrighted source. The behaviour of each
function is unchanged when the file exists; only the absent-file path differs, and it
now returns the same result the guard already intended rather than terminating.

Nothing was transcribed from recovered Kabam server data. No asset, binary, APK, game
data, network capture, or credential was added. Nothing under `media/` was touched. No
new dependency was introduced.

## Unicode round-tripping in `Server/jsonout.lbl`

`Server/jsonout.lbl` previously replaced every non-ASCII scalar with an ASCII `?` on the
decode side and aborted the process on the encode side, so any JSON string containing a
non-ASCII character was silently corrupted on a parse/encode round trip. The decoder now
turns a `\uXXXX` escape (including a UTF-16 surrogate pair, for codepoints above U+FFFF)
into the corresponding UTF-8 bytes, and maps a lone or unpaired surrogate to U+FFFD. The
encoder walks UTF-8 sequences back to codepoints and re-emits them as `\uXXXX`, using a
surrogate pair above U+FFFF, so output stays pure ASCII exactly as before. Malformed,
overlong, out-of-range and surrogate-encoded UTF-8 are rejected loudly rather than
substituted. Output was verified byte-identical to Python `json.dumps(ensure_ascii=True)`.

This is a correctness fix to this repository's own Legible source. **It adds no game
content of any kind** — no authored values, no identifiers, no wire keys. Nothing was
transcribed from recovered Kabam server data. No asset, binary, APK, game data, network
capture, or credential was added. Nothing under `media/` was touched. No new dependency was
introduced.

## Effect icon codepoints and the authoring guide's enum tables

Two corrections to material already in this repository.

`Server/gamedata.lbl` served `U+E402` as the bleed effect icon and `U+E412` as the shock
effect icon. Both are wrong on inspection — `U+E412` is a bare fist with no electrical
motif. They now serve `U+E414` and `U+E914`. All four codepoints are glyphs in
`Tecnica_Bold_116`, a font **already present inside the operator's own client**; this change
alters which existing glyph is referenced by an appearance record and **adds no font, asset,
or artwork of any kind**. Neither replacement is among the 72 private-use codepoints the
client references in its own string table, so no symbol the client already draws for its own
UI has been repurposed.

`ABILITY_AUTHORING.md` documented three wire fields with incomplete value lists. The
corrected tables (`BuffTriggerRate`, `BuffTargetTypes`, `BuffModTypes`) are **enum
definitions read out of the operator-supplied client binary**, recorded as an
interoperability schema observation — the same established precedent as the hit-stun wire
keys recorded above. The added §3.2.1, §4.1 and §8 sections are our own prose describing
this repository's own data format, plus a mapping of ability names to font codepoints that
is **original authored judgement**, not transcribed from any recovered source.

Nothing was transcribed from recovered Kabam server data. No asset, binary, APK, game data,
network capture, or credential was added. Nothing under `media/` was touched. No new
dependency was introduced.

## Revert of the effect icon codepoint swap, and of the swipe special-attack gesture

This contribution **removes** previously contributed material. It adds nothing.

The icon codepoint swap recorded in the section above (`U+E402` → `U+E414` for bleed,
`U+E412` → `U+E914` for shock) is **reverted**: `Server/gamedata.lbl` and the regenerated
`Server/responses/GET__bcg_getLoginData.json` once again emit the original `U+E402` /
`U+E412` / `U+E41D` set. The swap was committed as "not verified in-game yet"; no glyph it
changed was ever observed rendering in a running client. Rolling it back removes the only
unverified icon claims that reached the shipped payload. The paragraph above is retained,
corrected by this entry rather than deleted, so the reasoning that produced `U+E414` /
`U+E914` stays available for a future change that is verified first. The compliance posture
is unchanged either way: all four codepoints are glyphs in `Tecnica_Bold_116`, a font
**already present inside the operator's own client**, and no font, asset, or artwork was
added by the swap or by this revert.

The swipe special-attack gesture selection in `tools/nativehook/hook.c` (slots 164/165,
`PlayerController.GetAvailableSpecialTier` and `HudSpecialMeter.OnSpecialButtonPressed`)
is also **reverted**, restoring the payout hooks as the final slots and the stock
special-attack dispatch path. This removes interception of touch input inside the
operator's own client; nothing is added in its place. The gesture-specific checks in
`Server/test_nativehook_slots.lbl` were removed with the code they asserted. The
`hermesVersionCode` / `hermesVersionName` build properties, which landed in the same commit
but are unrelated to the gesture, are kept.

Nothing was transcribed from recovered Kabam server data. No asset, binary, APK, game data,
network capture, or credential was added. Nothing under `media/` was touched. No new
dependency was introduced.

## Karma Six activeTeams gap, native quest-reentry crash fix, and getBaseHeroData hardening

This contribution investigates and closes gaps a developer's separate personal fork
(`kmcbest/Transformers-Forged-To-Fight-Offline-Version`, branches `redeco`/`ability`/
`custom-special`) had already found and fixed for its own divergent Python-based server
rewrite of `Server/`. Nothing was ported from that fork's Python source or its
`assets_redeco/` tree (which carries ~119 MB of AssetBundles derived from the operator's
APK and is out of scope for this repository's data-only posture); each fix below was
independently re-derived by reading this repository's own `.lbl` code and, in one case,
the same developer's proper PR (#14) against this repository's Legible source.

`Server/gamedata.lbl`'s `build_user_data` and `Server/fakeserver.lbl`'s
`saved_team_envelope` were both missing an `activeTeams` entry for `challenge_qid()`
(the Karma Six Special Mission, `1.1.2`). `QuestFlow` checks `BCG.GetActiveTeam` before
loading a quest map; without this entry, entering Karma Six looped `quest-begin`
indefinitely instead of loading the map. This is the same root cause as this repository's
own upstream PR #14 (`358d5eb`), reconciled here alongside an independent, uncommitted
Act 3 custom-story addition that also needed its own new `activeTeams` entry
(`custom_story_act3_qid()`). No game content is authored by this fix: it is pure
plumbing that echoes an existing quest id back through an existing wire shape, the same
class of change the `activeTeams` paragraph earlier in this file already covers.

`tools/nativehook/hook.c` gained four `poke32` patches (installer, near the existing
60 fps/vSync patches) that redirect three functions in the operator's own
`libil2cpp.so` — `Legacy.QuestSet`, a badge-counter aggregator, and
`QuestDB.AddExpiredQuest` — from throwing `NullReferenceException` /
`IndexOutOfRangeException` on re-entering a quest after a battle or quit, to their
existing safe-exit paths in the same functions. This is a **binary patch of the
operator's own client**, the same category as every other `poke32` hook already recorded
throughout this file (e.g. the 60 fps and vSync patches, or `FIXWRAPMI`/`FIXSYN`): it
redirects an existing branch to another address already inside the same function; it
injects no new code, asset, or capability. This fix has not yet been re-verified live in
this repository's own build (device verification is the next step); the developer's own
project verified the equivalent patch on their fork.

`tools/nativehook/inapk_server.c`'s `getBaseHeroData` handler was hardened in two ways,
both defensive and neither adding game content: it now falls back through `bid` →
`character` → `id` when extracting a hero identifier from the request body (the client
is observed to vary which key it sends), and it clamps a requested `level` above 30 down
to 30 before building the `@hero:<bid>:<rank>:<level>` cache key, since this repository's
own export already authors a dense rank 1-5 × level 1-30 grid for every owned hero
(`Server/export_payload.lbl`'s `add_heroes`) and previously had no fallback for a level
outside that range. A `logmsg` diagnostic line was added for the same handler, matching
the existing diagnostic logging pattern already used elsewhere in this file.

Nothing was transcribed from recovered Kabam server data or from the fork's own
authored content. No asset, binary APK, captured audiovisual content, credential, or
recovered server dataset was added. Nothing under `media/` was touched. No new
dependency was introduced.

## 2026-09-18 — ABILITY_AUTHORING.md corrections (conditions, magnitude)

Documentation-only. Three corrections to `ABILITY_AUTHORING.md`, all of them to text I
authored in PR #10 that later testing showed to be wrong or incomplete.

1. The guide stated in two places that there is **no generic predicate system** and that
   *"opponent is class Y"* cannot be expressed as a condition. That is false. Conditions
   are authored in the `trs` field as `<target>:<key><op><value>`, parsed by
   `BuffTriggerFactory.ParseConditions` with the regex `([\w\.]+)(=|<=|>=|!=|>|<)(.+)`,
   six operators, and twelve readable keys. Demonstrated in a live fight with a matched
   pass/fail pair differing only by operator, plus an unconditioned control row.
2. The `m` field was documented only as "magnitude". It is an absolute total spread across
   the duration (`per tick = m / d / 2`), not a fraction of Attack, and because the HUD
   renderer takes an `int` a fractional value truncates to zero with no error. Added as a
   numbered pitfall with the measured values that established it.
3. The condition-class inventory was expanded to name the comparison family and
   `BuffConditionOp`, and to state the real remaining limit (no health key).

All findings are original: derived from disassembly of the client binary this repository
already targets, and from runtime logging via this repository's own `tools/nativehook`
harness during local play. Nothing was transcribed from recovered Kabam server data or from
any other fork's authored content. No asset, binary APK, captured audiovisual content,
credential, or recovered server dataset was added. Nothing under `media/` was touched. No
new dependency was introduced. No generated payload changed, so no regeneration was
required.

## 2026-09-18 — nativehook: two diagnostic hooks (magnitude + condition gate)

Adds two read-only logging hooks to `tools/nativehook/hook.c`, the instrumentation that
produced the measurements cited in the `ABILITY_AUTHORING.md` corrections in this PR.

1. `hooked_FloatingText_OnTick` now also calls `PlayerController.GetCachedValue`
   (`g_base + 0x117A1C0`) — the same function the effect itself branches on — and logs the
   returned float as `cached=`. Logging the cache key alone showed the effect was ticking
   but not why it stayed silent; logging the value is what established that `m` is an
   absolute total and that a fractional magnitude truncates to zero at the `int`-typed HUD
   renderer.
2. `hooked_TestForConditionsAndRoll` wraps `StatModifierController.TestForConditionsAndRoll`
   (`g_base + 0xCCF35C`) and logs the stat-mod id, the pass/fail result, the roll and the
   chance. This is how the `trs` condition format was verified.

Both hooks call the original and return its result unchanged; neither alters game state or
payload. A `statmod_id` helper reads `StatModifier._statModifier` (`+0x18`) then
`BCGStatModifier.ID` (`+0x10`) so the lines name an ability rather than a bare pointer.

`test_nativehook_slots` passes 14/0, confirming `H[]` and `handlers[]` remain contiguous and
that the restored gesture-hook slots from `d5038b6` are untouched. Compiles clean under
`aarch64-linux-android28-clang -fsyntax-only`.

All offsets were derived from disassembly of the client binary this repository already
targets. Nothing was transcribed from recovered Kabam server data or from any other fork's
authored content. No asset, binary APK, captured audiovisual content, credential, or
recovered server dataset was added. Nothing under `media/` was touched. No new dependency
was introduced. No generated payload changed.

## 2026-09-19 — ABILITY_AUTHORING.md restructure + merge of the ability reference

Documentation-only. Restructures the authoring guide and merges in an ability-grammar
reference built during local testing. No payload, asset or code change; nothing regenerates.

**Restructure.** Sections are reorganised by what a reader came to do rather than by the order
we investigated things, hard-coded section numbers are dropped in favour of descriptive
headings, project history and method move to appendices, and status claims are consolidated in
one section so they rot in a single place. A table of contents with anchor links is added, and
heading levels are strictly nested so the document converts cleanly to HTML/Word/PDF via
pandoc — stated near the top so nobody forks a second copy in another format.

**New reference content**, all of it original and derived from disassembly of the client binary
this repository already targets, plus runtime logging via this repository's own
`tools/nativehook` harness during local play:

- the complete `t` type-string registry recovered from both effect factories
  (`TFormBuffEffectFactory` `0xB07138`, `BuffEffectFactory` `0xE5DCC0`)
- the `tm` parameter keys for the effect classes that parse them, each corroborated against
  the class's own private field names
- the constructor-arity split that determines whether a class can receive `tm` at all
- the complete `ta`, `mt`, trigger, condition-key, state and stat-attribute vocabularies
- a capability map grouping the effect classes by what a player would recognise

**Every factual claim carries a provenance mark** stating how it is known — observed in a live
fight, present in the served payload, read from the binary, or inferred — with a legend and the
rule that a single-source claim is provisional. Claims that are unverified say so rather than
being asserted. Known limits are stated in the document: the type registry is known-good rather
than proven exhaustive, the multi-key `tm` separator is unconfirmed, and the area-spawner keys
are inherited from an abstract base.

Nothing was transcribed from recovered Kabam server data or from any other fork's authored
content. No asset, binary APK, captured audiovisual content, credential, or recovered server
dataset was added. Nothing under `media/` was touched. No new dependency was introduced. No
generated payload changed, so no regeneration was required.

## Unity 2020 porting patches (`tools/port202`)

The port scripts edit decompiled sources that are generated locally from the user's own
copy of the game and never committed. `unity2020_csharp_compat.py`,
`unity2020_firstpass_compat.py` and `unity6_compat.py` locate code by method signature,
a single-line anchor, or a regex over identifiers, and then replace a body, wrap a span
in try/catch, or insert a guard written for this project. They store interface names
and short anchors, not decompiled method bodies. Earlier versions of these scripts that
embedded multi-line decompiled blocks, and the removed `unity/StoryPort` prototype whose
classes were ported from the 2.0.2 decompiled source, were purged from the history of
`development` on 2026-09-26. The current bodies
are an order-total asset-eviction comparator, a missing-shader guard for contact shadows,
default node tuning when legacy tuning prefabs are absent, and texture assignment in an
async UI callback. `update_ui_atlases.py` reads atlas rectangles from a locally loaded 9.2
APK through a local AssetRipper instance and writes only into the git-ignored generated
project; its sprite aliases are asset names already present in that APK. No asset,
binary, captured audiovisual content, or recovered server data is added.

## 2026-10-09 — reproducible 9.2 recovery recipe

Adds the source-only `tools/recover_9_2.py` and `tools/build_recovered_9_2.py` workflow,
the Unity Editor build helper, and the local-use recipe in `DECOMPILATION.md`. The scripts
operate on an operator-supplied APK and verify pinned tool versions. The previously pinned
APK hash is now denied as a known debug-signed offline-patched artifact; the workflow rejects
its `libdothook` entry/dependency marker and requires independent package, version, and release
signer verification before accepting another hash. Generated inputs, assemblies, Unity project
copies, logs, signing keys, and APKs remain in ignored or operator-selected storage. The workflow
does not alter server-authored data. No APK, asset, binary, captured audiovisual content,
credential, or recovered server dataset is committed.

The local full 9.2 candidate is accepted only by its exact SHA-256 plus verified EBG JAR content
signature and clean ARM64 library/metadata hashes; its Android v2/v3 signing block is missing,
so it is extraction input rather than a directly installable original package. AssetRipper output
may be reused only after checking the candidate against the known patched APK and documenting the
limited native/signature-entry differences. No proprietary game code, assets, or generated build
artifacts are added to Git.

## 2026-10-10 — recovered SharpZipLib IL2CPP compatibility fallback

The local import repair may replace the malformed Cpp2IL body of
`DeflaterOutputStream.InitializeAESPassword` with an authored fallback that clears its two
byte-array output parameters. It applies only when the expected 0.86.0.518 method signature
and Cpp2IL debug-artifact markers are present. The original generated assembly remains
untouched under the ignored recovery directory; the fallback source contains no recovered
method body. AES-encrypted SharpZipLib ZIP entries are unsupported in APKs built with this
fallback. No APK, assembly, asset, or game-authored content is committed.

When a Unity 2020 IL2CPP log rejects a recovered method body, the import repair can match
that diagnostic by assembly, declaring type, method name, parameter count, and full signature,
then replace only that method body with an authored `NotSupportedException` stub. The generated
repair manifest records the affected methods. This preserves their API metadata but does not
reconstruct their behavior; a runtime call to one of them will fail explicitly. Constructors
and bodyless methods are excluded from this fallback.

## 2026-10-10 — UIBasicSprite constructor reconstruction

The local import repair reconstructs the malformed parameterless `UIBasicSprite`
constructor as a hand-authored IL body: call the verified `UIWidget` base constructor,
set `topType` to the recovered `AdvancedType.Sliced` value, and return. The Cpp2IL
body contained dead native-pointer scratch operations and invalid native-int stack
types; no game method body or asset is copied into the repository. The rewrite applies
only to the local generated `Assembly-CSharp-firstpass.dll` and validates the target
base type, field name, and field type before changing it.

## 2026-10-10 — packed color interpolation repair

The local Cpp2IL output for `EB.Math.Color.Lerp` contains unsupported ARM64 SIMD
instructions and invalid stack types. The versioned Cecil tool now reconstructs the
operation from the recovered packed RGBA field layout and the existing per-channel
integer interpolation helper. It writes only to the ignored generated
`Assembly-CSharp-firstpass.dll`, validates the target method and field signatures, and
contains no decompiled method block, APK bytes, or game assets.

The same Cecil repair also reconstructs `EB.Math.Color.Equals(Color)`, `op_Equality`,
and `op_Inequality` as packed-`UInt32` comparisons. This is an algorithmic rewrite from
the verified type layout and native comparison trace; it does not copy recovered method
text.

The color batch now also rebuilds non-premultiplied RGBA conversion, scalar channel
multiplication, and normalized `Vector3`/`Vector4` conversions from the 9.2 native traces.
It keeps the recovered packed field and existing constructor/helper signatures; no method
body text is copied.

The geometry batch reconstructs the `BoundingBox.Contains` point/box overloads from the
9.2 native comparison traces, plus the value-returning wrappers. It also reconstructs
`CameraData.Lerp` from the 9.2 native field layout and calls to the retained matrix and
Unity interpolation routines. These are new Cecil instruction sequences, not copied
method bodies or assets.

The follow-up geometry batch reconstructs `BoundingSphere.Contains` for points and spheres,
and `BoundingSphere.Intersects` for spheres and planes. It uses the traced enum outcomes,
center-distance calculations, and plane signed-distance comparisons.

`EB.Cache.PurgeCache(TimeSpan)` is rebuilt from the 9.2 native file-age sweep: it reads the
cache folder, compares each file's UTC age with the supplied shelf life, deletes expired
files, and retains the trace's `DiskCache`-gated failure logging. It does not copy decompiled
method text.

`EB.BitStream.Serialize(ref byte[])` is reconstructed from its native 9.2 trace. The rebuilt
method reads/writes the inline byte-length or `0xFF` plus little-endian 16-bit length prefix,
uses the retained `EB.Buffer` methods, and copies read data from the traced `ArraySegment<byte>`
into the by-reference output array. It retains the native 65,535-byte rejection and exception
message; the output is newly authored IL rather than copied decompiled text.

`EB.BitStream.Serialize(ref EB.Buffer)` is reconstructed independently from its native 9.2
trace. It uses the same inline/extended length prefix, constructs a `Buffer` over the traced
`ArraySegment<byte>` when reading, and delegates payload writes to `EB.Buffer.WriteBuffer`.
The 65,535-byte rejection is retained; this is newly authored IL, not copied method text.

`EB.BitStream.Serialize(ref string)` is reconstructed from the native 9.2 branch trace: it
assigns `EB.Buffer.ReadString()` through the by-reference parameter when reading and passes the
referenced string to `EB.Buffer.WriteString()` when writing. The serializer does not add framing
or transformation beyond those traced calls; this is newly authored IL.

`EB.Rendering.BeamRenderer.FloatEvlautation` is reconstructed from its native 9.2 ARM64 trace.
It returns the cached `List<float>` item after evaluations have been added; before then it
evaluates the supplied `AnimationCurve`, appends that value to the storage list, and returns it.
The replacement is newly authored IL based on the trace, not copied source text.

`EB.Rendering.BeamRenderer.Update` is reconstructed from its native 9.2 ARM64 trace. It applies
the Unity-object endpoint checks, records the elapsed time and endpoint transform positions,
performs the startup-duration interpolation, and calls `UpdateMesh` only while looping or before
the configured duration expires. The replacement is newly authored from the native field and
call flow; the older 2.0.2 method is not used as source text.

`Crash.DoAnim` is reconstructed from its native 9.2 ARM64 trace. It stops when the transform
compares equal to null, computes the elapsed damping factor, applies a per-axis random offset
within the shrinking magnitude, and writes the resulting local position when the transform is
present. The method is newly authored IL, not copied decompiled method text.

`EB.Base.BaseAPI.PlaceEntity` is reconstructed from its native 9.2 ARM64 trace. The replacement
builds the observed seven-argument `/base/place/{0}/{1}/{2}/{3}/{4}/{5}/{6}` route, including the
socket coordinate integer sentinel, then calls the retained `Post` and `Service` methods with
the 9.2 `Action<string, IDictionary>` callback type. The earlier 2.0.2 behavior was used only as
a field/order cross-check; its method body was not copied.

`EB.UI.DataBinding.CopyMemberBinding.EnsureTypeMatches` is reconstructed from the native 9.2
assignability, nullable-value, string-default, and `Activator.CreateInstance` paths. Its exception
handler retains the observed `EB.Debug.LogError` reporting behavior. The 2.0.2 implementation was
used only to cross-check the branch intent; no decompiled method body is copied.

The story-panel constructor repairs and `AlignUIElements.GetObjectBounds` use newly authored
control flow and IL based on the retained 9.2 native traces and recovered type metadata. The
`SocialHubTrayButton` constructor similarly uses newly authored vector initializers whose fields
and constants were checked against its 9.2 ARM64 field writes. These changes contain no APK
bytes, assets, or copied game method bodies; repaired assemblies and trace dumps remain in ignored
local recovery storage.

The current follow-up batch reconstructs `MatineeStage`'s traced position/rotation defaults, three
list fields, empty playback filter, completion flag, and base call. `SpecialAttackIcon` receives
the traced `FadeTime` value (`0.2f`) and base call. Both are newly authored CIL from the retained
9.2 ARM64 field writes; original generated assemblies and native dumps remain local.

`EB.Rendering.EBLightShadow`'s constructor is reconstructed from its retained 9.2 ARM64 field and
array traces, including the shadow-setting and texture-size defaults. This is newly authored CIL;
no APK bytes, assets, or copied managed method body are included.

`EB.Sparx.InventoryManager.OnUpdate` is newly authored from the clean 9.2 native trace and this
repository's existing offline-server wire contract (`item` plus absolute `quantity`). Its focused
`EB.Dot.String` helper and `EB.StringUtil` initializer/name filter are also authored as new CIL from
the native branch, field, and constant observations. The legacy `SafeKey` one-time warning output
is omitted; name normalization and filtering remain. A Unity-bundled Mono smoke check covers those
semantics and the inventory update path. No decompiled method body, APK, asset, or server-authored
data is copied into the repository; patched assemblies and analysis traces stay in ignored local
recovery storage.

The 9.2 `EB.SafeFloat.Value` and `EB.SafeInt.Value` setters are authored as CIL from their native
`BitConverter.GetBytes` → `SafeValue` construction/initialization → field-store path. The recovered
parameterless constructor is made assembly-visible so these sibling calls pass Mono access checks.
`EB.Hash.FNV64` now exits its complete-body repair before the shared tail emitter can append an
invalid second `ret`. `SocialStateModelBase.BadgeState`'s two-argument constructor writes the traced
`Count` and low-bit-normalized `IsNew` fields. The 16-byte BadgeState and 180-byte SafeInt ARM64
windows were compared at their native addresses against the confirmed-source `libil2cpp.so` and
matched exactly. Focused runtime checks ran against temporary local assemblies; none of those
assemblies, the APK, or trace output is tracked.

`ChapterPanel`'s four generated color initializers now reference the 9.2-compatible
`UnityEngine.Color32.op_Implicit(Color32) -> Color` member through its actual declaring type,
`Color32`. This is a metadata-owner correction to newly authored constructor IL; no game method
body is copied.

`EB.Math.Matrix.Add(ref,ref,ref)` is newly authored IL that adds its 16 traced scalar fields. The
field sequence and source bytes were checked against the clean 9.2 native image; no APK bytes,
assets, or decompiled game method body are included in the repository.

`EB.Missions.Map.SetupBuffs`, `Map.GetTile(int,int)`, and the MapTile buff-copy/list methods are
newly authored C# recovery sources compiled and transplanted as IL from clean 9.2 native traces
and recovered type metadata. The Vector2 membership branch follows the observed floor comparisons;
no APK bytes, assets, or copied decompiled game method body are tracked.

`DynamicScrollView.UpdatePositions` is newly authored CIL from the clean 9.2 cached-item and
transform trace. It uses recovered field/member metadata and retains no APK bytes, assets, or
copied game method body.

`EB.Math.Matrix.Multiply(ref,ref,out)` is newly authored CIL for the traced 4x4 scalar product.
Both inputs are snapshotted to preserve aliasing; no APK bytes, assets, or copied game method body
are retained.

`MapTile.x` and `MapTile.y` are newly authored CIL reading the traced Vector2 position backing
field; no APK bytes, assets, or copied game method body are retained.

`EBWorldPainterData.LinesCross` is newly authored CIL following the 9.2 segment cross-product
trace and recovered point fields. Its closed-interval endpoint behavior is inferred because the
recovered control flow is incomplete; no APK bytes, assets, or copied game method body are retained.

The `EB.Math.Matrix.Subtract`, `EB.Math.Point.Equals`, `EB.Math.Vector4` component getters, and
`EB.Math.Plane.Dot` repairs are newly authored from recovered signatures and scalar-field metadata.
No APK bytes, assets, or copied game method body are retained.

`EB.Collections.Pool<T>`'s stack constructor, default-value branch, and `Clear` operation are
newly authored from the recovered generic field and method signatures. `Clear` drops cached stack
items because recycled objects already receive the configured callback; no APK bytes, assets, or
copied game method body are retained.
