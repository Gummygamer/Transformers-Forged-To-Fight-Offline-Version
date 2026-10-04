package com.gummygamer.tftfpvphost.server

import java.io.ByteArrayOutputStream
import java.net.ServerSocket
import java.net.Socket
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Assert.fail
import org.junit.Test

class HostHttpServerTest {
    private val log = ServerLog()
    private var server: HostHttpServer? = null

    private val blob = PayloadBuilder.build(
        exact = mapOf("GET /static" to "{\"canned\":true}", "POST /static" to "{\"posted\":true}"),
        prefixes = listOf("/rule/" to "{\"rule\":true}"),
    )

    @After
    fun tearDown() {
        server?.stop()
    }

    private fun start(dynamic: DynamicHook = DynamicHook { null }): Int {
        val payload = (Payload.parse(blob) as PayloadResult.Loaded).payload
        val s = HostHttpServer(0, RouteResolver(payload, HostConfig(), dynamic), log)
        s.start()
        server = s
        return s.boundPort
    }

    /** Sends one raw request with `Connection: close` and returns the whole response text. */
    private fun exchange(port: Int, request: String): String {
        Socket("127.0.0.1", port).use { socket ->
            socket.soTimeout = 5000
            socket.getOutputStream().write(request.toByteArray(Charsets.ISO_8859_1))
            val out = ByteArrayOutputStream()
            socket.getInputStream().copyTo(out)
            return out.toString("ISO-8859-1")
        }
    }

    private fun get(port: Int, path: String, method: String = "GET", body: String = ""): String =
        exchange(
            port,
            "$method $path HTTP/1.1\r\nHost: x\r\nConnection: close\r\nContent-Length: ${body.length}\r\n\r\n$body")

    private fun bodyOf(response: String): String = response.substringAfter("\r\n\r\n")

    @Test
    fun staticRouteReturnsCannedBody() {
        val response = get(start(), "/static?stoken=abc")
        assertTrue(response.startsWith("HTTP/1.1 200 OK"))
        assertEquals("{\"canned\":true}", bodyOf(response))
        assertEquals("{\"posted\":true}", bodyOf(get(server!!.boundPort, "/static", "POST", "{}")))
    }

    @Test
    fun prefixRuleThenDefaultEnvelope() {
        val port = start()
        assertEquals("{\"rule\":true}", bodyOf(get(port, "/rule/anything")))
        assertEquals(PayloadBuilder.DEFAULT_BODY, bodyOf(get(port, "/nowhere")))
    }

    @Test
    fun headReturnsEmpty200() {
        val response = get(start(), "/static", "HEAD")
        assertTrue(response.startsWith("HTTP/1.1 200 OK"))
        assertTrue(response.contains("Content-Length: 0"))
        assertEquals("", bodyOf(response))
    }

    @Test
    fun dynamicHookWinsOverStaticAndEmptyFallsThrough() {
        val port = start { request ->
            when (request.path) {
                "/static" -> "{\"dynamic\":true}".toByteArray()
                "/rule/x" -> ByteArray(0)
                else -> null
            }
        }
        assertEquals("{\"dynamic\":true}", bodyOf(get(port, "/static")))
        assertEquals("{\"rule\":true}", bodyOf(get(port, "/rule/x")))
    }

    @Test
    fun throwingDynamicHookFallsBackToStaticResolution() {
        val port = start { throw IllegalStateException("boom") }
        assertEquals("{\"canned\":true}", bodyOf(get(port, "/static")))
    }

    @Test
    fun traversalAndMalformedTargetsAreRejected() {
        val port = start()
        for (target in listOf("/a/../b", "/%2e%2e/x", "/a/%2E%2e", "relative", "/a%00b", "/a\\..\\b")) {
            val response = exchange(port, "GET $target HTTP/1.1\r\nConnection: close\r\n\r\n")
            assertTrue("$target -> $response", response.startsWith("HTTP/1.1 400"))
        }
    }

    @Test
    fun oversizedHeadersAreRejected() {
        val port = start()
        val big = "X: " + "a".repeat(HttpRequestReader.MAX_HEAD)
        val response = exchange(port, "GET /static HTTP/1.1\r\n$big\r\n\r\n")
        assertTrue(response, response.startsWith("HTTP/1.1 431"))
    }

    @Test
    fun oversizedBodyIsTruncatedButStillAnswered() {
        val seen = ArrayList<Int>()
        val port = start { request ->
            seen.add(request.body.size)
            null
        }
        val body = "b".repeat(HttpRequestReader.MAX_BODY + 1000)
        val response = get(port, "/static", "POST", body)
        assertEquals("{\"posted\":true}", bodyOf(response))
        assertEquals(listOf(HttpRequestReader.MAX_BODY), seen)
    }

    @Test
    fun keepAliveServesSequentialRequestsOnOneConnection() {
        val port = start()
        Socket("127.0.0.1", port).use { socket ->
            socket.soTimeout = 5000
            val out = socket.getOutputStream()
            repeat(3) {
                out.write("GET /static HTTP/1.1\r\nHost: x\r\n\r\n".toByteArray())
                out.flush()
                assertEquals("{\"canned\":true}", readOneResponseBody(socket))
            }
        }
    }

    private fun readOneResponseBody(socket: Socket): String {
        val input = socket.getInputStream()
        val head = StringBuilder()
        while (!head.endsWith("\r\n\r\n")) head.append(input.read().toChar())
        val length = Regex("Content-Length: (\\d+)").find(head)!!.groupValues[1].toInt()
        return String(ByteArray(length).also { buf ->
            var got = 0
            while (got < length) got += input.read(buf, got, length - got)
        })
    }

    @Test
    fun portInUseIsReportedActionably() {
        ServerSocket(0).use { taken ->
            val payload = (Payload.parse(blob) as PayloadResult.Loaded).payload
            val second = HostHttpServer(taken.localPort, RouteResolver(payload, HostConfig()), log)
            try {
                second.start()
                fail("expected the port conflict to be reported")
            } catch (e: HostStartException) {
                assertTrue(e.message!!, e.message!!.contains("already in use"))
                assertTrue(e.message!!.contains("--server-port ${taken.localPort}"))
            } finally {
                second.stop()
            }
        }
    }

    @Test
    fun stopClosesListenerAndLiveConnections() {
        val port = start()
        val idle = Socket("127.0.0.1", port)
        server!!.stop()
        idle.soTimeout = 5000
        assertEquals(-1, idle.getInputStream().read())
        idle.close()
        try {
            Socket("127.0.0.1", port).close()
            fail("listener should be closed after stop")
        } catch (expected: java.io.IOException) {
            // connection refused: the listener is gone
        }
    }

    @Test
    fun runtimeRejectsCorruptPayloadWithoutBinding() {
        val corrupt = blob.copyOf().also { it[blob.size - 8] = (it[blob.size - 8] + 1).toByte() }
        val result = HostRuntime.start(corrupt, HostConfig(port = 18089))
        assertTrue(result is StartResult.Failed)
        assertTrue((result as StartResult.Failed).message.contains("failed validation"))
        assertEquals(false, HostRuntime.running.value)
        assertTrue(HostRuntime.start(ByteArray(0), HostConfig(port = 18089)).let {
            (it as StartResult.Failed).message.contains("prepare-assets.sh")
        })
    }
}
