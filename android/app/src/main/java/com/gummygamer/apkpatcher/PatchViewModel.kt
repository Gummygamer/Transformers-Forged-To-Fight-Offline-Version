package com.gummygamer.apkpatcher

import android.app.Application
import android.content.Intent
import android.content.pm.PackageInstaller
import android.net.Uri
import android.os.Build
import androidx.core.content.FileProvider
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import kotlinx.coroutines.Job
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch
import java.io.File
import java.io.FileInputStream
import java.io.OutputStream

/**
 * UI state matching the web GUI's sections: checks, command preview, build log, run result.
 */
data class UiState(
    // --- form fields ---
    val sourceApkUri: String = "",
    val sourceApkName: String = "",
    val outputName: String = "patched.apk",
    val abi: String = PatchRequest.ARM64,
    val serverMode: String = PatchRequest.BUNDLED,
    val serverHost: String = "127.0.0.1",
    val serverPort: Int = 8080,
    val scheme: String = "http",
    val keepOtherAbi: Boolean = false,
    val patchedIl2cppUri: String = "",
    val patchedIl2cppName: String = "",
    val autoPatchIl2cpp: Boolean = true,
    val keystoreUri: String = "",
    val keystoreName: String = "",
    val keystorePassword: String = "",
    val keyPassword: String = "",
    val keyAlias: String = "patcher",
    val offerInstall: Boolean = true,

    // --- validation ---
    val validationErrors: List<String> = emptyList(),
    val validationWarnings: List<String> = emptyList(),
    val stepPreview: List<String> = emptyList(),

    // --- execution ---
    val engineState: PatcherState = PatcherState.IDLE,
    val currentStep: String = "",
    val stepIndex: Int = 0,
    val stepTotal: Int = 0,
    val logLines: List<LogLine> = emptyList(),
    val resultMessage: String = "",

    // --- output ---
    val outputFilePath: String = "",
    val isInstalling: Boolean = false,
    val installResult: String = ""
)

class PatchViewModel(application: Application) : AndroidViewModel(application) {

    private val engine = PatcherEngine(application)
    private val _uiState = MutableStateFlow(UiState())
    val uiState: StateFlow<UiState> = _uiState.asStateFlow()
    private var patchJob: Job? = null

    override fun onCleared() {
        engine.cancel()
        patchJob?.cancel()
        super.onCleared()
    }

    // ---- Form updates ----

    fun setSourceApk(uri: String, displayName: String) {
        val baseName = displayName.substringBeforeLast(".")
        _uiState.update {
            it.copy(
                sourceApkUri = uri,
                sourceApkName = displayName,
                outputName = "${baseName}-patched.apk",
                resultMessage = "",
                validationErrors = emptyList(),
                validationWarnings = emptyList(),
                stepPreview = emptyList()
            )
        }
        revalidate()
    }

    fun setAbi(abi: String) {
        _uiState.update {
            val autoPatch = if (abi == PatchRequest.ARMV7 && it.patchedIl2cppUri.isBlank()) true else it.autoPatchIl2cpp
            it.copy(abi = abi, autoPatchIl2cpp = autoPatch)
        }
        revalidate()
    }

    fun setServerMode(mode: String) {
        _uiState.update {
            if (mode == PatchRequest.BUNDLED) {
                it.copy(serverMode = mode, serverHost = "127.0.0.1", serverPort = 8080, scheme = "http")
            } else {
                it.copy(serverMode = mode)
            }
        }
        revalidate()
    }

    fun setServerHost(host: String) {
        _uiState.update { it.copy(serverHost = host) }
        revalidate()
    }

    fun setServerPort(port: Int) {
        // Preserve invalid input so the validation card can explain it to the
        // user instead of silently converting it into a different port.
        _uiState.update { it.copy(serverPort = port) }
        revalidate()
    }

    fun setScheme(scheme: String) {
        _uiState.update { it.copy(scheme = scheme) }
        revalidate()
    }

    fun setKeepOtherAbi(keep: Boolean) {
        _uiState.update { it.copy(keepOtherAbi = keep) }
        revalidate()
    }

    fun setPatchedIl2cpp(uri: String, displayName: String) {
        _uiState.update { it.copy(patchedIl2cppUri = uri, patchedIl2cppName = displayName) }
        revalidate()
    }

    fun setAutoPatchIl2cpp(auto: Boolean) {
        _uiState.update { it.copy(autoPatchIl2cpp = auto) }
        revalidate()
    }

    fun setKeystore(uri: String, displayName: String) {
        _uiState.update { it.copy(keystoreUri = uri, keystoreName = displayName) }
        revalidate()
    }

    fun setKeystorePassword(pw: String) {
        _uiState.update { it.copy(keystorePassword = pw) }
        revalidate()
    }

