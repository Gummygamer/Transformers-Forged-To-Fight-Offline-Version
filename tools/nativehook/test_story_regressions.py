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


def move(dx, qid=QID):
    return request(f"/quests/quest-movedir/{qid}-0/{dx}/0", {})


def resolve(outcome, qid=QID):
    request("/matches/resolve-match/quests_fight",
            {"qid": qid + "-0", "results": {"result": outcome}})


def assert_squad(result):
    assert list(result["progression"]["users"][UID]["team"]) == TEAM


def assert_safe(result, x):
    progression = result["progression"]
    assert progression["currentPos"] == {"x": x, "y": 1}
    assert "currentBattleId" not in progression
    assert "currentBattleState" not in progression["users"][UID]
    assert all("battle" not in action["action"] for action in result["results"])
    assert_squad(result)


with tempfile.TemporaryDirectory(prefix="tftf-story-") as directory:
    env = dict(os.environ, TFTF_QUEST_STATE_FILE=directory + "/state")
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
        act2_tile = act2["map"]["grid"][1][1]
        assert act2["data"]["act"] == 2
        assert act2["map"]["gridDimension"] == 2
        assert act2_tile["dialogue"] == "custom_act2_intro"
        assert act2_tile["boss"] == "bumblebee_gs_kabam"
        assert "bumblebee_gs_kabam" in act2_tile["entities"]
        assert [entry["line"] for entry in act2["data"]["dialogueTable"]["custom_act2_intro"]] == [
            "Scanners found vital resources to repair our ship.",
            "Understood, lead the way and secure them.",
            "Absolutely, let's move out. We will surely face many battles, but we must be careful during these fights.",
        ]
        kickback = move(1, ACT2_QID)
        assert kickback["progression"]["currentBattleId"] == "kickback_gs_kabam"
        assert kickback["results"][1]["action"]["battle"]["isFinalBoss"] is True
        other = request("/quests/quest-begin/1.1.1", {})["activeQuests"]["1.1.1"]
        assert other["instances"][0]["cleared"] == []
        print("PASS: selected squad, forward links, loss, victory, backtracking, restart, final boss, quest isolation")
    finally:
        if process is not None and process.poll() is None:
            stop()
