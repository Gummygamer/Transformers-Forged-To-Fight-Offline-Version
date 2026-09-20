package com.gummygamer.tftfpvphost.ui

import android.content.Context
import com.gummygamer.tftfpvphost.server.HostConfig

/** Remembers the last port and presence timeout the user typed; contains no secrets. */
class HostPrefs(context: Context) {
    private val prefs = context.getSharedPreferences(NAME, Context.MODE_PRIVATE)

    var portText: String
        get() = prefs.getString(KEY_PORT, null) ?: HostConfig.DEFAULT_PORT.toString()
        set(value) { prefs.edit().putString(KEY_PORT, value).apply() }

    var ttlMsText: String
        get() = prefs.getString(KEY_TTL, null) ?: HostConfig.DEFAULT_PRESENCE_TTL_MS.toString()
        set(value) { prefs.edit().putString(KEY_TTL, value).apply() }

    var rewriteCdn: Boolean
        get() = prefs.getBoolean(KEY_REWRITE_CDN, true)
        set(value) { prefs.edit().putBoolean(KEY_REWRITE_CDN, value).apply() }

    var internetTunnel: Boolean
        get() = prefs.getBoolean(KEY_INTERNET_TUNNEL, false)
        set(value) { prefs.edit().putBoolean(KEY_INTERNET_TUNNEL, value).apply() }

    var relayHost: String
        get() = prefs.getString(KEY_RELAY_HOST, "").orEmpty()
        set(value) { prefs.edit().putString(KEY_RELAY_HOST, value).apply() }

    var relayPortText: String
        get() = prefs.getString(KEY_RELAY_PORT, "4433") ?: "4433"
        set(value) { prefs.edit().putString(KEY_RELAY_PORT, value).apply() }

    var tunnelRole: String
        get() = prefs.getString(KEY_TUNNEL_ROLE, "host") ?: "host"
        set(value) { prefs.edit().putString(KEY_TUNNEL_ROLE, value).apply() }

    var invitation: String
        get() = prefs.getString(KEY_INVITATION, "").orEmpty()
        set(value) { prefs.edit().putString(KEY_INVITATION, value).apply() }

    private companion object {
        const val NAME = "pvphost_ui"
        const val KEY_PORT = "port"
        const val KEY_TTL = "ttl_seconds"
        const val KEY_REWRITE_CDN = "rewrite_cdn"
        const val KEY_INTERNET_TUNNEL = "internet_tunnel"
        const val KEY_RELAY_HOST = "relay_host"
        const val KEY_RELAY_PORT = "relay_port"
        const val KEY_TUNNEL_ROLE = "tunnel_role"
        const val KEY_INVITATION = "invitation"
    }
}
