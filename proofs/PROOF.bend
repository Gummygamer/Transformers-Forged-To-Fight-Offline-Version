import Base
import ./LAWS.bend as L
import ./Raid.bend as R
import ./Board.bend as B
import ./Abilities.bend as A
import ./Data.bend as D
import ./Walk.bend as WK
import ./Stats.bend as S

def L.raid_only_forward(r):
  {==}

def L.raid_loss_keeps_progress(r):
  {==}

# is_eq(b, p) and is_eq(b, p + 1) never hold together.
law eq_next_excl:
  for +b: Nat
  for +p: Nat
  for +x: Bool
  {Bool.and(Nat.is_eq(b, p), Bool.and(x, Nat.is_eq(b, 1n+p))) == False{} : Bool}

def eq_next_excl(b, p, x):
  match b p:
    case 0n 0n:
      match x:
        case False{}:
          {==}
        case True{}:
          {==}
    case 0n 1n+q:
      {==}
    case 1n+c 0n:
      {==}
    case 1n+c 1n+q:
      eq_next_excl(c, q, x)

def L.raid_pending_blocks_step(r):
  match r:
    case R.Raid{count, pos, beaten}:
      match pos:
        case 0n:
          {==}
        case 1n+p:
          match p:
            case 0n:
              eq_next_excl(beaten, 0n, Nat.is_lt(1n, count))
            case 1n+(+q):
              eq_next_excl(beaten, 1n+q, Nat.is_lt(2n+q, count))

law min_le_r:
  for +a: Nat
  for +b: Nat
  {Nat.is_le(Nat.min(a, b), b) == True{} : Bool}

def min_le_r(a, b):
  match a b:
    case 0n 0n:
      {==}
    case 0n 1n+q:
      {==}
    case 1n+p 0n:
      {==}
    case 1n+p 1n+q:
      min_le_r(p, q)

law max_zero:
  for +a: Nat
  {a == Nat.max(a, 0n) : Nat}

def max_zero(a):
  match a:
    case 0n:
      {==}
    case 1n+p:
      {==}

# A clamped health is never above full.
law health_le_full:
  for +v: Nat
  {Nat.is_le(R.health(v), 100n) == True{} : Bool}

def health_le_full(v):
  match v:
    case 0n:
      {==}
    case 1n+(+m):
      %max_zero(Nat.min(m, 99n)) : {Nat.is_le(1n+_, 100n) == True{} : Bool}
      min_le_r(m, 99n)

def L.raid_hero_health_bounded(won, my):
  match won:
    case False{}:
      {==}
    case True{}:
      health_le_full(my)

def L.raid_defender_health_bounded(won, their):
  match won:
    case True{}:
      {==}
    case False{}:
      health_le_full(their)

def L.raid_winner_stands(my):
  match my:
    case 0n:
      {==}
    case 1n+0n:
      {==}
    case 1n+1n+(+k):
      %max_zero(Nat.min(1n+k, 99n)) : {Nat.is_ge(1n+_, 1n) == True{} : Bool}
      {==}

# ---- raid_always_ok: the state invariant survives every event ----

law imp_refl:
  for +x: Bool
  {WK.imp(x, x) == True{} : Bool}

def imp_refl(x):
  match x:
    case False{}:
      {==}
    case True{}:
      {==}

def bdisc(b: Bool) -> Type:
  match b:
    case False{}:
      Unit
    case True{}:
      Empty

law ft_absurd:
  for +e: {False{} == True{} : Bool}
  Empty

def ft_absurd(e):
  %e : bdisc(_)
  Unit{}

law ex_falso:
  for -G: Type
  for +e: Empty
  G

def ex_falso(G, e):
  match e:

law imp_trans:
  for +x: Bool
  for +y: Bool
  for +z: Bool
  for +xy: {WK.imp(x, y) == True{} : Bool}
  for +yz: {WK.imp(y, z) == True{} : Bool}
  {WK.imp(x, z) == True{} : Bool}

def imp_trans(x, y, z, xy, yz):
  match x y:
    case False{} _:
      {==}
    case True{} False{}:
      ex_falso({WK.imp(True{}, z) == True{} : Bool}, ft_absurd(xy))
    case True{} True{}:
      yz

law and_false_r:
  for +x: Bool
  {False{} == Bool.and(x, False{}) : Bool}

def and_false_r(x):
  match x:
    case False{}:
      {==}
    case True{}:
      {==}

# A step from a valid state where the attacker may advance lands in a valid state.
law step_core:
  for +c: Nat
  for +p: Nat
  for +b: Nat
  {WK.imp(Bool.and(R.can_advance(R.Raid{c, p, b}), R.ok(R.Raid{c, p, b})), R.ok(R.Raid{c, 1n+p, b})) == True{} : Bool}

def step_core(c, p, b):
  match c p b:
    case 0n 0n _:
      {==}
    case 0n 1n+q _:
      {==}
    case 1n+0n 0n 0n:
      {==}
    case 1n+1n+e 0n 0n:
      {==}
    case 1n+d 0n 1n+a:
      {==}
    case 1n+(+d) 1n+(+q) 0n:
      %and_false_r(Nat.is_lt(q, d)) : {WK.imp(Bool.and(_, R.ok(R.Raid{1n+d, 1n+q, 0n})), R.ok(R.Raid{1n+d, 2n+q, 0n})) == True{} : Bool}
      {==}
    case 1n+d 1n+q 1n+a:
      step_core(d, q, a)

law le_refl:
  for +p: Nat
  {True{} == Nat.is_le(p, p) : Bool}

def le_refl(p):
  match p:
    case 0n:
      {==}
    case 1n+q:
      le_refl(q)

law le_succ:
  for +p: Nat
  {True{} == Nat.is_le(p, 1n+p) : Bool}

def le_succ(p):
  match p:
    case 0n:
      {==}
    case 1n+q:
      le_succ(q)

law imp_and3:
  for +x: Bool
  for +y: Bool
  for +z: Bool
  {WK.imp(Bool.and(Bool.and(x, y), z), z) == True{} : Bool}

