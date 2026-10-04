package com.gummygamer.tftfpvphost.tunnel

import java.security.SecureRandom
import java.util.Base64

/** Credentials and fixed local endpoints for one two-player relay session. */
data class TunnelConfig(
    val relayHost: String,
    val relayPort: Int,
    val session: String,
    val token: String,
    val role: Role,
    /** Host-only authority that registers the join invitation with the relay. */
    val invite: String = "",
    val httpPort: Int = DEFAULT_HTTP_PORT,
    val combatPort: Int = DEFAULT_COMBAT_PORT,
) {
    enum class Role { HOST, JOIN }

    fun validationError(): String? = when {
        relayHost.isBlank() || relayHost.any { it.isWhitespace() || it == '/' } ->
            "Relay host must be a host name or IP address."
        relayPort !in 1..65535 -> "Relay port must be between 1 and 65535."
        !IDENTIFIER.matches(session) -> "Tunnel session is invalid or incomplete."
        !IDENTIFIER.matches(token) -> "Tunnel invitation is invalid or incomplete."
        role == Role.HOST && !IDENTIFIER.matches(invite) -> "Host invitation is invalid or incomplete."
        httpPort !in 1024..65535 -> "Tunnel HTTP port must be between 1024 and 65535."
        combatPort !in 1024..65535 -> "Tunnel combat port must be between 1024 and 65535."
        else -> null
    }

    companion object {
        const val DEFAULT_HTTP_PORT = 8080
        const val DEFAULT_COMBAT_PORT = 8777
        const val CONNECT_TIMEOUT_MS = 10_000
        const val IO_TIMEOUT_MS = 30_000
        const val CLOSE_TIMEOUT_MS = 500
        const val MAX_COMBAT_PACKET = 512
        const val MAX_PENDING_PACKETS = 64
        private val IDENTIFIER = Regex("[A-Za-z0-9_-]{8,128}")

        /** Generates a bearer invitation locally; it is shared only out of band. */
        fun newInvitation(): Invitation {
            val random = SecureRandom()
            fun value(bytes: Int): String = Base64.getUrlEncoder().withoutPadding().encodeToString(
                ByteArray(bytes).also(random::nextBytes),
            )
            return Invitation(value(16), value(32), value(32))
        }
    }
}

data class Invitation(val session: String, val hostToken: String, val joinToken: String) {
    /** The token printed/shared with the remote player. */
    val token: String get() = joinToken
}
