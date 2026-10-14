# Progress

Performance work targets single-CPU parity (<=1.0x) with pinned native qpdf 12.4.2
in thirteen codec-free public workloads. The target remains open. The latest full
panel (`f89d242`) records four cells at parity, with ratios 0.57-3.50x.
[Benchmarks](BENCHMARKS.md) gives current comparisons and reproducible commands.
Keep changes reducing time or allocation; no minimum percentage applies.

Pushed `086cd58` passes [hosted CI run 38577278896](https://github.com/Lokad/qpdf/actions/runs/38577278896):
Windows 5m29s, Ubuntu 5m43s, pinned reference 5m48s. Each ordinary platform passes
17,909 checks without failures/skips, locked Release build/explicit pack,
inspection, fresh consumer and rejection controls. Reference includes 1,858 exact
object comparisons and 442 rewrite trios. Pins, advisory exceptions and version
are unchanged.

## Current correction

The object cache still grows lazily, but validated xref cardinality now caps
geometric capacity overshoot. The existing container helper retains admission
before allocation, growth overlap and tight-budget fallback. It can grow beyond
a stale cardinality hint without losing accepted entries. No eager cache fill,
parsed-document reuse or new ownership machinery.

A four-second allocation trace on the qualified binary identifies cache-entry
arrays among material allocations (140 fresh object-heavy rewrites, every output
hash checked). The paired original object-heavy workload records allocation
4.824324 ->4.748851 MB/job, about 75.5 KB less, with identical independently valid
output bytes. Medians 27.265 ->26.148 ms have overlapping IQRs
26.477-28.167 /26.008-27.514, so no confirmed speedup is claimed. Its ratio 1.041x
remains above parity. All observations remain in the raw evidence.

All 8,067 final reader/parser/writer/publication checks pass (78s), including
normal/tight-budget growth beyond an initial cardinality hint. All 26 fresh
managed/native outcomes and three negative controls pass, inputs unchanged.
Locked Release build/explicit pack/inspection/fresh consumer/rejections pass (21s),
preserving the 2 MiB multisource ceiling. Seven visual cases and two negative
controls pass (21s) at unchanged tolerances; codecs/rendering are outside timing.

The clean-source panels retain 24,459 primary and 4,549 additional file-host
observations. Four original cells meet parity; sparse full-input selection stays
3.4995x, while the separate file-host cell is 0.3998x. Broad object-stream and
linearization timings prompted one paired check: it shows overlapping revision
distributions, lower allocation and unchanged bytes, without a demonstrated
source-related slowdown. The complete panel is retained without substitution.
Hosted qualification of this correction is pending.

The bounded async-wrapper and dense-xref-count trials had overlapping timings and
unchanged allocations; both were reverted. Their measurements remain available.
Existing parser scratch, scoped ownership, immutable payload borrowing, staged
content-ID hashing and writer allocation improvements remain integrated.

## Preserved scope

The [supported profile](SUPPORTED-PROFILE.md) stays delivered. CMYK/YCCK JPEG
remains opaque with warnings, Zopfli is unavailable, and crypto including
empty-password inputs is excluded. Other [compatibility requirements](COMPATIBILITY.md)
are not waived. Closed features and historical test counts add no obligations.

`MaxMemoryBytes` bounds approximate charged working storage. Admit significant
buffers, growth/copy overlap and decompression/allocation amplification; preserve
work limits, cancellation, host boundaries, cleanup, output integrity, public
immutability and surviving aliases. Unreleased charges do not prove retained
memory. Exact CLR sizes, individual small-object/metadata-alias receipts, fixed
GC rounds and precise bookkeeping charges remain withdrawn; see
[governance](SUPPORTED-PROFILE.md#governance).

No CPU parallelism, forced production GC, parsed-job cache, runtime native bridge,
new accounting/benchmark framework, dependency/version change, tagging or package
publication. The five reviewed temporary ImageSharp 3.1.12 exceptions remain.
