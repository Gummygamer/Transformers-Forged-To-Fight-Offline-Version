package com.gummygamer.tftfpvphost.state

import java.io.File
import java.util.concurrent.Callable
import java.util.concurrent.Executors
import java.util.concurrent.TimeUnit
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotEquals
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test

class PvpStoreTest {
    private lateinit var dir: File

    @Before
    fun setUp() {
        dir = kotlin.io.path.createTempDirectory("pvpstore").toFile()
    }

    @After
    fun tearDown() {
        dir.deleteRecursively()
    }

    private fun store(ttl: Long = 15_000L) = PvpStore(dir, presenceTtlMs = ttl)

    @Test
    fun presenceExpiresExactlyAtTtlBoundary() {
        val s = store()
        s.touchPresenceAt("alice", "Alice", 1_000)
        assertEquals(1, s.livePeersAt(1_000 + 14_999).size)
        assertEquals(0, s.livePeersAt(1_000 + 15_000).size)
        assertEquals(0, s.livePeersAt(999).size)
    }

    @Test
    fun ttlIsOverridableWithPositiveValueOnly() {
        val s = store()
        s.touchPresenceAt("alice", "Alice", 0)
        assertFalse(s.setPresenceTtlMs(0))
        assertFalse(s.setPresenceTtlMs(-5))
        assertEquals(15_000L, s.presenceTtlMs)
        assertTrue(s.setPresenceTtlMs(100))
        assertEquals(1, s.livePeersAt(99).size)
        assertEquals(0, s.livePeersAt(100).size)
    }

    @Test
    fun presenceNameFallsBackToPreviousThenSessionThenCommander() {
        val s = store()
        val session = s.ensureSession("dev-1", "Zed")
        assertEquals("Zed", s.touchPresenceAt(session.stoken, "", 10).name)
        assertEquals("Commander", s.touchPresenceAt("anon", "", 10).name)
        s.touchPresenceAt("anon", "Named", 20)
        assertEquals("Named", s.touchPresenceAt("anon", "", 30).name)
    }

    @Test
    fun twoLivePeersPairIntoOneSharedMatch() {
        val s = store()
        s.touchPresenceAt("bob", "Bob", 1_000)
        val a = s.pairPeersAt("alice", "arena_versus", 2_000)
        assertNotNull(a)
        assertEquals("arena_versus-2000", a!!.matchId)
        assertEquals("bob", a.opponentOf("alice"))
        assertEquals(a, s.pairPeersAt("bob", "arena_versus", 2_500))
        assertEquals(a, s.matchForPeer("bob"))
        assertEquals(a, s.pairPeersAt("alice", "arena_versus", 3_000))
    }

    @Test
    fun thirdPeerDoesNotStealAPairAndLonePeerGetsNothing() {
        val s = store()
        assertNull(s.pairPeersAt("alice", "arena", 1_000))
        s.touchPresenceAt("bob", "Bob", 1_100)
        assertNotNull(s.pairPeersAt("alice", "arena", 1_200))
        s.touchPresenceAt("carol", "Carol", 1_300)
        assertNull(s.pairPeersAt("carol", "arena", 1_300))
    }

    @Test
    fun expiredPeerIsNotPaired() {
        val s = store()
        s.touchPresenceAt("bob", "Bob", 0)
        assertNull(s.pairPeersAt("alice", "arena", 15_000))
    }

    @Test
    fun identicalTimestampsGetCollisionSuffixedMatchIds() {
        val s = store()
        for (name in listOf("a", "b", "c", "d")) s.touchPresenceAt(name, name, 5_000)
        val first = s.pairPeersAt("a", "arena", 5_000)!!
        val second = s.pairPeersAt("c", "arena", 5_000)!!
        assertEquals("arena-5000", first.matchId)
        assertEquals("arena-5000-1", second.matchId)
        assertEquals(setOf("a", "b"), setOf(first.peerA, first.peerB))
        assertEquals(setOf("c", "d"), setOf(second.peerA, second.peerB))
    }

    @Test
    fun pairingPicksFreshestPeerAndEarlierRowOnTie() {
        val s = store()
        s.touchPresenceAt("old", "Old", 1_000)
        s.touchPresenceAt("new", "New", 2_000)
        assertEquals("new", s.pairPeersAt("me", "arena", 2_100)!!.peerB)
        val t = PvpStore(File(dir, "tie"))
        t.touchPresenceAt("x", "X", 1_000)
        t.touchPresenceAt("y", "Y", 1_000)
        assertEquals("x", t.pairPeersAt("me", "arena", 1_100)!!.peerB)
    }

    @Test
    fun repeatRosterSaveReplacesInsteadOfAppending() {
        val s = store()
        assertTrue(s.storeRoster("alice", "Alice", listOf("h1", "h2")))
        s.storeRoster("bob", "Bob", listOf("h3"))
        s.storeRoster("alice", "Alice2", listOf("h4"))
        assertEquals(listOf("h4"), s.rosterFor("alice")!!.heroes)
        assertEquals("Alice2", s.rosterFor("alice")!!.name)
        assertEquals(2, File(dir, "rosters").readLines().size)
        assertEquals("alice", s.opponentRoster("bob").peer)
    }

