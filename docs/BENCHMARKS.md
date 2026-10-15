# Benchmarks

Lokad.qpdf reaches ≤1.0× native qpdf 12.4.2 in **13 of 13** comparable warmed
file-backed workloads; the comparable timing criterion is met. Release source
`83a9f93`, unchanged fixtures/options, fresh inputs and file destinations. Median milliseconds/job
(Q1–Q3); ratios use unrounded values. The extra job-JSON row tests the same sparse
selection through both engines' job-file interfaces.

| File-backed workload | Lokad.qpdf ms (Q1–Q3) | qpdf ms (Q1–Q3) | M/N |
| --- | ---: | ---: | ---: |
| select-S10 | 0.48 (0.46–0.48) | 0.75 (0.72–0.75) | 0.6418× |
| select-M500 | 13.48 (13.33–13.67) | 20.11 (19.78–20.55) | 0.6700× |
| merge-250x2 | 16.04 (16.00–16.41) | 18.20 (17.95–18.63) | 0.8815× |
| split-M500 | 21.24 (20.84–21.60) | 24.80 (23.94–25.03) | 0.8563× |
| rewrite-M500 | 12.75 (12.68–13.67) | 13.28 (13.08–14.26) | 0.9606× |
| rewrite-X100 | 1.70 (1.64–1.76) | 1.77 (1.72–1.81) | 0.9579× |
| object-streams-X100 | 1.85 (1.79–1.88) | 2.40 (2.33–2.44) | 0.7690× |
| linearize-M500 | 17.59 (17.55–17.93) | 20.18 (19.72–20.22) | 0.8718× |
| check-X100 | 1.90 (1.88–1.94) | 2.33 (2.27–2.81) | 0.8157× |
| rewrite-ID-M500 | 19.82 (19.69–20.31) | 22.91 (22.47–23.31) | 0.8654× |
| rewrite-objects | 20.05 (19.62–20.21) | 25.71 (23.78–25.88) | 0.7797× |
| select-sparse | 0.59 (0.58–0.61) | 1.57 (1.57–1.64) | 0.3767× |
| split-single | 5.02 (4.96–5.07) | 6.71 (6.28–6.96) | 0.7488× |
| select-sparse (job JSON) | 0.66 (0.65–0.69) | 1.68 (1.67–1.81) | 0.3919× |

Host idle snapshots were 89.5% before and 97.3% after the sequential panel.
All observations remain; separate panels establish no causal speedup.

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
| select-S10 | 0.075 | 74.6 / 6.3 | 3.9 / 4.1 |
| select-M500 | 17.192 | 110.4 / 7.7 | 5096.3 / 5105.1 |
| merge-250x2 | 22.319 | 119.3 / 8.4 | 5096.7 / 5105.5 |
| split-M500 | 25.827 | 147.2 / 7.9 | 5101.1 / 5108.7 |
| rewrite-M500 | 17.186 | 120.2 / 7.5 | 5096.3 / 5095.9 |
| rewrite-X100 | 0.536 | 78.5 / 6.4 | 69.5 / 69.5 |
| object-streams-X100 | 0.624 | 81.2 / 6.5 | 65.8 / 65.8 |
| linearize-M500 | 18.987 | 116.3 / 8.1 | 5099.2 / 5109.0 |
| check-X100 | 0.518 | 78.4 / 6.8 | 0.0 / 0.0 |
| rewrite-ID-M500 | 17.189 | 118.1 / 7.7 | 5096.3 / 5095.9 |
| rewrite-objects | 4.500 | 86.8 / 10.6 | 293.1 / 295.0 |
| select-sparse | 0.132 | 75.1 / 6.4 | 1.1 / 1.1 |
| split-single | 1.094 | 80.9 / 6.5 | 68.9 / 62.6 |
| select-sparse (job JSON) | 0.158 | 82.4 / 6.5 | 1.1 / 1.1 |

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
