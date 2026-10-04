package com.gummygamer.tftfpvphost.server

/** User-facing failure wording shared by the runtime, the service and the UI. */
object HostMessages {
    const val PAYLOAD_MISSING: String =
        "Bundled payload asset is missing. Run ./tools/prepare-assets.sh 8080 from the " +
            "android/ directory, then rebuild the host app."

    const val PAYLOAD_TOO_LARGE_FOR_MEMORY: String =
        "Not enough memory to load the bundled payload. Close other apps and start the host again."

    fun payloadInvalid(code: Int, detail: String): String =
        "Bundled payload failed validation ($code: $detail). " +
            "Regenerate it with ./tools/prepare-assets.sh 8080."

    fun payloadPortNote(blobPort: Int, boundPort: Int): String =
        "Bundled payload was generated for port $blobPort, and this host is serving port " +
            "$boundPort. That is expected for a LAN host: response bodies do not embed the " +
            "port, only the bundled in-APK server binds it."

    fun portInUse(port: Int): String =
        "Port $port is already in use on this device. Stop the other server or choose a " +
            "different port, then rebuild your players' APKs with --server-port $port."

    fun bindFailed(port: Int, reason: String): String =
        "Could not listen on port $port: $reason"
}
