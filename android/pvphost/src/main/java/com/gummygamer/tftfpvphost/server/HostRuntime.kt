package com.gummygamer.tftfpvphost.server

import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow

/** Result of [HostRuntime.start]; failures carry an actionable, user-facing message. */
sealed interface StartResult {
    object Started : StartResult

    class Failed(val message: String) : StartResult
}

/** Builds the dynamic hook once the payload has been validated and the config is known. */
fun interface DynamicHookFactory {
    fun create(payload: Payload, config: HostConfig): DynamicHook
}

/**
 * Process-wide owner of the running server, shared by [com.gummygamer.tftfpvphost.HostService]
 * and the UI. It holds only plain JVM types (no `android.*`), so it is unit-testable.
 * Start and stop are serialised on one lock and both are idempotent.
 */
object HostRuntime {
    val log: ServerLog = ServerLog()

    private val lock = Any()
    private val runningFlag = MutableStateFlow(false)
    private val errorFlag = MutableStateFlow<String?>(null)
    private var server: HostHttpServer? = null
    private var activeConfig: HostConfig? = null

    /** True only while the listener is actually bound. */
    val running: StateFlow<Boolean> = runningFlag.asStateFlow()

    /** Message from the last failed start, cleared by the next successful start. */
    val lastError: StateFlow<String?> = errorFlag.asStateFlow()

    val config: HostConfig? get() = synchronized(lock) { activeConfig }

    val boundPort: Int get() = synchronized(lock) { server?.boundPort ?: -1 }

    /**
     * Validates [payloadBytes], binds the listener and marks the runtime running.
     * [dynamic] is the PvP hook (a no-op until the endpoint layer is registered).
     */
    fun start(
        payloadBytes: ByteArray,
        config: HostConfig,
        dynamic: DynamicHook = DynamicHook { null },
    ): StartResult = startServing(payloadBytes, config) { _, _ -> dynamic }

    /** Like [start], but the hook is built from the validated payload (the PvP layer needs it). */
    fun startServing(
        payloadBytes: ByteArray,
        config: HostConfig,
        factory: DynamicHookFactory,
    ): StartResult = synchronized(lock) {
        if (server != null) return StartResult.Started
        val failure = prepare(payloadBytes, config)
        if (failure is PrepareOutcome.Failed) return fail(failure.message)
        val payload = (failure as PrepareOutcome.Ready).payload
        bind(payload, config, factory)
    }

    /** Idempotent; releases the socket, the workers and the payload bytes. */
    fun stop() {
        val stopping = synchronized(lock) {
            val current = server
            server = null
            activeConfig = null
            runningFlag.value = false
            current
        }
        stopping?.stop()
    }

    private sealed interface PrepareOutcome {
        class Ready(val payload: Payload) : PrepareOutcome

        class Failed(val message: String) : PrepareOutcome
    }

    private fun prepare(payloadBytes: ByteArray, config: HostConfig): PrepareOutcome {
        config.validationError()?.let { return PrepareOutcome.Failed(it) }
        if (payloadBytes.isEmpty()) return PrepareOutcome.Failed(HostMessages.PAYLOAD_MISSING)
        return when (val parsed = Payload.parse(payloadBytes)) {
            is PayloadResult.Invalid ->
                PrepareOutcome.Failed(HostMessages.payloadInvalid(parsed.code, parsed.detail))
            is PayloadResult.Loaded -> PrepareOutcome.Ready(parsed.payload)
        }
    }

    private fun bind(payload: Payload, config: HostConfig, factory: DynamicHookFactory): StartResult {
        val resolver = RouteResolver(payload, config, factory.create(payload, config)) { log.append(it) }
        val candidate = HostHttpServer(config.port, resolver, log)
        try {
            candidate.start()
        } catch (e: HostStartException) {
            candidate.stop()
            return fail(e.message.orEmpty())
        }
        server = candidate
        activeConfig = config
        errorFlag.value = null
        runningFlag.value = true
        if (payload.port != config.port) {
            log.append(HostMessages.payloadPortNote(payload.port, config.port))
        }
        return StartResult.Started
    }

    private fun fail(message: String): StartResult {
        log.append(message)
        errorFlag.value = message
        runningFlag.value = false
        return StartResult.Failed(message)
    }

    /** Records a start failure that happened before the runtime was reached (asset load). */
    fun reportFailure(message: String) {
        synchronized(lock) { if (server == null) fail(message) }
    }
}
