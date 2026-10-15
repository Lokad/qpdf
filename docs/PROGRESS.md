# Progress

Single-CPU warmed parity (<=1.0x) against pinned qpdf 12.4.2 remains open.
Clean source `8cf8cfb` records 11/13 comparable file-backed medians at parity;
sparse job JSON is 0.432x. Remaining full-panel cells: streamed-xref rewrite-X100
1.150x and scalar-heavy rewrite-objects 1.131x. [Benchmarks](BENCHMARKS.md) contains
current figures, ranges, resource measurements and commands in three tables.
Separate panels establish no causal attribution; unchanged-source noise does not
justify favorable reruns.

Original `bf03d9a` memory-host results remain end-to-end evidence: fresh complete
acquisition/registration, staging, defensive public output export and file writes.
Native opens files and can read less. Original sparse 3.4732x stays unchanged;
a different input path cannot substitute a pass. All 182 archived runs preserve
prior observations, including failed, slower and insensitive trials.

## Current implementation and next correction

Small fixed-byte writes use existing governed batching scratch when they fit.
Synchronous capability/flush checks, immutable retaining/async packets and large
writes retain their existing behavior. All 189 affected checks and two strengthened
boundary/caller-byte-alias cases pass. The paired rewrite's 1.995/1.983 ms ranges
overlap; no speedup or allocation gain is established.

Immutable names cache their bounded hash, reused by governed name-object lookup
with the same work charges and raw-byte comparison. All 192 affected checks pass.
The streamed-xref pair falls 2.128 ->2.065 ms, with non-overlapping IQRs
2.085-2.311 /2.032-2.074; allocation rises about 493 B/job. A scalar-heavy concern
check records 24.268 ->24.799 ms with broadly overlapping ranges and about 81 KB
more allocation. Its paired 0.907x does not close the full-panel 1.131x cell.
The next correction moves cached hashes to the existing fixed shared-name vocabulary
so ordinary names keep their prior footprint; final batch qualification follows.

That correction now uses the twelve shared syntax names and length-based matching,
with no per-name hash field. All 625 affected checks pass. Against the per-instance
cache, streamed-xref rewrite falls 2.140 ->2.085 ms with non-overlapping IQRs
2.118-2.196 /2.082-2.095, allocating about 626 B/job less. Scalar-heavy rewrite
records 24.244 ->22.934 ms with overlapping IQRs and about 81.3 KB/job less
allocation. Its paired 0.856x does not replace the prior full-panel result.
All 5,009 observations and byte-identical validated outputs remain; final corrected
source qualification follows before the combined push.

Rejected integer-lookahead replay passed 1,327 checks but added state/allocation
without demonstrated target-workload benefit; its implementation is restored.
The patch, artifacts and all 4,571 observations remain. Wider dictionary scratch
was rejected before implementation: all 100 original streamed-xref pages already
fit four entries. One existing six-second profile collected 2,136 hash-validated
jobs; selected intervals are diagnostic, not native CPU ratios or retained-memory
measurements. No forced collection was added.

## Verification

Source `8cf8cfb` passes 26 fresh semantic outcomes plus three negatives, seven visual
cases plus two negatives, locked Release restore/build/explicit pack, inspection,
fresh consumer and rejection controls. The unchanged 2 MiB multisource consumer
ceiling passes. All thirteen output sets match the preceding panel byte for byte;
original inputs and all 176 preceding archived run objects are unchanged.
All 27,400 file/4,363 job observations remain.

WSL build/semantics/file-panel/job phases take 16.59/32.55/220.33/20.21s;
package/consumer takes 24.58s, visual 16.47s. Hosted Windows/Ubuntu ordinary and
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

No CPU parallelism, parsed-document cache, runtime native bridge or new accounting/
benchmark framework. Pins, five reviewed temporary ImageSharp 3.1.12 exceptions and
release version remain. No tags or publication.
