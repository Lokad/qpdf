# Progress

Performance work targets single-CPU parity (≤1.0×) with pinned native qpdf 12.4.2
in thirteen codec-free structural workloads. The target remains open. The latest
qualified primary panel (`9767caa`) records five cells at or below 1.0×, with
ratios 0.59–3.54×. [Benchmarks](BENCHMARKS.md) gives current comparative figures
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
Clean-source comparisons retain all 27,289 primary and 6,638 additional file-host
observations, fresh independent semantics and original inputs. Five primary cells
meet parity; sparse full-input selection remains 3.54×, additional file-host
selection 0.401×. Separate panels establish no timing attribution.

A separate raw-name hashing fast path was tested and restored: allocation was
essentially unchanged, and 2.135→2.122 ms had overlapping IQRs. Its focused
checks passed; all observations remain in the raw archive. No additional
benchmark cell, cache or accounting framework was introduced. Two existing six-second
profiles verify every fresh output hash: ID rewriting points to writing/hashing;
object-heavy rewriting points to parsing. Traces and summaries are in the raw
archive. Sampled GC polling intervals are not exact CPU or explicit GC calls.

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
