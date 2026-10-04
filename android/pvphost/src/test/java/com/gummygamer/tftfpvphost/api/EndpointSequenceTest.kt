package com.gummygamer.tftfpvphost.api

import com.gummygamer.tftfpvphost.json.JsonArr
import com.gummygamer.tftfpvphost.json.JsonBool
import com.gummygamer.tftfpvphost.json.JsonInt
import com.gummygamer.tftfpvphost.json.JsonObj
import com.gummygamer.tftfpvphost.json.JsonParse
import com.gummygamer.tftfpvphost.json.JsonText
import com.gummygamer.tftfpvphost.json.JsonValue
import com.gummygamer.tftfpvphost.server.HostConfig
import com.gummygamer.tftfpvphost.server.HostHttpServer
import com.gummygamer.tftfpvphost.server.Payload
import com.gummygamer.tftfpvphost.server.PayloadBuilder
import com.gummygamer.tftfpvphost.server.PayloadResult
import com.gummygamer.tftfpvphost.server.RouteResolver
import com.gummygamer.tftfpvphost.server.ServerLog
import com.gummygamer.tftfpvphost.state.Clock
import com.gummygamer.tftfpvphost.state.PvpStore
import java.io.ByteArrayOutputStream
import java.io.File
import java.net.Socket
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotEquals
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test

/** Drives the dynamic layer over real HTTP against a small synthetic payload and a fake clock. */
class EndpointSequenceTest {
    private class FakeClock(var now: Long = 1_000_000L) : Clock {
        override fun nowMs(): Long = now
    }

    private val clock = FakeClock()
    private lateinit var dir: File
    private lateinit var store: PvpStore
    private lateinit var server: HostHttpServer
    private var port = 0

    @Before
    fun setUp() {
        dir = createTempDir("pvphost-test")
        val payload = (Payload.parse(blob()) as PayloadResult.Loaded).payload
        val game = GameData(payload)
        val config = HostConfig()
        store = PvpStore(dir, clock, game.defaultTeam, config.presenceTtlMs)
        val router = DynamicRouter(store, game, config, clock)
        server = HostHttpServer(0, RouteResolver(payload, config, router), ServerLog())
        server.start()
        port = server.boundPort
    }

    @After
    fun tearDown() {
        server.stop()
        dir.deleteRecursively()
    }

    private fun blob(): ByteArray {
        val hero = { bid: String -> "{\"entity_type\":\"bot\",\"bid\":\"$bid\",\"rank\":1,\"level\":1,\"mana_gain\":1.0}" }
        val exact = mutableMapOf(
            "@roster" to "hero_a\nhero_b\nhero_c\nhero_d\nhero_e\nhero_f\nhero_g\n",
            "@team:default" to "hero_a\nhero_b\nhero_c",
            "@savedteam:template" to "{\"error\":null,\"result\":{\"updates\":{\"savedTeams\":[{\"sid\":\"%TID%\"," +
                "\"heroes\":%STEAM%}],\"activeTeams\":[{\"aid\":\"1.1.1-%TID%\",\"heroes\":%ATEAM%}]},\"deletes\":{}}}",
            "@userdata:template" to "{\"error\":null,\"result\":{\"saved\":%STEAM%,\"active\":%ATEAM%}}",
            "@userprofile:template" to "{\"error\":null,\"result\":{\"uid\":\"%UID%\",\"name\":\"Commander\",\"lastLogin\":%NOW%}}",
            "@grouprefresh:" to "{\"error\":null,\"result\":{\"updates\":[]}}",
            "@grouprefresh:missionsconfig" to "{\"error\":null,\"result\":{\"updates\":[{\"name\":\"missionsconfig\"}]}}",
            "@tutorial:blocked" to "ShieldTutorial",
            "@tutorial:live" to "FTE",
            "@tutorial:started" to "{\"error\":null,\"result\":{\"%TID%\":{\"current_bid\":\"%BID%\"}}}",
            "@tutorial:started-nobid" to "{\"error\":null,\"result\":{\"%TID%\":{\"current_bid\":\"\"}}}",
            "@tutorial:completed" to "{\"error\":null,\"result\":{\"%TID%\":{\"s\":\"completed\",\"branch\":\"%BID%\"}}}",
            "@tutorial:completed-nobid" to "{\"error\":null,\"result\":{\"%TID%\":{\"s\":\"completed\"}}}",
            "@tutorial:error" to "{\"err\":\"unavailable\",\"result\":{}}",
            "GET /pvp/get-login-data" to "{\"error\":null,\"result\":{\"pvpUserData\":[],\"pvpMatchData\":[]}}",
        )
        for (bid in listOf("hero_a", "hero_b", "hero_c", "hero_d", "hero_e", "hero_f", "hero_g")) {
            exact["@savedteam:hero:$bid"] = hero(bid)
            exact["@hero:$bid:1:1"] = "{\"bid\":\"$bid\",\"sig_lvl\":%SIG%,\"attack\":5}"
        }
        return PayloadBuilder.build(exact)
    }

