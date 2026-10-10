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

from recovery_environment import check_external_build_storage


# This exact 9.2 APK was inspected and found to be debug-signed and offline-patched.
# It is retained as a denylist identity, never as an accepted pristine source hash.
KNOWN_OFFLINE_PATCHED_APK_SHA256 = "68ad382f3229578084f8590c236acf9a5547bda829e12e8beb929d844af7c1b9"
# This exact APK was reconstructed from the 9.2 package payload and retains the
# original EBG JAR content signature. Its Android v2/v3 signing block was
# stripped, so it is an extraction source, not a directly installable APK.
KNOWN_CLEAN_9_2_APK_SHA256 = "cae78579898a002b65b766d816584331972de79ed6183d7c7e9c943fc4403406"
KNOWN_CLEAN_9_2_SIGNER_SHA256 = "A8213D062F720775260A2F96E01AE5AD279AFEDFA4D63050EB815149F369C521"
KNOWN_CLEAN_9_2_LIBIL2CPP_SHA256 = "575aa973ed8fd54e79c70abdaed5b5a3b013e8e3ec68e0fa64e98f6bdfba9b8a"
KNOWN_CLEAN_9_2_METADATA_SHA256 = "636458c3bd9319d1b9112077c2396fabcfef9d60f3f5f7ebb350b27e1a47ade7"
KNOWN_OFFLINE_ASSET_SOURCE_SHA256 = KNOWN_OFFLINE_PATCHED_APK_SHA256
REUSE_ALLOWED_APK_ENTRY_DIFFERENCES = {
    "lib/arm64-v8a/libil2cpp.so",
    "lib/arm64-v8a/libdothook.so",
    "stamp-cert-sha256",
}
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


def verify_known_clean_signature(apk: Path) -> dict[str, str | bool]:
    jarsigner = shutil.which("jarsigner")
    keytool = shutil.which("keytool")
    if not jarsigner or not keytool:
        raise RuntimeError("jarsigner and keytool are required to verify the pinned clean 9.2 source")
    signature = subprocess.run(
        [jarsigner, "-verify", "-verbose", "-certs", str(apk)],
        capture_output=True, text=True, check=False,
    )
    signature_output = signature.stdout + signature.stderr
    if signature.returncode or "jar verified." not in signature_output.lower():
        raise RuntimeError("pinned clean APK JAR content signature did not verify")
    certificate = subprocess.run(
        [keytool, "-printcert", "-jarfile", str(apk)],
        capture_output=True, text=True, check=False,
    )
    certificate_output = certificate.stdout + certificate.stderr
    if certificate.returncode:
        raise RuntimeError("could not read signer certificate from pinned clean APK")
    fingerprints = [
        line.split(":", 1)[1].replace(":", "").replace(" ", "").upper()
        for line in certificate_output.splitlines()
        if "SHA256:" in line
    ]
    if KNOWN_CLEAN_9_2_SIGNER_SHA256 not in fingerprints:
        raise RuntimeError("pinned clean APK signer certificate does not match EBG release certificate")
    return {
        "jar_content_signature_verified": True,
        "signer_sha256": KNOWN_CLEAN_9_2_SIGNER_SHA256,
        "android_v2_v3_signing_block": "stripped; source is not directly installable",
    }


def validate_source_apk(apk: Path, input_hash: str) -> dict[str, object]:
    if input_hash.lower() == KNOWN_OFFLINE_PATCHED_APK_SHA256:
        raise RuntimeError(
            "this is the known debug-signed offline-patched 9.2 APK, not a pristine source: "
            "use a full Kabam-signed APK"
        )

    hook_entry_found = False
    hook_dependency_marker_found = False
    clean_source = input_hash.lower() == KNOWN_CLEAN_9_2_APK_SHA256
    with zipfile.ZipFile(apk) as archive:
        names = set(archive.namelist())
        required = {
            "lib/arm64-v8a/libil2cpp.so",
            "assets/bin/Data/Managed/Metadata/global-metadata.dat",
        }
        missing = sorted(required - names)
        if missing:
            raise RuntimeError("APK is missing required ARM64 IL2CPP inputs: " + ", ".join(missing))

        if clean_source:
            expected_inputs = {
                "lib/arm64-v8a/libil2cpp.so": KNOWN_CLEAN_9_2_LIBIL2CPP_SHA256,
                "assets/bin/Data/Managed/Metadata/global-metadata.dat": KNOWN_CLEAN_9_2_METADATA_SHA256,
            }
            for name, expected in expected_inputs.items():
                digest = hashlib.sha256()
                with archive.open(name) as stream:
                    for block in iter(lambda: stream.read(1024 * 1024), b""):
                        digest.update(block)
                if digest.hexdigest() != expected:
                    raise RuntimeError(f"pinned clean APK has unexpected payload for {name}")

        hook_entry_found = any(
            name.startswith("lib/") and name.endswith("/libdothook.so") for name in names
        )
        if not hook_entry_found:
            marker = b"libdothook.so\x00"
            with archive.open("lib/arm64-v8a/libil2cpp.so") as library:
                overlap = b""
                while block := library.read(1024 * 1024):
                    candidate = overlap + block
                    if marker in candidate:
                        hook_dependency_marker_found = True
                        break
                    overlap = candidate[-(len(marker) - 1):]

    if hook_entry_found or hook_dependency_marker_found:
        details = []
        if hook_entry_found:
            details.append("bundled libdothook.so")
        if hook_dependency_marker_found:
            details.append("libil2cpp.so references libdothook.so")
        raise RuntimeError(
            "offline hook marker found (" + "; ".join(details) + "); refusing a patched APK"
        )
    checks: dict[str, object] = {
        "known_offline_patched_hash_rejected": True,
        "libdothook_entry_absent": True,
        "libdothook_dependency_marker_absent": True,
    }
    if clean_source:
        checks["known_clean_9_2_source_hash"] = True
        checks["release_signature"] = verify_known_clean_signature(apk)
    return checks


