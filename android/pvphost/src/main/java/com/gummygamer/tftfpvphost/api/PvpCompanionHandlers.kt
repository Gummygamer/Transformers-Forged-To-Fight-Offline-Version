package com.gummygamer.tftfpvphost.api

import com.gummygamer.tftfpvphost.json.JsonArr
import com.gummygamer.tftfpvphost.json.JsonBool
import com.gummygamer.tftfpvphost.json.JsonInt
import com.gummygamer.tftfpvphost.json.JsonObj
import com.gummygamer.tftfpvphost.json.JsonParse
import com.gummygamer.tftfpvphost.json.jsonInt
import com.gummygamer.tftfpvphost.json.jsonObj
import com.gummygamer.tftfpvphost.json.jsonText
import com.gummygamer.tftfpvphost.json.with
import com.gummygamer.tftfpvphost.state.Clock
import com.gummygamer.tftfpvphost.state.FightPost
import com.gummygamer.tftfpvphost.state.Fields
import com.gummygamer.tftfpvphost.state.PvpStore
import com.gummygamer.tftfpvphost.state.Reconciliation
import com.gummygamer.tftfpvphost.state.normalizeOutcome

/**
 * The companion endpoints a retail client never calls (Server/fakeserver.lbl:1205-1332): presence
 * heartbeat, lobby, fight-event relay and explicit result reporting. All use the spaced envelope.
 */
class PvpCompanionHandlers(private val store: PvpStore, private val clock: Clock) {
    /** `/pvp/heartbeat`: touch presence with the body `name`, then the lobby without the caller. */
    fun heartbeat(call: Call): ByteArray {
        val presence = store.touchPresence(call.peer, JsonParse.textOr(call.body, "name", ""))
        return Reply.spaced(lobby(presence.peer, includeSelf = false))
    }

    /** `/pvp/lobby`: live peers including the caller. */
    fun lobby(call: Call): ByteArray = Reply.spaced(lobby(call.peer, includeSelf = true))

    /** `pvp_lobby_result` (:1205-1217), from one clock reading so `ageMs` and expiry agree. */
    private fun lobby(peer: String, includeSelf: Boolean): JsonObj {
        val now = clock.nowMs()
        val active = store.activeMatches()
        fun matchOf(key: String): String = active.firstOrNull { it.hasMember(key) }?.matchId.orEmpty()
        val rows = store.livePeersAt(now).filter { includeSelf || it.peer != peer }.map {
            jsonObj(
                "peer" to jsonText(it.peer),
                "name" to jsonText(it.name),
                "ageMs" to JsonInt(now - it.seenMs),
                "inMatch" to jsonText(matchOf(it.peer)),
            )
        }
        return jsonObj(
            "peer" to jsonText(peer),
            "now" to JsonInt(now),
            "ttl" to JsonInt(store.presenceTtlMs),
            "peers" to JsonArr(rows),
            "match" to jsonText(matchOf(peer)),
        )
    }

    /** `/pvp/leave-match` (:1232-1239): ends the caller's match; fight events are left alone. */
    fun leaveMatch(call: Call): ByteArray {
        val matchId = store.matchForPeer(call.peer)?.matchId.orEmpty()
        if (matchId.isNotEmpty()) store.endMatch(matchId)
        return Reply.spaced(jsonObj("peer" to jsonText(call.peer), "left" to JsonBool(matchId.isNotEmpty())))
    }

    /** `relay_match_id` (:1241-1252): body `matchID`, else query `matchID`, else the caller's match. */
    private fun relayMatchId(call: Call): String {
        val fromBody = JsonParse.textOr(call.body, "matchID", "")
        if (fromBody.isNotEmpty()) return Fields.idSafe(fromBody)
        val fromQuery = call.query("matchID")
        if (fromQuery.isNotEmpty()) return Fields.idSafe(fromQuery)
        return store.matchForPeer(call.peer)?.matchId.orEmpty()
    }

