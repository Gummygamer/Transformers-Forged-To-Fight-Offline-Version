package com.gummygamer.tftfpvphost.api

import com.gummygamer.tftfpvphost.json.JsonInt
import com.gummygamer.tftfpvphost.json.JsonObj
import com.gummygamer.tftfpvphost.json.JsonParse
import com.gummygamer.tftfpvphost.json.JsonText
import com.gummygamer.tftfpvphost.json.JsonEncoder
import com.gummygamer.tftfpvphost.json.jsonInt
import com.gummygamer.tftfpvphost.json.jsonObj
import com.gummygamer.tftfpvphost.json.jsonText
import com.gummygamer.tftfpvphost.server.HeroKeys
import com.gummygamer.tftfpvphost.server.HostConfig
import com.gummygamer.tftfpvphost.server.Templates
import com.gummygamer.tftfpvphost.state.PvpStore
import com.gummygamer.tftfpvphost.state.Session

/**
 * Identity and roster capture: `POST /auth/login` (`auth_login_response`,
 * Server/fakeserver.lbl:1147-1169) and `POST /bcg/setSavedTeam` (`saved_team_response`,
 * :1819-1840). setSavedTeam is the roster capture the opponent handler later serves.
 */
class GameHandlers(
    private val store: PvpStore,
    private val game: GameData,
    private val config: HostConfig,
) {
    /** Compact envelope with the per-device session token; a repeat login returns the same one. */
    fun authLogin(call: Call): ByteArray {
        val body = call.body
        val credentials = body.get("credentials") as? JsonObj ?: JsonObj(emptyList())
        val deviceName = JsonParse.textOr(credentials, "deviceName", "")
        val deviceModel = JsonParse.textOr(credentials, "deviceModel", "")
        val fingerprint = listOf(
            JsonParse.textOr(credentials, "udid", ""),
            JsonParse.textOr(body, "bid", ""),
            deviceName,
            deviceModel,
        ).firstOrNull { it.isNotEmpty() } ?: "local"
        val name = listOf(deviceName, deviceModel).firstOrNull { it.isNotEmpty() } ?: DEFAULT_NAME
        return Reply.compact(loginResult(store.ensureSession(fingerprint, name)))
    }

    private fun loginResult(session: Session): JsonObj = jsonObj(
        "stoken" to jsonText(session.stoken),
        "server_tag" to jsonText("prod"),
        "userToken" to jsonText(""),
        "auth_data" to JsonObj(emptyList()),
        "user" to jsonObj(
            "uid" to JsonInt(session.uid.toLongOrNull() ?: FIRST_UID),
            "name" to jsonText(session.name),
            "level" to jsonInt(config.accountLevel),
            "email" to jsonText(""),
            "fbid" to jsonText(""),
            "geo" to jsonText("US"),
            "naid" to jsonText(""),
            "cohort" to jsonInt(0),
            "revenue" to jsonInt(0),
        ),
    )

    /**
     * Stores the posted squad as the caller's roster, then answers with the blob's saved-team
     * template. A squad that is empty, too large or names an unknown hero never overwrites the
     * last valid roster (design section 5.5). Null (unhandled) when the blob lacks the template.
     */
    fun setSavedTeam(call: Call): ByteArray? {
        val heroes = JsonParse.textList(call.body.get("heroes"))
        if (game.isUsableTeam(heroes)) store.storeRoster(call.peer, savedTeamName(call), heroes)
        val squad = game.resolveTeam(store.rosterFor(call.peer)?.heroes.orEmpty())
        return savedTeamBody(teamId(call.body), squad)
    }

    /** Per-peer user data from the payload template, with that peer's saved squad spliced in. */
    fun getUserData(call: Call): ByteArray? {
        val squad = game.resolveTeam(store.rosterFor(call.peer)?.heroes.orEmpty())
        val parts = teamFragments(squad) ?: return null
        val template = game.lookup(USER_DATA_TEMPLATE) ?: return null
        return Templates.splice(template, "%STEAM%" to parts.first, "%ATEAM%" to parts.second)
    }

    /** Compact profile template with the caller's stable uid, name and current login time. */
    fun userProfile(call: Call): ByteArray? {
        val template = game.lookup(USER_PROFILE_TEMPLATE) ?: return null
        val session = store.sessionForToken(call.query("stoken"))
        val uid = JsonParse.valueText(call.body.get("uid"), session?.uid ?: FIRST_UID.toString())
        val name = session?.name?.takeIf { it.isNotEmpty() } ?: DEFAULT_NAME
        val now = (System.currentTimeMillis() / 1000L).toString()
        val escapedName = JsonEncoder.encodeCompact(jsonText(name))
        val spliced = Templates.splice(template, "%UID%" to uid, "%NOW%" to now) ?: return null
        return String(spliced, Charsets.UTF_8)
            .replace("\"name\":\"Commander\"", "\"name\":$escapedName")
            .toByteArray(Charsets.UTF_8)
    }

    /** Group refresh returns the authored missions update only when the client requested it. */
    fun groupRefresh(call: Call): ByteArray? {
        val key = if (hasMissionsConfig(call.request.query)) "@grouprefresh:missionsconfig" else "@grouprefresh:"
        return game.lookup(key)
    }

    /** The FTE marker is per peer so one device cannot consume another device's introduction. */
    fun tutorialLogin(call: Call): ByteArray = if (store.markTutorialLogin(call.peer)) {
        Reply.compact(jsonObj("FTE" to jsonObj(
            "s" to jsonInt(1),
            "current_bid" to jsonText("FTEIntroQuest"),
            "branches" to jsonObj("FTEIntroQuest" to jsonObj("s" to jsonInt(1))),
        )))
    } else {
        Reply.compact(jsonObj("FTE" to jsonObj(
            "s" to jsonInt(2), "current_bid" to jsonText("FTEComplete"), "branches" to JsonObj(emptyList()),
        )))
    }

    /** Serves the payload's started/completed tutorial template, or null for an unknown tid. */
    fun tutorialStep(call: Call): ByteArray? {
        val tid = JsonParse.textFirst(call.body, "tid", "tutorialId", "id")
        if (!HeroKeys.safeId(tid)) return null
        val bid = JsonParse.textFirst(call.body, "bid", "branchId", "")
        if (bid.isNotEmpty() && !HeroKeys.safeId(bid)) return null
        val blocked = payloadLines("@tutorial:blocked")
        val live = payloadLines("@tutorial:live")
        val key = when {
            tid in blocked -> "@tutorial:error"
            tid in live && !call.request.path.endsWith("/complete-tutorial") ->
                if (bid.isEmpty()) "@tutorial:started-nobid" else "@tutorial:started"
            else -> if (bid.isEmpty()) "@tutorial:completed-nobid" else "@tutorial:completed"
        }
        val template = game.lookup(key) ?: return null
        return Templates.splice(template, "%TID%" to tid, "%BID%" to bid)
    }

    /** Base hero data honors each requested bid, rank and level and uses payload fallback rules. */
    fun baseHeroData(call: Call): ByteArray =
        Reply.spaced(game.baseHeroDetails(JsonParse.listOrEmpty(call.body, "heroes")))

    private fun savedTeamName(call: Call): String {
        val posted = (call.body.get("name") as? JsonText)?.value
        if (posted != null) return posted
        return store.sessionForToken(call.peer)?.name?.takeIf { it.isNotEmpty() } ?: DEFAULT_NAME
    }

    /** `%TID%` is spliced raw into JSON, so anything that is not id-safe becomes `"0"`. */
    private fun teamId(body: JsonObj): String {
        val posted = JsonParse.valueText(body.get("teamID"), "0")
        return if (HeroKeys.safeId(posted)) posted else "0"
    }

    private fun savedTeamBody(teamId: String, squad: List<String>): ByteArray? {
        val template = game.lookup(SAVED_TEAM_TEMPLATE) ?: return null
        val parts = teamFragments(squad) ?: return null
        val spliced = Templates.splice(
            template, "%TID%" to teamId, "%STEAM%" to parts.first, "%ATEAM%" to parts.second)
        return spliced?.let(Templates::jsonDefaultSpaces)
    }

    private fun teamFragments(squad: List<String>): Pair<String, String>? {
        val texts = squad.map { game.savedTeamHeroText(it) ?: return null }
        val steam = texts.joinToString(",", "[", "]")
        val ateam = squad.zip(texts).joinToString(",", "{", "}") { (bid, text) -> "\"$bid\":$text" }
        return steam to ateam
    }

    private fun payloadLines(key: String): Set<String> =
        game.lookup(key)?.toString(Charsets.UTF_8)?.lineSequence()?.map(String::trim)?.filter(String::isNotEmpty)
            ?.toSet().orEmpty()

    private fun hasMissionsConfig(query: String): Boolean = query.split('&').any { item ->
        val pair = item.split('=', limit = 2)
        pair.size == 2 && com.gummygamer.tftfpvphost.server.percentDecode(pair[0]).matches(Regex("groups\\.\\d+\\.name")) &&
            com.gummygamer.tftfpvphost.server.percentDecode(pair[1]) == "missionsconfig"
    }

    private companion object {
        const val DEFAULT_NAME = "Commander"
        const val SAVED_TEAM_TEMPLATE = "@savedteam:template"
        const val USER_DATA_TEMPLATE = "@userdata:template"
        const val USER_PROFILE_TEMPLATE = "@userprofile:template"
        const val FIRST_UID = 1_000_000_000_001L
    }
}
