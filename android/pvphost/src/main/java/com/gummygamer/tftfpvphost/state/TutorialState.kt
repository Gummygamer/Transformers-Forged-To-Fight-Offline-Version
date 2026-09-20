package com.gummygamer.tftfpvphost.state

/** Per-peer first-login marker for the FTE tutorial (`fakeserver.lbl:126-168`). */
internal class TutorialState(private val file: RecordFile) {
    fun markFirstLogin(peer: String): Boolean {
        val key = Fields.idSafe(peer)
        if (key.isEmpty()) return false
        val seen = file.readLines().mapNotNull { line ->
            line.takeIf { it.startsWith("T|") }?.substringAfter('|')
        }.toMutableSet()
        if (!seen.add(key)) return false
        file.writeLines(seen.map { "T|$it" }.sorted())
        return true
    }

    fun clear() = file.clear()
}
