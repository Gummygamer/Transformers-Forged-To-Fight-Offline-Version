package com.gummygamer.apkpatcher

import java.nio.ByteBuffer
import java.nio.ByteOrder

/**
 * Patches global-metadata.dat to replace string literals pointing
 * at the dead Kabam servers with the user-supplied server host:port.
 *
 * Port of Server/build_phone_apk.lbl patch_metadata / literal_replacements.
 */
object MetadataPatch {

    /** Literal replacements applied to the metadata literal table. */
    data class Replacement(
        val old: String,
        val new: String
    )

    /** Result: patched data and list of changed hostnames. */
    data class Result(
        val data: ByteArray?,
        val changed: List<String>,
        val error: String?
    ) {
        val isSuccess: Boolean get() = error == null
    }

    fun literalReplacements(serverHost: String, serverPort: Int): List<Replacement> {
        val port = serverPort.toString()
        return listOf(
            Replacement("tf-odr.mcoc-cdn.cn", "$serverHost:$port"),
            Replacement("tf-static.mcoc-cdn.cn", "$serverHost:$port"),
            Replacement("words-express.tf-cdn.net", "$serverHost:$port"),
            Replacement("wss://gametalk.sparx.io:30443", "wss://$serverHost:30443")
        )
    }

    /**
     * Patch the metadata byte array.
     * Walks the literal table, finds matching strings, and replaces them.
     * Returns the patched data or an error if a required literal is missing.
     */
    fun patch(metadata: ByteArray, serverHost: String, serverPort: Int): Result {
        if (metadata.size < 24) {
            return Result(null, emptyList(), "metadata is too short (${metadata.size} bytes)")
        }

        val out = metadata.copyOf()
        val buf = ByteBuffer.wrap(out).order(ByteOrder.LITTLE_ENDIAN)

        val literalOffset = buf.getInt(8)
        val literalCount = buf.getInt(12)
        val dataOffset = buf.getInt(16)

        val changed = mutableListOf<String>()
        val pending = literalReplacements(serverHost, serverPort).toMutableList()

        var entry = literalOffset
        while (entry < literalOffset + literalCount && pending.isNotEmpty()) {
            if (entry + 8 > out.size) {
                return Result(null, changed, "metadata literal table is out of range")
            }

            // Each literal table entry: u32 size, u32 index into data section
            val size = buf.getInt(entry)
            val index = buf.getInt(entry + 4)
            val start = dataOffset + index

            // Check each pending replacement
            var hitIdx = -1
            for (i in pending.indices) {
                val r = pending[i]
                val oldBytes = r.old.toByteArray(Charsets.UTF_8)
                if (oldBytes.size == size && start + size <= out.size) {
                    var match = true
                    for (j in 0 until size) {
                        if (out[start + j] != oldBytes[j]) {
                            match = false
                            break
                        }
                    }
                    if (match) {
                        hitIdx = i
                        break
                    }
                }
            }

            if (hitIdx >= 0) {
                val r = pending.removeAt(hitIdx)
                val newBytes = r.new.toByteArray(Charsets.UTF_8)
                if (newBytes.size > size) {
                    return Result(null, changed,
                        "replacement is longer than literal: '${r.old}' (${newBytes.size} > $size)")
                }
                // Write replacement bytes
                for (j in newBytes.indices) {
                    out[start + j] = newBytes[j]
                }
                // Update size in the literal table entry
                buf.putInt(entry, newBytes.size)
                changed += r.old
            }

            entry += 8
        }

        if (pending.isNotEmpty()) {
            val missing = pending.joinToString(", ") { "'${it.old}'" }
            return Result(null, changed, "expected string literal(s) not found: $missing")
        }

        return Result(out, changed, null)
    }
}