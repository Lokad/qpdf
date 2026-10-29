"""Offline regeneration contracts using synthetic metadata and a mocked Git identity."""
import contextlib
import importlib.util
import io
import json
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location("inventory", Path(__file__).with_name("update-compatibility.py"))
inventory = importlib.util.module_from_spec(spec)
spec.loader.exec_module(inventory)
ROOT = Path(__file__).resolve().parent.parent


class CompatibilityUpdateTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.reference = Path(temporary.name) / "reference"
        (self.reference / "manual").mkdir(parents=True)
        self.output = Path(temporary.name) / "options.json"
        self.canonical = json.loads((ROOT / "tests/Compatibility/options.json").read_text(encoding="utf-8"))
        flags = sorted({row["flag"] for row in self.canonical["options"]})
        (self.reference / "manual/cli.rst").write_text(
            "\n".join(".. qpdf:option:: " + flag for flag in flags), encoding="utf-8")
        # JSON is a YAML subset: exercise the real pinned YAML reader without
        # copying upstream metadata or requiring an upstream checkout.
        job = {"options": [{"table": "main", "bare": ["show-npages"]}],
               "choices": {}, "json": {"show-npages": ""}}
        (self.reference / "job.yml").write_text(json.dumps(job), encoding="utf-8")

    def generate(self, commit):
        argv = ["update-compatibility.py", "--reference", str(self.reference), "--output", str(self.output)]
        with patch.object(sys, "argv", argv), \
                patch.object(inventory.subprocess, "check_output", return_value=commit), \
                patch.object(inventory.subprocess, "run"), \
                contextlib.redirect_stdout(io.StringIO()), contextlib.redirect_stderr(io.StringIO()):
            inventory.main()
        return json.loads(self.output.read_text(encoding="utf-8"))

    def write(self, value):
        self.output.write_text(json.dumps(value, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")

    def test_initial_generation_keeps_companion_requirements(self):
        result = self.generate(inventory.COMMIT)
        self.assertEqual(["--json-key=encrypt is excluded", "fix-qdf companion", "zlib-flate companion"],
                         result["supplementalRequirements"])
        self.assertEqual(140, len(result["options"]))
        self.assertEqual("12.4.2", result["reference"]["version"])

    def test_regeneration_preserves_reviewed_metadata_and_is_idempotent(self):
        reviewed = self.generate(inventory.COMMIT)
        reviewed["supplementalRequirements"] = self.canonical["supplementalRequirements"]
        row = next(row for row in reviewed["options"] if row["flag"] == "--show-npages")
        row.update(status="complete", defaultReview="reviewed", remainingGaps=[],
                   evidence={"positive": ["ReviewedFixture"], "negative": ["RejectedFixture"]})
        self.write(reviewed)
        regenerated = self.generate(inventory.COMMIT)
        self.assertEqual(reviewed["supplementalRequirements"], regenerated["supplementalRequirements"])
        self.assertEqual(reviewed, regenerated)
        before = self.output.read_bytes()
        self.generate(inventory.COMMIT)
        self.assertEqual(before, self.output.read_bytes())

    def test_explicit_empty_requirements_remain_empty(self):
        reviewed = self.generate(inventory.COMMIT)
        reviewed["supplementalRequirements"] = []
        self.write(reviewed)
        self.assertEqual([], self.generate(inventory.COMMIT)["supplementalRequirements"])

    def test_older_inventory_without_requirements_uses_defaults(self):
        reviewed = self.generate(inventory.COMMIT)
        expected = reviewed.pop("supplementalRequirements")
        self.write(reviewed)
        self.assertEqual(expected, self.generate(inventory.COMMIT)["supplementalRequirements"])

    def test_reference_version_migration_preserves_reviewed_file(self):
        reviewed = self.generate(inventory.COMMIT)
        reviewed["reference"]["commit"] = "different-reference"
        self.write(reviewed)
        before = self.output.read_bytes()
        with self.assertRaises(SystemExit):
            self.generate(inventory.COMMIT)
        self.assertEqual(before, self.output.read_bytes())

    def test_wrong_checkout_preserves_reviewed_file(self):
        self.generate(inventory.COMMIT)
        before = self.output.read_bytes()
        with self.assertRaises(SystemExit):
            self.generate("wrong-checkout")
        self.assertEqual(before, self.output.read_bytes())


if __name__ == "__main__":
    unittest.main()