    private fun call(path: String, body: String = "", method: String = "POST"): String {
        Socket("127.0.0.1", port).use { socket ->
            socket.soTimeout = 5000
            val request = "$method $path HTTP/1.1\r\nHost: x\r\nConnection: close\r\n" +
                "Content-Length: ${body.length}\r\n\r\n$body"
            socket.getOutputStream().write(request.toByteArray(Charsets.ISO_8859_1))
            val out = ByteArrayOutputStream()
            socket.getInputStream().copyTo(out)
            return out.toString("ISO-8859-1").substringAfter("\r\n\r\n")
        }
    }

    private fun json(path: String, body: String = "", method: String = "POST"): JsonObj {
        val text = call(path, body, method)
        val parsed = JsonParse.parse(text)
        assertTrue("not a JSON object: $text", parsed is JsonObj)
        return parsed as JsonObj
    }

    private fun JsonValue?.obj(key: String): JsonValue? = (this as? JsonObj)?.get(key)

    private fun JsonValue?.text(key: String): String = (obj(key) as JsonText).value

    private fun JsonValue?.list(key: String): List<JsonValue> = (obj(key) as JsonArr).items

    private fun result(response: JsonObj): JsonValue {
        assertEquals("error must be null", com.gummygamer.tftfpvphost.json.JsonNull, response.get("error"))
        return response.get("result")!!
    }

    private fun saveTeam(peer: String, vararg heroes: String) {
        val list = heroes.joinToString(",") { "\"$it\"" }
        call("/bcg/setSavedTeam?peer=$peer", "{\"teamID\":\"PVP1\",\"name\":\"$peer-name\",\"heroes\":[$list]}")
    }

    @Test
    fun loginSaveTeamHeartbeatLobbySequence() {
        val login = json("/auth/login", "{\"credentials\":{\"udid\":\"dev-1\",\"deviceName\":\"Pixel\"},\"bid\":\"b\"}")
        val user = result(login)
        val token = user.text("stoken")
        assertTrue(token.startsWith("sess"))
        assertEquals("prod", user.text("server_tag"))
        assertEquals("Pixel", user.obj("user").text("name"))
        assertEquals(token, result(json("/auth/login", "{\"credentials\":{\"udid\":\"dev-1\"}}")).text("stoken"))

        val saved = call("/bcg/setSavedTeam?stoken=$token", "{\"teamID\":\"PVP1\",\"heroes\":[\"hero_a\",\"hero_b\"]}")
        assertTrue(saved, saved.contains("\"sid\": \"PVP1\""))
        val activeId = JsonParse.parse(saved).obj("result").obj("updates").list("activeTeams")[0].text("aid")
        assertEquals("arena_versus", activeId)
        val story = call("/bcg/setSavedTeam?stoken=$token", "{\"teamID\":\"0\",\"heroes\":[\"hero_a\",\"hero_b\"]}")
        assertTrue(story, story.contains("\"aid\": \"1.1.1-0\""))
        val activeHeroes = (JsonParse.parse(saved).obj("result").obj("updates").list("activeTeams")[0]).obj("heroes")
        assertEquals(setOf("hero_a", "hero_b"), (activeHeroes as JsonObj).pairs.map { it.first }.toSet())
        assertEquals(listOf("hero_a", "hero_b"), store.rosterFor(token)!!.heroes)

        val beat = result(json("/pvp/heartbeat?stoken=$token", "{}"))
        assertEquals(token, beat.text("peer"))
        assertEquals(0, beat.list("peers").size)
        val lobby = result(json("/pvp/lobby?stoken=$token", "", "GET"))
        assertEquals(1, lobby.list("peers").size)
        assertEquals("Pixel", lobby.list("peers")[0].text("name"))
        assertEquals(15000L, (lobby.obj("ttl") as JsonInt).value)
        assertEquals("", lobby.text("match"))
    }

