package com.gummygamer.apkpatcher

import org.junit.Assert.*
import org.junit.Test
import java.io.ByteArrayOutputStream
import java.io.File
import java.nio.ByteBuffer
import java.nio.ByteOrder

/**
 * Comprehensive unit and integration tests for the APK patcher engine.
 *
 * Mirrors the test_pipeline.lbl + test_runner.lbl conventions from tools/apk_patcher_gui.
 * All tests use synthetic or authorized fixtures; no proprietary APK, keystore, or
 * media file is committed.
 *
 * Revision: android/ development branch
 */
class PatcherEngineTest {

    // ═══════════════════════════════════════════════════════════════════════
    // Metadata Patch Tests
    // ═══════════════════════════════════════════════════════════════════════

    @Test
    fun `metadata literal replacements are correct`() {
        val replacements = MetadataPatch.literalReplacements("127.0.0.1", 8080)
        assertEquals(4, replacements.size)
        assertEquals("tf-odr.mcoc-cdn.cn", replacements[0].old)
        assertEquals("127.0.0.1:8080", replacements[0].new)
        assertEquals("wss://gametalk.sparx.io:30443", replacements[3].old)
        assertEquals("wss://127.0.0.1:30443", replacements[3].new)
    }

    @Test
    fun `metadata literal replacements with custom host`() {
        val replacements = MetadataPatch.literalReplacements("10.0.2.2", 8123)
        assertEquals("10.0.2.2:8123", replacements[0].new)
        assertEquals("wss://10.0.2.2:30443", replacements[3].new)
    }

    @Test
    fun `metadata patch fails on too-short data`() {
        val result = MetadataPatch.patch(ByteArray(10), "127.0.0.1", 8080)
        assertFalse(result.isSuccess)
        assertNotNull(result.error)
    }

    @Test
    fun `metadata patch with valid synthetic metadata`() {
        val metadata = buildSyntheticMetadataForTest()
        val result = MetadataPatch.patch(metadata, "10.0.2.2", 8080)
        assertTrue("patch should succeed: ${result.error}", result.isSuccess)
        assertEquals(4, result.changed.size)
        assertTrue(result.changed.contains("tf-odr.mcoc-cdn.cn"))

        val patched = String(result.data!!, Charsets.UTF_8)
        assertTrue(patched.contains("10.0.2.2:8080"))
        assertTrue(patched.contains("wss://10.0.2.2:30443"))
    }

