"""Finite Linux process comparison; generated inputs and warmed managed results."""

import argparse
import base64
import hashlib
import json
import os
from pathlib import Path
import platform
import re
import signal
import statistics
import subprocess
import time


PIN = "4eba95899886e851cc41d76886483b347612f2a8"


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def run(argv):
    result = subprocess.run([str(a) for a in argv], capture_output=True, timeout=180)
    if result.returncode:
        raise RuntimeError(f"Command failed ({result.returncode}): {argv}\n{result.stderr.decode(errors='replace')}")
    return result.stdout


def native_json(qpdf, path):
    return json.loads(run([qpdf, "--json", "--json-key=pages", "--json-key=qpdf",
        "--json-stream-data=inline", "--decode-level=none", path]))


def fingerprints(qpdf, path):
    data = native_json(qpdf, path)
    objects = data["qpdf"][1]

    def entry(value):
        return objects["obj:" + value] if isinstance(value, str) and re.fullmatch(r"\d+ \d+ R", value) else {"value": value}

    def value(item):
        return entry(item).get("value", entry(item).get("stream", {}).get("dict"))

    def canonical(item, ancestors=()):
        if isinstance(item, str) and re.fullmatch(r"\d+ \d+ R", item):
            if item in ancestors:
                raise ValueError("Unexpected cycle in benchmark resource graph")
            obj = entry(item)
            if "stream" in obj:
                stream = obj["stream"]
                return {"dictionary": canonical({k: v for k, v in stream["dict"].items() if k != "/Length"}, ancestors + (item,)),
                        "payloadSHA256": hashlib.sha256(base64.b64decode(stream["data"])).hexdigest()}
            return canonical(obj["value"], ancestors + (item,))
        if isinstance(item, dict):
            return {k: canonical(v, ancestors) for k, v in sorted(item.items())}
        if isinstance(item, list):
            return [canonical(v, ancestors) for v in item]
        return item

    pages = []
    for page in data["pages"]:
        dictionary = value(page["object"])
        inherited = {}
        seen = set()
        while dictionary is not None:
            for key in ("/Resources", "/MediaBox", "/CropBox", "/Rotate"):
                if key in dictionary and key not in inherited:
                    inherited[key] = canonical(dictionary[key])
            parent = dictionary.get("/Parent")
            if parent is None:
                break
            if parent in seen:
                raise ValueError("Cyclic page parents")
            seen.add(parent)
            dictionary = value(parent)
        inherited.setdefault("/CropBox", inherited["/MediaBox"])
        inherited.setdefault("/Rotate", 0)
        pages.append({"attributes": inherited, "contents": [canonical(c) for c in page["contents"]]})
    trailer = objects["trailer"]["value"]
    info = canonical(trailer["/Info"]) if "/Info" in trailer else {}
    catalog = value(trailer["/Root"])
    return {"pages": pages, "info": info, "pageMode": catalog.get("/PageMode")}


def prepare_incremental(qpdf, root):
    source = root / "inputs/X100.pdf"
    streamed = root / "inputs/X100-streamed.pdf"
    run([qpdf, source, "--object-streams=generate", "--stream-data=preserve",
         "--compress-streams=n", "--force-version=1.5", "--static-id", streamed])
    data = native_json(qpdf, streamed)
    trailer = data["qpdf"][1]["trailer"]["value"]
    blob = streamed.read_bytes()
    previous = int(re.findall(rb"startxref\s+(\d+)", blob)[-1])
    number = trailer["/Size"]
    obj = f"{number} 0 obj\n<< /Title (X100 updated) /Producer (Generated Lokad.qpdf benchmark input) >>\nendobj\n".encode()
    position = len(blob)
    tail = (f"xref\n{number} 1\n{position:010d} 00000 n \ntrailer\n"
            f"<< /Size {number + 1} /Root {trailer['/Root']} /Info {number} 0 R /Prev {previous} >>\n"
            f"startxref\n{position + len(obj)}\n%%EOF\n").encode()
    (root / "inputs/X100-incremental.pdf").write_bytes(blob + obj + tail)


