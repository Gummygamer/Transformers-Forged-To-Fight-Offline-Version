"""Fast checks for the local 9.2 UI atlas conversion."""

from pathlib import Path
from tempfile import TemporaryDirectory
import unittest

from PIL import Image

from prepare_project import extract_atlas_sprites


class AtlasExtractionTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temp = TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.atlas = self.root / "atlas.png"
        self.metadata = self.root / "atlas.prefab"
        image = Image.new("RGBA", (4, 4), "red")
        for x in range(2, 4):
            for y in range(2, 4):
                image.putpixel((x, y), (0, 255, 0, 255))
        image.save(self.atlas)
        self.metadata.write_text(
            "  - name: first\n    x: 0\n    y: 0\n    width: 2\n    height: 2\n"
            "  - name: second\n    x: 2\n    y: 2\n    width: 2\n    height: 2\n"
        )

    def test_crops_named_sprites_at_top_left_atlas_coordinates(self) -> None:
        extract_atlas_sprites(self.atlas, self.metadata, self.root,
                              {"first": "first.png", "second": "second.png"})
        with Image.open(self.root / "first.png") as first, Image.open(self.root / "second.png") as second:
            self.assertEqual(first.getpixel((0, 0)), (255, 0, 0, 255))
            self.assertEqual(second.getpixel((0, 0)), (0, 255, 0, 255))
            self.assertEqual(second.size, (2, 2))

    def test_missing_or_invalid_sprite_fails_before_build(self) -> None:
        with self.assertRaisesRegex(ValueError, "Missing named 9.2 UI sprites: absent"):
            extract_atlas_sprites(self.atlas, self.metadata, self.root, {"absent": "absent.png"})
        self.metadata.write_text("  - name: second\n    x: 3\n    y: 3\n    width: 2\n    height: 2\n")
        with self.assertRaisesRegex(ValueError, "outside"):
            extract_atlas_sprites(self.atlas, self.metadata, self.root, {"second": "second.png"})


if __name__ == "__main__":
    unittest.main()