    @Test
    fun emptySquadNeverErasesStoredRosterAndHouseTeamIsTheFallback() {
        val s = store()
        assertEquals("Autobot Garrison", s.opponentRoster("alice").name)
        s.storeRoster("alice", "Alice", listOf("h1"))
        assertFalse(s.storeRoster("alice", "Alice", emptyList()))
        assertEquals(listOf("h1"), s.rosterFor("alice")!!.heroes)
        assertEquals("Autobot Garrison", s.opponentRoster("alice").name)
    }

    @Test
    fun repeatLoginReturnsStableUrlSafeToken() {
        val s = store()
        val first = s.ensureSession("udid-A/b c|d", "Dev")
        assertEquals(first, s.ensureSession("udid-A/b c|d", "Other"))
        assertTrue(first.stoken.matches(Regex("[A-Za-z0-9]+")))
        assertTrue(first.stoken.length <= 32)
        assertEquals("1000000000001", first.uid)
        assertEquals("1000000000002", s.ensureSession("second", "").uid)
    }

    @Test
    fun tokensThatCollideAreDisambiguated() {
        val s = store()
        val a = s.ensureSession("a-b", "")
        val b = s.ensureSession("ab", "")
        assertNotEquals(a.stoken, b.stoken)
        assertTrue(b.stoken.matches(Regex("[A-Za-z0-9]+")))
        val long1 = s.ensureSession("x".repeat(40) + "1", "")
        val long2 = s.ensureSession("x".repeat(40) + "2", "")
        assertNotEquals(long1.stoken, long2.stoken)
        assertTrue(long2.stoken.length <= 32)
    }

    private fun pairedStore(): Pair<PvpStore, Match> {
        val s = store()
        s.touchPresenceAt("bob", "Bob", 100)
        return s to s.pairPeersAt("alice", "arena", 200)!!
    }

    @Test
    fun fightEventsAreOneBasedAscendingAndMemberOnly() {
        val (s, m) = pairedStore()
        assertEquals(1L, s.nextFightSeq(m.matchId))
        val e1 = (s.postFightEventAt(m.matchId, "alice", "move", "{\"x\":1,\"y\":2}", 300) as FightPost.Accepted).event
        val e2 = (s.postFightEventAt(m.matchId, "bob", "hit|x", "p", 310) as FightPost.Accepted).event
        assertEquals(listOf(1L, 2L), listOf(e1.seq, e2.seq))
        assertEquals("{\"x\":1,\"y\":2}", e1.payload)
        assertEquals("hit_x", e2.kind)
        assertEquals(listOf(2L), s.pollFightEvents(m.matchId, "alice", 1)!!.map { it.seq })
        assertEquals(FightPost.NotMember, s.postFightEventAt(m.matchId, "mallory", "k", "p", 320))
        assertNull(s.pollFightEvents(m.matchId, "mallory", 0))
        assertEquals(FightPost.NotMember, s.postFightEventAt("nope", "alice", "k", "p", 320))
        assertEquals(FightPost.TooLarge, s.postFightEventAt(m.matchId, "alice", "k", "x".repeat(40_000), 330))
    }

    @Test
    fun agreedResultEndsMatchPurgesEventsButKeepsReports() {
        val (s, m) = pairedStore()
        s.postFightEventAt(m.matchId, "alice", "k", "p", 300)
        assertEquals("pending", s.reportResultAt(m.matchId, "alice", "win", 400).status)
        val r = s.reportResultAt(m.matchId, "bob", "DEFEAT", 410)
        assertEquals(Reconciliation(m.matchId, "agreed", "alice", "bob", 2), r)
        assertNull(s.matchForPeer("alice"))
        assertNull(s.matchForPeer("bob"))
        assertEquals(1L, s.nextFightSeq(m.matchId))
        assertEquals(2, s.reports().size)
        assertEquals(r, s.reconcileMatch(m.matchId))
    }

    @Test
    fun drawsAgreeWithNoWinner() {
        val (s, m) = pairedStore()
        s.reportResultAt(m.matchId, "alice", "tie", 400)
        assertEquals(Reconciliation(m.matchId, "agreed", "", "", 2), s.reportResultAt(m.matchId, "bob", "DRAW", 401))
    }

    @Test
    fun repeatReportReplacesThePeersEarlierOne() {
        val (s, m) = pairedStore()
        s.reportResultAt(m.matchId, "alice", "LOST", 400)
        s.reportResultAt(m.matchId, "alice", "WON", 410)
        assertEquals(1, s.reports().size)
        assertEquals("pending", s.reconcileMatch(m.matchId).status)
        assertEquals(1, s.reconcileMatch(m.matchId).reports)
    }

