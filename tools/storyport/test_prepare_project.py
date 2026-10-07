"""Fast checks for the local 9.2 UI atlas conversion."""

from pathlib import Path
from tempfile import TemporaryDirectory
import unittest

from PIL import Image

from prepare_project import arena_sky_texture, copy_localization_catalogs, extract_atlas_sprites, find_nav_font


class LocalizationCopyTests(unittest.TestCase):
    def test_copy_replaces_prepared_catalogs_and_removes_stale_locales(self) -> None:
        with TemporaryDirectory() as temporary:
            root = Path(temporary)
            source = root / "source"
            target = root / "prepared/Localization"
            source.mkdir()
            target.mkdir(parents=True)
            (source / "dialogue_en.txt").write_text("hello\n", encoding="utf-8")
            (target / "dialogue_old.txt").write_text("stale\n", encoding="utf-8")

            copy_localization_catalogs(source, target)

            self.assertEqual([path.name for path in target.iterdir()], ["dialogue_en.txt"])
            self.assertEqual((target / "dialogue_en.txt").read_text(encoding="utf-8"), "hello\n")

    def test_missing_source_removes_all_prepared_catalogs(self) -> None:
        with TemporaryDirectory() as temporary:
            root = Path(temporary)
            target = root / "prepared/Localization"
            target.mkdir(parents=True)
            (target / "dialogue_old.txt").write_text("stale\n", encoding="utf-8")

            copy_localization_catalogs(root / "missing", target)

            self.assertFalse(target.exists())


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

    def test_navigation_font_prefers_converted_art_and_honors_explicit_path(self) -> None:
        converted = self.root / "converted"
        local = converted / "Assets/Resources/ui/fonts/ttf/Tecnica_Bold_116.ttf"
        local.parent.mkdir(parents=True)
        local.write_bytes(b"local font")
        explicit = self.root / "another.ttf"
        explicit.write_bytes(b"explicit font")
        self.assertEqual(find_nav_font(converted, None, self.root / "absent.ttf"), local)
        self.assertEqual(find_nav_font(converted, explicit, self.root / "absent.ttf"), explicit)
        with self.assertRaisesRegex(FileNotFoundError, "does not exist"):
            find_nav_font(converted, self.root / "missing.ttf", local)

    def test_navigation_font_missing_fails_before_build(self) -> None:
        with self.assertRaisesRegex(FileNotFoundError, "pass --nav-font"):
            find_nav_font(self.root / "converted", None, self.root / "absent.ttf")


class ArenaSkyTests(unittest.TestCase):
    def test_resolves_the_base_texture_of_the_sky_renderer_material(self) -> None:
        with TemporaryDirectory() as temporary:
            root = Path(temporary)
            scenes = root / "bundles/scenes/demo_merged"
            scenes.mkdir(parents=True)
            (root / "Material").mkdir()
            (root / "Texture2D").mkdir()
            (root / "Material/sky.mat").write_text("_base_tex:\n        m_Texture: {fileID: 2800000, guid: " + "b" * 32 + ", type: 3}\n")
            (root / "Material/sky.mat.meta").write_text("guid: " + "a" * 32 + "\n")
            (root / "Texture2D/sky.png").write_bytes(b"png")
            (root / "Texture2D/sky.png.meta").write_text("guid: " + "b" * 32 + "\n")
            (scenes / "demo_timeofday_0_forward.prefab").write_text(
                "--- !u!23 &1\nMeshRenderer:\n  m_Materials:\n  - {fileID: 2100000, guid: " + "a" * 32 + ", type: 2}\n"
            )

            self.assertEqual(arena_sky_texture(root, "demo", 0), root / "Texture2D/sky.png")

    def test_returns_none_without_a_sky_material(self) -> None:
        with TemporaryDirectory() as temporary:
            root = Path(temporary)
            scenes = root / "bundles/scenes/demo_merged"
            scenes.mkdir(parents=True)
            (scenes / "demo_timeofday_0_forward.prefab").write_text("--- !u!1 &1\nGameObject:\n")

            self.assertIsNone(arena_sky_texture(root, "demo", 0))


if __name__ == "__main__":
    unittest.main()
