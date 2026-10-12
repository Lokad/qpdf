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
import tempfile
import time


PIN = "4eba95899886e851cc41d76886483b347612f2a8"


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def run(argv, input_bytes=None):
    result = subprocess.run([str(a) for a in argv], input=input_bytes, capture_output=True, timeout=180)
    if result.returncode:
        raise RuntimeError(f"Command failed ({result.returncode}): {argv}\n"
                           f"{result.stdout.decode(errors='replace')}\n{result.stderr.decode(errors='replace')}")
    return result.stdout


def page_texts(dotnet, probe, paths):
    """Reuse the existing bounded sequential probe batch for independent checks."""
    observed = []
    for first in range(0, len(paths), 64):
        group = paths[first:first + 64]
        with tempfile.TemporaryDirectory(prefix="page-text-", dir=group[0].parent) as name:
            directory = Path(name)
            requests = [{"Id": i, "Arguments": ["page-text", "--input", str(path)],
                "WorkingDirectory": str(directory), "StandardOutput": str(directory / f"{i}.json"),
                "StandardError": str(directory / f"{i}.log")} for i, path in enumerate(group)]
            responses = run([dotnet, probe, "batch"],
                ("".join(json.dumps(request) + "\n" for request in requests)).encode())
            expected = [{"Id": i, "ExitCode": 0} for i in range(len(group))]
            if [json.loads(line) for line in responses.splitlines()] != expected:
                errors = "\n".join((directory / f"{i}.log").read_text() for i in range(len(group)))
                raise ValueError("Independent page-text batch failed: " + errors)
            observed.extend(json.loads((directory / f"{i}.json").read_text()) for i in range(len(group)))
    return observed


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
    return {"pages": pages, "info": info, "pageMode": catalog.get("/PageMode"),
        "ids": trailer.get("/ID", []), "graph": canonical(catalog["/BenchGraph"]) if "/BenchGraph" in catalog else None}


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


