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
