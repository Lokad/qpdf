"""Controlled process checks for the warmed benchmark driver's worker ownership."""
from contextlib import ExitStack, redirect_stderr
import importlib.util
import io
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location("warm", Path(__file__).with_name("compare-warm-performance.py"))
warm = importlib.util.module_from_spec(spec)
spec.loader.exec_module(warm)


class WorkerOwnershipTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)
        self.workers = []

    def worker(self, code):
        worker = warm.Worker([sys.executable, "-u", "-c",
            "import sys; print('ready', flush=True); " + code], self.root,
            self.root / (str(len(self.workers)) + ".log"))
        self.workers.append(worker)
        self.addCleanup(self.retire, worker)
        self.assertEqual(worker.process.stdout.readline(), "ready\n")
        return worker

    def retire(self, worker):
        # Always retire this test's child, even if a regression breaks production cleanup.
        if worker.process.poll() is None:
            worker.process.kill()
        worker.process.wait(timeout=5)
        worker.process.stdin.close()
        worker.process.stdout.close()
        worker.log.close()

    def assert_closed(self, worker):
        self.assertIsNotNone(worker.process.poll())
        self.assertTrue(worker.process.stdin.closed)
        self.assertTrue(worker.process.stdout.closed)
        self.assertTrue(worker.log.closed)

    def test_normal_eof_waits_for_child_and_closes_streams(self):
        with self.worker("sys.stdin.read()") as worker:
            self.assertIsNone(worker.process.poll())
        self.assertEqual(worker.process.returncode, 0)
        self.assert_closed(worker)

    def test_nonzero_exit_closes_streams_and_retains_stderr(self):
        with self.assertRaisesRegex(RuntimeError, "Worker exited 7"):
            with self.worker("sys.stdin.read(); print('failure evidence', file=sys.stderr); sys.exit(7)") as worker:
                pass
        self.assert_closed(worker)
        self.assertIn("failure evidence", Path(worker.log.name).read_text())

    def test_shutdown_timeout_kills_and_awaits_child(self):
        worker = self.worker("import time; time.sleep(60)")
        real_wait = worker.process.wait
        def short_wait(*args, **kwargs):
            if "timeout" in kwargs:
                kwargs["timeout"] = 0.1
            return real_wait(*args, **kwargs)
        with patch.object(worker.process, "wait", side_effect=short_wait):
            with self.assertRaises(subprocess.TimeoutExpired):
                worker.close()
        self.assert_closed(worker)

    def test_failed_start_closes_log_and_preserves_exception(self):
        for error in (FileNotFoundError("missing worker"), KeyboardInterrupt()):
            with self.subTest(error=type(error).__name__):
                worker = warm.Worker.__new__(warm.Worker)
                with patch.object(warm.subprocess, "Popen", side_effect=error):
                    with self.assertRaises(type(error)) as raised:
                        worker.__init__(["missing"], self.root, self.root / "failed.log")
                self.addCleanup(worker.log.close)
                self.assertIs(raised.exception, error)
                self.assertTrue(worker.log.closed)

    def test_partial_startup_closes_already_owned_worker(self):
        with self.assertRaises(FileNotFoundError):
            with ExitStack() as owned:
                worker = owned.enter_context(self.worker("sys.stdin.read()"))
                owned.enter_context(warm.Worker([str(self.root / "missing-executable")],
                    self.root, self.root / "missing.log"))
        self.assert_closed(worker)

    def test_cleanup_failure_does_not_skip_other_workers(self):
        with self.assertRaisesRegex(RuntimeError, "Worker exited 7"):
            with ExitStack() as owned:
                first = owned.enter_context(self.worker("sys.stdin.read()"))
                second = owned.enter_context(self.worker("sys.stdin.read(); sys.exit(7)"))
        self.assert_closed(first)
        self.assert_closed(second)

    def test_multiple_cleanup_failures_keep_first_error_and_report_later_errors(self):
        diagnostics = io.StringIO()
        with redirect_stderr(diagnostics):
            with self.assertRaisesRegex(RuntimeError, "Worker exited 7"):
                with ExitStack() as owned:
                    first = owned.enter_context(self.worker("sys.stdin.read(); sys.exit(3)"))
                    second = owned.enter_context(self.worker("sys.stdin.read(); sys.exit(7)"))
        self.assert_closed(first)
        self.assert_closed(second)
        self.assertIn("Worker exited 3", diagnostics.getvalue())

    def test_comparison_failure_or_interruption_survives_cleanup_failure(self):
        for error in (ValueError("comparison rejected"), KeyboardInterrupt()):
            with self.subTest(error=type(error).__name__):
                diagnostics = io.StringIO()
                with redirect_stderr(diagnostics):
                    with self.assertRaises(type(error)) as raised:
                        with self.worker("sys.stdin.read(); sys.exit(7)") as worker:
                            raise error
                self.assertIs(raised.exception, error)
                self.assert_closed(worker)
                self.assertIn("Worker exited 7", diagnostics.getvalue())


if __name__ == "__main__":
    unittest.main()
