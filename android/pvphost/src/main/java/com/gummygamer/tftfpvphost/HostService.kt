package com.gummygamer.tftfpvphost

import android.app.Service
import android.content.Intent
import android.content.pm.ServiceInfo
import android.os.IBinder
import android.os.PowerManager
import com.gummygamer.tftfpvphost.api.PvpHost
import com.gummygamer.tftfpvphost.server.HostConfig
import com.gummygamer.tftfpvphost.server.HostMessages
import com.gummygamer.tftfpvphost.server.HostRuntime
import com.gummygamer.tftfpvphost.server.StartResult
import java.io.File
import java.io.IOException
import java.util.concurrent.Executors

/**
 * Foreground service that owns the server's lifetime. Loading the payload and binding the
 * socket run on a single-thread executor so start and stop requests are ordered and the
 * main thread is never blocked; [HostRuntime] holds the actual server state.
 */
class HostService : Service() {
    private var wakeLock: PowerManager.WakeLock? = null

    @Volatile
    private var destroyed = false
    private val lifecycle = Executors.newSingleThreadExecutor { task ->
        Thread(task, "tftf-host-lifecycle").apply { isDaemon = true }
    }

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        if (intent?.action == ACTION_STOP) {
            stopHost()
        } else {
            startHost(configFrom(intent))
        }
        return START_NOT_STICKY
    }

    override fun onDestroy() {
        destroyed = true
        lifecycle.shutdownNow()
        HostRuntime.stop()
        releaseWakeLock()
        super.onDestroy()
    }

    private fun configFrom(intent: Intent?): HostConfig = HostConfig(
        port = intent?.getIntExtra(EXTRA_PORT, HostConfig.DEFAULT_PORT) ?: HostConfig.DEFAULT_PORT,
        presenceTtlMs = intent?.getLongExtra(EXTRA_TTL_MS, HostConfig.DEFAULT_PRESENCE_TTL_MS)
            ?: HostConfig.DEFAULT_PRESENCE_TTL_MS,
        advertisedHost = intent?.getStringExtra(EXTRA_HOST).orEmpty(),
        rewriteCdn = intent?.getBooleanExtra(EXTRA_REWRITE_CDN, true) ?: true,
    )

    private fun startHost(config: HostConfig) {
        HostNotifications.ensureChannel(this)
        startForeground(
            HostNotifications.NOTIFICATION_ID,
            HostNotifications.build(this, config.advertisedHost, config.port, 0),
            ServiceInfo.FOREGROUND_SERVICE_TYPE_SPECIAL_USE)
        acquireWakeLock()
        lifecycle.execute { launchServer(config) }
    }

    /** Runs on the lifecycle thread; a failure leaves the runtime stopped and ends the service. */
    private fun launchServer(config: HostConfig) {
        val result = loadPayload()?.let { HostRuntime.startServing(it, config, PvpHost.hookFactory(stateDir())) }
        // onDestroy may have run while the start was in flight; never leave an orphan listener.
        if (destroyed) HostRuntime.stop()
        if (result is StartResult.Started) return
        stopSelf()
    }

    private fun stateDir(): File = File(filesDir, PvpHost.STATE_DIR_NAME)

    private fun loadPayload(): ByteArray? = try {
        assets.open(PAYLOAD_ASSET).use { it.readBytes() }
    } catch (e: IOException) {
        HostRuntime.reportFailure(HostMessages.PAYLOAD_MISSING)
        null
    } catch (e: OutOfMemoryError) {
        HostRuntime.reportFailure(HostMessages.PAYLOAD_TOO_LARGE_FOR_MEMORY)
        null
    }

    private fun stopHost() {
        lifecycle.execute { HostRuntime.stop() }
        releaseWakeLock()
        stopForeground(STOP_FOREGROUND_REMOVE)
        stopSelf()
    }

    private fun acquireWakeLock() {
        if (wakeLock?.isHeld == true) return
        val pm = getSystemService(PowerManager::class.java)
        wakeLock = pm.newWakeLock(PowerManager.PARTIAL_WAKE_LOCK, "tftfpvphost:host").apply {
            acquire()
        }
    }

    private fun releaseWakeLock() {
        wakeLock?.takeIf { it.isHeld }?.release()
        wakeLock = null
    }

    companion object {
        const val ACTION_STOP = "com.gummygamer.tftfpvphost.STOP"
        const val EXTRA_PORT = "port"
        const val EXTRA_TTL_MS = "ttl_ms"
        const val EXTRA_HOST = "advertised_host"
        const val EXTRA_REWRITE_CDN = "rewrite_cdn"
        const val PAYLOAD_ASSET = "tftf_payload.bin"
    }
}