def commands(case, engine, destination, root, dotnet, probe, qpdf):
    physical = {"outputs/" + name: str(destination / name) for name in case["Outputs"]}
    if engine == "managed":
        argv = [str(dotnet), str(probe), "emit", "--workdir", str(root)]
        for logical, path in physical.items():
            argv += ["--out", logical + "=" + path]
        return argv + ["--"] + case["Arguments"]
    physical["outputs/out.pdf"] = str(destination / "out.pdf")
    return [str(qpdf)] + [physical.get(a, a) for a in case["Arguments"]]


def output_identity(case, directory):
    actual = sorted(p.name for p in directory.glob("*.pdf"))
    if actual != sorted(case["Outputs"]):
        raise ValueError(f"Output set differs: {actual} / {case['Outputs']}")
    return {p.name: {"bytes": p.stat().st_size, "sha256": sha(p)} for p in sorted(directory.glob("*.pdf"))}


def validate(case, directory, sources, dotnet, probe, qpdf):
    expected = []
    for name in case["Inputs"]:
        expected.extend(sources[name]["pages"])
    if case["Name"].startswith("select-"):
        expected.reverse()
    pages, markers = [], []
    for name in case["Outputs"]:
        path = directory / name
        run([qpdf, "--check", path])
        semantic = fingerprints(qpdf, path)
        pages.extend(semantic["pages"])
        markers.extend(json.loads(run([dotnet, probe, "page-text", "--input", path])))
        if case["Name"].startswith(("rewrite-", "object-streams-", "linearize-")):
            source = sources[case["Inputs"][0]]
            if semantic["info"] != source["info"] or semantic["pageMode"] != source["pageMode"]:
                raise ValueError("Catalog/incremental metadata changed")
        if case["Name"].startswith("object-streams-"):
            if b"/ObjStm" not in path.read_bytes():
                raise ValueError("Object-stream generation did not occur")
        if case["Name"].startswith("linearize-"):
            run([qpdf, "--check-linearization", path])
            run([dotnet, probe, "emit", "--workdir", directory.parent,
                 "--", "inputs/linearized.pdf", "--check-linearization"])
    if case["Outputs"] and (pages != expected or markers != case["ExpectedMarkers"]):
        raise ValueError(f"Independent page/content/resource/order validation failed: {case['Name']}")
    if case["Name"] == "split-M500" and any(len(fingerprints(qpdf, directory / n)["pages"]) != 50 for n in case["Outputs"]):
        raise ValueError("Split group size changed")
    return output_identity(case, directory)


def measure(argv, directory):
    """GNU time isolates target RSS; monotonic wall includes its common launcher."""
    def expired(signum, frame):
        raise TimeoutError("Benchmark process exceeded 180 seconds")
    with (directory / "stdout.log").open("wb") as out, (directory / "stderr.log").open("wb") as err:
        usage = directory / "usage.json"
        timed = ["/usr/bin/time", "-f",
                 '{"elapsedSeconds":%e,"peakRssKiB":%M,"userSeconds":%U,"systemSeconds":%S}',
                 "-o", str(usage), *argv]
        start = time.perf_counter_ns()
        signal.signal(signal.SIGALRM, expired)
        signal.alarm(180)
        process = None
        try:
            process = subprocess.Popen(timed, stdout=out, stderr=err, start_new_session=True)
            code = process.wait()
            elapsed = (time.perf_counter_ns() - start) / 1_000_000
            if code:
                raise RuntimeError(f"Measured process failed ({code}); see {directory}")
        except BaseException:
            if process is not None and process.poll() is None:
                os.killpg(process.pid, signal.SIGKILL)
                process.wait()
            raise
        finally:
            signal.alarm(0)
    return {"elapsedMs": elapsed, **json.loads(usage.read_text())}


