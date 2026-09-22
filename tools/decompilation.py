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


def _named_items(items, key="name"):
    return {item[key]: item for item in items}


def _value_difference(original, compiled):
    if original == compiled:
        return None
    return {"original": original, "compiled": compiled}


def _is_compiler_generated_type(name):
    """Recognize common compiler-generated type names without hiding them.

    This is only a reporting classification.  Generated types remain in the
    exact added/removed/changed metadata diff because their names can still be
    relevant to Unity serialization or reflection.
    """
    return (name.startswith("<PrivateImplementationDetails>")
            or "+<>" in name
            or "+<" in name
            or "c__DisplayClass" in name
            or "c__Iterator" in name
            or "d__" in name)


def _generated_type_summary(type_diff):
    added = [name for name in type_diff["added"] if _is_compiler_generated_type(name)]
    removed = [name for name in type_diff["removed"] if _is_compiler_generated_type(name)]
    changed = [name for name in type_diff["changed"] if _is_compiler_generated_type(name)]
    return {"added": added, "removed": removed, "changed": changed,
            "added_count": len(added), "removed_count": len(removed),
            "changed_count": len(changed)}


def _framework_reference_changes(reference_diff):
    """Separate framework identity drift from application-reference changes."""
    names = set(reference_diff["changed"])
    framework = sorted(name for name in names
                       if name == "mscorlib" or name == "Mono.Security"
                       or name == "System" or name.startswith("System."))
    return {name: reference_diff["changed"][name] for name in framework}


def _api_visibility(flags, kind):
    """Return a review bucket while retaining exact ECMA-335 visibility elsewhere."""
    visibility = _metadata_visibility(flags, kind)
    if kind == "type":
        public = {"public", "nested-public", "nested-family", "nested-fam-or-assem"}
    else:
        public = {"public", "family", "fam-or-assem"}
    return "public" if visibility in public else "non-public"


def _member_visibility_diff(original, compiled, type_diff):
    """Summarize API visibility for exact type/member additions and removals.

    This is intentionally a classification of the exact diff, not a compatibility
    verdict.  Private, internal, and nested non-public declarations share the
    ``non-public`` review bucket; their raw ECMA-335 flags remain in ``differences``.
    """
    def member_visibility(type_name, type_info, kind, member):
        if kind in ("fields", "methods"):
            return _api_visibility(member["flags"], "member")
        # PropertyAttributes/EventAttributes contain no accessibility bits.
        # Resolve the MethodSemantics accessors emitted by the metadata helper.
        roles = ("getter", "setter") if kind == "properties" else ("adder", "remover", "raiser")
        accessors = [member.get(role, "") for role in roles] + member.get("others", [])
        buckets = []
        prefix = type_name + "::"
        for accessor in filter(None, accessors):
            method = (type_info["methods"].get(accessor[len(prefix):])
                      if accessor.startswith(prefix) else None)
            buckets.append(_api_visibility(method["flags"], "member") if method else "unknown")
        if "public" in buckets:
            return "public"
        return "unknown" if not buckets or "unknown" in buckets else "non-public"

    result = {
        "types": {"added": {"public": [], "non-public": []},
                  "removed": {"public": [], "non-public": []},
                  "changed": {"public": [], "non-public": []}},
        "members": {action: {bucket: [] for bucket in ("public", "non-public", "unknown")}
                    for action in ("added", "removed", "changed")},
        "visibility_changed": [],
    }
    original_types = original["types"]
    compiled_types = compiled["types"]
    for name in type_diff["added"]:
        bucket = _api_visibility(compiled_types[name]["flags"], "type")
        result["types"]["added"][bucket].append(name)
    for name in type_diff["removed"]:
        bucket = _api_visibility(original_types[name]["flags"], "type")
        result["types"]["removed"][bucket].append(name)
    for name, change in type_diff["changed"].items():
        old_bucket = _api_visibility(original_types[name]["flags"], "type")
        new_bucket = _api_visibility(compiled_types[name]["flags"], "type")
        result["types"]["changed"][old_bucket].append(name)
        if old_bucket != new_bucket:
            result["visibility_changed"].append({"kind": "type", "name": name,
                                                   "original": old_bucket, "compiled": new_bucket})

        def member_map(type_info, member_kind):
            members = type_info[member_kind]
            if isinstance(members, dict):
                return members
            if member_kind == "properties":
                return {f"{item['name']}:{item['signature']}": item for item in members}
            return {f"{item['name']}:{item['type']}": item for item in members}

        for member_kind in ("fields", "methods", "properties", "events"):
            old_members = member_map(original_types[name], member_kind)
            new_members = member_map(compiled_types[name], member_kind)
            added = set(new_members) - set(old_members)
            removed = set(old_members) - set(new_members)
            old_visibility = {key: member_visibility(name, original_types[name], member_kind, value)
                              for key, value in old_members.items()}
            new_visibility = {key: member_visibility(name, compiled_types[name], member_kind, value)
                              for key, value in new_members.items()}
            changed = {member for member in set(old_members) & set(new_members)
                       if old_members[member] != new_members[member]
                       or old_visibility[member] != new_visibility[member]}
            for action, members, visibility in (("added", added, new_visibility),
                                                ("removed", removed, old_visibility),
                                                ("changed", changed, old_visibility)):
                for member in sorted(members):
                    bucket = visibility[member]
                    result["members"][action][bucket].append(f"{name}::{member_kind}:{member}")
                    if action == "changed":
                        old_bucket = old_visibility[member]
                        new_bucket = new_visibility[member]
                        if old_bucket != new_bucket:
                            result["visibility_changed"].append(
                                {"kind": member_kind, "name": f"{name}::{member}",
                                 "original": old_bucket, "compiled": new_bucket})

    for section in result["types"].values():
        for names in section.values():
            names.sort()
    for section in result["members"].values():
        for names in section.values():
            names.sort()
    result["visibility_changed"].sort(key=lambda item: (item["kind"], item["name"]))
    return result


def _loader_risk_summary(original, compiled, differences, type_diff, reference_diff,
                         resource_diff):
    """Classify metadata differences that can affect managed loading.

    These are review signals over exact metadata differences.  They do not decide
    whether a Unity/Mono loader accepts the output.
    """
    identity_change = differences.get("identity")
    identity_fields = [] if identity_change is None else sorted(
        key for key in set(identity_change["original"]) | set(identity_change["compiled"])
        if identity_change["original"].get(key) != identity_change["compiled"].get(key))
    result = {
        "assembly_identity": identity_fields,
        "metadata_profile": sorted(key for key in ("metadata_version", "machine", "cor_flags", "module_name")
                                    if key in differences),
        "references": {
            "added": reference_diff["added"], "removed": reference_diff["removed"],
            "changed": sorted(reference_diff["changed"]),
            "framework_changed": sorted(_framework_reference_changes(reference_diff)),
        },
        "resources": {
            "added": resource_diff["added"], "removed": resource_diff["removed"],
            "changed": sorted(resource_diff["changed"]),
        },
        "inheritance_or_interfaces": [],
        "serialization_layout": [],
        "native_imports": [],
    }
    for name, change in type_diff["changed"].items():
        if any(key in change for key in ("base_type", "interfaces")):
            result["inheritance_or_interfaces"].append(name)
        if any(key in change for key in ("layout", "field_order", "fields")):
            result["serialization_layout"].append(name)
        for member, entry in change.get("methods", {}).get("changed", {}).items():
            old = entry.get("original", {})
            new = entry.get("compiled", {})
            if old.get("import") != new.get("import") and (
                    old.get("import") is not None or new.get("import") is not None):
                result["native_imports"].append(f"{name}::{member}")
    for key in ("inheritance_or_interfaces", "serialization_layout", "native_imports"):
        result[key].sort()
    return result


