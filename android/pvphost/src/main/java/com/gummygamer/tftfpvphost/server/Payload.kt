package com.gummygamer.tftfpvphost.server

import java.util.zip.CRC32

/** Outcome of [Payload.parse]; a bad blob is a value, never an exception. */
sealed interface PayloadResult {
    data class Loaded(val payload: Payload) : PayloadResult

    /** [code] follows `blob_validate` (tools/nativehook/inapk_server.c:98-111). */
    data class Invalid(val code: Int, val detail: String) : PayloadResult
}

/**
 * Reader for the TFTFPAY v1 blob (TECHNICAL_NOTES.md "In-app server payload v1").
 *
 * Layout and validation reproduce `blob_validate` (inapk_server.c:98-111), lookup
 * reproduces `find_key` (`:112-118`) and the prefix scan at `:612`. The blob is held as
 * one immutable byte array and never modified, so lookups are safe from any thread.
 */
class Payload private constructor(
    private val data: ByteArray,
    val port: Int,
    val exactCount: Int,
    private val exactOff: Int,
    val prefixCount: Int,
    private val prefixOff: Int,
    private val defaultOff: Int,
    private val defaultLen: Int,
) {
    /** Body for an exact key such as `"GET /bcg/getLoginData"` or `"@roster"`. */
    fun lookup(key: String): ByteArray? = lookup(key.toByteArray(Charsets.UTF_8))

    fun lookup(key: ByteArray): ByteArray? {
        var lo = 0
        var hi = exactCount
        while (lo < hi) {
            val mid = lo + (hi - lo) / 2
            val order = compareKey(exactOff + mid * REC, key)
            if (order == 0) return body(exactOff + mid * REC)
            if (order < 0) lo = mid + 1 else hi = mid
        }
        return null
    }

    /** First prefix rule, in stored order, whose key is a prefix of [path]. */
    fun prefixBody(path: String): ByteArray? {
        val raw = path.toByteArray(Charsets.UTF_8)
        for (i in 0 until prefixCount) {
            val rec = prefixOff + i * REC
            val len = u32(rec + 4)
            if (raw.size >= len && regionMatches(u32(rec), raw, len)) return body(rec)
        }
        return null
    }

    fun defaultBody(): ByteArray = data.copyOfRange(defaultOff, defaultOff + defaultLen)

    private fun u32(off: Int): Int = readU32(data, off).toInt()

    private fun body(rec: Int): ByteArray {
        val off = u32(rec + 8)
        return data.copyOfRange(off, off + u32(rec + 12))
    }

    private fun regionMatches(keyOff: Int, other: ByteArray, len: Int): Boolean {
        for (i in 0 until len) if (data[keyOff + i] != other[i]) return false
        return true
    }

    /** Raw unsigned byte compare, shorter prefix first (`keycmp`, `:93-97`). */
    private fun compareKey(rec: Int, key: ByteArray): Int {
        val keyOff = u32(rec)
        val keyLen = u32(rec + 4)
        val shared = minOf(keyLen, key.size)
        for (i in 0 until shared) {
            val diff = (data[keyOff + i].toInt() and 0xff) - (key[i].toInt() and 0xff)
            if (diff != 0) return diff
        }
        return keyLen.compareTo(key.size)
    }

    companion object {
        const val HEADER = 64
        const val REC = 16
        private val MAGIC = byteArrayOf(0x54, 0x46, 0x54, 0x46, 0x50, 0x41, 0x59, 0x00)

        fun readU32(bytes: ByteArray, off: Int): Long =
            (bytes[off].toLong() and 0xff) or
                ((bytes[off + 1].toLong() and 0xff) shl 8) or
                ((bytes[off + 2].toLong() and 0xff) shl 16) or
                ((bytes[off + 3].toLong() and 0xff) shl 24)

        /** Validates magic, version, total size, reserved words, CRC-32, tables and keys. */
        fun parse(bytes: ByteArray): PayloadResult {
            val header = validateHeader(bytes)
            if (header != null) return header
            val layout = readLayout(bytes)
                ?: return PayloadResult.Invalid(-3, "table or default-body range is out of bounds")
            val payload = Payload(
                bytes, layout.port, layout.count, layout.exactOff, layout.prefixCount,
                layout.prefixOff, layout.defaultOff, layout.defaultLen)
            return payload.validateRecords() ?: PayloadResult.Loaded(payload)
        }

        private class Layout(
            val port: Int,
            val count: Int,
            val exactOff: Int,
            val prefixCount: Int,
            val prefixOff: Int,
            val defaultOff: Int,
            val defaultLen: Int,
        )

        private fun validateHeader(bytes: ByteArray): PayloadResult.Invalid? {
            if (bytes.size < HEADER) return PayloadResult.Invalid(-1, "blob is shorter than the 64-byte header")
            if (!bytes.copyOfRange(0, 8).contentEquals(MAGIC)) return PayloadResult.Invalid(-1, "bad magic")
            if (readU32(bytes, 8) != 1L) return PayloadResult.Invalid(-1, "unsupported version")
            if (readU32(bytes, 12) != bytes.size.toLong()) {
                return PayloadResult.Invalid(-1, "total size does not match the file length (truncated?)")
            }
            for (off in 48..60 step 4) {
                if (readU32(bytes, off) != 0L) return PayloadResult.Invalid(-2, "reserved header words are not zero")
            }
            val crc = CRC32().apply { update(bytes, HEADER, bytes.size - HEADER) }
            if (crc.value != readU32(bytes, 44)) return PayloadResult.Invalid(-2, "CRC-32 mismatch")
            return null
        }

        private fun readLayout(bytes: ByteArray): Layout? {
            val total = bytes.size.toLong()
            val port = readU32(bytes, 16)
            val count = readU32(bytes, 20)
            val exactOff = readU32(bytes, 24)
            val prefixCount = readU32(bytes, 28)
            val prefixOff = readU32(bytes, 32)
            val defaultOff = readU32(bytes, 36)
            val defaultLen = readU32(bytes, 40)
            val tablesOk = tableOk(exactOff, count, total) && tableOk(prefixOff, prefixCount, total)
            if (port == 0L || port > 65535L || !tablesOk) return null
            if (!valueOk(bytes, defaultOff, defaultLen)) return null
            return Layout(
                port.toInt(), count.toInt(), exactOff.toInt(), prefixCount.toInt(),
                prefixOff.toInt(), defaultOff.toInt(), defaultLen.toInt())
        }

        private fun tableOk(off: Long, count: Long, total: Long): Boolean =
            off >= HEADER && off % 4 == 0L && off <= total && count * REC <= total - off

        /** `value_ok` (`:79-88`): aligned, in range, NUL-terminated, NUL-padded to 4. */
        private fun valueOk(bytes: ByteArray, off: Long, len: Long): Boolean {
            val total = bytes.size.toLong()
            if (off % 4 != 0L || off < HEADER || off > total || len > total - off) return false
            val end = off + len
            if (end >= total || bytes[end.toInt()].toInt() != 0) return false
            val pad = (end + 4) and 3L.inv()
            if (pad > total) return false
            for (i in (end + 1) until pad) if (bytes[i.toInt()].toInt() != 0) return false
            return true
        }
    }

    private fun validateRecords(): PayloadResult.Invalid? {
        var previous = -1
        for (i in 0 until exactCount + prefixCount) {
            val rec = if (i < exactCount) exactOff + i * REC else prefixOff + (i - exactCount) * REC
            if (!recordValuesOk(rec)) return PayloadResult.Invalid(-4, "record $i has a value out of range")
            if (i >= exactCount) continue
            if (previous >= 0 && compareRecords(previous, rec) >= 0) {
                return PayloadResult.Invalid(-5, "exact keys are not strictly ascending at record $i")
            }
            previous = rec
        }
        return null
    }

    private fun recordValuesOk(rec: Int): Boolean =
        valueOk(data, readU32(data, rec), readU32(data, rec + 4)) &&
            valueOk(data, readU32(data, rec + 8), readU32(data, rec + 12))

    private fun compareRecords(a: Int, b: Int): Int =
        compareKey(a, data.copyOfRange(u32(b), u32(b) + u32(b + 4)))
}
