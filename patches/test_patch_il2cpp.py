import struct
import unittest

from patches.patch_il2cpp import (
    ARM64,
    ARMV7,
    default_needed_mode,
    inject_needed_armv7_inplace,
)


def make_elf32_fixture(*, donor_relocation=False):
    blob = bytearray(0x700)
    blob[:6] = b"\x7fELF\x01\x01"
    struct.pack_into("<I", blob, 0x20, 0x500)  # e_shoff
    struct.pack_into("<HHH", blob, 0x2E, 40, 5, 0)  # e_shentsize/e_shnum/e_shstrndx

    dynstr = b"\0__floatundidf\0__aeabi_ul2d\0libc.so\0"
    dynstr_offset = 0x100
    blob[dynstr_offset:dynstr_offset + len(dynstr)] = dynstr
    donor_name = dynstr.index(b"__floatundidf")
    alias_name = dynstr.index(b"__aeabi_ul2d")
    libc_name = dynstr.index(b"libc.so")

    dynsym_offset = 0x200
    # Symbol zero is reserved. The two aliases intentionally describe one function.
    struct.pack_into("<IIIBBH", blob, dynsym_offset + 16, donor_name, 0x1234, 16, 0x12, 0, 7)
    struct.pack_into("<IIIBBH", blob, dynsym_offset + 32, alias_name, 0x1234, 16, 0x12, 0, 7)

    rel_offset = 0x300
    rel_size = 8 if donor_relocation else 0
    if donor_relocation:
        struct.pack_into("<II", blob, rel_offset, 0x4000, (1 << 8) | 22)

    dynamic_offset = 0x380
    struct.pack_into("<II", blob, dynamic_offset, 1, libc_name)
    # Two zero entries follow: one spare entry and the required final DT_NULL.

    # Section headers: null, dynstr, dynsym, dynamic, rel.
    headers = [
        (0, 0, 0, 0, 0, 0, 0, 0, 0, 0),
        (0, 3, 0, 0, dynstr_offset, len(dynstr), 0, 0, 1, 0),
        (0, 11, 0, 0, dynsym_offset, 48, 1, 0, 4, 16),
        (0, 6, 0, 0, dynamic_offset, 24, 1, 0, 4, 8),
        (0, 9, 0, 0, rel_offset, rel_size, 2, 0, 4, 8),
    ]
    for index, header in enumerate(headers):
        struct.pack_into("<10I", blob, 0x500 + index * 40, *header)
    return blob, {
        "donor_entry": dynsym_offset + 16,
        "alias_name": alias_name,
        "dependency_slot": dynstr_offset + donor_name,
        "dynamic_spare": dynamic_offset + 8,
    }


class Armv7InplaceNeededTests(unittest.TestCase):
    def test_adds_dependency_without_changing_layout(self):
        blob, offsets = make_elf32_fixture()
        original_size = len(blob)

        self.assertTrue(inject_needed_armv7_inplace(blob))

        self.assertEqual(len(blob), original_size)
        self.assertEqual(
            struct.unpack_from("<I", blob, offsets["donor_entry"])[0],
            offsets["alias_name"],
        )
        self.assertEqual(
            blob[offsets["dependency_slot"]:offsets["dependency_slot"] + 14],
            b"libdothook.so\0",
        )
        self.assertEqual(
            struct.unpack_from("<II", blob, offsets["dynamic_spare"]),
            (1, offsets["dependency_slot"] - 0x100),
        )
        self.assertEqual(
            struct.unpack_from("<II", blob, offsets["dynamic_spare"] + 8),
            (0, 0),
        )

    def test_is_idempotent(self):
        blob, _ = make_elf32_fixture()
        self.assertTrue(inject_needed_armv7_inplace(blob))
        after_first = bytes(blob)
        self.assertFalse(inject_needed_armv7_inplace(blob))
        self.assertEqual(bytes(blob), after_first)

    def test_rejects_a_donor_used_by_relocation(self):
        blob, _ = make_elf32_fixture(donor_relocation=True)
        with self.assertRaisesRegex(ValueError, "referenced by a relocation"):
            inject_needed_armv7_inplace(blob)

    def test_platform_defaults_preserve_linux_patchelf(self):
        self.assertEqual(default_needed_mode(ARMV7, "posix"), "patchelf")
        self.assertEqual(default_needed_mode(ARMV7, "nt"), "inplace")
        self.assertEqual(default_needed_mode(ARM64, "posix"), "byte")
        self.assertEqual(default_needed_mode(ARM64, "nt"), "byte")


if __name__ == "__main__":
    unittest.main()
