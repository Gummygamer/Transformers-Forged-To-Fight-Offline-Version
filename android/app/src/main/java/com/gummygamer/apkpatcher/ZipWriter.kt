package com.gummygamer.apkpatcher

import java.io.OutputStream
import java.io.InputStream
import java.io.ByteArrayInputStream
import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.util.zip.CRC32

/**
 * Streaming ZIP writer for APK reconstruction.
 *
 * Writes entries in order to an OutputStream, computing CRCs for stored entries,
 * and aligning .so entries on 4-byte boundaries per zipalign -p 4 convention.
 * Output is a valid ZIP with local headers, data, central directory, and EOCD.
 */
class ZipWriter(
    private val output: OutputStream,
    /**
     * Alignment boundary in bytes for uncompressed native libraries.
     *
     * Android devices with 16 KiB memory pages require native libraries to be
     * page aligned in the APK. 16 KiB also satisfies 4 KiB devices. Callers
     * that intentionally target an older device can still provide a smaller
     * boundary explicitly (the unit tests do this for their synthetic fixtures).
     */
    private val soAlignment: Int = 16 * 1024
) {
    private data class WrittenEntry(
        val name: String,
        val headerOffset: Long,
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
        val externalAttr: Long
    )

    private val entries = mutableListOf<WrittenEntry>()
    private var bytesWritten: Long = 0
    private val crc32 = CRC32()

    companion object {
        private const val LOCAL_SIGNATURE = 0x04034b50L
        private const val CENTRAL_SIGNATURE = 0x02014b50L
        private const val EOCD_SIGNATURE = 0x06054b50L
        private const val LOCAL_HEADER_SIZE = 30
        private const val DEFAULT_EXTERNAL_ATTR = 0x81B60000L
    }

    /**
     * Write a stored (uncompressed) entry to the archive.
     *
     * When [name] starts with "lib/" and ends with ".so", the entry data is aligned
     * to [soAlignment] bytes by adding a padding extra-field record before the data.
     */
    fun writeStored(
        name: String,
        data: ByteArray,
        createVersion: Int = 20,
        createSystem: Int = 3,
        extractVersion: Int = 20,
        dostime: Int = 0,
        dosdate: Int = 33,
        extra: ByteArray = ByteArray(0),
        comment: ByteArray = ByteArray(0),
        internalAttr: Int = 0,
        externalAttr: Long = DEFAULT_EXTERNAL_ATTR
    ) {
        val effectiveExtra = if (name.endsWith(".so") && name.startsWith("lib/")) {
            alignmentPadding(name, extra)
        } else {
            extra
        }
        writeStream(
            name = name,
            input = ByteArrayInputStream(data),
            compressType = 0,
            crc = crc32Of(data),
            compressedSize = data.size.toLong(),
            fileSize = data.size.toLong(),
            createVersion = createVersion,
            createSystem = createSystem,
            extractVersion = extractVersion,
            flagBits = 0,
            dostime = dostime,
            dosdate = dosdate,
            extra = effectiveExtra,
            comment = comment,
            internalAttr = internalAttr,
            externalAttr = externalAttr
        )
    }

    /** Copy an existing ZIP entry, preserving compressed bytes and metadata. */
    fun writeRaw(
        name: String,
        input: InputStream,
        compressType: Int,
        crc: Long,
        compressedSize: Long,
        fileSize: Long,
        createVersion: Int = 20,
        createSystem: Int = 3,
        extractVersion: Int = 20,
        flagBits: Int = 0,
        dostime: Int = 0,
        dosdate: Int = 33,
        extra: ByteArray = ByteArray(0),
        comment: ByteArray = ByteArray(0),
        internalAttr: Int = 0,
        externalAttr: Long = DEFAULT_EXTERNAL_ATTR
    ) {
        val isNative = name.startsWith("lib/") && name.endsWith(".so")
        val effectiveExtra = if (isNative && compressType == 0) alignmentPadding(name, extra) else extra
        writeStream(
            name = name,
            input = input,
            compressType = compressType,
            crc = crc,
            compressedSize = compressedSize,
            fileSize = fileSize,
            createVersion = createVersion,
            createSystem = createSystem,
            extractVersion = extractVersion,
            flagBits = flagBits,
            dostime = dostime,
            dosdate = dosdate,
            extra = effectiveExtra,
            comment = comment,
            internalAttr = internalAttr,
            externalAttr = externalAttr
        )
    }

    /**
     * Compute alignment padding for a .so entry.
     *
     * Returns an extra-field byte array that, when placed in the local header,
     * makes the entry data start at a [soAlignment]-byte boundary.
     *
     * The minimum ZIP extra-field record is 4 bytes (2-byte ID + 2-byte size).
     * If the alignment gap is 1-3 bytes we add one full alignment unit so that the
     * extra field is always >= 4 bytes and the data offset ends up aligned.
     */
    private fun alignmentPadding(name: String, existingExtra: ByteArray): ByteArray {
        val nameBytes = name.toByteArray(Charsets.UTF_8)
        val dataStartWithoutPad = bytesWritten + LOCAL_HEADER_SIZE + nameBytes.size + existingExtra.size
        val misalignment = (dataStartWithoutPad % soAlignment).toInt()
        if (misalignment == 0) return existingExtra

        // Compute the smallest padding size >= 4 that achieves alignment.
        var padSize = soAlignment - misalignment
        while (padSize < 4) {
            padSize += soAlignment
        }

        val pad = extraPadding(padSize)
        return if (existingExtra.isEmpty()) pad else existingExtra + pad
    }

    /**
     * Build a ZIP extra-field record with [totalSize] bytes: 2-byte ID (zero),
     * 2-byte length, and the remaining bytes zero-filled.
     */
    private fun extraPadding(totalSize: Int): ByteArray {
        val dataSize = totalSize - 4
        val buf = ByteBuffer.allocate(totalSize).order(ByteOrder.LITTLE_ENDIAN)
        buf.putShort(0)
        buf.putShort(dataSize.toShort())
        for (i in 0 until dataSize) buf.put(0)
        return buf.array()
    }

    private fun writeStream(
        name: String,
        input: InputStream,
        compressType: Int,
        crc: Long,
        compressedSize: Long,
        fileSize: Long,
        createVersion: Int,
        createSystem: Int,
        extractVersion: Int,
        flagBits: Int,
        dostime: Int,
        dosdate: Int,
        extra: ByteArray,
        comment: ByteArray,
        internalAttr: Int,
        externalAttr: Long
    ) {
        val nameBytes = name.toByteArray(Charsets.UTF_8)
        val headerOffset = bytesWritten

        require(compressedSize in 0..0xffffffffL && fileSize in 0..0xffffffffL) {
            "ZIP entry '$name' exceeds the classic ZIP 4 GiB limit"
        }

        val localHeader = ByteBuffer.allocate(LOCAL_HEADER_SIZE + nameBytes.size + extra.size)
            .order(ByteOrder.LITTLE_ENDIAN)
        localHeader.putInt(LOCAL_SIGNATURE.toInt())
        localHeader.putShort(extractVersion.toShort())
        localHeader.putShort((flagBits and 0xFFF7).toShort())
        localHeader.putShort(compressType.toShort())
        localHeader.putShort(dostime.toShort())
        localHeader.putShort(dosdate.toShort())
        localHeader.putInt(crc.toInt())
        localHeader.putInt(compressedSize.toInt())
        localHeader.putInt(fileSize.toInt())
        localHeader.putShort(nameBytes.size.toShort())
        localHeader.putShort(extra.size.toShort())
        localHeader.put(nameBytes)
        localHeader.put(extra)
        output.write(localHeader.array())
        bytesWritten += localHeader.array().size.toLong()

        var copied = 0L
        val buffer = ByteArray(64 * 1024)
        input.use { source ->
            while (copied < compressedSize) {
                val wanted = minOf(buffer.size.toLong(), compressedSize - copied).toInt()
                val count = source.read(buffer, 0, wanted)
                if (count < 0) throw java.io.EOFException("Entry '$name' ended before its ZIP size")
                if (count == 0) continue
                output.write(buffer, 0, count)
                copied += count
            }
        }
        if (copied != compressedSize) throw java.io.EOFException("Entry '$name' size mismatch")
        bytesWritten += copied

        entries += WrittenEntry(
            name = name,
            headerOffset = headerOffset,
            createVersion = createVersion,
            createSystem = createSystem,
            extractVersion = extractVersion,
            flagBits = flagBits and 0xFFF7,
            compressType = compressType,
            dostime = dostime,
            dosdate = dosdate,
            crc = crc,
            compressSize = compressedSize,
            fileSize = fileSize,
            extra = extra,
            comment = comment,
            internalAttr = internalAttr,
            externalAttr = externalAttr
        )
    }

    private fun crc32Of(data: ByteArray): Long {
        crc32.reset()
        crc32.update(data)
        return crc32.value
    }

    fun finish() {
        val centralStart = bytesWritten

        for (entry in entries) {
            val nameBytes = entry.name.toByteArray(Charsets.UTF_8)
            val centralEntry = ByteBuffer.allocate(46 + nameBytes.size + entry.extra.size + entry.comment.size)
                .order(ByteOrder.LITTLE_ENDIAN)
            val versionMadeBy = ((entry.createSystem and 0xFF) shl 8) or (entry.createVersion and 0xFF)
            centralEntry.putInt(CENTRAL_SIGNATURE.toInt())
            centralEntry.putShort(versionMadeBy.toShort())
            centralEntry.putShort(entry.extractVersion.toShort())
            centralEntry.putShort(entry.flagBits.toShort())
            centralEntry.putShort(entry.compressType.toShort())
            centralEntry.putShort(entry.dostime.toShort())
            centralEntry.putShort(entry.dosdate.toShort())
            centralEntry.putInt(entry.crc.toInt())
            centralEntry.putInt(entry.compressSize.toInt())
            centralEntry.putInt(entry.fileSize.toInt())
            centralEntry.putShort(nameBytes.size.toShort())
            centralEntry.putShort(entry.extra.size.toShort())
            centralEntry.putShort(entry.comment.size.toShort())
            centralEntry.putShort(0)
            centralEntry.putShort(entry.internalAttr.toShort())
            centralEntry.putInt(entry.externalAttr.toInt())
            centralEntry.putInt(entry.headerOffset.toInt())
            centralEntry.put(nameBytes)
            centralEntry.put(entry.extra)
            centralEntry.put(entry.comment)
            output.write(centralEntry.array())
            bytesWritten += centralEntry.array().size.toLong()
        }

        val centralEnd = bytesWritten
        val centralSize = centralEnd - centralStart

        val eocd = ByteBuffer.allocate(22).order(ByteOrder.LITTLE_ENDIAN)
        eocd.putInt(EOCD_SIGNATURE.toInt())
        eocd.putShort(0)
        eocd.putShort(0)
        eocd.putShort(entries.size.toShort())
        eocd.putShort(entries.size.toShort())
        eocd.putInt(centralSize.toInt())
        eocd.putInt(centralStart.toInt())
        eocd.putShort(0)
        output.write(eocd.array())
        bytesWritten += 22

        output.flush()
    }
}
