#!/usr/bin/env python3
import argparse
import struct
import sys
import tempfile
import unittest
import zipfile
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import build_phone_apk as builder


class PhoneApkBuilderTests(unittest.TestCase):
    def test_validate_server_host(self):
        self.assertEqual(builder.validate_server_host("192.168.0.139"), "192.168.0.139")
        self.assertEqual(builder.validate_server_host("tftf.local"), "tftf.local")
        for invalid in ("", "https://host", "host:8443", "bad host", "192.168.0.999"):
            with self.subTest(invalid=invalid), self.assertRaises(argparse.ArgumentTypeError):
                builder.validate_server_host(invalid)

    def test_patch_endpoint_config_preserves_size(self):
        original = b'{"Default":{"Prod":"https://tform-0901-hzlhiniyfcwf.tf-cdn.net"}}'
        patched = builder.patch_endpoint_config(original, "192.168.0.139")
        self.assertEqual(len(patched), len(original))
        self.assertIn(b"https://192.168.0.139:8443", patched)

    def test_patch_metadata_uses_lan_host_and_preserves_size(self):
        literals = [old for old, _ in builder.literal_replacements("192.168.0.139")]
        literal_offset = 24
        literal_count = len(literals) * 8
        data_offset = literal_offset + literal_count
        metadata = bytearray(data_offset + sum(map(len, literals)))
        struct.pack_into("<4I", metadata, 8, literal_offset, literal_count, data_offset, 0)
        cursor = 0
        for entry, literal in enumerate(literals):
            struct.pack_into("<II", metadata, literal_offset + entry * 8, len(literal), cursor)
            metadata[data_offset + cursor : data_offset + cursor + len(literal)] = literal
            cursor += len(literal)

        patched, changed = builder.patch_metadata(bytes(metadata), "192.168.0.139")

        self.assertEqual(len(patched), len(metadata))
        self.assertEqual(len(changed), len(literals))
        for replacement in dict(builder.literal_replacements("192.168.0.139")).values():
            self.assertIn(replacement, patched)


def elf_header(elf_class: int, machine: int) -> bytes:
    """Minimal ELF identification bytes; only class and e_machine are inspected."""
    header = bytearray(64)
    header[0:4] = b"\x7fELF"
    header[4] = elf_class
    struct.pack_into("<H", header, 18, machine)
    return bytes(header)


ARM64_ELF = elf_header(2, 183)
ARMV7_ELF = elf_header(1, 40)


class HookAbiTests(unittest.TestCase):
    def test_check_hook_accepts_matching_abi(self):
        builder.check_hook(ARM64_ELF, builder.ARM64, Path("libdothook.so"))
        builder.check_hook(ARMV7_ELF, builder.ARMV7, Path("libdothook.so"))

    def test_check_hook_rejects_mismatched_abi(self):
        # The failure this guards against is silent at runtime: the APK installs
        # and launches, and the hook simply never loads.
        with self.assertRaises(ValueError):
            builder.check_hook(ARM64_ELF, builder.ARMV7, Path("libdothook.so"))
        with self.assertRaises(ValueError):
            builder.check_hook(ARMV7_ELF, builder.ARM64, Path("libdothook.so"))

    def test_check_hook_rejects_non_elf(self):
        with self.assertRaises(ValueError):
            builder.check_hook(b"not an elf", builder.ARM64, Path("libdothook.so"))


