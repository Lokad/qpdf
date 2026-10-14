# Progress

Performance targets single-CPU parity (<=1.0x) with pinned native qpdf 12.4.2 in
thirteen codec-free public workloads. The target remains open. The latest full
panel (`a06e339`) records five cells at parity, with ratios 0.58-3.35x.
[Benchmarks](BENCHMARKS.md) gives current figures and reproducible commands.
Keep changes reducing time or allocation; no minimum percentage applies.

Pushed `2f20ee6` passes [hosted CI run 38592938536](https://github.com/Lokad/qpdf/actions/runs/38592938536):
Windows 6m34s, Ubuntu 8m57s, reference 6m05s. Both ordinary platforms pass 17,910
checks without failures/skips, locked Release build/pack, inspection, fresh
consumer and rejection controls. Reference covers 1,858 exact object comparisons
and 442 rewrite trios. Pins, advisory exceptions and version remain unchanged.

## Current correction

Publication remapping borrows unchanged immutable references, arrays, dictionaries
and streams, rebuilding only changed paths. It still visits every child for map
validation, generation normalization, work/depth limits and cancellation. Changed
arrays allocate once. Dictionary ordering, CLR-null elision and QDF's explicit
Length replacement/reordering remain unchanged. Existing source/job ownership
keeps borrowed values alive through complete publication; no new cache is added.

The paired original linearization job allocates 19.225128 ->19.047407 MB
(177.7 KB less), with byte-identical independently valid output. Median time
25.284 ->28.550 ms has overlapping IQRs 24.327-27.969 /25.146-47.084, so no
speedup is claimed; candidate/native ratio 1.0099x remains above parity.

All 5,006 focused writer/linearization/split/publication checks pass (82s),
including five new finite regressions for borrowing, unmapped references,
generation/prefix handling, null elision and defensive copies. Six QDF cleanup
cases now require a real generation change, preserving actual-storage retirement
checks while removing an obsolete exact metadata-charge threshold. Final full
comparison, package/consumer, visual and hosted qualification are pending.

Earlier parser scratch, approximate ownership, immutable borrowing, source/cache
capacity reductions, staged content-ID hashing, direct classic xref ranges and
hint-list filtering remain integrated. All 128 published raw runs are preserved.
The latest qualified full panels contain
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
