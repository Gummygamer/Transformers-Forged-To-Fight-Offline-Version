import json
import math
import importlib
import os
import unittest
from unittest import mock
from pathlib import Path

import gamedata


class ManaStartDiagnosticTests(unittest.TestCase):
    def test_default_mana_start_is_zero(self):
        with mock.patch.dict(os.environ, {}, clear=True):
            importlib.reload(gamedata)
            self.assertEqual(gamedata._DIAG_MANA_START, 0)
            self.assertEqual(
                gamedata.build_hero_base(gamedata.DEFAULT_TEAM[0])["mana_start"], 0
            )
        importlib.reload(gamedata)

    def test_mana_start_diagnostic_can_be_enabled(self):
        with mock.patch.dict(os.environ, {"TFTF_DIAG_MANA_START": "1"}):
            importlib.reload(gamedata)
            self.assertEqual(
                gamedata.build_hero_base(gamedata.DEFAULT_TEAM[0])["mana_start"], 1
            )
        # Do not leak the full-meter diagnostic setting into the rest of this test module.
        importlib.reload(gamedata)

    def test_combat_attribute_builders_carry_authored_mana_fields(self):
        entry = gamedata.build_hero_entry(gamedata.DEFAULT_TEAM[0])
        details = gamedata.build_base_hero_details([
            {"bid": gamedata.DEFAULT_TEAM[0]},
            {"bid": gamedata.DEFAULT_TEAM[1]},
        ])

        self.assertIsInstance(entry["mana_gain"], float)
        self.assertGreater(entry["mana_gain"], 0)
        self.assertEqual(entry["mana_start"], gamedata._DIAG_MANA_START)
        self.assertEqual(len(details), 2)
        for record in details:
            self.assertIsInstance(record["mana_gain"], float)
            self.assertGreater(record["mana_gain"], 0)
            self.assertEqual(record["mana_start"], gamedata._DIAG_MANA_START)

    def test_generated_user_data_has_mana_gain_for_every_owned_bot(self):
        response_path = Path(gamedata.RESP_DIR) / "GET__bcg_getUserData.json"
        response = json.loads(response_path.read_text(encoding="utf-8"))
        heroes = [
            hero for hero in response["result"]["updates"]["heroes"]
            if hero.get("entity_type") == "bot"
        ]

        self.assertTrue(heroes)
        for hero in heroes:
            self.assertIsInstance(hero["mana_gain"], float)
            self.assertGreater(hero["mana_gain"], 0)
            self.assertEqual(hero["mana_start"], gamedata._DIAG_MANA_START)


class AttackValuesTests(unittest.TestCase):
    def test_normal_attack_levels_have_nonzero_damage_rows(self):
        rows = gamedata.build_attack_values()
        mana_per_special = gamedata.build_missions_config()["configs"]["bcg-combat"][
            "manaPerSpecial"
        ]

        self.assertEqual(set(rows), {"Light", "Medium", "Heavy", "Ranged"})
        for attack_level, row in rows.items():
            self.assertEqual(row["id"], attack_level)
            self.assertGreater(row["a"], 0)
            self.assertGreaterEqual(row["m"], 10.0)
            self.assertEqual(set(row), {"id", "a", "m", "c", "d", "p"})
        self.assertGreater(rows["Heavy"]["m"], rows["Light"]["m"])
        self.assertLessEqual(mana_per_special / rows["Light"]["m"], 10)

    def test_generated_login_response_contains_current_attack_rows(self):
        response_path = Path(gamedata.RESP_DIR) / "GET__bcg_getLoginData.json"
        response = json.loads(response_path.read_text(encoding="utf-8"))

        self.assertEqual(
            response["result"]["attackValues"],
            gamedata.build_attack_values(),
        )


