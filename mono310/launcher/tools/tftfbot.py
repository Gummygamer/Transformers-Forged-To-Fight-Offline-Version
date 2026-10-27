#!/usr/bin/env python3
"""Drive the launcher on a phone for device checks (needs a build with shell/TftfMain/TftfBot.cs).

  tftfbot.py screen NAME [--timeout S]     wait until the game shows window NAME (logged as "[TFTFBOT] screen=NAME")
  tftfbot.py screens                       print every window name as it is shown (to learn the names for a flow)
  tftfbot.py tap X Y [--wait NAME]         tap, then optionally wait for a window
  tftfbot.py fight [--shots DIR] [--fights N]
                                           play fights: the game logs a compact state, Jev (typesafe.ai) picks the next
                                           action, the action goes back through tftf/botcmd.txt. Needs TYPESAFE_API_KEY;
                                           without it a fixed rule picks the action. --shots saves a screenshot every 2 s.

Phone coordinates are for the 2400x1080 Samsung A52. Use -s SERIAL for another device.
"""
import argparse, json, os, subprocess, sys, threading, time, urllib.request

DATA = "/sdcard/Android/data/unicronslair.bigrobot/files/tftf"
ACTIONS = {
    "light": "quick light attack, interrupts an idle or recovering opponent",
    "heavy": "charged heavy attack, breaks a block, slow to start",
    "block": "block for 0.6 s, stops light and medium attacks",
    "dodge": "dash backwards, evades a heavy or special attack",
    "dash": "dash forwards to close the distance",
    "combo": "dash in and chain four light attacks, best on an idle or recovering opponent in range",
    "special": "special attack, only possible when special is 1 or more",
    "wait": "do nothing this step",
}
INSTRUCTIONS = ("You control the left robot in a one-on-one fighting game. Pick the next action that wins the fight. "
                "range=melee means attacks connect; near/far means close the distance first. op is what the opponent is doing: "
                "block or dodge when it is starting an attack, attack when it is idle or recovering. Use special when special is 1 or more.")


def adb(serial, *args, **kw):
    return subprocess.run(["adb", "-s", serial, *args], capture_output=True, text=True, **kw)


def logcat(serial):
    adb(serial, "logcat", "-c")
    return subprocess.Popen(["adb", "-s", serial, "logcat", "-v", "raw", "-s", "Unity:*"], stdout=subprocess.PIPE, text=True, bufsize=1)


def bot_lines(proc):
    for line in proc.stdout:
        i = line.find("[TFTFBOT] ")
        if i >= 0:
            yield line[i + 10:].strip()


def wait_screen(serial, name, timeout):
    proc = logcat(serial)
    end = time.time() + timeout
    timer = threading.Timer(timeout, proc.kill); timer.start()
    try:
        for msg in bot_lines(proc):
            if msg == "screen=" + name:
                return True
            if time.time() > end:
                break
        return False
    finally:
        timer.cancel(); proc.kill()


def jev_choice(key, state):
    body = json.dumps({"model": "jev-latest", "state": state,
                       "questions": {"act": {"type": "choice", "instructions": INSTRUCTIONS, "criteria": ACTIONS}}}).encode()
    req = urllib.request.Request("https://api.typesafe.ai/v1/systemone", data=body, method="POST",
                                 headers={"Authorization": "Bearer " + key, "Content-Type": "application/json", "User-Agent": "tftfbot/1.0"})
    with urllib.request.urlopen(req, timeout=5) as resp:
        a = json.loads(resp.read())["answers"]["act"]
    return a["choice"], a.get("confidence")


def rule_choice(fields):
    """Fallback without a key: block attacks, punish recovery, otherwise close in and attack."""
    op, rng = fields.get("op", "idle"), fields.get("range", "far")
    if fields.get("special", "0") != "0" and rng == "melee":
        return "special"
    if op in ("heavy_startup", "special"):
        return "dodge"
    if op == "attack_startup":
        return "block"
    if rng != "melee":
        return "dash"
    return "combo" if op in ("attack_recovery", "stunned", "idle") else "light"


