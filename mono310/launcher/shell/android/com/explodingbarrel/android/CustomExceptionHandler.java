package com.explodingbarrel.android;

/** Newly authored stand-in: there is no crash report to hand back. */
public final class CustomExceptionHandler {
    private static final CustomExceptionHandler INSTANCE = new CustomExceptionHandler();
    private CustomExceptionHandler() { }
    public static CustomExceptionHandler getInstance() { return INSTANCE; }
    /** Unity 2020 passes a lone null argument where Unity 5 passed none. */
    public static CustomExceptionHandler getInstance(Object ignored) { return INSTANCE; }
    public String getLastCrash() { return ""; }

    /** Folder the crash reporter would write into: the app's private files folder. */
    public String getDataDirectory() {
        try {
            return com.unity3d.player.UnityPlayer.currentActivity.getFilesDir().getAbsolutePath();
        } catch (Exception e) {
            return "";
        }
    }

    /** Debug hooks of the original crash reporter; deliberately do nothing. */
    public void throwException() { }
    public void throwExceptionOnThread() { }
}
