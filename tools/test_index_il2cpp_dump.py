#!/usr/bin/env python3
import json
import tempfile
import unittest
from pathlib import Path

from index_il2cpp_dump import parse_dump, script_methods


SAMPLE = """// Namespace: Demo
public class Outer // TypeDefIndex: 1
{
    private static int _count; // 0x0
    // RVA: 0x1234 Offset: 0x1234 VA: 0x1234
    public void Start(int value) { }
}

// Namespace:
public class Outer.Inner // TypeDefIndex: 2
{
    private float <Time>k__BackingField; // 0x18
    // RVA: 0xABC0 Offset: 0xABC0 VA: 0xABC0
    public float get_Time() { }
}
"""


class DumpIndexTests(unittest.TestCase):
    def test_types_methods_and_fields(self):
        parsed = parse_dump(Path(self._write(SAMPLE)))
        self.assertEqual([t["name"] for t in parsed["types"]], ["Demo.Outer", "Outer.Inner"])
        self.assertEqual(parsed["methods"][0]["rva"], 0x1234)
        self.assertEqual(parsed["methods"][1]["declaring_type"], "Outer.Inner")
        self.assertEqual(parsed["fields"][1]["offset"], 0x18)

    def test_script_method_addresses_are_preserved(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "script.json"
            path.write_text(json.dumps({"ScriptMethod": [{"Name": "Demo.Outer.Start", "Address": 4660}]}))
            self.assertEqual(script_methods(path)[0]["address_hex"], "0x1234")

    @staticmethod
    def _write(text):
        handle = tempfile.NamedTemporaryFile("w", suffix=".cs", delete=False)
        handle.write(text)
        handle.close()
        return handle.name


if __name__ == "__main__":
    unittest.main()
