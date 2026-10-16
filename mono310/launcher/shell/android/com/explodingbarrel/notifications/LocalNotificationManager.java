package com.explodingbarrel.notifications;

import android.app.Activity;

/** Newly authored stand-in: local notifications are not scheduled by the offline game. */
public final class LocalNotificationManager {
    private LocalNotificationManager() { }
    public static void clearAllNotifications(Activity activity) { }
    public static String scheduleLocalNotification(int when, String category, String message, String uniqueName,
                                                   String groupName, String summary, String metaData, Activity activity) {
        return "";
    }
    public static void cancelNotification(Activity activity, String uniqueName) { }
    public static void cancelNotifications(Activity activity, Object uniqueNames) { }
}
