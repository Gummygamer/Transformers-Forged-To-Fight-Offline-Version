//@category Transformers
/** Replaces tools/decompile_targets.py with a JVM-hosted GhidraScript. */
import ghidra.app.decompiler.DecompInterface;
import ghidra.app.decompiler.DecompileResults;
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Function;
import ghidra.util.task.ConsoleTaskMonitor;

import java.io.BufferedWriter;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;

public class DecompileTargets extends GhidraScript {
    public void run() throws Exception {
        String[] args = getScriptArgs();
        Path input = Path.of(args.length > 0 ? args[0] : "il2cpp_out/decompile_targets.norm.txt");
        Path output = Path.of(args.length > 1 ? args[1] : "il2cpp_out/decomp_out.c");
        Address base = currentProgram.getImageBase();
        try (BufferedWriter writer = Files.newBufferedWriter(output, StandardCharsets.UTF_8)) {
            for (String line : Files.readAllLines(input, StandardCharsets.UTF_8)) {
                String value = line.trim();
                if (value.isEmpty()) continue;
                long target = Long.parseLong(value.substring(2), 16);
                Address address = base.add(target);
                try {
                    disassemble(address);
                } catch (Exception e) {
                }
                Function function = getFunctionAt(address);
                if (function == null) {
                    try {
                        function = createFunction(address, null);
                    } catch (Exception e) {
                        function = null;
                    }
                }
                if (function == null) {
                    writer.write("// ===== 0x" + Long.toHexString(target)
                        + ": could not create function =====\n\n");
                    continue;
                }
                DecompInterface decompiler = new DecompInterface();
                DecompileResults result = decompiler.decompileFunction(function, 180,
                    new ConsoleTaskMonitor());
                writer.write("// ===== " + function.getName() + " @ 0x"
                    + Long.toHexString(target) + " =====\n");
                if (result != null && result.decompileCompleted()) {
                    writer.write(result.getDecompiledFunction().getC());
                } else {
                    writer.write("// decompile failed: "
                        + (result != null ? result.getErrorMessage() : "no result") + "\n");
                }
                writer.write("\n\n");
            }
        }
        println("decompile_targets done -> decomp_out.c");
    }
}