    @Test
    fun disputeIsSettledByEarliestReportThenLexicographicPeer() {
        val (s, m) = pairedStore()
        s.reportResultAt(m.matchId, "bob", "WON", 500)
        val r = s.reportResultAt(m.matchId, "alice", "WON", 600)
        assertEquals(Reconciliation(m.matchId, "disputed", "bob", "alice", 2), r)
        assertNull(s.matchForPeer("alice"))

        val (s2, m2) = pairedStore2()
        s2.reportResultAt(m2.matchId, "bob", "LOST", 500)
        assertEquals("bob", s2.reportResultAt(m2.matchId, "alice", "LOST", 700).loser)

        val (s3, m3) = pairedStore2()
        s3.reportResultAt(m3.matchId, "bob", "WON", 500)
        val tie = s3.reportResultAt(m3.matchId, "alice", "WON", 500)
        assertEquals("alice", tie.winner)
        assertEquals("disputed", tie.status)
    }

    @Test
    fun disputeWithUnknownDeciderHasNoWinner() {
        val (s, m) = pairedStore()
        s.reportResultAt(m.matchId, "alice", "??", 500)
        val r = s.reportResultAt(m.matchId, "bob", "WON", 600)
        assertEquals("disputed", r.status)
        assertEquals("", r.winner)
    }

    private fun pairedStore2(): Pair<PvpStore, Match> {
        val s = PvpStore(kotlin.io.path.createTempDirectory(dir.toPath(), "second").toFile())
        s.touchPresenceAt("bob", "Bob", 100)
        return s to s.pairPeersAt("alice", "arena", 200)!!
    }

    @Test
    fun stateSurvivesRestart() {
        val (s, m) = pairedStore()
        s.storeRoster("alice", "Alice", listOf("h1"))
        s.postFightEventAt(m.matchId, "alice", "k", "p", 300)
        val session = s.ensureSession("dev", "Dev")
        s.reportResultAt(m.matchId, "alice", "WON", 400)
        val again = store()
        assertEquals(m, again.matchForPeer("bob"))
        assertEquals(listOf("h1"), again.rosterFor("alice")!!.heroes)
        assertEquals(2L, again.nextFightSeq(m.matchId))
        assertEquals(session, again.ensureSession("dev", ""))
        assertEquals(1, again.reports().size)
        assertEquals(2, again.livePeersAt(300).size)
    }

    @Test
    fun malformedRowsAreDroppedAndNumbersDefaultToZero() {
        File(dir, "presence").writeText("X|junk|1\nP||nameless|5\nP|ok|Ok|notanumber\n\nP|fine|Fine|10")
        File(dir, "matches").writeText("M|id|arena||b|1")
        val s = store()
        assertEquals(listOf("ok", "fine"), s.livePeersAt(11).map { it.peer })
        assertEquals(0, s.activeMatches().size)
    }

    @Test
    fun concurrentPairingProducesConsistentMatches() {
        val s = store()
        val names = (1..20).map { "p$it" }
        names.forEach { s.touchPresenceAt(it, it, 1_000) }
        val pool = Executors.newFixedThreadPool(8)
        val results = pool.invokeAll(names.map { n -> Callable { s.pairPeersAt(n, "arena", 1_000) } })
        pool.shutdown()
        assertTrue(pool.awaitTermination(30, TimeUnit.SECONDS))
        val matches = s.activeMatches()
        val members = matches.flatMap { listOf(it.peerA, it.peerB) }
        assertEquals(members.size, members.toSet().size)
        assertEquals(matches.size, matches.map { it.matchId }.toSet().size)
        assertEquals(10, matches.size)
        assertTrue(results.all { it.get() != null })
    }

    @Test
    fun concurrentRosterAndSessionWritesDoNotCorruptStores() {
        val s = store()
        val pool = Executors.newFixedThreadPool(8)
        val tasks = (1..40).map { i ->
            Callable {
                s.ensureSession("dev$i", "N$i")
                s.storeRoster("peer${i % 5}", "N", listOf("h$i"))
                s.touchPresenceAt("peer${i % 5}", "N", i.toLong())
            }
        }
        pool.invokeAll(tasks).forEach { it.get() }
        pool.shutdown()
        assertEquals(40, File(dir, "sessions").readLines().size)
        assertEquals(5, File(dir, "rosters").readLines().size)
        assertEquals(5, s.livePeersAt(40).size)
    }

    @Test
    fun snapshotAndResetAll() {
        val (s, m) = pairedStore()
        s.reportResultAt(m.matchId, "alice", "WON", 250)
        val snap = s.snapshotAt(300)
        assertEquals(2, snap.peers.size)
        assertEquals(listOf(m.matchId), snap.results.map { it.matchId })
        s.resetAll()
        assertEquals(0, s.snapshotAt(300).peers.size)
        assertEquals(0, s.reports().size)
        assertNull(s.matchForPeer("alice"))
        s.touchPresenceAt("alice", "Alice", 400)
        assertEquals(1, s.livePeersAt(401).size)
    }
}
