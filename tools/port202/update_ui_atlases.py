#!/usr/bin/env python3
"""Refresh legacy UI atlas rectangles from a loaded 9.2 Unity APK.

AssetRipper must have the 9.2 APK loaded in its local web UI. The script reads
only the serialized atlas maps through AssetRipper's read-only endpoints and
updates the matching generated Unity project prefabs under build/.
"""

from __future__ import annotations

import argparse
import json
import re
import struct
import urllib.parse
import urllib.request
from html.parser import HTMLParser
from pathlib import Path


ATLAS_NAMES = (
    "MainUIAtlasHdPrefab",
    "MainUIAtlasSdPrefab",
    "HudUIAtlasHdPrefab",
    "HudUIAtlasSdPrefab",
    "LoginUIAtlasHdPrefab",
    "LoginUIAtlasSdPrefab",
)

# 2.0.2 UI prefabs refer to a handful of sprite names that were renamed or
# moved in 9.2. These aliases point at the closest matching 9.2 art; aliases
# whose art lives in another atlas are routed to that atlas below.
SPRITE_ALIASES = {
    "MainUIAtlasHdPrefab": {
        "CommonHighlight": "PopUp_highlight",
        "CommonSquareGlow": "Hero_tile_glow",
        "CommonSquareOverlay": "CommonSquare",
        "HeroTile_AwakenedOverlay": "HeroTile_BackgroundAwakened",
        "HeroTile_Flip": "HeroTile_Background",
        "LoadingCircle": "IconLoading",
        "SRButtonFrame": "frame_button",
        "arrow": "FTE_arrow",
        "iconStoryBookmark": "bookmark",
        "iconStoryLock": "IconLock",
        "iconWarningSign": "SystemMessage_Warning",
    },
    "HudUIAtlasHdPrefab": {
        "BuffMeter_AreaFrame": "BuffMeter_TowerFrame",
        "icon_special_attack_level_1": "SpecialMeter_Icon",
        "special_attribute_meter_fill": "special_meter_segment",
    },
}

ATLAS_REF_TO_ASSET = {
    "MainUIAtlasRef": "MainUIAtlasHdPrefab",
    "HudUIAtlasRef": "HudUIAtlasHdPrefab",
    "LoginUIAtlasRef": "LoginUIAtlasHdPrefab",
    "MasteriesUIAtlasRef": "MasteriesUIAtlasHdPrefab",
}


class _SearchResults(HTMLParser):
    def __init__(self) -> None:
        super().__init__()
        self.rows: list[dict] = []
        self._row: dict | None = None
        self._cell = False
        self._cell_text = ""
        self._anchor: str | None = None
        self._anchor_text = ""

    def handle_starttag(self, tag: str, attrs: list[tuple[str, str | None]]) -> None:
        values = dict(attrs)
        if tag == "tr":
            self._row = {"class": values.get("data-class"), "links": []}
        elif self._row is not None and tag == "td":
            self._cell = True
            self._cell_text = ""
        elif self._row is not None and tag == "a":
            self._anchor = values.get("href") or ""
            self._anchor_text = ""

    def handle_data(self, data: str) -> None:
        if self._row is None:
            return
        if self._cell:
            self._cell_text += data
        if self._anchor is not None:
            self._anchor_text += data

    def handle_endtag(self, tag: str) -> None:
        if tag == "a" and self._row is not None and self._anchor is not None:
            self._row["links"].append((self._anchor_text.strip(), self._anchor))
            self._anchor = None
        elif tag == "td" and self._row is not None and self._cell:
            text = self._cell_text.strip()
            if not any(existing == text for existing, _ in self._row["links"]):
                self._row["links"].append((text, ""))
            self._cell = False
        elif tag == "tr" and self._row is not None:
            self.rows.append(self._row)
            self._row = None


def _get(url: str) -> str:
    request = urllib.request.Request(url, headers={"User-Agent": "TFTF-UI-atlas-converter/1.0"})
    with urllib.request.urlopen(request, timeout=90) as response:
        return response.read().decode("utf-8")


def _yaml_url(base_url: str, path: dict) -> str:
    encoded = urllib.parse.urlencode({"Path": json.dumps(path, separators=(",", ":"))})
    return urllib.parse.urljoin(base_url.rstrip("/") + "/", "Assets/Yaml?" + encoded)


def _component_ids(game_object_yaml: str) -> list[int]:
    return [
        int(value)
        for value in re.findall(
            r"^  - component: \{m_FileID: 0, m_PathID: (-?\d+), m_TargetClassID: \d+\}$",
            game_object_yaml,
            re.MULTILINE,
        )
    ]


