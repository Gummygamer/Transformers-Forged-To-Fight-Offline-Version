package com.gummygamer.apkpatcher

/**
 * Patches the Unity endpoint config asset at assets/bin/Data/e1917cd7a6bdb4492a247b8f758df2ae.
 * The original JSON value must appear exactly once; we replace it
 * and space-pad to the original length (fixed-size field).
 *
 * Port of Server/build_phone_apk.lbl patch_endpoint_config.
 */
object EndpointConfigPatch {

    private const val ORIGINAL_JSON =
        """{"Default":{"Prod":"https://tform-0901-hzlhiniyfcwf.tf-cdn.net"}}"""

    data class Result(
        val data: ByteArray?,
        val error: String?
    ) {
        val isSuccess: Boolean get() = error == null
    }

    fun patch(data: ByteArray, serverHost: String, scheme: String, serverPort: Int): Result {
        val newJson = """{"Default":{"Prod":"$scheme://$serverHost:$serverPort"}}"""
        return replaceExactlyOneFixed(data, ORIGINAL_JSON, newJson,
            "unexpected Unity endpoint config count",
            "server host is too long for the fixed-size Unity endpoint config")
    }

    /**
     * Replace exactly one occurrence of [old] with [replacement], space-padded
     * to preserve the exact original byte length.
     */
    private fun replaceExactlyOneFixed(
        data: ByteArray,
        old: String,
        replacement: String,
        errorLabel: String,
        tooLongLabel: String
    ): Result {
        val oldBytes = old.toByteArray(Charsets.UTF_8)
        val newBytes = replacement.toByteArray(Charsets.UTF_8)

        if (newBytes.size > oldBytes.size) {
            return Result(null, tooLongLabel)
        }

        val count = countOccurrences(data, oldBytes)
        if (count != 1) {
            return Result(null, "$errorLabel (found $count occurrences, expected 1)")
        }

        val at = indexOf(data, oldBytes, 0)
        val out = data.copyOf()

        // Write replacement
        for (i in newBytes.indices) {
            out[at + i] = newBytes[i]
        }
        // Space-pad the remainder
        for (i in newBytes.size until oldBytes.size) {
            out[at + i] = ' '.code.toByte()
        }

        return Result(out, null)
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