def imp_and3(x, y, z):
  match x y z:
    case False{} _ _:
      {==}
    case True{} False{} _:
      {==}
    case True{} True{} False{}:
      {==}
    case True{} True{} True{}:
      {==}

# A fight from a valid state lands in a valid state, won or lost.
law fight_ok:
  for +c: Nat
  for +p: Nat
  for +b: Nat
  for +k: Bool
  {WK.imp(R.ok(R.Raid{c, p, b}), R.ok(R.beat_if(k, R.Raid{c, p, b}))) == True{} : Bool}

def fight_ok(c, p, b, k):
  match k:
    case False{}:
      imp_refl(R.ok(R.Raid{c, p, b}))
    case True{}:
      %le_refl(p) : {WK.imp(R.ok(R.Raid{c, p, b}), Bool.and(Bool.and(_, Nat.is_le(p, 1n+p)), Nat.is_le(p, c))) == True{} : Bool}
      %le_succ(p) : {WK.imp(R.ok(R.Raid{c, p, b}), Bool.and(Bool.and(True{}, _), Nat.is_le(p, c))) == True{} : Bool}
      imp_and3(Nat.is_le(b, p), Nat.is_le(p, 1n+b), Nat.is_le(p, c))

law adv_ok:
  for +c: Nat
  for +p: Nat
  for +b: Nat
  for +k: Bool
  for +h: {WK.imp(k, R.can_advance(R.Raid{c, p, b})) == True{} : Bool}
  {WK.imp(R.ok(R.Raid{c, p, b}), R.ok(R.advance_if(k, R.Raid{c, p, b}))) == True{} : Bool}

def adv_ok(c, p, b, k, h):
  match k:
    case False{}:
      imp_refl(R.ok(R.Raid{c, p, b}))
    case True{}:
      %h : {WK.imp(Bool.and(_, R.ok(R.Raid{c, p, b})), R.ok(R.Raid{c, 1n+p, b})) == True{} : Bool}
      step_core(c, p, b)

law apply_ok:
  for +e: R.RaidEvent
  for +r: R.Raid
  {WK.imp(R.ok(r), R.ok(R.apply(e, r))) == True{} : Bool}

def apply_ok(e, r):
  match e r:
    case R.Step{False{}} _:
      imp_refl(R.ok(r))
    case R.Step{True{}} R.Raid{+c, +p, +b}:
      adv_ok(c, p, b, R.can_advance(R.Raid{c, p, b}), imp_refl(R.can_advance(R.Raid{c, p, b})))
    case R.Fight{False{}} _:
      imp_refl(R.ok(r))
    case R.Fight{True{}} R.Raid{+c, +p, +b}:
      fight_ok(c, p, b, R.fight_pending(R.Raid{c, p, b}))

law run_ok:
  for +es: List<&2, R.RaidEvent>
  for +r: R.Raid
  {WK.imp(R.ok(r), R.ok(R.run(es, r))) == True{} : Bool}

def run_ok(es, r):
  match es:
    case Nil{}:
      imp_refl(R.ok(r))
    case Con{+e, +rest}:
      imp_trans(R.ok(r), R.ok(R.apply(e, r)), R.ok(R.run(rest, R.apply(e, r))), apply_ok(e, r), run_ok(rest, R.apply(e, r)))

def L.raid_always_ok(count, es):
  match count:
    case 0n:
      run_ok(es, R.start(0n))
    case 1n+k:
      run_ok(es, R.start(1n+k))

def L.board_server_agrees_with_client(b):
  match b:
    case B.Linear{}:
      {==}
    case B.Custom{}:
      {==}
    case B.Standalone{}:
      {==}
    case B.Act3{}:
      {==}
    case B.Motormaster{}:
      {==}

def L.board_links_are_steps(b):
  match b:
    case B.Linear{}:
      {==}
    case B.Custom{}:
      {==}
    case B.Standalone{}:
      {==}
    case B.Act3{}:
      {==}
    case B.Motormaster{}:
      {==}

def L.board_all_reachable(b):
  match b:
    case B.Linear{}:
      {==}
    case B.Custom{}:
      {==}
    case B.Standalone{}:
      {==}
    case B.Act3{}:
      {==}
    case B.Motormaster{}:
      {==}

def L.board_walkable_count(b):
  match b:
    case B.Linear{}:
      {==}
    case B.Custom{}:
      {==}
    case B.Standalone{}:
      {==}
    case B.Act3{}:
      {==}
    case B.Motormaster{}:
      {==}

# ---- Ability assignment ----

law and_l:
  for +x: Bool
  for -y: Bool
  for +h: {Bool.and(x, y) == True{} : Bool}
  {x == True{} : Bool}

def and_l(x, y, h):
  match x:
    case False{}:
      h
    case True{}:
      {==}

law and_r:
  for +x: Bool
  for -y: Bool
  for +h: {Bool.and(x, y) == True{} : Bool}
  {y == True{} : Bool}

def and_r(x, y, h):
  match x:
    case False{}:
      ex_falso({y == True{} : Bool}, ft_absurd(h))
    case True{}:
      h

law pick_ok:
  for +c: Bool
  for +a: List<&2, String>
  for +b: List<&2, String>
  for +ha: {A.kit_ok(D.catalog(), a) == True{} : Bool}
  for +hb: {A.kit_ok(D.catalog(), b) == True{} : Bool}
  {A.kit_ok(D.catalog(), A.pick(c, a, b)) == True{} : Bool}

def pick_ok(c, a, b, ha, hb):
  match c:
    case True{}:
      ha
    case False{}:
      hb

# Whatever bot is looked up, its kit satisfies kit_ok when every branch of the table does.
law lookup_ok:
  for +table: List<&2, A.Kit>
  for +bid: String
  for +h: {A.table_ok(D.catalog(), table) == True{} : Bool}
  {A.kit_ok(D.catalog(), A.lookup(table, bid)) == True{} : Bool}