def _load_atlas_yaml(base_url: str, atlas_name: str) -> str:
    search_url = urllib.parse.urljoin(base_url.rstrip("/") + "/", "Search/View?")
    query = urllib.parse.urlencode({"q": atlas_name})
    parser = _SearchResults()
    parser.feed(_get(search_url + query))
    rows = [
        row for row in parser.rows
        if row["class"] == "GameObject"
        and any(text == atlas_name and href.startswith("/Assets/View?") for text, href in row["links"])
    ]
    if not rows:
        raise RuntimeError(f"AssetRipper search did not find GameObject {atlas_name!r}")

    # The first results are usually the serialized Resources.assets object;
    # try all duplicates and select the MonoBehaviour with the expected atlas.
    for row in rows:
        href = next(href for text, href in row["links"] if text == atlas_name and href)
        query_values = urllib.parse.parse_qs(urllib.parse.urlparse(href).query)
        object_path = json.loads(query_values["Path"][0])
        collection = object_path["C"]
        game_yaml = _get(_yaml_url(base_url, object_path))
        for component_id in _component_ids(game_yaml):
            component_path = {"C": collection, "D": component_id}
            component_yaml = _get(_yaml_url(base_url, component_path))
            if "mSprites:" not in component_yaml:
                continue
            texture_path = re.search(r"^  TexturePath: (.+)$", component_yaml, re.MULTILINE)
            if texture_path is not None and atlas_name.rsplit("Prefab", 1)[0].lower() in texture_path.group(1).lower():
                return component_yaml
    raise RuntimeError(f"No serialized atlas map found for {atlas_name!r}")


def _sprite_names(yaml_text: str) -> list[str]:
    start = yaml_text.find("  mSprites:\n")
    end = yaml_text.find("  TexturePath:", start)
    if start < 0 or end < 0:
        raise ValueError("Atlas YAML is missing mSprites or TexturePath")
    return re.findall(r"^  - name: (.+)$", yaml_text[start:end], re.MULTILINE)


def _texture_dimensions(path: Path) -> tuple[int, int]:
    with path.open("rb") as texture:
        header = texture.read(24)
    if header[:8] != b"\x89PNG\r\n\x1a\n":
        raise ValueError(f"Expected a PNG atlas texture: {path}")
    return struct.unpack(">II", header[16:24])


def _texture_file(yaml_text: str, texture_dir: Path) -> Path:
    match = re.search(r"^  TexturePath: (.+)$", yaml_text, re.MULTILINE)
    if match is None:
        raise ValueError("Atlas YAML is missing TexturePath")
    expected_stem = Path(match.group(1)).name.lower()
    for candidate in texture_dir.glob("*.png"):
        if candidate.stem.lower() == expected_stem:
            return candidate
    raise FileNotFoundError(f"No PNG atlas texture named {expected_stem!r} under {texture_dir}")


def _validate_bounds(yaml_text: str, texture_path: Path) -> None:
    width, height = _texture_dimensions(texture_path)
    sprite_block = yaml_text[yaml_text.index("  mSprites:\n"):yaml_text.index("  TexturePath:")]
    names = _sprite_names(yaml_text)
    if len(names) != len(set(names)):
        raise ValueError(f"Duplicate sprite names in {texture_path.name}")
    for match in re.finditer(r"^  - name: (.+?)\n(.*?)(?=^  - name: |\Z)", sprite_block, re.MULTILINE | re.DOTALL):
        name, block = match.groups()
        rect = {key: int(value) for key, value in re.findall(r"^    (x|y|width|height): (-?\d+)$", block, re.MULTILINE)}
        if len(rect) != 4:
            raise ValueError(f"Incomplete sprite bounds for {name!r}")
        if rect["x"] < 0 or rect["y"] < 0 or rect["x"] + rect["width"] > width or rect["y"] + rect["height"] > height:
            raise ValueError(f"Sprite {name!r} exceeds {width}x{height} texture {texture_path}")


def _append_aliases(yaml_text: str, aliases: dict[str, str]) -> str:
    """Add legacy sprite names using the bounds of a matching 9.2 sprite."""
    start = yaml_text.find("  mSprites:\n")
    end = yaml_text.find("  TexturePath:", start)
    if start < 0 or end < 0:
        raise ValueError("Atlas serialization is missing mSprites or TexturePath")
    block = yaml_text[start:end]
    existing = set(_sprite_names(yaml_text))
    source_blocks = {
        match.group(1): match.group(0)
        for match in re.finditer(
            r"^  - name: (.+?)\n.*?(?=^  - name: |\Z)",
            block,
            re.MULTILINE | re.DOTALL,
        )
    }
    additions = []
    for alias, source in aliases.items():
        if alias in existing:
            continue
        if source not in source_blocks:
            raise ValueError(f"Cannot create alias {alias!r}: source sprite {source!r} is absent")
        clone = re.sub(r"^  - name: .+$", f"  - name: {alias}", source_blocks[source], count=1, flags=re.MULTILINE)
        additions.append(clone)
    if not additions:
        return yaml_text
    return yaml_text[:end] + "".join(additions) + yaml_text[end:]


