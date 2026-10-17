#!/usr/bin/env python3
"""Renders revival-icon.svg into icon-512.png (the master launcher icon; the Unity build script turns it into the Android
icon sizes through the player settings) and icon-1024.png (the in-game splash).

usage: make_icon.py            (writes icon-512.png next to this script)
needs: pip install cairosvg    (Cairo SVG renderer)
"""
from pathlib import Path

import cairosvg


def main():
    here = Path(__file__).parent
    for size in (512, 1024):          # 512: app icon and first-run logo; 1024: the in-game splash
        cairosvg.svg2png(url=str(here / "revival-icon.svg"), write_to=str(here / f"icon-{size}.png"),
                         output_width=size, output_height=size)


if __name__ == "__main__":
    main()
