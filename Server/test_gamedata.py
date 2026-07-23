import json
import math
import unittest
from pathlib import Path

import gamedata


class AttackValuesTests(unittest.TestCase):
    def test_normal_attack_levels_have_nonzero_damage_rows(self):
        rows = gamedata.build_attack_values()

        self.assertEqual(set(rows), {"Light", "Medium", "Heavy", "Ranged"})
        for attack_level, row in rows.items():
            self.assertEqual(row["id"], attack_level)
            self.assertGreater(row["a"], 0)
            self.assertEqual(set(row), {"id", "a", "m", "c", "d", "p"})

    def test_generated_login_response_contains_current_attack_rows(self):
        response_path = Path(gamedata.RESP_DIR) / "GET__bcg_getLoginData.json"
        response = json.loads(response_path.read_text(encoding="utf-8"))

        self.assertEqual(
            response["result"]["attackValues"],
            gamedata.build_attack_values(),
        )


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
        # The portrait id must remain the short base, not the model id, or portraits break.
        blueprints = gamedata.build_blueprints()
        self.assertEqual(blueprints["optimusprime_cin_tf"]["img"], "optimusprime_cin")
        self.assertEqual(blueprints["optimusprime_cin_tf"]["mdl"], "optimusprime_cin_tf")

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
        missions_config = gamedata.build_missions_config()

        self.assertEqual(missions_config["configsHash"], "offline-v1")
        self.assertEqual(set(missions_config["configs"]), {"bcg-combat"})
        armor_constant = missions_config["configs"]["bcg-combat"][
            "armorRatingConstant"
        ]
        self.assertTrue(math.isfinite(armor_constant))
        self.assertGreater(armor_constant, 0)

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


if __name__ == "__main__":
    unittest.main()
