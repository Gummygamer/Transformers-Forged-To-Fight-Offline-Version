//@category Transformers
/** Replaces tools/light_analyze.py with a JVM-hosted GhidraScript. */
import ghidra.app.script.GhidraScript;

public class LightAnalyze extends GhidraScript {
    public void run() throws Exception {
        String[] analyzers = {
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
            "Subroutine References"
        };
        for (String name : analyzers) {
            try {
                setAnalysisOption(currentProgram, name, "false");
            } catch (Exception e) {
            }
        }
        println("light_analyze: disabled heavy analyzers");
    }
}
