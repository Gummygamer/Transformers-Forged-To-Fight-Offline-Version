package com.gummygamer.tftfpvphost.server

/** Immutable host settings; validated before the server starts. */
data class HostConfig(
    val port: Int = DEFAULT_PORT,
    val presenceTtlMs: Long = DEFAULT_PRESENCE_TTL_MS,
    /** Non-loopback IPv4 the players use; empty leaves canned bodies untouched. */
    val advertisedHost: String = "",
    val rewriteCdn: Boolean = true,
    val accountLevel: Int = DEFAULT_ACCOUNT_LEVEL,
    val defaultArena: String = DEFAULT_ARENA,
) {
    /** Actionable message for the first invalid field, or null when the config is usable. */
    fun validationError(): String? = when {
        port !in MIN_PORT..MAX_PORT ->
            "Port must be between $MIN_PORT and $MAX_PORT; Android apps cannot bind ports below $MIN_PORT."
        presenceTtlMs <= 0L -> "Presence timeout must be a positive number of milliseconds."
        advertisedHost.any { it.isWhitespace() || it == '/' || it == '"' || it == '\\' } ->
            "Advertised host must be a bare IP address or host name."
        else -> null
    }

    /** `http://host:port` written into canned bodies, or null when advertising is off. */
    fun advertisedBaseUrl(): String? =
        if (rewriteCdn && advertisedHost.isNotEmpty()) "http://$advertisedHost:$port" else null

    companion object {
        const val DEFAULT_PORT = 8080
        const val MIN_PORT = 1024
        const val MAX_PORT = 65535
        const val DEFAULT_PRESENCE_TTL_MS = 15_000L
        const val DEFAULT_ACCOUNT_LEVEL = 30
        const val DEFAULT_ARENA = "arena_versus"
    }
}
