#!/usr/bin/env python3
"""Find all arm64 BL callers of a target function in libil2cpp.so (RVA==offset).
Maps each call site to its containing function via script.json Addresses.
  python find_callers.py 0x146DA90 [0x146EF54 ...]
"""
import sys, json, bisect

SO = "extracted/lib/arm64-v8a/libil2cpp.so"
targets = [int(x, 16) for x in sys.argv[1:]]

blob = open(SO, "rb").read()
sj = json.load(open("il2cpp_out/script.json"))
# function starts -> name
fmap = {m["Address"]: m["Name"] for m in sj["ScriptMethod"]}
starts = sorted(set(list(fmap.keys()) + [a for a in sj["Addresses"]]))

def containing(off):
    i = bisect.bisect_right(starts, off) - 1
    if i < 0: return None
    s = starts[i]
    return s, fmap.get(s, "sub_%x" % s)

import struct
def s26(v):
    return v - (1 << 26) if v & (1 << 25) else v

# limit scan to plausible .text (skip the huge data tail); scan whole file aligned
res = {t: [] for t in targets}
n = len(blob) - (len(blob) % 4)
for off in range(0, n, 4):
    w = blob[off] | (blob[off+1] << 8) | (blob[off+2] << 16) | (blob[off+3] << 24)
    if (w >> 26) == 0b100101:  # BL
        tgt = off + (s26(w & 0x3FFFFFF) << 2)
        if tgt in res:
            res[tgt].append(off)

for t in targets:
    print(f"\n=== callers of 0x{t:X} ({fmap.get(t,'?')}): {len(res[t])} ===")
    seen = {}
    for off in res[t]:
        c = containing(off)
        if c:
            seen.setdefault(c[1], []).append(off)
    for name, offs in sorted(seen.items()):
        print(f"  0x{containing(offs[0])[0]:X}  {name}   (call@ {', '.join('0x%x'%o for o in offs[:3])})")
