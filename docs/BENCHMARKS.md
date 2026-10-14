# Benchmarks

In this run, Lokad.qpdf is at or below native qpdf 12.4.2 in **5 of 13**
codec-free structural workloads on this machine. 8 remain above the ≤1.0×
target; `select-sparse` is the largest gap at **3.42×**. These are warmed,
complete public jobs, including input reads, publication and output writes.

Clean Release source `69c9673`; median milliseconds per job,
with Q1–Q3 in parentheses. M/N is computed before rounding times; near-parity
ratios above 1.0 remain open. All 26,628 observations are retained.

| Workload | Lokad.qpdf ms (Q1–Q3) | qpdf ms (Q1–Q3) | M/N |
| --- | ---: | ---: | ---: |
| select-S10 | 0.44 (0.44–0.45) | 0.73 (0.73–0.74) | 0.6081× |
| select-M500 | 17.50 (17.44–17.87) | 19.96 (19.93–20.03) | 0.8767× |
| merge-250x2 | 20.74 (19.76–20.94) | 18.04 (17.86–18.08) | 1.1493× |
| split-M500 | 24.63 (24.48–26.36) | 24.59 (24.30–24.85) | 1.0016× |
| rewrite-M500 | 17.12 (16.83–17.61) | 13.90 (13.84–13.99) | 1.2322× |
| rewrite-X100 | 2.22 (2.22–2.29) | 1.80 (1.79–1.82) | 1.2337× |
| object-streams-X100 | 2.24 (2.22–2.25) | 2.37 (2.35–2.38) | 0.9463× |
| linearize-M500 | 23.27 (23.14–24.48) | 19.25 (19.14–19.30) | 1.2089× |
| check-X100 | 2.06 (2.04–2.07) | 2.24 (2.24–2.27) | 0.9200× |
| rewrite-ID-M500 | 26.86 (26.59–27.98) | 22.25 (22.17–22.31) | 1.2069× |
| rewrite-objects | 32.55 (31.83–32.91) | 27.29 (27.15–28.15) | 1.1927× |
| select-sparse | 5.77 (5.76–5.79) | 1.69 (1.69–1.69) | 3.4165× |
| split-single | 6.88 (6.49–7.57) | 7.85 (7.74–8.27) | 0.8755× |

S10 has 10 pages. M500 and merge sources have 500/250 pages with about 10 KiB
of content per page; split-M500 produces ten 50-page files. X100 has 100 pages,
streamed xrefs and an incremental update. The remaining cells isolate
content-derived IDs, 2,000 reachable scalar objects, selection of one page
while omitting a 16 MiB stream, and 64 individual split outputs. Shared fonts,
forms and metadata exercise structural interactions. No image decoding,
recompression, optimization or rendering is measured.

Sparse selection includes the full 16.8 MB managed input read; native can read
selected objects lazily. It exposes a real host/reader boundary cost. This
comparison covers these inputs and the built-in memory host, not every PDF,
operation or custom host. Separate runs under different load do not isolate
the effect of a source change.

| Workload | Managed allocation MB/job | Peak RSS MiB managed / native | Output KiB managed / native |
| --- | ---: | ---: | ---: |
| select-S10 | 0.087 | 73.2 / 6.4 | 3.9 / 4.1 |
| select-M500 | 17.679 | 154.8 / 7.7 | 5096.3 / 5105.1 |
| merge-250x2 | 22.808 | 169.8 / 8.4 | 5096.7 / 5105.5 |
| split-M500 | 30.514 | 144.9 / 8.0 | 5101.1 / 5108.7 |
| rewrite-M500 | 17.676 | 151.9 / 7.6 | 5096.3 / 5095.9 |
| rewrite-X100 | 0.694 | 76.2 / 6.5 | 69.5 / 69.5 |
| object-streams-X100 | 0.727 | 76.2 / 6.5 | 65.8 / 65.8 |
| linearize-M500 | 19.846 | 153.9 / 8.1 | 5099.2 / 5109.0 |
| check-X100 | 0.640 | 74.2 / 6.6 | 0.0 / 0.0 |
| rewrite-ID-M500 | 17.684 | 155.9 / 7.7 | 5096.3 / 5095.9 |
| rewrite-objects | 5.909 | 84.4 / 10.6 | 293.1 / 295.0 |
| select-sparse | 16.936 | 115.2 / 6.4 | 1.1 / 1.1 |
| split-single | 1.412 | 76.4 / 6.5 | 68.9 / 62.6 |