def verify_asset_source_pair(source_apk: Path, clean_apk: Path) -> dict[str, object]:
    if sha256(source_apk) != KNOWN_OFFLINE_ASSET_SOURCE_SHA256:
        raise RuntimeError("AssetRipper source APK is not the pinned offline asset export source")
    if sha256(clean_apk) != KNOWN_CLEAN_9_2_APK_SHA256:
        raise RuntimeError("asset reuse is allowed only for the pinned clean 9.2 APK")
    with zipfile.ZipFile(source_apk) as source, zipfile.ZipFile(clean_apk) as clean:
        source_infos = {i.filename: i for i in source.infolist()}
        clean_infos = {i.filename: i for i in clean.infolist()}
        differing = set(source_infos) ^ set(clean_infos)
        differing |= {
            name for name in set(source_infos) & set(clean_infos)
            if (source_infos[name].file_size, source_infos[name].CRC, source_infos[name].compress_type)
            != (clean_infos[name].file_size, clean_infos[name].CRC, clean_infos[name].compress_type)
        }
    allowed = REUSE_ALLOWED_APK_ENTRY_DIFFERENCES | {
        name for name in differing if name.startswith("META-INF/")
    }
    if differing - allowed:
        raise RuntimeError("APK payload differs outside approved native/signature entries: "
                           + ", ".join(sorted(differing - allowed)[:12]))
    if not REUSE_ALLOWED_APK_ENTRY_DIFFERENCES.issubset(differing):
        raise RuntimeError("APK pair no longer has the expected native/certificate-stamp differences")
    return {
        "asset_source_apk_sha256": KNOWN_OFFLINE_ASSET_SOURCE_SHA256,
        "clean_apk_sha256": KNOWN_CLEAN_9_2_APK_SHA256,
        "differing_entries": sorted(differing),
        "matching_non_signature_entries": len(set(source_infos) - differing),
        "comparison": "ZIP entry name, uncompressed size, CRC-32, and compression method",
    }


def tool_launch(executable: Path) -> tuple[list[str], dict[str, str]]:
    """Launch a packaged .NET apphost with the SDK recorded in its build manifest."""
    env = os.environ.copy()
    managed_assembly = executable.with_suffix(".dll")
    manifest = executable.parent.parent / "manifest.json"
    if managed_assembly.is_file() and manifest.is_file():
        try:
            details = json.loads(manifest.read_text())
            build_command = details.get("build_command", [])
            dotnet = Path(build_command[0]).expanduser() if build_command else None
        except (OSError, json.JSONDecodeError, IndexError, TypeError):
            dotnet = None
        if dotnet is not None and dotnet.is_file():
            dotnet = dotnet.resolve()
            env["DOTNET_ROOT"] = str(dotnet.parent)
            return [str(dotnet), str(managed_assembly)], env
    return [str(executable)], env


