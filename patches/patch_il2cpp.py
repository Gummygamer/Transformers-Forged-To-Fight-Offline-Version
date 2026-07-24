#!/usr/bin/env python3
"""
Patch libil2cpp.so to get past the dead-server checks, for either shipped ABI.

The APK ships both `lib/arm64-v8a/` and `lib/armeabi-v7a/`. The same six logical
patches apply to both builds; only the addresses and the instruction encodings
differ. Pass --abi to pick which one you are patching. RVA == file offset in the
text segment of both libraries.

The six patches, in both builds:
  1. TcpClientSSL.CertificateValidation      -> return true  (accept our TLS cert)
  2. TcpClientBouncy.NotifyServerCertificate -> no-op        (second pinning path)
  3. Hub.InitializeComponents                -> register the managers past the
     null Hub.Config.OTAConfig gate
  4. LoginManager._PostInit                  -> skip the "no valid authenticators
     found" failure when the list is empty, so login completes on our device session
  5. Hub.SubSystemConnecting                 -> do not FatalError on a subsystem
     that reached error-state 3
  6. SubSystem.FatalError                    -> return instead of tail-calling
     Hub.FatalError, silencing the "FAILED TO LOG IN" dialog

The armv7 addresses were derived from the arm64 ones with `patches/abi_map.py`,
which pairs the two Il2CppDumper dumps produced from the same global-metadata.dat,
then each site was confirmed by disassembly. The armv7 library is ARM mode (A32),
fixed 4-byte instructions -- not Thumb.
"""
import argparse
import os
import shutil
import struct
import subprocess
import sys

import capstone

ARM64 = "arm64-v8a"
ARMV7 = "armeabi-v7a"

# ---- arm64 (A64) encodings ------------------------------------------------
A64_RET = bytes.fromhex("c0035fd6")           # ret
A64_NOP = bytes.fromhex("1f2003d5")           # nop
A64_TRUE_RET = bytes.fromhex("20008052") + A64_RET   # movz w0,#1 ; ret
# b #+0x24 : at 0xFC21B4 jump into the manager-registration block, past the
# OTAConfig deref, so UserManager (& ~40 managers) register even when
# Hub.Config.OTAConfig is null (the dead-server offline state).
A64_B_FC21D8 = bytes.fromhex("09000014")

# ---- armv7 (A32) encodings ------------------------------------------------
A32_BX_LR = bytes.fromhex("1eff2fe1")         # bx lr
A32_NOP = bytes.fromhex("00f020e3")           # nop {0}
A32_TRUE_RET = bytes.fromhex("0100a0e3") + A32_BX_LR  # mov r0,#1 ; bx lr
# b #0xC29824 from 0xC297F4 : same intent as the arm64 0xFC21B4 patch. Skips both
# the OTAConfig null-throw and the OTAManager registration, landing on the next
# manager. imm24 = (0xC29824 - (0xC297F4 + 8)) >> 2 = 0xA.
A32_B_C29824 = bytes.fromhex("0a0000ea")

TARGETS = {
    ARM64: [
        (0x123A73C, A64_TRUE_RET, "TcpClientSSL.CertificateValidation -> true"),
        (0x14EC940, A64_RET, "TcpClientBouncy.NotifyServerCertificate -> noop"),
        (0xFC21B4, A64_B_FC21D8, "Hub.InitializeComponents -> register managers past OTAConfig gate"),
        # _PostInit coroutine: 'cbz w9' at 0x162B690 = if listToAuthenticate.Count==0 -> _LoginFailed
        # ("no valid authenticators found"). NOP it so count==0 falls through to the success
        # (SetState 2 / authenticate) path -> login completes with our device session (stoken).
        (0x162B690, A64_NOP, "LoginManager._PostInit -> skip 'no valid authenticators' fail on Count==0"),
        # Hub.SubSystemConnecting: 'cmp w8,#3; b.eq 0xFC3850' = if a subsystem reaches
        # error-state 3 -> FatalError(ID_SPARX_ERROR_UNKNOWN) = "FAILED TO LOG IN". NOP the
        # b.eq so a failed subsystem falls through as connected and login completes (grind
        # past the gate; the failed subsystem's features are degraded but boot proceeds).
        (0xFC3718, A64_NOP, "Hub.SubSystemConnecting -> skip FatalError on subsystem error-state 3"),
        # SubSystem.FatalError sets state=3 (handled by the 0xFC3718 skip) then TAIL-CALLs
        # Hub.FatalError (`b 0xfc0734`) which pops the "FAILED TO LOG IN" dialog directly,
        # bypassing the polling skip. Replace the tail-call with `ret` so a subsystem fatal
        # just sets state 3 and returns silently -> the Hub treats it as connected and the
        # boot grinds past data gates (e.g. "No User Data") to the next screen/FTE.
        (0x122C680, A64_RET, "SubSystem.FatalError -> ret instead of tail-call Hub.FatalError (silence subsystem fatals)"),
    ],
    ARMV7: [
        # Function entry; no frame is pushed before this point, so lr is still live.
        (0xF2E800, A32_TRUE_RET, "TcpClientSSL.CertificateValidation -> true"),
        # Overwrites the opening `push {r4..lr}`; void return, nothing to unwind.
        (0x12646D8, A32_BX_LR, "TcpClientBouncy.NotifyServerCertificate -> noop"),
        # `cmp r5,#0` guarding the OTAConfig null-throw at 0xC297FC. Config itself was
        # null-checked at 0xC297E4, so branching from here is safe.
        (0xC297F4, A32_B_C29824, "Hub.InitializeComponents -> register managers past OTAConfig gate"),
        # `beq 0x13E6A80` after `ldr r0,[r4,#0xc]` (List._size of listToAuthenticate).
        (0x13E69F8, A32_NOP, "LoginManager._PostInit -> skip 'no valid authenticators' fail on Count==0"),
        # `beq 0xC2B448` after `cmp r1,#3` on the subsystem state field (+0xc).
        (0xC2B2A4, A32_NOP, "Hub.SubSystemConnecting -> skip FatalError on subsystem error-state 3"),
        # `b 0xC279B0` (Hub.FatalError) tail-call; the preceding `pop` already restored lr.
        (0xF1C734, A32_BX_LR, "SubSystem.FatalError -> ret instead of tail-call Hub.FatalError (silence subsystem fatals)"),
    ],
}

