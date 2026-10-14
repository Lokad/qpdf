# Progress

Performance targets single-CPU parity (<=1.0x) with pinned native qpdf 12.4.2 in
thirteen codec-free public workloads. The target remains open. The latest full
panel (`ead62b0`) records five cells at parity, with ratios 0.60-3.60x.
[Benchmarks](BENCHMARKS.md) gives current figures and reproducible commands.
Keep reduced time or allocation; no minimum percentage applies.

Pushed `dee6921` passes [hosted CI run 38612060274](https://github.com/Lokad/qpdf/actions/runs/38612060274):
Windows 6m59s, Ubuntu 5m56s, reference 5m56s. Both ordinary platforms pass 17,918
checks without failures/skips, locked Release build/pack, inspection, fresh
consumer and rejection controls. Reference covers 1,858 exact object comparisons
and 442 rewrite trios. Pins, advisory exceptions and version remain unchanged.

## Current correction

Single/multi-source publication models enumerate concrete pool entries directly,
then sort their numbered records in place with the existing governed numeric
sorter. Catalogue and page-tree entries stay first. This removes temporary key
lists and repeated dictionary lookups while retaining capacity admission,
work/cancellation checks and output order. Earlier source-record/capacity and
coalescing-plus-rotation corrections remain integrated.

The original 500-page paired rewrite allocates 17.186530 ->17.181110 MB/job
(5.4 KB less), with byte-identical independently valid output. Median time
42.304 ->51.447 ms has overlapping IQRs 38.894-50.698 /37.440-58.706; native is
30.413 ms. The slower candidate observation remains preserved; no speedup is
claimed. Retain the structural allocation reduction, with no percentage gate.
Charged estimates are not retained-memory readings.

All 5,634 focused selection/merge/split/rotation/content/inspection/overlay/
appearance/flattening/publication checks pass (74s), including failure,
cancellation, cleanup, surviving outputs and the prior rotation regressions.
Locked Release restore/build/explicit pack/inspection/fresh consumer/rejection
checks pass (17s), preserving the unchanged 2 MiB multisource ceiling. All 26
fresh managed/native outcomes and three negatives pass with unchanged inputs.
The final clean panel retains 26,858 observations; ordinary rewrite is 1.2171x
and original sparse selection 3.6037x. The separate file-host case is 0.3890x.
An initial panel overlapped an independently observed compiler/test workload;
it remains preserved, with one replacement after that pipeline ended. Seven
visual cases and both negatives pass (17s) outside timed phases at unchanged
tolerances. Hosted qualification follows. Previous raw runs and native interaction
controls remain preserved; separate panels do not establish attribution.

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