def run_version(executable: Path, *args: str) -> str:
    command, env = tool_launch(executable)
    result = subprocess.run(
        [*command, *args], capture_output=True, text=True, check=False, env=env
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
    shutil.copytree(
        exported,
        destination,
        ignore=shutil.ignore_patterns(
            "Library", "Temp", "Logs", "UserSettings", "obj", "Build", "Builds"
        ),
    )
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
        help="accept a new APK hash only after independently verifying package, version, and Kabam release signer",
    )
    parser.add_argument(
        "--reuse-unity-project", type=Path,
        help="reuse a prior Unity asset export after its source APK is proven payload-equivalent",
    )
    parser.add_argument(
        "--asset-source-apk", type=Path,
        help="APK used for the prior AssetRipper export (required with --reuse-unity-project)",
    )
    args = parser.parse_args()

    apk = args.apk.expanduser().resolve()
    output = args.output.expanduser().resolve()
    cpp2il = Path(args.cpp2il).expanduser().resolve() if args.cpp2il else None
    assetripper = Path(args.assetripper).expanduser().resolve() if args.assetripper else None
    reuse_unity_project = (
        args.reuse_unity_project.expanduser().resolve() if args.reuse_unity_project else None
    )
    asset_source_apk = args.asset_source_apk.expanduser().resolve() if args.asset_source_apk else None
    if bool(reuse_unity_project) != bool(asset_source_apk):
        parser.error("--reuse-unity-project and --asset-source-apk must be supplied together")
    if not apk.is_file():
        parser.error(f"APK does not exist: {apk}")
    if cpp2il is None or not cpp2il.is_file():
        parser.error("provide --cpp2il or set CPP2IL_BIN to the Cpp2IL executable")
    if reuse_unity_project is None and (assetripper is None or not assetripper.is_file()):
        parser.error("provide --assetripper or set ASSETRIPPER_BIN to AssetRipper.GUI.Free")
    cpp2il_version = run_version(cpp2il, "--version")
    assetripper_version = run_version(assetripper, "--version") if assetripper else "reused export"
    if CPP2IL_COMMIT not in cpp2il_version:
        parser.error(
            f"Cpp2IL build does not identify pinned commit {CPP2IL_COMMIT}: {cpp2il_version}"
        )
    if reuse_unity_project is None and ASSETRIPPER_VERSION not in assetripper_version:
        parser.error(f"AssetRipper version does not match pinned build: {assetripper_version}")
    if output.exists() and any(output.iterdir()):
        parser.error(f"output directory must be empty or absent: {output}")

    try:
        check_external_build_storage(Path(__file__).resolve().parent.parent)
    except RuntimeError as error:
        parser.error(str(error))

    input_hash = sha256(apk)
    try:
        source_checks = validate_source_apk(apk, input_hash)
    except (RuntimeError, zipfile.BadZipFile) as error:
        parser.error(str(error))
    if input_hash.lower() != KNOWN_CLEAN_9_2_APK_SHA256 and not args.allow_unverified_apk:
        parser.error(
            f"no pristine Kabam 9.2.0 APK hash is verified in this workspace (input SHA-256 {input_hash}); "
            "verify package/version with REA and the release signer with apksigner, then pass "
            "--allow-unverified-apk"
        )

    asset_pair_checks = None
    if reuse_unity_project is not None:
        if not reuse_unity_project.is_dir():
            parser.error(f"reused Unity project does not exist: {reuse_unity_project}")
        if not asset_source_apk or not asset_source_apk.is_file():
            parser.error("AssetRipper source APK does not exist")
        try:
            asset_pair_checks = verify_asset_source_pair(asset_source_apk, apk)
        except (RuntimeError, zipfile.BadZipFile) as error:
            parser.error(str(error))
        reused_version = reuse_unity_project / "ProjectSettings/ProjectVersion.txt"
        if (
            not reused_version.is_file()
            or not (reuse_unity_project / "Assets").is_dir()
            or UNITY_VERSION not in reused_version.read_text()
        ):
            parser.error(f"reused Unity project is incomplete or not Unity {UNITY_VERSION}")

    cpp2il_command, cpp2il_env = tool_launch(cpp2il)
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
            *cpp2il_command, f"--game-path={apk}",
            f"--force-binary-path={native_dir / 'libil2cpp.so'}",
            f"--force-metadata-path={native_dir / 'global-metadata.dat'}",
            f"--force-unity-version={UNITY_VERSION}",
            "--output-as", "dll_il_recovery", "--output-to", str(cpp2il_out),
        ],
        env=cpp2il_env,
        check=False,
    )
    if cpp2il_result.returncode:
        raise RuntimeError(f"Cpp2IL exited with status {cpp2il_result.returncode}")

    asset_log = output / "assetripper.log" if reuse_unity_project is None else None
    console_log = output / "assetripper-console.log" if reuse_unity_project is None else None
    if reuse_unity_project is not None:
        unity_project = reuse_unity_project
        version_file = unity_project / "ProjectSettings/ProjectVersion.txt"
    else:
        port = free_port()
        assert asset_log is not None and console_log is not None and assetripper is not None
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
        exported_subproject = unity_project / "ExportedProject"
        if not version_file.is_file() and (
            exported_subproject / "ProjectSettings/ProjectVersion.txt"
        ).is_file():
            unity_project = exported_subproject
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
        "known_offline_patched_apk_sha256": KNOWN_OFFLINE_PATCHED_APK_SHA256,
        "known_clean_9_2_apk_sha256": KNOWN_CLEAN_9_2_APK_SHA256,
        "source_checks": source_checks,
        "reused_asset_export_checks": asset_pair_checks,
        "libil2cpp_sha256": sha256(native_dir / "libil2cpp.so"),
        "global_metadata_sha256": sha256(native_dir / "global-metadata.dat"),
        "cpp2il_version": cpp2il_version,
        "cpp2il_executable": str(cpp2il),
        "assetripper_version": assetripper_version,
        "assetripper_executable": str(assetripper) if assetripper else None,
        "unity_editor_version": UNITY_VERSION,
        "unity_project_version_file": version_file.read_text().strip(),
        "outputs": {
            "cpp2il": str(cpp2il_out),
            "unity_project": str(unity_project),
            "unity_rebuild_project": str(rebuild_project),
            "assembly_set": str(output / "assembly-set.txt"),
            "saved_assetripper_scripts": str(output / "RecoveredScripts"),
            "asset_ripper_log": str(asset_log) if asset_log else None,
            "asset_ripper_console_log": str(console_log) if console_log else None,
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
