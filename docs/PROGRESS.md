# Progress

Performance targets single-CPU parity (<=1.0x) with pinned native qpdf 12.4.2 in
thirteen codec-free public workloads. The target remains open. The latest full
panel (`a71a1ca`) records six cells at parity, with ratios 0.60-3.39x.
[Benchmarks](BENCHMARKS.md) gives current figures and reproducible commands.
Keep reduced time or allocation; no minimum percentage applies.

Pushed `2c6ee48` passes [hosted CI run 38598202274](https://github.com/Lokad/qpdf/actions/runs/38598202274):
Windows 6m25s, Ubuntu 4m14s, reference 6m06s. Both ordinary platforms pass 17,915
checks without failures/skips, locked Release build/pack, inspection, fresh
consumer and rejection controls. Reference covers 1,858 exact object comparisons
and 442 rewrite trios. Pins, advisory exceptions and version remain unchanged.

## Current correction

The graph copier reuses immutable references whose validated source identity
already equals their destination identity. Resolver validation still precedes
memo reuse; first-copy mapping, traversal and storage still occur. Cycles remain
copied and stale generations still become PDF null. Unchanged containers can
then use the existing copy-on-change path.

Single-source/grouped page Kids and form Fields/calculation-order arrays now pass
existing lists to the unchanged copying PdfArray constructor. This removes four
intermediate arrays while retaining its immutable defensive copy and item order.
No new API, cache or ownership mechanism is added.

All 5,410 focused writer/form/selection/split/merge/publication checks pass (71s),
including source-identity cycles, stale generations, limits, cancellation, cleanup
and surviving output values. The reference-reuse pair removes about 132 bytes
per original rewrite job with identical independently valid output. Median time
16.266 ->17.069 ms has overlapping IQRs; no speedup is claimed. A separate ancestry
leaf-skip trial showed no time/allocation improvement and was reverted; its raw
observations remain preserved. The array pair allocates 17.246767 ->17.243798 MB
per job (about 3 KB less), with identical independently valid bytes. Median time
18.094 ->17.151 ms has overlapping IQRs 16.838-18.762 /15.872-17.783; no speedup
is claimed. All 26 fresh managed/native outcomes and three negative controls
pass with unchanged inputs. Locked Release build/explicit pack/inspection/fresh
consumer/rejections pass (20s), preserving the 2 MiB multisource ceiling.
The final full panel retains 27,717 primary observations. Sparse full-input
selection remains 3.3898x; the separate file-host result is 0.3763x and replaces
no original case. Seven visual cases and two negative controls pass (18s) after
timing at unchanged tolerances. Hosted qualification is pending.

A paired check of the original 64-output split confirms about 1.7 KB less
allocation/job and byte-identical outputs. Medians 6.254 ->5.640 ms have slightly
overlapping IQRs 5.923-6.573 /5.536-5.952; no speedup is claimed. The larger
allocation difference between full panels cannot be attributed to this change.

Earlier parser scratch, approximate ownership, immutable borrowing, source/cache
capacity reductions, staged content-ID hashing, xref ranges, filtered hint lists
and publication remapping stay integrated. All earlier raw runs are preserved.
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