def checker_control_bytes():
    """Readable page tree, plus an unused object's contradictory xref header."""
    values = [b"<< /Type /Catalog /Pages 2 0 R >>",
        b"<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        b"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << >> >>", b"null"]
    data = bytearray(b"%PDF-1.5\n")
    offsets = []
    for number, value in enumerate(values, 1):
        offsets.append(len(data))
        header = 5 if number == 4 else number
        data += f"{header} 0 obj\n".encode() + value + b"\nendobj\n"
    xref = len(data)
    data += b"xref\n0 5\n0000000000 65535 f \n"
    for offset in offsets:
        data += f"{offset:010d} 00000 n \n".encode()
    data += f"trailer\n<< /Size 5 /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n".encode()
    return bytes(data)


def checker_preflight(directory, dotnet, probe, qpdf):
    """Opening succeeds; actual checking must detect a non-codec defect."""
    control = directory / "checker-control"
    (control / "inputs").mkdir(parents=True, exist_ok=True)
    (control / "inputs/control.pdf").write_bytes(checker_control_bytes())
    for option in ("--show-npages", "--check"):
        native = subprocess.run([str(qpdf), "inputs/control.pdf", option], cwd=control,
            capture_output=True, timeout=180)
        managed = subprocess.run([str(dotnet), str(probe), "emit", "--workdir", str(control),
            "--", "inputs/control.pdf", option], capture_output=True, timeout=180)
        match = re.search(rb"(?m)^emit: status=(\w+) exit=(\d+)\r?$", managed.stdout)
        expected_native, expected_managed, expected_status = (
            (0, 0, b"Completed") if option == "--show-npages" else (3, 2, b"Failed"))
        if (native.returncode != expected_native or managed.returncode != expected_managed
                or match is None or match.group(1) != expected_status
                or int(match.group(2)) != expected_managed):
            raise ValueError("Checker control outcome disagrees: " + option)
        if option == "--show-npages" and (native.stdout.strip() != b"1"
                or b"PageCount Info inputs/control.pdf: 1" not in managed.stdout):
            raise ValueError("Checker control must open with one page")
        (control / (option[2:] + "-native.log")).write_bytes(native.stdout + native.stderr)
        (control / (option[2:] + "-managed.log")).write_bytes(managed.stdout + managed.stderr)


def validate_object_streams(qpdf, path):
    objects = native_json(qpdf, path)["qpdf"][1]
    containers = {}
    for key, value in objects.items():
        stream = value.get("stream", {})
        dictionary = stream.get("dict", {})
        if dictionary.get("/Type") != "/ObjStm":
            continue
        count, first = dictionary.get("/N"), dictionary.get("/First")
        if (not isinstance(count, int) or count < 1 or not isinstance(first, int)
                or first < 0 or dictionary.get("/Filter") is not None):
            raise ValueError("Expected an uncompressed object-stream header")
        payload = base64.b64decode(stream["data"], validate=True)
        fields = payload[:first].split()
        if first >= len(payload) or len(fields) != 2 * count or any(not v.isdigit() for v in fields):
            raise ValueError("Invalid object-stream member table")
        numbers = [int(v) for v in fields[::2]]
        offsets = [int(v) for v in fields[1::2]]
        if (len(set(numbers)) != count or any(v < 1 for v in numbers)
                or any(a >= b for a, b in zip(offsets, offsets[1:]))
                or any(first + offset >= len(payload) for offset in offsets)):
            raise ValueError("Invalid object-stream member ownership/offsets")
        identity = re.fullmatch(r"obj:(\d+) 0 R", key)
        if identity is None or int(identity.group(1)) in numbers:
            raise ValueError("Invalid object-stream container identity")
        containers[int(identity.group(1))] = numbers
    entries = re.findall(rb"(?m)^(\d+)/0: compressed; stream = (\d+), index = (\d+)\r?$",
        run([qpdf, "--show-xref", path]))
    if not entries:
        raise ValueError("Object-stream generation did not create compressed xref entries")
    for member, owner, index in entries:
        number, container, slot = int(member), int(owner), int(index)
        if container not in containers or slot >= len(containers[container]) or containers[container][slot] != number:
            raise ValueError("Compressed xref disagrees with object-stream ownership")


def validate(case, directory, sources, dotnet, probe, qpdf, input_directory):
    expected = []
    for name in case["Inputs"]:
        expected.extend(sources[name]["pages"])
    if case["Name"] == "select-sparse":
        expected = expected[:1]
    elif case["Name"].startswith("select-"):
        expected.reverse()
    if case["Name"] == "rewrite-objects":
        witness = sources[case["Inputs"][0]]["graph"]
        wanted = [{"/Node": i, "/Key-" + str(i): "/Value-" + str(i),
            "/Nums": [i, 0, 0, 0], "/Text": "u:node\n" + str(i) + "(value)"} for i in range(2000)]
        if witness != wanted:
            raise ValueError("Object-heavy input lacks its reachable name/number/string witness")
    if not case["Outputs"]:
        if not case["Name"].startswith("check-"):
            raise ValueError("Outputless workload needs an explicit validation policy")
        observed = [text for pages in page_texts(dotnet, probe,
            [input_directory / name for name in case["Inputs"]]) for text in pages]
        if observed != case["ExpectedMarkers"]:
            raise ValueError("Independent checking input/order expectation failed")
        checker_preflight(directory.parent, dotnet, probe, qpdf)
    pages, markers, group_sizes = [], [], []
    texts = page_texts(dotnet, probe, [directory / name for name in case["Outputs"]])
    for name, text in zip(case["Outputs"], texts):
        path = directory / name
        run([qpdf, "--check", path])
        semantic = fingerprints(qpdf, path)
        pages.extend(semantic["pages"])
        group_sizes.append(len(semantic["pages"]))
        markers.extend(text)
        if case["Name"].startswith(("rewrite-", "object-streams-", "linearize-")):
            source = sources[case["Inputs"][0]]
            if semantic["info"] != source["info"] or semantic["pageMode"] != source["pageMode"]:
                raise ValueError("Catalog/incremental metadata changed")
            if semantic["graph"] != source["graph"]:
                raise ValueError("Reachable object witness changed")
        if case["Name"] == "rewrite-ID-M500":
            ids = semantic["ids"]
            if (len(ids) != 2 or any(not isinstance(value, str) or not re.fullmatch(r"b:[0-9a-f]{32}", value) for value in ids)
                    or (source["ids"] and ids[0] != source["ids"][0])):
                raise ValueError("Content-derived document IDs are invalid")
        if case["Name"].startswith("object-streams-"):
            validate_object_streams(qpdf, path)
        if case["Name"].startswith("linearize-"):
            run([qpdf, "--check-linearization", path])
            run([dotnet, probe, "emit", "--workdir", directory.parent,
                 "--", "inputs/linearized.pdf", "--check-linearization"])
    if case["Outputs"] and (pages != expected or markers != case["ExpectedMarkers"]):
        raise ValueError(f"Independent page/content/resource/order validation failed: {case['Name']}")
    if case["Name"] == "split-M500" and any(count != 50 for count in group_sizes):
        raise ValueError("Split group size changed")
    if case["Name"] == "split-single" and any(count != 1 for count in group_sizes):
        raise ValueError("Single-page split group size changed")
    if case["Name"] == "select-sparse" and sum((directory / name).stat().st_size for name in case["Outputs"]) > 65536:
        raise ValueError("Sparse selection retained material omitted payload")
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
    native_source, native_build = args.native_source.resolve(), args.native_build.resolve()
    root.mkdir(parents=True, exist_ok=False)
    os.chdir(root)
    probe = repo / "tools/QpdfProbe/bin/Release/net10.0/QpdfProbe.dll"
    benchmark = repo / "benchmarks/Lokad.Qpdf.Benchmarks/bin/Release/net10.0/Lokad.Qpdf.Benchmarks.dll"
    version = run([qpdf, "--version"]).decode().strip()
    if version.splitlines()[0] != "qpdf version 12.4.2":
        raise ValueError("Expected pinned native qpdf 12.4.2")
    actual_pin = run(["git", "-C", native_source, "rev-parse", "HEAD"]).decode().strip()
    if actual_pin != PIN or run(["git", "-C", native_source, "status", "--porcelain"]):
        raise ValueError("Native source must be clean at the pinned commit")
    cache = (native_build / "CMakeCache.txt").read_text()
    if "CMAKE_BUILD_TYPE:STRING=Release" not in cache:
        raise ValueError("Native build must be Release")
    if not qpdf.is_relative_to(native_build) or f"CMAKE_HOME_DIRECTORY:INTERNAL={native_source}\n" not in cache:
        raise ValueError("Native executable/build must belong to the pinned source")
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
            baseline[(case["Name"], engine)] = validate(case, directory, sources, dotnet, probe, qpdf, root / "inputs")
        print("VALIDATED", case["Name"], flush=True)
    negative = dict(cases[0])
    negative["ExpectedMarkers"] = list(reversed(negative["ExpectedMarkers"]))
    try:
        validate(negative, root / "validation" / negative["Name"] / "native/outputs", sources, dotnet, probe, qpdf, root / "inputs")
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
                   "--iterationTime", "1000", "--outliers", "DontRemove", "--exporters", "json", "--artifacts", str(root / "warmed")]
        (root / "warmed-command.json").write_text(json.dumps(command))
        with (root / "warmed.log").open("w") as log:
            subprocess.run(command, stdout=log, stderr=subprocess.STDOUT, check=True, timeout=1800, cwd=repo)
        reports = list((root / "warmed/results").glob("*ComparisonBenchmarks*full*.json"))
        if len(reports) != 1:
            raise ValueError("Warmed benchmark report is incomplete")
        benchmarks = json.loads(reports[0].read_text())["Benchmarks"]
        if len(benchmarks) != 9 or any(not b.get("Statistics") for b in benchmarks):
            raise ValueError("Warmed benchmark results are missing or failed")
        print("WARMED managed results complete", flush=True)
    print("Results:", root / "results.json", flush=True)


if __name__ == "__main__":
    main()
