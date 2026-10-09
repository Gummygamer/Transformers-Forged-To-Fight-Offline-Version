#!/usr/bin/env python3
"""Recreate local 9.2 IL2CPP and Unity recovery outputs from an operator APK."""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import shutil
import signal
import socket
import subprocess
import sys
import time
import urllib.error
import urllib.parse
import urllib.request
import zipfile
from pathlib import Path


APK_SHA256 = "68ad382f3229578084f8590c236acf9a5547bda829e12e8beb929d844af7c1b9"
CPP2IL_COMMIT = "b5ad444b82267cb1e4b88b8b373c008105bdea52"
ASSETRIPPER_VERSION = "2.0.0+1ac666f47d8e9dedf96afb0b914c70d7656151ea"
UNITY_VERSION = "2020.3.31f1"
REBUILD_ASSEMBLIES = (
    "Assembly-CSharp-firstpass.dll",
    "Assembly-CSharp.dll",
    "BouncyCastle.dll",
    "Fabric.Core.dll",
    "Facebook.Unity.Android.dll",
    "Facebook.Unity.Settings.dll",
    "Facebook.Unity.dll",
    "Firebase.App.dll",
    "Firebase.Messaging.dll",
    "Firebase.Platform.dll",
    "Google.Play.AssetDelivery.dll",
    "Google.Play.Common.dll",
    "Google.Play.Core.dll",
    "ICSharpCode.SharpZipLib.dll",
    "Kabam.Krash.Native.dll",
    "Kabam.Logger.dll",
    "Mono.Security.dll",
    "NBidi.dll",
    "enum2int.dll",
)


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def run_version(executable: Path, *args: str) -> str:
    result = subprocess.run(
        [str(executable), *args], capture_output=True, text=True, check=False
    )
    return (result.stdout + result.stderr).strip()


def free_port() -> int:
    with socket.socket() as sock:
        sock.bind(("127.0.0.1", 0))
        return int(sock.getsockname()[1])


def wait_for_server(url: str, process: subprocess.Popen[bytes], log: Path) -> None:
    deadline = time.monotonic() + 120
    while time.monotonic() < deadline:
        if process.poll() is not None:
            raise RuntimeError(f"AssetRipper exited ({process.returncode}); inspect {log}")
        try:
            with urllib.request.urlopen(url, timeout=2):
                return
        except (urllib.error.URLError, TimeoutError, OSError):
            time.sleep(1)
    raise TimeoutError(f"AssetRipper did not start at {url}; inspect {log}")


def post_form(url: str, values: dict[str, str], timeout: int = 1800) -> bytes:
    data = urllib.parse.urlencode(values).encode()
    request = urllib.request.Request(
        url, data=data, headers={"Content-Type": "application/x-www-form-urlencoded"}
    )
    with urllib.request.urlopen(request, timeout=timeout) as response:
        return response.read()


def stage_unity_project(exported: Path, cpp2il_out: Path, destination: Path, output: Path) -> None:
    missing = [name for name in REBUILD_ASSEMBLIES if not (cpp2il_out / name).is_file()]
    if missing:
        raise RuntimeError("Cpp2IL did not produce required assemblies: " + ", ".join(missing))
    shutil.copytree(exported, destination)
    recovered_scripts = output / "RecoveredScripts"
    for source in (destination / "Assets").rglob("*.cs"):
        relative = source.relative_to(destination)
        saved_source = recovered_scripts / relative
        saved_source.parent.mkdir(parents=True, exist_ok=True)
        shutil.move(str(source), str(saved_source))
        source_meta = source.with_suffix(source.suffix + ".meta")
        if source_meta.is_file():
            shutil.move(str(source_meta), str(saved_source.with_suffix(saved_source.suffix + ".meta")))

    plugin_dir = destination / "Assets/Plugins"
    plugin_dir.mkdir(parents=True, exist_ok=True)
    for assembly in REBUILD_ASSEMBLIES:
        shutil.copy2(cpp2il_out / assembly, plugin_dir / assembly)

    (output / "assembly-set.txt").write_text("\n".join(REBUILD_ASSEMBLIES) + "\n")


