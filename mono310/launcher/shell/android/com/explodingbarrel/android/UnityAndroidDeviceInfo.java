package com.explodingbarrel.android;

import android.app.Activity;
import android.content.Context;
import android.os.Build;
import android.provider.Settings;
import android.telephony.TelephonyManager;
import com.unity3d.player.UnityPlayer;

/** Newly authored stand-in for the original device-information plugin: the game only reads these strings. */
public final class UnityAndroidDeviceInfo {
    private UnityAndroidDeviceInfo() { }

    private static String esc(String s) {
        return s == null ? "" : s.replace("\\", "\\\\").replace("\"", "\\\"");
    }

    public static String GetDeviceInfo() {
        return "{\"model\":\"" + esc(Build.MODEL) + "\",\"product\":\"" + esc(Build.PRODUCT) + "\",\"device\":\""
                + esc(Build.DEVICE) + "\",\"display\":\"" + esc(Build.DISPLAY) + "\",\"bootloader\":\""
                + esc(Build.BOOTLOADER) + "\",\"brand\":\"" + esc(Build.BRAND) + "\",\"manufacturer\":\""
                + esc(Build.MANUFACTURER) + "\",\"fingerprint\":\"" + esc(Build.FINGERPRINT) + "\"}";
    }

    public static String GetDeviceID() {
        Activity a = UnityPlayer.currentActivity;
        String id = a == null ? null : Settings.Secure.getString(a.getContentResolver(), Settings.Secure.ANDROID_ID);
        return id == null ? "" : id;
    }

    public static String WifiMacAddress() { return ""; }

    public static String GetCarrier() {
        try {
            TelephonyManager t = (TelephonyManager) UnityPlayer.currentActivity.getSystemService(Context.TELEPHONY_SERVICE);
            String n = t == null ? null : t.getNetworkOperatorName();
            return n == null ? "" : n;
        } catch (Exception e) {
            return "";
        }
    }

    public static String GetPackageInstallerPlatform() { return "sideload"; }
    public static String GetAndroidVersion() { return Build.VERSION.RELEASE; }
    public static int GetAndroidSDKVersion() { return Build.VERSION.SDK_INT; }
    public static String GetAdvertisingInfo() { return "{\"id\":\"\",\"limitAdTracking\":true}"; }
    public static String GetBuildNumber() { return Build.DISPLAY; }
}
