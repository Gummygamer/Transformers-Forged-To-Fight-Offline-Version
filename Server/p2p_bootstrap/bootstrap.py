#!/usr/bin/env python3
"""Bootstrap node of the online Arena P2P network (launcher 0.12+, see mono310/launcher/ONLINE_PVP.md).

The only job of this node is the first hop: a game that joins the network sends J from its UDP socket, learns the
address the internet sees it at, and is introduced to a few games already in the network. Those games are told about
the newcomer at the same moment, so both sides send to each other and open their NATs (UDP hole punching). From then
on the games gossip, introduce each other, match players and carry the fight traffic among themselves. A game that
rejoins first tries the games it remembers and comes here only when none answers; a game with neighbours only sends a
heartbeat every 25 s, so it can be introduced to later newcomers. No fight or matchmaking traffic passes through here.

Datagrams are ASCII, each starting with the protocol tag:
  game -> node   TF1 J <id> <neighbours> <locals> <cookie>     join / heartbeat; locals "a:p,b:p" or "-"; cookie
                                                               from C, or 16 zeros before the game has one
  node -> game   TF1 C <cookie>                                the join had no valid cookie: send it again with this one
  node -> game   TF1 W <your endpoint> <peer> ...              peer = id/endpoint/locals; introductions only while
                                                               the game has fewer than WANT neighbours
  node -> peer   TF1 I <peer>                                  "punch towards this newcomer"

Spending guard, all on this node: fly.io bills the machine, dedicated IPv4 and volume at a fixed rate and outbound
traffic per GB. Two independent limits keep fixed cost + traffic within MONTHLY_BUDGET_USD (default 50):
  - the node counts what it sends each calendar month (UTC) and stops answering once the month's budget is used, until
    the next month. The count is saved to STATE_PATH (a fly volume) every SAVE_EVERY seconds and on shutdown and read back
    on start, so restarts and deploys do not reset it;
  - it never sends faster than the month's traffic budget spread over 31 days (with one minute of burst), so even with
    the count lost on every restart a month cannot cost more than the budget. Each IP address may send at most RATE_PER_IP datagrams a second, so a flood of (possibly spoofed)
joins cannot turn this node into a traffic amplifier.

Fair share of the list: every verified join is answered in full (its address, introductions, and the games introduced to
it are told to punch towards it), so no player is ever kept out of the network. Only the list of games this node hands
to newcomers is limited: at most LISTED_PER_IP games per IP address, without evicting any (an id from a full address is
simply not listed until a slot frees after EXPIRE seconds of silence), so one address, even one shared with many real
players behind a carrier NAT, cannot push other players out of the list. A listed id keeps its address until that
address has been silent for MOVE_AFTER seconds, so another sender cannot take over or delist a game by reusing its id.
A game not in the list is still found through the network's own gossip and the games that remember it.

Return-address check: a join only counts once it carries a cookie that proves the sender receives datagrams at the
address it sends from. The cookie is a keyed hash of that address and a COOKIE_SLOT-second time slot (valid for the
current and the previous slot), so the node keeps no state for unverified joins. A join without a valid cookie gets only
C, which is never longer than the join, so joins with a forged sender address can neither fill the list of games with
fake entries nor make this node send more traffic than it receives.

Usage: python3 bootstrap.py [--host fly-global-services|0.0.0.0] [--port 7777]
On fly.io UDP must be received on the address fly-global-services; it is used by default when it resolves.
"""
import argparse
import asyncio
import hashlib
import hmac
import ipaddress
import json
import logging
import os
import random
import signal
import socket
import time

TAG = "TF1"
WANT = 3                 # games with fewer neighbours than this are introduced to others
INTRODUCE = 4            # peers handed to one newcomer
EXPIRE = 75.0            # seconds without a heartbeat before a game is forgotten (games send one every 25 s)
MAX_PEERS = 20000
MAX_DATAGRAM = 512
RATE_PER_IP = 100.0      # datagrams a second, with a burst of the same size. Generous: behind one carrier NAT thousands
                         # of players share an IP (1000 heartbeats every 25 s = 40/s); a flood is bounded by the budget
