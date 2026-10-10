#!/usr/bin/env python3
"""Build the pinned Cpp2IL revision with this repository's generic IL-generation fixes."""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import shutil
import subprocess
import sys
from pathlib import Path

from recovery_environment import check_external_build_storage


CPP2IL_COMMIT = "b5ad444b82267cb1e4b88b8b373c008105bdea52"
PATCH_PATH = Path(__file__).resolve().parent / "patches/cpp2il-b5ad444-recovery.patch"
PATCH_SHA256 = "4b84ec9fe3d0662bdc07d34d6e80af512da1870668df719c5116d0f1cf2f8565"


def run(command: list[str], *, cwd: Path | None = None, env: dict[str, str] | None = None) -> str:
    result = subprocess.run(command, cwd=cwd, env=env, text=True, check=False)
    if result.returncode:
        raise RuntimeError(f"command failed ({result.returncode}): {' '.join(command)}")
    return " ".join(command)


def output_of(
    *command: str, cwd: Path | None = None, env: dict[str, str] | None = None
) -> str:
    result = subprocess.run(
        command, cwd=cwd, env=env, text=True, capture_output=True, check=False
    )
    if result.returncode:
        raise RuntimeError((result.stderr or result.stdout).strip())
    return result.stdout.strip()


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("source", type=Path, help="local git clone of Cpp2IL")
    parser.add_argument("--dotnet", type=Path, required=True, help=".NET 10 dotnet executable")
    parser.add_argument("--dotnet-root", type=Path, help="set DOTNET_ROOT for the build and resulting apphost")
    parser.add_argument("--output", type=Path, required=True, help="new local output directory")
    args = parser.parse_args()

    source = args.source.expanduser().resolve()
    dotnet = args.dotnet.expanduser().resolve()
    dotnet_root = args.dotnet_root.expanduser().resolve() if args.dotnet_root else None
    output = args.output.expanduser().resolve()
    if not (source / "Cpp2IL.slnx").is_file():
        parser.error(f"not a Cpp2IL source checkout: {source}")
    if not dotnet.is_file():
        parser.error(f"dotnet executable does not exist: {dotnet}")
    if output.exists() and any(output.iterdir()):
        parser.error(f"output directory must be empty or absent: {output}")
    actual_sha256 = hashlib.sha256(PATCH_PATH.read_bytes()).hexdigest()
    if actual_sha256 != PATCH_SHA256:
        parser.error(f"Cpp2IL patch SHA-256 is {actual_sha256}, expected {PATCH_SHA256}")

    try:
        check_external_build_storage(Path(__file__).resolve().parent.parent)
    except RuntimeError as error:
        parser.error(str(error))

    head = output_of("git", "rev-parse", "HEAD", cwd=source)
    if head != CPP2IL_COMMIT:
        parser.error(f"Cpp2IL checkout is {head}, expected pinned commit {CPP2IL_COMMIT}")
    output.mkdir(parents=True, exist_ok=True)
    patched_source = output / "source"
    build_dir = output / "build"
    run(["git", "worktree", "add", "--detach", str(patched_source), CPP2IL_COMMIT], cwd=source)
    run(["git", "apply", "--unidiff-zero", "--check", str(PATCH_PATH)], cwd=patched_source)
    run(["git", "apply", "--unidiff-zero", str(PATCH_PATH)], cwd=patched_source)

    env = os.environ.copy()
    if dotnet_root:
        env["DOTNET_ROOT"] = str(dotnet_root)
    build_command = [
        str(dotnet), "build", str(patched_source / "Cpp2IL/Cpp2IL.csproj"),
        "--configuration", "Debug", "--framework", "net10.0",
    ]
    run(build_command, env=env)

    compiled = patched_source / "Cpp2IL/bin/Debug/net10.0"
    if not (compiled / "Cpp2IL.dll").is_file():
        raise RuntimeError(f"Cpp2IL build produced no DLL in {compiled}")
    shutil.copytree(compiled, build_dir)
    executable = build_dir / "Cpp2IL"
    if not executable.is_file():
        executable = dotnet
        version = output_of(
            str(dotnet), str(build_dir / "Cpp2IL.dll"), "--version", cwd=output, env=env
        )
        executable_path = str(build_dir / "Cpp2IL.dll")
    else:
        version = output_of(str(executable), "--version", cwd=output, env=env)
        executable_path = str(executable)

    manifest = {
        "upstream": "https://github.com/SamboyCoding/Cpp2IL",
        "commit": CPP2IL_COMMIT,
        "patch": str(PATCH_PATH),
        "patch_sha256": actual_sha256,
        "version": version,
        "executable": executable_path,
        "build_command": build_command,
    }
    (output / "manifest.json").write_text(json.dumps(manifest, indent=2) + "\n")
    print(f"Patched Cpp2IL: {executable_path}")
    print(version)
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (OSError, RuntimeError, subprocess.SubprocessError) as error:
        print(f"build_patched_cpp2il: {error}", file=sys.stderr)
        raise SystemExit(1)
