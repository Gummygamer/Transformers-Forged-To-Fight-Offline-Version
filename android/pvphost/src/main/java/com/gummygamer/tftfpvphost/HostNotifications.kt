package com.gummygamer.tftfpvphost

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.content.Context
import android.content.Intent
import android.app.PendingIntent
import androidx.core.app.NotificationCompat

/** Notification channel and the ongoing foreground notification. */
object HostNotifications {
    const val CHANNEL_ID = "tftf_pvp_host"
    const val NOTIFICATION_ID = 1

    fun ensureChannel(context: Context) {
        val manager = context.getSystemService(NotificationManager::class.java)
        val name = context.getString(R.string.notification_channel_name)
        manager.createNotificationChannel(
            NotificationChannel(CHANNEL_ID, name, NotificationManager.IMPORTANCE_LOW))
    }

    fun build(context: Context, host: String, port: Int, peers: Int): Notification {
        val stop = Intent(context, HostService::class.java).setAction(HostService.ACTION_STOP)
        val stopIntent = PendingIntent.getService(
            context, 0, stop, PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE)
        return NotificationCompat.Builder(context, CHANNEL_ID)
            .setSmallIcon(R.mipmap.ic_launcher)
            .setContentTitle(context.getString(R.string.notification_title))
            .setContentText(context.getString(R.string.notification_text, host, port, peers))
            .setOngoing(true)
            .addAction(R.mipmap.ic_launcher, context.getString(R.string.action_stop), stopIntent)
            .build()
    }
}
