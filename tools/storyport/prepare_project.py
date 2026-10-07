#!/usr/bin/env python3
"""Create the local Unity workspace from tracked StoryPort sources + local 9.2 art."""
from __future__ import annotations

import argparse
import json
import re
import shutil
from pathlib import Path

from PIL import Image


VERSION = "6000.6.3f1"
EXCLUDED = {"Scripts", "Plugins"}  # Never import decompiled game code or APK plugins.
ARENA_LEVELS = ("chicago", "hongkong", "karnak", "mine", "rust")
ARENA_TODS = (0, 1, 2)
# Unity-internal GUIDs: default resources, plus the built-in extra (f) and editor (e) resources.
BUILTIN_GUIDS = {
    "0000000deadbeef15deadf00d0000000",
    "0000000000000000e000000000000000",
    "0000000000000000f000000000000000",
}
NAV_FONT_FALLBACK = Path(
    "build/assetripper/exports/recompilation-2020/ExportedProject/Assets/Resources/ui/fonts/ttf/Tecnica_Bold_116.ttf"
)


def extract_atlas_sprites(atlas_image: Path, atlas_data: Path, destination: Path,
                          wanted: dict[str, str]) -> None:
    """Crop named 9.2 UI sprites, failing before a build if the atlas is incomplete."""
    if not atlas_image.is_file() or not atlas_data.is_file():
        raise FileNotFoundError(f"Missing local 9.2 UI atlas: {atlas_image} or {atlas_data}")
    source_text = atlas_data.read_text(encoding="utf-8", errors="replace")
    found: set[str] = set()
    with Image.open(atlas_image) as atlas:
        for match in re.finditer(r"^  - name: ([^\n]+)\n(.*?)(?=^  - name: |\Z)", source_text, re.M | re.S):
            name, block = match.group(1).strip(), match.group(2)
            if name not in wanted:
                continue
            values = {}
            for key in ("x", "y", "width", "height"):
                value = re.search(rf"^    {key}: (-?\d+)$", block, re.M)
                if value is None:
                    raise ValueError(f"Sprite {name!r} is missing its {key} coordinate")
                values[key] = int(value.group(1))
            x, y, width, height = (values[key] for key in ("x", "y", "width", "height"))
            if x < 0 or y < 0 or width <= 0 or height <= 0 or x + width > atlas.width or y + height > atlas.height:
                raise ValueError(f"Sprite {name!r} is outside the {atlas.size} atlas")
            atlas.crop((x, y, x + width, y + height)).save(destination / wanted[name])
            found.add(name)
    missing = set(wanted) - found
    if missing:
        raise ValueError("Missing named 9.2 UI sprites: " + ", ".join(sorted(missing)))


def find_nav_font(converted_project: Path, explicit: Path | None,
                  fallback: Path = NAV_FONT_FALLBACK) -> Path:
    """Locate the local 9.2 icon font without putting it in the repository."""
    if explicit is not None:
        if explicit.is_file():
            return explicit
        raise FileNotFoundError(f"Navigation font does not exist: {explicit}")
    converted = converted_project / "Assets/Resources/ui/fonts/ttf/Tecnica_Bold_116.ttf"
    for candidate in (converted, fallback):
        if candidate.is_file():
            return candidate
    raise FileNotFoundError("Missing local 9.2 navigation font; pass --nav-font /path/to/Tecnica_Bold_116.ttf")


def copy_localization_catalogs(source: Path, target: Path) -> None:
    """Replace prepared catalogs from tracked source, removing stale locales."""
    if target.exists():
        shutil.rmtree(target)
    if source.is_dir():
        shutil.copytree(source, target)


def guid_set(root: Path) -> set[str]:
    """Collect the Unity asset GUIDs declared by every .meta file under a folder."""
    return {
        match.group(1)
        for meta in root.rglob("*.meta")
        if (match := re.search(r"^guid: ([0-9a-f]{32})$", meta.read_text(errors="ignore"), re.M))
    }


