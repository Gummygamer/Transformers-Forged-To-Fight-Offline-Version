package com.gummygamer.tftfpvphost.server

/**
 * Payload helper-key lookups for hero data (`@hero:<bid>:<rank>:<level>` and friends).
 *
 * The chain reproduces inapk_server.c:449: exact rank/level, then `@hero:<bid>:1:1`,
 * then the wildcard `@hero:*:1:1`.
 */
object HeroKeys {
    private const val MAX_LEVEL = 30

    /** Rank and level default to 1 when absent or 0; level is clamped to 30. */
    fun heroBody(payload: Payload, bid: String, rank: Long, level: Long): ByteArray? {
        val safeRank = if (rank <= 0L) 1L else rank
        val safeLevel = if (level <= 0L) 1L else minOf(level, MAX_LEVEL.toLong())
        return payload.lookup("@hero:$bid:$safeRank:$safeLevel")
            ?: payload.lookup("@hero:$bid:1:1")
            ?: payload.lookup("@hero:*:1:1")
    }

    fun savedTeamHeroKey(bid: String): String = "@savedteam:hero:$bid"

    fun questMemberKey(bid: String): String = "@questmember:$bid"

    const val ROSTER_KEY: String = "@roster"
    const val DEFAULT_TEAM_KEY: String = "@team:default"

    /** `safe_id` (inapk_server.c:153): `[A-Za-z0-9_.:-]`, 1..63 characters. */
    fun safeId(text: String): Boolean =
        text.length in 1..63 && text.all { it in 'a'..'z' || it in 'A'..'Z' || it in '0'..'9' || it in "_.:-" }
}
