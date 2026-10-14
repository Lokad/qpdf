# Benchmarks

Lokad.qpdf meets the ≤1.0× native qpdf 12.4.2 target in **5 of 13** warmed,
codec-free memory-host workloads in this run. The target remains open.
Clean Release source `c29c5a3`; median milliseconds/job with Q1–Q3. Ratios use
unrounded values; all 26,993 observations remain in the raw results.

| Workload | Lokad.qpdf ms (Q1–Q3) | qpdf ms (Q1–Q3) | M/N |
| --- | ---: | ---: | ---: |
| select-S10 | 0.43 (0.41–0.44) | 0.75 (0.70–0.75) | 0.5804× |
| select-M500 | 17.57 (17.18–17.70) | 19.73 (19.69–20.19) | 0.8906× |
| merge-250x2 | 20.11 (19.79–20.33) | 18.24 (17.60–18.42) | 1.1025× |
| split-M500 | 26.10 (25.43–26.74) | 24.88 (24.50–25.78) | 1.0492× |
| rewrite-M500 | 28.06 (19.82–35.85) | 24.09 (13.66–27.34) | 1.1648× |
| rewrite-X100 | 2.20 (2.19–2.21) | 1.78 (1.76–1.80) | 1.2344× |
| object-streams-X100 | 2.05 (2.01–2.15) | 2.36 (2.28–2.38) | 0.8694× |
| linearize-M500 | 24.23 (23.21–25.14) | 19.25 (19.13–19.46) | 1.2587× |
| check-X100 | 2.03 (1.99–2.05) | 2.26 (2.24–2.31) | 0.8971× |
| rewrite-ID-M500 | 29.40 (28.28–44.39) | 22.10 (22.02–22.73) | 1.3305× |
| rewrite-objects | 28.09 (27.63–29.61) | 24.63 (24.46–25.12) | 1.1407× |
| select-sparse | 5.69 (5.62–5.77) | 1.60 (1.60–1.66) | 3.5508× |
| split-single | 5.61 (5.57–5.69) | 6.46 (6.45–6.49) | 0.8677× |

S10/M500 have 10/500 pages; merge combines two 250-page files. M500 content is
about 10 KiB/page; splitting produces ten 50-page files. X100 has streamed xrefs
and an incremental update. Other cases isolate content-derived IDs, 2,000 reachable
scalars, one-page selection omitting a 16 MiB stream, and 64 individual outputs.
Shared fonts, forms and metadata exercise interactions. Codecs and rendering are excluded.

| Workload | Managed allocation MB/job | Peak RSS MiB managed / native | Output KiB managed / native |
| --- | ---: | ---: | ---: |
| select-S10 | 0.086 | 72.9 / 6.4 | 3.9 / 4.1 |
| select-M500 | 17.679 | 155.0 / 7.7 | 5096.3 / 5105.1 |
| merge-250x2 | 22.807 | 162.5 / 8.4 | 5096.7 / 5105.5 |
| split-M500 | 30.512 | 142.3 / 7.9 | 5101.1 / 5108.7 |
| rewrite-M500 | 17.675 | 153.8 / 7.6 | 5096.3 / 5095.9 |
| rewrite-X100 | 0.670 | 77.1 / 6.4 | 69.5 / 69.5 |
| object-streams-X100 | 0.727 | 75.9 / 6.5 | 65.8 / 65.8 |
| linearize-M500 | 19.846 | 153.8 / 8.2 | 5099.2 / 5109.0 |
| check-X100 | 0.615 | 75.2 / 6.7 | 0.0 / 0.0 |
| rewrite-ID-M500 | 17.684 | 156.5 / 7.8 | 5096.3 / 5095.9 |
| rewrite-objects | 5.910 | 85.2 / 10.5 | 293.1 / 295.0 |
| select-sparse | 16.935 | 119.3 / 6.4 | 1.1 / 1.1 |
| split-single | 1.412 | 76.5 / 6.5 | 68.9 / 62.6 |

Allocation is cumulative, not retained memory. RSS includes each dedicated worker's
warmup, runtime and host storage; it is not a per-job working set. Split sizes sum
all outputs. Valid physical layouts and compression can produce different sizes.

An **additional typed file-host selection** uses the same sparse input with bounded
ranged reads. Native options match strict opaque selection, deterministic IDs and
inherited version. This does not replace the original memory-host cell above,
which includes its complete fresh input read. Current comparison:

| Job | Managed ms (Q1–Q3) | Native ms (Q1–Q3) | M/N | Managed MB/job | Peak RSS MiB M/N | Output KiB M/N |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| Sparse file-host selection | 0.67 (0.66–0.67) | 1.60 (1.60–1.61) | 0.4158× | 0.156 | 78.2 / 6.5 | 1.1 / 1.1 |

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