def _route_renamed_sprites(project: Path, atlas_names: dict[str, set[str]]) -> int:
    """Point legacy UI components at a 9.2 atlas when a sprite moved there."""
    atlas_dir = project / "Assets/Resources/ui/atlases"
    ref_guids = {}
    asset_to_ref = {}
    for ref_name, hd_asset in ATLAS_REF_TO_ASSET.items():
        meta = atlas_dir / f"{ref_name}.prefab.meta"
        if not meta.exists():
            continue
        match = re.search(r"^guid: (\w+)$", meta.read_text(encoding="utf-8"), re.MULTILINE)
        if match:
            ref_guids[match.group(1)] = ref_name
            asset_to_ref[hd_asset] = ref_name

    # A moved sprite is aliased in its destination atlas, then this rewrites
    # only the specific UI component that requests the old sprite name.
    destination = {}
    for asset, aliases in SPRITE_ALIASES.items():
        for alias in aliases:
            destination[alias] = asset
    destination["iconNavAudioOn"] = "HudUIAtlasHdPrefab"
    moved = 0
    for prefab_path in (project / "Assets/Resources").rglob("*.prefab"):
        text = prefab_path.read_text(encoding="utf-8", errors="replace")
        changed = False
        docs = re.split(r"(?=^--- !u!)", text, flags=re.MULTILINE)
        for index, doc in enumerate(docs):
            atlas_match = re.search(r"^  mAtlas: \{fileID: (-?\d+), guid: (\w+), type: (\d+)\}$", doc, re.MULTILINE)
            sprite_match = re.search(r"^  mSpriteName: (.*)$", doc, re.MULTILINE)
            if not atlas_match or not sprite_match:
                continue
            current_ref = ref_guids.get(atlas_match.group(2))
            if current_ref is None:
                continue
            current_asset = ATLAS_REF_TO_ASSET.get(current_ref)
            sprite_name = sprite_match.group(1).strip()
            if current_asset is None or sprite_name in atlas_names.get(current_asset, set()):
                continue
            target_asset = destination.get(sprite_name)
            target_ref = asset_to_ref.get(target_asset or "")
            if target_asset is None or target_ref is None or sprite_name not in atlas_names.get(target_asset, set()):
                continue
            target_guid = next(guid for guid, ref in ref_guids.items() if ref == target_ref)
            docs[index] = doc[:atlas_match.start(2)] + target_guid + doc[atlas_match.end(2):]
            changed = True
            moved += 1
        if changed:
            prefab_path.write_text("".join(docs), encoding="utf-8")
    return moved


def update(project: Path, asset_ripper_url: str) -> int:
    atlas_dir = project / "Assets/Resources/ui/atlases"
    texture_dir = project / "Assets/Plugins/Android/assets/ui/bundles/uitextures/fte/atlases"
    changed = 0
    imported_names: dict[str, set[str]] = {}
    for atlas_name in ATLAS_NAMES:
        prefab_path = atlas_dir / f"{atlas_name}.prefab"
        prefab = prefab_path.read_text(encoding="utf-8")
        new_yaml = _load_atlas_yaml(asset_ripper_url, atlas_name)
        aliases = SPRITE_ALIASES.get(atlas_name.replace("SdPrefab", "HdPrefab"), {})
        new_yaml = _append_aliases(new_yaml, aliases)
        _validate_bounds(new_yaml, _texture_file(new_yaml, texture_dir))
        old_names = set(_sprite_names(prefab))
        new_names = set(_sprite_names(new_yaml))
        imported_names[atlas_name] = new_names
        missing = sorted(old_names - new_names)
        start = prefab.find("  mSprites:\n")
        end = prefab.find("  TexturePath:", start)
        new_start = new_yaml.find("  mSprites:\n")
        new_end = new_yaml.find("  TexturePath:", new_start)
        if min(start, end, new_start, new_end) < 0:
            raise ValueError(f"Atlas serialization changed for {atlas_name}")
        rewritten = prefab[:start] + new_yaml[new_start:new_end] + prefab[end:]
        if rewritten != prefab:
            prefab_path.write_text(rewritten, encoding="utf-8")
            changed += 1
        print(
            f"{atlas_name}: {len(new_names)} current sprites, "
            f"{len(old_names & new_names)} of {len(old_names)} legacy names retained"
            + (f"; missing: {', '.join(missing)}" if missing else "")
        )
    for base, aliases in SPRITE_ALIASES.items():
        for variant in (base, base.replace("HdPrefab", "SdPrefab")):
            if variant not in imported_names:
                continue
            new_names = imported_names[variant]
            missing_sources = sorted(source for source in aliases.values() if source not in new_names)
            if missing_sources:
                raise ValueError(f"{variant} is missing alias source sprites: {', '.join(missing_sources)}")
    moved = _route_renamed_sprites(project, imported_names)
    print(f"Routed {moved} renamed sprite references to their 9.2 atlas")
    return changed


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--project", type=Path, required=True, help="generated Unity 2020 project")
    parser.add_argument("--asset-ripper", default="http://127.0.0.1:19228", help="local AssetRipper web UI base URL")
    args = parser.parse_args()
    changed = update(args.project, args.asset_ripper)
    print(f"Updated {changed} UI atlas prefabs from 9.2 metadata")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
