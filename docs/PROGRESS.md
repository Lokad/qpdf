# Progress

Performance targets single-CPU parity (<=1.0x) with pinned native qpdf 12.4.2 in
thirteen codec-free public workloads. The target remains open. The latest full
panel (`f43d48e`) records five cells at parity, with ratios 0.57-3.56x.
[Benchmarks](BENCHMARKS.md) gives current figures and reproducible commands.
Keep reduced time or allocation; no minimum percentage applies.

Pushed `444cd06` passes [hosted CI run 38608289834](https://github.com/Lokad/qpdf/actions/runs/38608289834):
Windows 6m32s, Ubuntu 6m09s, reference 5m49s. Both ordinary platforms pass 17,918
checks without failures/skips, locked Release build/pack, inspection, fresh
consumer and rejection controls. Reference covers 1,858 exact object comparisons
and 442 rewrite trios. Pins, advisory exceptions and version remain unchanged.

## Current correction

Per-source selection records keep copier, destination ID and source page number;
they no longer retain unused page-dictionary snapshots. Inspection and actual
rotation snapshots retain their existing behavior. Single/multi-source numbered
model lists allocate once from the validated pool count, with a coarse capacity
reservation before allocation, avoiding geometric growth and copying.

The original object-heavy paired rewrite allocates 4.641642 ->4.544763 MB/job
(96.9 KB less), with byte-identical independently valid output. Median time
26.403 ->25.968 ms has overlapping IQRs 25.439-27.898 /25.272-26.288, so no speedup
is claimed; ratio 1.0125x remains above parity. Charged capacity can increase even
when actual allocation falls; charge snapshots are not retained-memory readings.

All 5,634 focused selection/merge/split/rotation/content/inspection/overlay/
appearance/flattening/publication checks pass (70s), including the coalescing-plus-
rotation regressions, failure, cancellation, cleanup and surviving outputs.
Locked Release build/explicit pack/inspection/fresh consumer/rejections pass
(21s) on these final source contents, preserving the 2 MiB multisource ceiling.
All 26 fresh managed/native outcomes and three negative controls pass with
unchanged original inputs. The clean panel preserves 26,818 primary observations.
Sparse full-input selection remains 3.5593x; the separate file-host case is
0.3830x and replaces no original cell. Seven visual cases and two negative
controls pass (19s) after timing at unchanged tolerances. Hosted qualification
is pending. Earlier corrections, native interaction controls and raw runs remain
preserved; separate full panels do not establish source-change attribution.

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
