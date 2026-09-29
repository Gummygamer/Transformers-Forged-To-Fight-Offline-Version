package com.gummygamer.apkpatcher

/**
 * Configures the target framerate in the arm64 runtime hook (libdothook.so).
 *
 * Primary path: Writes the target FPS into `g_fps_config` (marked by [MARKER] in .data).
 * Fallback path: For legacy prebuilt hook binaries without the marker, NOPs out the
 * 60 FPS hooks and pokes when 30 FPS is selected so the game runs at stock 30 FPS.
 */
object FpsConfigPatch {
    const val MARKER = "TFTF-FPS-CFG-1"
    const val MARKER_LEN = 16

    // Legacy fallback signatures in libdothook-arm64:
    // 1) mov w0, #60 in hooked_set_targetFrameRate (0x52800780 -> little-endian 80 07 80 52)
    private val LEGACY_MOV_W0_60 = byteArrayOf(0x80.toByte(), 0x07.toByte(), 0x80.toByte(), 0x52.toByte())
    // mov w0, #30 in ARM64: 0x528003C0 -> c0 03 80 52
    private val LEGACY_MOV_W0_30 = byteArrayOf(0xc0.toByte(), 0x03.toByte(), 0x80.toByte(), 0x52.toByte())

    // 2) ARM64 NOP instruction (0xd503201f -> 1f 20 03 d5)
    private val ARM64_NOP = byteArrayOf(0x1f.toByte(), 0x20.toByte(), 0x03.toByte(), 0xd5.toByte())

    // 3) Hook installation signatures for Application.set_targetFrameRate (0x1B46108) and QualitySettings.set_vSyncCount (0x16A71C0)
    // movz w9, #0x6108; movk w9, #0x1b4
    private val SIG_HOOK_FPS = byteArrayOf(0x09.toByte(), 0x21.toByte(), 0x8c.toByte(), 0x52.toByte(), 0x89.toByte(), 0x36.toByte(), 0xa0.toByte(), 0x72.toByte())
    // movz w9, #0x71c0; movk w9, #0x16a
    private val SIG_HOOK_VSYNC = byteArrayOf(0x09.toByte(), 0x38.toByte(), 0x8e.toByte(), 0x52.toByte(), 0x49.toByte(), 0x2d.toByte(), 0xa0.toByte(), 0x72.toByte())

    data class Result(
        val data: ByteArray,
        val patched: Boolean,
        val message: String
    )

    fun patch(hook: ByteArray, targetFps: Int): Result {
        if (targetFps !in listOf(PatchRequest.FPS_30, PatchRequest.FPS_60)) {
            return Result(hook, false, "Unknown framerate $targetFps FPS; keeping default")
        }

        val markerBytes = MARKER.toByteArray(Charsets.US_ASCII)
        val at = indexOf(hook, markerBytes)
        if (at >= 0) {
            val second = indexOf(hook, markerBytes, at + markerBytes.size)
            if (second >= 0) {
                return Result(hook, false, "Ambiguous marker $MARKER in hook binary")
            }
            if (at + MARKER_LEN + 4 > hook.size) {
                return Result(hook, false, "Hook's FPS config block is truncated")
            }

            val bodyStart = at + MARKER_LEN
            val currentFps = (hook[bodyStart].toInt() and 0xFF) or
                    ((hook[bodyStart + 1].toInt() and 0xFF) shl 8) or
                    ((hook[bodyStart + 2].toInt() and 0xFF) shl 16) or
                    ((hook[bodyStart + 3].toInt() and 0xFF) shl 24)

            val out = hook.copyOf()
            out[bodyStart] = (targetFps and 0xFF).toByte()
            out[bodyStart + 1] = ((targetFps shr 8) and 0xFF).toByte()
            out[bodyStart + 2] = ((targetFps shr 16) and 0xFF).toByte()
            out[bodyStart + 3] = ((targetFps shr 24) and 0xFF).toByte()

            val changed = currentFps != targetFps
            val msg = if (changed) {
                "Configured runtime hook framerate to $targetFps FPS"
            } else {
                "Runtime hook already set to $targetFps FPS"
            }
            return Result(out, changed, msg)
        }

        // Fallback for legacy hook binary without the marker
        if (targetFps == PatchRequest.FPS_30) {
            val out = hook.copyOf()
            if (patchLegacyHookFor30Fps(out)) {
                return Result(out, true, "Configured legacy hook for 30 FPS")
            }
        }

        return Result(hook, false, "Runtime hook framerate set to $targetFps FPS (default)")
    }

    private fun patchLegacyHookFor30Fps(data: ByteArray): Boolean {
        var modified = false

        // Patch mov w0, #60 -> mov w0, #30 in hooked_set_targetFrameRate
        val movIdx = indexOf(data, LEGACY_MOV_W0_60)
        if (movIdx >= 0) {
            LEGACY_MOV_W0_30.copyInto(data, movIdx)
            modified = true
        }

        // NOP the inline_hook call for set_targetFrameRate (bl at +28 from signature)
        val hook1Idx = indexOf(data, SIG_HOOK_FPS)
        if (hook1Idx >= 0 && hook1Idx + 32 <= data.size) {
            ARM64_NOP.copyInto(data, hook1Idx + 28)
            modified = true
        }

        // NOP the inline_hook call for set_vSyncCount (bl at +32 from signature)
        val hook2Idx = indexOf(data, SIG_HOOK_VSYNC)
        if (hook2Idx >= 0 && hook2Idx + 36 <= data.size) {
            ARM64_NOP.copyInto(data, hook2Idx + 32)
            modified = true
        }

        return modified
    }

    private fun indexOf(data: ByteArray, needle: ByteArray, start: Int = 0): Int {
        for (i in start..data.size - needle.size) {
            var match = true
            for (j in needle.indices) {
                if (data[i + j] != needle[j]) { match = false; break }
            }
            if (match) return i
        }
        return -1
    }
}
