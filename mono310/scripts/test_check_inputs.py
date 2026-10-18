import tempfile
import unittest
import json
from pathlib import Path

from check_inputs import check, check_project, identity


class SourceTrackChecks(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        root = Path(self.temp.name)
        self.ripped = root / "ripped"
        self.settings = self.ripped / "ExportedProject/ProjectSettings/ProjectSettings.asset"
        self.settings.parent.mkdir(parents=True)
        self.settings.write_text("PlayerSettings:\n  bundleVersion: 3.1.0\n")
        self.source = root / "sources"
        self.sci = self.source / "Assembly-CSharp/SCI.cs"
        self.sci.parent.mkdir(parents=True)
        self.sci.write_text("public class SCI<T> { private GameObject ContainerOverride; }")

    def test_matching_track(self):
        self.assertEqual(check(self.ripped, self.source), "3.1.0")

    def test_rejects_wrong_export_before_building(self):
        self.settings.write_text("PlayerSettings:\n  bundleVersion: 2.0.2\n")
        with self.assertRaisesRegex(ValueError, "identifies 2.0.2"):
            check(self.ripped, self.source)

    def test_rejects_wrong_decompilation_with_correct_export(self):
        self.sci.write_text("public class SCI<T> { public SharedComponentInstance SharedComponent; }")
        with self.assertRaisesRegex(ValueError, "shared UI layout"):
            check(self.ripped, self.source)

    def test_rejects_missing_version(self):
        self.settings.write_text("PlayerSettings:\n")
        with self.assertRaisesRegex(ValueError, "identifies unknown"):
            check(self.ripped, self.source)

    def test_rejects_stale_assembled_scripts_after_input_links_are_fixed(self):
        assemblies = self.ripped / "AuxiliaryFiles/GameAssemblies"
        assemblies.mkdir(parents=True)
        for name in ("Assembly-CSharp", "Assembly-CSharp-firstpass"):
            (assemblies / (name + ".dll")).write_bytes(b"matching 3.1.0 export")
        project = Path(self.temp.name) / "project"
        sci = project / "Assets/Scripts/Assembly-CSharp/SCI.cs"
        sci.parent.mkdir(parents=True)
        sci.write_text("public class SCI<T> { }")
        record = project / "mono310-source.json"
        record.write_text(json.dumps({"game_version": "3.1.0", "assemblies": identity(self.ripped)}))
        with self.assertRaisesRegex(ValueError, "assembled UI scripts"):
            check_project(project, self.ripped)
        sci.write_text(self.sci.read_text())
        check_project(project, self.ripped)
        (assemblies / "Assembly-CSharp.dll").write_bytes(b"another export")
        with self.assertRaisesRegex(ValueError, "different source inputs"):
            check_project(project, self.ripped)


if __name__ == "__main__":
    unittest.main()
