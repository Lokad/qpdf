# Progress

Single-CPU warmed parity (<=1.0x) against pinned qpdf 12.4.2 remains open.
Clean source `f292c5e` records 6/13 comparable file-backed medians at parity;
supplementary sparse job JSON is 0.480x. Remaining cells: rewrite-X100 1.294x,
rewrite-M500 1.125x, rewrite-objects 1.039x, linearize-M500 1.036x,
merge-250x2 1.022x, rewrite-ID-M500 1.015x, object-streams-X100 1.008x.
[Benchmarks](BENCHMARKS.md) contains current comparisons, ranges, resource figures
and commands in three tables. Separate panels establish no causal attribution;
unchanged-source timing noise does not justify favorable reruns.

Original `bf03d9a` memory-host results remain end-to-end evidence: fresh complete
acquisition/registration, staging, defensive public output export and file writes.
Native opens files and can read less. Original sparse 3.4732x stays unchanged;
a different input path cannot substitute a pass. All 172 archived runs preserve
prior observations, including failed, slower and insensitive trials.

## Current implementation

Scalar serialization returns existing formatting operations directly rather than
creating an outer recursive async state machine. All 187 affected writer/public
cancellation checks pass. The paired streamed-xref rewrite records 2.490 ->2.355 ms
with overlapping IQRs 2.385-2.693 /2.252-2.624; allocation is approximately 561 KB
in both lanes. No firm speedup or allocation gain is claimed; all 4,104 observations
and validated byte-identical outputs remain. This local structural correction will
share a later integration checkpoint; the comparative tables still identify `f292c5e`.

Accepted object-stream groups use their validated member count to hint existing
cache capacity at first accepted insertion. Rejected/unmatched attempts and tight
budgets retain normal growth. The original streamed-xref rewrite pair reduces
allocation 569,216 ->561,010 B/job; 5.329/5.332 ms medians have broad overlapping
ranges, establishing no speedup. All 1,962 observations remain.

Object-stream acceptance reuses its validated header bitmap. Copied inherited
attributes start at the parent after the leaf was checked, preserving nulls and
parent/cycle handling. Its paired rewrite is slower: 2.954 ->3.379 ms (IQR
2.918-2.992 /2.924-3.475), approximately 561 KB allocated in both lanes. All 3,498
observations remain; no measured gain is attributed to these structural removals.
The next bounded investigation is scalar serialization overhead using existing
formatting and sink helpers, targeting the streamed-xref rewrite gap.

Existing bounded argv/job selection preserves catalog behavior, default recovery,
stable leases and same-lease complete-read fallback. Private single-source copies
borrow immutable payloads under document/job funding through publication; accepted
inputs still close before output. Public-reader, multisource and ranged-buffer
independence remain. Classic/linearized/QDF writers give retaining/async sinks
fresh immutable packets of at most 32 KiB. Guard-disabled tests expose actual
retained input backings; protected jobs preserve output aliases, prior targets on
failure/cancellation and closed leases. No new ownership system or buffer pool.

## Verification

All 213 cache/reader checks and 486 lookup/selection/default/resource/transform
checks pass. Final-source qualification passes 26 fresh semantic outcomes plus
three negatives, seven visual cases plus two negatives, locked Release
restore/build/explicit pack, inspection, fresh consumer and rejection controls.
The unchanged 2 MiB multisource consumer ceiling passes. All thirteen managed output
sets are byte-identical to the preceding panel; original inputs remain unchanged.
All 168 previous archived run objects are preserved unchanged.

WSL build/semantics/file-panel/job phases take 30.14/33.03/236.84/18.26s;
package/consumer takes 33.09s, visual 20.04s. Hosted Windows/Ubuntu ordinary and
pinned-reference qualification, test logs/TRX and reference reports are available
through [CI](https://github.com/Lokad/qpdf/actions/workflows/ci.yml).

## Preserved scope

The [supported profile](SUPPORTED-PROFILE.md) remains delivered. CMYK/YCCK stays
opaque, Zopfli is unavailable, and crypto including empty-password inputs is
excluded. Other [compatibility requirements](COMPATIBILITY.md) are not waived.

`MaxMemoryBytes` bounds approximate charged working storage, not retained heap or
RSS. Significant capacity, growth/copy overlap, allocation/decompression
amplification, work/cancellation, host boundaries, cleanup, integrity, immutability
and surviving aliases remain protected. Exact CLR sizes, individual
small-object/metadata-alias receipts, prescribed GC rounds and precise bookkeeping
charges remain withdrawn; see [governance](SUPPORTED-PROFILE.md#governance).

No CPU parallelism, forced production GC, parsed-document cache, runtime native
bridge or new accounting/benchmark framework. Pins, five reviewed temporary
ImageSharp 3.1.12 exceptions and release version remain. No tags or publication.
