# Benchmarks

Lokad.qpdf meets the ≤1.0× native qpdf 12.4.2 target in **5 of 13** warmed,
codec-free memory-host workloads in this run. The target remains open.
Clean Release source `946af8b`; median milliseconds/job with Q1–Q3. Ratios use
unrounded values; all 27,152 observations remain in the raw results.

| Workload | Lokad.qpdf ms (Q1–Q3) | qpdf ms (Q1–Q3) | M/N |
| --- | ---: | ---: | ---: |
| select-S10 | 0.43 (0.42–0.43) | 0.74 (0.73–0.74) | 0.5783× |
| select-M500 | 16.37 (15.95–16.54) | 19.68 (18.71–20.03) | 0.8317× |
| merge-250x2 | 19.27 (19.09–20.34) | 18.08 (18.07–18.20) | 1.0657× |
| split-M500 | 24.36 (24.02–24.83) | 24.08 (23.92–24.82) | 1.0119× |
| rewrite-M500 | 16.59 (16.30–16.75) | 13.66 (13.64–13.77) | 1.2141× |
| rewrite-X100 | 2.11 (2.08–2.43) | 1.80 (1.80–1.83) | 1.1686× |
| object-streams-X100 | 2.04 (2.01–2.11) | 2.24 (2.24–2.36) | 0.9133× |
| linearize-M500 | 21.78 (21.51–23.51) | 19.55 (19.23–20.51) | 1.1142× |
| check-X100 | 1.91 (1.90–1.92) | 2.24 (2.23–2.27) | 0.8528× |
| rewrite-ID-M500 | 22.86 (22.74–23.98) | 22.40 (21.76–22.62) | 1.0202× |
| rewrite-objects | 25.67 (25.22–26.30) | 24.66 (24.39–25.30) | 1.0410× |
| select-sparse | 5.71 (5.67–5.78) | 1.61 (1.60–1.63) | 3.5465× |
| split-single | 5.93 (5.69–6.16) | 6.65 (6.58–6.76) | 0.8924× |

S10/M500 have 10/500 pages; merge combines two 250-page files. M500 content is
about 10 KiB/page; splitting produces ten 50-page files. X100 has streamed xrefs
and an incremental update. Other cases isolate content-derived IDs, 2,000 reachable
scalars, one-page selection omitting a 16 MiB stream, and 64 individual outputs.
Shared fonts, forms and metadata exercise interactions. Codecs and rendering are excluded.

| Workload | Managed allocation MB/job | Peak RSS MiB managed / native | Output KiB managed / native |
| --- | ---: | ---: | ---: |
| select-S10 | 0.072 | 73.3 / 6.3 | 3.9 / 4.1 |
| select-M500 | 17.305 | 148.3 / 7.7 | 5096.3 / 5105.1 |
| merge-250x2 | 22.430 | 158.4 / 8.5 | 5096.7 / 5105.5 |
| split-M500 | 30.135 | 146.3 / 8.0 | 5101.1 / 5108.7 |
| rewrite-M500 | 17.300 | 150.4 / 7.6 | 5096.3 / 5095.9 |
| rewrite-X100 | 0.591 | 76.4 / 6.5 | 69.5 / 69.5 |
| object-streams-X100 | 0.650 | 77.0 / 6.4 | 65.8 / 65.8 |
| linearize-M500 | 19.447 | 155.2 / 8.1 | 5099.2 / 5109.0 |
| check-X100 | 0.545 | 75.1 / 6.8 | 0.0 / 0.0 |
| rewrite-ID-M500 | 17.301 | 153.2 / 7.7 | 5096.3 / 5095.9 |
| rewrite-objects | 4.825 | 84.6 / 10.6 | 293.1 / 295.0 |
| select-sparse | 16.900 | 118.7 / 6.3 | 1.1 / 1.1 |
| split-single | 1.362 | 77.2 / 6.5 | 68.9 / 62.6 |

Allocation is cumulative, not retained memory. RSS includes each dedicated worker's
warmup, runtime and host storage; it is not a per-job working set. Split sizes sum
all outputs. Valid physical layouts and compression can produce different sizes.

