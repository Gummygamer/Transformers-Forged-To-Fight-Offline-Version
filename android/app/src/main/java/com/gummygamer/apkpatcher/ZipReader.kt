package com.gummygamer.apkpatcher

import java.io.Closeable
import java.io.InputStream
import java.io.FilterInputStream
import java.util.zip.Inflater
import java.util.zip.InflaterInputStream
import java.nio.ByteBuffer
import java.nio.ByteOrder

/**
 * Streaming ZIP reader for APK files (which are ZIP archives).
 * Parses the End-of-Central-Directory record to locate entries,
 * then reads central directory headers to enumerate files.
 *
 * Does NOT load the full archive into memory; preserves the ability
 * to stream individual entries on demand.
 */
class ZipReader private constructor(
    private val channel: SeekableByteChannel,
    val entries: List<ZipEntry>
) : Closeable {

    data class ZipEntry(
        val name: String,
        val createVersion: Int,
        val createSystem: Int,
        val extractVersion: Int,
        val flagBits: Int,
        val compressType: Int,
        val dostime: Int,
        val dosdate: Int,
        val crc: Long,
        val compressSize: Long,
        val fileSize: Long,
        val extra: ByteArray,
        val comment: ByteArray,
        val internalAttr: Int,
        val externalAttr: Long,
        val headerOffset: Long,
        val dataOffset: Long
    ) {
        val isDirectory: Boolean get() = name.endsWith("/")
        val isEncrypted: Boolean get() = (flagBits and 1) != 0
        val hasDataDescriptor: Boolean get() = (flagBits and 8) != 0

        override fun equals(other: Any?): Boolean {
            if (this === other) return true
            if (other !is ZipEntry) return false
            return name == other.name && headerOffset == other.headerOffset
        }

        override fun hashCode(): Int = 31 * name.hashCode() + headerOffset.hashCode()
    }

    companion object {
        private const val EOCD_MIN_SIZE = 22
        private const val EOCD_SIGNATURE = 0x06054b50L
        private const val CENTRAL_SIGNATURE = 0x02014b50L
        private const val LOCAL_SIGNATURE = 0x04034b50L
        private const val MAX_EOCD_COMMENT = 65535

        /**
         * Open a ZIP from a [SeekableByteChannel].
         * Scans backward from end for EOCD, then reads central directory.
         */
        fun open(channel: SeekableByteChannel): ZipReader {
            val size = channel.size()
            if (size < EOCD_MIN_SIZE) {
                throw ZipException("File is too small to be a ZIP archive (${size} bytes)")
            }

            // Scan backward for EOCD signature
            val scanStart = maxOf(0L, size - EOCD_MIN_SIZE - MAX_EOCD_COMMENT)
            val scanLen = (size - scanStart).toInt()
            val buf = ByteArray(scanLen)
            channel.read(scanStart, buf, 0, scanLen)

            var eocdOff = -1
            for (i in scanLen - EOCD_MIN_SIZE downTo 0) {
                if (buf[i].toInt() == 0x50 && buf[i + 1].toInt() == 0x4b &&
                    buf[i + 2].toInt() == 0x05 && buf[i + 3].toInt() == 0x06
                ) {
                    eocdOff = i
                    break
                }
            }

            if (eocdOff < 0) {
                throw ZipException("No end-of-central-directory record found")
            }

            val eocd = ByteBuffer.wrap(buf, eocdOff, EOCD_MIN_SIZE).order(ByteOrder.LITTLE_ENDIAN)
            eocd.getInt() // signature
            eocd.getShort() // disk number
            eocd.getShort() // disk with central dir
            val entryCountDisk = eocd.getShort().toInt() and 0xFFFF
            val entryCount = eocd.getShort().toInt() and 0xFFFF
            val centralSize = eocd.getInt().toLong() and 0xFFFFFFFFL
            val centralOffset = eocd.getInt().toLong() and 0xFFFFFFFFL

            if (entryCountDisk != entryCount) {
                throw ZipException("Multi-disk ZIP archives are not supported")
            }

            // Read central directory
            if (centralSize > Int.MAX_VALUE || centralOffset > size || centralSize > size - centralOffset) {
                throw ZipException("ZIP central directory is outside the source file")
            }
            val centralBuf = ByteArray(centralSize.toInt())
            readFully(channel, centralOffset, centralBuf)
            val centralBB = ByteBuffer.wrap(centralBuf).order(ByteOrder.LITTLE_ENDIAN)

            val entries = mutableListOf<ZipEntry>()
            var pos = 0
            while (pos + 46 <= centralSize && entries.size < entryCount) {
                centralBB.position(pos)
                val signature = centralBB.getInt().toLong() and 0xFFFFFFFFL
                if (signature != CENTRAL_SIGNATURE) {
                    throw ZipException("Invalid central directory signature at offset $pos")
                }

                // Standard ZIP central directory file header (46 bytes fixed):
                val versionMadeBy = centralBB.getShort().toInt() and 0xFFFF
                val createVersion = versionMadeBy and 0xFF
                val createSystem = (versionMadeBy shr 8) and 0xFF
                val extractVersion = centralBB.getShort().toInt() and 0xFFFF
                val flagBits = centralBB.getShort().toInt() and 0xFFFF
                val compressType = centralBB.getShort().toInt() and 0xFFFF
                val dostime = centralBB.getShort().toInt() and 0xFFFF
                val dosdate = centralBB.getShort().toInt() and 0xFFFF
                val crc = centralBB.getInt().toLong() and 0xFFFFFFFFL
                val compressSize = centralBB.getInt().toLong() and 0xFFFFFFFFL
                val fileSize = centralBB.getInt().toLong() and 0xFFFFFFFFL
                val nlen = centralBB.getShort().toInt() and 0xFFFF
                val xlen = centralBB.getShort().toInt() and 0xFFFF
                val clen = centralBB.getShort().toInt() and 0xFFFF
                centralBB.getShort() // disk start
                val internalAttr = centralBB.getShort().toInt() and 0xFFFF
                val externalAttr = centralBB.getInt().toLong() and 0xFFFFFFFFL
                val headerOffset = centralBB.getInt().toLong() and 0xFFFFFFFFL

                val nameBytes = ByteArray(nlen)
                centralBB.get(nameBytes)
                val name = String(nameBytes, Charsets.UTF_8)

                val extraBytes = if (xlen > 0) {
                    val ex = ByteArray(xlen)
                    centralBB.get(ex)
                    ex
                } else ByteArray(0)

                val commentBytes = if (clen > 0) {
                    val cm = ByteArray(clen)
                    centralBB.get(cm)
                    cm
                } else ByteArray(0)

                // Read local header to get data offset
                val localHeader = ByteArray(30)
                if (headerOffset < 0 || headerOffset > size - 30) {
                    throw ZipException("ZIP local header for '$name' is outside the source file")
                }
                readFully(channel, headerOffset, localHeader)
                val localBB = ByteBuffer.wrap(localHeader).order(ByteOrder.LITTLE_ENDIAN)
                val localSig = localBB.getInt().toLong() and 0xFFFFFFFFL
                if (localSig != LOCAL_SIGNATURE) {
                    throw ZipException("Invalid local header signature for entry '$name'")
                }
                localBB.getShort() // extract version
                val localFlagBits = localBB.getShort().toInt() and 0xFFFF
                localBB.getShort() // compress type
                localBB.getShort() // dostime
                localBB.getShort() // dosdate
                localBB.getInt() // crc
                localBB.getInt() // compress size
                localBB.getInt() // file size
                val localNlen = localBB.getShort().toInt() and 0xFFFF
                val localXlen = localBB.getShort().toInt() and 0xFFFF
                val dataOffset = headerOffset + 30 + localNlen + localXlen
                if (dataOffset < 0 || dataOffset > size || compressSize > size - dataOffset) {
                    throw ZipException("ZIP entry '$name' data is outside the source file")
                }

                entries += ZipEntry(
                    name = name,
                    createVersion = createVersion,
                    createSystem = createSystem,
                    extractVersion = extractVersion,
                    flagBits = flagBits,
                    compressType = compressType,
                    dostime = dostime,
                    dosdate = dosdate,
                    crc = crc,
                    compressSize = compressSize,
                    fileSize = fileSize,
                    extra = extraBytes,
                    comment = commentBytes,
                    internalAttr = internalAttr,
                    externalAttr = externalAttr,
                    headerOffset = headerOffset,
                    dataOffset = dataOffset
                )

                pos += 46 + nlen + xlen + clen
            }

            if (entries.size != entryCount) {
                throw ZipException("ZIP central directory ended before all entries were read")
            }
            return ZipReader(channel, entries)
        }

        private fun readFully(channel: SeekableByteChannel, position: Long, buffer: ByteArray) {
            var done = 0
            while (done < buffer.size) {
                val count = channel.read(position + done, buffer, done, buffer.size - done)
                if (count <= 0) throw ZipException("Unexpected end of ZIP archive")
                done += count
            }
        }
    }

    /** Find an entry by exact name, or -1 if not found. */
    fun find(name: String): Int = entries.indexOfFirst { it.name == name }

    /** Read the raw (possibly compressed) data for an entry at the given index. */
    fun readEntryData(index: Int): ByteArray {
        val entry = entries[index]
        if (entry.compressSize > Int.MAX_VALUE) {
            throw ZipException("ZIP entry '${entry.name}' is too large to load into memory")
        }
        val data = ByteArray(entry.compressSize.toInt())
        companionReadFully(entry.dataOffset, data)
        return data
    }

    /** Open one entry without loading the archive or entry into a whole-file buffer. */
    fun openEntryStream(index: Int, inflate: Boolean = false): InputStream {
        val entry = entries.getOrNull(index) ?: throw ZipException("ZIP entry index out of range: $index")
        if (entry.isEncrypted) throw ZipException("Unsupported encrypted ZIP entry: ${entry.name}")
        val raw = BoundedChannelInputStream(channel, entry.dataOffset, entry.compressSize)
        return when {
            !inflate -> raw
            entry.compressType == 0 -> raw
            entry.compressType == 8 -> InflaterInputStream(raw, Inflater(true), 32 * 1024)
            else -> throw ZipException("Unsupported compression type ${entry.compressType} for '${entry.name}'")
        }
    }

    fun copyEntryTo(index: Int, output: java.io.OutputStream, inflate: Boolean = false) {
        openEntryStream(index, inflate).use { it.copyTo(output, 32 * 1024) }
    }

    /** Read and inflate entry data (handles stored and deflate). */
    fun readEntryDataInflated(index: Int): ByteArray {
        val entry = entries[index]
        val raw = readEntryData(index)
        if (entry.compressType == 0) {
            return raw
        }
        // DEFLATE (type 8)
        if (entry.compressType == 8) {
            if (entry.fileSize > Int.MAX_VALUE) {
                throw ZipException("ZIP entry '${entry.name}' is too large to inflate into memory")
            }
            return InflaterUtils.inflate(raw, entry.fileSize.toInt())
        }
        throw ZipException("Unsupported compression type ${entry.compressType} for '${entry.name}'")
    }

    override fun close() {
        channel.close()
    }

    private fun companionReadFully(position: Long, buffer: ByteArray) {
        var done = 0
        while (done < buffer.size) {
            val count = channel.read(position + done, buffer, done, buffer.size - done)
            if (count <= 0) throw ZipException("Unexpected end of ZIP entry '${entries.firstOrNull { it.dataOffset == position }?.name ?: "unknown"}'")
            done += count
        }
    }
}

