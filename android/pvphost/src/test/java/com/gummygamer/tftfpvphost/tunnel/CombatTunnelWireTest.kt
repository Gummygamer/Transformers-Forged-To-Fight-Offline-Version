package com.gummygamer.tftfpvphost.tunnel

import org.junit.After
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Rule
import org.junit.Test
import org.junit.rules.TemporaryFolder
import java.io.DataInputStream
import java.io.InputStream
import java.io.OutputStream
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress
import java.util.concurrent.CopyOnWriteArrayList
import java.util.concurrent.LinkedBlockingQueue
import java.util.concurrent.TimeUnit
import javax.net.ssl.SSLServerSocket
import javax.net.ssl.SSLSocket

/**
 * Talks the relay wire protocol directly to CombatTunnel through a scripted TLS peer, to check
 * framing and failure handling the well-behaved real relay never produces.
 */
class CombatTunnelWireTest {
    @get:Rule val temp = TemporaryFolder()

    private lateinit var certificate: TestCertificate
    private lateinit var server: SSLServerSocket
    private val closeables = CopyOnWriteArrayList<AutoCloseable>()

    @Before
    fun setUp() {
        RelayProcess.assumeToolchain()
        certificate = TestCertificate.create(temp.newFolder())
        TunnelClient.socketFactory = certificate.clientContext.socketFactory
        server = certificate.serverContext.serverSocketFactory.createServerSocket(0, 1, InetAddress.getLoopbackAddress()) as SSLServerSocket
    }

    @After
    fun tearDown() {
        TunnelClient.socketFactory = null
        closeables.reversed().forEach { try { it.close() } catch (_: Exception) { } }
        server.close()
    }

    private class Scripted(val socket: SSLSocket) {
        val input: InputStream = socket.getInputStream()
        val output: OutputStream = socket.getOutputStream()
        fun readLine(): String {
            val line = StringBuilder()
            while (true) {
                val next = input.read()
                if (next < 0 || next == '\n'.code) return line.toString()
                line.append(next.toChar())
            }
        }
        fun readFrame(): ByteArray {
            val length = DataInputStream(input).readInt()
            return ByteArray(length).also { DataInputStream(input).readFully(it) }
        }
    }

    private fun startTunnel(role: TunnelConfig.Role = TunnelConfig.Role.JOIN, onDisconnect: (String) -> Unit = {}): Triple<CombatTunnel, Scripted, Int> {
        val invitation = TunnelConfig.newInvitation()
        val combatPort = freeUdpPort()
        val config = if (role == TunnelConfig.Role.JOIN) {
            TunnelConfig("localhost", server.localPort, invitation.session, invitation.joinToken, role, combatPort = combatPort)
        } else {
            TunnelConfig("localhost", server.localPort, invitation.session, invitation.hostToken, role, invitation.joinToken, combatPort = combatPort)
        }
        val accepted = LinkedBlockingQueue<SSLSocket>()
        Thread { accepted.put((server.accept() as SSLSocket).also { it.startHandshake() }) }.apply { isDaemon = true; start() }
        val tunnel = CombatTunnel(config, onDisconnect).also { closeables += it }
        tunnel.start()
        val peer = Scripted(accepted.poll(10, TimeUnit.SECONDS)!!).also { closeables += it.socket }
        return Triple(tunnel, peer, combatPort)
    }

    private fun hook(): DatagramSocket = DatagramSocket(0, InetAddress.getLoopbackAddress()).also {
        it.soTimeout = 5_000
        closeables += it
    }

    private fun send(hook: DatagramSocket, port: Int, payload: ByteArray) =
        hook.send(DatagramPacket(payload, payload.size, InetAddress.getLoopbackAddress(), port))

    @Test
    fun helloIsTheDocumentedBoundedJsonLine() {
        val (_, relay, _) = startTunnel(TunnelConfig.Role.HOST)
        val line = relay.readLine()
        assertTrue(line.length < 4096)
        assertTrue(line.startsWith("{\"version\":1,\"session\":\""))
        assertTrue(line.contains("\"role\":\"host\""))
        assertTrue(line.contains("\"channel\":\"combat\""))
        assertTrue(line.contains("\"invite\":\""))
        val (_, joinRelay, _) = startTunnel(TunnelConfig.Role.JOIN)
        assertFalse(joinRelay.readLine().contains("invite"))
    }

