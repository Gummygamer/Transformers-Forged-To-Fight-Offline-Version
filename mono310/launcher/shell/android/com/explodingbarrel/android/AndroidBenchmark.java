package com.explodingbarrel.android;

import android.app.ActivityManager;
import android.content.Context;
import com.unity3d.player.UnityPlayer;

/** Newly authored stand-in for the original benchmark plugin: reports a few device figures. */
public final class AndroidBenchmark {
    private AndroidBenchmark() { }

    public static String GetPath() { return "{}"; }

    // memory_class / memory_large_class are in MB. EB.AssetManager uses memory_large_class as its asset memory cap,
    // and PerformanceManager.IsLowMemoryDevice() is memory_large_class < 384. Without the key the cap became 0, so
    // every load evicted assets (blank or black textures, slow loads) and every phone ran the low-memory path that
    // destroys and rebuilds the quest board and the base on each screen change.
    public static String GetDeviceInfo() {
        long mem = 0;
        int memClass = 0, largeClass = 0;
        try {
            ActivityManager am = (ActivityManager) UnityPlayer.currentActivity.getSystemService(Context.ACTIVITY_SERVICE);
            ActivityManager.MemoryInfo mi = new ActivityManager.MemoryInfo();
            am.getMemoryInfo(mi);
            mem = mi.availMem;
            memClass = am.getMemoryClass();
            largeClass = am.getLargeMemoryClass();
        } catch (Exception ignored) { }
        return "{\"memory_available\":" + mem + ",\"memory_class\":" + memClass + ",\"memory_large_class\":" + largeClass
                + ",\"processors\":" + Runtime.getRuntime().availableProcessors()
                + ",\"wifi_signal_strength\":0,\"gles_version\":196610}";
    }
}