LISTED_PER_IP = 64       # games one IP address may have in the list newcomers are introduced to (never refuses a join)
MOVE_AFTER = 30.0        # seconds a listed game's address must be silent before its id may be listed at another address
MAX_LOCALS = 4
UDP_OVERHEAD = 28        # IPv4 + UDP header bytes billed with every datagram
COOKIE_SLOT = 120        # seconds; a cookie is valid for 2-4 minutes, games send a heartbeat at least every 25 s
COOKIE_LEN = 16          # hex digits
SAVE_EVERY = 10.0        # seconds between saves of the month's count
MONTH_SECONDS = 31 * 86400
BURST_SECONDS = 60.0

# A game's local addresses are only a shortcut for players on the same network, so only private addresses are passed on:
# a public address there would make the games introduced to it send their hellos to whatever address that is.
PRIVATE_NETS = [ipaddress.ip_network(n) for n in
                ("10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16", "100.64.0.0/10", "169.254.0.0/16")]

MONTHLY_BUDGET_USD = float(os.environ.get("MONTHLY_BUDGET_USD", "50"))
FIXED_USD = float(os.environ.get("FIXED_USD", "5"))                    # machine + dedicated IPv4 + volume, rounded up
STATE_PATH = os.environ.get("STATE_PATH", "")                          # the month's count; empty: kept in memory only
EGRESS_USD_PER_GB = float(os.environ.get("EGRESS_USD_PER_GB", "0.12"))  # fly's highest regional rate, to be safe

log = logging.getLogger("p2p_bootstrap")


def clean(s, limit=200):
    return "".join(c for c in s if " " < c < "\x7f")[:limit]


