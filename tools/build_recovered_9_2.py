#!/usr/bin/env python3
"""Build a local staged 9.2 Unity recovery project as an ARM64 Android APK."""

from __future__ import annotations

import argparse
import gzip
import hashlib
import json
import os
import re
import shutil
import struct
import subprocess
import sys
import time
from pathlib import Path

from recovery_environment import check_external_build_storage


def is_within(path: Path, parent: Path) -> bool:
    try:
        path.relative_to(parent)
        return True
    except ValueError:
        return False


def mark_managed_plugins_as_dll(project: Path) -> int:
    """Restore the PE DLL characteristic on managed plugin images in the staged project."""
    plugin_dir = project / "Assets/Plugins"
    patched = 0
    for path in plugin_dir.glob("*.dll"):
        with path.open("r+b") as image:
            dos_header = image.read(64)
            if len(dos_header) < 64 or dos_header[:2] != b"MZ":
                raise ValueError(f"invalid PE image: {path}")

            pe_offset = struct.unpack_from("<I", dos_header, 0x3C)[0]
            image.seek(pe_offset)
            if image.read(4) != b"PE\0\0":
                raise ValueError(f"invalid PE signature: {path}")

            image.seek(pe_offset + 20)
            optional_header_size = struct.unpack("<H", image.read(2))[0]
            characteristics_offset = pe_offset + 22
            characteristics = struct.unpack("<H", image.read(2))[0]
            optional_header = image.read(optional_header_size)
            if len(optional_header) != optional_header_size:
                raise ValueError(f"truncated PE optional header: {path}")

            magic = struct.unpack_from("<H", optional_header)[0]
            directories_offset = {0x10B: 96, 0x20B: 112}.get(magic)
            if directories_offset is None:
                continue
            clr_directory_offset = directories_offset + 14 * 8
            if len(optional_header) < clr_directory_offset + 8:
                continue
            clr_rva, clr_size = struct.unpack_from("<II", optional_header, clr_directory_offset)
            if not clr_rva or not clr_size or characteristics & 0x2000:
                continue

            image.seek(characteristics_offset)
            image.write(struct.pack("<H", characteristics | 0x2000))
            patched += 1
    return patched


def managed_build_fingerprint(project: Path, build_configuration: dict[str, object]) -> str:
    """Fingerprint IL2CPP inputs so failed, unchanged compiler runs can be skipped."""
    digest = hashlib.sha256()
    inputs = [
        project / "ProjectSettings/ProjectVersion.txt",
        project / "Packages/manifest.json",
        project / "Assets/Editor/RebuildRecoveredAndroid.cs",
        Path(__file__).resolve().with_name("RepairRecoveredConstructor.cs"),
        Path(__file__).resolve().with_name("repair_recovered_9_2_import.py"),
    ]
    inputs.extend(sorted(Path(__file__).resolve().parent.joinpath("recovery_sources").glob("*.cs")))
    inputs.extend(sorted((project / "Assets/Plugins").glob("*.dll")))
    digest.update(json.dumps(build_configuration, sort_keys=True).encode("utf-8"))
    for path in inputs:
        if not path.is_file():
            continue
        digest.update(path.relative_to(project).as_posix().encode("utf-8")
                      if is_within(path, project) else path.name.encode("utf-8"))
        with path.open("rb") as source:
            for block in iter(lambda: source.read(1024 * 1024), b""):
                digest.update(block)
    return digest.hexdigest()


