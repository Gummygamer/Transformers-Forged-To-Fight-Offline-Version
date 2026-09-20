package com.gummygamer.tftfpvphost.state

/** A peer's saved squad. The house roster uses peer `"house"`. */
data class Roster(val peer: String, val name: String, val heroes: List<String>)

/**
 * Rosters store (`R|peer|name|hero,hero,...`): one replaceable row per peer, newest last,
 * reproducing `load_rosters`, `store_roster` and `opponent_roster`
 * (Server/fakeserver.lbl:841-894).
 */
internal class Rosters(private val file: RecordFile, private val houseTeam: List<String>) {
    fun load(): List<Roster> = file.readLines().mapNotNull(::parse)

    fun forPeer(peer: String): Roster? {
        val key = Fields.idSafe(peer)
        return load().firstOrNull { it.peer == key }
    }

    /**
     * Replaces [peer]'s row. A squad with no usable hero id is a no-op so a transient partial
     * update never erases the last valid choice (fakeserver.lbl:240-241).
     */
    fun store(peer: String, name: String, heroes: List<String>): Boolean {
        val key = Fields.idSafe(peer)
        val cleaned = heroes.map { Fields.idSafe(it.trim()) }.filter { it.isNotEmpty() }
        if (key.isEmpty() || cleaned.isEmpty()) return false
        val kept = load().filter { it.peer != key }.takeLast(MAX_ROSTERS - 1)
        val row = Roster(key, Fields.nameSafe(name), cleaned)
        file.writeLines((kept + row).map(::render))
        return true
    }

    /** Most recently stored roster of a different peer, else the Autobot Garrison house roster. */
    fun opponentFor(peer: String): Roster {
        val key = Fields.idSafe(peer)
        return load().lastOrNull { it.peer != key } ?: house()
    }

    fun house(): Roster = Roster("house", "Autobot Garrison", houseTeam)

    fun clear() = file.clear()

    private fun render(roster: Roster): String =
        "R|${roster.peer}|${roster.name}|${roster.heroes.joinToString(",")}"

    private fun parse(line: String): Roster? {
        val f = RecordFile.fields(line)
        val peer = RecordFile.field(f, 1)
        if (RecordFile.field(f, 0) != "R" || peer.isEmpty()) return null
        val heroes = RecordFile.field(f, 3).split(',').filter { it.isNotEmpty() }
        return Roster(peer, RecordFile.field(f, 2), heroes)
    }

    private companion object {
        const val MAX_ROSTERS = 1_000
    }
}