def lookup_ok(table, bid, h):
  match table:
    case Nil{}:
      {==}
    case Con{A.Kit{+k, +abilities}, +rest}:
      pick_ok(String.eq(k, bid), abilities, A.lookup(rest, bid), and_l(A.kit_ok(D.catalog(), abilities), A.table_ok(D.catalog(), rest), h), lookup_ok(rest, bid, and_r(A.kit_ok(D.catalog(), abilities), A.table_ok(D.catalog(), rest), h)))

# The shipped table passes, checked by computation over the generated data.
law kit_holds:
  for +bid: String
  {A.kit_ok(D.catalog(), L.kit(bid)) == True{} : Bool}

def kit_holds(bid):
  lookup_ok(D.kits(), bid, {==})

def L.abilities_exist(bid):
  and_l(A.all_known(D.catalog(), L.kit(bid)), A.distinct(L.kit(bid)), and_l(Bool.and(A.all_known(D.catalog(), L.kit(bid)), A.distinct(L.kit(bid))), A.all_sound(D.catalog(), L.kit(bid)), kit_holds(bid)))

def L.abilities_distinct(bid):
  and_r(A.all_known(D.catalog(), L.kit(bid)), A.distinct(L.kit(bid)), and_l(Bool.and(A.all_known(D.catalog(), L.kit(bid)), A.distinct(L.kit(bid))), A.all_sound(D.catalog(), L.kit(bid)), kit_holds(bid)))

def L.abilities_coherent(bid):
  and_r(Bool.and(A.all_known(D.catalog(), L.kit(bid)), A.distinct(L.kit(bid))), A.all_sound(D.catalog(), L.kit(bid)), kit_holds(bid))

law pick_carded:
  for +c: Bool
  for +a: List<&2, String>
  for +b: List<&2, String>
  for +ha: {A.all_carded(D.mags(), D.cards(), a) == True{} : Bool}
  for +hb: {A.all_carded(D.mags(), D.cards(), b) == True{} : Bool}
  {A.all_carded(D.mags(), D.cards(), A.pick(c, a, b)) == True{} : Bool}

def pick_carded(c, a, b, ha, hb):
  match c:
    case True{}:
      ha
    case False{}:
      hb

# Whatever bot is looked up, its kit is carded when every branch of the table is.
law lookup_carded:
  for +table: List<&2, A.Kit>
  for +bid: String
  for +h: {A.cards_table_ok(D.mags(), D.cards(), table) == True{} : Bool}
  {A.all_carded(D.mags(), D.cards(), A.lookup(table, bid)) == True{} : Bool}

def lookup_carded(table, bid, h):
  match table:
    case Nil{}:
      {==}
    case Con{A.Kit{+k, +abilities}, +rest}:
      pick_carded(String.eq(k, bid), abilities, A.lookup(rest, bid), and_l(A.all_carded(D.mags(), D.cards(), abilities), A.cards_table_ok(D.mags(), D.cards(), rest), h), lookup_carded(rest, bid, and_r(A.all_carded(D.mags(), D.cards(), abilities), A.cards_table_ok(D.mags(), D.cards(), rest), h)))

def L.abilities_carded(bid):
  lookup_carded(D.kits(), bid, {==})

def L.kits_name_roster_bots():
  {==}

def L.kits_name_each_bot_once():
  {==}

# ---- The story walk ----

def L.walk_open_fight_blocks_moves(b, n, s, c, cl, left):
  {==}

law imp_intro:
  for +x: Bool
  for -y: Bool
  for f: {x == True{} : Bool} -> {y == True{} : Bool}
  {WK.imp(x, y) == True{} : Bool}

def imp_intro(x, y, f):
  match x:
    case False{}:
      {==}
    case True{}:
      f({==})

law imp_elim:
  for +x: Bool
  for -y: Bool
  for +h: {WK.imp(x, y) == True{} : Bool}
  for +hx: {x == True{} : Bool}
  {y == True{} : Bool}

def imp_elim(x, y, h, hx):
  match x:
    case False{}:
      ex_falso({y == True{} : Bool}, ft_absurd(hx))
    case True{}:
      h

law or_intro_l:
  for +x: Bool
  for -y: Bool
  for +hx: {x == True{} : Bool}
  {Bool.or(x, y) == True{} : Bool}

def or_intro_l(x, y, hx):
  match x:
    case False{}:
      ex_falso({Bool.or(False{}, y) == True{} : Bool}, ft_absurd(hx))
    case True{}:
      {==}

law or_intro_r:
  for +x: Bool
  for -y: Bool
  for +hy: {y == True{} : Bool}
  {Bool.or(x, y) == True{} : Bool}

def or_intro_r(x, y, hy):
  match x:
    case False{}:
      hy
    case True{}:
      {==}

law imp_or_mono:
  for +a: Bool
  for -x: Bool
  for -y: Bool
  for +h: {WK.imp(x, y) == True{} : Bool}
  {WK.imp(Bool.or(a, x), Bool.or(a, y)) == True{} : Bool}

def imp_or_mono(a, x, y, h):
  match a:
    case True{}:
      {==}
    case False{}:
      h

law imp_or_elim:
  for +x: Bool
  for -y: Bool
  for -z: Bool
  for +hx: {WK.imp(x, z) == True{} : Bool}
  for +hy: {WK.imp(y, z) == True{} : Bool}
  {WK.imp(Bool.or(x, y), z) == True{} : Bool}

def imp_or_elim(x, y, z, hx, hy):
  match x:
    case False{}:
      hy
    case True{}:
      hx

law nat_eq_refl:
  for +a: Nat
  {Nat.is_eq(a, a) == True{} : Bool}

def nat_eq_refl(a):
  match a:
    case 0n:
      {==}
    case 1n+p:
      nat_eq_refl(p)

law nat_eq_sound:
  for +a: Nat
  for +b: Nat
  for +h: {Nat.is_eq(a, b) == True{} : Bool}
  {a == b : Nat}

