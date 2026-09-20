package com.gummygamer.tftfpvphost.api

import com.gummygamer.tftfpvphost.json.JsonArr
import com.gummygamer.tftfpvphost.json.JsonBool
import com.gummygamer.tftfpvphost.json.JsonDec
import com.gummygamer.tftfpvphost.json.JsonInt
import com.gummygamer.tftfpvphost.json.JsonObj
import com.gummygamer.tftfpvphost.json.JsonValue
import com.gummygamer.tftfpvphost.json.jsonInt
import com.gummygamer.tftfpvphost.json.jsonObj
import com.gummygamer.tftfpvphost.json.jsonText
import com.gummygamer.tftfpvphost.state.Match
import com.gummygamer.tftfpvphost.state.PvpStore
import com.gummygamer.tftfpvphost.state.Roster

/** A built match body plus the facts handlers need to decorate it. */
class MatchView(val body: JsonObj, val match: Match?, val mine: Roster, val opponent: Roster) {
    val matchId: String get() = match?.matchId.orEmpty()
    val live: Boolean get() = match != null
}

/**
 * The `pvpMatchData` / `pvpUserData` containers the client requires, reproducing
 * `pvp_match_result` and its builders (Server/fakeserver.lbl:1423-1512).
 *
 * The opponent is the paired peer's saved team when the caller is in a match; otherwise the
 * newest roster stored by a different peer; otherwise the Autobot Garrison house team
 * (`opponent_roster`, :882-894, supplied by [PvpStore]).
 */
class PvpMatchBody(private val store: PvpStore, private val game: GameData) {
    /** Touches the caller, pairs them if a partner is live, and builds the body in [state]. */
    fun build(call: Call, arena: String, state: String): MatchView {
        val caller = call.touch()
        val match = store.pairPeers(caller.peer, arena)
        val mine = store.rosterFor(caller.peer) ?: Roster(caller.peer, caller.name, game.defaultTeam)
        val opponent = opponentRoster(match, caller.peer)
        return MatchView(matchJson(match, arena, state, mine, opponent), match, mine, opponent)
    }

    /** `match_opponent_roster` (:1444-1456). */
    private fun opponentRoster(match: Match?, peer: String): Roster {
        if (match == null) return store.opponentRoster(peer)
        val other = match.opponentOf(peer)
        return store.rosterFor(other) ?: Roster(other, liveName(other), game.defaultTeam)
    }

    /** `live_presence_name` (:1195-1203): live presence name, else session name, else Commander. */
    private fun liveName(peer: String): String {
        store.livePeers().firstOrNull { it.peer == peer && it.name.isNotEmpty() }?.let { return it.name }
        return store.sessionForToken(peer)?.name?.takeIf { it.isNotEmpty() } ?: "Commander"
    }

    private fun matchJson(match: Match?, arena: String, state: String, mine: Roster, opponent: Roster): JsonObj {
        val stats = arenaStats(arena)
        val heroes = game.resolveTeam(opponent.heroes)
        val matchups = matchups(game.resolveTeam(mine.heroes), heroes)
        val node = opponentNode(opponent, heroes, matchups, stats)
        return jsonObj(
            "pvpMatchData" to JsonArr(listOf(matchData(arena, match, state, node))),
            "pvpUserData" to JsonArr(listOf(stats)),
        )
    }

    /** `pvp_match_data` (:1489-1492). */
    private fun matchData(arena: String, match: Match?, state: String, node: JsonObj): JsonObj = jsonObj(
        "pid" to jsonText(arena),
        "matchID" to jsonText(match?.matchId.orEmpty()),
        "live" to JsonBool(match != null),
        "state" to jsonText(state),
        "opponentIndex" to jsonInt(0),
        "opponents" to JsonArr(listOf(node)),
        "fightRewards" to JsonArr(emptyList()),
        "seriesRewards" to jsonObj("claimed" to JsonBool(false), "box" to jsonText("")),
        "milestones" to JsonArr(emptyList()),
    )

    /** `pvp_opponent_node` (:1483-1487). */
    private fun opponentNode(opponent: Roster, heroes: List<String>, matchups: List<JsonValue>, stats: JsonObj) =
        jsonObj(
            "uid" to JsonInt(opponentUid(opponent.peer)),
            "team" to JsonArr(heroes.map(game::heroEntry)),
            "matchups" to JsonArr(matchups),
            "userData" to jsonObj(
                "name" to jsonText(opponent.name),
                "tag" to jsonText("LAN"),
                "level" to jsonInt(OPPONENT_LEVEL),
                "portrait" to jsonText(heroes.firstOrNull().orEmpty()),
                "factionId" to jsonText(""),
                "pvpStats" to stats,
            ),
        )

    /** `opponent_uid_for` (:1458-1467). */
    private fun opponentUid(peer: String): Long {
        val index = store.sessionIndexFor(peer)
        return if (index >= 0) FIRST_OPPONENT_UID + index else NO_SESSION_UID
    }

    /** `pvp_matchups` (:1423-1433): one row per opponent hero, padded with the caller's first hero. */
    private fun matchups(userHeroes: List<String>, opponentHeroes: List<String>): List<JsonValue> =
        opponentHeroes.mapIndexed { index, hero ->
            val user = userHeroes.getOrNull(index) ?: userHeroes.firstOrNull() ?: hero
            jsonObj(
                "userHero" to jsonText(user),
                "opponentHero" to jsonText(hero),
                "aip" to jsonText("normal"),
                "state" to jsonText("none"),
                "outcome" to jsonText("NONE"),
                "basePoints" to JsonDec(0.0),
                "actualPoints" to JsonDec(0.0),
                "fightId" to jsonText(""),
            )
        }

    /** `pvp_arena_stats` (:1478-1481): the client-required stats node, always zeroed. */
    private fun arenaStats(arena: String): JsonObj = jsonObj(
        "pid" to jsonText(arena),
        "winStreak" to jsonInt(0),
        "wins" to jsonInt(0),
        "losses" to jsonInt(0),
        "longestStreak" to jsonInt(0),
        "findAttempts" to jsonInt(0),
    )

    private companion object {
        const val OPPONENT_LEVEL = 40
        const val FIRST_OPPONENT_UID = 1_000_000_001L
        const val NO_SESSION_UID = 1_000_000_000L
    }
}
