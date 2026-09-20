package com.gummygamer.tftfpvphost.tunnel

import java.io.BufferedInputStream
import java.io.BufferedOutputStream
import java.io.EOFException
import java.io.IOException
import java.net.InetSocketAddress
import java.net.Socket
import javax.net.ssl.SSLParameters
import javax.net.ssl.SSLSocket
import javax.net.ssl.SSLSocketFactory

/** Creates authenticated TLS channels; no caller-supplied destination is sent to the relay. */
internal object TunnelClient {
    /** Replaced only by tests that need to trust a throwaway relay certificate. */
    @Volatile
    internal var socketFactory: SSLSocketFactory? = null

    fun connect(config: TunnelConfig, channel: String): SSLSocket {
        require(channel == "http" || channel == "combat") { "unsupported tunnel channel" }
        config.validationError()?.let { throw IOException(it) }
        var raw: Socket? = null
        var socket: SSLSocket? = null
        try {
            raw = Socket()
            raw.tcpNoDelay = true
            raw.connect(InetSocketAddress(config.relayHost, config.relayPort), TunnelConfig.CONNECT_TIMEOUT_MS)
            // Wrapping a connected socket with the relay host preserves SNI and the
            // peer host used by HTTPS endpoint identification.
            val secure = (socketFactory ?: SSLSocketFactory.getDefault() as SSLSocketFactory)
                .createSocket(raw, config.relayHost, config.relayPort, true) as SSLSocket
            socket = secure
            secure.tcpNoDelay = true
            secure.soTimeout = TunnelConfig.IO_TIMEOUT_MS
            val parameters: SSLParameters = secure.sslParameters.apply {
                endpointIdentificationAlgorithm = "HTTPS"
            }
            secure.sslParameters = parameters
            secure.startHandshake()
            val output = BufferedOutputStream(secure.outputStream)
            output.write(helloLine(config, channel).toByteArray(Charsets.US_ASCII))
            output.write('\n'.code)
            output.flush()
            return secure
        } catch (failure: Throwable) {
            closeQuietly(socket)
            closeQuietly(raw)
            if (failure is IOException) throw failure
            throw IOException("secure relay connection failed", failure)
        }
    }

    /**
     * TLS close waits for the peer's close_notify for up to the socket's read timeout. A dead or
     * malicious relay must not keep local ports and threads pinned for that long.
     */
    fun closeQuietly(socket: Socket?) {
        if (socket == null) return
        try { if (socket is SSLSocket) socket.soTimeout = TunnelConfig.CLOSE_TIMEOUT_MS } catch (_: IOException) { }
        try { socket.close() } catch (_: IOException) { }
    }

    /** Every value was validated as [A-Za-z0-9_-], so no JSON escaping is needed. */
    internal fun helloLine(config: TunnelConfig, channel: String): String {
        val invite = if (config.role == TunnelConfig.Role.HOST) ",\"invite\":\"${config.invite}\"" else ""
        return "{\"version\":1,\"session\":\"${config.session}\",\"role\":\"${config.role.name.lowercase()}\"," +
            "\"token\":\"${config.token}\",\"channel\":\"$channel\"$invite}"
    }

    fun copyBidirectional(left: Socket, right: Socket) {
        val failures = java.util.concurrent.atomic.AtomicReference<Throwable?>(null)
        val threads = listOf(
            Thread({ copy(left, right, failures) }, "tftf-tunnel-forward").apply { isDaemon = true },
            Thread({ copy(right, left, failures) }, "tftf-tunnel-reverse").apply { isDaemon = true },
        )
        threads.forEach(Thread::start)
        threads.forEach { thread ->
            try { thread.join(TunnelConfig.IO_TIMEOUT_MS.toLong()) } catch (_: InterruptedException) {
                Thread.currentThread().interrupt()
            }
        }
        closeQuietly(left)
        closeQuietly(right)
        failures.get()
    }

    private fun copy(left: Socket, right: Socket, failures: java.util.concurrent.atomic.AtomicReference<Throwable?>) {
        try {
            val input = BufferedInputStream(left.getInputStream())
            val output = BufferedOutputStream(right.getOutputStream())
            val buffer = ByteArray(16 * 1024)
            while (true) {
                val count = input.read(buffer)
                if (count < 0) break
                if (count == 0) continue
                output.write(buffer, 0, count)
                output.flush()
            }
        } catch (failure: Throwable) {
            failures.compareAndSet(null, failure)
        } finally {
            try { right.shutdownOutput() } catch (_: IOException) { }
        }
    }

    fun readFully(input: BufferedInputStream, buffer: ByteArray) {
        var offset = 0
        while (offset < buffer.size) {
            val count = input.read(buffer, offset, buffer.size - offset)
            if (count < 0) throw EOFException("relay closed during frame")
            offset += count
        }
    }

    fun frameLength(header: ByteArray): Int {
        if (header.size != 4) throw IOException("invalid combat frame header")
        val length = ((header[0].toInt() and 0xff) shl 24) or
            ((header[1].toInt() and 0xff) shl 16) or
            ((header[2].toInt() and 0xff) shl 8) or
            (header[3].toInt() and 0xff)
        if (length !in 1..TunnelConfig.MAX_COMBAT_PACKET) throw IOException("combat frame exceeds 512 bytes")
        return length
    }

    fun writeFrame(output: BufferedOutputStream, packet: ByteArray, length: Int) {
        if (length !in 1..TunnelConfig.MAX_COMBAT_PACKET) return
        output.write((length ushr 24) and 0xff)
        output.write((length ushr 16) and 0xff)
        output.write((length ushr 8) and 0xff)
        output.write(length and 0xff)
        output.write(packet, 0, length)
        output.flush()
    }
}
