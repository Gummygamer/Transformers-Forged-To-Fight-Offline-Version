"""Synthetic fixtures only; no game source or binaries are test inputs."""
import contextlib
import io
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
import zipfile

from decompilation import (assembly_entries, compile_audit, compile_candidate_paths, dependency_order, digest,
                           export_il, metadata_api_surface, metadata_contract_diff,
                           normalize_accessors, normalize_source_contracts, replacement_closure,
                           reference_identity, reference_inventory, replacement_plan,
                           source_declaration_inventory, substitution_candidate)


def synthetic_metadata(name, version="1.0.0.0", references=()):
    return {"identity": {"name": name, "version": version, "culture": "",
                         "public_key": "", "flags": 0},
            "references": [{"name": dependency, "version": required, "culture": "",
                            "public_key_or_token": "", "flags": 0, "hash": ""}
                           for dependency, required in references],
            "resources": [], "types": {}}


class RecoveryTests(unittest.TestCase):
    def test_reference_identity_normalizes_full_keys_and_tokens(self):
        definition = {"name": "Library", "version": "1.0.0.0", "culture": "",
                      "public_key": "00000000000000000400000000000000", "flags": 1}
        expected = {"name": "Library", "version": "1.0.0.0", "culture": "",
                    "public_key_token": "B77A5C561934E089"}
        self.assertEqual(reference_identity(definition, definition=True), expected)
        reference = {**definition, "public_key_or_token": definition["public_key"]}
        self.assertEqual(reference_identity(reference), expected)
        reference.update(flags=0, public_key_or_token="b77a5c561934e089")
        self.assertEqual(reference_identity(reference), expected)

    def test_candidate_checks_retained_consumers_and_baseline_gaps(self):
        original = {
            "Game": synthetic_metadata("Game", references=[("Framework", "1.0.0.0")]),
            "Framework": synthetic_metadata("Framework"),
            "Plugin": synthetic_metadata("Plugin", references=[("Game", "1.0.0.0"),
                                                                 ("External", "1.0.0.0")])}
        compiled = {"Game": synthetic_metadata("Game", "2.0.0.0",
                                              [("Framework", "3.0.0.0"), ("New", "1.0.0.0")])}
        result = substitution_candidate(original, compiled)
        self.assertEqual(result["replacement_set"], ["Game"])
        self.assertEqual(result["preserved_originals"], ["Framework", "Plugin"])
        refs = result["reference_inventory"]
        self.assertEqual(refs["original_baseline"]["counts"],
                         {"exact-identity-match": 2, "missing-provider": 1})
        self.assertEqual(refs["candidate"]["counts"],
                         {"identity-mismatch": 2, "missing-provider": 2})
        self.assertEqual([(r["consumer"], r["provider"]) for r in refs["introduced_or_changed_issues"]],
                         [("Game", "Framework"), ("Game", "New"), ("Plugin", "Game")])
        self.assertEqual(refs["retained_consumers_of_replacements"][0]["status"], "identity-mismatch")
        self.assertEqual(refs["added_edges"][0]["provider"], "New")
        self.assertFalse(result["comparisons"]["Game"]["equal"])
        self.assertFalse(result["packaging_authorized"])

    def test_exact_identity_matches_do_not_approve_candidate(self):
        original = {"Game": synthetic_metadata("Game")}
        result = substitution_candidate(original, original)
        self.assertTrue(result["comparisons"]["Game"]["equal"])
        self.assertEqual(result["status"], "review-required")
        self.assertFalse(result["runtime_verified"])

    def test_reference_inventory_detects_culture_and_key_drift(self):
        metadata = {"Game": synthetic_metadata("Game", references=[("Library", "1.0.0.0")]),
                    "Library": synthetic_metadata("Library")}
        metadata["Library"]["identity"].update(
            culture="fr", public_key="00000000000000000400000000000000")
        edge = reference_inventory(metadata)["edges"][0]
        self.assertEqual(edge["differing_identity_fields"], ["culture", "public_key_token"])

    def test_candidate_output_provenance_and_rejections(self):
        with tempfile.TemporaryDirectory() as directory:
            workspace = Path(directory)
            audit = workspace / "compile-fixture"
            (audit / "bin").mkdir(parents=True)
            output = audit / "bin" / "Game.dll"
            output.write_bytes(b"synthetic output")
            row = {"name": "Game", "status": "compiled", "exit_code": 0,
                   "output_path": "bin/Game.dll", "output_sha256": digest(output)}
            report = audit / "report.json"
            report.write_text(json.dumps({"assemblies": [row]}))
            paths, provenance = compile_candidate_paths(workspace, report, ["Game"])
            self.assertEqual(paths["Game"]["path"], output)
            self.assertEqual(provenance["sha256"], digest(report))
            for replacement, message in [
                ({"status": "failed"}, "No successful"),
                ({"exit_code": 1}, "No successful"),
                ({"output_path": "../managed/Game.dll"}, "isolated audit"),
                ({"output_sha256": "wrong"}, "changed since audit")]:
                with self.subTest(replacement=replacement):
                    report.write_text(json.dumps({"assemblies": [{**row, **replacement}]}))
                    with self.assertRaisesRegex(ValueError, message):
                        compile_candidate_paths(workspace, report, ["Game"])
            report.write_text(json.dumps({"assemblies": [row, row]}))
            with self.assertRaisesRegex(ValueError, "Duplicate"):
                compile_candidate_paths(workspace, report, ["Game"])
            report.write_text(json.dumps({"assemblies": [row]}))
            with self.assertRaisesRegex(ValueError, "No successful"):
                compile_candidate_paths(workspace, report, ["Missing"])
            output.write_bytes(b"changed")
            with self.assertRaisesRegex(ValueError, "changed since audit"):
                compile_candidate_paths(workspace, report, ["Game"])

    def test_replacement_plan_selects_only_explicit_roots(self):
        with tempfile.TemporaryDirectory() as directory:
            workspace = Path(directory)
            (workspace / "managed").mkdir()
            audit = workspace / "compile-fixture"
            (audit / "bin").mkdir(parents=True)
            manifest_rows, audit_rows = [], []
            for name in ("Assembly-CSharp", "Library"):
                original = workspace / "managed" / f"{name}.dll"
                original.write_bytes(b"original fixture")
                manifest_rows.append({"name": original.name, "sha256": digest(original)})
                output = audit / "bin" / original.name
                output.write_bytes(b"compiled fixture")
                audit_rows.append({"name": name, "status": "compiled", "exit_code": 0,
                                   "output_path": f"bin/{name}.dll", "output_sha256": digest(output)})
            (workspace / "manifest.json").write_text(json.dumps({"assemblies": manifest_rows}))
            compile_report = audit / "report.json"
            compile_report.write_text(json.dumps({"assemblies": audit_rows}))
            def inspect(command, **kwargs):
                return json.dumps(synthetic_metadata(Path(command[-1]).stem))
            with patch("decompilation.subprocess.check_output", side_effect=inspect), contextlib.redirect_stdout(io.StringIO()):
                self.assertEqual(replacement_plan(workspace, "dotnet", Path("helper.dll"),
                                                  ["Assembly-CSharp"], compile_report), 0)
            report = json.loads(next(workspace.glob("replacement-*/report.json")).read_text())
            candidate = report["substitution_candidate"]
            self.assertEqual(candidate["replacement_set"], ["Assembly-CSharp"])
            self.assertEqual(candidate["preserved_originals"], ["Library"])
            self.assertEqual(candidate["files"][1]["selected_sha256"], manifest_rows[1]["sha256"])
            self.assertEqual(candidate["files"][0]["apk_path"],
                             "assets/bin/Data/Managed/Assembly-CSharp.dll")

    def test_replacement_closure_follows_only_retained_references(self):
        refs = {"Game": ["FirstPass", "UnityEngine"], "FirstPass": ["crypto"],
                "UnityEngine": ["System"], "crypto": ["mscorlib"], "System": ["mscorlib"]}
        self.assertEqual(replacement_closure(["Game"], refs),
                         {"Game", "FirstPass", "UnityEngine", "crypto", "System", "mscorlib"})

    def test_source_inventory_is_explicitly_shallow(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / "Example.cs").write_text(
                "namespace Demo; public class Bot { private int health; public void Attack() {} }\n")
            result = source_declaration_inventory(root)
        self.assertEqual(result["types"], ["Bot"])
        self.assertEqual(result["methods"], ["Attack"])
        self.assertEqual(result["fields"], ["health"])

    def test_metadata_api_surface_keeps_exact_contracts_separate(self):
        metadata = {
            "identity": {"name": "Example"}, "references": [], "resources": [],
            "metadata_version": "v2.0.50727", "machine": "I386", "cor_flags": "ILOnly",
            "module_name": "Example.dll", "assembly_attributes": [], "module_attributes": [],
            "types": {
                "Demo.Bot": {
                    "flags": 1, "base_type": "[mscorlib]System.Object", "attributes": [],
                    "interfaces": [], "layout": {"size": -1, "packing": 0},
                    "field_order": ["health"], "method_impls": [],
                    "fields": {"health": {"signature": "I4", "flags": 3, "offset": 4,
                                            "attributes": []}},
                    "methods": {"Attack:I4:0:0(System.String)": {
                        "flags": 6, "attributes": [], "parameters": []}},
                    "properties": [], "events": [],
                }
            }
        }
        surface = metadata_api_surface(metadata)
        self.assertEqual(surface["assembly"]["resources"], [])
        bot = surface["types"][0]
        self.assertEqual(bot["base_type"], "[mscorlib]System.Object")
        self.assertEqual(bot["visibility"], "public")
        self.assertEqual(bot["serialization_layout"]["field_order"], ["health"])
        self.assertEqual(bot["fields"][0]["visibility"], "assembly")
        self.assertEqual(bot["methods"][0]["signature"], "I4:0:0(System.String)")
        self.assertEqual(bot["methods"][0]["visibility"], "public")

    def test_metadata_contract_diff_reports_api_and_layout_changes(self):
        original = {
            "identity": {"name": "Example", "version": "1.0.0.0"}, "metadata_version": "v2", "machine": "I386",
            "cor_flags": "ILOnly", "module_name": "Example.dll", "assembly_attributes": [],
            "module_attributes": [], "references": [{"name": "mscorlib", "version": "2.0.0.0"}],
            "resources": [], "types": {
                "Demo.Bot": {"flags": 1, "base_type": "Object", "layout": {"size": -1},
                              "generics": [], "attributes": [], "interfaces": [],
                              "method_impls": [], "field_order": ["health"],
                              "fields": {"health": {"signature": "I4", "flags": 3}},
                              "methods": {"Attack:I4": {"flags": 6,
                                                          "import": {"module": "old.so"}}},
                              "properties": [], "events": []}
            }
        }
        compiled = json.loads(json.dumps(original))
        compiled["references"][0]["version"] = "3.5.0.0"
        compiled["identity"]["version"] = "2.0.0.0"
        compiled["metadata_version"] = "v4.0.30319"
        compiled["machine"] = "AMD64"
        compiled["resources"] = [{"name": "config", "flags": 1, "embedded": True}]
        compiled["types"]["Demo.Bot"]["fields"]["health"]["flags"] = 6
        compiled["types"]["Demo.Bot"]["layout"]["size"] = 8
        compiled["types"]["Demo.Bot"]["field_order"] = ["health", "extra"]
        compiled["types"]["Demo.Bot"]["base_type"] = "Other.Base"
        compiled["types"]["Demo.Bot"]["methods"]["Attack:I4"]["import"] = {"module": "new.so"}
        compiled["types"]["Demo.Bot"]["methods"]["Attack:I4"]["flags"] = 38
        diff = metadata_contract_diff(original, compiled)
        self.assertFalse(diff["equal"])
        self.assertIn("references", diff["differences"])
        self.assertEqual(diff["summary"]["changed_field_count"], 1)
        self.assertEqual(diff["summary"]["changed_method_count"], 1)
        risks = diff["classification"]["loader_risk_differences"]
        self.assertEqual(risks["assembly_identity"], ["version"])
        self.assertEqual(risks["metadata_profile"], ["machine", "metadata_version"])
        self.assertEqual(risks["references"]["framework_changed"], ["mscorlib"])
        self.assertEqual(risks["resources"]["added"], ["config"])
        self.assertEqual(risks["inheritance_or_interfaces"], ["Demo.Bot"])
        self.assertEqual(risks["serialization_layout"], ["Demo.Bot"])
        self.assertEqual(risks["native_imports"], ["Demo.Bot::Attack:I4"])

    def test_metadata_diff_classifies_generated_and_framework_changes(self):
        original = {
            "identity": {"name": "Example"}, "metadata_version": "v2", "machine": "I386",
            "cor_flags": "ILOnly", "module_name": "Example.dll", "assembly_attributes": [],
            "module_attributes": [], "references": [{"name": "mscorlib", "version": "2.0.0.0"}],
            "resources": [], "types": {
                "<PrivateImplementationDetails>+$ArrayType$8": {
                    "flags": 1, "base_type": "Object", "layout": {"size": 8},
                    "generics": [], "attributes": [], "interfaces": [], "method_impls": [],
                    "field_order": [], "fields": {}, "methods": {}, "properties": [], "events": []
                },
                "Demo.Bot": {"flags": 1, "base_type": "Object", "layout": {"size": -1},
                              "generics": [], "attributes": [], "interfaces": [], "method_impls": [],
                              "field_order": [], "fields": {}, "methods": {}, "properties": [], "events": []}
            }
        }
        compiled = json.loads(json.dumps(original))
        compiled["references"][0]["version"] = "8.0.0.0"
        compiled["types"]["<PrivateImplementationDetails>+$ArrayType$8"] = compiled["types"].pop("Demo.Bot")
        compiled["types"]["Demo.Bot+<>c"] = compiled["types"].pop("<PrivateImplementationDetails>+$ArrayType$8")
        diff = metadata_contract_diff(original, compiled)
        classification = diff["classification"]
        self.assertEqual(classification["framework_reference_differences"]["mscorlib"]["original"]["version"], "2.0.0.0")
        self.assertEqual(classification["compiler_generated_type_differences"]["added_count"], 1)
        self.assertEqual(classification["compiler_generated_type_differences"]["removed_count"], 1)

    def test_metadata_diff_classifies_public_and_non_public_api_changes(self):
        original = {
            "identity": {"name": "Example"}, "metadata_version": "v2", "machine": "I386",
            "cor_flags": "ILOnly", "module_name": "Example.dll", "assembly_attributes": [],
            "module_attributes": [], "references": [], "resources": [], "types": {
                "Demo.PublicBot": {
                    "flags": 1, "base_type": "Object", "layout": {"size": -1},
                    "generics": [], "attributes": [], "interfaces": [], "method_impls": [],
                    "field_order": ["health"],
                    "fields": {"health": {"signature": "I4", "flags": 6}},
                    "methods": {"Attack:I4": {"flags": 6}, "get_Health:I4": {"flags": 6},
                                "add_Hit:void": {"flags": 6}, "add_Died:void": {"flags": 3}},
                    "properties": [{"name": "Health", "signature": "I4", "flags": 0,
                                    "getter": "Demo.PublicBot::get_Health:I4"}],
                    "events": [{"name": "Hit", "type": "Demo.HitEvent", "flags": 0,
                                "adder": "Demo.PublicBot::add_Hit:void"}],
                },
                "Demo.InternalBot": {
                    "flags": 0, "base_type": "Object", "layout": {"size": -1},
                    "generics": [], "attributes": [], "interfaces": [], "method_impls": [],
                    "field_order": [], "fields": {}, "methods": {}, "properties": [], "events": [],
                },
            }
        }
        compiled = json.loads(json.dumps(original))
        compiled["types"]["Demo.PublicBot"]["fields"]["newHealth"] = {
            "signature": "I4", "flags": 3
        }
        compiled["types"]["Demo.PublicBot"]["methods"]["Defend:I4"] = {"flags": 6}
        compiled["types"]["Demo.PublicBot"]["methods"]["get_Health:I4"]["flags"] = 1
        compiled["types"]["Demo.PublicBot"]["events"].append(
            {"name": "Died", "type": "Demo.DeathEvent", "flags": 0,
             "adder": "Demo.PublicBot::add_Died:void"})
        compiled["types"].pop("Demo.InternalBot")
        compiled["types"]["Demo.AddedBot"] = {
            "flags": 1, "base_type": "Object", "layout": {"size": -1},
            "generics": [], "attributes": [], "interfaces": [], "method_impls": [],
            "field_order": [], "fields": {}, "methods": {}, "properties": [], "events": [],
        }
        classification = metadata_contract_diff(original, compiled)["classification"]
        visibility = classification["api_visibility_differences"]
        self.assertEqual(visibility["types"]["added"]["public"], ["Demo.AddedBot"])
        self.assertEqual(visibility["types"]["removed"]["non-public"], ["Demo.InternalBot"])
        self.assertEqual(visibility["members"]["added"]["public"],
                         ["Demo.PublicBot::methods:Defend:I4"])
        self.assertEqual(visibility["members"]["added"]["non-public"],
                         ["Demo.PublicBot::events:Died:Demo.DeathEvent",
                          "Demo.PublicBot::fields:newHealth"])
        self.assertEqual(visibility["visibility_changed"], [{
            "kind": "methods", "name": "Demo.PublicBot::get_Health:I4",
            "original": "public", "compiled": "non-public"}, {
            "kind": "properties", "name": "Demo.PublicBot::Health:I4",
            "original": "public", "compiled": "non-public"}])

    def test_property_event_visibility_comes_from_accessors(self):
        type_info = {"flags": 1, "fields": {}, "methods": {
            "get_Value:I4": {"flags": 1}, "set_Value:void": {"flags": 6},
            "add_Hit:void": {"flags": 4}, "remove_Hit:void": {"flags": 1}},
            "properties": [], "events": []}
        original = {"references": [], "resources": [], "types": {"Demo.Bot": type_info}}
        compiled = json.loads(json.dumps(original))
        bot = compiled["types"]["Demo.Bot"]
        bot["properties"] = [
            {"name": "Value", "signature": "I4", "flags": 0x200,
             "getter": "Demo.Bot::get_Value:I4", "setter": "Demo.Bot::set_Value:void"},
            {"name": "Missing", "signature": "I4", "flags": 0,
             "getter": "Demo.Bot::get_Missing:I4"},
            {"name": "NoAccessors", "signature": "I4", "flags": 0}]
        bot["events"] = [{"name": "Hit", "type": "Demo.HitEvent", "flags": 0,
                          "adder": "Demo.Bot::add_Hit:void",
                          "remover": "Demo.Bot::remove_Hit:void"}]
        diff = metadata_contract_diff(original, compiled)
        added = diff["classification"]["api_visibility_differences"]["members"]["added"]
        self.assertEqual(added["public"], ["Demo.Bot::events:Hit:Demo.HitEvent",
                                           "Demo.Bot::properties:Value:I4"])
        self.assertEqual(added["unknown"], ["Demo.Bot::properties:Missing:I4",
                                            "Demo.Bot::properties:NoAccessors:I4"])
        self.assertEqual(added["non-public"], [])
        self.assertEqual(diff["differences"]["types"]["changed"]["Demo.Bot"]["properties"]["compiled"],
                         bot["properties"])
        # Accessor-only changes leave the property/event rows identical.
        original = json.loads(json.dumps(compiled))
        bot["methods"]["set_Value:void"]["flags"] = 1
        bot["methods"]["add_Hit:void"]["flags"] = 1
        changes = metadata_contract_diff(original, compiled)["classification"]["api_visibility_differences"]
        self.assertIn({"kind": "events", "name": "Demo.Bot::Hit:Demo.HitEvent",
                       "original": "public", "compiled": "non-public"}, changes["visibility_changed"])
        self.assertIn({"kind": "properties", "name": "Demo.Bot::Value:I4",
                       "original": "public", "compiled": "non-public"}, changes["visibility_changed"])

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

    def test_explicit_offline_runtime_repairs_are_opt_in(self):
        setup = 'ApiEndPoint = EB.Version.GetApiEndPoint("Default.Prod");\n'
        repaired, changes = normalize_source_contracts(
            Path("Setup.cs"), setup, "http://127.0.0.1:8080")
        self.assertIn('ApiEndPoint = "http://127.0.0.1:8080";', repaired)
        self.assertEqual(len(changes), 1)

        hub = "if (Config.UseGooglePlayGames && !flag)\n"
        repaired, changes = normalize_source_contracts(
            Path("EB.Sparx/Hub.cs"), hub, disable_google_play_games=True)
        self.assertIn("if (false && Config.UseGooglePlayGames && !flag)", repaired)
        self.assertEqual(len(changes), 1)
        self.assertEqual(normalize_source_contracts(Path("EB.Sparx/Hub.cs"), hub), (hub, []))

        inventory = ("Action<int, string, Hashtable> callback2 = "
                     "default(Action<int, string, Hashtable>);\n")
        repaired, changes = normalize_source_contracts(
            Path("EB.Sparx/InventoryAPI.cs"), inventory)
        self.assertIn("callback2 = callback;", repaired)
        self.assertEqual(len(changes), 1)

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

    def test_unity_repairs_preserve_absent_operators_and_interface_bodies(self):
        hash_source = ("namespace UnityEngine;\npublic struct Hash128\n{\n"
                       "\tpublic static bool operator ==(Hash128 hash1, Hash128 hash2)\n"
                       "\t{\n\t\treturn hash1.m == hash2.m;\n\t}\n}\n")
        repaired, changes = normalize_source_contracts(Path("Hash128.cs"), hash_source)
        self.assertEqual((repaired, changes), (hash_source, []))

        network_source = ("namespace UnityEngine.Networking;\npublic struct NetworkSceneId\n{\n"
                          "\tpublic static bool operator ==(NetworkSceneId c1, NetworkSceneId c2)\n"
                          "\t{\n\t\treturn c1.m == c2.m;\n\t}\n}\n")
        repaired, changes = normalize_source_contracts(Path("NetworkSceneId.cs"), network_source)
        self.assertEqual((repaired, changes), (network_source, []))

        ui_source = ("namespace UnityEngine.UI;\npublic class Graphic\n{\n"
                     "\tvirtual bool ICanvasElement.IsDestroyed()\n\t{\n"
                     "\t\treturn IsDestroyed();\n\t}\n"
                     "\tvirtual Transform ICanvasElement.get_transform()\n\t{\n"
                     "\t\treturn base.transform;\n\t}\n}\n")
        repaired, changes = normalize_source_contracts(Path("Graphic.cs"), ui_source)
        self.assertNotIn("virtual bool ICanvasElement", repaired)
        self.assertNotIn("virtual Transform ICanvasElement", repaired)
        self.assertEqual(len(changes), 1)

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

    def test_il_export_rejects_changed_input_before_tool_invocation(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            (root / "managed").mkdir()
            assembly = root / "managed/Example.dll"
            assembly.write_bytes(b"changed")
            (root / "manifest.json").write_text(json.dumps({
                "backend": "mono",
                "assemblies": [{"name": "Example.dll", "sha256": "0" * 64}],
            }))
            with self.assertRaisesRegex(ValueError, "Assembly changed"):
                export_il(root, "missing-ilspy")

    def test_il_export_snapshot_and_failure_provenance(self):
        for scenario in ("success", "empty", "failure", "frozen-change", "original-change"):
            with self.subTest(scenario=scenario), tempfile.TemporaryDirectory() as folder:
                root = Path(folder)
                (root / "managed").mkdir()
                original = root / "managed/Example.dll"
                original.write_bytes(b"synthetic assembly")
                manifest_path = root / "manifest.json"
                manifest_path.write_text(json.dumps({
                    "backend": "mono", "input": {"sha256": "synthetic APK hash"},
                    "assemblies": [{"name": original.name, "sha256": digest(original)}],
                }))
                manifest_bytes = manifest_path.read_bytes()
                manifest_hash = digest(manifest_path)

                def version(*args, **kwargs):
                    # A concurrent compile audit may update this mutable file.
                    manifest_path.write_text('{"compilation": "updated"}')
                    return "synthetic ILSpy version"

                def disassemble(command, **kwargs):
                    frozen = Path(command[-1])
                    self.assertNotEqual(frozen, original)
                    self.assertEqual(frozen.read_bytes(), original.read_bytes())
                    self.assertEqual(command[1:3], ["--disable-updatecheck", "-il"])
                    if scenario != "empty":
                        kwargs["stdout"].write("// synthetic IL fixture\n")
                    if scenario == "frozen-change":
                        frozen.write_bytes(b"modified snapshot")
                    if scenario == "original-change":
                        original.write_bytes(b"modified input")
                    return type("Result", (), {"returncode": int(scenario == "failure")})()

                with patch("decompilation.shutil.which", return_value="synthetic-ilspy"), \
                        patch("decompilation.subprocess.check_output", side_effect=version), \
                        patch("decompilation.subprocess.run", side_effect=disassemble), \
                        contextlib.redirect_stdout(io.StringIO()):
                    self.assertEqual(export_il(root, "ilspy"), int(scenario != "success"))
                report_path = next(root.glob("il-*/report.json"))
                report = json.loads(report_path.read_text())
                self.assertEqual(report["manifest_sha256"], manifest_hash)
                self.assertEqual((report_path.parent / "manifest.json").read_bytes(), manifest_bytes)
                self.assertEqual(report["status"], "exported" if scenario == "success" else "partial")
                self.assertFalse(report["runtime_verified"])
                row = report["assemblies"][0]
                self.assertEqual(row["inputs_unchanged"], not scenario.endswith("-change"))
                self.assertEqual(row["il_sha256"], digest(report_path.parent / row["il_path"]))


if __name__ == "__main__":
    unittest.main()
