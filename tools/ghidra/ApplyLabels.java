//@category Transformers
/** Replaces tools/apply_labels.py by consuming il2cpp_out/labels.tsv. */
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.symbol.SourceType;

import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.ArrayList;
import java.util.List;

public class ApplyLabels extends GhidraScript {
    private static String unescape(String value) {
        StringBuilder out = new StringBuilder();
        for (int i = 0; i < value.length(); i++) {
            char c = value.charAt(i);
            if (c == '\\' && i + 1 < value.length()) {
                char next = value.charAt(++i);
                if (next == '\\') out.append('\\');
                else if (next == 't') out.append('\t');
                else if (next == 'r') out.append('\r');
                else if (next == 'n') out.append('\n');
                else { out.append('\\'); out.append(next); }
            } else {
                out.append(c);
            }
        }
        return out.toString();
    }

    public void run() throws Exception {
        String[] args = getScriptArgs();
        Path input = Path.of(args.length > 0 ? args[0] : "il2cpp_out/labels.tsv");
        List<String> methods = new ArrayList<>();
        List<String> strings = new ArrayList<>();
        for (String line : Files.readAllLines(input, StandardCharsets.UTF_8)) {
            if (line.startsWith("M\t")) methods.add(line);
            else if (line.startsWith("S\t")) strings.add(line);
        }

        Address base = currentProgram.getImageBase();
        monitor.initialize(methods.size());
        monitor.setMessage("method labels");
        int n = 0;
        for (String line : methods) {
            try {
                String[] field = line.split("\\t", -1);
                createLabel(base.add(Long.parseLong(field[1], 16)), unescape(field[2]), true,
                    SourceType.USER_DEFINED);
                n++;
            } catch (Exception e) {
            }
            monitor.incrementProgress(1);
        }
        println("method labels: " + n);

        monitor.initialize(strings.size());
        monitor.setMessage("string labels");
        int k = 0;
        for (String line : strings) {
            try {
                String[] field = line.split("\\t", -1);
                Address address = base.add(Long.parseLong(field[1], 16));
                createLabel(address, unescape(field[2]), true, SourceType.USER_DEFINED);
                setEOLComment(address, unescape(field[3]));
                k++;
            } catch (Exception e) {
            }
            monitor.incrementProgress(1);
        }
        println("string labels: " + k);
        println("apply_labels done");
    }
}