/** Minimal seekable byte channel interface (decouples from Java NIO for testing). */
interface SeekableByteChannel : Closeable {
    fun size(): Long
    fun read(position: Long, buffer: ByteArray, offset: Int, length: Int): Int
}

class ZipException(message: String) : Exception(message)

private class BoundedChannelInputStream(
    private val channel: SeekableByteChannel,
    private val start: Long,
    private val length: Long
) : InputStream() {
    private var position = 0L

    override fun read(): Int {
        val one = ByteArray(1)
        return if (read(one, 0, 1) == 1) one[0].toInt() and 0xff else -1
    }

    override fun read(buffer: ByteArray, offset: Int, count: Int): Int {
        require(offset >= 0 && count >= 0 && offset <= buffer.size - count)
        if (position >= length || count == 0) return if (count == 0) 0 else -1
        val wanted = minOf(count.toLong(), length - position).toInt()
        val read = channel.read(start + position, buffer, offset, wanted)
        if (read <= 0) throw java.io.EOFException("Unexpected end of ZIP entry")
        position += read
        return read
    }

    override fun close() = Unit
}

/** Simple DEFLATE inflation using java.util.zip.Inflater. */
object InflaterUtils {
    fun inflate(data: ByteArray, estimatedSize: Int): ByteArray {
        val inflater = java.util.zip.Inflater(true)
        inflater.setInput(data)
        val output = java.io.ByteArrayOutputStream(maxOf(estimatedSize, data.size * 2))
        val buf = ByteArray(32768)
        while (!inflater.finished()) {
            val count = inflater.inflate(buf)
            if (count > 0) output.write(buf, 0, count)
            if (inflater.needsDictionary()) {
                throw ZipException("DEFLATE dictionary required but not available")
            }
            if (!inflater.finished() && inflater.needsInput()) {
                throw ZipException("DEFLATE entry is truncated")
            }
            if (count == 0 && !inflater.finished()) {
                throw ZipException("DEFLATE entry could not make progress")
            }
        }
        inflater.end()
        return output.toByteArray()
    }
}
