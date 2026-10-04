package com.gummygamer.tftfpvphost.state

/** A pairing of two peers that both keep until the match ends. */
data class Match(
    val matchId: String,
    val arena: String,
    val peerA: String,
    val peerB: String,
    val createdMs: Long,
) {
    /** The other side of the match, or `""` when [peer] is not a member. */
    fun opponentOf(peer: String): String {
        val key = Fields.idSafe(peer)
        return when (key) {
            peerA -> peerB
            peerB -> peerA
            else -> ""
        }
    }

    fun hasMember(peer: String): Boolean = opponentOf(peer).isNotEmpty()
}

/**
 * Matches store (`M|match_id|arena|peer_a|peer_b|created_ms`), reproducing `load_matches`,
 * `match_for_peer`, `end_match` and `pair_peers_at` (Server/fakeserver.lbl:446-537).
 */
internal class Matches(private val file: RecordFile) {
    fun load(): List<Match> = file.readLines().mapNotNull(::parse)

    fun forPeer(peer: String): Match? {
        val key = Fields.idSafe(peer)
        return load().firstOrNull { it.peerA == key || it.peerB == key }
    }

    fun byId(matchId: String): Match? {
        val key = Fields.idSafe(matchId)
        return if (key.isEmpty()) null else load().firstOrNull { it.matchId == key }
    }

    fun end(matchId: String) {
        val key = Fields.idSafe(matchId)
        val all = load()
        val remaining = all.filter { it.matchId != key }
        if (remaining.size != all.size) file.writeLines(remaining.map(::render))
    }

    /**
     * Pairs [caller] with the freshest live peer that has no match. Returns the caller's
     * existing match unchanged when there is one (idempotent), or null when nobody is
     * available. [live] is the live-peer list at [nowMs], in store order: on equal `seenMs`
     * the earlier row wins (fakeserver.lbl:512-517).
     */
    fun pair(caller: String, arena: String, nowMs: Long, live: List<Presence>): Match? {
        val key = Fields.idSafe(caller)
        val matches = load()
        matches.firstOrNull { it.peerA == key || it.peerB == key }?.let { return it }
        val busy = matches.flatMap { listOf(it.peerA, it.peerB) }.toSet()
        var candidate: Presence? = null
        for (p in live) {
            val best = candidate
            if (p.peer != key && p.peer !in busy && (best == null || p.seenMs > best.seenMs)) candidate = p
        }
        if (candidate == null || matches.size >= MAX_MATCHES) return null
        val safeArena = Fields.idSafe(arena)
        val created = Match(uniqueId(safeArena, nowMs, matches), safeArena, key, candidate.peer, nowMs)
        file.writeLines((matches + created).map(::render))
        return created
    }

    fun clear() = file.clear()

    /** `<arena>-<nowMs>`, with `-<matchCount>` appended (and bumped) on collision. */
    private fun uniqueId(arena: String, nowMs: Long, matches: List<Match>): String {
        val taken = matches.map { it.matchId }.toSet()
        val base = Fields.idSafe("$arena-$nowMs")
        if (base !in taken) return base
        var counter = matches.size
        while (Fields.idSafe("$base-$counter") in taken) counter++
        return Fields.idSafe("$base-$counter")
    }

    private fun render(m: Match): String = "M|${m.matchId}|${m.arena}|${m.peerA}|${m.peerB}|${m.createdMs}"

    private fun parse(line: String): Match? {
        val f = RecordFile.fields(line)
        val id = RecordFile.field(f, 1)
        val a = RecordFile.field(f, 3)
        val b = RecordFile.field(f, 4)
        if (RecordFile.field(f, 0) != "M" || id.isEmpty() || a.isEmpty() || b.isEmpty()) return null
        return Match(id, RecordFile.field(f, 2), a, b, RecordFile.longOrZero(RecordFile.field(f, 5)))
    }

    private companion object {
        const val MAX_MATCHES = 1_000
    }
}
