package com.gummygamer.tftfpvphost.tunnel

import org.junit.After
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Rule
import org.junit.Test
import org.junit.rules.TemporaryFolder
import java.io.IOException
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress
import java.net.InetSocketAddress
import java.util.concurrent.CopyOnWriteArrayList
import java.util.concurrent.Executors
import java.util.concurrent.TimeUnit

/**
 * Drives the real Android bridges through the real Python TLS relay. The two peers only share the
 * relay: each has its own loopback ports, the joiner never learns the host game's port, and every
 * assertion about game traffic goes through the join peer's local bridge endpoints.
 *
 * This is a single-machine simulation of separate networks (no network namespaces are available to
 * the build); it verifies the transport, not Internet reachability.
 */
class RelayTunnelIntegrationTest {
    @get:Rule val temp = TemporaryFolder()

    private lateinit var certificate: TestCertificate
    private lateinit var relay: RelayProcess
    private val closeables = CopyOnWriteArrayList<AutoCloseable>()

    @Before
    fun setUp() {
        RelayProcess.assumeToolchain()
        certificate = TestCertificate.create(temp.newFolder())
        TunnelClient.socketFactory = certificate.clientContext.socketFactory
        relay = RelayProcess.start(certificate, temp.newFile("relay.log"))
        closeables += relay
    }

    @After
    fun tearDown() {
        TunnelClient.socketFactory = null
        closeables.reversed().forEach { try { it.close() } catch (_: Exception) { } }
    }

    private class Peer(
        val invitation: Invitation,
        val host: TunnelSession,
        val join: TunnelSession,
        val hostHttp: Int,
        val joinHttp: Int,
        val hostCombat: Int,
        val joinCombat: Int,
    ) {
        val game get() = hostHttp
    }

    private fun configs(invitation: Invitation, hostHttp: Int, joinHttp: Int, hostCombat: Int, joinCombat: Int, joinToken: String = invitation.joinToken) = Pair(
        TunnelConfig("localhost", relay.port, invitation.session, invitation.hostToken, TunnelConfig.Role.HOST,
            invitation.joinToken, hostHttp, hostCombat),
        TunnelConfig("localhost", relay.port, invitation.session, joinToken, TunnelConfig.Role.JOIN,
            httpPort = joinHttp, combatPort = joinCombat),
    )

    private fun startPair(label: String = "host", joinToken: String? = null): Peer {
        val invitation = TunnelConfig.newInvitation()
        val hostHttp = freeTcpPort(); val joinHttp = freeTcpPort()
        val hostCombat = freeUdpPort(); val joinCombat = freeUdpPort()
        closeables += FakeGameServer(hostHttp, label)
        val (hostConfig, joinConfig) = configs(invitation, hostHttp, joinHttp, hostCombat, joinCombat, joinToken ?: invitation.joinToken)
        val host = TunnelSession(hostConfig).also { closeables += it }
        val join = TunnelSession(joinConfig).also { closeables += it }
        assertTrue("host tunnel must become ready", host.start())
        assertTrue("join tunnel must become ready", join.start())
        return Peer(invitation, host, join, hostHttp, joinHttp, hostCombat, joinCombat)
    }

    private class Hook(private val bridgePort: Int) : AutoCloseable {
        val socket = DatagramSocket(0, InetAddress.getLoopbackAddress()).apply { soTimeout = 5_000 }
        fun send(payload: ByteArray) =
            socket.send(DatagramPacket(payload, payload.size, InetAddress.getLoopbackAddress(), bridgePort))
        fun receive(timeoutMs: Int = 5_000): ByteArray {
            socket.soTimeout = timeoutMs
            val packet = DatagramPacket(ByteArray(2048), 2048)
            socket.receive(packet)
            return packet.data.copyOf(packet.length)
        }
        fun receiveOrNull(timeoutMs: Int): ByteArray? = try { receive(timeoutMs) } catch (_: java.net.SocketTimeoutException) { null }
        override fun close() = socket.close()
    }

    /** Each side has to speak once so its bridge learns where the local game hook lives. */
    private fun handshakeHooks(peer: Peer): Pair<Hook, Hook> {
        val hostHook = Hook(peer.hostCombat).also { closeables += it }
        val joinHook = Hook(peer.joinCombat).also { closeables += it }
        hostHook.send("HELLO|room|host".toByteArray())
        joinHook.send("HELLO|room|join".toByteArray())
        assertEquals("HELLO|room|host", String(joinHook.receive()))
        assertEquals("HELLO|room|join", String(hostHook.receive()))
        return hostHook to joinHook
    }

