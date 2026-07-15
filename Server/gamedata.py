#!/usr/bin/env python3
"""
Authored offline game data for the TFTF revival.

WHAT THIS IS
------------
This module is the single, hand-authored source of the server-side content that
Kabam used to stream to the client and that shut down with their servers in 2020.
None of the original balance data survived, so everything here is ORIGINAL work:
the class/faction assignments, the star rarities, every stat number, and the whole
combat balance table were invented for this offline revival, not copied from Kabam.

It only produces DATA in the JSON shapes the unmodified client already parses (the
same shapes proven to work in Server/responses/). It contains no game assets, no
copyrighted binaries, and no recovered Kabam data -- it references asset IDs that
already ship inside the user's own copy of the APK, and fills them with fresh numbers.

WHY IT IS SEPARATE
------------------
Keeping the content here (instead of inline in fakeserver.py or scattered across
hand-edited JSON files) makes the roster and balance easy to extend the way the
README describes: add a row, run `python gamedata.py`, verify against the client,
repeat. `build_responses()` regenerates the roster-driven response files from this
one source so they can never drift out of sync.

The character id list is taken from re_notes/ASSET_INVENTORY.txt -- these are the
bundles that already ship in the app, so their art exists; only the numbers were
missing. See COMPLIANCE.md for the full rationale.
"""
import json
import os

HERE = os.path.dirname(os.path.abspath(__file__))
RESP_DIR = os.path.join(HERE, "responses")

# The six combat classes are a factual part of the game's structure. Which class a
# given bot belongs to on Kabam's servers is lost data, so the assignments below are
# an original, self-consistent reconstruction, not the historical values.
CLASSES = ("braw", "tact", "scou", "demo", "warr", "tech")

# CDN host the working login-data response points at. Kept identical to the proven
# Server/responses/GET__bcg_getLoginData.json so asset resolution behaves the same.
CDN = "https://tform-0901-hzlhiniyfcwf.tf-cdn.net"

# ---------------------------------------------------------------------------
# Roster.  id -> (faction, class, star)
#   faction : "autobot" | "decepticon"  (maps to the `gen` field)
#   class   : one of CLASSES
#   star    : 1..5 rarity, drives the authored stat curve below
#
# The ids are the asset-bundle names from ASSET_INVENTORY.txt. Faction is set to the
# character's well-known allegiance; class and star are an ORIGINAL assignment made
# for this revival so the roster is playable and internally balanced.
# ---------------------------------------------------------------------------
ROSTER = {
    # --- First-time-experience bots (intro fight: Optimus vs Starscream) ---
    # These variants reuse Optimus/Starscream assets and MUST stay present, because
    # the scripted intro fight looks up these exact blueprint ids (see TECHNICAL_NOTES).
    "fte_optimus_gs_t3":            ("autobot",    "tact", 3),
    "fte_stars_gs_t3":              ("decepticon", "warr", 3),

    # --- Autobots ---
    "arcee_gs_deluxe2014":          ("autobot",    "scou", 3),
    "blaster_gs_leader2016":        ("autobot",    "tech", 4),
    "bumblebee_cin_dotm":           ("autobot",    "scou", 3),
    "bumblebee_gs_kabam":           ("autobot",    "scou", 3),
    "cheetor_bw_transmetal":        ("autobot",    "scou", 4),
    "cliffjumper_gs_kabam":         ("autobot",    "warr", 2),
    "dinobot_bw_kabam":             ("autobot",    "warr", 4),
    "drift_cin_aoe":                ("autobot",    "tact", 4),
    "grimlock_gs_mp08":             ("autobot",    "braw", 5),
    "hotrod_cin_tlk":               ("autobot",    "warr", 4),
    "hound_cin_tlk":                ("autobot",    "demo", 3),
    "ironhide_cin_rotf":            ("autobot",    "tact", 3),
    "ironhide_gs_kabam":            ("autobot",    "tact", 3),
    "jazz_gs_twm05":                ("autobot",    "scou", 3),
    "jetfire_gs_leader2014":        ("autobot",    "tech", 4),
    "mirage_gs_deluxe2016":         ("autobot",    "scou", 3),
    "optimusprimal_bw_mp32":        ("autobot",    "braw", 5),
    "optimusprime_cin_tf":          ("autobot",    "tact", 5),
    "prowl_gs_deluxe2016":          ("autobot",    "tact", 3),
    "ratchet_gs_kabam":             ("autobot",    "tech", 2),
    "rhinox_gs_voyager2014":        ("autobot",    "braw", 3),
    "rodimusprime_gs_mp09":         ("autobot",    "warr", 4),
    "sideswipe_gs":                 ("autobot",    "warr", 3),
    "sunstreaker_gs_deluxe2008":    ("autobot",    "warr", 3),
    "ultramagnus_gs_leader":        ("autobot",    "braw", 4),
    "wheeljack_gs_mp20":            ("autobot",    "tech", 3),
    "windblade_gs":                 ("autobot",    "scou", 4),

    # --- Decepticons ---
    "barricade_cin_dotm":           ("decepticon", "scou", 3),
    "bludgeon_gs_rd20":             ("decepticon", "warr", 4),
    "bonecrusher_cin_rotf":         ("decepticon", "demo", 3),
    "cyclonus_gs_uw06":             ("decepticon", "warr", 3),
    "dirge_gs_deluxe2008":          ("decepticon", "warr", 2),
    "galvatron_gs_voyager2016":     ("decepticon", "braw", 5),
    "grindor_cin_rotf":             ("decepticon", "demo", 3),
    "kickback_gs_kabam":            ("decepticon", "scou", 2),
    "megatron_cin_rotf":            ("decepticon", "braw", 5),
    "megatron_gs_leader2015":       ("decepticon", "braw", 5),
    "megatronus_gs_kabam":          ("decepticon", "tact", 5),
    "mixmaster_cin_rotf":           ("decepticon", "demo", 3),
    "motormaster_gs_voyager2015":   ("decepticon", "braw", 4),
    "necrotronus_gs_kabam":         ("decepticon", "demo", 4),
    "nemesisprime_gs_voyager2015":  ("decepticon", "tact", 4),
    "ramjet_gs_deluxe2008":         ("decepticon", "warr", 2),
    "scorponok_bw_kabam":           ("decepticon", "tech", 3),
    "shockwave_gs":                 ("decepticon", "tech", 4),
    "skywarp_gs_leader2015":        ("decepticon", "warr", 4),
    "slipstream_gs":                ("decepticon", "tact", 3),
    "soundblaster_gs_mp13b":        ("decepticon", "tech", 4),
    "soundwave_gs":                 ("decepticon", "tech", 4),
    "thundercracker_gs_leader2015": ("decepticon", "warr", 4),
    "tantrum_gs_kabam":             ("decepticon", "braw", 2),
    "waspinator_gs_deluxe":         ("decepticon", "tech", 2),

    # --- Sharkticons (generic enemy fodder; low rarity) ---
    "sharkticon_gs_kabam":          ("decepticon", "braw", 1),
    "sharkticon_gs_brawler":        ("decepticon", "braw", 1),
    "sharkticon_gs_demolition":     ("decepticon", "demo", 1),
    "sharkticon_gs_scout":          ("decepticon", "scou", 1),
    "sharkticon_gs_tactician":      ("decepticon", "tact", 1),
    "sharkticon_gs_tech":           ("decepticon", "tech", 1),
    "sharkticon_gs_warrior":        ("decepticon", "warr", 1),
}

