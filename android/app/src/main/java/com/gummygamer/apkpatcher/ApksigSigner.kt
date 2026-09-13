package com.gummygamer.apkpatcher

import org.bouncycastle.jce.provider.BouncyCastleProvider
import java.io.ByteArrayInputStream
import java.io.ByteArrayOutputStream
import java.io.File
import java.io.FileOutputStream
import java.io.IOException
import java.io.RandomAccessFile
import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.security.MessageDigest
import java.security.PrivateKey
import java.security.Signature
import java.security.cert.CertificateFactory
import java.security.cert.X509Certificate
import java.security.interfaces.RSAPrivateKey

/** Small, file-backed implementation of APK Signature Scheme v2. */
object ApksigSigner {
    private const val V2_ID = 0x7109871a
    private const val DIGEST_ALGORITHM = "SHA-256"
    private const val SIGNATURE_ALGORITHM = "SHA256withRSA"
    // RSASSA-PKCS1-v1_5 with SHA-256.
    private const val DIGEST_ALGORITHM_ID = 0x0103
    private const val CHUNK_SIZE = 1024 * 1024
    private const val EOCD_SIZE = 22
    private val MAGIC = "APK Sig Block 42".toByteArray(Charsets.US_ASCII)

    data class SignResult(val signedApk: ByteArray?, val error: String?) {
        val isSuccess: Boolean get() = error == null
    }

    data class VerifyResult(val isVerified: Boolean, val details: String)

    /** Compatibility API for JVM tests. Production callers should use the file overload. */
    fun sign(apkContent: ByteArray, centralDirAndEocd: ByteArray, privateKey: PrivateKey, certificate: X509Certificate): SignResult {
        return try {
            val input = File.createTempFile("tftf-sign-input-", ".apk")
            val output = File.createTempFile("tftf-sign-output-", ".apk")
            try {
                if (centralDirAndEocd.size < EOCD_SIZE) throw IOException("central directory and EOCD are truncated")
                val centralSize = centralDirAndEocd.size - EOCD_SIZE
                val eocd = centralDirAndEocd.copyOfRange(centralSize, centralDirAndEocd.size)
                putU32(eocd, 12, centralSize.toLong())
                putU32(eocd, 16, apkContent.size.toLong())
                FileOutputStream(input).use { it.write(apkContent); it.write(centralDirAndEocd, 0, centralSize); it.write(eocd) }
                val result = sign(input, output, privateKey, certificate)
                if (!result.isSuccess) result else SignResult(output.readBytes(), null)
            } finally { input.delete(); output.delete() }
        } catch (e: Exception) { SignResult(null, "Signing failed: ${describe(e)}") }
    }

    /** Sign an APK into a file without creating a whole-APK byte array. */
    fun sign(unsignedApk: File, signedApk: File, privateKey: PrivateKey, certificate: X509Certificate): SignResult {
        return try {
            val layout = readLayout(unsignedApk)
            // The EOCD offset is part of the signed content. The block length is
            // independent of digest values, so determine it with fixed-size placeholders.
            val signatureSize = ((privateKey as? RSAPrivateKey)?.modulus?.bitLength()?.plus(7)?.div(8)) ?: 256
            val placeholderData = buildSignedData(ByteArray(32), certificate.encoded)
            val publicKey = certificate.publicKey.encoded
            if (publicKey.isEmpty()) throw IOException("certificate public key is empty")
            val placeholderBlock = buildSigningBlock(buildSigner(placeholderData, ByteArray(signatureSize), publicKey))
            val finalEocd = adjustedEocd(layout.eocd, layout.centralDirectoryOffset + placeholderBlock.size)
            // The EOCD offset in the content digest is the offset in the
            // unsigned layout. Android restores that value while verifying;
            // the signed APK stores the adjusted offset only for ZIP readers.
            val digestEocd = adjustedEocd(layout.eocd, layout.centralDirectoryOffset)
            val digest = computeDigest(unsignedApk, layout.centralDirectoryOffset, layout.centralDirectoryOffset, layout.eocdOffset, digestEocd)
            val signedData = buildSignedData(digest, certificate.encoded)
            val signature = signData(signedData, privateKey)
            val signingBlock = buildSigningBlock(buildSigner(signedData, signature, publicKey))
            val newEocd = adjustedEocd(layout.eocd, layout.centralDirectoryOffset + signingBlock.size)

            FileOutputStream(signedApk).use { out ->
                RandomAccessFile(unsignedApk, "r").use { input ->
                    copyRange(input, out, 0L, layout.centralDirectoryOffset)
                    out.write(signingBlock)
                    copyRange(input, out, layout.centralDirectoryOffset, layout.eocdOffset)
                }
                out.write(newEocd)
                out.fd.sync()
            }
            SignResult(null, null)
        } catch (e: Exception) { SignResult(null, "Signing failed: ${describe(e)}") }
    }

