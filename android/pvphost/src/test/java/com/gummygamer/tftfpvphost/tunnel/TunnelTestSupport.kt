package com.gummygamer.tftfpvphost.tunnel

import org.junit.Assume
import java.io.ByteArrayOutputStream
import java.io.File
import java.net.DatagramSocket
import java.net.InetAddress
import java.net.InetSocketAddress
import java.net.ServerSocket
import java.net.Socket
import java.security.KeyStore
import java.security.cert.CertificateFactory
import java.util.concurrent.TimeUnit
import javax.net.ssl.KeyManagerFactory
import javax.net.ssl.SSLContext
import javax.net.ssl.TrustManagerFactory

/** Throwaway localhost certificate plus the trust/key material needed by test peers. */
class TestCertificate private constructor(val certPem: File, val keyPem: File, val keystore: File) {
    val clientContext: SSLContext by lazy {
        val trust = KeyStore.getInstance(KeyStore.getDefaultType()).apply {
            load(null, null)
            certPem.inputStream().use { setCertificateEntry("relay", CertificateFactory.getInstance("X.509").generateCertificate(it)) }
        }
        SSLContext.getInstance("TLS").apply {
            init(null, TrustManagerFactory.getInstance(TrustManagerFactory.getDefaultAlgorithm()).apply { init(trust) }.trustManagers, null)
        }
    }

    val serverContext: SSLContext by lazy {
        val store = KeyStore.getInstance("PKCS12").apply { keystore.inputStream().use { load(it, PASSWORD.toCharArray()) } }
        SSLContext.getInstance("TLS").apply {
            init(KeyManagerFactory.getInstance(KeyManagerFactory.getDefaultAlgorithm()).apply { init(store, PASSWORD.toCharArray()) }.keyManagers, null, null)
        }
    }

    companion object {
        private const val PASSWORD = "throwaway"

        fun create(dir: File): TestCertificate {
            val cert = File(dir, "relay.crt")
            val key = File(dir, "relay.key")
            val store = File(dir, "relay.p12")
            run(dir, "openssl", "req", "-x509", "-newkey", "ec", "-pkeyopt", "ec_paramgen_curve:prime256v1", "-nodes",
                "-keyout", key.path, "-out", cert.path, "-days", "1", "-subj", "/CN=localhost",
                "-addext", "subjectAltName=DNS:localhost")
            run(dir, "openssl", "pkcs12", "-export", "-in", cert.path, "-inkey", key.path, "-out", store.path,
                "-passout", "pass:$PASSWORD")
            return TestCertificate(cert, key, store)
        }

        private fun run(dir: File, vararg command: String) {
            val process = ProcessBuilder(*command).directory(dir).redirectErrorStream(true).start()
            val output = process.inputStream.readBytes().toString(Charsets.UTF_8)
            check(process.waitFor(30, TimeUnit.SECONDS) && process.exitValue() == 0) { "${command[0]} failed: $output" }
        }
    }
}

/** Runs the real Python relay as a subprocess with a TLS listener on loopback. */
class RelayProcess private constructor(private val process: Process, val port: Int) : AutoCloseable {
    override fun close() {
        process.destroy()
        if (!process.waitFor(5, TimeUnit.SECONDS)) process.destroyForcibly().waitFor()
    }

    companion object {
        fun repositoryRoot(): File =
            generateSequence(File(System.getProperty("user.dir")).absoluteFile) { it.parentFile }
                .first { File(it, "tools/internetrelay/relay.py").isFile }

        /** Skips (never silently passes) the calling test when the relay toolchain is unavailable. */
        fun assumeToolchain() {
            Assume.assumeTrue("python3 and openssl are required", listOf("python3", "openssl").all { tool ->
                try {
                    ProcessBuilder(tool, "--version").redirectErrorStream(true).start().also { it.inputStream.readBytes() }.waitFor() == 0
                } catch (_: Exception) { false }
            })
        }

        fun start(certificate: TestCertificate, log: File): RelayProcess {
            val port = freeTcpPort()
            val process = ProcessBuilder(
                "python3", "-m", "tools.internetrelay", "--host", "127.0.0.1", "--port", port.toString(),
                "--cert", certificate.certPem.path, "--key", certificate.keyPem.path,
            ).directory(repositoryRoot()).redirectErrorStream(true).redirectOutput(log).start()
            val deadline = System.nanoTime() + TimeUnit.SECONDS.toNanos(15)
            while (System.nanoTime() < deadline) {
                check(process.isAlive) { "relay exited early: ${log.readText()}" }
                try {
                    Socket().use { it.connect(InetSocketAddress(InetAddress.getLoopbackAddress(), port), 200) }
                    return RelayProcess(process, port)
                } catch (_: java.io.IOException) {
                    Thread.sleep(50)
                }
            }
            process.destroyForcibly()
            error("relay did not start: ${log.readText()}")
        }
    }
}

fun freeTcpPort(): Int = ServerSocket(0, 1, InetAddress.getLoopbackAddress()).use { it.localPort }
fun freeUdpPort(): Int = DatagramSocket(0, InetAddress.getLoopbackAddress()).use { it.localPort }

fun eventually(timeoutMs: Long = 10_000, message: String = "condition", condition: () -> Boolean) {
    val deadline = System.nanoTime() + TimeUnit.MILLISECONDS.toNanos(timeoutMs)
    while (System.nanoTime() < deadline) {
        if (condition()) return
        Thread.sleep(25)
    }
    throw AssertionError("timed out waiting for $message")
}

/** A stand-in for the game's HTTP server: answers every request, then closes the connection. */
class FakeGameServer(val port: Int, private val label: String) : AutoCloseable {
    private val server = ServerSocket(port, 16, InetAddress.getLoopbackAddress())
    private val thread = Thread({
        try {
            while (true) serve(server.accept())
        } catch (_: java.io.IOException) { }
    }, "test-game-server").apply { isDaemon = true; start() }

    private fun serve(socket: Socket) {
        Thread({
            socket.use {
                socket.soTimeout = 10_000
                val head = ByteArrayOutputStream()
                val input = socket.getInputStream()
                while (!head.toString(Charsets.ISO_8859_1).endsWith("\r\n\r\n")) {
                    val next = input.read()
                    if (next < 0) return@use
                    head.write(next)
                }
                val path = head.toString(Charsets.ISO_8859_1).lineSequence().first().split(' ')[1]
                val body = if (path == "/big") BIG_BODY else "$label:$path".toByteArray()
                socket.getOutputStream().write("HTTP/1.1 200 OK\r\nContent-Length: ${body.size}\r\n\r\n".toByteArray() + body)
            }
        }, "test-game-conn").apply { isDaemon = true; start() }
    }

    override fun close() { server.close(); thread.join(2_000) }

    companion object {
        val BIG_BODY = ByteArray(300_000) { (it * 31).toByte() }
    }
}

/** Minimal HTTP/1.1 client; returns the body of the response read until EOF. */
fun httpGet(port: Int, path: String, timeoutMs: Int = 10_000): ByteArray =
    Socket().use { socket ->
        socket.connect(InetSocketAddress(InetAddress.getLoopbackAddress(), port), 2_000)
        socket.soTimeout = timeoutMs
        socket.getOutputStream().write("GET $path HTTP/1.1\r\nHost: game\r\nConnection: close\r\n\r\n".toByteArray())
        val raw = socket.getInputStream().readBytes()
        val split = String(raw, Charsets.ISO_8859_1).indexOf("\r\n\r\n")
        if (split < 0) throw java.io.IOException("no HTTP response (${raw.size} bytes)")
        raw.copyOfRange(split + 4, raw.size)
    }
