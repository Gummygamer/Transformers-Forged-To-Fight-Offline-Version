#!/usr/bin/env python3
import argparse
import struct
import sys
import unittest
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


if __name__ == "__main__":
    unittest.main()
