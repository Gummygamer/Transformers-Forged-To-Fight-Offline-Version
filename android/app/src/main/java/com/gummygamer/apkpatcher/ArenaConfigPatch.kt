package com.gummygamer.apkpatcher

/**
 * Writes the live-Arena session into the arm64 hook's patchable block (see `g_arena_patch` in
 * `tools/nativehook/arena.c`). The block is a 16-byte marker followed by a zero-filled body that
 * the hook parses as `key=value` lines; an unpatched hook has an empty body and so stays on the
 * retail async Arena path.
 */
object ArenaConfigPatch {
    const val MARKER = "TFTF-ARENA-CFG-1"
    const val BODY_LENGTH = 192
    const val DEFAULT_PORT = 8777
    const val DEFAULT_ROOM = "arena_versus"

    data class Result(val data: ByteArray?, val error: String?) {
        val isSuccess: Boolean get() = error == null
    }

    fun patch(hook: ByteArray, relayHost: String, relayPort: Int, room: String = DEFAULT_ROOM): Result {
        if (!relayHost.matches(HOST_PATTERN)) return Result(null, "Arena relay host may only contain letters, digits, dots and dashes")
        if (relayPort !in 1..65535) return Result(null, "Arena relay port must be between 1 and 65535")
        val body = "host=$relayHost\nport=$relayPort\nroom=$room\n".toByteArray(Charsets.US_ASCII)
        if (body.size >= BODY_LENGTH) return Result(null, "Arena relay host is too long for the hook's session block")

        val marker = MARKER.toByteArray(Charsets.US_ASCII)
        val at = indexOf(hook, marker)
        if (at < 0) return Result(null, "This hook was built without the live Arena netcode; rerun android/tools/prepare-assets.sh")
        if (indexOf(hook, marker, at + 1) >= 0) return Result(null, "Hook contains more than one Arena session block")
        val bodyStart = at + marker.size
        if (bodyStart + BODY_LENGTH > hook.size) return Result(null, "Hook's Arena session block is truncated")

        val out = hook.copyOf()
        java.util.Arrays.fill(out, bodyStart, bodyStart + BODY_LENGTH, 0)
        System.arraycopy(body, 0, out, bodyStart, body.size)
        return Result(out, null)
    }

    private val HOST_PATTERN = Regex("^[A-Za-z0-9.-]{1,96}$")

    private fun indexOf(data: ByteArray, needle: ByteArray, start: Int = 0): Int {
        for (i in start..data.size - needle.size) {
            var match = true
            for (j in needle.indices) {
                if (data[i + j] != needle[j]) { match = false; break }
            }
            if (match) return i
        }
        return -1
    }
}