Allocation is cumulative managed allocation, not retained memory. RSS is each
dedicated worker's high-water mark, including warmup, runtime and host storage;
it is not a per-job working set. Split sizes sum all outputs. Valid differences
in numbering, packing and linearized layout can change output size.

## Protocol and correctness

Both persistent workers warm for at least 30 fresh jobs and two seconds.
Seven sequential batches per engine then alternate execution order, each
accumulating at least 350 ms and three jobs. Inputs are pre-read for warm
filesystem caches; inputs and fresh output destinations share Linux ext4.
All samples, medians, IQRs and ranges remain in the raw results.

Every job rereads its original inputs and creates fresh hosts/documents/outputs.
Managed transfers fresh file buffers into the memory host and includes public
defensive output copies and file writes. Native uses QPDFJob and file paths.
The timer includes completed publication/file closure; startup, worker protocol,
validation and cleanup are outside it. Neither lane uses fsync, parsed-document
caching, forced GC or CPU parallelism.

Before timing, native structural checks, canonical content/resource/metadata/
graph witnesses and independent PdfPig page/text checks validate fresh outputs.
Object-stream containers and compressed member entries, both linearization
checkers and content-ID repeatability receive explicit checks. Wrong content/
order, marker-only object streams and a no-op checker must reject. Every timed
output must match its implementation's independently validated hash; all original
input hashes remain unchanged. [Progress](PROGRESS.md) identifies integration
qualification of ordinary tests, packages and the separate pinned reference job.

Machine: Intel Xeon W-2223, four physical cores/eight logical CPUs, 31.7 GiB host
RAM / 15.5 GiB WSL RAM; WSL2 Ubuntu 24.04.3, kernel 6.18.40.1, Linux ext4.
Managed: .NET SDK 10.0.401/runtime 10.0.12, Release. Native: qpdf 12.4.2 commit
`4eba95899886e851cc41d76886483b347612f2a8`, GCC 13.3 Release `-O3`, static libqpdf,
system zlib/JPEG, Zopfli disabled. Build/binary identities are recorded in the
[raw results](../benchmarks/results/2026-10-12-parity-progress.json.gz), including
earlier and rejected trials; local paths/hostnames are redacted. This document
reports the current comparison rather than a performance history.

Whole-process measurements launch both engines freshly and include startup;
they remain [separate](../benchmarks/results/2026-10-11-linux.json).
Managed-only BenchmarkDotNet measurements exclude disk transport and supply
no native ratio. Never compare a warmed managed call with a new native process
as an engine-speed measurement. Managed allocation/GC diagnostics also remain
separate from cross-engine RSS comparisons.

## Reproduce or probe

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

# Generate/validate the licensed fixtures and measure whole processes separately.
python3 tools/compare-linux-performance.py --repo "$PWD" \
  --dotnet "$(command -v dotnet)" \
  --qpdf "$PWD/external/qpdf-linux-build/qpdf/qpdf" \
  --native-source "$PWD/external/qpdf" \
  --native-build "$PWD/external/qpdf-linux-build" \
  --work-root "$PWD/external/prepared-comparison" --skip-warmed

# Compare both warmed complete-job workers.
python3 tools/compare-warm-performance.py --repo "$PWD" \
  --dotnet "$(command -v dotnet)" \
  --qpdf "$PWD/external/qpdf-linux-build/qpdf/qpdf" \
  --native-source "$PWD/external/qpdf" \
  --native-build "$PWD/external/qpdf-linux-build" \
  --input-root "$PWD/external/prepared-comparison" \
  --work-root "$PWD/external/warm-comparison"
```

Use fresh work roots. Inputs are generated by repository code under the MIT
license, with no third-party assets; native creates the streamed variant before
the incremental metadata update. The runners require the clean pinned native
source and Release build. Read the archive with
`gzip -dc benchmarks/results/2026-10-12-parity-progress.json.gz`.

For a narrow probe, add `--workload rewrite-X100` (usually under one minute).
Preserve a baseline probe directory and use `--baseline-probe <dir>/QpdfProbe.dll`
with the same driver binary to interleave baseline, candidate and native jobs.
Keep a correction when it reduces time or allocation and passes focused behavior/
resource checks; no minimum percentage improvement applies. Straightforward
allocation removals can establish a structural win. Report variability and
preserve limits, cancellation, cleanup and public immutability.

`tools/test-performance-validation.py` reuses the prepared fixtures to check
all 26 managed/native outcomes and the three negative controls without timing.
Supply `--repo`, `--dotnet`, `--probe`, `--qpdf`, `--input-root` and a fresh
`--work-root`. Broader qualification belongs at integration checkpoints;
benchmarks are local development tools, never CI timing gates or package contents.
