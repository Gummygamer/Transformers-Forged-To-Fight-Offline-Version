package com.gummygamer.apkpatcher

import android.content.Context
import android.net.Uri
import android.os.ParcelFileDescriptor
import kotlinx.coroutines.*
import java.io.*
import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.util.concurrent.atomic.AtomicBoolean
import java.util.zip.CRC32

/**
 * Core patching engine. Ports the web GUI pipeline (pipeline.lbl + build_phone_apk.lbl)
 * to run entirely on-device.
 *
 * API contract:
 *   suspend fun patch(request, onStep, onLog): PatchOutcome
 *   fun cancel()
 *
 * All work runs on Dispatchers.IO; UI receives progress and log lines via callbacks.
 * Source APK is streamed (only patched entries are fully loaded into memory).
 */
class PatcherEngine(context: Context) {

    private val appContext = context.applicationContext
    private val cancelled = AtomicBoolean(false)
    private val running = AtomicBoolean(false)
    @Volatile
    private var currentJob: Job? = null

    // Entry name constants from build_phone_apk.lbl
    companion object {
        const val METADATA_NAME = "assets/bin/Data/Managed/Metadata/global-metadata.dat"
        const val SPARX_MANIFEST_NAME = "res/raw/sparxmanifest"
        const val ENDPOINT_CONFIG_NAME = "assets/bin/Data/e1917cd7a6bdb4492a247b8f758df2ae"
        const val PAYLOAD_ASSET = "assets/tftf_offline_payload.bin"
        const val RESOURCES_NAME = "resources.arsc"
    }

    /** Cancel any in-progress patch. Idempotent. */
    fun cancel() {
        cancelled.set(true)
        currentJob?.cancel()
    }

    /**
     * Execute the full patch pipeline.
     *
     * @param request Validated patch configuration.
     * @param onStep Callback for per-step progress (step name, index, total).
     * @param onLog Callback for log lines.
     * @return PatchOutcome describing success, failure, or cancellation.
     */
    suspend fun patch(
        request: PatchRequest,
        onStep: (StepProgress) -> Unit = {},
        onLog: (LogLine) -> Unit = {}
    ): PatchOutcome = withContext(Dispatchers.IO) {
        if (!running.compareAndSet(false, true)) {
            return@withContext PatchOutcome.Failed("Another patch is already running")
        }
        cancelled.set(false)

        return@withContext try {
            currentJob = coroutineContext[Job]

            val validation = request.validate()
            if (!validation.isValid) throw IOException(validation.errors.joinToString(" "))

            val steps = planSteps(request)
            onStep(StepProgress(PatcherState.RUNNING, "start", 0, steps.size))

            // Step 1: Read source APK zip structure
            checkCancelled()
            reportStep(onStep, onLog, 1, steps.size, "reading source APK")

            val sourceUri = Uri.parse(request.sourceApkUri)
            val sourceZip = openSourceZip(sourceUri)
            val workDir = File(appContext.cacheDir, "patching").also { it.mkdirs() }
            val unsignedFile = File.createTempFile("unsigned-", ".apk", workDir)
            val signedFile = File.createTempFile("signed-", ".apk", workDir)

            try {
                // Step 2: Validate source APK contents
                checkCancelled()
                reportStep(onStep, onLog, 2, steps.size, "validating source APK")
                validateApkContents(sourceZip, request)

                // Step 3: Load hook library
                checkCancelled()
                reportStep(onStep, onLog, 3, steps.size, "loading hook library")
                val hookData = loadHookAsset(request.abi, request.serverMode == PatchRequest.BUNDLED)

                // Step 4: Extract and/or patch libil2cpp
                checkCancelled()
                reportStep(onStep, onLog, 4, steps.size, "preparing patched libil2cpp")
                val il2cppData = prepareIl2cpp(sourceZip, request, onLog)

                // Step 5: Build patched APK
                checkCancelled()
                reportStep(onStep, onLog, 5, steps.size, "building patched APK")
                buildPatchedApk(sourceZip, request, hookData, il2cppData, unsignedFile, onLog)
                validateAndroidApkPackaging(unsignedFile)

                // Step 6: Sign APK (v2 scheme)
                checkCancelled()
                reportStep(onStep, onLog, 6, steps.size, "signing APK")
                signApk(unsignedFile, signedFile, request, onLog)
                validateAndroidApkPackaging(signedFile)

                // Step 7: Write output to temp file
                checkCancelled()
                reportStep(onStep, onLog, 7, steps.size, "writing signed APK")
                val outputFile = writeTempApk(signedFile, request.outputName)

                onLog(LogLine("SUCCESS: finished signed APK (${signedFile.length()} bytes)"))
                onStep(StepProgress(PatcherState.SUCCEEDED, "done", steps.size, steps.size))

                PatchOutcome.Success(
                    outputApkUri = Uri.fromFile(outputFile).toString(),
                    patchedHosts = listOf(
                        "tf-odr.mcoc-cdn.cn", "tf-static.mcoc-cdn.cn",
                        "words-express.tf-cdn.net", "gametalk.sparx.io",
                        "tform-0901-hzlhiniyfcwf.tf-cdn.net"
                    ),
                    abi = request.abi,
                    outputSizeBytes = signedFile.length()
                )
            } finally {
                sourceZip.close()
                unsignedFile.delete()
                signedFile.delete()
            }
        } catch (e: CancellationException) {
            onStep(StepProgress(PatcherState.CANCELLED, "cancelled", 0, 0))
            onLog(LogLine("Run cancelled by the user."))
            PatchOutcome.Cancelled
        } catch (e: Exception) {
            onStep(StepProgress(PatcherState.FAILED, "failed", 0, 0))
            onLog(LogLine("ERROR: ${e.message}", isError = true))
            PatchOutcome.Failed(e.message ?: "Unknown error")
        } finally {
            currentJob = null
            running.set(false)
        }
    }