DISASM = {
    ARM64: (capstone.CS_ARCH_ARM64, capstone.CS_MODE_LITTLE_ENDIAN),
    ARMV7: (capstone.CS_ARCH_ARM, capstone.CS_MODE_ARM),
}


def inject_needed_bytes(blob):
    """arm64 only: re-inject DT_NEEDED "libdothook.so" by hand.

    The pristine library has no such dependency, and without it the runtime hook is
    never mapped and login just hangs. Bytes verified by diffing the known-good
    hooked lib:
      - "libdothook.so" written into a zero cave inside .dynstr @ 0x7D3034
      - a .dynamic slot turned into DT_NEEDED: d_tag=1 @ 0x2BA9258, d_val=.dynstr off @ 0x2BA9260

    This hand-rolled entry is accepted by LDPlayer's linker but REJECTED by the
    stricter bionic linker on a stock Google emulator image (it trips
    linker_phdr.cpp get_string 'index < strtab_size_'). Use --needed patchelf there.
    """
    blob[0x7D3034:0x7D3034 + 13] = b"libdothook.so"
    blob[0x2BA9258] = 0x01
    blob[0x2BA9260:0x2BA9263] = bytes.fromhex("5cfe7b")


def inject_needed_patchelf(path):
    """Add DT_NEEDED properly, rebuilding the string table. Works for both ABIs.

    This remains the default ARMv7 route on Linux. Windows can use the strict
    layout-preserving ARMv7 injector below.
    """
    exe = shutil.which("patchelf")
    if not exe:
        sys.exit("[!] patchelf not found on PATH; install it or, for the pristine "
                 "9.2.0 ARMv7 library, pass --needed inplace")
    subprocess.run([exe, "--add-needed", "libdothook.so", path], check=True)


def _elf32_sections(blob):
    """Return ELF32 section headers without depending on pyelftools.

    The Windows 7 build deliberately keeps this helper in the standard library:
    current ELF rewriting packages either no longer support Python 3.8/Windows 7
    or rebuild this particular library with shifted code offsets.
    """
    if blob[:4] != b"\x7fELF" or blob[4] != 1 or blob[5] != 1:
        raise ValueError("in-place dependency injection needs a little-endian ELF32 library")
    shoff = struct.unpack_from("<I", blob, 0x20)[0]
    shentsize, shnum = struct.unpack_from("<HH", blob, 0x2E)
    if shentsize < 40 or not shoff or not shnum:
        raise ValueError("ELF32 section headers are missing")
    if shoff + shentsize * shnum > len(blob):
        raise ValueError("ELF32 section header table is outside the file")
    sections = []
    for index in range(shnum):
        off = shoff + index * shentsize
        fields = struct.unpack_from("<10I", blob, off)
        sections.append({
            "index": index,
            "type": fields[1],
            "offset": fields[4],
            "size": fields[5],
            "link": fields[6],
            "entsize": fields[9],
        })
    return sections


def _elf_cstring(blob, start, limit):
    if not 0 <= start < limit <= len(blob):
        raise ValueError("ELF string offset is outside the file")
    end = blob.find(b"\0", start, limit)
    if end < 0:
        raise ValueError("unterminated ELF string")
    return bytes(blob[start:end])