class Bootstrap(asyncio.DatagramProtocol):
    def __init__(self, budget_usd=MONTHLY_BUDGET_USD, secret=None, clock=time.time, monotonic=time.monotonic,
                 state_path=STATE_PATH):
        self.secret = secret or os.urandom(32)      # new on every start: games just get a new cookie
        self.clock = clock
        self.monotonic = monotonic
        self.peers = {}            # id -> [endpoint tuple, locals, last seen]
        self.per_ip = {}           # ip -> ids of listed games at that ip
        self.transport = None
        self.buckets = {}          # ip -> [tokens, last refill]
        self.month = None
        self.sent_bytes = 0
        self.byte_budget = max(0.0, budget_usd - FIXED_USD) / EGRESS_USD_PER_GB * 1e9
        self.over_budget = False
        self.rate = self.byte_budget / MONTH_SECONDS                   # bytes a second
        self.burst = max(self.rate * BURST_SECONDS, 65536.0)
        self.tokens, self.refilled = self.burst, self.monotonic()
        self.state_path = state_path
        self.saved_bytes = None
        self.load()

    def connection_made(self, transport):
        self.transport = transport

    def datagram_received(self, data, addr):
        if len(data) > MAX_DATAGRAM or not self.allow(addr[0]) or self.over_budget and not self.new_month():
            return
        p = data.decode("ascii", "replace").split()
        if len(p) < 5 or p[0] != TAG or p[1] != "J":
            return
        if len(p) < 6 or not self.valid_cookie(p[5], addr):
            reply = "%s C %s" % (TAG, self.cookie(addr, self.slot()))
            if len(reply) <= len(data):        # never more bytes out than in for an unverified sender
                self.send(reply, addr)
            return
        pid, locals_ = clean(p[2], 24), private_locals(p[4])
        try:
            neighbours = int(p[3])
        except ValueError:
            return
        if not pid:
            return
        now = self.monotonic()
        self.expire(now)
        listed = self.list(pid, addr, locals_, now)
        me = "%s/%s/%s" % (pid, ep(addr), locals_)
        chosen = []
        if neighbours < WANT:
            others = [k for k in self.peers if k != pid]
            chosen = random.sample(others, min(INTRODUCE, len(others)))
        reply = [TAG, "W", ep(addr)]
        for k in chosen:
            a, l, _ = self.peers[k]
            reply.append("%s/%s/%s" % (k, ep(a), l))
            self.send("%s I %s" % (TAG, me), a)
        self.send(" ".join(reply), addr)
        if listed == "new":
            log.info("join %s from %s, %d games known", pid, ep(addr), len(self.peers))

    def list(self, pid, addr, locals_, now):
        """Adds or refreshes a verified game in the list: "new", "refreshed", or None when it is served but not listed."""
        entry = self.peers.get(pid)
        if entry is not None:
            if entry[0] == addr:
                entry[1], entry[2] = locals_, now
                return "refreshed"
            if now - entry[2] < MOVE_AFTER:        # the id is alive at another address: keep that one
                return None
            self.unlist(pid)
        ids = self.per_ip.get(addr[0])
        if len(self.peers) >= MAX_PEERS or ids is not None and len(ids) >= LISTED_PER_IP:
            return None
        self.peers[pid] = [addr, locals_, now]
        self.per_ip.setdefault(addr[0], set()).add(pid)
        return "new"

    def unlist(self, pid):
        addr = self.peers.pop(pid)[0]
        ids = self.per_ip.get(addr[0])
        if ids is not None:
            ids.discard(pid)
            if not ids:
                del self.per_ip[addr[0]]

    def slot(self):
        return int(self.clock() // COOKIE_SLOT)

    def cookie(self, addr, slot):
        msg = ("%d|%s|%d" % (slot, addr[0], addr[1])).encode()
        return hmac.new(self.secret, msg, hashlib.sha256).hexdigest()[:COOKIE_LEN]

    def valid_cookie(self, cookie, addr):
        slot = self.slot()
        return any(hmac.compare_digest(cookie, self.cookie(addr, s)) for s in (slot, slot - 1))

    def expire(self, now):
        for k in [k for k, v in self.peers.items() if now - v[2] > EXPIRE]:
            self.unlist(k)

    def allow(self, ip):
        now = self.monotonic()
        b = self.buckets.get(ip)
        if b is None:
            if len(self.buckets) > 4 * MAX_PEERS:
                self.buckets = {k: v for k, v in self.buckets.items() if now - v[1] < 10}
            b = self.buckets[ip] = [RATE_PER_IP, now]
        b[0] = min(RATE_PER_IP, b[0] + (now - b[1]) * RATE_PER_IP)
        b[1] = now
        if b[0] < 1:
            return False
        b[0] -= 1
        return True

    def new_month(self):
        month = time.strftime("%Y-%m", time.gmtime(self.clock()))
        if month == self.month:
            return False
        if self.month is not None:
            log.info("new month %s: %.3f GB sent last month", month, self.sent_bytes / 1e9)
        self.month, self.sent_bytes, self.over_budget = month, 0, False
        self.save()
        return True

    def load(self):
        """Reads the month's count saved by an earlier run (same month only)."""
        if not self.state_path:
            return
        try:
            with open(self.state_path) as f:
                state = json.load(f)
            month, sent, used_up = str(state["month"]), int(state["sent_bytes"]), bool(state.get("used_up", False))
        except FileNotFoundError:
            return
        except (OSError, ValueError, KeyError, TypeError) as e:
            # an unreadable count must not reopen the budget: assume it is used up until the month changes
            log.error("cannot read %s (%s); not answering until next month", self.state_path, e)
            self.month = time.strftime("%Y-%m", time.gmtime(self.clock()))
            self.sent_bytes, self.over_budget = int(self.byte_budget) + 1, True
            return
        if month == time.strftime("%Y-%m", time.gmtime(self.clock())):
            self.month, self.sent_bytes = month, max(0, sent)
            self.over_budget = used_up or self.sent_bytes >= self.byte_budget
            log.info("month %s so far: %.3f GB sent", month, self.sent_bytes / 1e9)

    def save(self):
        """Writes the month's count (atomically, flushed to disk) when it changed."""
        state = (self.month, self.sent_bytes, self.over_budget)
        if not self.state_path or self.month is None or self.saved_bytes == state:
            return
        tmp = self.state_path + ".tmp"
        try:
            with open(tmp, "w") as f:
                json.dump({"month": self.month, "sent_bytes": self.sent_bytes, "used_up": self.over_budget}, f)
                f.flush()
                os.fsync(f.fileno())
            os.replace(tmp, self.state_path)
            self.saved_bytes = state
        except OSError as e:
            log.error("cannot save %s: %s", self.state_path, e)

    def send(self, text, addr):
        self.new_month()
        data = text.encode("ascii", "replace")
        if self.sent_bytes + len(data) + UDP_OVERHEAD > self.byte_budget:
            if not self.over_budget:
                log.warning("monthly budget of $%.0f reached (%.3f GB sent); not answering until next month",
                            MONTHLY_BUDGET_USD, self.sent_bytes / 1e9)
            self.over_budget = True
            self.save()
            return
        size = len(data) + UDP_OVERHEAD
        now = self.monotonic()
        self.tokens = min(self.burst, self.tokens + (now - self.refilled) * self.rate)
        self.refilled = now
        if self.tokens < size:                  # faster than the budget allows over a month: drop
            return
        self.tokens -= size
        self.sent_bytes += size
        self.transport.sendto(data, addr)


def private_locals(text):
    """The private ip:port entries of a game's local addresses, "-" when none."""
    out = []
    for e in text.split(","):
        host, _, port = e.rpartition(":")
        try:
            ip, port = ipaddress.IPv4Address(host), int(port)
        except ValueError:
            continue
        norm = "%s:%d" % (ip, port)
        if 0 < port < 65536 and any(ip in n for n in PRIVATE_NETS) and norm not in out:
            out.append(norm)
        if len(out) >= MAX_LOCALS:
            break
    return ",".join(out) or "-"


def ep(addr):
    return "%s:%d" % (addr[0], addr[1])


def default_host():
    try:
        socket.getaddrinfo("fly-global-services", None, socket.AF_INET)
        return "fly-global-services"
    except OSError:
        return "0.0.0.0"


async def serve(host, port, ready=None):
    loop = asyncio.get_running_loop()
    transport, node = await loop.create_datagram_endpoint(Bootstrap, local_addr=(host, port), family=socket.AF_INET)
    if ready is not None:
        ready.set_result(transport.get_extra_info("sockname")[1])
    stop = asyncio.Event()
    for sig in (signal.SIGINT, signal.SIGTERM):       # fly stops machines with SIGINT; save the count first
        try:
            loop.add_signal_handler(sig, stop.set)
        except (NotImplementedError, RuntimeError, ValueError):
            pass
    try:
        while not stop.is_set():
            try:
                await asyncio.wait_for(stop.wait(), SAVE_EVERY)
            except asyncio.TimeoutError:
                pass
            node.new_month()
            node.save()
    finally:
        node.save()
        transport.close()


def main():
    ap = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    ap.add_argument("--host", default=None)
    ap.add_argument("--port", type=int, default=int(os.environ.get("PORT", "7777")))
    args = ap.parse_args()
    host = args.host or default_host()
    logging.basicConfig(level=logging.INFO, format="%(asctime)s %(message)s")
    log.info("bootstrap on udp %s:%d", host, args.port)
    try:
        asyncio.run(serve(host, args.port))
    except KeyboardInterrupt:
        pass


if __name__ == "__main__":
    main()
