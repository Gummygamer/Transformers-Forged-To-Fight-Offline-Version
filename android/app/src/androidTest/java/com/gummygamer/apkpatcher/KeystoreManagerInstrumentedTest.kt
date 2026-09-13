package com.gummygamer.apkpatcher

import androidx.test.ext.junit.runners.AndroidJUnit4
import java.io.ByteArrayInputStream
import java.security.KeyStore
import java.security.cert.CertificateFactory
import java.security.cert.X509Certificate
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertTrue
import org.junit.Test
import org.junit.runner.RunWith

/** Provider and keystore checks that must execute on an Android runtime. */
@RunWith(AndroidJUnit4::class)
class KeystoreManagerInstrumentedTest {
    @Test
    fun generatedIdentityUsesAndroidCompatibleProviders() {
        val password = "device-test".toCharArray()
        val generated = KeystoreManager.generateKeystore(password, "device")

        val store = KeyStore.getInstance("PKCS12")
        store.load(ByteArrayInputStream(generated.keystoreBytes), password)
        assertTrue(store.containsAlias("device"))
        val loadedCertificate = store.getCertificate("device") as X509Certificate
        assertArrayEquals(generated.certificate.encoded, loadedCertificate.encoded)

        val parsed = CertificateFactory.getInstance("X.509")
            .generateCertificate(ByteArrayInputStream(generated.certificate.encoded)) as X509Certificate
        assertNotNull(parsed)
        assertEquals(generated.certificate.publicKey, parsed.publicKey)

        val loaded = KeystoreManager.loadKeystore(generated.keystoreBytes, password, "device")
        assertNotNull(loaded)
        assertEquals(generated.certificate.publicKey, loaded!!.certificate.publicKey)
    }
}
