# Static comparison: 2.0.2 Mono client versus offline server

This is a compatibility work-item inventory, not a runtime result. The client
side is the exported 2.0.2 managed source summarized in
[`MONO_SERVER_CONTRACTS.md`](MONO_SERVER_CONTRACTS.md). The server side is the
tracked source in `Server/fakeserver.lbl`, its response fixtures, and
`Server/responses/_prefix_rules.json`.

No Mono request was captured, no 2.0.2 APK was packaged, and no response below
has been accepted by the original Mono runtime. A route is marked `partial`
when a handler exists but its request or response shape is not the documented
Mono contract. A route-name match is not treated as compatibility.

## Route coverage

### Authentication, account, and tutorials

| Mono route | Offline-server evidence | Static result |
| --- | --- | --- |
| `POST /auth/init` | `POST__auth_init.json` | partial: canned `result` is an empty object; request fields are not validated |
| `POST /auth/enumerate` | `POST__auth_enumerate.json` | mismatch: fixture `result` is an object containing `accounts`, while `LoginAPI.Enumerate` selects `Response.arrayList` |
| `POST /auth/prelogin` | `POST__auth_prelogin.json` | partial: canned `result` has `nonce` and `salt`; `_v=4` and `sha1` are not validated |
| `POST /auth/login` | `auth_login_response` in `Server/fakeserver.lbl` | partial: returns a hashtable and `stoken`, but only a subset of the request is used; no API-version validation |
| `GET /account` | no dynamic handler or matching fixture | missing |
| `GET /account/data` | `GET__account_data.json` | container match (`Hashtable`), nested Mono boot compatibility not established |
| `POST /account/link` / `POST /account/unlink` | no handler or matching fixture | missing |
| `POST /account/check-name` / `POST /account/name` | no handler or matching fixture | missing |
| `GET /account/support` | no handler or matching fixture | missing |
| `/auth/users/new`, `/auth/users/switch`, `/auth/users/list` | no handler or matching fixture | missing |
| `POST /account/auth_capture` | no handler or matching fixture | missing |
| `POST /tutorial/get-login-data` | `static_dynamic` → `tutorial_login_response` | partial: returns a hashtable-shaped result, but `api=1` is not validated |
| `POST /tutorial/start-tutorial`, `/start-branch`, `/early-start-branch`, `/complete-tutorial` | `tutorial_response` | partial: `tid`/`bid` are read, but API version and full tutorial state are not validated |

### BCG

| Mono route | Offline-server evidence | Static result |
| --- | --- | --- |
| `GET /bcg/getLoginData` | `GET__bcg_getLoginData.json` | partial: direct top-level BCG keys are present; `api=6` is not validated |
| `POST /bcg/getBaseHeroData` | `static_dynamic` → `build_base_hero_details` | container match (`ArrayList`); request uses `heroes`, with server-side identifier fallbacks documented in its source |
| `GET /bcg/getHeroXPCurve` | no dynamic handler or matching fixture | missing |
| `GET /bcg/getUserData` | `static_dynamic` → `build_user_data` | container match (`Hashtable`); nested shape is authored for the current offline flow, not Mono-verified |
| `POST /bcg/setSavedTeam` | `quest_dynamic` → `saved_team_response` | partial: `teamID` and `heroes` are consumed; `api=6` is ignored |
| `POST /bcg/removeActiveTeam` | no handler or matching fixture | missing |
| `/bcg/upgrade-hero`, `/evolve-hero`, `/sell-hero`, `/buy-cat`, `/convert-to-coin`, `/buy-stamina`, `/clearUserData`, `/unittest-add-blueprint`, `/unittest-add-evo-blueprint` | no handler or matching fixture | missing |

### Quests

| Mono route | Offline-server evidence | Static result |
| --- | --- | --- |
| `GET /quests/quest-list` | `GET__quests_quest-list.json` | container match (`ArrayList`); authored quest data is not evidence of Mono parity |
| `GET /quests/quest-historical-results/{category}` | no handler or matching fixture | missing |
| `GET /quests/quest-progression` | `GET__quests_quest-progression.json` | mismatch: fixture result is `[]`, while `QuestsAPI.GetQuestsProgression` selects `Response.hashtable` |
| `GET /quests/quest-active` | `GET__quests_quest-active.json` | mismatch: fixture result is `[]`, while `QuestsAPI.GetActiveQuests` selects `Response.hashtable` |
| `POST /quests/quest-detail/{qid}` | `static_dynamic` → `build_quest_detail` | container match (`Hashtable`); nested parser coverage still needs comparison per quest mode |
| `POST /quests/quest-begin/{qid}` | `quest_begin_response` | partial: `tm0...` team fields are consumed; `api`, `hash`, `setId`, costs, and difficulty are not fully validated |
| `POST /quests/quest-join/{qid}` | no handler or matching fixture | missing |
| `POST /quests/quest-moveto/{qid}/{x}/{y}` | no handler; only `quest-movedir` is dynamic | missing |
| `POST /quests/quest-movedir/{qid}/{x}/{y}` | `movedir_response` | partial: route and offsets are handled; `api=11` is ignored |
| `POST /quests/quest-recall/{qid}` | no handler or matching fixture | missing |
| `GET /quests/quest-resync/avx/` | no handler or matching fixture | missing |
| `POST /quests/quest-quit/{qid}`, `/quest-reset-battle-timeout/{qid}`, `/quest-unlock-battle/{qid}` | no handler or matching fixture | missing |
| `POST /quests/use/{qid}`, `/quest-placehero/{qid}`, `/quest-swapheroes/{qid}`, `/send-pressure-notification` | no handler or matching fixture | missing |