def arena_scene_prefab(root: Path, level: str, tod: int) -> Path:
    """Locate one converted arena time-of-day prefab (sky and sun only) in an Assets folder."""
    return root / "bundles/scenes" / f"{level}_merged" / f"{level}_timeofday_{tod}_forward.prefab"


def arena_stage_prefab(root: Path, level: str) -> Path:
    """Locate the converted arena geometry prefab shared by every time of day."""
    return root / "bundles/scenes" / f"{level}_merged" / f"{level}_merged.prefab"


def arena_sky_texture(root: Path, level: str, tod: int) -> Path | None:
    """Resolve the base sky texture the time-of-day prefab's Sky renderer uses."""
    by_guid: dict[str, Path] = {}
    for meta in root.rglob("*.meta"):
        match = re.search(r"^guid: ([0-9a-f]{32})$", meta.read_text(errors="ignore"), re.M)
        if match:
            by_guid[match.group(1)] = meta.with_suffix("")
    prefab = arena_scene_prefab(root, level, tod).read_text(errors="ignore")
    for component in re.split(r"(?m)(?=^--- !u!)", prefab):
        if not component.startswith("--- !u!23 "):
            continue
        material = re.search(r"m_Materials:\n\s+- \{fileID: \d+, guid: ([0-9a-f]{32})", component)
        material_path = by_guid.get(material.group(1)) if material else None
        if material_path is None or not material_path.is_file():
            continue
        texture = re.search(r"_base_tex:\n\s+m_Texture: \{fileID: \d+, guid: ([0-9a-f]{32})", material_path.read_text(errors="ignore"))
        if texture and by_guid.get(texture.group(1)) is not None:
            return by_guid[texture.group(1)]
    return None


def import_arena_conversion(arena_roots: list[Path], level: str, imported_assets: Path) -> Path | None:
    """Import a standalone per-level conversion that the main converted project lacks.

    AssetRipper assigns GUIDs per run, so a separate conversion is self-contained and
    stays in its own folder. Returns the folder holding its prefabs, or None.
    """
    for root in arena_roots:
        assets = root / f"story-arena-conversion-{level}/ExportedProject/Assets"
        if not arena_stage_prefab(assets, level).is_file():
            continue
        target = imported_assets / "Arenas" / level
        if target.exists():
            shutil.rmtree(target)
        shutil.copytree(assets, target, ignore=lambda _directory, names: EXCLUDED.intersection(names))
        return target
    return None


