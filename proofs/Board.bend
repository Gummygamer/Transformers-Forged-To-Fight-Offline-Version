import Base

# Model of the story boards in Server/gamedata.lbl (the map payload the client draws
# and walks) and of the server's move check (quest_can_move + moved_offsets in
# Server/fakeserver.lbl). Coordinates are the wire Vector2 {x, y}; grid[x][y].
type Pos is Data:
  P{x: Nat, y: Nat}

def Pos.eq(a: Pos, b: Pos) -> Bool:
  match a b:
    case P{ax, ay} P{bx, by}:
      Nat.is_eq(ax, bx) && Nat.is_eq(ay, by)

# A move from one tile to another.
type Move is Data:
  M{src: Pos, dst: Pos}

type Board is Data:
  Linear{}       # build_linear_quest_map: the stock story missions, 11 x 11, path column 1
  Custom{}       # build_custom_story_map: custom story acts 1 and 2, 4 x 4, path column 1
  Standalone{}   # build_custom_story_standalone_map: 5 x 5, path column 2
  Act3{}         # build_custom_story_act3_map: a diamond with two routes
  Motormaster{}  # build_custom_story_motormaster_map: a fork after the intro

def dim(b: Board) -> Nat:
  match b:
    case Linear{}:
      11n
    case Custom{}:
      4n
    case Standalone{}:
      5n
    case Act3{}:
      5n
    case Motormaster{}:
      4n

def when(c: Bool, xs: List<&2, Pos>) -> List<&2, Pos>:
  match c:
    case False{}:
      []
    case True{}:
      xs

def pick(c: Bool, a: List<&2, Pos>, b: List<&2, Pos>) -> List<&2, Pos>:
  match c:
    case True{}:
      a
    case False{}:
      b

# links_for / custom_story_encounter_tile / custom_story_standalone_tile: a path tile
# links the tile before it and the tile after it.
def line_links(+d: Nat, +col: Nat, +p: Pos) -> List<&2, Pos>:
  match p:
    case P{+x, +y}:
      when(Nat.is_eq(y, col) && Nat.is_lt(x, d), List.append(&2, Pos, when(Nat.is_ge(x, 1n), [P{Nat.sub(x, 1n), col}]), when(Nat.is_lt(1n+x, d), [P{1n+x, col}])))

def act3_links(+p: Pos) -> List<&2, Pos>:
  pick(Pos.eq(p, P{0n, 2n}), [P{1n, 2n}],
  pick(Pos.eq(p, P{1n, 2n}), [P{0n, 2n}, P{2n, 1n}, P{2n, 3n}],
  pick(Pos.eq(p, P{2n, 1n}), [P{1n, 2n}, P{3n, 2n}],
  pick(Pos.eq(p, P{2n, 3n}), [P{1n, 2n}, P{3n, 2n}],
  pick(Pos.eq(p, P{3n, 2n}), [P{2n, 1n}, P{2n, 3n}], [])))))

def motormaster_links(+p: Pos) -> List<&2, Pos>:
  pick(Pos.eq(p, P{2n, 1n}), [P{2n, 2n}],
  pick(Pos.eq(p, P{2n, 2n}), [P{1n, 2n}, P{3n, 2n}],
  pick(Pos.eq(p, P{1n, 2n}), [P{2n, 2n}, P{2n, 3n}],
  pick(Pos.eq(p, P{3n, 2n}), [P{2n, 2n}, P{2n, 3n}],
  pick(Pos.eq(p, P{2n, 3n}), [P{1n, 2n}, P{3n, 2n}], [])))))

# The links of the tile at p, as the client reads them (MapTile.links).
def links(b: Board, +p: Pos) -> List<&2, Pos>:
  match b:
    case Linear{}:
      line_links(11n, 1n, p)
    case Custom{}:
      line_links(4n, 1n, p)
    case Standalone{}:
      line_links(5n, 2n, p)
    case Act3{}:
      act3_links(p)
    case Motormaster{}:
      motormaster_links(p)

def has(xs: List<&2, Pos>, +p: Pos) -> Bool:
  match xs:
    case Nil{}:
      False{}
    case Con{h, t}:
      Pos.eq(h, p) || has(t, p)

def has_links(+b: Board, +p: Pos) -> Bool:
  Bool.not(List.is_empty(&2, Pos, links(b, p)))

# A tile the player can stand on: every walkable tile of these boards has a link.
def walkable(+b: Board, +p: Pos) -> Bool:
  has_links(b, p)

# Distance one in both axes (a board step may be diagonal, as on the diamond boards).
def near(+a: Nat, +b: Nat) -> Bool:
  Nat.is_eq(a, b) || Nat.is_eq(1n+a, b) || Nat.is_eq(a, 1n+b)

def adjacent(+s: Pos, +n: Pos) -> Bool:
  match s n:
    case P{+sx, +sy} P{+nx, +ny}:
      near(sx, nx) && near(sy, ny) && Bool.not(Pos.eq(P{sx, sy}, P{nx, ny}))

# ---- The server's move check (Server/gamedata.lbl quest_can_move) ----
# Every board: re-enter a walkable tile, or step to a tile its links name.
def quest_can_move(+b: Board, +s: Pos, +n: Pos) -> Bool:
  (Pos.eq(s, n) && walkable(b, s)) || has(links(b, s), n)

# fakeserver.lbl moved_offsets: the target must also lie inside the board's x range.
def server_accepts(+b: Board, +s: Pos, +n: Pos) -> Bool:
  match n:
    case P{+nx, +ny}:
      Nat.is_lt(nx, dim(b)) && quest_can_move(b, s, P{nx, ny})

