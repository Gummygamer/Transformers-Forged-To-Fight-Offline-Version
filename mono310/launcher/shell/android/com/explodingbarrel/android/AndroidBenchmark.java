package com.explodingbarrel.android;

import android.app.ActivityManager;
import android.content.Context;
import com.unity3d.player.UnityPlayer;

/** Newly authored stand-in for the original benchmark plugin: reports a few device figures. */
public final class AndroidBenchmark {
    private AndroidBenchmark() { }

    public static String GetPath() { return "{}"; }

    public static String GetDeviceInfo() {
        long mem = 0;
        try {
            ActivityManager am = (ActivityManager) UnityPlayer.currentActivity.getSystemService(Context.ACTIVITY_SERVICE);
            ActivityManager.MemoryInfo mi = new ActivityManager.MemoryInfo();
            am.getMemoryInfo(mi);
            mem = mi.availMem;
        } catch (Exception ignored) { }
        return "{\"memory_available\":" + mem + ",\"processors\":" + Runtime.getRuntime().availableProcessors()
                + ",\"wifi_signal_strength\":0,\"gles_version\":196610}";
    }
}
