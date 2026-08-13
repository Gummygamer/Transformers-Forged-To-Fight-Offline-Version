#!/usr/bin/env python3
"""Wire-level STORY 1.1.1 walk checks against the live fake-server handler."""
import json
import threading
import unittest
from http.server import ThreadingHTTPServer
from urllib.request import Request, urlopen

import fakeserver
import gamedata


class QuestWalkWireTests(unittest.TestCase):
    def setUp(self):
        fakeserver._quest_positions.clear()
        fakeserver.reset_saved_team()

        # H logs every request to Server/logs/requests.log. The wire test must remain
        # runnable from a read-only checkout, and request logging is not its subject.
        self._log = fakeserver.log
        fakeserver.log = lambda line: None
        self.server = ThreadingHTTPServer(("127.0.0.1", 0), fakeserver.H)
        self.thread = threading.Thread(target=self.server.serve_forever, daemon=True)
        self.thread.start()
        self.base_url = "http://127.0.0.1:%d" % self.server.server_address[1]

    def tearDown(self):
        self.server.shutdown()
        self.server.server_close()
        self.thread.join()
        fakeserver.log = self._log
        fakeserver._quest_positions.clear()
        fakeserver.reset_saved_team()

    def post(self, path, body=None):
        data = json.dumps(body if body is not None else {}).encode("utf-8")
        request = Request(
            self.base_url + path,
            data=data,
            headers={"Content-Type": "application/json"},
            method="POST",
        )
        with urlopen(request, timeout=5) as response:
            self.assertEqual(response.status, 200)
            return json.loads(response.read().decode("utf-8"))["result"]

    def test_begin_then_walks_through_patrol_to_final_boss(self):
        begun = self.post(
            "/quests/quest-begin/1.1.1",
            {
                "setId": "story_act1",
                "tm0": "optimusprime_cin_tf",
                "tm1": "optimusprimal_bw_mp32",
                "tm2": "megatron_g1_mp10",
            },
        )
        board = begun["activeQuests"]["1.1.1"]["instances"][0]["map"]
        start, patrol, boss = (board["grid"][row][1] for row in range(3))
        self.assertTrue(start["start"])
        self.assertTrue(patrol["walkable"])
        self.assertFalse(patrol.get("final", False))
        self.assertEqual(patrol["boss"], gamedata.MINION_BLUEPRINT)
        self.assertIn(gamedata.MINION_BLUEPRINT, patrol["entities"])
        self.assertFalse(patrol["entities"][gamedata.MINION_BLUEPRINT]["isFinalBoss"])
        self.assertIn({"x": 0, "y": 1}, patrol["links"])
        self.assertIn({"x": 2, "y": 1}, patrol["links"])
        self.assertTrue(boss["final"])

        patrol_move = self.post("/quests/quest-movedir/1.1.1-0/1/0")
        self.assertEqual(len(patrol_move["results"]), 2)
        self.assertEqual(
            patrol_move["results"][0]["action"]["moveto"], {"x": 1, "y": 1}
        )
        patrol_battle = patrol_move["results"][1]["action"]["battle"]
        self.assertEqual(patrol_battle["x"], 1)
        self.assertEqual(patrol_battle["y"], 1)
        self.assertIs(patrol_battle["isFinalBoss"], False)
        self.assertEqual(
            patrol_battle["battleEnemy"]["key"], gamedata.MINION_BLUEPRINT
        )
        patrol_progression = patrol_move["progression"]
        self.assertEqual(patrol_progression["currentBattleId"], gamedata.MINION_BLUEPRINT)
        self.assertEqual(
            patrol_progression["currentBattleEnemy"], {"id": gamedata.MINION_BLUEPRINT}
        )
        self.assertEqual(patrol_progression["currentBattlePos"], {"x": 1, "y": 1})
        self.assertEqual(
            patrol_progression["users"][gamedata.LOCAL_UID]["currentBattleState"],
            "activated",
        )

        boss_move = self.post("/quests/quest-movedir/1.1.1-0/1/0")
        self.assertEqual(boss_move["results"][0]["action"]["moveto"], {"x": 2, "y": 1})
        boss_battle = boss_move["results"][1]["action"]["battle"]
        self.assertEqual((boss_battle["x"], boss_battle["y"]), (2, 1))
        self.assertIs(boss_battle["isFinalBoss"], True)
        self.assertEqual(boss_battle["battleEnemy"]["key"], gamedata.BOSS_BLUEPRINT)

    def test_both_quest_enemies_resolve_to_their_sharkticon_assets(self):
        heroes = [
            {"bid": gamedata.MINION_BLUEPRINT, "rank": 1, "level": 1},
            {"bid": gamedata.BOSS_BLUEPRINT, "rank": 1, "level": 1},
        ]
        result = self.post("/bcg/getBaseHeroData", {"heroes": heroes})
        self.assertEqual([hero["bid"] for hero in result], [hero["bid"] for hero in heroes])
        self.assertEqual(result[0]["img"], "npc_shark_warr")
        self.assertEqual(result[1]["img"], "npc_shark_braw")
        self.assertGreater(result[0]["max_hp"], 0)
        self.assertGreater(result[1]["max_hp"], 0)


if __name__ == "__main__":
    unittest.main()
