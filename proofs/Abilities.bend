import Base

# Model of ability assignment (ABILITY_AUTHORING.md): the statMods catalog and the
# per-bot kit table bot_abilities in Server/gamedata.lbl. The data is in Data.bend,
# generated from the server's files by gen_data.py.

# ta: who receives the effect.
type Target is Data:
  OnSelf{}
  OnOpponent{}
  OnOwner{}
  OnTower{}
  OnOppTower{}

# mt: how the effect is classified and shown.
type ModType is Data:
  Buff{}
  Debuff{}
  Passive{}

# One statMods row: id, ta, mt and the effect class t.
type Row is Data:
  Row{id: String, ta: Target, mt: ModType, cls: String}

# One branch of bot_abilities: a bot and the ability ids it carries.
type Kit is Data:
  Kit{bid: String, abilities: List<&2, String>}

def pick(c: Bool, a: List<&2, String>, b: List<&2, String>) -> List<&2, String>:
  match c:
    case True{}:
      a
    case False{}:
      b

# bot_abilities(bid): the first branch naming the bot, no abilities otherwise.
def lookup(table: List<&2, Kit>, +bid: String) -> List<&2, String>:
  match table:
    case Nil{}:
      []
    case Con{Kit{k, abilities}, rest}:
      pick(String.eq(k, bid), abilities, lookup(rest, bid))

# The classification a row gives, when the catalog has the id.
def row_ok_if(hit: Bool, ok: Bool, rest: Bool) -> Bool:
  match hit:
    case True{}:
      ok
    case False{}:
      rest

# The four ta x mt cells that make sense for a bot's kit (ABILITY_AUTHORING.md,
# "Combinations that work"): harm the opponent, or buff, debuff or shape yourself.
def coherent(ta: Target, mt: ModType) -> Bool:
  match ta mt:
    case OnOpponent{} Debuff{}:
      True{}
    case OnSelf{} _:
      True{}
    case _ _:
      False{}

def is_opponent(ta: Target) -> Bool:
  match ta:
    case OnOpponent{}:
      True{}
    case _:
      False{}

# A damage effect (t = dmg_*) lands on the opponent as a debuff.
def damage_ok(+cls: String, +ta: Target, +mt: ModType) -> Bool:
  Bool.not(String.starts_with(cls, "dmg_")) || (coherent(ta, mt) && is_opponent(ta))

def row_sound(r: Row) -> Bool:
  match r:
    case Row{id, +ta, +mt, cls}:
      coherent(ta, mt) && damage_ok(cls, ta, mt)

# Whether the catalog has the id, and whether its row is sound for a kit.
def known(rows: List<&2, Row>, +id: String) -> Bool:
  match rows:
    case Nil{}:
      False{}
    case Con{Row{k, ta, mt, cls}, rest}:
      String.eq(k, id) || known(rest, id)

def row_is(r: Row, +id: String) -> Bool:
  match r:
    case Row{k, ta, mt, cls}:
      String.eq(k, id)

def sound_at(+r: Row, +id: String, rest: Bool) -> Bool:
  row_ok_if(row_is(r, id), row_sound(r), rest)

def sound(rows: List<&2, Row>, +id: String) -> Bool:
  match rows:
    case Nil{}:
      False{}
    case Con{+r, rest}:
      sound_at(r, id, sound(rest, id))

def has(xs: List<&2, String>, +s: String) -> Bool:
  match xs:
    case Nil{}:
      False{}
    case Con{h, t}:
      String.eq(h, s) || has(t, s)

def distinct(xs: List<&2, String>) -> Bool:
  match xs:
    case Nil{}:
      True{}
    case Con{+h, +t}:
      Bool.not(has(t, h)) && distinct(t)

def all_known(+rows: List<&2, Row>, xs: List<&2, String>) -> Bool:
  match xs:
    case Nil{}:
      True{}
    case Con{+h, t}:
      known(rows, h) && all_known(rows, t)

def all_sound(+rows: List<&2, Row>, xs: List<&2, String>) -> Bool:
  match xs:
    case Nil{}:
      True{}
    case Con{+h, t}:
      sound(rows, h) && all_sound(rows, t)

# Everything a kit must satisfy.
def kit_ok(+rows: List<&2, Row>, +xs: List<&2, String>) -> Bool:
  all_known(rows, xs) && distinct(xs) && all_sound(rows, xs)

def table_ok(+rows: List<&2, Row>, table: List<&2, Kit>) -> Bool:
  match table:
    case Nil{}:
      True{}
    case Con{Kit{k, +abilities}, rest}:
      kit_ok(rows, abilities) && table_ok(rows, rest)