def remove_empty_missing_script_components(project: Path) -> tuple[int, int]:
    """Drop only empty MonoBehaviour placeholders with a null script reference."""
    assets = project / "Assets"
    extensions = {".prefab", ".unity", ".asset"}
    known_fields = {
        "m_ObjectHideFlags", "m_CorrespondingSourceObject", "m_PrefabInstance",
        "m_PrefabAsset", "m_GameObject", "m_Enabled", "m_EditorHideFlags",
        "m_Script", "m_Name", "m_EditorClassIdentifier",
    }
    document_start = re.compile(r"(?m)(?=^--- !u!)")
    header = re.compile(r"^--- !u!114 &(-?\d+)\s*$")
    removed = 0
    changed_files = 0

    for path in assets.rglob("*"):
        if path.suffix not in extensions or not path.is_file():
            continue
        try:
            contents = path.read_text(encoding="utf-8")
        except (OSError, UnicodeDecodeError):
            continue

        documents = document_start.split(contents)
        candidates: list[tuple[int, str]] = []
        for index, document in enumerate(documents):
            first_line = document.splitlines()[0] if document.splitlines() else ""
            match = header.match(first_line)
            if not match or "MonoBehaviour:" not in document or "m_Script: {fileID: 0}" not in document:
                continue
            field_names = set(re.findall(r"(?m)^  ([A-Za-z_][A-Za-z0-9_]*):", document))
            if field_names - known_fields:
                continue
            candidates.append((index, match.group(1)))

        if not candidates:
            continue

        removable_ids = {file_id for _, file_id in candidates}
        # A null-script component contains only Unity's base MonoBehaviour
        # fields. Require its GameObject's component-list reference too, so
        # malformed or data-bearing objects are left intact for recovery.
        for file_id in tuple(removable_ids):
            reference = re.compile(
                rf"(?m)^\s*- component: \{{fileID: {re.escape(file_id)}\}}\s*$"
            )
            if not reference.search(contents):
                removable_ids.remove(file_id)
        if not removable_ids:
            continue

        removable_indexes = {index for index, file_id in candidates if file_id in removable_ids}
        kept_documents = [document for index, document in enumerate(documents) if index not in removable_indexes]
        updated = "".join(kept_documents)
        for file_id in removable_ids:
            reference = re.compile(
                rf"(?m)^\s*- component: \{{fileID: {re.escape(file_id)}\}}\s*\n?"
            )
            updated, count = reference.subn("", updated)
            if count != 1:
                raise ValueError(f"expected one GameObject reference for missing script {file_id} in {path}")
        if updated != contents:
            path.write_text(updated, encoding="utf-8")
            removed += len(removable_ids)
            changed_files += 1

    return removed, changed_files


def ensure_unity_ui_package(project: Path) -> bool:
    """Add Unity's built-in UGUI package, required by recovered Unity UI references."""
    manifest = project / "Packages/manifest.json"
    if not manifest.is_file():
        raise ValueError(f"Unity package manifest is missing: {manifest}")
    data = json.loads(manifest.read_text())
    dependencies = data.setdefault("dependencies", {})
    if dependencies.get("com.unity.ugui") == "1.0.0":
        return False
    dependencies["com.unity.ugui"] = "1.0.0"
    manifest.write_text(json.dumps(data, indent=2) + "\n")
    return True


def legacy_runtime_is_compatible(library_dir: Path, inherited_library_path: str) -> tuple[bool, str]:
    """Check whether Unity's optional bundled native libraries fit this host runtime."""
    libxml = library_dir / "libxml2.so.2"
    if not libxml.is_file() or shutil.which("ldd") is None:
        return False, "libxml2 or ldd is unavailable"

    probe_env = os.environ.copy()
    probe_env["LD_LIBRARY_PATH"] = os.pathsep.join(
        path for path in (str(library_dir), inherited_library_path) if path
    )
    probe = subprocess.run(
        ["ldd", "-v", str(libxml)], env=probe_env, capture_output=True, text=True, check=False
    )
    diagnostics = (probe.stdout or "") + (probe.stderr or "")
    if probe.returncode != 0 or re.search(r"\bnot found\b", diagnostics, re.IGNORECASE):
        detail = next((line.strip() for line in diagnostics.splitlines()
                       if "not found" in line.lower()), "dependency inspection failed")
        return False, detail
    return True, ""