    @Test
    fun outboundDatagramsAreLengthPrefixedOneFramePerPacket() {
        val (_, relay, port) = startTunnel()
        relay.readLine()
        val game = hook()
        val packets = listOf(ByteArray(1) { 7 }, ByteArray(200) { it.toByte() }, ByteArray(512) { (it * 5).toByte() })
        packets.forEach { send(game, port, it) }
        packets.forEach { assertArrayEquals(it, relay.readFrame()) }
    }

    @Test
    fun fragmentedInboundFramesAreReassembledIntoWholeDatagrams() {
        val (_, relay, port) = startTunnel()
        relay.readLine()
        val game = hook()
        send(game, port, "learn-peer".toByteArray())
        relay.readFrame()

        val payloadA = "alpha".toByteArray()
        val payloadB = ByteArray(300) { it.toByte() }
        val wire = frame(payloadA) + frame(payloadB)
        // One byte at a time is the worst case TCP may deliver.
        wire.forEach { relay.output.write(it.toInt()); relay.output.flush() }

        val buffer = DatagramPacket(ByteArray(1024), 1024)
        game.receive(buffer)
        assertArrayEquals(payloadA, buffer.data.copyOf(buffer.length))
        buffer.length = 1024
        game.receive(buffer)
        assertArrayEquals(payloadB, buffer.data.copyOf(buffer.length))
    }

    @Test
    fun invalidInboundFramesDisconnectTheBridge() {
        for (header in listOf(0, 513, Int.MAX_VALUE, -1)) {
            val reasons = LinkedBlockingQueue<String>()
            val (tunnel, relay, port) = startTunnel(onDisconnect = { reasons.put(it) })
            relay.readLine()
            relay.output.write(byteArrayOf((header ushr 24).toByte(), (header ushr 16).toByte(), (header ushr 8).toByte(), header.toByte()))
            relay.output.flush()
            val reason = reasons.poll(5, TimeUnit.SECONDS)
            assertTrue("frame header $header must disconnect", reason != null)
            assertFalse(reason!!.contains("session"))
            tunnel.close()
            // The UDP port is released once the bridge is closed.
            eventually(message = "UDP port $port to be released") {
                try {
                    DatagramSocket(java.net.InetSocketAddress(InetAddress.getLoopbackAddress(), port)).close()
                    true
                } catch (_: java.net.BindException) { false }
            }
        }
    }

    @Test
    fun peerEofMidFrameAndCleanCloseBothReportDisconnect() {
        for (truncated in listOf(true, false)) {
            val reasons = LinkedBlockingQueue<String>()
            val (_, relay, _) = startTunnel(onDisconnect = { reasons.put(it) })
            relay.readLine()
            if (truncated) { relay.output.write(byteArrayOf(0, 0, 1, 0, 1, 2, 3)); relay.output.flush() }
            relay.socket.close()
            assertTrue("EOF (truncated=$truncated) must disconnect", reasons.poll(5, TimeUnit.SECONDS) != null)
        }
    }

    @Test
    fun inboundDatagramsAreOnlyEmittedToTheLocalGameHook() {
        val (_, relay, port) = startTunnel()
        relay.readLine()
        val game = hook()
        // Before any local packet the bridge has no destination and must drop, not broadcast.
        relay.output.write(frame("early".toByteArray())); relay.output.flush()
        game.soTimeout = 300
        try {
            game.receive(DatagramPacket(ByteArray(64), 64))
            throw AssertionError("a packet arrived before the bridge knew the game hook")
        } catch (_: java.net.SocketTimeoutException) { }
        send(game, port, "hello".toByteArray()); relay.readFrame()
        relay.output.write(frame("late".toByteArray())); relay.output.flush()
        game.soTimeout = 5_000
        val packet = DatagramPacket(ByteArray(64), 64)
        game.receive(packet)
        assertEquals("late", String(packet.data, 0, packet.length))
        assertEquals(InetAddress.getLoopbackAddress(), packet.address)
    }

    private fun frame(payload: ByteArray): ByteArray =
        byteArrayOf((payload.size ushr 24).toByte(), (payload.size ushr 16).toByte(), (payload.size ushr 8).toByte(), payload.size.toByte()) + payload
}
