import Base

# Model of base_stats in Server/gamedata.lbl:
#   factor = (1 + 0.35 * (max(1, rank) - 1)) * (1 + 0.025 * (max(1, level) - 1))
# and hp, attack = to_integer(star value * class multiplier * factor). Written exactly:
#   1 + 0.35 * s = (20 + 7 s) / 20 and 1 + 0.025 * s = (40 + s) / 40,
# so factor * 800 = rank_num(rank) * level_num(level). gen_data.py refuses to run if
# either constant changes in gamedata.lbl.

def steps(+n: Nat) -> Nat:
  Nat.sub(Nat.max(1n, n), 1n)

def rank_num(+r: Nat) -> Nat:
  Nat.add(20n, Nat.mul(7n, steps(r)))

def level_num(+l: Nat) -> Nat:
  Nat.add(40n, steps(l))

def factor(+r: Nat, +l: Nat) -> Nat:
  Nat.mul(rank_num(r), level_num(l))

def increasing_from(xs: List<&2, Nat>, +a: Nat) -> Bool:
  match xs:
    case Nil{}:
      True{}
    case Con{+b, rest}:
      Nat.is_lt(a, b) && increasing_from(rest, b)

# Each special in the list hits harder than the one before it.
def increasing(xs: List<&2, Nat>) -> Bool:
  match xs:
    case Nil{}:
      True{}
    case Con{+a, rest}:
      increasing_from(rest, a)

def all_above(+floor: Nat, xs: List<&2, Nat>) -> Bool:
  match xs:
    case Nil{}:
      True{}
    case Con{+a, rest}:
      Nat.is_gt(a, floor) && all_above(floor, rest)
