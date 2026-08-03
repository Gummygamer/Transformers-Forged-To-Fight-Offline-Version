"""Regression checks for the arm64 native-hook slot and handler ordering."""
from pathlib import Path
import re
import unittest


ROOT = Path(__file__).resolve().parents[1]
HOOK_SOURCE = ROOT / "tools" / "nativehook" / "hook.c"


class NativeHookSlotTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.source = HOOK_SOURCE.read_text()
        cls.slots = cls._slot_table(cls.source)
        cls.handlers = cls._handlers(cls.source)

    @staticmethod
    def _without_line_comments(text):
        return re.sub(r"//[^\n]*", "", text)

    @classmethod
    def _slot_table(cls, source):
        match = re.search(
            r"static\s+struct\s*\{.*?\}\s*H\s*\[\s*\]\s*=\s*\{(.*?)\n\s*\};\s*\n\s*#define\s+NH",
            source,
            re.DOTALL,
        )
        if not match:
            raise AssertionError("could not locate H slot table")
        table = cls._without_line_comments(match.group(1))
        entries = re.findall(
            r"^\s*\{\s*(0x[0-9A-Fa-f]+)\s*,\s*\"([^\"]+)\"\s*,[^\n{}]*\}\s*,?\s*$",
            table,
            re.MULTILINE,
        )
        if not entries:
            raise AssertionError("could not parse any H slot-table entries")
        return [(int(address, 16), tag) for address, tag in entries]

    @classmethod
    def _handlers(cls, source):
        match = re.search(
            r"static\s+void\s*\*\s*handlers\s*\[\s*\]\s*=\s*\{(.*?)\};",
            cls._without_line_comments(source),
            re.DOTALL,
        )
        if not match:
            raise AssertionError("could not locate handlers dispatch array")
        return [int(number) for number in re.findall(r"\bhook_(\d+)\b", match.group(1))]

    def test_roster_drag_slots_have_expected_order_and_addresses(self):
        expected = {
            "ROSTERDRAGFIX": (135, 0xB210CC),
            "ROSTERDRAGCTX": (136, 0x1E5F444),
            "ROSTERDRAGCHOKE": (137, 0x1404E6C),
        }
        by_tag = {tag: (index, address) for index, (address, tag) in enumerate(self.slots)}
        for tag, wanted in expected.items():
            self.assertIn(tag, by_tag)
            self.assertEqual(by_tag[tag], wanted)
            self.assertRegex(self.source, rf'"{tag}"[^\n]*//\s*{wanted[0]}\b')

    def test_roster_drag_handlers_match_their_slot_indices(self):
        for number in (135, 136, 137):
            self.assertRegex(self.source, rf"\bvoid\s*\*\s*hook_{number}\s*\(")
            self.assertGreater(len(self.handlers), number)
            self.assertEqual(self.handlers[number], number)

    def test_hardened_safe_action_slot_is_retained(self):
        self.assertGreater(len(self.slots), 77)
        self.assertEqual(self.slots[77], (0x152B570, "FIXWRAPMI"))


if __name__ == "__main__":
    unittest.main()
