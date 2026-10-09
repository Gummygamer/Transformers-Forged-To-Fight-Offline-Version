"""Exercise the actual Android server and payload on the host.

Usage: python3 tools/nativehook/test_story_regressions.py HARNESS PAYLOAD
"""
from concurrent.futures import ThreadPoolExecutor
import json
import threading
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


def request(path, body=None, allow_error=False):
    req = urllib.request.Request(
        f"http://127.0.0.1:{PORT}{path}",
        data=None if body is None else json.dumps(body).encode(),
        headers={"Content-Type": "application/json"},
    )
    with urllib.request.urlopen(req, timeout=5) as reply:
        decoded = json.load(reply)
        if allow_error:
            return decoded
        assert decoded["error"] is None, decoded
        return decoded["result"]


def begin(body=None, qid=QID):
    result = request(f"/quests/quest-begin/{qid}", body or {})
    return result["activeQuests"][qid]["instances"][0]


def move(dx, qid=QID, dy=0):
    return request(f"/quests/quest-movedir/{qid}-0/{dx}/{dy}", {})


def resolve(outcome, qid=QID, game_stats=None):
    body = {"qid": qid + "-0", "results": {"result": outcome}}
    if game_stats is not None:
        body["game_stats"] = game_stats
    request("/matches/resolve-match/quests_fight", body)


