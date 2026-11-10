#!/usr/bin/env python3
"""Play a raid on the phone from wherever the game is, and report what happened (needs a build with TftfBot.cs and
TftfRaidDiag.cs; best used with devserver.sh so the server runs on this PC).

  raid_e2e.py board [--npc N] [--fresh]    get to the raid board (start a raid against opponent N if none is running;
                                           --fresh quits a running raid first)
  raid_e2e.py step                         on the board: move to the next defender and wait for the pre-fight screen
  raid_e2e.py fight [--lose]               on the pre-fight screen: start the fight and play it (the fallback rule
                                           of tftfbot.py; --lose plays no inputs), until the board or the results show
  raid_e2e.py raid [--npc N] [--lose-at K] the whole raid: board --fresh, then step and fight until the results screen
  raid_e2e.py where                        the screen the game is on
  raid_e2e.py launch                       restart the game and wait for the home screen

Every action checks that the game is in front and waits for the screen it expects ("[TFTFBOT] screen=<name>" in
logcat); nothing is tapped blind. On a failed wait the run stops, saves a screenshot and prints the game's last warnings
and errors. The exit status is 0 only when the command reached its goal. Coordinates are for the 2400x1080 Samsung A52.
"""
import argparse, os, re, subprocess, sys, threading, time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from tftfbot import rule_choice  # noqa: E402

PKG = "unicronslair.bigrobot"
DATA = "/sdcard/Android/data/%s/files/tftf" % PKG
OUT = os.environ.get("TFTF_E2E_OUT", os.path.expanduser("~/tftf-build/devserver/e2e"))

# windows that are part of a flow; every other window (faders, overlays, menu bar) is ignored when asking where we are
MAIN = {"TransformersHomeScreen", "FightLandingScreen", "PVPRaidSelect", "ConfirmationPopup", "GenericPopup", "TeamSelectScreen",
        "QuestboardHud", "PreFightScreen", "PVPRaidResults", "BaseEditNodePopup", "FuseSocialHub", "ShieldWarning", "NSFPopup",
        "FightHud", "PostFightScreen", "FightResultsScreen", "PVPRaidReport", "FuseStoreScreen", "NodeInfoPopup"}
TAPS = {
    "play": (1200, 870), "fight_tab": (1200, 178), "raids_card": (1200, 340), "npc": [(726, 956), (1200, 956), (1674, 956)],
    "confirm_raid": (1354, 508), "start_raid": (2216, 980), "board_quit": (2313, 884), "quit_confirm": (1354, 627),
    "raid_more": (1408, 680), "prefight_fight": (1725, 993), "back": (65, 55), "chat_close": (2350, 40), "after_ko": (1200, 1000),
}


class Phone:
    def __init__(self, serial):
        self.serial = serial
        self.lines = []          # (time, text) of every Unity log line since start
        self.screens = []        # (time, name)
        self.cond = threading.Condition()
        # -T 400: also read the tail of the buffer, so the current screen is known at start
        self.proc = subprocess.Popen(["adb", "-s", serial, "logcat", "-v", "raw", "-T", "3000", "-s", "Unity:*"], stdout=subprocess.PIPE, text=True, bufsize=1, errors="replace")
        self.started = time.time()
        threading.Thread(target=self._read, daemon=True).start()
        time.sleep(1.0)          # let the buffered tail arrive

    def _read(self):
        for line in self.proc.stdout:
            line = line.rstrip()
            if not line:
                continue
            with self.cond:
                now = time.time()
                self.lines.append((now, line))
                i = line.find("[TFTFBOT] screen=")
                if i >= 0:
                    self.screens.append((now, line[i + 17:].strip()))
                self.cond.notify_all()

    def adb(self, *args):
        return subprocess.run(["adb", "-s", self.serial, *args], capture_output=True, text=True).stdout

    def in_front(self):
        return PKG in self.adb("shell", "dumpsys", "window").split("mCurrentFocus=")[-1].split("\n")[0]

    def where(self):
        with self.cond:
            for _, name in reversed(self.screens):
                if name in MAIN:
                    return name
        return "?"

    def wait(self, want, timeout=30, since=None, pattern=None):
        """Wait for one of the windows in `want` (names) to open after `since`, or for a log line matching `pattern`."""
        want = {want} if isinstance(want, str) else set(want)
        since = since if since is not None else time.time()
        end = time.time() + timeout
        rx = re.compile(pattern) if pattern else None
        with self.cond:
            while True:
                for t, name in self.screens:
                    if t >= since and name in want:
                        return name
                if rx:
                    for t, line in self.lines:
                        if t >= since and rx.search(line):
                            return line
                left = end - time.time()
                if left <= 0:
                    return None
                self.cond.wait(min(left, 1.0))

    def tap(self, name, index=None):
        if not self.in_front():
            fail(self, "the game is not in front; not tapping")
        x, y = TAPS[name][index] if index is not None else TAPS[name]
        t = time.time()
        self.adb("shell", "input", "tap", str(x), str(y))
        return t

    def tap_wait(self, name, want, timeout=30, tries=2, index=None):
        for _ in range(tries):
            t = self.tap(name, index)
            got = self.wait(want, timeout, since=t - 0.2)
            if got:
                say("%s -> %s" % (name, got))
                return got
        fail(self, "tapped %s, but none of %s opened" % (name, sorted({want} if isinstance(want, str) else want)))

    def shot(self, name):
        os.makedirs(OUT, exist_ok=True)
        path = os.path.join(OUT, name + ".png")
        with open(path, "wb") as f:
            f.write(subprocess.run(["adb", "-s", self.serial, "exec-out", "screencap", "-p"], capture_output=True).stdout)
        return path

    def recent(self, pattern, since, limit=12):
        rx = re.compile(pattern)
        with self.cond:
            return [l for t, l in self.lines if t >= since and rx.search(l)][-limit:]


