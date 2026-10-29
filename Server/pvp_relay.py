#!/usr/bin/env python3
"""Matchmaking relay of launcher 0.8-0.11 (see mono310/launcher/ONLINE_PVP.md). The game now uses the P2P network; this
relay is kept for the lockstep test (mono310/launcher/tools/net_lockstep_test/run.sh).

Two game clients that send HELLO with the same room code are paired; from then on every line one of them sends is
forwarded unchanged to the other. The relay never looks at fight traffic, so it needs no game data.

Protocol (newline-terminated ASCII lines):
  client -> relay   HELLO <protocol> <room> <name>        room "-" is the public queue
  relay  -> client  WAIT                                  queued, no opponent yet
  relay  -> client  MATCH <role> <seed> <peer name>       role 0 = host (authoritative), 1 = guest; same seed for both
  relay  -> client  ERR <reason>                          then the connection closes
  relay  -> client  BYE                                   the opponent disconnected; then the connection closes

Usage: python3 Server/pvp_relay.py [--host 0.0.0.0] [--port 7777]
"""
import argparse
import asyncio
import logging
import random

PROTOCOL = "1"
MAX_LINE = 4096
HELLO_TIMEOUT = 15.0

log = logging.getLogger("pvp_relay")


class Relay:
    def __init__(self):
        self.waiting = {}          # room -> (reader, writer, name, paired future)

    async def handle(self, reader, writer):
        peer = writer.get_extra_info("peername")
        try:
            line = await asyncio.wait_for(reader.readline(), HELLO_TIMEOUT)
        except (asyncio.TimeoutError, ConnectionError):
            writer.close()
            return
        parts = line.decode("ascii", "replace").strip().split(" ")
        if len(parts) < 4 or parts[0] != "HELLO":
            await self.refuse(writer, "hello")
            return
        if parts[1] != PROTOCOL:
            await self.refuse(writer, "version")
            return
        room = parts[2][:32] or "-"
        name = "_".join(parts[3:])[:24] or "Player"
        log.info("hello %s room=%s name=%s", peer, room, name)

        other = self.waiting.pop(room, None)
        if other is not None and other[4].done() and not other[4].cancelled():   # that client hung up while it waited
            other = None
        if other is None:
            paired = asyncio.get_running_loop().create_future()
            # clients send nothing between HELLO and MATCH, so a read that ends means the waiting client is gone
            hangup = asyncio.ensure_future(reader.read(1))
            self.waiting[room] = (reader, writer, name, paired, hangup)
            await self.send(writer, "WAIT")
            await asyncio.wait([paired, hangup], return_when=asyncio.FIRST_COMPLETED)
            if not hangup.cancelled():  # the pairing side cancels the read before it forwards this connection
                if self.waiting.get(room, (None, None))[1] is writer:
                    del self.waiting[room]
                log.info("left queue room=%s name=%s", room, name)
                writer.close()
                return
            await paired             # the pairing side forwards this connection until it ends
            return

        o_reader, o_writer, o_name, o_paired, o_hangup = other
        o_hangup.cancel()
        seed = random.randrange(1, 2 ** 31 - 1)
        await self.send(o_writer, "MATCH 0 %d %s" % (seed, name))
        await self.send(writer, "MATCH 1 %d %s" % (seed, o_name))
        log.info("match room=%s host=%s guest=%s", room, o_name, name)
        try:
            await asyncio.gather(self.pipe(o_reader, writer), self.pipe(reader, o_writer), return_exceptions=True)
        finally:
            o_paired.set_result(True)
            log.info("closed room=%s", room)

    async def pipe(self, src, dst):
        """Forward lines until src closes, then tell dst and close it."""
        try:
            while True:
                line = await src.readline()
                if not line or len(line) > MAX_LINE:
                    break
                dst.write(line)
                await dst.drain()
        except (ConnectionError, asyncio.IncompleteReadError):
            pass
        finally:
            try:
                dst.write(b"BYE\n")
                await dst.drain()
            except (ConnectionError, RuntimeError):
                pass
            dst.close()

    async def send(self, writer, text):
        writer.write((text + "\n").encode("ascii", "replace"))
        await writer.drain()

    async def refuse(self, writer, reason):
        try:
            await self.send(writer, "ERR " + reason)
        except ConnectionError:
            pass
        writer.close()


async def serve(host, port, ready=None):
    relay = Relay()
    server = await asyncio.start_server(relay.handle, host, port)
    if ready is not None:
        ready.set_result(server.sockets[0].getsockname()[1])
    async with server:
        await server.serve_forever()


def main():
    ap = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    ap.add_argument("--host", default="0.0.0.0")
    ap.add_argument("--port", type=int, default=7777)
    args = ap.parse_args()
    logging.basicConfig(level=logging.INFO, format="%(asctime)s %(message)s")
    log.info("listening on %s:%d", args.host, args.port)
    try:
        asyncio.run(serve(args.host, args.port))
    except KeyboardInterrupt:
        pass


if __name__ == "__main__":
    main()
