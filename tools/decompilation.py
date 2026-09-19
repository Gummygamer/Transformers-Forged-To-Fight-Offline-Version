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


def compile_audit(workspace, dotnet, csc):
    """Compile recovered game code against the APK's own framework, without NuGet."""
    manifest = json.loads((workspace / "manifest.json").read_text())
    # Verify input provenance before using assemblies as compiler references.
    for row in manifest["assemblies"]:
        if digest(workspace / "managed" / row["name"]) != row["sha256"]:
            raise ValueError(f"Assembly changed since export: {row['name']}")
    audit = Path(tempfile.mkdtemp(prefix="compile-", dir=workspace))
    report = {"scope": "game assemblies against original APK references; not a Unity build",
              "compiler": str(csc), "compiler_sha256": digest(csc), "assemblies": []}
    for name in ("Assembly-CSharp-firstpass", "Assembly-CSharp"):
        sources = sorted((workspace / "source" / name).rglob("*.cs"))
        if not sources:
            report["assemblies"].append({"name": name, "status": "missing-source"})
            continue
        flags = ["-nologo", "-target:library", "-unsafe", "-nostdlib+", "-langversion:12",
                 f'-out:"{audit / (name + ".dll")}"']
        flags += [f'-reference:"{p}"' for p in sorted((workspace / "managed").glob("*.dll"))
                  if p.stem != name]
        flags += [f'"{p}"' for p in sources]
        rsp = audit / f"{name}.rsp"
        rsp.write_text("\n".join(flags) + "\n")
        log = audit / f"{name}.log"
        with log.open("w") as output:
            result = subprocess.run([dotnet, str(csc), f"@{rsp}"], stdout=output,
                                    stderr=subprocess.STDOUT)
        errors = Counter(re.findall(r"\berror (CS\d+):", log.read_text()))
        report["assemblies"].append({"name": name, "exit_code": result.returncode,
                                     "status": "compiled" if result.returncode == 0 else "failed",
                                     "errors": dict(sorted(errors.items()))})
    save(audit / "report.json", report)
    success = all(r["status"] == "compiled" for r in report["assemblies"])
    manifest["compilation"] = "game-assemblies-compiled" if success else "failed"
    manifest["latest_compile_report"] = (audit / "report.json").relative_to(workspace).as_posix()
    save(workspace / "manifest.json", manifest)
    print(json.dumps(report, indent=2))
    print(f"Report: {audit / 'report.json'}")
    return 0 if success else 1


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    export = commands.add_parser("export", help="Recover every managed DLL into an isolated workspace")
    export.add_argument("apk", type=Path)
    export.add_argument("--ilspy", default=os.environ.get("ILSPYCMD", "ilspycmd"))
    audit = commands.add_parser("compile-audit", help="Measure game-source compiler blockers")
    audit.add_argument("workspace", type=Path)
    audit.add_argument("--dotnet", default="dotnet")
    audit.add_argument("--csc", required=True, type=Path, help="SDK Roslyn/bincore/csc.dll")
    args = parser.parse_args()
    try:
        if args.command == "export":
            return recover(args.apk.resolve(), args.ilspy)
        return compile_audit(args.workspace.resolve(), args.dotnet, args.csc.resolve())
    except (OSError, ValueError, zipfile.BadZipFile, subprocess.CalledProcessError) as error:
        parser.exit(1, f"Error: {error}\n")


if __name__ == "__main__":
    raise SystemExit(main())
