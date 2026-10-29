# Online Arena fights

## Table of contents

- [Status](#status)
- [For players](#for-players)
- [The P2P network](#the-p2p-network)
- [Running the bootstrap node](#running-the-bootstrap-node)
- [How it works](#how-it-works)
- [The relay (tests only)](#the-relay-tests-only)
- [Wire protocol](#wire-protocol)
- [Code map](#code-map)
- [Limits and open questions](#limits-and-open-questions)

## Status

New in launcher 0.8 over a relay; since 0.12 (unreleased) the Arena is an online mode on a P2P network, with no online
panel. **Not yet run on a phone.** What is checked so far:

- the lockstep code (`TftfNet`) and the P2P network (`TftfMesh`) without Unity: `launcher/tools/net_lockstep_test/run.sh`
  runs two clients through the bootstrap node for three fights of random inputs with timing jitter, once with the two
  players linked directly and once with them unable to reach each other, so every line goes through a third game. Both
  applied the same inputs, random rolls and fight clock on every tick, ended at the same tick after the knock-out, and a
  dropped connection was noticed. Two more games then play once through the bootstrap node, the node is stopped, and
  they play again, finding each other only from what they remembered. The same fights also run through the relay script;
- a bootstrap node is deployed on fly.io (2026-10-29; its address is not in the repository, see
  [Running the bootstrap node](#running-the-bootstrap-node)). Checked from a PC: the cookie exchange, the reported public address (fly passes the sender's address and port through, which hole
  punching needs), the saved monthly count on the volume, and two test clients on the same PC pairing through it and
  playing a fight with identical results on both. Two devices behind different NATs have not been tried yet;
- the bootstrap node's tests: `python3 Server/test_p2p_bootstrap.py` (return-address cookie, introductions, private local
  addresses, the per-IP share of the list without refusing or evicting anyone, id takeover, garbage, rate limit, spending
  guard);
- the relay script: `python3 Server/test_pvp_relay.py`;
- the game patches (P54) and the glue compiled in the ARM64 shell project before the P2P change; since then the changed
  files (`TftfNet`, `TftfMesh`, `TftfPvpPanel`, `TftfArenaOnline`, `TftfNetGlue`) compile with `mcs` against that
  build's assemblies, but no APK has been built with them.

Everything about in-fight behaviour is still **[⚠ inferred]** from the decompiled 3.1.0 code cited in
[Code map](#code-map) until it is seen in a fight. A first test needs two devices (for example the A52 and the
emulator) and the bootstrap node deployed (see [Running the bootstrap node](#running-the-bootstrap-node)).

## For players

1. Open **FIGHT → ARENAS**. While the Arena screen is open the game is online and looks for another player who is
   in an Arena too; the line at the bottom right says "LOOKING FOR AN OPPONENT...", then "OPPONENT: *name*".
2. Once you have an opponent, both of you start a match in the Arena as usual (pick the team, accept, assign the
   matchups, CONTINUE). Each fight starts when both players have pressed CONTINUE; the screen says "Waiting for *name*
   to pick a bot..." until then.
3. Your opponent's fighter is the bot they picked for that fight. If the opponent disconnects, does not pick
   within two minutes, or their game stops, the AI takes over their fighter and the screen says so.

A match started before an opponent is found is against the AI, as offline, and the search stops until you come back to
the Arena screen, so nobody is paired with you in the middle of a match. Leaving the Arena screen before an opponent is
found also stops the search. Once paired, you stay paired across matches until one of you is away from the Arena
(no Arena screen, no online fight) for three minutes or closes the game. Without internet, or when the bootstrap node
cannot be reached, the line says "OFFLINE" and the game retries every few seconds; Arena matches are then against the
AI. The VR build behaves the same.

## The P2P network

Every game that has opened the Arena is a node of the network (`TftfMesh`) until the app closes, also while it is not
looking for an opponent, so it can carry other players' traffic. There is one queue: anyone looking is paired with
anyone else looking.

- **Remembered games first.** Each game keeps the games it was linked with in the last 30 days (up to 32, newest first)
  and its own UDP port in `tftf_p2p_peers.txt` in the app's data folder (`TftfMesh.CachePath`), saved every 5 s while
  something changed and when it leaves. Whenever a game has no neighbours (on start, or after losing them all) it opens
  its socket on the remembered port again (a new one if that port is taken) and sends hellos to its remembered games,
  4 at a time in random order, 3 s per batch. A remembered game whose router still lets its old port through, on the
  same Wi-Fi or with a public address, answers, and the two are linked. Only when every remembered game has failed does
  the game ask the bootstrap node. So a flooded bootstrap node, or one silent after its spending cap, keeps returning
  players out of the network only when none of the games they played with is reachable.
- **First hop (bootstrap node).** A game with nothing remembered, or none answering, sends a join to the bootstrap node
  (`TftfNet.Bootstrap`, see [Running the bootstrap node](#running-the-bootstrap-node)). The node answers with the address the internet sees the
  game at and up to four games already in the network, and tells those games about the newcomer at the same moment. Both
  sides then send hellos to each other's public and Wi-Fi addresses, which opens their NATs (UDP hole punching).
- **After the first hop, only the games talk.** Every 6 s each game tells its neighbours who its other neighbours are.
  A game with fewer than three neighbours asks one of them to introduce it to one of theirs, and the two punch through
  the same way. A game with neighbours talks to the bootstrap node only for a heartbeat every 25 s, so that the node
  can introduce later newcomers to it; its own discovery goes through its neighbours. No matchmaking or fight
  traffic goes through the bootstrap node.
- **Matchmaking.** A player looking for an opponent floods `SEEK` (room, always `-` in the game; name; addresses) through the network every
  2.5 s. A seeker with a lower node id that receives a `SEEK` for its room sends that player an `OFFER` with the seed;
  the other answers `ACCEPT` (or `NO` if it is already taken). The one that offered is the host (role 0). Only the lower
  id offers, so two players never offer to each other at once.
- **Fight traffic.** Both players punch towards each other as soon as they are paired. Messages to a player that is a
  neighbour go straight to it; otherwise they go hop by hop through other games, each game sending them the way that
  player's messages last arrived from (or to all its neighbours when it does not know). The fight's lines are numbered,
  acknowledged and resent after 200 ms, so they arrive complete and in order, as over a relay. `TftfMesh.Direct` says
  whether the opponent is reached directly or through other players.

## Running the bootstrap node

`Server/p2p_bootstrap/` holds the node (`bootstrap.py`, Python 3.7+, no dependencies), its `Dockerfile` and `fly.toml`.
It keeps only the list of games that sent a join in the last 75 s. Deploy on fly.io from that directory:

```bash
export FLY_APP=<app>                                             # the app name is the node's address: not in the repository
fly apps create "$FLY_APP"                                       # once
fly ips allocate-v4                                              # UDP on fly.io needs a dedicated IPv4 (about $2/month)
fly volumes create bootstrap_data --size 1                       # the spending guard's monthly count (about $0.15/month)
fly deploy --ha=false                                            # one machine; without --ha=false fly starts two
fly logs                                                         # "join <id> from <ip:port>" per new game
```

**Return-address check.** The node only lists a game, answers it with `W` or introduces it once its join carries a
cookie proving that the game receives datagrams at the address it sends from. A join without a valid cookie gets only
`C <cookie>`; the game sends its join again with it. The cookie is the first 16 hex digits of an HMAC-SHA256, under a
key drawn at each start of the node, of the sender's address and a 120 s time slot, accepted for the current and the
previous slot; the node keeps no state for unverified joins. So joins with forged sender addresses cannot fill the list
with fake games (that newcomers would then be sent to), and since `C` is never longer than the join it answers, they
cannot make the node send more traffic than it receives. After a restart of the node every game simply gets a new
cookie on its next heartbeat.

**Nobody is refused; the list is shared fairly.** Every verified join gets its full answer: its public address,
introductions, and the games it is introduced to are told to punch towards it. Only the list of games the node hands
to newcomers is limited, and nothing is ever evicted from it:

- at most 64 games per IP address are listed (`LISTED_PER_IP`). Behind a carrier NAT thousands of real players can share
  one IP; the ones beyond 64 still join and play, they are just not handed to newcomers by this node until a slot frees
  after 75 s of silence (the network's gossip and the games that remember them still find them). Because a full address
  never evicts, a sender sharing an IP with real players can only hold slots it keeps alive, not push the players out;
- a listed id keeps its address until that address has been silent for 30 s (`MOVE_AFTER`), so no one can take over or
  delist another game by sending its id (ids are visible through gossip). A game whose network changes is served at once
  and listed at its new address 30 s later. A restarted game has a new id anyway;
- when the whole list is full (20 000 games) a join is still served, only not listed.

**Local addresses are private ones only.** A game's local addresses are only a shortcut for players on the same Wi-Fi;
internet play uses the public address the node or a neighbour sees, which this does not touch. Both the node and every
game keep only 10/8, 172.16/12, 192.168/16, 100.64/10 (carrier NAT) and 169.254/16 entries (at most 4), so no one can make
the games introduced to them send hellos to an arbitrary public address.

**Spending guard (on the node, not in the game).** The app is one always-on `shared-cpu-1x` machine with 256 MB, one
dedicated IPv4 and one 1 GB volume (never scale it up); `bootstrap.py` prices its outbound traffic at fly's highest rate
($0.12/GB) on top of a fixed $5 for those, and keeps the month (UTC) within `MONTHLY_BUDGET_USD` (50 in `fly.toml`) with
two independent limits:

- **Monthly count.** It counts the bytes it sends and stops answering once the month's traffic budget (about 375 GB, far
  beyond what joins of a few hundred bytes need) is used, until the next month. The count is saved to
  `/data/budget.json` on the volume every 10 s, when the budget runs out and when fly stops the machine, and read back on
  start, so restarts and deploys do not reset it. An unreadable count file keeps the node silent for the rest of the
  month rather than risk reopening the budget (delete the file to reset it by hand).
- **Rate ceiling.** It never sends faster than the month's traffic budget spread over 31 days, about 140 KB/s (with a
  one-minute burst), so even if the count were lost on every restart a month could not cost more than the budget. Real
  load is far below it (1000 players online send about 4 KB/s of heartbeats).

Each IP address may send at most 100 datagrams a second: generous because many players can share one carrier IP (1000 of
them send 40 heartbeats a second), and a flood is bounded by the limits above and cannot be amplified (see above). Games
already in the network keep playing while the node is silent; only games that remember no reachable game cannot join.

**The game's bootstrap address** is not in the source. It comes from the environment variable `TFTF_P2P_BOOTSTRAP`
(`host:port`):

- **Phone and VR builds** read it when the build is assembled: `scripts/assemble_project.py` writes it into the build's
  copy of `shell/Tftf/TftfBuildConfig.cs` (the checked-in copy is empty). For the fly.io node:

  ```bash
  export TFTF_P2P_BOOTSTRAP=<app>.fly.dev:7777
  ./run.sh assemble && ./run.sh assets && ./run.sh build && ./run.sh finalize
  ```

  `run.sh doctor` notes when it is unset, and assembling warns; a build without it shows "OFFLINE - No online network
  is set for this build" on the Arena screen and plays Arena matches against the AI.
- **At run time** the same variable, where the platform passes one (Windows build, the lockstep test), overrides the
  built-in value.

Locally: `python3 Server/p2p_bootstrap/bootstrap.py --host 0.0.0.0 --port 7777` and `TFTF_P2P_BOOTSTRAP=<computer's
address>:7777` for the build.

## How it works

**Pairing.** See [The P2P network](#the-p2p-network): the player whose node id is lower becomes the **host** (role 0),
the other the **guest** (role 1); both receive the same random seed.

**Before each fight** the client's `PVPUtil.GenerateFightData` (`PVPUtil.cs:165`) is intercepted
**[📄 binary]**: the phone sends the bot the player picked for this fight (blueprint, rank, level,
signature level, arena scene) and waits for the opponent's. The fight's second fighter is then replaced by
the opponent's bot, and both fighters get the *base* attributes of their bot, rank and level from the local
server (`/bcg/getBaseHeroData`), so both phones build the same two fighters. Team synergy bonuses and
auto-fight are off in online fights, because they are only known on, or only act on, one phone. The host's
arena is used on both phones.

**Lockstep.** The fight simulation runs at a fixed 30 ticks per second (`Simulation.kFixedUpdateRate`,
`Simulation.cs:7`) **[📄 binary]**. An input pressed during tick *t* is sent to the opponent and applied at
tick *t* + 3 (100 ms) on both phones; a phone only simulates tick *t* once it holds the opponent's inputs for
it, and pauses the game (`Time.timeScale = 0`) while it waits. Each phone shows its own fighter as player 0
and feeds the opponent's inputs into player 1's `PlayerInput`, whose AI is switched off. The fight's clock
(`Simulation.Time`) starts at the same value on both phones, and the fight's random rolls (critical hits,
perfect blocks, buff chances) come from generators seeded alike on both phones: one per fighter plus one
shared.

**Host authority.** The two simulations are not guaranteed to stay identical: floating point, Unity's
animation and the order in which each phone processes "its" player 0 first can make them drift
**[⚠ inferred]**. So the host sends the health and special meter of both fighters every 6 ticks; the guest
compares them with its own values at the same tick and adds the difference. Only the host decides
knock-outs: on the guest a fighter whose health runs out stays at 1 health until the host's knock-out
arrives. Both phones leave the lockstep 30 ticks after the knock-out, or at the outro or time-out, and each
reports its result to its own local server.

**Failure handling.** A phone that leaves the lockstep sends 90 empty input ticks ahead, so the other phone is
not stuck. If the opponent's inputs do not arrive for 15 s (60 s at the start of a fight, while the slower
phone loads), or the connection drops, the AI takes over the opponent's fighter for the rest of the fight.

## The relay (tests only)

`Server/pvp_relay.py` is the matchmaking relay of launcher 0.8–0.11: it pairs two TCP clients that send the same room
code and forwards their lines. The game no longer offers it; `TftfNet.Connect(address)` still speaks it, and the
lockstep test runs the same fights through it, so a lockstep fault can be told apart from a P2P one.

```bash
python3 Server/pvp_relay.py --port 7777          # Python 3.7+, no dependencies
```

## Wire protocol

**P2P network.** One ASCII datagram per message over UDP, each starting with `TF1`. A descriptor is
`<id>/<public ip:port>/<local ip:port,...>` (`-` when unknown).

| Datagram | Between | Meaning |
|---|---|---|
| `J <id> <neighbours> <locals> <cookie>` | game → bootstrap | join (every 2 s, only after the remembered games failed) and heartbeat (every 25 s with neighbours); cookie from `C`, 16 zeros before the first |
| `C <cookie>` | bootstrap → game | the join had no valid cookie; only sent when not longer than the join |
| `W <your ip:port> <descriptor>...` | bootstrap → game | the game's public address; games to punch towards (only while it has fewer than 3 neighbours) |
| `I <descriptor>` | bootstrap or neighbour → game | punch towards this game |
| `H <id> <your ip:port> <locals>` | game ↔ game | hello: punching, then a keepalive every 4 s; a link drops after 20 s of silence |
| `G <descriptor>...` | neighbours | gossip: the sender's other neighbours |
| `X <id>` | game → neighbour | introduce me to your neighbour `<id>` |
| `R <src> <dst> <msg id> <ttl> <body>` | routed | a message for `dst` (`*` = everyone), forwarded hop by hop; ttl 8 |

Routed bodies: `SEEK <room> <name> <descriptor>`, `OFFER <session> <seed> <room> <name> <descriptor>`,
`ACCEPT <session> <name> <descriptor>`, `NO <session>`, `S <session> <ack> <seq> <line>` (a line below),
`A <session> <ack>` and `BYE <session>`.

**Relay (tests only).** Newline-terminated ASCII lines over one TCP connection per client. Relay lines:

| Line | Direction | Meaning |
|---|---|---|
| `HELLO <protocol> <room> <name>` | phone → relay | protocol is `1`; room `-` = public queue |
| `WAIT` | relay → phone | queued |
| `MATCH <role> <seed> <peer name>` | relay → phone | paired; role 0 host, 1 guest |
| `ERR <reason>` | relay → phone | `hello` or `version`; the relay then closes |
| `BYE` | relay → phone | the opponent's connection ended |

After `MATCH` the relay (or the P2P session) carries every line unchanged to the opponent:

| Line | Sender | Meaning |
|---|---|---|
| `P <ms>` / `O <ms>` | both | ping every 2 s / its echo (round-trip time shown in the fight) |
| `H <round> <blueprint> <rank> <level> <sig> <scene> <clock>` | both | bot for fight number `round` (counted per match); blueprint and scene URL-escaped; clock = sender's `Time.fixedTime` |
| `T <round> <tick> <events>` | both | inputs that take effect at `tick`; `-` = none |
| `S <round> <tick> <hp0> <hp1> <mana0> <mana1>` | host | health and meter after `tick`, host's fighter first |
| `K <round> <tick> <fighter>` | host | knock-out of fighter 0 (host's) or 1 (guest's) at `tick` |

Input events are one character each: `a`/`A` attack press/release, `b`/`B` block press/release, `s` special,
`1`–`4` swipe up/left/down/right (`SwipeDirection` values, `SwipeDirection.cs`) **[📄 binary]**.

## Code map

| Part | File |
|---|---|
| Relay (tests only) and its tests | `Server/pvp_relay.py`, `Server/test_pvp_relay.py` |
| P2P bootstrap node, fly.io files and its tests | `Server/p2p_bootstrap/`, `Server/test_p2p_bootstrap.py` |
| P2P network node in the game | `shell/Tftf/TftfMesh.cs` |
| Bootstrap address from `TFTF_P2P_BOOTSTRAP` | `shell/Tftf/TftfBuildConfig.cs`, `scripts/assemble_project.py` `write_build_config` |
| Lockstep test without Unity | `tools/net_lockstep_test/` |
| Connection, lockstep, snapshots (no game types; first-pass assembly) | `shell/Tftf/TftfNet.cs` |
| Arena search and its status line (NGUI, the card's font) | `shell/TftfMain/TftfArenaOnline.cs` |
| Connection pump, away timeout | `TftfNetRunner` in `shell/TftfMain/TftfNetGlue.cs` |
| In-fight banners (IMGUI) | `shell/Tftf/TftfPvpPanel.cs` |
| Glue to the game's fight (Assembly-CSharp) | `shell/TftfMain/TftfNetGlue.cs` |
| Calls into the glue, one-line anchors | `scripts/compat310.py` `online_pvp` (P54) |

P54 touches, all in the decompiled 3.1.0 code **[📄 binary]**: `Simulation.FixedUpdate` (tick gate, fight
clock, inputs every tick instead of once per rendered frame, end-of-tick authority), `Simulation.Update`
(pacing), `Simulation.RegisterComponents` / `OnDestroy` (fight start and end), the six `PlayerInput.On*`
input methods (capture), `BattleArbiter.EnableAI` (no AI online), `BattleArbiterInitState` (the opponent's
`PlayerInput` enabled), `PlayerController.ApplyDamage` (guest waits for the host's knock-out),
`PVPUtil.GenerateFightData` (bot exchange), `PVPScreen.CacheReferences` (marks the Arena screen for the search)
and the `Random.value` rolls in `PlayerAttributes`, `StatModifierController`, `GetRandom_BuffValue` and
`CreateArea_BuffEffect`.

## Limits and open questions

- **Untested on phones.** The first one-phone run (with the stand-in) and two-phone run are the next steps. Things to watch: whether the
  intro hands over to the fight at the same tick on both phones (inputs before that are ignored by the game),
  how far the guest drifts between snapshots, and the cinematic specials.
- Only health and the special meter are corrected; fighter positions are not. If they drift, hits can land on
  one phone and miss on the other until the next snapshot evens out the health **[⚠ inferred]**.
- The rest of the Arena flow (opponent list, points, rewards) is still the local server's; each phone
  records its own result.
- Rounds are counted per match. If one player retries a fight and the other does not, the fight numbers no
  longer line up and later fights fall back to the AI after the two-minute wait.
- Pausing on one phone pauses the fight on both.
- No encryption or authentication: the relay and the P2P network carry plain text, and any game could claim any node id.
- **P2P and NATs.** Hole punching works through most home routers. When both players are behind NATs that give every
  destination a different port (symmetric NATs, common on mobile networks), they cannot link directly and the fight
  goes through other games, which costs latency per hop. A game that cannot link to any other game at all (symmetric
  NAT and a network of only such games) waits for an opponent forever; use a relay then.
- The P2P network keeps a phone's socket open while the app runs, also for other players' fights. It sends only
  keepalives when idle, but Android may stop it in the background like the relay.
