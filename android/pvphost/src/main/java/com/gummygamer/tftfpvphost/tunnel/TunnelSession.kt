package com.gummygamer.tftfpvphost.tunnel

import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import java.io.IOException

enum class TunnelState { IDLE, CONNECTING, READY, FAILED, DISCONNECTED }

data class TunnelStatus(
    val state: TunnelState = TunnelState.IDLE,
    val message: String? = null,
)

/** Owns both game channels for one relay session and gives the app a small lifecycle contract. */
class TunnelSession(private val config: TunnelConfig) : AutoCloseable {
    private val mutableStatus = MutableStateFlow(TunnelStatus())
    val status: StateFlow<TunnelStatus> = mutableStatus.asStateFlow()

    private var http: HttpTunnelBridge? = null
    private var combat: CombatTunnel? = null
    private var closed = false

    @Synchronized
    fun start(): Boolean {
        check(!closed) { "tunnel session is closed" }
        if (mutableStatus.value.state == TunnelState.READY) return true
        config.validationError()?.let { fail(it); return false }
        mutableStatus.value = TunnelStatus(TunnelState.CONNECTING)
        return try {
            val httpBridge = HttpTunnelBridge(config, ::disconnected)
            httpBridge.start()
            http = httpBridge
            val combatBridge = CombatTunnel(config, ::disconnected)
            combatBridge.start()
            combat = combatBridge
            mutableStatus.value = TunnelStatus(TunnelState.READY)
            true
        } catch (failure: IOException) {
            fail(failure.message ?: "The relay connection failed.")
            closeBridges()
            false
        } catch (failure: RuntimeException) {
            fail(failure.message ?: "The tunnel could not be started.")
            closeBridges()
            false
        }
    }

    @Synchronized
    override fun close() {
        if (closed) return
        closed = true
        closeBridges()
        mutableStatus.value = TunnelStatus(TunnelState.DISCONNECTED, "Tunnel disconnected.")
    }

    private fun disconnected(reason: String) {
        synchronized(this) {
            if (!closed && mutableStatus.value.state == TunnelState.READY) {
                mutableStatus.value = TunnelStatus(TunnelState.DISCONNECTED, reason)
                closeBridges()
            }
        }
    }

    private fun fail(message: String) {
        mutableStatus.value = TunnelStatus(TunnelState.FAILED, message)
    }

    private fun closeBridges() {
        try { combat?.close() } finally {
            combat = null
            try { http?.close() } finally { http = null }
        }
    }
}

/** Process-wide owner shared by the foreground service and the activity after rotation. */
object TunnelRuntime {
    private val lock = Any()
    private val mutableStatus = MutableStateFlow(TunnelStatus())
    val status: StateFlow<TunnelStatus> = mutableStatus.asStateFlow()

    private var session: TunnelSession? = null

    fun start(config: TunnelConfig): Boolean = synchronized(lock) {
        if (session != null && mutableStatus.value.state == TunnelState.READY) return true
        session?.close()
        val next = TunnelSession(config)
        session = next
        next.status.collectInBackground { nextStatus ->
            synchronized(lock) {
                if (session === next) mutableStatus.value = nextStatus
            }
        }
        val started = next.start()
        mutableStatus.value = next.status.value
        if (started) true else {
            if (session === next) session = null
            false
        }
    }

    fun stop() = synchronized(lock) {
        session?.close()
        session = null
        mutableStatus.value = TunnelStatus(TunnelState.DISCONNECTED, "Tunnel disconnected.")
    }

    private fun StateFlow<TunnelStatus>.collectInBackground(onValue: (TunnelStatus) -> Unit) {
        Thread({
            var last: TunnelStatus? = null
            while (true) {
                val value = value
                if (value != last) {
                    last = value
                    onValue(value)
                }
                if (value.state == TunnelState.DISCONNECTED || value.state == TunnelState.FAILED) return@Thread
                try { Thread.sleep(100) } catch (_: InterruptedException) { return@Thread }
            }
        }, "tftf-tunnel-status").apply { isDaemon = true; start() }
    }
}

/** Stable, pasteable invitation format. Relay tokens never enter logs or build flags. */
object InvitationCodec {
    private const val PREFIX = "TFTF1"

    fun encode(invitation: Invitation): String = listOf(
        PREFIX, invitation.session, invitation.hostToken, invitation.joinToken,
    ).joinToString("|")

    fun decode(value: String): Invitation? {
        val parts = value.trim().split('|')
        if (parts.size != 4 || parts[0] != PREFIX) return null
        val invitation = Invitation(parts[1], parts[2], parts[3])
        return if (invitation.session.matches(IDENTIFIER) &&
            invitation.hostToken.matches(IDENTIFIER) && invitation.joinToken.matches(IDENTIFIER)) {
            invitation
        } else null
    }

    private val IDENTIFIER = Regex("[A-Za-z0-9_-]{8,128}")
}
