#!/usr/bin/env python3
"""Restore ScriptableObject assets exported by AssetRipper as GameObject prefabs.

This targets the 2.0.2 AI tuning assets whose managed classes derive from
ScriptableObject but whose recovered YAML incorrectly wraps them in a GameObject.
It retains each asset GUID and serialized field data, updates local object file
IDs, and backs up the source YAML before replacing it.
"""

from __future__ import annotations

import argparse
import re
import shutil
from pathlib import Path


SCRIPTABLE_TYPES = ("AIProfile", "AIPersonality", "AITuneables")


def guid_for_script(assets: Path, class_name: str) -> str:
    source = assets / "Scripts" / "Assembly-CSharp" / f"{class_name}.cs"
    metadata = source.with_name(source.name + ".meta")
    if not source.is_file() or not metadata.is_file():
        raise FileNotFoundError(f"Missing source or metadata for {class_name}: {source}")
    code = source.read_text(encoding="utf-8", errors="replace")
    if not re.search(rf"\bclass\s+{re.escape(class_name)}\s*:\s*ScriptableObject\b", code):
        raise ValueError(f"{class_name} no longer derives from ScriptableObject")
    match = re.search(r"(?m)^guid: ([0-9a-f]{32})$", metadata.read_text(encoding="utf-8"))
    if not match:
        raise ValueError(f"No GUID in {metadata}")
    return match.group(1)


def convert(assets: Path, backup_root: Path) -> int:
    by_guid = {guid_for_script(assets, name): name for name in SCRIPTABLE_TYPES}
    converted: list[tuple[Path, Path, str]] = []

    for prefab in sorted((assets / "Resources").rglob("*.prefab")):
        text = prefab.read_text(encoding="utf-8", errors="replace")
        block_match = re.search(
            r"(?m)^--- !u!114 &(?P<file_id>-?\d+)\nMonoBehaviour:\n(?P<body>.*?)(?=^--- !u!|\Z)",
            text,
            re.S,
        )
        if not block_match:
            continue
        body = block_match.group("body")
        script = re.search(r"(?m)^  m_Script: \{fileID: 11500000, guid: ([0-9a-f]{32}), type: 3\}$", body)
        if not script or script.group(1) not in by_guid:
            continue
        name_match = re.search(r"(?m)^  m_Name: (.*)$", text)
        object_name = name_match.group(1).strip() if name_match else prefab.stem
        asset = prefab.with_suffix(".asset")
        meta = prefab.with_name(prefab.name + ".meta")
        asset_meta = asset.with_name(asset.name + ".meta")
        if asset.exists() or not meta.is_file():
            raise RuntimeError(f"Cannot safely convert {prefab}: destination or metadata missing")
        asset_guid_match = re.search(r"(?m)^guid: ([0-9a-f]{32})$", meta.read_text(encoding="utf-8"))
        if not asset_guid_match:
            raise ValueError(f"No asset GUID in {meta}")

        custom_fields = body.split("  m_EditorClassIdentifier:\n", 1)
        if len(custom_fields) != 2:
            raise ValueError(f"Missing serialized script fields in {prefab}")
        payload = custom_fields[1]
        # The component's script reference and application data are preserved;
        # only fields belonging to its GameObject attachment are discarded.
        payload = re.sub(r"(?m)^  m_Name:.*\n", "", payload, count=1)
        data = (
            "%YAML 1.1\n%TAG !u! tag:unity3d.com,2011:\n"
            "--- !u!114 &11400000\nMonoBehaviour:\n"
            "  m_ObjectHideFlags: 0\n"
            "  m_CorrespondingSourceObject: {fileID: 0}\n"
            "  m_PrefabInstance: {fileID: 0}\n"
            "  m_PrefabAsset: {fileID: 0}\n"
            f"  m_Name: {object_name}\n"
            f"  m_Script: {{fileID: 11500000, guid: {script.group(1)}, type: 3}}\n"
            "  m_EditorClassIdentifier:\n"
            + payload
        )
        relative = prefab.relative_to(assets)
        backup = backup_root / relative
        backup.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(prefab, backup)
        shutil.copy2(meta, backup.with_name(backup.name + ".meta"))
        asset.write_text(data, encoding="utf-8")
        importer_meta = (
            "fileFormatVersion: 2\n"
            f"guid: {asset_guid_match.group(1)}\n"
            "NativeFormatImporter:\n"
            "  externalObjects: {}\n"
            "  mainObjectFileID: 11400000\n"
            "  userData:\n"
            "  assetBundleName:\n"
            "  assetBundleVariant:\n"
        )
        asset_meta.write_text(importer_meta, encoding="utf-8")
        prefab.unlink()
        meta.unlink()
        converted.append((asset, asset, asset_guid_match.group(1)))

    # A prefab component reference used the recovered local file ID. Once its
    # object is a standalone ScriptableObject asset, Unity's canonical ID is
    # 11400000. Preserve external GUID references while updating that ID.
    guids = {guid for _, _, guid in converted}
    for asset in (assets / "Resources").rglob("*.asset"):
        content = asset.read_text(encoding="utf-8", errors="replace")
        script = re.search(r"(?m)^  m_Script: \{fileID: 11500000, guid: ([0-9a-f]{32}), type: 3\}$", content)
        if not script or script.group(1) not in by_guid:
            continue
        meta = asset.with_name(asset.name + ".meta")
        asset_guid = re.search(r"(?m)^guid: ([0-9a-f]{32})$", meta.read_text(encoding="utf-8"))
        if asset_guid:
            guids.add(asset_guid.group(1))
    if guids:
        for file in assets.rglob("*"):
            if not file.is_file() or file.suffix in {".meta", ".resS", ".resource", ".dll", ".assetbundle"}:
                continue
            try:
                content = file.read_text(encoding="utf-8")
            except (UnicodeDecodeError, OSError):
                continue
            updated = content
            for guid in guids:
                updated = re.sub(
                    rf"fileID: -?\d+, guid: {guid}(?=, type: \d+)",
                    f"fileID: 11400000, guid: {guid}",
                    updated,
                )
            if updated != content:
                file.write_text(updated, encoding="utf-8")
    return len(converted)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("assets", type=Path, help="Unity project Assets directory")
    parser.add_argument("backup", type=Path, help="directory for original prefab YAML and metadata")
    args = parser.parse_args()
    print(f"Converted {convert(args.assets, args.backup)} ScriptableObject prefabs")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
