# Linux benchmark comparison — 2026-10-12

All nine representative warmed structural jobs meet the **2× target** on `219eca3`; ratios range from 0.66× to 1.77× pinned native qpdf 12.4.2.
Fresh outputs independently validate; input hashes are unchanged and recorded managed output hashes match earlier validated runs. Production/probe binaries match qualified `219eca3`; measurement tooling is `96c591f`. This qualifies the selected workloads and built-in memory-host path on this machine, rather than every PDF, option or custom host.

[Protocol and reproduction](BENCHMARKS.md#warmed-complete-jobs-and-short-probes) · [raw observations and build/input/output identities (gzip JSON)](../benchmarks/results/2026-10-12-warm-comparison.json.gz)

## Elapsed time: both engines warmed

Median of seven batch means, in milliseconds per complete fresh job, with Q1–Q3 across batches in parentheses. The initial ratio is the original fair warmed baseline (`a4c8003`); separate-run changes do not isolate individual correction gains. All seven batches remain included.

| Workload | Managed ms (Q1–Q3) | Native ms (Q1–Q3) | Current M/N | Initial M/N |
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

## Allocation, process memory and output

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

All nine cells use static IDs. Incremental default-ID hashing removes a placeholder-PDF allocation, but this matrix does not measure that benefit; classic/QDF/linearized golden-ID tests preserve its original bytes. Allocation and accounting estimates are separate from actual retained storage. Final ordinary/reference/visual/package qualification is recorded in [progress](PROGRESS.md).

## Reproduction and historical startup measurements

Use the [existing commands and generated fixtures](BENCHMARKS.md), adding `--sample-seconds 1 --samples 7` for the final matrix. Inputs are generated under the repository license. Runs use WSL2 Linux ext4 on an Intel Xeon W-2223 (four physical cores/eight logical CPUs), 31.7 GiB host RAM and about 15.5 GiB WSL RAM; Ubuntu 24.04.3, kernel 6.18.40.1, .NET SDK/runtime 10.0.401/10.0.12, managed Release. Native is clean qpdf 12.4.2 commit `4eba95899886e851cc41d76886483b347612f2a8`, GCC 13.3 Release `-O3`, system zlib/jpeg, Zopfli disabled.

The compressed JSON retains every included run’s observations, medians/variability, source and binary hashes, input/output identities and build/runtime metadata. Workspace paths and hostname use reproduction variables; numeric observations are unchanged. Read it with `gzip -dc benchmarks/results/2026-10-12-warm-comparison.json.gz`.

The original **whole-process** comparison on `35eb3fb` remains separate: managed medians 233–577 ms, native 3.5–29 ms, including fresh startup and file transport. [Raw process results](../benchmarks/results/2026-10-11-linux.json) retain its samples and commands. The original [managed-only warmed BenchmarkDotNet results](../benchmarks/results/2026-10-11-warmed.json) exclude disk transport and have no native ratio. Neither series is mixed into the both-warmed table above.
