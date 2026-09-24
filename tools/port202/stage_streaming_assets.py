#!/usr/bin/env python3
"""Stage pack indexes for converted Android StreamingAssets bundles."""

from __future__ import annotations

import argparse
import json
import shutil
from pathlib import Path


def is_unity_version(bundle: Path, version: str) -> bool:
    """Return true only when the bundle's UnityFS header names this editor version."""
    try:
        header = bundle.read_bytes()[:128]
    except OSError:
        return False
    return header.startswith(b"UnityFS\0") and version.encode("ascii") + b"\0" in header


def stage(
    source_assets: Path,
    bundle_root: Path,
    android_assets: Path,
    native_unity_version: str | None = None,
    native_packs: set[str] | None = None,
) -> int:
    source_assets = source_assets.resolve()
    bundle_root = bundle_root.resolve()
    android_assets = android_assets.resolve()
    source_index = source_assets / "packs.txt"
    destination = android_assets
    index = json.loads(source_index.read_text(encoding="utf-8-sig"))
    selected: dict[str, str] = {}
    copied = 0

    for name, relative in index.get("packs", {}).items():
        source_toc = source_assets / relative / "toc.txt"
        destination_toc = destination / relative / "toc.txt"
        if not source_toc.is_file():
            continue
        toc = json.loads(source_toc.read_text(encoding="utf-8-sig"))
        bundles = toc.get("bundles", {})
        present: dict[str, dict] = {}
        for bundle_name, bundle in bundles.items():
            relative_bundle = Path(bundle.get("file", f"{bundle_name}.assetbundle"))
            converted_bundle = bundle_root / relative_bundle
            # A few small core bundles (for example global/quest.assetbundle)
            # are already compatible with the target editor and are not rebuilt
            # by AssetRipper.
            raw_bundle = source_assets / "assetpack" / name / relative_bundle.name
            source_bundle = converted_bundle
            if not source_bundle.is_file() and raw_bundle.is_file() and raw_bundle.stat().st_size <= 1_000_000:
                source_bundle = raw_bundle
            # Some source packs are already built by the same Unity editor as
            # the recompilation target. A Unity version is an explicit opt-in
            # to raw fallback; when a pack allowlist is supplied, keep it
            # narrow, otherwise accept every bundle whose header matches.
            if (
                not source_bundle.is_file()
                and raw_bundle.is_file()
                and native_unity_version
                and (not native_packs or name in native_packs)
                and is_unity_version(raw_bundle, native_unity_version)
            ):
                source_bundle = raw_bundle
            if not source_bundle.is_file():
                continue
            destination_bundle = destination / relative_bundle
            destination_bundle.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(source_bundle, destination_bundle)
            present[bundle_name] = bundle

        loose_source_root = source_assets / "assetpack" / relative
        loose_files = {
            path.relative_to(loose_source_root).as_posix().lower(): path
            for path in loose_source_root.rglob("*")
            if path.is_file()
        } if loose_source_root.is_dir() else {}
        present_files: list[str] = []
        for relative_file in toc.get("files", []):
            relative_file = Path(relative_file)
            source_file = loose_files.get(relative_file.as_posix().lower())
            if source_file is None:
                continue
            destination_file = destination / relative / relative_file
            destination_file.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(source_file, destination_file)
            present_files.append(relative_file.as_posix())

        if not present and not present_files:
            continue

        toc["bundles"] = present
        toc["files"] = present_files
        destination_toc.parent.mkdir(parents=True, exist_ok=True)
        destination_toc.write_text(json.dumps(toc, separators=(",", ":")), encoding="utf-8")
        selected[name] = relative
        copied += 1

    (destination / "packs.txt").write_text(
        json.dumps(
            {"scenes": index.get("scenes", []), "packs": selected},
            separators=(",", ":"),
        ),
        encoding="utf-8",
    )
    return copied


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("source_assets", type=Path, help="extracted 9.2 assetpack directory")
    parser.add_argument("bundle_root", type=Path, help="converted bundles under StreamingAssets/tftf-9.2")
    parser.add_argument("android_assets", type=Path, help="Assets/Plugins/Android/assets directory")
    parser.add_argument("--native-unity-version", help="allow raw bundles whose UnityFS header matches this version")
    parser.add_argument("--native-pack", action="append", default=[], help="optional pack allowlist for matching raw bundles; without it, all matching packs are included")
    args = parser.parse_args()
    if not (args.source_assets / "packs.txt").is_file():
        parser.error(f"packs.txt not found under {args.source_assets}")
    count = stage(
        args.source_assets,
        args.bundle_root,
        args.android_assets,
        native_unity_version=args.native_unity_version,
        native_packs=set(args.native_pack),
    )
    print(f"Staged packs.txt, {count} matching pack TOCs, and their bundles into Android APK assets")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
