package com.gummygamer.apkpatcher

/**
 * Immutable patch request mirroring the web GUI's BuildRequest minus desktop tool fields.
 * All fields represent user-intent configuration; the engine validates and resolves defaults.
 */
data class PatchRequest(
    /** Content URI of the source APK (from SAF OpenDocument). */
    val sourceApkUri: String,

    /** Display name for the output APK (used in SAF CreateDocument). */
    val outputName: String,

    /** Target ABI: "arm64-v8a" or "armeabi-v7a". */
    val abi: String,

    /** Server mode: "bundled" or "separate". */
    val serverMode: String,

    /** Server host (IPv4 or hostname; bundled forces "127.0.0.1"). */
    val serverHost: String,

    /** Server port (1-65535; bundled defaults to 8080). */
    val serverPort: Int,

    /** URL scheme: "http" or "https". */
    val scheme: String,

    /** Whether to keep libraries for the non-target ABI in the output. */
    val keepOtherAbi: Boolean,

    /** Content URI for a pre-patched libil2cpp.so, or empty to auto-patch. */
    val patchedIl2cppUri: String,

    /** Whether to auto-patch the pristine libil2cpp extracted from the source APK. */
    val autoPatchIl2cpp: Boolean,

    /** Content URI for a custom keystore (PKCS12 or JKS), or empty to use the app's generated default. */
    val keystoreUri: String,

    /** Keystore password (zeroed after use). */
    val keystorePassword: CharArray,

    /** Key password (zeroed after use). */
    val keyPassword: CharArray,

    /** Alias within the keystore to use for signing. */
    val keyAlias: String,

    /** Whether to offer the output APK for installation via PackageInstaller after patching. */
    val offerInstall: Boolean
) {
    /** Validate the request returns a list of user-facing error/warning messages. */
    fun validate(): ValidationResult {
        val errors = mutableListOf<String>()
        val warnings = mutableListOf<String>()

        if (abi != ARM64 && abi != ARMV7) {
            errors += "ABI must be arm64-v8a or armeabi-v7a."
        }

        if (serverMode != BUNDLED && serverMode != SEPARATE) {
            errors += "Server mode must be bundled or separate."
        } else if (serverMode == BUNDLED) {
            if (scheme != "http") {
                errors += "A bundled server only supports http."
            }
            if (serverHost != "127.0.0.1") {
                errors += "A bundled server host must be exactly 127.0.0.1."
            }
        } else { // SEPARATE
            if (serverHost.isBlank()) {
                errors += "A separate server needs a non-empty host."
            } else if (serverHost == "127.0.0.1") {
                warnings += "127.0.0.1 points at the phone itself; use your PC's reachable address for a separate server."
            }
        }

        if (scheme != "http" && scheme != "https") {
            errors += "Scheme must be http or https."
        }

        if (serverPort !in 1..65535) {
            errors += "Server port must be between 1 and 65535."
        }

        if (abi == ARMV7 && patchedIl2cppUri.isBlank() && !autoPatchIl2cpp) {
            errors += "32-bit builds need a patched libil2cpp.so; supply one or enable auto-patching."
        }

        if (abi == ARMV7 && !keepOtherAbi) {
            warnings += "32-bit-only output drops the arm64 libraries; enable keeping the other ABI if you need both architectures."
        }

        return ValidationResult(errors, warnings)
    }

    companion object {
        const val ARM64 = "arm64-v8a"
        const val ARMV7 = "armeabi-v7a"
        const val BUNDLED = "bundled"
        const val SEPARATE = "separate"
    }
}

data class ValidationResult(
    val errors: List<String>,
    val warnings: List<String>
) {
    val isValid: Boolean get() = errors.isEmpty()
}

/** Immutable outcome of a patch operation. */
sealed class PatchOutcome {
    /** Patch completed successfully; outputApkUri is an app-private file URI ready for export/install. */
    data class Success(
        val outputApkUri: String,
        val patchedHosts: List<String>,
        val abi: String,
        val outputSizeBytes: Long
    ) : PatchOutcome()

    /** Patch was cancelled by the user. */
    object Cancelled : PatchOutcome()

    /** Patch failed with an actionable error message. */
    data class Failed(val error: String) : PatchOutcome()
}

/** Progress state matching the web runner's vocabulary. */
enum class PatcherState {
    IDLE, RUNNING, SUCCEEDED, FAILED, CANCELLED
}

/** Per-step progress notification. */
data class StepProgress(
    val state: PatcherState,
    val stepName: String,
    val stepIndex: Int,
    val stepTotal: Int
)

/** A log line emitted during patching. */
data class LogLine(
    val text: String,
    val isError: Boolean = false
)
