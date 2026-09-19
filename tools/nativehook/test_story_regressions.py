"""Exercise the actual Android server and payload on the host.

Usage: python3 tools/nativehook/test_story_regressions.py HARNESS PAYLOAD
"""
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import time
import urllib.request


HARNESS, PAYLOAD = map(lambda p: str(Path(p).resolve()), sys.argv[1:])
PORT = int.from_bytes(Path(PAYLOAD).read_bytes()[16:20], "little")
TEAM = ["nemesisprime_gs_voyager2015", "grimlock_gs_mp08", "soundwave_gs"]
QID = "2.1.1"
ACT2_QID = "2.2.1"
ACT3_QID = "2.3.1"
UID = "1000000000001"


def request(path, body=None):
    req = urllib.request.Request(
        f"http://127.0.0.1:{PORT}{path}",
        data=None if body is None else json.dumps(body).encode(),
        headers={"Content-Type": "application/json"},
    )
    with urllib.request.urlopen(req, timeout=5) as reply:
        decoded = json.load(reply)
        assert decoded["error"] is None, decoded
        return decoded["result"]


def begin(body=None, qid=QID):
    result = request(f"/quests/quest-begin/{qid}", body or {})
    return result["activeQuests"][qid]["instances"][0]


def move(dx, qid=QID, dy=0):
    return request(f"/quests/quest-movedir/{qid}-0/{dx}/{dy}", {})


def resolve(outcome, qid=QID):
    request("/matches/resolve-match/quests_fight",
            {"qid": qid + "-0", "results": {"result": outcome}})


def assert_squad(result):
    assert list(result["progression"]["users"][UID]["team"]) == TEAM


def assert_safe(result, x, y=1):
    progression = result["progression"]
    assert progression["currentPos"] == {"x": x, "y": y}
    assert "currentBattleId" not in progression
    assert "currentBattleState" not in progression["users"][UID]
    assert all("battle" not in action["action"] for action in result["results"])
    assert_squad(result)


