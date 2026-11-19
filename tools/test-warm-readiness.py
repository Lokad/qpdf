"""Deterministic checks for benchmark preparation and bounded warmup."""
from contextlib import redirect_stdout
import importlib.util
import io
from pathlib import Path
import sys
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location("warm", Path(__file__).with_name("compare-warm-performance.py"))
warm = importlib.util.module_from_spec(spec)
spec.loader.exec_module(warm)


class WarmReadinessTests(unittest.TestCase):
    def test_preparation_only_uses_the_pinned_fixture_generator(self):
        comparison = warm.comparison
        repo = Path.cwd()
        oracle = repo / "qpdf"
        dotnet = repo / "dotnet"
        output = repo / "prepared"
        version = f"qpdf version {comparison.REFERENCE_PIN['version']}"
        args = ["compare", "--repo", str(repo), "--qpdf", str(oracle), "--dotnet", str(dotnet),
                "--work-root", str(output), "--prepare-only"]
        with redirect_stdout(io.StringIO()), patch.object(sys, "argv", args), \
                patch.object(comparison, "run", return_value=(version + "\r\n").encode()) as run, \
                patch.object(comparison, "prepare_inputs") as prepare, \
                patch.object(comparison.platform, "system", side_effect=AssertionError("Measurement platform was checked")):
            comparison.main()
        run.assert_called_once_with([oracle, "--version"])
        prepare.assert_called_once_with(dotnet,
            repo / "benchmarks/Lokad.Qpdf.Benchmarks/bin/Release/net11.0/Lokad.Qpdf.Benchmarks.dll",
            oracle, output)

    def test_native_version_rejects_unqualified_and_missing_banners(self):
        comparison = warm.comparison
        pin = comparison.REFERENCE_PIN["version"]
        for banner in ("", f"qpdf version {pin}.1", f"reference\nqpdf version {pin}", f"\nqpdf version {pin}"):
            with self.subTest(banner=banner), patch.object(comparison, "run", return_value=banner.encode()):
                with self.assertRaisesRegex(ValueError, "Expected pinned native qpdf"):
                    comparison.check_native_version("qpdf")

    def run_warmup(self, counters, managed=True):
        current = [0.0]
        def job():
            current[0] += 0.1
            return counters(current[0])
        with patch.object(warm.time, "monotonic", side_effect=lambda: current[0]):
            return warm.warm_up(job, 2.0, managed)

    def test_native_needs_no_managed_counters(self):
        result = self.run_warmup(lambda _: {}, managed=False)
        self.assertGreaterEqual(result["jobs"], 30)
        self.assertGreaterEqual(result["wallSeconds"], 2.0)
        self.assertIsNone(result["jitQuietSeconds"])

    def test_managed_waits_after_last_compilation(self):
        result = self.run_warmup(lambda now: {"jitCompiledMethods": int(min(now, 4.0) * 10),
            "jitCompilationMilliseconds": min(now, 4.0) * 10})
        self.assertGreaterEqual(result["wallSeconds"], 6.0)
        self.assertGreaterEqual(result["jitQuietSeconds"], 2.0)
        self.assertEqual(result["jitCompilationMilliseconds"], 40.0)

    def test_compilation_time_alone_prevents_false_readiness(self):
        result = self.run_warmup(lambda now: {"jitCompiledMethods": 100,
            "jitCompilationMilliseconds": min(now, 4.0) * 10})
        self.assertGreaterEqual(result["wallSeconds"], 6.0)

    def test_unsettled_compilation_fails_within_bound(self):
        with self.assertRaisesRegex(RuntimeError, "bounded warmup"):
            self.run_warmup(lambda now: {"jitCompiledMethods": int(now * 10),
                "jitCompilationMilliseconds": now * 10})

    def test_old_managed_driver_is_rejected(self):
        with self.assertRaisesRegex(ValueError, "Rebuild"):
            self.run_warmup(lambda _: {})

    def test_backwards_counters_are_rejected(self):
        with self.assertRaisesRegex(ValueError, "backwards"):
            self.run_warmup(lambda now: {"jitCompiledMethods": 100 - int(now),
                "jitCompilationMilliseconds": 100 - now})


if __name__ == "__main__":
    unittest.main()
