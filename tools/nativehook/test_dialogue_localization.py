"""Regression checks for the catalogs bundled into both native patcher hooks."""
import importlib.util
import os
import shutil
import subprocess
import tempfile
import unittest
from pathlib import Path
from zipfile import ZipFile


ROOT = Path(__file__).resolve().parents[2]
GENERATOR = ROOT / "tools/nativehook/generate_dialogue_header.py"
SPEC = importlib.util.spec_from_file_location("dialogue_header", GENERATOR)
dialogue_header = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(dialogue_header)


class BundledDialogueTests(unittest.TestCase):
    def test_both_native_deserializers_select_languages_and_preserve_fallbacks(self):
        compiler = shutil.which("cc")
        if not compiler:
            self.skipTest("a host C compiler is required for the native deserializer check")
        catalogs = {locale: dialogue_header.read_catalog(locale) for locale in dialogue_header.LOCALES}
        keys = [key for key in catalogs["en"] if key.startswith("ID_STORY_") and not key.startswith("ID_STORY_UI_")]
        # Compile the actual hook functions with a managed-string fixture and mocked
        # IL2CPP allocation/language callbacks; do not duplicate the lookup logic.
        for filename, length_offset, chars_offset, language_rva, line_offset in (
            ("hook.c", 0x10, 0x14, 0x127CD10, 0x38),
            ("hook_arm32.c", 0x08, 0x0C, 0xF7F3A4, 0x20),
        ):
            with self.subTest(abi=filename), tempfile.TemporaryDirectory(prefix="tftf-deserializer-") as directory:
                directory = Path(directory)
                dialogue_header.generate(directory / "dialogue_translations.generated.h")
                hook = (ROOT / "tools/nativehook" / filename).read_text(encoding="utf-8")
                start = hook.index("static int dialogue_string_equals_utf8(")
                end_marker = "// slot 44" if filename == "hook.c" else "// Managed pointers"
                functions = hook[start:hook.index(end_marker, start)]
                call = "hooked_dialogue_entry_deserialize(entry, NULL, NULL, NULL" + (", NULL" * 4 if filename == "hook_arm32.c" else "") + ")"
                fixture = [
                    "#include <stdint.h>\n#include <stddef.h>\n#include <string.h>\n",
                    '#include "dialogue_translations.generated.h"\n',
                    "typedef void* (*fn8)(void*,void*,void*,void*,void*,void*,void*,void*);\n",
                    "static fn8 g_dialogue_deserialize_orig; static uintptr_t g_base;\n",
                    "static void* (*g_strnew)(const char*);\n",
                    f"static const uint32_t RVA_LOCALIZER_GET_CURRENT = {language_rva};\nstatic const uint32_t OFFSET_DIALOGUE_ENTRY_LINE = {line_offset};\n" if filename == "hook.c" else "",
                    functions,
                    "static int current_language; static int language_calls;\n",
                    "static unsigned char managed[20000] __attribute__((aligned(8)));\n",
                    "static unsigned char entry_buf[256] __attribute__((aligned(8)));\n",
                    "static void* const result_value = (void*)0x1;\n",
                    "#define entry ((void*)entry_buf)\n",
                    f"#define LINE (*(void**)(entry_buf+{line_offset}))\n",
                    "static int language(void* unused) { language_calls++; return current_language; }\n",
                    "static void* allocate(const char* text) { return (void*)text; }\n",
                    "static void* original(void*a,void*b,void*c,void*d,void*e,void*f,void*g,void*h) { return result_value; }\n",
                    f"static void prepare(const uint16_t* text, int length) {{ memset(managed,0,sizeof managed); memcpy(managed+{length_offset}, &length, 4); memcpy(managed+{chars_offset}, text, length*2); }}\n",
                    f"int main(void) {{ g_base=(uintptr_t)language-{language_rva}; g_strnew=allocate; g_dialogue_deserialize_orig=original;\n",
                ]
                for key in keys:
                    units = catalogs["en"][key].encode("utf-16-le")
                    values = [int.from_bytes(units[i:i + 2], "little") for i in range(0, len(units), 2)]
                    fixture.append("{ const uint16_t text[]={" + ",".join(map(str, values)) + "}; prepare(text,sizeof text/2);\n")
                    for language, locale in enumerate(dialogue_header.LOCALES, 1):
                        expected = dialogue_header.c_string(catalogs[locale][key])
                        fixture.append(f"LINE=managed; current_language={language}; if({call}!=result_value || strcmp((const char*)LINE, {expected})) return 1;\n")
                    for language in (-1, 0, 17, 999):
                        fixture.append(f"LINE=managed; current_language={language}; if({call}!=result_value || LINE!=managed) return 2;\n")
                    fixture.append("}\n")
                fixture.extend([
                    f"if(language_calls!={len(keys) * 20}) return 3;\n",
                    f"current_language=2; const uint16_t unknown[]={{'u','n','k','n','o','w','n'}}; prepare(unknown,7); LINE=managed; if({call}!=result_value || LINE!=managed) return 4;\n",
                    # Also exercise UTF-16 surrogate pairs and non-ASCII punctuation.
                    'const uint16_t unicode[]={0x2019,0xD83D,0xDE80}; prepare(unicode,3); if(!dialogue_string_equals_utf8(managed,"’🚀") || dialogue_string_equals_utf8(managed,"’🚁")) return 5;\n',
                    f"g_strnew=NULL; LINE=managed; if({call}!=result_value || LINE!=managed) return 6;\n",
                    f"g_strnew=allocate; LINE=NULL; if({call}!=result_value || LINE!=NULL) return 7;\n",
                    f"g_dialogue_deserialize_orig=NULL; LINE=managed; if({call}!=NULL || LINE!=managed) return 8;\n",
                    "return 0; }\n",
                ])
                source, binary = directory / "check.c", directory / "check"
                source.write_text("".join(fixture), encoding="utf-8")
                subprocess.run([compiler, "-Wall", "-Werror", "-I", str(directory), str(source), "-o", str(binary)], check=True)
                subprocess.run([str(binary)], check=True)

    def test_native_hooks_target_dialogue_entry_deserialize_and_line_field(self):
        # get_line is an inlined auto-property getter the overlay never calls, so both hooks
        # must patch DialogueEntry.Deserialize and rewrite the <line> backing field.
        arm64 = (ROOT / "tools/nativehook/hook.c").read_text(encoding="utf-8")
        arm32 = (ROOT / "tools/nativehook/hook_arm32.c").read_text(encoding="utf-8")
        for needle in ("RVA_DIALOGUE_ENTRY_DESERIALIZE = 0x145D748", "OFFSET_DIALOGUE_ENTRY_LINE = 0x38"):
            self.assertIn(needle, arm64)
        for needle in ("g_base + 0x11BBE08", "(entry + 0x20)"):
            self.assertIn(needle, arm32)
        for source in (arm64, arm32):
            self.assertNotIn("hooked_dialogue_entry_get_line", source)

    def test_native_slots_match_game_language_index_order(self):
        self.assertEqual(
            ("en", "fr", "it", "de", "es", "pt", "ru", "ko", "zh-CN", "zh-TW", "ja", "tr", "nl", "ar", "th", "id"),
            dialogue_header.LOCALES,
        )

    def test_catalogs_generate_complete_language_matrix_and_lookup(self):
        compiler = shutil.which("cc")
        if not compiler:
            self.skipTest("a host C compiler is required for the generated-header lookup check")

        catalogs = {locale: dialogue_header.read_catalog(locale) for locale in dialogue_header.LOCALES}
        baseline = catalogs["en"]
        keys = [key for key in baseline if key.startswith("ID_STORY_") and not key.startswith("ID_STORY_UI_")]
        self.assertEqual(42, len(keys))

        with tempfile.TemporaryDirectory(prefix="tftf-dialogue-") as temp:
            temp = Path(temp)
            header = temp / "dialogue_translations.generated.h"
            dialogue_header.generate(header)
            generated = header.read_text(encoding="utf-8")
            self.assertIn("TFTF_DIALOGUE_TRANSLATION_COUNT 42", generated)
            self.assertIn("tftf_dialogue_locale_text", generated)

            source = temp / "check.c"
            binary = temp / "check"
            source.write_text(
                '#include <stddef.h>\n#include <stdio.h>\n'
                '#include "dialogue_translations.generated.h"\n'
                'int main(void) {\n'
                '  for (int i = 0; i < TFTF_DIALOGUE_TRANSLATION_COUNT; ++i) {\n'
                '    for (int locale = 1; locale <= 16; ++locale) {\n'
                '      const char *value = tftf_dialogue_locale_text(&g_tftf_dialogue_translations[i], locale);\n'
                '      if (!value) return 2;\n'
                '      puts(value);\n'
                '    }\n'
                '    if (tftf_dialogue_locale_text(&g_tftf_dialogue_translations[i], 0) || '
                'tftf_dialogue_locale_text(&g_tftf_dialogue_translations[i], 17)) return 3;\n'
                '  }\n'
                '  return 0;\n'
                '}\n',
                encoding="utf-8",
            )
            subprocess.run([compiler, "-Wall", "-Werror", "-I", str(temp), str(source), "-o", str(binary)], check=True)
            actual = subprocess.run([str(binary)], check=True, capture_output=True, text=True, encoding="utf-8").stdout.splitlines()

        expected = [catalogs[locale][key] for key in keys for locale in dialogue_header.LOCALES]
        self.assertEqual(expected, actual)

    def test_release_patcher_apk_contains_every_translation_in_both_hooks(self):
        apk_path = os.environ.get("TFTF_PATCHER_APK")
        if not apk_path:
            self.skipTest("set TFTF_PATCHER_APK to inspect a freshly built patcher APK")

        catalogs = {locale: dialogue_header.read_catalog(locale) for locale in dialogue_header.LOCALES}
        baseline = catalogs["en"]
        keys = [key for key in baseline if key.startswith("ID_STORY_") and not key.startswith("ID_STORY_UI_")]
        with ZipFile(apk_path) as apk:
            for asset in ("assets/libdothook-arm64.bin", "assets/libdothook-armv7.bin"):
                hook = apk.read(asset)
                for locale, catalog in catalogs.items():
                    for key in keys:
                        self.assertIn(
                            catalog[key].encode("utf-8"), hook,
                            f"{asset} omits {locale} translation for {key}",
                        )

    def test_catalog_validation_rejects_incomplete_and_placeholder_drift(self):
        original_dir = dialogue_header.CATALOG_DIR
        try:
            with tempfile.TemporaryDirectory(prefix="tftf-dialogue-invalid-") as temp:
                temp = Path(temp)
                for locale in dialogue_header.LOCALES:
                    shutil.copy(original_dir / f"dialogue_{locale}.txt", temp)
                dialogue_header.CATALOG_DIR = temp
                (temp / "dialogue_fr.txt").write_text("missing key\n", encoding="utf-8")
                with self.assertRaises(ValueError):
                    dialogue_header.generate(temp / "out.h")

                shutil.copy(original_dir / "dialogue_fr.txt", temp / "dialogue_fr.txt")
                lines = (temp / "dialogue_fr.txt").read_text(encoding="utf-8").splitlines()
                row_index = next(i for i, line in enumerate(lines) if line and not line.startswith("#"))
                key, value = lines[row_index].split("\t", 1)
                lines[row_index] = key + "\t" + value + " {missing_placeholder}"
                (temp / "dialogue_fr.txt").write_text("\n".join(lines) + "\n", encoding="utf-8")
                with self.assertRaisesRegex(ValueError, "placeholders"):
                    dialogue_header.generate(temp / "out.h")

                for suffix, message in ((" <b></b>", "markup"), ("", "empty")):
                    shutil.copy(original_dir / "dialogue_fr.txt", temp / "dialogue_fr.txt")
                    lines = (temp / "dialogue_fr.txt").read_text(encoding="utf-8").splitlines()
                    lines[row_index] = key + "\t" + (value + suffix if suffix else "")
                    (temp / "dialogue_fr.txt").write_text("\n".join(lines) + "\n", encoding="utf-8")
                    with self.assertRaisesRegex(ValueError, message):
                        dialogue_header.generate(temp / "out.h")
        finally:
            dialogue_header.CATALOG_DIR = original_dir


if __name__ == "__main__":
    unittest.main()
