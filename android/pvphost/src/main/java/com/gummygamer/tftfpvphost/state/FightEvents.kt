package com.gummygamer.tftfpvphost.state

/** One relayed fight event; [seq] is 1-based and per match. */
data class FightEvent(
    val matchId: String,
    val peer: String,
    val seq: Long,
    val kind: String,
    val payload: String,
    val sentMs: Long,
)

/**
 * Fight-event store (`F|match_id|peer|seq|kind|payload|sent_ms`), reproducing `next_fight_seq`,
 * `append_fight_event_at`, `fight_events_since` and `purge_fight_events`
 * (Server/fakeserver.lbl:539-632).
 */
internal class FightEvents(private val file: RecordFile) {
    fun load(): List<FightEvent> = file.readLines().mapNotNull(::parse)

    /** Highest stored sequence for the match plus one, so numbering starts at 1. */
    fun nextSeq(matchId: String): Long = nextSeq(load(), Fields.idSafe(matchId))

    fun append(matchId: String, peer: String, kind: String, payload: String, nowMs: Long): FightEvent {
        val key = Fields.idSafe(matchId)
        val rows = load()
        val event = FightEvent(key, Fields.idSafe(peer), nextSeq(rows, key), Fields.relaySafe(kind), Fields.relaySafe(payload), nowMs)
        val sameMatch = rows.count { it.matchId == key }
        val kept = if (sameMatch >= MAX_PER_MATCH) dropOldest(rows, key) else rows
        file.writeLines((kept + event).map(::render))
        return event
    }

    /** Events with `seq > afterSeq`, ascending by sequence regardless of file order. */
    fun since(matchId: String, afterSeq: Long): List<FightEvent> {
        val key = Fields.idSafe(matchId)
        return load().filter { it.matchId == key && it.seq > afterSeq }.sortedBy { it.seq }
    }

    fun purge(matchId: String) {
        val key = Fields.idSafe(matchId)
        val rows = load()
        val remaining = rows.filter { it.matchId != key }
        if (remaining.size != rows.size) file.writeLines(remaining.map(::render))
    }

    fun clear() = file.clear()

    private fun nextSeq(rows: List<FightEvent>, key: String): Long =
        (rows.filter { it.matchId == key }.maxOfOrNull { it.seq } ?: 0L) + 1L

    private fun dropOldest(rows: List<FightEvent>, key: String): List<FightEvent> {
        val oldest = rows.filter { it.matchId == key }.minByOrNull { it.seq } ?: return rows
        return rows - oldest
    }

    private fun render(e: FightEvent): String =
        "F|${e.matchId}|${e.peer}|${e.seq}|${e.kind}|${e.payload}|${e.sentMs}"

    private fun parse(line: String): FightEvent? {
        val f = RecordFile.fields(line)
        val id = RecordFile.field(f, 1)
        val peer = RecordFile.field(f, 2)
        if (RecordFile.field(f, 0) != "F" || id.isEmpty() || peer.isEmpty()) return null
        return FightEvent(
            id, peer, RecordFile.longOrZero(RecordFile.field(f, 3)), RecordFile.field(f, 4),
            RecordFile.field(f, 5), RecordFile.longOrZero(RecordFile.field(f, 6)),
        )
    }

    private companion object {
        const val MAX_PER_MATCH = 500
    }
}
