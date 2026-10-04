package com.gummygamer.apkpatcher

/**
 * Patches res/raw/sparxmanifest to replace the Sparx endpoint URL.
 * The original URL appears exactly once; we replace it in full
 * (length changes are allowed for sparxmanifest).
 *
 * Port of Server/build_phone_apk.lbl patch_sparx_manifest.
 */
object SparxPatch {

    private const val ORIGINAL_ENDPOINT = "https://tform-0901-hzlhiniyfcwf.tf-cdn.net"

    data class Result(
        val data: ByteArray?,
        val error: String?
    ) {
        val isSuccess: Boolean get() = error == null
    }

    fun patch(data: ByteArray, serverHost: String, scheme: String, serverPort: Int): Result {
        val newEndpoint = "$scheme://$serverHost:$serverPort"
        return replaceExactlyOne(data, ORIGINAL_ENDPOINT, newEndpoint,
            "unexpected Sparx manifest endpoint count")
    }

    /**
     * Replace exactly one occurrence of [old] with [replacement] in [data].
     * Unlike EndpointConfigPatch, this allows length changes.
     */
    private fun replaceExactlyOne(
        data: ByteArray,
        old: String,
        replacement: String,
        errorLabel: String
    ): Result {
        val oldBytes = old.toByteArray(Charsets.UTF_8)
        val newBytes = replacement.toByteArray(Charsets.UTF_8)

        val count = countOccurrences(data, oldBytes)
        if (count != 1) {
            return Result(null, "$errorLabel (found $count occurrences, expected 1)")
        }

        val at = indexOf(data, oldBytes, 0)
        // Build: prefix + new + suffix
        val result = ByteArray(data.size - oldBytes.size + newBytes.size)
        System.arraycopy(data, 0, result, 0, at)
        System.arraycopy(newBytes, 0, result, at, newBytes.size)
        System.arraycopy(data, at + oldBytes.size, result, at + newBytes.size,
            data.size - at - oldBytes.size)

        return Result(result, null)
    }

    private fun countOccurrences(data: ByteArray, needle: ByteArray): Int {
        var count = 0
        var at = indexOf(data, needle, 0)
        while (at >= 0) {
            count++
            at = indexOf(data, needle, at + needle.size)
        }
        return count
    }

    private fun indexOf(data: ByteArray, needle: ByteArray, start: Int): Int {
        val max = data.size - needle.size
        for (i in start..max) {
            var match = true
            for (j in needle.indices) {
                if (data[i + j] != needle[j]) {
                    match = false
                    break
                }
            }
            if (match) return i
        }
        return -1
    }
}