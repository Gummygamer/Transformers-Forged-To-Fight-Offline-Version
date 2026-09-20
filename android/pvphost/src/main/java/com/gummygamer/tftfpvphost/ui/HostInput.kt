package com.gummygamer.tftfpvphost.ui

import com.gummygamer.tftfpvphost.server.HostConfig

/** Parsed and validated form fields; [error] is a user-facing message or null. */
sealed interface Parsed<out T> {
    class Ok<T>(val value: T) : Parsed<T>

    class Bad(val error: ParseError) : Parsed<Nothing>
}

enum class ParseError { PORT_NOT_NUMBER, PORT_RANGE, TTL_NOT_NUMBER, TTL_RANGE }

/** Pure validation for the port and presence-timeout fields. */
object HostInput {
    fun port(text: String): Parsed<Int> {
        val value = text.trim().toIntOrNull() ?: return Parsed.Bad(ParseError.PORT_NOT_NUMBER)
        return if (HostConfig(port = value).validationError() == null) Parsed.Ok(value)
        else Parsed.Bad(ParseError.PORT_RANGE)
    }

    /** Presence timeout in milliseconds; the server accepts any positive duration. */
    fun ttlMs(text: String): Parsed<Long> {
        val milliseconds = text.trim().toLongOrNull() ?: return Parsed.Bad(ParseError.TTL_NOT_NUMBER)
        return if (milliseconds > 0L) Parsed.Ok(milliseconds)
        else Parsed.Bad(ParseError.TTL_RANGE)
    }
}
