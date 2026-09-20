package com.gummygamer.apkpatcher

import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class ArenaConfigPatchTest {
    private fun hook(): ByteArray {
        val marker = ArenaConfigPatch.MARKER.toByteArray(Charsets.US_ASCII)
        return ByteArray(64) { 0x11 } + marker + ByteArray(ArenaConfigPatch.BODY_LENGTH) + ByteArray(64) { 0x22 }
    }

    private fun bodyOf(data: ByteArray): String {
        val at = String(data, Charsets.ISO_8859_1).indexOf(ArenaConfigPatch.MARKER) + ArenaConfigPatch.MARKER.length
        return String(data, at, ArenaConfigPatch.BODY_LENGTH, Charsets.US_ASCII).trimEnd('\u0000')
    }

    @Test
    fun `patch writes host port and room after the marker and touches nothing else`() {
        val original = hook()
        val patched = ArenaConfigPatch.patch(original, "127.0.0.1", 8777).data!!
        assertEquals("host=127.0.0.1\nport=8777\nroom=arena_versus\n", bodyOf(patched))
        assertEquals(original.size, patched.size)
        val bodyStart = 64 + ArenaConfigPatch.MARKER.length
        assertArrayEquals(original.copyOfRange(0, bodyStart), patched.copyOfRange(0, bodyStart))
        val bodyEnd = bodyStart + ArenaConfigPatch.BODY_LENGTH
        assertArrayEquals(original.copyOfRange(bodyEnd, original.size), patched.copyOfRange(bodyEnd, patched.size))
    }

    @Test
    fun `repatching replaces the previous session without leftovers`() {
        val first = ArenaConfigPatch.patch(hook(), "relay.example.org", 9000).data!!
        val second = ArenaConfigPatch.patch(first, "10.0.0.2", 8777).data!!
        assertEquals("host=10.0.0.2\nport=8777\nroom=arena_versus\n", bodyOf(second))
    }

    @Test
    fun `hook without the netcode block is refused`() {
        val result = ArenaConfigPatch.patch(ByteArray(256), "127.0.0.1", 8777)
        assertFalse(result.isSuccess)
        assertTrue(result.error!!.contains("without the live Arena netcode"))
    }

    @Test
    fun `ambiguous marker is refused`() {
        val hook = hook()
        val twice = hook + hook
        assertFalse(ArenaConfigPatch.patch(twice, "127.0.0.1", 8777).isSuccess)
    }

    @Test
    fun `host and port are validated so they cannot inject config lines`() {
        assertFalse(ArenaConfigPatch.patch(hook(), "host\nroom=x", 8777).isSuccess)
        assertFalse(ArenaConfigPatch.patch(hook(), "", 8777).isSuccess)
        assertFalse(ArenaConfigPatch.patch(hook(), "127.0.0.1", 0).isSuccess)
        assertFalse(ArenaConfigPatch.patch(hook(), "127.0.0.1", 70000).isSuccess)
    }

    @Test
    fun `request validation gates the relay to arm64 and sane values`() {
        fun request(abi: String, host: String, port: Int) = PatchRequest(
            sourceApkUri = "content://x", outputName = "o.apk", abi = abi,
            serverMode = PatchRequest.SEPARATE, serverHost = "192.168.0.5", serverPort = 8080,
            scheme = "http", keepOtherAbi = true, patchedIl2cppUri = "", autoPatchIl2cpp = true,
            keystoreUri = "", keystorePassword = CharArray(0), keyPassword = CharArray(0),
            keyAlias = "", offerInstall = false, arenaRelayHost = host, arenaRelayPort = port,
        )
        assertTrue(request(PatchRequest.ARM64, "127.0.0.1", 8777).validate().isValid)
        assertTrue(request(PatchRequest.ARM64, "", 8777).validate().isValid)
        assertFalse(request(PatchRequest.ARMV7, "127.0.0.1", 8777).validate().isValid)
        assertFalse(request(PatchRequest.ARM64, "bad host", 8777).validate().isValid)
        assertFalse(request(PatchRequest.ARM64, "127.0.0.1", 0).validate().isValid)
    }
}