    fun setKeyPassword(pw: String) {
        _uiState.update { it.copy(keyPassword = pw) }
        revalidate()
    }

    fun setKeyAlias(alias: String) {
        _uiState.update { it.copy(keyAlias = alias.ifBlank { "patcher" }) }
        revalidate()
    }

    fun setOfferInstall(offer: Boolean) {
        _uiState.update { it.copy(offerInstall = offer) }
    }

    // ---- Validation + preview ----

    private fun revalidate() {
        val s = _uiState.value
        if (s.sourceApkUri.isBlank()) {
            _uiState.update {
                it.copy(
                    validationErrors = emptyList(),
                    validationWarnings = listOf("Select a source APK to begin."),
                    stepPreview = emptyList()
                )
            }
            return
        }

        val request = buildRequest(s)
        val result = request.validate()

        val steps = listOf(
            "1. read source APK",
            "2. validate source APK contents (${s.abi})",
            "3. load hook library (${if (s.serverMode == PatchRequest.BUNDLED) "bundled 127.0.0.1:${s.serverPort}" else "separate ${s.scheme}://${s.serverHost}:${s.serverPort}"})",
            "4. prepare patched libil2cpp (${if (s.autoPatchIl2cpp) "auto-patch" else if (s.patchedIl2cppUri.isNotBlank()) "user-supplied" else "none"})",
            "5. build patched APK (drop signatures${if (!s.keepOtherAbi) ", drop other ABI" else ""})",
            "6. sign APK (v2 scheme, ${if (s.keystoreUri.isNotBlank()) "user keystore" else "generated JKS"})",
            "7. write signed APK → ${s.outputName}"
        )

        _uiState.update {
            it.copy(
                validationErrors = result.errors,
                validationWarnings = result.warnings,
                stepPreview = steps
            )
        }
    }

    // ---- Build request ----

    private fun buildRequest(s: UiState) = PatchRequest(
        sourceApkUri = s.sourceApkUri,
        outputName = s.outputName,
        abi = s.abi,
        serverMode = s.serverMode,
        serverHost = s.serverHost,
        serverPort = s.serverPort,
        scheme = s.scheme,
        keepOtherAbi = s.keepOtherAbi,
        patchedIl2cppUri = s.patchedIl2cppUri,
        autoPatchIl2cpp = s.autoPatchIl2cpp,
        keystoreUri = s.keystoreUri,
        keystorePassword = s.keystorePassword.toCharArray(),
        keyPassword = s.keyPassword.ifBlank { s.keystorePassword }.toCharArray(),
        keyAlias = s.keyAlias,
        offerInstall = s.offerInstall
    )

    // ---- Execute ----

    fun startPatch() {
        if (_uiState.value.engineState == PatcherState.RUNNING) return

        val request = buildRequest(_uiState.value)
        val vr = request.validate()
        if (!vr.isValid) return

        _uiState.update {
            it.copy(
                engineState = PatcherState.RUNNING,
                logLines = emptyList(),
                resultMessage = "",
                outputFilePath = "",
                installResult = ""
            )
        }

        patchJob = viewModelScope.launch {
            val outcome = engine.patch(
                request = request,
                onStep = { step ->
                    _uiState.update {
                        it.copy(
                            currentStep = step.stepName,
                            stepIndex = step.stepIndex,
                            stepTotal = step.stepTotal,
                            engineState = step.state
                        )
                    }
                },
                onLog = { line ->
                    _uiState.update {
                        it.copy(logLines = it.logLines + line)
                    }
                }
            )

            when (outcome) {
                is PatchOutcome.Success -> {
                    _uiState.update {
                        it.copy(
                            engineState = PatcherState.SUCCEEDED,
                            resultMessage = "Build succeeded: ${request.outputName} (${formatSize(outcome.outputSizeBytes)})",
                            outputFilePath = Uri.parse(outcome.outputApkUri).path ?: "",
                            validationErrors = emptyList(),
                            validationWarnings = emptyList()
                        )
                    }
                    // Offer install if toggled
                    if (_uiState.value.offerInstall) {
                        startInstall(outcome.outputApkUri)
                    }
                }
                is PatchOutcome.Cancelled -> {
                    _uiState.update {
                        it.copy(
                            engineState = PatcherState.CANCELLED,
                            resultMessage = "Build cancelled."
                        )
                    }
                }
                is PatchOutcome.Failed -> {
                    _uiState.update {
                        it.copy(
                            engineState = PatcherState.FAILED,
                            resultMessage = "Build failed: ${outcome.error}"
                        )
                    }
                }
            }
        }
    }

    fun cancelPatch() {
        engine.cancel()
        patchJob?.cancel()
        _uiState.update {
            it.copy(
                engineState = PatcherState.CANCELLED,
                resultMessage = "Build cancelled."
            )
        }
    }

