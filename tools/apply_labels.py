# Jython (Python2) Ghidra headless postScript.
# Applies function-name labels (ScriptMethod) + string-literal labels (ScriptString)
# from Il2CppDumper script.json. No signatures/types/analysis -> fast. Project is
# saved so later runs decompile targets quickly.
import json

SCRIPT_JSON = "il2cpp_out/script.json"
base = currentProgram.getImageBase()
USER = ghidra.program.model.symbol.SourceType.USER_DEFINED

data = json.loads(open(SCRIPT_JSON, 'rb').read().decode('utf-8'))

sm = data.get("ScriptMethod", [])
monitor.initialize(len(sm))
monitor.setMessage("method labels")
n = 0
for m in sm:
    try:
        createLabel(base.add(m["Address"]), m["Name"].replace(' ', '-').encode('utf-8'), True, USER)
        n += 1
    except:
        pass
    monitor.incrementProgress(1)
print("method labels: %d" % n)

ss = data.get("ScriptString", [])
monitor.initialize(len(ss))
monitor.setMessage("string labels")
k = 0
for i, s in enumerate(ss):
    try:
        a = base.add(s["Address"])
        createLabel(a, "STR_%d" % i, True, USER)
        setEOLComment(a, s["Value"].encode('utf-8'))
        k += 1
    except:
        pass
    monitor.incrementProgress(1)
print("string labels: %d" % k)
print("apply_labels done")
