# Benchmarks

Lokad.qpdf's warmed elapsed-time medians are **0.38–0.96× native qpdf 12.4.2**
across 13 structural workloads; equivalent sparse job JSON is **0.39×**.
Process memory is higher: roughly **75–147 MiB** for managed workers versus
**6–11 MiB** for native workers on this set.

File-backed measurements use fresh documents in warmed workers. Times are
milliseconds/job (Q1–Q3); M/N is managed/native elapsed time before rounding.

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

Fixtures cover 10/100/500 pages, merging two 250-page inputs, splitting into
50-page or individual files, incremental/xref/object-stream structure, 2,000
reachable scalars and a 16 MiB omitted stream. Selections reverse page order;
shared fonts, forms and metadata exercise graph copying. Image decoding,
recompression, optimization and rendering are excluded.

## Memory and output size

Allocation is cumulative managed allocation/job. Peak RSS includes worker warmup
and runtime storage; it is not a per-job working set. Output sizes sum split files;
valid PDF layouts can differ in size.

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

## Measurement conditions

| Setting | Measured configuration |
| --- | --- |
| Source | Release implementation `83a9f93`, measured 2026-10-15 |
| Hardware | Xeon W-2223, 4 cores / 8 logical CPUs; 31.7 GiB host / 15.5 GiB WSL RAM |
| Platform | WSL2, Ubuntu 24.04.3, kernel 6.18.54.1, ext4 |
| Managed | .NET SDK 10.0.401, runtime 10.0.12, Release |
| Native | qpdf 12.4.2, GCC 13.3, Release `-O3`, static libqpdf, system zlib/JPEG, Zopfli off |

Both engines run sequentially with warm filesystem caches, equivalent options and
fresh file inputs/outputs. After ≥30 jobs/two seconds of warmup, seven alternating
batches/engine each run ≥350 ms/three jobs. Managed warmup also waits for two seconds
of quiet JIT counters, boundedly. Use an idle host; retain all samples and later JIT.

Timing includes input acquisition, parsing, transformation, staging, publication,
defensive export and file writes. Startup, protocol, validation and temporary-file
deletion are excluded. No parsed cache, forced GC, fsync or library parallelism is
used. Memory-input comparisons require equivalent prepared bytes or acquisition
costs; these tables establish the file-input contract.

Before timing, native checks and PdfPig validate fresh output structure, page/text
semantics, resources, metadata, graph sharing, object streams, linearization and IDs.
Negative controls detect wrong content/order, fake object streams and no-op checking.
Every timed output hash is verified; inputs stay unchanged.

[Raw measurements](../benchmarks/results/2026-10-12-parity-progress.json.gz) include
samples, hashes and build identities. Current entries:
`warm-integrated-83a9f93-file-13-01` and `warm-integrated-83a9f93-job-01`.
Archived full-acquisition memory-host results retain their separate contract.

## Reproduce on Linux or WSL

Requires Python 3, GNU time, CMake/Ninja, C++/zlib/JPEG development tools and the
SDK from `global.json`. The generated fixtures are MIT-licensed. From the repo root:

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

# Generate inputs and measure whole-process timings, including startup, separately.
python3 tools/compare-linux-performance.py --repo "$PWD" \
  --dotnet "$(command -v dotnet)" --qpdf "$PWD/external/qpdf-linux-build/qpdf/qpdf" \
  --native-source "$PWD/external/qpdf" --native-build "$PWD/external/qpdf-linux-build" \
  --work-root "$PWD/external/prepared-comparison" --skip-warmed

# Independently validate the file-backed operations and negative controls.
python3 tools/test-performance-validation.py --repo "$PWD" \
  --dotnet "$(command -v dotnet)" --probe "$PWD/tools/QpdfProbe/bin/Release/net10.0/QpdfProbe.dll" \
  --qpdf "$PWD/external/qpdf-linux-build/qpdf/qpdf" \
  --input-root "$PWD/external/prepared-comparison" \
  --work-root "$PWD/external/validate-file" --file-input

# Measure comparable warmed jobs.
python3 tools/compare-warm-performance.py --repo "$PWD" \
  --dotnet "$(command -v dotnet)" --qpdf "$PWD/external/qpdf-linux-build/qpdf/qpdf" \
  --native-source "$PWD/external/qpdf" --native-build "$PWD/external/qpdf-linux-build" \
  --input-root "$PWD/external/prepared-comparison" --work-root "$PWD/external/warm-file" \
  --file-input
```

Use fresh work roots. Add `--workload select-sparse --job-selection` for equivalent
job JSON, or `--workload rewrite-X100` for a narrow probe. Whole-process timings
include startup for both engines; the tables report warmed jobs. No CI timing gates.