    @Test
    fun `metadata patch fails when literal is longer than slot`() {
        val buf = ByteBuffer.allocate(128).order(ByteOrder.LITTLE_ENDIAN)
        buf.putInt(0x464d5447)
        buf.putInt(1)
        buf.putInt(24)
        buf.putInt(8)  // literalCount = 1 entry
        buf.putInt(32) // dataOffset
        buf.position(24)
        buf.putInt(3); buf.putInt(0)
        buf.position(32)
        buf.put("abc".toByteArray(Charsets.UTF_8))

        val result = MetadataPatch.patch(buf.array(), "long-hostname.example.com", 8443)
        assertFalse("should fail because replacement is longer than slot", result.isSuccess)
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Sparx Patch Tests
    // ═══════════════════════════════════════════════════════════════════════

    @Test
    fun `sparx manifest replaces endpoint`() {
        val original = "prefix https://tform-0901-hzlhiniyfcwf.tf-cdn.net suffix"
        val data = original.toByteArray(Charsets.UTF_8)
        val result = SparxPatch.patch(data, "10.0.2.2", "http", 8080)
        assertTrue(result.isSuccess)
        val patched = String(result.data!!, Charsets.UTF_8)
        assertTrue(patched.contains("http://10.0.2.2:8080"))
    }

    @Test
    fun `sparx manifest fails on missing endpoint`() {
        val data = "no endpoint here".toByteArray(Charsets.UTF_8)
        val result = SparxPatch.patch(data, "127.0.0.1", "http", 8080)
        assertFalse(result.isSuccess)
    }

    @Test
    fun `sparx manifest fails on multiple endpoints`() {
        val dup = "https://tform-0901-hzlhiniyfcwf.tf-cdn.net and again https://tform-0901-hzlhiniyfcwf.tf-cdn.net"
        val result = SparxPatch.patch(dup.toByteArray(Charsets.UTF_8), "127.0.0.1", "http", 8080)
        assertFalse(result.isSuccess)
        assertTrue(result.error!!.contains("2 occurrences"))
    }

    @Test
    fun `sparx manifest preserves length change`() {
        val original = "https://tform-0901-hzlhiniyfcwf.tf-cdn.net"
        val data = original.toByteArray(Charsets.UTF_8)
        val result = SparxPatch.patch(data, "1.2.3.4", "http", 80)
        assertTrue(result.isSuccess)
        assertEquals("http://1.2.3.4:80", String(result.data!!, Charsets.UTF_8))
        assertTrue(result.data!!.size < data.size)
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Endpoint Config Tests
    // ═══════════════════════════════════════════════════════════════════════

    @Test
    fun `endpoint config replaces and pads`() {
        val original = """{"Default":{"Prod":"https://tform-0901-hzlhiniyfcwf.tf-cdn.net"}}"""
        val data = original.toByteArray(Charsets.UTF_8)
        val result = EndpointConfigPatch.patch(data, "10.0.2.2", "http", 8080)
        assertTrue(result.isSuccess)
        assertEquals(data.size, result.data!!.size) // same size due to padding
        val patched = String(result.data!!, Charsets.UTF_8)
        assertTrue(patched.contains("http://10.0.2.2:8080"))
    }

    @Test
    fun `endpoint config fails on too-long replacement`() {
        val original = """{"Default":{"Prod":"short"}}"""
        val data = original.toByteArray(Charsets.UTF_8)
        val result = EndpointConfigPatch.patch(data, "a-very-long-hostname-that-exceeds-the-original", "https", 8443)
        assertFalse(result.isSuccess)
    }

    @Test
    fun `endpoint config fails on unexpected endpoint`() {
        val data = """{"Default":{"Prod":"https://different.example.com"}}""".toByteArray(Charsets.UTF_8)
        val result = EndpointConfigPatch.patch(data, "127.0.0.1", "http", 8080)
        assertFalse(result.isSuccess)
    }

    @Test
    fun `endpoint config fails on multiple occurrences`() {
        val dup = """{"Default":{"Prod":"https://tform-0901-hzlhiniyfcwf.tf-cdn.net"},"B":{"Prod":"https://tform-0901-hzlhiniyfcwf.tf-cdn.net"}}"""
        val result = EndpointConfigPatch.patch(dup.toByteArray(Charsets.UTF_8), "127.0.0.1", "http", 8080)
        assertFalse(result.isSuccess)
        assertTrue(result.error!!.contains("occurrences"))
    }

    // ═══════════════════════════════════════════════════════════════════════
    // IL2CPP Patch Tests
    // ═══════════════════════════════════════════════════════════════════════

    @Test
    fun `arm64 has 16 patch sites`() {
        assertEquals(16, Il2cppPatch.arm64Sites.size)
    }

    @Test
    fun `armv7 has 16 patch sites`() {
        assertEquals(16, Il2cppPatch.armv7Sites.size)
    }

    @Test
    fun `patch sites are at valid offsets`() {
        for (site in Il2cppPatch.arm64Sites) {
            assertTrue("arm64 site ${site.name} at ${site.offset}: patch bytes must be > 0",
                site.patchBytes.isNotEmpty())
        }
        for (site in Il2cppPatch.armv7Sites) {
            assertTrue("armv7 site ${site.name} at ${site.offset}: patch bytes must be > 0",
                site.patchBytes.isNotEmpty())
        }
    }

    @Test
    fun `elf detection identifies arm64`() {
        val data = ByteArray(64)
        data[0] = 0x7f; data[1] = 'E'.code.toByte(); data[2] = 'L'.code.toByte(); data[3] = 'F'.code.toByte()
        data[4] = 2 // 64-bit
        data[18] = 183.toByte() // AArch64 machine (little-endian low byte)
        assertEquals(PatchRequest.ARM64, Il2cppPatch.elfAbi(data))
    }

    @Test
    fun `elf detection identifies armv7`() {
        val data = ByteArray(64)
        data[0] = 0x7f; data[1] = 'E'.code.toByte(); data[2] = 'L'.code.toByte(); data[3] = 'F'.code.toByte()
        data[4] = 1 // 32-bit
        data[18] = 40.toByte() // ARM machine (little-endian low byte)
        assertEquals(PatchRequest.ARMV7, Il2cppPatch.elfAbi(data))
    }

    @Test
    fun `elf detection returns null for non-ELF`() {
        val data = "not an ELF file".toByteArray(Charsets.UTF_8)
        assertNull(Il2cppPatch.elfAbi(data))
    }

    @Test
    fun `elf detection returns null for short data`() {
        assertNull(Il2cppPatch.elfAbi(ByteArray(10)))
    }

    @Test
    fun `elf detection with wrong class byte`() {
        val data = ByteArray(64)
        data[0] = 0x7f; data[1] = 'E'.code.toByte(); data[2] = 'L'.code.toByte(); data[3] = 'F'.code.toByte()
        data[4] = 3 // neither 32-bit nor 64-bit
        assertNull(Il2cppPatch.elfAbi(data))
    }

    @Test
    fun `elf detection with unknown machine`() {
        val data = ByteArray(64)
        data[0] = 0x7f; data[1] = 'E'.code.toByte(); data[2] = 'L'.code.toByte(); data[3] = 'F'.code.toByte()
        data[4] = 2 // 64-bit
        data[18] = 62.toByte() // x86-64 machine
        assertNull(Il2cppPatch.elfAbi(data))
    }

    @Test
    fun `hex to bytes roundtrip`() {
        val hex = "20008052c0035fd6"
        val bytes = Il2cppPatch.hexToBytes(hex)
        assertEquals(8, bytes.size)
        assertEquals(0x20.toByte(), bytes[0])
        assertEquals(0xd6.toByte(), bytes[7])
    }

    @Test
    fun `hex to bytes empty`() {
        assertEquals(0, Il2cppPatch.hexToBytes("").size)
    }

    @Test(expected = IllegalArgumentException::class)
    fun `hex to bytes rejects odd length`() {
        Il2cppPatch.hexToBytes("abc")
    }

    @Test(expected = IllegalArgumentException::class)
    fun `hex to bytes rejects invalid characters`() {
        Il2cppPatch.hexToBytes("zz")
    }

    @Test
    fun `patch sites reject an unsupported ABI`() {
        val result = Il2cppPatch.applySites(ByteArray(32), "x86")
        assertFalse(result.isSuccess)
        assertTrue(result.error!!.contains("unsupported ABI"))
    }

    @Test
    fun `applySites is idempotent for arm64`() {
        val maxOffset = Il2cppPatch.arm64Sites.maxOf { it.offset + it.patchBytes.size }
        val data = ByteArray(maxOffset + 1024)
        data[0] = 0x7f; data[1] = 'E'.code.toByte(); data[2] = 'L'.code.toByte(); data[3] = 'F'.code.toByte()
        data[4] = 2

        val result1 = Il2cppPatch.applySites(data, PatchRequest.ARM64)
        assertTrue("applySites should succeed: ${result1.error}", result1.isSuccess)

        val result2 = Il2cppPatch.applySites(result1.data, PatchRequest.ARM64)
        assertTrue(result2.isSuccess)
        assertArrayEquals(result1.data, result2.data)
    }

    @Test
    fun `applySites is idempotent for armv7`() {
        val maxOffset = Il2cppPatch.armv7Sites.maxOf { it.offset + it.patchBytes.size }
        val data = ByteArray(maxOffset + 1024)
        data[0] = 0x7f; data[1] = 'E'.code.toByte(); data[2] = 'L'.code.toByte(); data[3] = 'F'.code.toByte()
        data[4] = 1

        val result1 = Il2cppPatch.applySites(data, PatchRequest.ARMV7)
        assertTrue("applySites armv7 should succeed: ${result1.error}", result1.isSuccess)

        val result2 = Il2cppPatch.applySites(result1.data, PatchRequest.ARMV7)
        assertTrue(result2.isSuccess)
        assertArrayEquals(result1.data, result2.data)
    }

    @Test
    fun `applySites fails on too-small file`() {
        val data = ByteArray(100)
        val result = Il2cppPatch.applySites(data, PatchRequest.ARM64)
        assertFalse(result.isSuccess)
        assertTrue(result.error!!.contains("too small"))
    }

    @Test
    fun `autoPatch arm64 returns patched data and injects hook name`() {
        val maxOffset = maxOf(
            Il2cppPatch.arm64Sites.maxOf { it.offset + it.patchBytes.size },
            45781603
        )
        val data = ByteArray(maxOffset + 4096)
        data[0] = 0x7f; data[1] = 'E'.code.toByte(); data[2] = 'L'.code.toByte(); data[3] = 'F'.code.toByte()
        data[4] = 2
        data[18] = 183.toByte()

        val result = Il2cppPatch.autoPatch(data, PatchRequest.ARM64)
        assertTrue("autoPatch arm64 should succeed: ${result.error}", result.isSuccess)
        assertNotNull(result.data)

        val hookName = "libdothook.so".toByteArray(Charsets.UTF_8)
        var found = false
        for (i in 0 until result.data.size - hookName.size) {
            if (result.data.sliceArray(i until i + hookName.size).contentEquals(hookName)) {
                found = true
                break
            }
        }
        assertTrue("libdothook.so should be present in auto-patched output", found)
    }

    @Test
    fun `checkOfflineReady detects missing hook reference`() {
        val maxOffset = Il2cppPatch.arm64Sites.maxOf { it.offset + it.patchBytes.size }
        val data = ByteArray(maxOffset + 1024)
        data[0] = 0x7f; data[1] = 'E'.code.toByte(); data[2] = 'L'.code.toByte(); data[3] = 'F'.code.toByte()
        data[4] = 2
        data[18] = 183.toByte()

        val result = Il2cppPatch.checkOfflineReady(data, PatchRequest.ARM64)
        assertFalse(result.isSuccess)
        assertTrue(result.error!!.contains("bundled server hook"))
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Request Validation Tests
    // ═══════════════════════════════════════════════════════════════════════

    @Test
    fun `valid request passes validation`() {
        val req = PatchRequest(
            sourceApkUri = "content://test/source.apk",
            outputName = "output.apk",
            abi = PatchRequest.ARM64,
            serverMode = PatchRequest.BUNDLED,
            serverHost = "127.0.0.1",
            serverPort = 8080,
            scheme = "http",
            keepOtherAbi = false,
            patchedIl2cppUri = "",
            autoPatchIl2cpp = true,
            keystoreUri = "",
            keystorePassword = CharArray(0),
            keyPassword = CharArray(0),
            keyAlias = "",
            offerInstall = false
        )
        assertTrue(req.validate().isValid)
    }

    @Test
    fun `valid separate server request`() {
        val req = PatchRequest(
            sourceApkUri = "content://test/source.apk",
            outputName = "output.apk",
            abi = PatchRequest.ARM64,
            serverMode = PatchRequest.SEPARATE,
            serverHost = "10.0.2.2",
            serverPort = 8080,
            scheme = "https",
            keepOtherAbi = true,
            patchedIl2cppUri = "content://test/patched.so",
            autoPatchIl2cpp = false,
            keystoreUri = "",
            keystorePassword = CharArray(0),
            keyPassword = CharArray(0),
            keyAlias = "mykey",
            offerInstall = true
        )
        assertTrue(req.validate().isValid)
    }

    @Test
    fun `invalid ABI fails validation`() {
        val req = PatchRequest(
            sourceApkUri = "", outputName = "", abi = "x86",
            serverMode = PatchRequest.BUNDLED, serverHost = "127.0.0.1",
            serverPort = 8080, scheme = "http", keepOtherAbi = false,
            patchedIl2cppUri = "", autoPatchIl2cpp = false,
            keystoreUri = "", keystorePassword = CharArray(0),
            keyPassword = CharArray(0), keyAlias = "", offerInstall = false
        )
        assertFalse(req.validate().isValid)
    }

    @Test
    fun `bundled mode requires http`() {
        val req = PatchRequest(
            sourceApkUri = "", outputName = "", abi = PatchRequest.ARM64,
            serverMode = PatchRequest.BUNDLED, serverHost = "127.0.0.1",
            serverPort = 8080, scheme = "https", keepOtherAbi = false,
            patchedIl2cppUri = "", autoPatchIl2cpp = false,
            keystoreUri = "", keystorePassword = CharArray(0),
            keyPassword = CharArray(0), keyAlias = "", offerInstall = false
        )
        assertFalse(req.validate().isValid)
    }

    @Test
    fun `bundled mode requires 127 dot 0 dot 0 dot 1`() {
        val req = PatchRequest(
            sourceApkUri = "", outputName = "", abi = PatchRequest.ARM64,
            serverMode = PatchRequest.BUNDLED, serverHost = "10.0.0.1",
            serverPort = 8080, scheme = "http", keepOtherAbi = false,
            patchedIl2cppUri = "", autoPatchIl2cpp = false,
            keystoreUri = "", keystorePassword = CharArray(0),
            keyPassword = CharArray(0), keyAlias = "", offerInstall = false
        )
        val result = req.validate()
        assertFalse(result.isValid)
        assertTrue(result.errors.any { it.contains("127.0.0.1") })
    }

    @Test
    fun `separate server requires non-blank host`() {
        val req = PatchRequest(
            sourceApkUri = "content://test/source.apk",
            outputName = "output.apk",
            abi = PatchRequest.ARM64,
            serverMode = PatchRequest.SEPARATE,
            serverHost = "",
            serverPort = 8080,
            scheme = "http",
            keepOtherAbi = false,
            patchedIl2cppUri = "content://test/patched.so",
            autoPatchIl2cpp = false,
            keystoreUri = "",
            keystorePassword = CharArray(0),
            keyPassword = CharArray(0),
            keyAlias = "",
            offerInstall = false
        )
        val result = req.validate()
        assertFalse(result.isValid)
        assertTrue(result.errors.any { it.contains("host") })
    }

    @Test
    fun `separate server warns about loopback`() {
        val req = PatchRequest(
            sourceApkUri = "content://test/source.apk",
            outputName = "output.apk",
            abi = PatchRequest.ARM64,
            serverMode = PatchRequest.SEPARATE,
            serverHost = "127.0.0.1",
            serverPort = 8080,
            scheme = "http",
            keepOtherAbi = false,
            patchedIl2cppUri = "content://test/patched.so",
            autoPatchIl2cpp = false,
            keystoreUri = "",
            keystorePassword = CharArray(0),
            keyPassword = CharArray(0),
            keyAlias = "",
            offerInstall = false
        )
        val result = req.validate()
        assertTrue(result.isValid)
        assertTrue(result.warnings.any { it.contains("127.0.0.1") && it.contains("phone") })
    }

    @Test
    fun `invalid scheme fails validation`() {
        val req = PatchRequest(
            sourceApkUri = "", outputName = "", abi = PatchRequest.ARM64,
            serverMode = PatchRequest.SEPARATE, serverHost = "10.0.0.1",
            serverPort = 8080, scheme = "ftp", keepOtherAbi = false,
            patchedIl2cppUri = "content://test/patched.so", autoPatchIl2cpp = false,
            keystoreUri = "", keystorePassword = CharArray(0),
            keyPassword = CharArray(0), keyAlias = "", offerInstall = false
        )
        assertFalse(req.validate().isValid)
    }

    @Test
    fun `port out of range fails validation`() {
        val req = PatchRequest(
            sourceApkUri = "", outputName = "", abi = PatchRequest.ARM64,
            serverMode = PatchRequest.BUNDLED, serverHost = "127.0.0.1",
            serverPort = 0, scheme = "http", keepOtherAbi = false,
            patchedIl2cppUri = "", autoPatchIl2cpp = false,
            keystoreUri = "", keystorePassword = CharArray(0),
            keyPassword = CharArray(0), keyAlias = "", offerInstall = false
        )
        assertFalse(req.validate().isValid)

        val req2 = req.copy(serverPort = 99999)
        assertFalse(req2.validate().isValid)
    }

    @Test
    fun `armv7 needs patched il2cpp or auto-patch`() {
        val req = PatchRequest(
            sourceApkUri = "content://test/source.apk",
            outputName = "output.apk",
            abi = PatchRequest.ARMV7,
            serverMode = PatchRequest.BUNDLED,
            serverHost = "127.0.0.1",
            serverPort = 8080,
            scheme = "http",
            keepOtherAbi = false,
            patchedIl2cppUri = "",
            autoPatchIl2cpp = false,
            keystoreUri = "",
            keystorePassword = CharArray(0),
            keyPassword = CharArray(0),
            keyAlias = "",
            offerInstall = false
        )
        val result = req.validate()
        assertFalse(result.isValid)
        assertTrue(result.errors.any { it.contains("32-bit") || it.contains("TFTFHOOK") })
    }

    @Test
    fun `armv7 auto-patch passes validation`() {
        val req = PatchRequest(
            sourceApkUri = "content://test/source.apk",
            outputName = "output.apk",
            abi = PatchRequest.ARMV7,
            serverMode = PatchRequest.BUNDLED,
            serverHost = "127.0.0.1",
            serverPort = 8080,
            scheme = "http",
            keepOtherAbi = false,
            patchedIl2cppUri = "",
            autoPatchIl2cpp = true,
            keystoreUri = "",
            keystorePassword = CharArray(0),
            keyPassword = CharArray(0),
            keyAlias = "",
            offerInstall = false
        )
        assertTrue(req.validate().isValid)
    }

    @Test
    fun `armv7 without keep-other-abi warns`() {
        val req = PatchRequest(
            sourceApkUri = "content://test/source.apk",
            outputName = "output.apk",
            abi = PatchRequest.ARMV7,
            serverMode = PatchRequest.BUNDLED,
            serverHost = "127.0.0.1",
            serverPort = 8080,
            scheme = "http",
            keepOtherAbi = false,
            patchedIl2cppUri = "content://test/patched.so",
            autoPatchIl2cpp = false,
            keystoreUri = "",
            keystorePassword = CharArray(0),
            keyPassword = CharArray(0),
            keyAlias = "",
            offerInstall = false
        )
        val result = req.validate()
        assertTrue(result.isValid)
        assertTrue(result.warnings.any { it.contains("32-bit") && it.contains("arm64") })
    }

    @Test
    fun `invalid server mode fails validation`() {
        val req = PatchRequest(
            sourceApkUri = "", outputName = "", abi = PatchRequest.ARM64,
            serverMode = "cloud", serverHost = "127.0.0.1",
            serverPort = 8080, scheme = "http", keepOtherAbi = false,
            patchedIl2cppUri = "", autoPatchIl2cpp = false,
            keystoreUri = "", keystorePassword = CharArray(0),
            keyPassword = CharArray(0), keyAlias = "", offerInstall = false
        )
        assertFalse(req.validate().isValid)
    }

    // ═══════════════════════════════════════════════════════════════════════
    // ZIP Reader Tests
    // ═══════════════════════════════════════════════════════════════════════

    @Test
    fun `zip reader parses valid minimal ZIP`() {
        val zipBytes = buildMinimalZip(listOf(
            "hello.txt" to "Hello, world!".toByteArray(Charsets.UTF_8)
        ))
        val reader = ZipReader.open(ByteArrayChannel(zipBytes))
        assertEquals(1, reader.entries.size)
        assertEquals("hello.txt", reader.entries[0].name)
        val data = reader.readEntryDataInflated(0)
        assertEquals("Hello, world!", String(data, Charsets.UTF_8))
        reader.close()
    }

    @Test
    fun `zip reader handles multiple entries`() {
        val zipBytes = buildMinimalZip(listOf(
            "a.txt" to "AAA".toByteArray(Charsets.UTF_8),
            "b.txt" to "BBB".toByteArray(Charsets.UTF_8),
            "lib/arm64-v8a/libtest.so" to ByteArray(1024)
        ))
        val reader = ZipReader.open(ByteArrayChannel(zipBytes))
        assertEquals(3, reader.entries.size)
        val names = reader.entries.map { it.name }
        assertTrue(names.contains("a.txt"))
        assertTrue(names.contains("b.txt"))
        assertTrue(names.contains("lib/arm64-v8a/libtest.so"))
        reader.close()
    }

    @Test
    fun `zip reader find returns correct index`() {
        val zipBytes = buildMinimalZip(listOf(
            "a.txt" to "A".toByteArray(Charsets.UTF_8),
            "b.txt" to "B".toByteArray(Charsets.UTF_8)
        ))
        val reader = ZipReader.open(ByteArrayChannel(zipBytes))
        assertEquals(0, reader.find("a.txt"))
        assertEquals(1, reader.find("b.txt"))
        assertEquals(-1, reader.find("c.txt"))
        reader.close()
    }

    @Test
    fun `zip reader rejects empty file`() {
        try {
            ZipReader.open(ByteArrayChannel(ByteArray(0)))
            fail("should have thrown")
        } catch (e: ZipException) {
            assertTrue(e.message!!.contains("too small"))
        }
    }

    @Test
    fun `zip reader rejects file without EOCD`() {
        try {
            ZipReader.open(ByteArrayChannel(ByteArray(100)))
            fail("should have thrown")
        } catch (e: ZipException) {
            assertTrue(e.message!!.contains("end-of-central-directory") || e.message!!.contains("EOCD"))
        }
    }

    @Test
    fun `zip reader rejects multi-disk ZIP`() {
        val zipBytes = buildMinimalZip(listOf("a.txt" to "A".toByteArray(Charsets.UTF_8)))
        val eocdOff = findEocdInBytes(zipBytes)
        zipBytes[eocdOff + 8] = 99 // corrupt disk-entry count
        try {
            ZipReader.open(ByteArrayChannel(zipBytes))
            fail("should have thrown for multi-disk")
        } catch (e: ZipException) {
            assertTrue(e.message!!.contains("Multi-disk"))
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // ZIP Writer Tests
    // ═══════════════════════════════════════════════════════════════════════

    @Test
    fun `zip writer produces valid ZIP with single entry`() {
        val bos = ByteArrayOutputStream()
        val writer = ZipWriter(bos)
        writer.writeStored("test.txt", "content".toByteArray(Charsets.UTF_8))
        writer.finish()

        val zipBytes = bos.toByteArray()
        assertTrue(zipBytes.size > 0)

        val reader = ZipReader.open(ByteArrayChannel(zipBytes))
        assertEquals(1, reader.entries.size)
        assertEquals("test.txt", reader.entries[0].name)
        assertEquals("content", String(reader.readEntryDataInflated(0), Charsets.UTF_8))
        reader.close()
    }

    @Test
    fun `zip writer aligns dot so entries on 4-byte boundary`() {
        val bos = ByteArrayOutputStream()
        val writer = ZipWriter(bos, soAlignment = 4)

        // Write a small entry first, then a .so entry
        writer.writeStored("small.txt", ByteArray(1))
        writer.writeStored("lib/arm64-v8a/libtest.so", "hook".toByteArray(Charsets.UTF_8))
        writer.finish()

        val zipBytes = bos.toByteArray()
        val reader = ZipReader.open(ByteArrayChannel(zipBytes))
        val soEntry = reader.entries.find { it.name.endsWith(".so") }!!
        // The data offset of the .so entry should be 4-byte aligned
        assertEquals("so entry data offset should be 4-byte aligned", 0, soEntry.dataOffset % 4)
        reader.close()
    }

    @Test
    fun `zip writer stores and aligns resources arsc`() {
        val bos = ByteArrayOutputStream()
        val writer = ZipWriter(bos, soAlignment = 4)
        writer.writeStored("prefix.bin", ByteArray(3))
        val resources = byteArrayOf(1, 2, 3, 4, 5)
        writer.writeStored("resources.arsc", resources)
        writer.finish()

        val reader = ZipReader.open(ByteArrayChannel(bos.toByteArray()))
        val entry = reader.entries.single { it.name == PatcherEngine.RESOURCES_NAME }
        assertEquals("resources.arsc must be stored", 0, entry.compressType)
        assertEquals("resources.arsc data must be 4-byte aligned", 0L, entry.dataOffset % 4L)
        assertArrayEquals(resources, reader.readEntryDataInflated(reader.find("resources.arsc")))
        reader.close()
    }

    @Test
    fun `zip writer defaults native libraries to 16KiB page alignment`() {
        val bos = ByteArrayOutputStream()
        val writer = ZipWriter(bos)
        writer.writeStored("assets/prefix.bin", ByteArray(37))
        writer.writeStored("lib/arm64-v8a/libtest.so", ByteArray(11))
        writer.finish()

        val reader = ZipReader.open(ByteArrayChannel(bos.toByteArray()))
        val soEntry = reader.entries.single { it.name.endsWith(".so") }
        assertEquals("native library data must be 16KiB page aligned", 0, soEntry.dataOffset % (16 * 1024))
        reader.close()
    }

    @Test
    fun `zip writer roundtrip preserves entry data`() {
        val originalData = ByteArray(4096) { (it % 256).toByte() }
        val bos = ByteArrayOutputStream()
        val writer = ZipWriter(bos)
        writer.writeStored("data.bin", originalData)
        writer.finish()

        val reader = ZipReader.open(ByteArrayChannel(bos.toByteArray()))
        val readData = reader.readEntryDataInflated(0)
        assertArrayEquals(originalData, readData)
        reader.close()
    }

    @Test
    fun `zip writer produces correct EOCD`() {
        val bos = ByteArrayOutputStream()
        val writer = ZipWriter(bos)
        writer.writeStored("a.txt", "A".toByteArray(Charsets.UTF_8))
        writer.writeStored("b.txt", "BB".toByteArray(Charsets.UTF_8))
        writer.finish()

        val data = bos.toByteArray()
        val eocdOff = findEocdInBytes(data)
        assertTrue("EOCD signature not found", eocdOff >= 0)

        val eocd = ByteBuffer.wrap(data, eocdOff, 22).slice().order(ByteOrder.LITTLE_ENDIAN)
        assertEquals(0x06054b50L, eocd.getInt().toLong() and 0xFFFFFFFFL)
        assertEquals(2, eocd.getShort(8).toInt() and 0xFFFF)
        assertEquals(2, eocd.getShort(10).toInt() and 0xFFFF)
    }

    @Test
    fun `zip writer preserves a deflated entry when copied as raw data`() {
        val original = "deflated content ".repeat(200).toByteArray(Charsets.UTF_8)
        val compressor = java.util.zip.Deflater(java.util.zip.Deflater.DEFAULT_COMPRESSION, true)
        compressor.setInput(original)
        compressor.finish()
        val compressed = ByteArray(4096)
        val compressedSize = compressor.deflate(compressed)
        compressor.end()
        val crc = java.util.zip.CRC32().apply { update(original) }.value

        val bos = ByteArrayOutputStream()
        ZipWriter(bos).apply {
            writeRaw("compressed.txt", java.io.ByteArrayInputStream(compressed, 0, compressedSize), 8,
                crc, compressedSize.toLong(), original.size.toLong())
            finish()
        }
        val reader = ZipReader.open(ByteArrayChannel(bos.toByteArray()))
        assertEquals(8, reader.entries.single().compressType)
        assertArrayEquals(original, reader.readEntryDataInflated(0))
        reader.close()
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Keystore Manager Tests
    // ═══════════════════════════════════════════════════════════════════════

    @Test
    fun `keystore manager generates valid keystore`() {
        val password = "testpass".toCharArray()
        val generated = KeystoreManager.generateKeystore(password, "testalias")

        assertNotNull(generated.keystoreBytes)
        assertTrue(generated.keystoreBytes.isNotEmpty())
        assertNotNull(generated.privateKey)
        assertNotNull(generated.certificate)
        assertEquals("testalias", generated.alias)

        // Load it back
        val loaded = KeystoreManager.loadKeystore(generated.keystoreBytes, password, "testalias")
        assertNotNull("should load generated keystore", loaded)
        assertEquals("testalias", loaded!!.alias)
    }

    @Test
    fun `keystore manager fails with wrong password`() {
        val generated = KeystoreManager.generateKeystore("correct".toCharArray())
        val loaded = KeystoreManager.loadKeystore(generated.keystoreBytes, "wrong".toCharArray())
        assertNull("should fail with wrong password", loaded)
    }

    @Test
    fun `keystore manager fails with corrupt bytes`() {
        val loaded = KeystoreManager.loadKeystore(ByteArray(50), "password".toCharArray())
        assertNull("should fail with corrupt bytes", loaded)
    }

    @Test
    fun `keystore manager generates with default alias`() {
        val generated = KeystoreManager.generateKeystore("test".toCharArray())
        assertEquals("patcher", generated.alias)
    }

    @Test
    fun `keystore manager generates a PKCS12 store`() {
        val password = "test".toCharArray()
        val generated = KeystoreManager.generateKeystore(password, "pkcs12")
        val store = java.security.KeyStore.getInstance("PKCS12")
        store.load(java.io.ByteArrayInputStream(generated.keystoreBytes), password)
        assertTrue(store.containsAlias("pkcs12"))
        assertEquals(generated.certificate.publicKey, store.getCertificate("pkcs12").publicKey)
    }

    @Test
    fun `default identity is stable and migrates a legacy JKS`() {
        val directory = java.nio.file.Files.createTempDirectory("tftf-keystore-").toFile()
        try {
            val legacyIdentity = KeystoreManager.generateKeystore("android".toCharArray(), "legacy")
            val legacyStore = java.security.KeyStore.getInstance("JKS")
            legacyStore.load(null, "android".toCharArray())
            legacyStore.setKeyEntry(
                "legacy", legacyIdentity.privateKey, "android".toCharArray(),
                arrayOf<java.security.cert.Certificate>(legacyIdentity.certificate)
            )
            val legacyFile = File(directory, "patcher-signing.jks")
            java.io.FileOutputStream(legacyFile).use { legacyStore.store(it, "android".toCharArray()) }

            val storage = File(directory, "patcher-signing.p12")
            val first = KeystoreManager.loadOrCreateDefault(storage, "legacy")
            val second = KeystoreManager.loadOrCreateDefault(storage, "legacy")
            assertTrue(storage.isFile)
            assertArrayEquals(first.certificate.encoded, second.certificate.encoded)
            assertArrayEquals(legacyIdentity.certificate.encoded, first.certificate.encoded)
            val migrated = java.security.KeyStore.getInstance("PKCS12")
            migrated.load(storage.inputStream(), "android".toCharArray())
            assertTrue(migrated.containsAlias("legacy"))
        } finally {
            directory.deleteRecursively()
        }
    }

    @Test
    fun `keystore manager supports a distinct private key password`() {
        val storePassword = "store-pass".toCharArray()
        val keyPassword = "key-pass".toCharArray()
        val generated = KeystoreManager.generateKeystore(storePassword, "separate-pass")
        val store = java.security.KeyStore.getInstance("JKS")
        store.load(java.io.ByteArrayInputStream(generated.keystoreBytes), storePassword)
        store.setKeyEntry(
            generated.alias,
            generated.privateKey,
            keyPassword,
            arrayOf<java.security.cert.Certificate>(generated.certificate)
        )
        val output = java.io.ByteArrayOutputStream()
        store.store(output, storePassword)

        assertNull(KeystoreManager.loadKeystore(output.toByteArray(), storePassword, generated.alias))
        val loaded = KeystoreManager.loadKeystore(
            output.toByteArray(), storePassword, generated.alias, keyPassword
        )
        assertNotNull("should load a key with its distinct password", loaded)
        assertEquals(generated.certificate.publicKey, loaded!!.certificate.publicKey)
    }

    // ═══════════════════════════════════════════════════════════════════════
    // APK Signer Tests
    // ═══════════════════════════════════════════════════════════════════════

    @Test
    fun `apk signer sign and verify roundtrip`() {
        val generated = KeystoreManager.generateKeystore("android".toCharArray(), "testkey")

        val content = "ZIP entry content data".toByteArray(Charsets.UTF_8)
        val cdAndEocd = buildFakeCentralDirAndEocd(content.size.toLong())

        val signResult = ApksigSigner.sign(
            apkContent = content,
            centralDirAndEocd = cdAndEocd,
            privateKey = generated.privateKey,
            certificate = generated.certificate
        )
        assertTrue("signing should succeed: ${signResult.error}", signResult.isSuccess)
        assertNotNull(signResult.signedApk)

        val verifyResult = ApksigSigner.verify(signResult.signedApk!!)
        assertTrue("verification should pass: ${verifyResult.details}", verifyResult.isVerified)
    }

    @Test
    fun `apk signer verify rejects unsigned data`() {
        val content = "unsigned content".toByteArray(Charsets.UTF_8)
        val cdAndEocd = buildFakeCentralDirAndEocd(content.size.toLong())
        val unsigned = content + cdAndEocd

        val verifyResult = ApksigSigner.verify(unsigned)
        assertFalse(verifyResult.isVerified)
    }

    @Test
    fun `apk signer verify rejects empty data`() {
        val verifyResult = ApksigSigner.verify(ByteArray(30))
        assertFalse(verifyResult.isVerified)
    }

    @Test
    fun `apk signer produces valid signatures for same input`() {
        val generated = KeystoreManager.generateKeystore("android".toCharArray(), "testkey")
        val content = "repeatable test".toByteArray(Charsets.UTF_8)
        val cdAndEocd = buildFakeCentralDirAndEocd(content.size.toLong())

        val result1 = ApksigSigner.sign(content, cdAndEocd, generated.privateKey, generated.certificate)
        val result2 = ApksigSigner.sign(content, cdAndEocd, generated.privateKey, generated.certificate)

        assertTrue(result1.isSuccess)
        assertTrue(result2.isSuccess)
        assertTrue(ApksigSigner.verify(result1.signedApk!!).isVerified)
        assertTrue(ApksigSigner.verify(result2.signedApk!!).isVerified)

        // Content prefix should be identical
        val content1 = result1.signedApk!!.sliceArray(0 until content.size)
        val content2 = result2.signedApk!!.sliceArray(0 until content.size)
        assertArrayEquals(content1, content2)
    }

    @Test
    fun `file signer verifies without loading the signed archive API`() {
        val unsigned = File.createTempFile("tftf-unsigned-", ".apk")
        val signed = File.createTempFile("tftf-signed-", ".apk")
        try {
            ByteArrayOutputStream().also { bos ->
                ZipWriter(bos).apply {
                    writeStored("one.txt", "one".toByteArray())
                    writeStored("resources.arsc", ByteArray(5) { it.toByte() })
                    writeStored("two.txt", "two".toByteArray())
                    finish()
                }
                unsigned.writeBytes(bos.toByteArray())
            }
            val generated = KeystoreManager.generateKeystore("android".toCharArray(), "testkey")
            val result = ApksigSigner.sign(unsigned, signed, generated.privateKey, generated.certificate)
            assertTrue("file signing should succeed: ${result.error}", result.isSuccess)
            assertTrue(ApksigSigner.verify(signed).isVerified)
            val signedZip = ZipReader.open(FileSeekableByteChannel(signed))
            val resourcesEntry = signedZip.entries.single { it.name == PatcherEngine.RESOURCES_NAME }
            assertEquals(0, resourcesEntry.compressType)
            assertEquals(0L, resourcesEntry.dataOffset % 4L)
            signedZip.close()
        } finally {
            unsigned.delete(); signed.delete()
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Full Pipeline CI Tests (synthetic APK builds)
    // ═══════════════════════════════════════════════════════════════════════

    @Test
    fun `synthetic APK builds valid ZIP with all required entries`() {
        val apk = buildSyntheticSourceApk(PatchRequest.ARM64)
        val reader = ZipReader.open(ByteArrayChannel(apk))

        assertTrue(reader.find("AndroidManifest.xml") >= 0)
        assertTrue(reader.find(PatcherEngine.RESOURCES_NAME) >= 0)
        assertTrue(reader.find(PatcherEngine.METADATA_NAME) >= 0)
        assertTrue(reader.find(PatcherEngine.SPARX_MANIFEST_NAME) >= 0)
        assertTrue(reader.find(PatcherEngine.ENDPOINT_CONFIG_NAME) >= 0)
        assertTrue(reader.find("lib/arm64-v8a/libil2cpp.so") >= 0)
        reader.close()
    }

    @Test
    fun `synthetic APK contains expected sparx endpoint`() {
        val apk = buildSyntheticSourceApk(PatchRequest.ARM64)
        val reader = ZipReader.open(ByteArrayChannel(apk))
        val idx = reader.find(PatcherEngine.SPARX_MANIFEST_NAME)
        val data = String(reader.readEntryDataInflated(idx), Charsets.UTF_8)
        assertTrue(data.contains("tform-0901-hzlhiniyfcwf.tf-cdn.net"))
        reader.close()
    }

    @Test
    fun `synthetic APK contains expected metadata literals`() {
        val apk = buildSyntheticSourceApk(PatchRequest.ARM64)
        val reader = ZipReader.open(ByteArrayChannel(apk))
        val idx = reader.find(PatcherEngine.METADATA_NAME)
        val data = reader.readEntryDataInflated(idx)
        val text = String(data, Charsets.UTF_8)
        assertTrue(text.contains("tf-odr.mcoc-cdn.cn"))
        assertTrue(text.contains("wss://gametalk.sparx.io:30443"))
        reader.close()
    }

    @Test
    fun `synthetic APK patching workflow parity check`() {
        // Build a synthetic source APK
        val sourceApk = buildSyntheticSourceApk(PatchRequest.ARM64)

        // Patch individual entries in isolation, verifying same transformations
        val reader = ZipReader.open(ByteArrayChannel(sourceApk))

        // Patch metadata
        val metaIdx = reader.find(PatcherEngine.METADATA_NAME)
        val metaData = reader.readEntryDataInflated(metaIdx)
        val metaResult = MetadataPatch.patch(metaData, "10.0.2.2", 8080)
        assertTrue("metadata patch: ${metaResult.error}", metaResult.isSuccess)
        assertTrue(metaResult.changed.contains("tf-odr.mcoc-cdn.cn"))

        // Patch sparx
        val sparxIdx = reader.find(PatcherEngine.SPARX_MANIFEST_NAME)
        val sparxData = reader.readEntryDataInflated(sparxIdx)
        val sparxResult = SparxPatch.patch(sparxData, "10.0.2.2", "http", 8080)
        assertTrue("sparx patch: ${sparxResult.error}", sparxResult.isSuccess)

        // Patch endpoint config
        val epIdx = reader.find(PatcherEngine.ENDPOINT_CONFIG_NAME)
        val epData = reader.readEntryDataInflated(epIdx)
        val epResult = EndpointConfigPatch.patch(epData, "10.0.2.2", "http", 8080)
        assertTrue("endpoint patch: ${epResult.error}", epResult.isSuccess)

        reader.close()
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Helpers
    // ═══════════════════════════════════════════════════════════════════════

    companion object {
        private fun buildMinimalZip(entries: List<Pair<String, ByteArray>>): ByteArray {
            val bos = ByteArrayOutputStream()
            val writer = ZipWriter(bos, soAlignment = 1)
            for ((name, data) in entries) {
                writer.writeStored(name, data)
            }
            writer.finish()
            return bos.toByteArray()
        }

        private fun findEocdInBytes(data: ByteArray): Int {
            for (i in data.size - 22 downTo maxOf(0, data.size - 65557)) {
                if (data[i].toInt() == 0x50 && data[i + 1].toInt() == 0x4b &&
                    data[i + 2].toInt() == 0x05 && data[i + 3].toInt() == 0x06
                ) return i
            }
            return -1
        }

        /** Build a fake central-directory + EOCD for APK signing tests. */
        private fun buildFakeCentralDirAndEocd(contentSize: Long): ByteArray {
            val nameBytes = "a.txt".toByteArray(Charsets.UTF_8)
            val bos = ByteArrayOutputStream()

            // Central directory entry: 46 bytes fixed + name
            val cdEntry = ByteBuffer.allocate(46 + nameBytes.size).order(ByteOrder.LITTLE_ENDIAN)
            cdEntry.putInt(0x02014b50) // central signature
            // Version made by: (Unix=3 << 8) | 20 = 0x0314
            cdEntry.putShort(((3 shl 8) or 20).toShort())
            cdEntry.putShort(20) // version needed
            cdEntry.putShort(0)  // flag bits
            cdEntry.putShort(0)  // compress type (stored)
            cdEntry.putShort(0)  // dostime
            cdEntry.putShort(33) // dosdate
            cdEntry.putInt(0)    // crc
            cdEntry.putInt(contentSize.toInt()) // compressed size
            cdEntry.putInt(contentSize.toInt()) // file size
            cdEntry.putShort(nameBytes.size.toShort()) // name length
            cdEntry.putShort(0)  // extra length
            cdEntry.putShort(0)  // comment length
            cdEntry.putShort(0)  // disk start
            cdEntry.putShort(0)  // internal attr
            cdEntry.putInt(0x81B60000.toInt()) // external attr
            cdEntry.putInt(0)    // local header offset
            cdEntry.put(nameBytes)
            bos.write(cdEntry.array())

            // Approximate location after signing block
            // The signing block size varies; compute the actual offset for EOCD
            val approxBlockSize = 8 + 16 + 8 + 4 + 8 + 16 + 8 + 4 + 4 + 8 + 4 // rough
            val cdStart = contentSize + approxBlockSize

            // EOCD
            val eocd = ByteBuffer.allocate(22).order(ByteOrder.LITTLE_ENDIAN)
            eocd.putInt(0x06054b50) // EOCD signature
            eocd.putShort(0)  // disk number
            eocd.putShort(0)  // disk with central dir
            eocd.putShort(1)  // entries on disk
            eocd.putShort(1)  // total entries
            eocd.putInt(cdEntry.array().size) // central dir size
            eocd.putInt(cdStart.toInt()) // central dir offset
            eocd.putShort(0)  // comment length
            bos.write(eocd.array())

            return bos.toByteArray()
        }

        /** Build a minimal global-metadata.dat with the 4 expected string literals. */
        private fun buildSyntheticMetadataForTest(): ByteArray {
            val literal1 = "tf-odr.mcoc-cdn.cn".toByteArray(Charsets.UTF_8)
            val literal2 = "tf-static.mcoc-cdn.cn".toByteArray(Charsets.UTF_8)
            val literal3 = "words-express.tf-cdn.net".toByteArray(Charsets.UTF_8)
            val literal4 = "wss://gametalk.sparx.io:30443".toByteArray(Charsets.UTF_8)

            val dataOffset = 56
            val bufSize = dataOffset + literal1.size + literal2.size + literal3.size + literal4.size
            val buf = ByteBuffer.allocate(bufSize).order(ByteOrder.LITTLE_ENDIAN)

            buf.putInt(0x464d5447) // magic
            buf.putInt(1) // version
            buf.putInt(24) // literalOffset
            buf.putInt(32) // literalCount (4 * 8)
            buf.putInt(dataOffset) // dataOffset
            buf.putInt(0) // reserved
            buf.position(24)

            var dataIdx = 0
            buf.putInt(literal1.size); buf.putInt(dataIdx); dataIdx += literal1.size
            buf.putInt(literal2.size); buf.putInt(dataIdx); dataIdx += literal2.size
            buf.putInt(literal3.size); buf.putInt(dataIdx); dataIdx += literal3.size
            buf.putInt(literal4.size); buf.putInt(dataIdx)

            buf.put(literal1)
            buf.put(literal2)
            buf.put(literal3)
            buf.put(literal4)

            return buf.array()
        }

        /** Build a synthetic source APK with all entries the engine expects. */
        private fun buildSyntheticSourceApk(abi: String): ByteArray {
            val bos = ByteArrayOutputStream()
            val writer = ZipWriter(bos, soAlignment = 4)

            // AndroidManifest.xml (minimal)
            val manifest = """<?xml version="1.0" encoding="utf-8"?>
<manifest xmlns:android="http://schemas.android.com/apk/res/android"
    package="com.kabam.bigrobot"
    android:versionCode="1" android:versionName="1.0">
</manifest>""".toByteArray(Charsets.UTF_8)
            writer.writeStored("AndroidManifest.xml", manifest)

            // Resource table must be stored and 4-byte aligned for API 30+ APKs.
            writer.writeStored("resources.arsc", ByteArray(17) { it.toByte() })

            // global-metadata.dat
            val metadata = buildSyntheticMetadataForTest()
            writer.writeStored(PatcherEngine.METADATA_NAME, metadata)

            // sparxmanifest
            val sparx = "endpoint = https://tform-0901-hzlhiniyfcwf.tf-cdn.net".toByteArray(Charsets.UTF_8)
            writer.writeStored(PatcherEngine.SPARX_MANIFEST_NAME, sparx)

            // endpoint config
            val endpointConfig = """{"Default":{"Prod":"https://tform-0901-hzlhiniyfcwf.tf-cdn.net"}}""".toByteArray(Charsets.UTF_8)
            writer.writeStored(PatcherEngine.ENDPOINT_CONFIG_NAME, endpointConfig)

            // libil2cpp.so (synthetic ELF for the requested ABI)
            val sites = if (abi == PatchRequest.ARM64) Il2cppPatch.arm64Sites else Il2cppPatch.armv7Sites
            val maxOffset = maxOf(sites.maxOf { it.offset + it.patchBytes.size }, 45781603)
            val il2cpp = ByteArray(maxOffset + 4096)
            il2cpp[0] = 0x7f; il2cpp[1] = 'E'.code.toByte(); il2cpp[2] = 'L'.code.toByte(); il2cpp[3] = 'F'.code.toByte()
            il2cpp[4] = if (abi == PatchRequest.ARM64) 2 else 1
            il2cpp[5] = 1
            if (abi == PatchRequest.ARM64) il2cpp[18] = 183.toByte() else il2cpp[18] = 40.toByte()
            writer.writeStored("lib/$abi/libil2cpp.so", il2cpp)

            // Other ABI library
            val otherAbi = if (abi == PatchRequest.ARM64) PatchRequest.ARMV7 else PatchRequest.ARM64
            val otherIl2cpp = ByteArray(4096)
            otherIl2cpp[0] = 0x7f; otherIl2cpp[1] = 'E'.code.toByte(); otherIl2cpp[2] = 'L'.code.toByte(); otherIl2cpp[3] = 'F'.code.toByte()
            otherIl2cpp[4] = if (otherAbi == PatchRequest.ARM64) 2 else 1
            otherIl2cpp[5] = 1
            if (otherAbi == PatchRequest.ARM64) otherIl2cpp[18] = 183.toByte() else otherIl2cpp[18] = 40.toByte()
            writer.writeStored("lib/$otherAbi/libil2cpp.so", otherIl2cpp)

            // Other lib entries
            writer.writeStored("lib/$abi/libunity.so", ByteArray(1024))
            writer.writeStored("lib/$otherAbi/libunity.so", ByteArray(1024))

            // META-INF entries (should be stripped during build)
            writer.writeStored("META-INF/MANIFEST.MF", "Manifest-Version: 1.0".toByteArray(Charsets.UTF_8))
            writer.writeStored("META-INF/CERT.SF", "Signature-Version: 1.0".toByteArray(Charsets.UTF_8))
            writer.writeStored("META-INF/CERT.RSA", ByteArray(256))

            writer.finish()
            return bos.toByteArray()
        }
    }
}

/**
 * In-memory ByteArray-backed SeekableByteChannel for testing ZipReader
 * without Android dependencies.
 */
class ByteArrayChannel(private val data: ByteArray) : SeekableByteChannel {
    private var closed = false
    override fun size(): Long = data.size.toLong()

    override fun read(position: Long, buffer: ByteArray, offset: Int, length: Int): Int {
        if (closed) throw ZipException("Channel closed")
        val start = position.toInt().coerceIn(0, data.size)
        val end = (start + length).coerceAtMost(data.size)
        val count = end - start
        if (count > 0) System.arraycopy(data, start, buffer, offset, count)
        return count
    }

    override fun close() { closed = true }
}