def copy_arena_prefabs(
    converted_project: Path, unity_assets: Path, imported_assets: Path, arena_roots: list[Path] | None = None
) -> None:
    """Create Resources aliases for each arena's geometry and per-time-of-day sky texture.

    The `<level>_timeofday_<n>_forward` prefabs hold only a sky dome, sun and lens flare;
    the street geometry lives in `<level>_merged`. Reject incomplete conversions.
    """
    resources = unity_assets / "Resources/StoryPort/Arenas"
    if resources.exists():
        shutil.rmtree(resources)
    resources.mkdir(parents=True, exist_ok=True)
    missing = []
    unresolved = []
    sources: dict[str, Path] = {}
    for level in ARENA_LEVELS:
        main_assets = converted_project / "Assets"
        if arena_stage_prefab(main_assets, level).is_file() and all(
            arena_scene_prefab(main_assets, level, tod).is_file() for tod in ARENA_TODS
        ):
            sources[level] = main_assets
        else:
            standalone = import_arena_conversion(arena_roots or [], level, imported_assets)
            if standalone is not None:
                sources[level] = standalone
    imported_guids = guid_set(imported_assets)
    for level in ARENA_LEVELS:
        if level not in sources:
            missing.append(level)
            continue
        stage = arena_stage_prefab(sources[level], level)
        stage_text = re.sub(r"^\s*m_Script: .*$", "", stage.read_text(errors="ignore"), flags=re.M)
        absent = sorted(set(re.findall(r"\bguid: ([0-9a-f]{32})\b", stage_text)) - {"0" * 32} - BUILTIN_GUIDS - imported_guids)
        if absent:
            unresolved.append(f"{level} stage: {', '.join(absent)}")
        else:
            shutil.copy2(stage, resources / f"{level}_stage.prefab")
        for tod in ARENA_TODS:
            sky = arena_sky_texture(sources[level], level, tod) if arena_scene_prefab(sources[level], level, tod).is_file() else None
            if sky is None or not sky.is_file():
                missing.append(f"{level}/TOD {tod} sky")
                continue
            shutil.copy2(sky, resources / f"{level}_sky_{tod}{sky.suffix}")
    if missing:
        raise FileNotFoundError(
            "Missing converted arena scenes required for Story battles: "
            + "; ".join(missing)
            + ". Convert the corresponding 9.2 scene bundles (see --arena-conversions) before preparing StoryPort."
        )
    if unresolved:
        raise RuntimeError("Converted Story arena prefabs have unresolved dependencies: " + "; ".join(unresolved))


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--converted-project", type=Path, required=True)
    parser.add_argument("--project", type=Path, required=True)
    parser.add_argument("--source", type=Path, default=Path("client/StoryPort"))
    parser.add_argument(
        "--assetpack-root",
        type=Path,
        default=Path("build/pristine-rebuild/tree/assets/assetpack"),
        help="Local extracted 9.2 assetpack root; copied artwork is not tracked.",
    )
    parser.add_argument(
        "--arena-conversions",
        type=Path,
        action="append",
        default=[Path("build")],
        help="Folder holding story-arena-conversion-<level>/ExportedProject for arena levels "
        "missing from the converted project. May be repeated.",
    )
    parser.add_argument(
        "--nav-font", type=Path,
        help="Local extracted 9.2 Tecnica_Bold_116.ttf, when absent from the converted project.",
    )
    args = parser.parse_args()

    unity_assets = args.project / "Assets"
    imported = unity_assets / "Art92"
    imported.parent.mkdir(parents=True, exist_ok=True)
    if imported.exists():
        shutil.rmtree(imported)

    def ignore(directory: str, names: list[str]) -> set[str]:
        return EXCLUDED.intersection(names)

    shutil.copytree(args.converted_project / "Assets", imported, ignore=ignore)

    # The lightweight scene shell used during the first client pass contained
    # only lights and a skybox. Use the original converted Chicago environment
    # prefab so the fight and quest route render on the actual game geometry.
    chicago_scene = (
        args.converted_project
        / "Assets/bundles/scenes/chicago_merged/chicago_merged.prefab"
    )
    if chicago_scene.is_file():
        imported_guids = {
            match.group(1)
            for meta in imported.rglob("*.meta")
            if (match := re.search(r"^guid: ([0-9a-f]{32})$", meta.read_text(errors="ignore"), re.M))
        }
        scene_text = chicago_scene.read_text(errors="ignore")
        mesh_guids = set()
        for component in re.split(r"(?m)(?=^--- !u!)", scene_text):
            if not component.startswith("--- !u!33 "):
                continue
            match = re.search(r"^  m_Mesh: \{fileID: (\d+), guid: ([0-9a-f]{32}), type: \d+\}$", component, re.M)
            if match and match.group(1) != "0":
                mesh_guids.add(match.group(2))
        missing_mesh_guids = sorted(mesh_guids - imported_guids)
        if missing_mesh_guids:
            raise RuntimeError(
                "Chicago scene references mesh assets absent from the converted project: "
                + ", ".join(missing_mesh_guids)
                + ". Use a full conversion that includes the Chicago meshes."
            )
        chicago_resource = (
            unity_assets / "Resources/StoryPort/ChicagoFightStage.prefab"
        )
        chicago_resource.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(chicago_scene, chicago_resource)
    else:
        stale_scene = unity_assets / "Resources/StoryPort/ChicagoFightStage.prefab"
        stale_scene.unlink(missing_ok=True)
        stale_scene.with_suffix(stale_scene.suffix + ".meta").unlink(missing_ok=True)

    copy_arena_prefabs(args.converted_project, unity_assets, imported, args.arena_conversions)

    # Small original menu, loading, story-card, and quest portrait images are
    # not stored in the Unity bundles. Keep them local in Resources so the clean
    # client uses 9.2 artwork without importing APK code.
    raw_art = args.project / "Assets" / "Resources" / "StoryPort"
    ui_art = raw_art / "UI"
    portrait_art = raw_art / "Portraits"
    for folder in (ui_art, portrait_art):
        if folder.exists():
            shutil.rmtree(folder)
        folder.mkdir(parents=True, exist_ok=True)
    nav_font = find_nav_font(args.converted_project, args.nav_font)
    font_art = raw_art / "Fonts"
    if font_art.exists():
        shutil.rmtree(font_art)
    font_art.mkdir(parents=True, exist_ok=True)
    shutil.copy2(nav_font, font_art / "tecnica_nav.ttf")
    base_sky = args.converted_project / "Assets/Texture2D/qb_primordial_sky_red.png"
    if not base_sky.is_file():
        raise FileNotFoundError(f"Missing local 9.2 Primordial sky texture: {base_sky}")
    shutil.copy2(base_sky, raw_art / "BaseSky.png")
    art_copies = {
        args.assetpack_root / "ui/titles/tff_logo_en.png": ui_art / "tff_logo_en.png",
        args.assetpack_root / "ui/titles/title_background.jpg": ui_art / "title_background.jpg",
        args.assetpack_root / "ui/loading/planet_landscape.jpg": ui_art / "planet_landscape.jpg",
        args.assetpack_root / "ui/loading/bot_roster.png": ui_art / "bot_roster.png",
        args.assetpack_root / "ui/loading/starscream_fight.jpg": ui_art / "starscream_fight.jpg",
        args.assetpack_root / "fightlanding_odr/fightlanding/fightstoryimglrg_hd.jpg": ui_art / "fightstoryimglrg_hd.jpg",
        args.assetpack_root / "fightlanding_odr/fightlanding/fightstoryimglrg_sd.jpg": ui_art / "fightstoryimglrg_sd.jpg",
        args.assetpack_root / "fightlanding_odr/fightlanding/fightstoryimgsml_hd.jpg": ui_art / "fightstoryimgsml_hd.jpg",
        args.assetpack_root / "fightlanding_odr/fightlanding/fightraidsimg_hd.png": ui_art / "fightraidsimg_hd.png",
        args.assetpack_root / "fightlanding_odr/fightlanding/fightallianceimg_hd.jpg": ui_art / "fightallianceimg_hd.jpg",
        args.assetpack_root / "fightlanding_odr/fightlanding/fighteventimglrg_hd.png": ui_art / "fighteventimglrg_hd.png",
        args.assetpack_root / "fightlanding_odr/fightlanding/fightl_dailymission_hd.png": ui_art / "fightl_dailymission_hd.png",
        args.assetpack_root / "fightlanding_odr/fightlanding/fightversusimg_hd.png": ui_art / "fightversusimg_hd.png",
        args.assetpack_root / "questboard_odr/questboard/background_button.png": ui_art / "background_button.png",
        args.assetpack_root / "questboard_odr/questboard/background_timer.png": ui_art / "background_timer.png",
        args.assetpack_root / "questboard_odr/questboard/bosscard_frame_playermarker.png": ui_art / "bosscard_frame_playermarker.png",
        args.assetpack_root / "questboard_odr/questboard/playermarker_notifbg.png": ui_art / "playermarker_notifbg.png",
    }
    for index in range(7):
        source = args.assetpack_root / "questboard_odr" / "questboard" / f"bosscard_frame{index}.png"
        art_copies[source] = ui_art / source.name
    for source, destination in art_copies.items():
        if source.is_file():
            shutil.copy2(source, destination)
    # Preserve the game-authored global navigation artwork from its atlas.
    # Sprite coordinates in MainUIAtlasHdPrefab are top-left based.
    atlas_image = args.assetpack_root / "ui/bundles/uitextures/fte/atlases/mainuiatlashdprefab.png"
    atlas_data = args.converted_project / "Assets/GameObject/MainUIAtlasHdPrefab.prefab"
    wanted = {
            "Button_MainGlowing": "button_main_glowing.png",
            "Button_Tab": "button_tab.png",
            "Button_TabActive": "button_tab_active.png",
            "frame_button": "frame_button.png",
            "GlobalNavButton_Main": "global_nav_button.png",
            "GlobalNavButton_MainActive": "global_nav_button_active.png",
            "GlobalNavButton_Center": "global_nav_center.png",
            "GlobalNavButton_CenterActive": "global_nav_center_active.png",
            "frame_hud_portrait_rarity_1": "frame_hud_portrait.png",
            "hexagon_border_HD": "hexagon_border.png",
            "hexagon_progress": "hexagon_progress.png",
            "boss_icon": "boss_icon.png",
            "energy_AvE": "energy_ave.png",
            "energy_PvE": "energy_pve.png",
            "hard-currency": "hard_currency.png",
            "soft-currency": "soft_currency.png",
            "ProgressBarFill": "progress_bar_fill.png",
            "ProgressBarFill_gray": "progress_bar_fill_gray.png",
            "SpecialFrame": "special_frame.png",
            "bookmark": "story_bookmark.png",
            "CommonFrame": "common_frame.png",
            "frame_gate": "frame_gate.png",
            "frame_Selection": "frame_selection.png",
            "HeroTile_Background": "hero_tile_background.png",
            "IconLoading": "icon_loading.png",
            "IconLock": "icon_lock.png",
            "PopUp_Background": "popup_background.png",
            "SelectionOutline": "selection_outline.png",
            "Teletraan_bg": "teletraan_bg.png",
        }
    extract_atlas_sprites(atlas_image, atlas_data, ui_art, wanted)
    game_portraits = args.assetpack_root / "portraits_odr" / "portraits"
    for image in game_portraits.glob("portrait_*_large.png"):
        shutil.copy2(image, portrait_art / image.name)
    for tree in ("Scripts", "Editor", "Shaders"):
        origin = args.source / "Assets" / tree
        target = unity_assets / tree
        if target.exists():
            shutil.rmtree(target)
        if origin.exists():
            shutil.copytree(origin, target)

    # Story text catalogs are tracked source, not converted art: replace the
    # prepared copy so removed languages do not linger.
    localization_source = args.source / "Assets" / "Resources" / "StoryPort" / "Localization"
    localization_target = raw_art / "Localization"
    copy_localization_catalogs(localization_source, localization_target)

    packages = args.project / "Packages"
    packages.mkdir(parents=True, exist_ok=True)
    (packages / "manifest.json").write_text(
        json.dumps(
            {
                "dependencies": {
                    "com.unity.ugui": "2.0.0",
                    "com.unity.modules.animation": "1.0.0",
                    "com.unity.modules.unitywebrequest": "1.0.0",
                }
            },
            indent=2,
        )
        + "\n"
    )
    settings = args.project / "ProjectSettings"
    settings.mkdir(parents=True, exist_ok=True)
    (settings / "ProjectVersion.txt").write_text(
        f"m_EditorVersion: {VERSION}\nm_EditorVersionWithRevision: {VERSION}\n"
    )
    print(f"Prepared Unity {VERSION} workspace: {args.project}")
    print(f"Imported art is local-only: {imported}")


if __name__ == "__main__":
    main()
