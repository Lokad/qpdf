# Progress

Single-CPU parity (<=1.0x) against pinned qpdf 12.4.2 remains open. Clean source
`679a213` records 5/13 comparable warmed file-backed medians at parity;
supplementary sparse job JSON is 0.424x. Remaining cells: rewrite-M500 1.345x,
linearize-M500 1.341x, rewrite-X100 1.333x, merge-250x2 1.258x, rewrite-ID-M500
1.250x, object-streams-X100 1.108x, select-M500 1.044x, rewrite-objects 1.032x.
[Benchmarks](BENCHMARKS.md) leads with comparative figures and preserves ranges,
resource figures and commands in three tables. Separate panels establish no causal
attribution, and unchanged-source timing noise does not justify reruns.

Original `bf03d9a` memory-host results stay end-to-end evidence: fresh complete
input acquisition/registration, staging, public output export and file writes.
Native can read less from files. Sparse selection's original 3.4732x result stays
unchanged; a different input path cannot turn it into a pass. All 164 archived runs
preserve previous observations, including rejected/slower trials.

## Current implementation and verification

Prepared split serialization uses existing bounded batching for main, identifier and
pass1 captures. Synchronous owned storage allows scratch reuse, with explicit
flushes before lengths/IDs/publication. Geometric segments avoid intermediate
arrays, cap capacity at 32 KiB and expose only filled prefixes. Growth-overlap
admission, bounded work/cancellation and immutable retained output chunks remain.

Two same-driver split probes reduce cumulative managed allocation 34.940848 to
34.646845 MB/job, then 34.649050 to 30.964657 MB/job. All ten outputs match each
baseline and pass independent checks. Timing IQRs overlap; no speedup is claimed.
The second trial's worker RSS increases, so allocation savings establish neither
lower retained memory nor lower RSS. A copy-loop trial without a qualifying gain was
reverted; every observation remains. There is no new pool or ownership model.

All 3,124 affected writer/split/presentation/publication controls pass (46s), and
2,669 affected growth checks pass (82s). Final-source checks pass: 26 fresh semantic
outcomes plus three negatives; seven visual cases plus two negatives; locked Release
restore/build/explicit pack, inspection, fresh consumer and rejection controls. The
unchanged 2 MiB multisource consumer ceiling passes. WSL build/
semantics/file-panel/job phases take 32.26/84.08/245.67/18.95s; package/consumer
79.85s and visual 22.15s. All thirteen managed output sets match the preceding
panel; original inputs remain unchanged. The final panels retain all 18,208 file and
5,346 job observations. Required Windows/Ubuntu ordinary and pinned-reference
integration results are published by
[CI](https://github.com/Lokad/qpdf/actions/workflows/ci.yml).

Reader cancellation fixtures use the shared input-read observer and 16 KiB payloads,
preserving in-flight cancellation, scratch rollback and accepted-cache checks without
a scheduling race between parsing and a polling thread. All 90 affected reader/recovery
checks pass. Runtime code and the qualified benchmark implementation remain unchanged.

Argv/job single-page and one-source range selection retain bounded classic-xref
reads, catalog behavior and default bounded recovery. Missing ranges, diagnostics or
uncertain parsing retry once on the same stable lease before publication. Accepted
range sources close before output creation, preserving one-handle limits and
close-fault protection. Job `suppressRecovery:false` matches absent/default repair;
true remains strict. Five regressions demonstrated those prior failures; 6,254
affected public checks pass. Earlier useful corrections remain integrated.

## Preserved scope

The [supported profile](SUPPORTED-PROFILE.md) stays delivered. CMYK/YCCK remains
opaque, Zopfli is unavailable, and crypto including empty-password inputs is
excluded. Other [compatibility requirements](COMPATIBILITY.md) are not waived.

`MaxMemoryBytes` bounds approximate charged working storage. Significant capacity,
copy/growth overlap, allocation/decompression amplification, work, cancellation,
host boundaries, cleanup, integrity, immutability and surviving aliases remain
protected. Charges do not establish retained memory. Exact CLR sizes, individual
small-object/metadata-alias receipts, fixed GC rounds and precise bookkeeping
charges remain withdrawn; see [governance](SUPPORTED-PROFILE.md#governance).

No CPU parallelism, forced production GC, parsed-document cache, runtime native
bridge or new accounting/benchmark framework. Pins, five reviewed temporary
ImageSharp 3.1.12 exceptions and release version remain. No tags or publication.
