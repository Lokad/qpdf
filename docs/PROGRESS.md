# Progress

Performance work targets single-CPU parity (≤1.0×) with pinned native qpdf 12.4.2
in thirteen codec-free structural workloads. The target remains open. The latest
qualified primary panel (`c29c5a3`) records five cells at or below 1.0×, with
ratios 0.58–3.55×. [Benchmarks](BENCHMARKS.md) gives current comparative figures
and reproducible commands. Keep changes that reduce time or allocation; no
minimum percentage applies.

Pushed `0210afd` passed [hosted CI run 38562169705](https://github.com/Lokad/qpdf/actions/runs/38562169705):
Windows 6m20s, Ubuntu 5m25s, pinned reference 4m47s. Each ordinary job passes
17,898 checks without failures/skips, locked Release build/pack, inspection,
fresh consumer and rejection controls. Reference covers 1,858 exact object
comparisons and 442 rewrite trios. Dependency pins and version are unchanged.

## Current correction

Mutable name tables now hold their active construction scope directly, removing
one temporary weak-reference wrapper per table and its target lookups. Freeze,
conversion and discard still clear the link; public dictionaries require frozen
storage. Completed immutable values/keys remain independent of execution state.
Owner validation, cancellation and storage admission retain their existing paths.

The original X100 paired probe allocates 669.534→661.882 KB/job (about 7.65 KB
less), with identical independently validated bytes. Medians 3.597→3.230 ms have
overlapping IQRs (3.302–3.613 / 3.116–3.318); retain allocation, with no confirmed
speedup. All 4,184 affected syntax/table/reader/selection checks pass (29s),
including frozen values after cleanup, conversion cancellation/failure, wrong
owners, collisions, warning/repair ordering and discarded-value cleanup.
All 26 fresh managed/native outcomes and three negative controls pass with
original inputs unchanged. Locked Release build/explicit pack/inspection/fresh
consumer/rejections pass (32s), preserving the 2 MiB multisource ceiling.

A separate raw-name hashing fast path was tested and restored: allocation was
essentially unchanged, and 2.135→2.122 ms had overlapping IQRs. Its focused
checks passed; all observations remain in the raw archive. No additional
benchmark cell, cache or accounting framework was introduced.

The earlier single-lookup dictionary insertion and bounded private file-host
selection remain integrated. The latter keeps complete-reader fallback on the
same lease before publication; public documents and memory-host jobs retain
their storage contracts. Its additional comparison stays separate from the
original thirteen memory-host cells and their unresolved full-input sparse cost.

## Preserved scope

Useful completed allocation, parser, copier and writer improvements remain in
place. Further work must name a concrete defect or finite verification task;
closed supported features and historical test counts add no obligations.
Source/artifact identities and all observations, including rejected trials,
remain in the [benchmark raw archives](BENCHMARKS.md).

The [supported profile](SUPPORTED-PROFILE.md) stays delivered. CMYK/YCCK JPEG
remains opaque with warnings, Zopfli is unavailable, and crypto including
empty-password inputs is excluded. These limitations are explicit and tested;
other [compatibility requirements](COMPATIBILITY.md) are not waived.

`MaxMemoryBytes` bounds approximate charged working storage. Admit significant
buffers, material growth/copy overlap and decompression/allocation amplification;
retain work limits, cancellation, host boundaries, cleanup and output integrity.
Unreleased charges do not prove retained memory. Exact CLR sizes, individual
small-object/metadata-alias receipts, fixed GC rounds and precise bookkeeping
charges remain withdrawn; see [governance](SUPPORTED-PROFILE.md#governance).
No CPU parallelism, forced production GC, parsed-job cache, runtime native bridge
or replacement accounting/benchmark framework was introduced.

Runtime pins and the five reviewed temporary ImageSharp 3.1.12 advisory exceptions
remain unchanged. No release-version change, tagging or package publication.
