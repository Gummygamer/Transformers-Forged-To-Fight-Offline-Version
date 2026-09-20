package com.gummygamer.tftfpvphost.server

import java.io.ByteArrayOutputStream
import java.io.IOException
import java.io.InputStream

/** One parsed HTTP request. [path] is query-stripped, [query] excludes the `?`. */
class HttpRequest(
    val method: String,
    val path: String,
    val query: String,
    val headers: Map<String, String>,
    val body: ByteArray,
    val keepAlive: Boolean,
) {
    val isHead: Boolean get() = method.equals("HEAD", ignoreCase = true)

    fun bodyText(): String = String(body, Charsets.UTF_8)

    /** `query_value` (fakeserver.lbl:1002-1016): URL-decoded, last match wins. */
    fun queryValue(name: String): String {
        var result = ""
        for (item in query.split('&')) {
            val pair = item.split('=')
            if (percentDecode(pair[0]) == name) result = percentDecode(pair.getOrElse(1) { "" })
        }
        return result
    }
}

/** Reader outcome: a request, a clean close, or a rejection with an HTTP status. */
sealed interface ReadResult {
    class Request(val request: HttpRequest) : ReadResult

    /** Peer closed before sending the next request. */
    object Closed : ReadResult

    class Rejected(val status: Int, val reason: String) : ReadResult
}

/**
 * Bounded request-line + header + body reader (limits copied from
 * tools/nativehook/inapk_server.c:31-39, 600-606).
 */
object HttpRequestReader {
    const val MAX_HEAD = 32 * 1024
    const val MAX_BODY = 1024 * 1024
    const val MAX_CONTENT_LENGTH = 64L * 1024 * 1024
    const val MAX_TARGET = 4095
    private const val MAX_METHOD = 23
    private const val BLANK_LINE = 0x0d0a0d0a

    /**
     * Reads the next request from [input]. [onContinue] is invoked when the client sent
     * `Expect: 100-continue` and a body follows. IO failures propagate to the caller.
     */
    fun read(input: InputStream, onContinue: () -> Unit): ReadResult {
        val head = readHead(input)
        if (head is HeadResult.Closed) return ReadResult.Closed
        if (head is HeadResult.TooLarge) return ReadResult.Rejected(431, "request head too large")
        val text = (head as HeadResult.Text).value
        val lines = text.split("\r\n")
        val line = parseRequestLine(lines[0]) ?: return ReadResult.Rejected(400, "bad request line")
        val headers = parseHeaders(lines.drop(1)) ?: return ReadResult.Rejected(400, "bad header")
        return finish(input, line, headers, onContinue)
    }

    private class RequestLine(val method: String, val path: String, val query: String, val http10: Boolean)

    private fun finish(
        input: InputStream,
        line: RequestLine,
        headers: Map<String, String>,
        onContinue: () -> Unit,
    ): ReadResult {
        if (headers.containsKey("transfer-encoding")) {
            return ReadResult.Rejected(501, "transfer-encoding is not supported")
        }
        val length = contentLength(headers) ?: return ReadResult.Rejected(400, "bad content-length")
        if (length > MAX_CONTENT_LENGTH) return ReadResult.Rejected(413, "body too large")
        if (length > 0 && headers["expect"]?.contains("100-continue", ignoreCase = true) == true) onContinue()
        val body = readBody(input, length) ?: return ReadResult.Closed
        val connection = headers["connection"].orEmpty()
        val close = connection.contains("close", ignoreCase = true) ||
            (line.http10 && !connection.contains("keep-alive", ignoreCase = true))
        return ReadResult.Request(HttpRequest(line.method, line.path, line.query, headers, body, !close))
    }

    private sealed interface HeadResult {
        object Closed : HeadResult
        object TooLarge : HeadResult
        class Text(val value: String) : HeadResult
    }

