import Base
import ./Raid.bend as R
import ./Board.bend as B
import ./Abilities.bend as A
import ./Data.bend as D
import ./Walk.bend as WK
import ./Stats.bend as S

# ---- Raids (Server/raid.lbl) ----

# A raid never both waits on a fight and lets the attacker walk on:
# the defender in front must be beaten before the next step.
law raid_pending_blocks_step:
  for r: R.Raid
  {Bool.and(R.fight_pending(r), R.can_advance(r)) == False{} : Bool}

# Only a forward step moves the attacker.
law raid_only_forward:
  for r: R.Raid
  {R.take_step(False{}, r) == r : R.Raid}

# A lost fight keeps the progression: the defender stays unbeaten.
law raid_loss_keeps_progress:
  for r: R.Raid
  {R.fight(False{}, r) == r : R.Raid}

# Any sequence of steps and fights from a fresh raid stays in a valid state:
# beaten <= pos <= beaten + 1 and pos <= count.
law raid_always_ok:
  for count: Nat
  for es: List<&2, R.RaidEvent>
  {R.ok(R.run(es, R.start(count))) == True{} : Bool}

# Health is a fraction of full health, whatever the client reports.
law raid_hero_health_bounded:
  for won: Bool
  for my: Nat
  {Nat.is_le(R.hero_after(won, my), 100n) == True{} : Bool}

law raid_defender_health_bounded:
  for won: Bool
  for their: Nat
  {Nat.is_le(R.defender_after(won, their), 100n) == True{} : Bool}

# A bot that wins a fight is still standing afterwards.
law raid_winner_stands:
  for my: Nat
  {Nat.is_ge(R.hero_after(True{}, my), 1n) == True{} : Bool}

# ---- Story boards (Server/gamedata.lbl maps, quest_can_move; fakeserver.lbl moved_offsets) ----

# The server accepts exactly the moves the client offers: a drawn link, or re-entering
# the tile the player stands on. No skipped nodes, no refused links.
law board_server_agrees_with_client:
  for b: B.Board
  {B.disagreements(b) == [] : List<&2, B.Move>}

# Every link joins two walkable tiles one step apart.
law board_links_are_steps:
  for b: B.Board
  {B.bad_links(b) == [] : List<&2, B.Move>}

# Every walkable tile can be reached from the start by following links.
law board_all_reachable:
  for b: B.Board
  {B.unreachable(b) == [] : List<&2, B.Pos>}

# The map's walkableCount is the number of tiles the player can stand on.
law board_walkable_count:
  for b: B.Board
  {List.length(&2, B.Pos, B.walkable_tiles(b)) == B.walkable_count(b) : Nat}

# ---- Ability assignment (Server/gamedata.lbl bot_abilities, Server/data/stat_modifiers.json) ----

# The kit of a bot, for any bot id.
def kit(+bid: String) -> List<&2, String>:
  A.lookup(D.kits(), bid)

# Every ability a bot carries is a row of the statMods catalog the client receives.
law abilities_exist:
  for +bid: String
  {A.all_known(D.catalog(), kit(bid)) == True{} : Bool}

# No bot carries the same ability twice.
law abilities_distinct:
  for +bid: String
  {A.distinct(kit(bid)) == True{} : Bool}

# Every ability a bot carries sits in one of the working ta x mt cells, and a damage
# effect lands on the opponent as a debuff.
law abilities_coherent:
  for +bid: String
  {A.all_sound(D.catalog(), kit(bid)) == True{} : Bool}

# Every ability a bot carries has a card (statModAppears) with a title, a description and
# one icon-font glyph, and the card tells the truth: every number it quotes is the row's,
# it states the row's strength and duration, and it names no moment the row does not fire on.
law abilities_carded:
  for +bid: String
  {A.all_carded(D.mags(), D.cards(), kit(bid)) == True{} : Bool}

# The kit table names only bots of the roster, each once: a misspelt bot id would grant
# nothing, and a second branch for a bot would never be read.
law kits_name_roster_bots:
  {A.all_in(D.roster(), A.bids(D.kits())) == True{} : Bool}

law kits_name_each_bot_once:
  {A.distinct(A.bids(D.kits())) == True{} : Bool}

# ---- The story walk (Server/fakeserver.lbl moved_offsets, position_after_win) ----

# While a fight is open the player cannot move.
law walk_open_fight_blocks_moves:
  for b: B.Board
  for n: B.Pos
  for s: B.Pos
  for c: Bool
  for cl: List<&2, B.Pos>
  for left: List<&2, B.Pos>
  {WK.move(b, n, WK.W{s, True{}, c, cl, left}) == WK.W{s, True{}, c, cl, left} : WK.Walk}

# Whatever the player taps, wins or loses, every encounter tile it has walked off is
# one it has won: no fight is ever walked past.
law walk_never_skips_a_fight:
  for +b: B.Board
  for +es: List<&2, WK.WalkEvent>
  {WK.left_ok(b, WK.run(es, b, WK.start(b))) == True{} : Bool}

# Standing on an encounter it has not won means that fight is open, and a tile marked
# completed is a cleared one.
law walk_unwon_encounter_is_open:
  for +b: B.Board
  for +es: List<&2, WK.WalkEvent>
  {WK.ok(b, WK.run(es, b, WK.start(b))) == True{} : Bool}

# ---- Hero stats and specials (Server/gamedata.lbl base_stats, special_damage_ratios) ----

# A bot one level higher is never weaker.
law stats_level_never_weaker:
  for +r: Nat
  for +l: Nat
  {Nat.is_le(S.factor(r, l), S.factor(r, 1n+l)) == True{} : Bool}

# A bot one rank higher is never weaker.
law stats_rank_never_weaker:
  for +r: Nat
  for +l: Nat
  {Nat.is_le(S.factor(r, l), S.factor(1n+r, l)) == True{} : Bool}

# There is one damage ratio per special bar, each special hits harder than the one
# before it, and every special hits harder than a plain hit (ratio 1.0).
law specials_one_per_bar:
  {List.length(&2, Nat, D.special_ratios()) == D.max_special_attacks() : Nat}

law specials_escalate:
  {S.increasing(D.special_ratios()) == True{} : Bool}

law specials_beat_a_hit:
  {S.all_above(100n, D.special_ratios()) == True{} : Bool}