def metadata_contract_diff(original, compiled):
    """Compare two helper models without loading either managed assembly.

    This is a contract diff, not a semantic or runtime comparison. It reports
    added/removed/changed metadata so an isolated compiler output can be reviewed
    before anyone considers it for substitution.
    """
    differences = {}
    for key in ("identity", "metadata_version", "machine", "cor_flags", "module_name",
                "assembly_attributes", "module_attributes"):
        change = _value_difference(original.get(key), compiled.get(key))
        if change is not None:
            differences[key] = change

    original_references = _named_items(original["references"])
    compiled_references = _named_items(compiled["references"])
    reference_diff = {
        "added": sorted(set(compiled_references) - set(original_references)),
        "removed": sorted(set(original_references) - set(compiled_references)),
        "changed": {name: _value_difference(original_references[name], compiled_references[name])
                    for name in sorted(set(original_references) & set(compiled_references))
                    if original_references[name] != compiled_references[name]},
    }
    if any(reference_diff.values()):
        differences["references"] = reference_diff

    original_resources = _named_items(original["resources"])
    compiled_resources = _named_items(compiled["resources"])
    resource_diff = {
        "added": sorted(set(compiled_resources) - set(original_resources)),
        "removed": sorted(set(original_resources) - set(compiled_resources)),
        "changed": {name: _value_difference(original_resources[name], compiled_resources[name])
                    for name in sorted(set(original_resources) & set(compiled_resources))
                    if original_resources[name] != compiled_resources[name]},
    }
    if any(resource_diff.values()):
        differences["resources"] = resource_diff

    original_types = original["types"]
    compiled_types = compiled["types"]
    type_diff = {
        "added": sorted(set(compiled_types) - set(original_types)),
        "removed": sorted(set(original_types) - set(compiled_types)),
        "changed": {},
    }
    for name in sorted(set(original_types) & set(compiled_types)):
        old = original_types[name]
        new = compiled_types[name]
        change = {}
        for key in ("flags", "base_type", "layout", "generics", "attributes",
                    "interfaces", "method_impls", "field_order", "properties", "events"):
            value = _value_difference(old.get(key), new.get(key))
            if value is not None:
                change[key] = value

        old_fields = old["fields"]
        new_fields = new["fields"]
        field_diff = {
            "added": sorted(set(new_fields) - set(old_fields)),
            "removed": sorted(set(old_fields) - set(new_fields)),
            "changed": {field: _value_difference(old_fields[field], new_fields[field])
                        for field in sorted(set(old_fields) & set(new_fields))
                        if old_fields[field] != new_fields[field]},
        }
        if any(field_diff.values()):
            change["fields"] = field_diff

        old_methods = old["methods"]
        new_methods = new["methods"]
        method_diff = {
            "added": sorted(set(new_methods) - set(old_methods)),
            "removed": sorted(set(old_methods) - set(new_methods)),
            "changed": {method: _value_difference(old_methods[method], new_methods[method])
                        for method in sorted(set(old_methods) & set(new_methods))
                        if old_methods[method] != new_methods[method]},
        }
        if any(method_diff.values()):
            change["methods"] = method_diff
        if change:
            type_diff["changed"][name] = change
    if any(type_diff.values()):
        differences["types"] = type_diff

    changed_types = len(type_diff["changed"])
    changed_fields = sum(len(value.get("fields", {}).get("changed", {}))
                         for value in type_diff["changed"].values())
    changed_methods = sum(len(value.get("methods", {}).get("changed", {}))
                         for value in type_diff["changed"].values())
    api_visibility = _member_visibility_diff(original, compiled, type_diff)
    loader_risks = _loader_risk_summary(original, compiled, differences, type_diff,
                                         reference_diff, resource_diff)
    return {"equal": not differences,
            "summary": {"original_type_count": len(original_types),
                        "compiled_type_count": len(compiled_types),
                        "added_type_count": len(type_diff["added"]),
                        "removed_type_count": len(type_diff["removed"]),
                        "changed_type_count": changed_types,
                        "changed_field_count": changed_fields,
                        "changed_method_count": changed_methods,
                        "compiler_generated_type_differences": {
                            "added": _generated_type_summary(type_diff)["added_count"],
                            "removed": _generated_type_summary(type_diff)["removed_count"],
                            "changed": _generated_type_summary(type_diff)["changed_count"]},
                        "framework_reference_change_count": len(
                            _framework_reference_changes(reference_diff))},
            "classification": {
                "compiler_generated_type_differences": _generated_type_summary(type_diff),
                "framework_reference_differences": _framework_reference_changes(reference_diff),
                "api_visibility_differences": api_visibility,
                "loader_risk_differences": loader_risks,
                "evidence_note": "Classifications do not remove exact metadata differences or establish runtime compatibility."
            },
            "differences": differences}


def metadata_compare(workspace, dotnet, metadata_tool, compiled_dir, assemblies=None):
    """Compare isolated compiled DLLs with the original managed inputs."""
    manifest = json.loads((workspace / "manifest.json").read_text())
    rows = {Path(row["name"]).stem: row for row in manifest["assemblies"]}
    names = assemblies or sorted(rows)
    if any(name not in rows for name in names):
        raise ValueError("Select assembly names present in the export manifest")
    compiled_dir = compiled_dir.resolve()
    report_dir = Path(tempfile.mkdtemp(prefix="metadata-compare-", dir=workspace))
    report = {"schema": 1,
              "scope": "original PE metadata compared with isolated compiled PE metadata",
              "evidence": "System.Reflection.Metadata; neither assembly is loaded",
              "manifest_input": manifest.get("input"),
              "manifest_sha256": hashlib.sha256((workspace / "manifest.json").read_bytes()).hexdigest(),
              "compiled_directory": str(compiled_dir), "runtime_verified": False,
              "assemblies": []}
    for name in names:
        row = rows[name]
        original_path = workspace / "managed" / row["name"]
        compiled_path = compiled_dir / row["name"]
        if digest(original_path) != row["sha256"]:
            raise ValueError(f"Assembly changed since export: {row['name']}")
        if not compiled_path.is_file():
            raise ValueError(f"Compiled assembly not found: {compiled_path}")
        original = json.loads(subprocess.check_output(
            [dotnet, str(metadata_tool), str(original_path)], text=True))
        compiled = json.loads(subprocess.check_output(
            [dotnet, str(metadata_tool), str(compiled_path)], text=True))
        report["assemblies"].append({"name": name,
                                     "original_sha256": row["sha256"],
                                     "compiled_sha256": digest(compiled_path),
                                     "comparison": metadata_contract_diff(original, compiled)})
    save(report_dir / "report.json", report)
    print(f"Report: {report_dir / 'report.json'}")
    return 0


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