    /** Reads up to and including the blank line; byte-at-a-time over a buffered stream. */
    private fun readHead(input: InputStream): HeadResult {
        val buf = ByteArrayOutputStream(512)
        var tail = 0
        while (true) {
            val b = input.read()
            if (b < 0) return if (buf.size() == 0) HeadResult.Closed else throw IOException("truncated request head")
            // Blank lines left over from the previous request are tolerated.
            if (buf.size() == 0 && (b == '\r'.code || b == '\n'.code)) continue
            buf.write(b)
            tail = (tail shl 8) or b
            if (buf.size() > MAX_HEAD) return HeadResult.TooLarge
            if (tail == BLANK_LINE) return HeadResult.Text(String(buf.toByteArray(), Charsets.ISO_8859_1))
        }
    }

    private fun parseRequestLine(line: String): RequestLine? {
        val parts = line.split(' ')
        if (parts.size != 3) return null
        val (method, target, version) = parts
        val validMethod = method.length in 1..MAX_METHOD && method.all { it in 'A'..'Z' || it in 'a'..'z' }
        if (!validMethod || !version.startsWith("HTTP/")) return null
        if (!targetAllowed(target)) return null
        val question = target.indexOf('?')
        val path = if (question < 0) target else target.substring(0, question)
        val query = if (question < 0) "" else target.substring(question + 1)
        if (hasTraversal(path)) return null
        return RequestLine(method, path, query, version == "HTTP/1.0")
    }

    /** Origin-form only: `/`-rooted, ASCII, no control bytes, bounded length. */
    private fun targetAllowed(target: String): Boolean =
        target.startsWith("/") && target.length <= MAX_TARGET &&
            target.all { it.code in 33..126 }

    /** True for a `..` path segment, raw or percent-encoded, or an encoded NUL. */
    private fun hasTraversal(path: String): Boolean {
        val decoded = percentDecode(path)
        if (decoded.contains(' ') || decoded.any { it.code < 32 }) return true
        return decoded.split('/', '\\').any { it == ".." }
    }

    private fun parseHeaders(lines: List<String>): Map<String, String>? {
        val headers = HashMap<String, String>()
        for (raw in lines) {
            if (raw.isEmpty()) continue
            val colon = raw.indexOf(':')
            if (colon <= 0) return null
            val name = raw.substring(0, colon).trim().lowercase()
            if (name.isEmpty() || name.any { it.code <= 32 || it.code >= 127 }) return null
            val value = raw.substring(colon + 1).trim()
            val previous = headers[name]
            if (name == "content-length" && previous != null && previous != value) return null
            headers[name] = value
        }
        return headers
    }

    private fun contentLength(headers: Map<String, String>): Long? {
        val raw = headers["content-length"] ?: return 0L
        if (raw.isEmpty() || raw.length > 12 || !raw.all { it in '0'..'9' }) return null
        return raw.toLong()
    }

    /** Retains up to [MAX_BODY] bytes and streams the rest away; null on early EOF. */
    private fun readBody(input: InputStream, length: Long): ByteArray? {
        val keep = minOf(length, MAX_BODY.toLong()).toInt()
        val body = ByteArray(keep)
        if (!readFully(input, body, keep)) return null
        var remaining = length - keep
        val junk = ByteArray(4096)
        while (remaining > 0) {
            val n = input.read(junk, 0, minOf(remaining, junk.size.toLong()).toInt())
            if (n < 0) return null
            remaining -= n
        }
        return body
    }

    private fun readFully(input: InputStream, into: ByteArray, count: Int): Boolean {
        var got = 0
        while (got < count) {
            val n = input.read(into, got, count - got)
            if (n < 0) return false
            got += n
        }
        return true
    }
}

/** Percent-decoding without `+` handling; malformed escapes stay literal. */
fun percentDecode(text: String): String {
    if (!text.contains('%')) return text
    val out = ByteArrayOutputStream(text.length)
    var i = 0
    while (i < text.length) {
        val hex = if (text[i] == '%') hexPair(text, i) else -1
        if (hex >= 0) {
            out.write(hex)
            i += 3
        } else {
            out.write(text[i].toString().toByteArray(Charsets.UTF_8))
            i++
        }
    }
    return String(out.toByteArray(), Charsets.UTF_8)
}

private fun hexPair(text: String, at: Int): Int {
    if (at + 2 >= text.length) return -1
    val hi = Character.digit(text[at + 1], 16)
    val lo = Character.digit(text[at + 2], 16)
    return if (hi < 0 || lo < 0) -1 else hi * 16 + lo
}
