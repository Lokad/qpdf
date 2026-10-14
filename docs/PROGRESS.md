# Progress

Performance targets single-CPU parity (<=1.0x) with pinned native qpdf 12.4.2 in
thirteen codec-free public workloads. The target remains open. The latest full
panel (`d66d5de`) records five cells at parity, with ratios 0.58-3.56x.
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
checks while removing an obsolete exact metadata-charge threshold. All 26 fresh
managed/native outcomes and three negative controls pass with unchanged inputs.
Locked Release build/explicit pack/inspection/fresh consumer/rejections pass
(17s), preserving the 2 MiB multisource ceiling. Seven visual cases and two
negative controls pass (18s) after timing, at unchanged tolerances. Hosted full
tests are pending. Initial hosted run `38597042520` passes reference (4m), but
both ordinary platforms find one collision-work fixture that no longer builds a
dictionary. Require generation normalization to exercise actual collision work
under its unchanged 50,000-work ceiling; production code and timings are unchanged.
All 48 focused collision/remapping checks pass (11s) after this fixture correction.

Earlier parser scratch, approximate ownership, immutable borrowing, source/cache
capacity reductions, staged content-ID hashing, direct classic xref ranges and
hint-list filtering remain integrated. All earlier raw runs are preserved.
The latest full primary panel contains 25,803 samples. Sparse full-input selection
remains 3.5623x; the separate file-host cell is 0.3796x and replaces no primary case.
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