class HitStunStatModifierTests(unittest.TestCase):
    def test_gp_hit_stun_is_a_complete_harvested_stat_modifier_row(self):
        rows = gamedata.build_stat_modifiers()
        row = rows["gp_hit_stun"]

        self.assertEqual(set(rows), {"gp_hit_stun"})
        self.assertEqual(row["id"], "gp_hit_stun")
        self.assertEqual(
            set(row),
            {
                "id", "t", "tm", "tr", "uit", "pri", "trm", "trs", "trr",
                "c", "m", "d", "s", "ta", "mt", "v", "ms", "st", "g",
                "gc", "gcv", "rcv", "ti", "a", "au", "rh", "ra",
            },
        )
        self.assertEqual(row["t"], "hit_stun")
        self.assertEqual(row["ta"], "self")
        self.assertEqual(row["mt"], "debuff")
        self.assertEqual(row["tr"], [])
        self.assertEqual(row["uit"], [])
        self.assertEqual(gamedata.build_login_data()["statMods"], rows)


class TransformDamageTests(unittest.TestCase):
    def test_every_special_out_damages_every_normal_attack(self):
        """A transform (special attack) must hit harder than punches and kicks. The
        special ratios (s1/s2/s3) and normal-attack percents share one scale, so the
        weakest special must exceed the strongest normal attack."""
        attack_rows = gamedata.build_attack_values()
        strongest_normal = max(row["a"] for row in attack_rows.values())

        blueprints = gamedata.build_blueprints()
        self.assertTrue(blueprints)
        for bid, bp in blueprints.items():
            specials = (bp["s1"], bp["s2"], bp["s3"])
            self.assertGreater(
                min(specials), strongest_normal,
                msg=f"{bid} special weaker than a normal attack",
            )

    def test_specials_escalate_by_index(self):
        for bid in gamedata.ROSTER:
            bp = gamedata.build_blueprints()[bid]
            self.assertLess(bp["s1"], bp["s2"])
            self.assertLess(bp["s2"], bp["s3"])

    def test_generated_login_response_uses_current_special_ratios(self):
        response_path = Path(gamedata.RESP_DIR) / "GET__bcg_getLoginData.json"
        response = json.loads(response_path.read_text(encoding="utf-8"))
        blueprints = response["result"]["blueprints"]
        for bid, bp in gamedata.build_blueprints().items():
            self.assertEqual(blueprints[bid]["s1"], bp["s1"])
            self.assertEqual(blueprints[bid]["s2"], bp["s2"])
            self.assertEqual(blueprints[bid]["s3"], bp["s3"])


