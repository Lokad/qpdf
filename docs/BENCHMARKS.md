# Benchmarks

Development performance evidence uses generated inputs and the existing
`benchmarks/Lokad.Qpdf.Benchmarks` harness. It is never packed or used as a CI
timing gate. Managed benchmarks need no native tool; the explicit Linux
comparison below requires pinned qpdf 12.4.2.

The [WSL comparison](BENCHMARK-RESULTS.md) records the nine cells, independent
validation, both-warmed medians, variability, allocation/GC, RSS and output sizes.
Raw samples and reproduction commands remain available; historical process and
managed-only warmed measurements stay separate.

## Linux process comparison

`tools/compare-linux-performance.py` runs thirteen cells: reversed selection of 10
and 500 pages; merge of two 250-page sources; split of 500 pages into ten groups;
rewriting 500 pages and a 100-page streamed/incremental file; object-stream
generation for 100 pages; linearization of 500 pages; and structural checking
of the streamed/incremental file. Four additional cells isolate content-derived
IDs on the existing 500-page input, rewriting 2,000 reachable name/number/string
objects, selecting one small page while omitting a 16 MiB stream, and splitting
64 compact pages into individual files. These expose hashing, object parsing,
whole-input overhead and per-output work without another workload matrix.
Generated text, shared fonts/forms and
incremental metadata exercise distinct structural paths. The 500-page family
has about 5 MiB of content. No images are decoded, recompressed, optimized or
rendered; these generated inputs contain no images.

Before timing, native checking, canonical content/resource fingerprints and
independent PdfPig page text validate original and resulting semantics. Rewrite
metadata, object-stream presence and both linearization checkers receive explicit
checks. Independent page-text checks reuse the existing bounded sequential probe
batch, with a fresh reader for every file. A wrong-page-order expectation must reject. Every timed output is fresh
and must match its independently validated output for that implementation.

Whole-process measurements launch both engines freshly with identical inputs,
explicit stream/version/ID options and equivalent file destinations. Managed
`QpdfProbe emit` includes input discovery, copying into the in-memory host,
publication, output copying and physical writing. Fresh file buffers transfer
to the memory host without a second clone; historical copying-transport
observations remain recorded separately. Native uses its CLI filesystem
path. This measures complete public jobs, including their different transport
costs. It is not a comparison of isolated engines.

Run sequentially on an idle Linux machine, using one filesystem (WSL's Linux
filesystem rather than a Windows mount). Pre-read inputs for a documented warm
filesystem cache; new processes do not imply cold cache. Exclude validation and
cleanup from timing. Two conditioning pairs precede seven paired repetitions,
alternating execution order. Record monotonic wall time, target peak RSS from
GNU time and total output bytes; report median, interquartile range and min/max.
Wall includes the same small GNU time launcher on both sides; GNU time's own
elapsed value has 10 ms resolution. Output completion is buffered file closure,
without `fsync` on either side. Raw samples, commands, hashes and build/machine
metadata are retained under the fresh work root.

Warmed `ComparisonBenchmarks` run separately with BenchmarkDotNet: one launch,
three warmups and seven measurement iterations per cell, targeting 1000 ms per
iteration. They include fresh memory-host construction/input copying and the
public job, with disk reads/writes and setup outside measurement. Managed
allocation/GC statistics remain separate from process RSS; outlier removal is
disabled and every sample is retained. Never derive an
engine-speed ratio between these warmed calls and fresh native CLI launches.

Requires Python 3, GNU time, CMake/Ninja, a C++ compiler, zlib/JPEG development
libraries and the SDK selected by `global.json`. In a Linux checkout:

```bash
git clone https://github.com/qpdf/qpdf.git external/qpdf
git -C external/qpdf checkout 4eba95899886e851cc41d76886483b347612f2a8
cmake -S external/qpdf -B external/qpdf-linux-build -G Ninja \
  -DCMAKE_BUILD_TYPE=Release -DBUILD_SHARED_LIBS=OFF -DBUILD_STATIC_LIBS=ON \
  -DUSE_IMPLICIT_CRYPTO=OFF -DREQUIRE_CRYPTO_NATIVE=ON -DZOPFLI=OFF
cmake --build external/qpdf-linux-build --target qpdf --parallel 4
dotnet restore benchmarks/Lokad.Qpdf.Benchmarks --locked-mode
dotnet restore tools/QpdfProbe --locked-mode
dotnet build benchmarks/Lokad.Qpdf.Benchmarks -c Release --no-restore
dotnet build tools/QpdfProbe -c Release --no-restore
python3 tools/compare-linux-performance.py --repo "$PWD" \
  --dotnet "$(command -v dotnet)" \
  --qpdf "$PWD/external/qpdf-linux-build/qpdf/qpdf" \
  --native-source "$PWD/external/qpdf" \
  --native-build "$PWD/external/qpdf-linux-build" \
  --work-root "$PWD/external/linux-comparison-$(date +%Y%m%d-%H%M%S)"
```

