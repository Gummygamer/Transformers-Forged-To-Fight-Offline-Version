#!/usr/bin/env python3
"""Generate local Unity script binders that retain 9.2 script GUIDs and DLL behavior."""

from __future__ import annotations

import argparse
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
from collections import defaultdict
from pathlib import Path


TYPE_DUMPER = r"""
using System;
using Mono.Cecil;
class TypeDump {
    static void Emit(TypeDefinition t, string assembly) {
        Console.WriteLine(assembly + "\t" + t.FullName + "\t" + t.IsClass + "\t" +
            t.IsPublic + "\t" + t.IsAbstract + "\t" + t.IsSealed + "\t" +
            t.GenericParameters.Count + "\t" + (t.BaseType == null ? "" : t.BaseType.FullName));
        foreach (TypeDefinition n in t.NestedTypes) Emit(n, assembly);
    }
    static int Main(string[] args) {
        foreach (string path in args) {
            AssemblyDefinition assembly = AssemblyDefinition.ReadAssembly(path);
            foreach (TypeDefinition type in assembly.MainModule.Types) Emit(type, assembly.Name.Name);
        }
        return 0;
    }
}
"""


CSHARP_KEYWORDS = set(
    "abstract as base bool break byte case catch char checked class const continue decimal "
    "default delegate do double else enum event explicit extern false finally fixed float for "
    "foreach goto if implicit in int interface internal is lock long namespace new null object "
    "operator out override params private protected public readonly ref return sbyte sealed short "
    "sizeof stackalloc static string struct switch this throw true try typeof uint ulong unchecked "
    "unsafe ushort using virtual void volatile while"
    .split()
)

SCRIPT_REFERENCE = re.compile(
    r"m_Script:\s*\{fileID:\s*11500000,\s*guid:\s*([0-9a-f]{32})"
)


def run(command: list[str], *, env: dict[str, str] | None = None) -> subprocess.CompletedProcess[str]:
    result = subprocess.run(command, capture_output=True, text=True, check=False, env=env)
    if result.returncode:
        details = (result.stdout + result.stderr).strip()
        raise RuntimeError(f"command failed ({result.returncode}): {' '.join(command)}\n{details}")
    return result


def csharp_identifier(value: str) -> str:
    return "@" + value if value in CSHARP_KEYWORDS else value


def csharp_qualified_name(value: str) -> str:
    return ".".join(csharp_identifier(part) for part in value.split("."))


def source_identity(source: Path) -> tuple[str, str, str] | None:
    text = source.read_text(errors="replace")
    namespace_match = re.search(r"\bnamespace\s+([\w.@]+)", text)
    namespace = namespace_match.group(1).replace("@", "") if namespace_match else ""
    class_match = re.search(
        r"\b(?:class|struct|interface|enum)\s+(@?[A-Za-z_][A-Za-z_0-9]*)\b", text
    )
    if class_match is None:
        return None
    name = class_match.group(1).lstrip("@")
    if name != source.stem:
        return None
    full_name = f"{namespace}.{name}" if namespace else name
    return namespace, name, full_name


def read_types(
    rows: list[str],
) -> tuple[dict[tuple[str, str], dict[str, object]], dict[str, list[tuple[str, str]]]]:
    definitions: dict[tuple[str, str], dict[str, object]] = {}
    by_name: dict[str, list[tuple[str, str]]] = defaultdict(list)
    for row in rows:
        columns = row.split("\t")
        if len(columns) != 8:
            continue
        assembly, full_name, is_class, is_public, is_abstract, is_sealed, generic_count, base = columns
        key = (assembly, full_name)
        definitions[key] = {
            "class": is_class == "True",
            "public": is_public == "True",
            "abstract": is_abstract == "True",
            "sealed": is_sealed == "True",
            "generic_count": int(generic_count),
            "base": base,
        }
        by_name[full_name].append(key)
    return definitions, by_name