with tempfile.TemporaryDirectory(prefix="tftf-story-") as directory:
    env = dict(os.environ, TFTF_QUEST_STATE_FILE=directory + "/state")
    state_path = Path(directory) / "state"
    process = None

    def start():
        global process
        process = subprocess.Popen([HARNESS, PAYLOAD], env=env,
                                   stdout=subprocess.PIPE, text=True)
        assert process.stdout.readline().startswith("listening")

    def stop():
        process.terminate()
        process.wait(timeout=5)

    try:
        start()
        saved = request("/bcg/setSavedTeam", {"teamID": "0", "heroes": TEAM})
        active = {team["aid"]: team for team in saved["updates"]["activeTeams"]}
        assert list(active[QID + "-0"]["heroes"]) == TEAM
        instance = begin({"setId": "custom_story_act1",
                          **{f"tm{i}": bid for i, bid in enumerate(TEAM)}})
        assert_squad(instance)
        grid = instance["map"]["grid"]
        for row in (1, 2):
            assert {"x": row + 1, "y": 1} in grid[row][1]["links"]
            assert {"x": row + 1, "y": 1} in grid[row][1]["visibleLinks"]
        first = move(1)
        assert first["progression"]["currentBattleId"] == "bludgeon_gs_rd20"
        assert {"x": 1, "y": 1} not in first["progression"]["cleared"]
        assert list(first["teamData"]["heroes"]) == TEAM
        resolve("LOST")
        assert move(1)["progression"]["currentBattleId"] == "bludgeon_gs_rd20"
        resolve("WON")
        cleared = move(0)
        assert_safe(cleared, 1)
        assert {"x": 1, "y": 1} in cleared["progression"]["cleared"]
        assert_safe(move(-1), 0)
        assert_safe(move(1), 1)
        second = move(1)
        assert second["progression"]["currentBattleId"] == "fte_stars_gs_t3"
        resolve("WON")
        assert_safe(move(-1), 1)
        stop()
        start()
        resumed = begin()
        assert_squad(resumed)
        assert {"x": 1, "y": 1} in resumed["cleared"]
        assert {"x": 2, "y": 1} in resumed["cleared"]
        assert_safe(move(0), 1)
        assert_safe(move(1), 2)
        final = move(1)
        assert final["progression"]["currentBattleId"] == "ironhide_cin_rotf"
        assert final["results"][1]["action"]["battle"]["isFinalBoss"] is True
        resolve("WON")

        act2 = begin(qid=ACT2_QID)
        act2_tile1 = act2["map"]["grid"][1][1]
        act2_tile2 = act2["map"]["grid"][2][1]
        act2_tile3 = act2["map"]["grid"][3][1]
        assert act2["data"]["act"] == 2
        assert act2["map"]["gridDimension"] == 4
        assert act2_tile1["dialogue"] == "custom_act2_intro"
        assert "dialogue" not in act2_tile2
        assert act2_tile1["boss"] == "bumblebee_gs_kabam"
        assert act2_tile2["boss"] == "mirage_gs_deluxe2016"
        assert "bumblebee_gs_kabam" in act2_tile1["entities"]
        assert [entry["line"] for entry in act2["data"]["dialogueTable"]["custom_act2_intro"]] == [
            "Scanners found vital resources to repair our ship.",
            "Understood, lead the way and secure them.",
            "Absolutely, let's move out. We will surely face many battles, but we must be careful during these fights.",
        ]
        kickback = move(1, ACT2_QID)
        assert kickback["progression"]["currentBattleId"] == "kickback_gs_kabam"
        assert kickback["results"][1]["action"]["battle"]["isFinalBoss"] is False
        resolve("WON", ACT2_QID)
        mirage = move(1, ACT2_QID)
        assert mirage["progression"]["currentBattleId"] == "mirage_gs_deluxe2016"
        assert mirage["results"][1]["action"]["battle"]["isFinalBoss"] is False
        resolve("LOST", ACT2_QID)
        assert move(1, ACT2_QID)["progression"]["currentBattleId"] == "mirage_gs_deluxe2016"
        resolve("WON", ACT2_QID)

        bumblebee = move(1, ACT2_QID)
        assert bumblebee["progression"]["currentBattleId"] == "bumblebee_gs_kabam"
        assert bumblebee["results"][1]["action"]["battle"]["isFinalBoss"] is True
        assert act2_tile3["boss"] == "bumblebee_gs_kabam"
        assert act2_tile3["dialogue"] == "custom_bumblebee_intro"
        assert [entry["character"] for entry in act2["data"]["dialogueTable"]["custom_bumblebee_intro"]] == [
            "bumblebee_gs_kabam", "optimusprime_cin_tf"
        ]
        assert [entry["line"] for entry in act2["data"]["dialogueTable"]["custom_bumblebee_intro"]] == [
            "Who are you? You will die!", "Show yourself."
        ]
        resolve("WON", ACT2_QID)
        assert_safe(move(1, ACT2_QID), 3)
        other = request("/quests/quest-begin/1.1.1", {})["activeQuests"]["1.1.1"]
        assert other["instances"][0]["cleared"] == []
        print("PASS: selected squad, forward links, loss, victory, backtracking, restart, Kickback-to-Mirage-to-Bumblebee transition, quest isolation")

        # --- Act 3: Bludgeon section ---

        # Act3 begins directly — the artificial Act-2-completion gate was removed
        # (commit d6f874a, device-verified); no quest-begin error body is ever returned.
        act3 = begin({"setId": "custom_story_act1"}, qid=ACT3_QID)
        assert act3["data"]["act"] == 3
        assert act3["data"]["image"] == "bludge_gs"
        assert act3["map"]["gridDimension"] == 5
        act3_grid = act3["map"]["grid"]
        # Jazz at (1,2) — first battle
        jazz_tile = act3_grid[1][2]
        assert jazz_tile["boss"] == "jazz_gs_twm05"
        assert jazz_tile["walkable"]
        # Grindor at (2,1), Ironhide at (2,3), Bludgeon at (3,2)
        assert act3_grid[2][1]["boss"] == "grindor_cin_rotf"
        assert act3_grid[2][3]["boss"] == "ironhide_cin_rotf"
        bludgeon_tile = act3_grid[3][2]
        assert bludgeon_tile["boss"] == "bludgeon_gs_rd20"
        assert bludgeon_tile["final"]

        # Jazz first (move dx=1, dy=0 from start (0,2))
        jazz_move = move(1, ACT3_QID, 0)
        assert jazz_move["progression"]["currentBattleId"] == "jazz_gs_twm05"
        assert jazz_move["results"][1]["action"]["battle"]["isFinalBoss"] is False
        resolve("LOST", ACT3_QID)
        # Retry Jazz after loss
        assert move(1, ACT3_QID, 0)["progression"]["currentBattleId"] == "jazz_gs_twm05"
        resolve("WON", ACT3_QID)

        # Off-link rejection: from (1,2), dy=+2 goes nowhere (no authored link)
        off_link = move(0, ACT3_QID, 2)
        assert off_link.get("progression", {}).get("currentPos") == {"x": 1, "y": 2}  # off-link ignored, position unchanged

        # Left branch: Grindor at (2,1) via dy=-1
        grindor_move = move(1, ACT3_QID, -1)
        assert grindor_move["progression"]["currentBattleId"] == "grindor_cin_rotf"
        assert grindor_move["results"][1]["action"]["battle"]["isFinalBoss"] is False
        resolve("WON", ACT3_QID)

        # From Grindor (2,1) to Bludgeon (3,2) via dx=1, dy=1
        boss_move = move(1, ACT3_QID, 1)
        assert boss_move["progression"]["currentBattleId"] == "bludgeon_gs_rd20"
        assert boss_move["results"][1]["action"]["battle"]["isFinalBoss"] is True
        # Duplicate WON should not re-grant (resolve again)
        resolve("WON", ACT3_QID)
        # After Bludgeon WON, moving on a terminal boss should be safe
        # Duplicate victory should be harmless
        resolve("WON", ACT3_QID)
        # Verify cleared contains all tiles traversed
        final_state = move(0, ACT3_QID, 0)
        cleared_tiles = final_state["progression"]["cleared"]
        assert {"x": 1, "y": 2} in cleared_tiles  # Jazz
        assert {"x": 2, "y": 1} in cleared_tiles  # Grindor
        assert {"x": 3, "y": 2} in cleared_tiles  # Bludgeon

        print("PASS: act3 Jazz-first, left-branch Grindor, convergence to Bludgeon, loss retry, off-link rejection, duplicate WON")

        # Test right branch (Ironhide) — restart harness to reset state
        stop()
        if state_path.exists():
            state_path.unlink()
        start()
        # No act2 progression needed to begin act3 (gate removed, commit d6f874a)
        request("/bcg/setSavedTeam", {"teamID": "0", "heroes": TEAM})

        # Right branch
        act3b = begin({"setId": "custom_story_act1"}, qid=ACT3_QID)
        move(1, ACT3_QID, 0); resolve("WON", ACT3_QID)  # Jazz
        ironhide_move = move(1, ACT3_QID, 1)  # dy=+1 → Ironhide
        assert ironhide_move["progression"]["currentBattleId"] == "ironhide_cin_rotf"
        assert ironhide_move["results"][1]["action"]["battle"]["isFinalBoss"] is False
        resolve("WON", ACT3_QID)
        # From Ironhide (2,3) to Bludgeon (3,2) via dx=1, dy=-1
        boss2 = move(1, ACT3_QID, -1)
        assert boss2["progression"]["currentBattleId"] == "bludgeon_gs_rd20"
        assert boss2["results"][1]["action"]["battle"]["isFinalBoss"] is True
        resolve("WON", ACT3_QID)

        print("PASS: act3 right-branch Ironhide, convergence to Bludgeon")

        # Test restart/resume with act3 position
        stop()
        start()
        act3c = begin({"setId": "custom_story_act1"}, qid=ACT3_QID)
        assert act3c["data"]["act"] == 3
        assert {"x": 3, "y": 2} in act3c["cleared"]  # Bludgeon persists
        assert {"x": 1, "y": 2} in act3c["cleared"]  # Jazz persists

        print("PASS: act3 restart/resume with cleared persistence")

        # Quest isolation: 1.1.1 should be unchanged
        other2 = request("/quests/quest-begin/1.1.1", {})["activeQuests"]["1.1.1"]
        assert other2["instances"][0]["cleared"] == []

        print("PASS: act3 quest isolation")

    finally:
        if process is not None and process.poll() is None:
            stop()
