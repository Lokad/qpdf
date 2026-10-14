# Benchmarks

Lokad.qpdf meets the ≤1.0× native qpdf 12.4.2 target in **4 of 13** warmed,
codec-free memory-host workloads in this run. The target remains open.
Clean Release source `f89d242`; median milliseconds/job with Q1–Q3. Ratios use
unrounded values; all 24,459 observations remain in the raw results.

| Workload | Lokad.qpdf ms (Q1–Q3) | qpdf ms (Q1–Q3) | M/N |
| --- | ---: | ---: | ---: |
| select-S10 | 0.42 (0.42–0.43) | 0.73 (0.72–0.74) | 0.5722× |
| select-M500 | 16.42 (16.24–16.92) | 19.08 (18.90–19.60) | 0.8605× |
| merge-250x2 | 19.89 (19.00–21.16) | 17.95 (17.68–18.15) | 1.1081× |
| split-M500 | 24.32 (24.02–24.77) | 24.02 (23.99–25.51) | 1.0126× |
| rewrite-M500 | 17.61 (17.41–18.53) | 14.91 (14.16–15.19) | 1.1813× |
| rewrite-X100 | 2.66 (2.55–3.01) | 2.41 (2.23–2.79) | 1.1024× |
| object-streams-X100 | 4.29 (2.99–4.56) | 2.73 (2.63–3.87) | 1.5725× |
| linearize-M500 | 42.08 (27.59–45.49) | 27.10 (25.61–39.43) | 1.5525× |
| check-X100 | 2.12 (2.07–2.31) | 2.47 (2.45–2.64) | 0.8607× |
| rewrite-ID-M500 | 25.39 (24.03–26.50) | 24.09 (23.45–24.86) | 1.0540× |
| rewrite-objects | 27.33 (26.18–28.95) | 26.99 (25.91–28.09) | 1.0125× |
| select-sparse | 5.96 (5.88–6.22) | 1.70 (1.68–1.75) | 3.4995× |
| split-single | 6.58 (6.24–6.73) | 7.29 (7.10–7.53) | 0.9023× |

S10/M500 have 10/500 pages; merge combines two 250-page files. M500 content is
about 10 KiB/page; splitting produces ten 50-page files. X100 has streamed xrefs
and an incremental update. Other cases isolate content-derived IDs, 2,000 reachable
scalars, one-page selection omitting a 16 MiB stream, and 64 individual outputs.
Shared fonts, forms and metadata exercise interactions. Codecs and rendering are excluded.

| Workload | Managed allocation MB/job | Peak RSS MiB managed / native | Output KiB managed / native |
| --- | ---: | ---: | ---: |
| select-S10 | 0.072 | 73.0 / 6.3 | 3.9 / 4.1 |
| select-M500 | 17.268 | 153.6 / 7.8 | 5096.3 / 5105.1 |
| merge-250x2 | 22.395 | 170.5 / 8.4 | 5096.7 / 5105.5 |
| split-M500 | 30.101 | 143.3 / 8.0 | 5101.1 / 5108.7 |
| rewrite-M500 | 17.264 | 153.2 / 7.5 | 5096.3 / 5095.9 |
| rewrite-X100 | 0.583 | 76.8 / 6.5 | 69.5 / 69.5 |
| object-streams-X100 | 0.642 | 76.6 / 6.4 | 65.8 / 65.8 |
| linearize-M500 | 19.410 | 154.1 / 8.1 | 5099.2 / 5109.0 |
| check-X100 | 0.537 | 74.6 / 6.6 | 0.0 / 0.0 |
| rewrite-ID-M500 | 17.265 | 155.6 / 7.8 | 5096.3 / 5095.9 |
| rewrite-objects | 4.749 | 89.4 / 10.6 | 293.1 / 295.0 |
| select-sparse | 16.900 | 118.5 / 6.3 | 1.1 / 1.1 |
| split-single | 1.361 | 76.8 / 6.5 | 68.9 / 62.6 |

Allocation is cumulative, not retained memory. RSS includes each dedicated worker's
warmup, runtime and host storage; it is not a per-job working set. Split sizes sum
all outputs. Valid physical layouts and compression can produce different sizes.

An **additional typed file-host selection** uses the same sparse input with bounded
ranged reads. Native options match strict opaque selection, deterministic IDs and
inherited version. This does not replace the original memory-host cell above,
which includes its complete fresh input read. Current comparison:

| Job | Managed ms (Q1–Q3) | Native ms (Q1–Q3) | M/N | Managed MB/job | Peak RSS MiB M/N | Output KiB M/N |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| Sparse file-host selection | 1.19 (0.99–1.21) | 2.99 (2.35–3.07) | 0.3998× | 0.121 | 78.5 / 6.5 | 1.1 / 1.1 |

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
