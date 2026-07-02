#!/usr/bin/env python3
"""Annotated arm64 disassembly of one il2cpp function: resolves bl targets to
method names and adrp/ldr/add string-literal loads to their string values,
using Il2CppDumper's script.json. RVA == file offset for this binary.

  python disasm_fn.py <offset_hex> [num_bytes]
"""
import sys, json, capstone

SO = "extracted/lib/arm64-v8a/libil2cpp.so"
OFF = int(sys.argv[1], 16)
N = int(sys.argv[2], 16) if len(sys.argv) > 2 else 0x500

print("[*] loading script.json ...", file=sys.stderr)
sj = json.load(open("il2cpp_out/script.json"))
meth = {m["Address"]: m["Name"] for m in sj["ScriptMethod"]}
strs = {s["Address"]: s["Value"] for s in sj["ScriptString"]}
metad = {m["Address"]: m["Name"] for m in sj.get("ScriptMetadata", [])}
print(f"[*] {len(meth)} methods, {len(strs)} strings", file=sys.stderr)

with open(SO, "rb") as f:
    f.seek(OFF); code = f.read(N)

md = capstone.Cs(capstone.CS_ARCH_ARM64, capstone.CS_MODE_LITTLE_ENDIAN)
md.detail = True
adrp = {}  # reg -> page base
for ins in md.disasm(code, OFF):
    note = ""
    m, ops = ins.mnemonic, ins.op_str
    if m == "adrp" and len(ins.operands) == 2:
        adrp[ins.operands[0].reg] = ins.operands[1].imm
    elif m in ("add", "ldr") and len(ins.operands) >= 2:
        dst = ins.operands[0].reg
        src = ins.operands[1].reg if ins.operands[1].type == capstone.CS_OP_REG else None
        if src in adrp:
            base = adrp[src]
            imm = 0
            if m == "add" and ins.operands[2].type == capstone.CS_OP_IMM:
                imm = ins.operands[2].imm
            elif m == "ldr" and ins.operands[1].type == capstone.CS_OP_MEM:
                imm = ins.operands[1].mem.disp
            addr = base + imm
            if addr in strs:
                note = f'  ; STR "{strs[addr]}"'
            elif addr in metad:
                note = f"  ; META {metad[addr]}"
            elif addr in meth:
                note = f"  ; &{meth[addr]}"
            if m == "add":
                adrp[dst] = addr  # carry resolved address
    elif m in ("bl", "b") and ins.operands and ins.operands[0].type == capstone.CS_OP_IMM:
        t = ins.operands[0].imm
        if t in meth:
            note = f"  ; -> {meth[t]}"
    print(f"0x{ins.address:X}: {m:7} {ops}{note}")
    if m == "ret":
        break
