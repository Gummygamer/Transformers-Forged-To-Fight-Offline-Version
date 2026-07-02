# Jython Ghidra postScript: find functions that reference given addresses
# (needs analysis done). Reads hex VAs (Ghidra base 0x10000) from xref_targets.txt.
from ghidra.program.model.symbol import RefType
fm = currentProgram.getFunctionManager()
af = currentProgram.getAddressFactory()
out = open("il2cpp_out/xrefs_out.txt", "w")
for line in open("il2cpp_out/xref_targets.txt"):
    line = line.strip()
    if not line or line.startswith("#"):
        continue
    addr = af.getAddress(line.split()[0])
    out.write("=== refs to %s ===\n" % line)
    refs = getReferencesTo(addr)
    seen = set()
    for r in refs:
        fa = r.getFromAddress()
        f = fm.getFunctionContaining(fa)
        name = f.getName() if f else "?"
        start = f.getEntryPoint() if f else fa
        key = str(start)
        if key in seen:
            continue
        seen.add(key)
        out.write("  %s  %s  (from %s)\n" % (start, name, fa))
out.close()
print("find_xrefs done -> xrefs_out.txt")