def reference_identity(value, definition=False):
    """Normalize AssemblyDef public keys and AssemblyRef keys/tokens for comparison."""
    key = value["public_key"] if definition else value["public_key_or_token"]
    if key and (definition or value["flags"] & 1):
        key = hashlib.sha1(bytes.fromhex(key)).digest()[-8:][::-1].hex()
    return {"name": value["name"], "version": value["version"],
            "culture": value["culture"], "public_key_token": key.upper()}


def reference_inventory(metadata):
    """Check exact identities within a proposed file set, not Mono binding policy."""
    edges = []
    for consumer, value in sorted(metadata.items()):
        for reference in value["references"]:
            requested = reference_identity(reference)
            provider = metadata.get(reference["name"])
            supplied = reference_identity(provider["identity"], definition=True) if provider else None
            differences = ([key for key in requested if requested[key] != supplied[key]]
                           if supplied else [])
            edges.append({"consumer": consumer, "provider": reference["name"],
                          "requested": requested, "supplied": supplied,
                          "reference_metadata": reference,
                          "status": "missing-provider" if provider is None else
                                    "identity-mismatch" if differences else "exact-identity-match",
                          "differing_identity_fields": differences})
    return {"counts": dict(sorted(Counter(row["status"] for row in edges).items())),
            "edges": edges}


def compile_candidate_paths(workspace, compile_report, roots):
    """Pin explicit roots to successful, hash-verified isolated audit outputs."""
    compile_report = compile_report.resolve()
    if not compile_report.is_relative_to(workspace.resolve()):
        raise ValueError("Compile report must be inside the generated workspace")
    report_hash = digest(compile_report)
    audit = json.loads(compile_report.read_text())
    rows = {}
    for row in audit["assemblies"]:
        if row["name"] in rows:
            raise ValueError(f"Duplicate compile audit assembly: {row['name']}")
        rows[row["name"]] = row
    outputs = {}
    for name in sorted(set(roots)):
        row = rows.get(name)
        if row is None or row.get("status") != "compiled" or row.get("exit_code") != 0:
            raise ValueError(f"No successful compile audit output for root: {name}")
        path = (compile_report.parent / row["output_path"]).resolve()
        expected = compile_report.parent / "bin" / f"{name}.dll"
        if path != expected or path.is_relative_to((workspace / "managed").resolve()):
            raise ValueError(f"Root must use an isolated audit bin output: {name}")
        if digest(path) != row["output_sha256"]:
            raise ValueError(f"Compiled assembly changed since audit: {name}")
        outputs[name] = {"path": path, "sha256": row["output_sha256"]}
    if digest(compile_report) != report_hash:
        raise ValueError("Compile report changed during candidate validation")
    return outputs, {"path": str(compile_report), "sha256": report_hash,
                     "evidence_note": "Output hashes match the audit; compilation was not rerun."}


def substitution_candidate(original, compiled):
    """Assess the combined replacement/retention set without copying any DLL."""
    selected = {**original, **compiled}
    baseline = reference_inventory(original)
    candidate = reference_inventory(selected)
    baseline_issues = {json.dumps(row, sort_keys=True) for row in baseline["edges"]
                       if row["status"] != "exact-identity-match"}
    introduced = [row for row in candidate["edges"]
                  if row["status"] != "exact-identity-match"
                  and json.dumps(row, sort_keys=True) not in baseline_issues]
    baseline_edges = {(row["consumer"], row["provider"]) for row in baseline["edges"]}
    return {
        "replacement_set": sorted(compiled),
        "preserved_originals": sorted(set(original) - set(compiled)),
        "comparisons": {name: metadata_contract_diff(original[name], compiled[name])
                        for name in sorted(compiled)},
        "reference_inventory": {"original_baseline": baseline, "candidate": candidate,
                                "introduced_or_changed_issues": introduced,
                                "added_edges": [row for row in candidate["edges"]
                                                if (row["consumer"], row["provider"]) not in baseline_edges],
                                "retained_consumers_of_replacements": [
                                    row for row in candidate["edges"]
                                    if row["consumer"] not in compiled and row["provider"] in compiled]},
        "status": "review-required",
        "packaging_authorized": False, "runtime_verified": False,
        "limitations": [
            "Exact reference identity matching is not Mono binding-policy or member-resolution verification.",
            "Preserving original dependencies does not establish that rebuilt callers use supported APIs.",
            "Metadata differences retain API, attributes, layout and native-import evidence; no behavioral equivalence is inferred.",
            "Resource contents, FieldRVA contents, type forwarders, Unity asset type trees, native binding resolution and runtime load order are not verified.",
            "This is a file-selection report only; no DLL is copied, installed or packaged."
        ]}