def inject_needed_armv7_inplace(blob):
    """Add libdothook.so to the shipped ARMv7 library without moving a byte.

    Growing .dynstr normally makes an ELF editor rebuild the file. LIEF 0.12.3,
    for example, shifts this client's executable sections by 0x1000, invalidating
    the verified file offsets above. The pristine 9.2.0 ARMv7 library gives us a
    safer exact-layout route:

    * __floatundidf and __aeabi_ul2d are equivalent exported aliases (same value,
      size, type, and section), and no relocation uses __floatundidf.
    * Repoint that unused alias's st_name to __aeabi_ul2d, then reuse its old
      14-byte string-table slot for "libdothook.so\\0".
    * Turn the first DT_NULL into DT_NEEDED; .dynamic has spare zero entries, so
      the following entry remains the terminator.

    This is intentionally strict. A different library/version fails closed
    instead of receiving a plausible-looking but unsafe patch.
    """
    sections = _elf32_sections(blob)
    dynsyms = [s for s in sections if s["type"] == 11]  # SHT_DYNSYM
    dynamics = [s for s in sections if s["type"] == 6]  # SHT_DYNAMIC
    if len(dynsyms) != 1 or len(dynamics) != 1:
        raise ValueError("expected exactly one ELF32 .dynsym and .dynamic section")
    dynsym = dynsyms[0]
    dynamic = dynamics[0]
    if dynsym["entsize"] != 16 or dynamic["entsize"] != 8:
        raise ValueError("unexpected ELF32 dynamic table entry size")
    if not 0 <= dynsym["link"] < len(sections):
        raise ValueError("ELF32 .dynsym has an invalid string-table link")
    dynstr = sections[dynsym["link"]]
    str_start = dynstr["offset"]
    str_limit = str_start + dynstr["size"]
    if str_limit > len(blob):
        raise ValueError("ELF32 dynamic string table is outside the file")

    dependency = b"libdothook.so"
    dynamic_end = dynamic["offset"] + dynamic["size"]
    for off in range(dynamic["offset"], dynamic_end, dynamic["entsize"]):
        if off + 8 > len(blob):
            raise ValueError("ELF32 dynamic table is outside the file")
        tag, value = struct.unpack_from("<II", blob, off)
        if tag == 0:
            break
        if tag == 1:  # DT_NEEDED
            if value >= dynstr["size"]:
                raise ValueError("ELF32 DT_NEEDED string is outside .dynstr")
            if _elf_cstring(blob, str_start + value, str_limit) == dependency:
                return False

    wanted_names = {b"__floatundidf": None, b"__aeabi_ul2d": None}
    symbol_count = dynsym["size"] // dynsym["entsize"]
    for index in range(symbol_count):
        off = dynsym["offset"] + index * dynsym["entsize"]
        if off + 16 > len(blob):
            raise ValueError("ELF32 dynamic symbol table is outside the file")
        name_offset, value, size, info, other, shndx = struct.unpack_from("<IIIBBH", blob, off)
        if name_offset >= dynstr["size"]:
            raise ValueError("ELF32 dynamic symbol name is outside .dynstr")
        name = _elf_cstring(blob, str_start + name_offset, str_limit)
        if name in wanted_names:
            wanted_names[name] = {
                "index": index,
                "entry_offset": off,
                "name_offset": name_offset,
                "value": value,
                "size": size,
                "type": info & 0xF,
                "section": shndx,
            }

    donor = wanted_names[b"__floatundidf"]
    alias = wanted_names[b"__aeabi_ul2d"]
    if donor is None or alias is None:
        raise ValueError("expected ARMv7 alias symbols were not found; is this the 9.2.0 library?")
    comparable = ("value", "size", "type", "section")
    if any(donor[key] != alias[key] for key in comparable) or donor["section"] == 0:
        raise ValueError("ARMv7 alias symbols no longer describe the same defined function")

    # Relocations name symbols by index. The known donor has none; fail closed if
    # another build starts using it instead of silently changing resolution.
    for rel in (s for s in sections if s["type"] in (4, 9)):  # SHT_RELA/SHT_REL
        expected_entsize = 12 if rel["type"] == 4 else 8
        entsize = rel["entsize"] or expected_entsize
        if entsize != expected_entsize:
            raise ValueError("unexpected ELF32 relocation entry size")
        for off in range(rel["offset"], rel["offset"] + rel["size"], entsize):
            if off + entsize > len(blob):
                raise ValueError("ELF32 relocation table is outside the file")
            info = struct.unpack_from("<I", blob, off + 4)[0]
            if info >> 8 == donor["index"]:
                raise ValueError("the ARMv7 donor alias is referenced by a relocation")

    old_name = b"__floatundidf"
    old_slot = str_start + donor["name_offset"]
    if _elf_cstring(blob, old_slot, str_limit) != old_name:
        raise ValueError("ARMv7 donor string changed unexpectedly")
    if len(dependency) > len(old_name):
        raise ValueError("ARMv7 donor string slot is too short")

    first_null = None
    for off in range(dynamic["offset"], dynamic_end, dynamic["entsize"]):
        if off + 8 > len(blob):
            raise ValueError("ELF32 dynamic table is outside the file")
        tag, value = struct.unpack_from("<II", blob, off)
        if tag == 0:
            first_null = off
            break
    if first_null is None or first_null + 16 > dynamic_end:
        raise ValueError("ELF32 .dynamic has no spare entry for DT_NEEDED")
    if struct.unpack_from("<II", blob, first_null + 8) != (0, 0):
        raise ValueError("ELF32 .dynamic has no terminating DT_NULL after the spare entry")

    # Keep the donor symbol valid (as a duplicate alias) before reclaiming its
    # unique old name. No section, segment, address, or file offset moves.
    struct.pack_into("<I", blob, donor["entry_offset"], alias["name_offset"])
    blob[old_slot:old_slot + len(old_name) + 1] = (
        dependency + b"\0" * (len(old_name) + 1 - len(dependency))
    )
    struct.pack_into("<II", blob, first_null, 1, donor["name_offset"])
    return True