def nat_eq_sound(a, b, h):
  match a b:
    case 0n 0n:
      {==}
    case 0n 1n+q:
      ex_falso({0n == 1n+q : Nat}, ft_absurd(h))
    case 1n+p 0n:
      ex_falso({1n+p == 0n : Nat}, ft_absurd(h))
    case 1n+(+p) 1n+(+q):
      %nat_eq_sound(p, q, h) : {1n+p == 1n+_ : Nat}
      {==}

law pos_eq_refl:
  for +p: B.Pos
  {B.Pos.eq(p, p) == True{} : Bool}

def pos_eq_refl(p):
  match p:
    case B.P{+x, +y}:
      %Equal.sym(Bool, Nat.is_eq(x, x), True{}, nat_eq_refl(x)) : {Bool.and(_, Nat.is_eq(y, y)) == True{} : Bool}
      nat_eq_refl(y)

law pos_eq_sound:
  for +a: B.Pos
  for +b: B.Pos
  for +h: {B.Pos.eq(a, b) == True{} : Bool}
  {a == b : B.Pos}

def pos_eq_sound(a, b, h):
  match a b:
    case B.P{+ax, +ay} B.P{+bx, +by}:
      %nat_eq_sound(ax, bx, and_l(Nat.is_eq(ax, bx), Nat.is_eq(ay, by), h)) : {B.P{ax, ay} == B.P{_, by} : B.Pos}
      %nat_eq_sound(ay, by, and_r(Nat.is_eq(ax, bx), Nat.is_eq(ay, by), h)) : {B.P{ax, ay} == B.P{ax, _} : B.Pos}
      {==}

law has_append_self:
  for +cl: List<&2, B.Pos>
  for +s: B.Pos
  {B.has(List.append(&2, B.Pos, cl, [s]), s) == True{} : Bool}

def has_append_self(cl, s):
  match cl:
    case Nil{}:
      or_intro_l(B.Pos.eq(s, s), False{}, pos_eq_refl(s))
    case Con{+h, +t}:
      or_intro_r(B.Pos.eq(h, s), B.has(List.append(&2, B.Pos, t, [s]), s), has_append_self(t, s))

law has_append_mono:
  for +cl: List<&2, B.Pos>
  for +s: B.Pos
  for +n: B.Pos
  {WK.imp(B.has(cl, n), B.has(List.append(&2, B.Pos, cl, [s]), n)) == True{} : Bool}

def has_append_mono(cl, s, n):
  match cl:
    case Nil{}:
      {==}
    case Con{+h, +t}:
      imp_or_mono(B.Pos.eq(h, n), B.has(t, n), B.has(List.append(&2, B.Pos, t, [s]), n), has_append_mono(t, s, n))

law keep_or_add_mono:
  for +k: Bool
  for +s: B.Pos
  for +cl: List<&2, B.Pos>
  for +n: B.Pos
  {WK.imp(B.has(cl, n), B.has(WK.keep_or_add(k, s, cl), n)) == True{} : Bool}

def keep_or_add_mono(k, s, cl, n):
  match k:
    case True{}:
      imp_refl(B.has(cl, n))
    case False{}:
      has_append_mono(cl, s, n)

law keep_or_add_self:
  for +k: Bool
  for +s: B.Pos
  for +cl: List<&2, B.Pos>
  for +e: {k == B.has(cl, s) : Bool}
  {B.has(WK.keep_or_add(k, s, cl), s) == True{} : Bool}

def keep_or_add_self(k, s, cl, e):
  match k:
    case True{}:
      Equal.sym(Bool, True{}, B.has(cl, s), e)
    case False{}:
      has_append_self(cl, s)

# The state after arriving is valid once the arrival flag implies the tile is cleared.
law arrive_bool:
  for +en: Bool
  for +h: Bool
  for +r: Bool
  for +hr: {WK.imp(r, h) == True{} : Bool}
  {Bool.and(WK.imp(Bool.and(en, Bool.not(h)), Bool.and(en, Bool.not(r))), WK.imp(r, h)) == True{} : Bool}

def arrive_bool(en, h, r, hr):
  match en h r:
    case False{} False{} False{}:
      {==}
    case False{} False{} True{}:
      hr
    case False{} True{} _:
      match r:
        case False{}:
          {==}
        case True{}:
          {==}
    case True{} False{} False{}:
      {==}
    case True{} False{} True{}:
      ex_falso({Bool.and(WK.imp(True{}, False{}), WK.imp(True{}, False{})) == True{} : Bool}, ft_absurd(hr))
    case True{} True{} False{}:
      {==}
    case True{} True{} True{}:
      {==}

# Re-entering a completed tile: it was cleared, so it stays cleared.
law revisit_cleared:
  for +b: B.Board
  for +n: B.Pos
  for +s: B.Pos
  for +c: Bool
  for +cl: List<&2, B.Pos>
  for +hc: {WK.imp(c, B.has(cl, s)) == True{} : Bool}
  for +h2: {Bool.and(c, B.Pos.eq(n, s)) == True{} : Bool}
  {B.has(WK.cleared_with_start(b, s, cl), n) == True{} : Bool}

def revisit_cleared(b, n, s, c, cl, hc, h2):
  %Equal.sym(B.Pos, n, s, pos_eq_sound(n, s, and_r(c, B.Pos.eq(n, s), h2))) : {B.has(WK.cleared_with_start(b, s, cl), _) == True{} : Bool}
  imp_elim(B.has(cl, s), B.has(WK.cleared_with_start(b, s, cl), s), keep_or_add_mono(Bool.or(WK.encounter(b, s), B.has(cl, s)), s, cl, s), imp_elim(c, B.has(cl, s), hc, and_l(c, B.Pos.eq(n, s), h2)))

law arrive_ok:
  for +b: B.Board
  for +n: B.Pos
  for +s: B.Pos
  for +c: Bool
  for +cl: List<&2, B.Pos>
  for +left: List<&2, B.Pos>
  for +hpre: {WK.ok(b, WK.W{s, False{}, c, cl, left}) == True{} : Bool}
  {WK.ok(b, WK.moved(True{}, b, n, WK.W{s, False{}, c, cl, left})) == True{} : Bool}

