#!/usr/bin/env python3
"""Tests for pvp_relay.py: pairing by room, forwarding, BYE on hang-up, version check. Run: python3 Server/test_pvp_relay.py
With PVP_RELAY_PORT set, the same tests run against a relay already listening on that port of 127.0.0.1."""
import asyncio
import os
import sys
import unittest

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import pvp_relay


class RelayTest(unittest.IsolatedAsyncioTestCase):
    async def asyncSetUp(self):
        self.server = None
        if os.environ.get("PVP_RELAY_PORT"):
            self.port = int(os.environ["PVP_RELAY_PORT"])
            return
        ready = asyncio.get_running_loop().create_future()
        self.server = asyncio.ensure_future(pvp_relay.serve("127.0.0.1", 0, ready))
        self.port = await ready

    async def asyncTearDown(self):
        if self.server is not None:
            self.server.cancel()

    async def client(self, room, name, proto="1"):
        r, w = await asyncio.open_connection("127.0.0.1", self.port)
        w.write(("HELLO %s %s %s\n" % (proto, room, name)).encode())
        await w.drain()
        return r, w

    async def line(self, r):
        return (await asyncio.wait_for(r.readline(), 2)).decode().strip()

    async def test_pair_and_forward(self):
        r1, w1 = await self.client("abc" + self.id()[-6:], "Alice")
        self.assertEqual(await self.line(r1), "WAIT")
        r2, w2 = await self.client("abc" + self.id()[-6:], "Bob")
        m1, m2 = (await self.line(r1)).split(" "), (await self.line(r2)).split(" ")
        self.assertEqual((m1[0], m1[1], m1[3]), ("MATCH", "0", "Bob"))
        self.assertEqual((m2[0], m2[1], m2[3]), ("MATCH", "1", "Alice"))
        self.assertEqual(m1[2], m2[2])
        w1.write(b"T 1 0 a\n"); await w1.drain()
        self.assertEqual(await self.line(r2), "T 1 0 a")
        w2.write(b"T 1 0 -\n"); await w2.drain()
        self.assertEqual(await self.line(r1), "T 1 0 -")
        w2.close()
        self.assertEqual(await self.line(r1), "BYE")

    async def test_rooms_are_separate(self):
        r1, _ = await self.client("one", "A")
        r2, _ = await self.client("two", "B")
        self.assertEqual(await self.line(r1), "WAIT")
        self.assertEqual(await self.line(r2), "WAIT")

    async def test_left_queue_is_not_paired(self):
        r1, w1 = await self.client("q", "Gone")
        self.assertEqual(await self.line(r1), "WAIT")
        w1.close()
        await asyncio.sleep(0.1)
        r2, _ = await self.client("q", "B")
        self.assertEqual(await self.line(r2), "WAIT")

    async def test_version_mismatch(self):
        r, _ = await self.client("-", "A", proto="0")
        self.assertEqual(await self.line(r), "ERR version")


if __name__ == "__main__":
    unittest.main()
