"""Small authenticated TLS reverse relay used by the PvP tunnel.

The relay is intentionally an address-blind rendezvous service.  A session is
created by its host credential and can contain exactly one host and one join
participant.  It only connects matching channels within that session; clients
cannot request arbitrary TCP or UDP destinations.
"""

from __future__ import annotations

import argparse
import asyncio
from dataclasses import dataclass, field
import hmac
import logging
import ssl
import time
from typing import Optional

from .protocol import (
    MAX_COMBAT_PACKET,
    MAX_FRAME,
    MAX_HELLO,
    Hello,
    ProtocolError,
    decode_frame_header,
    decode_hello,
    encode_frame,
)

LOG = logging.getLogger("tftf.internetrelay")
SESSION_TTL_SECONDS = 15 * 60
HANDSHAKE_TIMEOUT_SECONDS = 10
PEER_WAIT_TIMEOUT_SECONDS = 30
IO_TIMEOUT_SECONDS = 30
MAX_CHANNELS_PER_ROLE = 32
MAX_PENDING_FRAMES = 64


class RelayRejected(Exception):
    pass


@dataclass(eq=False)
class Channel:
    reader: asyncio.StreamReader
    writer: asyncio.StreamWriter
    hello: Hello
    session: "Session"
    outbound: asyncio.Queue[Optional[bytes]] = field(
        default_factory=lambda: asyncio.Queue(MAX_PENDING_FRAMES)
    )
    closed: bool = False
    peer: Optional["Channel"] = None
    proxy_owner: bool = False
    proxy_done: asyncio.Event = field(default_factory=asyncio.Event)

    async def close(self) -> None:
        if self.closed:
            return
        self.closed = True
        self.outbound.put_nowait(None) if not self.outbound.full() else None
        self.writer.close()
        transport = self.writer.transport
        if transport is not None:
            transport.abort()


@dataclass
class Session:
    session_id: str
    host_token: str
    join_token: str
    created: float
    roles: dict[str, set[Channel]] = field(default_factory=lambda: {"host": set(), "join": set()})

    def expired(self, now: float) -> bool:
        return now - self.created > SESSION_TTL_SECONDS

    def add(self, channel: Channel) -> None:
        peers = self.roles[channel.hello.role]
        if len(peers) >= MAX_CHANNELS_PER_ROLE:
            raise RelayRejected("too many channels")
        peers.add(channel)

    def remove(self, channel: Channel) -> None:
        self.roles[channel.hello.role].discard(channel)

    def paired(self, channel: Channel) -> Optional[Channel]:
        if channel.peer is not None and not channel.peer.closed:
            return channel.peer
        candidates = self.roles["join" if channel.hello.role == "host" else "host"]
        return next(
            (peer for peer in candidates if peer.peer is None and not peer.closed and peer.hello.channel == channel.hello.channel),
            None,
        )


