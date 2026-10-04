package com.gummygamer.tftfpvphost.server

import java.io.BufferedInputStream
import java.io.BufferedOutputStream
import java.io.IOException
import java.net.BindException
import java.net.InetSocketAddress
import java.net.ServerSocket
import java.net.Socket
import java.util.Collections
import java.util.concurrent.RejectedExecutionException
import java.util.concurrent.Semaphore
import java.util.concurrent.SynchronousQueue
import java.util.concurrent.ThreadFactory
import java.util.concurrent.ThreadPoolExecutor
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicBoolean
import java.util.concurrent.atomic.AtomicInteger

/** Raised by [HostHttpServer.start]; [message] is already actionable and user-facing. */
class HostStartException(message: String) : Exception(message)

/**
 * HTTP/1.1 listener bound to every interface (wildcard address, never loopback-only).
 *
 * One accept thread plus a bounded worker pool; each worker serves one keep-alive
 * connection. Connections beyond [MAX_CONNECTIONS] are closed immediately, mirroring
 * `MAX_CONN` in tools/nativehook/inapk_server.c:33,624. [stop] closes the listener and
 * every live socket, so blocked reads end and the pool drains.
 */
class HostHttpServer(
    private val port: Int,
    private val resolver: RouteResolver,
    private val log: ServerLog,
) {
    private val stopped = AtomicBoolean(false)
    private val permits = Semaphore(MAX_CONNECTIONS)
    private val live: MutableSet<Socket> = Collections.synchronizedSet(HashSet())
    private val workerIds = AtomicInteger()
    private var listener: ServerSocket? = null
    private var acceptThread: Thread? = null
    private val pool = ThreadPoolExecutor(
        WORKER_CORE, MAX_CONNECTIONS, WORKER_IDLE_SECONDS, TimeUnit.SECONDS,
        SynchronousQueue(), ThreadFactory { task ->
            Thread(task, "tftf-host-worker-${workerIds.incrementAndGet()}").apply { isDaemon = true }
        })

    /** Port actually bound; differs from the request only when port 0 was asked for. */
    val boundPort: Int get() = listener?.localPort ?: -1

    @Throws(HostStartException::class)
    fun start() {
        val socket = openListener()
        listener = socket
        acceptThread = Thread({ acceptLoop(socket) }, "tftf-host-accept").apply {
            isDaemon = true
            start()
        }
        log.append("listening on all interfaces, port ${socket.localPort}")
    }

    /** Idempotent; safe to call from any thread, including after a failed start. */
    fun stop() {
        if (!stopped.compareAndSet(false, true)) return
        val wasListening = listener != null
        closeQuietly(listener)
        synchronized(live) { live.toList() }.forEach { closeQuietly(it) }
        pool.shutdownNow()
        acceptThread?.join(JOIN_MS)
        pool.awaitTermination(JOIN_MS, TimeUnit.MILLISECONDS)
        if (wasListening) log.append("server stopped")
    }

    private fun openListener(): ServerSocket {
        val socket = ServerSocket()
        try {
            socket.reuseAddress = true
            // A null address binds the wildcard address: every interface, not loopback only.
            socket.bind(InetSocketAddress(null as java.net.InetAddress?, port), BACKLOG)
            return socket
        } catch (e: BindException) {
            closeQuietly(socket)
            throw HostStartException(bindMessage(e))
        } catch (e: IOException) {
            closeQuietly(socket)
            throw HostStartException(HostMessages.bindFailed(port, e.message ?: e.javaClass.simpleName))
        } catch (e: SecurityException) {
            closeQuietly(socket)
            throw HostStartException(HostMessages.bindFailed(port, "permission denied"))
        }
    }

    private fun bindMessage(e: BindException): String {
        val reason = e.message.orEmpty()
        return if (reason.contains("in use", ignoreCase = true)) {
            HostMessages.portInUse(port)
        } else {
            HostMessages.bindFailed(port, reason.ifEmpty { "bind failed" })
        }
    }

    private fun acceptLoop(socket: ServerSocket) {
        while (!stopped.get()) {
            val client = try {
                socket.accept()
            } catch (e: IOException) {
                if (!stopped.get()) log.append("accept failed: ${e.javaClass.simpleName}")
                return
            }
            dispatch(client)
        }
    }

    private fun dispatch(client: Socket) {
        if (!permits.tryAcquire()) {
            closeQuietly(client)
            return
        }
        live.add(client)
        try {
            pool.execute { serve(client) }
        } catch (e: RejectedExecutionException) {
            release(client)
        }
    }

    private fun serve(client: Socket) {
        try {
            client.soTimeout = READ_TIMEOUT_MS
            client.tcpNoDelay = true
            val input = BufferedInputStream(client.getInputStream(), 8192)
            val output = BufferedOutputStream(client.getOutputStream(), 8192)
            while (!stopped.get() && serveOne(input, output)) {
                // keep-alive loop
            }
        } catch (e: IOException) {
            // Timeouts, resets and stop() closing the socket all end the connection quietly.
        } finally {
            release(client)
        }
    }

    /** Handles one request; false ends the connection. */
    private fun serveOne(input: BufferedInputStream, output: BufferedOutputStream): Boolean {
        return when (val result = HttpRequestReader.read(input) { HttpResponse.writeContinue(output) }) {
            is ReadResult.Closed -> false
            is ReadResult.Rejected -> {
                log.append("rejected request: ${result.reason}")
                HttpResponse.writeRejection(output, result.status, result.reason)
                false
            }
            is ReadResult.Request -> answer(result.request, output)
        }
    }

    private fun answer(request: HttpRequest, output: BufferedOutputStream): Boolean {
        val resolution = resolver.resolve(request)
        log.append("${safeMethod(request.method)} ${request.path} -> ${describe(resolution)}")
        HttpResponse.writeOk(output, resolution.body, request.isHead, !request.keepAlive)
        return request.keepAlive
    }

    private fun describe(resolution: Resolution): String =
        if (resolution.source == RouteSource.DEFAULT) "default 200 envelope"
        else "${resolution.source.label} (${resolution.body.size}B)"

    private fun safeMethod(method: String): String = method.take(MAX_LOGGED_METHOD).uppercase()

    private fun release(client: Socket) {
        closeQuietly(client)
        if (live.remove(client)) permits.release()
    }

    private fun closeQuietly(closeable: java.io.Closeable?) {
        try {
            closeable?.close()
        } catch (e: IOException) {
            // Nothing useful to do when closing an already-broken socket.
        }
    }

    companion object {
        const val MAX_CONNECTIONS = 64
        private const val WORKER_CORE = 4
        private const val WORKER_IDLE_SECONDS = 60L
        private const val BACKLOG = 64
        private const val READ_TIMEOUT_MS = 60_000
        private const val JOIN_MS = 2_000L
        private const val MAX_LOGGED_METHOD = 8
    }
}
