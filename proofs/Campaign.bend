import Base
import ./Abilities.bend as A

# Model of the verification campaign (ABILITY_AUTHORING.md, "Planned — the verification
# campaign"): the batches of probe rows in Server/data/campaign.json and the switch that
# serves one of them (bot_abilities and build_stat_modifiers in Server/gamedata.lbl).
# The data is in Data.bend, generated from the server's files by gen_data.py.

# One probe row of a batch: id, ta, mt, the effect class t, the triggers tr and the
# chance c in percent.
type Probe is Data:
  Probe{id: String, ta: A.Target, mt: A.ModType, cls: String, tr: List<&2, String>, chance: Nat}

# The one axis a batch varies (ABILITY_AUTHORING.md, "it is 43 + 11, not 43 x 11").
type Axis is Data:
  OnEffect{}
  OnTrigger{}

type Batch is Data:
  Batch{name: String, axis: Axis, probes: List<&2, Probe>}

def probe_id(p: Probe) -> String:
  match p:
    case Probe{id, ta, mt, cls, tr, chance}:
      id

def probe_ids(ps: List<&2, Probe>) -> List<&2, String>:
  match ps:
    case Nil{}:
      []
    case Con{p, rest}:
      Con{probe_id(p), probe_ids(rest)}

def probe_classes(ps: List<&2, Probe>) -> List<&2, String>:
  match ps:
    case Nil{}:
      []
    case Con{Probe{id, ta, mt, cls, tr, chance}, rest}:
      Con{cls, probe_classes(rest)}

def batch_name(b: Batch) -> String:
  match b:
    case Batch{name, axis, probes}:
      name

def batch_names(bs: List<&2, Batch>) -> List<&2, String>:
  match bs:
    case Nil{}:
      []
    case Con{b, rest}:
      Con{batch_name(b), batch_names(rest)}

def batch_probes(b: Batch) -> List<&2, Probe>:
  match b:
    case Batch{name, axis, probes}:
      probes

def pick_probes(c: Bool, a: List<&2, Probe>, b: List<&2, Probe>) -> List<&2, Probe>:
  match c:
    case True{}:
      a
    case False{}:
      b

# The probes of the batch with this name; none when no batch has it.
def probes_named(bs: List<&2, Batch>, +name: String) -> List<&2, Probe>:
  match bs:
    case Nil{}:
      []
    case Con{Batch{k, axis, probes}, rest}:
      pick_probes(String.eq(k, name), probes, probes_named(rest, name))

def all_probe_ids(bs: List<&2, Batch>) -> List<&2, String>:
  match bs:
    case Nil{}:
      []
    case Con{b, rest}:
      List.append(&2, String, probe_ids(batch_probes(b)), all_probe_ids(rest))

# ---- What a bot carries (bot_abilities in Server/gamedata.lbl) ----

# With no batch active a bot carries its kit. With one active, the probe bot carries the
# batch and every other bot carries nothing, so whatever registers is attributable.
def carried(kits: List<&2, A.Kit>, bs: List<&2, Batch>, +bot: String, +active: String, +bid: String) -> List<&2, String>:
  A.pick(String.eq(active, ""), A.lookup(kits, bid), A.pick(String.eq(bid, bot), probe_ids(probes_named(bs, active)), []))

# What a bot other than the probe bot carries while a batch is active (nothing, by law).
def stray(kits: List<&2, A.Kit>, bs: List<&2, Batch>, +bot: String, +active: String, +bid: String) -> List<&2, String>:
  A.pick(String.eq(active, ""), [], A.pick(String.eq(bid, bot), [], carried(kits, bs, bot, active, bid)))

# ---- What a batch must be ----

# The fighting-ability classes that poison every other result in a fight: disable_sp*
# blocks the specials other effects need to fire. Never put one in a mixed batch.
def interfering() -> List<&2, String>:
  ["disable_sp1", "disable_sp2", "disable_sp3", "disable_run", "state_disable", "swap_ai", "ai_rage_mod"]

# The proven trigger: one trigger, onHit, at chance 100%.
def control(tr: List<&2, String>, +chance: Nat) -> Bool:
  match tr:
    case Con{h, Nil{}}:
      String.eq(h, "onHit") && Nat.is_eq(chance, 100n)
    case _:
      False{}

def single(tr: List<&2, String>) -> Bool:
  match tr:
    case Con{h, Nil{}}:
      True{}
    case _:
      False{}

def probe_row(p: Probe) -> A.Row:
  match p:
    case Probe{id, ta, mt, cls, tr, chance}:
      A.Row{id, ta, mt, cls}

def all_rows_sound(ps: List<&2, Probe>) -> Bool:
  match ps:
    case Nil{}:
      True{}
    case Con{p, rest}:
      A.row_sound(probe_row(p)) && all_rows_sound(rest)

