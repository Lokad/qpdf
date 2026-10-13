# Linux benchmark comparison — 2026-10-12

The current goal is single-CPU parity (≤1.0×) in every agreed structural workload;
it remains open. The clean `8f69bb7` panel meets parity in three of thirteen
codec-free cells. Ten gaps remain, including sparse selection at
7.17 / 2.22 ms (3.23×). Content-ID and streamed rewriting are 1.68× / 1.62×.
Fresh semantics and every original input are preserved;
differing load prevents attributing separate-panel times to source changes.
Required full-input transport remains timed; the managed reader receives all
16.8 MB while native can read selected objects lazily.

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
Geometric dictionary/set growth subsequently removes 1.041 MB/job from
object-heavy rewriting (10.223 → 9.183 MB), preserving hashes. Medians
44.91 → 44.21 ms overlap. The paired prototype precedes the final admission
fallback, which preserves unchanged tight-memory consumers before allocation;
final-source functional/consumer checks cover that fallback.
Selective numeric/reference lookahead fixes a public large-string work-limit
failure while retaining repair offsets and grammar. Canonical single-digit text
also reuses immutable literals. Its same-driver object-heavy pair removes about
54 KB/job (9.184 → 9.130 MB); output hashes match. Medians 44.54 → 44.97 ms overlap,
so claim allocation only. One generated large-string diagnostic has medians
38.34 → 30.29 ms with overlapping IQRs; it supplies no decisive time win or extra
parity cell. The generated input/manifest are embedded in the raw archive for
reproduction with the existing warm runner. Rejected retirement-read and earlier
numeric prototypes retain their raw observations; no losing samples are removed.

Private dictionary finalization reuses existing bucket/hash chains at unchanged
capacity, retaining the copy path for larger tables and repair receipts.
The identical-driver object-heavy pair removes about 119 KB/job (9.129 → 9.010 MB),
with unchanged hashes. Median times 74.94 → 49.41 ms have broad overlapping IQRs;
claim allocation only. This pair adds no workload or timing acceptance threshold.

Decoder-local parser reuse removes another 14 KB/job from streamed rewriting
(941.8 → 927.7 KB), preserving output hashes and independent member repair/cache
scopes. Identical-driver times 3.91 → 3.85 ms overlap; claim allocation only.

The `e2451c5` production checkpoint passes fresh semantics, Ubuntu qualification
and the pinned reference lane. Windows exposed a sleeping cancellation-test
observer that missed eight completed copies. Its correction changes test polling
only, retaining actual cancellation and cleanup requirements; production sources
and benchmark inputs/options stay unchanged. The identified final panel below
remains valid performance evidence; complete ordinary qualification is recorded
separately in [progress](PROGRESS.md).

Deferring cached dictionary key-view creation until `Keys` is requested removes
about 106 KB/job from object-heavy rewriting (9.009 → 8.902 MB), preserving output
hashes, key order and generic/nongeneric enumeration. Identical-driver times
56.20 → 52.11 ms overlap; claim allocation only. Significant-storage admission,
work, cancellation and immutable surviving aliases retain their existing behavior.

Classic object loading reuses one idle parser/tokenizer per document, keeping
active nested loads separate. Per-object error/borrowed observations and cursor
scan work reset; disposal drops the cached input reference. The identical-driver
object-heavy pair removes about 423 KB/job (8.902 → 8.479 MB), preserving hashes.
Median times 47.18 → 52.90 ms overlap; claim allocation only. The earlier
parser-only pair removes about 302 KB and remains identified as an intermediate
candidate. Existing limits, cache ownership, cancellation and public aliases
retain their behavior; this adds no workload, parsed-job cache or global pool.

