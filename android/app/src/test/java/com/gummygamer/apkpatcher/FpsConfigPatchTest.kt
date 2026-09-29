package com.gummygamer.apkpatcher

import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class FpsConfigPatchTest {
    private fun hook(initialFps: Int = 60): ByteArray {
        val marker = FpsConfigPatch.MARKER.toByteArray(Charsets.US_ASCII)
        val padLen = FpsConfigPatch.MARKER_LEN - marker.size
        val markerBlock = marker + ByteArray(padLen)
        val fpsBytes = byteArrayOf(
            (initialFps and 0xFF).toByte(),
            ((initialFps shr 8) and 0xFF).toByte(),
            ((initialFps shr 16) and 0xFF).toByte(),
            ((initialFps shr 24) and 0xFF).toByte()
        )
        return ByteArray(64) { 0x11 } + markerBlock + fpsBytes + ByteArray(64) { 0x22 }
    }

    private fun readFps(data: ByteArray): Int {
        val marker = FpsConfigPatch.MARKER.toByteArray(Charsets.US_ASCII)
        val at = String(data, Charsets.ISO_8859_1).indexOf(FpsConfigPatch.MARKER)
        val bodyStart = at + FpsConfigPatch.MARKER_LEN
        return (data[bodyStart].toInt() and 0xFF) or
                ((data[bodyStart + 1].toInt() and 0xFF) shl 8) or
                ((data[bodyStart + 2].toInt() and 0xFF) shl 16) or
                ((data[bodyStart + 3].toInt() and 0xFF) shl 24)
    }

    @Test
    fun `patch writes target fps 30 and touches nothing else`() {
        val original = hook(60)
        val result = FpsConfigPatch.patch(original, PatchRequest.FPS_30)
        assertTrue(result.patched)
        assertEquals(30, readFps(result.data))
        assertEquals(original.size, result.data.size)

        val fpsStart = 64 + FpsConfigPatch.MARKER_LEN
        assertArrayEquals(original.copyOfRange(0, fpsStart), result.data.copyOfRange(0, fpsStart))
        assertArrayEquals(original.copyOfRange(fpsStart + 4, original.size), result.data.copyOfRange(fpsStart + 4, result.data.size))
    }

    @Test
    fun `patching to current fps reports not changed`() {
        val original = hook(60)
        val result = FpsConfigPatch.patch(original, PatchRequest.FPS_60)
        assertFalse(result.patched)
        assertEquals(60, readFps(result.data))
        assertArrayEquals(original, result.data)
    }

    @Test
    fun `repatching toggles between 30 and 60 cleanly`() {
        val original = hook(60)
        val to30 = FpsConfigPatch.patch(original, PatchRequest.FPS_30)
        assertTrue(to30.patched)
        assertEquals(30, readFps(to30.data))

        val to60 = FpsConfigPatch.patch(to30.data, PatchRequest.FPS_60)
        assertTrue(to60.patched)
        assertEquals(60, readFps(to60.data))
        assertArrayEquals(original, to60.data)
    }

    @Test
    fun `legacy hook fallback rewrites instructions for 30 FPS`() {
        // Construct a buffer containing legacy signatures
        val mov60 = byteArrayOf(0x80.toByte(), 0x07.toByte(), 0x80.toByte(), 0x52.toByte())
        val sigFps = byteArrayOf(0x09.toByte(), 0x21.toByte(), 0x8c.toByte(), 0x52.toByte(), 0x89.toByte(), 0x36.toByte(), 0xa0.toByte(), 0x72.toByte())
        val sigVsync = byteArrayOf(0x09.toByte(), 0x38.toByte(), 0x8e.toByte(), 0x52.toByte(), 0x49.toByte(), 0x2d.toByte(), 0xa0.toByte(), 0x72.toByte())

        val legacyBuffer = ByteArray(32) { 0x00 } +
                mov60 +
                ByteArray(16) { 0x00 } +
                sigFps + ByteArray(20) { 0x00 } + byteArrayOf(0x01, 0x02, 0x03, 0x04) + // bl at +28
                ByteArray(16) { 0x00 } +
                sigVsync + ByteArray(24) { 0x00 } + byteArrayOf(0x05, 0x06, 0x07, 0x08) // bl at +32

        val result = FpsConfigPatch.patch(legacyBuffer, PatchRequest.FPS_30)
        assertTrue(result.patched)
        assertTrue(result.message.contains("legacy hook"))

        // Check mov w0, #30
        val mov30 = byteArrayOf(0xc0.toByte(), 0x03.toByte(), 0x80.toByte(), 0x52.toByte())
        val nop = byteArrayOf(0x1f.toByte(), 0x20.toByte(), 0x03.toByte(), 0xd5.toByte())

        val actualMov = result.data.copyOfRange(32, 36)
        assertArrayEquals(mov30, actualMov)

        // Check NOP at bl locations
        val hook1Nop = result.data.copyOfRange(32 + 4 + 16 + 28, 32 + 4 + 16 + 32)
        assertArrayEquals(nop, hook1Nop)

        val hook2Start = 32 + 4 + 16 + sigFps.size + 20 + 4 + 16
        val hook2Nop = result.data.copyOfRange(hook2Start + 32, hook2Start + 36)
        assertArrayEquals(nop, hook2Nop)
    }

    @Test
    fun `ambiguous marker is refused`() {
        val hook = hook(60)
        val twice = hook + hook
        val result = FpsConfigPatch.patch(twice, PatchRequest.FPS_30)
        assertFalse(result.patched)
        assertTrue(result.message.contains("Ambiguous marker"))
    }

    @Test
    fun `truncated marker block is refused`() {
        val marker = FpsConfigPatch.MARKER.toByteArray(Charsets.US_ASCII)
        val truncated = ByteArray(10) + marker + ByteArray(2) // Missing full marker pad + 4 bytes fps
        val result = FpsConfigPatch.patch(truncated, PatchRequest.FPS_30)
        assertFalse(result.patched)
        assertTrue(result.message.contains("truncated"))
    }

    @Test
    fun `invalid target framerate is rejected`() {
        val result = FpsConfigPatch.patch(hook(60), 120)
        assertFalse(result.patched)
        assertTrue(result.message.contains("Unknown framerate"))
    }

    @Test
    fun `request validation handles targetFps correctly`() {
        fun request(abi: String, targetFps: Int) = PatchRequest(
            sourceApkUri = "content://x", outputName = "o.apk", abi = abi,
            serverMode = PatchRequest.BUNDLED, serverHost = "127.0.0.1", serverPort = 8080,
            scheme = "http", keepOtherAbi = false, patchedIl2cppUri = "", autoPatchIl2cpp = true,
            keystoreUri = "", keystorePassword = CharArray(0), keyPassword = CharArray(0),
            keyAlias = "", offerInstall = false, targetFps = targetFps
        )

        // 60 FPS on ARM64 is valid with no warnings
        val arm64_60 = request(PatchRequest.ARM64, PatchRequest.FPS_60).validate()
        assertTrue(arm64_60.isValid)
        assertTrue(arm64_60.warnings.isEmpty())

        // 30 FPS on ARM64 is valid with no warnings
        val arm64_30 = request(PatchRequest.ARM64, PatchRequest.FPS_30).validate()
        assertTrue(arm64_30.isValid)
        assertTrue(arm64_30.warnings.isEmpty())

        // 30 FPS on ARMv7 is valid
        val armv7_30 = request(PatchRequest.ARMV7, PatchRequest.FPS_30).validate()
        assertTrue(armv7_30.isValid)

        // 60 FPS on ARMv7 produces warning that 60 FPS is ARM64 only
        val armv7_60 = request(PatchRequest.ARMV7, PatchRequest.FPS_60).validate()
        assertTrue(armv7_60.isValid)
        assertTrue(armv7_60.warnings.any { it.contains("60 FPS unlock is only available for 64-bit") })

        // Unsupported FPS value produces validation error
        val invalidFps = request(PatchRequest.ARM64, 45).validate()
        assertFalse(invalidFps.isValid)
        assertTrue(invalidFps.errors.any { it.contains("Target framerate must be 30 or 60 FPS") })
    }
}
