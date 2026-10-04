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
import com.gummygamer.tftfpvphost.tunnel.LanCombatRelay
import com.gummygamer.tftfpvphost.tunnel.TunnelConfig
import com.gummygamer.tftfpvphost.tunnel.TunnelRuntime
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

    @Volatile
    private var lanRelay: LanCombatRelay? = null
    private val lifecycle = Executors.newSingleThreadExecutor { task ->
        Thread(task, "tftf-host-lifecycle").apply { isDaemon = true }
    }

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        if (intent?.action == ACTION_STOP) {
            stopHost()
        } else {
            val tunnel = tunnelFrom(intent)
            startHost(configFrom(intent, tunnel), tunnel)
        }
        return START_NOT_STICKY
    }

    override fun onDestroy() {
        destroyed = true
        lifecycle.shutdownNow()
        stopLanRelay()
        TunnelRuntime.stop()
        HostRuntime.stop()
        releaseWakeLock()
        super.onDestroy()
    }

    private fun configFrom(intent: Intent?, tunnel: TunnelConfig?): HostConfig = HostConfig(
        port = intent?.getIntExtra(EXTRA_PORT, HostConfig.DEFAULT_PORT) ?: HostConfig.DEFAULT_PORT,
        presenceTtlMs = intent?.getLongExtra(EXTRA_TTL_MS, HostConfig.DEFAULT_PRESENCE_TTL_MS)
            ?: HostConfig.DEFAULT_PRESENCE_TTL_MS,
        advertisedHost = if (tunnel != null) "127.0.0.1" else intent?.getStringExtra(EXTRA_HOST).orEmpty(),
        rewriteCdn = tunnel == null && (intent?.getBooleanExtra(EXTRA_REWRITE_CDN, true) ?: true),
    )

    private fun tunnelFrom(intent: Intent?): TunnelConfig? {
        if (intent?.getBooleanExtra(EXTRA_TUNNEL_ENABLED, false) != true) return null
        val role = if (intent.getStringExtra(EXTRA_TUNNEL_ROLE) == ROLE_JOIN) {
            TunnelConfig.Role.JOIN
        } else {
            TunnelConfig.Role.HOST
        }
        return TunnelConfig(
            relayHost = intent.getStringExtra(EXTRA_RELAY_HOST).orEmpty(),
            relayPort = intent.getIntExtra(EXTRA_RELAY_PORT, 0),
            session = intent.getStringExtra(EXTRA_TUNNEL_SESSION).orEmpty(),
            token = intent.getStringExtra(EXTRA_TUNNEL_TOKEN).orEmpty(),
            role = role,
            invite = intent.getStringExtra(EXTRA_TUNNEL_INVITE).orEmpty(),
            httpPort = if (role == TunnelConfig.Role.HOST) {
                intent.getIntExtra(EXTRA_PORT, HostConfig.DEFAULT_PORT)
            } else HostConfig.DEFAULT_PORT,
            combatPort = TunnelConfig.DEFAULT_COMBAT_PORT,
        )
    }

    private fun startHost(config: HostConfig, tunnel: TunnelConfig?) {
        HostNotifications.ensureChannel(this)
        startForeground(
            HostNotifications.NOTIFICATION_ID,
            HostNotifications.build(this, config.advertisedHost, config.port, 0),
            ServiceInfo.FOREGROUND_SERVICE_TYPE_SPECIAL_USE)
        acquireWakeLock()
        lifecycle.execute { launchServer(config, tunnel) }
    }

    /** Runs on the lifecycle thread; a failure leaves the runtime stopped and ends the service. */
    private fun launchServer(config: HostConfig, tunnel: TunnelConfig?) {
        if (tunnel?.role == TunnelConfig.Role.JOIN) {
            if (!TunnelRuntime.start(tunnel)) stopSelf()
            return
        }
        val result = loadPayload()?.let { HostRuntime.startServing(it, config, PvpHost.hookFactory(stateDir())) }
        if (result is StartResult.Started && tunnel != null && !TunnelRuntime.start(tunnel)) {
            HostRuntime.stop()
            stopSelf()
            return
        }
        // In tunnel mode CombatTunnel owns loopback:8777, so only a plain LAN host runs the relay.
        if (result is StartResult.Started && tunnel == null) startLanRelay()
        // onDestroy may have run while the start was in flight; never leave an orphan listener.
        if (destroyed) {
            stopLanRelay()
            TunnelRuntime.stop()
            HostRuntime.stop()
        }
        if (result is StartResult.Started) return
        stopSelf()
    }

    /** A bind failure is logged and non-fatal: the HTTP server keeps serving without live fights. */
    private fun startLanRelay() {
        if (lanRelay != null) return
        val relay = LanCombatRelay(log = { HostRuntime.log.append(it) })
        try {
            relay.start()
            lanRelay = relay
        } catch (e: IOException) {
            relay.close()
            HostRuntime.log.append("Arena relay could not bind UDP ${TunnelConfig.DEFAULT_COMBAT_PORT}: ${e.message}. Live fights are unavailable.")
        }
    }

    private fun stopLanRelay() {
        lanRelay?.close()
        lanRelay = null
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
        lifecycle.execute {
            stopLanRelay()
            TunnelRuntime.stop()
            HostRuntime.stop()
        }
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
        const val EXTRA_TUNNEL_ENABLED = "tunnel_enabled"
        const val EXTRA_RELAY_HOST = "relay_host"
        const val EXTRA_RELAY_PORT = "relay_port"
        const val EXTRA_TUNNEL_ROLE = "tunnel_role"
        const val EXTRA_TUNNEL_SESSION = "tunnel_session"
        const val EXTRA_TUNNEL_TOKEN = "tunnel_token"
        const val EXTRA_TUNNEL_INVITE = "tunnel_invite"
        const val ROLE_JOIN = "join"
        const val PAYLOAD_ASSET = "tftf_payload.bin"
    }
}