def fight(serial, shots, fights):
    key = os.environ.get("TYPESAFE_API_KEY", "")
    print("deciding with", "Jev" if key else "the fallback rule")
    adb(serial, "shell", "touch", DATA + "/bot.on")
    proc = logcat(serial)
    seq, done, busy, last_shot, n = int(time.time()) % 100000, 0, threading.Event(), 0.0, 0

    def decide(state, fields):
        nonlocal seq
        try:
            act, conf = jev_choice(key, state) if key else (rule_choice(fields), None)
        except Exception as e:      # network trouble must not stall the fight
            act, conf = rule_choice(fields), "fallback:" + type(e).__name__
        seq += 1
        adb(serial, "shell", "echo '%d %s' > %s/botcmd.txt" % (seq, act, DATA))
        print("  %-60s -> %-7s %s" % (state, act, "" if conf is None else conf))
        busy.clear()

    try:
        for msg in bot_lines(proc):
            if msg.startswith("fight="):
                print(msg)
                if msg == "fight=end":
                    done += 1
                    if done >= fights:
                        break
            elif msg.startswith("state "):
                fields = dict(kv.split("=", 1) for kv in msg.split()[1:] if "=" in kv)
                # inputs are ignored while stunned; a running combo is only cut short by an incoming attack
                threat = fields.get("op") in ("attack_startup", "heavy_startup", "special", "attacking", "heavy")
                if busy.is_set() or fields.get("me") in ("hitstun", "stunned", "down") or (fields.get("busy") == "1" and not threat):
                    continue
                state = " ".join("%s=%s" % (k, v) for k, v in fields.items() if k not in ("seq", "busy"))
                busy.set()
                threading.Thread(target=decide, args=(state, fields), daemon=True).start()
            if shots and time.time() - last_shot >= 2:
                last_shot = time.time(); n += 1
                os.makedirs(shots, exist_ok=True)
                with open(os.path.join(shots, "fight%03d.png" % n), "wb") as f:
                    f.write(subprocess.run(["adb", "-s", serial, "exec-out", "screencap", "-p"], capture_output=True).stdout)
    finally:
        proc.kill()
        adb(serial, "shell", "rm", "-f", DATA + "/bot.on", DATA + "/botcmd.txt")


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("-s", "--serial", default=os.environ.get("ANDROID_SERIAL", "RX8R30BF6SZ"))
    sub = ap.add_subparsers(dest="cmd", required=True)
    p = sub.add_parser("screen"); p.add_argument("name"); p.add_argument("--timeout", type=float, default=120)
    sub.add_parser("screens")
    p = sub.add_parser("tap"); p.add_argument("x"); p.add_argument("y"); p.add_argument("--wait"); p.add_argument("--timeout", type=float, default=120)
    p = sub.add_parser("fight"); p.add_argument("--shots"); p.add_argument("--fights", type=int, default=1)
    a = ap.parse_args()
    if a.cmd == "screen":
        sys.exit(0 if wait_screen(a.serial, a.name, a.timeout) else 1)
    if a.cmd == "screens":
        proc = logcat(a.serial)
        for msg in bot_lines(proc):
            if msg.startswith("screen="):
                print(msg[7:], flush=True)
    if a.cmd == "tap":
        if a.wait:
            # start listening before the tap so a fast screen change is not missed
            res = {}
            t = threading.Thread(target=lambda: res.update(ok=wait_screen(a.serial, a.wait, a.timeout))); t.start(); time.sleep(0.5)
            adb(a.serial, "shell", "input", "tap", a.x, a.y); t.join()
            sys.exit(0 if res.get("ok") else 1)
        adb(a.serial, "shell", "input", "tap", a.x, a.y)
    if a.cmd == "fight":
        fight(a.serial, a.shots, a.fights)


if __name__ == "__main__":
    main()
