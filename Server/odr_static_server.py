#!/usr/bin/env python3
"""Static file server for TFTF_ODR_DIR (toc.zip etc.) with Range support.

Usage: TFTF_ODR_DIR=/path/to/odr [PORT=8090] python3 odr_static_server.py
Manifest 'cdn' must be http://127.0.0.1:8090 (urls like /odr/wad/toc.zip are
resolved relative to the parent of TFTF_ODR_DIR's 'odr' dir, and also directly
inside it); run `adb reverse tcp:8090 tcp:8090`.
"""
import os, re, sys
from http.server import ThreadingHTTPServer, BaseHTTPRequestHandler

ROOT = os.path.abspath(os.environ.get("TFTF_ODR_DIR") or (sys.argv[1] if len(sys.argv) > 1 else "."))
PORT = int(os.environ.get("PORT", sys.argv[2] if len(sys.argv) > 2 else 8090))


def resolve(urlpath):
    rel = os.path.normpath(urlpath.split("?")[0].lstrip("/"))
    cands = [rel]
    if rel.startswith("odr" + os.sep):
        cands.append(rel[4:])
    for c in cands:
        full = os.path.abspath(os.path.join(ROOT, c))
        if (full == ROOT or full.startswith(ROOT + os.sep)) and os.path.isfile(full):
            return full
    return None


class H(BaseHTTPRequestHandler):
    def _send(self, body):
        full = resolve(self.path)
        if not full:
            self.send_error(404)
            return
        size = os.path.getsize(full)
        start, end, code = 0, size - 1, 200
        m = re.match(r"bytes=(\d*)-(\d*)$", self.headers.get("Range", ""))
        if m and (m.group(1) or m.group(2)):
            if m.group(1):
                start = int(m.group(1))
                if m.group(2):
                    end = min(int(m.group(2)), size - 1)
            else:
                start = max(0, size - int(m.group(2)))
            if start >= size:
                self.send_response(416)
                self.send_header("Content-Range", "bytes */%d" % size)
                self.end_headers()
                return
            code = 206
        self.send_response(code)
        self.send_header("Content-Type", "application/json" if full.endswith(".json") else "application/octet-stream")
        self.send_header("Accept-Ranges", "bytes")
        self.send_header("Content-Length", str(end - start + 1))
        if code == 206:
            self.send_header("Content-Range", "bytes %d-%d/%d" % (start, end, size))
        self.end_headers()
        if body:
            with open(full, "rb") as f:
                f.seek(start)
                left = end - start + 1
                while left > 0:
                    chunk = f.read(min(1 << 20, left))
                    if not chunk:
                        break
                    try:
                        self.wfile.write(chunk)
                    except (BrokenPipeError, ConnectionResetError):
                        return
                    left -= len(chunk)

    def do_GET(self):
        self._send(True)

    def do_HEAD(self):
        self._send(False)


if __name__ == "__main__":
    print("serving %s on 0.0.0.0:%d" % (ROOT, PORT))
    ThreadingHTTPServer(("0.0.0.0", PORT), H).serve_forever()
