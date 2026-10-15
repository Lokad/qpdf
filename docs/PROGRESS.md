# Progress

Single-CPU warmed parity (<=1.0x) against pinned qpdf 12.4.2 remains open.
Clean source `593f134` has 12/13 comparable file-backed medians at parity;
sparse job JSON is 0.4554x. The remaining cell is rewrite-X100 at
1.0245x (1.881/1.836 ms).
[Benchmarks](BENCHMARKS.md) leads with comparisons and resources in three tables.
Separate panels establish no causal attribution; paired probes replace no cell.

Original `bf03d9a` memory-host results remain end-to-end evidence: fresh complete
acquisition/registration, staging, defensive public export and file writes.
Native opens files and can read less. Original sparse 3.4732x stays unchanged;
a different path cannot substitute a pass. All earlier observations remain.

## Current implementation

Writer counting/emission read each concrete dictionary entry once; array and
dictionary emission have separate async routines. Sizing/filling borrow readonly
fields through existing synchronous loops/private calls, with no borrow across
an await or added storage. Ordering, null/Length handling, work/depth checks,
cancellation and disposal remain. The 46s paired X100 medians 2.038 ->1.978 ms
overlap, with unchanged allocation (537 KB/job); no firm speedup is claimed.

The warm driver records process-wide JIT counters after timing/allocation capture.
Warmup keeps thirty fresh jobs/two seconds minimum, then waits for two seconds
without compilation changes, with bounded time/request limits. Raw results retain
readiness and later compilation; earlier protocols/results stay intact. All fresh
acquisition, parsing, staging/export and file writes remain timed. No parsed cache,
forced production GC, CPU parallelism or new measurement framework is introduced.
Private parser helpers now borrow readonly tokens, and three copier loops read
one dictionary-entry pair per visit. No borrow crosses an await; grammar,
lookahead, repair/offsets, work and ownership remain. All 6,250 affected checks
pass in 69s. The 62s paired X100 medians 1.961 ->1.936 ms overlap, without a firm
speedup claim. Integration qualification follows; the complete panel above
remains the measured result.
Qualified ranged selection/catalog/recovery/leases/fallback, immutable graph/payload
reuse, coarse ownership, checked sizing and fixed canonical integer atoms remain.

## Verification

All 2,117 affected writer/inventory checks pass in 44s; six readiness tests pass.
Final source passes 26 fresh semantic outcomes/three negatives, seven visual cases/
two negatives, locked Release restore/build/explicit pack, inspection, fresh
consumer and rejection controls. The unchanged 2 MiB multisource ceiling passes.
Original fixtures and all thirteen output sets are unchanged. The archive retains
217 runs with all 214 preceding objects intact, including original memory evidence
and rejected/slower/insensitive/outlier trials. All 66,717 file/13,459 job
observations from this final comparison remain.

WSL build/semantics/file-panel/job phases take 25.38/27.42/401.49/38.55s;
package/consumer takes 26.45s, visual 18.20s. Current WSL kernel is
6.18.54.1; original memory-host evidence records 6.18.40.1. Previous batch
`858d4af` passes [hosted CI](https://github.com/Lokad/qpdf/actions/runs/38687938181):
Ubuntu 5m44s, Windows 6m54s, reference 5m38s; each platform passes 17,940 tests,
reference passes 1,858 exact parser comparisons/442 rewrite trios. Release pack,
inspection and fresh-consumer/rejection checks pass on both. One combined hosted
qualification of the parser/copier batch follows; no component CI or timing gates.

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
