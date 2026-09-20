package com.gummygamer.tftfpvphost.api

import com.gummygamer.tftfpvphost.server.DynamicHook
import com.gummygamer.tftfpvphost.server.HostConfig
import com.gummygamer.tftfpvphost.server.HttpRequest
import com.gummygamer.tftfpvphost.state.Clock
import com.gummygamer.tftfpvphost.state.PvpStore
import com.gummygamer.tftfpvphost.state.SystemClock
import java.io.IOException

/** A storage failure while serving a request; the resolver logs it and falls back to canned data. */
class StateStoreException(cause: IOException) : RuntimeException("state store unavailable", cause)

/**
 * The dynamic layer, consulted before static resolution. Registration order and the
 * suffix/contains matching reproduce `dynamic` (Server/fakeserver.lbl:1890-1903): `/auth/login`,
 * `/matches/activate-match/pvp_fight`, any path containing `/pvp/`, `/matches/resolve-match/`,
 * then `/bcg/setSavedTeam`. Matching runs on the query-stripped path and is never equality. A
 * null result means "not handled", so the canned body still answers.
 */
class DynamicRouter(
    private val store: PvpStore,
    game: GameData,
    config: HostConfig,
    clock: Clock = SystemClock,
) : DynamicHook {
    private val boot = GameHandlers(store, game, config)
    private val matchBody = PvpMatchBody(store, game)
    private val arena = PvpArenaHandlers(store, game, config, matchBody)
    private val companion = PvpCompanionHandlers(store, clock)
    private val activation = ActivateMatchHandler(game, config, matchBody)

    private class Route(val suffixes: List<String>, val handler: (Call) -> ByteArray?)

    /** `pvp_static_dynamic` and `pvp_static_dynamic_second`, in fakeserver order (:1593-1615, 1570-1591). */
    private val pvpRoutes: List<Route> = listOf(
        Route(listOf("/pvp/heartbeat"), companion::heartbeat),
        Route(listOf("/pvp/lobby"), companion::lobby),
        Route(listOf("/pvp/leave-match"), companion::leaveMatch),
        Route(listOf("/pvp/fight-post"), companion::fightPost),
        Route(listOf("/pvp/fight-poll"), companion::fightPoll),
        Route(listOf("/pvp/report-result"), companion::reportResult),
        Route(listOf("/pvp/match-result"), companion::matchResult),
        Route(listOf("/pvp/get-login-data"), arena::loginData),
        Route(listOf("/pvp/get-user-data", "/pvp/get-active-pvp-data"), arena::arenaState),
        Route(listOf("/pvp/lockin", "/pvp/lock-in"), arena::lockIn),
        Route(listOf("/pvp/select-opponent"), arena::selectOpponent),
        Route(listOf("/pvp/quit", "/pvp/quit-arena"), arena::quitArena),
        Route(listOf("/pvp/match-loaded"), arena::matchLoaded),
        Route(listOf("/pvp/retry-match"), arena::retryMatch),
        Route(
            listOf(
                "/pvp/find-arena-opponent", "/pvp/get-new-opponent",
                "/pvp/get-new-arena-opponent", "/pvp/get-opponents",
            ),
            arena::opponent,
        ),
    )

    override fun respond(request: HttpRequest): ByteArray? = try {
        dispatch(request.path, Call(request, store))
    } catch (e: IOException) {
        throw StateStoreException(e)
    }

    private fun dispatch(path: String, call: Call): ByteArray? = when {
        path.endsWith("/auth/login") -> boot.authLogin(call)
        path.endsWith("/tutorial/get-login-data") -> boot.tutorialLogin(call)
        path.endsWith("/bcg/getUserData") -> boot.getUserData(call)
        path.endsWith("/autorefresh/grouprefresh") -> boot.groupRefresh(call)
        path.endsWith("/matches/activate-match/pvp_fight") -> activation.activate(call)
        path.contains("/pvp/") -> pvpRoutes.firstOrNull { route -> route.suffixes.any(path::endsWith) }?.handler?.invoke(call)
        isTutorialStep(path) -> boot.tutorialStep(call)
        path.endsWith("/bcg/getBaseHeroData") -> boot.baseHeroData(call)
        path.contains("/matches/resolve-match/") -> arena.resolveMatch(call)
        path.trimEnd('/').endsWith("/userprofile") -> boot.userProfile(call)
        path.endsWith("/bcg/setSavedTeam") -> boot.setSavedTeam(call)
        else -> null
    }

    private fun isTutorialStep(path: String): Boolean =
        path.endsWith("/tutorial/start-tutorial") || path.endsWith("/tutorial/start-branch") ||
            path.endsWith("/tutorial/early-start-branch") || path.endsWith("/tutorial/complete-tutorial")
}