The developer file adapter removes redundant clearing from known-length input
reads, using the same direct-handle operations as the
[pinned .NET 10.0.12 reader](https://github.com/dotnet/runtime/blob/v10.0.12/src/libraries/System.Private.CoreLib/src/System/IO/File.cs#L637).
It fills a fresh buffer completely before host transfer, loops over short reads,
rejects premature EOF and handles unknown/zero lengths through stream copying.
Full original input reads and public output copies remain timed. With identical
compiled libraries and intentionally different adapters, sparse medians fall
6.54 → 5.96 ms (8.9%); IQRs 6.45–6.85 and 5.95–6.05 ms are separated. Output
hashes match and allocations are effectively unchanged. This is a transport gain;
the native warmed median is 1.79 ms, so parity remains unmet. The unretained
FileStream trial (6.85 → 7.84 ms, overlapping IQRs) remains in the raw archive.

Synchronous hash and built-in memory-host destinations consume bounded batching
scratch directly. Only destinations advertising that capability borrow spans;
retaining/custom hosts receive independent immutable packets. Governed host-operation
admission, output-byte charging, limits and cancellation remain. An identical-driver
content-ID pair removes about 238 KB/job (18.906 → 18.667 MB), with matching hashes
and medians 36.324 → 32.694 ms. IQRs 35.175–37.548 and 32.564–35.062 ms are
separated. Native is 26.667 ms (1.23×), so parity remains open. Streamed allocation
falls 898.0 → 873.7 KB/job; times 4.95 → 3.58 ms overlap, so claim allocation only.
The intermediate hash-only pair (119 KB less) remains identified. No digest
algorithm, bytes, global buffer pool or public API changed. Existing retention/
publication controls and fresh independent outputs pass.

[Parity progress raw observations](../benchmarks/results/2026-10-12-parity-progress.json.gz)
preserve the thirteen-cell baseline, final source confirmation, correction/transport pairs and separate
managed-only memory diagnostics. Every sample and summary number is retained;
local paths/hostnames are redacted. The diagnostic compares memory-host Copy and
immutable Transfer: 2.68 / 1.14 ms medians, 16.26 MiB / 247 KiB allocation. It
supplies no native ratio and does not replace the complete file-job comparison.

## Current thirteen-cell checkpoint

Clean `8f69bb7`, seven sequential alternating 350 ms batches per engine after
warmup; medians in milliseconds, Q1–Q3 in parentheses. All 20,791 observations
are retained. Variable host load broadens these distributions; separate panels
do not isolate individual changes. Earlier panels remain in the raw archive.

| Workload | Managed ms (Q1–Q3) | Native ms (Q1–Q3) | M/N |
| --- | ---: | ---: | ---: |
| select-S10 | 0.62 (0.58–0.65) | 0.82 (0.78–0.83) | 0.76× |
| select-M500 | 21.60 (21.25–24.99) | 23.01 (22.05–23.82) | 0.94× |
| merge-250x2 | 28.33 (27.22–28.42) | 20.58 (19.53–21.77) | 1.38× |
| split-M500 | 35.12 (32.77–43.34) | 29.44 (27.78–29.78) | 1.19× |
| rewrite-M500 | 38.70 (31.26–55.75) | 25.18 (22.65–39.65) | 1.54× |
| rewrite-X100 | 3.41 (3.19–4.09) | 2.11 (2.05–2.22) | 1.62× |
| object-streams-X100 | 3.16 (3.01–3.18) | 2.61 (2.50–2.68) | 1.21× |
| linearize-M500 | 32.96 (31.05–42.12) | 22.37 (22.02–23.35) | 1.47× |
| check-X100 | 2.84 (2.61–3.58) | 2.56 (2.41–2.85) | 1.11× |
| rewrite-ID-M500 | 51.93 (44.42–78.10) | 30.96 (30.80–51.09) | 1.68× |
| rewrite-objects | 51.81 (48.08–56.80) | 36.80 (34.28–37.36) | 1.41× |
| select-sparse | 7.17 (7.01–7.25) | 2.22 (2.13–2.32) | 3.23× |
| split-single | 7.08 (7.06–8.01) | 7.32 (7.29–7.42) | 0.97× |

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
