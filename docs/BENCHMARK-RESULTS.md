# Linux benchmark comparison — 2026-10-12

The current goal is single-CPU parity (≤1.0×) in every agreed structural workload;
it remains open. The clean `11fb330` panel meets parity in three of thirteen
codec-free cells. Sparse selection exposes the largest complete-job gap:
8.17 / 2.26 ms (3.61×). Required full-input transport remains timed; the managed
reader receives all 16.8 MB while native can read selected objects lazily.

Recent production corrections remove split-wide scans, redundant writer clearing,
uncompressed counting buffers and unescaped-name buffers. Same-driver measurements
reduce object-stream allocation by 80.2 KB/job and object-heavy rewrite allocation
by 1.066 MB/job; their timing IQRs overlap. Fresh output hashes are unchanged.
All 26 extended managed/native outcomes and three negative controls pass.
Bounded batching with direct short-token formatting subsequently removes
1.465 MB/job from object-heavy rewriting and 84.7 KB/job from streamed rewriting.
Their timing IQRs overlap too; these are allocation wins.
Reusing batching for placeholder hashing and linearized publication removes
another 415 KB/job and 425 KB/job respectively. Same-driver medians are
103.99 → 110.69 ms and 124.03 → 129.73 ms with overlapping IQRs under load;
these observations establish no elapsed-time improvement. Output hashes match.
Deferring page-ancestry scratch until a second indirect parent removes another
323 KB/job from M500 selection (19.843 → 19.520 MB). Same-driver medians
32.09 → 31.71 ms overlap under load; control compilation may overlap the end
of that probe. Accept allocation only. Fresh output hashes remain unchanged.

[Parity progress raw observations](../benchmarks/results/2026-10-12-parity-progress.json.gz)
preserve the thirteen-cell baseline, final source confirmation, correction/transport pairs and separate
managed-only memory diagnostics. Every sample and summary number is retained;
local paths/hostnames are redacted. The diagnostic compares memory-host Copy and
immutable Transfer: 2.68 / 1.14 ms medians, 16.26 MiB / 247 KiB allocation. It
supplies no native ratio and does not replace the complete file-job comparison.

## Current thirteen-cell checkpoint

Clean `11fb330`, seven sequential alternating 350 ms batches per engine after
warmup; medians in milliseconds, Q1–Q3 in parentheses. All 16,005 observations
are retained. Variable host load broadens these distributions; separate panels
do not isolate individual changes. The following retained-chunk test correction
changes no production code, so it requires no additional benchmark run.

| Workload | Managed ms (Q1–Q3) | Native ms (Q1–Q3) | M/N |
| --- | ---: | ---: | ---: |
| select-S10 | 0.77 (0.72–0.99) | 0.99 (0.91–1.14) | 0.78× |
| select-M500 | 27.91 (23.52–29.58) | 22.74 (21.66–26.35) | 1.23× |
| merge-250x2 | 25.96 (24.47–26.83) | 19.95 (19.78–20.95) | 1.30× |
| split-M500 | 44.24 (37.03–57.30) | 27.29 (24.70–30.67) | 1.62× |
| rewrite-M500 | 34.70 (32.57–42.01) | 23.49 (19.88–25.26) | 1.48× |
| rewrite-X100 | 4.29 (3.90–5.38) | 2.68 (2.63–3.03) | 1.60× |
| object-streams-X100 | 6.82 (6.15–7.13) | 5.23 (3.81–6.92) | 1.30× |
| linearize-M500 | 35.37 (30.68–44.62) | 23.97 (23.09–24.22) | 1.48× |
| check-X100 | 4.20 (3.96–5.13) | 4.52 (3.73–4.68) | 0.93× |
| rewrite-ID-M500 | 61.61 (59.36–69.65) | 46.75 (38.67–53.69) | 1.32× |
| rewrite-objects | 74.48 (66.83–102.93) | 47.27 (42.38–50.62) | 1.58× |
| select-sparse | 8.17 (7.09–8.69) | 2.26 (2.06–2.74) | 3.61× |
| split-single | 17.77 (16.82–19.10) | 21.46 (21.06–23.29) | 0.83× |

