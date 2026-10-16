package com.explodingbarrel.android;

import java.io.File;
import java.io.FileOutputStream;
import java.io.InputStream;
import java.io.OutputStream;
import java.util.zip.ZipEntry;
import java.util.zip.ZipFile;

/** Newly authored: extracts one entry of a zip/APK to a file, in steps the game calls from its update loop. */
public final class JarExtraction {
    private final String jarPath, entryName, outputPath;
    private ZipFile zip;
    private InputStream in;
    private OutputStream out;

    public JarExtraction(String jarPath, String entryName, String outputPath) {
        this.jarPath = jarPath;
        this.entryName = entryName;
        this.outputPath = outputPath;
    }

    public boolean StartExtractFromJar() {
        try {
            zip = new ZipFile(jarPath);
            ZipEntry e = zip.getEntry(entryName);
            if (e == null) { CompleteExtractFromJar(); return false; }
            File f = new File(outputPath);
            if (f.getParentFile() != null) f.getParentFile().mkdirs();
            in = zip.getInputStream(e);
            out = new FileOutputStream(f);
            return true;
        } catch (Exception ex) {
            CompleteExtractFromJar();
            return false;
        }
    }

    /** Copies up to maxBytes; true when the entry is finished. */
    public boolean StepExtractFromJar(int maxBytes) {
        try {
            byte[] buf = new byte[Math.max(1, Math.min(maxBytes, 1 << 16))];
            int left = maxBytes;
            while (left > 0) {
                int n = in.read(buf, 0, Math.min(buf.length, left));
                if (n < 0) return true;
                out.write(buf, 0, n);
                left -= n;
            }
            return false;
        } catch (Exception ex) {
            return true;
        }
    }

    public void CompleteExtractFromJar() {
        try { if (out != null) out.close(); } catch (Exception ignored) { }
        try { if (in != null) in.close(); } catch (Exception ignored) { }
        try { if (zip != null) zip.close(); } catch (Exception ignored) { }
        out = null; in = null; zip = null;
    }
}