    @Test
    fun getLoginDataTouchesPresenceThenServesCannedBody() {
        val body = call("/pvp/get-login-data?peer=alice", "", "GET")
        assertEquals("{\"error\":null,\"result\":{\"pvpUserData\":[],\"pvpMatchData\":[]}}", body)
        assertEquals(listOf("alice"), store.livePeers().map { it.peer })
    }

    @Test
    fun bootEndpointsArePerPeerAndPayloadBacked() {
        val login = result(json("/auth/login", "{\"credentials\":{\"udid\":\"boot-a\",\"deviceName\":\"Tablet\"}}"))
        val token = login.text("stoken")
        saveTeam(token, "hero_a", "hero_b")
        val userData = call("/bcg/getUserData?stoken=$token", "", "GET")
        assertTrue(userData.contains("hero_a"))
        val profile = call("/userprofile/?stoken=$token", "", "POST")
        assertTrue(profile.contains("\"name\":\"Tablet\""))
        assertTrue(call("/autorefresh/grouprefresh?groups.0.name=other", "", "GET").contains("\"updates\":[]"))
        assertTrue(call("/autorefresh/grouprefresh?groups.0.name=missionsconfig", "", "GET").contains("missionsconfig"))
        val first = call("/tutorial/get-login-data?peer=boot-a", "", "GET")
        val second = call("/tutorial/get-login-data?peer=boot-a", "", "GET")
        // The host serves no STORY data, so the intro quest is never offered, even on first login.
        assertTrue(first.contains("FTEComplete"))
        assertTrue(!first.contains("FTEIntroQuest"))
        assertTrue(second.contains("FTEComplete"))
        assertTrue(call("/tutorial/start-tutorial?peer=boot-a", "{\"tid\":\"FTE\",\"bid\":\"Intro\"}").contains("Intro"))
        val heroes = call("/bcg/getBaseHeroData?peer=boot-a", "{\"heroes\":[{\"bid\":\"hero_a\",\"rank\":1,\"level\":1}]}")
        assertTrue(heroes.contains("\"bid\": \"hero_a\""))
    }

    @Test
    fun invalidSquadNeverOverwritesTheLastValidRoster() {
        saveTeam("alice", "hero_a", "hero_b")
        saveTeam("alice", "not_a_hero")
        call("/bcg/setSavedTeam?peer=alice", "{\"heroes\":[]}")
        assertEquals(listOf("hero_a", "hero_b"), store.rosterFor("alice")!!.heroes)
    }

    @Test
    fun pairedPeersShareAMatchAndAreServedEachOthersTeam() {
        saveTeam("alice", "hero_a", "hero_b")
        saveTeam("bob", "hero_d", "hero_e", "hero_f")
        val first = result(json("/pvp/lock-in?peer=alice", "{\"name\":\"alice-name\",\"heroes\":[\"hero_a\",\"hero_b\"]}"))
        assertEquals("", first.text("matchID"))
        assertEquals(JsonBool(false), first.obj("live"))
        clock.now += 1000
        val second = result(json("/pvp/lock-in?peer=bob", "{\"name\":\"bob-name\",\"heroes\":[\"hero_d\",\"hero_e\",\"hero_f\"]}"))
        val matchId = second.text("matchID")
        assertTrue(matchId.startsWith("arena_versus-"))
        assertEquals(JsonBool(true), second.obj("locked"))
        assertEquals("", second.text("error"))

        val aliceView = result(json("/pvp/find-arena-opponent?peer=alice", "{}"))
        val match = aliceView.list("pvpMatchData")[0]
        assertEquals(matchId, match.text("matchID"))
        val node = match.list("opponents")[0]
        assertEquals(listOf("hero_d", "hero_e", "hero_f"), node.list("team").map { it.text("bid") })
        assertEquals("bob-name", node.obj("userData").text("name"))
        assertEquals(3, node.list("matchups").size)
        assertEquals(1, aliceView.list("pvpUserData").size)

        val bobView = result(json("/pvp/get-opponents?peer=bob", "{}"))
        assertEquals(matchId, bobView.list("pvpMatchData")[0].text("matchID"))
        assertEquals("alice-name", bobView.list("pvpMatchData")[0].list("opponents")[0].obj("userData").text("name"))
    }

