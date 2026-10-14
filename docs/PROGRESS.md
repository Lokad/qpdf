# Progress

Single-CPU warmed parity (<=1.0x) against pinned qpdf 12.4.2 remains open.
Clean source `5854144` records 10/13 comparable file-backed medians at parity;
sparse job JSON is 0.425x. Remaining cells: streamed-xref rewrite-X100 1.093x,
ordinary rewrite-M500 1.069x and content-ID rewrite-ID-M500 1.037x.
[Benchmarks](BENCHMARKS.md) contains current figures, ranges, resource measurements
and commands in three tables. Separate panels establish no causal attribution;
unchanged-source noise does not justify favorable reruns.

Original `bf03d9a` memory-host results remain end-to-end evidence: fresh complete
acquisition/registration, staging, defensive public output export and file writes.
Native opens files and can read less. Original sparse 3.4732x stays unchanged;
a different input path cannot substitute a pass. All 176 archived runs preserve
prior observations, including failed, slower and insensitive trials.

## Current implementation

Scalar serialization, counted/shared token formatting and small writes return
existing operations directly. This avoids redundant async state machines while
preserving container recursion/order/depth, meaningful work and cancellation,
bounded large writes and encoded-buffer ownership through awaited completion.
Existing synchronous, retaining and async sink paths keep their publication and
immutability behavior; there is no new buffer pool or ownership system.

All 187 affected writer/public cancellation checks pass. The two paired streamed-
xref rewrite probes record 2.490 ->2.355 ms and 2.276 ->2.158 ms. Both distributions
overlap and allocate approximately 561 KB/job; no firm speedup or allocation gain
is claimed. All 4,104/4,483 observations and byte-identical validated outputs remain.
One test helper uses typed delegates to observe a direct private-method limit
exception without reflection wrapping; its original budget, before-write rejection
and cleanup assertions remain. The initial failed run remains recorded.

Bounded argv/job selection retains catalog behavior, default recovery, stable
leases and same-lease complete-read fallback. Single-source payload borrowing,
bounded immutable publication packets, multisource independence and validated
cache-capacity hints remain. Guard-disabled backing-retention regressions and
representative public success/failure/cancellation jobs keep their safeguards.

## Verification

Final-source qualification passes 26 fresh semantic outcomes plus three negatives,
seven visual cases plus two negatives, locked Release restore/build/explicit pack,
inspection, fresh consumer and rejection controls. The unchanged 2 MiB multisource
consumer ceiling passes. All thirteen output sets match the preceding panel byte
for byte; original inputs and all 172 prior archived run objects are unchanged.
All 27,237 file/5,724 job observations remain.

WSL build/semantics/file-panel/job phases take 17.70/27.49/215.44/18.69s;
package/consumer takes 22.35s, visual 16.72s. Hosted Windows/Ubuntu ordinary and
pinned-reference qualification, test logs/TRX and reference reports are available
through [CI](https://github.com/Lokad/qpdf/actions/workflows/ci.yml).

## Preserved scope

The [supported profile](SUPPORTED-PROFILE.md) remains delivered. CMYK/YCCK stays
opaque, Zopfli is unavailable, and crypto including empty-password inputs is
excluded. Other [compatibility requirements](COMPATIBILITY.md) are not waived.

Memory accounting remains approximate: significant capacity, growth/copy overlap,
allocation/decompression amplification, work/cancellation, cleanup, integrity and
surviving aliases stay protected. Charges do not establish retained heap or RSS.
Exact CLR sizes, per-small-object/metadata-alias receipts, prescribed GC rounds and
precise bookkeeping charges remain withdrawn; see
[governance](SUPPORTED-PROFILE.md#governance).

No CPU parallelism, forced production GC, parsed-document cache, runtime native
bridge or new accounting/benchmark framework. Pins, five reviewed temporary
ImageSharp 3.1.12 exceptions and release version remain. No tags or publication.
