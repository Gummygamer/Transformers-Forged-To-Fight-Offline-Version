package com.gummygamer.tftfpvphost.api

import com.gummygamer.tftfpvphost.server.HostRuntime
import com.gummygamer.tftfpvphost.state.PvpStore
import com.gummygamer.tftfpvphost.state.Reconciliation
import java.io.IOException

/** A live peer for the matchmaking panel. [key] is masked; the raw peer key is never exposed. */
data class PeerView(val name: String, val key: String, val ageMs: Long, val inMatch: Boolean)

/** An active match; the peers are display names or masked keys. */
data class MatchInfo(val matchId: String, val arena: String, val peerA: String, val peerB: String, val ageMs: Long)

/** A reconciled (or pending) result; winner and loser are display names or masked keys. */
data class ResultView(val matchId: String, val status: String, val winner: String, val loser: String, val reports: Int)

/** Everything the matchmaking panel shows, safe to render as-is. */
data class StatusView(
    val presenceTtlMs: Long,
    val peers: List<PeerView>,
    val matches: List<MatchInfo>,
    val results: List<ResultView>,
)

/**
 * Read-only host status for the UI, plus the two controls it needs (presence TTL, reset).
 *
 * Session tokens double as peer keys for retail clients, so every peer key is masked to its
 * first six and last three characters and a display name that equals the key is masked too
 * (pvphost-design section 8). Nothing here exposes a file path.
 */
object HostStatus {
    @Volatile
    private var store: PvpStore? = null

    /** Called when the server starts serving with [store]. */
    fun attach(store: PvpStore) {
        this.store = store
    }

    /** The current view, or null when the host is not running or its state is unreadable. */
    fun view(): StatusView? {
        val active = store?.takeIf { HostRuntime.running.value } ?: return null
        return try {
            render(active)
        } catch (e: IOException) {
            null
        }
    }

    /** Applies a new presence TTL to the running host; false for a non-positive value or no host. */
    fun setPresenceTtlMs(ms: Long): Boolean = store?.takeIf { HostRuntime.running.value }?.setPresenceTtlMs(ms) ?: false

    /** Clears every store while the server keeps serving; false when the host is not running. */
    fun resetState(): Boolean {
        val active = store?.takeIf { HostRuntime.running.value } ?: return false
        return try {
            active.resetAll()
            true
        } catch (e: IOException) {
            false
        }
    }

    private fun render(active: PvpStore): StatusView {
        val now = System.currentTimeMillis()
        val snapshot = active.snapshotAt(now)
        val names = snapshot.peers.associate { it.peer to displayName(it.name, it.peer) }
        fun who(peer: String): String = names[peer] ?: mask(peer)
        return StatusView(
            presenceTtlMs = snapshot.presenceTtlMs,
            peers = snapshot.peers.map { PeerView(displayName(it.name, it.peer), mask(it.peer), it.ageMs, it.inMatch) },
            matches = snapshot.matches.map { MatchInfo(it.matchId, it.arena, who(it.peerA), who(it.peerB), now - it.createdMs) },
            results = snapshot.results.map { result(it, ::who) },
        )
    }

    private fun result(r: Reconciliation, who: (String) -> String): ResultView =
        ResultView(r.matchId, r.status, r.winner.takeIf { it.isNotEmpty() }?.let(who).orEmpty(),
            r.loser.takeIf { it.isNotEmpty() }?.let(who).orEmpty(), r.reports)

    private fun displayName(name: String, peer: String): String =
        if (name == peer || name.startsWith("/") || name.matches(Regex("^[A-Za-z]:[\\\\/].*"))) mask(peer) else name

    /** `sessab…f12`: first six and last three characters; short keys are already not secrets. */
    internal fun mask(key: String): String =
        if (key.length <= MASK_KEEP_HEAD + MASK_KEEP_TAIL) key else key.take(MASK_KEEP_HEAD) + "…" + key.takeLast(MASK_KEEP_TAIL)

    private const val MASK_KEEP_HEAD = 6
    private const val MASK_KEEP_TAIL = 3
}