class ModelMeshTests(unittest.TestCase):
    """The combat actor loader mounts a bot's 3D mesh from BCGBlueprintBase.ModelID
    (wire key m/mdl). The mesh bundle is named by the FULL bid; the 2D portrait uses a
    SHORT art base. Truncating the model id (or omitting it) pointed at a missing bundle
    and forced every fighter to the same generic placeholder mesh."""

    def test_blueprint_model_id_matches_model_id_helper(self):
        # ModelID is the full bid, except a few variants that borrow a real mesh bundle.
        for bid, bp in gamedata.build_blueprints().items():
            self.assertEqual(bp["m"], gamedata.model_id(bid))
            self.assertEqual(bp["mdl"], gamedata.model_id(bid))

    def test_character_model_id_matches_model_id_helper(self):
        for bid, ch in gamedata.build_characters().items():
            self.assertEqual(ch["m"], gamedata.model_id(bid))
            self.assertEqual(ch["mdl"], gamedata.model_id(bid))

    def test_non_override_model_id_is_the_full_bid(self):
        for bid in gamedata.ROSTER:
            if bid in gamedata._MODEL_OVERRIDE:
                continue
            self.assertEqual(gamedata.model_id(bid), bid)

    def test_fte_optimus_uses_real_optimus_prime_mesh(self):
        # The intro fighter the player controls ("Optimus") has no fte_* bundle, so it must
        # borrow the real movie Optimus Prime mesh rather than the generic placeholder.
        self.assertEqual(
            gamedata.model_id("fte_optimus_gs_t3"), "optimusprime_cin_tf"
        )
        self.assertEqual(
            gamedata.build_blueprints()["fte_optimus_gs_t3"]["mdl"],
            "optimusprime_cin_tf",
        )

    def test_portrait_stays_short_art_base(self):
        # The portrait id is the short shipped art base: never the model id or naive prefix.
        blueprints = gamedata.build_blueprints()
        self.assertEqual(blueprints["optimusprime_cin_tf"]["img"], "optimus_c_tf")
        self.assertEqual(blueprints["optimusprime_cin_tf"]["mdl"], "optimusprime_cin_tf")

    def test_all_roster_portraits_ship_both_sizes(self):
        portraits_dir = (
            Path(__file__).resolve().parent.parent
            / "Transformers 9.2 extracted"
            / "assets/assetpack/portraits_odr/portraits"
        )
        if not portraits_dir.is_dir():
            self.skipTest("extracted APK portrait directory is not available")

        blueprints = gamedata.build_blueprints()
        characters = gamedata.build_characters()
        for bid in gamedata.ROSTER:
            base = gamedata.art_base(bid)
            for size in ("large", "small"):
                matches = list(portraits_dir.glob(f"portrait_{base}_{size}.*"))
                self.assertTrue(
                    matches,
                    f"{bid} resolves to {base}, which has no {size} portrait",
                )
            self.assertEqual(blueprints[bid]["img"], base, bid)
            self.assertEqual(characters[bid]["img"], base, bid)

    def test_art_base_overrides_cover_only_irregular_roster_bots(self):
        fallback_bots = {
            "arcee_gs_deluxe2014", "cheetor_bw_transmetal", "jazz_gs_twm05",
            "jetfire_gs_leader2014", "prowl_gs_deluxe2016", "dirge_gs_deluxe2008",
            "galvatron_gs_voyager2016", "ramjet_gs_deluxe2008",
            "scorponok_bw_kabam", "skywarp_gs_leader2015", "slipstream_gs",
            "tantrum_gs_kabam",
        }
        self.assertEqual(len(gamedata._ART_BASE), 49)
        self.assertTrue(set(gamedata._ART_BASE).issubset(gamedata.ROSTER))
        for bid, base in gamedata._ART_BASE.items():
            self.assertNotEqual(base, bid)
            self.assertNotEqual(base, "_".join(bid.split("_")[:2]))
        self.assertTrue(fallback_bots.isdisjoint(gamedata._ART_BASE))

    def test_player_lead_is_optimus_prime(self):
        # The bot the player controls on-screen (team lead, index 0) is Optimus Prime.
        self.assertEqual(gamedata.DEFAULT_TEAM[0], "optimusprime_cin_tf")

    def test_login_response_carries_model_ids(self):
        response_path = Path(gamedata.RESP_DIR) / "GET__bcg_getLoginData.json"
        response = json.loads(response_path.read_text(encoding="utf-8"))
        blueprints = response["result"]["blueprints"]
        for bid in gamedata.build_blueprints():
            self.assertEqual(blueprints[bid]["mdl"], gamedata.model_id(bid))


class MissionsConfigTests(unittest.TestCase):
    def test_combat_armor_rating_constant_is_finite_and_positive(self):
        """A zero input window makes HasAction false, so queued attacks never execute."""
        missions_config = gamedata.build_missions_config()

        self.assertEqual(missions_config["configsHash"], "offline-v1")
        self.assertEqual(
            set(missions_config["configs"]), {"bcg-combat", "user-base"}
        )
        armor_constant = missions_config["configs"]["bcg-combat"][
            "armorRatingConstant"
        ]
        self.assertTrue(math.isfinite(armor_constant))
        self.assertGreater(armor_constant, 0)

        mana_per_special = missions_config["configs"]["bcg-combat"]["manaPerSpecial"]
        self.assertTrue(math.isfinite(mana_per_special))
        self.assertGreater(mana_per_special, 0)

        combat_config = missions_config["configs"]["bcg-combat"]
        self.assertIn("maxQueuedActionTime", combat_config)
        max_queued_action_time = combat_config["maxQueuedActionTime"]
        self.assertIsInstance(max_queued_action_time, float)
        self.assertGreater(max_queued_action_time, 0)
        self.assertLess(max_queued_action_time, 0.5)

    def test_account_response_contains_current_missions_config(self):
        response_path = Path(gamedata.RESP_DIR) / "GET__account_data.json"
        response = json.loads(response_path.read_text(encoding="utf-8"))

        self.assertEqual(
            response["result"]["missionsconfig"],
            gamedata.build_missions_account_data(),
        )
        self.assertEqual(
            response["result"]["missionsconfig"]["missionsconfig"],
            gamedata.build_missions_config(),
        )

    def test_direct_autorefresh_repeats_current_missions_config(self):
        response_path = (
            Path(gamedata.RESP_DIR)
            / "GET__autorefresh_missionsconfig_refresh.json"
        )
        response = json.loads(response_path.read_text(encoding="utf-8"))

        self.assertEqual(
            response["result"],
            gamedata.build_missions_autorefresh_result(),
        )
        self.assertEqual(response["result"]["refresh"], 0)

    def test_group_autorefresh_update_has_exact_client_fields(self):
        update = gamedata.build_missions_autorefresh_update()

        self.assertEqual(
            set(update),
            {"name", "error", "check", "locHash", "refresh", "data", "cache"},
        )
        self.assertEqual(update["name"], "missionsconfig")
        self.assertEqual(update["data"], gamedata.build_missions_config())
        self.assertEqual(update["refresh"], 0)