    fun verify(signedApk: ByteArray): VerifyResult {
        return try {
            val file = File.createTempFile("tftf-verify-", ".apk")
            try { file.writeBytes(signedApk); verify(file) } finally { file.delete() }
        } catch (e: Exception) { VerifyResult(false, "Verification error: ${describe(e)}") }
    }

    /** Verify both the RSA signature and the v2 content digest. */
    fun verify(signedApk: File): VerifyResult {
        return try {
            val layout = readSignedLayout(signedApk)
            val pair = findV2Pair(signedApk, layout.blockStart, layout.pairsEnd) ?: return VerifyResult(false, "No v2 signature found")
            val signer = parseSigner(pair)
            val certificate = certificateFactory()
                .generateCertificate(ByteArrayInputStream(signer.certificate)) as X509Certificate
            if (signer.publicKey.isEmpty() || !signer.publicKey.contentEquals(certificate.publicKey.encoded)) {
                return VerifyResult(false, "v2 public key does not match the signing certificate")
            }
            val verifier = signature()
            verifier.initVerify(certificate.publicKey)
            verifier.update(signer.signedData)
            if (!verifier.verify(signer.signature)) return VerifyResult(false, "v2 signature cryptographic verification failed")
            val digestEocd = adjustedEocd(layout.eocd, layout.blockStart)
            val expected = computeDigest(
                signedApk,
                layout.blockStart,
                layout.centralDirectoryOffset,
                layout.eocdOffset,
                digestEocd
            )
            if (!expected.contentEquals(signer.digest)) return VerifyResult(false, "v2 content digest does not match APK bytes")
            VerifyResult(true, "v2 signature verified (cryptographic and content digest checks)")
        } catch (e: Exception) { VerifyResult(false, "Verification error: ${describe(e)}") }
    }

    /**
     * Android vendor images do not all expose the same provider services. Try the
     * platform implementation first, then an application-owned BC instance without
     * registering it under the potentially conflicting global name "BC".
     */
    private fun certificateFactory(): CertificateFactory = try {
        CertificateFactory.getInstance("X.509")
    } catch (first: Exception) {
        try {
            CertificateFactory.getInstance("X.509", BouncyCastleProvider())
        } catch (second: Exception) {
            first.addSuppressed(second)
            throw first
        }
    }

    private fun signature(): Signature = try {
        Signature.getInstance(SIGNATURE_ALGORITHM)
    } catch (first: Exception) {
        try {
            Signature.getInstance(SIGNATURE_ALGORITHM, BouncyCastleProvider())
        } catch (second: Exception) {
            first.addSuppressed(second)
            throw first
        }
    }

    private fun describe(error: Exception): String {
        val messages = mutableListOf<String>()
        var current: Throwable? = error
        while (current != null) {
            current.message?.takeIf { it.isNotBlank() }?.let { messages += it }
            current = current.cause
        }
        return messages.distinct().joinToString("; ").ifBlank { error.javaClass.simpleName }
    }

    private data class Layout(val centralDirectoryOffset: Long, val eocdOffset: Long, val eocd: ByteArray)
    private data class SignedLayout(val blockStart: Long, val centralDirectoryOffset: Long, val eocdOffset: Long, val eocd: ByteArray, val pairsEnd: Long)

    private fun readLayout(file: File): Layout {
        RandomAccessFile(file, "r").use { raf ->
            val eocdOffset = findEocd(raf)
            val eocd = readEocd(raf, eocdOffset)
            val cdOffset = readU32(eocd, 16)
            val cdSize = readU32(eocd, 12)
            if (cdOffset > eocdOffset || cdSize > eocdOffset - cdOffset) throw IOException("central directory is outside APK")
            return Layout(cdOffset, eocdOffset, eocd)
        }
    }

    private fun readSignedLayout(file: File): SignedLayout {
        RandomAccessFile(file, "r").use { raf ->
            val eocdOffset = findEocd(raf)
            val eocd = readEocd(raf, eocdOffset)
            val cdOffset = readU32(eocd, 16)
            if (cdOffset < 32 || cdOffset > eocdOffset) throw IOException("invalid central directory offset")
            val blockSize = readU64(raf, cdOffset - 24)
            if (blockSize < 32 || blockSize > cdOffset - 8) throw IOException("invalid APK signing block size")
            val blockStart = cdOffset - blockSize - 8
            if (readU64(raf, blockStart) != blockSize) throw IOException("signing block size mismatch")
            val magic = ByteArray(16); raf.seek(cdOffset - 16); raf.readFully(magic)
            if (!magic.contentEquals(MAGIC)) throw IOException("missing APK Sig Block magic")
            return SignedLayout(blockStart, cdOffset, eocdOffset, eocd, cdOffset - 24)
        }
    }

