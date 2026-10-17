#!/usr/bin/env python3
"""Renders revival-icon.svg into icon-512.png (the master launcher icon; the Unity build script turns it into the Android
icon sizes through the player settings) and icon-1024.png (the in-game splash, the same head without the purple tile
background and glow, on a transparent background).

usage: make_icon.py            (writes icon-512.png next to this script)
needs: pip install cairosvg    (Cairo SVG renderer)
"""
import re
from pathlib import Path

import cairosvg


def main():
    here = Path(__file__).parent
    svg = (here / "revival-icon.svg").read_text()
    cairosvg.svg2png(bytestring=svg.encode(), write_to=str(here / "icon-512.png"), output_width=512, output_height=512)
    # in-game splash: drop the tile background (rect id="bg") and the purple glow (circle id="glowdisc")
    bare = re.sub(r'<(rect|circle) id="(bg|glowdisc)"[^>]*/>', "", svg)
    cairosvg.svg2png(bytestring=bare.encode(), write_to=str(here / "icon-1024.png"), output_width=1024, output_height=1024)


if __name__ == "__main__":
    main()
