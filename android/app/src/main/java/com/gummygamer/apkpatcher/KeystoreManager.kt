package com.gummygamer.apkpatcher

import org.bouncycastle.asn1.x500.X500Name
import org.bouncycastle.cert.jcajce.JcaX509CertificateConverter
import org.bouncycastle.cert.jcajce.JcaX509v3CertificateBuilder
import org.bouncycastle.jce.provider.BouncyCastleProvider
import org.bouncycastle.operator.jcajce.JcaContentSignerBuilder
import java.io.ByteArrayInputStream
import java.io.ByteArrayOutputStream
import java.io.File
import java.math.BigInteger
import java.security.KeyPairGenerator
import java.security.KeyStore
import java.security.PrivateKey
import java.security.SecureRandom
import java.security.cert.Certificate
import java.security.cert.X509Certificate
import java.util.Date

/** Portable on-device signing identity management. */
object KeystoreManager {
    private const val DEFAULT_ALIAS = "patcher"
    private const val DEFAULT_DN = "CN=TFTF Patcher"
    private const val DEFAULT_PASSWORD = "android"
    private const val KEY_SIZE = 2048
    private const val VALIDITY_DAYS = 36500L

    /** Generate a PKCS12 identity without relying on Android's provider named "BC". */
    fun generateKeystore(password: CharArray, alias: String = DEFAULT_ALIAS): GeneratedKeystore {
        val keyGen = KeyPairGenerator.getInstance("RSA")
        keyGen.initialize(KEY_SIZE, SecureRandom())
        val keyPair = keyGen.generateKeyPair()
        val now = Date()
        val expiry = Date(now.time + VALIDITY_DAYS * 24L * 60L * 60L * 1000L)
        val subject = X500Name(DEFAULT_DN)
        val builder = JcaX509v3CertificateBuilder(
            subject, BigInteger.valueOf(now.time).abs(), now, expiry, subject, keyPair.public
        )
        // Use an application-owned provider instance. Some Android releases expose a
        // provider named BC whose X.509 implementation is incomplete or incompatible
        // with the Bouncy Castle PKIX classes bundled by this app.
        val provider = BouncyCastleProvider()
        val signer = JcaContentSignerBuilder("SHA256withRSA")
            .setProvider(provider)
            .build(keyPair.private)
        val cert = JcaX509CertificateConverter().setProvider(provider)
            .getCertificate(builder.build(signer))
        val ks = KeyStore.getInstance("PKCS12")
        ks.load(null, password)
        ks.setKeyEntry(alias, keyPair.private, password, arrayOf<Certificate>(cert))
        val bytes = ByteArrayOutputStream().also { ks.store(it, password) }.toByteArray()
        return GeneratedKeystore(bytes, keyPair.private, cert, alias)
    }

    /**
     * Load JKS or PKCS12, preferring the requested alias and never guessing a
     * password. Key stores may use a different password for the private key,
     * so keep the two credentials separate all the way to getKey().
     */
    fun loadKeystore(
        keystoreBytes: ByteArray,
        password: CharArray,
        alias: String = DEFAULT_ALIAS,
        keyPassword: CharArray = password
    ): LoadedKeystore? =
        tryLoad(keystoreBytes, password, keyPassword, alias, "PKCS12") ?:
            tryLoad(keystoreBytes, password, keyPassword, alias, "JKS")

    /** Keep the generated identity stable so repeated patches can update an installed APK. */
    fun loadOrCreateDefault(storage: File, alias: String = DEFAULT_ALIAS): LoadedKeystore {
        val password = DEFAULT_PASSWORD.toCharArray()
        if (storage.isFile) {
            loadKeystore(storage.readBytes(), password, alias)?.let { return it }
            throw IllegalStateException("The app signing identity could not be read; check its format and password")
        }
        // Versions before the provider fix generated a JKS at this location. Load it
        // first so upgrading the patcher never silently changes the signing identity.
        val legacy = File(storage.parentFile, "patcher-signing.jks")
        if (legacy.isFile) {
            val loaded = loadKeystore(legacy.readBytes(), password, alias)
                ?: throw IllegalStateException("The legacy app signing identity could not be read")
            writePkcs12(storage, password, loaded)
            return loaded
        }
        storage.parentFile?.mkdirs()
        val generated = generateKeystore(password, alias)
        val part = File(storage.parentFile, "${storage.name}.part")
        try {
            part.outputStream().use { out ->
                out.write(generated.keystoreBytes)
                out.flush()
                out.fd.sync()
            }
            if (!part.renameTo(storage)) throw IllegalStateException("Unable to save the app signing identity")
        } finally { part.delete() }
        return LoadedKeystore(generated.privateKey, generated.certificate, generated.alias)
    }

    private fun tryLoad(
        bytes: ByteArray,
        password: CharArray,
        keyPassword: CharArray,
        alias: String,
        type: String
    ): LoadedKeystore? = try {
        val ks = KeyStore.getInstance(type)
        ks.load(ByteArrayInputStream(bytes), password)
        val effective = if (ks.containsAlias(alias)) alias else ks.aliases().asSequence().firstOrNull() ?: return null
        val key = ks.getKey(effective, keyPassword) as? PrivateKey ?: return null
        val cert = ks.getCertificate(effective) as? X509Certificate ?: return null
        LoadedKeystore(key, cert, effective)
    } catch (_: Exception) { null }

    private fun writePkcs12(storage: File, password: CharArray, loaded: LoadedKeystore) {
        storage.parentFile?.mkdirs()
        val ks = KeyStore.getInstance("PKCS12")
        ks.load(null, password)
        ks.setKeyEntry(loaded.alias, loaded.privateKey, password, arrayOf<Certificate>(loaded.certificate))
        val part = File(storage.parentFile, "${storage.name}.part")
        try {
            part.outputStream().use { out -> ks.store(out, password); out.flush(); out.fd.sync() }
            if (!part.renameTo(storage)) throw IllegalStateException("Unable to save the app signing identity")
        } finally { part.delete() }
    }

    data class GeneratedKeystore(val keystoreBytes: ByteArray, val privateKey: PrivateKey, val certificate: X509Certificate, val alias: String)
    data class LoadedKeystore(val privateKey: PrivateKey, val certificate: X509Certificate, val alias: String)
}
