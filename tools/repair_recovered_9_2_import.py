#!/usr/bin/env python3
"""Repair diagnosed recovered IL and import metadata for Unity Android builds."""

from __future__ import annotations

import argparse
import gzip
import json
import os
import re
import subprocess
import tempfile
from pathlib import Path





def il2cpp_failure_plan_entry(assembly_name: str, signature: str):
    owner_method = signature.rsplit("::", 1)
    if len(owner_method) != 2 or "(" not in owner_method[1] or " " not in owner_method[0]:
        return None
    type_name = owner_method[0].rsplit(" ", 1)[1]
    method_name, parameter_blob = owner_method[1].split("(", 1)
    parameter_blob = parameter_blob.rsplit(")", 1)[0]
    if method_name in (".ctor", ".cctor"):
        return None
    depth = 0
    parameter_count = 0 if not parameter_blob.strip() else 1
    for character in parameter_blob:
        if character == "<": depth += 1
        elif character == ">": depth -= 1
        elif character == "," and depth == 0: parameter_count += 1
    return (assembly_name, type_name, method_name, parameter_count, signature)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("project", type=Path, help="staged 9.2 Unity project")
    parser.add_argument("--unity", type=Path, required=True, help="Unity Editor executable")
    parser.add_argument("--diagnostics", type=Path, help="Unity log whose malformed constructor errors should be repaired")
    args = parser.parse_args()

    project = args.project.expanduser().resolve()
    unity = args.unity.expanduser().resolve()
    if not (project / "ProjectSettings/ProjectVersion.txt").is_file():
        parser.error(f"not a Unity project: {project}")
    plugin_dir = project / "Assets/Plugins"
    if not (plugin_dir / "Assembly-CSharp.dll").is_file():
        parser.error(f"recovered game assembly is missing: {plugin_dir / 'Assembly-CSharp.dll'}")

    editor_root = unity.parent
    mono_root = editor_root / "Data/MonoBleedingEdge"
    mcs = mono_root / "bin/mcs"
    mono = mono_root / "bin/mono"
    core_library = mono_root / "lib/mono/4.5/mscorlib.dll"
    system_library = mono_root / "lib/mono/4.5/System.dll"
    mono_security = mono_root / "lib/mono/4.5/Mono.Security.dll"
    cecil_gac = mono_root / "lib/mono/gac/Mono.Cecil"
    cecil_versions = list(cecil_gac.glob("*/Mono.Cecil.dll"))
    def version_key(path: Path) -> tuple[int, ...]:
        match = re.match(r"([0-9]+(?:\.[0-9]+)+)_", path.parent.name)
        return tuple(int(part) for part in match.group(1).split(".")) if match else ()
    cecil_versions.sort(key=version_key, reverse=True)
    cecil = cecil_versions[0] if cecil_versions else cecil_gac / "0.10.0.0__0738eb9f132ed756/Mono.Cecil.dll"
    if not all(path.is_file() for path in (mcs, mono, cecil, core_library, system_library, mono_security)):
        parser.error(f"Unity's bundled Mono compiler or Mono.Cecil is missing under {mono_root}")

    targets: dict[tuple[str, str], int] = {
        ("Quests.Presentation.QuestNodeTuning", ".ctor"): 43,
        ("Quests.Presentation.GameboardBuilder", ".ctor"): -1,
        ("Quests.Presentation.GameboardBuilder", ".cctor"): -1,
        ("AVEBattlegroupSelectionPanel", ".ctor"): -1,
        ("EB.Rendering.EBParticlePal", ".ctor"): -1,
        ("EB.Rendering.EBParticlePal", ".cctor"): -1,
        ("EB.Rendering.EBParticlePal/Condition", ".ctor"): -1,
        ("UILabel", ".cctor"): -1,
        ("EB.UI.PrefabDiff.PrefabDiffTracker", ".cctor"): -1,
        ("EB.Localizer", ".cctor"): -1,
        ("EB.Localizer", "add_OnLocalizationChanged"): -1,
        ("EB.UI.Social.FuseSocialHub", ".cctor"): -1,
        ("EB.UI.SystemMessage.FuseSystemMessageOverlay", ".cctor"): -1,
        ("EB.UI.Social.FuseSocialHub/FuseSocialHubPresentationConfig", ".ctor"): -1,
        ("EB.UI.SystemMessage.FuseSystemMessageOverlay/SystemMessagePresentationConfig", ".ctor"): -1,
        ("EBWorldPainterData", ".cctor"): -1,
        ("AudioPal", ".cctor"): -1,
        ("BuffsController", ".cctor"): -1,
        ("EB.MoveEditor.PrefabLib", ".cctor"): -1,
        ("CriticalError", ".cctor"): -1,
        ("CriticalError/ConfigData", ".ctor"): -1,
        ("CriticalError/ConfigData/<>c", ".cctor"): -1,
        ("EZAnimation", "GetInterpolator"): -1,
        ("EZAnimation", ".cctor"): -1,
        ("AITuneables", "OnAfterDeserialize"): -1,
        ("AITuneablesSet", "SetupTuneables_Internal"): -1,
        ("AIRageSettings", "Init"): -1,
        ("Fabric.SerializableDictionary`2", "OnBeforeSerialize"): -1,
        ("WindowStateHelper", ".ctor"): -1,
        ("BadgeManager", ".ctor"): -1,
        ("BuildingPortrait", ".ctor"): -1,
        ("AllianceStatsPopup", ".ctor"): -1,
        ("GachaRevealPresentation", ".ctor"): -1,
        ("Facebook.Unity.Settings.FacebookSettings/UrlSchemes", ".ctor"): -1,
        ("EB.Hash", ".cctor"): -1,
        ("EB.Hash", "FNV64"): -1,
        ("EB.SafeValue", "Init"): -1,
        ("EB.Core.ThreadSafeRandom", "get_Randomizer"): -1,
    }
    il2cpp_invalid_methods: set[tuple[str, str, str, int, str]] = set()
    report_path = project / "recovered-import-repairs.json"
    # Diagnostic failures are useful for probing Unity's importer, but their
    # throwing stubs must never leak into a normal recovery run. Only replay
    # the previous failure plan when diagnostics are explicitly requested.
    if args.diagnostics and report_path.is_file():
        try:
            previous_report = json.loads(report_path.read_text())
            for entry in previous_report.get("unsupported_il2cpp_methods", []):
                assembly_name = entry.get("assembly", "")
                signature = entry.get("signature", "")
                if assembly_name and signature:
                    plan_entry = il2cpp_failure_plan_entry(assembly_name, signature)
                    if plan_entry is not None:
                        il2cpp_invalid_methods.add(plan_entry)
        except (OSError, json.JSONDecodeError):
            pass
    if args.diagnostics:
        diagnostic_path = args.diagnostics.expanduser().resolve()
        if diagnostic_path.suffix == ".gz":
            with gzip.open(diagnostic_path, "rt", errors="replace") as stream:
                log = stream.read()
        else:
            log = diagnostic_path.read_text(errors="replace")
        for signature, assembly_path in re.findall(
            r"IL2CPP error for method '([^']+)' in assembly '([^']+)'", log
        ):
            plan_entry = il2cpp_failure_plan_entry(Path(assembly_path).name, signature)
            if plan_entry is not None:
                il2cpp_invalid_methods.add(plan_entry)
        for type_name, method, parameters in re.findall(
            r"InvalidProgramException: Invalid IL code in (.+?):([A-Za-z0-9_.$/+`<>]+)\s*\(([^)]*)\):", log
        ):
            if method == "OnValidate" and not parameters.strip():
                # Unity invokes OnValidate during editor import. Replacing only
                # diagnosed malformed validation callbacks keeps gameplay
                # methods intact while allowing serialized assets to load.
                targets.setdefault((type_name, method), -1)
            elif method in (".ctor", ".cctor") and not parameters.strip():
                # Preserve parameterized decoding constructors and avoid
                # replacing type initializers that merely threw at runtime.
                targets.setdefault((type_name, method), -1)

    with tempfile.TemporaryDirectory(prefix="rea-9.2-constructor-repair-") as temporary:
        temporary_path = Path(temporary)
        source = Path(__file__).resolve().with_name("RepairRecoveredConstructor.cs")
        authored_source_dir = Path(__file__).resolve().parent / "recovery_sources"
        authored_sources = sorted(authored_source_dir.glob("*.cs"))
        executable = temporary_path / "RepairRecoveredConstructor.exe"
        authored_bodies = temporary_path / "RecoveryMethodBodies.dll"
        plan = temporary_path / "repair-plan.tsv"
        il2cpp_plan = temporary_path / "il2cpp-failure-plan.tsv"
        if not source.is_file():
            raise FileNotFoundError(source)
        if not authored_sources:
            raise FileNotFoundError(f"no authored recovery method sources under {authored_source_dir}")
        plan.write_text("".join(f"{type_name}\t{method}\t{fields}\n" for (type_name, method), fields in sorted(targets.items())))
        il2cpp_plan.write_text("".join("\t".join(map(str, row)) + "\n"
                                        for row in sorted(il2cpp_invalid_methods)))
        subprocess.run(
            [str(mcs), "-target:library", f"-out:{authored_bodies}",
             f"-r:{plugin_dir / 'Assembly-CSharp-firstpass.dll'}",
             f"-r:{editor_root / 'Data/Managed/UnityEngine/UnityEngine.CoreModule.dll'}",
             *map(str, authored_sources)],
            check=True,
        )
        subprocess.run(
            [str(mcs), f"-r:{cecil}", f"-out:{executable}", str(source)],
            check=True,
        )
        env = os.environ.copy()
        env["MONO_PATH"] = str(cecil.parent)
        subprocess.run(
            [str(mono), str(executable), str(plugin_dir), str(plan),
             str(editor_root / "Data/Managed/UnityEngine"), str(core_library), str(system_library),
             str(mono_security), str(il2cpp_plan), str(authored_bodies)],
            check=True,
            env=env,
        )

    report_path = project / "recovered-import-repairs.json"
    previous_unsupported: list[dict[str, str]] = []
    if args.diagnostics and report_path.is_file():
        try:
            previous_report = json.loads(report_path.read_text())
            previous_unsupported = previous_report.get("unsupported_il2cpp_methods", [])
        except (OSError, json.JSONDecodeError):
            previous_unsupported = []
    unsupported_by_key = {
        (entry.get("assembly", ""), entry.get("signature", "")): entry
        for entry in previous_unsupported
        if entry.get("assembly") and entry.get("signature")
    }
    unsupported_by_key.update({
        (assembly, signature): {"assembly": assembly, "signature": signature}
        for assembly, _type_name, _method_name, _parameter_count, signature
        in il2cpp_invalid_methods
    })
    report = {
        "repairs": [f"{type_name}::{method}()" for (type_name, method) in sorted(targets)],
        "unsupported_il2cpp_methods": [unsupported_by_key[key]
                                        for key in sorted(unsupported_by_key)],
        "compatibility_limits": [
            "SharpZipLib AES-encrypted ZIP initialization is unsupported when the recovered Cpp2IL method contains invalid by-reference IL; output arrays are cleared",
            "Methods explicitly rejected by Unity IL2CPP diagnostics are replaced with throwing stubs; their recovered bodies remain unverified and those APIs are unsupported at runtime"
        ],
        "structural_repairs": [
            "Transplant authored AlignUIElements.GetObjectBounds C# method body, reconstructed from the 9.2 ARM64 trace, into the recovered game assembly",
            "Set Cpp2ILInjected.Cpp2ILHelpers.BaseType to System.Object in every recovered plugin where the helper class has no base reference",
            "Replace Mono.Security.dll with Unity's 4.5 profile assembly only when its full strong-name identity matches and it contains PKCS12.GetExistingParameters(Boolean&)",
            "Rebuild EB.Math.Color vector constructors from native 9.2 channel clamp, byte conversion, and packing behavior",
            "Rebuild EB.Math.Color.Equals(Color), op_Equality, and op_Inequality from the recovered packed UInt32 field",
            "Rebuild EB.Math.Color.Lerp from its packed RGBA field and recovered per-channel integer interpolation helper",
            "Rebuild EB.Math.Color premultiplication, scalar multiplication, and normalized Vector3/Vector4 conversion methods from 9.2 traces",
            "Rebuild EB.Math.BoundingBox.Contains for point, box, and sphere overloads from exact 9.2 ARM64 traces",
            "Rebuild EB.Math.BoundingSphere point/sphere containment and sphere/plane intersection overloads from exact 9.2 traces",
            "Rebuild EB.Cache.PurgeCache(TimeSpan) from the native 9.2 cache-file age sweep and failure handling",
            "Rebuild EB.BitStream.Serialize(ref byte[]) from the traced 9.2 inline/extended length prefix, Buffer operations, and ArraySegment copy",
            "Rebuild EB.BitStream.Serialize(ref EB.Buffer) from the traced 9.2 inline/extended length prefix and EB.Buffer copy operations",
            "Rebuild EB.BitStream.Serialize(ref string) from the traced 9.2 isReading branch and EB.Buffer.ReadString/WriteString calls",
            "Rebuild EB.Rendering.BeamRenderer.FloatEvlautation from the 9.2 added-evaluations branch, AnimationCurve.Evaluate call, and List<float> cache accesses",
            "Rebuild EB.Rendering.BeamRenderer.Update from the native 9.2 endpoint null checks, transform positions, startup interpolation, and duration/loop gate",
            "Rebuild EB.Rendering.EBLightShadow parameterless constructor from the native 9.2 field defaults, shadow-setting objects, and texture-size arrays",
            "Rebuild Crash.DoAnim from the native 9.2 sub-transform null check, damping factor, per-axis random displacement, and local-position write",
            "Rebuild EB.Base.BaseAPI.PlaceEntity from the native 9.2 seven-value route, infinity coordinate sentinel, and Post/Service calls",
            "Rebuild CopyMemberBinding.EnsureTypeMatches from the native 9.2 assignability, nullable-value, string-empty, and default-instance paths",
            "Rebuild EB.Director.CameraData.Lerp from verified 9.2 fields and its Matrix4x4, Vector3, Quaternion, and Mathf interpolation calls",
            "Rebuild AveHistoryItem's malformed constructor using the retained 9.2 win/loss Color32 constants and field use",
            "Replace only the malformed recovered SharpZipLib AES ZIP initializer body with a local unsupported-feature fallback; AES-encrypted ZIP entries are not reconstructed"
        ],
        "reason": "Cpp2IL emitted malformed IL. Recovered import repairs rebuild GameboardBuilder's collection fields and three scalar constants, restore BattlegroupColours, reconstruct EBParticlePal's enum counts and observed instance defaults, initialize each Condition tuning entry, initialize UILabel's shared collections and localization subscription, initialize PrefabDiffTracker's modifier map and timeslice default, initialize Localizer's maps, format provider, flags, and a harmless placeholder regex until its lost literal is recovered, preserve QuestNodeTuning's serialized fields, correct a derived-field visibility mismatch, reconstruct EB.Hash static constants and FNV64, EB.SafeValue.Init, EB.Math.Color vector constructors, packed equality, inequality, and channel interpolation, the point/box/sphere EB.Math.BoundingBox.Contains overloads, EB.Director.CameraData.Lerp from its field layout and native traces, and ThreadSafeRandom.get_Randomizer from native 9.2 behavior, and clear only diagnosed parameterless constructors or editor OnValidate callbacks. PrefabDiffTracker's two custom modifier delegates and RAID_START_POS remain unrecovered.",
        "notice": "Local generated build input only; untouched Cpp2IL output remains under cpp2il/.",
    }
    report_path.write_text(json.dumps(report, indent=2) + "\n")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