    // ---- Step planning ----

    private fun planSteps(request: PatchRequest): List<String> = listOf(
        "read source APK",
        "validate source APK",
        "load hook library",
        "prepare patched libil2cpp",
        "build patched APK",
        "sign APK (v2)",
        "write signed APK"
    )

    // ---- APK I/O ----

    private fun openSourceZip(uri: Uri): ZipReader {
        val fd = appContext.contentResolver.openFileDescriptor(uri, "r")
            ?: throw IOException("Cannot open source APK: permission denied or file not found")
        val channel = ParcelFileDescriptorChannel(fd)
        return ZipReader.open(channel)
    }

    // ---- Validation ----

    private fun validateApkContents(zip: ZipReader, request: PatchRequest) {
        val requiredEntries = listOf(
            "AndroidManifest.xml",
            RESOURCES_NAME,
            METADATA_NAME,
            SPARX_MANIFEST_NAME,
            ENDPOINT_CONFIG_NAME,
            il2cppEntry(request.abi)
        )

        for (name in requiredEntries) {
            if (zip.find(name) < 0) {
                throw IOException("APK is missing required entry: $name")
            }
        }

        // Check for encrypted entries
        for (entry in zip.entries) {
            if (entry.isEncrypted) {
                throw IOException("Unsupported encrypted ZIP entry: ${entry.name}")
            }
        }
    }

    // ---- Hook library ----

    private fun loadHookAsset(abi: String, bundleServer: Boolean): ByteArray {
        val assetName = if (abi == PatchRequest.ARM64) {
            "libdothook-arm64.bin"
        } else {
            "libdothook-armv7.bin"
        }

        return try {
            appContext.assets.open(assetName).use { it.readBytes() }
        } catch (_: IOException) {
            throw IOException("Runtime hook is missing. Run android/tools/prepare-assets.sh before building the patcher")
        }.also {
            val elfAbi = Il2cppPatch.elfAbi(it)
            if (elfAbi != abi) throw IOException("Runtime hook asset $assetName is not a valid $abi ELF library")
            if (bundleServer && it.size < 4096) throw IOException("Runtime hook asset $assetName is incomplete")
        }
    }

    // ---- libil2cpp handling ----

    private fun il2cppEntry(abi: String) = "lib/$abi/libil2cpp.so"