Use a new work root each time. Inputs are generated by repository code under
the repository license; native only creates the streamed variant, followed by
an explicit incremental metadata update. The runner requires a clean native
source at the pinned commit and a Release build. `--skip-warmed` is available
for debugging and does not complete the comparison. No exhaustive matrix or
production correction is justified by these measurements alone.

## Warmed complete jobs and short probes

`tools/compare-warm-performance.py` reuses the prepared inputs, thirteen workloads
and independent validators above. It builds the development-only
`tools/qpdf-warm.cpp` against the clean pinned Release libqpdf, then keeps both
native QPDFJob and managed public-job workers alive. Every operation creates a
fresh job/document and writes fresh files. No parsed input is cached.

Both lanes include file input, processing and completed file output. Managed
uses the memory emit host with freshly read, transferred input buffers; its transport cost is included. Internal
job timers exclude protocol, validation and cleanup, and include no process
startup. This is a warmed complete-job comparison. The memory-host
BenchmarkDotNet lane is a separate diagnostic with different transport boundaries.

Each workload has its own workers and independently validated output. Inputs
are pre-read, execution is sequential, sample order alternates and all samples
are retained. At least 30 jobs and two seconds condition each engine, followed
by seven batches with at least three jobs and 350 ms of accumulated job time.
Report median batch time per operation, IQR and range. RSS is the dedicated
worker's process high-water mark, including warmup/runtime, rather than a
per-operation allocation. Managed allocation and generation collection deltas
remain separate diagnostic observations.

After preparing the process comparison's work root, run:

```bash
python3 tools/compare-warm-performance.py --repo "$PWD" \
  --dotnet "$(command -v dotnet)" \
  --qpdf "$PWD/external/qpdf-linux-build/qpdf/qpdf" \
  --native-source "$PWD/external/qpdf" \
  --native-build "$PWD/external/qpdf-linux-build" \
  --input-root "$PWD/external/prepared-comparison" \
  --work-root "$PWD/external/warm-comparison"
```

Select one existing cell with `--workload linearize-M500` for a short probe,
usually under one minute including validation and compilation. Preserve a built
baseline probe directory and pass `--baseline-probe <dir>/QpdfProbe.dll` to
interleave baseline, candidate and native jobs under the same conditions.
Performance commits need reduced elapsed time or per-job allocation with focused
correctness and resource checks; there is no minimum percentage improvement.
Removed allocations can establish a structural win. Report timing variability
and preserve meaningful limits, cancellation and cleanup. Library execution and
timed comparisons stay sequential; broader qualification belongs at integration
checkpoints.

To diagnose the sparse-selection transport gap, select only `select-sparse` in
the existing memory-only `ComparisonBenchmarks` lane. It defaults to defensive
input copying. Set `QPDF_COMPARISON_INPUT_OWNERSHIP=transfer` for a separate
observation using immutable prepared input arrays, without the optional host
clone. This isolates that allocation; it supplies no native engine ratio and
does not replace the file/job comparison or add a default ownership matrix.

For that selected diagnostic (repeat with `copy`):

```bash
QPDF_COMPARISON_ROOT="$PWD/external/prepared-comparison" \
QPDF_COMPARISON_INPUT_OWNERSHIP=transfer \
dotnet benchmarks/Lokad.Qpdf.Benchmarks/bin/Release/net10.0/Lokad.Qpdf.Benchmarks.dll \
  --filter '*ComparisonBenchmarks*select-sparse*' --inProcess --launchCount 1 \
  --warmupCount 7 --iterationCount 7 --iterationTime 350 --unrollFactor 1 \
  --outliers DontRemove --artifacts external/memory-sparse-transfer
```