class Relay:
    def __init__(self, *, clock=time.monotonic) -> None:
        self.sessions: dict[str, Session] = {}
        self.clock = clock
        self._server: Optional[asyncio.AbstractServer] = None
        self._cleanup_task: Optional[asyncio.Task[None]] = None
        self._connection_tasks: set[asyncio.Task[None]] = set()

    async def start(self, host: str, port: int, ssl_context: Optional[ssl.SSLContext] = None) -> int:
        self._server = await asyncio.start_server(self._track_connection, host, port, ssl=ssl_context)
        self._cleanup_task = asyncio.create_task(self._expire_sessions())
        sockets = self._server.sockets or []
        return int(sockets[0].getsockname()[1])

    async def close(self) -> None:
        if self._cleanup_task is not None:
            self._cleanup_task.cancel()
            try:
                await self._cleanup_task
            except asyncio.CancelledError:
                pass
            self._cleanup_task = None
        channels = [channel for session in self.sessions.values() for peers in session.roles.values() for channel in peers]
        await asyncio.gather(*(channel.close() for channel in channels), return_exceptions=True)
        current = asyncio.current_task()
        handlers = [task for task in self._connection_tasks if task is not current]
        for task in handlers:
            task.cancel()
        await asyncio.gather(*handlers, return_exceptions=True)
        self._connection_tasks.clear()
        if self._server is not None:
            self._server.close()
            await self._server.wait_closed()
            self._server = None
        self.sessions.clear()

    async def _track_connection(self, reader: asyncio.StreamReader, writer: asyncio.StreamWriter) -> None:
        task = asyncio.current_task()
        if task is not None:
            self._connection_tasks.add(task)
        try:
            await self._accept(reader, writer)
        finally:
            if task is not None:
                self._connection_tasks.discard(task)

    async def _expire_sessions(self) -> None:
        try:
            while True:
                await asyncio.sleep(5)
                now = self.clock()
                expired = [session for session in self.sessions.values() if session.expired(now)]
                for session in expired:
                    LOG.info("expiring session %s", session.session_id)
                    await self._remove_session(session)
        except asyncio.CancelledError:
            raise

    async def _remove_session(self, session: Session) -> None:
        self.sessions.pop(session.session_id, None)
        channels = [channel for peers in session.roles.values() for channel in peers]
        await asyncio.gather(*(channel.close() for channel in channels), return_exceptions=True)

    async def _accept(self, reader: asyncio.StreamReader, writer: asyncio.StreamWriter) -> None:
        channel: Optional[Channel] = None
        try:
            raw = await asyncio.wait_for(reader.readline(), HANDSHAKE_TIMEOUT_SECONDS)
            if len(raw) > MAX_HELLO:
                raise RelayRejected("hello too large")
            hello = decode_hello(raw)
            session = self._authorize(hello)
            channel = Channel(reader, writer, hello, session)
            session.add(channel)
            LOG.info("%s %s channel joined", hello.session, hello.channel)
            await self._run_channel(channel)
        except (RelayRejected, ProtocolError, asyncio.TimeoutError, UnicodeError) as exc:
            LOG.info("rejecting connection: %s", exc)
        except (ConnectionError, asyncio.IncompleteReadError, asyncio.CancelledError):
            pass
        finally:
            if channel is not None:
                peer = channel.session.paired(channel)
                if channel.proxy_owner:
                    channel.proxy_done.set()
                    channel.peer = None
                    if peer is not None:
                        peer.peer = None
                        peer.proxy_done.set()
                channel.session.remove(channel)
                if channel.proxy_owner and peer is not None:
                    await peer.close()
                if not any(channel_set for channel_set in channel.session.roles.values()):
                    self.sessions.pop(channel.session.session_id, None)
            writer.close()
            transport = writer.transport
            if transport is not None:
                transport.abort()
            if channel is None:
                # A rejected hello must not leave a half-open TLS/TCP stream
                # waiting for the client's read timeout.
                transport = writer.transport
                if transport is not None:
                    transport.abort()

    def _authorize(self, hello: Hello) -> Session:
        session = self.sessions.get(hello.session)
        if session is None:
            if hello.role != "host":
                raise RelayRejected("join requires a live host")
            session = Session(hello.session, hello.token, hello.invite, self.clock())
            self.sessions[hello.session] = session
        elif session.expired(self.clock()):
            self.sessions.pop(hello.session, None)
            raise RelayRejected("session expired")
        elif hello.role == "host":
            if not hmac.compare_digest(session.host_token, hello.token) or not hmac.compare_digest(session.join_token, hello.invite):
                raise RelayRejected("invalid host authority")
        elif not hmac.compare_digest(session.join_token, hello.token):
            raise RelayRejected("invalid invitation")
        return session

    async def _run_channel(self, channel: Channel) -> None:
        peer = await self._wait_for_peer(channel)
        if peer is None:
            raise RelayRejected("peer did not join")
        # Both accepted sockets observe the same pairing. Only the first one
        # owns the forwarding pumps; the other handler waits for its writer to
        # be closed by the owner. Two readers on one StreamReader would race.
        if not channel.proxy_owner:
            await channel.proxy_done.wait()
            return
        if channel.hello.channel == "http":
            await self._proxy_http(channel, peer)
        else:
            await self._proxy_combat(channel, peer)

    async def _wait_for_peer(self, channel: Channel) -> Optional[Channel]:
        deadline = min(
            self.clock() + PEER_WAIT_TIMEOUT_SECONDS,
            channel.session.created + SESSION_TTL_SECONDS,
        )
        while not channel.closed and self.clock() < deadline:
            if channel.reader.at_eof():
                return None
            if channel.peer is not None and not channel.peer.closed:
                return channel.peer
            peer = channel.session.paired(channel)
            if peer is not None:
                channel.peer = peer
                peer.peer = channel
                channel.proxy_owner = True
                peer.proxy_owner = False
                return peer
            await asyncio.sleep(0.05)
        return None

    async def _proxy_http(self, left: Channel, right: Channel) -> None:
        await self._proxy_raw(left, right)

    async def _proxy_raw(self, left: Channel, right: Channel) -> None:
        async def pump(source: Channel, target: Channel) -> None:
            while True:
                data = await asyncio.wait_for(source.reader.read(64 * 1024), IO_TIMEOUT_SECONDS)
                if not data:
                    return
                target.writer.write(data)
                await asyncio.wait_for(target.writer.drain(), IO_TIMEOUT_SECONDS)

        tasks = [asyncio.create_task(pump(left, right)), asyncio.create_task(pump(right, left))]
        done, pending = await asyncio.wait(tasks, return_when=asyncio.FIRST_COMPLETED)
        for task in pending:
            task.cancel()
        await asyncio.gather(*pending, return_exceptions=True)
        for task in done:
            exc = task.exception()
            if exc is not None and not isinstance(exc, (ConnectionError, asyncio.IncompleteReadError, asyncio.TimeoutError)):
                raise exc

    async def _proxy_combat(self, left: Channel, right: Channel) -> None:
        async def read_frames(source: Channel, target: Channel) -> None:
            while True:
                header = await asyncio.wait_for(source.reader.readexactly(4), IO_TIMEOUT_SECONDS)
                size = decode_frame_header(header)
                payload = await asyncio.wait_for(source.reader.readexactly(size), IO_TIMEOUT_SECONDS)
                try:
                    target.outbound.put_nowait(encode_frame(payload))
                except asyncio.QueueFull as exc:
                    raise RelayRejected("combat channel backpressure") from exc

        async def write_frames(channel: Channel) -> None:
            while True:
                frame = await channel.outbound.get()
                if frame is None:
                    return
                channel.writer.write(frame)
                await asyncio.wait_for(channel.writer.drain(), IO_TIMEOUT_SECONDS)

        tasks = [
            asyncio.create_task(read_frames(left, right)),
            asyncio.create_task(read_frames(right, left)),
            asyncio.create_task(write_frames(left)),
            asyncio.create_task(write_frames(right)),
        ]
        done, pending = await asyncio.wait(tasks, return_when=asyncio.FIRST_COMPLETED)
        for task in pending:
            task.cancel()
        await asyncio.gather(*pending, return_exceptions=True)
        for task in done:
            exc = task.exception()
            if exc is not None and not isinstance(exc, (ConnectionError, asyncio.IncompleteReadError, asyncio.TimeoutError)):
                raise exc


def tls_server_context(certfile: str, keyfile: str) -> ssl.SSLContext:
    context = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
    context.minimum_version = ssl.TLSVersion.TLSv1_2
    context.load_cert_chain(certfile, keyfile)
    return context


async def serve_forever(args: argparse.Namespace) -> None:
    relay = Relay()
    context = tls_server_context(args.cert, args.key)
    port = await relay.start(args.host, args.port, context)
    LOG.info("relay listening on %s:%d", args.host, port)
    try:
        await asyncio.Event().wait()
    finally:
        await relay.close()


def main() -> None:
    parser = argparse.ArgumentParser(description="TFTF authenticated reverse tunnel relay")
    parser.add_argument("--host", default="0.0.0.0")
    parser.add_argument("--port", type=int, default=4433)
    parser.add_argument("--cert", required=True, help="TLS certificate chain PEM")
    parser.add_argument("--key", required=True, help="TLS private key PEM")
    parser.add_argument("-v", "--verbose", action="store_true")
    args = parser.parse_args()
    logging.basicConfig(level=logging.DEBUG if args.verbose else logging.INFO, format="%(levelname)s %(message)s")
    asyncio.run(serve_forever(args))


if __name__ == "__main__":
    main()