    private fun prepareIl2cpp(
        zip: ZipReader,
        request: PatchRequest,
        onLog: (LogLine) -> Unit
    ): ByteArray? {
        val il2cppIdx = zip.find(il2cppEntry(request.abi))

        // If user supplied pre-patched library, validate and use it
        if (request.patchedIl2cppUri.isNotBlank()) {
            onLog(LogLine("Using supplied patched libil2cpp"))
            val patchedData = readUriBytes(Uri.parse(request.patchedIl2cppUri))
            val detectedAbi = Il2cppPatch.elfAbi(patchedData)
            if (detectedAbi != request.abi) {
                throw IOException(
                    "Supplied patched libil2cpp is not a valid ${request.abi} ELF library"
                )
            }
            if (request.serverMode == PatchRequest.BUNDLED) {
                val ready = Il2cppPatch.checkOfflineReady(patchedData, request.abi)
                if (!ready.isSuccess) throw IOException("Supplied libil2cpp is not ready for bundled mode: ${ready.error}")
                return ready.data
            }
            if (!Il2cppPatch.hasHookReference(patchedData)) {
                throw IOException("Supplied libil2cpp is not wired to libdothook.so for separate-server mode")
            }
            return patchedData
        }

        // Auto-patch from source
        if (request.autoPatchIl2cpp) {
            if (il2cppIdx < 0) {
                throw IOException("APK is missing ${il2cppEntry(request.abi)} for auto-patching")
            }
            onLog(LogLine("Extracting and auto-patching libil2cpp from source APK"))
            val pristineData = zip.readEntryDataInflated(il2cppIdx)

            val detectedAbi = Il2cppPatch.elfAbi(pristineData)
            if (detectedAbi != request.abi) {
                throw IOException(
                    "APK contains a libil2cpp that is not a valid ${request.abi} ELF library"
                )
            }

            val result = Il2cppPatch.autoPatch(pristineData, request.abi)
            if (result.error != null) {
                throw IOException("Auto-patch failed: ${result.error}")
            }
            if (request.serverMode == PatchRequest.BUNDLED) {
                val ready = Il2cppPatch.checkOfflineReady(result.data, request.abi)
                if (!ready.isSuccess) throw IOException("Bundled reachability patch failed: ${ready.error}")
                onLog(LogLine("Applied offline reachability stubs"))
                return ready.data
            }
            onLog(LogLine("Auto-patched 16 code sites + injected DT_NEEDED libdothook.so"))
            return result.data
        }

        // No patching needed
        return null
    }

    // ---- Build patched APK ----

