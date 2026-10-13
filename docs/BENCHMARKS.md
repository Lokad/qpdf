# Benchmarks

In this run, Lokad.qpdf is at or below native qpdf 12.4.2 in **4 of 13**
codec-free structural workloads on this machine. 9 remain above the ≤1.0×
target; `select-sparse` is the largest gap at **3.52×**. These are warmed,
complete public jobs, including input reads, publication and output writes.

Clean Release source `3d8f67c`; median milliseconds per job,
with Q1–Q3 in parentheses. M/N is computed before rounding times; near-parity
ratios above 1.0 remain open. All 24,690 observations are retained.

| Workload | Lokad.qpdf ms (Q1–Q3) | qpdf ms (Q1–Q3) | M/N |
| --- | ---: | ---: | ---: |
| select-S10 | 0.51 (0.50–0.51) | 0.78 (0.76–0.80) | 0.6577× |
| select-M500 | 18.47 (17.86–20.05) | 21.00 (20.68–21.31) | 0.8797× |
| merge-250x2 | 24.30 (23.49–26.04) | 19.38 (19.15–19.95) | 1.2539× |
| split-M500 | 29.46 (27.24–29.78) | 26.49 (25.80–27.19) | 1.1119× |
| rewrite-M500 | 18.02 (17.96–19.49) | 14.47 (14.33–14.68) | 1.2457× |
| rewrite-X100 | 2.66 (2.46–3.03) | 1.87 (1.86–1.93) | 1.4230× |
| object-streams-X100 | 2.63 (2.56–2.67) | 2.48 (2.47–2.51) | 1.0579× |
| linearize-M500 | 24.72 (24.23–28.19) | 20.88 (19.91–21.44) | 1.1840× |
| check-X100 | 2.22 (2.12–2.24) | 2.36 (2.35–2.42) | 0.9414× |
| rewrite-ID-M500 | 30.35 (28.98–31.38) | 23.60 (23.30–25.28) | 1.2861× |
| rewrite-objects | 33.51 (31.88–34.72) | 27.62 (26.70–27.96) | 1.2132× |
| select-sparse | 5.96 (5.92–6.10) | 1.70 (1.62–1.72) | 3.5171× |
| split-single | 6.38 (5.98–6.42) | 7.03 (6.90–7.19) | 0.9070× |

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
| select-S10 | 0.083 | 72.7 / 6.3 | 3.9 / 4.1 |
| select-M500 | 17.897 | 151.9 / 7.8 | 5096.3 / 5105.1 |
| merge-250x2 | 23.010 | 159.3 / 8.4 | 5096.7 / 5105.5 |
| split-M500 | 30.738 | 145.5 / 7.9 | 5101.1 / 5108.7 |
| rewrite-M500 | 17.893 | 156.2 / 7.5 | 5096.3 / 5095.9 |
| rewrite-X100 | 0.740 | 76.4 / 6.5 | 69.5 / 69.5 |
| object-streams-X100 | 0.823 | 76.5 / 6.5 | 65.8 / 65.8 |
| linearize-M500 | 20.053 | 155.3 / 8.0 | 5099.2 / 5109.0 |
| check-X100 | 0.656 | 73.3 / 6.7 | 0.0 / 0.0 |
| rewrite-ID-M500 | 17.931 | 156.2 / 7.8 | 5096.3 / 5095.9 |
| rewrite-objects | 6.239 | 84.7 / 10.6 | 293.1 / 295.0 |
| select-sparse | 16.943 | 113.7 / 6.4 | 1.1 / 1.1 |
| split-single | 1.463 | 76.2 / 6.4 | 68.9 / 62.6 |

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
