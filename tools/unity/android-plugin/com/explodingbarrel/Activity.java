package com.explodingbarrel;

import android.content.Intent;
import android.content.pm.PackageManager;
import android.net.Uri;
import android.os.Build;
import android.database.Cursor;
import android.os.ParcelFileDescriptor;
import android.provider.OpenableColumns;
import android.provider.Settings;
import com.unity3d.player.UnityPlayer;
import com.unity3d.player.UnityPlayerActivity;
import java.util.HashMap;

/**
 * Shim for the original 2.0.2 launcher activity. The original subclassed Unity
 * 5's UnityPlayerNativeActivity, which does not exist in Unity 2020; the
 * restored original manifest still names this class. It carries only the
 * permission bridge EB.Permissions calls through JNI.
 */
public class Activity extends UnityPlayerActivity {
    private final HashMap<Integer, String[]> requests = new HashMap<>();
    private final HashMap<Integer, String> receivers = new HashMap<>();

    /** csv of permission keys -> csv (mode 0) or "key,Result key,Result" (mode 1) of ungranted ones. */
    public String checkPermissionsArray(String csv, int withInfo) {
        StringBuilder out = new StringBuilder();
        for (String key : csv.split(",")) {
            if (key.isEmpty() || checkSelf(key)) continue;
            if (out.length() > 0) out.append(withInfo == 1 ? " " : ",");
            out.append(key);
            if (withInfo == 1) {
                boolean rationale = Build.VERSION.SDK_INT >= 23 && shouldShowRequestPermissionRationale(androidName(key));
                out.append(',').append(rationale ? "Ungranted" : "UngrantedWithoutPermissionRationale");
            }
        }
        return out.toString();
    }

    /** EB.Permissions passes short keys ("write_external_storage"); Android wants full names. */
    private static String androidName(String key) {
        return key.indexOf('.') >= 0 ? key : "android.permission." + key.toUpperCase();
    }

    private boolean checkSelf(String key) {
        String name = androidName(key);
        // Shared-storage write access is not a runtime permission on API 30+.
        if (Build.VERSION.SDK_INT >= 30 && name.endsWith("WRITE_EXTERNAL_STORAGE")) return true;
        return Build.VERSION.SDK_INT < 23
                || checkSelfPermission(name) == PackageManager.PERMISSION_GRANTED;
    }

    public void requestAppPermissionsArray(String csv, String receiver, int requestCode) {
        String[] keys = csv.split(",");
        String[] names = new String[keys.length];
        for (int i = 0; i < keys.length; i++) names[i] = androidName(keys[i]);
        requests.put(requestCode, keys);
        receivers.put(requestCode, receiver);
        if (Build.VERSION.SDK_INT >= 23) requestPermissions(names, requestCode);
        else sendResult(requestCode, true);
    }

    @Override
    public void onRequestPermissionsResult(int requestCode, String[] permissions, int[] results) {
        boolean granted = results.length > 0;
        for (int r : results) granted &= r == PackageManager.PERMISSION_GRANTED;
        sendResult(requestCode, granted);
    }

    private void sendResult(int requestCode, boolean granted) {
        String receiver = receivers.remove(requestCode);
        requests.remove(requestCode);
        if (receiver != null) {
            UnityPlayer.UnitySendMessage(receiver,
                    granted ? "OnPermissionGranted" : "OnPermissionDenied", String.valueOf(requestCode));
        }
    }

    // ---- launcher file picking (Storage Access Framework): the user supplies the base game APKs ----
    private static final int PICK_BASE = 7000;
    private final Uri[] picked = new Uri[2];

    /** slot 0 = the 3.1.0 APK, slot 1 = the 9.2.0 APK. Result arrives as UnitySendMessage(TftfLauncher, OnPicked, "slot|name"). */
    public void tftfPickFile(int slot) {
        Intent i = new Intent(Intent.ACTION_OPEN_DOCUMENT);
        i.addCategory(Intent.CATEGORY_OPENABLE);
        i.setType("*/*");
        startActivityForResult(i, PICK_BASE + slot);
    }

    @Override
    protected void onActivityResult(int requestCode, int resultCode, Intent data) {
        super.onActivityResult(requestCode, resultCode, data);
        int slot = requestCode - PICK_BASE;
        if (slot < 0 || slot > 1) return;
        if (resultCode != RESULT_OK || data == null || data.getData() == null) {
            UnityPlayer.UnitySendMessage("TftfLauncher", "OnPicked", slot + "|");
            return;
        }
        picked[slot] = data.getData();
        String name = "file";
        try (Cursor c = getContentResolver().query(picked[slot], null, null, null, null)) {
            if (c != null && c.moveToFirst()) {
                int col = c.getColumnIndex(OpenableColumns.DISPLAY_NAME);
                if (col >= 0) name = c.getString(col);
            }
        } catch (Exception ignored) { }
        UnityPlayer.UnitySendMessage("TftfLauncher", "OnPicked", slot + "|" + name);
    }

    /** A seekable file descriptor for the picked file, usable as /proc/self/fd/N; -1 when it cannot be opened. */
    public int tftfOpenPicked(int slot) {
        try {
            ParcelFileDescriptor pfd = getContentResolver().openFileDescriptor(picked[slot], "r");
            return pfd == null ? -1 : pfd.detachFd();
        } catch (Exception e) {
            return -1;
        }
    }

    /** Starts the app again in a fresh process (used once after the first-run installation). */
    public void tftfRestart() {
        Intent i = getPackageManager().getLaunchIntentForPackage(getPackageName());
        if (i == null) return;
        i.addFlags(Intent.FLAG_ACTIVITY_CLEAR_TOP | Intent.FLAG_ACTIVITY_NEW_TASK);
        startActivity(i);
        finish();
        new android.os.Handler().postDelayed(new Runnable() {
            public void run() { System.exit(0); }
        }, 300);
    }

    public void launchAppSettings() {
        Intent i = new Intent(Settings.ACTION_APPLICATION_DETAILS_SETTINGS,
                Uri.fromParts("package", getPackageName(), null));
        startActivity(i);
    }
}
