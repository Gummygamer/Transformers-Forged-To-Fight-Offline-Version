package com.gummygamer.apkpatcher

import org.junit.Assert.*
import org.junit.Test
import java.io.ByteArrayInputStream
import java.io.ByteArrayOutputStream
import java.io.IOException
import java.util.zip.ZipEntry
import java.util.zip.ZipInputStream
import java.util.zip.ZipOutputStream

class LocalizationCatalogTest {
    @Test fun `locale aliases normalize deterministically and preserve Chinese script`() {
        assertEquals("id", LocalizationCatalog.canonicalize(" IN_id "))
        assertEquals("it", LocalizationCatalog.canonicalize("IT_it"))
        assertEquals("zh-CN", LocalizationCatalog.canonicalize("zh-Hans"))
        assertEquals("zh-CN", LocalizationCatalog.canonicalize("zh_SG"))
        assertEquals("zh-TW", LocalizationCatalog.canonicalize("ZH-hant"))
        assertEquals("zh-TW", LocalizationCatalog.canonicalize("zh-HK"))
        assertEquals("fr", LocalizationCatalog.resolve("fr-CA", setOf("fr", "en")))
        assertEquals("en", LocalizationCatalog.resolve("ja", setOf("en")))
        try {
            LocalizationCatalog.resolve("ja", emptySet())
            fail("expected absent fallback failure")
        } catch (_: IOException) { }
    }

    @Test fun `request validates normalized English and explicit locale selection`() {
        val english = validRequest(gameLocale = "EN_us")
        assertTrue(english.validate().errors.toString(), english.validate().isValid)
        assertTrue(validRequest(gameLocale = "in", localizationCatalogUri = "content://catalog").validate().isValid)
        assertFalse(validRequest(gameLocale = "id").validate().isValid)
    }

    @Test fun `complete catalog zip validates and preserves bytes in packaged assets`() {
        val input = catalogZip()
        val catalog = LocalizationCatalog.readZip(ByteArrayInputStream(input), "fr-CA")
        assertEquals("fr", catalog.activeLocale)
        assertArrayEquals(catalogBytes("fr"), catalog.files["${LocalizationCatalog.APK_PREFIX}fr.json"])
        assertArrayEquals("fr".toByteArray(), catalog.files["${LocalizationCatalog.APK_PREFIX}active_locale.txt"])

        val output = ByteArrayOutputStream()
        val writer = ZipWriter(output)
        LocalizationCatalog.writeAssets(writer, catalog.files)
        writer.finish()
        val entries = ZipInputStream(ByteArrayInputStream(output.toByteArray())).use { zip ->
            buildMap { while (true) { val entry = zip.nextEntry ?: break; put(entry.name, zip.readBytes()) } }
        }
        assertEquals(catalog.files.keys, entries.keys)
        for ((path, bytes) in catalog.files) assertArrayEquals(bytes, entries[path])
        assertTrue(LocalizationCatalog.replacesExistingAsset("${LocalizationCatalog.APK_PREFIX}fr.json"))
        assertFalse(LocalizationCatalog.replacesExistingAsset("assets/unrelated.json"))
    }

    @Test fun `catalog rejects incomplete unsafe duplicate malformed and invalid translations`() {
        assertRejected(zipOf("locales/en.json" to catalogBytes("en")))
        assertRejected(zipOf("../locales/ar.json" to byteArrayOf(1)))
        val aliasDuplicate = zipOf(*(LocalizationCatalog.supportedLocales.map { "locales/$it.json" to catalogBytes(it) } + ("locales/in.json" to catalogBytes("id"))).toTypedArray())
        assertRejected(aliasDuplicate)
        val oversized = catalogZip { locale ->
            if (locale == "fr") ByteArray(LocalizationCatalog.MAX_CATALOG_BYTES + 1) else catalogBytes(locale)
        }
        assertRejected(oversized)

        val wrongTypes = catalogZip { locale -> if (locale == "fr") """{"meta":{},"strings":[{"k":12,"v":"bonjour"}]}""".toByteArray() else catalogBytes(locale) }
        assertRejected(wrongTypes)
        val badUtf8 = catalogZip { locale -> if (locale == "fr") byteArrayOf(0xc3.toByte(), 0x28) else catalogBytes(locale) }
        assertRejected(badUtf8)
        val changedPlaceholder = catalogZip { locale ->
            if (locale == "fr") """{"meta":{},"strings":[{"k":"greeting","v":"Salut {player}"}]}""".toByteArray()
            else catalogBytes(locale)
        }
        assertRejected(changedPlaceholder)
        val changedMarkup = catalogZip { locale ->
            if (locale == "fr") """{"meta":{},"strings":[{"k":"greeting","v":"<b>Salut {name}</i>"}]}""".toByteArray()
            else catalogBytes(locale)
        }
        assertRejected(changedMarkup)
    }

    @Test fun `catalog rejects malformed JSON and duplicate keys`() {
        assertRejected(catalogZip { locale -> if (locale == "fr") "{".toByteArray() else catalogBytes(locale) })
        assertRejected(catalogZip { locale -> if (locale == "fr") """{"meta":{},"strings":[{"k":"greeting","v":"a"},{"k":"greeting","v":"b"}]}""".toByteArray() else catalogBytes(locale) })
    }

    private fun assertRejected(bytes: ByteArray) {
        try {
            LocalizationCatalog.readZip(ByteArrayInputStream(bytes), "en")
            fail("expected catalog rejection")
        } catch (_: IOException) { }
    }

    private fun catalogZip(render: (String) -> ByteArray = ::catalogBytes): ByteArray = zipOf(
        *LocalizationCatalog.supportedLocales.map { "locales/$it.json" to render(it) }.toTypedArray()
    )

    private fun catalogBytes(locale: String) = """{"meta":{"locale":"$locale"},"strings":[{"k":"greeting","v":"Hello {name}"}]}""".toByteArray()

    private fun zipOf(vararg entries: Pair<String, ByteArray>): ByteArray {
        val output = ByteArrayOutputStream()
        ZipOutputStream(output).use { zip ->
            for ((name, bytes) in entries) {
                zip.putNextEntry(ZipEntry(name)); zip.write(bytes); zip.closeEntry()
            }
        }
        return output.toByteArray()
    }

    private fun validRequest(gameLocale: String, localizationCatalogUri: String = "") = PatchRequest(
        sourceApkUri = "content://apk", outputName = "out.apk", abi = PatchRequest.ARM64,
        serverMode = PatchRequest.BUNDLED, serverHost = "127.0.0.1", serverPort = 8080,
        scheme = "http", keepOtherAbi = false, patchedIl2cppUri = "", autoPatchIl2cpp = true,
        keystoreUri = "", keystorePassword = charArrayOf(), keyPassword = charArrayOf(), keyAlias = "patcher",
        offerInstall = true, localizationCatalogUri = localizationCatalogUri, gameLocale = gameLocale
    )
}