def summary(samples):
    result = {}
    for field in ("elapsedMs", "peakRssKiB", "outputBytes"):
        values = [sample[field] for sample in samples]
        quartiles = statistics.quantiles(values, n=4, method="inclusive")
        result[field] = {"median": statistics.median(values), "q1": quartiles[0], "q3": quartiles[2],
                         "min": min(values), "max": max(values)}
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--repo", required=True, type=Path)
    parser.add_argument("--dotnet", required=True, type=Path)
    parser.add_argument("--qpdf", required=True, type=Path)
    parser.add_argument("--native-build", required=True, type=Path)
    parser.add_argument("--native-source", required=True, type=Path)
    parser.add_argument("--work-root", required=True, type=Path)
    parser.add_argument("--skip-warmed", action="store_true", help="Debugging only; not a complete comparison")
    args = parser.parse_args()
    if platform.system() != "Linux":
        parser.error("Use Linux (WSL is supported)")
    repo, dotnet, qpdf, root = (p.resolve() for p in (args.repo, args.dotnet, args.qpdf, args.work_root))
    root.mkdir(parents=True, exist_ok=False)
    os.chdir(root)
    probe = repo / "tools/QpdfProbe/bin/Release/net10.0/QpdfProbe.dll"
    benchmark = repo / "benchmarks/Lokad.Qpdf.Benchmarks/bin/Release/net10.0/Lokad.Qpdf.Benchmarks.dll"
    version = run([qpdf, "--version"]).decode().strip()
    if version.splitlines()[0] != "qpdf version 12.4.2":
        raise ValueError("Expected pinned native qpdf 12.4.2")
    actual_pin = run(["git", "-C", args.native_source, "rev-parse", "HEAD"]).decode().strip()
    if actual_pin != PIN or run(["git", "-C", args.native_source, "status", "--porcelain"]):
        raise ValueError("Native source must be clean at the pinned commit")
    cache = (args.native_build / "CMakeCache.txt").read_text()
    if "CMAKE_BUILD_TYPE:STRING=Release" not in cache:
        raise ValueError("Native build must be Release")
    run([dotnet, benchmark, "--comparison-inputs", root])
    prepare_incremental(qpdf, root)
    cases = json.loads((root / "workloads.json").read_text())
    inputs = {p.name: {"bytes": p.stat().st_size, "sha256": sha(p)} for p in sorted((root / "inputs").glob("*.pdf"))}
    sources = {}
    for name in inputs:
        path = root / "inputs" / name
        run([qpdf, "--check", path])
        sources[name] = fingerprints(qpdf, path)
        text = json.loads(run([dotnet, probe, "page-text", "--input", path]))
        if len(text) != len(sources[name]["pages"]):
            raise ValueError("Input reader counts disagree")
    baseline = {}
    for case in cases:
        for engine in ("managed", "native"):
            directory = root / "validation" / case["Name"] / engine / "outputs"
            directory.mkdir(parents=True)
            command = commands(case, engine, directory, root, dotnet, probe, qpdf)
            output = run(command)
            if engine == "managed" and b"emit: status=Completed exit=0" not in output:
                raise ValueError("Managed public job did not complete cleanly")
            (directory.parent / "command.json").write_text(json.dumps(command))
            (directory.parent / "stdout.log").write_bytes(output)
            if case["Name"] == "linearize-M500":
                aliases = directory.parent / "inputs"
                aliases.mkdir()
                (aliases / "linearized.pdf").write_bytes((directory / "out.pdf").read_bytes())
            baseline[(case["Name"], engine)] = validate(case, directory, sources, dotnet, probe, qpdf)
        print("VALIDATED", case["Name"], flush=True)
    negative = dict(cases[0])
    negative["ExpectedMarkers"] = list(reversed(negative["ExpectedMarkers"]))
    try:
        validate(negative, root / "validation" / negative["Name"] / "native/outputs", sources, dotnet, probe, qpdf)
    except ValueError:
        print("PASS negative control: incorrect page-order expectation rejects", flush=True)
    else:
        raise ValueError("Semantic validator accepted incorrect page order")
    records = []
    for case in cases:
        for repetition in range(-2, 7):
            # Warm file cache explicitly. Validation and destination setup are outside timing.
            for name in case["Inputs"]:
                (root / "inputs" / name).read_bytes()
            order = ("managed", "native") if repetition % 2 == 0 else ("native", "managed")
            for engine in order:
                directory = root / "samples" / case["Name"] / str(repetition) / engine
                directory.mkdir(parents=True)
                command = commands(case, engine, directory, root, dotnet, probe, qpdf)
                (directory / "command.json").write_text(json.dumps(command))
                sample = measure(command, directory)
                if engine == "managed" and "emit: status=Completed exit=0" not in (directory / "stdout.log").read_text():
                    raise ValueError("Measured managed public job did not complete cleanly")
                identities = output_identity(case, directory)
                if identities != baseline[(case["Name"], engine)]:
                    raise ValueError("Fresh output differs from independently validated output")
                records.append({"workload": case["Name"], "engine": engine, "repetition": repetition,
                    "conditioning": repetition < 0, "order": list(order), **sample,
                    "outputBytes": sum(v["bytes"] for v in identities.values()), "outputs": identities})
            (root / "samples.json").write_text(json.dumps(records, indent=2) + "\n")
        print("MEASURED", case["Name"], flush=True)
    for name, identity in inputs.items():
        if sha(root / "inputs" / name) != identity["sha256"]:
            raise ValueError("Original input changed")
    environment = {"platform": platform.platform(), "osRelease": Path("/etc/os-release").read_text(),
        "cpu": run(["lscpu"]).decode(), "memory": Path("/proc/meminfo").read_text(),
        "filesystem": run(["findmnt", "-T", root, "-o", "FSTYPE,OPTIONS"]).decode(),
        "dotnetInfo": run([dotnet, "--info"]).decode(), "nativeVersion": version,
        "nativePinnedCommit": actual_pin, "nativeCmakeCache": cache,
        "nativeLibraries": run(["ldd", qpdf]).decode(), "compiler": run(["g++", "--version"]).decode(),
        "sourceCommit": run(["git", "-C", repo, "rev-parse", "HEAD"]).decode().strip(),
        "sourceDiff": run(["git", "-C", repo, "diff", "HEAD"]).decode(),
        "librarySHA256": sha(probe.parent / "Lokad.Qpdf.dll"), "probeSHA256": sha(probe),
        "benchmarkSHA256": sha(benchmark), "nativeSHA256": sha(qpdf),
        "cachePolicy": "Warm filesystem cache; pre-read inputs; fresh output destinations; no fsync; sequential alternating pairs",
        "wallTime": "Monotonic spawn-to-exit including the identical GNU time launcher; process elapsedSeconds has GNU time's 10ms resolution",
        "peakMemory": "GNU time target peak RSS (KiB), includes managed runtime/host; not parent Python memory, allocations or charged estimates"}
    result = {"environment": environment, "inputs": inputs, "workloads": cases, "samples": records,
        "summary": {case["Name"]: {engine: summary([r for r in records if r["workload"] == case["Name"]
            and r["engine"] == engine and not r["conditioning"]]) for engine in ("managed", "native")} for case in cases},
        "validation": "Native structural checks, canonical page/content/resource fingerprints versus original inputs, independent PdfPig page text/order, metadata and object-stream/linearization checks"}
    (root / "results.json").write_text(json.dumps(result, indent=2) + "\n")
    if not args.skip_warmed:
        os.environ["QPDF_COMPARISON_ROOT"] = str(root)
        command = [str(dotnet), str(benchmark), "--filter", "*ComparisonBenchmarks*", "--job", "short",
                   "--launchCount", "1", "--warmupCount", "3", "--iterationCount", "7",
                   "--iterationTime", "250", "--exporters", "json", "--artifacts", str(root / "warmed")]
        (root / "warmed-command.json").write_text(json.dumps(command))
        with (root / "warmed.log").open("w") as log:
            subprocess.run(command, stdout=log, stderr=subprocess.STDOUT, check=True, timeout=1800, cwd=repo)
        reports = list((root / "warmed/results").glob("*ComparisonBenchmarks*full.json"))
        if len(reports) != 1 or len(json.loads(reports[0].read_text())["Benchmarks"]) != 9:
            raise ValueError("Warmed benchmark report is incomplete")
        print("WARMED managed results complete", flush=True)
    print("Results:", root / "results.json", flush=True)


if __name__ == "__main__":
    main()