def is_unity_component(
    start: tuple[str, str],
    definitions: dict[tuple[str, str], dict[str, object]],
    by_name: dict[str, list[tuple[str, str]]],
) -> bool:
    current = start
    visited: set[tuple[str, str]] = set()
    for _ in range(100):
        if current in visited:
            return False
        visited.add(current)
        definition = definitions.get(current)
        if definition is None:
            return False
        base = str(definition["base"])
        if base in ("UnityEngine.MonoBehaviour", "UnityEngine.ScriptableObject"):
            return True
        base = base.split("<", 1)[0]
        choices = by_name.get(base, [])
        if len(choices) == 1:
            current = choices[0]
        else:
            same_assembly = [choice for choice in choices if choice[0] == current[0]]
            if len(same_assembly) != 1:
                return False
            current = same_assembly[0]
    return False


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("asset_export", type=Path, help="untouched AssetRipper Unity project export")
    parser.add_argument("cpp2il_output", type=Path, help="directory containing Cpp2IL DLL output")
    parser.add_argument("unity_assets", type=Path, help="Assets directory of the staged rebuild project")
    parser.add_argument("--unity", type=Path, required=True, help="Unity 2020.3.31f1 Editor executable")
    args = parser.parse_args()

    export = args.asset_export.expanduser().resolve()
    cpp2il = args.cpp2il_output.expanduser().resolve()
    unity = args.unity.expanduser().resolve()
    assets = args.unity_assets.expanduser().resolve()
    if not (export / "Assets").is_dir():
        parser.error(f"AssetRipper export has no Assets directory: {export}")
    if not assets.is_dir():
        parser.error(f"staged Unity project has no Assets directory: {assets}")
    required = (cpp2il / "Assembly-CSharp.dll", cpp2il / "Assembly-CSharp-firstpass.dll")
    if not all(path.is_file() for path in required):
        parser.error(f"Cpp2IL output is missing one of the game assemblies: {cpp2il}")
    if not unity.is_file():
        parser.error(f"Unity Editor executable does not exist: {unity}")
    generated_root = assets / "RecoveredBindings"
    generated_plugin = assets / "Plugins/RecoveredBindingBases.dll"
    if generated_root.exists() or generated_plugin.exists():
        parser.error("generated binding output already exists; use a fresh staged Unity project")

    editor_root = unity.parent
    mono_root = editor_root / "Data/MonoBleedingEdge"
    mcs = mono_root / "bin/mcs"
    mono = mono_root / "bin/mono"
    cecil = mono_root / "lib/mono/gac/Mono.Cecil/0.10.0.0__0738eb9f132ed756/Mono.Cecil.dll"
    if not all(path.is_file() for path in (mcs, mono, cecil)):
        parser.error(f"Unity's bundled Mono compiler or Mono.Cecil is missing under {mono_root}")

    source_assets = export / "Assets"
    script_metas: dict[str, Path] = {}
    for meta in source_assets.rglob("*.cs.meta"):
        match = re.search(r"^guid:\s*([0-9a-f]{32})\s*$", meta.read_text(errors="ignore"), re.MULTILINE)
        if match:
            guid = match.group(1)
            if guid in script_metas:
                parser.error(f"duplicate script GUID in AssetRipper export: {guid}")
            script_metas[guid] = meta

    referenced_guids: set[str] = set()
    for path in source_assets.rglob("*"):
        if path.is_file() and path.suffix in (".unity", ".prefab", ".asset"):
            try:
                referenced_guids.update(SCRIPT_REFERENCE.findall(path.read_text(errors="ignore")))
            except OSError:
                continue
    if not referenced_guids:
        parser.error(f"no serialized MonoScript references found under {source_assets}")

    groups: dict[str, list[tuple[str, Path, str, str]]] = {
        "Assembly-CSharp": [],
        "Assembly-CSharp-firstpass": [],
    }
    source_fallbacks: list[tuple[str, Path, str, str, str]] = []
    unmatched: list[str] = []
    metadata_missing: list[str] = []
    for guid in sorted(referenced_guids):
        meta = script_metas.get(guid)
        if meta is None:
            unmatched.append(guid)
            continue
        source = meta.with_suffix("")
        identity = source_identity(source)
        group = next((name for name in groups if name in source.parts), None)
        if identity is None or group is None:
            source_fallbacks.append((guid, meta, "", "", ""))
            continue
        namespace, name, full_name = identity
        groups[group].append((guid, meta, namespace, name))
        if not (cpp2il / f"{group}.dll").is_file():
            metadata_missing.append(guid)

    if metadata_missing:
        parser.error("Cpp2IL output is missing a script assembly required by the export")

    with tempfile.TemporaryDirectory(prefix="rea-9.2-bindings-") as temporary:
        temp = Path(temporary)
        dumper_source = temp / "TypeDump.cs"
        dumper = temp / "TypeDump.exe"
        dumper_source.write_text(TYPE_DUMPER)
        run([str(mcs), f"-r:{cecil}", f"-out:{dumper}", str(dumper_source)])
        metadata_env = os.environ.copy()
        metadata_env["MONO_PATH"] = str(cecil.parent)
        assemblies = sorted(cpp2il.glob("*.dll"))
        dumped = run(
            [str(mono), str(dumper), *(str(path) for path in assemblies)],
            env=metadata_env,
        )
        definitions, by_name = read_types(dumped.stdout.splitlines())

        selected: dict[str, list[tuple[str, Path, str, str]]] = {
            "Assembly-CSharp": [],
            "Assembly-CSharp-firstpass": [],
        }
        binary_fallbacks: list[tuple[str, Path, str, str, str]] = []
        for group, scripts in groups.items():
            for item in scripts:
                guid, meta, namespace, name = item
                full_name = f"{namespace}.{name}" if namespace else name
                definition = definitions.get((group, full_name))
                if definition is None:
                    metadata_missing.append(guid)
                    source_fallbacks.append((guid, meta, group, namespace, name))
                    continue
                eligible = (
                    bool(definition["class"])
                    and bool(definition["public"])
                    and not bool(definition["sealed"])
                    and int(definition["generic_count"]) == 0
                )
                if eligible and is_unity_component((group, full_name), definitions, by_name):
                    selected[group].append(item)
                else:
                    source_fallbacks.append((guid, meta, group, namespace, name))

        # AssetRipper can export a MonoBehaviour's source even when its compiled
        # implementation lives in a plugin DLL (for example Fabric.Core.dll).
        # Keep the serialized GUID but bind that script to the recovered DLL type
        # instead of compiling a second, conflicting copy of the exported source.
        still_source_fallbacks: list[tuple[str, Path, str, str, str]] = []
        for guid, meta, group, namespace, name in source_fallbacks:
            identity = source_identity(meta.with_suffix(""))
            if identity is None:
                still_source_fallbacks.append((guid, meta, group, namespace, name))
                continue
            namespace, name, full_name = identity
            candidates = []
            for key in by_name.get(full_name, []):
                definition = definitions[key]
                eligible = (
                    bool(definition["class"])
                    and bool(definition["public"])
                    and not bool(definition["sealed"])
                    and int(definition["generic_count"]) == 0
                    and is_unity_component(key, definitions, by_name)
                )
                if eligible:
                    candidates.append(key)
            if len(candidates) == 1:
                binary_fallbacks.append(
                    (guid, meta, namespace, name, candidates[0][0])
                )
            else:
                still_source_fallbacks.append((guid, meta, group, namespace, name))
        source_fallbacks = still_source_fallbacks

        extra_assemblies = sorted(
            {
                assembly
                for _guid, _meta, _namespace, _name, assembly in binary_fallbacks
                if assembly not in ("Assembly-CSharp", "Assembly-CSharp-firstpass")
            }
        )
        aliases = {
            assembly: f"RecoveredExtra_{index}"
            for index, assembly in enumerate(extra_assemblies)
        }

        generated_root.mkdir(parents=True)
        base_source = temp / "RecoveredBindingBases.cs"
        base_lines = [
            "extern alias RecoveredMain;",
            "extern alias RecoveredFirstPass;",
            *(f"extern alias {alias};" for alias in aliases.values()),
            "namespace RecoveredBindingBases",
            "{",
        ]
        for group, scripts in selected.items():
            alias = "RecoveredMain" if group == "Assembly-CSharp" else "RecoveredFirstPass"
            for guid, _meta, namespace, name in scripts:
                full_name = f"{namespace}.{name}" if namespace else name
                base_type = f"{alias}::{csharp_qualified_name(full_name)}"
                base_lines.append(
                    f"    public abstract class ScriptBase_{guid} : {base_type} {{ }}"
                )
        for guid, _meta, namespace, name, assembly in binary_fallbacks:
            alias = {
                "Assembly-CSharp": "RecoveredMain",
                "Assembly-CSharp-firstpass": "RecoveredFirstPass",
            }.get(assembly, aliases.get(assembly))
            if alias is None:
                raise RuntimeError(f"no extern alias assigned to recovered assembly {assembly}")
            full_name = f"{namespace}.{name}" if namespace else name
            base_type = f"{alias}::{csharp_qualified_name(full_name)}"
            base_lines.append(
                f"    public abstract class ScriptBase_{guid} : {base_type} {{ }}"
            )
        base_lines.append("}")
        base_source.write_text("\n".join(base_lines) + "\n")

        references: list[str] = []
        for assembly in assemblies:
            if assembly.name == "Assembly-CSharp.dll":
                references.append(f"-r:RecoveredMain={assembly}")
            elif assembly.name == "Assembly-CSharp-firstpass.dll":
                references.append(f"-r:RecoveredFirstPass={assembly}")
            elif assembly.stem in aliases:
                references.append(f"-r:{aliases[assembly.stem]}={assembly}")
            else:
                references.append(f"-r:{assembly}")
        if not references:
            raise RuntimeError(f"no Cpp2IL managed assemblies found in {cpp2il}")
        base_dll = temp / "RecoveredBindingBases.dll"
        run(
            [
                str(mcs), "-noconfig", "-nostdlib", "-target:library",
                f"-out:{base_dll}", *references, str(base_source),
            ]
        )

        shutil.copy2(base_dll, generated_plugin)
        for group, scripts in selected.items():
            group_dir = generated_root / group
            group_dir.mkdir(parents=True)
            assembly_name = "RecoveredBindings.Main" if group == "Assembly-CSharp" else "RecoveredBindings.FirstPass"
            (group_dir / f"{assembly_name}.asmdef").write_text(
                json.dumps(
                    {
                        "name": assembly_name,
                        "references": [],
                        "includePlatforms": [],
                        "excludePlatforms": [],
                        "allowUnsafeCode": False,
                        "overrideReferences": False,
                        "precompiledReferences": [],
                        "autoReferenced": True,
                        "defineConstraints": [],
                        "versionDefines": [],
                        "noEngineReferences": False,
                    },
                    indent=2,
                )
                + "\n"
            )
            for guid, meta, namespace, name in scripts:
                relative = meta.relative_to(source_assets)
                group_index = relative.parts.index(group)
                output_dir = group_dir / Path(*relative.parts[group_index + 1 : -1])
                output_dir.mkdir(parents=True, exist_ok=True)
                binding_namespace = f"RecoveredBindings.Generated.Guid_{guid}"
                code = (
                    f"namespace {binding_namespace}\n{{\n"
                    f"    public class {csharp_identifier(name)} : "
                    f"RecoveredBindingBases.ScriptBase_{guid} {{ }}\n"
                    "}\n"
                )
                output_source = output_dir / (name + ".cs")
                output_source.write_text(code)
                shutil.copy2(meta, output_source.with_suffix(".cs.meta"))

        fallback_dir = generated_root / "Fallback"
        if source_fallbacks or binary_fallbacks or unmatched:
            fallback_dir.mkdir()
            (fallback_dir / "RecoveredBindings.Fallback.asmdef").write_text(
                json.dumps({"name": "RecoveredBindings.Fallback", "autoReferenced": True}, indent=2)
                + "\n"
            )
            for guid, meta, group, _namespace, _name in source_fallbacks:
                source = meta.with_suffix("")
                if group in groups and group in source.parts:
                    source_relative = source.relative_to(source_assets)
                    group_index = source_relative.parts.index(group)
                    relative = Path(*source_relative.parts[group_index + 1 :])
                    output_source = fallback_dir / group / relative
                else:
                    output_source = fallback_dir / "Other" / source.name
                output_source.parent.mkdir(parents=True, exist_ok=True)
                shutil.copy2(source, output_source)
                shutil.copy2(meta, output_source.with_suffix(".cs.meta"))

            for guid, meta, namespace, name, _assembly in binary_fallbacks:
                source = meta.with_suffix("")
                output_source = fallback_dir / "Other" / source.name
                output_source.parent.mkdir(parents=True, exist_ok=True)
                binding_namespace = f"RecoveredBindings.Generated.Guid_{guid}"
                code = (
                    f"namespace {binding_namespace}\n{{\n"
                    f"    public class {csharp_identifier(name)} : "
                    f"RecoveredBindingBases.ScriptBase_{guid} {{ }}\n"
                    "}\n"
                )
                output_source.write_text(code)
                shutil.copy2(meta, output_source.with_suffix(".cs.meta"))

            if unmatched:
                placeholder_dir = fallback_dir / "Unresolved"
                placeholder_dir.mkdir()
                template = next(iter(script_metas.values())).read_text()
                for guid in unmatched:
                    name = f"RecoveredUnavailableScript_{guid}"
                    output_source = placeholder_dir / f"{name}.cs"
                    output_source.write_text(
                        "using UnityEngine;\n"
                        f"public class {name} : MonoBehaviour {{ }}\n"
                    )
                    output_source.with_suffix(".cs.meta").write_text(
                        re.sub(
                            r"(?m)^guid:\s*[0-9a-f]{32}",
                            f"guid: {guid}",
                            template,
                            count=1,
                        )
                    )

        manifest = {
            "asset_export": str(export),
            "cpp2il_output": str(cpp2il),
            "serialized_script_guid_count": len(referenced_guids),
            "matched_asset_script_guid_count": len(referenced_guids & script_metas.keys()),
            "main_script_bindings": len(selected["Assembly-CSharp"]),
            "firstpass_script_bindings": len(selected["Assembly-CSharp-firstpass"]),
            "fallback_script_count": len(source_fallbacks),
            "recovered_plugin_script_binding_count": len(binary_fallbacks),
            "unmatched_script_guids": unmatched,
            "synthetic_placeholder_guids": unmatched,
            "missing_cpp2il_types": sorted(set(metadata_missing)),
            "notice": "Local Unity script binders; source output is operator-generated and not distributable.",
        }

    manifest_path = assets.parent / "recovered-script-bindings-manifest.json"
    manifest_path.write_text(json.dumps(manifest, indent=2) + "\n")
    print(f"Generated {manifest['main_script_bindings']} main and {manifest['firstpass_script_bindings']} firstpass script bindings")
    print(
        f"Fallback source scripts: {manifest['fallback_script_count']}; "
        f"recovered plugin bindings: {manifest['recovered_plugin_script_binding_count']}; "
        f"unmatched GUIDs: {len(unmatched)}"
    )
    print(f"Unity Assets: {assets}")
    print(f"Manifest: {manifest_path}")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (OSError, RuntimeError, subprocess.SubprocessError) as error:
        print(f"generate_9_2_script_bindings: {error}", file=sys.stderr)
        raise SystemExit(1)
