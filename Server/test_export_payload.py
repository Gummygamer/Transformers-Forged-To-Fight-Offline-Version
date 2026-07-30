#!/usr/bin/env python3
import json
import struct
import sys
import unittest
import zlib
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import export_payload
import fakeserver
import gamedata


def expand_team_template(entries, body, bids=None, team_id="0"):
    bids = gamedata.resolve_team(bids)
    qteam = b"{" + b",".join(entries[f"@questmember:{bid}"] for bid in bids[:2]) + b"}"
    ateam = b"{" + b",".join(
        b'"' + bid.encode() + b'":' + entries[f"@savedteam:hero:{bid}"] for bid in bids[:2]
    ) + b"}"
    steam = b"[" + b",".join(entries[f"@savedteam:hero:{bid}"] for bid in bids) + b"]"
    return (body.replace(b"%TID%", team_id.encode()).replace(b"%LEAD%", bids[0].encode())
            .replace(b"%QTEAM%", qteam).replace(b"%ATEAM%", ateam).replace(b"%STEAM%", steam))


class PayloadTests(unittest.TestCase):
    def setUp(self):
        fakeserver.reset_saved_team()
        fakeserver._quest_positions.clear()
        self.blob = export_payload.build_payload(8123)
        self.payload = export_payload.load_payload(self.blob)

    def tearDown(self):
        fakeserver.reset_saved_team()
        fakeserver._quest_positions.clear()

    def test_header_layout_and_sorted_entries(self):
        fields = struct.unpack_from("<8s14I", self.blob)
        magic, version, total, port, count, entries_off, prefix_count, prefixes_off, default_off, default_len, crc, *reserved = fields
        self.assertEqual(magic, export_payload.PAYLOAD_MAGIC)
        self.assertEqual(version, export_payload.PAYLOAD_VERSION)
        self.assertEqual(total, len(self.blob))
        self.assertEqual(port, 8123)
        self.assertEqual(crc, zlib.crc32(self.blob[64:]) & 0xFFFFFFFF)
        self.assertEqual(reserved, [0, 0, 0, 0])
        self.assertEqual(self.payload.listen_port, 8123)
        offsets = [entries_off, prefixes_off, default_off]
        keys = []
        for index in range(count):
            key_off, key_len, body_off, body_len = struct.unpack_from("<4I", self.blob, entries_off + index * 16)
            offsets.extend((key_off, body_off))
            self.assertLess(key_off + key_len, len(self.blob))
            self.assertLess(body_off + body_len, len(self.blob))
            keys.append(self.blob[key_off:key_off + key_len])
        for index in range(prefix_count):
            key_off, key_len, body_off, body_len = struct.unpack_from("<4I", self.blob, prefixes_off + index * 16)
            offsets.extend((key_off, body_off))
            self.assertLess(key_off + key_len, len(self.blob))
            self.assertLess(body_off + body_len, len(self.blob))
        self.assertLess(default_off + default_len, len(self.blob))
        self.assertTrue(all(offset % 4 == 0 for offset in offsets))
        self.assertEqual(keys, sorted(keys))
        self.assertEqual(len(keys), len(set(keys)))

    def test_determinism_and_corruption_detection(self):
        export_payload.build_payload.cache_clear()
        first = export_payload.build_payload(8123)
        export_payload.build_payload.cache_clear()
        second = export_payload.build_payload(8123)
        self.assertEqual(first, second)
        corrupt = bytearray(first)
        _, _, _, _, _, entries_off, *_ = struct.unpack_from("<8s14I", corrupt)
        _, _, body_off, _ = struct.unpack_from("<4I", corrupt, entries_off)
        corrupt[body_off] ^= 1
        with self.assertRaisesRegex(ValueError, "CRC-32"):
            export_payload.load_payload(bytes(corrupt))

    def test_static_prefix_and_default_parity(self):
        responses = Path(export_payload.RESPONSES)
        for path in sorted(responses.glob("*.json")):
            if path.name.startswith("_"):
                continue
            method, route = export_payload._route_from_response(path)
            with self.subTest(path=path.name):
                self.assertEqual(self.payload.entries[f"{method} {route}"], path.read_bytes())
        rules = json.loads((responses / "_prefix_rules.json").read_bytes())
        self.assertEqual(
            self.payload.prefixes,
            [(prefix, (responses / filename).read_bytes()) for prefix, filename in rules],
        )
        self.assertEqual(self.payload.default, b'{"error":null,"result":{}}')

    def test_dynamic_parity(self):
        cases = [
            ("GET /base/active", "/base/active", ""),
            ("@grouprefresh:missionsconfig", "/autorefresh/grouprefresh?groups.0.name=missionsconfig", ""),
            ("@grouprefresh:", "/autorefresh/grouprefresh?groups.0.name=other", ""),
            ("POST /quests/quest-detail/1.1.1", "/quests/quest-detail/1.1.1", '{"setId":"story_act1"}'),
        ]
        for key, path, body in cases:
            with self.subTest(key=key):
                actual = fakeserver.H._dynamic(None, path, body)
                self.assertEqual(json.loads(self.payload.entries[key]), json.loads(actual))
        fakeserver.reset_saved_team()
        fakeserver._quest_positions.clear()
        actual = fakeserver.H._dynamic(None, "/quests/quest-begin/1.1.1", '{"setId":"story_act1"}')
        self.assertEqual(
            json.loads(expand_team_template(self.payload.entries, self.payload.entries["POST /quests/quest-begin/1.1.1"])),
            json.loads(actual),
        )

    def test_movedir_state_machine_parity(self):
        qid = "1.1.1"
        transitions = {}
        for line in self.payload.entries[f"@quest:moves:{qid}"].decode().splitlines():
            sx, sy, dx, dy, nx, ny = map(int, line.split())
            transitions[(sx, sy, dx, dy)] = (nx, ny)
        start = tuple(map(int, self.payload.entries[f"@quest:start:{qid}"].split()))
        fakeserver._quest_positions.clear()
        fakeserver.H._dynamic(None, f"/quests/quest-begin/{qid}", '{"setId":"story_act1"}')
        position = start
        # Reaches the final battle tile, then verifies both an out-of-map and a
        # wrong-row direction preserve position and use the baked illegal body.
        for dx, dy in ((1, 0), (1, 0), (1, 0), (-1, 0), (0, 1)):
            key = f"@movedir:{qid}:{position[0]}:{position[1]}:{dx}:{dy}"
            actual = fakeserver.H._dynamic(None, f"/quests/quest-movedir/{qid}-0/{dx}/{dy}", "")
            self.assertEqual(json.loads(expand_team_template(self.payload.entries, self.payload.entries[key])), json.loads(actual))
            position = transitions.get((position[0], position[1], dx, dy), position)

    def test_templates_reproduce_dynamic_responses(self):
        entries = self.payload.entries
        completed = entries["@tutorial:completed"].replace(b"%TID%", b"NormalTutorial").replace(b"%BID%", b"branch-a")
        self.assertEqual(
            json.loads(completed),
            json.loads(fakeserver.H._dynamic(None, "/tutorial/complete-tutorial", '{"tid":"NormalTutorial","bid":"branch-a"}')),
        )
        started = entries["@tutorial:started"].replace(b"%TID%", b"FTE").replace(b"%BID%", b"fight")
        self.assertEqual(
            json.loads(started),
            json.loads(fakeserver.H._dynamic(None, "/tutorial/start-branch", '{"tid":"FTE","bid":"fight"}')),
        )
        self.assertEqual(
            json.loads(entries["@tutorial:error"]),
            json.loads(fakeserver.H._dynamic(None, "/tutorial/start-tutorial", '{"tid":"ShieldTutorial"}')),
        )
        for key in ("@tutorial:completed", "@tutorial:started"):
            self.assertIn(b"%TID%", entries[key])
            self.assertIn(b"%BID%", entries[key])
        for key in ("@tutorial:completed-nobid", "@tutorial:started-nobid"):
            self.assertIn(b"%TID%", entries[key])
            self.assertNotIn(b"%BID%", entries[key])

        bid = "optimusprime_cin_tf"
        hero = entries[f"@hero:{bid}:2:3"].replace(b"%SIG%", b"7")
        hero_response = entries["@herodata:open"] + hero + entries["@herodata:close"]
        request = '{"heroes":[{"bid":"optimusprime_cin_tf","rank":2,"level":3,"sig_lvl":7}]}'
        self.assertEqual(
            json.loads(hero_response),
            json.loads(fakeserver.H._dynamic(None, "/bcg/getBaseHeroData", request)),
        )
        self.assertEqual(entries[f"@hero:{bid}:2:3"].count(b"%SIG%"), 1)

        selected = ["megatron_cin_rotf", "optimusprimal_bw_mp32"]
        saved = expand_team_template(entries, entries["@savedteam:template"], selected, "42")
        expected = fakeserver.H._dynamic(
            None, "/bcg/setSavedTeam", json.dumps({"teamID": "42", "heroes": selected})
        )
        self.assertEqual(json.loads(saved), json.loads(expected))
        self.assertIn("activeTeams", json.loads(saved)["result"]["updates"])
        self.assertEqual(entries["@savedteam:template"].count(b"%TID%"), 2)

        self.assertEqual(entries["@roster"].splitlines(), [bid.encode() for bid in sorted(gamedata.ROSTER)])
        self.assertEqual(entries["@team:default"].splitlines(), [bid.encode() for bid in gamedata.DEFAULT_TEAM])
        for member_bid in gamedata.OWNED:
            expected_member = gamedata.build_quest_progression(team=[member_bid])["users"][gamedata.LOCAL_UID]["team"]
            self.assertEqual(json.loads(b"{" + entries[f"@questmember:{member_bid}"] + b"}"), expected_member)
        user_data = expand_team_template(entries, entries["@userdata:template"])
        self.assertEqual(user_data, (export_payload.RESPONSES / "GET__bcg_getUserData.json").read_bytes())
        fakeserver.reset_saved_team()
        fakeserver._quest_positions.clear()


if __name__ == "__main__":
    unittest.main()