def default_needed_mode(abi, host_os=None):
    """Select a platform default while keeping both injectors available."""
    host_os = os.name if host_os is None else host_os
    if abi == ARM64:
        return "byte"
    return "inplace" if host_os == "nt" else "patchelf"


def main():
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("so", nargs="?", default="extracted/lib/arm64-v8a/libil2cpp.so",
                    help="path to the pristine libil2cpp.so for the chosen ABI")
    ap.add_argument("--abi", choices=[ARM64, ARMV7], default=ARM64,
                    help="which shipped ABI this library is (default: %(default)s)")
    ap.add_argument("--apply", action="store_true",
                    help="write the patched copy (default is verify-only)")
    ap.add_argument("--needed", choices=["byte", "inplace", "patchelf", "none"], default=None,
                    help="how to add the DT_NEEDED libdothook.so entry "
                         "(default: byte for arm64-v8a; in-place on Windows and "
                         "patchelf elsewhere for armeabi-v7a)")
    ap.add_argument("-o", "--out", default=None,
                    help="output path (default: <input>.patched.so)")
    args = ap.parse_args()

    needed = args.needed or default_needed_mode(args.abi)
    if needed == "byte" and args.abi != ARM64:
        sys.exit("[!] --needed byte is arm64-only (the cave offsets are specific to that binary)")
    if needed == "inplace" and args.abi != ARMV7:
        sys.exit("[!] --needed inplace is armeabi-v7a-only")

    targets = TARGETS[args.abi]
    md = capstone.Cs(*DISASM[args.abi])

    with open(args.so, "rb") as f:
        blob = bytearray(f.read())

    print(f"[*] {args.so} ({len(blob)} bytes)  abi={args.abi}  apply={args.apply}\n")
    for off, patch, name in targets:
        print(f"=== 0x{off:X}  {name}")
        print("  BEFORE:")
        for ins in md.disasm(bytes(blob[off:off + 20]), off):
            print(f"    0x{ins.address:X}: {ins.mnemonic:8} {ins.op_str}")
            if ins.address >= off + 16:
                break
        if args.apply:
            blob[off:off + len(patch)] = patch
            print("  AFTER:")
            for ins in md.disasm(bytes(blob[off:off + len(patch)]), off):
                print(f"    0x{ins.address:X}: {ins.mnemonic:8} {ins.op_str}")
        print()

    if not args.apply:
        print("[i] verify-only (pass --apply to write patched copy)")
        return

    out = args.out
    if out is None:
        out = args.so.replace(".so", ".patched.so") if ".patched" not in args.so else args.so

    if needed == "byte":
        inject_needed_bytes(blob)
        print("[+] re-injected DT_NEEDED libdothook.so (byte cave)")
    elif needed == "inplace":
        try:
            changed = inject_needed_armv7_inplace(blob)
        except ValueError as exc:
            sys.exit(f"[!] ARMv7 in-place DT_NEEDED injection failed: {exc}")
        if changed:
            print("[+] added DT_NEEDED libdothook.so (ARMv7 in-place, no layout shift)")
        else:
            print("[i] DT_NEEDED libdothook.so already present")
    with open(out, "wb") as f:
        f.write(blob)
    if needed == "patchelf":
        inject_needed_patchelf(out)
        print("[+] added DT_NEEDED libdothook.so (patchelf)")
    elif needed == "none":
        print("[!] no DT_NEEDED entry added; the runtime hook will NOT load")
    print(f"[+] wrote {out}")


if __name__ == "__main__":
    main()
