"""Background subprocess runner for the APK patcher pipeline.

Runs ``pipeline.Step`` objects one at a time, streaming each subprocess's
combined stdout/stderr line by line into an in-memory log.  Everything here is
stdlib-only (subprocess, threading, shlex, pathlib).
"""

from __future__ import annotations

import shlex
import subprocess
import threading
import os
from pathlib import Path

import pipeline  # provided by the same package via sys.path in server.py


RUNNING = "running"
SUCCEEDED = "succeeded"
FAILED = "failed"
CANCELLED = "cancelled"
IDLE = "idle"

Popen = subprocess.Popen  # tests may replace this with a fake


class Runner:
    """Run a list of pipeline ``Step`` objects in a daemon thread.

    All mutable state is guarded by ``self._lock`` so that a ``ThreadingHTTPServer``
    with concurrent handler threads can safely call ``snapshot`` / ``cancel`` /
    ``start`` at any time.
    """

    def __init__(self) -> None:
        self._lock = threading.Lock()
        self._lines: list[str] = []
        self._status: str = IDLE
        self._step: str = ""
        self._step_index: int = 0
        self._step_total: int = 0
        self._exit_code: int = 0
        self._proc: subprocess.Popen | None = None
        self._thread: threading.Thread | None = None

    # ------------------------------------------------------------------ helpers

    def _reset(self, total: int) -> None:
        self._lines = []
        self._status = RUNNING
        self._step = ""
        self._step_index = 0
        self._step_total = total
        self._exit_code = 0
        self._proc = None
        self._thread = None

    def _log(self, line: str) -> None:
        with self._lock:
            self._lines.append(line)

    def _set_status(self, status: str, exit_code: int = 0, step: str = "") -> None:
        with self._lock:
            self._status = status
            self._exit_code = exit_code
            self._step = step

    # -------------------------------------------------------------------- start

    def start(self, steps: list[pipeline.Step]) -> bool:
        """Spawn the worker daemon thread for ``steps``.  Refuses if busy."""
        with self._lock:
            # Cancellation changes the visible status immediately, but the worker
            # may still be draining and reaping its child.  Do not overlap it
            # with a new pipeline run.
            if self._status == RUNNING or (self._thread is not None and self._thread.is_alive()):
                return False
            self._reset(len(steps))
        thread = threading.Thread(target=self._run, args=(list(steps),), daemon=True)
        with self._lock:
            self._thread = thread
        thread.start()
        return True

    # ------------------------------------------------------------------ worker

    def _run(self, steps: list[pipeline.Step]) -> None:
        cancelled = False
        # Ensure the destination parent dir exists up front for the signed APK.
        try:
            if not _ensure_final_parent(steps):
                self._log("WARNING: could not pre-create the destination directory; the build may fail.")
        except Exception as exc:  # pragma: no cover - defensive
            self._log(f"WARNING: could not prepare output directory: {exc}")
        for index, step in enumerate(steps):
            step_num = index + 1
            total = len(steps)
            with self._lock:
                self._step_index = step_num
                self._step = step.name
            header = f"== [{step_num}/{total}] {step.name} =="
            cmd_line = "$ " + shlex.join(step.argv)
            self._log(header)
            self._log(cmd_line)

            # Check cancellation between steps.
            with self._lock:
                if self._status == CANCELLED:
                    cancelled = True
            if cancelled:
                break

            proc: subprocess.Popen | None = None
            try:
                popen_kwargs = {
                    "cwd": step.cwd,
                    "stdout": subprocess.PIPE,
                    "stderr": subprocess.STDOUT,
                    "text": True,
                    "bufsize": 1,
                    "errors": "replace",
                }
                if step.env:
                    popen_kwargs["env"] = {**os.environ, **step.env}
                proc = Popen(step.argv, **popen_kwargs)
            except (FileNotFoundError, OSError) as exc:
                reason = "file not found" if isinstance(exc, FileNotFoundError) else "OS error"
                self._log(f"ERROR: could not start step '{step.name}': {reason} ({exc})")
                self._set_status(FAILED, exit_code=127, step=step.name)
                return

            with self._lock:
                self._proc = proc
                if self._status == CANCELLED:
                    cancelled = True
            if cancelled:
                _terminate(proc)
                if proc.stdout is not None:
                    proc.stdout.close()
                with self._lock:
                    self._proc = None
                break

            assert proc.stdout is not None
            for raw_line in proc.stdout:
                self._log(raw_line.rstrip("\n"))
                with self._lock:
                    if self._status == CANCELLED:
                        cancelled = True
                if cancelled:
                    break
            proc.stdout.close()
            rc = proc.wait()

            with self._lock:
                self._proc = None

            if cancelled:
                break

            if rc != 0:
                self._log(f"STEP FAILED: '{step.name}' exited with code {rc}")
                self._set_status(FAILED, exit_code=rc, step=step.name)
                return

        if cancelled:
            self._log("Run cancelled by the user.")
            return
        # Success.  Publish the final log line and terminal state together so a
        # polling client cannot observe success before it receives that line.
        dest = _final_dest(steps)
        with self._lock:
            if self._status == CANCELLED:
                self._lines.append("Run cancelled by the user.")
                return
            if dest:
                self._lines.append(f"SUCCESS: finished signed APK at {dest}")
            else:
                self._lines.append("SUCCESS: all steps completed.")
            self._status = SUCCEEDED
            self._exit_code = 0
            self._step = ""

    # ---------------------------------------------------------------- snapshot

    def snapshot(self, offset: int) -> dict:
        """Return a JSON-serialisable view of the run from ``offset`` onward."""
        try:
            off = max(int(offset), 0)
        except (TypeError, ValueError):
            off = 0
        with self._lock:
            total = len(self._lines)
            if off > total:
                off = total
            chunk = self._lines[off:]
            next_offset = total
            return {
                "status": self._status,
                "lines": chunk,
                "next_offset": next_offset,
                "step": self._step,
                "step_index": self._step_index,
                "step_total": self._step_total,
                "exit_code": self._exit_code,
            }

    def join(self, timeout: float | None = None) -> bool:
        """Join the worker thread if running; return whether it finished."""
        with self._lock:
            thread = self._thread
        if thread is None:
            return True
        thread.join(timeout)
        return not thread.is_alive()

    # ------------------------------------------------------------------ cancel

    def cancel(self) -> None:
        """Terminate the running subprocess (if any) and mark the run cancelled."""
        with self._lock:
            if self._status != RUNNING:
                return
            self._status = CANCELLED
            proc = self._proc
        _terminate(proc)


# ---------------------------------------------------------------------- helpers


def _terminate(proc: subprocess.Popen | None) -> None:
    """Best-effort terminate then kill, bounded to a short wait."""
    if proc is None:
        return
    try:
        proc.terminate()
        try:
            proc.wait(timeout=2)
            return
        except subprocess.TimeoutExpired:
            pass
        proc.kill()
        try:
            proc.wait(timeout=2)
        except subprocess.TimeoutExpired:  # pragma: no cover - defensive
            pass
    except Exception:  # pragma: no cover - must not raise out of cancel
        pass


def _final_dest(steps: list[pipeline.Step]) -> str:
    """Best-effort guess at the destination APK path from the plan."""
    for step in reversed(steps):
        if step.name == "sign APK" and len(step.argv) >= 2:
            argv = step.argv
            if "--out" in argv:
                idx = argv.index("--out")
                if idx + 1 < len(argv):
                    return argv[idx + 1]
    return ""


def _ensure_final_parent(steps: list[pipeline.Step]) -> bool:
    """Create the parent directory of the signed APK before it is written."""
    dest = _final_dest(steps)
    if not dest:
        return True
    try:
        Path(dest).parent.mkdir(parents=True, exist_ok=True)
        return True
    except OSError:
        return False
