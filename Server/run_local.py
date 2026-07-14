#!/usr/bin/env python3
"""Run the fake Sparx server on unprivileged ports for the adb-reverse setup.

The emulator's guest hosts file points the Kabam domains at 127.0.0.1, and
`adb reverse tcp:443 tcp:8443` / `tcp:80 tcp:8080` forward the guest's
privileged ports (bound by the guest's root adbd) to these host ports. This
lets the server run as a normal user with no sudo.
"""
import os, sys, threading
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import fakeserver as fs

HTTP_PORT = int(os.environ.get("HTTP_PORT", "8080"))
HTTPS_PORT = int(os.environ.get("HTTPS_PORT", "8443"))

if __name__ == "__main__":
    open(fs.LOGFILE, "w").close()
    threading.Thread(target=fs.serve_http, args=(HTTP_PORT,), daemon=True).start()
    fs.serve_https(HTTPS_PORT)
