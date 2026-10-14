"""Explicit local checks of benchmark semantics and historical false passes."""
import argparse
import importlib.util
import json
import os
from pathlib import Path
import shutil
import subprocess
from unittest import mock


def require_rejection(action, name):
    try:
        action()
    except ValueError:
        return
    raise AssertionError("Validator accepted negative control: " + name)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ("repo", "dotnet", "probe", "qpdf", "input-root", "work-root"):
        parser.add_argument("--" + name, type=Path, required=True)
    parser.add_argument("--file-input", action="store_true", help="Validate the comparable file-backed argv lane")
    args = parser.parse_args()
    repo, dotnet, probe, qpdf, inputs, root = (
        getattr(args, name).resolve() for name in ("repo", "dotnet", "probe", "qpdf", "input_root", "work_root"))
    spec = importlib.util.spec_from_file_location("comparison", repo / "tools/compare-linux-performance.py")
    comparison = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(comparison)
    if comparison.run([qpdf, "--version"]).decode().splitlines()[0] != "qpdf version 12.4.2":
        raise ValueError("Require pinned native qpdf 12.4.2")
    root.mkdir(parents=True, exist_ok=False)
    shutil.copytree(inputs / "inputs", root / "inputs")
    cases = json.loads((inputs / "workloads.json").read_text())
    sources = {p.name: comparison.fingerprints(qpdf, p) for p in (root / "inputs").glob("*.pdf")}
    hashes = {p.name: comparison.sha(p) for p in (root / "inputs").glob("*.pdf")}
    os.chdir(root)
    accepted = []

    def command(case, engine, directory):
        argv = comparison.commands(case, engine, directory, root, dotnet, probe, qpdf)
        if args.file_input and engine == "managed":
            argv.insert(3, "--file-input")
        return argv

    for case in cases:
        for engine in ("native", "managed"):
            directory = root / case["Name"] / engine / "outputs"
            directory.mkdir(parents=True)
            output = comparison.run(command(case, engine, directory))
            if engine == "managed" and b"emit: status=Completed exit=0" not in output:
                raise AssertionError("Managed validation job did not succeed")
            if case["Name"].startswith("linearize-"):
                (directory.parent / "inputs").mkdir()
                shutil.copyfile(directory / "out.pdf", directory.parent / "inputs/linearized.pdf")
            comparison.validate(case, directory, sources, dotnet, probe, qpdf, root / "inputs")
            accepted.append(case["Name"] + ":" + engine)
            if case["Name"] == "rewrite-ID-M500":
                repeated = directory.parent / "repeated"
                repeated.mkdir()
                comparison.run(command(case, engine, repeated))
                if comparison.output_identity(case, repeated) != comparison.output_identity(case, directory):
                    raise AssertionError("Content-derived IDs/output are not repeatable: " + engine)
    check = next(c for c in cases if c["Name"] == "check-X100")
    wrong = dict(check)
    wrong["ExpectedMarkers"] = ["deliberately wrong"]
    require_rejection(lambda: comparison.validate(wrong, root / "check-X100/native/outputs",
        sources, dotnet, probe, qpdf, root / "inputs"), "outputless wrong expectation")
    obj = next(c for c in cases if c["Name"] == "object-streams-X100")
    marker = root / "marker-only.pdf"
    original = (root / "inputs" / obj["Inputs"][0]).read_bytes()
    if b"/ObjStm" in original:
        raise ValueError("Marker control requires the original classic input")
    marker.write_bytes(original + b"\n% /ObjStm\n")
    require_rejection(lambda: comparison.validate_object_streams(qpdf, marker), "marker-only classic PDF")
    execute = subprocess.run

    def no_check(command, **kwargs):
        if command[0] == str(dotnet) and "--check" in command:
            return subprocess.CompletedProcess(command, 0, b"emit: status=Completed exit=0\n", b"")
        return execute(command, **kwargs)

    with mock.patch.object(comparison.subprocess, "run", side_effect=no_check):
        require_rejection(lambda: comparison.checker_preflight(root / "noop-control", dotnet, probe, qpdf),
            "no-op managed checker")
    if any(comparison.sha(root / "inputs" / name) != identity for name, identity in hashes.items()):
        raise AssertionError("Original input changed")
    report = {"inputDelivery": "file-backed" if args.file_input else "memory-host", "accepted": accepted, "negativeControlsRejected": ["outputless wrong expectation",
        "marker-only classic PDF", "no-op managed checker"], "inputs": hashes}
    (root / "validation-results.json").write_text(json.dumps(report, indent=2) + "\n")
    print(f"Passed {len(accepted)} fresh outcomes and three negative controls; inputs unchanged.")


if __name__ == "__main__":
    main()
