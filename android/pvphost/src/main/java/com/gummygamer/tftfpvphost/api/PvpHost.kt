package com.gummygamer.tftfpvphost.api

import com.gummygamer.tftfpvphost.server.DynamicHookFactory
import com.gummygamer.tftfpvphost.state.PvpStore
import java.io.File

/**
 * Wires the PvP layer into [com.gummygamer.tftfpvphost.server.HostRuntime]: builds the state
 * store under [stateDir] (app-private storage), the payload-backed [GameData], and the
 * [DynamicRouter], and registers the store with [HostStatus].
 */
object PvpHost {
    /** Directory name under the app's private files dir. */
    const val STATE_DIR_NAME = "pvphost-state"

    fun hookFactory(stateDir: File): DynamicHookFactory = DynamicHookFactory { payload, config ->
        val game = GameData(payload)
        val store = PvpStore(
            stateDir, houseTeam = game.defaultTeam, presenceTtlMs = config.presenceTtlMs)
        HostStatus.attach(store)
        DynamicRouter(store, game, config)
    }
}