def arrive_ok(b, n, s, c, cl, left, hpre):
  arrive_bool(WK.encounter(b, n), B.has(WK.cleared_with_start(b, s, cl), n), Bool.or(B.has(cl, n), Bool.and(c, B.Pos.eq(n, s))), imp_or_elim(B.has(cl, n), Bool.and(c, B.Pos.eq(n, s)), B.has(WK.cleared_with_start(b, s, cl), n), keep_or_add_mono(Bool.or(WK.encounter(b, s), B.has(cl, s)), s, cl, n), imp_intro(Bool.and(c, B.Pos.eq(n, s)), B.has(WK.cleared_with_start(b, s, cl), n), h2 => revisit_cleared(b, n, s, c, cl, and_r(WK.imp(Bool.and(WK.encounter(b, s), Bool.not(B.has(cl, s))), False{}), WK.imp(c, B.has(cl, s)), hpre), h2))))

law moved_ok:
  for +k: Bool
  for +b: B.Board
  for +n: B.Pos
  for +s: B.Pos
  for +c: Bool
  for +cl: List<&2, B.Pos>
  for +left: List<&2, B.Pos>
  {WK.imp(WK.ok(b, WK.W{s, False{}, c, cl, left}), WK.ok(b, WK.moved(k, b, n, WK.W{s, False{}, c, cl, left}))) == True{} : Bool}

def moved_ok(k, b, n, s, c, cl, left):
  match k:
    case False{}:
      imp_refl(WK.ok(b, WK.W{s, False{}, c, cl, left}))
    case True{}:
      imp_intro(WK.ok(b, WK.W{s, False{}, c, cl, left}), WK.ok(b, WK.moved(True{}, b, n, WK.W{s, False{}, c, cl, left})), hpre => arrive_ok(b, n, s, c, cl, left, hpre))

law win_bool:
  for +x: Bool
  for +e: Bool
  {WK.imp(x, WK.ok_parts(e, True{}, False{}, True{})) == True{} : Bool}

def win_bool(x, e):
  match x e:
    case False{} _:
      {==}
    case True{} False{}:
      {==}
    case True{} True{}:
      {==}

law win_ok:
  for +b: B.Board
  for +s: B.Pos
  for +c: Bool
  for +cl: List<&2, B.Pos>
  for +left: List<&2, B.Pos>
  {WK.imp(WK.ok(b, WK.W{s, True{}, c, cl, left}), WK.ok(b, WK.win(WK.W{s, True{}, c, cl, left}))) == True{} : Bool}

def win_ok(b, s, c, cl, left):
  %Equal.sym(Bool, B.has(WK.keep_or_add(B.has(cl, s), s, cl), s), True{}, keep_or_add_self(B.has(cl, s), s, cl, {==})) : {WK.imp(WK.ok(b, WK.W{s, True{}, c, cl, left}), WK.ok_parts(WK.encounter(b, s), _, False{}, True{})) == True{} : Bool}
  win_bool(WK.ok(b, WK.W{s, True{}, c, cl, left}), WK.encounter(b, s))

law walk_apply_ok:
  for +b: B.Board
  for +e: WK.WalkEvent
  for +w: WK.Walk
  {WK.imp(WK.ok(b, w), WK.ok(b, WK.apply(b, e, w))) == True{} : Bool}

def walk_apply_ok(b, e, w):
  match e w:
    case WK.Go{+n} WK.W{+s, True{}, +c, +cl, +left}:
      imp_refl(WK.ok(b, WK.W{s, True{}, c, cl, left}))
    case WK.Go{+n} WK.W{+s, False{}, +c, +cl, +left}:
      moved_ok(B.server_accepts(b, s, n), b, n, s, c, cl, left)
    case WK.Victory{} WK.W{+s, False{}, +c, +cl, +left}:
      imp_refl(WK.ok(b, WK.W{s, False{}, c, cl, left}))
    case WK.Victory{} WK.W{+s, True{}, +c, +cl, +left}:
      win_ok(b, s, c, cl, left)
    case WK.Defeat{} _:
      imp_refl(WK.ok(b, w))

law walk_run_ok:
  for +es: List<&2, WK.WalkEvent>
  for +b: B.Board
  for +w: WK.Walk
  {WK.imp(WK.ok(b, w), WK.ok(b, WK.run(es, b, w))) == True{} : Bool}

def walk_run_ok(es, b, w):
  match es:
    case Nil{}:
      imp_refl(WK.ok(b, w))
    case Con{+e, +rest}:
      imp_trans(WK.ok(b, w), WK.ok(b, WK.apply(b, e, w)), WK.ok(b, WK.run(rest, b, WK.apply(b, e, w))), walk_apply_ok(b, e, w), walk_run_ok(rest, b, WK.apply(b, e, w)))

law walk_start_ok:
  for +b: B.Board
  {WK.ok(b, WK.start(b)) == True{} : Bool}

def walk_start_ok(b):
  match b:
    case B.Linear{}:
      {==}
    case B.Custom{}:
      {==}
    case B.Standalone{}:
      {==}
    case B.Act3{}:
      {==}
    case B.Motormaster{}:
      {==}

law and_tt:
  for +x: Bool
  for +y: Bool
  for +hx: {x == True{} : Bool}
  for +hy: {y == True{} : Bool}
  {Bool.and(x, y) == True{} : Bool}

def and_tt(x, y, hx, hy):
  match x:
    case False{}:
      ex_falso({Bool.and(False{}, y) == True{} : Bool}, ft_absurd(hx))
    case True{}:
      hy

law imp_and_r:
  for +x: Bool
  for +y: Bool
  {WK.imp(Bool.and(x, y), y) == True{} : Bool}

def imp_and_r(x, y):
  match x y:
    case False{} _:
      {==}
    case True{} False{}:
      {==}
    case True{} True{}:
      {==}

