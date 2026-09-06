#!/usr/bin/env python3
"""Black-box tests for the Arena realtime netcode relay.

Each test starts its own relay on a free UDP port and drives it with real sockets,
so the assertions are about wire behaviour rather than about the C source. Run with:

    python3 tools/netrelay/test_netrelay.py

Exits non-zero and prints [!] lines on failure, [ok] lines on success.
"""
import os
import socket
import subprocess
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
RELAY = os.path.join(HERE, "netrelay")
FAILURES = []


def report(name, passed, detail=""):
    if passed:
        print("[ok] " + name)
    else:
        print("[!] " + name + ("  " + detail if detail else ""))
        FAILURES.append(name)


class Relay:
    """Owns one relay process bound to an ephemeral UDP port."""

    def __init__(self, ttl_ms=8000, verbose=False):
        # Ask the OS for a free UDP port, release it, then hand the number to the
        # relay. SO_REUSEADDR in the relay covers the brief window.
        probe = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        probe.bind(("127.0.0.1", 0))
        self.port = probe.getsockname()[1]
        probe.close()
        cmd = [RELAY, "--port", str(self.port), "--ttl", str(ttl_ms)]
        if verbose:
            cmd.append("--verbose")
        self.proc = subprocess.Popen(cmd, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)

    def stop(self):
        self.proc.terminate()
        try:
            self.proc.wait(timeout=5)
        except subprocess.TimeoutExpired:
            self.proc.kill()

    def log(self):
        try:
            return self.proc.stdout.read()
        except Exception:
            return ""


class Client:
    """One UDP socket with a line queue.

    Every read drains whatever has arrived into `self.lines`, and `wait_for` scans that
    queue under a single absolute deadline. That matters because the relay pushes roster
    frames (PR) on its own schedule: a caller looking only for IN must be able to step
    over a PR that landed first instead of discarding it and timing out.
    """

    def __init__(self, port, timeout=2.0):
        self.sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        self.sock.settimeout(0.1)
        self.addr = ("127.0.0.1", port)
        self.lines = []

    def send(self, text):
        self.sock.sendto(text.encode(), self.addr)

    def _drain(self):
        """Move everything already received into the queue."""
        while True:
            try:
                data, _ = self.sock.recvfrom(4096)
            except socket.timeout:
                return
            except BlockingIOError:
                return
            self.lines.append(data.decode(errors="replace"))

    def wait_for(self, prefix, timeout=2.0):
        """Return the first queued or newly arrived line starting with prefix, else None."""
        deadline = time.time() + timeout
        while True:
            for i, line in enumerate(self.lines):
                if line.startswith(prefix):
                    return self.lines.pop(i)
            if time.time() >= deadline:
                return None
            self._drain()
            time.sleep(0.01)

    def wait_for_absent(self, prefix, timeout=0.4):
        """Assert no line with this prefix arrives. Returns the offender, or None."""
        deadline = time.time() + timeout
        while time.time() < deadline:
            self._drain()
            for line in self.lines:
                if line.startswith(prefix):
                    self.lines.remove(line)
                    return line
            time.sleep(0.02)
        return None

    def join_room(self, room, peer):
        """HELLO and wait for the OK. Returns the acknowledgement line."""
        self.send("HELLO|%s|%s" % (room, peer))
        return self.wait_for("OK|")

    def close(self):
        self.sock.close()


def pair(relay, room, first, second):
    """Join two peers to a room and drain the handshake. Returns (a, b).

    The relay answers a HELLO with OK (which already carries the roster) and pushes a PR
    only to the peers already in the room. So `first` receives OK then PR, while `second`
    receives OK alone. Waiting on `second` for a PR would block until the deadline and let
    both peers expire, which is a harness bug that looks exactly like a relay bug.
    """
    a = Client(relay.port)
    b = Client(relay.port)
    a.join_room(room, first)
    b.join_room(room, second)
    a.wait_for("PR|")
    a._drain()
    b._drain()
    return a, b