# Which bots the offline player owns at boot. For a preservation sandbox we grant the
# full roster so every screen (roster grid, hero details, team select) has content.
# The list is authored; trim it if you want a more faithful "fresh account" feel.
OWNED = [bid for bid in ROSTER if not bid.startswith("sharkticon_")]

# ---------------------------------------------------------------------------
# Authored stat curve.  All ORIGINAL, all invented for this revival.
# ---------------------------------------------------------------------------
# Per-class flavour: brawlers tanky, warriors hit hard, scouts glassy, etc.
# (multipliers applied on top of the star base). Original balance.
_CLASS_MOD = {
    "braw": (1.25, 0.90),  # (hp_mult, atk_mult)
    "tact": (1.00, 1.00),
    "scou": (0.85, 1.15),
    "demo": (1.10, 1.05),
    "warr": (0.95, 1.20),
    "tech": (1.05, 0.95),
}

# Star rarity base line (level 1, rank 1). Original.
_STAR_BASE = {
    1: (2200, 210),
    2: (3400, 300),
    3: (5200, 430),
    4: (7800, 610),
    5: (11500, 850),
}


def base_stats(bid, rank=1, level=1):
    """Authored HP/attack for a bot at a given rank/level. Pure, deterministic,
    and original. Higher rank and level scale monotonically so upgrades feel real."""
    faction, klass, star = ROSTER.get(bid, ("decepticon", "tact", 1))
    hp0, atk0 = _STAR_BASE.get(star, _STAR_BASE[1])
    hpm, atkm = _CLASS_MOD.get(klass, (1.0, 1.0))
    # rank multiplies, level adds a per-level slice (~9% of base per 10 levels).
    rank_mult = 1.0 + 0.35 * (max(1, rank) - 1)
    level_add = (max(1, level) - 1) / 100.0
    hp = int(hp0 * hpm * rank_mult * (1.0 + level_add))
    atk = int(atk0 * atkm * rank_mult * (1.0 + level_add))
    return hp, atk


