#!/usr/bin/env python3
"""Find functions that reference a given string-literal ADDRESS by decoding
ADRP (+ following ldr/add with matching lo12). Maps to containing function.
  python find_str_ref.py 0x2D60D78
"""
import sys, json, bisect

SO = "extracted/lib/arm64-v8a/libil2cpp.so"
target = int(sys.argv[1], 16)
page = target & ~0xFFF
lo12 = target & 0xFFF

blob = open(SO, "rb").read()
sj = json.load(open("il2cpp_out/script.json"))
fmap = {m["Address"]: m["Name"] for m in sj["ScriptMethod"]}
starts = sorted(set(list(fmap.keys()) + list(sj["Addresses"])))

def containing(off):
    i = bisect.bisect_right(starts, off) - 1
    if i < 0: return (0, "?")
    s = starts[i]; return s, fmap.get(s, "sub_%x" % s)

def w32(o):
    return blob[o] | (blob[o+1] << 8) | (blob[o+2] << 16) | (blob[o+3] << 24)

hits = set()
n = len(blob) - (len(blob) % 4)
for off in range(0, n, 4):
    w = w32(off)
    if (w & 0x9F000000) == 0x90000000:  # ADRP
        immlo = (w >> 29) & 3
        immhi = (w >> 5) & 0x7FFFF
        imm = ((immhi << 2) | immlo)
        if imm & (1 << 20): imm -= (1 << 21)
        tgt_page = (off & ~0xFFF) + (imm << 12)
        if tgt_page == page:
            rd = w & 0x1F
            # scan next 6 instrs for ldr/add using rd with lo12
            for k in range(1, 7):
                w2 = w32(off + 4*k)
                # ADD imm (lo12): 0x91000000 ; LDR imm unsigned offset (64) 0xF9400000 / (32) 0xB9400000
                rn = (w2 >> 5) & 0x1F
                if rn != rd: continue
                if (w2 & 0xFF800000) == 0x91000000:  # ADD imm
                    if ((w2 >> 10) & 0xFFF) == lo12: hits.add(off); break
                if (w2 & 0xFFC00000) in (0xF9400000, 0xB9400000):  # LDR uimm
                    sh = 3 if (w2 & 0xFFC00000) == 0xF9400000 else 2
                    if (((w2 >> 10) & 0xFFF) << sh) == lo12: hits.add(off); break

print(f"=== references to 0x{target:X}: {len(hits)} ===")
seen = {}
for off in sorted(hits):
    s, name = containing(off)
    seen.setdefault((s, name), []).append(off)
for (s, name), offs in sorted(seen.items()):
    print(f"  0x{s:X}  {name}   (ref@ {', '.join('0x%x'%o for o in offs[:3])})")
