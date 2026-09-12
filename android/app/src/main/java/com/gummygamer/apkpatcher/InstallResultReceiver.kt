package com.gummygamer.apkpatcher

import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.pm.PackageInstaller

/**
 * Receives the result of a PackageInstaller session commit.
 * The system sends this broadcast after the user approves/denies the install dialog.
 */
class InstallResultReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) {
        val status = intent.getIntExtra(PackageInstaller.EXTRA_STATUS, PackageInstaller.STATUS_FAILURE)
        val message = intent.getStringExtra(PackageInstaller.EXTRA_STATUS_MESSAGE) ?: ""

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

        // Store result in a static field that the ViewModel can read
        lastInstallResult = resultText
    }

    companion object {
        @Volatile
        var lastInstallResult: String? = null
    }
}