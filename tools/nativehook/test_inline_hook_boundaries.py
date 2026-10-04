"""Keep arm64 inline hooks from overwriting an adjacent IL2CPP method."""
import re
import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]
HOOK = ROOT / "tools/nativehook/hook.c"
DUMP = ROOT / "re_notes/dump.cs"


def hook_rvas(source):
    values = set()

    # Every entry in H is passed to inline_hook by the shared installation loop.
    table = re.search(r"static struct \{[^}]*\}\s*H\[\]\s*=\s*\{(.*?)\n\};", source, re.S)
    if table:
        values.update(int(value, 16) for value in re.findall(r"\{\s*(0x[0-9A-Fa-f]+)", table.group(1)))

    # Also collect direct inline_hook(g_base + RVA) installs and resolve named RVA constants.
    constants = {
        name: int(value, 16)
        for name, value in re.findall(r"(?:static\s+)?const\s+uint32_t\s+(RVA_\w+)\s*=\s*(0x[0-9A-Fa-f]+)", source)
    }
    for expression in re.findall(r"inline_hook\(\s*\(void\*\)\(g_base\s*\+\s*([^)]*)\)", source):
        expression = expression.strip()
        if expression in constants:
            values.add(constants[expression])
        elif re.fullmatch(r"0x[0-9A-Fa-f]+", expression):
            values.add(int(expression, 16))
    return values


class InlineHookBoundaryTests(unittest.TestCase):
    def test_all_arm64_inline_hooks_leave_sixteen_bytes_before_next_method(self):
        source = HOOK.read_text(encoding="utf-8")
        methods = sorted({
            int(value, 16)
            for value in re.findall(r"RVA: 0x([0-9A-Fa-f]+)", DUMP.read_text(encoding="utf-8"))
        })
        self.assertTrue(methods, "IL2CPP method RVAs were not found in re_notes/dump.cs")

        violations = []
        for rva in sorted(hook_rvas(source)):
            if rva not in methods:
                self.fail(f"hook RVA 0x{rva:X} is missing from the IL2CPP dump")
            next_method = next((method for method in methods if method > rva), None)
            if next_method is None or next_method - rva < 16:
                gap = "no following method" if next_method is None else f"only {next_method - rva} bytes"
                violations.append(f"0x{rva:X} ({gap})")

        self.assertEqual([], violations, "inline hooks overwrite adjacent methods: " + ", ".join(violations))

    def test_arm32_has_no_corresponding_label_setter_hook(self):
        arm32 = (ROOT / "tools/nativehook/hook_arm32.c").read_text(encoding="utf-8")
        self.assertNotIn("UILabel.set_text", arm32)
        self.assertNotIn("hooked_UILabel_set_text", arm32)


if __name__ == "__main__":
    unittest.main()
