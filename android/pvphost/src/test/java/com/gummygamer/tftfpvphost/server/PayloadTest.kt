package com.gummygamer.tftfpvphost.server

import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class PayloadTest {
    private val blob = PayloadBuilder.build(
        exact = mapOf(
            "GET /a" to "{\"a\":1}",
            "POST /a" to "{\"a\":2}",
            "@hero:*:1:1" to "wild",
            "@hero:x:1:1" to "x11",
            "@hero:x:2:5" to "x25",
        ),
        prefixes = listOf("/auto/" to "first", "/auto/deep" to "second"),
    )

    private fun load(bytes: ByteArray): Payload =
        (Payload.parse(bytes) as PayloadResult.Loaded).payload

    private fun invalidCode(bytes: ByteArray): Int = (Payload.parse(bytes) as PayloadResult.Invalid).code

    @Test
    fun exactLookupFindsEveryKeyAndMissesUnknown() {
        val payload = load(blob)
        assertEquals("{\"a\":1}", String(payload.lookup("GET /a")!!))
        assertEquals("{\"a\":2}", String(payload.lookup("POST /a")!!))
        assertNull(payload.lookup("GET /b"))
        assertNull(payload.lookup("GET /"))
    }

    @Test
    fun prefixRulesAreScannedInStoredOrder() {
        val payload = load(blob)
        assertEquals("first", String(payload.prefixBody("/auto/deep/x")!!))
        assertNull(payload.prefixBody("/other"))
    }

    @Test
    fun defaultBodyIsTheEmptyEnvelope() {
        assertArrayEquals(PayloadBuilder.DEFAULT_BODY.toByteArray(), load(blob).defaultBody())
    }

    @Test
    fun heroFallbackChainWalksExactThenBaseThenWildcard() {
        val payload = load(blob)
        assertEquals("x25", String(HeroKeys.heroBody(payload, "x", 2, 5)!!))
        assertEquals("x11", String(HeroKeys.heroBody(payload, "x", 4, 9)!!))
        assertEquals("wild", String(HeroKeys.heroBody(payload, "unknown", 1, 1)!!))
        assertEquals("x11", String(HeroKeys.heroBody(payload, "x", 0, 0)!!))
        assertEquals("x11", String(HeroKeys.heroBody(payload, "x", 1, 99)!!))
    }

    @Test
    fun rejectsBadMagicVersionAndSize() {
        val badMagic = blob.copyOf().also { it[0] = 0 }
        assertEquals(-1, invalidCode(badMagic))
        val badVersion = blob.copyOf().also { it[8] = 2 }
        assertEquals(-1, invalidCode(badVersion))
        assertEquals(-1, invalidCode(blob.copyOf(blob.size - 4)))
        assertEquals(-1, invalidCode(ByteArray(10)))
    }

    @Test
    fun rejectsCorruptedBodyByCrc() {
        val corrupt = blob.copyOf().also { it[blob.size - 8] = (it[blob.size - 8] + 1).toByte() }
        val result = Payload.parse(corrupt) as PayloadResult.Invalid
        assertEquals(-2, result.code)
        assertTrue(result.detail.contains("CRC"))
    }

    @Test
    fun rejectsNonZeroReservedWords() {
        assertEquals(-2, invalidCode(blob.copyOf().also { it[48] = 1 }))
    }
}
