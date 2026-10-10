#!/usr/bin/env python3
"""Preflight the configured external build volume before large recovery jobs."""

from __future__ import annotations

import os
import shutil
import tempfile
from pathlib import Path


MIN_FREE_BYTES = 10 * 1024**3


def check_external_build_storage(project_root: Path, *, min_free_bytes: int = MIN_FREE_BYTES) -> dict[str, object] | None:
    """Validate the repo's build symlink target when it is configured externally.

    Repos without a build symlink keep their usual behavior. When one is set,
    fail closed if the target volume is unmounted or read-only; otherwise a
    missing mount can turn a large Unity build into an accidental root-disk write.
    """
    build_link = project_root / "build"
    if not build_link.is_symlink():
        return None

    try:
        workspace_build = build_link.resolve(strict=True)
        external_root = workspace_build.parent
        external_stat = external_root.stat()
    except OSError as error:
        raise RuntimeError(f"configured external build volume is unavailable via {build_link}: {error}") from error

    if not external_root.is_dir():
        raise RuntimeError(f"external build root is not a directory: {external_root}")
    if external_stat.st_dev == Path("/").stat().st_dev:
        raise RuntimeError(
            f"build symlink resolves onto the root filesystem, not external storage: {external_root}"
        )

    usage = shutil.disk_usage(external_root)
    if usage.free < min_free_bytes:
        free_gib = usage.free / 1024**3
        required_gib = min_free_bytes / 1024**3
        raise RuntimeError(
            f"external build volume has {free_gib:.1f} GiB free; need at least {required_gib:.0f} GiB"
        )

    try:
        with tempfile.NamedTemporaryFile(prefix=".recovery-write-check-", dir=external_root) as probe:
            probe.write(b"recovery build storage check\n")
            probe.flush()
            os.fsync(probe.fileno())
    except OSError as error:
        raise RuntimeError(f"external build volume is not writable: {external_root}: {error}") from error

    return {
        "root": str(external_root),
        "device_id": external_stat.st_dev,
        "free_bytes": usage.free,
        "writable": True,
    }


if __name__ == "__main__":
    root = Path(__file__).resolve().parent.parent
    try:
        status = check_external_build_storage(root)
    except RuntimeError as error:
        raise SystemExit(f"recovery environment check failed: {error}")
    if status is None:
        print("No external build symlink configured; using local build storage")
    else:
        print(
            f"External build storage OK: {status['root']} "
            f"({status['free_bytes'] / 1024**3:.1f} GiB free, writable)"
        )
