#!/usr/bin/env python3
"""Restore 9.2 ScriptableObject assets exported as GameObject prefabs.

The three listed classes have ScriptableObject base types in the local 9.2
Assembly-CSharp metadata. AssetRipper writes their serialized instances as a
one-GameObject prefab with a MonoBehaviour attachment. Convert only that
known shape to standalone ScriptableObject YAML, retaining the prefab GUID,
all authored fields, and references to the asset. Original YAML and metadata
are copied to the requested backup directory before each conversion.
"""

from __future__ import annotations

import argparse
import re
import shutil
from pathlib import Path


SCRIPTABLE_TYPES = ("AIProfile", "AIPersonality", "AITuneables")
SCRIPT_BLOCK = re.compile(
    r"(?m)^--- !u!114 &(?P<file_id>-?\d+)\nMonoBehaviour:\n"
    r"(?P<body>.*?)(?=^--- !u!|\Z)", re.S
)
SCRIPT_REFERENCE = re.compile(
    r"(?m)^  m_Script: \{fileID: 11500000, guid: (?P<guid>[0-9a-f]{32}), type: 3\}$"
)
DOCUMENT = re.compile(r"(?m)^--- !u!(?P<class_id>\d+) &(?P<file_id>-?\d+)")
TEXT_ASSET_SUFFIXES = {
    ".asset", ".prefab", ".unity", ".mat", ".anim", ".controller",
    ".overridecontroller", ".mask", ".playable", ".lighting",
}


def script_guids(assets: Path) -> dict[str, str]:
    """Validate recovered script GUIDs against the generated type stubs."""
    result: dict[str, str] = {}
    source_dir = assets / "RecoveredBindings" / "Assembly-CSharp"
    for class_name in SCRIPTABLE_TYPES:
        source = source_dir / f"{class_name}.cs"
        meta = source.with_name(source.name + ".meta")
        if not source.is_file() or not meta.is_file():
            raise FileNotFoundError(f"Missing recovered script or metadata: {source}")
        guid_match = re.search(r"(?m)^guid: ([0-9a-f]{32})$", meta.read_text(encoding="utf-8"))
        if not guid_match:
            raise ValueError(f"Missing GUID in {meta}")
        guid = guid_match.group(1)
        declaration = re.compile(
            rf"\bclass\s+{re.escape(class_name)}\s*:\s*"
            rf"RecoveredBindingBases\.ScriptBase_{guid}\b"
        )
        if not declaration.search(source.read_text(encoding="utf-8", errors="replace")):
            raise ValueError(f"Recovered script stub does not match {class_name}'s GUID: {source}")
        result[guid] = class_name
    return result


