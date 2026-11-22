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

# ---- The kit table's bots (Server/gamedata.lbl roster) ----

def bids(table: List<&2, Kit>) -> List<&2, String>:
  match table:
    case Nil{}:
      []
    case Con{Kit{k, abilities}, rest}:
      Con{k, bids(rest)}

def all_in(+pool: List<&2, String>, xs: List<&2, String>) -> Bool:
  match xs:
    case Nil{}:
      True{}
    case Con{+h, t}:
      has(pool, h) && all_in(pool, t)

# ---- The ability card (Server/data/stat_mod_appears.json) ----

# One statModAppears record: the card the player and the glossary read. `icon` holds the
# codepoints of `t`, `nums` the whole numbers written in the description `l`, `says` the
# moments of a fight the description names ("hit", "special", "crit", "block").
type Card is Data:
  Card{id: String, title: String, text: String, icon: List<&2, Nat>, nums: List<&2, Nat>, says: List<&2, String>}

# What one statMods row does that a card may quote: m and c in percent, d in seconds
# (0 when the row has none, or lasts indefinitely), and the moments its triggers tr fire on.
type Mag is Data:
  Mag{id: String, pct: Nat, secs: Nat, chance: Nat, fires: List<&2, String>}

def has_nat(xs: List<&2, Nat>, +n: Nat) -> Bool:
  match xs:
    case Nil{}:
      False{}
    case Con{h, t}:
      Nat.is_eq(h, n) || has_nat(t, n)

def filled(+s: String) -> Bool:
  Bool.not(String.eq(s, ""))

# An effect icon is one glyph of the icon font, addressed by a Private Use Area codepoint
# (ABILITY_AUTHORING.md, "Effect icons are font glyphs"); any ASCII marker shows as garbage.
def glyph(icon: List<&2, Nat>) -> Bool:
  match icon:
    case Con{+cp, Nil{}}:
      Nat.is_le(57344n, cp) && Nat.is_le(cp, 63743n)
    case _:
      False{}

# Every number the card quotes is one the row has.
def quotes_row(xs: List<&2, Nat>, +pct: Nat, +secs: Nat, +chance: Nat) -> Bool:
  match xs:
    case Nil{}:
      True{}
    case Con{+h, t}:
      (Nat.is_eq(h, pct) || Nat.is_eq(h, secs) || Nat.is_eq(h, chance)) && quotes_row(t, pct, secs, chance)

# The card states the row's strength and duration, when the row has them.
def states(+xs: List<&2, Nat>, +n: Nat) -> Bool:
  Nat.is_eq(n, 0n) || has_nat(xs, n)

# Every number the card quotes is the row's, it states the row's strength and duration,
# and every moment it names is one the row fires on.
def honest_mag(+nums: List<&2, Nat>, +says: List<&2, String>, m: Mag) -> Bool:
  match m:
    case Mag{k, +pct, +secs, +chance, +fires}:
      quotes_row(nums, pct, secs, chance) && states(nums, pct) && states(nums, secs) && all_in(fires, says)

def mag_is(m: Mag, +id: String) -> Bool:
  match m:
    case Mag{k, pct, secs, chance, fires}:
      String.eq(k, id)

# Whether the row with this id is honestly described by the card.
def honest(mags: List<&2, Mag>, +id: String, +nums: List<&2, Nat>, +says: List<&2, String>) -> Bool:
  match mags:
    case Nil{}:
      False{}
    case Con{+m, rest}:
      row_ok_if(mag_is(m, id), honest_mag(nums, says, m), honest(rest, id, nums, says))

def card_sound(+mags: List<&2, Mag>, c: Card) -> Bool:
  match c:
    case Card{+k, +title, +text, icon, +nums, +says}:
      filled(title) && filled(text) && glyph(icon) && honest(mags, k, nums, says)

def card_is(c: Card, +id: String) -> Bool:
  match c:
    case Card{k, title, text, icon, nums, says}:
      String.eq(k, id)

# Whether the ability has a card, and the card is filled in and tells the truth.
def carded(+mags: List<&2, Mag>, cards: List<&2, Card>, +id: String) -> Bool:
  match cards:
    case Nil{}:
      False{}
    case Con{+c, rest}:
      row_ok_if(card_is(c, id), card_sound(mags, c), carded(mags, rest, id))

def all_carded(+mags: List<&2, Mag>, +cards: List<&2, Card>, xs: List<&2, String>) -> Bool:
  match xs:
    case Nil{}:
      True{}
    case Con{+h, t}:
      carded(mags, cards, h) && all_carded(mags, cards, t)

def cards_table_ok(+mags: List<&2, Mag>, +cards: List<&2, Card>, table: List<&2, Kit>) -> Bool:
  match table:
    case Nil{}:
      True{}
    case Con{Kit{k, +abilities}, rest}:
      all_carded(mags, cards, abilities) && cards_table_ok(mags, cards, rest)

