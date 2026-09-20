package com.gummygamer.tftfpvphost.state

/** One `/auth/login` identity: a device fingerprint bound to a stable URL-safe token. */
data class Session(val fingerprint: String, val stoken: String, val uid: String, val name: String)

/**
 * Sessions store (`S|fingerprint|stoken|uid|name`), reproducing `load_sessions`, `ensure_session`
 * and `token_for_fingerprint` (Server/fakeserver.lbl:337-356, 794-839).
 */
internal class Sessions(private val file: RecordFile) {
    fun load(): List<Session> = file.readLines().mapNotNull(::parse)

    fun forToken(stoken: String): Session? =
        if (stoken.isEmpty()) null else load().firstOrNull { it.stoken == stoken }

    /** Returns the existing row unchanged (stable token) or mints and stores a new one. */
    fun ensure(fingerprint: String, name: String): Session {
        val safeFingerprint = Fields.idSafe(fingerprint)
        val sessions = load()
        sessions.firstOrNull { it.fingerprint == safeFingerprint }?.let { return it }
        val created = Session(
            fingerprint = safeFingerprint,
            stoken = tokenFor(safeFingerprint, sessions),
            uid = (FIRST_UID + sessions.size).toString(),
            name = Fields.nameSafe(name),
        )
        // A full table hands out an unpersisted session rather than growing without bound.
        if (sessions.size < MAX_SESSIONS) file.writeLines(sessions.map(::render) + render(created))
        return created
    }

    fun clear() = file.clear()

    private fun tokenFor(fingerprint: String, sessions: List<Session>): String {
        val base = ("sess" + fingerprint.filter { it in TOKEN_CHARS }).take(TOKEN_MAX)
        fun taken(token: String) = sessions.any { it.stoken == token && it.fingerprint != fingerprint }
        if (!taken(base)) return base
        var counter = sessions.size
        while (true) {
            val suffix = counter.toString()
            val candidate = base.take(maxOf(0, TOKEN_MAX - suffix.length)) + suffix
            if (!taken(candidate)) return candidate
            counter++
        }
    }

    private fun render(session: Session): String =
        "S|${session.fingerprint}|${session.stoken}|${session.uid}|${session.name}"

    private fun parse(line: String): Session? {
        val f = RecordFile.fields(line)
        val fingerprint = RecordFile.field(f, 1)
        val stoken = RecordFile.field(f, 2)
        val uid = RecordFile.field(f, 3)
        if (RecordFile.field(f, 0) != "S" || fingerprint.isEmpty() || stoken.isEmpty() || uid.isEmpty()) return null
        return Session(fingerprint, stoken, uid, RecordFile.field(f, 4))
    }

    private companion object {
        const val FIRST_UID = 1_000_000_000_001L
        const val TOKEN_MAX = 32
        const val MAX_SESSIONS = 10_000
        const val TOKEN_CHARS = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789"
    }
}
