"""Sequential warmed complete-job comparison; select one workload for short probes."""
import argparse
import importlib.util
import json
import os
from pathlib import Path
import shutil
import select
import statistics
import subprocess
import time

spec = importlib.util.spec_from_file_location("linux_comparison", Path(__file__).with_name("compare-linux-performance.py"))
comparison = importlib.util.module_from_spec(spec)
spec.loader.exec_module(comparison)
PIN, commands, fingerprints, output_identity, run, sha, validate = (
    getattr(comparison, name) for name in ("PIN", "commands", "fingerprints", "output_identity", "run", "sha", "validate"))


class Worker:
    def __init__(self, command, cwd, log):
        self.log = log.open("wb")
        self.process = subprocess.Popen(command, cwd=cwd, stdin=subprocess.PIPE,
            stdout=subprocess.PIPE, stderr=self.log, text=True, bufsize=1)

    def job(self, arguments):
        if any("\n" in a or "\r" in a for a in arguments):
            raise ValueError("Line protocol cannot carry newline arguments")
        self.process.stdin.write(str(len(arguments)) + "\n" + "\n".join(arguments) + "\n")
        self.process.stdin.flush()
        if not select.select([self.process.stdout], [], [], 180)[0]:
            self.process.kill()
            raise TimeoutError("Warm job exceeded 180 seconds")
        line = self.process.stdout.readline()
        if not line.startswith("WARM "):
            raise RuntimeError("Warm worker failed; inspect stderr: " + line)
        sample = json.loads(line[5:])
        if sample["exitCode"] != 0:
            raise RuntimeError("Warm job failed: " + str(sample))
        status = Path(f"/proc/{self.process.pid}/status").read_text()
        sample["processPeakRssKiB"] = int(next(l for l in status.splitlines() if l.startswith("VmHWM:")).split()[1])
        return sample

    def close(self):
        try:
            self.process.stdin.close()
        except BrokenPipeError:
            pass
        self.process.wait(timeout=15)
        self.log.close()
        if self.process.returncode:
            raise RuntimeError("Worker exited " + str(self.process.returncode))


