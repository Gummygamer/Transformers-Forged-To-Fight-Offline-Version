# Online Arena fights

## Table of contents

- [Status](#status)
- [For players](#for-players)
- [Running a relay](#running-a-relay)
- [How it works](#how-it-works)
- [Wire protocol](#wire-protocol)
- [Code map](#code-map)
- [Limits and open questions](#limits-and-open-questions)

## Status

New in launcher 0.8. **Not yet run on a phone.** What is checked so far:

- the relay: unit tests, `python3 Server/test_pvp_relay.py`;
- the phone's relay (`TftfRelay`): the same unit tests against it (`PVP_RELAY_PORT`), run by the next item;
- the lockstep code (`TftfNet`) without Unity: `launcher/tools/net_lockstep_test/run.sh` runs two clients through
  each relay (the script and `TftfRelay`) for three fights of random inputs with timing jitter; both applied the same inputs, random rolls and fight
  clock on every tick, ended at the same tick after the knock-out, and a dropped connection was noticed;
- the game patches (P54) and the glue compile in the ARM64 shell project.

Everything about in-fight behaviour is still **[⚠ inferred]** from the decompiled 3.1.0 code cited in
[Code map](#code-map) until it is seen in a fight. For a first test with one phone, `Server/pvp_fake_peer.py` stands in
for the opponent (see [Running a relay](#running-a-relay)).

## For players

1. Someone runs a relay that both phones can reach: either one phone (**Host on this phone**, below) or a
   computer (see [Running a relay](#running-a-relay)).
2. Both players open **FIGHT → ARENAS** and pick the same Arena.
3. Tap **ONLINE PVP** at the bottom right of the Arena screen.
   - **Hosting:** tap **Host on this phone**. The panel shows this phone's address (for example
     `192.168.1.23`); tell it to the other player. This phone then waits for an opponent on its own relay.
   - **Joining:** enter the relay's address (`host` or `host:port`; the default port is 7777) and tap
     **Find opponent**.

   Optionally enter a room code first. Players with the same room code are matched; an empty room code
   matches with anyone else who left it empty.
4. When the button reads "Online: *name*", start a match in that Arena as usual (pick the team, accept,
   assign the matchups, CONTINUE). Each fight starts when both players have pressed CONTINUE; the screen
   says "Waiting for *name* to pick a bot..." until then.
5. Your opponent's fighter is the bot they picked for that fight. If the opponent disconnects, does not pick
   within two minutes, or their game stops, the AI takes over their fighter and the screen says so.

The address, room code and the last four relay addresses are remembered. **Leave online play** in the
panel disconnects. In the VR build the panel is shown too and says "headset" instead of "phone".

## Running a relay

**On a phone.** **Host on this phone** runs the relay inside the app (`TftfRelay`, same protocol and behaviour as
the script below) on port 7777, or on the port typed in the address field (`:port`), and connects that phone to it.
Other phones on the same Wi-Fi connect to the address the panel shows; over the internet the router must forward
that TCP port to the phone, and mobile networks usually do not allow incoming connections at all. The relay keeps
running while the app is open, also for other players' matches, until **Stop hosting**. Android may stop it when
the app is in the background.

**On a computer.**

```bash
python3 Server/pvp_relay.py --port 7777          # Python 3.7+, no dependencies
```

The relay only pairs players and forwards their lines; it needs no game data and never reads the fight
traffic. For play over the internet it must be reachable on that TCP port (port forwarding or a small
server). It holds no state besides the waiting queue, so restarting it only drops current matches.

**Testing with one phone.** Run the relay on the PC, `adb reverse tcp:7777 tcp:7777`, enter `127.0.0.1` as the
address on the phone and tap **Find opponent** (empty room code), then run

```bash
python3 Server/pvp_fake_peer.py --bot optimusprime_cin_tf      # --mash: the stand-in taps attack now and then
```

The stand-in answers the phone's bot with its own and returns an input line for every tick, so the fight runs at
full speed against an opponent that stands still. It does not simulate the fight, so it must join second (it is
then the guest and the phone is the host that decides knock-outs).

## How it works

**Pairing.** Both phones connect to the relay with a room code. The first to arrive waits; the second is
paired with it. The first becomes the **host** (role 0), the second the **guest** (role 1); both receive the
same random seed.

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

## Wire protocol

Newline-terminated ASCII lines over one TCP connection per phone. Relay lines:

| Line | Direction | Meaning |
|---|---|---|
| `HELLO <protocol> <room> <name>` | phone → relay | protocol is `1`; room `-` = public queue |
| `WAIT` | relay → phone | queued |
| `MATCH <role> <seed> <peer name>` | relay → phone | paired; role 0 host, 1 guest |
| `ERR <reason>` | relay → phone | `hello` or `version`; the relay then closes |
| `BYE` | relay → phone | the opponent's connection ended |

After `MATCH` the relay forwards every line unchanged to the opponent:

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
| Relay and its tests | `Server/pvp_relay.py`, `Server/test_pvp_relay.py` |
| Relay on the phone | `shell/Tftf/TftfRelay.cs` |
| Stand-in opponent for one-phone tests | `Server/pvp_fake_peer.py` |
| Lockstep test without Unity | `tools/net_lockstep_test/` |
| Connection, lockstep, snapshots (no game types; first-pass assembly) | `shell/Tftf/TftfNet.cs` |
| The Arena button and panel (NGUI, the game's sprites and fonts) | `shell/TftfMain/TftfPvpArenaUi.cs` |
| Connection pump and in-fight banners (IMGUI) | `shell/Tftf/TftfPvpPanel.cs` |
| Glue to the game's fight (Assembly-CSharp) | `shell/TftfMain/TftfNetGlue.cs` |
| Calls into the glue, one-line anchors | `scripts/compat310.py` `online_pvp` (P54) |

P54 touches, all in the decompiled 3.1.0 code **[📄 binary]**: `Simulation.FixedUpdate` (tick gate, fight
clock, inputs every tick instead of once per rendered frame, end-of-tick authority), `Simulation.Update`
(pacing), `Simulation.RegisterComponents` / `OnDestroy` (fight start and end), the six `PlayerInput.On*`
input methods (capture), `BattleArbiter.EnableAI` (no AI online), `BattleArbiterInitState` (the opponent's
`PlayerInput` enabled), `PlayerController.ApplyDamage` (guest waits for the host's knock-out),
`PVPUtil.GenerateFightData` (bot exchange), `PVPScreen.CacheReferences` (marks the Arena screen for the panel)
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
- No encryption or authentication: the relay forwards plain text. Run your own relay.