def convert(assets: Path, backup_root: Path) -> int:
    by_guid = script_guids(assets)
    resources = assets / "Resources"
    converted: list[tuple[Path, str]] = []

    for prefab in sorted(resources.rglob("*.prefab")):
        text = prefab.read_text(encoding="utf-8", errors="replace")
        match = SCRIPT_BLOCK.search(text)
        if not match:
            continue
        body = match.group("body")
        script = SCRIPT_REFERENCE.search(body)
        if not script or script.group("guid") not in by_guid:
            continue

        # These tuning/profile resources are ScriptableObjects, not components.
        # Preserve the data only when AssetRipper emitted the verified
        # one-GameObject/one-Transform/one-script wrapper shape.
        document_ids = [m.group("class_id") for m in DOCUMENT.finditer(text)]
        if sorted(document_ids) != ["1", "114", "4"]:
            raise ValueError(f"Unexpected prefab layout for ScriptableObject {prefab}: {document_ids}")
        if "  m_EditorClassIdentifier:\n" not in body:
            raise ValueError(f"Missing serialized field boundary in {prefab}")
        component_name = re.search(r"(?m)^  m_Name: (.*)$", text)
        object_name = component_name.group(1).strip() if component_name else prefab.stem
        asset = prefab.with_suffix(".asset")
        meta = prefab.with_name(prefab.name + ".meta")
        asset_meta = asset.with_name(asset.name + ".meta")
        if asset.exists() or asset_meta.exists() or not meta.is_file():
            raise RuntimeError(f"Cannot safely convert {prefab}: destination or metadata missing")
        prefab_guid = re.search(r"(?m)^guid: ([0-9a-f]{32})$", meta.read_text(encoding="utf-8"))
        if not prefab_guid:
            raise ValueError(f"Missing prefab GUID in {meta}")

        fields = body.split("  m_EditorClassIdentifier:\n", 1)[1]
        fields = re.sub(r"(?m)^  m_Name:.*\n", "", fields, count=1)
        data = (
            "%YAML 1.1\n%TAG !u! tag:unity3d.com,2011:\n"
            "--- !u!114 &11400000\nMonoBehaviour:\n"
            "  m_ObjectHideFlags: 0\n"
            "  m_CorrespondingSourceObject: {fileID: 0}\n"
            "  m_PrefabInstance: {fileID: 0}\n"
            "  m_PrefabAsset: {fileID: 0}\n"
            f"  m_Name: {object_name}\n"
            f"  m_Script: {{fileID: 11500000, guid: {script.group('guid')}, type: 3}}\n"
            "  m_EditorClassIdentifier:\n"
            + fields
        )
        relative = prefab.relative_to(assets)
        backup = backup_root / relative
        backup.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(prefab, backup)
        shutil.copy2(meta, backup.with_name(backup.name + ".meta"))
        asset.write_text(data, encoding="utf-8")
        asset_meta.write_text(
            "fileFormatVersion: 2\n"
            f"guid: {prefab_guid.group(1)}\n"
            "NativeFormatImporter:\n"
            "  externalObjects: {}\n"
            "  mainObjectFileID: 11400000\n"
            "  userData:\n"
            "  assetBundleName:\n"
            "  assetBundleVariant:\n",
            encoding="utf-8",
        )
        prefab.unlink()
        meta.unlink()
        converted.append((asset, prefab_guid.group(1)))

    # References to prefab components used their old local file IDs; the
    # standalone ScriptableObject uses Unity's canonical file ID 11400000.
    guid_set = {guid for _, guid in converted}
    for asset in resources.rglob("*.asset"):
        body = asset.read_text(encoding="utf-8", errors="replace")
        script = SCRIPT_REFERENCE.search(body)
        if not script or script.group("guid") not in by_guid:
            continue
        meta = asset.with_name(asset.name + ".meta")
        if not meta.is_file():
            continue
        asset_guid = re.search(r"(?m)^guid: ([0-9a-f]{32})$", meta.read_text(encoding="utf-8"))
        if asset_guid:
            guid_set.add(asset_guid.group(1))

    updated_files = 0
    if guid_set:
        guid_pattern = "(?:" + "|".join(re.escape(guid) for guid in sorted(guid_set)) + ")"
        pointer = re.compile(rf"fileID: -?\d+, guid: {guid_pattern}(?=, type: \d+)")
        for file in assets.rglob("*"):
            if not file.is_file() or file.suffix.lower() not in TEXT_ASSET_SUFFIXES:
                continue
            try:
                content = file.read_text(encoding="utf-8")
            except (UnicodeDecodeError, OSError):
                continue
            updated, count = pointer.subn(
                lambda match: "fileID: 11400000, guid: " + match.group(0).split("guid: ", 1)[1],
                content,
            )
            if updated != content:
                relative = file.relative_to(assets)
                reference_backup = backup_root / "_references" / relative
                if not reference_backup.exists():
                    reference_backup.parent.mkdir(parents=True, exist_ok=True)
                    shutil.copy2(file, reference_backup)
                file.write_text(updated, encoding="utf-8")
                updated_files += 1
    print(f"Updated ScriptableObject references in {updated_files} serialized asset file(s)")
    return len(converted)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("assets", type=Path, help="staged Unity project's Assets directory")
    parser.add_argument("backup", type=Path, help="directory for original prefab YAML and metadata")
    args = parser.parse_args()
    assets = args.assets.expanduser().resolve()
    backup = args.backup.expanduser().resolve()
    if not (assets / "Resources").is_dir():
        parser.error(f"Assets directory has no Resources folder: {assets}")
    print(f"Converted {convert(assets, backup)} 9.2 ScriptableObject prefab(s)")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