def describe(values):
    quartiles = statistics.quantiles(values, n=4, method="inclusive")
    return {"median": statistics.median(values), "q1": quartiles[0], "q3": quartiles[2],
        "min": min(values), "max": max(values)}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ("repo", "dotnet", "qpdf", "native-source", "native-build", "input-root", "work-root"):
        parser.add_argument("--" + name, required=True, type=Path)
    parser.add_argument("--workload", action="append", help="Selected existing manifest names; default all nine")
    parser.add_argument("--sample-seconds", type=float, default=0.35)
    parser.add_argument("--warmup-seconds", type=float, default=2.0)
    parser.add_argument("--samples", type=int, default=7)
    parser.add_argument("--baseline-probe", type=Path, help="Previously built managed probe for interleaved revision measurements")
    args = parser.parse_args()
    if args.samples < 5 or args.sample_seconds <= 0 or args.warmup_seconds <= 0:
        parser.error("Require at least five positive-duration samples and warmup")
    repo, dotnet, qpdf, native_source, native_build, input_root, root = (
        getattr(args, n).resolve() for n in ("repo", "dotnet", "qpdf", "native_source", "native_build", "input_root", "work_root"))
    if os.name != "posix" or not Path("/proc").exists():
        parser.error("Run on Linux, including WSL")
    if run(["git", "-C", native_source, "rev-parse", "HEAD"]).decode().strip() != PIN or run(["git", "-C", native_source, "status", "--porcelain"]):
        raise ValueError("Require clean pinned native source")
    cache = (native_build / "CMakeCache.txt").read_text()
    if "CMAKE_BUILD_TYPE:STRING=Release" not in cache or f"CMAKE_HOME_DIRECTORY:INTERNAL={native_source}\n" not in cache or not qpdf.is_relative_to(native_build):
        raise ValueError("Require pinned source's Release build")
    if run([qpdf, "--version"]).decode().splitlines()[0] != "qpdf version 12.4.2":
        raise ValueError("Require native qpdf 12.4.2")
    root.mkdir(parents=True, exist_ok=False)
    shutil.copytree(input_root / "inputs", root / "inputs")
    cases = json.loads((input_root / "workloads.json").read_text())
    if args.workload:
        if set(args.workload) - {c["Name"] for c in cases}:
            raise ValueError("Unknown workload")
        cases = [c for c in cases if c["Name"] in args.workload]
    probe = repo / "tools/QpdfProbe/bin/Release/net10.0/QpdfProbe.dll"
    driver = root / "qpdf-warm"
    compile_command = ["g++", "-std=c++17", "-O3", "-DNDEBUG", "-I" + str(native_source / "include"),
        "-I" + str(native_build / "libqpdf"), str(repo / "tools/qpdf-warm.cpp"),
        str(native_build / "libqpdf/libqpdf.a"), "-lz", "-ljpeg", "-pthread", "-o", str(driver)]
    run(compile_command)
    sources = {p.name: fingerprints(qpdf, p) for p in (root / "inputs").glob("*.pdf")}
    inputs = {p.name: {"sha256": sha(p), "bytes": p.stat().st_size} for p in (root / "inputs").glob("*.pdf")}
    records, summaries = [], {}
    for case in cases:
        # The consuming memory host registers this directory eagerly. Expose
        # only this job's required inputs to both lanes, with unchanged bytes.
        workload_root = root / "workloads" / case["Name"]
        workload_inputs = workload_root / "inputs"
        workload_inputs.mkdir(parents=True)
        for name in case["Inputs"]:
            shutil.copyfile(root / "inputs" / name, workload_inputs / name)
            if sha(workload_inputs / name) != inputs[name]["sha256"]:
                raise ValueError("Workload input differs from original")
        workers = {"managed": Worker([str(dotnet), str(probe), "warm"], workload_root, root / (case["Name"] + "-managed.log")),
            "native": Worker([str(driver)], workload_root, root / (case["Name"] + "-native.log"))}
        if args.baseline_probe:
            workers["baseline"] = Worker([str(dotnet), str(args.baseline_probe.resolve()), "warm"], workload_root, root / (case["Name"] + "-baseline.log"))
        identities = {}
        counter = 0

        def job(engine, phase, repetition):
            nonlocal counter
            directory = root / "jobs" / str(counter) / "outputs"
            counter += 1
            directory.mkdir(parents=True)
            command = commands(case, "managed" if engine == "baseline" else engine, directory, workload_root, dotnet, probe, qpdf)
            sample = workers[engine].job(command[1:] if engine == "native" else command[2:])
            identity = output_identity(case, directory)
            if phase == "validation":
                if case["Name"] == "linearize-M500":
                    (directory.parent / "inputs").mkdir()
                    shutil.copyfile(directory / "out.pdf", directory.parent / "inputs/linearized.pdf")
                identities[engine] = validate(case, directory, sources, dotnet, probe, qpdf)
                # Validator must reject a wrong marker/order expectation on actual worker output.
                if engine == "native" and case["Outputs"]:
                    negative = dict(case)
                    negative["ExpectedMarkers"] = ["deliberately wrong"]
                    try:
                        validate(negative, directory, sources, dotnet, probe, qpdf)
                    except ValueError:
                        pass
                    else:
                        raise ValueError("Wrong-order/content expectation accepted")
            elif identity != identities[engine]:
                raise ValueError("Fresh output differs from independently validated worker output")
            sample.update(workload=case["Name"], engine=engine, phase=phase, repetition=repetition,
                outputBytes=sum(v["bytes"] for v in identity.values()))
            records.append(sample)
            shutil.rmtree(directory.parent)
            return sample

        try:
            for engine in workers:
                job(engine, "validation", -1)
            for name in case["Inputs"]:
                (workload_inputs / name).read_bytes()
            for engine in workers:
                start = time.monotonic()
                count = 0
                while count < 30 or time.monotonic() - start < args.warmup_seconds:
                    job(engine, "warmup", -1)
                    count += 1
            batches = {e: [] for e in workers}
            for repetition in range(args.samples):
                order = list(workers)
                if repetition % 2 != 0:
                    order.reverse()
                for engine in order:
                    elapsed, count = 0, 0
                    while count < 3 or elapsed < args.sample_seconds * 1000:
                        sample = job(engine, "measurement", repetition)
                        elapsed += sample["elapsedMilliseconds"]
                        count += 1
                    batches[engine].append(elapsed / count)
                (root / "samples.json").write_text(json.dumps(records, indent=2) + "\n")
            summaries[case["Name"]] = {e: {"milliseconds": describe(batches[e]),
                "processPeakRssKiB": max(r["processPeakRssKiB"] for r in records if r["workload"] == case["Name"] and r["engine"] == e),
                "outputBytes": sum(v["bytes"] for v in identities[e].values())} for e in workers}
            for engine in workers:
                summaries[case["Name"]][engine]["outputs"] = identities[engine]
            summaries[case["Name"]]["ratio"] = summaries[case["Name"]]["managed"]["milliseconds"]["median"] / summaries[case["Name"]]["native"]["milliseconds"]["median"]
            if args.baseline_probe:
                summaries[case["Name"]]["improvementPercent"] = 100 * (1 - summaries[case["Name"]]["managed"]["milliseconds"]["median"] / summaries[case["Name"]]["baseline"]["milliseconds"]["median"])
            print(case["Name"], json.dumps(summaries[case["Name"]]), flush=True)
        finally:
            for worker in workers.values():
                worker.close()
        (root / "samples.json").write_text(json.dumps(records, indent=2) + "\n")
    for name, identity in inputs.items():
        if sha(root / "inputs" / name) != identity["sha256"]:
            raise ValueError("Input changed")
    result = {"sourceCommit": run(["git", "-C", repo, "rev-parse", "HEAD"]).decode().strip(),
        "sourceDiff": run(["git", "-C", repo, "diff", "HEAD"]).decode(), "inputs": inputs,
        "librarySHA256": sha(probe.parent / "Lokad.Qpdf.dll"), "probeSHA256": sha(probe),
        "driverSHA256": sha(driver), "nativeSHA256": sha(qpdf), "nativeCommit": PIN,
        "baselineProbeSHA256": sha(args.baseline_probe.resolve()) if args.baseline_probe else None,
        "dotnetInfo": run([dotnet, "--info"]).decode(), "cpu": run(["lscpu"]).decode(),
        "os": run(["uname", "-a"]).decode(), "compileCommand": compile_command,
        "protocol": "Fresh public jobs, warm runtime and file cache; only each workload's required inputs are exposed to both lanes; necessary host/file transport included; no parsed-document reuse, fsync, IPC, validation or cleanup in job timing. Sequential alternating batches; all samples retained. Each sample batch runs at least three jobs and the selected accumulated timed duration. Peak RSS is each dedicated workload worker's process high-water mark.",
        "summary": summaries, "samples": records}
    (root / "results.json").write_text(json.dumps(result, indent=2) + "\n")


if __name__ == "__main__":
    main()
