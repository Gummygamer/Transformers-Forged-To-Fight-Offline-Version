package com.gummygamer.tftfpvphost.json

/**
 * Permissive request-body reader (Server/fakeserver.lbl:920-984, 1043-1050).
 *
 * Malformed input yields the default value instead of an exception and nesting depth is
 * bounded, so a hostile body cannot crash a worker thread.
 */
object JsonParse {
    const val MAX_DEPTH = 32
    private const val MAX_TEXT = 1 shl 20

    /** `request_object`: `{}` unless the trimmed body starts with `{` and parses. */
    fun requestObject(body: String): JsonObj {
        if (!body.trimStart().startsWith("{")) return JsonObj(emptyList())
        return parse(body) as? JsonObj ?: JsonObj(emptyList())
    }

    /** Whole-document parse; null when the text is not exactly one JSON value. */
    fun parse(text: String): JsonValue? {
        if (text.length > MAX_TEXT) return null
        val reader = Reader(text)
        val value = reader.value(0) ?: return null
        reader.skipSpace()
        return if (reader.atEnd()) value else null
    }

    fun textOr(node: JsonObj, key: String, fallback: String): String =
        (node.get(key) as? JsonText)?.value ?: fallback

    fun textFirst(node: JsonObj, first: String, second: String, third: String): String {
        for (key in listOf(first, second, third)) {
            val text = textOr(node, key, "")
            if (text.isNotEmpty()) return text
        }
        return ""
    }

    fun intOr(node: JsonObj, key: String, fallback: Long): Long =
        (node.get(key) as? JsonInt)?.value ?: fallback

    fun listOrEmpty(node: JsonObj, key: String): List<JsonValue> =
        (node.get(key) as? JsonArr)?.items ?: emptyList()

    fun textList(node: JsonValue?): List<String> =
        (node as? JsonArr)?.items?.mapNotNull { (it as? JsonText)?.value } ?: emptyList()

    /** `json_value_text`: text or integer rendered as text, else the fallback. */
    fun valueText(node: JsonValue?, fallback: String): String = when (node) {
        is JsonText -> node.value
        is JsonInt -> node.value.toString()
        else -> fallback
    }

    private val LITERALS: List<Pair<String, JsonValue>> = listOf(
        "true" to JsonBool(true),
        "false" to JsonBool(false),
        "null" to JsonNull,
    )

    private val SIMPLE_ESCAPES: Map<Char, Char> = mapOf(
        '"' to '"', '\\' to '\\', '/' to '/', 'b' to '\b', 'f' to 12.toChar(),
        'n' to '\n', 'r' to '\r', 't' to '\t',
    )

    private class Reader(private val src: String) {
        private var pos = 0

        fun atEnd(): Boolean = pos >= src.length

        fun skipSpace() {
            while (pos < src.length && src[pos] in " \t\r\n") pos++
        }

        fun value(depth: Int): JsonValue? {
            if (depth > MAX_DEPTH) return null
            skipSpace()
            if (atEnd()) return null
            return when (src[pos]) {
                '{' -> obj(depth)
                '[' -> arr(depth)
                '"' -> string()?.let { JsonText(it) }
                else -> scalar()
            }
        }

        private fun obj(depth: Int): JsonValue? {
            pos++
            val pairs = ArrayList<Pair<String, JsonValue>>()
            skipSpace()
            if (peek() == '}') return close(JsonObj(pairs))
            while (true) {
                skipSpace()
                if (peek() != '"') return null
                val key = string() ?: return null
                skipSpace()
                if (peek() != ':') return null
                pos++
                pairs.add(key to (value(depth + 1) ?: return null))
                skipSpace()
                when (peek()) {
                    ',' -> pos++
                    '}' -> return close(JsonObj(pairs))
                    else -> return null
                }
            }
        }

        private fun arr(depth: Int): JsonValue? {
            pos++
            val items = ArrayList<JsonValue>()
            skipSpace()
            if (peek() == ']') return close(JsonArr(items))
            while (true) {
                items.add(value(depth + 1) ?: return null)
                skipSpace()
                when (peek()) {
                    ',' -> pos++
                    ']' -> return close(JsonArr(items))
                    else -> return null
                }
            }
        }

        private fun close(node: JsonValue): JsonValue {
            pos++
            return node
        }

        private fun peek(): Char = if (pos < src.length) src[pos] else NO_CHAR

        private fun scalar(): JsonValue? {
            for ((word, node) in LITERALS) {
                if (src.startsWith(word, pos)) {
                    pos += word.length
                    return node
                }
            }
            return number()
        }

        private fun number(): JsonValue? {
            val start = pos
            while (pos < src.length && src[pos] in "+-0123456789.eE") pos++
            val token = src.substring(start, pos)
            if (token.isEmpty()) return null
            token.toLongOrNull()?.let { return JsonInt(it) }
            val dec = token.toDoubleOrNull() ?: return null
            return if (dec.isFinite()) JsonDec(dec) else null
        }

        private fun string(): String? {
            pos++
            val out = StringBuilder()
            while (pos < src.length) {
                val ch = src[pos++]
                when {
                    ch == '"' -> return out.toString()
                    ch == '\\' -> if (!escape(out)) return null
                    ch.code < 32 -> return null
                    else -> out.append(ch)
                }
            }
            return null
        }

        private fun escape(out: StringBuilder): Boolean {
            if (pos >= src.length) return false
            val esc = src[pos++]
            val simple = SIMPLE_ESCAPES[esc]
            if (simple != null) {
                out.append(simple)
                return true
            }
            if (esc != 'u' || pos + 4 > src.length) return false
            val code = src.substring(pos, pos + 4).toIntOrNull(16) ?: return false
            pos += 4
            out.append(code.toChar())
            return true
        }

        private companion object {
            val NO_CHAR: Char = 0.toChar()
        }
    }
}
