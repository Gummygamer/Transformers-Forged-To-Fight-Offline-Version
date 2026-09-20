package com.gummygamer.tftfpvphost.tunnel

import java.io.IOException
import java.net.InetAddress
import java.net.ServerSocket
import java.net.Socket
import java.util.concurrent.atomic.AtomicBoolean

/**
 * Reverse HTTP bridge. A join participant exposes loopback:8080 to its game;
 * a host participant opens the matching relay channel and connects it to its
 * loopback game server. Each game TCP connection gets its own authenticated channel.
 */
class HttpTunnelBridge(
    private val config: TunnelConfig,
    private val onDisconnected: (String) -> Unit = {},
) : AutoCloseable {
    private val stopped = AtomicBoolean(false)
    private val live = java.util.Collections.synchronizedSet(HashSet<Socket>())
    private var listener: ServerSocket? = null
    private var worker: Thread? = null

    fun start(): Int {
        config.validationError()?.let { throw IOException(it) }
        check(worker == null) { "tunnel already started" }
        worker = Thread({ if (config.role == TunnelConfig.Role.JOIN) joinLoop() else hostLoop() }, "tftf-http-tunnel").apply {
            isDaemon = true
            start()
        }
        return config.httpPort
    }

    override fun close() {
        if (!stopped.compareAndSet(false, true)) return
        try { listener?.close() } catch (_: IOException) { }
        synchronized(live) { live.toList() }.forEach { socket -> TunnelClient.closeQuietly(socket) }
        worker?.interrupt()
        if (worker != Thread.currentThread()) {
            try { worker?.join(2_000) } catch (_: InterruptedException) { Thread.currentThread().interrupt() }
        }
    }

    private fun joinLoop() {
        try {
            listener = ServerSocket(config.httpPort, 16, InetAddress.getLoopbackAddress())
            while (!stopped.get()) {
                val local = listener!!.accept()
                register(local)
                Thread({
                    try {
                        bridge(local, connectRelay())
                    } catch (_: IOException) {
                        try { local.close() } catch (_: IOException) { }
                        unregister(local)
                    }
                }, "tftf-http-join").apply { isDaemon = true; start() }
            }
        } catch (_: IOException) {
            if (!stopped.get()) {
                onDisconnected("The local HTTP tunnel could not bind or accept connections.")
                close()
            }
        }
    }

    private fun hostLoop() {
        while (!stopped.get()) {
            try {
                val relay = connectRelay()
                val local = Socket()
                local.tcpNoDelay = true
                local.connect(InetAddress.getLoopbackAddress().let { java.net.InetSocketAddress(it, config.httpPort) }, TunnelConfig.CONNECT_TIMEOUT_MS)
                register(relay)
                register(local)
                TunnelClient.copyBidirectional(relay, local)
                unregister(relay); unregister(local)
            } catch (_: IOException) {
                if (!stopped.get()) {
                    onDisconnected("The relay HTTP channel disconnected.")
                    try { Thread.sleep(250) } catch (_: InterruptedException) { Thread.currentThread().interrupt() }
                }
            }
        }
    }

    private fun connectRelay(): Socket = TunnelClient.connect(config, "http")

    private fun bridge(local: Socket, relay: Socket) {
        try {
            register(relay)
            TunnelClient.copyBidirectional(local, relay)
        } catch (_: IOException) {
            TunnelClient.closeQuietly(local)
            TunnelClient.closeQuietly(relay)
        } finally {
            unregister(local); unregister(relay)
        }
    }

    private fun register(socket: Socket) { live.add(socket) }
    private fun unregister(socket: Socket) { live.remove(socket); TunnelClient.closeQuietly(socket) }
}