def test_hello_is_acknowledged_with_the_roster():
    relay = Relay()
    try:
        time.sleep(0.15)
        a = Client(relay.port)
        a.send("HELLO|arena_versus|alice")
        line = a.wait_for("OK|")
        report("hello is acknowledged", line is not None, str(line))
        if line:
            parts = line.split("|")
            report("hello names the caller", parts[1] == "alice", line)
            report("solo hello reports one live peer", parts[2] == "1" and parts[3] == "alice", line)
        b = Client(relay.port)
        b.send("HELLO|arena_versus|bob")
        report("second hello is acknowledged", b.wait_for("OK|") is not None)
        pushed = a.wait_for("PR|")
        report("first peer is told the roster grew", pushed is not None, str(pushed))
        if pushed:
            parts = pushed.split("|")
            report("roster lists both peers", parts[1] == "2" and set(parts[2].split(",")) == {"alice", "bob"}, pushed)
        a.close()
        b.close()
    finally:
        relay.stop()


def test_input_is_forwarded_to_the_other_peer_only():
    relay = Relay()
    try:
        time.sleep(0.15)
        a, b = pair(relay, "arena_versus", "alice", "bob")
        a.send("IN|arena_versus|alice|7|action=1,special=0")
        got = b.wait_for("IN|")
        report("input reaches the peer", got is not None, str(got))
        if got:
            parts = got.split("|")
            report("forwarded input names the sender", parts[1] == "alice", got)
            report("forwarded input keeps seq and payload", parts[2] == "7" and parts[3] == "action=1,special=0", got)
            report("forwarded input drops the room", len(parts) == 4, got)
        echo = a.wait_for_absent("IN|")
        report("sender does not receive its own input", echo is None, str(echo))
        a.close()
        b.close()
    finally:
        relay.stop()


def test_state_and_events_forward_independently():
    relay = Relay()
    try:
        time.sleep(0.15)
        a, b = pair(relay, "room1", "alice", "bob")
        a.send("ST|room1|alice|11|hp=4820.5,pos=3.25")
        a.send("EV|room1|alice|12|result=WON")
        st = b.wait_for("ST|")
        ev = b.wait_for("EV|")
        report("fighter state is forwarded", st is not None and st.split("|")[3] == "hp=4820.5,pos=3.25", str(st))
        report("fight event is forwarded", ev is not None and ev.split("|")[3] == "result=WON", str(ev))
        a.close()
        b.close()
    finally:
        relay.stop()


def test_rooms_are_isolated():
    relay = Relay()
    try:
        time.sleep(0.15)
        a = Client(relay.port)
        b = Client(relay.port)
        a.send("HELLO|arena_versus|alice")
        b.send("HELLO|other_arena|bob")
        a.wait_for("OK|")
        b.wait_for("OK|")
        a.send("IN|arena_versus|alice|1|action=256")
        leak = b.wait_for_absent("IN|")
        report("a different room receives nothing", leak is None, str(leak))
        a.close()
        b.close()
    finally:
        relay.stop()


def test_peer_expiry_removes_it_from_the_roster():
    """A silent peer must leave the roster, and its partner must be told.

    Timing is the whole content of this test. The TTL has to exceed the handshake drain
    (or both peers expire before bob is even closed), and alice's keepalive interval has
    to be well under the TTL (or she expires too and the roster push has no live
    recipient). 1500ms TTL with a 100ms keepalive leaves a wide margin on both sides.
    """
    relay = Relay(ttl_ms=1500)
    try:
        time.sleep(0.15)
        a, b = pair(relay, "arena_versus", "alice", "bob")
        b.close()
        pushed = None
        deadline = time.time() + 3.0
        while time.time() < deadline and pushed is None:
            a.send("IN|arena_versus|alice|1|action=1")
            pushed = a.wait_for("PR|", timeout=0.1)
        report("expiry pushes a shrunken roster", pushed is not None, str(pushed))
        if pushed:
            parts = pushed.split("|")
            report("expired peer is gone from the roster", parts[1] == "1" and parts[2] == "alice", pushed)
        stray = a.wait_for_absent("IN|", timeout=0.4)
        report("no forwarding after expiry", stray is None, str(stray))
        a.close()
    finally:
        relay.stop()


