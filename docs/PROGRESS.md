# Progress

Performance targets single-CPU parity (<=1.0x) with pinned native qpdf 12.4.2 in
thirteen codec-free public workloads. The target remains open. The latest full
panel (`2323d30`) records five cells at parity, with ratios 0.58-3.67x.
[Benchmarks](BENCHMARKS.md) gives current figures and reproducible commands.
Keep changes reducing time or allocation; no minimum percentage applies.

Pushed `c053add` passes [hosted CI run 38582982547](https://github.com/Lokad/qpdf/actions/runs/38582982547):
Windows 4m59s, Ubuntu 5m15s, reference 6m14s. Each ordinary platform passes 17,912
checks without failures/skips, locked Release build/pack, package inspection,
fresh consumer and rejection controls. Reference covers 1,858 exact object
comparisons and 442 rewrite trios. The combined correction below awaits hosted
qualification; pins, advisory exceptions and version are unchanged.

## Current correction

The copier's source-identity map caps lazy growth at validated xref cardinality
through the existing admission/growth helper. Duplicate destination pages keep
independent capacity. QDF xref-type and hint-filter checks compare raw decoded
name bytes and reuse fixed lookup names, removing presentation allocations.
The paired object-heavy job allocates 4.749187 ->4.714203 MB (35.0 KB less), with
identical independently valid bytes. Medians 26.998 ->25.510 ms overlap in IQR
25.217-27.045 /24.989-27.381; no confirmed speedup is claimed.

A six-second profile (174 fresh linearization jobs, every output hash checked)
identified repeated construction/sorting of already contiguous assigned xref
numbers. Both classic passes now emit numeric intervals directly. Offsets, hints,
IDs, output bytes and replay/publication checks remain. Allocation falls
19.393707 ->19.344938 MB/job (48.8 KB less); medians 23.029 ->21.348 ms overlap in
IQR 21.702-24.599 /20.877-31.644. Timing attribution remains unconfirmed.

All 2,991 focused copier/selection/merge/QDF/publication checks pass (15s), followed
by 3,075 linearization/writer/publication checks (25s). Both passes and classic/
fallback routes retain success/failure/cancellation coverage. Four tests observing
only the eliminated list wrappers were removed. Final-source fresh managed/native
validation passes all 26 outcomes and three negatives, with original inputs intact.
Locked Release build/explicit pack/inspection/fresh consumer/rejections pass (39s),
including the unchanged 2 MiB multisource ceiling. Seven visual cases and two
negative controls pass (21s) at unchanged tolerances, after timing.

Clean-source panels retain 25,318 primary and 6,870 additional file-host samples.
Sparse full-input selection is 3.6723x; the separate ranged file-host cell is
0.3762x and replaces no primary comparison. All 125 raw runs, including the
intermediate source panel and rejected trials, are retained. Different panels
under variable load do not establish causal speedups.

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
