"""Wire-level regression test for the small native payload server."""
import http.client
import os
from pathlib import Path
import shutil
import socket
import subprocess
import tempfile
import time
import unittest

import export_payload
import fakeserver


ROOT = Path(__file__).resolve().parents[1]
RESP = ROOT / "Server" / "responses"


class InApkServerTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        if shutil.which("cc") is None:
            raise unittest.SkipTest("cc is not on PATH")
        cls.tmp = tempfile.TemporaryDirectory()
        cls.harness = Path(cls.tmp.name) / "harness"
        subprocess.run([
            "cc", "-std=c11", "-O1", "-Wall", "-Wextra", "-o", str(cls.harness),
            str(ROOT / "tools/nativehook/inapk_server_main.c"),
            str(ROOT / "tools/nativehook/inapk_server.c"), "-lpthread",
        ], check=True)

    @classmethod
    def tearDownClass(cls):
        cls.tmp.cleanup()

    def setUp(self):
        fakeserver._quest_positions.clear()
        export_payload.build_payload.cache_clear()
        with socket.socket() as s:
            s.bind(("127.0.0.1", 0))
            self.port = s.getsockname()[1]
        self.payload = Path(self.tmp.name) / f"{self.port}.bin"
        self.payload.write_bytes(export_payload.build_payload(self.port))
        self.proc = subprocess.Popen([str(self.harness), str(self.payload)], stdout=subprocess.PIPE,
                                     stderr=subprocess.PIPE, text=True)
        self.addCleanup(self.stop)
        deadline = time.monotonic() + 10
        while time.monotonic() < deadline:
            line = self.proc.stdout.readline()
            if line == f"listening {self.port}\n":
                break
            if self.proc.poll() is not None:
                self.fail(self.proc.stderr.read())
        else:
            self.fail("native harness did not start")

    def stop(self):
        if self.proc.poll() is None:
            self.proc.terminate()
            try:
                self.proc.wait(5)
            except subprocess.TimeoutExpired:
                self.proc.kill()
                self.proc.wait(5)
        self.proc.stdout.close()
        self.proc.stderr.close()

    def request(self, method, path, body=None, conn=None):
        created = conn is None
        conn = conn or http.client.HTTPConnection("127.0.0.1", self.port, timeout=5)
        conn.request(method, path, body)
        response = conn.getresponse()
        result = response.status, response.getheaders(), response.read(), conn
        if created:
            conn.close()
        return result

    def dynamic(self, path, body):
        return fakeserver.H._dynamic(None, path, body.decode() if body else "")

    def test_payload_server_matches_fake_server(self):
        canned_get = (RESP / "GET__account_data.json").read_bytes()
        canned_auth = (RESP / "POST__auth_init.json").read_bytes()
        prefix = (RESP / "_autorefresh.json").read_bytes()
        cases = [
            ("GET", "/account/data", None, canned_get),
            ("POST", "/auth/init", b"{}", canned_auth),
            ("GET", "/totally/unknown/route?x=1", None, b'{"error":null,"result":{}}'),
            ("GET", "/autorefresh/somethingelse", None, prefix),
        ]
        for method, path, body, expected in cases:
            status, _, got, _ = self.request(method, path, body)
            self.assertEqual(status, 200)
            self.assertEqual(got, expected)
        c = http.client.HTTPConnection("127.0.0.1", self.port, timeout=5)
        self.addCleanup(c.close)
        self.assertEqual(self.request("GET", "/account/data", conn=c)[2], canned_get)
        self.assertEqual(self.request("GET", "/autorefresh/somethingelse", conn=c)[2], prefix)
        c.close()
        status, headers, got, _ = self.request("HEAD", "/account/data")
        self.assertEqual((status, dict(headers).get("Content-Length"), got), (200, "0", b""))

        for path in [
            "/autorefresh/grouprefresh?groups.0.name=gacha&groups.7.name=quests",
            "/autorefresh/grouprefresh?groups.3.name=missionsconfig",
            "/base/active",
        ]:
            expected = self.dynamic(path, b"")
            self.assertEqual(self.request("GET", path)[2], expected)

        tutorial_cases = [
            ("/tutorial/start-branch", b'{"tid":"FTE","bid":"IntroFight"}'),
            ("/tutorial/start-branch", b'{"tid":"ShieldTutorial"}'),
            ("/tutorial/start-branch", b'{"tid":"SomeOther","bid":"b1"}'),
            ("/tutorial/start-branch", b'{"tid":"FTE"}'),
            ("/tutorial/complete-tutorial", b'{"tid":"FTE","bid":"IntroFight"}'),
        ]
        for path, body in tutorial_cases:
            self.assertEqual(self.request("POST", path, body)[2], self.dynamic(path, body))
        self.assertEqual(self.request("POST", "/tutorial/start-tutorial", b"{}")[2],
                         (RESP / "POST__tutorial_start-tutorial.json").read_bytes())

        heroes = b'{"heroes":[{"level":1,"sig_lvl":0,"flvl":0,"rank":1,"bid":"sharkticon_gs_brawler"}],"nonce":"x","api":6}'
        heroes_two = b'{"heroes":[{"level":1,"sig_lvl":3,"rank":1,"bid":"sharkticon_gs_brawler"},{"level":2,"sig_lvl":4,"rank":1,"bid":"optimusprime_cin_tf"}]}'
        for body in (heroes, heroes_two):
            self.assertEqual(self.request("POST", "/bcg/getBaseHeroData", body)[2], self.dynamic("/bcg/getBaseHeroData", body))
        saved = b'{"heroes":["optimusprime_cin_tf","optimusprimal_bw_mp32"],"api":6,"nonce":"x","teamID":"0"}'
        self.assertEqual(self.request("POST", "/bcg/setSavedTeam", saved)[2], self.dynamic("/bcg/setSavedTeam", saved))

        detail = b'{"hash":"","setId":"story_act1"}'
        self.assertEqual(self.request("POST", "/quests/quest-detail/1.1.1?sig=x&stoken=y", detail)[2],
                         self.dynamic("/quests/quest-detail/1.1.1?sig=x&stoken=y", detail))
        begin = b'{"setId":"story_act1","tm0":"optimusprime_cin_tf"}'
        self.assertEqual(self.request("POST", "/quests/quest-begin/1.1.1", begin)[2], self.dynamic("/quests/quest-begin/1.1.1", begin))
        for _ in range(3):
            path = "/quests/quest-movedir/1.1.1-0/1/0"
            expected = self.dynamic(path, b"{}")
            self.assertEqual(self.request("POST", path, b"{}")[2], expected)


class InApkCandidateTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        if shutil.which("cc") is None:
            raise unittest.SkipTest("cc is not on PATH")
        cls.tmp = tempfile.TemporaryDirectory()
        cls.harness = Path(cls.tmp.name) / "candidates"
        subprocess.run([
            "cc", "-std=c11", "-O1", "-Wall", "-Wextra", "-o", str(cls.harness),
            str(ROOT / "tools/nativehook/inapk_candidates_main.c"),
            str(ROOT / "tools/nativehook/inapk_server.c"), "-lpthread",
        ], check=True)

    @classmethod
    def tearDownClass(cls):
        cls.tmp.cleanup()

    def candidates(self, maps, cmdline=None):
        maps_path = Path(self.tmp.name) / "maps"
        maps_path.write_text(maps)
        cmdline_path = Path(self.tmp.name) / "cmdline"
        if cmdline is None:
            cmdline_path.unlink(missing_ok=True)
        else:
            cmdline_path.write_bytes(cmdline)
        result = subprocess.run([str(self.harness), str(maps_path), str(cmdline_path)],
                                check=True, capture_output=True, text=True)
        return result.stdout.splitlines()

    def test_game_apk_is_ranked_ahead_of_earlier_play_services_apk(self):
        gms = "/data/app/~~sdunyrwN8nShSO2adhEEjg==/com.google.android.gms-Db7dSfGIhIufd1UUBDv5Ww==/base.apk"
        game = "/data/app/~~vxoZ9FQU_8mVV2lHYIaJuA==/com.kabam.bigrobot-TLmfuByms-uLJyuSkKWlAA==/base.apk"
        split = "/data/app/~~vxoZ9FQU_8mVV2lHYIaJuA==/com.kabam.bigrobot-xyz==/split_config.arm64_v8a.apk"
        maps = (
            f"7a1b000000-7a1b100000 r--p 00000000 fd:03 12345  {gms}\n"
            f"7a1c000000-7a1c100000 r--p 00000000 fd:03 12346  {game}\n"
            f"7a1c100000-7a1c200000 r-xp 00010000 fd:03 12346  {game}\n"
            f"7a1d000000-7a1d100000 r--p 00000000 fd:03 12347  {split}\n"
            "7a1e000000-7a1e100000 r-xp 00000000 fd:03 12348  /apex/com.android.art/lib64/libart.so\n"
        )
        candidates = self.candidates(maps, b"com.kabam.bigrobot\0")
        self.assertEqual(candidates[0], game)
        self.assertEqual(candidates.count(game), 1)
        self.assertIn(split, candidates)
        self.assertIn(gms, candidates)

    def test_empty_or_missing_cmdline_still_returns_all_apks(self):
        first = "/data/app/a/com.google.android.gms-x/base.apk"
        second = "/data/app/b/com.kabam.bigrobot-y/base.apk"
        maps = (
            f"1-2 r--p 0 00:00 1 {first}\n"
            f"2-3 r--p 0 00:00 2 {second}\n"
        )
        self.assertCountEqual(self.candidates(maps, b""), [first, second])
        self.assertCountEqual(self.candidates(maps), [first, second])