law imp_and_both:
  for +x: Bool
  for +y: Bool
  for +x2: Bool
  for +y2: Bool
  for +hx: {WK.imp(x, x2) == True{} : Bool}
  for +hy: {WK.imp(Bool.and(x, y), y2) == True{} : Bool}
  {WK.imp(Bool.and(x, y), Bool.and(x2, y2)) == True{} : Bool}

def imp_and_both(x, y, x2, y2, hx, hy):
  match x y:
    case False{} _:
      {==}
    case True{} False{}:
      {==}
    case True{} True{}:
      and_tt(x2, y2, hx, hy)

law imp_imp_mono:
  for +e: Bool
  for +h: Bool
  for +h2: Bool
  for +hh: {WK.imp(h, h2) == True{} : Bool}
  {WK.imp(WK.imp(e, h), WK.imp(e, h2)) == True{} : Bool}

def imp_imp_mono(e, h, h2, hh):
  match e:
    case False{}:
      {==}
    case True{}:
      hh

law imp_pair:
  for +x: Bool
  for +y: Bool
  for +x2: Bool
  for +y2: Bool
  for +hx: {WK.imp(x, x2) == True{} : Bool}
  for +hy: {WK.imp(y, y2) == True{} : Bool}
  {WK.imp(Bool.and(x, y), Bool.and(x2, y2)) == True{} : Bool}

def imp_pair(x, y, x2, y2, hx, hy):
  match x y:
    case False{} _:
      {==}
    case True{} False{}:
      {==}
    case True{} True{}:
      and_tt(x2, y2, hx, hy)

law open_or_cleared:
  for +e: Bool
  for +h: Bool
  for +k: {WK.imp(Bool.and(e, Bool.not(h)), False{}) == True{} : Bool}
  {WK.imp(e, h) == True{} : Bool}

def open_or_cleared(e, h, k):
  match e h:
    case False{} _:
      {==}
    case True{} False{}:
      ex_falso({WK.imp(True{}, False{}) == True{} : Bool}, ft_absurd(k))
    case True{} True{}:
      {==}

# Clearing more tiles keeps every walked-off encounter cleared.
law all_ok_mono:
  for +b: B.Board
  for +ps: List<&2, B.Pos>
  for +cl: List<&2, B.Pos>
  for +k: Bool
  for +s: B.Pos
  {WK.imp(WK.all_ok(b, ps, cl), WK.all_ok(b, ps, WK.keep_or_add(k, s, cl))) == True{} : Bool}

def all_ok_mono(b, ps, cl, k, s):
  match ps:
    case Nil{}:
      {==}
    case Con{+p, +rest}:
      imp_pair(WK.imp(WK.encounter(b, p), B.has(cl, p)), WK.all_ok(b, rest, cl), WK.imp(WK.encounter(b, p), B.has(WK.keep_or_add(k, s, cl), p)), WK.all_ok(b, rest, WK.keep_or_add(k, s, cl)), imp_imp_mono(WK.encounter(b, p), B.has(cl, p), B.has(WK.keep_or_add(k, s, cl), p), keep_or_add_mono(k, s, cl, p)), all_ok_mono(b, rest, cl, k, s))

law all_ok_append:
  for +b: B.Board
  for +ps: List<&2, B.Pos>
  for +cl: List<&2, B.Pos>
  for +s: B.Pos
  for +h1: {WK.all_ok(b, ps, cl) == True{} : Bool}
  for +h2: {WK.imp(WK.encounter(b, s), B.has(cl, s)) == True{} : Bool}
  {WK.all_ok(b, List.append(&2, B.Pos, ps, [s]), cl) == True{} : Bool}

def all_ok_append(b, ps, cl, s, h1, h2):
  match ps:
    case Nil{}:
      and_tt(WK.imp(WK.encounter(b, s), B.has(cl, s)), True{}, h2, {==})
    case Con{+p, +rest}:
      and_tt(WK.imp(WK.encounter(b, p), B.has(cl, p)), WK.all_ok(b, List.append(&2, B.Pos, rest, [s]), cl), and_l(WK.imp(WK.encounter(b, p), B.has(cl, p)), WK.all_ok(b, rest, cl), h1), all_ok_append(b, rest, cl, s, and_r(WK.imp(WK.encounter(b, p), B.has(cl, p)), WK.all_ok(b, rest, cl), h1), h2))

# Walking off a tile: no fight is open there, so if it holds an encounter it is cleared.
law left_move_ok:
  for +b: B.Board
  for +n: B.Pos
  for +s: B.Pos
  for +c: Bool
  for +cl: List<&2, B.Pos>
  for +left: List<&2, B.Pos>
  for +h: {WK.inv(b, WK.W{s, False{}, c, cl, left}) == True{} : Bool}
  {WK.left_ok(b, WK.moved(True{}, b, n, WK.W{s, False{}, c, cl, left})) == True{} : Bool}

def left_move_ok(b, n, s, c, cl, left, h):
  all_ok_append(b, left, WK.cleared_with_start(b, s, cl), s, imp_elim(WK.all_ok(b, left, cl), WK.all_ok(b, left, WK.cleared_with_start(b, s, cl)), all_ok_mono(b, left, cl, Bool.or(WK.encounter(b, s), B.has(cl, s)), s), and_r(WK.ok(b, WK.W{s, False{}, c, cl, left}), WK.left_ok(b, WK.W{s, False{}, c, cl, left}), h)), imp_trans(WK.encounter(b, s), B.has(cl, s), B.has(WK.cleared_with_start(b, s, cl), s), open_or_cleared(WK.encounter(b, s), B.has(cl, s), and_l(WK.imp(Bool.and(WK.encounter(b, s), Bool.not(B.has(cl, s))), False{}), WK.imp(c, B.has(cl, s)), and_l(WK.ok(b, WK.W{s, False{}, c, cl, left}), WK.left_ok(b, WK.W{s, False{}, c, cl, left}), h))), keep_or_add_mono(Bool.or(WK.encounter(b, s), B.has(cl, s)), s, cl, s)))

