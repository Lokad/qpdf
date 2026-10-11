# Progress

The agreed [supported profile](SUPPORTED-PROFILE.md) is qualified on committed
implementation `34c8097` (2026-10-11). No implementation or verification tasks
remain recorded for that profile. Hosted CI passes on `7bc2fc8`:
[run 38154808432](https://github.com/Lokad/qpdf/actions/runs/38154808432), with
Windows 7m01s, Ubuntu 5m26s and pinned qpdf 12.4.2 reference 3m45s. Both ordinary
jobs passed 17,874 tests with no failures/skips, package inspection, fresh locked
consumers and both rejection controls; logs/TRX/reference reports were uploaded.
Tagging and package publication remain separate actions.

Four-component CMYK/YCCK JPEGs remain opaque with warnings when decoding is
requested; Zopfli is explicitly unavailable. Both limitations are tested.
Other unmet original requirements remain documented in the
[compatibility inventory](COMPATIBILITY.md); qualification does not waive another
required compatibility mismatch or create a second blocking verdict.

## Qualification

Both platforms used .NET SDK 10.0.401/runtime 10.0.12. Locked restores and Release
builds passed with zero warnings/errors. Ordinary suites passed with no failures
or skips. Packages passed inspection, fresh locked-consumer execution and the
wrong-package-version/corrupt-external-hash rejection controls on both platforms.

| Check | Platform | Wall time | Result |
| --- | --- | ---: | --- |
| Ordinary suite | Windows | 7m39s | Passed |
| Ordinary suite | Ubuntu 24.04 WSL2 | 7m11s | Passed |
| Complete pinned qpdf 12.4.2 reference | Windows | 6m58s | Passed; 1858 object comparisons, 442 rewrite trios |
| Seven-case visual comparison | Windows | 24s | Passed, including both deliberate output-defect controls |

The reference run retains the original inputs, parser comparisons, limits and
negative controls, with bounded recovery additions. Its original 43m41s wall
time is now 6m58s: parser approximately 33m49s → 5m25s, differential 9m42s →
1m26s. Windows ordinary tests improved from the recorded 33.8-minute baseline
to 7m39s. No parallel verification workers or production forced GC were added.

Practical limits, allocation/decompression amplification protection, cancellation,
exceptional cleanup and surviving output aliases pass their existing controls.
Five representative normal/adversarial memory workload families pass, including
the original configuration fan-out failures and repeated hint encoding under
4 KiB. `MaxMemoryBytes` bounds a charged working-set estimate; unreleased charges
do not establish retained memory. Exact CLR sizes, small-object/metadata-alias
receipts, fixed GC retirement and precise bookkeeping accounting are withdrawn
release obligations. The [governance contract](SUPPORTED-PROFILE.md#governance)
and [development guide](DEVELOPMENT.md#focused-verification-and-memory-estimates)
describe the retained safeguards and representative checks.

Detailed local source/artifact identities, commands and phase results are in
`external/reviews/2026-10-11-delivery/c84059d7915a41fc91d488d5a9d66dc2/result.json`
(ignored run evidence). The Windows and Ubuntu packages were qualified from the
same implementation source. Subsequent documentation, assets and initial
benchmark tools preserved it. The performance corrections below require a new
integration checkpoint to qualify their final source/package.

## Continuing work

The initial finite Linux comparison on `35eb3fb` is recorded in
[the benchmark report](BENCHMARK-RESULTS.md). Its process and managed-only warmed
series remain separate. Subsequent complete-job measurements warm both engines,
using pinned native qpdf 12.4.2, fresh public jobs and validated outputs. Startup,
protocol and validation are excluded; necessary host/file transport is included.
There are no timing gates in CI.

Performance work targets at most 2× native on nine structural workloads. The
last complete rerank (`284af7a`) validated all nine cells: large and small page
selection meet the target; ordinary rewriting had the largest gap (3.57×).
Other gaps range from 2.11× for splitting to 3.00× for structural checking.
Later paired whole-file source reuse improves rewriting 67.85 → 54.59 ms (19.6%),
with native 17.98 ms (3.04×). Allocation falls 73.58 → 61.40 MB/job (16.6%).
The initial noisier pair showed 13.1%; both observations are retained. Source
reuse preserves labels/versions and closes inputs before publication, including
split, replace-input and pass1. No parsed document is cached between jobs.

| Integrated correction | Evidence |
| --- | --- |
| Remove quadratic thumbnail exclusion | Linearization 155.03 → 115.67 ms (25.4%); thumbnail placement, classification, cancellation/limits and work-growth checks pass. |
| Transfer fresh parser stream buffers; write memory-host spans | Rewriting 107.80 → 93.56 ms (13.2%); allocation falls 11.8%; recovery, aliases, transaction failures and active cancellation pass. |
| Remove name/container/parser scratch allocations | Owned name bytes, one display string, concrete dictionary enumeration, known-size arrays and direct decoded buffers; grammar, ordering/null semantics, mutation isolation and decoded-storage limits pass. |
| Use exclusive ownership for private complete-job sources | X100 rewriting 13.54 → 7.60 ms (43.9%); allocation falls 14.1%; versions, limits, cleanup and surviving outputs pass. GC rates are mixed across generations. |
| Reuse immutable graph stream payloads | About 5.13 MB/job less allocation on M500 selection/rewrite; fresh outputs match previous managed bytes; full payloads survive host collection. Timings show no reliable speedup. |
| Reuse the whole-file source in the shared selection core | Paired improvement above; 3,326 existing focused checks and 42 writer-control cases pass, including six source/publication regressions. |
| Reuse complete owned host-read arrays | 554 focused host, reader, configuration and practical-memory checks pass. Short reads, slices and non-array memory retain the assembly/copy fallback. |
| Compare reader page types as raw bytes | 815 focused checks pass, including escaped names, empty catalog types, effective versions and page selection. |
| Preallocate admitted memory-host staging from the counted output size | 2,414 focused checks pass, including failed capacity admission preserving prior output and closing the transaction. Rewrite allocation falls 56.12 → 44.56 MB/job (20.6%); output bytes are unchanged. Timing is noisy (61.35 → 66.11 ms), with no established speedup. |
| Borrow fixed writer syntax through sink awaits | 344 focused writer, object-stream, QDF, linearization, memory and active-cancellation checks pass. Shared immutable literals eliminate per-space/delimiter/frame buffers and scopes; variable tokens retain their existing encoding path. |
| Keep private checker graphs under document ownership | Interleaved check-X100 improves 16.27 → 7.54 ms (53.7%); allocation falls 1.50 → 0.99 MB/job (34.1%). 3,516 focused checks passed, then all 125 affected checker/control checks passed after correcting the new version-survival assertion and withdrawing small-fixture accounting exhaustion requirements. Compressed expansion still rejects at unchanged ceilings; public documents and surviving metadata remain independently owned. |
| Transfer complete private staging at publication | Rewrite allocation falls 42.35 → 37.13 MB/job (12.3%); identical independently validated output. Timings overlap (53.85 → 50.46 ms), with no decisive speedup. 2,414 focused checks passed, then all 1,551 affected cleanup controls passed after removing duplicate batches that required metadata/accounting retirement in four GC rounds. Public failure/cancellation/limits, prior outputs, mutation isolation and representative material-memory checks remain. |

Straightforward structural copy/allocation eliminations have correctness/resource
checks and no empirical timing acceptance gate. Larger performance hypotheses
remain profile-driven; paired public-workload timing and allocation observations
are retained without claiming gains from noisy samples. The same small
representative normal/adversarial memory families remain the practical target.
Small jobs that now fit the unchanged 16 KiB ceiling must publish valid output;
material-memory and work exhaustion still reject and clean up.

The final combined performance tree still needs nine-cell reranking and
comprehensive ordinary/reference/visual/package-consumer qualification at the
completed integration checkpoint. The overall 2× target remains unmet. Remaining
work focuses on copying/GC pressure and serialization, with bounded profiles and
focused checks before each correction. This adds no feature or accounting audit.

Use the [supported profile](SUPPORTED-PROFILE.md) for shipped behavior,
[architecture](../ARCHITECTURE.md) for implementation boundaries and the
[development guide](DEVELOPMENT.md) for checks and the five intentionally
suppressed ImageSharp 3.1.12 advisories. Choose new work by a concrete public
defect or an explicitly agreed feature. Run affected checks while correcting it,
then comprehensive qualification at integration/release checkpoints. Historical
test counts, checkpoint narratives and superseded audit ladders add no tasks;
Git retains that history.
