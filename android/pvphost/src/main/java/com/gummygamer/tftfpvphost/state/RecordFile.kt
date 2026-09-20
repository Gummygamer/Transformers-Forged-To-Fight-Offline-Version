package com.gummygamer.tftfpvphost.state

import java.io.File
import java.io.IOException
import java.nio.file.AtomicMoveNotSupportedException
import java.nio.file.Files
import java.nio.file.StandardCopyOption

/**
 * Field sanitisers shared by every store, reproducing `roster_safe` and `relay_safe`
 * (Server/fakeserver.lbl:290-293, 320-323).
 */
object Fields {
    const val MAX_ID_CHARS: Int = 128
    const val MAX_NAME_CHARS: Int = 64

    /** `|`, `,` and newline become `_` so the value round-trips through a record line. */
    fun rosterSafe(value: String): String =
        value.replace('|', '_').replace(',', '_').replace('\n', '_')

    /** Relay payloads may keep commas but not pipes or line breaks. */
    fun relaySafe(value: String): String =
        value.replace('|', '_').replace('\n', '_').replace('\r', '_')

    /** Peer keys and match ids: sanitised and length-bounded because peers choose them. */
    fun idSafe(value: String): String = rosterSafe(value).take(MAX_ID_CHARS)

    /** Display names: sanitised and length-bounded. */
    fun nameSafe(value: String): String = rosterSafe(value).take(MAX_NAME_CHARS)
}

/**
 * Line-oriented store primitive over one file (`|`-separated records, one per line).
 *
 * Writes go through `<name>.tmp` and an atomic rename so a crash cannot truncate a store
 * (tools/nativehook/inapk_server.c:231-254). Reads are defensive: a missing file is an empty
 * list and blank lines are dropped. Not internally synchronised; [PvpStore] holds the lock.
 */
internal class RecordFile(private val file: File) {
    fun readLines(): List<String> {
        if (!file.isFile) return emptyList()
        return try {
            file.readText(Charsets.UTF_8).split('\n').filter { it.isNotBlank() }
        } catch (_: IOException) {
            emptyList()
        }
    }

    fun writeLines(lines: List<String>) {
        val parent = file.parentFile
        if (parent != null && !parent.isDirectory && !parent.mkdirs() && !parent.isDirectory) {
            throw IOException("cannot create state directory")
        }
        val temp = File(parent, file.name + ".tmp")
        temp.writeText(lines.joinToString("\n"), Charsets.UTF_8)
        try {
            Files.move(temp.toPath(), file.toPath(), StandardCopyOption.ATOMIC_MOVE)
        } catch (_: AtomicMoveNotSupportedException) {
            Files.move(temp.toPath(), file.toPath(), StandardCopyOption.REPLACE_EXISTING)
        }
    }

    fun clear() {
        if (file.exists() && !file.delete()) writeLines(emptyList())
        File(file.parentFile, file.name + ".tmp").delete()
    }

    companion object {
        fun fields(line: String): List<String> = line.split('|')

        fun field(fields: List<String>, index: Int): String = fields.getOrNull(index) ?: ""

        /** Numeric fields default to 0 on a parse failure (fakeserver.lbl:388, 459, 553, 646). */
        fun longOrZero(text: String): Long = text.trim().toLongOrNull() ?: 0L
    }
}