    private fun buildPatchedApk(
        sourceZip: ZipReader,
        request: PatchRequest,
        hookData: ByteArray,
        il2cppData: ByteArray?,
        outputFile: File,
        onLog: (LogLine) -> Unit
    ): Long {
        val output = FileOutputStream(outputFile)
        try {
        val writer = ZipWriter(output)

        val otherAbi = if (request.abi == PatchRequest.ARM64) PatchRequest.ARMV7 else PatchRequest.ARM64
        val otherPrefix = "lib/$otherAbi/"
        val hookEntryPath = "lib/${request.abi}/libdothook.so"
        val il2cppEntryPath = il2cppEntry(request.abi)

        var hookWritten = false

        for ((i, entry) in sourceZip.entries.withIndex()) {
            // Drop META-INF/ signature files
            if (isSignatureEntry(entry.name)) continue

            // Drop other-ABI libraries unless keep_other_abi
            if (!request.keepOtherAbi && entry.name.startsWith(otherPrefix)) continue

            // Drop existing payload if bundled mode (we add a fresh one)
            if (request.serverMode == PatchRequest.BUNDLED && entry.name == PAYLOAD_ASSET) continue

            val isPatchTarget = entry.name == METADATA_NAME ||
                    entry.name == SPARX_MANIFEST_NAME ||
                    entry.name == ENDPOINT_CONFIG_NAME

            when {
                entry.name == hookEntryPath -> {
                    writer.writeStored(name = entry.name, data = hookData,
                        createVersion = entry.createVersion, createSystem = entry.createSystem,
                        extractVersion = entry.extractVersion, dostime = entry.dostime,
                        dosdate = entry.dosdate, comment = entry.comment,
                        internalAttr = entry.internalAttr, externalAttr = entry.externalAttr)
                    hookWritten = true
                }
                entry.name == il2cppEntryPath && il2cppData != null -> {
                    writer.writeStored(name = entry.name, data = il2cppData,
                        createVersion = entry.createVersion, createSystem = entry.createSystem,
                        extractVersion = entry.extractVersion, dostime = entry.dostime,
                        dosdate = entry.dosdate, comment = entry.comment,
                        internalAttr = entry.internalAttr, externalAttr = entry.externalAttr)
                }
                isPatchTarget -> {
                    val raw = sourceZip.readEntryDataInflated(i)
                    val result = patchEntryData(entry.name, raw, request)
                    if (result.error != null) {
                        throw IOException("Failed to patch ${entry.name}: ${result.error}")
                    }
                    writer.writeStored(name = entry.name, data = result.data!!,
                        createVersion = entry.createVersion, createSystem = entry.createSystem,
                        extractVersion = entry.extractVersion, dostime = entry.dostime,
                        dosdate = entry.dosdate, comment = entry.comment,
                        internalAttr = entry.internalAttr, externalAttr = entry.externalAttr)
                }
                else -> {
                    val native = entry.name.startsWith("lib/") && entry.name.endsWith(".so")
                    val resourceTable = entry.name == RESOURCES_NAME
                    val outputType = if (native || resourceTable) 0 else entry.compressType
                    sourceZip.openEntryStream(i, inflate = native || resourceTable).use { input ->
                        writer.writeRaw(
                            name = entry.name, input = input, compressType = outputType,
                            crc = entry.crc,
                            compressedSize = if (outputType == 0) entry.fileSize else entry.compressSize,
                            fileSize = entry.fileSize, createVersion = entry.createVersion,
                            createSystem = entry.createSystem, extractVersion = entry.extractVersion,
                            flagBits = entry.flagBits, dostime = entry.dostime, dosdate = entry.dosdate,
                            extra = entry.extra, comment = entry.comment,
                            internalAttr = entry.internalAttr, externalAttr = entry.externalAttr
                        )
                    }
                }
            }
        }

        // Add hook entry if it didn't exist in source
        if (!hookWritten) {
            onLog(LogLine("Adding hook entry: $hookEntryPath"))
            writer.writeStored(name = hookEntryPath, data = hookData)
        }

        // Add payload for bundled mode
        if (request.serverMode == PatchRequest.BUNDLED) {
            onLog(LogLine("Adding bundled server payload"))
            val payloadData = loadPayloadAsset(request.serverPort)
            writer.writeStored(
                name = PAYLOAD_ASSET,
                data = payloadData,
                externalAttr = 0x81A40000L
            )
        }

        // Log reachability stubs for bundled mode
        if (request.serverMode == PatchRequest.BUNDLED) {
            onLog(LogLine("Applied offline reachability stubs: Application.internetReachability; EndPoint.HasInternetConnectivity"))
        }

        writer.finish()
        return outputFile.length()
        } finally {
            output.close()
        }
    }

    // ---- Entry patching ----

    private data class EntryPatchResult(
        val data: ByteArray?,
        val error: String?,
        val changedHosts: List<String>?
    )

    private fun patchEntryData(
        name: String,
        data: ByteArray,
        request: PatchRequest
    ): EntryPatchResult {
        return when (name) {
            METADATA_NAME -> {
                val result = MetadataPatch.patch(data, request.serverHost, request.serverPort)
                if (!result.isSuccess) EntryPatchResult(null, result.error, null)
                else EntryPatchResult(result.data, null, result.changed)
            }
            SPARX_MANIFEST_NAME -> {
                val result = SparxPatch.patch(data, request.serverHost, request.scheme, request.serverPort)
                if (!result.isSuccess) EntryPatchResult(null, result.error, null)
                else EntryPatchResult(result.data, null,
                    listOf("tform-0901-hzlhiniyfcwf.tf-cdn.net"))
            }
            ENDPOINT_CONFIG_NAME -> {
                val result = EndpointConfigPatch.patch(data, request.serverHost, request.scheme, request.serverPort)
                if (!result.isSuccess) EntryPatchResult(null, result.error, null)
                else EntryPatchResult(result.data, null, emptyList())
            }
            else -> EntryPatchResult(data, null, null)
        }
    }

    // ---- Signing ----