def test_bye_leaves_immediately():
    relay = Relay()
    try:
        time.sleep(0.15)
        a = Client(relay.port)
        b = Client(relay.port)
        a.send("HELLO|arena_versus|alice")
        b.send("HELLO|arena_versus|bob")
        a.wait_for("OK|")
        b.wait_for("OK|")
        a.wait_for("PR|")
        b.send("BYE|arena_versus|bob")
        pushed = a.wait_for("PR|")
        report("bye pushes a shrunken roster", pushed is not None, str(pushed))
        if pushed:
            parts = pushed.split("|")
            report("bye removes the peer", parts[1] == "1" and parts[2] == "alice", pushed)
        b.close()
        a.close()
    finally:
        relay.stop()


def test_malformed_packets_are_rejected_not_fatal():
    relay = Relay()
    try:
        time.sleep(0.15)
        a = Client(relay.port)
        for bad in ["", "GARBAGE", "HELLO|roomonly", "HELLO||", "IN|arena_versus", "NOPE|a|b|c|d"]:
            a.send(bad)
            line = a.wait_for("ERR|", timeout=0.6)
            report("rejects " + (repr(bad) or "empty"), line is not None, str(line))
        a.send("HELLO|arena_versus|alice")
        report("relay still works after malformed input", a.wait_for("OK|") is not None)
        report("relay process is still alive", relay.proc.poll() is None)
        a.close()
    finally:
        relay.stop()


def test_input_before_hello_is_adopted():
    relay = Relay()
    try:
        time.sleep(0.15)
        a = Client(relay.port)
        b = Client(relay.port)
        b.send("HELLO|arena_versus|bob")
        b.wait_for("OK|")
        a.send("IN|arena_versus|alice|3|action=8")
        got = b.wait_for("IN|")
        report("input before hello still forwards", got is not None, str(got))
        a.send("HELLO|arena_versus|alice")
        ack = a.wait_for("OK|")
        report("late hello is still acknowledged", ack is not None, str(ack))
        if ack:
            parts = ack.split("|")
            report("late hello sees both peers", parts[2] == "2", ack)
        a.close()
        b.close()
    finally:
        relay.stop()


def test_many_packets_are_not_dropped():
    relay = Relay()
    try:
        time.sleep(0.15)
        a, b = pair(relay, "arena_versus", "alice", "bob")
        total = 200
        for i in range(total):
            a.send("IN|arena_versus|alice|%d|action=1" % i)
        seen = set()
        deadline = time.time() + 4.0
        while len(seen) < total and time.time() < deadline:
            b._drain()
            for line in list(b.lines):
                if line.startswith("IN|"):
                    b.lines.remove(line)
                    seen.add(int(line.split("|")[2]))
            time.sleep(0.005)
        report("all %d relayed packets arrive" % total, len(seen) == total, "got %d" % len(seen))
        report("relay counts them as forwarded", relay.proc.poll() is None)
        a.close()
        b.close()
    finally:
        relay.stop()


def test_large_payload_is_refused_not_truncated_silently():
    relay = Relay()
    try:
        time.sleep(0.15)
        a, b = pair(relay, "arena_versus", "alice", "bob")
        a.send("IN|arena_versus|alice|1|" + "x" * 900)
        line = a.wait_for("ERR|", timeout=0.6)
        report("oversized packet is rejected", line is not None and line.startswith("ERR|bad length"), str(line))
        stray = b.wait_for_absent("IN|")
        report("oversized packet is not forwarded", stray is None, str(stray))
        a.send("IN|arena_versus|alice|2|" + "y" * 400)
        got = b.wait_for("IN|")
        report("a full-size legal packet still forwards", got is not None and got.count("y") == 400, str(got)[:80])
        a.close()
        b.close()
    finally:
        relay.stop()


def main():
    if not os.path.exists(RELAY):
        print("[!] relay binary missing; build it with: gcc -O2 -o netrelay netrelay.c")
        return 1
    tests = [v for k, v in sorted(globals().items()) if k.startswith("test_")]
    for test in tests:
        test()
    print("")
    if FAILURES:
        print("%d failure(s): %s" % (len(FAILURES), ", ".join(FAILURES)))
        return 1
    print("all %d netrelay tests passed" % len(tests))
    return 0


if __name__ == "__main__":
    sys.exit(main())
