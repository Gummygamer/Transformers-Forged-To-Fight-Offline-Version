# Jython Ghidra postScript: decompile the functions whose hex offsets are listed
# in decompile_targets.txt (one 0x... per line) and write C to decomp_out.c.
# Run against the already-labeled project (-process), so calls show names.
from ghidra.app.decompiler import DecompInterface
from ghidra.util.task import ConsoleTaskMonitor

TARGETS = "il2cpp_out/decompile_targets.txt"
OUT = "il2cpp_out/decomp_out.c"

base = currentProgram.getImageBase()
fm = currentProgram.getFunctionManager()
di = DecompInterface()
di.openProgram(currentProgram)

targets = []
for line in open(TARGETS):
    line = line.strip()
    if line and not line.startswith("#"):
        targets.append(int(line.split()[0], 16))

out = open(OUT, "w")
for t in targets:
    a = base.add(t)
    try:
        disassemble(a)
    except:
        pass
    f = getFunctionAt(a)
    if f is None:
        try:
            f = createFunction(a, None)
        except:
            f = None
    if f is None:
        out.write("// ===== 0x%x: could not create function =====\n\n" % t)
        continue
    res = di.decompileFunction(f, 180, ConsoleTaskMonitor())
    out.write("// ===== %s @ 0x%x =====\n" % (f.getName(), t))
    if res and res.decompileCompleted():
        out.write(res.getDecompiledFunction().getC())
    else:
        out.write("// decompile failed: %s\n" % (res.getErrorMessage() if res else "no result"))
    out.write("\n\n")
out.close()
print("decompile_targets done -> decomp_out.c")
