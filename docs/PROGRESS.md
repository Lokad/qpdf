# Progress

Single-CPU warmed parity (<=1.0x) against pinned qpdf 12.4.2 remains open.
Clean source `f555a11`: 10/13 file-backed medians at parity; equivalent sparse
job JSON 0.3880x. Remaining cells: rewrite-M500 1.0113x, rewrite-X100 1.0708x,
and linearize-M500 1.0636x.
[Benchmarks](BENCHMARKS.md) leads with comparisons and resources in three tables.
Separate panels establish no causal attribution; paired probes replace no cell.

Original `bf03d9a` memory-host results remain end-to-end evidence: fresh complete
acquisition/registration, staging, defensive public export and file writes.
Native opens files and can read less. Original sparse 3.4732x stays unchanged;
a different path cannot substitute a pass. All earlier observations remain.

## Current implementation

Ten private parser helpers borrow readonly tokens; three copier loops read each
dictionary-entry pair once. Grammar, lookahead, repair/offsets, ordering/null
semantics, work and ownership remain, with no borrow across await or added storage.
All 6,250 affected syntax/reader/writer/inventory checks pass in 69s. The 62s paired
X100 medians 1.961 ->1.936 ms overlap, with unchanged allocation (537 KB/job);
these structural removals carry no confirmed speedup/allocation-benefit claim.
The insensitive work-charging JIT hint was restored (2.020 ->2.018 ms overlaps,
allocation unchanged); its trial is preserved. Rebuilt binaries match the qualified
source byte-for-byte, so its complete panel is retained without another rerun.

Bounded warmup keeps thirty fresh jobs/two seconds minimum, then waits for two
seconds without JIT counter changes. Counters follow job timing/allocation capture;
raw results retain readiness and later compilation. Earlier protocols/results
remain intact. Fresh acquisition, parsing, staging/export and file writes stay
timed. No parsed cache, forced production GC, CPU parallelism or new measurement
framework is introduced. Qualified ranged selection/catalog/recovery/leases/
fallback, immutable graph/payload reuse, coarse ownership, checked sizing, readonly
writer fields and fixed canonical integer atoms remain.

## Verification

Final source passes 26 fresh semantic outcomes/three negatives, seven visual cases/
two negatives, locked Release restore/build/explicit pack, inspection, fresh
consumer and rejection controls. The unchanged 2 MiB multisource ceiling passes.
Original inputs and all thirteen output sets are unchanged. The archive retains
221 runs with all 217 preceding objects intact, including original memory evidence
and rejected/slower/insensitive/outlier trials. All 54,842 file/10,819 job
observations from this final comparison remain.

WSL build/semantics/file-panel/job phases take 32.71/36.15/416.74/29.98s;
package/consumer takes 33.58s, visual 19.35s. Current WSL kernel is
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