    private fun signApk(
        unsignedApk: File,
        signedApk: File,
        request: PatchRequest,
        onLog: (LogLine) -> Unit
    ) {
        try {
            // Load/create keystore
            val loadedKs = if (request.keystoreUri.isNotBlank()) {
                val ksBytes = readUriBytes(Uri.parse(request.keystoreUri))
                KeystoreManager.loadKeystore(
                    keystoreBytes = ksBytes,
                    password = request.keystorePassword,
                    alias = request.keyAlias.ifBlank { "patcher" },
                    keyPassword = request.keyPassword
                )
                    ?: throw IOException(
                        "Unable to load signing keystore. Check the store/key passwords and alias, " +
                            "and ensure it is a PKCS12 or JKS store containing a private key with an X.509 certificate."
                    )
            } else {
                onLog(LogLine("Using the app's persistent on-device signing identity"))
                KeystoreManager.loadOrCreateDefault(File(appContext.noBackupFilesDir, "patcher-signing.p12"), request.keyAlias.ifBlank { "patcher" })
            }

            val result = ApksigSigner.sign(unsignedApk, signedApk, loadedKs.privateKey, loadedKs.certificate)

            if (!result.isSuccess) {
                throw IOException(result.error ?: "Signing failed")
            }

            // Verify both the cryptographic signature and the signed APK content.
            val verifyResult = ApksigSigner.verify(signedApk)
            if (!verifyResult.isVerified) {
                throw IOException("Signature verification failed: ${verifyResult.details}")
            }
            onLog(LogLine("APK Signature Scheme v2 verified: ${verifyResult.details}"))
        } finally {
            request.keystorePassword.fill('\u0000')
            request.keyPassword.fill('\u0000')
        }

    }

    /**
     * Android 11+ rejects APKs targeting API 30 or newer when resources.arsc is
     * compressed or its local-entry data is not four-byte aligned. Check the
     * actual artifact after each ZIP/signing stage so export cannot report an
     * APK that PackageInstaller will reject.
     */
    private fun validateAndroidApkPackaging(apk: File) {
        val reader = ZipReader.open(FileSeekableByteChannel(apk))
        try {
            val index = reader.find(RESOURCES_NAME)
            if (index < 0) throw IOException("APK is missing required entry: $RESOURCES_NAME")
            val entry = reader.entries[index]
            if (entry.compressType != 0) {
                throw IOException("$RESOURCES_NAME must be stored uncompressed (compression method ${entry.compressType})")
            }
            if (entry.dataOffset % 4L != 0L) {
                throw IOException("$RESOURCES_NAME data is not 4-byte aligned (offset ${entry.dataOffset})")
            }

            // Native libraries are loaded directly from the APK on modern
            // devices. Keep the same 16 KiB boundary used by ZipWriter so a
            // build cannot pass packaging checks while still failing at the
            // linker on a 16 KiB-page device.
            for (nativeEntry in reader.entries) {
                if (nativeEntry.name.startsWith("lib/") && nativeEntry.name.endsWith(".so") &&
                    nativeEntry.compressType == 0 && nativeEntry.dataOffset % (16L * 1024L) != 0L
                ) {
                    throw IOException("${nativeEntry.name} data is not 16 KiB aligned (offset ${nativeEntry.dataOffset})")
                }
            }
        } finally {
            reader.close()
        }
    }

    // ---- Output ----

    private fun writeTempApk(signedApk: File, outputName: String): File {
        // Keep completed artifacts in filesDir. cacheDir may be reclaimed by Android
        // before the user has a chance to export or retry the installation.
        val outputDir = File(appContext.filesDir, "patched_apks")
        outputDir.mkdirs()
        val safeName = outputName.replace(Regex("[^A-Za-z0-9._-]"), "_").ifBlank { "patched.apk" }
        val outputFile = File(outputDir, safeName)

        // Write atomically: to temp file, fsync, rename
        val tempFile = File.createTempFile("$safeName-", ".part", outputDir)
        signedApk.inputStream().use { input -> tempFile.outputStream().use { out ->
            input.copyTo(out, 64 * 1024)
            out.flush()
            out.fd.sync()
        } }

        if (!tempFile.renameTo(outputFile)) {
            throw IOException("Unable to publish signed APK in private app storage")
        }

        return outputFile
    }

    // ---- ZIP utilities ----

