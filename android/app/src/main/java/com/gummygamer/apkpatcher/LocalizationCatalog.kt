package com.gummygamer.apkpatcher

import org.json.JSONException
import org.json.JSONObject
import java.io.ByteArrayOutputStream
import java.io.IOException
import java.nio.ByteBuffer
import java.nio.charset.CodingErrorAction
import java.util.zip.ZipInputStream

/** Locale inventory and catalog ZIP contract shared by the patcher and catalog authoring. */
object LocalizationCatalog {
    const val APK_PREFIX = "assets/tftf_localization/"
    const val MAX_ARCHIVE_BYTES = 32 * 1024 * 1024
    const val MAX_CATALOG_BYTES = 4 * 1024 * 1024

    val supportedLocales = listOf(
        "ar", "de", "en", "es", "fr", "id", "it", "ja", "ko", "nl", "no", "pt", "ru", "th", "tr", "zh-CN", "zh-TW"
    )

    data class CatalogSet(val files: Map<String, ByteArray>, val activeLocale: String)

    /** Write validated APK assets as stored entries, preserving the catalog bytes exactly. */
    fun writeAssets(writer: ZipWriter, files: Map<String, ByteArray>) {
        for ((name, bytes) in files) writer.writeStored(name, bytes)
    }

    fun replacesExistingAsset(name: String): Boolean = name.startsWith(APK_PREFIX)

    /** Canonicalize documented locale tags while preserving Simplified/Traditional Chinese. */
    fun canonicalize(tag: String): String? {
        val normalized = tag.trim().replace('_', '-').lowercase()
        if (normalized.isEmpty()) return null
        val parts = normalized.split('-')
        val lang = when (parts.first()) { "in" -> "id"; else -> parts.first() }
        if (lang == "zh") {
            val explicitScript = parts.drop(1).firstOrNull { it == "hans" || it == "hant" }
            val region = parts.drop(1).firstOrNull { it in setOf("cn", "sg", "tw", "hk", "mo") }
            val simplified = when {
                explicitScript != null -> explicitScript == "hans"
                region != null -> region == "cn" || region == "sg"
                else -> true
            }
            return if (simplified) "zh-CN" else "zh-TW"
        }
        return lang.takeIf { it in supportedLocales }
    }

    /** Exact supported locale, then canonical language, then English. */
    fun resolve(requested: String, available: Set<String>): String {
        val canonical = canonicalize(requested) ?: "en"
        return when {
            canonical in available -> canonical
            canonical.substringBefore('-') in available -> canonical.substringBefore('-')
            "en" in available -> "en"
            else -> throw IOException("Localization catalog has no $canonical, base-language, or English fallback")
        }
    }

    /** Read and validate `locales/<tag>.json` entries from a bounded ZIP archive. */
    fun readZip(input: java.io.InputStream, requestedLocale: String): CatalogSet {
        val raw = linkedMapOf<String, ByteArray>()
        var total = 0
        ZipInputStream(input).use { zip ->
            while (true) {
                val entry = zip.nextEntry ?: break
                if (entry.isDirectory) continue
                if (entry.name.contains("..") || entry.name.startsWith('/') || entry.name.contains('\\')) {
                    throw IOException("Localization ZIP contains an unsafe path: ${entry.name}")
                }
                val match = Regex("^locales/([A-Za-z0-9_-]+)\\.json$").matchEntire(entry.name)
                    ?: throw IOException("Unexpected localization ZIP entry '${entry.name}'; expected locales/<locale>.json")
                val locale = canonicalize(match.groupValues[1])
                    ?: throw IOException("Unsupported locale in localization ZIP: ${match.groupValues[1]}")
                if (locale in raw) throw IOException("Localization ZIP contains duplicate catalog for $locale")
                val bytes = ByteArrayOutputStream()
                val buffer = ByteArray(8192)
                var size = 0
                while (true) {
                    val count = zip.read(buffer)
                    if (count < 0) break
                    size += count
                    total += count
                    if (size > MAX_CATALOG_BYTES || total > MAX_ARCHIVE_BYTES) {
                        throw IOException("Localization ZIP exceeds the 32 MiB total or 4 MiB per-catalog limit")
                    }
                    bytes.write(buffer, 0, count)
                }
                raw[locale] = bytes.toByteArray()
                zip.closeEntry()
            }
        }
        val missing = supportedLocales.filterNot(raw::containsKey)
        if (missing.isNotEmpty()) throw IOException("Localization ZIP is missing catalogs: ${missing.joinToString()}")
        validateCatalogs(raw)
        val active = resolve(requestedLocale, raw.keys)
        val apkFiles = raw.mapKeys { (locale, _) -> "$APK_PREFIX$locale.json" }.toMutableMap()
        apkFiles["${APK_PREFIX}active_locale.txt"] = active.toByteArray(Charsets.US_ASCII)
        return CatalogSet(apkFiles, active)
    }

