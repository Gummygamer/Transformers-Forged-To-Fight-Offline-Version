package com.gummygamer.tftfpvphost.api

import com.gummygamer.tftfpvphost.json.JsonObj
import com.gummygamer.tftfpvphost.json.JsonParse
import com.gummygamer.tftfpvphost.json.JsonValue
import com.gummygamer.tftfpvphost.json.jsonObj
import com.gummygamer.tftfpvphost.json.jsonText
import com.gummygamer.tftfpvphost.json.with
import com.gummygamer.tftfpvphost.server.HostConfig

/**
 * `POST /matches/activate-match/pvp_fight` (`pvp_activate_match_response`,
 * Server/fakeserver.lbl:1546-1568).
 *
 * The client discards the response body and reads its fight data from the `match-activated`
 * async message, whose payload node must itself be the flat dict
 * `{pvpMatchData, pvpUserData, matchAttributes}`; a missing `matchAttributes` makes
 * `PVPUtil.GenerateFightData` throw on the client (fakeserver.lbl:1549-1562). So `result` and
 * `async[0].payload` are the same dict.
 *
 * Deviation: fakeserver picks the opponent hero from the newest other roster even when the
 * caller is paired; here `matchAttributes` describes the same opponent the match body serves,
 * so the fight data and the opponent list can never disagree.
 */
class ActivateMatchHandler(
    private val game: GameData,
    private val config: HostConfig,
    private val matches: PvpMatchBody,
) {
    fun activate(call: Call): ByteArray {
        val arena = JsonParse.textOr(call.body, "arenaID", "").ifEmpty {
            JsonParse.textOr(call.body, "pid", config.defaultArena)
        }
        val view = matches.build(call, arena, "active")
        val data = view.body.with("matchAttributes", matchAttributes(call, view))
        val message = jsonObj(
            "component" to jsonText("PVPManager"),
            "channel" to jsonText(""),
            "message" to jsonText("match-activated"),
            "payload" to data,
        )
        return Reply.spacedAsync(data, listOf(message))
    }

    /** `activation_match_attributes` (:1538-1544): the first hero of each squad. */
    private fun matchAttributes(call: Call, view: MatchView): JsonObj {
        val posted = JsonParse.textList(call.body.get("heroes"))
        val mine = game.resolveTeam(posted.ifEmpty { view.mine.heroes })
        val theirs = game.resolveTeam(view.opponent.heroes.ifEmpty { game.defaultTeam })
        return jsonObj(
            "userHero" to detail(mine.firstOrNull() ?: game.defaultTeam.first()),
            "opponentHero" to detail(theirs.firstOrNull() ?: game.defaultTeam.first()),
        )
    }

    private fun detail(bid: String): JsonValue = game.baseHeroDetail(bid)
}
