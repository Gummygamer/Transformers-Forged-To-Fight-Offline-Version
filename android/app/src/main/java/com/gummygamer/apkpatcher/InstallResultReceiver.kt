package com.gummygamer.apkpatcher

import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.pm.PackageInstaller
import android.os.Build

/**
 * Receives the result of a PackageInstaller session commit.
 * The system sends this broadcast after the user approves/denies the install dialog.
 */
class InstallResultReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) {
        val status = intent.getIntExtra(PackageInstaller.EXTRA_STATUS, PackageInstaller.STATUS_FAILURE)
        val message = intent.getStringExtra(PackageInstaller.EXTRA_STATUS_MESSAGE) ?: ""

        if (status == PackageInstaller.STATUS_PENDING_USER_ACTION) {
            val confirmation = if (Build.VERSION.SDK_INT >= 33) {
                intent.getParcelableExtra(Intent.EXTRA_INTENT, Intent::class.java)
            } else {
                @Suppress("DEPRECATION") intent.getParcelableExtra(Intent.EXTRA_INTENT)
            }
            if (confirmation != null) {
                confirmation.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
                val launched = runCatching { context.startActivity(confirmation) }.isSuccess
                notifyUi(context, if (launched) {
                    "Android is waiting for installation confirmation…"
                } else {
                    "Install requires confirmation, but Android could not open the confirmation screen."
                })
            } else {
                notifyUi(context, "Install requires confirmation, but Android did not provide a confirmation screen.")
            }
            return
        }

        val resultText = when (status) {
            PackageInstaller.STATUS_SUCCESS -> "Install succeeded."
            PackageInstaller.STATUS_FAILURE_ABORTED -> "Install aborted by user."
            PackageInstaller.STATUS_FAILURE_BLOCKED -> "Install blocked by device policy."
            PackageInstaller.STATUS_FAILURE_CONFLICT -> "Install failed: signature conflict. The existing app was signed with a different key. Uninstall it first."
            PackageInstaller.STATUS_FAILURE_INCOMPATIBLE -> "Install failed: app incompatible with this device."
            PackageInstaller.STATUS_FAILURE_INVALID -> "Install failed: invalid APK."
            PackageInstaller.STATUS_FAILURE_STORAGE -> "Install failed: insufficient storage."
            else -> "Install ended with status $status: $message"
        }

        notifyUi(context, resultText)
    }

    companion object {
        const val ACTION_INSTALL_COMPLETE = "com.gummygamer.apkpatcher.INSTALL_COMPLETE"
        const val ACTION_INSTALL_RESULT = "com.gummygamer.apkpatcher.INSTALL_RESULT"

        private fun notifyUi(context: Context, text: String) {
            context.getSharedPreferences("patcher_state", Context.MODE_PRIVATE).edit()
                .putString("install_result", text).apply()
            context.sendBroadcast(Intent(ACTION_INSTALL_RESULT).setPackage(context.packageName)
                .putExtra("result", text))
        }
    }
}