class BuildAbiSelectionTests(unittest.TestCase):
    def make_source(self, path: Path, include_hooks=True) -> None:
        with zipfile.ZipFile(path, "w") as zf:
            zf.writestr("AndroidManifest.xml", b"manifest")
            zf.writestr(builder.METADATA, b"\x00" * 64)
            zf.writestr(builder.SPARX_MANIFEST, b"sparx")
            zf.writestr(builder.ENDPOINT_CONFIG, b"endpoint")
            if include_hooks:
                zf.writestr(f"lib/{builder.ARM64}/libdothook.so", ARM64_ELF)
            zf.writestr(f"lib/{builder.ARM64}/libil2cpp.so", b"arm64 il2cpp")
            if include_hooks:
                zf.writestr(f"lib/{builder.ARMV7}/libdothook.so", ARMV7_ELF)
            zf.writestr(f"lib/{builder.ARMV7}/libil2cpp.so", b"armv7 il2cpp")

    def build_with(self, abi, keep_other_abi=False, replace_il2cpp=False, include_hooks=True):
        with tempfile.TemporaryDirectory() as tmp:
            source = Path(tmp) / "source.apk"
            destination = Path(tmp) / "out.apk"
            self.make_source(source, include_hooks)
            hook_paths = {
                builder.ARM64: Path(tmp) / "libdothook-a64.so",
                builder.ARMV7: Path(tmp) / "libdothook-armv7.so",
            }
            hook_paths[builder.ARM64].write_bytes(ARM64_ELF)
            hook_paths[builder.ARMV7].write_bytes(ARMV7_ELF)
            patched_il2cpp = Path(tmp) / "libil2cpp-patched.so"
            patched_il2cpp.write_bytes(
                (ARM64_ELF if abi == builder.ARM64 else ARMV7_ELF)
                + b"libdothook.so patched"
            )
            # The host-rewriting steps need real Unity payload layouts, which the
            # tests above already cover. Stub them so these stay focused on ABI
            # handling and can use a small synthetic APK. Use synthetic hooks so
            # the tests do not depend on a locally built, ignored binary.
            saved = (builder.patch_metadata, builder.patch_sparx_manifest,
                     builder.patch_endpoint_config, builder.ABIS)
            builder.patch_metadata = lambda data, host: (data, [])
            builder.patch_sparx_manifest = lambda data, host: data
            builder.patch_endpoint_config = lambda data, host: data
            builder.ABIS = {
                builder.ARM64: (hook_paths[builder.ARM64], 2, 183),
                builder.ARMV7: (hook_paths[builder.ARMV7], 1, 40),
            }
            try:
                builder.build(
                    source,
                    destination,
                    "127.0.0.1",
                    abi,
                    keep_other_abi,
                    patched_il2cpp if replace_il2cpp else None,
                )
            finally:
                (builder.patch_metadata, builder.patch_sparx_manifest,
                 builder.patch_endpoint_config, builder.ABIS) = saved
            with zipfile.ZipFile(destination) as zf:
                return (
                    set(zf.namelist()),
                    zf.read(builder.hook_entry(abi)),
                    zf.read(builder.il2cpp_entry(abi)),
                )

    def test_armv7_build_drops_arm64_libraries(self):
        names, hook, _ = self.build_with(builder.ARMV7)
        self.assertNotIn(f"lib/{builder.ARM64}/libil2cpp.so", names)
        self.assertIn(f"lib/{builder.ARMV7}/libil2cpp.so", names)
        self.assertEqual(hook, ARMV7_ELF)

    def test_arm64_build_drops_armv7_libraries(self):
        names, _, _ = self.build_with(builder.ARM64)
        self.assertNotIn(f"lib/{builder.ARMV7}/libil2cpp.so", names)
        self.assertIn(f"lib/{builder.ARM64}/libil2cpp.so", names)

    def test_keep_other_abi_retains_both(self):
        names, _, _ = self.build_with(builder.ARMV7, keep_other_abi=True)
        self.assertIn(f"lib/{builder.ARM64}/libil2cpp.so", names)
        self.assertIn(f"lib/{builder.ARMV7}/libil2cpp.so", names)

    def test_embeds_explicit_patched_il2cpp(self):
        _, _, il2cpp = self.build_with(builder.ARMV7, replace_il2cpp=True)
        self.assertEqual(il2cpp, ARMV7_ELF + b"libdothook.so patched")

    def test_adds_hook_when_source_apk_is_pristine(self):
        names, hook, _ = self.build_with(builder.ARMV7, include_hooks=False)
        self.assertIn(builder.hook_entry(builder.ARMV7), names)
        self.assertEqual(hook, ARMV7_ELF)


if __name__ == "__main__":
    unittest.main()
