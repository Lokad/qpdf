# Progress

Performance work targets single-CPU parity (<=1.0x) with pinned native qpdf 12.4.2
in thirteen codec-free public workloads. The target remains open. The latest full
panel (`f89d242`) records four cells at parity, with ratios 0.57-3.50x.
[Benchmarks](BENCHMARKS.md) gives current comparisons and reproducible commands.
Keep changes reducing time or allocation; no minimum percentage applies.

Pushed `c053add` passes [hosted CI run 38582982547](https://github.com/Lokad/qpdf/actions/runs/38582982547):
Windows 4m59s, Ubuntu 5m15s, pinned reference 6m14s. Each ordinary platform passes
17,912 checks without failures/skips, locked Release build/explicit pack,
inspection, fresh consumer and rejection controls. Reference includes 1,858 exact
object comparisons and 442 rewrite trios. Pins, advisory exceptions and version
are unchanged.

## Current correction

The copier's source-identity map now caps geometric growth at validated source
xref cardinality through the existing container helper. It remains lazy, admits
allocation/growth overlap and preserves tight-budget fallback. Duplicate pages
use the separate destination map and retain independent growth. QDF xref-type
and linearization filter checks compare decoded name bytes directly and reuse
fixed lookup names, removing display-string and lookup-key allocations.

The paired original object-heavy workload records allocation
4.749187 ->4.714203 MB/job, about 35.0 KB less, with identical independently valid
output bytes. Medians 26.998 ->25.510 ms have overlapping IQRs
25.217-27.045 /24.989-27.381, so no confirmed speedup is claimed. Its ratio 1.032x
remains above parity. All 2,991 focused copier/selection/merge/QDF/linearization/
publication checks pass (15s), including duplicate destinations and cleanup.

A six-second profile of 174 fresh linearization jobs identified unnecessary xref
number-list construction and sorting. Classic numbering already forms contiguous
intervals; both passes now emit those intervals directly. Offset checks, hints,
IDs, layout and publication replay remain. Allocation falls
19.393707 ->19.344938 MB/job (48.8 KB less), with byte-identical validated output.
Medians 23.029 ->21.348 ms overlap in IQR (21.702-24.599 /20.877-31.644), so no
confirmed speedup is claimed; ratio 1.072x remains above parity.

All 3,075 focused linearization/writer/publication checks pass (25s). Both passes
and the classic/fallback routes retain success/failure/cancellation coverage.
Four tests observing only the eliminated list wrappers were removed. Integration
qualification of the combined batch is pending.

The preceding reader-cache growth cap, parser scratch, scoped ownership,
immutable payload borrowing and staged content-ID hashing remain integrated.
Existing raw evidence retains all 119 runs, including rejected trials. The latest
full panels contain 24,459 primary and 4,549 additional file-host observations.
Original sparse full-input selection remains 3.4995x; the separate ranged file-host
cell is 0.3998x and does not replace any primary comparison.

## Preserved scope

The [supported profile](SUPPORTED-PROFILE.md) stays delivered. CMYK/YCCK JPEG
remains opaque with warnings, Zopfli is unavailable, and crypto including
empty-password inputs is excluded. Other [compatibility requirements](COMPATIBILITY.md)
are not waived. Closed features and historical test counts add no obligations.

`MaxMemoryBytes` bounds approximate charged working storage. Admit significant
buffers, growth/copy overlap and decompression/allocation amplification; preserve
work limits, cancellation, host boundaries, cleanup, output integrity, public
immutability and surviving aliases. Preserve the 2 MiB multisource consumer
ceiling. Unreleased charges do not prove retained memory. Exact CLR sizes,
individual small-object/metadata-alias receipts, fixed GC rounds and precise
bookkeeping charges remain withdrawn; see [governance](SUPPORTED-PROFILE.md#governance).

No CPU parallelism, forced production GC, parsed-job cache, runtime native bridge,
new accounting/benchmark framework, dependency/version change, tagging or package
publication. The five reviewed temporary ImageSharp 3.1.12 exceptions remain.
