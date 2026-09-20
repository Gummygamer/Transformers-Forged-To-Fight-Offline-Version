package com.gummygamer.tftfpvphost.server

import java.util.zip.CRC32
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * Self-check for the synthetic blob builder every payload test depends on: it must emit a
 * byte-exact TFTFPAY v1 blob (contract section 1.1/1.2), otherwise the reader tests would be
 * assertions about a format no real host produces.
 *
 * Also pins the integer-handling of the validator, which guards a listener bound to every
 * interface: header and record fields are unsigned 32-bit, so a hostile value must be
 * rejected by range checks rather than reaching a signed size or offset computation.
 */
class PayloadBuilderTest {
    private val exact = linkedMapOf(
        "GET /a" to "{\"a\":1}",
        "POST /a" to "{\"a\":2}",
        "@hero:x:3:7" to "{\"bid\":\"x\"}",
    )
    private val prefixes = listOf("/rule/" to "{\"rule\":1}")

    private val bytes: ByteArray = PayloadBuilder.build(exact, prefixes)

    @Test
    fun syntheticBlobCarriesTheDocumentedHeaderFields() {
        assertNotNull("the builder must emit a blob the reader accepts", payload())
        assertArrayEquals(
            byteArrayOf('T'.code.toByte(), 'F'.code.toByte(), 'T'.code.toByte(), 'F'.code.toByte(),
                'P'.code.toByte(), 'A'.code.toByte(), 'Y'.code.toByte(), 0),
            bytes.copyOfRange(0, 8))
        assertEquals(1L, u32(8))                                          // version
        assertEquals(bytes.size.toLong(), u32(12))                         // total == length
        assertEquals(8080L, u32(16))                                       // built-for port
        assertEquals(3L, u32(20))                                          // exact count
        assertEquals(Payload.HEADER.toLong(), u32(24))                     // exact table offset
        assertEquals(1L, u32(28))                                          // prefix count
        assertEquals((Payload.HEADER + 3 * Payload.REC).toLong(), u32(32)) // prefix table offset
        val crc = CRC32().apply { update(bytes, Payload.HEADER, bytes.size - Payload.HEADER) }.value
        assertEquals(crc, u32(44))                                        // CRC-32 over [64:total]
        for (offset in 48..60 step 4) assertEquals("reserved word at $offset", 0L, u32(offset))
    }

    @Test
    fun everyRecordValueIsAlignedNulTerminatedAndNulPadded() {
        val values = ArrayList<Pair<Int, Int>>()
        for (record in 0 until 4) { // 3 exact + 1 prefix record
            val rec = Payload.HEADER + record * Payload.REC
            values.add(u32(rec).toInt() to u32(rec + 4).toInt())
            values.add(u32(rec + 8).toInt() to u32(rec + 12).toInt())
        }
        values.add(u32(36).toInt() to u32(40).toInt()) // the default body
        for ((offset, length) in values) {
            assertEquals("value at $offset must be 4-byte aligned", 0, offset % 4)
            assertTrue("value at $offset must fit in the blob", offset + length < bytes.size)
            assertEquals("value at $offset must carry a NUL terminator", 0, bytes[offset + length].toInt())
            for (fill in (offset + length + 1) until ((offset + length + 4) and 3.inv())) {
                assertEquals("padding after the value at $offset", 0, bytes[fill].toInt())
            }
        }
        assertArrayEquals(PayloadBuilder.DEFAULT_BODY.toByteArray(), payload()!!.defaultBody())
    }

    @Test
    fun exactKeysAreStoredStrictlyAscendingByRawBytes() {
        val keys = (0 until 3).map { record ->
            val rec = Payload.HEADER + record * Payload.REC
            val off = u32(rec).toInt()
            bytes.copyOfRange(off, off + u32(rec + 4).toInt())
        }
        // '@' (0x40) sorts after the ASCII letters only in the sense that it is above
        // space and punctuation; what matters is that the table is ordered by raw unsigned
        // bytes, because the reader's binary search compares exactly that way.
        assertEquals(listOf("@hero:x:3:7", "GET /a", "POST /a"), keys.map { String(it) })
        for (i in 1 until keys.size) assertTrue(rawCompare(keys[i - 1], keys[i]) < 0)
    }

    @Test
    fun unsignedRecordOffsetsAreRejectedWithoutAllocating() {
        // 0xFFFFFFFF is -1 as a signed int; a reader that used it as an array bound or size
        // would throw inside a request-handling thread.
        val hostile = bytes.patched(72 to 0xFFFFFFFFL, 76 to 0xFFFFFFFFL) // first exact body
        assertEquals(-4, codeOf(hostile, "a hostile record range must be rejected"))
    }

    @Test
    fun unsignedDefaultLengthCannotReadPastTheBlob() {
        // total stays correct, so this is not a "truncated file" case; only the declared
        // default-body range is hostile.
        val hostile = bytes.patched(40 to 0x7FFFFFFFL)
        assertEquals(-3, codeOf(hostile, "a default-body range past the end must be rejected"))
    }

    @Test
    fun hugeTableCountsCannotOverflowTheRangeCheck() {
        assertEquals(-3, codeOf(bytes.patched(20 to 0xFFFFFFFFL), "an unschedulable exact count"))
        assertEquals(-3, codeOf(bytes.patched(28 to 0xFFFFFFFFL), "an unschedulable prefix count"))
    }

    @Test
    fun outOfRangePortIsRejected() {
        assertEquals(-3, codeOf(bytes.patched(16 to 0L), "port 0 carries no usable listen port"))
        assertEquals(-3, codeOf(bytes.patched(16 to 70000L), "a port above 65535 must be rejected"))
    }

    @Test
    fun aBlobShorterThanTheHeaderIsRejectedAsCodeMinusOne() {
        assertEquals(-1, codeOf(bytes.copyOfRange(0, Payload.HEADER - 1), "a sub-header blob"))
    }

    private fun payload(): Payload? = (Payload.parse(bytes) as? PayloadResult.Loaded)?.payload

    private fun u32(offset: Int): Long = Payload.readU32(bytes, offset)

    private fun codeOf(target: ByteArray, message: String): Int {
        val result = Payload.parse(target)
        assertTrue("$message; got $result", result is PayloadResult.Invalid)
        return (result as PayloadResult.Invalid).code
    }

    private fun rawCompare(a: ByteArray, b: ByteArray): Int {
        for (i in 0 until minOf(a.size, b.size)) {
            val diff = (a[i].toInt() and 0xff) - (b[i].toInt() and 0xff)
            if (diff != 0) return diff
        }
        return a.size.compareTo(b.size)
    }
}

/**
 * Overwrites little-endian u32 words in a copy of the blob and refreshes the CRC, so the
 * reader reaches its later validation stages instead of stopping at a stale checksum.
 */
private fun ByteArray.patched(vararg words: Pair<Int, Long>): ByteArray {
    val copy = copyOf()
    for ((offset, value) in words) {
        for (k in 0 until 4) copy[offset + k] = ((value shr (8 * k)) and 0xff).toByte()
    }
    val crc = CRC32().apply { update(copy, Payload.HEADER, copy.size - Payload.HEADER) }.value
    for (k in 0 until 4) copy[44 + k] = (((crc and 0xffffffffL) shr (8 * k)) and 0xff).toByte()
    return copy
}
