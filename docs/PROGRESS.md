# Progress

Performance targets single-CPU parity (<=1.0x) with pinned native qpdf 12.4.2 in
thirteen codec-free public workloads. The target remains open. The latest full
panel (`610191b`) records five cells at parity, with ratios 0.58-3.36x.
[Benchmarks](BENCHMARKS.md) gives current figures and reproducible commands.
Keep reduced time or allocation; no minimum percentage applies.

Pushed `6db2221` passes [hosted CI run 38602960010](https://github.com/Lokad/qpdf/actions/runs/38602960010):
Windows 6m29s, Ubuntu 4m42s, reference 5m16s. Both ordinary platforms pass 17,916
checks without failures/skips, locked Release build/pack, inspection, fresh
consumer and rejection controls. Reference covers 1,858 exact object comparisons
and 442 rewrite trios. Pins, advisory exceptions and version remain unchanged.

## Current correction

Multi-source coalescing followed by rotation could restore an earlier page
snapshot, leaving two content streams instead of the requested single stream.
Both argv and job-JSON regressions reproduced this defect before correction.
Rotation now reads current copier pages; inspection retains its existing snapshot
path. Other transformations share one compact occurrence vector, removing the
second vector and its stale dictionaries. Source/output ownership stays unchanged.

Both selection cores also skip rotation preparation when no rotations are
requested. Actual requests retain the existing folding/application logic.
The original streamed-input rewrite pair changes allocation 578.1 ->572.6 KB/job
and median time 2.111 ->2.063 ms, with identical independently valid bytes.
Timing IQRs 2.101-2.193 /2.036-2.080 are separated in this probe; ratio 1.1605x
remains above parity. These are complete warmed public jobs on the same driver.

All 5,634 focused selection/content/rotation/inspection/overlay/appearance/
flattening/publication checks pass (73s). Two new regressions check coalescing
survives rotation, independent page counts, decoded content and closed host leases.
The vector pair removes another 2.5 KB/job (572.6 ->570.1 KB), with identical
normal output. Medians 2.200 ->2.131 ms have overlapping IQRs; no speedup is claimed.
The existing four-second allocation sampler identified empty per-page rotation
lists in 1,293 fresh verified jobs. Approximate ticks are diagnostic leads, not
exact per-type allocation, retained memory or native timing measurements.
Locked Release build/explicit pack/inspection/fresh consumer/rejections pass (41s),
preserving the 2 MiB multisource ceiling. The valid interaction fixture passes
pinned native checking/content/rotation assertions for native and both managed
routes; the saved baseline still fails coalescing on its rotated page. The new
fixture initially omitted Resources; adding the required empty dictionary changes
only the fixture, with both new tests passing again. All 26 fresh managed/native
outcomes and three negative controls pass, with original inputs unchanged.
The final panel preserves 26,664 primary samples. Sparse full-input selection is
3.3560x; the additional file-host case is 0.3737x and replaces no original cell.
Seven visual cases and two negative controls pass (18s) after timing at unchanged
tolerances. Hosted qualification is pending. Earlier corrections and raw runs stay
preserved; separate full panels do not establish source-change attribution.

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