    @Test
    fun httpTrafficCrossesTheRelayInBothDirectionsOnlyThroughTheJoinBridge() {
        // Without the tunnel the joiner's game endpoint does not exist at all.
        val joinHttp = freeTcpPort()
        assertThrowsIO { httpGet(joinHttp, "/pvp/lobby", 500) }

        val peer = startPair("alpha")
        assertNotEquals(peer.hostHttp, peer.joinHttp)
        repeat(3) { index ->
            assertEquals("alpha:/pvp/lobby/$index", String(httpGet(peer.joinHttp, "/pvp/lobby/$index")))
        }
        assertArrayEquals(FakeGameServer.BIG_BODY, httpGet(peer.joinHttp, "/big", 30_000))
    }

    @Test
    fun concurrentHttpRequestsAllCompleteWithTheirOwnResponses() {
        val peer = startPair("beta")
        val pool = Executors.newFixedThreadPool(4)
        try {
            val results = (0 until 4).map { index ->
                pool.submit<String> { String(httpGet(peer.joinHttp, "/n/$index", 30_000)) }
            }.map { it.get(60, TimeUnit.SECONDS) }
            assertEquals((0 until 4).map { "beta:/n/$it" }, results)
        } finally {
            pool.shutdownNow()
        }
    }

    @Test
    fun combatDatagramsKeepBoundariesAndOversizedOrEmptyPacketsAreDropped() {
        val peer = startPair()
        val (hostHook, joinHook) = handshakeHooks(peer)

        val sizes = listOf(1, 17, 255, 512)
        for (size in sizes) {
            val toHost = ByteArray(size) { (it + size).toByte() }
            val toJoin = ByteArray(size) { (it * 3 + size).toByte() }
            joinHook.send(toHost)
            hostHook.send(toJoin)
            assertArrayEquals("join->host size $size", toHost, hostHook.receive())
            assertArrayEquals("host->join size $size", toJoin, joinHook.receive())
        }

        // A 513-byte datagram must vanish rather than arrive truncated as 512 bytes.
        joinHook.send(ByteArray(513) { 9 })
        joinHook.send(ByteArray(0))
        joinHook.send("after".toByteArray())
        assertEquals("after", String(hostHook.receive()))
        assertNull(hostHook.receiveOrNull(200))

        // A burst larger than the per-direction queue stays ordered and undamaged.
        repeat(200) { joinHook.send("burst-$it".toByteArray()) }
        val received = (0 until 200).map { String(hostHook.receive(10_000)) }
        assertEquals((0 until 200).map { "burst-$it" }, received)
    }

    @Test
    fun sessionsAreIsolatedFromEachOther() {
        val first = startPair("first")
        val second = startPair("second")
        assertEquals("first:/x", String(httpGet(first.joinHttp, "/x")))
        assertEquals("second:/x", String(httpGet(second.joinHttp, "/x")))

        val (firstHost, firstJoin) = handshakeHooks(first)
        val (secondHost, secondJoin) = handshakeHooks(second)
        firstJoin.send("only-first".toByteArray())
        secondJoin.send("only-second".toByteArray())
        assertEquals("only-first", String(firstHost.receive()))
        assertEquals("only-second", String(secondHost.receive()))
        assertNull(firstHost.receiveOrNull(200))
        assertNull(secondHost.receiveOrNull(200))
    }

    @Test
    fun wrongInvitationIsRejectedAndNoGameDataFlows() {
        val invitation = TunnelConfig.newInvitation()
        val forged = TunnelConfig.newInvitation().joinToken
        val hostHttp = freeTcpPort(); val joinHttp = freeTcpPort()
        closeables += FakeGameServer(hostHttp, "secret-game")
        val (hostConfig, joinConfig) = configs(invitation, hostHttp, joinHttp, freeUdpPort(), freeUdpPort(), forged)
        val host = TunnelSession(hostConfig).also { closeables += it }
        val join = TunnelSession(joinConfig).also { closeables += it }
        assertTrue(host.start())
        join.start()
        eventually(message = "rejected join to leave READY") { join.status.value.state != TunnelState.READY }
        assertTrue(join.status.value.state in setOf(TunnelState.DISCONNECTED, TunnelState.FAILED))
        assertNoCredentials(join.status.value.message, forged, invitation.session, invitation.hostToken, invitation.joinToken)
        assertThrowsIO { String(httpGet(joinHttp, "/pvp/lobby", 1_000)) }
        assertEquals(TunnelState.READY, host.status.value.state)
    }

    @Test
    fun joinWithoutALiveHostIsRejected() {
        val invitation = TunnelConfig.newInvitation()
        val (_, joinConfig) = configs(invitation, freeTcpPort(), freeTcpPort(), freeUdpPort(), freeUdpPort())
        val join = TunnelSession(joinConfig).also { closeables += it }
        join.start()
        eventually(message = "orphan join to be rejected") { join.status.value.state != TunnelState.READY }
        assertNotEquals(TunnelState.READY, join.status.value.state)
        assertNoCredentials(join.status.value.message, invitation.session, invitation.joinToken)
    }

