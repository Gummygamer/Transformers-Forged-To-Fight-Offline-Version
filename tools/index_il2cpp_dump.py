#!/usr/bin/env python3
"""Build a local, machine-readable index from an Il2CppDumper dump.

The input and output are deliberately operator-local.  The repository carries
this parser, not a dump from a game binary.  The index is intended to generate
or validate hook/patch metadata by managed names instead of hand-copied RVAs.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import re
from pathlib import Path
from typing import Any


TYPE_RE = re.compile(
    r"^\s*(?:(?:public|private|protected|internal|sealed|abstract|static|partial)\s+)+"
    r"(?P<kind>class|struct|enum|interface)\s+(?P<name>[^\s:{]+)"
)
RVA_RE = re.compile(r"//\s*RVA:\s*0x(?P<rva>[0-9A-Fa-f]+)")
FIELD_RE = re.compile(
    r"^\s*(?P<mods>(?:(?:public|private|protected|internal|static|readonly|const|"
    r"volatile|sealed|new|unsafe)\s+)+)(?P<type>.+?)\s+"
    r"(?P<name>[A-Za-z_<>][\w<>]*)\s*;\s*//\s*0x(?P<offset>[0-9A-Fa-f]+)"
)
METHOD_RE = re.compile(
    r"^\s*(?:(?:public|private|protected|internal|static|virtual|abstract|final|"
    r"extern|new|sealed|unsafe|override|async|partial)\s+)+.+\([^;]*\)"
)
NAMESPACE_RE = re.compile(r"^\s*//\s*Namespace:\s*(?P<namespace>.*)\s*$")


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as source:
        for block in iter(lambda: source.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def method_name(declaration: str) -> str:
    before_args = declaration.split("(", 1)[0].rstrip()
    return re.search(r"([A-Za-z_.$<>][\w.$<>]*)$", before_args).group(1)


def script_methods(path: Path) -> list[dict[str, Any]]:
    root = json.loads(path.read_text(encoding="utf-8"))
    entries = root.get("ScriptMethod", [])
    result = []
    for entry in entries:
        if not isinstance(entry, dict):
            continue
        address = entry.get("Address")
        name = entry.get("Name", "")
        if not isinstance(address, int) or not isinstance(name, str):
            continue
        result.append({
            "name": name,
            "address": address,
            "address_hex": f"0x{address:x}",
            "signature": entry.get("Signature", ""),
        })
    return result


def parse_dump(path: Path) -> dict[str, Any]:
    types: list[dict[str, Any]] = []
    type_stack: list[tuple[int, int]] = []
    brace_depth = 0
    namespace = ""
    pending_rva: int | None = None

    for raw_line in path.read_text(encoding="utf-8", errors="replace").splitlines():
        while type_stack and brace_depth < type_stack[-1][0]:
            type_stack.pop()
        namespace_match = NAMESPACE_RE.match(raw_line)
        if namespace_match:
            namespace = namespace_match.group("namespace")

        type_match = TYPE_RE.match(raw_line)
        if type_match:
            # A previous type closed at this same depth. Nested declarations are
            # deeper than their parent and therefore leave the parent on stack.
            while type_stack and type_stack[-1][0] == brace_depth:
                type_stack.pop()
            name = type_match.group("name")
            full_name = f"{namespace}.{name}" if namespace and "." not in name else name
            record = {
                "name": full_name,
                "namespace": namespace,
                "kind": type_match.group("kind"),
                "fields": [],
                "methods": [],
            }
            types.append(record)
            # Declarations in dump.cs put the opening brace on this or the next line.
            body_depth = brace_depth + 1 if "{" in raw_line else brace_depth
            type_stack.append((body_depth, len(types) - 1))
            pending_rva = None
        elif type_stack:
            current = types[type_stack[-1][1]]
            rva_match = RVA_RE.search(raw_line)
            if rva_match:
                pending_rva = int(rva_match.group("rva"), 16)
            field_match = FIELD_RE.match(raw_line)
            if field_match:
                current["fields"].append({
                    "name": field_match.group("name"),
                    "type": field_match.group("type").strip(),
                    "modifiers": field_match.group("mods").split(),
                    "offset": int(field_match.group("offset"), 16),
                    "offset_hex": f"0x{int(field_match.group('offset'), 16):x}",
                })
            elif pending_rva is not None and METHOD_RE.match(raw_line):
                declaration = raw_line.strip()
                current["methods"].append({
                    "name": method_name(declaration),
                    "declaration": declaration,
                    "rva": pending_rva,
                    "rva_hex": f"0x{pending_rva:x}",
                })
                pending_rva = None
        brace_depth += raw_line.count("{") - raw_line.count("}")

    methods = [
        {**method, "declaring_type": record["name"]}
        for record in types
        for method in record["methods"]
    ]
    fields = [
        {**field, "declaring_type": record["name"]}
        for record in types
        for field in record["fields"]
    ]
    return {"types": types, "methods": methods, "fields": fields}


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("dump", type=Path, help="local Il2CppDumper dump.cs")
    parser.add_argument("--script-json", type=Path, help="optional local script.json")
    parser.add_argument("--out", type=Path, required=True, help="local JSON index path")
    args = parser.parse_args()
    if not args.dump.is_file():
        parser.error(f"dump not found: {args.dump}")
    if args.script_json and not args.script_json.is_file():
        parser.error(f"script.json not found: {args.script_json}")
    parsed = parse_dump(args.dump)
    output: dict[str, Any] = {
        "format": 1,
        "source": {"dump": str(args.dump), "dump_sha256": sha256(args.dump)},
        **parsed,
    }
    if args.script_json:
        output["source"]["script_json"] = str(args.script_json)
        output["source"]["script_json_sha256"] = sha256(args.script_json)
        output["script_methods"] = script_methods(args.script_json)
    args.out.parent.mkdir(parents=True, exist_ok=True)
    args.out.write_text(json.dumps(output, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    print(f"indexed {len(parsed['types'])} types, {len(parsed['methods'])} methods, "
          f"{len(parsed['fields'])} fields -> {args.out}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
