package com.gummygamer.tftfpvphost.api

import com.gummygamer.tftfpvphost.json.JsonArr
import com.gummygamer.tftfpvphost.json.JsonBool
import com.gummygamer.tftfpvphost.json.JsonObj
import com.gummygamer.tftfpvphost.json.JsonParse
import com.gummygamer.tftfpvphost.json.JsonInt
import com.gummygamer.tftfpvphost.json.jsonInt
import com.gummygamer.tftfpvphost.json.jsonObj
import com.gummygamer.tftfpvphost.json.jsonText
import com.gummygamer.tftfpvphost.json.with
import com.gummygamer.tftfpvphost.server.HostConfig
import com.gummygamer.tftfpvphost.state.Fields
import com.gummygamer.tftfpvphost.state.PvpStore

/**
 * The retail Arena endpoints (Server/fakeserver.lbl:1334-1421, 1514-1528, 1593-1615). Every
 * response uses the spaced envelope. A null return means "not handled": the canned body answers.
 */
class PvpArenaHandlers(
    private val store: PvpStore,
    private val game: GameData,
    private val config: HostConfig,
    private val matches: PvpMatchBody,
) {
    /** `/pvp/get-login-data`: touch presence, then fall through to the canned response. */
    fun loginData(call: Call): ByteArray? {
        call.touch()
        return null
    }

    /** `/pvp/get-user-data`, `/pvp/get-active-pvp-data` (`pvp_arena_state_response`). */
    fun arenaState(call: Call): ByteArray {
        val caller = call.touch()
        val arena = call.arenaId(config.defaultArena)
        var wins = 0
        var losses = 0
        var streak = 0
        for (report in store.reports().filter { it.peer == caller.peer }) {
            when (report.outcome) {
                "WON" -> { wins++; streak++ }
                "LOST" -> { losses++; streak = 0 }
                else -> streak = 0
            }
        }
        val matchId = store.matchForPeer(caller.peer)?.matchId.orEmpty()
        val others = store.livePeers().count { it.peer != caller.peer }
        return Reply.spaced(jsonObj(
            "pid" to jsonText(caller.peer),
            "winStreak" to jsonInt(streak),
            "wins" to jsonInt(wins),
            "losses" to jsonInt(losses),
            "arenaID" to jsonText(arena),
            "matchID" to jsonText(matchId),
            "live" to JsonBool(matchId.isNotEmpty()),
            "livePeers" to jsonInt(others),
        ))
    }

    /** `/pvp/lock-in`, `/pvp/lockin` (`pvp_lock_in_response`). */
    fun lockIn(call: Call): ByteArray {
        val caller = call.touch()
        val arena = lockArena(call)
        val heroes = lockHeroes(call)
        if (heroes.isEmpty()) return lockRejected(caller.peer, arena)
        if (game.isUsableTeam(heroes)) store.storeRoster(caller.peer, caller.name, heroes)
        val view = matches.build(call, arena, "active")
        return Reply.spaced(view.body
            .with("locked", JsonBool(true))
            .with("matchID", jsonText(view.matchId))
            .with("live", JsonBool(view.live))
            .with("arenaID", jsonText(arena))
            .with("error", jsonText("")))
    }

    /** `pvp_arena_id` (:1362-1366): body `arenaID`, else body `pid`, else the default arena. */
    private fun lockArena(call: Call): String {
        val posted = JsonParse.textOr(call.body, "arenaID", "")
        return Fields.idSafe(posted.ifEmpty { JsonParse.textOr(call.body, "pid", config.defaultArena) })
    }

    private fun lockHeroes(call: Call): List<String> {
        val posted = JsonParse.listOrEmpty(call.body, "heroes")
        val nodes = posted.ifEmpty { JsonParse.listOrEmpty(call.body, "team") }
        return JsonParse.textList(JsonArr(nodes))
    }

    /** `pvp_lock_rejected` (:1368-1371). */
    private fun lockRejected(peer: String, arena: String): ByteArray {
        val matchId = store.matchForPeer(peer)?.matchId.orEmpty()
        return Reply.spaced(jsonObj(
            "locked" to JsonBool(false),
            "matchID" to jsonText(matchId),
            "live" to JsonBool(matchId.isNotEmpty()),
            "arenaID" to jsonText(arena),
            "error" to jsonText("heroes are required"),
        ))
    }

    /** `/pvp/select-opponent` (:1396-1410). */
    fun selectOpponent(call: Call): ByteArray {
        val view = matches.build(call, lockArena(call), "selecting")
        val paired = view.match?.opponentOf(call.peer).orEmpty()
        val opponentId = JsonParse.textOr(call.body, "opponentID", paired)
        return Reply.spaced(view.body
            .with("selected", JsonBool(true))
            .with("matchID", jsonText(view.matchId))
            .with("live", JsonBool(view.live))
            .with("opponentIndex", JsonInt(JsonParse.intOr(call.body, "index", 0L)))
            .with("opponentID", jsonText(opponentId)))
    }

    /** `/pvp/quit`, `/pvp/quit-arena` (:1412-1421): ends the caller's match and purges its events. */
    fun quitArena(call: Call): ByteArray {
        val caller = call.touch()
        val matchId = store.matchForPeer(caller.peer)?.matchId.orEmpty()
        if (matchId.isNotEmpty()) store.concludeMatch(matchId)
        return Reply.spaced(jsonObj("quit" to JsonBool(true), "matchID" to jsonText(matchId)))
    }

    /** `/pvp/match-loaded` (:1519-1523): no state change. */
    fun matchLoaded(call: Call): ByteArray {
        val fid = JsonParse.textOr(call.body, "fid", "")
        return Reply.spaced(jsonObj(
            "loaded" to JsonBool(true),
            "fightID" to jsonText(JsonParse.textOr(call.body, "fightID", fid)),
        ))
    }

    /** `/pvp/retry-match` (:1525-1528). */
    fun retryMatch(call: Call): ByteArray = Reply.spaced(matches.build(call, lockArena(call), "active").body)

    /**
     * `/pvp/find-arena-opponent`, `/pvp/get-new-opponent`, `/pvp/get-new-arena-opponent`,
     * `/pvp/get-opponents` (`pvp_opponent_response`): the live-peer-first opponent handler.
     */
    fun opponent(call: Call): ByteArray = Reply.spaced(matches.build(call, lockArena(call), "selecting").body)

    /**
     * `/matches/resolve-match/` (`resolve_match_response`, :1799-1810): feeds a retail client's
     * outcome into reconciliation when it has an active match, then returns null so the canned
     * body still answers. STORY-mode win recording is out of scope for the host.
     */
    fun resolveMatch(call: Call): ByteArray? {
        val outcome = resolvedOutcome(call.body)
        val matchId = store.matchForPeer(call.peer)?.matchId.orEmpty()
        if (matchId.isNotEmpty() && outcome.isNotEmpty()) store.reportResult(matchId, call.peer, outcome)
        return null
    }

    /** `resolved_outcome` (:1793-1797): nested `results.result` first, then `result`. */
    private fun resolvedOutcome(body: JsonObj): String {
        val nested = body.get("results") as? JsonObj ?: JsonObj(emptyList())
        return JsonParse.textOr(nested, "result", JsonParse.textOr(body, "result", ""))
    }
}
