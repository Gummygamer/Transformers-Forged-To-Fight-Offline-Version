package com.explodingbarrel;

import java.io.BufferedReader;
import java.io.File;
import java.io.FileOutputStream;
import java.io.InputStreamReader;
import java.io.OutputStreamWriter;
import java.io.PrintWriter;
import java.nio.charset.StandardCharsets;

/** Starts the bundled Legible interpreter through Android's supported process API. */
public final class TftfProcess {
    private TftfProcess() {}

    public static Process start(String executable, String workingDirectory, String logPath,
                                String odrPath, String port) throws Exception {
        ProcessBuilder builder = new ProcessBuilder(executable, "run", "Server/run_local.lbl");
        builder.directory(new File(workingDirectory));
        builder.redirectErrorStream(true);
        builder.environment().put("HTTP_PORT", port);
        builder.environment().put("LEGIBLE_BIND", "127.0.0.1");
        builder.environment().put("TFTF_TUTORIAL_BLOCKLIST", "");
        builder.environment().put("TFTF_ODR_DIR", odrPath);
        final Process child = builder.start();
        final File log = new File(logPath);
        Thread logger = new Thread(new Runnable() {
            @Override public void run() {
                try (BufferedReader input = new BufferedReader(new InputStreamReader(
                            child.getInputStream(), StandardCharsets.UTF_8));
                     PrintWriter output = new PrintWriter(new OutputStreamWriter(
                            new FileOutputStream(log, false), StandardCharsets.UTF_8), true)) {
                    String line;
                    while ((line = input.readLine()) != null) output.println(line);
                } catch (Exception e) {
                    android.util.Log.e("TftfProcess", "Could not capture server output", e);
                }
            }
        }, "TftfServerLog");
        logger.setDaemon(true);
        logger.start();
        return child;
    }

    public static void stop(Process child) {
        if (child != null) child.destroy();
    }
}
