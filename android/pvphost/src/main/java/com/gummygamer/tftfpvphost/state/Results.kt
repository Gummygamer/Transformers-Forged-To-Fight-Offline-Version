package com.gummygamer.tftfpvphost.state

/** One peer's claimed outcome (`WON`, `LOST`, `DRAW` or `UNKNOWN`) for a match. */
data class MatchReport(val matchId: String, val peer: String, val outcome: String, val reportedMs: Long)

/** [status] is `pending`, `agreed` or `disputed`; [winner]/[loser] are empty for draws. */
data class Reconciliation(
    val matchId: String,
    val status: String,
    val winner: String,
    val loser: String,
    val reports: Int,
) {
    val settled: Boolean get() = status == AGREED || status == DISPUTED

    companion object {
        const val PENDING = "pending"
        const val AGREED = "agreed"
        const val DISPUTED = "disputed"
    }
}

/** `WIN|WON|VICTORY` -> `WON`, `LOSE|LOST|LOSS|DEFEAT` -> `LOST`, `DRAW|TIE` -> `DRAW`, else `UNKNOWN`. */
fun normalizeOutcome(value: String): String = when (value.uppercase()) {
    "WIN", "WON", "VICTORY" -> "WON"
    "LOSE", "LOST", "LOSS", "DEFEAT" -> "LOST"
    "DRAW", "TIE" -> "DRAW"
    else -> "UNKNOWN"
}

/**
 * Result-report store (`R|match_id|peer|outcome|reported_ms`) and the two-sided reconciliation,
 * reproducing `report_result_at` and `reconcile_match` (Server/fakeserver.lbl:634-787).
 * Reconciliation is a pure function of the stored rows, so both peers read the same answer.
 */
internal class Results(private val file: RecordFile) {
    fun load(): List<MatchReport> = file.readLines().mapNotNull(::parse)

    /** Stores the report (replacing that peer's earlier report for the match) and reconciles. */
    fun report(matchId: String, peer: String, outcome: String, nowMs: Long): Reconciliation {
        val key = Fields.idSafe(matchId)
        val reporter = Fields.idSafe(peer)
        val kept = load().filter { it.matchId != key || it.peer != reporter }.takeLast(MAX_REPORTS - 1)
        val added = MatchReport(key, reporter, Fields.relaySafe(normalizeOutcome(outcome)), nowMs)
        file.writeLines((kept + added).map(::render))
        return reconcile(key)
    }

    fun reconcile(matchId: String): Reconciliation {
        val key = Fields.idSafe(matchId)
        val chosen = reportsFor(key)
        if (chosen.size < 2) return Reconciliation(key, Reconciliation.PENDING, "", "", chosen.size)
        return twoReports(key, chosen[0], chosen[1])
    }

    fun matchIds(): List<String> = load().map { it.matchId }.distinct()

    fun clear() = file.clear()

    /** At most two reports, each peer's latest, most recent first (fakeserver.lbl:702-722). */
    private fun reportsFor(key: String): List<MatchReport> =
        load().filter { it.matchId == key }
            .groupBy { it.peer }
            .map { (_, rows) -> rows.sortedWith(RECENT_FIRST).first() }
            .sortedWith(RECENT_FIRST)
            .take(2)

    private fun twoReports(key: String, first: MatchReport, second: MatchReport): Reconciliation {
        val pair = first.outcome to second.outcome
        return when {
            pair == ("WON" to "LOST") -> settled(key, Reconciliation.AGREED, first.peer, second.peer)
            pair == ("LOST" to "WON") -> settled(key, Reconciliation.AGREED, second.peer, first.peer)
            pair == ("DRAW" to "DRAW") -> settled(key, Reconciliation.AGREED, "", "")
            else -> disputed(key, first, second)
        }
    }

    /** The earliest report decides; equal times fall to the lexicographically smaller peer. */
    private fun disputed(key: String, first: MatchReport, second: MatchReport): Reconciliation {
        val firstDecides = first.reportedMs < second.reportedMs ||
            (first.reportedMs == second.reportedMs && first.peer < second.peer)
        val decider = if (firstDecides) first else second
        val other = if (firstDecides) second else first
        return when (decider.outcome) {
            "WON" -> settled(key, Reconciliation.DISPUTED, decider.peer, other.peer)
            "LOST" -> settled(key, Reconciliation.DISPUTED, other.peer, decider.peer)
            else -> settled(key, Reconciliation.DISPUTED, "", "")
        }
    }

    private fun settled(key: String, status: String, winner: String, loser: String) =
        Reconciliation(key, status, winner, loser, SETTLED_REPORTS)

    private fun render(r: MatchReport): String = "R|${r.matchId}|${r.peer}|${r.outcome}|${r.reportedMs}"

    private fun parse(line: String): MatchReport? {
        val f = RecordFile.fields(line)
        val id = RecordFile.field(f, 1)
        val peer = RecordFile.field(f, 2)
        if (RecordFile.field(f, 0) != "R" || id.isEmpty() || peer.isEmpty()) return null
        val outcome = RecordFile.field(f, 3).ifEmpty { "UNKNOWN" }
        return MatchReport(id, peer, outcome, RecordFile.longOrZero(RecordFile.field(f, 4)))
    }

    private companion object {
        const val SETTLED_REPORTS = 2
        const val MAX_REPORTS = 2_000

        /** `report_is_more_recent`: later `reportedMs`, ties to the smaller peer. */
        val RECENT_FIRST: Comparator<MatchReport> =
            compareByDescending<MatchReport> { it.reportedMs }.thenBy { it.peer }
    }
}
