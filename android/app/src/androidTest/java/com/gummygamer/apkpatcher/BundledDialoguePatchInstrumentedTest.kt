package com.gummygamer.apkpatcher

import android.net.Uri
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import java.io.File
import java.util.zip.ZipFile
import kotlinx.coroutines.runBlocking
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertTrue
import org.junit.Assume.assumeTrue
import org.junit.Test
import org.junit.runner.RunWith

/** Opt-in end-to-end patch using a locally supplied, legally obtained game APK. */
@RunWith(AndroidJUnit4::class)
class BundledDialoguePatchInstrumentedTest {
    @Test
    fun ordinaryPatchBundlesDialoguesWithoutTranslationInputs() = runBlocking {
        val instrumentation = InstrumentationRegistry.getInstrumentation()
        val context = instrumentation.targetContext
        val path = InstrumentationRegistry.getArguments().getString("dialogueSourceApk")
        assumeTrue("Supply dialogueSourceApk to run the full game patch check", path != null)
        val source = File(path!!)
        assertTrue("Source APK must be readable by the patcher", source.isFile && source.canRead())
        val request = PatchRequest(
            sourceApkUri = Uri.fromFile(source).toString(), outputName = "dialogue-verification.apk",
            abi = PatchRequest.ARM64, serverMode = PatchRequest.BUNDLED,
            serverHost = "127.0.0.1", serverPort = 8080, scheme = "http",
            keepOtherAbi = false, patchedIl2cppUri = "", autoPatchIl2cpp = true,
            keystoreUri = "", keystorePassword = charArrayOf(), keyPassword = charArrayOf(),
            keyAlias = "patcher", offerInstall = false
        )
        val outcome = PatcherEngine(context).patch(request)
        assertTrue("Ordinary patch must succeed: $outcome", outcome is PatchOutcome.Success)
        val apk = File(Uri.parse((outcome as PatchOutcome.Success).outputApkUri).path!!)
        ZipFile(apk).use { zip ->
            val hook = zip.getEntry("lib/arm64-v8a/libdothook.so")
            assertTrue("Patched game must contain the bundled hook", hook != null)
            // The normal request's default session and frame rate leave the asset unchanged.
            val expected = context.assets.open("libdothook-arm64.bin").use { it.readBytes() }
            assertArrayEquals(expected, zip.getInputStream(hook).use { it.readBytes() })
            assertTrue(zip.getEntry(PatcherEngine.PAYLOAD_ASSET) != null)
            assertTrue(zip.entries().asSequence().none { it.name.startsWith("assets/tftf_localization/") })
        }
    }
}
