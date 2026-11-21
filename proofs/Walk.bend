import Base
import ./Board.bend as B

# Model of the story walk in Server/fakeserver.lbl: the saved Position (pending,
# completed, cleared), moved_offsets, cleared_with_start and position_after_win.

# gamedata.lbl quest_has_encounter_at: the tiles that open a fight.
def on_path(+d: Nat, +col: Nat, +p: B.Pos) -> Bool:
  match p:
    case B.P{+x, +y}:
      Nat.is_eq(y, col) && Nat.is_ge(x, 1n) && Nat.is_lt(x, d)

def encounter(b: B.Board, +p: B.Pos) -> Bool:
  match b:
    case B.Linear{}:
      on_path(11n, 1n, p)
    case B.Custom{}:
      on_path(4n, 1n, p)
    case B.Standalone{}:
      on_path(5n, 2n, p)
    case B.Act3{}:
      B.Pos.eq(p, B.P{1n, 2n}) || B.Pos.eq(p, B.P{2n, 1n}) || B.Pos.eq(p, B.P{2n, 3n}) || B.Pos.eq(p, B.P{3n, 2n})
    case B.Motormaster{}:
      B.Pos.eq(p, B.P{1n, 2n}) || B.Pos.eq(p, B.P{3n, 2n}) || B.Pos.eq(p, B.P{2n, 3n})

# The saved position: where the player stands, whether a fight is open there, whether
# the tile it stands on was already won, and every tile won or walked through.
# left is ghost state for the laws (the server keeps no such list): every tile the
# player has walked off, in order.
type Walk is Data:
  W{pos: B.Pos, pending: Bool, completed: Bool, cleared: List<&2, B.Pos>, left: List<&2, B.Pos>}

def start(+b: B.Board) -> Walk:
  W{B.start(b), False{}, False{}, [], []}

def keep_or_add(known: Bool, +p: B.Pos, xs: List<&2, B.Pos>) -> List<&2, B.Pos>:
  match known:
    case True{}:
      xs
    case False{}:
      List.append(&2, B.Pos, xs, [p])

# cleared_with_start: the tile being left joins cleared when it holds no encounter.
def cleared_with_start(+b: B.Board, +s: B.Pos, +cl: List<&2, B.Pos>) -> List<&2, B.Pos>:
  keep_or_add(encounter(b, s) || B.has(cl, s), s, cl)

def arrive(+rev: Bool, +b: B.Board, +n: B.Pos, cl: List<&2, B.Pos>, left: List<&2, B.Pos>) -> Walk:
  W{n, encounter(b, n) && Bool.not(rev), rev, cl, left}

# moved_offsets: no step while a fight is open; otherwise only a step the server accepts.

def moved(ok: Bool, +b: B.Board, +n: B.Pos, +w: Walk) -> Walk:
  match ok:
    case False{}:
      w
    case True{}:
      match w:
        case W{+s, pending, +completed, +cl, left}:
          arrive(B.has(cl, n) || (completed && B.Pos.eq(n, s)), b, n, cleared_with_start(b, s, cl), List.append(&2, B.Pos, left, [s]))

def walk_pos(w: Walk) -> B.Pos:
  match w:
    case W{s, pending, completed, cl, left}:
      s

# position_after_win: a won fight of this quest closes the open fight and clears its tile.

def move_if(pending: Bool, +b: B.Board, +n: B.Pos, +w: Walk) -> Walk:
  match pending:
    case True{}:
      w
    case False{}:
      moved(B.server_accepts(b, walk_pos(w), n), b, n, w)

def move(+b: B.Board, +n: B.Pos, +w: Walk) -> Walk:
  match w:
    case W{+s, +pending, +completed, +cl, +left}:
      move_if(pending, b, n, W{s, pending, completed, cl, left})

def won_if(pending: Bool, +s: B.Pos, completed: Bool, +cl: List<&2, B.Pos>, left: List<&2, B.Pos>) -> Walk:
  match pending:
    case False{}:
      W{s, False{}, completed, cl, left}
    case True{}:
      W{s, False{}, True{}, keep_or_add(B.has(cl, s), s, cl), left}

type WalkEvent is Data:
  Go{to: B.Pos}
  Victory{}
  Defeat{}

def win(+w: Walk) -> Walk:
  match w:
    case W{+s, +pending, completed, +cl, left}:
      won_if(pending, s, completed, cl, left)

def apply(+b: B.Board, e: WalkEvent, +w: Walk) -> Walk:
  match e:
    case Go{n}:
      move(b, n, w)
    case Victory{}:
      win(w)
    case Defeat{}:
      w

def run(es: List<&2, WalkEvent>, +b: B.Board, +w: Walk) -> Walk:
  match es:
    case Nil{}:
      w
    case Con{e, rest}:
      run(rest, b, apply(b, e, w))

# The invariant: standing on an encounter that is not cleared means its fight is open,
# and a completed tile is a cleared one.
def imp(a: Bool, b: Bool) -> Bool:
  match a:
    case False{}:
      True{}
    case True{}:
      b

def ok_parts(+enc: Bool, +cleared: Bool, pending: Bool, completed: Bool) -> Bool:
  imp(enc && Bool.not(cleared), pending) && imp(completed, cleared)

def ok(+b: B.Board, +w: Walk) -> Bool:
  match w:
    case W{+s, pending, completed, +cl, left}:
      ok_parts(encounter(b, s), B.has(cl, s), pending, completed)


# Every encounter tile in the list is cleared.
def all_ok(+b: B.Board, ps: List<&2, B.Pos>, +cl: List<&2, B.Pos>) -> Bool:
  match ps:
    case Nil{}:
      True{}
    case Con{+p, rest}:
      imp(encounter(b, p), B.has(cl, p)) && all_ok(b, rest, cl)

# Every encounter tile the player has walked off is one it has won.
def left_ok(+b: B.Board, w: Walk) -> Bool:
  match w:
    case W{s, pending, completed, +cl, left}:
      all_ok(b, left, cl)

def inv(+b: B.Board, +w: Walk) -> Bool:
  ok(b, w) && left_ok(b, w)
