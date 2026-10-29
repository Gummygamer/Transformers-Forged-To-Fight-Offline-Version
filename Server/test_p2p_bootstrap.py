#!/usr/bin/env python3
"""Tests for the P2P bootstrap node (p2p_bootstrap/bootstrap.py): return-address cookie, introductions, private local
addresses, the per-IP share of the list (never refusing a join), rate limit and the monthly budget.

Usage: python3 Server/test_p2p_bootstrap.py
"""
import os
import json
import socket
import sys
import tempfile
import time
import unittest

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "p2p_bootstrap"))
import bootstrap  # noqa: E402


class FakeTransport:
    def __init__(self):
        self.sent = []

    def sendto(self, data, addr):
        self.sent.append((data.decode("ascii"), addr))


def node(budget=50.0, monotonic=time.monotonic):
    b = bootstrap.Bootstrap(budget, monotonic=monotonic)
    b.connection_made(FakeTransport())
    return b


class Clock:
    def __init__(self):
        self.t = 1000.0

    def __call__(self):
        return self.t


def raw_join(b, pid, addr, neighbours=0, locals_="-", cookie=None):
    b.transport.sent.clear()
    text = "TF1 J %s %d %s" % (pid, neighbours, locals_) + (" " + cookie if cookie else "")
    b.datagram_received(text.encode(), addr)
    return list(b.transport.sent)


def join(b, pid, addr, neighbours=0, locals_="-"):
    """A game's join: the first try gets the cookie, the second (with it) counts."""
    first = raw_join(b, pid, addr, neighbours, locals_ + ",192.168.100.100:1")   # a real join is long enough for C
    if not first:
        return first
    text, to = first[0]
    assert to == addr and text.startswith("TF1 C "), first
    return raw_join(b, pid, addr, neighbours, locals_, text.split()[2])


