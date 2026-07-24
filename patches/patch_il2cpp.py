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
import shutil
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

    This is the only supported route for armv7: the byte-cave offsets above are
    specific to the arm64 binary and have no armv7 equivalent.
    """
    exe = shutil.which("patchelf")
    if not exe:
        sys.exit("[!] patchelf not found on PATH; install it or pass --needed none "
                 "and add the DT_NEEDED entry yourself")
    subprocess.run([exe, "--add-needed", "libdothook.so", path], check=True)


def main():
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("so", nargs="?", default="extracted/lib/arm64-v8a/libil2cpp.so",
                    help="path to the pristine libil2cpp.so for the chosen ABI")
    ap.add_argument("--abi", choices=[ARM64, ARMV7], default=ARM64,
                    help="which shipped ABI this library is (default: %(default)s)")
    ap.add_argument("--apply", action="store_true",
                    help="write the patched copy (default is verify-only)")
    ap.add_argument("--needed", choices=["byte", "patchelf", "none"], default=None,
                    help="how to add the DT_NEEDED libdothook.so entry "
                         "(default: byte for arm64-v8a, patchelf for armeabi-v7a)")
    ap.add_argument("-o", "--out", default=None,
                    help="output path (default: <input>.patched.so)")
    args = ap.parse_args()

    needed = args.needed or ("byte" if args.abi == ARM64 else "patchelf")
    if needed == "byte" and args.abi != ARM64:
        sys.exit("[!] --needed byte is arm64-only (the cave offsets are specific to that binary)")

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
