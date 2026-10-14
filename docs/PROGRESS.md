# Progress

Single-CPU parity (<=1.0x) against pinned qpdf 12.4.2 remains open. Clean source
`30bd234` records 8/13 comparable warmed file-backed medians at parity;
supplementary sparse job JSON is 0.446x. Remaining cells: rewrite-X100 1.294x,
merge-250x2 1.207x, linearize-M500 1.018x, object-streams-X100 1.011x,
rewrite-objects 1.011x. [Benchmarks](BENCHMARKS.md) leads with current comparisons,
ranges, resource figures and reproducible commands in three tables. Separate panels
establish no causal attribution; unchanged-source timing noise does not justify
reruns.

Original `bf03d9a` memory-host results stay end-to-end evidence: fresh complete
input acquisition/registration, staging, public output export and file writes.
Native can read less from files. Original sparse 3.4732x stays unchanged; a new path
cannot turn it into a pass. All 168 archived runs preserve prior observations,
including slower/rejected trials and insensitive probes.

## Current implementation and verification

Private single-source copies borrow input payloads under existing document/job
funding through publication, including preopened whole-file sources. Accepted
sources still close before output. Public readers, multisource storage, bounded
ranges, default recovery/catalog behavior and same-lease complete fallback remain.
Classic/linearized/QDF writers isolate input-backed payloads into fresh immutable
packets of at most 32 KiB for retaining or async sinks. Synchronous output and
prepared captures consume them directly. No new reader API, pool or ownership
system.

The original 500-page file-host rewrite pair falls 22.031 ->16.980 ms, with
non-overlapping IQRs 21.226-28.871 /16.778-20.067; allocation falls 22.320429 ->
17.187246 MB/job. Independently validated output is byte-identical; all 524
observations remain. Native 15.787 ms gives 1.076x, so that pair stays above target.
Worker RSS rises slightly; no retained-memory/RSS reduction is claimed.

All 8,946 affected checks pass before the bounded-packet refinement; 219 final
focused checks pass, including seven complete public jobs with 96 KiB payload and 2
MiB omitted stream under 8 MiB memory/256 KiB output limits. Five deliberate
isolation-disabled jobs retain the large input backing; guarded jobs preserve output
aliases, prior targets on write failure/cancellation, and closed leases. These
observe actual backings, rather than equating charges with retained heap.

Filtered object-stream decoding also reuses existing ArrayData: full immutable
arrays borrow and slices materialize under scratch. All 2,951 affected checks pass.
The original streamed/incremental rewrite fixture has UNFILTERED object streams, so
its overlapping 2.380/2.384 ms pair is insensitive to that correction. No timing or
allocation improvement is attributed to it; all 4,303 observations remain.

Final-source qualification passes 26 fresh semantic outcomes plus three negatives;
seven visual cases plus two negatives; locked Release restore/build/explicit pack,
inspection, fresh consumer and rejection controls. The unchanged 2 MiB multisource
consumer ceiling passes. WSL build/semantics/file-panel/job phases take
20.98/28.78/225.62/17.97s; package/consumer 22.10s, visual 18.99s. All thirteen
managed output sets match the preceding panel, original inputs remain unchanged, and
all 23,750 file/4,965 job observations remain. Required Windows/Ubuntu ordinary and
pinned-reference integration results are published by
[CI](https://github.com/Lokad/qpdf/actions/workflows/ci.yml).

## Preserved scope

The [supported profile](SUPPORTED-PROFILE.md) stays delivered. CMYK/YCCK remains
opaque, Zopfli is unavailable, and crypto including empty-password inputs is
excluded. Other [compatibility requirements](COMPATIBILITY.md) are not waived.

`MaxMemoryBytes` bounds approximate charged working storage. Significant capacity,
copy/growth overlap, allocation/decompression amplification, work/cancellation, host
boundaries, cleanup, integrity, immutability and surviving aliases remain protected.
Charges do not establish retained heap. Exact CLR sizes, individual
small-object/metadata-alias receipts, fixed GC rounds and precise bookkeeping
charges remain withdrawn; see [governance](SUPPORTED-PROFILE.md#governance).

No CPU parallelism, forced production GC, parsed-document cache, runtime native
bridge or new accounting/benchmark framework. Pins, five reviewed temporary
ImageSharp 3.1.12 exceptions and release version remain. No tags or publication.
