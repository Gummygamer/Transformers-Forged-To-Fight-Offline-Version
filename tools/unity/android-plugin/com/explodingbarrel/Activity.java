package com.explodingbarrel;

import android.content.Intent;
import android.content.pm.PackageManager;
import android.net.Uri;
import android.os.Build;
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
                boolean rationale = Build.VERSION.SDK_INT >= 23 && shouldShowRequestPermissionRationale(key);
                out.append(',').append(rationale ? "Ungranted" : "UngrantedWithoutPermissionRationale");
            }
        }
        return out.toString();
    }

    private boolean checkSelf(String key) {
        return Build.VERSION.SDK_INT < 23
                || checkSelfPermission(key) == PackageManager.PERMISSION_GRANTED;
    }

    public void requestAppPermissionsArray(String csv, String receiver, int requestCode) {
        String[] keys = csv.split(",");
        requests.put(requestCode, keys);
        receivers.put(requestCode, receiver);
        if (Build.VERSION.SDK_INT >= 23) requestPermissions(keys, requestCode);
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

    public void launchAppSettings() {
        Intent i = new Intent(Settings.ACTION_APPLICATION_DETAILS_SETTINGS,
                Uri.fromParts("package", getPackageName(), null));
        startActivity(i);
    }
}
