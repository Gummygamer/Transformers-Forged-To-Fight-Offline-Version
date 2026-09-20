"""Synthetic fixtures only; no game source or binaries are test inputs."""
import contextlib
import io
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
import zipfile

from decompilation import (assembly_entries, compile_audit, dependency_order, digest,
                           normalize_accessors, normalize_source_contracts)


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

    def test_contract_repairs_follow_metadata_contracts(self):
        source = ("using EB.Net;\n"
                  "private const BindingFlags BindingFlags = BindingFlags.Static | BindingFlags.Public;\n"
                  "void F(int alignment = 1, [Optional] Vector2 offset) {}\n"
                  "[CreateAssetMenu(menuName = \"AI\")]\n"
                  "[Header(\"Right\", order = 105)]\n")
        repaired, changes = normalize_source_contracts(Path("ProcessMemberBinding.cs"), source)
        repaired, alias_changes = normalize_source_contracts(Path("EB/DownloadExtractor.cs"), repaired)
        changes += alias_changes
        self.assertIn("using WebRequest = EB.Net.WebRequest;", repaired)
        self.assertIn("System.Reflection.BindingFlags", repaired)
        self.assertIn("int alignment = 1, Vector2 offset = default", repaired)
        self.assertIn("[CreateAssetMenu]", repaired)
        self.assertIn('[Header("Right")]', repaired)
        self.assertEqual(len(changes), 5)

    def test_facebook_method_call_restores_il_backing_fields(self):
        source = ("using System.Runtime.CompilerServices;\n\n"
                  "namespace Facebook.Unity;\n\n"
                  "internal abstract class MethodCall<T> where T : IResult\n"
                  "{\n"
                  "\tprotected FacebookBase FacebookImpl\n\t{\n"
                  "\t\t[CompilerGenerated]\n\t\tset\n\t\t{\n"
                  "\t\t\t_003CFacebookImpl_003Ek__BackingField = value;\n"
                  "\t\t}\n\t}\n\n"
                  "\tprotected MethodArguments Parameters\n\t{\n"
                  "\t\t[CompilerGenerated]\n\t\tset\n\t\t{\n"
                  "\t\t\t_003CParameters_003Ek__BackingField = value;\n"
                  "\t\t}\n\t}\n}\n")
        repaired, changes = normalize_source_contracts(Path("MethodCall.cs"), source)
        self.assertEqual(len(changes), 2)
        self.assertIn("private FacebookBase _003CFacebookImpl_003Ek__BackingField;", repaired)
        self.assertIn("private MethodArguments _003CParameters_003Ek__BackingField;", repaired)
        self.assertEqual(normalize_source_contracts(Path("MethodCall.cs"), repaired),
                         (repaired, []))

    def test_recovered_switch_contracts_restore_explicit_cases(self):
        fixtures = [
            (Path("BattleArbiter.cs"),
             "\t\tswitch (base.CurrentStateName)\n"
             "\t\t{\n"
             "\t\tcase \"Init\":\n"
             "\t\t\treturn;\n"
             "\t\t}\n"
             "\t\tint num;\n"
             "\t\tif (num != 1)\n"
             "\t\t{\n"
             "\t\t\tExit();\n"
             "\t\t}\n",
             ["case \"Exit\":", "Exit();"]),
            (Path("BuffConditionFactory.cs"),
             "\t\tswitch (key)\n"
             "\t\t{\n"
             "\t\tdefault:\n"
             "\t\t{\n"
             "\t\t\tint num;\n"
             "\t\t\tif (num == 1)\n"
             "\t\t\t{\n"
             "\t\t\t\tbuffCondition = new ActiveId_BuffCondition(target, rhs, op);\n"
             "\t\t\t\tbreak;\n"
             "\t\t\t}\n"
             "\t\t\tfor (int i = 0; i < _customFactories.Count; i++)\n"
             "\t\t\t{\n"
             "\t\t\t\tbuffCondition = _customFactories[i].CreateCondition(target, key, op, rhs);\n"
             "\t\t\t\tif (buffCondition != null)\n"
             "\t\t\t\t{\n"
             "\t\t\t\t\tbreak;\n"
             "\t\t\t\t}\n"
             "\t\t\t}\n"
             "\t\t\tif (buffCondition == null)\n"
             "\t\t\t{\n"
             "\t\t\t\tbuffCondition = new BuffFloatCondition(BuffValueFactory.Instance.CreateFloatValue(lhs), BuffValueFactory.Instance.CreateFloatValue(rhs), op);\n"
             "\t\t\t}\n"
             "\t\t\tbreak;\n"
             "\t\t}\n"
             "\t\tcase \"status\":\n"
             "\t\t\tbuffCondition = new ContainsBuff_BuffCondition(target, rhs, op);\n"
             "\t\t\tbreak;\n"
             "\t\t}\n",
             ["case \"activeId\":", "case \"status\":", "BuffFloatCondition"]),
            (Path("CustomStoreScreenPresentation.cs"),
             "\t\tswitch (_currentTabId)\n"
             "\t\t{\n"
             "\t\tdefault:\n"
             "\t\t{\n"
             "\t\t\tint num;\n"
             "\t\t\tif (num == 1)\n"
             "\t\t\t{\n"
             "\t\t\t\tTransformersMenuBar.Instance.SetSoftCurrencyTab(\"Raidchips\");\n"
             "\t\t\t}\n"
             "\t\t\telse\n"
             "\t\t\t{\n"
             "\t\t\t\tTransformersMenuBar.Instance.SetSoftCurrencyTab(string.Empty);\n"
             "\t\t\t}\n"
             "\t\t\tbreak;\n"
             "\t\t}\n"
             "\t\tcase \"loyalty\":\n"
             "\t\t\tTransformersMenuBar.Instance.SetSoftCurrencyTab(\"Loyalty\");\n"
             "\t\t\tbreak;\n"
             "\t\t}\n",
             ["case \"vs\":", "string.Empty"]),
            (Path("CustomRedeemerMappings/ResourceRedeemerMapping.cs"),
             "\t\tGameObject gameObject = null;\n"
             "\t\tint num;\n"
             "\t\tgameObject = resourceType switch\n"
             "\t\t{\n"
             "\t\t\t\"sc\" => Resources.Load(\"UI/Misc/SoftCurrencyResourceTrail\") as GameObject, \n"
             "\t\t\t_ => (num != 1) ? (Resources.Load(\"UI/Misc/GenericResourceTrail\") as GameObject) : (Resources.Load(\"UI/Misc/HardCurrencyResourceTrail\") as GameObject), \n"
             "\t\t};\n",
             ["resourceType == \"hc\"", "GenericResourceTrail"]),
            (Path("Legacy/ActiveQuest.cs"),
             "\t\tswitch (text3)\n"
             "\t\t{\n"
             "\t\tdefault:\n"
             "\t\t{\n"
             "\t\t\tint num12;\n"
             "\t\t\tif (num12 == 1)\n"
             "\t\t\t{\n"
             "\t\t\t\tphase = Phase.Defend;\n"
             "\t\t\t}\n"
             "\t\t\telse\n"
             "\t\t\t{\n"
             "\t\t\t\tphase = Phase.Attack;\n"
             "\t\t\t}\n"
             "\t\t\tbreak;\n"
             "\t\t}\n"
             "\t\tcase \"placement\":\n"
             "\t\t\tphase = Phase.Placement;\n"
             "\t\t\tbreak;\n"
             "\t\t}\n",
             ["case \"defend\":", "Phase.Attack"]),
            (Path("EB.UI.Gacha/GachaPurchasePresentation.cs"),
             "\t\tswitch (tabId)\n"
             "\t\t{\n"
             "\t\tdefault:\n"
             "\t\t{\n"
             "\t\t\tint num;\n"
             "\t\t\tif (num == 1)\n"
             "\t\t\t{\n"
             "\t\t\t\t_currentTab = GachaTab.SPECIAL;\n"
             "\t\t\t}\n"
             "\t\t\telse\n"
             "\t\t\t{\n"
             "\t\t\t\t_currentTab = GachaTab.CRYSTAL;\n"
             "\t\t\t}\n"
             "\t\t\tbreak;\n"
             "\t\t}\n"
             "\t\tcase \"shards\":\n"
             "\t\t\t_currentTab = GachaTab.SHARDS;\n"
             "\t\t\tbreak;\n"
             "\t\t}\n",
             ["case \"special\":", "GachaTab.CRYSTAL"]),
        ]
        for path, source, markers in fixtures:
            repaired, changes = normalize_source_contracts(path, source)
            self.assertTrue(changes, path)
            for marker in markers:
                self.assertIn(marker, repaired, path)
            self.assertEqual(normalize_source_contracts(path, repaired), (repaired, []), path)

        alliance = (
            "\t\t\t\t\t\t\t\t\tswitch (err)\n"
            "\t\t\t\t\t\t\t\t\t{\n"
            "\t\t\t\t\t\t\t\t\tdefault:\n"
            "\t\t\t\t\t\t\t\t\t{\n"
            "\t\t\t\t\t\t\t\t\t\tint num;\n"
            "\t\t\t\t\t\t\t\t\t\tif (num == 1)\n"
            "\t\t\t\t\t\t\t\t\t\t{\n"
            "\t\t\t\t\t\t\t\t\t\t\tAllianceUtil.GoToAlliance();\n"
            "\t\t\t\t\t\t\t\t\t\t}\n"
            "\t\t\t\t\t\t\t\t\t\tbreak;\n"
            "\t\t\t\t\t\t\t\t\t}\n"
            "\t\t\t\t\t\t\t\t\tcase \"privateAlliance\":\n"
            "\t\t\t\t\t\t\t\t\tcase \"nonJoinableAlliance\":\n"
            "\t\t\t\t\t\t\t\t\tcase \"full\":\n"
            "\t\t\t\t\t\t\t\t\t\tbreak;\n"
            "\t\t\t\t\t\t\t\t\t}\n"
            "\t\t\t\t\t\t\t}));\n"
        )
        repaired, changes = normalize_source_contracts(Path("AllianceMembersState.cs"), alliance)
        self.assertIn('case "notFound":', repaired)
        self.assertNotIn("int num;", repaired)
        self.assertTrue(changes)

    def test_dependency_order_uses_project_references(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            for name, reference in (("Game", "Firstpass"), ("Firstpass", "Fabric"),
                                    ("Fabric", "")):
                project_dir = root / "source" / name
                project_dir.mkdir(parents=True)
                (project_dir / f"{name}.csproj").write_text(
                    "<Project>" + (f'<ItemGroup><Reference Include="{reference}" /></ItemGroup>'
                                   if reference else "") + "</Project>")
            self.assertEqual(dependency_order(root, ["Game", "Firstpass", "Fabric"]),
                             ["Fabric", "Firstpass", "Game"])

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
