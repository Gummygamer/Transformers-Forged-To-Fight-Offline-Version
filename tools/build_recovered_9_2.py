#!/usr/bin/env python3
"""Build a local staged 9.2 Unity recovery project as an ARM64 Android APK."""

from __future__ import annotations

import argparse
import os
import shutil
import subprocess
import sys
from pathlib import Path


def is_within(path: Path, parent: Path) -> bool:
    try:
        path.relative_to(parent)
        return True
    except ValueError:
        return False


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("project", type=Path, help="writable staged Unity project directory")
    parser.add_argument("--unity", type=Path, required=True, help="Unity 2020.3.31f1 Editor executable")
    parser.add_argument("--output-apk", type=Path, required=True, help="local output APK path")
    parser.add_argument("--log", type=Path, help="Unity log path (defaults beside the APK)")
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

    keystore = output.with_suffix(".keystore")
    keytool = unity.parent / "Data/PlaybackEngines/AndroidPlayer/OpenJDK/bin/keytool"
    if not keytool.is_file():
        parser.error(f"Unity's bundled keytool does not exist: {keytool}")

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