# ---- The rule both sides must share ----
# The client lets the player tap a linked tile (IsMoveValid = links.Contains(target)),
# and re-entering the tile it stands on replays that tile.
def client_allows(+b: Board, +s: Pos, +n: Pos) -> Bool:
  (Pos.eq(s, n) && walkable(b, s)) || has(links(b, s), n)

# ---- Finite enumeration of a board, for laws checked tile by tile ----
def col_of(+x: Nat, ys: List<&2, Nat>) -> List<&2, Pos>:
  match ys:
    case Nil{}:
      []
    case Con{y, rest}:
      P{x, y} <> col_of(x, rest)

def box_go(xs: List<&2, Nat>, +d: Nat) -> List<&2, Pos>:
  match xs:
    case Nil{}:
      []
    case Con{x, rest}:
      List.append(&2, Pos, col_of(x, List.range(d)), box_go(rest, d))

# Every coordinate of the board's square grid.
def box(+b: Board) -> List<&2, Pos>:
  box_go(List.range(dim(b)), dim(b))

def walkable_tiles_go(+b: Board, ps: List<&2, Pos>) -> List<&2, Pos>:
  match ps:
    case Nil{}:
      []
    case Con{+p, rest}:
      List.append(&2, Pos, when(walkable(b, p), [p]), walkable_tiles_go(b, rest))

def walkable_tiles(+b: Board) -> List<&2, Pos>:
  walkable_tiles_go(b, box(b))

def cons_if(c: Bool, x: Move, xs: List<&2, Move>) -> List<&2, Move>:
  match c:
    case True{}:
      x <> xs
    case False{}:
      xs

# The pairs (s, n), n anywhere on the grid, where server and client disagree.
def disagree_at(+b: Board, +s: Pos, ns: List<&2, Pos>) -> List<&2, Move>:
  match ns:
    case Nil{}:
      []
    case Con{+n, rest}:
      cons_if(Bool.xor(server_accepts(b, s, n), client_allows(b, s, n)), M{s, n}, disagree_at(b, s, rest))

def disagree_go(+b: Board, ss: List<&2, Pos>) -> List<&2, Move>:
  match ss:
    case Nil{}:
      []
    case Con{+s, rest}:
      List.append(&2, Move, disagree_at(b, s, box(b)), disagree_go(b, rest))

# Every move one side allows and the other refuses, from every tile the player can stand on.
def disagreements(+b: Board) -> List<&2, Move>:
  disagree_go(b, walkable_tiles(b))

# Links that point off the walkable board or further than one tile.
def bad_links_at(+b: Board, +s: Pos, ns: List<&2, Pos>) -> List<&2, Move>:
  match ns:
    case Nil{}:
      []
    case Con{+n, rest}:
      cons_if(Bool.not(adjacent(s, n) && walkable(b, n)), M{s, n}, bad_links_at(b, s, rest))

def bad_links_go(+b: Board, ss: List<&2, Pos>) -> List<&2, Move>:
  match ss:
    case Nil{}:
      []
    case Con{+s, rest}:
      List.append(&2, Move, bad_links_at(b, s, links(b, s)), bad_links_go(b, rest))

def bad_links(+b: Board) -> List<&2, Move>:
  bad_links_go(b, walkable_tiles(b))

# The start tile of each board (the tile marked start in its map).
def start(b: Board) -> Pos:
  match b:
    case Linear{}:
      P{0n, 1n}
    case Custom{}:
      P{0n, 1n}
    case Standalone{}:
      P{0n, 2n}
    case Act3{}:
      P{0n, 2n}
    case Motormaster{}:
      P{2n, 1n}

# Reachability by links, breadth first, with one round per tile of the grid.
def add_if(known: Bool, +n: Pos, seen: List<&2, Pos>) -> List<&2, Pos>:
  match known:
    case True{}:
      seen
    case False{}:
      n <> seen

def add_new(ns: List<&2, Pos>, +seen: List<&2, Pos>) -> List<&2, Pos>:
  match ns:
    case Nil{}:
      seen
    case Con{+n, rest}:
      add_new(rest, add_if(has(seen, n), n, seen))

def expand(todo: List<&2, Pos>, +b: Board, seen: List<&2, Pos>) -> List<&2, Pos>:
  match todo:
    case Nil{}:
      seen
    case Con{+p, rest}:
      expand(rest, b, add_new(links(b, p), seen))

def reach_go(fuel: Nat, +b: Board, +seen: List<&2, Pos>) -> List<&2, Pos>:
  match fuel:
    case 0n:
      seen
    case 1n+f:
      reach_go(f, b, expand(seen, b, seen))

def reachable(+b: Board) -> List<&2, Pos>:
  reach_go(List.length(&2, Pos, box(b)), b, [start(b)])

# Walkable tiles no walk from the start can reach.
def unreachable_go(+seen: List<&2, Pos>, ps: List<&2, Pos>) -> List<&2, Pos>:
  match ps:
    case Nil{}:
      []
    case Con{+p, rest}:
      List.append(&2, Pos, when(Bool.not(has(seen, p)), [p]), unreachable_go(seen, rest))

def unreachable(+b: Board) -> List<&2, Pos>:
  unreachable_go(reachable(b), walkable_tiles(b))

# walkableCount, as each map payload sends it.
def walkable_count(b: Board) -> Nat:
  match b:
    case Linear{}:
      11n
    case Custom{}:
      4n
    case Standalone{}:
      5n
    case Act3{}:
      5n
    case Motormaster{}:
      5n