    private fun readEocd(raf: RandomAccessFile, offset: Long): ByteArray {
        val commentLength = readU16(raf, offset + 20)
        return ByteArray(EOCD_SIZE + commentLength).also { raf.seek(offset); raf.readFully(it) }
    }

    private fun adjustedEocd(original: ByteArray, centralDirectoryOffset: Long): ByteArray = original.copyOf().also { putU32(it, 16, centralDirectoryOffset) }

    /** Hash the logical APK content as chunked by the APK v2 specification. */
    private fun computeDigest(file: File, firstEnd: Long, secondStart: Long, secondEnd: Long, replacementEocd: ByteArray? = null): ByteArray {
        val chunks = ArrayList<ByteArray>()
        fun feedBytes(bytes: ByteArray) {
            var at = 0
            while (at < bytes.size) {
                val length = minOf(CHUNK_SIZE, bytes.size - at)
                chunks += chunkDigest(bytes, at, length)
                at += length
            }
        }
        fun feedRange(raf: RandomAccessFile, start: Long, end: Long) {
            if (start < 0 || end < start || end > raf.length()) throw IOException("invalid signed content range")
            raf.seek(start)
            var left = end - start
            val chunk = ByteArray(CHUNK_SIZE)
            while (left > 0) {
                val length = minOf(CHUNK_SIZE.toLong(), left).toInt()
                raf.readFully(chunk, 0, length)
                chunks += chunkDigest(chunk, 0, length)
                left -= length
            }
        }
        RandomAccessFile(file, "r").use { raf ->
            feedRange(raf, 0L, firstEnd)
            feedRange(raf, secondStart, secondEnd)
            if (replacementEocd != null) feedBytes(replacementEocd)
        }
        val root = MessageDigest.getInstance(DIGEST_ALGORITHM)
        root.update(0x5a.toByte()); root.update(le32(chunks.size.toLong())); chunks.forEach(root::update)
        return root.digest()
    }

    private fun chunkDigest(data: ByteArray, offset: Int, length: Int): ByteArray {
        val digest = MessageDigest.getInstance(DIGEST_ALGORITHM)
        digest.update(0xa5.toByte()); digest.update(le32(length.toLong())); digest.update(data, offset, length)
        return digest.digest()
    }

    private fun buildSignedData(digest: ByteArray, certificate: ByteArray): ByteArray {
        val digestRecord = le32(DIGEST_ALGORITHM_ID.toLong()) + lp(digest)
        return lp(lp(digestRecord)) + lp(lp(certificate)) + lp(ByteArray(0))
    }
    private fun signData(data: ByteArray, key: PrivateKey): ByteArray = signature().run { initSign(key); update(data); sign() }
    private fun buildSigner(signedData: ByteArray, signature: ByteArray, publicKey: ByteArray): ByteArray {
        val signatureRecord = le32(DIGEST_ALGORITHM_ID.toLong()) + lp(signature)
        // APK Signature Scheme v2 requires the signer's SubjectPublicKeyInfo
        // after the signer attributes. An empty field produces a block that
        // looks structurally plausible to this parser but is rejected by
        // Android's platform/apksigner verifier.
        return lp(signedData) + lp(lp(signatureRecord)) + lp(publicKey)
    }
    private fun buildSigningBlock(signer: ByteArray): ByteArray {
        val v2Value = lp(lp(signer)); val pairSize = 4L + v2Value.size
        val body = ByteArrayOutputStream(); body.write(le64(pairSize)); body.write(le32(V2_ID.toLong())); body.write(v2Value)
        val size = 8L + body.size() + 8L + MAGIC.size
        return ByteArrayOutputStream().also { it.write(le64(size - 8)); it.write(body.toByteArray()); it.write(le64(size - 8)); it.write(MAGIC) }.toByteArray()
    }

    private data class ParsedSigner(
        val signedData: ByteArray,
        val signature: ByteArray,
        val digest: ByteArray,
        val certificate: ByteArray,
        val publicKey: ByteArray
    )
    private fun findV2Pair(file: File, start: Long, end: Long): ByteArray? {
        RandomAccessFile(file, "r").use { raf ->
            var at = start + 8
            while (at + 12 <= end) {
                val pairSize = readU64(raf, at)
                if (pairSize < 4 || pairSize > end - at - 8) throw IOException("invalid signing block pair")
                if (readU32(raf, at + 8) == V2_ID.toLong()) {
                    if (pairSize - 4 > Int.MAX_VALUE) throw IOException("v2 pair is too large")
                    return ByteArray((pairSize - 4).toInt()).also { raf.seek(at + 12); raf.readFully(it) }
                }
                at += 8 + pairSize
            }
        }
        return null
    }

