# 2.0.2 Mono server contract inventory

This document records server-facing facts recovered from the managed assemblies
exported from the 2.0.2 Mono APK. It is deliberately separate from the 9.2
IL2CPP client path and from the offline server's implementation. The evidence is
static exported source/IL and metadata; no request in this document has been
observed at runtime.

## Evidence boundary

The endpoint paths, HTTP verbs, API versions, request keys, and response
container selections below are direct observations from the exported managed
source. Nested response keys are included only where the reconstructed client
parses them. A response container such as `Hashtable` or `ArrayList` does not
prove the complete wire schema, field types, server behavior, boot order, or
that the endpoint is reachable in the offline configuration.

The relevant source roots are:

- `Assembly-CSharp-firstpass/EB.Sparx/LoginAPI.cs`
- `Assembly-CSharp/BCGAPI.cs`
- `Assembly-CSharp/Legacy/QuestsAPI.cs`
- `Assembly-CSharp/PVPAPI.cs`
- `Assembly-CSharp-firstpass/EB.Sparx/TutorialAPI.cs`
- `Assembly-CSharp-firstpass/EB.Sparx/MatchAPI.cs`
- `Assembly-CSharp/BCGManagerBase.cs`
- quest manager/parser types under `Assembly-CSharp`

The source files remain generated workspace output and are not modified by this
document.

## Common request and response behavior

The APIs create requests through `EB.Sparx.EndPoint`, add fields with
`Request.AddData`, and dispatch them with `EndPoint.Service`. Successful and
failed callbacks inspect the decompiled `Response` members:

- `sucessful` (the spelling present in the recovered client)
- `hashtable`
- `arrayList`
- `result`
- `localizedError`, `errorStr`, and `error`

The API-specific version fields observed in the client are:

| API wrapper | Version | Default request field |
| --- | ---: | --- |
| `BCGAPI` | 6 | `api` |
| `Legacy.QuestsAPI` | 11 | `api` |
| `PVPAPI` | 5 | `api` |

The quest item-placement, item-use, and hero-swap methods add
`GameStoreAPI.GameStoreAPIVersion` instead of the `QuestsAPI` helper's version,
and also add a generated `nonce`. That is a source-level distinction that must
not be collapsed into one guessed quest version.

## Authentication and account data

`EB.Sparx.LoginAPI` adds the following common fields to its authentication
requests when the corresponding client values are available:

`platform`, `device`, `benchmark`, `version`, `locale`, `lang`, `cellular`,
`tz`, `lsurl=true`, `buildmachine`, optional `buildconfig`, and optional `bid`.

The observed operations are:

| Verb | Path | Request fields observed | Success payload |
| --- | --- | --- | --- |
| POST | `/auth/init` | common fields plus caller-supplied data | `result` |
| POST | `/auth/enumerate` | `auth` plus common fields | `arrayList` |
| POST | `/auth/prelogin` | `_v=4`, `sha1` plus common fields | `result` |
| POST | `/auth/login` | `authenticator`, `credentials`, `_v=4`, `wskeData`, optional extra data | `hashtable`; reads `stoken` |
| GET | `/account` | account-specific fields in the method | `arrayList` |
| GET | `/account/data` | `apiversions`, `cachedchecks`, optional `nid`, `ncat`, `ncta` | `hashtable` |
| POST | `/account/unlink` | `authenticator`, `aid` | callback uses response status/error fields |
| POST | `/account/link` | `authenticator`, `credentials` | `hashtable`; reads `stoken` |

`LoginDataUri` is the exact string `/account/data`. The login implementation
stores a successful `stoken` in the endpoint and bug-report data; this is a
client-side observation, not proof of the server's token format.

The same wrapper also contains these account-management operations:

| Verb | Path | Request fields observed | Success payload |
| --- | --- | --- | --- |
| POST | `/account/check-name` | `name` | no payload used |
| POST | `/account/name` | `name`, `assignUnique` | `hashtable` |
| GET | `/account/support` | none | `hashtable` |
| POST | `/auth/users/new` | `authenticator`, `credentials`, `_v=4` plus common fields | `hashtable` |
| POST | `/auth/users/switch` | `target`, `authenticator`, `credentials`, `_v=4` plus common fields | `hashtable` |
| GET | `/auth/users/list` | `authenticator`, `credentials`, `_v=4` | `hashtable` |
| POST | `/account/auth_capture` | `auth`, `data` | `hashtable` |

`/auth/enumerate` is the one authentication method whose callback selects an
`ArrayList`; the other authentication callbacks above select `result` or
`hashtable` as shown. `TutorialAPI` uses API version `1` and posts `api=1` on
all five tutorial operations:

| Verb | Path | Request fields observed | Success payload |
| --- | --- | --- | --- |
| POST | `/tutorial/get-login-data` | `api=1` | `hashtable` |
| POST | `/tutorial/start-tutorial` | `api=1`, `tid` | `hashtable` |
| POST | `/tutorial/early-start-branch` | `api=1`, `tid`, `bid` | `hashtable` |
| POST | `/tutorial/start-branch` | `api=1`, `tid`, `bid` | `hashtable` |
| POST | `/tutorial/complete-tutorial` | `api=1`, `tid` | `hashtable` |

## BCG hero and user data

`BCGAPI.BCGAPIVersion` is `6`. The core reads used by the game-source layer
are:

| Verb | Path | Request fields observed | Success payload |
| --- | --- | --- | --- |
| GET | `/bcg/getLoginData` | `api=6` | `Hashtable` |
| POST | `/bcg/getBaseHeroData` | `api=6`; `heroes` list of objects containing `bid`, `rank`, `level`, `sig_lvl` | `ArrayList` |
| GET | `/bcg/getHeroXPCurve` | `api=6`, `bid`, `rank` | `ArrayList` |
| GET | `/bcg/getUserData` | `api=6` | `Hashtable` |
| POST | `/bcg/setSavedTeam` | `api=6`, `teamID`, `heroes` | `Hashtable` |
| POST | `/bcg/removeActiveTeam` | `api=6`, `activityID` | `Hashtable` |

The exported wrapper also contains mutation and test operations:

- `/bcg/upgrade-hero`: `hero`, `bids`, `evos`, and calculated `isoItems`.
- `/bcg/evolve-hero`: `hero`, with optional `version_id`, `set_id`, and
  `pricing_id` when matching pricing data is present.
- `/bcg/sell-hero`: `bids`, `target`, and `type`.
- `/bcg/buy-cat`: `evo` and `quantity`.
- `/bcg/convert-to-coin`: serialized `items`, `bids`, and `evos`.
- `/bcg/buy-stamina`: `hero`, `item`, and, for multi-use purchases, `amount`.
- `/bcg/clearUserData`: no additional fields observed.
- `/bcg/unittest-add-blueprint` and `/bcg/unittest-add-evo-blueprint`: `type`
  and `quantity`.

These methods return `Hashtable` on the successful callback in the recovered
client, including the mutation methods that may also return an NSF/error
payload. The client-side `BCGManagerBase` parses the login/user-data `bcg`
object and observes keys including `cdn`, `sigLvlMax`, `ratingPrecision`,
`heroRatingAttackWeight`, `heroRatingMaxHPWeight`, `statMods`,
`statModAppears`, `heroes`, `blueprints`, `characters`, `evoBlueprints`,
`synergyBonuses`, `attackValues`, `blueprintBonuses`, `heroClasses`,
`staminaRegen`, `rarityProperties`, `evoCosts`, `curves`, and `userData`.
Those are parsed-key observations, not a complete schema declaration.

`MatchAPI` is a separate generic wrapper with API version `2`. Its concrete
route suffix is supplied by the caller, so the exported source establishes the
following route families rather than one fixed match path:

| Verb | Path family | Request fields observed | Success payload |
| --- | --- | --- | --- |
| POST | `/matches/find-match/{matchType}` | caller-supplied `FindMatchData` dictionary | `hashtable` passed to a typed result constructor |
| POST | `/matches/activate-match/{matchType}` | caller-supplied activation dictionary | `hashtable` |
| POST | `/matches/retry-match/{matchType}` | none beyond `api=2` | `hashtable` |
| POST | `/matches/resolve-match/{matchType}` | `id` plus result and stats dictionaries | `hashtable` |
| POST | `/matches/simulate-match/{matchType}` | caller-supplied simulation dictionary | `hashtable` |

The wrapper's generic dictionaries are intentionally not expanded into guessed
fields here. The exact fields depend on the concrete `FindMatchData`, match
result, and match-stat types used by the caller.

## Quest and AVX flows

`Legacy.QuestsAPI.QuestsAPIVersion` is `11`. The primary quest operations are:

| Verb | Path | Request fields observed | Success payload |
| --- | --- | --- | --- |
| GET | `/quests/quest-list` | `api=11` | `ArrayList` |
| GET | `/quests/quest-historical-results/{category}` | `api=11` | `ArrayList` |
| GET | `/quests/quest-progression` | `api=11` | `Hashtable` |
| GET | `/quests/quest-active` | `api=11`; optional `categories`, `opponent` | `Hashtable` |
| POST | `/quests/quest-detail/{qid}` | `api=11`, `hash`, `setId` | `Hashtable` |
| POST | `/quests/quest-begin/{qid}` | `api=11`, `hash`, `setId`, `quitExisting`; optional `difficulty`, `costs`, `tm0...` | `Hashtable` |
| POST | `/quests/quest-join/{qid}` | `api=11`, `tm0...` | `Hashtable` |
| POST | `/quests/quest-moveto/{qid}/{x}/{y}` | `api=11` | `Hashtable` |
| POST | `/quests/quest-movedir/{qid}/{x}/{y}` | `api=11` | `Hashtable` |
| POST | `/quests/quest-recall/{qid}` | `api=11` | `Hashtable` |
| POST | `/quests/quest-quit/{qid}` | `api=11` | `Hashtable` |
| POST | `/quests/quest-reset-battle-timeout/{qid}` | `api=11` | `Hashtable` |
| POST | `/quests/quest-unlock-battle/{qid}` | `api=11`, `battleId` | `Hashtable` |
| GET | `/quests/quest-resync/avx/` | `api=11`, `qid`; optional `opponent` | `Hashtable` |

Additional quest methods use the game-store API version and a `nonce`:

- `/quests/use/{qid}`: `items`, optional `context`.
- `/quests/quest-placehero/{qid}`: `hero`, `x`, `y`.
- `/quests/quest-swapheroes/{qid}`: `uid1`, `character1`, `x1`, `y1`,
  `uid2`, `character2`, `x2`, `y2`.
- `/quests/send-pressure-notification`: `qid`, `mode`, `uid`.

The quest manager and parser code separately reads fields such as `version`,
`cfid`, `currentBattleId`, `currentBattleEnemy.id`, `withheldItems`, `users`,
`usedConsumableCount`, `slots`, `enemyHealth`, `activeQuests`, `teamData`,
`config`, `availableQuests`, `progression`, `qid`, `battleId`, and `success`.
These identify client parsing dependencies but do not establish which fields
are mandatory on every endpoint response.

## PVP flows

`PVPAPI.PVPAPIVersion` is `5`:

| Verb | Path | Request fields observed | Success payload |
| --- | --- | --- | --- |
| GET | `/pvp/get-login-data` | `api=5` | `Hashtable` |
| GET | `/pvp/get-user-data` | `api=5`, `pid` | `ArrayList` |
| GET | `/pvp/get-pvp-match-data` | `api=5`, `pids` | `ArrayList` |
| POST | `/pvp/find-arena-opponent` | `api=5`, `arenaID`; optional `numOpp`, `heroes`, `type` | `Hashtable` |
| POST | `/pvp/quit` | `api=5`, `pid` | `Hashtable` |
| POST | `/pvp/get-new-opponent` | `api=5`, `pid`; optional `type` | `Hashtable` |
| POST | `/pvp/select-opponent` | `api=5`, `pid`, `index`, `oid` | `Hashtable` |
| POST | `/pvp/match-loaded` | `api=5`, `mid` | no success payload used |
| POST | `/pvp/retry-match` | `api=5`, `mid` | `Hashtable` |
| POST | `/pvp/lockin` | `api=5`, `pid`, `heroes` | `Hashtable` |
| POST | `/pvp/clear-invalid-teams` | `api=5` | `Hashtable` |

## Static comparison with the offline server

The current offline server is not assumed to implement this inventory merely
because a filename or route fragment looks similar. The route and payload
comparison is recorded separately in
[`MONO_SERVER_COMPARISON.md`](MONO_SERVER_COMPARISON.md). It compares the
managed-client observations above with `Server/fakeserver.lbl`, the files under
`Server/responses/`, and `Server/responses/_prefix_rules.json`.

That comparison is static. It does not establish that a 2.0.2 Mono client has
made a request, that the server accepted one, or that a response survives the
client's full parser and boot sequence.

## Consequences for the offline client

This inventory gives the reconstruction work a concrete 2.0.2 contract target:
the managed replacement must preserve the request construction and response
container expectations of these wrappers, while the offline server must be
checked against the same paths and fields. It does not justify copying the 9.2
IL2CPP request flow, changing endpoint versions, or adding compatibility
members to managed assemblies.

The next useful evidence is a separate comparison of these static contracts
with the offline server's implemented routes and fixtures. That comparison
should identify missing server routes and payload keys without treating a
route-name match as proof of end-to-end compatibility.

## Verification status

Verified statically:

- endpoint literals, HTTP verbs, API version values, request keys, and callback
  payload container selections listed above are present in the exported Mono
  managed source;
- no generated source under `build/decompilation/mono-2fviys29/source` was
  modified.

Not verified:

- live HTTP traffic from the 2.0.2 Mono client;
- server acceptance of any listed request;
- complete nested response schemas or boot sequencing;
- rebuilt assembly installation, APK packaging, device execution, runtime
  compatibility, playability, or Unity/IL2CPP equivalence.