# msa values from the proven Server/responses/GET__bcg_getLoginData.json. The intro
# fight was verified working with these exact numbers, so we preserve them rather than
# let the authored curve change proven-working entries.
_MSA_OVERRIDE = {
    "bumblebee_gs_kabam": 1,
    "fte_optimus_gs_t3": 3,
    "fte_stars_gs_t3": 3,
}


def max_special_attacks(bid, star):
    """How many special attacks a bot can charge. Proven entries keep their verified
    value; everything else uses the authored rarity curve."""
    if bid in _MSA_OVERRIDE:
        return _MSA_OVERRIDE[bid]
    return 1 if star <= 1 else (2 if star <= 3 else 3)


# ---------------------------------------------------------------------------
# JSON builders -- emit the exact proven shapes from Server/responses/.
# ---------------------------------------------------------------------------
def build_blueprints():
    """`blueprints` map for getLoginData. Same keys as the working response:
    id, et(entity type -- MUST be 'bot' for the roster to populate), s1/s2/s3
    (special-attack damage ratios), msa (max special attacks), ab (attack bonus),
    plus the gg/mfl/nfr/fcpg/fhpag fields the client reads."""
    out = {}
    for bid, (faction, klass, star) in ROSTER.items():
        out[bid] = {
            "id": bid, "et": "bot",
            "s1": 1.0, "s2": 1.0, "s3": 1.0,
            "msa": max_special_attacks(bid, star),
            "ab": 100.0, "gg": 1, "mfl": 0, "nfr": 0,
            "fcpg": "", "fhpag": "",
        }
    return out


def build_characters():
    """`characters` map for getLoginData. Keys id, sg, gen(faction), aip, sps --
    identical to the proven response."""
    out = {}
    for bid, (faction, klass, star) in ROSTER.items():
        out[bid] = {"id": bid, "sg": "", "gen": faction, "aip": "", "sps": ""}
    return out


def build_attack_values():
    """`attackValues` combat balance table. The proven response ships a single
    'default' entry with keys a/m/c/d/p; the client falls back to it for any move
    without a specific entry, so 'default' is what actually drives damage offline.

    We keep 'default' and add authored per-class rows (same shape) so the balance is
    documented and tunable. Every number here is ORIGINAL -- Kabam's real attackValues
    table was server-side and is gone (see TECHNICAL_NOTES 'Why combat is the wall')."""
    def av(a, m, c, d, p):
        # a: base attack multiplier   m: special-move multiplier
        # c: crit chance (0..1)        d: crit damage multiplier
        # p: penetration / chip factor
        return {"a": a, "m": m, "c": c, "d": d, "p": p}

    table = {"default": av(1.0, 1.0, 0.05, 1.5, 0.0)}
    # Authored class flavour for combat, mirroring the stat-curve intent.
    table["braw"] = av(1.00, 1.05, 0.04, 1.5, 0.05)
    table["tact"] = av(1.00, 1.00, 0.05, 1.5, 0.00)
    table["scou"] = av(1.05, 1.00, 0.10, 1.6, 0.00)
    table["demo"] = av(1.00, 1.10, 0.05, 1.5, 0.08)
    table["warr"] = av(1.10, 1.05, 0.06, 1.7, 0.00)
    table["tech"] = av(0.98, 1.00, 0.05, 1.5, 0.10)
    return table


_CLASS_NAMES = {
    "braw": "Brawler", "tact": "Tactician", "scou": "Scout",
    "demo": "Demolitions", "warr": "Warrior", "tech": "Tech",
}

# Rock-paper-scissors: each class is strong against the next in this ring and weak
# to the previous one. ORIGINAL assignment for this revival. Used to fill the
# IdealContender ("who this class beats") list in heroClasses.
_CLASS_RING = ("braw", "tact", "scou", "demo", "warr", "tech")


def build_hero_classes():
    """`heroClasses` map for getLoginData -> BCGManagerBase.HeroClassesData.
    The roster tile reads a bot's class metadata (frame/icon + attack bonus) from
    here; when this map is empty the class lookup yields nothing. Keyed by class id.
    Exact server short-codes for the inner fields are unknown, so we emit several
    aliases (the client's JSON parser ignores keys it doesn't recognise)."""
    out = {}
    for i, cid in enumerate(_CLASS_RING):
        beats = _CLASS_RING[(i + 1) % len(_CLASS_RING)]
        name = _CLASS_NAMES[cid]
        out[cid] = {
            "id": cid, "cid": cid, "class_id": cid,
            "n": name, "name": name, "class_name": name,
            "ab": 1.1, "atkb": 1.1, "attack_bonus": 1.1,
            "atkp": 0.9, "attack_penalty": 0.9,
            "ic": [beats], "ideal_contender": [beats],
        }
    return out


