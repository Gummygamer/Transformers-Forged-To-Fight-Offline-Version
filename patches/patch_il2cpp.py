#!/usr/bin/env python3
"""
Patch libil2cpp.so (arm64) to defeat EB.Net cert pinning so our local fake
Sparx server's TLS cert is accepted. RVA == file offset for this binary.

Targets (from IL2CPP dump):
  0x123A73C  EB.Net.TcpClientSSL.CertificateValidation(...) -> bool   => return true
  0x14EC940  EB.Net.TcpClientBouncy.MyTlsAuthentication.NotifyServerCertificate(...) -> void => no-op

arm64 stubs:
  return true : movz w0,#1 ; ret  =  20 00 80 52  c0 03 5f d6
  no-op void  : ret              =  c0 03 5f d6
"""
import sys, capstone

SO = sys.argv[1] if len(sys.argv) > 1 else "extracted/lib/arm64-v8a/libil2cpp.so"
APPLY = "--apply" in sys.argv

RET = bytes.fromhex("c0035fd6")
NOP = bytes.fromhex("1f2003d5")
TRUE_RET = bytes.fromhex("20008052") + RET
# b #+0x24 : at 0xFC21B4 jump into the manager-registration block, past the
# OTAConfig.flag deref, so UserManager (& ~40 managers) register even when
# Hub.Config.OTAConfig is null (the dead-server offline state).
B_FC21D8 = bytes.fromhex("09000014")

TARGETS = [
    (0x123A73C, TRUE_RET, "TcpClientSSL.CertificateValidation -> true"),
    (0x14EC940, RET,      "TcpClientBouncy.NotifyServerCertificate -> noop"),
    (0xFC21B4, B_FC21D8,  "Hub.InitializeComponents -> register managers past OTAConfig gate"),
    # _PostInit coroutine: 'cbz w9' at 0x162B690 = if listToAuthenticate.Count==0 -> _LoginFailed
    # ("no valid authenticators found"). NOP it so count==0 falls through to the success
    # (SetState 2 / authenticate) path -> login completes with our device session (stoken).
    (0x162B690, NOP,      "LoginManager._PostInit -> skip 'no valid authenticators' fail on Count==0"),
    # Hub.SubSystemConnecting: 'cmp w8,#3; b.eq 0xFC3850' = if a subsystem reaches
    # error-state 3 -> FatalError(ID_SPARX_ERROR_UNKNOWN) = "FAILED TO LOG IN". NOP the
    # b.eq so a failed subsystem falls through as connected and login completes (grind
    # past the gate; the failed subsystem's features are degraded but boot proceeds).
    (0xFC3718, NOP,       "Hub.SubSystemConnecting -> skip FatalError on subsystem error-state 3"),
    # SubSystem.FatalError sets state=3 (handled by the 0xFC3718 skip) then TAIL-CALLs
    # Hub.FatalError (`b 0xfc0734`) which pops the "FAILED TO LOG IN" dialog directly,
    # bypassing the polling skip. Replace the tail-call with `ret` so a subsystem fatal
    # just sets state 3 and returns silently -> the Hub treats it as connected and the
    # boot grinds past data gates (e.g. "No User Data") to the next screen/FTE.
    (0x122C680, RET,      "SubSystem.FatalError -> ret instead of tail-call Hub.FatalError (silence subsystem fatals)"),
]

md = capstone.Cs(capstone.CS_ARCH_ARM64, capstone.CS_MODE_LITTLE_ENDIAN)

with open(SO, "rb") as f:
    blob = bytearray(f.read())

print(f"[*] {SO} ({len(blob)} bytes)  apply={APPLY}\n")
for off, patch, name in TARGETS:
    print(f"=== 0x{off:X}  {name}")
    print("  BEFORE:")
    for ins in md.disasm(bytes(blob[off:off+20]), off):
        print(f"    0x{ins.address:X}: {ins.mnemonic:8} {ins.op_str}")
        if ins.address >= off + 16:
            break
    if APPLY:
        blob[off:off+len(patch)] = patch
        print("  AFTER:")
        for ins in md.disasm(bytes(blob[off:off+len(patch)]), off):
            print(f"    0x{ins.address:X}: {ins.mnemonic:8} {ins.op_str}")
    print()

if APPLY:
    # Re-inject DT_NEEDED "libdothook.so" so the runtime hook actually loads. This script
    # patches the PRISTINE lib (which has no such dependency); without re-adding it the hook
    # is never mapped and login hangs. Bytes verified by diffing the known-good hooked lib:
    #   - "libdothook.so" written into a zero cave inside .dynstr @ 0x7D3034
    #   - a .dynamic slot turned into DT_NEEDED: d_tag=1 @ 0x2BA9258, d_val=.dynstr off @ 0x2BA9260
    blob[0x7D3034:0x7D3034+13] = b"libdothook.so"
    blob[0x2BA9258] = 0x01
    blob[0x2BA9260:0x2BA9263] = bytes.fromhex("5cfe7b")
    print("[+] re-injected DT_NEEDED libdothook.so")
    out = SO.replace(".so", ".patched.so") if ".patched" not in SO else SO
    with open(out, "wb") as f:
        f.write(blob)
    print(f"[+] wrote {out}")
else:
    print("[i] verify-only (pass --apply to write patched copy)")
