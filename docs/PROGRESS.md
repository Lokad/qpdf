# Progress

Performance targets single-CPU parity (<=1.0x) with pinned native qpdf 12.4.2 in
thirteen codec-free public workloads. The target remains open. The latest full
panel (`a06e339`) records five cells at parity, with ratios 0.58-3.35x.
[Benchmarks](BENCHMARKS.md) gives current figures and reproducible commands.
Keep changes reducing time or allocation; no minimum percentage applies.

Pushed `f69ff7f` passes [hosted CI run 38588326945](https://github.com/Lokad/qpdf/actions/runs/38588326945):
Windows 6m38s, Ubuntu 6m26s, reference 5m58s. Both ordinary platforms pass 17,908
checks without failures/skips, locked Release build/pack, inspection, fresh
consumer and rejection controls. Reference covers 1,858 exact object comparisons
and 442 rewrite trios. Pins, advisory exceptions and version remain unchanged.

## Current correction

Hint construction filters page users to actual shared groups before sorting,
then maps the same list in place. Group membership uses source identities, whose
assigned numbers are unique. Retained capacity follows the filtered count;
unused private IDs cannot leave oversized arrays alive. Page/outline lengths
use existing source runs and mapped endpoints instead of temporary remapped lists.
The hint format, ordering and output bytes remain unchanged.

The paired original linearization job allocates 19.345465 ->19.225105 MB
(120.4 KB less), with byte-identical independently valid output. Median time
22.551 ->23.593 ms has overlapping IQRs 22.469-32.524 /22.889-25.023, so no
confirmed speedup is claimed; ratio 1.0832x remains above parity.

All 3,239 focused hint/linearization/publication checks pass (27s). Two new public
jobs cover spaced live source IDs, a shared font and many private objects, with
classic/generated object streams, independent page/text checks and lease cleanup.
All 26 fresh managed/native outcomes and three negative controls pass, with
original inputs unchanged. Locked Release build/explicit pack/inspection/fresh
consumer/rejections pass (30s), preserving the 2 MiB multisource ceiling. Seven
visual cases and two negative controls pass (18s) at unchanged tolerances, after
timing. Hosted qualification is pending.

Earlier parser scratch, approximate ownership, immutable borrowing, source/cache
capacity reductions, staged content-ID hashing and direct classic xref ranges
remain integrated. All 128 raw runs are preserved. The latest full panels contain
27,111 primary and 6,731 additional file-host samples. Sparse full-input selection
remains 3.3476x; the separate file-host cell is 0.3791x and replaces no primary case.
Different full panels do not establish causal speedups.

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
