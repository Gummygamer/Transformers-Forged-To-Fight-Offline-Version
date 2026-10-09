#!/usr/bin/env python3
"""Build a local staged 9.2 Unity recovery project as an ARM64 Android APK."""

from __future__ import annotations

import argparse
import json
import os
import shutil
import struct
import subprocess
import sys
from pathlib import Path


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
    args = parser.parse_args()

    project = args.project.expanduser().resolve()
    unity = args.unity.expanduser().resolve()
    output = args.output_apk.expanduser().resolve()
    log = args.log.expanduser().resolve() if args.log else output.with_suffix(".unity.log")
    if not (project / "ProjectSettings/ProjectVersion.txt").is_file():
        parser.error(f"not a Unity project: {project}")
    version = (project / "ProjectSettings/ProjectVersion.txt").read_text()
    if "2020.3.31f1" not in version:
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
    subprocess.run(repair_command, check=True)
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

    env = os.environ.copy()
    legacy_runtime_libraries = unity.parents[4] / "legacy-runtime/usr/lib/x86_64-linux-gnu"
    if (legacy_runtime_libraries / "libxml2.so.2").is_file():
        inherited_library_path = env.get("LD_LIBRARY_PATH", "")
        env["LD_LIBRARY_PATH"] = os.pathsep.join(
            path for path in (str(legacy_runtime_libraries), inherited_library_path) if path
        )
        # The Unity editor's bundled compatibility tree has ICU 74, while the
        # host's CoreCLR/Roslyn expects the system ICU 78. The launcher needs
        # this tree for libxml2, but child CoreCLR tools can run invariantly.
        env.setdefault("DOTNET_SYSTEM_GLOBALIZATION_INVARIANT", "1")
    env["RECOVERED_ANDROID_APK"] = str(output)
    env["RECOVERED_SCRIPTING_BACKEND"] = args.backend
    env["RECOVERED_ANDROID_ARCH"] = args.architecture
    env["RECOVERED_ANDROID_KEYSTORE"] = str(keystore)
    env["RECOVERED_ANDROID_KEYSTORE_PASSWORD"] = password
    command = [
        str(unity), "-batchmode", "-nographics", "-quit", "-buildTarget", "Android",
        "-projectPath", str(project), "-executeMethod", "RebuildRecoveredAndroid.Build",
        "-logFile", str(log), "-stackTraceLogType", "Full",
    ]
    result = subprocess.run(command, env=env, check=False)
    repair_queue = log.with_suffix(log.suffix + ".repair-queue.md")
    queue_builder = Path(__file__).resolve().with_name("summarize_unity_repair_queue.py")
    subprocess.run(
        [sys.executable, str(queue_builder), str(log), "--output", str(repair_queue)],
        check=True,
    )
    print(f"Unity repair queue: {repair_queue}")
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
