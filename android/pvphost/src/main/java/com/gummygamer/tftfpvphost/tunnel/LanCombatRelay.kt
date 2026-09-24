package com.gummygamer.tftfpvphost.tunnel

import java.io.IOException
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetSocketAddress
import java.net.SocketTimeoutException
import java.util.concurrent.atomic.AtomicBoolean

/**
 * LAN UDP relay for the Arena netcode: a Kotlin port of `tools/netrelay/netrelay.c`, so a
 * host phone can carry the live fight without any laptop service.
 *
 * Each device runs the fight locally and sends its own input/state; this class only tracks
 * who is present and forwards to the other live peers in the room. One datagram is one
 * `|`-separated packet of at most [MAX_PACKET] bytes:
 *
 *  client -> relay: `HELLO|room|peer`, `IN|room|peer|seq|payload` (also `ST`, `EV`), `BYE|room|peer`
 *  relay -> client: `OK|yourpeer|count|peers`, `PR|count|peers`, `IN|frompeer|seq|payload`
 *                   (also `ST`, `EV`), `ERR|reason`
 *
 * Peer identity is the UDP endpoint; the configured name is only a label, and a duplicate
 * label gets a `-2`, `-3`... suffix. Input from an unknown endpoint is adopted rather than
 * dropped. A peer silent for [ttlMs] is dropped and the room gets a fresh `PR`.
 */