    private fun validateCatalogs(catalogs: Map<String, ByteArray>) {
        val parsed = catalogs.mapValues { (locale, bytes) -> parseCatalog(locale, bytes) }
        val baseline = parsed["en"] ?: throw IOException("Localization ZIP must include English")
        for (locale in supportedLocales) {
            val entries = parsed[locale] ?: throw IOException("Localization ZIP is missing $locale")
            if (entries.keys.toList() != baseline.keys.toList()) {
                val missing = baseline.keys - entries.keys
                val extra = entries.keys - baseline.keys
                throw IOException("$locale catalog keys/order differ from English (missing: ${missing.joinToString().ifBlank { "none" }}; extra: ${extra.joinToString().ifBlank { "none" }})")
            }
            for ((key, translated) in entries) {
                val source = baseline.getValue(key)
                if (placeholderTokens(source) != placeholderTokens(translated)) {
                    throw IOException("$locale translation '$key' has different formatting placeholders than English")
                }
                if (markupTokens(source) != markupTokens(translated)) {
                    throw IOException("$locale translation '$key' has different markup tags than English")
                }
            }
        }
    }

    private fun parseCatalog(locale: String, bytes: ByteArray): LinkedHashMap<String, String> {
        val text = try {
            Charsets.UTF_8.newDecoder().onMalformedInput(CodingErrorAction.REPORT)
                .onUnmappableCharacter(CodingErrorAction.REPORT).decode(ByteBuffer.wrap(bytes)).toString()
        } catch (e: Exception) {
            throw IOException("$locale catalog is not valid UTF-8", e)
        }
        try {
            val root = JSONObject(text)
            if (!root.has("meta") || root.opt("meta") !is JSONObject) throw IOException("$locale catalog must contain a meta object")
            val array = root.optJSONArray("strings") ?: throw IOException("$locale catalog must contain a strings array")
            val result = LinkedHashMap<String, String>()
            for (index in 0 until array.length()) {
                val item = array.optJSONObject(index) ?: throw IOException("$locale catalog strings[$index] must be an object")
                val keyValue = item.opt("k")
                val translationValue = item.opt("v")
                if (keyValue !is String || translationValue !is String) {
                    throw IOException("$locale catalog strings[$index] must have string k and v fields")
                }
                val key = keyValue
                val value = translationValue
                if (key.isBlank()) throw IOException("$locale catalog strings[$index] has an empty key")
                if (value.isBlank()) throw IOException("$locale catalog translation '$key' is empty")
                if (result.put(key, value) != null) throw IOException("$locale catalog contains duplicate key '$key'")
            }
            if (result.isEmpty()) throw IOException("$locale catalog contains no strings")
            return result
        } catch (e: JSONException) {
            throw IOException("$locale catalog is malformed JSON: ${e.message}", e)
        }
    }

    private fun placeholderTokens(value: String): List<String> = Regex("%[0-9$]*[sdif]|\\{[A-Za-z_][A-Za-z0-9_.-]*}").findAll(value).map { it.value }.sorted().toList()
    private fun markupTokens(value: String): List<String> = Regex("</?[A-Za-z][A-Za-z0-9]*(?:\\s+[^<>]*?)?/?>").findAll(value).map { it.value }.sorted().toList()
}
