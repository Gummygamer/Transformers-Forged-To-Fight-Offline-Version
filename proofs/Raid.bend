import Base

# Model of Server/raid.lbl: the progression of one raid and the health it records.
# pos: the node the attacker stands on (0 = start, k = defender k).
# beaten: how many defenders the attacker has beaten. count: defenders on the board.
type Raid is Data:
  Raid{count: Nat, pos: Nat, beaten: Nat}

def start(count: Nat) -> Raid:
  Raid{count, 0n, 0n}

# raid.lbl can_advance: pos < length(defenders) and beaten == pos
def can_advance(r: Raid) -> Bool:
  match r:
    case Raid{count, +pos, beaten}:
      Nat.is_lt(pos, count) && Nat.is_eq(beaten, pos)

# raid.lbl fight_pending: pos >= 1 and beaten == pos - 1
def fight_pending(r: Raid) -> Bool:
  match r:
    case Raid{count, +pos, beaten}:
      Nat.is_ge(pos, 1n) && Nat.is_eq(beaten, Nat.sub(pos, 1n))

def advance_if(ok: Bool, r: Raid) -> Raid:
  match ok:
    case False{}:
      r
    case True{}:
      match r:
        case Raid{count, pos, beaten}:
          Raid{count, 1n+pos, beaten}

# raid.lbl take_step: forward = (dx == 1 and dy == 0)
def take_step(forward: Bool, +r: Raid) -> Raid:
  match forward:
    case False{}:
      r
    case True{}:
      advance_if(can_advance(r), r)

def beat_if(ok: Bool, r: Raid) -> Raid:
  match ok:
    case False{}:
      r
    case True{}:
      match r:
        case Raid{count, +pos, beaten}:
          Raid{count, pos, pos}

# raid.lbl fight_outcome / fought_active: a won pending fight sets beaten = pos;
# a lost fight or no pending fight leaves the progression unchanged.
def fight(won: Bool, +r: Raid) -> Raid:
  match won:
    case False{}:
      r
    case True{}:
      beat_if(fight_pending(r), r)

# raid.lbl played_fight_reply: the raid is won when the fight at the last defender is won
def raid_won(r: Raid) -> Bool:
  match r:
    case Raid{count, pos, beaten}:
      Nat.is_eq(beaten, count)

# Every reachable state: the attacker is at its beaten front or on the next defender,
# and never past the last one.
def ok(r: Raid) -> Bool:
  match r:
    case Raid{+count, +pos, +beaten}:
      Nat.is_le(beaten, pos) && Nat.is_le(pos, 1n+beaten) && Nat.is_le(pos, count)

type RaidEvent is Data:
  Step{forward: Bool}
  Fight{won: Bool}

def apply(e: RaidEvent, r: Raid) -> Raid:
  match e:
    case Step{forward}:
      take_step(forward, r)
    case Fight{won}:
      fight(won, r)

def run(es: List<&2, RaidEvent>, r: Raid) -> Raid:
  match es:
    case Nil{}:
      r
    case Con{e, rest}:
      run(rest, apply(e, r))

# Health, in hundredths of full health (100 = full, 1 = the 0.01 floor).
# raid.lbl fought_active: a fighter that is still standing keeps the health it reported,
# kept within [0.01, 1.0] (at_least(at_most(hp, 1.0), 0.01)); a beaten one drops to 0.
def health(+v: Nat) -> Nat:
  Nat.max(Nat.min(v, 100n), 1n)

def hero_after(won: Bool, my: Nat) -> Nat:
  match won:
    case True{}:
      health(my)
    case False{}:
      0n

def defender_after(won: Bool, their: Nat) -> Nat:
  match won:
    case True{}:
      0n
    case False{}:
      health(their)
