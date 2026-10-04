package com.gummygamer.tftfpvphost.api

import com.gummygamer.tftfpvphost.json.JsonArr
import com.gummygamer.tftfpvphost.json.JsonObj
import com.gummygamer.tftfpvphost.json.JsonParse
import com.gummygamer.tftfpvphost.json.JsonValue
import com.gummygamer.tftfpvphost.json.jsonInt
import com.gummygamer.tftfpvphost.json.jsonObj
import com.gummygamer.tftfpvphost.json.jsonText
import com.gummygamer.tftfpvphost.server.HeroKeys
import com.gummygamer.tftfpvphost.server.Payload
import com.gummygamer.tftfpvphost.server.Templates
import com.gummygamer.tftfpvphost.state.PvpStore
import java.util.concurrent.ConcurrentHashMap

/**
 * Hero and roster data the PvP handlers read from the payload blob.
 *
 * Stands in for `gamedata.resolve_team` / `default_team` / `build_hero_entry` /
 * `build_base_hero_details` (Server/gamedata.lbl:653-662, 923-941, 2408-2415): the blob already
 * carries the pre-rendered `@savedteam:hero:<bid>` entry (rank 1, level 1) and `@hero:<bid>:1:1`
 * detail for every roster hero, so no stat tables are needed here.
 */
class GameData(private val payload: Payload) {
    private val roster: Set<String> = payload.lookup(HeroKeys.ROSTER_KEY)?.let(::textLines)?.toSet().orEmpty()
    private val entries = ConcurrentHashMap<String, JsonValue>()
    private val details = ConcurrentHashMap<String, JsonValue>()

    /** `gamedata.default_team()`; the blob's `@team:default` when present. */
    val defaultTeam: List<String> =
        payload.lookup(HeroKeys.DEFAULT_TEAM_KEY)?.let(::textLines)?.takeIf { it.isNotEmpty() }
            ?: PvpStore.DEFAULT_HOUSE_TEAM

    fun lookup(key: String): ByteArray? = payload.lookup(key)

    /** A bid is usable when it is id-safe and, if the blob lists a roster, a member of it. */
    fun isRosterHero(bid: String): Boolean = HeroKeys.safeId(bid) && (roster.isEmpty() || bid in roster)

    /** True when [team] could be stored and served as-is (non-empty, at most 5, every bid known). */
    fun isUsableTeam(team: List<String>): Boolean =
        team.isNotEmpty() && team.size <= TEAM_SIZE_MAX && team.all(::isRosterHero)

    /** `resolve_team`: an unusable squad becomes the default trio. */
    fun resolveTeam(team: List<String>): List<String> = if (isUsableTeam(team)) team else defaultTeam

    /** The pre-rendered hero entry text, spliced verbatim into saved-team templates. */
    fun savedTeamHeroText(bid: String): String? =
        payload.lookup(HeroKeys.savedTeamHeroKey(bid))?.toString(Charsets.UTF_8)

    /** `build_hero_entry(bid, 1, 1)` as a JSON node; a minimal identity entry when the blob has none. */
    fun heroEntry(bid: String): JsonValue = cached(entries, bid) {
        savedTeamHeroText(bid)?.let(JsonParse::parse) ?: jsonObj(
            "entity_type" to jsonText("bot"), "bid" to jsonText(bid), "rank" to jsonInt(1), "level" to jsonInt(1))
    }

    /** One `base_hero_detail` node for `matchAttributes`; `{}` when the blob has no hero record. */
    fun baseHeroDetail(bid: String): JsonValue = cached(details, bid) {
        HeroKeys.heroBody(payload, bid, 1, 1)
            ?.let { Templates.splice(it, "%SIG%" to "0") }
            ?.let { JsonParse.parse(it.toString(Charsets.UTF_8)) }
            ?: JsonObj(emptyList())
    }

    /** Builds the requested base-hero details, retaining rank and level from each request node. */
    fun baseHeroDetails(requested: List<JsonValue>): JsonArr = JsonArr(requested.map { hero ->
        val node = hero as? JsonObj ?: JsonObj(emptyList())
        val bid = JsonParse.textOr(node, "bid", "")
        val rank = JsonParse.intOr(node, "rank", 1L)
        val level = JsonParse.intOr(node, "level", 1L)
        cached(details, "$bid:$rank:$level") {
            HeroKeys.heroBody(payload, bid, rank, level)
                ?.let { Templates.splice(it, "%SIG%" to "0") }
                ?.let { JsonParse.parse(it.toString(Charsets.UTF_8)) }
                ?: JsonObj(emptyList())
        }
    })

    /** Only roster heroes are cached, so a hostile bid cannot grow the maps. */
    private fun cached(cache: ConcurrentHashMap<String, JsonValue>, bid: String, build: () -> JsonValue): JsonValue {
        if (bid !in roster) return build()
        return cache.getOrPut(bid, build)
    }

    private fun textLines(bytes: ByteArray): List<String> =
        bytes.toString(Charsets.UTF_8).split('\n').map { it.trim() }.filter { it.isNotEmpty() }

    companion object {
        /** `gamedata.team_size_max()` (Server/gamedata.lbl:918-921). */
        const val TEAM_SIZE_MAX = 5
    }
}