class LanCombatRelay(
    private val port: Int = TunnelConfig.DEFAULT_COMBAT_PORT,
    private val ttlMs: Long = DEFAULT_TTL_MS,
    private val clock: () -> Long = System::currentTimeMillis,
    private val log: (String) -> Unit = {},
) : AutoCloseable {
    private class Peer(
        val room: String,
        val name: String,
        var addr: InetSocketAddress,
        var lastMs: Long,
    ) {
        var packets = 0L
    }

    private val stopped = AtomicBoolean(false)
    private val peers = ArrayList<Peer>()
    private var socket: DatagramSocket? = null
    private var thread: Thread? = null

    var forwarded = 0L
        private set
    var dropped = 0L
        private set
    var rejected = 0L
        private set

    /** The bound UDP port (differs from the requested one only when it was 0). */
    val boundPort: Int get() = socket?.localPort ?: -1

    /** Binds 0.0.0.0:[port] and starts the receive thread. Throws [IOException] if the port is taken. */
    fun start() {
        check(socket == null) { "relay already started" }
        val bound = DatagramSocket(null)
        try {
            // No SO_REUSEADDR: on Linux it lets two UDP sockets share a port, which would hide a
            // conflict with another app and split the packets between them.
            bound.bind(InetSocketAddress(port))
            bound.soTimeout = POLL_MS
        } catch (e: IOException) {
            bound.close()
            throw e
        }
        socket = bound
        thread = Thread({ receiveLoop(bound) }, "tftf-lan-relay").apply { isDaemon = true; start() }
        log("Arena relay listening on udp/0.0.0.0:${bound.localPort}")
    }

    override fun close() {
        if (!stopped.compareAndSet(false, true)) return
        socket?.close()
        thread?.interrupt()
        if (thread != Thread.currentThread()) {
            try { thread?.join(2_000) } catch (_: InterruptedException) { Thread.currentThread().interrupt() }
        }
    }

    private fun receiveLoop(bound: DatagramSocket) {
        // One spare-capacity buffer, larger than MAX_PACKET on purpose: receive() truncates to
        // the buffer, so a buffer at the limit would shrink an oversized datagram back under it.
        val buffer = ByteArray(MAX_PACKET * 8)
        val datagram = DatagramPacket(buffer, buffer.size)
        while (!stopped.get()) {
            try {
                datagram.length = buffer.size
                bound.receive(datagram)
                handle(buffer.copyOf(datagram.length), InetSocketAddress(datagram.address, datagram.port))
            } catch (_: SocketTimeoutException) {
                // idle tick: fall through to expiry
            } catch (e: IOException) {
                if (!stopped.get()) log("Arena relay receive failed: ${e.message}")
                if (bound.isClosed) return
            }
            expireStale()
        }
    }

    /** Handles one inbound datagram. Called only from the receive thread. */
    internal fun handle(data: ByteArray, from: InetSocketAddress) {
        var length = data.size
        if (length == 0 || length > MAX_PACKET) { reject(from, "bad length"); return }
        // Strip a trailing CR/LF so a telnet-style probe still parses.
        while (length > 0 && (data[length - 1] == '\n'.code.toByte() || data[length - 1] == '\r'.code.toByte())) length--
        val fields = String(data, 0, length, Charsets.ISO_8859_1).split('|')
        when (val command = fields[0]) {
            "HELLO" -> hello(fields, from)
            "BYE" -> bye(fields, from)
            "IN", "ST", "EV" -> forwardable(command, fields, from)
            else -> reject(from, "unknown command")
        }
    }

    private fun hello(fields: List<String>, from: InetSocketAddress) {
        if (fields.size < 3) { reject(from, "HELLO needs room and peer"); return }
        val room = fields[1].take(MAX_ROOM)
        val requested = fields[2].take(MAX_PEER)
        if (room.isEmpty() || requested.isEmpty()) { reject(from, "empty room or peer"); return }
        val sender = findAt(room, from) ?: allocate(room, uniqueName(room, requested), from)
        sender.addr = from
        sender.lastMs = clock()
        sender.packets++
        val roster = roster(room)
        send(from, "OK|${sender.name}|${roster.size}|${roster.joinToString(",") { it.name }}")
        log("HELLO room=$room peer=$requested as=${sender.name} from ${from.address.hostAddress} -> ${roster.size} live peer(s)")
        sendRoster(room, skip = sender)
    }

    private fun bye(fields: List<String>, from: InetSocketAddress) {
        if (fields.size < 3) { reject(from, "BYE needs room and peer"); return }
        val room = fields[1].take(MAX_ROOM)
        val sender = findAt(room, from) ?: find(room, fields[2].take(MAX_PEER)) ?: return
        log("BYE room=$room peer=${sender.name} after ${sender.packets} packets")
        peers.remove(sender)
        sendRoster(room, skip = null)
    }

    private fun forwardable(command: String, fields: List<String>, from: InetSocketAddress) {
        if (fields.size < 3) { reject(from, "forward needs room and peer"); return }
        val room = fields[1].take(MAX_ROOM)
        val label = fields[2].take(MAX_PEER)
        var sender = findAt(room, from)
        if (sender == null) {
            // A client may send input before its HELLO lands; adopt it rather than drop the
            // frame, because dropping input is a visible stall in the fight.
            sender = allocate(room, uniqueName(room, label), from)
            sender.lastMs = clock()
            sendRoster(room, skip = sender)
        }
        sender.addr = from
        sender.lastMs = clock()
        sender.packets++
        val seq = fields.getOrElse(3) { "" }.take(MAX_SEQ)
        val payload = fields.getOrElse(4) { "" }
        forward("$command|${sender.name}|$seq|$payload", room, sender)
    }

    private fun forward(line: String, room: String, sender: Peer) {
        val cutoff = clock() - ttlMs
        var recipients = 0
        for (peer in peers.toList()) {
            if (peer.room != room || peer.lastMs < cutoff || peer === sender) continue
            if (send(peer.addr, line)) recipients++
        }
        if (recipients > 0) forwarded++ else dropped++
    }

    private fun expireStale() {
        val cutoff = clock() - ttlMs
        for (peer in peers.filter { it.lastMs < cutoff }) {
            peers.remove(peer)
            log("peer ${peer.name} in room ${peer.room} expired after ${peer.packets} packets")
            sendRoster(peer.room, skip = null)
        }
    }

    private fun find(room: String, name: String) = peers.firstOrNull { it.room == room && it.name == name }

    private fun findAt(room: String, addr: InetSocketAddress) =
        peers.firstOrNull { it.room == room && it.addr == addr }

    /** Keeps the first client's label but makes a duplicate unique; the endpoint is the identity. */
    private fun uniqueName(room: String, requested: String): String {
        if (find(room, requested) == null) return requested
        for (suffix in 2 until 100_000) {
            val tail = "-$suffix"
            val candidate = requested.take(MAX_PEER - tail.length) + tail
            if (find(room, candidate) == null) return candidate
        }
        return requested
    }

    private fun allocate(room: String, name: String, addr: InetSocketAddress): Peer {
        if (peers.size >= MAX_PEERS) peers.remove(peers.minByOrNull { it.lastMs })
        return Peer(room, name, addr, 0L).also { peers += it }
    }

    private fun roster(room: String): List<Peer> {
        val cutoff = clock() - ttlMs
        return peers.filter { it.room == room && it.lastMs >= cutoff }
    }

    /** The newcomer is passed as [skip] on HELLO because its OK reply already carries the roster. */
    private fun sendRoster(room: String, skip: Peer?) {
        val live = roster(room)
        val line = "PR|${live.size}|${live.joinToString(",") { it.name }}"
        live.filter { it !== skip }.forEach { send(it.addr, line) }
    }

    private fun reject(to: InetSocketAddress, reason: String) {
        rejected++
        send(to, "ERR|$reason")
    }

    private fun send(to: InetSocketAddress, line: String): Boolean {
        val out = socket ?: return false
        val bytes = line.toByteArray(Charsets.ISO_8859_1)
        return try {
            out.send(DatagramPacket(bytes, bytes.size, to))
            true
        } catch (_: IOException) {
            false
        }
    }

    companion object {
        const val MAX_PACKET = TunnelConfig.MAX_COMBAT_PACKET
        const val MAX_PEERS = 16
        const val MAX_ROOM = 31
        const val MAX_PEER = 47
        const val MAX_SEQ = 23
        const val DEFAULT_TTL_MS = 10_000L
        private const val POLL_MS = 200
    }
}