    @Test
    fun opponentFallsBackToTheHouseTeamWhenNobodyElseHasARoster() {
        saveTeam("alice", "hero_a")
        val view = result(json("/pvp/get-new-arena-opponent?peer=alice", "{}"))
        val match = view.list("pvpMatchData")[0]
        assertEquals("", match.text("matchID"))
        val node = match.list("opponents")[0]
        assertEquals("Autobot Garrison", node.obj("userData").text("name"))
        assertEquals(listOf("hero_a", "hero_b", "hero_c"), node.list("team").map { it.text("bid") })
    }

    @Test
    fun fightRelayUsesPerMatchSequenceAndRejectsNonMembers() {
        val matchId = pairAliceAndBob()
        val post = { peer: String, body: String -> result(json("/pvp/fight-post?peer=$peer", body)) }
        assertEquals(1L, (post("alice", "{\"kind\":\"tap\",\"payload\":\"x\"}").obj("seq") as JsonInt).value)
        assertEquals(2L, (post("bob", "{\"payload\":\"y\"}").obj("seq") as JsonInt).value)
        val stranger = post("carol", "{\"matchID\":\"$matchId\",\"payload\":\"z\"}")
        assertEquals(JsonBool(false), stranger.obj("accepted"))
        assertEquals("peer is not in match", stranger.text("error"))
        assertEquals(0L, (stranger.obj("seq") as JsonInt).value)
        assertEquals("no active match", post("carol", "{}").text("error"))

        val poll = result(json("/pvp/fight-poll?peer=bob&since=1", "{}"))
        assertEquals(matchId, poll.text("matchID"))
        assertEquals("alice", poll.text("opponent"))
        assertEquals(listOf(2L), poll.list("events").map { (it.obj("seq") as JsonInt).value })
        assertEquals(JsonBool(true), poll.list("events")[0].obj("mine"))
        assertEquals("input", poll.list("events")[0].text("kind"))
        val outsider = result(json("/pvp/fight-poll?peer=carol&matchID=$matchId", "{}"))
        assertEquals(0, outsider.list("events").size)
    }

    @Test
    fun matchIdResolvesFromBodyThenQueryThenActiveMatch() {
        val matchId = pairAliceAndBob()
        val viaBody = result(json("/pvp/match-result?peer=alice&matchID=ignored", "{\"matchID\":\"$matchId\"}"))
        assertEquals(matchId, viaBody.text("matchID"))
        assertEquals("ignored", result(json("/pvp/match-result?peer=alice&matchID=ignored", "{}")).text("matchID"))
        assertEquals(matchId, result(json("/pvp/match-result?peer=alice", "{}")).text("matchID"))
    }

    @Test
    fun twoSidedResultReconcilesIdenticallyForBothPeers() {
        val matchId = pairAliceAndBob()
        val pending = result(json("/pvp/report-result?peer=alice", "{\"result\":\"WIN\"}"))
        assertEquals("pending", pending.text("status"))
        assertEquals("WON", pending.text("outcome"))
        clock.now += 10
        val agreed = result(json("/pvp/report-result?peer=bob", "{\"outcome\":\"LOSS\"}"))
        assertEquals("agreed", agreed.text("status"))
        assertEquals("alice", agreed.text("winner"))
        assertEquals("bob", agreed.text("loser"))
        val fromAlice = result(json("/pvp/match-result?peer=alice&matchID=$matchId", "{}"))
        val fromBob = result(json("/pvp/match-result?peer=bob&matchID=$matchId", "{}"))
        assertEquals(listOf("agreed", "alice", "bob"), listOf("status", "winner", "loser").map { fromAlice.text(it) })
        assertEquals(listOf("agreed", "alice", "bob"), listOf("status", "winner", "loser").map { fromBob.text(it) })
        assertEquals(null, store.matchForPeer("alice"))
        val state = result(json("/pvp/get-active-pvp-data?peer=alice", "{}"))
        assertEquals(1L, (state.obj("wins") as JsonInt).value)
        assertEquals(1L, (state.obj("winStreak") as JsonInt).value)
    }

