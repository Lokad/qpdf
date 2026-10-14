# Progress

Performance targets single-CPU parity (<=1.0x) with pinned native qpdf 12.4.2 in
thirteen codec-free public workloads. The target remains open. The latest full
panel (`bf03d9a`) records five cells at parity, with ratios 0.61-3.47x.
[Benchmarks](BENCHMARKS.md) gives current figures and reproducible commands.
Keep reduced time or allocation; no minimum percentage applies.

Pushed `f777881` passes [hosted CI run 38615811907](https://github.com/Lokad/qpdf/actions/runs/38615811907):
Windows 5m05s, Ubuntu 6m00s, reference 5m40s. Both ordinary platforms pass 17,918
checks without failures/skips, locked Release build/pack, inspection, fresh
consumer and rejection controls. Reference covers 1,858 exact object comparisons
and 442 rewrite trios. Pins, advisory exceptions and version remain unchanged.

## Current correction

Single/multi-source selection resolves the existing pruning heuristic once and
creates page-ID projection lists only when a content transform will run. Ordinary
copying with transforms disabled avoids unused lists. Auto/yes/no pruning,
coalescing, normalization and inline-image behavior keep their shared pipeline.

All 3,638 affected selection/merge/split/content/pruning/rotation/inspection/
overlay/appearance/flattening/publication checks pass (24s). The original
500-page paired rewrite allocates 17.181247 ->17.180683 MB/job (0.6 KB less), with
byte-identical independently valid output. Median 16.708 ->16.980 ms has overlapping
IQRs 16.268-17.083 /16.258-18.418; native 14.461 ms. Retain the structural allocation
reduction; no speedup is claimed.

An async-wrapper removal trial passed 2,105 writer checks but supplied no qualifying
gain and was reverted; its patch and all observations remain preserved. A single
six-second profile of the restored green source places 24.5% of sampled job
intervals in memory copies and 20.5% in GC waits; these are diagnostic observations,
not managed/native speed ratios. Public output copies and staging dominate the
copy stacks. Larger ownership changes need bounded retention and alias guarantees.

Locked Release restore/build/explicit pack/inspection/fresh consumer/rejection
checks pass (22s), preserving the unchanged 2 MiB multisource ceiling. All 26 fresh
managed/native outcomes and three negative controls pass with unchanged inputs.
The clean committed-source panel retains 23,507 observations; ordinary rewrite is
1.2417x and original sparse selection 3.4732x. The separate file-host case is
0.3747x. Seven visual cases and both negatives pass (29s) outside timing with
unchanged tolerances. Hosted qualification follows. Earlier useful corrections,
native interaction controls and all raw runs remain; separate panels do not
establish source-change attribution.
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