class BaseActiveTests(unittest.TestCase):
    """/base/active -- the home screen's base board.

    These lock in the wire contract read out of the client (see the comments above
    gamedata.build_base_active): the four `user`-prefixed keys with NO dot separator,
    the three non-empty ActiveMission identity fields, and a Map that cannot make the
    board builder null-deref.
    """

    def test_keys_use_the_concatenated_user_prefix(self):
        result = gamedata.build_base_active()

        self.assertEqual(
            set(result),
            {"userBase", "userAvailableBuildings", "userBuildings", "userSockets"},
        )

    def test_mission_identity_fields_are_all_non_empty(self):
        mission = gamedata.build_base_active()["userBase"]

        # ActiveMission.Deserialize aborts the whole parse if any of these is empty.
        for key in ("mode", "category", "id"):
            self.assertTrue(mission[key], f"{key} must be non-empty")
        # A valid uid is what makes Base.get_type answer "users" (the player's base)
        # rather than "alliances".
        self.assertEqual(mission["uid"], gamedata.LOCAL_UID)

    def test_summary_theme_resolves_to_a_bundled_base_library(self):
        summary = gamedata.build_base_active()["userBase"]["data"]

        # The builder loads "library_" + theme + "_base"; primordial_base ships in the APK.
        self.assertEqual(summary["theme"], "primordial")

    def test_map_grid_is_square_and_has_exactly_one_start_tile(self):
        game_map = gamedata.build_base_active()["userBase"]["map"]
        dim = game_map["gridDimension"]

        self.assertEqual(len(game_map["grid"]), dim)
        for row in game_map["grid"]:
            self.assertEqual(len(row), dim)
        starts = [
            (r, c)
            for r, row in enumerate(game_map["grid"])
            for c, tile in enumerate(row)
            if tile.get("start")
        ]
        self.assertEqual(len(starts), 1)

    def test_map_has_path_data_so_the_path_analyzer_cannot_null_deref(self):
        game_map = gamedata.build_base_active()["userBase"]["map"]

        self.assertGreaterEqual(len(game_map["pathData"]), 1)
        for path in game_map["pathData"]:
            self.assertGreaterEqual(len(path["path"]), 1)

    def test_every_walkable_tile_offers_an_unlocked_building_socket(self):
        result = gamedata.build_base_active()
        game_map = result["userBase"]["map"]

        walkable = [
            tile
            for row in game_map["grid"]
            for tile in row
            if tile.get("walkable")
        ]
        self.assertEqual(len(walkable), game_map["walkableCount"])
        for tile in walkable:
            self.assertTrue(tile["sockets"])
            for socket in tile["sockets"].values():
                # The value's entityType becomes Socket.type, and tile.sockets is
                # keyed by TYPE -- "building" is what buildingSocket lookup wants.
                # (Socket.id comes from the dict entry key, not an `id` field.)
                self.assertEqual(socket["entityType"], "building")
            for socket_id in tile["sockets"]:
                # The per-player unlock overlay must know every authored socket.
                self.assertTrue(result["userSockets"][socket_id])

    def test_placements_reference_authored_sockets_and_buildings(self):
        result = gamedata.build_base_active()
        mission = result["userBase"]
        available = {b["id"] for b in result["userAvailableBuildings"]}
        socket_ids = {
            socket_id
            for row in mission["map"]["grid"]
            for tile in row
            for socket_id in tile.get("sockets", {})
        }

        self.assertTrue(mission["placements"])
        for socket_id, placement in mission["placements"].items():
            # The placements dict key must be a real tile socket id: the tile's
            # building is mission.placement.entities[tile.buildingSocket.id].
            self.assertIn(socket_id, socket_ids)
            # entityType "building" routes NewEntity to the Building branch, and
            # `key` (extraData) must name a catalogue entry to clone.
            self.assertEqual(placement["entityType"], "building")
            self.assertIn(placement["key"], available)

    def test_owned_buildings_come_from_the_available_catalogue(self):
        result = gamedata.build_base_active()
        available = {b["id"] for b in result["userAvailableBuildings"]}

        self.assertTrue(result["userBuildings"])
        for owned in result["userBuildings"]:
            # DeserializeOwnedBuildings drops (and logs an error for) any owned id
            # missing from availableBuildings -- it clones the catalogue entry.
            self.assertIn(owned["id"], available)
            self.assertIn(owned["key"], result["userSockets"])

    def test_available_buildings_use_shipped_model_ids(self):
        # modelId must be a child of library_buildings in buildings.assetbundle;
        # this is the complete shipped set (see the note at BASE_BUILDINGS).
        shipped = {
            "z_bldg_alliance_help_%02d" % i for i in range(4)
        } | {
            "z_bldg_away_team_%02d" % i for i in range(4)
        } | {
            "z_bldg_battle_centre_%02d" % i for i in range(4)
        } | {"z_bldg_gacha_daily_01", "z_bldg_gacha_free_01", "z_bldg_gacha_other_01"}

        buildings = gamedata.build_base_active()["userAvailableBuildings"]
        self.assertTrue(buildings)
        for building in buildings:
            self.assertEqual(building["entityType"], "building")
            self.assertIn(building["modelId"], shipped)
            # Building.Deserialize aborts unless the identity fields parse.
            self.assertTrue(building["id"])
            self.assertTrue(building["name"])

    def test_links_only_point_at_walkable_neighbours(self):
        game_map = gamedata.build_base_active()["userBase"]["map"]
        grid = game_map["grid"]

        for row in grid:
            for tile in row:
                for link in tile.get("links", []):
                    target = grid[link["x"]][link["y"]]
                    self.assertTrue(target.get("walkable"))


