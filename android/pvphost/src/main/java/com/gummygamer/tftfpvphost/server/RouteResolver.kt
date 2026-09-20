package com.gummygamer.tftfpvphost.server

/**
 * Dynamic layer consulted before static resolution (Server/fakeserver.lbl:2004-2005).
 * Return null or an empty array for "not handled" so the request falls through to the
 * canned payload, exactly like a Legible handler returning `""`.
 */
fun interface DynamicHook {
    fun respond(request: HttpRequest): ByteArray?
}

/** Which layer produced a response body, for the request log line. */
enum class RouteSource(val label: String) {
    HEAD("head"),
    DYNAMIC("dynamic"),
    EXACT("canned"),
    PREFIX("rule"),
    DEFAULT("default"),
}

class Resolution(val body: ByteArray, val source: RouteSource)

/**
 * Dispatch order (contract section 3): dynamic hook -> exact `"METHOD /path"` key ->
 * prefix rule -> `{"error":null,"result":{}}`. HEAD short-circuits to an empty answer
 * before any lookup (tools/nativehook/inapk_server.c:608-611).
 */
class RouteResolver(
    private val payload: Payload,
    private val config: HostConfig,
    private val dynamic: DynamicHook = DynamicHook { null },
    private val onError: (String) -> Unit = {},
) {
    private val advertisedBase: ByteArray? = config.advertisedBaseUrl()?.toByteArray(Charsets.UTF_8)

    fun resolve(request: HttpRequest): Resolution {
        if (request.isHead) return Resolution(ByteArray(0), RouteSource.HEAD)
        runDynamic(request)?.let { return Resolution(it, RouteSource.DYNAMIC) }
        payload.lookup("${request.method} ${request.path}")?.let {
            return Resolution(advertise(it), RouteSource.EXACT)
        }
        payload.prefixBody(request.path)?.let { return Resolution(advertise(it), RouteSource.PREFIX) }
        return Resolution(payload.defaultBody(), RouteSource.DEFAULT)
    }

    /** A failing handler is logged and treated as unhandled, so the client still gets a 200. */
    private fun runDynamic(request: HttpRequest): ByteArray? = try {
        dynamic.respond(request)?.takeIf { it.isNotEmpty() }
    } catch (e: RuntimeException) {
        onError("dynamic handler failed for ${request.path}: ${e.javaClass.simpleName}")
        null
    }

    /**
     * `advertised_canned_body` (fakeserver.lbl:1910-1918): replaces the retail CDN base in
     * a canned body with this host's own base URL. Documented deviation: applied by
     * default whenever an advertised host is configured (design section 5.4).
     */
    private fun advertise(body: ByteArray): ByteArray {
        val base = advertisedBase ?: return body
        return replaceAll(body, RETAIL_CDN, base)
    }

    private fun replaceAll(source: ByteArray, needle: ByteArray, replacement: ByteArray): ByteArray {
        val first = indexOf(source, needle, 0)
        if (first < 0) return source
        val out = java.io.ByteArrayOutputStream(source.size + replacement.size)
        var from = 0
        var hit = first
        while (hit >= 0) {
            out.write(source, from, hit - from)
            out.write(replacement)
            from = hit + needle.size
            hit = indexOf(source, needle, from)
        }
        out.write(source, from, source.size - from)
        return out.toByteArray()
    }

    private fun indexOf(source: ByteArray, needle: ByteArray, start: Int): Int {
        val last = source.size - needle.size
        var i = start
        while (i <= last) {
            if (source[i] == needle[0] && regionEquals(source, i, needle)) return i
            i++
        }
        return -1
    }

    private fun regionEquals(source: ByteArray, at: Int, needle: ByteArray): Boolean {
        for (k in needle.indices) if (source[at + k] != needle[k]) return false
        return true
    }

    private companion object {
        val RETAIL_CDN: ByteArray = "https://tform-0901-hzlhiniyfcwf.tf-cdn.net".toByteArray(Charsets.UTF_8)
    }
}
