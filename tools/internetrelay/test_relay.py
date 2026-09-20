import asyncio
import os
import shutil
import ssl
import subprocess
import tempfile
import unittest
from unittest.mock import patch

from tools.internetrelay.protocol import Hello, ProtocolError, decode_frame_header, encode_frame, encode_hello
from tools.internetrelay.relay import Relay, tls_server_context
import tools.internetrelay.relay as relay_module


class RelayTest(unittest.IsolatedAsyncioTestCase):
    async def asyncSetUp(self):
        self.relay = Relay()
        self.port = await self.relay.start("127.0.0.1", 0)

    async def asyncTearDown(self):
        await self.relay.close()

    async def channel(self, role="host", session="session-1234", token=None, channel="http", invite="invite-1234"):
        reader, writer = await asyncio.open_connection("127.0.0.1", self.port)
        credential = token or ("token-1234" if role == "host" else invite)
        writer.write(encode_hello(Hello(session, role, credential, channel, invite if role == "host" else "")))
        await writer.drain()
        return reader, writer

    async def test_http_is_bidirectional_and_session_scoped(self):
        host_reader, host_writer = await self.channel("host")
        join_reader, join_writer = await self.channel("join")

        join_writer.write(b"GET /pvp/lobby HTTP/1.1\r\nContent-Length: 0\r\n\r\n")
        await join_writer.drain()
        self.assertEqual(
            b"GET /pvp/lobby HTTP/1.1\r\nContent-Length: 0\r\n\r\n",
            await host_reader.readexactly(46),
        )
        host_writer.write(b"HTTP/1.1 200 OK\r\nContent-Length: 2\r\n\r\nOK")
        await host_writer.drain()
        host_writer.close()
        self.assertEqual(b"HTTP/1.1 200 OK\r\nContent-Length: 2\r\n\r\nOK", await join_reader.read())
        join_writer.close()
        await host_writer.wait_closed(); await join_writer.wait_closed()

    async def test_relay_only_forwards_through_the_authenticated_session(self):
        first_reader, first_writer = await self.channel("host", session="first-1234", token="first-token", invite="first-invite")
        first_join_reader, first_join_writer = await self.channel("join", session="first-1234", token="first-invite")
        second_reader, second_writer = await self.channel("host", session="second-1234", token="second-token", invite="second-invite")
        second_join_reader, second_join_writer = await self.channel("join", session="second-1234", token="second-invite")

        first_writer.write(b"first-session")
        await first_writer.drain()
        self.assertEqual(b"first-session", await first_join_reader.readexactly(len(b"first-session")))
        second_join_writer.write(b"second-session")
        await second_join_writer.drain()
        self.assertEqual(b"second-session", await second_reader.readexactly(len(b"second-session")))
        second_join_reader_task = asyncio.create_task(second_join_reader.read(1))
        first_join_writer.write(b"x")
        await first_join_writer.drain()
        with self.assertRaises(asyncio.TimeoutError):
            await asyncio.wait_for(second_join_reader_task, 0.05)
        for writer in (first_writer, first_join_writer, second_writer, second_join_writer):
            writer.close()
        for writer in (first_writer, first_join_writer, second_writer, second_join_writer):
            await writer.wait_closed()

    async def test_peer_wait_timeout_closes_unpaired_channel(self):
        with patch.object(relay_module, "PEER_WAIT_TIMEOUT_SECONDS", 0.05):
            reader, writer = await self.channel("host", session="timeout-1234", token="timeout-token", invite="timeout-invite")
            self.assertEqual(b"", await asyncio.wait_for(reader.read(), 0.5))
            writer.close()
            await writer.wait_closed()
        self.assertNotIn("timeout-1234", self.relay.sessions)

    async def test_expired_session_rejects_new_join(self):
        host_reader, host_writer = await self.channel()
        with patch.object(relay_module, "SESSION_TTL_SECONDS", 0):
            reader, writer = await self.channel("join")
            self.assertEqual(b"", await asyncio.wait_for(reader.read(), 0.5))
            writer.close()
            await writer.wait_closed()
        host_writer.close()
        await host_writer.wait_closed()
        self.assertNotIn("session-1234", self.relay.sessions)

    async def test_relay_failure_disconnects_both_paired_channels(self):
        host_reader, host_writer = await self.channel()
        join_reader, join_writer = await self.channel("join")
        await asyncio.sleep(0.06)
        await self.relay.close()
        self.assertEqual(b"", await asyncio.wait_for(host_reader.read(), 0.5))
        self.assertEqual(b"", await asyncio.wait_for(join_reader.read(), 0.5))
        host_writer.close(); join_writer.close()
        await host_writer.wait_closed(); await join_writer.wait_closed()

    async def test_repeated_pair_cleanup_does_not_retain_sessions(self):
        for index in range(12):
            session = f"repeat-{index:04d}"
            host_reader, host_writer = await self.channel("host", session=session, token=f"host-{index:04d}", invite=f"invite-{index:04d}")
            join_reader, join_writer = await self.channel("join", session=session, token=f"invite-{index:04d}")
            await asyncio.sleep(0.06)
            host_writer.close(); join_writer.close()
            await host_writer.wait_closed(); await join_writer.wait_closed()
            await asyncio.sleep(0.01)
        self.assertEqual({}, self.relay.sessions)

    async def test_combat_frames_preserve_datagram_boundaries_when_fragmented(self):
        host_reader, host_writer = await self.channel("host", channel="combat")
        join_reader, join_writer = await self.channel("join", channel="combat")
        first = encode_frame(b"HELLO")
        second = encode_frame(b"EV" * 40)
        for part in (first[:2], first[2:] + second[:1], second[1:]):
            join_writer.write(part)
            await join_writer.drain()
        self.assertEqual(b"HELLO", await self.read_frame(host_reader))
        self.assertEqual(b"EV" * 40, await self.read_frame(host_reader))
        host_writer.write(encode_frame(b"BYE"))
        await host_writer.drain()
        self.assertEqual(b"BYE", await self.read_frame(join_reader))
        host_writer.close(); join_writer.close()
        await host_writer.wait_closed(); await join_writer.wait_closed()

    async def test_cross_session_and_role_misuse_are_rejected(self):
        join_reader, join_writer = await self.channel("join", session="unknown-1234")
        self.assertEqual(b"", await join_reader.read())
        join_writer.close(); await join_writer.wait_closed()

        host_reader, host_writer = await self.channel("host")
        wrong_reader, wrong_writer = await self.channel("join", token="other-token")
        self.assertEqual(b"", await wrong_reader.read())
        wrong_writer.close(); await wrong_writer.wait_closed()
        host_writer.close(); await host_writer.wait_closed()

        other_reader, other_writer = await self.channel("host", session="other-1234", token="other-token", invite="other-invite")
        self.assertIsNotNone(other_reader)
        other_writer.close(); await other_writer.wait_closed()

    async def test_join_invitation_cannot_be_used_as_host_authority(self):
        host_reader, host_writer = await self.channel("host")
        forged_reader, forged_writer = await self.channel("host", token="invite-1234")
        self.assertEqual(b"", await forged_reader.read())
        forged_writer.close(); host_writer.close()
        await forged_writer.wait_closed(); await host_writer.wait_closed()

    async def test_malformed_combat_frame_closes_only_that_channel(self):
        host_reader, host_writer = await self.channel("host", channel="combat")
        join_reader, join_writer = await self.channel("join", channel="combat")
        join_writer.write((513).to_bytes(4, "big"))
        await join_writer.drain()
        self.assertEqual(b"", await host_reader.read())
        # The other session can still be registered and is not a shared relay socket.
        other_reader, other_writer = await self.channel("host", session="fresh-1234", token="fresh-token")
        self.assertIsNotNone(other_reader)
        for writer in (host_writer, join_writer, other_writer):
            writer.close()
            await writer.wait_closed()

    async def test_combat_backpressure_queue_is_bounded(self):
        with patch.object(relay_module, "MAX_PENDING_FRAMES", 2):
            host_reader, host_writer = await self.channel("host", channel="combat")
            for _ in range(20):
                if "session-1234" in self.relay.sessions:
                    break
                await asyncio.sleep(0)
            self.assertIn("session-1234", self.relay.sessions)
            server_channel = next(iter(self.relay.sessions["session-1234"].roles["host"]))
            self.assertEqual(2, server_channel.outbound.maxsize)
            server_channel.outbound.put_nowait(b"one")
            server_channel.outbound.put_nowait(b"two")
            self.assertTrue(server_channel.outbound.full())
            with self.assertRaises(asyncio.QueueFull):
                server_channel.outbound.put_nowait(b"three")
            join_reader, join_writer = await self.channel("join", channel="combat")
        host_writer.close(); join_writer.close()
        await host_writer.wait_closed(); await join_writer.wait_closed()

    async def test_malformed_hello_is_rejected_and_does_not_disturb_live_sessions(self):
        host_reader, host_writer = await self.channel("host")
        join_reader, join_writer = await self.channel("join")
        garbage = [
            b"not json\n",
            b"\xff\xfe\x00\n",
            b'{"version":2,"session":"session-1234","role":"join","token":"invite-1234","channel":"http"}\n',
            b'{"version":1,"session":"session-1234","role":"join","token":"invite-1234","channel":"udp"}\n',
            b'{"version":1,"session":"../../etc","role":"join","token":"invite-1234","channel":"http"}\n',
            b'{"version":1,"session":"session-1234","role":"join","token":"invite-1234","channel":"http","invite":"invite-1234"}\n',
            b'[1,2,3]\n',
            b"x" * 5000 + b"\n",
        ]
        for line in garbage:
            reader, writer = await asyncio.open_connection("127.0.0.1", self.port)
            writer.write(line)
            await writer.drain()
            self.assertEqual(b"", await asyncio.wait_for(reader.read(), 2), line[:40])
            writer.close()
            await writer.wait_closed()
        # The already paired session is unaffected.
        host_writer.write(b"still-up")
        await host_writer.drain()
        self.assertEqual(b"still-up", await asyncio.wait_for(join_reader.readexactly(8), 2))
        for writer in (host_writer, join_writer):
            writer.close()
            await writer.wait_closed()

    async def test_silent_connection_is_dropped_at_handshake_deadline(self):
        with patch.object(relay_module, "HANDSHAKE_TIMEOUT_SECONDS", 0.1):
            reader, writer = await asyncio.open_connection("127.0.0.1", self.port)
            self.assertEqual(b"", await asyncio.wait_for(reader.read(), 2))
            writer.close()
            await writer.wait_closed()
        self.assertEqual({}, self.relay.sessions)

    async def test_third_party_cannot_join_an_occupied_pair(self):
        host_reader, host_writer = await self.channel("host", channel="combat")
        join_reader, join_writer = await self.channel("join", channel="combat")
        await asyncio.sleep(0.1)
        # A second join channel holding the same credential is admitted to the session, but it must
        # not be spliced into the established pair or receive its frames.
        extra_reader, extra_writer = await self.channel("join", channel="combat")
        join_writer.write(encode_frame(b"pair-only"))
        await join_writer.drain()
        self.assertEqual(b"pair-only", await asyncio.wait_for(self.read_frame(host_reader), 2))
        with self.assertRaises(asyncio.TimeoutError):
            await asyncio.wait_for(extra_reader.read(1), 0.2)
        for writer in (host_writer, join_writer, extra_writer):
            writer.close()
            await writer.wait_closed()

    async def test_combat_flood_to_a_stalled_reader_is_bounded_and_isolated(self):
        with patch.object(relay_module, "MAX_PENDING_FRAMES", 4):
            host_reader, host_writer = await self.channel("host", channel="combat")
            join_reader, join_writer = await self.channel("join", channel="combat")
            other_host, other_host_w = await self.channel("host", session="other-1234", token="other-token", invite="other-invite", channel="combat")
            other_join, other_join_w = await self.channel("join", session="other-1234", token="other-invite", channel="combat")
            await asyncio.sleep(0.1)
            # The host never reads. Keep flooding until the relay gives up on this pair.
            frame = encode_frame(b"f" * 512)
            closed = False
            for _ in range(200000):
                try:
                    join_writer.write(frame)
                    await asyncio.wait_for(join_writer.drain(), 2)
                except (ConnectionError, asyncio.TimeoutError):
                    closed = True
                    break
                if join_reader.at_eof():
                    closed = True
                    break
            if not closed:
                self.assertEqual(b"", await asyncio.wait_for(join_reader.read(), 5))
            # A stalled reader costs the relay a bounded queue, not the whole session table.
            self.assertTrue(closed or join_reader.at_eof())
            # Another session is unaffected by the flood.
            other_join_w.write(encode_frame(b"quiet"))
            await other_join_w.drain()
            self.assertEqual(b"quiet", await asyncio.wait_for(self.read_frame(other_host), 2))
            for writer in (host_writer, join_writer, other_host_w, other_join_w):
                writer.close()
                try:
                    await writer.wait_closed()
                except ConnectionError:
                    pass  # the flooded pair was reset by the relay, which is the behaviour under test

    async def read_frame(self, reader):
        header = await reader.readexactly(4)
        size = decode_frame_header(header)
        return await reader.readexactly(size)


