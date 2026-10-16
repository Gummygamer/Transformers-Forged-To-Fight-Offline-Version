#!/usr/bin/env python3
"""Renders revival-icon.svg into icon-512.png, the master launcher icon (the Unity build script turns it into the
Android icon sizes through the player settings).

usage: make_icon.py            (writes icon-512.png next to this script)
needs: pip install cairosvg    (Cairo SVG renderer)
"""
from pathlib import Path

import cairosvg


def main():
    here = Path(__file__).parent
    cairosvg.svg2png(url=str(here / "revival-icon.svg"), write_to=str(here / "icon-512.png"),
                     output_width=512, output_height=512)


if __name__ == "__main__":
    main()
