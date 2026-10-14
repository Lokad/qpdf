# Benchmarks

Lokad.qpdf reaches ≤1.0× native qpdf 12.4.2 in **6 of 13** comparable warmed
file-backed workloads; the target remains open. Release source `f292c5e`, unchanged
fixtures/options, fresh inputs and file destinations. Median milliseconds/job
(Q1–Q3); ratios use unrounded values. The extra job-JSON row tests the same sparse
selection through both engines' job-file interfaces.

| File-backed workload | Lokad.qpdf ms (Q1–Q3) | qpdf ms (Q1–Q3) | M/N |
| --- | ---: | ---: | ---: |
| select-S10 | 0.61 (0.58–0.61) | 0.79 (0.75–0.80) | 0.7743× |
| select-M500 | 16.73 (16.54–18.37) | 21.47 (21.39–21.95) | 0.7793× |
| merge-250x2 | 22.61 (21.18–34.84) | 22.12 (20.44–33.45) | 1.0220× |
| split-M500 | 29.95 (27.29–41.71) | 31.44 (29.18–33.69) | 0.9526× |
| rewrite-M500 | 19.73 (18.45–21.99) | 17.54 (16.07–17.68) | 1.1249× |
| rewrite-X100 | 2.49 (2.46–2.62) | 1.92 (1.84–1.96) | 1.2942× |
| object-streams-X100 | 2.56 (2.49–2.57) | 2.54 (2.49–2.62) | 1.0076× |
| linearize-M500 | 22.02 (21.36–23.95) | 21.26 (21.10–22.04) | 1.0357× |
| check-X100 | 2.32 (2.18–2.35) | 2.40 (2.35–2.45) | 0.9651× |
| rewrite-ID-M500 | 25.03 (22.77–26.11) | 24.66 (24.19–25.60) | 1.0149× |
| rewrite-objects | 29.45 (28.13–30.27) | 28.34 (27.98–28.67) | 1.0391× |
| select-sparse | 0.76 (0.74–0.79) | 1.75 (1.68–1.77) | 0.4375× |
| split-single | 9.11 (7.72–9.67) | 9.63 (7.76–18.29) | 0.9454× |
| select-sparse (job JSON) | 0.92 (0.84–0.96) | 1.91 (1.87–2.08) | 0.4802× |

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
| select-S10 | 0.072 | 74.1 / 6.3 | 3.9 / 4.1 |
| select-M500 | 17.193 | 121.6 / 7.8 | 5096.3 / 5105.1 |
| merge-250x2 | 22.321 | 117.8 / 8.4 | 5096.7 / 5105.5 |
| split-M500 | 25.830 | 147.7 / 7.8 | 5101.1 / 5108.7 |
| rewrite-M500 | 17.188 | 117.3 / 7.6 | 5096.3 / 5095.9 |
| rewrite-X100 | 0.561 | 78.5 / 6.5 | 69.5 / 69.5 |
| object-streams-X100 | 0.625 | 78.2 / 6.5 | 65.8 / 65.8 |
| linearize-M500 | 18.986 | 116.9 / 8.2 | 5099.2 / 5109.0 |
| check-X100 | 0.543 | 77.4 / 6.6 | 0.0 / 0.0 |
| rewrite-ID-M500 | 17.189 | 116.9 / 7.8 | 5096.3 / 5095.9 |
| rewrite-objects | 4.538 | 87.2 / 10.6 | 293.1 / 295.0 |
| select-sparse | 0.132 | 74.6 / 6.4 | 1.1 / 1.1 |
| split-single | 1.123 | 77.8 / 6.4 | 68.9 / 62.6 |
| select-sparse (job JSON) | 0.158 | 75.9 / 6.6 | 1.1 / 1.1 |

Persistent workers warm for ≥30 fresh jobs/two seconds, then alternate seven
sequential batches/engine, each ≥350 ms/three jobs. Both use warm ext4 caches,
fresh file inputs/destinations and fresh documents. Managed staging, defensive
export and file writes remain timed. Publication/closure are included; startup,
protocol, validation and cleanup stay separate. No fsync, parsed cache, forced GC
or CPU parallelism. All samples remain. Memory engine comparisons require equal
prepared bytes or equal acquisition; the original results do not claim either.

Fresh outputs pass native structural checks, content/resource/metadata/graph
witnesses, PdfPig page/text checks, object-stream/linearization checks and ID
repeatability before timing. Wrong-content/order, marker-only object streams and
no-op checking must reject. Timed hashes match; original inputs remain unchanged.
[Progress](PROGRESS.md) records integration qualification. Earlier typed sparse
file-host evidence (0.3747×, `bf03d9a`) has strict/opaque typed options and remains
separate from argv/job results and the original memory-host cell.

Machine: Xeon W-2223, 4 cores/8 logical CPUs, 31.7 GiB host/15.5 GiB WSL RAM;
WSL2 Ubuntu 24.04.3, kernel 6.18.40.1, ext4. .NET SDK 10.0.401/runtime 10.0.12,
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