    private fun parseSigner(v2Value: ByteArray): ParsedSigner {
        val signersBytes = Reader(v2Value).lp("v2 signers")
        val signerBytes = Reader(signersBytes).lp("signer"); val signer = Reader(signerBytes)
        val signedData = signer.lp("signed data"); val signatures = Reader(signer.lp("signatures")); val signatureRecord = Reader(signatures.lp("signature record"))
        if (signatureRecord.u32() != DIGEST_ALGORITHM_ID.toLong()) throw IOException("unsupported v2 signature algorithm")
        val signature = signatureRecord.lp("signature")
        val sd = Reader(signedData); val digests = Reader(sd.lp("digests")); val digestRecord = Reader(digests.lp("digest record"))
        if (digestRecord.u32() != DIGEST_ALGORITHM_ID.toLong()) throw IOException("unsupported v2 digest algorithm")
        val digest = digestRecord.lp("digest"); val certificates = Reader(sd.lp("certificates")); val certificate = certificates.lp("certificate"); sd.lp("signed data attributes")
        val publicKey = signer.lp("public key")
        return ParsedSigner(signedData, signature, digest, certificate, publicKey)
    }

    private class Reader(private val bytes: ByteArray) {
        private var offset = 0
        fun lp(label: String = "field"): ByteArray { val size = u32().toInt(); if (size < 0 || size > bytes.size - offset) throw IOException("invalid length-prefixed v2 $label at $offset (size=$size, remaining=${bytes.size - offset})"); return bytes.copyOfRange(offset, offset + size).also { offset += size } }
        fun u32(): Long { if (offset + 4 > bytes.size) throw IOException("truncated v2 field"); return readU32(bytes, offset).also { offset += 4 } }
    }

    private fun copyRange(input: RandomAccessFile, output: FileOutputStream, start: Long, end: Long) {
        input.seek(start); var left = end - start; val buffer = ByteArray(64 * 1024)
        while (left > 0) { val count = input.read(buffer, 0, minOf(buffer.size.toLong(), left).toInt()); if (count <= 0) throw IOException("unexpected end of unsigned APK"); output.write(buffer, 0, count); left -= count }
    }

    private fun findEocd(raf: RandomAccessFile): Long {
        val size = raf.length(); val scan = minOf(size, 22L + 65535L); val start = size - scan; val bytes = ByteArray(scan.toInt()); raf.seek(start); raf.readFully(bytes)
        for (i in bytes.size - EOCD_SIZE downTo 0) if (bytes[i] == 0x50.toByte() && bytes[i+1] == 0x4b.toByte() && bytes[i+2] == 5.toByte() && bytes[i+3] == 6.toByte() && i + EOCD_SIZE + readU16(bytes, i+20) == bytes.size) return start + i
        throw IOException("no valid EOCD found")
    }
    private fun readU16(raf: RandomAccessFile, offset: Long): Int = ByteArray(2).also { raf.seek(offset); raf.readFully(it) }.let { readU16(it, 0) }
    private fun readU16(data: ByteArray, offset: Int): Int = (data[offset].toInt() and 255) or ((data[offset+1].toInt() and 255) shl 8)
    private fun readU32(raf: RandomAccessFile, offset: Long): Long = ByteArray(4).also { raf.seek(offset); raf.readFully(it) }.let { readU32(it, 0) }
    private fun readU64(raf: RandomAccessFile, offset: Long): Long = ByteArray(8).also { raf.seek(offset); raf.readFully(it) }.let { ByteBuffer.wrap(it).order(ByteOrder.LITTLE_ENDIAN).long }
    private fun readU32(data: ByteArray, offset: Int): Long = (data[offset].toLong() and 255) or ((data[offset+1].toLong() and 255) shl 8) or ((data[offset+2].toLong() and 255) shl 16) or ((data[offset+3].toLong() and 255) shl 24)
    private fun putU32(data: ByteArray, offset: Int, value: Long) { System.arraycopy(le32(value), 0, data, offset, 4) }
    private fun le32(value: Long): ByteArray = ByteBuffer.allocate(4).order(ByteOrder.LITTLE_ENDIAN).putInt(value.toInt()).array()
    private fun le64(value: Long): ByteArray = ByteBuffer.allocate(8).order(ByteOrder.LITTLE_ENDIAN).putLong(value).array()
    private fun lp(data: ByteArray): ByteArray = le32(data.size.toLong()) + data
}
