#!/usr/bin/env python3
"""Export a Unity APK to a Unity project with the pinned headless AssetRipper.

usage: rip_assets.py APK OUT_DIR [--assetripper PATH]
AssetRipper's settings (TargetVersion 2020.3.31f1) come from the
AssetRipper.Settings.json next to the binary.
"""
import argparse, os, signal, socket, subprocess, sys, time, urllib.parse, urllib.request
from pathlib import Path

DEFAULT_RIPPER = ""


def post(url, values, timeout):
    req = urllib.request.Request(url, data=urllib.parse.urlencode(values).encode(),
                                 headers={"Content-Type": "application/x-www-form-urlencoded"})
    with urllib.request.urlopen(req, timeout=timeout) as r:
        return r.read()


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("apk", type=Path); ap.add_argument("out", type=Path)
    ap.add_argument("--assetripper", default=os.environ.get("ASSETRIPPER_BIN", DEFAULT_RIPPER))
    a = ap.parse_args()
    a.out.mkdir(parents=True, exist_ok=True)
    with socket.socket() as s:
        s.bind(("127.0.0.1", 0)); port = s.getsockname()[1]
    log = open(a.out.parent / (a.out.name + ".assetripper.log"), "wb")
    proc = subprocess.Popen([a.assetripper, "--headless", "--port", str(port)],
                            cwd=Path(a.assetripper).parent, stdout=log, stderr=subprocess.STDOUT)
    base = f"http://127.0.0.1:{port}"
    try:
        for _ in range(120):
            try:
                urllib.request.urlopen(base, timeout=2); break
            except OSError:
                if proc.poll() is not None:
                    sys.exit("AssetRipper exited early")
                time.sleep(1)
        post(f"{base}/LoadFile", {"path": str(a.apk.resolve())}, 3600)
        post(f"{base}/Export/UnityProject", {"Path": str(a.out), "CreateSubfolder": "false"}, 14400)
        print("exported", a.out)
    finally:
        proc.send_signal(signal.SIGTERM)
        try: proc.wait(20)
        except subprocess.TimeoutExpired: proc.kill()


if __name__ == "__main__":
    main()
