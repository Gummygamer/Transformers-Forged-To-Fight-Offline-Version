package com.gummygamer.tftfpvphost.server

import java.io.ByteArrayOutputStream

/**
 * Token splicing for payload templates (`%TID%`, `%BID%`, `%SIG%`, ...).
 *
 * Reproduces `out_template_args` (tools/nativehook/inapk_server.c:123-130): a single pass
 * that tries each token in order at every byte, emits the replacement raw (no JSON
 * escaping) and never rescans replacement text.
 */
object Templates {
    /** Output growth cap, 8x the request body cap (`inapk_server.c:120`). */
    const val MAX_OUTPUT: Int = 8 * 1024 * 1024

    class Token(val name: ByteArray, val value: ByteArray) {
        constructor(name: String, value: String) :
            this(name.toByteArray(Charsets.UTF_8), value.toByteArray(Charsets.UTF_8))
    }

    /** Null when the expanded output would exceed [MAX_OUTPUT]. */
    fun splice(source: ByteArray, tokens: List<Token>): ByteArray? {
        val out = ByteArrayOutputStream(source.size + 64)
        var i = 0
        while (i < source.size) {
            val hit = tokens.firstOrNull { matchesAt(source, i, it.name) }
            if (hit == null) {
                out.write(source[i].toInt())
                i++
            } else {
                out.write(hit.value)
                i += hit.name.size
            }
            if (out.size() > MAX_OUTPUT) return null
        }
        return out.toByteArray()
    }

    fun splice(source: ByteArray, vararg tokens: Pair<String, String>): ByteArray? =
        splice(source, tokens.map { Token(it.first, it.second) })

    private fun matchesAt(source: ByteArray, at: Int, name: ByteArray): Boolean {
        if (name.isEmpty() || at + name.size > source.size) return false
        for (k in name.indices) if (source[at + k] != name[k]) return false
        return true
    }

    /**
     * `json_default_spaces` (`inapk_server.c:135-147`): inserts one space after every `:`
     * and `,` outside string literals, giving Python's default `json.dumps` spacing.
     */
    fun jsonDefaultSpaces(source: ByteArray): ByteArray? {
        val out = ByteArrayOutputStream(source.size + source.size / 8)
        var quoted = false
        var escaped = false
        for (byte in source) {
            val c = byte.toInt()
            out.write(c)
            if (quoted) {
                if (escaped) escaped = false else if (c == '\\'.code) escaped = true else if (c == '"'.code) quoted = false
            } else if (c == '"'.code) {
                quoted = true
            } else if (c == ':'.code || c == ','.code) {
                out.write(' '.code)
            }
            if (out.size() > MAX_OUTPUT) return null
        }
        return out.toByteArray()
    }
}
