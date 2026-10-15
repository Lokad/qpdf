# Progress

Single-CPU warmed parity (<=1.0x) against pinned qpdf 12.4.2 remains open.
Clean source `1b0b9c4` has 11/13 comparable file-backed medians at parity;
sparse job JSON is 0.3930x. Remaining ordinary-rewrite cells are X100 at
1.0937x (1.930/1.765 ms) and M500 at 1.0679x (15.330/14.355 ms).
[Benchmarks](BENCHMARKS.md) leads with comparisons and resources in three tables.
Separate panels establish no causal attribution; paired probes replace no cell.

Original `bf03d9a` memory-host results remain end-to-end evidence: fresh complete
acquisition/registration, staging, defensive public export and file writes.
Native opens files and can read less. Original sparse 3.4732x stays unchanged;
a different path cannot substitute a pass. All earlier observations remain.

## Current implementation

Writer counting and emission read each concrete dictionary entry once. Separate
array/dictionary async routines avoid combined state; streams return the existing
operation directly. Ordering, null/Length handling, work/depth checks, cancellation
and disposal remain. The paired X100 medians overlap, with unchanged allocation;
these are straightforward structural removals, without a confirmed speedup claim.

Uninstrumented diagnostics found compilation continuing beyond the previous
two-second warmup. The existing driver now records process-wide JIT counters after
job timing/allocation capture. Warmup keeps thirty fresh jobs/two seconds minimum,
then waits for two seconds without compilation changes, with bounded time/request
limits. Raw results retain readiness and later compilation. All input acquisition,
parsing, staging/export and file writes remain timed. Earlier protocols/results
stay intact. The next correction targets measured costs in the two rewrite gaps
after compilation settles, using existing tools and narrow paired probes.

Formatter sizing/filling now borrow readonly fields through their existing
synchronous loops and private calls. No borrow crosses an await or adds storage.
All 2,117 affected checks pass in 44s. A 46s paired X100 probe observes 2.038 ->
1.978 ms with overlapping ranges and unchanged allocation (537 KB/job); this
structural removal carries no confirmed speedup claim. Its integration
qualification follows; the complete comparison above remains the measured result.

Ranged argv/job selection, catalog/recovery defaults, stable leases and complete-
read fallback remain qualified. Immutable graph/payload reuse, coarse private
ownership, unique dictionary-prefix transfer, concrete writer tables, checked
stateless sizing, fixed canonical integer atoms and geometric xref hints remain.
No parsed-document cache, forced production GC or CPU parallelism is introduced.

## Verification

All 2,117 affected writer/inventory checks pass in 42s; six readiness tests pass.
Final source passes 26 fresh semantic outcomes/three negatives, seven visual cases/
two negatives, locked Release restore/build/explicit pack, inspection, fresh
consumer and rejection controls. The unchanged 2 MiB multisource ceiling passes.
Original fixtures and all thirteen output sets are unchanged. The archive retains
214 runs, preserving all 204 preceding objects, original memory evidence and
rejected/slower/insensitive/outlier trials. All 58,890 file/10,460 job observations
from this final comparison remain.

WSL build/semantics/file-panel/job phases take 25.91/26.30/388.45/27.86s;
package/consumer takes 32.31s, visual 17.27s. Current WSL kernel is
6.18.54.1; original memory-host evidence records 6.18.40.1. Published batch
`a80e896` passes [hosted CI](https://github.com/Lokad/qpdf/actions/runs/38686113424):
Ubuntu 5m29s, Windows 6m15s, pinned reference 6m03s. Each ordinary platform passes
17,940 tests; reference passes 1,858 exact parser comparisons and 442 rewrite
trios. Release pack, inspection and fresh-consumer/rejection checks pass on both
platforms. No component CI or timing gates.

## Preserved scope

The [supported profile](SUPPORTED-PROFILE.md) stays delivered: CMYK/YCCK opaque,
Zopfli unavailable, crypto excluded. Other [compatibility](COMPATIBILITY.md)
requirements are not waived. Approximate accounting protects significant capacity/
growth overlap, amplification/decompression/work limits, cancellation, cleanup,
host boundaries, integrity, immutability and surviving aliases. Charges are not
retained heap/RSS. Exact CLR sizes, individual small-object/metadata-alias receipts,
GC rounds and bookkeeping-byte precision remain withdrawn; see
[governance](SUPPORTED-PROFILE.md#governance). Pins/version remain unchanged;
no native bridge or package publication is introduced.
