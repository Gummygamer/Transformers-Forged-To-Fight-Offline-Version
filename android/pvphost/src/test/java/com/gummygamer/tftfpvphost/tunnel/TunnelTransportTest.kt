package com.gummygamer.tftfpvphost.tunnel

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test
import java.io.IOException

class TunnelTransportTest {
    private fun config(role: TunnelConfig.Role = TunnelConfig.Role.HOST): TunnelConfig {
        val invite = TunnelConfig.newInvitation()
        return if (role == TunnelConfig.Role.HOST) {
            TunnelConfig("relay.example", 4433, invite.session, invite.hostToken, role, invite.joinToken)
        } else {
            TunnelConfig("relay.example", 4433, invite.session, invite.joinToken, role)
        }
    }

    @Test
    fun invitationIsValidAndUnique() {
        val first = TunnelConfig.newInvitation()
        val second = TunnelConfig.newInvitation()
        assertNotEquals(first.session, second.session)
        assertNotEquals(first.token, second.token)
        assertNotEquals(first.hostToken, first.joinToken)
        assertTrue(first.session.length >= 16)
        assertTrue(first.token.length >= 32)
        assertNull(config().validationError())
    }

    @Test
    fun invalidRelayAndLocalPortsAreActionable() {
        val valid = config()
        assertTrue(valid.copy(relayHost = "relay/example").validationError()!!.contains("host"))
        assertTrue(valid.copy(relayPort = 0).validationError()!!.contains("port"))
        assertTrue(valid.copy(httpPort = 80).validationError()!!.contains("HTTP"))
        assertTrue(valid.copy(combatPort = 80).validationError()!!.contains("combat"))
    }

    @Test
    fun combatFramesAreBoundedAndAllowFragmentedReads() {
        val packet = ByteArray(4)
        packet[0] = 0
        packet[1] = 0
        packet[2] = 1
        packet[3] = 0
        assertEquals(256, TunnelClient.frameLength(packet))
        packet[0] = 2
        assertThrowsIOException { TunnelClient.frameLength(packet) }
    }

    @Test
    fun joinUsesTheSameValidatedCredentialShape() {
        val host = config(TunnelConfig.Role.HOST)
        val join = TunnelConfig(
            host.relayHost, host.relayPort, host.session,
            TunnelConfig.newInvitation().joinToken, TunnelConfig.Role.JOIN,
        )
        assertEquals(host.session, join.session)
        assertTrue(join.token.isNotEmpty())
        assertFalse(join.validationError().orEmpty().contains("role"))
    }

    @Test
    fun invitationCodecRoundTripsWithoutChangingBearerParts() {
        val source = TunnelConfig.newInvitation()
        val encoded = InvitationCodec.encode(source)
        val decoded = InvitationCodec.decode(encoded)
        assertEquals(source, decoded)
        assertTrue(encoded.startsWith("TFTF1|"))
    }

    @Test
    fun invitationCodecRejectsIncompleteOrExtraFields() {
        assertNull(InvitationCodec.decode("TFTF1|session-only"))
        assertNull(InvitationCodec.decode("TFTF1|abcdefgh|token1234|invite1234|extra"))
        assertNull(InvitationCodec.decode("TFTF1|short|token1234|invite1234"))
    }

    @Test
    fun tunnelStatusStartsIdleAndHostRequiresAuthority() {
        val invitation = TunnelConfig.newInvitation()
        val missingAuthority = TunnelConfig(
            "relay.example", 4433, invitation.session, invitation.hostToken,
            TunnelConfig.Role.HOST,
        )
        assertEquals(TunnelState.IDLE, TunnelSession(missingAuthority).status.value.state)
        assertTrue(missingAuthority.validationError()!!.contains("invitation"))
    }

    private fun assertThrowsIOException(block: () -> Unit) {
        try {
            block()
            throw AssertionError("expected IOException")
        } catch (_: IOException) {
            // expected
        }
    }
}
