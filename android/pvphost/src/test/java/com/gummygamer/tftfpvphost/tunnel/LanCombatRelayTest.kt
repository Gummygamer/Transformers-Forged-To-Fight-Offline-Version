package com.gummygamer.tftfpvphost.tunnel

import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress
import java.net.SocketTimeoutException
import java.util.concurrent.CopyOnWriteArrayList
import java.util.concurrent.atomic.AtomicLong

/** Behavioural port of tools/netrelay/test_netrelay.py against the Kotlin LAN relay. */
class LanCombatRelayTest {
    private val now = AtomicLong(1_000_000L)
    private val closeables = CopyOnWriteArrayList<AutoCloseable>()
    private val logs = CopyOnWriteArrayList<String>()
    private lateinit var relay: LanCombatRelay
    private var port = 0

    @Before
    fun setUp() {
        relay = LanCombatRelay(port = 0, ttlMs = 10_000, clock = now::get, log = { logs += it })
        relay.start()
        closeables += relay
        port = relay.boundPort
    }

    @After
    fun tearDown() {
        closeables.reversed().forEach { try { it.close() } catch (_: Exception) { } }
    }

    private inner class Client {
        private val socket = DatagramSocket(0, InetAddress.getLoopbackAddress()).also { closeables += it }
        fun send(text: String) = send(text.toByteArray(Charsets.ISO_8859_1))
        fun send(bytes: ByteArray) =
            socket.send(DatagramPacket(bytes, bytes.size, InetAddress.getLoopbackAddress(), port))

        /** Next datagram whose text starts with [prefix], skipping others; null on timeout. */
        fun waitFor(prefix: String, timeoutMs: Int = 2_000): String? {
            val deadline = System.nanoTime() + timeoutMs * 1_000_000L
            while (true) {
                val remaining = ((deadline - System.nanoTime()) / 1_000_000L).toInt()
                if (remaining <= 0) return null
                socket.soTimeout = remaining
                val packet = DatagramPacket(ByteArray(4096), 4096)
                try { socket.receive(packet) } catch (_: SocketTimeoutException) { return null }
                val text = String(packet.data, 0, packet.length, Charsets.ISO_8859_1)
                if (text.startsWith(prefix)) return text
            }
        }
    }

    private fun pair(room: String, first: String, second: String): Pair<Client, Client> {
        val a = Client()
        val b = Client()
        a.send("HELLO|$room|$first")
        assertTrue(a.waitFor("OK|") != null)
        b.send("HELLO|$room|$second")
        assertTrue(b.waitFor("OK|") != null)
        assertTrue(a.waitFor("PR|") != null)
        return a to b
    }

    @Test
    fun helloIsAcknowledgedWithTheRoster() {
        val a = Client()
        a.send("HELLO|arena_versus|alice")
        assertEquals("OK|alice|1|alice", a.waitFor("OK|"))
        val b = Client()
        b.send("HELLO|arena_versus|bob")
        assertEquals("OK|bob|2|alice,bob", b.waitFor("OK|"))
        // The existing peer is told; the newcomer is not sent a redundant PR.
        assertEquals("PR|2|alice,bob", a.waitFor("PR|"))
        assertNull(b.waitFor("PR|", 300))
    }

    @Test
    fun inputIsForwardedToTheOtherPeerOnly() {
        val (a, b) = pair("arena_versus", "alice", "bob")
        a.send("IN|arena_versus|alice|7|action=3")
        assertEquals("IN|alice|7|action=3", b.waitFor("IN|"))
        assertNull("sender must not hear its own input", a.waitFor("IN|", 300))
    }

    @Test
    fun stateAndEventsForwardAndRoomsAreIsolated() {
        val (a, b) = pair("arena_versus", "alice", "bob")
        val other = Client()
        other.send("HELLO|other_room|carol")
        other.waitFor("OK|")
        a.send("ST|arena_versus|alice|1|hp=90")
        a.send("EV|arena_versus|alice|2|start")
        assertEquals("ST|alice|1|hp=90", b.waitFor("ST|"))
        assertEquals("EV|alice|2|start", b.waitFor("EV|"))
        assertNull(other.waitFor("ST|", 300))
    }

    @Test
    fun duplicateNamesGetASuffixAndStayDistinctByEndpoint() {
        val a = Client()
        val b = Client()
        a.send("HELLO|arena_versus|phone")
        assertEquals("OK|phone|1|phone", a.waitFor("OK|"))
        b.send("HELLO|arena_versus|phone")
        assertEquals("OK|phone-2|2|phone,phone-2", b.waitFor("OK|"))
        assertEquals("PR|2|phone,phone-2", a.waitFor("PR|"))
        a.send("IN|arena_versus|phone|1|action=1")
        assertEquals("IN|phone|1|action=1", b.waitFor("IN|"))
        b.send("IN|arena_versus|phone|2|action=2")
        assertEquals("IN|phone-2|2|action=2", a.waitFor("IN|"))
    }

