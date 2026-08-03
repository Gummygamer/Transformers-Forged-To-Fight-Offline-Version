# Copyright and security notes for the content-reconstruction work

This file records how the backend-content reconstruction in this package was done so
that it stays on the right side of copyright and stays defensive/interoperability-only.
It covers the data added in `Server/gamedata.py` and the regenerated response files.

## What was added

`Server/gamedata.py` is a hand-authored source of the server-side content that Kabam
used to stream to the client and that shut down with their servers in early 2020. From
it, `python Server/gamedata.py` regenerates two response files:

- `Server/responses/GET__bcg_getLoginData.json` — the config blob (blueprints,
  characters, and the `attackValues` combat balance table).
- `Server/responses/GET__bcg_getUserData.json` — the owned roster.

`Server/fakeserver.py`'s dynamic `/bcg/getBaseHeroData` handler now computes hero stats
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
itself sends — are wire-key and asset-id names that already exist inside the user's own copy of
the app. Observing which key the client sends and answering in the same shape is an
interoperability schema observation, the same established precedent recorded above for the
hit-stun wire keys. The in-APK C server change introduces no new capability: it makes the
already-shipped local loopback server compute the same response bodies the Python server already
produced, so the bundled build behaves like the host-server build. It adds no network
interception, no new binary patch, and no new hook.

The BOTS-roster navigation fix likewise introduces no authored game values: `Server/gamedata.py`
is unchanged, so it adds no invented numbers, names, or content. Its arm64 `RSENTER`/`RSEXIT`
hooks only call `GameObject.SetActive` on base-builder objects that the client's own base-board
code already activated and then failed to deactivate when roster navigation bypassed
`BaseBoard.LeaveBoard`. They add no network interception, no binary patch of shipped content,
and no new capability. Temporary read-only diagnostic hooks used to establish the affected
navigation path were removed before shipping.

The level-3 cinematic-special transform investigation ended in a negative result and ships no
fix. The four arm64 hooks it had previously added (`SP3XNEW`, `SP3XHOLD`, `SP3XIN`, `SP3XOUT`)
were measured to have no visible effect and have been removed again, so the shipped hook set is
smaller than before rather than larger. `Server/gamedata.py` is unchanged, so no invented
numbers, names, or game content were authored. The temporary read-only diagnostic hooks used
during the investigation only observed the client's own calls and were removed before shipping;
they added no network interception, no binary patch of shipped content, no new capability, and
no game assets or binaries to the repository.

## Why this is copyright-compliant

- **Everything authored here is original.** Kabam's real balance data (the roster
  numbers, the class/star assignments, the `attackValues` table, the ability
  definitions) was server-side and is gone. It was not recovered, copied, or
  reproduced. Every number, class, faction, and rarity in `gamedata.py` was invented
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
