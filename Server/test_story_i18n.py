#!/usr/bin/env python3
"""Every story line the server sends must have a translation in every language."""
import json
import re
import unittest
from pathlib import Path

HERE = Path(__file__).parent
TABLE = json.loads((HERE / "data/story_i18n.json").read_text(encoding="utf8"))
LINE = re.compile(r'dialogue_entry\((?:true|false), "[^"]*", "[^"]*", "[^"]*", "((?:[^"\\]|\\.)+)"')


class StoryTable(unittest.TestCase):
    def test_all_dialogue_listed(self):
        source = (HERE / "gamedata.lbl").read_text() + (HERE / "fakeserver.lbl").read_text()
        self.assertEqual(set(LINE.findall(source)) - set(TABLE["en"]), set())

    def test_languages_complete(self):
        for lang, lines in TABLE.items():
            self.assertEqual(len(lines), len(TABLE["en"]), lang)
            self.assertTrue(all(line.strip() for line in lines), lang)


if __name__ == "__main__":
    unittest.main()