The previously qualified nine-cell **2× target** on `219eca3` is historical evidence;
its ratios range from 0.66× to 1.77× pinned native qpdf 12.4.2.
Fresh outputs independently validate; input hashes are unchanged and recorded managed output hashes match earlier validated runs. Production/probe binaries match qualified `219eca3`; measurement tooling is `96c591f`. This qualifies the selected workloads and built-in memory-host path on this machine, rather than every PDF, option or custom host.

[Protocol and reproduction](BENCHMARKS.md#warmed-complete-jobs-and-short-probes) · [raw observations and build/input/output identities (gzip JSON)](../benchmarks/results/2026-10-12-warm-comparison.json.gz)

## Previously qualified elapsed time: both engines warmed

Median of seven batch means, in milliseconds per complete fresh job, with Q1–Q3 across batches in parentheses. The initial ratio is the original fair warmed baseline (`a4c8003`); separate-run changes do not isolate individual correction gains. All seven batches remain included.

| Workload | Managed ms (Q1–Q3) | Native ms (Q1–Q3) | Qualified M/N | Initial M/N |
| --- | ---: | ---: | ---: | ---: |
| select-S10 | 0.64 (0.59–0.91) | 0.98 (0.75–1.08) | 0.66× | 1.67× |
| select-M500 | 21.66 (21.49–22.18) | 20.87 (20.66–20.98) | 1.04× | 4.49× |
| merge-250x2 | 26.14 (25.98–27.51) | 18.63 (18.59–18.85) | 1.40× | 3.87× |
| split-M500 | 31.54 (31.34–32.89) | 24.60 (24.56–24.89) | 1.28× | 3.87× |
| rewrite-M500 | 22.28 (21.69–26.81) | 15.59 (14.41–17.03) | 1.43× | 6.28× |
| rewrite-X100 | 3.20 (3.15–3.23) | 1.81 (1.80–1.83) | 1.77× | 6.38× |
| object-streams-X100 | 3.45 (3.43–3.51) | 2.38 (2.37–2.40) | 1.45× | 4.95× |
| linearize-M500 | 26.94 (26.82–28.51) | 20.12 (20.03–20.71) | 1.34× | 6.51× |
| check-X100 | 2.59 (2.55–2.60) | 2.29 (2.28–2.29) | 1.13× | 2.93× |

The three largest initial gaps had managed medians of 181.82 → 22.28 ms for
M500 rewriting, 21.09 → 3.20 ms for X100 rewriting and 204.53 → 26.94 ms for
linearization. These before/after observations span different machine conditions;
the interleaved pair below isolates the final ownership correction.

The small input has 10 pages; medium inputs have 250/500 pages with approximately 10 KiB of content per page; the 100-page inputs exercise classic, incremental and streamed xrefs. The cells cover reversal, merging, splitting, ordinary rewriting, object-stream generation, linearization and checking, with shared fonts/forms. No image decoding, recompression, optimization or rendering is timed.

Both persistent workers warm for at least 30 fresh jobs and two seconds, then run seven sequential alternating batches per engine, each accumulating at least one second and three jobs. Required file reads, managed input/output copies, completed publication and file writes are inside the timer; startup, protocol, validation and cleanup are outside it. Both use warmed filesystem caches, equivalent options and fresh output directories on Linux ext4. There is no parsed-document cache or fsync. These are practical jobs with different host transport costs, not isolated engine timings.

The final same-driver M500 pair uses the identical current probe with reviewed baseline library `c4484fb`: 28.25 → 21.30 ms (24.6% lower observed median), with IQRs 27.92–28.52 and 21.22–21.71 ms. Allocation is 30.60 → 25.47 MB/job. The native median is 14.06 ms (1.51×).

Earlier direct-host probes also eagerly loaded unrelated fixtures; their cross-native ratios are invalid. The corrected runner exposes only each workload’s required original inputs to both engines. Those defect observations and all earlier noisy/contended runs remain in the raw report; no samples were discarded. Direct copy/allocation eliminations are accepted structurally after correctness/resource checks and have no empirical speed gate.

## Previously qualified allocation, process memory and output

Allocation is cumulative managed MB per fresh job, normalized separately from dedicated-worker peak RSS. RSS includes warmup, runtime and host storage; it is not an engine-only working set or a retained-memory-leak measurement. Split output size sums all files.

| Workload | Managed allocation MB/job initial → current | Peak RSS MiB managed/native | Output KiB managed/native |
| --- | ---: | ---: | ---: |
| select-S10 | 0.36 → 0.15 | 72.1/6.3 | 3.9/4.1 |
| select-M500 | 80.57 → 25.48 | 174.6/7.7 | 5096.3/5105.1 |
| merge-250x2 | 80.29 → 30.53 | 172.1/8.4 | 5096.7/5105.5 |
| split-M500 | 83.65 → 37.88 | 177.6/8.0 | 5101.1/5108.7 |
| rewrite-M500 | 92.91 → 25.47 | 173.6/7.6 | 5096.3/5095.9 |
| rewrite-X100 | 3.95 → 1.26 | 75.7/6.5 | 69.5/69.5 |
| object-streams-X100 | 5.55 → 1.39 | 75.4/6.6 | 65.8/65.8 |
| linearize-M500 | 91.67 → 27.60 | 176.9/8.0 | 5099.2/5109.0 |
| check-X100 | 1.68 → 0.92 | 75.5/6.8 | 0.0/0.0 |

M500 Gen2 collections per 1,000 measured jobs changed from 2381 to 835; object-stream generation changed from 233 to 10. Other generation/RSS results vary. Lower allocation does not establish a uniform GC-rate or process-memory reduction. No production forced collection was added.

## Correctness and scope

Every cell preserves the original inputs, options, limits and expected outcomes. Native structural checking, canonical content/resource/font/form fingerprints and independent PdfPig page/text order validate fresh outputs before timing. Metadata, object-stream presence and both linearization checkers have explicit checks; wrong-order/content controls reject. Every timed output must match its implementation’s validated output hash. Managed output identities match earlier recorded validated runs, and input hashes are unchanged. The first warmed baseline recorded output sizes without hashes; its data is retained as recorded. Valid object numbering, packing and linearization layouts explain the small output-size differences.

The warm probe now uses the built-in consuming memory host directly. The earlier filename-alias adapter correctly disables input-slice borrowing after the custom-host safety guard. The final pair uses one probe binary for both libraries to remove that transport-path difference. Single-source private graphs adopt the full pinned input charge; public readers, custom retaining hosts and multisource copying keep independent payloads. The unchanged 2 MiB three-source consumer and strengthened typed/argv/job controls pass.

The historical nine cells use static IDs; the extended panel separately measures
default-ID hashing. Classic/QDF/linearized golden-ID tests preserve original
bytes. Allocation and accounting estimates are separate from actual retained
storage. Integration qualification is recorded in [progress](PROGRESS.md).

## Reproduction and historical startup measurements

Use the [existing commands and generated fixtures](BENCHMARKS.md), adding `--sample-seconds 1 --samples 7` for the final matrix. Inputs are generated under the repository license. Runs use WSL2 Linux ext4 on an Intel Xeon W-2223 (four physical cores/eight logical CPUs), 31.7 GiB host RAM and about 15.5 GiB WSL RAM; Ubuntu 24.04.3, kernel 6.18.40.1, .NET SDK/runtime 10.0.401/10.0.12, managed Release. Native is clean qpdf 12.4.2 commit `4eba95899886e851cc41d76886483b347612f2a8`, GCC 13.3 Release `-O3`, system zlib/jpeg, Zopfli disabled.

The compressed JSON retains every included run’s observations, medians/variability, source and binary hashes, input/output identities and build/runtime metadata. Workspace paths and hostname use reproduction variables; numeric observations are unchanged. Read it with `gzip -dc benchmarks/results/2026-10-12-warm-comparison.json.gz`.

The original **whole-process** comparison on `35eb3fb` remains separate: managed medians 233–577 ms, native 3.5–29 ms, including fresh startup and file transport. [Raw process results](../benchmarks/results/2026-10-11-linux.json) retain its samples and commands. The original [managed-only warmed BenchmarkDotNet results](../benchmarks/results/2026-10-11-warmed.json) exclude disk transport and have no native ratio. Neither series is mixed into the both-warmed table above.