T0 = time.time()


def say(text):
    print("[%5.1fs] %s" % (time.time() - T0, text), flush=True)


def fail(phone, why):
    say("FAILED: " + why)
    say("screen: %s; screenshot: %s" % (phone.where(), phone.shot("failed")))
    for line in phone.recent(r" [WE]: (?!.*(ODRManager|Failed to get url|Unhandled Team|AssetManager))|Exception|\[PVPRAID\]|\[RAIDMOVE\]", time.time() - 40):
        print("    " + line[:260])
    sys.exit(1)


def to_home(phone):
    here = phone.where()
    if here == "?":
        # no window logged yet: the title screen (its button is the only green thing at that spot) or still loading
        say("no screen known; waiting for the title or home")
        end = time.time() + 300
        while time.time() < end and phone.where() == "?":
            from PIL import Image
            r, g, b = Image.open(phone.shot(".probe")).convert("RGB").getpixel(TAPS["play"])
            if g > 60 and g > r * 1.25 and g > b * 1.5:
                phone.tap_wait("play", "TransformersHomeScreen", 240, tries=1)
                time.sleep(3)
                return
            time.sleep(2)
        here = phone.where()
    for _ in range(6):
        here = phone.where()
        if here == "TransformersHomeScreen":
            return
        if here in ("FuseSocialHub",):
            phone.tap("chat_close"); time.sleep(1.5); phone.screens.append((time.time(), "QuestboardHud")); continue
        t = time.time()
        phone.adb("shell", "input", "keyevent", "KEYCODE_BACK") if phone.in_front() else fail(phone, "the game is not in front")
        if not phone.wait(MAIN, 15, since=t):
            break
    if phone.where() != "TransformersHomeScreen":
        fail(phone, "could not get back to home")


def quit_raid(phone):
    phone.tap_wait("board_quit", "GenericPopup", 10)
    time.sleep(0.5)
    phone.tap_wait("quit_confirm", "PVPRaidResults", 30, tries=1)
    time.sleep(2)


def board(phone, npc=0, fresh=False):
    for _ in range(12):
        here = phone.where()
        if here == "QuestboardHud":
            if not fresh:
                return
            quit_raid(phone); fresh = False
        elif here == "PreFightScreen" and fresh:
            phone.tap_wait("back", "QuestboardHud", 15, tries=1)
            time.sleep(1)
            quit_raid(phone); fresh = False
        elif here == "PVPRaidResults":
            fresh = False
            phone.tap_wait("raid_more", {"PVPRaidSelect", "TeamSelectScreen"}, 30)
            time.sleep(1.5)
        elif here == "PVPRaidSelect":
            phone.tap_wait("npc", "ConfirmationPopup", 10, index=npc)
            time.sleep(0.8)
            phone.tap_wait("confirm_raid", {"TeamSelectScreen", "PVPRaidSelect"}, 40, tries=1)
            time.sleep(2)
        elif here == "TeamSelectScreen":
            phone.tap_wait("start_raid", "QuestboardHud", 60, tries=1)
            time.sleep(5)        # the board settles (cards, paths) after its HUD opens
            return
        elif here == "FightLandingScreen":
            phone.tap_wait("raids_card", {"PVPRaidSelect", "TeamSelectScreen", "QuestboardHud"}, 30)
            time.sleep(2)
        elif here == "TransformersHomeScreen":
            phone.tap_wait("fight_tab", "FightLandingScreen", 15, tries=3)
            time.sleep(1)
        else:
            to_home(phone)
    fail(phone, "did not reach the raid board")