# An effect batch tests unverified classes, one per row, all on the proven trigger.
def effect_probe(+verified: List<&2, String>, p: Probe) -> Bool:
  match p:
    case Probe{id, ta, mt, +cls, tr, +chance}:
      control(tr, chance) && Bool.not(A.has(verified, cls))

# A trigger batch tests one trigger per row, all with a verified class.
def trigger_probe(+verified: List<&2, String>, p: Probe) -> Bool:
  match p:
    case Probe{id, ta, mt, +cls, tr, chance}:
      single(tr) && A.has(verified, cls)

def all_effect_probes(+verified: List<&2, String>, ps: List<&2, Probe>) -> Bool:
  match ps:
    case Nil{}:
      True{}
    case Con{p, rest}:
      effect_probe(verified, p) && all_effect_probes(verified, rest)

def all_trigger_probes(+verified: List<&2, String>, ps: List<&2, Probe>) -> Bool:
  match ps:
    case Nil{}:
      True{}
    case Con{p, rest}:
      trigger_probe(verified, p) && all_trigger_probes(verified, rest)

def probe_triggers(ps: List<&2, Probe>) -> List<&2, String>:
  match ps:
    case Nil{}:
      []
    case Con{Probe{id, ta, mt, cls, tr, chance}, rest}:
      List.append(&2, String, tr, probe_triggers(rest))

def one_axis(+verified: List<&2, String>, b: Batch) -> Bool:
  match b:
    case Batch{name, OnEffect{}, +probes}:
      all_effect_probes(verified, probes) && A.distinct(probe_classes(probes))
    case Batch{name, OnTrigger{}, +probes}:
      all_trigger_probes(verified, probes) && A.distinct(probe_triggers(probes))

def any_in(+pool: List<&2, String>, xs: List<&2, String>) -> Bool:
  match xs:
    case Nil{}:
      False{}
    case Con{h, t}:
      A.has(pool, h) || any_in(pool, t)

# A batch holding an interfering class holds nothing else.
def isolated(b: Batch) -> Bool:
  match b:
    case Batch{name, axis, +probes}:
      Bool.not(any_in(interfering(), probe_classes(probes))) || Nat.is_eq(List.length(&2, Probe, probes), 1n)

# T1 batches about ten rows per fight (ABILITY_AUTHORING.md, "Tier what verified means").
def fits(b: Batch) -> Bool:
  match b:
    case Batch{name, axis, +probes}:
      Nat.is_le(1n, List.length(&2, Probe, probes)) && Nat.is_le(List.length(&2, Probe, probes), 10n)

def none_known(+rows: List<&2, A.Row>, xs: List<&2, String>) -> Bool:
  match xs:
    case Nil{}:
      True{}
    case Con{h, t}:
      Bool.not(A.known(rows, h)) && none_known(rows, t)

def all_fit(bs: List<&2, Batch>) -> Bool:
  match bs:
    case Nil{}:
      True{}
    case Con{b, rest}:
      fits(b) && all_fit(rest)

def all_isolated(bs: List<&2, Batch>) -> Bool:
  match bs:
    case Nil{}:
      True{}
    case Con{b, rest}:
      isolated(b) && all_isolated(rest)

def all_one_axis(+verified: List<&2, String>, bs: List<&2, Batch>) -> Bool:
  match bs:
    case Nil{}:
      True{}
    case Con{b, rest}:
      one_axis(verified, b) && all_one_axis(verified, rest)

def all_batches_sound(bs: List<&2, Batch>) -> Bool:
  match bs:
    case Nil{}:
      True{}
    case Con{b, rest}:
      all_rows_sound(batch_probes(b)) && all_batches_sound(rest)

# ---- Shipped kits carry only verified effects ----

def pick_text(c: Bool, a: String, b: String) -> String:
  match c:
    case True{}:
      a
    case False{}:
      b

def class_of(rows: List<&2, A.Row>, +id: String) -> String:
  match rows:
    case Nil{}:
      ""
    case Con{A.Row{k, ta, mt, cls}, rest}:
      pick_text(String.eq(k, id), cls, class_of(rest, id))

def all_verified(+rows: List<&2, A.Row>, +verified: List<&2, String>, xs: List<&2, String>) -> Bool:
  match xs:
    case Nil{}:
      True{}
    case Con{+h, t}:
      A.has(verified, class_of(rows, h)) && all_verified(rows, verified, t)

def verified_table(+rows: List<&2, A.Row>, +verified: List<&2, String>, table: List<&2, A.Kit>) -> Bool:
  match table:
    case Nil{}:
      True{}
    case Con{A.Kit{k, +abilities}, rest}:
      all_verified(rows, verified, abilities) && verified_table(rows, verified, rest)
