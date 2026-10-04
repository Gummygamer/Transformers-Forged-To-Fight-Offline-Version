package com.gummygamer.tftfpvphost.server

import java.io.ByteArrayOutputStream
import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.util.zip.CRC32

/** Builds small TFTFPAY v1 blobs in test code so no test needs the large generated asset. */
object PayloadBuilder {
    const val DEFAULT_BODY = "{\"error\":null,\"result\":{}}"

    fun build(
        exact: Map<String, String>,
        prefixes: List<Pair<String, String>> = emptyList(),
        port: Int = 8080,
    ): ByteArray {
        val keys = exact.keys.sorted()
        val tableBytes = (keys.size + prefixes.size) * Payload.REC
        val values = ByteArrayOutputStream()
        val base = Payload.HEADER + tableBytes
        fun put(text: String): Pair<Int, Int> {
            val raw = text.toByteArray(Charsets.UTF_8)
            val offset = base + values.size()
            values.write(raw)
            repeat(4 - raw.size % 4) { values.write(0) }
            return offset to raw.size
        }
        val exactRecords = keys.map { put(it) to put(exact.getValue(it)) }
        val prefixRecords = prefixes.map { put(it.first) to put(it.second) }
        val (defaultOff, defaultLen) = put(DEFAULT_BODY)
        val total = base + values.size()
        val out = ByteBuffer.allocate(total).order(ByteOrder.LITTLE_ENDIAN)
        out.position(Payload.HEADER)
        (exactRecords + prefixRecords).forEach { (k, b) ->
            out.putInt(k.first).putInt(k.second).putInt(b.first).putInt(b.second)
        }
        out.put(values.toByteArray())
        val bytes = out.array()
        writeHeader(bytes, total, port, keys.size, prefixes.size, defaultOff, defaultLen)
        return bytes
    }

    private fun writeHeader(
        bytes: ByteArray, total: Int, port: Int, count: Int, prefixCount: Int, defaultOff: Int, defaultLen: Int,
    ) {
        val crc = CRC32().apply { update(bytes, Payload.HEADER, total - Payload.HEADER) }.value
        val head = ByteBuffer.wrap(bytes).order(ByteOrder.LITTLE_ENDIAN)
        head.put("TFTFPAY".toByteArray()).put(0)
        head.putInt(1).putInt(total).putInt(port).putInt(count)
        head.putInt(Payload.HEADER).putInt(prefixCount)
        head.putInt(Payload.HEADER + count * Payload.REC)
        head.putInt(defaultOff).putInt(defaultLen).putInt(crc.toInt())
    }
}