def step(phone):
    """Move east onto the next defender through TftfRaidDiag's command file; the board then opens the pre-fight screen."""
    if phone.where() != "QuestboardHud":
        fail(phone, "not on the board")
    t = time.time()
    seq = int(t * 10) % 100000000
    phone.adb("shell", "echo '%d EAST' > %s/raidcmd.txt" % (seq, DATA))
    if not phone.wait([], 15, since=t, pattern=r"\[RAIDMOVE\] seq=%d result=" % seq):
        fail(phone, "the move command was not taken")
    result = phone.recent(r"\[RAIDMOVE\] seq=%d result=" % seq, t)[-1].split("result=")[-1]
    say("move east: " + result)
    if result != "ok":
        fail(phone, "the move failed: " + result)
    got = phone.wait("PreFightScreen", 45, since=t)
    if not got:
        fail(phone, "the pre-fight screen did not open after the move")
    say("pre-fight screen")
    time.sleep(2)


def fight(phone, lose=False):
    if phone.where() != "PreFightScreen":
        fail(phone, "not on the pre-fight screen")
    phone.adb("shell", "touch", DATA + "/bot.on")
    t = phone.tap("prefight_fight")
    if not phone.wait([], 90, since=t, pattern=r"\[TFTFBOT\] fight=start"):
        fail(phone, "the fight did not start")
    say("fight started" + (" (playing to lose)" if lose else ""))
    seq, seen, last = int(time.time()) % 100000, 0, ""
    end = time.time() + 300
    while time.time() < end:
        with phone.cond:
            phone.cond.wait(0.2)
            new = phone.lines[seen:]
            seen = len(phone.lines)
        done = False
        for lt, line in new:
            if lt < t:
                continue
            if "[TFTFBOT] fight=end" in line:
                done = True
            i = line.find("[TFTFBOT] state ")
            if i >= 0:
                last = line[i + 16:]
        if done:
            break
        if last and not lose:
            fields = dict(kv.split("=", 1) for kv in last.split() if "=" in kv)
            last = ""
            if fields.get("me") in ("hitstun", "stunned", "down") or fields.get("busy") == "1":
                continue
            seq += 1
            phone.adb("shell", "echo '%d %s' > %s/botcmd.txt" % (seq, rule_choice(fields), DATA))
    else:
        fail(phone, "the fight did not end within 5 minutes")
    say("fight ended: " + (phone.recent(r"\[TFTFBOT\] state ", t)[-1].split("state ")[-1][:60] if phone.recent(r"\[TFTFBOT\] state ", t) else "?"))
    phone.adb("shell", "rm", "-f", DATA + "/bot.on", DATA + "/botcmd.txt")
    # after the K.O. the game shows its fight results; tap through until the board or the raid results are back
    t2 = time.time()
    for _ in range(40):
        got = phone.wait({"QuestboardHud", "PVPRaidResults", "PreFightScreen"}, 3, since=t2)
        if got:
            time.sleep(4)
            current = phone.where()
            if current in {"QuestboardHud", "PVPRaidResults", "PreFightScreen"}:
                say("after the fight: " + current)
                return current
        if phone.in_front():
            phone.tap("after_ko")
    fail(phone, "neither the board nor the raid results came back after the fight")


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("-s", "--serial", default=os.environ.get("ANDROID_SERIAL", "RX8R30BF6SZ"))
    sub = ap.add_subparsers(dest="cmd", required=True)
    p = sub.add_parser("board"); p.add_argument("--npc", type=int, default=0); p.add_argument("--fresh", action="store_true")
    sub.add_parser("step")
    p = sub.add_parser("fight"); p.add_argument("--lose", action="store_true")
    p = sub.add_parser("raid"); p.add_argument("--npc", type=int, default=0); p.add_argument("--lose-at", type=int, default=0)
    sub.add_parser("where")
    sub.add_parser("launch")
    a = ap.parse_args()
    if a.cmd == "launch":       # restart the game and wait for home
        subprocess.run(["adb", "-s", a.serial, "shell", "am", "force-stop", PKG])
        subprocess.run(["adb", "-s", a.serial, "logcat", "-c"])
        subprocess.run(["adb", "-s", a.serial, "shell", "monkey", "-p", PKG, "-c", "android.intent.category.LAUNCHER", "1"], capture_output=True)
        time.sleep(3)
        phone = Phone(a.serial)
        to_home(phone); say("home")
        return
    phone = Phone(a.serial)
    if a.cmd == "where":
        print(phone.where())
    elif a.cmd == "board":
        board(phone, a.npc, a.fresh); say("on the board")
    elif a.cmd == "step":
        step(phone)
    elif a.cmd == "fight":
        fight(phone, a.lose)
    elif a.cmd == "raid":
        board(phone, a.npc, fresh=True)
        for n in range(1, 10):
            if phone.where() != "PreFightScreen":
                step(phone)
            if fight(phone, lose=(n == a.lose_at)) == "PVPRaidResults":
                break
        else:
            fail(phone, "raid did not reach results within nine fights")
        say("raid finished; results screen: " + phone.shot("results"))


if __name__ == "__main__":
    main()
