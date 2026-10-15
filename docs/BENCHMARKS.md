# Benchmarks

Lokad.qpdf reaches ≤1.0× native qpdf 12.4.2 in **12 of 13** comparable warmed
file-backed workloads; the target remains open. Release source `6b0bc0e`, unchanged
fixtures/options, fresh inputs and file destinations. Median milliseconds/job
(Q1–Q3); ratios use unrounded values. The extra job-JSON row tests the same sparse
selection through both engines' job-file interfaces.

| File-backed workload | Lokad.qpdf ms (Q1–Q3) | qpdf ms (Q1–Q3) | M/N |
| --- | ---: | ---: | ---: |
| select-S10 | 0.46 (0.46–0.47) | 0.69 (0.69–0.70) | 0.6636× |
| select-M500 | 13.68 (13.56–13.76) | 20.17 (19.79–20.40) | 0.6783× |
| merge-250x2 | 16.71 (16.59–18.66) | 18.64 (18.53–19.39) | 0.8963× |
| split-M500 | 22.71 (22.53–22.89) | 25.10 (24.06–25.15) | 0.9048× |
| rewrite-M500 | 13.65 (13.29–13.83) | 13.96 (13.41–14.34) | 0.9782× |
| rewrite-X100 | 1.87 (1.82–2.65) | 1.81 (1.79–2.20) | 1.0307× |
| object-streams-X100 | 3.69 (3.68–3.78) | 7.69 (7.63–8.00) | 0.4804× |
| linearize-M500 | 19.49 (19.27–20.38) | 21.28 (21.07–22.09) | 0.9159× |
| check-X100 | 1.98 (1.97–1.98) | 2.25 (2.24–2.26) | 0.8775× |
| rewrite-ID-M500 | 20.61 (20.55–20.70) | 22.76 (22.69–23.55) | 0.9058× |
| rewrite-objects | 21.59 (20.17–28.76) | 28.32 (26.84–46.05) | 0.7622× |
| select-sparse | 0.98 (0.93–1.27) | 2.14 (2.05–3.28) | 0.4563× |
| split-single | 5.57 (5.41–5.93) | 7.14 (7.05–7.19) | 0.7804× |
| select-sparse (job JSON) | 0.77 (0.75–0.78) | 1.78 (1.75–1.94) | 0.4344× |

Host snapshots show 97.5% idle before and 21.8% after, with background scan activity.
All observations are retained; separate panels establish no causal speedup.

Fixtures cover 10/500 pages, two 250-page inputs, streamed/incremental xrefs,
2,000 reachable scalars, a 16 MiB omitted stream and 64 individual outputs.
Shared fonts/forms/metadata exercise interactions. Codecs and rendering stay out.
The incremental input uses unfiltered object streams; filtered object-stream
decoding has correctness coverage and no separate timed cell.

Original memory-host **end-to-end evidence** (`bf03d9a`, 23,507 observations):
managed includes fresh complete input acquisition and registration, staging, public
output export and file writes. Native opens files and can read less. These are
not equivalent prepared-input engine timings. In particular, sparse selection
still includes acquiring all 16.8 MB; a new ranged path does not turn its 3.4732×
result into a pass. All original allocations, RSS, sizes and samples remain archived.

| Original memory-host workload | Lokad.qpdf ms (Q1–Q3) | qpdf ms (Q1–Q3) | M/N |
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

Current file-backed resource figures; allocation is managed-only and cumulative.
RSS includes the dedicated worker's warmup/runtime/storage, not a per-job working
set. Split sizes sum all outputs; valid physical layouts can differ.

| File-backed workload | Managed MB allocated/job | Peak RSS MiB managed / native | Output KiB managed / native |
| --- | ---: | ---: | ---: |
| select-S10 | 0.075 | 74.6 / 6.4 | 3.9 / 4.1 |
| select-M500 | 17.192 | 113.7 / 7.7 | 5096.3 / 5105.1 |
| merge-250x2 | 22.319 | 113.3 / 8.3 | 5096.7 / 5105.5 |
| split-M500 | 25.827 | 145.1 / 7.9 | 5101.1 / 5108.7 |
| rewrite-M500 | 17.186 | 120.8 / 7.5 | 5096.3 / 5095.9 |
| rewrite-X100 | 0.536 | 77.5 / 6.5 | 69.5 / 69.5 |
| object-streams-X100 | 0.624 | 80.9 / 6.4 | 65.8 / 65.8 |
| linearize-M500 | 18.987 | 154.3 / 8.1 | 5099.2 / 5109.0 |
| check-X100 | 0.518 | 83.2 / 6.7 | 0.0 / 0.0 |
| rewrite-ID-M500 | 17.189 | 115.4 / 7.7 | 5096.3 / 5095.9 |
| rewrite-objects | 4.500 | 87.3 / 10.5 | 293.1 / 295.0 |
| select-sparse | 0.132 | 75.8 / 6.3 | 1.1 / 1.1 |
| split-single | 1.094 | 79.5 / 6.6 | 68.9 / 62.6 |
| select-sparse (job JSON) | 0.158 | 82.3 / 6.6 | 1.1 / 1.1 |

