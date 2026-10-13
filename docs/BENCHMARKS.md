# Benchmarks

Lokad.qpdf meets single-CPU parity with native qpdf 12.4.2 in **3 of 13**
codec-free structural workloads on this machine. Ten remain above the ≤1.0×
target; sparse selection is the largest gap at **3.61×**. These are warmed,
complete public jobs, including input reads, publication and output writes.

Clean Release source `d4032b3`; median milliseconds per job, with Q1–Q3 in
parentheses. M/N is computed before rounding times; `check-X100` is near parity,
but has not passed it. All 22,820 observations are retained.

| Workload | Lokad.qpdf ms (Q1–Q3) | qpdf ms (Q1–Q3) | M/N |
| --- | ---: | ---: | ---: |
| select-S10 | 0.57 (0.56–0.60) | 0.85 (0.84–0.90) | 0.6671× |
| select-M500 | 21.34 (20.73–22.64) | 22.40 (21.66–23.12) | 0.9527× |
| merge-250x2 | 25.52 (24.80–27.98) | 20.15 (19.75–22.31) | 1.2666× |
| split-M500 | 32.82 (31.39–39.14) | 27.57 (26.67–28.49) | 1.1902× |
| rewrite-M500 | 22.05 (21.26–24.40) | 15.37 (14.92–16.18) | 1.4350× |
| rewrite-X100 | 2.85 (2.79–2.92) | 1.92 (1.86–1.93) | 1.4849× |
| object-streams-X100 | 2.98 (2.92–3.13) | 2.45 (2.43–2.50) | 1.2173× |
| linearize-M500 | 27.76 (27.07–34.91) | 20.70 (20.53–20.91) | 1.3408× |
| check-X100 | 2.51 (2.46–2.69) | 2.48 (2.42–2.51) | 1.0088× |
| rewrite-ID-M500 | 31.93 (31.34–36.60) | 26.21 (24.20–30.92) | 1.2183× |
| rewrite-objects | 34.49 (33.87–39.40) | 26.90 (26.42–27.08) | 1.2822× |
| select-sparse | 6.17 (6.11–6.35) | 1.71 (1.70–1.75) | 3.6056× |
| split-single | 6.79 (6.67–7.03) | 6.98 (6.98–7.05) | 0.9716× |

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
| select-S10 | 0.103 | 72.5 / 6.3 | 3.9 / 4.1 |
| select-M500 | 18.392 | 152.5 / 7.7 | 5096.3 / 5105.1 |
| merge-250x2 | 23.501 | 185.7 / 8.5 | 5096.7 / 5105.5 |
| split-M500 | 31.262 | 139.9 / 7.9 | 5101.1 / 5108.7 |
| rewrite-M500 | 18.388 | 151.4 / 7.5 | 5096.3 / 5095.9 |
| rewrite-X100 | 0.828 | 75.8 / 6.4 | 69.5 / 69.5 |
| object-streams-X100 | 0.922 | 76.3 / 6.5 | 65.8 / 65.8 |
| linearize-M500 | 20.478 | 156.7 / 8.2 | 5099.2 / 5109.0 |
| check-X100 | 0.710 | 74.9 / 6.7 | 0.0 / 0.0 |
| rewrite-ID-M500 | 18.446 | 158.2 / 7.8 | 5096.3 / 5095.9 |
| rewrite-objects | 7.145 | 82.3 / 10.6 | 293.1 / 295.0 |
| select-sparse | 16.976 | 114.2 / 6.3 | 1.1 / 1.1 |
| split-single | 1.543 | 76.2 / 6.4 | 68.9 / 62.6 |

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
input hashes remain unchanged. The [integration run](https://github.com/Lokad/qpdf/actions/runs/38342431222)
passes Windows, Ubuntu and the separate pinned reference job.

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