class StoryFightArenaTests(unittest.TestCase):
    def test_default_arena_is_not_the_black_karnak_fallback(self):
        """ARENA_LEVEL must not default to karnak, whose offline ground is black."""
        self.assertNotEqual(gamedata.ARENA_LEVEL, "karnak")
        self.assertIn(gamedata.ARENA_LEVEL, gamedata.ARENA_LEVELS)

    def test_quest_enemy_assigns_a_nonempty_shipped_arena_level(self):
        enemy = gamedata.build_quest_enemy()

        self.assertIsInstance(enemy["mapOverride"], str)
        self.assertTrue(enemy["mapOverride"])
        self.assertEqual(enemy["mapOverride"], gamedata.ARENA_LEVEL)
        self.assertIn(enemy["mapOverride"], gamedata.ARENA_LEVELS)

    def test_quest_enemy_tod_is_valid_for_its_arena_level(self):
        enemy = gamedata.build_quest_enemy()

        self.assertIsInstance(enemy["todIndex"], int)
        self.assertIn(enemy["todIndex"], gamedata.ARENA_LEVELS[enemy["mapOverride"]])

    def test_arena_environment_overrides_are_applied_on_reload(self):
        with mock.patch.dict(
            os.environ, {"TFTF_ARENA_LEVEL": "mine", "TFTF_ARENA_TOD": "2"}
        ):
            importlib.reload(gamedata)
            enemy = gamedata.build_quest_enemy()
            self.assertEqual(enemy["mapOverride"], "mine")
            self.assertEqual(enemy["todIndex"], 2)
        importlib.reload(gamedata)

    def test_invalid_arena_level_raises_on_reload(self):
        with mock.patch.dict(os.environ, {"TFTF_ARENA_LEVEL": "not-a-level"}):
            with self.assertRaises(ValueError):
                importlib.reload(gamedata)
        importlib.reload(gamedata)

    def test_invalid_arena_tod_raises_on_reload(self):
        with mock.patch.dict(
            os.environ, {"TFTF_ARENA_LEVEL": "mine", "TFTF_ARENA_TOD": "3"}
        ):
            with self.assertRaises(ValueError):
                importlib.reload(gamedata)
        importlib.reload(gamedata)

    def test_final_map_tile_carries_the_authored_arena_assignment(self):
        game_map = gamedata.build_quest_map()
        final_tiles = [
            tile
            for row in game_map["grid"]
            for tile in row
            if tile.get("final")
        ]

        self.assertEqual(len(final_tiles), 1)
        boss = final_tiles[0]["entities"][gamedata.BOSS_BLUEPRINT]
        self.assertTrue(boss["mapOverride"])
        self.assertEqual(boss["mapOverride"], gamedata.ARENA_LEVEL)
        self.assertEqual(boss["todIndex"], gamedata.ARENA_TOD_INDEX)

    def test_arena_inventory_matches_shipped_level_prefabs_when_available(self):
        assets_dir = (
            Path(__file__).resolve().parent.parent
            / "Transformers 9.2 extracted"
            / "assets"
        )
        if not assets_dir.is_dir():
            self.skipTest("user-owned extracted game assets are not available")

        for level, tod_indices in gamedata.ARENA_LEVELS.items():
            toc_path = assets_dir / level / "toc.txt"
            self.assertTrue(toc_path.is_file(), toc_path)
            toc = json.loads(toc_path.read_text(encoding="utf-8"))
            paths = toc["bundles"][f"{level}_merged"]["paths"]
            self.assertIn(f"{level}_merged/{level}_merged", paths)
            for tod_index in tod_indices:
                self.assertIn(
                    f"{level}_merged/{level}_timeofday_{tod_index}_forward", paths
                )