class BootstrapTest(unittest.TestCase):
    def test_first_game_learns_its_address(self):
        sent = join(node(), "a", ("1.2.3.4", 5000))
        self.assertEqual(sent, [("TF1 W 1.2.3.4:5000", ("1.2.3.4", 5000))])

    def test_newcomer_and_peer_are_introduced_to_each_other(self):
        b = node()
        join(b, "a", ("1.2.3.4", 5000), locals_="10.0.0.2:5000")
        sent = join(b, "b", ("5.6.7.8", 6000))
        self.assertIn(("TF1 I b/5.6.7.8:6000/-", ("1.2.3.4", 5000)), sent)
        self.assertIn(("TF1 W 5.6.7.8:6000 a/1.2.3.4:5000/10.0.0.2:5000", ("5.6.7.8", 6000)), sent)

    def test_well_connected_game_gets_no_introductions(self):
        b = node()
        join(b, "a", ("1.2.3.4", 5000))
        sent = join(b, "b", ("5.6.7.8", 6000), neighbours=bootstrap.WANT)
        self.assertEqual(sent, [("TF1 W 5.6.7.8:6000", ("5.6.7.8", 6000))])

    def test_join_without_cookie_is_not_listed(self):
        b = node()
        sent = raw_join(b, "a", ("1.2.3.4", 5000), locals_="10.0.0.2:5000,10.0.0.3:5000")
        self.assertEqual(len(sent), 1)
        self.assertTrue(sent[0][0].startswith("TF1 C "))
        self.assertEqual(b.peers, {})

    def test_cookie_reply_is_never_longer_than_the_join(self):
        b = node()
        self.assertEqual(raw_join(b, "a", ("1.2.3.4", 5000)), [])          # "TF1 J a 0 -" is shorter than C
        for locals_ in ("10.0.0.2:5000", "10.0.0.2:5000,10.0.0.3:5000"):
            data = ("TF1 J a 0 " + locals_).encode()
            for text, _ in raw_join(b, "a", ("1.2.3.4", 5000), locals_=locals_):
                self.assertLessEqual(len(text), len(data))

    def test_forged_or_foreign_cookie_is_refused(self):
        b = node()
        cookie = b.cookie(("1.2.3.4", 5000), b.slot())
        for addr in (("1.2.3.4", 5001), ("1.2.3.5", 5000)):                 # someone else's cookie
            sent = raw_join(b, "x", addr, locals_="10.0.0.2:5000,10.0.0.3:5000", cookie=cookie)
            self.assertTrue(all(t.startswith("TF1 C ") for t, _ in sent))
        raw_join(b, "y", ("1.2.3.4", 5000), cookie="0" * bootstrap.COOKIE_LEN)
        self.assertEqual(b.peers, {})
        self.assertEqual(len(raw_join(b, "a", ("1.2.3.4", 5000), cookie=cookie)), 1)
        self.assertIn("a", b.peers)

    def test_cookie_expires_after_two_slots(self):
        now = [1000000.0]
        b = bootstrap.Bootstrap(secret=b"k", clock=lambda: now[0])
        b.connection_made(FakeTransport())
        cookie = b.cookie(("1.2.3.4", 5000), b.slot())
        now[0] += bootstrap.COOKIE_SLOT
        self.assertTrue(b.valid_cookie(cookie, ("1.2.3.4", 5000)))
        now[0] += bootstrap.COOKIE_SLOT
        self.assertFalse(b.valid_cookie(cookie, ("1.2.3.4", 5000)))

    def test_garbage_is_ignored(self):
        b = node()
        for junk in (b"hello", b"TF1 J", b"TF1 J a x -", b"TF2 J a 0 -", b"TF1 J a 0 -" + b"x" * 600):
            b.datagram_received(junk, ("1.2.3.4", 5000))
        self.assertEqual(b.transport.sent, [])

    def test_rate_limit_per_ip(self):
        b = node()
        answered = sum(len(raw_join(b, "g%d" % i, ("9.9.9.9", 1000 + i), locals_="10.0.0.2:5000,10.0.0.3:5000"))
                       for i in range(int(bootstrap.RATE_PER_IP) * 3))
        self.assertLessEqual(answered, bootstrap.RATE_PER_IP + 2)
        self.assertEqual(len(raw_join(b, "other", ("9.9.9.10", 1000), locals_="10.0.0.2:5000,10.0.0.3:5000")), 1)

    def test_stops_answering_at_the_budget_until_next_month(self):
        b = node(budget=bootstrap.FIXED_USD + 1e-6)      # about 8 KB of traffic
        answered = 0
        for i in range(400):
            answered += len(join(b, "p%d" % i, ("10.%d.%d.1" % (i // 250, i % 250), 7000), neighbours=bootstrap.WANT))
        self.assertTrue(b.over_budget)
        self.assertLess(answered, 400)
        self.assertLessEqual(b.sent_bytes, b.byte_budget)
        b.month = "1999-01"                                # the month turns over
        self.assertEqual(len(join(b, "late", ("11.0.0.1", 7000), neighbours=bootstrap.WANT)), 1)

    def test_only_private_local_addresses_are_passed_on(self):
        f = bootstrap.private_locals
        self.assertEqual(f("192.168.1.5:7000,10.1.2.3:7000,172.20.0.1:7000"), "192.168.1.5:7000,10.1.2.3:7000,172.20.0.1:7000")
        self.assertEqual(f("100.64.3.4:7000,169.254.1.1:7000"), "100.64.3.4:7000,169.254.1.1:7000")   # carrier NAT, link-local
        self.assertEqual(f("8.8.8.8:53,1.2.3.4:5000"), "-")                   # public: would aim hellos at strangers
        self.assertEqual(f("172.32.0.1:7000,127.0.0.1:7000,192.168.1.5:0,10.0.0.1:99999,x:1,-"), "-")
        self.assertEqual(f("10.0.0.1:1,10.0.0.1:1,10.0.0.2:1,10.0.0.3:1,10.0.0.4:1,10.0.0.5:1"),
                         "10.0.0.1:1,10.0.0.2:1,10.0.0.3:1,10.0.0.4:1")       # no duplicates, at most MAX_LOCALS
        b = node()
        join(b, "a", ("1.2.3.4", 5000), locals_="8.8.8.8:53,192.168.1.5:5000")
        sent = join(b, "b", ("5.6.7.8", 6000), locals_="8.8.4.4:53")
        self.assertIn(("TF1 I b/5.6.7.8:6000/-", ("1.2.3.4", 5000)), sent)
        self.assertIn(("TF1 W 5.6.7.8:6000 a/1.2.3.4:5000/192.168.1.5:5000", ("5.6.7.8", 6000)), sent)

    def fill_ip(self, b, clock, ip, count, prefix):
        for i in range(count):
            clock.t += 0.05                                                   # within the per-IP rate
            join(b, "%s%d" % (prefix, i), (ip, 2000 + i))

    def test_full_ip_is_still_served_but_not_listed(self):
        clock = Clock()
        b = node(monotonic=clock)
        join(b, "elsewhere", ("5.5.5.5", 1))
        self.fill_ip(b, clock, "9.9.9.9", bootstrap.LISTED_PER_IP, "n")
        self.assertEqual(len(b.per_ip["9.9.9.9"]), bootstrap.LISTED_PER_IP)
        clock.t += 0.05
        sent = join(b, "late", ("9.9.9.9", 9000))
        reply = [t for t, a in sent if a == ("9.9.9.9", 9000)]
        self.assertEqual(len(reply), 1)
        self.assertTrue(reply[0].startswith("TF1 W 9.9.9.9:9000 "))          # its address and introductions
        self.assertEqual(len(reply[0].split()) - 3, bootstrap.INTRODUCE)
        intros = [a for t, a in sent if t == "TF1 I late/9.9.9.9:9000/-"]
        self.assertEqual(len(intros), bootstrap.INTRODUCE)                     # others still punch towards it
        self.assertNotIn("late", b.peers)
        for i in range(bootstrap.LISTED_PER_IP):                              # nobody was evicted for it
            self.assertIn("n%d" % i, b.peers)
        clock.t += 0.05
        self.assertEqual(len(join(b, "fresh", ("7.7.7.7", 1))), 1 + bootstrap.INTRODUCE)   # W + I: other IPs unaffected
        self.assertIn("fresh", b.peers)

    def test_listing_slot_frees_after_silence(self):
        clock = Clock()
        b = node(monotonic=clock)
        self.fill_ip(b, clock, "9.9.9.9", bootstrap.LISTED_PER_IP, "n")
        clock.t += bootstrap.EXPIRE / 2
        for i in range(1, bootstrap.LISTED_PER_IP):                          # all but n0 keep sending heartbeats
            clock.t += 0.05
            join(b, "n%d" % i, ("9.9.9.9", 2000 + i), neighbours=bootstrap.WANT)
        clock.t += bootstrap.EXPIRE / 2 + 1
        join(b, "late", ("9.9.9.9", 9000), neighbours=bootstrap.WANT)
        self.assertIn("late", b.peers)
        self.assertNotIn("n0", b.peers)
        self.assertEqual(len(b.per_ip["9.9.9.9"]), bootstrap.LISTED_PER_IP)

    def test_id_cannot_be_taken_over_while_alive(self):
        clock = Clock()
        b = node(monotonic=clock)
        join(b, "victim", ("1.2.3.4", 5000))
        clock.t += 1
        sent = join(b, "victim", ("6.6.6.6", 6666))
        self.assertTrue(any(t.startswith("TF1 W 6.6.6.6:6666") for t, _ in sent))   # served all the same
        self.assertEqual(b.peers["victim"][0], ("1.2.3.4", 5000))
        self.assertNotIn("6.6.6.6", b.per_ip)
        clock.t += bootstrap.MOVE_AFTER + 1                                   # the old address went silent: the game moved
        join(b, "victim", ("6.6.6.6", 6666))
        self.assertEqual(b.peers["victim"][0], ("6.6.6.6", 6666))
        self.assertNotIn("1.2.3.4", b.per_ip)
        self.assertEqual(b.per_ip["6.6.6.6"], {"victim"})

    def test_full_list_still_serves(self):
        b = node()
        join(b, "a", ("1.2.3.4", 5000))
        old = bootstrap.MAX_PEERS
        bootstrap.MAX_PEERS = 1
        try:
            sent = join(b, "b", ("5.6.7.8", 6000))
        finally:
            bootstrap.MAX_PEERS = old
        self.assertIn(("TF1 W 5.6.7.8:6000 a/1.2.3.4:5000/-", ("5.6.7.8", 6000)), sent)
        self.assertNotIn("b", b.peers)

    def test_month_count_survives_a_restart(self):
        with tempfile.TemporaryDirectory() as d:
            path = os.path.join(d, "budget.json")
            wall = [1_800_000_000.0]                          # 2027-01-15
            b = bootstrap.Bootstrap(clock=lambda: wall[0], state_path=path)
            b.connection_made(FakeTransport())
            join(b, "a", ("1.2.3.4", 5000))
            sent = b.sent_bytes
            self.assertGreater(sent, 0)
            b.save()
            self.assertEqual(json.load(open(path)), {"month": "2027-01", "sent_bytes": sent, "used_up": False})
            b2 = bootstrap.Bootstrap(clock=lambda: wall[0], state_path=path)       # a restart: the count goes on
            self.assertEqual((b2.month, b2.sent_bytes), ("2027-01", sent))
            wall[0] += 31 * 86400                             # next month: a fresh count
            b3 = bootstrap.Bootstrap(clock=lambda: wall[0], state_path=path)
            self.assertIsNone(b3.month)
            b3.connection_made(FakeTransport())
            join(b3, "a", ("1.2.3.4", 5000))
            self.assertEqual(b3.month, "2027-02")
            self.assertLess(b3.sent_bytes, sent * 2)

    def test_used_up_budget_stays_used_up_after_a_restart(self):
        with tempfile.TemporaryDirectory() as d:
            path = os.path.join(d, "budget.json")
            b = bootstrap.Bootstrap(budget_usd=bootstrap.FIXED_USD + 1e-6, state_path=path)
            b.connection_made(FakeTransport())
            for i in range(400):
                join(b, "p%d" % i, ("10.%d.%d.1" % (i // 250, i % 250), 7000), neighbours=bootstrap.WANT)
            self.assertTrue(b.over_budget)
            b2 = bootstrap.Bootstrap(budget_usd=bootstrap.FIXED_USD + 1e-6, state_path=path)
            b2.connection_made(FakeTransport())
            self.assertTrue(b2.over_budget)
            self.assertEqual(raw_join(b2, "late", ("11.0.0.1", 7000), locals_="10.0.0.2:5000,10.0.0.3:5000"), [])

    def test_unreadable_count_keeps_the_node_silent(self):
        with tempfile.TemporaryDirectory() as d:
            path = os.path.join(d, "budget.json")
            open(path, "w").write("{not json")
            b = bootstrap.Bootstrap(state_path=path)
            b.connection_made(FakeTransport())
            self.assertTrue(b.over_budget)
            self.assertEqual(raw_join(b, "a", ("1.2.3.4", 5000), locals_="10.0.0.2:5000,10.0.0.3:5000"), [])

    def test_never_sends_faster_than_the_budget_allows(self):
        clock = Clock()
        budget = bootstrap.FIXED_USD + 0.5                    # a small budget, so 200 s of joins reach the ceiling
        b = node(budget=budget, monotonic=clock)
        b.byte_budget = float("inf")                          # only the rate ceiling counts here
        sent = 0
        for i in range(200000):                               # 200 s of joins from many addresses
            clock.t += 0.001
            for text, _ in raw_join(b, "g", ("10.%d.%d.%d" % (i >> 16 & 255, i >> 8 & 255, i & 255), 1),
                                    locals_="10.0.0.2:5000,10.0.0.3:5000"):
                sent += len(text) + bootstrap.UDP_OVERHEAD
        self.assertLessEqual(sent, b.burst + b.rate * 200 + 100)
        self.assertGreater(sent, b.rate * 200 * 0.9)          # and it does send up to the ceiling
        month_cost = (b.rate * bootstrap.MONTH_SECONDS / 1e9) * bootstrap.EGRESS_USD_PER_GB + bootstrap.FIXED_USD
        self.assertLessEqual(month_cost, budget + 1e-6)
        full = node()                                         # at $50 the ceiling is about 140 KB/s
        self.assertAlmostEqual(full.rate * bootstrap.MONTH_SECONDS / 1e9 * bootstrap.EGRESS_USD_PER_GB + bootstrap.FIXED_USD,
                               bootstrap.MONTHLY_BUDGET_USD, places=6)

    def test_real_socket(self):
        import asyncio
        import threading
        loop = asyncio.new_event_loop()
        ready = loop.create_future()
        t = threading.Thread(target=lambda: loop.run_until_complete(bootstrap.serve("127.0.0.1", 0, ready)), daemon=True)
        t.start()
        while not ready.done():
            time.sleep(0.01)
        port = ready.result()
        s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        s.settimeout(2)
        s.sendto(b"TF1 J x 0 10.0.0.2:5000,10.0.0.3:5000", ("127.0.0.1", port))
        data, _ = s.recvfrom(512)
        self.assertTrue(data.startswith(b"TF1 C "))
        s.sendto(b"TF1 J x 0 - " + data.split()[2], ("127.0.0.1", port))
        data, _ = s.recvfrom(512)
        self.assertEqual(data.decode(), "TF1 W 127.0.0.1:%d" % s.getsockname()[1])
        s.close()


if __name__ == "__main__":
    unittest.main()