    @Test
    fun repeatedHelloFromTheSameEndpointKeepsItsName() {
        val a = Client()
        a.send("HELLO|arena_versus|alice")
        a.waitFor("OK|")
        a.send("HELLO|arena_versus|alice")
        assertEquals("OK|alice|1|alice", a.waitFor("OK|"))
    }

    @Test
    fun anUnknownSenderIsAdoptedInsteadOfDropped() {
        val a = Client()
        val b = Client()
        b.send("HELLO|arena_versus|bob")
        b.waitFor("OK|")
        a.send("IN|arena_versus|alice|3|action=8")
        // The roster push precedes the forwarded frame (waitFor discards what it skips).
        assertEquals("PR|2|bob,alice", b.waitFor("PR|"))
        assertEquals("IN|alice|3|action=8", b.waitFor("IN|"))
        a.send("HELLO|arena_versus|alice")
        assertEquals("OK|alice|2|bob,alice", a.waitFor("OK|"))
    }

    @Test
    fun aSilentPeerExpiresAndTheRoomIsToldAboutIt() {
        val (a, b) = pair("arena_versus", "alice", "bob")
        // Only alice stays alive past bob's TTL.
        now.addAndGet(6_000)
        a.send("IN|arena_versus|alice|1|x")
        b.waitFor("IN|")
        now.addAndGet(6_000)
        assertEquals("PR|1|alice", a.waitFor("PR|"))
        assertTrue(logs.any { it.contains("peer bob") && it.contains("expired") })
        a.send("IN|arena_versus|alice|2|x")
        assertNull("an expired peer no longer receives", b.waitFor("IN|", 300))
    }

    @Test
    fun byeLeavesImmediately() {
        val (a, b) = pair("arena_versus", "alice", "bob")
        b.send("BYE|arena_versus|bob")
        assertEquals("PR|1|alice", a.waitFor("PR|"))
    }

    @Test
    fun malformedPacketsAreRejectedNotFatal() {
        val a = Client()
        a.send("HELLO|arena_versus")
        assertEquals("ERR|HELLO needs room and peer", a.waitFor("ERR|"))
        a.send("HELLO||bob")
        assertEquals("ERR|empty room or peer", a.waitFor("ERR|"))
        a.send("NOPE|x|y")
        assertEquals("ERR|unknown command", a.waitFor("ERR|"))
        a.send("IN|arena_versus")
        assertEquals("ERR|forward needs room and peer", a.waitFor("ERR|"))
        a.send(ByteArray(0))
        assertEquals("ERR|bad length", a.waitFor("ERR|"))
        a.send("HELLO|arena_versus|alice")
        assertTrue("relay still works", a.waitFor("OK|") != null)
        assertEquals(5, relay.rejected)
    }

    @Test
    fun anOversizedPacketIsRefusedNotTruncatedIntoAcceptance() {
        val (a, b) = pair("arena_versus", "alice", "bob")
        a.send("IN|arena_versus|alice|1|" + "x".repeat(900))
        assertEquals("ERR|bad length", a.waitFor("ERR|"))
        assertNull(b.waitFor("IN|", 300))
        // A full-size legal packet still goes through.
        val header = "IN|arena_versus|alice|2|"
        a.send(header + "y".repeat(LanCombatRelay.MAX_PACKET - header.length))
        val got = b.waitFor("IN|")
        assertTrue(got != null && got.count { it == 'y' } == LanCombatRelay.MAX_PACKET - header.length)
        // One byte over the limit is rejected.
        a.send(header + "z".repeat(LanCombatRelay.MAX_PACKET - header.length + 1))
        assertEquals("ERR|bad length", a.waitFor("ERR|"))
    }

    @Test
    fun manyPacketsAreNotDropped() {
        val (a, b) = pair("arena_versus", "alice", "bob")
        repeat(200) { a.send("IN|arena_versus|alice|$it|p") }
        var seen = 0
        while (b.waitFor("IN|", 500) != null) seen++
        assertTrue("received $seen of 200", seen >= 190)
    }

    @Test
    fun theRosterNeverExceedsSixteenPeers() {
        val clients = List(17) { Client() }
        clients.forEachIndexed { i, c ->
            c.send("HELLO|arena_versus|p$i")
            now.addAndGet(1) // make the eviction order deterministic
            c.waitFor("OK|")
        }
        val last = clients.last()
        last.send("HELLO|arena_versus|probe-check")
        val ok = last.waitFor("OK|")!!
        assertNotEquals("", ok)
        assertTrue(ok.split("|")[2].toInt() <= LanCombatRelay.MAX_PEERS)
    }

    @Test
    fun aBindConflictIsReportedAsAnIoException() {
        val second = LanCombatRelay(port = port)
        try {
            second.start()
            throw AssertionError("binding an occupied UDP port must fail")
        } catch (_: java.io.IOException) {
        } finally {
            second.close()
        }
        assertEquals(-1, second.boundPort)
    }
}