    @Test
    fun hostShutdownDisconnectsTheJoinerAndAFreshSessionWorks() {
        val peer = startPair("one")
        assertEquals("one:/a", String(httpGet(peer.joinHttp, "/a")))
        peer.host.close()
        eventually(message = "joiner to observe host disconnect") { peer.join.status.value.state == TunnelState.DISCONNECTED }
        assertThrowsIO { httpGet(peer.joinHttp, "/a", 1_000) }

        val next = startPair("two")
        assertEquals("two:/a", String(httpGet(next.joinHttp, "/a")))
    }

    @Test
    fun relayFailureDisconnectsBothPeersWithNoDirectFallback() {
        val peer = startPair("gamma")
        assertEquals("gamma:/a", String(httpGet(peer.joinHttp, "/a")))
        relay.close()
        eventually(message = "host disconnect") { peer.host.status.value.state == TunnelState.DISCONNECTED }
        eventually(message = "join disconnect") { peer.join.status.value.state == TunnelState.DISCONNECTED }
        assertThrowsIO { httpGet(peer.joinHttp, "/a", 1_000) }
    }

    @Test
    fun repeatedConnectDisconnectReleasesThreadsAndPorts() {
        repeat(6) { round ->
            val peer = startPair("round$round")
            assertEquals("round$round:/a", String(httpGet(peer.joinHttp, "/a")))
            val (hostHook, joinHook) = handshakeHooks(peer)
            joinHook.send("x".toByteArray())
            assertEquals("x", String(hostHook.receive()))
            peer.join.close()
            peer.host.close()
            // The ports must be immediately reusable by the next tunnel.
            java.net.ServerSocket(peer.joinHttp, 1, InetAddress.getLoopbackAddress()).close()
            DatagramSocket(InetSocketAddress(InetAddress.getLoopbackAddress(), peer.joinCombat)).close()
            DatagramSocket(InetSocketAddress(InetAddress.getLoopbackAddress(), peer.hostCombat)).close()
        }
        eventually(message = "tunnel threads to exit") { tunnelThreads().isEmpty() }
    }

    @Test
    fun unreachableRelayFailsQuicklyWithoutLeakingCredentials() {
        val invitation = TunnelConfig.newInvitation()
        val closedPort = freeTcpPort()
        val config = TunnelConfig("localhost", closedPort, invitation.session, invitation.hostToken,
            TunnelConfig.Role.HOST, invitation.joinToken, freeTcpPort(), freeUdpPort())
        val session = TunnelSession(config).also { closeables += it }
        val started = System.nanoTime()
        assertFalse(session.start())
        assertTrue(TimeUnit.NANOSECONDS.toSeconds(System.nanoTime() - started) < 8)
        assertEquals(TunnelState.FAILED, session.status.value.state)
        assertNoCredentials(session.status.value.message, invitation.session, invitation.hostToken, invitation.joinToken)
        eventually(message = "failed start to release threads") { tunnelThreads().isEmpty() }
    }

    @Test
    fun relayCertificateAndHostnameAreVerified() {
        val invitation = TunnelConfig.newInvitation()
        fun attempt(host: String): Throwable? = try {
            TunnelClient.connect(
                TunnelConfig(host, relay.port, invitation.session, invitation.hostToken, TunnelConfig.Role.HOST, invitation.joinToken),
                "http",
            ).close()
            null
        } catch (failure: IOException) { failure }

        assertNull("trusted certificate for the matching name", attempt("localhost"))
        assertTrue("name mismatch must fail the handshake", attempt("127.0.0.1") is javax.net.ssl.SSLException)
        TunnelClient.socketFactory = null
        assertTrue("an untrusted certificate must fail the handshake", attempt("localhost") is javax.net.ssl.SSLException)
    }

    private fun tunnelThreads(): List<Thread> = Thread.getAllStackTraces().keys.filter {
        it.isAlive && (it.name.startsWith("tftf-http") || it.name.startsWith("tftf-combat") || it.name.startsWith("tftf-tunnel-f") || it.name.startsWith("tftf-tunnel-r"))
    }

    private fun assertNoCredentials(message: String?, vararg secrets: String) {
        secrets.forEach { assertFalse("status leaked a credential", message.orEmpty().contains(it)) }
    }

    private fun assertThrowsIO(block: () -> Unit) {
        try {
            block()
        } catch (_: IOException) {
            return
        }
        throw AssertionError("expected an IOException")
    }
}
