"""Extract public option metadata; never import upstream implementation code."""

import argparse
import hashlib
import json
import re
import subprocess
from pathlib import Path

import yaml


VERSION = "12.4.2"
COMMIT = "4eba95899886e851cc41d76886483b347612f2a8"
CRYPTO = set("""
encrypt decrypt copy-encryption password password-file password-mode
password-is-hex-key suppress-password-recovery encryption-file-password
user-password owner-password bits allow-weak-crypto allow-insecure use-aes
force-V4 force-R5 cleartext-metadata static-aes-iv accessibility annotate
assemble extract form modify-other modify print show-crypto show-encryption
show-encryption-key is-encrypted requires-password remove-restrictions
""".split())
GROUPS = (
    "bare", "optional_parameter", "file_parameter", "required_parameter",
    "required_choices", "optional_choices",
)
MILESTONES = {
    "M5": "collate split-pages rotate replace-input set-page-labels",
    "M6": """compress-streams decode-level stream-data recompress-flate
        compression-level zopfli object-streams preserve-unreferenced
        newline-before-endstream min-version force-version deterministic-id
        static-id remove-info remove-metadata remove-page-labels remove-structure
        list-attachments show-attachment add-attachment copy-attachments-from
        remove-attachment key filename creationdate moddate mimetype description
        replace prefix json json-key json-object json-stream-data json-stream-prefix
        json-output json-input update-from-json test-json-schema json-help""",
    "M7": """pages file range overlay underlay to from repeat flatten-rotation
        flatten-annotations generate-appearances remove-acroform normalize-content
        coalesce-contents externalize-inline-images ii-min-bytes
        remove-unreferenced-resources preserve-unreferenced-resources""",
    "M8": "optimize-images oi-min-width oi-min-height oi-min-area keep-inline-images jpeg-quality",
    "M9": "qdf no-original-object-ids",
    "M10": "check linearize check-linearization show-linearization linearize-pass1",
}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--reference", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    reference = args.reference.resolve()
    actual_commit = subprocess.check_output(
        ["git", "-C", str(reference), "rev-parse", "HEAD"], text=True
    ).strip()
    if actual_commit != COMMIT:
        parser.error(f"Expected upstream commit {COMMIT}, got {actual_commit}")
    subprocess.run(
        ["git", "-C", str(reference), "diff", "--exit-code", "HEAD", "--", "job.yml", "manual/cli.rst"],
        check=True, capture_output=True,
    )
    job_bytes = (reference / "job.yml").read_bytes()
    manual_bytes = (reference / "manual/cli.rst").read_bytes()
    job = yaml.safe_load(job_bytes)
    manual = manual_bytes.decode("utf-8")
    forms = {}
    for number, line in enumerate(manual.splitlines(), 1):
        match = re.match(r"\.\. qpdf:option:: (--[A-Za-z0-9-]+)(.*)", line)
        if match:
            forms.setdefault(match[1], []).append({
                "syntax": match[1] + match[2], "source": f"manual/cli.rst:{number}",
            })
    if len(forms) != 140:
        parser.error(f"Expected 140 documented flags, got {len(forms)}")

    rows = []
    prefixes = {}
    contexts = []
    for table in job["options"]:
        context = table["table"]
        prefixes[table.get("prefix", context)] = context
        contexts.append({
            "name": context, "triggers": table.get("triggers", []),
            "positional": table.get("positional", False),
        })
        for group in GROUPS:
            options = table.get(group, {})
            for name in options:
                parameter = options[name] if isinstance(options, dict) else None
                choices = [str(choice) for choice in job["choices"][parameter]] if group.endswith("choices") else []
                rows.append({
                    "context": context, "flag": "--" + name,
                    "argumentKind": group, "parameter": parameter,
                    "choices": choices, "jobJsonPaths": [],
                })
    known_flags = {row["flag"] for row in rows}
    for flag in sorted(set(forms) - known_flags):
        rows.append({
            "context": "parser", "flag": flag, "argumentKind": "manual",
            "parameter": None, "choices": [], "jobJsonPaths": [],
        })
    if known_flags - set(forms):
        parser.error(f"Undocumented job flags need classification: {sorted(known_flags - set(forms))}")

    # job.yml defines camelCase JSON keys; prefixes qualify ambiguous CLI contexts.
    # Only metadata is retained. Help strings and upstream generated code are not copied.
    def visit_json(value, path):
        if isinstance(value, list):
            for child in value:
                visit_json(child, path + "[]")
        elif isinstance(value, dict):
            for raw, child in value.items():
                if raw.startswith("__"):
                    continue
                name = raw.split(".")[-1].lstrip("_")
                key = re.sub(r"-([A-Za-z])", lambda match: match[1].upper(), name)
                child_path = f"{path}.{key}" if path else key
                if not raw.startswith("_"):
                    prefix = raw.split(".")[0] if "." in raw else None
                    for row in rows:
                        if row["flag"] == "--" + name and (prefix is None or row["context"] == prefixes[prefix]):
                            row["jobJsonPaths"].append(child_path)
                visit_json(child, child_path)

    visit_json(job["json"], "")
    previous = {}
    if args.output.exists():
        old = json.loads(args.output.read_text(encoding="utf-8"))
        if old["reference"]["commit"] != COMMIT:
            parser.error("A reference-version migration requires explicit review")
        previous = {(row["context"], row["flag"]): row for row in old["options"]}

    for row in rows:
        name = row["flag"][2:]
        crypto = name in CRYPTO or "encryption" in row["context"]
        row["scope"] = "excluded-crypto" if crypto else (
            "documented-host/build-difference" if name == "zopfli" else "required")
        row["choiceScopes"] = {choice: (
            "excluded-crypto" if name == "json-key" and choice == "encrypt" else row["scope"]
        ) for choice in row["choices"]}
        row["manualForms"] = forms[row["flag"]]
        row["milestone"] = "excluded" if crypto else next(
            (milestone for milestone, names in MILESTONES.items() if name in names.split()), "M4")
        row["status"] = "not-started"
        row["defaultReview"] = "pending"
        row["remainingGaps"] = ["Runtime rejection not implemented." if crypto else "Behavior review and implementation pending."]
        row["evidence"] = {kind: [] for kind in ("positive", "negative", "interaction", "resource", "oracle")}
        row["jobJsonPaths"] = sorted(set(row["jobJsonPaths"]))
        old = previous.get((row["context"], row["flag"]), {})
        for field in ("status", "milestone", "defaultReview", "remainingGaps", "evidence"):
            if field in old:
                row[field] = old[field]

    result = {
        "schemaVersion": 1,
        "reference": {
            "repository": "https://github.com/qpdf/qpdf", "version": VERSION,
            "commit": COMMIT, "documentedFlagCount": len(forms),
            "sources": {"job.yml": hashlib.sha256(job_bytes).hexdigest(),
                        "manual/cli.rst": hashlib.sha256(manual_bytes).hexdigest()},
        },
        "contexts": contexts,
        "parserForms": ["input/output operands", "@file", "@-", "--", "page-range syntax"],
        "supplementalRequirements": ["--json-key=encrypt is excluded", "fix-qdf companion", "zlib-flate companion"],
        "options": sorted(rows, key=lambda row: (row["context"], row["flag"])),
    }
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(result, indent=2, ensure_ascii=False) + "\n", encoding="utf-8", newline="\n")
    print(f"Recorded {len(forms)} flags in {len(rows)} option/context entries.")


if __name__ == "__main__":
    main()