@unittest.skipUnless(shutil.which("openssl"), "openssl is required to create a throwaway certificate")
class RelayTlsTest(unittest.IsolatedAsyncioTestCase):
    async def asyncSetUp(self):
        self.dir = tempfile.TemporaryDirectory()
        cert, key = os.path.join(self.dir.name, "c.pem"), os.path.join(self.dir.name, "k.pem")
        subprocess.run(
            ["openssl", "req", "-x509", "-newkey", "ec", "-pkeyopt", "ec_paramgen_curve:prime256v1", "-nodes",
             "-keyout", key, "-out", cert, "-days", "1", "-subj", "/CN=localhost",
             "-addext", "subjectAltName=DNS:localhost"],
            check=True, capture_output=True,
        )
        self.cert = cert
        self.relay = Relay()
        self.port = await self.relay.start("127.0.0.1", 0, tls_server_context(cert, key))

    async def asyncTearDown(self):
        await self.relay.close()
        self.dir.cleanup()

    def client_context(self, *, verify=True):
        context = ssl.create_default_context(cafile=self.cert) if verify else ssl.create_default_context()
        return context

    async def open(self, context, hostname="localhost"):
        return await asyncio.open_connection("127.0.0.1", self.port, ssl=context, server_hostname=hostname)

    async def test_tls_pairs_and_forwards_and_refuses_untrusted_or_old_clients(self):
        context = self.client_context()
        host_reader, host_writer = await self.open(context)
        host_writer.write(encode_hello(Hello("tls-session-1", "host", "host-token-1", "http", "join-token-1")))
        join_reader, join_writer = await self.open(context)
        join_writer.write(encode_hello(Hello("tls-session-1", "join", "join-token-1", "http")))
        join_writer.write(b"ping")
        await join_writer.drain()
        self.assertEqual(b"ping", await asyncio.wait_for(host_reader.readexactly(4), 2))
        host_writer.write(b"pong")
        await host_writer.drain()
        self.assertEqual(b"pong", await asyncio.wait_for(join_reader.readexactly(4), 2))
        for writer in (host_writer, join_writer):
            writer.close()

        with self.assertRaises(ssl.SSLCertVerificationError):
            await self.open(self.client_context(verify=False))
        with self.assertRaises(ssl.SSLCertVerificationError):
            await self.open(context, hostname="wrong.example")
        legacy = ssl.SSLContext(ssl.PROTOCOL_TLS_CLIENT)
        legacy.load_verify_locations(self.cert)
        legacy.maximum_version = ssl.TLSVersion.TLSv1_1 if hasattr(ssl.TLSVersion, "TLSv1_1") else ssl.TLSVersion.TLSv1_2
        try:
            with self.assertRaises((ssl.SSLError, ConnectionError)):
                await self.open(legacy)
        except ValueError:
            self.skipTest("this OpenSSL build cannot offer TLS 1.1")

    async def test_plaintext_client_never_reaches_session_state(self):
        reader, writer = await asyncio.open_connection("127.0.0.1", self.port)
        writer.write(encode_hello(Hello("tls-session-2", "host", "host-token-2", "http", "join-token-2")))
        await writer.drain()
        try:
            await asyncio.wait_for(reader.read(), 2)
        except (ConnectionError, asyncio.TimeoutError):
            pass
        writer.close()
        self.assertNotIn("tls-session-2", self.relay.sessions)


class ProtocolTest(unittest.TestCase):
    def test_frame_limits_and_fragmented_header_validation(self):
        self.assertEqual(512, decode_frame_header((512).to_bytes(4, "big")))
        with self.assertRaises(ProtocolError):
            decode_frame_header((513).to_bytes(4, "big"))
        with self.assertRaises(ProtocolError):
            decode_frame_header(b"\x00\x01")

    def test_hello_rejects_invalid_values(self):
        with self.assertRaises(ProtocolError):
            encode_hello(Hello("short", "host", "valid-token", "http"))
        with self.assertRaises(ProtocolError):
            encode_hello(Hello("session-1234", "host", "valid-token", "udp"))


if __name__ == "__main__":
    unittest.main()
