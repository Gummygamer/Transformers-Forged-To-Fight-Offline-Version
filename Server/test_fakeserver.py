import json
import unittest

import fakeserver
import gamedata


class SavedSquadSessionTests(unittest.TestCase):
    TEAM = ["grimlock_gs_mp08", "megatron_cin_rotf"]

    def setUp(self):
        fakeserver._quest_positions.clear()
        fakeserver.reset_saved_team()

    def tearDown(self):
        fakeserver.reset_saved_team()

    def dynamic(self, path, body):
        return json.loads(fakeserver.H._dynamic(None, path, json.dumps(body)))

    def test_set_saved_team_updates_active_team_and_remembered_quest_squad(self):
        saved = self.dynamic(
            "/bcg/setSavedTeam", {"teamID": "0", "heroes": self.TEAM}
        )
        updates = saved["result"]["updates"]

        self.assertEqual(fakeserver.get_saved_team(), self.TEAM)
        self.assertEqual(len(updates["activeTeams"]), 1)
        self.assertEqual(list(updates["activeTeams"][0]["heroes"]), self.TEAM)

        refreshed = self.dynamic("/bcg/getUserData", {})
        refreshed_updates = refreshed["result"]["updates"]
        self.assertEqual(
            [hero["bid"] for hero in refreshed_updates["savedTeams"][0]["heroes"]],
            self.TEAM,
        )
        self.assertEqual(list(refreshed_updates["activeTeams"][0]["heroes"]), self.TEAM)

        begun = self.dynamic("/quests/quest-begin/1.1.1", {"setId": "story_act1"})
        progression = begun["result"]["activeQuests"]["1.1.1"]["instances"][0]["progression"]
        self.assertEqual(
            list(progression["users"][gamedata.LOCAL_UID]["team"]), self.TEAM
        )


if __name__ == "__main__":
    unittest.main()
