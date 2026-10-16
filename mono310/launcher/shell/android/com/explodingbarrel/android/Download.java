package com.explodingbarrel.android;

import com.unity3d.player.UnityPlayer;
import java.io.File;
import java.io.FileOutputStream;
import java.io.InputStream;
import java.io.OutputStream;
import java.net.HttpURLConnection;
import java.net.URL;
import java.util.concurrent.ConcurrentHashMap;

/** Newly authored: file downloads for the game's asset cache. Results go to UnityObject.OnDownloadResult as json. */
public final class Download {
    private final String handler;
    private final ConcurrentHashMap<String, Float> progress = new ConcurrentHashMap<>();
    private volatile boolean stop;

    public Download(String handler) { this.handler = handler; }

    public void RegisterReceivers() { stop = false; }
    public void UnregisterReceivers() { }
    public void StopPrefetchDownloads() { stop = true; }

    public float GetProgress(String url) {
        Float p = progress.get(url);
        return p == null ? 0f : p;
    }

    public void ToFileTask(final String url, final long expectedSize, final String path) { start(url, expectedSize, path); }
    public void ToFileService(final String url, final long expectedSize, final String path) { start(url, expectedSize, path); }

    private void start(final String url, final long expectedSize, final String path) {
        new Thread(new Runnable() {
            public void run() {
                String err = "";
                try {
                    HttpURLConnection c = (HttpURLConnection) new URL(url).openConnection();
                    c.setConnectTimeout(15000);
                    c.setReadTimeout(30000);
                    long total = c.getContentLengthLong();
                    File f = new File(path);
                    if (f.getParentFile() != null) f.getParentFile().mkdirs();
                    File tmp = new File(path + ".part");
                    try (InputStream in = c.getInputStream(); OutputStream out = new FileOutputStream(tmp)) {
                        byte[] buf = new byte[1 << 16];
                        long done = 0;
                        int n;
                        while ((n = in.read(buf)) > 0) {
                            if (stop) throw new Exception("stopped");
                            out.write(buf, 0, n);
                            done += n;
                            if (total > 0) progress.put(url, (float) done / total);
                        }
                    }
                    if (f.exists()) f.delete();
                    if (!tmp.renameTo(f)) throw new Exception("rename failed");
                    progress.put(url, 1f);
                } catch (Exception e) {
                    err = String.valueOf(e.getMessage());
                }
                String json = "{\"url\":\"" + url.replace("\\", "\\\\").replace("\"", "\\\"") + "\",\"file\":\""
                        + path.replace("\\", "\\\\").replace("\"", "\\\"") + "\",\"err\":\""
                        + err.replace("\\", "\\\\").replace("\"", "\\\"") + "\"}";
                UnityPlayer.UnitySendMessage(handler, "OnDownloadResult", json);
            }
        }).start();
    }
}