class SquadPropagationTests(unittest.TestCase):
    TEAM = ["grimlock_gs_mp08", "megatron_cin_rotf"]

    def test_quest_progression_uses_the_supplied_squad(self):
        progression = gamedata.build_quest_progression(team=self.TEAM)
        user = progression["users"][gamedata.LOCAL_UID]

        self.assertEqual(list(user["team"]), self.TEAM)
        self.assertEqual(user["strongestHero"], self.TEAM[0])

    def test_quest_begin_threads_the_squad_into_its_instance_progression(self):
        result = gamedata.build_quest_begin("1.1.1", "story_act1", self.TEAM)
        progression = result["activeQuests"]["1.1.1"]["instances"][0]["progression"]

        self.assertEqual(list(progression["users"][gamedata.LOCAL_UID]["team"]), self.TEAM)

    def test_quest_movedir_preserves_the_squad_in_progression_and_team_data(self):
        result = gamedata.build_quest_movedir(team=self.TEAM)

        self.assertEqual(
            list(result["progression"]["users"][gamedata.LOCAL_UID]["team"]), self.TEAM
        )
        self.assertEqual(list(result["teamData"]["heroes"]), self.TEAM)

    def test_user_data_uses_the_supplied_saved_and_active_team(self):
        result = gamedata.build_user_data(team=self.TEAM)
        updates = result["updates"]

        self.assertEqual([hero["bid"] for hero in updates["savedTeams"][0]["heroes"]], self.TEAM)
        self.assertEqual(list(updates["activeTeams"][0]["heroes"]), self.TEAM)

    def test_none_and_empty_team_preserve_default_json(self):
        builders = (
            gamedata.build_quest_progression,
            gamedata.build_active_quest,
            gamedata.build_quest_begin,
            gamedata.build_quest_movedir,
            gamedata.build_user_data,
        )
        for builder in builders:
            with self.subTest(builder=builder.__name__):
                default = json.dumps(builder(), separators=(",", ":"))
                self.assertEqual(json.dumps(builder(team=None), separators=(",", ":")), default)
                self.assertEqual(json.dumps(builder(team=[]), separators=(",", ":")), default)

    def test_unknown_blueprint_falls_back_to_the_default_squad(self):
        progression = gamedata.build_quest_progression(team=["not-a-blueprint"])
        user = progression["users"][gamedata.LOCAL_UID]

        self.assertEqual(list(user["team"]), gamedata.DEFAULT_TEAM[:2])
        self.assertEqual(user["strongestHero"], gamedata.DEFAULT_TEAM[0])


if __name__ == "__main__":
    unittest.main()