def build_rarity_properties():
    """`rarityProperties` map for getLoginData -> RarityPropertiesData. The roster
    tile needs the rarity (star) entry for a bot to draw its star frame; an empty
    map leaves star-N bots without a rarity definition. Keyed by star as a string."""
    out = {}
    for star in range(1, 6):
        name = f"{star} Star"
        out[str(star)] = {
            "id": str(star), "n": name, "name": name,
            "mv": star, "map_value": star,
            "sp3qt": 0.5, "sp3_quicktime": 0.5,
            "ms": 99, "max_sig": 99,
        }
    return out


def build_login_data():
    """Full getLoginData result. Preserves every top-level key from the proven
    response and only enriches blueprints / characters / attackValues plus the
    previously-empty heroClasses / rarityProperties tuning maps."""
    return {
        "cdn": CDN,
        "sigLvlMax": 99,
        "ratingPrecision": 4,
        "heroRatingAttackWeight": 1.0,
        "heroRatingMaxHPWeight": 1.0,
        "attributeGrowthDefs": [],
        "statMods": {},
        "statModAppears": {},
        "heroes": {},
        "blueprints": build_blueprints(),
        "evoBlueprints": {},
        "characters": build_characters(),
        "synergyBonuses": {},
        "attackValues": build_attack_values(),
        "blueprintBonuses": {},
        "heroClasses": build_hero_classes(),
        "staminaRegen": {},
        "rarityProperties": build_rarity_properties(),
        "evoCosts": {},
        "curves": {},
    }


def build_hero_entry(bid, rank=1, level=1):
    """One owned-hero record for getUserData `updates.heroes`. Same keys as the
    proven single-hero response; entity_type MUST be 'bot'."""
    hp, atk = base_stats(bid, rank, level)
    return {
        "entity_type": "bot", "bid": bid,
        "rank": rank, "level": level, "sig_lvl": 0,
        "required_xp": 0, "max_xp": 100,
        "stamina": 100, "stamina_ts": 0, "stamina_full_ts": 0, "stt": "",
        "max_hp": hp, "attack": atk,
        "rating": (hp + atk) // 20,
        "rating_attack": atk // 2, "rating_hp": hp // 2,
        "rating_attack_base": atk // 2, "rating_hp_base": hp // 2,
        "special_attacks": 0, "pvpb": {}, "exc": {},
        "flvl": 0, "req_fxp": 0, "max_fxp": 0, "mfl": 0,
    }


def build_user_data():
    """Full getUserData result. userData maxes + owned heroes through `updates`,
    exactly as the proven response and the README/TECHNICAL_NOTES describe."""
    heroes = [build_hero_entry(bid) for bid in OWNED]
    return {
        "userData": {"blueprintsMax": 500, "teamSizeMax": 3, "teamCountMax": 5},
        "updates": {"heroes": heroes, "savedTeams": [], "activeTeams": []},
        "deletes": {},
    }


def build_base_hero_details(req_heroes):
    """Dynamic /bcg/getBaseHeroData reply: an ARRAY of computed hero details, one per
    requested hero, drawn from the authored stat curve so the numbers match the roster
    instead of the old crude ad-hoc curve in fakeserver."""
    out = []
    for h in (req_heroes or []):
        bid = h.get("bid", "")
        rank = int(h.get("rank", 1) or 1)
        level = int(h.get("level", 1) or 1)
        hp, atk = base_stats(bid, rank, level)
        out.append({
            "bid": bid, "rank": rank, "level": level,
            "sig_lvl": int(h.get("sig_lvl", 0) or 0),
            "rating_hp": hp, "max_hp": hp,
            "rating_attack": atk, "attack": atk,
            "health": hp, "armor": 0, "crit_rate": 0, "crit_dmg": 0,
            "block_prof": 0, "perfect_block": 0, "sig_ability": 0,
            "special_attacks": 0, "user_owned": True,
            "synergyBonuses": [], "pvpb": {},
        })
    return out


def build_responses():
    """Regenerate the roster-driven canned response files from this single source.
    Run this after editing ROSTER. Only touches the two roster files; the auth /
    account / tutorial responses are left as hand-tuned files."""
    env = lambda result: json.dumps({"error": None, "result": result},
                                     separators=(",", ":"))
    targets = {
        "GET__bcg_getLoginData.json": build_login_data(),
        "GET__bcg_getUserData.json": build_user_data(),
    }
    for fname, result in targets.items():
        path = os.path.join(RESP_DIR, fname)
        with open(path, "w", encoding="utf-8") as f:
            f.write(env(result))
        print(f"wrote {fname}  ({len(OWNED) if 'UserData' in fname else len(ROSTER)} entries)")


if __name__ == "__main__":
    build_responses()
    print(f"roster: {len(ROSTER)} bots, {len(OWNED)} owned")