    @Test
    fun outsiderCannotRewriteAMatchResult() {
        val matchId = pairAliceAndBob()
        val forged = result(json("/pvp/report-result?peer=carol", "{\"matchID\":\"$matchId\",\"result\":\"WON\"}"))
        assertEquals("pending", forged.text("status"))
        assertEquals(0L, (forged.obj("reports") as JsonInt).value)
        assertTrue(store.reports().isEmpty())
    }

    @Test
    fun resolveMatchFeedsReconciliationAndFallsThroughToTheDefaultBody() {
        val matchId = pairAliceAndBob()
        val body = call("/matches/resolve-match/x?peer=alice", "{\"results\":{\"result\":\"WON\"}}")
        assertEquals(PayloadBuilder.DEFAULT_BODY, body)
        assertEquals("WON", store.reports().single { it.matchId == matchId }.outcome)
    }

    @Test
    fun quitAndLeaveEndTheMatch() {
        val matchId = pairAliceAndBob()
        val left = result(json("/pvp/leave-match?peer=alice", "{}"))
        assertEquals(JsonBool(true), left.obj("left"))
        assertEquals(JsonBool(false), result(json("/pvp/leave-match?peer=alice", "{}")).obj("left"))
        pairAliceAndBob()
        val quit = result(json("/pvp/quit-arena?peer=bob", "{}"))
        assertEquals(JsonBool(true), quit.obj("quit"))
        assertNotEquals("", quit.text("matchID"))
        assertNotEquals(matchId, "")
        assertEquals(null, store.matchForPeer("alice"))
    }

    @Test
    fun activateMatchCarriesMatchAttributesInResultAndAsyncPayload() {
        pairAliceAndBob()
        val response = json("/matches/activate-match/pvp_fight?peer=alice", "{}")
        val data = result(response)
        assertNotNull(data.obj("matchAttributes"))
        assertEquals("hero_a", data.obj("matchAttributes").obj("userHero").text("bid"))
        assertEquals("hero_d", data.obj("matchAttributes").obj("opponentHero").text("bid"))
        assertEquals(0L, (data.obj("matchAttributes").obj("userHero").obj("sig_lvl") as JsonInt).value)
        val async = (response.get("async") as JsonArr).items[0]
        assertEquals("PVPManager", async.text("component"))
        assertEquals("match-activated", async.text("message"))
        assertEquals(data, async.obj("payload"))
    }

    @Test
    fun presenceExpiresAtTheTtlBoundaryInTheLobby() {
        json("/pvp/heartbeat?peer=alice", "{}")
        clock.now += 14_999
        assertEquals(1, result(json("/pvp/lobby?peer=bob", "", "GET")).list("peers").size)
        clock.now += 1
        assertEquals(0, result(json("/pvp/lobby?peer=bob", "", "GET")).list("peers").size)
    }

    @Test
    fun matchLoadedAndSelectOpponentShapes() {
        val loaded = result(json("/pvp/match-loaded?peer=alice", "{\"fid\":\"f9\"}"))
        assertEquals("f9", loaded.text("fightID"))
        assertEquals(JsonBool(true), loaded.obj("loaded"))
        pairAliceAndBob()
        val selected = result(json("/pvp/select-opponent?peer=alice", "{\"index\":2}"))
        assertEquals("bob", selected.text("opponentID"))
        assertEquals(2L, (selected.obj("opponentIndex") as JsonInt).value)
        assertEquals(JsonBool(true), selected.obj("selected"))
        assertFalse(selected.text("matchID").isEmpty())
    }

    /** Both peers store a team, lock in, and end up paired; returns the shared match id. */
    private fun pairAliceAndBob(): String {
        saveTeam("alice", "hero_a", "hero_b")
        saveTeam("bob", "hero_d", "hero_e")
        json("/pvp/heartbeat?peer=alice", "{\"name\":\"alice-name\"}")
        clock.now += 5
        val locked = result(json("/pvp/lock-in?peer=bob", "{\"heroes\":[\"hero_d\",\"hero_e\"]}"))
        val matchId = locked.text("matchID")
        assertTrue("expected a pairing", matchId.isNotEmpty())
        assertEquals(matchId, store.matchForPeer("alice")!!.matchId)
        return matchId
    }
}
