"""Local web UI entry point for planning an APK patch build."""

from __future__ import annotations

import argparse
from dataclasses import asdict
import json
from http import HTTPStatus
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from urllib.parse import urlparse, parse_qs
import sys
import webbrowser


MODULE_DIR = Path(__file__).resolve().parent
if str(MODULE_DIR) not in sys.path:
    sys.path.insert(0, str(MODULE_DIR))
import pipeline  # noqa: E402
import runner as runner_module  # noqa: E402


STATIC_DIR = MODULE_DIR / "static"

# One shared Runner instance for the whole server; all handler threads reach it.
RUNNER = runner_module.Runner()


class PatcherHandler(BaseHTTPRequestHandler):
    server_version = "APK Patcher GUI"

    def log_message(self, format: str, *args: object) -> None:
        """Keep normal browser traffic from cluttering the launch terminal."""

    def _send_bytes(self, status: HTTPStatus, content_type: str, body: bytes) -> None:
        self.send_response(status)
        self.send_header("Content-Type", content_type)
        self.send_header("Content-Length", str(len(body)))
        self.send_header("Cache-Control", "no-store")
        self.end_headers()
        self.wfile.write(body)

    def _send_json(self, status: HTTPStatus, payload: object) -> None:
        self._send_bytes(status, "application/json; charset=utf-8", json.dumps(payload).encode("utf-8"))

    def _read_json(self) -> dict[str, object] | None:
        try:
            length = int(self.headers.get("Content-Length", "0"))
            body = self.rfile.read(length)
            value = json.loads(body.decode("utf-8"))
        except (ValueError, UnicodeDecodeError, json.JSONDecodeError):
            return None
        return value if isinstance(value, dict) else None

    def do_GET(self) -> None:  # noqa: N802
        parsed = urlparse(self.path)
        path = parsed.path
        query = parse_qs(parsed.query)
        if path == "/":
            self._serve_file(STATIC_DIR / "index.html", "text/html; charset=utf-8")
        elif path == "/api/defaults":
            request = pipeline.default_request()
            self._send_json(
                HTTPStatus.OK,
                {
                    "defaults": asdict(request),
                    "suggested_patched_il2cpp": (
                        str(pipeline.DEFAULT_PATCHED_IL2CPP)
                        if pipeline.DEFAULT_PATCHED_IL2CPP.is_file()
                        else ""
                    ),
                    "tool_status": {
                        "legible": bool(request.legible),
                        "zipalign": bool(request.zipalign),
                        "apksigner": bool(request.apksigner),
                        "java": bool(request.java),
                        "adb": bool(request.adb),
                    },
                },
            )
        elif path == "/api/log":
            raw_offset = query.get("offset", ["0"])[0]
            try:
                offset = int(raw_offset)
            except (TypeError, ValueError):
                offset = 0
            self._send_json(HTTPStatus.OK, RUNNER.snapshot(offset))
        elif path.startswith("/static/"):
            name = path.removeprefix("/static/")
            allowed = {"app.js": "application/javascript; charset=utf-8", "style.css": "text/css; charset=utf-8"}
            if name not in allowed or "/" in name or "\\" in name:
                self._send_json(HTTPStatus.NOT_FOUND, {"error": "not found"})
            else:
                self._serve_file(STATIC_DIR / name, allowed[name])
        else:
            self._send_json(HTTPStatus.NOT_FOUND, {"error": "not found"})

    def _serve_file(self, path: Path, content_type: str) -> None:
        try:
            self._send_bytes(HTTPStatus.OK, content_type, path.read_bytes())
        except FileNotFoundError:
            self._send_json(HTTPStatus.NOT_FOUND, {"error": "not found"})

    def do_POST(self) -> None:  # noqa: N802
        path = urlparse(self.path).path
        # Cancellation has no request payload.  The other API endpoints require
        # a JSON object because they turn those values into a BuildRequest.
        if path == "/api/cancel":
            RUNNER.cancel()
            self._send_json(HTTPStatus.OK, RUNNER.snapshot(0))
            return
        values = self._read_json()
        if values is None:
            self._send_json(HTTPStatus.BAD_REQUEST, {"error": "expected a JSON object"})
            return
        if path == "/api/validate":
            request = pipeline.request_from_mapping(values)
            errors, warnings = pipeline.validate(request)
            steps = [
                {"name": step.name, "argv": step.argv}
                for step in pipeline.plan_steps(request)
            ]
            self._send_json(HTTPStatus.OK, {"errors": errors, "warnings": warnings, "steps": steps})
        elif path == "/api/run":
            request = pipeline.request_from_mapping(values)
            errors, warnings = pipeline.validate(request)
            if errors:
                self._send_json(HTTPStatus.BAD_REQUEST, {"errors": errors, "warnings": warnings})
                return
            steps = pipeline.plan_steps(request)
            started = RUNNER.start(steps)
            if not started:
                self._send_json(
                    HTTPStatus.CONFLICT,
                    {"error": "a build is already in progress", "started": False},
                )
                return
            self._send_json(
                HTTPStatus.OK,
                {
                    "started": True,
                    "steps": [{"name": step.name, "argv": step.argv} for step in steps],
                },
            )
        else:
            self._send_json(HTTPStatus.NOT_FOUND, {"error": "not found"})


def main() -> None:
    parser = argparse.ArgumentParser(description="Local APK patcher options UI")
    parser.add_argument("--port", type=int, default=0, help="Local port (0 selects a free port)")
    parser.add_argument("--host", default="127.0.0.1", help="Loopback host to bind")
    parser.add_argument("--no-browser", action="store_true", help="Do not open the page automatically")
    args = parser.parse_args()
    if args.host != "127.0.0.1":
        parser.error("this local UI may only bind to 127.0.0.1")
    httpd = ThreadingHTTPServer((args.host, args.port), PatcherHandler)
    url = f"http://{args.host}:{httpd.server_port}/"
    print(f"APK patcher GUI listening at {url}", flush=True)
    if not args.no_browser:
        webbrowser.open(url)
    try:
        httpd.serve_forever()
    except KeyboardInterrupt:
        print("\nShutting down.")
    finally:
        httpd.server_close()


if __name__ == "__main__":
    main()
