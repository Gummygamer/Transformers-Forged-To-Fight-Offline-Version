package com.gummygamer.tftfpvphost.ui

import java.net.InetAddress
import org.junit.Test
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue

class HostInputTest {
    @Test
    fun portAcceptsBoundsAndRejectsOthers() {
        assertEquals(8080, (HostInput.port(" 8080 ") as Parsed.Ok).value)
        assertEquals(ParseError.PORT_RANGE, (HostInput.port("80") as Parsed.Bad).error)
        assertEquals(ParseError.PORT_RANGE, (HostInput.port("65536") as Parsed.Bad).error)
        assertEquals(ParseError.PORT_NOT_NUMBER, (HostInput.port("abc") as Parsed.Bad).error)
    }

    @Test
    fun ttlAcceptsMilliseconds() {
        assertEquals(15_000L, (HostInput.ttlMs("15000") as Parsed.Ok).value)
        assertEquals(ParseError.TTL_RANGE, (HostInput.ttlMs("0") as Parsed.Bad).error)
        assertEquals(ParseError.TTL_NOT_NUMBER, (HostInput.ttlMs("") as Parsed.Bad).error)
    }

    @Test
    fun onlyLanIpv4IsAdvertisable() {
        assertTrue(HostAddresses.isAdvertisable(InetAddress.getByName("192.168.1.20")))
        assertFalse(HostAddresses.isAdvertisable(InetAddress.getByName("127.0.0.1")))
        assertFalse(HostAddresses.isAdvertisable(InetAddress.getByName("169.254.3.4")))
        assertFalse(HostAddresses.isAdvertisable(InetAddress.getByName("::1")))
    }
}