    /** `/pvp/fight-post` (:1269-1283): only members of the resolved match may post. */
    fun fightPost(call: Call): ByteArray {
        val matchId = relayMatchId(call)
        val kind = JsonParse.textOr(call.body, "kind", "input")
        val payload = JsonParse.textOr(call.body, "payload", "")
        val post = if (matchId.isEmpty()) null else store.postFightEvent(matchId, call.peer, kind, payload)
        val seq = (post as? FightPost.Accepted)?.event?.seq ?: 0L
        return Reply.spaced(jsonObj(
            "peer" to jsonText(call.peer),
            "matchID" to jsonText(matchId),
            "seq" to JsonInt(seq),
            "accepted" to JsonBool(post is FightPost.Accepted),
            "error" to jsonText(postError(matchId, post)),
        ))
    }

    private fun postError(matchId: String, post: FightPost?): String = when {
        matchId.isEmpty() -> "no active match"
        post is FightPost.NotMember -> "peer is not in match"
        post is FightPost.TooLarge -> "payload too large"
        else -> ""
    }

    /**
     * `/pvp/fight-poll` (:1293-1308). Deviation from fakeserver, which never gates polling: a
     * peer that is not in the match gets no events, so a guessed match id leaks nothing.
     */
    fun fightPoll(call: Call): ByteArray {
        val matchId = relayMatchId(call)
        val since = requestedSince(call)
        val events = store.pollFightEvents(matchId, call.peer, since).orEmpty()
        val opponent = store.matchById(matchId)?.opponentOf(call.peer).orEmpty()
        val rows = events.map {
            jsonObj(
                "seq" to JsonInt(it.seq),
                "peer" to jsonText(it.peer),
                "kind" to jsonText(it.kind),
                "payload" to jsonText(it.payload),
                "sentMs" to JsonInt(it.sentMs),
                "mine" to JsonBool(it.peer == call.peer),
            )
        }
        return Reply.spaced(jsonObj(
            "peer" to jsonText(call.peer),
            "matchID" to jsonText(matchId),
            "since" to JsonInt(since),
            "nextSeq" to JsonInt(maxOf(since, events.maxOfOrNull { it.seq } ?: since)),
            "opponent" to jsonText(opponent),
            "events" to JsonArr(rows),
        ))
    }

    /** `requested_since` (:1285-1291): body `since` (int or numeric text), else query, floor 0. */
    private fun requestedSince(call: Call): Long {
        val fromBody = JsonParse.valueText(call.body.get("since"), "")
        val raw = fromBody.ifEmpty { call.query("since") }
        return maxOf(0L, raw.trim().toLongOrNull() ?: 0L)
    }

    /** `/pvp/report-result` (:1315-1325): records the caller's claim and returns the reconciliation. */
    fun reportResult(call: Call): ByteArray {
        val matchId = relayMatchId(call)
        val posted = JsonParse.textOr(call.body, "result", "")
        val outcome = posted.ifEmpty { JsonParse.textOr(call.body, "outcome", "") }
        val reconciliation =
            if (mayReport(matchId, call.peer)) store.reportResult(matchId, call.peer, outcome)
            else store.reconcileMatch(matchId)
        return Reply.spaced(reconciliationJson(call.peer, reconciliation).with("outcome", jsonText(normalizeOutcome(outcome))))
    }

    /**
     * Deviation from fakeserver, which records any report for any match id. A report is recorded
     * only for a live match the caller belongs to, or as a repeat of a report the caller already
     * filed for a match that has since concluded. That keeps a third party from rewriting a
     * settled result.
     */
    private fun mayReport(matchId: String, peer: String): Boolean {
        if (matchId.isEmpty()) return false
        val match = store.matchById(matchId)
        if (match != null) return match.hasMember(peer)
        return store.reports().any { it.matchId == matchId && it.peer == peer }
    }

    /** `/pvp/match-result` (:1327-1332): read-only reconciliation. */
    fun matchResult(call: Call): ByteArray =
        Reply.spaced(reconciliationJson(call.peer, store.reconcileMatch(relayMatchId(call))))

    /** `reconciliation_json` (:1310-1313). */
    private fun reconciliationJson(peer: String, r: Reconciliation): JsonObj = jsonObj(
        "peer" to jsonText(peer),
        "matchID" to jsonText(r.matchId),
        "status" to jsonText(r.status),
        "winner" to jsonText(r.winner),
        "loser" to jsonText(r.loser),
        "reports" to jsonInt(r.reports),
    )
}
