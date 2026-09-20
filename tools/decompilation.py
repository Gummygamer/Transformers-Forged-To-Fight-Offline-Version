#!/usr/bin/env python3
"""Reproducible, local-only managed-client recovery and compilation diagnostics."""
import argparse
from collections import Counter
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import tempfile
import uuid
import zipfile
import xml.etree.ElementTree as ET

REPO = Path(__file__).resolve().parents[1]
MANAGED = "assets/bin/Data/Managed/"


def digest(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def save(path, value):
    path.write_text(json.dumps(value, indent=2) + "\n")


def assembly_entries(archive):
    entries = []
    seen = set()
    for info in archive.infolist():
        if not info.filename.startswith(MANAGED) or not info.filename.endswith(".dll"):
            continue
        name = info.filename[len(MANAGED):]
        if not re.fullmatch(r"[A-Za-z0-9_][A-Za-z0-9_.-]*\.dll", name):
            raise ValueError(f"Unsupported managed assembly path: {info.filename}")
        if name.casefold() in seen:
            raise ValueError(f"Duplicate managed assembly: {name}")
        seen.add(name.casefold())
        entries.append(info)
    if "assembly-csharp.dll" not in seen:
        raise ValueError("No Mono Assembly-CSharp.dll; IL2CPP APKs need the native analysis track")
    return sorted(entries, key=lambda item: item.filename)


def solution(out, projects):
    lines = ["Microsoft Visual Studio Solution File, Format Version 12.00"]
    ids = []
    for project in projects:
        relative = project.relative_to(out).as_posix()
        guid = str(uuid.uuid5(uuid.NAMESPACE_URL, relative)).upper()
        ids.append(guid)
        lines.extend([
            f'Project("{{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}}") = '
            f'"{project.stem}", "{relative}", "{{{guid}}}"', "EndProject"])
    lines += ["Global", "\tGlobalSection(SolutionConfigurationPlatforms) = preSolution",
              "\t\tDebug|Any CPU = Debug|Any CPU", "\tEndGlobalSection",
              "\tGlobalSection(ProjectConfigurationPlatforms) = postSolution"]
    for guid in ids:
        lines.append(f"\t\t{{{guid}}}.Debug|Any CPU.ActiveCfg = Debug|Any CPU")
    lines += ["\tEndGlobalSection", "EndGlobal"]
    (out / "Recovered.sln").write_text("\n".join(lines) + "\n")


def recover(apk, ilspy):
    tool = shutil.which(ilspy)
    if tool is None:
        raise ValueError(f"ILSpy executable not found: {ilspy}")
    version = subprocess.check_output([tool, "--version"], text=True).strip()
    with zipfile.ZipFile(apk) as archive:
        entries = assembly_entries(archive)
        parent = REPO / "build/decompilation"
        parent.mkdir(parents=True, exist_ok=True)
        out = Path(tempfile.mkdtemp(prefix="mono-", dir=parent))
        for folder in ("managed", "source", "logs", "types"):
            (out / folder).mkdir()
        manifest = {"schema": 1, "input": {"path": str(apk), "sha256": digest(apk)},
                    "backend": "mono", "tool": version, "status": "running",
                    "compilation": "not-tested", "assemblies": []}
        save(out / "manifest.json", manifest)
        print(f"Workspace: {out}", flush=True)
        for info in entries:
            name = Path(info.filename).name
            target = out / "managed" / name
            with archive.open(info) as source, target.open("wb") as dest:
                shutil.copyfileobj(source, dest)
            manifest["assemblies"].append({"name": name, "apk_entry": info.filename,
                                            "sha256": digest(target), "status": "pending"})
        save(out / "manifest.json", manifest)
    for row in manifest["assemblies"]:
        name = Path(row["name"]).stem
        assembly = out / "managed" / row["name"]
        print(f"Decompiling {row['name']}...", flush=True)
        with (out / "types" / f"{name}.txt").open("w") as inventory, \
                (out / "logs" / f"{name}.log").open("w") as log:
            listed = subprocess.run([tool, "--disable-updatecheck", "-l", "c,i,s,d,e",
                                     str(assembly)], stdout=inventory, stderr=log)
            result = subprocess.run([tool, "--disable-updatecheck", "-p", "-r",
                                     str(out / "managed"), "-o", str(out / "source" / name),
                                     str(assembly)], stdout=log, stderr=log)
        projects = sorted((out / "source" / name).glob("*.csproj"))
        row.update(status="exported" if result.returncode == 0 and projects else "failed",
                   exit_code=result.returncode, inventory_exit_code=listed.returncode,
                   source_files=len(list((out / "source" / name).rglob("*.cs"))),
                   projects=[p.relative_to(out).as_posix() for p in projects])
        if listed.returncode:
            row["status"] = "failed"
        save(out / "manifest.json", manifest)
    solution(out, sorted((out / "source").glob("*/*.csproj")))
    manifest["status"] = ("exported" if all(r["status"] == "exported"
                                          for r in manifest["assemblies"]) else "partial")
    save(out / "manifest.json", manifest)
    print(f"{manifest['status']}: {out / 'Recovered.sln'}", flush=True)
    print("Export coverage does not establish compilation or runtime equivalence.")
    return 0 if manifest["status"] == "exported" else 1


def export_il(workspace, ilspy):
    """Disassemble every original managed input, with provenance and no repairs.

    IL represents retained metadata and instructions, including contracts that
    C# cannot express. Export success does not certify semantic equivalence or
    recover code stripped before the supplied APK was built.
    """
    manifest_path = workspace / "manifest.json"
    manifest_bytes = manifest_path.read_bytes()
    manifest = json.loads(manifest_bytes)
    rows = manifest["assemblies"]
    if manifest.get("backend") != "mono" or not rows:
        raise ValueError("Expected a nonempty Mono export manifest")
    names = set()
    for row in rows:
        name = row["name"]
        if (not re.fullmatch(r"[A-Za-z0-9_][A-Za-z0-9_.-]*\.dll", name)
                or name.casefold() in names):
            raise ValueError(f"Invalid or duplicate managed assembly name: {name}")
        names.add(name.casefold())
        if digest(workspace / "managed" / name) != row["sha256"]:
            raise ValueError(f"Assembly changed since export: {name}")
    tool = shutil.which(ilspy)
    if tool is None:
        raise ValueError(f"ILSpy executable not found: {ilspy}")
    version = subprocess.check_output([tool, "--version"], text=True).strip()
    out = Path(tempfile.mkdtemp(prefix="il-", dir=workspace))
    report = {"schema": 1, "scope": "original managed IL; no source repairs",
              "manifest_sha256": hashlib.sha256(manifest_bytes).hexdigest(),
              "input": manifest.get("input"),
              "tool": version, "status": "running", "runtime_verified": False,
              "assemblies": []}
    save(out / "report.json", report)
    # Retain exactly the manifest bytes used for validation, even if another
    # local audit updates the workspace manifest while ILSpy is running.
    (out / "manifest.json").write_bytes(manifest_bytes)
    print(f"IL workspace: {out}", flush=True)
    for row in rows:
        name = row["name"]
        original = workspace / "managed" / name
        # Freeze and recheck the actual decompiler input before invoking ILSpy.
        frozen = out / "managed" / name
        frozen.parent.mkdir(exist_ok=True)
        shutil.copyfile(original, frozen)
        if digest(frozen) != row["sha256"]:
            raise ValueError(f"Assembly changed during snapshot: {name}")
        il = out / (Path(name).stem + ".il")
        log = out / (Path(name).stem + ".log")
        with il.open("w") as output, log.open("w") as errors:
            result = subprocess.run([tool, "--disable-updatecheck", "-il", str(frozen)],
                                    stdout=output, stderr=errors)
        unchanged = digest(frozen) == row["sha256"] and digest(original) == row["sha256"]
        success = result.returncode == 0 and il.stat().st_size > 0 and unchanged
        report["assemblies"].append({"name": name, "input_sha256": row["sha256"],
                                     "status": "exported" if success else "failed",
                                     "exit_code": result.returncode,
                                     "inputs_unchanged": unchanged,
                                     "il_path": il.name, "il_sha256": digest(il),
                                     "log_path": log.name})
        save(out / "report.json", report)
        print(f"{name}: {report['assemblies'][-1]['status']}", flush=True)
    success = all(row["status"] == "exported" for row in report["assemblies"])
    report["status"] = "exported" if success else "partial"
    save(out / "report.json", report)
    print(f"Report: {out / 'report.json'}", flush=True)
    return 0 if success else 1


def source_declaration_inventory(source_root):
    """Collect a deliberately shallow C# declaration inventory for comparison.

    This is a decompiler-output observation only.  It does not infer visibility,
    overload signatures, or serialized layout; those facts come from metadata.
    """
    types = set()
    methods = set()
    fields = set()
    type_pattern = re.compile(r"\b(?:class|struct|interface|enum|delegate)\s+([A-Za-z_]\w*)")
    method_pattern = re.compile(
        r"\b(?:public|private|protected|internal|static|virtual|override|sealed|abstract|unsafe|new|extern|async|readonly|partial|\s)+"
        r"[A-Za-z_][\w<>,.?\[\]]*\s+([A-Za-z_]\w*)\s*\(")
    field_pattern = re.compile(
        r"(?:^|[;{}])\s*(?:public|private|protected|internal|static|readonly|const|volatile|unsafe|new|serialized|\s)+"
        r"[A-Za-z_][\w<>,.?\[\]]*\s+([A-Za-z_]\w*)\s*(?:[=;])", re.MULTILINE)
    for path in sorted(source_root.rglob("*.cs")):
        text = re.sub(r"//[^\n]*|/\*.*?\*/", "", path.read_text(), flags=re.S)
        types.update(type_pattern.findall(text))
        methods.update(method_pattern.findall(text))
        fields.update(field_pattern.findall(text))
    return {"types": sorted(types), "methods": sorted(methods), "fields": sorted(fields)}


def _metadata_visibility(flags, kind):
    """Decode only the ECMA-335 visibility bits; retain raw flags elsewhere."""
    masks = {
        "type": (0x7, {
            0: "not-public", 1: "public", 2: "nested-public", 3: "nested-private",
            4: "nested-family", 5: "nested-assembly", 6: "nested-fam-and-assem",
            7: "nested-fam-or-assem"}),
        "member": (0x7, {
            0: "compiler-controlled", 1: "private", 2: "fam-and-assem",
            3: "assembly", 4: "family", 5: "fam-or-assem", 6: "public"}),
    }
    mask, names = masks[kind]
    return names.get(flags & mask, f"unknown-{flags & mask}")


def metadata_api_surface(metadata):
    """Summarize exact PE contracts without treating C# output as authoritative.

    The full helper output remains under ``metadata_contracts`` in the report. This
    smaller view makes visibility, inheritance, overload signatures, attributes,
    and serialization-relevant field layout easy to inspect while retaining each
    raw metadata flag for independent checking.
    """
    types = []
    for name, info in metadata["types"].items():
        fields = []
        for field_name, field in info["fields"].items():
            flags = field["flags"]
            fields.append({"name": field_name, "signature": field["signature"],
                           "flags": flags, "visibility": _metadata_visibility(flags, "member"),
                           "static": bool(flags & 0x10), "readonly": bool(flags & 0x20),
                           "literal": bool(flags & 0x40), "offset": field["offset"],
                           "attributes": field["attributes"]})
        methods = []
        for key, method in info["methods"].items():
            method_name, signature = key.split(":", 1)
            flags = method["flags"]
            methods.append({"name": method_name, "signature": signature, "flags": flags,
                            "visibility": _metadata_visibility(flags, "member"),
                            "static": bool(flags & 0x10), "virtual": bool(flags & 0x40),
                            "abstract": bool(flags & 0x400), "special_name": bool(flags & 0x800),
                            "attributes": method["attributes"], "parameters": method["parameters"]})
        types.append({"name": name, "flags": info["flags"],
                      "visibility": _metadata_visibility(info["flags"], "type"),
                      "base_type": info["base_type"], "attributes": info["attributes"],
                      "interfaces": info["interfaces"], "layout": info["layout"],
                      "serialization_layout": {"field_order": info["field_order"],
                                                "fields": fields},
                      "fields": fields, "methods": methods,
                      "properties": info["properties"], "events": info["events"],
                      "method_impls": info["method_impls"]})
    return {"assembly": {"identity": metadata["identity"],
                          "references": metadata["references"],
                          "resources": metadata["resources"],
                          "metadata_version": metadata["metadata_version"],
                          "machine": metadata["machine"],
                          "cor_flags": metadata["cor_flags"],
                          "module_name": metadata["module_name"],
                          "assembly_attributes": metadata["assembly_attributes"],
                          "module_attributes": metadata["module_attributes"]},
            "types": types}


def metadata_audit(workspace, dotnet, metadata_tool, assemblies=None):
    """Compare a source declaration inventory with facts read from PE metadata.

    The metadata helper is intentionally a separate .NET program so the inspected
    DLL is never loaded into the audit process.  Reports retain both evidence
    classes instead of presenting decompiler output as recovered metadata.
    """
    manifest = json.loads((workspace / "manifest.json").read_text())
    rows = {Path(row["name"]).stem: row for row in manifest["assemblies"]}
    names = assemblies or sorted(rows)
    if any(name not in rows for name in names):
        raise ValueError("Select assembly names present in the export manifest")
    report_dir = Path(tempfile.mkdtemp(prefix="metadata-", dir=workspace))
    report = {"schema": 2, "scope": "exact PE metadata contracts compared with shallow C# declaration inventory",
              "metadata_evidence": "System.Reflection.Metadata over original managed PE; assembly is not loaded",
              "decompiler_evidence": "ILSpy-exported C# source; names only, no behavioral or layout claim",
              "runtime_verified": False, "assemblies": []}
    for name in names:
        row = rows[name]
        managed = workspace / "managed" / row["name"]
        if digest(managed) != row["sha256"]:
            raise ValueError(f"Assembly changed since export: {row['name']}")
        raw = subprocess.check_output([dotnet, str(metadata_tool), str(managed)], text=True)
        metadata = json.loads(raw)
        metadata_types = sorted(metadata["types"])
        source = source_declaration_inventory(workspace / "source" / name)
        source_types = source["types"]
        metadata_names = [x.rsplit("+", 1)[-1] for x in metadata_types if x != "<Module>"]
        metadata_fields = set()
        metadata_methods = set()
        for type_info in metadata["types"].values():
            metadata_fields.update(type_info["fields"])
            metadata_methods.update(key.split(":", 1)[0] for key in type_info["methods"])
        report["assemblies"].append({"name": name, "input_sha256": row["sha256"],
                                     "metadata": {"identity": metadata["identity"],
                                                   "references": metadata["references"],
                                                   "resources": metadata["resources"],
                                                   "type_count": len(metadata_types),
                                                   "types": metadata_types},
                                     # This is the exact metadata evidence. Keep it
                                     # separate from the source observation below;
                                     # decompiler names cannot establish signatures
                                     # or Unity serialization layout.
                                     "metadata_contracts": metadata["types"],
                                     "metadata_api_surface": metadata_api_surface(metadata),
                                     "decompiler": {"source_files": len(list((workspace / "source" / name).rglob("*.cs"))),
                                                     "declarations": source},
                                     "comparison": {"metadata_type_names_missing_from_source": sorted(set(metadata_names) - set(source_types)),
                                                    "source_type_names_missing_from_metadata": sorted(set(source_types) - set(metadata_names)),
                                                    "metadata_field_names_missing_from_source": sorted(metadata_fields - set(source["fields"])),
                                                    "source_field_names_missing_from_metadata": sorted(set(source["fields"]) - metadata_fields),
                                                    "metadata_method_names_missing_from_source": sorted(metadata_methods - set(source["methods"])),
                                                    "source_method_names_missing_from_metadata": sorted(set(source["methods"]) - metadata_methods)}})
    save(report_dir / "report.json", report)
    print(f"Report: {report_dir / 'report.json'}")
    return 0


def replacement_closure(roots, references):
    """Return the managed reference closure for explicitly chosen replacement roots."""
    closure = set()
    pending = list(roots)
    while pending:
        name = pending.pop()
        if name in closure:
            continue
        closure.add(name)
        pending.extend(references.get(name, ()))
    return closure


def replacement_plan(workspace, dotnet, metadata_tool, roots):
    """Write an evidence report for the proposed managed replacement boundary.

    Roots are an explicit engineering choice.  The report computes only the
    transitive managed dependency closure and labels framework/plugin assemblies
    as dependencies to inspect, not as assemblies that may safely be replaced.
    """
    manifest = json.loads((workspace / "manifest.json").read_text())
    rows = {Path(row["name"]).stem: row for row in manifest["assemblies"]}
    if not roots or any(root not in rows for root in roots):
        raise ValueError("Replacement roots must name assemblies present in the export manifest")
    metadata = {}
    references = {}
    for name, row in rows.items():
        managed = workspace / "managed" / row["name"]
        if digest(managed) != row["sha256"]:
            raise ValueError(f"Assembly changed since export: {row['name']}")
        value = json.loads(subprocess.check_output([dotnet, str(metadata_tool), str(managed)], text=True))
        metadata[name] = value
        references[name] = sorted({r["name"] for r in value["references"] if r["name"] in rows})
    closure = replacement_closure(roots, references)
    assemblies = []
    for name in sorted(rows):
        project_refs = sorted(project_references(workspace, name) & set(rows))
        source_root = workspace / "source" / name
        assemblies.append({"name": name, "input_sha256": rows[name]["sha256"],
                           "identity": metadata[name]["identity"],
                           "metadata_references": references[name],
                           "project_references": project_refs,
                           "source_files": len(list(source_root.rglob("*.cs"))),
                           "role": "replacement-root" if name in roots else
                                   "dependency-in-closure" if name in closure else "outside-root-closure",
                           "runtime_replacement_decision": "requires separate compatibility evidence"})
    report = {"schema": 1,
              "scope": "managed replacement-boundary evidence; no packaging or runtime claim",
              "evidence": {"metadata": "System.Reflection.Metadata over original managed PE",
                           "project_graph": "ILSpy-generated project references",
                           "input_manifest": manifest.get("input")},
              "runtime_verified": False,
              "roots": sorted(roots), "transitive_managed_closure": sorted(closure),
              "assemblies": assemblies,
              "interpretation": [
                  "Roots are explicit replacement candidates, not a claim that they can load in Unity.",
                  "Closure members are references needed to compile or resolve the roots; existing binaries may be preserved if identities and contracts remain compatible.",
                  "Assemblies outside the closure are not needed by these roots according to retained metadata references, but may still be loaded by other client code.",
                  "Assembly identity, resources, native bindings, Android packaging, and runtime load order remain unverified."
              ]}
    report_dir = Path(tempfile.mkdtemp(prefix="replacement-", dir=workspace))
    save(report_dir / "report.json", report)
    print(f"Report: {report_dir / 'report.json'}")
    return 0


def normalize_accessors(source):
    """Repair only standalone explicit getter methods with one simple return.

    This deliberately does not rewrite setters, attributes, complex bodies, or
    other virtual methods. Input is an isolated copy of decompiler output.
    """
    pattern = re.compile(
        r"(?m)^(?P<indent>[ \t]+)virtual (?P<type>[\w.<>]+) "
        r"(?P<interface>[\w.<>]+)\.get_(?P<property>\w+)\(\)\n"
        r"(?P=indent)\{\n(?P=indent)\treturn (?P<value>[\w.]+);\n"
        r"(?P=indent)\}")

    def replace(match):
        g = match.groupdict()
        return (f"{g['indent']}{g['type']} {g['interface']}.{g['property']}\n"
                f"{g['indent']}{{\n{g['indent']}\tget {{ return {g['value']}; }}\n"
                f"{g['indent']}}}")

    return pattern.subn(replace, source)


def normalize_source_contracts(path, source):
    """Apply narrow, IL-backed repairs to declarations rejected by Roslyn.

    ILSpy exposes a few metadata contracts that C# cannot spell directly: a
    type-local ``BindingFlags`` constant, a stripped Unity attribute getter,
    and optional flags on parameters without default constants.  These edits
    are made only in the isolated audit snapshot and are recorded by path.
    """
    changes = []
    if (path.name in {"InputField.cs", "ScrollRect.cs", "Graphic.cs", "Slider.cs",
                      "Toggle.cs", "Scrollbar.cs"}
            and "namespace UnityEngine.UI;" in source):
        source, count = re.subn(r"(?m)^\tvirtual (bool ICanvasElement\.|Transform ICanvasElement\.)",
                                r"\t\1", source)
        if count:
            changes.append(f"remove {count} invalid virtual explicit interface modifiers")
    if path.name == "MethodCall.cs" and "namespace Facebook.Unity;" in source:
        # Original MethodCall<T> metadata retains these private fields and
        # setter-only properties (setters at RVAs 0x65b8 / 0x65c4). ILSpy
        # suppresses the fields as if it had emitted complete auto-properties.
        # Keep the setters intact; adding getters would invent an API.
        for property_name, field_type in (("FacebookImpl", "FacebookBase"),
                                          ("Parameters", "MethodArguments")):
            field = f"_003C{property_name}_003Ek__BackingField"
            declaration = f"private {field_type} {field};"
            setter = (f"\tprotected {field_type} {property_name}\n\t{{\n"
                      f"\t\t[CompilerGenerated]\n\t\tset\n\t\t{{\n"
                      f"\t\t\t{field} = value;\n\t\t}}\n\t}}")
            if source.count(setter) == 1 and declaration not in source:
                source = source.replace(
                    setter, "\t[CompilerGenerated]\n"
                    "\t[System.Diagnostics.DebuggerBrowsable("
                    "System.Diagnostics.DebuggerBrowsableState.Never)]\n"
                    f"\t{declaration}\n\n" + setter, 1)
                changes.append(f"restore private {property_name} backing field from IL")
    if path.as_posix().endswith("EB/DownloadExtractor.cs"):
        old = "using EB.Net;"
        new = old + "\nusing WebRequest = EB.Net.WebRequest;"
        if old in source and "using WebRequest = EB.Net.WebRequest;" not in source:
            source = source.replace(old, new, 1)
            changes.append("alias EB.Net.WebRequest")
    if path.name == "ProcessMemberBinding.cs" and "private const BindingFlags BindingFlags" in source:
        source = source.replace(
            "private const BindingFlags BindingFlags = BindingFlags.Static | BindingFlags.Public;",
            "private const System.Reflection.BindingFlags BindingFlags = "
            "System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public;")
        changes.append("qualify BindingFlags constant")

    if "using EB.Sequence.Runtime;" in source and "using SequenceRuntimeTrigger = EB.Sequence.Runtime.Trigger;" not in source:
        source = source.replace("using EB.Sequence.Runtime;",
                                "using EB.Sequence.Runtime;\nusing SequenceRuntimeTrigger = EB.Sequence.Runtime.Trigger;", 1)
        source = re.sub(r"(?<![\[.])\bTrigger\b", "SequenceRuntimeTrigger", source)
        if path.as_posix().endswith("EB.Sequence/Utils.cs"):
            source = re.sub(r"(enum LinkType\s*\{[^}]*)\bSequenceRuntimeTrigger\b",
                            r"\1Trigger", source, count=1, flags=re.S)
        changes.append("qualify EB.Sequence.Runtime.Trigger")
    if (path.as_posix().startswith("EB.Base/") and "using EB.Sparx;" in source
            and "using SparxBuff = EB.Sparx.Buff;" not in source):
        source = source.replace("using EB.Sparx;",
                                "using EB.Sparx;\nusing SparxBuff = EB.Sparx.Buff;", 1)
        source = re.sub(r"(?<![\[.])\bBuff\b", "SparxBuff", source)
        changes.append("qualify EB.Sparx.Buff")
    if path.name in {"PropertyReference.cs", "NGUIMath.cs", "NGUITools.cs"}:
        source, count = re.subn(r"(?<![.\w])Debug\.", "UnityEngine.Debug.", source)
        if count:
            changes.append(f"qualify {count} UnityEngine.Debug calls")
    if path.name == "BuffValidationLogger.cs":
        source, count = re.subn(r"(?<![.\w])Debug\.", "EB.Debug.", source)
        if count:
            changes.append(f"qualify {count} EB.Debug calls")
    if path.name in {"UIPlayAnimation.cs", "UIPlayTween.cs"} and "using AnimationOrTween;" in source:
        source = source.replace("using AnimationOrTween;",
                                "using AnimationOrTween;\nusing NGUITrigger = AnimationOrTween.Trigger;", 1)
        source = re.sub(r"\bTrigger\b", "NGUITrigger", source)
        source = source.replace("AnimationOrTween.NGUITrigger", "AnimationOrTween.Trigger")
        changes.append("qualify AnimationOrTween.Trigger")
    if path.name == "TcpClientMono.cs":
        source, count = source.replace("leaveStreamOpen: true", "true"), source.count("leaveStreamOpen: true")
        if count:
            changes.append("use positional SslStream leave-open argument")
    if path.name == "GachaManager.cs" and "int num;" in source:
        old = '''\t\t\tdefault:\n\t\t\t{\n\t\t\t\tint num;\n\t\t\t\tif (num == 1)\n\t\t\t\t{\n\t\t\t\t\tresult = GetBox(data);\n\t\t\t\t\tbreak;\n\t\t\t\t}\n\t\t\t\tDebug.LogError("Unknown Gacha CallToAction Type {0}", type);\n\t\t\t\tbreak;\n\t\t\t}\n\t\t\tcase "token":\n\t\t\t\tresult = GetBoxForToken(data);\n\t\t\t\tbreak;'''
        new = '''\t\t\tcase "token":\n\t\t\t\tresult = GetBoxForToken(data);\n\t\t\t\tbreak;\n\t\t\tcase "box":\n\t\t\t\tresult = GetBox(data);\n\t\t\t\tbreak;\n\t\t\tdefault:\n\t\t\t\tEB.Debug.LogError("Unknown Gacha CallToAction Type {0}", type);\n\t\t\t\tbreak;'''
        if old in source:
            source = source.replace(old, new, 1)
            changes.append("restore IL switch cases in GachaManager")
    if path.name == "AvxQuestExpirationManager.cs":
        old = '''\t\tswitch (category)\n\t\t{\n\t\tdefault:\n\t\t{\n\t\t\tint num;\n\t\t\tif (num == 1)\n\t\t\t{\n\t\t\t\tempty = "ID_UI_AVESELECTION_QUEST_EXPIRED_HEADER";\n\t\t\t\tempty2 = "ID_UI_AVESELECTION_QUEST_EXPIRED_SPECTATE_ONLY";\n\t\t\t\tbreak;\n\t\t\t}\n\t\t\tonClose.SafeInvoke();\n\t\t\treturn;\n\t\t}\n\t\tcase "AvA":\n\t\t{\n\t\t\tActiveQuest activeQuest = ((!QuestsManager.Instance.avaPlacementQuests.IsNullOrEmpty()) ? QuestsManager.Instance.avaPlacementQuests[0] : null);\n\t\t\tif (activeQuest != null)\n\t\t\t{\n\t\t\t\tempty = "ID_UI_AVA_PLACEMENT_EXPIRING_SOON_HEADER";\n\t\t\t\tempty2 = "ID_UI_AVA_PLACEMENT_EXPIRES_SOON";\n\t\t\t}\n\t\t\telse\n\t\t\t{\n\t\t\t\tempty = "ID_UI_AVA_QUEST_EXPIRED_HEADER";\n\t\t\t\tempty2 = "ID_UI_AVA_QUEST_EXPIRED_SPECTATE_ONLY";\n\t\t\t}\n\t\t\tbreak;\n\t\t}\n\t\t}'''
        new = '''\t\tswitch (category)\n\t\t{\n\t\tcase "AvA":\n\t\t{\n\t\t\tActiveQuest activeQuest = ((!QuestsManager.Instance.avaPlacementQuests.IsNullOrEmpty()) ? QuestsManager.Instance.avaPlacementQuests[0] : null);\n\t\t\tif (activeQuest != null)\n\t\t\t{\n\t\t\t\tempty = "ID_UI_AVA_PLACEMENT_EXPIRING_SOON_HEADER";\n\t\t\t\tempty2 = "ID_UI_AVA_PLACEMENT_EXPIRES_SOON";\n\t\t\t}\n\t\t\telse\n\t\t\t{\n\t\t\t\tempty = "ID_UI_AVA_QUEST_EXPIRED_HEADER";\n\t\t\t\tempty2 = "ID_UI_AVA_QUEST_EXPIRED_SPECTATE_ONLY";\n\t\t\t}\n\t\t\tbreak;\n\t\t}\n\t\tcase "AvE":\n\t\t\tempty = "ID_UI_AVESELECTION_QUEST_EXPIRED_HEADER";\n\t\t\tempty2 = "ID_UI_AVESELECTION_QUEST_EXPIRED_SPECTATE_ONLY";\n\t\t\tbreak;\n\t\tdefault:\n\t\t\tonClose.SafeInvoke();\n\t\t\treturn;\n\t\t}'''
        if old in source:
            source = source.replace(old, new, 1)
            changes.append("restore AvA/AvE expiry switch cases")
        old = '''\t\tswitch (category)\n\t\t{\n\t\tdefault:\n\t\t{\n\t\t\tint num;\n\t\t\tif (num != 1)\n\t\t\t{\n\t\t\t}\n\t\t\tempty = "ID_UI_AVESELECTION_QUEST_EXPIRING_SOON_HEADER";\n\t\t\tempty2 = "ID_UI_AVESELECTION_QUEST_EXPIRES_SOON";\n\t\t\tbreak;\n\t\t}\n\t\tcase "AvA":\n\t\t{\n\t\t\tActiveQuest activeQuest = ((!QuestsManager.Instance.avaPlacementQuests.IsNullOrEmpty()) ? QuestsManager.Instance.avaPlacementQuests[0] : null);\n\t\t\tif (activeQuest != null)\n\t\t\t{\n\t\t\t\tempty = "ID_UI_AVA_PLACEMENT_EXPIRING_SOON_HEADER";\n\t\t\t\tempty2 = "ID_UI_AVA_PLACEMENT_EXPIRES_SOON";\n\t\t\t}\n\t\t\telse\n\t\t\t{\n\t\t\t\tempty = "ID_UI_AVA_QUEST_EXPIRING_SOON_HEADER";\n\t\t\t\tempty2 = "ID_UI_AVA_QUEST_EXPIRES_SOON";\n\t\t\t}\n\t\t\tbreak;\n\t\t}\n\t\t}'''
        new = '''\t\tswitch (category)\n\t\t{\n\t\tcase "AvA":\n\t\t{\n\t\t\tActiveQuest activeQuest = ((!QuestsManager.Instance.avaPlacementQuests.IsNullOrEmpty()) ? QuestsManager.Instance.avaPlacementQuests[0] : null);\n\t\t\tif (activeQuest != null)\n\t\t\t{\n\t\t\t\tempty = "ID_UI_AVA_PLACEMENT_EXPIRING_SOON_HEADER";\n\t\t\t\tempty2 = "ID_UI_AVA_PLACEMENT_EXPIRES_SOON";\n\t\t\t}\n\t\t\telse\n\t\t\t{\n\t\t\t\tempty = "ID_UI_AVA_QUEST_EXPIRING_SOON_HEADER";\n\t\t\t\tempty2 = "ID_UI_AVA_QUEST_EXPIRES_SOON";\n\t\t\t}\n\t\t\tbreak;\n\t\t}\n\t\tcase "AvE":\n\t\tdefault:\n\t\t\tempty = "ID_UI_AVESELECTION_QUEST_EXPIRING_SOON_HEADER";\n\t\t\tempty2 = "ID_UI_AVESELECTION_QUEST_EXPIRES_SOON";\n\t\t\tbreak;\n\t\t}'''
        if old in source:
            source = source.replace(old, new, 1)
            changes.append("restore AvA/AvE expiring switch cases")
    if path.name == "AllianceMembersState.cs":
        old = '''switch (err)\n\t\t\t\t{\n\t\t\t\tdefault:\n\t\t\t\t{\n\t\t\t\t\tint num;\n\t\t\t\t\tif (num == 1)\n\t\t\t\t\t{\n\t\t\t\t\t\tAllianceUtil.GoToAlliance();\n\t\t\t\t\t}\n\t\t\t\t\tbreak;\n\t\t\t\t}\n\t\t\t\tcase "privateAlliance":\n\t\t\t\tcase "nonJoinableAlliance":\n\t\t\t\tcase "full":\n\t\t\t\t\tbreak;\n\t\t\t\t}'''
        new = '''switch (err)\n\t\t\t\t{\n\t\t\t\tcase "privateAlliance":\n\t\t\t\tcase "nonJoinableAlliance":\n\t\t\t\tcase "full":\n\t\t\t\t\tbreak;\n\t\t\t\tcase "notFound":\n\t\t\t\t\tAllianceUtil.GoToAlliance();\n\t\t\t\t\tbreak;\n\t\t\t\tdefault:\n\t\t\t\t\tbreak;\n\t\t\t\t}'''
        if old in source:
            source = source.replace(old, new, 1)
            changes.append("restore AllianceMembersState error switch cases")
        elif "notFound" not in source and "switch (err)" in source:
            match = re.search(r"(?ms)(?P<indent>\t+)switch \(err\)\n.*?(?=\n\t+\}\)\);)", source)
            if match:
                indent = match.group("indent")
                body_indent = indent + "\t"
                source = source[:match.start()] + (
                    f'{indent}switch (err)\n'
                    f'{body_indent}{{\n'
                    f'{body_indent}case "privateAlliance":\n'
                    f'{body_indent}case "nonJoinableAlliance":\n'
                    f'{body_indent}case "full":\n'
                    f'{body_indent}\tbreak;\n'
                    f'{body_indent}case "notFound":\n'
                    f'{body_indent}\tAllianceUtil.GoToAlliance();\n'
                    f'{body_indent}\tbreak;\n'
                    f'{body_indent}default:\n'
                    f'{body_indent}\tbreak;\n'
                    f'{body_indent}}}'
                ) + source[match.end():]
                changes.append("restore AllianceMembersState error switch cases")
    if path.name == "BattleArbiter.cs":
        old = '''\t\tswitch (base.CurrentStateName)\n\t\t{\n\t\tcase "Init":\n\t\t\treturn;\n\t\t}\n\t\tint num;\n\t\tif (num != 1)\n\t\t{\n\t\t\tExit();\n\t\t}'''
        new = '''\t\tswitch (base.CurrentStateName)\n\t\t{\n\t\tcase "Init":\n\t\t\treturn;\n\t\tcase "Exit":\n\t\t\treturn;\n\t\tdefault:\n\t\t\tExit();\n\t\t\treturn;\n\t\t}'''
        if old in source:
            source = source.replace(old, new, 1)
            changes.append("restore BattleArbiter Disconnect switch cases")
    if path.name == "BuffConditionFactory.cs":
        old = '''\t\tswitch (key)\n\t\t{\n\t\tdefault:\n\t\t{\n\t\t\tint num;\n\t\t\tif (num == 1)\n\t\t\t{\n\t\t\t\tbuffCondition = new ActiveId_BuffCondition(target, rhs, op);\n\t\t\t\tbreak;\n\t\t\t}\n\t\t\tfor (int i = 0; i < _customFactories.Count; i++)\n\t\t\t{\n\t\t\t\tbuffCondition = _customFactories[i].CreateCondition(target, key, op, rhs);\n\t\t\t\tif (buffCondition != null)\n\t\t\t\t{\n\t\t\t\t\tbreak;\n\t\t\t\t}\n\t\t\t}\n\t\t\tif (buffCondition == null)\n\t\t\t{\n\t\t\t\tbuffCondition = new BuffFloatCondition(BuffValueFactory.Instance.CreateFloatValue(lhs), BuffValueFactory.Instance.CreateFloatValue(rhs), op);\n\t\t\t}\n\t\t\tbreak;\n\t\t}\n\t\tcase "status":\n\t\t\tbuffCondition = new ContainsBuff_BuffCondition(target, rhs, op);\n\t\t\tbreak;\n\t\t}'''
        new = '''\t\tswitch (key)\n\t\t{\n\t\tcase "status":\n\t\t\tbuffCondition = new ContainsBuff_BuffCondition(target, rhs, op);\n\t\t\tbreak;\n\t\tcase "activeId":\n\t\t\tbuffCondition = new ActiveId_BuffCondition(target, rhs, op);\n\t\t\tbreak;\n\t\tdefault:\n\t\t\tfor (int i = 0; i < _customFactories.Count; i++)\n\t\t\t{\n\t\t\t\tbuffCondition = _customFactories[i].CreateCondition(target, key, op, rhs);\n\t\t\t\tif (buffCondition != null)\n\t\t\t\t{\n\t\t\t\t\tbreak;\n\t\t\t\t}\n\t\t\t}\n\t\t\tif (buffCondition == null)\n\t\t\t{\n\t\t\t\tbuffCondition = new BuffFloatCondition(BuffValueFactory.Instance.CreateFloatValue(lhs), BuffValueFactory.Instance.CreateFloatValue(rhs), op);\n\t\t\t}\n\t\t\tbreak;\n\t\t}'''
        if old in source:
            source = source.replace(old, new, 1)
            changes.append("restore BuffConditionFactory switch cases")
    if path.name == "CustomStoreScreenPresentation.cs":
        old = '''\t\tswitch (_currentTabId)\n\t\t{\n\t\tdefault:\n\t\t{\n\t\t\tint num;\n\t\t\tif (num == 1)\n\t\t\t{\n\t\t\t\tTransformersMenuBar.Instance.SetSoftCurrencyTab("Raidchips");\n\t\t\t}\n\t\t\telse\n\t\t\t{\n\t\t\t\tTransformersMenuBar.Instance.SetSoftCurrencyTab(string.Empty);\n\t\t\t}\n\t\t\tbreak;\n\t\t}\n\t\tcase "loyalty":\n\t\t\tTransformersMenuBar.Instance.SetSoftCurrencyTab("Loyalty");\n\t\t\tbreak;\n\t\t}'''
        new = '''\t\tswitch (_currentTabId)\n\t\t{\n\t\tcase "loyalty":\n\t\t\tTransformersMenuBar.Instance.SetSoftCurrencyTab("Loyalty");\n\t\t\tbreak;\n\t\tcase "vs":\n\t\t\tTransformersMenuBar.Instance.SetSoftCurrencyTab("Raidchips");\n\t\t\tbreak;\n\t\tdefault:\n\t\t\tTransformersMenuBar.Instance.SetSoftCurrencyTab(string.Empty);\n\t\t\tbreak;\n\t\t}'''
        if old in source:
            source = source.replace(old, new, 1)
            changes.append("restore CustomStoreScreenPresentation switch cases")
    if path.as_posix().endswith("CustomRedeemerMappings/ResourceRedeemerMapping.cs"):
        old = '''\t\tGameObject gameObject = null;\n\t\tint num;\n\t\tgameObject = resourceType switch\n\t\t{\n\t\t\t"sc" => Resources.Load("UI/Misc/SoftCurrencyResourceTrail") as GameObject, \n\t\t\t_ => (num != 1) ? (Resources.Load("UI/Misc/GenericResourceTrail") as GameObject) : (Resources.Load("UI/Misc/HardCurrencyResourceTrail") as GameObject), \n\t\t};'''
        new = '''\t\tGameObject gameObject = null;\n\t\tif (resourceType == "sc")\n\t\t{\n\t\t\tgameObject = Resources.Load("UI/Misc/SoftCurrencyResourceTrail") as GameObject;\n\t\t}\n\t\telse if (resourceType == "hc")\n\t\t{\n\t\t\tgameObject = Resources.Load("UI/Misc/HardCurrencyResourceTrail") as GameObject;\n\t\t}\n\t\telse\n\t\t{\n\t\t\tgameObject = Resources.Load("UI/Misc/GenericResourceTrail") as GameObject;\n\t\t}'''
        if old in source:
            source = source.replace(old, new, 1)
            changes.append("restore ResourceRedeemerMapping resource switch cases")
    if path.name == "FlashSaleItem.cs":
        old = '''\t\tswitch (offer.Type)\n\t\t{\n\t\tdefault:\n\t\t{\n\t\t\tint num3;\n\t\t\tif (num3 == 1)\n\t\t\t{\n\t\t\t\tBCGEvoBlueprintBase evoBlueprint = BCGHelper.GetEvoBlueprint(offer.Data);\n\t\t\t\tif (evoBlueprint != null && BCGHelper.IsCatalyst(evoBlueprint))\n\t\t\t\t{\n\t\t\t\t\tnum = BCGHelper.GetNumBlueprints(evoBlueprint.EvoBlueprint);\n\t\t\t\t\tif (BCGHelper.IsConnected())\n\t\t\t\t\t{\n\t\t\t\t\t\tnum2 = BCGManager.Instance.UserData.GetInventoryMax(evoBlueprint.EvoBlueprint);\n\t\t\t\t\t}\n\t\t\t\t}\n\t\t\t}\n\t\t\telse\n\t\t\t{\n\t\t\t\tGameStoreItem item = Hub.Instance.GameStoreManager.GetItem(offer.Data);\n\t\t\t\tif (item != null)\n\t\t\t\t{\n\t\t\t\t\tnum = Hub.Instance.InventoryManager.GetCount(item.Name);\n\t\t\t\t\tnum2 = item.InventoryMax;\n\t\t\t\t}\n\t\t\t}\n\t\t\tbreak;\n\t\t}\n\t\tcase "skl":\n\t\tcase "hero":\n\t\tcase "bp":\n\t\tcase "rhero":\n\t\t\tbreak;\n\t\t}'''
        new = '''\t\tswitch (offer.Type)\n\t\t{\n\t\tcase "skl":\n\t\tcase "hero":\n\t\tcase "bp":\n\t\tcase "rhero":\n\t\t\tbreak;\n\t\tcase "ebp":\n\t\tcase "ebpf":\n\t\tcase "autoconvert_ebp":\n\t\tcase "overflow_ebp":\n\t\t{\n\t\t\tBCGEvoBlueprintBase evoBlueprint = BCGHelper.GetEvoBlueprint(offer.Data);\n\t\t\tif (evoBlueprint != null && BCGHelper.IsCatalyst(evoBlueprint))\n\t\t\t{\n\t\t\t\tnum = BCGHelper.GetNumBlueprints(evoBlueprint.EvoBlueprint);\n\t\t\t\tif (BCGHelper.IsConnected())\n\t\t\t\t{\n\t\t\t\t\tnum2 = BCGManager.Instance.UserData.GetInventoryMax(evoBlueprint.EvoBlueprint);\n\t\t\t\t}\n\t\t\t}\n\t\t\tbreak;\n\t\t}\n\t\tdefault:\n\t\t{\n\t\t\tGameStoreItem item = Hub.Instance.GameStoreManager.GetItem(offer.Data);\n\t\t\tif (item != null)\n\t\t\t{\n\t\t\t\tnum = Hub.Instance.InventoryManager.GetCount(item.Name);\n\t\t\t\tnum2 = item.InventoryMax;\n\t\t\t}\n\t\t\tbreak;\n\t\t}\n\t\t}'''
        if old in source:
            source = source.replace(old, new, 1)
            changes.append("restore FlashSaleItem offer switch cases")
    if path.name == "HeroConsumablesPopupPresentation.cs":
        old = '''\t\t\t\tswitch (_mode)\n\t\t\t\t{\n\t\t\t\tdefault:\n\t\t\t\t{\n\t\t\t\t\tint num;\n\t\t\t\t\tif (num != 1)\n\t\t\t\t\t{\n\t\t\t\t\t}\n\t\t\t\t\tif (activeTeam != null && _config.dataProvder.GetCurrentHero() != null)\n\t\t\t\t\t{\n\t\t\t\t\t\tfloat currentHeroHealth = _config.dataProvder.GetCurrentHeroHealth();\n\t\t\t\t\t\tflag = currentHeroHealth > 0f && currentHeroHealth < 1f && gameStoreInventoryItem != null && gameStoreInventoryItem.GetGameStoreItem().IsHeal();\n\t\t\t\t\t\tflag2 = currentHeroHealth == 0f && gameStoreInventoryItem != null && gameStoreInventoryItem.GetGameStoreItem().IsRevive();\n\t\t\t\t\t}\n\t\t\t\t\tbreak;\n\t\t\t\t}\n\t\t\t\tcase "kTeam":\n\t\t\t\t{\n\t\t\t\t\tfor (int i = 0; i < activeTeam.size; i++)\n\t\t\t\t\t{\n\t\t\t\t\t\tfloat hP = activeTeam.GetHP(i);\n\t\t\t\t\t\tflag |= hP > 0f && hP < 1f && gameStoreInventoryItem != null && gameStoreInventoryItem.GetGameStoreItem().IsHeal();\n\t\t\t\t\t\tflag2 |= hP == 0f && gameStoreInventoryItem != null && gameStoreInventoryItem.GetGameStoreItem().IsRevive();\n\t\t\t\t\t}\n\t\t\t\t\tbreak;\n\t\t\t\t}\n\t\t\t\t}'''
        new = '''\t\t\t\tswitch (_mode)\n\t\t\t\t{\n\t\t\t\tcase "kTeam":\n\t\t\t\t{\n\t\t\t\t\tfor (int i = 0; i < activeTeam.size; i++)\n\t\t\t\t\t{\n\t\t\t\t\t\tfloat hP = activeTeam.GetHP(i);\n\t\t\t\t\t\tflag |= hP > 0f && hP < 1f && gameStoreInventoryItem != null && gameStoreInventoryItem.GetGameStoreItem().IsHeal();\n\t\t\t\t\t\tflag2 |= hP == 0f && gameStoreInventoryItem != null && gameStoreInventoryItem.GetGameStoreItem().IsRevive();\n\t\t\t\t\t}\n\t\t\t\t\tbreak;\n\t\t\t\t}\n\t\t\t\tcase "kSingle":\n\t\t\t\tdefault:\n\t\t\t\t\tif (activeTeam != null && _config.dataProvder.GetCurrentHero() != null)\n\t\t\t\t\t{\n\t\t\t\t\t\tfloat currentHeroHealth = _config.dataProvder.GetCurrentHeroHealth();\n\t\t\t\t\t\tflag = currentHeroHealth > 0f && currentHeroHealth < 1f && gameStoreInventoryItem != null && gameStoreInventoryItem.GetGameStoreItem().IsHeal();\n\t\t\t\t\t\tflag2 = currentHeroHealth == 0f && gameStoreInventoryItem != null && gameStoreInventoryItem.GetGameStoreItem().IsRevive();\n\t\t\t\t\t}\n\t\t\t\t\tbreak;\n\t\t\t\t}'''
        if old in source:
            source = source.replace(old, new, 1)
            changes.append("restore HeroConsumablesPopupPresentation mode switch cases")
    if path.as_posix().endswith("Legacy/ActiveQuest.cs"):
        old = '''\t\tswitch (text3)\n\t\t{\n\t\tdefault:\n\t\t{\n\t\t\tint num12;\n\t\t\tif (num12 == 1)\n\t\t\t{\n\t\t\t\tphase = Phase.Defend;\n\t\t\t}\n\t\t\telse\n\t\t\t{\n\t\t\t\tphase = Phase.Attack;\n\t\t\t}\n\t\t\tbreak;\n\t\t}\n\t\tcase "placement":\n\t\t\tphase = Phase.Placement;\n\t\t\tbreak;\n\t\t}'''
        new = '''\t\tswitch (text3)\n\t\t{\n\t\tcase "placement":\n\t\t\tphase = Phase.Placement;\n\t\t\tbreak;\n\t\tcase "defend":\n\t\t\tphase = Phase.Defend;\n\t\t\tbreak;\n\t\tdefault:\n\t\t\tphase = Phase.Attack;\n\t\t\tbreak;\n\t\t}'''
        if old in source:
            source = source.replace(old, new, 1)
            changes.append("restore ActiveQuest phase switch cases")
    if path.as_posix().endswith("EB.UI.Gacha/GachaPurchasePresentation.cs"):
        old = '''\t\tswitch (tabId)\n\t\t{\n\t\tdefault:\n\t\t{\n\t\t\tint num;\n\t\t\tif (num == 1)\n\t\t\t{\n\t\t\t\t_currentTab = GachaTab.SPECIAL;\n\t\t\t}\n\t\t\telse\n\t\t\t{\n\t\t\t\t_currentTab = GachaTab.CRYSTAL;\n\t\t\t}\n\t\t\tbreak;\n\t\t}\n\t\tcase "shards":\n\t\t\t_currentTab = GachaTab.SHARDS;\n\t\t\tbreak;\n\t\t}'''
        new = '''\t\tswitch (tabId)\n\t\t{\n\t\tcase "shards":\n\t\t\t_currentTab = GachaTab.SHARDS;\n\t\t\tbreak;\n\t\tcase "special":\n\t\t\t_currentTab = GachaTab.SPECIAL;\n\t\t\tbreak;\n\t\tdefault:\n\t\t\t_currentTab = GachaTab.CRYSTAL;\n\t\t\tbreak;\n\t\t}'''
        if old in source:
            source = source.replace(old, new, 1)
            changes.append("restore GachaPurchasePresentation tab switch cases")
    if path.name == "StateMachine.cs" and "AddState(new T" in source:
        source = source.replace(
            "AddState(new T\n\t\t{\n\t\t\tStateName = stateName\n\t\t});",
            "State state = System.Activator.CreateInstance<T>();\n"
            "\t\tstate.StateName = stateName;\n\t\tAddState(state);", 1)
        changes.append("restore Activator.CreateInstance from IL")
    if path.name == "MOTDWidget.cs" and "new EB.Action<TransformersMOTDItem, MOTDTemplate>(HandleMOTDClick)" in source:
        source = source.replace(
            "new EB.Action<TransformersMOTDItem, MOTDTemplate>(HandleMOTDClick)",
            "new EB.Action<TransformersMOTDItem, MOTDTemplate>((item, data) => "
            "HandleMOTDClick(item, data, null))", 1)
        changes.append("adapt optional MOTD callback to two-argument delegate")
    if path.name == "HeroesScreen.cs" and "delegate(Building building)" in source:
        source = source.replace("delegate(Building building)", "delegate(Building callbackBuilding)", 1)
        source = source.replace("\t\t\tif (building != null)", "\t\t\tif (callbackBuilding != null)", 1)
        source = source.replace("BuildingDetailsScreen.Show(building, list);", "BuildingDetailsScreen.Show(callbackBuilding, list);", 1)
        changes.append("rename shadowed HeroesScreen callback parameter")

    # The original methods mark the later value as Optional in metadata.  The
    # decompiler prints that flag without a C# default, which makes all prior
    # defaults illegal and makes callers that omit the value fail.  A default
    # value is a source-only spelling of the same metadata contract.
    optional_value = re.compile(
        r"\[Optional\]\s+(?P<type>[^,\s]+(?:<[^>]+>)?(?:\[\])?)\s+(?P<name>\w+)"
        r"(?=\s*[,\)])")
    source, count = optional_value.subn(
        lambda m: f"{m.group('type')} {m.group('name')} = default", source)
    if count:
        changes.append(f"add {count} defaults for metadata-optional parameters")

    # Unity's stripped reference exposes setter-only attribute properties. A
    # named attribute argument is therefore not a legal C# declaration even
    # though the setter is present in the IL. Keep the attribute itself while
    # omitting only the unverifiable named argument in the compile snapshot.
    source, count = re.subn(r"\[CreateAssetMenu\(menuName\s*=\s*\"[^\"]*\"\)\]",
                            "[CreateAssetMenu]", source)
    if count:
        changes.append(f"remove {count} stripped CreateAssetMenu named arguments")
    source, count = re.subn(r"\[Header\((\"[^\"]*\"),\s*order\s*=\s*\d+\)\]",
                            r"[Header(\1)]", source)
    if count:
        changes.append(f"remove {count} stripped Header named arguments")
    return source, changes


def project_references(workspace, name):
    """Return assembly names referenced by an exported project file."""
    project = workspace / "source" / name / f"{name}.csproj"
    if not project.is_file():
        return set()
    try:
        root = ET.fromstring(project.read_text())
    except ET.ParseError:
        return set()
    refs = set()
    for item in root.findall(".//Reference"):
        include = item.attrib.get("Include", "").split(",", 1)[0]
        if include:
            refs.add(include)
    return refs


def dependency_order(workspace, names):
    """Topologically order selected projects using ILSpy project references."""
    selected = set(names)
    visiting = set()
    visited = set()
    ordered = []

    def visit(name):
        if name in visited:
            return
        if name in visiting:
            raise ValueError(f"Circular selected assembly dependency involving {name}")
        visiting.add(name)
        for dependency in sorted(project_references(workspace, name) & selected):
            visit(dependency)
        visiting.remove(name)
        visited.add(name)
        ordered.append(name)

    for name in names:
        visit(name)
    return ordered


def compile_audit(workspace, dotnet, csc, reference_dirs=(), assemblies=None,
                  repair_accessors=False, repair_contracts=False):
    """Compile recovered game code against the APK's own framework, without NuGet."""
    manifest = json.loads((workspace / "manifest.json").read_text())
    # Verify input provenance before using assemblies as compiler references.
    for row in manifest["assemblies"]:
        if digest(workspace / "managed" / row["name"]) != row["sha256"]:
            raise ValueError(f"Assembly changed since export: {row['name']}")
    names = assemblies or ["Assembly-CSharp-firstpass", "Assembly-CSharp"]
    available = {Path(row["name"]).stem for row in manifest["assemblies"]}
    if not names or any(name not in available for name in names):
        raise ValueError("Select assembly names present in the export manifest")
    names = dependency_order(workspace, names)
    references = {p.name: p for p in (workspace / "managed").glob("*.dll")}
    overrides = {}
    for directory in reference_dirs:
        dlls = sorted(directory.resolve().glob("*.dll"))
        if not dlls:
            raise ValueError(f"No reference assemblies in {directory}")
        for path in dlls:
            if path.name in references:
                references[path.name] = path
                overrides[path.name] = {"path": str(path), "sha256": digest(path)}
    audit = Path(tempfile.mkdtemp(prefix="compile-", dir=workspace))
    output_dir = audit / "bin"
    output_dir.mkdir()
    report = {"scope": "game assemblies against original APK references; not a Unity build",
              "compiler": str(csc), "compiler_sha256": digest(csc),
              "reference_overrides": overrides, "runtime_verified": False, "assemblies": []}
    compiled_refs = {}
    for name in names:
        sources = sorted((workspace / "source" / name).rglob("*.cs"))
        if not sources:
            report["assemblies"].append({"name": name, "status": "missing-source"})
            continue
        source_hashes = {p.relative_to(workspace).as_posix(): digest(p) for p in sources}
        changes = []
        snapshot = audit / "source" / name
        for path in sources:
            relative = path.relative_to(workspace / "source" / name)
            target = snapshot / relative
            target.parent.mkdir(parents=True, exist_ok=True)
            text = path.read_text()
            if repair_accessors:
                text, count = normalize_accessors(text)
                if count:
                    changes.append({"path": relative.as_posix(), "explicit_getters": count})
            if repair_contracts:
                text, contract_changes = normalize_source_contracts(relative, text)
                if contract_changes:
                    changes.append({"path": relative.as_posix(), "contracts": contract_changes})
            target.write_text(text)
        sources = sorted(snapshot.rglob("*.cs"))
        flags = ["-nologo", "-target:library", "-unsafe", "-nostdlib+", "-langversion:12",
                 "-deterministic+", f'-pathmap:"{audit}=/_/reconstruction"',
                 f'-out:"{audit / (name + ".dll")}"']
        reference_paths = []
        for p in sorted(references.values()):
            if p.stem == name:
                continue
            reference_paths.append(compiled_refs.get(p.stem, p))
        flags += [f'-reference:"{p}"' for p in reference_paths]
        flags += [f'"{p}"' for p in sources]
        output_path = output_dir / f"{name}.dll"
        flags[7] = f'-out:"{output_path}"'
        rsp = audit / f"{name}.rsp"
        rsp.write_text("\n".join(flags) + "\n")
        log = audit / f"{name}.log"
        with log.open("w") as output:
            result = subprocess.run([dotnet, str(csc), f"@{rsp}"], stdout=output,
                                    stderr=subprocess.STDOUT)
        errors = Counter(re.findall(r"\berror (CS\d+):", log.read_text()))
        report["assemblies"].append({"name": name, "exit_code": result.returncode,
                                     "status": "compiled" if result.returncode == 0 else "failed",
                                     "errors": dict(sorted(errors.items())),
                                     "source_hashes": source_hashes,
                                     "compiled_source_hashes": {
                                         p.relative_to(audit).as_posix(): digest(p) for p in sources},
                                     "repairs": changes,
                                     "reference_paths": [str(p) for p in reference_paths],
                                     "output_sha256": digest(output_path)
                                     if result.returncode == 0 else None,
                                     "output_path": output_path.relative_to(audit).as_posix()})
        if result.returncode == 0:
            compiled_refs[name] = output_path
    save(audit / "report.json", report)
    success = all(r["status"] == "compiled" for r in report["assemblies"])
    manifest["compilation"] = "selected-assemblies-compiled" if success else "failed"
    manifest["latest_compile_report"] = (audit / "report.json").relative_to(workspace).as_posix()
    save(workspace / "manifest.json", manifest)
    print(json.dumps([{k: v for k, v in row.items()
                       if k not in ("source_hashes", "compiled_source_hashes")}
                      for row in report["assemblies"]], indent=2))
    print(f"Report: {audit / 'report.json'}")
    return 0 if success else 1


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    export = commands.add_parser("export", help="Recover every managed DLL into an isolated workspace")
    export.add_argument("apk", type=Path)
    export.add_argument("--ilspy", default=os.environ.get("ILSPYCMD", "ilspycmd"))
    il_export = commands.add_parser("export-il", help="Export original IL without C# repairs")
    il_export.add_argument("workspace", type=Path)
    il_export.add_argument("--ilspy", default=os.environ.get("ILSPYCMD", "ilspycmd"))
    metadata = commands.add_parser("metadata-audit", help="Compare source declarations with original PE metadata")
    metadata.add_argument("workspace", type=Path)
    metadata.add_argument("--dotnet", default="dotnet")
    metadata.add_argument("--metadata-tool", required=True, type=Path,
                          help="Built MonoMetadata .NET tool assembly")
    metadata.add_argument("--assembly", action="append", help="Assembly name without .dll (repeatable)")
    plan = commands.add_parser("replacement-plan", help="Report the managed dependency closure for replacement roots")
    plan.add_argument("workspace", type=Path)
    plan.add_argument("--dotnet", default="dotnet")
    plan.add_argument("--metadata-tool", required=True, type=Path,
                      help="Built MonoMetadata .NET tool assembly")
    plan.add_argument("--root", action="append", required=True,
                      help="Managed replacement root without .dll (repeatable)")
    audit = commands.add_parser("compile-audit", help="Measure game-source compiler blockers")
    audit.add_argument("workspace", type=Path)
    audit.add_argument("--dotnet", default="dotnet")
    audit.add_argument("--csc", required=True, type=Path, help="SDK Roslyn/bincore/csc.dll")
    audit.add_argument("--reference-dir", action="append", default=[], type=Path,
                       help="Override matching APK references with unstripped SDK DLLs (repeatable)")
    audit.add_argument("--assembly", action="append", help="Assembly name without .dll (repeatable)")
    audit.add_argument("--repair-accessors", action="store_true",
                       help="Normalize simple explicit getters in a local source snapshot")
    audit.add_argument("--repair-contracts", action="store_true",
                       help="Apply IL-backed compiler-contract repairs in a local source snapshot")
    args = parser.parse_args()
    try:
        if args.command == "export":
            return recover(args.apk.resolve(), args.ilspy)
        if args.command == "export-il":
            return export_il(args.workspace.resolve(), args.ilspy)
        if args.command == "metadata-audit":
            return metadata_audit(args.workspace.resolve(), args.dotnet,
                                  args.metadata_tool.resolve(), args.assembly)
        if args.command == "replacement-plan":
            return replacement_plan(args.workspace.resolve(), args.dotnet,
                                    args.metadata_tool.resolve(), args.root)
        return compile_audit(args.workspace.resolve(), args.dotnet, args.csc.resolve(),
                             args.reference_dir, args.assembly, args.repair_accessors,
                             args.repair_contracts)
    except (OSError, ValueError, zipfile.BadZipFile, subprocess.CalledProcessError) as error:
        parser.exit(1, f"Error: {error}\n")


if __name__ == "__main__":
    raise SystemExit(main())