def disable_empty_plugin_shadow_asmdefs(project: Path) -> int:
    """Keep empty AssetRipper asmdef placeholders from shadowing shipped plugin DLLs."""
    plugins = project / "Assets/Plugins"
    scripts = project / "Assets/Scripts"
    if not plugins.is_dir() or not scripts.is_dir():
        return 0
    plugin_names = {path.stem for path in plugins.glob("*.dll")}
    # UnityEngine.UI comes from the editor's built-in com.unity.ugui package,
    # not from game-authored scripts; AssetRipper leaves an empty asmdef stub.
    plugin_names.add("UnityEngine.UI")
    disabled = 0
    for definition in scripts.rglob("*.asmdef"):
        try:
            name = json.loads(definition.read_text()).get("name", definition.stem)
        except (OSError, json.JSONDecodeError):
            continue
        if name not in plugin_names:
            continue
        if any(definition.parent.rglob("*.cs")):
            continue
        disabled_path = definition.with_name(definition.name + ".recovery-disabled")
        if disabled_path.exists():
            continue
        definition.rename(disabled_path)
        meta = definition.with_name(definition.name + ".meta")
        if meta.exists():
            meta.rename(disabled_path.with_name(disabled_path.name + ".meta"))
        disabled += 1
    return disabled


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("project", type=Path, help="writable staged Unity project directory")
    parser.add_argument("--unity", type=Path, required=True, help="Unity 2020.3.31f1 Editor executable")
    parser.add_argument("--output-apk", type=Path, required=True, help="local output APK path")
    parser.add_argument("--log", type=Path, help="Unity log path (defaults beside the APK)")
    parser.add_argument(
        "--compress-log", action="store_true",
        help="gzip the Unity log after the repair queue is summarized (keeps diagnostic history compact)",
    )
    parser.add_argument(
        "--repair-diagnostics", type=Path,
        help="prior Unity log used to repair diagnosed malformed import callbacks",
    )
    parser.add_argument(
        "--backend", choices=("IL2CPP", "Mono"), default="IL2CPP",
        help="Android scripting backend (default: IL2CPP)",
    )
    parser.add_argument(
        "--architecture", choices=("ARM64", "ARMv7"), default="ARM64",
        help="Android CPU architecture (default: ARM64)",
    )
    parser.add_argument(
        "--graphics", action="store_true",
        help="run Unity with the host display/GPU instead of -nographics",
    )
    parser.add_argument(
        "--allow-upgraded-project", action="store_true",
        help="allow a recovered 2020.3.31f1 project previously upgraded by a newer Unity Editor",
    )
    parser.add_argument(
        "--repeat-unchanged", action="store_true",
        help="run Unity again even when the same managed inputs already produced an IL2CPP failure",
    )
    parser.add_argument(
        "--legacy-runtime", choices=("auto", "always", "never"), default="auto",
        help="use Unity's optional legacy native-library directory (default: only when host-compatible)",
    )
    args = parser.parse_args()

    project = args.project.expanduser().resolve()
    unity = args.unity.expanduser().resolve()
    output = args.output_apk.expanduser().resolve()
    log = args.log.expanduser().resolve() if args.log else output.with_suffix(".unity.log")
    if not (project / "ProjectSettings/ProjectVersion.txt").is_file():
        parser.error(f"not a Unity project: {project}")
    version = (project / "ProjectSettings/ProjectVersion.txt").read_text()
    if "2020.3.31f1" not in version and not args.allow_upgraded_project:
        parser.error(f"project was not exported for Unity 2020.3.31f1: {version.strip()}")
    for assembly in ("Assembly-CSharp.dll", "Assembly-CSharp-firstpass.dll"):
        if not (project / "Assets/Plugins" / assembly).is_file():
            parser.error(f"staged Cpp2IL assembly missing from Assets/Plugins: {assembly}")
    if not unity.is_file():
        parser.error(f"Unity Editor executable does not exist: {unity}")
    if is_within(output, project):
        parser.error("write the APK outside the Unity project directory")
    if output.exists():
        parser.error(f"refusing to overwrite existing APK: {output}")

    try:
        check_external_build_storage(Path(__file__).resolve().parent.parent)
    except RuntimeError as error:
        parser.error(str(error))

    cycle_started = time.monotonic()
    cycle_storage_before = shutil.disk_usage(project)
    try:
        scriptable_converter = Path(__file__).resolve().with_name(
            "convert_9_2_scriptable_prefabs.py"
        )
        scriptable_backup = project.parent / "recovered-9.2-scriptable-prefab-backup"
        subprocess.run(
            [sys.executable, str(scriptable_converter), str(project / "Assets"), str(scriptable_backup)],
            check=True,
        )
        removed_components, changed_assets = remove_empty_missing_script_components(project)
        if removed_components:
            print(
                f"Removed {removed_components} empty null-script component(s) "
                f"from {changed_assets} staged asset file(s)"
            )
        if ensure_unity_ui_package(project):
            print("Added Unity built-in package com.unity.ugui@1.0.0 to the staged project")
        disabled_asmdefs = disable_empty_plugin_shadow_asmdefs(project)
        if disabled_asmdefs:
            print(f"Disabled {disabled_asmdefs} empty asmdef placeholder(s) shadowing shipped plugin assemblies")
    except (OSError, ValueError, json.JSONDecodeError) as error:
        parser.error(f"could not configure recovered Unity UI package: {error}")

    keystore = output.with_suffix(".keystore")
    keytool = unity.parent / "Data/PlaybackEngines/AndroidPlayer/OpenJDK/bin/keytool"
    if not keytool.is_file():
        parser.error(f"Unity's bundled keytool does not exist: {keytool}")
    repair = Path(__file__).resolve().with_name("repair_recovered_9_2_import.py")
    repair_command = [sys.executable, str(repair), str(project), "--unity", str(unity)]
    if args.repair_diagnostics:
        repair_command.extend(("--diagnostics", str(args.repair_diagnostics.expanduser().resolve())))
    prepare_log = output.with_suffix(".prepare.log")
    repair_result = subprocess.run(repair_command, capture_output=True, text=True, check=False)
    prepare_output = repair_result.stdout + repair_result.stderr
    prepare_log.write_text(prepare_output)
    if repair_result.returncode:
        print(
            f"Recovery preflight failed with status {repair_result.returncode}; "
            f"last repair output is in {prepare_log}:",
            file=sys.stderr,
        )
        print("\n".join(prepare_output.splitlines()[-60:]), file=sys.stderr)
        return repair_result.returncode
    repair_actions = sum(
        line.startswith(("reconstructed ", "repaired ", "restored ", "sanitized "))
        for line in prepare_output.splitlines()
    )
    print(
        f"Recovery preflight: {repair_actions} repair actions in "
        f"{len(prepare_output.splitlines())} output lines; transcript {prepare_log}"
    )
    try:
        marked_plugins = mark_managed_plugins_as_dll(project)
    except (OSError, ValueError, struct.error) as error:
        parser.error(f"could not normalize staged managed plugin headers: {error}")
    if marked_plugins:
        print(f"Marked {marked_plugins} staged managed plugin(s) as DLL images")

    helper = Path(__file__).resolve().parent / "unity/RebuildRecoveredAndroid.cs"
    editor_dir = project / "Assets/Editor"
    editor_dir.mkdir(parents=True, exist_ok=True)
    shutil.copyfile(helper, editor_dir / helper.name)
    output.parent.mkdir(parents=True, exist_ok=True)
    log.parent.mkdir(parents=True, exist_ok=True)
    build_configuration = {
        "unity": str(unity),
        "backend": args.backend,
        "architecture": args.architecture,
        "graphics": args.graphics,
    }
    input_fingerprint = managed_build_fingerprint(project, build_configuration)
    failed_input_record = output.parent / f".{project.name}.last-failed-il2cpp-inputs.json"
    if failed_input_record.is_file() and not args.repeat_unchanged:
        try:
            previous_failure = json.loads(failed_input_record.read_text())
        except (OSError, json.JSONDecodeError):
            previous_failure = {}
        if (previous_failure.get("fingerprint") == input_fingerprint and
                int(previous_failure.get("repair_queue_items", 0)) > 0):
            skipped_seconds = time.monotonic() - cycle_started
            skipped_storage_after = shutil.disk_usage(project)
            free_delta = skipped_storage_after.free - cycle_storage_before.free
            skip_report = Path(str(log) + ".skip.cycle.json")
            skip_report.write_text(json.dumps({
                "unity": str(unity),
                "project": str(project),
                "backend": args.backend,
                "architecture": args.architecture,
                "preflight_seconds": round(skipped_seconds, 2),
                "unity_seconds": 0,
                "skipped_unchanged_inputs": True,
                "previous_repair_queue_items": int(previous_failure["repair_queue_items"]),
                "external_free_before_bytes": cycle_storage_before.free,
                "external_free_after_bytes": skipped_storage_after.free,
                "net_free_space_delta_bytes": free_delta,
            }, indent=2) + "\n")
            print(
                "Unity build skipped: these managed inputs already failed IL2CPP with "
                f"{previous_failure['repair_queue_items']} unique queue item(s). "
                "Change the managed inputs or pass --repeat-unchanged to probe intermittency."
            )
            print(f"Previous failure log: {previous_failure.get('log', 'unknown')}")
            print(
                f"Skip cost: preflight {skipped_seconds:.1f} s; Unity 0 s; "
                f"external free-space delta {free_delta / 1024**2:+.2f} MiB"
            )
            print(f"Cycle metadata: {skip_report}")
            return 1
    password = "local-rebuild-only"
    if keystore.exists():
        keycheck = subprocess.run(
            [str(keytool), "-list", "-keystore", str(keystore), "-storepass", password,
             "-alias", "local-rebuild"],
            capture_output=True,
            text=True,
            check=False,
        )
        if keycheck.returncode:
            parser.error(f"existing local keystore is not usable: {keystore}")
    else:
        keygen = subprocess.run(
            [
                str(keytool), "-genkeypair", "-noprompt", "-keystore", str(keystore),
                "-storepass", password, "-keypass", password, "-alias", "local-rebuild",
                "-keyalg", "RSA", "-keysize", "2048", "-validity", "10000",
                "-dname", "CN=Local Recovery Build, OU=Local, O=Local, L=Local, ST=Local, C=US",
            ],
            capture_output=True,
            text=True,
            check=False,
        )
        if keygen.returncode:
            keystore.unlink(missing_ok=True)
            print(f"Could not create a local signing key: {keygen.stderr.strip()}", file=sys.stderr)
            return keygen.returncode

    # Unity stores external absolute keystore paths relative to its configured
    # dedicated-keystore directory. That path is host-specific and may not
    # exist inside a container, so stage the local key inside this disposable
    # recovered project for the duration of the build.
    project_keystore = project / ".recovered-local.keystore"
    shutil.copyfile(keystore, project_keystore)

    env = os.environ.copy()
    legacy_runtime_libraries = next((
        parent / "legacy-runtime/usr/lib/x86_64-linux-gnu"
        for parent in unity.parents
        if (parent / "legacy-runtime/usr/lib/x86_64-linux-gnu/libxml2.so.2").is_file()
    ), None)
    if legacy_runtime_libraries is not None and args.legacy_runtime != "never":
        inherited_library_path = env.get("LD_LIBRARY_PATH", "")
        compatible, reason = legacy_runtime_is_compatible(
            legacy_runtime_libraries, inherited_library_path
        )
        if compatible or args.legacy_runtime == "always":
            env["LD_LIBRARY_PATH"] = os.pathsep.join(
                path for path in (str(legacy_runtime_libraries), inherited_library_path) if path
            )
            # Unity's bundled compatibility tree may carry a different ICU from
            # the host; child .NET tools can use invariant globalization.
            env.setdefault("DOTNET_SYSTEM_GLOBALIZATION_INVARIANT", "1")
        elif args.legacy_runtime == "auto":
            print(f"Skipping incompatible Unity legacy runtime: {reason}")
    env["RECOVERED_ANDROID_APK"] = str(output)
    env["RECOVERED_SCRIPTING_BACKEND"] = args.backend
    env["RECOVERED_ANDROID_ARCH"] = args.architecture
    env["RECOVERED_ANDROID_KEYSTORE"] = str(project_keystore)
    env["RECOVERED_ANDROID_KEYSTORE_PASSWORD"] = password
    command = [
        str(unity), "-batchmode", "-disableManagedDebugger", "-quit",
        "-buildTarget", "Android",
        "-projectPath", str(project), "-executeMethod", "RebuildRecoveredAndroid.Build",
        "-logFile", str(log), "-stackTraceLogType", "Full",
    ]
    if not args.graphics:
        command.insert(2, "-nographics")
    # Capture the cost of the expensive phase without another project copy.
    unity_started = time.monotonic()
    preflight_seconds = unity_started - cycle_started
    storage_before = shutil.disk_usage(project)
    try:
        result = subprocess.run(command, env=env, check=False)
    finally:
        project_keystore.unlink(missing_ok=True)
    unity_seconds = time.monotonic() - unity_started
    repair_queue = log.with_suffix(log.suffix + ".repair-queue.md")
    queue_builder = Path(__file__).resolve().with_name("summarize_unity_repair_queue.py")
    if log.is_file():
        queue_result = subprocess.run(
            [sys.executable, str(queue_builder), str(log), "--output", str(repair_queue)],
            check=False,
        )
        if queue_result.returncode == 0:
            print(f"Unity repair queue: {repair_queue}")
        else:
            print(f"Could not summarize Unity repair queue; inspect {log}", file=sys.stderr)
    else:
        print(f"Unity exited before creating its log: {log}", file=sys.stderr)
    raw_log_bytes = log.stat().st_size if log.is_file() else 0
    if args.compress_log and log.is_file():
        compressed_log = log.with_suffix(log.suffix + ".gz")
        try:
            with log.open("rb") as source, gzip.open(compressed_log, "wb", compresslevel=6) as destination:
                shutil.copyfileobj(source, destination)
            log.unlink()
            log = compressed_log
            print(f"Compressed Unity log: {log}")
        except OSError as error:
            compressed_log.unlink(missing_ok=True)
            print(f"Could not compress Unity log; original retained at {log}: {error}", file=sys.stderr)
    storage_after = shutil.disk_usage(project)
    queue_items = 0
    if repair_queue.is_file():
        queue_items = sum(
            line.startswith("- **")
            for line in repair_queue.read_text(errors="replace").splitlines()
        )
    retained_log_bytes = log.stat().st_size if log.is_file() else 0
    cycle_report = log.with_suffix(log.suffix + ".cycle.json")
    cycle_report.write_text(json.dumps({
        "unity": str(unity),
        "project": str(project),
        "backend": args.backend,
        "architecture": args.architecture,
        "preflight_seconds": round(preflight_seconds, 2),
        "unity_seconds": round(unity_seconds, 2),
        "result_code": result.returncode,
        "raw_log_bytes": raw_log_bytes,
        "retained_log_bytes": retained_log_bytes,
        "repair_queue_items": queue_items,
        "external_free_before_bytes": storage_before.free,
        "external_free_after_bytes": storage_after.free,
        "net_free_space_delta_bytes": storage_after.free - storage_before.free,
        "apk_bytes": output.stat().st_size if output.is_file() else 0,
    }, indent=2) + "\n")
    if result.returncode and queue_items:
        failed_input_record.write_text(json.dumps({
            "fingerprint": input_fingerprint,
            "repair_queue_items": queue_items,
            "result_code": result.returncode,
            "log": str(log),
        }, indent=2) + "\n")
    elif output.is_file():
        failed_input_record.unlink(missing_ok=True)
    free_delta = storage_after.free - storage_before.free
    if abs(free_delta) < 1024**3:
        free_delta_label = f"{free_delta / 1024**2:+.2f} MiB"
    else:
        free_delta_label = f"{free_delta / 1024**3:+.2f} GiB"
    print(
        f"Cycle cost: preflight {preflight_seconds:.1f} s; Unity {unity_seconds / 60:.1f} min; "
        f"log {raw_log_bytes / 1024**2:.1f} MiB → {retained_log_bytes / 1024**2:.2f} MiB; "
        f"queue {queue_items} unique item(s); "
        f"net free-space change {free_delta_label}"
    )
    print(f"Cycle metadata: {cycle_report}")
    if result.returncode:
        print(f"Unity exited with status {result.returncode}; inspect {log}", file=sys.stderr)
        return result.returncode
    if not output.is_file() or output.stat().st_size == 0:
        print(f"Unity completed without producing the requested APK; inspect {log}", file=sys.stderr)
        return 1
    print(f"Built local APK: {output}")
    print(f"Unity log: {log}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