def replacement_plan(workspace, dotnet, metadata_tool, roots, compile_report=None):
    """Write an evidence report for the proposed managed replacement boundary.

    Roots are an explicit engineering choice.  The report computes only the
    transitive managed dependency closure and labels framework/plugin assemblies
    as dependencies to inspect, not as assemblies that may safely be replaced.
    """
    manifest = json.loads((workspace / "manifest.json").read_text())
    rows = {Path(row["name"]).stem: row for row in manifest["assemblies"]}
    if not roots or any(root not in rows for root in roots):
        raise ValueError("Replacement roots must name assemblies present in the export manifest")
    roots = sorted(set(roots))
    outputs, provenance = (compile_candidate_paths(workspace, compile_report, roots)
                           if compile_report else ({}, None))
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
    report = {"schema": 2,
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
    if compile_report:
        compiled = {}
        for name, output in outputs.items():
            compiled[name] = json.loads(subprocess.check_output(
                [dotnet, str(metadata_tool), str(output["path"])], text=True))
            if digest(output["path"]) != output["sha256"]:
                raise ValueError(f"Compiled assembly changed during metadata inspection: {name}")
        candidate = substitution_candidate(metadata, compiled)
        candidate["compile_audit"] = provenance
        candidate["files"] = [
            {"name": name, "apk_path": MANAGED + row["name"],
             "action": "replace" if name in outputs else "preserve-original",
             "original_sha256": row["sha256"],
             "selected_path": str(outputs[name]["path"] if name in outputs else
                                  workspace / "managed" / row["name"]),
             "selected_sha256": outputs[name]["sha256"] if name in outputs else row["sha256"]}
            for name, row in sorted(rows.items())]
        report["substitution_candidate"] = candidate
    # Reject concurrent changes rather than publishing stale provenance.
    for name, row in rows.items():
        if digest(workspace / "managed" / row["name"]) != row["sha256"]:
            raise ValueError(f"Assembly changed during metadata inspection: {name}")
    if compile_report:
        if digest(compile_report.resolve()) != provenance["sha256"]:
            raise ValueError("Compile report changed during metadata inspection")
        for name, output in outputs.items():
            if digest(output["path"]) != output["sha256"]:
                raise ValueError(f"Compiled assembly changed during metadata inspection: {name}")
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


def normalize_source_contracts(path, source, server_endpoint=None, disable_google_play_games=False,
                               runtime_diagnostics=False, disable_push=False,
                               allow_offline_network=False):
    """Apply narrow, IL-backed repairs to declarations rejected by Roslyn.

    ILSpy exposes a few metadata contracts that C# cannot spell directly: a
    type-local ``BindingFlags`` constant, a stripped Unity attribute getter,
    and optional flags on parameters without default constants.  These edits
    are made only in the isolated audit snapshot and are recorded by path.
    """
    changes = []
    if allow_offline_network and path.as_posix().endswith("EB.Sparx/EndPoint.cs"):
        # The reconstructed client is routed to the local revival server. The
        # original endpoint rejects every request when Unity reports no
        # Internet reachability, even when the loopback server is reachable via
        # adb reverse. Keep this authored offline behavior isolated to the
        # replacement snapshot; the exported source and original IL stay intact.
        old = "protected bool HasInternetConnectivity => Application.internetReachability != NetworkReachability.NotReachable;"
        new = "protected bool HasInternetConnectivity => true;"
        if old in source:
            source = source.replace(old, new, 1)
            changes.append("allow local revival-server requests without Internet reachability")
        elif "protected bool HasInternetConnectivity =>" in source:
            raise ValueError(f"EndPoint offline-network repair anchor changed: {path}")
    if path.as_posix().endswith("EB/Assets.cs"):
        # Original IL stores every async resource/bundle callback result in
        # the iterator's obj field and marks checkedResources in the first
        # Resources fallback. ILSpy emitted a dead val/flag pair instead;
        # with an ODRManager present that left obj null and made the outer
        # AssetManager callback receive null even when loading succeeded.
        old = ("\t\t\tT val;\n"
               "\t\t\tif (ODRManager.Instance == null)\n"
               "\t\t\t{\n"
               "\t\t\t\tbool flag;\n"
               "\t\t\t\tyield return LoadFromResources(path, delegate(T resourceObj)\n"
               "\t\t\t\t{\n"
               "\t\t\t\t\tval = resourceObj;\n"
               "\t\t\t\t\tflag = true;\n"
               "\t\t\t\t});\n"
               "\t\t\t}\n"
               "\t\t\tif (obj == null)\n"
               "\t\t\t{\n"
               "\t\t\t\tyield return AssetBundleManager.Instance.LoadAsync(path, delegate(T val2)\n"
               "\t\t\t\t{\n"
               "\t\t\t\t\tval = val2;\n"
               "\t\t\t\t});\n")
        new = ("\t\t\tif (ODRManager.Instance == null)\n"
               "\t\t\t{\n"
               "\t\t\t\tyield return LoadFromResources(path, delegate(T resourceObj)\n"
               "\t\t\t\t{\n"
               "\t\t\t\t\tobj = resourceObj;\n"
               "\t\t\t\t\tcheckedResources = true;\n"
               "\t\t\t\t});\n"
               "\t\t\t}\n"
               "\t\t\tif (obj == null)\n"
               "\t\t\t{\n"
               "\t\t\t\tyield return AssetBundleManager.Instance.LoadAsync(path, delegate(T val2)\n"
               "\t\t\t\t{\n"
               "\t\t\t\t\tobj = val2;\n"
               "\t\t\t\t});\n")
        if old in source:
            source = source.replace(old, new, 1)
            fallback = ("\t\t\t\tif (obj == null && !checkedResources)\n"
                        "\t\t\t\t{\n"
                        "\t\t\t\t\tyield return LoadFromResources(path, delegate(T resourceObj)\n"
                        "\t\t\t\t\t{\n"
                        "\t\t\t\t\t\tval = resourceObj;\n"
                        "\t\t\t\t\t});\n"
                        "\t\t\t\t}")
            fallback_repaired = fallback.replace("val = resourceObj;", "obj = resourceObj;")
            if source.count(fallback) != 1:
                raise ValueError(f"Assets.DoLoadAsync resource fallback anchor changed: {path}")
            source = source.replace(fallback, fallback_repaired, 1)
            changes.append("restore EB.Assets.DoLoadAsync iterator captures from original IL")
        elif "T val;" in source and "AssetBundleManager.Instance.LoadAsync" in source:
            raise ValueError(f"Assets.DoLoadAsync IL repair anchor changed: {path}")
    if path.as_posix().endswith("EB/AssetManager.cs"):
        # The original LoadInternal iterator captures path, assetType, the two
        # size estimates, and hold. ILSpy emitted unrelated default locals
        # instead of those closure fields, so every asset load callback used
        # null/zero values and could not complete the registry load. The
        # iterator field list and callback IL in Assembly-CSharp.il provide
        # the exact replacements below.
        replacements = (
            ("\t\tstring path2 = default(string);\n", ""),
            ("\t\t\tloadingInfo = _AssetLoads.Find((LoadingInfo info) => info.Path.Equals(path2));",
             "\t\t\tloadingInfo = _AssetLoads.Find((LoadingInfo info) => info.Path.Equals(path));"),
            ("\t\tint assetType2 = default(int);\n"
             "\t\tfloat estimatedSize2 = default(float);\n"
             "\t\tfloat estimatedInstantiatedSize2 = default(float);\n"
             "\t\tbool hold2 = default(bool);\n"
             "\t\tAssets.LoadAsync(path, delegate(UnityEngine.Object obj)\n"
             "\t\t{\n"
             "\t\t\tfuseTimer.Stop();\n"
             "\t\t\tif (obj == null)\n"
             "\t\t\t{\n"
             "\t\t\t\tDebug.LogError(\"[AssetManager.Load] ERROR - The requested object ({0}) does not exist with the specified path ({1}s)\", path2, Time.realtimeSinceStartup);\n"
             "\t\t\t}\n"
             "\t\t\telse\n"
             "\t\t\t{\n"
             "\t\t\t\tloadedObject = new LoadedObject(path2, obj, assetType2, priority, estimatedSize2, estimatedInstantiatedSize2);\n"
             "\t\t\t\tAddToAssetRegistry(loadedObject);\n"
             "\t\t\t}\n"
             "\t\t\tloadingInfo.OnLoaded(obj);\n"
             "\t\t\tif (hold2)\n"
             "\t\t\t{\n"
             "\t\t\t\tHold(path2, assetType2, hold: true);\n"
             "\t\t\t}\n"
             "\t\t\tif (_UnloadUnusedAssetsDelayed)\n"
             "\t\t\t{\n"
             "\t\t\t\tUnloadUnusedAssetsInternal();\n"
             "\t\t\t}\n"
             "\t\t\t_AssetLoads.Remove(loadingInfo);\n"
             "\t\t\t_CanAttemptToUnloadUnusedAssets = true;\n"
             "\t\t});\n",
             "\t\tAssets.LoadAsync(path, delegate(UnityEngine.Object obj)\n"
             "\t\t{\n"
             "\t\t\tfuseTimer.Stop();\n"
             "\t\t\tif (obj == null)\n"
             "\t\t\t{\n"
             "\t\t\t\tDebug.LogError(\"[AssetManager.Load] ERROR - The requested object ({0}) does not exist with the specified path ({1}s)\", path, Time.realtimeSinceStartup);\n"
             "\t\t\t}\n"
             "\t\t\telse\n"
             "\t\t\t{\n"
             "\t\t\t\tloadedObject = new LoadedObject(path, obj, assetType, priority, estimatedSize, estimatedInstantiatedSize);\n"
             "\t\t\t\tAddToAssetRegistry(loadedObject);\n"
             "\t\t\t}\n"
             "\t\t\tloadingInfo.OnLoaded(obj);\n"
             "\t\t\tif (hold)\n"
             "\t\t\t{\n"
             "\t\t\t\tHold(path, assetType, hold: true);\n"
             "\t\t\t}\n"
             "\t\t\tif (_UnloadUnusedAssetsDelayed)\n"
             "\t\t\t{\n"
             "\t\t\t\tUnloadUnusedAssetsInternal();\n"
             "\t\t\t}\n"
             "\t\t\t_AssetLoads.Remove(loadingInfo);\n"
             "\t\t\t_CanAttemptToUnloadUnusedAssets = true;\n"
             "\t\t});\n"),
        )
        stale_anchors = [old for old, _ in replacements if old in source]
        if stale_anchors:
            if len(stale_anchors) != len(replacements):
                raise ValueError(f"AssetManager.LoadInternal IL repair anchors partially changed: {path}")
            for old, new in replacements:
                if source.count(old) != 1:
                    raise ValueError(f"AssetManager.LoadInternal IL repair anchor changed: {path}: {old[:80]}")
                source = source.replace(old, new, 1)
            changes.append("restore EB.AssetManager.LoadInternal closure captures from original IL")
    if path.as_posix().endswith("EB.Sparx/ODRManager.cs"):
        # The original DownloadUnzipUrl iterator captures the method's
        # wadManifest and allowDownload parameters in its callback. ILSpy
        # emitted unrelated default locals instead; the null manifest makes
        # every successful ODR extraction fail while the false guard changes
        # the allowDownload=false diagnostic branch. Both defects stay in the
        # isolated replacement snapshot and are anchored to the exact
        # decompiler output observed in the Mono 2.0.2 assembly.
        old = ("\t\t\tbool allowDownload2 = default(bool);\n"
               "\t\t\tWADManifest wadManifest2 = default(WADManifest);\n")
        new = ""
        if source.count(old) == 1:
            source = source.replace(old, new, 1)
            changes.append("restore ODRManager.DownloadUnzipUrl callback captures from original IL")
        elif "bool allowDownload2 = default(bool);" in source or "WADManifest wadManifest2 = default(WADManifest);" in source:
            raise ValueError(f"ODRManager.DownloadUnzipUrl capture anchor changed: {path}")
        replacements = (
            ("if (!allowDownload2 && !success && string.IsNullOrEmpty(err))",
             "if (!allowDownload && !success && string.IsNullOrEmpty(err))"),
            ("wadManifest2.DownloadedFile = fileInfo.FullName;",
             "wadManifest.DownloadedFile = fileInfo.FullName;"),
            ("_wadFilenameHashMap[fileInfo.Name] = wadManifest2;",
             "_wadFilenameHashMap[fileInfo.Name] = wadManifest;")
        )
        changed = 0
        for old, new in replacements:
            if source.count(old) == 1:
                source = source.replace(old, new, 1)
                changed += 1
        if changed:
            if changed != len(replacements):
                raise ValueError(f"ODRManager.DownloadUnzipUrl callback anchor changed: {path}")
            if not any("restore ODRManager.DownloadUnzipUrl" in item for item in changes):
                changes.append("restore ODRManager.DownloadUnzipUrl callback captures from original IL")
        if allow_offline_network:
            # On the Android device, EB.DriveInfo cannot match the app's ODR
            # path to a System.IO.DriveInfo root and returns zero. The
            # original space guard then marks every WAD unavailable before
            # DownloadExtractor can issue its local-server request. Keep the
            # bypass limited to the opt-in offline replacement snapshot. The
            # Android bridge can return a nonzero but unusable value here, so
            # retaining the aggregate server-WAD check still raises the
            # client-fatal low-space dialog before a local WAD can start.
            old = "\t\tdecimal availableSpaceInBytes = DriveInfo.GetAvailableSpaceInBytes(odrExtractPath);\n"
            new = (old +
                   "\t\tDebug.LogWarning(\"ODRManager: bypassing aggregate WAD space guard for offline local WADs\");\n"
                   "\t\treturn;\n")
            marker = "unable to measure available space; continuing for offline local WADs"
            offline_marker = "bypassing aggregate WAD space guard for offline local WADs"
            if marker not in source and offline_marker not in source and source.count(old) == 1:
                source = source.replace(old, new, 1)
                changes.append("bypass aggregate ODR space guard for offline local WADs")
            elif marker not in source and offline_marker not in source:
                raise ValueError(f"ODRManager disk-space anchor changed: {path}")
    if path.as_posix().endswith("BattleArbiterInitState.cs"):
        # The original InitCharacter callback captures the method's id, data,
        # and towerData parameters. ILSpy emitted unrelated default locals,
        # which leaves PlayerController.Init with a null FighterData and
        # crashes the first fighter during offline FightFlow startup.
        old = ("\t\t\tint id2 = default(int);\n"
               "\t\t\tFighterData data2 = default(FighterData);\n"
               "\t\t\tTowerData towerData2 = default(TowerData);\n")
        new = ""
        if source.count(old) == 1:
            source = source.replace(old, new, 1)
            changes.append("restore BattleArbiter.InitCharacter callback captures from original IL")
        elif any(anchor in source for anchor in (
                "int id2 = default(int);",
                "FighterData data2 = default(FighterData);",
                "TowerData towerData2 = default(TowerData);")):
            raise ValueError(f"BattleArbiter.InitCharacter capture anchor changed: {path}")
        replacements = (
            ("InitPlayer(id2, character, isPlayer, mirror, data2, opponentData, towerData2);",
             "InitPlayer(id, character, isPlayer, mirror, data, opponentData, towerData);"),
        )
        for old, new in replacements:
            if source.count(old) == 1:
                source = source.replace(old, new, 1)
                if not any("restore BattleArbiter.InitCharacter" in item for item in changes):
                    changes.append("restore BattleArbiter.InitCharacter callback captures from original IL")
            elif old in source:
                raise ValueError(f"BattleArbiter.InitCharacter callback anchor changed: {path}")
    if allow_offline_network and path.as_posix().endswith("EB/DownloadExtractor.cs"):
        # DownloadExtractor repeats the same free-space policy after the
        # aggregate ODRManager check. On the offline device that second gate
        # prevents the local WAD request from starting, so make only this
        # opt-in replacement snapshot take the existing download branch.
        old = "\t\tif (totalSpaceRequired > diskSpace)\n"
        new = ("\t\tDebug.LogWarning(\"DownloadExtractor: bypassing per-WAD space guard for offline local WADs\");\n"
               "\t\tif (false)\n")
        marker = "bypassing per-WAD space guard for offline local WADs"
        if marker not in source and source.count(old) == 1:
            source = source.replace(old, new, 1)
            changes.append("bypass per-WAD DownloadExtractor space guard for offline local WADs")
        elif marker not in source:
            raise ValueError(f"DownloadExtractor disk-space anchor changed: {path}")
    if disable_push and path.as_posix().endswith("EB.Sparx/Hub.cs"):
        old = "if (Config.UsePush)"
        new = "if (false && Config.UsePush)"
        if old in source:
            source = source.replace(old, new, 1)
            changes.append("disable optional PushManager registration for the offline snapshot; "
                           "the revival server has no push websocket contract")
    if runtime_diagnostics:
        if path.as_posix().endswith("TransformersLoginListener.cs"):
            probes = [
                ('public override void OnLoggedIn()\n\t{\n\t\tbase.OnLoggedIn();',
                 'public override void OnLoggedIn()\n\t{\n\t\tbase.OnLoggedIn();\n'
                 '\t\tUnityEngine.Debug.Log("MONO login listener OnLoggedIn");'),
                ('Action onComplete = delegate\n\t\t{',
                 'Action onComplete = delegate\n\t\t{\n'
                 '\t\t\tUnityEngine.Debug.Log("MONO login flow callback");'),
                ('FlowManager.Instance.StartFlow(new HomeFlow(), delegate',
                 'UnityEngine.Debug.Log("MONO starting HomeFlow");\n\t\t\t\tFlowManager.Instance.StartFlow(new HomeFlow(), delegate'),
                ('Coroutines.Run(FlowManager.Instance, FlowManager.Instance.StartFTEFlow(new FTEFlow(), delegate',
                 'UnityEngine.Debug.Log("MONO starting FTEFlow");\n\t\t\t\tCoroutines.Run(FlowManager.Instance, FlowManager.Instance.StartFTEFlow(new FTEFlow(), delegate'),
            ]
            for old, new in probes:
                count = source.count(old)
                if count != (2 if "StartFTEFlow" in old else 1):
                    raise ValueError(f"Runtime diagnostic anchor changed: {path}: {old}")
                source = source.replace(old, new, count)
                changes.append("add bounded Mono login-flow diagnostic at " + old.splitlines()[0] +
                               (f" ({count} sites)" if count > 1 else ""))
        if path.as_posix().endswith("Quests.Presentation/GameboardManager.cs"):
            probes = [
                ('TFormAssetManager.Instance.LoadAndUse("assets_base/BaseRoot",',
                 'UnityEngine.Debug.Log("MONO request base asset");\n\t\t\tTFormAssetManager.Instance.LoadAndUse("assets_base/BaseRoot",'),
                ('private void OnBaseBoardLoaded(GameObject go)\n\t{\n\t\tStartCoroutine',
                 'private void OnBaseBoardLoaded(GameObject go)\n\t{\n'
                 '\t\tUnityEngine.Debug.Log("MONO base asset callback " + ((go == null) ? "null" : "object"));\n'
                 '\t\tStartCoroutine'),
            ]
            for old, new in probes:
                if source.count(old) != 1:
                    raise ValueError(f"Runtime diagnostic anchor changed: {path}: {old}")
                source = source.replace(old, new, 1)
                changes.append("add bounded Mono base-asset diagnostic at " + old.splitlines()[0])
        if path.as_posix().endswith("HomeFlow.cs"):
            old = 'private void OnBaseBoardLoaded(BaseBoard bb)\n\t{'
            new = old + '\n\t\tUnityEngine.Debug.Log("MONO HomeFlow base callback");'
            if source.count(old) != 1:
                raise ValueError(f"Runtime diagnostic anchor changed: {path}: {old}")
            source = source.replace(old, new, 1)
            changes.append("add bounded Mono HomeFlow base callback diagnostic")
        probes = {
            "EB.Sparx/Hub.cs": [
                ('case SubSystemState.Error:', 'case SubSystemState.Error:\n'
                 '\t\t\t\tUnityEngine.Debug.LogError("MONO subsystem error: " + subsystem.Name);'),
                ('public void FatalError(string error)\n\t{',
                 'public void FatalError(string error)\n\t{\n'
                 '\t\tUnityEngine.Debug.LogError("MONO fatal: " + error);'),
                ('State = HubState.Connected;', 'State = HubState.Connected;\n'
                 '\t\tUnityEngine.Debug.Log("MONO hub connected");'),
                ('if (Config.LoginConfig.Listener != null)\n\t\t{\n\t\t\tConfig.LoginConfig.Listener.OnLoggedIn();',
                 'if (Config.LoginConfig.Listener != null)\n\t\t{\n'
                 '\t\t\tUnityEngine.Debug.Log("MONO invoking login listener");\n'
                 '\t\t\tConfig.LoginConfig.Listener.OnLoggedIn();'),
            ],
            "EB.Sparx/PushManager.cs": [
                ('base.State = SubSystemState.Error;', 'base.State = SubSystemState.Error;\n'
                 '\t\t\tUnityEngine.Debug.LogError("MONO push token missing websocket");'),
            ],
        }
        for old, new in probes.get(path.as_posix(), []):
            if source.count(old) != 1:
                raise ValueError(f"Runtime diagnostic anchor changed: {path}: {old}")
            source = source.replace(old, new, 1)
            changes.append("add bounded Mono runtime diagnostic at " + old.splitlines()[0])
    if allow_offline_network and path.name == "TransformersLoginListener.cs":
        # The 2.0.2 client cannot load the later assets_base WAD, so its normal
        # login path blocks in HomeFlow before the first fight. The original
        # FTE fight already has a complete, local-compatible construction path
        # (Tuning starting bots + BCG base attributes + FightFlow). Route the
        # offline replacement directly through that path while preserving the
        # production FTE FightData construction and BattleArbiter startup.
        old = "if (TutorialManagerHelper.IsTutorialComplete(\"FTE\") || TutorialDB.SkipTutorials || TutorialDB.SkipFTE)"
        new = "if (OfflineFightBootstrap.Enabled || TutorialManagerHelper.IsTutorialComplete(\"FTE\") || TutorialDB.SkipTutorials || TutorialDB.SkipFTE)"
        if source.count(old) == 1 and "OfflineFightBootstrap.Enabled" not in source:
            source = source.replace(old, new, 1)
            old = "FlowManager.Instance.StartFlow(new HomeFlow(), delegate"
            new = "OfflineFightBootstrap.Start(delegate"
            if source.count(old) != 1:
                raise ValueError(f"Offline fight bootstrap HomeFlow anchor changed: {path}")
            source = source.replace(old, new, 1)
            helper = r'''

// Offline replacement-only bootstrap. This is deliberately kept beside the
// login listener so the audit can replace only managed game assemblies.
public static class OfflineFightBootstrap
{
	public static bool Enabled => true;

	public static void Start(EB.Action onComplete)
	{
		GameObject gameObject = new GameObject("OfflineFightBootstrap");
		Object.DontDestroyOnLoad(gameObject);
		Runner runner = gameObject.AddComponent<Runner>();
		runner.Begin(onComplete);
	}

	private sealed class Runner : MonoBehaviour
	{
		private EB.Action _onComplete;

		public void Begin(EB.Action onComplete)
		{
			_onComplete = onComplete;
			StartCoroutine(Boot());
		}

		private IEnumerator Boot()
		{
			while (Hub.Instance == null || Hub.Instance.State != HubState.Connected ||
				Tuning.Instance == null || BCGManager.Instance == null ||
				TuningAI.Instance == null)
			{
				yield return null;
			}
			string[] characterIds = new string[2]
			{
				Tuning.Instance.StartingPlayerBot,
				Tuning.Instance.StartingOpponentBot
			};
			System.Collections.Generic.List<BCGHeroRankLevel> ranks = new System.Collections.Generic.List<BCGHeroRankLevel>();
			BCGBlueprintBase[] blueprints = new BCGBlueprintBase[2];
			for (int i = 0; i < characterIds.Length; i++)
			{
				blueprints[i] = BCGHelper.GetBlueprint(characterIds[i]);
				if (blueprints[i] == null)
				{
					EB.Debug.LogError("OfflineFightBootstrap: missing starting blueprint " + characterIds[i]);
					Complete();
					yield break;
				}
				ranks.Add(new BCGHeroRankLevel(blueprints[i].Blueprint, 2, 1, 0));
			}
			WindowManager.Instance.ShowLoadingScreen(show: true, "Fight Load Offline");
			BCGManager.Instance.GetHeroBaseAttributes(ranks, OnAttributesReady);
		}

		private void OnAttributesReady(string error, System.Collections.Generic.List<BCGHeroDetails> details)
		{
			if (!string.IsNullOrEmpty(error) || details == null || details.Count < 2)
			{
				EB.Debug.LogError("OfflineFightBootstrap: base attributes unavailable: " + error);
				Complete();
				return;
			}
			string[] characterIds = new string[2]
			{
				Tuning.Instance.StartingPlayerBot,
				Tuning.Instance.StartingOpponentBot
			};
			FighterData[] fighters = new FighterData[2];
			for (int i = 0; i < fighters.Length; i++)
			{
				BCGBlueprintBase blueprint = BCGHelper.GetBlueprint(characterIds[i]);
				fighters[i] = new FighterData
				{
					Id = blueprint.Blueprint,
					Blueprint = blueprint,
					BaseAttributes = details[i].AttributeData,
					Attributes = details[i].AttributeData,
					SigLevel = details[i].SigLevel
				};
			}
			fighters[1].AIProfile = TuningAI.Instance.Profiles[0];
			if (TuningAI.Instance.Personalities.Length > 3)
			{
				fighters[1].OverrideAIPersonality = TuningAI.Instance.Personalities[3];
			}
			FightFlow.FightInitInfo initInfo = new FightFlow.FightInitInfo
			{
				playerFigher = fighters[0],
				enemyFigher = fighters[1],
				sceneName = Tuning.Instance.StartingArena,
				timeOfDay = Tuning.Instance.StartingArenaTOD,
				fightType = FightFlow.FIGHT_TYPE.FTE
			};
			EB.Debug.Log("OfflineFightBootstrap: starting FightFlow " + initInfo.sceneName);
			FlowManager.Instance.StartFlow(new FightFlow(initInfo), _onComplete);
			StartCoroutine(HideLoadingWhenFightStarts());
		}

		private IEnumerator HideLoadingWhenFightStarts()
		{
			while (BattleArbiter.Instance == null || BattleArbiter.Instance.IntroStage == null)
			{
				yield return null;
			}
			EB.Debug.Log("OfflineFightBootstrap: hiding loading screen after fight initialization");
			WindowManager.Instance.ShowLoadingScreen(show: false, "Fight Load Offline");
			Object.Destroy(base.gameObject);
		}

		private void Complete()
		{
			WindowManager.Instance.ShowLoadingScreen(show: false, "Fight Load Offline");
			_onComplete.SafeInvoke();
			Object.Destroy(base.gameObject);
		}
	}
}
'''
            source += helper
            changes.append("route offline login directly to the built-in FTE FightFlow")
        elif "OfflineFightBootstrap.Enabled" not in source:
            raise ValueError(f"Offline fight bootstrap repair already partially applied: {path}")
    if allow_offline_network and path.as_posix().endswith("EB/Download.cs"):
        # APK-backed bundles arrive from ZipDriver as jar:file:// URLs. The
        # original branch computes an entry offset, but then calls the
        # one-argument LoadFromFile overload on a synthetic "apk/path" string;
        # on the Android Unity runtime that does not load the bundle. Force
        # the existing Zip.Extract fallback for the offline replacement so the
        # subsequent file:// load receives a real filesystem path.
        old = "\t\t\tif (offset >= 0)\n"
        new = ("\t\t\t// Offline APK bundles must be extracted before Unity loads them.\n"
               "\t\t\tif (false && offset >= 0)\n")
        marker = "Offline APK bundles must be extracted before Unity loads them."
        if marker not in source and source.count(old) == 1:
            source = source.replace(old, new, 1)
            changes.append("extract offline APK asset bundles before Unity loading")
        elif marker not in source:
            raise ValueError(f"Download APK-bundle anchor changed: {path}")
    if disable_google_play_games and path.as_posix().endswith("EB.Sparx/Hub.cs"):
        old = "if (Config.UseGooglePlayGames && !flag)"
        new = "if (false && Config.UseGooglePlayGames && !flag)"
        if old in source:
            source = source.replace(old, new, 1)
            changes.append(
                "disable GooglePlayGamesManager registration in the isolated offline snapshot; "
                "device logs showed optional Play Games silent-auth attempts and certificate-mismatch DEVELOPER_ERROR")
    if path.as_posix() == "Setup.cs" and server_endpoint:
        old = 'ApiEndPoint = EB.Version.GetApiEndPoint("Default.Prod");'
        new = f'ApiEndPoint = {json.dumps(server_endpoint)};'
        if old in source:
            source = source.replace(old, new, 1)
            changes.append(f"route Setup.ApiEndPoint to configured revival endpoint {server_endpoint}")
    if path.as_posix().endswith("EB.Sparx/InventoryAPI.cs"):
        old = "Action<int, string, Hashtable> callback2 = default(Action<int, string, Hashtable>);"
        new = "Action<int, string, Hashtable> callback2 = callback;"
        count = source.count(old)
        if count:
            source = source.replace(old, new)
            changes.append(
                f"restore {count} InventoryAPI callback bindings from original IL iterator callback field")
    # Original firstpass IL calls MulticastDelegate.op_Inequality at these
    # three sites. Roslyn's concrete-delegate comparison instead binds to
    # Delegate.op_Inequality, absent from the APK's stripped mscorlib.
    # Cast only the observed operands; do not invent a runtime declaration.
    # Roslyn 8 lowers this explicit MulticastDelegate comparison to `ceq`
    # (verified in the emitted IL), which is runtime-profile safe even though
    # it does not reproduce the historical operator call byte-for-byte.
    delegate_sites = {
        "UITransition.cs": (("callback", "_callback", "Transition.Play"),
                            ("completionCallback", "_callback", "Transition.JumpToProgress")),
        "UIWidget.cs": (("mOnRender", "value", "UIWidget.set_onRender"),),
    }
    for left, right, method in delegate_sites.get(path.as_posix(), ()):
        old = f"{left} != {right}"
        new = f"(System.MulticastDelegate){left} != (System.MulticastDelegate){right}"
        if source.count(old) == 1:
            source = source.replace(old, new, 1)
            changes.append(
                f"{method}: cast inequality operands to System.MulticastDelegate; "
                "original IL calls MulticastDelegate.op_Inequality(MulticastDelegate, MulticastDelegate); "
                "unadapted Roslyn calls Delegate.op_Inequality(Delegate, Delegate); "
                "adapted Roslyn emits ceq; original mscorlib declares only the former operator")
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
        # The original CheckFileCrc iterator captures its fileDescriptor
        # parameter in the ComputeCrc callback. ILSpy emitted an unrelated
        # default local instead, so cached WAD validation dereferences a null
        # descriptor before the ODR download can begin.
        old = "\t\t\tFileDescriptor fileDescriptor2 = default(FileDescriptor);\n"
        new = ""
        if source.count(old) == 1:
            source = source.replace(old, new, 1)
            changes.append("restore DownloadExtractor.CheckFileCrc descriptor capture from original IL")
        elif "FileDescriptor fileDescriptor2 = default(FileDescriptor);" in source:
            raise ValueError(f"DownloadExtractor.CheckFileCrc capture anchor changed: {path}")
        replacements = (
            ("if (fileDescriptor2.Crc == existingFileCrc)",
             "if (fileDescriptor.Crc == existingFileCrc)"),
            ("SendCrcMismatchTelemetry(fileInfo.Name, existingFileCrc, fileDescriptor2.Crc);",
             "SendCrcMismatchTelemetry(fileInfo.Name, existingFileCrc, fileDescriptor.Crc);")
        )
        changed = 0
        for old, new in replacements:
            if source.count(old) == 1:
                source = source.replace(old, new, 1)
                changed += 1
        if changed:
            if changed != len(replacements):
                raise ValueError(f"DownloadExtractor.CheckFileCrc callback anchor changed: {path}")
            if not any("restore DownloadExtractor.CheckFileCrc" in item for item in changes):
                changes.append("restore DownloadExtractor.CheckFileCrc descriptor capture from original IL")
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
                  repair_accessors=False, repair_contracts=False, server_endpoint=None,
                  disable_google_play_games=False, runtime_diagnostics=False,
                  disable_push=False, allow_offline_network=False):
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
    if server_endpoint:
        if not re.match(r"^https?://[^\s\"']+$", server_endpoint):
            raise ValueError("server endpoint must be an absolute http:// or https:// URL")
        report["server_endpoint"] = server_endpoint
    if disable_google_play_games:
        report["disable_google_play_games"] = True
    if runtime_diagnostics:
        if not repair_contracts:
            raise ValueError("runtime diagnostics require --repair-contracts")
        report["runtime_diagnostics"] = True
    if disable_push:
        if not repair_contracts:
            raise ValueError("disabling push requires --repair-contracts")
        report["disable_push"] = True
    if allow_offline_network:
        if not repair_contracts:
            raise ValueError("allowing offline network requires --repair-contracts")
        report["allow_offline_network"] = True
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
                text, contract_changes = normalize_source_contracts(
                    relative, text, server_endpoint, disable_google_play_games, runtime_diagnostics,
                    disable_push, allow_offline_network)
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
    compare = commands.add_parser("metadata-compare",
                                  help="Compare isolated compiled metadata with original PE metadata")
    compare.add_argument("workspace", type=Path)
    compare.add_argument("--dotnet", default="dotnet")
    compare.add_argument("--metadata-tool", required=True, type=Path,
                         help="Built MonoMetadata .NET tool assembly")
    compare.add_argument("--compiled-dir", required=True, type=Path,
                         help="Directory containing isolated compiled DLLs")
    compare.add_argument("--assembly", action="append", help="Assembly name without .dll (repeatable)")
    plan = commands.add_parser("replacement-plan", help="Report the managed dependency closure for replacement roots")
    plan.add_argument("workspace", type=Path)
    plan.add_argument("--dotnet", default="dotnet")
    plan.add_argument("--metadata-tool", required=True, type=Path,
                      help="Built MonoMetadata .NET tool assembly")
    plan.add_argument("--root", action="append", required=True,
                      help="Managed replacement root without .dll (repeatable)")
    plan.add_argument("--compile-report", type=Path,
                      help="Pin roots to isolated audit outputs and assess them with preserved originals")
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
    audit.add_argument("--server-endpoint",
                       help="Route Setup.ApiEndPoint to this revival HTTP(S) endpoint in the isolated snapshot")
    audit.add_argument("--disable-google-play-games", action="store_true",
                       help="Disable GooglePlayGamesManager registration in the isolated offline snapshot")
    audit.add_argument("--runtime-diagnostics", action="store_true",
                       help="Log Mono subsystem/fatal transitions in the isolated snapshot")
    audit.add_argument("--disable-push", action="store_true",
                       help="Disable optional PushManager registration in the isolated offline snapshot")
    audit.add_argument("--allow-offline-network", action="store_true",
                       help="Allow local revival-server requests when Unity reports no Internet reachability")
    args = parser.parse_args()
    try:
        if args.command == "export":
            return recover(args.apk.resolve(), args.ilspy)
        if args.command == "export-il":
            return export_il(args.workspace.resolve(), args.ilspy)
        if args.command == "metadata-audit":
            return metadata_audit(args.workspace.resolve(), args.dotnet,
                                  args.metadata_tool.resolve(), args.assembly)
        if args.command == "metadata-compare":
            return metadata_compare(args.workspace.resolve(), args.dotnet,
                                    args.metadata_tool.resolve(), args.compiled_dir,
                                    args.assembly)
        if args.command == "replacement-plan":
            return replacement_plan(args.workspace.resolve(), args.dotnet,
                                    args.metadata_tool.resolve(), args.root, args.compile_report)
        return compile_audit(args.workspace.resolve(), args.dotnet, args.csc.resolve(),
                             args.reference_dir, args.assembly, args.repair_accessors,
                             args.repair_contracts, args.server_endpoint,
                             args.disable_google_play_games, args.runtime_diagnostics,
                             args.disable_push, args.allow_offline_network)
    except (OSError, ValueError, zipfile.BadZipFile, subprocess.CalledProcessError) as error:
        parser.exit(1, f"Error: {error}\n")


if __name__ == "__main__":
    raise SystemExit(main())
