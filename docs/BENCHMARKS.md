# Benchmarks

Lokad.qpdf meets the ≤1.0× native qpdf 12.4.2 target in **5 of 13** warmed,
codec-free memory-host workloads in this run. The target remains open.
Clean Release source `bf03d9a`; median milliseconds/job with Q1–Q3. Ratios use
unrounded values; all 23,507 observations remain in the raw results.

| Workload | Lokad.qpdf ms (Q1–Q3) | qpdf ms (Q1–Q3) | M/N |
| --- | ---: | ---: | ---: |
| select-S10 | 0.51 (0.49–0.63) | 0.84 (0.79–0.89) | 0.6136× |
| select-M500 | 17.75 (16.95–18.42) | 21.96 (20.96–22.97) | 0.8081× |
| merge-250x2 | 24.17 (22.23–40.84) | 22.44 (20.59–23.57) | 1.0772× |
| split-M500 | 35.11 (31.48–42.70) | 30.71 (29.97–35.31) | 1.1432× |
| rewrite-M500 | 18.49 (16.80–18.75) | 14.89 (14.75–16.41) | 1.2417× |
| rewrite-X100 | 2.35 (2.27–2.37) | 1.90 (1.86–1.92) | 1.2373× |
| object-streams-X100 | 2.23 (2.20–2.37) | 2.41 (2.34–2.43) | 0.9242× |
| linearize-M500 | 27.08 (26.32–39.62) | 24.45 (23.19–25.99) | 1.1076× |
| check-X100 | 2.33 (2.07–2.61) | 2.50 (2.38–2.66) | 0.9297× |
| rewrite-ID-M500 | 28.30 (26.69–32.87) | 25.03 (24.88–28.13) | 1.1308× |
| rewrite-objects | 30.36 (29.77–34.46) | 28.76 (28.18–30.31) | 1.0557× |
| select-sparse | 6.20 (5.96–6.26) | 1.78 (1.75–1.89) | 3.4732× |
| split-single | 5.96 (5.86–6.01) | 6.94 (6.83–7.02) | 0.8583× |

S10/M500 have 10/500 pages; merge combines two 250-page files. M500 content is
about 10 KiB/page; splitting produces ten 50-page files. X100 has streamed xrefs
and an incremental update. Other cases isolate content-derived IDs, 2,000 reachable
scalars, one-page selection omitting a 16 MiB stream, and 64 individual outputs.
Shared fonts, forms and metadata exercise interactions. Codecs and rendering are excluded.

| Workload | Managed allocation MB/job | Peak RSS MiB managed / native | Output KiB managed / native |
| --- | ---: | ---: | ---: |
| select-S10 | 0.066 | 73.1 / 6.4 | 3.9 / 4.1 |
| select-M500 | 17.184 | 152.6 / 7.7 | 5096.3 / 5105.1 |
| merge-250x2 | 22.312 | 159.7 / 8.4 | 5096.7 / 5105.5 |
| split-M500 | 29.802 | 151.1 / 8.0 | 5101.1 / 5108.7 |
| rewrite-M500 | 17.181 | 151.2 / 7.6 | 5096.3 / 5095.9 |
| rewrite-X100 | 0.563 | 76.3 / 6.5 | 69.5 / 69.5 |
| object-streams-X100 | 0.622 | 76.4 / 6.5 | 65.8 / 65.8 |
| linearize-M500 | 18.981 | 157.0 / 8.1 | 5099.2 / 5109.0 |
| check-X100 | 0.537 | 75.0 / 6.7 | 0.0 / 0.0 |
| rewrite-ID-M500 | 17.181 | 158.8 / 7.8 | 5096.3 / 5095.9 |
| rewrite-objects | 4.536 | 86.3 / 10.5 | 293.1 / 295.0 |
| select-sparse | 16.900 | 117.3 / 6.3 | 1.1 / 1.1 |
| split-single | 1.247 | 76.6 / 6.6 | 68.9 / 62.6 |

Allocation is cumulative, not retained memory. RSS includes each dedicated worker's
warmup, runtime and host storage; it is not a per-job working set. Split sizes sum
all outputs. Valid physical layouts and compression can produce different sizes.

An **additional typed file-host selection** uses the same sparse input with bounded
ranged reads. Native options match strict opaque selection, deterministic IDs and
inherited version. This does not replace the original memory-host cell above,
which includes its complete fresh input read. Current comparison:

| Job | Managed ms (Q1–Q3) | Native ms (Q1–Q3) | M/N | Managed MB/job | Peak RSS MiB M/N | Output KiB M/N |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| Sparse file-host selection | 0.70 (0.69–1.00) | 1.87 (1.77–2.43) | 0.3747× | 0.120 | 79.3 / 6.5 | 1.1 / 1.1 |

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
