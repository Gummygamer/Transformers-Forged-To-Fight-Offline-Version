//@category Transformers
/** Replaces tools/find_xrefs.py by writing portable raw reference records. */
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Function;
import ghidra.program.model.symbol.Reference;

import java.io.BufferedWriter;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.List;

public class FindXrefs extends GhidraScript {
    private static String escape(String value) {
        return value.replace("\\", "\\\\").replace("\t", "\\t")
            .replace("\r", "\\r").replace("\n", "\\n");
    }

    public void run() throws Exception {
        String[] args = getScriptArgs();
        Path input = Path.of(args.length > 0 ? args[0] : "il2cpp_out/xref_targets.norm.tsv");
        Path output = Path.of(args.length > 1 ? args[1] : "il2cpp_out/xrefs_raw.tsv");
        List<String> targets = Files.readAllLines(input, StandardCharsets.UTF_8);
        try (BufferedWriter writer = Files.newBufferedWriter(output, StandardCharsets.UTF_8)) {
            for (String line : targets) {
                String[] field = line.split("\\t", 2);
                if (field.length != 2 || field[0].isEmpty()) continue;
                writer.write("T\t" + field[1] + "\n");
                Address target = currentProgram.getAddressFactory().getAddress(field[0]);
                for (Reference reference : getReferencesTo(target)) {
                    Address fromAddress = reference.getFromAddress();
                    Function function = currentProgram.getFunctionManager()
                        .getFunctionContaining(fromAddress);
                    String name = function != null ? function.getName() : "?";
                    Address start = function != null ? function.getEntryPoint() : fromAddress;
                    writer.write("R\t" + start + "\t" + escape(name) + "\t" + fromAddress + "\n");
                }
            }
        }
        println("find_xrefs: wrote " + output);
    }
}
