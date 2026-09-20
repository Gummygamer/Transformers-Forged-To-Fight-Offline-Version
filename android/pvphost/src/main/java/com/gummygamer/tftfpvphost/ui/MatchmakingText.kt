package com.gummygamer.tftfpvphost.ui

import android.content.Context
import com.gummygamer.tftfpvphost.R
import com.gummygamer.tftfpvphost.api.StatusView

/** Turns the read-only status view into the text blocks of the matchmaking card. */
object MatchmakingText {
    fun peers(context: Context, status: StatusView?): String {
        if (status == null || status.peers.isEmpty()) return context.getString(R.string.peers_none)
        return status.peers.joinToString("\n") {
            context.getString(
                if (it.inMatch) R.string.peer_line_in_match else R.string.peer_line,
                it.name, it.key, it.ageMs / MILLIS)
        }
    }

    fun matches(context: Context, status: StatusView?): String {
        if (status == null || status.matches.isEmpty()) return context.getString(R.string.matches_none)
        return status.matches.joinToString("\n") {
            context.getString(R.string.match_line, it.matchId, it.peerA, it.peerB, it.ageMs / MILLIS)
        }
    }

    fun results(context: Context, status: StatusView?): String {
        if (status == null || status.results.isEmpty()) return context.getString(R.string.results_none)
        return status.results.joinToString("\n") {
            when (it.status) {
                "agreed" -> context.getString(R.string.result_agreed, it.matchId, it.winner, it.loser)
                "disputed" -> context.getString(R.string.result_disputed, it.matchId, it.winner, it.loser)
                else -> context.getString(R.string.result_pending, it.matchId, it.reports)
            }
        }
    }

    private const val MILLIS = 1000L
}
