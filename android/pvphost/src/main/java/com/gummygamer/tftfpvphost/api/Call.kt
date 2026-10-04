package com.gummygamer.tftfpvphost.api

import com.gummygamer.tftfpvphost.json.JsonObj
import com.gummygamer.tftfpvphost.json.JsonParse
import com.gummygamer.tftfpvphost.server.HttpRequest
import com.gummygamer.tftfpvphost.state.Fields
import com.gummygamer.tftfpvphost.state.PvpStore

/**
 * One request as a handler sees it: the parsed JSON body (lazily, `{}` when it is not an
 * object) and the caller identity.
 *
 * The peer key is `peer` query, else `stoken` query, else `"local"` (`peer_key`,
 * Server/fakeserver.lbl:1018-1029). It is sanitised the way every store sanitises it, so the
 * same string keys presence, rosters, matches and reports.
 */
class Call(val request: HttpRequest, private val store: PvpStore) {
    val body: JsonObj by lazy(LazyThreadSafetyMode.NONE) { JsonParse.requestObject(request.bodyText()) }

    private val rawPeer: String = peerKey(request)
    val peer: String = Fields.idSafe(rawPeer)

    fun query(name: String): String = request.queryValue(name)

    /** `pvp_caller_name` (fakeserver.lbl:1031-1041): session name, else body name, else the key. */
    fun callerName(): String {
        val session = store.sessionForToken(query("stoken"))?.name.orEmpty()
        if (session.isNotEmpty()) return session
        val supplied = JsonParse.textFirst(body, "name", "playerName", "")
        return supplied.ifEmpty { rawPeer }
    }

    /** Marks the caller live and returns the stored presence row. */
    fun touch() = store.touchPresence(peer, callerName())

    /** Arena from the body `arenaID`, else the configured default. */
    fun arenaId(default: String): String = Fields.idSafe(JsonParse.textOr(body, "arenaID", default))

    private fun peerKey(request: HttpRequest): String {
        val peer = request.queryValue("peer")
        if (peer.isNotEmpty()) return peer
        return request.queryValue("stoken").ifEmpty { "local" }
    }
}
