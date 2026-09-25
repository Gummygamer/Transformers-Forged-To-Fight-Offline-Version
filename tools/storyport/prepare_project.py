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
    art_copies = {
        args.assetpack_root / "ui/titles/tff_logo_en.png": ui_art / "tff_logo_en.png",
        args.assetpack_root / "ui/titles/title_background.jpg": ui_art / "title_background.jpg",
        args.assetpack_root / "ui/loading/planet_landscape.jpg": ui_art / "planet_landscape.jpg",
        args.assetpack_root / "ui/loading/bot_roster.png": ui_art / "bot_roster.png",
        args.assetpack_root / "ui/loading/starscream_fight.jpg": ui_art / "starscream_fight.jpg",
        args.assetpack_root / "fightlanding_odr/fightlanding/fightstoryimglrg_hd.jpg": ui_art / "fightstoryimglrg_hd.jpg",
        args.assetpack_root / "fightlanding_odr/fightlanding/fightstoryimglrg_sd.jpg": ui_art / "fightstoryimglrg_sd.jpg",
        args.assetpack_root / "fightlanding_odr/fightlanding/fightstoryimgsml_hd.jpg": ui_art / "fightstoryimgsml_hd.jpg",
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
    if atlas_image.is_file() and atlas_data.is_file():
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
        }
        source_text = atlas_data.read_text(encoding="utf-8", errors="replace")
        with Image.open(atlas_image) as atlas:
            for match in re.finditer(r"^  - name: ([^\n]+)\n(.*?)(?=^  - name: |\Z)", source_text, re.M | re.S):
                name, block = match.group(1).strip(), match.group(2)
                destination_name = wanted.get(name)
                if destination_name is None:
                    continue
                values = {}
                for key in ("x", "y", "width", "height"):
                    value = re.search(rf"^    {key}: (-?\d+)$", block, re.M)
                    if value is None:
                        break
                    values[key] = int(value.group(1))
                if len(values) != 4:
                    continue
                x, y, width, height = (values[key] for key in ("x", "y", "width", "height"))
                atlas.crop((x, y, x + width, y + height)).save(ui_art / destination_name)
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