The offline server does handle `POST /matches/resolve-match/{type}` for its
current quest result flow, but the handler returns an empty body after updating
local state. `MatchAPI.ResolveMatch` expects a successful `Hashtable`, so this
is a response-contract mismatch even though the route fragment is present.

### PVP and generic matches

| Mono route | Offline-server evidence | Static result |
| --- | --- | --- |
| `GET /pvp/get-login-data` | `GET__pvp_get-login-data.json` | container match (`Hashtable`); `api=5` is not validated |
| `GET /pvp/get-user-data` | `pvp_arena_state_response` | mismatch: handler returns an object, while `PVPAPI.GetUserData` selects `Response.arrayList`; it also reads `arenaID` before `pid` |
| `GET /pvp/get-pvp-match-data` | no matching handler or fixture | missing |
| `POST /pvp/find-arena-opponent` | `pvp_opponent_response` | partial: `arenaID`, optional heroes, and aliases are accepted; response is a generated object rather than a Mono-verified shape |
| `POST /pvp/quit` | `pvp_quit_arena_response` | partial: peer state is used; the Mono `pid` is not used to select the arena |
| `POST /pvp/get-new-opponent` | `pvp_opponent_response` | partial: `pid` is accepted by the arena-id fallback; response shape is not Mono-verified |
| `POST /pvp/select-opponent` | `pvp_select_opponent_response` | mismatch: Mono sends `oid`; handler reads `opponentID` and otherwise invents a fallback |
| `POST /pvp/match-loaded` | `pvp_fight_loaded_response` | mismatch: Mono sends `mid`; handler reads `fightID`/`fid`, so its generated `fightID` can be empty; Mono currently ignores the successful body |
| `POST /pvp/retry-match` | `pvp_retry_match_response` | partial: handler returns generated match data but does not use Mono's `mid` to select a match |
| `POST /pvp/lockin` | `pvp_lock_in_response` | partial: `pid` and `heroes` are accepted through the arena-id fallback; API version is ignored |
| `POST /pvp/clear-invalid-teams` | no matching handler or fixture | missing |

The server also has heartbeat, lobby, fight-relay, result, and alias routes
(`pvp/heartbeat`, `pvp/lobby`, `pvp/fight-post`, `pvp/fight-poll`,
`pvp/report-result`, `pvp/match-result`, `get-active-pvp-data`,
`lock-in`, and others). These are server-side extensions for the repository's
current 9.2/companion flows; their existence does not establish that the 2.0.2
Mono client calls them.

For `MatchAPI` the static result is narrower:

- `/matches/activate-match/pvp_fight` has a dynamic handler, but other
  `{matchType}` values are not established.
- `/matches/find-match/{matchType}`, `/matches/retry-match/{matchType}`, and
  `/matches/simulate-match/{matchType}` have no handler or matching fixture.
- `/matches/resolve-match/{matchType}` has the empty-body mismatch described
  above. The server reads `qid`/`results.result`/`result` for its own quest
  resolution instead of the generic `id` plus result/stat dictionaries defined
  by `MatchAPI`.

## Version and key conclusions

The managed client has distinct version constants: BCG `6`, Quests `11`, PVP
`5`, Tutorial `1`, and generic Match `2`. The offline server does not validate
these `api` fields and does not select response schemas by version. Therefore a
version mismatch cannot be claimed from a route-name match; version
compatibility remains unverified. The concrete request/response mismatches
above are sufficient to show that the current server is not yet a demonstrated
2.0.2 Mono backend.

The most important response-container mismatches are:

1. `/auth/enumerate`: object fixture versus `ArrayList` callback.
2. `/quests/quest-progression`: array fixture versus `Hashtable` callback.
3. `/quests/quest-active`: array fixture versus `Hashtable` callback.
4. `/pvp/get-user-data`: generated object versus `ArrayList` callback.
5. `/matches/resolve-match/{type}`: empty body versus `Hashtable` callback.

These are static facts from the current source and fixtures, not claims that the
client has already failed on them. No server routes or fixtures were changed as
part of this comparison because doing so would require a separately justified
compatibility implementation and later Mono runtime verification.

## Verification boundary

Performed:

- compared the exported Mono endpoint literals, request construction, callback
  container selections, and selected nested parser keys with the tracked offline
  server handlers and fixtures;
- kept the 2.0.2 inventory and this comparison separate from the 9.2 IL2CPP path.

Not performed:

- live 2.0.2 HTTP traffic or network capture;
- server acceptance tests using the original Mono client;
- rebuilt-DLL installation, APK substitution, packaging, device execution, or
  runtime verification;
- proof that any current route reaches the client in the same boot order as the
  original service.
