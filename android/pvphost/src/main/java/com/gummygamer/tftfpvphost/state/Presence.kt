package com.gummygamer.tftfpvphost.state

/** A peer's last heartbeat. */
data class Presence(val peer: String, val name: String, val seenMs: Long)

/**
 * Presence store (`P|peer|name|seen_ms`): one row per peer, rewritten whole, reproducing
 * `touch_presence_at` and `live_peers_at` (Server/fakeserver.lbl:377-444).
 */
internal class PresenceTable(private val file: RecordFile) {
    fun load(): List<Presence> = file.readLines().mapNotNull(::parse)

    /**
     * Stamps [peer] at [nowMs]. Name resolution order: [name] argument, previously stored name,
     * [sessionName], `"Commander"` (fakeserver.lbl:416-418).
     */
    fun touchAt(peer: String, name: String, sessionName: String, nowMs: Long): Presence {
        val key = Fields.idSafe(peer)
        val rows = load()
        val previousName = rows.firstOrNull { it.peer == key }?.name ?: ""
        val chosen = listOf(name, previousName, sessionName).firstOrNull { it.isNotEmpty() } ?: "Commander"
        val touched = Presence(key, Fields.nameSafe(chosen), nowMs)
        val others = rows.filter { it.peer != key }
        val bounded = if (others.size >= MAX_PEERS) others.sortedBy { it.seenMs }.takeLast(MAX_PEERS - 1) else others
        file.writeLines((bounded + touched).map(::render))
        return touched
    }

    /** Live means `0 <= age < ttl`: the boundary is exclusive (age `ttl-1` live, age `ttl` dead). */
    fun liveAt(nowMs: Long, ttlMs: Long): List<Presence> =
        load().filter { val age = nowMs - it.seenMs; age >= 0 && age < ttlMs }

    fun clear() = file.clear()

    private fun render(p: Presence): String = "P|${p.peer}|${p.name}|${p.seenMs}"

    private fun parse(line: String): Presence? {
        val f = RecordFile.fields(line)
        val peer = RecordFile.field(f, 1)
        if (RecordFile.field(f, 0) != "P" || peer.isEmpty()) return null
        return Presence(peer, RecordFile.field(f, 2), RecordFile.longOrZero(RecordFile.field(f, 3)))
    }

    private companion object {
        const val MAX_PEERS = 1_000
    }
}
