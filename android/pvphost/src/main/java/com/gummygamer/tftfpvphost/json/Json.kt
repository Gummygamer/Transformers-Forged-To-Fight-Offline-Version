package com.gummygamer.tftfpvphost.json

/**
 * Minimal JSON model and encoder reproducing Server/jsonout.lbl (`encode_compact`
 * `:281-284`, `encode_spaced` `:677-680`, `public_json_string` `:166-198`).
 *
 * No org.json: the AGP unit-test android.jar stubs it out, and the envelope spacing
 * rules need byte-exact separators.
 */
sealed class JsonValue

object JsonNull : JsonValue()

data class JsonBool(val value: Boolean) : JsonValue()

data class JsonInt(val value: Long) : JsonValue()

data class JsonDec(val value: Double) : JsonValue()

data class JsonText(val value: String) : JsonValue()

data class JsonArr(val items: List<JsonValue>) : JsonValue()

/** Ordered object; keys keep insertion order like the Legible `JObj` pair list. */
data class JsonObj(val pairs: List<Pair<String, JsonValue>>) : JsonValue() {
    fun has(key: String): Boolean = pairs.any { it.first == key }

    /** Last duplicate wins, matching Python's `json.loads`. */
    fun get(key: String): JsonValue? = pairs.lastOrNull { it.first == key }?.second
}

fun jsonObj(vararg pairs: Pair<String, JsonValue>): JsonObj = JsonObj(pairs.toList())

/** `obj_set`: replaces the value in place, or appends the pair when the key is new. */
fun JsonObj.with(key: String, value: JsonValue): JsonObj =
    if (has(key)) JsonObj(pairs.map { if (it.first == key) key to value else it })
    else JsonObj(pairs + (key to value))

fun jsonText(value: String): JsonText = JsonText(value)

fun jsonInt(value: Int): JsonInt = JsonInt(value.toLong())

object JsonEncoder {
    private const val HEX = "0123456789abcdef"
    private const val FORM_FEED = 12

    /** Separators `,` and `:`. */
    fun encodeCompact(node: JsonValue): String = encode(node, ",", ":")

    /** Separators `, ` and `: `, the Python `json.dumps` default. */
    fun encodeSpaced(node: JsonValue): String = encode(node, ", ", ": ")

    fun compactEnvelope(result: JsonValue): String =
        encodeCompact(jsonObj("error" to JsonNull, "result" to result))

    fun spacedEnvelope(result: JsonValue): String =
        encodeSpaced(jsonObj("error" to JsonNull, "result" to result))

    fun spacedEnvelopeAsync(result: JsonValue, asyncItems: List<JsonValue>): String =
        encodeSpaced(
            jsonObj("error" to JsonNull, "result" to result, "async" to JsonArr(asyncItems)))

    fun encode(node: JsonValue, separator: String, keySeparator: String): String {
        val out = StringBuilder()
        write(out, node, separator, keySeparator)
        return out.toString()
    }

    private fun write(out: StringBuilder, node: JsonValue, sep: String, keySep: String) {
        when (node) {
            is JsonNull -> out.append("null")
            is JsonBool -> out.append(if (node.value) "true" else "false")
            is JsonInt -> out.append(node.value)
            is JsonDec -> out.append(decimalText(node.value))
            is JsonText -> writeString(out, node.value)
            is JsonArr -> writeList(out, node.items, sep, keySep)
            is JsonObj -> writeObject(out, node.pairs, sep, keySep)
        }
    }

    private fun writeList(out: StringBuilder, items: List<JsonValue>, sep: String, keySep: String) {
        out.append('[')
        items.forEachIndexed { index, item ->
            if (index > 0) out.append(sep)
            write(out, item, sep, keySep)
        }
        out.append(']')
    }

    private fun writeObject(
        out: StringBuilder,
        pairs: List<Pair<String, JsonValue>>,
        sep: String,
        keySep: String,
    ) {
        out.append('{')
        pairs.forEachIndexed { index, pair ->
            if (index > 0) out.append(sep)
            writeString(out, pair.first)
            out.append(keySep)
            write(out, pair.second, sep, keySep)
        }
        out.append('}')
    }

    /** Decimals always carry a fractional part (`0.0`), as `decimal_text` (`:200-203`). */
    private fun decimalText(value: Double): String {
        val text = value.toString()
        val plain = text.contains('.') || text.contains('e') || text.contains('E')
        return if (plain) text else "$text.0"
    }

    /** ASCII output: control characters and every non-ASCII UTF-16 unit become escapes. */
    private fun writeString(out: StringBuilder, value: String) {
        out.append('"')
        for (ch in value) {
            when {
                ch == '"' -> out.append("\\\"")
                ch == '\\' -> out.append("\\\\")
                ch == '\n' -> out.append("\\n")
                ch == '\r' -> out.append("\\r")
                ch == '\t' -> out.append("\\t")
                ch == '\b' -> out.append("\\b")
                ch.code == FORM_FEED -> out.append("\\f")
                ch.code < 32 || ch.code > 127 -> appendUnicodeEscape(out, ch.code)
                else -> out.append(ch)
            }
        }
        out.append('"')
    }

    private fun appendUnicodeEscape(out: StringBuilder, code: Int) {
        out.append("\\u")
        for (shift in intArrayOf(12, 8, 4, 0)) out.append(HEX[(code shr shift) and 0xf])
    }
}
