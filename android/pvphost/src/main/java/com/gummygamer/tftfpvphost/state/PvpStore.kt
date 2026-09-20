package com.gummygamer.tftfpvphost.state

import java.io.File
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.locks.ReentrantLock
import kotlin.concurrent.withLock

/** Result of posting a fight event to the relay. */
sealed interface FightPost {
    data class Accepted(val event: FightEvent) : FightPost

    /** The match does not exist or the caller is neither peer. */
    data object NotMember : FightPost

    data object TooLarge : FightPost
}

/** A live peer as the UI sees it. [peer] may be a session token; callers must not display it raw. */
data class PeerSnapshot(val name: String, val peer: String, val ageMs: Long, val inMatch: Boolean)

/** Read-only view of everything the matchmaking panel shows. */
data class StoreSnapshot(
    val presenceTtlMs: Long,
    val peers: List<PeerSnapshot>,
    val matches: List<Match>,
    val results: List<Reconciliation>,
)

/**
 * Thread-safe façade over the persistent PvP stores in [dir] (`.pvp-*` line files).
 *
 * Every public operation is one read-modify-write under a single lock, and nothing is cached
 * in memory, so pairing, roster replacement and reconciliation are atomic against concurrent
 * requests and a new instance over the same directory sees the previous state (service restart).
 * The lock is shared per directory, so two instances over one directory stay consistent.
 * Storage failures surface as [java.io.IOException].
 */