An **additional typed file-host selection** uses the same sparse input with bounded
ranged reads. Native options match strict opaque selection, deterministic IDs and
inherited version. This does not replace the original memory-host cell above,
which includes its complete fresh input read. Current comparison:

| Job | Managed ms (Q1–Q3) | Native ms (Q1–Q3) | M/N | Managed MB/job | Peak RSS MiB M/N | Output KiB M/N |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| Sparse file-host selection | 0.60 (0.59–0.61) | 1.60 (1.58–1.62) | 0.3746× | 0.121 | 78.5 / 6.5 | 1.1 / 1.1 |

## Measurement and correctness

Both persistent workers warm for ≥30 fresh jobs and two seconds, then alternate
seven sequential batches/engine, each ≥350 ms and three jobs. Warm filesystem
caches, Linux ext4 and comparable fresh destinations apply to both. Every job
rereads inputs and creates fresh documents/outputs. Memory-host managed jobs also
register fresh buffers and include public defensive output copies/file writes;
native uses QPDFJob and file paths. Publication and closure are timed; startup,
protocol, validation and cleanup are separate. No fsync, parsed-job cache,
forced GC or CPU parallelism. All samples and ranges are retained.

Before timing, fresh outputs pass native structural checks, canonical content/
resource/metadata/graph witnesses and independent PdfPig page/text checks. Object
streams, linearization and content-ID repeatability have explicit checks. Wrong
content/order, marker-only object streams and a no-op checker must reject. Timed
outputs must match independently validated hashes; original inputs stay unchanged.
[Progress](PROGRESS.md) records integration qualification.

Machine: Xeon W-2223, 4 cores/8 logical CPUs, 31.7 GiB host/15.5 GiB WSL RAM;
WSL2 Ubuntu 24.04.3, kernel 6.18.40.1, ext4. .NET SDK 10.0.401/runtime 10.0.12,
Release; native qpdf 12.4.2 `4eba95899886e851cc41d76886483b347612f2a8`, GCC 13.3
Release `-O3`, static libqpdf, system zlib/JPEG, Zopfli disabled.
[Raw results](../benchmarks/results/2026-10-12-parity-progress.json.gz) retain build/
binary identities, all observations and earlier trials, with local paths redacted.
[Whole-process results](../benchmarks/results/2026-10-11-linux.json) include startup
for both engines and remain separate. Managed-only BenchmarkDotNet allocation/GC
measurements supply no native ratio; do not compare warm calls with fresh processes.

## Reproduce

Requires Python 3, GNU time, CMake/Ninja, a C++ compiler, zlib/JPEG development
libraries and the SDK from `global.json`. From a Linux checkout:

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
# Generate MIT-licensed fixtures; measure fresh processes separately.
python3 tools/compare-linux-performance.py --repo "$PWD" \
  --dotnet "$(command -v dotnet)" --qpdf "$PWD/external/qpdf-linux-build/qpdf/qpdf" \
  --native-source "$PWD/external/qpdf" --native-build "$PWD/external/qpdf-linux-build" \
  --work-root "$PWD/external/prepared-comparison" --skip-warmed
python3 tools/compare-warm-performance.py --repo "$PWD" \
  --dotnet "$(command -v dotnet)" --qpdf "$PWD/external/qpdf-linux-build/qpdf/qpdf" \
  --native-source "$PWD/external/qpdf" --native-build "$PWD/external/qpdf-linux-build" \
  --input-root "$PWD/external/prepared-comparison" --work-root "$PWD/external/warm-comparison"
```

Use fresh work roots. Add `--workload select-sparse --file-input-selection` for
the additional file-host case, or `--workload rewrite-X100` for a narrow probe
(usually under one minute). `--baseline-probe <dir>/QpdfProbe.dll` interleaves
revisions; use the same driver binary. Keep reduced time or allocation, with no
minimum percentage; structural allocation removals can establish a win. Preserve
limits, cancellation, cleanup and public immutability.

`tools/test-performance-validation.py` checks all 26 original outcomes and three
negative controls without timing. Supply `--repo`, `--dotnet`, `--probe`, `--qpdf`,
`--input-root` and a fresh `--work-root`. Benchmarks are local development tools;
broader reference/package qualification belongs at integration checkpoints.