Persistent workers run ≥30 fresh jobs/two seconds; managed revisions also wait
for two seconds without JIT method-count/compilation-time changes (bounded warmup).
Counters stay outside timing/allocation capture; raw results retain readiness and
later compilation. Seven alternating batches/engine each run ≥350 ms/three jobs.
Both use warm ext4 caches,
fresh file inputs/destinations and fresh documents. Managed staging, defensive
export and file writes remain timed. Publication/closure are included; startup,
protocol, validation and cleanup stay separate. No fsync, parsed cache, forced GC
or CPU parallelism. All samples remain. Memory engine comparisons require equal
prepared bytes or equal acquisition; the original results do not claim either.

Fresh outputs pass native structural checks, content/resource/metadata/graph
witnesses, PdfPig page/text checks, object-stream/linearization checks and ID
repeatability before timing. Wrong-content/order, marker-only object streams and
no-op checking must reject. Timed hashes match; original inputs remain unchanged.
[Progress](PROGRESS.md) records integration qualification. The typed selection variant remains a separate protocol in the raw results.

Machine: Xeon W-2223, 4 cores/8 logical CPUs, 31.7 GiB host/15.5 GiB WSL RAM;
WSL2 Ubuntu 24.04.3, kernel 6.18.54.1, ext4 (original memory-host run: 6.18.40.1).
.NET SDK 10.0.401/runtime 10.0.12,
Release; native qpdf 12.4.2 `4eba95899886e851cc41d76886483b347612f2a8`, GCC 13.3
Release `-O3`, static libqpdf, system zlib/JPEG, Zopfli disabled.
[Raw results](../benchmarks/results/2026-10-12-parity-progress.json.gz) retain build/
binary identities and all trials, with local paths redacted. Earlier
[whole-process results](../benchmarks/results/2026-10-11-linux.json) include startup
for both engines and stay separate. Managed-only BenchmarkDotNet allocation/GC
statistics supply no native engine ratio.

## Reproduce

Requires Python 3, GNU time, CMake/Ninja, C++/zlib/JPEG development tools and the
SDK from `global.json`. From a Linux checkout:

```bash
git clone https://github.com/qpdf/qpdf.git external/qpdf
git -C external/qpdf checkout 4eba95899886e851cc41d76886483b347612f2a8
cmake -S external/qpdf -B external/qpdf-linux-build -G Ninja \
  -DCMAKE_BUILD_TYPE=Release -DBUILD_SHARED_LIBS=OFF -DBUILD_STATIC_LIBS=ON \
  -DUSE_IMPLICIT_CRYPTO=OFF -DREQUIRE_CRYPTO_NATIVE=ON -DZOPFLI=OFF
cmake --build external/qpdf-linux-build --target qpdf --parallel 4
dotnet restore benchmarks/Lokad.Qpdf.Benchmarks --locked-mode
dotnet restore tools/QpdfProbe --locked-mode
dotnet build benchmarks/Lokad.Qpdf.Benchmarks -c Release --no-restore --disable-build-servers
dotnet build tools/QpdfProbe -c Release --no-restore --disable-build-servers
# Generate MIT-licensed fixtures; measure fresh processes separately.
python3 tools/compare-linux-performance.py --repo "$PWD" \
  --dotnet "$(command -v dotnet)" --qpdf "$PWD/external/qpdf-linux-build/qpdf/qpdf" \
  --native-source "$PWD/external/qpdf" --native-build "$PWD/external/qpdf-linux-build" \
  --work-root "$PWD/external/prepared-comparison" --skip-warmed
python3 tools/compare-warm-performance.py --repo "$PWD" \
  --dotnet "$(command -v dotnet)" --qpdf "$PWD/external/qpdf-linux-build/qpdf/qpdf" \
  --native-source "$PWD/external/qpdf" --native-build "$PWD/external/qpdf-linux-build" \
  --input-root "$PWD/external/prepared-comparison" --work-root "$PWD/external/warm-file" \
  --file-input
```

Use fresh work roots. Omit `--file-input` to reproduce the original full-acquisition
memory-host protocol. Add `--workload select-sparse --job-selection` for equivalent
file-backed job JSON, or `--workload rewrite-X100` for a narrow probe, usually under
one minute. `--baseline-probe <dir>/QpdfProbe.dll` interleaves revisions with the
same driver. Replace `--file-input` with
`--file-input-selection --workload select-sparse` for the separate typed case.
Keep reduced time or allocation; no minimum percentage applies.

`tools/test-performance-validation.py --file-input` checks all 26 outcomes and
three negative controls without timing; omit the flag for the original host.
Supply `--repo`, `--dotnet`, `--probe`, `--qpdf`, `--input-root`, and a fresh
`--work-root`. Reference/visual/package-consumer qualification belongs at integration
checkpoints. Benchmarks stay outside CI timing gates.