class PvpStore(
    dir: File,
    private val clock: Clock = SystemClock,
    houseTeam: List<String> = DEFAULT_HOUSE_TEAM,
    presenceTtlMs: Long = DEFAULT_PRESENCE_TTL_MS,
) {
    private val lock: ReentrantLock = LOCKS.computeIfAbsent(dir.absoluteFile.path) { ReentrantLock() }
    private val sessions = Sessions(RecordFile(File(dir, "sessions")))
    private val rosters = Rosters(RecordFile(File(dir, "rosters")), houseTeam)
    private val presence = PresenceTable(RecordFile(File(dir, "presence")))
    private val matches = Matches(RecordFile(File(dir, "matches")))
    private val fights = FightEvents(RecordFile(File(dir, "fights")))
    private val results = Results(RecordFile(File(dir, "results")))

    @Volatile
    var presenceTtlMs: Long = if (presenceTtlMs > 0L) presenceTtlMs else DEFAULT_PRESENCE_TTL_MS
        private set

    /** Overrides the presence TTL at runtime; a non-positive value is rejected and ignored. */
    fun setPresenceTtlMs(ms: Long): Boolean {
        if (ms <= 0L) return false
        presenceTtlMs = ms
        return true
    }

    // Sessions

    fun ensureSession(fingerprint: String, name: String): Session = locked { sessions.ensure(fingerprint, name) }

    fun sessionForToken(stoken: String): Session? = locked { sessions.forToken(stoken) }

    /** Position of the session that owns [stoken] in creation order, or -1 (`opponent_uid_for`). */
    fun sessionIndexFor(stoken: String): Int =
        if (stoken.isEmpty()) -1 else locked { sessions.load().indexOfFirst { it.stoken == stoken } }

    // Rosters

    /** Replaces [peer]'s saved squad; returns false (and changes nothing) for an empty squad. */
    fun storeRoster(peer: String, name: String, heroes: List<String>): Boolean =
        locked { rosters.store(peer, name, heroes) }

    fun rosterFor(peer: String): Roster? = locked { rosters.forPeer(peer) }

    fun opponentRoster(peer: String): Roster = locked { rosters.opponentFor(peer) }

    fun houseRoster(): Roster = rosters.house()

    // Presence

    fun touchPresenceAt(peer: String, name: String, nowMs: Long): Presence = locked { touch(peer, name, nowMs) }

    fun touchPresence(peer: String, name: String): Presence = touchPresenceAt(peer, name, clock.nowMs())

    fun livePeersAt(nowMs: Long): List<Presence> = locked { presence.liveAt(nowMs, presenceTtlMs) }

    fun livePeers(): List<Presence> = livePeersAt(clock.nowMs())

    // Matches

    fun matchForPeer(peer: String): Match? = locked { matches.forPeer(peer) }

    fun matchById(matchId: String): Match? = locked { matches.byId(matchId) }

    fun activeMatches(): List<Match> = locked { matches.load() }

    /** Ends the match; its fight events are left alone (use [concludeMatch] to purge them). */
    fun endMatch(matchId: String) = locked { matches.end(matchId) }

    /** Ends the match and purges its fight events; result rows survive. */
    fun concludeMatch(matchId: String) = locked { conclude(matchId) }

    fun pairPeersAt(peer: String, arena: String, nowMs: Long): Match? = locked {
        touch(peer, "", nowMs)
        matches.pair(peer, arena, nowMs, presence.liveAt(nowMs, presenceTtlMs))
    }

    fun pairPeers(peer: String, arena: String): Match? = pairPeersAt(peer, arena, clock.nowMs())

    // Fight events

    fun isMatchMember(matchId: String, peer: String): Boolean =
        locked { matches.byId(matchId)?.hasMember(peer) == true }

    fun postFightEventAt(matchId: String, peer: String, kind: String, payload: String, nowMs: Long): FightPost =
        locked {
            when {
                matches.byId(matchId)?.hasMember(peer) != true -> FightPost.NotMember
                payload.length > MAX_PAYLOAD_CHARS -> FightPost.TooLarge
                else -> FightPost.Accepted(fights.append(matchId, peer, kind, payload, nowMs))
            }
        }

    fun postFightEvent(matchId: String, peer: String, kind: String, payload: String): FightPost =
        postFightEventAt(matchId, peer, kind, payload, clock.nowMs())

    /** Events after [afterSeq] in ascending order, or null when [peer] is not in the match. */
    fun pollFightEvents(matchId: String, peer: String, afterSeq: Long): List<FightEvent>? = locked {
        if (matches.byId(matchId)?.hasMember(peer) == true) fights.since(matchId, afterSeq) else null
    }

    fun nextFightSeq(matchId: String): Long = locked { fights.nextSeq(matchId) }

    fun purgeFightEvents(matchId: String) = locked { fights.purge(matchId) }

    // Results

    /** Records the report; an agreed or disputed outcome ends the match and purges its events. */
    fun reportResultAt(matchId: String, peer: String, outcome: String, nowMs: Long): Reconciliation = locked {
        val reconciliation = results.report(matchId, peer, outcome, nowMs)
        if (reconciliation.settled) conclude(matchId)
        reconciliation
    }

    fun reportResult(matchId: String, peer: String, outcome: String): Reconciliation =
        reportResultAt(matchId, peer, outcome, clock.nowMs())

    fun reconcileMatch(matchId: String): Reconciliation = locked { results.reconcile(matchId) }

    fun reports(): List<MatchReport> = locked { results.load() }

    // Whole-store operations

    fun snapshotAt(nowMs: Long): StoreSnapshot = locked {
        val active = matches.load()
        val busy = active.flatMap { listOf(it.peerA, it.peerB) }.toSet()
        val peers = presence.liveAt(nowMs, presenceTtlMs).map {
            PeerSnapshot(it.name, it.peer, nowMs - it.seenMs, it.peer in busy)
        }
        val recent = results.matchIds().takeLast(SNAPSHOT_RESULTS).reversed().map(results::reconcile)
        StoreSnapshot(presenceTtlMs, peers, active, recent)
    }

    fun snapshot(): StoreSnapshot = snapshotAt(clock.nowMs())

    /** Clears every store (sessions included); the server keeps running. */
    fun resetAll() = locked {
        sessions.clear()
        rosters.clear()
        presence.clear()
        matches.clear()
        fights.clear()
        results.clear()
    }

    private fun touch(peer: String, name: String, nowMs: Long): Presence {
        val sessionName = sessions.forToken(Fields.idSafe(peer))?.name ?: ""
        return presence.touchAt(peer, name, sessionName, nowMs)
    }

    private fun conclude(matchId: String) {
        matches.end(matchId)
        fights.purge(matchId)
    }

    private inline fun <T> locked(block: () -> T): T = lock.withLock(block)

    companion object {
        const val DEFAULT_PRESENCE_TTL_MS: Long = 15_000L
        const val MAX_PAYLOAD_CHARS: Int = 32 * 1024
        private const val SNAPSHOT_RESULTS = 20

        /** `gamedata.default_team()` (pvphost-design §2 `api/GameData.kt`). */
        val DEFAULT_HOUSE_TEAM: List<String> =
            listOf("optimusprime_cin_tf", "optimusprimal_bw_mp32", "megatron_gs_leader2015")

        private val LOCKS = ConcurrentHashMap<String, ReentrantLock>()
    }
}
