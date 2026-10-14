# Progress

Performance targets single-CPU parity (<=1.0x) with pinned native qpdf 12.4.2 in
thirteen codec-free public workloads. The target remains open. The latest full
panel (`bf03d9a`) records five cells at parity, with ratios 0.61-3.47x.
[Benchmarks](BENCHMARKS.md) gives current figures and reproducible commands.
Keep reduced time or allocation; no minimum percentage applies.

Pushed `d67be39` passes [hosted CI run 38620390134](https://github.com/Lokad/qpdf/actions/runs/38620390134):
Windows 7m14s, Ubuntu 5m57s, reference 6m12s. Both ordinary platforms pass 17,918
checks without failures/skips, locked Release build/pack, inspection, fresh
consumer and rejection controls. Reference covers 1,858 exact object comparisons
and 442 rewrite trios. Pins, advisory exceptions and version remain unchanged.

## Current correction

Compressed-object source inventories are skipped when output explicitly disables
or regenerates object streams. Preserve/default modes keep their existing scans;
attachment copies use the same decision. Validation order, forced-version behavior
and source maps remain. All 3,557 affected object-stream/selection/attachment/
version/publication checks pass (39s).

The original 500-page rewrite pair against `d67be39` has identical independently
valid output. Median 20.022 ->18.804 ms has overlapping IQRs 17.980-22.420 /
17.347-31.160; no speedup is claimed. Allocation 17.180594 ->17.180484 MB/job is
essentially unchanged for this classic input. The unused traversal is removed
without a percentage gate. Full observations, including slow batches, remain.

This local correction will share the comprehensive checkpoint with the bounded
immutable-payload staging experiment. Existing public output copies, cancellation,
cleanup and proportional retained-storage guarantees remain required. Earlier
completed corrections, rejected writer-wrapper trial and raw evidence stay intact.
## Preserved scope

The [supported profile](SUPPORTED-PROFILE.md) stays delivered. CMYK/YCCK JPEG
remains opaque with warnings, Zopfli is unavailable, and crypto including
empty-password inputs is excluded. Other [compatibility requirements](COMPATIBILITY.md)
are not waived. Closed features and historical test counts add no obligations.

`MaxMemoryBytes` bounds approximate charged working storage. Admit significant
buffers, growth/copy overlap and decompression/allocation amplification; preserve
work limits, cancellation, host boundaries, cleanup, output integrity, public
immutability and surviving aliases. Keep the unchanged 2 MiB multisource consumer
ceiling. Unreleased charges do not prove retained memory. Exact CLR sizes,
individual small-object/metadata-alias receipts, fixed GC rounds and precise
bookkeeping charges remain withdrawn; see [governance](SUPPORTED-PROFILE.md#governance).

No CPU parallelism, forced production GC, parsed-job cache, runtime native bridge,
new accounting/benchmark framework, dependency/version change, tagging or package
publication. The five reviewed temporary ImageSharp 3.1.12 exceptions remain.
