#!/usr/bin/env python3
"""A stand-in opponent for testing online Arena fights with one phone (see mono310/launcher/ONLINE_PVP.md).

It joins a room on the relay like a phone would, answers the phone's bot with its own and returns an input line for
every tick the phone sends, so the phone's fight runs at full speed against an opponent that stands still (or, with
--mash, taps attack now and then). It does not simulate the fight, so it must be the guest: tap "Find opponent" on the
phone first, then start this. As host it would never decide a knock-out.

Usage: python3 Server/pvp_fake_peer.py [--relay 127.0.0.1:7777] [--room test] [--bot optimusprime_cin_tf] [--mash]
"""
import argparse
import random
import socket


def main():
    ap = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    ap.add_argument("--relay", default="127.0.0.1:7777")
    ap.add_argument("--room", default="-")
    ap.add_argument("--name", default="FakePeer")
    ap.add_argument("--bot", default="optimusprime_cin_tf", help="blueprint id of the bot this opponent fights with")
    ap.add_argument("--rank", type=int, default=1)
    ap.add_argument("--level", type=int, default=1)
    ap.add_argument("--mash", action="store_true", help="tap attack at random")
    args = ap.parse_args()
    host, _, port = args.relay.partition(":")
    sock = socket.create_connection((host, int(port or 7777)))
    sock.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
    f = sock.makefile("rw", encoding="ascii", newline="\n")

    def send(line):
        f.write(line + "\n")
        f.flush()

    send("HELLO 1 %s %s" % (args.room, args.name))
    for line in f:
        p = line.split()
        if not p:
            continue
        if p[0] in ("WAIT", "ERR", "BYE"):
            print(line.strip())
            if p[0] != "WAIT":
                return
        elif p[0] == "MATCH":
            print("matched with %s as %s" % (" ".join(p[3:]), "host" if p[1] == "0" else "guest"))
            if p[1] == "0":
                print("warning: this stand-in is the host and never decides knock-outs; start the phone's search first")
        elif p[0] == "P":
            send("O " + p[1])
        elif p[0] == "H" and len(p) >= 7:
            print("fight %s: phone picked %s, answering with %s" % (p[1], p[2], args.bot))
            send("H %s %s %d %d 0 %s %s" % (p[1], args.bot, args.rank, args.level, p[6], p[7] if len(p) > 7 else "0"))
        elif p[0] == "T" and len(p) >= 4:
            events = "-"
            if args.mash and random.random() < 0.08:
                events = "a"
            elif args.mash and random.random() < 0.08:
                events = "A"
            send("T %s %s %s" % (p[1], p[2], events))
        elif p[0] == "K":
            print("knock-out of fighter %s at tick %s" % (p[3], p[2]))


if __name__ == "__main__":
    main()