    // ---- Install ----

    private fun startInstall(apkUri: String) {
        val uri = Uri.parse(apkUri)
        val file = File(uri.path ?: return)

        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            // Use PackageInstaller session API for better feedback
            installViaSession(file)
        } else {
            // Fallback to intent-based install
            installViaIntent(file)
        }
    }

    private fun installViaSession(file: File) {
        _uiState.update { it.copy(isInstalling = true, installResult = "Starting install...") }
        try {
            val installer = getApplication<Application>().packageManager.packageInstaller
            val params = PackageInstaller.SessionParams(PackageInstaller.SessionParams.MODE_FULL_INSTALL)
            val sessionId = installer.createSession(params)
            val session = installer.openSession(sessionId)

            try {
                FileInputStream(file).use { input ->
                    session.openWrite("package", 0, file.length()).use { outStream ->
                        input.copyTo(outStream)
                        session.fsync(outStream)
                    }
                }

                // Commit with a broadcast receiver for result
                val pendingIntent = android.app.PendingIntent.getBroadcast(
                    getApplication(),
                    sessionId,
                    Intent("com.gummygamer.apkpatcher.INSTALL_COMPLETE"),
                    android.app.PendingIntent.FLAG_UPDATE_CURRENT or
                        android.app.PendingIntent.FLAG_IMMUTABLE
                )
                session.commit(pendingIntent.intentSender)
                session.close()

                _uiState.update { it.copy(isInstalling = false, installResult = "Install offered to system.") }
            } catch (e: Exception) {
                session.close()
                installer.abandonSession(sessionId)
                _uiState.update {
                    it.copy(
                        isInstalling = false,
                        installResult = "Install failed: ${e.message}. " +
                            "The APK may already be installed with a different signature. " +
                            "Uninstall the existing app first."
                    )
                }
            }
        } catch (e: Exception) {
            _uiState.update {
                it.copy(
                    isInstalling = false,
                    installResult = "Install failed: ${e.message}. " +
                        "If a different-signed version is installed, uninstall it first."
                )
            }
        }
    }

    private fun installViaIntent(file: File) {
        _uiState.update { it.copy(isInstalling = true, installResult = "Offering install...") }
        try {
            val apkUri = FileProvider.getUriForFile(
                getApplication(),
                "com.gummygamer.apkpatcher.fileprovider",
                file
            )
            val intent = Intent(Intent.ACTION_VIEW).apply {
                setDataAndType(apkUri, "application/vnd.android.package-archive")
                flags = Intent.FLAG_GRANT_READ_URI_PERMISSION or Intent.FLAG_ACTIVITY_NEW_TASK
            }
            getApplication<Application>().startActivity(intent)
            _uiState.update { it.copy(isInstalling = false, installResult = "Install dialog opened.") }
        } catch (e: Exception) {
            _uiState.update {
                it.copy(
                    isInstalling = false,
                    installResult = "Could not open installer: ${e.message}. " +
                        "The APK may be already installed with a different signature — uninstall first."
                )
            }
        }
    }

    // ---- Export ----

    fun getExportUri(): Uri? {
        val path = _uiState.value.outputFilePath
        if (path.isBlank()) return null
        return Uri.fromFile(File(path))
    }

    fun copyToUri(targetUri: Uri): Boolean {
        val sourcePath = _uiState.value.outputFilePath
        if (sourcePath.isBlank()) return false
        return try {
            val sourceFile = File(sourcePath)
            if (!sourceFile.isFile) throw java.io.IOException("the patched APK is no longer available")
            val output = getApplication<Application>().contentResolver.openOutputStream(targetUri)
                ?: throw java.io.IOException("the selected destination is not writable")
            output.use { out ->
                sourceFile.inputStream().use { input ->
                    val copied = input.copyTo(out)
                    if (copied != sourceFile.length()) {
                        throw java.io.IOException("export ended before the complete APK was written")
                    }
                }
            }
            true
        } catch (e: Exception) {
            _uiState.update {
                it.copy(resultMessage = "Export failed: ${e.message}")
            }
            false
        }
    }

    // ---- Utility ----

    private fun formatSize(bytes: Long): String = when {
        bytes < 1024 -> "$bytes B"
        bytes < 1024 * 1024 -> "${bytes / 1024} KB"
        else -> "%.1f MB".format(bytes.toDouble() / (1024 * 1024))
    }

    companion object {
        /** Steps that make up a patch run, for preview. */
        val STEP_LABELS = listOf(
            "read source APK",
            "validate source APK",
            "load hook library",
            "prepare patched libil2cpp",
            "build patched APK",
            "sign APK (v2)",
            "write signed APK"
        )
    }
}