    private fun isSignatureEntry(name: String): Boolean {
        val upper = name.uppercase()
        return upper.startsWith("META-INF/") &&
            (upper.endsWith(".SF") || upper.endsWith(".RSA") ||
             upper.endsWith(".DSA") || upper.endsWith(".EC") ||
             upper.endsWith("MANIFEST.MF"))
    }

    private fun findEocdOffset(data: ByteArray): Int {
        for (i in data.size - 22 downTo maxOf(0, data.size - 65557)) {
            if (data[i].toInt() == 0x50 && data[i + 1].toInt() == 0x4b &&
                data[i + 2].toInt() == 0x05 && data[i + 3].toInt() == 0x06
            ) {
                return i
            }
        }
        throw IOException("No EOCD found in unsigned APK")
    }

    private fun getCdOffset(data: ByteArray, eocdOff: Int): Long {
        val buf = ByteBuffer.wrap(data, eocdOff + 16, 4).order(ByteOrder.LITTLE_ENDIAN)
        return buf.getInt().toLong() and 0xFFFFFFFFL
    }

    // ---- Asset loading ----

    private fun loadPayloadAsset(port: Int): ByteArray {
        val data = try {
            appContext.assets.open("tftf_payload.bin").use { it.readBytes() }
        } catch (_: IOException) {
            throw IOException("Bundled server payload is missing. Run android/tools/prepare-assets.sh before building the patcher")
        }
        if (data.size < 64 || !data.copyOfRange(0, 8).contentEquals("TFTFPAY\u0000".toByteArray(Charsets.US_ASCII))) {
            throw IOException("Bundled server payload is invalid or incomplete")
        }
        val version = ByteBuffer.wrap(data).order(ByteOrder.LITTLE_ENDIAN).getInt(8)
        val payloadPort = ByteBuffer.wrap(data).order(ByteOrder.LITTLE_ENDIAN).getInt(16)
        val total = ByteBuffer.wrap(data).order(ByteOrder.LITTLE_ENDIAN).getInt(12).toLong() and 0xffffffffL
        if (version != 1 || total != data.size.toLong()) throw IOException("Bundled server payload failed integrity checks")
        if (payloadPort != port) throw IOException("Bundled payload is for port $payloadPort, but this build requests port $port; regenerate assets for that port")
        return data
    }

    private fun readUriBytes(uri: Uri): ByteArray {
        appContext.contentResolver.openInputStream(uri)?.use { input ->
            return input.readBytes()
        } ?: throw IOException("Cannot read: $uri")
    }

    // ---- Helpers ----

    private fun checkCancelled() {
        if (cancelled.get()) throw CancellationException("Patch cancelled")
    }

    private fun reportStep(
        onStep: (StepProgress) -> Unit,
        onLog: (LogLine) -> Unit,
        index: Int,
        total: Int,
        name: String
    ) {
        onStep(StepProgress(PatcherState.RUNNING, name, index, total))
        onLog(LogLine("== [$index/$total] $name =="))
    }
}

/**
 * Bridge from Android ParcelFileDescriptor to the ZipReader's SeekableByteChannel.
 * Supports random-access reads needed for ZIP parsing.
 */
class ParcelFileDescriptorChannel(
    private val fd: ParcelFileDescriptor
) : SeekableByteChannel {
    private val raf = RandomAccessFile("/proc/self/fd/${fd.fd}", "r")
    private val length = raf.length()

    override fun size(): Long = length

    override fun read(position: Long, buffer: ByteArray, offset: Int, length: Int): Int {
        synchronized(raf) {
            raf.seek(position)
            return raf.read(buffer, offset, length)
        }
    }

    override fun close() {
        try { raf.close() } catch (_: Exception) {}
        try { fd.close() } catch (_: Exception) {}
    }
}

/** File-backed channel used for post-build ZIP layout validation. */
class FileSeekableByteChannel(
    file: File
) : SeekableByteChannel {
    private val raf = RandomAccessFile(file, "r")

    override fun size(): Long = raf.length()

    override fun read(position: Long, buffer: ByteArray, offset: Int, length: Int): Int {
        synchronized(raf) {
            raf.seek(position)
            return raf.read(buffer, offset, length)
        }
    }

    override fun close() {
        raf.close()
    }
}
