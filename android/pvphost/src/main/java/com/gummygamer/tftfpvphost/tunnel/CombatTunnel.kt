package com.gummygamer.tftfpvphost.tunnel

import java.io.BufferedInputStream
import java.io.BufferedOutputStream
import java.io.IOException
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress
import java.net.InetSocketAddress
import java.net.Socket
import java.util.concurrent.atomic.AtomicReference
import java.util.concurrent.atomic.AtomicBoolean

/** Bridges the native hook's UDP/8777 traffic over a separate bounded TLS channel. */
class CombatTunnel(
    private val config: TunnelConfig,
    private val onDisconnected: (String) -> Unit = {},
) : AutoCloseable {
    private val stopped = AtomicBoolean(false)
    private val localPeer = AtomicReference<InetSocketAddress?>(null)
    private var relay: Socket? = null
    private var udp: DatagramSocket? = null
    private var sender: Thread? = null
    private var receiver: Thread? = null

    fun start() {
        config.validationError()?.let { throw IOException(it) }
        check(sender == null) { "tunnel already started" }
        val channel = TunnelClient.connect(config, "combat")
        // The native hook sends to loopback:8777 from an ephemeral source port. The
        // bridge owns 8777 and remembers that source so remote packets can be sent
        // back to the hook without requiring two processes to bind the same port.
        val socket = DatagramSocket(InetSocketAddress(InetAddress.getLoopbackAddress(), config.combatPort))
        relay = channel
        udp = socket
        sender = Thread({ sendLoop(channel, socket) }, "tftf-combat-send").apply { isDaemon = true; start() }
        receiver = Thread({ receiveLoop(channel, socket) }, "tftf-combat-receive").apply { isDaemon = true; start() }
    }

    override fun close() {
        if (!stopped.compareAndSet(false, true)) return
        udp?.close()
        TunnelClient.closeQuietly(relay)
        sender?.interrupt(); receiver?.interrupt()
        if (sender != Thread.currentThread()) {
            try { sender?.join(2_000) } catch (_: InterruptedException) { Thread.currentThread().interrupt() }
        }
        if (receiver != Thread.currentThread()) {
            try { receiver?.join(2_000) } catch (_: InterruptedException) { Thread.currentThread().interrupt() }
        }
    }

    private fun sendLoop(relay: Socket, udp: DatagramSocket) {
        try {
            // One spare byte lets an oversized datagram be recognised and dropped rather
            // than silently truncated into a valid-looking 512-byte frame.
            val input = DatagramPacket(ByteArray(TunnelConfig.MAX_COMBAT_PACKET + 1), TunnelConfig.MAX_COMBAT_PACKET + 1)
            val output = BufferedOutputStream(relay.getOutputStream())
            while (!stopped.get()) {
                input.length = input.data.size
                udp.receive(input)
                if (!input.address.isLoopbackAddress) continue
                localPeer.set(InetSocketAddress(input.address, input.port))
                if (input.length > TunnelConfig.MAX_COMBAT_PACKET) continue
                TunnelClient.writeFrame(output, input.data, input.length)
            }
        } catch (_: IOException) {
            if (!stopped.get()) {
                onDisconnected("The relay combat channel disconnected.")
                close()
            }
        }
    }

    private fun receiveLoop(relay: Socket, udp: DatagramSocket) {
        try {
            val input = BufferedInputStream(relay.getInputStream())
            val header = ByteArray(4)
            while (!stopped.get()) {
                TunnelClient.readFully(input, header)
                val length = TunnelClient.frameLength(header)
                val packet = ByteArray(length)
                TunnelClient.readFully(input, packet)
                val peer = localPeer.get() ?: continue
                udp.send(DatagramPacket(packet, packet.size, peer.address, peer.port))
            }
        } catch (_: IOException) {
            if (!stopped.get()) {
                onDisconnected("The relay combat channel disconnected.")
                close()
            }
        }
    }
}
