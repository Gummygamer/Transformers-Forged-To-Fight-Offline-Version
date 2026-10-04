package com.gummygamer.tftfpvphost.server

import java.time.Instant
import java.time.ZoneId
import java.time.format.DateTimeFormatter
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update

/**
 * Thread-safe bounded ring of structured log lines for the UI log pane, in the spirit of
 * Server/fakeserver.lbl:1980-2000 (`[HH:MM:SS.mmm] METHOD path -> source (nB)`).
 *
 * Callers must never pass session tokens, header values or file paths; [append] also
 * strips control characters and caps line length as a second line of defence.
 */
class ServerLog(private val nowMs: () -> Long = System::currentTimeMillis) {
    private val lines = MutableStateFlow<List<String>>(emptyList())
    val entries: StateFlow<List<String>> = lines.asStateFlow()

    fun append(message: String) {
        val stamp = STAMP.format(Instant.ofEpochMilli(nowMs()))
        val line = "[$stamp] ${clean(message)}"
        lines.update { (it + line).takeLast(CAPACITY) }
    }

    fun clear() {
        lines.value = emptyList()
    }

    private fun clean(message: String): String {
        val flat = message.map { if (it.code < 32 || it.code == 127) ' ' else it }.joinToString("")
        return if (flat.length > MAX_LINE) flat.substring(0, MAX_LINE) + "..." else flat
    }

    private companion object {
        const val CAPACITY = 500
        const val MAX_LINE = 400
        val STAMP: DateTimeFormatter =
            DateTimeFormatter.ofPattern("HH:mm:ss.SSS").withZone(ZoneId.systemDefault())
    }
}