law left_moved:
  for +k: Bool
  for +b: B.Board
  for +n: B.Pos
  for +s: B.Pos
  for +c: Bool
  for +cl: List<&2, B.Pos>
  for +left: List<&2, B.Pos>
  {WK.imp(WK.inv(b, WK.W{s, False{}, c, cl, left}), WK.left_ok(b, WK.moved(k, b, n, WK.W{s, False{}, c, cl, left}))) == True{} : Bool}

def left_moved(k, b, n, s, c, cl, left):
  match k:
    case False{}:
      imp_and_r(WK.ok(b, WK.W{s, False{}, c, cl, left}), WK.left_ok(b, WK.W{s, False{}, c, cl, left}))
    case True{}:
      imp_intro(WK.inv(b, WK.W{s, False{}, c, cl, left}), WK.left_ok(b, WK.moved(True{}, b, n, WK.W{s, False{}, c, cl, left})), h => left_move_ok(b, n, s, c, cl, left, h))

law walk_left_apply:
  for +b: B.Board
  for +e: WK.WalkEvent
  for +w: WK.Walk
  {WK.imp(WK.inv(b, w), WK.left_ok(b, WK.apply(b, e, w))) == True{} : Bool}

def walk_left_apply(b, e, w):
  match e w:
    case WK.Go{+n} WK.W{+s, True{}, +c, +cl, +left}:
      imp_and_r(WK.ok(b, WK.W{s, True{}, c, cl, left}), WK.left_ok(b, WK.W{s, True{}, c, cl, left}))
    case WK.Go{+n} WK.W{+s, False{}, +c, +cl, +left}:
      left_moved(B.server_accepts(b, s, n), b, n, s, c, cl, left)
    case WK.Victory{} WK.W{+s, False{}, +c, +cl, +left}:
      imp_and_r(WK.ok(b, WK.W{s, False{}, c, cl, left}), WK.left_ok(b, WK.W{s, False{}, c, cl, left}))
    case WK.Victory{} WK.W{+s, True{}, +c, +cl, +left}:
      imp_trans(WK.inv(b, WK.W{s, True{}, c, cl, left}), WK.all_ok(b, left, cl), WK.all_ok(b, left, WK.keep_or_add(B.has(cl, s), s, cl)), imp_and_r(WK.ok(b, WK.W{s, True{}, c, cl, left}), WK.left_ok(b, WK.W{s, True{}, c, cl, left})), all_ok_mono(b, left, cl, B.has(cl, s), s))
    case WK.Defeat{} _:
      imp_and_r(WK.ok(b, w), WK.left_ok(b, w))

law walk_inv_apply:
  for +b: B.Board
  for +e: WK.WalkEvent
  for +w: WK.Walk
  {WK.imp(WK.inv(b, w), WK.inv(b, WK.apply(b, e, w))) == True{} : Bool}

def walk_inv_apply(b, e, w):
  imp_and_both(WK.ok(b, w), WK.left_ok(b, w), WK.ok(b, WK.apply(b, e, w)), WK.left_ok(b, WK.apply(b, e, w)), imp_trans(WK.ok(b, w), WK.ok(b, w), WK.ok(b, WK.apply(b, e, w)), imp_refl(WK.ok(b, w)), walk_apply_ok(b, e, w)), walk_left_apply(b, e, w))

law walk_inv_run:
  for +es: List<&2, WK.WalkEvent>
  for +b: B.Board
  for +w: WK.Walk
  {WK.imp(WK.inv(b, w), WK.inv(b, WK.run(es, b, w))) == True{} : Bool}

def walk_inv_run(es, b, w):
  match es:
    case Nil{}:
      imp_refl(WK.inv(b, w))
    case Con{+e, +rest}:
      imp_trans(WK.inv(b, w), WK.inv(b, WK.apply(b, e, w)), WK.inv(b, WK.run(rest, b, WK.apply(b, e, w))), walk_inv_apply(b, e, w), walk_inv_run(rest, b, WK.apply(b, e, w)))

law walk_start_inv:
  for +b: B.Board
  {WK.inv(b, WK.start(b)) == True{} : Bool}

def walk_start_inv(b):
  match b:
    case B.Linear{}:
      {==}
    case B.Custom{}:
      {==}
    case B.Standalone{}:
      {==}
    case B.Act3{}:
      {==}
    case B.Motormaster{}:
      {==}

law walk_inv_reached:
  for +b: B.Board
  for +es: List<&2, WK.WalkEvent>
  {WK.inv(b, WK.run(es, b, WK.start(b))) == True{} : Bool}

def walk_inv_reached(b, es):
  imp_elim(WK.inv(b, WK.start(b)), WK.inv(b, WK.run(es, b, WK.start(b))), walk_inv_run(es, b, WK.start(b)), walk_start_inv(b))

def L.walk_never_skips_a_fight(b, es):
  and_r(WK.ok(b, WK.run(es, b, WK.start(b))), WK.left_ok(b, WK.run(es, b, WK.start(b))), walk_inv_reached(b, es))

def L.walk_unwon_encounter_is_open(b, es):
  and_l(WK.ok(b, WK.run(es, b, WK.start(b))), WK.left_ok(b, WK.run(es, b, WK.start(b))), walk_inv_reached(b, es))

# ---- Hero stats and specials ----

law le_zero:
  for +c: Nat
  {Nat.is_le(0n, c) == True{} : Bool}

def le_zero(c):
  match c:
    case 0n:
      {==}
    case 1n+q:
      {==}

law le_refl_f:
  for +p: Nat
  {Nat.is_le(p, p) == True{} : Bool}

def le_refl_f(p):
  Equal.sym(Bool, True{}, Nat.is_le(p, p), le_refl(p))

law le_succ_f:
  for +p: Nat
  {Nat.is_le(p, 1n+p) == True{} : Bool}

def le_succ_f(p):
  Equal.sym(Bool, True{}, Nat.is_le(p, 1n+p), le_succ(p))