Warmed cells use the built-in memory host directly, with their canonical input
names. File reads, owned input transfer, publication, output copies and physical
writes remain inside the timed call. The reference emitter retains its lazy
filename-alias adapter. For paired library comparisons, use the same probe build
with each reviewed library binary, so the host/transport path is identical.

Outputless checking has a readable defective-input preflight, whose expected
checker outcome prevents a successful no-op from qualifying. Object-stream
generation requires real containers and matching compressed xref/member entries;
an `/ObjStm` comment does not qualify. Run the finite acceptance controls with
`tools/test-performance-validation.py`, supplying `--repo`, `--dotnet`, `--probe`,
`--qpdf`, the prepared `--input-root` and a fresh `--work-root`. It validates both
engines' thirteen outcomes, content-ID repeatability and the reachable object
witness, and rejects the three historical controls,
without timing or a comprehensive product test run.

## Historical counter report

`--counter-report` runs representative operations and prints governor counters:
work units, decoded bytes, charged peak/live memory estimates, host operations and
diagnostics. The following report was recorded **2026-10-04**; its exact source
identity was not recorded here. Treat these values as a historical snapshot.
Bind new runs to a commit/source tree, runtime, platform and terminal exit.
Optional scenario names select a small subset, for example
`--counter-report select-few-50 linearize-10`. Cumulative allocation totals are
reported separately; neither those totals nor unreleased accounting charges
establish retained memory.

```
[counter] show-npages-50 status=Completed exit=0 in=11321 out=-1 work=767 decoded=0 peakmem=37057 livemem=37057 hostops=2 diags=1
[counter] select-few-50 status=Completed exit=0 in=11321 out=902 work=1359 decoded=0 peakmem=39519 livemem=39519 hostops=3 diags=0
[counter] merge-shared-8 status=Completed exit=0 in=898 out=1918 work=1278 decoded=0 peakmem=18382 livemem=18382 hostops=17 diags=0
[counter] merge-many-20 status=Completed exit=0 in=8980 out=4355 work=2814 decoded=0 peakmem=45515 livemem=45515 hostops=41 diags=0
[counter] decode-chain-30 status=Completed exit=0 in=1161 out=502 work=10170 decoded=4829 peakmem=10175 livemem=10175 hostops=3 diags=0
[counter] json-4 status=Completed exit=0 in=1104 out=2126 work=169 decoded=0 peakmem=4178 livemem=4178 hostops=3 diags=0
[counter] rewrite-compress-4 status=Completed exit=0 in=1104 out=1221 work=817 decoded=0 peakmem=7487 livemem=7487 hostops=3 diags=0
[counter] optimize-photo status=Completed exit=0 in=1798 out=1851 work=544 decoded=0 peakmem=8717 livemem=8717 hostops=3 diags=0
[counter] linearize-10 status=Completed exit=0 in=2433 out=2868 work=2351 decoded=0 peakmem=13791 livemem=13791 hostops=3 diags=0
[counter] cancel-select-50 status=Canceled exit=2 in=11321 work=0 hostops=0
counter-report: 10 scenarios, 0 failures
```

In this historical report, inspection decodes no payload bytes, the filter
chain produces under 5 KiB, host operations scale with opened inputs and
pre-canceled selection has zero work. These small scenarios did not expose the
ownership and counter defects found later by targeted probes. Governor counters
are supplemented by the small representative allocation/retention workloads and
bounded observed-work cancellation controls; [progress](PROGRESS.md) records
current corrections and remaining work. Exact physical-accounting calibration
and exhaustive retained/pool-state qualification are not release requirements.

## Timed benchmarks (local only)

BenchmarkDotNet benchmarks over the same synthetic inputs (page selection, filter-chain decode, page-count inspection) run locally via `dotnet run -c Release --project benchmarks/Lokad.Qpdf.Benchmarks -- --filter <pattern>`. Timings and allocations are machine-specific: they are measured locally, never asserted, and never recorded here as gates.

The historical first timed run (`--job short`, 3/3 benchmarks green in 43 s)
showed sub-millisecond means, allocations in the low hundreds of kilobytes and
no generation-2 pressure on these small inputs. The reported governor peak was
under 46 KiB. Those observations establish neither current admission completeness
nor a process-memory or cancellation-latency bound. Rerun against identified
sources when a concrete failure or material growth warrants further measurement.
