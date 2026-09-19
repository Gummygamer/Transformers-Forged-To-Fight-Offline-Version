"""Synthetic fixtures only; no game source or binaries are test inputs."""
import contextlib
import io
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
import zipfile

from decompilation import assembly_entries, compile_audit, digest, normalize_accessors


class RecoveryTests(unittest.TestCase):
    def test_zip_rejects_traversal_and_case_collisions(self):
        for extra in ("../escape.dll", "ASSEMBLY-CSHARP.dll"):
            stream = io.BytesIO()
            with zipfile.ZipFile(stream, "w") as archive:
                archive.writestr("assets/bin/Data/Managed/Assembly-CSharp.dll", b"fixture")
                archive.writestr("assets/bin/Data/Managed/" + extra, b"fixture")
                with self.assertRaises(ValueError):
                    assembly_entries(archive)

    def test_native_apk_is_not_a_mono_export(self):
        with zipfile.ZipFile(io.BytesIO(), "w") as archive:
            archive.writestr("lib/arm64-v8a/libil2cpp.so", b"fixture")
            with self.assertRaisesRegex(ValueError, "IL2CPP"):
                assembly_entries(archive)

    def test_getter_repair_preserves_return_and_is_idempotent(self):
        original = "\tvirtual int ICount.get_Count()\n\t{\n\t\treturn items.Count;\n\t}"
        repaired, count = normalize_accessors(original)
        self.assertEqual(count, 1)
        self.assertIn("int ICount.Count", repaired)
        self.assertIn("return items.Count;", repaired)
        self.assertEqual(normalize_accessors(repaired), (repaired, 0))
        complex_body = original.replace("return items.Count;", "Touch();\n\t\treturn items.Count;")
        self.assertEqual(normalize_accessors(complex_body), (complex_body, 0))

    def test_audit_uses_reference_override_and_preserves_input(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            (root / "managed").mkdir()
            source = root / "source/Example/Example.cs"
            source.parent.mkdir(parents=True)
            source.write_text("class Example {}\n")
            original_hash = digest(source)
            for name in ("Example.dll", "mscorlib.dll"):
                (root / "managed" / name).write_bytes(b"synthetic input")
            refs = root / "refs"
            refs.mkdir()
            (refs / "mscorlib.dll").write_bytes(b"synthetic reference")
            csc = root / "csc.dll"
            csc.write_bytes(b"synthetic compiler")
            (root / "manifest.json").write_text(json.dumps({"assemblies": [
                {"name": p.name, "sha256": digest(p)} for p in (root / "managed").iterdir()]}))

            def failed_compiler(command, **kwargs):
                rsp = Path(command[-1][1:]).read_text()
                self.assertIn(str(refs / "mscorlib.dll"), rsp)
                self.assertNotIn(str(root / "managed/mscorlib.dll"), rsp)
                self.assertNotIn(str(root / "managed/Example.dll"), rsp)
                kwargs["stdout"].write("error CS0001: synthetic failure\n")
                return type("Result", (), {"returncode": 1})()

            with patch("decompilation.subprocess.run", side_effect=failed_compiler), \
                    contextlib.redirect_stdout(io.StringIO()):
                self.assertEqual(compile_audit(root, "dotnet", csc, [refs], ["Example"]), 1)
            self.assertEqual(digest(source), original_hash)
            report = json.loads(next(root.glob("compile-*/report.json")).read_text())
            self.assertEqual(report["assemblies"][0]["errors"], {"CS0001": 1})
            self.assertFalse(report["runtime_verified"])
            (root / "managed/Example.dll").write_bytes(b"changed")
            with self.assertRaisesRegex(ValueError, "Assembly changed"):
                compile_audit(root, "dotnet", csc, [refs], ["Example"])


if __name__ == "__main__":
    unittest.main()
