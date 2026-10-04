#!/usr/bin/env python3
"""Export the locally owned 9.2 music, UI and combat sounds for StoryPort.

Writes WAV files into the StoryPort Unity project's Resources so the client can
load them by name. Nothing produced here is tracked by Git.

    build/unitypy-env/bin/python tools/storyport/extract_audio.py \
        --project <UnityProject>

UI and music clips come from bin/Data/sharedassets0.assets. Combat clips come
from the character audio bundles and are grouped by sound set (agile, brawler,
brute, cinematic, feral, generations, swords, tactition) so the client loads
only the set a fighter uses.
"""
from __future__ import annotations

import argparse
import re
from pathlib import Path

import UnityPy

SOUND_SETS = ("agile", "brawler", "brute", "cinematic", "feral", "generations", "swords", "tactition")


def clip_name(name: str) -> str:
    # Some source clip names carry stray spaces ("brawler_attack_3_a ").
    return re.sub(r"\s+", "", name)


def export(sources: list[Path], destination_for) -> int:
    count = 0
    for source in sources:
        if not source.is_file():
            raise FileNotFoundError(f"Missing local 9.2 audio source: {source}")
        env = UnityPy.load(str(source))
        for obj in env.objects:
            if obj.type.name != "AudioClip":
                continue
            clip = obj.read()
            name = clip_name(clip.m_Name)
            for _, data in clip.samples.items():
                target = destination_for(name)
                target.parent.mkdir(parents=True, exist_ok=True)
                target.write_bytes(data)
                count += 1
                break
    return count


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--project", type=Path, required=True)
    parser.add_argument("--apk-tree", type=Path, default=Path("build/pristine-rebuild/tree"))
    args = parser.parse_args()

    audio = args.project / "Assets/Resources/StoryPort/Audio"
    assets = args.apk_tree / "assets"

    ui = export([assets / "bin/Data/sharedassets0.assets"], lambda name: audio / "UI" / f"{name}.wav")

    def combat_target(name: str) -> Path:
        prefix = name.split("_", 1)[0]
        group = prefix if prefix in SOUND_SETS else "Shared"
        return audio / "Char" / group / f"{name}.wav"

    combat = export([
        assets / "assetpack/characters/character_audio.assetbundle",
        assets / "assetpack/characters_procedural_odr/character_audio_procedural.assetbundle",
    ], combat_target)
    if ui == 0 or combat == 0:
        raise SystemExit(f"Expected 9.2 audio clips, exported ui={ui} combat={combat}")
    print(f"Exported {ui} UI/music clips and {combat} combat clips into {audio}")


if __name__ == "__main__":
    main()