def assert_health(instance, bid, expected):
    team = instance["progression"]["users"][UID]["team"]
    assert abs(team[bid]["hp"] - expected) < 0.0001, team


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
    health_probe = Path(directory) / "health_probe"
    subprocess.run([
        "cc", "-std=c11", "-O1", "-Wall", "-Wextra", "-pthread",
        "-I", str(Path(__file__).resolve().parent),
        str(Path(__file__).resolve().parent / "test_health_probe.c"),
        str(Path(__file__).resolve().parent / "inapk_server.c"),
        "-o", str(health_probe),
    ], check=True)
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
        # Old saves could place Motormaster's 2.4.1 entry on the filler row.
        # Beginning the quest must snap to the authored start and retain valid
        # cleared history, then the first step must reach the authored enemy.
        stop()
        state_path.write_text("TFTF2\nS|" + ",".join(TEAM) +
                              "\nQ|2.4.1|0|1|0|0|1,2|0.8000,0.7000,0.6000,1.0000,1.0000\n")
        start()
        motormaster = begin(qid="2.4.1")
        assert motormaster["progression"]["currentPos"] == {"x": 2, "y": 1}
        assert {"x": 1, "y": 2} in motormaster["cleared"]
        assert_squad(motormaster)
        assert_health(motormaster, TEAM[0], 0.8)
        assert_health(motormaster, TEAM[1], 0.7)
        briefing = move(0, "2.4.1", dy=1)
        assert briefing["progression"]["currentPos"] == {"x": 2, "y": 2}
        assert "currentBattleId" not in briefing["progression"]
        waspinator = move(1, "2.4.1")
        assert waspinator["progression"].get("currentBattleId") == "waspinator_gs_deluxe", waspinator["progression"]

        # A valid in-flight encounter stays at its authored route coordinate
        # when the player reenters the quest.
        stop()
        state_path.write_text("TFTF2\nS|" + ",".join(TEAM) +
                              "\nQ|2.4.1|3|2|1|0|1,2|0.8000,0.7000,0.6000,1.0000,1.0000\n")
        start()
        pending = begin(qid="2.4.1")
        assert pending["progression"]["currentPos"] == {"x": 3, "y": 2}
        assert move(0, "2.4.1")["progression"]["currentBattleId"] == "waspinator_gs_deluxe"

        # Out-of-map coordinates are repaired by move as well as begin.
        stop()
        state_path.write_text("TFTF2\nS|" + ",".join(TEAM) +
                              "\nQ|2.4.1|99|99|1|1|3,2|0.8000,0.7000,0.6000,1.0000,1.0000\n")
        start()
        recovered = move(0, "2.4.1", dy=1)
        assert recovered["progression"]["currentPos"] == {"x": 2, "y": 2}
        assert "currentBattleId" not in recovered["progression"]
        assert {"x": 3, "y": 2} in recovered["progression"]["cleared"]
        print("PASS: stale and out-of-bounds Motormaster saves recover; valid pending progress persists")

        # The first move before any quest-begin call also uses the authored
        # (2,1) entry rather than the old generic (0,1) coordinate.
        stop()
        if state_path.exists():
            state_path.unlink()
        start()
        direct = move(0, "2.4.1", dy=1)
        assert direct["progression"]["currentPos"] == {"x": 2, "y": 2}
        assert "currentBattleId" not in direct["progression"]
        assert move(1, "2.4.1")["progression"]["currentBattleId"] == "waspinator_gs_deluxe"
        assert direct["progression"]["currentPos"] == {"x": 2, "y": 2}

        stop()
        if state_path.exists():
            state_path.unlink()
        start()
        refresh = request("/autorefresh/gamestore/refresh")
        catalog = refresh["gamestore"]
        assert catalog["version_id"] and len(catalog["items"]["consumable"]) == 3
        grouped = request("/autorefresh/grouprefresh?groups.0.name=gamestore")
        assert grouped["updates"][0]["gamestore"] == catalog
        combined = request("/autorefresh/grouprefresh?groups.0.name=missionsconfig&groups.1.name=gamestore")
        assert [update["name"] for update in combined["updates"]] == ["missionsconfig", "gamestore"]
        assert combined["updates"][1]["gamestore"] == catalog
        print("PASS: consumables load on direct and grouped refresh; combined refresh preserves missions")
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
        resolve("WON", game_stats={"player_0_stats": {
            "char": TEAM[0], "hp_percent": 0.42,
        }})
        cleared = move(0)
        assert_safe(cleared, 1)
        updated_team = cleared["teamData"]["updates"]["activeTeams"][0]
        assert updated_team["aid"] == QID + "-0"
        assert abs(updated_team["heroes"][TEAM[0]]["hp"] - 0.42) < 0.0001
        assert abs(cleared["progression"]["users"][UID]["team"][TEAM[0]]["hp"] - 0.42) < 0.0001
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
        assert_health(resumed, TEAM[0], 0.42)
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

        # An enemy that shares a blueprint with a benched team bot must not
        # zero that bot's health: only player_0_stats (the player) reports it.
        stop()
        if state_path.exists():
            state_path.unlink()
        start()
        shared = ["jazz_gs_twm05", "grimlock_gs_mp08", "soundwave_gs"]
        request("/bcg/setSavedTeam", {"teamID": "0", "heroes": shared})
        begin({"setId": "custom_story_act1"}, qid=ACT3_QID)
        assert move(1, ACT3_QID, 0)["progression"]["currentBattleId"] == "jazz_gs_twm05"
        resolve("WON", ACT3_QID, game_stats={
            "player_0_stats": {"char": shared[1], "hp_percent": 0.6},
            "player_1_stats": {"char": shared[0], "hp_percent": 0},
        })
        stop()
        start()
        resumed = begin(qid=ACT3_QID)
        assert_health(resumed, shared[0], 1.0)
        assert_health(resumed, shared[1], 0.6)
        assert_health(resumed, shared[2], 1.0)
        # The same bot fighting its own blueprint still reports its own health.
        assert move(1, ACT3_QID, -1)["progression"]["currentBattleId"] == "grindor_cin_rotf"
        resolve("WON", ACT3_QID, game_stats={
            "player_0_stats": {"char": shared[0], "hp_percent": 0.3},
            "player_1_stats": {"char": "grindor_cin_rotf", "hp_percent": 0},
        })
        stop()
        start()
        resumed = begin(qid=ACT3_QID)
        assert_health(resumed, shared[0], 0.3)
        assert_health(resumed, shared[1], 0.6)

        print("PASS: enemy sharing a team bot's blueprint leaves that bot's health intact")

        # Seed persisted injuries, then exercise the same routes and item ids
        # used by the Android popup. Validate server health and resource counts.
        stop()
        state_path.write_text("TFTF2\nS|" + ",".join(TEAM) +
                              "\nQ|2.1.1|1|1|1|0|0,1|0.5000,0.0000,0.3000,1.0000,1.0000\n"
                              "I|repair_kit|3\nI|team_repair_kit|1\nI|revive_kit|1\n")
        start()

        def use(item, bid, route="quests", allow_error=False):
            path = f"/quests/use/{QID}-0" if route == "quests" else "/gamestore/use"
            reply = request(path, {"items": [{"version_id": "offline-1",
                                             "item_name": item}],
                                  "context": {"bid": bid}}, allow_error=True)
            if allow_error:
                return reply
            assert reply["error"] is None, reply
            health, inventory = reply["async"]
            assert (health["component"], health["message"]) == ("BCGManager", "active-team-updated")
            assert (inventory["component"], inventory["message"]) == ("InventoryManager", "update")
            active = health["payload"]["result"]["updates"]["activeTeams"][0]
            assert active["aid"] == QID + "-0"
            progression = begin()["progression"]["users"][UID]["team"]
            assert all(abs(active["heroes"][bot]["hp"] - progression[bot]["hp"]) < 0.0001 for bot in TEAM)
            assert inventory["payload"] == [{"item": item, "quantity": request("/inventory")[item]}]
            return reply["result"]

        for route in ("quests", "gamestore"):
            assert use("repair_kit", TEAM[1], route, True)["error"] == "invalid"
            assert request("/inventory")["repair_kit"] == 999
        assert use("repair_kit", TEAM[0])["usedConsumableCount"] == 1
        assert_health(begin(), TEAM[0], 0.8)
        assert request("/inventory")["repair_kit"] == 999
        assert use("revive_kit", TEAM[1])["success"] is True
        assert_health(begin(), TEAM[1], 0.5)
        assert request("/inventory")["revive_kit"] == 0
        assert use("repair_kit", TEAM[0], "gamestore")["redeemers"][0]["t"] == "hth"
        assert_health(begin(), TEAM[0], 1.0)
        for route in ("quests", "gamestore"):
            assert use("repair_kit", TEAM[0], route, True)["error"] == "invalid"
            assert request("/inventory")["repair_kit"] == 999
        assert use("team_repair_kit", TEAM[0])["success"] is True
        healed = begin()
        assert_health(healed, TEAM[0], 1.0)
        assert_health(healed, TEAM[1], 0.7)
        assert_health(healed, TEAM[2], 0.5)
        assert request("/inventory")["team_repair_kit"] == 0
        assert use("repair_kit", TEAM[2])["success"] is True
        assert_health(begin(), TEAM[2], 0.8)
        assert use("repair_kit", TEAM[2], "gamestore", True)["error"] is None
        assert_health(begin(), TEAM[2], 1.0)
        stop()
        start()
        assert_health(begin(), TEAM[0], 1.0)
        assert_health(begin(), TEAM[1], 0.7)
        assert request("/inventory") == {"repair_kit": 999, "team_repair_kit": 0, "revive_kit": 0}
        active = move(0)["teamData"]["heroes"]
        assert abs(active[TEAM[1]]["hp"] - 0.7) < 0.0001
        resolve("WON")
        assert move(1)["progression"]["currentBattleId"] == "fte_stars_gs_t3"
        print("PASS: repair replenishment, revive, team heal, validation, persistence and subsequent fight")

        stop()
        state_path.write_text("TFTF2\nS|" + ",".join(TEAM) +
                              "\nQ|2.1.1|1|1|1|0|0,1|0.1000,0.0000,0.3000,1.0000,1.0000\n"
                              "I|repair_kit|0\nI|team_repair_kit|1\nI|revive_kit|1\n")
        start()
        item = {"version_id": "offline-1", "item_name": "repair_kit"}
        nested = {**item, "context": {"bid": TEAM[0]}}
        request(f"/quests/use/{QID}-0", {"items": [nested]})
        assert_health(begin(), TEAM[0], 0.4)
        # Context may precede or follow items in the serialized request.
        batch = {"context": {"bid": TEAM[0]}, "items": [item, item]}
        assert request(f"/quests/use/{QID}-0", batch)["usedConsumableCount"] == 2
        assert_health(begin(), TEAM[0], 1.0)
        assert request("/inventory")["repair_kit"] == 999
        store_batch = {"context": {"bid": TEAM[2]}, "items": [item, item]}
        assert request("/gamestore/use", store_batch, allow_error=True)["error"] is None
        assert_health(begin(), TEAM[2], 0.9)
        assert request("/inventory")["repair_kit"] == 999
        stop()
        start()
        assert request("/inventory")["repair_kit"] == 999
        assert_health(begin(), TEAM[0], 1.0)
        assert_health(begin(), TEAM[2], 0.9)
        request(f"/quests/use/{QID}-0", {"items": [{"item_name": "team_repair_kit"}],
                                           "context": {"aid": QID + "-0"}})
        assert_health(begin(), TEAM[1], 0.0)
        assert_health(begin(), TEAM[2], 1.0)
        print("PASS: legacy item context, root context in either order, repeated orders and team use without a bot target")

        # Karma Six has 74 combat nodes; cleared history must round-trip beyond
        # the old 16-entry native limit across a server restart.
        stop()
        cleared = ";".join(f"{x},{y}" for x, y in
                           [(index % 33, index // 33) for index in range(74)])
        state_path.write_text("TFTF2\nS|" + ",".join(TEAM) +
                              "\nQ|1.1.2|16|16|0|0|" + cleared + "|" +
                              ",".join(["1.0000"] * 5) + "\n")
        start()
        first_cleared = begin(qid="1.1.2")["cleared"]
        assert len(first_cleared) == 74, (len(first_cleared), state_path.read_text())
        stop()
        start()
        assert len(begin(qid="1.1.2")["cleared"]) == 74
        print("PASS: all 74 Karma Six cleared tiles round-trip through native persistence")

        # Invalid targets, malformed orders and absent quest state must be
        # terminal errors without health/resource mutation, on either route.
        stop()
        state_path.write_text("TFTF2\nS|" + ",".join(TEAM) +
                              "\nI|repair_kit|1\nI|team_repair_kit|1\nI|revive_kit|1\n")
        start()
        assert use("repair_kit", TEAM[0], "gamestore", True)["error"] == "invalid"
        assert request("/inventory")["repair_kit"] == 999
        stop()
        state_path.write_text("TFTF2\nS|" + ",".join(TEAM) +
                              "\nQ|2.1.1|1|1|1|0|0,1|0.1000,0.1000,0.1000,1.0000,1.0000\n"
                              "I|repair_kit|1\nI|team_repair_kit|1\nI|revive_kit|1\n")
        start()
        before = state_path.read_text()
        malformed = [{}, {"items": []}, {"items": [None]},
                     {"items": [{"item_name": "unknown"}]},
                     {"items": [item], "context": {"bid": "unknown_bot"}},
                     {"items": [item], "context": {"bid": "optimusprime_cin_tf"}}]
        for path in (f"/quests/use/{QID}-0", "/gamestore/use"):
            for body in malformed:
                reply = request(path, body, allow_error=True)
                assert reply == {"error": "invalid", "result": None}, reply
                assert state_path.read_text() == before
        print("PASS: missing quest, unknown/off-team targets and malformed/empty orders leave state intact")

        # The two endpoints can concurrently repair any eligible bots without
        # spending their renewable inventory or healing past full health.
        for attempt in range(12):
            stop()
            state_path.write_text("TFTF2\nS|" + ",".join(TEAM) +
                                  "\nQ|2.1.1|1|1|1|0|0,1|0.1000,0.1000,0.1000,1.0000,1.0000\n"
                                  "I|repair_kit|0\nI|team_repair_kit|1\nI|revive_kit|1\n")
            start()
            barrier = threading.Barrier(12)
            def final_item(index):
                barrier.wait(timeout=5)
                route = f"/quests/use/{QID}-0" if index % 2 else "/gamestore/use"
                return request(route, {"items": [item], "context": {"bid": TEAM[index % 3]}}, allow_error=True)
            with ThreadPoolExecutor(max_workers=12) as pool:
                replies = list(pool.map(final_item, range(12)))
            assert sum(reply["error"] is None for reply in replies) == 9, replies
            assert all(reply["error"] in (None, "invalid") for reply in replies), replies
            hp = begin()["progression"]["users"][UID]["team"]
            assert sorted(round(hp[bot]["hp"], 4) for bot in TEAM) == [1.0, 1.0, 1.0], hp
            assert request("/inventory") == {"repair_kit": 999, "team_repair_kit": 1, "revive_kit": 1}
        print("PASS: concurrent repairs replenish inventory and preserve health limits")

        # Leaving an unfinished Karma Six encounter and entering another story
        # quest must make combat read the newly selected quest's health.
        stop()
        if state_path.exists():
            state_path.unlink()
        start()
        selected_team = {f"tm{i}": bid for i, bid in enumerate(TEAM)}
        karma = begin(selected_team, "1.1.2")
        assert karma["progression"]["currentPos"] == {"x": 16, "y": 16}
        assert move(1, "1.1.2")["progression"]["currentBattleId"]
        resolve("LOST", "1.1.2", game_stats={"player_0_stats": {
            "char": TEAM[0], "hp_percent": 0.2,
        }})
        assert_health(begin(selected_team, "1.1.2"), TEAM[0], 0.2)
        begin(selected_team, QID)
        assert move(1, QID)["progression"]["currentBattleId"]
        stop()
        probe = subprocess.run([str(health_probe), PAYLOAD], env=env, text=True,
                              capture_output=True, check=True, timeout=10)
        assert probe.stdout.strip() == "1.0000", probe.stdout + probe.stderr
        start()
        request("/matches/resolve-match/quests_fight", {"results": {"result": "WON"}})
        state = state_path.read_text()
        assert "Q|1.1.2|17|16|1|0|16,16|0.2000" in state, state
        assert "Q|2.1.1|1|1|0|1|0,1;1,1|1.0000" in state, state
        assert request("/quests/quest-movedir/1.1.2-0/0/0", {})["progression"]["currentBattleId"]
        assert "currentBattleId" not in request("/quests/quest-movedir/2.1.1-0/0/0", {})["progression"]
        print("PASS: Karma Six partial play preserves its fight and health while the selected story quest resolves")

    finally:
        if process is not None and process.poll() is None:
            stop()
