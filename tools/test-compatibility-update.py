"""Offline regeneration contracts using synthetic metadata and a mocked Git identity."""
import contextlib
import hashlib
import importlib.util
import io
import json
import subprocess
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

    def use_canonical_syntax(self):
        # Reconstruct synthetic input from this repository's extracted syntax.
        # Reviewed status/evidence comes only from the separately supplied output.
        tables = {context["name"]: {"table": context["name"]} for context in self.canonical["contexts"]}
        choices = {}
        for index, row in enumerate(self.canonical["options"]):
            group = row["argumentKind"]
            if group == "manual":
                continue
            table = tables[row["context"]]
            name = row["flag"][2:]
            if group == "bare":
                table.setdefault(group, []).append(name)
            else:
                parameter = "parameter" + str(index)
                table.setdefault(group, {})[name] = parameter
                if group.endswith("choices"):
                    choices[parameter] = row["choices"]
        job = {"options": list(tables.values()), "choices": choices, "json": {}}
        (self.reference / "job.yml").write_text(json.dumps(job), encoding="utf-8")

    def test_initial_generation_keeps_companion_requirements(self):
        result = self.generate(inventory.COMMIT)
        self.assertEqual(["--json-key=encrypt must report non-secret encryption facts", "fix-qdf companion", "zlib-flate companion"],
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

    def test_numeric_json_key_segments_are_camel_case(self):
        job = {"options": [{"table": "main", "bare": ["warning-exit-0"]}],
               "choices": {}, "json": {"warning-exit-0": ""}}
        (self.reference / "job.yml").write_text(json.dumps(job), encoding="utf-8")
        result = self.generate(inventory.COMMIT)
        row = next(row for row in result["options"] if row["flag"] == "--warning-exit-0")
        self.assertEqual(["warningExit0"], row["jobJsonPaths"])

    def test_current_security_reviews_survive_regeneration(self):
        self.use_canonical_syntax()
        self.write(self.canonical)
        result = self.generate(inventory.COMMIT)
        expected = {(r["context"], r["flag"]): r for r in self.canonical["options"]}
        fields = ("scope", "choiceScopes", "status", "defaultReview", "remainingGaps",
                  "evidence", "cryptoSyntax", "exclusionReason")
        for row in result["options"]:
            with self.subTest(context=row["context"], flag=row["flag"]):
                old = expected[row["context"], row["flag"]]
                self.assertEqual({k: old[k] for k in fields if k in old},
                                 {k: row[k] for k in fields if k in row})
                self.assertNotIn("currentRejection", row)
        self.assertEqual({r["status"] for r in self.canonical["options"]},
                         {r["status"] for r in result["options"]})
        self.assertEqual(result, self.generate(inventory.COMMIT))

    def test_initial_security_classification_does_not_claim_rejection(self):
        self.use_canonical_syntax()
        result = self.generate(inventory.COMMIT)
        expected = {(r["context"], r["flag"]): r for r in self.canonical["options"]}
        for row in result["options"]:
            old = expected[row["context"], row["flag"]]
            self.assertEqual(old.get("cryptoSyntax"), row.get("cryptoSyntax"))
            self.assertNotIn("currentRejection", row)
            if row["scope"] == "required":
                self.assertEqual("not-started", row["status"])
                self.assertEqual([inventory.UNREVIEWED_GAP], row["remainingGaps"])
        self.assertTrue(next(r for r in result["options"] if r["flag"] == "--encrypt")["cryptoSyntax"])
        self.assertFalse(next(r for r in result["options"] if r["flag"] == "--show-encryption")["cryptoSyntax"])

    def test_legacy_rejection_markers_retire_without_losing_reviews(self):
        self.use_canonical_syntax()
        legacy = json.loads(json.dumps(self.canonical))
        for row in legacy["options"]:
            if "cryptoSyntax" in row:
                del row["cryptoSyntax"]
                row["currentRejection"] = True
        self.write(legacy)
        result = self.generate(inventory.COMMIT)
        expected = {(r["context"], r["flag"]): r for r in self.canonical["options"]}
        for row in result["options"]:
            old = expected[row["context"], row["flag"]]
            self.assertEqual(old.get("cryptoSyntax"), row.get("cryptoSyntax"))
            self.assertEqual(old["status"], row["status"])
            self.assertEqual(old["remainingGaps"], row["remainingGaps"])
            self.assertEqual(old["evidence"], row["evidence"])
            self.assertNotIn("currentRejection", row)
        self.assertEqual(result, self.generate(inventory.COMMIT))

    def test_source_hashes_ignore_checkout_newlines_but_preserve_content(self):
        sources = {name: (self.reference / name).read_bytes().replace(b"\r\n", b"\n")
                   for name in ("job.yml", "manual/cli.rst")}
        for name, data in sources.items():
            (self.reference / name).write_bytes(data)
        lf = self.generate(inventory.COMMIT)
        for name, data in sources.items():
            (self.reference / name).write_bytes(data.replace(b"\n", b"\r\n"))
        crlf = self.generate(inventory.COMMIT)
        self.assertEqual(lf, crlf)
        self.assertEqual({name: hashlib.sha256(data).hexdigest() for name, data in sources.items()},
                         lf["reference"]["sources"])
        (self.reference / "manual/cli.rst").write_bytes(sources["manual/cli.rst"] + b"\n.. hash control\n")
        changed = self.generate(inventory.COMMIT)
        self.assertNotEqual(lf["reference"]["sources"]["manual/cli.rst"],
                            changed["reference"]["sources"]["manual/cli.rst"])

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

    def test_edited_reference_preserves_reviewed_file(self):
        self.generate(inventory.COMMIT)
        before = self.output.read_bytes()
        argv = ["update-compatibility.py", "--reference", str(self.reference), "--output", str(self.output)]
        with patch.object(sys, "argv", argv), \
                patch.object(inventory.subprocess, "check_output", return_value=inventory.COMMIT), \
                patch.object(inventory.subprocess, "run", side_effect=subprocess.CalledProcessError(1, "git diff")):
            with self.assertRaises(subprocess.CalledProcessError):
                inventory.main()
        self.assertEqual(before, self.output.read_bytes())


if __name__ == "__main__":
    unittest.main()
