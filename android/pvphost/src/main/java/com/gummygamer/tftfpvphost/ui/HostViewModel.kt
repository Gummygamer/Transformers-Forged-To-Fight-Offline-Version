package com.gummygamer.tftfpvphost.ui

import android.app.Application
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import com.gummygamer.tftfpvphost.api.HostStatus
import com.gummygamer.tftfpvphost.api.StatusView
import com.gummygamer.tftfpvphost.server.HostConfig
import com.gummygamer.tftfpvphost.server.HostRuntime
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

/** Everything the screen renders, rebuilt about once a second from the process-wide runtime. */
data class HostUiState(
    val running: Boolean = false,
    val port: Int = HostConfig.DEFAULT_PORT,
    val error: String? = null,
    val addresses: List<String> = emptyList(),
    val rewriteCdn: Boolean = true,
    val status: StatusView? = null,
    val log: List<String> = emptyList(),
)

/**
 * Bridges [HostRuntime] and [HostStatus] to the activity. The runtime is process-wide, so the
 * running flag is correct after rotation or activity recreation; a dead process has no server.
 * The port and timeout the user last chose live in [HostPrefs] so they survive process death.
 */
class HostViewModel(app: Application) : AndroidViewModel(app) {
    private val prefs = HostPrefs(app)
    private val mutableState = MutableStateFlow(HostUiState())
    val state: StateFlow<HostUiState> = mutableState.asStateFlow()

    var portText: String
        get() = prefs.portText
        set(value) { prefs.portText = value }

    var ttlMsText: String
        get() = prefs.ttlMsText
        set(value) { prefs.ttlMsText = value }

    var rewriteCdn: Boolean
        get() = prefs.rewriteCdn
        set(value) { prefs.rewriteCdn = value }

    init {
        viewModelScope.launch { poll() }
    }

    private suspend fun poll() {
        while (true) {
            val next = withContext(Dispatchers.IO) { snapshot() }
            mutableState.value = next
            delay(REFRESH_MS)
        }
    }

    private fun snapshot(): HostUiState = HostUiState(
        running = HostRuntime.running.value,
        port = HostRuntime.boundPort,
        error = HostRuntime.lastError.value,
        addresses = HostAddresses.list(),
        rewriteCdn = prefs.rewriteCdn,
        status = HostStatus.view(),
        log = HostRuntime.log.entries.value,
    )

    /** Applies a new timeout to the running host; false when the host is stopped or the value is rejected. */
    fun applyTtl(ms: Long): Boolean = HostStatus.setPresenceTtlMs(ms)

    fun resetState(): Boolean = HostStatus.resetState()

    fun clearLog() = HostRuntime.log.clear()

    private companion object {
        const val REFRESH_MS = 1000L
    }
}
