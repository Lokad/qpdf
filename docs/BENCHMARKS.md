# Benchmarks

In this run, Lokad.qpdf is at or below native qpdf 12.4.2 in **4 of 13**
codec-free structural workloads on this machine. 9 remain above the ≤1.0×
target; `select-sparse` is the largest gap at **3.73×**. These are warmed,
complete public jobs, including input reads, publication and output writes.

Clean Release source `4d4f273`; median milliseconds per job,
with Q1–Q3 in parentheses. M/N is computed before rounding times; near-parity
ratios above 1.0 remain open. All 24,089 observations are retained.

| Workload | Lokad.qpdf ms (Q1–Q3) | qpdf ms (Q1–Q3) | M/N |
| --- | ---: | ---: | ---: |
| select-S10 | 0.48 (0.47–0.50) | 0.77 (0.76–0.78) | 0.6227× |
| select-M500 | 18.74 (18.34–20.13) | 21.15 (20.91–21.22) | 0.8864× |
| merge-250x2 | 21.46 (20.95–22.13) | 19.00 (18.90–19.20) | 1.1296× |
| split-M500 | 26.82 (25.79–27.38) | 25.54 (25.29–26.39) | 1.0502× |
| rewrite-M500 | 41.94 (30.48–49.43) | 26.09 (21.27–33.91) | 1.6076× |
| rewrite-X100 | 2.40 (2.33–2.46) | 1.81 (1.81–1.83) | 1.3242× |
| object-streams-X100 | 2.50 (2.43–2.55) | 2.49 (2.46–2.71) | 1.0033× |
| linearize-M500 | 39.41 (30.23–50.71) | 26.47 (24.53–29.12) | 1.4887× |
| check-X100 | 2.78 (2.68–3.23) | 3.60 (3.23–3.80) | 0.7718× |
| rewrite-ID-M500 | 29.86 (29.25–39.84) | 23.89 (23.39–26.27) | 1.2497× |
| rewrite-objects | 45.03 (32.00–71.80) | 35.53 (29.29–49.70) | 1.2673× |
| select-sparse | 6.27 (6.17–6.35) | 1.68 (1.66–1.73) | 3.7317× |
| split-single | 6.66 (6.30–6.96) | 7.13 (6.95–7.69) | 0.9350× |

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
| select-S10 | 0.084 | 72.3 / 6.3 | 3.9 / 4.1 |
| select-M500 | 17.793 | 154.4 / 7.8 | 5096.3 / 5105.1 |
| merge-250x2 | 22.904 | 161.4 / 8.4 | 5096.7 / 5105.5 |
| split-M500 | 30.627 | 146.2 / 7.9 | 5101.1 / 5108.7 |
| rewrite-M500 | 17.788 | 152.6 / 7.6 | 5096.3 / 5095.9 |
| rewrite-X100 | 0.718 | 75.4 / 6.5 | 69.5 / 69.5 |
| object-streams-X100 | 0.801 | 76.3 / 6.5 | 65.8 / 65.8 |
| linearize-M500 | 19.947 | 153.2 / 8.1 | 5099.2 / 5109.0 |
| check-X100 | 0.656 | 72.6 / 6.7 | 0.0 / 0.0 |
| rewrite-ID-M500 | 17.825 | 165.4 / 7.7 | 5096.3 / 5095.9 |
| rewrite-objects | 6.217 | 89.1 / 10.6 | 293.1 / 295.0 |
| select-sparse | 16.942 | 114.3 / 6.3 | 1.1 / 1.1 |
| split-single | 1.438 | 75.5 / 6.5 | 68.9 / 62.6 |

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