law le_trans:
  for +a: Nat
  for +b: Nat
  for +c: Nat
  for +hab: {Nat.is_le(a, b) == True{} : Bool}
  for +hbc: {Nat.is_le(b, c) == True{} : Bool}
  {Nat.is_le(a, c) == True{} : Bool}

def le_trans(a, b, c, hab, hbc):
  match a b c:
    case 0n _ _:
      le_zero(c)
    case 1n+x 0n _:
      ex_falso({Nat.is_le(1n+x, c) == True{} : Bool}, ft_absurd(hab))
    case 1n+x 1n+y 0n:
      ex_falso({Nat.is_le(1n+x, 0n) == True{} : Bool}, ft_absurd(hbc))
    case 1n+x 1n+y 1n+z:
      le_trans(x, y, z, hab, hbc)

law le_add_front:
  for +a: Nat
  for +x: Nat
  {Nat.is_le(x, Nat.add(a, x)) == True{} : Bool}

def le_add_front(a, x):
  match a:
    case 0n:
      le_refl_f(x)
    case 1n+p:
      le_trans(x, Nat.add(p, x), 1n+Nat.add(p, x), le_add_front(p, x), le_succ_f(Nat.add(p, x)))

law add_mono_r:
  for +k: Nat
  for +x: Nat
  for +y: Nat
  for +h: {Nat.is_le(x, y) == True{} : Bool}
  {Nat.is_le(Nat.add(k, x), Nat.add(k, y)) == True{} : Bool}

def add_mono_r(k, x, y, h):
  match k:
    case 0n:
      h
    case 1n+p:
      add_mono_r(p, x, y, h)

law add_mono_l:
  for +x: Nat
  for +y: Nat
  for +k: Nat
  for +h: {Nat.is_le(x, y) == True{} : Bool}
  {Nat.is_le(Nat.add(x, k), Nat.add(y, k)) == True{} : Bool}

def add_mono_l(x, y, k, h):
  match x y:
    case 0n 0n:
      le_refl_f(k)
    case 0n 1n+q:
      le_trans(k, Nat.add(q, k), 1n+Nat.add(q, k), le_add_front(q, k), le_succ_f(Nat.add(q, k)))
    case 1n+p 0n:
      ex_falso({Nat.is_le(Nat.add(1n+p, k), Nat.add(0n, k)) == True{} : Bool}, ft_absurd(h))
    case 1n+p 1n+q:
      add_mono_l(p, q, k, h)

law mul_mono_r:
  for +a: Nat
  for +x: Nat
  for +y: Nat
  for +h: {Nat.is_le(x, y) == True{} : Bool}
  {Nat.is_le(Nat.mul(a, x), Nat.mul(a, y)) == True{} : Bool}

def mul_mono_r(a, x, y, h):
  match a:
    case 0n:
      {==}
    case 1n+p:
      le_trans(Nat.add(x, Nat.mul(p, x)), Nat.add(y, Nat.mul(p, x)), Nat.add(y, Nat.mul(p, y)), add_mono_l(x, y, Nat.mul(p, x), h), add_mono_r(y, Nat.mul(p, x), Nat.mul(p, y), mul_mono_r(p, x, y, h)))

law mul_mono_l:
  for +x: Nat
  for +y: Nat
  for +k: Nat
  for +h: {Nat.is_le(x, y) == True{} : Bool}
  {Nat.is_le(Nat.mul(x, k), Nat.mul(y, k)) == True{} : Bool}

def mul_mono_l(x, y, k, h):
  match x y:
    case 0n _:
      le_zero(Nat.mul(y, k))
    case 1n+p 0n:
      ex_falso({Nat.is_le(Nat.mul(1n+p, k), 0n) == True{} : Bool}, ft_absurd(h))
    case 1n+p 1n+q:
      add_mono_r(k, Nat.mul(p, k), Nat.mul(q, k), mul_mono_l(p, q, k, h))

law steps_succ:
  for +k: Nat
  {S.steps(1n+k) == k : Nat}

def steps_succ(k):
  match k:
    case 0n:
      {==}
    case 1n+q:
      {==}

def L.stats_level_never_weaker(r, l):
  match l:
    case 0n:
      le_refl_f(S.factor(r, 0n))
    case 1n+k:
      %Equal.sym(Nat, S.steps(1n+k), k, steps_succ(k)) : {Nat.is_le(Nat.mul(S.rank_num(r), Nat.add(40n, _)), S.factor(r, 2n+k)) == True{} : Bool}
      %Equal.sym(Nat, S.steps(2n+k), 1n+k, steps_succ(1n+k)) : {Nat.is_le(Nat.mul(S.rank_num(r), Nat.add(40n, k)), Nat.mul(S.rank_num(r), Nat.add(40n, _))) == True{} : Bool}
      mul_mono_r(S.rank_num(r), Nat.add(40n, k), Nat.add(40n, 1n+k), le_succ_f(k))

def L.stats_rank_never_weaker(r, l):
  match r:
    case 0n:
      le_refl_f(S.factor(0n, l))
    case 1n+k:
      %Equal.sym(Nat, S.steps(1n+k), k, steps_succ(k)) : {Nat.is_le(Nat.mul(Nat.add(20n, Nat.mul(7n, _)), S.level_num(l)), S.factor(2n+k, l)) == True{} : Bool}
      %Equal.sym(Nat, S.steps(2n+k), 1n+k, steps_succ(1n+k)) : {Nat.is_le(Nat.mul(Nat.add(20n, Nat.mul(7n, k)), S.level_num(l)), Nat.mul(Nat.add(20n, Nat.mul(7n, _)), S.level_num(l))) == True{} : Bool}
      mul_mono_l(Nat.add(20n, Nat.mul(7n, k)), Nat.add(20n, Nat.mul(7n, 1n+k)), S.level_num(l), add_mono_r(20n, Nat.mul(7n, k), Nat.mul(7n, 1n+k), mul_mono_r(7n, k, 1n+k, le_succ_f(k))))

def L.specials_one_per_bar():
  {==}

def L.specials_escalate():
  {==}

def L.specials_beat_a_hit():
  {==}
