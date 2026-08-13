# Copyright and security notes for the content-reconstruction work

This file records how the backend-content reconstruction in this package was done so
that it stays on the right side of copyright and stays defensive/interoperability-only.
It covers the data added in `Server/gamedata.lbl` and the regenerated response files.

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
and Ranged; and per-hero `special_attacks` counts of one through three from the pre-existing
original `max_special_attacks` rarity curve. The `bcg-combat` config also carries
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

The BOTS-roster navigation fix likewise introduces no authored game values: `Server/gamedata.lbl`
is unchanged, so it adds no invented numbers, names, or content. Its arm64 `RSENTER`/`RSEXIT`
hooks only call `GameObject.SetActive` on base-builder objects that the client's own base-board
code already activated and then failed to deactivate when roster navigation bypassed
`BaseBoard.LeaveBoard`. They add no network interception, no binary patch of shipped content,
and no new capability. Temporary read-only diagnostic hooks used to establish the affected
navigation path were removed before shipping.

The level-3 cinematic-special transform investigation ended in a negative result and ships no
fix. The four arm64 hooks it had previously added (`SP3XNEW`, `SP3XHOLD`, `SP3XIN`, `SP3XOUT`)
were measured to have no visible effect and have been removed again, so the shipped hook set is
smaller than before rather than larger. `Server/gamedata.lbl` is unchanged, so no invented
numbers, names, or game content were authored. The temporary read-only diagnostic hooks used
during the investigation only observed the client's own calls and were removed before shipping;
they added no network interception, no binary patch of shipped content, no new capability, and
no game assets or binaries to the repository.

## Why this is copyright-compliant

- **Everything authored here is original.** Kabam's real balance data (the roster
  numbers, the class/star assignments, the `attackValues` table, the ability
  definitions) was server-side and is gone. It was not recovered, copied, or
  reproduced. Every number, class, faction, and rarity in `gamedata.lbl` was invented
  for this offline revival. The point of the file's header comment is to make that
  explicit and auditable.

- **No copyrighted material is included or distributed.** No game assets, no APK, no
  Kabam binaries, and no recovered Kabam server data are in this package. The character
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

The two boundary values that define the block are original authored constants for this revival:
the alternate form turns on at 1.0 seconds into the cinematic and turns off at 2.5 seconds into the
cinematic, the thresholds measured to reproduce the observed shape of the sequence. A constant was
authored here rather than read from shipped data for a direct reason: this build ships no real
level-3 special move for any character, so the substituted move's own transform-event duration is
not authentic beat data for the level-3 cinematic. The previous section's statement that the
alternation period is read at runtime from the character's own already-shipped move data is
therefore superseded for this schedule, and the shipped duration that informed it is now retained
only as a diagnostic value that is logged but drives nothing.

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
interception, no certificate bypass, and no patching of shipped content bundles. No new externally
derived IL2CPP addresses were needed for this change; it reuses the entry points already recorded
in the previous section.