def main() -> int:
    parser = argparse.ArgumentParser(
        description="Recreate local 9.2 recovery outputs from a user-supplied APK."
    )
    parser.add_argument("apk", type=Path, help="operator-supplied Transformers 9.2.0 APK")
    parser.add_argument(
        "--output",
        type=Path,
        default=Path("build/recovery-9.2"),
        help="local output directory (default: build/recovery-9.2)",
    )
    parser.add_argument(
        "--cpp2il", type=Path, default=os.environ.get("CPP2IL_BIN"),
        help="Cpp2IL executable (or set CPP2IL_BIN)",
    )
    parser.add_argument(
        "--assetripper", type=Path, default=os.environ.get("ASSETRIPPER_BIN"),
        help="AssetRipper.GUI.Free executable (or set ASSETRIPPER_BIN)",
    )
    parser.add_argument(
        "--allow-unverified-apk", action="store_true",
        help="continue when the APK SHA-256 differs from the known 9.2.0 input",
    )
    args = parser.parse_args()

    apk = args.apk.expanduser().resolve()
    output = args.output.expanduser().resolve()
    cpp2il = Path(args.cpp2il).expanduser().resolve() if args.cpp2il else None
    assetripper = Path(args.assetripper).expanduser().resolve() if args.assetripper else None
    if not apk.is_file():
        parser.error(f"APK does not exist: {apk}")
    if cpp2il is None or not cpp2il.is_file():
        parser.error("provide --cpp2il or set CPP2IL_BIN to the Cpp2IL executable")
    if assetripper is None or not assetripper.is_file():
        parser.error("provide --assetripper or set ASSETRIPPER_BIN to AssetRipper.GUI.Free")
    cpp2il_version = run_version(cpp2il, "--version")
    assetripper_version = run_version(assetripper, "--version")
    if CPP2IL_COMMIT not in cpp2il_version:
        parser.error(
            f"Cpp2IL build does not identify pinned commit {CPP2IL_COMMIT}: {cpp2il_version}"
        )
    if ASSETRIPPER_VERSION not in assetripper_version:
        parser.error(f"AssetRipper version does not match pinned build: {assetripper_version}")
    if output.exists() and any(output.iterdir()):
        parser.error(f"output directory must be empty or absent: {output}")

    input_hash = sha256(apk)
    if input_hash != APK_SHA256 and not args.allow_unverified_apk:
        parser.error(
            f"APK SHA-256 is {input_hash}, expected known 9.2.0 input {APK_SHA256}; "
            "use --allow-unverified-apk only after checking its package/version"
        )

    output.mkdir(parents=True, exist_ok=True)
    native_dir = output / "apk-native"
    native_dir.mkdir()
    with zipfile.ZipFile(apk) as archive:
        for name, target in (
            ("lib/arm64-v8a/libil2cpp.so", native_dir / "libil2cpp.so"),
            (
                "assets/bin/Data/Managed/Metadata/global-metadata.dat",
                native_dir / "global-metadata.dat",
            ),
        ):
            try:
                with archive.open(name) as source, target.open("wb") as dest:
                    shutil.copyfileobj(source, dest)
            except KeyError as exc:
                raise RuntimeError(f"APK is missing required IL2CPP input: {name}") from exc

    cpp2il_out = output / "cpp2il"
    cpp2il_result = subprocess.run(
        [
            str(cpp2il), f"--game-path={apk}",
            f"--force-binary-path={native_dir / 'libil2cpp.so'}",
            f"--force-metadata-path={native_dir / 'global-metadata.dat'}",
            f"--force-unity-version={UNITY_VERSION}",
            "--output-as", "dll_il_recovery", "--output-to", str(cpp2il_out),
        ],
        check=False,
    )
    if cpp2il_result.returncode:
        raise RuntimeError(f"Cpp2IL exited with status {cpp2il_result.returncode}")

    port = free_port()
    asset_log = output / "assetripper.log"
    console_log = output / "assetripper-console.log"
    log_stream = console_log.open("wb")
    process = subprocess.Popen(
        [
            str(assetripper), "--headless", "--port", str(port), "--log",
            "--log-path", str(asset_log),
        ],
        stdout=log_stream,
        stderr=subprocess.STDOUT,
    )
    base = f"http://127.0.0.1:{port}"
    try:
        wait_for_server(base, process, asset_log)
        post_form(f"{base}/LoadFile", {"path": str(apk)})
        unity_project = output / "unity-project"
        unity_project.mkdir()
        post_form(
            f"{base}/Export/UnityProject",
            {"Path": str(unity_project), "CreateSubfolder": "false"},
            timeout=7200,
        )
    finally:
        process.send_signal(signal.SIGTERM)
        try:
            process.wait(timeout=20)
        except subprocess.TimeoutExpired:
            process.kill()
            process.wait()
        log_stream.close()

    version_file = unity_project / "ProjectSettings/ProjectVersion.txt"
    if not version_file.is_file() or not (unity_project / "Assets").is_dir():
        raise RuntimeError(f"AssetRipper export is incomplete; inspect {asset_log}")
    if UNITY_VERSION not in version_file.read_text():
        raise RuntimeError(f"exported Unity version does not match {UNITY_VERSION}: {version_file}")

    rebuild_project = output / "unity-rebuild"
    stage_unity_project(unity_project, cpp2il_out, rebuild_project, output)

    manifest = {
        "apk": str(apk),
        "apk_sha256": input_hash,
        "known_9_2_sha256": APK_SHA256,
        "libil2cpp_sha256": sha256(native_dir / "libil2cpp.so"),
        "global_metadata_sha256": sha256(native_dir / "global-metadata.dat"),
        "cpp2il_version": cpp2il_version,
        "cpp2il_executable": str(cpp2il),
        "assetripper_version": assetripper_version,
        "assetripper_executable": str(assetripper),
        "unity_editor_version": UNITY_VERSION,
        "unity_project_version_file": version_file.read_text().strip(),
        "outputs": {
            "cpp2il": str(cpp2il_out),
            "unity_project": str(unity_project),
            "unity_rebuild_project": str(rebuild_project),
            "assembly_set": str(output / "assembly-set.txt"),
            "saved_assetripper_scripts": str(output / "RecoveredScripts"),
            "asset_ripper_log": str(asset_log),
            "asset_ripper_console_log": str(console_log),
        },
        "notice": "Generated local recovery output; do not add to Git or redistribute.",
    }
    (output / "recovery-manifest.json").write_text(
        json.dumps(manifest, indent=2) + "\n"
    )
    print(f"Recovery outputs: {output}")
    print(f"Cpp2IL: {cpp2il_out}")
    print(f"AssetRipper Unity project: {unity_project}")
    print(f"Unity project version: {version_file.read_text().strip()}")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (OSError, RuntimeError, TimeoutError, zipfile.BadZipFile) as error:
        print(f"recover_9_2: {error}", file=sys.stderr)
        raise SystemExit(1)
