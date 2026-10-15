# Progress

Single-CPU warmed parity (<=1.0x) against pinned qpdf 12.4.2 remains open.
Clean source `59536b5` records 10/13 comparable file-backed medians at parity;
sparse job JSON is 0.432x. Remaining cells: streamed-xref rewrite-X100 1.131x,
ordinary rewrite-M500 1.055x and linearize-M500 1.001x. The near-parity cell does
not justify an unchanged-source timing retry. [Benchmarks](BENCHMARKS.md) leads
with current figures, ranges, resource measurements and commands in three tables.
Separate panels establish no causal attribution.

Original `bf03d9a` memory-host results remain end-to-end evidence: fresh complete
acquisition/registration, staging, defensive public output export and file writes.
Native opens files and can read less. Original sparse 3.4732x stays unchanged;
a different input path cannot substitute a pass. All 185 archived runs preserve
prior observations, including failed, slower and insensitive trials.

## Current implementation

Small fixed-byte writes use existing governed batching scratch when they fit.
Synchronous capability/flush checks, immutable retaining/async packets and large
writes keep their existing behavior. All 189 affected checks and two strengthened
boundary/caller-byte-alias cases pass. Its original paired timing ranges overlap;
no measured gain is attributed to that structural removal.

Bounded hashes are cached only for the existing twelve shared syntax names, with
length-based matching and no per-name field. Governed name-object lookup keeps the
same work charges and raw-byte/collision behavior. All 625 affected checks pass.
Against the intermediate per-instance cache, streamed-xref rewrite falls
2.140 ->2.085 ms, with non-overlapping IQRs 2.118-2.196 /2.082-2.095, allocating
about 626 B/job less. Scalar-heavy rewrite records 24.244 ->22.934 ms with
overlapping ranges and about 81.3 KB/job less allocation. All 5,009 observations
and validated identical outputs remain. The intermediate allocation cost, its
slower scalar-heavy probe and both full panels remain archived.

Integer-lookahead replay passed 1,327 checks but added state/allocation without
established target-workload benefit; its implementation is restored. Its patch,
artifacts and 4,571 observations remain. Wider dictionary scratch was rejected
before implementation: all 100 original streamed-xref pages already fit four
entries. The existing six-second profile's 2,136 validated jobs remain diagnostic
evidence; sampled runtime polling is not proof of forced GC or retained memory.

## Verification

Final source passes 26 fresh semantic outcomes plus three negatives, seven visual
cases plus two negatives, locked Release restore/build/explicit pack, inspection,
fresh consumer and rejection controls. The unchanged 2 MiB multisource ceiling
passes. All thirteen output sets match the intermediate panel byte for byte;
original inputs and all 182 preceding archived run objects are unchanged.
All 28,074 file/5,483 job observations remain.

WSL build/semantics/file-panel/job phases take 24.84/29.27/216.11/18.07s;
package/consumer takes 31.06s, visual 17.57s. Hosted Windows/Ubuntu ordinary and
pinned-reference qualification, test logs/TRX and reference reports are available
through [CI](https://github.com/Lokad/qpdf/actions/workflows/ci.yml).

## Preserved scope

The [supported profile](SUPPORTED-PROFILE.md) remains delivered. CMYK/YCCK stays
opaque, Zopfli is unavailable, and crypto including empty-password inputs is
excluded. Other [compatibility requirements](COMPATIBILITY.md) are not waived.

Bounded argv/job reading, catalog/default recovery, stable leases/complete fallback,
input funding/early closure, immutable publication packets and multisource
independence remain. Approximate accounting protects significant capacity and
copy/growth overlap, allocation/decompression amplification, work/cancellation,
cleanup, integrity and surviving aliases. Charges are not retained heap or RSS.
Exact CLR sizes, per-small-object/metadata-alias receipts, prescribed GC rounds and
precise bookkeeping charges remain withdrawn; see
[governance](SUPPORTED-PROFILE.md#governance).

No CPU parallelism, forced production GC, parsed-document cache, runtime native
bridge or new accounting/benchmark framework. Pins, five reviewed temporary
ImageSharp 3.1.12 exceptions and release version remain. No tags or publication.
