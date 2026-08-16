"""Fast subprocess tests for the APK patcher background runner."""

from __future__ import annotations

import sys
import time
import unittest

import pipeline
from runner import Runner


def fake_step(name: str, program: str) -> pipeline.Step:
    """Make a Step backed by the current Python interpreter, never the builder."""
    return pipeline.Step(name, [sys.executable, "-u", "-c", program])


class RunnerTests(unittest.TestCase):
    def wait_for_completion(self, runner: Runner) -> dict:
        """Bounded wait that gives a useful test failure if a child hangs."""
        self.assertTrue(runner.join(5), "runner did not finish within five seconds")
        snapshot = runner.snapshot(0)
        self.assertNotEqual(snapshot["status"], "running")
        return snapshot

    def test_successful_steps_stream_incrementally(self) -> None:
        runner = Runner()
        steps = [
            fake_step("first", "import time; print('first output'); time.sleep(0.15); print('first done')"),
            fake_step("second", "print('second output')"),
        ]
        self.assertTrue(runner.start(steps))

        # Poll while the worker runs so the second snapshot has a non-zero
        # offset, just as the browser does.
        first = runner.snapshot(0)
        deadline = time.monotonic() + 2
        while not first["lines"] and time.monotonic() < deadline:
            time.sleep(0.01)
            first = runner.snapshot(0)
        self.assertTrue(first["lines"])

        finished = self.wait_for_completion(runner)
        second = runner.snapshot(first["next_offset"])
        self.assertEqual(first["lines"] + second["lines"], finished["lines"])
        self.assertEqual(second["next_offset"], len(finished["lines"]))
        self.assertEqual(finished["status"], "succeeded")
        text = "\n".join(finished["lines"])
        self.assertIn("first output", text)
        self.assertIn("first done", text)
        self.assertIn("second output", text)

    def test_failing_step_stops_later_steps(self) -> None:
        runner = Runner()
        steps = [
            fake_step("fails", "import sys; print('failure details', file=sys.stderr); raise SystemExit(3)"),
            fake_step("must not run", "print('LATER STEP RAN')"),
        ]
        self.assertTrue(runner.start(steps))
        finished = self.wait_for_completion(runner)

        self.assertEqual(finished["status"], "failed")
        self.assertEqual(finished["step"], "fails")
        self.assertEqual(finished["exit_code"], 3)
        text = "\n".join(finished["lines"])
        self.assertIn("failure details", text)
        self.assertIn("STEP FAILED: 'fails' exited with code 3", text)
        self.assertNotIn("LATER STEP RAN", text)
        self.assertNotIn("must not run ==", text)

    def test_start_is_refused_while_run_is_active(self) -> None:
        runner = Runner()
        self.assertTrue(runner.start([fake_step("slow", "import time; time.sleep(1)")]))
        self.assertFalse(runner.start([fake_step("other", "print('should not start')")]))
        runner.cancel()
        self.wait_for_completion(runner)

    def test_missing_command_fails_cleanly(self) -> None:
        runner = Runner()
        missing = "definitely-not-a-real-apk-patcher-command"
        self.assertTrue(runner.start([pipeline.Step("missing tool", [missing])]))
        finished = self.wait_for_completion(runner)

        self.assertEqual(finished["status"], "failed")
        self.assertEqual(finished["step"], "missing tool")
        self.assertEqual(finished["exit_code"], 127)
        self.assertIn("could not start step 'missing tool'", "\n".join(finished["lines"]))


if __name__ == "__main__":
    unittest.main()
