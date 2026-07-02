# Jython Ghidra PRE-script: disable the memory/time-heavy analyzers so whole-binary
# analysis completes on the 48MB il2cpp .so, while keeping the REFERENCE analyzers
# (ADRP+LDR -> address) that produce string xrefs.
heavy = [
    "Decompiler Parameter ID",
    "Decompiler Switch Analysis",
    "Aggressive Instruction Finder",
    "Call Convention ID",
    "Stack",
    "Variadic Function Signature Override",
    "Non-Returning Functions - Discovered",
    "Create Address Tables",
    "Shared Return Calls",
    "Function Start Search",
    "Demangler GNU",
    "Embedded Media",
    "Subroutine References",
]
for name in heavy:
    try:
        setAnalysisOption(currentProgram, name, "false")
    except:
        pass
print("light_analyze: disabled heavy analyzers")
